#!/usr/bin/env python3
"""Checks staged .kspkg files against the release manifest that describes them.

Two checks. The hash is the artifact's identity, so nothing is published under a name whose bytes
were not verified. And the copy of the metadata *inside* the archive must agree with the manifest
outside it — the archive carries its own package.json so a package is self-describing offline, and a
duplicate that nobody compares is a duplicate that silently drifts.
"""
import hashlib
import json
import sys
import zipfile
from pathlib import Path

manifest_path, staging = Path(sys.argv[1]), Path(sys.argv[2])
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
release = f"{manifest['version']}-r{manifest['revision']}"
problems = []

for platform, artifact in manifest["artifacts"].items():
    name = f"{manifest['name']}-{release}-{platform}.kspkg"
    path = staging / name

    if not path.exists():
        problems.append(f"{name} is missing")
        continue

    data = path.read_bytes()
    actual = hashlib.sha256(data).hexdigest()

    if actual != artifact["sha256"]:
        problems.append(
            f"{name} hashes to {actual}, manifest records {artifact['sha256']}"
        )
        continue

    if len(data) != artifact["size"]:
        problems.append(f"{name} is {len(data)} bytes, manifest records {artifact['size']}")

    try:
        with zipfile.ZipFile(path) as archive:
            embedded = json.loads(archive.read("package.json"))
    except (KeyError, zipfile.BadZipFile, json.JSONDecodeError) as error:
        problems.append(f"{name} has no readable package.json inside it ({error})")
        continue

    if embedded.get("platform") != platform:
        problems.append(
            f"{name} is the '{platform}' artifact but describes itself as "
            f"'{embedded.get('platform')}'"
        )

    for field in ("name", "owner", "version", "revision", "entry", "engines", "dependencies"):
        inside, outside = embedded.get(field), manifest.get(field)

        # An empty mapping and an absent one mean the same thing; only a real disagreement counts.
        if (inside or None) != (outside or None):
            problems.append(
                f"{name} records {field}={json.dumps(inside)} inside the archive but "
                f"{json.dumps(outside)} in its manifest"
            )

if problems:
    for problem in problems:
        print(f"::error file={manifest_path}::{problem}")
    sys.exit(1)

print(f"  verified {len(manifest['artifacts'])} artifact(s)")
