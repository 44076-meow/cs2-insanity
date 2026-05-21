// =============================================================================
// AimController.cs — per-bot driver for the v7 per-slot aim override pool.
//
// Etap D (Layer 1 — own target picker, 2026-05-09):
// Stops parasitizing engine BT's m_lookPitch/m_lookYaw target. Each tick
// AimController scans alive enemy controllers, applies a 180° front-cone
// FOV filter (relative to bot's current eye yaw — discourages 360° spin-
// to-track-enemy-behind-you), picks closest, computes aim angle from
// bot eye-pos to target body-center, applies skill-scaled uniform noise,
// writes to AimSlot pool.
//
// Why own target: engine BT's target picking is the floor we couldn't
// beat by degrading. With own target picking, "good aim" is a real
// thing we can produce (high-skill bot precisely on target body) and
// "bad aim" is also a real thing (low-skill bot scattered around the
// vicinity). Skill differentiation finally has dynamic range.
//
// LoS check (raycast eye→target body) deferred to L1.5 — engine wrappers
// for TraceRay aren't trivially exposed in CSSharp; for L1 a bot may
// "track" enemies through walls. Visually wonky but lets us validate
// the angle pipeline first.
//
// Trigger discipline: engine BT still owns the fire decision (whether to
// pull trigger this tick). Our aim override only changes WHERE bullets
// go. If BT picks target A but we're aimed at B, bullets fly toward B
// when BT decides to fire. Some shots wasted, some lucky — acceptable
// for L1; L2+ may add usercmd injection to also drive trigger from C#.
//
// Pool architecture unchanged from v7: AimSlot[64] with bot_key +
// override + bt_target_*. We still write override; bt_target_* is now
// pure diagnostic (we no longer read it as our source of truth). The
// C++ writer stays in place — bt_target_* is a stale-but-fresh BT
// look-angle telemetry channel kept available for a possible future
// hybrid mode ("BT picks, C# refines"). See issue #46.
//
// History (don't redo):
//   - Identity passthrough through engine target = pure-degrade ceiling
//     too low, kennyS Smurf 95 indistinguishable from donk skill 51
//     in 7-round live test (2026-05-09). Hence Etap D.
//   - Sample-write phase pattern (8-tick cache) = 110ms aim lag, worse
//     than per-tick.
//   - Writing only m_angEyeAngles = smoother undoes it. Need m_lookPitch
//     write too. C++ side already does this.
// =============================================================================

using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace InsanityRevive;

public sealed class AimController
{
    /// <summary>Global off-switch. When true, all AimControllers no-op
    /// in Tick. Useful for diagnostic — set via rcon to test perslot
    /// override in isolation without our identity-passthrough writes
    /// stomping on it.</summary>
    public static bool GlobalDisable { get; set; } = false;

    private bool  _armed;
    private int   _lastSlot = -1;  // slot we last wrote to — clear on slot change
    private uint  _lastTargetSlotPlus1;  // target's slot+1 (so 0 = "no target last tick")

    // xorshift32 state for per-bot, per-tick aim noise. Seeded lazily from
    // BotProfile.Seed so two bots with the same skill/profile don't share
    // an RNG sequence (which would cluster their misses identically).
    private uint _rng;
    private bool _rngSeeded;

    /// <summary>FOV cone (full angle, degrees) the bot considers "in front
    /// of me" for target selection. 180° = front hemisphere. Tighter (e.g.
    /// 110°) would simulate "human can't see enemy 80° to the side" but
    /// also causes bot to ignore flanks; CS2 BT's natural FOV is wider
    /// than 110° so 180° is the conservative L1 default.</summary>
    private const float FovDeg = 180f;

    /// <summary>Approximate eye-z offset above pawn AbsOrigin for vector
    /// math. Real value is ~64 standing, ~46 crouched; for L1 we use a
    /// constant. Slight inaccuracy on crouched targets, will refine in
    /// L2+ when we read the actual viewmodel offset field.</summary>
    private const float EyeHeight = 64f;

    /// <summary>Z offset above pawn AbsOrigin where we aim — body
    /// center, NOT head. L1 is "aim at body, don't headhunt". Head
    /// aim is a per-skill modulation in L2+ (high-skill targets head,
    /// low-skill aims at chest/stomach).</summary>
    private const float TargetBodyHeight = 48f;

    /// <summary>Maximum per-axis additive aim error applied to the engine
    /// target before writing to the override pool. The actual error is scaled
    /// by (1 − CurrentAimSkill/100), so skill=100 → 0°, skill=50 → 2.5°,
    /// skill=0 → 5°. Picked to be visibly bad at low skill (a 5° flick miss
    /// at 30 m is ~2.6 m, full-body offset) but not silly: BT will still
    /// converge on the player when skill is high. Etap C+ will replace
    /// uniform noise with per-target scatter and twitch reaction.</summary>
    private const float MaxAimErrorDeg = 5f;

    public bool Armed => _armed;
    public int  LastTargetSlot => (int)_lastTargetSlotPlus1 - 1;

    /// <summary>Drive one tick. Pool may be null (early boot before manager
    /// owns its mmap); in that case do nothing — the engine smoother runs.</summary>
    public void Tick(int slot, CCSPlayerController? ctrl, PoolMmap? pool, BotProfile? profile)
    {
        if (GlobalDisable) { Disarm(pool); return; }
        if (pool == null || !pool.IsOpen) { _armed = false; return; }
        // Ctrl + pawn + bot all have to be live; bots in spec / mid-respawn
        // briefly drop out and we should disarm so the engine takes over.
        if (ctrl == null || !ctrl.IsValid) { Disarm(pool); return; }
        var pawn = ctrl.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) { Disarm(pool); return; }
        var bot = pawn.Bot;
        if (bot == null || bot.Handle == IntPtr.Zero) { Disarm(pool); return; }

        // If the slot moved (mapchange / reuse), clear the old AimSlot so we
        // don't leave a dangling override for the previous occupant.
        if (_lastSlot >= 0 && _lastSlot != slot && _armed)
        {
            pool.ClearAimSlot(_lastSlot);
            _armed = false;
        }
        _lastSlot = slot;

        // Bot eye position. AbsOrigin is feet; eye is +EyeHeight on Z.
        if (pawn.AbsOrigin == null) { Disarm(pool); return; }
        var eyePos = pawn.AbsOrigin;
        float eyeX = eyePos.X, eyeY = eyePos.Y, eyeZ = eyePos.Z + EyeHeight;

        // Bot's current facing yaw — for FOV gate.
        float yawRad = pawn.EyeAngles.Y * MathF.PI / 180f;
        float fwdX = MathF.Cos(yawRad);
        float fwdY = MathF.Sin(yawRad);

        // PICK TARGET: closest alive enemy in front-cone FOV.
        var target = PickTarget(ctrl, eyeX, eyeY, eyeZ, fwdX, fwdY);
        if (target == null)
        {
            // No target visible/picked — disarm so engine BT runs natural
            // (bot can wander, BT may still pick targets we don't see).
            Disarm(pool);
            return;
        }

        var tPawn = target.PlayerPawn?.Value;
        if (tPawn?.AbsOrigin == null) { Disarm(pool); return; }
        float tx = tPawn.AbsOrigin.X;
        float ty = tPawn.AbsOrigin.Y;
        float tz = tPawn.AbsOrigin.Z + TargetBodyHeight;

        // Compute aim angle from bot eye to target body center.
        float dx = tx - eyeX;
        float dy = ty - eyeY;
        float dz = tz - eyeZ;
        float dist2d = MathF.Sqrt(dx * dx + dy * dy);
        float yawDeg   = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        // Source 2 convention: pitch positive = looking down. atan2(dz, dist2d)
        // is positive when looking up (target above), so negate.
        float pitchDeg = -MathF.Atan2(dz, MathF.Max(0.001f, dist2d)) * 180f / MathF.PI;

        // Apply skill-scaled noise.
        if (!_rngSeeded)
        {
            ulong s = (profile?.Seed ?? 0xDEADBEEFCAFEBABEUL) ^ ((ulong)(uint)slot << 32) ^ 0xA1B2C3D4E5F60718UL;
            _rng = (uint)(s ^ (s >> 32));
            if (_rng == 0) _rng = 0xCAFEBABE;
            _rngSeeded = true;
        }
        float skill = profile?.CurrentAimSkill ?? 50f;
        float errorFactor = MathF.Max(0f, 1f - skill / 100f);
        float errorDeg = errorFactor * MaxAimErrorDeg;
        float noiseP = NextFloatM1to1() * errorDeg;
        float noiseY = NextFloatM1to1() * errorDeg;
        float overP = MathF.Max(-89f, MathF.Min(89f, pitchDeg + noiseP));
        float overY = yawDeg + noiseY;

        ulong botKey = (ulong)bot.Handle.ToInt64();
        pool.WriteAimSlot(slot, botKey, enabled: true, pitch: overP, yaw: overY);
        _armed = true;
        _lastTargetSlotPlus1 = (uint)(target.Slot + 1);
    }

    /// <summary>Pick closest alive enemy controller within FOV cone of
    /// (fwdX, fwdY). Returns null if no enemies in view. L1: distance-only,
    /// no LoS check (will see through walls). L1.5+ adds raycast.</summary>
    private CCSPlayerController? PickTarget(
        CCSPlayerController self,
        float eyeX, float eyeY, float eyeZ,
        float fwdX, float fwdY)
    {
        // FOV dot threshold: cos(half_fov_rad). For 180° → 0 (any front
        // hemisphere); 110° → cos(55°) ≈ 0.574.
        float halfFovRad = (FovDeg * 0.5f) * MathF.PI / 180f;
        float fovDotThreshold = MathF.Cos(halfFovRad);

        int myTeam = self.TeamNum;
        CCSPlayerController? best = null;
        float bestDistSq = float.MaxValue;

        foreach (var enemy in Utilities.GetPlayers())
        {
            if (enemy == null || !enemy.IsValid) continue;
            if (enemy.Slot == self.Slot) continue;
            if (enemy.TeamNum == myTeam) continue;
            // Spec / unassigned can't be hit.
            if (enemy.TeamNum < 2) continue;

            var ePawn = enemy.PlayerPawn?.Value;
            if (ePawn == null || !ePawn.IsValid) continue;
            if (ePawn.LifeState != 0) continue;  // 0 = alive
            if (ePawn.AbsOrigin == null) continue;

            float dx = ePawn.AbsOrigin.X - eyeX;
            float dy = ePawn.AbsOrigin.Y - eyeY;
            float dz = (ePawn.AbsOrigin.Z + TargetBodyHeight) - eyeZ;
            float distSq = dx * dx + dy * dy + dz * dz;
            if (distSq < 1f) continue;

            // FOV check on the horizontal plane (yaw only). This is
            // intentional — vertical FOV is rarely the bottleneck for
            // human aim, and CS maps are mostly horizontal anyway.
            float dist2d = MathF.Sqrt(dx * dx + dy * dy);
            if (dist2d < 0.001f) continue;  // directly above/below — skip
            float dot = (dx * fwdX + dy * fwdY) / dist2d;
            if (dot < fovDotThreshold) continue;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = enemy;
            }
        }
        return best;
    }

    /// <summary>Force-disarm: clear our AimSlot if armed. Idempotent. Called
    /// when the bot loses its pawn / is despawned, on slot change, or after
    /// IdleThreshold ticks of no look movement.</summary>
    public void Disarm(PoolMmap? pool)
    {
        if (!_armed) return;
        if (pool != null && pool.IsOpen && _lastSlot >= 0) pool.ClearAimSlot(_lastSlot);
        _armed = false;
        _lastTargetSlotPlus1 = 0;
    }

    private static float AngleDelta(float a, float b)
    {
        float d = a - b;
        while (d >  180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    /// <summary>Fold any angle into [-180, 180]. Used on lookP before
    /// pitch-clamp so wraparound values (e.g. 357° meaning -3°) don't
    /// fold to ±89° straight up/down after Min/Max.</summary>
    private static float NormalizeAngle(float a)
    {
        float n = a;
        while (n >  180f) n -= 360f;
        while (n < -180f) n += 360f;
        return n;
    }

    /// <summary>xorshift32 step + map to [-1, 1) float. Allocation-free,
    /// fine quality for visual aim noise (not crypto). State must be
    /// nonzero — caller seeds non-zero in Tick.</summary>
    private float NextFloatM1to1()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        // Use 24 high bits for mantissa precision; map [0, 2^24) → [-1, 1).
        return (_rng >> 8) * (2f / 16777216f) - 1f;
    }
}
