#!/usr/bin/env python3
"""Build a mod and assemble its Thunderstore package.

    python3 tools/package-mod.py LineOfSight [--output dist]

Produces dist/<Mod>-<version>.zip, laid out the way Thunderstore expects: manifest.json,
icon.png, README.md and the DLL all flat at the root of the archive.
"""

import argparse
import json
import re
import struct
import subprocess
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CONFIGURATION = "Release"


def fail(message):
    sys.exit(f"error: {message}")


def normalise(version):
    """Thunderstore versions are strictly major.minor.patch; BepInPlugin often has a 4th part."""
    parts = version.split(".")
    if len(parts) < 3 or not all(p.isdigit() for p in parts):
        fail(f"'{version}' is not a valid version")
    return ".".join(parts[:3])


def declared_plugin_versions(mod_dir):
    """Every version literal the mod's BepInPlugin attribute could be using."""
    found = set()
    for source in mod_dir.rglob("*.cs"):
        if {"obj", "bin"} & set(source.parts):
            continue
        text = source.read_text(encoding="utf-8", errors="replace")
        for attribute in re.findall(r"BepInPlugin\s*\(([^)]*)\)", text):
            found.update(re.findall(r'"([\d.]+)"', attribute))
        found.update(re.findall(r'\bVersion\s*=\s*"([\d.]+)"', text))
    return {normalise(v) for v in found if v.count(".") >= 2}


def check_icon(icon):
    """Thunderstore rejects anything but a 256x256 PNG, and only at upload time."""
    header = icon.read_bytes()[:24]
    if header[:8] != b"\x89PNG\r\n\x1a\n":
        fail(f"{icon.name} is not a PNG")
    width, height = struct.unpack(">II", header[16:24])
    if (width, height) != (256, 256):
        fail(f"{icon.name} must be 256x256, not {width}x{height}")


def read_manifest(mod_dir, mod):
    path = mod_dir / "thunderstore" / "manifest.json"
    if not path.exists():
        fail(f"{path.relative_to(REPO)} is missing — {mod} is not set up for publishing yet")
    manifest = json.loads(path.read_text())
    for field in ("name", "version_number", "description", "dependencies"):
        if field not in manifest:
            fail(f"{path.relative_to(REPO)} has no '{field}'")
    if len(manifest["description"]) > 250:
        fail("Thunderstore descriptions are limited to 250 characters")
    return manifest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mod")
    parser.add_argument("--output", default="dist")
    parser.add_argument("--expect-version", help="fail unless the manifest declares this version")
    args = parser.parse_args()

    mod = args.mod
    mod_dir = REPO / mod
    if not (mod_dir / f"{mod}.csproj").exists():
        fail(f"no such mod: {mod}")

    manifest = read_manifest(mod_dir, mod)
    version = normalise(manifest["version_number"])
    manifest["version_number"] = version

    if args.expect_version and normalise(args.expect_version) != version:
        fail(f"tag says {normalise(args.expect_version)} but the manifest says {version}")

    # A package whose version disagrees with the one the game reports is how players end up
    # bug-reporting against a build they are not running.
    declared = declared_plugin_versions(mod_dir)
    if declared and version not in declared:
        fail(f"manifest says {version} but BepInPlugin declares {', '.join(sorted(declared))}")

    icon = mod_dir / "thunderstore" / "icon.png"
    readme = mod_dir / "README.md"
    for required in (icon, readme):
        if not required.exists():
            fail(f"{required.relative_to(REPO)} is required in the package")
    check_icon(icon)

    subprocess.run(
        ["dotnet", "build", mod, "-c", CONFIGURATION, "--nologo"], cwd=REPO, check=True
    )
    dll = mod_dir / "bin" / CONFIGURATION / f"{mod}.dll"
    if not dll.exists():
        fail(f"build produced no {dll.relative_to(REPO)}")

    out_dir = REPO / args.output
    out_dir.mkdir(parents=True, exist_ok=True)
    package = out_dir / f"{mod}-{version}.zip"

    with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("manifest.json", json.dumps(manifest, indent=2) + "\n")
        zf.write(icon, "icon.png")
        zf.write(readme, "README.md")
        zf.write(dll, dll.name)

    print(f"{package.relative_to(REPO)}  ({package.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
