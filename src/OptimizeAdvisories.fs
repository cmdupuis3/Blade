// Recorded advisories, never rewrites: the segment-streaming decision and the gram_apply advisory (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeAdvisories

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis

/// The streamed variable an expression reads, looking through a lifted
/// kernel: the kernel of an apply is a reference to a callable in the
/// module's function table, and the source it reads is one of that
/// callable's CAPTURES (by the outer binding's id), not a var in the
/// expression itself.
let private streamedReadOf (modul: IRModule) (streamed: Map<Blade.Types.IRId, ProviderReadSpec>) (value: IRExpr) : ProviderReadSpec option =
    let mutable found = None
    iterIRExpr (fun e ->
        match e with
        | IRVar (vid, _) when Map.containsKey vid streamed -> found <- Some streamed.[vid]
        | IRVar (fid, _) ->
            (match modul.Functions |> List.tryFind (fun f -> f.Id = fid) with
             | Some f ->
                 (match f.Captures |> List.tryFind (fun c -> Map.containsKey c.Id streamed) with
                  | Some c -> found <- Some streamed.[c.Id]
                  | None -> ())
             | None -> ())
        | _ -> ()) value
    found

let internal recordSegmentStreaming (modul: IRModule) : unit =
    let streamed =
        modul.ProviderReads
        |> Map.filter (fun _ s -> s.Streamed && not s.VarType.IndexTypes.IsEmpty)
    if not (Map.isEmpty streamed) then
        let structural =
            modul.Bindings
            |> List.choose (fun b -> match b.Value with IRSegments _ | IRSegmentsGrid _ -> Some b.Id | _ -> None)
            |> Set.ofList
        // The one number a cost model needs first: what materializing the
        // variable would hold in memory (literal extents only; a symbolic
        // extent says so). The choice itself stays by consumer shape.
        let materializedBytes (s: ProviderReadSpec) : string =
            let elem =
                match stripUnits s.VarType.ElemType with
                | IRTScalar (ETFloat64 | ETInt64) -> Some 8L
                | IRTScalar (ETFloat32 | ETInt32) -> Some 4L
                | IRTScalar ETComplex128 -> Some 16L
                | IRTScalar ETComplex64 -> Some 8L
                | IRTScalar ETBool -> Some 1L
                | _ -> None
            let cells =
                s.VarType.IndexTypes |> List.fold (fun acc ix ->
                    match acc, ix.Extent with
                    | Some a, IRLit (IRLitInt n) -> Some (a * n)
                    | _ -> None) (Some 1L)
            match elem, cells with
            | Some e, Some c -> $"materialized it would hold {e * c} B"
            | _ -> "materialized size not static"
        let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "segment-streaming"; Version = 2
                  Span = Blade.Ast.noSpan; Subject = subject
                  Outcome = outcome; Evidence = evidence }
        for b in modul.Bindings do
            match b.Value with
            | ReduceOfNamed (vid, _, _, _) when Map.containsKey vid streamed ->
                let s = streamed.[vid]
                decide b.Name Blade.Effects.Applied
                    [ $"fold over the streamed variable '{s.VarName}' walks the store one block at a time, in storage order: the same operation sequence as the flat fold, so the answer is bitwise the materialized one"
                      materializedBytes s ]
            | value when
                    (let mutable halo = false
                     iterIRExpr (fun e ->
                         match e with
                         | IRRange ([ ix ], _) when (match ix.Tag with Some t -> t.StartsWith Blade.Types.haloWinTagPrefix | None -> false) -> halo <- true
                         | _ -> ()) value
                     halo && (streamedReadOf modul streamed value).IsSome) ->
                let s = (streamedReadOf modul streamed value).Value
                decide b.Name Blade.Effects.Applied
                    [ $"the stencil over the streamed variable '{s.VarName}' runs one segment at a time; each run is read with the ghost cells its halo reach demands, nothing else of the variable is ever in memory"
                      materializedBytes s ]
            | value when
                    (let mutable found = false
                     iterIRExpr (fun e ->
                         match e with
                         | IRApplyCombinator info when info.Arrays |> List.exists (function IRVar (vid, _) -> Map.containsKey vid streamed | _ -> false) -> found <- true
                         | _ -> ()) value
                     found) ->
                // every apply in the binding (a zip nested under a reduce
                // included) whose OPERANDS are streamed is an elementwise
                // consumer run one block at a time
                iterIRExpr (fun e ->
                    match e with
                    | IRApplyCombinator info ->
                        (match info.Arrays |> List.tryPick (function IRVar (vid, _) when Map.containsKey vid streamed -> Some streamed.[vid] | _ -> None) with
                         | Some s ->
                             decide b.Name Blade.Effects.Applied
                                 [ $"the elementwise consumer of the streamed variable '{s.VarName}' runs one block of the store's chunk edge at a time (a band of rows above rank 1), each block its own window; nothing else of the variable is ever in memory"
                                   materializedBytes s ]
                         | None -> ())
                    | _ -> ()) value
            | IRGroupBy (IRVar (vid, _), IRVar (gid, _)) when Map.containsKey vid streamed ->
                let s = streamed.[vid]
                if Set.contains gid structural then
                    decide b.Name Blade.Effects.Applied
                        [ $"group_by over the streamed variable '{s.VarName}' under a structural grouping reads one run per group straight into its row; the whole variable is never materialized"
                          materializedBytes s ]
                else
                    decide b.Name (Blade.Effects.Declined "a key grouping needs every cell before any row is known")
                        [ $"'{s.VarName}' is streamed but the grouping is by keys; bind it with .read" ]
            | _ -> ()

/// The structural/05 D7 ADVISORY, never a rewrite: the checker/optimizer sees
/// `gram(A, A)` decompacted and applied to a vector by a row `prodsum` -- an
/// N x N matrix formed and read once -- and records that `gram_apply(A, A,
/// v)` declares the same action without the matrix. Recorded as a DECLINED
/// decision ("left as written") so `blade plan` shows it beside the others;
/// the fastest-way P3 advisory channel, when it exists, can lift it.
let internal recordGramApplyAdvisory (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let values = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let rec collapse (e: IRExpr) = match e with IRCompute i -> collapse i | e -> e
    for b in modul.Bindings do
        match collapse b.Value with
        | IRApplyCombinator info ->
            (match info.Arrays, resolveCallableIn funcs info.Kernel with
             | [ IRVar (gdId, _) ], Some k ->
                // prodsum's two operands arrive in either order (the checker
                // may canonicalize them): one is the row parameter, the other
                // the vector.
                let rowAndVec (p: IRParam) (body: IRExpr) =
                    match body with
                    | IRProdSum [ IRVar (a, _); IRVar (b, _) ] when a = p.VarId -> Some b
                    | IRProdSum [ IRVar (a, _); IRVar (b, _) ] when b = p.VarId -> Some a
                    | _ -> None
                (match Map.tryFind gdId values, k.Params, k.Params |> List.tryHead |> Option.bind (fun p -> rowAndVec p k.Body) with
                 | Some gd, [ _ ], Some vid ->
                    (match gd.Value with
                     | IRDecompact (IRVar (gId, _), 0) ->
                        (match Map.tryFind gId values with
                         | Some g ->
                            (match g.Value with
                             | IRGram (IRVar (aId, _), IRVar (a2, _), true) when aId = a2 ->
                                let nameOf id = modul.Bindings |> List.tryFind (fun x -> x.Id = id) |> Option.map (fun x -> x.Name) |> Option.defaultValue $"__v{id}"
                                Blade.Effects.Decisions.record
                                    { Blade.Effects.Rule = "gram-apply-advisory"; Version = 1
                                      Span = Blade.Ast.noSpan; Subject = b.Name
                                      Outcome = Blade.Effects.Declined $"left as written (advise, never rewrite): `gram_apply({nameOf aId}, {nameOf aId}, {nameOf vid})` declares the same action without the N x N pool"
                                      Evidence = [ $"`{b.Name}` forms the Gram matrix of `{nameOf aId}` (gram, decompact) and applies it once by a row prodsum against `{nameOf vid}`; `gram_apply({nameOf aId}, {nameOf aId}, {nameOf vid})` declares the same action with two rank-1 temporaries and no N x N pool (docs/plans/structural/05)" ] }
                             | _ -> ())
                         | None -> ())
                     | _ -> ())
                 | _ -> ())
             | _ -> ())
        | _ -> ()
