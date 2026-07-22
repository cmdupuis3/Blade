# Streaming Provider I/O — v1 design and the <&>/<&!> fusion plan

Status: v1 (single-leaf streamed READS) IMPLEMENTED 2026-07-15, both
providers. This note records the v1 semantics and the plan for I/O-efficient
merged nests.

## v1: `alias.stream` — fiber reads at the S/T boundary

`let A = s.vars.A |> z.stream` binds a NON-materialized provider read: no
whole-array buffer ever exists. A consuming `method_for` nest inlines one
store read per FIBER (the trailing T axis at fixed site coordinates) at the
loop level where the site indices are fully bound — exactly the shapes:

```
mean:  for s:           read fiber A[s,:]                 -> out[s]      (arity-1 fiber kernel)
cov:   for s1: read A[s1,:]; for s2 >= s1: read A[s2,:]   -> m2[s1][s2]  (comm pair; fiber s1 HOISTED at its level)
skew:  the comm triple, same structure one level deeper
```

Multi-dimensional sites work through the fused joint-symmetry level (the
compound SymIdx<r, prod(sites)> loop): the row-major site decode is kept,
the peels are replaced by fiber reads at the decoded coordinates.

Mechanics: `ProviderReadSpec.Streamed`; the binding emits only the
provider's `GenStreamOpen` prologue (handles + fiber extents vector); the
nest allocates one destination buffer per streamed kernel argument
(`<v>_fb_p<pos>` — a comm kernel holds several fibers of one source
concurrently) and `genElementBindingStreamed` emits `GenStreamFiber` +
a rank-1 `Array<T,1>` wrapper where a fiber peel would have been — the
kernel body is untouched. netcdf fibers = one `nc_get_vara` per fiber;
zarr fibers = one seek+read per t-chunk of the chunk files covering the
site (the fiber axis is innermost, so within-chunk fibers are contiguous).
Gates: differential (`.read` vs `.stream` byte-identical compute output)
over mean/cov/skew, 1D and 2D sites, both providers; elementwise or
otherwise ineligible consumption fails loudly with steering.

v1 scope notes:
- Dense variables only (packed streams need the pool-order story; reject).
- One trailing T axis (rank-1 fibers). Deeper fiber ranks: follow-up.
- Fusion (`<&>`/`<&!>`) leaves are NOT stream-eligible yet (see plan below);
  a streamed source consumed by a fused tree fails at C++ compile time —
  make this a loud classify-time reject when the fusion work starts.
- WRITES: v1 keeps the compute output materialized (it is a program value)
  and `z.write(path, out)` streams FROM that buffer — post-hoc, sequential,
  no extra memory. True in-nest writes only pay once outputs can be
  NON-materialized, which requires the write-terminal form below. This is
  a deliberate scoping decision: the input side (the T-extended array) is
  the memory cliff; outputs of fiber reductions are T-free.

## Plan: I/O-efficient merged nests (<&> soft join, <&!> hard join)

The staggered merged-nest machinery (fusion arc) already merges leaves of
different depths over one loop skeleton. Streaming makes the merged nest an
I/O SCHEDULE; the strategy, in implementation order:

1. **Fiber dedup at shared levels (P2).** In a merged nest, bind ONE fiber
   read per (source, site-tuple, level), shared by every leaf: in a fused
   mean+cov+skew tower, mean consumes the s1-level fiber that cov/skew
   already read — adding mean to a cov pass costs ZERO extra I/O. Keyed
   like the existing self-zip peel dedup, extended across leaves.
   `<&!>` (hard, single pass) then reads each fiber pair exactly once for
   ALL leaves — the I/O analog of the shared sufficient-statistic pool from
   the single-pass tower arc, and the recommended default for same-source
   towers. `<&>` (soft) keeps separate passes: correct, I/O multiplies by
   pass count; prefer it only when leaves iterate incompatible site spaces.
2. **Staggered writes at leaf boundaries (P2).** Each leaf's output cells
   complete at its own depth (mean at s1-close, cov per (s1,s2)); with
   canonical iteration order = storage order, per-leaf sequential writes
   stream independently. Combined with (1): one pass over the store, k
   outputs written.
3. **LRU chunk cache in the providers (P3, providers/-only).** The
   quadratic pass (cov's inner s2 loop) re-reads fibers; when site chunks
   hold c0·c1 fibers, an N-slot cache keyed on chunk key amortizes reads
   across neighboring sites with zero nest changes. This is the cheap 80%.
4. **Tile-blocked pair iteration (P4).** Iterate site PAIRS in tile order —
   the simplex-blocks grid (SymIdx<2, T> over tiles): load tiles T1, T2
   (2·B fibers), sweep all pairs within, move on. Fiber reads drop from
   O(M²) to O(M²/B) with bounded memory 2·B·fiberLen. The storage
   decomposition and the I/O blocking are the same object — align B with
   the store's site-chunk extents (zarr) or the blocks layout's tile so a
   tile load = whole chunks. Per-cell outputs are order-independent
   (no FP reassociation for method_for maps), so the reorder is safe;
   reduce-terminals need the diff-oracle gate.
5. **Write terminals (P5).** `z.write("out", <unforced pipeline>)` — the
   write IS the force point (mirroring `reduce(deferred, op)`): the nest
   writes cells and the output array never materializes. This is where
   in-nest writes earn their keep (big outputs, e.g. wide cov), and the
   natural meeting point with the deferred "standard streaming out-of-core
   construction" arc.
6. **MPI × streaming (P6).** Ownership = output cell ranges (existing
   MpiSimplicial split); each rank streams only fibers its cells touch
   (s1-range × all s2, or tile-pair ownership from (4) for locality);
   per-leaf Allgatherv restoration (existing co-fusion machinery). This
   supersedes the current distribute-then-materialize for streamed
   pipelines.

Related docs: providers/ZarrVirtualArraysSpec.md (provider-backed virtual
arrays subsume `.stream`/`.read`/windows as force-point semantics),
providers/ZarrSimplexBlocksPlan.md (the tile machinery (4) reuses).
