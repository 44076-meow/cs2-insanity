// =============================================================================
// AimDiag.cs — diagnostic probe answering "where does a bot's bullet ACTUALLY
// go, and which schema field (if any) drives that direction?"
//
// The five fields we've patched server-side or via the C++ inline hook
// (m_angEyeAngles, m_angStashedShootAngles, m_lookPitch, m_lookYaw, the
// PathFinderControl flag) are all REPORT/SNAPSHOT — visible from outside but
// not the source of the shoot trace. Before deciding what to hook next, we
// need to discriminate:
//   (A) the shoot direction lives in some angle field we haven't found
//   (B) the shoot direction is fabricated at fire time inside a usercmd
//       and never persists in any field — meaning the only override path
//       is the cmd builder / RunPlayerMove etc.
//
// Methodology:
//   * On EventWeaponFire (any bot), capture the shooter's eye position and
//     all known angle fields RIGHT AT FIRE TIME.
//   * On EventBulletImpact for the same shooter (same tick or next),
//     compute direction = (impact_pos - eye_pos).Normalize().
//   * Convert that direction back into pitch/yaw degrees.
//   * Log a line: actual_dir vs each captured field, with delta.
//
// If actual_dir matches m_angEyeAngles within ~1° → m_angEyeAngles drives
// shoot trace; our problem is we couldn't WRITE it persistently mid-tick.
//
// If actual_dir matches m_angStashedShootAngles → that's the field, hook target.
//
// If actual_dir matches NEITHER (and not m_lookPitch/Yaw) → answer is (B):
// shoot direction is synthesized in usercmd, no field carries it.
//
// Output lands in server.log with [Replica][INFO] AimDiag prefix. Toggle
// via replica_aim_diag rcon (default off — events fire too often otherwise).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace InsanityRevive;

public static class AimDiag
{
    public static bool Enabled { get; private set; } = false;
    /// <summary>How many fire-events to log before auto-disabling, to keep logs readable.</summary>
    private const int LogBudget = 30;
    private static int _logsRemaining;

    // Per-shooter capture from EventWeaponFire, consumed by next EventBulletImpact.
    private struct FireSnapshot
    {
        public int    Tick;
        public Vector EyePos;
        public float  EyePitch, EyeYaw;            // m_angEyeAngles snapshot
        public float  StashedPitch, StashedYaw;    // m_angStashedShootAngles snapshot
        public float  LookPitch, LookYaw;          // CCSBot.m_lookPitch / m_lookYaw via offset
    }
    private static readonly Dictionary<uint, FireSnapshot> _pending = new();

    public static void SetEnabled(bool on, int logBudget = LogBudget)
    {
        Enabled = on;
        _logsRemaining = on ? logBudget : 0;
        Log.Info($"AimDiag enabled={on} budget={_logsRemaining}");
    }

    public static int LogsRemaining => _logsRemaining;

    /// <summary>EventWeaponFire handler. Called from InsanityRevivePlugin.</summary>
    public static unsafe void OnWeaponFire(CCSPlayerController? shooter)
    {
        if (!Enabled || _logsRemaining <= 0) return;
        if (shooter == null || !shooter.IsValid) return;
        var pawn = shooter.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;

        var snap = new FireSnapshot {
            Tick   = Server.TickCount,
            EyePos = pawn.AbsOrigin == null ? new Vector(0,0,0) : new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + 64f /* approx eye height */),
        };
        try
        {
            var eye = pawn.EyeAngles;
            snap.EyePitch = eye.X;
            snap.EyeYaw   = eye.Y;
        } catch { snap.EyePitch = float.NaN; snap.EyeYaw = float.NaN; }

        // m_angStashedShootAngles via raw offset (Schema.GetSchemaOffset works
        // for non-deny-listed fields). Layout = QAngle = 3 floats.
        try
        {
            int off = Schema.GetSchemaOffset("CCSPlayerPawn", "m_angStashedShootAngles");
            if (off > 0)
            {
                float* p = (float*)((byte*)pawn.Handle.ToPointer() + off);
                snap.StashedPitch = p[0];
                snap.StashedYaw   = p[1];
            }
            else { snap.StashedPitch = float.NaN; snap.StashedYaw = float.NaN; }
        } catch { snap.StashedPitch = float.NaN; snap.StashedYaw = float.NaN; }

        // CCSBot.m_lookPitch/Yaw via pawn.Bot wrapper if available.
        snap.LookPitch = float.NaN;
        snap.LookYaw   = float.NaN;
        try
        {
            var bot = pawn.Bot;
            if (bot != null && bot.Handle != IntPtr.Zero)
            {
                int pOff = Schema.GetSchemaOffset("CCSBot", "m_lookPitch");
                int yOff = Schema.GetSchemaOffset("CCSBot", "m_lookYaw");
                if (pOff > 0 && yOff > 0)
                {
                    snap.LookPitch = *(float*)((byte*)bot.Handle.ToPointer() + pOff);
                    snap.LookYaw   = *(float*)((byte*)bot.Handle.ToPointer() + yOff);
                }
            }
        } catch { /* leave NaN */ }

        _pending[shooter.Index] = snap;
    }

    /// <summary>EventBulletImpact handler. Called from InsanityRevivePlugin.</summary>
    public static void OnBulletImpact(CCSPlayerController? shooter, float x, float y, float z)
    {
        if (!Enabled || _logsRemaining <= 0) return;
        if (shooter == null || !shooter.IsValid) return;
        if (!_pending.TryGetValue(shooter.Index, out var snap)) return;
        // Only handle this if impact happened within ~1 tick of the fire.
        if (Server.TickCount - snap.Tick > 2) return;

        // Compute actual direction from eye pos to impact.
        float dx = x - snap.EyePos.X;
        float dy = y - snap.EyePos.Y;
        float dz = z - snap.EyePos.Z;
        float len = MathF.Sqrt(dx*dx + dy*dy + dz*dz);
        if (len < 1f) return;
        // Convert to (pitch, yaw): yaw = atan2(dy, dx) in degrees,
        //                          pitch = -asin(dz / len) in degrees.
        // (Source engine convention: pitch positive = looking down.)
        float actualYaw   = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        float actualPitch = -MathF.Asin(dz / len) * 180f / MathF.PI;

        // Deltas (with yaw wrap).
        float dEyeYaw   = AngleDelta(actualYaw, snap.EyeYaw);
        float dEyePitch = actualPitch - snap.EyePitch;
        float dStashYaw = AngleDelta(actualYaw, snap.StashedYaw);
        float dStashP   = actualPitch - snap.StashedPitch;
        float dLookYaw  = AngleDelta(actualYaw, snap.LookYaw);
        float dLookP    = actualPitch - snap.LookPitch;

        Log.Info($"AimDiag fire@t={snap.Tick}/imp@t={Server.TickCount} shooter='{shooter.PlayerName}' " +
                 $"actual=(p={actualPitch:F1},y={actualYaw:F1}) " +
                 $"eye=(p={snap.EyePitch:F1},y={snap.EyeYaw:F1}, dp={dEyePitch:+F1;-F1;0},dy={dEyeYaw:+F1;-F1;0}) " +
                 $"stash=(p={snap.StashedPitch:F1},y={snap.StashedYaw:F1}, dp={dStashP:+F1;-F1;0},dy={dStashYaw:+F1;-F1;0}) " +
                 $"look=(p={snap.LookPitch:F1},y={snap.LookYaw:F1}, dp={dLookP:+F1;-F1;0},dy={dLookYaw:+F1;-F1;0})");

        _pending.Remove(shooter.Index);
        _logsRemaining--;
        if (_logsRemaining == 0)
        {
            Enabled = false;
            Log.Info("AimDiag log budget exhausted, auto-disabled");
        }
    }

    private static float AngleDelta(float a, float b)
    {
        float d = a - b;
        while (d >  180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }
}
