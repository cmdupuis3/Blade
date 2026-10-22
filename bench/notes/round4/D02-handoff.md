# D02 frozen candidate: required rank-1 Zarr resident destination

Status: implemented, unbuilt/unemitted/unexecuted/untested/unmeasured by lane D.
One PM43 route only; root reviews, Luna alone executes. No commit or merge.
Branch `codex/round4-d-streaming`; base `9b36f1d17574bba040c79118aa491e1a6f7c1350`.

## Census and observed baseline

The already-executed D01 `.read` reference
`generated_cpp_tests/zarr_join_v2_n11_read.cpp`, SHA256
`FA2E4523BAF0BBDFB18F3873E5240D6143C66AC1EBB55499FA678A517405D43A`,
allocates `double A_flat[11]` plus a four-cell disk chunk, frees the chunk,
reports missing cells, then allocates the final Array and copies all eleven
cells into it. Lines 222/223/243/244/247/249/251 show those operations.
This artifact was read, not regenerated, for D02.

The ordinary required-output witness is:

```blade
import zarr as z
let s = z.load("store")
let A = s.vars.A |> z.read
```

All new successful witnesses explicitly select `BLADE_PRINT=A`; the final
Array remains required and live. This is distinct from D01's streamed scalar
terminals. No map/fold rewrite or output omission is involved.

## Changed files and ownership

- `src/providers/ZarrProvider.fs`: private `AssemblyDestination` contract
  on `genAssembleFlatCore`; `OwnedFlat` retains the existing allocation/stores.
  `CallerDestination(buffer, setup)` inserts caller setup after the metadata
  probe and directs the SAME traversal/casts/fill/tally into caller backing.
  Existing generic, unchecked-window-source, phased and Icechunk entry points
  explicitly retain `OwnedFlat`. New `tryGenReadVarIntoFinal` admits the narrow
  required rank-one destination and publishes its wrapper after validation.
- `src/CodeGenState.fs`: `TopLevelProviderRead: IRId option`, default None.
- `src/CodeGen.fs`: private `genModuleBinding` grants the capability for the
  current actual module-level provider binding and restores the incoming field
  in its returned context. Normal and split module emission use this helper.
- `src/CodeGenBinding.fs`: the ordinary dense provider handler checks exact
  capability/binding-ID equality and no MPI program, then asks the Zarr
  capability. Stream/packed/wreath/tile routes run before this hook.
- `tests/ZarrTests.fs`: new `residentDestinationE2E` inside standalone Zarr.
- This handoff.

No Optimize/Lowering, allocator, registry or interpreter production code is
changed. The D01 temporary local type supplement is untouched on this lane;
integration already removed that supplement through the C01 shared census.

## Admission, allocation and cleanup

Only an explicitly certified actual module binding receives the new route.
Indentation, parameter emptiness, variable name and ambient/current-ID cells
do not establish authority. Nested reads have different IDs and cannot inherit
authority from their enclosing binding. Empty/default contexts and the generic
`GenReadVar` API retain the original owned-flat reader. The context field is
immutable and granted/restored per binding; no cross-compilation plan exists.

Require Zarr, ordinary non-streamed/unmasked/unwindowed `.read`, nonvirtual
dense plain rank one, metadata v2/v3 identity codec with no packed layout,
matching literal nonnegative shape and exact provider element type. The
shared core uses Int32 arithmetic: destination bytes and padded disk chunk
bytes are each checked against Int32.MaxValue BEFORE narrowing/multiplication.
Unsupported or unproved cases retain their existing reader, not a new refusal.

The metadata-existence probe still precedes allocation. At the former flat
allocation site, emit static extents and allocate private `A__resident_pending`
using `allocate<typename promote<T,1>::type,nullptr>(A_extents)`. Rank one has
no skeleton: this is the allocator's normal scalar pool, including its existing
one-cell sentinel for N=0. Stores write directly into that backing. Chunk
scratch remains the original correctly typed `new disk_type[C]`/`delete[]`.
After scratch release and the original missing report, publish
`Array<T,1> A = { A__resident_pending, A_extents }`.

There is no raw-flat-buffer adoption or allocator mismatch. Final storage has
the same type/extents/allocator as the old final output. The admitted module
binding remains live for auto-print exactly as before; module top level is not
scope-freed. No scoped read is newly admitted and no unsupported lifetime
registration is inferred. Later existing array ownership sees the same final
Array representation.

## Source-data semantics and failures

One assembly implementation supplies both paths: same ascending chunk walk,
full padded disk chunk reads, bounds clipping, `(elemCpp)disk_cell` conversion,
fill conversion, endian/codec metadata gates, per-cell global tally, chunk
release and final missing report. Edge padding never contributes missing cells.
Integer width conversions, including the existing unsigned conversion behavior,
are unchanged; Float32 stays Float32. There is no new zero-fill optimization.

Earlier nonfinite values remain a tally: a later short or absent/null-fill chunk
still fails immediately before any missing report or Array publication. A read
with only nonfinite failures reports its whole-array count and earliest global
coordinate after all chunk reads. Pending backing is not published as A until
validation succeeds. Fatal readers retain process-exit behavior; this change
adds no exception-cleanup guarantee. Allocation strategy/timing changes, and
identical out-of-memory behavior is explicitly NOT promised.

## Storage and traffic model

For N logical cells, C padded chunk cells, element width E and disk width D,
old payload peak is `max(N*E + C*D, 2*N*E)` because disk scratch was released
before final allocation/copy. New payload is `N*E + C*D`, plus fixed descriptors,
tally and extents (the N=0 allocator sentinel is one element). The full required
output is retained. This removes one full payload allocation and its final
N-cell copy, modeled as `2*N*E` copy traffic. When chunk scratch dominates,
removing the copy need not reduce peak payload. This is neither measured RSS
nor a whole-process bound: allocator retention, iostream/path allocations and
compiler metadata are outside this model. No time/memory gain is claimed.

## Authored validation requests, all unrun

Luna: verify these hashes, build Release in the serialized queue, then run
standalone `Blade.exe test zarr` in its private run context. Do not run the
default/full suite. Record all pass/fail/skips and actual emitted route evidence.

The new tests assert `zarr-rank1-direct-destination` on the actual normal
compiler path, required final-pool allocation, metadata-before-allocation,
no `A_flat`/final-copy loop, chunk scratch deletion, and final Array publication
after whole-read validation. Exact reader-span count must be one. For the
reference, the test replaces ONLY that exact span with the unchanged generic
reader emitted through `genBinding` on an unprivileged default context. All
remaining C++, printer, demanded A, source store and output selection match.
Known printed arrays independently pin the reference and new output.

Positive fixtures: v2 Float64 N=11/C=4; v3 Float64 with NaN padding outside
the odd tail; v2 Float32; v3 on-disk Int32 widened to Int64; zero extent; and
a finite-fill missing middle chunk. Negative/provenance assertions cover
default context, a different authorized binding ID, MPI with flag restored
in finally, stream, masked raw, window, other provider, rank two, mismatched
shape and metadata-only oversized padded chunk (no giant buffer allocation).
An emitted rank-two control retains original flat assembly and copy.

Paired direct/reference failures: first/last-block NaNs must report two cells,
first [0], without printing A; first NaN plus truncated last chunk must report
expected-32-byte short read before BL8015; first NaN plus absent final chunk
with null fill must report the missing chunk before BL8015. Each pair still
requires the new emitted route. The standalone suite also covers the existing
rank>=2/packed/window/stream/Icechunk/shared-core controls as applicable; request
additional affected Icechunk focused checks if root's review requires them.

## Frozen SHA256, working-tree bytes

| File | SHA256 |
|---|---|
| src/CodeGen.fs | 1D4E36490D66CA0FB44C1A9B4975A852B88D79618A91AED309F63C6D52B6C869 |
| src/CodeGenBinding.fs | 4DCE22C115038E4DD65144EB9CAB8C2D0FE5C4D694E884AE8DB59D75B8684BA2 |
| src/CodeGenState.fs | FA92836B366C9BE67D0773276425359B785FAA1478F4F21E2450F35F39B55294 |
| src/providers/ZarrProvider.fs | 82EA96945B462D181F3B2D27261E00B66FC4BF91995DE183509AEDC377765F80 |
| tests/ZarrTests.fs | A44D58B808D62263C114F11D4ECD4D89A59BEBF39255CDC15E279232578BBEE4 |

Static checks: git diff --check clean; all five tracked files remain index LF /
working-tree CRLF. No compiler, fixture, build, test, benchmark or emitter was
executed by lane D. Graft source census returned no F# hits/coverage; targeted
reads supplied the source anchors, with no savings footer.
