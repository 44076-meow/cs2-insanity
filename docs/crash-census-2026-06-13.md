# CS2 dedicated crash census — 2026-06-13 night

Forensic pass over `dmesg` after a live-match crash (12:52, score 3:1).
Surfaces a single persistent server-killer that the earlier mitigations did
**not** close, and separates it from incidental boot-race noise.

## Census (all `cs2` kernel faults on record)

| Time (local)      | Fault                                   | Class |
|-------------------|-----------------------------------------|-------|
| Jun 10 23:25      | GPF `libanimationsystem.so[60bdf1]`     | **animation** |
| Jun 11 00:04      | GPF `libanimationsystem.so[60bdf1]`     | **animation** |
| Jun 11 04:33      | GPF `libanimationsystem.so[60bdf1]`     | **animation** |
| Jun 12 22:14      | GPF `libanimationsystem.so[60bdf1]`     | **animation** |
| Jun 13 00:55      | GPF `libserver.so[1b2b981]`             | engine (rare) |
| Jun 13 01:12      | GPF `libanimationsystem.so[60bdf1]`     | **animation** |
| Jun 13 01:45      | segfault `libtier0.so[27f780]`          | boot/bind race |
| Jun 13 02:27      | segfault `libtier0.so[293334]`          | boot/bind race |
| Jun 13 02:36      | GPF `libanimationsystem.so[60bdf1]`     | **animation** (post-#199) |
| Jun 13 02:41      | GPF `libanimationsystem.so[60bdf1]`     | **animation** (post-#199) |
| Jun 13 02:45      | GPF `libserver.so[20eaf5d]`             | engine (rare) |
| Jun 13 12:48      | segfault `libtier0.so[27f780]`          | boot/bind race |
| Jun 13 12:52      | GPF `libanimationsystem.so[60bdf1]`     | **animation** (live match) |

(Plus several `segfault at …9e400 error 14` / `error 6` lines — instruction-fetch
/ null faults that coincide with the boot-race losers.)

## Finding 1 — the real game-crasher

`libanimationsystem.so` **general protection fault at the same offset `60bdf1`,
8 times, since Jun 10.** Deterministic: one specific instruction in the
animation system, hit when the bot model/bodygroup/attribute set is applied
during the spawn/buy (freeze-period) window. This is the "Mode-B" SIGSEGV
family (issue #58).

**#199 did not fix it.** The weapon-apply queue (2/tick) shipped, yet the
crash recurred at 02:36, 02:41 and 12:52 — all post-#199. So the throttle was
aimed at the wrong write: it's not the *weapon* skin apply that faults, it's
the **agent (player-model) application** path — `SetModel`/bodygroup on the
pawn while the animation graph isn't in a state that tolerates it.

The 12:52 console line `Long frame (FreezePeriod): 26` is the symptom: the
main thread stalled hard in the animation system right before the GPF.

### Options (need owner call — not changed blind during live play)

1. **Gate agent (player-model) application OFF**, keep weapon / glove / knife
   skins. If the fault is the agent `SetModel`, this removes the crasher
   outright at the cost of custom player models. Lowest-risk kill.
2. **Defer agent apply to a safe tick** — never `SetModel` on the spawn tick;
   apply N ticks later once the pawn's animation graph is initialized, behind
   a readiness guard. Keeps agents, needs validation.
3. **Capture the minidump** (now automated, see below) and resolve the exact
   `libanimationsystem+60bdf1` symbol to fix precisely.

## Finding 2 — boot/bind races (self-inflicted, avoidable)

`libtier0.so` segfaults at 01:45, 02:27, 12:48 line up with double-boots
(two `start.sh` racing for UDP :27015 — the loser crashes in tier0 networking
with `Cannot create listen socket / Failed to bind`). **Mitigation:** only ever
launch one `start.sh`; verify no dedicated `cs2` is alive and `:27015` is free
first. Not a plugin bug.

## Tooling added

- `scripts/crash-watch.sh` — independent crash analyzer. Survives the server
  crash; on death snapshots `server.log` (before the reboot truncates it),
  the `dmesg` fault line + parsed faulting module/offset, fresh
  `/tmp/dumps/*.dmp`, the Replica telemetry tail and framework-log tail into
  `crash-reports/<ts>/crash-report.md` with a verdict. Also live-logs
  anomalies (long frames, exceptions, ghost/husk/giveup) to
  `crash-reports/anomalies.log`. Optional persistent user unit shipped.
- Pre-existing `scripts/mode-b-watchdog.py` already auto-*restarts* on this
  crash family but was not running tonight — complementary to crash-watch
  (restart vs. analyze). Worth enabling alongside.
