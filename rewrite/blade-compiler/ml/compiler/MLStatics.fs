/// The ML module's static-evaluation layer: StaticValue <-> spec/config
/// conversions and the sizing builtins (sh_spec, total_dim, tp_weight_dim,
/// linear_weight_dim), registered into the core evaluator through
/// StaticEval's external-builtin registry so StaticEval.fs itself stays
/// ML-free.
///
/// `install()` is idempotent and is invoked at the top of
/// MLElaborate.expand — the first ML-aware stop of every compilation — so
/// the builtins are visible to every later resolveStatics pass (the
/// elaborator's own, checkModule's, and Lowering's Phase 0).
module Blade.ML.Statics

open Blade.StaticEval
open Blade.ML.Spec

/// Convert a static value to an ML irreps spec: array of (l, parity, mult)
/// int triples, parity 0 = even / 1 = odd.
let specOfStatic (what: string) (v: StaticValue) : Result<Spec, string> =
    let entryOf (e: StaticValue) =
        match e with
        | SVTuple [ SVInt l; SVInt p; SVInt m ] ->
            if l < 0L then Error (sprintf "%s: l must be >= 0" what)
            elif p <> 0L && p <> 1L then Error (sprintf "%s: parity must be 0 (even) or 1 (odd)" what)
            elif m < 1L then Error (sprintf "%s: multiplicity must be >= 1" what)
            else Ok ({ L = int l; Parity = int p; Mult = int m } : SpecEntry)
        | _ -> Error (sprintf "%s: spec entries must be (l, parity, mult) int triples" what)
    match v with
    | SVTuple entries when not entries.IsEmpty ->
        entries |> List.fold (fun acc e ->
            acc |> Result.bind (fun es -> entryOf e |> Result.map (fun x -> es @ [x])))
            (Ok [])
    | _ -> Error (sprintf "%s: expected a static array of (l, parity, mult) triples" what)

/// Convert a static (spec1, spec2, specOut) triple to a TP config.
let cfgOfStatic (what: string) (v: StaticValue) : Result<TPConfig, string> =
    match v with
    | SVTuple [ s1; s2; so ] ->
        specOfStatic (what + " spec1") s1 |> Result.bind (fun a ->
        specOfStatic (what + " spec2") s2 |> Result.bind (fun b ->
        specOfStatic (what + " specOut") so |> Result.map (fun c ->
            ({ Spec1 = a; Spec2 = b; SpecOut = c } : TPConfig))))
    | _ -> Error (sprintf "%s: expected a static (spec1, spec2, specOut) triple" what)

let private specToStatic (s: Spec) : StaticValue =
    SVTuple (s |> List.map (fun e ->
        SVTuple [ SVInt (int64 e.L); SVInt (int64 e.Parity); SVInt (int64 e.Mult) ]))

let mutable private installed = false

/// Register the ML sizing builtins with the core static evaluator.
/// Idempotent; safe to call from multiple entry points.
let install () =
    if not installed then
        installed <- true
        registerStaticBuiltin "sh_spec" (fun args ->
            match args with
            | [ SVInt lmax ] when lmax >= 0L -> Ok (specToStatic (shSpec (int lmax)))
            | _ -> Error "sh_spec: expected a non-negative static int lmax")
        registerStaticBuiltin "total_dim" (fun args ->
            match args with
            | [ spec ] ->
                specOfStatic "total_dim" spec
                |> Result.map (fun s -> SVInt (int64 (totalDim s)))
            | _ -> Error "total_dim: expected one static spec argument")
        registerStaticBuiltin "tp_weight_dim" (fun args ->
            match args with
            | [ cfg ] ->
                cfgOfStatic "tp_weight_dim" cfg
                |> Result.map (fun c -> SVInt (int64 (tpWeightDim c)))
            | _ -> Error "tp_weight_dim: expected one static (spec1, spec2, specOut) argument")
        registerStaticBuiltin "linear_weight_dim" (fun args ->
            match args with
            | [ sIn; sOut ] ->
                specOfStatic "linear_weight_dim specIn" sIn |> Result.bind (fun a ->
                specOfStatic "linear_weight_dim specOut" sOut |> Result.bind (fun b ->
                linearWeightDim a b |> Result.map (int64 >> SVInt)))
            | _ -> Error "linear_weight_dim: expected (specIn, specOut) static arguments")
