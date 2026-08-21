# Simplex-blocked compute — running symmetric structure on rectangular backends

**Status (2026-08-18): P0 + P1-for-rank-2 IMPLEMENTED on the LLVM lane, and MEASURED —
the P1 gate DOES NOT PASS. See §0.** Everything from §1 down is the 2026-08-17 spec,
kept verbatim as the prediction the implementation was scored against.

---

## 0. Measured result 2026-08-18 — S0 at rank 2 costs ~7%, buys nothing

**Landed** (`feat/llvm-backend`, 4bf4234): §8.1's P0 deliverable as `src/SimplexBlocksCore.fs`
— a compute-side copy of the SimplexBlocks identities, *deliberately* not a lift of the
provider's module (the provider needs the equal-size quadtree for MPI ownership; compute
coarsens off-diagonal triangle pairs into unequal bricks — §6's MPI row said so, and the
file's header says why at length). The two are pinned EQUAL over a grid of
(n, B, r, symmetry) by `blade test llvm goldens`: 16 agreement assertions, 0 failed.
§8.2's `BrickPlan` exists inside `src/EmitLlvm.fs` as the rank-2 blocked nest: dense B×B
off-diagonal tiles in ascending-lex order plus a serial diagonal triangle, which is
exactly §8.2's "leaf simplex blocks run serial triangular" policy. `BLADE_LLVM_BRICKS` is
§8.3's measurement-only override, read per call: `off`/`0`/`none` forces the serial
triangle, a bare number pins the tile edge B, anything else defers to the derived default
(B = 64, blocks only above n = 64).

**Correct**: `blade test llvm blocks` runs every symmetric program three ways — C++ lane,
llvm serial, llvm bricked at two tile edges chosen so the ragged last tile lands
differently — and demands cell-for-cell agreement. 10 passed, 0 failed. A fourth
assertion checks the knob is *live* (the bricked `.ll` must differ textually from the
serial one), so a silently inert decomposition cannot pass.

**Not profitable, at S0, at rank 2** (`blade test llvm-bench`, non-power-of-two extents,
arms rotated round-robin, 27 samples per arm, medians):

| shape | llvm-serial | llvm-bricked | verdict |
|---|---|---|---|
| symmetric map, n = 2003 (2 007 006 cells) | 4.255 ms | 4.239 ms | indistinguishable; both inside a 2.8–6.3 ms spread |
| symmetric map, n = 6007 (18 048 028 cells) | 31.303 ms | 34.125 ms | **bricked 1.07x SLOWER** |

§9's P1 gate reads "parity-or-better with ≥1 clear win". No shape showed a win; the
larger, better-resolved shape showed a loss. **The gate does not pass**, and §10's risk 1
("measure-first; the 2.14x packed precedent") is the reason it was written that way.

**What this does and does not kill.** It is a negative result on **S0 only** — brick
iteration over the existing packed pool, the variant that changes no layout and was
picked because it is cheapest. The hypothesis in §9 P1 was that bricks pay *zero*
per-cell canonicalization, which is what the 2.14x paid; that much is true, and it still
did not pay, which says the triangular nest's non-uniform bounds were not costing what
the spec assumed. Untouched and unmeasured: **S1** (brick-major layout — contiguous,
alignable, fully affine tiles, which is where the BLAS/`linalg`/`cuda_tile` claims in §6
actually live), the §4b mirror-expansion contraction modes, and rank ≥ 3 prisms. Those
are where the plan's real claim is, and none of them is refuted by this.

**Shipping posture — RESOLVED 2026-08-18, same branch.** The default is now the serial
triangle at every extent: `SimplexBlocksCore.autoTileEdge` returns `None`
unconditionally, so `BLADE_LLVM_BRICKS` unset never blocks — the default emission is the
fastest measured path, which is what "the fastest way is the only way" demands. (The
earlier auto policy returned `Some 64` above n = 64 and its comment called that "the
simplest thing that cannot lose"; the measurement falsified exactly that sentence, so
the policy went.) The bricked nest stays reachable through an explicit
`BLADE_LLVM_BRICKS=<B>` — the bench's bricked arm now pins B = 64 — because the knob is
how the next variant (S1 brick-major layout, or a §4b mirror-expansion contraction)
gets A/B'd against a control already proven correct by the three-way gate. The
crossover extent, if one exists, remains unmeasured; a nonzero derived policy returns
only with a measurement that beats serial at matched extents.

**Second measurement 2026-08-18 — the divisibility control (raggedness ruled out).**
The first verdict was read at PRIME extents, where every brick row ends ragged — a fair
objection, since real extents are usually composite and an exact tile divisor kills
raggedness entirely (the count of 2s in n's factorization bounds how deep exact halving
can recurse). Tested at n = 6006 = 2·3·7·11·13, matched work (C(6007,2) cells, 0.05%
below the prime shape), four arms: serial **27.337 ms**; B = 64 ragged **29.519 ms**
(1.08x); B = 66 exact divisor, T = 91, zero ragged tiles **29.277 ms** (1.07x). Exact
divisibility recovers ~0.24 ms of a ~2.2 ms penalty — raggedness is real but explains
roughly a tenth of the loss. The remainder is S0-structural: the serial triangle's
inner loop runs contiguous packed spans up to n long, while a brick chops them into
B-wide restarts whose columns are NOT contiguous across rows of the packed pool. That
is exactly the cost **S1 brick-major layout** removes (a brick becomes B·B contiguous
scalars), so the control result *sharpens* the S1 motivation while closing the S0
question: the serial default stands even at divisible extents.
`SimplexBlocksCore.divisorTileEdgeIn` — the brick-only-when-a-divisor-exists candidate
policy — stays in the tree as measurement infrastructure, adopted as a default only if
an S1-class variant wins with it.

**Third measurement 2026-08-18 — the decomposition (H1 owns the map penalty; reuse
pressure flips the verdict). HANDOFF: measured in scratchpad, patch NOT yet landed.**

Written as a handoff after a hard stop on concurrent compute: everything below was
measured; the patch is specified, not applied. Probe artifacts live in the session
scratchpad under `bricks/` and can be re-run as-is: `serial.ll` / `brick64.ll` /
`brick66.ll` (+ compiled `.exe`s — the real emitted arms of `bench_sym_divisible`),
`twins.c`/`twins.exe` (the four-variant decomposition), `reuse.c`/`reuse.exe`/
`reuse_fast.exe`/`reuse_results.txt` (the reuse-pressure shape),
`gram_probe.blade`, `gram_bench.blade` and `gb_serial.exe`/`gb_brick66.exe`/
`gb_serial_ra.exe`/`gb_brick66_ra.exe` (bench-sized lane arms, **built but not yet
timed**; `_ra` = emitted under `BLADE_FP_REASSOC=1` — the licensed-reassociation
arms, nothing to do with affinity). Machine note: same 5800HS as the §0 runs but in
a slower power state (the serial arm reproduced at 38.27 ms vs the recorded 27.34;
rotated 13-rep medians serial 38.27 / brick64 40.43 / brick66 39.77 — ordering and
ratios transfer, absolutes do not).

*Hypothesis verdicts, four tested:*

- **H3 (vectorization asymmetry) — REFUTED.** clang 22.1.8 `-O3 -march=native`
  `-Rpass=loop-vectorize -Rpass-missed=loop-vectorize` on the real emitted arms:
  `serial.ll` vectorizes 2/2 loops (VF 4, IC 4), `brick66.ll` 5/5 (VF 4, IC 4),
  zero missed-vectorization remarks on either.
- **H2 (per-row base recompute) — REFUTED.** The `brickinc` twin (row base and
  brick write base strength-reduced across brick rows, no per-row mul/div) is
  indistinguishable from `brick` in every column of the table below. The emitted
  `sdiv .., 2` is folded to shifts by instcombine; clang already strength-reduces
  what matters.
- **H1 (pool-write scatter) — CONFIRMED, owns the loss.** The `brickseq` twin
  keeps the brick iteration and the brick READ pattern byte-for-byte but stores
  through a running cursor (brick-major sequential order — an S1-layout write
  probe). It recovers most of the gap; what it does not recover is short-loop
  structure overhead, which the twin exaggerates (its tile edge is a runtime
  argument; the emission's trip counts are literals — hence the real arms' total
  gap is smaller than the twin's warm ratio).
- **H4 (the bench shape cannot reward blocking) — CONFIRMED.** The map's operand
  `v` is 48 KB: L2-resident, so bricks have nothing to reuse, and the serial
  triangle's writes are pool-sequential — optimal by construction for a
  pool-output map. The proposed "i-band panel" salvage is vacuous for maps: a
  band whose rows each run the full remaining row IS the serial order. **No brick
  variant can beat serial for a pool-output map over a cached operand.**

*The twin table* (`twins.c`, n = 6006, pool 137.6 MB, 9 rotated reps, medians;
checksums exact — v(i) = 1 + i/2 makes every product an exact quarter. `cold` =
fresh demand-zero pages + fill; `warm` = immediate refill; `arm` = memset then
fill, which is what `blade_alloc_cells` + map actually does):

| variant, B = 66 | cold | warm | arm = memset + fill |
|---|---|---|---|
| serial | 28.653 | 17.497 | 14.823 + 17.387 = 32.210 |
| brick (emission twin) | 58.842 | 23.053 | 15.861 + 23.294 = 39.155 |
| brickinc (H2 probe) | 59.103 | 23.350 | 15.757 + 23.011 = 38.768 |
| brickseq (H1 probe) | 30.014 | 19.923 | 14.701 + 19.409 = 34.109 |

(B = 264 repeats the picture: serial 28.7/19.9, brick 56.4/22.1, brickseq
29.4/21.0 cold/warm.) Reading: of the ~5.6 ms warm fill gap, write scatter
(brick − brickseq) is ~3.4–3.9 ms; the residue is short-loop structure, inflated
here by the runtime tile edge. Two side findings: (a) demand-zero first-touch
punishes scattered writes **2x** (cold column) — invisible in the shipped arms
only because `blade_alloc_aligned_zeroed` (src/cpp/blade_llvm_shim.c) memsets
eagerly, pre-faulting pages sequentially for both arms inside the timed region;
(b) that memset is ~15 ms of the ~32 ms serial arm and is dead work for a total
map (cold 28.65 < arm 32.21, an ~11% win) — a follow-on for a separate change,
out of scope here.

*The reuse-pressure flip* (`reuse.c`: s(i,j) = Σ_k bm(i,k)·bm(j,k), d = 402,
B = 66, exact-quarter checksums agree across arms; `strict` mirrors Blade's
order-preserving default, `fast` = `-ffast-math`, the C proxy for the licensed
`BLADE_FP_REASSOC` arm — verified on the lane: `gram_probe.blade` emitted under
`BLADE_FP_REASSOC=1` carries `fadd reassoc nsz` on exactly the kernel's fold
accumulator, and its serial/bricked arms print identical values):

| shape (operand size) | serial ms | brick-66 ms | verdict |
|---|---|---|---|
| strict, n = 3001 (bm 9.2 MB) | 1638.6 | 1560.0 | brick 1.05x faster |
| strict, n = 6006 (bm 18.4 MB > 16 MB L3) | 6947.1 | 6260.2 | brick 1.11x faster |
| reassoc, n = 3001 (9.2 MB) | 264.3 | 191.1 | brick 1.38x faster |
| reassoc, n = 6006 (18.4 MB) | 2200.3 | 797.4 | **brick 2.76x faster** |

The strict n = 3001 row is latency-bound on the unvectorizable `acc +=` chain
(~2.2 GFLOP/s), which masks memory effects; reassociation exposes them. This is
§6's second row made concrete: bricks pay exactly when the kernel re-reads
O(row) operand data per cell and the operand cannot stay cached — S0, no layout
change, wins up to 2.76x on the very nest that loses 1.07x on the map.

*The patch (exact; anchors given by symbol — line numbers are against a working
tree that still carries the in-flight IRForRange hunks and WILL drift):*

1. `src/SimplexBlocksCore.fs` — keep `autoTileEdge _ = None` (line 267,
   unchanged: maps stay serial). Add below it:

   ```fsharp
   /// The derived tile edge for a rank-2 simplex whose PRODUCER re-reads
   /// O(row) operand data per cell (row-operand kernels). Measured
   /// 2026-08-18 (plan section 0, third block): bricks win 1.05-2.76x once
   /// the row-operand working set is ~9 MB or more; raggedness is noise at
   /// that scale. Prefer an exact divisor near 64; fall back to 64 ragged.
   let reuseTileEdge (n: int64) : int64 option =
       if n <= 128L then None
       else
           match divisorTileEdgeIn 32L 128L 64L n with
           | Some b -> Some b
           | None -> Some 64L
   ```

2. `src/EmitLlvm.fs`, `type private ArrVal` (~line 418): add field
   `RowOpBytes: int64` — bytes of the largest operand a kernel parameter binds
   as a ROW VIEW, 0 when every bind is a scalar. All 12 explicit
   `{ Elem = ...; Groups = ...; Src = ... }` constructions gain
   `RowOpBytes = 0L` (the compiler enforces the sweep); `{ a with ... }`
   updates need nothing.

3. `src/EmitLlvm.fs`, `applyToArr` (~line 2343) — the loop-nest producer. In
   the non-flat branch (the `AVirt` record right after the `flatEligible`
   short-circuit, ~line 2483), compute at CONSTRUCTION time (not per cell),
   using the same peel test `bindKernelParam`'s `KArray (rowView ...)` site
   makes (~line 2547):

   ```fsharp
   let rowOpBytes =
       positions
       |> List.choose (fun pos ->
           match List.item pos operands with
           | Some a when (levelsOf pos |> List.length) < List.length a.Extents ->
               Some (shapeCells a.Groups * poolElemBytes a.Elem)
           | _ -> None)
       |> function [] -> 0L | xs -> List.max xs
   ```

   and set `RowOpBytes = rowOpBytes` on that record only (the flat-eligible
   record keeps `0L` — flat operands bind scalars by definition).

4. `src/EmitLlvm.fs`, `brickTileEdge` (~line 1188): new signature
   `brickTileEdge (licensed: bool) (rowOpBytes: int64) (n: int64)`.
   Unlicensed → `None` (unchanged, the gate); `BrickOff` → `None`;
   `BrickFixed b` → unchanged; `BrickAuto` →
   `if rowOpBytes >= reuseThresholdBytes then SimplexBlocksCore.reuseTileEdge n
    else SimplexBlocksCore.autoTileEdge n`. Next to it:

   ```fsharp
   /// Bricks pay only when the serial traversal re-streams a row-operand
   /// working set that cannot stay cached. Smallest measured winning set:
   /// 9.2 MB (1.38x reassoc / 1.05x strict); 18.4 MB won 2.76x / 1.11x
   /// (section 0, third block). The threshold sits just under the smallest
   /// measured win; below it the serial triangle keeps the proven-fastest
   /// default. The principled form is "operand bytes > outermost-cache
   /// capacity"; 8 MiB is the measured stand-in until a cache probe exists.
   let [<Literal>] reuseThresholdBytes = 8388608L
   ```

   Tile-edge sizing note for the future refinement: the brick read set is
   2·B·rowBytes and should target ~half of L2 (B = 66 at d = 402 is 424 KB
   against a 512 KiB L2 — measured good); a derived
   `B ≈ L2/(4·rowBytes)` clamped to [32, 128] and divisor-preferred is the
   formula, with divisor-near-64 as today's starting policy.

5. Call sites: `materialize`'s `soleSimplex2` branch (~line 2249) becomes
   `brickTileEdge true a.RowOpBytes n`; `emitCompactFold`'s
   `match brickTileEdge licensed n` (~line 2782) becomes
   `brickTileEdge licensed 0L n` — fold bricking stays knob+licence-only
   (compact sym folds are BL3999-refused upstream regardless).

6. `tests/LlvmTests.fs`, the blocks gate's direct assertions (lines 892–911):
   every `EmitLlvm.brickTileEdge <lic> <n>` gains a middle `0L`. Add three
   assertions under a knob-unset `withEnv`: `brickTileEdge true 9000000L 6006L
   = Some 66L` (reuse hint bricks, divisor preferred), `brickTileEdge true
   8388607L 6006L = None` (below threshold stays serial), `brickTileEdge false
   9000000L 6006L = None` (the licence still dominates the hint).

   What stays knob-only after the patch: `BLADE_LLVM_BRICKS=<B>` forces bricks
   for any licensed nest and `off` forces serial everywhere (both override the
   hint — measurement use, §8.3); plain maps (`RowOpBytes = 0`) stay serial at
   every extent, which is the second-block verdict, untouched.

*The new fixture* — `tests/fixtures/llvm/bench_sym_gram.blade`, verbatim (this
exact source emitted and compiled through both arms of the lane already; the
built exes are the `gb_*` scratchpad artifacts):

```blade
// RUNTIME SHAPE 3d: reuse pressure -- the shape class where S0 bricks WIN.
//
// The sym-map shapes (3a-3c) read a 48 KB operand: L2-resident, nothing for
// blocking to reuse, and the serial triangle's pool-sequential writes are
// unbeatable (the 2026-08-18 decomposition: write scatter owns the bricked
// penalty there). This shape is the other class: s(i,j) = dot(bm(i), bm(j))
// re-streams an 18.4 MB row-operand matrix (6006 x 402 doubles, larger than
// the reference machine's 16 MB L3) once per output ROW serially, but once
// per row-TILE bricked -- measured in C twins at 2.76x (reassoc) / 1.11x
// (strict) in favor of B = 66 bricks. Extents are composite non-powers of
// two (6006 = 2*3*7*11*13, 402 = 2*3*67); 66 divides 6006 exactly.
// Values are exact quarters, so every association prints identical bits.
type Sx = Idx<6006>
type Kx = Idx<402>

let bm = method_for(range<Sx>, range<Kx>) <@> lambda(i, k) -> 1.0 + 0.5 * ((i * 31 + k) % 7) |> compute
let s = method_for(bm, bm) <@> lambda(x: T^1, y: T^1) where comm(x, y) -> reduce(x * y, (+)) |> compute
let probe = s((0 : Sx), (0 : Sx)) + s((7 : Sx), (6005 : Sx)) + s((3001 : Sx), (3001 : Sx))
```

Its `rtShapes` entry (`tests/LlvmTests.fs`, append after the divisible shape,
~line 1689). All four arms pin `BLADE_FP_REASSOC=1`: the reuse win is a
memory-system fact but the strict arms are latency-bound and slow (~7 s/run);
values are exact quarters so the oracle comparison stays exact under any
association. The `llvm-auto` arm carries NO bricks pin — it exercises the
derived default and must land on the brick-66 time after the patch (before the
patch it equals llvm-serial, which is itself a useful pre/post A/B):

```fsharp
      { Name = "symmetric gram, n = 6006, d = 402 (row-operand reuse; bm = 18.4 MB > L3)"
        Fixture = "bench_sym_gram"
        Note = "the shape class where S0 bricks win: serial re-streams bm per output row; all arms reassoc-licensed (cells are exact quarters, so association cannot move the printed values)"
        Arms =
          [ { armCpp with Pins = [ "BLADE_FP_REASSOC", Some "1" ] }
            { Label = "llvm-auto"; Lane = LaneLlvm; Pins = [ "BLADE_FP_REASSOC", Some "1" ] }
            { Label = "llvm-serial"; Lane = LaneLlvm; Pins = [ "BLADE_FP_REASSOC", Some "1"; "BLADE_LLVM_BRICKS", Some "off" ] }
            { Label = "llvm-brick-66"; Lane = LaneLlvm; Pins = [ "BLADE_FP_REASSOC", Some "1"; "BLADE_LLVM_BRICKS", Some "66" ] } ] }
```

Cost: ~0.8–2.2 s/run × 4 arms × 30 runs ≈ 2.5–4 min added to the standalone
bench block; if too heavy, a per-shape `Reps` override on `RtShape` is the
maintainer's call. Do NOT add the fixture to `codegenPrograms` (~line 1488) —
that table's six-program identity is pinned in §0.2 of the llvm plan.

*Verification list, in order:*

0. No patch needed: time the prebuilt scratchpad arms `gb_serial(.exe)`,
   `gb_brick66`, `gb_serial_ra`, `gb_brick66_ra` (rotated, ≥9 reps, medians).
   EXPECT: all four print the same `probe`; brick66/serial ≈ 0.85–0.95x
   strict and ≈ 0.30–0.50x reassoc. This is the end-to-end lane confirmation
   of the C-twin table above and the one measurement Phase A did not finish.
1. Wait for the `llvm: the IRForRange arm` commit; `git status --short` clean.
2. Apply the patch; `dotnet build Blade.fsproj -c Release`.
3. `blade test llvm` — EXPECT green at baseline counts. A missed
   `brickTileEdge` call-site arity shows as a build error, not a red.
4. `blade test llvm-bench` — EXPECT shapes 3a–3c unchanged (auto never bricks
   a map: `RowOpBytes = 0`); on 3d, `llvm-auto ≈ llvm-brick-66`, both well
   under `llvm-serial`. If `llvm-auto` matches `llvm-serial` instead, the
   hint is not reaching `materialize` — check the `RowOpBytes` threading and
   that arm pins land after `defaultEmissionEnv` in `buildRtArm` (~line 1695).
5. C++ regression: `blade test basic` / `functions` / `loops` — EXPECT green
   (the C++ lane is untouched by every hunk above).
6. Record the harness tables here as a fourth dated block; commit
   `llvm: ...` per house format.

Follow-on flagged, separate change: the eager memset in
`blade_alloc_aligned_zeroed` is dead work for total maps (~11% of the sym-map
arm at 137 MB); demand-zero allocation would keep the zero guarantee for
fresh pools without paying 2x traffic.

**Patch LANDED 2026-08-18 — verified structurally; the runtime table is NOT
re-measured on this machine.** The handoff's patch is applied exactly as
specified: `reuseTileEdge` (SimplexBlocksCore.fs), `ArrVal.RowOpBytes` with
all twelve construction sites swept, the hint computed once in `applyToArr`'s
non-flat branch, `brickTileEdge licensed rowOpBytes n` with
`reuseThresholdBytes = 8 MiB`, both call sites updated (the fold passes `0L`
— folds re-read nothing, and their real future is §5.6's row bins in
plan-compact-sym-folds.md), the `bench_sym_gram` fixture, and its four-arm
`rtShapes` entry.

What was verified, and how:

- `blade test llvm` — **348 passed, 0 failed, 364 skipped** (was 347; the new
  assertion is the reuse gate: hint ≥ 8 MiB bricks with the divisor edge
  (66 at n = 6006, falling back to 64 ragged at prime 6007), one byte under
  stays serial, and an unlicensed fold stays `None` whatever the hint says).
- **The hint reaches emission**, proved textually rather than by timing: the
  gram fixture emitted with no knob differs from `BLADE_LLVM_BRICKS=off` and
  is **byte-for-byte identical to `BLADE_LLVM_BRICKS=66`** — the derived
  policy independently picks the exact edge the measurement chose.
- The C++ lane needs no separate regression run here: every hunk is inside
  `EmitLlvm.fs`/`SimplexBlocksCore.fs`/tests, reachable only under
  `BLADE_LLVM`, and the 276 differential tests in the suite above each
  compile and run the C++ lane as their oracle.

What is NOT verified: the end-to-end **runtime** table for shape 3d. The
host crashed repeatedly under sustained compiler/benchmark load (three
bugchecks in ninety minutes, the last `VIDEO_MEMORY_MANAGEMENT_INTERNAL`,
with the shell dying before the OS), so the four-arm bench was not run. The
C-twin numbers above (2.76x reassoc / 1.11x strict at 18.4 MB) remain the
only evidence for the win itself, and they were measured in the scratchpad,
not through the lane. **Anyone with a healthy machine should run
`blade test llvm-bench` and record shape 3d as the fourth dated block**; the
expectation is `llvm-auto ≈ llvm-brick-66`, both well under `llvm-serial`.
Until then the threshold is a measured-elsewhere constant, and that is the
honest status.

**Fourth measurement 2026-08-18 evening — one bricked sample; correctness yes,
verdict no.** After the print fix (248021e), on a restarted host with Windows
Max processor state capped at 70%:

| arm | reps | power | time | probe |
|---|---|---|---|---|
| llvm-serial (`BRICKS=off`) | 5 | 100% | **9.84 s** median (8.12–10.33) | 8217.5 |
| llvm-bricked (derived policy) | 1 | 70% | **9.077 s** | 8217.5 |

What this establishes: **correctness end-to-end through the lane** — both arms
print 8217.5 to the digit at n = 3001, which until now had only been shown in
the C twins and in the three-way `blade test llvm blocks` gate.

What it does NOT establish: the win. The bricked sample is 1.08x under the
serial median *while power-capped* (a bias against bricks, so the direction is
real), but it lands INSIDE the serial spread, and one sample against a
five-sample median is not a measurement. A fair comparison needs both arms at
the same power state.

One observation worth more than the timing: ~1.8 GFLOP in ~9 s is ~0.2
GFLOP/s, an order of magnitude off what AVX2 should deliver on this shape. The
C twins timed the gram fold ALONE and saw 1.38x; the whole Blade program also
builds `bm` (1.2 M cells) and allocates the 4.5 M-cell output, so the fold is
not the dominant term here and any brick win is diluted. Before spending more
runs on wall-clock arms, put a timer around the gram fold itself (or shrink
`bm`'s construction cost) so the thing being measured is the thing the
decomposition changes.

**Fifth measurement 2026-08-18 — matched power, five samples each, and a
baseline that reframes the shape.** Host restarted, Windows Max processor
state capped at 70%, one minute of idle between batches, `bench_sym_gram_small`
(n = 3001, bm 9.2 MB, reassoc-licensed):

| arm | samples | median | range | probe |
|---|---|---|---|---|
| baseline `bench_sym_gram_bmonly` (no fold) | 5 | **0.0039 s** | 0.0036–0.0042 | 4.5 |
| llvm-serial | 5 | **8.277 s** | 7.39–9.28 | 8217.5 |
| llvm-bricked (derived policy) | 5 | **7.782 s** | 7.13–8.65 | 8217.5 |

**The baseline is the important row.** `bench_sym_gram_bmonly.blade` is the
same program with `s` deleted — same extents, same `bm`, same probe shape — so
it prices process start, the 1.2 M-cell `bm` build and the 36 MB pool's
allocation and first touch. It comes to **4 ms against a ~8 s program**: the
fold is 99.95% of the runtime. The earlier suspicion that the whole-program
timing was diluted by setup is therefore WRONG, and the totals above are fold
times to three digits. (Subtraction is left available anyway; the fixture is
knob-independent by construction, since no simplex domain appears in it.)

**The brick verdict at matched power: suggestive, not significant.** Bricked is
**1.064x** faster at the median and wins **18 of 25** pairwise comparisons, but
Mann-Whitney U = 7 where n = 5,5 needs U ≤ 4 for one-tailed p < 0.05, and the
ranges overlap substantially. Five samples cannot resolve a 6% effect on a
spread this wide. What IS solid is correctness: **8217.5 to the digit on all
ten runs plus the single earlier one**, so serial and bricked agree end to end
through the lane.

**Why 1.064x and not the C twins' 1.38x — the finding worth more than the
verdict.** 3.62 GFLOP in ~8 s is **0.44 GFLOP/s**, one to two orders of
magnitude below what AVX2 delivers on a dot-product shape, and the 4 ms
baseline proves the time is genuinely in the fold rather than around it. The
twins hit 1.38x on a fold running near memory bandwidth, where locality is the
binding constraint; here some per-cell cost dominates so completely that
improving locality moves the total by only 6%. The prime suspect is a
materialized temporary per output cell — `reduce(x * y, (+))` over two row
views, where `x * y` should stay a deferred producer consumed inside the fold
but may be forced by one of the five documented declines
(docs/plans/plan-deferred-combinators.md, `tryInferReduceCompute`). 4.5 M
allocations of a 402-element array would explain both the throughput and the
memory pressure.

**So the ordering is now clear: fix the fold's per-cell cost first, then
re-measure bricks.** Any brick verdict taken against a fold running 50x slow is
measuring the wrong bottleneck. Concretely: check whether this shape's `x * y`
materializes (emit the `.ll` and look for an alloc in the cell loop), and if it
does, that is a deferred-combinators D-phase item whose payoff dwarfs the
decomposition's.

*Machine note, unexpected and worth keeping:* the **70%-capped** serial median
(8.277 s) is FASTER than the uncapped one measured earlier the same evening
(9.84 s, range 8.12–10.33). A power cap that improves throughput is the
signature of an uncapped machine throttling itself, which is consistent with
the day's crash history. Benchmarks on this host should be run capped.

**Sixth measurement 2026-08-18 — THE P1 GATE PASSES, on the shape the earlier
verdicts could not see.** The four preceding blocks all measured symmetric MAPS,
where bricks lose (~7%) because the serial triangle's writes are pool-sequential
and the operand is cache-resident — H1 and H4, both confirmed. What none of them
could measure was a fold, because `reduce(x * y, (+))` allocated and refilled a
402-cell temp *per output cell*: at ~0.44 GFLOP/s the traversal was 50x off, and
locality cannot show through a constant that large. The fold-fusion fix
(plan-llvm-backend.md §0.7, commit 5a7b119) removed the temp. Re-measured
immediately (`blade test llvm-bench`, `bench_sym_gram_small`, n = 3001, d = 402,
bm = 9.2 MB, reassoc-licensed, 27 samples per arm, medians):

| arm | inner median | vs cpp | vs llvm-serial |
|---|---|---|---|
| cpp | 829.9 ms | 1.00x | — |
| llvm-serial (`BRICKS=off`) | 232.6 ms | 0.28x | 1.00x |
| llvm-brick-64 | 208.7 ms | 0.25x | **1.11x faster** |
| **llvm-auto (derived policy)** | **207.3 ms** | **0.25x** | **1.12x faster** |

**§9's P1 gate reads "parity-or-better with ≥1 clear win". This is the win**, and
it arrives without a layout change — S0, brick iteration over the existing packed
pool, exactly the variant four measurements had called unprofitable. The map
verdict is not overturned; it is *bounded*: bricks cost ~7% where there is no
operand reuse and pay where there is, which is precisely what §6's second row
predicted and what the reuse-hint threshold (8 MiB, plan-llvm-backend.md) keys on.
`llvm-auto` carries no knob at all and lands on the bricked time — the derived
policy picks the winner by itself.

Two honest notes. The 1.12x here is smaller than the C twins' 1.38x at the same
size, and the whole-program measurement includes building `bm`; the twins timed
the fold alone. And the *serial* llvm arm is already 3.6x faster than the C++
lane on this shape — because the C++ lane still materializes the per-cell temp
(it frees it, so it is slow rather than fatal). Most of the 4x is fusion; the
brick decomposition is the last 12%.

**Seventh measurement 2026-08-18 — the licence×brick interaction at n = 6006,
and a control lesson.** Same protocol (interleaved arms, 27 samples, medians),
`bench_sym_gram` (n = 6006, d = 402, bm = 19.3 MB — twice the sixth block's
operand), full 2×2 over `BLADE_FP_REASSOC` × `BLADE_LLVM_BRICKS`:

| arm | median | vs serial, same licence |
|---|---|---|
| serial (`BRICKS=off`) | 5.202 s | 1.00x |
| brick-64 | 4.939 s | 1.05x |
| serial + reassoc | 1.422 s | 1.00x |
| brick-64 + reassoc | **0.837 s** | **1.70x** |

Three readings. (1) **The reassociation licence is the big lever and it is the
SIMD switch**: the vectorization census (clang `-Rpass=loop-vectorize` over the
emitted `.ll`) shows the fused fold's every loop refused with
`CantReorderFPOps` until the licence grants `reassoc`, after which all of them
vectorize — the llvm lane's exact counterpart of the C++ lane's
`omp simd reduction`. Licence alone: 3.66x serial, 5.9x bricked. (2) **The
licence amplifies bricks**: 1.05x unlicensed grows to 1.70x licensed, because a
vectorized fold is memory-bound and cache blocking then pays at full weight —
H1's reuse story, now measured through the SIMD regime it was always going to
live in. Combined, licence + bricks = **6.2x** on this shape, on top of the
fusion win. (3) The 1.70x at bm = 19.3 MB versus 1.12x at 9.2 MB says the reuse
win GROWS with the operand past the cache, as §6 predicted.

*The control lesson, so nobody repeats it:* with `BLADE_LLVM_BRICKS` UNSET this
fixture auto-bricks — the RowOpBytes reuse hint fires at 19.3 MB ≥ 8 MiB — so an
"unset" arm is NOT a serial control (a first pass here measured brick-66 against
brick-64 and read "bricks are flat"). The serial control must pin `BRICKS=off`,
which is exactly how `blade test llvm-bench` already pins its arms. The auto
policy itself is vindicated twice over: it fires on this shape and lands on the
winning emission at both measured sizes, still with no knob in the program.

## 0a. Rank r, measured 2026-08-19 — the simplex emitter is arbitrary-rank, and r! is 92-95% delivered

The llvm lane refused every compact group of rank ≥ 3. It no longer does, and
nothing in the new code is per-rank: `SimplexBlocksCore.prefixTerm` gives level
k's contribution in closed form (the hockey-stick collapse of `rankOfCoords`'s
inner sum into a difference of two binomials), `emitSimplexSerialR` threads it
down an r-deep nest hoisting each term to its own level, and `canonRead`
canonicalizes by sorting network with the antisym sign as the exchange PARITY.
The two degenerate cases are what make it trustworthy: at r = 2 the formula IS
`rowBase2`, and at the last level it collapses to `i - lo`, so the innermost run
stays affine and pool-contiguous at every rank.

**The measurement** (compact vs dense at matched n and matched kernel — the
dense arm is the same program with the `where comm(...)` clause deleted, which
is the r! control the corpus already uses; non-power-of-two extents, interleaved
arms, 27 samples per arm, medians; probe values identical across all four arms):

| shape | lane | compact | dense | ratio | of theory | ns/cell compact |
|---|---|---|---|---|---|---|
| r = 3, n = 301 (4 590 551 vs 27 270 901 cells; theory **5.941x**) | cpp | 8.46 ms | 45.59 ms | 5.39x | 91% | 1.84 |
| | **llvm** | 9.16 ms | 49.92 ms | **5.45x** | **92%** | 2.00 |
| r = 4, n = 61 (635 376 vs 13 845 841 cells; theory **21.792x**) | cpp | 1.58 ms | 25.29 ms | 16.01x | 73% | 2.49 |
| | **llvm** | 1.27 ms | 26.38 ms | **20.77x** | **95%** | 2.00 |

Three readings, in order of how much they should change anyone's plans.

1. **r! is essentially delivered.** 92% of the finite-n ceiling at rank 3 and 95%
   at rank 4. The residual is per-cell, not structural: the triangular nest costs
   2.00 ns/cell against the dense nest's 1.83-1.91: about 9%, which is the
   dependent trip counts and the extra loop levels' bookkeeping, and it is the
   whole gap. Note the ceiling is 5.94 rather than 6 at n = 301 — quoting "6x"
   at a benchmarkable extent is quoting the asymptote, which is why the corpus
   benchmark compares against `exactSimplexRatio` instead.
2. **The llvm lane's compact addressing is RANK-FLAT: 2.00 ns/cell at both r = 3
   and r = 4.** That is the closed form's O(1)-per-cell claim, measured rather
   than argued — each level's term is hoisted, so adding a rank adds a hoisted
   polynomial, not per-cell work.
3. **The C++ lane's compact addressing is NOT rank-flat — 1.84 → 2.49 ns/cell
   from r = 3 to r = 4 — so the llvm lane overtakes it at rank 4** (20.77x vs
   16.01x; 1.27 ms vs 1.58 ms on the compact arm). This is the first shape
   measured where the llvm lane beats the C++ lane on RUNTIME rather than
   codegen time, and the mechanism is structural: the C++ lane addresses a
   compact cell through an allocation-time Iliffe skeleton, which costs r
   pointer dereferences per cell and therefore grows with rank, while the closed
   form's cost sits in hoisted terms that do not. The rank-2 measurement that
   found the two lanes at parity (§0.3 of plan-llvm-backend.md) was reading the
   flat end of that curve.
   **AMENDED by §0b below: the overtake is emitted-vs-emitted only.** Hand-erased
   C++ twins with flat addressing win at BOTH ranks, so reading 3's mechanism
   diagnosis stands (the skeleton write path IS the deficit) but its
   backend conclusion does not survive giving the C++ lane the same addressing —
   the win is code-shape, not backend, exactly as the backend-independence
   theorem predicts.

**What this does NOT show.** The blocked (brick) schedule is still rank-2 only:
`SimplexBlocksCore` enumerates rank-r blocks already, but the prism emitter does
not exist, and rank 3+ refuses the blocked schedule by name rather than silently
running serial (which would corrupt the deterministic combine order the blocked
arm exists for). Nothing above is evidence for or against bricks at rank 3 — and
§3's own table argues the case is *weaker* there (dense-brick fraction 37.5% at
r=3/T=4 against 75% at r=2/T=4, needing T ≥ r before any dense brick exists at
all). The serial rank-r nest is what delivers the r! above, and it delivers it
without blocking.

## 0b. The home-turf control, measured 2026-08-20 — erased C++ wins both ranks; the rank-4 "llvm overtake" was an addressing artifact

§0a's reading 3 compared the two lanes' EMITTED programs. The fairness question
is what each lane can do in its IDEAL setup — so the same fixtures were re-run
against HAND-ERASED C++ twins (the `plan-static-array-erasure.md` §3b
methodology): identical storage-coordinate loops, dependent bounds, per-level
operand hoists, kernel, fill, probes and timing region, with only the
ADDRESSING changed. Two variants, both writing the flat pool directly with no
Iliffe skeleton: `cursor` (one running write cursor — the canonical nest IS
pool order, so `s[cur++]` is exact and addressing costs zero arithmetic;
single-thread ideal, rows serially dependent) and `closed` (the hockey-stick
closed form hoisted per level — the llvm lane's addressing spelled in C++;
rows stay independent). Compiled with the lane's own flags
(`-O3 -march=native -ffp-contract=fast`, g++ 15.2 and clang++ 22.1.8); probe
values identical across every arm; two clean interleaved runs, 27 samples per
arm, medians (a first run was discarded for ambient contamination — spreads
2-4x the clean runs', another session's compute burst; the clean runs
reproduce each other and the §0a absolutes).

| arm | r=3 median | ns/cell | r=4 median | ns/cell |
|---|---|---|---|---|
| **flat cursor, g++** | **7.67 ms** | **1.67** | **0.98 ms** | **1.54** |
| flat closed-form, g++ | 7.78 ms | 1.69 | 1.00 ms | 1.57 |
| flat closed-form, clang++ | 7.92 ms | 1.73 | 1.06 ms | 1.67 |
| emitted C++ (skeleton), g++ | 8.25 ms | 1.80 | 1.53 ms | 2.41 |
| emitted C++ (skeleton), clang++ | 8.36 ms | 1.82 | 1.92 ms* | 3.02* |
| llvm lane | 8.96 ms | 1.95 | 1.20 ms | 1.89 |

(*from the contaminated run only — treat as ≈ g++-emitted, not as a clang
penalty.)

Four findings, ranked by how much they should change anyone's plans.

1. **C++ on its home turf wins at both ranks.** Flat-pool addressing beats the
   llvm lane by 1.17x (r=3) and 1.20-1.30x (r=4), and beats the lane's own
   emitted skeleton code by 1.07x (r=3) and **1.53x (r=4)**. §0a reading 3 is
   AMENDED: the rank-4 overtake was real against the emitters as they stand,
   but it is an ADDRESSING artifact, fully recoverable in C++ — the
   backend-independence theorem (plan-llvm-backend.md §1-2) survives its
   sharpest test yet.
2. **The actionable emitter item: canonical compact FILLS should write the flat
   pool, not the skeleton.** `closed` ≈ `cursor` within noise (1.57 vs 1.54
   ns/cell at r=4), so the parallelizable form is free — the C++ emitter change
   should emit closed-form flat writes (keeping rows independent for OMP),
   replacing `__orow = A[i][j]`-style skeleton writes in exactly the canonical
   compact-output nests. Reads through the skeleton elsewhere are untouched.
   This is the same "flat arithmetic == skeleton, so erasure is safe" result
   plan-static-array-erasure.md measured at rank 3 on the fiber shape — but at
   rank 4 on the map shape it is no longer parity, it is 1.53x, because the
   per-loop-entry skeleton dereferences amortize over ever-shorter inner runs
   (mean inner trip ≈ n/r).
3. **The toolchain axis is neutral** (clang++ twins within 4-8% of g++ twins,
   both orderings preserved), so none of the above is a gcc-vs-clang effect.
4. **The llvm lane has its own 13-20% IR-shape headroom**: clang++ compiling
   clean C++ of the SAME algorithm (closed twin: 1.06 ms) beats the lane's
   emitted IR (1.20 ms) — so the deficit is the lane's alloca/load-store IR
   shape, not the clang backend. Worth an emission-diff pass (probe artifacts:
   session scratchpad `cpp-juice/`, `twin.cpp` with the full variant matrix in
   its header) before any deeper backend investment.

---

**Eighth measurement 2026-08-18 — packed triangular storage has no power-of-two
pathology, and the licence's payoff is contiguity-dependent.** Mirror-read
symmetric folds (`reduce` over `S(i, j) + S(j, i)` per dense cell, canonical
reads both ways) at n = 2003 (prime) vs 2016 (2⁵·3²·7) vs 2048 (2¹¹), sym and
antisym, both licences, 27 interleaved samples per arm, per-cell medians:

| shape | 2003 | 2016 | 2048 |
|---|---|---|---|
| sym serial | 1.453 ns | 1.417 | 1.471 |
| sym + reassoc | 1.643 | 1.605 | 1.662 |
| antisym serial | 1.805 | 1.841 | 1.811 |
| antisym + reassoc | 1.832 | 1.840 | 1.845 |

(1) **Flat within ~3% across prime / composite / 2¹¹.** The benchmark
discipline's power-of-two artifact (~7x on dense strides) does not exist on the
packed simplex, and the reason is structural: `rowBase(i) = i·n − i(i−1)/2`
changes its pitch every row, so no constant 2^k stride ever forms to alias
cache sets — the triangular layout is self-skewing. The non-power-of-two rule
remains in force for DENSE fixtures; compact pools are exempt by construction.
(2) **The reassoc licence is neutral-to-negative on gather-shaped reads**: sym
mirror folds are consistently ~10–13% SLOWER licensed than serial at every
size (the vectorizer's gathers lose to the scalar loop on this hardware);
antisym sits at parity (its select-heavy canonical read vectorizes to no
benefit). Contrast the seventh measurement's 3.66–5.9x licence win on the
CONTIGUOUS row-view fold: the licence's SIMD payoff follows read contiguity,
not the fold per se. No emitter change follows yet — the licence is
user-requested and correct either way — but any future auto-policy for FMF
should key on operand contiguity the way brick auto keys on RowOpBytes.

---

Written 2026-08-17. This is the spec for the "triangular quadtree tiling" follow-on the
retired perf plan left open, prompted by one observation: **the simplex-blocks
decomposition already landed for Zarr storage is also an iteration and layout
strategy for computation.** Decompose a symmetric domain into tile blocks; every
block whose tiles are all distinct is a *dense rectangular brick* with no
symmetry constraint; recurse only the on-diagonal residue. At modest depth, all
but a vanishing fraction of a symmetric computation is plain dense rectangular
work — which is exactly the shape BLAS, the LLVM vectorizer, `linalg`, and
`cuda_tile` want. This is the constructive bridge past the "non-affine ⇒
refuse" verdicts in both backend plans (plan-mlir-backend.md §5,
plan-llvm-backend.md T1.1): it does not make the simplex affine; it **covers**
the simplex with affine pieces and confines the non-affine part to the residue.

## 1. The observation, rank 2

Take the upper-triangle domain Δ₂(n) = {(i,j) : 0 ≤ i ≤ j < n} and split both
axes at the midpoint. Three cells result: two on-diagonal triangles (each a
half-size Δ₂) and one off-diagonal cell {i < m ≤ j} — which is a **full dense
square**: for i and j in disjoint ordered tiles, i < j holds automatically, so
*every* point is canonical. No canonicalization, no mask, no guard — the
constraint has been discharged by the block structure itself. Recurse the two
triangles; the squares accumulate.

Two views of the same recursion: the quadtree view (each triangle → four
congruent sub-triangles, two on-diagonal + two off-diagonal) is what the
recursive-halving *path order* enumerates; the compute view **coarsens the two
off-diagonal triangles into one square**, because rectangular backends want
boxes. Both views share leaf structure — the T = 2^k identity already verified
in the storage plan.

Residue arithmetic: with T tiles at one level, the on-diagonal triangles hold
1/T of the cells (T=4 → 75% dense, T=8 → 87.5%, T=16 → 93.75%); recursive
halving to depth d leaves 2^{-d}. At full recursion the residue is exactly the
diagonal cells i=j — lower order. For **antisym there is no diagonal at all**:
full recursion covers 100% of strict cells with dense bricks (repeated-tile
blocks at B=1 are empty — the rule the storage code already implements).

## 2. The landed math (reuse, don't re-derive)

`module SimplexBlocks` (src/providers/ZarrProvider.fs:139-240), implemented and
gated for the Zarr provider (`blade test zarr` 263/1 — the one failure is a pre-existing
NetCDF-stream typing error unrelated to this decomposition; plan:
src/providers/ZarrSimplexBlocksPlan.md; format: src/providers/ZarrTriangularSpec.md):

- **Block grid = SymIdx<r, T>** — tile multisets (t₁ ≤ … ≤ t_r) enumerate the
  blocks; C(T+r-1, r) of them; ranking/unranking reuses the combinadics
  (`rankOfCoords` :142 / `unrankToCoords` :158).
- **Each block is a product of smaller simplices** (`blockCellCount`, :184):
  group tiles by multiplicity; a tile of width w appearing m times contributes
  C(w+m-1, m) (sym) or C(w, m) (antisym — **zero** when m > w, the empty
  diagonal blocks). All-distinct blocks are dense boxes of B^r cells.
- **Intra-block iteration is branch-free** (`enumBlockCells`, :199):
  i_k ∈ [max(tile_k·B, i_{k-1}+strict), min((tile_k+1)·B, n)) — uniform bounds,
  no per-shape codegen.
- **Recursive halving = T = 2^k** with the mixed-radix DFS path order
  (`pathMultisets` :219 / `pathRows` :238) giving subtree-contiguous ranges.
- Measured padding for the *storage* row format: dominated by last-tile
  raggedness, 8.9–41.9% (prefer B | n, T ≳ 10) — relevant to §5's S1 only.

The module is provider-local today. Compute reuse lifts it to a small shared
file placed before its consumers in Blade.fsproj (or, minimally, twins the
identities at the consumer with a property-test pin against the provider's —
the differential-twin discipline either way).

## 3. Generalization: rank r, comm groups, signs

**The unit of the design is the iteration domain, not the array.** Any
comm-group-licensed triangular iteration — symmetric storage traversal,
same-array commuting positions in a kernel, symmetric-output method_for — ranges
over a simplex, and the decomposition applies uniformly. Storage participation
is optional (§5).

For rank r with multiplicity pattern λ (how many times each tile repeats), a
block is ∏ᵢ Δ_{λᵢ}(B): dense along every multiplicity-1 tile, simplex-shaped
along repeated tiles. Three consequences:

1. **Fully dense bricks** are the all-distinct blocks, C(T,r) of them; their
   cell fraction is r!·C(T,r)/T^r ≈ ∏_{k=1}^{r-1}(1 − k/T):

   | | T=4 | T=8 | T=16 | T=32 |
   |---|---|---|---|---|
   | r=2 | 75% | 87.5% | 93.8% | 96.9% |
   | r=3 | 37.5% | 65.6% | 82.0% | 90.8% |
   | r=4 | 9.4% | 41.0% | 66.6% | 82.3% |

   Note T ≥ r is required for *any* dense brick to exist.
2. **Mixed blocks are prisms** — e.g. a rank-3 block (t,t,u) is Δ₂(B) × [B]:
   dense along u's axis, triangular along the repeated pair. Recursion applies
   *per repeated factor*, so mixed blocks progressively shed their constraint
   too; the higher-r fractions above are the depth-1 floor, not the ceiling.
3. **Antisym** uses strict factors and skips empty diagonal blocks (already in
   `blockCellCount`). Sign handling: canonical cells carry +; only *mirror*
   consumption (§4b) applies a character, constant per brick-role. **Hermitian
   stays reserved** (constraint-coupled cells — same status as the storage
   spec), and **OrbIdx depth ≥ 2 is out of scope** for the same reason
   packed-blocks is undefined for orbit heads: a wreath pool has no tile
   multiset (ZarrTriangularSpec.md, version-2 rules).

## 4. Two consumption modes

**(a) Canonical-domain iteration** — each canonical cell exactly once: maps
over symmetric arrays, folds over the canonical domain, symmetric-output
kernels. Bricks *partition* the canonical set; brick order is free for maps
(independent writes to distinct cells), license-gated for folds (§6).

**(b) Full-domain semantics via mirror expansion** — contractions that consume
both (i,j) and (j,i): a dense brick (t₁ < t₂) serves both roles, once as-is and
once transposed (with the antisym character on the mirrored role). This is the
classic blocked-symmetric structure: symmetric matvec = per-brick GEMV +
GEMVᵀ into two output ranges; symmetric-output contraction (gram) = off-diagonal
output bricks are plain GEMMs, diagonal output bricks are half-size SYRKs —
recursion. The license is the same comm group that licensed triangular
iteration in the first place; which mode applies is derivable from the existing
SymcomState/decompaction machinery and must ride on the plan record (§7).

## 5. Storage options (orthogonal to iteration; decide by measurement)

- **S0 — iteration-only over the existing packed pool.** No storage change.
  Within an off-diagonal brick, packed addressing is affine along the last
  axis (row base + j), non-affine only across rows — enough for CPU SIMD on
  the inner axis. Cheapest; entirely emission-side; where P1 starts.
- **S1 — brick-major in-memory layout** = the Zarr `packed-blocks` row format
  brought in-memory, which its plan explicitly anticipated ("the store layout
  is then the in-memory layout, and chunk I/O is a straight block copy").
  Dense bricks become contiguous, alignable, *fully affine* 2-D tiles —
  BLAS-viewable, `linalg`/`cuda_tile`-consumable. Padding cost is the measured
  8.9–41.9% raggedness figure; policy: prefer B | n, T ≳ 10, refuse past a
  threshold (the storage plan's guidance, reused). This is a *deduced layout*
  in the sense of Blade's layout ownership — no surface syntax.
- **S2 — per-call brick packing** (BLAS-style pack-and-compute) for library
  routes that want contiguous operands without committing the array's layout.

## 6. What it buys, per consumer — with the honesty the backend audit demanded

| consumer | today | with bricks |
|---|---|---|
| elementwise maps over sym arrays | flat-pool traversal, already optimal (2.0x banked, zero coordinate math) | **nothing — do not touch this path** |
| coordinate-bearing sym kernels + licensed folds | triangular nest, `schedule(dynamic)` (CodeGenLoopNest.fs:584), non-uniform bounds | uniform literal-B trip counts (the 1.77x axis, per brick), static scheduling of equal-size work units, cache blocking, vectorized inner axis |
| symmetric contractions | top-level syrk routing (LinAlgPatterns; packed pool proven == BLAS packed) | blocked GEMV/GEMM/SYRK structure at tile granularity; GPU-fit |
| MLIR / cuda_tile lanes | **whole-kernel refusal** (plan-mlir-backend.md §5: non-affine) | bricks are static-shape dense ops; refusal shrinks to the 2^{-d} residue, which stays on the host path — the standard refusal-with-fallback pattern, now per-residue instead of per-kernel |
| LLVM lane | T1.1 "backend-negative: non-affine defeats SCEV/vectorizer" (plan-llvm-backend.md) | bricks are affine; the verdict flips to backend-positive-except-residue |
| layout algebra (plan-mlir-backend.md §6) | SymIdx packing outside CuTe's multilinear algebra | brick-major is a *tree of affine tiles* — expressible; only the residue stays outside |
| parallel scheduling | `schedule(dynamic)` derived from triangular imbalance | uniform bricks → static schedule; FoldChunkPlan's outermost-rectangular-only restriction (IRStorage.fs:79-94) generalizes to triangular domains |
| MPI / out-of-core | landed: block-scoped Zarr reads, BSP ownership, window reads | **deliberately a DIFFERENT decomposition — do not unify.** Rank load balancing requires equal-size ownership units, which is exactly what the *triangular quadtree* (congruent-triangles path order, `pathMultisets`) provides and what the Zarr/MPI side therefore keeps. The compute-side blocked-simplex coarsens off-diagonal triangle pairs into dense bricks precisely because backends want boxes — producing *unequal* unit shapes (bricks + residue) that are fine for a shared-memory work queue or static brick schedule but wrong as per-rank ownership units. The two schemes share the leaf structure and the SimplexBlocks identities (agreement property-pins, §8), nothing more |

The interpreter is untouched throughout: blocking is an emission strategy (a
CodeGen-phase-local plan record, like `LoopNestCodeGen`/`FoldChunkPlan`), maps
produce identical results, and licensed folds follow the standing
licensed-path-is-not-the-tested-path policy.

## 7. Licensing and numeric policy

- **Maps**: distinct-cell writes; brick order free; no license needed.
- **Folds**: brick partials reorder the fold ⇒ requires `foldReorderLicensed`
  (the BL4016 family), exactly like OMP chunking. Deterministic form: fixed
  ascending-lex brick order, partials combined in block order — deterministic-
  but-reassociated, the K-lane philosophy at brick granularity. Unlicensed
  folds keep the serial canonical order (no blocking); the default build is
  unchanged. Licensed-path testing uses integer-valued f64 corpus + invariant
  checks, never a float tolerance in InterpDiff (plan-llvm-backend.md §6).
- **Mirror expansion** (§4b) is licensed by the kernel's comm groups; antisym
  mirrored roles carry the character. No new license kind is needed anywhere.

## 8. Compiler seams

1. **Lift `SimplexBlocks`** to a shared module (placement before its consumers;
   `EnableDefaultItems=false` — manual `<Compile>` entry), keeping the provider
   consuming the same code; extend with the per-brick facts compute needs:
   brick-role list for mirror mode, per-brick sign/character, dense-axis mask
   (which λ positions are multiplicity-1).
2. **`BrickPlan`** — a new plan record beside `FoldChunkPlan` on the loop-plan
   layer: block enumeration (closed-form, or materialized tile list for small
   T), consumption mode (§4), per-brick bounds (the `enumBlockCells` identity),
   combine order for folds, leaf policy. Leaf simplex blocks run **serial
   triangular** (they are small by construction; dense-with-mask wastes half of
   a tiny block and creates write hazards for canonical outputs).
3. **B/depth policy is derived, not user-tuned** ("the fastest way is the only
   way"): B a multiple of the vector width, brick working set targeted at a
   cache-level fraction, B | n preferred, T ≥ r required, refuse-to-block below
   profitability thresholds (the r=4/T=4 row of §3's table is a non-starter).
   An env override exists for measurement only, read per-call as a function.
4. **Backend handoff**: the same `BrickPlan` serializes as dense sub-nests
   (C++/LLVM lanes), `linalg` ops on brick views (MLIR lane, S1), or tile
   launches — the `BackendFacts.fs` pattern from the LLVM plan applies.

## 9. Phasing

| phase | deliverable | gate |
|---|---|---|
*Outcomes as of 2026-08-18 are in §0.* P0 **landed** (`src/SimplexBlocksCore.fs`, 16
agreement pins); P1 **landed for rank 2 on the LLVM lane and its gate DID NOT PASS**
(bricked 1.07x slower at n = 6007, no win anywhere); P2–P5 not started.

| **P0** | lift SimplexBlocks + extend with mirror/sign facts; property pins vs the provider module (Σ blockCells = C(n+r-1,r); partition exactness; mode derivation) | pure math, hermetic tests |
| **P1** | S0 iteration blocking for comm-licensed rank-2 folds and coordinate-bearing maps, behind a gate | **the measurement gate**: comoment/covariance flagship shapes plus one large-n case, non-power-of-two extents, 3 rounds/9 reps/medians, arms = current triangular `schedule(dynamic)` vs bricked-static; go/no-go = parity-or-better with ≥1 clear win. The 2.14x packed precedent is the standing warning — the mechanism differs here (bricks pay **zero** per-cell canonicalization, which is what the 2.14x paid), but that is a hypothesis until this gate runs |
| **P2** | contraction bricks via LinAlgPatterns: symmetric matvec GEMV pairs; gram/syrk brick recursion (leverages packed==BLAS-packed) | measured vs the existing top-level syrk route |
| **P3** | S1 brick-major deduced layout; unify with Zarr packed-blocks so provider I/O becomes straight block copies; alignment + padding policy | padding within threshold on flagship shapes; no elementwise regression |
| **P4** | backend consumption: MLIR/`cuda_tile`/LLVM lanes take bricks as dense ops with residue-refusal; revise plan-mlir-backend.md §5 and plan-llvm-backend.md T1.1 verdicts | a symmetric kernel runs on a rectangular backend end-to-end, residue on host, diff-gates green |
| **P5** | rank ≥ 3 prisms (per-factor recursion), adaptive depth (the storage plan's Phase 4 twin), MPI compute ownership aligned with the landed read ownership | demand-driven |

## 10. Risks

1. **Measure-first** (the 2.14x precedent). P1's gate is designed to kill the
   plan cheaply if brick overhead (block-loop bookkeeping, worse locality on
   the packed pool in S0) eats the uniformity win.
2. **Do not touch the flat elementwise path** — it is already optimal and the
   decomposition has nothing to offer it.
3. **Fold-order licensing** — blocking folds without `foldReorderLicensed`
   would silently reassociate; the gate structure in §7 is load-bearing.
4. **Small-n / high-r profitability** — §3's table; refuse-to-block is a
   feature, and the leaf-serial policy bounds the loss at exactly today's
   behavior.
5. **Padding (S1 only)** — measured up to 41.9% at bad tile choices; policy
   inherited from the storage plan.
6. **Scope discipline** — Hermitian and OrbIdx excluded (same as storage);
   ragged/sparse/compound never had simplex domains and are unaffected.
