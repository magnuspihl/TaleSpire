## MapGen: Map Generation System

### Build
Always build with `dotnet build -c Debug` (never `-c Release`). After any build, the user must **fully restart TaleSpire** before testing — stale DLLs cause the old code to stay loaded and produce wrong in-game results.

### Pipeline overview

```
LayoutSpec → SlabBuilder.Build() → List<(guid,x,y,z,rot)> → SlabEncoder.Encode() → base64 slab
```

`BuildRoom()` handles each room independently. `BuildCorridors()` handles all corridors in a shared multi-phase pipeline so crossing corridors interact correctly.

---

### TaleSpire coordinate system

| Axis | Direction        | ASCII art |
|------|------------------|-----------|
| X    | increases East   | column right |
| Z    | increases South  | row down  |
| Y    | increases Up     | elevation |

TaleSpire's default camera faces **West**, so "up on screen" = –X (West). When users describe what they see and say "north," they often mean West in TaleSpire's actual coordinate space.

Rotation is **counter-clockwise**, 15° per step (24 steps = 360°):

| Constant   | Value | Direction |
|------------|-------|-----------|
| ROT_NORTH  | 0     | –Z        |
| ROT_WEST   | 6     | –X        |
| ROT_SOUTH  | 12    | +Z        |
| ROT_EAST   | 18    | +X        |

**Wall tile rotation = the direction the wall faces** (the side it blocks). A wall on the north edge of a floor tile uses `ROT_NORTH`.

**Corner tile rotation = the corner's open exterior**:
- `ROT_NORTH` (0): NW outer corner — walls face N and W
- `ROT_WEST` (6): SW outer corner — walls face S and W
- `ROT_SOUTH` (12): SE outer corner — walls face S and E
- `ROT_EAST` (18): NE outer corner — walls face N and E

**Sub-tile snap grid**: each 1×1 cell has a 2×2 grid. Valid sub-tile offsets are `0` and `0.5f` only — **never 0.25f**. Quadrants: NW=(0,0), NE=(0.5,0), SW=(0,0.5), SE=(0.5,0.5).

**Tile elevation rules**:
- Floor, wall, corner tiles: at `y` (integer)
- Door tiles: `y + 0.5f` (confirmed in-game — placing at `y` makes doors invisible/flush)
- Inner corner filler tile: `y + 0.5f`

---

### Corridor generation: the multi-phase pipeline

`BuildCorridors` runs in five phases over all corridors at once. The shared `globalFloors` set (all corridor floor positions) and `wallRots` map (wall note accumulator) are what make crossing corridors interact correctly.

#### CorridorRec fields
- `X1,Z1` → start of horizontal leg; `X2,Z2` → end of vertical leg
- `XStep`/`ZStep` → sign of travel direction in each axis (+1 = east/south, -1 = west/north)
- `hasBend = (X1 != X2 && Z1 != Z2)` — true for L-bends, false for straight corridors
- `FromSkip`/`ToSkip` → wall direction suppressed at each room end (`= OppositeDir(doorWall)`)
- `Positions` → full list of `(x,z)` floor tile positions

Position generation:
```csharp
for (int x = x1; x != x2; x += xStep) positions.Add((x, z1));  // horizontal leg
for (int z = z1; z != z2 + zStep; z += zStep) positions.Add((x2, z)); // vertical leg
```
For straight corridors (z1==z2) this degenerates to a single row.

#### Preamble: roomPositions
Before any phase, `BuildCorridors` builds `roomPositions` — every `(x,z)` cell belonging to any room (walls, corners, door gaps, interior floors). `NoteWall` silently drops any note that lands on a room cell: **the room tile always wins**. This prevents corridor wall tiles from stacking on top of room corner tiles at junction points.

#### Phase 1 — floors + globalFloors
Emit a floor tile for every position in every corridor. Add all positions to `globalFloors`. The shared floor set is critical: it prevents corridors from walling off each other at crossings, and makes crossing corridors accumulate dual wall notes on their shared diagonal positions.

#### Phase 2 — wall notes (wallRots)
For each corridor, walk every floor tile and try to note walls in all 4 directions. `NoteWall(wx, wz, rot)` adds `rot` to `wallRots[(wx,wz)]`. Two suppression rules:

1. **L-bend IC position** (`hasBend && (wx,wz) == (X2-XStep, Z1+ZStep)`): skip — Phase 4 handles this explicitly.
2. **Room positions** (`roomPositions.Contains((wx,wz))`): skip — room tile wins.

Additionally, `atFrom`/`atTo` flags suppress the wall note on the room-facing side:
- At the first tile (`atFrom`): suppress the direction matching `c.FromSkip`
- At the last tile (`atTo`): suppress the direction matching `c.ToSkip`

When two corridors cross, the 4 diagonal cells each accumulate notes from both corridors in perpendicular directions — Phase 3 resolves these to inner corners.

#### Phase 2.5 — strip the L-bend OC position from wallRots
The vertical-leg tiles note the outer corner position as a straight wall. Strip it so Phase 3 doesn't place a wall there; Phase 4 places the correct corner tile.

**Exactly one OC per L-bend** — the "near" outer corner at `(X2+XStep, Z1-ZStep)`.

```csharp
bool hasBend = (c.X1 != c.X2 && c.Z1 != c.Z2);
if (!hasBend) continue;  // straight corridors have no OC
int ocx = c.X2 + c.XStep, ocz = c.Z1 - c.ZStep;
if (!globalFloors.Contains((ocx, ocz))) wallRots.Remove((ocx, ocz));
```

**Critical guard: use `hasBend`, not `Z1==Z2`.** Using `Z1==Z2` incorrectly skips purely vertical straight corridors (`X1==X2`), which would then have their OC position incorrectly stripped and lose wall tiles.

**Do NOT add a "far" OC** at `(X2+XStep, Z1+ZStep)`. This was tried and caused wrong/duplicate tiles inside straight wall columns. Revert immediately if re-added.

#### Phase 2.7 — terminal corridor-end corner gaps
Phase 2 only visits direct neighbors of floor tiles. The diagonal position where a corridor's end-cap wall meets its side wall is never adjacent to any floor tile and therefore receives no note. For example, when a straight corridor exits a room going west:

```
FFFW----    ← F = corridor floor, W = end-cap wall at x+1
WWW?----    ← W = side walls, ? = missing corner (diagonal from last F)
```

Phase 2.7 detects these by checking all 4 diagonals of every floor tile. If both perpendicular neighbors are in `wallRots` with rotations pointing **away** from the floor tile, the diagonal is an unhandled corner:

```csharp
int sideRot = dx > 0 ? ROT_EAST : ROT_WEST;   // faces away in X
int endRot  = dz > 0 ? ROT_SOUTH : ROT_NORTH;  // faces away in Z
// if wallRots[(fx+dx, fz)] contains sideRot AND wallRots[(fx, fz+dz)] contains endRot:
//   → emit Corner at (fx+dx, fz+dz) with CornerRotFromFacings([sideRot, endRot])
```

Deduplication guards:
- Skip if `globalFloors.Contains((cx,cz))` — it's a floor tile
- Skip if `wallRots.ContainsKey((cx,cz))` — Phase 3 already handles it
- Skip if `handledOC.Contains((cx,cz))` — it's a Phase 4 L-bend OC (would double-place)
- Skip if `emitted27.Contains((cx,cz))` — already emitted by this phase (dedup across corridors)

#### Phase 3 — emit walls and inner corners
For each entry in `wallRots`:
- **1 note**: emit a straight wall tile at that rotation
- **2+ notes**: emit a floor tile + inner corner filler tile at `y+0.5f`

Inner corner filler sub-tile offset (the "pocket" quadrant):
```csharp
float icXOff = rots.Contains(ROT_EAST)  ? 0.5f : 0f;
float icZOff = rots.Contains(ROT_SOUTH) ? 0.5f : 0f;
```
`ROT_EAST` note = corridor is to the +X side = filler shifts +0.5 in X.
`ROT_SOUTH` note = corridor is to the +Z side = filler shifts +0.5 in Z.

#### Phase 4 — explicit L-bend inner and outer corners
Only fires when `hasBend = true`.

**Inner corner** at `(X2-XStep, Z1+ZStep)` (the concave pocket of the bend):
- Skip if it's a global floor tile (another corridor passes through)
- Sub-tile offsets: `icXOff = (XStep < 0) ? 0.5f : 0f`, `icZOff = (ZStep > 0) ? 0.5f : 0f`
- Emits: floor tile + inner corner filler at `y+0.5f`

**Outer corner** at `(X2+XStep, Z1-ZStep)` (the convex outside of the bend):
- `OuterCornerRot(XStep, ZStep)`: `(+,+)→E, (+,-)→S, (-,+)→N, (-,-)→W`
- Emits: corner tile only (no floor tile underneath)

---

### Common bugs and their fixes (learned the hard way)

**1. Stale DLL / in-game results don't match code**
Always fully restart TaleSpire after every build. The DLL is loaded once at launch — changes don't hot-reload. This caused many apparent logic bugs that disappeared after a restart.

**2. Door tiles invisible / flush with floor**
Door tiles must be placed at `y + 0.5f`, not `y`. Placing at `y` makes them sit flush and effectively invisible.

**3. Inner corner filler in wrong quadrant**
Sub-tile offsets must be `0` or `0.5f` — never `0.25f`. The formula above (ROT_EAST → +0.5 in X, ROT_SOUTH → +0.5 in Z) is confirmed correct.

**4. Walls appearing over room corner tiles (stacking)**
Corridor wall notes must not land on room tile positions. Suppressed via the `roomPositions` guard in `NoteWall`. Without this, a corridor running along a room wall causes both a corridor wall tile AND the room corner tile to be placed at the same position.

**5. L-bend outer corner missing**
The OC position `(X2+XStep, Z1-ZStep)` is noted as a straight wall by the vertical-leg tiles. Phase 2.5 must strip it from `wallRots` before Phase 3 runs. Phase 4 then places the correct corner tile there.

**6. Straight corridor terminal corner missing**
The diagonal position at a corridor's open end has no adjacent floor tile and never gets a Phase 2 note. Phase 2.7 detects and fills it. Without Phase 2.7, L-shaped corridor/room junctions are missing a corner at the terminal end.

**7. Phase 2.5 stripping walls from straight vertical corridors**
Using `if (c.Z1 == c.Z2) continue` in Phase 2.5 is wrong. It skips horizontal corridors but leaves purely vertical corridors (`X1==X2`) unguarded, stripping their wall tiles. Use `if (!hasBend) continue` instead.

**8. "Far" outer corner causing duplicate tiles in walls**
Adding a second OC at `(X2+XStep, Z1+ZStep)` was tried and caused visual regressions in straight wall columns. Only the near OC `(X2+XStep, Z1-ZStep)` is correct.

---

### Debugging tools

**`tools/encode_slab.py`** — Generate a test slab from hardcoded placements. Includes `lbend_oc`, `single_corner`, `bend1_exact`, `bend2_exact` layouts. Output is a base64 string ready to paste into TaleSpire.

**`tools/decode_slab.py`** — Decode a base64 slab string into readable tile list, or diff two slabs to see what was added/removed between versions.

**In-code debug logging** — `SlabBuilder.DebugLog` is wired to BepInEx logger via `Plugin.Awake`. Active log tags: `[Phase2.5]`, `[Phase2.7]`, `[Phase3-wall]`, `[Phase3-inner]`, `[Phase4-OC]`, `[Phase4-OC-skip]`, `[TileDump]`. The TileDump section logs all tiles within 3 cells of hardcoded positions (13,17) and (23,35) — update these to match the area you're debugging.
