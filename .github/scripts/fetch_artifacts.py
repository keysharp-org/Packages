#!/usr/bin/env python3
"""Downloads an externally built release's artifacts so they can be mirrored onto this registry.

Sources are tried in order and the content is verified by verify_artifacts.py afterwards, so a
source that serves the wrong bytes fails the publish rather than poisoning the registry.
"""
import json
import sys
import urllib.request
from pathlib import Path

manifest_path, staging = Path(sys.argv[1]), Path(sys.argv[2])
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
release = f"{manifest['version']}-r{manifest['revision']}"

for platform, artifact in manifest["artifacts"].items():
    name = f"{manifest['name']}-{release}-{platform}.kspkg"
    target = staging / name
    failures = []

    for url in artifact.get("sources", []):
        try:
            with urllib.request.urlopen(url, timeout=120) as response:
                target.write_bytes(response.read())
            print(f"  fetched {name} from {url}")
            break
        except Exception as error:  # noqa: BLE001 - any failure just means "try the next source"
            failures.append(f"{url}: {error}")
    else:
        print(f"::error file={manifest_path}::could not download {name}:")
        for failure in failures:
            print(f"::error::  {failure}")
        sys.exit(1)
