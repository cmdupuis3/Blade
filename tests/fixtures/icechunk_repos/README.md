# icechunk_repos/

Generated Icechunk fixture repos for `blade test icechunk` (see
`tests/IcechunkTests.fs`).

Every repo in here is written on the fly by `Blade.IcechunkWrite.writeRepo`
(`src/providers/IcechunkWrite.fs`) at the start of the relevant test section —
pure .NET file writes through the vendored FlatBuffers object API plus
ZstdSharp, with no external tool and no icechunk-python in the loop. Nothing
here is committed or hand-maintained, and the whole directory is safe to
delete: the next `blade test icechunk` run recreates exactly what it needs.

**Nothing but this README and the `.gitignore` beside it is tracked**, and the
`.gitignore` is what enforces that. The sibling `zarr_stores/` directory
carries the same promise in prose but has 161 generated files tracked anyway,
because it never got a `.gitignore`; this one had both from day one so the
mistake does not repeat. If `git status` ever shows a repo directory here as
untracked-but-not-ignored, the `.gitignore` is what to fix — not the test.

`writeRepo` **replaces** the root it is given (an existing directory is deleted
first), so a fixture never inherits chunk or manifest files from an earlier
spec — which matters here more than it does for Zarr, because Icechunk object
files are content-addressed and several tests count or diff them.

Two copies of each fixture exist during a test run, the same split as
`zarr_stores/`:

- `tests/fixtures/icechunk_repos/<repo>` (this directory) — resolved at the
  **compiler's** cwd, for compile-time ref resolution, metadata loads and
  static folds.
- `generated_cpp_tests/tests/fixtures/icechunk_repos/<repo>` — the mirror
  resolved at the **test executable's** cwd, for the chunk reads the compiled
  program performs at run time.

The same relative path string (`tests/fixtures/icechunk_repos/<repo>`) is baked
into the blade sources so it resolves correctly from either working directory.

## What a fixture repo looks like

```
<repo>/repo                  the RepoInfo file: refs -> snapshots (the ONLY
                             mutable object in an Icechunk repo)
<repo>/snapshots/<id>        one Snapshot per commit
<repo>/manifests/<id>        chunk tables
<repo>/chunks/<id>           RAW, headerless chunk payloads
<repo>/transactions/         created empty — tx logs are PRUNABLE, so no read
<repo>/overwritten/          path may depend on either directory existing
```

`<id>` is Crockford base32, uppercase, no padding: 20 characters for the
12-byte object ids that name snapshots, manifests and chunks. Metadata files
carry the 39-byte Icechunk header; chunk files carry no header at all.

Ids are deterministic — a truncated SHA-256 over `(seed, role, payload)`, never
`System.Random` — with two consequences the tests lean on: a repo written twice
from the same spec is byte-identical, and an array left untouched between two
snapshots reuses its manifest and chunk files exactly, which is what makes the
axis-identity scenario of `docs/plans/plan-icechunk-provider.md` §5 expressible
as a fixture.
