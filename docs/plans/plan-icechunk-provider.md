# Icechunk provider — versioned stores, the checkout factory, and axis identity across checkouts

**Status (2026-08-24, rev 2): ACTIVE — P0 (ChunkSource seam), the checkout
desugar, and the provider skeleton in flight on `feat/icechunk-provider`.**
Rev 2 folds in design review: unit-marker disambiguation replaces string-prefixed
refspecs at the surface (§3), the four per-phase checkout arms collapse into one
raw-AST desugar (§4), and §5's metadata fingerprint is stated for all variables,
not just axes. The FlatBuffers dependency question (§6.2) and the fixture
strategy it couples to (§10) remain DEFERRED and gate P1's payload decode.
Format facts below are against Icechunk spec 2.1 / icechunk-python 2.1.2
(2026-07-29); code citations are against the working tree at 76ac59a and will drift.

## 1. Why

Icechunk (https://icechunk.io) is a transactional storage engine over Zarr: a repo
holds git-like branches, tags, and immutable snapshots of a Zarr hierarchy, with
ACID commits. Scientific stores that today reach Blade as bare Zarr directories
increasingly live inside Icechunk repos, and the interesting new questions are
version-shaped: *read the store as of tag `v2024`; difference two commits of the
same field; pin a compiled program to an exact snapshot.*

Two properties make Icechunk an unusually good fit for Blade's provider model:

- **Everything but one file is immutable.** Snapshots, manifests, and chunk files
  never change after write; the only mutable object in a repo is the root `repo`
  file that maps refs to snapshots. Blade providers already resolve store metadata
  at compile time; with Icechunk the compiler can resolve *everything* — ref →
  snapshot → manifests → per-chunk byte ranges — at compile time and bake the
  result, because the resolved snapshot cannot change under the emitted binary.
  The generated C++ contains no Icechunk logic at all: no FlatBuffers, no zstd,
  just `std::ifstream` + offset reads, preserving the Zarr provider's
  `LinkNeeds = "none (pure std C++17)"` property exactly.
- **Version identity is first-class.** The provider's `Fingerprint` becomes the
  resolved snapshot ID (the semantically right provenance token — no sha256 sweep)
  and `VersionStamp` becomes the mtime of the single mutable `repo` file (exact
  and O(1), versus the Zarr provider's max-mtime walk over every file).

The payoff feature is snapshot differencing as a *language-level* idiom: two
checkouts of one repo whose shared axes have not diverged produce arrays that
unify, so `ck2.vars.temp - ck1.vars.temp` compiles to a fused loop over provably
identical index spaces — and refuses, loudly, when the grids genuinely diverged.
§5 is the theory of when that unification is licensed.

## 2. Format facts this design leans on

From the spec (https://icechunk.io/en/latest/reference/spec-v2-1/) and the
FlatBuffers schemas at `icechunk-format/flatbuffers/{common,repo,snapshot,manifest,transaction_log}.fbs`
in https://github.com/earth-mover/icechunk. Facts marked *(impl)* are confirmed
from the reference Rust implementation but not stated in spec prose.

- **Layout** (identical on local FS and object store): `$ROOT/repo` (entry point,
  only mutable file), `snapshots/`, `manifests/`, `chunks/`, `transactions/`,
  `overwritten/` (repo-file backups). IDs are Crockford base32, uppercase, no
  padding: 12-byte object IDs → 20-char names, 8-byte node IDs → 13 chars.
- **Metadata file framing**: a 39-byte header — 12 bytes magic `"ICE🧊CHUNK"`,
  24 bytes implementation name (space-padded), 1 byte spec version (`1` or `2`;
  2.0 vs 2.1 is *not* distinguished in the byte — 2.1 only adds one optional
  table field), 1 byte file type (Snapshot=1, Manifest=2, TransactionLog=4,
  RepoInfo=6; the enum also defines Attributes=3 and Chunk=5 but no current
  writer stamps them), 1 byte compression (0=none, 1=zstd) — then the payload:
  a (usually zstd-compressed) FlatBuffer.
- **The `repo` file** (root table `Repo`): `branches: [Ref]` and `tags: [Ref]`
  — **separate namespaces**; a branch `x` and tag `x` can coexist *(impl)* —
  each `Ref = {name, snapshot_index}` indexing into `snapshots: [SnapshotInfo]`
  (which carries `parent_offset`, timestamps, messages);
  `deleted_tags: [string]` tombstones (a deleted tag name must never be
  recreated); `status: RepoStatus` with availability Online=0 / ReadOnly=1 /
  Offline=2. Updates are optimistic-concurrency conditional writes with the old
  version backed up under `overwritten/`.
- **Snapshots**: `NodeSnapshot` entries sorted by path. `id: ObjectId8` is
  spec-stable — *"Stable across snapshots — a node keeps its id for its
  lifetime."* Moves/renames preserve the id (`moved_nodes` in transaction logs
  carries the same id across paths); delete-then-recreate mints a fresh random
  id *(impl)*. `user_data` is **the array's `zarr.json` content verbatim, as
  UTF-8 JSON bytes** — shape, dtype, codecs, `dimension_names`, and `attributes`
  (so the `blade` packed-layout attribute rides along unchanged). Dimension
  names are additionally stored structurally in `ArrayNodeData`. An array's
  chunks may be spread over multiple manifests: `manifests: [ManifestRef]`,
  each with one `ChunkIndexRange {from, to}` per dimension; ranges must not
  overlap — each chunk coordinate is covered by at most one manifest.
- **Manifests**: `ArrayManifest` entries sorted by node id; within one,
  `refs: [ChunkRef]` sorted lexicographically by `index: [uint32]` (plain
  coordinate vectors, no delta coding). A `ChunkRef` is exactly one of:
  **inline** (`inline: [uint8]`, post-codec bytes, ≤ the configurable
  512-byte default threshold), **native** (`chunk_id` + `offset` + `length`
  into `$ROOT/chunks/{chunk_id}`), or **virtual** (`location` or
  zstd-dict-`compressed_location` + offset/length + optional checksum, pointing
  at an external file/URL). A coordinate with no ref reads as the array's fill
  value (Zarr semantics; spec-silent).
- **Chunk files are raw**: native chunk data is written headerless and
  uncompressed-by-Icechunk — the bytes are exactly what the Zarr codec pipeline
  produced. The reference writer emits one chunk per file (offset effectively 0),
  but the schema permits packing; readers must honor offset/length.
- **Transaction logs are prunable** (2.1 `pruned_ancestor_tx_logs`): a reader
  must not depend on full tx history existing. This decides §5's mechanism —
  pairwise chunk-ref comparison, never ancestry walks.
- **No C API.** Rust crate + PyO3 + JS/WASM bindings only. Blade parses the
  format itself; §11 records the rejected alternatives.

## 3. Surface: `load` returns a repo handle; `checkout` is the factory

```blade
import icechunk as ic

let repo = ic.load("data/weather.icechunk")     // repo handle: no vars, no dims
let ck1  = repo.checkout("main")                // bare: name unique across namespaces
let ck2  = repo.checkout("v1.0", ic.tag)        // marker: the tag namespace
let ck3  = repo.checkout("main", ic.branch)     // marker: the branch namespace
let ck4  = repo.checkout("1CECHNKREP0F1RSTCMT0", ic.snapshot)

let temp_now  = ck1.vars.temp |> ic.read
let temp_then = ck2.vars.temp |> ic.read
let drift = temp_now - temp_then                // unifies iff temp's axes are
                                                // unchanged between ck1 and ck2 (§5)
```

- `ic.load(path)` opens the repo, parses the `repo` file, and binds a **repo
  handle**: an empty provider module (no `dims`/`vars` structs — already a
  first-class outcome of `registerProviderModule`, TypeEnv.fs:1010–1055, whose
  `moduleFields` simply come out `[]`). The handle erases to unit at lowering
  like every load binding (Lowering.fs:1662–1669). Its nominal type does not
  satisfy `providerAliasName` (registry lookup on the module name fails), so no
  alias verb can mis-fire against it. Because the repo file is parsed at `load`,
  a bad path, an Offline repo, or a spec-1 repo fails **at the load site**, not
  at first checkout.
- `repo.checkout("<ref>")` is the module-creating site — the analogue of what
  `z.load(path)` is for Zarr. The argument is a string **literal** (same
  restriction as `load`'s path, and for the same reason: metadata resolution is
  a compile-time act). Both bindings are required in v1 — `ic.load(p).checkout(r)`
  chaining is a possible later sugar, not in scope.
- **Ref resolution and the unit markers.** Branches and tags are separate
  namespaces, so bare names can collide. The provider declares three units at
  module level — `Unit branch`, `Unit tag`, `Unit snapshot` — and exposes one
  unit-carrying marker constant of each (`ic.branch`, `ic.tag`, `ic.snapshot`).
  `checkout`'s optional second argument is such a marker, and **dispatch is on
  the argument's unit**: one method name serves all three namespaces,
  disambiguated by type rather than by string prefixes — the same move arity
  polymorphism makes, with units as the discriminant. (Named call arguments
  are reduce-only today — ParserGrammar.fs:743–749 — so the marker is a plain
  positional argument; no grammar change.) The bare one-argument form resolves
  the name across branches ∪ tags ∪ syntactically-plausible snapshot ids
  (20 chars, Crockford alphabet) and demands uniqueness: zero hits is a loud
  error **listing the repo's actual branches and tags** (we hold them parsed);
  two hits is a loud refusal naming both, with the marker spellings as the
  remedy. A name in `deleted_tags` names its tombstone. Refusals are features:
  no silent precedence order between namespaces. String-prefixed refspecs do
  not exist at the surface; `branch:`/`tag:`/`snap:` survive only inside the
  canonical key (§3.1).

  **Surface outcome (2026-08-24): the desugar erases the marker,** so v1
  ships this syntax with no unit machinery at all — `repo.checkout("v1.0",
  ic.tag)` is rewritten whole, and `ic.tag` never reaches typecheck (writing
  `let t = ic.tag` fails as the ordinary unknown-member refusal, which is
  correct). Making the markers genuine unit-carrying values (usable outside
  checkout position, hoverable) costs two touch points — a units slot on
  `ProviderSpec` plus registration in the provider-import arm
  (TypeCheckInfer.fs:12329–12337) — recorded as v-next. The
  tempting alternative, a stdlib `icechunk.blade` carrying the `Unit` decls,
  is a dead end twice over: `ModuleResolve.isBuiltinModule` deliberately steps
  over registered provider names, and a module-exports hit would *shadow* the
  provider alias, breaking `ic.load` recognition entirely.

### 3.1 The canonical key

The entire provider contract — `LoadAsModule`, `ReadVarData`, `GenReadVar`,
the fold cache, `Fingerprint`, `VersionStamp` — threads a single `path: string`
(ProviderRegistry.fs:38–93). Rather than widen that signature everywhere,
checkout **desugars to a canonical key**: `"<repoPath>@<kind>:<name>"`, e.g.
`data/weather.icechunk@branch:main` — `<kind>` read off the marker's unit, or
`?` for the bare form (the provider resolves `?` by cross-namespace uniqueness
at `LoadAsModule` time). That string is what enters every
path-keyed carrier (`ProviderPaths`, `ProviderRoots`, the fold/axis caches,
`ProviderReadSpec.FilePath`), and `IcechunkProvider` parses it back internally.
Consequence, stated rather than hidden: `ic.load("data/weather.icechunk@branch:main")`
is *reachable* as the desugared spelling — checkout is sugar over one mechanism,
not a second mechanism. The documented surface is the factory; the key form is
internal, appears in provenance/diagnostic strings, and is not promised stable.

Ref resolution is **memoized per (repoPath, refspec, repo-file mtime)** so
typecheck, static folds, lowering, and codegen all see the *same* snapshot even
if a writer commits mid-compilation — closing a TOCTOU the Zarr provider
structurally has (it re-`load`s the store directory in each phase).

## 4. Wiring: one desugar; everything downstream already exists

`checkout` is the first surface verb whose receiver is a **derived binding**
rather than the imported alias. The rev-1 design taught the new shape to every
phase individually — a twin arm in typecheck's load recognition
(TypeCheckInfer.fs:11773), in Lowering's `tryInvokeProvider`
(Lowering.fs:1652–1673), in StaticEval's raw-AST `providerRoots` scan
(StaticEval.fs:946–953), and in two Ide walkers — because the binding→path
association is carried by three independently-built maps. Review called that
over-engineered, and it is: none of it generalizes to providers that exist,
and it plants Icechunk-specific shape knowledge in four base-compiler phases.

Rev 2: a **single raw-AST desugar pass**, run once before both typecheck and
StaticEval's scan (they consume the same `Decl` list). The pass walks the
module's decls, records `repoBinding → (alias, path)` from load-shaped lets
whose alias resolves to the icechunk provider, and rewrites

```
let ck = repo.checkout("v1.0", ic.tag)
    ⇒  let ck = ic.load("data/weather.icechunk@tag:v1.0")
```

preserving the original span on the rewritten node, so every diagnostic still
points at the checkout text. The marker argument is read syntactically at the
rewrite (a field access on the provider alias naming one of the declared ref
units); the bare form encodes `@?:`. A `.checkout` on anything that is not a
recorded icechunk load binding is left unrewritten and fails as the ordinary
missing-member error it is. The pass is a no-op for programs with no icechunk
loads.

After the rewrite, every downstream phase — typecheck, lowering, StaticEval,
Ide hovers, the interpreter, codegen — sees the load shape it already handles:
**zero new arms anywhere**, and the three existing binding→path carriers work
on the canonical key as a plain path string. Codegen and the interpreter were
never in question: they dispatch off IRId-keyed `ProviderReads`/`ProviderWrites`
side maps whose specs carry `FilePath` baked in (CodeGenBinding.fs:1444,
Interp/Run.fs:422–425); EmitLlvm.fs:4047–4048 refuses provider I/O wholesale,
so Icechunk inherits C++-backend-only like every provider.

The repo handle needs no new machinery either: `ic.load(path)` with a bare
path binds an **empty provider module** (`registerProviderModule` with no
dims/vars structs is already a first-class outcome, TypeEnv.fs:1010–1055) and
erases to unit at lowering like every load binding (Lowering.fs:1662–1669);
its nominal type does not satisfy `providerAliasName`, so alias verbs cannot
mis-fire against it. The desugar stays deliberately concrete to icechunk — no
generic "derived-binding verb" registry slot until a second provider actually
wants one.

**Desugar outcome (2026-08-24): LANDED as designed** — `src/ProviderDesugar.fs`
(`expand` for the diagnosing pipeline entry, `desugarOrIdentity` for the
others), wired at three sites: the typeCheck pipeline ahead of `Unfold.expand`,
the raw decl list at the top of `lowerTypedProgram` (the single funnel every
lowering caller passes through — which is where StaticEval's `providerRoots`
scan gets fed), and the IDE entry-buffer check. All three raw-AST load-shape
matchers in the repo receive desugared ASTs; malformed checkouts on a recorded
repo surface as BL3007 at the checkout call's span, and the rewrite fires only
at a top-level binding's RHS — the only position any binding→path carrier
recognizes a load in. Known edges, accepted: tests that call `resolveStatics`
directly on their own parse must desugar first, and hovers inside multi-file
*member* buffers see undesugared checkouts (compile behavior is unaffected —
typeCheck desugars the whole module set).

## 5. Axis identity across checkouts — the theory

### 5.1 What the type system does today

Every provider load mints **fresh** index-type identities:
`zarrDimToNamedIndexType` takes `builder.FreshId()` per dim
(ZarrProvider.fs:1022–1032), the per-store `dimMap` shares that identity across
one store's arrays (so `A(lat, lon)` and `lat` co-index within a load —
pinned by tests/ZarrTests.fs:220–224), and `unify` compares index slots by
`Id`+`Tag` **only** — never name, never extent (Unify.fs:990–992). Two loads in
one program — even the same store twice — are nominal strangers and refuse to
mix. There is no accidental-unification hazard to defend against; sharing must
be *built*, and the hook already exists: `zarrStoreToModule`'s `externalDimMap:
Map<string, IRIndexType> option` parameter (ZarrProvider.fs:1063, 1090–1098),
currently always `None`.

### 5.2 When two checkouts present *the same axis*

An index type is an axis's identity. Two checkouts of one repo share the index
type for dim `d` **iff the axis is unchanged between their snapshots**:

1. **same dim name** `d`, and
2. **same extent**, and
3. **same coordinate-variable content**, when a coordinate variable exists
   (the xarray convention: an array named `d` indexed by `d`). A dim with no
   coordinate variable has no data to compare; (1)+(2) is all the identity
   there is.

Condition (3) is what the user-facing sentence "lat/lon/time are the same
between checkouts because they don't have diffs" means, and Icechunk makes it
decidable **from metadata alone, with no data reads**:

- the coordinate variable's **node id** must match (spec-stable for the node's
  lifetime; a delete-and-recreate — a genuinely new axis — breaks identity
  because the id changes);
- its **`user_data` JSON** must match bytewise (dtype, fill, codecs, shape —
  metadata edits are axis changes); and
- its **chunk-ref table** must match: the sorted `ChunkRef` list — coordinate
  vectors plus, per ref, the inline bytes or `(chunk_id, offset, length)` or
  fill-absence — compared structurally. Chunk files and manifests are
  immutable, so equal refs ⇒ byte-identical content. Commits that never
  touched the coordinate array keep pointing at the same chunk ids, so the
  common case (data commits on a fixed grid) compares equal instantly.

**Rejected mechanism — transaction-log walking** (diff the commit range and
check the node never appears in `updated_arrays`/`updated_chunks`): tx logs are
prunable under 2.1 expiration, ancestry walks cost O(history), and two refs
need not have a simple ancestor relationship. Pairwise ref-table comparison is
direct, O(coord-chunks), and prune-proof.

**Failure direction is safe.** If expiration/compaction rewrites a manifest to
new chunk ids with identical bytes, the ref tables differ and the axes do *not*
share — a false negative: the program refuses arithmetic that would have been
sound. It never falsely accepts. (Tightening: hash actual coordinate bytes at
compile time — coordinate arrays are small and often folded anyway. Polish,
not v1.)

### 5.3 Mechanism

A per-compilation, repo-scoped **axis mint table** (AsyncLocal, reset like
`IdeStores.reset`):

```
(repoPath, dimName) → { Extent; CoordFP; IndexType }     // CoordFP from §5.2
```

The first checkout that surfaces an axis mints its `IRIndexType` (fresh id, as
today) and records it. Each later checkout computes the axis's
`(Extent, CoordFP)`; on equality it passes the recorded `IRIndexType` in via
`externalDimMap`, so its arrays are built over the **same identity** — `unify`
then succeeds by the ordinary `Id` rule, no unifier changes at all. On
inequality it mints fresh (and records *why* the identity split). Notes:

- `registerProviderModule` qualifies index-type names per binding
  (`"{name}.index.{d}"`, TypeEnv.fs:1041), so two checkouts still get distinct
  *source-level* names over one shared `Id` — the flat `TypeDefs` map stays
  collision-free, and hovers stay per-binding. Unification is by `Id`, so the
  distinct qualified names cost nothing.
- Rank-≥2 variables share automatically when **all** their axes share; the
  element array's own data is irrelevant to its *type* — which is the point:
  `temp` may differ wildly between checkouts while `(lat, lon, time)` agree,
  and that is exactly the differencing case.
- Two checkouts of the *same* ref (or a branch and the tag pointing at the same
  snapshot) share trivially — every axis compares equal.
- **The fingerprint generalizes past axes.** `(node id, user_data bytes,
  chunk-ref table)` decides content-equality between two checkouts for *any*
  variable, not only coordinates — the axis rule is just its type-level use.
  The same test later licenses value-level reuse: reading an unchanged element
  array once and aliasing the buffer across checkouts, and sharing fold-cache
  entries between checkout keys whose fingerprints match (§11).
- Different repos never share: `repoPath` is in the key. Cross-provider sharing
  (an Icechunk checkout vs a bare-Zarr load of "the same" data) stays out;
  there is no identity to anchor it.
- **Divergence diagnostics**: when arithmetic later fails to unify two
  checkout-minted axes of one `(repoPath, dimName)`, the recorded split reason
  lets the error say *"axis 'lat' diverged between checkouts 'main' and
  'v1.0': coordinate data differs (extent 180 = 180)"* rather than a bare
  nominal mismatch. v1 may ship with the generic mismatch; the enriched
  message (likely its own BL code — five touch points per
  `adding-a-BL-diagnostic-code`) is a fast follow.

## 6. Compile-time metadata reading

### 6.1 Reader pipeline (pure F#)

`load` / checkout resolution reads, in order: header (39 bytes; refuse spec
byte 1 by name, accept 2; accept compression 0 and 1) → `repo` FlatBuffer
(status gate: Online/ReadOnly proceed, Offline refuses; refs; snapshot list) →
snapshot for the resolved ref → per-array: parse `user_data` **with the
existing `parseArrayMetaV3`** (ZarrProvider.fs:677) — the JSON is verbatim
`zarr.json`, so every v1 gate is inherited for free: uncompressed single
`bytes` codec, little-endian, numeric dtypes, regular chunk grid, and the
`blade` packed/orbit layout attribute — then cross-check the snapshot's
structural shape/dimension-names against the JSON (loud on disagreement) →
manifests for the arrays actually used: union the `ManifestRef`s by their
non-overlapping `ChunkIndexRange`s into one chunk table per array:

```
ChunkLoc = Fill | Inline of byte[] | Native of {File: string; Offset: int64; Length: int64}
```

Virtual refs are refused by name at parse. Native lengths are validated against
the chunk's expected byte size (chunk grids are regular and edge chunks
full-size under the raw `bytes` codec — same contract as Zarr v1). Hierarchy:
root-level arrays only (path `/name`, name a valid identifier), mirroring the
Zarr provider's one-level rule; deeper groups refuse by name.

### 6.2 Dependencies — the DEFERRED decision (gates P1)

Blade.fsproj has **zero** PackageReferences today; this provider forces the
first, and the choice couples to the fixture strategy (§10):

| Need | Option A (lean) | Option B (robust) |
|---|---|---|
| zstd | ZstdSharp.Port — managed-only, keeps "no native library" | same; unavoidable either way (real writers compress metadata) |
| FlatBuffers | hand-rolled reader (~200 lines: root offset, vtables, tables, vectors, strings, structs) pinned to the ~8 tables read, field ids transcribed from the `.fbs` files, loud on unknown spec byte | Google.FlatBuffers NuGet + vendored `flatc` output from `icechunk-format/flatbuffers/*.fbs` |

A reads-only and matches the repo's self-contained style; B additionally buys a
FlatBuffers **writer**, which makes hermetic on-the-fly fixtures (the `ZarrWrite`
discipline) cheap and later enables writes-as-commits (§11). FlatBuffers'
forward-compatibility (unknown fields ignored) protects both options equally;
the spec-version byte is the loud gate.

**DECIDED (2026-08-25): Option B** — ZstdSharp.Port for zstd, Google.FlatBuffers
runtime + vendored `flatc`-generated accessors from icechunk's own
`icechunk-format/flatbuffers/*.fbs`, pinned to the icechunk release the spec
facts were taken from. One mechanical consequence: `flatc` emits C#, not F#, so
the generated code (and the .fbs files beside it, with a README recording the
pinned source commit and the exact flatc invocation) lives in a small
class-library csproj that Blade.fsproj takes a ProjectReference to — the first
project reference and the first NuGet packages in the build. `blade test
icechunk` gains golden-bytes tests so a runtime/codegen version bump that
changes wire behavior fails loudly rather than drifting.

## 7. Runtime C++: the baked chunk table

The refactor that pays for everything (P0): parametrize the Zarr provider's
shared chunk-assembly core over its **chunk source**.

- F# side: `readArrayData` / `readPackedPool` take a `coords -> byte[] option`
  fetcher instead of hard-coding key-file reads. Zarr's fetcher is today's
  file-per-key; Icechunk's serves inline bytes or offset/length file reads.
  Everything above the fetcher — fill handling, edge intersection, packed pool
  assembly, wreath pools, windows — is shared and cannot drift.
- Codegen side: `genAssembleFlat` (ZarrProvider.fs:1268) takes a chunk-fetch
  *emitter*. Zarr's emits the chunk-key-string + `ifstream` open it emits
  today, byte-for-byte. Icechunk's emits compile-time-baked static tables
  indexed by flattened grid coordinate — per chunk a relative file path,
  offset, and length; inline chunks as byte-array literals (≤512 B each);
  a sentinel for fill — and a single loop of open/seek/read/copy. Missing
  chunk files (a GC'd pinned snapshot) die loudly via the existing
  `zExit` discipline, never silent zeros.

The generated program stays pure `<fstream>`/`<filesystem>` C++17 —
`LinkNeeds = "none"`. Dense, packed (sym/antisym), orbit, `read_window`, and
the MPI-distributed packed read all ride the shared core unchanged.

**P0 outcome (2026-08-24): LANDED, with two scope corrections.** The seam
landed as `ChunkSource` (F#: `Label` + `Fetch`, post-codec bytes, `None` =
fill) and `ChunkFetchEmitter` (codegen: `Prologue`/`Locate`/`Present`/`Read`/
`Ident`), with `genAssembleFlatVia` public for the Icechunk instance; nine
emit programs (dense v2/v3, packed flat, orbit, window flat, window blocks,
stream, null-fill, int dtype) verified byte-identical pre/post. Corrections to
the prose above: (a) the **packed-blocks** layout has its own assembler
(`genAssemblePackedBlocks`) with differently-shaped per-block I/O that does
NOT route through the shared core — P4 must either give it a second
`ChunkFetch` wiring or merge the assemblers; (b) the fill-branch *body* stays
in the shared core (the emitter owns detection + acquisition only), and
`genStreamOpen`/`genStreamFiber` still bake their own partial-read I/O,
consistent with streams being `None` in §8.

## 8. ProviderSpec assembly

Registered as `"icechunk"` in `ProviderStatics.install` (ProviderStatics.fs:161);
new file `src/providers/IcechunkProvider.fs` compiled **after** `ZarrProvider.fs`
(it reuses the parsers, SimplexBlocks, and the shared core) and before
`ProviderStatics.fs` — hand-placed `<Compile>` entry, `EnableDefaultItems=false`.

| Slot | v1 | Note |
|---|---|---|
| `LoadAsModule` | ✓ | bare path → repo-handle module (empty); canonical key → full dims/vars module |
| `ReadVarData` | ✓ | fold path; packed variables steer to `ic.read` like Zarr (ZarrProvider.fs:2202–2212) |
| `GenReadVar` / `GenReadPacked` / `ReadWreathPool` | ✓ | via the shared chunk-source core (§7) |
| `GenReadCompoundVar` | None | loud, as Zarr |
| `GenWriteVar` | refuses loudly | a write is a commit: new chunks + manifest + snapshot + conditional `repo` swap — its own arc (§11) |
| `GenStreamOpen` / `GenStreamFiber` | None | deferred; the baked table makes fiber reads easy later |
| `VarDimNames` | ✓ | from the snapshot |
| `Fingerprint` | resolved **snapshot ID** | exact provenance: `folded temp from weather.icechunk@branch:main = snapshot 1CECH…` |
| `VersionStamp` | mtime ticks of `$ROOT/repo` | the only mutable file; every existing (path, var, stamp)-keyed cache works unchanged. Polish: `tag:`/`snap:` keys are immutable and could skip invalidation |
| `LinkNeeds` | `"none (pure std C++17)"` | |

## 9. v1 scope — loud, specific refusals

In: local-filesystem repos; spec byte 2 (covers 2.0/2.1); branch/tag/snapshot
checkouts; dense + packed + orbit reads, `read_window`, static folds; interp
parity (free via `ReadVarData`); axis sharing per §5.

Refused **by name**: object-store URLs (`s3://…` — the runtime is `fstream`);
spec byte 1; virtual chunk refs; repo status Offline; deleted-tag names;
ambiguous bare refs (remedy: the unit markers); nested groups; writes; `.stream`;
`load_compound`; non-literal checkout arguments. Compressed/transformed Zarr
codecs, big-endian, non-numeric dtypes: inherited verbatim from
`parseArrayMetaV3`'s gates.

## 10. Testing

- `tests/IcechunkTests.fs`, entry `runIcechunkTests () : int`, local
  `check`/counters per the ZarrTests pattern; `printHeader`/`printFooter` from
  `TestHarness`; e2e compile+run behind `Build.compileCpp` with the
  `isSkipError`-SKIP and `baselineFailed` disciplines (tests/ZarrTests.fs:50–61).
- Dispatch arm `| [ "icechunk" ] -> …` immediately after `csv`
  (src/CliSelfTests.fs:2234–2237). Provider lanes are **standalone-only** —
  nothing in `tests/RunAll.fs` calls them — so `blade test icechunk` joins the
  nightly set alongside netcdf/zarr/csv/hybrid; a green default suite says
  nothing about this lane.
- **Fixtures**: generated on the fly, `ZarrWrite`-style — which requires
  FlatBuffers *writing* and is why §6.2 gates P1. Interim: golden byte fixtures
  hand-assembled in F# for the parser unit tests (a minimal FlatBuffer is
  constructible by hand), full hermetic repos once the §6.2 decision lands.
  `tests/fixtures/icechunk_repos/` gets a README **and a .gitignore from day
  one** — the zarr_stores precedent states nothing is committed but 161
  generated files are in fact tracked; don't repeat that.
- One-time (not CI) cross-validation of `IcechunkWrite` fixtures against
  icechunk-python 2.1.x, oracle-style.
- Axis-sharing tests (P3): same-ref checkouts unify; data-only commits keep
  axes shared (arithmetic across checkouts compiles and runs); a coordinate
  rewrite splits identity (rejects); an extent change splits identity; a
  branch/tag name collision refuses with the qualified remedy.

## 11. Later arcs and rejected alternatives

Later: **writes-as-commits** (`GenWriteVar` or an F#-side post-run commit: new
chunk files + manifest + snapshot + conditional `repo` swap; note the local-FS
conditional-put mechanism in the reference `object_store` backend is
unconfirmed — pin it down before building); **virtual chunk refs** (a natural
bridge to NetcdfProvider — a virtual ref *is* an offset/length into an external
file); object-store repos; `.stream`; checkout chaining; buffer dedup when two
checkouts' chunk tables coincide (read once, alias); repo-handle introspection
(branch/tag enumeration as compile-time values).

Rejected: **FFI to the Rust crate** (no C API exists; a bespoke cdylib shim
adds a native dependency to compile *and* run); **shelling out to
icechunk-python** (non-hermetic compile); **"export to plain Zarr first"**
(works today, loses versioning, provenance, and the differencing idiom);
**tx-log diffing for axis identity** (§5.2).

## 12. Phasing

| phase | deliverable | gate |
|---|---|---|
| P0 | ChunkSource seam in ZarrProvider: F# fetcher param + codegen fetch-emitter param; zero behavior change | **LANDED** — byte-identical across nine emit programs; `blade test zarr` confirmation rides the next runner pass. Packed-blocks assembler intentionally NOT shared (see §7 outcome; moves to P4) |
| P1 | §6.2 decision made; format reader: header, zstd, FlatBuffers repo/snapshot/manifest models; refusal gates (spec-1, Offline, virtual, deleted tags) | parses a repo written by icechunk-python 2.1.x; every refusal fires with its named message; parser unit tests hermetic |
| P2 | Provider + surface: spec registration, repo-handle load, the checkout desugar pass, ref units + marker constants, canonical key, memoized resolution; dense reads, folds, `blade test icechunk` lane | **SURFACE SHELL LANDED** (desugar + skeleton + both lanes green: provider-desugar 44/0, icechunk 130/0, zarr 273/0, full suite 5108/0; markers are desugar-erased, see §3 outcome). Reads/folds/e2e remain gated on P1's payload decode |
| P3 | Axis mint table + `externalDimMap` wiring + divergence recording | sharing tests of §10 pass: unchanged axes unify across checkouts, diverged axes refuse |
| P4 | Packed / orbit / `read_window` / MPI-distributed parity through the shared core | Zarr's packed/window test shapes mirrored under Icechunk, green |

Landing P2 adds the `| Icechunk | … |` row to `docs/features.md` §15 and updates
this doc's Status header plus the README index row — never the filename.

## 13. Risks

- **First NuGet dependencies** in Blade.fsproj (zstd at minimum). Managed-only
  packages preserve the no-native-library property, but the build acquires a
  restore step it never had; worth a deliberate call, not a drive-by.
- **Schema drift**: FlatBuffers forward-compat absorbs added fields; the spec
  version byte is the cliff, and it refuses loudly. A spec-3 repo is a new
  reader, by design.
- **Axis-sharing false negatives** after manifest compaction/expiration
  (§5.2): sound but potentially surprising; the divergence diagnostic must say
  *why* so the user isn't debugging a phantom.
- **Desugar placement**: the pass must run before *every* consumer of the raw
  `Decl` list — typecheck, StaticEval's `providerRoots` scan, and the Ide
  walkers. A consumer that grabs the AST upstream of the pass sees undesugared
  checkouts and fails obliquely (StaticEval's miss is silent: "not foldable").
  The P2 gate includes a fold test *and* a runtime test *and* an IDE-hover
  check for this reason. Span fidelity on the rewritten node is part of the
  gate: diagnostics must point at the checkout text, not the synthesized key.
- **Baked snapshots vs GC**: a compiled binary pinned to an expired snapshot
  dies at first read (loudly, missing file). Acceptable — and the loud path
  must stay loud through the shared emitter refactor.
- The **local-FS conditional-write mechanism** for the `repo` file is
  unconfirmed (read path doesn't care; the write arc does). Recorded here so
  the writes arc starts from the open question, not a guess.
