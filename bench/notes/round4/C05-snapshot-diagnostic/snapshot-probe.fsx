// Runner-only source diagnostic. No native compilation/execution or new build.
open System
open System.IO
open System.Collections
open Microsoft.FSharp.Reflection
open Blade
open Blade.IR

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 1 then failwith "usage: snapshot-probe.fsx <fresh-output-directory>"
let outDir = Path.GetFullPath args.[0]
if Directory.Exists outDir then failwith "output directory must be fresh; preserve prior evidence"
Directory.CreateDirectory outDir |> ignore
let utf8 = System.Text.UTF8Encoding(false)
let write (name: string) (text: string) = File.WriteAllText(Path.Combine(outDir, name), text, utf8)

// Exhaustive reflected IR dump: unlike %A, no collection/depth truncation.
let dumpFull (path: string) (value: obj) =
    use writer = new StreamWriter(path, false, utf8)
    let rec dump depth (v: obj) =
        let line text = writer.WriteLine(String.replicate depth "  " + text)
        if isNull v then line "null (including None/empty-list union representation)"
        else
            let t = v.GetType()
            if t = typeof<string> || t.IsPrimitive || t.IsEnum || t = typeof<decimal> then
                line (sprintf "%s = %A" t.FullName v)
            elif v :? SrcLoc then
                line t.FullName
                line "Span"
                dump (depth + 1) (box ((v :?> SrcLoc).Span))
            elif FSharpType.IsRecord t then
                line t.FullName
                Array.zip (FSharpType.GetRecordFields t) (FSharpValue.GetRecordFields v)
                |> Array.iter (fun (field, child) -> line (field.Name + ": " + field.PropertyType.ToString()); dump (depth + 1) child)
            elif FSharpType.IsTuple t then
                line t.FullName
                FSharpValue.GetTupleFields v |> Array.iter (dump (depth + 1))
            elif FSharpType.IsUnion t then
                let case, children = FSharpValue.GetUnionFields(v, t)
                line (t.FullName + "." + case.Name)
                Array.zip (case.GetFields()) children
                |> Array.iter (fun (field, child) -> line (field.Name + ": " + field.PropertyType.ToString()); dump (depth + 1) child)
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.KeyValuePair<_, _>> then
                line t.FullName
                dump (depth + 1) (t.GetProperty("Key").GetValue v)
                dump (depth + 1) (t.GetProperty("Value").GetValue v)
            elif v :? IEnumerable then
                line t.FullName
                for child in (v :?> IEnumerable) do dump (depth + 1) child
            else failwithf "unhandled full-IR dump type %s" t.FullName
    dump 0 value


// Exact strings/transform from sparseWildcardValuesAndRefusals, not a rewrite.
let primary =
    "let static K = [(4, 1), (0, 3), (2, 1), (1, 0), (0, 1)]\n"
    + "let vals: Array<Float64 like Idx<5>> = [4.0, 9.0, 2.0, 8.0, 3.0]\n"
    + "let S = sparse(vals, K)\n"
    + "let total = reduce(S((_, 1)), lambda(a: Float64, b: Float64) -> a * 10.0 + b, 7.0)\n"
let beforeTotal = primary.Substring(0, primary.IndexOf("let total =", StringComparison.Ordinal))
let snapshot =
    beforeTotal.Replace("let S =", "let mut S =")
    + "function snapshot(p: mut Array<Float64 like SparseIdx<K>>) -> Float64 = {\n"
    + "    let residual = p((_, 1))\n    p((4, 1)) = 99.0\n"
    + "    reduce(residual, lambda(a: Float64, b: Float64) -> a * 10.0 + b, 7.0)\n}\n"
    + "let total = snapshot(S)\nlet after = S((4, 1))\n"
let corpusPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../../..", "tests/corpus/sql-reduce/050_sparse_private_snapshot_mutation.blade"))
let cases =
    [ ("synthetic", "sparse_wildcard_canonical_interpreter_and_cpp_snapshot_mutation", snapshot);
      ("corpus050", "C05 census private sparse residual snapshot across parent mutation", File.ReadAllText corpusPath) ]
// Exact withTransposeRows/withPartialRows environment; inherited other gates
// are reported, not silently changed. Both inputs run once, with FUSION=on.
let gates =
    [ ("BLADE_PRINT", ""); ("BLADE_FP_REPRO", "1"); ("BLADE_FP_CONTRACT", "off");
      ("BLADE_FUSION", "on"); ("BLADE_BLAS", "off"); ("BLADE_OMP_THREADS", "1") ]
let old = gates |> List.map (fun (key, _) -> key, Environment.GetEnvironmentVariable key)
let pendingDumps = ResizeArray<string * IRProgram>()
let failures = ResizeArray<string>()
printfn "ASSEMBLY %s" typeof<IRProgram>.Assembly.Location
printfn "CORPUS %s" corpusPath
try
    for key, value in gates do Environment.SetEnvironmentVariable(key, value)
    for key in ["BLADE_PRINT"; "BLADE_FP_REPRO"; "BLADE_FP_REASSOC"; "BLADE_FP_CONTRACT"; "BLADE_FUSION"; "BLADE_BLAS"; "OPENBLAS_DIR"; "BLADE_OMP_THREADS"; "BLADE_SIBLING_FOLDS"; "BLADE_COMPACTION_FUSION"] do
        printfn "ENV %s=%A" key (Environment.GetEnvironmentVariable key)
    for stem, testName, source in cases do
        write (stem + ".blade") source
        try
            Runtime.runOnLargeStack (fun () ->
                Effects.Decisions.start ()
                match Lowering.lower source with
                | Error error ->
                    write (stem + ".lower-status.txt") ("ERROR\n" + error)
                    failwithf "%s lower refused: %s" stem error
                | Ok ir ->
                    write (stem + ".lower-status.txt") "OK\n"
                    match IRValidate.validateIR ir with
                    | Error errors ->
                        write (stem + ".validate-status.txt") ("ERROR\n" + String.concat "\n" errors)
                        failwithf "%s validation refused" stem
                    | Ok _ ->
                        write (stem + ".validate-status.txt") "OK\n"
                        // Match test ordering: C++ source generation precedes runProgram.
                        // This only generates text; no compiler/native process is used.
                        let cpp = fst (CodeGen.genSelfContainedProgramFromIR ir testName)
                        write (stem + ".cpp") cpp
                        let decisions = Effects.Decisions.drain () |> List.map Effects.Decisions.render |> String.concat "\n"
                        write (stem + ".decisions.txt") (decisions + "\n")
                        let run = Blade.Interp.Run.runProgram ir testName Blade.Interp.Value.defaultLimits
                        write (stem + ".stdout.raw.txt") run.Stdout
                        write (stem + ".stderr.raw.txt") run.Stderr
                        write (stem + ".status.txt") (string run.ExitCode + "\n")
                        printfn "CASE %s EXIT %d\nSTDOUT_ESCAPED %A\nSTDERR_ESCAPED %A" stem run.ExitCode run.Stdout run.Stderr
                        pendingDumps.Add((stem, ir))
                        let proofLines =
                            [ for m in ir.Modules do
                                for b in m.Bindings do
                                    if b.Name = "total" then
                                        yield sprintf "HOST module=%s name=%s id=%d value=%s" m.Name b.Name b.Id (IRPrint.ppIRExpr 0 b.Value)
                                        match b.Value with
                                        | IRReduce (a, k, s) ->
                                            match Optimize.certifySparseWildcardFold (Some m) b a k s with
                                            | Error why -> yield "CERTIFICATE DECLINED " + why
                                            | Ok p -> yield sprintf "CERTIFICATE ADMITTED parent=%d pinAxis=%d pin=%d fold=%d" p.ParentId p.PinAxis p.Pin p.Fold.Id
                                        | _ -> yield "CERTIFICATE NOT_APPLICABLE: actual ordinary host is not a direct IRReduce; function-expression fallback retained" ]
                        write (stem + ".certificate.txt") (String.concat "\n" proofLines + "\n"))
        with ex ->
            let error = ex.ToString()
            write (stem + ".collector-error.txt") error
            failures.Add(stem + ": " + error)
    // Save BOTH raw results before reflection can fail. Accepted C04 walker
    // explicitly serializes SrcLoc.Span and refuses unknown class payloads.
    for stem, ir in pendingDumps do
        dumpFull (Path.Combine(outDir, stem + ".typed-ir.txt")) (box ir)
    if failures.Count > 0 then failwith (String.concat "\n" failures)
finally
    for key, value in old do Environment.SetEnvironmentVariable(key, value)
