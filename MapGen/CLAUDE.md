## MapGen: Map Generation System

### Build
Build with `dotnet build -c Debug` while developing, so there is only ever one output folder to deploy from. Release packages are built `-c Release` by `tools/package-mod.py`. After any build, the user must **fully restart TaleSpire** before testing — stale DLLs cause the old code to stay loaded and produce wrong in-game results.

### Pipeline overview

```
LayoutSpec → SlabBuilder.Build() → List<(guid,x,y,z,rot)> → SlabEncoder.Encode() → base64 slab
```

`BuildRoom()` handles each room independently. `BuildCorridors()` handles all corridors in a shared multi-phase pipeline so crossing corridors interact correctly.

---

### Layout shape — why the dungeon is not a tree

`BuildConnections` walks the BSP and joins each subtree to its sibling exactly once, so on its own it produces a **spanning tree**: every room but one is a dead end, there is never a second route anywhere, and play is explore-a-branch-then-backtrack. Measured cyclomatic complexity was 0-1 across every seed.

`AddLoopConnections` (`DungeonTemplate.cs`) adds a few chords afterwards. It only joins rooms that are on the same storey, have at least 3 cells of facing wall overlap (a door needs to sit away from both corners), sit within `MaxLoopGap`, and have no third room in the corridor band — a corridor driven through an unrelated room breaches its wall without a door, which `EnclosureLeak` correctly reports. Candidates are ranked by current **graph** distance, so a chord closes a long circuit instead of cutting a corner; pairs already within 4 hops are skipped as they only make a triangle. Budget is `_loopBudget`, per size preset. Typical result is 2 loops on small, 4 on medium, 5 on large.

**Order matters: this must run after `ApplyMultiFloor`.** Multi-floor chooses which rooms go upstairs by looking for *pendant* rooms (exactly one connection), so adding chords first starves it of candidates and costs stairs.

Two knobs control how much bare ground ends up inside the exterior shell, which rings the whole bounding box of a storey:
- `Padding` — gap between a partition edge and its room. This is the dominant term, because it applies on **both** sides of every partition boundary: `Padding = 2` put a 4-cell gap between neighbouring rooms and held fill to ~40%. At 1 it is ~51%.
- `RoomFill` — floor of the room-size draw, as a fraction of what the partition could hold. Drawing uniformly from `_minRoom` leaves small rooms inside large partitions.

Changing either alters every generated map, so re-run the fuzzer rather than assuming.

### Upper floors must be one compact cluster

An upper storey is meant to read as a self-contained level standing on the part of the dungeon beneath it. What made upper floors look empty was not room count — it was *spread*. Carriers picked as "the N largest rooms", or even "the N nearest the largest", land all over the map, and the exterior shell then rings a bounding box that is mostly bare board. Measured upper-floor fill was 24-26%; the ground floor is ~50%.

Three rules keep the storey together, all in `ApplyMultiFloor` / `BuildExteriorShell`:

- **Carriers grow by adjacency.** Start from the largest eligible room and add each next room only if it comes within `MaxLandingGap` of one already chosen. Stop early rather than reaching for a distant room to fill `_maxHubCandidates`. Fewer, closer carriers beat more, scattered ones.
- **Landings obey the same gap.** A landing sits directly above the pendant room it isolates, so a pendant on the far side of the map plants an upper room away from every hub no matter how well the hubs cluster. Pendants outside the cluster are simply not isolated.
- **One shell per cluster, not per elevation.** `ClusterRooms` unions rooms within `ShellClusterGap` on both axes, so a storey whose rooms genuinely sit apart gets a ring around each group instead of one enormous wall. It also unions any two rooms joined by a `Connection`, **which is load-bearing**: split a connected pair across two rings and the corridor between them runs through open board, walled by neither. And `blocked` is recomputed inside the cluster loop so a later ring yields to an earlier one — where two rings touch, the second must not put a wall at a different rotation on a cell the first already filled (`StackedWalls`).

`MaxLandingGap` and `ShellClusterGap` are both 8 and are meant to agree: the layout aims for a cluster the shell will then recognise as one. Result is 41-52% upper fill at the cost of roughly one to two upper rooms per map.

### Mixing tilesets — one theme per room, not per map

`RoomSpec.Theme` is per-room and `SlabBuilder` honours it everywhere: rooms take their own theme,
a corridor takes its *from* room's, the staircase takes the **lower** room's, and the exterior ring
takes that of the tallest room on its storey. `LayoutSpec.Theme` is only the fallback for a room
that names none. So a map with two tilesets in it needs no new machinery — just rooms that disagree.

`TemplateParams.UpperTheme` is the first use of that: a dungeon basement under a castle keep.
`ApplyMultiFloor` stamps it on the hub and landing rooms it creates, and everything downstream
follows. Two things deliberately stay in the *lower* theme, because they belong to the lower storey
rather than the upper one:

- **The staircase.** It stands in the lower room and climbs out of it, so you take a dungeon stair
  up and arrive on castle floor. Reversing this puts a castle stair in a dungeon cellar.
- **The storey height.** `upperWallRows` and the stair run are measured with the lower profile's
  `WallHeight`/`FloorHeight`, because the rise to be climbed is the lower storey's. The upper
  rooms' own wall pitch still comes from their own tiles, which is what `WallRowOverlap` checks.

The exterior shell hid a bug here for as long as every map had one theme. Its "tallest room wins"
loop seeded `wallRows` at 1, so on a storey where every room has a single wall row the loop never
fired and `theme` kept its initial value — the *layout* theme. With one theme everywhere that was
indistinguishable from correct; with two it ringed the castle keep in dungeon wall. Seed the
accumulator at 0 so the first room always wins. **Any "pick the best room's X" loop on a storey
must be seeded so it cannot fall through to a layout-wide default.**

Fuzz mixed maps with `fuzz --mixed`, which gives every map a different theme upstairs than
downstairs and rotates the pairing by seed so each theme meets every other across a run.

Still open: **type 2 upper floors** — balconies overlooking a double-height room below. These cannot be built from the same machinery, because an open balcony edge is exactly what `EnclosureLeak` exists to flag. They need the *lower* room to declare itself multi-level and the upper level to yield a hole around it, plus railing tiles, which none of Dungeon Cellar, MegaDungeon, Marble Palace or Sewers actually contain.

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

**A tile's stored position is the minimum corner of its bounding box, on all three axes** — the
game writes `GetWorldSnappedBound().min`. On Y that is the familiar "tiles stack on what is below
them". On X and Z it means a tile smaller than its cell does *not* centre itself and does *not*
follow its rotation into the right corner: it settles against the low-x/low-z side every time, and
anything else has to be an explicit offset. Half the tilesets ship a 1 x 0.5 wall, so this is the
common case, not an exotic one — see bug 25. The per-tile `footprint` in `tileset_profiles.json` is
what makes it computable; `tile_catalog.json` has no bounds at all.

Everything under this heading follows from that one fact, and every constant below is derived
rather than guessed — `TileCatalog` is the only place that decides them.

| What | Elevation | Why |
|------|-----------|-----|
| Floor tile | `y` | the storey's base |
| Wall / corner, combo theme | `y` | the tile carries its own floor slab |
| Wall / corner, non-combo theme | `y + FloorThickness` | it stands *on* the floor tile |
| Door | `y + FloorThickness` | in its doorway, on the floor surface |
| Inner corner filler | `y + FloorThickness` | ditto |
| Bottom stair tread | `y + FloorThickness` | rests on the floor you walk on |
| Tread *n* | previous tread + `StairRise` | a tread rises by its own height |
| Ceiling / next storey's floor | `y + StoreyHeight(theme, wallRows)` | |

Walkable surface of a storey is `y + FloorThickness`, so the rise from one storey to the next is
`StoreyHeight = (combo ? 0 : FloorThickness) + wallRows * WallPitch`.

`FloorThickness` is 0.5 on every catalogued theme except Shogun Palace (0.2), and `StairRise` is 1.0
on all of them — but read them from `TileCatalog`, never hardcode. A storey height that is not a
whole number of treads (Dungeon Cellar climbs 2.5 on 1.0 treads) leaves a sub-tread lip at the top;
`StairStepCount` rounds **down** so the flight can never poke above the floor it feeds.

A flight crosses a theme boundary, so `StairStepCount` takes both: it climbs off the lower storey's
surface and has to arrive on the upper one's, and only when the two storeys share a tileset do their
floor thicknesses cancel. The parameter is not defaulted on purpose — see bug 24.

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
Door tiles must be placed on the floor's *surface*, not at its base — at `y` they sit flush and are effectively invisible. That is `y + TileCatalog.FloorThickness(theme)`; the long-standing hardcoded `y + 0.5f` was right for 19 of the 20 themes and wrong for Shogun Palace, whose floor is 0.2 thick.

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
`BuildStaircase` emits one cell per tread, so a tall storey is a long straight run. The storey height used to come from the tallest BSP room, which made runs longer than any room could hold; two such runs in facing rooms overlapped and produced duplicate stair tiles. `upperWallRows` is now the *shortest* stack clearing `MinCeilingHeight`, and rooms that cannot hold `StairStepCount + 4` cells on their long axis are not chosen as carriers.

**15. One-tile corridor legs leaking into open space**
When an L-bend's horizontal leg is a single tile, the cell diagonally outside the bend borders no floor tile and gets no Phase 2 note — but Phase 4 still lays a floor on the inner corner next to it, so the dungeon interior opens straight into the void. Phase 2.6 seals it. This is invisible by eye in-game (you have to walk to that one cell) and was only ever found by the `EnclosureLeak` validator, which is why it survived so long.

**16. A negative coordinate makes the whole slab paste as nothing**
Slab positions are packed into **unsigned 18-bit** fields, so a tile at x=-1 does not fail loudly — it wraps, and TaleSpire then rejects the entire slab with no message at all. A map that is otherwise perfect simply refuses to paste. Every other check passes, because the tiles themselves are well formed. This bit the contact sheet and the `rooms` command, both of which put a room at the origin and had the exterior shell extend one cell outside it. Lay anything out with a margin of at least one cell; `NegativePosition` catches the rest.

**17. Walls standing half a unit below the floor you walk on**
`PlaceWall` emitted the floor tile and the wall tile at the same `y`, burying the wall's bottom `FloorThickness`. Invisible on the 11 combo tilesets (they emit no floor under the wall at all) and wrong on all 9 others — the symptom people report is "the walls of <tileset> don't line up with the floor", which sounds like bad curation but is one code bug. The same mistake was in the corridor pipeline (Phases 2.7/3/4) and the exterior shell. `WallRowOverlap` never saw it because it only compared wallish tiles to *other wallish tiles*; `FloorWallOverlap` compares them to floors and is what closes the hole.

**18. Storey height double-counting the floor**
`upperY = wallRows * WallHeight + FloorHeight` counts the floor twice on a combo tileset, whose wall tile already contains one. On Dungeon Cellar the wall tops out at 2.5 and the storey above was placed at 3.0 — the visible half-unit gap between a basement and the keep on top of it, and the reason its staircase ended in mid-air. Use `TileCatalog.StoreyHeight`; no caller should compute a storey top by hand. Note that `ApplyMultiFloor`'s `MinCeilingHeight` loop deliberately still uses the curated figures — it is a headroom check against curated data, not a placement.

**19. A flight of stairs reading as several staircases side by side**
`BuildStaircase` stepped by a hardcoded `0.5` against tread tiles that are **1.0 tall** and rise by their own height, so a 2.5 storey got five treads climbing 2.5 cells of ground, each floating half-buried in the next. It also started the bottom tread at `OriginY`, sinking it into the floor. And the tread rotation is a quarter turn (**+6 steps**) off the wall-facing convention `DirToRot` encodes: at `DirToRot("west")` the treads incline north while the run climbs west. Confirmed in game on Dungeon Cellar.

**20. The upper floor coming out a fraction of the lower one**
Two separate causes, and they are easy to confuse. (a) `ApplyMultiFloor` filtered carriers to rooms at or above *average area* before growing the cluster by adjacency, so on a map whose big rooms all sit more than `MaxLandingGap` apart, growth stopped at the seed. Area now only picks the seed. (b) The seed was the largest room on the map, which is often the one in a corner with nothing within reach — `BestClusterSeed` picks the largest room of the best-connected *group* instead. Together these took seed 0 from 17% of the ground floor to 35%, and the worst sampled seed from 11% to 39%. **Report the ratio, don't eyeball it**: `render` prints it per storey, because an earlier attempt at this raised fill *within the storey's shell* while leaving the total footprint untouched, and looked like progress.

**21. The encoder truncating a centimetre off every position**
`PackAsset` stored `(int)(worldPos * 100f)`. While every elevation was a multiple of 0.5 that was
exact, but Shogun Palace's floor tile is 0.19 thick, so its storeys land on 2.19 and 4.19 — which
as a `float` are a hair *under*, and truncation threw away the whole centimetre. The tile is then
placed 1cm into the one below it, in the game only, with nothing in the generator's own view of the
map to show for it. Rounds now. `EncoderRoundTrip` catches this class, but only because it
normalises with `Math.Round` and so disagrees with a truncating encoder — keep them different.

**22. A corner half a unit taller than the wall it turns**
Five tilesets — Facility, Marble Palace, Industrial, Brick Building, Concrete Building — rank a
2.5-tall corner first for a 2.0 wall (or the reverse). Rows are pitched at `WallPitch`, which is
the *wall's* height, so every corner above the first was buried in the one below it. All five carry
a matching-height candidate further down their list, so `PickOfHeight` resolves it rather than the
profile needing to be re-curated. This only surfaced once bug 20's fix made many more rooms two
rows tall, which is a good argument for fuzzing across every theme rather than the default one.

**23. A floor tile under a wall that carries its own**
Where one corridor's walkway crosses another's wall line, the first lays a floor on the cell and the
second stands a combo wall in it. Neither stage can see the other, so `Build()` drops any floor
sharing a position with a wall or corner after the fact — a wall at exactly floor level can only be
a floor-carrying one, since bug 17's fix put every other wall a `FloorThickness` higher. The
inner-corner `separate_tile` path had the same fault directly and no longer lays the floor itself.

**24. A flight sized against the wrong storey's floor**
`StairStepCount` measured `StoreyHeight` of the *lower* theme, which is the rise to the storey line
— but the landing is the *upper* storey's walking surface, one `FloorThickness` above that line, and
that thickness belongs to the upper theme. The two cancel only when both storeys share a tileset.
A Marble Palace basement under a Shogun Palace keep loses the 0.31 between a 0.5 floor and a 0.19
one, and a two-tread flight topped out at 2.5 against a landing at 2.19. It now takes both themes
and measures surface to surface. Structurally invisible to a uniform sweep: **fuzz `--mixed`, or
every cross-theme elevation bug hides behind a subtraction that happens to cancel.**

**25. Walls parked half a cell in from the edge they line**
A slab position is **the minimum corner of the tile's world bounding box**, not the cell it belongs
to — `TileBuilderBoardTool.AssetHolder.Spawn` stores `GetWorldSnappedBound().min`, and
`GetRotationBoundsOffset` is what puts it back. So a tile whose footprint is shallower than its cell
settles against the cell's low-x/low-z side however it is turned. Facing north or west that *is* the
outer edge and looks right; facing south or east it is the inner one, and the floor tile juts out
past the wall into open air. Six of the twenty usable tilesets ship a 1 x 0.5 wall and every one of
them was wrong on two sides of every room. A combo wall carries its own floor and so fills the cell,
which is why three themes' worth of in-game checks never showed it.

`Mk` now pushes walls and corners back against the edge their rotation implies, using a `footprint`
field folded into `tileset_profiles.json` from the asset packs' own `ColliderBoundsBound`. It is a
no-op for a full-cell tile, so nothing that already looked right moved. Doors and inner-corner
fillers are excluded on purpose: those offsets mean *which half* and *which pocket*, not *which
edge*, and would double up.

Moving a wall off its cell origin broke something that had quietly depended on it: the final pass in
`Build` that drops a loose floor tile out from under a wall carrying its own floor matched on the
*exact* position, so once the wall shifted to x+0.5 it stopped firing and 460 buried walls came back
across the sweep. It matches on the grid cell now. The lesson is narrow but sharp — **a change to
where a tile is written invalidates every comparison keyed on that position**, and the only reason
this was caught is that the sweep runs 18,000 maps rather than the handful the change was checked on.

The reason this took a decompile rather than a guess: `tile_catalog.json` carries no bounds, so
there was no way to tell a 1 x 0.5 wall from a 1 x 1 one, and the observed symptom (two sides wrong,
two right) is not something any amount of reasoning about rotation conventions produces. **When tile
geometry is in question, `Taleweaver/*/index.json` has the real bounds and the game's own assemblies
in `TaleSpire_Data/Managed` have the real placement rule.**

### Choosing tiles — the catalog is data, not code

`TileCatalog` used to carry a hardcoded table of GUIDs for three themes. It now resolves every
role out of `MapGen/tileset_profiles.json`, and the C# rule is deliberately trivial: **take the
first 1x1 entry in the role's candidate list**. All the judgement lives in the data, so adding a
theme is a data edit and never a code change. This is what took the generator from 3 themes to 20.

The file is generated — do not hand-edit it. `tools/build-mapgen-profiles.py` copies the curated
profiles from `/home/coder/TaleSpire-Binaries/`, folds in each tile's `name`, `size` and `height`
from `tile_catalog.json` (which is far too large to ship), sorts each role's candidates best-first
so "take the first" lands correctly, and then applies `tools/tileset_picks.json` — the repo-local
overlay of picks that have actually been looked at in game. `--check` fails if the committed copy
is stale; the copy *was* stale once and silently held a wrong wall for CastleRuins.

Two things make this irreducibly a curation problem rather than a derivation:

- **`hasIntegratedFloorWall` is one boolean per tileset, but carrying-a-floor is a per-tile
  property.** Half the sets contain both kinds of wall. The build script infers the combo as
  `bare + floorHeight` and prefers it, which scores 7 of 9 against known-good picks — but where a
  set lists its walls at only one height, "bare" is unobservable and the inference says nothing.
  Both themes verified in game are exactly that shape.
- **Some correct tiles are not in `tilesByRole` at all.** MegaDungeon's floor-bearing wall and
  corner are absent from the curated lists, so no ordering rule could ever have found them.
  `apply_picks` inserts a pick that is missing rather than only reordering.

So the loop for curating a theme is:

1. `tools/theme-sweep.sh /tmp/themes` generates one map per buildable theme and previews each on
   the rig, so a whole pass is one montage to look at rather than 20 sessions.
2. For a theme that comes out wrong, `python3 tools/pick-probe.py "<Tileset>" wall` builds a **real
   room per candidate tile** and lines them up in one screenshot. A lone tile does not answer the
   question — an isolated panel and an isolated pillar look alike from the board camera; what
   separates them is whether a row of them closes into a wall. Candidates come from the whole
   catalog folder, not the curated role list, because the recurring failure is a correct tile that
   was never curated at all. `tools/tileset-contact.py` is the flatter view: every 1x1 tile in the
   set on bare board, useful for spotting which tiles carry their own floor.
3. Write the GUIDs into `tileset_picks.json` with `verified: true` and a note, regenerate, re-fuzz.

**Swap wall, corner and `cornerFiller` together, never one at a time.** `pick-probe.py` substitutes
a single role, which is the right tool for "which of these five walls is the wall" but the wrong one
for judging a *tileset*: a 2.0 wall standing next to a 2.5 corner leaves a hole at every turn and
reads exactly like a set that cannot enclose. Two tilesets were excluded on that mistake and later
recovered — Desert Village was graded on its 2.5 wall/floor pieces while its plain 2.0 wall closes
into a continuous buttressed face, and Chamber was graded with a `Chamber hold` wall against a
caged-tunnel corner. Probe matched sets.

Probing substitutes candidates through a **sidecar `tileset_profiles.json` next to the MapGenQA
binary** — the same override a user can drop next to `MapGen.dll`. Nothing in the repo is touched,
so a probe cannot leave a half-applied pick behind. It is deleted afterwards in a `finally`: a
sidecar left in place shadows the embedded profiles for every later harness run, `fuzz` included.

`excluded: true` is the verdict for a tileset that passes every structural check on paper — three
1x1 structural roles, all tiles 1x1 — and still cannot build a room, because **no 1x1 tile in it
forms a continuous wall face**. Harbor is a pier kit with no wall tile at all; Rural and Tavern are
post-and-beam village sets whose every candidate is a post, so a ring of them never closes. They are
dropped by `ProfileCatalog.BuildableThemes()` and never offered. The reason is recorded per theme
in `tileset_picks.json` so the verdict can be re-argued without repeating the rig session. Current
standing: **20 verified, 3 excluded.** CastleRuins is verified deliberately — its walls are broken
stubs, but that is the set's intent, and the map structure underneath is coherent.

The overlay can also override `hasIntegratedFloorWall`, because the reference flag is per-tileset
while carrying a floor is per-tile: Desert Village's set is flagged true, but the wall actually
picked is the bare 2.0 one, and left uncorrected the wall ring would stand a floor's thickness below
the room it encloses. `build-mapgen-profiles.py` warns whenever a pick's height disagrees with the
flag, which is the signal that an override is needed.

**Doors are nearly all borrowed.** Only Dungeon Cellar, MegaDungeon and Sewers own a 1x1 door;
everything else takes one from the `Doors` or `Doors (Modern)` folder. `TileCatalog.Fallback`
means a theme with no door pick silently gets the *dungeon* door, which is wrong in a spaceship
and looks like a generator bug rather than a missing pick — so every buildable theme now names one
explicitly. The reference data's `doorCompatibility` list is the starting point, but it often names
only 2x1 pieces (Marble Palace, Castle Fortified, Sewers) and MapGen can only place 1x1, so the
usable door is frequently one the list never mentions. Shogun Palace likewise contains no stair
tile at all and borrows the ship's wooden one.

**Never grade a theme off a screenshot taken too early.** A thousand-tile map streams into a
preview over several seconds, and a half-loaded one reads as a scatter of floating panels —
indistinguishable from a genuinely broken tileset. Nine themes were written off that way against a
3-second wait; `tools/rig-preview.sh` now waits 12. Three of the nine were fine.

**Heights come from the tile, not the profile.** `TileCatalog.WallPitch` returns the placed wall
tile's own height and only falls back to the profile's curated `wallHeight`. The two disagree in
several sets — BellowGloom lists 2.2 against a 2.5-tall wall — and stacking on the curated figure
sinks each row 0.3 into the one below it. `FloorThickness` and `StairRise` follow the same
precedent, and so does `StoreyHeight`, which is derived from them rather than curated. The one
deliberate exception is `ApplyMultiFloor`'s `MinCeilingHeight` loop: that criterion was curated
against the curated `wallHeight`/`floorHeight`, so mixing in a real tile height would compare two
different measurements. `WallRowOverlap` guards row stacking, `FloorWallOverlap` guards the wall's
base against the floor beneath it.

### Automated QA — `tools/MapGenQA`

A net8.0 console app that runs the *shipped* generator headlessly. It links
`MapGen/Generation/*.cs` directly (`<Compile Include=...>`) rather than referencing a built
assembly, because MapGen targets net48 against the game's Unity DLLs. `UnityShim.cs` stubs the
only two Unity touchpoints — `GUIUtility.systemCopyBuffer` and `UnityEngine.Debug`. **MapGen
itself needs no changes to be testable**; keep it that way.

```
dotnet run -c Release -- fuzz   --seeds 1-1000 --sizes small,medium,large --themes all
dotnet run -c Release -- fuzz   --seeds 1-300 --themes all --mixed   # different theme upstairs
dotnet run -c Release -- render --seed 42 --size medium        # ASCII floor plan per storey
dotnet run -c Release -- render --seed 7 --theme "Dungeon Cellar" --upper "Castle Fortified"
dotnet run -c Release -- spec   --seed 42 --size medium        # rooms, connections, stairs
dotnet run -c Release -- cell   --seed 42 --size medium --x 33 --z 32 --radius 2
dotnet run -c Release -- slab   --seed 42 --size medium        # base64, ready to paste
dotnet run -c Release -- rooms  --themes "A,B,C" --rows 1      # one plain room per theme, in a row
dotnet run -c Release -- tiles  --themes Facility              # resolved tile per role, + heights
```

Reach for `tiles` first whenever a fault looks like it is about elevation. Bug 22 presented as a
wall row sunk into the one below it and read like a stacking bug; one line of `tiles` showed the
corner was simply half a unit taller than the wall. It prints the derived figures —
`wallPitch`, `floorThick`, `storey`, `stairRise`, `steps` — beside the tiles they come from, which
is the whole chain any elevation bug lives in.

`rooms` exists for tile curation, not for testing layout: a whole generated map is a bad place to
judge a tile, because the candidate is buried among corridors and two themes cannot be compared
without regenerating between pastes. `RoomSpec.Theme` is per-room, so one spec can hold rooms from
many themes at once. Drive it through `tools/pick-probe.py`.

`fuzz` exits non-zero on any failure and names the failing seed/size/theme, so a regression is
always reproducible with `render` or `cell`. Bugs 9-15 above were all found this way — several of
them, #15 especially, are effectively invisible in a visual spot-check. Run it before shipping any
generator change; 9000 maps takes about a minute.

A long sweep runs `--no-build`, so **anything you change while one is in flight is not in the run**,
and the findings it reports are about a build that no longer exists. Three of them were chased back
to code that had already been fixed. Kill the sweep and restart it after any edit.

But a green sweep only means the validators you have are satisfied. Bugs 17-20 were all live while
every one of the then-18 validators passed, and were found by a human looking at one map in game.
Each of them therefore shipped with a validator that fails on the pre-fix build — `FloorWallOverlap`,
`StoreyAlignment`, `StairRiseContinuous`, `StairReachesLanding`, `StairWellOpen`, taking the suite to
23. **Demonstrate a new validator failing before you trust it**: a checker written from the same
formula as the fix it guards will pass either way. `Validators.StairRuns` exists for that reason —
it walks the run outward from its origin following the tiles actually emitted, instead of
recomputing where they ought to be.

Bugs 21-23 then fell out of that same sweep: none of them were reported by anyone, and all three
were latent long before the work that exposed them. **Fuzz `--themes all`, not the default theme.**
Each was confined to tilesets nobody had been generating with — a 0.19-thick floor, a mismatched
corner, a combo wall — and a sweep of Dungeon Cellar alone is green through every one of them.

Bug 24 then survived even *that*, and only `--mixed` reached it: it is a difference between two
themes' floors, so on any map whose storeys share a tileset the error is exactly zero and all 20
themes pass. **Run both sweeps.** A uniform one cannot fail on a quantity that only exists when the
storeys differ, and per-floor theming means that quantity is now everywhere.

Because `ProfileCatalog` resolves `tileset_profiles.json` out of *its own assembly*, the harness
must embed the same file under the same logical name (`TaleSpireMapGen.tileset_profiles.json`) —
see `MapGenQA.csproj`. Without it the catalog is empty, the generator falls back to hardcoded
defaults and skips multi-floor, and every stair validator passes because there are no stairs. A
`Preflight()` in `fuzz` asserts a profile resolves for each theme and refuses to report a green
run otherwise; keep that check honest if profile loading changes again.

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

**`tools/rig-preview.sh`** — paste a slab, screenshot it, cancel. Every rig inspection goes through
this, and it only ever issues **button 3**: a left-click *commits* the paste onto the campaign
board, and TaleSpire has no bulk delete, so one stray click costs a session. It also re-enters
build mode first, because a cancelled paste sometimes drops the game out of it and a paste outside
build mode is silently discarded. `tools/theme-sweep.sh` and `tools/pick-probe.py` both sit on top
of it.

Its `zoom` and `orbit` arguments are **absolute, not deltas** — the camera keeps whatever position
the last session left it in, so two identical invocations otherwise frame the board completely
differently, and a shot taken from inside a wall is indistinguishable from a broken tileset. Zoom
and pitch are coupled in TaleSpire: scrolling in drops the camera towards ground level, which is
the only way to look at a wall face rather than the tops of walls.

**`tools/encode_slab.py`** — Generate a test slab from hardcoded placements. Includes `lbend_oc`, `single_corner`, `bend1_exact`, `bend2_exact` layouts. Output is a base64 string ready to paste into TaleSpire.

**`tools/decode_slab.py`** — Decode a base64 slab string into readable tile list, or diff two slabs to see what was added/removed between versions.

**In-code debug logging** — `SlabBuilder.DebugLog` is wired to BepInEx logger via `Plugin.Awake`. Active log tags: `[Phase2.5]`, `[Phase2.6]`, `[Phase2.7]`, `[Phase3-wall]`, `[Phase3-inner]`, `[Phase4-OC]`, `[Phase4-OC-skip]`. There used to be a `[TileDump]` block here that logged every tile near two hardcoded cells and labelled roles by Dungeon Cellar GUID prefixes; it was scaffolding for a finished investigation, ran for every user on every generate, and mislabelled every other theme. Prefer `mapgenqa render`/`slab` for this now — the harness sees the same tiles without shipping a probe in the plugin.
