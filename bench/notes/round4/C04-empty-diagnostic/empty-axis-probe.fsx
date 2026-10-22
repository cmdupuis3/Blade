// Runner-only diagnostic against the exact existing C04 attempt3 assembly.
// This script lowers and interprets; the C coder must not execute it.
open System
open System.IO
open System.Collections
open Microsoft.FSharp.Reflection
open Blade
open Blade.IR

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 1 then failwith "usage: empty-axis-probe.fsx <output-directory>"
let outDir = Path.GetFullPath args.[0]
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

// Exact strings and transformations from transposeRowsInterpreterCases.
let primary =
    "type R = Idx<2>\ntype C = Idx<3>\n"
    + "let M: Array<Float64 like R, C> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n"
    + "let rows: Array<Float64 like C> = reduce(transpose(M, [0, 1]), lambda(a: Float64, b: Float64) -> a * 10.0 + b, 7.0)\n"
let cases =
    [ ("R0", primary.Replace("Idx<2>", "Idx<0>").Replace("[[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]", "[]").Replace("7.0)", "-7.25)"));
      ("negative_zero", primary.Replace("Idx<2>", "Idx<0>").Replace("[[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]", "[]").Replace("7.0)", "-0.0)")) ]
let gates =
    [ ("BLADE_PRINT", ""); ("BLADE_FP_REPRO", "1"); ("BLADE_FP_CONTRACT", "off");
      ("BLADE_FUSION", "on"); ("BLADE_BLAS", "off"); ("BLADE_OMP_THREADS", "1") ]
let old = gates |> List.map (fun (key, _) -> key, Environment.GetEnvironmentVariable key)
let pendingDumps = ResizeArray<string * IRProgram>()
printfn "ASSEMBLY %s" typeof<IRProgram>.Assembly.Location
try
    for key, value in gates do Environment.SetEnvironmentVariable(key, value)
    for label, source in cases do
        write (label + ".blade") source
        for fusion in ["on"; "0"] do
            Environment.SetEnvironmentVariable("BLADE_FUSION", fusion)
            let stem = label + "-" + fusion
            printfn "=== %s ===" stem
            for key, _ in gates do printfn "ENV %s=%A" key (Environment.GetEnvironmentVariable key)
            Blade.Runtime.runOnLargeStack (fun () ->
                match Lowering.lower source with
                | Error error -> failwithf "%s lower refused: %s" stem error
                | Ok ir ->
                    match IRValidate.validateIR ir with
                    | Error errors -> failwithf "%s validation refused: %s" stem (String.concat "; " errors)
                    | Ok valid ->
                        let route = valid.Modules |> List.exists (fun m -> m.Functions |> List.exists (fun f -> f.Name.StartsWith "__transpose_rows_"))
                        let result = Blade.Interp.Run.runProgram valid ("transpose_rows_interp_independent_on_off_" + label) Blade.Interp.Value.defaultLimits
                        write (stem + ".stdout.raw.txt") result.Stdout
                        write (stem + ".stderr.raw.txt") result.Stderr
                        write (stem + ".status.txt") (string result.ExitCode + "\n")
                        printfn "ROUTED %b EXIT %d" route result.ExitCode
                        printfn "STDOUT_ESCAPED %A" result.Stdout
                        printfn "STDERR_ESCAPED %A" result.Stderr
                        printfn "STDOUT_RAW_BEGIN\n%s\nSTDOUT_RAW_END" result.Stdout
                        printfn "STDERR_RAW_BEGIN\n%s\nSTDERR_RAW_END" result.Stderr
                        pendingDumps.Add((stem, ir)))
    // Persist all four primary results before any optional IR collection fails.
    for stem, ir in pendingDumps do
        dumpFull (Path.Combine(outDir, stem + ".typed-ir.txt")) (box ir)
finally
    for key, value in old do Environment.SetEnvironmentVariable(key, value)
