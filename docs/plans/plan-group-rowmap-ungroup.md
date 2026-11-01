# Plan: the per-group row map between `group_by` and `ungroup` (G90, PM57 full target)

Status: **SPEC ONLY** (round 4 wave 3, lane D item D4). Nothing here is built. The user
decides whether wave 4 builds it. Gap row: G90 (`bench/notes/gap-register.md`).

## 1. What exists today

```blade sketch
type I  = Idx<11>
type CI = Chunked<I, 3>                   // runs [0,3) [3,6) [6,9) [9,11)
let a: Array<Float64 like I> = Float64(0..11) * 3.0 - 1.0 |> compute
let g = group_by(a, segments(CI))         // Array<Float64 like GroupOuter, GroupMember>
let back = ungroup(g, CI)                 // cell for cell `a`
let h = method_for(g) <@> lambda(r) -> r - reduce(r, (+)) / Float64(extents(r)) |> compute
let b = ungroup(h, CI)                    // REFUSED today
```

- `group_by` over a structural grouping (`segments(CI)`, `files(T)`) and `ungroup(g)` /
  `ungroup(g, CI)` are built in every lane that has a `segments` arm (C++, interpreter;
  the LLVM lane refuses `segments` and the program falls back to C++).
- Wave 2 (PM57, `BLADE_GROUP_VIEW`, rule `structural-group-view v1`): a top-level
  structural `group_by` over a never-written plain rank-1 source reads its rows IN the
  source's pool (no CSR copy), and its `ungroup` shares the source (no scatter copy).
- A per-group ROW MAP `h = method_for(g) <@> lambda(r) -> <row expr>` typechecks and
  lowers, but the C++ emitter refuses it: the group-shaped arm of the ragged peel in
  `src/CodeGenCuda.fs` (~line 2716) raises BL7004 "elementwise map over a group_by result
  is not yet supported". The interpreter has no row-shaped grouped map either.
  `docs/features/sql.md` section 7c lists it under "Not yet".
- What users write instead: map BEFORE grouping (only for a row-independent kernel), or
  `reduce` per group and broadcast back by hand -- the per-group normalization above has
  no clean spelling, and that is the feature.

## 2. Surface spelling (no new syntax)

The spelling above IS the feature: `method_for(g) <@> f` over a grouped array, `f` taking
one ROW `r: Array<T like GroupMember>` and returning a row, then `ungroup(h, A)` (or
`ungroup(h)` when `h` keeps `g`'s registration). Optional later form:
`method_for(zip(g, range<GroupOuter>)) <@> lambda(r, k) -> ..` for the group ordinal.
Nothing new is reserved; the BL7004 refusal simply goes away for the admitted class.

## 3. Typing rule

Given `g : Array<T like GO, GM>` from `group_by(v, gk)` with `gk` structural over axis `A`:

- `f : Array<T like GM> -> Array<U like GM>` -- the result row must carry the SAME member
  index `GM` (length preservation is a type fact, proved the way every extent is proved:
  elementwise operations on `r`, broadcasts of row scalars, `range` over `extents(r)`,
  `let rec` over `r`'s axis). Then `h : Array<U like GO, GM>`, and `h` keeps `g`'s
  grouping registration (so `ungroup(h)` resolves without naming the axis).
- `ungroup(h, A) : Array<U like A>` requires `h`'s outer/member to derive from a
  structural grouping of `A` (today's rule for `ungroup(g, A)`).
- A row kernel whose result carries ANOTHER member index (a filter, a fixed-length
  summary `[s, q]`) types as today (a ragged or dense-trailing result); `ungroup` of it
  is refused (BL3016-class extent disagreement, spanned at the `ungroup`), never a
  run-time length check.
- A key grouping (`group_keys(...)`) types the same way; its `ungroup` is a scatter by the
  grouping's permutation (section 5.4).

## 4. Semantics (denotation; formalism 10.x)

For `b = ungroup(method_for(group_by(v, segments(CI))) <@> f, CI)`:
`b(i) = f(g(run(i)))(i - lo(run(i)))`, where `run(i)` is the run containing `i` and `lo`
its first cell. Evaluation order: groups ascending (outer level of the map, as every
`method_for` over a rank-2 value), within a group the kernel's own order. Snapshot: `g`
holds the source as of `group_by` (the copy semantics; PM57's view is legal only when no
write can observe the difference -- unchanged).

Failure semantics: a kernel failure (BL8013 / BL8014 / BL8006 / a panic in a callee) is
raised at the first failing group in ascending order, at the same site as the
interpreter's per-group evaluation; nothing of `b` is observable (the program ends). A
kernel that WRITES captured storage is an ordered serial side effect (G41/G43): groups in
order, no team; under `where omp` it is refused (BL4005) as everywhere. Empty groups do
not arise for `segments` (runs are non-empty); for a key grouping an empty group calls
`f` on an empty row (its folds' empty rules apply: BL8003 for an unseeded fold).

## 5. Emission: destination passing (the storage target)

### 5.1 The pool IS the result
For a STRUCTURAL grouping the rows of `h`, in group order, tile `A` in axis order. So
`h`'s CSR pool laid out in run order is cell for cell `b`. The plan: allocate ONE pool of
`|A|` cells (the required output), run each group's kernel with its DESTINATION row
`pool + lo(run)`, and let `ungroup(h, A)` share that pool when the never-written closure
(`aliasWriteClosure`, PM57's) holds over `h` and `b`. With PM57's view on the input side
the pipeline `a -> g -> h -> b` holds exactly `a` and `b`: no grouped pool, no `h` copy,
no ungroup scatter.

### 5.2 The row kernel into a destination
The admitted kernel grammar renders straight into `dest[0 .. len)`:
- a scalar PRELUDE of folds over `r` (`reduce`, `prodsum`, `extents`) and scalar lets,
  evaluated once per group (the row-normalization shape: mean, variance), then
- a final ELEMENTWISE map of `r` (and prelude scalars, captured scalars) written cell by
  cell into `dest` (the A06 / PM03 same-cell discipline: no row temporary).
A kernel outside this grammar (a `let rec` over the row, a nested map with its own
temporaries, a call returning a row) evaluates into ONE workspace of `max run length`
cells reused across groups, then copies into `dest` -- bounded workspace, never an `h`
pool. A callee returning a row uses the existing return-into-destination ABI where it
applies, else the workspace.

### 5.3 When `h` has other observers
If `h` is printed, peeled (`h(k)`), folded or passed on, it stays a stored grouped value
(row table over the same contiguous pool) and `ungroup` still shares the pool when the
closure holds; when something may write `h` or `b`, `ungroup` copies (today's semantics).

### 5.4 Key groupings (v2)
For `group_keys`, `h`'s pool is in PERMUTED order; destination passing writes each row's
cells straight to `b(perm(k))` (a scatter inside the kernel's store), again without an
`h` pool when `h`'s only observer is `ungroup`. Listed so v1's refusal names it.

### 5.5 Streams and sinks
Over a streamed source (`group_by(A, segments(A))` with `A = s.vars.A |> z.stream`), each
group's row is one run read (built today), so `b` is produced run by run; when `b`'s only
consumer is a provider write, `b` itself need not be stored -- provider-sink-stream
(BLADE_SINK_STREAM, D1 / G91) generalized from a dense map to a run loop (one run of
workspace). That is the bandwidth endpoint of PM57/PM45 combined and is a separate item.

## 6. Interpreter twin

`src/Interp/Loops.fs`: an `IRApplyCombinator` over a grouped value with a one-parameter
row kernel evaluates the kernel per group in ascending order on the row as an array value
(the grouped value's row view), checks nothing at run time (the type proved the length),
and returns a grouped value with `g`'s registration; `ungroup` already reassembles a
grouped value. The interpreter keeps copies (it is the oracle); `test interp` covers the
corpus; `blade test opt-diff` compares the storage plan's gate off vs on.

## 7. LLVM lane

The LLVM lane has no `segments` arm: the program falls back to C++ (recorded, as for
every `segments` program today). Building `segments` there is its own item.

## 8. Plan rule, knob and records

Rule `group-rowmap v1`, knob `BLADE_GROUP_ROWMAP` (in `optimizerGates`) for the STORAGE
plan only (5.1-5.3); the feature itself (lifting the BL7004) is not gated. Records:
`applied [h's pool is b's; kernel into destination | workspace of N cells; ungroup shares]`
or `declined -- <first failed condition>` (a written source / h / b, a key grouping in v1,
a kernel outside the destination grammar -> workspace, MPI, a CUDA module, not main's top
level). Five-party write audit through `scheduleMayWriteIn` + `aliasWriteClosure`
(extents included, the G89 lesson).

## 9. Test plan

- corpus `segments/0xx` (values, both lanes + interpreter): elementwise row `r * 2.0`;
  per-group normalization `r - mean(r)`; a captured scalar; an odd tail run; `ungroup(h)`
  and `ungroup(h, CI)`; `h` also printed (5.3); a `files(T)` grouping; a kernel needing
  the workspace (`let rec` running sum over the row).
- refusals: a non-length-preserving row then `ungroup` (spanned BL3016-class); a key
  grouping `ungroup` in v1 (named refusal); a writing kernel under `where omp` (BL4005);
  a failing kernel (BL8013 in group 2: same site and order as the interpreter).
- `blade test optimize`: the plan's applied / declined records; the emitted C++ holds no
  grouped pool, no `h` pool and no ungroup copy on the admitted shape.
- `blade test zarr`: the streamed-source spelling (one run read per group).
- measurement: peak working set at an odd extent (e.g. 3,000,001 cells, runs of 1001),
  gate on vs off, 5 interleaved runs; expected `a + b` vs `a + g + h + b`.

## 10. Questions for the user

1. Is `f : Array<T like GM> -> Array<U like GM>` (length preservation as a TYPE fact) the
   rule you want, or a run-time length check (BL8001) for kernels the type cannot prove?
2. v1 structural groupings only, key groupings (scatter) in v2 -- agreed?
3. The group-ordinal form `lambda(r, k)`: in v1 or later?
