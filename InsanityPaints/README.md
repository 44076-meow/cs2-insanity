# InsanityPaints

> **Alpha v0.1.0.** Weapons / knives / gloves / agents / music kits /
> pins / stickers / keychains, dynamic StatTrak, named loadout layouts,
> in-game inspect, web admin panel.

Third plugin in the [`cs2-insanity`](../) stack. Slaps custom paintkits,
knife swaps, glove models, character models (agents), music kits and
pins onto:

- **Real players** with admin permissions, picking via in-chat menus or
  the bundled web panel — selections persist per SteamID64 with
  unlimited named loadout presets (`asiimov`, `fade`, `cheap`, …) and
  one always-present `default`.
- **InsanityRevive-managed bots** — every loadout axis is derived
  deterministically from the bot's persona name (SHA-256 of UTF-8
  bytes). `Andrey_K` carries the same AK paint, same knife, same
  gloves, same agent across rounds, mapchanges, and full server
  restarts.
- **Not** `bot_add` engine bots. They stay vanilla so they're easy to
  tell apart in experiments. Toggle via `apply_to_revive_bots` if you
  want managed bots also untouched (see [Known issues](#known-issues)
  — this is currently the recommended setting).

## Required CS2 setting

`core.json` must have:

```jsonc
{
  "FollowCS2ServerGuidelines": false
}
```

Without that, CSSharp blocks the `m_iItemDefinitionIndex` /
`m_nFallbackPaintKit` writes and skins silently do nothing. See the
top-level `cs2-insanity` README for the full warning.

## How it talks to InsanityRevive

`InsanityPaints` is a **read-only** consumer of the shared mmap pool
at `/tmp/insanityrevive_fake_slots.bin`. It reads two things from the
pool:

- `managed[120]` — distinguishes Revive-managed bots from `bot_add`
  engine bots and human players.
- The 32-byte UTF-8 persona name slot for each managed bot (offset
  136 onwards in pool layout v7). This is **stable**: it's written
  synchronously by Revive before the slot is marked managed, so by
  the time `IsManaged()` returns true the name is already there.
  We use this as the seed for `BotLoadoutResolver` instead of
  `player.PlayerName` because the engine name is overwritten
  asynchronously and can briefly read `Bot01` on early ticks.

No writes — the pool is owned by Revive, and we never touch any byte
of it. If the pool file isn't there (Revive hasn't loaded yet),
`FakeSlotsReader.IsManaged` returns `false` for every slot and the
plugin treats only real humans.

## Web panel

The headline feature. Built-in HTTP server (System.Net.HttpListener,
no NuGet), serves a single-page UI from `wwwroot/`.

- **URL**: `http://127.0.0.1:27018/` by default (configurable bind
  and port in `settings.json`)
- **Auth**: bearer token, auto-generated on first run, stored in
  `settings.json` under `web_token`. The UI's login screen asks for
  it once and caches in browser localStorage.

What it does:

- **Players tab** — live roster on the left (online + stored
  loadouts), editor on the right.
  - Layout dropdown at the top: switch between named presets, `+ New`
    saves the current state as a new layout, `Rename`, `× Delete`
    (default is protected).
  - Weapons list with image previews, skin name, wear slider, seed
    number input, StatTrak checkbox + count, nametag text input. Each
    paint pick offers a **👁 Inspect** button that temporarily applies
    the paint to your live weapon and fires `+lookatweapon` for the
    in-game inspect animation.
  - Knife + paint + glove + agent slots (T / CT pickers, team-locked).
  - Sticky **Save & reload** bar at the bottom + **🎲 Randomize
    seeds** rolls a fresh seed for every weapon and glove in one
    click.
- **Bots tab** — read-only card per managed bot with their resolver
  picks (weapons, knives, gloves, agents). Lifetime StatTrak counters
  per persona live in `bots.json`.
- **Catalog tab** — browse the full 1978-paintkit catalog with search.

## Chat commands

All admin-gated by the flag in `settings.json` (default `@css/root`).

| Command   | Action                                                          |
| --------- | --------------------------------------------------------------- |
| `!ws`     | Open weapon picker → pick a weapon → pick a paintkit            |
| `!knife`  | Pick a knife defindex for the current team, then a paint        |
| `!gloves` | Pick a glove model + paint for the current team                 |

Changes apply on the next spawn or weapon pickup. Picks save to
`players.json` immediately. The full editor (layouts, agents, wear,
StatTrak, inspect) lives in the web panel — chat commands cover the
common case of "swap one paint mid-round".

## Console commands

| Command                      | Purpose                                                                                   |
| ---------------------------- | ----------------------------------------------------------------------------------------- |
| `css_insanity_paints_reload` | Hot-reload `settings.json`, all catalogs, `players.json`, `bots.json`. No restart needed. |

Server console can always call it; in-game callers need the admin
flag. Note: an `InsanityPaints` reload is roster-neutral — Paints
doesn't own bot slots, so this command never produces ghost-slot
accumulation. (Earlier docs warned about ghost slots from
`css_plugins reload InsanityRevive`; that was fixed in `fd9a478f`
by threading `hotReload` through `FakeClientManager.OnUnload`.)

## Configuration files

All under `csgo/addons/counterstrikesharp/configs/plugins/InsanityPaints/`.
The plugin creates `settings.json` (with a fresh random `web_token`)
and an empty `players.json` on first run; the catalogs are shipped
with the build.

| File                  | Purpose                                                                                                     |
| --------------------- | ----------------------------------------------------------------------------------------------------------- |
| `settings.json`       | Plugin flags, web bind / port / token, admin flag, log level, pool path                                     |
| `players.json`        | Per-SteamID64 named-layout wrappers — see below                                                             |
| `bots.json`           | Per-persona-name `{weapons: {def: kills}}` for dynamic StatTrak                                             |
| `weapons_paints.json` | `(weapon_defindex, paint, name, image, legacy_model)` rows — every paintkit Valve has ever shipped (1978)   |
| `knives.json`         | Knife defindexes + engine names (20 entries)                                                                |
| `gloves.json`         | `(defindex, paint, name, image)` glove rows (90 entries)                                                    |
| `agents.json`         | `(defindex, team, model, name, image)` character models (63 entries: 34 T + 29 CT)                          |
| `music_kits.json`     | `(defindex, name, image)` music kits (189) — applied via `MusicKitID` + `InventoryServices.MusicID`         |
| `pins.json`           | `(defindex, name, image)` collectible pins (79) — applied via `InventoryServices.Rank[5]` per team          |
| `stickers.json`       | `(defindex, name, image)` stickers (9669) — applied via `sticker slot N id` attribute injection (4 slots)   |
| `keychains.json`      | `(defindex, name, image)` weapon charms (78) — applied via `keychain slot 0 id` per weapon                  |

### players.json format

```json
{
  "76561198000000001": {
    "active": "default",
    "layouts": {
      "default": {
        "weapons": {
          "7":  { "paint": 1100, "seed": 0, "wear": 0.01, "stattrak": -1, "nametag": "" },
          "16": { "paint": 309,  "seed": 0, "wear": 0.05, "stattrak": 42, "nametag": "owned" }
        },
        "knives_t":  515,
        "knives_ct": 508,
        "gloves_t":  { "defindex": 5032, "paint": 10037, "seed": 0, "wear": 0.05 },
        "gloves_ct": { "defindex": 5034, "paint": 10037, "seed": 0, "wear": 0.05 },
        "agent_t":   4732,
        "agent_ct":  4753
      },
      "asiimov": { "...": "another full PlayerLoadout..." }
    }
  }
}
```

The v1 schema (flat `PlayerLoadout` per SteamID, no `layouts` wrapper)
is auto-migrated on load. First save rewrites in the v2 form.

### bots.json format

```json
{
  "ZywOo":  { "weapons": { "9": 42 } },
  "s1mple": { "weapons": { "9": 17, "16": 8 } }
}
```

`weapons` maps weapon defindex → kill count. Incremented on every
managed-bot kill via `OnPlayerDeath` (saved every kill — file is
tiny).

## How a bot's loadout is decided

`BotLoadoutResolver` reads the persona name from the pool slot,
SHA-256 over its UTF-8 bytes, takes the first 8 bytes as `ulong`,
modulo against the catalog length for each axis.

Why SHA-256 and not `string.GetHashCode()` — the latter is randomized
per-process in modern .NET for security, so the same bot would catch
a different skin on every server restart. The SHA-256 path is stable
across processes, .NET versions, and machines.

To break ties between axes, a small ASCII prefix is mixed in before
hashing: `w7:Andrey_K`, `kT:Andrey_K`, `gCT:Andrey_K`, `aT:Andrey_K`,
`mk:Andrey_K`, etc. So a bot can carry a distinct weapon paint /
knife / glove / agent / music kit / pin and still be deterministic.

## Apply path

The skin write itself is lifted (in spirit, not in copy) from
[Nereziel/cs2-WeaponPaints](https://github.com/Nereziel/cs2-WeaponPaints).

1. `EventPlayerSpawn` → `Server.NextFrame` so the pawn's
   `WeaponServices.MyWeapons` is populated.
2. `Listeners.OnEntitySpawned` for `weapon_*` entities →
   `Server.NextWorldUpdate`, so mid-round buys catch the right paint
   without a respawn.
3. Per weapon:
   - **Knife**: `AcceptInput("ChangeSubclass", newDefindex)`, set
     `EntityQuality = 3`, clear attribute lists.
   - Set `ItemID / ItemIDLow / ItemIDHigh` to a fresh per-apply value
     (starting at 16384, monotonically bumped).
   - Set `FallbackPaintKit / FallbackSeed / FallbackWear` and the
     `nametag` via `CustomName`.
   - Inject `set item texture prefab/seed/wear` attributes via the
     `CAttributeList_SetOrAddAttributeValueByName` gamedata signature.
     The `Fallback*` fields cover most older paintkits; the attribute
     injection covers newer paintkits that silently no-op on `Fallback*`
     alone.
   - StatTrak: humans get a toggleable counter that ticks on every kill
     by that weapon, persisted to `players.json` per-SteamID. Bots get
     an always-on counter persisted to `bots.json` per-persona; both
     resolve `EntityQuality = 9` + `FallbackStatTrak = N`.
   - **bodygroup toggle**: `AcceptInput("SetBodygroup", "body,0")` for
     modern Source 2 skins, `body,1` for legacy CS:GO-era ones. The
     `legacy_model` flag in `weapons_paints.json` picks which one.
     Without this toggle, multi-layer skins like Printstream and
     Doppler render only their base coat (we hit that bug; the apply
     looked washed out until the bodygroup was set).
4. **Gloves**: `pawn.EconGloves.ItemDefinitionIndex = …`, clear
   attributes, inject paint/seed/wear via the same signature
   (`CEconItemView` has no `Fallback*` fields, so attribute injection
   is the only path). For humans, a `lastinv` toggle plus a 0.08s
   timer plus a `SetBodygroup first_or_third_person 0 → 1` pulse
   makes the new model + paint pop in this very life. For bots, the
   pulse is **skipped** (spectator views third-person; bots never
   see their own viewmodel).
5. **Agents**: `pawn.SetModel(agent.Model)`. The model path is the
   `.vmdl` shipped with the agent (e.g.
   `agents/models/ctm_st6/ctm_st6_variantj.vmdl`). Team-locked: T
   agents only on T side, CT on CT.

The single `CAttributeList_SetOrAddAttributeValueByName` signature
ships in `gamedata/InsanityPaints.json`. CSSharp resolves it lazily
on first apply — if it doesn't match the running CS2 build, the
plugin logs a warning and falls back to `Fallback*` only.

## Inspect endpoint

The web panel's `👁 Inspect` button posts to `/api/inspect`:

```json
POST /api/inspect
{
  "steamid":   "76561199...",
  "weapon_def": 7,
  "paint":     1100,
  "seed":      435,
  "wear":      0.01,
  "stattrak":  -1,
  "nametag":   ""
}
```

The server looks up your live weapon of that defindex, applies the
candidate paint **without saving**, fires `+lookatweapon`, and after
3.5 seconds releases with `-lookatweapon`. Subsequent respawn / buy
reverts to the saved loadout. Returns 409 if the player isn't holding
that weapon.

## Build

```bash
cd InsanityPaints
dotnet build -c Release
```

Output goes to `bin/Release/net8.0/InsanityPaints.dll` plus the
`wwwroot/` tree copied alongside it. The plugin is deployed to
`csgo/addons/counterstrikesharp/plugins/InsanityPaints/` with
`gamedata/InsanityPaints.json` inside it. Catalogs and the
`settings.json` template land in
`csgo/addons/counterstrikesharp/configs/plugins/InsanityPaints/`.

CI is in `.github/workflows/build.yml` — deterministic-rebuild guard
+ release packaging.

## Refreshing the catalog

Whenever Valve ships new skins, run the importer:

```bash
./scripts/import_catalog.py
```

It pulls fresh JSON from
[ByMykel/CSGO-API](https://github.com/ByMykel/CSGO-API) and rewrites
`weapons_paints.json` / `gloves.json` / `music_kits.json` /
`pins.json` / `stickers.json` / `keychains.json` / `agents.json` with
the latest set, preserving image URLs and the `legacy_model` flag.

## Known issues

- **Animation-system SIGSEGV during mass bot respawn — FIXED 2026-05-17.**
  Symptom was SIGSEGV in `libanimationsystem.so` → `libserver.so`
  (pure-native stack) during FleetManager spawn burst or
  OnPreResetRound team-swap. Root cause: `Server.NextFrame()` in
  `OnPlayerSpawn` queued every player's ApplyAll into the same
  next frame, producing N simultaneous SetModel + SetBodygroup +
  attribute-list writes into animation system → race.
  Fix (commit `fc7d2d1`): stagger by slot index —
  `_plugin.AddTimer(slot * Server.TickInterval, ApplyAll)` —
  so at most one player's apply runs per tick.
  Discriminator history:
  - FleetSize=8, stagger NO → crash within minutes
  - FleetSize=4, stagger NO → stable (race window narrower)
  - FleetSize=8, stagger YES → stable (verified 98min normal play)
  Cost: skin pop-in is visibly deferred by ~15.6ms per slot index
  (max ~187ms for slot 12), within the spawn-flicker window so
  imperceptible.
  Edge that still crashes: manual `mp_warmup_start` +
  `mp_warmup_pausetimer` + `mp_restartgame 1` cascade. This is an
  engine-side full-game-reset path that respawns everyone outside
  the normal EventPlayerSpawn flow, so the stagger doesn't catch
  it. Workaround: don't issue that command combo by hand;
  competitive-gamemode auto-warmup transitions don't trigger it
  (verified empirically across 98min of normal play).
- **Hot-reload ghost-slot accumulation** — fixed in `fd9a478f`.
  Previously, `css_plugins reload InsanityRevive` (or a full
  `css_plugins reload`) left the old roster's `kickid` commands
  queued while the new instance's `AdoptExistingBots()` re-claimed
  the live players, doubling roster size. The fix threads a
  `hotReload` flag through `FakeClientManager.OnUnload` and skips
  the `Despawn` loop. Reloading **only** `InsanityPaints` was
  never affected — Paints does not own bot slots — so the
  previously-documented `css_plugins reload InsanityPaints`
  reproducer was a doc error. Verified post-fix: 8 → reload → 8,
  zero ghosts.
- **Doppler / Printstream phases** need `legacy_model: false` AND the
  bodygroup toggle to render their secondary design layer. Already
  handled — listed here in case the importer ever loses the flag.

## Roadmap

- **Mode-B animation crash narrowing.** Workaround `apply_to_revive_bots: false`
  was disproved (crashes still happen). Real suspect list: Hider
  `m_bFakePlayer` flip on mass spawn, AimHook `CCSBot::UpdateLookAngles`
  detour, FleetManager cascade. Need plugin-level discriminators
  (unload Hider for a session; reduce FleetSize from 8 to 4) to
  narrow further.
- **Sticker offset / wear / scale / rotation controls.** Currently
  every sticker is placed with zero offsets, full scale, no rotation.
  The attribute names are already there in the apply path; just need
  UI sliders + persistence in `WeaponLoadout`.
- **Bot-psychology-driven skin selection.** Read `BotProfile.PsychologyType`
  from Revive (would be a new `psy_type[120]` field in the pool mmap
  — layout v8 bump on Revive's side) and use it as an extra seed in
  `BotLoadoutResolver` so e.g. an aggressive bot tends toward
  red / neon AK skins. Touches both plugins.
- **Per-subsystem apply-to-bots toggles**, to let the narrowing
  experiment (above, in Known issues) re-enable bots' apply path
  one slice at a time without recompiling.
