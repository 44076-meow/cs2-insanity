// =============================================================================
// AimLookflowProbe.cs — discriminant probe for the AimState design.
//
// Question (AimState design depends on this): when the C++ inline detour
// fires PRE-CCSBot::UpdateLookAngles, does m_lookPitch/Yaw already contain
// the engine BT's intended look target for THIS tick (so we can piggyback
// on engine target picking), or is it just a copy of m_angEyeAngles after
// the smoother (so we'd need POST-hook or our own target picker)?
//
// Methodology — sample, don't infer. On each tick, for one selected bot,
// capture m_lookPitch/Yaw (CCSBot) and m_angEyeAngles X/Y (CCSPlayerPawn,
// both via CSSharp wrapper and via raw schema offset). Also capture pawn
// origin (movement detector) and bot/pawn handle pointers (compare with
// [AimHook] fire# pointers to verify the slot has an active CCSBot AI —
// fake-client slots renamed by InsanityHider don't, and produce zeros).
//
// ──────────────────────────────────────────────────────────────────────────────
// ANSWER (2026-05-09, slot 1 / Aleksib, 2048-tick run during bot-vs-bot fight):
//
//   * Active ticks: 622 / 2048 (30%) had non-zero deltas — bot spent most
//     of round walking and tracking targets.
//   * Per-tick deltas, mean (over active ticks): dLook=(0.02°,0.30°),
//     dEye=(0.02°,0.29°). Eye consistently lagged look by ~0.04°.
//   * Per-tick deltas, max:               dLook=(5.16°,176.02°),
//                                         dEye=(5.29°,174.24°). Big flicks
//     are synchronous-ish but eye still trails look by ~2° even at 174°
//     yaw delta — i.e. smoother coefficient is NOT 1.0, eye genuinely
//     chases look as a first-order lerp.
//
// CONCLUSION: m_lookPitch/Yaw IS engine target (BT's "where I want to
// look this tick"); m_angEyeAngles IS smoother output ("where I'm looking
// now"). C# AimState can read m_lookPitch/Yaw to know engine target,
// then write m_angEyeAngles via the existing C++ AimHook PRE-detour to
// replace the smoother with our own (BotProfile-driven aim error,
// reaction delay, custom smoothing).
//
// IDLE GATE: dLook ≈ 0 for several consecutive ticks → BT is not actively
// pushing a new target. AimState should clear its override in that case
// and let the engine smoother run unmolested (otherwise idle bots would
// jitter on whatever stale (pitch,yaw) we last wrote).
//
// EDGE CASE seen on slot 0 (jks, idle 4 sec): lookP=6.91, eyeP=3.82 with
// 0 delta on all 256 ticks. Engine apparently froze look at one value
// while eye stuck at another — pitch deadband, sleeping BT, or paused
// smoother. Doesn't block AimState, but the override layer should not
// assume eye smoothly chases look indefinitely.
// ──────────────────────────────────────────────────────────────────────────────
//
// Workflow (kept for regression — re-run after engine updates that may
// reshuffle the smoother):
//   replica_probe_lookflow <slot> [ticks=256]   arm capture; dump after
//                                                <ticks> game ticks
//   replica_probe_lookflow stop                 abort early, dump partial
//
// Output is verbose but capped. Only ticks with at least one non-zero
// delta log (idle bot won't spam).
// =============================================================================

using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Memory;

namespace InsanityRevive;

public static class AimLookflowProbe
{
    private struct Sample
    {
        public int   Tick;
        public float LookP, LookY;   // CCSBot.m_lookPitch / m_lookYaw
        public float EyeP,  EyeY;    // CCSPlayerPawn.m_angEyeAngles X / Y (via wrapper)
        public float RawEyeP, RawEyeY; // m_angEyeAngles via raw schema offset
        public float Ox, Oy, Oz;     // pawn AbsOrigin (movement detector)
    }

    private static int       _slot      = -1;
    private static int       _remaining = 0;
    private static readonly List<Sample> _buf = new();

    // Cached schema offsets — looked up once on first Start.
    private static int _lookPOff = -1;
    private static int _lookYOff = -1;
    private static int _eyeOff   = -1;

    // Diagnostic anchors — captured once at start, logged in dump header.
    // Compare _botHandleAtStart with [AimHook] fire# pointers in the meta log:
    // if they don't match, we are reading a slot that has no active CCSBot AI
    // and the rest of the dump is meaningless.
    private static long _botHandleAtStart;
    private static long _pawnHandleAtStart;

    public static bool IsArmed => _slot >= 0 && _remaining > 0;

    public static void Start(int slot, int ticks)
    {
        if (_lookPOff < 0)
        {
            _lookPOff = Schema.GetSchemaOffset("CCSBot", "m_lookPitch");
            _lookYOff = Schema.GetSchemaOffset("CCSBot", "m_lookYaw");
            _eyeOff   = Schema.GetSchemaOffset("CCSPlayerPawn", "m_angEyeAngles");
            Log.Info($"AimLookflowProbe schema offsets cached: lookP={_lookPOff} lookY={_lookYOff} eye={_eyeOff}");
        }
        _slot      = slot;
        _remaining = ticks;
        _botHandleAtStart  = 0;
        _pawnHandleAtStart = 0;
        _buf.Clear();
        _buf.Capacity = Math.Max(_buf.Capacity, ticks);
        Log.Info($"AimLookflowProbe armed slot={slot} ticks={ticks} (engage the bot, dump fires automatically)");
    }

    public static void StopEarly()
    {
        if (!IsArmed) { Log.Info("AimLookflowProbe: not armed"); return; }
        Stop("manual stop");
    }

    public static unsafe void OnTick()
    {
        if (!IsArmed) return;

        var ctrl = Utilities.GetPlayerFromSlot(_slot);
        if (ctrl == null || !ctrl.IsValid) { Stop("controller gone"); return; }
        var pawn = ctrl.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return; // mid-respawn — skip this tick, don't abort

        var s = new Sample { Tick = Server.TickCount };

        try
        {
            var eye = pawn.EyeAngles;
            s.EyeP = eye.X;
            s.EyeY = eye.Y;
        }
        catch { s.EyeP = float.NaN; s.EyeY = float.NaN; }

        // Raw eye via schema offset — bypasses CSSharp's pawn.EyeAngles
        // wrapper to discriminate "wrapper caches the value" from "field
        // genuinely doesn't change". If wrapper-eye stays put while raw-eye
        // moves, that's the wrapper caching.
        s.RawEyeP = float.NaN;
        s.RawEyeY = float.NaN;
        try
        {
            if (_eyeOff > 0)
            {
                float* p = (float*)((byte*)pawn.Handle.ToPointer() + _eyeOff);
                s.RawEyeP = p[0];
                s.RawEyeY = p[1];
            }
        }
        catch { /* keep NaN */ }

        // Origin (X/Y/Z) — moves iff bot is walking. Distinguishes
        // "BT genuinely passive" from "engine BT not even running for this slot".
        try
        {
            if (pawn.AbsOrigin != null)
            {
                s.Ox = pawn.AbsOrigin.X;
                s.Oy = pawn.AbsOrigin.Y;
                s.Oz = pawn.AbsOrigin.Z;
            }
        }
        catch { /* leave 0 */ }

        s.LookP = float.NaN;
        s.LookY = float.NaN;
        try
        {
            var bot = pawn.Bot;
            if (bot != null && bot.Handle != IntPtr.Zero && _lookPOff > 0 && _lookYOff > 0)
            {
                if (_botHandleAtStart == 0)
                {
                    _botHandleAtStart  = bot.Handle.ToInt64();
                    _pawnHandleAtStart = pawn.Handle.ToInt64();
                }
                s.LookP = *(float*)((byte*)bot.Handle.ToPointer() + _lookPOff);
                s.LookY = *(float*)((byte*)bot.Handle.ToPointer() + _lookYOff);
            }
        }
        catch { /* keep NaN */ }

        _buf.Add(s);
        _remaining--;
        if (_remaining == 0) Stop("budget exhausted");
    }

    private static void Stop(string reason)
    {
        Log.Info($"AimLookflowProbe stopping ({reason}); samples={_buf.Count}");
        DumpBuffer();
        _slot      = -1;
        _remaining = 0;
        _buf.Clear();
    }

    private static void DumpBuffer()
    {
        if (_buf.Count == 0) { Log.Info("AimLookflowProbe: empty buffer"); return; }

        Log.Info($"AimLookflowProbe DUMP slot={_slot} samples={_buf.Count} " +
                 $"botHandle=0x{_botHandleAtStart:X} pawnHandle=0x{_pawnHandleAtStart:X} " +
                 $"(compare botHandle with [AimHook] fire# pointers — if they don't match, " +
                 $"this slot has no active CCSBot AI and the dump is meaningless)");
        var first = _buf[0];
        Log.Info($"  t={first.Tick} lookP={first.LookP:F2} lookY={first.LookY:F2} " +
                 $"eyeP={first.EyeP:F2} eyeY={first.EyeY:F2} " +
                 $"rawEyeP={first.RawEyeP:F2} rawEyeY={first.RawEyeY:F2} " +
                 $"O=({first.Ox:F0},{first.Oy:F0},{first.Oz:F0})");

        var prev = first;
        int skipped = 0;
        for (int i = 1; i < _buf.Count; i++)
        {
            var s = _buf[i];
            float dLP = Diff(s.LookP, prev.LookP);
            float dLY = AngleDelta(s.LookY, prev.LookY);
            float dEP = Diff(s.EyeP, prev.EyeP);
            float dEY = AngleDelta(s.EyeY, prev.EyeY);
            float dRP = Diff(s.RawEyeP, prev.RawEyeP);
            float dRY = AngleDelta(s.RawEyeY, prev.RawEyeY);
            float dOx = s.Ox - prev.Ox;
            float dOy = s.Oy - prev.Oy;
            float dOz = s.Oz - prev.Oz;
            const float thresh = 0.01f;
            bool angMoved = MathF.Abs(dLP) > thresh || MathF.Abs(dLY) > thresh
                         || MathF.Abs(dEP) > thresh || MathF.Abs(dEY) > thresh
                         || MathF.Abs(dRP) > thresh || MathF.Abs(dRY) > thresh;
            bool posMoved = MathF.Abs(dOx) > 0.5f || MathF.Abs(dOy) > 0.5f || MathF.Abs(dOz) > 0.5f;
            if (!angMoved && !posMoved) { skipped++; prev = s; continue; }

            Log.Info($"  t={s.Tick} lookP={s.LookP:F2} lookY={s.LookY:F2} " +
                     $"eyeP={s.EyeP:F2} eyeY={s.EyeY:F2} " +
                     $"rawEyeP={s.RawEyeP:F2} rawEyeY={s.RawEyeY:F2} " +
                     $"O=({s.Ox:F0},{s.Oy:F0},{s.Oz:F0}) " +
                     $"dL=({dLP:+0.00;-0.00;0.00},{dLY:+0.00;-0.00;0.00}) " +
                     $"dE=({dEP:+0.00;-0.00;0.00},{dEY:+0.00;-0.00;0.00}) " +
                     $"dR=({dRP:+0.00;-0.00;0.00},{dRY:+0.00;-0.00;0.00}) " +
                     $"dO=({dOx:+0.0;-0.0;0},{dOy:+0.0;-0.0;0},{dOz:+0.0;-0.0;0})");
            prev = s;
        }
        if (skipped > 0) Log.Info($"  (skipped {skipped} idle ticks where all deltas below threshold)");
    }

    private static float Diff(float a, float b)
    {
        if (float.IsNaN(a) || float.IsNaN(b)) return 0f;
        return a - b;
    }

    private static float AngleDelta(float a, float b)
    {
        if (float.IsNaN(a) || float.IsNaN(b)) return 0f;
        float d = a - b;
        while (d >  180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }
}
