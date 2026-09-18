#!/usr/bin/env python3
"""Emit a contact sheet for a tileset: every 1x1 tile it has, laid out to be looked at in game.

    python3 tools/tileset-contact.py "Sewers"                  # base64 slab, ready to paste
    python3 tools/tileset-contact.py "Sewers" --legend          # which tile is in which cell
    python3 tools/tileset-contact.py "Sewers" --match wall      # only tiles whose name matches
    python3 tools/tileset-contact.py "Sewers" --match wall --run 3   # three adjacent copies each

Picking a tile for a role is not derivable from the profile metadata. hasIntegratedFloorWall is
one boolean per tileset while carrying-a-floor is a per-tile property, and some correct tiles are
missing from the curated tilesByRole lists altogether — so a sheet of the curated candidates can
confirm a pick but can never find one that was never curated. This sheets the whole tileset.

Every tile sits on bare board with nothing beneath it, spaced a cell apart. A tile that carries
its own floor then visibly has one and a bare tile does not, which is the distinction that has to
be made by eye and the whole reason for the rig round trip. Feed what you learn back into
tools/tileset_picks.json.

Reading the sheet back off a screenshot means knowing which blob is which tile, and a wrapped grid
gives no reliable way to tell — the board is drawn at an angle and rows and columns look alike.
So a sheet narrow enough to fit one row is emitted as one row, and the first tile is duplicated
one cell to the side of itself. The doubled end is cell 0; count from there.

A single copy of a tile tells you little about whether it is a wall: an isolated panel and an
isolated pillar look much alike from the board camera. --run places several adjacent copies of
each candidate instead, which is the decisive picture — a wall joins into a continuous face, a
pillar stays a row of posts with daylight between them.
"""

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from encode_slab import encode  # noqa: E402

CATALOG = "/home/coder/TaleSpire-Binaries/tile_catalog.json"
SPACING = 2
# Wider than this and the far end leaves the screen at the zoom the sheet is legible at.
ROW_LIMIT = 9


def unit_tiles(folder: str) -> list:
    with open(CATALOG, encoding="utf-8") as f:
        catalog = json.load(f)
    for folders in catalog["packs"].values():
        if folder in folders:
            tiles = [t for t in folders[folder] if t["size"] == "1x1"]
            # By name, so a set's variants of one piece land next to each other on the sheet and
            # can be compared without hunting across it.
            return sorted(tiles, key=lambda t: t["name"])
    raise SystemExit(f"no tileset folder named '{folder}' in the catalog")


def layout(tiles: list, run: int = 1) -> list:
    if run > 1:
        placed, x = [], 0
        for t in tiles:
            placed += [(t, x + i, SPACING) for i in range(run)]
            x += run + SPACING
    else:
        columns = len(tiles) if len(tiles) <= ROW_LIMIT else ROW_LIMIT
        placed = [(t, (i % columns) * SPACING, (i // columns) * SPACING + SPACING)
                  for i, t in enumerate(tiles)]
        if columns != len(tiles):
            return placed
    # Start marker, beside cell 0. The rows above are already shifted south by SPACING to make
    # room for it, because slab positions are packed unsigned and a negative coordinate silently
    # encodes garbage rather than failing.
    placed.append((tiles[0], 0, 0))
    return placed


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("folder", help="tileset folder name, e.g. 'Marble Palace'")
    ap.add_argument("--legend", action="store_true", help="print the cell map instead of the slab")
    ap.add_argument("--match", help="only tiles whose name contains this (case-insensitive)")
    ap.add_argument("--run", type=int, default=1, help="adjacent copies of each tile")
    ap.add_argument("--pick", help="comma-separated legend indices, e.g. 0,1")
    a = ap.parse_args()

    tiles = unit_tiles(a.folder)
    if a.match:
        tiles = [t for t in tiles if a.match.lower() in t["name"].lower()]
        if not tiles:
            raise SystemExit(f"no 1x1 tile in '{a.folder}' matches '{a.match}'")
    if a.pick:
        tiles = [tiles[int(i)] for i in a.pick.split(",")]
    placed = layout(tiles, a.run)

    if a.legend:
        print(f"# {a.folder} — {len(tiles)} 1x1 tiles, X east across, Z south down")
        for i, t in enumerate(tiles):
            print(f"[{i}]  h={t['height']:<5} {t['name']}  {t['id']}")
        return 0

    print(encode([(t["id"], x, 0, z, 0) for t, x, z in placed]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
