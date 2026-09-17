using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace TaleSpireMapGen.Generation
{
    public class TileProfileEntry
    {
        [JsonProperty("id")]
        public string Id;
        [JsonProperty("name")]
        public string Name;
        // "1x1", "2x1", "2x2", ... MapGen places on a 1x1 grid, so anything else is unusable.
        [JsonProperty("size")]
        public string Size;
        [JsonProperty("height")]
        public float Height;
        // [x, z] size of the tile's collider bounds, in cells. Absent means assume a full cell.
        [JsonProperty("footprint")]
        public float[] Footprint;

        public bool IsUnit => Size == "1x1";
        public float FootprintX => Footprint != null && Footprint.Length == 2 ? Footprint[0] : 1f;
        public float FootprintZ => Footprint != null && Footprint.Length == 2 ? Footprint[1] : 1f;
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
        // True once the theme has been looked at in game on the rig, not merely compiled.
        [JsonProperty("verified")]
        public bool Verified;
        // Set for a tileset that satisfies every structural check and still cannot build a room:
        // its wall art is a post or an arch, so a ring of them never closes. Absent means fine,
        // so a profile written before this flag existed stays offered.
        [JsonProperty("excluded")]
        public bool Excluded;
        [JsonProperty("tilesByRole")]
        public Dictionary<string, List<TileProfileEntry>> TilesByRole;

        // Candidates are stored best-first by tools/build-mapgen-profiles.py, so the first 1x1
        // entry is the intended pick. Non-1x1 tiles are kept in the file — a role can look
        // populated and still be unusable, and that distinction is worth being able to see.
        public TileProfileEntry Pick(string role)
        {
            if (TilesByRole == null || !TilesByRole.TryGetValue(role, out var entries) || entries == null)
                return null;
            foreach (var e in entries)
                if (e != null && e.IsUnit) return e;
            return null;
        }

        /// <summary>Best 1x1 candidate of that height, or the plain best if none matches.</summary>
        // Five tilesets list a corner that is half a unit taller than their wall — Facility's
        // best-ranked corner is 2.5 against a 2.0 wall — and a ring stacked at the wall's pitch
        // then buries every corner above the first. Each of the five also carries a matching
        // candidate further down its list, so this is a pick problem rather than a curation gap.
        public TileProfileEntry PickOfHeight(string role, float height)
        {
            if (TilesByRole == null || !TilesByRole.TryGetValue(role, out var entries) || entries == null)
                return null;
            foreach (var e in entries)
                if (e != null && e.IsUnit && Math.Abs(e.Height - height) < 0.001f) return e;
            return Pick(role);
        }
    }

    public static class ProfileCatalog
    {
        private const string EmbeddedResourceName = "TaleSpireMapGen.tileset_profiles.json";

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

                // Sidecar file takes precedence — drop a tileset_profiles.json next to the DLL
                // to update profiles without rebuilding. Falls back to the bundled embedded copy.
                string json = TryReadSidecarFile() ?? TryReadEmbeddedResource();
                if (json == null) return;

                try
                {
                    // JSON shape: { "profiles": { "Pack Name": { "Folder Name": {...}, ... } } }
                    // Curated profiles legitimately leave numeric fields null where a tileset has
                    // no sensible value. Without Ignore, the first such null aborts the whole file
                    // and every profile is lost, not just the incomplete one.
                    var root = JsonConvert.DeserializeObject<ProfilesRoot>(json, new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore,
                    });
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

        private static string TryReadSidecarFile()
        {
            try
            {
                string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dllDir, "tileset_profiles.json");
                if (File.Exists(path))
                    return File.ReadAllText(path);
            }
            catch { }
            return null;
        }

        private static string TryReadEmbeddedResource()
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream))
                        return reader.ReadToEnd();
                }
            }
            catch { }
            return null;
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

        // Themes that can actually build a map: 1x1 tiles for the three structural roles, on a
        // tileset whose tiles are 1x1 to begin with. Verified themes come first — the rest
        // generate, but their tile picks are a heuristic nobody has looked at yet.
        public static List<string> BuildableThemes()
        {
            EnsureLoaded();
            var verified = new List<string>();
            var rest     = new List<string>();

            foreach (var pack in _profiles.Values)
            foreach (var kv in pack)
            {
                var p = kv.Value;
                if (p.Excluded) continue;
                if (p.MinTileDimension > 1) continue;
                if (p.Pick("wall") == null || p.Pick("floor") == null || p.Pick("corner") == null)
                    continue;
                (p.Verified ? verified : rest).Add(kv.Key);
            }

            verified.Sort(StringComparer.OrdinalIgnoreCase);
            rest.Sort(StringComparer.OrdinalIgnoreCase);
            verified.AddRange(rest);
            return verified;
        }

        // Shim so callers always get usable values, even with no profile on disk.
        public static (float wallHeight, float floorHeight, bool wallCombo, string icStyle, string stairType, float minCeilingHeight)
            GetData(string theme)
        {
            var p = GetProfile(theme);
            if (p != null)
                // A profile may omit any numeric field. Zero wall height would stack every wall row
                // at one elevation, so fall back rather than trusting an absent value.
                return (p.WallHeight > 0 ? p.WallHeight : 2.5f,
                        p.FloorHeight > 0 ? p.FloorHeight : 0.5f,
                        p.HasIntegratedFloorWall,
                        p.InnerCornerStyle ?? "filler",
                        p.StairType ?? "stackable",
                        p.MinCeilingHeight > 0 ? p.MinCeilingHeight : 2.5f);
            return (2.5f, 0.5f, false, "filler", "stackable", 2.5f);
        }

        private class ProfilesRoot
        {
            [JsonProperty("profiles")]
            public Dictionary<string, Dictionary<string, TilesetProfile>> Profiles { get; set; }
        }
    }
}
