using System;
using System.Collections.Generic;
using System.Linq;

namespace TaleSpireMapGen.Generation
{
    /// <summary>
    /// Decides what each room is for. Reads the finished layout and writes only RoomSpec.Purpose.
    /// </summary>
    // Purpose is assigned from the shape of the map rather than drawn at random per room, because
    // the things that make a purpose read as deliberate are all relational: a treasury is the
    // dead-end furthest from the entrance, a guardroom is the room you pass through first, a hall
    // is the biggest and best connected. None of that is knowable while the BSP is still splitting,
    // which is why this runs last — after ApplyMultiFloor has moved rooms upstairs and
    // AddLoopConnections has settled the degrees.
    public static class PurposeAssigner
    {
        // How much a purpose's weight is cut each time it is used. Without damping the filler draw
        // hands a 9-room dungeon five storerooms, because "storeroom fits anywhere" and "storeroom
        // is what this room should be" are the same number to a weighted pick.
        private const float RepeatDamping = 0.45f;

        // Floor under a scored weight, so a purpose the map suits badly is unlikely rather than
        // impossible — otherwise a map whose rooms all score negatively for everything but Empty
        // comes out entirely empty.
        private const float MinScoredWeight = 0.05f;

        private class Features
        {
            public RoomSpec Room;
            public int Area, Degree, Depth;
            public bool Upper;
            public string Style;
            public float AreaN, DegreeN, DepthN;   // -1..1 against this map's own spread
        }

        public static void Assign(LayoutSpec spec, Dictionary<string, int> quota, Random rng)
        {
            if (spec?.Rooms == null || spec.Rooms.Count == 0) return;

            spec.PurposeQuota = quota != null && quota.Count > 0
                ? quota.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value)
                : null;
            spec.PurposeShortfall = null;

            if (!RecipeCatalog.IsLoaded)
            {
                foreach (var r in spec.Rooms) r.Purpose = RecipeCatalog.Empty;
                return;
            }

            var feats = Measure(spec);
            var unassigned = new List<Features>(feats);

            var shortfall = new Dictionary<string, int>();
            foreach (var (purpose, want) in QuotaOrder(spec.PurposeQuota, feats))
            {
                int placed = 0;
                foreach (var f in unassigned
                             .Where(f => Eligible(f, purpose))
                             .OrderByDescending(f => Fit(f, purpose))
                             .ThenBy(f => f.Room.Id, StringComparer.Ordinal)
                             .Take(want)
                             .ToList())
                {
                    f.Room.Purpose = purpose;
                    unassigned.Remove(f);
                    placed++;
                }
                if (placed < want) shortfall[purpose] = want - placed;
            }
            if (shortfall.Count > 0) spec.PurposeShortfall = shortfall;

            // Filler. Rooms in a shuffled order so the damping does not always spend a purpose's
            // full weight on whichever room happens to be room_0.
            var damping = new Dictionary<string, float>();
            foreach (var f in unassigned.OrderBy(_ => rng.Next()))
                f.Room.Purpose = DrawFiller(f, spec.PurposeQuota, damping, rng);
        }

        // ── Features ─────────────────────────────────────────────────────────

        private static List<Features> Measure(LayoutSpec spec)
        {
            var degree = Degrees(spec);
            var depth  = DepthsFromEntrance(spec);

            var feats = spec.Rooms.Select(r => new Features
            {
                Room   = r,
                Area   = Math.Max(r.Width, 3) * Math.Max(r.Depth, 3),
                Degree = degree.TryGetValue(r.Id ?? "", out int d) ? d : 0,
                Depth  = depth.TryGetValue(r.Id ?? "", out int p) ? p : 0,
                Upper  = r.OriginY > 0.001f,
                Style  = RecipeCatalog.StyleFor(string.IsNullOrEmpty(r.Theme) ? spec.Theme : r.Theme),
            }).ToList();

            // Scored against this map rather than against absolute numbers: "the deepest room" has
            // to mean something on a 6-room small map as well as a 30-room large one.
            Normalise(feats, f => f.Area,   (f, v) => f.AreaN   = v);
            Normalise(feats, f => f.Degree, (f, v) => f.DegreeN = v);
            Normalise(feats, f => f.Depth,  (f, v) => f.DepthN  = v);
            return feats;
        }

        private static void Normalise(List<Features> feats, Func<Features, int> read,
                                      Action<Features, float> write)
        {
            int lo = feats.Min(read), hi = feats.Max(read);
            foreach (var f in feats)
                write(f, hi == lo ? 0f : 2f * (read(f) - lo) / (hi - lo) - 1f);
        }

        private static Dictionary<string, int> Degrees(LayoutSpec spec)
        {
            var nb = new Dictionary<string, HashSet<string>>();
            void Link(string a, string b)
            {
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;
                if (!nb.TryGetValue(a, out var set)) nb[a] = set = new HashSet<string>();
                set.Add(b);
            }
            foreach (var c in spec.Connections ?? Enumerable.Empty<Connection>())
            { Link(c.FromRoomId, c.ToRoomId); Link(c.ToRoomId, c.FromRoomId); }
            foreach (var v in spec.VerticalConnections ?? Enumerable.Empty<VerticalConnection>())
            { Link(v.LowerRoomId, v.UpperRoomId); Link(v.UpperRoomId, v.LowerRoomId); }

            return spec.Rooms.ToDictionary(
                r => r.Id ?? "", r => nb.TryGetValue(r.Id ?? "", out var set) ? set.Count : 0);
        }

        /// <summary>Rooms in graph steps from the entrance, so "deep" means deep to walk to.</summary>
        private static Dictionary<string, int> DepthsFromEntrance(LayoutSpec spec)
        {
            var depths = new Dictionary<string, int>();
            string entranceId = EntranceRoomId(spec);
            if (entranceId == null) return depths;

            var adj = new Dictionary<string, List<string>>();
            void Link(string a, string b)
            {
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;
                if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<string>();
                if (!l.Contains(b)) l.Add(b);
            }
            foreach (var c in spec.Connections ?? Enumerable.Empty<Connection>())
            { Link(c.FromRoomId, c.ToRoomId); Link(c.ToRoomId, c.FromRoomId); }
            foreach (var v in spec.VerticalConnections ?? Enumerable.Empty<VerticalConnection>())
            { Link(v.LowerRoomId, v.UpperRoomId); Link(v.UpperRoomId, v.LowerRoomId); }

            depths[entranceId] = 0;
            var q = new Queue<string>();
            q.Enqueue(entranceId);
            while (q.Count > 0)
            {
                string cur = q.Dequeue();
                if (!adj.TryGetValue(cur, out var list)) continue;
                foreach (string next in list)
                    if (!depths.ContainsKey(next)) { depths[next] = depths[cur] + 1; q.Enqueue(next); }
            }

            // A room the entrance cannot reach is as deep as the map goes, not depth 0 — that is
            // exactly the pendant behind the upstairs route, which should read as remote.
            int max = depths.Count > 0 ? depths.Values.Max() : 0;
            foreach (var r in spec.Rooms)
                if (!depths.ContainsKey(r.Id ?? "")) depths[r.Id ?? ""] = max + 1;
            return depths;
        }

        // The door EntrancePlacer cut, when there is one. The fallback — the ground room nearest the
        // edge of the map's bounding box — is not merely defensive: a spec assembled by hand or by
        // a future template may carry no entrance, and depth is still the better ordering than none.
        private static string EntranceRoomId(LayoutSpec spec)
        {
            if (!string.IsNullOrEmpty(spec.Entrance?.RoomId)) return spec.Entrance.RoomId;

            var ground = spec.Rooms.Where(r => r.OriginY < 0.001f).ToList();
            if (ground.Count == 0) return null;

            int minX = ground.Min(r => r.OriginX), minZ = ground.Min(r => r.OriginZ);
            int maxX = ground.Max(r => r.OriginX + Math.Max(r.Width, 3));
            int maxZ = ground.Max(r => r.OriginZ + Math.Max(r.Depth, 3));

            return ground
                .OrderBy(r => Math.Min(
                    Math.Min(r.OriginX - minX, maxX - (r.OriginX + Math.Max(r.Width, 3))),
                    Math.Min(r.OriginZ - minZ, maxZ - (r.OriginZ + Math.Max(r.Depth, 3)))))
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .First().Id;
        }

        // ── Scoring ──────────────────────────────────────────────────────────

        private static bool Eligible(Features f, string purpose)
        {
            var recipe = RecipeCatalog.Get(f.Style, purpose);
            return recipe != null && recipe.Fits(f.Area, f.Upper);
        }

        private static float Fit(Features f, string purpose)
        {
            var recipe = RecipeCatalog.Get(f.Style, purpose);
            return recipe == null ? 0f : Fit(f, recipe);
        }

        private static float Fit(Features f, PropRecipe recipe) =>
            recipe.WantsArea * f.AreaN + recipe.WantsDegree * f.DegreeN + recipe.WantsDepth * f.DepthN;

        // Tightest quota first: a purpose only a couple of rooms can take would otherwise find its
        // rooms already spent on a purpose that had the whole map to choose from.
        private static IEnumerable<(string purpose, int want)> QuotaOrder(
            Dictionary<string, int> quota, List<Features> feats)
        {
            if (quota == null) yield break;
            foreach (var kv in quota
                         .OrderBy(kv => feats.Count(f => Eligible(f, kv.Key)))
                         .ThenByDescending(kv => kv.Value)
                         .ThenBy(kv => kv.Key, StringComparer.Ordinal))
                yield return (kv.Key, kv.Value);
        }

        // A quota is exact rather than a floor: a purpose the caller named is kept out of the filler
        // draw, so "one treasury" means one and the shortfall figure stays meaningful. Letting the
        // filler top it up would make "1 requested, 3 assigned, 0 short" a normal reading.
        private static string DrawFiller(Features f, Dictionary<string, int> quota,
                                         Dictionary<string, float> damping, Random rng)
        {
            var pool = new List<(string purpose, float weight)>();
            foreach (var recipe in RecipeCatalog.Purposes(f.Style))
            {
                if (quota != null && quota.ContainsKey(recipe.Name)) continue;
                if (!recipe.Fits(f.Area, f.Upper)) continue;
                float damp  = damping.TryGetValue(recipe.Name, out float d) ? d : 1f;
                float score = Math.Max(MinScoredWeight, 1f + Fit(f, recipe));
                pool.Add((recipe.Name, recipe.Weight * damp * score));
            }
            if (pool.Count == 0) return RecipeCatalog.Empty;

            float total = pool.Sum(p => p.weight);
            double roll = rng.NextDouble() * total;
            foreach (var (purpose, weight) in pool)
            {
                roll -= weight;
                if (roll > 0) continue;
                damping[purpose] = (damping.TryGetValue(purpose, out float d) ? d : 1f) * RepeatDamping;
                return purpose;
            }
            return pool[pool.Count - 1].purpose;
        }
    }
}
