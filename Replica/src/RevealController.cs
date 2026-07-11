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

/// <summary>
/// Reveal Finale state machine (P/12, v0.7.0-beta).
///
/// Triggered by `!reveal` (admin chat) or `replica_reveal` (rcon). NO
/// confirmation prompt, re-runnable indefinitely. Re-trigger during an
/// active reveal calls <see cref="CleanupReveal"/> first, then enters
/// Stage 0 fresh.
///
/// State machine:
///   Idle
///     ↓  Start()
///   Stage 0  pre-warning ~5s — random bot spams "1" 5× + sync impulse
///     ↓  Stage 1 entry at t=5s
///   Stage 1  knife rush — strip+knife, m_flLaggedMovementValue=1.4
///     ↓  threshold = min(stage2_time s, stage2_kills bots dead)
///   Stage 2  escalation — m249/negev, infinite ammo, perfect aim,
///                          slowmo 0.3 for 2s on human death
///     ↓  trigger = 0 living humans
///   Stage 3  HELL MODE — instant bot respawn for <see cref="Stage3MaxDurationSec"/>s
///     ↓  TickStage3 timer expiry
///   <see cref="EndReveal"/>  (wraps <see cref="CleanupReveal"/> + optional mp_restartgame 1)
///     ↓
///   Idle
///
///   Stage 1/2/3 ──!reveal_apocalypse──→ Stage 4 (manual transition only)
///                                         ↓ all carriers detonated OR Stage4MaxDurationSec
///                                       EndReveal
///
/// Stage 4 (APOCALYPSE, v0.7.0-beta) is a manual-only branch: it cannot be
/// entered automatically from the linear 0→3 progression — only via
/// <c>!reveal_apocalypse</c> / <c>replica_reveal_apocalypse</c> while a
/// Stage 1/2/3 reveal is already active. Design contract:
///   - <c>BotDamagePatch</c> stays on (carriers must remain killable so
///     humans can pre-emptively pop them before detonation).
///     Per-carrier incoming damage is halved (issue #26) so AWP/HE
///     counter-pressure can't OHKO a carrier before its timer fires.
///   - 1 in <see cref="Stage4CarrierFraction"/> bots is promoted to a C4
///     carrier on Stage 4 entry (skips dead/invalid slots — see #24).
///   - Each carrier's detonator arms when it sights a human within
///     <see cref="Stage4VisionRangeHU"/> (~30m), then ticks down through
///     <see cref="Stage4DetonateDelayMinTicks"/>..<see cref="Stage4DetonateDelayMaxTicks"/>
///     with a beep cadence interpolating from
///     <see cref="Stage4BeepIntervalEarlyTicks"/> to
///     <see cref="Stage4BeepIntervalLateTicks"/>.
///   - On detonation an <c>env_explosion</c> spawns at the carrier's
///     position (magnitude <see cref="Stage4ExplosionMagnitude"/>, radius
///     <see cref="Stage4ExplosionRadius"/>).
///   - <see cref="Stage4MaxDurationSec"/> is a hard ceiling — EndReveal
///     fires regardless of remaining carriers.
///
/// Advisor-flagged risks (not yet probed empirically):
/// 1. Sync impulse via <c>AbsVelocity.Z=300</c>: bot AI may write velocity
///    every tick, overwriting our impulse. Fallback: drop sync, rely on
///    chat spam alone for Stage 0 punch.
/// 2. Perfect aim via <c>EyeAngles</c> write: tick-ordering vs bot AI's
///    own aim writes is unverified. May need to use post-bot-AI hook
///    (Listeners.OnTick fires once per tick — ordering relative to AI tbd).
/// 3. Stage 1 knife-only is leaky: bots pick up dropped guns. Tick guard
///    re-strips if active weapon != knife.
/// </summary>
public sealed partial class RevealController
{
    public enum RevealStage { Idle, Stage0, Stage1, Stage2, Stage3, Stage4 }

    /// <summary>
    /// Cooldown between same-slot respawns in HELL MODE. Without it,
    /// EventPlayerDeath could fire repeatedly on a single death and
    /// schedule N respawns. 1 sec is plenty.
    /// </summary>
    private const int Stage3RespawnCooldownTicks = 64;
    private readonly Dictionary<int, int> _lastRespawnTick = new();

    public RevealStage Stage { get; private set; } = RevealStage.Idle;

    private readonly FakeClientManager _mgr;
    private int _stageStartTick;
    private int _botsKilledThisReveal;
    private int _humansAtStart;          // for Stage 3 detection
    private bool _stage2Triggered;
    private readonly Random _rng = new();

    /// <summary>
    /// Monotonic counter incremented at every CleanupReveal. Reveal-scoped
    /// delayed callbacks (chat-spam, stage transitions, post-restartgame
    /// work, HELL MODE respawns) capture this on scheduling and bail out
    /// when it changes — prevents stale timers from a previous reveal
    /// firing into a new reveal cycle. See <see cref="ScheduleStageWork"/>.
    /// Issue #4.
    /// </summary>
    private int _revealGen;

    /// <summary>
    /// Consecutive ticks where LivingHumansCount() returned 0 during
    /// Stage 1+2. Stage 3 only fires after this exceeds
    /// <see cref="ZeroHumansDampenTicks"/> — rides out respawn flicker
    /// (mp_restartgame briefly leaves the human's pawn in transient
    /// LifeState != 0 state, which would false-trigger Stage 3).
    /// </summary>
    private int _zeroHumansTickCount;
    private const int ZeroHumansDampenTicks = 64;  // 1 sec @ 64 Hz

    /// <summary>Bots whose loadout we've forced — used for Stage 1 weapon-lock + Stage 3 restore.</summary>
    private readonly Dictionary<int, BotCombatState> _combatState = new();

    /// <summary>
    /// Pre-reveal team membership per bot slot. Captured at Stage 1
    /// entry BEFORE any team flip; restored at CleanupReveal so the
    /// fleet returns to whatever T/CT distribution it had before reveal.
    /// Bots that get bounced to spectator due to team-cap overflow also
    /// have their original team recorded here for clean restore.
    /// </summary>
    private readonly Dictionary<int, int> _botPrevTeams = new();

    /// <summary>
    /// Slot → desired team during reveal. Populated at FlipTeamsWithCap;
    /// TickStage1 + TickStage2 re-issue SwitchTeam if a bot drifted off
    /// (engine queue dropped the call, respawn re-aligned to old team,
    /// etc.). Cleared at CleanupReveal. Without this, ~2 of 8 bots stay
    /// on the wrong team after Stage 1 entry (v0.6.0.8 playtest evidence:
    /// 2 bots stuck on T with the human, materializing as a "mummy" at
    /// T-spawn because mp_solid_teammates=0 lets them clip).
    /// </summary>
    private readonly Dictionary<int, int> _botTargetTeams = new();

    /// <summary>
    /// Pre-reveal value of <c>mp_teammates_are_enemies</c> — captured
    /// at Stage 1 entry, restored exactly at CleanupReveal. v0.6.0.1
    /// forced this to 1, but the side-effect (bots target each other,
    /// fleet self-mulches in 30s) made it unworkable. v0.6.0.2 reverts
    /// to natural CT-vs-T combat — see Stage 1 doc for the design.
    /// </summary>
    private bool? _prevTeammatesAreEnemies;

    /// <summary>
    /// Pre-reveal value of <c>mp_solid_teammates</c>. Forced to 0 at
    /// Stage 1 entry so the bot swarm can pile through each other
    /// without collision (otherwise teleport-on-top-of-human stacks
    /// and ejects bots in random directions). Restored exactly at
    /// CleanupReveal.
    /// </summary>
    private bool? _prevSolidTeammates;

    private struct BotCombatState
    {
        public string ForcedWeapon;       // e.g. "weapon_knife", "weapon_m249"
        public RevealStage StageWhenSet;
    }

    public RevealController(FakeClientManager mgr) => _mgr = mgr;

    /// <summary>
    /// Called from <see cref="FakeClientManager.OnMapStart"/> at the start
    /// of every map transition. Resets reveal state to Idle without issuing
    /// any <c>Server.ExecuteCommand</c> — cvars at this point reflect the
    /// new map's cfg reload, not the pre-reveal captured values, so
    /// restoring captured cvars here would clobber the new map's defaults.
    ///
    /// Without this, mid-reveal mapchange left <c>_botPrevTeams</c>,
    /// <c>_botTargetTeams</c>, <c>_combatState</c>, <c>_lastRespawnTick</c>,
    /// <c>_apocalypseCarriers</c> keyed on slot indices the engine may
    /// rebind to real humans on the new map — causing TickStageN to
    /// SwitchTeam / Respawn / strip+knife real players, and leaving
    /// captured cvars dangling to clobber the new map's defaults on the
    /// next CleanupReveal. Issue #5.
    /// </summary>
    public void OnMapStart()
    {
        if (Stage != RevealStage.Idle)
        {
            _mgr.Telemetry.Write("reveal_aborted_mapchange", new Dictionary<string, object?> {
                { "stage", Stage.ToString() },
                { "humansAtStart", _humansAtStart },
                { "botsKilled", _botsKilledThisReveal },
                { "carriers", _apocalypseCarriers.Count },
            });
            Log.Info($"Reveal: mapchange detected mid-{Stage} — wiping state, no cvar restore (engine reloaded cfg)");
        }

        Stage = RevealStage.Idle;
        _stageStartTick = 0;
        _botsKilledThisReveal = 0;
        _humansAtStart = 0;
        _stage2Triggered = false;
        _zeroHumansTickCount = 0;

        _botPrevTeams.Clear();
        _botTargetTeams.Clear();
        _combatState.Clear();
        _lastRespawnTick.Clear();
        _apocalypseCarriers.Clear();
        _stage4LastVisionTick = 0;

        // Drop captured cvar values without writing them back. The engine
        // resets cvars to new-map gamemode_*.cfg defaults at the transition;
        // attempting to restore the pre-mapchange values here would silently
        // override the operator's intended map config.
        _prevTeammatesAreEnemies = null;
        _prevSolidTeammates = null;

        // Uninstall Stage 4 damage filter if it was active. Fresh map starts
        // with no carrier-immune state.
        try {
            if (_mgr.DamagePatch.IsInstalled) _mgr.DamagePatch.Uninstall();
        } catch (Exception ex) {
            Log.Debug($"OnMapStart DamagePatch uninstall: {ex.Message}");
        }
    }

    /// <summary>Admin entry. !reveal or replica_reveal lands here.</summary>
    public void Start()
    {
        if (Stage != RevealStage.Idle)
        {
            Log.Info($"Reveal restart requested mid-reveal (was {Stage}) — running CleanupReveal first");
            CleanupReveal();
        }

        // v0.6.0.11 fix: if fleet is drained (override=0 from bot_kick),
        // auto-restore + schedule reveal to retry in 10 sec. Without this,
        // user does `bot_kick` then `!reveal` and reveal silently aborts
        // because Stage 0 sees 0 bots. Confusing UX.
        if (_mgr.All.Count == 0 && _mgr.PendingPersonaCount == 0)
        {
            Log.Info("Reveal: fleet empty — auto-restoring (clearing override) and retrying in 10s");
            _mgr.Config.SetFleetSizeOverride(null);
            Server.PrintToChatAll($" {ChatColors.DarkRed}[REPLICÁ] fleet empty — restoring, retrying reveal in 10s");
            ScheduleStageWork(64 * 10, () => {
                if (Stage == RevealStage.Idle && _mgr.All.Count > 0)
                    EnterStage0();
                else
                    Log.Warn($"Reveal retry: stage={Stage} bots={_mgr.All.Count} — abort");
            });
            return;
        }

        EnterStage0();
    }

    private List<CCSPlayerController> LivingHumanControllers()
    {
        var list = new List<CCSPlayerController>();
        foreach (var c in Utilities.GetPlayers()) {
            if (c == null || !c.IsValid || c.IsHLTV) continue;
            if (_mgr.FindBySlot((int)c.Slot) != null) continue;  // managed bot
            // ZOMBIE FILTER (v0.6.0.8): engine clients lingering from prior
            // reveal cycles or mp_restartgame respawn churn show up in
            // GetPlayers() with no Steam authorization. Exclude them so
            // FlipTeamsWithCap doesn't burn target-team cap on phantoms,
            // and so humansAtStart actually counts real players.
            // Real humans always have AuthorizedSteamID after spawn.
            if (c.AuthorizedSteamID == null) continue;
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;
            if (pawn.LifeState != 0) continue;
            list.Add(c);
        }
        return list;
    }

    private static Vector ComputeCentroid(List<CCSPlayerController> humans)
    {
        float sx = 0, sy = 0, sz = 0;
        int n = 0;
        foreach (var h in humans) {
            var p = h.PlayerPawn?.Value;
            if (p?.AbsOrigin == null) continue;
            sx += p.AbsOrigin.X; sy += p.AbsOrigin.Y; sz += p.AbsOrigin.Z;
            n++;
        }
        return n == 0 ? new Vector(0, 0, 0) : new Vector(sx / n, sy / n, sz / n);
    }

    private void EndReveal()
    {
        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "End" }, { "totalKills", _botsKilledThisReveal } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[REPLICÁ] reveal complete");
        CleanupReveal();

        if (_mgr.Config.RevealAutoRestart) {
            // mp_restartgame 1 revives killed humans for next !reveal.
            // 2-tick delay so chat message renders before round flash.
            // Gen-scoped: if user re-triggers !reveal in the 2-tick gap,
            // the autorestart skips so it doesn't mp_restartgame the new
            // reveal's Stage 0.
            ScheduleStageWork(2,
                () => Server.ExecuteCommand("mp_restartgame 1"));
        }
    }

    /// <summary>
    /// Roll back ALL per-stage live overrides — host_timescale, weapon
    /// locks, speed multipliers, combat state. Idempotent. Called:
    /// 1. As FIRST step of Start() if reveal was already active;
    /// 2. From EnterStage3() on natural end.
    /// </summary>
    public void CleanupReveal()
    {
        // Bump generation FIRST — any scheduled callback queued under the
        // outgoing generation will see _revealGen != captured-gen and
        // bail out. See ScheduleStageWork. Issue #4.
        _revealGen++;
        try {
            Server.ExecuteCommand("host_timescale 1.0");
            // Restore captured cvars. null = never captured (Stage 0 or
            // Idle re-trigger before ever reaching Stage 1) — leave cvar
            // alone.
            if (_prevTeammatesAreEnemies.HasValue) {
                Server.ExecuteCommand($"mp_teammates_are_enemies {(_prevTeammatesAreEnemies.Value ? 1 : 0)}");
                _prevTeammatesAreEnemies = null;
            }
            if (_prevSolidTeammates.HasValue) {
                Server.ExecuteCommand($"mp_solid_teammates {(_prevSolidTeammates.Value ? 1 : 0)}");
                _prevSolidTeammates = null;
            }

            // Restore each bot to its pre-reveal team. Bots that were in
            // spectator at reveal entry stay there (we didn't capture
            // them in _botPrevTeams). Bots flipped to spectator due to
            // team-cap overflow re-join their original team here.
            foreach (var (slot, prevTeam) in _botPrevTeams) {
                try {
                    // Slot-ownership guard (#18). Bot may have disconnected
                    // mid-reveal; a human on the freed slot would otherwise
                    // be force-switched to the bot's pre-reveal team here.
                    if (_mgr.FindBySlot(slot) == null) continue;
                    var c = Utilities.GetPlayerFromSlot(slot);
                    if (c == null || !c.IsValid) continue;
                    if (c.AuthorizedSteamID != null) continue;  // human moved in
                    c.SwitchTeam((CsTeam)prevTeam);
                } catch (Exception ex) { Log.Debug($"Restore team slot={slot}: {ex.Message}"); }
            }
            _botPrevTeams.Clear();
            _botTargetTeams.Clear();  // stop tick-level enforcement

            foreach (var fc in _mgr.All) RestoreNormalLoadout(fc);
            _combatState.Clear();

            // Stage 4 cleanup: uninstall the damage filter (was installed
            // at Stage 4 entry to make the swarm immune to its own
            // explosions), clear carrier state. Spawned env_explosion
            // entities self-clean after Explode input fires; weapon_c4
            // give-effects clear at the next mp_restartgame which the
            // RevealAutoRestart path issues a few lines below this method.
            _mgr.DamagePatch.CarrierPredicate = null;
            if (_mgr.DamagePatch.IsInstalled)
            {
                _mgr.DamagePatch.Uninstall();
            }
            _apocalypseCarriers.Clear();
            _stage4LastVisionTick = 0;
        } catch (Exception ex) { Log.Error($"CleanupReveal: {ex.Message}"); }
        Stage = RevealStage.Idle;
        // Telemetry MUST come before the counter reset below: post-reveal
        // analysis reads totalKills / _zeroHumansTickCount / etc. from
        // this event. Do not invert this ordering during cleanup-of-the-
        // cleanup refactors.
        _mgr.Telemetry.Write("reveal_cleanup", new Dictionary<string, object?> {
            { "totalKills", _botsKilledThisReveal } });
        _botsKilledThisReveal = 0;

        // Reset per-reveal counters that previously carried over to the
        // next Start() and could fire a false-positive EndReveal in the
        // first tick of a re-triggered reveal. Specifically:
        //   _zeroHumansTickCount: if the previous reveal ended on the
        //     Stage 3 timer (not on zero-humans), this could still be
        //     just below the 64-tick threshold; the next Stage 1 entry's
        //     mp_restartgame respawn flicker would push it past the
        //     threshold and EndReveal within the first second.
        //   _humansAtStart / _stage2Triggered / _lastRespawnTick: EnterStage0
        //     is the authoritative writer for _humansAtStart and
        //     _stage2Triggered; EnterStage3 clears _lastRespawnTick — but
        //     only on the happy path. Reset here so an aborted reveal that
        //     never reached the resetting stage still leaves us in a known-
        //     clean state. The _humansAtStart=0 assignment is technically
        //     redundant for the normal Start()→EnterStage0 path (which
        //     overwrites it) but load-bearing for any abort path that
        //     calls CleanupReveal without going through EnterStage0 next.
        _zeroHumansTickCount = 0;
        _humansAtStart = 0;
        _stage2Triggered = false;
        _lastRespawnTick.Clear();
    }

    /// <summary>
    /// Schedule a reveal-scoped delayed action. The callback no-ops if
    /// CleanupReveal ran between scheduling and firing — protects
    /// against stale callbacks from a prior reveal leaking into a new
    /// one (Stage transitions, post-restartgame setup, HELL MODE
    /// respawns, chat-spam, slowmo restore, autorestart, etc.).
    /// Use this wrapper everywhere a reveal-scoped delayed action goes
    /// through <see cref="Server.RunOnTick"/>. Issue #4.
    /// </summary>
    private void ScheduleStageWork(int delayTicks, Action work)
    {
        int gen = _revealGen;
        Server.RunOnTick(Server.TickCount + delayTicks, () => {
            if (gen != _revealGen) return;  // stale: cleanup ran since scheduling
            work();
        });
    }

    private void RestoreNormalLoadout(FakeClient fc)
    {
        try {
            var c = Utilities.GetPlayerFromSlot(fc.Slot);
            if (c == null || !c.IsValid) return;
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) return;

            StripAllWeapons(c);
            // Vanilla baseline: a pistol + rifle for the team. Bot AI
            // will pick up dropped weapons if available; this just gives
            // them something to work with.
            c.GiveNamedItem("weapon_glock");

            // Reset speed multiplier.
            SchemaSafety.WriteAndMark<float>(pawn, pawn.Handle, "CCSPlayerPawn",
                "m_flVelocityModifier", 1.0f);
        } catch (Exception ex) { Log.Error($"RestoreNormalLoadout slot={fc.Slot}: {ex.Message}"); }
    }

    // ──────────────────────────────────────────────────────────────────
    // Tick logic — drives stage transitions + per-tick overrides
    // ──────────────────────────────────────────────────────────────────
    public void OnTick()
    {
        if (Stage == RevealStage.Idle) return;

        // Abort if the fleet drained mid-stage (e.g. external bot_kick
        // after Stage 1 entered). The EnterStageN abort guards catch the
        // entry-time empty case; this catches drain between stages or
        // during a long-running stage. Cheaper than letting Stage N run
        // its full timer (60-90s each) on no bots.
        if (Stage != RevealStage.Stage0 && _mgr.All.Count == 0) {
            Log.Info("Reveal aborted: fleet drained mid-stage");
            EndReveal();
            return;
        }

        switch (Stage)
        {
            case RevealStage.Stage1: TickStage1(); break;
            case RevealStage.Stage2: TickStage2(); break;
            case RevealStage.Stage3: TickStage3(); break;
            case RevealStage.Stage4: TickStage4(); break;
        }

        // Early-end trigger: 0 living humans sustained for ≥1 sec.
        // Dampening because mp_restartgame at Stage 1/2 entry briefly
        // puts ALL pawns (including humans) in transient respawn state
        // (LifeState != 0 → LivingHumansCount() == 0 for a few ticks).
        //
        // Was: "0 humans → EnterStage3 (which was cleanup)".
        // Now: "0 humans → EndReveal directly". Skipping HELL MODE makes
        // sense because there's no one to terrorize. HELL MODE Stage 3
        // is reached normally via Stage 2's natural timer transition.
        //
        // Stage 0 is included (issue #7): without it, all humans
        // disconnecting during the 5-second pre-warning would still let
        // Stage 1's swarm-deploy run on an empty server.
        if (Stage == RevealStage.Stage0 || Stage == RevealStage.Stage1
            || Stage == RevealStage.Stage2 || Stage == RevealStage.Stage3
            || Stage == RevealStage.Stage4)
        {
            if (LivingHumansCount() == 0 && _humansAtStart > 0) {
                _zeroHumansTickCount++;
                if (_zeroHumansTickCount >= ZeroHumansDampenTicks)
                    EndReveal();
            } else {
                _zeroHumansTickCount = 0;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Event hooks
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drop all per-slot state when a client (bot or human) disconnects.
    /// Fixes #18: CS2 freely re-uses freed slots; without this purge, the
    /// per-tick enforcers (EnforceTeamMembership / HELL respawn / cvar
    /// restore) would treat whoever lands on the slot next as the old
    /// bot — force-switching humans to the bot's reveal team, scheduling
    /// respawns + m249 hand-outs, etc.
    ///
    /// Called from ReplicaPlugin's OnClientDisconnect listener
    /// AFTER FakeClientManager has processed the disconnect. Safe to call
    /// for any slot; dict removes are no-ops if the slot wasn't tracked.
    /// </summary>
    public void OnClientDisconnect(int slot)
    {
        if (Stage == RevealStage.Idle) return;
        _botTargetTeams.Remove(slot);
        _botPrevTeams.Remove(slot);
        _combatState.Remove(slot);
        _lastRespawnTick.Remove(slot);
        _apocalypseCarriers.Remove(slot);
    }

    public void OnPlayerDeath(int victimSlot, bool victimIsBot)
    {
        if (Stage == RevealStage.Idle) return;

        // FIX (v0.6.0.6): re-derive bot-ness from pool authoritatively
        // instead of trusting caller's flag. The caller (ReplicaPlugin
        // dispatcher) computes `victimIsBot = c.IsBot OR FindBySlot != null`,
        // but `c.IsBot` is FALSE for our bots (we flipped m_bFakePlayer=0)
        // AND FindBySlot can return null in transient states (mid-Despawn,
        // mid-mapchange). When the cached flag is wrong, slow-mo fired on
        // bot deaths. Source-of-truth check: IS the slot in our managed
        // pool right now? If yes → bot. Else → real human.
        bool actuallyManagedBot = _mgr.FindBySlot(victimSlot) != null;

        if (actuallyManagedBot)
        {
            _botsKilledThisReveal++;
            // HELL MODE: respawn dead bots and re-equip. Cooldown 1 sec
            // per slot prevents infinite-loop if EventPlayerDeath fires
            // multiple times for the same death.
            if (Stage == RevealStage.Stage3)
            {
                int now = Server.TickCount;
                if (_lastRespawnTick.TryGetValue(victimSlot, out var last) &&
                    now - last < Stage3RespawnCooldownTicks) return;
                _lastRespawnTick[victimSlot] = now;
                // Schedule respawn ~0.5s after death (camera + ragdoll
                // settles), then re-apply m249 + armor 1 tick later.
                ScheduleStageWork(32, () => {
                    try {
                        // Slot-ownership guard (#18). Between scheduling
                        // and firing this callback (~0.5s), the original
                        // bot may have disconnected and a human taken its
                        // slot — c.Respawn() would force-respawn the human
                        // and the chained ApplyM249Rush would hand them a
                        // m249/negev. Verify the slot still belongs to a
                        // managed bot before either operation.
                        if (_mgr.FindBySlot(victimSlot) == null) return;
                        var c = Utilities.GetPlayerFromSlot(victimSlot);
                        if (c == null || !c.IsValid) return;
                        if (c.AuthorizedSteamID != null) return;  // human moved in
                        c.Respawn();
                        ScheduleStageWork(4, () => {
                            var fc = _mgr.FindBySlot(victimSlot);
                            if (fc != null) ApplyM249Rush(fc);
                        });
                    } catch (Exception ex) { Log.Debug($"hell respawn slot={victimSlot}: {ex.Message}"); }
                });
            }
            return;
        }

        // True human died — slowmo death cam (Stage 2 only).
        if (Stage == RevealStage.Stage2)
        {
            Server.ExecuteCommand("host_timescale 0.3");
            ScheduleStageWork((int)(2 * 64 * 0.3),
                () => Server.ExecuteCommand("host_timescale 1.0"));
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────
    private static void StripAllWeapons(CCSPlayerController c)
    {
        var pawn = c.PlayerPawn?.Value;
        var weapons = pawn?.WeaponServices?.MyWeapons;
        if (weapons == null) return;
        // Iterate copy — RemoveItemByDesignerName mutates the collection.
        var names = weapons
            .Where(h => h.Value != null)
            .Select(h => h.Value!.DesignerName)
            .ToList();
        foreach (var name in names)
        {
            try { c.RemoveItemByDesignerName(name); } catch { }
        }
    }

    /// <summary>
    /// Count living humans on the server. Two-stage filter:
    ///   (1) <see cref="FakeClientManager.FindBySlot"/> != null  — drop
    ///       OUR managed bots (m_bFakePlayer=0 + synthetic Steam ID hides
    ///       them from c.IsBot/AuthorizedSteamID).
    ///   (2) AuthorizedSteamID == null — drop ZOMBIE engine clients
    ///       (v0.6.0.8 hardening): lingering CServerSideClient from prior
    ///       reveal cycles / mp_restartgame churn that aren't connected
    ///       enough to have Steam auth. Real humans always have it.
    /// Combination is correct because step (1) runs first — so step (2)
    /// only filters non-managed clients, where AuthorizedSteamID is the
    /// canonical "real human" signal.
    /// </summary>
    private int LivingHumansCount()
    {
        int n = 0;
        foreach (var c in Utilities.GetPlayers())
        {
            if (c == null || !c.IsValid || c.IsHLTV) continue;
            if (_mgr.FindBySlot((int)c.Slot) != null) continue;  // managed bot
            if (c.AuthorizedSteamID == null) continue;            // zombie/unauth
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;
            if (pawn.LifeState != 0) continue;  // 0 = LIFE_ALIVE
            n++;
        }
        return n;
    }

    /// <summary>
    /// Broadcast a chat line that appears as if the bot itself typed it
    /// (real chat — team-color name + message, NOT server-prefixed).
    /// Uses the SayText2 user message protobuf with the bot's controller
    /// entity index as sender, so receiving clients render it identically
    /// to a human player saying the line.
    ///
    /// Falls back to <see cref="Server.PrintToChatAll"/> if UserMessage
    /// construction fails (proto field name drift across CSSharp versions
    /// is a known risk).
    /// </summary>
    private static void SayAsBot(FakeClient fc, string text)
    {
        CCSPlayerController? c = null;
        try { c = Utilities.GetPlayerFromSlot(fc.Slot); } catch { }
        if (c == null || !c.IsValid) return;

        try {
            var um = UserMessage.FromPartialName("CMsgSayText2");
            // Most CS2 chat plugins target all connected players for
            // global chat. Recipients API exposes AddAllPlayers().
            um.Recipients.AddAllPlayers();
            um.SetInt("entityindex", (int)c.Index);
            um.SetBool("chat", true);
            // messagename = format string. CS2's Cstrike_Chat_All uses
            // \x01 reset color, \x09 team color, etc. Inline format:
            // "{name} :  {text}" with name colored to bot's team.
            um.SetString("messagename",
                $"\x01\x09{fc.Name}\x01 :  {text}");
            um.SetString("param1", "");
            um.SetString("param2", "");
            um.SetString("param3", "");
            um.SetString("param4", "");
            um.Send();
            return;
        } catch (Exception ex) {
            Log.Debug($"SayAsBot UserMessage: {ex.Message}; falling back to PrintToChatAll");
        }
        try {
            Server.PrintToChatAll($" {fc.Name}{ChatColors.Default} : {text}");
        } catch { }
    }
}
