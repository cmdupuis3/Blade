// STREAMED KEY GROUPING TO AGGREGATE (round 4 wave 4, lane H item H2 / ST08,
// gap G129): plan rule `stream-group-aggregate v1`, gate
// BLADE_STREAM_GROUP_AGG (opt-in: an optimizer gate with a mirror).
//
// A KEY grouping over a STREAMED rank-1 variable (`let g = group_by(A, gk)`,
// `A` bound with `|> z.stream`) is emitted as a windowed scatter into the
// CSR pool (CodeGenBinding, ungated): the pool is the grouped array's only
// copy of the values. When the grouped array is NOT an output -- a print
// selection excludes it -- and its only readers are top-level bindings whose
// WHOLE value is the per-group fold `method_for(g) <@> lambda(r) -> reduce(r,
// k[, s]) |> compute`, even the pool is a temporary: the same windowed pass
// advances one STATE per group per fold instead (bounded state: ngroups
// cells per fold), in source order -- rows ascending within each group, the
// pool's order -- so every group's chain is the stored row's chain.
//
// ADMISSION (fail closed, the first failed condition recorded):
//   * A is a streamed provider read (rank 1), gk a named single grouping;
//   * a print selection is set and excludes g; no other module names g; no
//     callable, type or side table names g;
//   * every reader of g is a top-level binding whose whole value is the map
//     above: one array operand g, no Reynolds, the kernel one parameter whose
//     body is exactly `reduce(r, k[, s])` over that parameter;
//   * k resolves to a capture-free, closed pure total two-parameter kernel
//     (Optimize.totalScalarOver) with no team / device request; the state,
//     both parameters, the result and the element ONE scalar type;
//   * the seed is a literal, or absent only when the grouping has no empty
//     group (dynamic keys: every group holds its first row);
//   * STRICT FIRST: the stored row fold keeps the serial chain in this build
//     -- an integer builtin `+` / `*`, a kernel with no reorder licence, or
//     BLADE_FP_REPRO=1; a LICENSED float fold declines (the stored row's
//     licensed form is the lane tree, which a per-row chain does not
//     reproduce).
// Every state update happens at g's site, where the stored program reads the
// stream and gathers the pool; the folds' kernels are pure and total and
// their seeds literals, so evaluating them there instead of at each fold's
// site is unobservable. C++ only (the LLVM lane does not build streamed
// provider reads; the program falls back to C++). The interpreter runs the
// as-written tree (it reads the variable resident).
module Blade.OptimizeStreamGroup

open Blade.Types
open Blade.IR

type StreamFold = {
    Id: IRId
    Name: string
    Kernel: IRExpr
    Seed: IRExpr option
}

type StreamGroupAggPlan = {
    GroupById: IRId
    GroupByName: string
    Folds: StreamFold list
}

module StreamGroupAggTable =
    open System.Threading
    let private cell = new AsyncLocal<Map<IRId, StreamGroupAggPlan>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    // the handshake: the C++ emission of g took the state form (its folds adopt the states)
    let private emittedCell = new AsyncLocal<Set<IRId>>()
    let reset () = cell.Value <- Map.empty; emittedCell.Value <- Set.empty
    let record (p: StreamGroupAggPlan) = cell.Value <- Map.add p.GroupById p (get ())
    let tryGroupBy (id: IRId) : StreamGroupAggPlan option = Map.tryFind id (get ())
    let markEmitted (id: IRId) = emittedCell.Value <- Set.add id (match box emittedCell.Value with null -> Set.empty | _ -> emittedCell.Value)
    let emitted (id: IRId) = match box emittedCell.Value with null -> false | _ -> Set.contains id emittedCell.Value
    let tryFold (id: IRId) : (StreamGroupAggPlan * StreamFold) option =
        get () |> Map.toSeq |> Seq.map snd
        |> Seq.tryPick (fun p -> p.Folds |> List.tryFind (fun f -> f.Id = id) |> Option.map (fun f -> (p, f)))

/// OPT-IN: BLADE_STREAM_GROUP_AGG=1|on plans it; unset / 0 / off = the pool
/// (the windowed scatter). Read per call.
let streamGroupAggEnabled () = Blade.Toolchain.envFlag "BLADE_STREAM_GROUP_AGG" false

let private printSelectionNames () : Set<string> option =
    match System.Environment.GetEnvironmentVariable "BLADE_PRINT" with
    | null -> None
    | s ->
        let names = s.Split([| ','; ' '; ';' |], System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun n -> n.Trim()) |> Array.filter ((<>) "") |> Set.ofArray
        if names.IsEmpty then None else Some names

let planStreamGroupAggregate (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let funcs = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let resolve (k: IRExpr) = match k with IRVar (fid, _) -> Map.tryFind fid funcs | _ -> None
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "stream-group-aggregate"; Version = 1
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let builtinOp (c: IRCallable) : IRBinOp option =
        match c.Params, c.Body with
        | [ p0; p1 ], IRBinOp (_, op, IRVar (a, _), IRVar (b, _), _)
            when (match op with IRAdd | IRMul -> true | _ -> false)
                 && ((a = p0.VarId && b = p1.VarId) || (a = p1.VarId && b = p0.VarId)) -> Some op
        | _ -> None
    // `foldReorderLicensed`'s rule, restated (as join-aggregate restates it)
    let licensed (c: IRCallable) =
        let vetoed =
            c.IsRepro
            || (match c.Body with
                | IRApp (IRVar (fid, _), _, _) -> (match Map.tryFind fid funcs with Some g -> g.IsRepro | None -> false)
                | _ -> false)
        not vetoed && (c.IsCommutative || not c.CommGroups.IsEmpty || (builtinOp c).IsSome)
    let scalarOf (t: IRType) = match stripUnits t with IRTScalar et -> Some et | _ -> None
    let enabled = streamGroupAggEnabled ()
    let selection = printSelectionNames ()
    let refsOf (e: IRExpr) = collectVarRefsIR e
    for b in modul.Bindings do
        match b.Value with
        | IRGroupBy (IRVar (aId, aTy), IRVar (gkId, gkTy))
            when (Map.tryFind aId modul.ProviderReads |> Option.exists (fun r -> r.Streamed)) ->
            let gid, gname = b.Id, b.Name
            let refsG (e: IRExpr) = Set.contains gid (refsOf e)
            let elemEt =
                match stripUnits aTy with
                | ArrayElem a when a.IndexTypes.Length = 1 -> scalarOf a.ElemType
                | _ -> None
            let noEmptyGroups =
                match gkTy with
                | IRTGroupKeys (outer, _, enumValues) ->
                    (match outer.Extent, enumValues with
                     | IRLit (IRLitInt _), _ -> false   // positional: groups 0 .. N-1, empty ones included
                     | _, Some _ -> false               // EnumIdx: every declared member, empty ones included
                     | _ -> true)                       // dynamic: a group exists because a row holds its key
                | _ -> false
            let readers = modul.Bindings |> List.filter (fun r -> r.Id <> gid && (Blade.Optimize.bindingPayload modul r |> List.exists refsG))
            // a reader is `method_for(g) <@> lambda(r) -> reduce(r, k[, s])`, whole value, |> compute
            let foldOf (r: IRBinding) : Result<StreamFold, string> =
                let info =
                    match r.Value with
                    | IRCompute (IRApplyCombinator info) | IRApplyCombinator info -> Some info
                    | _ -> None
                match info with
                | None -> Error $"`{r.Name}` reads g other than as a whole-binding per-group fold (`method_for(g) <@> lambda(r) -> reduce(r, k[, s])`)"
                | Some info ->
                match info.Arrays, resolve (fst (peelReynolds info.Kernel)) with
                | [ IRVar (v, _) ], Some kc when v = gid && not info.HasReynolds && kc.Captures.IsEmpty ->
                    (match kc.Params, kc.Body with
                     | [ p ], IRReduce (IRVar (rv, _), k, seed) when rv = p.VarId ->
                         if Set.contains p.VarId (refsOf k) || (seed |> Option.exists (fun s -> Set.contains p.VarId (refsOf s))) then
                             Error $"`{r.Name}`: the fold kernel or seed reads the group row"
                         else Ok { Id = r.Id; Name = r.Name; Kernel = k; Seed = seed }
                     | _ -> Error $"`{r.Name}`: the per-group kernel is not exactly `reduce(r, k[, s])` over its row")
                | _ -> Error $"`{r.Name}`: not a capture-free map over g alone"
            let foldWhy (f: StreamFold) : string option =
                match resolve f.Kernel with
                | None -> Some $"`{f.Name}`: the fold kernel does not resolve to a callable"
                | Some c ->
                    let types = c.RetType :: (c.Params |> List.map (fun p -> p.Type)) |> List.map scalarOf
                    if c.Params.Length <> 2 || not c.Captures.IsEmpty || c.IsOmpParallel || c.IsCudaKernel || c.IsMpiParallel then
                        Some $"`{f.Name}`: the fold kernel is not a capture-free two-parameter kernel without a team / device request"
                    elif not (Blade.Optimize.totalScalarOver c.Params c.Body) then
                        Some $"`{f.Name}`: the fold kernel is not a closed, pure, total scalar"
                    elif elemEt.IsNone || types |> List.exists (fun t -> t <> elemEt) then
                        Some $"`{f.Name}`: the state, the kernel's parameters and result and the element are not one scalar type"
                    else
                    (match f.Seed with
                     | Some (IRLit _) -> None
                     | Some _ -> Some $"`{f.Name}`: the seed is not a literal"
                     | None when not noEmptyGroups -> Some $"`{f.Name}`: an unseeded fold over a grouping with empty groups (positional / EnumIdx keys): BL8003 stays at the fold"
                     | None -> None)
                    |> Option.orElse (
                        let exactInt = (match elemEt with Some (ETInt64 | ETInt32) -> true | _ -> false) && (builtinOp c).IsSome
                        if exactInt || Blade.Toolchain.fpReproEnabled () || not (licensed c) then None
                        else Some $"`{f.Name}` is LICENSED in this build (the stored row's fold is the lane tree, which a per-row chain does not reproduce); BLADE_FP_REPRO=1 or an unlicensed kernel keeps the serial chain")
            let folds = readers |> List.map foldOf
            let fnReader = modul.Functions |> List.tryFind (fun f -> refsG f.Body || (Blade.Optimize.embeddedExtentExprs f.Body |> List.exists refsG))
            let typeReader =
                modul.Bindings |> List.tryFind (fun r ->
                    (Blade.Optimize.irTypeExtentExprs r.Type @ (Blade.Optimize.bindingPayload modul r |> List.collect Blade.Optimize.embeddedExtentExprs))
                    |> List.exists refsG)
            let why =
                if not enabled then Some "disabled by BLADE_STREAM_GROUP_AGG"
                elif elemEt.IsNone then Some "the streamed values are not a rank-1 scalar variable"
                elif selection.IsNone then Some "g is printed (no print selection excludes it): its rows are an output"
                elif selection.Value.Contains gname then Some "the print selection names g: its rows are an output"
                elif foreignRefs.Contains gid then Some "another module names g"
                elif fnReader.IsSome then Some $"a callable (`{fnReader.Value.SourceName}`) reads g (v1 plans top-level folds only)"
                elif typeReader.IsSome then Some $"g is named inside a type extent (in `{typeReader.Value.Name}`)"
                elif readers.IsEmpty then Some "no fold reads g"
                else
                    match folds |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
                    | Some e -> Some e
                    | None -> folds |> List.choose (function Ok f -> Some f | Error _ -> None) |> List.tryPick foldWhy
            let okFolds = folds |> List.choose (function Ok f -> Some f | Error _ -> None)
            let evidence =
                [ $"g = group_by over the STREAMED `{(Map.find aId modul.ProviderReads).VarName}` with a key grouping (windowed reads at g's site)"
                  (match okFolds with
                   | [] -> "folds: none"
                   | fs -> "per-group folds advanced in the windowed pass: " + (fs |> List.map (fun f -> $"`{f.Name}`") |> String.concat ", "))
                  "order: rows ascending within each group (the CSR pool's order), each group's chain from its seed (unseeded: the group's first row; dynamic keys only, so no group is empty)"
                  "parties: the fold kernels capture-free, closed pure total (Optimize.totalScalarOver), seeds literals; every state update at g's site, where the stored program gathers the pool; nothing else moves"
                  "state: ngroups cells per fold instead of the pool's n cells"
                  "llvm lane: streamed provider reads are not built there (C++ fallback)" ]
            match why with
            | Some w -> decide gname (Blade.Effects.Declined w) evidence
            | None ->
                StreamGroupAggTable.record { GroupById = gid; GroupByName = gname; Folds = okFolds }
                decide gname Blade.Effects.Applied evidence
        | _ -> ()
