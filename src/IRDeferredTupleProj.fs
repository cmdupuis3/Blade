// A projection of a NAMED wholly-deferred tuple IS its member (formalism 10.2,
// "by substitution"; gap G326, borrow round lane x, found by lane o's program
// generator, seed 1 program 12). One IR normalization, run once by Lowering
// before the write and ordering judgments, so every lane (C++, LLVM, the
// interpreter) and the borrow rule read the use of `t[k]` as that use of the
// member.
module Blade.IRDeferredTupleProj

open Blade.Types
open Blade.IR
open Blade.IRLift

/// THE CLASS. `t[k]` (a structural projection, never a join tuple's flat leaf
/// index) where t names a top-level binding of the module that
/// IRLift.computeDeferredIds calls deferred and whose value is a tuple
/// (IRLift.deferredTupleMembersOf: through top-level aliases `let ta = t`,
/// `|> compute` wrappers, and nested projections `t[0][1]` of a tuple member
/// that is itself such a tuple), and whose k-th member is a NAMED binder of an
/// uncomputed array value (deferred, array-typed, not itself a tuple). The
/// projection is replaced by that binder: a fold over it fuses, a subscript
/// forces it under its own name, a call argument or a map operand reads it,
/// exactly as the member written there would -- same failure position, same
/// print set (R5, R6). Anything else is left alone: a member written inline in
/// the tuple (an anonymous map), a value tuple's projection (a copy of a member
/// evaluated at construction), a projection of a block- or body-local tuple,
/// another module's tuple.
///
/// WHERE. Every top-level binding value and the body of every callable with NO
/// captures (a named function reads module bindings by name). A callable that
/// captures (a lambda, a kernel) reads t through its capture list, which the
/// type checker computed; rewriting its body would read a member it does not
/// capture, so its body is left as written (the C++ lane refuses such a capture
/// of a deferred tuple with a coded BL7004). A let whose WHOLE value is the
/// projection (`let v = t[1]`) is already the member's alias (IRLift's alias
/// classes, every lane's projection arm) and is left as written. Walks the
/// generic ExprShape children: types and the extents they carry, module side
/// tables and match patterns are not rewritten (none holds a projection of a
/// deferred tuple a lane evaluates).
let private rewriteWith (dv: DeferredView) (isMember: IRId -> bool) (value: IRExpr) : IRExpr =
    let rec membersOf (e: IRExpr) : IRExpr list option =
        match e with
        | IRTupleProj (b, k, false) ->
            (match membersOf b with
             | Some ms when k >= 0 && k < ms.Length -> membersOf ms[k]
             | _ -> None)
        | _ -> deferredTupleMembersOf dv e
    let mutable changed = false
    let rec f (e: IRExpr) : IRExpr voption =
        match e with
        // the whole value of a let: the member's alias already
        | IRLet (id, (IRTupleProj _ as v), body) ->
            ValueSome (IRLet (id, v, rewriteTopDown f body))
        | IRTupleProj (b, k, false) ->
            (match membersOf b with
             | Some ms when k >= 0 && k < ms.Length ->
                 (match ms[k] with
                  | IRVar (src, _) as m when isMember src ->
                      changed <- true
                      ValueSome m
                  | _ -> ValueNone)
             | _ -> ValueNone)
        | _ -> ValueNone
    let value' = rewriteTopDown f value
    if changed then value' else value

/// One module: its top-level deferred table, then the rewrite of its binding
/// values and capture-free callable bodies.
let substituteModule (m: IRModule) : IRModule =
    let deferredIds = computeDeferredIds m.Bindings
    let valueOf = m.Bindings |> List.map (fun b -> (b.Id, b.Value)) |> Map.ofList
    let typeOfBinder = m.Bindings |> List.map (fun b -> (b.Id, b.Type)) |> Map.ofList
    let hasTuple =
        m.Bindings |> List.exists (fun b ->
            deferredIds.Contains b.Id && (match b.Value with IRTuple _ -> true | _ -> false))
    if not hasTuple then m else
    let dv : DeferredView =
        { IsDeferred = deferredIds.Contains
          ValueOf = (fun id -> Map.tryFind id valueOf)
          TypeOfExpr = typeOf }
    // a NAMED uncomputed array binder that is not itself a tuple
    let isMember (src: IRId) =
        deferredIds.Contains src
        && (match Map.tryFind src typeOfBinder with
            | Some ty -> (match stripUnits ty with ArrayElem _ -> true | _ -> false)
            | None -> false)
        && (deferredTupleMembersOf dv (IRVar (src, IRTUnit))).IsNone
    let bindings =
        m.Bindings |> List.map (fun b ->
            match b.Value with
            | IRTupleProj _ -> b
            | v ->
                let v' = rewriteWith dv isMember v
                if obj.ReferenceEquals (v', v) then b else { b with Value = v' })
    let functions =
        m.Functions |> List.map (fun f ->
            if not f.Captures.IsEmpty then f
            else
                let body' = rewriteWith dv isMember f.Body
                if obj.ReferenceEquals (body', f.Body) then f else { f with Body = body' })
    { m with Bindings = bindings; Functions = functions }

/// The whole program, module by module (a tuple is judged by its own module's
/// table; another module's tuple is left alone).
let substituteModules (modules: IRModule list) : IRModule list =
    modules |> List.map substituteModule
