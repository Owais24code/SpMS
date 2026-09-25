#!/usr/bin/env python3
"""
Rebuilds backend/vendor/nuget: the offline package feed NuGet.offline.config reads.

It copies exactly what a successful restore resolved, and nothing it guessed:
every package listed in the restore's obj/project.assets.json files, plus the
local tools pinned in dotnet-tools.json. (The previous version walked .nuspec
dependency ranges and picked versions itself; it compared versions as strings
and took the lowest cached version of each root, so the feed it produced did
not restore.)

On a machine that can reach nuget.org:

    cd backend
    dotnet restore Spms.sln
    dotnet tool restore
    python3 tools/vendor-nuget.py                       # cache = ~/.nuget/packages
    python3 tools/vendor-nuget.py --cache D:/nuget --out vendor/nuget

It copies, never downloads, so it also works where nuget.org is blocked, as long
as the package cache from a successful restore is reachable.
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent.parent   # backend/


def resolved_packages(root: Path) -> set[tuple[str, str]]:
    found: set[tuple[str, str]] = set()
    assets = list(root.glob("src/*/obj/project.assets.json")) + list(root.glob("tests/*/obj/project.assets.json"))
    if not assets:
        raise SystemExit("No obj/project.assets.json found: run `dotnet restore Spms.sln` first.")
    for path in assets:
        libraries = json.loads(path.read_text(encoding="utf-8"))["libraries"]
        for key, lib in libraries.items():
            if lib.get("type") == "package":
                name, version = key.split("/", 1)
                found.add((name.lower(), version.lower()))
    manifest = next((p for p in (root / "dotnet-tools.json", root / ".config" / "dotnet-tools.json") if p.exists()), None)
    if manifest:
        for name, tool in json.loads(manifest.read_text(encoding="utf-8")).get("tools", {}).items():
            found.add((name.lower(), tool["version"].lower()))
    return found


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=str(Path.home() / ".nuget" / "packages"))
    ap.add_argument("--out", default=str(HERE / "vendor" / "nuget"))
    args = ap.parse_args()

    cache, out = Path(args.cache).expanduser(), Path(args.out)
    if not cache.is_dir():
        print(f"No NuGet cache at {cache}.", file=sys.stderr)
        return 1

    wanted = resolved_packages(HERE)
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    missing = []
    for name, version in sorted(wanted):
        source = cache / name / version / f"{name}.{version}.nupkg"
        if source.is_file():
            shutil.copy2(source, out / source.name)
        else:
            missing.append(f"{name} {version}")

    total = sum(f.stat().st_size for f in out.glob("*.nupkg"))
    print(f"{len(wanted) - len(missing)} packages, {total / 1e6:.1f} MB in {out}")
    if missing:
        print("Not in the cache (restore on a connected machine first):\n  " + "\n  ".join(missing), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
