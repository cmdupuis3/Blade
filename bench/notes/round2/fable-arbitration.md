# Round-2 arbitration (third reviewer: Fable), master 4136f0b5

Read-only on the repository. Evidence lines are current source at 4136f0b5.

## Question 1 -- unseeded `(+)` legs in item C: `-0.0` seed vs peeled first element

**RULING: `-0.0` (and `1.0` / int `0` / int `1`) seeding is ADMISSIBLE in v1, with the documented exclusion below; the peeled first-element start is NOT required. Signalling-NaN state is outside Blade's arithmetic/identity contract, and no Blade surface can produce one.**

### (a) Does the contract treat signalling-NaN state as observable? No.

1. The contract itself. `docs/formalism.md` §2.4 "Arithmetic semantics -- one contract, every
   lane" (lines ~143-170) defines identity as "compute the same value, or fail with the same
   code"; its table has no row for NaN payload or signalling state. `bench/notes/phase2-microkernel-plan.md:191-192`
   says it outright: "NaN payloads are outside the contract (x86 propagates the FIRST operand's
   payload; the printers print 'nan' with no payload)". A signalling NaN is a payload bit
   (quiet bit clear): it is covered by that clause, not an exception to it.
2. Observation channels. Every printer collapses every NaN: `src/Interp/CppFormat.fs:23-25`
   ("every NaN prints 'nan' -- ... the sign bit and the mantissa payload are irrelevant"),
   `:98`, `:154`; `src/cpp/blade_llvm_shim.c:431`; the C++ lane's ucrt iostreams likewise. The
   differential gates compare NORMALIZED STDOUT only (`tests/InterpDiff.fs:84`, `:309-310`;
   `tests/OptDiff.fs:113` reuses that normalizer). `tests/Expect.fs:647` treats NaN == NaN.
   Hence no gate, pin or printed program can distinguish 0x7ff0000000000001 from
   0x7ff8000000000001 -- or from 0xfff8000000000000.
3. The provider boundary refuses NaN in BOTH directions: `src/cpp/blade_runtime.hpp:421-476`
   (`refuse_missing`, BL8015: a NaN/Inf cell "refuses the read instead of reaching the program
   as data") and `:478-491` (`refuse_nonfinite_write`, BL8015). So an sNaN cannot enter from a
   store and bits cannot leave to one.
4. Reachability. x86 arithmetic on finite/inf operands yields only the default quiet NaN
   (0xfff8...0); `0.0 / 0.0`, `sqrt(-1.0)` etc. are quiet. There is no bit-cast / `from_bits`
   intrinsic in the surface (grep of `src/*.fs` for `to_bits|from_bits|bitcast|reinterpret`:
   nothing in the type checker or parser; the only `Int64BitsToDouble` uses are the interpreter's
   own constants, `src/Interp/Numerics.fs:638-640`, a qNaN). A signalling NaN is therefore
   UNREACHABLE by any Blade program today. Codex's counterexample is arithmetically correct
   (confirmed: `codex-AC-work/seed-contract-off.log` line 3, and my own probe
   `scratchpad/fable-probe/snan.cpp`: `volatile -0.0 + sNaN` -> 7ff8000000000001) and lies
   outside the value domain the language can present to the fold.
5. The toolchain could not honour such a promise anyway. g++ runs with `-fno-signaling-nans`
   (default), under which GCC's own `fold_real_zero_addition_p` folds `x + (-0.0)` to `x` and
   its constant folder does NOT quiet an sNaN: my probe's inlined `seeded(a, 1)` constant-folded
   to 7ff0000000000001 while the run-time `vaddsd` quiets it. The C++ lane's own compiler
   already treats sNaN state as unobservable; LLVM's IR semantics do the same (`fadd X, -0.0`
   simplifies to `X`). A Blade promise about sNaN state would be unkeepable in two of three lanes.

### (b) Admissibility and the preamble's "strict chain" clause

Preamble constraint 2 says every fold chain keeps its exact seed and operand order. An
unseeded fold HAS no seed: its chain is `a0 + a1 + ... + a(n-1)` (`src/Interp/ArrayOps.fs:853-860`
copies `leaves.[0]` then folds k = 1..n-1; `src/CodeGenBinding.fs:3241-3245` "without init,
seed = arr[0], fold from 1"; `src/EmitLlvm.fs:5826-5846` same, start = 1). Prepending
`-0.0 +` adds one operation that is the IEEE identity in round-to-nearest for EVERY value the
language can produce: finite x (including subnormals), +-inf, +0.0 (+0 + -0 = +0), -0.0
(-0 + -0 = -0), every quiet NaN (payload and sign propagate from the NaN operand, as Codex's
own log line 4 shows for 7ff8...01). The result chain is bit-identical to the unseeded one in
every lane, under both contraction modes (no product is involved; and even a contracted
`fma(a, b, -0.0)` rounds once to the same value as `a * b`). "Bitwise" in this repo means
bitwise against a named reference over the contract's value domain
(`phase2-microkernel-plan.md:176-178`); that is satisfied. The same reasoning covers `1.0 *`
for `(*)` and the exact integer identities.

Conditions of admissibility (all already in the C brief §2.3; they are now REQUIRED, not
optional):

- Only builtin `(+)` -> `-0.0` / `(*)` -> `1.0` over Float64/Float32; Int32/Int64 `(+)` -> `0`,
  `(*)` -> `1`. Any other unseeded kernel, complex, or unit-carrying element: DECLINE.
- The seed is emitted as the IR node `IRUnaryOp(Neg, IRLit 0.0)` (what surface `-0.0` lowers
  to), never a float-literal printer's rendering of negative zero.
- Only when the leg's extent is a LITERAL >= 1 (the brief's totality rule): the empty-array
  BL8003 of the original must never be silently replaced by a seed. Runtime or zero extents
  decline.
- The surface join's own `+0.0` implicit seed (`src/TypeCheckInfer.fs:3071-3078`) is a
  user-visible rule for a join the USER wrote; it is untouched by this item. The rewrite of an
  unseeded leg must use `-0.0` precisely so that a rewritten program keeps the standalone
  fold's value (`research/C/negzero.blade`: unseeded `-0.0`, surface join `0.0`).

Peeled first-element start: not required for v1. It would buy the preservation of a state the
contract excludes and the language cannot reach, at the price of an emitter change in files
another wave-2 item owns. It MAY be revisited if Blade ever gains a bit-level float
constructor or a provider path that admits NaN; at that point the formalism clause below is
the thing to re-decide, and the row in CLAUDE.md names it.

### (c) Required wording and tests

**docs/formalism.md §2.4**, one paragraph after the arithmetic table (canonical; land it in the
same change as the C item -- the phase-2 bench note is not the contract's home):

> NaN payloads and signalling state are outside the contract. Every lane prints every NaN as
> `nan`; the provider boundary refuses a NaN in both directions (BL8015); no Blade operation or
> intrinsic distinguishes a signalling NaN from a quiet one, and none can construct one. A
> compiler rewrite may therefore insert an identity operation on a fold's chain (`-0.0 + x`,
> `1.0 * x`), which is bit-exact for every finite, infinite, signed-zero and quiet-NaN value
> and would only quiet a signalling NaN.

**CLAUDE.md env-table row for the C knob** (whatever name C registers in `optimizerGates`),
appended sentence:

> An UNSEEDED builtin `(+)` / `(*)` leg joins seeded with `-0.0` / `1.0` (`0` / `1` for
> integers) -- the exact identity under round-to-nearest, so the leg's bits are unchanged
> (`[-0.0, -0.0]` still sums to `-0.0`; a seeded surface join sums it to `+0.0`); signalling-NaN
> state is not preserved and is outside the arithmetic contract (formalism §2.4). Only legs
> with a literal extent >= 1 are rewritten (BL8003 on an empty unseeded fold is kept).

**`blade plan` record** (per rewritten unseeded leg, inside the item's applied line):
`leg <j>: unseeded (+) -> seed -0.0 (identity; bit-exact)` / `... (*) -> seed 1.0 ...`;
a declined kernel: `declined -- leg <j> is unseeded with a non-builtin kernel (no identity)`.

**Tests** (one corpus file in the C item's category; the pins below are QUOTED string pins,
which compare the printed text byte for byte -- `tests/Expect.fs:456-459` `ExpectedString`,
precedent `tests/corpus/basic/082_float_print_spelling.blade:32` `negzero = "-0.0"` and `:45`
`inf = "inf"`. A numeric `// EXPECT: s = -0.0` does NOT pin the sign: `floatEquals`,
`Expect.fs:647-653`, accepts either zero.) Each fold must sit beside a sibling fold over the
same array so the rewrite FIRES (check the plan line in the `blade test` block that owns the
item's emission needles):

```
let z: Array<Float64 like R> = [-0.0, -0.0, -0.0]
let s_neg = reduce(z, (+))            // EXPECT: s_neg = "-0.0"
let inv_neg = 1.0 / s_neg             // EXPECT: inv_neg = "-inf"
let p_neg = reduce([-0.0, 2.0, 1.0], (*))   // (*) leg: 1.0 seed      // EXPECT: p_neg = "-0.0"
let s_mixed = reduce([0.0, -0.0, -0.0], (+)) // +0 + -0 = +0 unchanged  // EXPECT: s_mixed = "0.0"
let s_inf = reduce([-1.0 / 0.0, 1.0], (+))  // float / 0.0 is IEEE (only Int panics)  // EXPECT: s_inf = "-inf"
let s_nan = reduce([0.0 / 0.0, 1.0], (+))   // qNaN propagates          // EXPECT: s_nan = "nan"
let q = reduce(z * z, (+))                  // the sibling that forms the group
```

plus an Int64 pair (`reduce(iz, (+))` / `reduce(iz, (*))` with a wrapping-irrelevant seed), and
a `(rejects)`-free decline case: an unseeded `lambda(a, b) -> a - b` leg beside a `(+)` leg
must keep its value (`((1 - 2) - 3) - 4 = -8`) and the plan must say `declined -- ... no
identity`. The file must pass `blade test interp <dir>` and `blade test opt-diff <dir>`
(off = unseeded chain, on = `-0.0` chain: byte-identical output is the proof the identity
holds in both lanes). Also promote `research/C/negzero.blade` to the corpus as written
(`unseeded = "-0.0"`, `seeded_pos = "0.0"`, `j_neg = "-0.0"`, `j_surface = "0.0"`): it pins the
standalone-vs-surface-join divergence the rewrite must not widen.

Recommendation, not a requirement of this ruling: the surface join's implicit `(+)` seed
(`TypeCheckInfer.fs:3076` `LitFloat 0.0`) could itself become `-0.0` so a hand-written join
leg and the standalone fold agree on all-negative-zero input. Separate item; it changes
user-visible output of existing programs (any pin reading `0.0` there would read `-0.0`).

## Question 2 -- G7 thread policy for `<&!>` joins

**RULING: CONFIRMED. G7 threads only on an explicit `where omp` REQUEST (on the join's fold kernel or a leg's fold kernel) AND a per-leg reorder LICENCE; auto-threading builtin `(+)` joins would be a language-policy change, and `onepass.blade` adopting `where omp` on its fold kernel is the symmetric spelling.**

Evidence that request and licence are separate axes in the repo today, and that threads hang
on the request:

- `src/CodeGenBinding.fs:3087-3090`: `parallelFold` requires `c.IsOmpParallel && foldReorderLicensed c && fpReassocEnabled ()`; the block comment at `:3070-3071`: "opt-in; the LICENCE (declared comm, or a builtin body) is what makes the reorder sound".
- `src/CodeGenExprSupport.fs:1634-1648` `foldReorderLicensed`: "Answers only the LICENCE question -- whether omp was requested is separate (callable.IsOmpParallel), so the two can be reported independently."
- `src/CodeGenExpr.fs:1468`: the census marker is "Triggered by the REQUEST (`IsOmpParallel`), not by the licence".
- The deferred-fold Path B gate (`CodeGenBinding.fs:3892-3905`, cited by Codex DE #11) takes a fold request OR a producer-map request COVERING THE OUTER LEVEL, AND the fold's own licence -- so even today a map request selects the traversal only, never the licence.
- CLAUDE.md: `omp(x: n)` is the "parallelism license"; "A bare `omp` on a `reduce` kernel licenses fold reordering ... and is emitted vectorized/laned by default (`where omp` implies simd, even under `BLADE_OMP_THREADS=1`)"; the `BLADE_FOLD_LANES` row: "Under a `where omp` fold the same tree runs INSIDE each Path B chunk". The 2026-10-17 policy (`docs/plans/plan-fp-default-fastest.md`, FOLD ASSOCIATION POLICY) made the licensed fold FORM (lanes, `omp simd reduction`) the default; it did not make thread TEAMS automatic -- a plain `reduce(a, (+))` prints `[fold form: K-lanes(8); reduce in expression position]` with no team (dot's plan, Codex DE #11), and OMP is still an opt-in test block.
- `bench/notes/gap-register.md:56` (G7) names the spelling itself: "`omp` on the join's fold kernel -> DROPPED SILENTLY"; the fix target is to honour that request, not to invent one.

Consequences I hold the plan to:

1. Rule, exactly: thread the shared traversal iff (i) `IsOmpParallel` on the join's common fold kernel or on at least one leg's fold kernel (a leaf-map request counts only under the existing Path B rule, covering the outer level), AND (ii) EVERY leg independently satisfies `foldReorderLicensed` and no leg is repro-vetoed, AND (iii) the shape/purity checks. Facts are never OR-ed across legs (preamble constraint 2). A join with one threaded-eligible leg and one unlicensed leg stays serial with the canonical `[omp] requested but emitted serial` census line, never a silent drop (`omp census marker invariant`).
2. Item C must not manufacture a request: a join C synthesizes carries each source leg's own `IsOmpParallel`, and C's own rule ("decline iff the build would thread some leg", C §2.3) keeps C from ever changing execution policy. Confirmed consistent.
3. `onepass.blade`: `reduce((l <@> ...) <&!> ..., lambda(a, b) where omp -> a + b)` (or the same clause in whatever spelling the fold-kernel slot accepts). Bench fairness holds: the expert's `!$omp parallel do reduction(+:...)` is an explicit request too. Runner check (measurement, not policy): the SERIAL and REPRO arms' `fold form:` lines for onepass must be unchanged by the added clause (`where omp` already implies the simd form the join takes today, and REPRO serializes), so only the mt8 cell moves. The `BL4016` commutativity check must accept `a + b` as a builtin body.
4. A future "auto-thread every licensed builtin fold" decision is the USER's language-policy call (it would change single folds and joins uniformly, and its default would need a knob and a CLAUDE.md row); this round does not take it.

(Further questions after the B review: append below under new headings.)
