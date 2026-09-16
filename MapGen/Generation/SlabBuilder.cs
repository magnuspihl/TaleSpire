using System;
using System.Collections.Generic;
using System.Linq;

namespace TaleSpireMapGen.Generation
{
    /// <summary>
    /// Converts a LayoutSpec into tile placements for SlabEncoder.
    /// TaleSpire CCW rotation convention (15° per step, 24 steps = 360°):
    ///   0=North(-Z)  6=West(-X)  12=South(+Z)  18=East(+X)
    /// Corner tile at rot=0 is the NW corner (walls face North and West).
    /// </summary>
    public static class SlabBuilder
    {
        // Set this from Plugin.Awake to capture diagnostic output via BepInEx logger.
        public static Action<string> DebugLog;

        private const int ROT_NORTH = 0;
        private const int ROT_WEST  = 6;
        private const int ROT_SOUTH = 12;
        private const int ROT_EAST  = 18;

        private struct Tile
        {
            public byte[] Guid;
            public float X, Y, Z;
            public int RotStep;
        }

        // Per-position metadata accumulated during Phase 2; stored so Phase 3 can
        // emit the right tiles (and the right number of stacked rows) in one pass.
        private struct WallInfo
        {
            public float     Cy;
            public TileEntry Floor, Wall, Inner, Corner;
            public string    IcStyle;
            public int       WallRows;
            public float     WallHeight;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Public entry point
        // ──────────────────────────────────────────────────────────────────────

        public static List<(byte[] guid, float x, float y, float z, int rotStep)> Build(LayoutSpec spec)
        {
            var tiles = new List<Tile>();

            var roomById = new Dictionary<string, RoomSpec>();
            var doorGaps = new HashSet<(string roomId, string wallSide, int offset)>();

            foreach (var room in spec.Rooms ?? Enumerable.Empty<RoomSpec>())
                roomById[room.Id] = room;

            foreach (var conn in spec.Connections ?? Enumerable.Empty<Connection>())
                doorGaps.Add((conn.FromRoomId, conn.WallSide.ToLowerInvariant(), conn.Offset));

            string layoutTheme = spec.Theme ?? "Dungeon Cellar";
            foreach (var room in spec.Rooms ?? Enumerable.Empty<RoomSpec>())
                BuildRoom(room, layoutTheme, doorGaps, tiles);

            if (spec.Connections != null)
                BuildCorridors(spec.Connections, roomById, layoutTheme, tiles);

            if (spec.VerticalConnections != null)
                foreach (var vc in spec.VerticalConnections)
                    BuildStaircase(vc, roomById, layoutTheme, tiles);

            BuildBalconies(spec, roomById, layoutTheme, tiles);
            BuildExteriorShell(spec, layoutTheme, tiles);

            // TODO: BuildRoofs(spec, roomById, layoutTheme, tiles)
            // A room needs a roof when no other room's XZ footprint overlaps it at a higher Y.
            // Roof tiles go at OriginY + wallRows * wallHeight (same layer as multi-row ceilings).
            // Requires TileRole.Roof to be defined in the catalog + profiles before implementing.

            // Several stages legitimately cover the same cell — crossing corridors both emit a
            // floor, balconies overlap corridors on the storey above. Identical tiles stacked at
            // one position z-fight in game, so collapse them here rather than teaching every
            // stage about the others.
            var seen = new HashSet<(string, int, int, int, int)>();
            var unique = new List<Tile>(tiles.Count);
            foreach (var t in tiles)
            {
                var key = (BitConverter.ToString(t.Guid),
                           (int)Math.Round(t.X * 100), (int)Math.Round(t.Y * 100), (int)Math.Round(t.Z * 100),
                           t.RotStep);
                if (seen.Add(key)) unique.Add(t);
            }

            return unique.Select(t => (t.Guid, t.X, t.Y, t.Z, t.RotStep)).ToList();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Room geometry
        // ──────────────────────────────────────────────────────────────────────

        private static void BuildRoom(
            RoomSpec room,
            string layoutTheme,
            HashSet<(string, string, int)> doorGaps,
            List<Tile> out_)
        {
            string theme = !string.IsNullOrEmpty(room.Theme) ? room.Theme : layoutTheme;

            var (wallHeight, _, wallCombo, _, _, _) = ProfileCatalog.GetData(theme);
            int wallRows = room.WallRows > 0 ? room.WallRows : 1;

            var floorTile  = TileCatalog.Get(theme, TileRole.Floor);
            var wallTile   = TileCatalog.Get(theme, TileRole.Wall);
            var cornerTile = TileCatalog.Get(theme, TileRole.Corner);
            var doorTile   = TileCatalog.Get(theme, TileRole.Door);

            int   ox = room.OriginX, oz = room.OriginZ;
            float fy = room.OriginY;
            int   w  = Math.Max(room.Width, 3);
            int   d  = Math.Max(room.Depth, 3);

            // Place wall/corner ring for each vertical row.
            for (int row = 0; row < wallRows; row++)
            {
                float wy     = fy + row * wallHeight;
                bool  isBase = row == 0;
                // Suppress the under-wall floor tile on upper rows, and for combo themes always.
                bool  noFloor = !isBase || wallCombo;

                // Corners — NW(0) NE(18) SE(12) SW(6)
                PlaceWall(out_, cornerTile, floorTile, noFloor, ox,       wy, oz,       ROT_NORTH);
                PlaceWall(out_, cornerTile, floorTile, noFloor, ox + w-1, wy, oz,       ROT_EAST);
                PlaceWall(out_, cornerTile, floorTile, noFloor, ox + w-1, wy, oz + d-1, ROT_SOUTH);
                PlaceWall(out_, cornerTile, floorTile, noFloor, ox,       wy, oz + d-1, ROT_WEST);

                // North wall (z = oz)
                for (int x = ox + 1; x <= ox + w - 2; x++)
                {
                    if (isBase)
                    {
                        bool door = doorGaps.Contains((room.Id, "north", x - (ox + 1)));
                        PlaceDoorOrWall(out_, door, doorTile, wallTile, floorTile, wallCombo, x, wy, oz, ROT_NORTH);
                    }
                    else PlaceWall(out_, wallTile, floorTile, true, x, wy, oz, ROT_NORTH);
                }

                // South wall (z = oz + d - 1)
                for (int x = ox + 1; x <= ox + w - 2; x++)
                {
                    if (isBase)
                    {
                        bool door = doorGaps.Contains((room.Id, "south", x - (ox + 1)));
                        PlaceDoorOrWall(out_, door, doorTile, wallTile, floorTile, wallCombo, x, wy, oz + d - 1, ROT_SOUTH);
                    }
                    else PlaceWall(out_, wallTile, floorTile, true, x, wy, oz + d - 1, ROT_SOUTH);
                }

                // West wall (x = ox)
                for (int z = oz + 1; z <= oz + d - 2; z++)
                {
                    if (isBase)
                    {
                        bool door = doorGaps.Contains((room.Id, "west", z - (oz + 1)));
                        PlaceDoorOrWall(out_, door, doorTile, wallTile, floorTile, wallCombo, ox, wy, z, ROT_WEST);
                    }
                    else PlaceWall(out_, wallTile, floorTile, true, ox, wy, z, ROT_WEST);
                }

                // East wall (x = ox + w - 1)
                for (int z = oz + 1; z <= oz + d - 2; z++)
                {
                    if (isBase)
                    {
                        bool door = doorGaps.Contains((room.Id, "east", z - (oz + 1)));
                        PlaceDoorOrWall(out_, door, doorTile, wallTile, floorTile, wallCombo, ox + w - 1, wy, z, ROT_EAST);
                    }
                    else PlaceWall(out_, wallTile, floorTile, true, ox + w - 1, wy, z, ROT_EAST);
                }
            }

            // Interior floor — placed once at the base elevation.
            for (int x = ox + 1; x <= ox + w - 2; x++)
                for (int z = oz + 1; z <= oz + d - 2; z++)
                    out_.Add(Mk(floorTile, x, fy, z, 0));

            // Internal ceiling: when wallRows > 1, floor tiles cap the top of the wall stack.
            // This forms the visible ceiling below and the walkable surface for any room above.
            // For rooms that are the topmost exposed layer (no room overlapping in XZ at higher Y),
            // roof tiles (TileRole.Roof) would replace or supplement these — see BuildRoofs TODO in Build().
            if (wallRows > 1)
            {
                float ceilingY = fy + wallRows * wallHeight;
                for (int x = ox; x < ox + w; x++)
                    for (int z = oz; z < oz + d; z++)
                        out_.Add(Mk(floorTile, x, ceilingY, z, 0));
            }
        }

        // Place a wall or corner tile; for non-combo themes add a floor tile underneath.
        private static void PlaceWall(
            List<Tile> out_, TileEntry tile, TileEntry floor, bool wallCombo,
            int x, float y, int z, int rot)
        {
            if (!wallCombo) out_.Add(Mk(floor, x, y, z, 0));
            out_.Add(Mk(tile, x, y, z, rot));
        }

        // At door openings always place a floor tile under the door (door tiles have no floor baked in).
        // For non-combo themes, also add floor under wall tiles.
        private static void PlaceDoorOrWall(
            List<Tile> out_, bool isDoor,
            TileEntry door, TileEntry wall, TileEntry floor, bool wallCombo,
            int x, float y, int z, int rot)
        {
            if (isDoor)
            {
                // Door is 0.5-wide; shift it to the half nearest the corridor (away from room).
                // TaleSpire snap grid: 0 = NW half, +0.5 = SE half within the tile cell.
                float odx = (rot == ROT_EAST) ? 0.5f : 0f;
                float odz = (rot == ROT_SOUTH) ? 0.5f : 0f;
                out_.Add(Mk(floor, x,       y,        z,       0));
                out_.Add(Mk(door,  x + odx, y + 0.5f, z + odz, (rot + 12) % 24));
            }
            else
            {
                if (!wallCombo) out_.Add(Mk(floor, x, y, z, 0));
                out_.Add(Mk(wall, x, y, z, rot));
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Staircase placement
        // ──────────────────────────────────────────────────────────────────────

        private static void BuildStaircase(
            VerticalConnection vc,
            Dictionary<string, RoomSpec> roomById,
            string layoutTheme,
            List<Tile> out_)
        {
            if (!roomById.TryGetValue(vc.LowerRoomId, out var lowerRoom)) return;

            string theme   = !string.IsNullOrEmpty(lowerRoom.Theme) ? lowerRoom.Theme : layoutTheme;
            var    profile = ProfileCatalog.GetProfile(theme);

            if (profile == null)
            {
                DebugLog?.Invoke($"[Staircase] No profile for theme '{theme}', skipping");
                return;
            }

            string stairType = profile.StairType ?? "";
            if (stairType != "stackable" && stairType != "mixed")
            {
                DebugLog?.Invoke($"[Staircase] Unsupported stair type '{stairType}' for '{theme}', skipping");
                return;
            }

            List<TileProfileEntry> stairList = null;
            if (profile.TilesByRole == null || !profile.TilesByRole.TryGetValue("stairs", out stairList) || stairList.Count == 0)
            {
                DebugLog?.Invoke($"[Staircase] No stair tiles for '{theme}', skipping");
                return;
            }

            int   wallRows    = lowerRoom.WallRows > 0 ? lowerRoom.WallRows : 1;
            float totalHeight = wallRows * profile.WallHeight;
            int   steps       = profile.WallHeight > 0 ? (int)Math.Round(totalHeight / 0.5f) : 0;
            if (steps == 0) { DebugLog?.Invoke("[Staircase] Zero steps, skipping"); return; }

            var stairEntry = new TileEntry(stairList[0].Id, "Stairs", TileRole.Stairs, "1x1");
            int rot = DirToRot(vc.ClimbDirection);
            (int dx, int dz) = DirToStep(vc.ClimbDirection);

            for (int i = 0; i < steps; i++)
            {
                float sx = vc.StairOriginX + dx * i;
                float sy = lowerRoom.OriginY + i * 0.5f;
                float sz = vc.StairOriginZ + dz * i;
                out_.Add(Mk(stairEntry, sx, sy, sz, rot));
            }

            DebugLog?.Invoke($"[Staircase] {vc.LowerRoomId}→{vc.UpperRoomId} theme='{theme}' steps={steps} dir={vc.ClimbDirection}");
        }

        // ──────────────────────────────────────────────────────────────────────
        // Balcony generation
        // ──────────────────────────────────────────────────────────────────────
        //
        // A balcony appears on a multi-level lower room (WallRows ≥ 2) whenever the
        // hub upper room stacked above it has at least one horizontal corridor connection
        // to another upper room.  The balcony fills the annular region between the
        // lower room's interior boundary and the hub room's footprint at the hub room's
        // floor Y, creating a walkable ring you can traverse and look down from.
        //
        // Only floor tiles are placed for now.
        // TODO: Once a "railing" or "balustrade" tile role is defined in tileset profiles,
        //       add railing tiles along the inner edge of the balcony (the hub-room-facing
        //       boundary) so players can't accidentally walk off the edge.

        private static void BuildBalconies(
            LayoutSpec spec,
            Dictionary<string, RoomSpec> roomById,
            string layoutTheme,
            List<Tile> out_)
        {
            if (spec.VerticalConnections == null) return;

            // Which upper rooms have at least one horizontal corridor?
            var hasHorizontalConn = new HashSet<string>();
            foreach (var conn in spec.Connections ?? Enumerable.Empty<Connection>())
                hasHorizontalConn.Add(conn.FromRoomId);

            foreach (var vc in spec.VerticalConnections)
            {
                if (!roomById.TryGetValue(vc.LowerRoomId, out var lower)) continue;
                if (!roomById.TryGetValue(vc.UpperRoomId, out var upper)) continue;

                // Only multi-level lower rooms need a balcony.
                int wallRows = lower.WallRows > 0 ? lower.WallRows : 1;
                if (wallRows < 2) continue;

                // Only emit a balcony when the hub room is reachable from another upper room.
                if (!hasHorizontalConn.Contains(upper.Id)) continue;

                string theme     = !string.IsNullOrEmpty(lower.Theme) ? lower.Theme : layoutTheme;
                var    floorTile = TileCatalog.Get(theme, TileRole.Floor);
                float  balconyY  = upper.OriginY;   // same level as the hub room's floor

                int oxL = lower.OriginX, ozL = lower.OriginZ;
                int wL  = Math.Max(lower.Width,  3);
                int dL  = Math.Max(lower.Depth,  3);

                int oxU = upper.OriginX, ozU = upper.OriginZ;
                int wU  = Math.Max(upper.Width,  3);
                int dU  = Math.Max(upper.Depth,  3);

                // Iterate over the lower room's interior (1 tile inside its walls).
                // Skip any tile that falls inside the hub room's footprint — those are
                // already covered by the hub room's own floor tiles.
                for (int x = oxL + 1; x <= oxL + wL - 2; x++)
                {
                    for (int z = ozL + 1; z <= ozL + dL - 2; z++)
                    {
                        bool inHub = x >= oxU && x <= oxU + wU - 1
                                  && z >= ozU && z <= ozU + dU - 1;
                        if (inHub) continue;

                        out_.Add(Mk(floorTile, x, balconyY, z, 0));
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Exterior shell — one cell outside each floor level's combined footprint,
        // closing the perimeter so the dungeon reads as a solid block from outside.
        // Space between this shell and inner room walls is hidden; no fill needed.
        // ──────────────────────────────────────────────────────────────────────

        private static void BuildExteriorShell(
            LayoutSpec spec,
            string layoutTheme,
            List<Tile> out_)
        {
            if (spec.Rooms == null || spec.Rooms.Count == 0) return;

            // Group rooms by floor Y; the shell is one rectangle per Y level so all
            // rooms at the same elevation read as one contiguous block from outside.
            var roomsByY = new Dictionary<float, List<RoomSpec>>();
            foreach (var room in spec.Rooms)
            {
                if (!roomsByY.TryGetValue(room.OriginY, out var list))
                    roomsByY[room.OriginY] = list = new List<RoomSpec>();
                list.Add(room);
            }

            foreach (var kvp in roomsByY)
            {
                float y      = kvp.Key;
                var   yRooms = kvp.Value;

                // Use the tallest room's theme/height for this level's shell.
                int    wallRows   = 1;
                float  wallHeight = 1f;
                string theme      = layoutTheme;
                foreach (var room in yRooms)
                {
                    int wr = room.WallRows > 0 ? room.WallRows : 1;
                    if (wr > wallRows)
                    {
                        wallRows = wr;
                        theme    = !string.IsNullOrEmpty(room.Theme) ? room.Theme : layoutTheme;
                    }
                }
                var pd         = ProfileCatalog.GetData(theme);
                wallHeight     = pd.wallHeight;
                var wallTile   = TileCatalog.Get(theme, TileRole.Wall);
                var cornerTile = TileCatalog.Get(theme, TileRole.Corner);

                // One ring per *cluster* of nearby rooms, not one per elevation. A storey whose
                // rooms are spread out — typically an upper floor, where rooms sit above whichever
                // lower rooms were chosen to carry them — would otherwise get a single enormous
                // wall around mostly bare board. A dense storey still clusters into one group, so
                // ground floors are unaffected.
                float bandTop = y + wallRows * wallHeight;

                foreach (var cluster in ClusterRooms(yRooms, spec))
                {
                    int minX = int.MaxValue, maxX = int.MinValue;
                    int minZ = int.MaxValue, maxZ = int.MinValue;
                    foreach (var room in cluster)
                    {
                        int rx = room.OriginX, rz = room.OriginZ;
                        int rw = Math.Max(room.Width, 3), rd = Math.Max(room.Depth, 3);
                        if (rx          < minX) minX = rx;
                        if (rx + rw - 1 > maxX) maxX = rx + rw - 1;
                        if (rz          < minZ) minZ = rz;
                        if (rz + rd - 1 > maxZ) maxZ = rz + rd - 1;
                    }
                    if (minX == int.MaxValue) continue;

                    // Shell ring: 1 cell outside the room bounding box.
                    // Blank space between shell and inner room walls is hidden from outside.
                    int sx0 = minX - 1, sx1 = maxX + 1;
                    int sz0 = minZ - 1, sz1 = maxZ + 1;

                    // A corridor can bulge outside the bounding box and so land on the ring.
                    // Shelling over it both stacks tiles and bricks up the corridor, so the ring
                    // yields to anything already standing in the elevation band it occupies. This
                    // is recomputed per cluster so a later ring also yields to an earlier one —
                    // where two rings touch, the second must not add a wall at a different
                    // rotation on a cell the first already filled.
                    var blocked = new HashSet<(int, int)>();
                    foreach (var t in out_)
                        if (t.Y >= y - 0.01f && t.Y < bandTop - 0.01f)
                            blocked.Add(((int)Math.Floor(t.X), (int)Math.Floor(t.Z)));

                    void Ring(TileEntry tile, int x, float wy, int z, int rot)
                    {
                        if (!blocked.Contains((x, z))) out_.Add(Mk(tile, x, wy, z, rot));
                    }

                    for (int row = 0; row < wallRows; row++)
                    {
                        float wy = y + row * wallHeight;

                        // North edge (z = sz0) — NW corner, north walls, NE corner.
                        Ring(cornerTile, sx0, wy, sz0, ROT_NORTH);
                        for (int x = sx0 + 1; x < sx1; x++)
                            Ring(wallTile, x, wy, sz0, ROT_NORTH);
                        Ring(cornerTile, sx1, wy, sz0, ROT_EAST);

                        // South edge (z = sz1) — SW corner, south walls, SE corner.
                        Ring(cornerTile, sx0, wy, sz1, ROT_WEST);
                        for (int x = sx0 + 1; x < sx1; x++)
                            Ring(wallTile, x, wy, sz1, ROT_SOUTH);
                        Ring(cornerTile, sx1, wy, sz1, ROT_SOUTH);

                        // West edge (x = sx0) — between the two corners.
                        for (int z = sz0 + 1; z < sz1; z++)
                            Ring(wallTile, sx0, wy, z, ROT_WEST);

                        // East edge (x = sx1) — between the two corners.
                        for (int z = sz0 + 1; z < sz1; z++)
                            Ring(wallTile, sx1, wy, z, ROT_EAST);
                    }
                }
            }
        }

        // Rooms closer than this on both axes belong under one shell. Wide enough that a normally
        // packed storey stays a single ring, narrow enough to separate rooms that only share a
        // storey because they were placed above scattered carriers.
        private const int ShellClusterGap = 8;

        private static List<List<RoomSpec>> ClusterRooms(List<RoomSpec> rooms, LayoutSpec spec)
        {
            var parent = new int[rooms.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

            for (int i = 0; i < rooms.Count; i++)
            for (int j = i + 1; j < rooms.Count; j++)
            {
                var a = rooms[i];
                var b = rooms[j];
                int dx = Math.Max(0, Math.Max(a.OriginX - (b.OriginX + b.Width),
                                              b.OriginX - (a.OriginX + a.Width)));
                int dz = Math.Max(0, Math.Max(a.OriginZ - (b.OriginZ + b.Depth),
                                              b.OriginZ - (a.OriginZ + a.Depth)));
                if (dx <= ShellClusterGap && dz <= ShellClusterGap)
                    parent[Find(i)] = Find(j);
            }

            // Two rooms joined by a corridor must share a shell whatever the distance between
            // them. Split them and the corridor runs between the two rings through open board,
            // walled by neither.
            var index = new Dictionary<string, int>();
            for (int i = 0; i < rooms.Count; i++) index[rooms[i].Id] = i;
            foreach (var c in spec.Connections ?? new List<Connection>())
                if (index.TryGetValue(c.FromRoomId, out int fi) &&
                    index.TryGetValue(c.ToRoomId,   out int ti))
                    parent[Find(fi)] = Find(ti);

            var groups = new Dictionary<int, List<RoomSpec>>();
            for (int i = 0; i < rooms.Count; i++)
            {
                int root = Find(i);
                if (!groups.TryGetValue(root, out var g)) groups[root] = g = new List<RoomSpec>();
                g.Add(rooms[i]);
            }
            return groups.Values.ToList();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Corridor generation — two-pass so crossing corridors share a global
        // floor set and naturally produce inner corners at intersections.
        // ──────────────────────────────────────────────────────────────────────

        private struct CorridorRec
        {
            public List<(int x, int z)> Positions;
            public float Cy;
            public int X1, Z1, X2, Z2, XStep, ZStep;
            public string FromSkip, ToSkip;
            public TileEntry Floor, Wall, Corner, Inner;
            public string InnerCornerStyle;
            public int   WallRows;   // = max(fromRoom.WallRows, toRoom.WallRows), min 1
            public float WallHeight; // from profile
        }

        /// The pipeline's shared state — room cells, global floors, wall notes — is keyed on (x,z)
        /// with no elevation. Feeding every storey through it at once lets a room on one floor
        /// suppress the walls of a corridor passing overhead, leaving open-sided corridors in
        /// mid-air. Each elevation therefore gets its own pass over the unchanged pipeline.
        private static HashSet<(int, int)> BuildCorridors(
            List<Connection> connections,
            Dictionary<string, RoomSpec> rooms,
            string layoutTheme,
            List<Tile> out_)
        {
            var byElevation = new Dictionary<float, List<Connection>>();
            foreach (var c in connections)
            {
                if (c?.FromRoomId == null || !rooms.TryGetValue(c.FromRoomId, out var fromRoom)) continue;
                float y = rooms.TryGetValue(c.ToRoomId ?? "", out var toRoom)
                    ? Math.Min(fromRoom.OriginY, toRoom.OriginY)
                    : fromRoom.OriginY;
                if (!byElevation.TryGetValue(y, out var list))
                    byElevation[y] = list = new List<Connection>();
                list.Add(c);
            }

            var allFloors = new HashSet<(int, int)>();
            foreach (var kv in byElevation)
            {
                var roomsHere = rooms.Values.Where(r => Math.Abs(r.OriginY - kv.Key) < 0.001f).ToList();
                foreach (var p in BuildCorridorsAtElevation(kv.Value, rooms, roomsHere, layoutTheme, out_))
                    allFloors.Add(p);
            }
            return allFloors;
        }

        private static HashSet<(int, int)> BuildCorridorsAtElevation(
            List<Connection> connections,
            Dictionary<string, RoomSpec> rooms,
            List<RoomSpec> roomsAtElevation,
            string layoutTheme,
            List<Tile> out_)
        {
            // Build the set of every grid cell that belongs to a room on this storey (walls + floor).
            // Corridor wall notes that land on room cells are suppressed; the room tile wins.
            var roomPositions = new HashSet<(int, int)>();
            foreach (var room in roomsAtElevation)
            {
                int rx = room.OriginX, rz = room.OriginZ;
                int rw = Math.Max(room.Width, 3), rd = Math.Max(room.Depth, 3);
                for (int x = rx; x < rx + rw; x++)
                    for (int z = rz; z < rz + rd; z++)
                        roomPositions.Add((x, z));
            }

            var done = new HashSet<int>();
            var recs = new List<CorridorRec>();
            var globalFloors = new HashSet<(int, int)>();

            // ── Phase 1: compute positions, emit floor tiles, build global floor set ──
            for (int i = 0; i < connections.Count; i++)
            {
                if (done.Contains(i)) continue;
                var fwd = connections[i];

                if (!rooms.TryGetValue(fwd.FromRoomId, out var fromRoom)) continue;
                if (!rooms.TryGetValue(fwd.ToRoomId,   out var toRoom))   continue;

                Connection rev = null;
                for (int j = i + 1; j < connections.Count; j++)
                {
                    var c2 = connections[j];
                    if (c2.FromRoomId == fwd.ToRoomId && c2.ToRoomId == fwd.FromRoomId)
                    { rev = c2; done.Add(j); break; }
                }
                done.Add(i);

                (float fx, float fy, float fz) = DoorPos(fromRoom, fwd.WallSide, fwd.Offset);
                (float tx, float ty, float tz, string toWall) =
                    rev != null
                        ? DoorPosWithWall(toRoom, rev.WallSide, rev.Offset)
                        : FindEntry(fromRoom, fwd, toRoom);

                string theme = !string.IsNullOrEmpty(fromRoom.Theme) ? fromRoom.Theme : layoutTheme;
                var    pd    = ProfileCatalog.GetData(theme);

                // Corridor wall height = max of the two rooms' heights.
                int fromWR = fromRoom.WallRows > 0 ? fromRoom.WallRows : 1;
                int toWR   = toRoom.WallRows   > 0 ? toRoom.WallRows   : 1;
                int corridorWR = Math.Max(fromWR, toWR);

                (float sx, float sz) = StepOut(fx, fz, fwd.WallSide);
                (float ex, float ez) = StepOut(tx, tz, toWall);
                float cy = Math.Min(fy, ty);

                int x1 = (int)Math.Round(sx), z1 = (int)Math.Round(sz);
                int x2 = (int)Math.Round(ex), z2 = (int)Math.Round(ez);
                int xStep = x2 >= x1 ? 1 : -1;
                int zStep = z2 >= z1 ? 1 : -1;
                bool dbgHasBend = (x1 != x2 && z1 != z2);
                DebugLog?.Invoke($"[Phase1] {fwd.FromRoomId}→{fwd.ToRoomId} door=({fx:F1},{fz:F1})→({tx:F1},{tz:F1}) step-out=({sx:F1},{sz:F1})→({ex:F1},{ez:F1}) grid=({x1},{z1})→({x2},{z2}) hasBend={dbgHasBend} wr={corridorWR}");

                var positions = new List<(int x, int z)>();
                for (int x = x1; x != x2; x += xStep) positions.Add((x, z1));
                for (int z = z1; z != z2 + zStep; z += zStep) positions.Add((x2, z));

                if (positions.Count == 0) continue;

                foreach (var p in positions) globalFloors.Add(p);

                var floorTile = TileCatalog.Get(theme, TileRole.Floor);
                foreach (var (cx, cz) in positions)
                    out_.Add(Mk(floorTile, cx, cy, cz, 0));

                recs.Add(new CorridorRec
                {
                    Positions        = positions, Cy = cy,
                    X1 = x1, Z1 = z1, X2 = x2, Z2 = z2,
                    XStep = xStep, ZStep = zStep,
                    FromSkip = OppositeDir(fwd.WallSide), ToSkip = OppositeDir(toWall),
                    Floor      = floorTile,
                    Wall       = TileCatalog.Get(theme, TileRole.Wall),
                    Corner     = TileCatalog.Get(theme, TileRole.Corner),
                    Inner      = TileCatalog.Get(theme, TileRole.InnerCorner),
                    InnerCornerStyle = pd.icStyle,
                    WallRows   = corridorWR,
                    WallHeight = pd.wallHeight,
                });
            }

            // ── Phase 2: accumulate wall notes from ALL corridors into a shared map ──
            // Using globalFloors means corridors don't wall off each other.
            // Where two corridors cross, the 4 diagonal cells accumulate notes from both,
            // which Phase 3 resolves to the correct inner corner.
            var wallRots = new Dictionary<(int, int), List<int>>();
            var wallInfo = new Dictionary<(int, int), WallInfo>();

            foreach (var c in recs)
            {
                bool hasBend = (c.X1 != c.X2 && c.Z1 != c.Z2);
                int icx = hasBend ? c.X2 - c.XStep : 0;
                int icz = hasBend ? c.Z1 + c.ZStep : 0;

                void NoteWall(int wx, int wz, int rot)
                {
                    if (hasBend && wx == icx && wz == icz) return; // explicit in Phase 4
                    if (roomPositions.Contains((wx, wz))) return;  // room tile wins
                    var key = (wx, wz);
                    if (!wallRots.TryGetValue(key, out var lst))
                    {
                        wallRots[key] = lst = new List<int>(2);
                        wallInfo[key] = new WallInfo
                        {
                            Cy = c.Cy, Floor = c.Floor, Wall = c.Wall, Inner = c.Inner,
                            Corner = c.Corner, IcStyle = c.InnerCornerStyle,
                            WallRows = c.WallRows, WallHeight = c.WallHeight,
                        };
                    }
                    else if (c.WallRows > wallInfo[key].WallRows)
                    {
                        // Taller corridor wins — update height at this position.
                        var wi = wallInfo[key];
                        wi.WallRows   = c.WallRows;
                        wi.WallHeight = c.WallHeight;
                        wallInfo[key] = wi;
                    }
                    if (!lst.Contains(rot)) lst.Add(rot);
                }

                for (int i = 0; i < c.Positions.Count; i++)
                {
                    var (cx, cz) = c.Positions[i];
                    bool atFrom = (i == 0);
                    bool atTo   = (i == c.Positions.Count - 1);

                    if (!globalFloors.Contains((cx, cz - 1)) && !(atFrom && c.FromSkip == "north") && !(atTo && c.ToSkip == "north"))
                        NoteWall(cx, cz - 1, ROT_NORTH);
                    if (!globalFloors.Contains((cx, cz + 1)) && !(atFrom && c.FromSkip == "south") && !(atTo && c.ToSkip == "south"))
                        NoteWall(cx, cz + 1, ROT_SOUTH);
                    if (!globalFloors.Contains((cx - 1, cz)) && !(atFrom && c.FromSkip == "west")  && !(atTo && c.ToSkip == "west"))
                        NoteWall(cx - 1, cz, ROT_WEST);
                    if (!globalFloors.Contains((cx + 1, cz)) && !(atFrom && c.FromSkip == "east")  && !(atTo && c.ToSkip == "east"))
                        NoteWall(cx + 1, cz, ROT_EAST);
                }
            }

            // ── Phase 2.5: reserve the bend corners Phase 4 will fill ──
            // Vertical-leg tiles note an L-bend's corner cells as straight walls, and Phase 2
            // only suppresses the inner corner for the corridor that owns the bend — a second
            // corridor passing nearby still notes it, and Phase 3's wall then lands under the
            // corner. Decide here exactly which cells Phase 4 will tile and strip only those:
            // stripping a cell Phase 4 declines to fill leaves a hole in the corridor wall.
            var claimIC = new (int x, int z)?[recs.Count];
            var claimOC = new (int x, int z)?[recs.Count];
            var claimed4 = new HashSet<(int, int)>();
            for (int i = 0; i < recs.Count; i++)
            {
                var c = recs[i];
                bool hasBend = (c.X1 != c.X2 && c.Z1 != c.Z2);
                if (!hasBend) continue; // L-bends only

                int icx = c.X2 - c.XStep, icz = c.Z1 + c.ZStep;
                int ocx = c.X2 + c.XStep, ocz = c.Z1 - c.ZStep;

                foreach (var (cell, isIc) in new[] { ((icx, icz), true), ((ocx, ocz), false) })
                {
                    if (globalFloors.Contains(cell) || roomPositions.Contains(cell)) continue;
                    if (!claimed4.Add(cell)) continue;
                    if (isIc) claimIC[i] = cell; else claimOC[i] = cell;
                    wallRots.Remove(cell);
                }

                DebugLog?.Invoke($"[Phase2.5] bend ({c.X1},{c.Z1})->({c.X2},{c.Z2}) " +
                                 $"ic={claimIC[i]?.ToString() ?? "-"} oc={claimOC[i]?.ToString() ?? "-"}");
            }

            // ── Phase 2.6: seal around the claimed inner corners ──
            // Phase 4's inner corner lays a floor tile on a cell that is not in any
            // corridor's Positions, so Phase 2 never walked it and never noted walls
            // around it. Usually harmless — the neighbouring leg tiles have already
            // walled the same cells — but when a leg is only one tile long the cell
            // diagonally outside the bend is left as open void and the corridor leaks.
            // Only fill genuine holes: adding a second note to an existing wall would
            // turn a straight run into an inner corner.
            for (int i = 0; i < recs.Count; i++)
            {
                if (!claimIC[i].HasValue) continue;
                var c = recs[i];
                var (icx, icz) = claimIC[i].Value;

                foreach (var (nx, nz, rot) in new[]
                {
                    (icx, icz - 1, ROT_NORTH), (icx, icz + 1, ROT_SOUTH),
                    (icx - 1, icz, ROT_WEST),  (icx + 1, icz, ROT_EAST),
                })
                {
                    var key = (nx, nz);
                    if (globalFloors.Contains(key) || roomPositions.Contains(key)) continue;
                    if (claimed4.Contains(key) || wallRots.ContainsKey(key)) continue;

                    wallRots[key] = new List<int> { rot };
                    wallInfo[key] = new WallInfo
                    {
                        Cy = c.Cy, Floor = c.Floor, Wall = c.Wall, Inner = c.Inner,
                        Corner = c.Corner, IcStyle = c.InnerCornerStyle,
                        WallRows = c.WallRows, WallHeight = c.WallHeight,
                    };
                    DebugLog?.Invoke($"[Phase2.6] sealed ({nx},{nz}) rot={rot} beside ic ({icx},{icz})");
                }
            }

            // ── Phase 2.7: fill terminal corridor-end corner gaps ──
            // Phase 2 only notes neighbors of floor tiles. The diagonal position where
            // a corridor's end-cap wall meets its side wall is never adjacent to any
            // floor tile so it gets no note.  Detect these "missing corners" by checking
            // each floor tile's four diagonals: if both perpendicular neighbors are in
            // wallRots with rotations pointing away from the floor, emit corner tiles
            // (stacked to match the corridor's WallRows).
            {
                var emitted27 = new HashSet<(int, int)>();
                var deltas    = new (int dx, int dz)[] { (1,1), (1,-1), (-1,1), (-1,-1) };

                foreach (var c in recs)
                {
                    foreach (var (fx, fz) in c.Positions)
                    {
                        foreach (var (dx, dz) in deltas)
                        {
                            int cx = fx + dx, cz = fz + dz;
                            if (globalFloors.Contains((cx, cz))) continue;
                            if (wallRots.ContainsKey((cx, cz)))  continue;
                            if (claimed4.Contains((cx, cz)))     continue;
                            if (roomPositions.Contains((cx, cz))) continue;  // room tile wins

                            int sideRot = dx > 0 ? ROT_EAST  : ROT_WEST;
                            int endRot  = dz > 0 ? ROT_SOUTH : ROT_NORTH;

                            bool hasSide = wallRots.TryGetValue((fx + dx, fz),       out var sw) && sw.Contains(sideRot);
                            bool hasEnd  = wallRots.TryGetValue((fx,       fz + dz), out var ew) && ew.Contains(endRot);
                            if (!hasSide || !hasEnd) continue;

                            if (!emitted27.Add((cx, cz))) continue;

                            int rot = CornerRotFromFacings(new List<int> { sideRot, endRot });
                            // Stack corner tiles to match corridor height.
                            for (int row = 0; row < c.WallRows; row++)
                                out_.Add(Mk(c.Corner, cx, c.Cy + row * c.WallHeight, cz, rot));

                            DebugLog?.Invoke($"[Phase2.7] floor=({fx},{fz}) corner=({cx},{cz}) rot={rot} rows={c.WallRows}");
                        }
                    }
                }
            }

            // ── Phase 3: emit walls and inner corners from shared map ──
            // count == 1 → straight wall tiles (stacked to WallRows)
            // count >= 2 → inner corner at row 0 only (filler or corner tile per profile)
            foreach (var kvp in wallRots)
            {
                var (wx, wz) = kvp.Key;
                var rots     = kvp.Value;
                var wi       = wallInfo[kvp.Key];

                bool nearOC = (Math.Abs(wx - 13) <= 3 && Math.Abs(wz - 17) <= 3)
                           || (Math.Abs(wx - 23) <= 3 && Math.Abs(wz - 35) <= 3);

                if (rots.Count == 1)
                {
                    // Straight wall — stack once per row.
                    for (int row = 0; row < wi.WallRows; row++)
                        out_.Add(Mk(wi.Wall, wx, wi.Cy + row * wi.WallHeight, wz, rots[0]));

                    if (nearOC) DebugLog?.Invoke($"[Phase3-wall] ({wx},{wz}) rot={rots[0]} rows={wi.WallRows}");
                }
                else
                {
                    // Inner corner at base row only — upper rows left open (acceptable gap).
                    int    rot   = CornerRotFromFacings(rots);
                    string style = wi.IcStyle ?? "filler";

                    if (style == "none")
                    {
                        out_.Add(Mk(wi.Floor, wx, wi.Cy, wz, 0));
                        if (nearOC) DebugLog?.Invoke($"[Phase3-inner-none] ({wx},{wz}) rot={rot}");
                    }
                    else if (style == "separate_tile")
                    {
                        out_.Add(Mk(wi.Floor,  wx, wi.Cy, wz, 0));
                        out_.Add(Mk(wi.Corner, wx, wi.Cy, wz, rot));
                        if (nearOC) DebugLog?.Invoke($"[Phase3-inner-sep] ({wx},{wz}) rot={rot}");
                    }
                    else // filler (default)
                    {
                        float icXOff = rots.Contains(ROT_EAST)  ? 0.5f : 0f;
                        float icZOff = rots.Contains(ROT_SOUTH) ? 0.5f : 0f;
                        out_.Add(Mk(wi.Floor, wx,          wi.Cy,        wz,          0));
                        out_.Add(Mk(wi.Inner, wx + icXOff, wi.Cy + 0.5f, wz + icZOff, rot));
                        if (nearOC) DebugLog?.Invoke($"[Phase3-inner] ({wx},{wz}) rot={rot} offsets=({icXOff},{icZOff})");
                    }
                }
            }

            // ── Phase 4: explicit L-bend inner and outer corners per corridor ──
            for (int i = 0; i < recs.Count; i++)
            {
                var c = recs[i];

                // Inner corner at the concave pocket — base row + filler style; upper rows use
                // the inner tile stacked at the same sub-tile offset.
                if (claimIC[i].HasValue)
                {
                    int icx = claimIC[i].Value.x, icz = claimIC[i].Value.z;
                    float icXOff = (c.XStep < 0) ? 0.5f : 0f;
                    float icZOff = (c.ZStep > 0) ? 0.5f : 0f;
                    int   icRot  = InnerCornerRot(c.XStep, c.ZStep);
                    string style = c.InnerCornerStyle ?? "filler";

                    out_.Add(Mk(c.Floor, icx, c.Cy, icz, 0)); // floor at base always

                    if (style == "none")
                    {
                        // floor only — nothing more
                    }
                    else if (style == "separate_tile")
                    {
                        for (int row = 0; row < c.WallRows; row++)
                            out_.Add(Mk(c.Corner, icx, c.Cy + row * c.WallHeight, icz, icRot));
                    }
                    else // filler
                    {
                        for (int row = 0; row < c.WallRows; row++)
                            out_.Add(Mk(c.Inner, icx + icXOff, c.Cy + row * c.WallHeight + 0.5f, icz + icZOff, icRot));
                    }
                }

                // Outer corner at the elbow — stacked to match corridor height.
                if (claimOC[i].HasValue)
                {
                    int ocx = claimOC[i].Value.x, ocz = claimOC[i].Value.z;
                    int rot = OuterCornerRot(c.XStep, c.ZStep);
                    for (int row = 0; row < c.WallRows; row++)
                        out_.Add(Mk(c.Corner, ocx, c.Cy + row * c.WallHeight, ocz, rot));

                    DebugLog?.Invoke($"[Phase4-OC] ({c.X1},{c.Z1})->({c.X2},{c.Z2}) step=({c.XStep},{c.ZStep}) corner=({ocx},{ocz}) rot={rot} rows={c.WallRows}");
                }
                else
                {
                    DebugLog?.Invoke($"[Phase4-OC-skip] straight corridor ({c.X1},{c.Z1})->({c.X2},{c.Z2})");
                }
            }

            // ── Tile dump: log all tiles within 3 cells of each outer corner cluster ──
            foreach (var t in out_)
            {
                bool nearOC = (Math.Abs(t.X - 13) <= 3 && Math.Abs(t.Z - 17) <= 3)
                           || (Math.Abs(t.X - 23) <= 3 && Math.Abs(t.Z - 35) <= 3);
                if (nearOC)
                {
                    string guidHex = BitConverter.ToString(t.Guid).Replace("-","").ToLower();
                    string role = guidHex.StartsWith("dcc8d3fd") ? "CORNER" :
                                  guidHex.StartsWith("cec14f2e") ? "INNER " :
                                  guidHex.StartsWith("ed0ad169") ? "WALL  " :
                                  guidHex.StartsWith("d5900784") ? "FLOOR " : guidHex.Substring(0,8);
                    DebugLog?.Invoke($"[TileDump] {role} x={t.X:F1} y={t.Y:F1} z={t.Z:F1} rot={t.RotStep}");
                }
            }

            return globalFloors;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Rotation helpers
        // ──────────────────────────────────────────────────────────────────────

        private static string OppositeDir(string dir)
        {
            switch (dir?.ToLowerInvariant())
            {
                case "north": return "south";
                case "south": return "north";
                case "east":  return "west";
                case "west":  return "east";
                default:      return "north";
            }
        }

        // Given two perpendicular wall-facing rotations, return the correct corner tile rotation.
        // rot=0 = NW corner (walls face N+W); rot=18 = NE; rot=12 = SE; rot=6 = SW.
        private static int CornerRotFromFacings(List<int> rots)
        {
            bool n = rots.Contains(ROT_NORTH), s = rots.Contains(ROT_SOUTH);
            bool e = rots.Contains(ROT_EAST),  w = rots.Contains(ROT_WEST);
            if (n && w) return ROT_NORTH;   // NW = 0
            if (n && e) return ROT_EAST;    // NE = 18
            if (s && e) return ROT_SOUTH;   // SE = 12
            if (s && w) return ROT_WEST;    // SW = 6
            return rots[0];                 // fallback
        }

        // The outer corner tile at the elbow of an L-corridor faces the open exterior.
        // xStep/zStep describe the direction of the horizontal/vertical legs respectively.
        private static int OuterCornerRot(int xStep, int zStep)
        {
            if (xStep > 0 && zStep > 0) return ROT_EAST;   // going E then S → NE outer corner
            if (xStep > 0 && zStep < 0) return ROT_SOUTH;  // going E then N → SE outer corner
            if (xStep < 0 && zStep > 0) return ROT_NORTH;  // going W then S → NW outer corner
            return ROT_WEST;                                // going W then N → SW outer corner
        }

        private static int InnerCornerRot(int xStep, int zStep)
        {
            if (xStep > 0 && zStep > 0) return ROT_WEST;   // SW inner corner = 6
            if (xStep > 0 && zStep < 0) return ROT_NORTH;  // NW inner corner = 0
            if (xStep < 0 && zStep > 0) return ROT_SOUTH;  // SE inner corner = 12
            return ROT_EAST;                                // NE inner corner = 18
        }

        private static int DirToRot(string dir)
        {
            switch (dir?.ToLowerInvariant())
            {
                case "north": return ROT_NORTH;
                case "west":  return ROT_WEST;
                case "south": return ROT_SOUTH;
                case "east":  return ROT_EAST;
                default:      return ROT_NORTH;
            }
        }

        private static (int dx, int dz) DirToStep(string dir)
        {
            switch (dir?.ToLowerInvariant())
            {
                case "north": return (0, -1);
                case "south": return (0,  1);
                case "east":  return (1,  0);
                case "west":  return (-1, 0);
                default:      return (0, -1);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Door / entry helpers
        // ──────────────────────────────────────────────────────────────────────

        private static (float x, float y, float z) DoorPos(RoomSpec r, string wall, int offset)
        {
            switch (wall?.ToLowerInvariant())
            {
                case "north": return (r.OriginX + 1 + offset, r.OriginY, r.OriginZ);
                case "south": return (r.OriginX + 1 + offset, r.OriginY, r.OriginZ + r.Depth - 1);
                case "west":  return (r.OriginX,               r.OriginY, r.OriginZ + 1 + offset);
                case "east":  return (r.OriginX + r.Width - 1, r.OriginY, r.OriginZ + 1 + offset);
                default:      return (r.OriginX + r.Width / 2f, r.OriginY, r.OriginZ + r.Depth / 2f);
            }
        }

        private static (float x, float y, float z, string wall) DoorPosWithWall(RoomSpec r, string wall, int offset)
        {
            var (x, y, z) = DoorPos(r, wall, offset);
            return (x, y, z, wall?.ToLowerInvariant() ?? "north");
        }

        private static (float x, float y, float z, string wall) FindEntry(
            RoomSpec fromRoom, Connection conn, RoomSpec toRoom)
        {
            float dx = (fromRoom.OriginX + fromRoom.Width  / 2f) - (toRoom.OriginX + toRoom.Width  / 2f);
            float dz = (fromRoom.OriginZ + fromRoom.Depth  / 2f) - (toRoom.OriginZ + toRoom.Depth  / 2f);

            string side;
            float x, z;

            if (Math.Abs(dx) >= Math.Abs(dz))
            {
                side = dx > 0 ? "east" : "west";
                x    = side == "east" ? toRoom.OriginX + toRoom.Width - 1 : toRoom.OriginX;
                var (_, _, fdZ) = DoorPos(fromRoom, conn.WallSide, conn.Offset);
                z = Clamp(fdZ, toRoom.OriginZ + 1, toRoom.OriginZ + toRoom.Depth - 2);
            }
            else
            {
                side = dz > 0 ? "south" : "north";
                z    = side == "south" ? toRoom.OriginZ + toRoom.Depth - 1 : toRoom.OriginZ;
                var (fdX, _, _) = DoorPos(fromRoom, conn.WallSide, conn.Offset);
                x = Clamp(fdX, toRoom.OriginX + 1, toRoom.OriginX + toRoom.Width - 2);
            }

            return (x, toRoom.OriginY, z, side);
        }

        private static (float x, float z) StepOut(float doorX, float doorZ, string wall)
        {
            switch (wall?.ToLowerInvariant())
            {
                case "north": return (doorX, doorZ - 1);
                case "south": return (doorX, doorZ + 1);
                case "west":  return (doorX - 1, doorZ);
                case "east":  return (doorX + 1, doorZ);
                default:      return (doorX, doorZ);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Utilities
        // ──────────────────────────────────────────────────────────────────────

        private static Tile Mk(TileEntry tile, int x, float y, int z, int rot) =>
            new Tile { Guid = tile.GuidBytes, X = x, Y = y, Z = z, RotStep = rot };

        private static Tile Mk(TileEntry tile, float x, float y, float z, int rot) =>
            new Tile { Guid = tile.GuidBytes, X = x, Y = y, Z = z, RotStep = rot };

        private static float Clamp(float v, float lo, float hi) =>
            v < lo ? lo : v > hi ? hi : v;
    }
}
