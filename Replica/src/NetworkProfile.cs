using System;
using System.Collections.Generic;

namespace Replica;

/// <summary>
/// Connection archetype — what kind of "internet" the bot pretends to
/// have. Distribution leans realistic-MM: mostly cable/wifi, rare
/// extremes. Spec from 2026-05-08 user task.
/// </summary>
public enum ConnectionType
{
    CableStable,    // 15% — fiber/ethernet, almost no jitter
    CableNormal,    // 20% — typical home cable, mild jitter
    WifiGood,       // 22% — good wifi, occasional small spikes
    WifiMid,        // 18% — medium wifi, regular jitter
    WifiBad,        //  8% — bad wifi, wild jitter
    Mobile5G,       //  4% — 5G hotspot
    Mobile4G,       //  3% — 4G hotspot, drifty
    RegionFar,      //  5% — different country, stable high
    RegionVeryFar,  //  2% — very far region (CN/SA/AU)
    SchoolNet,      //  1% — school/work network, chaos
    Vpn,            //  2% — VPN routed
}

/// <summary>
/// Static parameters per connection type. All ms values are observed
/// real-world MM ranges. Weights sum to 100.
/// </summary>
internal readonly struct ConnectionParams
{
    public readonly int Weight;
    public readonly int BaselineMin, BaselineMax;
    public readonly int JitterRange;            // ±this on each tick
    public readonly double MicroSpikePerSec;    // micro events/sec (+5-15ms for ~30ms)
    public readonly double LightSpikePerMin;    // light events/min (+10-150ms for 0.5-2s)
    public readonly int LightSpikePeakMin, LightSpikePeakMax;
    public readonly int LightSpikeDurMinMs, LightSpikeDurMaxMs;
    public readonly double BaseLossRate;

    public ConnectionParams(int weight, int blMin, int blMax, int jitter,
                            double microPerSec, double spikePerMin,
                            int spikePeakMin, int spikePeakMax,
                            int spikeDurMinMs, int spikeDurMaxMs,
                            double loss)
    {
        Weight = weight;
        BaselineMin = blMin; BaselineMax = blMax;
        JitterRange = jitter;
        MicroSpikePerSec = microPerSec;
        LightSpikePerMin = spikePerMin;
        LightSpikePeakMin = spikePeakMin; LightSpikePeakMax = spikePeakMax;
        LightSpikeDurMinMs = spikeDurMinMs; LightSpikeDurMaxMs = spikeDurMaxMs;
        BaseLossRate = loss;
    }
}

/// <summary>
/// Immutable per-bot network DNA. Generated once at spawn from the bot's
/// fake SteamID64 — same identity always produces same network feel,
/// so behaviour is reproducible across sessions.
///
/// Fields preserved for backwards compat with telemetry and status
/// output (BaseLatencyMs, JitterRangeMs, LossRate). New fields hold
/// the layered-spike parameters introduced 2026-05-08.
/// </summary>
public sealed class NetworkProfile
{
    public ConnectionType Type { get; init; }
    public int    BaseLatencyMs           { get; init; }
    public int    JitterRangeMs           { get; init; }
    public double MicroSpikeChancePerSec  { get; init; }
    public double LightSpikeChancePerSec  { get; init; }  // /sec, derived from /min
    public int    LightSpikeDurationMinMs { get; init; }
    public int    LightSpikeDurationMaxMs { get; init; }
    public int    LightSpikePeakMinMs     { get; init; }
    public int    LightSpikePeakMaxMs     { get; init; }
    public double LossRate                { get; init; }
    public ulong  Seed                    { get; init; }

    // Backwards-compat aliases for code that references the old single-
    // tier spike fields (FakeClientManager telemetry, mostly).
    public double SpikeChancePerSec   => LightSpikeChancePerSec;
    public int    SpikeDurationMinMs  => LightSpikeDurationMinMs;
    public int    SpikeDurationMaxMs  => LightSpikeDurationMaxMs;
    public int    SpikePeakMinMs      => LightSpikePeakMinMs;
    public int    SpikePeakMaxMs      => LightSpikePeakMaxMs;

    private static readonly Dictionary<ConnectionType, ConnectionParams> _table = new()
    {
        // type            wt blMin blMax jit microPerSec spikePerMin peakMin peakMax durMin durMax  loss
        [ConnectionType.CableStable]   = new(15,   8,  25,   2, 0.05,  0.0,    0,   0,    0,    0, 0.0001),
        [ConnectionType.CableNormal]   = new(20,  20,  45,   3, 0.10,  0.5,   10,  25,  300,  600, 0.0005),
        [ConnectionType.WifiGood]      = new(22,  25,  55,   4, 0.20,  1.0,   15,  35,  400,  800, 0.0010),
        [ConnectionType.WifiMid]       = new(18,  35,  75,   6, 0.40,  3.0,   25,  60,  500, 1200, 0.0030),
        [ConnectionType.WifiBad]       = new( 8,  50, 100,  10, 0.80,  6.0,   40, 100,  600, 1500, 0.0080),
        [ConnectionType.Mobile5G]      = new( 4,  30,  70,   5, 0.30,  2.0,   20,  50,  500, 1000, 0.0020),
        [ConnectionType.Mobile4G]      = new( 3,  60, 110,   8, 0.60,  5.0,   30,  80,  600, 1200, 0.0050),
        [ConnectionType.RegionFar]     = new( 5,  80, 140,   3, 0.10,  1.0,   15,  40,  400,  800, 0.0010),
        [ConnectionType.RegionVeryFar] = new( 2, 150, 250,   5, 0.15,  1.0,   20,  50,  500, 1000, 0.0020),
        [ConnectionType.SchoolNet]     = new( 1,  40, 180,  20, 1.20, 10.0,   60, 150,  800, 2000, 0.0150),
        [ConnectionType.Vpn]           = new( 2,  60, 120,   4, 0.15,  2.0,   25,  60,  500, 1000, 0.0020),
    };

    /// <summary>
    /// Deterministic profile selection from seed. Two bots with the
    /// same seed always get the same connection type + baseline.
    /// Seeds derive from persona SteamId64 → identity → network feel
    /// is stable across sessions.
    ///
    /// This is the "unbiased" entry point — BotProfile.Generate calls
    /// <see cref="GenerateForType"/> directly with a hardware-correlated
    /// type choice instead.
    /// </summary>
    public static NetworkProfile Generate(ulong seed)
    {
        unchecked
        {
            // Hash seed into 64-bit stream, peel ranges off it.
            ulong h = Mix(seed);

            // Pick connection type by weighted random (use low 16 bits).
            int totalWeight = 0;
            foreach (var p in _table.Values) totalWeight += p.Weight;
            int roll = (int)((h & 0xFFFF) % (ulong)totalWeight);
            ConnectionType type = ConnectionType.CableNormal;
            int acc = 0;
            foreach (var (k, v) in _table)
            {
                acc += v.Weight;
                if (roll < acc) { type = k; break; }
            }
            return GenerateForType(seed, type);
        }
    }

    /// <summary>
    /// Build a profile with an explicit connection type. Used by
    /// BotProfile.Generate so it can apply hardware-correlated bias
    /// (potato → wifi/4G, enthusiast → cable) without the unbiased
    /// weighted roll inside <see cref="Generate"/>.
    /// </summary>
    public static NetworkProfile GenerateForType(ulong seed, ConnectionType type)
    {
        unchecked
        {
            ulong h = Mix(seed ^ ((ulong)type * 0x100000001B3UL));
            var pp = _table[type];

            int blRoll = (int)((h >> 16) & 0xFFFF);
            int span   = pp.BaselineMax - pp.BaselineMin + 1;
            int baseline = pp.BaselineMin + blRoll % span;

            return new NetworkProfile
            {
                Type                    = type,
                Seed                    = seed,
                BaseLatencyMs           = baseline,
                JitterRangeMs           = pp.JitterRange,
                MicroSpikeChancePerSec  = pp.MicroSpikePerSec,
                LightSpikeChancePerSec  = pp.LightSpikePerMin / 60.0,
                LightSpikeDurationMinMs = pp.LightSpikeDurMinMs,
                LightSpikeDurationMaxMs = pp.LightSpikeDurMaxMs,
                LightSpikePeakMinMs     = pp.LightSpikePeakMin,
                LightSpikePeakMaxMs     = pp.LightSpikePeakMax,
                LossRate                = pp.BaseLossRate,
            };
        }
    }

    private static ulong Mix(ulong seed)
    {
        unchecked
        {
            ulong h = seed * 0x9E3779B97F4A7C15UL ^ 0xDEADBEEFCAFEBABEUL;
            h = (h ^ (h >> 33)) * 0xFF51AFD7ED558CCDUL;
            h = (h ^ (h >> 33)) * 0xC4CEB9FE1A85EC53UL;
            h ^= (h >> 33);
            return h;
        }
    }

    public override string ToString() =>
        $"{Type} base={BaseLatencyMs}ms jit=±{JitterRangeMs} " +
        $"micro={MicroSpikeChancePerSec * 60:F1}/min " +
        $"spike={LightSpikeChancePerSec * 60:F1}/min " +
        $"({LightSpikePeakMinMs}–{LightSpikePeakMaxMs}ms peak, " +
        $"{LightSpikeDurationMinMs}–{LightSpikeDurationMaxMs}ms dur) " +
        $"loss={LossRate * 100:F2}%";
}
