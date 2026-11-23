// Let-level common-subexpression elimination, rule cse (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeCse

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis


/// A float literal as its bit pattern, for identity only: structural `=` on
/// floats is IEEE equality, which calls `0.0` and `-0.0` the same value.
let internal floatBitsLit (n: IRExpr) : IRExpr =
    match n with
    | IRLit (IRLitFloat f) -> IRLit (IRLitString ("\u0001f64:" + string (System.BitConverter.DoubleToInt64Bits f)))
    | IRLit (IRLitFloat32 f) -> IRLit (IRLitString ("\u0001f32:" + string (System.BitConverter.SingleToInt32Bits f)))
    | _ -> n

let cseModule (modul: IRModule) : IRModule =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let enabled = cseEnabled ()
    // Two references to callables are the SAME value when the callables are
    // structurally identical: the whole record once its own id and name and
    // its parameters' names and ids are erased (a parameter becomes its
    // position), float literals by bit pattern. Every `(+)` section and
    // every inline lambda is lifted to its own callable, so two identical
    // folds name different ids; this is the identity the comparison needs.
    // `representative` maps a callable to the first structurally identical
    // one ASKED ABOUT -- a consistent partition by key, computed only for
    // callables a candidate value names.
    let callableKey (f: IRCallable) : IRCallable =
        let subst = f.Params |> List.mapi (fun i p -> (p.VarId, -(i + 1))) |> Map.ofList
        let body =
            mapIRExpr (fun e ->
                match e with
                | IRVar (id, t) when Map.containsKey id subst -> IRVar (subst.[id], t)
                | _ -> floatBitsLit e) f.Body
        { f with
            Id = 0
            Name = ""
            SourceName = ""
            Params = f.Params |> List.mapi (fun i p -> { p with Name = ""; VarId = -(i + 1) })
            Body = body }
    let repOf = System.Collections.Generic.Dictionary<IRId, IRId>()
    let byKey = System.Collections.Generic.Dictionary<IRCallable, IRId>(HashIdentity.Structural)
    let representative (f: IRCallable) : IRId =
        match repOf.TryGetValue f.Id with
        | true, r -> r
        | _ ->
            let k = callableKey f
            let r =
                match byKey.TryGetValue k with
                | true, first -> first
                | _ -> byKey.[k] <- f.Id; f.Id
            repOf.[f.Id] <- r
            r
    let canon (e: IRExpr) : IRExpr =
        mapIRExpr (fun n ->
            match n with
            | IRVar (id, t) ->
                (match Map.tryFind id funcs with
                 | Some f -> IRVar (representative f, t)
                 | None -> n)
            | _ -> floatBitsLit n) e
    let rec unroll (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRLet (id, v, body) ->
            let (iv, vf) = unroll v
            let (rb, rf) = unroll body
            (match iv with
             | [] -> ((id, v) :: rb, rf)
             | _ -> (iv @ [ (id, vf) ] @ rb, rf))
        | _ -> ([], e)
    let trivial (v: IRExpr) =
        match v with
        | IRLit _ | IRVar _ | IRParam _ -> true
        | IRApplyCombinator _ | IRComposeApply _ -> true   // deferred: a join's sharing declaration
        | _ -> false
    let hasBarrier (e: IRExpr) =
        let mutable found = false
        iterIRExpr (fun n -> match n with IRAssign _ | IRForRange _ | IRBeginPoolScope _ | IRReleasePools _ -> found <- true | _ -> ()) e
        found
    let moduleSubst = System.Collections.Generic.Dictionary<IRId, IRId>()
    let rewriteBody (f: IRCallable) : IRCallable =
        let (lets, ret) = unroll f.Body
        if lets.Length < 2 || hasBarrier f.Body then f
        else
            let subst = System.Collections.Generic.Dictionary<IRId, IRId>()
            let applySubst (e: IRExpr) =
                if subst.Count = 0 then e
                else
                    mapIRExpr (fun n ->
                        match n with
                        | IRVar (id, t) when subst.ContainsKey id -> IRVar (subst.[id], t)
                        | _ -> n) e
            let kept = ResizeArray<IRId * IRExpr>()
            let pairs = ResizeArray<IRId * IRId>()
            // The values available for reuse, by canonical form. Cleared at
            // every let that may write: nothing before a barrier is reused
            // after it.
            let available = System.Collections.Generic.Dictionary<IRExpr, IRId>(HashIdentity.Structural)
            // A DEFERRED let (a bare apply left for a join) runs its kernel
            // where it is READ, not where it is bound (codegen forces the
            // producer at the consumer), so an expression naming one
            // inherits its facts -- transitively, since a deferred producer
            // may read another.
            let deferredVals =
                lets |> List.filter (fun (_, v) -> match v with IRApplyCombinator _ | IRComposeApply _ -> true | _ -> false)
                |> Map.ofList
            let deferredFacts = System.Collections.Generic.Dictionary<IRId, CseFacts>()
            let rec factsIn (visiting: Set<IRId>) (v: IRExpr) : CseFacts =
                let mutable r = facts v
                for x in collectVarRefsIR v do
                    match Map.tryFind x deferredVals with
                    | Some dv when not (Set.contains x visiting) ->
                        let d =
                            match deferredFacts.TryGetValue x with
                            | true, d -> d
                            | _ ->
                                let d = factsIn (Set.add x visiting) dv
                                deferredFacts.[x] <- d
                                d
                        r <- { MayWrite = r.MayWrite || d.MayWrite; Repeatable = r.Repeatable && d.Repeatable }
                    | _ -> ()
                r
            let factsOfValue = factsIn Set.empty
            // Lets a writing evaluation NAMES -- directly, through the
            // captures or body of a callable it names, or through a deferred
            // let it reads -- anywhere in the body (the return included).
            // Such a let's storage is what a `mut` argument writes, so it
            // takes no part in a merge, in either role: merged, the write
            // would reach the other name too (a later read of the untouched
            // twin would see it).
            let exposed =
                let acc = System.Collections.Generic.HashSet<IRId>()
                let work = System.Collections.Generic.Stack<IRId>()
                for v in (lets |> List.map snd) @ [ ret ] do
                    if (factsOfValue v).MayWrite then
                        for r in collectVarRefsIR v do work.Push r
                while work.Count > 0 do
                    let r = work.Pop()
                    if acc.Add r then
                        (match Map.tryFind r funcs with
                         | Some g ->
                            for c in g.Captures do work.Push c.Id
                            for b in collectVarRefsIR g.Body do work.Push b
                         | None -> ())
                        (match Map.tryFind r deferredVals with
                         | Some dv -> for b in collectVarRefsIR dv do work.Push b
                         | None -> ())
                acc
            for (id, v0) in lets do
                let v = applySubst v0
                let vf = factsOfValue v
                let dup =
                    if trivial v || not vf.Repeatable || exposed.Contains id then None
                    else
                        let cv = canon v
                        match available.TryGetValue cv with
                        | true, earlier -> Some earlier
                        | _ -> available.[cv] <- id; None
                match dup with
                | Some earlier ->
                    subst.[id] <- earlier
                    pairs.Add((id, earlier))
                | None -> kept.Add((id, v))
                if vf.MayWrite then available.Clear()
            if pairs.Count = 0 then f
            elif not enabled then
                Blade.Effects.Decisions.record
                    { Blade.Effects.Rule = "cse"; Version = 2
                      Span = Blade.Ast.noSpan; Subject = f.SourceName
                      Outcome = Blade.Effects.Declined "disabled by BLADE_CSE"
                      Evidence = pairs |> Seq.map (fun (j, i) -> $"__v{j} is the same repeatable value as __v{i}: left as written") |> List.ofSeq }
                f
            else
                let ret' = applySubst ret
                let body' = Seq.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) kept ret'
                // A dropped let may be CAPTURED by a kernel lambda lifted out of
                // this body: that callable's capture list and body name the
                // dropped id, and codegen forwards captures by name, so both
                // are rewritten too (ids are program-global, so this is exact).
                for (j, i) in pairs do moduleSubst.[j] <- i
                Blade.Effects.Decisions.record
                    { Blade.Effects.Rule = "cse"; Version = 2
                      Span = Blade.Ast.noSpan; Subject = f.SourceName
                      Outcome = Blade.Effects.Applied
                      Evidence = pairs |> Seq.map (fun (j, i) -> $"__v{j} is the same repeatable value as __v{i}: dropped, its reads go to __v{i}") |> List.ofSeq }
                { f with Body = body' }
    let rewritten = modul.Functions |> List.map rewriteBody
    if moduleSubst.Count = 0 then { modul with Functions = rewritten }
    else
        let fix (e: IRExpr) =
            mapIRExpr (fun n ->
                match n with
                | IRVar (id, t) when moduleSubst.ContainsKey id -> IRVar (moduleSubst.[id], t)
                | _ -> n) e
        { modul with
            Functions =
                rewritten |> List.map (fun g ->
                    { g with
                        Body = fix g.Body
                        Captures = g.Captures |> List.map (fun c -> if moduleSubst.ContainsKey c.Id then { c with Id = moduleSubst.[c.Id] } else c) }) }
