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

        // Grid and sizing — set per-generation by ApplySizePreset
        private int _gridW          = 64;
        private int _gridD          = 64;
        private int _minPartition   = 14;
        private int _minRoom        = 6;
        private int _maxRoom        = 18;
        private int _maxDepth       = 5;
        private int _maxHubCandidates = 2;
        private int _loopBudget     = 2;
        private const int Padding   = 1;  // gap between partition edge and room edge

        // A loop corridor longer than this reads as a second route to somewhere else rather than
        // a shortcut, so pairs further apart than this are not worth connecting.
        private const int MaxLoopGap = 14;

        // Fraction of what a partition could hold that a room must reach at minimum.
        private const float RoomFill = 0.7f;

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
            ApplySizePreset(p.DungeonSize);

            var rng   = new Random(p.Seed);
            string theme = string.IsNullOrEmpty(p.Theme) ? "Dungeon Cellar" : p.Theme;

            // Build BSP tree
            var root = new BspNode { X = 0, Z = 0, W = _gridW, D = _gridD };
            Split(root, rng, depth: 0);

            // Assign rooms to leaves
            var rooms = new List<RoomSpec>();
            int idx = 0;
            AssignRooms(root, theme, rng, rooms, ref idx);

            // Connect sibling pairs
            var connections = new List<Connection>();
            BuildConnections(root, connections, rng);

            string[] sizeNames = { "small", "medium", "large" };
            string sizeName = sizeNames[Math.Max(0, Math.Min(2, p.DungeonSize))];

            var spec = new LayoutSpec
            {
                Theme       = theme,
                Rooms       = rooms,
                Connections = connections,
                Notes       = $"BSP dungeon, seed={p.Seed}, size={sizeName}, rooms={rooms.Count}",
            };

            int minFloors = p.MinFloors > 0 ? p.MinFloors : 1;
            int maxFloors = p.MaxFloors > 0 ? p.MaxFloors : 2;
            ApplyMultiFloor(spec, rng, minFloors, maxFloors);
            AddLoopConnections(spec, rng);

            return spec;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Loop connections
        // ──────────────────────────────────────────────────────────────────────

        // BSP connects each subtree to its sibling exactly once, so the room graph is a spanning
        // tree: every room but one is a dead end and play degenerates into explore-then-backtrack.
        // This adds a few chords between rooms that are near in space but far apart in the graph,
        // which is what turns a branch into a circuit.
        //
        // Runs after ApplyMultiFloor on purpose. Multi-floor picks which rooms go upstairs by
        // looking for pendant rooms (exactly one connection), so adding chords first would starve
        // it of candidates and cost stairs.
        private void AddLoopConnections(LayoutSpec spec, Random rng)
        {
            if (_loopBudget <= 0 || spec.Rooms == null || spec.Rooms.Count < 4) return;

            for (int added = 0; added < _loopBudget; added++)
            {
                var dist = GraphDistances(spec);
                var best = LoopCandidates(spec)
                    .Where(c => Detour(dist, c.a.Id, c.b.Id) >= 4)   // below this it is a triangle
                    .OrderByDescending(c => Detour(dist, c.a.Id, c.b.Id))
                    .ThenBy(c => c.gap)
                    .FirstOrDefault();

                if (best.a == null) return;
                ConnectRooms(best.a, best.b, spec.Connections, rng);
            }
        }

        // How far apart two rooms currently are in the room graph. int.MaxValue/2 keeps the
        // arithmetic safe for rooms on separate components, which multi-floor can produce.
        private static int Detour(Dictionary<string, Dictionary<string, int>> dist, string a, string b)
        {
            if (dist.TryGetValue(a, out var from) && from.TryGetValue(b, out int d)) return d;
            return int.MaxValue / 2;
        }

        private static Dictionary<string, Dictionary<string, int>> GraphDistances(LayoutSpec spec)
        {
            var adj = new Dictionary<string, List<string>>();
            void Link(string x, string y)
            {
                if (!adj.TryGetValue(x, out var l)) adj[x] = l = new List<string>();
                if (!l.Contains(y)) l.Add(y);
            }
            foreach (var r in spec.Rooms) adj[r.Id] = new List<string>();
            foreach (var c in spec.Connections ?? new List<Connection>())
            { Link(c.FromRoomId, c.ToRoomId); Link(c.ToRoomId, c.FromRoomId); }
            foreach (var v in spec.VerticalConnections ?? new List<VerticalConnection>())
            { Link(v.LowerRoomId, v.UpperRoomId); Link(v.UpperRoomId, v.LowerRoomId); }

            var all = new Dictionary<string, Dictionary<string, int>>();
            foreach (var r in spec.Rooms)
            {
                var d = new Dictionary<string, int> { [r.Id] = 0 };
                var q = new Queue<string>();
                q.Enqueue(r.Id);
                while (q.Count > 0)
                {
                    string cur = q.Dequeue();
                    foreach (string nb in adj[cur])
                        if (!d.ContainsKey(nb)) { d[nb] = d[cur] + 1; q.Enqueue(nb); }
                }
                all[r.Id] = d;
            }
            return all;
        }

        // Pairs that could take a straight corridor between facing walls: same storey, walls that
        // overlap enough for a door, a short gap, and nothing in the way.
        private IEnumerable<(RoomSpec a, RoomSpec b, int gap)> LoopCandidates(LayoutSpec spec)
        {
            var linked = new HashSet<(string, string)>();
            foreach (var c in spec.Connections ?? new List<Connection>())
            { linked.Add((c.FromRoomId, c.ToRoomId)); linked.Add((c.ToRoomId, c.FromRoomId)); }

            var rooms = spec.Rooms;
            for (int i = 0; i < rooms.Count; i++)
            for (int j = i + 1; j < rooms.Count; j++)
            {
                var a = rooms[i];
                var b = rooms[j];
                if (a.OriginY != b.OriginY) continue;
                if (linked.Contains((a.Id, b.Id))) continue;

                // ConnectRooms needs three cells of wall to place a door away from the corners.
                int zOv = Overlap(a.OriginZ, a.Depth, b.OriginZ, b.Depth);
                int xOv = Overlap(a.OriginX, a.Width, b.OriginX, b.Width);

                if (zOv >= 3)
                {
                    var (l, r) = a.OriginX < b.OriginX ? (a, b) : (b, a);
                    int gap = r.OriginX - (l.OriginX + l.Width);
                    if (gap >= 1 && gap <= MaxLoopGap &&
                        !Blocked(spec, a, b, l.OriginX + l.Width, r.OriginX,
                                 Math.Max(a.OriginZ, b.OriginZ),
                                 Math.Min(a.OriginZ + a.Depth, b.OriginZ + b.Depth), a.OriginY))
                        yield return (a, b, gap);
                }
                else if (xOv >= 3)
                {
                    var (n, s) = a.OriginZ < b.OriginZ ? (a, b) : (b, a);
                    int gap = s.OriginZ - (n.OriginZ + n.Depth);
                    if (gap >= 1 && gap <= MaxLoopGap &&
                        !Blocked(spec, a, b, Math.Max(a.OriginX, b.OriginX),
                                 Math.Min(a.OriginX + a.Width, b.OriginX + b.Width),
                                 n.OriginZ + n.Depth, s.OriginZ, a.OriginY))
                        yield return (a, b, gap);
                }
            }
        }

        private static int Overlap(int aStart, int aLen, int bStart, int bLen) =>
            Math.Min(aStart + aLen, bStart + bLen) - Math.Max(aStart, bStart);

        // A corridor driven through a third room would breach its wall without a door, which the
        // enclosure checks treat — correctly — as a leak. Reject the pair instead.
        private static bool Blocked(LayoutSpec spec, RoomSpec a, RoomSpec b,
                                    int x0, int x1, int z0, int z1, float y)
        {
            foreach (var r in spec.Rooms)
            {
                if (r == a || r == b || r.OriginY != y) continue;
                // Grown by one so a corridor grazing a room's wall ring still counts as blocked.
                if (r.OriginX - 1 < x1 && r.OriginX + r.Width  + 1 > x0 &&
                    r.OriginZ - 1 < z1 && r.OriginZ + r.Depth  + 1 > z0) return true;
            }
            return false;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Size preset
        // ──────────────────────────────────────────────────────────────────────

        private void ApplySizePreset(int size)
        {
            switch (size)
            {
                case 0: // Small
                    _gridW = _gridD = 44; _minPartition = 12; _minRoom = 5;
                    _maxRoom = 14; _maxDepth = 4; _maxHubCandidates = 1; _loopBudget = 2; break;
                case 2: // Large
                    _gridW = _gridD = 88; _minPartition = 16; _minRoom = 6;
                    _maxRoom = 22; _maxDepth = 6; _maxHubCandidates = 3; _loopBudget = 5; break;
                default: // Medium
                    _gridW = _gridD = 64; _minPartition = 14; _minRoom = 6;
                    _maxRoom = 18; _maxDepth = 5; _maxHubCandidates = 2; _loopBudget = 4; break;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // BSP splitting
        // ──────────────────────────────────────────────────────────────────────

        private void Split(BspNode node, Random rng, int depth)
        {
            bool canX = node.W >= _minPartition * 2;
            bool canZ = node.D >= _minPartition * 2;

            if ((!canX && !canZ) || depth >= _maxDepth) return;

            // Prefer to split the longer dimension; randomise when equal
            bool splitX = canX && (!canZ || node.W >= node.D);

            if (splitX)
            {
                int lo = node.X + _minPartition;
                int hi = node.X + node.W - _minPartition;
                if (lo >= hi) return;

                int cut = rng.Next(lo, hi + 1);
                node.Left  = new BspNode { X = node.X, Z = node.Z, W = cut - node.X,          D = node.D };
                node.Right = new BspNode { X = cut,    Z = node.Z, W = node.X + node.W - cut, D = node.D };
            }
            else
            {
                int lo = node.Z + _minPartition;
                int hi = node.Z + node.D - _minPartition;
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
                int maxW = Math.Min(_maxRoom, node.W - Padding * 2);
                int maxD = Math.Min(_maxRoom, node.D - Padding * 2);

                // Drawing uniformly from _minRoom leaves rooms sitting around the middle of what
                // their partition can hold, so the exterior shell — which rings the whole bounding
                // box — ends up enclosing more bare board than room. Raising the floor of the draw
                // keeps the variation but stops rooms being small inside a large partition.
                int roomW = rng.Next(LowerBound(maxW), Math.Max(_minRoom, maxW) + 1);
                int roomD = rng.Next(LowerBound(maxD), Math.Max(_minRoom, maxD) + 1);

                int spaceX = Math.Max(0, node.W - Padding * 2 - roomW);
                int spaceZ = Math.Max(0, node.D - Padding * 2 - roomD);
                int roomX  = node.X + Padding + (spaceX > 0 ? rng.Next(0, spaceX + 1) : 0);
                int roomZ  = node.Z + Padding + (spaceZ > 0 ? rng.Next(0, spaceZ + 1) : 0);

                // Larger rooms randomly become tall (2-row) or grand-hall (3-row).
                int area = roomW * roomD;
                int wallRows = 1;
                if (area >= 196)          // ~14×14 and above
                {
                    double r = rng.NextDouble();
                    if      (r < 0.20) wallRows = 3;   // 20% grand hall
                    else if (r < 0.55) wallRows = 2;   // 35% tall room
                }
                else if (area >= 100)     // ~10×10 to ~14×14
                {
                    if (rng.NextDouble() < 0.30) wallRows = 2;
                }

                node.Room = new RoomSpec
                {
                    Id       = $"room_{idx++}",
                    Theme    = theme,
                    Width    = roomW,
                    Depth    = roomD,
                    OriginX  = roomX,
                    OriginZ  = roomZ,
                    OriginY  = 0,
                    WallRows = wallRows,
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

        // Smallest room dimension worth drawing for a partition that could hold `max`, never
        // below _minRoom and never above `max` itself.
        private int LowerBound(int max) =>
            Clamp((int)(max * RoomFill), _minRoom, Math.Max(_minRoom, max));

        // ──────────────────────────────────────────────────────────────────────
        // Multi-floor pass
        // ──────────────────────────────────────────────────────────────────────

        private void ApplyMultiFloor(LayoutSpec spec, Random rng, int minFloors, int maxFloors)
        {
            if (maxFloors < 2) return;

            string theme   = spec.Theme ?? "Dungeon Cellar";
            var    profile = ProfileCatalog.GetProfile(theme);
            if (profile == null) return;

            string stairType = profile.StairType ?? "";
            if (stairType != "stackable" && stairType != "mixed") return;

            // ── Phase 0: how tall the storey is, and how long that makes the stairs ──
            // The shortest stack that still clears MinCeilingHeight. Deriving it from the
            // tallest BSP room instead would raise the whole storey to suit one room, and the
            // straight stair run needed to reach it would no longer fit inside any of them.
            int upperWallRows = 1;
            while (upperWallRows * profile.WallHeight + profile.FloorHeight < profile.MinCeilingHeight
                   && upperWallRows < 8)
                upperWallRows++;

            // BuildStaircase climbs in 0.5 increments, one cell per step, starting one cell in
            // from the wall — so the room must be this long on the axis it climbs.
            int stairRun = (int)Math.Round(upperWallRows * profile.WallHeight / 0.5f);
            bool Fits(RoomSpec r) => Math.Max(r.Width, r.Depth) >= stairRun + 3;

            // ── Phase 1: choose which lower rooms will carry an upper room ──
            // Hubs sit above the largest rooms; landings sit above pendant rooms cut off from
            // the ground network. Both sets are decided before any geometry is fixed, because
            // every upper room has to end up on one shared storey (see Phase 2).
            // Candidate count scales with dungeon size via _maxHubCandidates.
            float avgArea = 0f;
            foreach (var r in spec.Rooms) avgArea += r.Width * r.Depth;
            avgArea /= spec.Rooms.Count;

            var candidates = new List<RoomSpec>();
            foreach (var r in spec.Rooms)
                if (r.Width * r.Depth >= avgArea && Fits(r))
                    candidates.Add(r);
            candidates.Sort((a, b) => (b.Width * b.Depth).CompareTo(a.Width * a.Depth));
            if (candidates.Count > _maxHubCandidates) candidates.RemoveRange(_maxHubCandidates, candidates.Count - _maxHubCandidates);
            if (candidates.Count == 0) return;

            var hubLowerIds = new HashSet<string>();
            foreach (var c in candidates) hubLowerIds.Add(c.Id);

            var pendants     = FindPendantRooms(spec, hubLowerIds).FindAll(Fits);
            int isolateCount = pendants.Count > 0 ? Math.Min(pendants.Count, rng.Next(1, 3)) : 0;
            var isolated     = pendants.GetRange(0, isolateCount);

            // ── Phase 2: put the whole upper storey on one elevation ──
            // Wall rows vary per room, so deriving each upper room's height from its own lower
            // room scatters them across several elevations. The horizontal corridors joining
            // them would then span storeys, and SlabBuilder would emit those corridors at the
            // lower of the two heights — leaving open-ended corridors hanging in mid-air.
            var carriers = new List<RoomSpec>(candidates);
            carriers.AddRange(isolated);
            foreach (var lower in carriers) lower.WallRows = upperWallRows;

            // A ground room taller than the storey gap pushes its top wall row up through the
            // upper floor, and the corridors that inherit its height do the same wherever they
            // run. Shorter rooms are left alone — they only fall short of the ceiling.
            foreach (var r in spec.Rooms)
                if (r.WallRows > upperWallRows) r.WallRows = upperWallRows;

            // Every BSP room sits at OriginY 0, so this one height serves the whole storey.
            float upperY = upperWallRows * profile.WallHeight + profile.FloorHeight;

            int upperIdx  = spec.Rooms.Count;
            var vertConns = new List<VerticalConnection>();
            var hubRooms  = new List<RoomSpec>();  // upper rooms above hub lower rooms

            foreach (var lower in candidates)
            {
                float scaleW = 0.6f + (float)rng.NextDouble() * 0.2f;
                float scaleD = 0.6f + (float)rng.NextDouble() * 0.2f;
                int   hubW   = Math.Max(_minRoom, (int)Math.Round(lower.Width  * scaleW));
                int   hubD   = Math.Max(_minRoom, (int)Math.Round(lower.Depth  * scaleD));
                int   hubX   = lower.OriginX + (lower.Width  - hubW) / 2;
                int   hubZ   = lower.OriginZ + (lower.Depth  - hubD) / 2;

                var hubRoom = new RoomSpec
                {
                    Id       = $"room_{upperIdx++}",
                    Theme    = lower.Theme,
                    Width    = hubW,
                    Depth    = hubD,
                    OriginX  = hubX,
                    OriginZ  = hubZ,
                    OriginY  = upperY,
                    WallRows = 1,
                };
                spec.Rooms.Add(hubRoom);
                hubRooms.Add(hubRoom);

                string climbDir = lower.Depth >= lower.Width
                    ? (rng.Next(2) == 0 ? "north" : "south")
                    : (rng.Next(2) == 0 ? "east"  : "west");
                (int sx, int sz) = StairOriginInRoom(lower, climbDir);

                vertConns.Add(new VerticalConnection
                {
                    LowerRoomId    = lower.Id,
                    UpperRoomId    = hubRoom.Id,
                    StairOriginX   = sx,
                    StairOriginZ   = sz,
                    ClimbDirection = climbDir,
                });
            }

            // ── Phase 2: Upper-floor corridors between hub rooms ──
            // MinFloors >= 2 forces connection; otherwise 70% chance.
            double hubConnectChance = minFloors >= 2 ? 1.0 : 0.70;
            if (hubRooms.Count >= 2 && rng.NextDouble() < hubConnectChance)
                ConnectRooms(hubRooms[0], hubRooms[1], spec.Connections, rng);

            // ── Phase 4: Isolated lower rooms (only reachable via upper floor) ──
            // The pendant rooms picked in Phase 1 are cut off from the ground network. Each
            // gets a landing room above it connected back to a hub, making the only path:
            // main floor → hub stairs → upper corridor → landing → stairs down.
            foreach (var lower in isolated)
            {
                // Remove all horizontal connections to this room.
                spec.Connections.RemoveAll(c => c.FromRoomId == lower.Id || c.ToRoomId == lower.Id);

                // Landing room centred above the isolated lower room (50–70% of its footprint).
                float sW      = 0.5f + (float)rng.NextDouble() * 0.2f;
                float sD      = 0.5f + (float)rng.NextDouble() * 0.2f;
                int   landW   = Math.Max(_minRoom, (int)Math.Round(lower.Width  * sW));
                int   landD   = Math.Max(_minRoom, (int)Math.Round(lower.Depth  * sD));
                int   landX   = lower.OriginX + (lower.Width  - landW) / 2;
                int   landZ   = lower.OriginZ + (lower.Depth  - landD) / 2;

                var landing = new RoomSpec
                {
                    Id       = $"room_{upperIdx++}",
                    Theme    = lower.Theme,
                    Width    = landW,
                    Depth    = landD,
                    OriginX  = landX,
                    OriginZ  = landZ,
                    OriginY  = upperY,
                    WallRows = 1,
                };
                spec.Rooms.Add(landing);

                string climbDir = lower.Depth >= lower.Width
                    ? (rng.Next(2) == 0 ? "north" : "south")
                    : (rng.Next(2) == 0 ? "east"  : "west");
                (int sx, int sz) = StairOriginInRoom(lower, climbDir);

                vertConns.Add(new VerticalConnection
                {
                    LowerRoomId    = lower.Id,
                    UpperRoomId    = landing.Id,
                    StairOriginX   = sx,
                    StairOriginZ   = sz,
                    ClimbDirection = climbDir,
                });

                // Connect the landing (dead-end upper room) to the nearest hub room via
                // an upper-floor corridor.  This is the only horizontal connection it gets.
                var nearestHub = hubRooms[0];
                float bestDist = RoomDistSq(landing, hubRooms[0]);
                for (int h = 1; h < hubRooms.Count; h++)
                {
                    float d = RoomDistSq(landing, hubRooms[h]);
                    if (d < bestDist) { bestDist = d; nearestHub = hubRooms[h]; }
                }
                ConnectRooms(landing, nearestHub, spec.Connections, rng);
            }

            spec.VerticalConnections = vertConns;
        }

        // Returns rooms with exactly one distinct horizontal neighbour (pendant nodes).
        // Excludes rooms already used as hub lower rooms.  Sorted smallest-first so the
        // most tucked-away rooms become the hidden ones.
        private List<RoomSpec> FindPendantRooms(LayoutSpec spec, HashSet<string> excludeIds)
        {
            var neighbors = new Dictionary<string, HashSet<string>>();
            foreach (var conn in spec.Connections)
            {
                if (!neighbors.ContainsKey(conn.FromRoomId))
                    neighbors[conn.FromRoomId] = new HashSet<string>();
                neighbors[conn.FromRoomId].Add(conn.ToRoomId);
            }

            var result = new List<RoomSpec>();
            foreach (var room in spec.Rooms)
            {
                if (excludeIds.Contains(room.Id)) continue;
                if (!neighbors.TryGetValue(room.Id, out var nb)) continue;
                if (nb.Count == 1) result.Add(room);
            }
            result.Sort((a, b) => (a.Width * a.Depth).CompareTo(b.Width * b.Depth));
            return result;
        }

        // Returns the stair origin inside a room for a given climb direction.
        // The origin is placed on the interior edge opposite the climb direction so the
        // stair chain runs toward the wall and doesn't start outside the room.
        private static (int x, int z) StairOriginInRoom(RoomSpec room, string climbDir)
        {
            switch (climbDir?.ToLowerInvariant())
            {
                case "north": return (room.OriginX + room.Width / 2, room.OriginZ + room.Depth - 2);
                case "south": return (room.OriginX + room.Width / 2, room.OriginZ + 1);
                case "east":  return (room.OriginX + 1,              room.OriginZ + room.Depth / 2);
                default:      return (room.OriginX + room.Width - 2, room.OriginZ + room.Depth / 2);
            }
        }

        private static float RoomDistSq(RoomSpec a, RoomSpec b)
        {
            float dx = (a.OriginX + a.Width  / 2f) - (b.OriginX + b.Width  / 2f);
            float dz = (a.OriginZ + a.Depth  / 2f) - (b.OriginZ + b.Depth  / 2f);
            return dx * dx + dz * dz;
        }
    }
}
