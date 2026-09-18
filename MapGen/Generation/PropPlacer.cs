using System;
using System.Collections.Generic;
using System.Linq;

namespace TaleSpireMapGen.Generation
{
    /// <summary>
    /// Dresses built rooms with furniture. Runs after the geometry is decided and never changes
    /// it — a prop is added to the slab or it is not.
    /// </summary>
    // Props are a different placeable kind from tiles and obey different rules: their stored
    // position is a pivot rather than a bounds corner, they snap to the centimetre instead of the
    // half cell, and they may take any of the 24 rotation steps. Keeping them out of SlabBuilder
    // keeps those two conventions from being mixed up in one file.
    public static class PropPlacer
    {
        private const int ROT_NORTH = 0;
        private const int ROT_WEST  = 6;
        private const int ROT_SOUTH = 12;
        private const int ROT_EAST  = 18;

        // A prop's rotation is offset half a turn from a tile's. Tile art is authored facing -Z,
        // so a wall at rot 0 faces north; prop art is authored facing +Z, so a prop at rot 0 faces
        // south. Nothing in the data records this — it was read off the rig from a row of
        // bookshelves at rot 0/6/12/18, which is the only thing that can distinguish the two.
        private const int PropFacingOffset = 12;

        // Fraction of a room's eligible wall cells that get a prop, per ClutterDensity step.
        // Index 0 is never reached — density 0 means the whole pass is skipped — but it is listed
        // so the array reads as the knob does.
        private static readonly float[] Fill = { 0f, 0.15f, 0.35f, 0.7f };

        // The middle of a room is where people and minis go, so it fills far more slowly than the
        // walls do. At the same fraction as the walls a dense room becomes impassable furniture.
        private static readonly float[] CentreFill = { 0f, 0.05f, 0.12f, 0.25f };

        // Cells between wall lights. Fixed rather than scaled by density: a dark room is a bug,
        // not a sparse one, so the knob changes how furnished a room is and not how lit.
        private const int LightSpacing = 5;

        /// <summary>Where a prop sits within the block of cells it claims, per axis.</summary>
        private enum Align { Low, High, Centre }

        public struct PropPlacement
        {
            public byte[] Guid;
            public float X, Y, Z;
            public int RotStep;
        }

        public static List<PropPlacement> Place(LayoutSpec spec, string layoutTheme)
        {
            var placed = new List<PropPlacement>();
            if (spec?.Rooms == null || spec.ClutterDensity <= 0 || !PropCatalog.IsLoaded)
                return placed;

            int density = Math.Min(spec.ClutterDensity, Fill.Length - 1);
            var rng = new Random(spec.Seed * 31 + 7);
            var reserved = ReservedCells(spec, layoutTheme);

            foreach (var room in spec.Rooms)
                PlaceInRoom(room, layoutTheme, reserved, density, rng, placed);

            return placed;
        }

        // ── One room ─────────────────────────────────────────────────────────

        private static void PlaceInRoom(
            RoomSpec room, string layoutTheme,
            HashSet<(int x, int z, int y100)> reserved,
            int density, Random rng, List<PropPlacement> out_)
        {
            string theme   = !string.IsNullOrEmpty(room.Theme) ? room.Theme : layoutTheme;
            string style   = RecipeCatalog.StyleFor(theme);
            float  surface = room.OriginY + TileCatalog.FloorThickness(theme);
            int    y100    = (int)Math.Round(room.OriginY * 100);

            // Headroom, so a two-storey statue is not stood in a crawlspace. The ceiling is the
            // storey line; the floor a prop stands on is a FloorThickness above the room's base.
            int   rows      = room.WallRows > 0 ? room.WallRows : 1;
            float clearance = TileCatalog.StoreyHeight(theme, rows) - TileCatalog.FloorThickness(theme);

            int w = Math.Max(room.Width, 3), d = Math.Max(room.Depth, 3);
            int x0 = room.OriginX + 1, x1 = room.OriginX + w - 2;
            int z0 = room.OriginZ + 1, z1 = room.OriginZ + d - 2;

            // Every interior cell that is still free, and the cells a prop may not take but a
            // party still walks over. A prop consumes free cells as it is placed, so this is
            // mutated rather than recomputed.
            var free  = new HashSet<(int, int)>();
            var ports = new List<(int, int)>();
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (reserved.Contains((x, z, y100))) ports.Add((x, z));
                    else free.Add((x, z));

            var room_ = new Room
            {
                X0 = x0, X1 = x1, Z0 = z0, Z1 = z1,
                Free = free, Ports = ports, Surface = surface, Clearance = clearance,
            };

            // Only guard a room whose ports reach each other to begin with. A room that arrives
            // already cut in two — a stair run spanning it corner to corner, say — would otherwise
            // reject every prop and come out bare, which reads as the recipe being broken.
            room_.Guard = PortsConnected(room_);

            var recipe = RecipeCatalog.Get(style, room.Purpose);

            PlaceLights(room_, style, rng, out_);
            PlaceCorners(room_, style, recipe, density, rng, out_);
            PlaceWalls(room_, style, recipe, density, rng, out_);
            PlaceCentre(room_, style, recipe, density, rng, out_);
        }

        /// <summary>The placeable state of one room — what the zone passes read and consume.</summary>
        private class Room
        {
            public int X0, X1, Z0, Z1;
            public HashSet<(int, int)> Free;
            public List<(int, int)> Ports;
            public float Surface, Clearance;
            public bool Guard;

            public bool OnWall(int x, int z) => x == X0 || x == X1 || z == Z0 || z == Z1;
        }

        // ── Zones ────────────────────────────────────────────────────────────

        // Light is per style rather than per purpose, because a room being dark is a property of
        // the dungeon and not of what the room is for. One fixture type per room: a wall carrying
        // a torch, a lantern and a brazier in three consecutive sconces reads as a junk shop.
        private static void PlaceLights(Room room, string style, Random rng, List<PropPlacement> out_)
        {
            var fixture = PickFrom(ResolveGroups(style, RecipeCatalog.Light(style)), room.Clearance, rng);
            if (fixture == null) return;

            var anchors = WallAnchors(room);
            for (int i = 0; i < anchors.Count; i += LightSpacing)
            {
                var (cell, wallRot) = anchors[i];
                TryPlaceAt(room, fixture, cell, wallRot, out_);
            }
        }

        // Corners are their own zone because a prop in one wants to be turned into the angle
        // rather than flat against either face, and because a cell touching two walls is the one
        // place a bulky prop costs the room nothing.
        private static void PlaceCorners(Room room, string style, PropRecipe recipe,
                                         int density, Random rng, List<PropPlacement> out_)
        {
            var groups = Resolve(style, recipe, RecipeCatalog.ZoneCorner);
            if (groups.Count == 0) return;

            var corners = new List<((int x, int z) cell, int outward)>
            {
                ((room.X0, room.Z0), Diagonal(ROT_NORTH, ROT_WEST)),
                ((room.X1, room.Z0), Diagonal(ROT_NORTH, ROT_EAST)),
                ((room.X0, room.Z1), Diagonal(ROT_SOUTH, ROT_WEST)),
                ((room.X1, room.Z1), Diagonal(ROT_SOUTH, ROT_EAST)),
            };
            Shuffle(corners, rng);

            int budget = (int)Math.Round(corners.Count * Fill[density]);
            foreach (var (cell, outward) in corners)
            {
                if (budget <= 0) break;
                var prop = PickFrom(groups, room.Clearance, rng);
                if (prop == null) continue;
                if (TryPlaceAt(room, prop, cell, outward, out_)) budget--;
            }
        }

        private static void PlaceWalls(Room room, string style, PropRecipe recipe,
                                       int density, Random rng, List<PropPlacement> out_)
        {
            var groups = Resolve(style, recipe, RecipeCatalog.ZoneWall);
            if (groups.Count == 0) return;

            var anchors = WallAnchors(room);
            int budget = (int)Math.Round(anchors.Count * Fill[density]);
            Shuffle(anchors, rng);

            foreach (var (cell, wallRot) in anchors)
            {
                if (budget <= 0) break;
                var prop = PickFrom(groups, room.Clearance, rng);
                if (prop == null) continue;
                if (TryPlaceAt(room, prop, cell, wallRot, out_)) budget--;
            }
        }

        // Standing clear of the walls, and turned any quarter — a table has no back, so the
        // into-the-room rule the other two zones turn on says nothing here.
        private static void PlaceCentre(Room room, string style, PropRecipe recipe,
                                        int density, Random rng, List<PropPlacement> out_)
        {
            var groups = Resolve(style, recipe, RecipeCatalog.ZoneCentre);
            if (groups.Count == 0) return;

            var cells = room.Free.Where(c => !room.OnWall(c.Item1, c.Item2)).ToList();
            if (cells.Count == 0) return;
            Shuffle(cells, rng);

            int budget = (int)Math.Round(cells.Count * CentreFill[density]);
            foreach (var cell in cells)
            {
                if (budget <= 0) break;
                var prop = PickFrom(groups, room.Clearance, rng);
                if (prop == null) continue;
                int rot = rng.Next(4) * 6;
                if (TryPlace(room, prop, cell, Align.Centre, Align.Centre, rot, out_)) budget--;
            }
        }

        /// <summary>Free cells touching exactly one wall, in perimeter order, with that wall's rotation.</summary>
        // Perimeter order rather than shuffled, because the lighting pass steps through this list
        // at a fixed stride and wants consecutive entries to be adjacent cells.
        private static List<((int x, int z) cell, int wallRot)> WallAnchors(Room room)
        {
            var anchors = new List<((int, int), int)>();
            void Add(int x, int z, int rot)
            {
                if (room.Free.Contains((x, z))) anchors.Add(((x, z), rot));
            }

            for (int x = room.X0 + 1; x <= room.X1 - 1; x++) Add(x, room.Z0, ROT_NORTH);
            for (int z = room.Z0 + 1; z <= room.Z1 - 1; z++) Add(room.X1, z, ROT_EAST);
            for (int x = room.X1 - 1; x >= room.X0 + 1; x--) Add(x, room.Z1, ROT_SOUTH);
            for (int z = room.Z1 - 1; z >= room.Z0 + 1; z--) Add(room.X0, z, ROT_WEST);
            return anchors;
        }

        // ── Placement ────────────────────────────────────────────────────────

        /// <summary>Seat a prop against the wall (or corner) whose exterior faces `outward`.</summary>
        private static bool TryPlaceAt(Room room, PropEntry prop, (int x, int z) cell,
                                       int outward, List<PropPlacement> out_)
        {
            // The prop looks into the room, which is the opposite of the way the wall it backs
            // onto faces. Undoing the prop offset then cancels that half turn exactly, so a prop
            // ends up carrying its wall's rotation unchanged. The two halves are independent
            // facts and only cancel here, so they stay written out.
            int intoRoom = (outward + 12) % 24;
            int rot      = (intoRoom - PropFacingOffset + 24) % 24;

            // The block grows away from whichever walls the anchor touches, and the prop is flush
            // against those and centred across the rest.
            Align alignX = cell.x == room.X0 ? Align.Low
                         : cell.x == room.X1 ? Align.High : Align.Centre;
            Align alignZ = cell.z == room.Z0 ? Align.Low
                         : cell.z == room.Z1 ? Align.High : Align.Centre;

            return TryPlace(room, prop, cell, alignX, alignZ, rot, out_);
        }

        /// <summary>Claim the cells a prop needs and write it, or leave the room untouched.</summary>
        private static bool TryPlace(Room room, PropEntry prop, (int x, int z) anchor,
                                     Align alignX, Align alignZ, int rot, List<PropPlacement> out_)
        {
            var (fx, fz) = prop.RotatedFootprint(rot);
            int spanX = Cells(fx), spanZ = Cells(fz);

            // Cells are claimed whole: a prop 1.4 deep takes two of them and the leftover 0.6 is
            // floor nobody else may use.
            int minX = alignX == Align.High ? anchor.x - spanX + 1 : anchor.x;
            int minZ = alignZ == Align.High ? anchor.z - spanZ + 1 : anchor.z;

            var block = new List<(int, int)>(spanX * spanZ);
            for (int cx = minX; cx < minX + spanX; cx++)
                for (int cz = minZ; cz < minZ + spanZ; cz++)
                {
                    // `Free` holds only unreserved interior cells, so this is the bounds check too.
                    if (!room.Free.Contains((cx, cz))) return false;
                    block.Add((cx, cz));
                }

            foreach (var c in block) room.Free.Remove(c);
            if (room.Guard && !PortsConnected(room))
            {
                foreach (var c in block) room.Free.Add(c);
                return false;
            }

            float centreX = Seat(minX, spanX, fx, alignX);
            float centreZ = Seat(minZ, spanZ, fz, alignZ);
            var (px, pz) = prop.OriginForCentre(centreX, centreZ, rot);

            out_.Add(new PropPlacement
            {
                Guid = prop.GuidBytes,
                X = Q(px), Y = Q(room.Surface), Z = Q(pz), RotStep = rot,
            });
            return true;
        }

        /// <summary>Where the prop's centre lands on one axis, given its block and alignment.</summary>
        private static float Seat(int min, int span, float extent, Align align) =>
            align switch
            {
                Align.Low  => min + extent / 2f,
                Align.High => min + span - extent / 2f,
                _          => min + span / 2f,
            };

        // A prop is decoration; a prop that seals the only route between a room's two doors is a
        // broken map, and nothing downstream can move it afterwards. Doorway and stair cells are
        // already reserved, so the question is only whether furniture has cut the floor between
        // them in two.
        private static bool PortsConnected(Room room)
        {
            if (room.Ports.Count < 2) return true;

            var seen  = new HashSet<(int, int)>();
            var queue = new Queue<(int x, int z)>();
            seen.Add(room.Ports[0]);
            queue.Enqueue(room.Ports[0]);

            int found = 1;
            while (queue.Count > 0 && found < room.Ports.Count)
            {
                var (x, z) = queue.Dequeue();
                foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                {
                    if (nx < room.X0 || nx > room.X1 || nz < room.Z0 || nz > room.Z1) continue;
                    if (!seen.Add((nx, nz))) continue;
                    // A reserved cell is furniture-free floor, so it carries the route as well as
                    // a free cell does — it is only closed to *placing* in.
                    bool walkable = room.Free.Contains((nx, nz)) || room.Ports.Contains((nx, nz));
                    if (!walkable) continue;
                    if (room.Ports.Contains((nx, nz))) found++;
                    queue.Enqueue((nx, nz));
                }
            }
            return found == room.Ports.Count;
        }

        // ── Prop choice ──────────────────────────────────────────────────────

        /// <summary>The non-empty prop groups one zone of a recipe names.</summary>
        // A group that resolves to nothing is dropped rather than reported: recipes are authored
        // by hand against 162 pack-authored group tags, and naming one that does not exist should
        // cost the variety it would have added and nothing else. `MapGenQA recipes` is where that
        // is meant to be noticed.
        private static List<IReadOnlyList<PropEntry>> Resolve(string style, PropRecipe recipe, string zone) =>
            ResolveGroups(style, recipe == null
                ? Enumerable.Empty<string>()
                : (IEnumerable<string>)recipe.Zone(zone));

        private static List<IReadOnlyList<PropEntry>> ResolveGroups(string style, IEnumerable<string> names) =>
            names.Select(g => PropCatalog.Group(style, g))
                 .Where(list => list.Count > 0)
                 .ToList();

        /// <summary>One prop, from one of the groups, that stands on a floor and fits under the ceiling.</summary>
        // Redraws rather than returning nothing on a miss: a group mixes standing and hanging
        // props freely — `medieval/prison` holds both manacles and a rack — so a single draw from
        // an otherwise usable group fails often enough to leave rooms visibly under-dressed.
        private static PropEntry PickFrom(List<IReadOnlyList<PropEntry>> groups, float clearance, Random rng)
        {
            if (groups.Count == 0) return null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var pool = groups[rng.Next(groups.Count)];
                var prop = pool[rng.Next(pool.Count)];
                if (prop.IsFloorMounted && prop.Height <= clearance) return prop;
            }
            return null;
        }

        // ── Reserved cells ───────────────────────────────────────────────────

        /// <summary>Cells that must stay empty: doorways and their thresholds, and stair runs.</summary>
        // A prop is decoration, but one standing in a doorway or on a tread turns a working map
        // into a broken one, and nothing downstream can move it afterwards.
        private static HashSet<(int x, int z, int y100)> ReservedCells(LayoutSpec spec, string layoutTheme)
        {
            var reserved = new HashSet<(int, int, int)>();
            var roomById = new Dictionary<string, RoomSpec>();
            foreach (var r in spec.Rooms ?? Enumerable.Empty<RoomSpec>()) roomById[r.Id] = r;

            // Every join is stored as a pair of Connections, one owned by each room, so clearing
            // only the owner's side of each still reaches both ends of every corridor.
            foreach (var c in spec.Connections ?? Enumerable.Empty<Connection>())
            {
                if (!roomById.TryGetValue(c.FromRoomId ?? "", out var r)) continue;
                int y100 = (int)Math.Round(r.OriginY * 100);
                foreach (var (dx, dz) in Thresholds(r, c.WallSide, c.Offset))
                    reserved.Add((dx, dz, y100));
            }

            // The way in is not a Connection, so it would otherwise be the one doorway in the map a
            // wardrobe is allowed to stand against. Reserving it also makes it a *port*, which is
            // what the traversability guard measures a room's connectedness between.
            if (spec.Entrance != null &&
                roomById.TryGetValue(spec.Entrance.RoomId ?? "", out var entranceRoom))
            {
                int y100 = (int)Math.Round(entranceRoom.OriginY * 100);
                foreach (var (dx, dz) in Thresholds(entranceRoom, spec.Entrance.WallSide, spec.Entrance.Offset))
                    reserved.Add((dx, dz, y100));
            }

            foreach (var vc in spec.VerticalConnections ?? Enumerable.Empty<VerticalConnection>())
            {
                if (!roomById.TryGetValue(vc.LowerRoomId ?? "", out var lower)) continue;
                roomById.TryGetValue(vc.UpperRoomId ?? "", out var upper);

                string theme      = !string.IsNullOrEmpty(lower.Theme) ? lower.Theme : layoutTheme;
                string upperTheme = !string.IsNullOrEmpty(upper?.Theme) ? upper.Theme : layoutTheme;
                int    steps = TileCatalog.StairStepCount(
                    theme, lower.WallRows > 0 ? lower.WallRows : 1, upperTheme);

                (int dx, int dz) = DirToStep(vc.ClimbDirection);
                int lowY = (int)Math.Round(lower.OriginY * 100);
                int upY  = upper == null ? lowY : (int)Math.Round(upper.OriginY * 100);

                // The run itself, a landing cell at each end, and the same column in the storey
                // above, which is open floor around the stairwell rather than solid.
                for (int i = -1; i <= steps; i++)
                {
                    int cx = vc.StairOriginX + dx * i, cz = vc.StairOriginZ + dz * i;
                    reserved.Add((cx, cz, lowY));
                    reserved.Add((cx, cz, upY));
                }
            }

            return reserved;
        }

        /// <summary>The door cell and the interior cell in front of it, for one doorway of a room.</summary>
        private static IEnumerable<(int x, int z)> Thresholds(RoomSpec r, string side, int offset)
        {
            int w = Math.Max(r.Width, 3), d = Math.Max(r.Depth, 3);

            switch ((side ?? "").ToLowerInvariant())
            {
                case "north": yield return (r.OriginX + 1 + offset, r.OriginZ);
                              yield return (r.OriginX + 1 + offset, r.OriginZ + 1); break;
                case "south": yield return (r.OriginX + 1 + offset, r.OriginZ + d - 1);
                              yield return (r.OriginX + 1 + offset, r.OriginZ + d - 2); break;
                case "west":  yield return (r.OriginX,     r.OriginZ + 1 + offset);
                              yield return (r.OriginX + 1, r.OriginZ + 1 + offset); break;
                case "east":  yield return (r.OriginX + w - 1, r.OriginZ + 1 + offset);
                              yield return (r.OriginX + w - 2, r.OriginZ + 1 + offset); break;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>The 45° direction between two perpendicular wall facings.</summary>
        private static int Diagonal(int a, int b)
        {
            int diff = ((b - a) % 24 + 24) % 24;
            if (diff > 12) diff -= 24;               // take the short way round
            return ((a + diff / 2) % 24 + 24) % 24;
        }

        private static int Cells(float extent) => Math.Max(1, (int)Math.Ceiling(extent - 0.001f));

        private static (int dx, int dz) DirToStep(string dir) =>
            (dir ?? "").ToLowerInvariant() switch
            {
                "north" => (0, -1),
                "south" => (0, 1),
                "west"  => (-1, 0),
                _       => (1, 0),
            };

        private static void Shuffle<T>(List<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // Props snap to the centimetre — SnapPosToNearestHundredth — which is also the resolution
        // the slab format stores, so this is the whole of their grid.
        private static float Q(float v) => (float)Math.Round(v * 100.0) / 100f;
    }
}
