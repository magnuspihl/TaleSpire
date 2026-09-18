#!/usr/bin/env python3
"""Put every candidate tile for one role on the board at once, as real generated rooms.

    python3 tools/pick-probe.py Tavern wall              # slab + legend on stderr
    python3 tools/pick-probe.py Tavern wall --match wall  # narrow the candidates

Which tile is right for a role is not derivable from the metadata, so it has to be looked at in
game — but looking at a lone tile does not answer the question either. An isolated panel and an
isolated pillar are hard to tell apart from the board camera; what distinguishes them is whether a
row of them closes into a wall. So this builds an actual room per candidate, through the shipped
SlabBuilder, and lays the rooms out in a line to be compared in one screenshot.

The candidates are substituted by writing a sidecar tileset_profiles.json next to the MapGenQA
binary — the same override a user can drop next to MapGen.dll — with one throwaway theme per
candidate. Nothing in the repo is modified, so a probe cannot leave a half-applied pick behind.

Candidates are drawn from the whole tileset folder in the catalog, not from the profile's curated
role list: the recurring failure is a correct tile that was never curated, and a probe that only
offers the curated ones can never find it.
"""

import argparse
import json
import os
import subprocess
import sys

HERE     = os.path.dirname(os.path.abspath(__file__))
CATALOG  = "/home/coder/TaleSpire-Binaries/tile_catalog.json"
PROFILES = os.path.join(HERE, "..", "MapGen", "tileset_profiles.json")
QA       = os.path.join(HERE, "MapGenQA")
SIDECAR  = os.path.join(QA, "bin", "Debug", "net8.0", "tileset_profiles.json")


def folder_tiles(folder: str) -> list:
    with open(CATALOG, encoding="utf-8") as f:
        catalog = json.load(f)
    for folders in catalog["packs"].values():
        if folder in folders:
            return sorted([t for t in folders[folder] if t["size"] == "1x1"],
                          key=lambda t: t["name"])
    raise SystemExit(f"no tileset folder named '{folder}' in the catalog")


def find_profile(data: dict, theme: str):
    for pack, tilesets in data["profiles"].items():
        if theme in tilesets:
            return pack, tilesets[theme]
    raise SystemExit(f"no profile for '{theme}'")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("theme")
    ap.add_argument("role", help="wall, floor, corner, cornerFiller, stairs or door")
    ap.add_argument("--match", help="only candidates whose name contains this")
    ap.add_argument("--max", type=int, default=5, help="candidates per probe, to keep them on screen")
    ap.add_argument("--skip", type=int, default=0, help="candidates to skip, to page through them")
    # One row by default. Where the wall carries a floor, a second row lays that floor across the
    # top of the ring and the room reads as a closed box from the board camera, which hides the
    # very thing being judged.
    ap.add_argument("--rows", type=int, default=1, help="wall rows per probe room")
    a = ap.parse_args()

    with open(PROFILES, encoding="utf-8") as f:
        data = json.load(f)
    pack, base = find_profile(data, a.theme)

    tiles = folder_tiles(a.theme)
    if a.match:
        tiles = [t for t in tiles if a.match.lower() in t["name"].lower()]
    tiles = tiles[a.skip:a.skip + a.max]
    if not tiles:
        raise SystemExit("no candidates left after --match/--skip")

    names = []
    for i, t in enumerate(tiles):
        probe = json.loads(json.dumps(base))
        roles = probe.setdefault("tilesByRole", {})
        rest = [e for e in roles.get(a.role, []) if e["id"] != t["id"]]
        roles[a.role] = [{"id": t["id"], "name": t["name"],
                          "size": t["size"], "height": t["height"]}] + rest
        name = f"probe_{i}"
        data["profiles"][pack][name] = probe
        names.append(name)
        print(f"[{i}] h={t['height']:<5} {t['name']}  {t['id']}", file=sys.stderr)

    # The rooms come out as a line on screen with no way to tell which end is candidate 0 — the
    # board is drawn at an angle and the candidates are, by construction, variations on one look.
    # A room in an unmistakably different material marks the start.
    marker = "Dungeon Cellar" if a.theme == "Marble Palace" else "Marble Palace"
    names.insert(0, marker)
    print(f"[marker] {marker} room at the start of the line", file=sys.stderr)

    os.makedirs(os.path.dirname(SIDECAR), exist_ok=True)
    with open(SIDECAR, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)

    try:
        out = subprocess.run(
            ["dotnet", "run", "-c", "Debug", "--no-build", "--", "rooms",
             "--themes", ",".join(names), "--rows", str(a.rows)],
            cwd=QA, check=True, capture_output=True, text=True)
        print(out.stdout.strip())
    finally:
        # The sidecar shadows the embedded profiles for every later harness run, including fuzz,
        # so leaving it behind would quietly validate a map nobody asked for.
        os.remove(SIDECAR)
    return 0


if __name__ == "__main__":
    sys.exit(main())
