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

/// Internal static-evaluator name for a sizing builtin. Qualified call sites
/// (`ml.total_dim(...)`) are normalized to this mangled form by MLElaborate,
/// and the registry is keyed by it — so a bare `total_dim(...)` in user
/// source no longer resolves. The ML surface is reachable only through an
/// `import ml` alias, not language-wide. The surface (unmangled) names are
/// listed in MLElaborate.sizingNames; keep the two in sync.
let statName (name: string) : string = "__ml_stat_" + name

/// Register the ML sizing builtins with the core static evaluator.
/// Idempotent; safe to call from multiple entry points.
let install () =
    if not installed then
        installed <- true
        registerStaticBuiltin (statName "sh_spec") (fun args ->
            match args with
            | [ SVInt lmax ] when lmax >= 0L -> Ok (specToStatic (shSpec (int lmax)))
            | _ -> Error "sh_spec: expected a non-negative static int lmax")
        registerStaticBuiltin (statName "total_dim") (fun args ->
            match args with
            | [ spec ] ->
                specOfStatic "total_dim" spec
                |> Result.map (fun s -> SVInt (int64 (totalDim s)))
            | _ -> Error "total_dim: expected one static spec argument")
        registerStaticBuiltin (statName "tp_weight_dim") (fun args ->
            match args with
            | [ cfg ] ->
                cfgOfStatic "tp_weight_dim" cfg
                |> Result.map (fun c -> SVInt (int64 (tpWeightDim c)))
            | _ -> Error "tp_weight_dim: expected one static (spec1, spec2, specOut) argument")
        registerStaticBuiltin (statName "linear_weight_dim") (fun args ->
            match args with
            | [ sIn; sOut ] ->
                specOfStatic "linear_weight_dim specIn" sIn |> Result.bind (fun a ->
                specOfStatic "linear_weight_dim specOut" sOut |> Result.bind (fun b ->
                linearWeightDim a b |> Result.map (int64 >> SVInt)))
            | _ -> Error "linear_weight_dim: expected (specIn, specOut) static arguments")
        // Block-navigation builtins (IrrepsIdx v3): fully static per-block
        // accessors so users write block-structured loop nests —
        //   x(irreps_offset(spec, b) + mu * irreps_dim(spec, b) + m)
        // — with every offset and bound folding at compile time. Pure
        // StaticEval surface; no codegen involvement.
        registerStaticBuiltin (statName "irreps_len") (fun args ->
            match args with
            | [ spec ] ->
                specOfStatic "irreps_len" spec
                |> Result.map (fun s -> SVInt (int64 s.Length))
            | _ -> Error "irreps_len: expected one static spec argument")
        let registerBlockAccessor name (f: Spec -> int -> int) =
            registerStaticBuiltin (statName name) (fun args ->
                match args with
                | [ spec; SVInt b ] ->
                    specOfStatic name spec |> Result.bind (fun s ->
                        if b < 0L || b >= int64 s.Length then
                            Error (sprintf "%s: block index %d out of range (spec has %d blocks)" name b s.Length)
                        else Ok (SVInt (int64 (f s (int b)))))
                | _ -> Error (sprintf "%s: expected (spec, block) static arguments" name))
        registerBlockAccessor "irreps_l" (fun s b -> s.[b].L)
        registerBlockAccessor "irreps_parity" (fun s b -> s.[b].Parity)
        registerBlockAccessor "irreps_mult" (fun s b -> s.[b].Mult)
        registerBlockAccessor "irreps_dim" (fun s b -> dim s.[b])
        registerBlockAccessor "irreps_offset" (fun s b -> (blockStarts s).[b])
