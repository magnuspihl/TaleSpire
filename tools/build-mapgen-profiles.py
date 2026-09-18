#!/usr/bin/env python3
"""Build MapGen/tileset_profiles.json from the reference data outside the repo.

    python3 tools/build-mapgen-profiles.py [--check]

The curated profiles in /home/coder/TaleSpire-Binaries/ list tile GUIDs per role and nothing
else, but MapGen places every tile on a 1x1 grid and has to know which candidates are actually
1x1 before it can pick one. That size lives in tile_catalog.json, which is far too large to ship.
So this copies the profiles across and folds in just the four fields the generator needs —
name, size, height and footprint — leaving the reference data untouched.

--check verifies the committed copy is up to date without writing, for use in CI.
"""

import argparse
import glob
import json
import os
import sys

HERE    = os.path.dirname(os.path.abspath(__file__))
REF_DIR = "/home/coder/TaleSpire-Binaries"
CATALOG = os.path.join(REF_DIR, "tile_catalog.json")
SOURCE  = os.path.join(REF_DIR, "tileset_profiles.json")
PACKS   = os.path.join(REF_DIR, "Taleweaver")
PICKS   = os.path.join(HERE, "tileset_picks.json")
OUTPUT  = os.path.join(HERE, "..", "MapGen", "tileset_profiles.json")

ROLES = ("wall", "floor", "corner", "cornerFiller", "stairs", "stairBlock", "door")


def tile_index() -> dict:
    with open(CATALOG, encoding="utf-8") as f:
        catalog = json.load(f)
    index = {}
    for folders in catalog["packs"].values():
        for tiles in folders.values():
            for t in tiles:
                index[t["id"]] = t
    return index


def pack_index() -> tuple:
    """(footprints, groups) keyed by GUID, read from the asset packs' own indexes.

    A footprint is the [x, z] size of the tile's collider bounds in cells. TaleSpire stores a
    placed tile's position as the *minimum* corner of its world bounding box
    (TileBuilderBoardTool.AssetHolder.Spawn writes GetWorldSnappedBound().min), so a tile whose
    footprint is smaller than its cell settles against the cell's low-x/low-z side however it is
    turned. Half the tilesets ship a wall that is 1 x 0.5, and without the footprint there is no
    way to push it back against the edge it is supposed to line.

    A group is the pack's own GroupTag. Most packs group by role, which tells us nothing, but
    some group by *family* instead — Castle Fortified's 'wall' and 'wall base' each hold a wall
    and a matching corner, in two different architectural styles. tile_catalog.json carries
    neither field.
    """
    sizes, groups = {}, {}
    for path in glob.glob(os.path.join(PACKS, "*", "index.json")):
        with open(path, encoding="utf-8") as f:
            for tile in json.load(f)["Tiles"]:
                extent = tile["ColliderBoundsBound"]["m_Extent"]
                sizes[tile["Id"]] = [round(extent["x"] * 2, 3), round(extent["z"] * 2, 3)]
                groups[tile["Id"]] = tile.get("GroupTag")
    return sizes, groups


def build() -> dict:
    index = tile_index()
    with open(SOURCE, encoding="utf-8") as f:
        data = json.load(f)

    unresolved = []
    for pack, tilesets in data["profiles"].items():
        for name, profile in tilesets.items():
            for role, entries in (profile.get("tilesByRole") or {}).items():
                for entry in entries:
                    tile = index.get(entry["id"])
                    if tile is None:
                        unresolved.append(f"{name}/{role}/{entry['id']}")
                        continue
                    entry["name"]   = tile["name"]
                    entry["size"]   = tile["size"]
                    entry["height"] = tile["height"]

    if unresolved:
        print(f"WARNING: {len(unresolved)} tile GUID(s) not in the catalog:", file=sys.stderr)
        for u in unresolved:
            print(f"  {u}", file=sys.stderr)

    for tilesets in data["profiles"].values():
        for profile in tilesets.values():
            order_candidates(profile)

    footprints, groups = pack_index()
    apply_picks(data, index)
    stamp_footprints(data, footprints)
    check_combo_flags(data)
    check_corner_pitch(data)
    check_wall_footprint(data)
    check_wall_family(data, groups)
    check_role_collisions(data)
    return data


def stamp_footprints(data: dict, sizes: dict) -> None:
    """Attach each tile's collider footprint, after the picks have settled the entry lists."""
    for tilesets in data["profiles"].values():
        for profile in tilesets.values():
            for entries in (profile.get("tilesByRole") or {}).values():
                for entry in entries:
                    footprint = sizes.get(entry["id"])
                    if footprint:
                        entry["footprint"] = footprint


def order_candidates(profile: dict) -> None:
    """Sort each role's 1x1 candidates best-first, so the generator's rule stays 'take the first'.

    All three themes verified in game use a wall and corner that carry a floor — they stand
    floorHeight taller than the set's bare wall. Preferring those means one tile fills a cell
    instead of a wall stacked on a separate floor, which is fewer tiles and no seam. Non-1x1
    candidates are kept but sorted last; they are the reason a role can look populated and still
    be unusable."""
    roles = profile.get("tilesByRole") or {}
    floor_h = profile.get("floorHeight") or 0.5

    for role, entries in roles.items():
        unit  = [e for e in entries if e.get("size") == "1x1"]
        other = [e for e in entries if e.get("size") != "1x1"]
        if not unit:
            continue

        if role in ("wall", "corner"):
            bare  = min(e["height"] for e in unit)
            combo = bare + floor_h
            unit.sort(key=lambda e: (abs(e["height"] - combo) > 0.01, e["height"]))
        elif role == "floor":
            unit.sort(key=lambda e: e["height"])

        roles[role] = unit + other


def apply_picks(data: dict, index: dict) -> None:
    """Hoist verified picks to the front of their role list so MapGen's 'first 1x1 candidate'
    rule lands on them. A pick missing from the curated list is inserted, because some correct
    tiles were never curated in the first place.

    Also carries the excluded flag across, for tilesets that look buildable on paper — three 1x1
    structural roles, all tiles 1x1 — but whose wall art is a post or an arch, so a ring of them
    never closes."""
    with open(PICKS, encoding="utf-8") as f:
        picks = json.load(f)

    by_name = {}
    for tilesets in data["profiles"].values():
        by_name.update(tilesets)

    for theme, pick in picks.items():
        if theme.startswith("_"):
            continue
        profile = by_name.get(theme)
        if profile is None:
            print(f"WARNING: picks name unknown tileset '{theme}'", file=sys.stderr)
            continue

        profile["verified"] = bool(pick.get("verified"))
        profile["excluded"] = bool(pick.get("excluded"))
        roles = profile.setdefault("tilesByRole", {})

        for role in ROLES:
            guid = pick.get(role)
            if not guid:
                continue
            tile = index.get(guid)
            if tile is None:
                print(f"WARNING: {theme}/{role} pick {guid} is not in the catalog", file=sys.stderr)
                continue
            entries = [e for e in roles.get(role, []) if e["id"] != guid]
            roles[role] = [{"id": guid, "name": tile["name"],
                            "size": tile["size"], "height": tile["height"]}] + entries

        # The reference flag is one boolean per tileset, but carrying a floor is a property of the
        # individual tile. Where a set holds both kinds, only the pick knows which one was taken,
        # and getting it wrong leaves the wall ring standing a floor's thickness below the room.
        if "hasIntegratedFloorWall" in pick:
            profile["hasIntegratedFloorWall"] = bool(pick["hasIntegratedFloorWall"])


def check_combo_flags(data: dict) -> None:
    """Warn where the picked wall disagrees with hasIntegratedFloorWall.

    Only meaningful when a set lists walls at two different heights, so the bare one is actually
    observable. A set whose 1x1 walls are all the same height tells us nothing either way — both
    themes verified in game are exactly that shape."""
    for tilesets in data["profiles"].values():
        for name, p in tilesets.items():
            unit = [e for e in (p.get("tilesByRole") or {}).get("wall", [])
                    if e.get("size") == "1x1"]
            heights = {e["height"] for e in unit}
            if len(heights) < 2:
                continue
            combo = min(heights) + (p.get("floorHeight") or 0.5)
            if (abs(unit[0]["height"] - combo) < 0.01) != bool(p.get("hasIntegratedFloorWall")):
                print(f"WARNING: {name} wall pick {unit[0]['height']} disagrees with "
                      f"hasIntegratedFloorWall={bool(p.get('hasIntegratedFloorWall'))}",
                      file=sys.stderr)


def picked_unit(profile: dict, role: str) -> dict:
    """The 1x1 tile the generator will actually resolve for a role, or None."""
    entries = (profile.get("tilesByRole") or {}).get(role) or []
    return next((e for e in entries if e.get("size") == "1x1"), None)


def check_corner_pitch(data: dict) -> None:
    """Warn where no corner in the set stands as tall as the picked wall.

    A corner is a wall that turns a right angle, so a shorter one leaves a notch in the top of the
    ring at every corner. MapGen resolves corners through PickOfHeight, which searches the whole
    candidate list for one matching the wall's pitch and only falls back to the first entry when
    there is none — so a corner pick out of order is harmless and only a set with nothing of the
    right height is a fault."""
    for tilesets in data["profiles"].values():
        for name, p in tilesets.items():
            if p.get("excluded"):
                continue
            wall = picked_unit(p, "wall")
            corners = [e for e in ((p.get("tilesByRole") or {}).get("corner") or [])
                       if e.get("size") == "1x1"]
            if not wall or not corners:
                continue
            if not any(abs(e["height"] - wall["height"]) < 0.01 for e in corners):
                heights = ", ".join(str(e["height"]) for e in corners)
                print(f"WARNING: {name} has no corner as tall as its {wall['height']} wall "
                      f"'{wall['name']}' — corners are {heights}", file=sys.stderr)


def check_wall_footprint(data: dict) -> None:
    """Warn on the two ways a wall pick's collider bounds contradict how it is being placed.

    A wall shallower than its cell cannot also be a floor, so hasIntegratedFloorWall over such a
    pick makes the builder seat the ring at y=0 with no floor laid — the same fault as bug C,
    arriving through curation instead of code. A wall deeper in z than in x was authored looking
    west instead of north; MapGen turns those onto its own convention, but which of the two faces
    ends up outward is not something the footprint settles, so say so. Desert Village and Shogun
    Palace have both since been looked at and come out finished-side-out. Neither check is
    derivable from height or size; both need the footprint."""
    for tilesets in data["profiles"].values():
        for name, p in tilesets.items():
            if p.get("excluded"):
                continue
            wall = picked_unit(p, "wall")
            footprint = wall and wall.get("footprint")
            if not footprint:
                continue
            fx, fz = footprint
            if p.get("hasIntegratedFloorWall") and fz < 0.99:
                print(f"WARNING: {name} is hasIntegratedFloorWall but its wall "
                      f"'{wall['name']}' is only {fz} deep, so it carries no floor",
                      file=sys.stderr)
            if fx < fz - 0.01:
                print(f"NOTE: {name} wall '{wall['name']}' is {fx}x{fz} — authored looking west. "
                      f"TileEntry.AuthoredRotBias turns it onto the convention, so the ring closes; "
                      f"what is left is a rig question about which face ends up outward",
                      file=sys.stderr)


def check_wall_family(data: dict, groups: dict) -> None:
    """Warn where a wall from the corner's own family was available and something else was picked.

    Only fires when the pack groups by family rather than by role, which is exactly the case where
    the corner's GroupTag also holds a wall. Castle Fortified's corner is 'wall base' and its
    picked wall was plain 'wall' — two styles that are the same size and the same height, so
    nothing already in the profile could tell them apart, and the mismatch showed up only as
    corners that did not match their walls in game."""
    for tilesets in data["profiles"].values():
        for name, p in tilesets.items():
            if p.get("excluded"):
                continue
            roles = p.get("tilesByRole") or {}
            walls   = [e for e in roles.get("wall", [])   if e.get("size") == "1x1"]
            corners = [e for e in roles.get("corner", []) if e.get("size") == "1x1"]
            if not walls or not corners:
                continue
            family = groups.get(corners[0]["id"])
            kin = [e for e in walls if groups.get(e["id"]) == family]
            if not kin or groups.get(walls[0]["id"]) == family:
                continue
            names = ", ".join(e["name"] for e in kin)
            print(f"WARNING: {name} wall pick '{walls[0]['name']}' is not from its corner's "
                  f"'{family}' family; available: {names}", file=sys.stderr)


def check_role_collisions(data: dict) -> None:
    """Warn where one theme picks the same tile for two roles.

    Nothing downstream can tell the two apart: the QA harness indexes placements by GUID to
    recover what role they were placed as, so a shared GUID makes every tile of one role read as
    the other. Four sets picked their stair *block* as their stairs, and the treads then reported
    as blocks and the flights as missing entirely."""
    for tilesets in data["profiles"].values():
        for name, p in tilesets.items():
            if p.get("excluded"):
                continue
            picked = {}
            for role in ROLES:
                entries = (p.get("tilesByRole") or {}).get(role) or []
                unit = next((e for e in entries if e.get("size") == "1x1"), None)
                if unit:
                    picked.setdefault(unit["id"], []).append(role)
            for guid, roles in picked.items():
                if len(roles) > 1:
                    print(f"WARNING: {name} picks one tile for {' and '.join(roles)} ({guid})",
                          file=sys.stderr)


def summarise(data: dict) -> None:
    usable = unusable = 0
    for tilesets in data["profiles"].values():
        for name, p in tilesets.items():
            roles = p.get("tilesByRole") or {}
            unit = {r: [e for e in es if e.get("size") == "1x1"] for r, es in roles.items()}
            core = all(unit.get(r) for r in ("wall", "floor", "corner"))
            if core and p.get("minTileDimension", 1) == 1 and not p.get("excluded"):
                usable += 1
            else:
                unusable += 1
    print(f"{usable} profiles can build a 1x1 map, {unusable} cannot")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true",
                    help="fail if the committed copy differs, instead of writing it")
    a = ap.parse_args()

    data = build()
    rendered = json.dumps(data, indent=2, ensure_ascii=False) + "\n"

    if a.check:
        with open(OUTPUT, encoding="utf-8") as f:
            if f.read() == rendered:
                print("up to date")
                return 0
        print(f"{OUTPUT} is stale — re-run without --check", file=sys.stderr)
        return 1

    with open(OUTPUT, "w", encoding="utf-8") as f:
        f.write(rendered)
    print(f"wrote {OUTPUT}")
    summarise(data)
    return 0


if __name__ == "__main__":
    sys.exit(main())
