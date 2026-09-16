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

        public TileEntry(string id, string name, TileRole role, string size)
        { Id = id; Name = name; Role = role; Size = size; }

        public byte[] GuidBytes => Guid.Parse(Id).ToByteArray();
    }

    /// <summary>
    /// Hardcoded tile catalog for known TaleSpire themes.
    /// GUIDs sourced from /home/coder/TaleSpire/tools/tile_catalog.json.
    /// Rotation semantics: each theme's Wall and Corner tiles are 1x1 combos —
    ///   rotStep 0=North-face, 6=East-face, 12=South-face, 18=West-face.
    /// </summary>
    public static class TileCatalog
    {
        private static readonly Dictionary<string, Dictionary<TileRole, TileEntry>> _themes
            = new Dictionary<string, Dictionary<TileRole, TileEntry>>(StringComparer.OrdinalIgnoreCase);

        static TileCatalog()
        {
            // Dungeon Cellar — Wall/Floor combo tiles (floor baked into wall/corner).
            // Floor tile: "Dungeon Floor - Small" (1x1, h=0.5) — the only 1x1 floor in this set.
            // Wall:       "Dungeon Wall/Floor 01"            (1x1, h=2.5, includes floor).
            // Corner:     "Dungeon Wall/Floor Corner - Small" (1x1, h=2.5, includes floor).
            // InnerCorner = floor-level filler tile placed on top of a floor tile (h=2.0, no floor baked in).
            Add("Dungeon Cellar",
                (TileRole.Floor,       "d5900784-9510-4cf7-b017-f369448d0d52", "Dungeon Floor - Small",               "1x1"),
                (TileRole.Wall,        "ed0ad169-3248-411a-9eb0-4aada656fb61", "Dungeon Wall/Floor 01",                "1x1"),
                (TileRole.Corner,      "fdd3c8dc-9c94-4a63-a7d9-10ae36d07fe7", "Dungeon Wall/Floor Corner - Small",   "1x1"),
                (TileRole.InnerCorner, "cec14f2e-faf2-4a9a-bd96-909c16b92197", "Dungeon Wall Corner Filler",          "1x1"),
                (TileRole.Door,        "3151e230-bac7-401e-8fa8-b993bbee17ea", "Door - Dungeon",                      "1x1"),
                (TileRole.Stairs,      "ed072f00-f245-44e7-9d25-6a1cb498cbaa", "Dungeon Stairs",                      "1x1")
            );

            // MegaDungeon — "pref" variants (role=prop, h=2.5) include a floor base.
            // Wall without floor: md_wall_1x1_01 (h=2.0).  Wall WITH floor: md_pref_wall_1x1_01 (h=2.5).
            // Corner without floor: md_wall_corner_1x1_01 (h=2.0).  Corner WITH floor: same name, h=2.5, different GUID.
            Add("MegaDungeon",
                (TileRole.Floor,       "940d013f-c982-43e7-922a-0317cc1c74db", "md_floor_1x1_01",                     "1x1"),
                (TileRole.Wall,        "c8312af8-8bfb-41e1-bb6e-30280686b168", "md_pref_wall_1x1_01",                 "1x1"),
                (TileRole.Corner,      "0820791e-c1a8-4a1e-b690-f34b0cb276ba", "md_wall_corner_1x1_01 (with floor)",  "1x1"),
                (TileRole.InnerCorner, "bb8e7378-44da-4556-bac5-92420f766815", "md_corner_filler_01",                 "1x1"),
                (TileRole.Door,        "3151e230-bac7-401e-8fa8-b993bbee17ea", "Door - Dungeon",                      "1x1"),
                (TileRole.Stairs,      "0252d209-c23c-4f72-aa8b-51ed0dc09f3a", "md_stairblock_01",                    "1x1")
            );

            Add("Sewers",
                (TileRole.Floor,       "81a8acd3-0685-44ce-b185-cb70c58ae68f", "Sewer Floor 01",                      "1x1"),
                (TileRole.Wall,        "887d5410-4c59-40a3-b338-8d8c2efaafd3", "Sewer Wall/Floor 01",                 "1x1"),
                (TileRole.Corner,      "180105d6-8c75-4da0-85ae-251aa3145e98", "Sewer Wall/Floor Corner - Small",     "1x1"),
                (TileRole.InnerCorner, "d172bb08-175b-495a-bd50-22f4135d5209", "Sewer Corner Filler - Small",         "1x1"),
                (TileRole.Door,        "3151e230-bac7-401e-8fa8-b993bbee17ea", "Door - Dungeon",                      "1x1"),
                (TileRole.Stairs,      "ed072f00-f245-44e7-9d25-6a1cb498cbaa", "Dungeon Stairs",                      "1x1")
            );

        }

        private static void Add(string theme, params (TileRole role, string id, string name, string size)[] entries)
        {
            var dict = new Dictionary<TileRole, TileEntry>();
            foreach (var (role, id, name, size) in entries)
                dict[role] = new TileEntry(id, name, role, size);
            _themes[theme] = dict;
        }

        /// <summary>Returns the best-matching tile for a theme+role, falling back to Dungeon Cellar.</summary>
        public static TileEntry Get(string theme, TileRole role)
        {
            if (!string.IsNullOrEmpty(theme)
                && _themes.TryGetValue(theme, out var d)
                && d.TryGetValue(role, out var entry))
                return entry;

            // Fallback: Dungeon Cellar
            return _themes["Dungeon Cellar"][role];
        }

        public static bool IsKnownTheme(string theme) =>
            !string.IsNullOrEmpty(theme) && _themes.ContainsKey(theme);

        // Returns true when the Wall/Corner tiles for a theme already include a floor component.
        // All three current themes use h=2.5 combo tiles, so no extra floor tile is needed under walls.
        public static bool WallIncludesFloor(string theme) =>
            string.Equals(theme, "Dungeon Cellar", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, "MegaDungeon",    StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, "Sewers",         StringComparison.OrdinalIgnoreCase);

        public static readonly string[] KnownThemes = { "Dungeon Cellar", "MegaDungeon", "Sewers" };
    }
}
