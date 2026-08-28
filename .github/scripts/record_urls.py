#!/usr/bin/env python3
"""Prepends this registry's own release URL to each artifact's source list.

The registry's copy goes first so installs come from here by default; any build-origin URL stays
behind it as a mirror. Editing a published manifest is normally forbidden, but `sources` is not
identity — the sha256 is — so adding a place to fetch identical bytes changes nothing a lockfile
depends on.
"""
import json
import sys
from pathlib import Path

manifest_path, repository, tag, staging = (
    Path(sys.argv[1]),
    sys.argv[2],
    sys.argv[3],
    Path(sys.argv[4]),
)
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
release = f"{manifest['version']}-r{manifest['revision']}"
changed = False

for platform, artifact in manifest["artifacts"].items():
    name = f"{manifest['name']}-{release}-{platform}.kspkg"
    url = f"https://github.com/{repository}/releases/download/{tag}/{name}"
    sources = artifact.setdefault("sources", [])

    if url in sources:
        continue

    sources.insert(0, url)
    changed = True

if changed:
    manifest_path.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"  recorded release URLs in {manifest_path}")
