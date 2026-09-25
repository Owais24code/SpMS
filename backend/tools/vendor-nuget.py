#!/usr/bin/env python3
"""
Rebuilds backend/vendor/nuget: the offline package feed NuGet.offline.config reads.

Why this is a script and not a list. A NuGet feed with a missing transitive
dependency does not warn; the restore fails with NU1101 naming only the FIRST
package it could not find, so assembling the closure by hand is a loop of
"restore, read one name, copy one file" that takes as many passes as the graph
is deep. This walks the graph instead, reading each .nuspec's <dependencies>
for the target framework and following them, so one pass produces a feed that
restores.

It needs a machine that already has the packages in its NuGet cache — run a
normal `dotnet restore` first, then this. It copies, never downloads, so it
works on a machine whose route to nuget.org has since been closed.

    python3 tools/vendor-nuget.py                 # from backend/
    python3 tools/vendor-nuget.py --cache ~/.nuget/packages --out vendor/nuget

Version selection: a dependency range like [8.0.0, ) is satisfied by whatever
the cache holds, lowest first, matching NuGet's own preference. That is why the
build tolerates NU1603 — see the note in Directory.Build.props.
"""

from __future__ import annotations

import argparse
import re
import shutil
import sys
import zipfile
from pathlib import Path
from xml.etree import ElementTree

# The roots. Everything else is discovered by walking dependencies.
ROOTS = [
    "microsoft.entityframeworkcore",
    "microsoft.entityframeworkcore.relational",
    "npgsql",
    "npgsql.entityframeworkcore.postgresql",
    "microsoft.aspnetcore.authentication.jwtbearer",
    "microsoft.net.test.sdk",
    "xunit",
    "xunit.runner.visualstudio",
    # modular monolith + authorization (added with the R1 schema)
    "microsoft.entityframeworkcore.design",
    "openfga.sdk",
    "dotnet-ef",
]


def versions(cache: Path, package: str) -> list[str]:
    folder = cache / package
    if not folder.is_dir():
        return []
    # Numeric-aware, so 10.0.0 sorts after 9.0.0 rather than before it.
    def key(name: str) -> tuple:
        return tuple(int(p) if p.isdigit() else 0 for p in re.split(r"[.\-+]", name)[:4])
    return sorted((d.name for d in folder.iterdir() if d.is_dir()), key=key)


def nupkg(cache: Path, package: str, version: str) -> Path | None:
    for candidate in (cache / package / version).glob("*.nupkg"):
        return candidate
    return None


def dependencies(path: Path) -> list[tuple[str, str]]:
    """Every dependency in the .nuspec, across all target frameworks.

    Taking the union rather than one framework's group is deliberate: the feed
    has to satisfy whichever framework the consuming project resolves to, and a
    few extra .nupkg files cost less than a failed restore.
    """
    with zipfile.ZipFile(path) as z:
        spec = next((n for n in z.namelist() if n.endswith(".nuspec")), None)
        if spec is None:
            return []
        xml = z.read(spec).decode("utf-8", "replace")

    # The nuspec namespace varies by schema version, so it is stripped rather
    # than matched.
    xml = re.sub(r'\sxmlns="[^"]+"', "", xml, count=1)
    try:
        root = ElementTree.fromstring(xml)
    except ElementTree.ParseError:
        return []

    found: list[tuple[str, str]] = []
    for dep in root.iter("dependency"):
        name = dep.get("id")
        if name:
            found.append((name.lower(), (dep.get("version") or "").strip("[]()<>= ").split(",")[0]))
    return found


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=str(Path.home() / ".nuget" / "packages"))
    ap.add_argument("--out", default="vendor/nuget")
    args = ap.parse_args()

    cache, out = Path(args.cache).expanduser(), Path(args.out)
    if not cache.is_dir():
        print(f"No NuGet cache at {cache}. Run a normal restore first.", file=sys.stderr)
        return 1

    out.mkdir(parents=True, exist_ok=True)

    pending = [(r, None) for r in ROOTS]
    done: set[tuple[str, str]] = set()
    missing: list[str] = []

    while pending:
        package, wanted = pending.pop()
        available = versions(cache, package)
        if not available:
            missing.append(package)
            continue

        # Lowest version at or above the floor, which is what NuGet itself
        # would pick. Falling back to the newest available keeps a package with
        # only a higher version in the cache usable.
        chosen = next((v for v in available if wanted is None or v >= wanted), available[-1])
        if (package, chosen) in done:
            continue
        done.add((package, chosen))

        source = nupkg(cache, package, chosen)
        if source is None:
            missing.append(f"{package} {chosen} (no .nupkg in the cache — extracted only)")
            continue

        shutil.copy2(source, out / source.name)
        pending.extend(dependencies(source))

    total = sum(f.stat().st_size for f in out.glob("*.nupkg"))
    print(f"{len(list(out.glob('*.nupkg')))} packages, {total / 1e6:.1f} MB in {out}")

    if missing:
        print("\nNot in the cache — restore these on a connected machine first:", file=sys.stderr)
        for m in sorted(set(missing)):
            print(f"  {m}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
