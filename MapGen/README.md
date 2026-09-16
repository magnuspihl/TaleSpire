# TaleSpire MapGen

Procedural dungeon map generator for TaleSpire. Press **F8** to open the generator window, configure your layout, and paste the resulting slab directly onto your board.

## Features

- Generates rooms, corridors, and multi-floor dungeon layouts
- Outputs a pasteable TaleSpire slab — no board editing required
- Supports multiple tilesets via bundled tileset profiles
- Sidecar `tileset_profiles.json` next to the DLL overrides the bundled profiles for updates without a reinstall

## Installation

Install via r2modman or Gale, or drop `MapGen.dll` into your `BepInEx/plugins` folder.

## Usage

1. Load a TaleSpire board
2. Press **F8** to open the MapGen window
3. Configure room count, size, and tileset
4. Click **Generate** and paste the slab onto your board

## Keybinds

| Key | Action |
|-----|--------|
| F8  | Toggle MapGen window |

The keybind is configurable in `BepInEx/config/org.talespire.plugins.mapgen.cfg`.
