# Contributing

Everything is a pull request. CI checks it, a maintainer reviews it, and merging publishes it.

Install [KPM](https://github.com/keysharp-org/KPM) first — every check below runs locally with the
same code CI uses, so you can see the answer before you push.

## Publishing a package you maintain elsewhere

Your library stays in your repository; only its metadata comes here.

1. Build the artifact and note its hash:

   ```
   kpm pack path/to/your/package
   ```

2. Publish that `.kspkg` as a release asset on **your** repository.
3. Add `packages/<owner>/<name>/package.json` and `packages/<owner>/<name>/versions/<version>-r1.json`
   here, with the hash from step 1 and your release URL in `sources`.
4. Open a pull request.

On merge, CI downloads the artifact, verifies it against your hash, and mirrors it onto this
repository's releases so the registry does not depend on your repository staying up.

## Adding a source-hosted package (a "port")

For a library with no upstream repository of its own — a Keysharp port of an AutoHotkey script, or
something written for Keysharp directly. Its source is maintained here.

```
packages/<owner>/<name>/
├── package.json
├── port.json
├── src/…
├── versions/<version>-r1.json
└── README.md
```

Write `package.json` and `port.json`, put the source under `src/`, then generate the release
manifest — never write hashes by hand:

```
kpm manifest packages/<owner>/<name>
kpm validate .
kpm probe packages/<owner>/<name> --out /tmp/probe
Keysharp.exe /validate /errorstdout /tmp/probe/probe.ks
```

`kpm manifest` packs the source and records the resulting hashes. CI re-packs from your merged tree
and requires the same hash, which is what lets anyone verify the artifact without trusting whoever
built it. If you change anything under `src/`, bump `port.json`'s `version` and regenerate — CI
rejects a source change without one.

No binaries under `src/`. Native payloads belong in `native/` (or `platform/<rid>/native/`) and only
ever reach release assets.

## Shipping different code per platform

There is one rule. A package's content lives in `src/` (scripts) and `native/` (binaries), and
those two names mean two things depending on where they sit:

- **at the top of the package** — goes into *every* artifact;
- **under `platform/<rid>/`** — goes into *that* artifact alone.

The `<rid>` is a selector for the repository and never appears in the archive, so a script always
refers to `src/Engine.ahk` and `native/foo.dll` by the same path, whichever platform it is running
on and whichever artifact it came from.

The same path may not come from both places. `src/Engine.ahk` together with
`platform/linux-x64/src/Engine.ahk` is an error, not a silent win for one of them, because
otherwise a reader looking at `src/Engine.ahk` would have no way to tell it is replaced on Linux.
If a file differs per platform, every platform names its own copy — including the portable `any`
build:

```
packages/Descolada/OCR/
├── src/
│   └── OCR.ahk                     shared API — #include "Engine.ahk"
└── platform/
    ├── any/src/Engine.ahk          Windows engine; also what AutoHotkey resolves
    └── linux-x64/src/Engine.ahk    Tesseract engine
```

Note that `src`, `native` and `platform` are reserved only at the *top* of a package directory,
never inside `src/`. A package is free to have its own `src/Native/` or `src/platform/` — and one
in this registry does.

```json
"platforms": ["any", "linux-x64"],
"engines": { "autohotkey": ">=2.0", "keysharp": ">=0.0.0.17" }
```

AutoHotkey and Keysharp on Windows resolve the `any` artifact and get the Windows engine; Keysharp
on Linux resolves `linux-x64` and gets the Tesseract one. Only one `Engine.ahk` is ever in an
artifact, so nobody downloads or parses the other platform's code.

**Use this rather than compile-time platform branches.** Keysharp has an `#if WINDOWS` / `#if LINUX`
preprocessor and it is fine for a Keysharp-only package, but AutoHotkey rejects those lines outright
(`This line does not contain a recognized action`), so a package that uses them can never claim the
`autohotkey` engine. The overlay keeps every file plain, portable script and moves the branch to
packing time, which is what lets one package serve both engines.

Two limits worth knowing: the unit is a whole file, not part of one — a platform that differs in one
function needs its own copy of that file; and dependencies are declared per release, not per
platform, so a package needed only by the Linux code is installed for everyone.

## Updating a package

- **The library changed** → new version. `0.4.0` → `0.5.0`.
- **Only the packaging was wrong** (missing file, wrong dependency, bad archive) → new revision.
  `0.4.0-r1` → `0.4.0-r2`. Add `versions/0.4.0-r2.json`; leave `-r1` alone.

Never edit or delete a published `versions/*.json`. CI rejects it, and an existing lockfile depends
on it staying what it was.

## Declaring engines

```json
"engines": { "keysharp": ">=0.0.0.17" }
```

Claim only what you have tested. CI compile-checks every claim on every platform you list and fails
the pull request if one does not hold. Note the two grammars, which are deliberately different:
`engines` uses `#Requires`-style comparisons (`>=0.0.0.17`), `dependencies` uses npm-style SemVer
ranges (`^1.4.0`).

Most imported packages declare `autohotkey` only. Adding a `keysharp` claim to one is a welcome
contribution — open a pull request that adds it and let CI decide.

## Licensing

Record the licence the source actually states. If it states none — common for forum scripts — use
`NOASSERTION`. Do not guess, and do not apply your own licence to someone else's code. Porting a
script does not change who owns it: credit the original author in the package README and record the
upstream in `port.json`.

## Review

A maintainer reviews every pull request. Automated imports are labelled `bot` and reviewed the same
way, since a forum edit that only fixes a typo should not become a release.
