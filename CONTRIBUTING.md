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

No binaries under `src/`. Native payloads belong in `native/<platform>/` and only ever reach
release assets.

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
