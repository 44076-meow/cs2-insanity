using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;

namespace InsanityPaints;

[MinimumApiVersion(220)]
public sealed class InsanityPaintsPlugin : BasePlugin
{
    public override string ModuleName    => "InsanityPaints";
    public override string ModuleVersion => "v0.1.0-alpha";
    public override string ModuleAuthor  => "frad70";

    private Settings?            _settings;
    private FakeSlotsReader?     _slots;
    private PaintsDatabase?      _db;
    private PlayersStore?        _players;
    private BotsStore?           _bots;
    private BotLoadoutResolver?  _resolver;
    private ApplyService?        _apply;
    private ChatMenuController?  _menus;
    private WebServer?           _web;

    public override void Load(bool hotReload)
    {
        try { LoadEverything(); }
        catch (Exception ex) { Log.Error($"Load: {ex.Message}"); throw; }

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnClientDisconnect>(slot => {
            // Flush kill-counter increments to disk when a player leaves
            // so we don't lose their progress on the next plugin reload.
            // Bot counters also get the same flush — managed bots
            // disconnect on mapchange / Revive kick, and we don't want
            // ropz's 1337 frags to vanish if the process dies right after.
            try { _players?.Save(); } catch { }
            try { _bots?.Save();    } catch { }
        });

        AddCommand("css_insanity_paints_reload", "InsanityPaints: hot-reload settings + catalogs + players", OnReloadCommand);

        Log.Info($"{ModuleName} {ModuleVersion} loaded (hotReload={hotReload})");
    }

    public override void Unload(bool hotReload)
    {
        // Persist in-flight state before tearing down. Hot-reloads happen
        // mid-session; without this any kill counts collected since the
        // last 5-kill autosave / disconnect would evaporate.
        try { _players?.Save(); } catch { }
        try { _bots?.Save();    } catch { }
        try { _web?.Stop();     } catch { }
        try { _slots?.Dispose();} catch { }
        Log.Info($"{ModuleName} unloaded");
    }

    /// <summary>Full bootstrap. Runs once in Load() and again when the
    /// admin issues <c>css_insanity_paints_reload</c> from the server
    /// console — both contexts are safe to recreate the WebServer +
    /// reregister the chat menus.</summary>
    private void LoadEverything()
    {
        ReloadData();

        _menus = new ChatMenuController(this, _settings!, _db!, _players!);
        _menus.Register();

        // Web panel — stop any previous instance first so a reload
        // doesn't leave a zombie listener holding the port.
        try { _web?.Stop(); } catch { }
        _web = new WebServer(
            // Settings is the only dependency that's reassigned on
            // reload (it's a record-style snapshot of the JSON file);
            // pass an accessor so the panel always sees the freshest
            // value. ApplyService is also reassigned on reload — same
            // accessor pattern. The other refs are reloaded in-place by
            // ReloadData and stay stable.
            settingsFn: () => _settings!,
            db: _db!,
            players: _players!,
            resolver: _resolver!,
            slots: _slots!,
            applyFn: () => _apply,
            // BasePlugin exposes ModuleDirectory — the on-disk path of
            // the loaded plugin. We need it because CSSharp's assembly
            // load context leaves Assembly.Location empty; ModuleDirectory
            // is the authoritative root for wwwroot/.
            moduleDir: ModuleDirectory,
            // The web panel's Reload button is data-only — we *can't*
            // restart the WebServer from inside a web request without
            // killing the in-flight response. ChatMenuController is
            // also intentionally skipped: commands are already
            // registered, and re-AddCommand on the same name churns
            // CSSharp internals.
            reloadAction: ReloadData);
        _web.Start();
    }

    /// <summary>Re-read settings / catalogs / players + rebuild the
    /// resolver cache and apply service. Safe to call from the web
    /// panel: it never touches CSSharp registration APIs or the HTTP
    /// listener itself. Crucially, the dependency objects the
    /// WebServer captured at boot time are *mutated in place*
    /// (PaintsDatabase.LoadFrom resets and repopulates, PlayersStore.Load
    /// clears and refills, FakeSlotsReader.TryOpen closes+reopens,
    /// BotLoadoutResolver.Clear drops the cache). So even though we
    /// reassign `_settings` (via accessor) and `_apply`, the WebServer
    /// keeps reading live data.</summary>
    private void ReloadData()
    {
        _settings = Settings.LoadOrCreate();
        Log.SetLevel(_settings.LogLevel);

        _slots ??= new FakeSlotsReader();
        _slots.TryOpen(_settings.PoolPath);  // closes+reopens if already open

        _db ??= new PaintsDatabase();
        _db.LoadFrom();

        _players ??= new PlayersStore();
        _players.Load();

        _bots ??= new BotsStore();
        _bots.Load();

        _resolver ??= new BotLoadoutResolver(_db);
        _resolver.Clear();   // drop the cached per-name loadouts

        _apply = new ApplyService(this, _settings, _db, _players, _bots, _resolver, _slots);
    }

    // -- events ----------------------------------------------------------

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        try
        {
            var player = @event.Userid;
            if (player == null || _apply == null) return HookResult.Continue;
            // If our reader didn't attach earlier (Revive may have been
            // loaded after us), opportunistically retry. Cheap when
            // already open — TryOpen short-circuits on success.
            if (_slots != null && !_slots.IsOpen)
                _slots.TryOpen(_settings!.PoolPath);
            _apply.OnPlayerSpawn(player);
        }
        catch (Exception ex) { Log.Warn($"OnPlayerSpawn: {ex.Message}"); }
        return HookResult.Continue;
    }

    /// <summary>Catch the round-restart paths that respawn outside the
    /// EventPlayerSpawn flow (notably the
    /// `mp_warmup_start` + `mp_warmup_pausetimer` + `mp_restartgame 1`
    /// cascade — see issue #58 / commit a0e1e4c8). The per-slot
    /// debounce inside <see cref="ApplyService.ScheduleStaggeredApply"/>
    /// keeps this from double-applying on normal new-rounds where
    /// EventPlayerSpawn also fires.</summary>
    private HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
    {
        try { _apply?.OnRoundPrestart(); }
        catch (Exception ex) { Log.Warn($"OnRoundPrestart: {ex.Message}"); }
        return HookResult.Continue;
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        try
        {
            if (_apply == null) return;
            var name = entity.DesignerName;
            if (!name.Contains("weapon_", StringComparison.Ordinal)) return;
            var weapon = new CBasePlayerWeapon(entity.Handle);
            _apply.OnWeaponEntitySpawn(weapon);
        }
        catch (Exception ex) { Log.Debug($"OnEntitySpawned: {ex.Message}"); }
    }

    /// <summary>Increment a StatTrak counter on each kill. Two paths:
    ///   - Real humans → PlayersStore keyed by SteamID64. The counter
    ///     lives inside the WeaponLoadout object; StatTrak is on/off
    ///     per-weapon via the >=0 / -1 sentinel.
    ///   - Managed Revive bots → BotsStore keyed by persona name. Bots
    ///     always count kills (no per-weapon enable toggle — they earn
    ///     it just by existing). Counter is independent of the live
    ///     WeaponLoadout the resolver builds, so it survives plugin
    ///     reloads and BotLoadoutResolver.Clear().
    /// Engine bot_add bots are skipped (managed=false, no persona).</summary>
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        try
        {
            var attacker = @event.Attacker;
            var victim   = @event.Userid;
            if (attacker == null || !attacker.IsValid) return HookResult.Continue;
            if (victim   == null || !victim.IsValid)   return HookResult.Continue;
            if (attacker.Slot == victim.Slot)          return HookResult.Continue;

            var pawn   = attacker.PlayerPawn.Value;
            var weapon = pawn?.WeaponServices?.ActiveWeapon.Value;
            if (weapon == null || !weapon.IsValid) return HookResult.Continue;
            int defindex = weapon.AttributeManager.Item.ItemDefinitionIndex;

            // -- Managed bot kill ------------------------------------------
            if (_slots != null && _slots.IsManaged(attacker.Slot))
            {
                if (_bots == null) return HookResult.Continue;
                // Resolver-keyed by persona name (NOT player.PlayerName) so
                // we match the same identity the loadout was built under.
                var personaName = _slots.ReadName(attacker.Slot);
                if (string.IsNullOrEmpty(personaName)) return HookResult.Continue;

                int newCount = _bots.Increment(personaName, defindex);
                // Stamp the live weapon so the kill count shows on the HUD
                // in this very life. EntityQuality=9 forces the StatTrak
                // overlay to render; FallbackStatTrak is the displayed
                // number. Otherwise the bot's StatTrak only appears the
                // *next* time the weapon entity is rebuilt (round start).
                weapon.AttributeManager.Item.EntityQuality = 9;
                weapon.FallbackStatTrak = newCount;
                // Persist immediately. The file is tiny (< 10 KB even at
                // hundreds of personas × dozens of weapons each) and disk
                // is fast — losing a single kill to a crash isn't worth
                // the autosave-batching complexity.
                _bots.Save();
                return HookResult.Continue;
            }

            // -- Engine bot_add — skip (no persistent store) --------------
            if (attacker.IsBot) return HookResult.Continue;

            // -- Real human kill ------------------------------------------
            if (_players == null) return HookResult.Continue;
            var loadout = _players.TryGet(attacker.SteamID);
            if (loadout == null) return HookResult.Continue;
            if (!loadout.Weapons.TryGetValue(defindex, out var w)) return HookResult.Continue;
            if (w.StatTrak < 0) return HookResult.Continue;   // StatTrak disabled

            w.StatTrak++;
            weapon.FallbackStatTrak = w.StatTrak;
            if ((w.StatTrak % 5) == 0) _players.Save();
        }
        catch (Exception ex) { Log.Debug($"OnPlayerDeath: {ex.Message}"); }
        return HookResult.Continue;
    }

    // -- reload command --------------------------------------------------

    private void OnReloadCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        // Server console (player == null) always allowed; in-game callers
        // must have admin flag.
        if (player != null)
        {
            if (!AdminManager.PlayerHasPermissions(player, _settings?.AdminFlag ?? "@css/root"))
            {
                cmd.ReplyToCommand("[Paints] admin only.");
                return;
            }
        }
        try
        {
            _slots?.Dispose();
            LoadEverything();
            cmd.ReplyToCommand($"[Paints] reloaded.");
            Log.Info("reloaded settings + catalogs + players");
        }
        catch (Exception ex)
        {
            cmd.ReplyToCommand($"[Paints] reload failed: {ex.Message}");
            Log.Error($"reload failed: {ex.Message}");
        }
    }
}
