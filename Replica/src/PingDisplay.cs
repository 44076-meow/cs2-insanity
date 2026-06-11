using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Replica;

// Rolling 64-tick (1s @ 64-tick) average of CurrentLatencyMs from the
// simulator. We write to m_iPing every 32 ticks (twice per second) —
// scoreboard already smooths so sub-half-second updates would just spam
// SetStateChanged for nothing visible.
public sealed class PingDisplay
{
    private const int WindowTicks    = 64;
    private const int WriteEveryTicks = 32;

    private readonly int[] _samples = new int[WindowTicks];
    private int _idx;
    private int _sum;
    private int _filled;
    private int _ticksSinceWrite;
    public int LastWrittenPing { get; private set; }

    public void RecordSample(int latencyMs)
    {
        var clamped = latencyMs < 0 ? 0 : latencyMs > 999 ? 999 : latencyMs;
        var oldest = _samples[_idx];
        _samples[_idx] = clamped;
        _sum += clamped - oldest;
        _idx = (_idx + 1) % WindowTicks;
        if (_filled < WindowTicks) _filled++;
    }

    public int CurrentAverage()
    {
        if (_filled == 0) return 0;
        return _sum / _filled;
    }

    public bool MaybeWrite(CCSPlayerController? controller)
    {
        _ticksSinceWrite++;
        if (_ticksSinceWrite < WriteEveryTicks) return false;
        _ticksSinceWrite = 0;

        if (controller == null || !controller.IsValid) return false;
        if (_filled == 0) return false;

        var avg = CurrentAverage();
        if (avg == LastWrittenPing) return false;

        try
        {
            controller.Ping = (uint)avg;
            SchemaSafety.MarkChanged(controller, "CCSPlayerController", "m_iPing", 0);
            // m_flSmoothedPing exists in schema but is server-side only
            // (CSSharp warns "not networked"), so writing it has no
            // visible effect — left out to avoid spamming the log.
            LastWrittenPing = avg;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"PingDisplay write failed: {ex.Message}");
            return false;
        }
    }

    public void Reset()
    {
        Array.Clear(_samples, 0, _samples.Length);
        _idx = _sum = _filled = _ticksSinceWrite = 0;
        LastWrittenPing = 0;
    }
}
