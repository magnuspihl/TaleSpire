## This repository

A collection of independent [TaleSpire](https://talespire.com) mods. Each mod is its own folder,
builds to its own DLL, and is installed on its own — users pick the ones they want. Nothing is
shared at runtime; the sharing is all at build time.

```
Directory.Build.props        shared build settings + the TaleSpire/BepInEx references
TaleSpire.sln                every project, so `dotnet build` at the root builds them all
TaleSpire-LineOfSight/       mod: hides terrain a player cannot see
TaleSpire-MapGen/            mod: procedural map generation
tools/                       developer tools, not shipped to users
```

Per-mod guidance lives in that mod's own `CLAUDE.md`.

## Adding a mod

Create a folder, add a `.csproj`, add it to the solution. The csproj only needs what is unique
to the mod:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>TaleSpire-YourMod</AssemblyName>
    <RootNamespace>YourMod</RootNamespace>
  </PropertyGroup>
</Project>
```

`net48`, `LangVersion 9`, and the TaleSpire and BepInEx references are inherited from
`Directory.Build.props` at the root. Add extra `PackageReference` entries for anything only that
mod needs (RadialUI, SetInjectionFlag, and so on).

Use the `org.talespire.plugins.<name>` GUID convention in `[BepInPlugin]`, and keep each mod's
GUID unique — BepInEx treats two different GUIDs as two different plugins and will load both.

## Building

`dotnet build -c Debug` at the repo root. Dependencies come from NuGet, so a fresh clone builds
with no local TaleSpire install and no absolute paths.

After any build the user must **fully restart TaleSpire** before testing. The DLL is loaded once
at launch and does not hot-reload — stale DLLs are a frequent source of apparent logic bugs.

BepInEx also does not reload its config files while the game is running, so a changed keybind or
setting needs a restart too.

## Reference data

`tile_catalog.json` and `build_tile_catalog.py` live in `/home/coder/TaleSpire-Binaries/`, not in
this repo. Do not read `tile_catalog.json` into context — it is large reference data. Query it
with grep or python when specific tile lookups are needed.
