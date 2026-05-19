# Compatibility matrix

CS2 ships engine updates with no migration guarantee, and this stack
patches in-engine memory (schema fields + an inline detour). Each tag is
only valid for the CS2 build range it was probed against. This page is the
record of what we've confirmed.

> **How to read this table.** A row is added when a plugin tag is cut. A
> row is annotated (and never deleted) when a later CS2 build is
> *observed* to keep the row valid or to break it. "Broken" means at
> least one drift event — see Discussions → **Schema drift watch** —
> not "completely non-functional".

## Tags vs. CS2 builds

| Plugin tag | First CS2 build probed | Last CS2 build still good | Status | Notes |
|---|---|---|---|---|
| _populate via release process_ | _PatchVersion=…_ | _PatchVersion=…_ | _good / drift / broken_ | _link drift threads here_ |

When adding a row, link the originating drift Discussion thread (and any
`[drift]` tracking issue) in the **Notes** cell. Don't squash multiple
drift events into one row — that's what the column is for.

## Anchors & fields tracked

The detour / schema surfaces below are the ones a drift event most often
moves. Track each one's current observed offset / pattern in this list,
and bump it (with a CS2 build number) when it changes. The point is to
give the next person bisecting an outage a short list of "look at these
first".

- `CCSBot::UpdateLookAngles` — inline detour anchor (`libserver.so`).
- `CCSPlayerController::ProcessUsercmds` — interception path.
- `CCSPlayerController::m_bFakePlayer` — schema field flipped in the
  post-`OnClientConnected` chain.
- Userinfo broadcast ordering — when `m_bFakePlayer` flip has to land
  relative to the engine's broadcast.
- Shared pool layout — `[magic | version | reserved | slots[120]]`, 132
  bytes total. Bump `version` on any layout change; see
  [`README.md` § Shared pool](../README.md#shared-pool--132-byte-mmap).

## When a row goes red

1. Open a thread in Discussions → **Schema drift watch** (template
   prompts for everything below).
2. If confirmed, a maintainer opens a `[drift]` tracking issue and links
   the thread.
3. The next tag in the `v0.6.0.x-beta` stream either restores the row or
   adds a new row marked `good` from the new CS2 build. Old rows stay,
   annotated with the build that broke them.

## See also

- [`notes/stage_3_4_probes.md`](../notes/stage_3_4_probes.md) — what was
  verified on disk vs. live engine.
- [`notes/stage_4_probes.md`](../notes/stage_4_probes.md) — follow-up
  probes.
- [`notes/DISCUSSIONS_SETUP.md`](../notes/DISCUSSIONS_SETUP.md) — bringing
  the Discussions UI side in line with the templates committed in this
  repo.
