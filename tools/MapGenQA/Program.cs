using System;
using System.Collections.Generic;
using System.Linq;
using TaleSpireMapGen.Generation;

namespace MapGenQA
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0) { Usage(); return 2; }

            var opt = ParseOptions(args.Skip(1));
            try
            {
                return args[0] switch
                {
                    "fuzz"   => Fuzz(opt),
                    "render" => Render(opt),
                    "slab"   => Slab(opt),
                    "rooms"  => Rooms(opt),
                    "spec"   => Spec(opt),
                    "cell"   => Cell(opt),
                    "tiles"  => Tiles(opt),
                    _        => Unknown(args[0]),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 2;
            }
        }

        private static int Unknown(string cmd)
        {
            Console.Error.WriteLine($"unknown command '{cmd}'");
            Usage();
            return 2;
        }

        private static void Usage() => Console.Error.WriteLine("""
            MapGenQA — headless QA for the MapGen generator

              fuzz   [--seeds 1-200] [--sizes small,medium,large] [--themes all] [--examples 3]
                     [--mixed]
                     Generate every combination and run all validators. Exit 1 if any fail.
                     --mixed gives each map a different theme upstairs than downstairs.

              render [--seed 42] [--size medium] [--theme "Dungeon Cellar"] [--upper "Castle Fortified"]
                     Print ASCII floor plans for one map.

              slab   [--seed 42] [--size medium] [--theme "Dungeon Cellar"] [--upper "Castle Fortified"]
                     Print the base64 slab for one map, ready to paste into TaleSpire.

              rooms  --themes "A,B,C" [--w 5] [--d 5] [--rows 2] [--doors]
                     Print one base64 slab holding one plain room per theme, side by side, for
                     comparing tile picks in game. --doors joins neighbouring rooms so each one
                     gets a door, which is the only way the door role becomes visible.

              spec   [--seed 42] [--size medium] [--theme "Dungeon Cellar"]
                     Print the room / connection / stair spec behind one map.

              cell   [--seed 42] [--size medium] --x 54 --z 6 [--radius 1]
                     Print every tile near one cell, and which room footprint covers it.

              tiles  [--themes all]
                     Print what each role resolves to per theme, with the heights every
                     elevation in the builder is derived from.
            """);

        // ── options ──────────────────────────────────────────────────────────

        private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
        {
            var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string key = null;
            foreach (var a in args)
            {
                if (a.StartsWith("--")) { key = a[2..]; o[key] = "true"; }
                else if (key != null) { o[key] = a; key = null; }
            }
            return o;
        }

        private static string Get(Dictionary<string, string> o, string k, string dflt) =>
            o.TryGetValue(k, out var v) && v != "true" ? v : dflt;

        private static IEnumerable<int> ParseSeeds(string s)
        {
            foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var r = part.Split('-', 2);
                if (r.Length == 2 && int.TryParse(r[0], out int a) && int.TryParse(r[1], out int b))
                    for (int i = a; i <= b; i++) yield return i;
                else if (int.TryParse(part, out int one)) yield return one;
            }
        }

        private static int ParseSize(string s) => s.ToLowerInvariant() switch
        {
            "small" => 0, "medium" => 1, "large" => 2,
            _ => int.TryParse(s, out int v) ? v : 1,
        };

        private static string[] ParseThemes(string s) =>
            s.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? TileCatalog.KnownThemes
                : s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();

        // ── commands ─────────────────────────────────────────────────────────

        // An empty profile catalog does not fail any validator — it makes the generator fall back
        // to hardcoded defaults and skip multi-floor entirely, so every stair check passes because
        // there are no stairs. That is exactly how the profile-loading bug survived, so check the
        // catalog is actually populated before trusting a green run.
        private static bool Preflight()
        {
            var missing = ParseThemes("all").Where(t => ProfileCatalog.GetProfile(t) == null).ToList();
            if (missing.Count == 0) return true;

            Console.Error.WriteLine($"PREFLIGHT FAILED: no tileset profile for {missing.Count} theme(s): " +
                                    string.Join(", ", missing));
            Console.Error.WriteLine("The catalog is empty or incomplete, so multi-floor checks would " +
                                    "pass vacuously. Refusing to report a green run.");
            return false;
        }

        private static int Fuzz(Dictionary<string, string> o)
        {
            var seeds  = ParseSeeds(Get(o, "seeds", "1-200")).ToList();
            var sizes  = Get(o, "sizes", "small,medium,large").Split(',').Select(ParseSize).ToList();
            var themes = ParseThemes(Get(o, "themes", "all"));
            int maxEx  = int.TryParse(Get(o, "examples", "3"), out var m) ? m : 3;

            // --mixed pairs every theme with a *different* one upstairs, which is the case a
            // uniform sweep cannot reach: a castle keep over a dungeon basement puts two wall
            // heights, two stair pitches and two door sets in one map.
            bool mixed = o.ContainsKey("mixed");

            if (!Preflight()) return 1;

            Console.WriteLine($"fuzzing {seeds.Count} seeds x {sizes.Count} sizes x {themes.Length} themes " +
                              $"= {seeds.Count * sizes.Count * themes.Length} maps"
                              + (mixed ? " (mixed ground/upper themes)" : ""));

            var failures = Validators.All.ToDictionary(v => v.Name, _ => new List<string>());
            int generated = 0, crashed = 0;

            foreach (int seed in seeds)
            foreach (int size in sizes)
            for (int ti = 0; ti < themes.Length; ti++)
            {
                string theme = themes[ti];
                // Offset by the seed so a given theme meets every other one across a run, rather
                // than always being paired with its neighbour in the list.
                string upper = mixed && themes.Length > 1
                    ? themes[(ti + 1 + seed % (themes.Length - 1)) % themes.Length]
                    : null;

                GeneratedMap map;
                try { map = GeneratedMap.Generate(seed, size, theme, upper); generated++; }
                catch (Exception ex)
                {
                    crashed++;
                    failures["DuplicateTile"].Add($"seed={seed} size={size} theme='{theme}' upper='{upper}' GENERATOR THREW: {ex.Message}");
                    continue;
                }

                foreach (var v in Validators.All)
                {
                    foreach (string f in v.Check(map).Take(maxEx))
                        failures[v.Name].Add($"{map.Label}: {f}");
                }
            }

            Console.WriteLine($"generated {generated} maps ({crashed} generator crashes)");
            Console.WriteLine();

            int failingValidators = 0;
            foreach (var v in Validators.All)
            {
                var list = failures[v.Name];
                if (list.Count == 0)
                {
                    Console.WriteLine($"  PASS  {v.Name}");
                    continue;
                }
                failingValidators++;
                Console.WriteLine($"  FAIL  {v.Name} — {v.Describes}  ({list.Count} findings)");
                foreach (string f in list.Take(maxEx)) Console.WriteLine($"          {f}");
                if (list.Count > maxEx) Console.WriteLine($"          ... and {list.Count - maxEx} more");
            }

            Console.WriteLine();
            Console.WriteLine(failingValidators == 0
                ? "all validators passed"
                : $"{failingValidators} of {Validators.All.Length} validators failed");
            return failingValidators == 0 ? 0 : 1;
        }

        private static GeneratedMap One(Dictionary<string, string> o) =>
            GeneratedMap.Generate(
                int.TryParse(Get(o, "seed", "42"), out var s) ? s : 42,
                ParseSize(Get(o, "size", "medium")),
                Get(o, "theme", "Dungeon Cellar"),
                Get(o, "upper", null));

        private static int Render(Dictionary<string, string> o)
        {
            var map = One(o);
            Console.WriteLine(Renderer.Render(map));

            var found = Validators.All
                .SelectMany(v => v.Check(map).Take(3).Select(f => $"  {v.Name}: {f}")).ToList();
            if (found.Count > 0)
            {
                Console.WriteLine("validator findings for this map:");
                foreach (string f in found) Console.WriteLine(f);
            }
            else Console.WriteLine("no validator findings for this map");
            return 0;
        }

        // What a theme actually resolves to, role by role, with the heights every elevation in the
        // builder is derived from. A wall and a corner of different heights cannot stack on one
        // pitch, so seeing the numbers side by side is usually enough to explain a geometry fault.
        private static int Tiles(Dictionary<string, string> o)
        {
            var themes = Get(o, "themes", null) is string s && s != "all"
                ? s.Split(',').Select(t => t.Trim()).ToList()
                : TileCatalog.KnownThemes.ToList();

            foreach (string theme in themes)
            {
                int rows = 1;
                Console.WriteLine($"── {theme}  combo={TileCatalog.WallIncludesFloor(theme)} " +
                                  $"wallPitch={TileCatalog.WallPitch(theme)} " +
                                  $"floorThick={TileCatalog.FloorThickness(theme)} " +
                                  $"storey={TileCatalog.StoreyHeight(theme, rows)} " +
                                  $"stairRise={TileCatalog.StairRise(theme)} " +
                                  $"steps={TileCatalog.StairStepCount(theme, rows, theme)}");
                foreach (TileRole role in Enum.GetValues(typeof(TileRole)).Cast<TileRole>())
                {
                    TileEntry e;
                    try { e = TileCatalog.Get(theme, role); } catch { continue; }
                    Console.WriteLine($"     {role,-12} h={e.Height,-5} {e.Size,-5} {e.Name}");
                }
            }
            return 0;
        }

        private static int Cell(Dictionary<string, string> o)
        {
            var map = One(o);
            int cx = int.Parse(Get(o, "x", "0"));
            int cz = int.Parse(Get(o, "z", "0"));
            int r  = int.TryParse(Get(o, "radius", "1"), out var rr) ? rr : 1;

            Console.WriteLine($"{map.Label} — tiles within {r} of ({cx},{cz})");
            foreach (var t in map.Tiles
                         .Where(t => Math.Abs(t.Cell.Item1 - cx) <= r && Math.Abs(t.Cell.Item2 - cz) <= r)
                         .OrderBy(t => t.Y).ThenBy(t => t.Cell.Item1).ThenBy(t => t.Cell.Item2))
                Console.WriteLine($"  ({t.X},{t.Y},{t.Z}) rot={t.Rot,-3} {(t.RoleKnown ? t.Role.ToString() : "Unknown")}");

            foreach (var room in (map.Spec.Rooms ?? new List<RoomSpec>()))
            {
                bool covers = GeneratedMap.Footprint(room).Any(c => Math.Abs(c.x - cx) <= r && Math.Abs(c.z - cz) <= r);
                if (covers)
                    Console.WriteLine($"  [in footprint of {room.Id} origin=({room.OriginX},{room.OriginY},{room.OriginZ}) size={room.Width}x{room.Depth}]");
            }
            return 0;
        }

        private static int Spec(Dictionary<string, string> o)
        {
            var map = One(o);
            Console.WriteLine(map.Label);

            foreach (var r in (map.Spec.Rooms ?? new List<RoomSpec>()).OrderBy(r => r.OriginY).ThenBy(r => r.Id))
                Console.WriteLine($"  room {r.Id,-9} origin=({r.OriginX},{r.OriginY},{r.OriginZ}) " +
                                  $"size={r.Width}x{r.Depth} wallRows={r.WallRows}");

            foreach (var c in map.Spec.Connections ?? new List<Connection>())
                Console.WriteLine($"  conn {c.FromRoomId} -> {c.ToRoomId} via {c.WallSide}+{c.Offset}");

            foreach (var v in map.Spec.VerticalConnections ?? new List<VerticalConnection>())
                Console.WriteLine($"  stair {v.LowerRoomId} -> {v.UpperRoomId} at ({v.StairOriginX},{v.StairOriginZ}) climbing {v.ClimbDirection}");

            var dupes = (map.Spec.Connections ?? new List<Connection>())
                .GroupBy(c => (c.FromRoomId, c.ToRoomId, c.WallSide, c.Offset))
                .Where(g => g.Count() > 1).ToList();
            foreach (var d in dupes)
                Console.WriteLine($"  DUPLICATE CONNECTION x{d.Count()}: {d.Key}");

            return 0;
        }

        // Which tile is right for a role is a question only the rig can answer, and a whole
        // generated map is a bad place to ask it: the candidate is buried among corridors and the
        // themes cannot be compared without regenerating between pastes. A row of plain rooms,
        // one per theme, puts every candidate on screen at once. Drive it with
        // tools/pick-probe.py, which synthesises one throwaway theme per candidate.
        private static int Rooms(Dictionary<string, string> o)
        {
            var themes = Get(o, "themes", TileCatalog.DefaultTheme)
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();
            int w    = int.TryParse(Get(o, "w", "5"), out var ww) ? ww : 5;
            int d    = int.TryParse(Get(o, "d", "5"), out var dd) ? dd : 5;
            int rows = int.TryParse(Get(o, "rows", "2"), out var rr) ? rr : 2;

            var spec = new LayoutSpec { Rooms = new List<RoomSpec>() };
            for (int i = 0; i < themes.Length; i++)
                spec.Rooms.Add(new RoomSpec
                {
                    Id = $"room_{i}", Theme = themes[i],
                    Width = w, Depth = d, WallRows = rows,
                    // Inset by one cell: the exterior shell is built outside the wall ring, and
                    // at the origin it would land on -1, which the encoder packs unsigned and
                    // TaleSpire then rejects as a whole.
                    OriginX = 1 + i * (w + 2), OriginZ = 1, OriginY = 0,
                });

            // A door tile is only placed where a connection lands, so a row of unconnected rooms
            // says nothing about the door role. Joining neighbours puts a door in each room's
            // facing wall. Offsets count from the first interior cell, so a wall of depth d has
            // its middle at (d-2)/2.
            if (o.ContainsKey("doors"))
            {
                spec.Connections = new List<Connection>();
                for (int i = 0; i + 1 < themes.Length; i++)
                {
                    spec.Connections.Add(new Connection
                    {
                        FromRoomId = $"room_{i}", ToRoomId = $"room_{i + 1}",
                        WallSide = "east", Offset = (d - 2) / 2,
                    });
                    spec.Connections.Add(new Connection
                    {
                        FromRoomId = $"room_{i + 1}", ToRoomId = $"room_{i}",
                        WallSide = "west", Offset = (d - 2) / 2,
                    });
                }
            }

            Console.WriteLine(SlabEncoder.Encode(SlabBuilder.Build(spec)));
            return 0;
        }

        private static int Slab(Dictionary<string, string> o)
        {
            var map = One(o);
            Console.WriteLine(SlabEncoder.Encode(
                map.Tiles.Select(t => (new Guid(t.Guid).ToByteArray(), t.X, t.Y, t.Z, t.Rot)).ToList()));
            return 0;
        }

    }
}
