using System;
using System.Collections.Generic;
using System.Linq;
using TaleSpireMapGen.Generation;

namespace MapGenQA
{
    public interface IValidator
    {
        string Name { get; }
        string Describes { get; }
        IEnumerable<string> Check(GeneratedMap map);
    }

    public static class Validators
    {
        public const int RotNorth = 0, RotWest = 6, RotSouth = 12, RotEast = 18;

        public static readonly IValidator[] All =
        {
            new DuplicateTile(),
            new StackedWalls(),
            new IllegalSubTileOffset(),
            new NegativePosition(),
            new IllegalRotation(),
            new EncoderRoundTrip(),
            new TileSizeIsUnit(),
            new WallRowOverlap(),
            new FloorCoverage(),
            new RoomOverlap(),
            new RoomConnectivity(),
            new CorridorSpansElevations(),
            new CorridorReachesDoor(),
            new StairsWhenMultiFloor(),
            new StairFootprintInsideRoom(),
            new UpperRoomSupported(),
            new MissingCorner(),
            new EnclosureLeak(),
            new EntranceLeadsOutside(),
            new FloorWallOverlap(),
            new StoreyAlignment(),
            new StairRiseContinuous(),
            new StairReachesLanding(),
            new StairWellOpen(),
            new StairTreadSupported(),
            new WallSeatedOnEdge(),
            new PropInsideRoom(),
            new PropOnFloor(),
            new PropOverlap(),
            new PropNotBlockingDoor(),
            new PropNotBlockingStairs(),
            new RoomTraversable(),
            new PurposeQuotaMet(),
        };

        /// A room's walkable interior as a world rectangle, with the surface props stand on.
        internal sealed class RoomFloor
        {
            public RoomSpec Room;
            public float Surface, MinX, MinZ, MaxX, MaxZ;
        }

        internal static List<RoomFloor> RoomFloors(GeneratedMap map)
        {
            var list = new List<RoomFloor>();
            foreach (var r in map.Spec.Rooms ?? new List<RoomSpec>())
            {
                int w = Math.Max(r.Width, 3), d = Math.Max(r.Depth, 3);
                list.Add(new RoomFloor
                {
                    Room    = r,
                    Surface = r.OriginY + TileCatalog.FloorThickness(ThemeOf(map, r)),
                    MinX    = r.OriginX + 1,       MinZ = r.OriginZ + 1,
                    MaxX    = r.OriginX + w - 1,   MaxZ = r.OriginZ + d - 1,
                });
            }
            return list;
        }

        internal static bool IsWallish(Placement p) =>
            p.RoleKnown && (p.Role == TileRole.Wall || p.Role == TileRole.Corner || p.Role == TileRole.InnerCorner);

        /// Cells occupied by any room at the given elevation, wall ring included.
        internal static HashSet<(int, int)> RoomCellsAt(GeneratedMap map, float y) =>
            (map.Spec.Rooms ?? new List<RoomSpec>())
                .Where(r => Math.Abs(r.OriginY - y) < 0.001f)
                .SelectMany(GeneratedMap.Footprint)
                .ToHashSet();

        internal static string ThemeOf(GeneratedMap map, RoomSpec r) =>
            string.IsNullOrEmpty(r.Theme) ? (map.Spec.Theme ?? TileCatalog.DefaultTheme) : r.Theme;

        internal static (int dx, int dz) Step(string dir)
        {
            switch (dir?.ToLowerInvariant())
            {
                case "north": return (0, -1);
                case "south": return (0, 1);
                case "east":  return (1, 0);
                default:      return (-1, 0);
            }
        }

        /// One staircase, resolved from the spec back onto the tiles that were actually placed.
        internal class StairRun
        {
            public VerticalConnection Conn;
            public RoomSpec Lower, Upper;
            public string LowerTheme, UpperTheme;
            public List<(int x, int z)> Cells = new();   // climb order
            public List<Placement> Treads = new();       // one per cell, same order
        }

        /// Walks each VerticalConnection's run outward from its origin for as long as stair tiles
        /// are found, so every checker below sees the same run the builder emitted rather than one
        /// recomputed from the same formula it is meant to be testing.
        internal static List<StairRun> StairRuns(GeneratedMap map)
        {
            var byId = (map.Spec.Rooms ?? new List<RoomSpec>()).ToDictionary(r => r.Id);
            var treadAt = map.Tiles.Where(t => t.RoleKnown && t.Role == TileRole.Stairs)
                                   .GroupBy(t => t.Cell)
                                   .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Y).First());

            var runs = new List<StairRun>();
            foreach (var v in map.Spec.VerticalConnections ?? new List<VerticalConnection>())
            {
                if (!byId.TryGetValue(v.LowerRoomId ?? "", out var lower)) continue;
                if (!byId.TryGetValue(v.UpperRoomId ?? "", out var upper)) continue;

                var run = new StairRun
                {
                    Conn = v, Lower = lower, Upper = upper,
                    LowerTheme = ThemeOf(map, lower), UpperTheme = ThemeOf(map, upper),
                };

                var (dx, dz) = Step(v.ClimbDirection);
                var cell = (v.StairOriginX, v.StairOriginZ);
                while (treadAt.TryGetValue(cell, out var t) && run.Cells.Count < 64)
                {
                    run.Cells.Add(cell);
                    run.Treads.Add(t);
                    cell = (cell.Item1 + dx, cell.Item2 + dz);
                }
                runs.Add(run);
            }
            return runs;
        }

        /// Grid cells a prop's collider reaches into, and the storey it stands on.
        // Derived from the bounds rather than from the placer's own block, so a prop that claimed
        // two cells and then sprawled across three is visible here.
        internal static IEnumerable<(int x, int z, int y100)> PropCells(PropAt p)
        {
            var b = p.Bounds;
            int y100 = (int)Math.Round(p.Y * 100);
            for (int x = (int)Math.Floor(b.MinX + 0.01f); x <= (int)Math.Ceiling(b.MaxX - 0.01f) - 1; x++)
                for (int z = (int)Math.Floor(b.MinZ + 0.01f); z <= (int)Math.Ceiling(b.MaxZ - 0.01f) - 1; z++)
                    yield return (x, z, y100);
        }

        /// The interior cell a party stands on to use a door, per room of each connection.
        /// The elevation a prop standing in this room is written at: the floor *surface*, a
        /// FloorThickness above the storey base.
        // Anything comparing prop positions against cells derived from a RoomSpec has to go through
        // here. Keying such a set on OriginY instead is silent in exactly the worst way — every
        // lookup misses by half a unit, nothing ever matches, and the validator reports a clean run
        // on a map full of furniture standing in doorways. Three of them did.
        internal static int PropY100(GeneratedMap map, RoomSpec r) =>
            (int)Math.Round((r.OriginY + TileCatalog.FloorThickness(ThemeOf(map, r))) * 100);

        internal static IEnumerable<(RoomSpec room, (int x, int z) cell)> DoorApproaches(GeneratedMap map)
        {
            var byId = (map.Spec.Rooms ?? new List<RoomSpec>()).ToDictionary(r => r.Id);
            foreach (var c in map.Spec.Connections ?? new List<Connection>())
            {
                if (!byId.TryGetValue(c.FromRoomId ?? "", out var r)) continue;
                var door = GeneratedMap.DoorCell(r, c);
                if (door == null) continue;

                // Inward is the opposite of the side the door is on.
                var (dx, dz) = (c.WallSide ?? "").ToLowerInvariant() switch
                {
                    "north" => (0, 1),
                    "south" => (0, -1),
                    "west"  => (1, 0),
                    _       => (-1, 0),
                };
                yield return (r, (door.Value.x + dx, door.Value.z + dz));
            }

            // The way in is a doorway like any other for everything that reads this — it is simply
            // not stored as a Connection, which is the kind of gap that leaves exactly one door in
            // the map unguarded.
            var e = map.Spec.Entrance;
            if (e != null && byId.TryGetValue(e.RoomId ?? "", out var er))
            {
                string side = (e.WallSide ?? "").ToLowerInvariant();
                var (ex, ez) = EntrancePlacer.DoorCell(er, side, e.Offset);
                var (idx, idz) = EntrancePlacer.Step(side);
                yield return (er, (ex - idx, ez - idz));
            }
        }

        /// Cells a stair comes up through, keyed by the elevation of the storey it breaks through.
        internal static HashSet<(int, int, int)> StairWells(GeneratedMap map)
        {
            var wells = new HashSet<(int, int, int)>();
            foreach (var run in StairRuns(map))
            {
                int y100 = (int)Math.Round(run.Upper.OriginY * 100);
                foreach (var c in run.Cells) wells.Add((c.x, c.z, y100));
            }
            return wells;
        }
    }

    // ── Integrity ────────────────────────────────────────────────────────────

    public class DuplicateTile : IValidator
    {
        public string Name => "DuplicateTile";
        public string Describes => "the same tile GUID placed twice at one position";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            var seen = new HashSet<(string, (int, int, int))>();
            foreach (var t in map.Tiles)
                if (!seen.Add((t.Guid, t.Key)))
                    yield return $"duplicate {t}";
        }
    }

    public class StackedWalls : IValidator
    {
        public string Name => "StackedWalls";
        public string Describes => "two different wall/corner tiles occupying one position";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var g in map.Tiles.Where(Validators.IsWallish).GroupBy(t => t.Key))
            {
                var distinct = g.Select(t => t.Guid).Distinct().Count();
                if (distinct > 1)
                    yield return $"stacked at ({g.First().X},{g.First().Y},{g.First().Z}): " +
                                 string.Join(" + ", g.Select(t => t.Role.ToString()).Distinct());
            }
        }
    }

    public class IllegalSubTileOffset : IValidator
    {
        public string Name => "IllegalSubTileOffset";
        public string Describes => "sub-tile offsets that are not exactly 0 or 0.5";

        // X and Z only. Y is an elevation, not a quadrant: it is whatever the tiles below add up
        // to, and a tileset with a 2.2-tall wall legitimately puts its second row at 2.2.
        // WallRowOverlap covers the vertical axis.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var t in map.Tiles)
            {
                if (!IsHalfStep(t.X) || !IsHalfStep(t.Z))
                    yield return $"offset {t}";
            }
        }

        private static bool IsHalfStep(float v)
        {
            float f = Math.Abs(v - (float)Math.Floor(v));
            return f < 0.001f || Math.Abs(f - 0.5f) < 0.001f;
        }
    }

    public class NegativePosition : IValidator
    {
        public string Name => "NegativePosition";
        public string Describes => "a tile at a negative coordinate, which the slab format cannot hold";

        // Slab positions are packed into unsigned 18-bit fields, so a negative coordinate does not
        // fail loudly — it wraps, and TaleSpire then rejects the whole slab silently. A map that is
        // otherwise perfect simply refuses to paste, and no other check here can see it: the tiles
        // themselves are all well formed.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var t in map.Tiles)
                if (t.X < 0 || t.Y < 0 || t.Z < 0)
                    yield return $"negative position {t}";
        }
    }

    public class IllegalRotation : IValidator
    {
        public string Name => "IllegalRotation";
        public string Describes => "rotation steps outside the valid 0-23 range";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var t in map.Tiles)
                if (t.Rot < 0 || t.Rot > 23)
                    yield return $"rot={t.Rot} at ({t.X},{t.Y},{t.Z})";
        }
    }

    public class EncoderRoundTrip : IValidator
    {
        public string Name => "EncoderRoundTrip";
        public string Describes => "slab encoding losing or corrupting placements";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            var placements = map.Tiles.Select(t => (t.Guid, t.X, t.Y, t.Z, t.Rot)).ToList();
            List<(string guid, float x, float y, float z, int rot)> back = null;
            string error = null;
            try
            {
                string b64 = SlabEncoder.Encode(
                    map.Tiles.Select(t => (new Guid(t.Guid).ToByteArray(), t.X, t.Y, t.Z, t.Rot)).ToList());
                back = SlabDecode.Decode(b64);
            }
            catch (Exception ex) { error = ex.Message; }

            if (error != null)
            {
                yield return $"encode/decode threw: {error}";
                yield break;
            }

            if (back.Count != placements.Count)
            {
                yield return $"tile count {placements.Count} -> {back.Count} after round trip";
                yield break;
            }

            var before = placements.Select(Norm).ToHashSet();
            foreach (var b in back.Select(Norm))
                if (!before.Contains(b))
                {
                    yield return $"round trip produced a placement that was not encoded: {b}";
                    yield break;   // one example is enough
                }
        }

        private static (string, int, int, int, int) Norm((string guid, float x, float y, float z, int rot) p) =>
            (p.guid.ToLowerInvariant(), (int)Math.Round(p.x * 100), (int)Math.Round(p.y * 100),
             (int)Math.Round(p.z * 100), p.rot);
    }

    // ── Tile fitness ─────────────────────────────────────────────────────────

    public class TileSizeIsUnit : IValidator
    {
        public string Name => "TileSizeIsUnit";
        public string Describes => "a tile larger than 1x1 placed on the 1x1 grid";

        // The generator reserves exactly one cell per tile. A 2x1 tile placed on adjacent cells
        // interpenetrates its neighbour, and no other validator sees it — the positions are
        // distinct, so DuplicateTile and StackedWalls both pass.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var t in map.Tiles)
                if (t.RoleKnown && t.Size != "1x1")
                    yield return $"{t.Role} tile is {t.Size} at ({t.X},{t.Y},{t.Z})";
        }
    }

    public class WallRowOverlap : IValidator
    {
        public string Name => "WallRowOverlap";
        public string Describes => "stacked wall rows that sink into the row below";

        // Rows are pitched by the profile's wallHeight but filled with a tile of its own height,
        // and the two need not agree. Where the tile is taller than the pitch, every row above
        // the first is buried in the one below it.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var col in map.Tiles.Where(Validators.IsWallish)
                                         .Where(t => t.Role != TileRole.InnerCorner && t.Height > 0)
                                         .GroupBy(t => t.Cell))
            {
                var stack = col.OrderBy(t => t.Y).ToList();
                for (int i = 1; i < stack.Count; i++)
                {
                    float top = stack[i - 1].Y + stack[i - 1].Height;
                    if (stack[i].Y < top - 0.001f)
                    {
                        yield return $"at ({col.Key.Item1},{col.Key.Item2}): {stack[i].Role} at " +
                                     $"y={stack[i].Y} overlaps the tile below, which tops out at {top}";
                        break;   // one report per column
                    }
                }
            }
        }
    }

    public class FloorCoverage : IValidator
    {
        public string Name => "FloorCoverage";
        public string Describes => "a room interior cell without exactly one floor tile";

        // Exactly one, in both directions. None leaves the room standing on open board; two
        // stacks coplanar surfaces that z-fight in game. Only interiors are checked — on the
        // wall ring the surface may legitimately come from a floor-carrying wall instead, and
        // whether it does is the very thing the profile flag is asserting.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            // A stair breaking through this floor is meant to leave a hole. StairWellOpen owns
            // the question of whether the hole is in the right place.
            var wells = Validators.StairWells(map);

            foreach (var room in map.Spec.Rooms ?? new List<RoomSpec>())
            {
                int y100 = (int)Math.Round(room.OriginY * 100);

                var counts = new Dictionary<(int, int), int>();
                foreach (var t in map.At(room.OriginY).Where(t => t.RoleKnown && t.Role == TileRole.Floor))
                    counts[t.Cell] = counts.TryGetValue(t.Cell, out int c) ? c + 1 : 1;

                foreach (var cell in GeneratedMap.Interior(room))
                {
                    if (wells.Contains((cell.x, cell.z, y100))) continue;
                    int n = counts.TryGetValue(cell, out int c) ? c : 0;
                    if (n != 1)
                    {
                        yield return $"room {room.Id} has {n} floor tiles at ({cell.x},{cell.z}) y={room.OriginY}";
                        break;   // one report per room
                    }
                }
            }
        }
    }

    // ── Layout quality ───────────────────────────────────────────────────────

    public class RoomOverlap : IValidator
    {
        public string Name => "RoomOverlap";
        public string Describes => "two rooms overlapping at the same elevation";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            var rooms = map.Spec.Rooms ?? new List<RoomSpec>();
            for (int i = 0; i < rooms.Count; i++)
                for (int j = i + 1; j < rooms.Count; j++)
                {
                    if (Math.Abs(rooms[i].OriginY - rooms[j].OriginY) > 0.001f) continue;
                    var a = GeneratedMap.Footprint(rooms[i]).ToHashSet();
                    if (GeneratedMap.Footprint(rooms[j]).Any(a.Contains))
                        yield return $"{rooms[i].Id} overlaps {rooms[j].Id} at y={rooms[i].OriginY}";
                }
        }
    }

    public class RoomConnectivity : IValidator
    {
        public string Name => "RoomConnectivity";
        public string Describes => "rooms unreachable from the rest of the dungeon";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            var rooms = map.Spec.Rooms ?? new List<RoomSpec>();
            if (rooms.Count == 0) yield break;

            var adj = rooms.ToDictionary(r => r.Id, _ => new List<string>());
            void Link(string a, string b)
            {
                if (a == null || b == null) return;
                if (adj.ContainsKey(a) && adj.ContainsKey(b)) { adj[a].Add(b); adj[b].Add(a); }
            }
            foreach (var c in map.Spec.Connections ?? new List<Connection>()) Link(c.FromRoomId, c.ToRoomId);
            foreach (var v in map.Spec.VerticalConnections ?? new List<VerticalConnection>()) Link(v.LowerRoomId, v.UpperRoomId);

            var seen = new HashSet<string> { rooms[0].Id };
            var queue = new Queue<string>();
            queue.Enqueue(rooms[0].Id);
            while (queue.Count > 0)
                foreach (var n in adj[queue.Dequeue()])
                    if (seen.Add(n)) queue.Enqueue(n);

            var orphans = rooms.Where(r => !seen.Contains(r.Id)).Select(r => r.Id).ToList();
            if (orphans.Count > 0)
                yield return $"{orphans.Count}/{rooms.Count} rooms unreachable: {string.Join(",", orphans.Take(6))}";
        }
    }

    public class CorridorSpansElevations : IValidator
    {
        public string Name => "CorridorSpansElevations";
        public string Describes => "a horizontal corridor joining rooms on different storeys";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            var byId = (map.Spec.Rooms ?? new List<RoomSpec>()).ToDictionary(r => r.Id);
            foreach (var c in map.Spec.Connections ?? new List<Connection>())
            {
                if (!byId.TryGetValue(c.FromRoomId ?? "", out var a)) continue;
                if (!byId.TryGetValue(c.ToRoomId ?? "", out var b)) continue;
                if (Math.Abs(a.OriginY - b.OriginY) > 0.001f)
                    yield return $"{a.Id} (y={a.OriginY}) -> {b.Id} (y={b.OriginY}) joined by a horizontal corridor";
            }
        }
    }

    public class CorridorReachesDoor : IValidator
    {
        public string Name => "CorridorReachesDoor";
        public string Describes => "a door with no corridor or room on the far side";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            var byId = (map.Spec.Rooms ?? new List<RoomSpec>()).ToDictionary(r => r.Id);

            foreach (var c in map.Spec.Connections ?? new List<Connection>())
            {
                if (!byId.TryGetValue(c.FromRoomId ?? "", out var room)) continue;
                var door = GeneratedMap.DoorCell(room, c);
                if (door == null) continue;

                var roomCells = Validators.RoomCellsAt(map, room.OriginY);
                var floors = map.At(room.OriginY)
                    .Where(t => t.RoleKnown && t.Role == TileRole.Floor)
                    .Select(t => t.Cell).ToHashSet();

                // Something walkable must sit next to the door that is not this room's own interior.
                var own = GeneratedMap.Interior(room).ToHashSet();
                bool ok = Neighbours(door.Value).Any(n => (floors.Contains(n) && !own.Contains(n)) || roomCells.Contains(n) && !own.Contains(n));
                if (!ok)
                    yield return $"door of {room.Id} ({c.WallSide}+{c.Offset}) at {door.Value} leads nowhere";
            }
        }

        internal static IEnumerable<(int, int)> Neighbours((int x, int z) c)
        {
            yield return (c.x + 1, c.z); yield return (c.x - 1, c.z);
            yield return (c.x, c.z + 1); yield return (c.x, c.z - 1);
        }
    }

    // ── Multi-floor ──────────────────────────────────────────────────────────

    public class StairsWhenMultiFloor : IValidator
    {
        public string Name => "StairsWhenMultiFloor";
        public string Describes => "rooms on more than one storey with no stairs between them";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            var levels = map.RoomElevations.ToList();
            if (levels.Count <= 1) yield break;

            int vcount = map.Spec.VerticalConnections?.Count ?? 0;
            if (vcount == 0)
            {
                yield return $"rooms exist at {levels.Count} elevations ({string.Join(",", levels)}) but no VerticalConnections were generated";
                yield break;
            }

            // A stair spec that emitted no tiles is just as broken as no spec at all.
            var stairCells = map.Spec.VerticalConnections
                .Select(v => ((int)v.StairOriginX, (int)v.StairOriginZ)).ToHashSet();
            bool any = map.Tiles.Any(t => stairCells.Contains(t.Cell));
            if (!any)
                yield return $"{vcount} VerticalConnections declared but no stair tiles were placed";
        }
    }

    public class StairFootprintInsideRoom : IValidator
    {
        public string Name => "StairFootprintInsideRoom";
        public string Describes => "a stair run leaving the rooms it joins";

        // Checked against both ends. The run stands on the lower room's floor, so leaving that
        // room puts treads in open board; it surfaces through the upper room's floor, so leaving
        // *that* room means the hole it needs is somewhere the upper storey never laid floor.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var run in Validators.StairRuns(map))
            {
                if (run.Cells.Count == 0) continue;

                foreach (var (room, which) in new[] { (run.Lower, "lower"), (run.Upper, "upper") })
                {
                    var interior = GeneratedMap.Interior(room).ToHashSet();
                    foreach (var c in run.Cells)
                    {
                        if (interior.Contains(c)) continue;
                        yield return $"{run.Conn.LowerRoomId}->{run.Conn.UpperRoomId} climbing " +
                                     $"{run.Conn.ClimbDirection} leaves the {which} room {room.Id} " +
                                     $"at ({c.x},{c.z})";
                        break;
                    }
                }
            }
        }
    }

    public class UpperRoomSupported : IValidator
    {
        public string Name => "UpperRoomSupported";
        public string Describes => "an upper room floating with no room beneath it";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            var rooms = map.Spec.Rooms ?? new List<RoomSpec>();
            foreach (var up in rooms.Where(r => r.OriginY > 0.001f))
            {
                var foot = GeneratedMap.Footprint(up).ToHashSet();
                bool supported = rooms.Any(low =>
                    low.OriginY < up.OriginY - 0.001f &&
                    GeneratedMap.Footprint(low).Any(foot.Contains));
                if (!supported)
                    yield return $"{up.Id} at y={up.OriginY} has nothing beneath it";
            }
        }
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    public class MissingCorner : IValidator
    {
        public string Name => "MissingCorner";
        public string Describes => "two perpendicular walls meeting with no corner tile at the diagonal";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (float y in map.Tiles.Select(t => t.Y).Distinct().ToList())
            {
                var level = map.At(y).ToList();

                var wallRots = new Dictionary<(int, int), HashSet<int>>();
                foreach (var t in level.Where(t => t.RoleKnown && t.Role == TileRole.Wall))
                    (wallRots.TryGetValue(t.Cell, out var s) ? s : wallRots[t.Cell] = new HashSet<int>()).Add(t.Rot);

                var corners = level.Where(t => t.RoleKnown && (t.Role == TileRole.Corner || t.Role == TileRole.InnerCorner))
                                   .Select(t => t.Cell).ToHashSet();
                var floors = level.Where(t => t.RoleKnown && t.Role == TileRole.Floor).Select(t => t.Cell).ToHashSet();
                int reported = 0;

                foreach (var f in floors)
                {
                    foreach (int dx in new[] { -1, 1 })
                    foreach (int dz in new[] { -1, 1 })
                    {
                        int sideRot = dx > 0 ? Validators.RotEast : Validators.RotWest;
                        int endRot  = dz > 0 ? Validators.RotSouth : Validators.RotNorth;

                        if (!wallRots.TryGetValue((f.Item1 + dx, f.Item2), out var sx) || !sx.Contains(sideRot)) continue;
                        if (!wallRots.TryGetValue((f.Item1, f.Item2 + dz), out var sz) || !sz.Contains(endRot)) continue;

                        var diag = (f.Item1 + dx, f.Item2 + dz);
                        if (corners.Contains(diag) || floors.Contains(diag) || wallRots.ContainsKey(diag)) continue;

                        if (reported++ < 3)
                            yield return $"y={y}: walls meet at {diag} with nothing placed there";
                    }
                }
            }
        }
    }

    public class EnclosureLeak : IValidator
    {
        public string Name => "EnclosureLeak";
        public string Describes => "room interiors leaking into open space instead of being walled in";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (float y in map.RoomElevations)
            {
                // Inner corners and doors sit half a step above the storey they belong to, so the
                // whole band up to the next wall row counts as this level. Doors are deliberately
                // not blocking: the flood is meant to leave the room through them.
                var level = map.Tiles.Where(t => t.Y >= y - 0.001f && t.Y < y + 1f).ToList();
                var blocking = level.Where(t => t.RoleKnown &&
                                   (t.Role == TileRole.Wall || t.Role == TileRole.Corner))
                               .Select(t => t.Cell).ToHashSet();

                // The way in is the one place the interior is *meant* to reach open space, so it is
                // sealed here rather than excused afterwards: every other leak still has to be
                // found, and sealing keeps this validator answering exactly one question.
                // EntranceLeadsOutside is what checks the door itself.
                var entranceCell = EntranceDoorCell(map, y);
                if (entranceCell != null) blocking.Add(entranceCell.Value);
                var walkable = level.Where(t => t.RoleKnown && (t.Role == TileRole.Floor || t.Role == TileRole.InnerCorner))
                               .Select(t => t.Cell).ToHashSet();
                if (walkable.Count == 0) continue;

                // An inner corner shares its cell with a floor but carries wall geometry on the
                // two faces its rotation names, so it blocks in those directions only.
                var icFaces = new Dictionary<(int, int), HashSet<(int, int)>>();
                foreach (var t in level.Where(t => t.RoleKnown && t.Role == TileRole.InnerCorner))
                {
                    if (!icFaces.TryGetValue(t.Cell, out var f)) f = icFaces[t.Cell] = new HashSet<(int, int)>();
                    foreach (var d in CornerFacings(t.Rot)) f.Add(d);
                }
                bool Blocked((int, int) c, (int, int) n)
                {
                    if (blocking.Contains(n)) return true;
                    var d = (n.Item1 - c.Item1, n.Item2 - c.Item2);
                    if (icFaces.TryGetValue(c, out var fc) && fc.Contains(d)) return true;
                    return icFaces.TryGetValue(n, out var fn) && fn.Contains((-d.Item1, -d.Item2));
                }

                int minX = walkable.Min(c => c.Item1) - 2, maxX = walkable.Max(c => c.Item1) + 2;
                int minZ = walkable.Min(c => c.Item2) - 2, maxZ = walkable.Max(c => c.Item2) + 2;

                var rooms = (map.Spec.Rooms ?? new List<RoomSpec>()).Where(r => Math.Abs(r.OriginY - y) < 0.001f).ToList();
                if (rooms.Count == 0) continue;

                var start = GeneratedMap.Interior(rooms[0]).FirstOrDefault();
                var seen  = new HashSet<(int, int)> { start };
                var from  = new Dictionary<(int, int), (int, int)>();
                var queue = new Queue<(int, int)>();
                queue.Enqueue(start);
                (int, int)? escapeAt = null;

                while (queue.Count > 0 && escapeAt == null)
                {
                    var c = queue.Dequeue();
                    foreach (var n in CorridorReachesDoor.Neighbours(c))
                    {
                        if (n.Item1 <= minX || n.Item1 >= maxX || n.Item2 <= minZ || n.Item2 >= maxZ)
                        { escapeAt = c; break; }
                        if (Blocked(c, n) || !seen.Add(n)) continue;
                        from[n] = c;
                        queue.Enqueue(n);
                    }
                }

                if (escapeAt == null) continue;

                // The cell that reached the edge is far outside. The useful one is where the
                // flood first stepped off tiled ground — that names the wall that is missing.
                var trail = new List<(int, int)>();
                for (var c = escapeAt.Value; ; )
                {
                    trail.Add(c);
                    if (!from.TryGetValue(c, out var prev)) break;
                    c = prev;
                }
                trail.Reverse();
                int i = trail.FindIndex(c => !walkable.Contains(c));
                if (i < 1)
                {
                    yield return $"y={y}: interior of {rooms[0].Id} reaches open space — walkable " +
                                 $"ground itself runs to the edge at ({trail[^1].Item1},{trail[^1].Item2})";
                    continue;
                }

                yield return $"y={y}: interior of {rooms[0].Id} reaches open space — leaves tiled " +
                             $"ground at ({trail[i].Item1},{trail[i].Item2}) from " +
                             $"({trail[i - 1].Item1},{trail[i - 1].Item2})";
            }
        }

        /// <summary>Cell of the declared entrance door, if it belongs to this storey.</summary>
        internal static (int, int)? EntranceDoorCell(GeneratedMap map, float y)
        {
            var e = map.Spec?.Entrance;
            if (e == null) return null;

            var room = (map.Spec.Rooms ?? new List<RoomSpec>())
                .FirstOrDefault(r => r.Id == e.RoomId);
            if (room == null || Math.Abs(room.OriginY - y) > 0.001f) return null;

            return EntrancePlacer.DoorCell(room, (e.WallSide ?? "").ToLowerInvariant(), e.Offset);
        }

        /// The two unit directions a corner tile of this rotation walls off.
        /// rot 0 = NW corner (walls face N+W), 18 = NE, 12 = SE, 6 = SW.
        internal static IEnumerable<(int, int)> CornerFacings(int rot)
        {
            switch (rot)
            {
                case Validators.RotNorth: yield return (0, -1); yield return (-1, 0); break;
                case Validators.RotEast:  yield return (0, -1); yield return (1, 0);  break;
                case Validators.RotSouth: yield return (0, 1);  yield return (1, 0);  break;
                case Validators.RotWest:  yield return (0, 1);  yield return (-1, 0); break;
            }
        }
    }

    public class EntranceLeadsOutside : IValidator
    {
        public string Name => "EntranceLeadsOutside";
        public string Describes => "the way in from outside being absent, unbuilt, or opening onto another room";

        // EnclosureLeak seals the entrance cell and asks whether everything *else* holds. This is
        // the other half of that: the door has to actually be there, and walking out of it has to
        // reach open board rather than the back of the next room along.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            var rooms = map.Spec?.Rooms ?? new List<RoomSpec>();
            var ground = rooms.Where(r => r.OriginY < 0.001f).ToList();
            if (ground.Count == 0) yield break;

            var e = map.Spec.Entrance;
            if (e == null)
            {
                yield return $"{ground.Count} ground-floor rooms and no entrance — the dungeon cannot be entered";
                yield break;
            }

            var room = rooms.FirstOrDefault(r => r.Id == e.RoomId);
            if (room == null)
            {
                yield return $"entrance names room {e.RoomId}, which is not in the layout";
                yield break;
            }

            string side = (e.WallSide ?? "").ToLowerInvariant();
            var (cx, cz) = EntrancePlacer.DoorCell(room, side, e.Offset);
            float y = room.OriginY;

            var band = map.Tiles.Where(t => t.Y >= y - 0.001f && t.Y < y + 1f).ToList();
            bool door = band.Any(t => t.RoleKnown && t.Role == TileRole.Door && t.Cell == (cx, cz));
            if (!door)
            {
                string got = string.Join("+", band.Where(t => t.RoleKnown && t.Cell == (cx, cz))
                                                  .Select(t => t.Role.ToString()).Distinct());
                yield return $"entrance at ({cx},{cz}) on the {side} wall of {room.Id} has no door tile" +
                             (got.Length > 0 ? $" — found {got}" : " — nothing placed there");
            }

            // Every ground-floor footprint cell, not just the tiles: a room that happens to be bare
            // in front of the door is still a room, and a door into one is not a way in from outside.
            var footprint = new HashSet<(int, int)>();
            foreach (var r in ground)
                for (int x = r.OriginX; x < r.OriginX + Math.Max(r.Width, 3); x++)
                    for (int z = r.OriginZ; z < r.OriginZ + Math.Max(r.Depth, 3); z++)
                        footprint.Add((x, z));

            var (dx, dz) = EntrancePlacer.Step(side);
            int maxX = ground.Max(r => r.OriginX + Math.Max(r.Width, 3));
            int maxZ = ground.Max(r => r.OriginZ + Math.Max(r.Depth, 3));
            int minX = ground.Min(r => r.OriginX), minZ = ground.Min(r => r.OriginZ);

            for (int x = cx + dx, z = cz + dz;
                 x >= minX - 1 && x <= maxX + 1 && z >= minZ - 1 && z <= maxZ + 1;
                 x += dx, z += dz)
            {
                if (footprint.Contains((x, z)))
                {
                    yield return $"entrance on the {side} wall of {room.Id} at ({cx},{cz}) opens onto " +
                                 $"built ground at ({x},{z}) rather than open board";
                    yield break;
                }
                if (band.Any(t => t.RoleKnown && t.Cell == (x, z) &&
                                  (t.Role == TileRole.Wall || t.Role == TileRole.Corner)))
                {
                    yield return $"entrance on the {side} wall of {room.Id} at ({cx},{cz}) is blocked " +
                                 $"by a corridor wall at ({x},{z})";
                    yield break;
                }
            }
        }
    }

    public class FloorWallOverlap : IValidator
    {
        public string Name => "FloorWallOverlap";
        public string Describes => "a wall whose bottom is buried in the floor tile under it";

        // WallRowOverlap only ever compared wallish tiles to each other, so a wall emitted at the
        // same elevation as the floor beneath it went unseen for the life of the project: on every
        // tileset whose wall does not carry its own floor, the ring stood half a unit below the
        // surface you walk on. Compared per cell rather than per room so shell and corridor walls
        // are covered by the same rule.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var col in map.Tiles.Where(t => t.RoleKnown).GroupBy(t => t.Cell))
            {
                var floors = col.Where(t => t.Role == TileRole.Floor && t.Height > 0).ToList();
                if (floors.Count == 0) continue;

                foreach (var w in col.Where(t => t.Role == TileRole.Wall || t.Role == TileRole.Corner))
                foreach (var f in floors)
                {
                    if (w.Y < f.Y - 0.001f || w.Y > f.Y + f.Height - 0.001f) continue;
                    yield return $"at ({col.Key.Item1},{col.Key.Item2}): {w.Role} at y={w.Y} sits " +
                                 $"inside the floor tile spanning {f.Y}..{f.Y + f.Height}";
                    goto next;   // one report per cell
                }
                next: ;
            }
        }
    }

    public class StoreyAlignment : IValidator
    {
        public string Name => "StoreyAlignment";
        public string Describes => "an upper storey that does not rest on the one below it";

        // The gap the in-game test showed between a Dungeon Cellar basement and the keep above it:
        // upperY was computed as wallRows*wallHeight + floorHeight, which counts the floor twice on
        // a tileset whose wall already carries one.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var run in Validators.StairRuns(map))
            {
                int   rows     = run.Lower.WallRows > 0 ? run.Lower.WallRows : 1;
                float expected = run.Lower.OriginY + TileCatalog.StoreyHeight(run.LowerTheme, rows);
                if (Math.Abs(run.Upper.OriginY - expected) < 0.001f) continue;

                yield return $"{run.Upper.Id} at y={run.Upper.OriginY} should sit at {expected} — " +
                             $"{rows} row(s) of '{run.LowerTheme}' above {run.Lower.Id} (y={run.Lower.OriginY})";
            }
        }
    }

    public class StairRiseContinuous : IValidator
    {
        public string Name => "StairRiseContinuous";
        public string Describes => "stair treads that do not form one continuous flight";

        // The builder stepped by a fixed 0.5 against 1.0-tall treads and started the first one at
        // the room's origin rather than on the floor's surface, so a flight read as a row of
        // separate half-height staircases standing side by side, the lowest sunk into the ground.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var run in Validators.StairRuns(map))
            {
                if (run.Treads.Count == 0)
                {
                    yield return $"{run.Conn.LowerRoomId}->{run.Conn.UpperRoomId} has no stair tile at its origin " +
                                 $"({run.Conn.StairOriginX},{run.Conn.StairOriginZ})";
                    continue;
                }

                float rise    = TileCatalog.StairRise(run.LowerTheme);
                float surface = run.Lower.OriginY + TileCatalog.FloorThickness(run.LowerTheme);

                if (Math.Abs(run.Treads[0].Y - surface) > 0.001f)
                {
                    yield return $"{run.Conn.LowerRoomId}->{run.Conn.UpperRoomId} bottom tread at y={run.Treads[0].Y} " +
                                 $"does not rest on the floor surface ({surface})";
                    continue;
                }

                for (int i = 1; i < run.Treads.Count; i++)
                {
                    float step = run.Treads[i].Y - run.Treads[i - 1].Y;
                    if (Math.Abs(step - rise) < 0.001f) continue;
                    yield return $"{run.Conn.LowerRoomId}->{run.Conn.UpperRoomId} tread {i} rises {step} " +
                                 $"above the one below it, not {rise}";
                    break;
                }
            }
        }
    }

    public class StairReachesLanding : IValidator
    {
        public string Name => "StairReachesLanding";
        public string Describes => "a stair that overshoots or falls well short of the storey it feeds";

        // Ties the tread count to the storey height: the two are computed by separate formulas and
        // a flight that stops a whole tread below its landing, or pokes above it, is the "staircase
        // ends in mid-air" symptom. A storey height that is not a whole number of treads (Dungeon
        // Cellar climbs 2.5 on 1.0 treads) leaves a sub-tread lip, which is tolerated.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var run in Validators.StairRuns(map))
            {
                if (run.Treads.Count == 0) continue;

                var   top     = run.Treads[^1];
                float arrival = top.Y + (top.Height > 0 ? top.Height : TileCatalog.StairRise(run.LowerTheme));
                float landing = run.Upper.OriginY + TileCatalog.FloorThickness(run.UpperTheme);
                float rise    = TileCatalog.StairRise(run.LowerTheme);

                if (arrival > landing + 0.001f)
                    yield return $"{run.Conn.LowerRoomId}->{run.Conn.UpperRoomId} top tread tops out at " +
                                 $"{arrival}, above the landing at {landing}";
                else if (landing - arrival >= rise - 0.001f)
                    yield return $"{run.Conn.LowerRoomId}->{run.Conn.UpperRoomId} top tread tops out at " +
                                 $"{arrival}, a full tread or more below the landing at {landing}";
            }
        }
    }

    public class StairWellOpen : IValidator
    {
        public string Name => "StairWellOpen";
        public string Describes => "a stair with no hole in the floor it climbs through, or a hole left unwalled";

        // The run surfaces inside the upper room, so that room's floor has to be cut away over it —
        // without the hole the flight tops out against the underside of the storey above. The cell
        // one beyond the top tread is the landing and must still be floored, or the hole is a pit.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var run in Validators.StairRuns(map))
            {
                if (run.Cells.Count == 0) continue;

                var floors = map.At(run.Upper.OriginY)
                                .Where(t => t.RoleKnown && t.Role == TileRole.Floor)
                                .Select(t => t.Cell).ToHashSet();

                foreach (var c in run.Cells)
                    if (floors.Contains(c))
                    {
                        yield return $"{run.Conn.LowerRoomId}->{run.Conn.UpperRoomId} climbs into floor at " +
                                     $"({c.x},{c.z}) y={run.Upper.OriginY}";
                        break;
                    }

                var (dx, dz) = Validators.Step(run.Conn.ClimbDirection);
                var head = (run.Cells[^1].x + dx, run.Cells[^1].z + dz);
                if (!floors.Contains(head))
                    yield return $"{run.Conn.LowerRoomId}->{run.Conn.UpperRoomId} arrives at ({head.Item1},{head.Item2}) " +
                                 $"y={run.Upper.OriginY}, where there is no floor to step onto";
            }
        }
    }

    public class StairTreadSupported : IValidator
    {
        public string Name => "StairTreadSupported";
        public string Describes => "a raised stair tread with nothing underneath it";

        // A tread carries only its own step, so every one above the first hangs a full rise over
        // open air and the flight reads as separate steps floating in a line rather than as a
        // staircase. The column under each tread has to be filled solid down to the floor surface,
        // by the set's stair block where it has one and its floor tile where it does not — so this
        // checks coverage of the gap rather than the presence of any particular tile.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var run in Validators.StairRuns(map))
            {
                float surface = run.Lower.OriginY + TileCatalog.FloorThickness(run.LowerTheme);

                for (int i = 0; i < run.Treads.Count; i++)
                {
                    var tread = run.Treads[i];
                    if (tread.Y <= surface + 0.01f) continue;

                    float filled = surface;
                    var column = map.Tiles
                        .Where(t => t.Cell == run.Cells[i] && t.Height > 0
                                 && t.Y >= surface - 0.01f && t.Y < tread.Y - 0.01f)
                        .OrderBy(t => t.Y);

                    foreach (var t in column)
                    {
                        if (t.Y > filled + 0.01f) break;      // gap below this tile
                        filled = Math.Max(filled, t.Y + t.Height);
                    }

                    if (filled < tread.Y - 0.01f)
                        yield return $"{run.Conn.LowerRoomId}->{run.Conn.UpperRoomId} tread {i} at " +
                                     $"({run.Cells[i].x},{run.Cells[i].z}) y={tread.Y} is unsupported from " +
                                     $"y={filled} — {tread.Y - filled:0.##} of open air beneath it";
                }
            }
        }
    }

    public class WallSeatedOnEdge : IValidator
    {
        public string Name => "WallSeatedOnEdge";
        public string Describes => "a wall parked half a cell in from the edge it is supposed to line";

        // A slab position is the minimum corner of the tile's world bounding box, so a wall whose
        // footprint is shallower than its cell hugs the low-x/low-z side whichever way it faces.
        // Facing north or west that is the outer edge and looks right; facing south or east it is
        // the inner one, and the floor tile it stands on juts out past it into open air. Only the
        // tilesets whose wall carries no floor of its own are shallow enough to show it, which is
        // why this went unseen until Castle Fortified was built in game.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var t in map.Tiles)
            {
                if (!t.RoleKnown || (t.Role != TileRole.Wall && t.Role != TileRole.Corner)) continue;
                if (t.Rot % 6 != 0) continue;
                if (!TileIndex.TryGet(t.Guid, out var entry)) continue;

                // The stored rotation is the facing the builder wanted turned onto the tile's own
                // authored one, so take the bias back off before asking which edge this piece
                // lines. Without that a west-authored wall reads as facing a side it is not on.
                int facing = (t.Rot - entry.AuthoredRotBias + 24) % 24;
                if (facing % 6 != 0) continue;
                bool linesZEdge = facing == Validators.RotNorth || facing == Validators.RotSouth;
                var foot = entry.RotatedFootprint(t.Rot);

                // A wall lies along the edge it lines, so as placed it must be the thin way across
                // that edge and fill the cell along it. Getting this wrong is what an authored
                // facing the builder has not compensated for looks like: the panel stands square
                // to the wall it is supposed to be part of.
                float across = linesZEdge ? foot.Z : foot.X;
                float along  = linesZEdge ? foot.X : foot.Z;
                if (across > entry.Thin + 0.01f || along < 0.99f)
                {
                    yield return $"{entry.Name} {t} is {foot.X:0.##}x{foot.Z:0.##} as placed, " +
                                 $"turned across the edge it lines rather than along it";
                    continue;
                }

                float far = facing == Validators.RotEast ? t.X + foot.X
                          : facing == Validators.RotSouth ? t.Z + foot.Z
                          : float.NaN;
                if (float.IsNaN(far)) continue;

                float cell = facing == Validators.RotEast ? (float)Math.Floor(t.X) : (float)Math.Floor(t.Z);
                if (Math.Abs(far - (cell + 1f)) > 0.01f)
                    yield return $"{entry.Name} {t} stops at {far:0.##}, {cell + 1f - far:0.##} short of the cell edge";
            }
        }
    }

    public class PropInsideRoom : IValidator
    {
        public string Name => "PropInsideRoom";
        public string Describes => "a prop sticking out through a wall, or standing outside any room";

        // The whole prop, not just its origin. A prop's stored position is its pivot, which for a
        // third of the catalog is not even inside its own bounds — so a prop can sit well within a
        // room by position and still have half a bed in the corridor next door.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            var floors = Validators.RoomFloors(map);

            foreach (var p in map.Props)
            {
                var (cx, cz) = p.Centre;
                var room = floors.FirstOrDefault(f =>
                    Math.Abs(f.Surface - p.Y) < 0.01f &&
                    cx >= f.MinX && cx <= f.MaxX && cz >= f.MinZ && cz <= f.MaxZ);

                if (room == null)
                {
                    yield return $"{p} centres on ({cx:0.##},{cz:0.##}) which is in no room's interior at y={p.Y}";
                    continue;
                }

                var b = p.Bounds;
                if (b.MinX < room.MinX - 0.01f || b.MaxX > room.MaxX + 0.01f ||
                    b.MinZ < room.MinZ - 0.01f || b.MaxZ > room.MaxZ + 0.01f)
                    yield return $"{p} spans ({b.MinX:0.##},{b.MinZ:0.##})-({b.MaxX:0.##},{b.MaxZ:0.##}), "
                               + $"outside {room.Room.Id} ({room.MinX},{room.MinZ})-({room.MaxX},{room.MaxZ})";
            }
        }
    }

    public class PropOnFloor : IValidator
    {
        public string Name => "PropOnFloor";
        public string Describes => "a prop buried in the floor or floating above it";

        // 89% of props are pivoted on their bottom face, so placing one at the floor surface
        // stands it there. The other 11% are pivoted on top and hang below — screens, curtains,
        // wall cuffs — and the same placement sinks them out of sight.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            var surfaces = Validators.RoomFloors(map).Select(f => f.Surface).Distinct().ToList();

            foreach (var p in map.Props)
            {
                if (!p.Entry.IsFloorMounted)
                    yield return $"{p} is '{p.Entry.Mount}'-mounted but was placed as though it stands on the floor";
                else if (!surfaces.Any(s => Math.Abs(s - p.Y) < 0.01f))
                    yield return $"{p} sits at y={p.Y}, which is no room's floor surface "
                               + $"({string.Join(", ", surfaces.OrderBy(s => s))})";
            }
        }
    }

    public class PropOverlap : IValidator
    {
        public string Name => "PropOverlap";
        public string Describes => "two props interpenetrating";

        // Compared as bounds rather than as grid cells: props do not sit on the grid, and two that
        // each claim one cell can still overlap if either is placed off-centre in it.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            var byLevel = map.Props.GroupBy(p => (int)Math.Round(p.Y * 100));

            foreach (var level in byLevel)
            {
                var list = level.Select(p => (Prop: p, B: p.Bounds)).ToList();
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var (a, b) = (list[i].B, list[j].B);
                        if (a.MinX < b.MaxX - 0.01f && b.MinX < a.MaxX - 0.01f &&
                            a.MinZ < b.MaxZ - 0.01f && b.MinZ < a.MaxZ - 0.01f)
                            yield return $"{list[i].Prop} overlaps {list[j].Prop}";
                    }
            }
        }
    }

    public class PropNotBlockingDoor : IValidator
    {
        public string Name => "PropNotBlockingDoor";
        public string Describes => "a prop standing in the cell you have to walk through to use a door";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            if (map.Props.Count == 0) yield break;

            var approaches = new Dictionary<(int, int, int), string>();
            foreach (var (room, cell) in Validators.DoorApproaches(map))
                approaches[(cell.x, cell.z, Validators.PropY100(map, room))] = room.Id;

            foreach (var p in map.Props)
                foreach (var c in Validators.PropCells(p))
                    if (approaches.TryGetValue(c, out string roomId))
                    {
                        yield return $"{p} covers the doorway approach of {roomId} at ({c.x},{c.z})";
                        break;
                    }
        }
    }

    public class PropNotBlockingStairs : IValidator
    {
        public string Name => "PropNotBlockingStairs";
        public string Describes => "a prop standing on a stair tread or its landing";

        // Both ends and both storeys. The run itself is obvious; the landing cell beyond the top
        // tread is the one that is easy to miss, because on the upper storey it is ordinary floor
        // in the middle of a room and nothing about it looks reserved.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            if (map.Props.Count == 0) yield break;

            var blocked = new HashSet<(int, int, int)>();
            foreach (var run in Validators.StairRuns(map))
            {
                if (run.Cells.Count == 0) continue;
                var (dx, dz) = Validators.Step(run.Conn.ClimbDirection);
                int lowY = Validators.PropY100(map, run.Lower);
                int upY  = Validators.PropY100(map, run.Upper);

                var cells = new List<(int x, int z)>(run.Cells)
                {
                    (run.Cells[0].x - dx, run.Cells[0].z - dz),
                    (run.Cells[^1].x + dx, run.Cells[^1].z + dz),
                };
                foreach (var c in cells) { blocked.Add((c.x, c.z, lowY)); blocked.Add((c.x, c.z, upY)); }
            }

            foreach (var p in map.Props)
                foreach (var c in Validators.PropCells(p))
                    if (blocked.Contains(c))
                    {
                        yield return $"{p} stands on the staircase at ({c.x},{c.z})";
                        break;
                    }
        }
    }

    public class RoomTraversable : IValidator
    {
        public string Name => "RoomTraversable";
        public string Describes => "furniture cutting a room's doors and stairs off from each other";

        // The failure clutter can actually cause. Every individual prop can be inside the room, on
        // the floor, clear of the doorways and overlapping nothing, and the room still be impassable
        // — a dense draw lines both walls and meets in the middle of a 3-wide room. Nothing
        // downstream can move a prop afterwards, so a map like that is simply broken.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            if (map.Props.Count == 0) yield break;

            var occupied = new HashSet<(int, int, int)>();
            foreach (var p in map.Props)
                foreach (var c in Validators.PropCells(p)) occupied.Add(c);

            var ports = new Dictionary<string, List<(int x, int z)>>();
            void Port(RoomSpec r, (int x, int z) cell)
            {
                if (!GeneratedMap.Interior(r).Contains(cell)) return;
                if (!ports.TryGetValue(r.Id, out var l)) ports[r.Id] = l = new List<(int, int)>();
                if (!l.Contains(cell)) l.Add(cell);
            }
            foreach (var (room, cell) in Validators.DoorApproaches(map)) Port(room, cell);
            foreach (var run in Validators.StairRuns(map))
            {
                foreach (var c in run.Cells) { Port(run.Lower, c); Port(run.Upper, c); }
                if (run.Cells.Count == 0) continue;
                var (dx, dz) = Validators.Step(run.Conn.ClimbDirection);
                Port(run.Upper, (run.Cells[^1].x + dx, run.Cells[^1].z + dz));
            }

            foreach (var room in map.Spec.Rooms ?? new List<RoomSpec>())
            {
                if (!ports.TryGetValue(room.Id, out var list) || list.Count < 2) continue;
                int y100 = Validators.PropY100(map, room);

                var interior = GeneratedMap.Interior(room).ToHashSet();
                var seen  = new HashSet<(int, int)> { list[0] };
                var queue = new Queue<(int x, int z)>();
                queue.Enqueue(list[0]);
                while (queue.Count > 0)
                {
                    var c = queue.Dequeue();
                    foreach (var n in CorridorReachesDoor.Neighbours(c))
                    {
                        if (!interior.Contains(n) || occupied.Contains((n.Item1, n.Item2, y100))) continue;
                        if (seen.Add(n)) queue.Enqueue(n);
                    }
                }

                var cut = list.Where(c => !seen.Contains(c)).ToList();
                if (cut.Count > 0)
                    yield return $"{room.Id} has {cut.Count}/{list.Count} doorways or stair landings "
                               + $"walled off by furniture, e.g. ({cut[0].x},{cut[0].z})";
            }
        }
    }

    public class PurposeQuotaMet : IValidator
    {
        public string Name => "PurposeQuotaMet";
        public string Describes => "a purpose quota silently not honoured, or a room left with no purpose";

        // The quota is exact: the assigner serves it first and then keeps those purposes out of the
        // filler draw, so requested == assigned + shortfall must hold for every purpose named. And
        // a shortfall is only honest if the rooms really were unavailable — every room eligible for
        // a short purpose has to have been spent on another *quota* purpose, because the filler runs
        // afterwards and never touches a quota purpose's rooms.
        public IEnumerable<string> Check(GeneratedMap map)
        {
            var rooms = map.Spec.Rooms ?? new List<RoomSpec>();
            if (rooms.Count == 0 || !RecipeCatalog.IsLoaded) yield break;

            foreach (var r in rooms)
                if (string.IsNullOrEmpty(r.Purpose))
                {
                    yield return $"{r.Id} was never given a purpose";
                    yield break;   // the assigner did not run at all; the rest says nothing
                }

            var quota = map.Spec.PurposeQuota;
            if (quota == null) yield break;

            var shortfall = map.Spec.PurposeShortfall ?? new Dictionary<string, int>();
            var quotaNames = new HashSet<string>(quota.Keys);

            foreach (var (purpose, want) in quota.Select(kv => (kv.Key, kv.Value)))
            {
                int assigned = rooms.Count(r => r.Purpose == purpose);
                int short_   = shortfall.TryGetValue(purpose, out int s) ? s : 0;

                if (assigned + short_ != want)
                    yield return $"{purpose}: {want} requested but {assigned} assigned and {short_} "
                               + $"reported short";

                if (short_ == 0) continue;
                foreach (var r in rooms)
                {
                    if (!EligibleFor(map, r, purpose)) continue;
                    if (quotaNames.Contains(r.Purpose)) continue;
                    yield return $"{purpose} reported {short_} short, but {r.Id} could have taken it "
                               + $"and was filled with {r.Purpose} instead";
                    break;
                }
            }
        }

        private static bool EligibleFor(GeneratedMap map, RoomSpec r, string purpose)
        {
            var recipe = RecipeCatalog.Get(RecipeCatalog.StyleFor(Validators.ThemeOf(map, r)), purpose);
            int area = Math.Max(r.Width, 3) * Math.Max(r.Depth, 3);
            return recipe != null && recipe.Fits(area, r.OriginY > 0.001f);
        }
    }
}
