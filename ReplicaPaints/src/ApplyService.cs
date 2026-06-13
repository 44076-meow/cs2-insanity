using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace ReplicaPaints;

// The bit that actually slaps the skins onto the weapons. Lifted in
// spirit from Nereziel/cs2-WeaponPaints GivePlayerWeaponSkin, trimmed to
// the parts that don't need a gamedata signature:
//
//   1. Knife — call ChangeSubclass to swap ItemDefinitionIndex, then set
//      EntityQuality = 3, clear attribute lists.
//   2. Other weapons — EntityQuality = 0.
//   3. Set ItemID / ItemIDLow / ItemIDHigh to a per-weapon unique value
//      (starts at MinCustomItemId, bumped each apply) so the engine
//      treats every applied item as a distinct custom item.
//   4. Set FallbackPaintKit / FallbackSeed / FallbackWear.
//
// Stickers / charms / per-weapon StatTrak count rely on the
// CAttributeList_SetOrAddAttributeValueByName signature in Nereziel's
// gamedata.json — Phase 2/3 territory; we deliberately don't ship that.
public sealed class ApplyService
{
    // Knife designer-name → defindex. Mirror of the table Nereziel keeps in
    // Variables.cs (we only need it on the apply side to decide whether a
    // weapon is a knife and which knife defindex to swap to). We don't use
    // this for the swap target — we use whatever the resolved loadout
    // chose — but we use the contains("knife"|"bayonet") check.
    private static bool IsKnifeDesigner(string designerName)
    {
        return designerName.Contains("knife", StringComparison.Ordinal)
            || designerName.Contains("bayonet", StringComparison.Ordinal);
    }

    // First "custom" item-id. Anything below this is reserved by the
    // engine for stock items; using a fresh number per apply tells the
    // client this item is server-overridden custom inventory.
    private const ulong MinCustomItemId = 16384UL;
    private ulong _nextItemId = MinCustomItemId;

    private readonly Settings        _settings;
    private readonly PaintsDatabase  _db;
    private readonly PlayersStore    _players;
    private readonly BotsStore       _bots;
    private readonly BotLoadoutResolver _resolver;
    private readonly FakeSlotsReader _slots;
    // ApplyGloves needs AddTimer to space out the bodygroup toggle —
    // CSSharp exposes that through BasePlugin, not Server.
    private readonly BasePlugin      _plugin;

    // CAttributeList::SetOrAddAttributeValueByName(this, name, value) —
    // resolved from gamedata/ReplicaPaints.json. We need it for the
    // "set item texture prefab/seed/wear" attribute writes; without
    // those, gloves can't carry a paint at all (CEconItemView has no
    // Fallback* fields) and some newer weapon paintkits silently no-op
    // even on `FallbackPaintKit`. Lazily resolved on first use so the
    // plugin still loads if the signature is missing — we just log and
    // skip the attribute injection.
    private MemoryFunctionVoid<nint, string, float>? _attrSet;
    private bool _attrSetTried;

    private MemoryFunctionVoid<nint, string, float>? AttrSet()
    {
        if (_attrSet != null) return _attrSet;
        if (_attrSetTried) return null;
        _attrSetTried = true;
        try
        {
            _attrSet = new MemoryFunctionVoid<nint, string, float>(
                GameData.GetSignature("CAttributeList_SetOrAddAttributeValueByName"));
            return _attrSet;
        }
        catch (Exception ex)
        {
            Log.Warn("CAttributeList_SetOrAddAttributeValueByName signature missing — "
                   + "glove paints + some weapon paintkits will silently no-op. "
                   + $"({ex.Message})");
            return null;
        }
    }

    private void SetAttribute(nint listHandle, string name, float value)
    {
        var fn = AttrSet();
        fn?.Invoke(listHandle, name, value);
    }

    public ApplyService(
        BasePlugin plugin,
        Settings settings,
        PaintsDatabase db,
        PlayersStore players,
        BotsStore bots,
        BotLoadoutResolver resolver,
        FakeSlotsReader slots)
    {
        _plugin   = plugin;
        _settings = settings;
        _db       = db;
        _players  = players;
        _bots     = bots;
        _resolver = resolver;
        _slots    = slots;
    }

    /// <summary>Top-level entry from EventPlayerSpawn. Deferred one frame
    /// so the pawn's WeaponServices.MyWeapons is populated.</summary>
    /// <summary>One-shot "try this paint on my live weapon" path used by
    /// the web panel's Inspect button. Finds the matching weapon in the
    /// player's inventory, slaps the candidate paint on it, then fires
    /// <c>+lookatweapon</c> / <c>-lookatweapon</c> so the user sees the
    /// inspect animation with the trial skin applied.
    ///
    /// Returns:
    ///   - InspectResult.Applied         — found weapon + inspect kicked off
    ///   - InspectResult.NotHolding      — weapon not in inventory (UI asks them to switch)
    ///   - InspectResult.NotAlive        — player dead / spectating
    ///   - InspectResult.NoMatchingDef   — defindex unknown
    ///
    /// Doesn't persist anything: the next time the weapon entity is
    /// rebuilt (round start, new buy) the apply path reads the saved
    /// loadout and reverts. So this is a "try before save" preview.</summary>
    public InspectResult Inspect(CCSPlayerController player, int defindex, WeaponLoadout candidate)
    {
        if (defindex <= 0) return InspectResult.NoMatchingDef;
        if (player == null || !player.IsValid) return InspectResult.NotAlive;
        if (!player.PawnIsAlive) return InspectResult.NotAlive;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return InspectResult.NotAlive;
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return InspectResult.NotHolding;

        CBasePlayerWeapon? match = null;
        foreach (var handle in weapons)
        {
            if (!handle.IsValid || handle.Value == null || !handle.Value.IsValid) continue;
            var w = handle.Value;
            if (w.AttributeManager.Item.ItemDefinitionIndex == defindex)
            {
                match = w;
                break;
            }
        }
        if (match == null) return InspectResult.NotHolding;

        // Same field-set as ApplyPaintFields but explicit so the inspect
        // path is self-contained (it can write nondestructive overrides
        // without going through StatTrak overlay etc).
        match.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
        match.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        match.FallbackPaintKit = candidate.Paint;
        match.FallbackSeed     = candidate.Seed;
        match.FallbackWear     = candidate.Wear;
        if (candidate.StatTrak >= 0)
        {
            match.AttributeManager.Item.EntityQuality = 9;
            match.FallbackStatTrak = candidate.StatTrak;
        }
        match.AttributeManager.Item.CustomName = candidate.Nametag ?? "";
        var item = match.AttributeManager.Item;
        SetAttribute(item.NetworkedDynamicAttributes.Handle, "set item texture prefab", candidate.Paint);
        SetAttribute(item.NetworkedDynamicAttributes.Handle, "set item texture seed",   candidate.Seed);
        SetAttribute(item.NetworkedDynamicAttributes.Handle, "set item texture wear",   candidate.Wear);
        SetAttribute(item.AttributeList.Handle, "set item texture prefab", candidate.Paint);
        SetAttribute(item.AttributeList.Handle, "set item texture seed",   candidate.Seed);
        SetAttribute(item.AttributeList.Handle, "set item texture wear",   candidate.Wear);
        BumpItemId(match);
        // Same bodygroup toggle the regular apply path does — without it
        // Printstream / Doppler look just as washed out in inspect mode.
        SetMeshGroup(match, defindex, candidate.Paint);

        // Force-switch the player to the weapon they're inspecting so the
        // +lookatweapon animation actually plays on the visualised paint.
        // `use weapon_<designer>` is the engine-side way; the designer
        // name lives on CBasePlayerWeapon as DesignerName.
        var designer = match.DesignerName;
        if (!string.IsNullOrEmpty(designer))
        {
            player.ExecuteClientCommand($"use {designer}");
        }

        // Trigger the inspect animation. +lookatweapon is the half-bound
        // for the standard inspect key. We hold it for ~3.5 s — the
        // engine plays out the animation as long as the input is "down".
        player.ExecuteClientCommand("+lookatweapon");
        _plugin.AddTimer(3.5f, () =>
        {
            try
            {
                if (player.IsValid)
                    player.ExecuteClientCommand("-lookatweapon");
            }
            catch (Exception ex) { Log.Debug($"Inspect release: {ex.Message}"); }
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return InspectResult.Applied;
    }

    public void OnPlayerSpawn(CCSPlayerController player)
    {
        if (!ShouldApply(player, out _)) return;
        ScheduleStaggeredApply(player);
    }

    /// <summary>Round-restart entry point. The `mp_restartgame 1`
    /// (and `mp_warmup_start` + `mp_warmup_pausetimer` + `mp_restartgame 1`)
    /// cascade respawns everyone via the engine's full-game-reset
    /// path, which bypasses EventPlayerSpawn — so the per-slot
    /// stagger we install in <see cref="OnPlayerSpawn"/> never gets a
    /// chance to fire. <c>EventRoundPrestart</c> still fires on the
    /// cascade path, so we re-trigger the same staggered apply
    /// here. On normal new-rounds both events fire; the per-slot
    /// debounce in <see cref="ScheduleStaggeredApply"/> coalesces
    /// them so we don't double-schedule.</summary>
    public void OnRoundPrestart()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!ShouldApply(player, out _)) continue;
            ScheduleStaggeredApply(player);
        }
    }

    // Per-slot debounce window. Big enough to cover the slot 0 → slot
    // 63 stagger span (max ~1s at 64-tick) so EventRoundPrestart +
    // EventPlayerSpawn for the same round can't queue two parallel
    // bursts; small enough that the next round's apply is never
    // blocked.
    private const int ApplyDebounceTicks = 64;
    private readonly Dictionary<int, int> _lastApplyScheduledTick = new();

    private void ScheduleStaggeredApply(CCSPlayerController player)
    {
        int slot = player.Slot;
        int now = Server.TickCount;
        if (_lastApplyScheduledTick.TryGetValue(slot, out var prev)
            && now - prev < ApplyDebounceTicks)
            return;
        _lastApplyScheduledTick[slot] = now;

        // Stagger apply across ticks by slot index. Mass-respawn
        // (OnPreResetRound / boot fleet-spawn / mp_restartgame
        // cascade) lines up every bot's spawn write on one engine
        // tick — the prior Server.NextFrame queued every ApplyAll
        // into the *same* next frame, producing 8 simultaneous
        // SetModel + SetBodygroup + attribute-list writes through
        // animation system. That race window is what crashes
        // libanimationsystem.so (mode-B). By spreading 1 tick per
        // slot we keep at most one player's apply per animation tick
        // — race window closes.
        //
        // Cost: skins for slot N visibly pop in N*15.6ms after spawn.
        // With 64 slot indexes max = ~1s worst case. For typical
        // 4-12 active players the spread is 60-180ms which is
        // imperceptible vs the spawn flicker itself.
        float delay = slot * Server.TickInterval;
        _plugin.AddTimer(delay, () =>
        {
            try { ApplyAll(player); }
            catch (Exception ex) { Log.Warn($"ApplyAll: {ex.Message}"); }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>Entry point for newly-spawned weapon entities (so a buy
    /// mid-round picks up the right skin without a full respawn).
    ///
    /// Queued, NOT applied inline: the old Server.NextWorldUpdate path
    /// bunched every weapon of a freeze-time buy burst (9 bots × full
    /// loadout ≈ 30-45 entities within a second) into the same world
    /// update — the same all-writes-on-one-tick animation race the
    /// per-slot spawn stagger (fc7d2d1) closed for ApplyAll. Three live
    /// GPFs in libanimationsystem at identical offsets, all at buy
    /// moments, 2026-06-13. The queue drains at WeaponAppliesPerTick
    /// from the plugin's OnTick; the ≥1-tick delay also preserves the
    /// owner-not-yet-set grace the NextWorldUpdate hop used to give.</summary>
    public void OnWeaponEntitySpawn(CBasePlayerWeapon weapon)
    {
        if (!weapon.IsValid) return;
        _weaponApplyQueue.Enqueue((weapon, Server.TickCount));
    }

    private readonly Queue<(CBasePlayerWeapon Weapon, int EnqueueTick)> _weaponApplyQueue = new();
    private const int WeaponAppliesPerTick    = 2;
    private const int WeaponApplyTimeoutTicks = 128;  // 2s — entries whose entity died drop out

    /// <summary>Per-tick drain — at most WeaponAppliesPerTick actual
    /// applies reach the animation system per tick. Skipped/stale
    /// entries don't consume budget.</summary>
    public void DrainWeaponApplyQueue()
    {
        int budget = WeaponAppliesPerTick;
        while (budget > 0 && _weaponApplyQueue.Count > 0)
        {
            var (weapon, enqueueTick) = _weaponApplyQueue.Dequeue();
            if (Server.TickCount - enqueueTick > WeaponApplyTimeoutTicks) continue;
            try
            {
                if (!weapon.IsValid) continue;
                var ownerHandle = weapon.OwnerEntity;
                if (!ownerHandle.IsValid) continue;
                var pawn = new CCSPlayerPawn(ownerHandle.Value!.Handle);
                if (!pawn.IsValid || pawn.Controller.Value == null) continue;
                var controller = new CCSPlayerController(pawn.Controller.Value.Handle);
                if (!ShouldApply(controller, out _)) continue;
                ApplyToWeapon(controller, weapon);
                budget--;
            }
            catch (Exception ex) { Log.Debug($"DrainWeaponApplyQueue: {ex.Message}"); }
        }
    }

    // -- internals -------------------------------------------------------

    /// <summary>Decide whether we apply skins to this player at all.
    /// `kind` distinguishes humans from Replica-managed bots so the apply
    /// path can pick the right loadout source. Engine `bot_add` bots are
    /// rejected here — they're bots but not in Replica's managed-slot
    /// table.</summary>
    private bool ShouldApply(CCSPlayerController? player, out PlayerKind kind)
    {
        kind = PlayerKind.Skip;
        if (player == null || !player.IsValid || player.IsHLTV) return false;
        if (player.Connected != PlayerConnectedState.Connected) return false;

        // Managed Replica slot is the truthy signal — ReplicaHider flips
        // m_bFakePlayer to zero on managed slots, which makes
        // `player.IsBot` return false even though they're our bots.
        // We check the pool first; only fall back to IsBot to filter out
        // engine-spawned `bot_add` clients (managed=false, IsBot=true).
        if (_slots.IsManaged(player.Slot))
        {
            if (!_settings.ApplyToReviveBots) return false;
            kind = PlayerKind.ReviveBot;
            return true;
        }
        if (player.IsBot) return false;  // engine bot — skip
        kind = PlayerKind.Human;
        return true;
    }

    private void ApplyAll(CCSPlayerController player)
    {
        if (!ShouldApply(player, out var kind)) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        // Agent first — SetModel resets a bunch of pawn-side state, so we
        // want it to happen before glove + weapon writes that rely on the
        // pawn's EconGloves and WeaponServices being in their post-spawn
        // shape. Cheap if no agent is set (just early-return).
        ApplyAgent(player, pawn, kind);
        ApplyMusicKit(player, kind);
        ApplyPin(player, kind);
        ApplyGloves(player, pawn, kind);

        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return;
        foreach (var handle in weapons)
        {
            if (!handle.IsValid || handle.Value == null || !handle.Value.IsValid) continue;
            ApplyToWeapon(player, handle.Value);
        }
    }

    /// <summary>Apply the player's selected music kit. CS2 stores the
    /// kit ID in two mirrored fields:
    ///   - <c>CCSPlayerController.MusicKitID</c> / <c>m_iMusicKitID</c>
    ///   - <c>InventoryServices.MusicID</c>
    ///
    /// Display-only: the kit NAME shows up on the scoreboard / MVP
    /// screen from this. <b>Audio is client-side and gated by Steam
    /// inventory ownership</b> — observer's CS2 client plays the
    /// anthem only if the observer owns the kit defindex locally. So
    /// for bots: silence unless the viewer happens to own that
    /// random kit. For self: the client overrides our write with the
    /// viewer's own purchased kit (verified empirically 2026-05-17).
    /// Server can't change this — it's a CS2 architectural choice,
    /// presumably to prevent random servers from spamming arbitrary
    /// music kits at clients who don't own them.
    ///
    /// We DON'T write <c>m_bMvpNoMusic</c> or <c>m_iMusicKitMVPs</c> —
    /// tried both, neither unlocks audio playback for non-owned kits.
    /// Nereziel intentionally left those commented out for the same
    /// reason.</summary>
    private void ApplyMusicKit(CCSPlayerController player, PlayerKind kind)
    {
        int kitId = ChooseMusicKit(player, kind);
        if (kitId <= 0) return;
        try
        {
            player.MusicKitID = kitId;
            if (player.InventoryServices != null)
            {
                player.InventoryServices.MusicID = (ushort)kitId;
            }
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
        }
        catch (Exception ex) { Log.Debug($"ApplyMusicKit: {ex.Message}"); }
    }

    /// <summary>Apply the player's collectible pin to the scoreboard /
    /// MVP screen. CS2 stores pins in InventoryServices.Rank[5] (the
    /// pin slot specifically — Rank[0..4] are for ranks / medals).
    /// Per-team because the picker offers a separate pin for T and CT
    /// sides.</summary>
    private void ApplyPin(CCSPlayerController player, PlayerKind kind)
    {
        var team = (CsTeam)player.TeamNum;
        if (team is CsTeam.None or CsTeam.Spectator) return;
        int pinId = ChoosePin(player, kind, team);
        if (pinId <= 0 || player.InventoryServices == null) return;
        try
        {
            player.InventoryServices.Rank[5] = (MedalRank_t)pinId;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
        }
        catch (Exception ex) { Log.Debug($"ApplyPin: {ex.Message}"); }
    }

    private int ChooseMusicKit(CCSPlayerController player, PlayerKind kind)
    {
        if (kind == PlayerKind.Human)
        {
            var pl = _players.TryGet(player.SteamID);
            return pl?.MusicKit ?? 0;
        }
        return _resolver.Resolve(BotSeed(player)).MusicKit;
    }

    private int ChoosePin(CCSPlayerController player, PlayerKind kind, CsTeam team)
    {
        if (kind == PlayerKind.Human)
        {
            var pl = _players.TryGet(player.SteamID);
            if (pl == null) return 0;
            return team == CsTeam.Terrorist ? pl.PinT : pl.PinCT;
        }
        var resolved = _resolver.Resolve(BotSeed(player));
        return team == CsTeam.Terrorist ? resolved.PinT : resolved.PinCT;
    }

    /// <summary>Set the player's character model via the agent's .vmdl path.
    /// Per-team — agents are team-locked, so we pick AgentT for T side and
    /// AgentCT for CT side. SetModel is the same call humans use for any
    /// model change; CS2 streams the .vmdl in async and the client picks it
    /// up automatically on the next frame.</summary>
    private void ApplyAgent(CCSPlayerController player, CCSPlayerPawn pawn, PlayerKind kind)
    {
        var team = (CsTeam)player.TeamNum;
        if (team is CsTeam.None or CsTeam.Spectator) return;

        int defindex = ChooseAgent(player, kind, team);
        if (defindex <= 0) return;

        var agent = _db.AgentByDef(defindex);
        if (agent == null || string.IsNullOrEmpty(agent.Model)) return;

        // Team-mismatch safety: even though humans pick per-team, a freshly
        // joined player could have a saved AgentCT entry but be on T this
        // round. We *would* still apply it via the team-correct slot only
        // (ChooseAgent already does the team split), so this check is a
        // belt-and-suspenders against malformed catalog rows.
        if (agent.Team == "T"  && team != CsTeam.Terrorist)         return;
        if (agent.Team == "CT" && team != CsTeam.CounterTerrorist)  return;

        // #11 crash fix: SetModel frees+rebuilds the pawn's animation graph.
        // ApplyAll re-runs on EVERY round-prestart (plus the staggered spawn
        // path), so without this guard we re-SetModel the SAME agent model
        // onto the SAME living pawn round after round — each one a fresh
        // free+rebuild the engine can crash on when its animation tree walk
        // races the free (the use-after-free in libanimationsystem confirmed
        // by 17 minidumps, owner-confirmed culprit). The model doesn't change
        // round-to-round, so set it ONCE per pawn instance and never churn it
        // again. Keyed by the native pawn handle (unique per spawn); a
        // recycled handle at worst costs one cosmetic re-set, never a crash.
        // Prior fixes (#58/#199/staggers) only moved WHEN SetModel ran — this
        // cuts HOW MANY, the angle not tried before.
        var key = pawn.Handle;
        if (_agentModelByPawn.TryGetValue(key, out var cur) && cur == agent.Model)
            return;

        try
        {
            pawn.SetModel(agent.Model);
            _agentModelByPawn[key] = agent.Model;
            if (_agentModelByPawn.Count > 256) PruneAgentModelMap(key, agent.Model);
        }
        catch (Exception ex)
        {
            Log.Debug($"ApplyAgent SetModel('{agent.Model}'): {ex.Message}");
        }
    }

    // Per-pawn agent-model memo for the #11 redundant-SetModel guard.
    private readonly Dictionary<nint, string> _agentModelByPawn = new();
    private void PruneAgentModelMap(nint keep, string keepVal)
    {
        _agentModelByPawn.Clear();
        _agentModelByPawn[keep] = keepVal;
    }

    private int ChooseAgent(CCSPlayerController player, PlayerKind kind, CsTeam team)
    {
        if (kind == PlayerKind.Human)
        {
            var pl = _players.TryGet(player.SteamID);
            if (pl == null) return 0;
            return team == CsTeam.Terrorist ? pl.AgentT : pl.AgentCT;
        }
        var resolved = _resolver.Resolve(BotSeed(player));
        return team == CsTeam.Terrorist ? resolved.AgentT : resolved.AgentCT;
    }

    private void ApplyToWeapon(CCSPlayerController player, CBasePlayerWeapon weapon)
    {
        if (!ShouldApply(player, out var kind)) return;
        var team = (CsTeam)player.TeamNum;
        if (team is CsTeam.None or CsTeam.Spectator) return;

        bool isKnife = IsKnifeDesigner(weapon.DesignerName);

        if (isKnife)
        {
            if (!_settings.EnableKnives) return;
            int chosenKnifeDef = ChooseKnifeDefindex(player, kind, team);
            if (chosenKnifeDef <= 0) return;

            if (weapon.AttributeManager.Item.ItemDefinitionIndex != (ushort)chosenKnifeDef)
            {
                // ChangeSubclass swaps the underlying weapon class. Without
                // this the defindex change alone leaves model + viewmodel
                // out of sync.
                weapon.AcceptInput("ChangeSubclass", value: chosenKnifeDef.ToString());
            }
            weapon.AttributeManager.Item.ItemDefinitionIndex = (ushort)chosenKnifeDef;
            weapon.AttributeManager.Item.EntityQuality       = 3;
            weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
            weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();

            var knifePaint = ChooseKnifePaint(player, kind, chosenKnifeDef);
            if (knifePaint != null)
            {
                ApplyPaintFields(weapon, knifePaint);
                ApplyStickersAndKeychain(weapon, knifePaint);
            }

            // For bots, the knife defindex picked above is the StatTrak
            // key — knives don't share a generic "knife" counter, each
            // model has its own (M9 Bayonet kills don't count toward
            // Karambit). Note this overrides the EntityQuality=3 set
            // earlier; StatTrak requires 9.
            OverlayBotStatTrak(player, kind, weapon, chosenKnifeDef);

            BumpItemId(weapon);
            SetEcon(weapon, player);
            // Doppler / Gamma Doppler / Marble Fade phases need
            // body=0 the same way Printstream does — they're modern
            // skins with the secondary design layer baked into the new
            // mesh. body=1 for older knife paints (Crimson Web, Slaughter).
            SetMeshGroup(weapon, chosenKnifeDef, knifePaint?.Paint ?? 0);
            UpdatedStateChanged(player, weapon);
            return;
        }

        if (!_settings.EnableWeapons) return;
        int defindex = weapon.AttributeManager.Item.ItemDefinitionIndex;
        var paint = ChooseWeaponPaint(player, kind, defindex);
        if (paint == null) return;

        weapon.AttributeManager.Item.EntityQuality = 0;
        weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
        weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();

        ApplyPaintFields(weapon, paint);
        ApplyStickersAndKeychain(weapon, paint);
        OverlayBotStatTrak(player, kind, weapon, defindex);
        BumpItemId(weapon);
        SetEcon(weapon, player);
        SetMeshGroup(weapon, defindex, paint.Paint);
        UpdatedStateChanged(player, weapon);
    }

    /// <summary>Write the 4 sticker slots + 1 keychain slot onto the
    /// weapon's attribute list. Each non-zero entry produces a sticker
    /// at the matching defindex, with default placement / scale /
    /// rotation. Cosmetic offsets and rotation are out of scope for
    /// the first cut — admins picking "AK-47 | Vulcan with a Howling
    /// Dawn slot 0 sticker" is the common case; fine-tuning where on
    /// the gun it sits can come later.
    ///
    /// Lifted from Nereziel's SetStickers / SetKeychain (the attribute
    /// names + ViewAsFloat int→float reinterpret are the only stable
    /// way to push these through the engine's econ pipeline).</summary>
    private void ApplyStickersAndKeychain(CBasePlayerWeapon weapon, WeaponLoadout loadout)
    {
        var handle = weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle;
        var placements = loadout.StickerPlacements;
        if (loadout.Stickers != null)
        {
            for (int slot = 0; slot < loadout.Stickers.Length && slot < 4; slot++)
            {
                int stickerDef = loadout.Stickers[slot];
                if (stickerDef <= 0) continue;
                // Issue #60 — placement from loadout when present, canonical
                // defaults (0/0/0/1/0) otherwise. Bots get non-uniform values
                // via BotLoadoutResolver; human loadouts default to canonical
                // until the web UI exposes per-slot fields.
                var p = placements != null && slot < placements.Length
                    ? placements[slot] ?? DefaultStickerPlacement
                    : DefaultStickerPlacement;
                // ViewAsFloat reinterprets the int's bits as a float — the
                // engine stores attribute values as float-only, but the
                // semantic for "sticker id" is uint, so we smuggle it.
                SetAttribute(handle, $"sticker slot {slot} id",       ViewAsFloat((uint)stickerDef));
                SetAttribute(handle, $"sticker slot {slot} offset x", p.OffsetX);
                SetAttribute(handle, $"sticker slot {slot} offset y", p.OffsetY);
                SetAttribute(handle, $"sticker slot {slot} wear",     p.Wear);
                SetAttribute(handle, $"sticker slot {slot} scale",    p.Scale);
                SetAttribute(handle, $"sticker slot {slot} rotation", p.Rotation);
            }
        }
        if (loadout.Keychain > 0)
        {
            var kp = loadout.KeychainPlacement ?? DefaultKeychainPlacement;
            SetAttribute(handle, "keychain slot 0 id",       ViewAsFloat((uint)loadout.Keychain));
            SetAttribute(handle, "keychain slot 0 offset x", kp.OffsetX);
            SetAttribute(handle, "keychain slot 0 offset y", kp.OffsetY);
            SetAttribute(handle, "keychain slot 0 offset z", kp.OffsetZ);
            SetAttribute(handle, "keychain slot 0 seed",     ViewAsFloat((uint)kp.Seed));
        }
    }

    // Cached canonical defaults — Scale=1, everything else 0. Matches the
    // legacy hardcoded behaviour for loadouts without explicit placement,
    // and avoids per-call allocation in the hot apply path.
    private static readonly StickerPlacement   DefaultStickerPlacement   = new() { Scale = 1f };
    private static readonly KeychainPlacement  DefaultKeychainPlacement  = new();

    private static float ViewAsFloat(uint v) => BitConverter.UInt32BitsToSingle(v);

    /// <summary>Toggle the weapon's bodygroup so the engine renders the
    /// right mesh for the applied paintkit. CS2 weapons ship with two
    /// bodygroups:
    ///   - body=0 → modern Source 2 mesh with the secondary design layer
    ///     baked in. Printstream's ink overlay, Doppler's gem phase,
    ///     Marble Fade's pattern flow — all live in this layer.
    ///   - body=1 → legacy CS:GO-era mesh used by pre-CS2 paint kits.
    /// PaintsDatabase.IsLegacyPaint picks the right toggle from the
    /// `legacy_model` flag baked into the catalog at import time. Without
    /// this call, modern skins like Printstream render only their base
    /// coat and look washed out (this is what bit us on 2026-05-16).
    /// Lifted from Nereziel's UpdateWeaponMeshGroupMask.</summary>
    private void SetMeshGroup(CBasePlayerWeapon weapon, int defindex, int paint)
    {
        bool legacy = _db.IsLegacyPaint(defindex, paint);
        try
        {
            weapon.AcceptInput("SetBodygroup", value: $"body,{(legacy ? 1 : 0)}");
        }
        catch (Exception ex) { Log.Debug($"SetMeshGroup body={(legacy?1:0)}: {ex.Message}"); }
    }

    /// <summary>For Replica bots, look up the persistent kill count from
    /// BotsStore and stamp it on the weapon so the StatTrak HUD shows
    /// the bot's lifetime kills. Humans get StatTrak through the explicit
    /// per-weapon toggle in their loadout (handled by ApplyPaintFields);
    /// bots get it always-on, derived from real kills. Knife defindex
    /// path uses the same overlay — the bot's M9 Bayonet counter ticks
    /// just like their AK counter.</summary>
    private void OverlayBotStatTrak(CCSPlayerController player, PlayerKind kind,
                                    CBasePlayerWeapon weapon, int defindex)
    {
        if (kind != PlayerKind.ReviveBot) return;
        var personaName = _slots.ReadName(player.Slot);
        if (string.IsNullOrEmpty(personaName)) return;
        int kills = _bots.GetKills(personaName, defindex);
        weapon.AttributeManager.Item.EntityQuality = 9;
        weapon.FallbackStatTrak                    = kills;
    }

    private void ApplyGloves(CCSPlayerController player, CCSPlayerPawn pawn, PlayerKind kind)
    {
        if (!_settings.EnableGloves) return;
        var team = (CsTeam)player.TeamNum;
        if (team is CsTeam.None or CsTeam.Spectator) return;

        var glove = ChooseGloves(player, kind, team);
        if (glove == null || glove.Defindex == 0) return;

        var item = pawn.EconGloves;
        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        item.AttributeList.Attributes.RemoveAll();

        // Two apply variants:
        //   - Humans: full Nereziel dance with viewmodel "lastinv" toggle +
        //     SetBodygroup pulse so the new model pops in this very life.
        //   - Bots: bare write. Bots don't have a viewmodel rendered on
        //     anyone's screen (spectators see them in third person, which
        //     reads ItemDefinitionIndex directly), so we skip the pulse +
        //     lastinv entirely. This also matters for *crash* reasons:
        //     8 bots × per-spawn SetBodygroup pulse = a storm of queued
        //     entity-IO inputs through libanimationsystem, and the 0.2s
        //     "back to 1" timer kept firing on respawned/dead pawns. The
        //     21:38 SIGSEGV on 2026-05-16 was exactly that path (stack
        //     was libanimationsystem.so → libserver.so → coreclr signal).
        bool fancy = (kind == PlayerKind.Human);
        if (fancy) player.ExecuteClientCommand("lastinv");

        _plugin.AddTimer(0.08f, () =>
        {
            try
            {
                if (!player.IsValid || !player.PawnIsAlive) return;
                // Re-fetch the pawn through the live handle: the one we
                // captured 80 ms ago might point at a freed entity by now.
                var livePawn = player.PlayerPawn.Value;
                if (livePawn == null || !livePawn.IsValid) return;
                var liveItem = livePawn.EconGloves;
                if (liveItem == null) return;

                liveItem.ItemDefinitionIndex = (ushort)glove.Defindex;

                // Bump ItemID so the client treats this as a brand-new
                // econ item — without it the inventory layer may keep
                // the cached model + attributes from the previous slot.
                var id = _nextItemId++;
                liveItem.ItemID     = id;
                liveItem.ItemIDLow  = (uint)(id & 0xFFFFFFFFu);
                liveItem.ItemIDHigh = (uint)(id >> 32);

                liveItem.NetworkedDynamicAttributes.Attributes.RemoveAll();
                // CEconItemView has no Fallback* fields — the only way
                // to give gloves a paint is to push the three attributes
                // into its attribute lists.
                SetAttribute(liveItem.NetworkedDynamicAttributes.Handle, "set item texture prefab", glove.Paint);
                SetAttribute(liveItem.NetworkedDynamicAttributes.Handle, "set item texture seed",   glove.Seed);
                SetAttribute(liveItem.NetworkedDynamicAttributes.Handle, "set item texture wear",   glove.Wear);

                liveItem.AttributeList.Attributes.RemoveAll();
                SetAttribute(liveItem.AttributeList.Handle, "set item texture prefab", glove.Paint);
                SetAttribute(liveItem.AttributeList.Handle, "set item texture seed",   glove.Seed);
                SetAttribute(liveItem.AttributeList.Handle, "set item texture wear",   glove.Wear);
                liveItem.Initialized = true;

                if (!fancy) return;  // bot path stops here

                // Force a full glove-mesh refresh for humans: toggle the
                // bodygroup that controls "first or third person" view.
                // The 0→1 pulse is what makes the new model + paint
                // actually pop in this life.
                player.ExecuteClientCommand("lastinv");
                livePawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
                _plugin.AddTimer(0.2f, () =>
                {
                    try
                    {
                        if (!player.IsValid || !player.PawnIsAlive) return;
                        var p2 = player.PlayerPawn.Value;
                        if (p2 == null || !p2.IsValid) return;
                        p2.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
                    }
                    catch (Exception ex) { Log.Debug($"ApplyGloves bodygroup back: {ex.Message}"); }
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
            catch (Exception ex) { Log.Debug($"ApplyGloves apply: {ex.Message}"); }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ApplyPaintFields(CBasePlayerWeapon weapon, WeaponLoadout p)
    {
        weapon.FallbackPaintKit = p.Paint;
        weapon.FallbackSeed     = p.Seed;
        weapon.FallbackWear     = p.Wear;
        // StatTrak with a fixed count, when configured. -1 means no
        // StatTrak. The count itself doesn't tick in this phase — see
        // README for the Phase-2 plan.
        if (p.StatTrak >= 0)
        {
            weapon.AttributeManager.Item.EntityQuality = 9;
            weapon.FallbackStatTrak                    = p.StatTrak;
        }
        else
        {
            weapon.FallbackStatTrak = -1;
        }
        // Nametag — empty string = no tag. CEconItemView.CustomName takes
        // a string and the engine handles truncation if it's too long.
        weapon.AttributeManager.Item.CustomName = p.Nametag ?? "";

        // Push the same paint/seed/wear into both attribute lists. The
        // Fallback* fields above are enough for older paintkits, but
        // newer ones (and anything that wants to be glove-style indexed)
        // silently no-op unless the attribute list also carries them.
        // SetAttribute is a no-op if the gamedata signature failed to
        // resolve — Phase 1 still works for the common cases without it.
        var item = weapon.AttributeManager.Item;
        SetAttribute(item.NetworkedDynamicAttributes.Handle, "set item texture prefab", p.Paint);
        SetAttribute(item.NetworkedDynamicAttributes.Handle, "set item texture seed",   p.Seed);
        SetAttribute(item.NetworkedDynamicAttributes.Handle, "set item texture wear",   p.Wear);
        SetAttribute(item.AttributeList.Handle,              "set item texture prefab", p.Paint);
        SetAttribute(item.AttributeList.Handle,              "set item texture seed",   p.Seed);
        SetAttribute(item.AttributeList.Handle,              "set item texture wear",   p.Wear);
    }

    private void BumpItemId(CBasePlayerWeapon weapon)
    {
        var id = _nextItemId++;
        var item = weapon.AttributeManager.Item;
        item.ItemID     = id;
        item.ItemIDLow  = (uint)(id & 0xFFFFFFFFu);
        item.ItemIDHigh = (uint)(id >> 32);
    }

    private static void SetEcon(CBasePlayerWeapon weapon, CCSPlayerController player)
    {
        // AccountID is normally the owner's Steam account number. For real
        // players we plug in the bottom 32 bits of SteamID64; for bots
        // it's zero, which the client tolerates.
        unchecked { weapon.AttributeManager.Item.AccountID = (uint)player.SteamID; }
    }

    private static void UpdatedStateChanged(CCSPlayerController player, CBasePlayerWeapon weapon)
    {
        // Empty by design — CSSharp's schema layer doesn't recognise the
        // Fallback* fields as networked (they live deeper in the engine
        // proto), so calling SetStateChanged on them just spams the
        // "not networked" warning and changes nothing. The Fallback*
        // assignments above replicate through the regular econ-item
        // state update cycle without our help.
    }

    // -- loadout source dispatch ----------------------------------------

    private WeaponLoadout? ChooseWeaponPaint(CCSPlayerController player, PlayerKind kind, int defindex)
    {
        if (kind == PlayerKind.Human)
        {
            var pl = _players.TryGet(player.SteamID);
            if (pl == null) return null;
            return pl.Weapons.TryGetValue(defindex, out var w) ? w : null;
        }
        var resolved = _resolver.Resolve(BotSeed(player));
        return resolved.Weapons.TryGetValue(defindex, out var bw) ? bw : null;
    }

    private int ChooseKnifeDefindex(CCSPlayerController player, PlayerKind kind, CsTeam team)
    {
        if (kind == PlayerKind.Human)
        {
            var pl = _players.TryGet(player.SteamID);
            if (pl == null) return 0;
            return team == CsTeam.Terrorist ? pl.KnifeT : pl.KnifeCT;
        }
        var resolved = _resolver.Resolve(BotSeed(player));
        return team == CsTeam.Terrorist ? resolved.KnifeT : resolved.KnifeCT;
    }

    private WeaponLoadout? ChooseKnifePaint(CCSPlayerController player, PlayerKind kind, int knifeDefindex)
    {
        if (kind == PlayerKind.Human)
        {
            var pl = _players.TryGet(player.SteamID);
            if (pl == null) return null;
            return pl.Weapons.TryGetValue(knifeDefindex, out var w) ? w : null;
        }
        var resolved = _resolver.Resolve(BotSeed(player));
        return resolved.Weapons.TryGetValue(knifeDefindex, out var bw) ? bw : null;
    }

    private GloveLoadout? ChooseGloves(CCSPlayerController player, PlayerKind kind, CsTeam team)
    {
        if (kind == PlayerKind.Human)
        {
            var pl = _players.TryGet(player.SteamID);
            if (pl == null) return null;
            return team == CsTeam.Terrorist ? pl.GlovesT : pl.GlovesCT;
        }
        var resolved = _resolver.Resolve(BotSeed(player));
        return team == CsTeam.Terrorist ? resolved.GlovesT : resolved.GlovesCT;
    }

    /// <summary>Stable seed for a Replica bot. Replica writes the persona
    /// name into the pool synchronously before pre-marking the slot as
    /// managed, so by the time IsManaged returns true the pool entry is
    /// already there. PlayerName, by contrast, is overwritten via an
    /// async engine call and on early ticks can still be the engine
    /// default (`Bot01`, …). Using the pool value keeps a bot's loadout
    /// consistent from its very first spawn.</summary>
    private string BotSeed(CCSPlayerController player)
    {
        var poolName = _slots.ReadName(player.Slot);
        if (!string.IsNullOrEmpty(poolName)) return poolName;
        return player.PlayerName;  // last-resort fallback
    }
}

internal enum PlayerKind { Skip, Human, ReviveBot }

/// <summary>Outcome of <see cref="ApplyService.Inspect"/>. Encodes the
/// not-found-in-inventory case so the web panel can show the user a
/// helpful "switch to this weapon first" hint instead of a generic
/// 500.</summary>
public enum InspectResult
{
    Applied,
    NotHolding,
    NotAlive,
    NoMatchingDef,
}
