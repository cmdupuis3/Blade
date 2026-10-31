// OpenMP thread-coverage tests: verify emitted pragmas form genuine parallel
// regions, and that values stay correct under forced multi-threading.
// Extracted verbatim from Main.fs (audit §2.3). Requires g++; skips otherwise.
module Blade.Tests.OmpTests

open System
open Blade
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.Lowering
open Blade.CodeGen
open Blade.Build
open Blade.Tests.TestHarness
open Blade.Tests.Expect

// ============================================================================
// Pragma-emission tests (pure codegen; no g++, no threads, always run)
// ============================================================================
//
// The thread-coverage block below answers "does an emitted pragma form a real
// parallel team". These answer the question BEFORE it: "is a pragma emitted at
// all for the clause the user wrote". That gap is not academic — `where omp`
// on a NAMED FUNCTION used as a kernel was silently dropped for a long time,
// producing serial code with no diagnostic, because every existing omp test
// wrote the clause on an inline or let-bound LAMBDA. A lambda kernel keeps its
// where-clause on the TypedLambdaInfo; a named-function kernel is eta-expanded
// into a wrapper lambda built with no where-clause, so the clause has to be
// surfaced explicitly onto the wrapper (TypeCheck.etaExpandFunctionKernel).
// Both spellings are pinned here so the two paths cannot drift again.
//
// These assert on the generated C++ STRING rather than on runtime behaviour, so
// they need no toolchain and run in the default suite.

/// Pin one environment variable for the duration of a scope, restoring the
/// prior value on exit. Same use-guard idiom as `DiffOracle.pinFpContractOff`
/// and `LinAlgTests.pinEnv`, and it works for the same reason: every gate it
/// targets is read PER CALL, so a mid-process set takes effect immediately. A
/// `null` value UNSETS the variable.
let private pinEnv (name: string) (value: string) =
    let prior = System.Environment.GetEnvironmentVariable(name)
    System.Environment.SetEnvironmentVariable(name, value)
    { new System.IDisposable with
        member _.Dispose() = System.Environment.SetEnvironmentVariable(name, prior) }

/// Pin `BLADE_OMP_THREADS` UNSET for a whole block.
///
/// WHY EVERY OMP BLOCK NEEDS THIS. `BLADE_OMP_THREADS=1|0|off` is a BUILD knob
/// (CodeGen.ompThreadEmissionEnabled) that suppresses every thread-level OpenMP
/// construct, and it is meant to be set GLOBALLY on a deployment box. A user
/// with it set in their environment would otherwise turn these suites VACUOUS
/// rather than red: `blade test omp-pragma` would find no pragmas to assert on,
/// `omp-coverage` would find no teams to observe, and both would report their
/// own emptiness as success. Pinning it unset makes every block below measure
/// the DEFAULT emission whatever the ambient environment says; the arms that
/// deliberately exercise the knob pin it back on for their own scope.
let private pinOmpThreadsUnset () = pinEnv "BLADE_OMP_THREADS" null

/// Lower + generate, returning the C++ source. No compiler involved.
///
/// `lowerCaptured`, not `lower`: these cases assert EMISSION SHAPE, and several
/// of their kernels (a commutative body applied to one array twice) legitimately
/// earn a BL4010 storage suggestion. `lower` would print it straight to stderr
/// from inside the pipeline, un-attributed, in the middle of a run whose tests
/// all pass — the same leak the corpus lanes now capture. There is no pin
/// mechanism for these hand-written sources, so the warnings are simply dropped.
let private cppOf (testName: string) (src: string) : Result<string, string> =
    try
        match fst (lowerCaptured src) with
        | Error e -> Error ($"lower: {e}")
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    with ex -> Error ($"codegen raised: {ex.Message}")

/// A kernel body and the two arrays it folds over, spelled once. Each case
/// below varies ONLY how the kernel is written and where the clause sits.
let private ompPragmaCases : (string * string * bool) list =
    // (name, source, expectPragma)
    let arrays = "let A = Float64([1.0, 2.0, 3.0])\nlet B = Float64([4.0, 5.0, 6.0])\n"
    [ // The regression case: clause on a named function, function used as the
      // object_for kernel. Eta-expanded — the clause must survive the wrapper.
      ("named_function_object_for",
       "function cov(a: Float64, b: Float64) where omp(a: 1) = a * b\n" + arrays +
       "let m = object_for(cov) <@> (A, B) |> compute\n", true)
      // Same drop, reached through the OTHER eta site: a bare named function as
      // the RIGHT operand of <@>.
      ("named_function_method_for_apply",
       "function cov(a: Float64, b: Float64) where omp(a: 1) = a * b\n" + arrays +
       "let m = method_for(A, B) <@> cov |> compute\n", true)
      // Control: the spelling every pre-existing omp test uses. Passed before
      // the fix and must keep passing.
      ("let_bound_lambda",
       "let k = lambda(x, y) where omp(x: 1) -> x * y\n" + arrays +
       "let m = object_for(k) <@> (A, B) |> compute\n", true)
      // Control: inline lambda, the third spelling.
      ("inline_lambda",
       arrays +
       "let m = method_for(A, B) <@> lambda(x, y) where omp(x: 1) -> x * y |> compute\n", true)
      // Negatives — parallelism is OPT-IN. Identical programs minus the clause
      // must stay serial; without these the fix could degenerate into
      // "parallelize every named-function kernel".
      ("named_function_no_clause",
       "function cov(a: Float64, b: Float64) = a * b\n" + arrays +
       "let m = object_for(cov) <@> (A, B) |> compute\n", false)
      ("let_bound_lambda_no_clause",
       "let k = lambda(x, y) -> x * y\n" + arrays +
       "let m = object_for(k) <@> (A, B) |> compute\n", false) ]

/// The `omp(a: n)` DEPTH, checked as an exact pragma string rather than mere
/// presence. `n` is a LICENCE — "up to n dimensions of this argument may carry
/// threads" — counted per-argument, outermost first. It caps the structural
/// collapse/dynamic strategy instead of replacing it, so these cases pin the
/// interaction of the two rather than the licence alone.
///
/// Before this was implemented the depth was inert (written by
/// extractParallelism, read by nothing), so every case below emitted whatever
/// the bound structure alone dictated — `omp(a: 1)` on a 2-level nest produced
/// `collapse(2)`, threading a dimension of `b`, which granted nothing.
let private ompDepthCases : (string * string * string) list =
    // (name, source, exact expected pragma line)
    let arrays = "let A = Float64([1.0, 2.0, 3.0])\nlet B = Float64([4.0, 5.0, 6.0])\n"
    let apply = "let m = object_for(k) <@> (A, B) |> compute\n"
    let kern clause = $"function k(a: Float64, b: Float64) where {clause} = a * b\n"
    [ // One dimension licensed of a 2-level collapsible nest: collapse would
      // thread b's level too, so it must NOT be used.
      ("depth_1_of_2_no_collapse", kern "omp(a: 1)" + arrays + apply,
       "#pragma omp parallel for")
      // Both arguments licensed: collapse(2) is now permitted.
      ("depth_1_1_collapses_2", kern "omp(a: 1, b: 1)" + arrays + apply,
       "#pragma omp parallel for collapse(2)")
      // Per-argument counting: `a` is rank-1 and owns exactly one level, so a
      // depth of 2 cannot reach into b's level. (Under a whole-nest "budget"
      // reading this would collapse(2) — that reading is NOT what is
      // implemented; the depth counts levels OF THE NAMED ARGUMENT.)
      ("depth_2_on_rank1_arg_caps_at_1", kern "omp(a: 2)" + arrays + apply,
       "#pragma omp parallel for")
      // A licence on an argument owning an INNER level parallelizes that level
      // rather than silently doing nothing or threading the unlicensed outer.
      ("inner_arg_licence_moves_pragma", kern "omp(b: 1)" + arrays + apply,
       "#pragma omp parallel for")
      // The canonical documented case (quickstart-2 "Parallelism"): ONE argument
      // owning BOTH levels, so the depth alone scales how many are threaded.
      // This is the pair the old inert-depth behaviour could not distinguish —
      // it emitted collapse(2) for both.
      ("one_arg_two_levels_depth_1",
       "function k(a: Float64) where omp(a: 1) = a * 2.0\n" +
       "let M = Float64([[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]])\n" +
       "let m = object_for(k) <@> (M) |> compute\n",
       "#pragma omp parallel for")
      // Depth 2 on the same nest threads BOTH levels. Since Phase 3
      // (docs/plan-cpp-perf-exploitation.md) this nest is index-free
      // elementwise over one contiguous pool, so it is emitted as a single
      // flat loop over all cells and the pragma is `parallel for simd` rather
      // than `collapse(2)` over the two headers. Same licence, same threaded
      // dimensions — collapse(2) fuses exactly the iteration space the flat
      // loop already IS, so the fused form subsumes it (and adds SIMD, which
      // the flat write pattern licenses). The pair this case exists to
      // separate is unaffected: depth 1 above still threads one level only.
      ("one_arg_two_levels_depth_2",
       "function k(a: Float64) where omp(a: 2) = a * 2.0\n" +
       "let M = Float64([[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]])\n" +
       "let m = object_for(k) <@> (M) |> compute\n",
       "#pragma omp parallel for simd")
      // TUPLE PARAMETER (docs/plan-tuples-vs-arg-packs.md 6c). A `Tuple<k>`
      // parameter is ONE schema node whose levels are its k rows in order, and
      // the apply seam realizes it by replacing it with k row params -- which
      // deletes the name the licence resolves by. These two cases are the pin
      // that the licence survives that expansion AND keeps its depth meaning:
      // one level licensed of a two-row node is a plain `parallel for`, both
      // levels licensed is `collapse(2)`. Before the split, BOTH of these were
      // a hard BL3034 refusal (any where-clause + any tuple param).
      ("tuple_param_depth_1_no_collapse",
       "let A: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n" +
       "let B: Array<Float64 like Idx<3>> = [10.0, 20.0, 30.0]\n" +
       "let f = lambda(p: Tuple<2>) where omp(p: 1) -> p[0] * p[1]\n" +
       "let m = object_for(f) <@> (A, B) |> compute\n",
       "#pragma omp parallel for")
      ("tuple_param_depth_2_collapses",
       "let A: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n" +
       "let B: Array<Float64 like Idx<3>> = [10.0, 20.0, 30.0]\n" +
       "let f = lambda(p: Tuple<2>) where omp(p: 2) -> p[0] * p[1]\n" +
       "let m = object_for(f) <@> (A, B) |> compute\n",
       "#pragma omp parallel for collapse(2)")
      // The same licence reached through the NAMED-function eta wrapper, which
      // renames the params ONCE (to `__k<uid>_<i>`) before the tuple expansion
      // renames them again. The two remappings have to compose.
      ("tuple_param_named_function_depth_1",
       "function f(p: Tuple<2>) where omp(p: 1) = p[0] * p[1]\n" +
       "let A: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n" +
       "let B: Array<Float64 like Idx<3>> = [10.0, 20.0, 30.0]\n" +
       "let m = object_for(f) <@> (A, B) |> compute\n",
       "#pragma omp parallel for") ]

/// Comm-licensed parallel REDUCTIONS (Phase 2 of
/// docs/plan-cpp-perf-exploitation.md). `where ... omp` on a FOLD kernel opts
/// the reduction into a parallel fold; which SHAPE it gets is decided by the
/// licence, and the two shapes are textually unmistakable:
///
///   Path A — builtin `+`/`*` body: `#pragma omp parallel for simd
///     reduction(<op>:acc)` over the flat sweep, no runtime API at all.
///   Path B — any other licensed kernel (and every reduce over a deferred
///     computation): an explicit team with per-thread partials, which is the
///     only shape that calls `omp_get_max_threads` / `omp_get_thread_num`.
///
/// Asserting `mustNotContain` matters as much as `mustContain` here: the two
/// paths differ in reassociation guarantees (Path A's combine order is
/// unspecified; Path B's is fixed by chunk index), so a kernel silently
/// switching paths changes a property tests downstream rely on.
/// Gap G41 fixtures for the provenance cases below: a map-licensed fold whose
/// `comm` kernel body is the argument.
let private g41Pre1 = "type I = Idx<37>\ntype K = Idx<2>\nlet mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n"
let private g41Pre2 = "type I = Idx<37>\ntype K = Idx<2>\nlet mut G: Array<Float64 like K, K> = method_for(range<K>, range<K>) <@> lambda(k, l) -> 0.0 |> compute\n"
let private g41Fold (body: string) =
    "let s = reduce(method_for(range<I>) <@> lambda(i) where omp(i: 1) -> Float64(i) + G((0 : K), (0 : K)), lambda(a, b) where comm(a, b) -> " + body + ", 0.0)\n"
let private g41Fold1 (body: string) =
    "let s = reduce(method_for(range<I>) <@> lambda(i) where omp(i: 1) -> Float64(i) + G((0 : K)), lambda(a, b) where comm(a, b) -> " + body + ", 0.0)\n"
let private g41WriteCensus = "[omp] requested but emitted serial: the fold kernel writes a captured binding; a map licence does not make the fold race-safe"
let private g41Team = "comm-licensed parallel fold, outer level chunked"
let private g41NoTeam = ["BLADE_OMP_PARALLEL_NUM_THREADS("; "blade_fold::chunk_lo"]
/// Every spelling of a dependence-discarding producer annotation (Codex
/// review W2F-4 (f)): the portable macro, the directive however spelled
/// (`GCC ivdep`, `#pragma ivdep`, `_Pragma("ivdep")`), and the omp producer
/// SIMD / team routes. (The bare word `ivdep` is not banned: the emitted
/// program's own explanatory comments mention it.)
let private g41WriterBans =
    [ "BLADE_IVDEP"; "GCC ivdep"; "pragma ivdep"; "_Pragma(\"ivdep"
      "BLADE_OMP_SIMD\n"; "#pragma omp simd"; "BLADE_OMP_PARALLEL_FOR_SIMD"; "BLADE_OMP_PARALLEL_FOR\n"
      "BLADE_OMP_PARALLEL_FOR_COLLAPSE"; "BLADE_OMP_PARALLEL_FOR_DYNAMIC" ]
let private ompReduceCases : (string * string * string list * string list) list =
    // (name, source, mustContain, mustNotContain)
    let arr = "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])\n"
    [ // Path A: builtin body, no comm needed — `+` carries commutativity and
      // associativity outright.
      ("reduce_builtin_lambda_omp",
       arr + "let s = reduce(A, lambda(a, b) where omp -> a + b)\n",
       ["#pragma omp parallel for simd reduction(+:s)"],
       ["omp_get_max_threads"])
      ("reduce_builtin_product_omp",
       arr + "let s = reduce(A, lambda(a, b) where omp -> a * b)\n",
       ["#pragma omp parallel for simd reduction(*:s)"], [])
      // Negative control: parallelism is OPT-IN. The identical program minus
      // the clause must emit the old serial accumulator loop.
      ("reduce_builtin_lambda_no_clause",
       arr + "let s = reduce(A, lambda(a, b) -> a + b)\n",
       ["// reduce: accumulator loop, eager"], ["#pragma omp"])
      // Negative control: an operator section cannot carry a where-clause, so
      // the most common fold spelling of all stays serial.
      ("reduce_section_no_clause",
       arr + "let s = reduce(A, (+))\n",
       ["// reduce: accumulator loop, eager"], ["#pragma omp"])
      // Path B via a NAMED function — the eta-prone spelling, and the one whose
      // body is invisible at the reduce seam (TypeEnv.FuncFoldBuiltin exists for
      // exactly this). Body is deliberately not a bare builtin op.
      ("reduce_named_comm_function",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" + arr +
       "let s = reduce(A, myAdd)\n",
       ["#pragma omp parallel num_threads("; "omp_get_thread_num()"],
       ["#pragma omp parallel for"])
      // Path B via an inline comm lambda.
      ("reduce_inline_comm_lambda",
       arr + "let s = reduce(A, lambda(a, b) where comm(a, b), omp -> (a + b) * 1.0)\n",
       ["#pragma omp parallel num_threads("], ["#pragma omp parallel for"])
      // Path B with an explicit init: the seed stays in the shared accumulator
      // and enters the fixed-order combine first, so nothing about the chunk
      // shape changes.
      ("reduce_named_comm_with_init",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" + arr +
       "let s = reduce(A, myAdd, 100.0)\n",
       ["#pragma omp parallel num_threads("], [])
      // Path B over a DEFERRED computation: no intermediate array is
      // materialized, and the OUTERMOST level is the one chunked.
      //
      // `axes = 2` IS THE FEATURE'S SPELLING, not a decoration. The chunked
      // fold collapses the whole cell grid into one scalar accumulator, and
      // since the 2026-08-09 ruling on `reduce` (docs/features/sql.md 10) the
      // full fold is `axes = rank` -- the default is the innermost axis only.
      // A rank-2 deferred computation reduced WITHOUT the count is a partial
      // fold to a rank-1 array, which is a different program: it takes the
      // row-wise `<@>` route, materializes the grid, and never reaches the
      // fused reduction terminal that owns this emission. Dropping the count
      // here is what made this case (and the two knob arms below) vacuous.
      ("reduce_over_computation_chunked",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" + arr +
       "let B = Float64([2.0, 3.0, 4.0, 5.0, 6.0, 7.0])\n" +
       "let s = reduce(method_for(A, B) <@> lambda(x, y) -> x * y, myAdd, 0.0, axes = 2)\n",
       ["comm-licensed parallel fold, outer level chunked"
        "#pragma omp parallel num_threads("
        "for (size_t __i0 = __rlo; __i0 < __rhi; __i0++)"], [])
      // Same computation, clause dropped: back to the materialize-then-fold
      // desugar, and no parallel region anywhere.
      ("reduce_over_computation_no_clause",
       "function myAdd(a: Float64, b: Float64) where comm(a, b) = (a + b) * 1.0\n" + arr +
       "let B = Float64([2.0, 3.0, 4.0, 5.0, 6.0, 7.0])\n" +
       "let s = reduce(method_for(A, B) <@> lambda(x, y) -> x * y, myAdd, 0.0, axes = 2)\n",
       [], ["#pragma omp"])
      // ---- the EXPRESSION-form reduce: declined, but no longer silent -------
      // A reduce in expression position (kernel body, inline arithmetic) opens
      // no team ON PURPOSE -- its context is routinely already inside a parallel
      // region, so a nested team would be a pessimisation or the wrong size (see
      // the note atop `renderReduceExpr`). The decline is legitimate; being
      // SILENT about it was the bug, and these cases are what keep it audible.
      //
      // A BLOCK comment is asserted, not just the phrase: every arm of that
      // emitter space-joins its statements into ONE line, so a `//` marker would
      // swallow the fold. That is a compile error, which makes the comment STYLE
      // load-bearing rather than cosmetic.
      ("reduce_in_kernel_body_marks_dropped_omp",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" +
       "let M = Float64([[1.0, 2.0, 3.0, 4.0, 5.0], [4.0, 5.0, 6.0, 7.0, 8.0], [9.0, 10.0, 11.0, 12.0, 13.0]])\n" +
       "let r = method_for(M) <@> lambda(row) -> reduce(row, myAdd, 0.0) |> compute\n",
       ["/* [omp] requested but emitted serial: reduce in expression position"],
       ["#pragma omp"; "omp_get_"])
      // The same drop reached through the PARTIAL FOLD, which is what makes this
      // marker matter: since the 2026-08-09 `reduce` ruling the default folds the
      // innermost axis only, and `partialFold` desugars that into the row-wise
      // form above -- so this perfectly ordinary rank-2 `reduce` lands in
      // expression position and its `omp` clause buys nothing. `axes = 2` is the
      // spelling that gets the parallel fold (reduce_over_computation_chunked).
      ("reduce_partial_default_marks_dropped_omp",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" +
       "let M = Float64([[1.0, 2.0, 3.0, 4.0, 5.0], [4.0, 5.0, 6.0, 7.0, 8.0], [9.0, 10.0, 11.0, 12.0, 13.0]])\n" +
       "let r = reduce(M, myAdd, 0.0)\n",
       ["/* [omp] requested but emitted serial: reduce in expression position"],
       ["#pragma omp"; "omp_get_"])
      // Negative control, and the one that keeps the two above honest: the marker
      // reports a DROPPED CLAUSE, so a kernel that never asked must not carry it.
      // Without this the emitter could pass both cases by marking every reduce.
      ("reduce_in_kernel_body_no_clause_no_marker",
       "function myAdd(a: Float64, b: Float64) where comm(a, b) = (a + b) * 1.0\n" +
       "let M = Float64([[1.0, 2.0, 3.0, 4.0, 5.0], [4.0, 5.0, 6.0, 7.0, 8.0], [9.0, 10.0, 11.0, 12.0, 13.0]])\n" +
       "let r = reduce(M, myAdd, 0.0)\n",
       [], ["[omp]"; "#pragma omp"])
      // ---- the licence on the MAP feeding a fold (bench gap G27) ------------
      // `where omp(a: 1)` on the row map a builtin `(+)` consumes licenses the
      // outermost level the fold sweeps -- the level Path B chunks -- and `(+)`
      // is reorder-licensed, so the fold threads. Before, this placement was
      // accepted and DROPPED with no trace (the one-level simd arm never reached
      // the nest emitter whose marker would have reported it).
      ("map_licence_feeding_fold_threads_path_b",
       "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0])\n" +
       "let s = reduce(method_for(A) <@> lambda(a) where omp(a: 1) -> a * a, (+))\n",
       ["comm-licensed parallel fold, outer level chunked"
        "#pragma omp parallel num_threads("
        "omp_get_"],
       ["[omp] requested but emitted serial"])
      // Negative control: the same fold, no clause on the map -- no team.
      ("map_without_licence_feeding_fold_no_team",
       "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0])\n" +
       "let s = reduce(method_for(A) <@> lambda(a) -> a * a, (+))\n",
       [], ["[omp]"; "#pragma omp parallel"; "omp_get_"])
      // ---- gap G41: a licence never makes a WRITING kernel race-safe --------
      // The map's `omp(i: 1)` reaches the fold's level, but the `comm` fold
      // kernel writes a captured `let mut` (legal: BL4005 covers parallel
      // bodies only): its applications are ordered side effects, so it is not
      // reorder-licensed -- no Path B team, no lanes, the serial chain, and the
      // dropped map licence says why. Corpus loops/492 pins the values.
      ("map_licence_writing_comm_fold_kernel_stays_serial",
       "type I = Idx<37>\ntype K = Idx<2>\n"
       + "let mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n"
       + "let s = reduce(method_for(range<I>) <@> lambda(i) where omp(i: 1) -> Float64(i) + G((0 : K)), lambda(a, b) where comm(a, b) -> { G((0 : K)) = G((0 : K)) + 1.0; a + b }, 0.0)\n",
       ["[omp] requested but emitted serial: the fold kernel writes a captured binding; a map licence does not make the fold race-safe"],
       ["BLADE_OMP_PARALLEL_NUM_THREADS("; "omp_get_"; "blade_fold::chunk_lo"; "__rlane"; "SIMD_REDUCTION"])
      // The mirror: a `where omp` FOLD over a map whose body writes (the map
      // carries no omp). The fold's licence covers reassociating the fold,
      // not running the map body on a team or under `omp simd`. Corpus
      // loops/493 pins the values.
      ("omp_fold_over_writing_map_body_stays_serial",
       "type I = Idx<37>\ntype K = Idx<2>\n"
       + "let mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n"
       + "let s = reduce(method_for(range<I>) <@> lambda(i) -> { G((0 : K)) = G((0 : K)) + 1.0; G((0 : K)) + Float64(i) }, lambda(a, b) where omp -> a + b, 0.0)\n",
       ["[omp] requested but emitted serial: the map kernel writes a captured binding; a fold licence does not make the map's body race-safe"],
       ["BLADE_OMP_PARALLEL_NUM_THREADS("; "omp_get_"; "blade_fold::chunk_lo"; "__rlane"; "SIMD_REDUCTION"])
      // Storage PROVENANCE, not initializer syntax (Codex review of W2-F,
      // blocker 1): a store through a ROW VIEW of the captured G, or through an
      // array a call returned, is a write to G; corpus loops/495 pins values.
      ("map_licence_fold_kernel_writes_through_view_stays_serial",
       g41Pre2 + g41Fold "{ let t = G((0 : K)); t((0 : K)) = t((0 : K)) + 1.0; a + b }",
       [g41WriteCensus], g41NoTeam)
      ("map_licence_fold_kernel_writes_returned_alias_stays_serial",
       g41Pre1 + "function getG() -> Array<Float64 like K> = G\n" + g41Fold1 "{ let t = getG(); t((0 : K)) = t((0 : K)) + 1.0; a + b }",
       [g41WriteCensus], g41NoTeam)
      // ...and storage the kernel makes itself is LOCAL: these still thread.
      ("map_licence_fold_kernel_local_scalar_threads",
       g41Pre1 + g41Fold1 "{ let mut q = a; q = q + b; q }",
       [g41Team], [g41WriteCensus])
      ("map_licence_fold_kernel_fresh_scratch_threads",
       g41Pre1 + g41Fold1 "{ let mut s = method_for(range<K>) <@> lambda(k) -> Float64(0.0) |> compute; s((0 : K)) = a; s((1 : K)) = b; s((0 : K)) + s((1 : K)) }",
       [g41Team], [g41WriteCensus])
      ("map_licence_fold_kernel_local_alias_threads",
       g41Pre1 + g41Fold1 "{ let mut s = method_for(range<K>) <@> lambda(k) -> Float64(0.0) |> compute; let u = s; u((0 : K)) = a + b; s((0 : K)) }",
       [g41Team], [g41WriteCensus])
      ("map_licence_fold_kernel_callee_fills_fresh_scratch_threads",
       g41Pre1 + "function fill(buf: mut Array<Float64 like K>, v: Float64) -> Float64 = {\n    buf((0 : K)) = v\n    v\n}\n"
       + g41Fold1 "{\n    let mut scratch = method_for(range<K>) <@> lambda(k) -> Float64(0.0) |> compute\n    fill(scratch, a + b)\n}",
       [g41Team], [g41WriteCensus])
      // REBINDING (Codex re-review, finding 9): `t` starts as fresh scratch,
      // then is rebound to a row VIEW of G -- every later store through `t`
      // writes G. A fresh-to-fresh rebind stays local.
      ("map_licence_fold_kernel_rebound_to_view_stays_serial",
       g41Pre2 + g41Fold "{\n    let mut t = method_for(range<K>) <@> lambda(k) -> Float64(0.0) |> compute\n    t = G((0 : K))\n    t((0 : K)) = t((0 : K)) + 1.0\n    a + b\n}",
       [g41WriteCensus], g41NoTeam)
      ("map_licence_fold_kernel_rebound_fresh_to_fresh_threads",
       g41Pre1 + g41Fold1 "{\n    let mut t = method_for(range<K>) <@> lambda(k) -> Float64(0.0) |> compute\n    t = method_for(range<K>) <@> lambda(k) -> Float64(1.0) |> compute\n    t((0 : K)) = a + b\n    t((0 : K))\n}",
       [g41Team], [g41WriteCensus])
      ("map_licence_fold_kernel_callee_local_let_rec_threads",
       g41Pre1 + "function tri(v: Float64) -> Float64 = {\n    let rec r: Array<Float64 like K> =\n        match r with\n        | zero -> zero\n        | zero :: s -> zero :: v\n        | prefix :: n -> prefix :: prefix(n - 1) + v\n    reduce(r, (+))\n}\n"
       + g41Fold1 "tri(a + b)",
       [g41Team], [g41WriteCensus])
      ("map_licence_fold_kernel_callee_fills_captured_stays_serial",
       g41Pre1 + "function fill(buf: mut Array<Float64 like K>, v: Float64) -> Float64 = {\n    buf((0 : K)) = v\n    v\n}\n"
       + g41Fold1 "{ fill(G, a + b) }",
       [g41WriteCensus], g41NoTeam)      // ---- M14 (gap G25): the fast form INSIDE each Path B chunk ------------
      // A `where omp` builtin-`+` fold over a dot-class body: the team is
      // opened and each thread's chunk is ONE call of the explicit K-lane
      // vector form over [__rlo, __rhi) -- no loop nest at all; partition by
      // blade_fold::chunk_lo, partials combined in job order.
      ("path_b_dot_class_vector_per_chunk",
       "type Ix = Idx<1001>\n" +
       "let a = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7 - 3) |> compute\n" +
       "let b = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 5 - 2) |> compute\n" +
       "let s = reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y, lambda(p, q) where omp -> p + q, 0.0)\n",
       ["BLADE_OMP_PARALLEL_NUM_THREADS("
        "blade_fold::chunk_lo(__rn, (size_t)__rt, (size_t)__rnt)"
        "blade_fold::dot<double, 8>(__rsrc0 + __rlo, __rsrc1 + __rlo, __rhi - __rlo)"
        "#include \"blade_fold_lanes.hpp\""],
       ["for (size_t __i0 = __rlo"; "SIMD_REDUCTION"])
      // A `where omp` builtin-`+` fold whose body is NOT a bare product: the
      // nest is kept, chunked per thread, and the chunk's loop carries its own
      // `omp simd reduction` into an identity-seeded accumulator read through
      // restrict pool aliases.
      ("path_b_simd_reduction_per_chunk",
       "type Ix = Idx<1001>\n" +
       "let a = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7 - 3) |> compute\n" +
       "let s = reduce(method_for(a) <@> lambda(v) -> (v + 1.0) * v, lambda(p, q) where omp -> p + q, 0.0)\n",
       ["BLADE_OMP_PARALLEL_NUM_THREADS("
        "blade_fold::chunk_lo(__rn_"
        "BLADE_OMP_SIMD_REDUCTION(+:__racc_"
        "for (size_t __i0 = __rlo; __i0 < __rhi; __i0++)"
        "__rhad_"],
       ["blade_fold::dot<"])
      // BLADE_FOLD_LANES=0 is the A/B knob: both chunk forms revert to the
      // serial chain seeded from the chunk's first value (checked by the
      // knob-off case in the optimize block's plan pins; here the SHAPE).
      // ---- a licensed fold inside a top-level result TUPLE (G27 / G14) ------
      // A module binding's tuple is a statement position, so the fold is bound
      // to its own let (IRLift) and takes the same Path A team the let-bound
      // spelling takes -- not the expression-position decline.
      // (The sibling component is a plain read, not a second fold of A: two
      // licensed folds of A beside each other are joined by the sibling-fold
      // pass and threaded as ONE join (G7) -- pinned in omp-reduce's g7_*
      // cases -- which is not this case's subject.)
      ("tuple_component_licensed_fold_opens_team",
       "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0])\n" +
       "let (a, b) = (A(0), reduce(A, lambda(p, q) where omp -> p + q, 0.0))\n",
       ["#pragma omp parallel for simd reduction(+:"],
       // (A fold in a KERNEL BODY keeps the structural decline, pinned above
       // by reduce_in_kernel_body_marks_dropped_omp: only the statement
       // position changed.)
       ["reduce in expression position"]) ]

/// The BL-coded refusal for an UNLICENSED `omp` on a fold kernel. Pinned in the
/// diagnostics corpus too (049_fold_omp_needs_license.blade); repeated here so
/// the codegen-side block that owns the two emission paths also owns the
/// statement that there is no third, silent one.
let private ompReduceDiagCases : (string * string) list =
    // (name, source) — each must fail to lower, with BL4016 in the message.
    let arr = "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])\n"
    [ ("omp_without_comm_antisym_body",
       arr + "let s = reduce(A, lambda(a, b) where omp -> a - b)\n")
      ("omp_without_comm_nonbuiltin_body",
       arr + "let s = reduce(A, lambda(a, b) where omp -> (a + b) * 2.0)\n")
      ("omp_named_function_without_comm",
       "function halfSum(a: Float64, b: Float64) where omp = (a + b) * 0.5\n" + arr +
       "let s = reduce(A, halfSum)\n") ]

/// Emission SHAPE pins for the three decisions that are invisible in values and
/// unreachable from the pragma-text cases above: how the native (BLAS-gate-off)
/// gram/matmul arms are threaded and ordered, whether the loop nest annotates a
/// header it cannot vectorize, and which DIRECTION a threaded triangular outer
/// level runs in. All three are pure codegen assertions -- no toolchain, no
/// threads -- for the same reason the cases above are: the emitted text is the
/// artifact, and a value test cannot see any of it.
///
/// `mustNotContain` carries as much weight as `mustContain` in every case here:
/// each is an OLD emission whose disappearance is the actual claim (the strided
/// `B[__mt][__mj]` read, the register accumulator, the per-iteration operand
/// subscripts, an `ivdep` on a loop containing a loop).
let private emissionShapeCases : (string * string * string list * string list) list =
    // (name, source, mustContain, mustNotContain)
    //
    // Non-power-of-two extents throughout (3x5, 5x7, 4x5): a bound rendered
    // from the wrong axis is invisible at square or equal extents.
    let matmulSrc =
        "import math as m\n" +
        "type M35 = Array<Float64 like Idx<3>, Idx<5>>\n" +
        "type M57 = Array<Float64 like Idx<5>, Idx<7>>\n" +
        "let A: M35 = [[1.0, 2.0, 3.0, 4.0, 5.0], [6.0, 7.0, 8.0, 9.0, 10.0], [11.0, 12.0, 13.0, 14.0, 15.0]]\n" +
        "let B: M57 = [[1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0], [8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0], [15.0, 16.0, 17.0, 18.0, 19.0, 20.0, 21.0], [22.0, 23.0, 24.0, 25.0, 26.0, 27.0, 28.0], [29.0, 30.0, 31.0, 32.0, 33.0, 34.0, 35.0]]\n" +
        "let P = m.matmul(A, B)\n"
    let gramSrc =
        "type M35 = Array<Float64 like Idx<3>, Idx<5>>\n" +
        "type M45 = Array<Float64 like Idx<4>, Idx<5>>\n" +
        "let A: M35 = [[1.0, 2.0, 3.0, 4.0, 5.0], [6.0, 7.0, 8.0, 9.0, 10.0], [11.0, 12.0, 13.0, 14.0, 15.0]]\n" +
        "let C: M45 = [[2.0, 3.0, 5.0, 7.0, 11.0], [13.0, 17.0, 19.0, 23.0, 29.0], [31.0, 37.0, 41.0, 43.0, 47.0], [53.0, 59.0, 61.0, 67.0, 71.0]]\n" +
        "let G = gram(A, A)\n" +
        "let H = gram(A, C)\n"
    // Rank-2 nest whose kernel body is a `prodsum` over the peeled row fibers.
    // The nest sees TWO levels; the `__pt` loop lives inside the body's IIFE and
    // is invisible to it -- which is exactly how `ivdep` came to sit on a header
    // whose body is a loop.
    let fiber = "Array<Float64 like Idx<5>>"
    let rowsDecl = "let R = Float64([[1.0, 2.0, 3.0, 4.0, 5.0], [6.0, 7.0, 8.0, 9.0, 10.0], [11.0, 12.0, 13.0, 14.0, 15.0]])\n"
    let prodsumSrc =
        rowsDecl +
        $"let Cv = method_for(R, R) <@> lambda(a: {fiber}, b: {fiber}) -> prodsum(a, b) |> compute\n"
    let loopFreeSrc =
        rowsDecl +
        $"let D = method_for(R, R) <@> lambda(a: {fiber}, b: {fiber}) -> a(0) * b(0) |> compute\n"
    // Rank-3 triangular comm nest with an omp licence: the `schedule(dynamic)`
    // outer level. Extent 13 -- prime, and not 3, so a bound that lost a
    // dependency subtraction is visible.
    let triSrc =
        "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0])\n" +
        "let L = method_for(A, A, A)\n" +
        "let k = lambda(x, y, z) where comm(x, y, z), omp(x: 1) -> x * y * z + x + y + z\n" +
        "let R = L <@> k |> compute\n"
    [ // ---- matmul: i-t-j, in-place row accumulation, threaded outer ----------
      ("matmul_native_arm_reordered_i_t_j", matmulSrc,
       [ // outer level threaded (via the portable macro -- see
         // cpp/blade_portability.hpp for why this is not a `#pragma` line:
         // these emitters' output can be space-joined into a one-line IIFE).
         // Spelled with the header it governs so the _DYNAMIC variant cannot
         // satisfy it by prefix.
         "BLADE_OMP_PARALLEL_FOR\nfor (size_t __mi = 0; __mi < 3; __mi++) {"
         // the accumulator is the output row itself, hoisted and restrict-qualified
         "double* BLADE_RESTRICT __mcrow = &P[__mi][0];"
         // ... which therefore has to be zeroed first, at the literal n
         "for (size_t __mj = 0; __mj < 7; __mj++) { __mcrow[__mj] = double(); }"
         // t OUTSIDE j: this ordering IS the change. Literal `5` = A's trailing
         // extent, baked from A's own index record rather than read at runtime.
         "for (size_t __mt = 0; __mt < 5; __mt++) {"
         "const double __ma = A[__mi][__mt];"
         "const double* BLADE_RESTRICT __mbrow = &B[__mt][0];"
         // unit-stride inner loop, and ivdep is TRUE here (fresh output pool)
         "BLADE_IVDEP"
         "__mcrow[__mj] += __ma * __mbrow[__mj];" ],
       [ // the strided column walk that made the old order slow
         "B[__mt][__mj]"
         // the register accumulator the old order needed
         "__macc"
         // runtime extent reads for bounds the operand types already pin
         "__mt < A.extents[1]"
         "__mj < B.extents[1]" ])
      // ---- gram: both arms threaded, rows hoisted, order UNCHANGED -----------
      ("gram_native_arms_threaded_and_hoisted", gramSrc,
       [ // same-array arm is triangular (inner span `3 - __gi`), so dynamic
         "BLADE_OMP_PARALLEL_FOR_DYNAMIC"
         "const size_t __gspan = 3 - __gi;"
         // distinct arm is rectangular, so the static schedule
         "BLADE_OMP_PARALLEL_FOR\nfor (size_t __gi = 0; __gi < 3; __gi++) {"
         // both operand rows hoisted out of the contraction loop
         "const double* BLADE_RESTRICT __growi = &A[__gi][0];"
         "const double* BLADE_RESTRICT __growj = &A[__gj][0];"
         "const double* BLADE_RESTRICT __growj = &C[__gj][0];"
         // contraction reads go through the hoists; `k` is still innermost and
         // still bounded by the LITERAL trailing extent
         "for (size_t __gk = 0; __gk < 5; __gk++) {"
         "__gacc += __growi[__gk] * nested_array_utilities::conj_scalar(__growj[__gk]);" ],
       [ // the per-iteration double subscript the hoists replaced
         "A[__gi][__gk]"
         "C[__gj][__gk]"
         // gram's inner loop is a REDUCTION into `__gacc`: a loop-carried
         // dependence, so `ivdep` there would be a false claim AND inert.
         // (matmul's inner loop is not a reduction, which is why it gets one.)
         "BLADE_IVDEP\nfor (size_t __gk" ])
      // ---- ivdep is declined, loudly, when the body hides a loop -------------
      ("ivdep_declined_on_prodsum_body", prodsumSrc,
       [ "// [ivdep] declined: kernel body contains an inner loop" ],
       [ "BLADE_IVDEP" ])
      // Control: identical nest shape, loop-free body. Without this the fix
      // could degenerate into "never emit ivdep", which is not the claim.
      ("ivdep_kept_on_loop_free_body", loopFreeSrc,
       [ "BLADE_IVDEP" ],
       [ "// [ivdep] declined" ])
      // ---- G41 (Codex re-re-review, finding 15): `ivdep` asserts NO
      // loop-carried dependence, so a producer whose kernel WRITES captured
      // storage (a running counter) never gets it -- the plain flat loop, the
      // cell-epilogue producer, and the general nest. Pure twins keep it.
      ("ivdep_declined_on_writing_flat_producer",
       "let x = Float64([1.0, 2.0, 3.0, 4.0, 5.0])\nlet mut G = Float64([0.0])\nlet p = method_for(x) <@> lambda(v) -> {\n    G(0) = G(0) + v\n    G(0)\n} |> compute\n",
       [ "for (size_t __fk = 0; __fk < __fp_cells; __fk++)" ], g41WriterBans)
      ("ivdep_kept_on_pure_flat_producer",
       "let x = Float64([1.0, 2.0, 3.0, 4.0, 5.0])\nlet p = method_for(x) <@> lambda(v) -> v * 2.0 + 1.0 |> compute\n",
       [ "BLADE_IVDEP" ], [])
      ("ivdep_declined_on_writing_epilogue_producer",
       "let x = Float64([1.0, 2.0, 3.0, 4.0, 5.0])\nlet mut G = Float64([0.0])\nlet p = method_for(x) <@> lambda(v) -> {\n    G(0) = G(0) + v\n    G(0)\n} |> compute\nlet total = reduce(method_for(p) <@> lambda(v) -> v, (+), 0.0)\n",
       // the path taken: the plain flat producer (the epilogue declined it)
       [ "// flat elementwise: 5 cells"; "for (size_t __fk = 0; __fk < __fp_cells; __fk++)" ], g41WriterBans @ [ "blade_fold::lane_state" ])
      ("ivdep_declined_on_writing_nest_producer",
       "type I = Idx<3>\ntype J = Idx<4>\nlet mut G = Float64([0.0])\nlet p = method_for(range<I>, range<J>) <@> lambda(i, j) -> {\n    G(0) = G(0) + 1.0\n    G(0)\n} |> compute\n",
       // the path taken: the general two-level nest
       [ "for (size_t __i0 = 0; __i0 < 3; __i0++) {"; "for (size_t __i1 = 0; __i1 < 4; __i1++) {" ], g41WriterBans)
      // ---- G41 (Codex review W2F-5, finding 20): a parallel kernel that
      // calls a CALLBACK is judged through every call site's argument. A
      // pure callback keeps the team (corpus loops/503); a writing one is
      // refused at lowering (mutability-errors/029). Where the emitter's own,
      // more conservative analysis still sees a possible write (here: a
      // kernel-body `let mut c = G`, which the C++ lane aliases -- gap G46),
      // the general nest drops the team WITH the census line.
      ("omp_pure_callback_keeps_team",
       "function twice(v: Float64) -> Float64 = v * 2.0\nfunction launch(f: (Float64) -> Float64) = method_for(0..2) <@> lambda(i) where omp(i: 1) -> f(Float64(i) + 1.0) |> compute\nlet r = launch(twice)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      // Codex review W2F-6 (f): a pure SELF-RECURSIVE helper forwarding an
      // array (corpus loops/504) and a pure callback forwarded through one
      // (loops/505) keep the team.
      ("omp_recursive_pure_helper_keeps_team",
       "type I = Idx<3>\nlet a: Array<Float64 like I> = [1.0, 2.0, 3.0]\nfunction walk(v: Array<Float64 like I>, k: Int64, acc: Float64) -> Float64 =\n    if k >= 3 then acc else walk(v, k + 1, acc + v((k : I)))\nlet r = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> walk(a, 0, Float64(i)) |> compute\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      ("omp_pure_callback_through_recursion_keeps_team",
       "function scale(v: Int64) -> Float64 = Float64(v) * 2.0\nfunction recur(f: (Int64) -> Float64, n: Int64) -> Float64 = if n == 0 then f(0) else recur(f, n - 1)\nfunction launch(g: (Int64) -> Float64) = method_for(0..2) <@> lambda(i) where omp(i: 1) -> recur(g, 3) + Float64(i) |> compute\nlet r = launch(scale)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      // Codex review W2F-7 finding 24: a pure NULLARY callback (the
      // empty-slot arrow; corpus loops/506) keeps the team.
      ("omp_pure_nullary_callback_keeps_team",
       "function one() -> Float64 = 1.0\nfunction launch(f: () -> Float64) = method_for(0..2) <@> lambda(i) where omp(i: 1) -> f() + Float64(i) |> compute\nlet r = launch(one)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      // Codex review W2F-7 finding 25 controls: every candidate reaching
      // launch's `f` -- directly and through via's indirect `h(f)` -- is pure,
      // named (loops/507) or a lambda (loops/508): the team is kept.
      ("omp_second_order_pure_callbacks_keep_team",
       "type I = Idx<2>\nfunction ident(v: Float64) -> Float64 = v\nfunction twice(v: Float64) -> Float64 = v * 2.0\nfunction launch(f: (Float64) -> Float64) -> Array<Float64 like I> = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nfunction via(h: ((Float64) -> Float64) -> Array<Float64 like I>, f: (Float64) -> Float64) -> Array<Float64 like I> = h(f)\nlet a = launch(ident)\nlet b = via(launch, twice)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      ("omp_second_order_pure_lambda_keeps_team",
       "type I = Idx<2>\nfunction ident(v: Float64) -> Float64 = v\nfunction launch(f: (Float64) -> Float64) -> Array<Float64 like I> = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nfunction via(h: ((Float64) -> Float64) -> Array<Float64 like I>, f: (Float64) -> Float64) -> Array<Float64 like I> = h(f)\nlet a = launch(ident)\nlet b = via(launch, lambda(v) -> v + 10.0)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      // Codex review W2F-8: a MODULE alias of the launcher (26, loops/509),
      // a local alias of a callback parameter called directly or through a
      // tuple (27, loops/510-511), and a third-order chain in both discovery
      // orders (28, loops/512-513) -- all pure: the team is kept.
      ("omp_pure_callback_through_module_alias_keeps_team",
       "type I = Idx<2>\nfunction ident(v: Float64) -> Float64 = v\nfunction launch(f: (Float64) -> Float64) -> Array<Float64 like I> = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nlet run = launch\nlet r = run(ident)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      ("omp_pure_callback_via_local_alias_keeps_team",
       "type I = Idx<2>\nfunction ident(v: Float64) -> Float64 = v\nfunction launch(f: (Float64) -> Float64) -> Array<Float64 like I> = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nfunction via(h: ((Float64) -> Float64) -> Array<Float64 like I>, f: (Float64) -> Float64) -> Array<Float64 like I> = {\n    let k = h\n    k(f)\n}\nlet a = launch(ident)\nlet b = via(launch, ident)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      ("omp_pure_callback_via_tuple_held_alias_keeps_team",
       "type I = Idx<2>\nfunction ident(v: Float64) -> Float64 = v\nfunction launch(f: (Float64) -> Float64) -> Array<Float64 like I> = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nfunction via(h: ((Float64) -> Float64) -> Array<Float64 like I>, f: (Float64) -> Float64) -> Array<Float64 like I> = {\n    let k = h\n    let t = (k, Int64(0))\n    t[0](f)\n}\nlet a = launch(ident)\nlet b = via(launch, ident)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      ("omp_pure_callback_third_order_keeps_team",
       "type I = Idx<2>\ntype F = (Float64) -> Float64\ntype R = Array<Float64 like I>\ntype H = (F) -> R\ntype Q = (H, F) -> R\ntype D = (Q, F) -> R\nfunction ident(v: Float64) -> Float64 = v\nfunction launch(f: F) -> R = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nfunction via(h: H, f: F) -> R = h(f)\nfunction bridge(q: Q, f: F) -> R = q(launch, f)\nfunction relay(h: D, f: F) -> R = h(via, f)\nlet a = launch(ident)\nlet b = relay(bridge, ident)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      ("omp_pure_callback_third_order_reversed_keeps_team",
       "type I = Idx<2>\ntype F = (Float64) -> Float64\ntype R = Array<Float64 like I>\ntype H = (F) -> R\ntype Q = (H, F) -> R\ntype D = (Q, F) -> R\nfunction ident(v: Float64) -> Float64 = v\nfunction launch(f: F) -> R = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nfunction via(h: H, f: F) -> R = h(f)\nfunction relay(h: D, f: F) -> R = h(via, f)\nfunction bridge(q: Q, f: F) -> R = q(launch, f)\nlet b = relay(bridge, ident)\nlet a = launch(ident)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      // Gap G46: a kernel-body `let mut c = G` DEEP-COPIES G in the C++ lane
      // too (it used to alias G's pool; the interpreter always copied), so
      // the backend write analysis reads the copy as LOCAL storage
      // (CopiedLet) and the `where omp` kernel keeps its team. This was the
      // census case of the nest's write veto (`omp_nest_write_veto_census`);
      // every other shape that writes captured storage from a parallel body
      // is refused BL4005 before the emitter (Lowering's provenance and
      // callback checks), so the veto stays a backstop. corpus loops/517
      // pins the values in every lane.
      ("omp_nest_kernel_let_mut_copy_keeps_team",
       "type K = Idx<2>\ntype I = Idx<3>\nlet G = method_for(range<K>) <@> lambda(k) -> Float64(0.0) |> compute\nlet r = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> {\n    let mut c = G\n    c((0 : K)) = Float64(i)\n    c((0 : K)) + G((0 : K))\n} |> compute\n",
       [ "BLADE_OMP_PARALLEL_FOR"; "std::copy_n(pool_base(G.data)" ],
       [ "[omp] requested but emitted serial" ])
      // Gap G47 (Codex review W2F-9 finding 29): RETURN FLOW in the callback
      // table -- a returned local alias of a callback parameter, a tuple
      // PARAMETER read only for its scalar, and the further return shapes of
      // loops/516 -- keeps a pure kernel's team (each was refused BL4005 at
      // f1910060; the writing twins are mutability-errors/042-047).
      ("omp_pure_callback_returned_through_local_alias_keeps_team",
       "type I = Idx<2>\ntype F = (Float64) -> Float64\ntype R = Array<Float64 like I>\ntype H = (F) -> R\nfunction ident(v: Float64) -> Float64 = v\nfunction launch(f: F) -> R = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nfunction passthrough(h: H) -> H = {\n    let k = h\n    k\n}\nlet a = launch(ident)\nlet b = passthrough(launch)(ident)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      ("omp_pure_callback_tuple_parameter_reads_scalar_keeps_team",
       "type I = Idx<2>\ntype F = (Float64) -> Float64\ntype R = Array<Float64 like I>\ntype H = (F) -> R\nfunction ident(v: Float64) -> Float64 = v\nfunction launch(f: F) -> R = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nfunction tag(p: (H, Int64)) -> Int64 = p[1]\nfunction via(h: H, f: F) -> R = {\n    let k = h\n    let t = (k, Int64(0))\n    let n = tag(t)\n    h(f) + Float64(n)\n}\nlet a = launch(ident)\nlet b = via(launch, ident)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      ("omp_pure_callback_return_flow_shapes_keep_team",
       "type I = Idx<2>\ntype F = (Float64) -> Float64\ntype R = Array<Float64 like I>\ntype H = (F) -> R\nfunction ident(v: Float64) -> Float64 = v\nfunction launch(f: F) -> R = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute\nfunction first(p: (H, Int64)) -> H = p[0]\nfunction rec_ret(n: Int64, h: H) -> H = if n <= 0 then h else rec_ret(n - 1, h)\nfunction pair(h: H) -> (H, Int64) = (h, 1)\nfunction mk() -> H = launch\nfunction pick(c: Bool, h: H) -> H = if c then h else launch\nlet a = launch(ident)\nlet b = first((launch, Int64(0)))(ident)\nlet c = rec_ret(3, launch)(ident)\nlet d = pair(launch)[0](ident)\nlet e = pick(true, mk())(ident)\n",
       [ "BLADE_OMP_PARALLEL_FOR" ], [ "requested but emitted serial" ])
      ("ivdep_kept_on_pure_nest_producer",
       "type I = Idx<3>\ntype J = Idx<4>\nlet p = method_for(range<I>, range<J>) <@> lambda(i, j) -> Float64(i * 4 + j) * 0.5 |> compute\n",
       [ "BLADE_IVDEP" ], [])      // ---- the threaded triangular outer level runs ASCENDING ----------------
      // Pinned as a DECISION, not as an accident of never having considered it.
      // A performance audit proposed reversing this level to descending, on the
      // theory that `schedule(dynamic)` hands out chunks in iteration order so
      // descending would be largest-chunk-first (LPT). It is backwards for this
      // shape: `genForLoopHeader` SUBTRACTS the outer index from a triangular
      // level's bound (below: `13 - __i0`, `13 - __i1 - __i0`), so work per
      // outer iteration DECREASES in `__i0` and ascending order is already
      // largest-first. Reversing would end the schedule on the ~C(13,2)-cell
      // row instead of the 1-cell row -- turning the good makespan bound into
      // the bad one. See the derivation at CodeGen.genNestPragma.
      ("triangular_outer_level_stays_ascending", triSrc,
       [ "#pragma omp parallel for schedule(dynamic)"
         "for (size_t __i0 = 0; __i0 < 13; __i0++) {"
         // the bounds that make ascending the largest-first order
         "for (size_t __i1 = 0; __i1 < 13 - __i0; __i1++) {"
         "for (size_t __i2 = 0; __i2 < 13 - __i1 - __i0; __i2++) {" ],
       [ // the two descending spellings, neither of which is an OpenMP
         // canonical loop form anyway
         "__i0-- > 0"
         "__i0 = 13;" ]) ]

/// Which loop index the pragma precedes, for the inner-licence case. Presence
/// alone cannot distinguish "parallelized the licensed inner level" from
/// "parallelized the unlicensed outer level".
let private ompPlacementCases : (string * string * string) list =
    // (name, source, index variable the pragma must immediately precede)
    let arrays = "let A = Float64([1.0, 2.0, 3.0])\nlet B = Float64([4.0, 5.0, 6.0])\n"
    let apply = "let m = object_for(k) <@> (A, B) |> compute\n"
    let kern clause = $"function k(a: Float64, b: Float64) where {clause} = a * b\n"
    [ ("outer_licence_pragma_on_i0", kern "omp(a: 1)" + arrays + apply, "__i0")
      ("inner_licence_pragma_on_i1", kern "omp(b: 1)" + arrays + apply, "__i1") ]

/// The RAGGED / GROUPED PEEL row loop (`CodeGen.peelRowPragma`).
///
/// `tryRaggedPeel` and `tryGroupedZipPeel` hand-roll their `__g` row loop and
/// bypass the generic loop-nest builder, which is the only place `genNestPragma`
/// attaches a pragma. A `where omp(...)` clause on a kernel applied to a grouped
/// or ragged array was therefore ACCEPTED AND SILENTLY DROPPED -- and because
/// "asked and got serial" is byte-identical to "never asked", the drop was
/// invisible. These cases pin both directions of the fix.
///
/// A SEPARATE TABLE from `ompDepthCases`, which asserts exactly one pragma
/// PROGRAM-WIDE. That is a different claim than "one pragma for this nest" once
/// group-by emission is in the program, and conflating them would make a future
/// pragma elsewhere in the group-by lowering read as a failure here.
///
/// `None` means NO pragma anywhere and no `[omp]` marker either -- parallelism is
/// opt-in, and the clause-deleted twins are the claim that silence stays the
/// default so existing corpus output is unchanged.
///
/// KERNELS ARE LAMBDAS, NOT NAMED FUNCTIONS, deliberately: `checkOmpInternalLoop`
/// fires only on function decls, and warns when a licensed param is an operand of
/// an inner apply whose kernel carries no clause. A named-function spelling of
/// the zip case could earn a stray BL4001 that has nothing to do with what these
/// cases measure. The corpus uses the lambda spelling for these shapes anyway.
let private peelPragmaCases : (string * string * string option) list =
    // (name, source, the single expected pragma line -- None = no pragma at all)
    let dynamicPragma = Some "#pragma omp parallel for schedule(dynamic)"
    // Grouped: uneven group sizes, the shape `schedule(dynamic)` exists for.
    let grouped body =
        "let region = Int64([0, 1, 0, 1, 1])\n" +
        "let temps = Float64([20.0, 25.0, 30.0, 22.0, 27.0])\n" +
        "let gk = group_keys(region)\n" +
        "let grouped = group_by(temps, gk)\n" + body
    let groupedZip body =
        "let region = Int64([0, 1, 0, 1, 1])\n" +
        "let a = Float64([1.0, 2.0, 3.0, 4.0, 5.0])\n" +
        "let b = Float64([10.0, 20.0, 30.0, 40.0, 50.0])\n" +
        "let gk = group_keys(region)\n" +
        "let ga = group_by(a, gk)\nlet gb = group_by(b, gk)\n" + body
    let raggedLit body =
        "let lens: Array<Int64 like Idx<3>> = [3, 2, 1]\n" +
        "let r: Array<Float64 like Idx<3>, RaggedIdx<lens>> = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]]\n" + body
    let raggedInline body =
        "let r = Float64([[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]])\n" + body
    let row = "Array<Float64 like RaggedIdx<_>>"
    [ // ---- grouped, single operand: the reported case ---------------------
      ("peel_grouped_reduce_licensed",
       grouped ($"let s = method_for(grouped) <@> lambda(g: {row}) where omp(g: 1) -> reduce(g, (+)) |> compute\n"),
       dynamicPragma)
      ("peel_grouped_reduce_unlicensed",
       grouped ($"let s = method_for(grouped) <@> lambda(g: {row}) -> reduce(g, (+)) |> compute\n"),
       None)
      // ---- grouped co-iteration (tryGroupedZipPeel) ------------------------
      ("peel_grouped_zip_licensed",
       groupedZip ($"let d = method_for(zip(ga, gb)) <@> lambda(ra: {row}, rb: {row}) where omp(ra: 1) -> prodsum(ra, rb) |> compute\n"),
       dynamicPragma)
      ("peel_grouped_zip_unlicensed",
       groupedZip ($"let d = method_for(zip(ga, gb)) <@> lambda(ra: {row}, rb: {row}) -> prodsum(ra, rb) |> compute\n"),
       None)
      // ---- ragged LITERAL, row-consuming ----------------------------------
      ("peel_ragged_literal_licensed",
       raggedLit ($"let s = method_for(r) <@> lambda(g: {row}) where omp(g: 1) -> reduce(g, (+)) |> compute\n"),
       dynamicPragma)
      ("peel_ragged_literal_unlicensed",
       raggedLit ($"let s = method_for(r) <@> lambda(g: {row}) -> reduce(g, (+)) |> compute\n"),
       None)
      // ---- ragged ELEMENTWISE map (the two-loop arm) ----------------------
      // The pragma must land on `__g`; the `__k` header below it stays bare.
      // Honouring `omp(e: 2)` would need collapse(2), and the inner bound
      // `r.lens[__g]` is a memory load rather than affine in `__g` -- outside
      // even OpenMP 5.0's non-rectangular collapse, so it would be ill-formed.
      ("peel_ragged_elementwise_licensed",
       raggedInline "let d = method_for(r) <@> lambda(e) where omp(e: 1) -> e * 2.0 |> compute\n",
       dynamicPragma)
      ("peel_ragged_elementwise_unlicensed",
       raggedInline "let d = method_for(r) <@> lambda(e) -> e * 2.0 |> compute\n",
       None) ]

/// `omp(g: 0)`: REQUESTED, but licensing no level. `parseOmpArgs` accepts any
/// `TokInt`, so this parses with `IsOmpParallel = true` and a maximum depth of 0
/// -- which makes `peelRowPragma`'s licence-decline branch reachable, and that
/// branch is the whole difference between declining out loud and the silent drop
/// this change exists to fix.
///
/// NOT expressible in `peelPragmaCases`: its `None` arm means "no pragma AND no
/// marker" (never asked), whereas this case must emit no pragma AND a marker.
/// The two expectations are opposites, which is why this is its own case.
let private peelDeclineMarkerCase : string * string =
    ("peel_depth_zero_reports_serial",
     "let region = Int64([0, 1, 0, 1, 1])\n" +
     "let temps = Float64([20.0, 25.0, 30.0, 22.0, 27.0])\n" +
     "let gk = group_keys(region)\n" +
     "let grouped = group_by(temps, gk)\n" +
     "let s = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) where omp(g: 0) -> reduce(g, (+)) |> compute\n")

// ============================================================================
// BLADE_OMP_THREADS -- the serial-emission BUILD knob
// ============================================================================
//
// The LICENCE in source ("this kernel is safe to thread") and the BUILD KNOB
// ("this deployment spends licensed parallelism") are separate decisions; the
// knob turns the second off without touching the first. See
// `CodeGen.ompThreadEmissionEnabled` for the measurement that motivated it (a
// pragma'd row map at OMP_NUM_THREADS=1 runs 1.86x SLOWER than the same code
// emitted without the pragma -- GCC's outlining is a compile-time cost no
// runtime thread count recovers).
//
// Every case below runs the SAME sources the blocks above run, so a licence
// that stopped reaching codegen would fail there first and these would not
// silently take its place.

/// What each formerly-threaded site emits with the knob on. `mustNotContain`
/// carries the actual claim in every case: the point of the knob is the ABSENCE
/// of team-creating constructs, and `omp_get_*` absence is what says the
/// program acquired no OpenMP runtime dependency either.
let private ompThreadsKnobCases : (string * string * string list * string list) list =
    // (name, source, mustContain, mustNotContain)
    // Non-power-of-two extents throughout, per the repo's stride discipline.
    let arrays = "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0])\nlet B = Float64([4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0])\n"
    let arr = "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0])\n"
    let marker = "// [omp] requested but emitted serial: BLADE_OMP_THREADS=1"
    [ // ---- loop nests: genNestPragma's three forms all go away --------------
      ("knob_licensed_map_emits_no_team",
       "function cov(a: Float64, b: Float64) where omp(a: 1) = a * b\n" + arrays +
       "let m = object_for(cov) <@> (A, B) |> compute\n",
       [ marker; "for (size_t __i0 = 0; __i0 < 7; __i0++) {" ],
       [ "#pragma omp parallel"; "omp_get_" ])
      ("knob_collapse_form_suppressed",
       "function k(a: Float64, b: Float64) where omp(a: 1, b: 1) = a * b + 1.0\n" +
       "let P = Float64([1.5, 2.5, 3.5, 4.5, 5.5])\nlet Q = Float64([0.25, 0.5, 0.75, 1.25])\n" +
       "let M = object_for(k) <@> (P, Q) |> compute\n",
       [ marker ], [ "#pragma omp parallel"; "collapse("; "omp_get_" ])
      // The triangular arm: `schedule(dynamic)` is a `parallel for` clause, so
      // it goes with the construct rather than surviving on a serial loop.
      ("knob_triangular_dynamic_suppressed",
       "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0])\n" +
       "let L = method_for(A, A, A)\n" +
       "let k = lambda(x, y, z) where comm(x, y, z), omp(x: 1) -> x * y * z + x + y + z\n" +
       "let R = L <@> k |> compute\n",
       [ marker; "for (size_t __i1 = 0; __i1 < 13 - __i0; __i1++) {" ],
       [ "#pragma omp parallel"; "schedule(dynamic)"; "omp_get_" ])
      // ---- the flat elementwise fast path KEEPS ITS VECTORIZATION ----------
      // `parallel for simd` is two constructs; only the first opens a team.
      // Dropping to `BLADE_IVDEP` here would throw away a vectorization the
      // knob was never about, so this case is the one that pins the split.
      ("knob_flat_elementwise_keeps_omp_simd",
       "function k(a: Float64) where omp(a: 2) = a * 2.0\n" +
       "let M = Float64([[1.0, 2.0, 3.0, 4.0, 5.0], [4.0, 5.0, 6.0, 7.0, 8.0], [9.0, 10.0, 11.0, 12.0, 13.0]])\n" +
       "let m = object_for(k) <@> (M) |> compute\n",
       [ marker; "#pragma omp simd" ],
       [ "#pragma omp parallel"; "omp_get_" ])
      // ---- reduce Path A: same split, through the portable macro -----------
      ("knob_reduce_path_a_keeps_simd_reduction",
       arr + "let s = reduce(A, lambda(a, b) where omp -> a + b)\n",
       [ marker; "BLADE_OMP_SIMD_REDUCTION(+:s)" ],
       [ "#pragma omp parallel"; "omp_get_" ])
      ("knob_reduce_path_a_product",
       arr + "let s = reduce(A, lambda(a, b) where omp -> a * b)\n",
       [ "BLADE_OMP_SIMD_REDUCTION(*:s)" ],
       [ "#pragma omp parallel"; "omp_get_" ])
      // ---- reduce Path B: nothing to keep, so the serial arm ---------------
      // Path B IS the threading (an explicit team with per-thread partials), so
      // unlike Path A there is no vector half to preserve. The kernel lands on
      // the arm an UNLICENSED kernel takes, and the marker is what keeps the
      // dropped clause from being silent there.
      ("knob_reduce_path_b_named_comm_serial",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" + arr +
       "let s = reduce(A, myAdd)\n",
       [ marker; "// reduce: accumulator loop, eager" ],
       [ "#pragma omp"; "omp_get_"; "__rpart_s" ])
      // The MAP-licensed fold (bench gap G27): the chunk plan the map's
      // `omp(a: 1)` set is dead in this build, so the fold takes the
      // vectorized form -- with the census line for the dropped team (block
      // form: this arm's lines may be space-joined into a kernel-body IIFE).
      ("knob_map_licensed_fold_simd_and_census",
       arr + "let s = reduce(method_for(A) <@> lambda(a) where omp(a: 1) -> a * a, (+))\n",
       // A self-dot body: the vector form since M9; the census line stays.
       [ "/* [omp] requested but emitted serial: BLADE_OMP_THREADS=1"; "blade_fold::sq<double, 8>(" ],
       [ "#pragma omp parallel"; "omp_get_"; "outer level chunked" ])
      // `axes = 2` for the reason spelled out at `reduce_over_computation_chunked`:
      // without it this is a partial fold that never had a chunk plan to drop,
      // so the knob arm and its `_control_knob_off` twin both go vacuous.
      ("knob_reduce_over_computation_serial",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" + arr +
       "let B = Float64([2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0])\n" +
       "let s = reduce(method_for(A, B) <@> lambda(x, y) -> x * y, myAdd, 0.0, axes = 2)\n",
       [ marker ],
       [ "#pragma omp"; "omp_get_"; "outer level chunked" ])
      // ---- the intrinsic emitters (gram / matmul) --------------------------
      // These emit the portable MACRO, not a `#pragma` line, because their
      // output can be space-joined into a one-line IIFE -- which is also why
      // their suppression marker is a BLOCK comment. Both facts are pinned.
      // ---- the ragged / grouped PEEL row loop ------------------------------
      // The peel hand-rolls its `__g` loop outside the nest builder, so it needs
      // its own knob check; without this case the knob could silently stop
      // covering the newest thread-emitting site. The loop below generates a
      // knob-OFF control twin requiring `#pragma omp parallel` to be present,
      // which is what keeps this arm from passing vacuously.
      ("knob_ragged_peel_row_loop_suppressed",
       "let region = Int64([0, 1, 0, 1, 1])\n" +
       "let temps = Float64([20.0, 25.0, 30.0, 22.0, 27.0])\n" +
       "let gk = group_keys(region)\n" +
       "let grouped = group_by(temps, gk)\n" +
       "let s = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) where omp(g: 1) -> reduce(g, (+)) |> compute\n",
       [ marker; "for (size_t __g = 0; __g < gk__ngroups; __g++) {" ],
       [ "#pragma omp parallel"; "schedule(dynamic)"; "omp_get_" ])
      // ---- the intrinsic emitters (gram / matmul) --------------------------
      // These emit the portable MACRO, not a `#pragma` line, because their
      // output can be space-joined into a one-line IIFE -- which is also why
      // their suppression marker is a BLOCK comment. Both facts are pinned.
      ("knob_native_matmul_gram_macros_suppressed",
       "import math as m\n" +
       "type M35 = Array<Float64 like Idx<3>, Idx<5>>\n" +
       "type M57 = Array<Float64 like Idx<5>, Idx<7>>\n" +
       "let A: M35 = [[1.0, 2.0, 3.0, 4.0, 5.0], [6.0, 7.0, 8.0, 9.0, 10.0], [11.0, 12.0, 13.0, 14.0, 15.0]]\n" +
       "let B: M57 = [[1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0], [8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0], [15.0, 16.0, 17.0, 18.0, 19.0, 20.0, 21.0], [22.0, 23.0, 24.0, 25.0, 26.0, 27.0, 28.0], [29.0, 30.0, 31.0, 32.0, 33.0, 34.0, 35.0]]\n" +
       "let P = m.matmul(A, B)\nlet G = gram(A, A)\n",
       [ "/* [omp] thread pragma suppressed: BLADE_OMP_THREADS=1"
         // the loops themselves are untouched -- only the macro line moved
         "for (size_t __mi = 0; __mi < 3; __mi++) {"
         "for (size_t __gi = 0; __gi < 3; __gi++) {"
         // ivdep is a VECTORIZATION assertion, not a thread construct: it stays
         "BLADE_IVDEP" ],
       [ "BLADE_OMP_PARALLEL_FOR"; "#pragma omp parallel"; "omp_get_" ]) ]

/// The strongest statement the knob makes: with it on, a LICENSED program emits
/// the SAME C++ as the identical program with the clause deleted. "Serial
/// emission" is not "some other serial shape" -- `pragmaLevel` goes to None, so
/// `ompLastLevel` goes to -1 and `BLADE_IVDEP` lands exactly where the
/// unlicensed nest puts it, and every downstream decision follows.
///
/// Compared modulo the `[omp]` census markers, which the licensed program emits
/// BY DESIGN (a dropped clause must not be silent) and the unlicensed one has
/// nothing to report.
let private ompThreadsEquivalenceCases : (string * string * string) list =
    // (name, licensed source, the same program with the clause deleted)
    let arrays = "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0])\nlet B = Float64([4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0])\n"
    let tri = "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0])\nlet L = method_for(A, A, A)\n"
    // `clause` is the ONLY textual difference between the two programs, so the
    // comparison cannot accidentally be measuring some other edit.
    let grouped (clause: string) =
        "let region = [0, 1, 0, 1, 1]\n" +
        "let temps = Float64([20.0, 25.0, 30.0, 22.0, 27.0])\n" +
        "let gk = group_keys(region)\n" +
        "let grouped = group_by(temps, gk)\n" +
        $"let s = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) {clause}-> reduce(g, (+)) |> compute\n"
    [ ("knob_equals_unlicensed_map",
       "function cov(a: Float64, b: Float64) where omp(a: 1) = a * b\n" + arrays +
       "let m = object_for(cov) <@> (A, B) |> compute\n",
       "function cov(a: Float64, b: Float64) = a * b\n" + arrays +
       "let m = object_for(cov) <@> (A, B) |> compute\n")
      // The ragged/grouped PEEL. Stronger here than for the nest cases: the peel
      // has no `pragmaLevel`/`ompLastLevel` state to reset, so byte-identity is a
      // direct statement that adding the licence changes NOTHING about the
      // emitted row loop except the pragma line itself -- no reordered
      // declarations, no altered subscripts, and in particular no reassociated
      // per-row reduction.
      ("knob_equals_unlicensed_grouped_peel",
       grouped "where omp(g: 1) ", grouped "")
      ("knob_equals_unlicensed_triangular",
       tri + "let k = lambda(x, y, z) where comm(x, y, z), omp(x: 1) -> x * y * z + x + y + z\n" +
       "let R = L <@> k |> compute\n",
       tri + "let k = lambda(x, y, z) where comm(x, y, z) -> x * y * z + x + y + z\n" +
       "let R = L <@> k |> compute\n") ]

// ---- G35: threaded complete-row folds under the repro build -------------
//
// A `where omp` fold whose outer level's value is ONE complete scalar fold per
// index: under BLADE_FP_REPRO=1 with threads on, each row's fold runs whole on
// one thread into `__rslot[i]` and the outer chain runs serially afterwards
// (ChunkRowSlots). Every case pins the fold-form record AND the emitted C++,
// so a record that drifted from the emission (or the reverse) fails here.

/// The bench checksum shape over a SymIdx<2, p> pool: `outer` is the outer
/// fold kernel, `body` the inner cell expression (reads g(i, j)).
let private rowSlotSym2 (p: int) (outer: string) (body: string) =
    $"type Var = Idx<{p}>\ntype Obs = Idx<3>\n"
    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 7 + s * 3) % 11) * 0.37 + 0.1 / Float64(i + 1) |> compute\n"
    + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
    + $"let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> {body}, (+)), {outer}, 0.25)\n"

let private rowSlotOmpOuter = "lambda(u, v) where omp -> u + v"

/// The outer-kernel-write counterexample (Codex diff review, blocker 1): the
/// MAP carries the thread request, the fold kernel is a serial `comm` kernel
/// that bumps G(0) once per application, and row i reads G(0) = i in the
/// serial walk. The row-slot form must decline (else every row would read 0).
/// Serial value: sum_i sum_j (G(j) + i) = 522 * (0 + .. + 520) = 70710120.
let private rowSlotOuterWriteSrc =
    "type I = Idx<521>\n"
    + "let mut G: Array<Float64 like I> = method_for(range<I>) <@> lambda(i) -> 0.0 |> compute\n"
    + "let s = reduce(method_for(range<I>) <@> lambda(i) where omp(i: 1) -> reduce(method_for(range<I>) <@> lambda(j) -> G(j) + Float64(i), (+)), lambda(a, b) where comm(a, b) -> {\n"
    + "    G((0 : I)) = G((0 : I)) + 1.0\n"
    + "    a + b\n"
    + "}, 0.0)\n"

/// G51 (round 3, A2): the bench stencil's input-checksum shape -- an outer
/// fold whose row value is a fold over a ROW VIEW `u(i)` of a stored dense
/// rank-2 array. `rows` x 509 cells (521 rows: 265189 inner steps, above the
/// row-slot gate); `inner` is the row's fold, `outer` the outer fold kernel.
let private rowViewSrcRC (rows: int) (cols: int) (inner: string) (outer: string) =
    $"type Y = Idx<{rows}>\ntype X = Idx<{cols}>\n"
    + "let u = method_for(range<Y, X>) <@> lambda(i, j) -> Float64((i * 7 + j * 3) % 11) * 0.25 - 1.0 |> compute\n"
    + $"let s = reduce(method_for(range<Y>) <@> lambda(i) -> {inner}, {outer}, 0.5)\n"
let private rowViewSrc (rows: int) (inner: string) (outer: string) = rowViewSrcRC rows 509 inner outer

/// ...with the thread request on the MAP's outer level (G27's placement) and a
/// builtin `(+)` outer fold.
let private rowViewMapSrc (inner: string) =
    "type Y = Idx<521>\ntype X = Idx<509>\n"
    + "let u = method_for(range<Y, X>) <@> lambda(i, j) -> Float64((i * 7 + j * 3) % 11) * 0.25 - 1.0 |> compute\n"
    + $"let s = reduce(method_for(range<Y>) <@> lambda(i) where omp(i: 1) -> {inner}, (+), 0.5)\n"

/// The five-party audit's writing parties (loops/535, 536): the inner fold's
/// kernel writes a captured counter; or writes the BASE through a view.
let private rowViewWritingInnerSrc =
    "type Y = Idx<521>\ntype X = Idx<509>\ntype K = Idx<2>\n"
    + "let mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n"
    + "let u = method_for(range<Y, X>) <@> lambda(i, j) -> Float64((i * 7 + j * 3) % 11) * 0.25 - 1.0 |> compute\n"
    + "let s = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), lambda(a, b) -> {\n"
    + "    G((0 : K)) = G((0 : K)) + 1.0\n"
    + "    a + b\n"
    + "}), lambda(p, q) where omp -> p + q, 0.0)\n"
let private rowViewBaseWriterSrc =
    "type Y = Idx<521>\ntype X = Idx<509>\n"
    + "let mut u = method_for(range<Y, X>) <@> lambda(i, j) -> Float64((i * 7 + j * 3) % 11) * 0.25 - 1.0 |> compute\n"
    + "let s = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), lambda(a, b) -> {\n"
    + "    let v = u((1 : Y))\n"
    + "    v((0 : X)) = v((0 : X)) + 1.0\n"
    + "    a + b\n"
    + "}), lambda(p, q) where omp -> p + q, 0.0)\n"

/// The decision record and the C++ text of one lower + generate.
let private planAndCppOf (testName: string) (src: string) : Result<Blade.Effects.Decision list * string, string> =
    Blade.Effects.Decisions.start ()
    let r =
        try
            match fst (lowerCaptured src) with
            | Error e -> Error ($"lower: {e}")
            | Ok ir ->
                let cpp = fst (CodeGen.genSelfContainedProgramFromIR ir testName)
                Ok (Blade.Effects.Decisions.drain (), cpp)
        with ex -> Error ($"codegen raised: {ex.Message}")
    Blade.Effects.Decisions.drain () |> ignore
    r

/// (name, env pins, source, record predicate + its description, C++ must
/// contain, C++ must not contain).
let private rowSlotCases : (string * (string * string) list * string * (Blade.Effects.Decision -> bool) * string * string list * string list) list =
    let repro = [ ("BLADE_FP_REPRO", "1") ]
    // Since round 3 (W2-0a) the ordered orbit scatter takes precedence over
    // the row slots wherever BOTH admit a walk -- every rowSlotSym2 walk with a
    // scatter-admissible body is one. The cases that pin the ROW-SLOT
    // emission over such a walk ask for the round-2 order explicitly.
    let reproPrefer = repro @ [ ("BLADE_FOLD_ROW_SLOTS", "prefer") ]
    // The yielded row-slot record (both forms admitted, the scatter taken).
    let yieldedToScatter (d: Blade.Effects.Decision) =
        d.Rule = "fold-form"
        && (match d.Outcome with
            | Blade.Effects.Declined why -> why = "row-slot form: " + Blade.OrbitFold.rowSlotsYieldedToScatter
            | _ -> false)
        && d.Evidence |> List.exists (fun e -> e = "fold form: ordered orbit scatter (repro; the threaded complete-row form admitted but yielded: measured slower, round 3)")
        && d.Evidence |> List.exists (fun e -> e.StartsWith "both forms admitted this walk: the row-slot admission's five-party audit passed")
    let yieldedCensus = "the ordered orbit scatter runs serially (its chains thread only as a wavefront; the threaded complete-row form admitted but yielded to the scatter: measured slower under mt8 repro, round 3 (BLADE_FOLD_ROW_SLOTS=prefer spends the request on the row-slot team); requested at: level 1's fold)"
    let applied (d: Blade.Effects.Decision) =
        d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
        && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded complete rows (repro): ")
    let declined (needle: string) (d: Blade.Effects.Decision) =
        d.Rule = "fold-form"
        && (match d.Outcome with
            | Blade.Effects.Declined why -> why.StartsWith "row-slot form: " && why.Contains needle
            | _ -> false)
    let reproCensus = "[omp] requested but emitted serial: BLADE_FP_REPRO=1"
    // G51: the row slots taken over ROW-VIEW folds -- the row-view record and
    // the five-party matrix ride the applied fold-form record.
    let rowViewApplied (d: Blade.Effects.Decision) =
        applied d
        && d.Evidence |> List.exists (fun e -> e.StartsWith "row-view folds (G51): each row's value is reduce over a ROW VIEW")
        && d.Evidence |> List.exists (fun e ->
               e.StartsWith "five-party audit (shared transitive write analysis + the IRTotality closed whitelist): producer = "
               && e.Contains "consumer = " && e.Contains "outer kernel = " && e.Contains "seeds = " && e.Contains "intervening = none (no cross-binding motion")
    // ...and the licensed arm: Path B, blade_fold::sum per row, the knob named.
    let rowViewLicensed (d: Blade.Effects.Decision) =
        d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
        && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded(Path B chunks, blade_fold::sum<double, 8> per row: licensed row-view folds")
        && d.Evidence |> List.exists (fun e ->
               e.StartsWith "row-view folds (G51, licensed arm, BLADE_FOLD_LANES)"
               && e.Contains "0/off keeps the previous chunk form" && e.Contains "the row loop's omp simd dropped")
        && d.Evidence |> List.exists (fun e -> e.StartsWith "five-party audit")
    // A writing party: no chunk plan at all (the map body writes), the
    // fold-form record names it.
    let rowViewWriterDeclined (d: Blade.Effects.Decision) =
        d.Rule = "fold-form"
        && (match d.Outcome with
            | Blade.Effects.Declined why -> why.Contains "the map kernel writes a captured binding"
            | _ -> false)
    // `axes = 2`: the full fold over the two-level nest (the default would
    // fold only the innermost axis and bind a rank-1 array).
    let multiLevel =
        (rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)")
            .Replace("let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j) * g(i, j), (+)), ",
                     "let s = reduce(method_for(range<Var>, range<Var>) <@> lambda(i, j) -> g(i, j) * g(i, j), ")
            .Replace(", 0.25)\n", ", 0.25, axes = 2)\n")
    [ // ABOVE the gate: 521 rows x 521 = 271441 inner steps >= 262144.
      // (prefer: the scatter admits this walk and would take it by default.)
      ("row_slots_threaded_repro", reproPrefer,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       applied, "fold-form applied: threaded complete rows (repro)",
       [ "__rslot_s[__i0] = "; "blade_fold::chunk_lo(__rn_s, (size_t)__rt, (size_t)__rnt)"
         "for (size_t __ri = 0; __ri < __rn_s; __ri++) s = "; "delete[] __rslot_s;" ],
       [ reproCensus; "__rpart_s"; "__racc_s" ])
      // The rank-3 walk (comoment3's shape): 67 rows x 67 x 67 = 300763.
      ("row_slots_threaded_repro_rank3", reproPrefer,
       "type I = Idx<67>\ntype Obs = Idx<3>\n"
       + "let x = method_for(range<I>, range<Obs>) <@> lambda(i, s) -> Float64((i * 5 + s) % 7) - 3.0 |> compute\n"
       + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
       + $"let s = reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> reduce(method_for(range<I>) <@> lambda(k) -> t(i, j, k) * t(i, j, k), (+)), (+)), {rowSlotOmpOuter}, 0.0)\n",
       applied, "fold-form applied (rank 3)",
       [ "__rslot_s[__i0] = " ], [ reproCensus ])
      // BLADE_ORBIT_FOLD=0 keeps the LOGICAL WALK; under repro the walk's rows
      // may still be threaded, and the record says whose rows they are.
      ("row_slots_orbit_fold_off_threads_the_walk", reproPrefer @ [ ("BLADE_ORBIT_FOLD", "0") ],
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "orbit-class fold not taken: disabled by BLADE_ORBIT_FOLD=0")),
       "fold-form applied, naming the orbit fold's knob decline",
       [ "__rslot_s[__i0] = " ], [ reproCensus ])
      // Threads off: the serial walk and the knob's census line.
      ("row_slots_threads_suppressed", repro @ [ ("BLADE_OMP_THREADS", "1") ],
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       declined "BLADE_OMP_THREADS=1", "row-slot form declined: BLADE_OMP_THREADS=1",
       [ "[omp] requested but emitted serial: BLADE_OMP_THREADS=1" ],
       [ "__rslot"; "BLADE_OMP_PARALLEL_NUM_THREADS" ])
      // Below the work gate: 43 x 43 = 1849 inner steps.
      ("row_slots_declined_light_rows", repro,
       rowSlotSym2 43 rowSlotOmpOuter "g(i, j) * g(i, j)",
       declined "below rowslots_min = 262144", "row-slot form declined below the gate",
       [ reproCensus ], [ "__rslot" ])
      // ...and the gate is the provisional TUNE key: pinned low, it admits.
      ("row_slots_gate_is_tunable", reproPrefer @ [ ("BLADE_TILE_TUNE", "rowslots_min=1000") ],
       rowSlotSym2 43 rowSlotOmpOuter "g(i, j) * g(i, j)",
       applied, "fold-form applied with rowslots_min=1000",
       [ "__rslot_s[__i0] = " ], [ reproCensus ])
      // A body that can panic (integer `/`, BL8013): the first-arriving
      // worker would report a racy coordinate, so the form refuses it.
      ("row_slots_declined_panicking_body", repro,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * Float64((j + 1) / 3)",
       declined "body may panic (integer /)", "row-slot form declined: integer /",
       [ reproCensus ], [ "__rslot" ])
      ("row_slots_declined_float_to_int_cast", repro,
       rowSlotSym2 521 rowSlotOmpOuter "Float64(Int64(floor(g(i, j))))",
       declined "body may panic (float -> int cast)", "row-slot form declined: float -> int cast",
       [ reproCensus ], [ "__rslot" ])
      // A multi-level nest continues ONE chain across its rows: no slots.
      ("row_slots_declined_multilevel", repro,
       multiLevel,
       declined "not one complete fold per outer index", "row-slot form declined: multi-level nest",
       [ reproCensus ], [ "__rslot" ])
      // The rest of the closed whitelist's refusals, one per abort family.
      ("row_slots_declined_modulo", repro,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * Float64((j + 1) % 3)",
       declined "body may panic (%)", "row-slot form declined: %",
       [ reproCensus ], [ "__rslot" ])
      ("row_slots_declined_negative_integer_power", repro,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * Float64((j + 2) ^ (j - 3))",
       declined "body may panic (integer ^ with a possibly negative exponent)", "row-slot form declined: integer ^",
       [ reproCensus ], [ "__rslot" ])
      ("row_slots_declined_lgamma", repro,
       rowSlotSym2 521 rowSlotOmpOuter "lgamma(g(i, j) + 100.0)",
       declined "body may panic (lgamma)", "row-slot form declined: lgamma",
       [ reproCensus ], [ "__rslot" ])
      ("row_slots_declined_digamma", repro,
       rowSlotSym2 521 rowSlotOmpOuter "digamma(g(i, j) + 100.0)",
       declined "body may panic (digamma)", "row-slot form declined: digamma",
       [ reproCensus ], [ "__rslot" ])
      ("row_slots_declined_unproven_subscript", repro,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, (520 - j : Var))",
       declined "subscript not proven in range", "row-slot form declined: unproven subscript",
       [ reproCensus ], [ "__rslot" ])
      // Float `/` is IEEE-total: admitted (integer `/` is the refusal above).
      ("row_slots_float_division_admitted", reproPrefer,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) / (1.0 + abs(g(i, j)))",
       applied, "fold-form applied with a float `/` body",
       [ "__rslot_s[__i0] = " ], [ reproCensus ])
      // THE OUTER FOLD KERNEL is audited too (Codex diff review, blocker 1):
      // a `comm`-declared serial kernel may write a captured array the rows
      // read (the front end bans writes only in parallel bodies); moving every
      // row before every kernel application would change the operands. Since
      // gap G41 a WRITING fold kernel is not reorder-licensed at all, so the
      // fold never reaches the row-slot admission: no chunk plan, the serial
      // chain, and the census names the write.
      ("row_slots_declined_outer_kernel_write", repro,
       rowSlotOuterWriteSrc,
       (fun d -> d.Rule = "fold-form"
                 && (match d.Outcome with
                     | Blade.Effects.Declined why -> why.Contains "the fold kernel writes a captured binding"
                     | _ -> false)),
       "fold-form declined: the fold kernel writes (not reorder-licensed; G41)",
       [ "[omp] requested but emitted serial: the fold kernel writes a captured binding; a map licence does not make the fold race-safe" ],
       [ "__rslot"; "BLADE_OMP_PARALLEL_NUM_THREADS"; "blade_scatter::" ])
      // THE PRECEDENCE (round 3, W2-0a; the wave-1 harness A/B measured the
      // scatter faster under mt8 repro): this walk is one BOTH forms admit;
      // the ordered orbit scatter is taken, the row-slot form records that it
      // was admitted and yielded, and the census says why the omp request
      // was not spent on a team.
      ("orbit_scatter_precedes_row_slots", repro,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       yieldedToScatter,
       "the row-slot form admitted but yielded to the ordered orbit scatter (record names the scatter form and the audit)",
       [ "blade_scatter::scatter2<4, 64, true>("; yieldedCensus; reproCensus ], [ "__rslot" ])
      ("orbit_scatter_precedes_row_slots_scatter_record", repro,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: ordered orbit scatter (rank 2, extent 521, row groups of 4)")),
       "fold-form applied: the ordered orbit scatter over the walk the row slots also admit",
       [ "blade_scatter::scatter2<" ], [ "__rslot"; "BLADE_OMP_PARALLEL_NUM_THREADS" ])
      // Rank 3 (comoment3's shape): the same precedence.
      ("orbit_scatter_precedes_row_slots_rank3", repro,
       "type I = Idx<67>\ntype Obs = Idx<3>\n"
       + "let x = method_for(range<I>, range<Obs>) <@> lambda(i, s) -> Float64((i * 5 + s) % 7) - 3.0 |> compute\n"
       + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
       + $"let s = reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> reduce(method_for(range<I>) <@> lambda(k) -> t(i, j, k) * t(i, j, k), (+)), (+)), {rowSlotOmpOuter}, 0.0)\n",
       yieldedToScatter, "rank 3: the row-slot form admitted but yielded to the scatter",
       [ "blade_scatter::scatter3<"; yieldedCensus; reproCensus ], [ "__rslot" ])
      // BLADE_FOLD_ROW_SLOTS=prefer restores the round-2 order: the taken
      // row-slot form wins under the omp request, and the scatter records
      // that it yielded (the old record and the old emission).
      ("row_slots_prefer_knob", reproPrefer,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       (fun d -> d.Rule = "fold-form"
                 && (match d.Outcome with
                     | Blade.Effects.Declined why ->
                         why.StartsWith "ordered orbit scatter not taken: the threaded complete-row form (G35) is taken under the omp request"
                         && why.Contains "BLADE_FOLD_ROW_SLOTS=prefer"
                     | _ -> false)
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: ordered orbit scatter (rank 2, extent 521")),
       "prefer: the ordered orbit scatter yields to the taken row-slot form (record names its form and the knob)",
       [ "__rslot_s[__i0] = "; "blade_fold::chunk_lo(__rn_s, (size_t)__rt, (size_t)__rnt)" ], [ "blade_scatter::" ])
      ("row_slots_prefer_knob_rows_applied", reproPrefer,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       applied, "prefer: fold-form applied, threaded complete rows",
       [ "__rslot_s[__i0] = " ], [ "blade_scatter::"; "admitted but yielded" ])
      // The row slots stay the DEFAULT form for a walk the scatter does not
      // admit: here the cell function uses a level coordinate.
      ("row_slots_default_where_scatter_declines", repro,
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * Float64(j + 1)",
       applied, "fold-form applied by default: threaded complete rows (the scatter declines a coordinate-dependent term)",
       [ "__rslot_s[__i0] = " ], [ "blade_scatter::"; "admitted but yielded" ])
      // ...and with threads off (or below the gate) the same walk is the
      // serial ordered orbit scatter, the census naming why the request was
      // not spent.
      ("orbit_scatter_when_threads_suppressed", repro @ [ ("BLADE_OMP_THREADS", "1") ],
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: ordered orbit scatter (rank 2, extent 521, row groups of 4)")),
       "fold-form applied: ordered orbit scatter (threads off)",
       [ "blade_scatter::scatter2<4, 64, true>("; "[omp] requested but emitted serial: BLADE_OMP_THREADS=1" ], [ "__rslot" ])
      ("orbit_scatter_below_row_slot_gate", repro,
       rowSlotSym2 43 rowSlotOmpOuter "g(i, j) * g(i, j)",
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: ordered orbit scatter (rank 2, extent 43")),
       "fold-form applied: ordered orbit scatter (G35 below its gate)",
       [ "blade_scatter::scatter2<"; "the ordered orbit scatter runs serially"; reproCensus ], [ "__rslot" ])
      // EVERY level's thread request reaches the scatter's census (Codex diff
      // review of W2-A, finding 4): an INNER fold's `where omp`, and mixed
      // placements, threads on and off -- no licence moves between levels.
      ("orbit_scatter_inner_fold_request", repro,
       (rowSlotSym2 43 "(+)" "g(i, j) * g(i, j)").Replace("g(i, j) * g(i, j), (+))", "g(i, j) * g(i, j), lambda(a, b) where omp -> a + b, 0.0)"),
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: ordered orbit scatter (rank 2, extent 43")),
       "fold-form applied: ordered orbit scatter (inner-fold request only)",
       [ "blade_scatter::scatter2<"; reproCensus; "requested at: level 2's fold)" ], [ "__rslot" ])
      ("orbit_scatter_inner_fold_request_threads_off", repro @ [ ("BLADE_OMP_THREADS", "1") ],
       (rowSlotSym2 43 "(+)" "g(i, j) * g(i, j)").Replace("g(i, j) * g(i, j), (+))", "g(i, j) * g(i, j), lambda(a, b) where omp -> a + b, 0.0)"),
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: ordered orbit scatter (rank 2, extent 43")),
       "fold-form applied: ordered orbit scatter (inner-fold request, threads off)",
       [ "[omp] requested but emitted serial: BLADE_OMP_THREADS=1"; "(requested at: level 2's fold)" ], [ "__rslot" ])
      ("orbit_scatter_mixed_requests", repro,
       (rowSlotSym2 43 rowSlotOmpOuter "g(i, j) * g(i, j)").Replace("lambda(j) -> g(i, j)", "lambda(j) where omp(j: 1) -> g(i, j)"),
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: ordered orbit scatter (rank 2, extent 43")),
       "fold-form applied: ordered orbit scatter (outer fold + inner map requests)",
       [ "blade_scatter::scatter2<"; reproCensus; "requested at: level 1's fold, level 2's map)" ], [ "__rslot" ])
      ("row_slots_knob_off", repro @ [ ("BLADE_FOLD_ROW_SLOTS", "0") ],
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       declined "disabled by BLADE_FOLD_ROW_SLOTS=0", "row-slot form declined by its knob",
       [ reproCensus ], [ "__rslot" ])
      // The default (licensed) build never takes it: a licensed compact form does --
      // the orbit-class fold, or (since round-2 B3, when the producer is the plain
      // compact nest) the compact row classes taken behind each packed row.
      ("row_slots_not_in_default_build", [],
       rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)",
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e ->
                        e.StartsWith "fold form: orbit-class fold"
                        || e.StartsWith "fold form: compact row classes")),
       "a licensed compact form applied (orbit-class fold or compact row classes; no row slots)",
       [], [ "__rslot" ])
      // ---- G51 (round 3, A2): ROW-VIEW inner folds ----------------------
      // The stencil checksum shape: TAKEN by the repro row slots, with the
      // row-view record and the five-party matrix; every party's refusal
      // declines with its name; the licensed arm folds each row with ONE
      // blade_fold::sum call, its seed combined once, no omp simd on the row
      // loop, gated by BLADE_FOLD_LANES and the inner fold's OWN licence.
      ("row_view_slots_threaded_repro", repro,
       rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter,
       rowViewApplied, "fold-form applied: threaded complete rows over row-view folds (row-view record + five-party matrix)",
       [ "__rslot_s[__i0] = "; "for (size_t __ri = 0; __ri < __rn_s; __ri++) s = "; "delete[] __rslot_s;" ],
       [ reproCensus; "__rpart_s"; "blade_fold::sum<" ])
      ("row_view_slots_seeded_repro", repro,
       rowViewSrc 521 "reduce(u(i), (+), 7.0 + Float64(i))" rowSlotOmpOuter,
       (fun d -> rowViewApplied d && d.Evidence |> List.exists (fun e -> e.Contains "its explicit seed, evaluated once per row in the row's own scope")),
       "fold-form applied over seeded row-view folds (the seed named in the record)",
       [ "__rslot_s[__i0] = " ], [ reproCensus; "blade_fold::sum<" ])
      ("row_view_slots_map_request_repro", repro,
       rowViewMapSrc "reduce(u(i), (+))",
       rowViewApplied, "fold-form applied: the map's omp(i: 1) request (G27 placement) takes the row slots too",
       [ "__rslot_s[__i0] = " ], [ reproCensus ])
      ("row_view_slots_noncommutative_kernel_repro", repro,
       rowViewSrc 521 "reduce(u(i), lambda(a, b) -> 3.0 * a + b, 1.0)" rowSlotOmpOuter,
       rowViewApplied, "fold-form applied with a non-commutative inner kernel (each row's chain is the serial walk's own)",
       [ "__rslot_s[__i0] = " ], [ reproCensus; "blade_fold::sum<" ])
      ("row_view_slots_declined_light_rows", repro,
       rowViewSrc 43 "reduce(u(i), (+))" rowSlotOmpOuter,
       declined "43 row(s) x 509 fold step(s) = 21887 below rowslots_min = 262144", "row-slot form declined below the gate (the row-view work count is the row's cells)",
       [ reproCensus ], [ "__rslot" ])
      ("row_view_slots_threads_suppressed", repro @ [ ("BLADE_OMP_THREADS", "1") ],
       rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter,
       declined "BLADE_OMP_THREADS=1", "row-slot form declined: BLADE_OMP_THREADS=1",
       [ "[omp] requested but emitted serial: BLADE_OMP_THREADS=1" ], [ "__rslot"; "BLADE_OMP_PARALLEL_NUM_THREADS" ])
      ("row_view_slots_knob_off", repro @ [ ("BLADE_FOLD_ROW_SLOTS", "0") ],
       rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter,
       declined "disabled by BLADE_FOLD_ROW_SLOTS=0", "row-slot form declined by its knob",
       [ reproCensus ], [ "__rslot" ])
      ("row_view_slots_declined_unproven_subscript", repro,
       rowViewSrc 521 "reduce(u((520 - i : Y)), (+))" rowSlotOmpOuter,
       declined "a row-view subscript not proven in range: BL8006", "row-slot form declined: a row view by a computed (checked) subscript",
       [ reproCensus ], [ "__rslot" ])
      ("row_view_slots_declined_panicking_seed", repro,
       rowViewSrc 521 "reduce(u(i), (+), Float64((i + 1) / 3))" rowSlotOmpOuter,
       declined "body may panic (integer /)", "row-slot form declined: the inner seed may panic (seed party)",
       [ reproCensus ], [ "__rslot" ])
      ("row_view_slots_declined_panicking_kernel", repro,
       rowViewSrc 521 "reduce(u(i), lambda(a, b) -> a + Float64(Int64(floor(b))))" rowSlotOmpOuter,
       declined "body may panic (float -> int cast)", "row-slot form declined: the inner fold kernel may panic (consumer party)",
       [ reproCensus ], [ "__rslot" ])
      ("row_view_declined_writing_inner_kernel_repro", repro,
       rowViewWritingInnerSrc,
       rowViewWriterDeclined, "fold-form declined: the inner fold's kernel writes (consumer party; no chunk plan)",
       [ "[omp] requested but emitted serial: the map kernel writes a captured binding" ],
       [ "__rslot"; "BLADE_OMP_PARALLEL_NUM_THREADS"; "blade_fold::sum<" ])
      ("row_view_declined_base_writer_through_view_repro", repro,
       rowViewBaseWriterSrc,
       rowViewWriterDeclined, "fold-form declined: the inner fold's kernel writes the base through a view (producer party)",
       [ "[omp] requested but emitted serial: the map kernel writes a captured binding" ],
       [ "__rslot"; "BLADE_OMP_PARALLEL_NUM_THREADS"; "blade_fold::sum<" ])
      ("row_view_licensed_declined_writing_inner_kernel", [],
       rowViewWritingInnerSrc,
       rowViewWriterDeclined, "licensed build: the writing inner kernel keeps the serial walk",
       [ "[omp] requested but emitted serial: the map kernel writes a captured binding" ],
       [ "BLADE_OMP_PARALLEL_NUM_THREADS"; "blade_fold::sum<" ])
      ("row_view_licensed_declined_base_writer_through_view", [],
       rowViewBaseWriterSrc,
       rowViewWriterDeclined, "licensed build: the base writer keeps the serial walk",
       [ "[omp] requested but emitted serial: the map kernel writes a captured binding" ],
       [ "BLADE_OMP_PARALLEL_NUM_THREADS"; "blade_fold::sum<" ])
      // ---- the licensed arm (default build) ----
      ("row_view_licensed_sum_helper_unseeded", [],
       rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter,
       (fun d -> rowViewLicensed d && d.Evidence |> List.exists (fun e -> e.Contains "unseeded: head-peeled, row[0] + sum(row[1..n))")),
       "fold-form applied: Path B, blade_fold::sum per row (head peel), no omp simd on the row loop",
       [ "const double* __rrow = u.data[i];"
         "const double __rv = __rrow[0] + blade_fold::sum<double, 8>((__rrow + 1), (size_t)508);"
         "BLADE_OMP_PARALLEL_NUM_THREADS(__rT)"; "blade_fold::chunk_lo(__rn, (size_t)__rt, (size_t)__rnt)"
         "for (int __rt = 0; __rt < __rT; __rt++) if (__rhad[__rt]) s = " ],
       [ "BLADE_OMP_SIMD_REDUCTION"; "__rslot" ])
      ("row_view_licensed_seed_combined_once", [],
       rowViewSrc 521 "reduce(u(i), (+), 7.0 + Float64(i))" rowSlotOmpOuter,
       (fun d -> rowViewLicensed d && d.Evidence |> List.exists (fun e -> e.Contains "its seed combined once (seed + sum(row))")),
       "fold-form applied: the inner seed combined exactly once per row (Codex review A, finding 4)",
       [ "const double __rv = ((7.0 + static_cast<double>(i))) + blade_fold::sum<double, 8>(__rrow, (size_t)509);" ],
       [ "BLADE_OMP_SIMD_REDUCTION" ])
      ("row_view_licensed_map_request", [],
       rowViewMapSrc "reduce(u(i), (+))",
       rowViewLicensed, "fold-form applied: the licensed arm under the map's omp(i: 1) request",
       [ "blade_fold::sum<double, 8>((__rrow + 1), (size_t)508)" ], [ "BLADE_OMP_SIMD_REDUCTION" ])
      ("row_view_licensed_singleton_rows_no_helper", [],
       rowViewSrcRC 521 1 "reduce(u(i), (+))" rowSlotOmpOuter,
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded(Path B chunks, one-cell rows, no helper call: licensed row-view folds, unseeded single-cell rows: the row's one cell")),
       "fold-form applied: an unseeded one-cell row is its cell (no empty helper call)",
       [ "const double __rv = __rrow[0];" ], [ "blade_fold::sum<"; "BLADE_OMP_SIMD_REDUCTION" ])
      ("row_view_licensed_singleton_rows_seeded", [],
       rowViewSrcRC 521 1 "reduce(u(i), (+), 0.25)" rowSlotOmpOuter,
       rowViewLicensed, "fold-form applied: a seeded one-cell row is seed + sum(row, 1)",
       [ "const double __rv = (0.25) + blade_fold::sum<double, 8>(__rrow, (size_t)1);" ], [])
      ("row_view_licensed_lanes_knob_k4", [ ("BLADE_FOLD_LANES", "4") ],
       rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter,
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded(Path B chunks, blade_fold::sum<double, 4> per row")),
       "fold-form applied with K = 4 under BLADE_FOLD_LANES=4",
       [ "blade_fold::sum<double, 4>((__rrow + 1), (size_t)508)" ], [ "blade_fold::sum<double, 8>" ])
      ("row_view_licensed_lanes_knob_off_keeps_previous_form", [ ("BLADE_FOLD_LANES", "0") ],
       rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter,
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded(Path B chunks, serial within each chunk: BLADE_FOLD_LANES=0/off")),
       "BLADE_FOLD_LANES=0: the previous Path B chunk form, no helper",
       [ "// reduce over computation: comm-licensed parallel fold, outer level chunked" ], [ "blade_fold::sum<" ])
      ("row_view_licensed_noncommutative_kernel_no_helper", [],
       rowViewSrc 521 "reduce(u(i), lambda(a, b) -> 3.0 * a + b, 1.0)" rowSlotOmpOuter,
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded(Path B chunks, simd-reduction within each chunk)")),
       "an unlicensed inner kernel never takes the reassociating helper (the previous Path B form, each row its serial chain)",
       [ "BLADE_OMP_SIMD_REDUCTION" ], [ "blade_fold::sum<" ])
      ("row_view_licensed_repro_inner_kernel_no_helper", [],
       "function radd(a: Float64, b: Float64) where repro -> Float64 = a + b\n" + rowViewSrc 521 "reduce(u(i), radd)" rowSlotOmpOuter,
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded(Path B chunks, simd-reduction within each chunk)")),
       "a `where repro` inner kernel never takes the helper (its chain is the contract)",
       [], [ "blade_fold::sum<" ])
      ("row_view_licensed_threads_suppressed_unchanged", [ ("BLADE_OMP_THREADS", "1") ],
       rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter,
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: simd-reduction")),
       "threads off: the serial licensed form is unchanged (the helper arm is the Path B team's)",
       [ "[omp] requested but emitted serial: BLADE_OMP_THREADS=1" ], [ "blade_fold::sum<"; "BLADE_OMP_PARALLEL_NUM_THREADS" ])
      // ---- admission edges (Codex diff review of W1-A2, findings 1 and 4) ----
      // A row body with a LEADING LET holding the seed: the repro row slots
      // walk the let and take it; the licensed helper takes only the bare
      // `reduce(u(i), +[, s])` body, so it keeps the previous chunk form.
      ("row_view_leading_let_repro_admitted", repro,
       rowViewSrc 521 "{\n    let t = 7.0 + Float64(i)\n    reduce(u(i), (+), t)\n}" rowSlotOmpOuter,
       rowViewApplied, "repro: a leading-let row body is admitted (the walk covers the let)",
       [ "__rslot_s[__i0] = " ], [ reproCensus; "blade_fold::sum<" ])
      ("row_view_leading_let_licensed_helper_declined", [],
       rowViewSrc 521 "{\n    let t = 7.0 + Float64(i)\n    reduce(u(i), (+), t)\n}" rowSlotOmpOuter,
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded(Path B chunks, simd-reduction within each chunk)")),
       "licensed: a leading-let row body declines the helper (the previous Path B chunk form)",
       [ "BLADE_OMP_SIMD_REDUCTION" ], [ "blade_fold::sum<" ])
      // An OUTER SEED that is itself a nonzero pure fold: evaluated once, before
      // the team, in both forms.
      ("row_view_fold_valued_outer_seed_repro", repro,
       (rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter).Replace(", 0.5)\n", ", reduce(method_for(range<X>) <@> lambda(j) -> Float64(j % 5) * 0.25, (+)))\n"),
       rowViewApplied, "repro: a fold-valued outer seed (write-audited, before the team) admits the row slots",
       [ "__rslot_s[__i0] = " ], [ reproCensus ])
      ("row_view_fold_valued_outer_seed_licensed", [],
       (rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter).Replace(", 0.5)\n", ", reduce(method_for(range<X>) <@> lambda(j) -> Float64(j % 5) * 0.25, (+)))\n"),
       rowViewLicensed, "licensed: a fold-valued outer seed keeps the helper arm",
       [ "blade_fold::sum<double, 8>((__rrow + 1), (size_t)508)" ], [])
      // INTEGER elements: the repro row slots take them (integer + is total and
      // wrapping); the helper has no integer form.
      ("row_view_integer_elements_repro", repro,
       "type Y = Idx<521>\ntype X = Idx<509>\n"
       + "let u = method_for(range<Y, X>) <@> lambda(i, j) -> (Int64(i) * 7 + Int64(j) * 3) % 11 - 4 |> compute\n"
       + "let s = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), (+)), lambda(p, q) where omp -> p + q, 0)\n",
       rowViewApplied, "repro: integer row-view folds are admitted",
       [ "__rslot_s[__i0] = " ], [ reproCensus; "blade_fold::sum<" ])
      ("row_view_integer_elements_licensed_no_helper", [],
       "type Y = Idx<521>\ntype X = Idx<509>\n"
       + "let u = method_for(range<Y, X>) <@> lambda(i, j) -> (Int64(i) * 7 + Int64(j) * 3) % 11 - 4 |> compute\n"
       + "let s = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), (+)), lambda(p, q) where omp -> p + q, 0)\n",
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded(Path B chunks, simd-reduction within each chunk)")),
       "licensed: integer elements decline the helper (no integer vector form)",
       [ "BLADE_OMP_SIMD_REDUCTION" ], [ "blade_fold::sum<" ])
      // A genuinely NON-builtin comm-declared inner kernel (`a + 1.0 * b` is
      // not the recognised `a + b` body, so foldKernelBuiltinOp does not grant
      // it): reorder-licensed by its comm, but the helper spells a builtin `+`
      // tree, so it declines; the repro row slots take it (a total kernel).
      ("row_view_comm_declared_inner_kernel_licensed_no_helper", [],
       rowViewSrc 521 "reduce(u(i), lambda(a, b) where comm(a, b) -> a + 1.0 * b)" rowSlotOmpOuter,
       (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                 && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded(Path B chunks, simd-reduction within each chunk)")),
       "licensed: a non-builtin comm-declared inner kernel declines the helper",
       [ "BLADE_OMP_SIMD_REDUCTION" ], [ "blade_fold::sum<" ])
      ("row_view_comm_declared_inner_kernel_repro", repro,
       rowViewSrc 521 "reduce(u(i), lambda(a, b) where comm(a, b) -> a + 1.0 * b)" rowSlotOmpOuter,
       rowViewApplied, "repro: a non-builtin comm-declared inner kernel is admitted (total)",
       [ "__rslot_s[__i0] = " ], [ reproCensus ])
      // THE OUTER KERNEL'S TOTALITY (finding 1): a comm-declared outer kernel
      // that may panic (a float -> int cast, BL8014) gets NO team in the
      // licensed build -- neither the helper arm nor the general Path B chunk
      // form -- with the census line; the repro row slots decline it by name.
      ("row_view_outer_kernel_may_panic_licensed_no_team", [],
       rowViewSrc 521 "reduce(u(i), (+))" "lambda(p, q) where comm(p, q), omp -> Float64(Int64(floor(p + q)))",
       (fun d -> d.Rule = "fold-form"
                 && (match d.Outcome with
                     | Blade.Effects.Declined why -> why.StartsWith "row-view helper arm: the outer fold kernel is outside the closed totality whitelist (body may panic (float -> int cast))"
                     | _ -> false)),
       "licensed: a panicking comm-declared outer kernel is refused the helper arm and any team",
       [ "[omp] requested but emitted serial: the outer fold kernel is outside the closed totality whitelist (body may panic (float -> int cast))" ],
       [ "blade_fold::sum<"; "BLADE_OMP_PARALLEL_NUM_THREADS"; "__rpart" ])
      ("row_view_outer_kernel_may_panic_repro_declined", repro,
       rowViewSrc 521 "reduce(u(i), (+))" "lambda(p, q) where comm(p, q), omp -> Float64(Int64(floor(p + q)))",
       declined "the outer fold kernel is outside the row-slot whitelist (body may panic (float -> int cast))",
       "repro: the row slots decline a panicking outer kernel by name",
       [ reproCensus ], [ "__rslot" ])
      // ...and a TOTAL non-builtin comm-declared outer kernel keeps the arm
      // (its combine is the fold wrapper; the licence is its comm).
      ("row_view_outer_kernel_total_comm_licensed_helper", [],
       rowViewSrc 521 "reduce(u(i), (+))" "lambda(p, q) where comm(p, q), omp -> p + 1.0 * q",
       rowViewLicensed, "licensed: a total comm-declared outer kernel keeps the helper arm",
       [ "blade_fold::sum<double, 8>((__rrow + 1), (size_t)508)"; "BLADE_OMP_PARALLEL_NUM_THREADS(__rT)" ], [ "BLADE_OMP_SIMD_REDUCTION" ]) ]

/// Assert that `omp` reaches codegen as a pragma for every spelling of a
/// kernel, and reaches it for NO spelling that omitted the clause.
let runOmpPragmaTests () : Blade.Tests.TestHarness.BlockResult =
    printHeader "OpenMP Pragma Emission"
    // Everything in this block asserts the DEFAULT emission, so the serial
    // build knob is pinned unset for it -- see `pinOmpThreadsUnset`. The
    // knob's own arms at the bottom re-pin it ON for their own scope.
    use _ompThreadsPin = pinOmpThreadsUnset ()
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [name]
        resultLine Fail name detail
    for (name, src, expectPragma) in ompPragmaCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            let hasPragma = (ompConstructView cpp).Contains "#pragma omp"
            // A dropped clause is exactly the silent case: no pragma AND no
            // marker. Assert the marker's absence too, so a future change that
            // "fixes" a case by suppressing it loudly is still caught here.
            let hasMarker = cpp.Contains "[omp] requested but emitted serial"
            if hasPragma <> expectPragma then
                fail name
                    (sprintf "expected %s, got %s%s"
                        (if expectPragma then "a pragma" else "no pragma")
                        (if hasPragma then "a pragma" else "none")
                        (if hasMarker then " (nest reports omp requested but suppressed)" else ""))
            elif hasMarker then
                fail name "unexpected omp-suppressed marker in generated code"
            else
                passed <- passed + 1
                resultLine Pass name
                    (if expectPragma then "pragma emitted" else "serial (no clause)")
    // ---- depth-as-licence: the exact pragma, not just its presence ----
    for (name, src, expectedPragma) in ompDepthCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            // Compare the whole pragma line: `collapse(2)` vs plain is exactly
            // the distinction the licence controls, and `Contains` on the plain
            // form would match the collapse form as a prefix.
            let pragmaLines =
                (ompConstructView cpp).Split('\n')
                |> Array.map (_.Trim())
                |> Array.filter (fun l -> l.StartsWith "#pragma omp")
                |> Array.toList
            match pragmaLines with
            | [actual] when actual = expectedPragma ->
                passed <- passed + 1
                resultLine Pass name actual
            | [actual] -> fail name ($"expected `{expectedPragma}`, got `{actual}`")
            | [] -> fail name ($"expected `{expectedPragma}`, got no pragma")
            | many -> fail name ($"""expected one pragma, got {many.Length}: {(String.concat " | " many)}""")
    // ---- placement: WHICH loop the pragma governs ----
    for (name, src, expectedIdx) in ompPlacementCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            // The emitter writes the pragma and the loop header it governs as
            // consecutive lines, so the next non-blank line after the pragma is
            // that header.
            let lines = (ompConstructView cpp).Split('\n') |> Array.map (_.Trim())
            let governed =
                lines
                |> Array.tryFindIndex (fun l -> l.StartsWith "#pragma omp")
                |> Option.bind (fun i ->
                    lines
                    |> Array.skip (i + 1)
                    |> Array.tryFind (fun l -> l <> "")
                    |> Option.map (fun header ->
                        // "for (size_t __iN = ..." -> "__iN"
                        header.Split([|' '; '('; ')'; '='|])
                        |> Array.tryFind (fun t -> t.StartsWith "__i")
                        |> Option.defaultValue header))
            match governed with
            | Some idx when idx = expectedIdx ->
                passed <- passed + 1
                resultLine Pass name ($"pragma governs {idx}")
            | Some idx -> fail name ($"pragma governs {idx}, expected {expectedIdx}")
            | None -> fail name "no pragma found"
    // ---- the RAGGED / GROUPED PEEL row loop ----
    // Count, exact string, AND placement in one assertion, because for this site
    // each is separately falsifiable: the peel could emit the right pragma on the
    // wrong loop (`group_keys` builds its own `__g` loop earlier in the program),
    // or the right count with the wrong schedule.
    //
    // Placement is checked as "the next non-blank line AFTER the pragma", reusing
    // the ompPlacementCases rule rather than searching for `for (size_t __g`
    // directly -- a direct search would find the group_keys build loop and pass
    // for the wrong reason.
    for (name, src, expectedPragma) in peelPragmaCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            let lines = (ompConstructView cpp).Split('\n') |> Array.map (_.Trim())
            let pragmaLines = lines |> Array.filter (fun l -> l.StartsWith "#pragma omp") |> Array.toList
            let governedIsRowLoop () =
                lines
                |> Array.tryFindIndex (fun l -> l.StartsWith "#pragma omp")
                |> Option.bind (fun i -> lines |> Array.skip (i + 1) |> Array.tryFind (fun l -> l <> ""))
                |> Option.map (fun header -> header.StartsWith "for (size_t __g = 0;")
                |> Option.defaultValue false
            match expectedPragma, pragmaLines with
            | None, [] ->
                // Parallelism is opt-in, so no pragma is only half the claim: the
                // decline marker must be absent too, or "never asked" would be
                // indistinguishable from "asked and was refused".
                if cpp.Contains "[omp]" then
                    fail name "expected no pragma and no [omp] marker, but a marker was emitted"
                else
                    passed <- passed + 1
                    resultLine Pass name "serial, and silent (no clause)"
            | None, many ->
                fail name ($"""expected no pragma, got {many.Length}: {(String.concat " | " many)}""")
            | Some expected, [actual] when actual = expected ->
                if not (governedIsRowLoop ()) then
                    fail name "pragma emitted but does not immediately precede the peel's `__g` row loop"
                elif cpp.Contains "[omp] requested but emitted serial" then
                    fail name "pragma emitted AND a requested-but-serial marker -- the two are contradictory"
                else
                    passed <- passed + 1
                    resultLine Pass name ($"{actual} on __g")
            | Some expected, [actual] -> fail name ($"expected `{expected}`, got `{actual}`")
            | Some expected, [] -> fail name ($"expected `{expected}`, got no pragma")
            | Some expected, many ->
                fail name ($"""expected one `{expected}`, got {many.Length}: {(String.concat " | " many)}""")
    // ---- the peel's licence DECLINE is audible ----
    (let (name, src) = peelDeclineMarkerCase
     match cppOf name src with
     | Error e -> fail name e
     | Ok cpp ->
         let expected = "// [omp] requested but emitted serial: the omp(...) licence covers no level of the peeled row loop"
         if cpp.Contains expected then
             passed <- passed + 1
             resultLine Pass name "decline reported in the emitted C++"
         else
             fail name "omp(g: 0) declined SILENTLY -- no licence-decline marker in the emitted C++")
    // ---- emission SHAPE: native gram/matmul arms, ivdep, loop direction ----
    // Compared against the source with every line's LEADING INDENT stripped:
    // these assertions are about which statements are emitted and in what
    // order, never about how deep the emitter happened to indent them, and a
    // multi-line pattern would otherwise have to restate the nesting depth of
    // whatever construct the form was materialized inside.
    for (name, src, mustContain, mustNotContain) in emissionShapeCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            let flat =
                cpp.Split('\n') |> Array.map (_.TrimStart()) |> String.concat "\n"
            let missing = mustContain |> List.filter (fun s -> not (hasOmpPin flat s))
            let present = mustNotContain |> List.filter (hasOmpPin flat)
            if not missing.IsEmpty then
                fail name ($"""generated C++ lacks: {(String.concat " | " (missing |> List.map (_.Replace("\n", " \\n "))))}""")
            elif not present.IsEmpty then
                fail name ($"""generated C++ still contains: {(String.concat " | " (present |> List.map (_.Replace("\n", " \\n "))))}""")
            else
                passed <- passed + 1
                resultLine Pass name "emission shape as expected"
    // ---- comm-licensed parallel REDUCTIONS: which emission path fired ----
    for (name, src, mustContain, mustNotContain) in ompReduceCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            let missing = mustContain |> List.filter (fun s -> not (hasOmpPin cpp s))
            let present = mustNotContain |> List.filter (hasOmpPin cpp)
            // A licensed fold calls the OpenMP runtime API, which needs the
            // header — `#pragma omp` alone does not. Asserted as "included, not
            // commented out", the shape genIncludes can regress to.
            let needsHeader = mustContain |> List.exists (fun s -> s.Contains "omp_get_")
            let headerOk =
                not needsHeader
                || (cpp.Split('\n')
                    |> Array.exists (fun l -> l.Trim().StartsWith "#include <omp.h>"))
            if not missing.IsEmpty then
                fail name ($"""generated C++ lacks: {(String.concat " | " missing)}""")
            elif not present.IsEmpty then
                fail name ($"""generated C++ unexpectedly contains: {(String.concat " | " present)}""")
            elif not headerOk then
                fail name "calls the omp_* runtime API but <omp.h> is not included"
            // Every construct goes through a blade_portability.hpp macro and
            // every runtime query through its `blade_omp_get_*` stub-able
            // wrapper: a raw `#pragma omp` line or bare `omp_get_*` call is
            // an unknown pragma and an unlinkable symbol in a build without
            // OpenMP (the Path-B team used to be both).
            elif cpp.Contains "#pragma omp"
                 || System.Text.RegularExpressions.Regex.IsMatch(cpp, @"(?<![A-Za-z_])omp_get_\w+\(") then
                fail name "emits a raw `#pragma omp` line or a bare omp_get_* call (use the BLADE_OMP_* macros / blade_omp_get_* wrappers)"
            else
                passed <- passed + 1
                resultLine Pass name "emission path as expected"
    // ---- the unlicensed case is a hard error, not a silent serial fold ----
    for (name, src) in ompReduceDiagCases do
        // lowerDiag, not lower: the CODE is the assertion, and the plain
        // string channel renders the message without it.
        match fst (Lowering.lowerDiag None src) with
        | Ok _ -> fail name "compiled cleanly; expected BL4016"
        | Error diags ->
            let codes = diags |> List.map (fun (d: Blade.Diagnostics.Diagnostic) -> d.Code)
            if List.contains "BL4016" codes then
                passed <- passed + 1
                resultLine Pass name "BL4016"
            else fail name ($"""expected BL4016, got: {(String.concat ", " codes)}""")
    // ---- BLADE_OMP_THREADS=1: what each formerly-threaded site emits ----
    // The pin is scoped to these arms and restored immediately, so nothing
    // above (or in any later block) can see it. Same discipline as the
    // BLADE_FP_REASSOC / BLADE_BLAS pins in the omp-reduce block.
    for (name, src, mustContain, mustNotContain) in ompThreadsKnobCases do
        let emitted =
            use _knob = pinEnv "BLADE_OMP_THREADS" "1"
            cppOf name src
        match emitted with
        | Error e -> fail name e
        | Ok cpp ->
            let flat =
                cpp.Split('\n') |> Array.map (_.TrimStart()) |> String.concat "\n"
            let missing = mustContain |> List.filter (fun s -> not (hasOmpPin flat s))
            let present = mustNotContain |> List.filter (hasOmpPin flat)
            if not missing.IsEmpty then
                fail name ($"""knob-on C++ lacks: {(String.concat " | " (missing |> List.map (_.Replace("\n", " \\n "))))}""")
            elif not present.IsEmpty then
                fail name ($"""knob-on C++ still contains: {(String.concat " | " present)}""")
            else
                passed <- passed + 1
                resultLine Pass name "serial emission as expected"
    // ---- and the same programs with the knob OFF still get their pragma ----
    // Without this pair the arms above could pass by the SOURCE having lost its
    // licence rather than by the knob suppressing it.
    for (name, src, _, _) in ompThreadsKnobCases do
        let ctlName = name + "_control_knob_off"
        match cppOf ctlName src with
        | Error e -> fail ctlName e
        | Ok cpp ->
            let threaded =
                // The construct view: the Path-B team is spelled
                // BLADE_OMP_PARALLEL_NUM_THREADS(n) since P3-3.
                (ompConstructView cpp).Contains "#pragma omp parallel"
            if not threaded then
                fail ctlName "knob OFF emitted no thread construct either (the source lost its licence; the knob arm above is vacuous)"
            elif cpp.Contains "BLADE_OMP_THREADS" then
                fail ctlName "knob OFF emitted a suppression marker"
            else
                passed <- passed + 1
                resultLine Pass ctlName "threaded with the knob off"
    // ---- knob-on emission == the SAME PROGRAM WITHOUT THE CLAUSE ----
    for (name, licensedSrc, unlicensedSrc) in ompThreadsEquivalenceCases do
        // The same testName for both, so the generated banner/timing lines
        // (which embed it) cannot be what differs.
        let licensed =
            use _knob = pinEnv "BLADE_OMP_THREADS" "1"
            cppOf name licensedSrc
        match licensed, cppOf name unlicensedSrc with
        | Error e, _ -> fail name ($"knob-on emit: {e}")
        | _, Error e -> fail name ($"unlicensed emit: {e}")
        | Ok onCpp, Ok unlicCpp ->
            // The census markers are the ONE intended difference: the licensed
            // program has a dropped clause to report and the unlicensed one has
            // nothing to say.
            let dropMarkers (s: string) =
                s.Split('\n') |> Array.filter (fun l -> not (l.Contains "[omp]")) |> String.concat "\n"
            if dropMarkers onCpp <> dropMarkers unlicCpp then
                fail name "knob-on emission differs from the same program without the omp clause"
            elif not (onCpp.Contains "[omp] requested but emitted serial: BLADE_OMP_THREADS=1") then
                fail name "knob-on emission carries no census marker (a dropped clause must not be silent)"
            else
                passed <- passed + 1
                resultLine Pass name "byte-identical to the unlicensed program (modulo the census marker)"
    // ---- G35: threaded complete-row folds (repro), record + emission ----
    // Each case pins its own knobs; every other knob the case reads is pinned
    // UNSET so an ambient setting cannot make a case vacuous.
    for (name, pins, src, want, describe, mustContain, mustNotContain) in rowSlotCases do
        let knobs = [ "BLADE_FP_REPRO"; "BLADE_FP_REASSOC"; "BLADE_ORBIT_FOLD"; "BLADE_TILE_TUNE"; "BLADE_FOLD_ROW_SLOTS"; "BLADE_FOLD_LANES" ]
        let guards =
            (knobs |> List.map (fun k -> pinEnv k null))
            @ (pins |> List.map (fun (k, v) -> pinEnv k v))
        let result =
            try planAndCppOf name src
            finally
                for g in List.rev guards do g.Dispose()
        match result with
        | Error e -> fail name e
        | Ok (ds, cpp) ->
            let missing = mustContain |> List.filter (fun s -> not (cpp.Contains s))
            let present = mustNotContain |> List.filter cpp.Contains
            if not (ds |> List.exists want) then
                let seen = ds |> List.filter (fun d -> d.Rule = "fold-form" || d.Rule = "omp") |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                fail name ($"wanted {describe}; the record: {seen}")
            elif not missing.IsEmpty then
                fail name ($"""generated C++ lacks: {(String.concat " | " missing)}""")
            elif not present.IsEmpty then
                fail name ($"""generated C++ still contains: {(String.concat " | " present)}""")
            // An applied row-slot form opens a team: its `omp` record must be
            // the applied construct, not a census decline.
            elif ds |> List.exists (fun d -> d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                                             && d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: threaded complete rows"))
                 && not (ds |> List.exists (fun d -> d.Rule = "omp" && d.Outcome = Blade.Effects.Applied
                                                     && d.Evidence |> List.exists (fun e -> e.Contains "serial ascending outer chain"))) then
                fail name "fold-form says threaded complete rows, but no applied `omp` record for the team"
            else
                passed <- passed + 1
                resultLine Pass name describe
    // ---- the whitelist is CLOSED at the IR level: names and constructors
    // the front end never produces today must decline, not default-accept.
    let lit = IRLit (IRLitFloat 1.5)
    let irCases : (string * IRExpr * string option) list =
        // (name, expression, Some refusal needle | None = admitted)
        [ ("row_slots_ir_unknown_unary_name", IRUnaryOp (IRMath "erf", lit), Some "`erf` outside the row-slot whitelist")
          ("row_slots_ir_unknown_binary_name", IRBinOp (IRElementwise, IRMath2 "hypot", lit, lit, SrcLoc.Nowhere), Some "`hypot` outside the row-slot whitelist")
          ("row_slots_ir_complex_accessor", IRUnaryOp (IRReal, lit), Some "`real` outside the row-slot whitelist")
          ("row_slots_ir_lgamma", IRUnaryOp (IRMath "lgamma", lit), Some "body may panic (lgamma)")
          ("row_slots_ir_integer_division", IRBinOp (IRElementwise, IRDiv, IRLit (IRLitInt 7L), IRLit (IRLitInt 2L), SrcLoc.Nowhere), Some "body may panic (integer /)")
          ("row_slots_ir_total_unary_admitted", IRUnaryOp (IRMath "sqrt", lit), None)
          ("row_slots_ir_atan2_admitted", IRBinOp (IRElementwise, IRMath2 "atan2", lit, lit, SrcLoc.Nowhere), None)
          ("row_slots_ir_float_division_admitted", IRBinOp (IRElementwise, IRDiv, lit, lit, SrcLoc.Nowhere), None) ]
    for (name, e, expect) in irCases do
        let got =
            try Blade.CodeGenBinding.rowSlotWalk Map.empty Map.empty Map.empty Set.empty Set.empty Set.empty e
            with ex -> Error ($"raised: {ex.Message}")
        match expect, got with
        | None, Ok _ -> passed <- passed + 1; resultLine Pass name "admitted"
        | Some needle, Error why when why.Contains needle -> passed <- passed + 1; resultLine Pass name why
        | None, Error why -> fail name ($"expected admitted, got: {why}")
        | Some needle, Error why -> fail name ($"expected a refusal naming `{needle}`, got: {why}")
        | Some needle, Ok _ -> fail name ($"expected a refusal naming `{needle}`, but it was admitted")
    // ---- G7 / G35: the subscript proof is INDEX IDENTITY, not extent. The
    // front end refuses `y(i)` with `i: Nat<Ix>` into `y` over `Jx` (BL4003),
    // so the audit boundary is pinned on hand-built IR: a coordinate bound to
    // the level `Ix` subscripting an array over the SAME-EXTENT `Jx` must be
    // refused by the shared whitelist (Blade.IRTotality, the predicate both
    // the emitter and the sibling-fold pass apply); bound to `Jx` it is proven.
    (let name = "g7_audit_same_extent_different_index_refused"
     let src =
         "type Ix = Idx<1009>\ntype Jx = Idx<1009>\n"
         + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i)) |> compute\n"
         + "let y = method_for(range<Jx>) <@> lambda(j) -> Float64(Int64(j)) |> compute\n"
     match lower src with
     | Error e -> fail name ($"lower: {e}")
     | Ok ir ->
         let find (n: string) : IRBinding option =
             ir.Modules |> List.collect (fun m -> m.Bindings) |> List.tryFind (fun (b: IRBinding) -> b.Name = n)
         let ixOf (b: IRBinding) =
             match b.Type with
             | ArrayElem at -> List.tryHead at.IndexTypes
             | _ -> None
         match find "x", find "y" with
         | Some bx, Some by ->
             match ixOf bx, ixOf by with
             | Some ixX, Some ixY ->
                 let coord = IRVar (987654321, IRTScalar ETInt64)
                 let read = IRIndex (IRVar (by.Id, by.Type), [ coord ], None)
                 let walk (lvl: IRIndexType) =
                     Blade.IRTotality.walk resolveCallable (fun _ -> None) (fun _ -> false)
                         (Map.ofList [ (987654321, lvl) ]) Map.empty Set.empty Set.empty Set.empty read
                 match walk ixX, walk ixY with
                 | Error why, Ok _ when why.Contains "not proven in range" ->
                     passed <- passed + 1
                     resultLine Pass name "Ix coordinate into a Jx array (equal extent): refused; a Jx coordinate: proven"
                 | a, b -> fail name ($"expected refused / proven, got {a} / {b}")
             | _ -> fail name "x / y carry no index records"
         | _ -> fail name "bindings x / y not found")
    // ---- G51: the ROW-VIEW IRReduce arm of the shared whitelist, on
    // hand-built IR where the front end would refuse first: a row view by a
    // coordinate of a SAME-EXTENT different index (BL4003 at the front end) is
    // refused, the base's own first index is proven; an UNSEEDED fold over an
    // empty row (BL3007 at the front end) is refused, the seeded one admitted;
    // a view of a rank-3 array and a view of a deferred computation decline.
    (let name = "g51_row_view_walk_identity_empty_rank_deferred"
     let src =
         "type Ix = Idx<7>\ntype Jx = Idx<7>\ntype Kx = Idx<5>\ntype Ex = Idx<0>\n"
         + "let w = method_for(range<Jx, Kx>) <@> lambda(j, k) -> Float64(j * k) |> compute\n"
         + "let e = method_for(range<Jx, Ex>) <@> lambda(j, k) -> Float64(j * k) |> compute\n"
         + "let c = method_for(range<Jx, Kx, Kx>) <@> lambda(j, k, l) -> Float64(j * k + l) |> compute\n"
         + "let ix = method_for(range<Ix>) <@> lambda(i) -> Float64(i) |> compute\n"
         + "let t = reduce(w((0 : Jx)), (+))\n"
     match lower src with
     | Error e -> fail name ($"lower: {e}")
     | Ok ir ->
         let binds = ir.Modules |> List.collect (fun m -> m.Bindings)
         let fns = ir.Modules |> List.collect (fun m -> m.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
         let res (x: IRExpr) =
             match x with
             | IRVar (id, _) when fns.ContainsKey id -> Some fns.[id]
             | _ -> resolveCallable x
         let find (n: string) = binds |> List.tryFind (fun (b: IRBinding) -> b.Name = n)
         let mutable kernel = None
         for b in binds do
             iterIRExpr (fun n -> match n with IRReduce (_, k, _) when kernel.IsNone -> kernel <- Some k | _ -> ()) b.Value
         let ixOf (b: IRBinding) =
             match b.Type with
             | ArrayElem at -> List.tryHead at.IndexTypes
             | _ -> None
         match find "w", find "e", find "c", find "ix", kernel with
         | Some bw, Some be, Some bc, Some bi, Some k ->
             (match ixOf bi, ixOf bw with
              | Some ixI, Some ixJ ->
                  let coord = IRVar (987654321, IRTScalar ETInt64)
                  let view (b: IRBinding) = IRIndex (IRVar (b.Id, b.Type), [ coord ], None)
                  let walkWith (isDeferred: IRId -> bool) (lvl: IRIndexType) (e: IRExpr) =
                      Blade.IRTotality.walk res (fun _ -> None) isDeferred
                          (Map.ofList [ (987654321, lvl) ]) Map.empty Set.empty Set.empty Set.empty e
                  let walk = walkWith (fun _ -> false)
                  // a base whose COLUMN extent does not evaluate (a runtime
                  // extent): declined for the seeded and the unseeded fold
                  let dynView =
                      match bw.Type with
                      | ArrayElem at ->
                          let colDyn = { at.IndexTypes.[1] with Extent = IRVar (987654322, IRTScalar ETInt64) }
                          Some (IRIndex (IRVar (bw.Id, mkArrayLike { at with IndexTypes = [ at.IndexTypes.[0]; colDyn ] }), [ coord ], None))
                      | _ -> None
                  let checks =
                      (match dynView with
                       | Some dv ->
                           [ ("an unevaluable column extent, unseeded", walk ixJ (IRReduce (dv, k, None)), Some "no static extent")
                             ("an unevaluable column extent, seeded", walk ixJ (IRReduce (dv, k, Some (IRLit (IRLitFloat 1.0)))), Some "no static extent") ]
                       | None -> [ ("an unevaluable column extent", Error "w is not an array", Some "never") ])
                      @
                      [ ("a coordinate of a same-extent other index", walk ixI (IRReduce (view bw, k, None)), Some "row-view subscript not proven in range")
                        ("the base's own first index", walk ixJ (IRReduce (view bw, k, None)), None)
                        ("an unseeded fold over an empty row", walk ixJ (IRReduce (view be, k, None)), Some "BL8003: an unseeded fold over an empty row")
                        ("a seeded fold over an empty row", walk ixJ (IRReduce (view be, k, Some (IRLit (IRLitFloat 1.0)))), None)
                        ("a view of a rank-3 array", walk ixJ (IRReduce (view bc, k, None)), Some "not dense rank 2")
                        ("a view of a deferred computation", walkWith (fun id -> id = bw.Id) ixJ (IRReduce (view bw, k, None)), Some "reads a deferred computation") ]
                  let bad =
                      checks |> List.choose (fun (what, got, want) ->
                          match want, got with
                          | None, Ok _ -> None
                          | Some needle, Error why when why.Contains needle -> None
                          | _ -> Some ($"{what}: got {got}"))
                  if bad.IsEmpty then
                      passed <- passed + 1
                      resultLine Pass name "other index / empty unseeded / rank 3 / deferred / unevaluable column extent (seeded and unseeded): refused; own index / empty seeded: admitted"
                  else fail name (String.concat " | " bad)
              | _ -> fail name "w / ix carry no index records")
         | _ -> fail name "bindings w / e / c / ix or the (+) kernel not found")
    printFooter "OpenMP Pragma" [$"{passed} passed"; $"{failed} failed"]
    { Block = "OpenMP Pragma"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }

/// Run OpenMP thread-coverage tests. Generates representative loop-nest
/// programs with codegen TEST MODE on (which injects per-region thread
/// observation), compiles with -fopenmp, runs with OMP_NUM_THREADS forced > 1,
/// parses the emitted "[omp-coverage] region=... teamsz=K distinct=D maxth=M"
/// lines, and applies the rule:
///   - maxth <= 1            : single-core context — cannot test parallelism;
///                             reported as a skip-ish PASS (not a failure).
///   - maxth > 1, teamsz <= 1: ERROR — a loop that should be an OpenMP-parallel
///                             loop ran as a serial region (pragma not honored).
///   - maxth > 1, teamsz > 1 : PASS — a genuine parallel team was formed. (If
///                             distinct == 1, the scheduler put all work on one
///                             thread; that is an allowed scheduler choice, so
///                             it is reported as a WARNING, not a failure.)
///
/// Returns 0 if no errors (warnings allowed), 1 if any error.
let runOmpCoverageTests () : Blade.Tests.TestHarness.BlockResult =
    let caps = capabilities.Value
    printHeader "OpenMP Thread-Coverage Tests"
    // This block's whole question is "does an emitted pragma form a real team",
    // which a globally-set BLADE_OMP_THREADS=1 would answer by deleting the
    // pragma -- and the block would then report its own vacuity as a pass (zero
    // [omp-coverage] lines is already a hard failure below, so it would in fact
    // report it as a FAILURE unrelated to any real regression). Pinned unset.
    use _ompThreadsPin = pinOmpThreadsUnset ()
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile the -fopenmp coverage programs)."
        // Skipped = 1, not 0: a Skipped = 0 return made a toolchain-less box
        // print "0 passed, 0 failed" for this block with no skip note. Same
        // convention as DiffOracle/InterpDiff.
        { Block = "OpenMP Coverage"; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
        // Representative programs exercising each parallelization strategy.
        // Source strings are defined as separate bindings (not inline in the
        // list) so the triple-quoted content does not disturb F# offside parsing.
        //
        // COVERAGE programs (just need to compile + form a parallel region):
        //   rect (collapse), symmetric (dynamic outer), and a partial-comm 3-arg
        //   kernel (mixed symmetry structure). Antisymmetric STRICT iteration is
        //   not expressed by a simple clause (it requires AntisymIdx typing), so
        //   it is intentionally omitted here rather than guessed at.
        let rectSrc =
            "let A = Float64([1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0])\n" +
            "let B = Float64([1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0])\n" +
            "let L = method_for(A, B)\n" +
            "let f = lambda(x, y) where omp(x: 1) -> x * y\n" +
            "let result = L <@> f |> compute\n"
        let symSrc =
            "let A = Float64([1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0])\n" +
            "let L = method_for(A, A)\n" +
            "let k = lambda(x, y) where comm(x, y), omp(x: 1) -> x * y\n" +
            "let result = L <@> k |> compute\n"
        // Partial comm: 3-arg kernel with comm on a subset (proven form, see
        // Test_Symmetry). Exercises a mixed symmetry nest through genNestPragma.
        let mixedSrc =
            "let A = Float64([1.0,2.0,3.0,4.0])\n" +
            "let L = method_for(A, A, A)\n" +
            "let k = lambda(x, y, z) where comm(x, y), omp(x: 1) -> x * y * z\n" +
            "let result = L <@> k |> compute\n"
        // Phase 2 Path B: a comm-licensed fold over a DEFERRED computation. Its
        // parallel region is an explicit team with per-thread partials, not a
        // `parallel for`, so pragma text alone cannot say whether a team really
        // forms — this is the ground-truth check that it does.
        // `axes = 2` is the full fold's spelling (see the note at
        // `reduce_over_computation_chunked`); without it this is a partial fold
        // with no team to observe, and the coverage check goes vacuous.
        let foldSrc =
            "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" +
            "let A = Float64([1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0])\n" +
            "let B = Float64([1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0])\n" +
            "let s = reduce(method_for(A, B) <@> lambda(x, y) -> x * y, myAdd, 0.0, axes = 2)\n"
        // G55: a SCALAR omp fold let in a FUNCTION body -- its own let, so it
        // takes the statement fold path and forms a real team (it used to be
        // rendered as an expression-position fold that opens none). The named
        // comm kernel is the Path B explicit team.
        let fnBodyFoldSrc =
            "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" +
            "type I = Idx<2003>\n" +
            "function fsum(v: Array<Float64 like I>) -> Float64 = {\n    let s = reduce(v, myAdd, 0.0)\n    s\n}\n" +
            "let x = Float64(0..2003) * 0.5 |> compute\n" +
            "let fb = fsum(x)\n"
        let programs =
            [ ("rect_outer_product", rectSrc)
              ("symmetric_triangular", symSrc)
              ("mixed_partial_comm", mixedSrc)
              ("comm_licensed_fold", foldSrc)
              ("function_body_scalar_fold_let", fnBodyFoldSrc) ]
        let outputDir = "./generated_omp_coverage"
        Directory.CreateDirectory(outputDir) |> ignore
        // Write runtime headers into the output dir so the generated programs'
        // #include "nested_array_utilities.hpp" / "nested_array_types.hpp"
        // resolve at g++ time (same as the main test path does).
        CodeGen.deployRuntimeHeaders outputDir
        let mutable errors = 0
        let mutable warnings = 0
        let mutable passed = 0
        let mutable failedNames = []
        // Force a multi-thread environment for the run so the gate is meaningful.
        let forcedThreads = "4"
        for (name, src) in programs do
            // Generate with codegen test-mode ON (injects instrumentation), then
            // restore so nothing else in the process is affected.
            setOmpTestMode true
            let outcome =
                try
                    let safeName = sanitizeFileName name
                    match lower src with
                    | Error e -> Error ($"lower failed: {e}")
                    | Ok ir0 ->
                        // A validation error is a hard failure. The old
                        // `| Error _ -> ir0` ("validation errors don't block this
                        // probe") generated C++ from invalid IR, so a validator
                        // regression on these programs was invisible here.
                        match IRValidate.validateIR ir0 with
                        | Error validationErrors ->
                            Error ($"""IR validation failed: {(String.concat "; " validationErrors)}""")
                        | Ok ir ->
                            let (cppCode, _warnings) = CodeGen.genSelfContainedProgramFromIR ir name
                            let srcFile = Path.Combine(outputDir, safeName + ".cpp")
                            File.WriteAllText(srcFile, cppCode)
                            Ok srcFile
                with ex -> Error ($"codegen failed: {ex.Message}")
            setOmpTestMode false
            match outcome with
            | Error e -> Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name ($"generation: {e}"); errors <- errors + 1; failedNames <- failedNames @ [name]
            | Ok srcFile ->
                let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
                // Use ABSOLUTE paths for g++. srcFile is relative to the process
                // cwd; passing it relative while also setting WorkingDirectory to
                // the output dir caused g++ to resolve it against that dir (a
                // doubled path). Absolute paths make the working dir irrelevant.
                let srcAbs = Path.GetFullPath(srcFile)
                let exeAbs = Path.ChangeExtension(srcAbs, exeExt)
                let cpsi = ProcessStartInfo("g++", $"-std=c++17 {(optFlags ())} -fopenmp -o \"{exeAbs}\" \"{srcAbs}\"")
                cpsi.RedirectStandardError <- true
                cpsi.UseShellExecute <- false
                use cproc = Process.Start(cpsi)
                let cerr = Blade.Runtime.readToEndOffPool cproc.StandardError
                // WaitForExit(ms) returns false on TIMEOUT; ExitCode is
                // meaningless then, so a hung g++ used to be read as success.
                let cExited = cproc.WaitForExit(60000)
                if not cExited then
                    (try cproc.Kill(true) with _ -> ())
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name "compile timed out (60s)"
                    errors <- errors + 1
                    failedNames <- failedNames @ [name]
                elif cproc.ExitCode <> 0 then
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name ($"compile: {cerr.Result}")
                    errors <- errors + 1
                    failedNames <- failedNames @ [name]
                else
                    // Run with OMP_NUM_THREADS forced.
                    let rpsi = ProcessStartInfo(exeAbs)
                    rpsi.RedirectStandardOutput <- true
                    rpsi.RedirectStandardError <- true
                    rpsi.UseShellExecute <- false
                    rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                    rpsi.Environment.["OMP_NUM_THREADS"] <- forcedThreads
                    use rproc = Process.Start(rpsi)
                    let rout = Blade.Runtime.readToEndOffPool rproc.StandardOutput
                    let rerr = Blade.Runtime.readToEndOffPool rproc.StandardError
                    let rExited = rproc.WaitForExit(30000)
                    if not rExited then (try rproc.Kill(true) with _ -> ())
                    let lines = rout.Result.Split('\n') |> Array.filter (_.Contains("[omp-coverage]"))
                    if not rExited then
                        Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name "run timed out (30s)"
                        errors <- errors + 1
                        failedNames <- failedNames @ [name]
                    elif lines.Length = 0 then
                        // Every program in `programs` carries an explicit
                        // `omp(x: 1)` clause, and CodeGen emits the
                        // [omp-coverage] instrumentation exactly when
                        // ompInstrument && outerIsParallel. So zero coverage
                        // lines means the pragma was NOT emitted for an
                        // annotated kernel -- the very condition this block
                        // exists to detect. It used to print a Skip line and
                        // increment nothing at all: not passed, not failed, not
                        // skipped, so the block silently shrank to nothing.
                        Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name
                            (sprintf "no [omp-coverage] line emitted for an omp()-annotated kernel (exit %d)%s"
                                rproc.ExitCode
                                (if String.IsNullOrWhiteSpace rerr.Result then "" else " stderr: " + rerr.Result.Trim()))
                        errors <- errors + 1
                        failedNames <- failedNames @ [name]
                    for line in lines do
                        // parse "region=R teamsz=K distinct=D maxth=M"
                        // None (not -1) on a regex miss: a sentinel -1 flowed
                        // into `maxth <= 1`, which is the UNCONDITIONAL-PASS
                        // arm below, so an unparseable coverage line scored a
                        // "single-core, cannot test parallelism" pass.
                        let getField (k: string) =
                            let m = System.Text.RegularExpressions.Regex.Match(line, k + "=(\\d+)")
                            if m.Success then Some (int m.Groups.[1].Value) else None
                        let teamszO = getField "teamsz"
                        let distinctO = getField "distinct"
                        let maxthO = getField "maxth"
                        let missing =
                            [ if teamszO.IsNone then "teamsz"
                              if distinctO.IsNone then "distinct"
                              if maxthO.IsNone then "maxth" ]
                        let teamsz = defaultArg teamszO 0
                        let distinct = defaultArg distinctO 0
                        let maxth = defaultArg maxthO 0
                        if not missing.IsEmpty then
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name
                                ($"""unparseable coverage line (missing {(String.concat ", " missing)}): {(line.Trim())}""")
                            errors <- errors + 1
                            failedNames <- failedNames @ [name]
                        elif maxth <= 1 then
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name ($"single-core: maxth={maxth}, cannot test parallelism")
                            passed <- passed + 1
                        elif teamsz <= 1 then
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name ($"parallel loop ran serially (teamsz={teamsz}, maxth={maxth}) -- pragma not honored")
                            errors <- errors + 1
                            failedNames <- failedNames @ [name]
                        elif distinct <= 1 then
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name ($"WARNING: parallel team formed (teamsz={teamsz}) but scheduler used 1 thread (distinct={distinct})")
                            warnings <- warnings + 1
                            passed <- passed + 1
                        else
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name ($"teamsz={teamsz}, distinct={distinct}, maxth={maxth}")
                            passed <- passed + 1

        // -------------------------------------------------------------------
        // G35: THE ROW-SLOT TEAM IS OBSERVED, not only emitted. Generated under
        // BLADE_FP_REPRO=1 with test mode on, run with OMP_DYNAMIC=false and no
        // thread limit (a dynamic or limited runtime could hand back a team of
        // one and make the identity checks vacuous): at T = 4 and 7 the team
        // must have > 1 member and > 1 thread must have done rows. A small rn
        // (3 rows, gate lowered) pins the clamp T = min(T, rn); rn = 0 pins the
        // empty guard (no region, the seed is the value). Every run's checksum
        // line must equal the T = 1 run's.
        // -------------------------------------------------------------------
        let zeroRowsSrc =
            "type Z = Idx<0>\ntype J = Idx<5>\n"
            + "let w = method_for(range<J>) <@> lambda(j) -> Float64(j) + 0.5 |> compute\n"
            + "let s = reduce(method_for(range<Z>) <@> lambda(i) -> reduce(method_for(range<J>) <@> lambda(j) -> w(j) * 2.0, (+)), lambda(u, v) where omp -> u + v, 0.25)\n"
        // (name, source, BLADE_TILE_TUNE, BLADE_FOLD_ROW_SLOTS, max team size
        // the clamp allows, expected `s` line or ""). The two rowSlotSym2 walks
        // are ones the ordered orbit scatter also admits, and since round 3
        // (W2-0a) it takes them by default: they observe the ROW-SLOT team
        // under `prefer`. The zero-row and row-view walks are no scatter's, so
        // the row slots are their default form.
        let rowSlotCoverage : (string * string * string * string * int * string) list =
            [ ("row_slots_team_rank2", rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)", null, "prefer", 64, "")
              ("row_slots_team_small_rn_clamped", rowSlotSym2 3 rowSlotOmpOuter "g(i, j) * g(i, j)", "rowslots_min=1", "prefer", 3, "")
              ("row_slots_zero_rows_keeps_seed", zeroRowsSrc, "rowslots_min=0", null, 0, "s = 0.25")
              // G51: the stencil checksum shape -- row-view folds -- observed too
              // (quarter-integer data: the value is exact, 66298.5 = 0.5 + sum u)
              ("row_slots_team_row_view", rowViewSrc 521 "reduce(u(i), (+))" rowSlotOmpOuter, null, null, 64, "s = 66298.5") ]
        for (name, src, tune, slotsKnob, maxTeam, expectS) in rowSlotCoverage do
            setOmpTestMode true
            let gen =
                use _r = pinEnv "BLADE_FP_REPRO" "1"
                use _c = pinEnv "BLADE_FP_CONTRACT" "off"
                use _t = pinEnv "BLADE_TILE_TUNE" tune
                use _k = pinEnv "BLADE_FOLD_ROW_SLOTS" slotsKnob
                use _p = pinEnv "BLADE_PRINT" "s"
                try
                    match lower src with
                    | Error e -> Error ($"lower failed: {e}")
                    | Ok ir ->
                        let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir name
                        let srcAbs = Path.GetFullPath(Path.Combine(outputDir, sanitizeFileName name + ".cpp"))
                        File.WriteAllText(srcAbs, cpp)
                        Ok (srcAbs, cpp)
                with ex -> Error ($"codegen failed: {ex.Message}")
            setOmpTestMode false
            // Ok = pass detail, Error = failure detail (no closure over the
            // block's mutable counters; they are updated once below).
            let outcome : Result<string, string> =
                match gen with
                | Error e -> Error e
                | Ok (_, cpp) when not (cpp.Contains "__rslot_s[__i0] = ") -> Error "the generated C++ has no row-slot assignment (the case would not test the form)"
                | Ok (srcAbs, _) ->
                    let exeAbs = Path.ChangeExtension(srcAbs, (if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"))
                    let cpsi = ProcessStartInfo("g++", $"-std=c++17 {(optFlags ())} -fopenmp -o \"{exeAbs}\" \"{srcAbs}\"")
                    cpsi.RedirectStandardError <- true
                    cpsi.UseShellExecute <- false
                    use cproc = Process.Start(cpsi)
                    let cerr = Blade.Runtime.readToEndOffPool cproc.StandardError
                    if not (cproc.WaitForExit(120000)) then
                        (try cproc.Kill(true) with _ -> ())
                        Error "compile timed out (120s)"
                    elif cproc.ExitCode <> 0 then Error ($"compile: {cerr.Result}")
                    else
                    let runAt (threads: string) =
                        let rpsi = ProcessStartInfo(exeAbs)
                        rpsi.RedirectStandardOutput <- true
                        rpsi.RedirectStandardError <- true
                        rpsi.UseShellExecute <- false
                        rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                        rpsi.Environment.["OMP_NUM_THREADS"] <- threads
                        rpsi.Environment.["OMP_DYNAMIC"] <- "false"
                        rpsi.Environment.Remove("OMP_THREAD_LIMIT") |> ignore
                        use rproc = Process.Start(rpsi)
                        let rout = Blade.Runtime.readToEndOffPool rproc.StandardOutput
                        if not (rproc.WaitForExit(60000)) then
                            (try rproc.Kill(true) with _ -> ())
                            Error "run timed out (60s)"
                        elif rproc.ExitCode <> 0 then Error ($"exit {rproc.ExitCode}")
                        else
                            let out = rout.Result
                            let field (k: string) =
                                let m = System.Text.RegularExpressions.Regex.Match(out, k + "=(\\d+)")
                                if m.Success then Some (int m.Groups.[1].Value) else None
                            let sLine =
                                out.Split('\n') |> Array.map (_.Trim()) |> Array.tryFind (fun l -> l.StartsWith "s = ")
                            Ok (field "teamsz", field "distinct", field "maxth", defaultArg sLine "")
                    // T = 8 too (Codex diff review of W1-A2, finding 3); with
                    // dynamic teams off and no thread limit the team must be
                    // EXACTLY min(T, rn), every member doing rows when rn >= T.
                    match runAt "1", runAt "4", runAt "7", runAt "8" with
                    | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> Error e
                    | Ok (_, _, _, s1), Ok (tz4, d4, mx4, s4), Ok (tz7, d7, mx7, s7), Ok (tz8, d8, mx8, s8) ->
                        if s1 = "" then Error "no `s = ` line in the output"
                        elif s4 <> s1 || s7 <> s1 || s8 <> s1 then Error ($"checksum depends on the team: T=1 [{s1}] T=4 [{s4}] T=7 [{s7}] T=8 [{s8}]")
                        elif expectS <> "" && s1 <> expectS then Error ($"expected `{expectS}`, got `{s1}`")
                        elif maxTeam = 0 then Ok ($"rn = 0: no team, the seed is the value ({s1})")
                        else
                            match tz4, d4, mx4, tz7, d7, mx7, tz8, d8, mx8 with
                            | Some tz4, Some d4, Some mx4, Some tz7, Some d7, Some mx7, Some tz8, Some d8, Some mx8 ->
                                let want (t: int) = min t maxTeam
                                if mx4 <= 1 || mx7 <= 1 || mx8 <= 1 then Ok ($"single-core: maxth={mx4}/{mx7}/{mx8}, cannot test parallelism")
                                elif tz4 <= 1 || tz7 <= 1 || tz8 <= 1 || d4 <= 1 || d7 <= 1 || d8 <= 1 then
                                    Error ($"row-slot team not observed: T=4 teamsz={tz4} distinct={d4}; T=7 teamsz={tz7} distinct={d7}; T=8 teamsz={tz8} distinct={d8}")
                                elif tz4 <> want 4 || tz7 <> want 7 || tz8 <> want 8 then
                                    Error ($"team is not exactly min(T, rn = {maxTeam}): T=4 teamsz={tz4}, T=7 teamsz={tz7}, T=8 teamsz={tz8}")
                                elif d4 <> want 4 || d7 <> want 7 || d8 <> want 8 then
                                    Error ($"not every team member did rows: T=4 distinct={d4}, T=7 distinct={d7}, T=8 distinct={d8}")
                                else Ok ($"T=4 teamsz={tz4} distinct={d4}; T=7 teamsz={tz7} distinct={d7}; T=8 teamsz={tz8} distinct={d8}; {s1} at every T")
                            | _ -> Error "unparseable or missing [omp-coverage] line"
            match outcome with
            | Ok detail ->
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name detail
                passed <- passed + 1
            | Error detail ->
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
                errors <- errors + 1
                failedNames <- failedNames @ [name]

        // -------------------------------------------------------------------
        // W2-0a (round 3): THE SCATTER'S PRECEDENCE, OBSERVED. A walk both the
        // row slots and the ordered orbit scatter admit, generated under
        // BLADE_FP_REPRO=1 + CONTRACT=off with test mode on, three ways: the
        // DEFAULT (the scatter taken: no row slots, so NO team forms -- zero
        // [omp-coverage] lines at T = 8 -- and the census in the C++ says why
        // the request was not spent), `prefer` (the row-slot team, observed),
        // and the serial walk (BLADE_OMP_THREADS=1 BLADE_ORBIT_SCATTER=0). The
        // printed value is the same text in all three, at T = 1 and 8.
        // -------------------------------------------------------------------
        (let name = "orbit_scatter_precedence_spends_no_team"
         let src = rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)"
         let genWith (knob: string) (threads: string) (scatter: string) (tag: string) =
             setOmpTestMode true
             let r =
                 use _r = pinEnv "BLADE_FP_REPRO" "1"
                 use _c = pinEnv "BLADE_FP_CONTRACT" "off"
                 use _t = pinEnv "BLADE_TILE_TUNE" null
                 use _k = pinEnv "BLADE_FOLD_ROW_SLOTS" knob
                 use _o = pinEnv "BLADE_OMP_THREADS" threads
                 use _s = pinEnv "BLADE_ORBIT_SCATTER" scatter
                 use _p = pinEnv "BLADE_PRINT" "s"
                 try
                     match lower src with
                     | Error e -> Error ($"lower failed: {e}")
                     | Ok ir ->
                         let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir (name + tag)
                         let srcAbs = Path.GetFullPath(Path.Combine(outputDir, sanitizeFileName (name + tag) + ".cpp"))
                         File.WriteAllText(srcAbs, cpp)
                         Ok (srcAbs, cpp)
                 with ex -> Error ($"codegen failed: {ex.Message}")
             setOmpTestMode false
             r
         let compileExe (srcAbs: string) : Result<string, string> =
             let exeAbs = Path.ChangeExtension(srcAbs, (if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"))
             let cpsi = ProcessStartInfo("g++", $"-std=c++17 {(optFlags ())} -fopenmp -o \"{exeAbs}\" \"{srcAbs}\"")
             cpsi.RedirectStandardError <- true
             cpsi.UseShellExecute <- false
             use cproc = Process.Start(cpsi)
             let cerr = Blade.Runtime.readToEndOffPool cproc.StandardError
             if not (cproc.WaitForExit(120000)) then
                 (try cproc.Kill(true) with _ -> ())
                 Error "compile timed out (120s)"
             elif cproc.ExitCode <> 0 then Error ($"compile: {cerr.Result}")
             else Ok exeAbs
         let runAt (exeAbs: string) (threads: string) : Result<string * string list, string> =
             let rpsi = ProcessStartInfo(exeAbs)
             rpsi.RedirectStandardOutput <- true
             rpsi.RedirectStandardError <- true
             rpsi.UseShellExecute <- false
             rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
             rpsi.Environment.["OMP_NUM_THREADS"] <- threads
             rpsi.Environment.["OMP_DYNAMIC"] <- "false"
             rpsi.Environment.Remove("OMP_THREAD_LIMIT") |> ignore
             use rproc = Process.Start(rpsi)
             let rout = Blade.Runtime.readToEndOffPool rproc.StandardOutput
             if not (rproc.WaitForExit(60000)) then
                 (try rproc.Kill(true) with _ -> ())
                 Error "run timed out (60s)"
             elif rproc.ExitCode <> 0 then Error ($"exit {rproc.ExitCode}")
             else
                 let lines = rout.Result.Split('\n') |> Array.map (_.Trim())
                 let sLine = lines |> Array.tryFind (fun l -> l.StartsWith "s = ") |> Option.defaultValue ""
                 Ok (sLine, lines |> Array.filter (fun l -> l.StartsWith "[omp-coverage]") |> Array.toList)
         let outcome : Result<string, string> =
             match genWith null null null "_default", genWith "prefer" null null "_prefer", genWith null "1" "0" "_walk" with
             | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e
             | Ok (dSrc, dCpp), Ok (pSrc, pCpp), Ok (wSrc, wCpp) ->
                 if not (dCpp.Contains "blade_scatter::scatter2<") || dCpp.Contains "__rslot" then
                     Error "the default build is not the ordered orbit scatter (no blade_scatter:: call, or row slots emitted)"
                 elif not (dCpp.Contains ("; " + Blade.OrbitFold.rowSlotsYieldedCensus + "; requested at: level 1's fold)")) then
                     Error "the default build's census does not say the row-slot form was admitted but yielded"
                 elif not (pCpp.Contains "__rslot_s[__i0] = ") || pCpp.Contains "blade_scatter::" then
                     Error "the prefer build is not the row-slot form"
                 elif wCpp.Contains "__rslot" || wCpp.Contains "blade_scatter::" then
                     Error "the serial-walk build emitted row slots or the scatter"
                 else
                 match compileExe dSrc, compileExe pSrc, compileExe wSrc with
                 | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e
                 | Ok dExe, Ok pExe, Ok wExe ->
                     match runAt dExe "1", runAt dExe "8", runAt pExe "1", runAt pExe "8", runAt wExe "1" with
                     | Error e, _, _, _, _ | _, Error e, _, _, _ | _, _, Error e, _, _ | _, _, _, Error e, _ | _, _, _, _, Error e -> Error e
                     | Ok (d1, _), Ok (d8, dCov8), Ok (p1, _), Ok (p8, pCov8), Ok (w1, _) ->
                         let teamOf (cov: string list) =
                             cov |> List.choose (fun l ->
                                 let m = System.Text.RegularExpressions.Regex.Match(l, @"teamsz=(\d+) distinct=(\d+) maxth=(\d+)")
                                 if m.Success then Some (int m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value) else None)
                         if w1 = "" then Error "no `s = ` line in the serial walk's output"
                         elif d1 <> w1 || d8 <> w1 || p1 <> w1 || p8 <> w1 then
                             Error ($"the value depends on the form: walk [{w1}] scatter T=1 [{d1}] T=8 [{d8}] row slots T=1 [{p1}] T=8 [{p8}]")
                         elif not dCov8.IsEmpty then
                             Error ($"""the scatter build formed a team at T=8 (the request must not be spent): {(String.concat " | " dCov8)}""")
                         else
                             match teamOf pCov8 with
                             | [ (_, _, mx) ] when mx <= 1 -> Ok ($"single-core (maxth={mx}); scatter: no team; {w1} in every form")
                             | [ (tz, d, _) ] when tz = 8 && d = 8 -> Ok ($"scatter: no team at T=8, census names the yield; prefer: the row-slot team at T=8 (teamsz=distinct=8); {w1} in the walk, the scatter (T = 1, 8) and the row slots (T = 1, 8)")
                             | other -> Error ($"the prefer build's row-slot team at T=8 is not observed as 8 / 8: {other}")
         match outcome with
         | Ok detail ->
             Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name detail
             passed <- passed + 1
         | Error detail ->
             Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
             errors <- errors + 1
             failedNames <- failedNames @ [name])

        // -------------------------------------------------------------------
        // W2-0a (round 3; Codex diff review of W2-0, finding 2): THE PRODUCER
        // TEAMS ARE UNTOUCHED by the precedence. The measured hybrid's shape
        // (pairkernel / comoment3): a stored-pool producer with its OWN omp
        // licences -- the input map `omp(i: 1)` and the comm kernel's level 0
        // `omp(a: 1)` (a non-dot body, so a plain threaded nest) -- then the
        // checksum walk both forms admit. Generated in test mode under
        // BLADE_FP_REPRO=1 + CONTRACT=off three ways -- the default (the
        // scatter), BLADE_FOLD_ROW_SLOTS=0 (the scatter), `prefer` (the row
        // slots) -- with ONE program name, so the texts are comparable:
        //   * the C++ up to the fold (every producer line) is byte-identical
        //     in all three, and carries no census line;
        //   * default vs slots=0 differ ONLY in the fold's census comment (the
        //     yield vs the knob decline), every other line identical;
        //   * at T = 8 the default and slots=0 builds observe EXACTLY the same
        //     producer regions (each a team of 8), and prefer observes those
        //     same lines plus ONE more region, the row-slot team -- the census
        //     sites distinguished: one census at the fold in default / slots=0,
        //     none in prefer;
        //   * `s` prints the same text in all three at T = 1 and 8.
        // -------------------------------------------------------------------
        (let name = "orbit_scatter_precedence_keeps_producer_teams"
         let src =
             "type Var = Idx<521>\ntype Obs = Idx<3>\n"
             + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) where omp(i: 1) -> Float64((i * 7 + s * 3) % 11) * 0.37 + 0.1 / Float64(i + 1) |> compute\n"
             + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b), omp(a: 1) -> (a((0 : Obs)) - b((0 : Obs))) * (a((1 : Obs)) - b((1 : Obs))) + a((2 : Obs)) * b((2 : Obs)) |> compute\n"
             + "let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j) * g(i, j), (+)), lambda(u, v) where omp -> u + v, 0.25)\n"
         let anchor = "\n    double s = "
         let genWith (knob: string) (tag: string) =
             setOmpTestMode true
             let r =
                 use _r = pinEnv "BLADE_FP_REPRO" "1"
                 use _c = pinEnv "BLADE_FP_CONTRACT" "off"
                 use _t = pinEnv "BLADE_TILE_TUNE" null
                 use _k = pinEnv "BLADE_FOLD_ROW_SLOTS" knob
                 use _o = pinEnv "BLADE_OMP_THREADS" null
                 use _s = pinEnv "BLADE_ORBIT_SCATTER" null
                 use _b = pinEnv "BLADE_BLAS" "0"
                 use _p = pinEnv "BLADE_PRINT" "s"
                 try
                     match lower src with
                     | Error e -> Error ($"lower failed: {e}")
                     | Ok ir ->
                         let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir name
                         let srcAbs = Path.GetFullPath(Path.Combine(outputDir, sanitizeFileName (name + tag) + ".cpp"))
                         File.WriteAllText(srcAbs, cpp)
                         let exeAbs = Path.ChangeExtension(srcAbs, (if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"))
                         let cpsi = ProcessStartInfo("g++", $"-std=c++17 {(optFlags ())} -fopenmp -o \"{exeAbs}\" \"{srcAbs}\"")
                         cpsi.RedirectStandardError <- true
                         cpsi.UseShellExecute <- false
                         use cproc = Process.Start(cpsi)
                         let cerr = Blade.Runtime.readToEndOffPool cproc.StandardError
                         if not (cproc.WaitForExit(120000)) then
                             (try cproc.Kill(true) with _ -> ())
                             Error "compile timed out (120s)"
                         elif cproc.ExitCode <> 0 then Error ($"compile: {cerr.Result}")
                         else Ok (exeAbs, cpp.Replace("\r\n", "\n"))
                 with ex -> Error ($"codegen failed: {ex.Message}")
             setOmpTestMode false
             r
         // (s line, the sorted [omp-coverage] lines), dynamic teams off, no limit
         let runAt (exeAbs: string) (threads: string) : Result<string * string list, string> =
             let rpsi = ProcessStartInfo(exeAbs)
             rpsi.RedirectStandardOutput <- true
             rpsi.RedirectStandardError <- true
             rpsi.UseShellExecute <- false
             rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
             rpsi.Environment.["OMP_NUM_THREADS"] <- threads
             rpsi.Environment.["OMP_DYNAMIC"] <- "false"
             rpsi.Environment.Remove("OMP_THREAD_LIMIT") |> ignore
             use rproc = Process.Start(rpsi)
             let rout = Blade.Runtime.readToEndOffPool rproc.StandardOutput
             if not (rproc.WaitForExit(60000)) then
                 (try rproc.Kill(true) with _ -> ())
                 Error "run timed out (60s)"
             elif rproc.ExitCode <> 0 then Error ($"exit {rproc.ExitCode}")
             else
                 let lines = rout.Result.Replace("\r\n", "\n").Split('\n') |> Array.map (_.Trim())
                 let sLine = lines |> Array.tryFind (fun l -> l.StartsWith "s = ") |> Option.defaultValue ""
                 Ok (sLine, lines |> Array.filter (fun l -> l.StartsWith "[omp-coverage]") |> Array.sort |> Array.toList)
         let census = "[omp] requested but emitted serial"
         let countOf (needle: string) (text: string) =
             (System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape needle)).Count
         let outcome : Result<string, string> =
             match genWith null "_default", genWith "0" "_slots_off", genWith "prefer" "_prefer" with
             | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e
             | Ok (dExe, dCpp), Ok (oExe, oCpp), Ok (pExe, pCpp) ->
                 // the fold's first line: its seed, or (the row-slot form) the
                 // fold kernel's wrapper emitted just before it
                 let split (cpp: string) =
                     let m = System.Text.RegularExpressions.Regex.Match(cpp, @"\n    (auto __wrap_\w+_s = |double s = )")
                     let at = if m.Success then m.Index else cpp.IndexOf anchor
                     // the one head line the FOLD's route decides: the scatter
                     // runtime's include (asserted per build below)
                     let scatterInclude = "#include \"blade_orbit_scatter.hpp\"\n"
                     if at < 0 then None else Some (cpp.Substring(0, at).Replace(scatterInclude, ""), cpp.Substring(at))
                 match split dCpp, split oCpp, split pCpp with
                 | None, _, _ | _, None, _ | _, _, None -> Error "the fold's first line (`double s = `) is missing from a build"
                 | Some (dHead, dFold), Some (oHead, oFold), Some (pHead, pFold) ->
                     let foldCensusOther (fold: string) = fold.Split('\n') |> Array.filter (fun l -> l.Contains census) |> Array.toList
                     let dMinus = dFold.Split('\n') |> Array.filter (fun l -> not (l.Contains census))
                     let oMinus = oFold.Split('\n') |> Array.filter (fun l -> not (l.Contains census))
                     let inc = "#include \"blade_orbit_scatter.hpp\""
                     if not (dCpp.Contains inc && oCpp.Contains inc) || pCpp.Contains inc then
                         Error "the scatter runtime include must be in the default / slots=0 builds only"
                     elif not (dHead = oHead && dHead = pHead) then
                         Error "the producer C++ (everything before the fold, the scatter include aside) differs between default / slots=0 / prefer"
                     elif countOf census dHead <> 0 then
                         Error "the producer C++ carries a census line (its omp licences must be spent as teams)"
                     elif not (dCpp.Contains "blade_scatter::scatter2<") || dCpp.Contains "__rslot" || not (oCpp.Contains "blade_scatter::scatter2<") || oCpp.Contains "__rslot" then
                         Error "the default / slots=0 builds are not the ordered orbit scatter"
                     elif not (pCpp.Contains "__rslot_s[__i0] = ") || pCpp.Contains "blade_scatter::" then
                         Error "the prefer build is not the row-slot form"
                     elif (foldCensusOther dFold).Length <> 1 || not ((foldCensusOther dFold).Head.Contains Blade.OrbitFold.rowSlotsYieldedCensus) then
                         Error ($"""the default fold must carry ONE census line naming the yield: {String.concat " | " (foldCensusOther dFold)}""")
                     elif (foldCensusOther oFold).Length <> 1 || not ((foldCensusOther oFold).Head.Contains "the threaded complete-row form declined: disabled by BLADE_FOLD_ROW_SLOTS=0") then
                         Error ($"""the slots=0 fold must carry ONE census line naming the knob: {String.concat " | " (foldCensusOther oFold)}""")
                     elif countOf census pFold <> 0 then
                         Error "the prefer fold spends the request on the row-slot team: no census line expected there"
                     elif dMinus <> oMinus then
                         Error "default and slots=0 differ beyond the fold's census comment"
                     else
                     match runAt dExe "1", runAt dExe "8", runAt oExe "8", runAt pExe "1", runAt pExe "8" with
                     | Error e, _, _, _, _ | _, Error e, _, _, _ | _, _, Error e, _, _ | _, _, _, Error e, _ | _, _, _, _, Error e -> Error e
                     | Ok (d1, _), Ok (d8, dCov), Ok (o8, oCov), Ok (p1, _), Ok (p8, pCov) ->
                         // (region, teamsz, distinct, maxth); the REGION and the
                         // team size are compared (`distinct` may vary run to run
                         // under the producers' dynamic schedule)
                         let teamOf (l: string) =
                             let m = System.Text.RegularExpressions.Regex.Match(l, @"region=(\S+) teamsz=(\d+) distinct=(\d+) maxth=(\d+)")
                             if m.Success then Some (m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value, int m.Groups.[4].Value) else None
                         let parsed (cov: string list) = cov |> List.choose teamOf
                         let keys (cov: string list) = parsed cov |> List.map (fun (r, tz, _, _) -> (r, tz)) |> List.sort
                         let dK, oK, pK = keys dCov, keys oCov, keys pCov
                         let extra = pK |> List.filter (fun k -> not (List.contains k dK))
                         let shown (cov: string list) = String.concat " | " cov
                         if d1 = "" then Error "no `s = ` line"
                         elif d8 <> d1 || o8 <> d1 || p1 <> d1 || p8 <> d1 then
                             Error ($"s differs between the forms: default T=1 [{d1}] T=8 [{d8}] slots=0 [{o8}] prefer T=1 [{p1}] T=8 [{p8}]")
                         elif dCov.IsEmpty then Error "no producer region observed at T = 8 (the fixture would not test the producer team)"
                         elif dK.Length <> dCov.Length || pK.Length <> pCov.Length || oK.Length <> oCov.Length then
                             Error "unparseable [omp-coverage] line"
                         elif dK <> oK then
                             Error ($"the default and slots=0 builds observe different regions: [{shown dCov}] vs [{shown oCov}]")
                         elif not (dK |> List.forall (fun k -> List.contains k pK)) || extra.Length <> 1 || pK.Length <> dK.Length + 1 then
                             Error ($"prefer must observe the producer regions plus ONE row-slot region: default [{shown dCov}] prefer [{shown pCov}]")
                         else
                         let (rreg, _) = extra.Head
                         let rowSlotLine = parsed pCov |> List.find (fun (r, _, _, _) -> r = rreg)
                         let (_, rtz, rd, rmx) = rowSlotLine
                         if rmx <= 1 then Ok "single-core: cannot test teams (texts and values compared)"
                         elif dK |> List.exists (fun (_, tz) -> tz <> 8) then
                             Error ($"a producer region is not a team of 8: {shown dCov}")
                         elif rtz <> 8 || rd <> 8 then Error ($"the prefer build's row-slot region {rreg} is teamsz={rtz} distinct={rd}, wanted 8 / 8")
                         else
                             let regs = dK |> List.map fst |> String.concat ", "
                             Ok ($"producer C++ byte-identical in default / slots=0 / prefer (no census); default vs slots=0 differ only in the fold's census line (yield vs knob); producer regions {regs} each a team of 8 in all three; prefer adds only the row-slot region {rreg} (8 / 8); {d1} in every form at T = 1 and 8")
         match outcome with
         | Ok detail ->
             Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name detail
             passed <- passed + 1
         | Error detail ->
             Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
             errors <- errors + 1
             failedNames <- failedNames @ [name])
        // THE ROW-OWNED PULL SCATTER'S TEAM IS OBSERVED (round 3, D-P3): the
        // rank-3 checksum walk under BLADE_FP_REPRO=1 with the outer request,
        // generated in test mode (the pull site prints its own [omp-coverage]
        // line, now with its EXECUTED form and requested / actual team), run
        // with dynamic teams off at T = 1, 4, 7, 8 and on a RESTRICTED team
        // (OMP_NUM_THREADS=8 OMP_THREAD_LIMIT=1): the team is EXACTLY
        // min(T, p), every member checked in and the form `row-pull`; T = 1
        // and the restricted team run `serial-leaf3` on one thread; the
        // checksum is the T = 1 run's everywhere. p = 5 (gate lowered) pins
        // the clamp.
        // -------------------------------------------------------------------
        let pullSrc (p: int) =
            $"type Var = Idx<{p}>\ntype Obs = Idx<2>\n"
            + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 3 + s * 5) % 7) * 0.37 - 2.0 |> compute\n"
            + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
            + "let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> t(i, j, k) * t(i, j, k), (+)), (+)), lambda(u, v) where omp -> u + v, 0.0)\n"
        for (name, p, tune) in [ ("orbit_pull_team_p97", 97, null); ("orbit_pull_team_clamped_p5", 5, "pull_min=0") ] do
            setOmpTestMode true
            let gen =
                use _r = pinEnv "BLADE_FP_REPRO" "1"
                use _c = pinEnv "BLADE_FP_CONTRACT" "off"
                use _t = pinEnv "BLADE_TILE_TUNE" tune
                use _k = pinEnv "BLADE_ORBIT_PULL" null
                use _p = pinEnv "BLADE_PRINT" "s"
                try
                    match lower (pullSrc p) with
                    | Error e -> Error ($"lower failed: {e}")
                    | Ok ir ->
                        let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir name
                        let srcAbs = Path.GetFullPath(Path.Combine(outputDir, sanitizeFileName name + ".cpp"))
                        File.WriteAllText(srcAbs, cpp)
                        Ok (srcAbs, cpp)
                with ex -> Error ($"codegen failed: {ex.Message}")
            setOmpTestMode false
            let outcome : Result<string, string> =
                match gen with
                | Error e -> Error e
                | Ok (_, cpp) when not (cpp.Contains "blade_scatter::pull3<") -> Error "the generated C++ has no pull3 call (the case would not test the form)"
                | Ok (srcAbs, _) ->
                    let exeAbs = Path.ChangeExtension(srcAbs, (if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"))
                    let cpsi = ProcessStartInfo("g++", $"-std=c++17 {(optFlags ())} -fopenmp -o \"{exeAbs}\" \"{srcAbs}\"")
                    cpsi.RedirectStandardError <- true
                    cpsi.UseShellExecute <- false
                    use cproc = Process.Start(cpsi)
                    let cerr = Blade.Runtime.readToEndOffPool cproc.StandardError
                    if not (cproc.WaitForExit(120000)) then
                        (try cproc.Kill(true) with _ -> ())
                        Error "compile timed out (120s)"
                    elif cproc.ExitCode <> 0 then Error ($"compile: {cerr.Result}")
                    else
                    let fixedTeam (threads: string) (limit: string option) =
                        let rpsi = ProcessStartInfo(exeAbs)
                        rpsi.RedirectStandardOutput <- true
                        rpsi.RedirectStandardError <- true
                        rpsi.UseShellExecute <- false
                        rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                        rpsi.Environment.["OMP_NUM_THREADS"] <- threads
                        rpsi.Environment.["OMP_DYNAMIC"] <- "false"
                        (match limit with
                         | Some l -> rpsi.Environment.["OMP_THREAD_LIMIT"] <- l
                         | None -> rpsi.Environment.Remove("OMP_THREAD_LIMIT") |> ignore)
                        use rproc = Process.Start(rpsi)
                        let rout = Blade.Runtime.readToEndOffPool rproc.StandardOutput
                        if not (rproc.WaitForExit(60000)) then
                            (try rproc.Kill(true) with _ -> ())
                            Error "run timed out (60s)"
                        elif rproc.ExitCode <> 0 then Error ($"exit {rproc.ExitCode}")
                        else Ok rout.Result
                    // (teamsz, distinct, maxth, executed form, actual team, `s` line)
                    let runAtL (threads: string) (limit: string option) =
                        match fixedTeam threads limit with
                        | Error e -> Error e
                        | Ok out ->
                            let m = System.Text.RegularExpressions.Regex.Match(out, @"\[omp-coverage\] region=s teamsz=(\d+) distinct=(\d+) maxth=(\d+) form=(\S+) requested=(\d+) actual=(\d+)")
                            let sLine = out.Split('\n') |> Array.map (_.Trim()) |> Array.tryFind (fun l -> l.StartsWith "s = ")
                            if not m.Success then Error "no [omp-coverage] line (with its executed form) for the pull site"
                            else Ok (int m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value, m.Groups.[4].Value, int m.Groups.[6].Value, defaultArg sLine "")
                    let runAt (threads: string) = runAtL threads None
                    match runAt "1", runAt "4", runAt "7", runAt "8", runAtL "8" (Some "1") with
                    | Error e, _, _, _, _ | _, Error e, _, _, _ | _, _, Error e, _, _ | _, _, _, Error e, _ | _, _, _, _, Error e -> Error e
                    | Ok (tz1, _, _, f1, a1, s1), Ok (tz4, d4, mx4, f4, a4, s4), Ok (tz7, d7, mx7, f7, a7, s7), Ok (tz8, d8, mx8, f8, a8, s8), Ok (_, _, _, fr, ar, sr) ->
                        let want (t: int) = min t p
                        let formOf (t: int) = if want t > 1 then "row-pull" else "serial-leaf3"
                        if s1 = "" then Error "no `s = ` line in the output"
                        elif s4 <> s1 || s7 <> s1 || s8 <> s1 || sr <> s1 then Error ($"checksum depends on the team: T=1 [{s1}] T=4 [{s4}] T=7 [{s7}] T=8 [{s8}] restricted [{sr}]")
                        elif tz1 <> 1 || f1 <> "serial-leaf3" || a1 <> 1 then Error ($"T=1: team {tz1}, form {f1}, actual {a1} (wanted serial-leaf3 on a team of one)")
                        elif fr <> "serial-leaf3" || ar <> 1 then Error ($"OMP_NUM_THREADS=8 OMP_THREAD_LIMIT=1: form {fr}, actual {ar} (wanted serial-leaf3 on the ACTUAL team of one)")
                        elif mx4 <= 1 || mx7 <= 1 || mx8 <= 1 then Ok ($"single-core: maxth={mx4}/{mx7}/{mx8}, cannot test parallelism")
                        elif tz4 <> want 4 || tz7 <> want 7 || tz8 <> want 8 || d4 <> want 4 || d7 <> want 7 || d8 <> want 8 then
                            Error ($"pull team is not exactly min(T, p = {p}): T=4 {tz4}/{d4}, T=7 {tz7}/{d7}, T=8 {tz8}/{d8} (teamsz/distinct)")
                        elif f4 <> formOf 4 || f7 <> formOf 7 || f8 <> formOf 8 || a4 <> want 4 || a7 <> want 7 || a8 <> want 8 then
                            Error ($"executed form: T=4 {f4}/{a4}, T=7 {f7}/{a7}, T=8 {f8}/{a8} (form/actual)")
                        else Ok ($"T=4 teamsz={tz4}; T=7 teamsz={tz7}; T=8 teamsz={tz8} (= min(T, {p})), form row-pull; T=1 and OMP_THREAD_LIMIT=1 serial-leaf3 on one thread; {s1} at every T")
            match outcome with
            | Ok detail ->
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name detail
                passed <- passed + 1
            | Error detail ->
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
                errors <- errors + 1
                failedNames <- failedNames @ [name]

        // -------------------------------------------------------------------
        // G7: THE THREADED REDUCTION JOIN'S TEAM IS OBSERVED. Generated in the
        // default licensed build with test mode on (the coverage lines sit
        // outside the chunk's simd loop, so the form is the shipped one), run
        // with OMP_DYNAMIC=false and no thread limit at T = 1, 3 and 8: the
        // team must be exactly min(T, n) and, for T > 1, more than one worker
        // must have swept a chunk; every run's sums equal the T = 1 run's
        // (integer-valued cells: exact). n = 2 pins the clamp T = min(T, n).
        // -------------------------------------------------------------------
        let g7CoverageSrc (n: int) =
            $"type Ix = Idx<{n}>\n"
            + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7) - 2.0 |> compute\n"
            + "let l = method_for(x)\n"
            + "let (s1, s2, s3, s4) = reduce((l <@> lambda(v) -> v) <&!> (l <@> lambda(v) -> v * v) <&!> (l <@> lambda(v) -> v * v * v) <&!> (l <@> lambda(v) -> v * v * v * v), lambda(u, v) where omp -> u + v, 0.0)\n"
        for (name, n) in [ ("join_threaded_team_1009", 1009); ("join_threaded_team_clamped_n2", 2) ] do
            setOmpTestMode true
            let gen =
                use _r = pinEnv "BLADE_FP_REPRO" null
                use _j = pinEnv "BLADE_JOIN_THREADS" null
                use _p = pinEnv "BLADE_PRINT" "s1,s2,s3,s4"
                try
                    match lower (g7CoverageSrc n) with
                    | Error e -> Error ($"lower failed: {e}")
                    | Ok ir ->
                        let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir name
                        let srcAbs = Path.GetFullPath(Path.Combine(outputDir, sanitizeFileName name + ".cpp"))
                        File.WriteAllText(srcAbs, cpp)
                        Ok (srcAbs, cpp)
                with ex -> Error ($"codegen failed: {ex.Message}")
            setOmpTestMode false
            let outcome : Result<string, string> =
                match gen with
                | Error e -> Error e
                | Ok (_, cpp) when not (cpp.Contains "BLADE_OMP_SIMD_REDUCTION(+:__jp0_") ->
                    Error "the generated C++ has no threaded-join chunk sweep (the case would not test the form)"
                | Ok (srcAbs, _) ->
                    let exeAbs = Path.ChangeExtension(srcAbs, (if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"))
                    let cpsi = ProcessStartInfo("g++", $"-std=c++17 {(optFlags ())} -fopenmp -o \"{exeAbs}\" \"{srcAbs}\"")
                    cpsi.RedirectStandardError <- true
                    cpsi.UseShellExecute <- false
                    use cproc = Process.Start(cpsi)
                    let cerr = Blade.Runtime.readToEndOffPool cproc.StandardError
                    if not (cproc.WaitForExit(120000)) then
                        (try cproc.Kill(true) with _ -> ())
                        Error "compile timed out (120s)"
                    elif cproc.ExitCode <> 0 then Error ($"compile: {cerr.Result}")
                    else
                    let runLimited (threads: int) (limit: int option) =
                        let rpsi = ProcessStartInfo(exeAbs)
                        rpsi.RedirectStandardOutput <- true
                        rpsi.RedirectStandardError <- true
                        rpsi.UseShellExecute <- false
                        rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                        rpsi.Environment.["OMP_NUM_THREADS"] <- string threads
                        rpsi.Environment.["OMP_DYNAMIC"] <- "false"
                        (match limit with
                         | Some l -> rpsi.Environment.["OMP_THREAD_LIMIT"] <- string l
                         | None -> rpsi.Environment.Remove("OMP_THREAD_LIMIT") |> ignore)
                        use rproc = Process.Start(rpsi)
                        let rout = Blade.Runtime.readToEndOffPool rproc.StandardOutput
                        if not (rproc.WaitForExit(60000)) then
                            (try rproc.Kill(true) with _ -> ())
                            Error "run timed out (60s)"
                        elif rproc.ExitCode <> 0 then Error ($"exit {rproc.ExitCode}")
                        else
                            let out = rout.Result
                            let field (k: string) =
                                let m = System.Text.RegularExpressions.Regex.Match(out, k + "=(\\d+)")
                                if m.Success then Some (int m.Groups.[1].Value) else None
                            let sums =
                                out.Split('\n') |> Array.map (_.Trim()) |> Array.filter (fun l -> l.StartsWith "s") |> String.concat " "
                            Ok (field "teamsz", field "distinct", field "maxth", sums)
                    let runAt (threads: int) = runLimited threads None
                    // OMP_NUM_THREADS=8 under OMP_THREAD_LIMIT=3: the REQUESTED team
                    // (8, sized from omp_get_max_threads) exceeds the ACTUAL one
                    // (3): the partition must use the actual size, and the five
                    // unused partial slots (never published, `__jhad` false) must
                    // never enter the combine -- the sums must not move.
                    match runAt 1, runAt 3, runAt 8, runLimited 8 (Some 3) with
                    | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> Error e
                    | Ok (tz1, _, _, s1), Ok (tz3, d3, mx3, s3), Ok (tz8, d8, mx8, s8), Ok (tzL, dL, mxL, sL) ->
                        if s1 = "" || not (s1.Contains "s1 = ") then Error "no `s1 = ` line in the output"
                        elif s3 <> s1 || s8 <> s1 || sL <> s1 then Error ($"sums depend on the team: T=1 [{s1}] T=3 [{s3}] T=8 [{s8}] T=8/limit 3 [{sL}]")
                        else
                            match tz1, tz3, d3, mx3, tz8, d8, mx8, tzL, dL with
                            | Some tz1, Some tz3, Some d3, Some mx3, Some tz8, Some d8, Some mx8, Some tzL, Some dL ->
                                let want t = min t n
                                if mx3 <= 1 || mx8 <= 1 then Ok ($"single-core: maxth={mx3}/{mx8}, cannot test parallelism")
                                elif tz1 <> 1 then Error ($"T=1 team = {tz1}")
                                elif tz3 <> want 3 || tz8 <> want 8 then
                                    Error ($"team != min(T, n = {n}): T=3 teamsz={tz3}, T=8 teamsz={tz8}")
                                elif d3 <> want 3 || d8 <> want 8 then
                                    Error ($"not every worker swept a chunk: T=3 distinct={d3}, T=8 distinct={d8}")
                                elif tzL <> want 3 || dL <> want 3 then
                                    Error ($"OMP_THREAD_LIMIT=3: expected an actual team of {want 3} that all swept chunks, got teamsz={tzL} distinct={dL}")
                                else Ok ($"T=1/3/8 teamsz={tz1}/{tz3}/{tz8} distinct={d3}/{d8}; T=8 under OMP_THREAD_LIMIT=3: requested (max_threads) {(defaultArg mxL 0)}, teamsz={tzL} distinct={dL}; [{s1}] at every T")
                            | _ -> Error "unparseable or missing [omp-coverage] line"
            match outcome with
            | Ok detail ->
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name detail
                passed <- passed + 1
            | Error detail ->
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
                errors <- errors + 1
                failedNames <- failedNames @ [name]

        // -------------------------------------------------------------------
        // VALUE CORRECTNESS UNDER FORCED MULTI-THREADING (#2)
        // -------------------------------------------------------------------
        // The coverage checks above confirm a parallel region forms, but NOT
        // that the parallelized computation produces CORRECT values. A data
        // race in the triangular outer-parallelization (the disjoint-slab
        // assumption) would show as wrong values only under threading — which
        // neither the coverage checks nor the main value suite (default
        // threading) would catch. Here we run a symmetric computation with
        // KNOWN expected values under OMP_NUM_THREADS=4, repeated several times
        // (races are nondeterministic, so one run can pass by luck), and assert
        // the values are correct every time.
        //
        // N=12 symmetric: C(13,2)=78 elements, large enough that the scheduler
        // genuinely distributes the outer loop. Expected values computed here
        // (not hand-written): for comm(x,y)->x*y over A=[1..N], the left-
        // justified symmetric order is A[i]*A[j] for i<=j.
        let nVal = 12
        let aVals = [ for i in 1 .. nVal -> float i ]
        let expectedSym =
            [ for i in 0 .. nVal - 1 do
                for j in i .. nVal - 1 do
                    yield aVals.[i] * aVals.[j] ]
        let aLit = aVals |> List.map (sprintf "%g") |> String.concat ","
        let expectedLit = expectedSym |> List.map (sprintf "%g") |> String.concat ", "
        let valSrc =
            $"let A = Int64([{aLit}])\n" +
            "let L = method_for(A, A)\n" +
            // omp clause so this genuinely runs parallel under OMP_NUM_THREADS=4
            // — otherwise (post-flip) it would be serial and the env var inert,
            // defeating the race-detection purpose of the repeated runs.
            "let k = lambda(x, y) where comm(x, y), omp(x: 1) -> x * y\n" +
            "let result = L <@> k |> compute\n" +
            $"// EXPECT: result = [{expectedLit}]\n"
        printSubHeader "Value correctness under forced threading (N=12 symmetric)"
        setOmpTestMode false  // value test: no instrumentation, just real codegen
        let valOutcome =
            try
                match lower valSrc with
                | Error e -> Error ($"lower failed: {e}")
                | Ok ir0 ->
                    // Hard-fail on validation errors (was `| Error _ -> ir0`).
                    match IRValidate.validateIR ir0 with
                    | Error validationErrors ->
                        Error ($"""IR validation failed: {(String.concat "; " validationErrors)}""")
                    | Ok ir ->
                        let (cppCode, _w) = CodeGen.genSelfContainedProgramFromIR ir "omp_value_check"
                        let sf = Path.Combine(outputDir, "omp_value_check.cpp")
                        File.WriteAllText(sf, cppCode)
                        Ok (Path.GetFullPath sf)
            with ex -> Error ($"codegen failed: {ex.Message}")
        match valOutcome with
        | Error e -> Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail "omp_value_check" ($"generation: {e}"); errors <- errors + 1; failedNames <- failedNames @ ["omp_value_check"]
        | Ok srcAbs ->
            let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
            let exeAbs = Path.ChangeExtension(srcAbs, exeExt)
            let cpsi = ProcessStartInfo("g++", $"-std=c++17 {(optFlags ())} -fopenmp -o \"{exeAbs}\" \"{srcAbs}\"")
            cpsi.RedirectStandardError <- true
            cpsi.UseShellExecute <- false
            use cproc = Process.Start(cpsi)
            let cerr = Blade.Runtime.readToEndOffPool cproc.StandardError
            let cExited = cproc.WaitForExit(60000)
            if not cExited then
                (try cproc.Kill(true) with _ -> ())
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail "omp_value_check" "compile timed out (60s)"
                errors <- errors + 1
                failedNames <- failedNames @ ["omp_value_check"]
            elif cproc.ExitCode <> 0 then
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail "omp_value_check" ($"compile: {cerr.Result}")
                errors <- errors + 1
                failedNames <- failedNames @ ["omp_value_check"]
            else
                // Malformed-pin gate, same rule as Runner.fs: a `// EXPECT:`
                // line that does not parse is a DROPPED assertion, and
                // checkExpectedValues over an empty pin list returns Ok (),
                // so without this the whole race check could pass vacuously.
                let malformed = parseMalformedExpectLines valSrc
                let expected = parseExpectedValues valSrc
                let mutable allRunsOk = true
                if not malformed.IsEmpty then
                    allRunsOk <- false
                    printfn "    unparseable EXPECT pin(s): %s" (String.concat " | " malformed)
                elif expected.IsEmpty then
                    allRunsOk <- false
                    printfn "    no EXPECT pin parsed -- the value check would be vacuous"
                else
                    // Repeat: a race may pass on some runs and fail on others.
                    for run in 1 .. 5 do
                        let rpsi = ProcessStartInfo(exeAbs)
                        rpsi.RedirectStandardOutput <- true
                        rpsi.RedirectStandardError <- true
                        rpsi.UseShellExecute <- false
                        rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                        rpsi.Environment.["OMP_NUM_THREADS"] <- forcedThreads
                        use rproc = Process.Start(rpsi)
                        let rout = Blade.Runtime.readToEndOffPool rproc.StandardOutput
                        let rerrV = Blade.Runtime.readToEndOffPool rproc.StandardError
                        let rExited = rproc.WaitForExit(30000)
                        if not rExited then
                            (try rproc.Kill(true) with _ -> ())
                            rproc.WaitForExit(5000) |> ignore
                            allRunsOk <- false
                            printfn "    run %d: TIMED OUT (30s)" run
                        elif rproc.ExitCode <> 0 then
                            // A crashed run prints little or nothing; with no
                            // exit-code gate the value check over the truncated
                            // stdout was the only verdict.
                            allRunsOk <- false
                            printfn "    run %d: exit %d %s" run rproc.ExitCode (rerrV.Result.Trim())
                        else
                            match checkExpectedValues expected rout.Result with
                            | Ok () -> ()
                            | Error errs ->
                                allRunsOk <- false
                                printfn "    run %d: VALUE MISMATCH (possible race): %s" run (String.concat "; " errs)
                if allRunsOk then
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass "omp_value_check" ($"correct values across 5 runs under OMP_NUM_THREADS={forcedThreads}")
                    passed <- passed + 1
                else
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail "omp_value_check" "values incorrect under threading -- likely a data race in parallelization"
                    errors <- errors + 1
                    failedNames <- failedNames @ ["omp_value_check"]

        printFooter "OpenMP Coverage" [$"{passed} passed"; $"{errors} error(s)"; $"{warnings} warning(s)"]
        { Block = "OpenMP Coverage"; Passed = passed; Failed = errors; Skipped = 0; FailedNames = failedNames }

// ============================================================================
// Comm-licensed parallel reductions: VALUES under real threads
// ============================================================================
//
// The pragma block above answers "which emission path fired". This one answers
// the question no string check can: does the parallel fold compute what the
// serial fold computes, when a real OpenMP runtime really splits the axis?
//
// Everything here is a DIFFERENTIAL against the same program with the `omp`
// clause removed — not against hand-written expected values — so the oracle
// cannot drift from the feature. Two properties are asserted separately:
//
//   * VALUE, within 1e-9 of serial. Float tolerance lives HERE and nowhere
//     else: the corpus files for this feature (loops/110, loops/111) are also
//     run by the interpreter differential, which demands byte-identical output,
//     so they are restricted to integer-valued data where every association is
//     exact. This block is not part of that gate and may use awkward values,
//     which is the point — it is where reassociation is actually exercised.
//
//   * RUN-TO-RUN IDENTITY at a fixed OMP_NUM_THREADS, for Path B ONLY. Path B's
//     chunk boundaries and combine order are fixed functions of the team size,
//     so repeated runs reproduce bit-for-bit; Path A hands the combine to the
//     OpenMP runtime, whose order is unspecified by the standard, so asserting
//     it there would be pinning an implementation detail (a legal runtime could
//     fail it).
//
// Also here, and deliberately not a reduce: a COMPILED-AND-RUN case of
// `where omp(a: 1, b: 1)` on a 2-level dense map. `collapse(2)` fuses the two
// headers into one iteration space, and g++ rejects `#pragma GCC ivdep` on a
// header the construct owns ("loop not permitted in intervening code in OpenMP
// loop body" / "not enough nested loops"). The omp-pragma block asserts the
// pragma TEXT and never compiles it; the omp-coverage block compiles but with
// test-mode instrumentation, which disables ivdep by its own gate. Neither can
// see the interaction — so it gets a plain compile+run here.

/// A single compiled program: source -> exe path, ready to run. `testMode` is
/// forced OFF (the coverage instrumentation suppresses Phase 1's ivdep, which
/// is exactly what the collapse case below needs to exercise).
/// `compileProgram`, optionally with codegen TEST MODE on for the generation
/// (the [omp-coverage] team observation; G51's identity battery observes its
/// actual teams through it). Test mode is always switched off again.
let private compileLoweredModeWith (testMode: bool) (transform: string -> Result<string, string>) (outputDir: string) (name: string) (lowerIt: unit -> Result<IRProgram, string>) : Result<string, string> =
    try
        setOmpTestMode false
        match lowerIt () with
        | Error e -> Error ($"lower failed: {e}")
        | Ok ir0 ->
            match IRValidate.validateIR ir0 with
            | Error errs -> Error ($"""IR validation failed: {(String.concat "; " errs)}""")
            | Ok ir ->
                let (cppCode0, _w) =
                    setOmpTestMode testMode
                    try CodeGen.genSelfContainedProgramFromIR ir name
                    finally setOmpTestMode false
                match transform cppCode0 with
                | Error e -> Error e
                | Ok cppCode ->
                let srcAbs = Path.GetFullPath(Path.Combine(outputDir, sanitizeFileName name + ".cpp"))
                File.WriteAllText(srcAbs, cppCode)
                let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
                let exeAbs = Path.ChangeExtension(srcAbs, exeExt)
                let cpsi = ProcessStartInfo("g++", $"-std=c++17 {(optFlags ())} -fopenmp -o \"{exeAbs}\" \"{srcAbs}\"")
                cpsi.RedirectStandardError <- true
                cpsi.UseShellExecute <- false
                use cproc = Process.Start(cpsi)
                let cerr = Blade.Runtime.readToEndOffPool cproc.StandardError
                if not (cproc.WaitForExit(120000)) then
                    (try cproc.Kill(true) with _ -> ())
                    Error "compile timed out (120s)"
                elif cproc.ExitCode <> 0 then Error ($"compile: {(cerr.Result.Trim())}")
                else Ok exeAbs
    with ex -> Error ($"codegen raised: {ex.Message}")

let private compileProgramModeWith (testMode: bool) (transform: string -> Result<string, string>) (outputDir: string) (name: string) (src: string) : Result<string, string> =
    compileLoweredModeWith testMode transform outputDir name (fun () -> lower src)

/// `compileProgram` over a FILE-BACKED lowering: the source is written to
/// `<outputDir>/<name>.blade` and lowered with that file name, so spans carry
/// it and a run-time panic prints its `--> <file>:line:col` source line (the
/// File=None `lower` gives the C++ runtime a null file and no `-->` line).
let private compileProgramFileBacked (outputDir: string) (name: string) (src: string) : Result<string, string> =
    let path = Path.GetFullPath(Path.Combine(outputDir, sanitizeFileName name + ".blade"))
    File.WriteAllText(path, src)
    compileLoweredModeWith false Ok outputDir name (fun () ->
        match fst (Lowering.lowerDiag (Some path) src) with
        | Ok (ir, _) -> Ok ir
        | Error ds -> Error (ds |> List.map (fun d -> d.Message) |> String.concat "; "))

let private compileProgramMode (testMode: bool) (outputDir: string) (name: string) (src: string) : Result<string, string> =
    compileProgramModeWith testMode Ok outputDir name src

let private compileProgram (outputDir: string) (name: string) (src: string) : Result<string, string> =
    compileProgramMode false outputDir name src

/// The RAW-BIT probe of the scatter-precedence battery (round 3, W2-0a, Codex
/// diff review finding 1): `pokes` = (pool binding, [pool index, uint64 bit
/// pattern]) writes placed right before `anchor` (the first line of the first
/// fold, after every pool is computed), so every form -- scatter, row slots,
/// walk -- reads the SAME pool bytes; then, before main's `return 0;`, one
/// `RAW <name> <%016llx>` line per scalar binding (its exact uint64
/// representation: NaN payload and sign included).
let private rawBitTransform (anchor: string) (pokes: (string * (int * uint64) list) list) (scalars: string list) (cpp0: string) : Result<string, string> =
    let cpp = cpp0.Replace("\r\n", "\n")
    let at = cpp.IndexOf anchor
    let marker = "\n    return 0;\n    } catch"
    let endAt = cpp.IndexOf marker
    if at < 0 then Error ($"raw-bit probe: anchor `{anchor.Trim()}` not in the C++")
    elif endAt < 0 then Error "raw-bit probe: main's `return 0;` not found"
    elif at > endAt then Error "raw-bit probe: the anchor follows main's return"
    else
    let poke =
        pokes |> List.collect (fun (pool, cells) ->
            cells |> List.map (fun (ix, bits) ->
                $"    {{ double* __pb = const_cast<double*>(nested_array_utilities::pool_base({pool}.data)); const unsigned long long __u = 0x{bits:X16}ULL; std::memcpy(__pb + {ix}, &__u, 8); }}\n"))
        |> String.concat ""
    let probe =
        scalars
        |> List.map (fun s -> $"    {{ unsigned long long __u; const double __d = (double)({s}); std::memcpy(&__u, &__d, 8); std::printf(\"RAW {s} %%016llx\\n\", __u); }}")
        |> String.concat "\n"
    let lineStart = cpp.LastIndexOf('\n', at) + 1
    let head = "#include <cstdio>\n#include <cstring>\n"
    Ok (head + cpp.Substring(0, lineStart) + poke + cpp.Substring(lineStart, endAt - lineStart) + "\n" + probe + cpp.Substring(endAt))

/// Run a compiled program at a FIXED team: OMP_NUM_THREADS forced, dynamic
/// teams off (OMP_DYNAMIC=false), no OMP_THREAD_LIMIT inherited -- so the
/// runtime may not hand back a smaller team than requested (Codex diff review
/// of W1-A2, finding 3). Returns stdout.
let private runProgramFixedTeam (exeAbs: string) (threads: string) : Result<string, string> =
    let rpsi = ProcessStartInfo(exeAbs)
    rpsi.RedirectStandardOutput <- true
    rpsi.RedirectStandardError <- true
    rpsi.UseShellExecute <- false
    rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
    rpsi.Environment.["OMP_NUM_THREADS"] <- threads
    rpsi.Environment.["OMP_DYNAMIC"] <- "false"
    rpsi.Environment.Remove("OMP_THREAD_LIMIT") |> ignore
    use rproc = Process.Start(rpsi)
    let rout = Blade.Runtime.readToEndOffPool rproc.StandardOutput
    let rerr = Blade.Runtime.readToEndOffPool rproc.StandardError
    if not (rproc.WaitForExit(60000)) then
        (try rproc.Kill(true) with _ -> ())
        Error "run timed out (60s)"
    elif rproc.ExitCode <> 0 then
        Error ($"exit {rproc.ExitCode}: {(rerr.Result.Trim())}")
    else Ok rout.Result

/// Run a compiled program with OMP_NUM_THREADS forced, returning stdout.
let private runProgram (exeAbs: string) (threads: string) : Result<string, string> =
    let rpsi = ProcessStartInfo(exeAbs)
    rpsi.RedirectStandardOutput <- true
    rpsi.RedirectStandardError <- true
    rpsi.UseShellExecute <- false
    rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
    rpsi.Environment.["OMP_NUM_THREADS"] <- threads
    use rproc = Process.Start(rpsi)
    let rout = Blade.Runtime.readToEndOffPool rproc.StandardOutput
    let rerr = Blade.Runtime.readToEndOffPool rproc.StandardError
    if not (rproc.WaitForExit(60000)) then
        (try rproc.Kill(true) with _ -> ())
        Error "run timed out (60s)"
    elif rproc.ExitCode <> 0 then
        Error ($"exit {rproc.ExitCode}: {(rerr.Result.Trim())}")
    else Ok rout.Result

/// Every `name = value` scalar line of a program's stdout, as floats. The
/// auto-printer emits one per top-level binding, which is what the differential
/// compares.
let private scalarBindings (stdout: string) : Map<string, float> =
    stdout.Split('\n')
    |> Array.choose (fun line ->
        let m = System.Text.RegularExpressions.Regex.Match(
                    line.Trim(), @"^([A-Za-z_][A-Za-z0-9_]*) = (-?[0-9.eE+-]+)$")
        if m.Success then
            match System.Double.TryParse(m.Groups.[2].Value,
                                         System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture) with
            | true, v -> Some (m.Groups.[1].Value, v)
            | _ -> None
        else None)
    |> Map.ofArray

// ============================================================================
// G7: THE THREADED REDUCTION JOIN (generalized Path B over one shared sweep)
// ============================================================================
// Policy (round-2 arbitration Q2): a team iff an EXPLICIT request (the join's
// fold kernel, a leg's fold kernel, or a leaf map covering the outer level)
// AND every leg's OWN reorder licence AND the shared-traversal shape/purity
// checks. Integer-valued x(i) = (i mod 7) - 2 over a non-power-of-two 1009:
// every sum is exact under any association, so the threaded and serial
// values are compared EXACTLY.

/// The onepass shape: four power sums through the fused tree (common kernel).
let private g7FusedSrc (foldKernel: string) =
    "type Ix = Idx<1009>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7) - 2.0 |> compute\n"
    + "let l = method_for(x)\n"
    + "let (s1, s2, s3, s4) = reduce((l <@> lambda(v) -> v) <&!> (l <@> lambda(v) -> v * v) <&!> (l <@> lambda(v) -> v * v * v) <&!> (l <@> lambda(v) -> v * v * v * v), "
    + foldKernel + ", 0.0)\n"
    + "let checksum = s1 + 2.0 * s2 + 3.0 * s3 + 4.0 * s4\n"

/// The per-leg pack shape with a SHARED deferred producer `e` (spelled by two
/// legs) and per-leg seeds 10 / 0 / 30. `leg0` is the first leg's fold kernel.
let private g7PackSrc (leg0: string) (extraLeg: string) =
    "type Ix = Idx<1009>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7) - 2.0 |> compute\n"
    + "let e = method_for(x) <@> lambda(v) -> v * 0.5 + 1.0\n"
    + (if extraLeg = "" then "let (a, b, c) = object_for(<&!>) <@> (reduce(e, " + leg0 + ", 10.0), prodsum(e, x), reduce(x, (+), 30.0))\n"
       else "let (a, b, c, d) = object_for(<&!>) <@> (reduce(e, " + leg0 + ", 10.0), prodsum(e, x), reduce(x, (+), 30.0), " + extraLeg + ")\n")

/// PRODUCT legs with non-identity caller seeds: x(i) = 2^((i mod 3) - 1) over
/// 1009 cells (0.5, 1, 2 repeating; exact in binary), so prod x = 0.5 and
/// prod x^2 = 0.25 exactly under any association.
let private g7ProductPrelude =
    "type Ix = Idx<1009>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(2 ^ (Int64(i) % 3)) * 0.5 |> compute\n"
let private g7ProductPackSrc =
    g7ProductPrelude
    + "let e = method_for(x) <@> lambda(v) -> v * v\n"
    + "let (p1, p2) = object_for(<&!>) <@> (reduce(x, lambda(p, q) where omp -> p * q, 2.0), reduce(e, (*), 3.0))\n"
let private g7ProductFusedSrc =
    g7ProductPrelude
    + "let l = method_for(x)\n"
    + "let (f1, f2) = reduce((l <@> lambda(v) -> v) <&!> (l <@> lambda(v) -> v * v), lambda(u, v) where omp -> u * v, 2.0)\n"
/// A ZERO-extent seeded join: no team opens, every leg is its seed.
let private g7ZeroSrc =
    "type Z = Idx<0>\n"
    + "let x = method_for(range<Z>) <@> lambda(i) -> Float64(Int64(i)) + 1.0 |> compute\n"
    + "let l = method_for(x)\n"
    + "let (z1, z2) = reduce((l <@> lambda(v) -> v) <&!> (l <@> lambda(v) -> v * v), lambda(u, v) where omp -> u + v, 10.0)\n"
    + "let (y1, y2) = object_for(<&!>) <@> (reduce(x, lambda(p, q) where omp -> p + q, Float64(20.0)), reduce(x, (+), Float64(30.0)))\n"

let private g7Count (needle: string) (s: string) =
    let mutable n = 0
    let mutable i = s.IndexOf(needle, StringComparison.Ordinal)
    while i >= 0 do
        n <- n + 1
        i <- s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)
    n

let private g7Rx (pattern: string) (s: string) =
    System.Text.RegularExpressions.Regex.Matches(s, pattern).Count

/// Gap G43 (W2-F), re-pinning three W2-E cases that encoded the OLD policy (a
/// writing join leg declined to the serial join): a join defines no order for
/// a leg's observable write, so lowering REFUSES it in every lane (BL4005),
/// before any backend gate runs. (name, source).
let private g7RefusedCases : (string * string) list =
    let omp = "lambda(u, v) where omp -> u + v"
    [ // a serial captured write in a leg's map (corpus mutability-errors/021)
      ("g7_fused_join_serial_captured_write_refused",
       "let mut counter = 0\nlet x = [1.0, 2.0, 3.0]\nlet l = method_for(x)\n"
       + "let (s1, s2) = reduce((l <@> lambda(v) -> {\n    counter = counter + 1\n    v\n}) <&!> (l <@> lambda(v) -> v * v), " + omp + ", 0.0)\n")
      // a WRITING SHARED PRODUCER through a transitive helper (per-leg pack)
      ("g7_pack_join_writing_shared_producer_refused",
       "let mut counter: Int64 = 0\nfunction bump(v: Float64) -> Float64 = {\n    counter = counter + 1\n    v\n}\n"
       + "type Ix = Idx<1009>\n"
       + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7) |> compute\n"
       + "let e = method_for(x) <@> lambda(v) -> bump(v)\n"
       + "let (a, b, c) = object_for(<&!>) <@> (reduce(e, lambda(p, q) where omp -> p + q, Float64(10.0)), prodsum(e, x), reduce(x, (+), Float64(30.0)))\n")
      // a comm-declared WRITING custom fold beside a requesting leg
      ("g7_pack_join_comm_writing_custom_fold_refused",
       "let mut counter: Int64 = 0\nlet x = Float64([1.0, 2.0, 3.0])\n"
       + "let (a, b) = object_for(<&!>) <@> (reduce(x, lambda(p, q) where omp -> p + q, Float64(0.0)), reduce(x, lambda(p, q) where comm(p, q) -> {\n    counter = counter + 1\n    p + q\n}, Float64(0.0)))\n") ]
/// The emission + record cases. (name, pins, source, check over (decisions, C++)).
let private g7EmitCases : (string * (string * string) list * string * (Blade.Effects.Decision list -> string -> Result<string, string>)) list =
    let team = "BLADE_OMP_PARALLEL_NUM_THREADS("
    let omp = "lambda(u, v) where omp -> u + v"
    let appliedOmp (ds: Blade.Effects.Decision list) (prov: string) =
        ds |> List.exists (fun d ->
            d.Rule = "omp" && d.Outcome = Blade.Effects.Applied
            && d.Evidence |> List.exists (fun ev -> ev.Contains "Path B joined chunks, G7" && ev.Contains prov))
    let foldForm (ds: Blade.Effects.Decision list) (form: string) =
        ds |> List.exists (fun d -> d.Rule = "fold-form" && d.Evidence |> List.exists (fun ev -> ev.Contains form))
    let declinedOmp (ds: Blade.Effects.Decision list) (why: string) =
        ds |> List.exists (fun d ->
            d.Rule = "omp" && (match d.Outcome with Blade.Effects.Declined r -> r.Contains why | _ -> false))
    let noDotCalls (cpp: string) =
        not (cpp.Contains "blade_fold::dot<" || cpp.Contains "blade_fold::sq<" || cpp.Contains "blade_fold::sum<" || cpp.Contains "blade_fold::dot3<")
    // Threads on, licensed (the default): ONE team, ONE sweep, one load per
    // coordinate, four identity-seeded privates under ONE simd clause, the
    // chunk_lo partition at the ACTUAL team size, the fixed combine, source-
    // order products, no K-lane dot call.
    let fusedTeam (ds: Blade.Effects.Decision list) (cpp: string) =
        let checks =
            [ g7Count team cpp = 1, $"teams = {g7Count team cpp}, expected 1"
              g7Rx @"for \(size_t __i0 = __rlo; __i0 < __rhi; __i0\+\+\)" cpp = 1, "expected exactly one chunk sweep"
              g7Rx @"= __jsrc0_\w+\[__i0\];" cpp = 1, "expected one load of x per coordinate"
              g7Rx @"double __jp\d_\w+ = 0;" cpp = 4, "expected exactly 4 identity-seeded private accumulators"
              g7Rx @"BLADE_OMP_SIMD_REDUCTION\(\+:__jp0_\w+, __jp1_\w+, __jp2_\w+, __jp3_\w+\)" cpp = 1, "expected ONE simd clause over the four privates"
              g7Count "blade_fold::chunk_lo(" cpp = 2, "expected the chunk_lo partition (lo, hi)"
              cpp.Contains "const int __rnt = blade_omp_get_num_threads();", "expected the actual team size"
              g7Rx @"if \(__jhad_\w+\[__rt\]\) \{ \w+_0 = \w+\(\w+_0, __jpart0_\w+\[__rt\]\); \w+_1 = \w+\(\w+_1, __jpart1_\w+\[__rt\]\); \w+_2 = \w+\(\w+_2, __jpart2_\w+\[__rt\]\); \w+_3 = \w+\(\w+_3, __jpart3_\w+\[__rt\]\); \}" cpp = 1,
                  "expected the fixed ascending-chunk combine of legs 0..3 onto their seeded accumulators"
              g7Rx @"__jp3_\w+ = __jp3_\w+ \+ \(\(\(\((\w+) \* \1\) \* \1\) \* \1\)\);" cpp = 1, "expected v*v*v*v in source order ((v*v)*v)*v"
              noDotCalls cpp, "a K-lane dot call appeared in the joined kernel"
              foldForm ds "threaded(Path B joined chunks, 4 builtin + legs, simd-reduction within each chunk)", "expected the threaded fold-form record"
              appliedOmp ds "the join's fold kernel", "expected one applied omp record naming the fold-kernel request"
              not (cpp.Contains "requested but emitted serial"), "a census line remained beside the applied team" ]
        match checks |> List.tryFind (fst >> not) with
        | Some (_, why) -> Error why
        | None -> Ok "one team, one sweep, one load/coordinate, 4 privates, one simd clause, chunk_lo + actual team, fixed combine, source-order powers, no K-lane call"
    let packTeam (ds: Blade.Effects.Decision list) (cpp: string) =
        let region = let k = cpp.IndexOf "for (size_t __i0 = __rlo" in if k < 0 then "" else cpp.Substring k
        let checks =
            [ g7Count team cpp = 1, "expected one team"
              g7Count "const double e = " cpp = 1, "expected exactly one evaluation of the shared producer per coordinate"
              region.Contains "const double e = ", "the shared producer is not evaluated inside the worker's sweep"
              g7Rx @"= __jsrc0_\w+\[__i0\];" cpp = 1, "expected one load of x per coordinate"
              g7Rx @"double __jp\d_\w+ = 0;" cpp = 3, "expected 3 private accumulators"
              cpp.Contains "_0 = 10.0;" && cpp.Contains "_2 = 30.0;", "the original seeds are not the accumulators' initializers"
              not (g7Rx @"__jp\d_\w+ = 10\.0" cpp > 0 || g7Rx @"__jp\d_\w+ = 30\.0" cpp > 0), "a caller seed entered a private partial"
              appliedOmp ds "leg 0's fold kernel", "expected the applied omp record naming leg 0's request" ]
        match checks |> List.tryFind (fst >> not) with
        | Some (_, why) -> Error why
        | None -> Ok "one team, the shared producer once per coordinate inside the sweep, seeds outside the partials"
    // No team, the census line naming `why`, and (when `simd`) the one-sweep
    // vectorized join kept; (when not) no simd clause either.
    let declined (why: string) (simd: bool) (ds: Blade.Effects.Decision list) (cpp: string) =
        if cpp.Contains team then Error "a team was emitted"
        elif not (cpp.Contains ("[omp] requested but emitted serial: " + why) || cpp.Contains why) then Error ($"no census line naming `{why}`")
        elif not (declinedOmp ds why) then Error ($"no declined omp record naming `{why}`")
        elif simd && not (cpp.Contains "BLADE_OMP_SIMD_REDUCTION(") then Error "the serial join lost its vectorized form"
        elif not simd && cpp.Contains "BLADE_OMP_SIMD_REDUCTION(" then Error "a simd clause appeared (the serial chains must stay ordered)"
        else Ok ($"no team; census + record name `{why}`")
    [ ("g7_fused_join_team_shape", [], g7FusedSrc omp, fusedTeam)
      ("g7_pack_join_team_shared_producer", [], g7PackSrc "lambda(p, q) where omp -> p + q" "", packTeam)
      ("g7_fused_join_threads_off_census", [ ("BLADE_OMP_THREADS", "1") ], g7FusedSrc omp, declined "BLADE_OMP_THREADS=1" true)
      ("g7_pack_join_threads_off_census", [ ("BLADE_OMP_THREADS", "1") ], g7PackSrc "lambda(p, q) where omp -> p + q" "", declined "BLADE_OMP_THREADS=1" true)
      ("g7_fused_join_repro_census", [ ("BLADE_FP_REPRO", "1") ], g7FusedSrc omp, declined "BLADE_FP_REPRO=1" false)
      ("g7_pack_join_repro_census", [ ("BLADE_FP_REPRO", "1") ], g7PackSrc "lambda(p, q) where omp -> p + q" "", declined "BLADE_FP_REPRO=1" false)
      ("g7_fused_join_knob_off_census", [ ("BLADE_JOIN_THREADS", "0") ], g7FusedSrc omp, declined "BLADE_JOIN_THREADS=0" true)
      // one leg unlicensed: never borrows the others' licences
      ("g7_pack_join_unlicensed_leg_declines", [], g7PackSrc "lambda(p, q) where omp -> p + q" "reduce(x, lambda(p, q) -> p + 2.0 * q, 0.0)",
       declined "leg 3's fold kernel has no reorder licence" false)
      // one leg repro-vetoed (`where repro` named function)
      ("g7_pack_join_repro_leg_declines", [],
       "function rsum(a: Float64, b: Float64) where repro -> Float64 = a + b\n" + g7PackSrc "lambda(p, q) where omp -> p + q" "reduce(x, rsum, 0.5)",
       declined "leg 3's fold kernel is `where repro`" false)
      // no request: the builtin-+ join stays serial (policy), no census line
      ("g7_fused_join_no_request_stays_serial", [], g7FusedSrc "(+)",
       (fun ds cpp ->
            if cpp.Contains team then Error "a builtin-+ join without a request opened a team"
            elif cpp.Contains "requested but emitted serial" then Error "a census line without a request"
            elif not (cpp.Contains "BLADE_OMP_SIMD_REDUCTION(") then Error "the serial vectorized join was lost"
            else Ok "no request: one vectorized sweep, no team, no census"))
      // a leaf map's request covering the outer level selects the traversal
      ("g7_fused_join_leaf_map_request", [],
       "type Ix = Idx<1009>\n"
       + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7) - 2.0 |> compute\n"
       + "let l = method_for(x)\n"
       + "let (s1, s2) = reduce((l <@> lambda(v) where omp -> v) <&!> (l <@> lambda(v) -> v * v), (+))\n",
       (fun ds cpp ->
            if g7Count team cpp <> 1 then Error "expected one team"
            elif not (appliedOmp ds "a leaf map's `omp(...)` covering the outer level") then Error "expected the applied record naming the leaf-map request"
            elif cpp.Contains "requested but emitted serial" then Error "a census line beside the applied team"
            else Ok "the leaf map's outer-level request threads the join"))
      // ---- the PURITY / SHAPE refusal boundary, both entry shapes --------
      // (A WRITING leg -- map, shared producer, or fold kernel -- is no longer a
      // backend decline: since gap G43 lowering refuses it, BL4005; see
      // g7RefusedCases.)
      // An UNPROVEN subscript (an arithmetic coordinate, BL8006 at run time):
      // a team would race the failing coordinate -- refused.
      ("g7_fused_join_unproven_subscript_declines", [],
       "type Ix = Idx<1009>\n"
       + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7) |> compute\n"
       + "let r = method_for(range<Ix>)\n"
       + "let (s1, s2) = reduce((r <@> lambda(i) -> x(Int64(i) + 1)) <&!> (r <@> lambda(i) -> x(i) * x(i)), " + omp + ", 0.0)\n",
       declined "a subscript not proven in range: BL8006" true)
      // PRODUCT legs: the private partials start from the identity 1, the
      // caller seeds 2.0 / 3.0 enter only the shared accumulators.
      ("g7_pack_join_product_legs_identity_one", [], g7ProductPackSrc,
       (fun ds cpp ->
            if g7Count team cpp <> 1 then Error "expected one team"
            elif g7Rx @"double __jp\d_\w+ = 1;" cpp <> 2 then Error "expected two private partials seeded with the identity 1"
            elif g7Rx @"BLADE_OMP_SIMD_REDUCTION\(\*:__jp0_\w+, __jp1_\w+\)" cpp <> 1 then Error "expected one `*` simd clause over the privates"
            elif not (cpp.Contains "_0 = 2.0;" && cpp.Contains "_1 = 3.0;") then Error "the caller seeds are not the shared accumulators' initializers"
            elif g7Rx @"__jp\d_\w+ = [23]\.0" cpp > 0 then Error "a caller seed entered a private partial"
            elif not (foldForm ds "threaded(Path B joined chunks, 2 builtin * legs") then Error "expected the threaded product join"
            else Ok "product legs: privates from 1, seeds 2.0 / 3.0 once, one `*` clause"))
      // A range-driven join whose legs subscript a captured array by the
      // range's own index: the subscript is PROVEN in range (the audit binds
      // the parameter to its `range<Ix>` level), so the purity/totality
      // whitelist admits it and the join threads.
      ("g7_range_driven_join_proven_subscript", [],
       "type Ix = Idx<1009>\n"
       + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7) - 2.0 |> compute\n"
       + "let r = method_for(range<Ix>)\n"
       + "let (s1, s2) = reduce((r <@> lambda(i) -> x(i)) <&!> (r <@> lambda(i) -> x(i) * x(i)), lambda(u, v) where omp -> u + v, 0.0)\n",
       (fun ds cpp ->
            if g7Count team cpp <> 1 then Error "expected one team"
            elif not (foldForm ds "threaded(Path B joined chunks, 2 builtin + legs") then Error "expected the threaded two-leg join"
            elif cpp.Contains "requested but emitted serial" then Error "a census line beside the applied team"
            else Ok "proven subscripts admitted; one team"))
      // Two licensed sibling folds of one array in a top-level tuple, one of
      // them requesting omp: the sibling-fold pass joins them (every leg is
      // licensed) and the join threads under the requesting leg's own clause.
      ("g7_sibling_tuple_components_join_and_thread", [],
       "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0])\n"
       + "let (a, b) = (reduce(A, (+)), reduce(A, lambda(p, q) where omp -> p + q, 0.0))\n",
       (fun ds cpp ->
            if g7Count team cpp <> 1 then Error "expected one team for the joined pair"
            elif not (foldForm ds "threaded(Path B joined chunks, 2 builtin + legs") then Error "expected the threaded two-leg join"
            elif not (appliedOmp ds "leg 1's fold kernel") then Error "expected the applied record naming leg 1's own request"
            elif cpp.Contains "requested but emitted serial" then Error "a census line beside the applied team"
            else Ok "joined by the sibling-fold pass; one team under leg 1's own request"))
      // BLADE_FOLD_LANES=off: a scalar private chain per chunk, the team kept
      ("g7_fused_join_fold_lanes_off_keeps_team", [ ("BLADE_FOLD_LANES", "off") ], g7FusedSrc omp,
       (fun ds cpp ->
            if g7Count team cpp <> 1 then Error "BLADE_FOLD_LANES=off suppressed the team"
            elif cpp.Contains "BLADE_OMP_SIMD_REDUCTION(" then Error "a simd clause under BLADE_FOLD_LANES=off"
            elif g7Rx @"__jp3_\w+ = __jp3_\w+ \+ \(\(\(\((\w+) \* \1\) \* \1\) \* \1\)\);" cpp <> 1 then Error "the powers' parenthesization changed"
            elif not (foldForm ds "serial within each chunk: BLADE_FOLD_LANES=0/off") then Error "expected the knob's fold-form record"
            else Ok "team kept, scalar private chains, source-order powers")) ]

/// Comm-licensed parallel reduction VALUE tests. Requires g++; skips otherwise.
let runOmpReduceTests () : Blade.Tests.TestHarness.BlockResult =
    let caps = capabilities.Value
    printHeader "OpenMP Comm-Licensed Reductions"
    // Pinned unset for the block: every differential below compares an
    // `omp`-licensed build against its serial twin, and a global
    // BLADE_OMP_THREADS=1 would make both sides the SAME serial program --
    // every arm would pass while measuring nothing. The knob's own arm at the
    // end re-pins it for its own scope.
    use _ompThreadsPin = pinOmpThreadsUnset ()
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile the -fopenmp reduction programs)."
        { Block = "OpenMP Reduce"; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
        let outputDir = "./generated_omp_reduce"
        Directory.CreateDirectory(outputDir) |> ignore
        CodeGen.deployRuntimeHeaders outputDir
        let forcedThreads = "4"
        let mutable passed = 0
        let mutable failed = 0
        let mutable failedNames = []
        let fail name detail =
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
        let pass name detail =
            passed <- passed + 1
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name detail

        // Awkward, NON-integer data: reassociation is only observable when the
        // partial sums are not exact. 240 elements so a 4-thread split gives
        // four genuinely different chunks.
        let n = 240
        let vals = [ for i in 1 .. n -> 1.0 / float i + float (i % 7) * 0.3 ]
        let aLit = vals |> List.map (_.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) |> String.concat ", "
        let arrDecl = $"let A = Float64([{aLit}])\n"
        // A second array for the deferred-computation (2-level nest) case; kept
        // short so the fused nest stays a quick compile.
        let bVals = [ for i in 1 .. 9 -> 0.5 + 1.0 / float (i + 2) ]
        let bLit = bVals |> List.map (_.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) |> String.concat ", "
        let bDecl = $"let B = Float64([{bLit}])\n"
        let commFn = "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n"
        let serialFn = "function myAdd(a: Float64, b: Float64) where comm(a, b) = (a + b) * 1.0\n"
        // Compound (masked) operand: reduce walks the flat `.data` buffer of
        // present cells, which is the OTHER contiguous 1-D sweep Path A covers.
        let maskDecl = "let m = mask(A, lambda(x) -> x > 1.0)\nlet C = compound(A, m)\n"
        // A fold over a closed-form domain of n x n with conjunct `conj`, its
        // kernel the bench band's coefficient on non-integer data.
        let domainSrc (n: int) (w: int) (conj: string) (foldKernel: string) =
            $"let static n: Int64 = {n}\nlet static w: Int64 = {w}\ntype Pts = Idx<n>\n"
            + "static struct D {\n    i: Int64<min=0, max=n-1>,\n    j: Int64<min=0, max=n-1>\n} where " + conj + "\n"
            + "let x = 1.0 / (1.0 + Float64(0..n))\n"
            + "function phi(i: Int64, j: Int64) -> Float64 = {\n    let d = Float64(i - j)\n    (2.0 + d / Float64(w + 1)) / (1.0 + d * d)\n}\n"
            + $"let s = reduce(method_for(range<D>) <@> lambda(i, j) -> phi(i, j) * x((i : Pts)) * x((j : Pts)), {foldKernel}, 0.0)\n"

        // The orbit-fold programs, parameterized by the OUTER fold kernel.
        let orbitSym2 (outer: string) =
            "type Var = Idx<43>\ntype Obs = Idx<3>\n"
            + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 7 + s * 3) % 11) * 0.37 + 0.1 / Float64(i + 1) |> compute\n"
            + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
            + $"let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j) * g(i, j) + 0.5, (+)), {outer}, 0.25)\n"
        let orbitAnti3 (outer: string) =
            "type I = Idx<43>\n"
            + "let w = method_for(range<I>) <@> lambda(i) -> Float64((i * 7) % 13) * 0.31 + 0.2 / Float64(i + 1) |> compute\n"
            + "let R = method_for(w, w, w) <@> lambda(a, b, c) where anticomm(a, b, c) -> (a - b) * (b - c) * (a - c) |> compute\n"
            + $"let s = reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> reduce(method_for(range<I>) <@> lambda(k) -> R(i, j, k) * R(i, j, k) * R(i, j, k) + R(i, j, k) * R(i, j, k), (+)), (+)), {outer}, 0.0)\n"
        // (name, ompSource, serialSource, pathB?)
        let cases : (string * string * string * bool) list =
            [ ("path_a_builtin_sum_dense",
               arrDecl + "let s = reduce(A, lambda(a, b) where omp -> a + b)\n",
               arrDecl + "let s = reduce(A, lambda(a, b) -> a + b)\n",
               false)
              ("path_a_builtin_sum_compound",
               arrDecl + maskDecl + "let s = reduce(C, lambda(a, b) where omp -> a + b)\n",
               arrDecl + maskDecl + "let s = reduce(C, lambda(a, b) -> a + b)\n",
               false)
              ("path_b_named_comm_function",
               commFn + arrDecl + "let s = reduce(A, myAdd)\n",
               serialFn + arrDecl + "let s = reduce(A, myAdd)\n",
               true)
              ("path_b_named_comm_with_init",
               commFn + arrDecl + "let s = reduce(A, myAdd, 100.0)\n",
               serialFn + arrDecl + "let s = reduce(A, myAdd, 100.0)\n",
               true)
              ("path_b_inline_comm_lambda",
               arrDecl + "let s = reduce(A, lambda(a, b) where comm(a, b), omp -> (a + b) * 1.0)\n",
               arrDecl + "let s = reduce(A, lambda(a, b) where comm(a, b) -> (a + b) * 1.0)\n",
               true)
              // `axes = 2`: the full fold to the scalar `s` this case compares.
              // The default (innermost axis only) would bind `s` to a rank-1
              // array and the comparison would have nothing to read.
              ("path_b_reduce_over_computation",
               commFn + bDecl + "let s = reduce(method_for(B, B) <@> lambda(x, y) -> x * y, myAdd, 0.0, axes = 2)\n",
               serialFn + bDecl + "let s = reduce(method_for(B, B) <@> lambda(x, y) -> x * y, myAdd, 0.0, axes = 2)\n",
               true)
              // The licence on the MAP feeding a builtin fold (bench gap G27):
              // the map's `omp(x: 1)` covers the level Path B chunks.
              ("path_b_map_licence_feeding_builtin_fold",
               arrDecl + "let s = reduce(method_for(A) <@> lambda(x) where omp(x: 1) -> x * x + 0.25, (+))\n",
               arrDecl + "let s = reduce(method_for(A) <@> lambda(x) -> x * x + 0.25, (+))\n",
               true)
              // THE THREADED ORBIT-CLASS FOLD (bench G32): `where omp` on the
              // outer fold of a logical walk over a packed pool deals the
              // first-coordinate rows to the team; the twin without `omp` is
              // the serial orbit pass. 43 rows over 4 threads: several
              // boustrophedon blocks and a ragged last one. Rank 2 symmetric
              // and rank 3 strict with a general kernel (two accumulators).
              // Fixed-team determinism is checked like Path B's.
              ("orbit_fold_threaded_sym2",
               orbitSym2 "lambda(u, v) where omp -> u + v", orbitSym2 "(+)", true)
              ("orbit_fold_threaded_antisym3_general",
               orbitAnti3 "lambda(u, v) where omp -> u + v", orbitAnti3 "(+)", true)
              // A CLOSED-FORM DOMAIN fold (bench gap G31): one team, the
              // outermost enumerated variable in fixed per-thread blocks, a
              // fixed-order combine -- deterministic like Path B. The band walks
              // by diagonal (k outer); w = 1 gives 3 diagonals, fewer than the
              // 4-thread team, so each diagonal's run is the thing cut; the
              // triangle is not a band and threads its lex nest's first field.
              ("domain_band_diagonal_threaded",
               domainSrc 997 13 "abs(i - j) <= w" "lambda(p, q) where omp -> p + q",
               domainSrc 997 13 "abs(i - j) <= w" "lambda(p, q) -> p + q",
               true)
              ("domain_band_fewer_diagonals_than_team",
               domainSrc 997 1 "abs(i - j) <= w" "lambda(p, q) where omp -> p + q",
               domainSrc 997 1 "abs(i - j) <= w" "lambda(p, q) -> p + q",
               true)
              ("domain_triangle_lex_threaded",
               domainSrc 401 0 "i < j" "lambda(p, q) where omp -> p + q",
               domainSrc 401 0 "i < j" "lambda(p, q) -> p + q",
               true) ]

        for (name, ompSrc, serialSrc, isPathB) in cases do
            match compileProgram outputDir (name + "_omp") ompSrc,
                  compileProgram outputDir (name + "_serial") serialSrc with
            | Error e, _ -> fail name ($"omp build: {e}")
            | _, Error e -> fail name ($"serial build: {e}")
            | Ok ompExe, Ok serialExe ->
                match runProgram ompExe forcedThreads, runProgram serialExe "1" with
                | Error e, _ -> fail name ($"omp run: {e}")
                | _, Error e -> fail name ($"serial run: {e}")
                | Ok ompOut, Ok serialOut ->
                    let ompVals = scalarBindings ompOut
                    let serVals = scalarBindings serialOut
                    // `s` is the fold's binding in every case above. Its absence
                    // would make the comparison vacuous, so it is checked.
                    match Map.tryFind "s" ompVals, Map.tryFind "s" serVals with
                    | Some pv, Some sv ->
                        let diff = abs (pv - sv)
                        let tol = 1e-9 * max 1.0 (abs sv)
                        if diff > tol then
                            fail name (sprintf "parallel %.17g vs serial %.17g (|diff| = %g)" pv sv diff)
                        elif not isPathB then
                            pass name (sprintf "value matches serial (|diff| = %g)" diff)
                        else
                            // Path B determinism: a second run at the same team
                            // size must reproduce the first byte for byte.
                            match runProgram ompExe forcedThreads with
                            | Error e -> fail name ($"second omp run: {e}")
                            | Ok again ->
                                let strip (s: string) =
                                    s.Split('\n')
                                    |> Array.filter (fun l -> not (l.Contains "completed in"))
                                    |> String.concat "\n"
                                if strip again <> strip ompOut then
                                    fail name "run-to-run output differs at fixed OMP_NUM_THREADS=4 (chunking is not deterministic)"
                                else
                                    pass name (sprintf "value matches serial (|diff| = %g); identical across 2 runs" diff)
                    | _ ->
                        fail name "no scalar binding 's' in program output (comparison would be vacuous)"

        // ---- Round C: lane-boundary battery -------------------------------
        // Path B's chunk is now swept by K strided lane accumulators (K fixed
        // at codegen; CodeGen.foldLaneCount). Every arm of that sweep has an
        // element-count boundary, and getting any of them wrong silently drops
        // or double-counts elements — the fold still prints A number, just the
        // wrong one. So the battery walks n across all of them:
        //
        //   n < K            -> the short-chunk arm (no lanes at all)
        //   n = K            -> lanes seeded, main loop and tail both empty
        //   n = K + 1        -> one tail element, landing on lane 0
        //   n = 2K - 1       -> maximal tail (K-1 elements, lanes 0..K-2)
        //   n not div by K   -> main loop plus a partial tail
        //   n < thread count -> chunks smaller than K under a 4-thread request
        //
        // K is deliberately NOT read from the compiler here: the battery is
        // written to straddle any K up to 16, so it keeps its meaning if the
        // constant is retuned. Values are non-integer (reassociation is real)
        // and the oracle is the same program with `omp` dropped, at 1e-9.
        //
        // An INTEGER-data twin runs at n = 2K-1 and demands EXACT equality —
        // that is the property the corpus files (loops/110, loops/111) and the
        // interpreter differential rely on, so it is pinned here directly
        // rather than inferred.
        let laneKernelOmp = "function laneAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n"
        let laneKernelSer = "function laneAdd(a: Float64, b: Float64) where comm(a, b) = (a + b) * 1.0\n"
        let laneLit (count: int) (f: int -> float) =
            // A decimal point is forced: an integer-valued literal printed by
            // "R" comes out as `1`, which the checker reads as Int64 and then
            // refuses against the Float64 kernel.
            [ for i in 1 .. count ->
                let s = (f i).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                if s.Contains "." || s.Contains "E" then s else s + ".0" ]
            |> String.concat ", "
        let awkward i = 1.0 / float i + float (i % 5) * 0.7
        let laneCases : (string * int * bool) list =
            // (label, n, integerData)
            [ ("n1_below_lanes",        1,  false)
              ("n3_below_lanes",        3,  false)
              ("n4_at_lanes",           4,  false)
              ("n5_lanes_plus_one",     5,  false)
              ("n7_maximal_tail",       7,  false)
              ("n8_two_full_lanes",     8,  false)
              ("n15_maximal_tail_k8",  15,  false)
              ("n17_indivisible",      17,  false)
              ("n3_below_thread_count", 3,  false)
              ("n33_indivisible",      33,  false)
              ("n15_integer_exact",    15,  true) ]
        for (label, n, integerData) in laneCases do
            let name = "lane_boundary_" + label
            let lit = laneLit n (fun i -> if integerData then float (i % 9 + 1) else awkward i)
            let decl = $"let A = Float64([{lit}])\n"
            let body = "let s = reduce(A, laneAdd)\n"
            match compileProgram outputDir (name + "_omp") (laneKernelOmp + decl + body),
                  compileProgram outputDir (name + "_serial") (laneKernelSer + decl + body) with
            | Error e, _ -> fail name ($"omp build: {e}")
            | _, Error e -> fail name ($"serial build: {e}")
            | Ok ompExe, Ok serialExe ->
                match runProgram ompExe forcedThreads, runProgram serialExe "1" with
                | Error e, _ -> fail name ($"omp run: {e}")
                | _, Error e -> fail name ($"serial run: {e}")
                | Ok ompOut, Ok serialOut ->
                    match Map.tryFind "s" (scalarBindings ompOut), Map.tryFind "s" (scalarBindings serialOut) with
                    | Some pv, Some sv ->
                        let diff = abs (pv - sv)
                        let tol = if integerData then 0.0 else 1e-9 * max 1.0 (abs sv)
                        if diff > tol then
                            fail name (sprintf "n=%d: parallel %.17g vs serial %.17g (|diff| = %g, tol = %g)" n pv sv diff tol)
                        else
                            // Determinism re-check: the lane count is a compile-time
                            // constant, so a fixed team size must still reproduce the
                            // run bit for bit.
                            match runProgram ompExe forcedThreads with
                            | Error e -> fail name ($"second omp run: {e}")
                            | Ok again ->
                                let strip (s: string) =
                                    s.Split('\n')
                                    |> Array.filter (fun l -> not (l.Contains "completed in"))
                                    |> String.concat "\n"
                                if strip again <> strip ompOut then
                                    fail name ($"n={n}: run-to-run output differs at fixed OMP_NUM_THREADS=4")
                                else
                                    pass name (sprintf "n=%d %s: |diff| = %g; identical across 2 runs"
                                                   n (if integerData then "exact" else "vs serial") diff)
                    | _ -> fail name "no scalar binding 's' in program output (comparison would be vacuous)"

        // ---- Fold reassociation: the fast default vs the repro build -------
        // Since 2026-10-17 the DEFAULT build reassociates a licensed SERIAL
        // floating-point accumulation chain -- `prodsum`'s fiber sweep, a
        // reduce over a deferred computation, and an UNQUALIFIED builtin
        // `reduce` (one the user never marked `omp`) -- and
        // `BLADE_FP_REASSOC=0` (= `BLADE_FP_REPRO=1`) is the serial build. The
        // arms below pin "1" (the default, spelled out) against "0", so every
        // property they assert is unchanged by the polarity flip.
        //
        // TWO EMITTED FORMS, chosen per site by measurement (CodeGen.fs
        // `fpReassocSimdStmts` / `fpReassocLaneStmts`, each of which carries the
        // numbers that chose it):
        //
        //   omp simd   `#pragma omp simd reduction(<op>:acc)` over the plain
        //              serial loop. Emitted by the `prodsum` IIFE and by the
        //              reduce-over-computation nest. Needs a builtin `+`/`*`
        //              body (a reduction clause names an OPERATOR) and a scalar
        //              element type.
        //   K lanes    K independent named accumulators combined in fixed
        //              ascending order, no pragma at all. Emitted by `reduce`
        //              over a MATERIALIZED array (both its statement and its
        //              expression form), and everywhere the simd form is not
        //              available -- above all for a `comm`-declared kernel,
        //              whose combine is a CALL.
        //
        // The `0` arm is the REPRO build, which is what makes "repro means
        // byte-identical" checkable at all. The pin below is per-COMPILE and
        // restored immediately, so nothing outside these arms can see it. (Test
        // blocks run sequentially in one process -- see Cli.fs -- so a
        // process-global env pin is safe here, the same way InterpDiff pins
        // BLADE_FP_CONTRACT for its block.)
        //
        // Three properties are asserted, and they are not the same property:
        //
        //   1. VALUE. Knob-on agrees with knob-off to 1e-9 relative on awkward
        //      non-representable data, and EXACTLY on integer-valued data.
        //      Integer exactness holds under ANY summation order -- including
        //      the vectorizer's, which is why it survives the simd form
        //      unchanged -- and it is the property loops/110-111 and the
        //      interpreter differential depend on.
        //   2. DETERMINISM, in the form the contract at `fpReassocEnabled` now
        //      states it: for a FIXED BINARY the answer is identical across two
        //      runs and across OMP_NUM_THREADS values. NEITHER form creates a
        //      team (`omp simd` is a SIMD construct, and the lane form has no
        //      pragma at all), so thread count cannot enter the answer. What is
        //      deliberately NOT asserted any more is reproducibility across
        //      compilers or flags: under the simd form the summation order is
        //      the vectorizer's, and the project policy is that optimized
        //      Release builds are not bit-reproducible across builds. (The lane
        //      arms do still have the stronger property -- a fixed function of
        //      the data and K -- but it is not separately pinned here, because
        //      one binary cannot observe it.)
        //   3. LICENCE + LIVENESS, at the emission level. Positive controls
        //      prove each site emits the FORM it is supposed to (without them
        //      the value arms could pass vacuously by never firing, and a site
        //      could silently swap forms), and a negative control proves an
        //      UNLICENSED user kernel -- no builtin body, no `comm` -- is
        //      emitted byte-identically with the knob on.
        //
        // n walks every boundary of the strided sweep: below the lanes (1, 3),
        // maximal tail (7), exactly the lanes (8), one past (9), maximal tail
        // above one full stride (15), indivisible (17). Getting any of them
        // wrong drops or double-counts elements silently -- the fold still
        // prints A number. The boundaries still matter under the simd form: the
        // vectorizer's own scalar remainder is exercised by exactly the same
        // awkward trip counts.
        let withReassoc (on: bool) (f: unit -> 'a) : 'a =
            let prior = System.Environment.GetEnvironmentVariable("BLADE_FP_REASSOC")
            System.Environment.SetEnvironmentVariable("BLADE_FP_REASSOC", (if on then "1" else "0"))
            try f ()
            finally System.Environment.SetEnvironmentVariable("BLADE_FP_REASSOC", prior)
        // The dot-shaped arms below (`reduce(<unforced zip>, (+))`) are EXACTLY
        // the shape `LinAlgPatterns.BlasL1` recognises, so in an environment
        // where BLAS is enabled (OPENBLAS_DIR set, or `blade test linalg`'s
        // BLADE_BLAS=1) they would emit one `blade_linalg::blade_dot` call and
        // never reach the fold nest at all -- the lane assertions would pass
        // vacuously and the value arms would compare two identical BLAS calls.
        // Pinned OFF for the whole reassoc block (no arm here wants a dispatch)
        // and restored immediately, same discipline as the knob pin above.
        let withBlasOff (f: unit -> 'a) : 'a =
            let prior = System.Environment.GetEnvironmentVariable("BLADE_BLAS")
            System.Environment.SetEnvironmentVariable("BLADE_BLAS", "0")
            try f ()
            finally System.Environment.SetEnvironmentVariable("BLADE_BLAS", prior)
        let emitOnly (nm: string) (src: string) : Result<string, string> =
            try
                match lower src with
                | Error e -> Error ($"lower failed: {e}")
                | Ok ir0 ->
                    match IRValidate.validateIR ir0 with
                    | Error errs -> Error (String.concat "; " errs)
                    | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir nm))
            with ex -> Error ($"codegen raised: {ex.Message}")
        let stripTiming (s: string) =
            s.Split('\n') |> Array.filter (fun l -> not (l.Contains "completed in")) |> String.concat "\n"

        // (label, n, integerData, sourceOf n)
        let fprSrcReduce (lit: string) =
            $"let A = Float64([{lit}])\nlet s = reduce(A, lambda(a, b) -> a + b)\n"
        let fprSrcProdsum (lit: string) (lit2: string) =
            $"let A = Float64([{lit}])\nlet B = Float64([{lit2}])\nlet s = prodsum(A, B)\n"
        // THREE operand streams: the comoment3 fiber kernel's shape, and the
        // one the arity-aware lane count (`laneCountForStreams`) puts at K = 5.
        let fprSrcProdsum3 (l1: string) (l2: string) (l3: string) =
            $"let A = Float64([{l1}])\nlet B = Float64([{l2}])\nlet C = Float64([{l3}])\nlet s = prodsum(A, B, C)\n"
        // reduce over an UNFORCED zip -- the dot shape. This lowers through
        // `genReduceComputeBinding` (reduce over a deferred computation), a
        // different emitter from the two above: its element is not a subscript
        // but the nest's whole per-iteration body evaluated at the lane index.
        let fprSrcDot (lit: string) (lit2: string) =
            $"let x = Float64([{lit}])\nlet y = Float64([{lit2}])\n"
              + "let P = method_for(zip(x, y)) <@> lambda(a: Float64, b: Float64) -> a * b\n"
              + "let s = reduce(P, (+))\n"
        // A `comm`-DECLARED kernel whose body is NOT a bare builtin op. This is
        // the licence class the simd form cannot spell -- no operator to name in
        // a reduction clause -- so it is what still gets the K-lane form, at
        // every site. `+ 0.0` is what keeps `foldKernelBuiltinOp` from
        // recognising the body while leaving the arithmetic (and hence the
        // expected value) identical to a plain sum.
        let fprCommKernel =
            "function myK(a: Float64, b: Float64) where comm(a, b) = a + b + 0.0\n"
        let fprSrcCommReduce (lit: string) =
            fprCommKernel + $"let A = Float64([{lit}])\nlet s = reduce(A, myK)\n"
        // comm kernel over a deferred computation: the reduce-over-computation
        // site's lane fallback. The 3-arg form is required there (a fused fold
        // cannot seed from its first element).
        let fprSrcCommDot (lit: string) (lit2: string) =
            fprCommKernel + $"let x = Float64([{lit}])\nlet y = Float64([{lit2}])\n"
              + "let P = method_for(zip(x, y)) <@> lambda(a: Float64, b: Float64) -> a * b\n"
              + "let s = reduce(P, myK, 0.0)\n"
        let fprSrcCommDot3 (l1: string) (l2: string) (l3: string) =
            fprCommKernel + $"let x = Float64([{l1}])\nlet y = Float64([{l2}])\nlet z = Float64([{l3}])\n"
              + "let P = method_for(zip(x, y, z)) <@> lambda(a: Float64, b: Float64, c: Float64) -> a * b * c\n"
              + "let s = reduce(P, myK, 0.0)\n"
        let fprNs = [1; 3; 7; 8; 9; 15; 17]
        // K = 5 for three streams, so its boundaries are a DIFFERENT set of n:
        // below the lanes (1, 3), exactly the lanes (5), maximal tail (4, 9),
        // one past (6), indivisible (11, 17).
        let fprNs3 = [1; 3; 4; 5; 6; 9; 11; 17]
        let fprCases : (string * string) list =
            [ for n in fprNs do
                yield ($"reduce_n{n}", fprSrcReduce (laneLit n awkward))
                yield ($"prodsum_n{n}",
                       fprSrcProdsum (laneLit n awkward) (laneLit n (fun i -> awkward (i + 3))))
                yield ($"dot_n{n}",
                       fprSrcDot (laneLit n awkward) (laneLit n (fun i -> awkward (i + 3))))
              for n in fprNs3 do
                yield ($"prodsum3_n{n}",
                       fprSrcProdsum3 (laneLit n awkward)
                                      (laneLit n (fun i -> awkward (i + 3)))
                                      (laneLit n (fun i -> awkward (i + 7))))
              // Integer-valued twins: EXACT equality is demanded of these.
              yield ("reduce_n15_integer_exact", fprSrcReduce (laneLit 15 (fun i -> float (i % 9 + 1))))
              yield ("prodsum_n15_integer_exact",
                     fprSrcProdsum (laneLit 15 (fun i -> float (i % 9 + 1)))
                                   (laneLit 15 (fun i -> float (i % 5 + 2))))
              yield ("prodsum3_n11_integer_exact",
                     fprSrcProdsum3 (laneLit 11 (fun i -> float (i % 9 + 1)))
                                    (laneLit 11 (fun i -> float (i % 5 + 2)))
                                    (laneLit 11 (fun i -> float (i % 3 + 1))))
              yield ("dot_n15_integer_exact",
                     fprSrcDot (laneLit 15 (fun i -> float (i % 9 + 1)))
                               (laneLit 15 (fun i -> float (i % 5 + 2))))
              // The K-LANE form's own value arms. The four cases above all
              // reach a site that now emits `omp simd`, so without these the
              // lane emitter -- still live for every `comm`-declared kernel --
              // would have no value coverage at all here.
              for n in fprNs do
                yield ($"comm_reduce_n{n}", fprSrcCommReduce (laneLit n awkward))
                yield ($"comm_dot_n{n}",
                       fprSrcCommDot (laneLit n awkward) (laneLit n (fun i -> awkward (i + 3))))
              yield ("comm_reduce_n15_integer_exact",
                     fprSrcCommReduce (laneLit 15 (fun i -> float (i % 9 + 1))))
              yield ("comm_dot_n15_integer_exact",
                     fprSrcCommDot (laneLit 15 (fun i -> float (i % 9 + 1)))
                                   (laneLit 15 (fun i -> float (i % 5 + 2)))) ]
        for (label, src) in fprCases do
            let name = "fp_reassoc_" + label
            let exact = label.EndsWith "integer_exact"
            match withReassoc true (fun () -> withBlasOff (fun () -> compileProgram outputDir (name + "_on") src)),
                  withReassoc false (fun () -> withBlasOff (fun () -> compileProgram outputDir (name + "_off") src)) with
            | Error e, _ -> fail name ($"reassoc-on build: {e}")
            | _, Error e -> fail name ($"reassoc-off build: {e}")
            | Ok onExe, Ok offExe ->
                match runProgram onExe "1", runProgram offExe "1" with
                | Error e, _ -> fail name ($"reassoc-on run: {e}")
                | _, Error e -> fail name ($"reassoc-off run: {e}")
                | Ok onOut, Ok offOut ->
                    match Map.tryFind "s" (scalarBindings onOut), Map.tryFind "s" (scalarBindings offOut) with
                    | Some ov, Some fv ->
                        let diff = abs (ov - fv)
                        let tol = if exact then 0.0 else 1e-9 * max 1.0 (abs fv)
                        if diff > tol then
                            fail name (sprintf "reassoc %.17g vs serial %.17g (|diff| = %g, tol = %g)" ov fv diff tol)
                        else
                            // Thread-count independence AND run-to-run identity,
                            // on the SAME BINARY -- the whole of what the knob
                            // now promises. Neither form takes anything from a
                            // team (`omp simd` is a SIMD construct and creates
                            // none; the lane form has no pragma at all), so both
                            // must hold outright. Note what is NOT compared: two
                            // DIFFERENTLY BUILT binaries, which the contract at
                            // `fpReassocEnabled` no longer claims agree.
                            match runProgram onExe "4", runProgram onExe "1" with
                            | Error e, _ -> fail name ($"reassoc-on run at 4 threads: {e}")
                            | _, Error e -> fail name ($"reassoc-on second run: {e}")
                            | Ok at4, Ok again ->
                                if stripTiming at4 <> stripTiming onOut then
                                    fail name "output differs between OMP_NUM_THREADS=1 and 4 (a reassociated form took something from a team)"
                                elif stripTiming again <> stripTiming onOut then
                                    fail name "output differs across two runs of the same binary at a fixed thread count"
                                else
                                    pass name (sprintf "%s: |diff| = %g; identical at 1 vs 4 threads and across 2 runs"
                                                   (if exact then "exact" else "vs serial") diff)
                    | _ -> fail name "no scalar binding 's' in program output (comparison would be vacuous)"

        // Emission controls. Without these the value arms could all pass by the
        // knob never firing at all -- AND, now that there are two forms, a site
        // could silently swap one for the other and every value arm would still
        // pass. Each case therefore pins WHICH form its site emits, which is a
        // per-site MEASUREMENT (see the comment at each `fpReassocEnabled ()`
        // site in CodeGen.fs) and not an arbitrary choice: a regression that
        // flips one of these is a performance regression the values cannot see.
        let fprEmitCases : (string * string * string) list =
            // (label, source, expected form: "simd" | "lanes" | "unchanged")
            [ // reduce over a MATERIALIZED array -> lanes (measured 1.12x
              // bandwidth-bound / 1.90x cache-resident over the simd form).
              ("builtin_reduce_lanes",
               fprSrcReduce (laneLit 17 awkward), "lanes")
              // prodsum's fiber IIFE -> the explicit K-lane VECTOR form since
              // M9 (2026-10-19; blade_fold_lanes.hpp): K = 8 independent 32-byte
              // accumulators in a fixed tree, replacing the one-accumulator
              // simd reduction the site chose on 2026-10-17 (2.60x on the gemv
              // fiber and 3.36x at three streams over the scalar lanes). Neither
              // the macro nor the scalar lanes may appear: the vector call IS
              // the form.
              ("prodsum_vector",
               fprSrcProdsum (laneLit 17 awkward) (laneLit 17 (fun i -> awkward (i + 3))), "vector")
              ("prodsum3_vector",
               fprSrcProdsum3 (laneLit 17 awkward)
                              (laneLit 17 (fun i -> awkward (i + 3)))
                              (laneLit 17 (fun i -> awkward (i + 7))), "vector")
              // reduce-over-deferred-computation (the dot shape) -> the same
              // vector form (M9); a non-dot-class body keeps the simd form (the
              // `_simd` pins below).
              ("dot_reduce_over_computation_vector",
               fprSrcDot (laneLit 17 awkward) (laneLit 17 (fun i -> awkward (i + 3))), "vector")
              // A `comm`-declared kernel is licensed but its combine is a CALL,
              // so no reduction clause can name it: BOTH sites fall back to the
              // lanes. This is the arm that keeps `fpReassocLaneStmts` live at
              // the reduce-over-computation site.
              ("comm_reduce_lanes",
               fprSrcCommReduce (laneLit 17 awkward), "lanes")
              ("comm_dot_over_computation_lanes",
               fprSrcCommDot (laneLit 17 awkward) (laneLit 17 (fun i -> awkward (i + 3))), "lanes")
              // Unlicensed: body is not a bare builtin op and no `comm` is
              // declared, so the knob grants nothing. `where omp` is absent too,
              // so this is the serial arm either way.
              ("unlicensed_kernel_stays_serial",
               "function myK(a: Float64, b: Float64) = (a + b) * 1.0000001\n"
                 + $"let A = Float64([{(laneLit 17 awkward)}])\n"
                 + "let s = reduce(A, myK)\n", "unchanged")
              // A FUSED FOLD over a `<&!>` tree (one shared builtin kernel,
              // one accumulator per leaf) -> ONE `omp simd reduction` clause
              // naming every accumulator (2026-10-17; the onepass shape, bench
              // gap G20/F11). Before, the tree stayed serial in every build.
              ("fused_join_simd",
               $"let x = Float64([{(laneLit 17 awkward)}])\n"
                 + "let l = method_for(x)\n"
                 + "let (s1, s2) = reduce((l <@> lambda(v) -> v) <&!> (l <@> lambda(v) -> v * v), (+))\n"
                 + "let s = s1 + s2\n", "simd")
              // A REDUCTION JOIN with per-leg builtin `+` folds: same clause.
              ("pack_join_simd",
               $"let x = Float64([{(laneLit 17 awkward)}])\n"
                 + $"let y = Float64([{(laneLit 17 (fun i -> awkward (i + 3)))}])\n"
                 + "let (a, b) = object_for(<&!>) <@> (reduce(x, (+), 0.0), prodsum(x, y))\n"
                 + "let s = a + b\n", "simd") ]
        for (label, src, expectForm) in fprEmitCases do
            let name = "fp_reassoc_emission_" + label
            match withReassoc true (fun () -> withBlasOff (fun () -> emitOnly name src)),
                  withReassoc false (fun () -> withBlasOff (fun () -> emitOnly name src)) with
            | Error e, _ -> fail name ($"emit (knob on): {e}")
            | _, Error e -> fail name ($"emit (knob off): {e}")
            | Ok onCpp, Ok offCpp ->
                let hasLanes (s: string) = s.Contains "__rlane" || s.Contains "__pl0"
                // The macro, not a raw `#pragma`: cpp/blade_portability.hpp owns
                // the spelling (and its OpenMP-4.0 gate), and codegen emits only
                // the macro -- so a raw pragma appearing here would itself be
                // the regression.
                let hasSimd (s: string) = s.Contains "BLADE_OMP_SIMD_REDUCTION"
                // The explicit K-lane vector form (blade_fold_lanes.hpp, M9).
                let hasVector (s: string) = s.Contains "blade_fold::dot<" || s.Contains "blade_fold::sq<" || s.Contains "blade_fold::dot3<" || s.Contains "blade_fold::sum<"
                if hasLanes offCpp || hasSimd offCpp || hasVector offCpp then
                    fail name "BLADE_FP_REASSOC=0 (the repro build) emitted a reassociated form (it must never reassociate)"
                else
                    match expectForm with
                    | "vector" when not (hasVector onCpp) ->
                        fail name "knob ON emitted no blade_fold:: vector call (the gate never fired, or the site fell back to simd / lanes)"
                    | "vector" when hasSimd onCpp || hasLanes onCpp ->
                        fail name "knob ON emitted a simd clause or scalar lanes beside the vector call"
                    | "simd" when hasVector onCpp ->
                        fail name "knob ON emitted the vector form at a simd site (the body is not dot-class)"
                    | "lanes" when not (hasLanes onCpp) ->
                        fail name "knob ON emitted no lane accumulators (the gate never fired, or the site swapped to simd)"
                    | "lanes" when hasSimd onCpp ->
                        fail name "knob ON emitted BOTH forms at a lane site"
                    | "simd" when not (hasSimd onCpp) ->
                        fail name "knob ON emitted no omp simd reduction (the gate never fired, or the site swapped to lanes)"
                    | "simd" when hasLanes onCpp ->
                        fail name "knob ON emitted lane accumulators at a simd site (the measured-worse form)"
                    | "unchanged" when onCpp <> offCpp ->
                        fail name "unlicensed kernel: knob ON changed the emission (the knob is not a licence)"
                    | "lanes" -> pass name "K-lane form, only with the knob on"
                    | "simd" -> pass name "omp simd reduction form, only with the knob on"
                    | "vector" -> pass name "explicit K-lane vector form (blade_fold::), only with the knob on"
                    | "unchanged" -> pass name "byte-identical with the knob on (unlicensed, stays serial)"
                    | other -> fail name ($"test bug: unknown expected form {other}")

        // "omp IMPLIES simd" on a fold over a DEFERRED computation: a `where omp`
        // fold kernel sets the Path B chunk plan, which a serial-emission build
        // (`BLADE_OMP_THREADS=1`) drops at its consumer. The fold used to fall to
        // the plain serial chain there (bench gap G20 / F11 -- the dot, composed
        // chain and checksum shapes); it now takes the vectorized form, with the
        // census line for the dropped team. Three builds of one source (a
        // dot-class body, so the fast form is the explicit K-lane vector tree
        // since M9 / M14, 2026-10-19):
        //   threads on (default)        -> the Path B team, one blade_fold::dot call per chunk
        //   BLADE_OMP_THREADS=1         -> one blade_fold::dot call over the sweep + the census line
        //   ... + BLADE_FP_REASSOC=0    -> neither (the repro build)
        // The CENSUS INVARIANT is the point: a `where omp` fold emitted in its
        // vector form under BLADE_OMP_THREADS=1 still prints the line naming
        // the knob (bench register G7 / G27: a dropped omp is never silent).
        (let name = "fp_reassoc_emission_omp_deferred_fold_serial_build_simd"
         let src =
             $"let x = Float64([{(laneLit 17 awkward)}])\n"
               + $"let y = Float64([{(laneLit 17 (fun i -> awkward (i + 3)))}])\n"
               + "let s = reduce(method_for(zip(x, y)) <@> lambda(a: Float64, b: Float64) -> a * b, lambda(p, q) where omp -> p + q, 0.0)\n"
         let withThreads (v: string) (f: unit -> 'a) : 'a =
             let prior = System.Environment.GetEnvironmentVariable("BLADE_OMP_THREADS")
             System.Environment.SetEnvironmentVariable("BLADE_OMP_THREADS", v)
             try f ()
             finally System.Environment.SetEnvironmentVariable("BLADE_OMP_THREADS", prior)
         let hasSimd (s: string) = s.Contains "BLADE_OMP_SIMD_REDUCTION("
         let hasVector (s: string) = s.Contains "blade_fold::dot<double, 8>("
         let hasTeam (s: string) = s.Contains "BLADE_OMP_PARALLEL_NUM_THREADS("
         let census (s: string) = s.Contains "[omp] requested but emitted serial: BLADE_OMP_THREADS="
         match withReassoc true (fun () -> withBlasOff (fun () -> withThreads null (fun () -> emitOnly name src))),
               withReassoc true (fun () -> withBlasOff (fun () -> withThreads "1" (fun () -> emitOnly name src))),
               withReassoc false (fun () -> withBlasOff (fun () -> withThreads "1" (fun () -> emitOnly name src))) with
         | Ok team, Ok serial, Ok repro ->
             if not (hasTeam team) || hasSimd team || not (team.Contains "blade_fold::dot<double, 8>(__rsrc0 + __rlo, __rsrc1 + __rlo, __rhi - __rlo)") then
                 fail name "threads on: expected the Path B team with one blade_fold::dot call per chunk and no simd clause"
             elif not (hasVector serial) || not (census serial) || hasTeam serial || hasSimd serial then
                 fail name "BLADE_OMP_THREADS=1: expected one blade_fold::dot call over the sweep and the census line, no team, no simd clause"
             elif hasSimd repro || hasTeam repro || hasVector repro then
                 fail name "repro build: expected the serial chain"
             else pass name "team of vector chunks / vector + census / serial, as each build should"
         | Error e, _, _ | _, Error e, _ | _, _, Error e -> fail name ($"emit: {e}"))

        // ---- The lane COUNT is a function of the operand-stream count -------
        // `laneCountForStreams` divides a fixed register/ILP budget among the
        // concurrent value streams one lane iteration keeps live: K = 8 at one
        // and two streams (the repo's measured anchor), K = floor(16/s) beyond.
        // The count is part of the lane form's EVALUATION ORDER, so it is
        // pinned here as emitted text, not left to be inferred from a timing.
        //
        // THE SOURCES CHANGED WITH THE FORMS, THE RULE DID NOT. `prodsum` used
        // to be where the arity-aware count was observable; it now emits `omp
        // simd`, where the partial count is the vectorizer's and there is
        // nothing to pin. The rule is still live -- and still observable --
        // wherever the LANE form is what gets emitted, which is any
        // `comm`-declared kernel, so the multi-stream arms move to a comm fold
        // over a deferred computation of the same arity.
        //
        // The three-stream arm is the one with a measurement behind it: 8 lanes
        // on a three-stream body ran 2.2-2.7x SLOWER than 5 lanes in the
        // comoment3 shape (61 vars x 2003 samples), while 8 lanes on the
        // two-stream form helped. A regression that silently restores 8 here
        // restores that.
        let distinctLanes (prefix: string) (s: string) =
            System.Text.RegularExpressions.Regex.Matches(s, prefix + @"(\d+)")
            |> Seq.cast<System.Text.RegularExpressions.Match>
            |> Seq.map (fun m -> int m.Groups.[1].Value)
            |> Seq.distinct |> Seq.length
        let fprLaneCountCases : (string * string * string * int) list =
            // (label, source, lane-local prefix, expected distinct lanes)
            [ ("reduce_one_stream", fprSrcReduce (laneLit 33 awkward), "__rlane", 8)
              ("comm_reduce_one_stream", fprSrcCommReduce (laneLit 33 awkward), "__rlane", 8)
              ("comm_dot_two_streams",
               fprSrcCommDot (laneLit 33 awkward) (laneLit 33 (fun i -> awkward (i + 3))), "__rlane", 8)
              ("comm_dot3_three_streams",
               fprSrcCommDot3 (laneLit 33 awkward)
                              (laneLit 33 (fun i -> awkward (i + 3)))
                              (laneLit 33 (fun i -> awkward (i + 7))), "__rlane", 5) ]
        for (label, src, prefix, expected) in fprLaneCountCases do
            let name = "fp_reassoc_lane_count_" + label
            match withReassoc true (fun () -> withBlasOff (fun () -> emitOnly name src)) with
            | Error e -> fail name ($"emit (knob on): {e}")
            | Ok cpp ->
                let got = distinctLanes prefix cpp
                if got <> expected then
                    fail name ($"expected {expected} lanes ({prefix}0..{prefix}{(expected - 1)}), emitted {got}")
                else pass name ($"{got} lanes")

        // ---- Phase 1 regression: ivdep must not land inside collapse(2) ----
        // Text-only assertions cannot see this; only g++ can.
        let collapseSrc =
            "function k(a: Float64, b: Float64) where omp(a: 1, b: 1) = a * b + 1.0\n" +
            "let P = Float64([1.5, 2.5, 3.5, 4.5, 5.5])\n" +
            "let Q = Float64([0.25, 0.5, 0.75, 1.25])\n" +
            "let M = object_for(k) <@> (P, Q) |> compute\n"
        let collapseName = "collapse2_dense_map_compiles"
        match compileProgram outputDir collapseName collapseSrc with
        | Error e -> fail collapseName e
        | Ok exeAbs ->
            match runProgram exeAbs forcedThreads with
            | Error e -> fail collapseName ($"run: {e}")
            | Ok out ->
                // 5x4 outer product through the kernel. A rank-2 array prints
                // NESTED — `M = [[a, b, ...], [...], ...]` — so the old flat
                // `\[([^\]]*)\]` parse stopped at the first inner `]` and read
                // one row, not the array. Match the whole bracketed value and
                // then read the ROWS, which is also a STRONGER assertion than a
                // flatten-and-parse would be: it pins the 5x4 shape, not just
                // the 20 values in DFS order.
                let expectedRows =
                    [ for p in [1.5; 2.5; 3.5; 4.5; 5.5] ->
                        [ for q in [0.25; 0.5; 0.75; 1.25] -> p * q + 1.0 ] ]
                let parseNum (s: string) =
                    match System.Double.TryParse(s.Trim(),
                                                 System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture) with
                    | true, v -> Some v
                    | _ -> None
                let rows =
                    let m = System.Text.RegularExpressions.Regex.Match(out, @"M = (\[\[.*\]\])")
                    if not m.Success then []
                    else
                        System.Text.RegularExpressions.Regex.Matches(m.Groups.[1].Value, @"\[([^\[\]]*)\]")
                        |> Seq.cast<System.Text.RegularExpressions.Match>
                        |> Seq.map (fun r -> r.Groups.[1].Value.Split(',') |> Array.toList |> List.choose parseNum)
                        |> Seq.toList
                if List.length rows <> List.length expectedRows
                   || List.exists2 (fun (a: float list) (b: float list) -> a.Length <> b.Length) rows expectedRows then
                    fail collapseName
                        (sprintf "expected a %dx%d nested print, parsed rows of lengths %A"
                             (List.length expectedRows) 4 (rows |> List.map List.length))
                elif List.exists2 (fun (ra: float list) rb ->
                                       List.exists2 (fun (a: float) b -> abs (a - b) > 1e-9) ra rb)
                                  rows expectedRows then
                    fail collapseName "collapse(2) map produced wrong values under 4 threads"
                else
                    pass collapseName "compiles under g++ and computes correctly (ivdep/collapse interaction)"

        // ---- BLADE_OMP_THREADS: same numbers, different threading ----------
        // The knob is a THREADING decision, never a numeric one: what it
        // changes is whether a team is created, and the emitted arithmetic on
        // each side is one the suite already accepts (the licensed omp form, or
        // the serial form its own differentials compare against). So a
        // knob-on/knob-off pair must agree to the same tolerance an
        // omp-vs-serial pair does -- and this arm is the one that COMPILES AND
        // RUNS both, which no text assertion in the omp-pragma block can do.
        //
        // Non-integer data, deliberately: on integer-valued f64 every
        // summation order is exact and the comparison could not tell a genuine
        // agreement from an arithmetic that got lost. The Path A arm is the
        // sharp one -- suppressed Path A keeps `omp simd reduction`, so both
        // sides reassociate, just at different widths.
        let withOmpThreads (v: string) (f: unit -> 'a) : 'a =
            let prior = System.Environment.GetEnvironmentVariable("BLADE_OMP_THREADS")
            System.Environment.SetEnvironmentVariable("BLADE_OMP_THREADS", v)
            try f ()
            finally System.Environment.SetEnvironmentVariable("BLADE_OMP_THREADS", prior)
        let knobN = 33
        let knobLit = laneLit knobN awkward
        let knobDecl = $"let A = Float64([{knobLit}])\n"
        let knobCases : (string * string) list =
            // (label, source) -- one per suppression shape the knob has.
            [ ("path_a_builtin_sum",
               knobDecl + "let s = reduce(A, lambda(a, b) where omp -> a + b)\n")
              ("path_b_named_comm",
               commFn + knobDecl + "let s = reduce(A, myAdd)\n")
              // `axes = 2` for the same reason as the differential case above.
              ("path_b_reduce_over_computation",
               commFn + bDecl + "let s = reduce(method_for(B, B) <@> lambda(x, y) -> x * y, myAdd, 0.0, axes = 2)\n") ]
        for (label, src) in knobCases do
            let name = "omp_threads_knob_" + label
            match withOmpThreads "1" (fun () -> compileProgram outputDir (name + "_serial") src),
                  withOmpThreads null (fun () -> compileProgram outputDir (name + "_threaded") src) with
            | Error e, _ -> fail name ($"knob-on build: {e}")
            | _, Error e -> fail name ($"knob-off build: {e}")
            | Ok serialExe, Ok threadedExe ->
                // The knob-on binary is run at OMP_NUM_THREADS=4 ON PURPOSE:
                // it has no parallel construct left, so the runtime setting
                // must be inert. If it is not, a team survived somewhere.
                match runProgram serialExe forcedThreads, runProgram threadedExe forcedThreads with
                | Error e, _ -> fail name ($"knob-on run: {e}")
                | _, Error e -> fail name ($"knob-off run: {e}")
                | Ok serialOut, Ok threadedOut ->
                    match Map.tryFind "s" (scalarBindings serialOut),
                          Map.tryFind "s" (scalarBindings threadedOut) with
                    | Some sv, Some tv ->
                        let diff = abs (sv - tv)
                        let tol = 1e-9 * max 1.0 (abs tv)
                        if diff > tol then
                            fail name (sprintf "knob-on %.17g vs knob-off %.17g (|diff| = %g)" sv tv diff)
                        else
                            match runProgram serialExe "1" with
                            | Error e -> fail name ($"knob-on run at 1 thread: {e}")
                            | Ok at1 ->
                                if stripTiming at1 <> stripTiming serialOut then
                                    fail name "knob-on output depends on OMP_NUM_THREADS (a thread construct survived suppression)"
                                else
                                    pass name (sprintf "|diff| = %g; knob-on output independent of OMP_NUM_THREADS" diff)
                    | _ -> fail name "no scalar binding 's' in program output (comparison would be vacuous)"

        // ---- G35 IDENTITY: threaded complete rows == the serial walk ------
        // BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off, the same program built with
        // threads (row slots) and with BLADE_OMP_THREADS=1 (the serial walk),
        // run at several team sizes: the printed text must be IDENTICAL. What
        // that text pins: FINITE-VALUE LOW-BIT IDENTITY, SIGNED ZERO and
        // NaN-EQUIVALENCE -- not raw bits (decimal text carries no NaN payload).
        // The data is chosen so that any other association would show: rows
        // mix 1e4-scaled and unit points (cross terms ~1e8 beside ~1, signed
        // weights, heavy cancellation -- the default licensed build prints a
        // different r1), a -0.0 seed chain over -0.0 cells (1.0 / s2 pins the
        // sign: -inf), and a NaN cell (sqrt of a negative weight; the output
        // must SAY nan). Printing is 15 digits, so r1 = s1 mod 1024 (exact: a
        // power-of-two floor) re-exposes s1's low bits: one ulp of s1 moves r1
        // well inside its 15 printed digits. Rank 2 (extent 521) and rank 3
        // (extent 67, the comoment3 shape), both above the work gate.
        let rowSlotSrc2 =
            "type Var = Idx<521>\ntype Obs = Idx<3>\n"
            + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> (Float64((i * 7 + s * 3) % 11) * 0.37 + 0.1 / Float64(i + 1)) * (1.0 + 9999.0 * Float64((i % 4) / 3)) |> compute\n"
            + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
            + "let w = method_for(range<Var>) <@> lambda(j) -> Float64((j * 5) % 7) - 3.0 + 0.25 / Float64(j + 1) |> compute\n"
            + "let s1 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j) * w(j), (+)), lambda(u, v) where omp -> u + v, 0.25)\n"
            + "let r1 = s1 - floor(s1 / 1024.0) * 1024.0\n"
            + "let s2 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> -0.0 * g(i, j), lambda(a, b) -> a + b, -0.0), lambda(u, v) where omp -> u + v, -0.0)\n"
            + "let z2 = 1.0 / s2\n"
            + "let s3 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> sqrt(w(j)) * g(i, j), (+)), lambda(u, v) where omp -> u + v, 0.0)\n"
        let rowSlotSrc3 =
            "type Var = Idx<67>\ntype Obs = Idx<3>\n"
            + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> (Float64((i * 7 + s * 3) % 11) * 0.37 + 0.1 / Float64(i + 1)) * (1.0 + 99.0 * Float64((i % 4) / 3)) |> compute\n"
            + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
            + "let w = method_for(range<Var>) <@> lambda(k) -> Float64((k * 5) % 7) - 3.0 + 0.25 / Float64(k + 1) |> compute\n"
            + "let s1 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> t(i, j, k) * w(k), (+)), (+)), lambda(u, v) where omp -> u + v, 0.25)\n"
            + "let r1 = s1 - floor(s1 / 1024.0) * 1024.0\n"
            + "let s2 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> -0.0 * t(i, j, k) * t(i, j, k), lambda(a, b) -> a + b, -0.0), lambda(a, b) -> a + b, -0.0), lambda(u, v) where omp -> u + v, -0.0)\n"
            + "let z2 = 1.0 / s2\n"
            + "let s3 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> sqrt(w(k)) * t(i, j, k), (+)), (+)), lambda(u, v) where omp -> u + v, 0.0)\n"
        // ROUND 3 (W2-0a): s2's walk (`-0.0 * g(i, j)` / `t(i, j, k)`, a plain
        // `+` kernel, coordinate-free seeds) is one the ORDERED ORBIT SCATTER
        // admits too, and since round 3 the scatter takes precedence there by
        // default; s1 and s3 read `w`, which the scatter declines, so they
        // keep the row slots. So the identity runs FOUR builds, every one's
        // text equal at every team size: the default threaded build (row
        // slots for s1 / s3, the scatter for s2), BLADE_FOLD_ROW_SLOTS=prefer
        // (the round-2 order: row slots for all three), BLADE_OMP_THREADS=1
        // (s2 the serial scatter), and the true serial walk (BLADE_OMP_THREADS=1
        // BLADE_ORBIT_SCATTER=0: neither form).
        for (rank, rowSlotSrc) in [ ("rank2", rowSlotSrc2); ("rank3", rowSlotSrc3) ] do
         let name = $"row_slots_identity_{rank}_threads_vs_serial_walk"
         let build (threads: string) (knob: string) (scatter: string) (tag: string) =
             use _r = pinEnv "BLADE_FP_REPRO" "1"
             use _c = pinEnv "BLADE_FP_CONTRACT" "off"
             use _p = pinEnv "BLADE_PRINT" "s1,r1,s2,z2,s3"
             use _k = pinEnv "BLADE_FOLD_ROW_SLOTS" knob
             use _o = pinEnv "BLADE_ORBIT_SCATTER" scatter
             use _g = pinEnv "BLADE_TILE_TUNE" null
             use _t = pinEnv "BLADE_OMP_THREADS" threads
             compileProgram outputDir (name + tag) rowSlotSrc
         match build null null null "_threaded", build null "prefer" null "_prefer", build "1" null null "_serial_scatter", build "1" null "0" "_serial" with
         | Error e, _, _, _ -> fail name ($"threaded build: {e}")
         | _, Error e, _, _ -> fail name ($"prefer build: {e}")
         | _, _, Error e, _ -> fail name ($"serial (scatter) build: {e}")
         | _, _, _, Error e -> fail name ($"serial walk build: {e}")
         | Ok thrExe, Ok prfExe, Ok sscExe, Ok serExe ->
             let cppOfExe (exe: string) = File.ReadAllText(Path.ChangeExtension(exe, ".cpp"))
             let slotsOf (exe: string) = (System.Text.RegularExpressions.Regex.Matches(cppOfExe exe, @"__rslot_s\d\[__i0\] = ")).Count
             let scattersOf (exe: string) = (System.Text.RegularExpressions.Regex.Matches(cppOfExe exe, @"= blade_scatter::scatter[23]<")).Count
             if slotsOf thrExe <> 2 || scattersOf thrExe <> 1 then
                 fail name ($"the default threaded build emitted {slotsOf thrExe} row-slot assignment(s) and {scattersOf thrExe} scatter(s), expected 2 (s1, s3) and 1 (s2: the scatter's precedence) -- the comparison would not test the forms")
             elif slotsOf prfExe <> 3 || scattersOf prfExe <> 0 then
                 fail name ($"the prefer build emitted {slotsOf prfExe} row-slot assignment(s) and {scattersOf prfExe} scatter(s), expected 3 (s1, s2, s3) and 0")
             elif (cppOfExe sscExe).Contains "__rslot" || scattersOf sscExe <> 1 then
                 fail name "the BLADE_OMP_THREADS=1 build must scatter s2 serially and emit no row slots"
             elif (cppOfExe serExe).Contains "__rslot" || scattersOf serExe <> 0 then
                 fail name "the BLADE_OMP_THREADS=1 BLADE_ORBIT_SCATTER=0 build emitted row slots or a scatter (it must be the serial walk)"
             else
             match runProgram serExe "1", runProgram thrExe "4", runProgram thrExe "7", runProgram thrExe "1",
                   runProgram prfExe "4", runProgram prfExe "7", runProgram prfExe "1", runProgram sscExe "1" with
             | Error e, _, _, _, _, _, _, _ -> fail name ($"serial run: {e}")
             | _, Error e, _, _, _, _, _, _ | _, _, Error e, _, _, _, _, _ | _, _, _, Error e, _, _, _, _ -> fail name ($"threaded run: {e}")
             | _, _, _, _, Error e, _, _, _ | _, _, _, _, _, Error e, _, _ | _, _, _, _, _, _, Error e, _ -> fail name ($"prefer run: {e}")
             | _, _, _, _, _, _, _, Error e -> fail name ($"serial scatter run: {e}")
             | Ok ser, Ok t4, Ok t7, Ok t1, Ok p4, Ok p7, Ok p1, Ok ssc ->
                 let s = stripTiming ser
                 let expected = [ "s1 = "; "r1 = "; "s2 = -0.0"; "z2 = -inf" ]
                 let missing = expected |> List.filter (fun e -> not (s.Contains e))
                 let nanOk = System.Text.RegularExpressions.Regex.IsMatch(s, @"(?m)^s3 = -?nan\r?$")
                 let differ =
                     [ ("default T=4", t4); ("default T=7", t7); ("default T=1", t1)
                       ("prefer T=4", p4); ("prefer T=7", p7); ("prefer T=1", p1); ("threads-off scatter", ssc) ]
                     |> List.filter (fun (_, o) -> stripTiming o <> s)
                 if not missing.IsEmpty then
                     fail name ($"""serial walk output lacks {(String.concat " | " missing)}: {s}""")
                 elif not nanOk then
                     fail name ($"the NaN cell's checksum must print nan: {s}")
                 elif not differ.IsEmpty then
                     fail name ($"""a form differs from the serial walk [{s}]: {(differ |> List.map (fun (k, o) -> $"{k} [{stripTiming o}]") |> String.concat " / ")}""")
                 else
                     pass name "default (row slots + the scatter's precedence), prefer (row slots only) at T = 1, 4, 7 and the threads-off scatter: text identical to the serial walk under REPRO + CONTRACT=off: finite-value low-bit identity (s1, r1), signed zero (-0.0 / -inf), NaN-equivalence (s3 = nan)"

        // ---- W2-0a RAW BITS: the scatter's precedence, every route -------
        // (round 3; Codex diff review of W2-0, finding 1.) Walks that BOTH the
        // row slots and the ordered orbit scatter admit (rank 2 at extent 521,
        // rank 3 at extent 67, above the row-slot gate), over stored pools
        // with heavy cancellation (1e4-scaled rows beside unit rows, signed
        // values), a -0.0 seed chain over -0.0 cells, and a second pool with
        // TWO DISTINCT NaN PAYLOADS poked into its stored bytes (one with the
        // sign bit set) -- read raw and through arithmetic (s5). Every scalar's
        // exact uint64 representation is captured, and five builds under
        // BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off must print the SAME bits at
        // every team size: the DEFAULT (the scatter, by precedence), `prefer`
        // (the row slots), BLADE_FOLD_ROW_SLOTS=0 (the scatter), threads off
        // (the serial scatter) and the true serial walk (threads off,
        // BLADE_ORBIT_SCATTER=0). Each binding's ROUTE is asserted per build.
        // G74: s7 / s8 fold their rows SEEDLESS (`(+)`): s7 over -0.0 cells
        // must be -0.0 exactly (the left fold of the cells; the checker's
        // former +0.0 seed gave +0.0), s8 must carry the payload poked into
        // the pool's FIRST cell -- row 0's head -- as its bits.
        let rawNaNA = 0x7FF800000000A11CUL
        let rawNaNB = 0xFFF80000000B0B0BUL
        let rawCases =
            let x2 = "(Float64((i * 7 + s * 3) % 11) * 0.37 - 1.85 + 0.1 / Float64(i + 1)) * (1.0 + 9999.0 * Float64((i % 4) / 3))"
            let x3 = "(Float64((i * 7 + s * 3) % 11) * 0.37 - 1.85 + 0.1 / Float64(i + 1)) * (1.0 + 99.0 * Float64((i % 4) / 3))"
            let omp = "lambda(u, v) where omp -> u + v"
            let rank2 =
                "type Var = Idx<521>\ntype Obs = Idx<3>\n"
                + $"let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> {x2} |> compute\n"
                + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
                + "let gn = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) * 0.5 |> compute\n"
                + "let gm = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) * 0.25 |> compute\n"
                + "let gh = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) * 0.125 |> compute\n"
                + $"let s1 =reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j), (+)), {omp}, 0.25)\n"
                + $"let s2 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> -0.0 * (g(i, j) * g(i, j)), lambda(a, b) -> a + b, -0.0), {omp}, -0.0)\n"
                + $"let s3 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j) * g(i, j) * 1.0e-9 - g(i, j), (+)), {omp}, 0.0)\n"
                + $"let s4 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> gn(i, j), (+)), {omp}, 0.0)\n"
                + $"let s5 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> gn(i, j) * 2.0 + 1.0, (+)), {omp}, 0.0)\n"
                + $"let s6 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> gm(i, j), (+)), {omp}, 0.0)\n"
                + $"let s7 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> -0.0 * (g(i, j) * g(i, j)), (+)), {omp}, -0.0)\n"
                + $"let s8 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> gh(i, j), (+)), {omp}, 0.0)\n"
            let lv3 (body: string) (inner: string) =
                $"reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> {body}, {inner}), {inner}), {omp}"
            let rank3 =
                "type Var = Idx<67>\ntype Obs = Idx<3>\n"
                + $"let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> {x3} |> compute\n"
                + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
                + "let tn = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) * 0.5 |> compute\n"
                + "let tm = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) * 0.25 |> compute\n"
                + "let th = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) * 0.125 |> compute\n"
                + "let s1 = " + lv3 "t(i, j, k)" "(+)" + ", 0.25)\n"
                + "let s2 = " + lv3 "-0.0 * (t(i, j, k) * t(i, j, k))" "lambda(a, b) -> a + b, -0.0" + ", -0.0)\n"
                + "let s3 = " + lv3 "t(i, j, k) * t(i, j, k) * 1.0e-9 - t(i, j, k)" "(+)" + ", 0.0)\n"
                + "let s4 = " + lv3 "tn(i, j, k)" "(+)" + ", 0.0)\n"
                + "let s5 = " + lv3 "tn(i, j, k) * 2.0 + 1.0" "(+)" + ", 0.0)\n"
                + "let s6 = " + lv3 "tm(i, j, k)" "(+)" + ", 0.0)\n"
                + "let s7 = " + lv3 "-0.0 * (t(i, j, k) * t(i, j, k))" "(+)" + ", -0.0)\n"
                + "let s8 = " + lv3 "th(i, j, k)" "(+)" + ", 0.0)\n"
            // (rank, source, the NaN pool, its poked cells: C(522, 2) = 135981
            // and C(69, 3) = 52394 canonical cells)
            // `gm` / `tm` carry the same two payloads in the OPPOSITE order, so
            // each payload is the first one some walk meets.
            // G74: `gh` / `th` carry payload A in their FIRST canonical cell
            // (S(0, 0) / T(0, 0, 0)), the head of row 0's seedless chain (s8)
            [ ("rank2", rank2, [ ("gn", [ (1234, rawNaNA); (70000, rawNaNB) ]); ("gm", [ (500, rawNaNB); (90000, rawNaNA) ]); ("gh", [ (0, rawNaNA) ]) ])
              ("rank3", rank3, [ ("tn", [ (777, rawNaNA); (40000, rawNaNB) ]); ("tm", [ (300, rawNaNB); (45000, rawNaNA) ]); ("th", [ (0, rawNaNA) ]) ]) ]
        let rawScalars = [ "s1"; "s2"; "s3"; "s4"; "s5"; "s6"; "s7"; "s8" ]
        for (rank, src, pokes) in rawCases do
         let name = $"scatter_precedence_raw_bits_{rank}"
         // (tag, BLADE_OMP_THREADS, BLADE_FOLD_ROW_SLOTS, BLADE_ORBIT_SCATTER, expected route, team sizes)
         let builds =
             [ ("default", null, null, null, "scatter", [ "1"; "4"; "7" ])
               ("prefer", null, "prefer", null, "slots", [ "1"; "4"; "7" ])
               ("slots_off", null, "0", null, "scatter", [ "1"; "4"; "7" ])
               ("threads_off", "1", null, null, "scatter", [ "1" ])
               ("walk", "1", null, "0", "walk", [ "1" ]) ]
         let build (threads: string) (slots: string) (scatter: string) (tag: string) =
             use _r = pinEnv "BLADE_FP_REPRO" "1"
             use _a = pinEnv "BLADE_FP_REASSOC" null
             use _c = pinEnv "BLADE_FP_CONTRACT" "off"
             use _b = pinEnv "BLADE_BLAS" "0"
             use _p = pinEnv "BLADE_PRINT" (String.concat "," rawScalars)
             use _k = pinEnv "BLADE_FOLD_ROW_SLOTS" slots
             use _o = pinEnv "BLADE_ORBIT_SCATTER" scatter
             use _g = pinEnv "BLADE_TILE_TUNE" null
             use _t = pinEnv "BLADE_OMP_THREADS" threads
             compileProgramModeWith false (rawBitTransform "\n    double s1 = " pokes rawScalars) outputDir (name + "_" + tag) src
         let routeOf (cpp: string) (s: string) =
             let scattered = System.Text.RegularExpressions.Regex.IsMatch(cpp, $@"\b{s} = blade_scatter::scatter[23]<")
             let slotted = cpp.Contains $"__rslot_{s}[__i0] = "
             match scattered, slotted with
             | true, false -> "scatter"
             | false, true -> "slots"
             | false, false -> "walk"
             | true, true -> "both"
         let rawOf (out: string) =
             out.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.StartsWith "RAW ") |> List.ofArray
         let results =
             builds |> List.map (fun (tag, threads, slots, scatter, want, teams) ->
                 match build threads slots scatter tag with
                 | Error e -> Error ($"{tag} build: {e}")
                 | Ok exe ->
                     let cpp = File.ReadAllText(Path.ChangeExtension(exe, ".cpp"))
                     let bad = rawScalars |> List.filter (fun s -> routeOf cpp s <> want) |> List.map (fun s -> $"{s} is {routeOf cpp s}")
                     if not bad.IsEmpty then Error ($"""{tag}: route wanted {want} for every binding, got {(String.concat ", " bad)}""")
                     else
                     let runs = teams |> List.map (fun t -> (t, runProgramFixedTeam exe t))
                     match runs |> List.tryPick (fun (t, r) -> match r with Error e -> Some ($"{tag} T={t}: {e}") | Ok _ -> None) with
                     | Some e -> Error e
                     | None -> Ok (tag, runs |> List.map (fun (t, r) -> (t, (match r with Ok o -> rawOf o | Error _ -> []))), cpp))
         match results |> List.tryPick (fun r -> match r with Error e -> Some e | Ok _ -> None) with
         | Some e -> fail name e
         | None ->
             let ok = results |> List.choose (fun r -> match r with Ok v -> Some v | Error _ -> None)
             let (_, walkRuns, _) = ok |> List.find (fun (tag, _, _) -> tag = "walk")
             let reference = snd walkRuns.Head
             let bitsOf (s: string) =
                 reference |> List.tryPick (fun l -> if l.StartsWith ($"RAW {s} ") then Some (System.Convert.ToUInt64(l.Substring(5 + s.Length).Trim(), 16)) else None)
             let isNaN (b: uint64) = (b &&& 0x7FF0000000000000UL) = 0x7FF0000000000000UL && (b &&& 0x000FFFFFFFFFFFFFUL) <> 0UL
             let isFinite (b: uint64) = (b &&& 0x7FF0000000000000UL) <> 0x7FF0000000000000UL
             let differ =
                 ok |> List.collect (fun (tag, runs, _) ->
                     runs |> List.choose (fun (t, lines) -> if lines <> reference then Some ($"""{tag} T={t} [{String.concat "; " lines}]""") else None))
             if reference.Length <> rawScalars.Length then
                 fail name ($"the walk printed {reference.Length} RAW line(s), wanted {rawScalars.Length}")
             elif bitsOf "s2" <> Some 0x8000000000000000UL then
                 fail name ($"""s2 must be -0.0 (0x8000000000000000) in the walk, got {bitsOf "s2"}""")
             elif bitsOf "s7" <> Some 0x8000000000000000UL then
                 fail name ($"""s7 (SEEDLESS inner `(+)` over -0.0 cells, a -0.0 outer seed) must be -0.0 (0x8000000000000000: the left fold of the cells, G74) in the walk, got {bitsOf "s7"}""")
             elif bitsOf "s8" <> Some rawNaNA then
                 fail name ($"""s8 (SEEDLESS rows, row 0's FIRST cell carrying payload 0x{rawNaNA:X16}) must carry that payload as the head in the walk, got {bitsOf "s8"}""")
             elif not ([ "s1"; "s3" ] |> List.forall (fun s -> bitsOf s |> Option.exists isFinite)) then
                 fail name ($"""s1 / s3 must be finite in the walk: {String.concat "; " reference}""")
             elif not ([ "s4"; "s5"; "s6" ] |> List.forall (fun s -> bitsOf s |> Option.exists isNaN)) then
                 fail name ($"""the poked NaN payloads did not reach s4 / s5 / s6 (the probe would not test them): {String.concat "; " reference}""")
             elif bitsOf "s4" = bitsOf "s6" then
                 fail name ($"""s4 and s6 (the same two payloads, met in opposite orders) carry the SAME bits in the walk: the payload probe would not distinguish an order change: {String.concat "; " reference}""")
             elif not differ.IsEmpty then
                 fail name ($"""raw bits differ from the true serial walk [{String.concat "; " reference}]: {String.concat " | " differ}""")
             else
                 let nanTxt = [ "s4"; "s5"; "s6" ] |> List.map (fun s -> $"{s} = 0x{(bitsOf s).Value:X16}") |> String.concat ", "
                 pass name ($"""every binding's route as wanted in each build (default / slots=0 / threads-off: the scatter; prefer: the row slots; the walk), and the SAME uint64 bits at T = 1, 4, 7: cancellation (s1, s3), -0.0 (s2), poked NaN payloads 0x{rawNaNA:X16} / 0x{rawNaNB:X16} -> {nanTxt}; seedless rows (G74): s7 = -0.0, s8 = the first cell's 0x{rawNaNA:X16}""")

        // ---- G51 IDENTITY: row-view folds in threaded complete rows -------
        // The stencil input-checksum shape `reduce(u(i), op[, s])` under an
        // outer `where omp` fold, BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off: the
        // threaded build (row slots TAKEN: 15 slot assignments, one per fold)
        // prints the SAME text as the BLADE_OMP_THREADS=1 build (the serial
        // walk), and each row-view spelling the same text as its twin.
        // Unseeded (head peel), seeded by a row-dependent expression, -0.0 rows
        // with -0.0 seeds (1 / s = -inf), a NaN row, a NON-commutative kernel
        // 3a + b, an unseeded -0.0 view (no nested twin: that spelling seeds its
        // unseeded builtin fold with +0), and the admission edges (Codex diff
        // review of W1-A2, finding 4): the MAP-request spelling (s7, twin s1),
        // a row body with a LEADING LET holding the seed (s8, twin s2), an outer
        // seed that is itself a nonzero pure FOLD (s9, twin n9). r* / q* =
        // value mod 1024 re-expose the low bits.
        // THE TEAMS ARE OBSERVED (finding 3): every run has dynamic teams off
        // and no thread limit (runProgramFixedTeam), and a second threaded build
        // generated under codegen test mode prints one [omp-coverage] line per
        // region -- each of the 15 must report teamsz = distinct = T at
        // T = 1 / 4 / 7 / 8 (521 rows: every member owns rows), with the same
        // values as the serial walk.
        (let name = "row_view_slots_identity_threads_vs_serial_walk"
         let src =
             "type Y = Idx<521>\ntype X = Idx<509>\n"
             + "let u = method_for(range<Y, X>) <@> lambda(i, j) -> (Float64((i * 7 + j * 3) % 11) * 0.37 + 0.1 / Float64(j + 1)) * (1.0 + 9999.0 * Float64((j % 4) / 3)) - 1.5 |> compute\n"
             + "let z = method_for(range<Y, X>) <@> lambda(i, j) -> -0.0 * Float64(i + j + 1) |> compute\n"
             + "let w = method_for(range<Y, X>) <@> lambda(i, j) -> sqrt(Float64((i * 5 + j) % 7) - 0.5) |> compute\n"
             + "let v = method_for(range<X>) <@> lambda(j) -> Float64((j * 3) % 7) * 0.37 + 0.01 / Float64(j + 1) |> compute\n"
             + "let s1 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), (+)), lambda(p, q) where omp -> p + q, 0.25)\n"
             + "let n1 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(method_for(range<X>) <@> lambda(j) -> u(i, j), (+)), lambda(p, q) where omp -> p + q, 0.25)\n"
             + "let r1 = s1 - floor(s1 / 1024.0) * 1024.0\n"
             + "let q1 = n1 - floor(n1 / 1024.0) * 1024.0\n"
             + "let s2 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), (+), 7.0 + Float64(i)), lambda(p, q) where omp -> p + q, 0.5)\n"
             + "let n2 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(method_for(range<X>) <@> lambda(j) -> u(i, j), (+), 7.0 + Float64(i)), lambda(p, q) where omp -> p + q, 0.5)\n"
             + "let r2 = s2 - floor(s2 / 1024.0) * 1024.0\n"
             + "let q2 = n2 - floor(n2 / 1024.0) * 1024.0\n"
             + "let s3 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(z(i), lambda(a, b) -> a + b, -0.0), lambda(p, q) where omp -> p + q, -0.0)\n"
             + "let n3 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(method_for(range<X>) <@> lambda(j) -> z(i, j), lambda(a, b) -> a + b, -0.0), lambda(p, q) where omp -> p + q, -0.0)\n"
             + "let z3 = 1.0 / s3\n"
             + "let s4 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(w(i), (+)), lambda(p, q) where omp -> p + q, 0.0)\n"
             + "let n4 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(method_for(range<X>) <@> lambda(j) -> w(i, j), (+)), lambda(p, q) where omp -> p + q, 0.0)\n"
             + "let s5 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), lambda(a, b) -> 3.0 * a + b, 1.0), lambda(p, q) where omp -> p + q, 0.0)\n"
             + "let n5 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(method_for(range<X>) <@> lambda(j) -> u(i, j), lambda(a, b) -> 3.0 * a + b, 1.0), lambda(p, q) where omp -> p + q, 0.0)\n"
             + "let s6 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(z(i), (+)), lambda(p, q) where omp -> p + q, -0.0)\n"
             + "let z6 = 1.0 / s6\n"
             + "let s7 = reduce(method_for(range<Y>) <@> lambda(i) where omp(i: 1) -> reduce(u(i), (+)), (+), 0.25)\n"
             + "let r7 = s7 - floor(s7 / 1024.0) * 1024.0\n"
             + "let s8 = reduce(method_for(range<Y>) <@> lambda(i) -> {\n    let t = 7.0 + Float64(i)\n    reduce(u(i), (+), t)\n}, lambda(p, q) where omp -> p + q, 0.5)\n"
             + "let r8 = s8 - floor(s8 / 1024.0) * 1024.0\n"
             + "let s9 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), (+)), lambda(p, q) where omp -> p + q, reduce(v, (+)))\n"
             + "let n9 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(method_for(range<X>) <@> lambda(j) -> u(i, j), (+)), lambda(p, q) where omp -> p + q, reduce(v, (+)))\n"
             + "let r9 = s9 - floor(s9 / 1024.0) * 1024.0\n"
             + "let q9 = n9 - floor(n9 / 1024.0) * 1024.0\n"
         let printed = "s1,n1,r1,q1,s2,n2,r2,q2,s3,n3,z3,s4,n4,s5,n5,s6,z6,s7,r7,s8,r8,s9,n9,r9,q9"
         let build (instrumented: bool) (threads: string) (tag: string) =
             use _r = pinEnv "BLADE_FP_REPRO" "1"
             use _c = pinEnv "BLADE_FP_CONTRACT" "off"
             use _p = pinEnv "BLADE_PRINT" printed
             use _k = pinEnv "BLADE_FOLD_ROW_SLOTS" null
             use _g = pinEnv "BLADE_TILE_TUNE" null
             use _l = pinEnv "BLADE_FOLD_LANES" null
             use _t = pinEnv "BLADE_OMP_THREADS" threads
             compileProgramMode instrumented outputDir (name + tag) src
         match build false null "_threaded", build false "1" "_serial", build true null "_observed" with
         | Error e, _, _ -> fail name ($"threaded build: {e}")
         | _, Error e, _ -> fail name ($"serial build: {e}")
         | _, _, Error e -> fail name ($"observed (test-mode) build: {e}")
         | Ok thrExe, Ok serExe, Ok obsExe ->
             let cppOfExe (exe: string) = File.ReadAllText(Path.ChangeExtension(exe, ".cpp"))
             let slotsOf (exe: string) = (System.Text.RegularExpressions.Regex.Matches(cppOfExe exe, @"__rslot_\w+\[__i0\] = ")).Count
             if slotsOf thrExe <> 15 || slotsOf obsExe <> 15 then
                 fail name ($"the threaded builds emitted {slotsOf thrExe} / {slotsOf obsExe} row-slot assignment(s), expected 15 (s1..s9, n1..n5, n9) -- the comparison would not test the form")
             elif (cppOfExe serExe).Contains "__rslot" then
                 fail name "the BLADE_OMP_THREADS=1 build emitted row slots (it must be the serial walk)"
             else
             let teams = [ "1"; "4"; "7"; "8" ]
             let runs (exe: string) = teams |> List.map (fun t -> (t, runProgramFixedTeam exe t))
             match runProgramFixedTeam serExe "1" with
             | Error e -> fail name ($"serial run: {e}")
             | Ok ser ->
             let thr = runs thrExe
             let obs = runs obsExe
             match (thr @ obs) |> List.tryPick (fun (t, r) -> match r with Error e -> Some ($"T={t}: {e}") | Ok _ -> None) with
             | Some e -> fail name ($"threaded run: {e}")
             | None ->
                 let outOf (r: Result<string, string>) = match r with Ok o -> o | Error _ -> ""
                 let valuesOnly (o: string) =
                     (stripTiming o).Replace("\r\n", "\n").Split('\n')
                     |> Array.filter (fun l -> not (l.StartsWith "[omp-coverage]"))
                     |> String.concat "\n"
                 let s = valuesOnly ser
                 let valueOf (n: string) =
                     let m = System.Text.RegularExpressions.Regex.Match(s, $@"(?m)^{n} = (.*?)\r?$")
                     if m.Success then Some m.Groups.[1].Value else None
                 let twins = [ ("s1", "n1"); ("r1", "q1"); ("s2", "n2"); ("r2", "q2"); ("s3", "n3"); ("s4", "n4"); ("s5", "n5")
                               ("s7", "s1"); ("r7", "r1"); ("s8", "s2"); ("r8", "r2"); ("s9", "n9"); ("r9", "q9") ]
                 let twinBad =
                     twins |> List.choose (fun (a, b) ->
                         match valueOf a, valueOf b with
                         | Some x, Some y when x = y -> None
                         | x, y -> Some ($"{a} = {x} vs {b} = {y}"))
                 let expected = [ "s3 = -0.0"; "n3 = -0.0"; "z3 = -inf"; "s6 = -0.0"; "z6 = -inf" ]
                 let missing = expected |> List.filter (fun e -> not (s.Contains e))
                 let nanOk = System.Text.RegularExpressions.Regex.IsMatch(s, @"(?m)^s4 = -?nan\r?$")
                 // the fold-valued seed must actually matter (s9 is not s1)
                 let seedMatters = valueOf "s9" <> valueOf "s1"
                 let differ =
                     (thr @ obs) |> List.choose (fun (t, r) -> if valuesOnly (outOf r) <> s then Some t else None)
                 // the observed teams: 15 regions, each teamsz = distinct = T
                 let teamBad =
                     obs |> List.choose (fun (t, r) ->
                         let want = int t
                         let ms = System.Text.RegularExpressions.Regex.Matches(outOf r, @"\[omp-coverage\] region=(\S+) teamsz=(\d+) distinct=(\d+) maxth=(\d+)")
                         let lines = [ for m in ms -> (m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value, int m.Groups.[4].Value) ]
                         if lines.Length <> 15 then Some ($"T={t}: {lines.Length} [omp-coverage] region line(s), expected 15")
                         else
                             match lines |> List.tryFind (fun (_, tz, d, _) -> tz <> want || d <> want) with
                             | Some (reg, tz, d, mx) -> Some ($"T={t}: region {reg} teamsz={tz} distinct={d} maxth={mx} (wanted {want} / {want})")
                             | None -> None)
                 if not missing.IsEmpty then
                     fail name ($"""serial walk output lacks {(String.concat " | " missing)}: {s}""")
                 elif not nanOk then
                     fail name ($"the NaN row's checksum must print nan: {s}")
                 elif not twinBad.IsEmpty then
                     fail name ($"""a row-view spelling differs from its twin: {(String.concat " | " twinBad)}""")
                 elif not seedMatters then
                     fail name "s9 (outer seed = a nonzero fold) printed the same as s1 (seed 0.25): the seed case would be vacuous"
                 elif not differ.IsEmpty then
                     fail name ($"""threaded row-view slots differ from the serial walk at T = {(String.concat ", " differ)} (production or observed build)""")
                 elif not teamBad.IsEmpty then
                     fail name ($"""observed teams are not the requested sizes: {(String.concat " | " teamBad)}""")
                 else
                     pass name "threaded (T = 1, 4, 7, 8; dynamic teams off, no thread limit; every one of the 15 regions observed at teamsz = distinct = T) text identical to the serial walk under REPRO + CONTRACT=off, every row-view spelling identical to its twin: low bits, signed zero (-0.0 / -inf), NaN, 3a + b, the map-request spelling, a leading-let seed, a fold-valued outer seed")

        // ---- G51, the LICENSED arm: values vs the previous form ----------
        // Default build (the Path B team with one blade_fold::sum per row, no
        // omp simd on the row loop) at T = 1 and 8 (dynamic teams off, no thread
        // limit), against the previous chunk form (BLADE_FOLD_LANES=0) and the
        // repro serial walk. Quarter-integer data are exact in every
        // association, so a1 / a2 pin the SEEDS (unseeded head peel; seed 7 + i
        // combined once per row -- the literal helper rewrite that dropped it
        // would print a2 = 66298.5); the admission edges (Codex diff review of
        // W1-A2, finding 4) print the same exact values in every build: a5, a
        // LEADING-LET row body holding the seed (the helper declines it: the
        // previous chunk form), a6, an outer seed that is itself a nonzero pure
        // FOLD (the helper keeps it; the seed is evaluated once, before the
        // team), k1, INTEGER elements (no integer helper: the previous form).
        // The -0.0 rows pin signed zero as TEXT, the licensed contract's way:
        // the tree's chains start at +0, so an all-(-0) total prints 0.0 / inf
        // in the default build -- as its previous form (omp simd private copies
        // from +0) and the LLVM lane print it -- for the row-view spellings a3 /
        // a4 and the nested-range twin n3 alike, while the repro walk keeps
        // -0.0 / -inf (BLADE_FOLD_LANES=0's serial chunks keep it too, gap G40,
        // so only its exact seeds and c1 are compared); c1, cancellation-heavy,
        // stays within the licensed 1e-9 of both references.
        (let name = "row_view_licensed_helper_values"
         let src =
             "type Y = Idx<521>\ntype X = Idx<509>\n"
             + "let u = method_for(range<Y, X>) <@> lambda(i, j) -> Float64((i * 7 + j * 3) % 11) * 0.25 - 1.0 |> compute\n"
             + "let ui = method_for(range<Y, X>) <@> lambda(i, j) -> (Int64(i) * 7 + Int64(j) * 3) % 11 - 4 |> compute\n"
             + "let c = method_for(range<Y, X>) <@> lambda(i, j) -> (Float64((i * 7 + j * 3) % 11) * 0.37 + 0.1 / Float64(j + 1)) * (1.0 + 9999.0 * Float64((j % 4) / 3)) - 1.5 |> compute\n"
             + "let z = method_for(range<Y, X>) <@> lambda(i, j) -> -0.0 * Float64(i + j + 1) |> compute\n"
             + "let v = method_for(range<X>) <@> lambda(j) -> Float64(j % 5) * 0.25 |> compute\n"
             + "let a1 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), (+)), lambda(p, q) where omp -> p + q, 0.25)\n"
             + "let a2 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), (+), 7.0 + Float64(i)), lambda(p, q) where omp -> p + q, 0.5)\n"
             + "let a3 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(z(i), (+), -0.0), lambda(p, q) where omp -> p + q, -0.0)\n"
             + "let i3 = 1.0 / a3\n"
             + "let n3 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(method_for(range<X>) <@> lambda(j) -> z(i, j), lambda(a, b) -> a + b, -0.0), lambda(p, q) where omp -> p + q, -0.0)\n"
             + "let j3 = 1.0 / n3\n"
             + "let a4 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(z(i), (+)), lambda(p, q) where omp -> p + q, -0.0)\n"
             + "let i4 = 1.0 / a4\n"
             + "let a5 = reduce(method_for(range<Y>) <@> lambda(i) -> {\n    let t = 7.0 + Float64(i)\n    reduce(u(i), (+), t)\n}, lambda(p, q) where omp -> p + q, 0.5)\n"
             + "let a6 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i), (+)), lambda(p, q) where omp -> p + q, reduce(v, (+)))\n"
             + "let k1 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(ui(i), (+)), lambda(p, q) where omp -> p + q, 0)\n"
             + "let c1 = reduce(method_for(range<Y>) <@> lambda(i) -> reduce(c(i), (+)), lambda(p, q) where omp -> p + q, 0.25)\n"
         let build (repro: string) (lanes: string) (threads: string) (tag: string) =
             use _r = pinEnv "BLADE_FP_REPRO" repro
             use _c = pinEnv "BLADE_FP_CONTRACT" null
             use _p = pinEnv "BLADE_PRINT" "a1,a2,a3,i3,n3,j3,a4,i4,a5,a6,k1,c1"
             use _l = pinEnv "BLADE_FOLD_LANES" lanes
             use _t = pinEnv "BLADE_OMP_THREADS" threads
             compileProgram outputDir (name + tag) src
         match build null null null "_helper", build null "0" null "_previous", build "1" null "1" "_repro_serial" with
         | Error e, _, _ -> fail name ($"helper build: {e}")
         | _, Error e, _ -> fail name ($"previous-form build: {e}")
         | _, _, Error e -> fail name ($"repro serial build: {e}")
         | Ok newExe, Ok oldExe, Ok serExe ->
             let cppOfExe (exe: string) = File.ReadAllText(Path.ChangeExtension(exe, ".cpp"))
             let count (rx: string) (exe: string) = (System.Text.RegularExpressions.Regex.Matches(cppOfExe exe, rx)).Count
             let helpers = count @"blade_fold::sum<double, 8>\(" newExe
             // the folds the helper declines keep the previous chunk form: a5
             // (leading let), n3 (nested range), k1 (integer elements)
             let simd = count @"BLADE_OMP_SIMD_REDUCTION\(\+:__racc_" newExe
             if helpers <> 6 then
                 fail name ($"the default build emitted {helpers} blade_fold::sum call(s), expected 6 (a1..a4, a6, c1)")
             elif simd <> 3 then
                 fail name ($"the default build has {simd} Path B chunk(s) with an omp simd reduction, expected 3 (the declined a5, n3, k1 keep the previous chunk form; no helper row loop carries one)")
             elif (cppOfExe oldExe).Contains "blade_fold::sum<" then
                 fail name "the BLADE_FOLD_LANES=0 build took the helper arm (the knob must keep the previous form)"
             else
             match runProgramFixedTeam newExe "1", runProgramFixedTeam newExe "8", runProgramFixedTeam oldExe "8", runProgramFixedTeam serExe "1" with
             | Error e, _, _, _ | _, Error e, _, _ -> fail name ($"helper run: {e}")
             | _, _, Error e, _ -> fail name ($"previous-form run: {e}")
             | _, _, _, Error e -> fail name ($"repro serial run: {e}")
             | Ok n1, Ok n8, Ok o8, Ok sr ->
                 let exactAll = [ "a1 = 66298.25"; "a2 = 205405.5"; "a5 = 205405.5"; "a6 = 66552.0"; "k1 = 265192" ]
                 let licensed = exactAll @ [ "a3 = 0.0"; "i3 = inf"; "n3 = 0.0"; "j3 = inf"; "a4 = 0.0"; "i4 = inf" ]
                 let strict = exactAll @ [ "a3 = -0.0"; "i3 = -inf"; "n3 = -0.0"; "j3 = -inf"; "a4 = -0.0"; "i4 = -inf" ]
                 let lacksOf (want: string list) (out: string) =
                     let lines = (stripTiming out).Split('\n') |> Array.map (fun l -> l.Trim())
                     want |> List.filter (fun e -> not (Array.contains e lines))
                 let lacks = lacksOf licensed
                 let c1Of (out: string) = Map.tryFind "c1" (scalarBindings (stripTiming out))
                 let within (a: float) (b: float) = abs (a - b) <= 1e-9 * max 1.0 (abs b)
                 match lacks n1, lacks n8, lacksOf exactAll o8, lacksOf strict sr with
                 | (_ :: _ as m), _, _, _ -> fail name ($"""helper build at T=1 lacks {(String.concat " | " m)}""")
                 | _, (_ :: _ as m), _, _ -> fail name ($"""helper build at T=8 lacks {(String.concat " | " m)}""")
                 | _, _, (_ :: _ as m), _ -> fail name ($"""the previous form at T=8 lacks {(String.concat " | " m)} (the exact values must agree with it)""")
                 | _, _, _, (_ :: _ as m) -> fail name ($"""repro serial walk lacks {(String.concat " | " m)}""")
                 | [], [], [], [] ->
                     match c1Of n1, c1Of n8, c1Of o8, c1Of sr with
                     | Some x1, Some x8, Some old8, Some ser ->
                         if within x1 ser && within x8 ser && within x8 old8 then
                             pass name (sprintf "seeds exact (a1 unseeded, a2 seed once per row, a5 leading-let seed, a6 fold-valued outer seed, k1 integer) at T = 1 and 8 and in the previous form; all-(-0) totals a3 / n3 / a4 print 0.0 / inf under the licence, -0.0 / -inf in the repro walk; c1 within 1e-9 of the previous form (%.17g vs %.17g) and the repro walk (%.17g)" x8 old8 ser)
                         else fail name (sprintf "c1 outside the licensed 1e-9: helper T=1 %.17g / T=8 %.17g, previous %.17g, repro serial %.17g" x1 x8 old8 ser)
                     | _ -> fail name "no c1 binding in some output (comparison would be vacuous)")

        // ---- G51: corpus loops/533 itself, both build modes, as TEXT ---------
        // Value pins compare a zero's NUMBER, not its sign (Expect.fs), so the
        // corpus file's three licensed spellings are pinned here as printed
        // text (Codex diff review of W1-A2, finding 2): the default build (the
        // helper arm: 0.0, the licensed +0 chain starts), BLADE_FP_REPRO=1 +
        // CONTRACT=off (the repro row slots: -0.0, the serial walk), and the G40
        // probe BLADE_FOLD_LANES=0 (the previous serial chunks: -0.0); the
        // `where repro` strict spellings print -0.0 / true in all three. T = 1
        // and 8, dynamic teams off.
        (let name = "row_view_signed_zero_corpus_533_both_modes"
         let path = Path.Combine(Directory.GetCurrentDirectory(), "tests", "corpus", "loops", "533_row_view_fold_signed_zeros.blade")
         if not (File.Exists path) then fail name ($"corpus file not found: {path} (run from the repository root)")
         else
         let src = File.ReadAllText path
         let build (repro: string) (contract: string) (lanes: string) (tag: string) =
             use _r = pinEnv "BLADE_FP_REPRO" repro
             use _c = pinEnv "BLADE_FP_CONTRACT" contract
             use _p = pinEnv "BLADE_PRINT" null
             use _l = pinEnv "BLADE_FOLD_LANES" lanes
             use _t = pinEnv "BLADE_OMP_THREADS" null
             use _k = pinEnv "BLADE_FOLD_ROW_SLOTS" null
             compileProgram outputDir (name + tag) src
         let strictLines = [ "strict = -0.0"; "strict_negative = true"; "strict_peeled = -0.0"; "strict_peeled_negative = true" ]
         let modes =
             [ ("default", (null, null, null), [ "neg = 0.0"; "neg_peeled = 0.0"; "mixed = 0.0" ] @ strictLines, "blade_fold::sum<double, 8>(", 3)
               ("repro", ("1", "off", null), [ "neg = -0.0"; "neg_peeled = -0.0"; "mixed = 0.0" ] @ strictLines, "__rslot_", 3)
               ("fold_lanes_off", (null, null, "0"), [ "neg = -0.0"; "neg_peeled = -0.0"; "mixed = 0.0" ] @ strictLines, "", 0) ]
         let bad =
             modes |> List.collect (fun (tag, (r, c, l), want, needle, needleCount) ->
                 match build r c l ("_" + tag) with
                 | Error e -> [ $"{tag} build: {e}" ]
                 | Ok exe ->
                     let cpp = File.ReadAllText(Path.ChangeExtension(exe, ".cpp"))
                     let formBad =
                         if needle = "" then (if cpp.Contains "blade_fold::sum<" then [ $"{tag}: took the helper arm" ] else [])
                         else
                             let n = (System.Text.RegularExpressions.Regex.Matches(cpp, System.Text.RegularExpressions.Regex.Escape needle)).Count
                             let n = if needle = "__rslot_" then (System.Text.RegularExpressions.Regex.Matches(cpp, @"__rslot_\w+\[__i0\] = ")).Count else n
                             if n <> needleCount then [ $"{tag}: {n} x `{needle}`, expected {needleCount} (the form was not taken)" ] else []
                     let runBad =
                         [ "1"; "8" ] |> List.collect (fun t ->
                             match runProgramFixedTeam exe t with
                             | Error e -> [ $"{tag} T={t}: {e}" ]
                             | Ok out ->
                                 let lines = (stripTiming out).Split('\n') |> Array.map (fun x -> x.Trim())
                                 want |> List.filter (fun w -> not (Array.contains w lines)) |> List.map (fun w -> $"{tag} T={t} lacks `{w}`"))
                     formBad @ runBad)
         if bad.IsEmpty then
             pass name "loops/533 as text: default 0.0 x3 (helper arm), repro -0.0 / -0.0 / 0.0 (row slots), BLADE_FOLD_LANES=0 -0.0 / -0.0 / 0.0 (G40 probe); the strict `where repro` spellings -0.0 / true in all three modes, T = 1 and 8"
         else fail name (String.concat " | " bad))

        // ---- the outer-kernel-write counterexample, RUN (blocker 1) -------
        // Declined, so the knob is inert: knob on and knob off print the same
        // serial value 70710120.0 (with the slots, every row would read
        // G(0) = 0 and the total would drop by 135460). Repro build: the
        // licensed default build's Path B threads the same fold kernel.
        (let name = "row_slots_outer_kernel_write_values"
         let build (knob: string) (tag: string) =
             use _r = pinEnv "BLADE_FP_REPRO" "1"
             use _c = pinEnv "BLADE_FP_CONTRACT" "off"
             use _p = pinEnv "BLADE_PRINT" "s"
             use _k = pinEnv "BLADE_FOLD_ROW_SLOTS" knob
             use _g = pinEnv "BLADE_TILE_TUNE" null
             use _t = pinEnv "BLADE_OMP_THREADS" null
             compileProgram outputDir (name + tag) rowSlotOuterWriteSrc
         match build null "_knob_on", build "0" "_knob_off" with
         | Error e, _ -> fail name ($"knob-on build: {e}")
         | _, Error e -> fail name ($"knob-off build: {e}")
         | Ok onExe, Ok offExe ->
             if (File.ReadAllText(Path.ChangeExtension(onExe, ".cpp"))).Contains "__rslot" then
                 fail name "the outer kernel writes G, yet the knob-on build emitted row slots"
             else
             match runProgram onExe "4", runProgram offExe "4" with
             | Error e, _ | _, Error e -> fail name ($"run: {e}")
             | Ok onOut, Ok offOut ->
                 if stripTiming onOut <> stripTiming offOut then
                     fail name ($"knob on [{stripTiming onOut}] vs off [{stripTiming offOut}]")
                 elif not ((stripTiming onOut).Contains "s = 70710120.0") then
                     fail name ($"expected the serial walk's s = 70710120.0, got: {stripTiming onOut}")
                 else
                     pass name "declined; knob on == knob off == the serial walk (s = 70710120.0)")

        // ---- gap G41, RUN: writing fold kernels / map bodies stay ordered ---
        // Corpus loops/492 (a `comm` fold kernel writing G, reached through the
        // map's omp(i: 1)), 495 (the same write through a ROW VIEW of G) and 493
        // (a `where omp` fold over a map body that writes). Before the fix the
        // licensed build threaded or laned them: five mt8 runs, five totals.
        // Every build and team size must print the serial total AND counter.
        let g41Cases =
            let pre = "type I = Idx<521>\ntype K = Idx<2>\n"
            [ ("g41_comm_fold_kernel_write",
               pre + "let mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n"
               + "let total = reduce(method_for(range<I>) <@> lambda(i) where omp(i: 1) -> reduce(method_for(range<I>) <@> lambda(j) -> G((0 : K)) + Float64(i), (+)), lambda(a, b) where comm(a, b) -> { G((0 : K)) = G((0 : K)) + 1.0; a + b }, 0.0)\n"
               + "let g0 = G((0 : K))\n",
               141149320.0, 521.0)
              ("g41_comm_fold_kernel_write_through_view",
               pre + "let mut G: Array<Float64 like K, K> = method_for(range<K>, range<K>) <@> lambda(k, l) -> 0.0 |> compute\n"
               + "let total = reduce(method_for(range<I>) <@> lambda(i) where omp(i: 1) -> reduce(method_for(range<I>) <@> lambda(j) -> G((0 : K), (0 : K)) + Float64(i), (+)), lambda(a, b) where comm(a, b) -> {\n    let t = G((0 : K))\n    t((0 : K)) = t((0 : K)) + 1.0\n    a + b\n}, 0.0)\n"
               + "let g0 = G((0 : K), (0 : K))\n",
               141149320.0, 521.0)
              ("g41_comm_fold_kernel_write_through_rebound_local",
               pre + "let mut G: Array<Float64 like K, K> = method_for(range<K>, range<K>) <@> lambda(k, l) -> 0.0 |> compute\n"
               + "let total = reduce(method_for(range<I>) <@> lambda(i) where omp(i: 1) -> reduce(method_for(range<I>) <@> lambda(j) -> G((0 : K), (0 : K)) + Float64(i), (+)), lambda(a, b) where comm(a, b) -> {\n    let mut t = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n    t = G((0 : K))\n    t((0 : K)) = t((0 : K)) + 1.0\n    a + b\n}, 0.0)\n"
               + "let g0 = G((0 : K), (0 : K))\n",
               141149320.0, 521.0)
              ("g41_omp_fold_over_writing_map",
               pre + "let mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n"
               + "let total = reduce(method_for(range<I>) <@> lambda(i) -> { G((0 : K)) = G((0 : K)) + 1.0; G((0 : K)) + Float64(i) }, lambda(a, b) where omp -> a + b, 0.0)\n"
               + "let g0 = G((0 : K))\n",
               271441.0, 521.0) ]
        for (name, src, expTotal, expG0) in g41Cases do
            let build (threads: string) (tag: string) =
                use _p = pinEnv "BLADE_PRINT" "total,g0"
                use _t = pinEnv "BLADE_OMP_THREADS" threads
                compileProgram outputDir (name + tag) src
            match build "1" "_serial", build null "_threads_on" with
            | Error e, _ | _, Error e -> fail name ($"build: {e}")
            | Ok serExe, Ok thrExe ->
                let runs =
                    [ ("BLADE_OMP_THREADS=1", runProgram serExe "1")
                      ("emission on, OMP_NUM_THREADS=1", runProgram thrExe "1") ]
                    @ [ for k in 1 .. 5 -> ($"emission on, OMP_NUM_THREADS=8 (run {k})", runProgram thrExe "8") ]
                let bad =
                    runs |> List.choose (fun (what, r) ->
                        match r with
                        | Error e -> Some ($"{what}: {e}")
                        | Ok out ->
                            let vals = scalarBindings out
                            match Map.tryFind "total" vals, Map.tryFind "g0" vals with
                            | Some t, Some g when t = expTotal && g = expG0 -> None
                            | t, g -> Some ($"{what}: total = {t}, g0 = {g}"))
                if bad.IsEmpty then pass name ($"total = {expTotal}, g0 = {expG0} in all 7 runs (serial build; threads on at T = 1 and 5x T = 8)")
                else fail name (String.concat " | " bad)

        // ---- G7: the threaded reduction join -------------------------------
        // Emission + record shape per build (both entry shapes), then the
        // values: the threaded binary at T = 1 / 3 / 8 against the serial
        // (BLADE_OMP_THREADS=1) build, EXACT (integer-valued cells).
        for (name, pins, src, check) in g7EmitCases do
            let disposables =
                ([ "BLADE_FP_REPRO"; "BLADE_FP_REASSOC"; "BLADE_OMP_THREADS"; "BLADE_JOIN_THREADS"; "BLADE_FOLD_LANES"; "BLADE_SIBLING_FOLDS" ]
                 |> List.filter (fun k -> not (pins |> List.exists (fun (p, _) -> p = k)))
                 |> List.map (fun k -> pinEnv k null))
                @ (pins |> List.map (fun (k, v) -> pinEnv k v))
            try
                match planAndCppOf name src with
                | Error e -> fail name e
                | Ok (ds, cpp) ->
                    match check ds cpp with
                    | Ok detail -> pass name detail
                    | Error why -> fail name why
            finally
                for d in List.rev disposables do d.Dispose()
        for (name, src) in g7RefusedCases do
            match planAndCppOf name src with
            | Ok _ -> fail name "the writing join lowered (G43: it must be refused, BL4005)"
            | Error e when e.Contains "a leg of this join writes a captured binding" ->
                pass name "refused at lowering: BL4005, a leg of this join writes a captured binding"
            | Error e -> fail name ($"refused for another reason: {e}")
        let g7Values (name: string) (src: string) (expected: string list) =
            let build (threads: string) (tag: string) =
                use _r = pinEnv "BLADE_FP_REPRO" null
                use _t = pinEnv "BLADE_OMP_THREADS" threads
                use _j = pinEnv "BLADE_JOIN_THREADS" null
                compileProgram outputDir (name + tag) src
            match build null "_team", build "1" "_serial" with
            | Error e, _ -> fail name ($"threaded build: {e}")
            | _, Error e -> fail name ($"serial build: {e}")
            | Ok teamExe, Ok serExe ->
                if not ((File.ReadAllText(Path.ChangeExtension(teamExe, ".cpp"))).Contains "BLADE_OMP_PARALLEL_NUM_THREADS(") then
                    fail name "the threaded build has no team (the comparison would not test the form)"
                else
                match runProgram serExe "1", runProgram teamExe "1", runProgram teamExe "3", runProgram teamExe "8" with
                | Error e, _, _, _ -> fail name ($"serial run: {e}")
                | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> fail name ($"threaded run: {e}")
                | Ok ser, Ok t1, Ok t3, Ok t8 ->
                    let s = stripTiming ser
                    let missing = expected |> List.filter (fun e -> not (s.Contains e))
                    if not missing.IsEmpty then
                        fail name ($"""serial build lacks {(String.concat " | " missing)}""")
                    elif stripTiming t1 <> s || stripTiming t3 <> s || stripTiming t8 <> s then
                        fail name "the threaded join's output differs from the serial build at some team size"
                    else
                        pass name ($"""threaded T = 1 / 3 / 8 identical to the serial build: {(String.concat ", " expected)}""")
        g7Values "g7_fused_join_values" (g7FusedSrc "lambda(u, v) where omp -> u + v")
            [ "s1 = 1006.0"; "s2 = 5044.0"; "s3 = 13096.0"; "s4 = 53440.0"; "checksum = 264142.0" ]
        g7Values "g7_pack_join_values" (g7PackSrc "lambda(p, q) where omp -> p + q" "")
            [ "a = 1522.0"; "b = 3528.0"; "c = 1036.0" ]
        // product legs, non-identity caller seeds (privates from 1): both shapes
        g7Values "g7_pack_join_product_values" g7ProductPackSrc [ "p1 = 1.0"; "p2 = 0.75" ]
        g7Values "g7_fused_join_product_values" g7ProductFusedSrc [ "f1 = 1.0"; "f2 = 0.5" ]
        // zero extent: no team opens, every leg is exactly its seed, both shapes
        g7Values "g7_zero_extent_seeded_joins" g7ZeroSrc [ "z1 = 10.0"; "z2 = 10.0"; "y1 = 20.0"; "y2 = 30.0" ]

        // ---- G55: a SCALAR omp fold let in a FUNCTION body takes the statement
        // fold path (the Path A team) exactly as the top-level spelling does,
        // instead of the expression form's "reduce in expression position"
        // decline. (1) the emission of `fsum` carries the team and no decline;
        // a fold that is an OPERAND (`fsum2`) keeps the decline, with the
        // corrected suggestion; (2) licensed, threads at T = 4: fb within 1e-9
        // of top; (3) BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off: the raw Float64
        // bits (BLADE_PRINT_BITS) of fb equal top's, in the threaded build at
        // T = 1 / 4 and in the BLADE_OMP_THREADS=1 build. Awkward data (1e4-
        // scaled cells beside unit ones) so another association would show.
        let g55Src =
            "type I = Idx<2003>\n"
            + "let x = method_for(range<I>) <@> lambda(i) -> (Float64((i * 7) % 11) * 0.37 + 0.1 / Float64(i + 1)) * (1.0 + 9999.0 * Float64((i % 4) / 3)) |> compute\n"
            + "function fsum(v: Array<Float64 like I>) -> Float64 = {\n    let s = reduce(v, lambda(p, q) where omp -> p + q, 0.25)\n    s\n}\n"
            + "function fsum2(v: Array<Float64 like I>) -> Float64 = {\n    let s = reduce(v, lambda(p, q) where omp -> p + q, 0.0) * 2.0\n    s\n}\n"
            + "let top = reduce(x, lambda(p, q) where omp -> p + q, 0.25)\n"
            + "let fb = fsum(x)\n"
            + "let fb2 = fsum2(x)\n"
        let withEnv (pairs: (string * string) list) (f: unit -> 'a) : 'a =
            let priors = pairs |> List.map (fun (k, _) -> (k, System.Environment.GetEnvironmentVariable k))
            for (k, v) in pairs do System.Environment.SetEnvironmentVariable(k, v)
            try f ()
            finally for (k, v) in priors do System.Environment.SetEnvironmentVariable(k, v)
        let funcBody (cpp: string) (fname: string) =
            let cpp = cpp.Replace("\r\n", "\n")
            // the DEFINITION (`double f(...) {`), not the forward declaration
            let m = System.Text.RegularExpressions.Regex.Match(cpp, "\ndouble " + fname + @"\([^;{]*\) \{\n")
            let defAt = if m.Success then m.Index + m.Length - 2 else -1
            let endAt = if defAt < 0 then -1 else cpp.IndexOf("\n}\n", defAt)
            if endAt < 0 then None else Some (cpp.Substring(defAt, endAt - defAt))
        (let name = "g55_function_body_scalar_fold_let_threads"
         match compileProgram outputDir (name + "_lic") g55Src with
         | Error e -> fail name ($"licensed build: {e}")
         | Ok exe ->
         let cpp = File.ReadAllText(Path.ChangeExtension(exe, ".cpp"))
         match funcBody cpp "fsum", funcBody cpp "fsum2" with
         | None, _ | _, None -> fail name "no fsum / fsum2 definition in the emission"
         | Some fs, Some fs2 ->
         if not (fs.Contains "BLADE_OMP_PARALLEL_FOR_SIMD_REDUCTION(+:") || fs.Contains "reduce in expression position" then
             fail name "fsum's scalar fold let did not take the statement fold path (no Path A team, or the expression decline)"
         elif not (fs2.Contains "reduce in expression position" && fs2.Contains "WHOLE value of its own `let`") then
             fail name "fsum2's operand fold lost its expression-position decline or the corrected suggestion"
         else
         match runProgram exe forcedThreads with
         | Error e -> fail name ($"licensed run: {e}")
         | Ok out ->
         let sb = scalarBindings out
         match Map.tryFind "top" sb, Map.tryFind "fb" sb with
         | Some t, Some f when abs (t - f) <= 1e-9 * max 1.0 (abs t) ->
             let bits (o: string) (k: string) =
                 let m = System.Text.RegularExpressions.Regex.Match(o, "(?m)^" + k + @" = \S+ #([0-9a-f]{16})")
                 if m.Success then Some m.Groups.[1].Value else None
             let repro = [ "BLADE_FP_REPRO", "1"; "BLADE_FP_CONTRACT", "off" ]
             match withEnv repro (fun () -> compileProgram outputDir (name + "_rep") g55Src),
                   withEnv (("BLADE_OMP_THREADS", "1") :: repro) (fun () -> compileProgram outputDir (name + "_rep1") g55Src) with
             | Error e, _ | _, Error e -> fail name ($"repro build: {e}")
             | Ok repExe, Ok rep1Exe ->
                 let runs =
                     withEnv [ "BLADE_PRINT_BITS", "1" ] (fun () ->
                         [ runProgram repExe "1"; runProgram repExe forcedThreads; runProgram rep1Exe forcedThreads ])
                 match runs |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
                 | Some e -> fail name ($"repro run: {e}")
                 | None ->
                 let outs = runs |> List.choose (function Ok o -> Some o | Error _ -> None)
                 let pairs = outs |> List.map (fun o -> (bits o "top", bits o "fb"))
                 match pairs with
                 | (Some t0, Some f0) :: _ when pairs |> List.forall (fun p -> p = (Some t0, Some f0)) && t0 = f0 ->
                     pass name ($"fsum threads (Path A); licensed |fb - top| = {abs (t - f)}; repro bits fb = top = #{t0} at T = 1 / 4 and with BLADE_OMP_THREADS=1")
                 | _ -> fail name (sprintf "repro bits differ (top, fb) per run: %A" pairs)
         | t, f -> fail name (sprintf "licensed values top %A fb %A" t f))

        // G55 census: the function-body let's `[omp]` record is the top-level
        // spelling's -- applied (Path A) with threads, declined with the knob's
        // census text under BLADE_OMP_THREADS=1 and under BLADE_FP_REPRO=1.
        // The record's SUBJECT is one stable source-facing name, `fsum.s` (the
        // function and the fold's let), in EVERY outcome (W4B-C; it used to be
        // the emitted `__v<id>` when applied and `fsum` when declined): exactly
        // one omp record names `fsum.s`, none names `fsum` or an emitted
        // `__v<id>`, and its outcome + reason + evidence equal top's (the
        // reduction variable's emitted name normalized away).
        (let name = "g55_function_body_fold_let_census_threads_off_and_repro"
         let norm (s: string) = System.Text.RegularExpressions.Regex.Replace(s, @"\(\+:(?:__v\d+|top)\)", "(+:<acc>)")
         let shown (d: Blade.Effects.Decision) =
             let outcome =
                 match d.Outcome with
                 | Blade.Effects.Applied -> "applied"
                 | Blade.Effects.Declined why -> "declined -- " + why
             norm (outcome + " [" + String.concat "; " d.Evidence + "]")
         let check (pins: (string * string) list) (wantPrefix: string) =
             match withEnv pins (fun () -> planAndCppOf (name + "_plan") g55Src) with
             | Error e -> Error e
             | Ok (ds, _) ->
                 let omp = ds |> List.filter (fun d -> d.Rule = "omp")
                 let bySubject (s: string) = omp |> List.filter (fun d -> d.Subject = s)
                 let stray = omp |> List.filter (fun d -> d.Subject = "fsum" || d.Subject.StartsWith "__v")
                 match bySubject "fsum.s", bySubject "top", bySubject "fsum2" with
                 | [ f ], [ t ], [ f2 ] when stray.IsEmpty ->
                     let fsText, topText = shown f, shown t
                     if fsText <> topText then Error ($"fsum.s `{fsText}` differs from top `{topText}`")
                     elif not (fsText.StartsWith wantPrefix) then Error ($"fsum.s `{fsText}`, want it to start `{wantPrefix}`")
                     elif not ((shown f2).StartsWith "declined -- reduce in expression position") then Error ($"fsum2 `{shown f2}`")
                     else Ok ()
                 | fs, tp, f2 ->
                     Error (sprintf "omp subjects: fsum.s %d, top %d, fsum2 %d, stray %A" fs.Length tp.Length f2.Length
                                (stray |> List.map Blade.Effects.Decisions.render))
         match check [] "applied [BLADE_OMP_PARALLEL_FOR_SIMD_REDUCTION(+:<acc>)",
               check [ "BLADE_OMP_THREADS", "1" ] "declined -- BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP",
               check [ "BLADE_FP_REPRO", "1" ] "declined -- BLADE_FP_REPRO=1 -- this build keeps the serial association of every fold" with
         | Ok (), Ok (), Ok () -> pass name "one subject `fsum.s` in every outcome; its record equals top's: applied (Path A) / declined with the BLADE_OMP_THREADS=1 and BLADE_FP_REPRO=1 census text"
         | Error e, _, _ | _, Error e, _ | _, _, Error e -> fail name e)

        // G55, NAMED kernels (fix round of the W4B-C Codex look): two body
        // lets folding with the named omp function `add` and one folding with
        // a separately bound local lambda `k` carry no let name in the IR
        // (Lowering records none; gap G55), so their subject is the k-th
        // scalar fold let of the body -- `two.<fold 1>`, `two.<fold 2>`,
        // `loc.<fold 1>`: DISTINCT per fold and the SAME in every outcome
        // (applied / declined under BLADE_OMP_THREADS=1 / under
        // BLADE_FP_REPRO=1); no record names the bare function or an emitted
        // `__v<id>`. MIXED-KERNEL control (the second Codex look): in `mix` an
        // inline-lambda fold let NAMED `fold2` (`mix.fold2`) sits beside a
        // named-kernel fold, the body's 2nd (`mix.<fold 2>`): the fallback
        // spelling is one no let name can take, so the two never collide.
        // (Different index types keep the sibling-fold pass from joining folds.)
        (let name = "g55_named_kernel_fold_lets_distinct_subjects"
         let src =
             "type I = Idx<2003>\ntype J = Idx<1999>\ntype K = Idx<1997>\n"
             + "let x = method_for(range<I>) <@> lambda(i) -> Float64(i % 7) * 0.5 |> compute\n"
             + "let y = method_for(range<J>) <@> lambda(j) -> Float64(j % 5) * 0.25 |> compute\n"
             + "let z = method_for(range<K>) <@> lambda(k) -> Float64(k % 3) * 0.125 |> compute\n"
             + "function add(p: Float64, q: Float64) where omp -> Float64 = p + q\n"
             + "function two(v: Array<Float64 like I>, w: Array<Float64 like J>) -> Float64 = {\n    let s = reduce(v, add, 0.25)\n    let t = reduce(w, add, 0.5)\n    s + t\n}\n"
             + "function loc(v: Array<Float64 like I>) -> Float64 = {\n    let k = lambda(p: Float64, q: Float64) where omp -> p + q\n    let u = reduce(v, k, 0.0)\n    u\n}\n"
             + "function mix(v: Array<Float64 like I>, w: Array<Float64 like K>) -> Float64 = {\n    let fold2 = reduce(v, lambda(p, q) where omp -> p + q, 0.0)\n    let s = reduce(w, add, 0.5)\n    fold2 + s\n}\n"
             + "let a = two(x, y)\nlet b = loc(x)\nlet c = mix(x, z)\n"
         let want = [ "loc.<fold 1>"; "mix.<fold 2>"; "mix.fold2"; "two.<fold 1>"; "two.<fold 2>" ] |> List.sort
         let check (pins: (string * string) list) (wantPrefix: string) =
             match withEnv pins (fun () -> planAndCppOf (name + "_plan") src) with
             | Error e -> Error e
             | Ok (ds, _) ->
                 let omp = ds |> List.filter (fun d -> d.Rule = "omp")
                 let subjects = omp |> List.map (fun d -> d.Subject) |> List.sort
                 let outcomeOf (d: Blade.Effects.Decision) =
                     match d.Outcome with
                     | Blade.Effects.Applied -> "applied [" + String.concat "; " d.Evidence + "]"
                     | Blade.Effects.Declined why -> "declined -- " + why
                 if subjects <> want then Error (sprintf "omp subjects %A, want %A" subjects want)
                 else
                 match omp |> List.tryFind (fun d -> not ((outcomeOf d).StartsWith wantPrefix)) with
                 | Some d -> Error ($"{d.Subject}: `{outcomeOf d}`, want it to start `{wantPrefix}`")
                 | None -> Ok ()
         match check [] "applied [BLADE_OMP_PARALLEL_FOR_SIMD_REDUCTION(+:",
               check [ "BLADE_OMP_THREADS", "1" ] "declined -- BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP",
               check [ "BLADE_FP_REPRO", "1" ] "declined -- BLADE_FP_REPRO=1 -- this build keeps the serial association of every fold" with
         | Ok (), Ok (), Ok () -> pass name "subjects two.<fold 1> / two.<fold 2> / loc.<fold 1> / mix.fold2 / mix.<fold 2>, distinct, in every outcome: applied (Path A) / BLADE_OMP_THREADS=1 / BLADE_FP_REPRO=1"
         | Error e, _, _ | _, Error e, _ | _, _, Error e -> fail name e)

        // G55 first failure: an empty unseeded fold (BL8003) and a failing seed
        // (BL8013), each lowered FILE-BACKED (W4B-C: the C++ runtime then has
        // the file and prints the `--> <file>:<line>` source line; the File=None
        // `lower` printed none), the fold on line 5 in both spellings. The
        // COMPLETE located text -- every stderr line, the file path normalized
        // -- is pinned per spelling:
        //   * BL8013 (the seed's `7 / z`, located at its operator): `error[..]`,
        //     `--> <file>:5`, and in the function body `at f` -- the top-level
        //     text plus the frame;
        //   * BL8003 (the empty-fold guard has no expression span, it reports
        //     the ENCLOSING DECLARATION's line, gap G87): `--> <file>:5` at top
        //     level (the binding's line) and `--> <file>:4` in the function body
        //     (the function's line), then `at f` -- pinned as it is.
        // And the function body's statement form (the omp request) fails with
        // byte-identical text to the same body without the request (the
        // expression form it used to take).
        (let normalize (e: string) =
             let e = e.Replace("\r\n", "\n").Trim()
             let e = if e.StartsWith "exit " then e.Substring(e.IndexOf(':') + 1).Trim() else e
             System.Text.RegularExpressions.Regex.Replace(e, @"--> [^\n]*?\.blade((?::\d+)+)", "--> <file>$1")
         let lines (e: string) = (normalize e).Split('\n') |> Array.map (fun l -> l.Trim()) |> List.ofArray
         let fails (tag: string) (src: string) : Result<string, string> =
             match compileProgramFileBacked outputDir ("g55_ff_" + tag) src with
             | Error e -> Error ($"{tag} build: {e}")
             | Ok exe ->
                 match runProgram exe forcedThreads with
                 | Ok out -> Error ($"{tag} did not fail: {out}")
                 | Error e -> Ok e
         let kern (omp: bool) = if omp then "lambda(p, q) where omp -> p + q" else "lambda(p, q) -> p + q"
         let emptyPre = "let x = Float64(0..5) |> compute\nlet n = extents(x) - 5\nlet e = Float64(0..n) |> compute\n"
         let emptyTop = emptyPre + "// the fold below is on line 5, as in the function body\n" + $"let s = reduce(e, {kern true})\n"
         let emptyFn (omp: bool) = emptyPre + $"function f(v: T^1) -> T^0 = {{\n    let s = reduce(v, {kern omp})\n    s\n}}\nlet r = f(e)\n"
         let seedPre = "type I = Idx<5>\nlet x = Float64(0..5) |> compute\nlet z = extents(x) - 5\n"
         let seedTop = seedPre + "// the fold below is on line 5, as in the function body\n" + $"let s = reduce(x, {kern true}, Float64(7 / z))\n"
         let seedFn (omp: bool) = seedPre + $"function f(v: Array<Float64 like I>, z: Int64) -> Float64 = {{\n    let s = reduce(v, {kern omp}, Float64(7 / z))\n    s\n}}\nlet r = f(x, z)\n"
         let msg8003 = "error[BL8003]: reduce: empty array, no reduction possible"
         let msg8013 = "error[BL8013]: integer division by zero"
         for (label, topSrc, fnSrc, wantTop, wantFn) in
               [ ("empty_unseeded", emptyTop, emptyFn, [ msg8003; "--> <file>:5" ], [ msg8003; "--> <file>:4"; "at f" ])
                 ("failing_seed", seedTop, seedFn, [ msg8013; "--> <file>:5" ], [ msg8013; "--> <file>:5"; "at f" ]) ] do
             let name = $"g55_first_failure_{label}_function_body_vs_top_level"
             match fails (label + "_top") topSrc, fails (label + "_fn") (fnSrc true), fails (label + "_fnser") (fnSrc false) with
             | Error e, _, _ | _, Error e, _ | _, _, Error e -> fail name e
             | Ok t, Ok f, Ok fs ->
                 let lt, lf = lines t, lines f
                 if lt <> wantTop then fail name ($"top level: {lt}, want {wantTop}")
                 elif lf <> wantFn then fail name ($"function body: {lf}, want {wantFn}")
                 elif normalize f <> normalize fs then fail name ($"function body: statement form `{normalize f}` vs expression form `{normalize fs}`")
                 else
                     pass name ($"""top level [{String.concat " | " lt}]; function body [{String.concat " | " lf}], byte-identical to the expression form"""))

        printFooter "OpenMP Reduce" [$"{passed} passed"; $"{failed} failed"]
        { Block = "OpenMP Reduce"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
