// Zarr provider tests. Fully hermetic: metadata/parse/codegen tests are
// pure, and the live tests GENERATE their fixture stores on the fly via
// ZarrProvider.ZarrWrite (pure .NET file writes — no external library, no
// committed binary fixture; contrast NetcdfTests' sample.nc + libnetcdf).
// Only the e2e compile+run blocks need g++, and they skip gracefully
// without it (Build.isSkipError), mirroring NetcdfTests' discipline.
module Blade.Tests.ZarrTests

open System
open System.IO
open Blade
open Blade.Ast
open Blade.Parser
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.TypeEnv
open Blade.Lowering
open Blade.CodeGen
open Blade.ZarrProvider
open Blade.Build
open Blade.Tests.TestHarness

/// D09-I1/I2 focused gate, callable without running the whole Zarr category.
/// Keep all evidence in a new private directory and stop on the first failure.
let runRank2StreamedValueBoundaryTests (workDirectory: string) : int =
    printHeader "Rank-two streamed value boundary and certified rows (D09-I1/I2)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let check (label: string) (ok: bool) (detail: string) =
        if ok then
            passed <- passed + 1
            printfn "  PASS: %s" label
        else
            failed <- failed + 1
            printfn "  FAIL: %s -- %s" label detail
            failwith ("D09-I1 stopped: " + label)
    let directory = Path.GetFullPath workDirectory
    if Directory.Exists directory || File.Exists directory then
        invalidArg "workDirectory" "D09-I1 requires a new private directory; preserve earlier evidence"
    if directory.Length > 160 || (directory |> Seq.exists (fun c -> c < ' ' || c > '~')) then
        invalidArg "workDirectory" "Use a short private ASCII path (at most 160 characters)"
    Directory.CreateDirectory directory |> ignore
    let keys =
        ([ "BLADE_FP_REPRO"; "BLADE_FP_REASSOC"; "BLADE_FP_CONTRACT";
           "BLADE_PRINT"; "BLADE_TILE_CACHE"; "BLADE_LLVM"; "BLADE_BLAS"; "BLADE_OMP_THREADS" ]
         @ Blade.Optimize.optimizerGates) |> List.distinct
    let saved = keys |> List.map (fun k -> k, Environment.GetEnvironmentVariable k)
    let savedTiming = Blade.CodeGenState.splitTimingModeEnabled ()
    let savedTarget = Blade.CodeGenState.splitTimingOnlyBinding ()
    let aData = [| for i in 0..4 do for j in 0..2 -> float (10*i+j) |]
    let bData = aData |> Array.map (fun x -> x+100.0)
    let fixtureShape (label: string) (n: int64) (k: int64) (a: float array) (b: float array)
                (fillA: ZarrFill) (omitA: int64 list list)
                (fillB: ZarrFill) (omitB: int64 list list) =
        let store = Path.Combine(directory, "s_" + label)
        let variable (name: string) (data: float array) (fill: ZarrFill) (omit: int64 list list) : ZarrWrite.WriteVar =
            { Name = name
              DimNames = Some ["row"; "col"]
              Shape = [n; 3L]
              Chunks = [k; 2L]
              FillValue = fill
              Data = ZarrWrite.WF64 data
              OmitChunks = omit
              Blade = None }
        ZarrWrite.writeStoreV2 store [variable "A" a fillA omitA; variable "B" b fillB omitB]
        store.Replace('\\','/')
    let fixture label a b fillA omitA fillB omitB =
        fixtureShape label 5L 2L a b fillA omitA fillB omitB
    let source (store: string) (bVerb: string) (tail: string) =
        String.concat "\n"
            [ "import zarr as z";
              sprintf "let s = z.load(\"%s\")" store;
              "let A = s.vars.A |> z.stream";
              "let B = s.vars.B |> z." + bVerb;
              tail; "" ]
    let map (body: string) =
        "let out = (method_for(zip(A, B)) <@> lambda(a: Float64, b: Float64) -> " + body + ") |> compute"
    let lowerCase (label: string) (demand: string) (text: string) =
        Environment.SetEnvironmentVariable("BLADE_PRINT",demand)
        File.WriteAllText(Path.Combine(directory,label + ".demand.txt"),demand)
        File.WriteAllText(Path.Combine(directory,label + ".blade"),text)
        match Blade.Lowering.lower text with
        | Ok ir -> ir
        | Error e -> failwith (label + " lower: " + e)
    let emit (label: string) (demand: string) (ir: IRProgram) =
        Environment.SetEnvironmentVariable("BLADE_PRINT",demand)
        File.WriteAllText(Path.Combine(directory,label + ".demand.txt"),demand)
        match validateIR ir with
        | Error errors -> failwith (label + " validation: " + String.concat "\n" errors)
        | Ok validated ->
            let cpp, warnings = CodeGen.genSelfContainedProgramFromIR validated ("D09_I1_" + label)
            File.WriteAllText(Path.Combine(directory,label + ".cpp"),cpp)
            let refusals = Blade.CodeGenState.takeCodegenRefusalDiagnostics cpp
            let reasons =
                [ yield! warnings
                  for d in refusals do
                      yield sprintf "%s %d:%d-%d:%d %s" d.Code d.Span.StartLine d.Span.StartCol d.Span.EndLine d.Span.EndCol d.Message ]
            File.WriteAllText(Path.Combine(directory,label + ".diagnostics.txt"),String.concat "\n" reasons)
            cpp, refusals
    let accepted (label: string) (demand: string) (ir: IRProgram) =
        let cpp, refusals = emit label demand ir
        check (label + ": no refusal")
            (refusals.IsEmpty && not (cpp.Contains "#error") && not (cpp.Contains "BLADE_CODEGEN_ERROR"))
            (String.concat "\n" (refusals |> List.map (_.Message)))
        cpp
    let refused (label: string) (text: string) =
        let cpp, refusals = emit label "out" (lowerCase label "out" text)
        check (label + ": settled BL7004, never a runnable borrowed-array fallback")
            (cpp.Contains "#error" && cpp.Contains "BLADE_CODEGEN_ERROR_STREAMED_VALUE_B"
             && (refusals |> List.exists (fun d -> d.Code="BL7004" && d.Span.StartLine>0
                                                    && d.Message.Contains "'B'" && d.Message.Contains ".stream")))
            (String.concat "\n" (refusals |> List.map (_.Message)))
    let native (label: string) (cpp: string) (judge: int -> string -> bool) =
        if not Blade.Build.capabilities.Value.HasGpp then
            skipped <- skipped + 1
            printfn "  SKIP: %s -- g++ not found" label
        else
            // Refused C++ is evidence only, never a native fixture.
            check (label + ": runnable source admitted") (not (cpp.Contains "#error")) "refusal directive"
            CodeGen.deployRuntimeHeaders directory
            let file = Path.Combine(directory,label + ".cpp")
            match compileCpp file directory with
            | Error e ->
                File.WriteAllText(Path.Combine(directory,label + ".compile-error.txt"),e)
                if isSkipError e then
                    skipped <- skipped + 1
                    printfn "  SKIP: %s -- %s" label e
                else check (label + ": compile") false e
            | Ok exe ->
                File.WriteAllText(Path.Combine(directory,label + ".executable.txt"),exe)
                match runExecutable exe with
                | Error e -> check (label + ": run") false e
                | Ok (code, output) ->
                    File.WriteAllText(Path.Combine(directory,label + ".output.txt"),output)
                    File.WriteAllText(Path.Combine(directory,label + ".exit.txt"),string code)
                    check (label + ": independent values/failure") (judge code output) output
    let values (expected: float array) (code: int) (output: string) =
        code=0 && tryParsePrintedFloats "out" output = Some expected
    let directRows (label: string) (cpp: string) =
        check (label + ": both actual row loads are window-relative, no input tables/aliases")
            (cpp.Contains "__win0 + static_cast<size_t>(static_cast<int64_t>(__i0) - static_cast<int64_t>(__w_lo)) * 3UL"
             && cpp.Contains "__win1 + static_cast<size_t>(static_cast<int64_t>(__i0) - static_cast<int64_t>(__w_lo)) * 3UL"
             && not (cpp.Contains "__rows0") && not (cpp.Contains "__rows1")
             && not (cpp.Contains "__srcext0") && not (cpp.Contains "__srcext1")
             && not (cpp.Contains "A.data[__i0]") && not (cpp.Contains "B.data[__i0]")) ""
    let fallbackTable (label: string) (cpp: string) =
        check (label + ": independent I1 table allocation/install/alias/deletion witness")
            (cpp.Contains "out__rows0 = new double*[5]()"
             && cpp.Contains "out__rows0[__r] = out__win0 + (__r - __w_lo) * 3UL"
             && cpp.Contains "Array<double, 2> A = { out__rows0, out__srcext0 }"
             && cpp.Contains "A.data[__i0]" && cpp.Contains "delete[] out__rows0;") ""
    try
        try
            for key in Blade.Optimize.optimizerGates do Environment.SetEnvironmentVariable(key,null)
            Environment.SetEnvironmentVariable("BLADE_FP_REPRO","1")
            Environment.SetEnvironmentVariable("BLADE_FP_REASSOC",null)
            Environment.SetEnvironmentVariable("BLADE_FP_CONTRACT","off")
            Environment.SetEnvironmentVariable("BLADE_PRINT","out")
            Environment.SetEnvironmentVariable("BLADE_TILE_CACHE",null)
            Environment.SetEnvironmentVariable("BLADE_LLVM","off")
            Environment.SetEnvironmentVariable("BLADE_BLAS","off")
            Environment.SetEnvironmentVariable("BLADE_OMP_THREADS","1")
            Blade.CodeGenState.setSplitTimingMode false
            Blade.CodeGenState.setSplitTimingOnlyBinding None
            File.WriteAllLines(Path.Combine(directory,"environment.txt"),
                keys |> List.map (fun k -> sprintf "%s=%A" k (Environment.GetEnvironmentVariable k)))
            let good = fixture "good" aData bData (FillFloat 0.0) [] (FillFloat 0.0) []
            let positive = source good "stream" (map "a + 2.0 * b")
            let gather = source good "stream" (map "a + B(0, 0)")
            let escape = source good "stream" (map "B")
            let positiveIr = lowerCase "positive" "out" positive
            let cpp = accepted "positive" "out" positiveIr
            check "positive: two borrowed windows, required output and run table retained"
                (cpp.Contains "out__win0 = new double[6]" && cpp.Contains "out__win1 = new double[6]"
                 && cpp.Contains "out__runs[4]" && cpp.Contains "0UL, 2UL, 4UL, 5UL"
                 && cpp.Contains "Array<double, 2> out = { allocate<"
                 && cpp.Contains "delete[] out__win0;" && cpp.Contains "delete[] out__win1;") ""
            check "positive: A then B normal window scans and scalar peels"
                (cpp.IndexOf("refuse_missing(out__win0",StringComparison.Ordinal) >= 0
                 && cpp.IndexOf("refuse_missing(out__win0",StringComparison.Ordinal) < cpp.IndexOf("refuse_missing(out__win1",StringComparison.Ordinal)
                 && cpp.Contains "A____i0____i1" && cpp.Contains "B____i0____i1") ""
            // Exercise the opaque proof through its public-in-assembly APIs.
            // These tests never allocate/execute a large resident output.
            let modul = positiveIr.Modules.Head
            let oldAnalysis = setCallablesContext (buildCallablesTableForModule modul)
            try
                let applies = ResizeArray<ApplyInfo>()
                for b in modul.Bindings do
                    iterIRExpr (function IRApplyCombinator a -> applies.Add a | _ -> ()) b.Value
                let info = applies |> Seq.find (fun a -> a.Arrays.Length = 2 && a.IsCoIteration)
                let bindingsById = modul.Bindings |> List.map (fun b -> b.Id, b) |> Map.ofList
                let operands =
                    info.Arrays |> List.map (function
                        | IRVar (id, _) -> id, bindingsById.[id].Name, modul.ProviderReads.[id]
                        | _ -> failwith "direct source fixture changed")
                let names = operands |> List.map (fun (id, vn, _) -> id, vn) |> Map.ofList
                let evidence = operands |> List.mapi (fun k (id, vn, spec) -> id, vn, spec, $"__cert__win{k}")
                let builder = IRBuilder()
                builder.EnsureAtLeast(0x40000000)
                let initial = buildLoopNestCodeGen info (operands |> List.map (fun (_, vn, _) -> vn)) "__cert" builder
                let cg = { initial with MpiSlab = true }
                let runs = [0L; 2L; 4L; 5L]
                let certify a c s r = Blade.CodeGenLoopNest.tryCertifyRank2BandPeels a c s r names
                let certification = certify info cg evidence runs
                File.WriteAllText(Path.Combine(directory,"i2-certificate.txt"),
                    match certification with Ok _ -> "admitted exact final test projection" | Error why -> "declined: " + why)
                match certification with
                | Error why -> check "I2 final proof fixture admits" false why
                | Ok cert ->
                    let stale label changed changedNames streamed =
                        let result = Blade.CodeGenLoopNest.tryGenLoopNestRank2Band cert streamed changed changedNames 0
                        check ("I2 stale " + label + " rejects before renderer mutation")
                            (match result with Error _ -> true | Ok _ -> false) ""
                    stale "kernel" { cg with KernelExpr = IRUnaryOp(IRNeg,cg.KernelExpr) } names Map.empty
                    stale "bindings" { cg with Bindings = cg.Bindings |> List.map (fun b -> { b with StrictOffset = 1 }) } names Map.empty
                    let changedInner =
                        { cg.Bindings.[1] with
                            Elements = cg.Bindings.[1].Elements |> List.map (fun e -> { e with ParamVarId = e.ParamVarId + 1000 }) }
                    stale "inner peel" { cg with Bindings = [cg.Bindings.Head; changedInner] } names Map.empty
                    stale "name context" cg (names |> Map.map (fun _ vn -> vn + "_changed")) Map.empty
                    let _, vn, spec, _ = evidence.Head
                    stale "fiber path" cg names (Map.ofList [vn, spec])
                    check "I2 owner contract rejects changed capacity and source order"
                        (Blade.CodeGenLoopNest.rank2BandOwnsWindows cert "__cert" (operands |> List.map (fun (_, vn, s) -> vn, s)) 2L
                         && not (Blade.CodeGenLoopNest.rank2BandOwnsWindows cert "__cert" (operands |> List.map (fun (_, vn, s) -> vn, s)) 1L)
                         && not (Blade.CodeGenLoopNest.rank2BandOwnsWindows cert "__cert" (operands |> List.rev |> List.map (fun (_, vn, s) -> vn, s)) 2L)
                         && not (Blade.CodeGenLoopNest.rank2BandOwnsWindows cert "__other" (operands |> List.map (fun (_, vn, s) -> vn, s)) 2L)) ""
                    let rk = (resolveKernel info.Kernel).Value.Callable
                    let prior = setCallablesContext (Map.add rk.Id { rk with Body = IRUnaryOp(IRNeg,rk.Body) } (buildCallablesTableForModule modul))
                    try stale "callee identity" cg names Map.empty
                    finally restoreAnalysisContext prior
                let declines label a c s r =
                    check ("I2 nonadmission: " + label)
                        (match certify a c s r with Error _ -> true | Ok _ -> false) ""
                declines "duplicate run" info cg evidence [0L; 2L; 2L; 5L]
                declines "different grid" info cg evidence [0L; 3L; 5L]
                declines "bad final endpoint" info cg evidence [0L; 2L; 4L; 6L]
                let originalLoop =
                    match info.Loop with IRMethodFor loop -> loop | _ -> failwith "zip loop fixture changed"
                let hiddenLoop = { originalLoop with Arrays = [IRLit(IRLitFloat 1.0)] }
                declines "hidden method_for operand" { info with Loop = IRMethodFor hiddenLoop } cg evidence runs
                declines "altered scalar variable type" info
                    { cg with KernelExpr = IRVar(cg.KernelParams.Head.VarId,IRTScalar ETFloat32) } evidence runs
                let float32Arithmetic =
                    IRBinOp(IRElementwise,IRAdd,IRLit(IRLitFloat32 1.0f),IRLit(IRLitFloat32 2.0f),SrcLoc.Nowhere)
                declines "non-Float64 arithmetic result" info { cg with KernelExpr = float32Arithmetic } evidence runs
                declines "array-valued side use" info { cg with KernelExpr = info.Arrays.Head } evidence runs
                declines "unsupported epilogue metadata" info { cg with OutputSymmVec = [1; 1] } evidence runs
                let changedSources =
                    evidence |> List.map (fun (id, vn, s, win) ->
                        let axes = s.VarType.IndexTypes |> List.map (fun ix -> { ix with Id = ix.Id + 1000 })
                        id, vn, { s with VarType = { s.VarType with IndexTypes = axes } }, win)
                declines "same extents, foreign axis identities" info cg changedSources runs
                let oversized, largeAxes =
                    match cg.OutputType with
                    | ArrayElem at ->
                        let axes = at.IndexTypes |> List.mapi (fun i ix -> if i=0 then { ix with Extent = IRLit(IRLitInt 2147483647L) } else ix)
                        mkArrayLike { at with IndexTypes = axes }, axes
                    | _ -> failwith "rank-two output fixture changed"
                let largeInfo =
                    { info with
                        OutputType = oversized
                        SharedIndexTypes = largeAxes
                        Loop = IRMethodFor { originalLoop with SharedIndexTypes = largeAxes } }
                check "I2 metadata-only oversized allocation reaches checked wide bound"
                    (match certify largeInfo { cg with OutputType = oversized } evidence runs with
                     | Error why -> why.Contains "conservative checked arithmetic"
                     | Ok _ -> false) ""
                let metadataPath = Path.Combine(good,"A",".zarray")
                let savedMetadata = File.ReadAllText metadataPath
                let malformed = System.Text.Json.Nodes.JsonNode.Parse savedMetadata
                malformed.["chunks"] <- System.Text.Json.Nodes.JsonNode.Parse "[2147483647,2]"
                try
                    File.WriteAllText(metadataPath,malformed.ToJsonString())
                    check "I2 padded chunk bytes/strides reject before provider int narrowing"
                        (match certify info cg evidence runs with
                         | Error why -> why.Contains "representable local v2"
                         | Ok _ -> false) ""
                finally
                    File.WriteAllText(metadataPath,savedMetadata)
            finally
                restoreAnalysisContext oldAnalysis
            directRows "positive" cpp
            native "positive" cpp (values [| for i in 0..4 do for j in 0..2 -> float (200+30*i+3*j) |])
            refused "gather" gather
            refused "escape" escape
            // Same already-lowered object after a refusal: no stale mask or
            // settled-refusal state may leak into the next direct emitter.
            accepted "after_refusal" "out" positiveIr |> ignore
            let termSource = source good "stream"
                                "let term = reduce(method_for(zip(A, B)) <@> lambda(a, b) -> a + 2.0 * b, (+), axes = 2)"
            let termCpp = accepted "terminal" "term" (lowerCase "terminal" "term" termSource)
            directRows "terminal" termCpp
            check "terminal: streamed owned producer and separate scalar fold retained"
                (termCpp.Contains "elementwise over segments"
                 && termCpp.Contains "double term =" && termCpp.Contains "< 5L;" && termCpp.Contains "< 3L;") ""
            native "terminal" termCpp (fun code output ->
                let printed = output.Split('\n') |> Array.tryFind (fun l -> l.Trim().StartsWith "term = ")
                let correct =
                    printed |> Option.exists (fun l ->
                        match Double.TryParse(l.Trim().Substring(7),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture) with
                        | true, v -> v=3945.0
                        | _ -> false)
                code=0 && correct)
            let residentCases : (string * string * float array) list =
                [ ("read_gather", "a + B(0, 0)", aData |> Array.map (fun x -> x+100.0));
                  ("read_escape", "B", Array.init 225 (fun i -> bData.[i%15])) ]
            for label, body, expected in residentCases do
                let text = source good "read" (map body)
                let result = accepted label "out" (lowerCase label "out" text)
                fallbackTable label result
                native label result (values expected)
            let fiberSource = source good "stream"
                                  "let out = (method_for(A) <@> lambda(row: Float64^1) -> row(0)) |> compute"
            let fiber = accepted "fiber" "out" (lowerCase "fiber" "out" fiberSource)
            check "fiber: legitimate trailing-axis reader remains selected" (fiber.Contains "A_fb_p0" && not (fiber.Contains "out__rows0")) ""
            native "fiber" fiber (values [|0.0;10.0;20.0;30.0;40.0|])
            let literalText = source good "stream" (map "[a, b]")
            let literal = accepted "literal" "out" (lowerCase "literal" "out" literalText)
            fallbackTable "literal" literal
            let literalExpected =
                [| for i in 0..14 do
                       yield aData.[i]
                       yield bData.[i] |]
            native "literal" literal (values literalExpected)
            let pureHelper = source good "stream"
                                 ("function combine(a: Float64, b: Float64) -> Float64 = { a + 2.0 * b }\n"
                                  + "let out = (method_for(zip(A, B)) <@> combine) |> compute")
            let helper = accepted "helper" "out" (lowerCase "helper" "out" pureHelper)
            directRows "helper" helper
            native "helper" helper (values [| for i in 0..4 do for j in 0..2 -> float (200+30*i+3*j) |])
            let repeatedText = positive.Replace("zip(A, B)","zip(A, A)")
            let repeated = accepted "repeated" "out" (lowerCase "repeated" "out" repeatedText)
            check "repeated A,A: one exact window/row/scalar declaration, two scalar parameter uses"
                (repeated.Contains "out__win0 + static_cast<size_t>" && not (repeated.Contains "out__win1")
                 && not (repeated.Contains "out__rows")
                 && repeated.Split("double* BLADE_RESTRICT A____i0 =",StringSplitOptions.None).Length = 2
                 && repeated.Split("double A____i0____i1 =",StringSplitOptions.None).Length = 2
                 && repeated.Split("refuse_missing(out__win0",StringSplitOptions.None).Length = 2) ""
            native "repeated" repeated (values (aData |> Array.map (fun a -> 3.0*a)))
            let distinctText =
                positive.Replace("let B = s.vars.B |> z.stream","let B = s.vars.A |> z.stream")
            let distinct = accepted "distinct_same_variable" "out" (lowerCase "distinct_same_variable" "out" distinctText)
            directRows "distinct_same_variable" distinct
            check "distinct source IDs retain both read/scan sites"
                (distinct.Contains "refuse_missing(out__win0" && distinct.Contains "refuse_missing(out__win1"
                 && distinct.Contains "delete[] out__win0;" && distinct.Contains "delete[] out__win1;") ""
            native "distinct_same_variable" distinct (values (aData |> Array.map (fun a -> 3.0*a)))
            let scalarCapture = source good "stream"
                                    ("let bias_data = s.vars.A |> z.read\n"
                                     + "let bias = bias_data(0, 0) + 3.0\n" + map "a + b + bias")
            let captured = accepted "scalar_capture" "out" (lowerCase "scalar_capture" "out" scalarCapture)
            fallbackTable "scalar_capture" captured
            native "scalar_capture" captured (values (Array.map2 (fun a b -> a+b+3.0) aData bData))
            let unaryText = source good "stream"
                                "let out = (method_for(A) <@> lambda(a: Float64) -> a + 1.0) |> compute"
            let unary = accepted "unary" "out" (lowerCase "unary" "out" unaryText)
            fallbackTable "unary" unary
            native "unary" unary (values (aData |> Array.map (fun a -> a+1.0)))
            let smallStore = fixtureShape "n1" 1L 2L aData.[0..2] bData.[0..2] (FillFloat 0.0) [] (FillFloat 0.0) []
            let smallText = source smallStore "stream" (map "a + 2.0 * b")
            let small = accepted "n1" "out" (lowerCase "n1" "out" smallText)
            directRows "n1" small
            check "N1/K2: exact one-row capacity and one run, required output"
                (small.Contains "out__win0 = new double[3]" && small.Contains "out__runs[2]"
                 && small.Contains "0UL, 1UL" && small.Contains "Array<double, 2> out = { allocate<") ""
            native "n1" small (values [|200.0;203.0;206.0|])
            let sevenA = [| for i in 0..6 do for j in 0..2 -> float (10*i+j) |]
            let sevenB = sevenA |> Array.map (fun x -> x+100.0)
            let sevenStore = fixtureShape "n7k3" 7L 3L sevenA sevenB (FillFloat 0.0) [] (FillFloat 0.0) []
            let sevenText = source sevenStore "stream" (map "a + 2.0 * b")
            let seven = accepted "n7k3" "out" (lowerCase "n7k3" "out" sevenText)
            directRows "n7k3" seven
            check "N7/K3: independent bands [0,3),[3,6),[6,7) and nine-cell capacity"
                (seven.Contains "out__win0 = new double[9]" && seven.Contains "0UL, 3UL, 6UL, 7UL") ""
            native "n7k3" seven (values [| for i in 0..6 do for j in 0..2 -> float (200+30*i+3*j) |])
            let hugeStore = fixtureShape "derived_nonfinite" 1L 2L [|1e200;2.0;3.0|] bData.[0..2] (FillFloat 0.0) [] (FillFloat 0.0) []
            let hugeText = source hugeStore "stream" (map "a * a + 2.0 * b")
            let huge = accepted "derived_nonfinite" "out" (lowerCase "derived_nonfinite" "out" hugeText)
            directRows "derived_nonfinite" huge
            native "derived_nonfinite" huge (fun code output ->
                // Check the IEEE category, not equality with infinity/NaN.
                let printed = output.Split('\n') |> Array.tryFind (fun l -> l.Trim().StartsWith "out = [")
                let correct =
                    printed |> Option.exists (fun line ->
                        let cells = line.Substring(line.IndexOf('[')).Replace("[","").Replace("]","").Split(',')
                        cells.Length = 3 && cells.[0].Trim().ToLowerInvariant() = "inf"
                        && Double.Parse(cells.[1],System.Globalization.CultureInfo.InvariantCulture) = 206.0
                        && Double.Parse(cells.[2],System.Globalization.CultureInfo.InvariantCulture) = 213.0)
                code=0 && correct && not (output.Contains "BL8015"))
            let captureTail =
                "function from_b(x: Float64) -> Float64 = { x + B(0, 0) }\n"
                + "function through(x: Float64) -> Float64 = { from_b(x) }\n"
                + map "through(a)"
            refused "captured_helper" (source good "stream" captureTail)
            let residentHelperText = source good "read" captureTail
            let residentHelper = accepted "resident_helper" "out" (lowerCase "resident_helper" "out" residentHelperText)
            fallbackTable "resident_helper" residentHelper
            native "resident_helper" residentHelper (values (aData |> Array.map (fun x -> x+100.0)))
            refused "callable_alias" (source good "stream"
                ("function from_b(x: Float64) -> Float64 = { x + B(0, 0) }\nlet h = from_b\n" + map "h(a)"))
            refused "local_alias" (source good "stream" (map "{ let full = B; a + full(0, 0) }"))
            // Preserve the exact validated source type and capture metadata;
            // only replace the gather value with a static shape query. The
            // descriptor alone must not reject this inline literal query.
            let gatherIr = lowerCase "metadata_base" "out" gather
            let b = gatherIr.Modules |> List.collect (_.Bindings) |> List.find (fun b -> b.Name="B")
            let rewrite = function
                | IRIndex (IRVar (id, ty), [IRLit(IRLitInt 0L); IRLit(IRLitInt 0L)], _) when id=b.Id ->
                    IRUnaryOp(IRCast(ETFloat64,SrcLoc.Nowhere),IRExtent(IRVar(id,ty),0))
                | e -> e
            let metadataIr : IRProgram =
                { Modules = gatherIr.Modules |> List.map (fun m ->
                    { m with
                        Functions = m.Functions |> List.map (fun f -> { f with Body = mapIRExpr rewrite f.Body })
                        Bindings = m.Bindings |> List.map (fun b -> { b with Value = mapIRExpr rewrite b.Value }) })
                  DeclSpans = gatherIr.DeclSpans }
            File.WriteAllText(Path.Combine(directory,"metadata.rewrite.txt"),"IRIndex(B,[0,0]) -> Float64(IRExtent(B,0)); original source/capture types unchanged")
            let metadata = accepted "metadata" "out" metadataIr
            fallbackTable "metadata" metadata
            native "metadata" metadata (values (aData |> Array.map (fun x -> x+5.0)))
            let finiteStore = fixture "finite_fill" aData bData (FillFloat 0.0) [] (FillFloat -1.0) [[0L;1L]]
            let finiteText = source finiteStore "stream" (map "a + 2.0 * b")
            let finite = accepted "finite_fill" "out" (lowerCase "finite_fill" "out" finiteText)
            directRows "finite_fill" finite
            let finiteExpected =
                [| for i in 0..4 do
                       for j in 0..2 ->
                           if i<2 && j=2 then float (10*i+j-2)
                           else float (200+30*i+3*j) |]
            native "finite_fill" finite (values finiteExpected)
            // The same finite fixture is recreated per failure pair; only
            // twelve padded 32-byte chunks are ever needed by one case.
            let failures : (string * string * string * int * bool * bool) list =
                [ ("same_short", "A", "0.1", 0, false, false);
                  ("same_null", "A", "0.1", 0, true, false);
                  ("cross_source", "B", "0.1", 0, false, false);
                  ("future_short", "A", "1.1", 0, false, false);
                  ("reversed", "B", "0.1", 0, false, true);
                  ("global_location", "", "", 13, false, false) ]
            for label, badSource, badChunk, missingAt, nullFill, reversed in failures do
                let missing = Array.copy aData
                missing.[missingAt] <- nan
                let omitted = if nullFill then [[0L;1L]] else []
                let store = fixture label missing bData (if nullFill then FillNone else FillFloat 0.0) omitted (FillFloat 0.0) []
                let text = source store "stream" ((map "a + 2.0 * b").Replace("zip(A, B)", if reversed then "zip(B, A)" else "zip(A, B)"))
                let result = accepted label "out" (lowerCase label "out" text)
                directRows label result
                if badSource<>"" && not nullFill then File.WriteAllBytes(Path.Combine(store,badSource,badChunk),[|0uy|])
                native label result (fun code output ->
                    let firstAssemblyFailure = label="same_short" || label="same_null" || reversed
                    let correct =
                        if firstAssemblyFailure then
                            output.Contains (if nullFill then "is missing and fill_value is null" else "is short (expected 32 bytes)")
                            && not (output.Contains "error[BL8015]")
                        else
                            output.Contains "error[BL8015]" && output.Contains (if label="global_location" then "first at [4, 1]" else "first at [0, 0]")
                            && not (output.Contains "is short") && not (output.Contains "fill_value is null")
                    code<>0 && correct && not (output.Contains "out = "))
            // Fresh lower + emitter after both indirect refusals and provider
            // failures must still admit the original ordinary map.
            accepted "final_state" "out" (lowerCase "final_state" "out" positive) |> ignore
        with ex ->
            if failed=0 then
                failed <- failed+1
                printfn "  FAIL: D09-I1 completes -- %s" (ex.ToString())
            else printfn "  D09-I1 stopped: %s" (ex.ToString())
    finally
        Blade.CodeGenState.setSplitTimingMode savedTiming
        Blade.CodeGenState.setSplitTimingOnlyBinding savedTarget
        for key,value in saved do Environment.SetEnvironmentVariable(key,value)
    printFooter "Rank-two streamed value boundary" [$"{passed} passed"; $"{failed} failed"; $"{skipped} skipped"]
    if failed=0 then 0 else 1

let runZarrTests () =
    printHeader "Zarr Provider Tests"
    let mutable passed = 0
    let mutable failed = 0

    let check (name: string) (condition: bool) (detail: string) =
        if condition then
            printfn "  PASS: %s" name
            passed <- passed + 1
        else
            printfn "  FAIL: %s — %s" name detail
            failed <- failed + 1

    let isError (r: Result<'a, string>) (needle: string) =
        match r with
        | Error e -> e.Contains needle
        | Ok _ -> false

    // "Check the environment FIRST." Probe once, here, so every e2e section
    // below can treat a failing baseline as a genuine failure instead of an
    // ambiguous "maybe the toolchain is missing" skip.
    let zCaps = Blade.Build.capabilities.Value

    /// Verdict for a failing BASELINE/ORACLE build (the `.read` reference, the
    /// serial reference, ...). Policy: a genuine missing-toolchain condition is
    /// a SKIP; a lowering error, a compile failure, or a nonzero exit of the
    /// baseline is a FAILURE. Every one of these arms used to print SKIP
    /// unconditionally, which silently DELETED the differential assertions that
    /// depend on the baseline -- the block stayed green with nothing tested.
    let baselineFailed (what: string) (e: string) =
        if not zCaps.HasGpp then printfn "  SKIP %s: g++ not found (%s)" what e
        elif isSkipError e then printfn "  SKIP %s: %s" what e
        else check ($"{what}: baseline builds and runs") false e

    // Fixture stores live under tests/fixtures/zarr_stores/ (not the repo root).
    // The SAME relative string resolves at the compiler cwd (compile-time
    // metadata loads) and, mirrored under generated_cpp_tests, at the exe
    // cwd (runtime reads/writes) — ZarrWrite/create_directories make parents.
    let fixStore (name: string) = "tests/fixtures/zarr_stores/" + name

    // ---------------------------------------------------------------
    // 1. Dtype mapping (pure)
    // ---------------------------------------------------------------
    printfn "\n--- dtype mapping ---"
    check "v2 <f8 -> ETFloat64/8"
        (match zarrDtypeV2 "<f8" with Ok d -> d.Elem = ETFloat64 && d.ByteSize = 8 && d.IsFloat | _ -> false) ""
    check "v2 <f4 -> ETFloat32/4"
        (match zarrDtypeV2 "<f4" with Ok d -> d.Elem = ETFloat32 && d.ByteSize = 4 | _ -> false) ""
    check "v2 <i4 -> ETInt64/4 (integer collapse)"
        (match zarrDtypeV2 "<i4" with Ok d -> d.Elem = ETInt64 && d.ByteSize = 4 && not d.IsFloat | _ -> false) ""
    check "v2 |i1 -> ETInt64/1"
        (match zarrDtypeV2 "|i1" with Ok d -> d.Elem = ETInt64 && d.ByteSize = 1 | _ -> false) ""
    check "v2 <u2 -> ETInt64/2"
        (match zarrDtypeV2 "<u2" with Ok d -> d.Elem = ETInt64 && d.ByteSize = 2 | _ -> false) ""
    check "v2 >f8 rejected (big-endian)"
        (isError (zarrDtypeV2 ">f8") "big-endian") ""
    check "v2 |b1 rejected (bool)"
        (isError (zarrDtypeV2 "|b1") "unsupported dtype") ""
    check "v3 float32 -> ETFloat32"
        (match zarrDtypeV3 "float32" with Ok d -> d.Elem = ETFloat32 | _ -> false) ""
    check "v3 uint64 -> ETInt64"
        (match zarrDtypeV3 "uint64" with Ok d -> d.Elem = ETInt64 && d.ByteSize = 8 | _ -> false) ""
    check "v3 bool rejected"
        (isError (zarrDtypeV3 "bool") "unsupported data_type") ""

    // ---------------------------------------------------------------
    // 2. v2 .zarray parsing (pure JSON)
    // ---------------------------------------------------------------
    printfn "\n--- v2 metadata parse ---"
    let v2good = """{"zarr_format": 2, "shape": [5, 7], "chunks": [2, 3], "dtype": "<f8",
                     "compressor": null, "fill_value": -1.5, "order": "C", "filters": null}"""
    (match parseArrayMetaV2 "A" "/tmp/s/A" v2good (Some """{"_ARRAY_DIMENSIONS": ["x", "y"]}""") with
     | Ok m ->
         check "v2 parse: shape/chunks" (m.Shape = [5L; 7L] && m.Chunks = [2L; 3L]) (sprintf "%A" m)
         check "v2 parse: dtype f8" (m.Dtype.Code = "f8") m.Dtype.Code
         check "v2 parse: fill -1.5" (m.FillValue = FillFloat -1.5) (sprintf "%A" m.FillValue)
         check "v2 parse: dim names from _ARRAY_DIMENSIONS" (m.DimNames = Some ["x"; "y"]) (sprintf "%A" m.DimNames)
         check "v2 parse: '.' separator, no prefix" (m.ChunkKeySep = "." && m.ChunkKeyPrefix = "") ""
     | Error e -> check "v2 parse: good array parses" false e)
    check "v2 parse: blosc compressor rejected BY NAME"
        (isError (parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":{"id":"blosc","cname":"lz4"},"fill_value":0,"order":"C","filters":null}""" None) "blosc") ""
    check "v2 parse: order F rejected"
        (isError (parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":0,"order":"F","filters":null}""" None) "order 'F'") ""
    check "v2 parse: filters rejected"
        (isError (parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":[{"id":"delta"}]}""" None) "filters") ""
    check "v2 parse: null fill -> FillNone"
        (match parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<i8","compressor":null,"fill_value":null,"order":"C","filters":null}""" None with
         | Ok m -> m.FillValue = FillNone | _ -> false) ""
    check "v2 parse: \"NaN\" fill"
        (match parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":"NaN","order":"C","filters":null}""" None with
         | Ok m -> (match m.FillValue with FillFloat f -> Double.IsNaN f | _ -> false) | _ -> false) ""
    check "v2 parse: '/' dimension_separator honored"
        (match parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null,"dimension_separator":"/"}""" None with
         | Ok m -> m.ChunkKeySep = "/" | _ -> false) ""
    check "v2 parse: rank mismatch rejected"
        (isError (parseArrayMetaV2 "A" "d" """{"shape":[4,5],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}""" None) "rank") ""

    // ---------------------------------------------------------------
    // 3. v3 zarr.json parsing (pure JSON)
    // ---------------------------------------------------------------
    printfn "\n--- v3 metadata parse ---"
    let v3good = """{"zarr_format": 3, "node_type": "array", "shape": [6, 4], "data_type": "float32",
                     "chunk_grid": {"name": "regular", "configuration": {"chunk_shape": [3, 4]}},
                     "chunk_key_encoding": {"name": "default", "configuration": {"separator": "/"}},
                     "fill_value": 0, "codecs": [{"name": "bytes", "configuration": {"endian": "little"}}],
                     "dimension_names": ["t", "p"]}"""
    (match parseArrayMetaV3 "B" "/tmp/s/B" v3good with
     | Ok m ->
         check "v3 parse: shape/chunks" (m.Shape = [6L; 4L] && m.Chunks = [3L; 4L]) (sprintf "%A" m)
         check "v3 parse: dtype f4" (m.Dtype.Code = "f4") m.Dtype.Code
         check "v3 parse: dimension_names" (m.DimNames = Some ["t"; "p"]) (sprintf "%A" m.DimNames)
         check "v3 parse: 'c' prefix + '/' separator" (m.ChunkKeyPrefix = "c" && m.ChunkKeySep = "/") ""
     | Error e -> check "v3 parse: good array parses" false e)
    check "v3 parse: gzip codec rejected BY NAME"
        (isError (parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[2]}},"fill_value":0,"codecs":[{"name":"bytes"},{"name":"gzip","configuration":{"level":5}}]}""") "gzip") ""
    check "v3 parse: big-endian bytes codec rejected"
        (isError (parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[2]}},"fill_value":0,"codecs":[{"name":"bytes","configuration":{"endian":"big"}}]}""") "big-endian") ""
    check "v3 parse: v2 chunk_key_encoding (no prefix, '.' default)"
        (match parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[2]}},"fill_value":0,"codecs":[{"name":"bytes"}],"chunk_key_encoding":{"name":"v2"}}""" with
         | Ok m -> m.ChunkKeyPrefix = "" && m.ChunkKeySep = "." | _ -> false) ""
    check "v3 parse: missing chunk_shape rejected"
        (isError (parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"regular"},"fill_value":0}""") "chunk_shape") ""
    check "v3 parse: non-regular chunk_grid rejected"
        (isError (parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"rectilinear"},"fill_value":0}""") "regular") ""

    // ---------------------------------------------------------------
    // 4. Chunk keys + grid math (pure)
    // ---------------------------------------------------------------
    printfn "\n--- chunk keys + grid math ---"
    let mkMeta prefix sep = {
        Name = "A"; ArrayDir = "d"; Shape = [5L; 7L]; Chunks = [2L; 3L]
        Dtype = { Code = "f8"; Elem = ETFloat64; ByteSize = 8; IsFloat = true }
        DimNames = None; FillValue = FillNone; Codec = CodecIdentity; Blade = None
        Version = 2; ChunkKeySep = sep; ChunkKeyPrefix = prefix }
    check "key v2 [0;1] -> 0.1" (chunkKey (mkMeta "" ".") [0L; 1L] = "0.1") (chunkKey (mkMeta "" ".") [0L; 1L])
    check "key v2 rank-0 -> 0" (chunkKey (mkMeta "" ".") [] = "0") ""
    check "key v3 [2;3] -> c/2/3" (chunkKey (mkMeta "c" "/") [2L; 3L] = "c/2/3") (chunkKey (mkMeta "c" "/") [2L; 3L])
    check "key v3 rank-0 -> c" (chunkKey (mkMeta "c" "/") [] = "c") ""
    check "key v2 '/'-separated -> 0/1" (chunkKey (mkMeta "" "/") [0L; 1L] = "0/1") ""
    check "gridDims [5;7]/[2;3] = [3;3]" (gridDims [5L; 7L] [2L; 3L] = [3L; 3L]) (sprintf "%A" (gridDims [5L; 7L] [2L; 3L]))
    check "gridCoords count = 9" ((gridCoords [5L; 7L] [2L; 3L]).Length = 9) ""
    check "rowMajorStrides [5;7] = [7;1]" (rowMajorStrides [5; 7] = [7; 1]) (sprintf "%A" (rowMajorStrides [5; 7]))
    check "rowMajorStrides [2;3;4] = [12;4;1]" (rowMajorStrides [2; 3; 4] = [12; 4; 1]) ""

    // ---------------------------------------------------------------
    // 5. Module construction from a mock store (pure; mirrors NetcdfTests 2-3b)
    // ---------------------------------------------------------------
    printfn "\n--- zarrStoreToModule (mock store) ---"
    let mockDt code elem bs isF = { Code = code; Elem = elem; ByteSize = bs; IsFloat = isF }
    let mockStore = {
        Path = "/mock/store"; Version = 2
        Arrays = [
            { Name = "A"; ArrayDir = "/mock/store/A"; Shape = [4L; 3L]; Chunks = [4L; 3L]
              Dtype = mockDt "f4" ETFloat32 4 true; DimNames = Some ["x"; "y"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
            { Name = "x"; ArrayDir = "/mock/store/x"; Shape = [4L]; Chunks = [4L]
              Dtype = mockDt "f8" ETFloat64 8 true; DimNames = Some ["x"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
        ]
    }
    let builder = IRBuilder()
    let modul = zarrStoreToModule builder "sample" mockStore None
    let indexDefs = modul.Types |> List.choose (function IRTDIndexType (n, it) -> Some (n, it) | _ -> None)
    check "mock module: named index types x, y"
        (indexDefs |> List.map fst |> List.sort = ["x"; "y"]) (sprintf "%A" (List.map fst indexDefs))
    let dimsFields =
        modul.Types |> List.tryPick (function IRTDStruct ("sample__dims", fs) -> Some fs | _ -> None) |> Option.defaultValue []
    let varsFields =
        modul.Types |> List.tryPick (function IRTDStruct ("sample__vars", fs) -> Some fs | _ -> None) |> Option.defaultValue []
    check "mock module: dims has x and y" (dimsFields |> List.map fst |> List.sort = ["x"; "y"]) (sprintf "%A" (List.map fst dimsFields))
    check "mock module: vars has A only (x is a coordinate array)"
        (varsFields |> List.map fst = ["A"]) (sprintf "%A" (List.map fst varsFields))
    let elemOfArrow (t: IRType) =
        match t with
        | ArrayElem at -> Some at.ElemType
        | _ -> None
    check "mock module: coordinate x keeps its ACTUAL f8 elem (Zarr divergence from NetCDF's Int64)"
        (dimsFields |> List.tryFind (fun (n, _) -> n = "x")
         |> Option.bind (snd >> elemOfArrow) = Some (IRTScalar ETFloat64))
        (sprintf "%A" (dimsFields |> List.tryFind (fun (n, _) -> n = "x") |> Option.bind (snd >> elemOfArrow)))
    check "mock module: unnamed-dim y coordinate defaults to Int64"
        (dimsFields |> List.tryFind (fun (n, _) -> n = "y")
         |> Option.bind (snd >> elemOfArrow) = Some (IRTScalar ETInt64))
        ""
    (let aIdxIds =
        varsFields |> List.tryPick (fun (n, t) ->
            if n <> "A" then None else
            match t with
            | ArrayElem at -> Some (at.IndexTypes |> List.map (_.Id))
            | _ -> None)
     let xId = indexDefs |> List.tryPick (fun (n, it) -> if n = "x" then Some it.Id else None)
     check "mock module: A's first index IS the shared x index type (same Id)"
         (match aIdxIds, xId with
          | Some (a0 :: _), Some x -> a0 = x
          | _ -> false)
         (sprintf "A ids %A, x id %A" aIdxIds xId))
    check "mock module: conflicting dim extents rejected"
        (try
            let bad = { mockStore with
                          Arrays = mockStore.Arrays @ [ { mockStore.Arrays.[0] with Name = "C"; Shape = [9L; 3L] } ] }
            zarrStoreToModule (IRBuilder()) "s" bad None |> ignore
            false
         with ex -> ex.Message.Contains "conflicting extents") ""

    // ---------------------------------------------------------------
    // 5b. TWO loads in one program (regression — silent miscompile)
    //
    // env.TypeDefs is a flat Map and registerTypeDef is a blind Map.add, while
    // field access re-resolves the struct NAME at every use site
    // (TypeCheck.structFieldTypesOf). So when both loads emitted structs under
    // the literal names "dims"/"vars", the second load overwrote the first and
    // every `first.vars.X` in the program silently type-checked against the
    // SECOND store's fields — wrong element type / wrong rank where the names
    // collided, spurious "field not found" where they didn't. No diagnostic.
    //
    // The fix is per-binding struct names ("<binding>__dims"/"<binding>__vars",
    // the convention CsvProvider established). Registering second AFTER first
    // is the ordering that used to clobber. Mirrors NetcdfTests 3b-2.
    // ---------------------------------------------------------------
    printfn "\n--- two loads in one program (no cross-store clobber) ---"
    let storeFirst = {
        Path = "/mock/first"; Version = 2
        Arrays = [
            // Shares the name "A" with the second store, but f4 / rank 2.
            { Name = "A"; ArrayDir = "/mock/first/A"; Shape = [4L; 3L]; Chunks = [4L; 3L]
              Dtype = mockDt "f4" ETFloat32 4 true; DimNames = Some ["x"; "y"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
            { Name = "only_first"; ArrayDir = "/mock/first/only_first"; Shape = [4L]; Chunks = [4L]
              Dtype = mockDt "f4" ETFloat32 4 true; DimNames = Some ["x"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
            // Coordinate array for x -> lands in first__dims.
            { Name = "x"; ArrayDir = "/mock/first/x"; Shape = [4L]; Chunks = [4L]
              Dtype = mockDt "f8" ETFloat64 8 true; DimNames = Some ["x"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
        ]
    }
    let storeSecond = {
        Path = "/mock/second"; Version = 2
        Arrays = [
            // Same name "A", deliberately different: f8 / rank 1.
            { Name = "A"; ArrayDir = "/mock/second/A"; Shape = [7L]; Chunks = [7L]
              Dtype = mockDt "f8" ETFloat64 8 true; DimNames = Some ["depth"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
            { Name = "only_second"; ArrayDir = "/mock/second/only_second"; Shape = [7L]; Chunks = [7L]
              Dtype = mockDt "f8" ETFloat64 8 true; DimNames = Some ["depth"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
            { Name = "depth"; ArrayDir = "/mock/second/depth"; Shape = [7L]; Chunks = [7L]
              Dtype = mockDt "f8" ETFloat64 8 true; DimNames = Some ["depth"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
        ]
    }
    let twoBuilder = IRBuilder()
    let modFirst = zarrStoreToModule twoBuilder "first" storeFirst None
    let modSecond = zarrStoreToModule twoBuilder "second" storeSecond None
    let structNamesOf (m: IRModule) =
        m.Types |> List.choose (function IRTDStruct (n, _) -> Some n | _ -> None) |> List.sort
    check "two loads: struct names are namespaced per binding"
        (structNamesOf modFirst = ["first__dims"; "first__vars"]
         && structNamesOf modSecond = ["second__dims"; "second__vars"])
        (sprintf "first %A second %A" (structNamesOf modFirst) (structNamesOf modSecond))

    let (envZ1, _) = registerProviderModule (emptyEnv ()) "first" modFirst
    let (envZ2, _) = registerProviderModule envZ1 "second" modSecond

    // Walk the same chain TypeCheck does: binding struct -> section field ->
    // IRTNamed <struct> -> member field. A live lookup at each hop, which is
    // exactly why a clobbered TypeDefs entry retyped earlier use sites.
    let zMemberOf (binding: string) (section: string) (field: string) : IRType option =
        let fieldsOf structName =
            match lookupTypeDef structName envZ2 with
            | Some (TDIStruct (_, _, fields, _)) -> Some fields
            | _ -> None
        fieldsOf binding
        |> Option.bind (List.tryPick (fun (n, t) -> if n = section then Some t else None))
        |> Option.bind (function IRTNamed sn -> fieldsOf sn | _ -> None)
        |> Option.bind (List.tryPick (fun (n, t) -> if n = field then Some t else None))

    // The core assertion: after the SECOND load registers, the FIRST store's
    // shared-name member still resolves to the FIRST store's type.
    check "two loads: first.vars.A keeps first's type (Float32, rank 2)"
        (match zMemberOf "first" "vars" "A" with
         | Some (ArrayElem a) -> a.ElemType = IRTScalar ETFloat32 && a.IndexTypes.Length = 2
         | _ -> false)
        (sprintf "got %A" (zMemberOf "first" "vars" "A"))
    check "two loads: second.vars.A keeps second's type (Float64, rank 1)"
        (match zMemberOf "second" "vars" "A" with
         | Some (ArrayElem a) -> a.ElemType = IRTScalar ETFloat64 && a.IndexTypes.Length = 1
         | _ -> false)
        (sprintf "got %A" (zMemberOf "second" "vars" "A"))
    check "two loads: first.vars.only_first resolves"
        ((zMemberOf "first" "vars" "only_first").IsSome) "clobbered vars struct would lose it"
    check "two loads: second.vars.only_second resolves"
        ((zMemberOf "second" "vars" "only_second").IsSome) ""
    check "two loads: first.vars has no member of the second store"
        ((zMemberOf "first" "vars" "only_second").IsNone) "second store's field leaked into first"
    check "two loads: first.dims.x resolves"
        ((zMemberOf "first" "dims" "x").IsSome) "clobbered dims struct would lose it"
    check "two loads: second.dims.depth resolves"
        ((zMemberOf "second" "dims" "depth").IsSome) ""
    check "two loads: first.dims has no member of the second store"
        ((zMemberOf "first" "dims" "depth").IsNone) "second store's dim leaked into first"
    // Bare "dims"/"vars" must not be registered at all — that was the clobber.
    check "two loads: bare 'vars'/'dims' are not registered globally"
        ((lookupTypeDef "vars" envZ2).IsNone && (lookupTypeDef "dims" envZ2).IsNone)
        "bare names would clobber across stores"

    // ---------------------------------------------------------------
    // 6. Live store roundtrips (generated fixtures; F#-only, no g++)
    // ---------------------------------------------------------------
    printfn "\n--- live store write -> load -> read (v2 and v3) ---"
    let scratch = Path.Combine(Path.GetTempPath(), "blade_zarr_tests_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory scratch |> ignore
    let aData = [| for i in 0 .. 34 -> float i * 1.5 |]
    let xCoord = [| 1.0; 2.0; 3.0; 4.0; 5.0 |]
    let bData = [| for i in 0 .. 11 -> int32 (100 + i) |]
    let mkVars () : ZarrWrite.WriteVar list = [
        { Name = "A"; DimNames = Some ["x"; "y"]; Shape = [5L; 7L]; Chunks = [2L; 3L]
          FillValue = FillFloat -1.0; Data = ZarrWrite.WF64 aData; OmitChunks = []; Blade = None }
        { Name = "x"; DimNames = Some ["x"]; Shape = [5L]; Chunks = [5L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 xCoord; OmitChunks = []; Blade = None }
        // B: 3x4 int32, chunks 2x2, chunk (1,1) omitted -> fill 99 there.
        { Name = "B"; DimNames = Some ["r"; "c"]; Shape = [3L; 4L]; Chunks = [2L; 2L]
          FillValue = FillInt 99L; Data = ZarrWrite.WI32 bData; OmitChunks = [[1L; 1L]]; Blade = None }
    ]
    let expectB () =
        // Row-major 3x4; chunk (1,1) covers rows 2, cols 2..3 -> fill 99.
        [| for r in 0 .. 2 do
             for c in 0 .. 3 ->
               if r >= 2 && c >= 2 then 99L else int64 (100 + r * 4 + c) |]
    for (version, writer) in [ (2, ZarrWrite.writeStoreV2); (3, ZarrWrite.writeStoreV3) ] do
        let root = Path.Combine(scratch, $"store_v{version}")
        writer root (mkVars ())
        (try
            let store = load root
            check ($"v{version}: load discovers 3 arrays")
                (store.Version = version && (store.Arrays |> List.map (_.Name) |> List.sort) = ["A"; "B"; "x"])
                (sprintf "version %d arrays %A" store.Version (store.Arrays |> List.map (_.Name)))
            (match tryFindArray store "A" with
             | Some m ->
                 check ($"v{version}: A meta roundtrip (shape/chunks/dims/fill)")
                     (m.Shape = [5L; 7L] && m.Chunks = [2L; 3L] && m.DimNames = Some ["x"; "y"] && m.FillValue = FillFloat -1.0)
                     (sprintf "%A" m)
             | None -> check ($"v{version}: A found") false "")
            (match readVarData root "A" with
             | Ok { DimLengths = [5; 7]; Payload = ZFloats got } ->
                 check ($"v{version}: A values roundtrip through multi-chunk assembly (edge chunks)")
                     (got = aData) (sprintf "first few: %A vs %A" (Array.truncate 5 got) (Array.truncate 5 aData))
             | Ok d -> check ($"v{version}: A values roundtrip") false (sprintf "unexpected payload %A" d.DimLengths)
             | Error e -> check ($"v{version}: A values roundtrip") false e)
            (match readVarData root "B" with
             | Ok { Payload = ZInts got } ->
                 check ($"v{version}: B omitted chunk reads as fill (99), int32 widened to int64")
                     (got = expectB ()) (sprintf "got %A" got)
             | Ok _ -> check ($"v{version}: B fill/widening") false "not ints"
             | Error e -> check ($"v{version}: B fill/widening") false e)
         with ex -> check ($"v{version}: store loads") false ex.Message)
    // Missing chunk + null fill = loud error, not silent zeros.
    (let root = Path.Combine(scratch, "store_nullfill")
     ZarrWrite.writeStoreV2 root [
        { Name = "N"; DimNames = None; Shape = [4L]; Chunks = [2L]
          FillValue = FillNone; Data = ZarrWrite.WF64 [| 1.0; 2.0; 3.0; 4.0 |]; OmitChunks = [[1L]]; Blade = None } ]
     check "null fill + missing chunk -> loud refusal"
         (isError (readVarData root "N") "refusing to invent data") (sprintf "%A" (readVarData root "N")))

    // ---------------------------------------------------------------
    // 7. C++ generator string checks
    // ---------------------------------------------------------------
    printfn "\n--- CppZarr generators ---"
    let f64Idx (builder: IRBuilder) n =
        { Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt n); Symmetry = SymNone
          Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    let wArrType =
        let b = IRBuilder()
        { ElemType = IRTScalar ETFloat64
          IndexTypes = [f64Idx b 5L; f64Idx b 7L]
          IsVirtual = false; Identity = Some (AIDVariable "A") }
    let wCode = CppZarr.genWriteVar "out_store" "A" "A" wArrType ["x"; "y"] |> String.concat "\n"
    check "genWriteVar: creates the array directory" (wCode.Contains "create_directories" && wCode.Contains "out_store/A") wCode
    check "genWriteVar: writes v2 .zarray with compressor null"
        (wCode.Contains ".zarray" && wCode.Contains "zarr_format" && wCode.Contains "compressor") ""
    check "genWriteVar: records _ARRAY_DIMENSIONS" (wCode.Contains "_ARRAY_DIMENSIONS" && wCode.Contains "\\\"x\\\"") ""
    check "genWriteVar: single whole-array chunk 0.0" (wCode.Contains "/0.0") ""
    check "genWriteVar: binary chunk write from the flat buffer"
        (wCode.Contains "A_flat" && wCode.Contains "sizeof(double) * 35") ""
    check "genWriteVar: loud on stream failure" (wCode.Contains "Zarr error" && wCode.Contains "std::exit(1)") ""
    // genReadVar needs real store metadata: reuse the v2 fixture from section 6.
    (let root = Path.Combine(scratch, "store_v2")
     let rCode = CppZarr.genReadVar root "A" "A" wArrType |> String.concat "\n"
     check "genReadVar: metadata existence check, loud" (rCode.Contains ".zarray" && rCode.Contains "Zarr error") ""
     check "genReadVar: fstream chunk reads with computed keys"
         (rCode.Contains "std::ifstream" && rCode.Contains "std::to_string(A_c0)") ""
     check "genReadVar: fill_value branch for missing chunks" (rCode.Contains "A_fillv") ""
     check "genReadVar: short-read guard" (rCode.Contains "gcount()") ""
     check "genReadVar: materializes nested Array (allocate + promote)"
         (rCode.Contains "allocate<typename promote<double, 2>::type") ""
     check "genReadVar: releases buffers" (rCode.Contains "delete[] A_flat" && rCode.Contains "delete[] A_cbuf") "")

    // ---------------------------------------------------------------
    // 8. Registry + surface gates
    // ---------------------------------------------------------------
    printfn "\n--- registry + module-surface gates ---"
    Blade.ProviderStatics.install ()
    check "registry: zarr registered"
        (match Blade.ProviderRegistry.tryFind "zarr" with Some s -> s.Name = "zarr" | None -> false) ""
    check "registry: csv + icechunk + netcdf registered too"
        ((Blade.ProviderRegistry.names ()) |> List.sort = ["csv"; "icechunk"; "netcdf"; "zarr"]) (sprintf "%A" (Blade.ProviderRegistry.names ()))
    check "registry: zarr rejects load_compound (no compound reader)"
        (match Blade.ProviderRegistry.tryFind "zarr" with Some s -> s.GenReadCompoundVar.IsNone | None -> false) ""
    check "registry: zarr needs no link flags"
        (match Blade.ProviderRegistry.tryFind "zarr" with Some s -> s.LinkNeeds.Contains "none" | None -> false) ""
    let typeErrOf (src: string) : string =
        match Parser.parseProgram src with
        | Error e -> e.Message
        | Ok program ->
            match TypeCheck.typeCheck program with
            | Error errs -> errs |> List.map TypeEnv.formatCompileError |> String.concat "; "
            | Ok _ -> ""
    check "old spelling `import Providers.NetCDF` -> steering error"
        ((typeErrOf "import Providers.NetCDF as NetCDF\nlet x = 1\n").Contains "import netcdf as")
        (typeErrOf "import Providers.NetCDF as NetCDF\nlet x = 1\n")
    check "`from zarr import load` -> selective import rejected"
        ((typeErrOf "from zarr import load\nlet x = 1\n").Contains "selective import")
        (typeErrOf "from zarr import load\nlet x = 1\n")
    check "bare `|> read` is a hard break (unbound identifier)"
        ((typeErrOf "let x = 5 |> read\n").Contains "read")
        (typeErrOf "let x = 5 |> read\n")

    // ---------------------------------------------------------------
    // 9. Static fold e2e (hermetic — needs no g++, no external library)
    // ---------------------------------------------------------------
    printfn "\n--- provider statics: zarr fold + ceiling ---"
    (let foldRoot = fixStore "zarr_fold_store"
     (try Directory.Delete(foldRoot, true) with _ -> ())
     ZarrWrite.writeStoreV2 foldRoot [
        { Name = "x"; DimNames = Some ["x"]; Shape = [6L]; Chunks = [3L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| 1.0; 2.0; 3.0; 4.0; 5.0; 6.0 |]; OmitChunks = []; Blade = None }
        { Name = "A"; DimNames = Some ["x"]; Shape = [6L]; Chunks = [6L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| for i in 1 .. 6 -> float (i * i) |]; OmitChunks = []; Blade = None } ]
     let foldSource = """
import zarr as z

let sample = z.load("tests/fixtures/zarr_stores/zarr_fold_store")
let static xd = sample.dims.x |> z.read
let static n = length(xd)
let static ps = prodsum(xd, xd)
let a = n
let b = ps
"""
     (try
         match Parser.parseProgram foldSource with
         | Error e -> check "zarr fold: parses" false e.Message
         | Ok program ->
             match TypeCheck.typeCheck program with
             | Error errs ->
                 check "zarr fold: typechecks (fold succeeded)" false
                     (errs |> List.map TypeEnv.formatCompileError |> String.concat "; ")
             | Ok _ ->
                 check "zarr fold: typechecks (fold succeeded)" true ""
                 match Blade.StaticEval.resolveStatics program.Modules.Head.Decls with
                 | Ok (se, _) ->
                     check "zarr fold: length(xd) = 6"
                         (Map.tryFind "n" se.Values = Some (Blade.StaticEval.SVInt 6L))
                         (sprintf "got %A" (Map.tryFind "n" se.Values))
                     check "zarr fold: prodsum(xd, xd) = 91"
                         (Map.tryFind "ps" se.Values = Some (Blade.StaticEval.SVFloat 91.0))
                         (sprintf "got %A" (Map.tryFind "ps" se.Values))
                 | Error e -> check "zarr fold: resolveStatics" false e
      with ex -> check "zarr fold: runs" false ex.Message)
     // Fold ceiling: 70000 > 65536 elements refuses with steering.
     let bigRoot = fixStore "zarr_fold_big"
     (try Directory.Delete(bigRoot, true) with _ -> ())
     ZarrWrite.writeStoreV2 bigRoot [
        { Name = "big"; DimNames = Some ["n"]; Shape = [70000L]; Chunks = [70000L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 (Array.zeroCreate 70000); OmitChunks = []; Blade = None } ]
     let bigSource = """
import zarr as z

let sample = z.load("tests/fixtures/zarr_stores/zarr_fold_big")
let static v = sample.vars.big |> z.read
"""
     (try
         match Parser.parseProgram bigSource with
         | Error e -> check "zarr fold ceiling: parses" false e.Message
         | Ok program ->
             match TypeCheck.typeCheck program with
             | Error errs ->
                 let msg = errs |> List.map TypeEnv.formatCompileError |> String.concat "; "
                 check "zarr fold ceiling: 70000 elements refused with steering"
                     (msg.Contains "fold ceiling") msg
             | Ok _ -> check "zarr fold ceiling: 70000 elements refused with steering" false "typechecked (fold went through?)"
      with ex -> check "zarr fold ceiling: runs" false ex.Message))

    // ---------------------------------------------------------------
    // 9b. Provider axes reach the module elaborations (hermetic)
    // ---------------------------------------------------------------
    // spectra/math/sgs/ppl specialize their generated code to STATIC extents,
    // and they run BEFORE the type check that registers `<store>.index.<dim>`
    // — so they resolve the axis against the store's metadata themselves. The
    // assertion is code IDENTITY: `type X = sample.index.x` must generate
    // exactly what the hand-copied `type X = Idx<6>` generates. Reuses the
    // section-9 fold fixture (dim x, extent 6).
    printfn "\n--- provider axes: spectra transform over sample.index.x ---"
    (let axisSource (axis: string) = sprintf """
import spectra as sp
import zarr as z

let sample = z.load("tests/fixtures/zarr_stores/zarr_fold_store")
type X = %s
type Sig = Array<Float64 like X>
let A: Sig = sample.vars.A |> z.read
let S = sp.fft(A)
"""
                                        axis
     (try
         match lower (axisSource "sample.index.x"), lower (axisSource "Idx<6>") with
         | Ok irProv, Ok irLit ->
             check "provider axis: sp.fft over sample.index.x elaborates" true ""
             let (cppProv, _) = CodeGen.genSelfContainedProgramFromIR irProv "zarr_axis_probe"
             let (cppLit, _) = CodeGen.genSelfContainedProgramFromIR irLit "zarr_axis_probe"
             check "provider axis: generates exactly what Idx<6> generates"
                 (cppProv = cppLit) "generated program differs from the literal-extent twin"
         | Error e, _ ->
             check "provider axis: sp.fft over sample.index.x elaborates" false e
         | _, Error e ->
             check "provider axis: Idx<6> twin lowers" false e
      with ex -> check "provider axis: runs" false ex.Message))

    // ---------------------------------------------------------------
    // 10. Runtime dense read e2e (g++; store generated on the fly)
    // ---------------------------------------------------------------
    // Multi-chunk WITH edge chunks and one missing chunk (fill -1), so the
    // runtime path exercises key formatting, intersection copy, and fill.
    printfn "\n--- dense read e2e: method_for(z.read(s.vars.A)) <@> (x -> x+x) ---"
    let e2eDir = "./generated_cpp_tests"
    if not (Directory.Exists e2eDir) then Directory.CreateDirectory e2eDir |> ignore
    for (version, writer) in [ (2, ZarrWrite.writeStoreV2); (3, ZarrWrite.writeStoreV3) ] do
        let storeName = fixStore ($"zarr_e2e_v{version}")
        let storeInDir = Path.Combine(e2eDir, storeName)
        let e2eVars : ZarrWrite.WriteVar list = [
            { Name = "A"; DimNames = Some ["x"; "y"]; Shape = [5L; 7L]; Chunks = [2L; 3L]
              FillValue = FillFloat -1.0; Data = ZarrWrite.WF64 aData; OmitChunks = [[2L; 2L]]; Blade = None } ]
        // Twice: at the compiler's cwd (compile-time metadata resolves the
        // relative path here) and beside the exe (runtime reads resolve
        // against the executable's working directory) — the same split as
        // NetcdfTests' sample.nc copy.
        (try Directory.Delete(storeName, true) with _ -> ())
        (try Directory.Delete(storeInDir, true) with _ -> ())
        writer storeName e2eVars
        writer storeInDir e2eVars
        let readSource = sprintf """
import zarr as z

let sample = z.load("%s")
let A = sample.vars.A |> z.read
let out = method_for(A) <@> lambda(x) -> x + x |> compute
"""
                             storeName
        try
            match lower readSource with
            | Ok ir ->
                check ($"e2e v{version}: ProviderReads spec (provider=zarr, maskless)")
                    (ir.Modules.[0].ProviderReads |> Map.exists (fun _ s -> s.Provider = "zarr" && s.VarName = "A" && s.MaskName = None))
                    ""
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir ($"zarr_read_e2e_v{version}")
                check ($"e2e v{version}: emits fstream reads, no netcdf dependency")
                    (cppCode.Contains "std::ifstream" && not (cppCode.Contains "netcdf.h")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, $"zarr_read_e2e_v{version}.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     check ($"e2e v{version}: compiles (pure std C++ — no link flags)") true ""
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          check ($"e2e v{version}: runs (exit 0)") true ""
                          // Ground truth via the F# read path (fill -1 in the
                          // omitted chunk region), kernel doubles it.
                          (match readVarData storeInDir "A" with
                           | Ok { Payload = ZFloats truth } ->
                               let expected = truth |> Array.map (fun x -> x + x)
                               // Shape-tolerant flatten (TestHarness): `out`
                               // is rank 2 and prints NESTED since 7ac4d3a.
                               (match tryParsePrintedFloats "out" runOut with
                                | None -> check ($"e2e v{version}: values match ground truth") false "no parseable out = [...] line"
                                | Some parsed ->
                                    let ok =
                                        parsed.Length = expected.Length
                                        && Array.forall2 (fun a b -> abs (a - b) <= 1e-9 * max 1.0 (abs b)) parsed expected
                                    check ($"e2e v{version}: values match ground truth (2*A incl. fill region)")
                                        ok
                                        ($"{parsed.Length} vs {expected.Length} values"))
                           | Ok _ -> check ($"e2e v{version}: values match ground truth") false "truth not floats"
                           | Error e -> check ($"e2e v{version}: values match ground truth") false e)
                          // Missing store at runtime fails loudly (metadata check).
                          if version = 2 then
                              let missingDir = Path.Combine(Path.GetTempPath(), "blade_zarr_missing_" + Guid.NewGuid().ToString("N"))
                              Directory.CreateDirectory missingDir |> ignore
                              (try
                                  let exeCopy = Path.Combine(missingDir, Path.GetFileName exePath)
                                  File.Copy(exePath, exeCopy, true)
                                  (match runExecutable exeCopy with
                                   | Ok (code, missOut) ->
                                       check "e2e: missing store at runtime fails loudly (nonzero + Zarr error)"
                                           (code <> 0 && missOut.Contains "Zarr error")
                                           ($"exit {code}: {(missOut.Substring(0, min 200 missOut.Length))}")
                                   | Error e -> check "e2e: missing store fails loudly" false e)
                               finally
                                  try Directory.Delete(missingDir, true) with _ -> ())
                      | Ok (code, runOut) -> check ($"e2e v{version}: runs (exit 0)") false ($"exit {code}: {runOut}")
                      | Error e -> check ($"e2e v{version}: runs (exit 0)") false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP zarr read e2e v%d (compile skipped): %s" version e
                     else check ($"e2e v{version}: compiles") false e)
            | Error e -> check ($"e2e v{version}: lowers") false e
        with ex -> check ($"e2e v{version}") false ex.Message

    // ---------------------------------------------------------------
    // 10c. Provider-inherited segmentation (docs/plans/structural/07 §3.1):
    // `type CX = Chunked<sample.index.x, store>` takes the store's own chunk
    // edge on x (recorded at the load site by ProviderRegistry.DimChunks),
    // and the structural grouping / ungroup pipeline runs over it. A store
    // whose variables chunk x differently refuses at the declaration.
    // ---------------------------------------------------------------
    printfn "
--- inherited segmentation: Chunked<sample.index.x, store> ---"
    let inheritedSegmentationE2E () =
        let segStore = fixStore "zarr_segments"
        let segInDir = Path.Combine(e2eDir, segStore)
        let segVars : ZarrWrite.WriteVar list = [
            { Name = "A"; DimNames = Some ["x"]; Shape = [10L]; Chunks = [4L]
              FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| for i in 1 .. 10 -> float i |]; OmitChunks = []; Blade = None } ]
        (try Directory.Delete(segStore, true) with _ -> ())
        (try Directory.Delete(segInDir, true) with _ -> ())
        ZarrWrite.writeStoreV3 segStore segVars
        ZarrWrite.writeStoreV3 segInDir segVars
        let segSource = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
let A = sample.vars.A |> z.read
let seg = segments(CX)
let sizes = extents(seg)
let g = group_by(A, seg)
let sums = method_for(g) <@> lambda(r) -> reduce(r, (+)) |> compute
let back = ungroup(g)
"""
                            segStore
        try
            match lower segSource with
            | Ok ir ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_inherited"
                check "inherited segmentation: emits the structural grouping (no __perm)"
                    (cppCode.Contains "structural" && not (cppCode.Contains "seg__perm")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, "zarr_segments_inherited.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          let has (line: string) = runOut.Contains line
                          check "inherited segmentation: sizes = [4, 4, 2]" (has "sizes = [4, 4, 2]") runOut
                          check "inherited segmentation: per-segment sums" (has "sums = [10.0, 26.0, 19.0]") runOut
                          check "inherited segmentation: ungroup restores the axis" (has "back = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0]") runOut
                      | Ok (code, out) -> check "inherited segmentation: runs" false ($"exit {code}: {out}")
                      | Error e -> check "inherited segmentation: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP inherited segmentation (compile skipped): %s" e
                     else check "inherited segmentation: compiles" false e)
            | Error e -> check "inherited segmentation: lowers" false e
        with ex -> check "inherited segmentation" false ex.Message
        // the refusal: two variables chunk x differently
        let mixStore = fixStore "zarr_segments_mixed"
        (try Directory.Delete(mixStore, true) with _ -> ())
        ZarrWrite.writeStoreV3 mixStore [
            { Name = "A"; DimNames = Some ["x"]; Shape = [10L]; Chunks = [4L]
              FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| for i in 1 .. 10 -> float i |]; OmitChunks = []; Blade = None }
            { Name = "B"; DimNames = Some ["x"]; Shape = [10L]; Chunks = [5L]
              FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| for i in 1 .. 10 -> float i |]; OmitChunks = []; Blade = None } ]
        let mixSource = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
"""
                            mixStore
        (match lower mixSource with
         | Error e -> check "inherited segmentation: non-uniform chunking refuses with a steer" (e.Contains "does not chunk 'x' uniformly") e
         | Ok _ -> check "inherited segmentation: non-uniform chunking refuses with a steer" false "lowered")

    inheritedSegmentationE2E ()

    // ---------------------------------------------------------------
    // 10d. The FILE level (docs/plans/structural/07 §2.7, decisions D1/D3/D5):
    // `type T = Chunked<s1.index.x, [[s1, store], [s2, store]]>` tiles x over
    // two stores in order, each keeping its own chunk grid (a dependent chunk
    // level: 3-cell chunks in the first file, 2-cell in the second). `files(T)`
    // is the file grouping (labelled by the store names), `segments(T)` the
    // innermost chunk grouping, and `ungroup([a1, a2], T)` names a variable
    // that lives in both stores over the tiled axis. Provider-agnostic: only
    // the stores' extents and edges are consulted, never a coordinate value.
    // ---------------------------------------------------------------
    printfn "
--- file level: Chunked<s1.index.x, [[s1, store], [s2, store]]> ---"
    let fileLevelE2E () =
        let st1 = fixStore "zarr_tile_a"
        let st2 = fixStore "zarr_tile_b"
        let mk (name: string) (n: int) (lo: int) (edge: int64) : ZarrWrite.WriteVar list =
            [ { Name = "A"; DimNames = Some ["x"]; Shape = [int64 n]; Chunks = [edge]
                FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| for i in lo .. lo + n - 1 -> float i |]; OmitChunks = []; Blade = None } ]
        for (store, vars) in [ (st1, mk "A" 6 1 3L); (st2, mk "A" 4 7 2L) ] do
            let inDir = Path.Combine(e2eDir, store)
            (try Directory.Delete(store, true) with _ -> ())
            (try Directory.Delete(inDir, true) with _ -> ())
            ZarrWrite.writeStoreV3 store vars
            ZarrWrite.writeStoreV3 inDir vars
        let src = sprintf """
import zarr as z

let s1 = z.load("%s")
let s2 = z.load("%s")
type T = Chunked<s1.index.x, [[s1, store], [s2, store]]>
let a1 = s1.vars.A |> z.read
let a2 = s2.vars.A |> z.read
let a = ungroup([a1, a2], T)
let fs = files(T)
let fsizes = extents(fs)
let seg = segments(T)
let ssizes = extents(seg)
let gf = group_by(a, fs)
let fsums = method_for(gf) <@> lambda(r) -> reduce(r, (+)) |> compute
let gs = group_by(a, seg)
let ssums = method_for(gs) <@> lambda(r) -> reduce(r, (+)) |> compute
let back = ungroup(gs)
"""
                            st1 st2
        try
            match lower src with
            | Ok ir ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_files"
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, "zarr_segments_files.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          let has (line: string) = runOut.Contains line
                          check "file level: the tiled variable" (has "a = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0]") runOut
                          check "file level: files(T) sizes = [6, 4]" (has "fsizes = [6, 4]") runOut
                          check "file level: segments(T) sizes = [3, 3, 2, 2] (per-file chunk grids)" (has "ssizes = [3, 3, 2, 2]") runOut
                          check "file level: per-file sums" (has "fsums = [21.0, 34.0]") runOut
                          check "file level: per-chunk sums" (has "ssums = [6.0, 15.0, 15.0, 19.0]") runOut
                          check "file level: ungroup of the chunk grouping restores the axis" (has "back = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0]") runOut
                      | Ok (code, out) -> check "file level: runs" false ($"exit {code}: {out}")
                      | Error e -> check "file level: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP file level (compile skipped): %s" e
                     else check "file level: compiles" false e)
            | Error e -> check "file level: lowers" false e
        with ex -> check "file level" false ex.Message
        // refusals: a wrong row count, and a store without the dimension
        let bad1 = sprintf """
import zarr as z

let s1 = z.load("%s")
let s2 = z.load("%s")
type T = Chunked<s1.index.x, [[s1, store], [s2, store]]>
let a1 = s1.vars.A |> z.read
let a = ungroup([a1], T)
"""
                             st1 st2
        (match lower bad1 with
         | Error e -> check "file level: wrong row count refuses" (e.Contains "tiles 2 store(s) but 1 row(s)") e
         | Ok _ -> check "file level: wrong row count refuses" false "lowered")
        let bad2 = sprintf """
import zarr as z

let s1 = z.load("%s")
let s2 = z.load("%s")
type T = Chunked<s1.index.x, [[s1, store], [s2, 3]]>
let a1 = s1.vars.A |> z.read
let a2 = s2.vars.A |> z.read
let a = ungroup([a2, a1], T)
"""
                             st1 st2
        (match lower bad2 with
         | Error e -> check "file level: a row of the wrong extent refuses" (e.Contains "has extent 4 but store 0") e
         | Ok _ -> check "file level: a row of the wrong extent refuses" false "lowered")
    fileLevelE2E ()

    // ---------------------------------------------------------------
    // 10e. PER-SEGMENT streamed reads (docs/plans/structural/07 §3.4): a rank-1
    // variable bound with `.stream` and grouped by a structural grouping is
    // read one run at a time, straight into each group's row -- the emitted
    // C++ has no whole-array buffer for it. A KEY grouping over a streamed
    // variable keeps refusing with a steer to `.read`.
    // ---------------------------------------------------------------
    printfn "
--- per-segment stream: group_by(s.vars.A |> z.stream, segments(CX)) ---"
    let streamedSegmentsE2E () =
        let segStore = fixStore "zarr_segments"    // written by 10c (10 cells, chunked at 4)
        let src = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
let A = sample.vars.A |> z.stream
let seg = segments(CX)
let g = group_by(A, seg)
let sums = method_for(g) <@> lambda(r) -> reduce(r, (+)) |> compute
let counts = method_for(g) <@> lambda(r) -> extents(r) |> compute
"""
                            segStore
        try
            match lower src with
            | Ok ir ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_streamed"
                check "per-segment stream: rows are read per run (marker present)"
                    (cppCode.Contains "per-segment STREAMED rows") ""
                check "per-segment stream: no whole-array buffer for A"
                    (not (cppCode.Contains "A_flat = new") && not (cppCode.Contains "Array<double, 1> A = ")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, "zarr_segments_streamed.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          let has (line: string) = runOut.Contains line
                          check "per-segment stream: per-run sums" (has "sums = [10.0, 26.0, 19.0]") runOut
                          check "per-segment stream: per-run counts" (has "counts = [4, 4, 2]") runOut
                      | Ok (code, out) -> check "per-segment stream: runs" false ($"exit {code}: {out}")
                      | Error e -> check "per-segment stream: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP per-segment stream (compile skipped): %s" e
                     else check "per-segment stream: compiles" false e)
            | Error e -> check "per-segment stream: lowers" false e
        with ex -> check "per-segment stream" false ex.Message
        // a key grouping over a streamed variable still refuses
        let bad = sprintf """
import zarr as z

let sample = z.load("%s")
let A = sample.vars.A |> z.stream
let keys = [0, 0, 1, 1, 2, 2, 0, 1, 2, 0]
let gk = group_keys(keys)
let g = group_by(A, gk)
let sums = method_for(g) <@> lambda(r) -> reduce(r, (+)) |> compute
"""
                            segStore
        (match lower bad with
         | Ok ir ->
             (try
                 let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_streamed_bad"
                 let refusals = CodeGen.takeCodegenRefusalDiagnostics cppCode
                 check "per-segment stream: a key grouping over a streamed variable refuses with a steer"
                     (refusals |> List.exists (fun r -> (string r).Contains "needs a structural grouping") || cppCode.Contains "needs a structural grouping") cppCode
              with ex -> check "per-segment stream: a key grouping over a streamed variable refuses with a steer" (ex.Message.Contains "needs a structural grouping") ex.Message)
         | Error e -> check "per-segment stream: key grouping program lowers" false e)
        // The same streamed group_by inside a FUNCTION BODY -- where an
        // allocation scope exists, so the result must be freed at its exit.
        // The row table and the one pool its rows slice are freed together
        // (deallocate_ragged_storage). This branch once registered that free
        // with its argument missing -- a discarded partial application, the
        // build's FS0193 -- so nothing was freed and every call leaked both.
        // The top-level program above cannot see it: main frees nothing.
        let inFn = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
let A = sample.vars.A |> z.stream
let seg = segments(CX)
function run_sums(k: Int64) -> Float = {
    let g = group_by(A, seg)
    let s = method_for(g) <@> lambda(r) -> reduce(r, (+)) |> compute
    reduce(s, (+)) + Float64(k)
}
let total = run_sums(0)
"""
                            segStore
        try
            match lower inFn with
            | Ok ir ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_streamed_fn"
                let marker = cppCode.IndexOf "per-segment STREAMED rows"
                check "per-segment stream in a function body: rows are read per run (marker present)" (marker >= 0) ""
                // The table's name is a generated `__vN`, so read it off the
                // streamed block's own declaration rather than pinning a number.
                let table =
                    if marker < 0 then ""
                    else
                        System.Text.RegularExpressions.Regex.Match(
                            cppCode.Substring marker, @"Array<double\*, 1> (\w+) = \{ new double\*\[").Groups.[1].Value
                check "per-segment stream in a function body: the row table and its pool are freed at scope exit"
                    (table <> ""
                     && cppCode.Contains $"nested_array_utilities::deallocate_ragged_storage({table}.data, {table}__pool);")
                    (if table = "" then "no streamed row-table declaration found"
                     else $"no deallocate_ragged_storage({table}.data, {table}__pool) in the emitted C++")
                let cppFile = Path.Combine(e2eDir, "zarr_segments_streamed_fn.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          check "per-segment stream in a function body: total of the per-run sums"
                              (runOut.Contains "total = 55.0") runOut
                      | Ok (code, out) -> check "per-segment stream in a function body: runs" false ($"exit {code}: {out}")
                      | Error e -> check "per-segment stream in a function body: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP per-segment stream in a function body (compile skipped): %s" e
                     else check "per-segment stream in a function body: compiles" false e)
            | Error e -> check "per-segment stream in a function body: lowers" false e
        with ex -> check "per-segment stream in a function body" false ex.Message
        // The same group_by inside a KERNEL body is different: the kernel is
        // compiled as a separate C++ function that sees only its parameters,
        // and a grouping cannot be one -- it is an opaque sentinel whose state
        // (`seg__ngroups`, `seg__offsets`) is main() locals. It used to reach
        // g++ as "'seg__ngroups' / 'A_fillv' / 'A' was not declared in this
        // scope"; codegen now refuses the captured grouping (BL7004) with a
        // steer, and the #error guard stops the compile before g++ runs.
        let inKernel = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
type K = Idx<3>
let A = sample.vars.A |> z.stream
let seg = segments(CX)
let totals = method_for(range<K>) <@> lambda(k) -> {
    let g = group_by(A, seg)
    let s = method_for(g) <@> lambda(r) -> reduce(r, (+)) |> compute
    reduce(s, (+)) + Float64(k)
} |> compute
"""
                            segStore
        (match lower inKernel with
         | Ok ir ->
             (try
                 let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_streamed_kernel"
                 let refusals = CodeGen.takeCodegenRefusalDiagnostics cppCode |> List.map string
                 let steer = "captures the grouping 'seg'"
                 check "per-segment stream in a kernel body: refused before g++ (BL7004), with a steer"
                     (cppCode.Contains "#error" && refusals |> List.exists (fun r -> r.Contains steer))
                     (String.concat " | " refusals)
              with ex -> check "per-segment stream in a kernel body: refused before g++ (BL7004), with a steer" false ex.Message)
         | Error e -> check "per-segment stream in a kernel body: lowers" false e)
        // ...and the steer it gives compiles: group ONCE, outside the kernel,
        // and use the result inside. A group_by result is forwarded into the
        // kernel's function (its row table plus the grouping's ngroups and
        // offsets as hidden params) -- and it reads the store once, not once
        // per iteration.
        let steered = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
type K = Idx<3>
let A = sample.vars.A |> z.stream
let seg = segments(CX)
let g = group_by(A, seg)
let totals = method_for(range<K>) <@> lambda(k) -> {
    let s = method_for(g) <@> lambda(r) -> reduce(r, (+)) |> compute
    reduce(s, (+)) + Float64(k)
} |> compute
"""
                            segStore
        try
            match lower steered with
            | Ok ir ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_streamed_steered"
                check "per-segment stream, grouped outside a kernel: rows are read per run (marker present)"
                    (cppCode.Contains "per-segment STREAMED rows") ""
                let cppFile = Path.Combine(e2eDir, "zarr_segments_streamed_steered.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          check "per-segment stream, grouped outside a kernel: per-iteration totals"
                              (runOut.Contains "totals = [55.0, 56.0, 57.0]") runOut
                      | Ok (code, out) -> check "per-segment stream, grouped outside a kernel: runs" false ($"exit {code}: {out}")
                      | Error e -> check "per-segment stream, grouped outside a kernel: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP per-segment stream grouped outside a kernel (compile skipped): %s" e
                     else check "per-segment stream, grouped outside a kernel: compiles" false e)
            | Error e -> check "per-segment stream, grouped outside a kernel: lowers" false e
        with ex -> check "per-segment stream, grouped outside a kernel" false ex.Message
        // A streamed variable used as a plain ARRAY anywhere else. `A` is
        // never materialized -- its binding emits a reader, and nothing named
        // `A` -- so these used to reach g++ as "'A' was not declared in this
        // scope". Each is now a BL7004 refusal before g++ runs, whatever
        // construct the use sits in: a fold inlined from a kernel body into
        // main(), the same fold inside a named function's closure, an
        // argument to a function, a scalar index.
        let streamedLeaks =
            [ ("a fold inside a kernel body",
               "let totals = method_for(range<K>) <@> lambda(k) -> reduce(A, (+)) + Float64(k) |> compute")
              ("a fold inside a named function",
               "function total_plus(k: Int64) -> Float = reduce(A, (+)) + Float64(k)\nlet totals = method_for(range<K>) <@> lambda(k) -> total_plus(k) |> compute")
              ("an argument to a function",
               "function total(v: Array<Float like CX>) -> Float = reduce(v, (+))\nlet t = total(A)")
              ("a scalar index",
               "let x = A((3 : CX)) + 1.0") ]
        for (what, body) in streamedLeaks do
            let src = sprintf "import zarr as z\nlet sample = z.load(\"%s\")\ntype CX = Chunked<sample.index.x, store>\ntype K = Idx<3>\nlet A: Array<Float like CX> = sample.vars.A |> z.stream\n%s\n" segStore body
            let label = $"streamed variable used as an array ({what}): refused before g++ (BL7004), with a steer"
            match lower src with
            | Ok ir ->
                (try
                    let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_streamed_leak"
                    let refusals = CodeGen.takeCodegenRefusalDiagnostics cppCode |> List.map string
                    check label
                        (cppCode.Contains "#error"
                         && refusals |> List.exists (fun r -> r.Contains "'A' is bound with `.stream`, so it is never materialized"))
                        (String.concat " | " refusals)
                 with ex -> check label false ex.Message)
            | Error e -> check $"streamed variable used as an array ({what}): lowers" false e
        // ...and the steer compiles: fold at top level (the streamed fold,
        // structural/07 D6) and use the scalar inside the kernel.
        let foldedOutside = sprintf """
import zarr as z

let sample = z.load("%s")
type K = Idx<3>
let A = sample.vars.A |> z.stream
let t = reduce(A, (+))
let totals = method_for(range<K>) <@> lambda(k) -> t + Float64(k) |> compute
"""
                                segStore
        try
            match lower foldedOutside with
            | Ok ir ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_streamed_fold_outside"
                let cppFile = Path.Combine(e2eDir, "zarr_streamed_fold_outside.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          check "streamed fold at top level, used in a kernel: totals"
                              (runOut.Contains "totals = [55.0, 56.0, 57.0]") runOut
                      | Ok (code, out) -> check "streamed fold at top level, used in a kernel: runs" false ($"exit {code}: {out}")
                      | Error e -> check "streamed fold at top level, used in a kernel: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP streamed fold at top level (compile skipped): %s" e
                     else check "streamed fold at top level, used in a kernel: compiles" false e)
            | Error e -> check "streamed fold at top level, used in a kernel: lowers" false e
        with ex -> check "streamed fold at top level, used in a kernel" false ex.Message
    streamedSegmentsE2E ()

    // ---------------------------------------------------------------
    // 10f. STREAMED FOLD (decision D6 of docs/plans/structural/07): `reduce`
    // over a rank-1 `.stream` variable is the plain flat fold at the API and
    // walks the store one block at a time in storage order -- the same
    // operation sequence as the flat fold over a materialized copy, so the
    // answer is bitwise identical and no reorder licence is involved. Both
    // a builtin operator and a lambda kernel; and `segments(A)` off the
    // annotation's `Chunked` slot. The decision is recorded for `blade plan`.
    // ---------------------------------------------------------------
    printfn "
--- streamed fold: reduce(s.vars.A |> z.stream, (+)) ---"
    let streamedFoldE2E () =
        let segStore = fixStore "zarr_segments"    // 10 cells 1..10, chunked at 4
        let src = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
let A: Array<Float like CX> = sample.vars.A |> z.stream
let total = reduce(A, (+))
let biggest = reduce(A, lambda(x, y) -> if x > y then x else y)
let shifted = reduce(A, (+), 100.0)
let seg = segments(A)
let sizes = extents(seg)
"""
                            segStore
        try
            Blade.Effects.Decisions.start ()
            match lower src with
            | Ok ir ->
                let decisions = Blade.Effects.Decisions.drain ()
                check "streamed fold: `blade plan` records segment-streaming for the folds"
                    (decisions |> List.filter (fun d -> d.Rule = "segment-streaming" && d.Outcome = Blade.Effects.Applied) |> List.length >= 3)
                    (sprintf "%A" (decisions |> List.map (fun d -> d.Rule + ":" + d.Subject)))
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_fold"
                check "streamed fold: block-wise emission, no whole-array buffer"
                    (cppCode.Contains "STREAMED fold" && not (cppCode.Contains "A_flat = new") && not (cppCode.Contains "Array<double, 1> A = ")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, "zarr_segments_fold.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          let has (line: string) = runOut.Contains line
                          check "streamed fold: builtin (+) = 55" (has "total = 55.0") runOut
                          check "streamed fold: lambda max = 10" (has "biggest = 10.0") runOut
                          check "streamed fold: seeded (+) = 155" (has "shifted = 155.0") runOut
                          check "streamed fold: segments(A) off the annotation" (has "sizes = [4, 4, 2]") runOut
                      | Ok (code, out) -> check "streamed fold: runs" false ($"exit {code}: {out}")
                      | Error e -> check "streamed fold: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP streamed fold (compile skipped): %s" e
                     else check "streamed fold: compiles" false e)
            | Error e ->
                Blade.Effects.Decisions.drain () |> ignore
                check "streamed fold: lowers" false e
        with ex -> check "streamed fold" false ex.Message
    streamedFoldE2E ()

    // S2 / D01: one rank-1 reader -> a NAMED shared affine producer ->
    // independently seeded sum and sumsq. Route assertions are required:
    // a successful materialized map is not evidence for this bridge.
    let streamedJoinedScalarsE2E () =
        printfn "\n--- rank1 Zarr block consumer: shared producer and seeded scalar join ---"
        let knobs = [ "BLADE_FP_REPRO"; "BLADE_FP_REASSOC"; "BLADE_FP_CONTRACT"; "BLADE_PRINT"; "BLADE_TILE_CACHE" ]
        let saved = knobs |> List.map (fun k -> k, Environment.GetEnvironmentVariable k)
        let marker = "rank1-zarr-joined-scalars: bounded input"
        let program store mode map extra =
            String.concat "\n"
                [ "import zarr as z"
                  sprintf "let sample = z.load(\"%s\")" store
                  sprintf "let A = sample.vars.A |> z.%s" mode
                  sprintf "let y = method_for(A) <@> lambda(x) -> %s" map
                  extra
                  "let total, squares = object_for(<&!>) <@> (reduce(y, (+), 7.0), reduce(method_for(y) <@> lambda(v) -> v * v, (+), 3.0))"
                  "" ]
        let normal (s: string) =
            s.Replace("\r\n", "\n").Split('\n')
            |> Array.filter (fun l -> l.StartsWith "total = " || l.StartsWith "squares = ")
            |> String.concat "\n"
        let emit label src =
            match lower src with
            | Error e -> check ($"{label}: lowers") false e; None
            | Ok ir ->
                try
                    let cpp, _ = CodeGen.genSelfContainedProgramFromIR ir label
                    Some (ir, cpp)
                with ex -> check ($"{label}: emits") false ex.Message; None
        let run (label: string) (cpp: string) =
            CodeGen.deployRuntimeHeaders e2eDir
            let file = Path.Combine(e2eDir, label + ".cpp")
            File.WriteAllText(file, cpp)
            match compileCpp file e2eDir with
            | Error e -> Error e
            | Ok exe -> runExecutable exe
        let fixture label version n bad =
            let store = Path.GetFullPath(Path.Combine(scratch, "stream_terminal_" + label)).Replace('\\', '/')
            let data = Array.init n (fun i -> if bad && (i = 0 || i = n - 1) then nan else float (i + 1))
            let vars : ZarrWrite.WriteVar list =
                [ { Name = "A"; DimNames = Some [ "x" ]; Shape = [ int64 n ]; Chunks = [ 4L ]
                    FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 data; OmitChunks = []; Blade = None } ]
            (if version = 2 then ZarrWrite.writeStoreV2 else ZarrWrite.writeStoreV3) store vars
            store
        try
            Environment.SetEnvironmentVariable("BLADE_FP_REPRO", "1")
            Environment.SetEnvironmentVariable("BLADE_FP_REASSOC", null)
            Environment.SetEnvironmentVariable("BLADE_FP_CONTRACT", "off")
            Environment.SetEnvironmentVariable("BLADE_PRINT", "total,squares")
            Environment.SetEnvironmentVariable("BLADE_TILE_CACHE", null)
            for version, n in [ 2, 0; 2, 11; 3, 11; 2, 257 ] do
                let label = $"zarr_join_v{version}_n{n}"
                let store = fixture label version n false
                let mk mode = program store mode "x * 2.0 + 1.0" ""
                match emit (label + "_stream") (mk "stream"), emit (label + "_read") (mk "read") with
                | Some (ir, streamCpp), Some (_, readCpp) ->
                    check ($"{label}: bounded scalar route actually emits")
                        (streamCpp.Contains marker && streamCpp.Contains "std::vector<double>"
                         && not (streamCpp.Contains "elementwise over segments")
                         && not (streamCpp.Contains "A_flat = new")
                         && not (streamCpp.Contains "allocate<typename promote<double, 1>::type")
                         && not (streamCpp.Contains "_run_offsets")
                         && not (readCpp.Contains marker) && readCpp.Contains "Read A from zarr store") ""
                    check ($"{label}: missing validation precedes joined scalar publication")
                        (streamCpp.IndexOf("_miss.count)", StringComparison.Ordinal) >= 0
                         && streamCpp.IndexOf("_miss.count)", StringComparison.Ordinal)
                            < streamCpp.LastIndexOf("std::make_tuple(", StringComparison.Ordinal)) ""
                    // The provider stream is explicitly unsupported in this lane.
                    if version = 2 && n = 11 then
                        // Provenance belongs to the private whole-program cores,
                        // never to state left behind by a previous assembly.
                        let savedTiming = Blade.CodeGenState.splitTimingModeEnabled ()
                        try
                            CodeGen.setSplitTimingMode false
                            let normalCpp, _ = CodeGen.genSelfContainedProgramFromIR ir (label + "_normal_census")
                            check "rank1 scalar stream: normal whole-program assembly admits the route"
                                (normalCpp.Contains marker) ""
                            match ir.Modules |> List.tryFind (fun m -> m.ProviderReads |> Map.exists (fun _ r -> r.Streamed)) with
                            | None -> check "rank1 scalar stream: low-level provenance fixture has a streamed module" false "no source module"
                            | Some modul ->
                                let probe api generate =
                                    let builder = IRBuilder()
                                    builder.EnsureAtLeast(0x40000000)
                                    let cpp = generate modul builder |> String.concat "\n"
                                    let refusals = CodeGen.takeCodegenRefusalDiagnostics cpp |> List.map string |> String.concat " | "
                                    check ($"rank1 scalar stream: direct {api} refuses without whole-program evidence after normal assembly")
                                        (not (cpp.Contains marker)
                                         && refusals.Contains "whole-module demand facts are unavailable") refusals
                                probe "genModule" (fun m b ->
                                    let funcs, bindings = CodeGen.genModule m b
                                    funcs @ bindings)
                                probe "genModuleSplit" (fun m b ->
                                    let funcs, setup, compute = CodeGen.genModuleSplit m b
                                    funcs @ setup @ compute)
                            CodeGen.setSplitTimingMode true
                            let splitCpp, _ = CodeGen.genSelfContainedProgramFromIR ir (label + "_split_census")
                            check "rank1 scalar stream: split-timing whole-program assembly admits the route"
                                (splitCpp.Contains marker && splitCpp.Contains "input allocation took") ""
                        finally
                            CodeGen.setSplitTimingMode savedTiming
                        let interp = Blade.Interp.Run.runProgram ir label Blade.Interp.Value.defaultLimits
                        check "rank1 scalar stream: interpreter refuses explicitly"
                            (interp.ExitCode <> 0 && interp.Stderr.Contains "streamed provider read") interp.Stderr
                        match Blade.EmitLlvm.tryEmitProgramNamed label ir with
                        | Error why -> check "rank1 scalar stream: LLVM refuses provider I/O explicitly" (why.Contains "provider read") why
                        | Ok _ -> check "rank1 scalar stream: LLVM refuses provider I/O explicitly" false "unexpected success"
                    if not zCaps.HasGpp then printfn "  SKIP %s: g++ not found" label
                    else
                        match run (label + "_read") readCpp, run (label + "_stream") streamCpp with
                        | Ok (0, expected), Ok (0, actual) ->
                            let nf = float n
                            let sum = nf * (nf + 2.0) + 7.0
                            let squares = nf * (4.0 * nf * nf + 12.0 * nf + 11.0) / 3.0 + 3.0
                            let known = sprintf "total = %.1f\nsquares = %.1f" sum squares
                            check ($"{label}: exact .read reference with identical print selection")
                                (normal expected = normal actual && normal actual <> "") ($"read: {expected}; stream: {actual}")
                            check ($"{label}: independent sum/sumsq including both seeds")
                                (normal actual = known) ($"expected {known}; actual {actual}")
                        | Error e, _ -> baselineFailed label e
                        | _, Error e -> check ($"{label}: streamed build/run") false e
                        | a, b -> check ($"{label}: both runs succeed") false (sprintf "%A / %A" a b)
                | _ -> ()
            let store = fixture "refusals" 2 11 false
            let refusal label residentFallback src =
                match emit ("zarr_join_refuse_" + label) src with
                | Some (_, cpp) ->
                    let errors = CodeGen.takeCodegenRefusalDiagnostics cpp |> List.map string |> String.concat " | "
                    check ($"rank1 scalar join refuses {label} before the block route")
                        (not (cpp.Contains marker)
                         && (errors.Contains "rank-1 streamed reduction join"
                             || (residentFallback && cpp.Contains "elementwise over segments"))) errors
                | None -> ()
            refusal "fallible map" false (program store "stream" "Float64(Int64(floor(x)))" "")
            refusal "indexed source" false (program store "stream" "x * 2.0 + 1.0" "let observed = A((0 : sample.index.x))")
            refusal "required producer array" true (program store "stream" "x * 2.0 + 1.0" "let observed = y |> compute")
            refusal "second provider read" false (program store "stream" "x * 2.0 + 1.0" "let B = sample.vars.A |> z.stream")
            refusal "captured source" false (program store "stream" "x * 2.0 + 1.0" "function leak() -> Float64 = reduce(A, (+))")
            refusal "mutable array state" false (program store "stream" "x * 2.0 + 1.0" "let mut state = [0.0]")
            Environment.SetEnvironmentVariable("BLADE_PRINT", "y,total,squares")
            refusal "explicit producer print" false (program store "stream" "x * 2.0 + 1.0" "")
            Environment.SetEnvironmentVariable("BLADE_PRINT", "total,squares")
            Environment.SetEnvironmentVariable("BLADE_FP_REPRO", "0")
            refusal "licensed fastest fold tree" false (program store "stream" "x * 2.0 + 1.0" "")
            Environment.SetEnvironmentVariable("BLADE_FP_REPRO", "1")
            // A bad first block plus a bad last block reports ONE whole-read
            // count, without publishing scalars. Corrupting the last chunk
            // afterward makes its I/O error take precedence in BOTH routes.
            for shortTail in [ false; true ] do
                let label = if shortTail then "zarr_join_missing_then_short" else "zarr_join_missing_tally"
                let store = fixture label 2 11 true
                let mk mode = program store mode "x * 2.0 + 1.0" ""
                match emit (label + "_stream") (mk "stream"), emit (label + "_read") (mk "read") with
                | Some (_, streamCpp), Some (_, readCpp) ->
                    check ($"{label}: failure witness uses the block route") (streamCpp.Contains marker) ""
                    if shortTail then File.WriteAllBytes(Path.Combine(store, "A", "2"), [| 0uy |])
                    if not zCaps.HasGpp then printfn "  SKIP %s: g++ not found" label
                    else
                        for mode, cpp in [ "stream", streamCpp; "read", readCpp ] do
                            match run (label + "_" + mode) cpp with
                            | Ok (code, output) ->
                                let expected =
                                    if shortTail then output.Contains "is short (expected 32 bytes)" && not (output.Contains "error[BL8015]")
                                    else output.Contains "2 cell(s) hold a missing value" && output.Contains "first at [0]"
                                check ($"{label}: {mode} preserves whole-read failure order")
                                    (code <> 0 && expected && normal output = "") output
                            | Error e -> check ($"{label}: {mode} builds/runs") false e
                | _ -> ()
        with ex -> check "rank1 streamed joined scalars" false ex.Message
        for key, value in saved do Environment.SetEnvironmentVariable(key, value)
    streamedJoinedScalarsE2E ()

    // PM43 / D02: the output itself is REQUIRED and printed. Only its duplicate
    // flat assembly/copy may disappear. The unprivileged provider reader is
    // the exact old implementation and supplies a materialized reference.
    let residentDestinationE2E () =
        printfn "\n--- rank1 Zarr resident destination: one final pool, no flat copy ---"
        let marker = "zarr-rank1-direct-destination: required resident output"
        let savedPrint = Environment.GetEnvironmentVariable "BLADE_PRINT"
        let savedTile = Environment.GetEnvironmentVariable "BLADE_TILE_CACHE"
        let source store = String.concat "\n" [ "import zarr as z"; sprintf "let s = z.load(\"%s\")" store; "let A = s.vars.A |> z.read"; "" ]
        let fixture label version payload shape chunks fill omit =
            let store = Path.GetFullPath(Path.Combine(scratch, "resident_destination_" + label)).Replace('\\', '/')
            let vars : ZarrWrite.WriteVar list =
                [ { Name = "A"; DimNames = Some (if List.length shape = 1 then [ "x" ] else [ "x"; "y" ])
                    Shape = shape; Chunks = chunks; FillValue = fill; Data = payload; OmitChunks = omit; Blade = None } ]
            (if version = 2 then ZarrWrite.writeStoreV2 else ZarrWrite.writeStoreV3) store vars
            store
        let run (label: string) (cpp: string) =
            CodeGen.deployRuntimeHeaders e2eDir
            let file = Path.Combine(e2eDir, label + ".cpp")
            File.WriteAllText(file, cpp)
            match compileCpp file e2eDir with Error e -> Error e | Ok exe -> runExecutable exe
        let printed (output: string) =
            output.Replace("\r\n", "\n").Split('\n')
            |> Array.filter (fun line -> line.StartsWith "A = ") |> String.concat "\n"
        let emitPair label store =
            match lower (source store) with
            | Error e -> check ($"{label}: lowers") false e; None
            | Ok ir ->
                let cpp, _ = CodeGen.genSelfContainedProgramFromIR ir label
                let specId, spec = ir.Modules |> List.collect (fun m -> Map.toList m.ProviderReads) |> List.exactlyOne
                let binding = ir.Modules |> List.collect (_.Bindings) |> List.find (fun b -> b.Id = specId)
                let ctx = { Blade.CodeGenState.emptyContext () with ProviderReads = Map.ofList [ specId, spec ] }
                let builder = IRBuilder()
                builder.EnsureAtLeast(0x40000000)
                let legacyLines, returned = Blade.CodeGenBinding.genBinding ctx binding builder
                let legacy = String.concat "\n" legacyLines
                check ($"{label}: default binding context has no top-level destination authority")
                    (not (legacy.Contains marker) && legacy.Contains "A_flat = new" && returned.TopLevelProviderRead.IsNone) legacy
                match CppZarr.tryGenReadVarIntoFinal spec "A" with
                | None -> check ($"{label}: rank1 destination capability") false "declined fixture"; None
                | Some directLines ->
                    let indented lines = lines |> List.map (fun line -> "    " + line) |> String.concat "\n"
                    let directBlock = indented directLines
                    // Replace exactly the provider reader span with the unchanged
                    // generic reader. Every printer, demanded output and wrapper
                    // stays identical, and a missing/duplicated span is a failure.
                    let copies = cpp.Split([| directBlock |], StringSplitOptions.None).Length - 1
                    check ($"{label}: direct reader span occurs exactly once") (copies = 1) ($"found {copies}")
                    let baseline = cpp.Replace(directBlock, indented legacyLines)
                    let pending = "A__resident_pending = allocate<typename promote<"
                    let publication = cpp.IndexOf("A = { A__resident_pending, A_extents }", StringComparison.Ordinal)
                    let metadata = cpp.IndexOf("std::ifstream A_zm", StringComparison.Ordinal)
                    let allocation = cpp.IndexOf(pending, StringComparison.Ordinal)
                    check ($"{label}: actual final-allocator route has no full flat allocation/copy")
                        (cpp.Contains marker && allocation >= 0 && publication > allocation
                         && metadata >= 0 && metadata < allocation
                         && not (cpp.Contains "A_flat") && not (cpp.Contains "A_i0")
                         && cpp.Contains "delete[] A_cbuf;"
                         && baseline.Contains "A_flat = new" && not (baseline.Contains marker)) ""
                    if spec.VarType.ElemType = IRTScalar ETFloat32 || spec.VarType.ElemType = IRTScalar ETFloat64 then
                        check ($"{label}: whole-read validation completes before final Array publication")
                            (cpp.IndexOf("delete[] A_cbuf;", StringComparison.Ordinal) < cpp.IndexOf("if (A_miss.count)", StringComparison.Ordinal)
                             && cpp.IndexOf("if (A_miss.count)", StringComparison.Ordinal) < publication) ""
                    // Even an explicitly different authorized ID cannot leak
                    // authority into this local binding, regardless of its indent.
                    let wrongOwner = { ctx with TopLevelProviderRead = Some (specId + 1); Indent = 0 }
                    let denied, _ = Blade.CodeGenBinding.genBinding wrongOwner binding builder
                    check ($"{label}: another binding's capability cannot authorize a local read")
                        ((denied |> List.exists (fun line -> line.Contains "A_flat = new"))
                         && not (denied |> List.exists (fun line -> line.Contains marker))) ""
                    let savedMpi = Blade.CodeGenState.mpiProgramOn ()
                    try
                        Blade.CodeGenState.setMpiProgramOn true
                        let denied, _ = Blade.CodeGenBinding.genBinding { ctx with TopLevelProviderRead = Some specId } binding builder
                        check ($"{label}: MPI retains the generic resident reader")
                            ((denied |> List.exists (fun line -> line.Contains "A_flat = new"))
                             && not (denied |> List.exists (fun line -> line.Contains marker))) ""
                    finally Blade.CodeGenState.setMpiProgramOn savedMpi
                    for why, unsupported in
                        [ "stream", { spec with Streamed = true }
                          "masked raw", { spec with AllowMissing = true }
                          "window", { spec with Window = Some (0L, 1L) }
                          "other provider", { spec with Provider = "netcdf" }
                          "rank two", { spec with VarType = { spec.VarType with IndexTypes = spec.VarType.IndexTypes @ spec.VarType.IndexTypes } }
                          "mismatched shape", { spec with VarType = { spec.VarType with IndexTypes = spec.VarType.IndexTypes |> List.map (fun ix -> { ix with Extent = IRLit (IRLitInt Int64.MaxValue) }) } } ] do
                        check ($"{label}: destination capability falls back for {why}")
                            (CppZarr.tryGenReadVarIntoFinal unsupported "A").IsNone ""
                    Some (cpp, baseline, spec)
        try
            try
                Environment.SetEnvironmentVariable("BLADE_PRINT", "A")
                Environment.SetEnvironmentVariable("BLADE_TILE_CACHE", null)
                let values = [| for i in 1 .. 11 -> float i |]
                let floatExpected = "A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0]"
                let cases =
                    [ "f64_v2_odd", 2, ZarrWrite.WF64 values, 11L, FillFloat 0.0, [], floatExpected
                      "f64_v3_padding_nan", 3, ZarrWrite.WF64 values, 11L, FillFloat nan, [], floatExpected
                      "f32_v2_odd", 2, ZarrWrite.WF32 (values |> Array.map float32), 11L, FillFloat 0.0, [], floatExpected
                      "i4_v3_widen", 3, ZarrWrite.WI32 [| -5 .. 5 |], 11L, FillInt 0L, [], "A = [-5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5]"
                      "empty", 2, ZarrWrite.WF64 [||], 0L, FillFloat 0.0, [], "A = []"
                      "finite_fill", 2, ZarrWrite.WF64 values, 11L, FillFloat -2.0, [ [ 1L ] ], "A = [1.0, 2.0, 3.0, 4.0, -2.0, -2.0, -2.0, -2.0, 9.0, 10.0, 11.0]" ]
                for tag, version, payload, n, fill, omit, expected in cases do
                    let label = "zarr_resident_" + tag
                    let store = fixture tag version payload [ n ] [ 4L ] fill omit
                    match emitPair label store with
                    | None -> ()
                    | Some (cpp, baseline, spec) ->
                        if not zCaps.HasGpp then printfn "  SKIP %s: g++ not found" label
                        else
                            match run (label + "_reference") baseline, run label cpp with
                            | Ok (0, reference), Ok (0, actual) ->
                                check ($"{label}: exact unchanged-reader reference and independently known printed A")
                                    (printed actual = expected && printed reference = expected) ($"reference: {reference}; actual: {actual}")
                            | Error e, _ -> baselineFailed label e
                            | _, Error e -> check ($"{label}: direct reader builds/runs") false e
                            | a, b -> check ($"{label}: both readers succeed") false (sprintf "%A / %A" a b)
                        if tag = "f64_v2_odd" then
                            // Metadata-only oversized padded chunk: do not allocate
                            // its bytes, and do not call the unchecked legacy core.
                            let hugeStore = Path.Combine(scratch, "resident_destination_oversized")
                            Directory.CreateDirectory(Path.Combine(hugeStore, "A")) |> ignore
                            File.WriteAllText(Path.Combine(hugeStore, ".zgroup"), "{\"zarr_format\":2}")
                            File.WriteAllText(Path.Combine(hugeStore, "A", ".zarray"), "{\"shape\":[11],\"chunks\":[2147483647],\"dtype\":\"<f8\",\"compressor\":null,\"fill_value\":0,\"order\":\"C\",\"filters\":null}")
                            check "resident destination: oversized padded chunk declines before Int32 multiplication"
                                (CppZarr.tryGenReadVarIntoFinal { spec with FilePath = hugeStore } "A").IsNone ""
                // Same source-data failure order as the original owned-flat
                // assembly, including errors after an earlier nonfinite cell.
                for failure in [ "missing_tally"; "later_short"; "later_null_chunk" ] do
                    let data = [| for i in 0 .. 10 -> if i = 0 || i = 10 then nan else float (i + 1) |]
                    let fill, omit = if failure = "later_null_chunk" then FillNone, [ [ 2L ] ] else FillFloat 0.0, []
                    let label = "zarr_resident_" + failure
                    let store = fixture failure 2 (ZarrWrite.WF64 data) [ 11L ] [ 4L ] fill omit
                    match emitPair label store with
                    | None -> ()
                    | Some (cpp, baseline, _) ->
                        if failure = "later_short" then File.WriteAllBytes(Path.Combine(store, "A", "2"), [| 0uy |])
                        if not zCaps.HasGpp then printfn "  SKIP %s: g++ not found" label
                        else
                            for lane, text in [ "direct", cpp; "reference", baseline ] do
                                match run (label + "_" + lane) text with
                                | Ok (code, output) ->
                                    let diagnostic =
                                        match failure with
                                        | "later_short" -> output.Contains "is short (expected 32 bytes)" && not (output.Contains "error[BL8015]")
                                        | "later_null_chunk" -> output.Contains "is missing and fill_value is null" && not (output.Contains "error[BL8015]")
                                        | _ -> output.Contains "2 cell(s) hold a missing value" && output.Contains "first at [0]"
                                    check ($"{label}: {lane} preserves diagnostic priority and publishes no Array")
                                        (code <> 0 && diagnostic && printed output = "") output
                                | Error e -> check ($"{label}: {lane} builds/runs") false e
                let rank2 = fixture "rank2_control" 2 (ZarrWrite.WF64 [| for i in 1 .. 15 -> float i |]) [ 3L; 5L ] [ 2L; 3L ] (FillFloat 0.0) []
                match lower (source rank2) with
                | Error e -> check "resident destination: rank2 fallback lowers" false e
                | Ok ir ->
                    let cpp, _ = CodeGen.genSelfContainedProgramFromIR ir "zarr_resident_rank2_control"
                    check "resident destination: rank2 keeps original owned-flat assembly and copy"
                        (not (cpp.Contains marker) && cpp.Contains "A_flat = new" && cpp.Contains "delete[] A_flat;") ""
            with ex -> check "rank1 Zarr resident destination" false ex.Message
        finally
            Environment.SetEnvironmentVariable("BLADE_PRINT", savedPrint)
            Environment.SetEnvironmentVariable("BLADE_TILE_CACHE", savedTile)
    residentDestinationE2E ()

    // ---------------------------------------------------------------
    // 10f'. Sibling folds over a STREAMED variable are not joined
    // (Optimize.shareSiblingFoldsModule): a `.stream` read has an ordinary
    // dense array type but no storage a reduction join could share (each fold
    // walks the store block by block), and a provider read is not a pure
    // value. Two folds of it in one binding block keep their own streamed
    // fold route; the sibling-fold record says why.
    // ---------------------------------------------------------------
    printfn "\n--- streamed fold siblings: not joined ---"
    let streamedSiblingsE2E () =
        let segStore = fixStore "zarr_segments"    // 10 cells 1..10, chunked at 4
        let src = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
let A: Array<Float like CX> = sample.vars.A |> z.stream
let (p, q) = {
    let s = reduce(A, (+))
    let t = reduce(A, (+), 1.0)
    (s, t)
}
"""
                            segStore
        let prior = System.Environment.GetEnvironmentVariable "BLADE_FP_REPRO"
        try
            // Repro, so the decline cannot come from a licence disagreement.
            System.Environment.SetEnvironmentVariable("BLADE_FP_REPRO", "1")
            Blade.Effects.Decisions.start ()
            match lower src with
            | Ok ir ->
                let decisions = Blade.Effects.Decisions.drain ()
                let sib = decisions |> List.filter (fun d -> d.Rule = "sibling-folds")
                check "streamed siblings: sibling-folds declines on the streamed operand"
                    (sib |> List.exists (fun d ->
                        match d.Outcome with
                        | Blade.Effects.Declined why -> why.Contains "streamed provider read" || d.Evidence |> List.exists (fun e -> e.Contains "streamed provider read")
                        | _ -> false)
                     && not (sib |> List.exists (fun d -> d.Outcome = Blade.Effects.Applied)))
                    (sprintf "%A" (sib |> List.map Blade.Effects.Decisions.render))
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_streamed_siblings"
                check "streamed siblings: each fold keeps its streamed route, no join"
                    (cppCode.Contains "STREAMED fold" && not (cppCode.Contains "reduction join")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, "zarr_streamed_siblings.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          check "streamed siblings: p = 55, q = 56" (runOut.Contains "p = 55.0" && runOut.Contains "q = 56.0") runOut
                      | Ok (code, out) -> check "streamed siblings: runs" false ($"exit {code}: {out}")
                      | Error e -> check "streamed siblings: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP streamed siblings (compile skipped): %s" e
                     else check "streamed siblings: compiles" false e)
            | Error e ->
                Blade.Effects.Decisions.drain () |> ignore
                check "streamed siblings: lowers" false e
        with ex -> check "streamed siblings" false ex.Message
        System.Environment.SetEnvironmentVariable("BLADE_FP_REPRO", prior)
    streamedSiblingsE2E ()

    // ---------------------------------------------------------------
    // 10g. STENCIL OVER SEGMENTS (docs/plans/structural/07 §2.3): a halo map
    // over a streamed rank-1 variable on a `Chunked` axis runs one segment at
    // a time, reading each run plus the ghost cells its reach demands. The
    // values are the flat stencil's; the emission has no whole-array buffer.
    // ---------------------------------------------------------------
    printfn "\n--- stencil over segments: halo<CX, ..> over s.vars.A |> z.stream ---"
    let stencilSegmentsE2E () =
        let segStore = fixStore "zarr_segments"    // 10 cells 1..10, chunked at 4
        let src = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
let A: Array<Float like CX> = sample.vars.A |> z.stream
let d = method_for(halo<CX, [-1, 0, 1]>) <@> lambda(w) -> A(w(1)) - A(w(-1)) |> compute
let d2 = method_for(halo<CX, [-2, 0, 2]>) <@> lambda(w) -> A(w(2)) * A(w(-2)) - A(w(0)) * A(w(0)) |> compute
"""
                            segStore
        try
            Blade.Effects.Decisions.start ()
            match lower src with
            | Ok ir ->
                let decisions = Blade.Effects.Decisions.drain ()
                check "stencil over segments: `blade plan` records the decision"
                    (decisions |> List.exists (fun d -> d.Rule = "segment-streaming" && d.Outcome = Blade.Effects.Applied && d.Evidence |> List.exists (fun e -> e.Contains "stencil")))
                    (sprintf "%A" (decisions |> List.map (fun d -> d.Rule + ":" + d.Subject)))
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_stencil"
                check "stencil over segments: per-run emission, no whole-array buffer"
                    (cppCode.Contains "stencil over segments" && not (cppCode.Contains "A_flat = new")) ""
                check "stencil over segments: actual local loads replace every rank1 alias"
                    (cppCode.Contains "d__win0[static_cast<size_t>"
                     && cppCode.Contains "d2__win0[static_cast<size_t>"
                     && not (cppCode.Contains " - __w_lo,")
                     && not (cppCode.Contains "Array<double, 1> A =")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, "zarr_segments_stencil.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          let has (line: string) = runOut.Contains line
                          check "stencil over segments: central difference" (has "d = [2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0]") runOut
                          check "stencil over segments: reach-2 kernel" (has "d2 = [-4.0, -4.0, -4.0, -4.0, -4.0, -4.0]") runOut
                      | Ok (code, out) -> check "stencil over segments: runs" false ($"exit {code}: {out}")
                      | Error e -> check "stencil over segments: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP stencil over segments (compile skipped): %s" e
                     else check "stencil over segments: compiles" false e)
            | Error e ->
                Blade.Effects.Decisions.drain () |> ignore
                check "stencil over segments: lowers" false e
        with ex -> check "stencil over segments" false ex.Message
    stencilSegmentsE2E ()

    // ---------------------------------------------------------------
    // 10h. ELEMENTWISE consumers of a streamed source. StreamingIONotes v1
    // refused these outright ("elementwise consumption is not stream-
    // eligible") because its only in-nest read was a whole trailing fiber
    // at a site; the segment run loop (10g) with zero reach is the missing
    // mechanism: a map, a scalar-broadcast binop and a zip with a
    // materialized array each run one block of the store's chunk edge at a
    // time, the source never materialized.
    // ---------------------------------------------------------------
    printfn "
--- elementwise over a streamed source: map, A + 1.0, zip(A, M) ---"
    let elementwiseStreamE2E () =
        let segStore = fixStore "zarr_segments"    // 10 cells 1..10, chunked at 4
        let src = sprintf """
import zarr as z

let sample = z.load("%s")
type CX = Chunked<sample.index.x, store>
let A: Array<Float like CX> = sample.vars.A |> z.stream
let doubled = method_for(A) <@> lambda(x) -> x * 2.0 |> compute
let shifted = A + 1.0
let m: Array<Float like CX> = [0.5, 0.5, 0.5, 0.5, 0.5, 2.0, 2.0, 2.0, 2.0, 2.0]
let scaled = method_for(zip(A, m)) <@> lambda(x, w) -> x * w |> compute
"""
                            segStore
        try
            Blade.Effects.Decisions.start ()
            match lower src with
            | Ok ir ->
                let decisions = Blade.Effects.Decisions.drain ()
                check "elementwise stream: `blade plan` records three elementwise decisions"
                    ((decisions |> List.filter (fun d -> d.Rule = "segment-streaming" && d.Evidence |> List.exists (fun e -> e.Contains "elementwise")) |> List.length) = 3)
                    (sprintf "%A" (decisions |> List.map (fun d -> d.Subject + ":" + (String.concat "|" d.Evidence).Substring(0, 30))))
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_elementwise"
                check "elementwise stream: per-block emission, no whole-array buffer"
                    (cppCode.Contains "elementwise over segments" && not (cppCode.Contains "A_flat = new")) ""
                check "elementwise stream: scalar peels use local windows, resident zip stays resident"
                    (cppCode.Contains "doubled__win0[static_cast<size_t>"
                     && cppCode.Contains "shifted__win0[static_cast<size_t>"
                     && cppCode.Contains "scaled__win0[static_cast<size_t>"
                     && not (cppCode.Contains " - __w_lo,")
                     && not (cppCode.Contains "Array<double, 1> A =")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, "zarr_segments_elementwise.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          let has (line: string) = runOut.Contains line
                          check "elementwise stream: map" (has "doubled = [2.0, 4.0, 6.0, 8.0, 10.0, 12.0, 14.0, 16.0, 18.0, 20.0]") runOut
                          check "elementwise stream: scalar broadcast" (has "shifted = [2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0]") runOut
                          check "elementwise stream: zip with a materialized array" (has "scaled = [0.5, 1.0, 1.5, 2.0, 2.5, 12.0, 14.0, 16.0, 18.0, 20.0]") runOut
                      | Ok (code, out) -> check "elementwise stream: runs" false ($"exit {code}: {out}")
                      | Error e -> check "elementwise stream: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP elementwise stream (compile skipped): %s" e
                     else check "elementwise stream: compiles" false e)
            | Error e ->
                Blade.Effects.Decisions.drain () |> ignore
                check "elementwise stream: lowers" false e
        with ex -> check "elementwise stream" false ex.Message
    elementwiseStreamE2E ()

    // D04: local addressing is a safety prerequisite, independent of caching.
    // These fixtures pin values without compiling the former invalid alias.
    let rank1WindowAddressingE2E () =
        printfn "\n--- rank1 streamed windows: local addresses, global coordinates ---"
        let savedPrint = Environment.GetEnvironmentVariable "BLADE_PRINT"
        let savedTile = Environment.GetEnvironmentVariable "BLADE_TILE_CACHE"
        let fixture label version payload n fill omit =
            let store = Path.GetFullPath(Path.Combine(scratch, "window_address_" + label)).Replace('\\', '/')
            let vars : ZarrWrite.WriteVar list =
                [ { Name = "A"; DimNames = Some ["x"]; Shape = [n]; Chunks = [4L]
                    FillValue = fill; Data = payload; OmitChunks = omit; Blade = None } ]
            (if version = 2 then ZarrWrite.writeStoreV2 else ZarrWrite.writeStoreV3) store vars
            store
        let source (store: string) (annotation: string) (extra: string) : string =
            let lines : string list =
                [ "import zarr as z";
                  sprintf "let s = z.load(\"%s\")" store;
                  "type CX = Chunked<s.index.x, store>";
                  $"let A: Array<{annotation} like CX> = s.vars.A |> z.stream";
                  "let d = method_for(halo<CX, [-1, 0, 1]>) <@> lambda(w) -> { let hi = A(w(1)); hi - A(w(-1)) } |> compute";
                  extra; "" ]
            String.concat "\n" lines
        let run (label: string) (cpp: string) =
            CodeGen.deployRuntimeHeaders e2eDir
            let file = Path.Combine(e2eDir, label + ".cpp")
            File.WriteAllText(file, cpp)
            match compileCpp file e2eDir with Error e -> Error e | Ok exe -> runExecutable exe
        let emit (label: string) (src: string) =
            match lower src with
            | Error e -> check ($"{label}: lowers") false e; None
            | Ok ir ->
                let cpp, _ = CodeGen.genSelfContainedProgramFromIR ir label
                Some (ir, cpp)
        let noUnsafeAlias (cpp: string) =
            not (cpp.Contains " - __w_lo,") && not (cpp.Contains "Array<double, 1> A =")
            && not (cpp.Contains "Array<float, 1> A =") && not (cpp.Contains "Array<int64_t, 1> A =")
        try
            try
                Environment.SetEnvironmentVariable("BLADE_TILE_CACHE", null)
                let dtypeCases : (string * string * ZarrWrite.WritePayload) list =
                    [ ("f64", "Float", ZarrWrite.WF64 [| for j in 1 .. 11 -> float (j * j) |]);
                      ("f32", "Float32", ZarrWrite.WF32 [| for j in 1 .. 11 -> float32 (j * j) |]);
                      ("i4", "Int64", ZarrWrite.WI32 [| for j in 1 .. 11 -> j * j |]) ]
                for dtype, annotation, payload in dtypeCases do
                    for version in [2; 3] do
                        let label = $"zarr_window_{dtype}_v{version}"
                        let store = fixture label version payload 11L (if dtype = "i4" then FillInt 0L else FillFloat nan) []
                        let extra =
                            if dtype = "f64" then
                                "let coords = method_for(halo<CX, [-1, 0, 1]>) <@> lambda(w) -> Float64(w(0)) + A(w(0)) |> compute"
                            else ""
                        Environment.SetEnvironmentVariable("BLADE_PRINT", if extra = "" then "d" else "d,coords")
                        match emit label (source store annotation extra) with
                        | None -> ()
                        | Some (_, cpp) ->
                            check ($"{label}: actual local load, final output and unchanged read owner")
                                (noUnsafeAlias cpp && cpp.Contains "d__win0[static_cast<size_t>"
                                 && cpp.Contains "static_cast<int64_t>(__w_lo)"
                                 && cpp.Contains "uniform-zarr-halo-cursor" && not (cpp.Contains "d__runs[")
                                 && cpp.Contains "delete[] d__win0;"
                                 && cpp.Contains "d = { allocate<typename promote<"
                                 && not (cpp.Contains "#error") && not (cpp.Contains "BLADE_CODEGEN_ERROR")
                                 && not (cpp.Contains "A_flat = new")) ""
                            if not zCaps.HasGpp then printfn "  SKIP %s: g++ not found" label
                            else
                                match run label cpp with
                                | Ok (0, output) ->
                                    let expected =
                                        if dtype = "i4" then "d = [8, 12, 16, 20, 24, 28, 32, 36, 40]"
                                        else "d = [8.0, 12.0, 16.0, 20.0, 24.0, 28.0, 32.0, 36.0, 40.0]"
                                    check ($"{label}: independent odd-tail values, padded NaNs unobserved") (output.Contains expected) output
                                    if extra <> "" then
                                        check ($"{label}: kernel coordinates remain global across windows")
                                            (output.Contains "coords = [5.0, 11.0, 19.0, 29.0, 41.0, 55.0, 71.0, 89.0, 109.0]") output
                                | Ok (code, output) -> check ($"{label}: runs") false ($"exit {code}: {output}")
                                | Error e -> baselineFailed label e
                // Finite omitted chunk fills are real neighbor values, not
                // invented boundary ghosts. Expected values computed by hand.
                Environment.SetEnvironmentVariable("BLADE_PRINT", "d")
                for n, expected in [0, "d = []"; 1, "d = [2.0]"] do
                    let label = $"zarr_window_empty_or_single_{n}"
                    let store = fixture label 2 (ZarrWrite.WF64 [| for j in 1 .. n -> float j |]) (int64 n) (FillFloat 0.0) []
                    let lines : string list =
                        [ "import zarr as z";
                          sprintf "let s = z.load(\"%s\")" store;
                          "let A = s.vars.A |> z.stream";
                          "let d = A + 1.0"; "" ]
                    let src = String.concat "\n" lines
                    match emit label src with
                    | Some (_, cpp) ->
                        check ($"{label}: elementwise path retains safe local peel")
                            (noUnsafeAlias cpp && cpp.Contains "d__win0[static_cast<size_t>") ""
                        if zCaps.HasGpp then
                            match run label cpp with
                            | Ok (0, output) -> check ($"{label}: independent values and empty interior") (output.Contains expected) output
                            | other -> check ($"{label}: runs") false (sprintf "%A" other)
                    | None -> ()
                let finite = fixture "finite_fill" 2 (ZarrWrite.WF64 [| 1.0 .. 10.0 |]) 10L (FillFloat -1.0) [[1L]]
                match emit "zarr_window_finite" (source finite "Float" "") with
                | Some (_, cpp) when zCaps.HasGpp ->
                    match run "zarr_window_finite" cpp with
                    | Ok (0, output) -> check "rank1 local windows: finite missing-chunk fill" (output.Contains "d = [2.0, 2.0, -4.0, -5.0, 0.0, 0.0, 10.0, 11.0]") output
                    | other -> check "rank1 local windows: finite fill runs" false (sprintf "%A" other)
                | _ -> ()
                // Same-window source-data failure wins over an earlier NaN;
                // future-window failure must not be touched before its turn.
                let failureCases : (string * int * int * bool) list =
                    [ ("future_short", 2, 0, false); ("same_short", 1, 0, false);
                      ("future_null", 2, 0, true); ("same_null", 1, 0, true);
                      ("global_missing", -1, 6, false) ]
                for label, badChunk, missingAt, nullFill in failureCases do
                    let data = [| for j in 0 .. 10 -> if j = missingAt then nan else float (j + 1) |]
                    let store = fixture label 2 (ZarrWrite.WF64 data) 11L (if nullFill then FillNone else FillFloat 0.0) (if nullFill then [[int64 badChunk]] else [])
                    match emit ("zarr_window_" + label) (source store "Float" "") with
                    | None -> ()
                    | Some (_, cpp) ->
                        if badChunk >= 0 && not nullFill then File.WriteAllBytes(Path.Combine(store, "A", string badChunk), [|0uy|])
                        if zCaps.HasGpp then
                            match run ("zarr_window_" + label) cpp with
                            | Ok (code, output) ->
                                let expected =
                                    if label = "same_short" then output.Contains "is short (expected 32 bytes)" && not (output.Contains "error[BL8015]")
                                    elif label = "same_null" then output.Contains "is missing and fill_value is null" && not (output.Contains "error[BL8015]")
                                    else output.Contains "error[BL8015]" && output.Contains ($"first at [{missingAt}]")
                                         && not (output.Contains "is short") && not (output.Contains "fill_value is null")
                                check ($"rank1 local windows: {label} preserves per-window failure order/global location")
                                    (code <> 0 && expected && not (output.Contains "d = ")) output
                            | Error e -> baselineFailed label e
                // Mutate otherwise-positive lowered IR to isolate backend
                // refusal boundaries even when the front end rules them out.
                let store = fixture "refusal" 2 (ZarrWrite.WF64 [| 1.0 .. 11.0 |]) 11L (FillFloat 0.0) []
                let helperSource =
                    let withHelper = (source store "Float" "").Replace(
                        "let d = method_for", "function gap(a: Float, b: Float) -> Float = { a - b }\nlet d = method_for")
                    withHelper.Replace("let hi = A(w(1)); hi - A(w(-1))", "gap(A(w(1)), A(w(-1)))")
                match emit "zarr_window_scalar_helper" helperSource with
                | Some (_, cpp) ->
                    let helperCall = System.Text.RegularExpressions.Regex.IsMatch(cpp, @"gap[^\s(]*\(d__win0\[static_cast<size_t>")
                    check "rank1 local windows: capture-free named scalar call renders certified loads"
                        (helperCall && noUnsafeAlias cpp && not (cpp.Contains "BLADE_CODEGEN_ERROR")) ""
                    if zCaps.HasGpp then
                        match run "zarr_window_scalar_helper" cpp with
                        | Ok (0, output) ->
                            check "rank1 local windows: named scalar helper independent values"
                                (output.Contains "d = [2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0]") output
                        | other -> check "rank1 local windows: named scalar helper runs" false (sprintf "%A" other)
                | None -> ()
                match lower (source store "Float" "") with
                | Error e -> check "rank1 local windows: refusal fixture lowers" false e
                | Ok ir ->
                    let transformExpr rewrite (program: IRProgram) =
                        { Modules = program.Modules |> List.map (fun m ->
                            { m with Functions = m.Functions |> List.map (fun f -> { f with Body = mapIRExpr rewrite f.Body })
                                     Bindings = m.Bindings |> List.map (fun b -> { b with Value = mapIRExpr rewrite b.Value }) })
                          DeclSpans = program.DeclSpans }
                    let rewriteRead change = function
                        | IRIndex (IRVar (aid, ty), [IRBinOp (IRElementwise, IRAdd, w, off, loc)], ident) as e
                            when (Blade.IRAccess.denseHaloTagOf w).IsSome -> change e aid ty w off loc ident
                        | e -> e
                    let refuse label program =
                        try
                            let cpp, _ = CodeGen.genSelfContainedProgramFromIR program ("zarr_window_refuse_" + label)
                            check ($"rank1 local windows: {label} refuses without runnable alias fallback") false cpp
                        with
                        | Blade.Diagnostics.BladeDiagnosticException d ->
                            check ($"rank1 local windows: {label} refuses without runnable alias fallback")
                                (d.Code = "BL7001" && d.Message.Contains "rank-one streamed window addressing" && d.Span.StartLine > 0) (string d)
                    let changed label change = refuse label (transformExpr (rewriteRead change) ir)
                    changed "computed_offset" (fun _ aid ty w _ loc ident -> IRIndex (IRVar(aid,ty), [IRBinOp(IRElementwise,IRAdd,w,w,loc)], ident))
                    changed "huge_offset" (fun _ aid ty w _ loc ident -> IRIndex (IRVar(aid,ty), [IRBinOp(IRElementwise,IRAdd,w,IRLit(IRLitInt System.Int64.MaxValue),loc)], ident))
                    changed "foreign_window" (fun _ aid ty w off loc ident ->
                        let foreign = match w with IRVar (_, t) -> IRVar (0x3ffffffe, t) | _ -> w
                        IRIndex (IRVar(aid,ty), [IRBinOp(IRElementwise,IRAdd,foreign,off,loc)], ident))
                    changed "mixed_global_read" (fun e aid ty _ _ loc _ -> IRBinOp(IRElementwise,IRAdd,e,IRIndex(IRVar(aid,ty),[IRLit(IRLitInt 0L)],None),loc))
                    changed "source_escape" (fun e aid ty _ _ _ _ -> IRLet (0x3ffffffd, IRVar(aid,ty), e))
                    changed "hidden_type_extent" (fun _ aid ty w off loc ident ->
                        let badTy = IRTIdxTagged(ty, IRefAnon(0x3ffffffc, IRVar(aid,ty)))
                        IRIndex(IRVar(aid,badTy),[IRBinOp(IRElementwise,IRAdd,w,off,loc)],ident))
                    refuse "reynolds" (transformExpr (function IRApplyCombinator a -> IRApplyCombinator {a with HasReynolds = true} | e -> e) ir)
                    let haloCallable =
                        ir.Modules |> List.collect (_.Functions) |> List.tryFind (fun f ->
                            not (Blade.IRAccess.windowReadsOf (fun w -> (Blade.IRAccess.denseHaloTagOf w).IsSome) f.Body).IsEmpty)
                    match haloCallable with
                    | None -> check "rank1 local windows: captured-callee refusal has a real lifted kernel" false "no callable"
                    | Some f ->
                        let callee = { f with Id = 0x3ffffffb; Name = "__window_captured_callee" }
                        let call = IRApp(IRVar(callee.Id, IRTUnit), f.Params |> List.map (fun p -> IRVar(p.VarId,p.Type)), f.RetType)
                        let opaque =
                            { Modules = ir.Modules |> List.map (fun m ->
                                if m.Functions |> List.exists (fun c -> c.Id = f.Id) then
                                    { m with Functions = (m.Functions |> List.map (fun c -> if c.Id = f.Id then {c with Body = call} else c)) @ [callee] }
                                else m)
                              DeclSpans = ir.DeclSpans }
                        refuse "captured_callee" opaque
                    let arraySource = (source store "Float" "").Replace("let hi = A(w(1)); hi - A(w(-1))", "[A(w(1)), A(w(-1))]")
                    match lower arraySource with
                    | Ok arrayIr -> refuse "array_result" arrayIr
                    | Error e -> check "rank1 local windows: array-result refusal fixture lowers" false e
                    let changedProvider (_: IRId) (s: ProviderReadSpec) : ProviderReadSpec =
                        let changedIndices : IRIndexType list =
                            s.VarType.IndexTypes |> List.map (fun ix -> {ix with Extent = IRLit(IRLitInt 12L)})
                        let changedType : IRArrayType = {s.VarType with IndexTypes = changedIndices}
                        {s with VarType = changedType}
                    let changedModule (m: IRModule) : IRModule =
                        let changedReads : Map<IRId, ProviderReadSpec> = Map.map changedProvider m.ProviderReads
                        {m with ProviderReads = changedReads}
                    let disagree : IRProgram = {ir with Modules = List.map changedModule ir.Modules}
                    refuse "source_extent_disagreement" disagree
                    // The immutable certificate is scoped to the final IR and
                    // exact binding records; it cannot be reused after a rewrite.
                    let priorAnalysis =
                        setCallablesContext (ir.Modules |> List.collect (_.Functions) |> List.map (fun f -> f.Id, f) |> Map.ofList)
                    try
                        let applies = ResizeArray<ApplyInfo>()
                        for m in ir.Modules do
                            for b in m.Bindings do
                                iterIRExpr (function IRApplyCombinator a -> applies.Add a | _ -> ()) b.Value
                        let a = applies |> Seq.find (fun a -> match a.Arrays with [IRRange _] -> true | _ -> false)
                        let sourceId, spec = ir.Modules |> List.collect (fun m -> Map.toList m.ProviderReads) |> List.find (fun (_, s) -> s.Streamed)
                        let builder = IRBuilder()
                        builder.EnsureAtLeast(0x40000000)
                        let initial = buildLoopNestCodeGen a ["__halo_range"] "__certificate_test" builder
                        let cg = {initial with MpiSlab = true}
                        match Blade.CodeGenLoopNest.certifyRank1WindowReads cg "stencil" [(sourceId,"A",spec,"__cert_win")] (-1L) 1L 1L [] with
                        | Error why -> check "rank1 local windows: stale-proof fixture obtains a certificate" false why
                        | Ok cert ->
                            let stale label changed =
                                try
                                    Blade.CodeGenLoopNest.genLoopNestWindowed cert Map.empty changed Map.empty 0 |> ignore
                                    check ($"rank1 local windows: changed {label} rejects stale certificate before rendering") false "unexpected render"
                                with
                                | :? InvalidOperationException as ex ->
                                    check ($"rank1 local windows: changed {label} rejects stale certificate before rendering")
                                        (ex.Message.Contains "certificate belongs to a different kernel") ex.Message
                            stale "kernel" {cg with KernelExpr = IRUnaryOp(IRNeg,cg.KernelExpr)}
                            stale "bindings" {cg with Bindings = cg.Bindings |> List.map (fun b -> {b with Extent = IRLit(IRLitInt 1L)})}
                    finally
                        restoreAnalysisContext priorAnalysis
                    // A normal emission after refusals cannot inherit state or
                    // lose its own certificate. No ambient authorization exists.
                    let cpp, _ = CodeGen.genSelfContainedProgramFromIR ir "zarr_window_after_refusal"
                    check "rank1 local windows: repeated emission retains safe local addressing"
                        (cpp.Contains "d__win0[static_cast<size_t>" && noUnsafeAlias cpp
                         && not (cpp.Contains "#error") && not (cpp.Contains "BLADE_CODEGEN_ERROR")) ""
            with ex -> check "rank1 local window addressing" false ex.Message
        finally
            Environment.SetEnvironmentVariable("BLADE_PRINT", savedPrint)
            Environment.SetEnvironmentVariable("BLADE_TILE_CACHE", savedTile)
    rank1WindowAddressingE2E ()

    // D05: bound traversal metadata through declaration and emission. Explicit
    // structural groupings and store fingerprint enumeration remain separate.
    let uniformHaloCursorE2E () =
        printfn "\n--- uniform Zarr halo cursor: compact metadata and identical windows ---"
        let marker = "uniform-zarr-halo-cursor"
        let savedPrint = Environment.GetEnvironmentVariable "BLADE_PRINT"
        let savedTile = Environment.GetEnvironmentVariable "BLADE_TILE_CACHE"
        let source (store: string) (edge: string) (radius: int) : string =
            let lines : string list =
                [ "import zarr as z";
                  sprintf "let s = z.load(\"%s\")" store;
                  sprintf "type CX = Chunked<s.index.x, %s>" edge;
                  "let A: Array<Float like CX> = s.vars.A |> z.stream";
                  sprintf "let d = method_for(halo<CX, [-%d, 0, %d]>) <@> lambda(w) -> A(w(%d)) - A(w(-%d)) |> compute" radius radius radius radius;
                  "" ]
            String.concat "\n" lines
        let fixture (label: string) (n: int) (missing: bool) (nullChunk: bool) : string =
            let store = Path.GetFullPath(Path.Combine(scratch, "uniform_cursor_" + label)).Replace('\\', '/')
            let values = [| for j in 0 .. n - 1 -> if missing && j = 0 then nan else float ((j + 1) * (j + 1)) |]
            let vars : ZarrWrite.WriteVar list =
                [ { Name = "A"; DimNames = Some ["x"]; Shape = [int64 n]; Chunks = [4L]
                    FillValue = (if nullChunk then FillNone else FillFloat 0.0); Data = ZarrWrite.WF64 values
                    OmitChunks = (if nullChunk then [[1L]] else []); Blade = None } ]
            ZarrWrite.writeStoreV2 store vars
            store
        let metadataOnly (label: string) (n: int64) (edge: int64) : string =
            let store = Path.GetFullPath(Path.Combine(scratch, "uniform_cursor_metadata_" + label)).Replace('\\', '/')
            let arrayDir = Path.Combine(store, "A")
            Directory.CreateDirectory arrayDir |> ignore
            let metadata : string = sprintf """{"zarr_format":2,"shape":[%d],"chunks":[%d],"dtype":"<f8","compressor":null,"fill_value":0.0,"order":"C","filters":null}""" n edge
            File.WriteAllText(Path.Combine(store, ".zgroup"), """{"zarr_format":2}""")
            File.WriteAllText(Path.Combine(arrayDir, ".zarray"), metadata)
            File.WriteAllText(Path.Combine(arrayDir, ".zattrs"), """{"_ARRAY_DIMENSIONS":["x"]}""")
            store
        let run (label: string) (cpp: string) =
            CodeGen.deployRuntimeHeaders e2eDir
            let file = Path.Combine(e2eDir, label + ".cpp")
            File.WriteAllText(file, cpp)
            match compileCpp file e2eDir with Error e -> Error e | Ok exe -> runExecutable exe
        let emit (label: string) (src: string) : (IRProgram * string) option =
            match lower src with
            | Error e -> check ($"{label}: lowers") false e; None
            | Ok ir ->
                let cpp, _ = CodeGen.genSelfContainedProgramFromIR ir label
                Some (ir, cpp)
        try
            try
                Environment.SetEnvironmentVariable("BLADE_PRINT", "d")
                Environment.SetEnvironmentVariable("BLADE_TILE_CACHE", null)
                SegmentTable.recordUniform "D05.left.CX" 10L 3L
                SegmentTable.recordUniform "D05.right.CX" 11L 4L
                check "uniform cursor: qualified alias identities stay distinct"
                    (SegmentTable.tryFindRuns "D05.left.CX" = Some (SegmentTable.UniformRuns(10L,3L))
                     && SegmentTable.tryFindRuns "D05.right.CX" = Some (SegmentTable.UniformRuns(11L,4L))) ""
                SegmentTable.record "D05.explicit" [0L; 3L; 8L; 10L]
                check "uniform cursor: explicit registry and compatibility APIs retain boundaries"
                    (SegmentTable.tryFindRuns "D05.explicit" = Some (SegmentTable.ExplicitRuns [0L;3L;8L;10L])
                     && SegmentTable.tryFind "D05.explicit" = Some [0L;3L;8L;10L]
                     && SegmentTable.tryFind "D05.left.CX" = Some [0L;3L;6L;9L;10L]) ""
                SegmentTable.reset ()
                check "uniform cursor: reset clears compact and explicit entries"
                    (SegmentTable.tryFindRuns "D05.left.CX" = None && SegmentTable.tryFindRuns "D05.explicit" = None) ""
                let geometryCases : (int64 * int64 * int64 * int64 * int64 * int64 list) list =
                    [ (10L,4L,-1L,1L,6L,[0L;4L;8L;10L]);
                      (11L,4L,-1L,1L,6L,[0L;4L;8L;11L]);
                      (10L,3L,-1L,1L,5L,[0L;3L;6L;9L;10L]);
                      (11L,2L,-3L,3L,8L,[0L;2L;4L;6L;8L;10L;11L]);
                      (2L,1L,-1L,1L,1L,[0L;1L;2L]);
                      (10L,4L,-3L,0L,7L,[0L;4L;8L;10L]) ]
                for n,k,lo,hi,capacity,offsets in geometryCases do
                    let actual = Blade.CodeGenCuda.uniformHaloWindowCapacity n k (-lo) (n-hi) lo hi
                    check ($"uniform cursor: independent exact capacity N{n} K{k} reach{lo},{hi}") (actual = Ok capacity) (sprintf "%A" actual)
                    check ($"uniform cursor: compatibility boundary reference N{n} K{k}")
                        (SegmentTable.toExplicitOffsets (SegmentTable.UniformRuns(n,k)) = offsets) ""
                check "uniform cursor: signed-limit capacity checked without enumeration"
                    (Blade.CodeGenCuda.uniformHaloWindowCapacity Int64.MaxValue Int64.MaxValue 0L Int64.MaxValue 0L 0L = Ok Int64.MaxValue) ""
                let badGeometry : (int64 * int64 * int64 * int64 * int64 * int64) list =
                    [ (10L,0L,1L,9L,-1L,1L); (10L,11L,1L,9L,-1L,1L);
                      (10L,4L,2L,9L,-1L,1L); (10L,4L,1L,10L,-1L,1L);
                      (0L,1L,0L,0L,0L,0L); (10L,4L,0L,9L,Int64.MinValue,1L) ]
                for n,k,a,b,lo,hi in badGeometry do
                    check "uniform cursor: malformed/overflowing geometry refuses without enumeration"
                        ((Blade.CodeGenCuda.uniformHaloWindowCapacity n k a b lo hi).IsError) ""
                let valueCases : (string * int * string * int * int64 * string) list =
                    [ ("store",11,"store",1,6L,"d = [8.0, 12.0, 16.0, 20.0, 24.0, 28.0, 32.0, 36.0, 40.0]");
                      ("literal3",10,"3",1,5L,"d = [8.0, 12.0, 16.0, 20.0, 24.0, 28.0, 32.0, 36.0]");
                      ("wide_radius",11,"2",3,8L,"d = [48.0, 60.0, 72.0, 84.0, 96.0]");
                      ("empty_interior",2,"1",1,1L,"d = []") ]
                for label,n,edge,radius,capacity,expected in valueCases do
                    let store = fixture label n false false
                    match emit ("zarr_cursor_" + label) (source store edge radius) with
                    | None -> ()
                    | Some (_, cpp) ->
                        let k = if edge = "store" then 4L else Int64.Parse edge
                        check ($"uniform cursor: {label} keeps compact declaration facts through emission")
                            (SegmentTable.tryFindRuns "CX" = Some (SegmentTable.UniformRuns(int64 n,k))) ""
                        let advance = cpp.IndexOf("__run_next = __run_hi;", StringComparison.Ordinal)
                        let emptyContinue = cpp.IndexOf("if (__c_lo >= __c_hi) continue;", StringComparison.Ordinal)
                        check ($"uniform cursor: {label} actual capped step, local load and progress before continue")
                            (cpp.Contains marker && not (cpp.Contains "d__runs[")
                             && cpp.Contains "__run_step" && cpp.Contains "d__win0[static_cast<size_t>"
                             && advance >= 0 && emptyContinue > advance
                             && cpp.Contains ($"d__win0 = new double[{capacity}];")
                             && cpp.Contains "__c_lo = __run_lo <" && cpp.Contains "__c_hi = __run_hi >"
                             && cpp.Contains "delete[] d__win0;" && not (cpp.Contains " - __w_lo,")) ""
                        if zCaps.HasGpp then
                            match run ("zarr_cursor_" + label) cpp with
                            | Ok (0, output) -> check ($"uniform cursor: {label} independent values/clipped empty runs") (output.Contains expected) output
                            | other -> check ($"uniform cursor: {label} runs") false (sprintf "%A" other)
                        else printfn "  SKIP uniform cursor %s: g++ not found" label
                // Metadata-only fixtures contain no payload. Large N cannot
                // build a compiler boundary list or emitted boundary initializer.
                let mutable smallCppBytes = 0
                for label,n in [("small",1009L); ("large",1000000007L)] do
                    let store = metadataOnly label n 127L
                    match emit ("zarr_cursor_metadata_" + label) (source store "store" 1) with
                    | None -> ()
                    | Some (_, cpp) ->
                        check ($"uniform cursor: metadata-only {label} has one compact descriptor and no payload")
                            (SegmentTable.tryFindRuns "CX" = Some (SegmentTable.UniformRuns(n,127L))
                             && Directory.GetFiles(store,"*",SearchOption.AllDirectories).Length = 3
                             && cpp.Contains marker && not (cpp.Contains "d__runs[")) ""
                        if label = "small" then smallCppBytes <- cpp.Length
                        else
                            check "uniform cursor: emitted metadata does not grow with N/K"
                                (smallCppBytes > 0 && cpp.Length < smallCppBytes + 8192) ($"small={smallCppBytes}, large={cpp.Length}")
                let store = fixture "explicit_control" 11 false false
                match emit "zarr_cursor_control" (source store "store" 1) with
                | None -> ()
                | Some (ir, _) ->
                    SegmentTable.record "CX" [0L;3L;7L;11L]
                    let cpp, _ = CodeGen.genSelfContainedProgramFromIR ir "zarr_cursor_explicit"
                    check "uniform cursor: irregular explicit plan keeps D04 table and safe local addressing"
                        (not (cpp.Contains marker) && cpp.Contains "d__runs[4] = { 0UL, 3UL, 7UL, 11UL }"
                         && cpp.Contains "d__win0[static_cast<size_t>" && not (cpp.Contains " - __w_lo,")) ""
                    let invalidDescriptors : (int64 * int64) list = [(12L,4L); (11L,0L); (11L,-1L); (11L,12L)]
                    for n,k in invalidDescriptors do
                        SegmentTable.recordUniform "CX" n k
                        try
                            CodeGen.genSelfContainedProgramFromIR ir "zarr_cursor_bad_descriptor" |> ignore
                            check "uniform cursor: malformed descriptor refuses before emission" false "unexpected success"
                        with
                        | Blade.Diagnostics.BladeDiagnosticException d ->
                            check "uniform cursor: malformed descriptor refuses before emission"
                                (d.Code = "BL7001" && d.Message.Contains "rank-one streamed window addressing") d.Message
                    SegmentTable.recordUniform "CX" 11L 4L
                // Separate user-visible structural grouping keeps its own
                // explicit IR/offsets representation; no whole-program bound.
                let groupedSource = (source store "store" 1) + "\nlet seg = segments(CX)\n"
                match emit "zarr_cursor_grouping_control" groupedSource with
                | Some (_, cpp) ->
                    check "uniform cursor: explicit segments consumer retains its table"
                        (cpp.Contains marker && cpp.Contains "seg__offsets[4]") ""
                | None -> ()
                let huge = metadataOnly "overflow" Int64.MaxValue 127L
                match lower (source huge "store" 1) with
                | Error e -> check "uniform cursor: overflow fixture lowers without eager offsets" false e
                | Ok ir ->
                    try
                        CodeGen.genSelfContainedProgramFromIR ir "zarr_cursor_capacity_overflow" |> ignore
                        check "uniform cursor: required output capacity refuses before allocation" false "unexpected success"
                    with
                    | Blade.Diagnostics.BladeDiagnosticException d ->
                        check "uniform cursor: required output capacity refuses before allocation"
                            (d.Code = "BL7001" && d.Message.Contains "allocation capacity overflows") d.Message
                let zero = metadataOnly "zero" 0L 1L
                check "uniform cursor: frontend zero-extent Chunked admission remains unchanged"
                    (isError (lower (source zero "1" 1)) "chunk edge must be between 1") ""
                // K is the authored segmentation, independently of physical C=4.
                let failures : (string * bool) list = [("short",false); ("null",true)]
                for label,isNull in failures do
                    for k in [3;4] do
                        let label = $"zarr_cursor_{label}_K{k}"
                        let store = fixture label 10 true isNull
                        match emit label (source store (string k) 1) with
                        | None -> ()
                        | Some (_, cpp) ->
                            if not isNull then File.WriteAllBytes(Path.Combine(store,"A","1"),[|0uy|])
                            if zCaps.HasGpp then
                                match run label cpp with
                                | Ok (code, output) ->
                                    let correct =
                                        if k = 3 then output.Contains "error[BL8015]" && output.Contains "first at [0]"
                                        elif isNull then output.Contains "fill_value is null" && not (output.Contains "error[BL8015]")
                                        else output.Contains "is short (expected 32 bytes)" && not (output.Contains "error[BL8015]")
                                    check ($"uniform cursor: {label} preserves authored-grid failure order")
                                        (code <> 0 && correct && not (output.Contains "d = ")) output
                                | Error e -> baselineFailed label e
            with ex -> check "uniform Zarr halo cursor" false ex.Message
        finally
            Environment.SetEnvironmentVariable("BLADE_PRINT", savedPrint)
            Environment.SetEnvironmentVariable("BLADE_TILE_CACHE", savedTile)
            SegmentTable.reset ()
    uniformHaloCursorE2E ()

    check "private statistical-barrier spool focused gate"
        (Blade.Tests.PrivateSpoolTests.runPrivateSpoolTests
            (Path.Combine(e2eDir, "D08_" + Guid.NewGuid().ToString("N"))) = 0) "see focused gate above"

    check "rank-two streamed value boundary focused gate"
        (runRank2StreamedValueBoundaryTests
            (Path.Combine(e2eDir, "D09_I1_" + Guid.NewGuid().ToString("N"))) = 0) "see focused gate above"

    // ---------------------------------------------------------------
    // 10i. RANK-2 streaming: one store standing in for four files decomposed
    // along lat and lon (a 6 x 8 variable chunked 3 x 4 = four chunk files).
    // `segments(T)` off the annotation's two `Chunked` slots is the tile
    // grouping; `group_by` over the streamed variable reads each tile as one
    // window (here, exactly one chunk file) into its row; an elementwise
    // consumer runs one band of rows (the leading chunk edge) at a time with
    // a row-pointer alias; `ungroup` puts the tiles back. The traversal order
    // is the store's own. No whole-array buffer for T anywhere.
    // ---------------------------------------------------------------
    printfn "
--- rank-2 streaming: tiles, row bands, ungroup ---"
    let rank2StreamE2E () =
        let tileStore = fixStore "zarr_tiles_2d"
        let tileInDir = Path.Combine(e2eDir, tileStore)
        let vars : ZarrWrite.WriteVar list = [
            { Name = "T"; DimNames = Some ["lat"; "lon"]; Shape = [6L; 8L]; Chunks = [3L; 4L]
              FillValue = FillFloat 0.0
              Data = ZarrWrite.WF64 [| for i in 0 .. 5 do for j in 0 .. 7 -> float (10 * i + j) |]
              OmitChunks = []; Blade = None } ]
        (try Directory.Delete(tileStore, true) with _ -> ())
        (try Directory.Delete(tileInDir, true) with _ -> ())
        ZarrWrite.writeStoreV3 tileStore vars
        ZarrWrite.writeStoreV3 tileInDir vars
        let src = sprintf """
import zarr as z

let s = z.load("%s")
type CLat = Chunked<s.index.lat, store>
type CLon = Chunked<s.index.lon, store>
let T: Array<Float like CLat, CLon> = s.vars.T |> z.stream
let tiles = segments(T)
let tsizes = extents(tiles)
let g = group_by(T, tiles)
let tsums = method_for(g) <@> lambda(r) -> reduce(r, (+)) |> compute
let doubled = T * 2.0
let back = ungroup(g)
let dev = reduce(back - T, (+), axes = 2)
"""
                            tileStore
        try
            Blade.Effects.Decisions.start ()
            match lower src with
            | Ok ir ->
                let decisions = Blade.Effects.Decisions.drain ()
                check "rank-2 stream: plan records the tile group_by and the elementwise consumers"
                    ((decisions |> List.filter (fun d -> d.Rule = "segment-streaming" && d.Outcome = Blade.Effects.Applied) |> List.length) >= 3)
                    (sprintf "%A" (decisions |> List.map (fun d -> d.Subject)))
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_segments_rank2"
                check "rank-2 stream: tile windows and row bands, no whole-array buffer"
                    (cppCode.Contains "per-tile STREAMED windows" && cppCode.Contains "elementwise over segments" && not (cppCode.Contains "T_flat = new")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, "zarr_segments_rank2.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          let has (line: string) = runOut.Contains line
                          check "rank-2 stream: tile sizes" (has "tsizes = [12, 12, 12, 12]") runOut
                          check "rank-2 stream: per-tile sums" (has "tsums = [138.0, 186.0, 498.0, 546.0]") runOut
                          check "rank-2 stream: elementwise by row bands" (has "doubled = [[0.0, 2.0, 4.0, 6.0, 8.0, 10.0, 12.0, 14.0], [20.0, 22.0, 24.0, 26.0, 28.0, 30.0, 32.0, 34.0]") runOut
                          check "rank-2 stream: ungroup of the tiles restores the variable (zip with the stream)" (has "dev = 0.0") runOut
                      | Ok (code, out) -> check "rank-2 stream: runs" false ($"exit {code}: {out}")
                      | Error e -> check "rank-2 stream: runs" false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP rank-2 stream (compile skipped): %s" e
                     else check "rank-2 stream: compiles" false e)
            | Error e ->
                Blade.Effects.Decisions.drain () |> ignore
                check "rank-2 stream: lowers" false e
        with ex -> check "rank-2 stream" false ex.Message
    rank2StreamE2E ()

    // ---------------------------------------------------------------
    // 10b. Dimension names that collide with C-library globals.
    //
    // Every store dimension derives a named index type, and codegen emits one
    // `using <name> = int64_t;` per index type into the GLOBAL namespace. A
    // store with a dimension named `time` -- the geoscience default, so the
    // common case rather than an exotic one -- therefore emitted
    // `using time = int64_t;` against <ctime>'s `time`, and g++ refused the
    // whole translation unit with "redeclared as different kind of entity".
    // `j0` covers the same hazard from <cmath>'s Bessel functions.
    //
    // The mangling is an EMISSION concern (CodeGenState.indexTypeCppName), so
    // the fixture asserts both halves: the Blade-visible names are untouched
    // (`sample.dims.time` still resolves in source), and the emitted C++ never
    // carries the bare alias.
    // ---------------------------------------------------------------
    printfn "\n--- reserved-identifier dim names: `time` / `j0` ---"
    (let rsvStore = fixStore "zarr_reserved_dims"
     let rsvInDir = Path.Combine(e2eDir, rsvStore)
     let rsvVars : ZarrWrite.WriteVar list = [
        // Coordinate array for the `time` dimension (1-D and same-named, the
        // shape xarray writes), so `sample.dims.time` is readable too.
        { Name = "time"; DimNames = Some ["time"]; Shape = [3L]; Chunks = [3L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| 10.0; 20.0; 30.0 |]
          OmitChunks = []; Blade = None }
        { Name = "T"; DimNames = Some ["time"; "j0"]; Shape = [3L; 2L]; Chunks = [3L; 2L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| 1.0 .. 6.0 |]
          OmitChunks = []; Blade = None } ]
     (try Directory.Delete(rsvStore, true) with _ -> ())
     (try Directory.Delete(rsvInDir, true) with _ -> ())
     ZarrWrite.writeStoreV3 rsvStore rsvVars
     ZarrWrite.writeStoreV3 rsvInDir rsvVars
     let rsvSource = sprintf """
import zarr as z

let sample = z.load("%s")
let T = sample.vars.T |> z.read
let ts = sample.dims.time |> z.read
let total = reduce(T, (+), axes = 2)
let tsum = reduce(ts, (+))
"""
                         rsvStore
     /// `name = <number>` on its own line, InvariantCulture.
     let printedScalar (name: string) (out: string) : float option =
        out.Split('\n')
        |> Array.tryPick (fun l ->
            let l = l.Trim()
            let prefix = name + " = "
            if l.StartsWith prefix then
                match Double.TryParse(l.Substring prefix.Length,
                                      Globalization.NumberStyles.Float,
                                      Globalization.CultureInfo.InvariantCulture) with
                | true, v -> Some v
                | _ -> None
            else None)
     try
        match lower rsvSource with
        | Ok ir ->
            let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_reserved_dims"
            check "reserved dims: no bare `using time =` / `using j0 =` emitted"
                (not (cppCode.Contains "using time = ") && not (cppCode.Contains "using j0 = "))
                "bare alias still emitted -- g++ would refuse the TU"
            check "reserved dims: mangled aliases emitted instead"
                (cppCode.Contains "using time_ = int64_t;" && cppCode.Contains "using j0_ = int64_t;")
                "expected time_ / j0_ aliases"
            CodeGen.deployRuntimeHeaders e2eDir
            let rsvCpp = Path.Combine(e2eDir, "zarr_reserved_dims.cpp")
            File.WriteAllText(rsvCpp, cppCode)
            (match compileCpp rsvCpp e2eDir with
             | Ok exePath ->
                 // The gate this section exists for: before the mangling, g++
                 // died here on the `time` alias alone.
                 check "reserved dims: compiles (the alias no longer redeclares a C-library global)" true ""
                 (match runExecutable exePath with
                  | Ok (0, runOut) ->
                      check "reserved dims: total = sum(T) = 21"
                          (match printedScalar "total" runOut with
                           | Some v -> abs (v - 21.0) <= 1e-9
                           | None -> false)
                          runOut
                      check "reserved dims: tsum = sum(time coord) = 60"
                          (match printedScalar "tsum" runOut with
                           | Some v -> abs (v - 60.0) <= 1e-9
                           | None -> false)
                          runOut
                  | Ok (code, runOut) -> check "reserved dims: runs (exit 0)" false ($"exit {code}: {runOut}")
                  | Error e -> check "reserved dims: runs (exit 0)" false e)
             | Error e ->
                 if isSkipError e then printfn "  SKIP zarr reserved-dim e2e (compile skipped): %s" e
                 else check "reserved dims: compiles" false e)
        | Error e ->
            // Names the Blade-side half of the invariant: mangling happens at
            // EMISSION, so `sample.dims.time` must still resolve in source.
            check "reserved dims: lowers (`time` is still the Blade-visible dim name)" false e
     with ex -> check "reserved dims e2e" false ex.Message)

    // ---------------------------------------------------------------
    // 11. Write -> read roundtrip e2e (the Blade-side writer)
    // ---------------------------------------------------------------
    printfn "\n--- write e2e: z.read |> z.write -> F# reads it back ---"
    (let inStore = fixStore "zarr_wrt_in"
     let outStore = fixStore "zarr_wrt_out"
     let inDirFull = Path.Combine(e2eDir, inStore)
     let outDirFull = Path.Combine(e2eDir, outStore)
     let wrtVars : ZarrWrite.WriteVar list = [
        { Name = "A"; DimNames = Some ["x"; "y"]; Shape = [4L; 3L]; Chunks = [2L; 2L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| for i in 0 .. 11 -> float i + 0.25 |]; OmitChunks = []; Blade = None } ]
     (try Directory.Delete(inStore, true) with _ -> ())
     (try Directory.Delete(inDirFull, true) with _ -> ())
     (try Directory.Delete(outDirFull, true) with _ -> ())
     ZarrWrite.writeStoreV3 inStore wrtVars       // compile-time metadata (compiler cwd)
     ZarrWrite.writeStoreV3 inDirFull wrtVars     // runtime read (exe cwd)
     let writeSource = sprintf """
import zarr as z

let sample = z.load("%s")
let A = sample.vars.A |> z.read
let w = z.write("%s", A)
"""
                           inStore outStore
     try
        match lower writeSource with
        | Ok ir ->
            check "write e2e: ProviderWrites spec recorded (provider=zarr)"
                (ir.Modules.[0].ProviderWrites |> Map.exists (fun _ s -> s.Provider = "zarr" && s.VarName = "A" && s.FilePath = outStore))
                ($"{(Map.count ir.Modules.[0].ProviderWrites)} write specs")
            let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_write_e2e"
            check "write e2e: emits flatten + filesystem writer"
                (cppCode.Contains "create_directories" && cppCode.Contains ".zarray") ""
            CodeGen.deployRuntimeHeaders e2eDir
            let cppFile = Path.Combine(e2eDir, "zarr_write_e2e.cpp")
            File.WriteAllText(cppFile, cppCode)
            (match compileCpp cppFile e2eDir with
             | Ok exePath ->
                 check "write e2e: compiles" true ""
                 (match runExecutable exePath with
                  | Ok (0, _) ->
                      check "write e2e: runs (exit 0)" true ""
                      (match readVarData outDirFull "A" with
                       | Ok { DimLengths = [4; 3]; Payload = ZFloats got } ->
                           check "write e2e: written store reads back exactly (F# reader)"
                               (got = [| for i in 0 .. 11 -> float i + 0.25 |]) (sprintf "got %A" got)
                       | Ok d -> check "write e2e: written store reads back" false (sprintf "shape %A" d.DimLengths)
                       | Error e -> check "write e2e: written store reads back" false e)
                      (match readVarData outDirFull "A" with
                       | Ok _ ->
                           let store = load outDirFull
                           check "write e2e: written store carries dim names (from provider index types)"
                               (match tryFindArray store "A" with
                                | Some m -> m.DimNames = Some ["x"; "y"]
                                | None -> false)
                               ""
                       | Error _ -> ())
                  | Ok (code, runOut) -> check "write e2e: runs (exit 0)" false ($"exit {code}: {runOut}")
                  | Error e -> check "write e2e: runs (exit 0)" false e)
             | Error e ->
                 if isSkipError e then printfn "  SKIP zarr write e2e (compile skipped): %s" e
                 else check "write e2e: compiles" false e)
        | Error e -> check "write e2e: lowers" false e
     with ex -> check "write e2e" false ex.Message)

    // ---------------------------------------------------------------
    // 12. load_compound rejection at codegen (zarr has no compound reader)
    // ---------------------------------------------------------------
    printfn "\n--- load_compound: loud zarr rejection ---"
    (let lcStore = fixStore "zarr_lc"
     (try Directory.Delete(lcStore, true) with _ -> ())
     ZarrWrite.writeStoreV2 lcStore [
        { Name = "A"; DimNames = Some ["x"]; Shape = [4L]; Chunks = [4L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| 1.0; 2.0; 3.0; 4.0 |]; OmitChunks = []; Blade = None }
        { Name = "M"; DimNames = Some ["x"]; Shape = [4L]; Chunks = [4L]
          FillValue = FillInt 0L; Data = ZarrWrite.WI64 [| 1L; 0L; 1L; 1L |]; OmitChunks = []; Blade = None } ]
     let lcSource = """
import zarr as z

let sample = z.load("tests/fixtures/zarr_stores/zarr_lc")
let data = z.load_compound(sample.vars.A, sample.vars.M) |> z.read
"""
     match lower lcSource with
     | Ok ir ->
         (try
             CodeGen.genSelfContainedProgramFromIR ir "zarr_lc" |> ignore
             check "load_compound via zarr: rejected loudly at codegen" false "codegen succeeded?"
          with ex ->
             check "load_compound via zarr: rejected loudly at codegen"
                 (ex.Message.Contains "does not support load_compound") ex.Message)
     | Error e ->
         check "load_compound via zarr: lowers (rejection is codegen's job)" false e)

    // ---------------------------------------------------------------
    // 13. blade layout attribute: parse + validation (pure)
    // ---------------------------------------------------------------
    printfn "\n--- blade packed layout: attribute parse ---"
    let bladeZattrs extra = sprintf """{"blade": {"spec_version": 1, "layout": "packed", "order": "ascending-lex", "index_types": [%s]}}""" extra
    let v2packed shape = sprintf """{"shape":[%s],"chunks":[%s],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}""" shape shape
    (match parseArrayMetaV2 "C" "d" (v2packed "10") (Some (bladeZattrs """{"kind": "sym", "rank": 2, "extent": 4}""")) with
     | Ok m ->
         check "blade parse: sym r2 n4 accepted (card 10)"
             (match m.Blade with
              | Some l -> l.Group.Sym = SymSymmetric && l.Group.Rank = 2 && l.Group.Extent = 4L && l.DenseDims = []
              | None -> false)
             (sprintf "%A" m.Blade)
     | Error e -> check "blade parse: sym r2 n4 accepted (card 10)" false e)
    check "blade parse: antisym r2 n4 accepted (card 6)"
        (match parseArrayMetaV2 "C" "d" (v2packed "6") (Some (bladeZattrs """{"kind": "antisym", "rank": 2, "extent": 4}""")) with
         | Ok m -> (match m.Blade with Some l -> l.Group.Sym = SymAntisymmetric | None -> false)
         | Error _ -> false) ""
    check "blade parse: mixed sym x dense accepted"
        (match parseArrayMetaV2 "C" "d" """{"shape":[6,3],"chunks":[6,3],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}"""
                   (Some (bladeZattrs """{"kind": "sym", "rank": 2, "extent": 3}, {"kind": "dense", "extent": 3}""")) with
         | Ok m -> (match m.Blade with Some l -> l.DenseDims = [3L] | None -> false)
         | Error _ -> false) ""
    check "blade parse: cardinality mismatch is LOUD"
        (isError (parseArrayMetaV2 "C" "d" (v2packed "9") (Some (bladeZattrs """{"kind": "sym", "rank": 2, "extent": 4}"""))) "cardinality 10") ""
    check "blade parse: herm reserved"
        (isError (parseArrayMetaV2 "C" "d" (v2packed "10") (Some (bladeZattrs """{"kind": "herm", "rank": 2, "extent": 4}"""))) "reserved") ""
    check "blade parse: unknown kind rejected"
        (isError (parseArrayMetaV2 "C" "d" (v2packed "10") (Some (bladeZattrs """{"kind": "diag", "rank": 2, "extent": 4}"""))) "unknown kind") ""
    check "blade parse: dense-first rejected (packed group must lead)"
        (isError (parseArrayMetaV2 "C" "d" """{"shape":[3,10],"chunks":[3,10],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}"""
                      (Some (bladeZattrs """{"kind": "dense", "extent": 3}, {"kind": "sym", "rank": 2, "extent": 4}"""))) "FIRST") ""
    // The version gate is closed UPWARD, not at 1: spec_version 2 (the orbit
    // head, section 18b) is implemented and a sym head under it is valid, so
    // the "future version" probe moved to 3.
    check "blade parse: future spec_version rejected"
        (isError (parseArrayMetaV2 "C" "d" (v2packed "10") (Some """{"blade": {"spec_version": 3, "layout": "packed", "index_types": [{"kind": "sym", "rank": 2, "extent": 4}]}}""")) "spec_version") ""
    check "blade parse: v3 attributes carry the layout too"
        (match parseArrayMetaV3 "C" "d" """{"zarr_format":3,"node_type":"array","shape":[10],"data_type":"float64","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[5]}},"fill_value":0,"codecs":[{"name":"bytes"}],"attributes":{"blade":{"spec_version":1,"layout":"packed","order":"ascending-lex","index_types":[{"kind":"sym","rank":2,"extent":4}]}}}""" with
         | Ok m -> (match m.Blade with Some l -> l.Group.Rank = 2 && l.Group.Extent = 4L | None -> false)
         | Error e -> false) ""
    check "binom/packedCardinality: sym(2,4)=10, antisym(2,4)=6, sym(3,3)=10"
        (packedCardinality { Sym = SymSymmetric; Rank = 2; Extent = 4L; Levels = [] } = 10L
         && packedCardinality { Sym = SymAntisymmetric; Rank = 2; Extent = 4L; Levels = [] } = 6L
         && packedCardinality { Sym = SymSymmetric; Rank = 3; Extent = 3L; Levels = [] } = 10L) ""

    // ---------------------------------------------------------------
    // 14. Packed module typing (mirrors source-level SymIdx lowering)
    // ---------------------------------------------------------------
    printfn "\n--- blade packed layout: module typing ---"
    (let packedStore = {
        Path = "/mock/tri"; Version = 2
        Arrays = [
            { Name = "C"; ArrayDir = "/mock/tri/C"; Shape = [10L]; Chunks = [10L]
              Dtype = mockDt "f8" ETFloat64 8 true; DimNames = None
              FillValue = FillFloat 0.0; Codec = CodecIdentity
              Blade = Some { Group = { Sym = SymSymmetric; Rank = 2; Extent = 4L; Levels = [] }; DenseDims = []; Blocks = None }
              Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
        ] }
     let m = zarrStoreToModule (IRBuilder()) "tri" packedStore None
     let cType =
         m.Types |> List.tryPick (function IRTDStruct ("tri__vars", fs) -> Some fs | _ -> None)
         |> Option.defaultValue []
         |> List.tryPick (fun (n, t) -> if n = "C" then (match t with ArrayElem at -> Some at | _ -> None) else None)
     check "packed typing: C is Array<f64 like SymIdx<2,4>> (Symmetry/Rank/Extent match source lowering)"
         (match cType with
          | Some at ->
              at.IndexTypes.Length = 1
              && at.IndexTypes.[0].Symmetry = SymSymmetric
              && at.IndexTypes.[0].Rank = 2
              && (match at.IndexTypes.[0].Extent with IRLit (IRLitInt 4L) -> true | _ -> false)
              && at.IndexTypes.[0].IxKind = IxKPlain
          | None -> false)
         (sprintf "%A" cType))

    // ---------------------------------------------------------------
    // 15-17. Packed e2e: independent oracle -> read -> compute; and
    // read -> write roundtrip preserving exact pool order + metadata.
    // The oracle pool is computed with an INDEPENDENT F# enumeration
    // (ascending-lex loops here, not shared with provider code).
    // ---------------------------------------------------------------
    printfn "\n--- blade packed e2e: read + write roundtrips (sym and antisym) ---"
    let triOracle (strict: bool) (n: int) : float[] =
        [| for i in 0 .. n - 1 do
             for j in (if strict then i + 1 else i) .. n - 1 ->
               float ((i + 1) * 10 + (j + 1)) |]
    // Fold rejection first (hermetic, no g++): packed vars refuse to fold.
    (let foldTri = fixStore "zarr_tri_foldreject"
     (try Directory.Delete(foldTri, true) with _ -> ())
     ZarrWrite.writeStoreV2 foldTri [
        { Name = "C"; DimNames = None; Shape = [10L]; Chunks = [10L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 (triOracle false 4); OmitChunks = []
          Blade = Some { Group = { Sym = SymSymmetric; Rank = 2; Extent = 4L; Levels = [] }; DenseDims = []; Blocks = None } } ]
     match Blade.ProviderRegistry.tryFind "zarr" with
     | Some s ->
         check "packed fold: refused with steering"
             (match s.ReadVarData foldTri "C" with
              | Error e -> e.Contains "do not fold"
              | Ok _ -> false) ""
     | None -> check "packed fold: registry has zarr" false "")
    for (kind, sym, strict) in [ ("sym", SymSymmetric, false); ("antisym", SymAntisymmetric, true) ] do
        let n = 4
        let pool = triOracle strict n
        let card = int64 pool.Length
        let inStore = fixStore ($"zarr_tri_{kind}")
        let outStore = fixStore ($"zarr_tri_{kind}_out")
        let layout : BladeLayout = { Group = { Sym = sym; Rank = 2; Extent = int64 n; Levels = [] }; DenseDims = []; Blocks = None }
        let triVars : ZarrWrite.WriteVar list = [
            { Name = "C"; DimNames = None; Shape = [card]; Chunks = [card]
              FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
              Blade = Some layout } ]
        (try Directory.Delete(inStore, true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
        ZarrWrite.writeStoreV2 inStore triVars
        ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) triVars
        let triSource = sprintf """
import zarr as z

let s = z.load("%s")
let C = s.vars.C |> z.read
let out = method_for(C) <@> lambda(x) -> x + x |> compute
let w = z.write("%s", C)
"""
                            inStore outStore
        try
            match lower triSource with
            | Ok ir ->
                check ($"packed {kind}: read spec is packed (provider=zarr)")
                    (ir.Modules.[0].ProviderReads |> Map.exists (fun _ s ->
                        s.Provider = "zarr" && s.VarType.IndexTypes |> List.exists (fun ix -> ix.Symmetry = sym && ix.Rank = 2)))
                    ""
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir ($"zarr_tri_{kind}_e2e")
                // BOTH classes materialize by a linear pool_base copy under a
                // hoisted SYMM: allocate<>'s DFS pool order IS ascending-lex
                // for strict (antisym) storage exactly as for inclusive (sym)
                // storage, and the strict pool is compact (C(n,r) cells, no
                // dead diagonal). The antisym arm used to unrank each cell and
                // index the skeleton with `ix[k] - ix[k-1]`, which shifted the
                // whole pool by one; per-cell unranking here is now a defect.
                check ($"packed {kind}: codegen materializes the packed pool by linear copy")
                    (cppCode.Contains "pool_base"
                     && cppCode.Contains (if strict then "_anti" else "_symm")
                     && not (cppCode.Contains "antisymmetric::unlinearize")) ""
                check ($"packed {kind}: linearized_storage header included")
                    (cppCode.Contains "linearized_storage.hpp") ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, $"zarr_tri_{kind}_e2e.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     check ($"packed {kind}: compiles") true ""
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          check ($"packed {kind}: runs (exit 0)") true ""
                          // The doubled kernel output must be the doubled pool
                          // IN ORDER, cell for cell. A set comparison (or one
                          // that whitelists spurious 0.0) cannot see a pool
                          // rotated by one, which is precisely how the antisym
                          // strict-subscript defects presented — so this is a
                          // strict ordered comparison, and the print order of a
                          // packed array is part of the contract: it walks the
                          // canonical pool.
                          // Shape-tolerant flatten (TestHarness): a packed
                          // rank-2 group prints NESTED (genPrintArraySymAware
                          // routes rank 2 through genPrintNested2, row shrink
                          // and all), and flattening the nested walk IS the
                          // canonical pool order.
                          (match tryParsePrintedFloats "out" runOut with
                           | Some got ->
                               // EXACT elementwise equality in pool order, not
                               // the old
                               //   Set.isSubset expected got
                               //   && Set.isSubset (Set.remove 0.0 got) expected
                               // which was vacuous in two ways: Set.ofArray
                               // discarded MULTIPLICITY (a pool with repeated
                               // values passed even if the kernel emitted the
                               // wrong number of them), and `Set.remove 0.0 got`
                               // whitelisted an UNBOUNDED number of spurious
                               // zeros -- precisely the signature of a packed
                               // read that emitted unwritten padding cells. That
                               // whitelist was in fact absorbing a real defect:
                               // the antisym strict-storage off-by-one fixed on
                               // this branch, which shifted the pool and
                               // appended a 0.0. With the shift fixed, pool
                               // order is deterministic, so this pins ORDER too
                               // rather than sorting both sides -- a sorted
                               // compare would hide a future re-ordering
                               // regression in exactly the storage path the
                               // off-by-one lived in.
                               let expected = pool |> Array.map (fun x -> x + x)
                               check ($"packed {kind}: kernel values = 2x oracle pool (exact, in pool order)")
                                   (got = expected)
                                   (sprintf "got %A expected %A" got expected)
                           | None -> check ($"packed {kind}: kernel values") false "no parseable out = [...] line")
                          // Write roundtrip: exact pool order + blade metadata.
                          let outFull = Path.Combine(e2eDir, outStore)
                          (match readVarData outFull "C" with
                           | Ok { Payload = ZFloats got } ->
                               check ($"packed {kind}: written pool is EXACTLY the input pool (canonical order preserved)")
                                   (got = pool) (sprintf "got %A" (Array.truncate 6 got))
                           | Ok _ -> check ($"packed {kind}: written pool exact") false "not floats"
                           | Error e -> check ($"packed {kind}: written pool exact") false e)
                          (try
                              let wstore = load outFull
                              check ($"packed {kind}: written store carries the blade attribute")
                                  (match tryFindArray wstore "C" with
                                   | Some m -> m.Blade = Some layout
                                   | None -> false) ""
                           with ex -> check ($"packed {kind}: written store loads") false ex.Message)
                      | Ok (code, runOut) -> check ($"packed {kind}: runs (exit 0)") false ($"exit {code}: {runOut}")
                      | Error e -> check ($"packed {kind}: runs (exit 0)") false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP packed %s e2e (compile skipped): %s" kind e
                     else check ($"packed {kind}: compiles") false e)
            | Error e -> check ($"packed {kind}: lowers") false e
        with ex -> check ($"packed {kind} e2e") false ex.Message

    // ---------------------------------------------------------------
    // 17b. KERNEL-PRODUCED packed pool -> write (write side, unmasked)
    // ---------------------------------------------------------------
    // The read -> write roundtrip above CANNOT see a write-side pool shift:
    // the read materialization and the write flatten share one offset
    // formula, so a rotation applied on the way in is undone on the way out
    // and the store still matches byte for byte. The write path is only
    // pinned when the pool it flattens was built by a KERNEL (canonical
    // storage, no provider read in the chain) and is compared against an
    // independent oracle. That is this block. It is what the antisym
    // strict-subscript defect escaped through: `blade run` printed the right
    // six cells while the chunk on disk held the pool shifted left by one
    // with a fill 0.0 appended.
    printfn "\n--- blade packed e2e: kernel-produced pool -> write (independent oracle) ---"
    let sign (p: int list) =
        let a = List.toArray p
        let mutable s = 0
        for i in 0 .. a.Length - 1 do
            for j in i + 1 .. a.Length - 1 do
                if a.[i] > a.[j] then s <- s + 1
        if s % 2 = 0 then 1.0 else -1.0
    let rec perms (xs: int list) =
        match xs with
        | [] -> [ [] ]
        | _ -> xs |> List.collect (fun x -> perms (List.filter ((<>) x) xs) |> List.map (fun r -> x :: r))
    // (label, extent, rank, kernel body over params p0..p{rank-1}, scalar oracle
    //  on one ordered cell's A-values, symmetry keyword, SymmetryClass)
    // The oracle Reynolds-folds the SAME scalar function over every parameter
    // permutation with the group's sign — an enumeration written here, not
    // shared with any compiler code.
    let kernelWrites =
        [ "sym2",     4, 2, "p0 * 10.0 + p1",  (fun (v: float list) -> v.[0] * 10.0 + v.[1]),  "Symmetric",     SymSymmetric
          "antisym2", 4, 2, "p0 * 10.0 + p1",  (fun (v: float list) -> v.[0] * 10.0 + v.[1]),  "Antisymmetric", SymAntisymmetric
          // Rank 3 compounds the per-level strict shift: the retired formula
          // was wrong at every level beyond the first, not just the last.
          "antisym3", 5, 3, "p0 * p0 * p1",    (fun (v: float list) -> v.[0] * v.[0] * v.[1]), "Antisymmetric", SymAntisymmetric ]
    for (label, n, rank, body, scalarF, symKw, sym) in kernelWrites do
        let strict = (sym = SymAntisymmetric)
        let avals = [ for i in 0 .. n - 1 -> float (i + 1) ]
        // Canonical cells in ascending-lex order (i0 <= i1 <= .. for sym,
        // strictly increasing for antisym) — enumerated directly here.
        let rec cells (lo: int) (k: int) : int list list =
            if k = 0 then [ [] ]
            else [ for i in lo .. n - 1 do
                     for rest in cells (if strict then i + 1 else i) (k - 1) -> i :: rest ]
        let oracle =
            [| for c in cells 0 rank ->
                 perms [ 0 .. rank - 1 ]
                 |> List.sumBy (fun p ->
                     let vs = p |> List.map (fun q -> avals.[c.[q]])
                     (if strict then sign p else 1.0) * scalarF vs) |]
        let ps = [ 0 .. rank - 1 ] |> List.map (sprintf "p%d") |> String.concat ", "
        let outStore = fixStore ($"zarr_kw_{label}_out")
        (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
        let src =
            sprintf """
import zarr as z

let A = [%s]
let g = lambda(%s) where comm(%s) -> %s
let C = method_for(%s) <@> reynolds(g, %s) |> compute
let w = z.write("%s", C)
"""
                (avals |> List.map (sprintf "%.1f") |> String.concat ", ")
                ps ps body
                (List.replicate rank "A" |> String.concat ", ")
                symKw outStore
        try
            match lower src with
            | Ok ir ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir ($"zarr_kw_{label}_e2e")
                check ($"kernel-write {label}: pool flatten is a linear pool_base copy")
                    (cppCode.Contains "pool_base" && not (cppCode.Contains "antisymmetric::unlinearize")) ""
                // provider-sink v1 (BLADE_SINK_ALIAS, default on): the writer
                // reads C's pool in place -- no flatten buffer, no delete.
                check ($"kernel-write {label}: the writer reads the pool in place (provider-sink v1)")
                    (cppCode.Contains "_flat = nested_array_utilities::pool_base(C.data);"
                     && not (cppCode.Contains "__pc_pool") && not (cppCode.Contains "delete[] C_wr")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, $"zarr_kw_{label}_e2e.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          // The chunk on disk must equal the oracle pool cell
                          // for cell — no rotation, no trailing fill.
                          (match readVarData (Path.Combine(e2eDir, outStore)) "C" with
                           | Ok { DimLengths = dl; Payload = ZFloats got } ->
                               check ($"kernel-write {label}: written pool = independent oracle (exact, in order)")
                                   (dl = [oracle.Length] && got = oracle)
                                   (sprintf "shape %A got %A expected %A" dl got oracle)
                           | Ok _ -> check ($"kernel-write {label}: written pool") false "not floats"
                           | Error e -> check ($"kernel-write {label}: written pool") false e)
                          // ... and the in-process print must agree with it, so
                          // a shift cannot hide by moving between the two.
                          // Shape-tolerant flatten (TestHarness): C is a
                          // packed rank-2 group and prints NESTED; the
                          // flattened walk is the canonical pool order.
                          (match tryParsePrintedFloats "C" runOut with
                           | Some got ->
                               check ($"kernel-write {label}: printed pool = written pool = oracle")
                                   (got = oracle) (sprintf "got %A expected %A" got oracle)
                           | None -> check ($"kernel-write {label}: printed pool") false "no parseable C = [...] line")
                      | Ok (code, runOut) -> check ($"kernel-write {label}: runs (exit 0)") false ($"exit {code}: {runOut}")
                      | Error e -> check ($"kernel-write {label}: runs (exit 0)") false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP kernel-write %s e2e (compile skipped): %s" label e
                     else check ($"kernel-write {label}: compiles") false e)
            | Error e -> check ($"kernel-write {label}: lowers") false e
        with ex -> check ($"kernel-write {label} e2e") false ex.Message

    // ---------------------------------------------------------------
    // 18. Mixed sym x dense packed read -> write roundtrip
    // ---------------------------------------------------------------
    printfn "\n--- blade packed e2e: mixed sym x dense ---"
    (let n = 3
     let trail = 2
     let symCells = [ for i in 0 .. n - 1 do for j in i .. n - 1 -> (i, j) ]
     let pool =
         [| for (i, j) in symCells do
              for t in 0 .. trail - 1 ->
                float (100 * (i + 1) + 10 * (j + 1) + t) |]
     let card = int64 symCells.Length
     let layout : BladeLayout = { Group = { Sym = SymSymmetric; Rank = 2; Extent = int64 n; Levels = [] }; DenseDims = [int64 trail]; Blocks = None }
     let mixVars : ZarrWrite.WriteVar list = [
        { Name = "D"; DimNames = Some ["cells"; "t"]; Shape = [card; int64 trail]; Chunks = [card; int64 trail]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
          Blade = Some layout } ]
     let inStore = fixStore "zarr_tri_mixed"
     let outStore = fixStore "zarr_tri_mixed_out"
     (try Directory.Delete(inStore, true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
     ZarrWrite.writeStoreV2 inStore mixVars
     ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) mixVars
     let mixSource = sprintf """
import zarr as z

let s = z.load("%s")
let D = s.vars.D |> z.read
let w = z.write("%s", D)
"""
                         inStore outStore
     try
        match lower mixSource with
        | Ok ir ->
            let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_tri_mixed_e2e"
            CodeGen.deployRuntimeHeaders e2eDir
            let cppFile = Path.Combine(e2eDir, "zarr_tri_mixed_e2e.cpp")
            File.WriteAllText(cppFile, cppCode)
            (match compileCpp cppFile e2eDir with
             | Ok exePath ->
                 (match runExecutable exePath with
                  | Ok (0, _) ->
                      check "packed mixed: runs (exit 0)" true ""
                      (match readVarData (Path.Combine(e2eDir, outStore)) "D" with
                       | Ok { DimLengths = dl; Payload = ZFloats got } ->
                           check "packed mixed: pool x trailing roundtrips exactly"
                               (dl = [int card; trail] && got = pool) (sprintf "shape %A" dl)
                       | Ok _ -> check "packed mixed: roundtrip" false "not floats"
                       | Error e -> check "packed mixed: roundtrip" false e)
                  | Ok (code, out) -> check "packed mixed: runs (exit 0)" false ($"exit {code}: {out}")
                  | Error e -> check "packed mixed: runs (exit 0)" false e)
             | Error e ->
                 if isSkipError e then printfn "  SKIP packed mixed e2e (compile skipped): %s" e
                 else check "packed mixed: compiles" false e)
        | Error e -> check "packed mixed: lowers" false e
     with ex -> check "packed mixed e2e" false ex.Message)

    // ---------------------------------------------------------------
    // 18b. spec_version 2: the `orbit` (iterated-wreath) head — metadata
    // ---------------------------------------------------------------
    // Pure JSON/typing probes, hermetic (no g++). The e2e half is 18c.
    printfn "\n--- blade orbit head (spec_version 2): attribute parse ---"
    let orbAttr (spec: int) (levels: string) (extent: int) (extraEntries: string) (layoutAndDecomp: string) =
        sprintf """{"blade": {"spec_version": %d, "layout": %s, "order": "ascending-lex", "index_types": [{"kind": "orbit", "levels": [%s], "extent": %d}%s]}}"""
            spec layoutAndDecomp levels extent extraEntries
    let v2arr (shape: string) =
        sprintf """{"shape":[%s],"chunks":[%s],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}""" shape shape
    // [(2,+),(2,+)] at n = 3 folds 3 -> C(4,2) = 6 -> C(7,2) = 21.
    (match parseArrayMetaV2 "W" "d" (v2arr "21") (Some (orbAttr 2 """[2, "+"], [2, "+"]""" 3 "" "\"packed\"")) with
     | Ok m ->
         check "orbit parse: [(2,+),(2,+)] n=3 -> SymWreath group, 21-cell pool"
             (match m.Blade with
              | Some l ->
                  l.Group.Sym = SymWreath && l.Group.Levels = [(2, true); (2, true)]
                  && l.Group.Extent = 3L && l.Group.Rank = 4 && l.DenseDims = [] && l.Blocks = None
              | None -> false)
             (sprintf "%A" m.Blade)
     | Error e -> check "orbit parse: [(2,+),(2,+)] n=3" false e)
    // The Riemann twin: mixed character, 4 -> C(4,2) = 6 -> C(7,2) = 21.
    (match parseArrayMetaV2 "R" "d" (v2arr "21") (Some (orbAttr 2 """[2, "-"], [2, "+"]""" 4 "" "\"packed\"")) with
     | Ok m ->
         check "orbit parse: [(2,-),(2,+)] n=4 -> 21-cell pool (mixed character)"
             (match m.Blade with
              | Some l -> l.Group.Levels = [(2, false); (2, true)] && l.Group.Extent = 4L
              | None -> false)
             (sprintf "%A" m.Blade)
     | Error e -> check "orbit parse: [(2,-),(2,+)] n=4" false e)
    check "orbit parse: cardinality is the ITERATED fold (packedCardinalityChecked)"
        (packedCardinalityChecked { Sym = SymWreath; Rank = 4; Extent = 3L; Levels = [(2, true); (2, true)] } = Ok 21L
         && packedCardinalityChecked { Sym = SymWreath; Rank = 4; Extent = 4L; Levels = [(2, false); (2, true)] } = Ok 21L
         && packedCardinalityChecked { Sym = SymWreath; Rank = 4; Extent = 4L; Levels = [(2, true); (2, true)] } = Ok 55L) ""
    // THE version gate: a v1 reader must refuse an orbit head loudly. Our own
    // v1 path is that reader, so this pins it rather than assuming it.
    check "orbit parse: kind 'orbit' under spec_version 1 is refused BY NAME"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "21") (Some (orbAttr 1 """[2, "+"], [2, "+"]""" 3 "" "\"packed\""))) "spec_version 2 head") ""
    // One spelling per class on disk.
    check "orbit parse: a DEPTH-1 orbit head is illegal (sym/antisym is the spelling)"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "6") (Some (orbAttr 2 """[2, "+"]""" 3 "" "\"packed\""))) "depth >= 2") ""
    check "orbit parse: an EMPTY level list is illegal"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "3") (Some (orbAttr 2 "" 3 "" "\"packed\""))) "depth >= 2") ""
    check "orbit parse: a rank-1 level is illegal (it normalizes away -> two spellings)"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "21") (Some (orbAttr 2 """[1, "+"], [2, "+"], [2, "+"]""" 3 "" "\"packed\""))) "rank 1 is not >= 2") ""
    check "orbit parse: a malformed sign is refused"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "21") (Some (orbAttr 2 """[2, "+"], [2, "*"]""" 3 "" "\"packed\""))) "is not \"+\" or \"-\"") ""
    check "orbit parse: a missing levels array is refused"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "21")
                     (Some """{"blade": {"spec_version": 2, "layout": "packed", "index_types": [{"kind": "orbit", "extent": 3}]}}""")) "missing its 'levels'") ""
    // Pool-length mismatch: a LOAD ERROR, never a reinterpretation. 20 != 21.
    check "orbit parse: pool length != cardinality is a loud load error"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "20") (Some (orbAttr 2 """[2, "+"], [2, "+"]""" 3 "" "\"packed\""))) "cardinality 21 but the pool dimension is 20") ""
    check "orbit parse: trailing dense dims are refused (not yet supported)"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "21, 2")
                     (Some (orbAttr 2 """[2, "+"], [2, "+"]""" 3 """, {"kind": "dense", "extent": 2}""" "\"packed\""))) "not yet supported") ""
    check "orbit parse: 'packed-blocks' is not defined for an orbit head"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "21") (Some (orbAttr 2 """[2, "+"], [2, "+"]""" 3 "" "\"packed-blocks\""))) "not defined for an orbit") ""
    check "orbit parse: spec_version 3 is still refused (the version gate is closed upward)"
        (isError (parseArrayMetaV2 "W" "d" (v2arr "21") (Some (orbAttr 3 """[2, "+"], [2, "+"]""" 3 "" "\"packed\""))) "spec_version 3 is not supported") ""
    check "orbit parse: sym heads still work under spec_version 2 (superset, not replacement)"
        (match parseArrayMetaV2 "C" "d" (v2arr "10")
                   (Some """{"blade": {"spec_version": 2, "layout": "packed", "index_types": [{"kind": "sym", "rank": 2, "extent": 4}]}}""") with
         | Ok m -> (match m.Blade with Some l -> l.Group.Sym = SymSymmetric && l.Group.Levels = [] | None -> false)
         | Error _ -> false) ""
    check "orbit parse: v3 (zarr.json) carries the same head"
        (match parseArrayMetaV3 "W" "d" """{"zarr_format":3,"node_type":"array","shape":[21],"data_type":"float64","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[21]}},"fill_value":0,"codecs":[{"name":"bytes"}],"attributes":{"blade":{"spec_version":2,"layout":"packed","order":"ascending-lex","index_types":[{"kind":"orbit","levels":[[2,"-"],[2,"+"]],"extent":4}]}}}""" with
         | Ok m -> (match m.Blade with Some l -> l.Group.Sym = SymWreath && l.Group.Levels = [(2, false); (2, true)] | None -> false)
         | Error _ -> false) ""

    printfn "\n--- blade orbit head: module typing + attribute round trip ---"
    (let orbLayout : BladeLayout =
        { Group = { Sym = SymWreath; Rank = 4; Extent = 3L; Levels = [(2, true); (2, true)] }
          DenseDims = []; Blocks = None }
     let orbStore = {
        Path = "/tmp/orb"; Version = 2
        Arrays = [ { Name = "W"; ArrayDir = "/tmp/orb/W"; Shape = [21L]; Chunks = [21L]
                     Dtype = { Code = "f8"; Elem = ETFloat64; ByteSize = 8; IsFloat = true }
                     DimNames = None; FillValue = FillFloat 0.0; Codec = CodecIdentity
                     Blade = Some orbLayout; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" } ] }
     let m = zarrStoreToModule (IRBuilder()) "orb" orbStore None
     let wType =
        m.Types |> List.tryPick (function
            | IRTDStruct ("orb__vars", fields) -> fields |> List.tryPick (fun (n, t) -> if n = "W" then Some t else None)
            | _ -> None)
     // The record must be THE one mkWreathIndexRecord builds — same Rank (raw
     // axes), same IROrbitClass carrier, same "__orbidx" sentinel — because a
     // loaded class and a deduced one have to be the same type, not two records
     // that merely print alike.
     check "orbit typing: W types as the SymWreath record (levels, extent, raw-axis rank, sentinel tag)"
         (match wType with
          | Some (ArrayElem at) ->
              (match at.IndexTypes with
               | [ ix ] ->
                   ix.Symmetry = SymWreath && ix.Rank = 4 && ix.Tag = Some "__orbidx"
                   && ix.IxKind = IxKOrbit
                   && Blade.IR.orbitLevelsOf ix = [(2, true); (2, true)]
                   && Blade.IR.orbitBaseExtent ix = IRLit (IRLitInt 3L)
               | _ -> false)
          | _ -> false)
         (sprintf "%A" wType)
     check "orbit typing: the pool dim does NOT join the module's shareable dimensions"
         (m.Types |> List.forall (fun t -> not t.IsIRTDIndexType)) ""
     // F#-writer round trip: the attribute we emit is the attribute we parse.
     let orbFix = fixStore "zarr_orb_attr"
     (try Directory.Delete(orbFix, true) with _ -> ())
     let orbVars : ZarrWrite.WriteVar list = [
        { Name = "W"; DimNames = None; Shape = [21L]; Chunks = [21L]
          FillValue = FillFloat 0.0
          Data = ZarrWrite.WF64 [| for i in 1 .. 21 -> float i |]; OmitChunks = []
          Blade = Some orbLayout } ]
     ZarrWrite.writeStoreV2 orbFix orbVars
     let attrText = File.ReadAllText (Path.Combine(orbFix, "W", ".zattrs"))
     check "orbit write (F#): the attribute says spec_version 2 + kind orbit + outermost-last levels"
         (attrText.Contains "\"spec_version\": 2"
          && attrText.Contains "\"kind\": \"orbit\""
          && attrText.Contains "\"levels\": [[2, \"+\"], [2, \"+\"]]"
          && attrText.Contains "\"extent\": 3") attrText
     check "orbit write (F#): the store reloads to the SAME layout record"
         (match load orbFix |> fun s -> tryFindArray s "W" with
          | Some m2 -> m2.Blade = Some orbLayout
          | None -> false) ""
     check "orbit write (F#): readPackedPool returns the 21 cells in order"
         (match load orbFix |> fun s -> tryFindArray s "W" |> Option.get |> readPackedPool with
          | Ok { DimLengths = [21]; Payload = ZFloats xs } -> xs = [| for i in 1 .. 21 -> float i |]
          | other -> printfn "    (got %A)" other; false) ""
     // v3 too — the head is version-agnostic.
     let orbFix3 = fixStore "zarr_orb_attr_v3"
     (try Directory.Delete(orbFix3, true) with _ -> ())
     ZarrWrite.writeStoreV3 orbFix3 orbVars
     check "orbit write (F#): v3 store carries and reloads the same head"
         (match load orbFix3 |> fun s -> tryFindArray s "W" with
          | Some m3 -> m3.Blade = Some orbLayout && m3.Version = 3
          | None -> false) "")

    // ---------------------------------------------------------------
    // 18c. spec_version 2 e2e: deduced wreath -> write -> read -> use
    // ---------------------------------------------------------------
    // Both directions of the store boundary, on BOTH backends. Values are
    // hand-derived (corpus 200 / 213 derive them from the kernel definition,
    // not from a pool dump), never a read->write roundtrip: both sides of a
    // roundtrip shift together, so a roundtrip cannot see an order mismatch
    // (plan-orbidx-bijections §3, the antisym storage post-mortem).
    printfn "\n--- blade orbit e2e: deduced wreath -> z.write -> z.read ---"
    /// Compiled stdout for a source, or a skip/failure reason.
    let compiledStdout (label: string) (src: string) : Result<string, string> =
        match lower src with
        | Error e -> Error ($"lower: {e}")
        | Ok ir ->
            let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir label
            CodeGen.deployRuntimeHeaders e2eDir
            let cppFile = Path.Combine(e2eDir, label + ".cpp")
            File.WriteAllText(cppFile, cpp)
            match compileCpp cppFile e2eDir with
            | Error e -> Error e
            | Ok exePath ->
                match runExecutable exePath with
                | Ok (0, out) -> Ok out
                | Ok (code, out) -> Error ($"exit {code}: {out}")
                | Error e -> Error e
    /// Normalized stdout comparison, mirroring InterpDiff.normalize (timing
    /// lines out, CRLF -> LF, trimmed).
    let normOut (s: string) =
        s.Replace("\r\n", "\n").Split('\n')
        |> Array.filter (fun l -> not (l.Contains "completed in") && not (l.Contains "input allocation took"))
        |> Array.map (_.TrimEnd())
        |> String.concat "\n" |> fun t -> t.Trim()
    /// The interpreter walk of the same program (the second backend).
    let interpStdout (label: string) (src: string) : Result<string, string> =
        match lower src with
        | Error e -> Error ($"lower: {e}")
        | Ok ir ->
            let r = Blade.Interp.Run.runProgram ir label Blade.Interp.Value.defaultLimits
            if r.ExitCode = 0 then Ok r.Stdout
            else Error ($"interp exit {r.ExitCode}: {(r.Stderr.Trim())}")
    let orbCases =
        [ // (label, extent, inner reynolds keyword or "", inner kernel, outer
          //  kernel, LEVELS, hand-derived pool, probe (name, subscript, value))
          // 1. The all-'+' tie: S = A(x)A symmetric, W = S + S over the pair-of-pairs
          //    class OrbIdx<[(2,+),(2,+)], 3>. 21 cells; corpus 200's hand-derived list.
          ("orb_tied", 3, "", "x * y", "p + q", [(2, true); (2, true)],
           [| 2.0; 3.0; 4.0; 5.0; 7.0; 10.0; 4.0; 5.0; 6.0; 8.0; 11.0; 6.0; 7.0; 9.0; 12.0; 8.0; 10.0; 13.0; 12.0; 15.0; 18.0 |],
           [ "canonical", "W(0, 1, 0, 1)", 4.0
             "mirrored",  "W(1, 0, 0, 1)", 4.0
             "outer",     "W(2, 2, 0, 0)", 10.0
             "outerSwapped", "W(0, 0, 2, 2)", 10.0
             "bothMirrored", "W(2, 1, 1, 0)", 8.0 ])
          // 2. The Riemann twin: OrbIdx<[(2,-),(2,+)], 4>. Mixed character —
          //    a signed mirror AND a zero set, neither of which the all-'+'
          //    class can exercise. Corpus 213's hand-derived values.
          ("orb_riemann", 4, "Antisymmetric", "2.0 * x + y", "p * q", [(2, false); (2, true)],
           [| 1.0; 2.0; 3.0; 1.0; 2.0; 1.0; 4.0; 6.0; 2.0; 4.0; 2.0; 9.0; 3.0; 6.0; 3.0; 1.0; 2.0; 1.0; 4.0; 2.0; 1.0 |],
           [ "canonical",  "W(0, 1, 2, 3)", 1.0
             "oneMirror",  "W(1, 0, 2, 3)", -1.0
             "bothMirror", "W(1, 0, 3, 2)", 1.0
             "blockSwap",  "W(2, 3, 0, 1)", 1.0
             "crossed",    "W(0, 2, 1, 3)", 4.0
             "zeroFirst",  "W(0, 0, 1, 2)", 0.0
             "zeroSecond", "W(1, 2, 3, 3)", 0.0 ]) ]
    for (label, n, innerSym, innerBody, outerBody, levels, pool, probes) in orbCases do
        // Two independent renderings of the SAME level list: the surface
        // spelling (unused by the write program, which DEDUCES the class — it is
        // here so the case row states what is expected) and the on-disk JSON the
        // attribute must carry, outermost-last in both.
        let levelsJson =
            "[" + (levels |> List.map (fun (r, plus) -> sprintf "[%d, \"%s\"]" r (if plus then "+" else "-")) |> String.concat ", ") + "]"
        let outStore = fixStore ($"zarr_{label}_out")
        (try Directory.Delete(outStore, true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
        let avals = [ for i in 1 .. n -> sprintf "%.1f" (float i) ] |> String.concat ", "
        let innerKernel = if innerSym = "" then "g" else $"reynolds(g, {innerSym})"
        let writeSrc =
            sprintf """
import zarr as z

let A = [%s]
let g = lambda(x, y) where comm(x, y) -> %s
let S = method_for(A, A) <@> %s |> compute
let h = lambda(p, q) where comm(p, q) -> %s
let W = method_for(S, S) <@> h |> compute
let w = z.write("%s", W)
"""
                    avals innerBody innerKernel outerBody outStore
        try
            match lower writeSrc with
            | Error e -> check ($"{label}: write program lowers") false e
            | Ok ir ->
                // The write must go through the ORBIT arm, not the depth-1
                // packed one: a wreath array IS its flat pool, so nothing may
                // reach W through a skeleton (`pool_base(W.data)`) and the
                // packed arm's pool copy (`__pc_pool`, genPackedPoolCopy) must
                // not appear at all.
                //
                // BOTH negatives are SCOPED, and must stay scoped. This used to
                // scan the whole program for the bare word "pool_base", which was
                // only ever true by accident: the program also builds the compact
                // intermediate S, and since 39e0a0c a canonical compact fill
                // reaches its output row through `pool_base(S.data)` instead of
                // the Iliffe skeleton. That is an unrelated array and says nothing
                // about how W is written -- but it turned this into a false red.
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir ($"{label}_write")
                // provider-sink v1 (BLADE_SINK_ALIAS, default on): the writer
                // reads W's flat pool in place through a const pointer -- no
                // flatten buffer, no copy, no delete; the pin stays first.
                check ($"{label}: write reads the flat pool in place + the orb_cell_count pin (no skeleton route for W)")
                    (cppCode.Contains "orb_cell_count" && cppCode.Contains "_flat = &(W[0]);"
                     && not (cppCode.Contains "_flat[__ow_i]") && not (cppCode.Contains "delete[] W_wr")
                     && not (cppCode.Contains "pool_base(W.data)")
                     && not (cppCode.Contains "__pc_pool")) ""
                // ... and with the gate off, the flat pool copy of old.
                let offCode =
                    let prev = Environment.GetEnvironmentVariable "BLADE_SINK_ALIAS"
                    Environment.SetEnvironmentVariable("BLADE_SINK_ALIAS", "0")
                    try fst (CodeGen.genSelfContainedProgramFromIR ir ($"{label}_write_off"))
                    finally Environment.SetEnvironmentVariable("BLADE_SINK_ALIAS", prev)
                check ($"{label}: BLADE_SINK_ALIAS=0 restores the flat pool copy")
                    (offCode.Contains "orb_cell_count" && offCode.Contains "_flat[__ow_i]" && offCode.Contains "delete[] W_wr") ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, $"{label}_write.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Error e ->
                     if isSkipError e then printfn "  SKIP %s e2e (compile skipped): %s" label e
                     else check ($"{label}: write compiles") false e
                 | Ok exePath ->
                 match runExecutable exePath with
                 | Ok (code, out) when code <> 0 ->
                     check ($"{label}: write runs (exit 0)") false ($"exit {code}: {out}")
                 | Error e -> check ($"{label}: write runs (exit 0)") false e
                 | Ok (_, writeOut) ->
                     check ($"{label}: write runs (exit 0)") true ""
                     let written = Path.Combine(e2eDir, outStore)
                     // (a) The pool on disk equals the hand-derived cells, in
                     // order — the independent-oracle comparison, not a roundtrip.
                     (match readVarData written "W" with
                      | Ok { DimLengths = dl; Payload = ZFloats got } ->
                          check ($"{label}: written pool = hand-derived cells (exact, in canonical order)")
                              (dl = [pool.Length] && got = pool)
                              (sprintf "shape %A got %A expected %A" dl got pool)
                      | Ok _ -> check ($"{label}: written pool") false "not floats"
                      | Error e -> check ($"{label}: written pool") false e)
                     // ... and the in-process print agrees, so a shift cannot
                     // hide by moving between the two.
                     // Shape-tolerant flatten (TestHarness). A wreath pool
                     // prints FLAT today (genPrintArrayWreath), so this is a
                     // no-op flatten -- used anyway so a printer-shape change
                     // cannot rot this site the way rank-2 nesting rotted the
                     // packed ones.
                     (match tryParsePrintedFloats "W" writeOut with
                      | Some got ->
                          check ($"{label}: printed pool = written pool") (got = pool) (sprintf "got %A" got)
                      | None -> check ($"{label}: printed pool") false "no parseable W = [...] line")
                     // (b) The attribute: spec_version 2, orbit head, exact
                     // level list and extent.
                     let attrText = File.ReadAllText (Path.Combine(written, "W", ".zattrs"))
                     check ($"{label}: written attribute is spec_version 2 + kind orbit + the exact levels")
                         (attrText.Contains "\"spec_version\": 2"
                          && attrText.Contains "\"kind\": \"orbit\""
                          && attrText.Contains ($"\"extent\": {n}")
                          && attrText.Contains ($"\"levels\": {levelsJson}"))
                         ($"attr {attrText} (wanted levels {levelsJson})")
                     // (c) Reload: the store types back as the same class, and
                     // the RELOADED array serves mirrored / zero-set reads.
                     (try Directory.Delete(outStore, true) with _ -> ())
                     let copyDir (src: string) (dst: string) =
                         for f in Directory.GetFiles(src, "*", SearchOption.AllDirectories) do
                             let rel = Path.GetRelativePath(src, f)
                             let target = Path.Combine(dst, rel)
                             Directory.CreateDirectory (Path.GetDirectoryName target) |> ignore
                             File.Copy(f, target, true)
                     // Compiler cwd copy (metadata + the interpreter's read)
                     // beside the exe-dir copy the binary reads: the same
                     // two-copy scheme every other e2e block uses.
                     copyDir written outStore
                     let probeLines =
                         probes |> List.map (fun (nm, expr, _) -> $"let {nm} = {expr}") |> String.concat "\n"
                     let readSrc =
                         sprintf """
import zarr as z

let s = z.load("%s")
let W = s.vars.W |> z.read
%s
"""
                                 outStore probeLines
                     match compiledStdout ($"{label}_read") readSrc with
                     | Error e ->
                         if isSkipError e then printfn "  SKIP %s read e2e: %s" label e
                         else check ($"{label}: read compiles and runs") false e
                     | Ok readOut ->
                         check ($"{label}: read compiles and runs") true ""
                         let scalarOf (nm: string) : float option =
                             readOut.Split('\n')
                             |> Array.tryPick (fun l ->
                                 let l = l.Trim()
                                 if l.StartsWith (nm + " = ") then
                                     match Double.TryParse(l.Substring((nm + " = ").Length).Trim(),
                                                           Globalization.NumberStyles.Float,
                                                           Globalization.CultureInfo.InvariantCulture) with
                                     | true, v -> Some v
                                     | _ -> None
                                 else None)
                         let missing =
                             probes |> List.choose (fun (nm, _, v) ->
                                 match scalarOf nm with
                                 | Some got when abs (got - v) < 1e-9 -> None
                                 | other -> Some (nm, v, other))
                         check ($"{label}: RELOADED array serves every mirrored / zero-set read")
                             (List.isEmpty missing)
                             (sprintf "wrong/missing %A in:\n%s" missing readOut)
                         // Shape-tolerant flatten (TestHarness) -- see the
                         // write-side W parse above for why.
                         let reloadedPool = tryParsePrintedFloats "W" readOut
                         check ($"{label}: RELOADED array prints the pool in canonical order ({pool.Length} cells)")
                             (reloadedPool = Some pool)
                             (sprintf "got %A expected %A" reloadedPool pool)
                         // (d) BOTH BACKENDS. The interpreter walks the same
                         // store from the compiler cwd copy; its stdout must be
                         // byte-identical to the compiled binary's.
                         (match interpStdout ($"{label}_read") readSrc with
                          | Error e -> check ($"{label}: interpreter reads the orbit store") false e
                          | Ok interpOut ->
                              check ($"{label}: interp stdout == compiled stdout (both backends agree)")
                                  (normOut interpOut = normOut readOut)
                                  ($"interp:\n{(normOut interpOut)}\ncompiled:\n{(normOut readOut)}")))
            with ex -> check ($"{label} e2e") false ex.Message

    // A DEPTH-1 class spelled `OrbIdx<[(2,+)], n>` must write as "sym", not
    // "orbit": it normalizes to the SymIdx record at lowering, so the orbit
    // head never sees it, and the store keeps ONE spelling per class.
    printfn "\n--- blade orbit head: depth-1 OrbIdx writes as sym/antisym ---"
    for (kw, kindStr, cells) in [ ("+", "sym", 6); ("-", "antisym", 3) ] do
        let outStore = fixStore ($"zarr_orb_d1_{kindStr}_out")
        (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
        let symKw = if kw = "+" then "Symmetric" else "Antisymmetric"
        let src =
            sprintf """
import zarr as z

let A = [1.0, 2.0, 3.0]
let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y
let C: Array<Float64 like OrbIdx<[(2,%s)], 3>> = method_for(A, A) <@> reynolds(g, %s) |> compute
let w = z.write("%s", C)
"""
                    kw symKw outStore
        try
            match lower src with
            | Error e -> check ($"orbit depth-1 {kindStr}: lowers") false e
            | Ok ir ->
                let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir ($"zarr_orb_d1_{kindStr}")
                // Pinned on the EMITTED attribute, no run required: the writer
                // bakes the JSON as a literal.
                check ($"orbit depth-1 {kindStr}: emitted attribute says kind '{kindStr}', spec_version 1, NOT orbit")
                    (cpp.Contains ($"kind\\\": \\\"{kindStr}")
                     && cpp.Contains "spec_version\\\": 1"
                     && not (cpp.Contains "\\\"orbit\\\"")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, $"zarr_orb_d1_{kindStr}.cpp")
                File.WriteAllText(cppFile, cpp)
                (match compileCpp cppFile e2eDir with
                 | Error e ->
                     if isSkipError e then printfn "  SKIP orbit depth-1 %s e2e: %s" kindStr e
                     else check ($"orbit depth-1 {kindStr}: compiles") false e
                 | Ok exePath ->
                     match runExecutable exePath with
                     | Ok (0, _) ->
                         check ($"orbit depth-1 {kindStr}: written store is a depth-1 packed head (reload pins it)")
                             (match load (Path.Combine(e2eDir, outStore)) |> fun s -> tryFindArray s "C" with
                              | Some m ->
                                  (match m.Blade with
                                   | Some l ->
                                       l.Group.Levels = []
                                       && l.Group.Sym = (if kw = "+" then SymSymmetric else SymAntisymmetric)
                                       && l.Group.Rank = 2 && m.Shape = [int64 cells]
                                   | None -> false)
                              | None -> false) ""
                     | Ok (code, out) -> check ($"orbit depth-1 {kindStr}: runs") false ($"exit {code}: {out}")
                     | Error e -> check ($"orbit depth-1 {kindStr}: runs") false e)
        with ex -> check ($"orbit depth-1 {kindStr}") false ex.Message

    // ---------------------------------------------------------------
    // 18d. Corrupted orbit stores + the providers that still refuse
    // ---------------------------------------------------------------
    printfn "\n--- blade orbit head: corrupt stores and non-zarr providers refuse ---"
    (let badPool = fixStore "zarr_orb_badpool"
     (try Directory.Delete(badPool, true) with _ -> ())
     // A hand-built store whose pool is one cell short of the class's fold.
     // ZarrWrite would refuse to build the inconsistency, so it is written by
     // hand — which is exactly the shape an external writer's bug takes.
     Directory.CreateDirectory (Path.Combine(badPool, "W")) |> ignore
     File.WriteAllText(Path.Combine(badPool, ".zgroup"), "{\"zarr_format\": 2}")
     File.WriteAllText(Path.Combine(badPool, "W", ".zarray"),
        "{\"zarr_format\": 2, \"shape\": [20], \"chunks\": [20], \"dtype\": \"<f8\", \"compressor\": null, \"fill_value\": 0, \"order\": \"C\", \"filters\": null}")
     File.WriteAllText(Path.Combine(badPool, "W", ".zattrs"),
        "{\"blade\": {\"spec_version\": 2, \"layout\": \"packed\", \"order\": \"ascending-lex\", \"index_types\": [{\"kind\": \"orbit\", \"levels\": [[2, \"+\"], [2, \"+\"]], \"extent\": 3}]}}")
     File.WriteAllBytes(Path.Combine(badPool, "W", "0"), Array.zeroCreate (20 * 8))
     let src = sprintf """
import zarr as z

let s = z.load("%s")
let W = s.vars.W |> z.read
"""
                     badPool
     check "orbit corrupt: a short pool fails the LOAD, loudly, with both numbers"
         (match (try lower src with ex -> Error ex.Message) with
          | Error e -> e.Contains "cardinality 21" && e.Contains "pool dimension is 20"
          | Ok _ -> false)
         (match (try lower src with ex -> Error ex.Message) with Error e -> e | Ok _ -> "lowered"))
    // Depth-1 orbit head on disk: refused at load, not silently read as a
    // SymIdx whose cardinality happens to agree.
    (let d1Store = fixStore "zarr_orb_depth1"
     (try Directory.Delete(d1Store, true) with _ -> ())
     Directory.CreateDirectory (Path.Combine(d1Store, "W")) |> ignore
     File.WriteAllText(Path.Combine(d1Store, ".zgroup"), "{\"zarr_format\": 2}")
     File.WriteAllText(Path.Combine(d1Store, "W", ".zarray"),
        "{\"zarr_format\": 2, \"shape\": [6], \"chunks\": [6], \"dtype\": \"<f8\", \"compressor\": null, \"fill_value\": 0, \"order\": \"C\", \"filters\": null}")
     File.WriteAllText(Path.Combine(d1Store, "W", ".zattrs"),
        "{\"blade\": {\"spec_version\": 2, \"layout\": \"packed\", \"order\": \"ascending-lex\", \"index_types\": [{\"kind\": \"orbit\", \"levels\": [[2, \"+\"]], \"extent\": 3}]}}")
     File.WriteAllBytes(Path.Combine(d1Store, "W", "0"), Array.zeroCreate (6 * 8))
     let src = sprintf """
import zarr as z

let s = z.load("%s")
let W = s.vars.W |> z.read
"""
                     d1Store
     check "orbit corrupt: a depth-1 orbit head on disk is refused (its cardinality WOULD have matched)"
         (match (try lower src with ex -> Error ex.Message) with
          | Error e -> e.Contains "depth >= 2"
          | Ok _ -> false) "")
    // CSV and NetCDF have no wreath pools: both still refuse, by name.
    for (alias, importLine) in [ ("c", "import csv as c"); ("nc", "import netcdf as nc") ] do
        let src =
            sprintf """
%s

let A = [1.0, 2.0, 3.0]
let g = lambda(x, y) where comm(x, y) -> x * y
let S = method_for(A, A) <@> g |> compute
let h = lambda(p, q) where comm(p, q) -> p + q
let W = method_for(S, S) <@> h |> compute
let w = %s.write("generated_cpp_tests/orb_refuse_out", W)
"""
                    importLine alias
        let outcome =
            try
                match lower src with
                | Error e -> e
                | Ok ir ->
                    let (_, _) = CodeGen.genSelfContainedProgramFromIR ir ($"orb_refuse_{alias}")
                    "emitted (no refusal)"
            with ex -> ex.Message
        check ($"orbit refusal: provider '{alias}' still refuses a wreath write")
            (outcome.Contains "stores no OrbIdx pools") outcome

    // ---------------------------------------------------------------
    // 19. Simplex-blocks: math identities (Phase 0, pure)
    // ---------------------------------------------------------------
    printfn "\n--- simplex-blocks: math identities ---"
    (let roundtrip strict n r =
        let count = if strict then binom n r else binom (n + int64 r - 1L) r
        seq { 0L .. count - 1L }
        |> Seq.forall (fun rank ->
            let c = SimplexBlocks.unrankToCoords strict n r rank
            SimplexBlocks.rankOfCoords strict n c = rank)
     check "rank/unrank roundtrip: sym n=5 r=3 (35 cells)" (roundtrip false 5L 3) ""
     check "rank/unrank roundtrip: antisym n=6 r=3 (20 cells)" (roundtrip true 6L 3) "")
    (let sumCells strict n B r =
        let T = (n + B - 1L) / B
        seq { 0L .. SimplexBlocks.blockCount r T - 1L }
        |> Seq.sumBy (fun b ->
            SimplexBlocks.blockCellCount strict n B (SimplexBlocks.unrankToCoords false T r b))
     check "block cells sum to cardinality: sym n=5 B=2 r=2 (ragged tile)"
         (sumCells false 5L 2L 2 = 15L) (string (sumCells false 5L 2L 2))
     check "block cells sum to cardinality: antisym n=5 B=2 r=2"
         (sumCells true 5L 2L 2 = 10L) (string (sumCells true 5L 2L 2))
     check "block cells sum to cardinality: antisym n=4 B=1 r=2 (diagonal blocks EMPTY)"
         (sumCells true 4L 1L 2 = 6L) (string (sumCells true 4L 1L 2)))
    check "antisym B=1: every repeated-tile block is empty (the diagonal specialness)"
        (seq { 0L .. SimplexBlocks.blockCount 2 4L - 1L }
         |> Seq.forall (fun b ->
             let tiles = SimplexBlocks.unrankToCoords false 4L 2 b
             let cells = SimplexBlocks.blockCellCount true 4L 1L tiles
             if tiles.[0] = tiles.[1] then cells = 0L else cells = 1L)) ""
    check "maxBlockCells bounds every block (sym + antisym, n=7 B=3 r=3)"
        (let T = 3L
         seq { 0L .. SimplexBlocks.blockCount 3 T - 1L }
         |> Seq.forall (fun b ->
             let tiles = SimplexBlocks.unrankToCoords false T 3 b
             SimplexBlocks.blockCellCount false 7L 3L tiles <= SimplexBlocks.maxBlockCells 3 3L
             && SimplexBlocks.blockCellCount true 7L 3L tiles <= SimplexBlocks.maxBlockCells 3 3L)) ""
    check "enumBlockCells: counts match closed form + cells canonical (sym n=5 B=2 r=2)"
        (let T = 3L
         seq { 0L .. SimplexBlocks.blockCount 2 T - 1L }
         |> Seq.forall (fun b ->
             let tiles = SimplexBlocks.unrankToCoords false T 2 b
             let cells = SimplexBlocks.enumBlockCells false 5L 2L tiles |> List.ofSeq
             int64 cells.Length = SimplexBlocks.blockCellCount false 5L 2L tiles
             && cells |> List.forall (fun c -> c.[0] <= c.[1]))) ""
    (let bijective strict n B r order =
        let map = SimplexBlocks.blocksCellMap strict n B r order
        let hits = map |> Array.filter (fun p -> p >= 0)
        let card = if strict then binom n r else binom (n + int64 r - 1L) r
        int64 hits.Length = card && (hits |> Array.sort) = [| 0 .. int card - 1 |]
     check "blocksCellMap: bijection onto the pool (sym n=5 B=2)" (bijective false 5L 2L 2 OrderLex) ""
     check "blocksCellMap: bijection onto the pool (antisym n=4 B=1, empty rows)" (bijective true 4L 1L 2 OrderLex) ""
     check "blocksCellMap: bijection under PATH order (sym n=8 B=2, T=4)" (bijective false 8L 2L 2 OrderPath) "")
    check "pathRows: a permutation (r=2, T=4)"
        (let rows = SimplexBlocks.pathRows 2 4L
         (rows |> Array.sort) = [| 0L .. int64 rows.Length - 1L |]) ""
    check "pathRows: non-power-of-two grid refused"
        (try SimplexBlocks.pathRows 2 3L |> ignore; false
         with ex -> ex.Message.Contains "power-of-two") ""
    (let (phys, pool) = SimplexBlocks.paddingReport false 100L 16L 2
     printfn "  INFO: padding sym n=100 B=16 r=2: physical %d cells vs pool %d (%.1f%% overhead)"
         phys pool (100.0 * float (phys - pool) / float pool))

    // ---------------------------------------------------------------
    // 20. Simplex-blocks: store I/O (Phase 1)
    // ---------------------------------------------------------------
    printfn "\n--- simplex-blocks: store write -> load -> pool roundtrip ---"
    let sbLayout sym strict rank n tile order : BladeLayout =
        let T = (n + tile - 1L) / tile
        { Group = { Sym = sym; Rank = rank; Extent = n; Levels = [] }
          DenseDims = []
          Blocks = Some { Tile = tile; Grid = T; Order = order } }
    let sbPool strict n =
        triOracle strict (int n)
    let sbRoundtrip (name: string) (sym, strict) (n: int64) (tile: int64) order (writer: string -> ZarrWrite.WriteVar list -> unit) =
        let layout = sbLayout sym strict 2 n tile order
        let pool = sbPool strict n
        let root = Path.Combine(scratch, name)
        (try Directory.Delete(root, true) with _ -> ())
        writer root [
            { Name = "C"; DimNames = None; Shape = [int64 pool.Length]; Chunks = [int64 pool.Length]
              FillValue = FillFloat -9.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
              Blade = Some layout } ]
        let store = load root
        match tryFindArray store "C" with
        | None -> check ($"{name}: array found") false ""
        | Some m ->
            check ($"{name}: physical shape is [blockCount, tile^r]")
                (match m.Blade with
                 | Some l -> l.Blocks.IsSome && m.Shape.Length = 2
                 | None -> false)
                (sprintf "%A" m.Shape)
            (match readPackedPool m with
             | Ok { DimLengths = [len]; Payload = ZFloats got } ->
                 check ($"{name}: pool roundtrips exactly through block rows")
                     (len = pool.Length && got = pool) ($"len {len}")
             | Ok d -> check ($"{name}: pool roundtrips") false (sprintf "%A" d.DimLengths)
             | Error e -> check ($"{name}: pool roundtrips") false e)
    sbRoundtrip "sb_sym_ragged_v2" (SymSymmetric, false) 5L 2L OrderLex ZarrWrite.writeStoreV2
    sbRoundtrip "sb_antisym_ragged_v2" (SymAntisymmetric, true) 5L 2L OrderLex ZarrWrite.writeStoreV2
    sbRoundtrip "sb_antisym_B1_v2" (SymAntisymmetric, true) 4L 1L OrderLex ZarrWrite.writeStoreV2
    sbRoundtrip "sb_sym_path_v3" (SymSymmetric, false) 8L 2L OrderPath ZarrWrite.writeStoreV3
    // Differential: same pool via flat "packed" and via blocks reads identically.
    (let pool = sbPool false 5L
     let flatRoot = Path.Combine(scratch, "sb_diff_flat")
     (try Directory.Delete(flatRoot, true) with _ -> ())
     ZarrWrite.writeStoreV2 flatRoot [
        { Name = "C"; DimNames = None; Shape = [int64 pool.Length]; Chunks = [int64 pool.Length]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
          Blade = Some { Group = { Sym = SymSymmetric; Rank = 2; Extent = 5L; Levels = [] }; DenseDims = []; Blocks = None } } ]
     let flatPool =
         match load flatRoot |> fun s -> tryFindArray s "C" |> Option.get |> readPackedPool with
         | Ok { Payload = ZFloats xs } -> xs
         | _ -> [||]
     let blocksPool =
         match load (Path.Combine(scratch, "sb_sym_ragged_v2")) |> fun s -> tryFindArray s "C" |> Option.get |> readPackedPool with
         | Ok { Payload = ZFloats xs } -> xs
         | _ -> [||]
     check "differential: flat-packed and simplex-blocks stores read the SAME pool"
         (flatPool = pool && blocksPool = pool) "")
    // Mixed trailing dims through block rows.
    (let n = 3L
     let trail = 2
     let symCells = [ for i in 0 .. int n - 1 do for j in i .. int n - 1 -> (i, j) ]
     let pool = [| for (i, j) in symCells do for t in 0 .. trail - 1 -> float (100 * (i + 1) + 10 * (j + 1) + t) |]
     let layout = { Group = { Sym = SymSymmetric; Rank = 2; Extent = n; Levels = [] }
                    DenseDims = [int64 trail]
                    Blocks = Some { Tile = 2L; Grid = 2L; Order = OrderLex } }
     let root = Path.Combine(scratch, "sb_mixed")
     (try Directory.Delete(root, true) with _ -> ())
     ZarrWrite.writeStoreV2 root [
        { Name = "D"; DimNames = None; Shape = [int64 symCells.Length; int64 trail]; Chunks = [int64 symCells.Length; int64 trail]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
          Blade = Some layout } ]
     match load root |> fun s -> tryFindArray s "D" |> Option.get |> readPackedPool with
     | Ok { DimLengths = [6; 2]; Payload = ZFloats got } ->
         check "mixed sym x dense through block rows roundtrips" (got = pool) ""
     | Ok d -> check "mixed sym x dense through block rows roundtrips" false (sprintf "%A" d.DimLengths)
     | Error e -> check "mixed sym x dense through block rows roundtrips" false e)
    // Parse rejections.
    (let blocksAttr tile grid extra =
        sprintf """{"blade": {"spec_version": 1, "layout": "packed-blocks", "order": "ascending-lex", "index_types": [{"kind": "sym", "rank": 2, "extent": 5}], "decomposition": {"scheme": "simplex-blocks", "tile": %d, "grid": %d%s}}}""" tile grid extra
     let v2phys shape = sprintf """{"shape":[%s],"chunks":[%s],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}""" shape shape
     check "blocks parse: good store accepted (shape [6,4], n=5 B=2)"
         (match parseArrayMetaV2 "C" "d" (v2phys "6, 4") (Some (blocksAttr 2 3 "")) with
          | Ok m -> (match m.Blade with Some l -> l.Blocks = Some { Tile = 2L; Grid = 3L; Order = OrderLex } | None -> false)
          | Error _ -> false) ""
     check "blocks parse: wrong physical shape LOUD"
         (isError (parseArrayMetaV2 "C" "d" (v2phys "5, 4") (Some (blocksAttr 2 3 ""))) "does not match [blockCount") ""
     check "blocks parse: grid/tile mismatch LOUD"
         (isError (parseArrayMetaV2 "C" "d" (v2phys "6, 4") (Some (blocksAttr 2 4 ""))) "does not match ceil") ""
     check "blocks parse: path order on non-power-of-two grid LOUD"
         (isError (parseArrayMetaV2 "C" "d" (v2phys "6, 4") (Some (blocksAttr 2 3 ", \"block_order\": \"path\""))) "power-of-two") ""
     check "blocks parse: unknown scheme LOUD"
         (isError (parseArrayMetaV2 "C" "d" (v2phys "6, 4") (Some """{"blade": {"spec_version": 1, "layout": "packed-blocks", "index_types": [{"kind": "sym", "rank": 2, "extent": 5}], "decomposition": {"scheme": "hilbert", "tile": 2, "grid": 3}}}""")) "hilbert") "")

    // ---------------------------------------------------------------
    // 21. Simplex-blocks: runtime read e2e (Phase 2)
    // ---------------------------------------------------------------
    // A Blade program reads a BLOCKS store and writes a flat "packed" store;
    // F# compares the pools exactly — pinning the emitted per-block
    // reassembly (tile unrank + branch-free bounds + linearize) end to end.
    printfn "\n--- simplex-blocks: runtime read e2e ---"
    let sbE2E (name: string) (sym, strict) (n: int64) (tile: int64) order =
        let layout = sbLayout sym strict 2 n tile order
        let pool = sbPool strict n
        let inStore = fixStore ($"zarr_sb_{name}")
        let outStore = fixStore ($"zarr_sb_{name}_out")
        let vars : ZarrWrite.WriteVar list = [
            { Name = "C"; DimNames = None; Shape = [int64 pool.Length]; Chunks = [int64 pool.Length]
              FillValue = FillFloat -9.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
              Blade = Some layout } ]
        (try Directory.Delete(inStore, true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
        ZarrWrite.writeStoreV2 inStore vars
        ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) vars
        let src = sprintf """
import zarr as z

let s = z.load("%s")
let C = s.vars.C |> z.read
let w = z.write("%s", C)
"""
                      inStore outStore
        try
            match lower src with
            | Ok ir ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir ($"zarr_sb_{name}_e2e")
                check ($"sb e2e {name}: emits per-block reassembly")
                    (cppCode.Contains "simplex-blocks" && cppCode.Contains "symmetric::unlinearize") ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, $"zarr_sb_{name}_e2e.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, _) ->
                          check ($"sb e2e {name}: runs (exit 0)") true ""
                          (match readVarData (Path.Combine(e2eDir, outStore)) "C" with
                           | Ok { Payload = ZFloats got } ->
                               check ($"sb e2e {name}: pool through C++ blocks read == oracle pool")
                                   (got = pool) (sprintf "got %A" (Array.truncate 6 got))
                           | Ok _ -> check ($"sb e2e {name}: pool matches") false "not floats"
                           | Error e -> check ($"sb e2e {name}: pool matches") false e)
                      | Ok (code, out) -> check ($"sb e2e {name}: runs (exit 0)") false ($"exit {code}: {out}")
                      | Error e -> check ($"sb e2e {name}: runs (exit 0)") false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP sb e2e %s (compile skipped): %s" name e
                     else check ($"sb e2e {name}: compiles") false e)
            | Error e -> check ($"sb e2e {name}: lowers") false e
        with ex -> check ($"sb e2e {name}") false ex.Message
    sbE2E "sym" (SymSymmetric, false) 5L 2L OrderLex
    sbE2E "antisym" (SymAntisymmetric, true) 5L 2L OrderLex
    sbE2E "path" (SymSymmetric, false) 8L 2L OrderPath

    // ---------------------------------------------------------------
    // 22. Window reads: z.read_window(var, lo, hi) (Phase 3b)
    // ---------------------------------------------------------------
    printfn "\n--- read_window: sub-simplex window reads ---"
    (let n = 6L
     let winPool =
         [| for i in 2 .. 5 do
              for j in i .. 5 ->
                float ((i + 1) * 10 + (j + 1)) |]
     for (label, blocks) in [ ("blocks", Some { Tile = 2L; Grid = 3L; Order = OrderLex }); ("flat", None) ] do
        let layout : BladeLayout = { Group = { Sym = SymSymmetric; Rank = 2; Extent = n; Levels = [] }; DenseDims = []; Blocks = blocks }
        let pool = sbPool false n
        let inStore = fixStore ($"zarr_win_{label}")
        let outStore = fixStore ($"zarr_win_{label}_out")
        let vars : ZarrWrite.WriteVar list = [
            { Name = "C"; DimNames = None; Shape = [int64 pool.Length]; Chunks = [int64 pool.Length]
              FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
              Blade = Some layout } ]
        (try Directory.Delete(inStore, true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
        ZarrWrite.writeStoreV2 inStore vars
        ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) vars
        let src = sprintf """
import zarr as z

let s = z.load("%s")
let W = z.read_window(s.vars.C, 2, 6)
let w = z.write("%s", W)
"""
                      inStore outStore
        try
            match lower src with
            | Ok ir ->
                check ($"window {label}: spec carries Window=(2,6) and the WINDOW type (extent 4)")
                    (ir.Modules.[0].ProviderReads |> Map.exists (fun _ s ->
                        s.Window = Some (2L, 6L)
                        && (match s.VarType.IndexTypes with
                            | lead :: _ -> (match lead.Extent with IRLit (IRLitInt 4L) -> lead.Symmetry = SymSymmetric | _ -> false)
                            | [] -> false)))
                    ""
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir ($"zarr_win_{label}_e2e")
                check ($"window {label}: emits the extraction pass")
                    (cppCode.Contains "window [2, 6) extraction") ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, $"zarr_win_{label}_e2e.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, _) ->
                          check ($"window {label}: runs (exit 0)") true ""
                          (match readVarData (Path.Combine(e2eDir, outStore)) "W" with
                           | Ok { Payload = ZFloats got } ->
                               check ($"window {label}: window pool == oracle sub-simplex (translated SymIdx<2,4>)")
                                   (got = winPool) (sprintf "got %A" got)
                           | Ok _ -> check ($"window {label}: window pool") false "not floats"
                           | Error e -> check ($"window {label}: window pool") false e)
                          (try
                              let ws = load (Path.Combine(e2eDir, outStore))
                              check ($"window {label}: written window store types as SymIdx<2,4>")
                                  (match tryFindArray ws "W" with
                                   | Some m -> (match m.Blade with Some l -> l.Group.Extent = 4L | None -> false)
                                   | None -> false) ""
                           with ex -> check ($"window {label}: out store loads") false ex.Message)
                      | Ok (code, out) -> check ($"window {label}: runs (exit 0)") false ($"exit {code}: {out}")
                      | Error e -> check ($"window {label}: runs (exit 0)") false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP window %s e2e (compile skipped): %s" label e
                     else check ($"window {label}: compiles") false e)
            | Error e -> check ($"window {label}: lowers") false e
        with ex -> check ($"window {label} e2e") false ex.Message)
    check "window: out-of-range bounds rejected at typecheck"
        ((typeErrOf "import zarr as z\nlet s = z.load(\"tests/fixtures/zarr_stores/zarr_win_blocks\")\nlet W = z.read_window(s.vars.C, 2, 7)\n").Contains "bounds")
        (typeErrOf "import zarr as z\nlet s = z.load(\"tests/fixtures/zarr_stores/zarr_win_blocks\")\nlet W = z.read_window(s.vars.C, 2, 7)\n")
    // The IDE payload agrees: a window read is a provider-read binding, so
    // the fast tier's `providerRead` names the store member (the 3-arg
    // read_window arm of Ide.readOperandProvenance).
    (let ideSrc = "import zarr as z\nlet s = z.load(\"tests/fixtures/zarr_stores/zarr_win_blocks\")\nlet W = z.read_window(s.vars.C, 2, 6)\n"
     let (ideJson, ideCode) = Blade.Ide.ideCheckSource "zarr_win_ide.blade" ideSrc
     check "window: ide payload providerRead = {store s, member vars.C}"
         (ideCode = 0 && ideJson.Contains "\"providerRead\":{\"store\":\"s\",\"member\":\"vars.C\"}")
         (if ideJson.Length > 400 then ideJson.Substring(0, 400) else ideJson))
    // The dims/vars split is Zarr's too (isCoordinateArr), so the same
    // wrong-section mistake is available here -- and BL3018 answers it with
    // the sibling accessor, in both directions. The seam is shared with
    // NetCDF and CSV: one field-access resolution site, not three.
    let wrongSection = """
import zarr as z
let store = z.load("tests/fixtures/zarr_stores/zarr_e2e_v2")
let bad = store.vars.x
"""
    check "wrong section: a coordinate under .vars is refused, naming .dims"
        ((typeErrOf wrongSection).Contains "struct store__vars has no field 'x'"
         && (typeErrOf wrongSection).Contains "store.dims.x")
        (typeErrOf wrongSection)
    let wrongSectionRev = """
import zarr as z
let store = z.load("tests/fixtures/zarr_stores/zarr_e2e_v2")
let bad = store.dims.A
"""
    check "wrong section: a data array under .dims is refused, naming .vars"
        ((typeErrOf wrongSectionRev).Contains "struct store__dims has no field 'A'"
         && (typeErrOf wrongSectionRev).Contains "store.vars.A")
        (typeErrOf wrongSectionRev)
    check "window: dense variables rejected with steering"
        ((typeErrOf "import zarr as z\nlet s = z.load(\"tests/fixtures/zarr_stores/zarr_e2e_v2\")\nlet W = z.read_window(s.vars.A, 0, 2)\n").Contains "PACKED")
        (typeErrOf "import zarr as z\nlet s = z.load(\"tests/fixtures/zarr_stores/zarr_e2e_v2\")\nlet W = z.read_window(s.vars.A, 0, 2)\n")

    // ---------------------------------------------------------------
    // 23. MPI-distributed packed read (Phase 3a; needs mpiexec, skips
    // gracefully). Differential: serial build (gate off) vs mpiexec -n 1/3
    // (gate on) — identical stdout, identical written pool, and the mpi
    // build's read is genuinely rank-scoped (Allgatherv restoration).
    // ---------------------------------------------------------------
    printfn "\n--- zarr mpi: distributed simplex-blocks read (differential) ---"
    (let mpiPool = sbPool false 5L
     let mpiLayout : BladeLayout =
         { Group = { Sym = SymSymmetric; Rank = 2; Extent = 5L; Levels = [] }; DenseDims = []
           Blocks = Some { Tile = 2L; Grid = 3L; Order = OrderLex } }
     let aData = [| for i in 0 .. 11 -> float i * 0.5 |]
     let mpiVars : ZarrWrite.WriteVar list = [
        { Name = "A"; DimNames = Some ["x"; "y"]; Shape = [4L; 3L]; Chunks = [4L; 3L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 aData; OmitChunks = []; Blade = None }
        { Name = "C"; DimNames = None; Shape = [int64 mpiPool.Length]; Chunks = [int64 mpiPool.Length]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 mpiPool; OmitChunks = []
          Blade = Some mpiLayout } ]
     let inStore = fixStore "zarr_mpi_in"
     let outStore = fixStore "zarr_mpi_out"
     let outFull = Path.Combine(e2eDir, outStore)
     (try Directory.Delete(inStore, true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
     ZarrWrite.writeStoreV2 inStore mpiVars
     ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) mpiVars
     let src = sprintf """
import zarr as z

let s = z.load("%s")
let A = s.vars.A |> z.read
let C = s.vars.C |> z.read
let R = method_for(A) <@> lambda(x) where mpi -> x * 2.0 |> compute
let w = z.write("%s", C)
"""
                   inStore outStore
     // Environment first: g++ + a linkable MS-MPI + mpiexec. With these ruled
     // out up front, a failing serial reference below is a real failure.
     if not zCaps.HasGpp then
         printfn "  SKIP zarr mpi differential: g++ not found"
     elif not Blade.Build.hasMpiLink.Value then
         printfn "  SKIP zarr mpi differential: g++ cannot link MS-MPI (-lmsmpi)"
     elif Blade.Build.mpiexecPath.Value.IsNone then
         printfn "  SKIP zarr mpi differential: mpiexec not found"
     else
     try
        try
            // Serial reference (emit gate OFF: the mpi clause is inert).
            CodeGen.setMpiEmitMode false
            // Keep the REASON the serial reference failed. This used to collapse
            // every error to `None` and print one SKIP line, so a lowering
            // error, a compile failure and a crashing oracle were
            // indistinguishable -- and all three silently deleted every zarr-mpi
            // assertion below while the block still reported green.
            let serialOut : Result<string, string> =
                match lower src with
                | Error e -> Error ($"lower: {e}")
                | Ok ir ->
                    let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_mpi_ref"
                    CodeGen.deployRuntimeHeaders e2eDir
                    let f = Path.Combine(e2eDir, "zarr_mpi_ref.cpp")
                    File.WriteAllText(f, cpp)
                    (match compileCpp f e2eDir with
                     | Error e when isSkipError e -> Error e
                     | Error e -> Error ($"compile: {e}")
                     | Ok exe ->
                         (try Directory.Delete(outFull, true) with _ -> ())
                         (match runExecutable exe with
                          | Ok (0, out) -> Ok out
                          | Ok (code, out) -> Error ($"exit {code}: {(out.Substring(0, min 300 out.Length))}")
                          | Error e -> Error e))
            match serialOut with
            | Error e -> baselineFailed "zarr mpi differential" e
            | Ok refOut ->
                (match readVarData outFull "C" with
                 | Ok { Payload = ZFloats got } ->
                     check "zarr mpi: serial reference writes the oracle pool" (got = mpiPool) ""
                 | _ -> check "zarr mpi: serial reference writes the oracle pool" false "")
                // MPI build (emit gate ON).
                CodeGen.setMpiEmitMode true
                match lower src with
                | Error e -> check "zarr mpi: lowers under the emit gate" false e
                | Ok ir ->
                    let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_mpi_e2e"
                    check "zarr mpi: emits the distributed read (rank-scoped + Allgatherv)"
                        (cpp.Contains "distributed simplex-blocks read" && cpp.Contains "MPI_Allgatherv") ""
                    check "zarr mpi: provider write is rank-0 guarded"
                        (cpp.Contains "provider write: rank 0 only") ""
                    CodeGen.deployRuntimeHeaders e2eDir
                    let f = Path.Combine(e2eDir, "zarr_mpi_e2e.cpp")
                    File.WriteAllText(f, cpp)
                    (match compileCpp f e2eDir with
                     | Ok exe ->
                         for ranks in [1; 3] do
                             (try Directory.Delete(outFull, true) with _ -> ())
                             (match runExecutableMpi ranks exe with
                              | Ok (0, out) ->
                                  // Drop the wall-clock timing line — it differs
                                  // by nature between any two runs.
                                  let normalize (s: string) =
                                      s.Split('\n')
                                      |> Array.filter (fun l -> not (l.Contains "completed in"))
                                      |> Array.map (_.TrimEnd())
                                      |> String.concat "\n"
                                      |> fun x -> x.Trim()
                                  // An empty serial reference would make this
                                  // "" = "", a pass with no evidence.
                                  check ($"zarr mpi -n {ranks}: stdout identical to serial")
                                      (not (String.IsNullOrWhiteSpace (normalize refOut))
                                       && normalize out = normalize refOut)
                                      (sprintf "mpi: %s | serial: %s"
                                          (out.Substring(0, min 200 out.Length))
                                          (let r = normalize refOut in if r = "" then "<EMPTY -- nothing to compare>" else r.Substring(0, min 120 r.Length)))
                                  (match readVarData outFull "C" with
                                   | Ok { Payload = ZFloats got } ->
                                       check ($"zarr mpi -n {ranks}: gathered pool == oracle (write from rank 0)")
                                           (got = mpiPool) ""
                                   | _ -> check ($"zarr mpi -n {ranks}: gathered pool == oracle") false "read-back failed")
                              | Ok (code, out) -> check ($"zarr mpi -n {ranks}: runs (exit 0)") false ($"exit {code}: {(out.Substring(0, min 200 out.Length))}")
                              | Error e -> check ($"zarr mpi -n {ranks}: runs (exit 0)") false e)
                     | Error e ->
                         if isSkipError e then printfn "  SKIP zarr mpi e2e (compile skipped): %s" e
                         else check "zarr mpi: compiles" false e)
        finally
            CodeGen.setMpiEmitMode false
     with ex -> check "zarr mpi differential" false ex.Message)

    // ---------------------------------------------------------------
    // 24. Streaming reads (z.stream): fiber reads inlined at the S/T
    // boundary. THE gate is differential: the same program with `.read`
    // (materialize) and `.stream` must produce identical stdout, while the
    // streamed build must show the in-nest fiber reads and NO whole-array
    // materialization.
    // ---------------------------------------------------------------
    printfn "\n--- z.stream: inline fiber reads (differential vs .read) ---"
    (let strmData = [| for i in 0 .. 11 -> float ((i * 7) % 13) + 0.5 |]
     let strmVars : ZarrWrite.WriteVar list = [
        { Name = "A"; DimNames = Some ["s"; "t"]; Shape = [4L; 3L]; Chunks = [2L; 2L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 strmData; OmitChunks = []; Blade = None } ]
     let strmStore = fixStore "zarr_strm"
     (try Directory.Delete(strmStore, true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, strmStore), true) with _ -> ())
     ZarrWrite.writeStoreV2 strmStore strmVars
     ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, strmStore)) strmVars
     // 2D-site store for the fused joint-symmetry case.
     let strm2Data = [| for i in 0 .. 17 -> float ((i * 5) % 11) + 0.25 |]
     let strm2Vars : ZarrWrite.WriteVar list = [
        { Name = "B"; DimNames = Some ["p"; "q"; "t"]; Shape = [3L; 2L; 3L]; Chunks = [2L; 2L; 2L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 strm2Data; OmitChunks = []; Blade = None } ]
     let strm2Store = fixStore "zarr_strm2"
     (try Directory.Delete(strm2Store, true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, strm2Store), true) with _ -> ())
     ZarrWrite.writeStoreV2 strm2Store strm2Vars
     ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, strm2Store)) strm2Vars

     // Compare COMPUTE outputs only: the .read build additionally prints
     // the materialized source array (A/B = [...]), which the streamed
     // build correctly has nothing to print. The timing line differs by
     // nature.
     let normalize (s: string) =
         s.Split('\n')
         |> Array.filter (fun l ->
             not (l.Contains "completed in")
             && not (l.TrimStart().StartsWith "A = [")
             && not (l.TrimStart().StartsWith "B = ["))
         |> Array.map (_.TrimEnd())
         |> String.concat "\n"
         |> fun x -> x.Trim()
     /// Compare the two builds' COMPUTE output, and refuse to call an empty
     /// comparison a match.
     ///
     /// `normalize` deliberately drops the data-bearing `A = [`/`B = [` lines,
     /// because the `.read` build materializes and prints the source array while
     /// the streamed build has nothing there to print -- that is the one
     /// difference the builds are allowed to have. But nothing checked that
     /// ANYTHING survived the filter: if both sides normalized to "", the
     /// assertion `normalize out = normalize refOut` was `"" = ""`, a pass with
     /// zero evidence. This returns false in that case, and additionally
     /// requires the `.read` baseline to have actually PRINTED the source array
     /// it is supposed to materialize -- so the filter is only ever removing
     /// content that was really there.
     let sameCompute (out: string) (refOut: string) : bool * string =
         let a, b = normalize out, normalize refOut
         let refPrintedSource =
             refOut.Split('\n')
             |> Array.exists (fun l ->
                 let t = l.TrimStart()
                 t.StartsWith "A = [" || t.StartsWith "B = [")
         if a = "" || b = "" then
             (false, $"nothing left to compare after normalization (stream={a.Length} chars, read={b.Length} chars); the comparison would be vacuous")
         elif not refPrintedSource then
             (false, ".read baseline never printed the materialized source array (A/B = [...]) -- it did not materialize, so this is not a read-vs-stream differential")
         elif a <> b then (false, $"stream: {a} / read: {b}")
         else (true, "")
     let compileRun (testName: string) (src: string) : Result<string * string, string> =
         match lower src with
         | Error e -> Error ($"lower: {e}")
         | Ok ir ->
             let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir testName
             CodeGen.deployRuntimeHeaders e2eDir
             let f = Path.Combine(e2eDir, testName + ".cpp")
             File.WriteAllText(f, cpp)
             match compileCpp f e2eDir with
             // Preserve the skip marker so baselineFailed can tell a toolchain
             // skip from a genuine compile failure.
             | Error e when isSkipError e -> Error e
             | Error e -> Error ($"compile: {e}")
             | Ok exe ->
                 match runExecutable exe with
                 | Ok (0, out) -> Ok (out, cpp)
                 | Ok (code, out) -> Error ($"exit {code}: {(out.Substring(0, min 300 out.Length))}")
                 | Error e -> Error e
     let differential (label: string) (mkSrc: string -> string) =
         match compileRun ($"strm_{label}_read") (mkSrc "read") with
         | Error e ->
             baselineFailed ($"stream differential {label} (.read baseline)") e
         | Ok (refOut, _) ->
             (match compileRun ($"strm_{label}_stream") (mkSrc "stream") with
              | Error e -> check ($"stream {label}: streamed build runs") false e
              | Ok (out, cpp) ->
                  let (ok, why) = sameCompute out refOut
                  check ($"stream {label}: stdout identical to .read") ok why
                  check ($"stream {label}: fiber buffers + stream prologue emitted")
                      (cpp.Contains "_fb_p" && cpp.Contains "// Stream ") "")

     // (b) cov-like: comm pair over 1D sites, SymIdx<2,4> output.
     differential "cov" (fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("tests/fixtures/zarr_stores/zarr_strm")
let A = sd.vars.A |> z.%s
let m2 = method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) / 3.0 |> compute
"""
                                         verb)
     // (c) skew-like: comm triple, SymIdx<3,4> output.
     differential "skew" (fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("tests/fixtures/zarr_stores/zarr_strm")
let A = sd.vars.A |> z.%s
let m3 = method_for(A, A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>, z2: Array<Float64 like TimeIdx>) where comm(x, y, z2) -> prodsum(x, y, z2) / 3.0 |> compute
"""
                                         verb)
     // (a) mean-like: arity-1 fiber map (skips with a note if the shape
     // isn't supported by the .read baseline either).
     differential "mean" (fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("tests/fixtures/zarr_stores/zarr_strm")
let A = sd.vars.A |> z.%s
let mu = method_for(A) <@> lambda(x: Array<Float64 like TimeIdx>) -> prodsum(x, x) / 3.0 |> compute
"""
                                         verb)
     // (d) 2D sites + time: the user's headline case — joint-symmetric cov
     // over (p, q) sites (fused compound level) with streamed fibers.
     differential "cov2d" (fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("tests/fixtures/zarr_stores/zarr_strm2")
let B = sd.vars.B |> z.%s
let m2 = method_for(B, B) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) / 3.0 |> compute
"""
                                         verb)
     // (g) FUSED trees over provider I/O — the <&>/<&!> baseline: a
     // mean<&!>cov tower (staggered depths) and a cov<&>cov soft join,
     // each differential .read vs .stream. The hard join additionally
     // pins CROSS-LEAF FIBER DEDUP: mean's s1-level fiber IS cov's first
     // argument — the wrapper bind must appear exactly once.
     let fusedTower = fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("tests/fixtures/zarr_stores/zarr_strm")
let A = sd.vars.A |> z.%s
let (mu, m2) = (method_for(A) <@> lambda(x: Array<Float64 like TimeIdx>) -> prodsum(x, x) / 3.0) <&!> (method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) / 3.0) |> compute
"""
                                       verb
     (match compileRun "strm_fused_read" (fusedTower "read") with
      | Error e -> baselineFailed "fused stream differential (.read baseline)" e
      | Ok (refOut, _) ->
          (match compileRun "strm_fused_stream" (fusedTower "stream") with
           | Error e -> check "stream fused <&!>: streamed build runs" false e
           | Ok (out, cpp) ->
               let (ok, why) = sameCompute out refOut
               check "stream fused <&!>: stdout identical to .read" ok why
               let wrapperBinds =
                   cpp.Split('\n')
                   |> Array.filter (fun l -> l.Contains "= { A_fb_p0, A_fiber_ext }")
                   |> Array.length
               check "stream fused <&!>: shared s1 fiber bound ONCE (cross-leaf dedup)"
                   (wrapperBinds = 1) ($"{wrapperBinds} wrapper binds of A_fb_p0")))
     let fusedSoft = fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("tests/fixtures/zarr_stores/zarr_strm")
let A = sd.vars.A |> z.%s
let (m2a, m2b) = (method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) / 3.0) <&> (method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) * 2.0) |> compute
"""
                                      verb
     (match compileRun "strm_soft_read" (fusedSoft "read") with
      | Error e -> baselineFailed "soft-fused stream differential (.read baseline)" e
      | Ok (refOut, _) ->
          (match compileRun "strm_soft_stream" (fusedSoft "stream") with
           | Error e -> check "stream fused <&>: streamed build runs" false e
           | Ok (out, _) ->
               let (ok, why) = sameCompute out refOut
               check "stream fused <&>: stdout identical to .read" ok why))

     // (f) elementwise consumption of a streamed source: ONCE a loud reject
     // (v1's only in-nest read was a whole fiber at a site); now the segment
     // run loop reads one band of rows at a time (docs/plans/structural/07
     // §3.4, zarr lane 10h/10i), so it compiles and says so.
     (let src = """
import zarr as z

let sd = z.load("tests/fixtures/zarr_stores/zarr_strm")
let A = sd.vars.A |> z.stream
let out = method_for(A) <@> lambda(x) -> x + x |> compute
"""
      match lower src with
      | Ok ir ->
          (try
              let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir "strm_elem_bands"
              check "stream: elementwise consumption runs by row bands (was a loud reject)"
                  (cpp.Contains "elementwise over segments" && not (cpp.Contains "not stream-eligible")) ""
           with ex ->
              check "stream: elementwise consumption runs by row bands (was a loud reject)" false ex.Message)
      | Error e -> check "stream: elementwise reject case lowers" false e)
     // (e) netcdf streaming differential (needs sample.nc + libnetcdf).
     if File.Exists "tests/fixtures/sample.nc" then
         (try
             Directory.CreateDirectory(Path.Combine(e2eDir, "tests", "fixtures")) |> ignore
             File.Copy("tests/fixtures/sample.nc", Path.Combine(e2eDir, "tests", "fixtures", "sample.nc"), true)
             let ncDiff (label: string) (mkSrc: string -> string) =
                 match compileRun ($"strm_{label}_read") (mkSrc "read") with
                 // A missing libnetcdf shows up as a link failure (g++), or as
                 // BL2007's "cannot load its native library" when the checker's
                 // compile-time metadata read cannot load the DLL -- both are
                 // genuine environment conditions, recognize them as skips;
                 // everything else is a failure. Previously every error printed
                 // SKIP and deleted the differential.
                 | Error e when e.Contains "-lnetcdf" || e.Contains "netcdf.h"
                                || e.Contains "cannot load its native library" ->
                     printfn "  SKIP nc stream differential: libnetcdf not available for g++ (%s)" e
                 | Error e -> baselineFailed "nc stream differential (.read baseline)" e
                 | Ok (refOut, _) ->
                     (match compileRun ($"strm_{label}_stream") (mkSrc "stream") with
                      | Error e -> check ($"stream {label}: streamed build runs") false e
                      | Ok (out, cpp) ->
                          let (ok, why) = sameCompute out refOut
                          check ($"stream {label}: stdout identical to .read") ok why
                          check ($"stream {label}: nc_get_vara fiber reads inlined")
                              (cpp.Contains "nc_get_vara") "")
             ncDiff "nc_cov" (fun verb -> sprintf """
import netcdf as nc

let sample = nc.load("tests/fixtures/sample.nc")
let A = sample.vars.A |> nc.%s
let m2 = method_for(A, A) <@> lambda(x: Array<Float32 like xdim>, y: Array<Float32 like xdim>) where comm(x, y) -> prodsum(x, y) |> compute
"""
                                              verb)
          // The missing-fixture case is already handled by the File.Exists gate
          // below, so an exception in here is a genuine failure (a throwing
          // codegen, a bad copy, ...) -- not something to swallow as a SKIP.
          with ex -> check "nc stream differential" false ex.Message)
     else
         printfn "  SKIP nc stream differential: sample.nc not found")

    // ---------------------------------------------------------------
    // The provider boundary rule (BL8015) and the masked read, BOTH lanes.
    // A floating Zarr array refuses NaN / +-Inf whether an absent chunk
    // filled them from a NaN fill_value or a written chunk holds them; a
    // FINITE fill_value is a value. ABSOLUTE store path, so the compiled exe
    // (cwd = its own dir) and the interpreter read the same store.
    // ---------------------------------------------------------------
    printfn "\n--- boundary rule (BL8015) + masked read (both lanes) ---"
    (try
        let bDir = Path.Combine(scratch, "boundary")
        Directory.CreateDirectory bDir |> ignore
        let store = Path.Combine(bDir, "zb").Replace("\\", "/")
        (try Directory.Delete(store, true) with _ -> ())
        let wv name dims shape chunks fill data omit : ZarrWrite.WriteVar =
            { Name = name; DimNames = Some dims; Shape = shape; Chunks = chunks; FillValue = fill
              Data = ZarrWrite.WF64 data; OmitChunks = omit; Blade = None }
        ZarrWrite.writeStoreV2 store
            [ // NaN fill, row 1's chunk ABSENT: three NaN cells from the fill.
              wv "P" [ "pr"; "pc" ] [ 2L; 3L ] [ 1L; 3L ] (FillFloat nan) [| 1.5; 2.5; 3.5; 9.0; 9.0; 9.0 |] [ [ 1L; 0L ] ]
              // Finite fill, a WRITTEN NaN: refused too (not only fill).
              wv "Q" [ "q" ] [ 3L ] [ 3L ] (FillFloat 0.0) [| 1.0; nan; 3.0 |] []
              // Finite fill, an absent chunk: the fill is a VALUE.
              wv "R" [ "r" ] [ 4L ] [ 2L ] (FillFloat -1.0) [| 1.0; 2.0; 0.0; 0.0 |] [ [ 1L ] ]
              // Streamed: row 1 holds a +Inf (site 1, cell 1).
              wv "S" [ "sr"; "sc" ] [ 2L; 3L ] [ 1L; 3L ] (FillFloat 0.0) [| 1.0; 2.0; 3.0; 4.0; infinity; 6.0 |] [] ]
        let prog (body: string) = "import zarr as z\nlet zb = z.load(\"" + store + "\")\n" + body + "\n"
        let run (name: string) (body: string) = Blade.Tests.ProviderBoundaryTests.bothLanes e2eDir name [] (prog body)
        (match run "zarr_boundary_fill" "let p = zb.vars.P |> z.read" with
         | Ok r ->
             Blade.Tests.ProviderBoundaryTests.expectRefusal check "zarr: NaN fill_value + absent chunk refused" r
                 [ $"provider read of 'P' from '{store}'"; "3 cell(s) hold a missing value"; "first at [1, 0]"; "NaN or infinite (fill_value NaN)" ]
         | Error e -> check "zarr boundary fill: lowers" false e)
        (match run "zarr_boundary_written" "let q = zb.vars.Q |> z.read" with
         | Ok r ->
             Blade.Tests.ProviderBoundaryTests.expectRefusal check "zarr: a WRITTEN NaN under a finite fill refused" r
                 [ "provider read of 'Q'"; "1 cell(s) hold a missing value"; "first at [1]" ]
         | Error e -> check "zarr boundary written: lowers" false e)
        (match run "zarr_boundary_finite" "let rv = zb.vars.R |> z.read" with
         | Ok r ->
             Blade.Tests.ProviderBoundaryTests.expectValues check "zarr: a finite fill_value stays a value" r
                 [ "rv = [1.0, 2.0, -1.0, -1.0]" ]
         | Error e -> check "zarr boundary finite: lowers" false e)
        (match run "zarr_boundary_present" "let t = zb.vars.P |> z.read_present\nlet ok = zb.vars.P |> z.present\nlet n = extents(t)\nlet total = reduce(t, (+))" with
         | Ok r ->
             Blade.Tests.ProviderBoundaryTests.expectValues check "zarr: masked read (read_present / present)" r
                 [ "n = 3"; "total = 7.5"; "ok = [[true, true, true], [false, false, false]]" ]
             // Each masked-read binding is ONE raw read of the store (no second
             // reader mode): `t` and `ok` are two bindings, so two reads.
             check "zarr: each masked-read binding reads the store once (2 bindings, 2 raw reads)"
                 ((r.Cpp.Split([| "_flat = new" |], StringSplitOptions.None)).Length = 3) ""
         | Error e -> check "zarr masked read: lowers" false e)
        (match run "zarr_boundary_stream" "let A = zb.vars.S |> z.stream\nlet s = method_for(A) <@> lambda(x: Array<Float64 like sc>) -> reduce(x, (+)) |> compute" with
         | Ok r ->
             (match r.Compiled with
              | None -> printfn "  SKIP zarr stream refusal: toolchain unavailable"
              | Some (Ok (code, out)) ->
                  check "zarr: a streamed fiber holding +Inf refuses per fiber (BL8015, site coordinates)"
                      (code <> 0 && out.Contains "error[BL8015]: provider read of 'S'" && out.Contains "first at [1, 1]") out
              | Some (Error e) -> check "zarr: stream refusal runs" false e)
         | Error e -> check "zarr boundary stream: lowers" false e)
        (match run "zarr_boundary_write" ("let a = [1.0, 2.0]\nlet bad = method_for(a) <@> lambda(x) -> x / (x - x) |> compute\nlet _ = z.write(\"" + store + "_out\", bad)") with
         | Ok r ->
             (match r.Compiled with
              | None -> printfn "  SKIP zarr write refusal: toolchain unavailable"
              | Some (Ok (code, out)) ->
                  check "zarr: a write of infinite cells is refused (BL8015)"
                      (code <> 0 && out.Contains "error[BL8015]: provider write of 'bad'" && out.Contains "2 cell(s) are NaN or infinite") out
              | Some (Error e) -> check "zarr: write refusal runs" false e)
         | Error e -> check "zarr boundary write: lowers" false e)
        // provider-sink v1: a PACKED write reads the pool in place (no flatten
        // buffer); the BL8015 scan of that pool still runs BEFORE any file
        // effect, so nothing of the store is published.
        (let outStore = store + "_packed_out"
         (try Directory.Delete(outStore, true) with _ -> ())
         // A PRE-EXISTING destination: the refusal must leave it untouched.
         Directory.CreateDirectory outStore |> ignore
         let sentinel = Path.Combine(outStore, "sentinel.txt")
         File.WriteAllText(sentinel, "untouched")
         // Pool order (0,0) (0,1) (0,2) (1,1) (1,2) (2,2) over a = [1, 2, 3]:
         // a cell is infinite exactly when one of its coordinates holds 2.0,
         // so 3 cells, the first at flat cell 1 (a finite prefix before it).
         match run "zarr_boundary_write_packed" ("let a = [1.0, 2.0, 3.0]\nlet g = lambda(p, q) where comm(p, q) -> p / (q - 2.0)\nlet bad = method_for(a, a) <@> reynolds(g, Symmetric) |> compute\nlet _ = z.write(\"" + outStore + "\", bad)") with
         | Ok r ->
             check "zarr: a packed write reads the pool in place (provider-sink v1)"
                 (r.Cpp.Contains "_flat = nested_array_utilities::pool_base(bad.data);" && not (r.Cpp.Contains "delete[] bad_wr")) ""
             (match r.Compiled with
              | None -> printfn "  SKIP zarr packed write refusal: toolchain unavailable"
              | Some (Ok (code, out)) ->
                  check "zarr: a packed write of infinite cells is refused (BL8015: count and first flat cell) and leaves an existing destination untouched"
                      (code <> 0 && out.Contains "error[BL8015]: provider write of 'bad'" && out.Contains "3 cell(s) are NaN or infinite (first at flat cell 1)"
                       && File.ReadAllText sentinel = "untouched"
                       && not (File.Exists (Path.Combine(outStore, ".zgroup")))
                       && not (Directory.Exists (Path.Combine(outStore, "bad")))) out
              | Some (Error e) -> check "zarr: packed write refusal runs" false e)
         | Error e -> check "zarr boundary packed write: lowers" false e)
        (match lower (prog "let static V = zb.vars.Q |> z.read") with
         | Ok _ -> check "zarr: static fold of a NaN cell refused" false "lowered without error"
         | Error e ->
             check "zarr: static fold refuses with the BL8015 sentence (the `let static` assertion)"
                 (e.Contains "does not evaluate at compile time" && e.Contains "provider read of 'Q'" && e.Contains "first at [1]") e)
     with ex -> check "zarr boundary rule: no unexpected exception" false ex.Message)

    // Cleanup the temp scratch (e2e stores stay in generated_cpp_tests
    // beside the .cpp files, like the netcdf fixtures).
    (try Directory.Delete(scratch, true) with _ -> ())

    // ---------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------
    printFooter "Zarr Provider" [$"{passed} passed"; $"{failed} failed"]
    if failed > 0 then 1 else 0
