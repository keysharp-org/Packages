#!/usr/bin/env bash
# Gives every release manifest an artifact on this repository, and records the URL in the manifest.
#
# For a source-hosted package the artifact is *born* here: CI packs it from the merged tree and
# checks the hash reproduces what the manifest claims. For an externally built package the artifact
# is *mirrored* here after its hash is verified, so the registry stays self-sufficient even if the
# author's repository disappears.
set -euo pipefail

published=0

for manifest in packages/*/*/versions/*.json; do
	package_dir=$(dirname "$(dirname "$manifest")")
	read -r owner name version revision <<<"$(python3 -c "
import json
m = json.load(open('$manifest'))
print(m['owner'], m['name'], m['version'], m['revision'])
")"
	release="${version}-r${revision}"
	tag="pkg/${owner}/${name}/${release}"

	if gh release view "$tag" >/dev/null 2>&1; then
		continue # already published; releases are immutable, so there is nothing to update
	fi

	echo "▸ $owner/$name $release"
	staging="$RUNNER_TEMP/artifacts/$owner-$name-$release"
	rm -rf "$staging"
	mkdir -p "$staging"

	if [ -f "$package_dir/port.json" ]; then
		# Source-hosted: rebuild from the tree. The hash gate is in `kpm validate`, which already
		# ran on the pull request; packing again here is what actually produces the bytes.
		kpm pack "$package_dir" --revision "$revision" --out "$staging" >/dev/null
	else
		# Externally built: fetch from the sources the manifest lists and verify before mirroring.
		python3 .github/scripts/fetch_artifacts.py "$manifest" "$staging"
	fi

	# Verify what we are about to publish matches the manifest, whichever way it was obtained.
	python3 .github/scripts/verify_artifacts.py "$manifest" "$staging"

	gh release create "$tag" "$staging"/*.kspkg \
		--title "$owner/$name $release" \
		--notes "Artifacts for \`$owner/$name\` $version (revision $revision).

Published automatically from \`$package_dir\`. This release is immutable: a correction is published as a new revision."

	# Nothing is written back to the manifest: it already names this URL, because the tag and asset
	# name are fully determined by the release. That is what keeps a published manifest immutable
	# and lets the branch require pull requests without an exception for CI.
	published=$((published + 1))
done

echo "published=$published" >> "$GITHUB_OUTPUT"
echo "$published release(s) published"
