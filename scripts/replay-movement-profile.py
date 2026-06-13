#!/usr/bin/env python3
"""
replay-movement-profile.py — extract DETERMINISTIC opening-route patterns of
each recorded player, the movement analogue of replay-structural-profile.py.

Goal (epic #188 / Phase B): bots that move like the real player they're named
after — not synthetic lane heuristics. The recorder's per-player snapshot
stream (x/y/z, view, buttons, ~20 Hz) is a full movement trace. Here we
distill, per player, the OPENING ROUTE they take each round (the first
OPENING_SEC after freeze ends, while alive), grouped by side (T/CT) and buy
type (pistol / eco / full). Phase C will hand a bot one of its own recorded
routes to follow, with diversity across the fleet so they stop herding.

Output: <session>/_meta/movement_<name>.json
    { "schema": "movement-v1", "name": ..., "routes": {
        "T/full":  [ [[x,y],[x,y],...], ... ],   # one polyline per round
        "CT/pistol": [ ... ], ... },
      "spawn_origins": {...}, "n_rounds": N }

Usage:  replay-movement-profile.py [SESSION_DIR ...]   (default: all sessions)
"""
from __future__ import annotations
import glob, gzip, json, math, os, sys

OPENING_SEC   = 20.0   # how much of the round opening to capture
WAYPOINT_DT   = 0.5    # downsample interval (s) -> ~2 Hz polyline
MIN_ROUTE_PTS = 4      # drop near-empty routes (died/stood in spawn)
REC_ROOT      = "/mnt/storage/cs2-server/recordings"


def load_stream(path):
    # Recordings can be truncated mid-write (the server crashed often) — a
    # gz then ends without its stream marker. Read line-by-line and stop
    # cleanly at the truncation instead of throwing the whole file away.
    op = gzip.open if path.endswith(".gz") else open
    try:
        with op(path, "rt", errors="ignore") as fh:
            while True:
                try:
                    line = fh.readline()
                except (EOFError, OSError):
                    break  # truncated gz — keep what we got
                if not line:
                    break
                line = line.strip()
                if line:
                    try: yield json.loads(line)
                    except ValueError: continue
    except (EOFError, OSError):
        return


def buytype(money, weapon, round_idx):
    # Pistol rounds are round 1 and 13 (CS2 MR12) — strongest signal.
    if round_idx in (0, 12):
        return "pistol"
    # Otherwise infer from spend: a primary rifle/smg => full; bare pistol +
    # low money => eco. weapon is the active gun at freeze-end.
    rifles = ("ak47", "m4a1", "m4a1_silencer", "sg556", "aug", "famas", "galilar",
              "awp", "ssg08", "scar20", "g3sg1", "mac10", "mp9", "mp7", "mp5sd",
              "ump45", "p90", "bizon", "nova", "xm1014", "mag7", "sawedoff", "m249", "negev")
    w = (weapon or "").replace("weapon_", "")
    if any(w == r for r in rifles):
        return "full"
    if money is not None and money < 2000:
        return "eco"
    return "full"


def extract_session(session_dir):
    streams = []
    for side in ("T", "CT"):
        d = os.path.join(session_dir, side)
        if os.path.isdir(d):
            streams += glob.glob(os.path.join(d, "*.jsonl*"))
    if not streams:
        return 0

    # name -> { "T/full": [polyline,...], ... }, spawn origins, round count
    per_name = {}
    rounds_seen = set()

    for path in streams:
        events = list(load_stream(path))
        if not events:
            continue
        name = None
        # Build round windows: (freeze_end_t, round_end_t, round_idx)
        windows, freeze_t, idx = [], None, -1
        for e in events:
            k = e.get("kind")
            if name is None and e.get("name"):
                name = e["name"]
            if k == "round_start":
                idx += 1
            elif k == "round_freeze_end":
                freeze_t = e.get("t")
            elif k in ("round_end", "round_officially_ended") and freeze_t is not None:
                windows.append((freeze_t, e.get("t"), idx))
                rounds_seen.add(round(freeze_t, 1))
                freeze_t = None
        if not name:
            continue

        side = "T"
        snaps = [e for e in events if e.get("kind") == "snapshot"]
        prof = per_name.setdefault(name, {"routes": {}, "spawns": {}})

        for (f_t, e_t, ridx) in windows:
            end = f_t + OPENING_SEC if e_t is None else min(e_t, f_t + OPENING_SEC)
            seg = [s for s in snaps if f_t <= s.get("t", -1) <= end and s.get("alive", True)]
            if len(seg) < MIN_ROUTE_PTS:
                continue
            s0 = seg[0]
            side = s0.get("team", side) or side
            bt = buytype(s0.get("money"), s0.get("weapon"), ridx)
            # downsample to WAYPOINT_DT
            poly, last_t = [], None
            for s in seg:
                t = s.get("t")
                if last_t is None or (t - last_t) >= WAYPOINT_DT:
                    poly.append([round(s.get("x", 0), 1), round(s.get("y", 0), 1)])
                    last_t = t
            if len(poly) < MIN_ROUTE_PTS:
                continue
            key = f"{side}/{bt}"
            prof["routes"].setdefault(key, []).append(poly)
            prof["spawns"].setdefault(side, []).append(poly[0])

    # write per-name
    meta = os.path.join(session_dir, "_meta")
    os.makedirs(meta, exist_ok=True)
    written = 0
    for name, prof in per_name.items():
        if not prof["routes"]:
            continue
        spawn_origins = {}
        for side, pts in prof["spawns"].items():
            n = len(pts)
            spawn_origins[side] = [round(sum(p[0] for p in pts) / n, 1),
                                   round(sum(p[1] for p in pts) / n, 1)]
        out = {
            "schema": "movement-v1",
            "name": name,
            "n_rounds": sum(len(v) for v in prof["routes"].values()),
            "routes": prof["routes"],
            "spawn_origins": spawn_origins,
        }
        with open(os.path.join(meta, f"movement_{name}.json"), "w") as fh:
            json.dump(out, fh, separators=(",", ":"))
        written += 1
    return written


def main():
    sessions = sys.argv[1:] or sorted(glob.glob(os.path.join(REC_ROOT, "*/")))
    total_files, total_routes = 0, 0
    for s in sessions:
        s = s.rstrip("/")
        n = extract_session(s)
        if n:
            total_files += n
            print(f"{os.path.basename(s)}: wrote {n} movement profile(s)")
    print(f"done — {total_files} profile file(s) across {len(sessions)} session(s)")


if __name__ == "__main__":
    main()
