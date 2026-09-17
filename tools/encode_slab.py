#!/usr/bin/env python3
"""
Encode a TaleSpire v2 slab from a list of tile placements.

Usage:
    python encode_slab.py [script_name]

script_name selects a hardcoded test layout (default: lbend_oc).
Prints the base64 slab string ready to paste into TaleSpire.

GUID bytes: C# Guid.ToByteArray() = UUID.bytes_le (mixed-endian).
Positions: integer * 100, 18-bit signed, packed into ulong.
Format: gzip(header + layout_headers + asset_data), base64.
"""

import base64, gzip, struct, sys, uuid as uuid_mod

MAGIC   = 0xD1CEFACE
VERSION = 2

# Rotation constants (15° per step, CCW)
ROT_NORTH = 0
ROT_WEST  = 6
ROT_SOUTH = 12
ROT_EAST  = 18

# Dungeon Cellar GUIDs
FLOOR_GUID        = "d5900784-9510-4cf7-b017-f369448d0d52"
WALL_GUID         = "ed0ad169-3248-411a-9eb0-4aada656fb61"
CORNER_GUID       = "fdd3c8dc-9c94-4a63-a7d9-10ae36d07fe7"
INNER_CORNER_GUID = "cec14f2e-faf2-4a9a-bd96-909c16b92197"


def guid_bytes_le(guid_str):
    """Convert GUID string to 16 bytes in C# mixed-endian format.

    Matches Guid.Parse(s).ToByteArray() exactly:
      bytes 0-3:  data1 little-endian
      bytes 4-5:  data2 little-endian
      bytes 6-7:  data3 little-endian
      bytes 8-15: data4 as-is (big-endian)
    """
    return uuid_mod.UUID(guid_str).bytes_le


def pack_asset(x, y, z, rot_step):
    """Pack x/y/z floats and rot_step int into an 8-byte little-endian ulong.

    Mirrors C# PackAsset: (ulong)(uint)(int)(v*100) & 0x3FFFF for each axis.
    The double-cast (uint)(int) preserves two's-complement sign extension into
    the upper bits, then & 0x3FFFF keeps only the 18 least significant bits.
    """
    def scaled(v):
        i = int(v * 100.0)          # float → int (truncate, matching C# cast)
        return i & 0x3FFFF          # keep 18 bits (handles negatives via wrap)

    sx  = scaled(x)
    sy  = scaled(y)
    sz  = scaled(z)
    rot = rot_step & 0x1F           # 5 bits

    packed = sx | (sy << 18) | (sz << 36) | (rot << 54)
    return struct.pack('<Q', packed)


def encode(placements):
    """Encode a list of (guid_str, x, y, z, rot_step) into a slab base64 string."""
    # Group by GUID, preserving insertion order (dict in Python 3.7+)
    layouts = {}
    for (g, x, y, z, rot) in placements:
        if g not in layouts:
            layouts[g] = []
        layouts[g].append((x, y, z, rot))

    buf = bytearray()

    # Header: magic(4) + version(2) + layoutCount(2) + creatureCount(2)
    buf += struct.pack('<I', MAGIC)
    buf += struct.pack('<H', VERSION)
    buf += struct.pack('<H', len(layouts))
    buf += struct.pack('<H', 0)            # creature count

    # Layout headers: guid(16) + assetCount(2) + padding(2)
    for g, assets in layouts.items():
        buf += guid_bytes_le(g)
        buf += struct.pack('<H', len(assets))
        buf += struct.pack('<H', 0)

    # Asset data
    for g, assets in layouts.items():
        for (x, y, z, rot) in assets:
            buf += pack_asset(x, y, z, rot)

    compressed = gzip.compress(bytes(buf), compresslevel=9)
    return base64.b64encode(compressed).decode('ascii')


# ── Test layouts ──────────────────────────────────────────────────────────────

def lbend_oc():
    """
    Minimal L-bend with one OC corner tile to test outer-corner placement.

    Layout (looking down, x→right, z→down):

          x:  4   5   6   7   8
      z=4:  [OC] [W] [W] [W] [W]   OC = NW corner at (4,4) rot=N=0
      z=5:  [W]  [F] [F] [F] [F]   elbow floor at (5,5)
      z=6:  [W]  [F]
      z=7:  [W]  [F]

    14 tiles total.
    All tiles at y=0.
    """
    p = []

    # OC corner tile: NW outer corner at (4,4)
    p.append((CORNER_GUID, 4.0, 0.0, 4.0, ROT_NORTH))

    # North-facing walls along top edge (z=4, x=5..8)
    for x in [5, 6, 7, 8]:
        p.append((WALL_GUID, float(x), 0.0, 4.0, ROT_NORTH))

    # West-facing walls along left edge (x=4, z=5..7)
    for z in [5, 6, 7]:
        p.append((WALL_GUID, 4.0, 0.0, float(z), ROT_WEST))

    # Floor tiles: horizontal leg (z=5, x=5..8)
    for x in [5, 6, 7, 8]:
        p.append((FLOOR_GUID, float(x), 0.0, 5.0, ROT_NORTH))

    # Floor tiles: vertical leg (x=5, z=6..7)
    for z in [6, 7]:
        p.append((FLOOR_GUID, 5.0, 0.0, float(z), ROT_NORTH))

    return p


def single_corner():
    """Just one corner tile plus a floor beneath it, to test bare placement."""
    return [
        (FLOOR_GUID,  0.0, 0.0, 0.0, ROT_NORTH),
        (CORNER_GUID, 0.0, 0.0, 0.0, ROT_NORTH),
    ]


def bend1_exact():
    """
    L-bend matching real dungeon bend1 exactly.
    Elbow at (14,17), OC at (13,16).
    Corridor goes west (x=18..15 at z=17) then south (x=14, z=17..20).

    Layout in my (x,z) coords:
          x: 13  14  15  16  17  18
      z=16: [OC] [N] [N] [N] [N] [N]     N = north-facing wall
      z=17: [W]  [E] [h] [h] [h] [h]     E = elbow floor, h = horizontal floor, W = west wall
      z=18: [W]  [v]
      z=19: [W]  [v]
      z=20: [W]  [v]
    """
    p = []

    # OC corner: west-then-south bend → NW outer corner → ROT_NORTH=0
    p.append((CORNER_GUID, 13.0, 0.0, 16.0, ROT_NORTH))

    # North-facing walls (z=16 row, x=14..18)
    for x in [14, 15, 16, 17, 18]:
        p.append((WALL_GUID, float(x), 0.0, 16.0, ROT_NORTH))

    # West-facing walls (x=13, z=17..20)
    for z in [17, 18, 19, 20]:
        p.append((WALL_GUID, 13.0, 0.0, float(z), ROT_WEST))

    # Horizontal leg floors: z=17, x=18..15 (elbow at x=14 is vertical start)
    for x in [18, 17, 16, 15]:
        p.append((FLOOR_GUID, float(x), 0.0, 17.0, ROT_NORTH))

    # Vertical leg floors: x=14, z=17..20
    for z in [17, 18, 19, 20]:
        p.append((FLOOR_GUID, 14.0, 0.0, float(z), ROT_NORTH))

    return p


def bend2_exact():
    """
    L-bend matching real dungeon bend2 exactly.
    Elbow at (22,35), OC at (23,34).
    Corridor goes east (x=18..21 at z=35) then south (x=22, z=35..38).

    Layout in my (x,z) coords:
          x: 18  19  20  21  22  23
      z=34: [N]  [N] [N] [N] [N] [OC]    N = north-facing wall
      z=35: [h]  [h] [h] [h] [E] [W]     E = elbow, h = horizontal floor, W = east-side wall
      z=36:               [v] [W]
      z=37:               [v] [W]
      z=38:               [v] [W]

    OC at (23,34): east-then-south bend → NE outer corner → ROT_EAST=18
    """
    p = []

    # OC corner: east-then-south bend → NE outer corner → ROT_EAST=18
    p.append((CORNER_GUID, 23.0, 0.0, 34.0, ROT_EAST))

    # North-facing walls (z=34, x=18..22)
    for x in [18, 19, 20, 21, 22]:
        p.append((WALL_GUID, float(x), 0.0, 34.0, ROT_NORTH))

    # East-facing walls (x=23, z=35..38)
    for z in [35, 36, 37, 38]:
        p.append((WALL_GUID, 23.0, 0.0, float(z), ROT_EAST))

    # Horizontal leg floors: z=35, x=18..21
    for x in [18, 19, 20, 21]:
        p.append((FLOOR_GUID, float(x), 0.0, 35.0, ROT_NORTH))

    # Vertical leg floors: x=22, z=35..38
    for z in [35, 36, 37, 38]:
        p.append((FLOOR_GUID, 22.0, 0.0, float(z), ROT_NORTH))

    return p


def wall_rot_probe():
    """
    Where inside its cell does a wall tile's plate sit, per rotation? Answer: against
    the low-x/low-z side every time, because a stored position is the minimum corner of
    the tile's world bounding box. So a 1 x 0.5 wall lines the cell edge facing north or
    west and sits half a cell in facing south or east — see bug 25 in MapGen/CLAUDE.md.

    Positions here are raw, with none of SlabBuilder's edge correction, so the probe
    shows that rule directly. Each rotation gets one isolated floor tile with one wall
    on it and empty board all round, so the floor square reads as a ruler and the wall
    as a bar along one of its edges. Dungeon Cellar (combo, fills its cell) is the
    control row against Castle Fortified (separate wall, 1 x 0.5).

    An L of bare floor tiles at the origin fixes which way +X and +Z point on screen,
    because the board camera faces west and the compass cannot be assumed.
    """
    CF_WALL  = "d3f7dbbf-6f78-4c5b-93e3-a6c1d00cbefd"
    CF_FLOOR = "a2eaf2e2-01d8-4548-a517-50df8551057a"

    p = []

    # Compass L: 4 tiles running +X, 2 running +Z, sharing the corner at (10,10).
    for x in range(10, 14): p.append((FLOOR_GUID, float(x), 0.0, 10.0, ROT_NORTH))
    for z in range(11, 13): p.append((FLOOR_GUID, 10.0, 0.0, float(z), ROT_NORTH))

    rots = [("N", ROT_NORTH), ("W", ROT_WEST), ("S", ROT_SOUTH), ("E", ROT_EAST)]

    # Control row (combo tileset) at z=16; test row (separate floor+wall) at z=22.
    for zrow, floor, wall, wy in [(16, FLOOR_GUID, WALL_GUID,  0.0),
                                  (22, CF_FLOOR,   CF_WALL,    0.5)]:
        for i, (_, rot) in enumerate(rots):
            x = 10.0 + i * 4
            p.append((floor, x, 0.0, float(zrow), ROT_NORTH))
            p.append((wall,  x, wy,  float(zrow), rot))

    return p


SCRIPTS = {
    "lbend_oc":       lbend_oc,
    "single_corner":  single_corner,
    "bend1_exact":    bend1_exact,
    "bend2_exact":    bend2_exact,
    "wall_rot_probe": wall_rot_probe,
}

if __name__ == "__main__":
    name = sys.argv[1] if len(sys.argv) > 1 else "lbend_oc"
    if name not in SCRIPTS:
        print(f"Unknown script '{name}'. Available: {', '.join(SCRIPTS)}")
        sys.exit(1)
    placements = SCRIPTS[name]()
    print(encode(placements))
