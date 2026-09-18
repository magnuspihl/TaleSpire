using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;

namespace TaleSpireMapGen.Generation
{
    /// <summary>One furnishing prop, exactly as the asset pack's own index describes it.</summary>
    public class PropEntry
    {
        [JsonProperty("id")]        public string Id;
        [JsonProperty("name")]      public string Name;
        [JsonProperty("folder")]    public string Folder;
        // "floor" — pivot on the bottom face, so the prop stands on whatever you place it at.
        // "hanging" — pivot on the top face and the prop drops below it: screens, curtains,
        // prison wall cuffs, chains. Placing one of those at floor level buries it.
        [JsonProperty("mount")]     public string Mount;
        // [x, z] collider size in cells, unrotated.
        [JsonProperty("footprint")] public float[] Footprint;
        [JsonProperty("height")]    public float Height;
        // Offset from the pivot to the bounds centre on the horizontal plane. Zero for two thirds
        // of props; the rest need it or they do not sit where they are put.
        [JsonProperty("pivot")]     public float[] Pivot;

        public bool IsFloorMounted => Mount != "hanging";

        public float FootX => Footprint != null && Footprint.Length == 2 ? Footprint[0] : 1f;
        public float FootZ => Footprint != null && Footprint.Length == 2 ? Footprint[1] : 1f;
        public float PivotX => Pivot != null && Pivot.Length == 2 ? Pivot[0] : 0f;
        public float PivotZ => Pivot != null && Pivot.Length == 2 ? Pivot[1] : 0f;

        public byte[] GuidBytes => Guid.Parse(Id).ToByteArray();

        /// <summary>Axis-aligned extent of the footprint once turned, as (x, z).</summary>
        // The general rotated AABB rather than an axis swap, because a corner prop is turned into
        // the angle at 45° and a swap says its footprint is unchanged — so a 2 x 1 cabinet laid
        // diagonally would be measured as claiming two cells when it sweeps closer to three.
        public (float X, float Z) RotatedFootprint(int rotStep)
        {
            double a = rotStep * Math.PI / 12.0;
            float c = Math.Abs((float)Math.Cos(a)), s = Math.Abs((float)Math.Sin(a));
            return (c * FootX + s * FootZ, s * FootX + c * FootZ);
        }

        /// <summary>Where to write the prop so its bounds centre lands on (cx, cz).</summary>
        // A prop's stored position is its *pivot*, not the minimum corner of its bounds the way a
        // tile's is: AssetMath dispatches on PlaceableKind and builds prop bounds around the origin
        // with `centre = origin + rotate(pivotOffset)`. So none of the tile placement machinery
        // transfers — SlabBuilder.EdgeHug in particular would be actively wrong here.
        public (float X, float Z) OriginForCentre(float cx, float cz, int rotStep)
        {
            double a = rotStep * Math.PI / 12.0;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            return (cx - (s * PivotZ + c * PivotX),
                    cz - (c * PivotZ - s * PivotX));
        }
    }

    /// <summary>
    /// The props available to dress a room with, keyed "style/group" — "medieval/bed".
    /// </summary>
    // Prop folders are functional rather than per-tileset: there is no "Castle Fortified
    // furniture", only a Furniture folder shared by every medieval set. So the only theming the
    // data supports is the coarse medieval/modern split the two packs already are, and a recipe
    // names a GroupTag the pack author wrote rather than a GUID anyone curated.
    public static class PropCatalog
    {
        private const string EmbeddedResourceName = "TaleSpireMapGen.prop_catalog.json";

        private static Dictionary<string, List<PropEntry>> _groups;
        private static readonly object _lock = new object();

        private static void EnsureLoaded()
        {
            if (_groups != null) return;
            lock (_lock)
            {
                if (_groups != null) return;
                _groups = new Dictionary<string, List<PropEntry>>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    string json;
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName))
                    {
                        if (stream == null) return;
                        using (var reader = new System.IO.StreamReader(stream))
                            json = reader.ReadToEnd();
                    }

                    var root = JsonConvert.DeserializeObject<CatalogRoot>(json);
                    if (root?.Groups == null) return;
                    foreach (var kv in root.Groups)
                        if (kv.Value != null && kv.Value.Count > 0)
                            _groups[kv.Key] = kv.Value;
                }
                catch (Exception ex)
                {
                    // Clutter is decoration: a map with no props is still a map, so never take the
                    // generator down over it.
                    UnityEngine.Debug.LogWarning($"[PropCatalog] Failed to load props: {ex.Message}");
                }
            }
        }

        /// <summary>Props in a group, or an empty list. Never null.</summary>
        public static IReadOnlyList<PropEntry> Group(string style, string group)
        {
            EnsureLoaded();
            return _groups.TryGetValue($"{style}/{group}", out var entries)
                ? (IReadOnlyList<PropEntry>)entries
                : Array.Empty<PropEntry>();
        }

        public static bool IsLoaded
        {
            get { EnsureLoaded(); return _groups.Count > 0; }
        }

        /// <summary>Look a prop up by GUID — how anything downstream tells a prop from a tile.</summary>
        // The slab format records no placeable kind; TaleSpire itself resolves Tile vs Prop from
        // the GUID against the installed packs, and this is the same question asked locally.
        public static bool TryGet(string id, out PropEntry entry)
        {
            EnsureLoaded();
            if (_byId == null)
            {
                var index = new Dictionary<string, PropEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entries in _groups.Values)
                    foreach (var e in entries)
                        if (!string.IsNullOrEmpty(e.Id)) index[e.Id] = e;
                _byId = index;
            }
            return _byId.TryGetValue(id ?? "", out entry);
        }

        private static Dictionary<string, PropEntry> _byId;

        private class CatalogRoot
        {
            [JsonProperty("groups")]
            public Dictionary<string, List<PropEntry>> Groups { get; set; }
        }
    }
}
