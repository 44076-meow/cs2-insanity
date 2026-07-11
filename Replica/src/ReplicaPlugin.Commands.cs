using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;

namespace Replica;

// Console/chat command surface. Plugin lifecycle: see ReplicaPlugin.cs.
public sealed partial class ReplicaPlugin
{
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
