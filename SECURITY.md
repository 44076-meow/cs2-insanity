# Security policy

This project ships a Metamod:Source C++ plugin with an inline detour into
the CS2 server binary, plus a CSSharp/.NET 8 plugin that writes to a
shared mmap. Both load inside a long-running dedicated game server. The
security surface that matters here is **memory safety of the in-process
plugin code** and **the integrity of the shared mmap** — not network
endpoints (there are none beyond what the server already exposes).

## Supported versions

This is early alpha. Only `main` and the most recent tag in the
`v0.6.0.x-beta` stream receive fixes. Older tags are archival.

## Reporting a vulnerability

If you've found a memory-safety bug, a crash with attacker-influenced
input on the server (e.g. via a crafted userinfo string or rcon flow),
or a way to escape the synthetic SteamID64 range — **do not open a public
issue or discussion**. Use GitHub's private reporting:

  **Repository → Security → Report a vulnerability**

(equivalently:
<https://github.com/Frad70/cs2-insanity/security/advisories/new>).

Include:

- Affected plugin tag / commit.
- CS2 dedicated server `PatchVersion=`.
- Repro: minimum server config, exact commands, whether a human client
  needs to be connected.
- Impact you observed (crash, OOB read/write, pool corruption, ...).
- Any patch you have.

## Out of scope

The following are *intentional* properties of the project and not
vulnerabilities:

- The plugins hide the `BOT` glyph and substitute synthetic SteamID64s on
  the dedicated server you run yourself. That's the stated feature; see
  README and **Disclaimer**.
- The synthetic SteamID64 range
  (`76561198_900_000_000`..`76561198_999_999_999`) is observable. It is
  reserved-by-design to avoid collisions with real accounts; observability
  is not a defect.
- Running this stack on Valve matchmaking, public ranked, or any server
  where players "reasonably expect to distinguish humans from bots" is
  explicitly **not** an intended use. Reports framed as "Valve can detect
  this on their service" will be closed — the project's threat model
  doesn't cover that scenario.

## Disclosure timeline

We aim to acknowledge a private report within 7 days and ship a fix (or
explain why not) within 30 days for confirmed issues in `main`. Coordinated
disclosure preferred; embargo length negotiable on a case-by-case basis.
