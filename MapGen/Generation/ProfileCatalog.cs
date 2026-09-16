using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace TaleSpireMapGen.Generation
{
    public class TileProfileEntry
    {
        [JsonProperty("id")]
        public string Id;
    }

    public class TilesetProfile
    {
        [JsonProperty("folder")]
        public string Folder;
        [JsonProperty("pack")]
        public string Pack;
        [JsonProperty("wallHeight")]
        public float WallHeight;
        [JsonProperty("floorHeight")]
        public float FloorHeight;
        [JsonProperty("hasIntegratedFloorWall")]
        public bool HasIntegratedFloorWall;
        [JsonProperty("innerCornerStyle")]
        public string InnerCornerStyle;
        [JsonProperty("stairType")]
        public string StairType;
        [JsonProperty("minCeilingHeight")]
        public float MinCeilingHeight;
        [JsonProperty("minTileDimension")]
        public int MinTileDimension;
        [JsonProperty("tilesByRole")]
        public Dictionary<string, List<TileProfileEntry>> TilesByRole;
    }

    public static class ProfileCatalog
    {
        private const string ProfilesPath = "/home/coder/TaleSpire-Binaries/tileset_profiles.json";

        // _profiles[pack][folder] = TilesetProfile
        private static Dictionary<string, Dictionary<string, TilesetProfile>> _profiles;
        private static readonly object _lock = new object();

        private static void EnsureLoaded()
        {
            if (_profiles != null) return;
            lock (_lock)
            {
                if (_profiles != null) return;
                _profiles = new Dictionary<string, Dictionary<string, TilesetProfile>>(StringComparer.OrdinalIgnoreCase);

                if (!File.Exists(ProfilesPath)) return;

                try
                {
                    string json = File.ReadAllText(ProfilesPath);
                    // JSON shape: { "profiles": { "Pack Name": { "Folder Name": {...}, ... } } }
                    var root = JsonConvert.DeserializeObject<ProfilesRoot>(json);
                    if (root?.Profiles == null) return;

                    foreach (var pack in root.Profiles)
                    {
                        if (pack.Value == null) continue;
                        var packDict = new Dictionary<string, TilesetProfile>(StringComparer.OrdinalIgnoreCase);
                        foreach (var folder in pack.Value)
                            if (folder.Value != null)
                                packDict[folder.Key] = folder.Value;
                        _profiles[pack.Key] = packDict;
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't crash — callers fall back to TileCatalog defaults.
                    UnityEngine.Debug.LogWarning($"[ProfileCatalog] Failed to load profiles: {ex.Message}");
                }
            }
        }

        public static TilesetProfile GetProfile(string pack, string folder)
        {
            EnsureLoaded();
            if (_profiles.TryGetValue(pack, out var packDict) &&
                packDict.TryGetValue(folder, out var profile))
                return profile;
            return null;
        }

        public static TilesetProfile GetProfile(string folder)
        {
            EnsureLoaded();
            foreach (var pack in _profiles.Values)
                if (pack.TryGetValue(folder, out var profile))
                    return profile;
            return null;
        }

        // Shim so callers always get usable values, even with no profile on disk.
        public static (float wallHeight, float floorHeight, bool wallCombo, string icStyle, string stairType, float minCeilingHeight)
            GetData(string theme)
        {
            var p = GetProfile(theme);
            if (p != null)
                return (p.WallHeight, p.FloorHeight, p.HasIntegratedFloorWall,
                        p.InnerCornerStyle ?? "filler", p.StairType ?? "stackable", p.MinCeilingHeight);
            return (2.5f, 0.5f, TileCatalog.WallIncludesFloor(theme), "filler", "stackable", 2.5f);
        }

        private class ProfilesRoot
        {
            [JsonProperty("profiles")]
            public Dictionary<string, Dictionary<string, TilesetProfile>> Profiles { get; set; }
        }
    }
}
