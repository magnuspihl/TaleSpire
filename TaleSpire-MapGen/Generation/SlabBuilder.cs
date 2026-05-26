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

            return tiles.Select(t => (t.Guid, t.X, t.Y, t.Z, t.RotStep)).ToList();
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
            string theme      = !string.IsNullOrEmpty(room.Theme) ? room.Theme : layoutTheme;
            bool   wallCombo  = TileCatalog.WallIncludesFloor(theme);

            var floorTile  = TileCatalog.Get(theme, TileRole.Floor);
            var wallTile   = TileCatalog.Get(theme, TileRole.Wall);
            var cornerTile = TileCatalog.Get(theme, TileRole.Corner);
            var doorTile   = TileCatalog.Get(theme, TileRole.Door);

            int   ox = room.OriginX, oz = room.OriginZ;
            float fy = room.OriginY;
            int   w  = Math.Max(room.Width, 3);
            int   d  = Math.Max(room.Depth, 3);

            // Corners — rot=0 is NW; CW order: NE=18, SE=12, SW=6
            PlaceWall(out_, cornerTile, floorTile, wallCombo, ox,       fy, oz,       ROT_NORTH);
            PlaceWall(out_, cornerTile, floorTile, wallCombo, ox + w-1, fy, oz,       ROT_EAST);
            PlaceWall(out_, cornerTile, floorTile, wallCombo, ox + w-1, fy, oz + d-1, ROT_SOUTH);
            PlaceWall(out_, cornerTile, floorTile, wallCombo, ox,       fy, oz + d-1, ROT_WEST);

            // North wall (z = oz)
            for (int x = ox + 1; x <= ox + w - 2; x++)
            {
                bool door = doorGaps.Contains((room.Id, "north", x - (ox + 1)));
                PlaceDoorOrWall(out_, door, doorTile, wallTile, floorTile, wallCombo, x, fy, oz, ROT_NORTH);
            }

            // South wall (z = oz + d - 1)
            for (int x = ox + 1; x <= ox + w - 2; x++)
            {
                bool door = doorGaps.Contains((room.Id, "south", x - (ox + 1)));
                PlaceDoorOrWall(out_, door, doorTile, wallTile, floorTile, wallCombo, x, fy, oz + d - 1, ROT_SOUTH);
            }

            // West wall (x = ox)
            for (int z = oz + 1; z <= oz + d - 2; z++)
            {
                bool door = doorGaps.Contains((room.Id, "west", z - (oz + 1)));
                PlaceDoorOrWall(out_, door, doorTile, wallTile, floorTile, wallCombo, ox, fy, z, ROT_WEST);
            }

            // East wall (x = ox + w - 1)
            for (int z = oz + 1; z <= oz + d - 2; z++)
            {
                bool door = doorGaps.Contains((room.Id, "east", z - (oz + 1)));
                PlaceDoorOrWall(out_, door, doorTile, wallTile, floorTile, wallCombo, ox + w - 1, fy, z, ROT_EAST);
            }

            // Interior floor
            for (int x = ox + 1; x <= ox + w - 2; x++)
                for (int z = oz + 1; z <= oz + d - 2; z++)
                    out_.Add(Mk(floorTile, x, fy, z, 0));
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
        }

        private static void BuildCorridors(
            List<Connection> connections,
            Dictionary<string, RoomSpec> rooms,
            string layoutTheme,
            List<Tile> out_)
        {
            // Build the set of every grid cell that belongs to any room (walls + floor).
            // Corridor wall notes that land on room cells are suppressed; the room tile wins.
            var roomPositions = new HashSet<(int, int)>();
            foreach (var room in rooms.Values)
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

                (float sx, float sz) = StepOut(fx, fz, fwd.WallSide);
                (float ex, float ez) = StepOut(tx, tz, toWall);
                float cy = Math.Min(fy, ty);

                int x1 = (int)Math.Round(sx), z1 = (int)Math.Round(sz);
                int x2 = (int)Math.Round(ex), z2 = (int)Math.Round(ez);
                int xStep = x2 >= x1 ? 1 : -1;
                int zStep = z2 >= z1 ? 1 : -1;
                bool dbgHasBend = (x1 != x2 && z1 != z2);
                DebugLog?.Invoke($"[Phase1] {fwd.FromRoomId}→{fwd.ToRoomId} door=({fx:F1},{fz:F1})→({tx:F1},{tz:F1}) step-out=({sx:F1},{sz:F1})→({ex:F1},{ez:F1}) grid=({x1},{z1})→({x2},{z2}) hasBend={dbgHasBend}");

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
                    Positions = positions, Cy = cy,
                    X1 = x1, Z1 = z1, X2 = x2, Z2 = z2,
                    XStep = xStep, ZStep = zStep,
                    FromSkip = OppositeDir(fwd.WallSide), ToSkip = OppositeDir(toWall),
                    Floor  = floorTile,
                    Wall   = TileCatalog.Get(theme, TileRole.Wall),
                    Corner = TileCatalog.Get(theme, TileRole.Corner),
                    Inner  = TileCatalog.Get(theme, TileRole.InnerCorner),
                });
            }

            // ── Phase 2: accumulate wall notes from ALL corridors into a shared map ──
            // Using globalFloors means corridors don't wall off each other, and crossing
            // corridors contribute notes from two directions at the 4 diagonal positions,
            // which CornerRotFromFacings then resolves to the correct inner corner rotation.
            var wallRots = new Dictionary<(int, int), List<int>>();
            var wallInfo = new Dictionary<(int, int), (float cy, TileEntry floor, TileEntry wall, TileEntry inner)>();

            foreach (var c in recs)
            {
                bool hasBend = (c.X1 != c.X2 && c.Z1 != c.Z2);
                int icx = hasBend ? c.X2 - c.XStep : 0;
                int icz = hasBend ? c.Z1 + c.ZStep : 0;

                void NoteWall(int wx, int wz, int rot)
                {
                    if (hasBend && wx == icx && wz == icz) return; // explicit below
                    if (roomPositions.Contains((wx, wz))) return;  // room tile wins
                    var key = (wx, wz);
                    if (!wallRots.TryGetValue(key, out var lst))
                    {
                        wallRots[key] = lst = new List<int>(2);
                        wallInfo[key] = (c.Cy, c.Floor, c.Wall, c.Inner);
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

            // ── Phase 2.5: remove outer corner positions from wallRots ──
            // Vertical-leg tiles note both outer corner positions as straight walls.
            // Strip them so Phase 3 doesn't place walls there; Phase 4 places correct corners.
            foreach (var c in recs)
            {
                bool hasBend = (c.X1 != c.X2 && c.Z1 != c.Z2);
                if (!hasBend) continue; // L-bends only
                int ocx = c.X2 + c.XStep, ocz = c.Z1 - c.ZStep;
                bool inFloor = globalFloors.Contains((ocx, ocz));
                bool inWall  = wallRots.ContainsKey((ocx, ocz));
                DebugLog?.Invoke($"[Phase2.5] OC pos ({ocx},{ocz}) inFloors={inFloor} inWallRots={inWall}" +
                    (inWall ? $" rots=[{string.Join(",", wallRots[(ocx, ocz)])}]" : ""));
                if (!inFloor) wallRots.Remove((ocx, ocz));
            }

            // ── Phase 2.7: fill terminal corridor-end corner gaps ──
            // Phase 2 only notes neighbors of floor tiles. The diagonal position where
            // a corridor's east end-wall meets its south wall is never adjacent to any
            // floor tile, so it gets no note. Detect these "missing corners" by checking
            // each floor tile's four diagonals: if both perpendicular neighbors are in
            // wallRots with rotations pointing away from the floor, emit a corner tile.
            {
                // L-bend outer corners are emitted by Phase 4; skip them here.
                var handledOC = new HashSet<(int, int)>();
                foreach (var c in recs)
                {
                    bool hasBend = (c.X1 != c.X2 && c.Z1 != c.Z2);
                    if (hasBend) handledOC.Add((c.X2 + c.XStep, c.Z1 - c.ZStep));
                }

                var emitted27 = new HashSet<(int, int)>();
                var deltas = new (int dx, int dz)[] { (1,1), (1,-1), (-1,1), (-1,-1) };

                foreach (var c in recs)
                {
                    foreach (var (fx, fz) in c.Positions)
                    {
                        foreach (var (dx, dz) in deltas)
                        {
                            int cx = fx + dx, cz = fz + dz;
                            if (globalFloors.Contains((cx, cz))) continue;
                            if (wallRots.ContainsKey((cx, cz)))  continue;
                            if (handledOC.Contains((cx, cz)))    continue;

                            int sideRot = dx > 0 ? ROT_EAST  : ROT_WEST;
                            int endRot  = dz > 0 ? ROT_SOUTH : ROT_NORTH;

                            bool hasSide = wallRots.TryGetValue((fx + dx, fz),      out var sw) && sw.Contains(sideRot);
                            bool hasEnd  = wallRots.TryGetValue((fx,       fz + dz), out var ew) && ew.Contains(endRot);
                            if (!hasSide || !hasEnd) continue;

                            if (!emitted27.Add((cx, cz))) continue;

                            int rot = CornerRotFromFacings(new List<int> { sideRot, endRot });
                            out_.Add(Mk(c.Corner, cx, c.Cy, cz, rot));
                            DebugLog?.Invoke($"[Phase2.7] floor=({fx},{fz}) corner=({cx},{cz}) rot={rot}");
                        }
                    }
                }
            }

            // ── Phase 3: emit walls and inner corners from shared map ──
            // count == 1 → straight wall tile
            // count >= 2 → inner corner (L-bend pocket or intersection diagonal):
            //              floor at centre + filler offset into the pocket quarter
            foreach (var kvp in wallRots)
            {
                var (wx, wz)                      = kvp.Key;
                var rots                          = kvp.Value;
                var (wallCy, floorT, wallT, innerT) = wallInfo[kvp.Key];

                if (rots.Count == 1)
                {
                    out_.Add(Mk(wallT, wx, wallCy, wz, rots[0]));
                    bool nearOC = (Math.Abs(wx - 13) <= 3 && Math.Abs(wz - 17) <= 3)
                               || (Math.Abs(wx - 23) <= 3 && Math.Abs(wz - 35) <= 3);
                    if (nearOC) DebugLog?.Invoke($"[Phase3-wall] ({wx},{wz}) rot={rots[0]}");
                }
                else
                {
                    int rot     = CornerRotFromFacings(rots);
                    float icXOff = rots.Contains(ROT_EAST)  ? 0.5f : 0f;
                    float icZOff = rots.Contains(ROT_SOUTH) ? 0.5f : 0f;
                    out_.Add(Mk(floorT, wx,          wallCy,        wz,          0));
                    out_.Add(Mk(innerT, wx + icXOff, wallCy + 0.5f, wz + icZOff, rot));
                    bool nearOC = (Math.Abs(wx - 13) <= 3 && Math.Abs(wz - 17) <= 3)
                               || (Math.Abs(wx - 23) <= 3 && Math.Abs(wz - 35) <= 3);
                    if (nearOC) DebugLog?.Invoke($"[Phase3-inner] ({wx},{wz}) rot={rot} offsets=({icXOff},{icZOff})");
                }
            }

            // ── Phase 4: explicit L-bend inner and outer corners per corridor ──
            foreach (var c in recs)
            {
                bool hasBend = (c.X1 != c.X2 && c.Z1 != c.Z2);

                // Inner corner: only for proper two-leg L-bends.
                if (hasBend)
                {
                    int icx = c.X2 - c.XStep, icz = c.Z1 + c.ZStep;
                    if (!globalFloors.Contains((icx, icz)))
                    {
                        float icXOff = (c.XStep < 0) ? 0.5f : 0f;
                        float icZOff = (c.ZStep > 0) ? 0.5f : 0f;
                        out_.Add(Mk(c.Floor, icx,          c.Cy,        icz,          0));
                        out_.Add(Mk(c.Inner, icx + icXOff, c.Cy + 0.5f, icz + icZOff, InnerCornerRot(c.XStep, c.ZStep)));
                    }
                }

                // Outer corner at the elbow of an L-bend.
                if (hasBend)
                {
                    int ocx = c.X2 + c.XStep, ocz = c.Z1 - c.ZStep;
                    out_.Add(Mk(c.Corner, ocx, c.Cy, ocz, OuterCornerRot(c.XStep, c.ZStep)));
                    DebugLog?.Invoke($"[Phase4-OC] ({c.X1},{c.Z1})->({c.X2},{c.Z2}) step=({c.XStep},{c.ZStep}) corner=({ocx},{ocz}) rot={OuterCornerRot(c.XStep, c.ZStep)}");
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
