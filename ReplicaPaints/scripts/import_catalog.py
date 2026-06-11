#!/usr/bin/env python3
"""Import the full ByMykel CSGO-API skin index into our three plugin
catalog files. Run whenever Valve ships new skins:

    ./scripts/import_catalog.py

Writes:
    configs/weapons_paints.json   (all weapon + knife paint kits)
    configs/gloves.json           (all glove defindex+paint pairs)
Leaves alone:
    configs/knives.json           (defindex -> name; rarely changes)
    configs/settings.json
    data/players.json

Source: https://github.com/ByMykel/CSGO-API (MIT-licensed JSON dump).
"""
from __future__ import annotations
import json, sys, urllib.request, pathlib

BASE_URL = "https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en"
ROOT = pathlib.Path(__file__).resolve().parents[1]
CONFIGS = ROOT / "configs"

def fetch_json(name):
    url = f"{BASE_URL}/{name}"
    print(f"fetching {url} ...", flush=True)
    req = urllib.request.Request(url, headers={"User-Agent": "ReplicaPaints-importer"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.load(r)

def fetch_skins():
    return fetch_json("skins.json")

def main():
    data = fetch_skins()
    weapons, gloves = [], []
    for s in data:
        if not isinstance(s, dict): continue
        w = s.get("weapon") or {}
        wid = w.get("weapon_id")
        paint = s.get("paint_index")
        name = s.get("name")
        if wid is None or paint is None or not name:
            continue
        try: paint = int(paint)
        except (TypeError, ValueError): continue
        image = s.get("image") or ""
        # `legacy_model` is ByMykel's flag for skins that ship with the
        # pre-CS2 weapon mesh. Why we care: Source 2 weapons have two
        # bodygroups — `body=0` is the modern mesh with the secondary
        # design layer baked in (this is what Printstream / Doppler /
        # Marble Fade need to render their ink overlay); `body=1` is the
        # legacy mesh used by pre-CS2 paint kits. The apply path picks
        # which one to enable based on this flag, so missing it = all
        # multi-layer skins look washed out.
        legacy = bool(s.get("legacy_model"))
        # Doppler / Gamma Doppler / Marble Fade etc. share a knife name
        # across multiple paint_index values that map to the visual
        # "phase" (Ruby, Sapphire, Phase 1..4, Black Pearl, Emerald).
        # ByMykel stores those as separate rows with the same `name` but
        # different `paint_index` + a `phase` field. If we keep the bare
        # name, the menu shows a wall of identical-looking "Doppler"
        # rows; we suffix the phase so the visual distinction survives.
        phase = s.get("phase")
        if phase:
            name = f"{name} ({phase})"
        if 5000 <= wid < 6000:
            gloves.append({"defindex": wid, "paint": paint, "name": name, "image": image, "legacy_model": legacy})
        elif 1 <= wid < 600:
            weapons.append({"weapon_defindex": wid, "paint": paint, "name": name, "image": image, "legacy_model": legacy})
    # Sort: weapons by (defindex, paint), gloves by (defindex, paint). Keeps
    # diffs readable when the index regenerates.
    weapons.sort(key=lambda e: (e["weapon_defindex"], e["paint"]))
    gloves.sort(key=lambda e: (e["defindex"], e["paint"]))

    (CONFIGS / "weapons_paints.json").write_text(json.dumps(weapons, ensure_ascii=False, indent=2))
    (CONFIGS / "gloves.json").write_text(json.dumps(gloves, ensure_ascii=False, indent=2))

    unique_defs = sorted({e["weapon_defindex"] for e in weapons})
    print(f"weapons_paints.json: {len(weapons)} rows across {len(unique_defs)} defindexes")
    print(f"gloves.json:         {len(gloves)} rows")

    # -- Music kits, keychains, stickers, pins ----------------------
    # All four share the same minimal record shape (def_index + name +
    # image) — the engine identifies the item by def_index, the UI
    # renders by name + image. Distinct files so the C# loader doesn't
    # have to type-switch.
    music = []
    for m in fetch_json("music_kits.json"):
        di = _intornone(m.get("def_index"))
        if di is None or not m.get("name"): continue
        music.append({"defindex": di, "name": m["name"], "image": m.get("image") or ""})
    music.sort(key=lambda e: e["defindex"])
    (CONFIGS / "music_kits.json").write_text(json.dumps(music, ensure_ascii=False, indent=2))
    print(f"music_kits.json:     {len(music)} rows")

    keychains = []
    for k in fetch_json("keychains.json"):
        di = _intornone(k.get("def_index"))
        if di is None or not k.get("name"): continue
        keychains.append({"defindex": di, "name": k["name"], "image": k.get("image") or ""})
    keychains.sort(key=lambda e: e["defindex"])
    (CONFIGS / "keychains.json").write_text(json.dumps(keychains, ensure_ascii=False, indent=2))
    print(f"keychains.json:      {len(keychains)} rows")

    stickers = []
    for s in fetch_json("stickers.json"):
        di = _intornone(s.get("def_index"))
        if di is None or not s.get("name"): continue
        stickers.append({"defindex": di, "name": s["name"], "image": s.get("image") or ""})
    stickers.sort(key=lambda e: e["defindex"])
    (CONFIGS / "stickers.json").write_text(json.dumps(stickers, ensure_ascii=False, indent=2))
    print(f"stickers.json:       {len(stickers)} rows")

    # Pins are a subset of collectibles where type == "Pin". Coins /
    # trophies / patches live in the same file but we don't expose
    # those — they're either non-applicable or use different slots.
    pins = []
    for c in fetch_json("collectibles.json"):
        if c.get("type") != "Pin": continue
        di = _intornone(c.get("def_index"))
        if di is None or not c.get("name"): continue
        pins.append({"defindex": di, "name": c["name"], "image": c.get("image") or ""})
    pins.sort(key=lambda e: e["defindex"])
    (CONFIGS / "pins.json").write_text(json.dumps(pins, ensure_ascii=False, indent=2))
    print(f"pins.json:           {len(pins)} rows")

def _intornone(v):
    if v is None: return None
    try: return int(v)
    except (TypeError, ValueError): return None

if __name__ == "__main__":
    sys.exit(main())
