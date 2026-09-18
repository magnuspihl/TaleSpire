using System;
using System.Collections.Generic;
using System.Linq;

namespace TaleSpireMapGen.Generation
{
    /// <summary>
    /// Picks the way in from outside. Reads the finished layout and writes only LayoutSpec.Entrance.
    /// </summary>
    // Runs after the layout is settled and before purposes are assigned, because "how deep is this
    // room" is measured in steps from the way in, and until there is one the assigner has to guess
    // at it. Only the geometry is decided here; SlabBuilder cuts the actual door.
    public static class EntrancePlacer
    {
        private static readonly string[] Sides = { "north", "south", "east", "west" };

        public static void Place(LayoutSpec spec)
        {
            spec.Entrance = null;
            if (spec?.Rooms == null) return;

            var ground = spec.Rooms.Where(r => r.OriginY < 0.001f).ToList();
            if (ground.Count == 0) return;

            var footprint = new HashSet<(int, int)>();
            foreach (var r in ground)
                for (int x = r.OriginX; x < r.OriginX + W(r); x++)
                    for (int z = r.OriginZ; z < r.OriginZ + D(r); z++)
                        footprint.Add((x, z));

            int minX = ground.Min(r => r.OriginX),          minZ = ground.Min(r => r.OriginZ);
            int maxX = ground.Max(r => r.OriginX + W(r) - 1), maxZ = ground.Max(r => r.OriginZ + D(r) - 1);

            // Offsets already spoken for by a corridor door, so the way in never lands on one.
            var taken = new HashSet<(string, string, int)>();
            foreach (var c in spec.Connections ?? Enumerable.Empty<Connection>())
                taken.Add((c.FromRoomId, (c.WallSide ?? "").ToLowerInvariant(), c.Offset));

            EntranceSpec best = null;
            var bestKey = (dist: int.MaxValue, area: int.MinValue, id: "");

            foreach (var room in ground)
            foreach (string side in Sides)
            {
                // How far this wall sits from the edge of the map it faces. The smallest distance
                // wins, which puts the door on the outside of the whole dungeon rather than on some
                // interior room that merely happens to have a free wall.
                int dist = side switch
                {
                    "north" => room.OriginZ - minZ,
                    "south" => maxZ - (room.OriginZ + D(room) - 1),
                    "west"  => room.OriginX - minX,
                    _       => maxX - (room.OriginX + W(room) - 1),
                };

                var key = (dist, W(room) * D(room), room.Id ?? "");
                if (Compare(key, bestKey) >= 0) continue;

                int offset = PickOffset(room, side, taken, footprint, minX, maxX, minZ, maxZ);
                if (offset < 0) continue;

                best    = new EntranceSpec { RoomId = room.Id, WallSide = side, Offset = offset };
                bestKey = key;
            }

            spec.Entrance = best;
        }

        // Lower is better: nearer the map edge, then the larger room, then lowest id.
        private static int Compare((int dist, int area, string id) a, (int dist, int area, string id) b)
        {
            if (a.dist != b.dist) return a.dist < b.dist ? -1 : 1;
            if (a.area != b.area) return a.area > b.area ? -1 : 1;
            return string.CompareOrdinal(a.id, b.id);
        }

        /// <summary>Middle-most offset on this wall with nothing built between it and open board.</summary>
        // Searched outward from the middle so the door reads as the front of the room rather than
        // being tucked against a corner, but the middle is a preference and not a requirement: a
        // wall whose middle faces another room still yields a usable way in further along.
        private static int PickOffset(RoomSpec room, string side,
                                      HashSet<(string, string, int)> taken,
                                      HashSet<(int, int)> footprint,
                                      int minX, int maxX, int minZ, int maxZ)
        {
            bool alongX = side == "north" || side == "south";
            int span = (alongX ? W(room) : D(room)) - 2;   // usable wall cells between the corners
            if (span < 1) return -1;

            for (int step = 0; step < span; step++)
            {
                // middle, middle+1, middle-1, middle+2, …
                int half = (step + 1) / 2;
                int offset = (span - 1) / 2 + (step % 2 == 0 ? half : -half);
                if (offset < 0 || offset >= span) continue;
                if (taken.Contains((room.Id, side, offset))) continue;
                if (LeadsOutside(room, side, offset, footprint, minX, maxX, minZ, maxZ)) return offset;
            }
            return -1;
        }

        /// <summary>Whether stepping out of this door and walking on reaches open board.</summary>
        // A door opening straight onto the wall of the next room along is worse than no door at
        // all, so the whole ray has to be clear rather than just the cell in front of it.
        private static bool LeadsOutside(RoomSpec room, string side, int offset,
                                         HashSet<(int, int)> footprint,
                                         int minX, int maxX, int minZ, int maxZ)
        {
            var (cx, cz) = DoorCell(room, side, offset);
            var (dx, dz) = Step(side);

            for (int x = cx + dx, z = cz + dz;
                 x >= minX - 1 && x <= maxX + 1 && z >= minZ - 1 && z <= maxZ + 1;
                 x += dx, z += dz)
                if (footprint.Contains((x, z))) return false;

            return true;
        }

        public static (int x, int z) DoorCell(RoomSpec room, string side, int offset) => side switch
        {
            "north" => (room.OriginX + 1 + offset, room.OriginZ),
            "south" => (room.OriginX + 1 + offset, room.OriginZ + D(room) - 1),
            "west"  => (room.OriginX,              room.OriginZ + 1 + offset),
            _       => (room.OriginX + W(room) - 1, room.OriginZ + 1 + offset),
        };

        /// <summary>Unit step from inside to outside through a wall on this side.</summary>
        public static (int dx, int dz) Step(string side) => side switch
        {
            "north" => (0, -1),
            "south" => (0, 1),
            "west"  => (-1, 0),
            _       => (1, 0),
        };

        private static int W(RoomSpec r) => Math.Max(r.Width, 3);
        private static int D(RoomSpec r) => Math.Max(r.Depth, 3);
    }
}
