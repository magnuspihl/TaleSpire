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

        public Placement(string guid, float x, float y, float z, int rot, TileRole role, bool roleKnown)
        {
            Guid = guid; X = x; Y = y; Z = z; Rot = rot; Role = role; RoleKnown = roleKnown;
        }

        // Positions are keyed the way the encoder stores them (value * 100, truncated to int),
        // so two placements collide here exactly when they would collide in the game.
        public (int, int, int) Key => ((int)Math.Round(X * 100), (int)Math.Round(Y * 100), (int)Math.Round(Z * 100));
        public (int, int) Cell => ((int)Math.Floor(X), (int)Math.Floor(Z));

        public override string ToString() => $"({X},{Y},{Z}) rot={Rot} {(RoleKnown ? Role.ToString() : "Unknown")}";
    }

    /// Maps tile GUIDs back to the role they were placed as. Built by walking TileCatalog's
    /// public surface, since it does not expose its backing dictionary.
    public static class TileIndex
    {
        private static readonly Dictionary<string, TileRole> _byGuid = new(StringComparer.OrdinalIgnoreCase);

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
                        _byGuid[e.Id] = role;
                }
            }
        }

        public static bool TryGetRole(string guid, out TileRole role) => _byGuid.TryGetValue(guid, out role);
    }

    /// One generated map: the spec that produced it plus the resolved tile placements.
    public class GeneratedMap
    {
        public int Seed;
        public int Size;
        public string Theme;
        public LayoutSpec Spec;
        public List<Placement> Tiles = new();

        public string Label => $"seed={Seed} size={SizeName(Size)} theme='{Theme}'";

        public static string SizeName(int s) => s switch { 0 => "small", 1 => "medium", 2 => "large", _ => s.ToString() };

        public static GeneratedMap Generate(int seed, int size, string theme)
        {
            var spec = new DungeonTemplate().Generate(new TemplateParams
            {
                Seed = seed, Theme = theme, DungeonSize = size, MinFloors = 1, MaxFloors = 2,
            });

            var map = new GeneratedMap { Seed = seed, Size = size, Theme = theme, Spec = spec };

            foreach (var (guid, x, y, z, rot) in SlabBuilder.Build(spec))
            {
                string g = new Guid(guid).ToString();
                bool known = TileIndex.TryGetRole(g, out var role);
                map.Tiles.Add(new Placement(g, x, y, z, rot, known ? role : default, known));
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
