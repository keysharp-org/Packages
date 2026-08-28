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

# The engines are desktop applications, and Keysharp on Linux will not start without a reachable
# display server — not even to compile, and not even to print its version. A virtual display keeps a
# compile-only check from requiring a desktop.
if [ "$RUNNER_OS" = Linux ] && [ -z "${DISPLAY:-}" ]; then
	if ! command -v xvfb-run > /dev/null; then
		echo "::error::no display server and no xvfb-run; the engine cannot start here"
		exit 1
	fi
	virtual_display=1
fi

# One-shot invocations. Keysharp's compile daemon outlives the launcher and inherits whatever handles
# it was given, and a step whose stdout a live child still holds never reaches EOF — the job then
# hangs long after the compile finished.
export KEYSHARP_DAEMON=0

# Both engines take their switches with a leading slash, and the Git Bash behind `shell: bash` on
# Windows rewrites any such argument into a path: /validate arrived as C:\Program Files\Git\validate,
# Keysharp took that for the script it was asked to run, and reported the missing file through a
# modal dialog nobody can close on a runner. The job sat there until killed, having printed nothing.
# Ignored by the other two platforms' shells.
export MSYS2_ARG_CONV_EXCL='*'

# A compile takes a couple of seconds; this only has to be shorter than a runner's patience.
engine_timeout_seconds=180
engine_log="$RUNNER_TEMP/engine-output.log"

# macOS ships BSD userland and no GNU coreutils, so there is no `timeout` there. It is a safety net
# rather than a requirement — bounding a wedged engine — so go without it rather than install one.
with_timeout() {
	if command -v timeout > /dev/null; then
		timeout "$engine_timeout_seconds" "$@"
	else
		"$@"
	fi
}

# The engine writes to a file rather than to this step's stdout, and reads stdin from /dev/null, so
# that a child holding those handles cannot wedge the run.
run_engine() {
	local status=0

	if [ -n "${virtual_display:-}" ]; then
		with_timeout xvfb-run -a "$@" > "$engine_log" 2>&1 < /dev/null || status=$?
	else
		with_timeout "$@" > "$engine_log" 2>&1 < /dev/null || status=$?
	fi

	cat "$engine_log"

	# timeout's own code for "it was still running when I killed it".
	if [ "$status" -eq 124 ]; then
		echo "::error::$engine did not return within ${engine_timeout_seconds}s, and printed what is above"
		echo "::error::before stopping. Keysharp reports some failures through a modal dialog, which on a"
		echo "::error::runner with no desktop waits for a click that never comes."
	fi

	return "$status"
}

# The switches an engine takes to compile a file without running it. Switches must precede the
# script path: anything after it is an argument to the script.
compile_check() {
	case "$engine" in
	keysharp) run_engine "$command" /validate /errorstdout "$1" ;;
	autohotkey) run_engine "$command" /validate /ErrorStdOut "$1" ;;
	esac
}

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

	if compile_check "$probe"; then
		echo "  ok"
	else
		echo "::error file=$package/port.json::$package does not compile under $engine on $platform"
		failed=1
	fi
done

exit $failed
