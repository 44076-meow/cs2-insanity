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

// Stage 0–3 machinery. State-machine contract: see RevealController.cs.
public sealed partial class RevealController
{
    /// <summary>
    /// Stage 3 (HELL MODE) max duration before auto-cleanup. Bots respawn
    /// instantly when killed during this stage — without a cap, and with
    /// no humans to kill them, hell mode would loop forever.
    /// </summary>
    private const int Stage3MaxDurationSec = 30;

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

        // Entry guard: no humans on server → nothing to terrorize. Abort
        // before any chat-spam or stage-transition timers get scheduled.
        // (The OnTick mid-reveal-abort path below covers the case where
        // humans leave AFTER Stage 0 starts; this catches the case where
        // none were here at trigger time.) Issue #7.
        if (_humansAtStart == 0)
        {
            Log.Warn("Reveal Stage 0: no humans on server — aborting before any setup");
            Server.PrintToChatAll($" {ChatColors.DarkRed}[REPLICÁ] reveal aborted — no humans on server");
            CleanupReveal();
            return;
        }

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
                ScheduleStageWork(delayTicks, () => SayAsBot(capturedFc, "1"));
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
        ScheduleStageWork(5 * 64, () => {
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
        // Mirror EnterStage0's empty-fleet abort. Without this, a fleet
        // drained during Stage 0's 5-sec wait (e.g. external bot_kick)
        // proceeds into the full cvar-mutating + mp_restartgame path on
        // an empty fleet, then grinds through 2-2.5 minutes of sterile
        // Stage 1 → 2 → 3 before the natural timer chain expires —
        // hitting humans with TWO useless mp_restartgame commands and
        // "[REPLICÁ] STAGE 2 / HELL MODE" chat spam with no visible swarm.
        if (_mgr.All.Count == 0) {
            Log.Warn("EnterStage1: fleet empty — aborting reveal (Stage 0→1 raced bot_kick)");
            CleanupReveal();
            return;
        }

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

        Server.PrintToChatAll($" {ChatColors.DarkRed}[REPLICÁ] reveal initiated");

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
        ScheduleStageWork((int)(64 * 1.5), () => {
            if (Stage != RevealStage.Stage1) return;
            FlipTeamsWithCap(botTeam);
        });

        // (7) +5s after restart: teleport swarm to human centroid, then
        //     apply knife rush. Allows team flip to settle (3.5s buffer
        //     after FlipTeams).
        ScheduleStageWork(64 * 5, () => {
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
                // Slot-ownership guard (#18). CS2 freely re-uses freed
                // slots; if a bot disconnected (bot_kick, engine drop)
                // and a human took the slot, EnforceTeamMembership would
                // force-switch the human every tick. Skip slots no longer
                // owned by a managed bot. Belt-and-suspenders: also skip
                // if the slot now belongs to an authorized human (Steam
                // ID present — managed bots have none).
                if (_mgr.FindBySlot(slot) == null) continue;
                var c = Utilities.GetPlayerFromSlot(slot);
                if (c == null || !c.IsValid) continue;
                if (c.AuthorizedSteamID != null) continue;
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
        // Mirror EnterStage0/1 empty-fleet abort — see EnterStage1 comment.
        // Empty here means the swarm vanished mid-Stage-1; an extra
        // mp_restartgame on humans plus the "STAGE 2" chat spam without
        // any visible bots is the worst-case UX.
        if (_mgr.All.Count == 0) {
            Log.Warn("EnterStage2: fleet empty — aborting reveal");
            CleanupReveal();
            return;
        }
        _stage2Triggered = true;
        Stage = RevealStage.Stage2;
        _stageStartTick = Server.TickCount;
        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage2" }, { "kills", _botsKilledThisReveal } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[REPLICÁ] STAGE 2 — AIM ASSIST ENGAGED");

        // Round restart for clean weapon state. mp_restartgame N takes
        // N seconds to actually fire — wait 4s (1s for restart command +
        // 1s for round restart processing + 2s buffer for respawn frames
        // and CCSPlayerPawn re-init) before giving m249s. Earlier 2-tick
        // delay raced with respawn machinery and crashed the server.
        Server.ExecuteCommand("mp_restartgame 1");
        ScheduleStageWork(64 * 4, () => {
            // Re-check stage in case CleanupReveal raced (re-trigger).
            // (Belt-and-suspenders — ScheduleStageWork already bails on
            // generation mismatch, but the stage check guards the case
            // where the same reveal cycle moved past Stage 2 naturally.)
            if (Stage != RevealStage.Stage2) return;
            // Re-sample bot target teams BEFORE m249 hand-out. mp_restartgame
            // can reshuffle players (autobalance, gamemode rules) — the
            // _botTargetTeams snapshot from Stage 1 entry may now point bots
            // onto the same team as the human, which EnforceTeamMembership
            // would then enforce every tick. See issue #13.
            RefreshTargetTeamsAfterRestart();
            foreach (var fc in _mgr.All) ApplyM249Rush(fc);
        });
    }

    /// <summary>
    /// Recompute desired bot team after mp_restartgame settles. Looks at
    /// the post-restart human team and rewrites every existing
    /// <see cref="_botTargetTeams"/> entry to the opposite team. Does NOT
    /// add new entries — Stage 1 already classified which bots are
    /// in-cap (had a target) vs. cap-overflow (left on prev team); we
    /// only correct the direction for those already inside the cap.
    /// No SwitchTeam call — <see cref="EnforceTeamMembership"/> picks
    /// up the new targets on the next tick. Idempotent when humans
    /// stayed on the same team.
    /// </summary>
    private void RefreshTargetTeamsAfterRestart()
    {
        if (_botTargetTeams.Count == 0) return;
        var humans = LivingHumanControllers();
        if (humans.Count == 0) return;  // no humans to anchor against
        int humanTeam = (int)humans[0].TeamNum;
        if (humanTeam != 2 && humanTeam != 3) return;
        int newBotTeam = humanTeam == 2 ? 3 : 2;
        int changed = 0;
        foreach (var slot in _botTargetTeams.Keys.ToList()) {
            if (_botTargetTeams[slot] != newBotTeam) {
                _botTargetTeams[slot] = newBotTeam;
                changed++;
            }
        }
        if (changed > 0)
            Log.Info($"Stage 2 target-team refresh: humanTeam={humanTeam}, " +
                     $"botTeam={newBotTeam}, {changed} entries rewritten " +
                     $"(mp_restartgame shuffle)");
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
        // Mirror EnterStage0-2 empty-fleet abort. HELL MODE on an empty
        // fleet wastes 30s with no respawns to enforce.
        if (_mgr.All.Count == 0) {
            Log.Warn("EnterStage3: fleet empty — aborting reveal");
            CleanupReveal();
            return;
        }

        Stage = RevealStage.Stage3;
        _stageStartTick = Server.TickCount;
        _lastRespawnTick.Clear();
        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage3" }, { "name", "HELL_MODE" },
            { "killsBeforeEntry", _botsKilledThisReveal } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[REPLICÁ] HELL MODE — RESPAWNS ENABLED");
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

                // WeaponServices is null briefly during a respawn / equip
                // transition — wait for next tick instead of spamming
                // GiveNamedItem at 64 Hz, which both churns engine entity
                // creation AND races the engine's own loadout setup.
                // Issue #8.
                var weaponServices = pawn.WeaponServices;
                if (weaponServices == null) continue;

                var active = weaponServices.ActiveWeapon?.Value;
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
}
