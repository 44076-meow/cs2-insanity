# Stage 4 — live probe results

This file is the live-verification companion to `notes/stage_3_4_probes.md`
(desk research) and `Replica/src/Probe.cs` (the rcon commands that
issue the probes).

Each probe section is filled in **after** a friend playtest where a connected
human client visually confirms what the engine actually did.

Status legend:

- `🟢 GREEN`  — works as intended; Stage 4 may use this codepath as designed.
- `🟡 YELLOW` — partial / cosmetic quirk; acceptable to ship, document behaviour.
- `🔴 RED`    — broken or crashes; Stage 4 must take the documented fallback.
- `⚪ PENDING` — probe code shipped, live verification not yet done.

---

## Probe 1 — `m_clrRender` red tint on player pawn

**Command:** `replica_probe_glow <slot> [r g b]` (defaults to red 255/0/0).
**Code:** `Probe.Glow` — sets `pawn.Render = Color.FromArgb(...)` and calls
`SchemaSafety.MarkChanged(pawn, "CBaseModelEntity", "m_clrRender")`.
**Status:** ⚪ PENDING (autonomous session 2026-05-08 — user away, ship code,
defer live test).

**Expected outcomes:**

- 🟢 — model tints. Stage 4 entry tints all C4 carriers (or all bots) red.
- 🟡 — partial tint (world model only / different shade). Acceptable —
  human in third-person sees the swarm marked.
- 🔴 — no visible change OR engine warns "not networked". Fallback:
  `m_iGlowRange` + `m_clrGlow` (CS2 player glow API), or per-bot
  `light_dynamic` entity parented to pawn.

**Live result:** _to be filled in by next user-facing session_

---

## Probe 2 — `GiveNamedItem("weapon_c4")` on a bot

**Command:** `replica_probe_c4 <slot>`.
**Code:** `Probe.GiveC4` — `c.GiveNamedItem("weapon_c4")` on a fake-client
controller. Same code path used for `weapon_knife` (Stage 1) and
`weapon_m249` / `weapon_negev` (Stage 2).
**Status:** ⚪ PENDING.

**Expected outcomes:**

- 🟢 — bot holds C4 model in hip / hand, no "PLANT THE BOMB" text on
  any client, no engine auto-revoke when bot is on CT side. Stage 4
  ships visible C4 + manual `env_explosion` detonation.
- 🟡 — bot holds C4 but radar shows "PLANT" marker. Cosmetic only —
  user sees bomb radar icon during the prank but nothing actually plants.
  Document and ship.
- 🔴 — engine revokes C4 from CT bots, OR floods all clients with the
  bomb-objective announcement. Fallback: skip visible C4 entirely;
  trigger `env_explosion` on bot death / vision-trigger anyway. Bot
  looks like a regular m249 carrier; explodes anyway. Suicide
  mechanic preserved, visual deferred.

**Live result:** _to be filled in_

---

## Probe 3 — `OnEntityTakeDamagePre` filter (BotDamagePatch)

**Command:** `replica_probe_hurtzero [arm|disarm]` (default: arm).
**Code:** `Probe.HurtZeroArmOnce` — installs the production
`BotDamagePatch` listener (which uses `Listeners.OnEntityTakeDamagePre`
internally — ported from deprecated `CBaseEntity_TakeDamageOldFunc`
in step 2 of the 2026-05-08 session). Probe just toggles the same
listener on/off so the playtest can verify behaviour in isolation
before Stage 4 wires it automatically.
**Status:** ⚪ PENDING (listener registered, behaviour not yet
verified live — relies on probe 2's molotov / HE rain to trigger).

**Test procedure (when user runs it):**

1. `replica_probe_hurtzero arm` (installs filter).
2. Trigger bot-vs-bot damage: e.g. `mp_friendlyfire 1` then watch a
   knife rush. Bots should NOT damage each other.
3. Trigger inferno damage: spawn `inferno` near a bot. Bot should
   take zero damage.
4. Trigger human damage: a human shoots a bot. Damage should flow
   normally.
5. `replica_probe_hurtzero disarm` (removes filter).

**Expected outcomes:**

- 🟢 — bots immune to bot-vs-bot direct hits AND inferno/molotov/HE.
  Humans take damage normally. Stage 4 entry calls `Install()`,
  `EndReveal` calls `Uninstall()`.
- 🟡 — only direct hits filtered, projectile damage still flows through.
  Acceptable for Stage 1+2; for Stage 4 grenade rain we'd need a
  parallel filter (e.g. `OnPlayerHurt` PRE) to catch what
  `OnEntityTakeDamagePre` misses.
- 🔴 — listener doesn't fire at all (CSSharp 1.0.367 bug?), or filter
  causes crashes. Fall back to `BotDamagePatch.cs` git-revert before
  step 2; reintroduce `VirtualFunctions.CBaseEntity_TakeDamageOldFunc`
  with `[Obsolete]` warning suppressed.

**Live result:** _to be filled in_

---

## Removal policy

Probe commands are SAFE to leave in production builds — they require
`@css/cheats` permission and have no side effect on inactive bots /
disconnected slots. Once Stage 4 ships AND each probe has a 🟢/🟡
status above, the commands and `Probe.cs` may be removed in a cleanup
commit. No rush — they cost ~1 KB DLL size and zero runtime.
