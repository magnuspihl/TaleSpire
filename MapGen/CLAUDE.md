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

Two knobs control how densely a storey's bounding box is filled with rooms:
- `Padding` — gap between a partition edge and its room. This is the dominant term, because it applies on **both** sides of every partition boundary: `Padding = 2` put a 4-cell gap between neighbouring rooms and held fill to ~40%. At 1 it is ~51%.
- `RoomFill` — floor of the room-size draw, as a fraction of what the partition could hold. Drawing uniformly from `_minRoom` leaves small rooms inside large partitions.

Changing either alters every generated map, so re-run the fuzzer rather than assuming.

### Upper floors must be one compact cluster

An upper storey is meant to read as a self-contained level standing on the part of the dungeon beneath it. What made upper floors look empty was not room count — it was *spread*. Carriers picked as "the N largest rooms", or even "the N nearest the largest", land all over the map, so the storey read as scattered outbuildings on a roof. Measured upper-floor fill was 24-26%; the ground floor is ~50%.

Two rules in `ApplyMultiFloor` keep the storey together:

- **Carriers grow by adjacency.** Start from the largest eligible room and add each next room only if it comes within `MaxLandingGap` of one already chosen. Stop early rather than reaching for a distant room to fill `_maxHubCandidates`. Fewer, closer carriers beat more, scattered ones.
- **Landings obey the same gap.** A landing sits directly above the pendant room it isolates, so a pendant on the far side of the map plants an upper room away from every hub no matter how well the hubs cluster. Pendants outside the cluster are simply not isolated.

Result is 41-52% upper fill at the cost of roughly one to two upper rooms per map.

### There is no exterior shell, and there was never a reason for one

`BuildExteriorShell` used to ring the whole bounding box of each storey in wall. It was added so a map would look like a building from outside, and the shape it actually produced was a house inside a house: rooms have their own complete wall rings, so the shell was a second, outer wall with bare board between the two.

It carried **no structural load at all** — turning it off and fuzzing 2,400 maps passed all 32 validators — while costing 11-18% of every slab's tiles. It was also the direct cause of bug 12, the wall-theme bug at the end of the next section, and a coupling between `MaxLandingGap` and its own `ShellClusterGap`; and it framed "upper floors look sparse" as a fill-ratio problem for a year, because what looked sparse was the bare board its ring enclosed.

**Do not bring it back as a ring.** The real complaint underneath it — a skinny corridor looks odd seen from outside — is about *corridor* walls, not the building's outline, and a trace of the outermost wall faces would be the fix if it ever matters again. Above-ground silhouettes are the `BuildRoofs` TODO's problem, not a wall ring's.

### The way in

`EntrancePlacer` writes `LayoutSpec.Entrance` and nothing else. It picks the ground-floor room whose wall sits nearest the edge of the map's own bounding box (ties to the larger room, then the lowest id), then searches that wall outward from the middle for an offset that is not already spoken for by a corridor door and whose outward ray clears every ground-floor footprint. `SlabBuilder` then cuts it exactly as it cuts a corridor doorway, by adding it to `doorGaps` — there is no separate code path, and every theme already has a curated `door` pick.

It runs **after** `AddLoopConnections` and **before** `PurposeAssigner`, because depth is measured in graph steps from the way in and the assigner used to have to guess at it. `PurposeAssigner` reads `spec.Entrance.RoomId` and keeps its old "nearest the bounding box edge" rule only as a fallback for a spec that carries no entrance.

Two validators split the question in half, and the split is the point. `EnclosureLeak` **seals the entrance cell** and asks whether everything else still holds — so a second hole somewhere is still a finding rather than being excused by the presence of a legitimate one. `EntranceLeadsOutside` asks about the door itself: that there is one, that a `Door` tile was actually placed at that cell, and that walking out of it reaches open board rather than the back of the next room. All three of its failure modes were demonstrated on deliberately broken builds, and removing `EnclosureLeak`'s seal makes that validator fail on every map — which is the evidence that the door is a real hole in a real wall and not a cosmetic swap.

`render` marks it `@`.

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

The now-deleted exterior shell hid a bug here for as long as every map had one theme, and the lesson
outlives the code. Its "tallest room wins" loop seeded `wallRows` at 1, so on a storey where every
room has a single wall row the loop never fired and `theme` kept its initial value — the *layout*
theme. With one theme everywhere that was indistinguishable from correct; with two it ringed the
castle keep in dungeon wall. **Any "pick the best room's X" loop on a storey must be seeded so it
cannot fall through to a layout-wide default.**

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

**12. Exterior shell bricking up corridors** *(moot — the shell is gone)*
`BuildExteriorShell` ringed the room bounding box, and a corridor could bulge outside that box and land on the ring, which both stacked tiles and walled the corridor off. Kept as a record because the class is live: **any stage that lays tiles on cells another stage also owns has to yield per cell**, not per stage.

**13. Ground storey poking through the upper floor**
`ApplyMultiFloor` raised only the *carrier* rooms to `upperWallRows`. A non-carrier BSP room with more wall rows — and every corridor inheriting its height — pushed its top wall row up through the upper storey. All ground rooms are now clamped to `upperWallRows`.

**14. Stair runs climbing out through the far wall**
`BuildStaircase` emits one cell per tread, so a tall storey is a long straight run. The storey height used to come from the tallest BSP room, which made runs longer than any room could hold; two such runs in facing rooms overlapped and produced duplicate stair tiles. `upperWallRows` is now the *shortest* stack clearing `MinCeilingHeight`, and rooms that cannot hold `StairStepCount + 4` cells on their long axis are not chosen as carriers.

**15. One-tile corridor legs leaking into open space**
When an L-bend's horizontal leg is a single tile, the cell diagonally outside the bend borders no floor tile and gets no Phase 2 note — but Phase 4 still lays a floor on the inner corner next to it, so the dungeon interior opens straight into the void. Phase 2.6 seals it. This is invisible by eye in-game (you have to walk to that one cell) and was only ever found by the `EnclosureLeak` validator, which is why it survived so long.

**16. A negative coordinate makes the whole slab paste as nothing**
Slab positions are packed into **unsigned 18-bit** fields, so a tile at x=-1 does not fail loudly — it wraps, and TaleSpire then rejects the entire slab with no message at all. A map that is otherwise perfect simply refuses to paste. Every other check passes, because the tiles themselves are well formed. This bit the contact sheet and the `rooms` command, both of which put a room at the origin and had the (since deleted) exterior shell extend one cell outside it. Lay anything out with a margin of at least one cell; `NegativePosition` catches the rest.

**17. Walls standing half a unit below the floor you walk on**
`PlaceWall` emitted the floor tile and the wall tile at the same `y`, burying the wall's bottom `FloorThickness`. Invisible on the 11 combo tilesets (they emit no floor under the wall at all) and wrong on all 9 others — the symptom people report is "the walls of <tileset> don't line up with the floor", which sounds like bad curation but is one code bug. The same mistake was in the corridor pipeline (Phases 2.7/3/4) and in the exterior shell. `WallRowOverlap` never saw it because it only compared wallish tiles to *other wallish tiles*; `FloorWallOverlap` compares them to floors and is what closes the hole.

**18. Storey height double-counting the floor**
`upperY = wallRows * WallHeight + FloorHeight` counts the floor twice on a combo tileset, whose wall tile already contains one. On Dungeon Cellar the wall tops out at 2.5 and the storey above was placed at 3.0 — the visible half-unit gap between a basement and the keep on top of it, and the reason its staircase ended in mid-air. Use `TileCatalog.StoreyHeight`; no caller should compute a storey top by hand. Note that `ApplyMultiFloor`'s `MinCeilingHeight` loop deliberately still uses the curated figures — it is a headroom check against curated data, not a placement.

**19. A flight of stairs reading as several staircases side by side**
`BuildStaircase` stepped by a hardcoded `0.5` against tread tiles that are **1.0 tall** and rise by their own height, so a 2.5 storey got five treads climbing 2.5 cells of ground, each floating half-buried in the next. It also started the bottom tread at `OriginY`, sinking it into the floor. And the tread rotation is a quarter turn (**+6 steps**) off the wall-facing convention `DirToRot` encodes: at `DirToRot("west")` the treads incline north while the run climbs west. Confirmed in game on Dungeon Cellar.

**20. The upper floor coming out a fraction of the lower one**
Two separate causes, and they are easy to confuse. (a) `ApplyMultiFloor` filtered carriers to rooms at or above *average area* before growing the cluster by adjacency, so on a map whose big rooms all sit more than `MaxLandingGap` apart, growth stopped at the seed. Area now only picks the seed. (b) The seed was the largest room on the map, which is often the one in a corner with nothing within reach — `BestClusterSeed` picks the largest room of the best-connected *group* instead. Together these took seed 0 from 17% of the ground floor to 35%, and the worst sampled seed from 11% to 39%. **Report the ratio, don't eyeball it**: `render` prints it per storey, because an earlier attempt at this raised fill *within the storey's then-shell* while leaving the total footprint untouched, and looked like progress.

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

**26. A wall from the wrong family in the same tileset**
Castle Fortified came back from the rig as corners that did not match their walls. Both candidate
walls are 1x1, both 2.0 tall, both 1 x 0.5 — identical on every field the profile carries. The
discriminator is the pack's own `GroupTag`, which in this set does not name a role but a *family*:
`wall` and `wall base` each hold a wall and its matching corner, in two different architectural
styles. The corner pick was from `wall base` and the wall pick from `wall`.

Because every structural pick is pinned in `tileset_picks.json`, correcting it is a data edit and a
sort tiebreak would have been dead code. What is worth keeping is the check:
`build-mapgen-profiles.py` now warns whenever a wall from the corner's own family was available and
something else was picked. It fires only where a pack groups by family rather than by role — most
put all walls in `wall` and all corners in `corner`, which says nothing — so it is quiet except
where it means something. It flagged Shogun Palace next, which turned out to be the same fault for
a different reason — see bug 29 — and is now silent on every buildable set.

**27. Stair treads with nothing under them**
A tread carries only its own step, so every tread above the first hangs a full rise over open air
and a flight reads as a row of separate floating steps. The column under each tread is now filled
solid down to the floor surface. Seven tilesets ship a block for exactly this (Dungeon Cellar's is
`Dungeon Stair Block`); Ship and Interstellar have an unnamed full-cell solid; every other theme
fills with its own floor tile, which is solid, always present and the right material.

The fallback is deliberately *not* `TileCatalog.Get`'s usual one. That falls back to the **default
theme**, which would put dungeon stone under a marble staircase — so `BuildStaircase` asks
`HasTile` first and drops to the theme's own floor rather than to another theme's block.

Fixing this exposed a curation fault behind it: four sets — BellowGloom, MegaDungeon, Desert Village
and Industrial — had picked their stair *block* as their `stairs` tile, so their flights were stacks
of cubes rather than treads, and `stairs` and `stairBlock` resolved to one GUID. That is invisible
to the generator but fatal to the harness, which indexes placements by GUID to recover the role they
were placed as: every tread reported as a block and every flight as missing entirely. All four sets
do ship a sloped tread and now use it. `build-mapgen-profiles.py` warns when one theme picks a
single tile for two roles, because **a GUID is the only handle anything downstream has on what a
tile was placed as.**

**28. A wall authored looking west, turned a quarter circle wrong on all four sides**
Bug 25 established that a tile shallower than its cell hugs the low-x/low-z side, and `EdgeHug`
pushes it back against the edge its rotation implies. That is only right if the tile's art faces
the way `rotStep` says it does. Three sets — Desert Village, Shogun Palace, Hull — ship a wall that
is **0.5 x 1** rather than the usual 1 x 0.5, which is the same panel authored looking *west*: at
`rotStep 0` it stands square to the wall it is meant to be part of, so a ring of them never closes
and `EdgeHug` pushes it along the wrong axis on top of that.

`TileEntry.AuthoredRotBias` is the correction, and it is **+18, not +6**. A quarter turn runs
N→W→S→E (`ROT_NORTH 0, ROT_WEST 6, ROT_SOUTH 12, ROT_EAST 18`), so bringing west round to north
takes three of them. `TileCatalog`'s own class comment claimed `6=East, 18=West` and was simply
stale and backwards; it produced a confident wrong answer first time round. **When two places in
the repo state a convention, the constants are the truth and the prose is a claim.**

`Mk` adds the bias to the caller's rotation *after* computing `EdgeHug`, because the hug is about
which edge the caller wants lined and the bias is about the tile's own art. `WallSeatedOnEdge`
takes the bias back off before asking which edge a placed piece lines, and asserts the piece is
thin *across* that edge and full *along* it — without that second assertion the validator checks
position given correct rotation and a missing bias passes silently. Forcing the bias to 0 makes it
fail with 12 findings, which is the evidence it is doing work.

Confirmed in game on Desert Village and Shogun Palace: the finished, detailed face ends up outward
and the rough back inward. Hull is uncurated and unlooked-at.

**29. A tileset's picks drawn from two incompatible families**
Bug 26 was one set; the same fault turned out to be systemic, and the shape of the data makes it
checkable. **Every healthy tileset's wall/corner/cornerFiller picks fall into exactly one of two
coherent families:**

| | `hasIntegratedFloorWall` | wall | corner | cornerFiller |
|---|---|---|---|---|
| **bare**  | false | h2.0, 1 x 0.5 | h2.0, 1 x 1 | h2.0, 0.5 x 0.5 |
| **combo** | true  | h2.5, 1 x 1   | h2.5, 1 x 1 | — |

Six sets are healthy-bare, eight healthy-combo, and Chamber is a deliberate exception (bare, but
its caged-tunnel wall genuinely fills the cell). Five sets were mixed across the two, and each
mixture produced a different visible symptom, which is why they were never recognised as one class:

- **Concrete Building, Industrial** picked a 2.5 combo wall against 2.0 corners — a notch in the
  top of the ring at every corner, and for Industrial an inner-corner filler from the other family
  as well. Both are now bare throughout. Industrial has *two* tiles named `industrial_wall_1x1_02`
  and two named `industrial_corner_filler_01` at different heights, so this is a GUID edit and the
  names are actively misleading.
- **Facility, Marble Palace** were flagged `hasIntegratedFloorWall` over a 1 x 0.5 bare wall, so
  the builder laid no floor and seated the ring at y=0 — bug 17's symptom arriving through
  curation rather than code. Flag cleared on both.
- **Shogun Palace** picked the plaster `shogunPlainWall_1x1`, whose own corner `shogunWallCorner1x1`
  is **2x1** and therefore unplaceable, so it fell back to the only 1x1 corner in the set — a bare
  timber block from the dig-wall family. On the rig the plaster wall's white cap band ran straight
  into it at every corner. The whole ring is the dig family now. The plaster wall looks better in
  isolation and that is exactly the trap: **a wall cannot be judged without the corner it will be
  paired with.**

Three checks in `build-mapgen-profiles.py` make the class non-recurring, and all three need the
per-tile `footprint`, which is why none of them existed before bug 25. `check_corner_pitch` warns
where *no* corner in the set matches the picked wall's height — phrased against the whole candidate
list rather than the first entry, because `PickOfHeight` searches the list and a corner out of order
is harmless. `check_wall_footprint` warns on a combo flag over a shallow wall and notes a
west-authored one. `check_wall_family` (bug 26) warns when a wall from the corner's own `GroupTag`
family was available and something else was picked.

All five are fixed and the build script is now silent but for three informational NOTEs.

---

### Props are not tiles, and break three conventions at once

Furniture rides the same slab as tiles — the format records no placeable kind, and TaleSpire
recovers it from the GUID against the installed packs — so `SlabEncoder` needed no changes at all.
But everything about *where* a prop goes differs from a tile, and mixing the two up is silent:

- **Position is the pivot, not a bounds corner.** `AssetMath` dispatches on `PlaceableKind` and
  builds prop bounds around the origin as `centre = origin + rotate(pivotOffset)`, where a tile's
  position is `GetWorldSnappedBound().min`. So none of the tile placement machinery transfers, and
  `SlabBuilder.EdgeHug` in particular would be actively wrong. `PropEntry.OriginForCentre` solves
  that relation backwards and is the only place the inversion is written; `MapGenQA`'s
  `PropAt.Centre` derives it *forwards* on purpose, so a sign error shows up as a disagreement
  between the two instead of cancelling out.
- **Snap is the centimetre, not the half cell** (`SnapPosToNearestHundredth`), and any of the 24
  rotation steps is legal rather than multiples of 6.
- **Rotation is offset half a turn from a tile's.** Tile art is authored facing −Z, which is why
  "wall rotation = the direction the wall faces" holds. Prop art is authored facing **+Z**, so a
  prop at rot 0 faces *south*. `PropPlacer.PropFacingOffset` is that difference, and without it
  every prop is seated with its back to the room: bookshelves showing a flat panel, beds with the
  headboard out in the floor and the foot in the wall.

Nothing in `prop_catalog.json` or the packs' `index.json` records the facing offset, so it cannot
be derived — it was read off the rig from a row of bookshelves at rot 0/6/12/18 with a 3-tile floor
tab pointing north and a 6-tile one pointing east, because **a screenshot of a furnished room only
answers "which way is that facing" if the image carries its own compass**. An earlier attempt to
judge it from a generated room failed twice over: the props against a wall are in its shadow, and
the board camera's azimuth is whatever the last session left it at. Build the compass into the
slab.

That the offset is exactly 12 makes the placement rule collapse to `rot = wallRot` — a prop backed
against a wall carries that wall's rotation unchanged. Do not write it that way. The two halves are
independent facts and only cancel for this one use; a prop placed by any other rule (centre of the
room, on a table, hung on a wall plane) needs the offset applied on its own.

`MapGen/prop_catalog.json` is generated by `tools/build-mapgen-props.py` from the packs'
`index.json` and has a `--check` mode like the profiles script. Unlike tiles, none of it is
curated: `mount` is derived by comparing a prop's pivot to the bottom of its bounds (`hanging`
props pivot on the *top* face and drop below it — screens, curtains, wall cuffs — so placing one at
floor level buries it), and the grouping is the pack author's own `GroupTag`. There is nothing to
curate against, because **prop folders are functional rather than per-tileset**: there is no
"Castle Fortified furniture", only a `Furniture` folder shared by every medieval set. The only
theming the data supports is the coarse medieval/modern split the two pack GUIDs already are, which
is what `PropPlacer.StyleFor` reads off the profile's `Pack`. `Nature` and `Splats` are skipped
pending an outdoor pass.

A harness flag is not a user control. Props were placed, validated on 6,000 maps and rig-checked
before anyone noticed that `MapGenWindow` never set `ClutterDensity` at all, so the whole pass
short-circuited for the only person who runs the plugin — the harness reaches `LayoutSpec` directly
and had been exercising a path the UI could not. Anything added to `TemplateParams` has to be wired
into the window in the same change, and the window's own status line is the cheapest place to prove
it: it now counts props separately from tiles (recovered from the GUID, as TaleSpire does), so a
knob that is not reaching the generator reads as `0 props` instead of hiding inside a tile total.

No validator guards facing, deliberately. Geometry is checkable — `PropInsideRoom`, `PropOnFloor`
and `PropOverlap` each fail on a deliberately broken build — but a checker for rotation could only
restate the formula it is checking, and would pass either way. This is the same class as the stair
rotation in bug 19: a convention question the rig answers and the suite cannot.

### What a room is for decides what goes in it

`RoomSpec.Purpose` is a first-class field, and `MapGen/prop_recipes.json` is the whole vocabulary —
`Hall`, `Guardroom`, `Barracks`, … for medieval, `Control`, `Quarters`, `Mess`, … for modern. There
is deliberately no enum beside it: nothing in the generator switches on a particular purpose, so an
enum would just be a second list to keep in step.

`PurposeAssigner` runs **last**, after `ApplyMultiFloor` and `AddLoopConnections`, because every
property that makes a purpose read as deliberate is *relational*: a treasury is the dead end
furthest from the entrance, a guardroom is the room you walk through first, a hall is the biggest
and best connected. None of that is knowable while the BSP is still splitting. It writes nothing
but `Purpose`.

Each recipe scores itself against three features — area, graph degree, and depth in steps from the
entrance — through `wantsArea`/`wantsDegree`/`wantsDepth` in −1..1. **The features are normalised
against the map's own spread, not against absolute numbers**, so "the deepest room" means something
on a 6-room small map as well as a 30-room large one. There is no entrance in the spec (the dungeon
has no outside door yet), so it is taken to be the ground-floor room nearest the edge of the
storey's bounding box; a room the entrance cannot reach is scored as *deeper than the deepest*
rather than depth 0, which is exactly the pendant behind an upstairs route.

`minArea`/`maxArea` are plausibility bounds and **not** the selector — the wants do the selecting.
Treating a cap as the selector is how `Cell`'s `maxArea: 90` came to leave 4% of large-map rooms
eligible, so a quota of two cells could not be met on two of the three size presets. Room areas
vary by preset far more than is obvious (median 100 small, 154 medium, 216 large), so **a band
tuned by eye against one preset silently excludes the purpose from another**. Measure before
touching one, and re-measure if `Padding` or `RoomFill` change, since both move every room size.

**A quota is exact, not a floor.** A purpose the caller names is kept out of the filler draw
entirely, so "one treasury" means one and `LayoutSpec.PurposeShortfall` stays meaningful — letting
the filler top it up would make "1 requested, 3 assigned, 0 short" a normal reading, and there
would then be no number that means anything. Quotas are served tightest-first (fewest eligible
rooms first), or a purpose only two rooms can take finds them already spent on one that had the
whole map to choose from. Whatever is left is drawn by weight, damped by `RepeatDamping` each time
a purpose is used — without the damping a 9-room dungeon comes out as five storerooms, because
"storeroom fits anywhere" and "storeroom is what this room should be" are the same number to a
weighted pick.

`PropPlacer` then runs four passes per room over one shared alignment model: `light`, `corner`,
`wall`, `centre`. Lighting is per *style* rather than per purpose — every furnished room gets lit,
one fixture kind per room at a fixed spacing along its walls, whatever the room is for. The zone
names in a recipe are the pack authors' own `GroupTag`s, and **a zone that resolves to nothing is
skipped silently**, so naming an absent group costs only the variety it would have added. That is
cheap but invisible, which is what `MapGenQA recipes` is for: it prints what every zone actually
resolves to and distinguishes a group that is *absent* from one that exists but is
`0/N hanging-only`. It earned its keep immediately — three authored zones were dead, and
`modern/Control`'s `screens` was the worst of them, because a group that is 100% hanging props
looks perfectly reasonable in the file.

Two things guard the room rather than the prop. Doorway approaches and stair runs (plus a landing
cell at each end, on **both** storeys) are reserved before anything is placed. And `TryPlace`
claims its cells, re-runs a BFS between the room's ports, and *reverts* if the room came apart —
because every individual prop can be inside the room, on the floor, clear of the doorways and
overlapping nothing, and the room still be impassable when a dense draw lines both walls of a
3-wide room. Removing that guard produces 21,000 findings across a 7,200-map sweep, so it is doing
real work rather than guarding a hypothetical. The guard is armed only if the room's ports reached
each other to begin with: a room arriving already cut in two by a stair run spanning it would
otherwise reject every prop and come out bare, which reads as a broken recipe.

Four validators cover this, taking the suite to 32: `PropNotBlockingDoor`, `PropNotBlockingStairs`,
`RoomTraversable`, `PurposeQuotaMet`. **Three of them were incapable of ever failing when first
written**, and only breaking the generator on purpose showed it: they keyed their cell sets on the
room's `OriginY`, while a prop's stored Y is the floor *surface* a `FloorThickness` above it. Every
lookup missed by half a unit, nothing ever matched, and all three reported a clean run on maps full
of furniture standing in doorways. `Validators.PropY100` is now the only place that elevation is
computed — anything comparing a prop position against a cell derived from a `RoomSpec` must go
through it.

The window offers every purpose of the ground theme's style **except `Empty`**. `Empty` is a real
recipe, but it is the bucket a room falls into when nothing else fits and every room suits it, so a
quota of it is always met and only takes rooms away from what was actually asked for — a knob that
can never report a shortfall. Rig-checked end to end: a quota of Hall 2 / Guardroom 1 / Cell 2 /
Treasury 1 on a medium map came out silent, and Cell 9 on the same map reported `No room suited:
3 Cell` in amber while the prop count moved 586 → 613, which is the evidence that the spinners reach
the generator rather than merely rendering.

One curation item stands, from looking at a furnished Abandoned Village map on the rig: the four
`bed sling*` entries in `medieval/bed` are the pack's hammocks and are derived `mount: floor`,
because their pivot genuinely does sit at the bottom of their bounds — the pivot heuristic cannot
tell a slung bed from a hammock. They therefore get laid flat on the floor. Four of fourteen members
of the group, so roughly one bed in four. Whether a `bed sling` is a camp cot or a hammock is a
question for the rig, not for the data, so they are left in rather than guessed out.

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
caged-tunnel corner. Probe matched sets — and know which of the two families in bug 29 you are
aiming at before you start, because the pool of candidates is small enough that the family settles
the pick nearly every time and only the leftovers are genuinely a rig question.

The sidecar makes an A/B possible without touching the repo or rebuilding, which matters when a
long sweep has the harness binary open: write a modified `tileset_profiles.json` next to the
MapGenQA binary, run `rooms`, delete it. `ProfileCatalog.EnsureLoaded` caches into a static on
first use, so a process that has been running for minutes has already read its embedded copy and a
sidecar appearing beside it cannot change what that run sees.

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

`verified` now means what it says for all twenty: every buildable set has had a plain probe room
pasted on the rig and looked at, not merely compiled. The eleven that had never been seen were
checked in one pass, three or four rooms to a screenshot, and ten of them were already right — the
offline footprint-and-height analysis in bug 29 had found and fixed the faults first, so the rig
was confirming rather than discovering. **Spend the rig on what only eyes can settle.** The
questions that genuinely needed it were which face of a west-authored wall ends up outward, and
whether a corner from another family reads as deliberate (it does not).

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
tile at all and borrows the ship's wooden one — and, for consistency, the ship's block under it.
**Borrow the stair and its block from the same set**, or the flight and its underside are different
materials.

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
dotnet run -c Release -- rooms  --themes "Dungeon Cellar" --w 11 --d 11 --clutter 3   # furnished
dotnet run -c Release -- rooms  --themes "Sewers" --clutter 3 --purpose Library   # one purpose, pinned
dotnet run -c Release -- tiles  --themes Facility              # resolved tile per role, + heights
dotnet run -c Release -- recipes --style medieval              # what every zone resolves to
dotnet run -c Release -- fuzz   --seeds 1-300 --themes all --clutter 3 --quota "Cell=2,Treasury=1"
```

`--clutter 0-3` is off by default on `fuzz`, `render`, `slab` and `rooms`, so the tile
validators keep being exercised on bare geometry and a prop fault cannot be mistaken for a
structural one. Turn it on for a run of its own.

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

Bugs 25-27 added `WallSeatedOnEdge` and `StairTreadSupported`, taking the suite to 25. Neither was
reachable by any sweep before the data they depend on existed — the first needs the per-tile
`footprint`, the second needs `stairs` and `stairBlock` to be distinct GUIDs. **A validator can only
see distinctions the data already makes**, which is why several of these bugs survived a green sweep
for as long as they did.

Props took it to 32 — `PropInsideRoom`, `PropOnFloor`, `PropOverlap`, then `PropNotBlockingDoor`,
`PropNotBlockingStairs`, `RoomTraversable`, `PurposeQuotaMet`. They walk `map.Props`, not
`map.Tiles`, and the split is the harness's own: the tile validators reason about grid cells and
stacking, and a pivot-positioned prop that snaps to the centimetre obeys neither. `GeneratedMap`
sorts each placement by asking `PropCatalog.TryGet` about its GUID, which is the same question
TaleSpire itself asks. `EntranceLeadsOutside` then took it to 33.

The prop validators added a third trap to the two below, and it is the one that cost the most:
**a prop's Y is the floor surface, a `FloorThickness` above the storey base a `RoomSpec` records.**
Three of the four keyed their cell sets on `room.OriginY`, so every lookup missed by half a unit,
nothing ever matched, and all three passed on maps with furniture standing in doorways. They were
only caught by disabling each guard in the generator and expecting a failure that never came —
`Validators.PropY100` is now the single place that elevation is derived. A validator that compares
two coordinates has to be shown failing, because agreeing on a wrong key looks identical to
agreeing on a right one.

Because `ProfileCatalog` and `PropCatalog` resolve their JSON out of *their own assembly*, the
harness must embed the same files under the same logical names
(`TaleSpireMapGen.tileset_profiles.json`, `TaleSpireMapGen.prop_catalog.json`,
`TaleSpireMapGen.prop_recipes.json`) — see
`MapGenQA.csproj`. Without the profiles the catalog is empty, the generator falls back to hardcoded
defaults and skips multi-floor, and every stair validator passes because there are no stairs.
Without the props every prop validator passes because nothing is ever placed, and — worse — the
harness cannot tell a prop from a tile, so furniture lands in `map.Tiles` and the *tile* validators
start reporting nonsense. Without the recipes `RecipeCatalog.IsLoaded` is false, every room is
assigned `Empty`, and the four purpose and prop-placement validators pass on a map with no
furniture in it at all. A `Preflight()` in `fuzz` asserts a profile resolves for each theme and
refuses to report a green run otherwise; keep that check honest if resource loading changes again.

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
the only way to look at a wall face rather than the tops of walls. Calibration, since the argument
is absolute and guessing it costs a whole paste cycle: **zoom 5-6 frames two or three `rooms`
probes side by side** at a readable size, and **zoom 1 is what a whole small map needs** — zoom 8
on a small map fills the screen with four floor tiles. The paste x/y is a one-shot aim because the
preview is stationary once dropped and WASD does not pan; moving the anchor translates the map 1:1
in screen pixels, so correcting a bad frame means re-pasting, not nudging.

Read the screenshots by cropping the region of interest out of the full-resolution PNG and
upscaling it with PIL's LANCZOS before looking — a downscaled whole-screen shot hides exactly the
thing being judged, which is whether a cap course runs through a corner or stops at it. `numpy` is
not installed on this box, so do it in pure PIL.

**`tools/encode_slab.py`** — Generate a test slab from hardcoded placements. Includes `lbend_oc`, `single_corner`, `bend1_exact`, `bend2_exact` layouts. Output is a base64 string ready to paste into TaleSpire.

**`tools/decode_slab.py`** — Decode a base64 slab string into readable tile list, or diff two slabs to see what was added/removed between versions.

**In-code debug logging** — `SlabBuilder.DebugLog` is wired to BepInEx logger via `Plugin.Awake`. Active log tags: `[Phase2.5]`, `[Phase2.6]`, `[Phase2.7]`, `[Phase3-wall]`, `[Phase3-inner]`, `[Phase4-OC]`, `[Phase4-OC-skip]`. There used to be a `[TileDump]` block here that logged every tile near two hardcoded cells and labelled roles by Dungeon Cellar GUID prefixes; it was scaffolding for a finished investigation, ran for every user on every generate, and mislabelled every other theme. Prefer `mapgenqa render`/`slab` for this now — the harness sees the same tiles without shipping a probe in the plugin.
