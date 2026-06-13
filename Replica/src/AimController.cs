// =============================================================================
// AimController.cs — per-bot driver for the v7 per-slot aim override pool.
//
// Etap D (Layer 1 — own target picker, 2026-05-09):
// Stops parasitizing engine BT's m_lookPitch/m_lookYaw target. Each tick
// AimController scans alive enemy controllers, applies a 180° front-cone
// FOV filter (relative to bot's current eye yaw — discourages 360° spin-
// to-track-enemy-behind-you), picks closest, computes aim angle from
// bot eye-pos to target body-center, applies a smoothed per-target miss
// offset (#43) scaled by skill and bot velocity (#45), writes to AimSlot
// pool. On target switch the eye holds its previous angle for the
// profile's reaction time before tracking the new target.
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
// pure diagnostic (we no longer read it as our source of truth).
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

namespace Replica;

public sealed class AimController
{
    /// <summary>Global off-switch. When true, all AimControllers no-op
    /// in Tick. Useful for diagnostic — set via rcon to test perslot
    /// override in isolation without our identity-passthrough writes
    /// stomping on it.</summary>
    public static bool GlobalDisable { get; set; } = false;

    /// <summary>Structural aim bias (#44, record→profile→bot loop). When on,
    /// each bot adds its recorded human's deterministic per-weapon aim flaw
    /// (median yaw/pitch from StructuralProfileStore) on top of the RNG
    /// scatter. OFF by default — flipped at runtime via replica_aim_structural
    /// so it can be A/B'd live. The per-bot bias map is set at adopt time.</summary>
    public static bool StructuralEnabled { get; set; } = false;

    /// <summary>This bot's recorded per-weapon (yaw, pitch) bias in degrees,
    /// or null if its persona has no recording. Cached by the manager at
    /// FakeClient creation (keyed by persona name) so Tick does no lookup
    /// beyond the current weapon.</summary>
    public Dictionary<string, (float Yaw, float Pitch)>? StructuralBias { get; set; }

    private bool  _armed;
    private int   _lastSlot = -1;  // slot we last wrote to — clear on slot change
    private uint  _lastTargetSlotPlus1;  // target's slot+1 (so 0 = "no target last tick")

    // Smoothed per-target scatter (#43). Direction of the miss is a unit
    // offset in [-1,1] per axis, persisted across ticks for the current
    // target and resampled only on target change or after the profile's
    // reaction interval. Magnitude is recomputed every tick (skill ×
    // movement), so the offset direction stays stable while its size
    // breathes with bot velocity — no write-phase caching, no added lag
    // (the 8-tick cache attempt cost 110 ms and was rolled back).
    private float _scatterUnitP;
    private float _scatterUnitY;
    private int   _scatterAgeTicks;

    // Reaction-delay onset (#43): on target switch the eye holds the last
    // written angle for CurrentReactionMs before tracking the new target —
    // "hasn't reacquired yet" instead of a same-tick snap.
    private int   _acquireTicksLeft;
    private float _latchPitchDeg;
    private float _latchYawDeg;
    private float _lastWrittenPitch;
    private float _lastWrittenYaw;

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
    /// converge on the player when skill is high. Direction of the error
    /// is the smoothed per-target scatter (see #43); magnitude additionally
    /// scales with bot velocity (see #45).</summary>
    private const float MaxAimErrorDeg = 5f;

    /// <summary>Movement-vs-camp error scaling (#45). A bot holding an
    /// angle keeps its base error; a bot mid-run multiplies it. Ramp is
    /// linear on horizontal speed between CampSpeed and RunSpeed u/s
    /// (walk ≈ 85, run ≈ 250 in CS2), capped at MoveErrorMultMax. Applied
    /// on top of the per-skill factor: a camping low-skill bot still
    /// misses, a running high-skill bot is no longer laser-precise.</summary>
    private const float CampSpeed       = 30f;
    private const float RunSpeed        = 220f;
    private const float MoveErrorMultMax = 2.75f;

    // Spray-pattern modulation (#45). Error blooms with consecutive shots
    // in a burst: humans lose the pattern after the first few bullets.
    // Burst length is fed from EventWeaponFire (NotifyShot) — no schema
    // dependency; a gap longer than BurstResetTicks (≈250 ms) starts a
    // fresh burst. First SprayFreeShots shots are unaffected (taps and
    // 2-bursts stay true to base skill), then +SprayBloomPerShot per
    // bullet up to SprayBloomMax.
    private const int   BurstResetTicks  = 16;
    private const int   SprayFreeShots   = 2;
    private const float SprayBloomPerShot = 0.07f;
    private const float SprayBloomMax     = 1.6f;
    private int _lastShotTick = -100000;
    private int _burstShots;

    // Distance × weapon-class error scaling (#45 follow-up from live
    // observation: long-range pistol headshots were systematically too
    // frequent — P250 HS at ~57 m class). Angle-constant error already
    // shrinks hit probability with range, but real aim degrades FASTER
    // than linear once past mid-range, and degrades differently per
    // weapon class (a pistol duel at 50 m is a coin toss for humans; an
    // AWP is not). farT ramps 0→1 between FarStartM and FarEndM; the
    // class multiplier is the error factor at full range.
    private const float FarStartM = 18f;
    private const float FarEndM   = 50f;
    private const float UnitsPerMeter = 52.5f;

    // Head-bias (#44, path b — high-skill differentiation). L1 aimed at
    // body center for everyone, capping top personas at body-shot
    // lethality. Now aim height lerps from body center to head with
    // skill: ≤HeadBiasSkillFloor stays body, 100 aims at the head.
    // Because the bias rides CurrentAimSkill (mood/tilt-modulated), a
    // tilted pro stops headhunting — exactly the human signature.
    private const float HeadHeight         = 66f;
    private const float HeadBiasSkillFloor = 65f;

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

        // Head-bias (#44): aim height rises from body center toward the
        // head as CurrentAimSkill exceeds the floor. Computed before the
        // angle math so scatter applies around the *intended* point.
        float skill = profile?.CurrentAimSkill ?? 50f;
        float headBias = Math.Clamp((skill - HeadBiasSkillFloor) / (100f - HeadBiasSkillFloor), 0f, 1f);
        float aimHeight = TargetBodyHeight + headBias * (HeadHeight - TargetBodyHeight);

        float tx = tPawn.AbsOrigin.X;
        float ty = tPawn.AbsOrigin.Y;
        float tz = tPawn.AbsOrigin.Z + aimHeight;

        // Compute aim angle from bot eye to target body center.
        float dx = tx - eyeX;
        float dy = ty - eyeY;
        float dz = tz - eyeZ;
        float dist2d = MathF.Sqrt(dx * dx + dy * dy);
        float yawDeg   = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        // Source 2 convention: pitch positive = looking down. atan2(dz, dist2d)
        // is positive when looking up (target above), so negate.
        float pitchDeg = -MathF.Atan2(dz, MathF.Max(0.001f, dist2d)) * 180f / MathF.PI;

        if (!_rngSeeded)
        {
            ulong s = (profile?.Seed ?? 0xDEADBEEFCAFEBABEUL) ^ ((ulong)(uint)slot << 32) ^ 0xA1B2C3D4E5F60718UL;
            _rng = (uint)(s ^ (s >> 32));
            if (_rng == 0) _rng = 0xCAFEBABE;
            _rngSeeded = true;
        }

        // Reaction interval in ticks (64 Hz server). Floor of 1 tick so a
        // degenerate profile can't divide the resample cadence to zero.
        int reactionTicks = Math.Max(1, (profile?.CurrentReactionMs ?? 250) * 64 / 1000);

        uint targetKey = (uint)(target.Slot + 1);
        if (targetKey != _lastTargetSlotPlus1)
        {
            // Target switch: latch the previous written angle for the
            // reaction window (only if we were tracking someone — coming
            // from disarmed, the engine owned the eye and there is nothing
            // of ours to hold).
            if (_lastTargetSlotPlus1 != 0 && _armed)
            {
                _acquireTicksLeft = reactionTicks;
                _latchPitchDeg = _lastWrittenPitch;
                _latchYawDeg   = _lastWrittenYaw;
            }
            ResampleScatter();
            _lastTargetSlotPlus1 = targetKey;
        }
        else if (++_scatterAgeTicks >= reactionTicks)
        {
            // Same target, but the human "re-estimates" where the enemy is
            // about once per reaction interval — drift the miss direction.
            ResampleScatter();
        }

        ulong botKey = (ulong)bot.Handle.ToInt64();

        if (_acquireTicksLeft > 0)
        {
            // Reaction-delay onset: hold the stale angle; the eye reaches
            // the new target only after the profile's reaction time.
            _acquireTicksLeft--;
            pool.WriteAimSlot(slot, botKey, enabled: true, pitch: _latchPitchDeg, yaw: _latchYawDeg);
            _armed = true;
            return;
        }

        // Error magnitude: per-skill factor × movement × spray × range (#45).
        float errorFactor = MathF.Max(0f, 1f - skill / 100f);
        float speed2d = 0f;
        var vel = pawn.AbsVelocity;
        if (vel != null) speed2d = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
        float moveT = Math.Clamp((speed2d - CampSpeed) / (RunSpeed - CampSpeed), 0f, 1f);
        float moveMult = 1f + moveT * (MoveErrorMultMax - 1f);

        // Spray bloom: burst length is maintained by NotifyShot (event-
        // fed); a stale burst (gap > BurstResetTicks) reads as zero.
        int burst = (Server.TickCount - _lastShotTick) > BurstResetTicks ? 0 : _burstShots;
        float sprayMult = MathF.Min(SprayBloomMax,
            1f + MathF.Max(0, burst - SprayFreeShots) * SprayBloomPerShot);

        // Distance × weapon-class scaling. Skill shaves the penalty: a
        // 95-skill persona keeps 35% of the class penalty, a 50-skill
        // one takes it in full — pros hit long pistol shots *sometimes*.
        float farT = Math.Clamp((dist2d / UnitsPerMeter - FarStartM) / (FarEndM - FarStartM), 0f, 1f);
        string? weaponName = ActiveWeaponName(pawn);
        float classFarMult = FarClassMult(weaponName);
        float skillShave = 1f - 0.65f * (skill / 100f);
        float rangeMult = 1f + farT * (classFarMult - 1f) * skillShave;

        float errorDeg = errorFactor * MaxAimErrorDeg * moveMult * sprayMult * rangeMult;

        float overP = pitchDeg + _scatterUnitP * errorDeg;
        float overY = yawDeg + _scatterUnitY * errorDeg;

        // Structural bias (#44): deterministic recorded-human per-weapon flaw,
        // added BEFORE the pitch clamp so it can't push past ±89°. Applied on
        // top of scatter so the bot still has both a stable habit and live
        // jitter. Magnitudes are sub-degree to a few degrees (medians).
        if (StructuralEnabled && StructuralBias != null && weaponName != null
            && StructuralBias.TryGetValue(weaponName, out var bias))
        {
            overY += bias.Yaw;
            overP += bias.Pitch;
        }
        overP = MathF.Max(-89f, MathF.Min(89f, overP));

        pool.WriteAimSlot(slot, botKey, enabled: true, pitch: overP, yaw: overY);
        _armed = true;
        _lastWrittenPitch = overP;
        _lastWrittenYaw   = overY;
    }

    /// <summary>Burst bookkeeping for spray bloom (#45). Fed from the
    /// plugin's EventWeaponFire handler — the engine BT owns the trigger,
    /// we only observe. A gap longer than BurstResetTicks starts a new
    /// burst.</summary>
    public void NotifyShot(int tick)
    {
        _burstShots = (tick - _lastShotTick) > BurstResetTicks ? 1 : _burstShots + 1;
        _lastShotTick = tick;
    }

    /// <summary>Full-range error multiplier by weapon class (#45). Read
    /// from the active weapon's designer name; null/missing (mid-equip,
    /// knife out) reads as 1.0 — no penalty. Snipers get a discount:
    /// long range is their job.</summary>
    /// <summary>Active weapon designer name (e.g. "weapon_ak47"), or null
    /// mid-equip / no weapon. Read once per tick, shared by the range-class
    /// multiplier and the structural-bias lookup.</summary>
    private static string? ActiveWeaponName(CCSPlayerPawn pawn)
    {
        try { return pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName; } catch { return null; }
    }

    private static float FarClassMult(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 1.0f;
        switch (name)
        {
            case "weapon_glock": case "weapon_usp_silencer": case "weapon_hkp2000":
            case "weapon_p250":  case "weapon_elite":        case "weapon_fiveseven":
            case "weapon_cz75a": case "weapon_tec9":         case "weapon_deagle":
            case "weapon_revolver":
                return 2.2f;
            case "weapon_mp9":   case "weapon_mac10": case "weapon_mp7":
            case "weapon_mp5sd": case "weapon_ump45": case "weapon_p90":
            case "weapon_bizon":
                return 1.5f;
            case "weapon_nova":  case "weapon_xm1014": case "weapon_sawedoff":
            case "weapon_mag7":
                return 2.5f;
            case "weapon_awp":   case "weapon_ssg08": case "weapon_scar20":
            case "weapon_g3sg1":
                return 0.85f;
            default:
                return 1.15f;  // rifles / LMGs — mild far-range tax
        }
    }

    /// <summary>Resample the persistent miss direction (unit offset per
    /// axis) and reset its age. Called on target change and once per
    /// reaction interval while tracking the same target.</summary>
    private void ResampleScatter()
    {
        _scatterUnitP = NextFloatM1to1();
        _scatterUnitY = NextFloatM1to1();
        _scatterAgeTicks = 0;
    }

    /// <summary>Pick closest alive enemy controller within FOV cone of
    /// (fwdX, fwdY). Returns null if no enemies in view.
    ///
    /// LoS (#42): instead of our own raycast (engine trace isn't cleanly
    /// exposed to CSSharp), gate on the engine's spotted state —
    /// <c>m_entitySpottedState.m_bSpottedByMask</c> is maintained by the
    /// engine's own visibility traces per observer. An enemy whose mask
    /// lacks our slot bit is invisible to this bot: not pickable, so the
    /// eye no longer tracks people through walls. Spotted-state decay
    /// (~radar memory) gives a human-ish brief "remembering" of a target
    /// that just broke LoS, and the reaction latch smooths reacquire.</summary>
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
            if (!IsSpottedBy(ePawn, self.Slot)) continue;  // LoS gate (#42)

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

    /// <summary>Engine-maintained visibility: does observer slot's bit sit
    /// in the pawn's spotted-by mask? Mask layout: uint32[2], one bit per
    /// slot (0-63). Any read failure counts as NOT spotted — the safe
    /// default is "can't see", which disarms rather than wallhacks.</summary>
    private static bool IsSpottedBy(CCSPlayerPawn pawn, int observerSlot)
    {
        if (observerSlot < 0 || observerSlot >= 64) return false;
        try
        {
            var mask = pawn.EntitySpottedState.SpottedByMask;
            uint word = mask[observerSlot / 32];
            return (word & (1u << (observerSlot % 32))) != 0;
        }
        catch { return false; }
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
        _acquireTicksLeft = 0;  // never carry a stale reaction latch across re-arm
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
