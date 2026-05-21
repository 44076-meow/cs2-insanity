# Stage 3 + Stage 4 — Pre-implementation probe report

**Date:** 2026-05-02
**Tag at probe time:** v0.6.0.5-beta (b4adfff)
**Status:** desk research only — NO code changes, awaiting friend playtest greenlight on v0.6.0.5-beta before implementation.

Each probe answers: **is the technique feasible in CSSharp 1.0.367 + CS2 schema, and what's the cleanest API path?** No live testing performed.

---

## Probe 1 — `m_clrRender` on player models (red glow)

**Goal:** tint each managed bot's player-model red so the swarm visually reads as "those aren't friendlies anymore". Used in Stage 3 entry.

**API findings:**

- `m_clrRender` field present in CSSharp DLL string table (verified via `strings` grep).
- `RenderMode` and `SetColor` also present.
- **NO typed C# property** for `m_clrRender` in CSSharp 1.0.367 public XML — it's a schema field, accessed via dynamic `Schema.SetSchemaValue<Color>(handle, "CBaseModelEntity", "m_clrRender", color)`.
- The schema class is `CBaseModelEntity` (parent of `CCSPlayerPawn`).
- May also need `RenderMode = kRenderTransColor` (or `kRenderNormal` — depends on CS2's rendering pipeline) for the color tint to actually apply rather than just be ignored.

**Risk:** medium.

- Untested in this repo — first dynamic Schema write of a `Color24` type.
- Player models in CS2 have view-model and world-model. Red tint may apply to one but not the other.
- Some CS2 builds have been reported to ignore `m_clrRender` on networked player entities (server-side write but client renders via different path).

**Cleanest implementation candidate:**

```csharp
var pawn = c.PlayerPawn?.Value;
Schema.SetSchemaValue<Color>(pawn.Handle, "CBaseModelEntity", "m_clrRender",
    Color.FromArgb(255, 255, 0, 0));
Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
```

**Live verification required:** I-as-server cannot visually confirm — needs a connected human client to look at the bots and report "are they red?". Defer to friend playtest probe before commit.

**Fallback if `m_clrRender` doesn't apply:** spawn a `light_dynamic` entity per bot, parent it to the bot via `m_pParent`, color = red, brightness pulsing. More work (entity lifecycle to track + cleanup), but bypasses the per-pawn render-color limitation.

**Recommendation:** schedule live probe via temporary admin command (`insanity_probe_glow <slot>`) on user's next playtest, BEFORE committing Stage 3 implementation. 5-min test at user's leisure.

---

## Probe 2 — Direct grenade entity spawn (no thrower)

**Goal:** rain molotov + HE grenades onto humans during Stage 3 without needing a player to throw them. Required for "molotov rain" effect.

**API findings:**

- Schema classes confirmed in CSSharp DLL:
  - `CBaseCSGrenadeProjectile` (parent)
  - `CMolotovProjectile`
  - `CHEGrenadeProjectile`
  - `CFlashbangProjectile`
  - `CSmokeGrenadeProjectile`
  - `CDecoyProjectile`
  - `CInferno` (the fire-on-ground entity that molotovs spawn after detonation)
- Entity factory: `UTIL_CreateEntityByName` symbol present in DLL. Standard CSSharp pattern is `Utilities.CreateEntityByName<T>("class_name")` returning a typed pointer, but the public XML does not document this method directly.
- `CBaseEntity.DispatchSpawn(CEntityKeyValues)` IS documented — required to finalize entity after creation.
- Real example pattern from many CSSharp plugins (no-thrower grenade spawn):

  ```csharp
  var molotov = Utilities.CreateEntityByName<CMolotovProjectile>("molotov_projectile");
  molotov.Teleport(spawnPos, QAngle.Zero, new Vector(0, 0, -300));  // initial down velocity
  molotov.DispatchSpawn();
  ```

**Risk:** low-medium.

- Spawning projectiles without an OWNER (Thrower) entity may cause server-side warnings; some CS2 builds null-deref when a projectile's OwnerEntity is null at detonation.
- Workaround: assign one of our bots as nominal owner (`molotov.OwnerEntity = bot.Pawn` or via schema), so detonation logic has a non-null thrower.
- `molotov_throw_detonate_time` cvar likely controls airburst time. Setting it to 9999 (per spec) means "never airburst" → only ground-contact detonation. Need to verify cvar exists in CS2.

**Recommendation:** feasible. Minimal risk if we assign one of our managed bots as nominal owner. Implementation pattern is well-tested across CSSharp plugins. ~30-60 min implementation including ground-contact detection and cleanup.

---

## Probe 3 — `GiveNamedItem("weapon_c4")` on a bot

**Goal:** Stage 4 suicide bots — every 3rd bot carries C4 as their primary weapon, beep + detonate when humans visible.

**API findings:**

- `GiveNamedItem` is the same method we already use successfully for `weapon_knife` (Stage 1) and `weapon_m249` / `weapon_negev` (Stage 2). Code-path proven.
- `weapon_c4` is a string in CSSharp DLL — confirms the entity-name is recognized.
- `CCSGameRulesProxy` present — bomb-carrier flag is set on the player pawn via `m_pInGameMoneyServices` or similar gamerules hook.

**Risk:** medium.

- C4 in CS2 is normally given automatically to one T-side player at round start. Giving it manually outside that flow may:
  - Conflict with engine's "is the bomb in play?" tracking → bomb icon on HUD goes weird
  - Show a "PLANT THE BOMB" objective marker for the carrying bot
  - Auto-revoke if the bot is on CT (engine may filter giving bomb to wrong team)
- C4 is a "carry" weapon (slot 5, like knife/grenades), not "primary" (slot 1). Visual: bot will hold C4 model, NOT a rifle. May look weird for a "swarm in m249 mode".
- "Suicide vest" semantically means we trigger detonation on bot-vision-of-human, NOT on bomb-plant-then-explode. So we never call `c.PlantBomb()` — we directly spawn an `env_explosion` at bot's position when conditions met.

**Cleanest implementation candidate:**

```csharp
// Give C4 (carry slot)
c.GiveNamedItem("weapon_c4");
// Don't call PlantBomb() — we control detonation manually via env_explosion
// Mark bot's combat role
_combatState[fc.Slot] = new BotCombatState {
    ForcedWeapon = "weapon_c4", StageWhenSet = RevealStage.Stage4 };
```

**Recommendation:** feasible but visual oddities likely. Need probe-on-friend-playtest:

- Does C4 model show on bot's hand / hip without "PLANT" objective marker?
- Does engine try to revoke C4 from CT-side bots?

If revoke happens, fallback: skip the visible C4 weapon, just trigger env_explosion at bot pos when vision condition met. Bot looks normal but explodes anyway.

---

## Probe 4 — `player_hurt` PRE hook + damage zero-out

**Goal:** during Stage 3+4, prevent bot-vs-bot grenade damage and bot-vs-bot direct damage. Only humans should take damage from the apocalypse.

**API findings — JACKPOT:**

- CSSharp 1.0.367 has `Listeners.OnEntityTakeDamagePre` — modern, documented, public.
- XML documentation:
  > Called when an entity is about to take damage.
  > Returning HookResult.Handled or greater will prevent the entire damage application process.
  > Param: `entity` (the entity about to take damage)
  > Param: `info` (the damage info — CTakeDamageInfo)
- This SUPERSEDES the deprecated `VirtualFunctions.CBaseEntity_TakeDamageOldFunc` we explored in v0.6.0.2 (`BotDamagePatch.cs` is built on the OLD path; should be ported to the new Listeners path).

**Cleanest implementation candidate:**

```csharp
RegisterListener<Listeners.OnEntityTakeDamagePre>((entity, info) => {
    // entity = victim (CEntityInstance)
    // info.Attacker = damage source
    // info.Inflictor = projectile/weapon entity (e.g. CMolotovProjectile, CInferno)
    // info.Damage = float — can be modified, OR return Handled to fully cancel

    if (Stage == RevealStage.Idle) return HookResult.Continue;

    var victimSlot = ResolveSlot(entity);
    if (victimSlot == null) return HookResult.Continue;
    bool victimIsBot = _mgr.FindBySlot(victimSlot.Value) != null;
    if (!victimIsBot) return HookResult.Continue;  // human victim — let damage flow

    // Bot victim: filter damage from inferno / molotov / hegrenade / other bots
    var inflictorClass = info.Inflictor?.DesignerName;
    if (inflictorClass is "inferno" or "molotov_projectile" or "hegrenade_projectile") {
        return HookResult.Handled;  // bots immune to grenade rain
    }
    var attackerSlot = info.Attacker?.Index;
    if (attackerSlot.HasValue && _mgr.FindBySlot(attackerSlot.Value) != null) {
        return HookResult.Handled;  // bot-vs-bot direct damage blocked too
    }
    return HookResult.Continue;
});
```

**Risk:** low.

- Modern API, documented, actively maintained.
- HookResult.Handled is the canonical "prevent damage" return value.
- No version-fragility concerns vs the deprecated VirtualFunctions path.

**Recommendation:** strongly viable. v0.6.0.2's `BotDamagePatch.cs` should be REWRITTEN to use this Listener path before Stage 3 ships. Uses one event hook instead of a virtual function hook — more idiomatic.

---

## Summary table

| Probe | Feasibility | Live test needed | Risk | Implementation est |
| --- | --- | --- | --- | --- |
| 1. m_clrRender red glow | YES (untested) | yes — visual | medium | 1-2h (write + tune RenderMode) |
| 2. Grenade rain spawn | YES | minimal | low-medium | 30-60min |
| 3. weapon_c4 give | YES (visual quirks) | yes — model + HUD | medium | 1-2h + fallback path |
| 4. Damage filter PRE-hook | YES (modern API) | minimal | low | 30-45min |

**Aggregate verdict for Stage 3 + 4:** all four mechanisms are feasible. Two require friend-playtest probe before code commits (1 + 3). Two are safe to implement based on this desk research (2 + 4).

**Aggregate time estimate (post friend playtest greenlight):** 12-18h dev + smoke. Roughly matches Claude-to-Claude prompt's 15-20h estimate.

---

## Artifacts pending live verification

Before Stage 3 implementation is committed:

1. **m_clrRender visual test** — temporary `insanity_probe_glow <slot>` rcon command writes Color.Red to one bot, friend reports if visible.
2. **C4 visual test** — temporary `insanity_probe_c4 <slot>` rcon command gives weapon_c4 to one bot, friend reports model + HUD state.

These two probes can be added as zero-impact temporary admin commands in a separate `Probe.cs` file (NOT touching production code paths). Removed before any tag.

---

## Recommended next-session order (post friend playtest)

If user gives Stage 3+4 greenlight:

1. **Live probes 1 + 3** (visual tests with friend, ~30 min total).
2. **Port damage filter** to `Listeners.OnEntityTakeDamagePre`. Replace `BotDamagePatch.cs` virtual hook → modern listener. Test with current Stage 1 bot mulching as the test case (FF cvar=1 → no bot-vs-bot damage).
3. **Stage 3 entry** — team flip + glow + fleet bump 8→10 + grenade rain + damage filter live.
4. Smoke isolated, tag `v0.7.0-alpha`.
5. **Stage 4 entry** — C4 suicide bots (vision detection + beep + detonate + drop-on-death visual).
6. Smoke full Stage 0→1→2→3→4, tag `v0.7.0-beta`.

If user prefers polish first (perfect aim + slowmo from `v0.6.1` deferred), do that; this report stays valid for whenever Stage 3+4 actually ships.
