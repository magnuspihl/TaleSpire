using System;
using System.Collections.Generic;
using System.Linq;

namespace TaleSpireMapGen.Generation
{
    /// <summary>
    /// BSP dungeon generator.
    /// Recursively partitions a 64×64 tile grid into leaf cells, places one room
    /// per leaf, then connects sibling rooms with paired Connection entries.
    /// The SlabBuilder turns the resulting LayoutSpec into actual tile geometry
    /// including L-shaped corridor floors between connected rooms.
    /// </summary>
    public class DungeonTemplate : ITemplate
    {
        public string Name        => "Dungeon";
        public string Description => "BSP algorithmic dungeon — multiple rooms with corridors";

        // Grid and sizing constants
        private const int GridW        = 64;
        private const int GridD        = 64;
        private const int MinPartition = 14;  // smallest partition allowed to split further
        private const int MinRoom      = 6;   // minimum room dimension in tiles
        private const int MaxRoom      = 18;  // maximum room dimension in tiles
        private const int Padding      = 2;   // gap between partition edge and room edge

        // ──────────────────────────────────────────────────────────────────────
        // BSP node
        // ──────────────────────────────────────────────────────────────────────

        private class BspNode
        {
            public int X, Z, W, D;       // partition bounds (world tile coords)
            public BspNode Left, Right;  // children (null = leaf)
            public RoomSpec Room;        // set only on leaf
        }

        // ──────────────────────────────────────────────────────────────────────
        // ITemplate
        // ──────────────────────────────────────────────────────────────────────

        public LayoutSpec Generate(TemplateParams p)
        {
            var rng   = new Random(p.Seed);
            string theme = string.IsNullOrEmpty(p.Theme) ? "Dungeon Cellar" : p.Theme;

            // Build BSP tree
            var root = new BspNode { X = 0, Z = 0, W = GridW, D = GridD };
            Split(root, rng, depth: 0);

            // Assign rooms to leaves
            var rooms = new List<RoomSpec>();
            int idx = 0;
            AssignRooms(root, theme, rng, rooms, ref idx);

            // Connect sibling pairs
            var connections = new List<Connection>();
            BuildConnections(root, connections, rng);

            return new LayoutSpec
            {
                Theme       = theme,
                Rooms       = rooms,
                Connections = connections,
                Notes       = $"BSP dungeon, seed={p.Seed}, rooms={rooms.Count}",
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // BSP splitting
        // ──────────────────────────────────────────────────────────────────────

        private void Split(BspNode node, Random rng, int depth)
        {
            bool canX = node.W >= MinPartition * 2;
            bool canZ = node.D >= MinPartition * 2;

            if ((!canX && !canZ) || depth >= 5) return;

            // Prefer to split the longer dimension; randomise when equal
            bool splitX = canX && (!canZ || node.W >= node.D);

            if (splitX)
            {
                int lo = node.X + MinPartition;
                int hi = node.X + node.W - MinPartition;
                if (lo >= hi) return;

                int cut = rng.Next(lo, hi + 1);
                node.Left  = new BspNode { X = node.X, Z = node.Z, W = cut - node.X,          D = node.D };
                node.Right = new BspNode { X = cut,    Z = node.Z, W = node.X + node.W - cut, D = node.D };
            }
            else
            {
                int lo = node.Z + MinPartition;
                int hi = node.Z + node.D - MinPartition;
                if (lo >= hi) return;

                int cut = rng.Next(lo, hi + 1);
                node.Left  = new BspNode { X = node.X, Z = node.Z, W = node.W, D = cut - node.Z          };
                node.Right = new BspNode { X = node.X, Z = cut,    W = node.W, D = node.Z + node.D - cut };
            }

            Split(node.Left,  rng, depth + 1);
            Split(node.Right, rng, depth + 1);
        }

        // ──────────────────────────────────────────────────────────────────────
        // Room assignment
        // ──────────────────────────────────────────────────────────────────────

        private void AssignRooms(BspNode node, string theme, Random rng,
                                  List<RoomSpec> rooms, ref int idx)
        {
            if (node.Left == null) // leaf
            {
                int maxW = Math.Min(MaxRoom, node.W - Padding * 2);
                int maxD = Math.Min(MaxRoom, node.D - Padding * 2);
                int roomW = rng.Next(MinRoom, Math.Max(MinRoom, maxW) + 1);
                int roomD = rng.Next(MinRoom, Math.Max(MinRoom, maxD) + 1);

                int spaceX = Math.Max(0, node.W - Padding * 2 - roomW);
                int spaceZ = Math.Max(0, node.D - Padding * 2 - roomD);
                int roomX  = node.X + Padding + (spaceX > 0 ? rng.Next(0, spaceX + 1) : 0);
                int roomZ  = node.Z + Padding + (spaceZ > 0 ? rng.Next(0, spaceZ + 1) : 0);

                node.Room = new RoomSpec
                {
                    Id      = $"room_{idx++}",
                    Theme   = theme,
                    Width   = roomW,
                    Depth   = roomD,
                    OriginX = roomX,
                    OriginZ = roomZ,
                    OriginY = 0,
                };
                rooms.Add(node.Room);
            }
            else
            {
                AssignRooms(node.Left,  theme, rng, rooms, ref idx);
                AssignRooms(node.Right, theme, rng, rooms, ref idx);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Connection generation
        // ──────────────────────────────────────────────────────────────────────

        private void BuildConnections(BspNode node, List<Connection> conns, Random rng)
        {
            if (node.Left == null) return;

            BuildConnections(node.Left,  conns, rng);
            BuildConnections(node.Right, conns, rng);

            // Connect the room from each subtree that is closest to the other subtree
            var roomA = ClosestRoom(node.Left,  node.Right);
            var roomB = ClosestRoom(node.Right, node.Left);
            if (roomA == null || roomB == null) return;

            ConnectRooms(roomA, roomB, conns, rng);
        }

        private RoomSpec ClosestRoom(BspNode fromNode, BspNode toNode)
        {
            var all = new List<RoomSpec>();
            CollectRooms(fromNode, all);
            if (all.Count == 0) return null;

            float toCX = toNode.X + toNode.W / 2f;
            float toCZ = toNode.Z + toNode.D / 2f;

            return all.OrderBy(r =>
            {
                float dx = (r.OriginX + r.Width  / 2f) - toCX;
                float dz = (r.OriginZ + r.Depth  / 2f) - toCZ;
                return dx * dx + dz * dz;
            }).First();
        }

        private void CollectRooms(BspNode node, List<RoomSpec> out_)
        {
            if (node.Left == null) { if (node.Room != null) out_.Add(node.Room); }
            else { CollectRooms(node.Left, out_); CollectRooms(node.Right, out_); }
        }

        private void ConnectRooms(RoomSpec a, RoomSpec b, List<Connection> conns, Random rng)
        {
            float aCX = a.OriginX + a.Width  / 2f;
            float aCZ = a.OriginZ + a.Depth  / 2f;
            float bCX = b.OriginX + b.Width  / 2f;
            float bCZ = b.OriginZ + b.Depth  / 2f;

            float dx = bCX - aCX;
            float dz = bCZ - aCZ;

            string wallA, wallB;
            int offA, offB;

            if (Math.Abs(dx) >= Math.Abs(dz))
            {
                wallA = dx > 0 ? "east"  : "west";
                wallB = dx > 0 ? "west"  : "east";

                // Pick a z that overlaps both walls if possible
                int aLo = a.OriginZ + 1, aHi = a.OriginZ + a.Depth - 2;
                int bLo = b.OriginZ + 1, bHi = b.OriginZ + b.Depth - 2;
                int lo  = Math.Max(aLo, bLo);
                int hi  = Math.Min(aHi, bHi);

                int doorZ = lo <= hi
                    ? rng.Next(lo, hi + 1)
                    : (aLo + aHi) / 2;

                offA = Clamp(doorZ - (a.OriginZ + 1), 0, a.Depth - 3);
                int bDoorZ = Clamp(doorZ, bLo, bHi);
                offB = Clamp(bDoorZ - (b.OriginZ + 1), 0, b.Depth - 3);
            }
            else
            {
                wallA = dz > 0 ? "south" : "north";
                wallB = dz > 0 ? "north" : "south";

                int aLo = a.OriginX + 1, aHi = a.OriginX + a.Width - 2;
                int bLo = b.OriginX + 1, bHi = b.OriginX + b.Width - 2;
                int lo  = Math.Max(aLo, bLo);
                int hi  = Math.Min(aHi, bHi);

                int doorX = lo <= hi
                    ? rng.Next(lo, hi + 1)
                    : (aLo + aHi) / 2;

                offA = Clamp(doorX - (a.OriginX + 1), 0, a.Width - 3);
                int bDoorX = Clamp(doorX, bLo, bHi);
                offB = Clamp(bDoorX - (b.OriginX + 1), 0, b.Width - 3);
            }

            conns.Add(new Connection { FromRoomId = a.Id, ToRoomId = b.Id, WallSide = wallA, Offset = offA });
            conns.Add(new Connection { FromRoomId = b.Id, ToRoomId = a.Id, WallSide = wallB, Offset = offB });
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
