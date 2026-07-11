using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;

namespace Replica;

// Stage 4 — APOCALYPSE (C4 suicide bots). Manual-only branch: see RevealController.cs.
public sealed partial class RevealController
{
    // Stage 4 (APOCALYPSE) tunables.
    /// <summary>Hard upper bound on Stage 4 duration. After this, EndReveal regardless of carrier state.</summary>
    private const int Stage4MaxDurationSec = 90;
    /// <summary>Carrier "vision" range for detonator-mode arming. ~30m world ≈ 1968 Hammer Units (1m ≈ 65.6 HU).</summary>
    private const float Stage4VisionRangeHU = 1968f;
    /// <summary>Detonator timer min/max (random per-carrier on arm). Range 5–10 sec.</summary>
    private const int Stage4DetonateDelayMinTicks = 5 * 64;
    private const int Stage4DetonateDelayMaxTicks = 10 * 64;
    /// <summary>Beep interval at arm time (slow) and at detonation (fast). Linear interpolation between.</summary>
    private const int Stage4BeepIntervalEarlyTicks = 45;  // ~0.7s @ 64Hz
    private const int Stage4BeepIntervalLateTicks  = 19;  // ~0.3s @ 64Hz
    /// <summary>Vision-detection cadence. 0.5s — cheap, sufficient for "saw a human in last half-second".</summary>
    private const int Stage4VisionTickInterval = 32;
    /// <summary>env_explosion params at detonation. iMagnitude 200 ~ HE grenade equivalent; radius 400 HU ~ 6m.</summary>
    private const int   Stage4ExplosionMagnitude = 200;
    private const float Stage4ExplosionRadius    = 400f;
    /// <summary>1 in N bots becomes a C4 carrier on Stage 4 entry. 3 = ~33% of fleet.</summary>
    private const int Stage4CarrierFraction = 3;

    /// <summary>
    /// Per-carrier Stage 4 state. Created on Stage 4 entry for each bot
    /// promoted to C4 carrier, mutated through arm → beep → detonate.
    /// Cleared at CleanupReveal.
    /// </summary>
    private struct ApocalypseCarrier
    {
        public bool   Armed;
        public bool   Detonated;
        public int    ArmedAtTick;
        public int    DetonateAtTick;
        public int    LastBeepTick;
        public Vector? LastKnownHumanPos;
    }

    private readonly Dictionary<int, ApocalypseCarrier> _apocalypseCarriers = new();

    // Stage4 beep — soundevent names rename across CS2 patches. Resolved
    // ONCE at EnterStage4 against the first promoted-and-validity-passed
    // carrier pawn (deterministic probe — avoids latching silent if the
    // first per-tick caller happens to hit a transient bad pawn state).
    // Cached for the duration of the Stage 4 cycle; reset at EnterStage4.
    // If all candidates fail (including a generic-CS2 fallback), warn
    // admin chat once so the silent-detonation UX hole is visible.
    // See issue #22 + Wave review on PR #70.
    private string? _stage4WorkingBeep;
    private int _stage4LastVisionTick;

    // ──────────────────────────────────────────────────────────────────
    // Stage 4 — APOCALYPSE (C4 suicide bots)
    // ──────────────────────────────────────────────────────────────────
    //
    // Manual-only trigger: !reveal_apocalypse / replica_reveal_apocalypse.
    // Per session-plan 2026-05-08: usable from Stage 1/2/3 (not Idle).
    // Calling during an already-active Stage 4 is a no-op (idempotent).
    //
    // Entry effects:
    //  - Install BotDamagePatch (Listeners.OnEntityTakeDamagePre filter,
    //    blocks inferno/molotov/HE damage TO managed bots — explosions
    //    fry humans only). Step 2 of 2026-05-08 session.
    //  - Every Nth bot (default 1-of-3) gets weapon_c4 via GiveNamedItem.
    //    Probe 2 (notes/stage_4_probes.md) verifies whether visible C4 +
    //    no PLANT-marker is achievable; v1 ships best-effort, will adjust
    //    after first user-driven probe.
    //  - Each carrier enters arm-pending state. Vision tick polls every
    //    0.5s, arms carrier when distance to nearest human < ~30m.
    //
    // Per-tick:
    //  - Beep escalation (interval lerps 0.7s → 0.3s as detonation nears).
    //  - Detonation when timer expires — env_explosion at carrier's
    //    current pos (or last-known if pawn went invalid).
    //
    // Termination: max-duration hit OR all carriers detonated.
    public bool StartApocalypse()
    {
        if (Stage == RevealStage.Idle) {
            Log.Warn("StartApocalypse: reveal not active — Stage Idle.");
            return false;
        }
        if (Stage == RevealStage.Stage4) {
            Log.Info("StartApocalypse: already in Stage 4 — no-op.");
            return false;
        }
        EnterStage4();
        return true;
    }

    private void EnterStage4()
    {
        // Mirror EnterStage0-3 empty-fleet abort. APOCALYPSE on an empty
        // fleet promotes zero carriers and ticks for Stage4MaxDurationSec
        // (60s) without doing anything.
        if (_mgr.All.Count == 0) {
            Log.Warn("EnterStage4: fleet empty — aborting reveal");
            CleanupReveal();
            return;
        }

        Stage = RevealStage.Stage4;
        _stageStartTick = Server.TickCount;
        _stage4LastVisionTick = 0;
        _apocalypseCarriers.Clear();
        // Fresh sound-resolution cycle — bootstrap probe runs below.
        _stage4WorkingBeep = null;

        Server.PrintToChatAll($" {ChatColors.DarkRed}[REPLICÁ] APOCALYPSE — C4 RAIN");

        // Install damage filter so the explosions don't fry our own swarm
        // alongside the humans. Idempotent — Install no-ops if already on.
        if (!_mgr.DamagePatch.IsInstalled) _mgr.DamagePatch.Install();

        // Per-carrier 0.5x incoming damage (issue #26). Predicate reads
        // _apocalypseCarriers live, so newly-promoted carriers below pick
        // up the multiplier the moment they're added to the dict.
        _mgr.DamagePatch.CarrierPredicate = slot => _apocalypseCarriers.ContainsKey(slot);

        // Promote ~1-of-N bots to C4 carriers (issue #24).
        //
        // Old impl used index-modulo (`i % Stage4CarrierFraction == 0`). If
        // the bot at index k*F happened to be dead / invalid / mid-respawn,
        // the slot was silently skipped and carrierCount shrank below the
        // design fraction — pathological case: every Nth bot in flicker →
        // 0 carriers → Stage 4 sits idle for Stage4MaxDurationSec doing
        // nothing. Decouple the target count from index position: pre-
        // filter alive bots, then take the first `target` of them.
        var bots = _mgr.All.ToList();
        CCSPlayerPawn? probePawn = null;
        int target = bots.Count == 0
            ? 0
            // ceil(N/F) — preserves original count for all-alive case
            // (e.g. 6 bots / 3 → 2; 7 bots / 3 → 3; 9 bots / 3 → 3).
            : Math.Max(1, (bots.Count + Stage4CarrierFraction - 1) / Stage4CarrierFraction);

        var alive = new List<FakeClient>(bots.Count);
        foreach (var fc in bots)
        {
            try
            {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                var pawn = c.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid || pawn.LifeState != 0) continue;
                alive.Add(fc);
            }
            catch (Exception ex) { Log.Debug($"Stage4 alive-filter slot={fc.Slot}: {ex.Message}"); }
        }

        int carrierCount = 0;
        foreach (var fc in alive.Take(target))
        {
            try
            {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;  // re-check; state can drift between filter and promote
                var pawn = c.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid) continue;
                c.GiveNamedItem("weapon_c4");
                _combatState[fc.Slot] = new BotCombatState {
                    ForcedWeapon = "weapon_c4", StageWhenSet = RevealStage.Stage4 };
                _apocalypseCarriers[fc.Slot] = new ApocalypseCarrier {
                    Armed = false, Detonated = false,
                    LastBeepTick = 0, LastKnownHumanPos = null,
                };
                carrierCount++;
                if (probePawn == null) probePawn = pawn;
            }
            catch (Exception ex) { Log.Error($"Stage4 give c4 slot={fc.Slot}: {ex.Message}"); }
        }

        if (target > alive.Count)
        {
            Log.Warn($"Stage 4 APOCALYPSE: only {alive.Count}/{target} desired carriers " +
                     $"({alive.Count} bots alive of {bots.Count} fleet)");
        }

        // Bootstrap-resolve beep against a known-good emitter (first
        // promoted carrier post-validity). Done here so a transient bad
        // pawn during the cycle can't latch us into silent state via the
        // per-tick path. Wave review on PR #70.
        if (probePawn != null) ResolveStage4Beep(probePawn);

        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage4" }, { "name", "APOCALYPSE" },
            { "resolved_beep", _stage4WorkingBeep },
            { "carrier_count", carrierCount },
        });

        Log.Info($"Stage 4 APOCALYPSE: {carrierCount} carriers armed of {bots.Count} bots " +
                 $"(target {target}, fraction 1/{Stage4CarrierFraction})");
    }

    private void TickStage4()
    {
        EnforceTeamMembership();

        // Vision check at 0.5s cadence — distance-only because no working
        // TraceRay wrapper in CSSharp 1.0.367 (see DeploySwarmAndKnifeRush
        // comment for why we don't LOS-check). Distance-only means a
        // carrier can arm through a wall; acceptable trade for v1 — the
        // explosion radius is large enough to splash into adjacent rooms
        // anyway.
        if (Server.TickCount - _stage4LastVisionTick >= Stage4VisionTickInterval)
        {
            _stage4LastVisionTick = Server.TickCount;
            DoStage4Vision();
        }

        // Per-tick: beep + detonate. Both are cheap (≤ N=fleet/3 reads).
        DoStage4Beep();
        DoStage4Detonate();

        // Termination 1: max duration hit.
        var elapsedSec = (Server.TickCount - _stageStartTick) / 64;
        if (elapsedSec >= Stage4MaxDurationSec)
        {
            EndReveal();
            return;
        }

        // Termination 2: all carriers detonated. Saves us 60+ sec of
        // dead-stage if the boom plays out fast.
        bool anyCarrierLive = false;
        foreach (var (_, st) in _apocalypseCarriers)
        {
            if (!st.Detonated) { anyCarrierLive = true; break; }
        }
        if (!anyCarrierLive && _apocalypseCarriers.Count > 0) EndReveal();
    }

    private void DoStage4Vision()
    {
        var humans = LivingHumanControllers();
        if (humans.Count == 0) return;

        var slots = _apocalypseCarriers.Keys.ToList();
        foreach (var slot in slots)
        {
            var carrier = _apocalypseCarriers[slot];
            if (carrier.Armed || carrier.Detonated) continue;

            try
            {
                var c = Utilities.GetPlayerFromSlot(slot);
                if (c == null || !c.IsValid) continue;
                var pawn = c.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid || pawn.LifeState != 0) continue;
                if (pawn.AbsOrigin == null) continue;

                Vector? nearestPos = null;
                float nearestD2 = Stage4VisionRangeHU * Stage4VisionRangeHU;
                foreach (var h in humans)
                {
                    var hp = h.PlayerPawn?.Value;
                    if (hp?.AbsOrigin == null) continue;
                    float dx = hp.AbsOrigin.X - pawn.AbsOrigin.X;
                    float dy = hp.AbsOrigin.Y - pawn.AbsOrigin.Y;
                    float dz = hp.AbsOrigin.Z - pawn.AbsOrigin.Z;
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 < nearestD2) { nearestD2 = d2; nearestPos = hp.AbsOrigin; }
                }

                if (nearestPos == null) continue;

                int delay = _rng.Next(Stage4DetonateDelayMinTicks,
                                      Stage4DetonateDelayMaxTicks + 1);
                carrier.Armed = true;
                carrier.ArmedAtTick = Server.TickCount;
                carrier.DetonateAtTick = Server.TickCount + delay;
                carrier.LastKnownHumanPos = nearestPos;
                _apocalypseCarriers[slot] = carrier;
                Log.Info($"Stage 4 carrier slot={slot} ARMED — detonate in {delay / 64.0:F1}s");
            }
            catch (Exception ex) { Log.Debug($"Stage4 vision slot={slot}: {ex.Message}"); }
        }
    }

    private void DoStage4Beep()
    {
        var slots = _apocalypseCarriers.Keys.ToList();
        foreach (var slot in slots)
        {
            var carrier = _apocalypseCarriers[slot];
            if (!carrier.Armed || carrier.Detonated) continue;

            int totalDelay = carrier.DetonateAtTick - carrier.ArmedAtTick;
            int elapsed    = Server.TickCount - carrier.ArmedAtTick;
            float t = totalDelay > 0
                ? Math.Clamp((float)elapsed / totalDelay, 0f, 1f)
                : 1f;
            int interval = (int)(Stage4BeepIntervalEarlyTicks * (1 - t)
                               + Stage4BeepIntervalLateTicks  * t);

            if (Server.TickCount - carrier.LastBeepTick < interval) continue;

            try
            {
                var c = Utilities.GetPlayerFromSlot(slot);
                var pawn = c?.PlayerPawn?.Value;
                if (pawn != null && pawn.IsValid)
                {
                    EmitStage4Beep(pawn, slot);
                }
            }
            catch (Exception ex) { Log.Debug($"Stage4 beep slot={slot}: {ex.Message}"); }

            carrier.LastBeepTick = Server.TickCount;
            _apocalypseCarriers[slot] = carrier;
        }
    }

    /// <summary>
    /// Bootstrap-probe candidate C4 beep soundevents at Stage 4 entry.
    /// Walks a known-good emitter (first promoted-and-validity-passed
    /// carrier pawn) through the candidate list; the first one that
    /// doesn't throw becomes the cached name for the rest of the cycle.
    /// If all candidates fail (incl. the generic-CS2 fallback), warn
    /// admin chat once + Log.Warn so the silent-detonation UX hole isn't
    /// buried in Debug. Issue #22 + Wave review on PR #70.
    /// </summary>
    private void ResolveStage4Beep(CCSPlayerPawn probe)
    {
        // Order: known CS2 C4 names, then a generic-CS2 fallback that's
        // been stable across patches. If even the fallback throws, we
        // surface the failure once and stay silent for the cycle.
        string[] candidates = {
            "Weapon_C4.Click", "weapons.c4.beep", "Weapons.C4.Beep",
            "BombPlant.Beep",  "Weapon_C4.PlantBeep",
            // Generic standard-manifest sound — load-bearing fallback so
            // Stage 4 has SOME audio cue even if all C4 events are renamed.
            "Buttons.snd9",
        };
        foreach (var name in candidates)
        {
            try
            {
                probe.EmitSound(name);
                _stage4WorkingBeep = name;
                break;
            }
            catch { /* try next */ }
        }

        if (_stage4WorkingBeep == null)
        {
            Log.Warn($"Stage4 beep: ALL {candidates.Length} candidate soundevents failed on bootstrap probe (incl. generic fallback). " +
                     $"Stage 4 will run silent — update RevealController.cs candidates list.");
            Server.PrintToChatAll(
                $" {ChatColors.DarkRed}[REPLICÁ] {ChatColors.Default}stage4: beep audio unavailable this round — escalating-tension cue muted");
        }
        else if (_stage4WorkingBeep == "Buttons.snd9")
        {
            Log.Warn($"Stage4 beep: C4 soundevents not found; falling back to '{_stage4WorkingBeep}'. " +
                     $"Update RevealController.cs candidates list with the new CS2 name.");
        }
        else
        {
            Log.Info($"Stage4 beep: resolved to '{_stage4WorkingBeep}'");
        }
    }

    private void EmitStage4Beep(CCSPlayerPawn pawn, int slot)
    {
        // Bootstrap probe ran at EnterStage4 — emit the cached name (or
        // no-op silently if the probe found nothing). Cache-invalidation
        // on per-tick throw is a defense against a soundevent vanishing
        // mid-stage (extremely rare — soundevents are level-loaded); we
        // null the cache so the rest of the cycle stays silent rather
        // than re-probing on the per-tick path (race risk).
        if (_stage4WorkingBeep == null) return;
        try { pawn.EmitSound(_stage4WorkingBeep); }
        catch (Exception ex)
        {
            Log.Debug($"Stage4 beep slot={slot}: cached '{_stage4WorkingBeep}' threw: {ex.Message}");
            _stage4WorkingBeep = null;
        }
    }

    private void DoStage4Detonate()
    {
        var slots = _apocalypseCarriers.Keys.ToList();
        foreach (var slot in slots)
        {
            var carrier = _apocalypseCarriers[slot];
            if (!carrier.Armed || carrier.Detonated) continue;
            if (Server.TickCount < carrier.DetonateAtTick) continue;

            // Resolve detonation position.
            Vector? pos = null;
            CCSPlayerController? c = null;
            try
            {
                c = Utilities.GetPlayerFromSlot(slot);
                var pawn = c?.PlayerPawn?.Value;
                if (pawn?.AbsOrigin != null && pawn.IsValid && pawn.LifeState == 0)
                {
                    pos = pawn.AbsOrigin;
                }
                else
                {
                    pos = carrier.LastKnownHumanPos;
                }
            }
            catch { pos = carrier.LastKnownHumanPos; }

            if (pos != null) SpawnExplosionAt(pos);

            carrier.Detonated = true;
            _apocalypseCarriers[slot] = carrier;

            // Suicide the carrier — visual death + drops C4. Best-effort;
            // if pawn already invalid the explosion already fired anyway.
            try
            {
                var pawn = c?.PlayerPawn?.Value;
                if (pawn != null && pawn.IsValid && pawn.LifeState == 0)
                    pawn.CommitSuicide(true, true);
            }
            catch (Exception ex) { Log.Debug($"Stage4 carrier suicide slot={slot}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Spawn an env_explosion at <paramref name="pos"/> and immediately
    /// trigger it via "Explode" input. Bots are immune via BotDamagePatch
    /// (inflictor class = "hegrenade_projectile" or similar from the
    /// explosion's damage path). Humans take damage normally.
    ///
    /// Best-effort: if entity creation fails (CSSharp build doesn't
    /// expose CEnvExplosion typed wrapper, or DispatchSpawn API
    /// missing), logs and returns. Stage 4 still ends, just without
    /// visible boom — fallback acceptable for v1.
    /// </summary>
    private void SpawnExplosionAt(Vector pos)
    {
        try
        {
            var explosion = Utilities.CreateEntityByName<CEnvExplosion>("env_explosion");
            if (explosion == null || !explosion.IsValid)
            {
                Log.Warn("Stage 4 SpawnExplosionAt: CreateEntityByName returned null/invalid");
                return;
            }

            // DispatchSpawn finalizes networked-state from keyvalues —
            // anything we write to the entity BEFORE that pass can get
            // clobbered by spawn-time defaulting (issue #21 for magnitude/
            // radius). Origin is no different: a pre-spawn Teleport may
            // be reset to the entity-dict default (world origin) by the
            // same pass, in which case the explosion would fire at
            // (0,0,0) regardless of `pos`. Spawn first with an empty
            // entity, THEN teleport + configure schema, THEN trigger.
            explosion.DispatchSpawn();
            explosion.Teleport(pos, new QAngle(), new Vector());

            // Configure magnitude + radius via schema AFTER DispatchSpawn so
            // the values stick at the moment Explode reads them. If
            // SchemaSafety refuses, we get a default-strength explosion
            // which is still visible/audible.
            SchemaSafety.WriteAndMark<int>(explosion, explosion.Handle,
                "CEnvExplosion", "m_iMagnitude", Stage4ExplosionMagnitude);
            SchemaSafety.WriteAndMark<float>(explosion, explosion.Handle,
                "CEnvExplosion", "m_flRadius", Stage4ExplosionRadius);

            explosion.AcceptInput("Explode");
        }
        catch (Exception ex) { Log.Error($"Stage 4 SpawnExplosionAt: {ex.Message}"); }
    }
}
