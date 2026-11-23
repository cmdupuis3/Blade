# CLAUDE.md

Guidance for AI coding agents working in the Blade repository.

## What Blade is

Blade is an array-functional programming language: ML-style syntax over a numpy/R-flavored
math surface. The compiler is F# (`src/*.fs`, one exe project `Blade.fsproj`) emitting C++17
(every compile of generated code passes `-std=c++17`), compiled with g++ and run. A
tree-walking interpreter (`src/Interp/`) mirrors codegen for differential testing and the REPL;
a direct LLVM back end (`src/EmitLlvm.fs`, opt-in) is the third lane.

Three concepts carry the language ("structure-first" programming, `docs/quickstart-1.md`):

- **Loop reification** — for-loops are first-class values (`method_for`, `object_for`) you
  store, compose, join, and apply later.
- **Dimensional currying** — an `Array<T like I, J>` *is* the function `I → J → T`;
  `A(i, j) ≡ A(i)(j)`, and partial indexing yields a lower-rank view.
- **Arity polymorphism** — one function typechecks and compiles differently depending on how
  many array arguments it receives, deducing output rank and symmetry.

The design invariant is **"the fastest way is the only way"**: iteration order, storage
layout, parallelism, and BLAS routing are derived from types and kernel annotations, never
hand-authored. Declaring structure (`where comm(...)`, `where omp(...)`, index types) *is*
the optimization interface.

## Ask the tools before reading source

- **The `blade` MCP server**, when it is connected, answers language questions faster and more
  reliably than this file: `blade_check` (typecheck a file or snippet: diagnostics, binding
  types, deduced symmetry), `blade_explain` (any `BLxxxx` code), `blade_corpus_find` ("how do I
  write X": the construct and real corpus files that show it — use it before writing anything
  loop-shaped), `blade_eval` (run source in a persistent session), `blade_symbols`,
  `blade_doctor` (toolchain health). They typecheck, lower and interpret; backend-only refusals
  (BL7xxx) show up only with `blade run` / `blade emit`.
- **`blade plan prog.blade`** lists every optimization decision for a program (rule, subject,
  applied or declined with the first failed condition, evidence). Read it before concluding
  that a pass did or did not fire.
- **`docs/reference/knobs.md`** is the generated index of every environment knob;
  `docs/reference/knob-notes.md` holds the long per-knob notes (admission rules, measurements).
- **`docs/formalism.md`** is the canonical semantics; this file states a rule only where an
  agent gets it wrong often.

## Repo layout

```
Blade.fsproj / Blade.sln   root exe project: compiler + test harness in one binary.
                           EnableDefaultItems=false — new .fs files MUST be added to
                           Blade.fsproj by hand, in dependency order. fsproj compile order is
                           NOT phase order: read the fsproj's group comments before choosing
                           where to insert a <Compile> entry.
src/*.fs                   compiler pipeline. Runtime phases: Lexer/Parser → TypeCheck/
                           Unify/Deduce → Lowering → IR passes (IRMono, Optimize*) →
                           EmitCpp/CodeGen. The big phases are FILE GROUPS in dependency order:
                           Parser* (grammar in ParserGrammar.fs), Grad* (AD pipeline), IR* (hot
                           surface in IR.fs), TypeCheck* (the inference chain in
                           TypeCheckInfer.fs; Blade.TypeCheck = the driver), Optimize*,
                           CodeGen* (chains in CodeGenExpr.fs / CodeGenBinding.fs;
                           Blade.CodeGen = assembly + re-exported surface). See "Recursive
                           chains" below for how code may leave a chain.
src/Knobs.fs               the registry of every environment knob (see "Environment knobs").
src/cpp/                   C++ runtime headers, deployed beside every generated .cpp.
src/Interp/                IR interpreter (used by `test interp`, `repl`, diff gates).
src/{ml,ppl,math,rand,display,spectra,sgs}/compiler/
                           domain elaborators compiled INTO Blade.exe.
oracles/{math,ml,ppl,sgs,spectra}/
                           5 SEPARATE oracle .fsproj — standalone reference programs whose
                           dump verbs generate corpus EXPECT pins; not part of Blade.exe.
                           Run: dotnet run --project oracles/<domain>.
tests/                     harness compiled into Blade.fsproj (no xunit). Corpus tests live
                           in tests/corpus/<category>/*.blade, one test per file.
stdlib/                    Blade-source stdlib (units/SI.blade, stats.blade, plot.blade), beside
                           the binary; BLADE_STDLIB adds a search root. Read at RUN time: in a
                           CHECKOUT the resolver prefers this directory over the deployed copy,
                           so edits need no rebuild (`blade doctor` names the root that answered).
examples/                  10 numbered worked programs, lsdft.blade / lswosa.blade and several
                           .bladenb notebooks — the best source of idiomatic Blade; physics/ is
                           a second, self-contained corpus with its own README.
docs/                      formalism.md (canonical semantics), features.md (feature census),
                           quickstart-1/2.md (tutorial), proofs.md (Coq correspondence),
                           reference/ (knobs), plans/*.md (design docs; plans/README.md
                           indexes status).
proofs/                    the Coq/Rocq formalization itself.
protocol/                  the editor / MCP protocol package (JavaScript).
bench/                     the Fortran comparison suite; bench/results/stats.csv is THE stats
                           table; bench/notes/gap-register.md is the gap ledger (its lines are
                           thousands of characters long: never grep it with a bounded
                           repetition, cut lines instead).
generated_cpp_tests/       gitignored scratch dir the harness writes .cpp/.exe into.
                           It is CWD-RELATIVE: never run two `blade test` processes from the
                           same working directory.
legacy/                    pre-rename Blade-DSL artifacts. Do not imitate.
```

## Build and CLI

```bash
dotnet build Blade.fsproj -c Release
```

The build must stay at 0 warnings. The binary is `bin/Release/net10.0/Blade.exe` (below,
`blade` = that exe or `dotnet run --project Blade.fsproj -c Release --`):

```bash
blade check prog.blade        # typecheck + lower (no C++)
blade emit prog.blade         # emit C++ without compiling
blade compile prog.blade      # produce an executable
blade run prog.blade          # compile and run (--verbose, --mpi N, --memcheck, --run-record out.json,
                              #                  --print a,b -- print only these top-level bindings,
                              #                  --print-form test|display -- default display, see below)
blade plan prog.blade         # optimization decisions + the input manifest (--json)
blade doctor                  # native-toolchain health (g++/OpenMP, BLAS, NetCDF, MPI, CUDA)
blade test                    # full default suite
blade test docs               # every `blade` code block in CLAUDE.md + docs/ (tests/DocTests.fs)
blade test smoke              # a fixed ~370-program corpus sample, every directory (the CI push lane)
```

Bare `blade` (no verb) prints the usage, which lists every `blade test` key. `blade run` /
`compile` build in a private `%TEMP%\blade-build\...` directory and run the program with the
SOURCE's directory as its working directory. `--verbose` keeps the build directory. A g++
rejection of generated code is an internal error, BL9002.

`blade run` / `compile` / `emit` / `plan` print the DISPLAY form by default (formalism 3.9:
shortest round-trip floats, units after the value, bare expressions unlabelled, sum types
printed); `--print-form test` (or `BLADE_PRINT_FORM=test`) gives the TEST form, the spelling
every `// EXPECT:` pin is written in — write pins from `--print-form test` output. `blade test`,
the REPL, notebooks and the IDE always use the test form. Anything that parses a program's
printed output passes `--print-form test`.

`--strict-pins` (on `check`/`emit`/`compile`/`run`) promotes BL4010 pin suggestions to errors.

Requirements: .NET 10 SDK (F# 10); MSYS2 **ucrt64** g++ on PATH for anything that compiles C++
(`C:\msys64\ucrt64\bin`, first on PATH). `blade test cuda` additionally needs a vcvars64
environment (cl.exe + nvcc) with ucrt64 layered on top.

## Testing

- `blade test` runs the default suite. OMP/CUDA/MPI/timing/interp/diff-oracle are **excluded by
  default** — opt in with `blade test --omp --cuda --mpi --timing --interp --diff-oracle`. The
  NetCDF/Zarr/CSV/hybrid provider blocks are reachable **only standalone**
  (`blade test netcdf|zarr|csv|hybrid`).
- **Read the totals, not the exit code.** Missing toolchain pieces make tests **skip, not fail**,
  and skips do not affect the exit code: check the TOTAL line and its `, N skipped` suffix before
  trusting a green run.
- `blade test smoke` is the corpus slice CI runs on every push and pull request. The nightly CI
  lanes add `--omp --mpi`, `--llvm-backend`, `test interp`, `test opt-diff` and the providers.
- Two category namespaces: `blade test <key>` uses alias keys from `src/CliSelfTests.fs`
  (multiword categories accept both spellings, `index-types` / `indextypes`);
  `blade test interp <dir>` / `diff-oracle <dir>` / `opt-diff <dir>` / `opt-diff-licensed <dir>` /
  `llvm <dir>` take the **literal** `tests/corpus/<dir>` name.
- **The differentials, and what each can see:**
  - `test interp` (interpreter vs compiled, every corpus directory) pins the reproducible build
    (`BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`) and compares bytes. The optimizer runs BEFORE the
    interpreter/codegen split, so `test interp` cannot see an optimizer miscompile.
  - `test opt-diff` emits every program with all optimizer gates OFF and ON and runs the pairs
    whose C++ differs, under the reproducible build. Run it after touching any pass; a new
    pass's gate goes in `optimizerGates`. `test opt-diff-licensed` does the same under the
    default FP policy with a 1e-9 token rule.
  - `test llvm` compares the LLVM lane with the C++ lane.
  - `blade test --interp` is NOT `test interp`: it is the whole default suite with the
    differential appended.
- FOLD ASSOCIATION POLICY: the default suite runs the FAST default (licensed folds reassociated,
  contraction on) and its `// EXPECT:` pins hold at 1e-9 relative, so pins read
  association-invariant values; a test ABOUT a value under cancellation uses integer-valued or
  unlicensed folds, never a loosened pin.
- The corpus is DISCOVERED, not listed: every single-file directory runs in the default suite,
  answers to `blade test <its-literal-dir-name>`, and is walked by `test interp`. The
  wholly-negative directories are ONE list, `Corpus.rejectOnlyCategories`. The default suite's
  "Corpus Wiring" block fails if a hand-written fact stops matching the disk; its "Knobs Sync"
  block fails if an environment knob is not registered.
- `blade test` clears every emission-affecting knob for its duration and pins a few
  (`suiteClearedKnobs` / `suitePinnedKnobs` in `src/CliSelfTests.fs`: `BLADE_BLAS=off`,
  `BLADE_BLAS_PREFER=native`, `BLADE_POOL_RETAIN=default`, the test print form), prints one line
  naming any it found set, and restores them after; blocks that exercise a knob set it themselves.
- Pin hygiene is enforced: a value test with no `// EXPECT:` or `// WARN:` pin FAILS unless it
  carries `// NOPINS: <reason>`; an `// ERROR:` whose code or span does not parse fails; an
  `=`-bearing `// EXPECT:` on a `(rejects)` probe fails; any program output containing a raw heap
  pointer fails.
- `blade test docs [<page>]` checks every `blade` code fence in CLAUDE.md and docs/: a plain
  block must lower, one with `// EXPECT:` lines is also run and its pins checked. Fence flags
  after `blade` — `sketch` / `rejects` / `prelude` / `cont` / `check` — are specified in
  `tests/DocTests.fs`. A doc example that cannot compile is a sketch or a bug.
- To iterate on a single corpus test, `blade run tests/corpus/<cat>/<file>.blade` (fast, but
  pins are only validated by the harness, not by `run`).
- Full-suite runs from concurrent sessions must use private working directories (the scratch dir
  is cwd-relative) and should not overlap in time. Every run ends with a `Timings` table
  (reporting only).

### Corpus test conventions

One `.blade` file per test; pins are comments (grammar documented in `tests/corpus/README.md`):

- `// TEST: <name>` — required first line; a `(rejects)` suffix is semantically load-bearing.
- `// EXPECT: <var> = <value>` — pins a printed value. Numbers are spelled as their type prints:
  `2.0` for a float, `2` for an int (the harness checks the spelling class as well as the value).
- `// ERROR: BLxxxx [@ l:c[-l:c]]`, `// ERROR-CONTAINS: <substring>`, `// ABORT: <substring>`,
  `// REJECT-AT: lower|codegen`.
- `// WARN: BLxxxx` and `// WARN-CODEGEN: <substring>` are **strict in both directions**: an
  unpinned warning fails the test AND a pin that never fires fails the test. If you fix a
  warning false-positive, remove its now-dead pins in the same change.
- `// MODULE: <name>` for multi-file tests (pins union across member files).
- `// NOPINS: <reason>` — required on a value test that pins nothing; a stale marker fails.

`.editorconfig` exempts `tests/corpus/**` and `examples/**` from trailing-whitespace and
final-newline fixing — these are byte-pinned assets; never auto-reformat them.

## Environment knobs

Every environment variable the compiler, the compiled programs and the harness read is one
entry of the registry `src/Knobs.fs` (name, kind, default, accepted values, what `blade test`
does with it). `docs/reference/knobs.md` is GENERATED from it; `docs/reference/knob-notes.md`
holds the hand-written notes per knob — admission rules, what was measured, which tests pin it.
Read the notes row before changing or relying on an optimizer gate.

Rules:

- Gates are **functions read per call, never cached values**, so a harness can pin and restore
  one mid-process. Do not "optimize" a gate into a module-level `let`.
- A new knob: an entry in `src/Knobs.fs` (and in `all`), then `blade test knobs --write`
  regenerates the index; give it a row in the notes when it carries an admission rule. A knob
  that changes the EMISSION is keyed by the exe cache for free and belongs in
  `suiteClearedKnobs`; an optimizer pass's on/off switch belongs in `Optimize.optimizerGates`
  (a non-default arm in `optimizerGateMirrors`), so `test opt-diff` compares it.
- A disabled or declined plan still records its decision in `blade plan` (`declined -- ...`).

The ones you meet daily:

| Variable | Effect |
|---|---|
| `OPENBLAS_DIR` / `BLADE_BLAS` | BLAS/LAPACK install root; its presence enables the BLAS route unless `BLADE_BLAS=0`. `blade test` pins the route off |
| `BLADE_OMP_THREADS` | `1`/`0`/`off` suppresses OpenMP pragma emission; the runtime thread count is plain `OMP_NUM_THREADS` |
| `BLADE_FP_REPRO` | `1`: every fold keeps the ascending serial association the interpreter evaluates. Unset: licensed folds take their fastest form |
| `BLADE_FP_CONTRACT` | g++ `-ffp-contract=` (default `fast`); `off` with `BLADE_FP_REPRO=1` is the byte-reproducible build the differentials pin |
| `BLADE_MARCH` | g++ `-march=` (default `native`) |
| `BLADE_PRINT` / `--print a,b` | which top-level bindings print; an imported module's binding is named qualified (`Lib.y`) |
| `BLADE_PRINT_FORM` | `test` or `display` spelling of printed values (see "Build and CLI") |
| `BLADE_PRINT_BITS` | run-time test instrument: appends each printed float's raw bits |
| `BLADE_LLVM` | `1`: `run`/`compile`/`emit` try the LLVM back end first (falls back to C++ when it declines) |
| `BLADE_MEMCHECK` | ASan-instrumented Debug build (`--memcheck`) |
| `BLADE_EXE_CACHE` / `BLADE_PCH` | the content-addressed executable cache and the precompiled prelude; `0` disables |
| `BLADE_STDLIB` | extra stdlib search root |
| `BLADE_TOOLCHAIN_FILE` | the `blade.toolchain.json` consulted (written by `blade setup`); its keys mirror env vars |
| `BLADE_RUN_RECORD` / `--run-record` | path of the JSON run record the compiled program writes at exit |
| `BLADE_INTERP_TIMEOUT_MS` / `BLADE_INTERP_JOBS` | the interpreter differential's per-test ceiling and look-ahead |
| `NETCDF_DIR` | NetCDF provider include/link root |

Each optimizer pass has its own switch (`BLADE_FUSION`, `BLADE_CSE`, `BLADE_HOIST`,
`BLADE_COMPUTE_ELIDE`, `BLADE_SIBLING_FOLDS`, ... about fifty): unset is the default arm, `0` /
`off` is the as-written form, and some have named opt-in arms. Their rows in the notes say
what each admits and declines.

## Writing Blade: language essentials

A `.blade` file is a flat sequence of declarations; there is no `main` — bare top-level
expressions evaluate and print. `module Name` / `import X` / `from X import name` handle
multi-file programs.

```blade
let static n = 4                 // compile-time, immutable everywhere
let x = 1                        // reassign with `x = 2` in its OWN scope; a second top-level `let x` is BL2009
let mut y = 2                    // reassignable; `mut` params are ARRAY-only (element writes alias the caller; scalars: return instead, BL4005)

function add1(array: T^2) -> T^2 = { array + 1 }

function mean(row: T^1) -> T^0 = reduce(row, (+)) / Float64(extents(row))   // or: from stats import mean
function covariance(a: T^1, b: T^1) where comm(a, b) =
    mean((a - mean(a)) * (b - mean(b)))

Unit meters
Unit seconds
Unit mps = meters / seconds
type Speed = Float64<mps>          // unit-carrying scalar type

type LatIdx = Idx<180>           // named index types give structural identity
type LonIdx = Idx<360>
type EarthArray = Array<Float64 like LatIdx, LonIdx>
```

- Functions see **abstract types** `T^r` (element type + rank + symmetry); arrays have
  concrete types `Array<T like I, J, ...>`. `T<u>` marks a unit-carrying type variable.
- **Index types are the type system's spine**: `Idx<N>`, `EnumIdx<S>`,
  `SymIdx<r, N>` (canonical tuples `i₁ ≤ … ≤ iᵣ`, triangular storage),
  `AntisymIdx<r, N>` (strict `i₁ < … < iᵣ`, sign-tracked, **no stored diagonal**),
  `HermitianIdx<N>`, `CompoundIdx<mask>` (flat `B(lat, lon)` subscripts),
  `SparseIdx<keys>` (hashed; owns wildcard/partial reads `S((lat, _))`), `RaggedIdx`,
  `OrbIdx` (declarable at any depth, but beyond depth 1 only *deduced* classes reach
  storage), `IrrepsIdx<spec>` (equivariant ML).
- `Nat<LatIdx>` and `Nat<LonIdx>` don't unify even at equal extent — index provenance is
  part of the type. A subscript is either proven (a literal, an iteration index or parameter of
  exactly that type, a cast/guard) or checked at run time (BL8006), in every lane, for named and
  anonymous slots alike (`docs/formalism.md` §3.10). Iterate with `range<I>` / `halo<I, ...>` to
  get the proofs.
- `()` is application/indexing (curried); `[]` is ONLY tuple/pack structural access
  (`t[0]`, `args[k]`), never array indexing.
- A tuple is a value: `(g, 1.0)` evaluates a deferred map member AT CONSTRUCTION, left to
  right, as `g |> compute` would — a failing member fails there even if never projected
  (a tuple of ONLY deferred computations stays deferred; formalism §10.2).
- A deferred map (a `let` of a map with no `|> compute`) has no storage of its own: writing
  through it, a top-level alias or a view of it, or handing it to a `mut` parameter that is
  written or to a callback, is BL4005 — bind `|> compute` first. An `if` VALUE selecting between
  computed arrays inside a function / lambda / loop body, or one whose arm forces a deferred map
  that nothing forced earlier, is BL6002 — force it before the `if` (formalism §10.2 has the
  exact rule).
- Statements end at a newline, `;`, or a closer; anything else on the same line is BL1001
  (no implicit multiplication). A line opening with a binary operator continues the one
  above (indented past the statement's column); a line opening with `(`/`[` starts a new
  statement, never a call or index of the line above. At top level a `let`'s value starts on
  the `=` line (inside a block it may break after `=`). Prefix minus binds looser than `^`:
  `-x^2` is `-(x^2)` (formalism §15.1).
- Numeric literals (formalism §2.4): the spelling is the class (`1` integer, `1.0` float), the
  context is the width — an operator partner, an annotation, a parameter, a cast, the other
  `if`/`match` branch, the other array-literal elements, an index position (Int64). A
  literal-only expression adapts as a whole (`let c: Float64 = 1.0 / 3.0`). A literal that does
  not FIT the width its context fixes (`5000000000` where an Int32 is fixed, `1e40` where a
  Float32 is, `1e400` anywhere) is BL3020, never wrapped or rounded to inf / zero. With nothing
  fixing it the DEFAULT is the smaller width: Int32 (Int64 only past Int32's range), Float32
  (Float64 only outside Float32's finite range). So `let x = 0.5` is a Float32 VARIABLE: spell
  `let x: Float64 = 0.5` when you mean a Float64. **Nothing converts implicitly**, not even a
  widening at a call (BL3020): annotate, or cast with the type name in call position
  (`Float64(n)`, `Float64(xs)`).
- Arithmetic is defined on NUMBERS ONLY (formalism §2.4): a Bool, String, struct, sum-type
  value, tuple or function — or an array of them, a mask included — in `+ - * / % ^`,
  negation, `reduce(m, (+))`, `prodsum` or a math intrinsic, or compared with a number, is
  BL3001 (`+` joining two Strings is the one exception); `!` `&&` `||` take Bools only. A Bool is
  not a number: convert it with `Int64(b)` / `Float64(m)`; `reduce(Int64(m), (+))` counts a
  mask's true cells.
- Arithmetic has one contract in every lane (formalism §2.4): integers wrap; integer `/`
  `%` by zero and a negative integer exponent panic BL8013; a NaN/out-of-range float→int
  cast panics BL8014. Floating-point ROUNDING is fast by default (a compiled build may
  contract `a*b + c` to one FMA, and every LICENSED fold is reassociated into lanes /
  `omp simd reduction`); bit-reproducibility is opt-in: `where repro` per function,
  `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` per build.
- `where` clauses carry kernel metadata: `comm(a, b)` / `anticomm(...)` (interchangeable
  args), `omp(x: n)` (parallelism license, depth-capped), `cuda`, `tdim({...})`. A bare
  `omp` on a `reduce` kernel licenses fold reordering and requires commutativity (else
  BL4016), and is emitted vectorized/laned by default; `BLADE_FP_REPRO=1` restores the serial
  association. A fold that is the WHOLE value of its own `let` threads in a function body
  exactly as at top level; a fold that is an operand of arithmetic or a bare kernel return opens
  no team (the `[omp]` census says so). `reynolds(g)` *manufactures* a commutative kernel by
  symmetrization when you can't annotate one.

## Style guide

**The golden rule: iteration is declarative. Parallel structure is a loop object;
sequential structure is a recursive array. There is nothing else, by design.**

The imperative loop is not merely missing — it was removed and diagnosed:

```blade rejects
function f() -> Int64 = {
    let mut s = 0
    for k in 0..3 { s += k }   // removed from the language
    s
}
// ERROR: BL1003
```

(BL1003 fires at top level and inside blocks alike.)

Do not try to reintroduce it through workarounds. Every "loop-shaped" problem has a
first-class construct:

| You want | Write | Not |
|---|---|---|
| elementwise op over same index space | `A + B`, or `method_for(zip(A, B)) <@> f` | index-juggling |
| all-pairs / outer product | `A [+] B`, `A [*] B`, or `method_for(A, B) <@> f` | nested loops |
| full fold to a scalar | rank 1: `reduce(A, (+))`; rank k: `reduce(A, (+), axes = k)` | accumulator mutation |
| peel the innermost axis (partial reduction) | bare `reduce(A, (+))` — the default is `axes = 1`, so on rank ≥ 2 it returns an array, not a scalar | manual row loops |
| several statistics in ONE pass | `reduce((L <@> k1) <&!> (L <@> k2) <&!> (L <@> k3), (+))` then tuple-destructure | separate passes |
| filter / WHERE | `mask(xs, pred)` + `compound(data, mask)`; compose masks with `&&`/`\|\|` | a filter loop |
| iterate a constrained index domain (a band, a triangle, a selection rule) | `static struct Band { i: Int32<min=0, max=n-1>, j: Int32<min=0, max=n-1> } where abs(i - j) <= w` (`let static` n and w) then `reduce(method_for(range<Band>) <@> lambda(i, j) -> ..., (+))` — linear conjuncts enumerate in closed form as an affine loop nest, and the fold streams it with no array and no key table; `\|> compute` fills a sparse-shaped array for keyed reads `R((i, j))` | a dense nest with an `if` in the kernel; a mask over the whole box |
| stencil / lags / rolling window | `method_for(halo<I, [-1, 0, 1]>) <@> lambda(w) -> A(w(1)) - A(w(-1))` | index arithmetic with edge guards |
| index generation | `0..8` anonymous range (a first-class rank-1 array), or `range<I>` when the named tag should flow | materialized iota; `method_for(range) <@> lambda(i) -> i \|> compute` |
| coordinate axis / linspace | `x0 + dx * Float64(0..n)` — implicit lifting over the range | `method_for(range<I>) <@> lambda(j) -> x0 + dx * Float64(j)` for a body that is just affine in the index |
| sum/product of an index range | `reduce(0..n, (+))` | wrapping the range in a map first |
| numeric width/class conversion | `Float64(n)`, `Float32(x)`, `Int64(floor(x))` — scalar type name in call position; it converts the element only (arrays, generic `T`, `T^k` keep their shape, symmetry and units) | `* 1.0` fudges; any implicit int/float mix — `x * 2` over a float, `let y: Float64 = 1`, an Int64 into a Float64 parameter — is BL3020; bare `Int64(x)` on a float is BL3019 (the rounding must be visible: `Int64(floor(x))`) |
| pipeline of stages | compose values: `object_for(f) >>@ object_for(g)`, apply with `<@>`, materialize with `\|> compute` | eager temporaries per stage |
| recurrence / time-stepping / running state | `let rec` recursive array (see below) | `let mut` + a loop |
| iterate to convergence (trip count not known up front) | the inductive arm's `while` guard over a BUDGET extent: `\| prefix :: n while <cond> -> prefix :: <step>` — frozen once the guard goes false, runtime BL8010 if the budget runs out with it still true | a `while` loop; running the full budget unconditionally and hoping |
| sum over EVERY logical cell of a compact (SymIdx/AntisymIdx) array | `reduce(S, (+), axes = r)` (or the nested `range<I>` walk reading `f(S(i, j))`): one pass over the packed pool with orbit weights | `decompact` then fold; hand-weighted `2 * off + diag` triangle sums |
| symmetric pairwise stats (covariance, comoments) | `where comm(a, b)` kernels, `reynolds(...)`, `gram(R, R)` | hand-written triangular loops |
| several solves against one matrix | `let f = m.lu(A)` once, then `m.lu_solve(f, b)` / `m.lu_solve_t(f, b)` per side | `m.solve(A, b)` per side, refactoring A each time |
| Jacobian actions of an array-valued function, no Jacobian formed | `ad.jvp(f)(args..., seeds...)` = `(y, J v)`; `ad.vjp(f)(args..., w, buffers...)` accumulates `Jᵀ w` | a dense Jacobian by finite differences |
| random draws that survive chunking, reordering, restart | `import rand as r` then `r.uniform_at(key, stream, offset, n)` (and `normal_at`, ...): cell i is logical sample `offset + i`, a pure function of its address; give draws meant to be independent different `stream` ids | `r.uniform(key, n)` sliced — sequenced draws shift when the fill's extent or order changes |
| apply a Gram operator to a vector (`(A Bᴴ) x`) | `gram_apply(A, B, x)` — two rank-1 temporaries, never the N×N matrix | `gram(A, B)` then `decompact` then a row `prodsum` |
| per-chunk / per-file work over a chunked or multi-file axis | `type CI = Chunked<I, K>`, then `let seg = segments(CI)` / `files(T)` and `group_by(a, seg)` for per-run kernels; `ungroup(g)` restores the axis (docs/features/sql.md 7c) | hand-computed offsets; `join` (mints a fresh axis) |
| normalized / weighted pairwise sums (kernel smoothing, softmax rows) | a per-row kernel over `range<I>` whose body NAMES the deferred producer and folds it with a reduction join: `let w = method_for(xs) <@> k; let z, u = object_for(<&!>) <@> (reduce(w, (+)), prodsum(w, v)); u / z` (`examples/10`) | an M x N weight array then two partial folds |

Pick the highest idiom rung the body admits: implicit lifting over a range
(`x0 + dx * Float64(0..n)`) beats `method_for(range<...>) <@> lambda`, which in
turn beats `let rec` — but only when the body really is arithmetic in the
index. `method_for(range<...>)` stays the right spelling when the kernel is a
block or a gather/conditional, when the loop composes with combinators
(`>>@`, `<&!>`, multi-operand `method_for(range<I>, A, B)`), when the range is
multi-slot (`range<Y, X>`) or non-plain (`SymIdx`/`CompoundIdx`/`halo`), or
when you want the named index tag to flow into the result.

Real code (excerpts from `examples/` — the full files compile and run):

```blade sketch
// Filter + aggregate (examples/01_weather_stations.blade)
let qc_ok = mask(r_qc, lambda(q) -> q == 0)
let good_temps = compound(r_temp, qc_ok)
let n_good = extents(good_temps)
let sum_good = reduce(good_temps, (+))

// Pipeline as a value; nothing runs until compute (examples/03_signal_conditioning.blade)
let condition = object_for(calibrate) >>@ object_for(deadband)
let cond0 = condition <@> r0 |> compute

// One traversal, several accumulators (examples/03_signal_conditioning.blade)
let (s0, ss0, n0) = reduce((L0 <@> k_id) <&!> (L0 <@> square) <&!> (L0 <@> k_one), (+))
```

Sequential structure — the replacement for accumulation loops — is `let rec` by structural
induction on the leading axis (`docs/formalism.md` §7.5):

```blade
// tests/corpus/recursive-arrays/002_running_reduce.blade
type Step = Idx<6>
let rec x: Array<Float64 like Step> =
    match x with
    | zero -> zero
    | zero :: s -> zero :: 1.0
    | prefix :: n -> prefix :: prefix(n - 1) * 0.5 + 1.0
let total = reduce(x, (+))
```

This compiles to one pre-allocated buffer (no recursion frames), or to a small ring when its
prefix reads are all constant lags and its later uses are fixed-row reads or folds (`blade plan`
rules `rec-window`, `rec-fold-ring`). Reads past the built prefix are implicitly zero, so lag
schemes need **no** bounds guards — don't write `if n >= k then prefix(n - k) else 0.0`. The
recursion axis is always the leading axis and its extent must be static. The same shape scales
to RK4 time-stepping and DP tables (`tests/corpus/recursive-arrays/007`, `008`).

### Rules that prevent real bugs

- **`method_for(A, B)` is the outer product; `method_for(zip(A, B))` is co-iteration.**
  Confusing them silently changes the output rank.
- **`reduce` is a LEFT fold in ascending storage order, innermost axis first**:
  `reduce([1, 2, 3, 4], lambda(a, b) -> a - b)` is `((1 - 2) - 3) - 4 = -8`. Irrelevant for
  `(+)`; decisive the moment your fold kernel isn't commutative or associative.
- **Observable writes: refused in parallel bodies and joins, ordered everywhere else.** A
  kernel or function may assign a captured `let mut` binding in serial code; inside an
  `omp` / `cuda` body the same write is refused (BL4005, a race), and so is a `<&!>` / `<&>` /
  reduction-join leg whose map kernel, fold kernel, or a named deferred map it reads writes one
  (the join defines no order for it; `|> compute` the map first). Writes to storage the body
  makes itself (local scalars, fresh scratch arrays, let-rec buffers) are fine anywhere; a
  write through a view or alias of a captured array (`let t = G(0)`) is a write to G. A serial
  write is an ORDERED side effect, so no licence reorders it: a fold whose kernel or deferred
  map body writes keeps the serial chain in every build and lane (`blade plan` says why).
- **Only a `let mut` is writable from a callable that captures it.** A plain `let` may be
  assigned in the callable that defines it; a nested function, lambda or kernel — and a
  top-level function naming a module binding — that writes a binding it captures, directly or
  through a view, alias, tuple member, callee or `mut` argument, needs it declared `let mut`
  (BL4005; formalism 2.7). A parameter is read-only unless declared `mut`, through a local view
  or alias of it too; the declaration spells it `a: mut T^1`. A whole `v = e` rebinds and
  writes no storage.
- **A block-bound lambda is monomorphic when the block checks that way**; otherwise it is
  generic in exactly the types it is applied at (formalism 4.3). An operator section bound to a
  name (`let op = (+)`) IS the lambda `lambda(x, y) -> x + y` (`op(a, b)` and `a :op: b` are
  direct calls); a section in a kernel slot (`reduce(A, (+))`) is the slot's operator.
- **A function that reads a field names the struct**: `function getx(p) = p.x` with `p`
  unannotated is BL3024.
- **Don't hand-optimize.** No manual triangular index math, no hand-placed OpenMP pragmas,
  no manual BLAS calls. Declare `comm`/`anticomm`/`omp` and use array identity at the call
  site; the compiler derives triangular storage/iteration (r! savings), pragma placement,
  and BLAS routing. Symmetric output storage is *only* licensed when the SAME array occupies
  ADJACENT commuting positions — two different arrays over the same index space get nothing,
  and a non-adjacent repeat (`method_for(A, B, A)` with `comm(x, z)`) is stored dense in
  operand order with a BL4010 naming the adjacent spelling (formalism §8.3); both refusals
  are proved correct in the Rocq model (`docs/proofs.md` says what the model covers), and the
  second is proved sound-but-not-optimal. A `comm` claim the checker cannot decide is TRUSTED:
  a false one returns wrong values.
- **Named runtime functions can self-recurse; mutual recursion is rejected (BL2001)** — a
  body sees only names bound before it. Restructure into one function, or make both
  `static function`s (compile-time, may mutually recurse).
- **Refusals are features.** Unit clashes, index-type mismatches, extent disagreements
  (BL3016), unlicensed symmetry claims: the compiler refuses to compile a plausible-but-wrong
  fast path. When you hit one, fix the declaration, don't cast around it.
- Benchmark discipline: never measure at power-of-two extents (known ~7x cache artifact);
  interleave A/B runs and report medians, not single passes.

### Naming and formatting

- Types and index aliases: `PascalCase` (`StationIdx`, `Speed`); rank-polymorphic type vars
  are single capitals (`T`, `X`). Units: lowercase physical names (`meters`, `mps`).
- Values/functions: `snake_case` (`station_means`, `co_gain_dense`); short math scratch
  names (`k1`, `Z2`) are fine locally. Leading `__` marks internal non-API bindings.
- `///` doc comment above a declaration; `//` banner blocks as section headers; trailing
  `// EXPECT:` comments pin values in examples and corpus tests.
- 4-space indent inside `{ }` blocks. `where` goes between the parameter list and the return
  type (`function f(a: T^1, b: T^1) where comm(a, b) -> T^0 = ...`; after `->` it is a parse
  error).

## Working on the compiler (F#)

- **Compile order** in `Blade.fsproj` is dependency order and default items are disabled: adding
  a file means adding a `<Compile Include=...>` entry in the right position.
- **Recursive chains.** `TypeCheckInfer.fs` (inferExpr .. checkExpr), `CodeGenExpr.fs` and
  `CodeGenBinding.fs` each hold one `let rec ... and ...` chain. Keep a chain's functions small:
  a large match arm is a named function of the same chain, and a fall-through pyramid is a
  chain of named attempts tried in order (`dispatchAppOrIndex` in `TypeCheckSupport.fs` is the
  model). A chain holds only what recurses; the rest LEAVES it for another file, in one of
  three ways (the `TypeCheckInfer*.fs` files are the model, and the fsproj group comment lists
  them): a group the chain calls that never calls back goes in a file BEFORE the chain and
  takes no extra parameter (`TypeCheckInferUnits.fs`, `TypeCheckInferApply.fs`); a group that
  calls back takes the chain's entry points as its first argument, an immutable record the
  chain builds ONCE (`InferEntries` in `TypeCheckInferEntries.fs`, the value `inferEntries`),
  and binds the entries it uses under their own names (`TypeCheckInferRecArray.fs`); a group
  that only calls the chain goes in a file AFTER it (`TypeCheckInferDecl.fs`). Never a mutable
  forward reference, and never a closure built per call.
- **Shapes and traversals have names — use them.** `IR.fs` defines struct-returning active
  patterns for the shapes that repeat (`CallOfNamed`, `IndexOfNamed`, `ReduceOfNamed`,
  `ProjOfNamed`, `Tagged`, `IntTaggedIndex`, `NegIntLit`) and the generic walkers
  `existsIRExpr` / `visitTopDown` / `rewriteTopDown` over `(|ExprShape|)`, the one exhaustive
  match on all IR cases. `ExprShape` does NOT visit types and the extents they carry, callable
  bodies, module side tables or match patterns: an analysis that must see those says so and
  walks them (missed channels of exactly this kind are the optimizer's recurring bug class).
  A hand-written walker with a `| _ ->` default is not interchangeable with a generic one.
- **Error-or-continue staircases** in the type checker use the `typed { }` builder
  (`src/TypedBuilder.fs`). A new function boundary on a hot path can turn a cheap call into a
  slow `tail.` helper call; bind the result at the hand-off (`let r = f x in r`) when a
  type-checking time measurement moves.
- **Environment gates** are functions over the `Knobs` registry, not module-level `let`s, so
  tests can pin and restore them mid-process.
- **The lanes are differential twins.** The interpreter (`src/Interp/`), the C++ generator and
  the LLVM lane must agree: a behaviour change lands in all of them or the `test interp` /
  `test llvm` gates catch the drift. Scalar arithmetic has ONE written contract,
  `docs/formalism.md` §2.4; a change to it lands in every lane at once
  (`CodeGenExprSupport.renderContractBinOp` / `renderUnaryOpTyped` + `src/cpp/blade_runtime.hpp`,
  `src/Interp/Numerics.fs`, `src/EmitLlvm.fs` + `src/cpp/blade_llvm_shim.c`,
  `StaticEval.evalBinOp`).
- **Optimizer plans** follow one template: a gate in `optimizerGates`, an admission that fails
  closed and records the first failed condition, a `blade plan` record either way, a corpus
  witness, and an `opt-diff` comparison. The shared analyses (`scheduleMayWriteIn`,
  `storedArrayProof`, `aliasWriteClosure`, `IRTotality`) are the only accepted proofs of
  "nothing writes", "stored" and "cannot fail"; do not write a local approximation.
- **Diagnostics** have stable `BLxxxx` codes (`src/Diagnostics.fs`); corpus tests pin them, so
  changing a code or message means updating pins in the same change.
- **C++ names** come from ONE per-program table, `CodeGenState.installCppNameTable`: binding
  renames are keyed by the RAW IR spelling, callables are renamed at IR level, and a user
  spelling the emitted TU reserves moves. The reserved set is GENERATED: rerun
  `bash -o igncr tools/cpp-reserved-names/generate.sh` after adding a runtime header or a
  preamble `#define`. A name the emitter mints for a body local goes through `localVarName` /
  `avoidUserSpelled`.
- **Line endings.** The git index is LF except `src/cpp/index_types.{cpp,h}` (and `legacy/`
  `.edgi` files); `.gitattributes` only marks binary fixtures, and `core.autocrlf=true` makes
  working trees CRLF. Editing tools can silently flip a file's *stored* endings — check with
  `git ls-files --eol <path>`, and don't flip a file wholesale in an unrelated change.
- **F# style in this code base**: `xs[i]` index notation (not `.[`), `_.Member` for a
  one-member lambda, `[<TailCall>]` on a module-level function that is meant to loop,
  `[<return: Struct>]` on partial active patterns, no `%A` in anything a cache key or a
  message depends on, no new module-level mutable state.

## Docs map

`docs/formalism.md` is canonical for semantics (types, index types, loop objects,
combinators, symmetry, §2.4 arithmetic, §3.10 subscripts, §15 concrete syntax).
`docs/features.md` is the feature census with a per-row Status; trust formalism.md and the
corpus when they disagree. Every `blade` code block in the docs is checked by `blade test docs`.
`docs/proofs.md` maps which guarantees are machine-checked in `proofs/` versus
implemented-and-corpus-pinned. `docs/reference/knobs.md` and `knob-notes.md` cover the
environment knobs. Tutorials: `docs/quickstart-1.md`, `docs/quickstart-2.md`.
The richest idiom sources are `examples/01_weather_stations.blade`,
`examples/03_signal_conditioning.blade`, and `examples/lsdft.blade`.
