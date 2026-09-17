using System;
using System.Collections.Generic;

namespace TaleSpireMapGen.Generation
{
    public struct TileEntry
    {
        public readonly string Id;
        public readonly string Name;
        public readonly TileRole Role;
        public readonly string Size;
        public readonly float Height;
        // Collider bounds in cells, unrotated. Half the tilesets ship a wall that is 1 x 0.5.
        public readonly float FootX;
        public readonly float FootZ;

        public TileEntry(string id, string name, TileRole role, string size, float height,
                         float footX = 1f, float footZ = 1f)
        {
            Id = id; Name = name; Role = role; Size = size; Height = height;
            FootX = footX; FootZ = footZ;
        }

        public byte[] GuidBytes => Guid.Parse(Id).ToByteArray();
        public bool IsUnit => Size == "1x1";

        /// <summary>Footprint after a quarter-turn rotation, as (x, z).</summary>
        public (float X, float Z) RotatedFootprint(int rotStep)
        {
            bool quarterTurn = ((rotStep / 6) & 1) == 1;
            return quarterTurn ? (FootZ, FootX) : (FootX, FootZ);
        }
    }

    /// <summary>
    /// Resolves a theme + role to the tile to place, out of the curated profiles.
    /// Rotation semantics: Wall and Corner are 1x1 combos —
    ///   rotStep 0=North-face, 6=East-face, 12=South-face, 18=West-face.
    /// </summary>
    public static class TileCatalog
    {
        public const string DefaultTheme = "Dungeon Cellar";

        // Profile role names. "cornerFiller" is the inner pocket piece; "corner" is the outer one.
        private static readonly Dictionary<TileRole, string> RoleNames
            = new Dictionary<TileRole, string>
            {
                { TileRole.Floor,       "floor"        },
                { TileRole.Wall,        "wall"         },
                { TileRole.Corner,      "corner"       },
                { TileRole.InnerCorner, "cornerFiller" },
                { TileRole.Stairs,      "stairs"       },
                { TileRole.StairBlock,  "stairBlock"   },
                { TileRole.Door,        "door"         },
                { TileRole.Pillar,      "pillar"       },
            };

        private static readonly Dictionary<(string, TileRole), TileEntry?> _cache
            = new Dictionary<(string, TileRole), TileEntry?>();
        private static readonly object _lock = new object();

        private static TileEntry? Resolve(string theme, TileRole role)
        {
            if (string.IsNullOrEmpty(theme)) return null;
            if (!RoleNames.TryGetValue(role, out string roleName)) return null;

            var profile = ProfileCatalog.GetProfile(theme);
            // A corner is a wall that turns a right angle, so it has to stack on the same pitch.
            var pick = role == TileRole.Corner
                ? profile?.PickOfHeight(roleName, WallPitch(theme))
                : profile?.Pick(roleName);
            if (pick == null) return null;

            return new TileEntry(pick.Id, pick.Name, role, pick.Size, pick.Height,
                                 pick.FootprintX, pick.FootprintZ);
        }

        /// <summary>The tile to place for a theme+role, falling back to the default theme.</summary>
        public static TileEntry Get(string theme, TileRole role)
        {
            var key = (theme ?? "", role);
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var hit))
                    return hit ?? Fallback(role);

                var resolved = Resolve(theme, role);
                _cache[key] = resolved;
                return resolved ?? Fallback(role);
            }
        }

        // Most tilesets have no door of their own — only 4 of 38 carry one. Borrowing the dungeon
        // door across themes is what the generator has always done and is better than leaving the
        // gap unfilled, but it is the wrong look in, say, a spaceship. Worth revisiting once
        // doorCompatibility is curated more widely.
        private static TileEntry Fallback(TileRole role)
        {
            var entry = Resolve(DefaultTheme, role);
            if (entry != null) return entry.Value;
            throw new InvalidOperationException(
                $"No tile for role {role} in '{DefaultTheme}' — tileset_profiles.json is missing or incomplete.");
        }

        public static bool HasTile(string theme, TileRole role) => Resolve(theme, role) != null;

        // Vertical distance between stacked wall rows. The placed tile's own height decides this,
        // not the profile's curated wallHeight: the two disagree in several tilesets (BellowGloom
        // lists 2.2 against a 2.5-tall wall), and stacking on the curated figure sinks each row
        // into the one below it. The profile value is only a fallback for a tile with no height.
        public static float WallPitch(string theme)
        {
            var wall = Resolve(theme, TileRole.Wall);
            if (wall != null && wall.Value.Height > 0) return wall.Value.Height;
            return ProfileCatalog.GetData(theme).wallHeight;
        }

        // Thickness of the floor layer, from the placed floor tile rather than the profile's
        // curated floorHeight — same argument as WallPitch. Everything that stands *on* the floor
        // starts here: a wall with no floor of its own, a door, an inner-corner filler, the first
        // tread of a stair.
        public static float FloorThickness(string theme)
        {
            var floor = Resolve(theme, TileRole.Floor);
            if (floor != null && floor.Value.Height > 0) return floor.Value.Height;
            return ProfileCatalog.GetData(theme).floorHeight;
        }

        public static bool IsKnownTheme(string theme) =>
            !string.IsNullOrEmpty(theme) && ProfileCatalog.GetProfile(theme) != null;

        /// <summary>
        /// Rise from one storey's floor layer to the next one's — the single rule for how tall a
        /// storey is. Every caller that needs a storey top must come through here.
        /// </summary>
        // A wall that carries its own floor already contains that thickness, so adding floorHeight
        // on top of the wall stack counts it twice. That is the half-unit gap you could see between
        // a Dungeon Cellar basement and the keep standing on it: the wall topped out at 2.5 and the
        // storey above was placed at 3.0, leaving the stair climbing into mid-air.
        public static float StoreyHeight(string theme, int wallRows)
        {
            if (wallRows < 1) wallRows = 1;
            return (WallIncludesFloor(theme) ? 0f : FloorThickness(theme))
                 + wallRows * WallPitch(theme);
        }

        /// <summary>How far one stair tread lifts you — its own height.</summary>
        public static float StairRise(string theme)
        {
            var stairs = Resolve(theme, TileRole.Stairs);
            if (stairs != null && stairs.Value.Height > 0) return stairs.Value.Height;
            return 0.5f;
        }

        /// <summary>Treads needed to climb from one storey's floor surface to the next one's.</summary>
        // A tread rises by its own height, and every stair tile in the catalog is 1.0 tall. The
        // builder stepped by a fixed 0.5 instead, so a 2.5 storey got five treads climbing 2.5
        // cells of ground — they read as a row of separate half-height flights standing side by
        // side rather than one staircase. Rounded *down*: overshooting leaves the top tread
        // hanging above the floor it is supposed to deliver you to, which is worse than the
        // half-step that rounding down can leave.
        // The run leaves the lower storey's floor surface and has to arrive on the upper storey's.
        // Those two thicknesses only cancel when both storeys share a tileset — a Marble Palace
        // basement under a Shogun Palace keep loses 0.31 to the difference, and a run counted
        // against the lower floor alone then tops out above the landing.
        // Not defaulted: a caller that does not know which theme is upstairs cannot size a flight,
        // and a default would let that mistake compile.
        public static int StairStepCount(string theme, int wallRows, string upperTheme)
        {
            float rise = StairRise(theme);
            if (rise <= 0) return 0;
            float climb = StoreyHeight(theme, wallRows)
                        + FloorThickness(string.IsNullOrEmpty(upperTheme) ? theme : upperTheme)
                        - FloorThickness(theme);
            return Math.Max(1, (int)Math.Floor(climb / rise + 0.001f));
        }

        // True when a theme's wall and corner tiles already carry a floor, so no separate floor
        // tile is needed beneath them. This stays a profile flag rather than something derived
        // from tile height: the curated wallHeight means the combo height in some tilesets and
        // the bare height in others, so the two cannot be compared across themes.
        public static bool WallIncludesFloor(string theme) =>
            ProfileCatalog.GetProfile(theme)?.HasIntegratedFloorWall ?? false;

        /// <summary>Themes that can build a map, verified ones first.</summary>
        public static string[] KnownThemes => ProfileCatalog.BuildableThemes().ToArray();

        /// <summary>True once the theme's tile picks have been checked in game, not just compiled.</summary>
        public static bool IsVerified(string theme) =>
            ProfileCatalog.GetProfile(theme)?.Verified ?? false;
    }
}
