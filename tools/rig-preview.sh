#!/usr/bin/env bash
# Paste a slab as a preview, screenshot it, and cancel — without ever committing it to the board.
#
#     tools/rig-preview.sh /tmp/foo.slab /tmp/shot.png [x] [y] [zoom] [orbit]
#
# A left-click commits a paste onto Magnus's campaign board and TaleSpire has no bulk delete, so
# this only ever issues button 3. Build mode is re-entered first: a cancelled paste sometimes drops
# the game out of it, and a paste outside build mode is silently discarded.
#
# zoom is a number of scroll-in steps from fully zoomed out, not a delta. The camera keeps whatever
# zoom the previous run left it at, so two identical invocations otherwise frame the board
# differently — and a shot taken from inside a wall looks like a broken tileset. 0 means leave the
# camera alone. Around 3 frames a row of probe rooms; 7 is close enough to read a door.
#
# orbit is a count of Q (negative) or E (positive) presses, which swing the camera around the
# board. From directly above, a door reads as a gap in a line of wall tops and nothing more — the
# face it sits in has to be turned towards the camera before it can be judged at all. A press is
# only a degree or two, so a quarter turn wants tens of them, not a handful.
#
# What this cannot do: frame a whole map, or aim at a chosen cell. The zoom-out is clamped at
# roughly the screen width of one `small` map's diamond, x/y is the only aim control, and the
# preview is *stationary* once pasted — moving the mouse afterwards does not slide it, so the
# paste position is the single shot you get. Anchor movement does translate the map 1:1 in screen
# pixels, which is enough to walk along an edge but not to reach a cell tens of cells inland.
# To judge one specific tile, build a slab that contains little else; to judge a tile in a full
# map, find a tile the generator places by the same code path and judge that instead.
set -eu

SLAB=$1
OUT=${2:-/tmp/rig-preview.png}
X=${3:-700}
Y=${4:-500}
ZOOM=${5:-0}
ORBIT=${6:-0}
HERE=$(cd "$(dirname "$0")" && pwd)

rig() { python3 "$HERE/rig.py" "$@" > /dev/null; }

# Clear any paste left pending by a previous run with button 3 rather than Escape: Escape cancels a
# paste too, but with nothing pending it opens the pause menu instead, and the menu then swallows
# every key that follows — including the b that re-enters build mode.
rig click "$X" "$Y" --button 3; sleep 1
rig key b;                      sleep 2
rig clipboard --file "$SLAB"
rig move "$X" "$Y"
rig paste

if [ "$ZOOM" -gt 0 ]; then
    for _ in $(seq 25); do rig click "$X" "$Y" --button 5; done
    for _ in $(seq "$ZOOM"); do rig click "$X" "$Y" --button 4; sleep 0.4; done
fi

if [ "$ORBIT" -ne 0 ]; then
    key=e; n=$ORBIT
    [ "$n" -lt 0 ] && { key=q; n=$(( -n )); }
    for _ in $(seq "$n"); do rig key "$key"; sleep 0.4; done
fi
# A thousand-tile map streams in over several seconds. Shooting too early grades a half-loaded
# preview, which reads as a scatter of floating panels and looks exactly like a broken tileset —
# three themes were written off that way before the wait was long enough.
sleep 12
python3 "$HERE/rig.py" screenshot "$OUT"
rig click "$X" "$Y" --button 3
