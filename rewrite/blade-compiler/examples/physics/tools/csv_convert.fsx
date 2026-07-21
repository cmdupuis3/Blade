// One-off conversion: extract the big embedded array literals from the
// large physics examples into examples/physics/data/*.csv and rewrite the
// bindings as CSV provider loads (import csv as csvd). Tokens are copied
// VERBATIM from the literals — the CSV payload is bit-identical to what the
// .blade source carried, so every existing // EXPECT: pin doubles as the
// conversion oracle.
//
// Run from examples/physics:  dotnet fsi tools/csv_convert.fsx
// Idempotent-ish: skips a file whose targets are already converted.
open System
open System.IO

let physDir = __SOURCE_DIRECTORY__ |> Path.GetDirectoryName   // examples/physics
let dataDir = Path.Combine(physDir, "data")
Directory.CreateDirectory dataDir |> ignore

/// (file, prefix, binding names to convert)
let targets = [
    "42_dynamical_q.blade", "42", ["EC"; "VC"; "EI"; "VI"]
    "30_observer_free_noise.blade", "30", ["XA"; "XB"; "XH"; "XC"]
    "31_free_deconvolution.blade", "31", ["DIG"]
    "43_cleaning_the_spikes.blade", "43", ["ZT"; "GT"]
    "17_spectral_persistence_torus_vs_chaos.blade", "17", ["REG"; "CHA"]
]

/// Find `let NAME: TYPE = [ ... ]` in text: returns (startIdx, endIdx-exclusive,
/// typeAnnotation, literalBody) where literalBody excludes the outer brackets.
let findBinding (text: string) (name: string) =
    let decl = sprintf "let %s:" name
    let s = text.IndexOf decl
    if s < 0 then None
    else
        let eq = text.IndexOf('=', s)
        let openB = text.IndexOf('[', eq)
        let ty = text.Substring(s + decl.Length, eq - s - decl.Length).Trim()
        let mutable depth = 0
        let mutable i = openB
        let mutable closeB = -1
        while closeB < 0 && i < text.Length do
            (match text.[i] with
             | '[' -> depth <- depth + 1
             | ']' -> depth <- depth - 1; if depth = 0 then closeB <- i
             | _ -> ())
            i <- i + 1
        if closeB < 0 then failwithf "%s: unbalanced literal" name
        // Extend end past a trailing newline so the replacement splices clean.
        let mutable e = closeB + 1
        while e < text.Length && (text.[e] = '\r' || text.[e] = '\n') do e <- e + 1
        Some (s, e, ty, text.Substring(openB + 1, closeB - openB - 1))

/// Literal body -> rows of verbatim numeric tokens. 2-D iff it contains '['.
let tokenize (body: string) : string list list =
    if body.Contains "[" then
        // Rows are depth-1 [ ... ] groups.
        let rows = ResizeArray<string>()
        let mutable depth = 0
        let mutable start = -1
        body |> Seq.iteri (fun i ch ->
            match ch with
            | '[' ->
                depth <- depth + 1
                if depth = 1 then start <- i + 1
            | ']' ->
                if depth = 1 then rows.Add (body.Substring(start, i - start))
                depth <- depth - 1
            | _ -> ())
        rows |> List.ofSeq |> List.map (fun r ->
            r.Split(',') |> Array.map (fun t -> t.Trim()) |> Array.filter ((<>) "") |> List.ofArray)
    else
        // 1-D: ONE CSV row (re-loads as 1 x N; the rewrite extracts row 0).
        [ body.Split(',') |> Array.map (fun t -> t.Trim()) |> Array.filter ((<>) "") |> List.ofArray ]

for (file, prefix, names) in targets do
    let path = Path.Combine(physDir, file)
    let original = File.ReadAllText path
    if original.Contains "import csv as csvd" then
        printfn "%s: already converted, skipped" file
    else
        let mutable text = original
        let mutable firstDeclPos = Int32.MaxValue
        for name in names do
            match findBinding text name with
            | None -> failwithf "%s: binding %s not found" file name
            | Some (s, e, ty, body) ->
                let rows = tokenize body
                let is1D = (body.Contains "[") |> not
                let csvName = sprintf "%s_%s.csv" prefix name
                File.WriteAllText(
                    Path.Combine(dataDir, csvName),
                    (rows |> List.map (String.concat ",") |> String.concat "\n") + "\n")
                let replacement =
                    if is1D then
                        // 1 x N row; partial index (0) recovers the vector.
                        sprintf "let %s_h = csvd.load(\"data/%s\")\nlet %s_m = %s_h.vars.data |> csvd.read\nlet %s: %s = %s_m(0)\n"
                            name csvName name name name ty name
                    else
                        sprintf "let %s_h = csvd.load(\"data/%s\")\nlet %s: %s = %s_h.vars.data |> csvd.read\n"
                            name csvName name ty name
                text <- text.Substring(0, s) + replacement + text.Substring e
                firstDeclPos <- min firstDeclPos s
                printfn "%s: %s -> data/%s (%d row(s) x %d)" file name csvName rows.Length rows.Head.Length
        // Import immediately before the first converted binding (after all
        // header comments, before first use).
        text <- text.Substring(0, firstDeclPos) + "import csv as csvd\n" + text.Substring firstDeclPos
        File.WriteAllText(path, text)
        printfn "%s: rewritten (%d -> %d chars)" file original.Length text.Length
