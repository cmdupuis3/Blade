// Interpreter differential gate — Milestone M0.
//
// The twin of DiffOracle.fs: where that harness diffs two COMPILER binaries,
// this one diffs the tree-walking INTERPRETER (Blade.Interp.Run.runProgram)
// against the compiled C++ binary, value by value, over the corpus. The
// compiled side is the ground truth the interpreter must reproduce byte-for-
// byte (after timing/pointer normalization), which is the whole reason the
// interpreter exists — a second, independent evaluator to pin the language's
// runtime semantics.
//
// One locked, large-stack F# pipeline pass per test (reusing Runner.runFullTest)
// yields BOTH the validated IRProgram (the interpreter's input) and the
// compiled-side compile+run result, so the expensive front-end runs once.
module Blade.Tests.InterpDiff

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Blade
open Blade.IR
open Blade.Build
open Blade.CodeGen
open Blade.Interp
open Blade.Tests.TestHarness
open Blade.Tests.Corpus
open Blade.Tests.Expect
open Blade.Tests.Runner

/// M0 differential slice: the categories the interpreter must match the
/// compiled binary on at Milestone 0. Grows as later milestones claim more of
/// the value/printing surface (this list is the documented M0 default set).
let m0Slice = [ "basic"; "guards"; "static"; "intrinsics" ]

/// M1 differential slice: M0 plus the functions/structs/state surface. These
/// are CORPUS DIRECTORY names (the gate loads them via Corpus.category), not
/// the Cli.fs display aliases — e.g. "sum-types", not "sumtypes". Two of the
/// state categories are two-directory pairs whose "-errors" half is a
/// reject-only negative corpus (see rejectOnlyCategories):
///   * mutability  + mutability-errors
///   * units       + unit-errors
/// The "modules" category here is SINGLE-file (Corpus.category "modules", 2
/// tests); genuine multi-source tests live in the separate "multifile"
/// category (Corpus.multiFileCategory), which is NOT part of this slice, so
/// the gate never needs lowerMultiSource.
let m1Slice =
    m0Slice
    @ [ "functions"
        "structs"; "struct-mutual"; "struct-aborts"
        "sum-types"; "interfaces"
        "mutability"; "mutability-errors"
        "modules"
        "units"; "unit-errors" ]

/// M2 differential slice: the dense-array + loop-object surface (arrays,
/// virtual ranges, loop objects, combinator algebra, reductions, replicate,
/// arity-poly, function arrays). These are CORPUS DIRECTORY names (Corpus.category
/// loads them), reconciled against tests/corpus/ — ALL TWELVE of the m2-design.md
/// §8 build-plan categories EXIST verbatim as directories, so no name corrections
/// were needed:
///   loops for-in bracketed anon-ranges replicate tuple-views
///   zero-combinators sequence-combinators guard-combinators
///   func-arrays arity inference-probes
/// Reject-probes ("(rejects)") and the single func-arrays abort-probe
/// ("(aborts)") carry their name markers, so the existing isRejectProbe /
/// isAbortProbe classification handles them — NONE of these is an unmarked
/// reject-only corpus, so no rejectOnlyCategories entry is required.
/// (Until the interpreter's array/loop evaluation lands, most members classify
/// SKIP-UNSUPPORTED; this slice makes the future passes visible per checkpoint.)
let m2Slice =
    [ "loops"; "for-in"; "bracketed"; "anon-ranges"; "replicate"; "tuple-views"
      "zero-combinators"; "sequence-combinators"; "guard-combinators"
      "func-arrays"; "arity"; "inference-probes" ]

/// M3 differential slice: symmetric/antisymmetric/Hermitian COMPACT storage
/// (triangular output allocation + left-justified writes + genPrintArraySymAware
/// print) and Reynolds KERNELS (permutation-sum evaluation). Corpus directory
/// names. Members whose output rides the FUSED-JOINT compound-axis path (joint
/// SymIdx over a repeated multi-dim array — symmetry/012,013,015,016 and
/// reynolds/022,023) classify SKIP-UNSUPPORTED, as do the OpenMP/parallel/fusion
/// members outside the default (serial) run; the compact + Reynolds value tests
/// pass byte-for-byte.
let m3Slice = [ "symmetry"; "reynolds" ]

/// M5-prep slice: the native random-fill surface (rand.uniform / rand.normal
/// bindings, materialized in Interp/Run.fs from IRModule.RandomInits). Dense
/// rank-1 float arrays; 002-004 also exercise reduce / method_for (M2 layer).
/// fill_random(mod) (FillModulus = nondeterministic C rand()) classifies
/// SKIP-UNSUPPORTED by design.
let randSlice = [ "rand" ]

/// M4a differential slice: the SQL-ish relational surface. CORPUS directory
/// names (Corpus.category loads them), each VERIFIED against tests/corpus/ and
/// Corpus.fs and — critically — RUN through the gate in isolation, each showing
/// ZERO FAIL (passes + SKIP-UNSUPPORTED + reject-probes only). Adding only
/// zero-fail categories preserves the gate's zero-fail invariant while making
/// the not-yet-interpreted SQL surface VISIBLE per checkpoint (most members
/// classify SKIP-UNSUPPORTED until the interpreter's mask/group/join/set-op
/// evaluation lands; the reduce / foreign-key / extents value tests already pass
/// byte-for-byte).
///
/// HELD BACK — NOT added because it FAILS the gate against this snapshot (its
/// four failures are the next wave's fix list, all in Core/Loops/ArrayOps,
/// which this wave does not own):
///   index-types (63 pass / 4 FAIL / 60 skip):
///     * Ragged Tuple Form Read (018)  — interp exits 1 (BL8003 array index out
///       of bounds) vs compiled 0
///     * Ragged Literal Indexing (077) — interp exits 1 (BL8003 array index out
///       of bounds) vs compiled 0
///     * Ragged Subview Metadata (023) — interp bug: IndexOutOfRange in the
///       ragged sub-view reader
///     * Complex Transcendental (125)  — interp stdout diverges from the
///       compiled binary (complex-transcendental print formatting)
/// Add index-types once those four are fixed.
let m4aSlice =
    [ "sql-reduce"; "sql-foreign-keys"; "sql-sort"; "sql-set-ops"
      "sql-unique-contains"; "sql-extents"; "sql-regressions"
      "sql-combined"; "sql-extents-multi-rank"; "sql-group-by"
      "sql-masks"; "sql-semijoins"; "sql-v24d-probes" ]

/// Fallback slice: the `<|:>` allocated-fallback corpus (the NEW `fallback`
/// category). Verified 0 FAIL; every member currently classifies
/// SKIP-UNSUPPORTED (the interpreter has no fallback materializer yet — lands a
/// later wave). Added for coverage visibility per the verification-first rule.
let fallbackSlice = [ "fallback" ]

/// index-types: added once its four blockers fell (ragged-trio SRagged
/// construction/peel fix + the Kahan complex-sqrt port) — verified 0 FAIL.
let indexTypesSlice = [ "index-types" ]

/// M5 differential slice: the DOMAIN-LAYER categories. CORPUS directory names
/// (Corpus.category), each VERIFIED through the gate in isolation against this
/// snapshot showing ZERO FAIL:
///   ad       10/0/0  (8 grad() value + 2 reject) — the grad canary
///   spectra  22/0/0  (14 bit-exact complex FFT/ifft/power/polyspec + 8 reject)
///   math     40/0/0  (34 svd/eigh/eig/hosvd/unfold + 6 reject; the 2-D matrix
///                     prints render byte-exact through the existing ArrayOps path)
///   ml-ops   11/0/0  (5 IrrepsIdx/tp_spec/hom_dim ops + 6 reject)
///   ml-equiv 21/0/0  (13 derive_linear/derive_tp/certificate/derive-train + 8 reject)
///   ml-e2e    2/0/0  (2 full E(3) message-passing grad-training loops)
///   ppl      68/0/1  (53 pool/dist/jet/map value + 15 reject; ONE
///                     SKIP-UNSUPPORTED = ppl/007 "Moments Multiaxis", whose
///                     multiaxis moment takes the per-cell fused-joint output path
///                     the gate holds at M3+. Not a fail; a feature-gap skip.)
/// Why these are core surface (M5 audit): Dist ERASES to plain cumulant ARRAYS
/// before IR (no IRTDist ever reaches the interpreter), so the ppl pool path is
/// straight-line arithmetic over rank-1 sample-axis reduces the M0-M2 core already
/// evaluates. spectra rides the bit-exact complex arm only (baked-float twiddles,
/// naive complex mul = __muldc3 for finite inputs; NO complex div/exp/log/`^`).
/// math is pure imperative REAL arithmetic (Jacobi/Francis on flat mut arrays;
/// eig eigenvalues are real (re,im) pairs — no native Complex128). ad/ml-e2e/
/// ml-equiv ride grad()'s core imperative reverse pass. The ppl halo formers
/// (068,069) materialize through the general range machinery — no halo-specific
/// interp code is needed and they PASS. ml-e2e's two 30-step training loops are
/// the only heavy members (~16s / ~26s measured interp wall time), well inside the
/// 120s runInterpTimed ceiling — no timeout.
let m5Slice =
    [ "ad"; "spectra"; "math"; "ml-ops"; "ml-equiv"; "ml-e2e"; "ppl" ]

/// The slice the default `test interp` arm runs. Later milestones extend it
/// (index types once its ragged/complex fixes land, ...). Kept as its own name
/// so the Cli arm (`... runInterpDiffTests currentSlice ...`) never needs
/// editing again as milestones land — only this binding grows.
let currentSlice = m1Slice @ m2Slice @ m3Slice @ randSlice @ m4aSlice @ fallbackSlice @ m5Slice @ indexTypesSlice

/// Output-line normalizer, shared in spirit with DiffOracle.normalize
/// (DiffOracle.fs:79-85), widened for the split-timing wrapper:
///   * drop the "<name> completed in <t>s" compute-timing line AND the
///     "<name> input allocation took <t>s" setup-timing line
///     (CodeGen.genMainWrapper / genMainWrapperSplit) — both vary per run;
///   * mask heap pointers 0x... -> 0xPTR (the struct printer still renders an
///     array-typed field as its raw address, different every run);
///   * CRLF -> LF; trim trailing whitespace per line and the whole text.
let private normalize (s: string) : string =
    s.Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun l ->
        not (l.Contains "completed in") && not (l.Contains "input allocation took"))
    |> Array.map (fun l -> Regex.Replace(l.TrimEnd(), "0x[0-9a-fA-F]+", "0xPTR"))
    |> String.concat "\n"
    |> fun t -> t.Trim()

/// First line of a (possibly multi-line) string, for compact failure details.
let private firstLine (s: string) =
    s.Replace("\r\n", "\n").Split('\n') |> Array.tryHead |> Option.defaultValue ""

/// Recover the feature name from an "interp-unsupported: <feature>" stderr.
let private unsupportedFeature (r: Run.InterpResult) =
    let s = r.Stderr.Trim()
    let prefix = "interp-unsupported: "
    if s.StartsWith prefix then s.Substring(prefix.Length).Trim() else s

/// Run the interpreter under a wall-clock ceiling (~120s). runProgram already
/// recurses on the large stack and its InterpLimits bound the step/cell budget;
/// this Task guard is the outer backstop so a pathological non-terminating walk
/// cannot hang the whole gate.
let private runInterpTimed (program: IRProgram) (name: string) : Result<Run.InterpResult, string> =
    let task = Task.Run(fun () -> Run.runProgram program name Blade.Interp.Value.defaultLimits)
    if task.Wait(120000) then Ok task.Result
    else Error "interp timed out (>120s)"

/// Corpus categories that are ENTIRELY negative tests: every source is meant
/// to be refused by the compiled front-end (a type/lower/unit error), yet
/// their test NAMES carry no "(rejects)" marker for the harness to key on
/// (they open "// TEST: <plain name>"). These two "-errors" corpora are not
/// referenced by the main-suite `allTests` at all — the interp gate is their
/// first consumer — so there is no prior classification to mirror; we define
/// the faithful one here: treat every member as a compile-reject probe.
let private rejectOnlyCategories = Set.ofList [ "mutability-errors"; "unit-errors" ]

/// Differential gate over the given corpus categories. Verdicts:
///   * reject-probe ("(rejects)", OR any test in a rejectOnlyCategory): correct
///     iff the compiled pipeline refuses it; the interpreter has nothing to run
///     and trivially agrees.
///   * abort-probe ("(aborts)"): both sides must exit nonzero AND the
///     interpreter's stderr must contain every pinned // ABORT: substring. Each
///     // ABORT: pin is an INDEPENDENT substring matched over the whole (multi-
///     line) stderr, so the coded panic line, the "--> file:line" line, and the
///     shadow-stack frames ("at inner"/"at outer") each match wherever they
///     land — line boundaries are irrelevant.
///   * normal test: byte-equal NORMALIZED stdout + matching exit-code class,
///     then a second gate — Expect.checkExpectedValues over the interp output.
///   * interp reports ExitUnsupported (125): [SKIP-UNSUPPORTED], counted apart.
///   * compiled FRONT-END rejected a non-reject value test (IRResult = Error):
///     no compiled reference exists to diff against, so this is NOT an interp
///     parity failure — [SKIP-COMPILED-REJECTED], surfaced distinctly (neither a
///     failure nor a silent pass). This is the faithful landing for a
///     "known-failing"/forward-looking spec that the compiler cannot yet build.
///     (In the current tree functions/017,018 — the documented Poly+HM
///     known-failing pair — actually COMPILE AND RUN, so they take the normal
///     path; this branch is the defensive net if such a spec regresses.)
let runInterpDiffTests (categories: string list) : BlockResult =
    printHeader "Interpreter Differential (M0)"
    let blockName = "Interp Diff"
    if not capabilities.Value.HasGpp then
        printfn "Skipped: requires g++ (the compiled binary is the differential reference)."
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
        let tmpRoot = Path.Combine(Path.GetTempPath(), "blade_interp_diff")
        (try Directory.Delete(tmpRoot, true) with _ -> ())
        Directory.CreateDirectory(tmpRoot) |> ignore
        // runFullTest generates the .cpp here but does NOT ship the runtime
        // headers; deploy them once so g++ resolves #include "blade_runtime.hpp".
        CodeGen.deployRuntimeHeaders tmpRoot

        let mutable passed = 0
        let mutable failed = 0
        let mutable skipped = 0
        let mutable unsupported = 0
        let mutable compiledRejected = 0
        let mutable failedNames : string list = []

        // Per-test interpreter wall-time. Only tests that ACTUALLY run the
        // interpreter are timed (reject-probes short-circuit before it). M2's
        // array/loop interpretation can be slow, and M5 adds heavy categories;
        // this surfaces any test whose interp walk exceeds 5s so the cost is
        // visible per checkpoint rather than discovered as a gate-wide slowdown.
        let interpTimes = System.Collections.Generic.List<string * float>()
        let runInterpTimedRec (program: IRProgram) (name: string) : Result<Run.InterpResult, string> =
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let r = runInterpTimed program name
            sw.Stop()
            interpTimes.Add(name, sw.Elapsed.TotalSeconds)
            r

        let pass name detail =
            passed <- passed + 1
            resultLine Pass name detail
        let fail name detail =
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name detail
        let skip name detail =
            skipped <- skipped + 1
            resultLine Skip name detail
        let skipUnsupported name feature =
            unsupported <- unsupported + 1
            resultLine Skip name (sprintf "SKIP-UNSUPPORTED: %s" feature)
        // Compiled front-end refused this (supposed-to-pass) test, so there is
        // no compiled reference to diff the interpreter against. Not an interp
        // parity failure; surfaced distinctly so it can never pass silently.
        let skipCompiledRejected name detail =
            compiledRejected <- compiledRejected + 1
            resultLine Skip name (sprintf "SKIP-COMPILED-REJECTED: %s" (firstLine detail))

        for cat in categories do
            printSubHeader (sprintf "category: %s" cat)
            // Every test in a reject-only category is a compile-reject probe,
            // even without the "(rejects)" name marker (see rejectOnlyCategories).
            let catRejectOnly = rejectOnlyCategories.Contains cat
            for (name, source) in category cat do
                // ONE locked, large-stack pipeline pass -> IRProgram + compiled run.
                let result = runFullTest name source tmpRoot true

                if isRejectProbe result || catRejectOnly then
                    // Compile-reject probe: correct iff the pipeline refuses it.
                    if not (isFullPass result) then pass name "compile-reject: interp trivially agrees"
                    else fail name "expected rejection but pipeline accepted"

                elif isAbortProbe result then
                    match result.IRResult with
                    | Error e -> fail name (sprintf "abort probe failed front-end: %s" e)
                    | Ok program ->
                        match runInterpTimedRec program name with
                        | Error e -> fail name e
                        | Ok interp ->
                            if interp.ExitCode = Run.ExitUnsupported then
                                skipUnsupported name (unsupportedFeature interp)
                            else
                                let compiledAborted = isExpectedAbort result
                                let interpAborted =
                                    interp.ExitCode <> 0
                                    && result.AbortExpectation |> List.forall (fun sub -> interp.Stderr.Contains sub)
                                if compiledAborted && interpAborted then
                                    pass name "both abort with pinned message"
                                elif not compiledAborted then
                                    fail name "compiled side did not abort as expected"
                                else
                                    fail name (sprintf "interp did not abort with pinned message (exit %d): %s"
                                                   interp.ExitCode (firstLine interp.Stderr))

                else
                    // Normal value test.
                    match result.RunResult with
                    | Error e when isSkipError e -> skip name e            // toolchain/GPU unavailable
                    | Error e ->
                        // No compiled reference. If the FRONT-END refused the
                        // program (IRResult = Error), the interpreter never even
                        // got its input — a known-failing/forward-looking spec or
                        // an unmarked negative test, NOT an interp parity failure;
                        // record a distinct skip. If the front-end accepted but a
                        // later stage failed (codegen/compile emitted bad C++),
                        // that IS a genuine break worth flagging as a failure.
                        match result.IRResult with
                        | Error _ ->
                            // Per-TEST unmarked reject. The compiled FRONT-END
                            // refused this test, yet its name carries no
                            // "(rejects)" marker and it is not in a
                            // rejectOnlyCategory (the M4 audit flagged ~13
                            // such index-types/sql negatives; most have since
                            // been re-marked "(rejects)" and are caught above).
                            // Faithful landing keyed on whether the test asserts
                            // any values:
                            //   * NO EXPECT values -> an unmarked negative /
                            //     forward-looking smoke spec the compiler
                            //     CORRECTLY rejects. The interpreter never
                            //     received input, so it trivially AGREES with the
                            //     rejection: PASS-REJECT — mirroring the
                            //     "(rejects)"-probe branch at the top of this loop
                            //     and the rejectOnlyCategories precedent for whole
                            //     negative corpora (a name-less reject is the
                            //     per-test analogue). The main Runner has no
                            //     compiled reference to diff either (isFullPass is
                            //     false), so no parity signal is lost.
                            //   * HAS EXPECT values -> a value spec the compiler
                            //     cannot yet BUILD, so there is no compiled output
                            //     to diff the interpreter's values against. Keep
                            //     the distinct SKIP-COMPILED-REJECTED so a value
                            //     test can NEVER pass silently on a rejection.
                            if not result.HasExpectedValues then
                                pass name "compile-reject (unmarked, no EXPECT): interp trivially agrees"
                            else
                                skipCompiledRejected name e
                        | Ok _ -> fail name (sprintf "compiled side failed: %s" e)
                    | Ok (compiledExit, compiledOut) ->
                        match result.IRResult with
                        | Error e -> fail name (sprintf "front-end rejected (unexpected): %s" e)
                        | Ok program ->
                            match runInterpTimedRec program name with
                            | Error e -> fail name e
                            | Ok interp ->
                                if interp.ExitCode = Run.ExitUnsupported then
                                    skipUnsupported name (unsupportedFeature interp)
                                elif interp.ExitCode = Run.ExitInterpBug then
                                    fail name (sprintf "interp bug: %s" (firstLine interp.Stderr))
                                elif compiledExit <> 0 then
                                    fail name (sprintf "compiled binary exited %d (non-abort test)" compiledExit)
                                elif interp.ExitCode <> 0 then
                                    fail name (sprintf "interp exited %d, compiled exited 0: %s"
                                                   interp.ExitCode (firstLine interp.Stderr))
                                else
                                    let mine = normalize interp.Stdout
                                    let theirs = normalize compiledOut
                                    if mine <> theirs then
                                        fail name "stdout diverges from compiled binary"
                                        printfn "    interp:   %s"
                                            (mine.Split('\n') |> Array.truncate 3 |> String.concat " | ")
                                        printfn "    compiled: %s"
                                            (theirs.Split('\n') |> Array.truncate 3 |> String.concat " | ")
                                    else
                                        // Second gate: EXPECT values vs interp output.
                                        match checkExpectedValues (parseExpectedValues source) interp.Stdout with
                                        | Ok () -> pass name "values identical"
                                        | Error msgs -> fail name (sprintf "interp value-check: %s" (String.concat "; " msgs))

        // ------------------------------------------------------------------
        // Provider-read wiring verification (M6). There are NO netcdf/zarr CORPUS
        // categories (Corpus.category has none — the provider tests live in the
        // separate runNetcdfTests / runZarrTests blocks with in-process .blade
        // sources), so the category loop above can never reach a provider read.
        // This hermetic block drives Run.fs's materializeProviderRead end to end
        // and byte-diffs it against the compiled binary: it writes a Zarr store
        // (pure std C++17 — no external lib) holding a dense f64 (rank-2) + f32 +
        // i64 variable and reads+prints all three, exercising the three store
        // arms (SFloat from f64, SFloat from widened f32, SInt from i64) and the
        // Print narrow-back-to-declared-width coercion.
        //
        // An ABSOLUTE store path is baked into the source so both the compiled
        // binary (cwd = exe dir) and the interpreter (cwd = compiler process)
        // resolve the SAME file — proving the read itself free of the cwd
        // asymmetry. The RELATIVE two-copy scheme a corpus test would need staged
        // (compiler-cwd copy for the interpreter + exe-dir copy for the binary)
        // is documented in Run.fs's materializeProviderRead header; wiring a
        // provider corpus category into this gate is a Corpus.fs + fixture-staging
        // follow-up, out of scope for this verification-first wave.
        (let provName = "provider-reads: zarr dense f64/f32/i64 (M6 wiring)"
         printSubHeader "category: provider-reads (zarr hermetic, M6)"
         try
            Blade.ProviderStatics.install ()   // idempotent; ensure the registry is populated
            let provDir = Path.Combine(tmpRoot, "prov")
            Directory.CreateDirectory provDir |> ignore
            CodeGen.deployRuntimeHeaders provDir
            let storePath = Path.Combine(provDir, "store").Replace("\\", "/")
            (try Directory.Delete(storePath, true) with _ -> ())
            let aData = [| for i in 1 .. 12 -> float i * 0.5 |]
            let vars : Blade.ZarrProvider.ZarrWrite.WriteVar list =
                [ { Name = "A"; DimNames = Some ["x"; "y"]; Shape = [3L; 4L]; Chunks = [2L; 2L]
                    FillValue = Blade.ZarrProvider.FillFloat 0.0
                    Data = Blade.ZarrProvider.ZarrWrite.WF64 aData; OmitChunks = []; Blade = None }
                  { Name = "F"; DimNames = Some ["x"]; Shape = [3L]; Chunks = [3L]
                    FillValue = Blade.ZarrProvider.FillFloat 0.0
                    Data = Blade.ZarrProvider.ZarrWrite.WF32 [| 1.5f; 2.5f; 3.5f |]; OmitChunks = []; Blade = None }
                  { Name = "N"; DimNames = Some ["x"]; Shape = [3L]; Chunks = [3L]
                    FillValue = Blade.ZarrProvider.FillInt 0L
                    Data = Blade.ZarrProvider.ZarrWrite.WI64 [| 10L; 20L; 30L |]; OmitChunks = []; Blade = None } ]
            Blade.ZarrProvider.ZarrWrite.writeStoreV2 storePath vars
            let src =
                sprintf "import zarr as z\nlet sample = z.load(\"%s\")\nlet A = sample.vars.A |> z.read\nlet F = sample.vars.F |> z.read\nlet N = sample.vars.N |> z.read\n" storePath
            match Blade.Lowering.lower src with
            | Error e -> fail provName (sprintf "lower failed: %s" e)
            | Ok ir ->
                let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir "interp_prov_verify"
                let cppFile = Path.Combine(provDir, "interp_prov_verify.cpp")
                File.WriteAllText(cppFile, cpp)
                match compileCpp cppFile provDir with
                | Error e when isSkipError e -> skip provName (sprintf "compile skipped: %s" e)
                | Error e -> fail provName (sprintf "compile failed: %s" e)
                | Ok exe ->
                    match runExecutable exe with
                    | Error e -> fail provName (sprintf "compiled run failed: %s" e)
                    | Ok (code, compiledOut) when code <> 0 ->
                        fail provName (sprintf "compiled binary exited %d: %s" code (firstLine compiledOut))
                    | Ok (_, compiledOut) ->
                        match runInterpTimed ir "interp_prov_verify" with
                        | Error e -> fail provName e
                        | Ok interp when interp.ExitCode <> 0 ->
                            fail provName (sprintf "interp exited %d (compiled 0): %s" interp.ExitCode (firstLine interp.Stderr))
                        | Ok interp ->
                            let mine = normalize interp.Stdout
                            let theirs = normalize compiledOut
                            if mine = theirs && mine.Contains "A = [" && mine.Contains "F = [" && mine.Contains "N = [" then
                                pass provName "interp read == compiled read (byte-identical: f64 rank-2 + f32 + i64)"
                            else
                                fail provName "stdout diverges from compiled binary"
                                printfn "    interp:   %s" (mine.Split('\n') |> Array.truncate 4 |> String.concat " | ")
                                printfn "    compiled: %s" (theirs.Split('\n') |> Array.truncate 4 |> String.concat " | ")
         with ex -> fail provName (sprintf "exception: %s" ex.Message))

        if unsupported > 0 then
            printfn ""
            printfn "  SKIP-UNSUPPORTED: %d test(s) not yet evaluated/printed by the interpreter" unsupported
        if compiledRejected > 0 then
            printfn "  SKIP-COMPILED-REJECTED: %d test(s) the compiled front-end refused (no differential)" compiledRejected

        // Interp-cost visibility: report any test whose interpreter walk took
        // over 5s (count + the worst 5), so a slow M2/M5 category is surfaced
        // before it becomes a gate-wide bottleneck. Silent when nothing is slow.
        let slowThreshold = 5.0
        let slow =
            interpTimes
            |> Seq.filter (fun (_, t) -> t > slowThreshold)
            |> Seq.sortByDescending snd
            |> Seq.toList
        if not (List.isEmpty slow) then
            printfn ""
            printfn "  SLOW INTERP (>%.0fs): %d test(s)" slowThreshold (List.length slow)
            slow
            |> List.truncate 5
            |> List.iter (fun (n, t) -> printfn "    %7.2fs  %s" t n)
        printFooter blockName
            [ sprintf "%d passed" passed
              sprintf "%d failed" failed
              sprintf "%d skip-unsupported" unsupported
              sprintf "%d skip-compiled-rejected" compiledRejected
              sprintf "%d skipped" skipped ]
        // SKIP-UNSUPPORTED and SKIP-COMPILED-REJECTED both fold into the
        // roll-up's Skipped bucket (neither is a failure), but each is surfaced
        // distinctly in the footer/summary lines above.
        { Block = blockName
          Passed = passed
          Failed = failed
          Skipped = skipped + unsupported + compiledRejected
          FailedNames = failedNames }
