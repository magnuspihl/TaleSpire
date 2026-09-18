#!/usr/bin/env python3
"""Build MapGen/prop_catalog.json from the asset packs' own indexes.

    python3 tools/build-mapgen-props.py [--check]

Furniture is not in tile_catalog.json at all. The packs carry a separate `Props` section holding
1500 objects, each already labelled by Bouncyrock with a functional `GroupTag` — "bed", "chair",
"crate", "console" — which is the taxonomy the recipes are written against. Nothing here is
curated: every field is derived from the pack index, so adding a pack is a re-run.

The output is keyed `style/group` because that is exactly how a recipe asks for props: "give me a
medieval bed". Recipes name groups, never GUIDs, which is what keeps 1500 props reachable from
twenty-odd lines of authored data.

--check verifies the committed copy is up to date without writing, for use in CI.
"""

import argparse
import glob
import json
import os
import sys

HERE   = os.path.dirname(os.path.abspath(__file__))
PACKS  = "/home/coder/TaleSpire-Binaries/Taleweaver"
OUTPUT = os.path.join(HERE, "..", "MapGen", "prop_catalog.json")

# Nature is the largest prop folder by some way, but it is the courtyard/exterior story rather than
# the room-dressing one, and it is deliberately held back so the outdoor pass can be judged on its
# own. Splats are blood/paint decals that lie flat on the floor and want a placement rule of their
# own rather than the furniture one.
SKIP_FOLDERS = {"Nature", "Splats"}

# The pack GUIDs, and the art style each one dresses a room in. Recipes select on this, because
# prop folders are functional rather than per-tileset — there is no "Castle Fortified furniture" —
# so a coarse medieval/modern split is the only theming the data actually supports.
STYLES = {
    "d71427a1-5535-4fa7-82d7-4ca1e75edbfd": "medieval",
    "fc3a4efc-e8d9-4d82-8620-f26c5b66023c": "modern",
}


def classify_mount(centre_y: float, extent_y: float) -> str:
    """floor or hanging, from where the pivot sits inside the tile's own bounds.

    A prop's stored position is a *pivot*: AssetMath.RotatePropBounds builds the bounds around it
    rather than treating it as a corner. For 89% of props the pivot sits on the bottom face, so
    placing one at the floor surface stands it on the floor. The rest are pivoted at the top and
    hang below it — screens, curtains, prison wall cuffs, chains. That is not a label anyone wrote
    down, but it falls straight out of the bounds, and it is the difference between a curtain on a
    wall and a curtain buried in the floor.
    """
    return "floor" if abs(centre_y - extent_y) < 0.02 else "hanging"


def build() -> dict:
    groups, skipped, deprecated = {}, 0, 0

    for path in sorted(glob.glob(os.path.join(PACKS, "*", "index.json"))):
        pack = os.path.basename(os.path.dirname(path))
        style = STYLES.get(pack)
        if style is None:
            print(f"WARNING: pack {pack} has no style mapping — skipped", file=sys.stderr)
            continue

        with open(path, encoding="utf-8") as f:
            for prop in json.load(f)["Props"]:
                folder = prop.get("Folder") or ""
                if folder in SKIP_FOLDERS:
                    skipped += 1
                    continue
                if prop.get("IsDeprecated"):
                    deprecated += 1
                    continue

                group = (prop.get("GroupTag") or "").strip()
                if not group or group == "GroupTagName":
                    # A handful of props carry the pack author's placeholder tag verbatim. They are
                    # unreachable by any recipe, which is the correct outcome — a recipe asking for
                    # "GroupTagName" is a typo, not an intent.
                    skipped += 1
                    continue

                bounds = prop["ColliderBoundsBound"]
                centre, extent = bounds["m_Center"], bounds["m_Extent"]

                key = f"{style}/{group.lower()}"
                groups.setdefault(key, []).append({
                    "id":        prop["Id"],
                    "name":      prop["Name"],
                    "folder":    folder,
                    "mount":     classify_mount(centre["y"], extent["y"]),
                    "footprint": [round(extent["x"] * 2, 3), round(extent["z"] * 2, 3)],
                    "height":    round(extent["y"] * 2, 3),
                    # How far the bounds centre sits from the pivot on the horizontal plane. Zero
                    # for two thirds of props; the rest need it to sit flush against a wall.
                    "pivot":     [round(centre["x"], 3), round(centre["z"], 3)],
                })

    for entries in groups.values():
        entries.sort(key=lambda e: e["name"])

    print(f"{sum(len(v) for v in groups.values())} props in {len(groups)} groups "
          f"({skipped} skipped, {deprecated} deprecated)", file=sys.stderr)
    return {"groups": dict(sorted(groups.items()))}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true",
                    help="fail if the committed copy differs, instead of writing it")
    a = ap.parse_args()

    rendered = json.dumps(build(), indent=2, ensure_ascii=False) + "\n"

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
    return 0


if __name__ == "__main__":
    sys.exit(main())
