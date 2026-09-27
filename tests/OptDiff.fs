// The OPTIMIZER DIFFERENTIAL: every corpus program with every gate of the
// semantic-equivalence layer OFF (src/Optimize.fs `optimizerGates`) against
// the same program with every gate ON, printed output compared.
//
// Why a lane of its own. The optimizer runs in Lowering, BEFORE the split
// into the C++ emitter and the interpreter (charter rule 2, "twin-safe"), so
// both evaluators consume one optimized tree: a pass that changes a value
// makes both lanes print the same wrong answer, and `test interp` /
// `diff-oracle` stay green. The only reference that can see such a bug is
// the program with the pass switched off -- which is what the gates exist
// for (charter rule 3). Two CSE miscompiles (a merge across a call that
// mutates the array, and a `%A` identity key that merged `b * 0.1` with
// `b * 0.10000000001`) shipped exactly this way.
//
// Why this design is cheap. An optimization that does not fire leaves the
// emitted C++ byte-identical, and the same C++ compiled by the same g++ with
// the same flags prints the same thing -- so for the programs the optimizer
// does not touch there is nothing to run. The lane EMITS each program twice
// in-process (no toolchain), compares the text, and compiles + runs only the
// pairs whose C++ differs. Its cost is proportional to how much the
// optimizer actually rewrites, not to the corpus. Both compiles go through
// the exe cache, so a rerun over unchanged programs is nearly free.
// (The alternative -- feeding the interpreter pre-optimization IR -- would
// retire the interpreter's role as the codegen twin and cannot see
// codegen-only plans such as pool reuse at all.)
//
// Legitimately different outputs: none by construction. Both sides are
// built with FP contraction OFF (a fused chain may otherwise contract an
// fma a materialized one cannot -- a last-ULP difference, not a bug), the
// timing lines are dropped by the interpreter differential's normalizer, and
// the exit code must match exactly. A raw heap pointer in either output is a
// FAIL of its own (Expect.rawPointerLine): nothing is masked.
//
// Verdicts per program:
//   PASS  identical C++ ("optimizer inert"), or different C++ and identical
//         normalized output + exit code;
//   FAIL  outputs differ, exit codes differ, exactly one side compiled, or
//         exactly one side was refused by the front end / codegen. A FAIL
//         names the gates whose single switch-off changes the emission.
//   SKIP  both sides refused (a reject probe), or the toolchain is missing
//         for the program's back end.
//
// Standalone only (not in `blade test`, like interp / diff-oracle):
//   blade test opt-diff               every corpus category (single + multi-file)
//   blade test opt-diff <category>    one tests/corpus/<category> (literal name)
module Blade.Tests.OptDiff

open System
open System.IO
open Blade
open Blade.Build
open Blade.Lowering
open Blade.Tests.TestHarness
open Blade.Tests.Corpus

/// Set each variable (None = unset), run `f`, restore what was there. The
/// optimizer gates are read per call by design, which is what makes this
/// work.
let private withEnv (pins: (string * string option) list) (f: unit -> 'a) : 'a =
    let prior = pins |> List.map (fun (k, _) -> (k, Environment.GetEnvironmentVariable k))
    for (k, v) in pins do Environment.SetEnvironmentVariable(k, Option.toObj v)
    try f ()
    finally for (k, v) in prior do Environment.SetEnvironmentVariable(k, v)

let private gatesOff (gates: string list) = gates |> List.map (fun g -> (g, Some "0"))
let private gatesDefault = Optimize.optimizerGates |> List.map (fun g -> (g, None))

type private Emission =
    | Emitted of string
    | Refused of string

type private Program =
    | Single of string
    | Multi of (string * string) list

/// Lower + validate + emit under the CURRENT environment (warnings captured,
/// not printed), on the large stack the corpus pipeline uses.
let private emit (name: string) (program: Program) : Emission =
    Blade.Runtime.runOnLargeStack (fun () ->
        try
            let lowered =
                match program with
                | Single src -> fst (lowerCaptured src)
                | Multi files -> fst (lowerMultiSourceCaptured files)
            match lowered with
            | Error e -> Refused ("lower: " + e)
            | Ok ir ->
                match IRValidate.validateIR ir with
                | Error es -> Refused ("IR validation: " + String.concat "; " es)
                | Ok ir ->
                    let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir name
                    // drained per generation, as the corpus pipeline does
                    CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                    Emitted cpp
        with ex -> Refused ("codegen raised: " + ex.Message))

let private firstLine (s: string) =
    s.Replace("\r\n", "\n").Split('\n') |> Array.tryHead |> Option.defaultValue ""

/// Compile `cpp` as `<dir>/<stem>.<ext>` and run it: Ok (exit, normalized
/// output) | Error message ("Skipped: ..." for a missing toolchain).
let private compileAndRun (dir: string) (stem: string) (cpp: string) : Result<int * string, string> =
    let req = inferBackendReq cpp
    let ext = match req with RequiresCuda -> ".cu" | RequiresMpi | CpuOnly -> ".cpp"
    let file = Path.Combine(dir, stem + ext)
    File.WriteAllText(file, cpp)
    match compileForBackendSource (Some cpp) capabilities.Value req file dir with
    | Error e -> Error e
    | Ok exe ->
        if req = RequiresCuda && not capabilities.Value.HasGpu then Error "Skipped: no GPU"
        else
            match runExecutable exe with
            | Ok (code, out) -> Ok (code, Blade.Tests.InterpDiff.normalize out)
            | Error e -> Error e

type private Pending = { Name: string; Stem: string; Program: Program; On: string; Off: string }

let runOptDiffTests (categories: string list) (includeMultiFile: bool) : BlockResult =
    printHeader "Optimizer Differential (all gates OFF vs ON)"
    let blockName = "Opt Diff"
    let outDir = Path.Combine("generated_cpp_tests", "opt_diff")
    Directory.CreateDirectory outDir |> ignore
    CodeGen.deployRuntimeHeaders outDir
    printfn "gates: %s" (String.concat ", " Optimize.optimizerGates)
    let passed = ref 0
    let failed = ref 0
    let skipped = ref 0
    let inert = ref 0
    let failedNames : string list ref = ref []
    let pass name detail = passed.Value <- passed.Value + 1; resultLine Pass name detail
    let fail name detail = failed.Value <- failed.Value + 1; failedNames.Value <- failedNames.Value @ [ name ]; resultLine Fail name detail
    let skip name detail = skipped.Value <- skipped.Value + 1; resultLine Skip name detail
    let sw = System.Diagnostics.Stopwatch.StartNew()
    // The whole block runs with contraction off (see the header) and with
    // the gates at their defaults on the ON side, whatever the shell carries.
    withEnv [ ("BLADE_FP_CONTRACT", Some "off") ] (fun () ->
        let programs =
            [ for cat in categories do
                for (name, src) in category cat do
                    yield (cat, name, Single src)
              if includeMultiFile then
                for (name, files) in multiFileCategory "multifile" do
                    yield ("multifile", name, Multi files) ]
        // PHASE 1, sequential (the gates are process environment): emit
        // every program both ways; keep only the pairs whose C++ differs.
        let pending = ResizeArray<Pending>()
        let mutable lastCat = ""
        let mutable ordinal = 0
        for (cat, name, program) in programs do
            if cat <> lastCat then
                if lastCat <> "" then printfn "  (%s emitted)" lastCat
                lastCat <- cat
            ordinal <- ordinal + 1
            let on = withEnv gatesDefault (fun () -> emit name program)
            let off = withEnv (gatesOff Optimize.optimizerGates) (fun () -> emit name program)
            match on, off with
            | Refused _, Refused _ ->
                // a reject probe (or a program both lanes refuse alike):
                // nothing to compare, and the main suite owns the verdict
                skipped.Value <- skipped.Value + 1
            | Emitted a, Emitted b when a = b ->
                inert.Value <- inert.Value + 1
                passed.Value <- passed.Value + 1
            | Emitted a, Emitted b ->
                pending.Add { Name = name; Stem = $"{ordinal:D4}_{sanitizeFileName name}"; Program = program; On = a; Off = b }
            | Refused e, Emitted _ ->
                fail name $"refused with the optimizer ON only: {firstLine e}"
            | Emitted _, Refused e ->
                fail name $"refused with the optimizer OFF only: {firstLine e}"
        if lastCat <> "" then printfn "  (%s emitted)" lastCat
        printfn "emitted %d programs in %.1fs: %d with identical C++ (optimizer inert), %d to compile and run both ways"
            programs.Length sw.Elapsed.TotalSeconds inert.Value pending.Count
        // Which gates move a program's emission, by switching each off
        // alone (emission only -- cheap). Sequential, like phase 1.
        let culpritsOf (p: Pending) =
            Optimize.optimizerGates
            |> List.filter (fun g ->
                match withEnv (gatesDefault @ gatesOff [ g ]) (fun () -> emit p.Name p.Program) with
                | Emitted c -> c <> p.On
                | Refused _ -> true)
        let attributions = pending |> Seq.map (fun p -> (p.Stem, culpritsOf p)) |> Map.ofSeq
        if not capabilities.Value.HasGpp then
            for p in pending do
                skip p.Name "C++ differs; requires g++ to compare outputs"
        else
            // PHASE 2, parallel: nothing here reads an optimizer gate.
            let results =
                pending.ToArray()
                |> Array.Parallel.map (fun p -> (p, compileAndRun outDir (p.Stem + "_on") p.On, compileAndRun outDir (p.Stem + "_off") p.Off))
            for (p, on, off) in results do
                let who = attributions.[p.Stem] |> String.concat "+"
                let pointerIn (r: Result<int * string, string>) =
                    match r with
                    | Ok (_, out) -> Blade.Tests.Expect.rawPointerLine out
                    | Error _ -> None
                match on, off with
                | _ when (pointerIn on).IsSome || (pointerIn off).IsSome ->
                    let line = defaultArg (pointerIn on) (defaultArg (pointerIn off) "")
                    fail p.Name $"output prints a raw pointer ({who}): {line}"
                | Ok (ca, oa), Ok (cb, ob) when ca = cb && oa = ob ->
                    pass p.Name $"C++ differs ({who}), output identical"
                | Ok (ca, oa), Ok (cb, ob) ->
                    fail p.Name $"OUTPUT DIVERGES ({who}): exit {ca} ON vs {cb} OFF"
                    let la = oa.Split('\n')
                    let lb = ob.Split('\n')
                    let firstDiff =
                        Seq.zip la lb |> Seq.tryFindIndex (fun (x, y) -> x <> y)
                        |> Option.defaultValue (min la.Length lb.Length)
                    let at (ls: string[]) = if firstDiff < ls.Length then ls.[firstDiff] else "<end of output>"
                    printfn "    ON : %s" (at la)
                    printfn "    OFF: %s" (at lb)
                | Error e, _ when isSkipError e -> skip p.Name e
                | _, Error e when isSkipError e -> skip p.Name e
                | Error ea, Error _ when p.On.Contains "#error" && p.Off.Contains "#error" ->
                    // both refused by a codegen `#error` guard (a codegen-
                    // stage probe): the lanes agree on the refusal
                    pass p.Name $"C++ differs ({who}); both refused by a codegen #error ({firstLine ea})"
                | Error ea, Error _ ->
                    // both failed to compile for some other reason (a
                    // missing header, a toolchain gap): nothing was observed
                    skip p.Name $"C++ differs ({who}); neither side compiled ({firstLine ea})"
                | Error e, Ok _ -> fail p.Name $"only the OFF side compiled ({who}): {firstLine e}"
                | Ok _, Error e -> fail p.Name $"only the ON side compiled ({who}): {firstLine e}")
    printFooter blockName
        [ $"{passed.Value} passed ({inert.Value} optimizer-inert)"; $"{failed.Value} failed"; $"{skipped.Value} skipped"
          $"{sw.Elapsed.TotalSeconds:F0}s" ]
    { Block = blockName; Passed = passed.Value; Failed = failed.Value; Skipped = skipped.Value; FailedNames = failedNames.Value }
