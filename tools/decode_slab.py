#!/usr/bin/env python3
"""
Decode and diff TaleSpire v2 slab strings.

Usage:
    python decode_slab.py <slab_a> [slab_b]

If two slabs are given, prints a diff: tiles only in A, only in B, and common.
If one slab is given, prints all tiles.

Tile positions are stored as integer * 100 in 18-bit signed fields.
Rotation is a 5-bit field (0-23, steps of 15°).
"""

import sys, base64, gzip, struct, json
from collections import defaultdict

# ── GUID lookup (sourced from TileCatalog.cs) ────────────────────────────────
GUID_NAMES = {
    # Dungeon Cellar
    "d5900784-9510-4cf7-b017-f369448d0d52": "DC Floor",
    "ed0ad169-3248-411a-9eb0-4aada656fb61": "DC Wall",
    "fdd3c8dc-9c94-4a63-a7d9-10ae36d07fe7": "DC Corner",
    "cec14f2e-faf2-4a9a-bd96-909c16b92197": "DC InnerCorner",
    "3151e230-bac7-401e-8fa8-b993bbee17ea": "Door",
    "ed072f00-f245-44e7-9d25-6a1cb498cbaa": "Dungeon Stairs",
    # MegaDungeon
    "940d013f-c982-43e7-922a-0317cc1c74db": "MD Floor",
    "c8312af8-8bfb-41e1-bb6e-30280686b168": "MD Wall",
    "0820791e-c1a8-4a1e-b690-f34b0cb276ba": "MD Corner",
    "bb8e7378-44da-4556-bac5-92420f766815": "MD InnerCorner",
    # Sewers
    "81a8acd3-0685-44ce-b185-cb70c58ae68f": "Sewer Floor",
    "887d5410-4c59-40a3-b338-8d8c2efaafd3": "Sewer Wall",
    "180105d6-8c75-4da0-85ae-251aa3145e98": "Sewer Corner",
    "d172bb08-175b-495a-bd50-22f4135d5209": "Sewer InnerCorner",
}

ROT_NAMES = {0: "N", 6: "W", 12: "S", 18: "E"}

def rot_name(r):
    return ROT_NAMES.get(r, str(r))

def unpack_asset(val):
    """Unpack a 64-bit asset word into (x, y, z, rot) as floats/int."""
    def signed18(v):
        v &= 0x3FFFF
        if v & 0x20000:
            v -= 0x40000
        return v

    sx  = signed18(val & 0x3FFFF)
    sy  = signed18((val >> 18) & 0x3FFFF)
    sz  = signed18((val >> 36) & 0x3FFFF)
    rot = int((val >> 54) & 0x1F)
    return sx / 100.0, sy / 100.0, sz / 100.0, rot

def decode_slab(b64_string):
    """Decode a slab string into a list of (guid_str, x, y, z, rot) tuples."""
    # Strip whitespace/newlines that might appear in pasted strings
    b64_string = b64_string.strip().replace('\n', '').replace('\r', '').replace(' ', '')
    raw = gzip.decompress(base64.b64decode(b64_string))
    buf = memoryview(raw)
    off = 0

    magic, version, layout_count, creature_count = struct.unpack_from('<IHHh', buf, off)
    off += 10
    assert magic == 0xD1CEFACE, f"Bad magic: {magic:#x}"

    # Read layout headers
    layouts = []
    for _ in range(layout_count):
        guid_bytes = bytes(buf[off:off+16])
        asset_count, _pad = struct.unpack_from('<HH', buf, off+16)
        off += 20
        # Convert to standard UUID string
        g = guid_bytes
        guid_str = (
            f"{g[3]:02x}{g[2]:02x}{g[1]:02x}{g[0]:02x}-"
            f"{g[5]:02x}{g[4]:02x}-"
            f"{g[7]:02x}{g[6]:02x}-"
            f"{g[8]:02x}{g[9]:02x}-"
            f"{g[10]:02x}{g[11]:02x}{g[12]:02x}{g[13]:02x}{g[14]:02x}{g[15]:02x}"
        )
        layouts.append((guid_str, asset_count))

    tiles = []
    for guid_str, asset_count in layouts:
        for _ in range(asset_count):
            val = struct.unpack_from('<Q', buf, off)[0]
            off += 8
            x, y, z, rot = unpack_asset(val)
            tiles.append((guid_str, x, y, z, rot))

    return tiles

def tile_key(t):
    guid, x, y, z, rot = t
    return (round(x, 2), round(y, 2), round(z, 2), rot, guid)

def tile_str(t):
    guid, x, y, z, rot = t
    name = GUID_NAMES.get(guid, guid)
    return f"({x:6.2f},{y:5.2f},{z:6.2f}) rot={rot_name(rot):>2} [{name}]"

def print_tiles(tiles, label=""):
    if label:
        print(f"\n=== {label} ({len(tiles)} tiles) ===")
    # Sort by z, x, y for readability
    for t in sorted(tiles, key=lambda t: (round(t[3]/100), round(t[1]), round(t[2]))):
        print(" ", tile_str(t))

def diff_slabs(tiles_a, tiles_b):
    set_a = {tile_key(t): t for t in tiles_a}
    set_b = {tile_key(t): t for t in tiles_b}

    keys_a = set(set_a)
    keys_b = set(set_b)

    only_a = keys_a - keys_b
    only_b = keys_b - keys_a
    common = keys_a & keys_b

    print(f"\n{'='*60}")
    print(f"Slab A: {len(tiles_a)} tiles   Slab B: {len(tiles_b)} tiles")
    print(f"Common: {len(common)}   Only in A (removed): {len(only_a)}   Only in B (added): {len(only_b)}")
    print(f"{'='*60}")

    if only_a:
        print(f"\n--- REMOVED (in A but not B) ---")
        for k in sorted(only_a, key=lambda k: (k[2], k[0], k[1])):
            print(f"  - {tile_str(set_a[k])}")

    if only_b:
        print(f"\n+++ ADDED (in B but not A) +++")
        for k in sorted(only_b, key=lambda k: (k[2], k[0], k[1])):
            print(f"  + {tile_str(set_b[k])}")

    if not only_a and not only_b:
        print("\n(Slabs are identical)")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: decode_slab.py <slab_a> [slab_b]")
        sys.exit(1)

    tiles_a = decode_slab(sys.argv[1])

    if len(sys.argv) >= 3:
        tiles_b = decode_slab(sys.argv[2])
        diff_slabs(tiles_a, tiles_b)
    else:
        print_tiles(tiles_a, label="Slab")
