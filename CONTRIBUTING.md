# Contributing

This project is an **early-alpha research stack**. Issues and PRs are
welcome, but the bar is "make the experiment more legible", not "stabilize
the public surface". Read the [README](README.md) — especially the
**WARNING** block — before opening anything.

## Where to post what

| You have | Go to |
|---|---|
| A question about how something works | Discussions → **Q&A** |
| "CS2 update broke this" report | Discussions → **Schema drift watch** |
| A new offset / schema observation | Discussions → **Schema fields & gamedata** |
| A build / sha256 / CI issue | Discussions → **Build & reproducibility** |
| A feature pitch (not yet planned) | Discussions → **Ideas** |
| A reproducible defect with a known scope | Issues → **Bug report** |
| A confirmed drift event to track | Issues → **Schema drift — tracking issue** |
| A detour anchor that fails to install | Issues → **Detour anchor broken** |
| sha256 mismatch between two builds | Issues → **Build reproducibility — sha256 drift** |
| A planned feature with a sketch | Issues → **Feature request** |

If the Discussion categories listed above don't exist yet, see
[`notes/DISCUSSIONS_SETUP.md`](notes/DISCUSSIONS_SETUP.md).

## Scope

Acceptable contributions:

- Bug fixes against the documented architecture.
- Schema / gamedata updates for current CS2 builds, accompanied by a probe
  recipe (see `notes/stage_3_4_probes.md` for the style).
- AimState improvements (Etap A→D and beyond) that stay within
  `BotProfile`-driven aim skill.
- CI / build hardening — anything that strengthens the two-pass sha256
  invariant or shortens the bisect when it breaks.
- Notes / docs that capture what *was probed*, not speculation.

Out of scope (will be closed):

- Anything aimed at running on Valve matchmaking, public ranked, or
  competitive third-party services. See the README disclaimer.
- Anti-detection / signature obfuscation work intended to evade Valve
  client-visible heuristics.
- Use of real Steam IDs. The synthetic 76561198\_9xx range is deliberate.
- Manual `bot_add` styling (the README **Selectivity** section spells out
  why this isn't supported).

## Branching

- Open PRs against `main`.
- Tag releases use the `v0.6.0.x-beta` stream; you don't tag — maintainer does.
- Keep PRs small. If a change crosses both plugins *and* the pool layout
  *and* the gamedata, split it.

## Code style

- C# — repo `.editorconfig` is canonical. `dotnet format` on touched files.
- C++ — follow surrounding style in `InsanityHider/src/`. No new
  dependencies without prior discussion.
- Commits — past-tense imperative, prefix tag (`ci:`, `aim:`, `schema:`,
  `pool:`, `docs:`, `notes:`) where the existing log uses one. See
  `git log --oneline -20` for the cadence.

## Build & verify

Per the README **Build** section. PRs that touch C# code must keep the
two-pass sha256 invariant green. If a change is intentionally
non-deterministic (e.g. embeds a build timestamp), explain it in the PR
body and update the CI job rather than weakening the invariant silently.

## Reporting a security concern

See [`SECURITY.md`](SECURITY.md). Do not file public issues for memory-safety
findings in the C++ detour code.
