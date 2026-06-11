// =============================================================================
// AimProbe.cs — STEP 0 of the Aim Module v1 spec.
// =============================================================================
//
// The aim spec ("AimState/phase machine/parametric models") rests on ONE
// unproven assumption: the plugin can override a bot's view angles per tick
// and have the override stick (i.e. survive the engine's bot-AI pass).
//
// History against this assumption:
//   * SchemaSafety.cs incident log: writing m_angEyeAngles via
//     Schema.SetSchemaValue<QAngle> + SetStateChanged crashed the server
//     ("not networked" warning, next-tick crash). Field is in the deny-list.
//   * The same incident log suggests the next thing to try is
//     Schema.SetSchemaValue<QAngle>(pawn, "CCSPlayerPawn", "v_angle", angle)
//     WITHOUT SetStateChanged — UNVERIFIED.
//   * Older replica plugin (rm'd 2026-05-01) hit a separate wall:
//     even when its plugin-side AimController writes "succeeded", the engine
//     BT subtree (bt_attack.kv3 inside the VPK) re-wrote eye angles next
//     frame, producing 5500°/s flicks vs the plugin's 1000°/s clamp. Plugin
//     writes lost the race against engine writes.
//
// This probe answers the question ahead of any architecture work:
//   1. Does the chosen write path crash, succeed silently, or actually move
//      the bot's view in-game?
//   2. Does the value persist tick-over-tick when we DON'T re-write, or
//      does the engine BT clobber it?
//   3. Does Listeners.OnTick (our hook point) run AFTER the engine's bot AI,
//      so per-tick re-writes win the race?
//
// Methods exposed:
//   "vangle"   — Schema.SetSchemaValue<RawAngle> on "CCSPlayerPawn.v_angle"
//                without SetStateChanged. The recommended next-thing-to-try
//                from the SchemaSafety log.
//   "eye"      — Schema.SetSchemaValue<RawAngle> on "CCSPlayerPawn.m_angEyeAngles"
//                without SetStateChanged. Bypasses the deny-list by going
//                around SchemaSafety. Only enable if "vangle" gives no
//                visible movement.
//   "teleport" — pawn.Teleport(currentOrigin, qAngle, currentVelocity).
//                Heavyweight (full physics teleport) but well-trodden in
//                RevealController. Useful as a "definitely networked"
//                control case.
//
// Workflow:
//   replica_probe_aim_pin <slot> <pitch> <yaw> [seconds] [method]
//     -> pin one bot's view to (pitch,yaw); each tick the chosen method
//        writes that value, then we read EyeAngles back and log what the
//        engine reports. After [seconds] (default 5) the pin clears.
//   replica_probe_aim_persist <slot> <pitch> <yaw> [method]
//     -> like pin but writes ONCE then stops; logs the read-back every tick
//        for ~3 seconds so we can see whether the engine BT clobbered it.
//   replica_probe_aim_unpin [slot|all]
//     -> manual cancel.
//   replica_probe_aim_status
//     -> dump active pins.
//
// Probes are SAFE in production builds — gated by @css/cheats and clamp
// pitch to [-89,89]. Removing the file once the question is answered is
// fine; production aim code will not depend on this state.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace Replica;

public static class AimProbe
{
    /// <summary>3-float layout matching Source 2's QAngle. Used as the
    /// unmanaged generic argument to Schema.SetSchemaValue&lt;T&gt;.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RawAngle
    {
        public float Pitch;   // X — up/down,  +/- 89
        public float Yaw;     // Y — left/right
        public float Roll;    // Z — usually 0
    }

    public enum Method
    {
        VAngle,         // Schema.GetSchemaOffset("CCSPlayerPawn", "v_angle") — field unknown (returns 0).
        EyeAngles,      // raw write to CCSPlayerPawn.m_angEyeAngles. STICKS server-side, but bot AI ignores it for shoot trace.
        Teleport,       // pawn.Teleport(origin, qAngle, velocity) — rotates the WHOLE body (incl. up-vector); bot becomes prone.
        Stash,          // raw write to CCSPlayerPawn.m_angStashedShootAngles. Untested.
        Look,           // raw write to CCSBot.m_lookPitch (X) + m_lookYaw (Y). Per strings dump these are bot AI's COMMANDED look direction
                        // — the BT writes these before driving aim. Most promising.
        EyeFree,        // (1) write false to CCSBot.m_bEyeAnglesUnderPathFinderControl ONCE (releases BT control of eye angles).
                        // (2) write CCSPlayerPawn.m_angEyeAngles per tick. Tests advisor's hypothesis: m_angEyeAngles only "STICKS" because
                        // BT's path-finder owns it; flip flag → our writes drive aim.
    }

    private struct Pin
    {
        public int    Slot;
        public float  Pitch;
        public float  Yaw;
        public Method Method;
        /// <summary>Ticks remaining; pin removed when it hits 0.</summary>
        public int    TicksLeft;
        /// <summary>If true, write only on first tick; otherwise write every
        /// tick. "Persist" mode = one-shot write, observe whether engine BT
        /// overwrites.</summary>
        public bool   OneShot;
        public bool   FirstWriteDone;
        /// <summary>Last EyeAngles read back from the pawn (for diff).</summary>
        public float  LastObservedPitch;
        public float  LastObservedYaw;
        /// <summary>Stripe ticks for periodic console logs (every ~16 ticks).</summary>
        public int    LogStripe;
    }

    private static readonly Dictionary<int, Pin> _pins = new();
    private const int DefaultDurationSec = 5;

    public static string PinSlot(int slot, float pitch, float yaw,
                                  int durationSec, Method method)
    {
        var c = Utilities.GetPlayerFromSlot(slot);
        if (c == null || !c.IsValid) return $"slot {slot}: no controller";
        var pawn = c.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return $"slot {slot} ({c.PlayerName}): no pawn";

        pitch = Math.Clamp(pitch, -89f, 89f);
        yaw   = NormalizeYaw(yaw);

        _pins[slot] = new Pin {
            Slot      = slot,
            Pitch     = pitch,
            Yaw       = yaw,
            Method    = method,
            TicksLeft = Math.Max(1, durationSec) * 64,
            OneShot   = false,
        };
        return $"slot {slot} ({c.PlayerName}): pinned pitch={pitch:F1} yaw={yaw:F1} " +
               $"method={method} for {durationSec}s — read EyeAngles via replica_status";
    }

    public static string PersistSlot(int slot, float pitch, float yaw, Method method)
    {
        var c = Utilities.GetPlayerFromSlot(slot);
        if (c == null || !c.IsValid) return $"slot {slot}: no controller";
        var pawn = c.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return $"slot {slot} ({c.PlayerName}): no pawn";

        pitch = Math.Clamp(pitch, -89f, 89f);
        yaw   = NormalizeYaw(yaw);

        _pins[slot] = new Pin {
            Slot      = slot,
            Pitch     = pitch,
            Yaw       = yaw,
            Method    = method,
            TicksLeft = 3 * 64,   // observe for 3 seconds
            OneShot   = true,
        };
        return $"slot {slot} ({c.PlayerName}): one-shot write pitch={pitch:F1} yaw={yaw:F1} " +
               $"method={method} — will log EyeAngles drift for 3s";
    }

    public static string Unpin(int slot)
    {
        if (slot < 0)
        {
            int n = _pins.Count;
            _pins.Clear();
            return $"cleared {n} aim pins";
        }
        if (_pins.Remove(slot)) return $"slot {slot}: unpinned";
        return $"slot {slot}: no active pin";
    }

    public static string Status()
    {
        if (_pins.Count == 0) return "no active aim pins";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"active aim pins: {_pins.Count}");
        foreach (var (slot, p) in _pins)
        {
            sb.AppendLine($"  slot={slot} target=({p.Pitch:F1},{p.Yaw:F1}) " +
                          $"observed=({p.LastObservedPitch:F1},{p.LastObservedYaw:F1}) " +
                          $"method={p.Method} ticksLeft={p.TicksLeft} oneShot={p.OneShot}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Per-tick driver. Hooked from <see cref="ReplicaPlugin"/> via
    /// Listeners.OnTick. Cheap (only iterates the active-pin map). Detaches
    /// pins when their TicksLeft expires.
    /// </summary>
    public static void OnTick()
    {
        if (_pins.Count == 0) return;
        var slots = new List<int>(_pins.Keys);
        foreach (var slot in slots)
        {
            var p = _pins[slot];
            try
            {
                StepPin(ref p);
            }
            catch (Exception ex)
            {
                Log.Error($"AimProbe.OnTick slot={slot}: {ex.GetType().Name}: {ex.Message}");
                _pins.Remove(slot);
                continue;
            }

            p.TicksLeft--;
            if (p.TicksLeft <= 0)
            {
                _pins.Remove(slot);
                Log.Info($"AimProbe slot={slot} pin expired (target=({p.Pitch:F1},{p.Yaw:F1}) " +
                         $"final-observed=({p.LastObservedPitch:F1},{p.LastObservedYaw:F1}))");
            }
            else
            {
                _pins[slot] = p;
            }
        }
    }

    private static void StepPin(ref Pin p)
    {
        var c = Utilities.GetPlayerFromSlot(p.Slot);
        if (c == null || !c.IsValid) return;
        var pawn = c.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;
        if (pawn.LifeState != 0) return;  // dead — skip

        bool shouldWrite = !p.OneShot || !p.FirstWriteDone;
        bool wrote = false;
        string writeNote = "";
        if (shouldWrite)
        {
            switch (p.Method)
            {
                case Method.VAngle:
                    wrote = WriteRawAngle(pawn.Handle, "CCSPlayerPawn", "v_angle",
                                          p.Pitch, p.Yaw, out writeNote);
                    break;
                case Method.EyeAngles:
                    // Bypass SchemaSafety deny-list — this is the whole point
                    // of the probe. If the field still crashes, that's a
                    // hard data point against this method.
                    wrote = WriteRawAngle(pawn.Handle, "CCSPlayerPawn", "m_angEyeAngles",
                                          p.Pitch, p.Yaw, out writeNote);
                    break;
                case Method.Teleport:
                    try
                    {
                        var origin = pawn.AbsOrigin ?? new Vector(0, 0, 0);
                        var velocity = pawn.AbsVelocity ?? new Vector(0, 0, 0);
                        var ang = new QAngle(p.Pitch, p.Yaw, 0);
                        pawn.Teleport(origin, ang, velocity);
                        wrote = true;
                        writeNote = "Teleport ok";
                    }
                    catch (Exception ex)
                    {
                        writeNote = $"Teleport threw: {ex.GetType().Name}: {ex.Message}";
                    }
                    break;
                case Method.Stash:
                    wrote = WriteRawAngle(pawn.Handle, "CCSPlayerPawn", "m_angStashedShootAngles",
                                          p.Pitch, p.Yaw, out writeNote);
                    break;
                case Method.Look:
                    // CCSBot.m_lookPitch + m_lookYaw — these are floats on the
                    // CCSBot struct (the AI controller, separate entity from
                    // the pawn). Reach via pawn.Bot if exposed; else write
                    // through pawn schema offset (works only if these fields
                    // are inlined on the pawn — unlikely but cheap to try).
                    wrote = WriteLookPitchYaw(pawn, p.Pitch, p.Yaw, out writeNote);
                    break;
                case Method.EyeFree:
                    // First tick: release BT control. Every tick: write eye angles.
                    if (!p.FirstWriteDone)
                    {
                        var flagNote = WriteEyeFreeFlag(pawn);
                        Log.Info($"AimProbe slot={p.Slot} EyeFree flag-write: {flagNote}");
                    }
                    wrote = WriteRawAngle(pawn.Handle, "CCSPlayerPawn", "m_angEyeAngles",
                                          p.Pitch, p.Yaw, out writeNote);
                    break;
            }
            p.FirstWriteDone = true;
        }

        // Read back the engine's view of the pawn's eye angles. EyeAngles
        // is a getter on CCSPlayerPawn that returns the QAngle the engine
        // currently considers canonical for view direction.
        try
        {
            var eye = pawn.EyeAngles;
            p.LastObservedPitch = eye.X;
            p.LastObservedYaw   = eye.Y;
        }
        catch
        {
            p.LastObservedPitch = float.NaN;
            p.LastObservedYaw   = float.NaN;
        }

        // Periodic log: every 16 ticks (~4Hz). Avoids 64Hz console spam
        // while still letting us see whether the BT clobbers our value.
        p.LogStripe++;
        if (p.LogStripe >= 16)
        {
            p.LogStripe = 0;
            float dPitch = Math.Abs(p.LastObservedPitch - p.Pitch);
            float dYaw   = AngleDelta(p.LastObservedYaw, p.Yaw);
            string verdict = (dPitch < 0.5f && Math.Abs(dYaw) < 0.5f)
                ? "STICKS"
                : "DRIFTED";
            Log.Info($"AimProbe slot={p.Slot} method={p.Method} {verdict} " +
                     $"target=({p.Pitch:F1},{p.Yaw:F1}) observed=" +
                     $"({p.LastObservedPitch:F1},{p.LastObservedYaw:F1}) " +
                     $"d=({dPitch:F1},{dYaw:F1}) wrote={wrote} {writeNote}");
        }
    }

    private static unsafe bool WriteRawAngle(IntPtr handle, string schemaClass,
                                              string fieldName,
                                              float pitch, float yaw,
                                              out string note)
    {
        // CSSharp's Schema.SetSchemaValue<T> resolves T against schema
        // metadata — it rejects user-defined structs (first attempt with
        // a local 3-float struct produced "Error retrieving data type for
        // type Replica.AimProbe+RawAngle"). Bypass: look up the
        // field's byte offset, then memcpy 3 floats directly into the
        // native object. No SetStateChanged — that's the whole point of
        // the probe (deny-list crash trigger was specifically the
        // SetStateChanged for this family of fields).
        try
        {
            int offset = Schema.GetSchemaOffset(schemaClass, fieldName);
            if (offset <= 0)
            {
                note = $"Schema.GetSchemaOffset({schemaClass}.{fieldName}) returned {offset} — field unknown?";
                return false;
            }
            float* p = (float*)((byte*)handle.ToPointer() + offset);
            p[0] = pitch;
            p[1] = yaw;
            p[2] = 0f;
            note = $"raw write @ offset 0x{offset:X} of {schemaClass}.{fieldName} (no SetStateChanged)";
            return true;
        }
        catch (Exception ex)
        {
            note = $"raw write {schemaClass}.{fieldName} threw: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Write CCSBot.m_lookPitch (float) + CCSBot.m_lookYaw (float) — the
    /// bot AI's commanded look direction. Reaches CCSBot via pawn.Bot
    /// (CSSharp wrapper) if available; offset lookup is on "CCSBot" class.
    /// Writes raw float at each offset, no SetStateChanged.
    /// </summary>
    private static unsafe bool WriteLookPitchYaw(CCSPlayerPawn pawn, float pitch, float yaw, out string note)
    {
        try
        {
            // Try CSSharp's typed Bot accessor first. If it exists and
            // returns a valid handle, use that.
            IntPtr botHandle = IntPtr.Zero;
            try
            {
                var bot = pawn.Bot;
                if (bot != null && bot.Handle != IntPtr.Zero) botHandle = bot.Handle;
            }
            catch (Exception ex)
            {
                note = $"pawn.Bot threw: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            if (botHandle == IntPtr.Zero)
            {
                note = "pawn.Bot was null/0 — bot AI not attached?";
                return false;
            }

            int pitchOff = Schema.GetSchemaOffset("CCSBot", "m_lookPitch");
            int yawOff   = Schema.GetSchemaOffset("CCSBot", "m_lookYaw");
            if (pitchOff <= 0 || yawOff <= 0)
            {
                note = $"GetSchemaOffset CCSBot.m_lookPitch={pitchOff} m_lookYaw={yawOff} — field unknown?";
                return false;
            }
            float* pp = (float*)((byte*)botHandle.ToPointer() + pitchOff);
            float* py = (float*)((byte*)botHandle.ToPointer() + yawOff);
            *pp = pitch;
            *py = yaw;
            note = $"raw look write @ pitch=0x{pitchOff:X} yaw=0x{yawOff:X} on CCSBot (no SetStateChanged)";
            return true;
        }
        catch (Exception ex)
        {
            note = $"WriteLookPitchYaw threw: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// One-shot: write false to CCSBot.m_bEyeAnglesUnderPathFinderControl.
    /// If this flag gates whether the BT owns the bot's eye angles, flipping
    /// it should let our subsequent m_angEyeAngles writes actually drive aim.
    /// Returns a note describing the result.
    /// </summary>
    private static unsafe string WriteEyeFreeFlag(CCSPlayerPawn pawn)
    {
        try
        {
            IntPtr botHandle = IntPtr.Zero;
            try
            {
                var bot = pawn.Bot;
                if (bot != null && bot.Handle != IntPtr.Zero) botHandle = bot.Handle;
            }
            catch (Exception ex)
            {
                return $"pawn.Bot threw: {ex.GetType().Name}: {ex.Message}";
            }
            if (botHandle == IntPtr.Zero) return "pawn.Bot null/0";
            int off = Schema.GetSchemaOffset("CCSBot", "m_bEyeAnglesUnderPathFinderControl");
            if (off <= 0) return $"GetSchemaOffset returned {off}";
            byte* p = (byte*)((byte*)botHandle.ToPointer() + off);
            byte prev = *p;
            *p = 0;
            return $"set CCSBot.m_bEyeAnglesUnderPathFinderControl @ 0x{off:X} (was={prev}, now=0)";
        }
        catch (Exception ex)
        {
            return $"WriteEyeFreeFlag threw: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static float NormalizeYaw(float yaw)
    {
        while (yaw >  180f) yaw -= 360f;
        while (yaw < -180f) yaw += 360f;
        return yaw;
    }

    private static float AngleDelta(float a, float b)
    {
        float d = a - b;
        while (d >  180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    public static Method ParseMethod(string s)
    {
        return (s ?? "").Trim().ToLowerInvariant() switch
        {
            "eye" or "eyeangles" or "m_angeyeangles" => Method.EyeAngles,
            "teleport" or "tp"                        => Method.Teleport,
            "stash" or "stashed" or "shoot"           => Method.Stash,
            "look" or "lookpitch" or "lookyaw"        => Method.Look,
            "eyefree" or "free" or "pathfinder"       => Method.EyeFree,
            _                                          => Method.VAngle,
        };
    }
}
