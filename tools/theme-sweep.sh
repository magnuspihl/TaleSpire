#!/usr/bin/env bash
# Screenshot one generated map per theme on the rig, without ever touching the board.
#
#     tools/theme-sweep.sh /tmp/themes [seed] [size] [zoom]
#
# Each map is pasted as a preview and cancelled, via tools/rig-preview.sh — a left-click would
# commit it onto Magnus's campaign board, and TaleSpire has no bulk delete.
set -u

OUT=${1:-/tmp/themes}
SEED=${2:-7}
SIZE=${3:-small}
# Absolute, not a delta: the camera keeps whatever zoom the last session left it at, so without
# this a sweep can be shot from inside a wall and every theme graded as broken.
ZOOM=${4:-2}
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(dirname "$HERE")
QA="$REPO/tools/MapGenQA"

mkdir -p "$OUT"

mapfile -t THEMES < <(python3 - "$REPO" <<'PY'
import json, sys
d = json.load(open(f"{sys.argv[1]}/MapGen/tileset_profiles.json"))
for pack in d["profiles"].values():
    for name, p in pack.items():
        roles = p.get("tilesByRole") or {}
        unit = {r: [e for e in roles.get(r, []) if e.get("size") == "1x1"]
                for r in ("wall", "floor", "corner")}
        if all(unit.values()) and p.get("minTileDimension", 1) == 1 and not p.get("excluded"):
            print(name)
PY
)

for theme in "${THEMES[@]}"; do
    slug=$(echo "$theme" | tr ' /' '__')
    echo "=== $theme"
    (cd "$QA" && dotnet run -c Debug --no-build -- \
        slab --seed "$SEED" --size "$SIZE" --theme "$theme") > "$OUT/$slug.slab" || continue
    "$HERE/rig-preview.sh" "$OUT/$slug.slab" "$OUT/$slug.png" 700 500 "$ZOOM" > /dev/null
done

echo "wrote $(ls "$OUT"/*.png | wc -l) screenshots to $OUT"
