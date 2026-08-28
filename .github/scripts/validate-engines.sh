#!/usr/bin/env bash
# Compile-checks every package changed by this pull request against one engine.
#
# A library often does not parse standalone and needs its dependencies present, so each package is
# checked through a real install and a real include — `kpm probe` lays that out — rather than by
# handing the engine a source file directly.
#
# This is a floor, not proof of behaviour: it says the package compiles, not that it works.
set -euo pipefail

engine="$1"
command="$2"
base="origin/${GITHUB_BASE_REF:-main}"

# Which packages this pull request touches. Both a source change and a new release manifest count.
packages=$(git diff --name-only "$base"...HEAD -- 'packages/*/*' \
	| sed -n 's|^\(packages/[^/]*/[^/]*\)/.*|\1|p' | sort -u)

if [ -z "$packages" ]; then
	echo "no package changes to check"
	exit 0
fi

# The runner's actual machine. Which artifact it gets is the registry's decision, not this
# script's: `kpm probe` resolves it through the same architecture -> OS -> any fallback an install
# uses, and fails if the package ships nothing this machine can take.
case "$RUNNER_OS" in
Linux) platform="linux-x64" ;;
Windows) platform="win-x64" ;;
macOS) platform="osx-arm64" ;;
esac

failed=0

for package in $packages; do
	if [ ! -f "$package/package.json" ]; then
		continue # deleted, or not a package directory
	fi

	# Only check what the package claims. A Keysharp-only package must not fail the AutoHotkey job.
	claims=$(python3 -c "
import json,sys,glob,os
port = os.path.join('$package','port.json')
if os.path.exists(port):
    print('keysharp' if 'keysharp' in json.load(open(port)).get('engines',{}) else '', end=' ')
    print('autohotkey' if 'autohotkey' in json.load(open(port)).get('engines',{}) else '')
else:
    engines=set()
    for f in glob.glob(os.path.join('$package','versions','*.json')):
        engines |= set(json.load(open(f)).get('engines',{}))
    print(' '.join(sorted(engines)))
")

	if [[ " $claims " != *" $engine "* ]]; then
		echo "· $package does not claim $engine, skipping"
		continue
	fi

	if [ ! -f "$package/port.json" ]; then
		echo "· $package is not source-hosted; its artifact was verified by the manifests job"
		continue
	fi

	# A package that ships no build this runner can take has nothing to check here. Asking kpm keeps
	# the fallback rule in one place instead of reimplementing it in shell.
	probe_dir="$RUNNER_TEMP/probe/$(echo "$package" | tr '/' '-')"

	if ! probe=$(kpm probe "$package" --out "$probe_dir" --registry . --engine "$engine" --platform "$platform"); then
		echo "· $package ships no build for $platform, skipping"
		continue
	fi
	echo "▸ $package"

	case "$engine" in
	keysharp) arguments=(/validate /errorstdout "$probe") ;;
	autohotkey) arguments=(/validate /ErrorStdOut "$probe") ;;
	esac

	# Switches must precede the script path: anything after it is an argument to the script.
	if "$command" "${arguments[@]}"; then
		echo "  ok"
	else
		echo "::error file=$package/port.json::$package does not compile under $engine on $platform"
		failed=1
	fi
done

exit $failed
