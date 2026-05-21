using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;

namespace InsanityRevive;

[MinimumApiVersion(220)]
public sealed class InsanityRevivePlugin : BasePlugin
{
    public override string ModuleName    => "InsanityRevive";
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
        // AimProbe tick driver — Step 0 of the Aim Module v1 spec.
        // Registered SEPARATELY (not inside FakeClientManager.OnTick) so its
        // lifetime is independent of bot management; probe runs even on
        // freshly connected slots that haven't been adopted yet. Cheap when
        // the pin map is empty (early-out).
        RegisterListener<Listeners.OnTick>(AimProbe.OnTick);
        RegisterListener<Listeners.OnTick>(AimLookflowProbe.OnTick);
        RegisterListener<Listeners.OnMapStart>(map =>
        {
            try { _manager?.OnMapStart(); }
            catch (Exception ex) { Log.Error($"OnMapStart: {ex.Message}"); }
        });
        RegisterListener<Listeners.OnClientConnected>(slot => _manager?.OnClientConnected(slot));
        RegisterListener<Listeners.OnClientPutInServer>(slot => _manager?.OnClientPutInServer(slot));
        RegisterListener<Listeners.OnClientDisconnect>(slot => _manager?.OnClientDisconnect(slot));

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
        // arm via insanity_aim_diag.
        RegisterEventHandler<EventWeaponFire>((@event, info) => {
            try { AimDiag.OnWeaponFire(@event.Userid); }
            catch (Exception ex) { Log.Debug($"AimDiag.OnWeaponFire: {ex.Message}"); }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventBulletImpact>((@event, info) => {
            try { AimDiag.OnBulletImpact(@event.Userid, @event.X, @event.Y, @event.Z); }
            catch (Exception ex) { Log.Debug($"AimDiag.OnBulletImpact: {ex.Message}"); }
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
                            OwnPerformance     = 0.5,  // placeholder; future: from MatchStats
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
        // the user restores it via `insanity_fleet_size N`. Targeted
        // form (`bot_kick <name>`) is left alone — that's how we kick
        // individual bots ourselves and how admins surgically drop one.
        AddCommandListener("bot_kick", (caller, info) => {
            if (_manager == null) return HookResult.Continue;
            if (info.ArgCount > 1) return HookResult.Continue; // targeted, leave alone
            try {
                var n = _manager.DespawnAll("vanilla_bot_kick");
                _manager.Config.SetFleetSizeOverride(0);
                Log.Info($"vanilla bot_kick intercepted: drained {n}, fleet pinned to 0 — `insanity_fleet_size N` to restore");
            } catch (Exception ex) { Log.Error($"bot_kick listener: {ex.Message}"); }
            return HookResult.Continue;
        }, HookMode.Pre);

        // AimHook controller — only writes override to shared pool. The real
        // detour lives in InsanityHider C++. See AimHook.cs comment for
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
        try { _manager?.Dispose(); } catch { }
        try { _telemetry?.Dispose(); } catch { }
        _manager = null; _telemetry = null;
    }

    /// <summary>
    /// Coarse "observed" skill estimate for a real human (0..100 scale).
    /// Used by the complacency mechanic — bots shouldn't peek the
    /// real player's hidden SkillRating, they only see what's
    /// observable in-match. v1: derive from in-match K/D once at least
    /// 3 deaths have happened; otherwise return 50 baseline.
    ///
    /// MatchStats fields (from `c.ActionTrackingServices.MatchStats` or
    /// similar) may not always be populated; if they aren't, fall
    /// through to the baseline. Don't crash on access.
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

    [ConsoleCommand("insanity_spawn_bots", "Spawn N fake bots; team = ct|t|split (default split)")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[count] [ct|t|split]")]
    public void OnSpawnBots(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
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
        info.ReplyToCommand($"[Insanity] queued {n} bot_add → {label}; see scoreboard in ~1s");
    }

    [ConsoleCommand("insanity_kick_bots", "Kick all fake bots; pins FleetSize=0 unless 'respawn' arg given")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[respawn]")]
    public void OnKickBots(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        bool respawn = info.ArgCount > 1
            && info.GetArg(1).Trim().Equals("respawn", StringComparison.OrdinalIgnoreCase);
        var n = _manager.DespawnAll(respawn ? "admin_kick_respawn" : "admin_kick_drain");
        if (!respawn)
        {
            // Pin to 0 so FleetManager.Reconcile holds the empty state.
            // Without this the fleet repopulates within 1 second.
            _manager.Config.SetFleetSizeOverride(0);
            info.ReplyToCommand($"[Insanity] kicked {n} fake bots; fleet drained — use `insanity_fleet_size N` to restore");
        }
        else
        {
            // `respawn` is an explicit user intent to "return to normal size".
            // If a prior drain (vanilla bot_kick or `insanity_kick_bots`) left
            // override pinned to 0, FleetSize would still report 0 and the
            // fleet wouldn't repopulate — message would lie. Clear override
            // first so the cfg-file FleetSize takes over.
            _manager.Config.SetFleetSizeOverride(null);
            info.ReplyToCommand($"[Insanity] kicked {n} fake bots; fleet will respawn (size={_manager.Config.FleetSize})");
        }
    }

    [ConsoleCommand("insanity_fleet_size", "Set FleetSize override at runtime (0..16); 'default' clears override")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "<0..16|default>")]
    public void OnFleetSize(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (info.ArgCount < 2)
        {
            var ovr = _manager.Config.HasFleetSizeOverride
                ? _manager.Config.FleetSizeOverride!.Value.ToString()
                : "(none — using cfg)";
            info.ReplyToCommand($"[Insanity] fleet size={_manager.Config.FleetSize} override={ovr} active={_manager.All.Count} pending={_manager.PendingPersonaCount}");
            return;
        }
        var arg = info.GetArg(1).Trim();
        if (arg.Equals("default", StringComparison.OrdinalIgnoreCase) || arg == "-1")
        {
            _manager.Config.SetFleetSizeOverride(null);
            info.ReplyToCommand($"[Insanity] fleet size override cleared — using cfg ({_manager.Config.FleetSize})");
            return;
        }
        if (!int.TryParse(arg, out var n))
        {
            info.ReplyToCommand($"[Insanity] usage: insanity_fleet_size <0..16|default>");
            return;
        }
        var clamped = Math.Clamp(n, 0, 16);
        _manager.Config.SetFleetSizeOverride(clamped);
        info.ReplyToCommand($"[Insanity] fleet size override = {clamped} (was active={_manager.All.Count} pending={_manager.PendingPersonaCount})");
    }

    [ConsoleCommand("insanity_status", "Print fake-client manager status")]
    [RequiresPermissions("@css/generic")]
    public void OnStatus(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var ovrLabel = _manager.Config.HasFleetSizeOverride
            ? $" (override={_manager.Config.FleetSizeOverride})"
            : "";
        info.ReplyToCommand($"[Insanity] bots={_manager.All.Count} pending={_manager.PendingPersonaCount} " +
                            $"target={_manager.Config.FleetSize}{ovrLabel} " +
                            $"detour={_manager.DetourInstalled} " +
                            $"steamIdMode={_manager.SteamIds.Mode} telemetry={_telemetry?.Path}");
        if (_manager.All.Count == 0
            && _manager.Config.HasFleetSizeOverride
            && _manager.Config.FleetSizeOverride == 0)
        {
            info.ReplyToCommand("  (fleet drained — `insanity_fleet_size N` or `insanity_kick_bots respawn` to restore)");
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
        info.ReplyToCommand($"[Insanity] hider active={_manager.IsHiderActive()}");
    }

    // P/12 Reveal Finale entry. Two registrations:
    //   - `insanity_reveal` — rcon / server console
    //   - `css_reveal`      — chat trigger `!reveal` (CSSharp's css_ prefix
    //                          maps `css_NAME` to `!NAME` chat command)
    // Permission @css/root — admin-only.
    [ConsoleCommand("insanity_reveal", "Trigger reveal finale state machine")]
    [ConsoleCommand("css_reveal", "Trigger reveal finale state machine (chat: !reveal)")]
    [RequiresPermissions("@css/root")]
    public void OnReveal(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var prevStage = _manager.Reveal.Stage;
        _manager.Reveal.Start();
        info.ReplyToCommand($"[Insanity] reveal: prev={prevStage} → Stage0");
    }

    // P/12 Stage 4 APOCALYPSE manual trigger (v0.7.0-beta — 2026-05-08).
    // Requires reveal already active (Stage 1/2/3); transitions to Stage 4.
    [ConsoleCommand("insanity_reveal_apocalypse", "Trigger Stage 4 (C4 suicide bots) — requires active reveal")]
    [ConsoleCommand("css_reveal_apocalypse", "Trigger Stage 4 (chat: !reveal_apocalypse)")]
    [RequiresPermissions("@css/root")]
    public void OnRevealApocalypse(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var prevStage = _manager.Reveal.Stage;
        bool ok = _manager.Reveal.StartApocalypse();
        info.ReplyToCommand(ok
            ? $"[Insanity] APOCALYPSE: prev={prevStage} → Stage4"
            : $"[Insanity] APOCALYPSE refused (stage={prevStage}); start a reveal first");
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 4 probes (temporary live-verification commands; see Probe.cs)
    // ──────────────────────────────────────────────────────────────────

    [ConsoleCommand("insanity_probe_glow", "Probe 1: tint a bot's pawn red via m_clrRender")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot> [r g b]")]
    public void OnProbeGlow(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (!int.TryParse(info.GetArg(1), out var slot)) {
            info.ReplyToCommand("[probe] usage: insanity_probe_glow <slot> [r g b]");
            return;
        }
        byte r = 255, g = 0, b = 0;
        if (info.ArgCount >= 5
            && byte.TryParse(info.GetArg(2), out var pr)
            && byte.TryParse(info.GetArg(3), out var pg)
            && byte.TryParse(info.GetArg(4), out var pb)) { r = pr; g = pg; b = pb; }
        info.ReplyToCommand($"[probe] {Probe.Glow(slot, r, g, b)}");
    }

    [ConsoleCommand("insanity_probe_c4", "Probe 2: GiveNamedItem(weapon_c4) on a bot")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot>")]
    public void OnProbeC4(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (!int.TryParse(info.GetArg(1), out var slot)) {
            info.ReplyToCommand("[probe] usage: insanity_probe_c4 <slot>");
            return;
        }
        info.ReplyToCommand($"[probe] {Probe.GiveC4(slot)}");
    }

    [ConsoleCommand("insanity_probe_hurtzero", "Probe 3: arm/disarm BotDamagePatch as alt damage filter")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[arm|disarm]")]
    public void OnProbeHurtZero(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var arg = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : "arm";
        var msg = arg == "disarm" ? Probe.HurtZeroDisarm(_manager) : Probe.HurtZeroArmOnce(_manager);
        info.ReplyToCommand($"[probe] {msg}");
    }

    // ──────────────────────────────────────────────────────────────────
    // Aim Module v1 — Step 0 probes (AimProbe.cs). Verifies that a write
    // path for view angles exists from CSSharp before the parametric model
    // is built. Drop these once one of three methods is empirically green.
    // ──────────────────────────────────────────────────────────────────

    [ConsoleCommand("insanity_probe_aim_pin",
        "Aim probe: write fixed (pitch,yaw) every tick for N seconds")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 3, usage: "<slot> <pitch> <yaw> [seconds=5] [method=vangle|eye|teleport]")]
    public void OnProbeAimPin(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (!int.TryParse(info.GetArg(1), out var slot)
            || !float.TryParse(info.GetArg(2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pitch)
            || !float.TryParse(info.GetArg(3),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var yaw))
        {
            info.ReplyToCommand("[probe] usage: insanity_probe_aim_pin <slot> <pitch> <yaw> [seconds] [method]");
            return;
        }
        int seconds = 5;
        if (info.ArgCount >= 5 && int.TryParse(info.GetArg(4), out var s)) seconds = Math.Clamp(s, 1, 30);
        var method = info.ArgCount >= 6 ? AimProbe.ParseMethod(info.GetArg(5)) : AimProbe.Method.VAngle;
        info.ReplyToCommand($"[probe] {AimProbe.PinSlot(slot, pitch, yaw, seconds, method)}");
    }

    [ConsoleCommand("insanity_probe_aim_persist",
        "Aim probe: write (pitch,yaw) ONCE then observe drift for 3s")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 3, usage: "<slot> <pitch> <yaw> [method=vangle|eye|teleport]")]
    public void OnProbeAimPersist(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (!int.TryParse(info.GetArg(1), out var slot)
            || !float.TryParse(info.GetArg(2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pitch)
            || !float.TryParse(info.GetArg(3),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var yaw))
        {
            info.ReplyToCommand("[probe] usage: insanity_probe_aim_persist <slot> <pitch> <yaw> [method]");
            return;
        }
        var method = info.ArgCount >= 5 ? AimProbe.ParseMethod(info.GetArg(4)) : AimProbe.Method.VAngle;
        info.ReplyToCommand($"[probe] {AimProbe.PersistSlot(slot, pitch, yaw, method)}");
    }

    [ConsoleCommand("insanity_probe_aim_unpin",
        "Aim probe: cancel a pin (slot or 'all')")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[slot|all]")]
    public void OnProbeAimUnpin(CCSPlayerController? caller, CommandInfo info)
    {
        int slot = -1;
        if (info.ArgCount > 1 && info.GetArg(1).Trim().ToLowerInvariant() != "all"
            && !int.TryParse(info.GetArg(1), out slot))
        {
            info.ReplyToCommand("[probe] usage: insanity_probe_aim_unpin [slot|all]");
            return;
        }
        info.ReplyToCommand($"[probe] {AimProbe.Unpin(slot)}");
    }

    [ConsoleCommand("insanity_probe_aim_status",
        "Aim probe: list active pins")]
    [RequiresPermissions("@css/cheats")]
    public void OnProbeAimStatus(CCSPlayerController? caller, CommandInfo info)
    {
        foreach (var line in AimProbe.Status().Split('\n'))
            info.ReplyToCommand($"[probe] {line}");
    }

    // ──────────────────────────────────────────────────────────────────
    // Aim Hook — PRE-detour on libserver.so:CCSBot::UpdateLookAngles
    // Writes m_lookPitch/Yaw before bot AI smoother reads them.
    // ──────────────────────────────────────────────────────────────────

    [ConsoleCommand("insanity_aim_diag",
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

    [ConsoleCommand("insanity_aim_disable",
        "Toggle AimController identity-passthrough+noise globally for diagnostic")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[on|off]")]
    public void OnAimDisable(CCSPlayerController? caller, CommandInfo info)
    {
        bool off = info.ArgCount >= 2 && info.GetArg(1).Trim().ToLowerInvariant() is "on" or "true" or "1";
        AimController.GlobalDisable = off;
        info.ReplyToCommand($"[aim] AimController disabled = {off}  (ON = AimController.Tick no-ops, manual perslot writes survive)");
    }

    [ConsoleCommand("insanity_aim_perslot",
        "Per-slot aim override: writes pawn ptr + (pitch, yaw) into pool AimSlot[slot]; only that bot turns")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot> <pitch> <yaw>  |  <slot> off  |  status")]
    public void OnAimPerSlot(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
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
            info.ReplyToCommand("[aimperslot] usage: insanity_aim_perslot <slot> <pitch> <yaw> | <slot> off | status");
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
            info.ReplyToCommand("[aimperslot] usage: insanity_aim_perslot <slot> <pitch> <yaw>");
            return;
        }
        pitch = Math.Clamp(pitch, -89f, 89f);
        pool.WriteAimSlot(slot, botKey, enabled: true, pitch: pitch, yaw: yaw);
        info.ReplyToCommand($"[aimperslot] slot={slot} botKey=0x{botKey:X} pitch={pitch:F1} yaw={yaw:F1}");
    }

    [ConsoleCommand("insanity_probe_lookflow",
        "Capture per-tick m_lookPitch/Yaw + m_angEyeAngles for one bot to discriminate engine-target vs smoother-output")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot> [ticks=256]  |  stop")]
    public void OnAimProbeLookflow(CCSPlayerController? caller, CommandInfo info)
    {
        var first = info.GetArg(1).Trim().ToLowerInvariant();
        if (first is "stop" or "off")
        {
            AimLookflowProbe.StopEarly();
            info.ReplyToCommand("[lookflow] stop requested; dump in server.log");
            return;
        }
        if (!int.TryParse(first, out var slot) || slot < 0 || slot > 63)
        {
            info.ReplyToCommand("[lookflow] usage: insanity_probe_lookflow <slot> [ticks=256] | stop");
            return;
        }
        int ticks = 256;
        if (info.ArgCount >= 3 && int.TryParse(info.GetArg(2), out var t)) ticks = Math.Clamp(t, 16, 4096);
        AimLookflowProbe.Start(slot, ticks);
        info.ReplyToCommand($"[lookflow] armed slot={slot} ticks={ticks}; engage the bot, dump fires automatically into server.log");
    }

    [ConsoleCommand("insanity_aim_hook_set",
        "Aim override (global): writes pool fields read by InsanityHider C++ PRE-detour on CCSBot::UpdateLookAngles")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "<pitch> <yaw>  |  off  |  status")]
    public void OnAimHookSet(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
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
            info.ReplyToCommand("[aimhook] usage: insanity_aim_hook_set <pitch> <yaw> | off | status");
            return;
        }
        pitch = Math.Clamp(pitch, -89f, 89f);
        AimHook.SetGlobalOverride(pool, pitch, yaw);
        info.ReplyToCommand($"[aimhook] override set p={pitch:F1} y={yaw:F1}. {AimHook.DebugStatus(pool)}");
    }



    // ──────────────────────────────────────────────────────────────────
    // BotProfile inspection (the umbrella structure introduced 2026-05-08)
    // ──────────────────────────────────────────────────────────────────

    [ConsoleCommand("insanity_profile", "Print full BotProfile dump for a bot")]
    [ConsoleCommand("css_profile", "Print BotProfile (chat: !profile <slot>)")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot>")]
    public void OnProfile(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (!int.TryParse(info.GetArg(1), out var slot)) {
            info.ReplyToCommand("[Insanity] usage: insanity_profile <slot>");
            return;
        }
        var fc = _manager.FindBySlot(slot);
        if (fc == null) {
            info.ReplyToCommand($"[Insanity] no managed bot at slot {slot}");
            return;
        }
        info.ReplyToCommand($"[Insanity] BotProfile for #{fc.Id} {fc.Name} (slot={slot}):");
        foreach (var line in fc.Profile.DebugDump().Split('\n'))
            info.ReplyToCommand(line);
        info.ReplyToCommand($"  simulator:    {fc.Simulator.DebugStateString()}");
    }

    [ConsoleCommand("insanity_hider_active", "Toggle InsanityHider BOT-icon hiding (0/1)")]
    [RequiresPermissions("@css/generic")]
    public void OnHiderActive(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (info.ArgCount < 2)
        {
            info.ReplyToCommand($"[Insanity] hider active={_manager.IsHiderActive()} (usage: insanity_hider_active 0|1)");
            return;
        }
        bool on = info.GetArg(1).Trim() is "1" or "true" or "on";
        _manager.SetHiderActive(on);
        info.ReplyToCommand($"[Insanity] hider active={on}");
    }
}
