using System;
using System.Collections.Generic;

namespace Replica;

// Opaque payload type. Behaviour layers (later) push these in; the detour
// drains ready ones. Base layer never produces real cmds — the buffer is
// here so the simulator and detour have something concrete to talk about.
public readonly struct BufferedCmd
{
    public readonly int  PushTick;
    public readonly long Generation;
    public readonly int  Buttons;
    public readonly float Pitch;
    public readonly float Yaw;
    public readonly float ForwardMove;
    public readonly float SideMove;
    public readonly float UpMove;

    public BufferedCmd(int pushTick, long generation, int buttons,
        float pitch, float yaw, float fwd, float side, float up)
    {
        PushTick = pushTick; Generation = generation; Buttons = buttons;
        Pitch = pitch; Yaw = yaw;
        ForwardMove = fwd; SideMove = side; UpMove = up;
    }
}

public sealed class InputBuffer
{
    private readonly Queue<(BufferedCmd Cmd, int TargetTick)> _q = new();
    private const int OverflowLimit = 256; // bots produce ~64 cmd/s; this is 4s of slack

    public int Pending => _q.Count;
    public int LastDropReasonOverflow { get; private set; }
    public int LastDropReasonLoss     { get; private set; }

    public void Enqueue(BufferedCmd cmd, int currentTick, int latencyTicks)
    {
        if (_q.Count >= OverflowLimit)
        {
            _q.Dequeue();
            LastDropReasonOverflow++;
        }
        _q.Enqueue((cmd, currentTick + Math.Max(0, latencyTicks)));
    }

    // Drop the oldest pending cmd. Used when NetworkSimulator says
    // "loss this tick" — physically what happens on a real client when
    // a UDP packet vanishes mid-flight.
    public void DropOldest()
    {
        if (_q.Count == 0) return;
        _q.Dequeue();
        LastDropReasonLoss++;
    }

    // Returns (and removes) every cmd whose targetTick <= currentTick.
    public IEnumerable<BufferedCmd> DrainReady(int currentTick)
    {
        while (_q.Count > 0)
        {
            var head = _q.Peek();
            if (head.TargetTick > currentTick) yield break;
            _q.Dequeue();
            yield return head.Cmd;
        }
    }

    public void Clear()
    {
        _q.Clear();
        LastDropReasonOverflow = 0;
        LastDropReasonLoss = 0;
    }
}
