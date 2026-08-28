# Keysharp Packages

The package registry for [Keysharp](https://github.com/Descolada/Keysharp) and AutoHotkey script
libraries. Metadata lives in this repository; artifacts live in its GitHub releases; the index
clients download is published to <https://packages.keysharp.org>.

Install things with [KPM](https://github.com/keysharp-org/KPM):

```
kpm add Keysharp/FindText
kpm add FindText            # a bare name works when only one package has it
```

## The registry is client-neutral

Nothing here requires KPM. The public contract is:

| Piece | Where |
|---|---|
| Schemas for every file kind | [`schemas/`](schemas/) |
| The whole registry, one document | `https://keysharp-org.github.io/Packages/index.json.gz` |
| Browsing view, newest release per package | `https://keysharp-org.github.io/Packages/catalog.json` |
| Artifacts, addressed by SHA-256 | this repository's releases |

The index host is client configuration and is never written into a lockfile — lockfiles pin artifact
URLs and hashes — so moving the index to a custom domain later costs a client release and breaks
nothing already published.

KPM is the reference implementation of that contract — this repository's own CI uses it — not the
protocol. Another client, an editor plugin, or a script fetching `catalog.json` consumes the same
thing. Clients read only the published index, never this file tree, so the layout below can change
without breaking anything installed.

Reading the registry needs **no token and no API**: the index and the artifacts are static files
over HTTPS. That is deliberate — rate limits and API outages must never be a package manager's
problem.

## Layout

```
packages/<owner>/<name>/
├── package.json          the package-level record; the only file here that may be edited later
├── versions/
│   ├── 0.1.0-r1.json     one immutable release each
│   └── 0.2.0-r1.json
├── port.json             ┐ source-hosted packages only
├── src/                  ┘ (see below)
└── README.md
```

An id is `Owner/Name`, and the directory path *is* the identity — casing included. Ids keep the
casing their author uses (`Descolada/OCR`, `thqby/child_process`), because these are people's names
and their libraries' names. Comparison is case-insensitive everywhere, so `kpm add descolada/ocr`
finds it and the registry refuses two ids that differ only by case — which would be one directory on
Windows and macOS.

## Two kinds of package

**Source-hosted** ("ports") keep their maintained source right here, under `src/`, with a
`port.json`. These are libraries with no upstream repository of their own — a Keysharp port of an
AutoHotkey script, or something written for Keysharp directly. One pull request ships the source
change, the version bump and the release manifest together, and CI rebuilds the artifact from the
merged tree to check it matches the hash the manifest claims.

**Externally published** packages live in their author's own repository and only their metadata is
here. Their artifacts are verified against the recorded hash and then mirrored onto this
repository's releases, so the registry keeps working even if an upstream repository disappears.

## Versions and revisions

A release is `<version>-r<revision>`. The version describes the library; the revision counts
repackagings of that same source — a wrong dependency, a missing file, a bad archive. Only the
version is normally shown to users, because bumping it for a packaging mistake would claim the
library changed when it did not.

Packages whose upstream publishes versions keep those numbers (`versioning: "upstream"`). Packages
without them — forum scripts, untagged repositories — get a synthesized `0.x` series
(`versioning: "registry"`), minor-bumped per upstream change and never promoted to `1.0` by the
registry. A `0.x` number promises no compatibility, which is exactly the truth about a script whose
author never versioned it.

**Published releases are immutable.** A `versions/*.json` file is never edited or deleted once
merged, and CI rejects a pull request that tries. Corrections are new revisions. Withdrawing a bad
release is a `yanked` entry in `package.json`, which stops new resolutions from choosing it while
letting an existing lockfile keep installing.

## Engines and platforms are earned, not declared

Every release declares which engines it runs on, and CI compile-checks each claim on each platform
the package lists. A claim that does not pass is not merged.

This matters because most of the imported corpus is AutoHotkey code that has never been checked
against Keysharp. Those packages declare `autohotkey` only, and KPM's Keysharp client filters them
out rather than offering a catalog that is mostly broken. A package earns its `keysharp` claim by
passing validation — or by someone porting it, which makes it a source-hosted package here.

Compile-checking is a floor, not proof of behaviour: it says the package parses and compiles, not
that it works.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licensing and takedowns

Each package records its own licence. `NOASSERTION` means the source states no licence — common for
forum scripts — and is recorded honestly rather than guessed.

The registry hosts artifacts for imported packages regardless, with provenance and author credit in
every release manifest, which is what ScriptHub and Aris already do for this same corpus. **If you
are an author and want your package removed, open an issue and it will be taken down** — no
justification needed. Equally, if you want it kept and maintained, claim it (below).

## Claiming a package

Packages imported under your name are held by the organisation until you claim them. Open a pull
request adding a line to [CODEOWNERS](CODEOWNERS):

```
/packages/YourName/  @your-github-handle
```

Once merged you review changes to your own packages. Claiming also lets you correct the licence, the
description, or anything else in `package.json`.
