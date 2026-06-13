#!/usr/bin/env bash
# =============================================================================
# crash-watch.sh — independent CS2 dedicated crash analyzer + anomaly logger.
#
# Runs OUTSIDE the game process so it survives a server crash. Two jobs:
#
#  1. CRASH SNAPSHOT. The moment the dedicated cs2 process disappears, grab
#     everything that the reboot would destroy or that's scattered across the
#     system, into crash-reports/<ts>/:
#       - server.log  (tee TRUNCATES it on the next boot — this is the only
#                       window to keep the crash tail)
#       - dmesg fault line(s)  (segfault / GPF + the faulting .so + offset)
#       - any fresh /tmp/dumps/*.dmp minidump
#       - replica telemetry tail (logs/.../replica/*.jsonl — the black box)
#       - CSSharp framework log tail (log-all / log-cssharp)
#       - a human-readable crash-report.md with the verdict (faulting module).
#
#  2. ANOMALY LOG. Tail server.log live and append "weird shit" to
#     crash-reports/anomalies.log with timestamps — long frames, watchdog,
#     plugin exceptions, ghost/husk/giveup warnings, failed asserts. So
#     in-game weirdness that DOESN'T crash the server still leaves a trail.
#
# Usage:   crash-watch.sh [--once]      (--once: exit after first crash snap)
# Install: see crash-watch.service note at bottom for a persistent user unit.
# =============================================================================
set -u

SERVER_ROOT="/mnt/storage/cs2-server"
SERVER_LOG="$SERVER_ROOT/server.log"
DUMPS_DIR="/tmp/dumps"
CSS_LOGS="$SERVER_ROOT/game/csgo/addons/counterstrikesharp/logs"
TELE_DIR="$CSS_LOGS/replica"
OUT_ROOT="$SERVER_ROOT/crash-reports"
ANOMALY_LOG="$OUT_ROOT/anomalies.log"
POLL=2          # seconds between liveness checks
ONCE=0
[ "${1:-}" = "--once" ] && ONCE=1

mkdir -p "$OUT_ROOT"

ts()   { date '+%Y-%m-%d_%H-%M-%S'; }
stamp(){ date '+%Y-%m-%d %H:%M:%S'; }

# PID of the dedicated server (not the Steam client), via /proc cmdline scan.
dedik_pid() {
  for pid in $(pgrep -x cs2 2>/dev/null); do
    if tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -q -- '-dedicated'; then
      echo "$pid"; return 0
    fi
  done
  return 1
}

# ---- anomaly tailer (background sub-job) ------------------------------------
# Patterns worth a trail even without a crash. fflush via grep --line-buffered.
start_anomaly_tail() {
  # Note: routine freeze-period frames are 16-30ms — noise. Only flag a
  # Long frame when its ms value is >=100ms (3+ integer digits), i.e. a real
  # stall worth a trail. Everything else here is genuinely abnormal.
  ( tail -F "$SERVER_LOG" 2>/dev/null \
    | grep --line-buffered -iE \
        'Long frame[^:]*: [0-9]{3,}\.[0-9]+ms|FATAL|Watchdog|Segmentation|exception|assert|null ref|Unhandled|team_enforce_giveup|husk_sweep|GHOST|HUSK|out of memory' \
    | while IFS= read -r line; do
        printf '[%s] %s\n' "$(stamp)" "$line" >> "$ANOMALY_LOG"
      done ) &
  echo $!
}

# ---- crash snapshot ---------------------------------------------------------
snapshot() {
  local dir="$OUT_ROOT/$(ts)"
  mkdir -p "$dir"
  local report="$dir/crash-report.md"

  # 1. server.log — copy NOW before any reboot truncates it.
  [ -f "$SERVER_LOG" ] && cp -f "$SERVER_LOG" "$dir/server.log" 2>/dev/null
  local logtail; logtail=$(tail -40 "$SERVER_LOG" 2>/dev/null)

  # 2. dmesg fault lines (last few cs2 faults).
  local faults; faults=$(sudo dmesg -T 2>/dev/null | grep -iE 'cs2.*(segfault|general protection|trap)' | tail -4)
  local lastfault; lastfault=$(echo "$faults" | tail -1)
  # Parse faulting module + offset from a line like:
  #   ... in libanimationsystem.so[60bdf1,...]
  local module offset
  module=$(echo "$lastfault" | grep -oE 'in [a-zA-Z0-9_.]+\.so' | sed 's/^in //' | tail -1)
  offset=$(echo "$lastfault" | grep -oE '\.so\[[0-9a-f]+' | grep -oE '[0-9a-f]+$' | tail -1)
  [ -z "$module" ] && module="(no dmesg fault line — possible clean exit / OOM / external kill)"

  # 3. fresh minidumps (last 3 min).
  local dumps; dumps=$(find "$DUMPS_DIR" -name '*.dmp' -mmin -3 2>/dev/null)
  for d in $dumps; do cp -f "$d" "$dir/" 2>/dev/null; done

  # 4. replica telemetry tail (most recent jsonl).
  local tele; tele=$(ls -t "$TELE_DIR"/*.jsonl 2>/dev/null | head -1)
  if [ -n "$tele" ]; then
    tail -120 "$tele" > "$dir/telemetry-tail.jsonl" 2>/dev/null
  fi

  # 5. framework log tail.
  local fw; fw=$(ls -t "$CSS_LOGS"/log-all*.txt 2>/dev/null | head -1)
  [ -n "$fw" ] && tail -60 "$fw" > "$dir/framework-tail.txt" 2>/dev/null

  # 6. memory snapshot (rule out OOM).
  local mem; mem=$(free -h 2>/dev/null | head -2)
  local oom; oom=$(sudo dmesg -T 2>/dev/null | grep -iE 'out of memory|killed process' | tail -3)

  # 7. verdict.
  local verdict
  if echo "$lastfault" | grep -qi 'libanimationsystem'; then
    verdict="Mode-B animation GPF (model/attribute write race during spawn/buy). See issue #58/#11."
  elif echo "$lastfault" | grep -qi 'libtier0'; then
    verdict="libtier0 fault — usually a boot/bind race (two cs2 fighting for :27015) or network teardown. Check for double-boot."
  elif [ -n "$oom" ]; then
    verdict="OOM killer fired — environmental memory pressure, not a plugin bug."
  elif echo "$lastfault" | grep -qi 'libserver'; then
    verdict="libserver fault — engine-side; capture the minidump symbols."
  else
    verdict="Unknown — no matching fault signature. Possible clean exit, watchdog, or external kill."
  fi

  {
    echo "# CS2 crash report — $(stamp)"
    echo
    echo "## Verdict"
    echo "$verdict"
    echo
    echo "## Faulting module"
    echo '```'
    echo "module: ${module}"
    echo "offset: ${offset:-?}"
    echo '```'
    echo
    echo "## dmesg fault lines"
    echo '```'
    echo "${faults:-（none — process vanished without a kernel fault: clean exit / watchdog / external kill）}"
    echo '```'
    echo
    echo "## server.log tail (last 40)"
    echo '```'
    echo "$logtail"
    echo '```'
    echo
    echo "## Minidumps captured"
    echo '```'
    if [ -n "$dumps" ]; then echo "$dumps"; else echo "(none in last 3 min)"; fi
    echo '```'
    echo
    echo "## Memory at capture"
    echo '```'
    echo "$mem"
    [ -n "$oom" ] && { echo; echo "OOM lines:"; echo "$oom"; }
    echo '```'
    echo
    echo "## Artifacts in this dir"
    echo '```'
    ls -la "$dir"
    echo '```'
  } > "$report"

  echo "$dir"
}

echo "[$(stamp)] crash-watch started (poll ${POLL}s, out=$OUT_ROOT)" >> "$ANOMALY_LOG"
ANOM_PID=$(start_anomaly_tail)
trap 'kill $ANOM_PID 2>/dev/null' EXIT

# Wait until the server is up the first time, so we don't false-trigger on a
# down server we were started alongside.
while ! dedik_pid >/dev/null; do sleep "$POLL"; done
echo "[$(stamp)] dedicated server detected (pid $(dedik_pid)) — watching" >> "$ANOMALY_LOG"

while true; do
  if ! dedik_pid >/dev/null; then
    # Grace: give a real reboot a couple seconds (avoid snapping our own
    # intentional restarts mid-swap). If it comes back fast, it was a managed
    # restart; if it stays down, snapshot.
    sleep 3
    if dedik_pid >/dev/null; then continue; fi
    DIR=$(snapshot)
    echo "[$(stamp)] CRASH SNAPSHOT -> $DIR" >> "$ANOMALY_LOG"
    echo "CRASH_SNAPSHOT $DIR"
    [ "$ONCE" = "1" ] && exit 0
    # Wait for the server to come back before resuming watch (don't spam).
    while ! dedik_pid >/dev/null; do sleep "$POLL"; done
    echo "[$(stamp)] server back up (pid $(dedik_pid))" >> "$ANOMALY_LOG"
  fi
  sleep "$POLL"
done

# -----------------------------------------------------------------------------
# Persistent install (user service), so it survives logout/reboot:
#   ~/.config/systemd/user/crash-watch.service
#     [Unit]
#     Description=CS2 Replica crash analyzer
#     [Service]
#     ExecStart=/mnt/storage/cs2-server/scripts/crash-watch.sh
#     Restart=always
#     [Install]
#     WantedBy=default.target
#   systemctl --user enable --now crash-watch.service
# (sudo dmesg needs the NOPASSWD already granted to frad70.)
# -----------------------------------------------------------------------------
