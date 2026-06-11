using System;

namespace InsanityRevive;

/// <summary>
/// Per-bot network state machine. Driven once per server tick.
/// Owns its own RNG (xorshift64*, seeded distinctly from Profile so spike
/// timing is independent of profile-shape choices).
///
/// Three layers, all combined into the published latency:
///   Layer 1 — baseline + jitter (±JitterRangeMs) every tick.
///   Layer 2 — microspikes: short +5–15ms bumps for ~30ms (2 ticks).
///             Independent of Layer 3, fires from MicroSpikeChancePerSec.
///   Layer 3 — light spikes: longer +N–Nms bumps for 0.5–2s, with a
///             cooldown after each. Fires from LightSpikeChancePerSec.
///
/// Anti-flatline: if 30 consecutive seconds pass without any latency
/// variation, we force a microspike. Real connections never sit on a
/// single number for that long; statistical detectors flag it.
///
/// Loss: independent Bernoulli per tick from Profile.LossRate.
///
/// DEFERRED (second pass, see chat.md spec 2026-05-08):
///   - Catastrophic events (route drop, burst loss, major lag, disconnect).
///   - Context triggers (round events, after-death, clutch).
///   - Loss/choke correlation with current spike state.
/// </summary>
public sealed class NetworkSimulator
{
    private const double TickHz = 64.0;
    private const double MsPerTick = 1000.0 / TickHz;

    /// <summary>30 sec @ 64 Hz — flatline detector horizon.</summary>
    private const int FlatlineForceTicks = 30 * 64;

    /// <summary>Microspike duration in ticks (~2 ticks ≈ 31ms).</summary>
    private const int MicroSpikeTicks = 2;

    /// <summary>Microspike peak min/max additive bump in ms.</summary>
    private const int MicroSpikePeakMinMs = 5;
    private const int MicroSpikePeakMaxMs = 15;

    public NetworkProfile Profile { get; }
    public int  CurrentLatencyMs { get; private set; }
    public bool LossThisTick     { get; private set; }

    // --- Layer 3: light spike state ----------------------------------
    private enum SpikeState { Idle, Spiking, Cooldown }
    private SpikeState _state;
    private int _spikeTicksRemaining;
    private int _cooldownTicksRemaining;
    private int _spikePeakMs;
    private int _spikeDurationMs;

    public bool InSpike => _state == SpikeState.Spiking;
    public int  ActiveSpikePeakMs => _state == SpikeState.Spiking ? _spikePeakMs : 0;

    // event payload for telemetry; cleared each tick
    public bool SpikeStartedThisTick { get; private set; }
    public bool SpikeEndedThisTick   { get; private set; }

    // --- Layer 2: microspike (background) ----------------------------
    private int _microSpikeTicksRemaining;
    private int _microSpikePeakMs;

    public bool InMicroSpike => _microSpikeTicksRemaining > 0;
    public int  ActiveMicroSpikePeakMs =>
        _microSpikeTicksRemaining > 0 ? _microSpikePeakMs : 0;

    // --- Anti-flatline ----------------------------------------------
    private int _tick;
    private int _lastVariationTick;
    private int _prevLatency;
    public int  TicksSinceVariation => _tick - _lastVariationTick;

    private ulong _rng;

    public NetworkSimulator(NetworkProfile p, ulong rngSeed)
    {
        Profile = p;
        _rng = rngSeed == 0 ? 0xA5A5A5A5A5A5A5A5UL : rngSeed;
        CurrentLatencyMs = p.BaseLatencyMs;
        _prevLatency = p.BaseLatencyMs;
        _state = SpikeState.Idle;
    }

    public int GetCurrentLatencyTicks()
    {
        var ms = CurrentLatencyMs;
        if (ms < 0) ms = 0;
        return (int)Math.Round(ms / MsPerTick);
    }

    public bool ShouldDropThisTick() => LossThisTick;

    public void Tick()
    {
        _tick++;
        SpikeStartedThisTick = false;
        SpikeEndedThisTick = false;

        // Loss: independent Bernoulli per tick.
        LossThisTick = NextDouble() < Profile.LossRate;

        TickLightSpike();
        TickMicroSpike();
        ComposeLatency();
        EnforceAntiFlatline();
    }

    private void TickLightSpike()
    {
        switch (_state)
        {
            case SpikeState.Idle:
            {
                var perTick = Profile.LightSpikeChancePerSec / TickHz;
                if (NextDouble() < perTick)
                {
                    _spikeDurationMs = NextInt(Profile.LightSpikeDurationMinMs,
                                                Profile.LightSpikeDurationMaxMs);
                    _spikePeakMs     = NextInt(Profile.LightSpikePeakMinMs,
                                                Profile.LightSpikePeakMaxMs);
                    _spikeTicksRemaining = (int)Math.Round(_spikeDurationMs / MsPerTick);
                    if (_spikeTicksRemaining < 1) _spikeTicksRemaining = 1;
                    _state = SpikeState.Spiking;
                    SpikeStartedThisTick = true;
                }
                break;
            }
            case SpikeState.Spiking:
            {
                _spikeTicksRemaining--;
                if (_spikeTicksRemaining <= 0)
                {
                    _state = SpikeState.Cooldown;
                    // Cooldown ≈ 1× spike duration — avoids back-to-back spikes
                    // which would read as periodic lag, not real network noise.
                    _cooldownTicksRemaining = (int)Math.Round(_spikeDurationMs / MsPerTick);
                    SpikeEndedThisTick = true;
                }
                break;
            }
            case SpikeState.Cooldown:
            {
                _cooldownTicksRemaining--;
                if (_cooldownTicksRemaining <= 0) _state = SpikeState.Idle;
                break;
            }
        }
    }

    private void TickMicroSpike()
    {
        if (_microSpikeTicksRemaining > 0)
        {
            _microSpikeTicksRemaining--;
            return;
        }
        var perTick = Profile.MicroSpikeChancePerSec / TickHz;
        if (NextDouble() < perTick)
        {
            _microSpikeTicksRemaining = MicroSpikeTicks;
            _microSpikePeakMs = NextInt(MicroSpikePeakMinMs, MicroSpikePeakMaxMs);
        }
    }

    private void ComposeLatency()
    {
        // Base + jitter + microspike + light spike.
        var jitter = NextInt(-Profile.JitterRangeMs, Profile.JitterRangeMs);
        var lat = Profile.BaseLatencyMs + jitter;
        if (_microSpikeTicksRemaining > 0) lat += _microSpikePeakMs;
        if (_state == SpikeState.Spiking)  lat += _spikePeakMs;
        // Anti-detect: 5ms floor (impossible IRL to be lower).
        if (lat < 5)   lat = 5;
        if (lat > 999) lat = 999;
        CurrentLatencyMs = lat;
    }

    private void EnforceAntiFlatline()
    {
        if (Math.Abs(CurrentLatencyMs - _prevLatency) > 0)
        {
            _lastVariationTick = _tick;
        }
        else if (_tick - _lastVariationTick > FlatlineForceTicks
                 && _microSpikeTicksRemaining == 0
                 && _state == SpikeState.Idle)
        {
            // 30s without a single-ms variation — force a microspike so
            // statistical detectors don't see a perfectly flat line.
            _microSpikeTicksRemaining = MicroSpikeTicks;
            _microSpikePeakMs = NextInt(MicroSpikePeakMinMs, MicroSpikePeakMaxMs);
            _lastVariationTick = _tick;
        }
        _prevLatency = CurrentLatencyMs;
    }

    public int LastSpikeDurationMs => _spikeDurationMs;
    public int LastSpikePeakMs     => _spikePeakMs;

    /// <summary>One-line state dump — for `replica_net_debug` rcon.</summary>
    public string DebugStateString()
    {
        return $"latency={CurrentLatencyMs}ms loss={(LossThisTick ? "YES" : "no")} " +
               $"light={_state}(rem={_spikeTicksRemaining}t cd={_cooldownTicksRemaining}t " +
               $"peak={_spikePeakMs} dur={_spikeDurationMs}ms) " +
               $"micro=(rem={_microSpikeTicksRemaining}t peak={_microSpikePeakMs}) " +
               $"flatlineSince={TicksSinceVariation}t";
    }

    private ulong NextU64()
    {
        // xorshift64*
        ulong x = _rng;
        x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
        _rng = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    private double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));

    private int NextInt(int lo, int hi)
    {
        if (hi <= lo) return lo;
        var range = (uint)(hi - lo + 1);
        return lo + (int)(NextU64() % range);
    }
}
