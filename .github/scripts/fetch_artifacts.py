#!/usr/bin/env python3
"""Downloads an externally built release's artifacts so they can be mirrored onto this registry.

Sources are tried in order and the content is verified by verify_artifacts.py afterwards, so a
source that serves the wrong bytes fails the publish rather than poisoning the registry.
"""
import json
import os
import sys
import urllib.request
from pathlib import Path

manifest_path, staging = Path(sys.argv[1]), Path(sys.argv[2])
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
release = f"{manifest['version']}-r{manifest['revision']}"
repository = os.environ.get("GITHUB_REPOSITORY", "")

for platform, artifact in manifest["artifacts"].items():
    name = f"{manifest['name']}-{release}-{platform}.kspkg"
    target = staging / name
    sources = artifact.get("sources", [])
    failures = []

    for url in sources:
        try:
            with urllib.request.urlopen(url, timeout=120) as response:
                target.write_bytes(response.read())
            print(f"  fetched {name} from {url}")
            break
        except Exception as error:  # noqa: BLE001 - any failure just means "try the next source"
            failures.append(f"{url}: {error}")
    else:
        # Every source pointing back at this repository's own releases means the artifact was never
        # published in the first place, rather than a source being down. CI cannot recover from that:
        # it has no bytes to publish, and only whoever built the artifact does.
        own = repository and all(f"/{repository}/releases/download/" in url for url in sources)
        print(f"::error file={manifest_path}::could not download {name}:")

        for failure in failures:
            print(f"::error::  {failure}")

        if own:
            print(
                f"::error file={manifest_path}::{manifest['owner']}/{manifest['name']} {release} names "
                "only this registry's own releases as its source, and that release does not exist. "
                "An imported package's artifacts must be published before its manifest is merged — "
                "run `kpm-bot publish --registry . --artifacts <dir>` from the machine that imported it."
            )

        sys.exit(1)
