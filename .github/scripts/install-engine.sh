#!/usr/bin/env bash
# Installs an engine for compile-checking and prints the command that runs it.
#
# Engines are fetched from their own releases rather than built here: CI validates packages against
# what users actually run, and building an engine per pull request would dominate the run time.
set -euo pipefail

engine="$1"
destination="$2"
mkdir -p "$destination"

case "$engine" in
keysharp)
	case "$RUNNER_OS" in
	Linux) asset_pattern='*linux-x64*' ;;
	Windows) asset_pattern='*win-x64*' ;;
	macOS) asset_pattern='*osx-arm64*' ;;
	*) echo "::error::unsupported runner $RUNNER_OS"; exit 1 ;;
	esac

	gh release download --repo "${KEYSHARP_REPOSITORY:-Descolada/Keysharp}" \
		--pattern "$asset_pattern" --dir "$destination" --clobber

	archive=$(find "$destination" -maxdepth 1 -type f | head -1)
	case "$archive" in
	*.zip) unzip -q -o "$archive" -d "$destination" ;;
	*.tar.gz | *.tgz) tar -xzf "$archive" -C "$destination" ;;
	*) echo "::error::unrecognized archive $archive"; exit 1 ;;
	esac

	binary=$(find "$destination" -type f \( -name 'Keysharp' -o -name 'Keysharp.exe' \) | head -1)
	if [ -z "$binary" ]; then
		echo "::error::no Keysharp binary in the downloaded release"
		exit 1
	fi
	chmod +x "$binary" || true
	echo "command=$binary" >> "$GITHUB_OUTPUT"
	;;

autohotkey)
	# Windows only, and deliberately a pinned version: "whatever is newest" would make a package's
	# recorded compatibility depend on the day CI ran.
	version="${AHK_VERSION:-2.0.18}"
	curl -sSL -o "$destination/ahk.zip" "https://www.autohotkey.com/download/2.0/AutoHotkey_${version}.zip"
	unzip -q -o "$destination/ahk.zip" -d "$destination"
	echo "command=$destination/AutoHotkey64.exe" >> "$GITHUB_OUTPUT"
	;;

*)
	echo "::error::unknown engine $engine"
	exit 1
	;;
esac
