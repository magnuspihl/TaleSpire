# TaleSpire mods

Mods for [TaleSpire](https://talespire.com), built on [BepInEx](https://github.com/BepInEx/BepInEx).

Each mod is independent: it ships as a single DLL, has its own settings, and works on its own.
Install only the ones you want.

| Mod | What it does |
| --- | --- |
| [LineOfSight](LineOfSight/) | Hides terrain a player's creature cannot see, with optional fog-of-war memory |
| [MapGen](MapGen/) | Generates dungeon layouts as pasteable slabs |

## Installing

Install BepInEx 5.4.x, then drop the mod's DLL into `BepInEx/plugins/`. Check the mod's own page
for anything else it needs — some depend on other plugins.

Every client that should be affected needs the mod installed; a player without it sees the game
as normal.

## Building from source

Requires the .NET SDK. Dependencies come from NuGet, so no local TaleSpire installation is
needed.

```
dotnet build -c Debug
```

That builds every mod. Each one lands in its own `bin/Debug/` folder. To build just one, point
at its project:

```
dotnet build LineOfSight -c Debug
```

Shared build settings live in `Directory.Build.props` at the root, so an individual mod's
`.csproj` contains only what is unique to it. See [CLAUDE.md](CLAUDE.md) for how to add a mod.
