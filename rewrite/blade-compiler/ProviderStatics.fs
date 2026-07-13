/// Provider-backed statics: the bridge that lets
/// `let static A = sample.vars.A |> read` FOLD the file's payload at
/// compile time — staging contract clause 1 ("inputs are immutable values
/// within a program's scope; fold freely"). Layering: NetcdfProvider
/// compiles before StaticEval and neither may reference the other, so the
/// reader is REGISTERED into StaticEval's provider hook from here
/// (installed by TypeCheck.typeCheck, ahead of every resolveStatics pass —
/// the PPL elaboration's own statics inherit the fold for free).
///
/// Operational honesty is PROVENANCE, not freshness: each fold records
/// (path, variable, sha256) — "this executable = program · file@hash" —
/// and prints a provenance note at compile time. The hash log is also the
/// future memoization key for incremental compile-time folds.
module Blade.ProviderStatics

open Blade.StaticEval

/// (file path, variable name, sha256 hex) per compile-time fold.
let provenance = ResizeArray<string * string * string>()

let private hashCache = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

let private fileHash (path: string) : string =
    hashCache.GetOrAdd(path, fun p ->
        use sha = System.Security.Cryptography.SHA256.Create()
        sha.ComputeHash(System.IO.File.ReadAllBytes p)
        |> Array.map (sprintf "%02x")
        |> String.concat "")

/// Fold ceiling in elements. Beyond it the fold refuses with steering:
/// "large and closed" inputs belong to the runtime/streaming schedule
/// (a multi-million-element C++ literal would sink the C++ compiler),
/// per the fold/residualize/stream table in ppl/NOTES.md.
let foldCeiling = 65536

/// Shape a flat row-major buffer into nested SVTuples by dim extents
/// (a rank-0 variable folds to its bare scalar).
let shapeValue (lens: int list) (leaf: int -> StaticValue) : StaticValue =
    let rec go (lens: int list) (offset: int) : StaticValue * int =
        match lens with
        | [] -> (leaf offset, offset + 1)
        | n :: rest ->
            let mutable off = offset
            let items =
                [ for _ in 1 .. n ->
                    let (v, off') = go rest off
                    off <- off'
                    v ]
            (SVTuple items, off)
    fst (go lens 0)

/// Fold memoization: the pipeline runs several resolveStatics passes per
/// compilation (checkModule, the ML and PPL elaborations, lowering's
/// Phase 0) — the payload is read and provenance recorded ONCE. Keyed on
/// (path, var, mtime) so a long-lived compiler process re-reads when the
/// file actually changed between compilations.
let private foldCache =
    System.Collections.Concurrent.ConcurrentDictionary<string * string * int64, Result<StaticValue, string>>()

let private readAndFoldUncached (path: string) (varName: string) : Result<StaticValue, string> =
    match Blade.NetcdfProvider.readVarData path varName with
    | Error e ->
        Error (sprintf "provider fold of '%s' from '%s' failed: %s" varName path e)
    | Ok data ->
        let count = data.DimLengths |> List.fold (*) 1
        if count > foldCeiling then
            Error (sprintf "'%s' has %d elements — beyond the %d-element fold ceiling; large closed inputs take the runtime schedule (bind with a plain `let ... |> read`)" varName count foldCeiling)
        else
            let h = fileHash path
            provenance.Add((path, varName, h))
            eprintfn "[provenance] folded %s from %s@%s" varName path (h.Substring(0, min 12 h.Length))
            match data.Payload with
            | Blade.NetcdfProvider.NcFloats xs -> Ok (shapeValue data.DimLengths (fun i -> SVFloat xs.[i]))
            | Blade.NetcdfProvider.NcInts xs -> Ok (shapeValue data.DimLengths (fun i -> SVInt xs.[i]))

let private readAndFold (path: string) (varName: string) : Result<StaticValue, string> =
    let mtime =
        try System.IO.File.GetLastWriteTimeUtc(path).Ticks
        with _ -> 0L
    foldCache.GetOrAdd((path, varName, mtime), fun _ -> readAndFoldUncached path varName)

/// Idempotent installation of the compile-time reader.
let install () =
    registerProviderReader readAndFold
