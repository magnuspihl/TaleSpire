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
        public string Notes;    // freeform, for AI-generated context
    }

    public class Connection
    {
        public string FromRoomId;
        public string ToRoomId;
        public string WallSide;  // "north" | "south" | "east" | "west"
        public int Offset;       // tile offset along that wall where the door goes
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
        public List<RoomSpec> Rooms;
        public List<Connection> Connections;
        public List<VerticalConnection> VerticalConnections;
        public string Notes;
    }
}
