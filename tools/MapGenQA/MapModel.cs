using System;
using System.Collections.Generic;
using System.Linq;
using TaleSpireMapGen.Generation;

namespace MapGenQA
{
    /// A single tile placement, with its GUID resolved back to a role where possible.
    public readonly struct Placement
    {
        public readonly string Guid;
        public readonly float X, Y, Z;
        public readonly int Rot;
        public readonly TileRole Role;
        public readonly bool RoleKnown;
        public readonly string Size;
        public readonly float Height;

        public Placement(string guid, float x, float y, float z, int rot,
                         TileRole role, bool roleKnown, string size, float height)
        {
            Guid = guid; X = x; Y = y; Z = z; Rot = rot; Role = role; RoleKnown = roleKnown;
            Size = size; Height = height;
        }

        // Positions are keyed the way the encoder stores them (value * 100, truncated to int),
        // so two placements collide here exactly when they would collide in the game.
        public (int, int, int) Key => ((int)Math.Round(X * 100), (int)Math.Round(Y * 100), (int)Math.Round(Z * 100));
        public (int, int) Cell => ((int)Math.Floor(X), (int)Math.Floor(Z));

        public override string ToString() => $"({X},{Y},{Z}) rot={Rot} {(RoleKnown ? Role.ToString() : "Unknown")}";
    }

    /// A furnishing prop placement, with the catalog entry behind it.
    public readonly struct PropAt
    {
        public readonly string Guid;
        // What the slab stores: the prop's *pivot*, not a corner of its bounds.
        public readonly float X, Y, Z;
        public readonly int Rot;
        public readonly PropEntry Entry;

        public PropAt(string guid, float x, float y, float z, int rot, PropEntry entry)
        {
            Guid = guid; X = x; Y = y; Z = z; Rot = rot; Entry = entry;
        }

        // Derived forwards — centre = pivot + rotate(offset) — where PropPlacer solves the same
        // relation backwards for the pivot. A sign error in either direction shows up as a
        // disagreement here rather than cancelling out.
        public (float X, float Z) Centre
        {
            get
            {
                double a = Rot * Math.PI / 12.0;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                return (X + s * Entry.PivotZ + c * Entry.PivotX,
                        Z + c * Entry.PivotZ - s * Entry.PivotX);
            }
        }

        /// Axis-aligned XZ extremes of the prop's collider, in world cells.
        public (float MinX, float MinZ, float MaxX, float MaxZ) Bounds
        {
            get
            {
                var (cx, cz) = Centre;
                var (fx, fz) = Entry.RotatedFootprint(Rot);
                return (cx - fx / 2f, cz - fz / 2f, cx + fx / 2f, cz + fz / 2f);
            }
        }

        public override string ToString() => $"'{Entry.Name}' pivot=({X},{Y},{Z}) rot={Rot}";
    }

    /// Maps tile GUIDs back to the entry they were placed as. Built by walking TileCatalog's
    /// public surface, since it does not expose its backing dictionary.
    public static class TileIndex
    {
        private static readonly Dictionary<string, TileEntry> _byGuid = new(StringComparer.OrdinalIgnoreCase);

        static TileIndex()
        {
            foreach (string theme in TileCatalog.KnownThemes)
            {
                foreach (TileRole role in Enum.GetValues(typeof(TileRole)).Cast<TileRole>())
                {
                    TileEntry e;
                    try { e = TileCatalog.Get(theme, role); }
                    catch { continue; }   // role absent for this theme
                    if (!string.IsNullOrEmpty(e.Id))
                        _byGuid[e.Id] = e;
                }
            }
        }

        public static bool TryGet(string guid, out TileEntry entry) => _byGuid.TryGetValue(guid, out entry);
    }

    /// One generated map: the spec that produced it plus the resolved tile placements.
    public class GeneratedMap
    {
        public int Seed;
        public int Size;
        public string Theme;
        public string UpperTheme;
        public LayoutSpec Spec;
        public List<Placement> Tiles = new();
        // Props are kept out of Tiles on purpose: the 25 tile validators walk that list and reason
        // about grid cells and stacking, none of which a pivot-positioned prop obeys.
        public List<PropAt> Props = new();

        public string Label => $"seed={Seed} size={SizeName(Size)} theme='{Theme}'"
                             + (UpperTheme == null ? "" : $" upper='{UpperTheme}'");

        public static string SizeName(int s) => s switch { 0 => "small", 1 => "medium", 2 => "large", _ => s.ToString() };

        public static GeneratedMap Generate(int seed, int size, string theme, string upperTheme = null,
                                            int clutter = 0, Dictionary<string, int> quota = null)
        {
            var spec = new DungeonTemplate().Generate(new TemplateParams
            {
                Seed = seed, Theme = theme, UpperTheme = upperTheme,
                DungeonSize = size, MinFloors = 1, MaxFloors = 2,
                ClutterDensity = clutter, PurposeQuota = quota,
            });

            var map = new GeneratedMap
            {
                Seed = seed, Size = size, Theme = theme, UpperTheme = upperTheme, Spec = spec,
            };

            foreach (var (guid, x, y, z, rot) in SlabBuilder.Build(spec))
            {
                string g = new Guid(guid).ToString();
                if (PropCatalog.TryGet(g, out var prop))
                {
                    map.Props.Add(new PropAt(g, x, y, z, rot, prop));
                    continue;
                }
                bool known = TileIndex.TryGet(g, out var e);
                map.Tiles.Add(new Placement(g, x, y, z, rot, known ? e.Role : default, known,
                                            known ? e.Size : null, known ? e.Height : 0f));
            }
            return map;
        }

        // Distinct floor elevations, taken from the rooms rather than the tiles so that
        // ceilings and stair half-steps do not read as extra storeys.
        public IEnumerable<float> RoomElevations =>
            (Spec.Rooms ?? new List<RoomSpec>()).Select(r => r.OriginY).Distinct().OrderBy(y => y);

        public IEnumerable<Placement> At(float y) => Tiles.Where(t => Math.Abs(t.Y - y) < 0.001f);

        /// Interior cells of a room (inside its wall ring).
        public static IEnumerable<(int x, int z)> Interior(RoomSpec r)
        {
            int w = Math.Max(r.Width, 3), d = Math.Max(r.Depth, 3);
            for (int x = r.OriginX + 1; x <= r.OriginX + w - 2; x++)
                for (int z = r.OriginZ + 1; z <= r.OriginZ + d - 2; z++)
                    yield return (x, z);
        }

        /// Every cell the room occupies, wall ring included.
        public static IEnumerable<(int x, int z)> Footprint(RoomSpec r)
        {
            int w = Math.Max(r.Width, 3), d = Math.Max(r.Depth, 3);
            for (int x = r.OriginX; x <= r.OriginX + w - 1; x++)
                for (int z = r.OriginZ; z <= r.OriginZ + d - 1; z++)
                    yield return (x, z);
        }

        /// The cell a connection's door sits in, derived the same way BuildRoom places it.
        public static (int x, int z)? DoorCell(RoomSpec r, Connection c)
        {
            int w = Math.Max(r.Width, 3), d = Math.Max(r.Depth, 3);
            return (c.WallSide ?? "").ToLowerInvariant() switch
            {
                "north" => (r.OriginX + 1 + c.Offset, r.OriginZ),
                "south" => (r.OriginX + 1 + c.Offset, r.OriginZ + d - 1),
                "west"  => (r.OriginX, r.OriginZ + 1 + c.Offset),
                "east"  => (r.OriginX + w - 1, r.OriginZ + 1 + c.Offset),
                _ => null,
            };
        }
    }
}
