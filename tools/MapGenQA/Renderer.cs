using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleSpireMapGen.Generation;

namespace MapGenQA
{
    /// Top-down ASCII floor plans. Deliberately not a pretty picture — the point is to make
    /// structural faults (gaps in walls, stranded corridors, missing corners) visible at a glance.
    public static class Renderer
    {
        // Higher wins when several tiles share a cell.
        private static readonly (TileRole role, char ch, int rank)[] Glyphs =
        {
            (TileRole.Floor,       '.', 0),
            (TileRole.InnerCorner, 'i', 1),
            (TileRole.Wall,        '#', 2),
            (TileRole.Corner,      '+', 3),
            (TileRole.Stairs,      'S', 4),
            (TileRole.Door,        'D', 5),
        };

        public static string Render(GeneratedMap map)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{map.Label}  rooms={map.Spec.Rooms?.Count ?? 0} " +
                          $"corridors={map.Spec.Connections?.Count ?? 0} " +
                          $"stairs={map.Spec.VerticalConnections?.Count ?? 0} " +
                          $"tiles={map.Tiles.Count}");

            int groundArea = (map.Spec.Rooms ?? new List<RoomSpec>())
                .Where(r => r.OriginY < 0.001f)
                .Sum(r => Math.Max(r.Width, 3) * Math.Max(r.Depth, 3));

            foreach (float y in map.RoomElevations)
            {
                var cells = new Dictionary<(int, int), (char ch, int rank)>();
                // Half-step tiles (doors, stair treads) belong to the cell they sit in.
                foreach (var t in map.Tiles.Where(t => t.Y >= y - 0.001f && t.Y < y + 1.0f))
                {
                    if (!t.RoleKnown) continue;
                    var g = Glyphs.FirstOrDefault(x => x.role == t.Role);
                    if (g.ch == '\0') continue;
                    if (!cells.TryGetValue(t.Cell, out var cur) || g.rank > cur.rank)
                        cells[t.Cell] = (g.ch, g.rank);
                }
                if (cells.Count == 0) continue;

                // The way in is a door like any other and would otherwise be indistinguishable from
                // the dozen corridor doorways around it, which is precisely the thing to eyeball.
                var entrance = EnclosureLeak.EntranceDoorCell(map, y);
                if (entrance != null) cells[entrance.Value] = ('@', 99);

                int minX = cells.Keys.Min(c => c.Item1), maxX = cells.Keys.Max(c => c.Item1);
                int minZ = cells.Keys.Min(c => c.Item2), maxZ = cells.Keys.Max(c => c.Item2);

                var roomsHere = (map.Spec.Rooms ?? new List<RoomSpec>())
                    .Where(r => Math.Abs(r.OriginY - y) < 0.001f).ToList();

                // Total room footprint, and what fraction of the ground floor's it is. "The upper
                // floor is too small" is a judgement about this number, and without printing it
                // there is no way to tell a real improvement from one that moved a different
                // measure — fill within the storey's own shell, say.
                int area   = roomsHere.Sum(r => Math.Max(r.Width, 3) * Math.Max(r.Depth, 3));
                string rel = groundArea > 0 && y > 0.001f
                    ? $", {100f * area / groundArea:F0}% of ground" : "";

                sb.AppendLine();
                sb.AppendLine($"── elevation y={y}  ({roomsHere.Count} rooms, {area} cells{rel}, " +
                              $"x {minX}..{maxX}, z {minZ}..{maxZ}) ──");
                for (int z = minZ; z <= maxZ; z++)
                {
                    var line = new StringBuilder();
                    for (int x = minX; x <= maxX; x++)
                        line.Append(cells.TryGetValue((x, z), out var c) ? c.ch : ' ');
                    sb.AppendLine(line.ToString().TrimEnd());
                }
            }

            sb.AppendLine();
            sb.AppendLine("legend: . floor  # wall  + corner  i inner-corner  D door  S stairs  @ way in");
            return sb.ToString();
        }
    }
}
