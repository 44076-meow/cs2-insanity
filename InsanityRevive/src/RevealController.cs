using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;

namespace InsanityRevive;

/// <summary>
/// Reveal Finale state machine (P/12, v0.6.0-beta).
///
/// Triggered by `!reveal` (admin chat) or `insanity_reveal` (rcon). NO
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
///   Stage 3  cleanup — CleanupReveal + mp_restartgame 1 (if config'd)
///     ↓
///   Idle
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
public sealed class RevealController
{
    public enum RevealStage { Idle, Stage0, Stage1, Stage2, Stage3, Stage4 }

    /// <summary>
    /// Stage 3 (HELL MODE) max duration before auto-cleanup. Bots respawn
    /// instantly when killed during this stage — without a cap, and with
    /// no humans to kill them, hell mode would loop forever.
    /// </summary>
    private const int Stage3MaxDurationSec = 30;

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
    /// CS2 competitive 5v5 team cap. Per-team max, including humans.
    /// Hardcoded for v0.6.0.5 — if `+game_alias` ever changes from
    /// competitive to deathmatch/etc, revisit. Bots beyond this cap
    /// get sent to spectator at Stage 1 entry; they re-join their
    /// original team at CleanupReveal.
    /// </summary>
    private const int TeamCap = 5;

    /// <summary>
    /// Stage 1 minimum duration. Hard gate before Stage 2 can fire,
    /// regardless of how many bots have died. Prevents pathologically
    /// short Stage 1 (e.g. 4 bots die in 10 sec → Stage 2 immediately,
    /// felt rushed). User-spec v0.6.0.5: 30 sec minimum.
    /// </summary>
    private const int Stage1MinDurationSec = 30;

    /// <summary>
    /// Stage 1 maximum duration. Hard cap; Stage 2 fires regardless of
    /// kill count once this elapses. Prevents Stage 1 dragging out if
    /// bots can't reach humans (open map, human in safe spot).
    /// </summary>
    private const int Stage1MaxDurationSec = 60;

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

    public RevealController(FakeClientManager mgr) => _mgr = mgr;

    /// <summary>Admin entry. !reveal or insanity_reveal lands here.</summary>
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
            Server.PrintToChatAll($" {ChatColors.DarkRed}[INSANITY] fleet empty — restoring, retrying reveal in 10s");
            Server.RunOnTick(Server.TickCount + 64 * 10, () => {
                if (Stage == RevealStage.Idle && _mgr.All.Count > 0)
                    EnterStage0();
                else
                    Log.Warn($"Reveal retry: stage={Stage} bots={_mgr.All.Count} — abort");
            });
            return;
        }

        EnterStage0();
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 0 — pre-warning
    // ──────────────────────────────────────────────────────────────────
    private void EnterStage0()
    {
        Stage = RevealStage.Stage0;
        _stageStartTick = Server.TickCount;
        _botsKilledThisReveal = 0;
        _stage2Triggered = false;
        _humansAtStart = LivingHumansCount();

        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage0" }, { "humansAtStart", _humansAtStart },
            { "fleetSize", _mgr.All.Count } });

        var bots = _mgr.All.ToList();
        if (bots.Count == 0)
        {
            Log.Warn("Reveal Stage 0: no bots in fleet — aborting");
            CleanupReveal();
            return;
        }

        // Each bot fires 15 "1" lines with INDEPENDENT random delay
        // 0.2-0.5 sec between consecutive messages. Per-bot total span
        // ≈ 3-7.5 sec; across 8 bots staggering naturally → ~120 chat
        // events spread over 5-8 seconds (overflows Stage 0 into early
        // Stage 1, which is intentional — the chat-flood masks the
        // moment of swarm-teleport so it feels like the chaos started
        // mid-sentence). Each message via UserMessage SayText2 broadcast.
        const int SpamMessagesPerBot = 15;
        foreach (var fc in _mgr.All)
        {
            var capturedFc = fc;
            int cumulativeTicks = 0;
            for (int j = 0; j < SpamMessagesPerBot; j++)
            {
                int delayTicks = cumulativeTicks;
                Server.RunOnTick(Server.TickCount + delayTicks,
                    () => SayAsBot(capturedFc, "1"));
                // Roll next delay 0.2-0.5 sec uniform.
                double sec = 0.2 + _rng.NextDouble() * 0.3;
                cumulativeTicks += (int)(sec * 64);
            }
        }

        // Sync jump impulse DROPPED in v0.6.0-beta. m_vecAbsVelocity is
        // a CBaseEntity field that CSSharp warns isn't networked — even
        // if the write succeeded server-side, clients wouldn't see the
        // visible jump. Without a working sync-jump effect, the bot AI
        // would just pop up imperceptibly. Chat spam alone signals weirdness
        // for Stage 0 (advisor's recommended fallback).
        // Reinstate in v0.6.1 once a clientside-visible-jump path is found
        // (candidates: write IN_JUMP via CCSPlayer_MovementServices schema,
        // teleport up via m_vecOrigin, or trigger via console alias).

        // At t=5s: enter Stage 1.
        Server.RunOnTick(Server.TickCount + 5 * 64, () => {
            if (Stage == RevealStage.Stage0) EnterStage1();
        });
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 1 — knife rush (swarm)
    // ──────────────────────────────────────────────────────────────────
    //
    // Design (v0.6.0.2-beta): natural CT-vs-T combat — all bots flipped
    // to the opposite team of the (first) living human, then teleported
    // to a tight cluster around the human centroid. Bot AI sees only
    // humans as enemies (default mp_teammates_are_enemies=0), so they
    // converge on humans without internal fighting. mp_solid_teammates=0
    // lets the swarm pile through each other without colliding.
    //
    // Sequence:
    //   1. Capture cvars (mp_teammates_are_enemies, mp_solid_teammates).
    //   2. Force defaults (=0) and mp_solid_teammates=0.
    //   3. Pre-flip all bots to opposite team via SwitchTeam().
    //   4. mp_restartgame 1 — clean round, all respawn at spawn points
    //      with new team affiliation honored.
    //   5. After 5-tick-second settle: detect human centroid, teleport
    //      bots to (centroid.X+offset, centroid.Y+offset, centroid.Z),
    //      then strip+knife+speed-boost.
    //
    // Edge case: zero living humans at swarm time → skip teleport, run
    // knife rush in default spawn positions (bots wander).
    private void EnterStage1()
    {
        Stage = RevealStage.Stage1;
        _stageStartTick = Server.TickCount;

        // (1) Capture cvars to restore at CleanupReveal.
        try {
            _prevTeammatesAreEnemies = ConVar.Find("mp_teammates_are_enemies")?.GetPrimitiveValue<bool>() ?? false;
        } catch { _prevTeammatesAreEnemies = false; }
        try {
            _prevSolidTeammates = ConVar.Find("mp_solid_teammates")?.GetPrimitiveValue<bool>() ?? true;
        } catch { _prevSolidTeammates = true; }

        // (2) Force cvars: default targeting (bots respect team), no
        //     teammate collision (swarm pile-up).
        Server.ExecuteCommand("mp_teammates_are_enemies 0");
        Server.ExecuteCommand("mp_solid_teammates 0");

        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage1" },
            { "prevTeammatesAreEnemies", _prevTeammatesAreEnemies },
            { "prevSolidTeammates", _prevSolidTeammates } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[INSANITY] reveal initiated");

        // (3) Determine bot target team = opposite of human's team. If
        //     no humans, default to T (2) — bots will be on T, no humans
        //     to fight, knife-rush plays out as wandering chaos.
        var humans = LivingHumanControllers();
        int humanTeam = humans.Count > 0 ? (int)humans[0].TeamNum : 3;  // default human=CT
        int botTeam = humanTeam == 2 ? 3 : 2;  // opposite

        // (4) Capture each bot's PRE-REVEAL team. Don't SwitchTeam yet —
        //     v0.6.0.6 fix: SwitchTeam BEFORE mp_restartgame raced with the
        //     restart's own team-rebalance logic, leaving ~half the bots on
        //     their original team and the rest in spectator-but-mid-respawn
        //     limbo. New flow: capture teams now, mp_restartgame, then 1
        //     sec later do team flip in a clean round state.
        _botPrevTeams.Clear();
        foreach (var fc in _mgr.All) {
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                int prevTeam = (int)c.TeamNum;
                if (prevTeam >= 2) _botPrevTeams[fc.Slot] = prevTeam;
            } catch (Exception ex) { Log.Debug($"capture team slot={fc.Slot}: {ex.Message}"); }
        }

        // (5) Clean round restart for fresh respawn state.
        Server.ExecuteCommand("mp_restartgame 1");

        // (6) +1.5s after restart: do team flip with cap awareness and
        //     belt-and-suspenders schema-write fallback.
        Server.RunOnTick(Server.TickCount + (int)(64 * 1.5), () => {
            if (Stage != RevealStage.Stage1) return;
            FlipTeamsWithCap(botTeam);
        });

        // (7) +5s after restart: teleport swarm to human centroid, then
        //     apply knife rush. Allows team flip to settle (3.5s buffer
        //     after FlipTeams).
        Server.RunOnTick(Server.TickCount + 64 * 5, () => {
            if (Stage != RevealStage.Stage1) return;
            DeploySwarmAndKnifeRush();
        });
    }

    /// <summary>
    /// Move all bots to <paramref name="botTeam"/> until cap is reached,
    /// rest to spectator.
    ///
    /// HISTORY: v0.6.0.6 attempted a Schema.SetSchemaValue&lt;byte&gt; +
    /// SetStateChanged fallback for m_iTeamNum when SwitchTeam appeared to
    /// fail verification. CSSharp warned "Field CCSPlayerController:
    /// m_iTeamNum is not networked, but SetStateChanged was called on it"
    /// and the server CRASHED on the next tick (dump 21:45:24). m_iTeamNum
    /// IS server-state but writing it via the schema bypass path corrupts
    /// engine team-counter accounting. Reverted to plain SwitchTeam +
    /// log-only verification for diagnostics. If a switch fails, accept
    /// it — better an unflipped bot than a crashed server.
    /// </summary>
    private void FlipTeamsWithCap(int botTeam)
    {
        var humans = LivingHumanControllers();
        int humansOnTargetTeam = humans.Count(h => (int)h.TeamNum == botTeam);
        int availableSlots = Math.Max(0, TeamCap - humansOnTargetTeam);
        int sentToTarget = 0;
        int leftOnPrev = 0;
        int verifyMismatch = 0;
        _botTargetTeams.Clear();
        foreach (var fc in _mgr.All) {
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;

                if (sentToTarget < availableSlots)
                {
                    // Within cap — flip to opposite team.
                    _botTargetTeams[fc.Slot] = botTeam;
                    c.SwitchTeam((CsTeam)botTeam);
                    if ((int)c.TeamNum != botTeam) verifyMismatch++;
                    sentToTarget++;
                }
                else
                {
                    // Cap hit — LEAVE bot on its prior team. v0.6.0.11
                    // fix: previous code did SwitchTeam(Spectator) which
                    // engine rejected with "CCSPlayerPawnBase::SwitchTeam(1)
                    // - invalid team index" log spam (not crashy but
                    // floods server.log). Side effect: cap-overflow bots
                    // remain on user's team and may not attack — known
                    // limitation, accept until cap-bypass mechanism found.
                    leftOnPrev++;
                }
            } catch (Exception ex) { Log.Debug($"FlipTeams slot={fc.Slot}: {ex.Message}"); }
        }
        Log.Info($"Stage 1 team flip: {sentToTarget} → team {botTeam}, {leftOnPrev} left on prev " +
                 $"(cap={TeamCap}, humans on target={humansOnTargetTeam}, " +
                 $"{verifyMismatch} immediate-verify-mismatch — TickStage1 will re-attempt)");
    }

    /// <summary>
    /// Re-issue SwitchTeam for any bot whose current TeamNum doesn't
    /// match its desired-team in <see cref="_botTargetTeams"/>. Catches:
    /// (a) v0.6.0.8 playtest failure where 2 of 8 bots stayed on the
    ///     wrong team, (b) bots that respawn with team-rebalance reset.
    /// Cheap (8 reads + at most 8 writes per call). Called from
    /// TickStage1 (1 Hz) AND TickStage2.
    /// </summary>
    private void EnforceTeamMembership()
    {
        if (_botTargetTeams.Count == 0) return;
        foreach (var (slot, target) in _botTargetTeams) {
            try {
                var c = Utilities.GetPlayerFromSlot(slot);
                if (c == null || !c.IsValid) continue;
                if ((int)c.TeamNum == target) continue;
                c.SwitchTeam((CsTeam)target);
            } catch { }
        }
    }

    /// <summary>
    /// Distance (Hammer Units) from human centroid where the bot
    /// cluster materializes. v0.6.0.3 had 300 HU (~5m world); friend
    /// playtest reported "way too far" — bots wandered for too long
    /// before reaching, lost the surprise. v0.6.0.4 cut to 80 HU
    /// (~1.3m), giving 0.5-1 sec to react. Knife-only design assumes
    /// the human can shoot back before contact; if bots pile too fast
    /// raise to 100-150 HU.
    /// </summary>
    private const float SwarmOffsetDistance = 80f;

    private void DeploySwarmAndKnifeRush()
    {
        var humansNow = LivingHumanControllers();
        Vector? centroid = humansNow.Count > 0 ? ComputeCentroid(humansNow) : null;

        // Pick a single 2D direction biased toward the human's current
        // FACING — random within ±90° of where the human is looking. Z
        // stays at human's centroid Z. Rationale: human's view direction
        // is statistically "open space" (you don't usually stare at a
        // wall 1ft from your face), so clustering somewhere in the half-
        // circle in front of them lands in playable terrain. Spawn-in-
        // wall avoidance without a working TraceRay wrapper in CSSharp
        // 1.0.367. Behind-the-back ambush sacrificed for survivability.
        Vector? clusterOrigin = null;
        if (centroid != null) {
            // Reference yaw: first human's view direction.
            float yawDeg = 0f;
            try {
                var refPawn = humansNow[0].PlayerPawn?.Value;
                if (refPawn != null && refPawn.IsValid)
                    yawDeg = refPawn.EyeAngles.Y;
            } catch { /* fall through with yawDeg = 0 */ }

            // Random offset in [-π/2, +π/2] from forward.
            double yawRad = yawDeg * Math.PI / 180.0;
            double offsetRad = (_rng.NextDouble() - 0.5) * Math.PI;
            double finalRad = yawRad + offsetRad;

            clusterOrigin = new Vector(
                centroid.X + (float)(Math.Cos(finalRad) * SwarmOffsetDistance),
                centroid.Y + (float)(Math.Sin(finalRad) * SwarmOffsetDistance),
                centroid.Z);
        }

        var bots = _mgr.All.ToList();
        for (int i = 0; i < bots.Count; i++) {
            var fc = bots[i];
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                var pawn = c.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid) continue;
                if (pawn.LifeState != 0) continue;  // dead bot — skip swarm-tp

                // Per-bot stagger inside cluster: 4-wide rows, ±1.5 unit
                // spread. With mp_solid_teammates=0 they can occupy the
                // same point, but visual variation reads as "8 bots", not
                // "one bot quintuple-stacked".
                if (clusterOrigin != null) {
                    float dx = (i % 4) - 1.5f;
                    float dy = (i / 4) - 1.0f;
                    var pos = new Vector(
                        clusterOrigin.X + dx,
                        clusterOrigin.Y + dy,
                        clusterOrigin.Z);
                    pawn.Teleport(pos, pawn.AbsRotation, new Vector(0, 0, 0));
                }

                ApplyKnifeRush(fc);
            } catch (Exception ex) { Log.Error($"DeploySwarm slot={fc.Slot}: {ex.Message}"); }
        }
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

    private void ApplyKnifeRush(FakeClient fc)
    {
        try {
            var c = Utilities.GetPlayerFromSlot(fc.Slot);
            if (c == null || !c.IsValid) return;
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) return;

            StripAllWeapons(c);
            c.GiveNamedItem("weapon_knife");

            // Speed boost — v0.6.0.9: 1.4 → 2.0. Playtest evidence:
            // 1.4× was easy to outrun with pistol fire, user wiped 6 bots
            // in 3 sec. 2.0× makes 80 HU close gap in ~0.4s, harder to
            // pre-fire all of them. Tunable; if "too easy" persists, raise
            // to 2.5 or shorten SwarmOffsetDistance.
            SchemaSafety.WriteAndMark<float>(pawn, pawn.Handle, "CCSPlayerPawn",
                "m_flVelocityModifier", 2.0f);

            // ARMOR REVERTED v0.6.0.11: my v0.6.0.9 attempt to write
            // m_ArmorValue + m_bHasHelmet via Schema.SetSchemaValue +
            // SetStateChanged CRASHED the server at 14:53:19 with the
            // same patho as m_iTeamNum and m_angEyeAngles — CSSharp
            // warned "Field CCSPlayerPawn:m_bHasHelmet is not networked,
            // but SetStateChanged was called on it" and crashed on next
            // tick. Despite the comment claim that CSSFixes uses identical
            // writes, my exact path crashes here. Need different approach
            // (typed pawn.ArmorValue setter? or via item give like
            // "item_assaultsuit"?) — TODO for next iteration.
            //
            // For now: knife rush relies on speed boost (2.0) alone +
            // 80 HU short distance to close gap before user fires.

            _combatState[fc.Slot] = new BotCombatState {
                ForcedWeapon = "weapon_knife", StageWhenSet = RevealStage.Stage1 };
        } catch (Exception ex) { Log.Error($"ApplyKnifeRush slot={fc.Slot}: {ex.Message}"); }
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 2 — escalation
    // ──────────────────────────────────────────────────────────────────
    //
    // Design note (v0.6.0-beta): mid-round weapon swap (strip + give m249)
    // crashed the server in initial smoke tests — engine has live
    // references to in-flight weapons that StripAllWeapons invalidates.
    // Workaround: trigger `mp_restartgame 1` at Stage 2 entry (clean round
    // state, all weapons reset), THEN give m249/negev to bots in the
    // fresh round. Side benefit: respawns the human as well, which is
    // narratively appropriate ("the round was over but the bots still
    // showed up with m249s" — feels intentional).
    private void EnterStage2()
    {
        if (_stage2Triggered) return;
        _stage2Triggered = true;
        Stage = RevealStage.Stage2;
        _stageStartTick = Server.TickCount;
        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage2" }, { "kills", _botsKilledThisReveal } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[INSANITY] STAGE 2 — AIM ASSIST ENGAGED");

        // Round restart for clean weapon state. mp_restartgame N takes
        // N seconds to actually fire — wait 4s (1s for restart command +
        // 1s for round restart processing + 2s buffer for respawn frames
        // and CCSPlayerPawn re-init) before giving m249s. Earlier 2-tick
        // delay raced with respawn machinery and crashed the server.
        Server.ExecuteCommand("mp_restartgame 1");
        Server.RunOnTick(Server.TickCount + 64 * 4, () => {
            // Re-check stage in case CleanupReveal raced (re-trigger).
            if (Stage != RevealStage.Stage2) return;
            foreach (var fc in _mgr.All) ApplyM249Rush(fc);
        });
    }

    private void ApplyM249Rush(FakeClient fc)
    {
        try {
            var c = Utilities.GetPlayerFromSlot(fc.Slot);
            if (c == null || !c.IsValid) return;
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) return;
            if (pawn.LifeState != 0) return;  // dead — wait for next round

            // Don't strip — fresh round means bots only have default
            // pistol. Just give the heavy. Engine handles slot management.
            var weapon = _rng.Next(2) == 0 ? "weapon_m249" : "weapon_negev";
            c.GiveNamedItem(weapon);

            _combatState[fc.Slot] = new BotCombatState {
                ForcedWeapon = weapon, StageWhenSet = RevealStage.Stage2 };
        } catch (Exception ex) { Log.Error($"ApplyM249Rush slot={fc.Slot}: {ex.Message}"); }
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 3 — HELL MODE (v0.6.0.10-beta, was previously cleanup-trigger)
    // ──────────────────────────────────────────────────────────────────
    //
    // User playtest after v0.6.0.9 reported: "Стадия 2 закончена (Я ВЫЖИЛ),
    // и якобы reveal complete. Не увидел даже 3 и 4 стадии." The original
    // Stage 3 was just a cleanup-transition pseudo-stage. Renamed flow:
    //   Stage 2 timer/kills → Stage 3 (HELL MODE) → 30s timer → EndReveal
    //
    // HELL MODE behavior: bots that die get instant respawn (cooldown 1
    // sec/slot to prevent EventPlayerDeath loops), re-equipped with m249
    // and armor on respawn. Human can keep killing them but can never
    // deplete the fleet. After 30s the hell ends and CleanupReveal fires.
    //
    // Stage 4 (APOCALYPSE — C4 suicide bots) is a separate auto-chain
    // after Stage 3 — see EnterStage4 below. NOT yet implemented in this
    // version; placeholder enum value reserves the slot.
    private void EnterStage3()
    {
        Stage = RevealStage.Stage3;
        _stageStartTick = Server.TickCount;
        _lastRespawnTick.Clear();
        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage3" }, { "name", "HELL_MODE" },
            { "killsBeforeEntry", _botsKilledThisReveal } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[INSANITY] HELL MODE — RESPAWNS ENABLED");
        // Bots already on m249 from Stage 2; armor stays from Stage 1.
        // Tick3 will reapply both on respawn via re-call to ApplyM249Rush.
    }

    private void TickStage3()
    {
        EnforceTeamMembership();

        var elapsedSec = (Server.TickCount - _stageStartTick) / 64;
        if (elapsedSec >= Stage3MaxDurationSec) {
            EndReveal();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 4 — APOCALYPSE (C4 suicide bots)
    // ──────────────────────────────────────────────────────────────────
    //
    // Manual-only trigger: !reveal_apocalypse / insanity_reveal_apocalypse.
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
        Stage = RevealStage.Stage4;
        _stageStartTick = Server.TickCount;
        _stage4LastVisionTick = 0;
        _apocalypseCarriers.Clear();
        // Fresh sound-resolution cycle — bootstrap probe runs below.
        _stage4WorkingBeep = null;

        Server.PrintToChatAll($" {ChatColors.DarkRed}[INSANITY] APOCALYPSE — C4 RAIN");

        // Install damage filter so the explosions don't fry our own swarm
        // alongside the humans. Idempotent — Install no-ops if already on.
        if (!_mgr.DamagePatch.IsInstalled) _mgr.DamagePatch.Install();

        // Promote 1-of-N bots to C4 carriers + capture probe pawn.
        var bots = _mgr.All.ToList();
        int carrierCount = 0;
        CCSPlayerPawn? probePawn = null;
        for (int i = 0; i < bots.Count; i++)
        {
            if (i % Stage4CarrierFraction != 0) continue;
            var fc = bots[i];
            try
            {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                var pawn = c.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid || pawn.LifeState != 0) continue;

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
                 $"(fraction 1/{Stage4CarrierFraction})");
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
                $" {ChatColors.DarkRed}[INSANITY] {ChatColors.Default}stage4: beep audio unavailable this round — escalating-tension cue muted");
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

    private void EndReveal()
    {
        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "End" }, { "totalKills", _botsKilledThisReveal } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[INSANITY] reveal complete");
        CleanupReveal();

        if (_mgr.Config.RevealAutoRestart) {
            // mp_restartgame 1 revives killed humans for next !reveal.
            // 2-tick delay so chat message renders before round flash.
            Server.RunOnTick(Server.TickCount + 2,
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
                    var c = Utilities.GetPlayerFromSlot(slot);
                    if (c != null && c.IsValid)
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
            if (_mgr.DamagePatch.IsInstalled)
            {
                _mgr.DamagePatch.Uninstall();
            }
            _apocalypseCarriers.Clear();
            _stage4LastVisionTick = 0;
        } catch (Exception ex) { Log.Error($"CleanupReveal: {ex.Message}"); }
        Stage = RevealStage.Idle;
        _mgr.Telemetry.Write("reveal_cleanup", new Dictionary<string, object?> {
            { "totalKills", _botsKilledThisReveal } });
        _botsKilledThisReveal = 0;
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
        if (Stage == RevealStage.Stage1 || Stage == RevealStage.Stage2
            || Stage == RevealStage.Stage3 || Stage == RevealStage.Stage4)
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

    private void TickStage1()
    {
        // CRITICAL ORDERING (v0.6.0.6 fix): knife enforcement runs FIRST,
        // BEFORE any time-based gates. v0.6.0.5 had the 30s min-duration
        // gate as an early-return at the top, which silently disabled
        // knife enforcement during the first 30 seconds of Stage 1.
        EnforceKnifeOnAll();
        // v0.6.0.9 fix: re-issue SwitchTeam for any bot that drifted off
        // its assigned team (queue race, respawn rebalance). Without this,
        // 2 of 8 bots stay on T with the human → "mummy" stack at T-spawn.
        EnforceTeamMembership();

        // Stage 2 trigger logic (v0.6.0.5 user-spec):
        //   - HARD MINIMUM Stage1MinDurationSec.
        //   - After minimum: fire on EITHER 50% bots dead OR 60s timeout.
        var elapsedTicks = Server.TickCount - _stageStartTick;
        var elapsedSec = elapsedTicks / 64;

        if (elapsedSec < Stage1MinDurationSec) return;  // hard gate (transition only)

        var killThreshold = _mgr.Config.Stage2Kills > 0
            ? _mgr.Config.Stage2Kills
            : Math.Max(1, (_mgr.Config.FleetSize + 1) / 2);
        bool killsDone = _botsKilledThisReveal >= killThreshold;
        bool maxReached = elapsedSec >= Stage1MaxDurationSec;
        if (killsDone || maxReached)
        {
            EnterStage2();
            return;
        }
    }

    /// <summary>
    /// Strips and re-equips weapon_knife on every living bot — runs
    /// every Stage 1 tick. Idempotent (no-op if bot already holds knife).
    /// </summary>
    private void EnforceKnifeOnAll()
    {
        // (Old comment block from TickStage1 still applies here:)
        //
        // Continuous knife-only enforcement on ALL living bots — not
        // gated by _combatState membership. Catches:
        //  - bots whose ApplyKnifeRush hasn't run yet (5-sec swarm-deploy
        //    window after mp_restartgame, where defaults could otherwise
        //    show pistols on screen)
        //  - bots that respawned mid-stage and got default loadout
        //  - bots that picked up a dropped weapon from an earlier kill
        // Cost is fleet_size × 64 Hz reads/writes — trivial.
        foreach (var fc in _mgr.All)
        {
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                var pawn = c.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid) continue;
                if (pawn.LifeState != 0) continue;  // dead — wait for next round

                var active = pawn.WeaponServices?.ActiveWeapon?.Value;
                if (active == null) {
                    c.GiveNamedItem("weapon_knife");
                    continue;
                }
                if (active.DesignerName != "weapon_knife")
                {
                    StripAllWeapons(c);
                    c.GiveNamedItem("weapon_knife");
                }
            } catch (Exception ex) { Log.Debug($"TickStage1 enforce slot={fc.Slot}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Stage 2 max duration before auto-cleanup (handles "no humans"
    /// edge case — without humans, the natural Stage-3 trigger never
    /// fires). Generous: lets the m249 spectacle play out.
    /// </summary>
    private const int Stage2MaxDurationSec = 60;

    private void TickStage2()
    {
        // Re-enforce team membership (cheap, prevents drift if Stage 2's
        // mp_restartgame pulled any bots back to their pre-reveal team).
        EnforceTeamMembership();

        // ONLY a timeout check — no per-tick weapon overrides. Earlier
        // versions wrote Clip1/AccuracyPenalty per tick, which raced
        // with mp_restartgame's respawn machinery and crashed the server
        // (smoke run 2026-05-02). Bots have enough ammo from the m249's
        // native 100-round mag + reserve for a 60-second finale.
        var elapsedSec = (Server.TickCount - _stageStartTick) / 64;
        if (elapsedSec >= Stage2MaxDurationSec) EnterStage3();
    }

    // ──────────────────────────────────────────────────────────────────
    // Event hooks
    // ──────────────────────────────────────────────────────────────────
    public void OnPlayerDeath(int victimSlot, bool victimIsBot)
    {
        if (Stage == RevealStage.Idle) return;

        // FIX (v0.6.0.6): re-derive bot-ness from pool authoritatively
        // instead of trusting caller's flag. The caller (InsanityRevivePlugin
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
                Server.RunOnTick(now + 32, () => {
                    try {
                        var c = Utilities.GetPlayerFromSlot(victimSlot);
                        if (c == null || !c.IsValid) return;
                        c.Respawn();
                        Server.RunOnTick(Server.TickCount + 4, () => {
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
            Server.RunOnTick(Server.TickCount + (int)(2 * 64 * 0.3),
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
