# Plan: general OrbIdx decompaction

Status: **§2 and the FULL-decompaction endpoint of §3 are implemented
(2026-08-02); §3's partial/peel lattice and §4.4's `reduce` integration are
not.** Depends on
[plan-orbit-index-types.md](plan-orbit-index-types.md) (the class, §5's
canonicalization, §8.1's trust model) and
[plan-orbidx-bijections.md](plan-orbidx-bijections.md) (`orbRank`/`orbUnrank`).

What shipped, and where:

| this doc | surface | interp | codegen |
|---|---|---|---|
| §2 read at any raw tuple | `W(i,j,k,l)` — flat, one coordinate per raw axis | `ArrayOps.wreathReadAny` → `OrbRank.orbReadPlan` | `orb_read<T, orb_level<...>...>` |
| §3 full decompaction | `decompact(W, 0)` (`d` = levels to KEEP, §4.3) | `ArrayOps.decompactArray`, unchanged — its per-cell `readCompact` now routes a sole wreath slot to the §2 read | `materializeDecompactForm`'s wreath arm: dense alloc + one `orb_read` per cell |

Both sides consume the verified artifacts rather than re-deriving canon/rank
(`src/cpp/orbit_wreath_utilities.hpp`, `src/OrbRank.fs`), which is what makes
"the two backends agree" a property of the wiring instead of a coincidence. The
decompaction is DENSE-SEQUENTIAL, not the pool-sequential scatter §4.2 sketches
— see the note there. Corpus: `index-types/206, 207, 213, 214, 215` for the
values, `216–219` for the boundaries.

Still refused, each with its own diagnostic: partial (per-level) decompaction,
a partial/sub-array subscript, `reduce`/`prodsum` over a pool, writing the class
down as an annotation, `transpose`, and provider I/O.

## 1. Why decompaction is load-bearing, not a convenience

Three forces make general decompaction the first `OrbIdx` feature users will
actually hit:

- **`reduce()` requires it today.** Any reduction over compact storage is a
  hard error that *names decompaction as the remedy*: "decompact(A, d) first
  for the logical fold" (`TypeCheck.fs:3531-3542`; same rejection in
  `prodsum`, :3603-3609, and the fused-reduce path, :3296-3301). No
  residual-symmetry machinery exists. A wreath class that cannot be
  decompacted cannot be reduced at all.
- **Non-odd maps force it.** The deduction model
  (`proofs/OrbitDeduceModel.fsx`, T5-T9) shows a `-` level does not survive an
  arbitrary elementwise kernel: even maps flip it to `+`, general maps degrade
  the true group to one `OrbIdx` cannot spell (`A_3` for `[(3,-)]`, the
  order-4 kernel for the Riemann class). The honest output type is *weaker*
  than the input's — and producing a weaker-typed array from packed input IS a
  (partial) decompaction.
- **Speed is on decompaction's side.** Measured on this project: packed
  symmetric compute ran 2.14× *slower* than dense; free loop reordering beat
  the symmetry rewrite. Decompact-then-reorder is the performance escape
  hatch; the packed pool is the memory/canonicity format. The two need a
  cheap, correct bridge in both directions.

## 2. Semantics

For a class `OrbIdx<levels, n>` with packed pool `P` and any logical tuple
`t` (raw axes, digits in `[0,n)`):

```
dense[t] = 0                                if canon(t) = zero-set
         = χ(t) · P[orbRank(canonical(t))]  otherwise
```

where `canonical`/`χ` are §5's per-level sort fold and accumulated sign
(reference implementation: `canon` in `proofs/OrbitEnum.fsx`; sign cases
pinned by enumeration — e.g. `canon [(2,-),(2,+)] [2;0;1;2] = ([0;2;1;2], -1)`).
This generalizes exactly what depth-1 does today: `readCompact`
(`Interp/ArrayOps.fs:415-442`) is the one-level instance (`canonFold` +
`canonLeftJustify` + `NegateOnSwap`/`ConjugateOnSwap`), and `decompactArray`
(:927-944) is already "re-enumerate the output shape, `readCompact` each
cell" — the same value-equivalence-driven design scales to depth `d` by
swapping the one-level fold for the level list.

**Read transforms become sign products.** `ReadTransform` is today a
per-group enum (`Identity | NegateOnSwap | ConjugateOnSwap`); a wreath class
needs the accumulated per-level product instead. The C++ `canon_fold`
(`nested_array_utilities.hpp:846-859`) already returns a parity — the change
is to fold it across levels, not to invent a new mechanism. (`Hermitian`
stays depth-1-only; conjugation is outside the ±1 character system, per the
OrbIdx plan §3.)

## 3. Partial decompaction — a typed lattice, not a boolean

Full decompaction (pack → dense) is one endpoint. The useful operations sit
between, and each is a move **down the expressiveness lattice** with a typed
result:

| operation | input | output type | cells |
|---|---|---|---|
| peel outer level | `OrbIdx<[(2,-),(2,+)],n>` | `OrbIdx<[(2,-)],n>` ×2, untied | `C(n,2)²` |
| drop a `-` sign (even-map result) | `OrbIdx<[(2,-)],n>` | `OrbIdx<[(2,+)],n>` | `C(n+1,2)` |
| full decompaction | any | dense `Idx<n>^rank` | `n^rank` |

Rules the type system needs (all verified by enumeration in
`OrbitDeduceModel.fsx`):

- **Peeling level `d`** unties its `r_d` sub-blocks: result is `r_d`
  juxtaposed copies of the depth-(d−1) class. Sound because the wreath group
  contains the product of the block groups.
- **Minimal decompaction for reduce**: reducing an axis set `S` only requires
  decompacting the levels `S` *punctures* (touches partially). The verified
  residual rules: puncturing one axis of one block breaks every level above
  it (T11); reducing **aligned** axes — the same position in every tied
  block — keeps the tie and lowers the inner rank (T12); reducing a full
  block lowers the outer tie rank (T13). `reduce` should emit exactly the
  cheapest of these rather than demanding full decompaction.
- **Sign discharge**: any peel or drop that crosses a `-` level must bake the
  accumulated sign into the copied values (the χ in §2) — after decompaction
  there is no read transform left to apply it.

## 4. Implementation sketch

1. **Interp first**: `decompactOrb : levels → keepDepth → BladeArray →
   BladeArray`, generalizing `decompactArray` (`ArrayOps.fs:927-944`):
   enumerate the *output* type's storage cells (`forEachStorageCell`
   pattern, :872-913), fill each by the §2 read. Full decompaction is
   `keepDepth = 0`.
2. **Codegen/C++**: a streaming kernel — iterate the *pool* in DFS order
   (sequential reads), `orbUnrank` each offset to its canonical tuple, and
   scatter to the orbit's dense positions with the per-element sign. Pool-
   sequential beats dense-sequential here because each pool cell fans out to
   its whole orbit; measure, don't assume (and never at power-of-two `n`).

   **NOT WHAT SHIPPED, and the reason is availability, not performance.** The
   scatter needs each pool cell's ORBIT enumerated with its per-element
   character, and no verified emitter produces that: `orb_visit` hands out
   canonical tuples only, and writing an orbit enumerator would be a *second*
   implementation of the wreath action to keep in step with `orb_canon`. The
   dense walk needs nothing new — `orb_read` IS the §2 read, already checked
   against brute force — so both backends do one `orb_read` per dense cell and
   the differential harness is then comparing implementations of one algorithm
   rather than two algorithms. The performance claim above remains unmeasured in
   either direction; it stays here as the optimization to reach for once an
   orbit enumerator earns its own tests.
3. **Surface**: extend the existing `decompact(A, d)` spelling — `d` becomes
   "levels to keep" for a wreath class; the depth-1 meaning is unchanged.
4. **`reduce()` integration** (last): replace the hard error with
   minimal-decompaction per §3, so `reduce` over compact storage becomes a
   typed, cost-visible desugar instead of a dead end.
5. **Providers**: Zarr triangular writes stay canonical-pool-shaped
   (`providers/ZarrTriangularSpec.md`); decompaction is a reader-side
   operation there, using the same §2 read against flat-range blocks.

## 5. What this deliberately does not do

- No in-place decompaction: pack → dense always allocates (the shapes share
  no layout; an in-place scheme would need the §3 lattice inverted, i.e.
  *compaction*, which is a separate, simpler operation: canonicalize + write
  through `orbRank`, with a well-definedness check that the source actually
  has the claimed symmetry — the OrbIdx plan's §8.1 trust model decides
  whether that check is a debug assert or always-on).
- No layout innovation: the dense output is plain row-major; blocked or
  padded targets belong to a layout pass Blade does not have.
- No Hermitian generalization (see §2).

## 6. Verification

- **Independent dense oracle**: build the dense tensor directly from the
  logical definition (never via the pack) and compare full decompaction
  byte-for-byte — the antisym post-mortem rule: a kernel-produced pool
  compared against an oracle, never a read→write roundtrip (which cancels
  layout bugs exactly).
- **Sign spot checks**: the pinned canon/sign cases (`OrbitEnum.fsx` §5 and
  the P9-P13 family) exercised through `decompactOrb` — one negated mirror
  cell per `-` level, one zero-set cell per level.
- **Partial-decompaction typing**: each §3 row round-trips through the type
  checker with the stated output type; the T11-T13 residual-rule enumerations
  are the ground truth for the reduce integration.
- **Cardinality conservation**: nonzero dense cells = Σ over orbits of orbit
  size; total dense = `n^rank` — cheap invariants that catch fan-out bugs.
