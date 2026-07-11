using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;

namespace Replica;

[MinimumApiVersion(220)]
public sealed partial class ReplicaPlugin : BasePlugin
{
    public override string ModuleName    => "Replica";
    public override string ModuleVersion => "v3.0.0-base";
    public override string ModuleAuthor  => "frad70";

    private Telemetry?           _telemetry;
    private Config?              _config;
    private FakeClientManager?   _manager;

    public override void Load(bool hotReload)
    {
        _config = new Config();
        Log.SetLevel(_config.LogLevel);

        _telemetry = new Telemetry(_config.TelemetryPath);
        _manager = new FakeClientManager(this, _config, _telemetry);
        _manager.OnLoad(Api.GetVersionString());

        // Without this, the server hibernates when no real players are
        // connected — game frames stop, Server.NextFrame queue stalls,
        // and bot_add adopt callbacks never fire. Bots only "appear"
        // once a human joins. Force ticks to keep flowing.
        Server.ExecuteCommand("sv_hibernate_when_empty 0");

        RegisterListener<Listeners.OnTick>(_manager.OnTick);
        RegisterListener<Listeners.OnMapStart>(map =>
        {
            try { _manager?.OnMapStart(); }
            catch (Exception ex) { Log.Error($"OnMapStart: {ex.Message}"); }
        });
        RegisterListener<Listeners.OnClientConnected>(slot => _manager?.OnClientConnected(slot));
        RegisterListener<Listeners.OnClientPutInServer>(slot => _manager?.OnClientPutInServer(slot));
        RegisterListener<Listeners.OnClientDisconnect>(slot => {
            _manager?.OnClientDisconnect(slot);
            // #18: purge per-slot reveal state so a human inheriting the
            // freed slot isn't force-switched / force-respawned as if it
            // were the old bot.
            _manager?.Reveal.OnClientDisconnect(slot);
        });

        // P/12 Reveal Finale (v0.6.0-beta): EventPlayerDeath dispatches to
        // RevealController for bot-kill counter (Stage 2 trigger threshold)
        // and human-death detection (slowmo death cam in Stage 2 + Stage 3
        // trigger when last human dies).
        RegisterEventHandler<EventPlayerDeath>((@event, info) => {
            try {
                var victim = @event.Userid;
                if (victim == null || !victim.IsValid) return HookResult.Continue;
                var isBot = victim.IsBot
                    || (_manager?.FindBySlot((int)victim.Slot) != null);
                _manager?.Reveal.OnPlayerDeath((int)victim.Slot, isBot);

                // BotProfile dynamic-state notify: tilt the victim if it's
                // a managed bot, reward the attacker if it's also a bot.
                // Modules reading CurrentAimSkill / CurrentReactionMs see
                // the resulting drift in following ticks.
                var victimFc = _manager?.FindBySlot((int)victim.Slot);
                victimFc?.Profile.NotifyEvent("Death");
                var attacker = @event.Attacker;
                if (attacker != null && attacker.IsValid && attacker.Slot != victim.Slot) {
                    var attackerFc = _manager?.FindBySlot((int)attacker.Slot);
                    attackerFc?.Profile.NotifyEvent("Kill");
                }
            } catch (Exception ex) { Log.Debug($"EventPlayerDeath dispatch: {ex.Message}"); }
            return HookResult.Continue;
        });

        // AimDiag: capture the bot's view-angle snapshots at fire time and
        // diff against actual bullet trajectory at impact. Off by default;
        // arm via replica_aim_diag.
        RegisterEventHandler<EventWeaponFire>((@event, info) => {
            try {
                AimDiag.OnWeaponFire(@event.Userid);
                // Spray-bloom feed (#45): burst length drives the aim
                // error multiplier in AimController.
                var shooter = @event.Userid;
                if (shooter != null && shooter.IsValid)
                    _manager?.FindBySlot((int)shooter.Slot)?.Aim.NotifyShot(Server.TickCount);
            }
            catch (Exception ex) { Log.Debug($"AimDiag.OnWeaponFire: {ex.Message}"); }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventBulletImpact>((@event, info) => {
            try { AimDiag.OnBulletImpact(@event.Userid, @event.X, @event.Y, @event.Z); }
            catch (Exception ex) { Log.Debug($"AimDiag.OnBulletImpact: {ex.Message}"); }
            return HookResult.Continue;
        });

        // Fleet-team enforcer quiet window: every round/match lifecycle
        // event pushes SwitchTeam activity out past the engine's
        // teardown/spawn dance (see EnforceFleetTeams hang-safety note).
        // round_end is folded into the complacency handler below.
        RegisterEventHandler<EventRoundPrestart>((@event, info) => {
            _manager?.NoteRoundTransition("round_prestart");
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundStart>((@event, info) => {
            _manager?.NoteRoundTransition("round_start");
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundAnnounceMatchStart>((@event, info) => {
            _manager?.NoteRoundTransition("match_start");
            return HookResult.Continue;
        });
        RegisterEventHandler<EventBeginNewMatch>((@event, info) => {
            _manager?.NoteRoundTransition("new_match");
            return HookResult.Continue;
        });

        // BotProfile complacency mechanic (2026-05-08): on round end,
        // compute observed-skill team averages and dispatch RoundEnd
        // events with skill-gap data to each managed bot. Bot's own
        // SkillRating is used directly; human "observed skill" is
        // estimated from in-match K/D (baseline 50 if too few samples).
        RegisterEventHandler<EventRoundEnd>((@event, info) => {
            try {
                if (_manager == null) return HookResult.Continue;
                _manager.NoteRoundTransition("round_end");
                int winnerTeam = @event.Winner;  // 2=T, 3=CT, 0=draw

                // Draw (winnerTeam=0, or other non-T/CT codes used for
                // forfeit/abort): the RoundEnd-with-args path would mark
                // every managed bot as a loser (`win = (0 == botTeam)
                // == false`), inflating LossStreak/Tilt and falsely
                // tripping the complacency-wakeup roll on rounds nobody
                // won. Skip that path; instead fire a "Draw" so each bot
                // still advances RoundsPlayed + RecomputeMood, leaving
                // streaks and the skill-gap drift untouched. (Without
                // bumping RoundsPlayed, the complacency machine sees an
                // artificially small round count and stays in its
                // "first-round excluded" baseline longer than the bots
                // have actually played — Wave's #71 review catch.)
                if (winnerTeam != 2 && winnerTeam != 3) {
                    foreach (var fc in _manager.All) {
                        try { fc.Profile.NotifyEvent("Draw"); }
                        catch (Exception ex) { Log.Debug($"Draw notify slot={fc.Slot}: {ex.Message}"); }
                    }
                    return HookResult.Continue;
                }

                double ctSum = 0, tSum = 0;
                int ctCount = 0, tCount = 0;
                foreach (var c in CounterStrikeSharp.API.Utilities.GetPlayers())
                {
                    if (c == null || !c.IsValid || c.IsHLTV) continue;
                    int team = (int)c.TeamNum;
                    if (team != 2 && team != 3) continue;
                    var fc = _manager.FindBySlot((int)c.Slot);
                    double skill = fc != null
                        ? fc.Profile.SkillRating
                        : EstimateHumanSkill(c);
                    if (team == 3) { ctSum += skill; ctCount++; }
                    else           { tSum += skill;  tCount++; }
                }
                double ctAvg = ctCount > 0 ? ctSum / ctCount : 50.0;
                double tAvg  = tCount  > 0 ? tSum  / tCount  : 50.0;

                foreach (var fc in _manager.All)
                {
                    try {
                        var c = CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(fc.Slot);
                        if (c == null || !c.IsValid) continue;
                        int botTeam = (int)c.TeamNum;
                        if (botTeam != 2 && botTeam != 3) continue;
                        bool win = winnerTeam == botTeam;
                        double ownAvg   = botTeam == 3 ? ctAvg : tAvg;
                        double enemyAvg = botTeam == 3 ? tAvg  : ctAvg;
                        fc.Profile.NotifyEvent("RoundEnd", new RoundEventArgs {
                            Win                = win,
                            OwnTeamAvgSkill    = ownAvg,
                            EnemyTeamAvgSkill  = enemyAvg,
                            OwnPerformance     = EstimateOwnPerformance(c),
                        });
                    } catch (Exception ex) { Log.Debug($"RoundEnd notify slot={fc.Slot}: {ex.Message}"); }
                }
            } catch (Exception ex) { Log.Debug($"EventRoundEnd dispatch: {ex.Message}"); }
            return HookResult.Continue;
        });

        Log.Info($"loaded — telemetry={_telemetry.Path} session={_telemetry.SessionId} " +
                 $"detour={_manager.DetourInstalled} steamIdMode={_manager.SteamIds.Mode}");

        // Vanilla `bot_kick` (no args) kicks every bot, but engine state
        // doesn't tell FleetManager — Reconcile() repopulates within ~1s.
        // Intercept the bare form, drain the fleet through the plugin,
        // and pin FleetSize=0 so Reconcile holds the empty state until
        // the user restores it via `replica_fleet_size N`. Targeted
        // form (`bot_kick <name>`) is left alone — that's how we kick
        // individual bots ourselves and how admins surgically drop one.
        AddCommandListener("bot_kick", (caller, info) => {
            if (_manager == null) return HookResult.Continue;
            // CS2 engine accepts bot_kick in four mass-or-targeted forms:
            //   bot_kick           — mass (no args)
            //   bot_kick all       — mass (all bots)
            //   bot_kick t | ct    — mass (one team)
            //   bot_kick <name>    — targeted (one specific bot)
            // The plugin path (DespawnAll + pin FleetSize=0) only fits the
            // mass forms. The previous `ArgCount > 1` guard fell through on
            // `bot_kick all/t/ct`, letting the engine drain the fleet
            // natively — and FleetManager.Reconcile then spawned a fresh
            // fleet ~1 sec later because no FleetSize-override pin was
            // set. Recognise the mass tokens explicitly. Issue #6.
            bool isMassKick = info.ArgCount == 1;
            if (!isMassKick && info.ArgCount == 2)
            {
                var arg = info.GetArg(1).Trim().ToLowerInvariant();
                if (arg is "all" or "t" or "ct") isMassKick = true;
            }
            if (!isMassKick) return HookResult.Continue; // targeted by name — let engine kick one bot
            try {
                var n = _manager.DespawnAll("vanilla_bot_kick");
                _manager.Config.SetFleetSizeOverride(0);
                Log.Info($"vanilla bot_kick intercepted: drained {n}, fleet pinned to 0 — `replica_fleet_size N` to restore");
            } catch (Exception ex) { Log.Error($"bot_kick listener: {ex.Message}"); }
            return HookResult.Continue;
        }, HookMode.Pre);

        // AimHook controller — only writes override to shared pool. The real
        // detour lives in ReplicaHider C++. See AimHook.cs comment for
        // the rationale (CSSharp Funchook silently failed on this function).

        // Hot reload: OnClientPutInServer will not fire for bots that
        // are already on the server, so adopt them now in one pass.
        if (hotReload) {
            _manager.AdoptExistingBots();
            // OnMapStart won't fire on hot-reload (map already loaded),
            // so arm FleetManager directly. Reconcile will run from
            // Tick at 1Hz and spawn up to FleetSize.
            _manager.Fleet.OnMapStartComplete();
        }
    }

    public override void Unload(bool hotReload)
    {
        // Thread hotReload through (fd9a478, restored in #196): on a
        // hot-reload OnUnload must NOT kick the fleet — the async kickid
        // queue races the next instance's adopt sweep and the stale
        // slots float as ghosts while a fresh fleet spawns alongside.
        try { _manager?.OnUnload(hotReload); } catch { }
        try { _telemetry?.Dispose(); } catch { }
        _manager = null; _telemetry = null;
    }

    /// <summary>
    /// Coarse "observed" skill estimate for a real human (0..100 scale).
    /// Used by the complacency mechanic — bots shouldn't peek the
    /// real player's hidden SkillRating, they only see what's
    /// observable in-match.
    ///
    /// v1 implementation: linear map of <c>c.Score</c> (cumulative match
    /// score including objective bonuses) into a 50..80 band — score &lt;= 0
    /// returns the 50 baseline; each score point adds 0.5 skill, capped at
    /// +30. Score is noisier than K/D and biased toward objective-heavy
    /// roles, but it's always populated; a K/D-based estimator via
    /// <c>MatchStats</c> is tracked in #38 / #39 as a v2 swap. Don't crash
    /// on access.
    /// </summary>
    private static double EstimateHumanSkill(CCSPlayerController c)
    {
        try
        {
            // Try score / K-D-style heuristic via Score property on the
            // controller. CSSharp exposes c.Score (sum of points) — high
            // scores correlate roughly with skill but are too noisy
            // mid-round. Fallback to baseline 50 for v1.
            int score = c.Score;
            if (score <= 0) return 50.0;
            // Linear map 0–60 score → 50–80 skill. Cap to keep estimate
            // away from extremes; complacency math is robust to noise.
            double s = 50.0 + Math.Min(30.0, score * 0.5);
            return s;
        }
        catch
        {
            return 50.0;
        }
    }

    /// <summary>
    /// Per-bot performance signal for the complacency mechanic, derived
    /// from in-match K/D via <c>c.ActionTrackingServices.MatchStats</c>.
    /// Returns 0..1, with 0.5 = neutral (no signal yet, or balanced K/D),
    /// >0.5 = over-performing this match, &lt;0.5 = under-performing.
    ///
    /// Mapping: K/D ratio clamped to [0.25, 4.0], log2-symmetric around
    /// 1.0 (so K/D 2.0 → 0.75 and K/D 0.5 → 0.25). Requires kills+deaths
    /// ≥ 3 before trusting the sample; below that, returns 0.5 baseline.
    /// MatchStats access is guarded — null ActionTrackingServices or any
    /// throw on access falls through to 0.5. Replaces the previous
    /// hardcoded 0.5 placeholder per #38.
    /// </summary>
    private static double EstimateOwnPerformance(CCSPlayerController c)
    {
        try
        {
            var ats = c.ActionTrackingServices;
            if (ats == null) return 0.5;
            int kills  = ats.MatchStats.Kills;
            int deaths = ats.MatchStats.Deaths;
            if (kills + deaths < 3) return 0.5;
            double denom = deaths > 0 ? deaths : 1;
            double kd = Math.Clamp(kills / denom, 0.25, 4.0);
            // log2(0.25)=-2, log2(4)=+2 → /4 → [-0.5,+0.5] → +0.5 → [0,1].
            double perf = 0.5 + Math.Log2(kd) / 4.0;
            return Math.Clamp(perf, 0.0, 1.0);
        }
        catch
        {
            return 0.5;
        }
    }

}
