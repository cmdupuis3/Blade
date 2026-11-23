// Storage shared in place: the LU column-major companion, provider-sink destination passing, the structural group view and the per-group map's reassembled axis (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeInPlace

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeTranspose

/// BLADE_LU_COMPANION=0|off keeps the per-solve column-major rebuild
/// (`declined -- disabled by BLADE_LU_COMPANION`). Unset = on. Read per call.
let luCompanionEnabled () = gateOn "BLADE_LU_COMPANION"

/// The admitted LU factors and solves (rule `lu-companion v1`). Read by
/// CodeGenBinding.genLuBinding / genLuSolveBinding (main's top level only).
module LuCompanionTable =
    open System.Threading
    let private factors = new AsyncLocal<Set<IRId>>()
    let private solves = new AsyncLocal<Map<IRId, IRId>>()
    let private lasts = new AsyncLocal<Set<IRId>>()
    let private getF () = match box factors.Value with null -> Set.empty | _ -> factors.Value
    let private getS () = match box solves.Value with null -> Map.empty | _ -> solves.Value
    let private getL () = match box lasts.Value with null -> Set.empty | _ -> lasts.Value
    let reset () =
        factors.Value <- Set.empty
        solves.Value <- Map.empty
        lasts.Value <- Set.empty
    let recordFactor (fid: IRId) (solveIds: IRId list) =
        factors.Value <- Set.add fid (getF ())
        solves.Value <- solveIds |> List.fold (fun m sid -> Map.add sid fid m) (getS ())
        match List.tryLast solveIds with
        | Some last -> lasts.Value <- Set.add last (getL ())
        | None -> ()
    let isFactor (id: IRId) : bool = Set.contains id (getF ())
    let factorOfSolve (id: IRId) : IRId option = Map.tryFind id (getS ())
    let isLastSolve (id: IRId) : bool = Set.contains id (getL ())

let private recordLuCompanion (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "lu-companion"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

/// Rule `lu-companion v1` (see the banner above).
let planLuCompanion (lapackGateProbe: unit -> bool) (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let enabled = luCompanionEnabled ()
    let arr = List.toArray modul.Bindings
    let byId = arr |> Array.map (fun b -> (b.Id, b)) |> Map.ofArray
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    let closure = lazy (aliasWriteClosure programFuncs foreignRefs modul)
    // The factor tuple a solve operand projects component k of: the
    // projection itself, or a top-level name bound to it (the destructured
    // `let (LU, piv) = m.lu(A)`).
    let rec projRoot (k: int) (e: IRExpr) : IRId option =
        match e with
        | ProjOfNamed (fid, _, k', _) when k' = k -> Some fid
        | IRVar (id, _) ->
            (match Map.tryFind id byId with
             // An ordinary top-level `let` is ASSIGNABLE (Blade lets may be
             // reassigned in their own scope), so assignability is no refusal:
             // the never-written closure over the factor (which reaches this
             // name, its payload naming the factor) refuses an actual write.
             | Some b ->
                (match b.Value with
                 | ProjOfNamed (fid, _, k', _) when k' = k -> Some fid
                 | _ -> None)
             | _ -> None)
        | _ -> None
    let solvesOf (fid: IRId) =
        arr |> Array.toList |> List.choose (fun b ->
            match b.Value with
            | IRLuSolve (l, p, _, _) when projRoot 0 l = Some fid && projRoot 1 p = Some fid
                                          && Map.find b.Id indexOf > Map.find fid indexOf ->
                Some b
            | _ -> None)
    for b in arr do
        match b.Value with
        | IRLu _ ->
            let solves = solvesOf b.Id
            let names = solves |> List.map (fun s -> s.Name) |> String.concat ", "
            let what =
                $"`{b.Name}` keeps ?getrf's column-major factor and LAPACK's pivots beside its row-major (LU, piv) value (8 n^2 bytes until its last admitted solve); the solves {names} hand it to ?getrs instead of rebuilding it per right-hand side (8 n^2 transient bytes and an O(n^2) strided copy each); bitwise unchanged (the value is an exact copy of the companion, the rebuild its exact inverse)"
            let decision : Result<string list, string> =
                // Assignability is not a write (every ordinary top-level `let`
                // is assignable; Codex review round 1 of wave 5 found the old
                // refusal declined EVERY factor): the closure below refuses a
                // factor that is actually assigned or written.
                if solves.IsEmpty then Error $"no top-level `m.lu_solve` / `m.lu_solve_t` binding reads `{b.Name}`'s two halves (a solve nested in a larger expression or in a function body keeps the per-solve copy)"
                elif not (lapackGateProbe ()) then Error "the LAPACK route is off (BLADE_BLAS / OPENBLAS_DIR): the native factor's solves already read the row-major factor in place, nothing to save"
                else
                    match closure.Force () (Set.singleton b.Id) with
                    | Error why -> Error $"the factor may be written, so the companion could go stale: {why}"
                    | Ok proof -> Ok [ $"never-written closure: {proof}"; "C++ lane, routed (LAPACK) arm, main top level; the LLVM lane refuses IRLu (recorded, C++ fallback); the interpreter runs the native twin" ]
            match decision with
            | Error why -> recordLuCompanion b.Name (Blade.Effects.Declined why) []
            | Ok ev when not enabled -> recordLuCompanion b.Name (Blade.Effects.Declined "disabled by BLADE_LU_COMPANION") (what :: ev)
            | Ok ev ->
                LuCompanionTable.recordFactor b.Id (solves |> List.map (fun s -> s.Id))
                recordLuCompanion b.Name Blade.Effects.Applied (what :: ev)
        | _ -> ()

// ===========================================================================
// STRUCTURAL GROUP VIEW (BLADE_GROUP_VIEW, plan rule `structural-group-view
// v1`; round 4 wave 2, PM57). `group_by(a, seg)` over a STRUCTURAL grouping
// (`segments(CI)` / `files(T)`: static run boundaries, identity position
// accessor) copies `a` into a CSR pool cell for cell -- the pool is a byte
// copy of the source, and `ungroup(g)` copies it back. When nothing can
// write the source, the grouped array or anything that may share their
// storage (the never-written closure), the snapshot the copy takes is
// unobservable: the row table points INTO the source's storage (D1a), and
// `ungroup(g)` -- cell for cell the source, since the accessor is the
// identity -- shares it too (D1b). Main's top level only: no ordinary
// allocation there is scope-freed, and the only explicit release form, a
// lifetime region's paired IRBeginPoolScope / IRReleasePools, names pools
// local to its region (checked: no release names a member).
// ===========================================================================

/// PROVIDER SINK DESTINATION PASSING (BLADE_SINK_ALIAS, rule `provider-sink
/// v1`; round 4 wave 2, PM45): a provider write of a PACKED (SymIdx /
/// AntisymIdx leading group + dense trailing) or an OrbIdx (wreath) source
/// hands the writer the source's own pool -- the flatten was a linear copy
/// of exactly those cells in exactly that order -- instead of a new buffer
/// and a copy. `0`/`off` = the copy. Read per call by the C++ emitter.
let sinkAliasEnabled () = gateOn "BLADE_SINK_ALIAS"

/// The decision record of provider-sink v1 (written by the C++ emitter).
let recordProviderSink (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "provider-sink"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }
/// BLADE_GROUP_VIEW=0|off keeps the CSR pool copy and the ungroup copy;
/// unset = on. Read per call.
let groupViewEnabled () = gateOn "BLADE_GROUP_VIEW"

/// The plan's verdicts, read by the C++ emitter (genGroupByBinding,
/// genUngroupBinding), which records the decision where it emits: a group_by
/// binding id -> its verdict (evidence, or the first failed condition); an
/// ungroup binding id -> (its group's id, verdict); and, written by the
/// emitter, the C++ name of the storage an emitted view reads (D1b needs it).
module GroupViewTable =
    open System.Threading
    type State =
        { Groups: Map<IRId, Result<string list, string>>
          Ungroups: Map<IRId, IRId * Result<string list, string>>
          Emitted: Map<IRId, string> }
    let private empty = { Groups = Map.empty; Ungroups = Map.empty; Emitted = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let recordGroup (id: IRId) (v: Result<string list, string>) =
        let s = get () in cell.Value <- { s with Groups = Map.add id v s.Groups }
    let recordUngroup (id: IRId) (gid: IRId) (v: Result<string list, string>) =
        let s = get () in cell.Value <- { s with Ungroups = Map.add id (gid, v) s.Ungroups }
    let tryGroup (id: IRId) = Map.tryFind id (get ()).Groups
    let tryUngroup (id: IRId) = Map.tryFind id (get ()).Ungroups
    let noteEmitted (gid: IRId) (sourceCpp: string) =
        let s = get () in cell.Value <- { s with Emitted = Map.add gid sourceCpp s.Emitted }
    let tryEmitted (gid: IRId) = Map.tryFind gid (get ()).Emitted

/// The decision record of the rule (written by the C++ emitter, which knows
/// whether the view was emitted).
let recordGroupView (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "structural-group-view"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

// ---- the view's admission, one named check per condition (cleanup K9 pilot) --
// Each check answers Ok (what the later checks read) or Error (the decline
// text: the first one is the verdict). `groupVerdict` below runs them in
// order in one `admission { }` block.

let private plainRank1 (t: IRType) : (IRType * int64) option =
    match stripUnits t with
    | ArrayElem at when not at.IsVirtual ->
        match at.IndexTypes with
        | [ ix ] when ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty ->
            (match ix.Extent with IRLit (IRLitInt n) -> Some (stripUnits at.ElemType, n) | _ -> None)
        | _ -> None
    | _ -> None

// The inline operands forceDeferredArrayInput materializes into a FRESH
// private temporary: a map (deferred or `|> compute`d). An inline compute
// of a NAME (`a |> compute` emits `Array<T, 1> tmp = a;`, sharing a's
// data), a bind, a parallel / fusion / functor form or any other inline
// value is not proved private.
let private freshInline (v: IRExpr) : bool =
    match v with
    | IRApplyCombinator _ | IRComposeApply _ -> true
    | IRCompute _ -> isFreshPoolFormWith (fun _ -> false) v
    | _ -> false

let private gvNotMpi (hasMpi: bool) : Result<unit, string> =
    require (not hasMpi) "an MPI build (slab-distributed storage is not audited)"

let private gvNotCuda (hasCuda: bool) : Result<unit, string> =
    require (not hasCuda) "the module holds a CUDA kernel (device copies of the rows are not audited)"

/// The values' (element, extent): a plain dense rank-1 array with a literal extent.
let private gvValuesPlainRank1 (vals: IRExpr) : Result<IRType * int64, string> =
    match plainRank1 (typeOf vals) with
    | None -> Error "the values are not a plain dense rank-1 array with a literal extent"
    | Some (velem, n) -> Ok (velem, n)

let private gvElementMatches (g: IRBinding) (velem: IRType) : Result<unit, string> =
    let gelemOk =
        match stripUnits g.Type with
        | ArrayElem gat -> stripUnits gat.ElemType = velem
        | _ -> false
    require gelemOk "the grouped array's element type is not the values' element type"

let private gvOffsetsPartition (offsets: int64 list) (n: int64) : Result<unit, string> =
    let offsetsOk =
        not offsets.IsEmpty && offsets.Head = 0L && List.last offsets = n
        && (offsets |> List.pairwise |> List.forall (fun (x, y) -> x <= y))
    require offsetsOk "the run boundaries are not a static partition of the source axis from 0 to its extent"

/// The values' provenance (how, and the alias chain the closure seeds with).
let private gvStoredAndFresh (byId: Map<IRId, IRBinding>) (indexOf: Map<IRId, int>)
                             (fresh: IRBinding -> Result<string * IRBinding list, string>)
                             (g: IRBinding) (vals: IRExpr) : Result<string * Set<IRId>, string> =
    match vals with
    | IRVar (vid, _) ->
        (match Map.tryFind vid byId, Map.tryFind vid indexOf, Map.tryFind g.Id indexOf with
         | Some vb, Some vi, Some gi when vi < gi ->
             (match fresh vb with
              | Ok (how, chain) -> Ok ($"the values `{vb.Name}`: {how}", chain |> List.map (fun c -> c.Id) |> Set.ofList)
              | Error why -> Error $"the values are not proved stored and fresh: {why}")
         | _ -> Error "the values are not an earlier top-level binding")
    | v when freshInline v -> Ok ("the values: an inline map materialized into a private temporary named by nothing else", Set.empty)
    | _ -> Error "the values are an inline form not proved to materialize a private temporary (an inline `|> compute` of a name, a bind or a parallel form may share storage)"

let private gvNoLifetimeRelease (byId: Map<IRId, IRBinding>) (released: Lazy<Set<IRId>>) : Result<unit, string> =
    // Any module binding a lifetime-region release names --
    // not only the seeds: every closure member that holds
    // storage is a module binding (fail closed; regions
    // release their own local pools only).
    let rel = Set.intersect (byId |> Map.keys |> Set.ofSeq) (released.Force())
    require rel.IsEmpty "an explicit lifetime-region release (IRReleasePools) names a module binding"

/// The never-written closure over the seeds: Ok why, or the snapshot decline.
let private gvNeverWritten (closure: Lazy<Set<IRId> -> Result<string, string>>) (seeds: Set<IRId>) : Result<string, string> =
    match closure.Force() seeds with
    | Error why -> Error $"the copy keeps the grouped rows a snapshot: {why}"
    | Ok why -> Ok why

/// Plan the views. Every module binding `let g = group_by(v, seg)` whose
/// grouping is a structural one (IRSegments or IRSegmentsGrid) gets a verdict;
/// a key grouping gets none (it permutes its members: no candidate).
let planStructuralGroupView (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let arr = List.toArray modul.Bindings
    let byId = arr |> Array.map (fun b -> (b.Id, b)) |> Map.ofArray
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    let fresh = freshStoredChain modul
    let closure = lazy (aliasWriteClosure programFuncs foreignRefs modul)
    let callables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    // Pools an explicit lifetime-region release names, anywhere.
    let released =
        lazy (
            let mutable s = Set.empty
            let visit (e: IRExpr) =
                iterIRExpr (fun n ->
                    match n with
                    | IRReleasePools (_, pools, _) -> for p in pools do s <- Set.union s (collectVarRefsIR p)
                    | _ -> ()) e
            for b in arr do visit b.Value
            for f in callables do visit f.Body
            s)
    let hasCuda = callables |> List.exists (fun f -> f.IsCudaKernel)
    let hasMpi = callables |> List.exists (fun f -> f.IsMpiParallel)
    let groupVerdict (g: IRBinding) (vals: IRExpr) (segB: IRBinding) : Result<string list, string> =
        match segB.Value with
        | IRSegmentsGrid _ -> Error "a tile grouping (segments(C0, C1)): a tile is a rectangular window of the row-major source, not a run of it"
        | IRSegments (offsets, _) ->
            admission {
                do! gvNotMpi hasMpi
                do! gvNotCuda hasCuda
                let! (velem, n) = gvValuesPlainRank1 vals
                do! gvElementMatches g velem
                do! gvOffsetsPartition offsets n
                let! (how, chain) = gvStoredAndFresh byId indexOf fresh g vals
                let seeds = Set.add g.Id chain
                do! gvNoLifetimeRelease byId released
                let! why = gvNeverWritten closure seeds
                return
                    [ $"rows view the source's storage: {offsets.Length - 1} contiguous run(s) over {n} cells, identity position accessor (no pool, no copy)"
                      how
                      why
                      "main's top level: no ordinary allocation is scope-freed, and no lifetime-region release names a member" ] }
        | _ -> Error "the grouping is not a structural one"
    let disabled (v: Result<string list, string>) =
        match v with
        | Ok ev when not (groupViewEnabled ()) -> Error "disabled by BLADE_GROUP_VIEW", ev
        | Ok ev -> Ok ev, ev
        | Error why -> Error why, []
    for g in arr do
        match g.Value with
        | IRGroupBy (vals, IRVar (sid, _)) ->
            (match Map.tryFind sid byId with
             | Some segB when (match segB.Value with IRSegments _ | IRSegmentsGrid _ -> true | _ -> false) ->
                 let v = groupVerdict g vals segB
                 let (outcome, ev) = disabled v
                 let recorded =
                     match outcome with
                     | Error why when not ev.IsEmpty -> Error (why + " [" + String.concat "; " ev + "]")
                     | o -> o
                 GroupViewTable.recordGroup g.Id recorded
                 // D1b: an `ungroup` of an admitted view. Its binding names g,
                 // so the closure above already holds over it.
                 match v with
                 | Ok _ ->
                     for u in arr do
                         match u.Value with
                         | IRUngroup (IRVar (gid, _), _) when gid = g.Id ->
                             let uv =
                                 match plainRank1 u.Type, plainRank1 (typeOf vals) with
                                 | Some (ue, un), Some (ve, vn) when ue = ve && un = vn ->
                                     Ok [ $"`{u.Name}` = ungroup(`{g.Name}`): cell for cell the source (identity accessor), never written (the closure holds over it): it shares the source's storage" ]
                                 | _ -> Error "the reassembled array is not the source's plain rank-1 shape"
                             GroupViewTable.recordUngroup u.Id g.Id (fst (disabled uv))
                         | _ -> ()
                 | Error _ -> ()
             | _ -> ())
        | _ -> ()

// ===========================================================================
// GROUP ROW MAP STORAGE (BLADE_GROUP_ROWMAP, plan rule `group-rowmap v1`;
// round 4 wave 4, lane F item F1, gap G90). A per-group map
// `h = method_for(g) <@> f |> compute` over a STRUCTURAL grouping
// `g = group_by(v, segments(CI))` is a grouped value whose ONE pool holds the
// rows in group order -- and for a structural rank-1 grouping the runs tile the
// axis in axis order, so that pool, cell for cell, IS `ungroup(h[, CI])`.
// When nothing can write h, the reassembled array or anything that may share
// their storage (the never-written closure), the ungroup's copy is
// unobservable: the reassembled array shares h's pool (destination passing:
// each group's kernel already wrote its run's final cells). Main's top level
// only (no ordinary allocation is scope-freed there). Verdicts only; the C++
// emitter re-checks what only it knows (h was emitted as a grouped pool, main's
// top level) and records the decision. The interpreter keeps its copy (the
// oracle); the LLVM lane has no `segments` arm (the program falls back).
// ===========================================================================

/// BLADE_GROUP_ROWMAP=0|off keeps the ungroup copy; unset = on. Read per call.
let groupRowMapEnabled () = gateOn "BLADE_GROUP_ROWMAP"

/// The plan's verdicts, read by the C++ emitter (genUngroupBinding): an
/// ungroup binding id -> (its row map's id, verdict); and, written by the
/// emitter, the C++ name of each emitted row map (its pool is `<name>__pool`).
module GroupRowMapTable =
    open System.Threading
    type State =
        { Ungroups: Map<IRId, IRId * Result<string list, string>>
          Maps: Set<IRId>
          Emitted: Map<IRId, string> }
    let private empty = { Ungroups = Map.empty; Maps = Set.empty; Emitted = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let recordUngroup (id: IRId) (hid: IRId) (v: Result<string list, string>) =
        let s = get ()
        cell.Value <- { s with Ungroups = Map.add id (hid, v) s.Ungroups
                               Maps = (match v with Ok _ -> Set.add hid s.Maps | Error _ -> s.Maps) }
    let tryUngroup (id: IRId) = Map.tryFind id (get ()).Ungroups
    /// A binding this plan owns (an admitted row map or ungroup).
    let contains (id: IRId) =
        let s = get ()
        s.Maps.Contains id || (match Map.tryFind id s.Ungroups with Some (_, Ok _) -> true | _ -> false)
    let noteEmitted (hid: IRId) (cppName: string) =
        let s = get () in cell.Value <- { s with Emitted = Map.add hid cppName s.Emitted }
    let tryEmitted (hid: IRId) = Map.tryFind hid (get ()).Emitted

/// The decision record of the rule (written by the C++ emitter, which knows
/// whether the row map was emitted as a grouped pool).
let recordGroupRowMap (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "group-rowmap"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

/// Plan the shares. Every module binding `let b = ungroup(h[, A])` whose
/// operand is a module binding holding a map gets a verdict.
let planGroupRowMap (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let arr = List.toArray modul.Bindings
    let byId = arr |> Array.map (fun b -> (b.Id, b)) |> Map.ofArray
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    let closure = lazy (aliasWriteClosure programFuncs foreignRefs modul)
    let callables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let hasCuda = callables |> List.exists (fun f -> f.IsCudaKernel)
    let hasMpi = callables |> List.exists (fun f -> f.IsMpiParallel)
    let released =
        lazy (
            let mutable s = Set.empty
            let visit (e: IRExpr) =
                iterIRExpr (fun n ->
                    match n with
                    | IRReleasePools (_, pools, _) -> for p in pools do s <- Set.union s (collectVarRefsIR p)
                    | _ -> ()) e
            for b in arr do visit b.Value
            for f in callables do visit f.Body
            s)
    let groupShaped (t: IRType) =
        match stripUnits t with
        | ArrayElem at ->
            (match at.IndexTypes with
             | outer :: rest -> outer.IxKind = IxKGroupOuter && rest |> List.exists (fun ix -> ix.IxKind = IxKGroupMember)
             | [] -> false)
        | _ -> false
    let plainRank1 (t: IRType) : (IRType * int64) option =
        match stripUnits t with
        | ArrayElem at when not at.IsVirtual ->
            match at.IndexTypes with
            | [ ix ] when ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty ->
                (match ix.Extent with IRLit (IRLitInt n) -> Some (stripUnits at.ElemType, n) | _ -> None)
            | _ -> None
        | _ -> None
    let elemOf (t: IRType) = match stripUnits t with ArrayElem at -> Some (stripUnits at.ElemType) | _ -> None
    let verdict (u: IRBinding) (h: IRBinding) : Result<string list, string> =
        let info =
            match h.Value with
            | IRCompute (IRApplyCombinator i) -> Ok i
            | IRApplyCombinator _ -> Error "the map is deferred (no `|> compute`): it has no storage to share"
            | _ -> Error "the ungroup's operand is not a computed per-group map"
        match info with
        | Error why -> Error why
        | Ok info ->
        // the grouping behind the operand, through its group_by binding
        let grouping : Result<IRBinding * IRBinding, string> =
            match info.Arrays with
            | [ IRVar (gid, _) ] ->
                (match Map.tryFind gid byId with
                 | Some gb ->
                     (match gb.Value with
                      | IRGroupBy (_, IRVar (sid, _)) ->
                          (match Map.tryFind sid byId with
                           | Some sb -> Ok (gb, sb)
                           | None -> Error "the grouping is not a top-level binding")
                      | _ -> Error "the map's operand is not a top-level group_by binding (a map of a map is v1.1)")
                 | None -> Error "the map's operand is not a top-level binding")
            | _ -> Error "the map's operand is not a named group_by result"
        match grouping with
        | Error why -> Error why
        | Ok (gb, sb) ->
        match sb.Value with
        | IRSegmentsGrid _ -> Error "a tile grouping (segments(C0, C1)): a tile is a rectangular window of the row-major axes, not a run of one, so the map's pool is not the reassembled array's layout"
        | IRGroupKeys _ -> Error "a key grouping (group_keys): its rows are in the permuted order, so reassembly is a scatter (G90 v2)"
        | IRSegments (offsets, _) ->
            if hasMpi then Error "an MPI build (slab-distributed storage is not audited)"
            elif hasCuda then Error "the module holds a CUDA kernel (device copies are not audited)"
            elif not (groupShaped h.Type) then Error "the map's result is not a grouped value (it does not keep the member axis)"
            else
            match plainRank1 u.Type, elemOf h.Type with
            | None, _ -> Error "the reassembled array is not a plain dense rank-1 array with a literal extent"
            | Some (ue, n), Some he when ue = he ->
                let offsetsOk =
                    not offsets.IsEmpty && offsets.Head = 0L && List.last offsets = n
                    && (offsets |> List.pairwise |> List.forall (fun (x, y) -> x <= y))
                if not offsetsOk then Error "the run boundaries are not a static partition of the reassembled axis from 0 to its extent"
                else
                match Map.tryFind h.Id indexOf, Map.tryFind u.Id indexOf, Map.tryFind gb.Id indexOf with
                | Some hi, Some ui, Some gi when gi < hi && hi < ui ->
                    let rel = Set.intersect (byId |> Map.keys |> Set.ofSeq) (released.Force())
                    if not rel.IsEmpty then Error "an explicit lifetime-region release (IRReleasePools) names a module binding"
                    else
                    match closure.Force() (Set.ofList [ h.Id; u.Id ]) with
                    | Error why -> Error $"the ungroup copy keeps the reassembled array a snapshot: {why}"
                    | Ok why ->
                        Ok [ $"`{u.Name}` = ungroup(`{h.Name}`): the {offsets.Length - 1} structural run(s) tile the axis in axis order, so `{h.Name}`'s one pool of {n} cells in group order IS the reassembled array (destination passing: each group's kernel writes its run's final cells)"
                             $"`{h.Name}` = a per-group map over `{gb.Name}` (grouping `{sb.Name}`, identity position accessor)"
                             why
                             "main's top level: no ordinary allocation is scope-freed, and no lifetime-region release names a member" ]
                | _ -> Error "the grouping, the map and the ungroup are not top-level bindings in that order"
            | Some _, _ -> Error "the reassembled array's element type is not the map's"
        | _ -> Error "the grouping is not a structural one"
    let disabled (v: Result<string list, string>) =
        match v with
        | Ok ev when not (groupRowMapEnabled ()) -> Error ("disabled by BLADE_GROUP_ROWMAP [" + String.concat "; " ev + "]")
        | o -> o
    for u in arr do
        match u.Value with
        | IRUngroup (IRVar (hid, _), _) ->
            (match Map.tryFind hid byId with
             | Some h when (match h.Value with IRCompute (IRApplyCombinator _) | IRApplyCombinator _ -> true | _ -> false) ->
                 GroupRowMapTable.recordUngroup u.Id hid (disabled (verdict u h))
             | _ -> ())
        | _ -> ()
