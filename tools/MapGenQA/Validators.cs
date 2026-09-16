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
            new IllegalRotation(),
            new EncoderRoundTrip(),
            new RoomOverlap(),
            new RoomConnectivity(),
            new CorridorSpansElevations(),
            new CorridorReachesDoor(),
            new StairsWhenMultiFloor(),
            new StairFootprintInsideRoom(),
            new UpperRoomSupported(),
            new MissingCorner(),
            new EnclosureLeak(),
        };

        internal static bool IsWallish(Placement p) =>
            p.RoleKnown && (p.Role == TileRole.Wall || p.Role == TileRole.Corner || p.Role == TileRole.InnerCorner);

        /// Cells occupied by any room at the given elevation, wall ring included.
        internal static HashSet<(int, int)> RoomCellsAt(GeneratedMap map, float y) =>
            (map.Spec.Rooms ?? new List<RoomSpec>())
                .Where(r => Math.Abs(r.OriginY - y) < 0.001f)
                .SelectMany(GeneratedMap.Footprint)
                .ToHashSet();
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

        public IEnumerable<string> Check(GeneratedMap map)
        {
            foreach (var t in map.Tiles)
            {
                if (!IsHalfStep(t.X) || !IsHalfStep(t.Z) || !IsHalfStep(t.Y))
                    yield return $"offset {t}";
            }
        }

        private static bool IsHalfStep(float v)
        {
            float f = Math.Abs(v - (float)Math.Floor(v));
            return f < 0.001f || Math.Abs(f - 0.5f) < 0.001f;
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
        public string Describes => "a stair run leaving the room it starts in";

        public IEnumerable<string> Check(GeneratedMap map)
        {
            var byId = (map.Spec.Rooms ?? new List<RoomSpec>()).ToDictionary(r => r.Id);
            var stairCells = map.Tiles.Where(t => t.RoleKnown && t.Role == TileRole.Stairs)
                                      .Select(t => t.Cell).ToHashSet();

            foreach (var v in map.Spec.VerticalConnections ?? new List<VerticalConnection>())
            {
                if (!byId.TryGetValue(v.LowerRoomId ?? "", out var lower)) continue;
                var interior = GeneratedMap.Interior(lower).ToHashSet();
                if (!interior.Contains((v.StairOriginX, v.StairOriginZ)))
                {
                    yield return $"{v.LowerRoomId}->{v.UpperRoomId} stair origin ({v.StairOriginX},{v.StairOriginZ}) is outside the room interior";
                    continue;
                }

                // A fitting origin says nothing about the run: a stair tall enough to reach the
                // upper storey can climb straight out through the far wall.
                var (dx, dz) = Step(v.ClimbDirection);
                var cell = (v.StairOriginX, v.StairOriginZ);
                while (stairCells.Contains(cell))
                {
                    if (!interior.Contains(cell))
                    {
                        yield return $"{v.LowerRoomId}->{v.UpperRoomId} stair run climbing {v.ClimbDirection} " +
                                     $"leaves the room at ({cell.Item1},{cell.Item2})";
                        break;
                    }
                    cell = (cell.Item1 + dx, cell.Item2 + dz);
                }
            }
        }

        private static (int, int) Step(string dir)
        {
            switch (dir?.ToLowerInvariant())
            {
                case "north": return (0, -1);
                case "south": return (0, 1);
                case "east":  return (1, 0);
                default:      return (-1, 0);
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
}
