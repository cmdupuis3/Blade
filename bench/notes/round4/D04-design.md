# D04 / PM42 prerequisite: rank-one streamed window addressing

State: **design only; implementation awaits root review**. D03 is committed as
`d13883acaf50a1dba57705f8f3689fe59fd72df2`. No production/test changes,
build, emission, compiler execution, fixture execution or benchmark for D04.
Graft has no F# coverage (`genElementBindingPeel` query: no hits); the anchors
below are targeted source reads on the D lane. This is a pointer-safety fix,
not an adjacent-chunk cache or a performance claim.

## Concrete defect and affected routes

`src/CodeGenCuda.fs:4134-4137` emits a rank-one source alias with data pointer
`win - __w_lo`. For later windows, this forms a pointer before its allocation,
even though the subsequent global subscript would mathematically return to the
allocation. C++ pointer formation itself is invalid. The existing generated
artifact cited in D03 shows this at lines 255 and 304; it was not regenerated.

The alias serves two existing routes, so a halo-only replacement is incomplete:

- `CodeGenCuda.fs:3550-3576`: a dense halo over a Chunked axis, reading one
  streamed rank-one source through `IRAccess.windowReadsOf`.
- `CodeGenCuda.fs:3580-3606`: rank-one elementwise maps/broadcasts/zips, whose
  streamed operands are peeled to scalar kernel parameters outside kernel IR.
  The existing map, A+1 and zip(A,resident) witnesses are
  `tests/ZarrTests.fs:1600-1613`; halo radius one/two are at 1545-1555.

Rank-two bands use a separately allocated full-height row-pointer table with
filled rows pointing **inside** win (`CodeGenCuda.fs:4138-4141`). They do not
need this rank-one alias fix. Mixed routes containing a rank-one source must
nevertheless certify that source or refuse; rank-two support is not evidence
for rank-one safety.

## Proposed representation and insertion ownership

Keep each existing converted window allocation as a plain `T* win`. Do not
construct a rank-one Array for the streamed source. Every admitted consumer
load uses `win[local_coordinate]`; the source has no escaping resident wrapper.
The global window parameter, output ordinal, source extent and diagnostic
coordinates retain their original meaning.

Use an explicit, immutable, per-emission certificate passed to a private loop
renderer core, with existing public wrappers passing no certificate. Its two
recipe kinds are (1) exact IRIndex node -> source window/local-index recipe and
(2) exact dense scalar ElementBinding site -> source window/local-index recipe.
Use source IR identity and operand position/site identity, not names alone.
No mutable ambient current-binding/capability state. This certificate is local
to a selected segment plan and cannot authorize another invocation.

Expected production ownership if approved: `src/CodeGenCuda.fs` constructs the
certificate before removing streamed-source sentinels/rendering loopCode, and
removes the rank-one alias arm; `src/CodeGenLoopNest.fs` consumes the explicit
recipes at scalar peels and kernel rendering. Existing `IRAccess` and `SubstMap`
types can be reused without changing their public semantics. Tests belong in
`tests/ZarrTests.fs`. No provider/runtime ABI change is needed.

### Halo loads

`src/IRAccess.fs:23-69` returns actual IRIndex nodes, including unknown-offset
reads. `src/CodeGenState.fs:1802-1810` keys SubstMap by reference identity.
`src/CodeGenExpr.fs:290-295` consults it before rendering the source Array or its
indices. `CodeGenLoopNest.fs:41` exposes `exprToCppWithSubst`.

Build the certificate over the **final** `codeGen.KernelExpr`, after
`routeKernelBodyThroughCall` and statement-form routing
(`CodeGenCuda.fs:3427-3450`), and after any further body-changing rewrite.
Do not rebuild this IR between census and rendering. At
`CodeGenLoopNest.fs:2104-2114`, once the real final kernel name map exists,
materialize the recipe strings into SubstMap and use it for the scalar kernel
expression. Retain global w; replace only the source load, conceptually:

```
A(w(o))  ->  win[size_t((int64_t(global_w) + signed_o) - int64_t(__w_lo))]
```

The new scalar path must render through `exprToCppCore`/WithSubst, rather than
the current empty-substitution `genKernelExprWithReynolds` call at 2149-2150.
Initially decline Reynolds and array-valued kernel results. The literal-row
arm at 2238-2264 independently uses emptySubst; it is not covered by changing
the scalar expression alone. Named/lifted calls do not inherit a caller's
SubstMap into their bodies. A routed opaque call capturing A therefore refuses.

### Elementwise loads

`genElementBindingPeel` (`CodeGenLoopNest.fs:405-445`) emits an original-array
scalar leaf using a global coordinate. Its caller at 2087-2102 chooses the
site, emits it, and records the resulting scalar name in paramFinalNames.
At this caller, a certified rank-one dense scalar site instead emits the same
scalar declaration/name with RHS `win[size_t(global_i - __w_lo)]`. Deduplication
and parameter mapping remain unchanged. No fake Array is needed, and resident
zip operands keep their existing peel.

Permit simple scalar elementwise kernels (including a named scalar callee only
when its scalar signature and source-free captures are proved). Require every
streamed rank-one input site to be a depth-one original-array scalar peel with
no packed/fused/domain/strict/dependency coordinate transformation. A source
also captured directly by the body needs its own complete index certificate;
the narrow initial implementation can refuse that combination. Two streamed
zip inputs may be admitted only with independently certified peels and equal
checked extents; otherwise refuse. Preserve source read order in either case.

## Complete admission and bounds proof

The current `windowReadsOf` search is not a complete-use proof. Before any
alias mask, sentinel removal or rendering, require all of the following:

1. Known plain dense rank-one source storage and nonnegative static extent N,
   concrete valid segment offsets, one loop dimension, scalar kernel result,
   no Reynolds or unsupported statement/array-result renderer arm. Check every
   actual source/loop binding and read site; do not infer permission from the
   presence of one recognized read. Keep unrelated rank-two/fiber/tile paths.
2. Halo reads have rank one, dimension zero, no prefix, the exact selected
   halo window parameter, and signed literal offset o within the declared
   reach [rLo,rHi]. Re-read the original offset literal as checked Int64;
   `IRAccess.staticOffset` currently narrows Int64 to int at 41-44, which is
   insufficient evidence for a huge-offset case. Computed offsets refuse.
3. A pruning walk treats only certified load nodes as consumed. Every remaining
   reference to the streamed source (including aliases, array arguments,
   shape/type/extent payloads, capture forwarding and escapes) must refuse.
   Analyze callees/captures or reject opaque calls; inspecting their scalar
   caller expression alone cannot prove a hidden source capture absent.
   Pure arithmetic/conditionals/scalar lets over the certified loads can stay.
4. Prove and check arithmetic before emitting it: valid runs inside [0,N],
   nonempty clipped centers [cLo,cHi), valid halo interior, bounded signed
   endpoints, max-window cells and bytes fitting the allocation/index domain.
   Multiple sources must have compatible checked extents. Do not rely on the
   current minimum of source extents as a proof. Reject unproved dynamic or
   overflowing geometry instead of narrowing/wrapping.

For a nonempty halo run let L=cLo+rLo, H=cHi+rHi. Admission must establish
`0 <= L <= H <= N <= INT64_MAX` and `H-L <= allocated_window_cells`.
For every center c in [cLo,cHi) and certified o in [rLo,rHi],
`L <= c+o < H`, hence `0 <= (c+o)-L < H-L`. GenStreamRows fills exactly
[L,H) into win[0,H-L) before the kernel executes. Signed intermediate
arithmetic is proved representable before converting the nonnegative local
index to size_t. Do not cast negative offsets to size_t and rely on wraparound.
For an elementwise site rLo=rHi=0, L=cLo, H=cHi, and the same proof applies.
Empty interiors execute no read/load; preserve the existing minimum allocation
policy rather than introducing a separate empty-runtime optimization.

An unsupported rank-one segment plan must produce a deliberate backend
diagnostic **before** emitting the old loop/alias. The simplest narrow failure
is a spanned `BladeDiagnosticException` using existing backend-limit BL7001
with guidance to use `.read` or the supported static halo form. A mere
`recordCodegenRefusal` plus returning loopCode is insufficient: that channel
only delivers when a refusal marker survives (`CodeGenState.fs:1597-1615`).
Do not silently materialize `.read`: its validation/failure order differs.
No unsafe rank-one alias may remain as a generic fallback.

## Semantics and ownership kept fixed

Retain the current run table, O(N/C) metadata, clipping, traversal order, one
provider call per source per window, and output allocation. Keep the window
owner/deletion in `CodeGenCuda.fs:4130-4165`; no transferred/donor backing,
cache, prefetch, snapshot, bounded cursor or new wrapper lifetime is introduced.
Ghost cells remain real source neighbors, copied by the existing provider.

`src/providers/ZarrProvider.fs:2106-2197` performs window chunk fetch/decode,
dtype conversion/copy and **per-window** missing validation before returning.
Leave it intact, including full padded chunk capacity, partial edge handling,
finite fills/endianness, short/null-read failures and global diagnostics.
Successful validation of the current window precedes all consumer loads.
No chunk from a future window is touched early; no whole-read tally replaces
the window scan. This repair requires no immutable-source promise.

## Proposed focused assertions (not authored or executed)

- Existing N=10,C=4 values 1..10: radius-one result eight 2s; radius-two
  `A(c+2)*A(c-2)-A(c)^2` result six -4s. Existing map gives 2,4,..,20;
  broadcast gives 2,3,..,11; resident zip gives
  0.5,1,1.5,2,2.5,12,14,16,18,20. Keep their plan markers.
- N=11,C=4 with A(j)=(j+1)^2: radius-one results
  [8,12,16,20,24,28,32,36,40]. Include an expression returning Float64(w(0))
  to pin global centers [1,2,3,4,5,6,7,8,9], plus a block with scalar lets and
  repeated source reads. Exercise first/middle/odd-tail and radius two.
- Emission assertions: no `win - __w_lo` (including generic rank-one paths),
  no rank-one Array alias for these streamed inputs, direct bounded local
  loads present, required final output allocation and existing per-window
  read/validation structure present, no full source staging. A zero-length or
  too-small halo interior has no consumer loads; probe signed/huge geometry
  refusals without executing pathological allocations.
- Int64, Float32 and Float64 cases, finite missing-fill and padded-tail NaN
  controls use existing provider conversion/validation. Missing index 0 plus
  later short chunk 2 must report current-window missing before visiting 2;
  missing index 0 plus short/null chunk 1 must report that read failure first.
  A missing value in a later window reports its original global coordinate.
  Compare exact diagnostic text/order with the existing accepted reader route,
  not merely nonzero status. No invalid-pointer build is used as value oracle.
- Refuse computed/huge offsets, out-of-reach or mismatched window parameters,
  mixed certified+uncertified reads, source passed/aliased/escaped, opaque
  source-capturing callee, unsupported array-result/Reynolds and incompatible
  source extents. Assert diagnostic and absence of runnable unsafe fallback.
- Existing rank-two bands, trailing-fiber stream, resident halo and unrelated
  providers retain their valid paths. Repeated public/direct emitter calls
  cannot inherit a prior certificate. A direct low-level renderer with no
  certificate does not manufacture permission for a rank-one segment source.

No additional execution request is needed for this design checkpoint. If
implementation is approved, the coder authors these assertions and freezes the
diff; Luna alone builds/emits/runs the focused Zarr category under root's gate.

## Frozen source evidence (working-file SHA256)

| File | SHA256 |
|---|---|
| src/CodeGenCuda.fs | F2CA557D742C72ECBF1BEA5794BF116BA3C6A7254053FD3C82F91FF5A5EF1ECA |
| src/CodeGenLoopNest.fs | 63E5EAE269B60B5F52EDFCCABB84C16E0A81EBACBB9D938292DD562D8711589E |
| src/CodeGenExpr.fs | 7738FC158243C9F99342C9523C26DBF9D43F763C68B6C4084DB5E799F89DB703 |
| src/CodeGenState.fs | FA92836B366C9BE67D0773276425359B785FAA1478F4F21E2450F35F39B55294 |
| src/IRAccess.fs | 4C28228496E3AEDB4E8565D8243EF9599FE882552D7E245996CF8552F87620D1 |
| src/providers/ZarrProvider.fs | 82EA96945B462D181F3B2D27261E00B66FC4BF91995DE183509AEDC377765F80 |
| tests/ZarrTests.fs | A44D58B808D62263C114F11D4ECD4D89A59BEBF39255CDC15E279232578BBEE4 |

The D01 temporary local type supplement remains untouched on this lane; root
already removed it on integration using C01's shared roots. This document
claims a proposed complete admission boundary, not completed implementation or
measured memory/runtime behavior.
