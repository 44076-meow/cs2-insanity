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
public sealed class ReplicaPlugin : BasePlugin
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

    [ConsoleCommand("replica_spawn_bots", "Spawn N fake bots; team = ct|t|split (default split)")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[count] [ct|t|split]")]
    public void OnSpawnBots(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        var n = _config?.DefaultBotCount ?? 5;
        if (info.ArgCount > 1 && int.TryParse(info.GetArg(1), out var parsed)) n = Math.Clamp(parsed, 1, 32);

        // teamArg: "ct" / "t" force one side; anything else (incl. "split") splits.
        string teamArg = info.ArgCount > 2 ? info.GetArg(2).Trim().ToLowerInvariant() : "split";
        FakeTeam? forced = teamArg switch {
            "ct" => FakeTeam.CT,
            "t"  => FakeTeam.T,
            _    => (FakeTeam?)null,
        };

        for (var i = 0; i < n; i++)
        {
            var team = forced ?? ((i % 2 == 0) ? FakeTeam.CT : FakeTeam.T);
            _manager.Spawn(team);
        }
        var label = forced.HasValue ? forced.Value.ToString() : "split CT/T";
        info.ReplyToCommand($"[Replica] queued {n} bot_add → {label}; see scoreboard in ~1s");
    }

    [ConsoleCommand("replica_kick_bots", "Kick all fake bots; pins FleetSize=0 unless 'respawn' arg given")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[respawn]")]
    public void OnKickBots(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        bool respawn = info.ArgCount > 1
            && info.GetArg(1).Trim().Equals("respawn", StringComparison.OrdinalIgnoreCase);
        var n = _manager.DespawnAll(respawn ? "admin_kick_respawn" : "admin_kick_drain");
        if (!respawn)
        {
            // Pin to 0 so FleetManager.Reconcile holds the empty state.
            // Without this the fleet repopulates within 1 second.
            _manager.Config.SetFleetSizeOverride(0);
            info.ReplyToCommand($"[Replica] kicked {n} fake bots; fleet drained — use `replica_fleet_size N` to restore");
        }
        else
        {
            // `respawn` is an explicit user intent to "return to normal size".
            // If a prior drain (vanilla bot_kick or `replica_kick_bots`) left
            // override pinned to 0, FleetSize would still report 0 and the
            // fleet wouldn't repopulate — message would lie. Clear override
            // first so the cfg-file FleetSize takes over.
            _manager.Config.SetFleetSizeOverride(null);
            info.ReplyToCommand($"[Replica] kicked {n} fake bots; fleet will respawn (size={_manager.Config.FleetSize})");
        }
    }

    [ConsoleCommand("replica_fleet_size", "Set FleetSize override at runtime (0..16); 'default' clears override")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "<0..16|default>")]
    public void OnFleetSize(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        if (info.ArgCount < 2)
        {
            var ovr = _manager.Config.HasFleetSizeOverride
                ? _manager.Config.FleetSizeOverride!.Value.ToString()
                : "(none — using cfg)";
            info.ReplyToCommand($"[Replica] fleet size={_manager.Config.FleetSize} override={ovr} active={_manager.All.Count} pending={_manager.PendingPersonaCount}");
            return;
        }
        var arg = info.GetArg(1).Trim();
        if (arg.Equals("default", StringComparison.OrdinalIgnoreCase) || arg == "-1")
        {
            _manager.Config.SetFleetSizeOverride(null);
            info.ReplyToCommand($"[Replica] fleet size override cleared — using cfg ({_manager.Config.FleetSize})");
            return;
        }
        if (!int.TryParse(arg, out var n))
        {
            info.ReplyToCommand($"[Replica] usage: replica_fleet_size <0..16|default>");
            return;
        }
        var clamped = Math.Clamp(n, 0, 16);
        _manager.Config.SetFleetSizeOverride(clamped);
        info.ReplyToCommand($"[Replica] fleet size override = {clamped} (was active={_manager.All.Count} pending={_manager.PendingPersonaCount})");
    }

    [ConsoleCommand("replica_status", "Print fake-client manager status")]
    [RequiresPermissions("@css/generic")]
    public void OnStatus(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        var ovrLabel = _manager.Config.HasFleetSizeOverride
            ? $" (override={_manager.Config.FleetSizeOverride})"
            : "";
        info.ReplyToCommand($"[Replica] bots={_manager.All.Count} pending={_manager.PendingPersonaCount} " +
                            $"target={_manager.Config.FleetSize}{ovrLabel} " +
                            $"detour={_manager.DetourInstalled} " +
                            $"steamIdMode={_manager.SteamIds.Mode} telemetry={_telemetry?.Path}");
        if (_manager.All.Count == 0
            && _manager.Config.HasFleetSizeOverride
            && _manager.Config.FleetSizeOverride == 0)
        {
            info.ReplyToCommand("  (fleet drained — `replica_fleet_size N` or `replica_kick_bots respawn` to restore)");
        }
        foreach (var fc in _manager.All.Take(16))
        {
            string schemaName = "?";
            try
            {
                var c = CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(fc.Slot);
                if (c != null && c.IsValid) schemaName = c.PlayerName ?? "<null>";
            } catch { }
            info.ReplyToCommand($"  #{fc.Id} target={fc.Name} schemaName={schemaName} slot={fc.Slot} " +
                                $"ping={fc.PingView.LastWrittenPing}ms " +
                                $"net={fc.Network.Type} arch={fc.Profile.Archetype} skill={fc.Profile.SkillRating} " +
                                $"mood={fc.Profile.Mood} tilt={fc.Profile.Tilt}");
        }
        info.ReplyToCommand($"[Replica] hider active={_manager.IsHiderActive()}");
    }

    [ConsoleCommand("replica_doctor", "Ground-truth roster/fleet diagnostic — managed vs live controllers vs ghosts/husks")]
    [RequiresPermissions("@css/generic")]
    public void OnDoctor(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        foreach (var line in _manager.BuildDoctorReport()) info.ReplyToCommand(line);
    }

    [ConsoleCommand("replica_aim_structural",
        "Toggle recorded-human per-weapon aim bias on bots (record→profile→bot, #44)")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[on|off|reload]")]
    public void OnAimStructural(CCSPlayerController? caller, CommandInfo info)
    {
        var arg = info.ArgCount >= 2 ? info.GetArg(1).Trim().ToLowerInvariant() : "";
        if (arg == "reload")
        {
            StructuralProfileStore.Load();
            // Re-attach to every live bot so a reload takes effect without respawn.
            if (_manager != null)
                foreach (var fc in _manager.All)
                    fc.Aim.StructuralBias = StructuralProfileStore.TryGet(fc.Name);
            info.ReplyToCommand($"[Replica] structural profiles reloaded: {StructuralProfileStore.LoadedProfiles} persona(s) from {StructuralProfileStore.LoadedFrom ?? "(none)"}");
            return;
        }
        if (arg == "on")  AimController.StructuralEnabled = true;
        if (arg == "off") AimController.StructuralEnabled = false;
        info.ReplyToCommand($"[Replica] structural aim = {(AimController.StructuralEnabled ? "ON" : "OFF")} "
                          + $"(profiles loaded: {StructuralProfileStore.LoadedProfiles}; "
                          + $"usage: replica_aim_structural [on|off|reload])");
    }

    // P/12 Reveal Finale entry. Two registrations:
    //   - `replica_reveal` — rcon / server console
    //   - `css_reveal`      — chat trigger `!reveal` (CSSharp's css_ prefix
    //                          maps `css_NAME` to `!NAME` chat command)
    // Permission @css/root — admin-only.
    [ConsoleCommand("replica_reveal", "Trigger reveal finale state machine")]
    [ConsoleCommand("css_reveal", "Trigger reveal finale state machine (chat: !reveal)")]
    [RequiresPermissions("@css/root")]
    public void OnReveal(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        var prevStage = _manager.Reveal.Stage;
        _manager.Reveal.Start();
        info.ReplyToCommand($"[Replica] reveal: prev={prevStage} → Stage0");
    }

    // P/12 Stage 4 APOCALYPSE manual trigger (v0.7.0-beta — 2026-05-08).
    // Requires reveal already active (Stage 1/2/3); transitions to Stage 4.
    [ConsoleCommand("replica_reveal_apocalypse", "Trigger Stage 4 (C4 suicide bots) — requires active reveal")]
    [ConsoleCommand("css_reveal_apocalypse", "Trigger Stage 4 (chat: !reveal_apocalypse)")]
    [RequiresPermissions("@css/root")]
    public void OnRevealApocalypse(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        var prevStage = _manager.Reveal.Stage;
        bool ok = _manager.Reveal.StartApocalypse();
        info.ReplyToCommand(ok
            ? $"[Replica] APOCALYPSE: prev={prevStage} → Stage4"
            : $"[Replica] APOCALYPSE refused (stage={prevStage}); start a reveal first");
    }

    // ──────────────────────────────────────────────────────────────────
    // Aim Hook — PRE-detour on libserver.so:CCSBot::UpdateLookAngles
    // Writes m_lookPitch/Yaw before bot AI smoother reads them.
    // ──────────────────────────────────────────────────────────────────

    [ConsoleCommand("replica_aim_diag",
        "Aim diagnostic: log shooter's angle fields vs actual bullet trajectory")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[on|off] [budget=30]")]
    public void OnAimDiag(CCSPlayerController? caller, CommandInfo info)
    {
        bool on = info.ArgCount < 2 || info.GetArg(1).Trim().ToLowerInvariant() != "off";
        int budget = 30;
        if (info.ArgCount >= 3 && int.TryParse(info.GetArg(2), out var b)) budget = Math.Clamp(b, 1, 500);
        AimDiag.SetEnabled(on, budget);
        info.ReplyToCommand($"[aimdiag] enabled={on} budget={AimDiag.LogsRemaining}");
    }

    [ConsoleCommand("replica_aim_disable",
        "Toggle AimController identity-passthrough+noise globally for diagnostic")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[on|off]")]
    public void OnAimDisable(CCSPlayerController? caller, CommandInfo info)
    {
        bool off = info.ArgCount >= 2 && info.GetArg(1).Trim().ToLowerInvariant() is "on" or "true" or "1";
        AimController.GlobalDisable = off;
        info.ReplyToCommand($"[aim] AimController disabled = {off}  (ON = AimController.Tick no-ops, manual perslot writes survive)");
    }

    [ConsoleCommand("replica_aim_perslot",
        "Per-slot aim override: writes pawn ptr + (pitch, yaw) into pool AimSlot[slot]; only that bot turns")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot> <pitch> <yaw>  |  <slot> off  |  status")]
    public void OnAimPerSlot(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        var pool = _manager.GetPool();
        if (pool == null || !pool.IsOpen) { info.ReplyToCommand("[aimperslot] pool not open"); return; }

        var first = info.GetArg(1).Trim().ToLowerInvariant();
        if (first == "status")
        {
            int active = 0;
            for (int i = 0; i < PoolMmap.AimSlotCount; i++)
            {
                var (key, en, p, y) = pool.ReadAimSlot(i);
                if (key == 0 && !en) continue;
                info.ReplyToCommand($"  slot={i} botKey=0x{key:X} enabled={en} pitch={p:F1} yaw={y:F1}");
                active++;
            }
            info.ReplyToCommand($"[aimperslot] {active} slot(s) populated");
            return;
        }

        if (!int.TryParse(first, out var slot) || slot < 0 || slot >= PoolMmap.AimSlotCount)
        {
            info.ReplyToCommand("[aimperslot] usage: replica_aim_perslot <slot> <pitch> <yaw> | <slot> off | status");
            return;
        }

        var ctrl = Utilities.GetPlayerFromSlot(slot);
        if (ctrl == null || !ctrl.IsValid)
        {
            info.ReplyToCommand($"[aimperslot] slot {slot} has no controller");
            return;
        }
        var pawn = ctrl.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
        {
            info.ReplyToCommand($"[aimperslot] slot {slot} pawn invalid");
            return;
        }
        // Pool key is the CCSBot pointer (matches `this` inside the C++
        // AimHook PRE-detour). pawn.Handle would be the wrong key — see
        // aim_hook.cpp comment near LookupPerSlotAim call.
        var bot = pawn.Bot;
        if (bot == null || bot.Handle == IntPtr.Zero)
        {
            info.ReplyToCommand($"[aimperslot] slot {slot} bot is null (real CCSBot AI not present)");
            return;
        }
        ulong botKey = (ulong)bot.Handle.ToInt64();

        if (info.ArgCount >= 3 && info.GetArg(2).Trim().ToLowerInvariant() is "off" or "clear" or "none")
        {
            pool.ClearAimSlot(slot);
            info.ReplyToCommand($"[aimperslot] cleared slot {slot}");
            return;
        }

        if (info.ArgCount < 4
            || !float.TryParse(info.GetArg(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pitch)
            || !float.TryParse(info.GetArg(3), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var yaw))
        {
            info.ReplyToCommand("[aimperslot] usage: replica_aim_perslot <slot> <pitch> <yaw>");
            return;
        }
        pitch = Math.Clamp(pitch, -89f, 89f);
        pool.WriteAimSlot(slot, botKey, enabled: true, pitch: pitch, yaw: yaw);
        info.ReplyToCommand($"[aimperslot] slot={slot} botKey=0x{botKey:X} pitch={pitch:F1} yaw={yaw:F1}");
    }

    [ConsoleCommand("replica_aim_hook_set",
        "Aim override (global): writes pool fields read by ReplicaHider C++ PRE-detour on CCSBot::UpdateLookAngles")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "<pitch> <yaw>  |  off  |  status")]
    public void OnAimHookSet(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        var pool = _manager.GetPool();
        if (info.ArgCount < 2)
        {
            info.ReplyToCommand($"[aimhook] {AimHook.DebugStatus(pool)}");
            return;
        }
        var first = info.GetArg(1).Trim().ToLowerInvariant();
        if (first is "off" or "clear" or "none")
        {
            AimHook.SetGlobalOverride(pool, null, null);
            info.ReplyToCommand($"[aimhook] override cleared. {AimHook.DebugStatus(pool)}");
            return;
        }
        if (first == "status")
        {
            info.ReplyToCommand($"[aimhook] {AimHook.DebugStatus(pool)}");
            return;
        }
        if (info.ArgCount < 3
            || !float.TryParse(info.GetArg(1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pitch)
            || !float.TryParse(info.GetArg(2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var yaw))
        {
            info.ReplyToCommand("[aimhook] usage: replica_aim_hook_set <pitch> <yaw> | off | status");
            return;
        }
        pitch = Math.Clamp(pitch, -89f, 89f);
        AimHook.SetGlobalOverride(pool, pitch, yaw);
        info.ReplyToCommand($"[aimhook] override set p={pitch:F1} y={yaw:F1}. {AimHook.DebugStatus(pool)}");
    }



    // ──────────────────────────────────────────────────────────────────
    // BotProfile inspection (the umbrella structure introduced 2026-05-08)
    // ──────────────────────────────────────────────────────────────────

    [ConsoleCommand("replica_profile", "Print full BotProfile dump for a bot")]
    [ConsoleCommand("css_profile", "Print BotProfile (chat: !profile <slot>)")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot>")]
    public void OnProfile(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        if (!int.TryParse(info.GetArg(1), out var slot)) {
            info.ReplyToCommand("[Replica] usage: replica_profile <slot>");
            return;
        }
        var fc = _manager.FindBySlot(slot);
        if (fc == null) {
            info.ReplyToCommand($"[Replica] no managed bot at slot {slot}");
            return;
        }
        info.ReplyToCommand($"[Replica] BotProfile for #{fc.Id} {fc.Name} (slot={slot}):");
        foreach (var line in fc.Profile.DebugDump().Split('\n'))
            info.ReplyToCommand(line);
        info.ReplyToCommand($"  simulator:    {fc.Simulator.DebugStateString()}");
    }

    [ConsoleCommand("replica_hider_active", "Toggle ReplicaHider BOT-icon hiding (0/1)")]
    [RequiresPermissions("@css/generic")]
    public void OnHiderActive(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Replica] not loaded"); return; }
        if (info.ArgCount < 2)
        {
            info.ReplyToCommand($"[Replica] hider active={_manager.IsHiderActive()} (usage: replica_hider_active 0|1)");
            return;
        }
        bool on = info.GetArg(1).Trim() is "1" or "true" or "on";
        _manager.SetHiderActive(on);
        info.ReplyToCommand($"[Replica] hider active={on}");
    }
}
