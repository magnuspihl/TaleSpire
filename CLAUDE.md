## This repository

A collection of independent [TaleSpire](https://talespire.com) mods. Each mod is its own folder,
builds to its own DLL, and is installed on its own — users pick the ones they want. Nothing is
shared at runtime; the sharing is all at build time.

```
Directory.Build.props        shared build settings + the TaleSpire/BepInEx references
TaleSpire.sln                every project, so `dotnet build` at the root builds them all
LineOfSight/                 mod: hides terrain a player cannot see
MapGen/                      mod: procedural map generation
tools/                       developer tools, not shipped to users
```

Per-mod guidance lives in that mod's own `CLAUDE.md`.

## Adding a mod

Name the folder and the `.csproj` after the mod — `YourMod/YourMod.csproj` — and add it to the
solution. The SDK then derives the assembly name and root namespace from the project file name,
so a mod with no extra dependencies needs nothing at all:

```xml
<Project Sdk="Microsoft.NET.Sdk">
</Project>
```

`net48`, `LangVersion 9`, and the TaleSpire and BepInEx references are inherited from
`Directory.Build.props` at the root. Add `PackageReference` entries for anything only that mod
needs (RadialUI, SetInjectionFlag, and so on).

Use the `org.talespire.plugins.<name>` GUID convention in `[BepInPlugin]`, and keep each mod's
GUID unique — BepInEx treats two different GUIDs as two different plugins and will load both.

## Building

`dotnet build -c Debug` at the repo root. Dependencies come from NuGet, so a fresh clone builds
with no local TaleSpire install and no absolute paths. Develop against `Debug` so there is only
one output folder to deploy from; `tools/package-mod.py` builds `Release` for published packages.

After any build the user must **fully restart TaleSpire** before testing. The DLL is loaded once
at launch and does not hot-reload — stale DLLs are a frequent source of apparent logic bugs.

BepInEx also does not reload its config files while the game is running, so a changed keybind or
setting needs a restart too.

## Releasing

Mods are published to [Thunderstore](https://thunderstore.io/c/talespire/), which is where the
mod managers people actually use (r2modman, Gale) install from. A package is a flat zip of
`manifest.json`, `icon.png`, `README.md`, `LICENSE` and the DLL, uploaded under the `Magnus` team.

To make a mod publishable, add `<Mod>/thunderstore/manifest.json` and a 256x256
`<Mod>/thunderstore/icon.png`. Dependencies in the manifest are full Thunderstore identifiers
(`Namespace-Name-Version`) and must name the versions that actually run on TaleSpire 1.6.0, which
are not always the ones the `.csproj` compiles against.

Build a package locally with:

```
python3 tools/package-mod.py LineOfSight
```

Releases are cut per mod, by pushing a tag named `<Mod>-v<version>`:

```
git tag LineOfSight-v1.0.0 && git push origin LineOfSight-v1.0.0
```

That runs `.github/workflows/release.yml`, which packages the mod and attaches the zip to a
GitHub release. Uploading to Thunderstore is still manual — a published version there cannot be
deleted or replaced.

The version lives in two places that must agree: `version_number` in the manifest and the
`[BepInPlugin]` attribute. The packaging script refuses to build if they disagree, or if they
disagree with the tag, because a package whose version differs from the one the game reports
sends players chasing bugs in a build they are not running.

## Reference data

`tile_catalog.json`, `tileset_profiles.json`, and the scripts that generate them
(`build_tile_catalog.py`, `build_tileset_profiles.py`) live in `/home/coder/TaleSpire-Binaries/`,
not in this repo. Do not read either JSON file into context — they are large reference data.
Query them with grep or python when specific tile or profile lookups are needed.
