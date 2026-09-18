using System.Collections.Generic;

namespace TaleSpireMapGen.Generation
{
    public enum TileRole
    {
        Floor, Wall, Corner, InnerCorner, Stairs, StairBlock, Door, Window, Arch, Roof, Pillar, Prop
    }

    public class TileRef
    {
        public string Id;       // ContentGuid string
        public string Name;
        public TileRole Role;
        public string Size;     // e.g. "1x1", "2x1"
    }

    public class RoomSpec
    {
        public string Id;       // unique within layout, e.g. "room_0"
        public string Theme;    // folder name from tile catalog, e.g. "Dungeon Cellar"
        public int Width;       // in tiles
        public int Depth;       // in tiles
        public int OriginX;     // grid position
        public int OriginZ;
        public float OriginY;   // elevation (0 = ground); float to support mid-unit floors
        public int WallRows;    // wall tile rows tall (1=cramped, 2=normal, 3+=grand). 0 means use profile default.
        // What the room is for, named by prop_recipes.json — "Barracks", "Treasury", "Empty".
        // Decides furniture only; the geometry is already fixed by the time it is assigned.
        public string Purpose;
        public string Notes;    // freeform, for AI-generated context
    }

    public class Connection
    {
        public string FromRoomId;
        public string ToRoomId;
        public string WallSide;  // "north" | "south" | "east" | "west"
        public int Offset;       // tile offset along that wall where the door goes
    }

    /// <summary>The way in from outside — a door in a ground-floor room's outward-facing wall.</summary>
    // Declared on the spec rather than worked out wherever it is needed. Three places care where
    // the way in is (the builder cuts the door, the purpose assigner measures depth from it, the
    // harness checks nothing else leaks), and three independent derivations of "the outermost
    // room" would agree right up until one of them did not.
    public class EntranceSpec
    {
        public string RoomId;
        public string WallSide;  // "north" | "south" | "east" | "west", as Connection
        public int Offset;       // tile offset along that wall, as Connection
    }

    public class VerticalConnection
    {
        public string LowerRoomId;
        public string UpperRoomId;
        public int StairOriginX;    // tile coord of bottom stair step
        public int StairOriginZ;
        public string ClimbDirection; // "north"|"south"|"east"|"west" — direction stairs ascend
    }

    public class LayoutSpec
    {
        public string Theme;
        // 0 = bare rooms, 3 = as furnished as the recipes go. Clutter lives on the spec rather
        // than on the template because it dresses the built geometry and changes none of it.
        public int ClutterDensity;
        // Drives prop selection only — the layout is already fixed by the time this is read.
        public int Seed;
        // Purposes the caller asked for, and how many of each could not be placed. Kept on the
        // spec rather than returned, so "you asked for two treasuries and this map has room for
        // one" survives into the log dump and the harness instead of being lost in the assigner.
        public Dictionary<string, int> PurposeQuota;
        public Dictionary<string, int> PurposeShortfall;
        public List<RoomSpec> Rooms;
        public List<Connection> Connections;
        public List<VerticalConnection> VerticalConnections;
        public EntranceSpec Entrance;
        public string Notes;
    }
}
