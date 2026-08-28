#!/usr/bin/env python3
"""Checks staged .kspkg files against the hashes their release manifest records.

Runs whether the artifact was packed here or downloaded from elsewhere: the hash is the artifact's
identity, so nothing gets published under a name whose bytes were not verified first.
"""
import hashlib
import json
import sys
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
    elif len(data) != artifact["size"]:
        problems.append(f"{name} is {len(data)} bytes, manifest records {artifact['size']}")

if problems:
    for problem in problems:
        print(f"::error file={manifest_path}::{problem}")
    sys.exit(1)

print(f"  verified {len(manifest['artifacts'])} artifact(s)")
