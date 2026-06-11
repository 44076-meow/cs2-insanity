<div align="center">

# *INSANITY* → Replicá

**A two-plugin engineering experiment for Counter-Strike 2 dedicated servers.**
Gives engine bots a more lifelike scoreboard presence — synthetic SteamID64s,
curated persona names, jittered ping, no `BOT` glyph — so practice and
low-population servers feel populated instead of empty.

[![build](https://github.com/44076-meow/replica/actions/workflows/build.yml/badge.svg)](https://github.com/44076-meow/replica/actions/workflows/build.yml)
[![codeql](https://github.com/44076-meow/replica/actions/workflows/codeql.yml/badge.svg)](https://github.com/44076-meow/replica/actions/workflows/codeql.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![stage: early alpha](https://img.shields.io/badge/stage-early%20alpha-orange)
![CS2 dedicated](https://img.shields.io/badge/target-CS2%20dedicated%20server-informational)
![lang: C%23%20%2B%20C%2B%2B](https://img.shields.io/badge/lang-C%23%20%2B%20C%2B%2B-success)

</div>

> [!NOTE]
> **Development is currently on hold. For further details, please refer to the `Discussions` section.**

> [!NOTE]
> The project has been renamed; **insanity is now Replicá.** <br>
> "Insanity" was just for reveal until now, whereas the idea behind the project is replicating professional players — virtual players indistinguishable from humans. That's what the rename means. <br>
> The rename brings: repository cs2-insanity → replica, new logo, and `replica_*` → `replica_*`. <br>
> Razones: es un buen nombre y suena a Pokémon. ¡Replicá!

---

> [!WARNING]
> **Early alpha — expect breakage.**
> This is a research project. Interfaces, plugin layout, mmap protocol, schema
> offsets, and detour anchors all churn between tags. CS2 engine updates *will*
> break things (and have, several times — see the `v0.6.0.x-beta` tag stream).
> No backwards-compatibility guarantees, no migration scripts, no support
> commitment. **If you run this on a server, plan on rebuilding from `main`
> after every CS2 update and accept that telemetry/log schemas may shift
> without notice.**

> [!IMPORTANT]
> **Intended use — your own dedicated server only.**
> Private practice ranges, gun-game servers, training boxes, internal LAN.
> **Do not** run on official Valve matchmaking, public ranked servers, or any
> server where players reasonably expect to distinguish humans from bots.
> See the [Disclaimer](#disclaimer) at the bottom.

---

## Why it exists

The visible feature — bots that look like players — is the *demo*. The
interesting work is the plumbing underneath:

- A **CSSharp (.NET 8) plugin** and a **Metamod:Source (C++) plugin** cooperating
  over a tiny shared mmap.
- An **inline detour** into `libserver.so::CCSBot::UpdateLookAngles` to take
  ownership of per-tick aim from the engine's bot AI.
- **`ProcessUsercmds`** interception for per-bot input shaping.
- **Schema patching** on `m_bFakePlayer` post-`OnClientConnected`, before the
  engine's userinfo broadcast leaves the server — that's what hides the `BOT`
  icon and renders a real ping in the scoreboard column.
- A **deterministic CI pipeline** that builds the C# DLL twice and compares
  `sha256` to enforce reproducibility — closing the "DLL drift" diagnosis hole.

## Architecture

| Plugin | Side | Role |
| --- | --- | --- |
| **`Replica/`** | CSSharp · CounterStrikeSharp · .NET 8 | Lifecycle owner. Spawns bots via `bot_add`, overwrites identity (name, SteamID, profile, ping), JSONL telemetry, `ProcessUsercmds` detour, mapchange survival. Source of truth for the shared pool. |
| **`ReplicaHider/`** | Metamod:Source · C++ | Sits early in the engine callback chain. On `OnClientConnected` post-hook, flips `m_bFakePlayer = 0` for slots marked in the shared pool — *before* the engine's userinfo broadcast leaves the server. |

### Shared pool — 132-byte mmap

The two halves communicate through `/tmp/replica_fake_slots.bin`:

```text
[ 0.. 3]  magic    = 0x46534E49 ('INSF')
[ 4.. 7]  version  = 1
[ 8..11]  reserved
[12..131] slots[120]   uint8: 0 = unmanaged, 1 = ours
```

CSSharp owns the writes (pre-mark in `Spawn()`, un-mark in `Despawn()`).
C++ only reads.

### SteamID generation

SteamID64s for managed bots are minted from a **reserved synthetic range**
(`76561198_900_000_000` .. `76561198_999_999_999`), seeded from the session ID.
This sits above the prefix range where real Steam accounts have ever been
allocated, so synthetic IDs cannot collide with a live account. There is no
facility for using real Steam IDs.

### Selectivity

`bot_quota 0` in `server.cfg` — the engine never auto-fills bots. Every
fake-client that exists came through `FakeClientManager.Spawn()`, was
pre-marked in the pool, and gets its `BOT` icon flipped off in the post-OCC
chain. Manual `bot_add` from rcon is *not* marked and stays visible as a
bot — selectivity is the whole point.

## Build

### Prerequisites

- Linux host (Windows build path is partially implemented, not load-bearing).
- .NET 8 SDK.
- A CS2 dedicated server install (the C++ side needs the schema layout to
  match a real server).
- `hl2sdk-cs2` + `metamod-source` vendored beside `ReplicaHider/` (the CI
  workflow under [`.github/workflows/build.yml`](.github/workflows/build.yml)
  does this from scratch and is the canonical reference).

### Build commands

```bash
# C# / CSSharp plugin. Point at a CounterStrikeSharp install via SRCDS_ROOT,
# or override the API DLL location explicitly:
export SRCDS_ROOT=/path/to/cs2-dedicated-server
cd Replica && dotnet build -c Release
# or:
cd Replica && dotnet build -c Release \
    -p:CSSharpApiPath=/path/to/CounterStrikeSharp.API.dll

# C++ / Metamod plugin.
# Vendor the SDKs once, apply a small compat patch (see scripts/ci-patch-sdks.sh
# header for the rationale), then build:
cd ReplicaHider
git clone --depth 1 --branch cs2    https://github.com/alliedmodders/hl2sdk.git           hl2sdk
git clone --depth 1 --branch master https://github.com/alliedmodders/metamod-source.git   mmsource
../scripts/ci-patch-sdks.sh hl2sdk
make
```

### Deploy

The [`scripts/deploy.sh`](scripts/deploy.sh) helper builds, prints a
deploy-baseline stanza (commit-sha + DLL sha256), and optionally copies the
binary to `$SRCDS_ROOT/game/csgo/addons/counterstrikesharp/plugins/Replica/`:

```bash
SRCDS_ROOT=/path/to/cs2-dedicated-server ./scripts/deploy.sh --auto
```

## Upgrading

### PersonaRegistry path (≥ commit `1d1d9a3`)

The `PersonaRegistry` JSON file was relocated from a developer-only
absolute path to a `Server.GameDirectory`-relative path so the public
build doesn't depend on a `/home/<user>/` layout.

- **Before** (≤ `1d1d9a3^`): `~/cs2-server/replica/personas.json`
- **After**  (≥ `1d1d9a3`):  `$SRCDS_ROOT/game/csgo/addons/counterstrikesharp/configs/plugins/Replica/personas.json`

If you ran an earlier build, the plugin will not pick up your old file
on first load and will start re-minting personas from the fallback
roster. The previous file remains stranded at its original location.

**Migration** (one-shot, before first start on the new build):

```bash
# Adjust SRCDS_ROOT to your install.
mkdir -p "$SRCDS_ROOT/game/csgo/addons/counterstrikesharp/configs/plugins/Replica"
mv ~/cs2-server/replica/personas.json \
   "$SRCDS_ROOT/game/csgo/addons/counterstrikesharp/configs/plugins/Replica/personas.json"
```

If your old `personas.json` lived somewhere other than the historical
default, substitute that path for the source. The plugin logs
`PersonaRegistry: no file at <path>, starting empty …` when it can't
find the file at the new location — that line is the prompt to migrate.

## Documentation

- [`notes/`](notes/) — design notes for live-engine probes (Stage 3 / 4
  series). What was checked on disk vs. what required a connected human
  client to verify, and which schema fields turned out to be server-side-only
  on current CS2 builds.
- [`docs/COMPAT.md`](docs/COMPAT.md) — plugin-tag × CS2-build compatibility
  matrix, and the short list of anchors/fields most often moved by drift.
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — where to post what (Issues vs.
  the topic-specific Discussions categories), in-scope vs. out-of-scope.
- [`SECURITY.md`](SECURITY.md) — how to privately report memory-safety
  issues in the detour / mmap code.

## Upgrade notes

The repo doesn't carry a CHANGELOG yet, so cvar/option churn lives here.
If a previous `replica.cfg` (formerly `replica.cfg`) (or an `autoexec.cfg` that `exec`s into it)
still references one of the names below, you'll see `unknown command`
warnings on the console at map start; remove the line — behaviour is
unchanged.

### `v0.6.0.x-beta` → `v0.7.x-beta`

Removed cvars (no replacement — the real-SteamID provider mode was
dropped at public release):

- `replica_steamid_mode` — was `synthetic` / `real`. The synthetic
  provider is now the only one; the cvar has no effect.
- `replica_real_steamids_file` — pointed at the real-SteamID JSONL
  consumed by the `real` mode. Unused now; safe to delete the file too.

If you previously kept either line in a server-side config, drop it.
`SyntheticSteamIdProvider` is selected unconditionally
(`Replica/src/SteamIdProvider.cs`).

## Project status

This repository is in **early alpha**. The README, public surface, tag stream,
and even the directory layout may shift as the experiment matures. Issues and
discussion are welcome, but expect the answer to many "is this stable?"
questions to be *"not yet."*

## Disclaimer

This project is an independent technical experiment. It is **not affiliated
with, endorsed by, or sponsored by Valve Corporation.** *Counter-Strike 2*,
*Counter-Strike*, *Steam*, *SteamID*, and all related trademarks are property
of Valve Corporation.

The plugins manipulate server-side bot state on a Source 2 dedicated server
you run yourself. They do not modify the *Counter-Strike 2* client, do not
ship game assets, and do not interact with Valve's matchmaking or
authentication services in any way. Use on official Valve-operated servers
or matchmaking is **not** an intended use and may violate Valve's terms of
service for those services. **Run only on dedicated servers you own and
administer.**

This software is provided "as is", without warranty of any kind — see
[`LICENSE`](LICENSE) for the full MIT license terms.
