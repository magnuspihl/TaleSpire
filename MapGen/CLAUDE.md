## MapGen: Map Generation System

### Build
Build with `dotnet build -c Debug` while developing, so there is only ever one output folder to deploy from. Release packages are built `-c Release` by `tools/package-mod.py`. After any build, the user must **fully restart TaleSpire** before testing — stale DLLs cause the old code to stay loaded and produce wrong in-game results.

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

#### Phase 2.5 — claim the bend corners Phase 4 will fill
An L-bend has two corner cells: the inner corner (IC) at `(X2-XStep, Z1+ZStep)` and the "near" outer corner (OC) at `(X2+XStep, Z1-ZStep)`. Both get noted as straight walls by leg tiles — the OC by the bend's own vertical leg, the IC by any *other* corridor passing nearby, since Phase 2's `NoteWall` only suppresses the IC for the corridor that owns the bend. Phase 3 would then place a wall underneath the corner tile Phase 4 places.

Phase 2.5 decides up front exactly which cells Phase 4 will tile — recording them in `claimIC[i]` / `claimOC[i]` / `claimed4` — and strips **only those** from `wallRots`:

```csharp
bool hasBend = (c.X1 != c.X2 && c.Z1 != c.Z2);
if (!hasBend) continue;  // straight corridors have no bend corners
foreach (var (cell, isIc) in new[] { ((icx, icz), true), ((ocx, ocz), false) })
{
    if (globalFloors.Contains(cell) || roomPositions.Contains(cell)) continue;
    if (!claimed4.Add(cell)) continue;   // another bend already owns it
    if (isIc) claimIC[i] = cell; else claimOC[i] = cell;
    wallRots.Remove(cell);
}
```

**The claim and the emit must agree.** Stripping a cell Phase 4 then declines to fill leaves a hole in the corridor wall; letting two bends both emit into one cell produces duplicate corner tiles at different rotations — which the `Build()` dedupe does *not* catch, because its key includes rotation.

**Critical guard: use `hasBend`, not `Z1==Z2`.** Using `Z1==Z2` incorrectly skips purely vertical straight corridors (`X1==X2`), which would then have their OC position incorrectly stripped and lose wall tiles.

**Do NOT add a "far" OC** at `(X2+XStep, Z1+ZStep)`. This was tried and caused wrong/duplicate tiles inside straight wall columns. Revert immediately if re-added.

#### Phase 2.6 — seal around the claimed inner corners
Phase 4's IC lays a floor tile on a cell that is in no corridor's `Positions`, so Phase 2 never walked it and never noted walls around it. Normally harmless — the neighbouring leg tiles have already walled the same cells — but when a leg is **one tile long** the cell diagonally outside the bend is adjacent to no floor tile at all and is left as open void, so the corridor leaks into open space.

For each claimed IC, note a wall facing away in any of the 4 directions whose cell is not a floor, not a room cell, not claimed, **and not already in `wallRots`**. That last guard matters: adding a second note to an existing wall would turn a straight run into an inner corner.

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
Emits exactly the cells Phase 2.5 claimed: `if (claimIC[i].HasValue)` / `if (claimOC[i].HasValue)`. Never re-derive the positions here — the claim is the single source of truth.

**Inner corner** (the concave pocket of the bend):
- Sub-tile offsets: `icXOff = (XStep < 0) ? 0.5f : 0f`, `icZOff = (ZStep > 0) ? 0.5f : 0f`
- Emits a floor tile at the base row, then per `InnerCornerStyle`: `filler` → inner tile at the pocket quadrant, `y + row*wallHeight + 0.5f`; `separate_tile` → corner tile at the cell origin; `none` → nothing

**Outer corner** (the convex outside of the bend):
- `OuterCornerRot(XStep, ZStep)`: `(+,+)→E, (+,-)→S, (-,+)→N, (-,-)→W`
- Emits: corner tiles only (no floor tile underneath), stacked to `WallRows`

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

**9. Whole profile catalog failing to load, silently**
`tileset_profiles.json` has nulls in `wallHeight`, `floorHeight`, `stairType`, `minCeilingHeight` and `innerCornerStyle`. With non-nullable fields on `TilesetProfile`, Newtonsoft threw on the first null and **the entire file failed to deserialize** — `GetProfile()` then returned null for every theme, so `ApplyMultiFloor` and `BuildStaircase` both bailed and **stairs and multi-floor dungeons never generated at all**. Rooms and corridors still looked fine because `GetData` falls back to hardcoded defaults, which is why this went unnoticed for a long time. Fixed with `NullValueHandling.Ignore` plus defaults in `GetData`. One bad profile must never take down the catalog.

**10. Phase 4 inner corner stacking on a Phase 3 wall**
Phase 2 suppresses the IC note only for the corridor that *owns* the bend. A second corridor passing nearby still notes that cell, and Phase 3's wall then lands underneath the corner Phase 4 places there. Phase 2.5 must strip the IC position `(X2-XStep, Z1+ZStep)` too, not just the OC. Only visible on themes whose `innerCornerStyle` is `separate_tile` (Sewers) — with the default `filler` style the collision is there but invisible.

**11. Two corridors bending into the same cell**
Two corridors leaving the same room wall can elbow into one cell, each wanting its own Phase 4 corner. One corner fills the cell; the `claimed4` set lets the first bend claim it. The `Build()` dedupe does not save you here — its key includes rotation, and the two corners want different rotations.

**12. Exterior shell bricking up corridors**
`BuildExteriorShell` rings the room bounding box. A corridor can bulge outside that box and land on the ring, which both stacked tiles and walled the corridor off. The ring now skips any cell already occupied in the elevation band it covers.

**13. Ground storey poking through the upper floor**
`ApplyMultiFloor` raised only the *carrier* rooms to `upperWallRows`. A non-carrier BSP room with more wall rows — and every corridor inheriting its height — pushed its top wall row up through the upper storey. All ground rooms are now clamped to `upperWallRows`.

**14. Stair runs climbing out through the far wall**
`BuildStaircase` emits one cell per 0.5 of rise, so reaching a storey at y=8 is a 15-cell straight run. The storey height used to come from the tallest BSP room, which made runs longer than any room could hold; two such runs in facing rooms overlapped and produced duplicate stair tiles. `upperWallRows` is now the *shortest* stack clearing `MinCeilingHeight`, and rooms that still cannot hold the run are not chosen as carriers.

**15. One-tile corridor legs leaking into open space**
When an L-bend's horizontal leg is a single tile, the cell diagonally outside the bend borders no floor tile and gets no Phase 2 note — but Phase 4 still lays a floor on the inner corner next to it, so the dungeon interior opens straight into the void. Phase 2.6 seals it. This is invisible by eye in-game (you have to walk to that one cell) and was only ever found by the `EnclosureLeak` validator, which is why it survived so long.

---

### Automated QA — `tools/MapGenQA`

A net8.0 console app that runs the *shipped* generator headlessly. It links
`MapGen/Generation/*.cs` directly (`<Compile Include=...>`) rather than referencing a built
assembly, because MapGen targets net48 against the game's Unity DLLs. `UnityShim.cs` stubs the
only two Unity touchpoints — `GUIUtility.systemCopyBuffer` and `UnityEngine.Debug`. **MapGen
itself needs no changes to be testable**; keep it that way.

```
dotnet run -c Release -- fuzz   --seeds 1-1000 --sizes small,medium,large --themes all
dotnet run -c Release -- render --seed 42 --size medium        # ASCII floor plan per storey
dotnet run -c Release -- spec   --seed 42 --size medium        # rooms, connections, stairs
dotnet run -c Release -- cell   --seed 42 --size medium --x 33 --z 32 --radius 2
dotnet run -c Release -- slab   --seed 42 --size medium        # base64, ready to paste
```

`fuzz` exits non-zero on any failure and names the failing seed/size/theme, so a regression is
always reproducible with `render` or `cell`. Bugs 9-15 above were all found this way — several of
them, #15 especially, are effectively invisible in a visual spot-check. Run it before shipping any
generator change; 9000 maps takes about a minute.

Two traps that made validators quietly useless, worth remembering when adding more:
- **`map.At(y)` is an exact match.** Doors and inner-corner fillers live at `y + 0.5`, so a
  validator that walks "the tiles at y=0" silently misses both. `EnclosureLeak` takes the whole
  band `[y, y+1)` instead.
- **An inner corner is not a wall and not open floor.** It shares its cell with a floor tile and
  blocks only the two faces its rotation names (rot 0 = NW = blocks N and W). Treating it as
  passable invents leaks; treating it as solid hides them.

### Debugging tools

**`tools/rig.py`** — drives the Odin steam-headless rig: `status`, `deploy <Mod> [--restart]`,
`log [--follow] [--grep]`, `launch`, `shutdown`, `screenshot <out> [--crop] [--zoom]`,
`clipboard --file|--text`, `paste`. Use it to put a `MapGenQA slab` output on the container
clipboard and paste it onto a live board for the visual checks geometry assertions can't make.
`shutdown` goes through the game's own quit dialog, which is the path that saves board state —
never kill the process.

**`tools/encode_slab.py`** — Generate a test slab from hardcoded placements. Includes `lbend_oc`, `single_corner`, `bend1_exact`, `bend2_exact` layouts. Output is a base64 string ready to paste into TaleSpire.

**`tools/decode_slab.py`** — Decode a base64 slab string into readable tile list, or diff two slabs to see what was added/removed between versions.

**In-code debug logging** — `SlabBuilder.DebugLog` is wired to BepInEx logger via `Plugin.Awake`. Active log tags: `[Phase2.5]`, `[Phase2.6]`, `[Phase2.7]`, `[Phase3-wall]`, `[Phase3-inner]`, `[Phase4-OC]`, `[Phase4-OC-skip]`, `[TileDump]`. The TileDump section logs all tiles within 3 cells of hardcoded positions (13,17) and (23,35) — update these to match the area you're debugging.
