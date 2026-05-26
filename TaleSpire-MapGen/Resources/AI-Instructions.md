# MapGen AI Instructions

## Purpose

Paste this document (or the relevant sections) to an AI assistant, then paste the
AI's JSON response into the **Paste AI JSON here** field in the MapGen window.
Click **Generate & Copy Slab**, then paste (`Ctrl+V`) inside TaleSpire.

---

## LayoutSpec JSON Schema

```json
{
  "Theme": "string — tile catalog folder name (see Known Themes below)",
  "Notes": "string — optional, any freeform context",
  "Rooms": [ /* one or more RoomSpec objects */ ],
  "Connections": [ /* zero or more Connection objects */ ]
}
```

### RoomSpec

| Field     | Type   | Description |
|-----------|--------|-------------|
| Id        | string | Unique ID within this layout, e.g. `"room_0"`, `"throne"` |
| Theme     | string | Override tile set for this room (optional — inherits layout Theme) |
| Width     | int    | Room width in tiles (minimum 4) |
| Depth     | int    | Room depth in tiles (minimum 4) |
| OriginX   | int    | Top-left X position on the tile grid |
| OriginZ   | int    | Top-left Z position on the tile grid |
| OriginY   | int    | Elevation (0 = ground floor; 4 ≈ one floor up) |
| Notes     | string | Freeform flavour text (ignored by the builder) |

**Rooms must not overlap.** Leave at least 1 tile of space between rooms so corridors can be placed.

### Connection

| Field      | Type   | Description |
|------------|--------|-------------|
| FromRoomId | string | Source room's Id |
| ToRoomId   | string | Destination room's Id |
| WallSide   | string | Which wall of *FromRoom* has the door: `"north"` \| `"south"` \| `"east"` \| `"west"` |
| Offset     | int    | Tile offset along that wall, starting from 0 at the left/top end |

**Connections should be paired.** Provide one entry A→B and one entry B→A so
the builder knows where to open the door on both sides and can route the corridor.

Wall offset ranges:
- `"north"` / `"south"` wall: offset 0 to `Width - 3`
- `"east"` / `"west"` wall: offset 0 to `Depth - 3`

---

## Known Themes

- `Dungeon Cellar` — stone dungeon walls/floors, chains, torches
- `MegaDungeon` — modular stone corridors, arched ceilings
- `Sewers` — brick arches, slick stone, drains

(More themes can be added in `TileCatalog.cs`.)

---

## Minimal Valid Example (1 room, no connections)

```json
{
  "Theme": "Dungeon Cellar",
  "Notes": "A single 10×10 chamber",
  "Rooms": [
    {
      "Id": "room_0",
      "Theme": "Dungeon Cellar",
      "Width": 10,
      "Depth": 10,
      "OriginX": 0,
      "OriginZ": 0,
      "OriginY": 0,
      "Notes": "Main chamber"
    }
  ],
  "Connections": []
}
```

---

## Two-Room Example With Corridor

```json
{
  "Theme": "Dungeon Cellar",
  "Notes": "Guard room leads east to a treasury",
  "Rooms": [
    {
      "Id": "guard_room",
      "Theme": "Dungeon Cellar",
      "Width": 8,
      "Depth": 10,
      "OriginX": 0,
      "OriginZ": 0,
      "OriginY": 0,
      "Notes": "Entry guard post"
    },
    {
      "Id": "treasury",
      "Theme": "Dungeon Cellar",
      "Width": 12,
      "Depth": 12,
      "OriginX": 14,
      "OriginZ": 0,
      "OriginY": 0,
      "Notes": "Well-defended vault"
    }
  ],
  "Connections": [
    {
      "FromRoomId": "guard_room",
      "ToRoomId":   "treasury",
      "WallSide":   "east",
      "Offset":     3
    },
    {
      "FromRoomId": "treasury",
      "ToRoomId":   "guard_room",
      "WallSide":   "west",
      "Offset":     3
    }
  ]
}
```

---

## Tips for the AI

- Keep rooms separated by at least 2 tiles so the corridor floor has room to render.
- Orient connections so `WallSide` on the source room faces toward the destination room.
- Use OriginY multiples of 4 for multi-level layouts (stairs are not yet auto-placed).
- For large layouts: use a 60×60 bounding box and keep room widths between 6 and 18 tiles.
- Paste **only the raw JSON** — no markdown fences, no prose before or after.
