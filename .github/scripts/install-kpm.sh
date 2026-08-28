#!/usr/bin/env bash
# Puts a released kpm on the PATH.
#
# A released binary rather than a build of KPM's main branch, for two reasons. Building main meant
# the registry validated submissions with whatever had just been pushed there, so an unrelated
# change could break every open pull request with no way to say which version the registry runs.
# And downloading the same archive a user downloads means every registry run exercises the real
# release artifact — the alternative is binaries that go untested until someone reports them broken,
# which is exactly how a broken installer survived in a neighbouring project.
set -euo pipefail

version="${KPM_VERSION:?KPM_VERSION must be set to a released tag, e.g. v0.1.0}"
repository="${KPM_REPOSITORY:-keysharp-org/KPM}"
destination="${1:-$RUNNER_TEMP/kpm}"
mkdir -p "$destination"

case "$RUNNER_OS" in
Linux) pattern='*linux-x64.tar.gz' ;;
Windows) pattern='*win-x64.zip' ;;
macOS) pattern='*osx-arm64.tar.gz' ;;
*) echo "::error::unsupported runner $RUNNER_OS"; exit 1 ;;
esac

gh release download "$version" --repo "$repository" \
	--pattern "$pattern" --pattern SHA256SUMS --dir "$destination" --clobber

archive=$(find "$destination" -maxdepth 1 -type f ! -name SHA256SUMS | head -1)

# The registry verifies every package artifact by hash; the tool doing the verifying deserves the
# same treatment. SHA256SUMS lists names as ./name, so compare the one line that matters.
expected=$(grep -F "$(basename "$archive")" "$destination/SHA256SUMS" | awk '{print $1}')
actual=$(sha256sum "$archive" | awk '{print $1}')
if [ "$expected" != "$actual" ]; then
	echo "::error::$(basename "$archive") hashes to $actual, SHA256SUMS says $expected"
	exit 1
fi

case "$archive" in
*.zip) unzip -q -o "$archive" -d "$destination" ;;
*.tar.gz) tar -xzf "$archive" -C "$destination" ;;
esac

chmod +x "$destination/kpm" 2>/dev/null || true
echo "$destination" >> "$GITHUB_PATH"
echo "kpm $version ready ($(basename "$archive"))"
