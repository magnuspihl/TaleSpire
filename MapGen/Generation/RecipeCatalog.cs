using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;

namespace TaleSpireMapGen.Generation
{
    /// <summary>What one room purpose is worth to a room, and what it fills that room with.</summary>
    public class PropRecipe
    {
        [JsonIgnore] public string Name;

        [JsonProperty("weight")]      public float Weight = 1f;
        [JsonProperty("minArea")]     public int MinArea;
        [JsonProperty("maxArea")]     public int MaxArea;      // 0 = no cap
        [JsonProperty("wantsArea")]   public float WantsArea;
        [JsonProperty("wantsDegree")] public float WantsDegree;
        [JsonProperty("wantsDepth")]  public float WantsDepth;
        [JsonProperty("storey")]      public string Storey = "any";
        [JsonProperty("zones")]       public Dictionary<string, List<string>> Zones;

        /// <summary>Whether a room of this size, on this storey, may take this purpose at all.</summary>
        // Kept on the recipe rather than in the assigner so the quota validator can ask the same
        // question of the same data without re-reading the JSON's shape.
        public bool Fits(int area, bool upper)
        {
            if (area < MinArea) return false;
            if (MaxArea > 0 && area > MaxArea) return false;
            return (Storey ?? "any").ToLowerInvariant() switch
            {
                "ground" => !upper,
                "upper"  => upper,
                _        => true,
            };
        }

        public IReadOnlyList<string> Zone(string zone) =>
            Zones != null && Zones.TryGetValue(zone, out var groups) && groups != null
                ? (IReadOnlyList<string>)groups
                : Array.Empty<string>();
    }

    /// <summary>
    /// The room purposes available per style, loaded from the authored prop_recipes.json.
    /// </summary>
    // Purposes are strings rather than an enum: the recipe file is the whole vocabulary, and an
    // enum beside it would be a second list to keep in step for no gain — nothing in the generator
    // switches on a particular purpose.
    public static class RecipeCatalog
    {
        public const string Empty = "Empty";
        public const string ZoneWall = "wall", ZoneCorner = "corner", ZoneCentre = "centre";

        private const string EmbeddedResourceName = "TaleSpireMapGen.prop_recipes.json";

        private static Dictionary<string, StyleRecipes> _styles;
        private static readonly object _lock = new object();

        private static void EnsureLoaded()
        {
            if (_styles != null) return;
            lock (_lock)
            {
                if (_styles != null) return;
                _styles = new Dictionary<string, StyleRecipes>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    string json;
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName))
                    {
                        if (stream == null) return;
                        using (var reader = new System.IO.StreamReader(stream))
                            json = reader.ReadToEnd();
                    }

                    var root = JsonConvert.DeserializeObject<RecipeRoot>(json);
                    if (root?.Styles == null) return;
                    foreach (var kv in root.Styles)
                    {
                        if (kv.Value?.Purposes == null) continue;
                        foreach (var p in kv.Value.Purposes) p.Value.Name = p.Key;
                        _styles[kv.Key] = kv.Value;
                    }
                }
                catch (Exception ex)
                {
                    // Same rule as the prop catalog: a dungeon with unfurnished rooms is still a
                    // dungeon, so never take the generator down over decoration data.
                    UnityEngine.Debug.LogWarning($"[RecipeCatalog] Failed to load recipes: {ex.Message}");
                }
            }
        }

        public static bool IsLoaded
        {
            get { EnsureLoaded(); return _styles.Count > 0; }
        }

        /// <summary>Every purpose the style defines, in file order.</summary>
        public static IReadOnlyList<PropRecipe> Purposes(string style)
        {
            EnsureLoaded();
            if (!_styles.TryGetValue(style ?? "", out var s) || s.Purposes == null)
                return Array.Empty<PropRecipe>();
            var list = new List<PropRecipe>(s.Purposes.Count);
            foreach (var kv in s.Purposes) list.Add(kv.Value);
            return list;
        }

        public static PropRecipe Get(string style, string purpose)
        {
            EnsureLoaded();
            return _styles.TryGetValue(style ?? "", out var s) && s.Purposes != null &&
                   s.Purposes.TryGetValue(purpose ?? "", out var r) ? r : null;
        }

        /// <summary>Groups the lighting layer draws from — one list for the whole style.</summary>
        public static IReadOnlyList<string> Light(string style)
        {
            EnsureLoaded();
            return _styles.TryGetValue(style ?? "", out var s) && s.Light != null
                ? (IReadOnlyList<string>)s.Light
                : Array.Empty<string>();
        }

        /// <summary>Which prop vocabulary a tileset belongs to.</summary>
        // Two packs, two looks. Props are grouped by function rather than by tileset, so this coarse
        // split off the pack GUID is the only theming the data supports.
        public static string StyleFor(string theme)
        {
            string pack = ProfileCatalog.GetProfile(theme)?.Pack ?? "";
            return pack.IndexOf("Cyberpunk", StringComparison.OrdinalIgnoreCase) >= 0
                || pack.IndexOf("Sci-fi", StringComparison.OrdinalIgnoreCase) >= 0
                ? "modern" : "medieval";
        }

        private class StyleRecipes
        {
            [JsonProperty("light")]    public List<string> Light { get; set; }
            [JsonProperty("purposes")] public Dictionary<string, PropRecipe> Purposes { get; set; }
        }

        private class RecipeRoot
        {
            [JsonProperty("styles")] public Dictionary<string, StyleRecipes> Styles { get; set; }
        }
    }
}
