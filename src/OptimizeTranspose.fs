// The transpose normal form, the read-only alias plan and its never-written closure, and the alias identity record (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeTranspose

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage

// ===========================================================================
// TRANSPOSE NORMAL FORM (P2 entry, docs/plans/plan-identity-transpose-compute.md
// sections 3 and 6 P2; `blade plan` rule `transpose-normal-form v1`)
// ===========================================================================
//
// Two eliminations, both pure: every lane computes the same values with or
// without them, so BLADE_TRANSPOSE_ELIDE (an optimizer gate) A/Bs them.
//
//   1. THE PERMUTATION CHAIN (`normalizeTransposesModule`, an IR rewrite --
//      a SEMANTIC normal form, so codegen, the LLVM lane and the interpreter
//      all consume it): `T_s(T_r(.. U))` composes to ONE coordinate
//      permutation of U (T_sigma(T_pi U) = T_{pi o sigma} U); an identity
//      permutation disappears (T_id U = U, the frame restored), anything else
//      is rebuilt as its MINIMAL swap sequence (rank - cycles swaps, chosen
//      deterministically), so a chain only ever loses copies. Each surviving
//      swap is still the eager fresh copy it was; only the IDENTITY case
//      changes which storage a reader sees, so only it needs evidence: a
//      fresh base (a value no binding names), the binding's own copy-on-bind
//      (`let T2 = transpose(transpose(M))` becomes `let T2 = M`, which
//      deep-copies like any assignable alias -- or shares, under 2), or, for a
//      chain nested in a larger expression, M's never-written closure below.
//   2. THE READ-ONLY ALIAS (`planAliasElision`, a SIDE TABLE read by both
//      emitters; the interpreter keeps its deep copy, the independent
//      oracle): an assignable top-level `let X = Y` over a stored array --
//      the symmetric transpose `let ST = transpose(S, [0, 1])`, which the
//      checker erases to `let ST = S` (a certified symmetric identity), and
//      the identity chain of 1 -- copies the pool only so that a write
//      through one name cannot reach the other. When NEITHER can be written
//      (fail-closed: `aliasWriteClosure`) the copy is unobservable and X
//      reads Y's storage: no allocation, no pool copy. Main's top level only:
//      top-level pools live to program exit (nothing is scope-freed there),
//      so the shared storage outlives every reader.
// Wave 3 (the P2 remainder) extends rule 1 and adds three more, all decided
// inside `normalizeTransposesModule`:
//   1b. NAMED CHAINS: `let C = transpose(M, ..)` read by `transpose(C, ..)`
//       splices C's ops in and reads M, when the closure proves C and M
//       (with M's alias chain) never written -- so C's cells at the read are
//       its chain of M's -- and the spliced normal form is strictly shorter.
//   1c. THE SPLICED INTERMEDIATE is dropped when nothing reads it any more and
//       a single-module print selection excludes it (pure, total value).
//   2.  PAYLOAD ACTIONS (the antisymmetric negation, the Hermitian / `conj`
//       conjugation) are explicit nodes of the normal form: each is an exact
//       involution on every stored cell that commutes with the permutation,
//       so an even count cancels; ONE survivor is today's materialization
//       (negate_pool / conjugate_pool into a fresh pool), never a negation of
//       every logical read (an implicit antisymmetric zero is never stored).
//   3.  ABSORPTION INTO A FULL FOLD under plan section 3's fold-order rule: the
//       rank-2 accumulator nest (TypeCheckInfer's rankKDesugar) and the fused
//       terminal (`where omp` kernels) fold the base in its SOURCE order only
//       in a licensed build with a licensed, write-free, total kernel; a
//       strict fold (BLADE_FP_REPRO=1, `where repro`, an unlicensed kernel)
//       keeps the transpose, and its record names the rule -- EXCEPT
//   3c. THE STRICT EXACT CHAIN (PM25, round 4 wave 3 B2): a strict
//       rank-2 accumulator nest keeps the transposed copy's ORDER and reads
//       the base strided in place (X(b, a) in the same a-outer, b-inner
//       nest): the same terms, order, seed and kernel applications, bitwise
//       in every build. Legal when the kernel and seed write nothing (the
//       copy shielded the fold from a write to its base) and the base is
//       proved stored (`storedBase`); otherwise the copy stays. The fused
//       terminal (form B) keeps the copy under a strict build (v1).
//   3b. POINTWISE ABSORPTION: a computed top-level map reading `transpose(X)`
//       once per cell becomes the range map over the output's axes reading
//       X(j, i) -- in every build (no fold, no order question).
//   4.  `f(A, transpose(A, ..))` into an identity-sensitive callee is
//       RECORDED (a transposed argument is its own partition class).
// Still declined: an identity chain inside a function body over a non-fresh
// base (the operand could be returned and alias the caller's argument), a
// partial reduction over a transposed operand, named chains inside function
// bodies, a mixed chain whose permutation AND action both survive.
/// BLADE_TRANSPOSE_ELIDE=0|off keeps today's copies: no transpose chain is
/// normalized and nothing reaches AliasElideTable; every decision records
/// `declined -- disabled by BLADE_TRANSPOSE_ELIDE` with what it would have
/// done. Unset = on. Read per call.
let transposeElideEnabled () = gateOn "BLADE_TRANSPOSE_ELIDE"

/// The read-only alias plan: module binding ids whose `let X = Y` copy is
/// elided (X shares Y's storage). Read by CodeGenBinding.genVarAliasBinding
/// and EmitLlvm.bindNamed; only at main's top level (each emitter re-checks).
module AliasElideTable =
    open System.Threading
    let private cell = new AsyncLocal<Set<IRId>>()
    let private get () = match box cell.Value with null -> Set.empty | _ -> cell.Value
    let reset () = cell.Value <- Set.empty
    let record (id: IRId) = cell.Value <- Set.add id (get ())
    let contains (id: IRId) : bool = Set.contains id (get ())

let private recordTransposeNF (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "transpose-normal-form"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

/// A value that OWNS a freshly allocated pool no binding names, and is not
/// deferred: the shared fresh-pool classification minus the deferred map
/// forms (a deferred map is not stored; reading it re-runs its kernel on
/// current inputs) and minus calls (a callee may hand back storage a module
/// binding owns). None = not proved fresh.
let rec private freshRootForm (v: IRExpr) : string option =
    match v with
    // Literal operands of a forced map are hoisted into a let prelude.
    // Its result still owns the fresh pool allocated by the final form:
    // earlier bindings cannot name that later allocation. Follow only the
    // result, retaining the refusals for returned variables, views and calls.
    // aliasWriteClosure separately audits the entire prelude for writes.
    | IRLet (_, _, body) -> freshRootForm body
    | IRApplyCombinator _ | IRComposeApply _ | IRApp _ -> None
    | IRArrayLit _ -> Some "an array literal"
    | IRCompute _ when isFreshPoolFormWith (fun _ -> false) v -> Some "a `|> compute`"
    | _ when isFreshPoolFormWith (fun _ -> false) v -> Some "an eager array operation (a fresh pool)"
    | _ -> None

/// THE STORED, FRESH ROOT of a top-level array binding, through any chain of
/// earlier `let B = A` aliases: the root's value must be `freshRootForm` or a
/// non-streamed provider read. Stricter than `storedArrayProof` (which also
/// accepts a call's result): an alias that SHARES storage must know nothing
/// else owns it. Returns how the root is stored and the chain (the named
/// binding first, the root last).
let internal freshStoredChain (modul: IRModule) : IRBinding -> Result<string * IRBinding list, string> =
    let arr = List.toArray modul.Bindings
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    let rec go (seen: Set<IRId>) (b: IRBinding) : Result<string * IRBinding list, string> =
        let isVirtual = match stripUnits b.Type with ArrayElem at -> at.IsVirtual | _ -> false
        if isVirtual then Error $"`{b.Name}` is a virtual array (it has no stored cells)"
        else
        match Map.tryFind b.Id modul.ProviderReads with
        | Some r when r.Streamed -> Error $"`{b.Name}` is a streamed provider read (never materialized)"
        | Some _ -> Ok ("a provider read, materialized at its site", [ b ])
        | None ->
        if modul.CompoundInits.ContainsKey b.Id || modul.SparseInits.ContainsKey b.Id || modul.RandomInits.ContainsKey b.Id then
            Error $"`{b.Name}` is a side-table value v1 does not prove fresh"
        else
        match b.Value with
        | IRVar (aid, _) ->
            match Map.tryFind aid indexOf, Map.tryFind b.Id indexOf with
            | Some ai, Some bi when ai < bi && not (seen.Contains aid) ->
                match go (seen.Add b.Id) arr.[ai] with
                | Ok (how, chain) -> Ok ($"an alias of `{arr.[ai].Name}`, {how}", b :: chain)
                | Error why -> Error $"`{b.Name}` is an alias of `{arr.[ai].Name}`, and {why}"
            | _ -> Error $"`{b.Name}` is an alias of a value that is not an earlier top-level binding"
        | v when isDeferredForm v -> Error $"`{b.Name}` is a deferred computation (not stored)"
        | v ->
            match freshRootForm v with
            | Some how -> Ok (how, [ b ])
            | None -> Error $"`{b.Name}`'s value is not proved fresh (a call's result, a view or a deferred form may share or defer storage)"
    go Set.empty

/// THE NEVER-WRITTEN CLOSURE (fail closed). `seeds` are module binding ids
/// whose storage is to be shared. The closure adds every STORAGE-BEARING
/// module binding whose payload names a member (a view, alias, tuple, map or
/// call result derived from it may share its storage) and every callable whose
/// body or captures name a member (a call may hand a member back), to a
/// fixpoint. Ok iff no member is the target of an assignment anywhere, no
/// member is named by another module, no module binding whose payload names a
/// member writes ANYTHING (syntactically: an assignment, a call to a writing
/// callable, an unresolved call -- a write to local storage included, since
/// the copy that made it local is the one being elided), and no member
/// callable's body writes.
let internal aliasWriteClosure (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : Set<IRId> -> Result<string, string> =
    let arr = List.toArray modul.Bindings
    let payloadOf = intervalPayloadOf modul
    let pay = arr |> Array.map (fun b -> payloadOf b.Id b.Value)
    // EXECUTABLE EXTENTS (gap G89): a statement's types run code too -- the
    // range bound of `method_for(0..bump(a))` calls `bump`, which may write `a`
    // through a `mut` parameter, and neither that reference nor that call is
    // an expression child of the value. Every judgement below (references,
    // assignments, writes) reads a statement as its payload PLUS the extent
    // expressions embedded in its nodes PLUS its declared type's extents; a
    // callable as its body, the extents embedded in it and its parameter,
    // return and capture types. The traversal is COMPLETE: `embeddedExtentExprs`
    // expands every extent it finds to completion (no depth bound), and a
    // declared type's extents are taken with everything embedded in them. A
    // type or expression that cannot be walked makes the whole closure
    // decline (`unwalkable`), never a guess.
    let unwalkable : string option ref = ref None
    let closedExtents (roots: IRExpr list) (types: IRType list) (owner: string) : IRExpr list =
        try
            let seen = System.Collections.Generic.HashSet<IRExpr>(HashIdentity.Reference)
            let out = ResizeArray<IRExpr>()
            let add (x: IRExpr) = if seen.Add x then out.Add x
            for r in roots do
                for x in embeddedExtentExprs r do add x
            for t in types do
                for x in irTypeExtentExprs t do
                    add x
                    for y in embeddedExtentExprs x do add y
            List.ofSeq out
        with ex ->
            if unwalkable.Value.IsNone then unwalkable.Value <- Some $"the types of `{owner}` cannot be walked ({ex.Message})"
            []
    let payX : IRExpr list [] =
        arr |> Array.mapi (fun i b -> pay.[i] :: closedExtents [ pay.[i] ] [ b.Type ] b.Name)
    let fnX (f: IRCallable) : IRExpr list =
        f.Body
        :: closedExtents [ f.Body ]
               ((f.Params |> List.map (fun p -> p.Type)) @ [ f.RetType ] @ (f.Captures |> List.map (fun c -> c.Type)))
               f.SourceName
    let refsOfAll (es: IRExpr list) = es |> List.map collectVarRefsIR |> Set.unionMany
    let payRefs = payX |> Array.map refsOfAll
    let callables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let callableX = callables |> List.map (fun f -> (f.Id, fnX f)) |> Map.ofList
    let calleeRefs =
        callables |> List.map (fun f ->
            (f, Set.union (refsOfAll callableX.[f.Id]) (f.Captures |> List.map (fun c -> c.Id) |> Set.ofList)))
    let assigned =
        let mutable s = Set.empty
        let visit (e: IRExpr) =
            iterIRExpr (fun n ->
                match n with
                | IRAssign (t, _) -> s <- Set.union s (collectVarRefsIR t)
                | _ -> ()) e
        for es in payX do List.iter visit es
        for f in callables do List.iter visit callableX.[f.Id]
        s
    // WRITE-FREE, syntactically (fail closed). The shared write analysis
    // (`scheduleMayWriteIn`) judges a write to provably FRESH local storage
    // private -- a `let mut X = M` copy passed to a `mut` parameter, a row view
    // of a fresh transpose then assigned -- and those are exactly the copies a
    // shared alias or an identity normal form removes (Codex review of this
    // pass, findings 1-2). So a statement or callable that names a member must
    // write NOTHING at all: no assignment, no call head that does not resolve
    // to a callable (an unknown or function-valued call may do anything), and
    // every callable it references (a call head or a kernel / callback slot)
    // write-free too, transitively.
    let funcsById = callables |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let letValue = arr |> Array.map (fun b -> (b.Id, b.Value)) |> Map.ofArray
    let rec resolveId (seen: Set<IRId>) (id: IRId) : IRCallable option =
        match Map.tryFind id funcsById with
        | Some f -> Some f
        | None ->
            match Map.tryFind id letValue with
            | Some (IRVar (j, _)) when not (seen.Contains j) -> resolveId (seen.Add id) j
            | _ -> None
    let directlyWrites1 (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n ->
            match n with
            // Rebinding a SCALAR name (a lowered fold's accumulator) touches
            // no array storage; every other assignment counts.
            | IRAssign (IRVar (_, vty), _) when (try holdsNoStorage vty with _ -> false) -> ()
            | IRAssign _ -> hit <- true
            | IRApp (h, _, _) ->
                (match h with
                 | IRVar (hid, _) when (resolveId Set.empty hid).IsSome -> ()
                 | IRObjectFor _ | IRMethodFor _ | IRCompose _ | IRComposeObj _ | IRComposeMeth _ -> ()
                 | _ -> hit <- true)
            | _ -> ()) e
        hit
    let directlyWrites (es: IRExpr list) = es |> List.exists directlyWrites1
    // The least fixpoint of "may write" over every callable AND every module
    // binding: a name's definition runs where the name is used when it is a
    // callable, a let alias of one, or a deferred value (a named composition
    // `K = object_for(poke) >>@ ..` applied later runs poke there -- Codex
    // review, finding 3), so a reference to a writing definition is a write.
    // A STORED binding ran its payload at its own site, so a reference to it
    // runs nothing: only deferred forms and let aliases carry their payload
    // to the use (a stored binding's own payload is judged as a statement).
    let bodyOf =
        (callables |> List.map (fun f -> (f.Id, callableX.[f.Id])))
        @ (arr |> Array.mapi (fun i b -> (b, payX.[i]))
               |> Array.filter (fun (b, _) ->
                   isDeferredForm b.Value || (match b.Value with IRVar _ -> true | _ -> false)
                   // a value that may CARRY code (a closure, a loop object, a tuple
                   // holding one): anything but plain data or a stored array
                   || not ((try holdsNoStorage b.Type with _ -> false) || (match stripUnits b.Type with ArrayElem _ -> true | _ -> false)))
               |> Array.map (fun (b, p) -> (b.Id, p)) |> List.ofArray)
        |> Map.ofList
    let refsOf (es: IRExpr list) : IRId list =
        refsOfAll es |> Set.toList |> List.collect (fun id ->
            match resolveId Set.empty id with
            | Some f -> [ id; f.Id ]
            | None -> [ id ])
        |> List.filter bodyOf.ContainsKey
    let writingDefs =
        let edges = bodyOf |> Map.map (fun _ body -> refsOf body)
        let mutable s = bodyOf |> Map.filter (fun _ body -> directlyWrites body) |> Map.keys |> Set.ofSeq
        let mutable changed = true
        while changed do
            changed <- false
            for KeyValue (id, outs) in edges do
                if not (s.Contains id) && (outs |> List.exists s.Contains) then
                    s <- s.Add id
                    changed <- true
        s
    let writes (es: IRExpr list) = directlyWrites es || (refsOf es |> List.exists writingDefs.Contains)
    let nameOf =
        let m1 = arr |> Array.map (fun b -> (b.Id, b.Name)) |> Map.ofArray
        let m2 = callables |> List.map (fun f -> (f.Id, f.SourceName)) |> Map.ofList
        fun (id: IRId) ->
            match Map.tryFind id m1 with
            | Some n -> n
            | None -> match Map.tryFind id m2 with Some n -> n | None -> $"__v{id}"
    fun (seeds: Set<IRId>) ->
        match unwalkable.Value with
        | Some why -> Error $"the closure cannot judge the module: {why}"
        | None ->
        let mutable members = seeds
        let mutable changed = true
        while changed do
            changed <- false
            for i in 0 .. arr.Length - 1 do
                let b = arr.[i]
                if not (members.Contains b.Id)
                   && not (try holdsNoStorage b.Type with _ -> false)
                   && not (Set.isEmpty (Set.intersect payRefs.[i] members)) then
                    members <- members.Add b.Id
                    changed <- true
            for (f, refs) in calleeRefs do
                if not (members.Contains f.Id) && not (Set.isEmpty (Set.intersect refs members)) then
                    members <- members.Add f.Id
                    changed <- true
        let named (ids: Set<IRId>) = ids |> Set.toList |> List.map (fun id -> $"`{nameOf id}`") |> String.concat ", "
        let assignedHit = Set.intersect members assigned
        let foreignHit = Set.intersect members foreignRefs
        if not assignedHit.IsEmpty then
            Error $"""{named assignedHit} {(if assignedHit.Count = 1 then "is" else "are")} assigned in the program"""
        elif not foreignHit.IsEmpty then
            Error $"""{named foreignHit} {(if foreignHit.Count = 1 then "is" else "are")} named by another module (its writes are not judged here)"""
        else
            let writingBinding =
                [ 0 .. arr.Length - 1 ] |> List.tryFind (fun i ->
                    not (Set.isEmpty (Set.intersect payRefs.[i] members)) && writes payX.[i])
            match writingBinding with
            | Some i -> Error $"`{arr.[i].Name}` names {named (Set.intersect payRefs.[i] members)} and may write (an assignment, a call to a writing callable, or an unresolved call -- a write to local storage counts: the copy it relies on is the one elided)"
            | None ->
                let writingFn =
                    calleeRefs |> List.tryFind (fun (f, _) -> members.Contains f.Id && writes callableX.[f.Id])
                match writingFn with
                | Some (f, refs) -> Error $"function `{f.SourceName}` names {named (Set.intersect refs members)} and may write (an assignment, a writing callee or an unresolved call)"
                | None ->
                    let derived = Set.difference members seeds
                    Ok ($"never written: no assignment, mutating call or writing function names {named seeds}"
                        + (if derived.IsEmpty then "" else $" or a value that may share their storage ({named derived})"))

/// The plain array shape the two copy paths (C++ materializeArrayCopyForm,
/// the LLVM lane's copyArr) handle: one stored pool, plain or compact.
let private aliasableArrayType (t: IRType) : bool =
    match stripUnits t with
    | ArrayElem at ->
        not at.IsVirtual
        && not at.IndexTypes.IsEmpty
        && at.IndexTypes |> List.forall (fun ix ->
            ix.IxKind = IxKPlain
            && (match ix.Symmetry with SymNone | SymSymmetric | SymAntisymmetric | SymHermitian -> true | _ -> false))
    | _ -> false

/// The axis-index list of an array type, for the normal form's type check.
let private axisIds (t: IRType) : IRId list option =
    match stripUnits t with
    | ArrayElem at -> Some (at.IndexTypes |> List.map (fun ix -> ix.Id))
    | _ -> None

/// Compose a swap chain (INNERMOST first) into p: base component k of the
/// result's cell j is j[p[k]] (each swap acts on the cell's index tuple).
let private composeSwaps (rank: int) (swaps: (int * int) list) : int[] =
    let p = Array.init rank id
    for (d1, d2) in swaps do
        for k in 0 .. rank - 1 do
            p.[k] <- (if p.[k] = d1 then d2 elif p.[k] = d2 then d1 else p.[k])
    p

/// The MINIMAL swap sequence of p (INNERMOST first), deterministic: peel the
/// outermost swap (k, p k) for the smallest moved k; each step fixes k and
/// every point p already fixed, so rank - cycles swaps (the minimum).
let private minimalSwaps (p: int[]) : (int * int) list =
    let q = Array.copy p
    let outerFirst = ResizeArray<int * int>()
    let mutable k = 0
    while k < q.Length do
        if q.[k] = k then k <- k + 1
        else
            let a, b = min k q.[k], max k q.[k]
            outerFirst.Add((a, b))
            for x in 0 .. q.Length - 1 do
                q.[x] <- (if q.[x] = a then b elif q.[x] = b then a else q.[x])
    outerFirst |> Seq.rev |> List.ofSeq

/// One node of a transpose chain in the normal form's typed map/action
/// algebra (plan section 3): a COORDINATE SWAP of two plain dims, or a
/// whole-array PAYLOAD ACTION -- the antisymmetric transpose's negation
/// (`negate_pool`), the Hermitian transpose's (or an array `conj`'s)
/// conjugation (`conjugate_pool`). The actions stay explicit: a single one is
/// today's materialization; only a chain whose actions cancel loses them.
type private TnfOp =
    | TnfSwap of int * int
    | TnfNeg
    | TnfConj

let private tnfOpText (op: TnfOp) =
    match op with
    | TnfSwap (a, b) -> $"[{a}, {b}]"
    | TnfNeg -> "negate"
    | TnfConj -> "conjugate"

let private tnfChainText (ops: TnfOp list) = ops |> List.map tnfOpText |> String.concat " then "

/// Peel a chain: its ops INNERMOST first, and the base under it.
let private tnfPeel (e: IRExpr) : TnfOp list * IRExpr =
    let rec go (acc: TnfOp list) (e: IRExpr) =
        match e with
        | IRTranspose (inner, d1, d2) -> go (TnfSwap (d1, d2) :: acc) inner
        | IRArrayNegate inner -> go (TnfNeg :: acc) inner
        | IRArrayConjugate inner -> go (TnfConj :: acc) inner
        | b -> (acc, b)
    go [] e

/// Rebuild ops (innermost first) over a base.
let private tnfRebuild (ops: TnfOp list) (b: IRExpr) : IRExpr =
    ops |> List.fold (fun acc op ->
        match op with
        | TnfSwap (x, y) -> IRTranspose (acc, x, y)
        | TnfNeg -> IRArrayNegate acc
        | TnfConj -> IRArrayConjugate acc) b

/// The LOGICAL dimension count of an array type (a rank-k compact group is k
/// dims; IRTranspose numbers dims, not index slots).
let private dimCount (t: IRType) : int option =
    match stripUnits t with
    | ArrayElem at -> Some (at.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank))
    | _ -> None

/// THE NORMAL FORM of a chain over a base of `rank` dims: the composed
/// permutation as its minimal swaps, then ONE negation iff the chain negates
/// an odd number of times, then ONE conjugation iff it conjugates an odd
/// number of times. Sound because both actions are exact involutions on every
/// stored cell -- `-(-x)` and `conj(conj(z))` flip a sign bit twice, so signed
/// zeros and NaN payloads come back bit-identical, and integer negation wraps
/// and wraps back -- that commute with each other and with a coordinate
/// permutation (an action changes each cell's value, a swap its position).
/// Returns the permutation, the normal form, and whether the minimal swaps
/// reproduce the permutation.
let private tnfNormal (rank: int) (ops: TnfOp list) : int[] * TnfOp list * bool =
    let swaps = ops |> List.choose (function TnfSwap (a, b) -> Some (a, b) | _ -> None)
    let p = composeSwaps rank swaps
    let ms = minimalSwaps p
    let odd (o: TnfOp) = (ops |> List.filter (fun x -> x = o) |> List.length) % 2 = 1
    let nf = (ms |> List.map TnfSwap) @ (if odd TnfNeg then [ TnfNeg ] else []) @ (if odd TnfConj then [ TnfConj ] else [])
    (p, nf, composeSwaps rank ms = p)

/// What the normal form does with one chain (records are the caller's).
type private TnfOutcome =
    | TnfKeep                                  // a one-node chain: nothing to compose
    | TnfAlready of shape: string * nfLen: int // its normal form is no shorter
    | TnfDecline of why: string * shape: string
    | TnfApply of expr: IRExpr * shape: string * why: string

/// The setup of `normalizeTransposesModule` that its three absorptions read, built once per call (cleanup
/// item 2). Field names are the bindings' own, so the lifted bodies read them unchanged.
type private TnContext =
    { builder: IRBuilder
      enabled: bool
      byId: Map<IRId, IRBinding>
      chainOf: IRBinding -> Result<string * IRBinding list, string>
      closure: Lazy<Set<IRId> -> Result<string, string>>
      kernelOf: IRExpr -> IRCallable option
      mayWrite: IRExpr -> bool
      readsForeign: IRExpr list -> bool
      rank2Dense: IRType -> (IRIndexType * IRIndexType) option
      extentTxt: IRIndexType -> string
      nameOfExpr: IRExpr -> string
      storedBase: Set<IRId> -> IRExpr -> Result<string, string>
      strictRule: string
      resolveExpr: IRExpr -> IRCallable option
      deferredOf: IRId -> IRExpr option
      isDeferredId: IRId -> bool
      foldLicence: string -> IRCallable option -> IRBinOp option -> bool -> Result<string, string>
      absorbedRecord: string -> string -> string -> string -> IRIndexType -> IRIndexType -> string -> string -> bool }

/// The POINTWISE-MAP absorption of a transposed operand (lifted from normalizeTransposesModule, cleanup
/// item 2): the setup through the context, the minted-kernel list as a parameter.
let private tnAbsorbPointwise (ctx: TnContext) (mintedKernels: ResizeArray<IRId * IRCallable>) (b: IRBinding) (v: IRExpr) : IRExpr =
    let { nameOfExpr = nameOfExpr; rank2Dense = rank2Dense; kernelOf = kernelOf; byId = byId; chainOf = chainOf;
          mayWrite = mayWrite; closure = closure; builder = builder; extentTxt = extentTxt; enabled = enabled } = ctx
    match v with
    | IRCompute (IRApplyCombinator info)
        when info.Arrays |> List.exists (function IRTranspose _ -> true | _ -> false) ->
        let opsTxt = info.Arrays |> List.map (function IRTranspose (x, d1, d2) -> $"transpose({nameOfExpr x}, [{d1}, {d2}])" | a -> nameOfExpr a) |> String.concat ", "
        let consumer = $"the pointwise map over ({opsTxt})"
        let decline (why: string) =
            recordTransposeNF b.Name (Blade.Effects.Declined why) [ $"{consumer} keeps each transposed operand's copy" ]
            v
        let outAxes = rank2Dense info.OutputType
        let kernel = kernelOf info.Kernel
        let operandProblem =
            info.Arrays |> List.tryPick (fun a ->
                match a with
                | IRTranspose (IRVar (x, xt), d1, d2) when min d1 d2 = 0 && max d1 d2 = 1 && (rank2Dense xt).IsSome && byId.ContainsKey x ->
                    match chainOf byId.[x] with
                    | Error why -> Some $"the transposed operand {why}"
                    | Ok _ -> None
                | IRTranspose _ -> Some "a transposed operand is not a rank-2 swap of a top-level array"
                | IRVar (y, yt) when (rank2Dense yt).IsSome && byId.ContainsKey y ->
                    match chainOf byId.[y] with
                    | Error why -> Some $"the operand {why}"
                    | Ok _ -> None
                | _ -> Some "an operand is not a named dense rank-2 top-level array")
        match info.Loop, outAxes, kernel with
        | _, _, _ when info.HasReynolds || info.KernelOutputRank <> 0 || not info.KernelTDims.IsEmpty
                       || info.KernelInputRanks |> List.exists (fun r -> r <> 0) ->
            decline "v1 absorbs only a scalar-cell kernel (input ranks 0, output rank 0, no reynolds)"
        | IRMethodFor _, Some (ix0, ix1), Some c ->
            let staticAxes = [ ix0; ix1 ] |> List.forall (fun ix -> match ix.Extent with IRLit (IRLitInt _) -> true | _ -> false)
            if not staticAxes then decline "the output's extents are not literals (v1 builds the range over literal extents)"
            elif c.Params.Length <> info.Arrays.Length then decline "the kernel's arity is not the operand count"
            elif not c.Captures.IsEmpty then decline $"the kernel `{c.SourceName}` captures values (v1 calls only a capture-free kernel from the range map)"
            elif c.IsOmpParallel || not c.Parallelism.IsEmpty || c.IsCudaKernel || c.IsMpiParallel || c.IsStatic then
                decline $"the kernel `{c.SourceName}` carries an omp / cuda / mpi request or is static (v1 would not carry the request to the range map, and a dropped request is never silent)"
            elif mayWrite c.Body then decline $"the kernel `{c.SourceName}` may write (the transposed copy was a snapshot taken before it ran)"
            elif (info.Arrays |> List.exists (fun a -> collectVarRefsIR a |> Set.exists (fun r -> r >= c.Id))) then
                decline $"the kernel `{c.SourceName}` is declared before an operand (the range kernel is emitted at the kernel's program point, which must follow every array it reads)"
            else
            match operandProblem with
            | Some why -> decline why
            | None ->
            // every transposed base must be NEVER WRITTEN: the copy was a
            // snapshot at this binding, the gather reads the base wherever
            // the map finally runs (a later pass may move it to its reader)
            let bases =
                info.Arrays |> List.choose (function IRTranspose (IRVar (x, _), _, _) -> Some x | _ -> None) |> List.distinct
            let seeds =
                bases |> List.collect (fun x -> match chainOf byId.[x] with Ok (_, ch) -> ch |> List.map (fun c -> c.Id) | Error _ -> [ x ])
                |> Set.ofList
            match closure.Force() seeds with
            | Error why -> decline $"a transposed base may be written ({why}): the copy is the snapshot that keeps the earlier cells"
            | Ok whyNever ->
            let tagTy (ix: IRIndexType) =
                match ix.Tag with
                | Some t -> IRTIdxTagged (IRTScalar ETInt64, IRefNamed t)
                | None -> IRTScalar ETInt64
            let rix = [ ix0; ix1 ] |> List.map (fun ix -> { ix with Id = builder.FreshId() })
            let pi = builder.FreshId()
            let pj = builder.FreshId()
            let vi = IRVar (pi, tagTy ix0)
            let vj = IRVar (pj, tagTy ix1)
            let reads =
                info.Arrays |> List.map (fun a ->
                    match a with
                    | IRTranspose (x, _, _) -> IRIndex (x, [ vj; vi ], None)
                    | a -> IRIndex (a, [ vi; vj ], None))
            // A binder-free kernel body is inlined (its parameters replaced
            // by the reads: pure subscripts, so a repeated parameter only
            // repeats a read); any other kernel is called.
            let binderFree =
                let mutable ok = true
                iterIRExpr (fun n ->
                    match n with
                    | IRLet _ | IRForRange _ | IRMatch _ | IRApplyCombinator _ | IRComposeApply _ | IRReduce _
                    | IRReduceCompute _ | IRMethodFor _ | IRObjectFor _ | IRAssign _ -> ok <- false
                    | _ -> ()) c.Body
                ok
            let body =
                if binderFree then
                    substituteIRVars (List.zip (c.Params |> List.map (fun p -> p.VarId)) reads |> Map.ofList) c.Body
                else IRApp (info.Kernel, reads, c.RetType)
            let parms : IRParam list =
                [ { Name = "__tnf_i"; Type = tagTy ix0; Index = 0; VarId = pi }
                  { Name = "__tnf_j"; Type = tagTy ix1; Index = 1; VarId = pj } ]
            let lam =
                { mkLambdaCallable builder parms body c.RetType [] false [] [] false false 256 false
                    with SourceName = anonymousCallableName (Some b.Name) "lambda" }
            let rangeAt : IRArrayType = { ElemType = tagTy ix1; IndexTypes = rix; IsVirtual = true; Identity = None }
            let range = IRRange (rix, None)
            let ident = coIterIdentityOf builder range
            let mf : MethodForInfo =
                { Arrays = [ range ]; Identities = [ ident ]; ArrayTypes = [ rangeAt ]; SDimsPerArray = [ 2 ]
                  TotalSDims = 2; SharedIndexTypes = [] }
            let apply =
                IRApplyCombinator
                    { Loop = IRMethodFor mf
                      Kernel = IRVar (lam.Id, mkFuncArrow [ tagTy ix0; tagTy ix1 ] c.RetType)
                      Arrays = [ range ]; Identities = [ ident ]; ArrayTypes = [ rangeAt ]; SharedIndexTypes = []
                      SymcomStates = [ SCNeither; SCNeither ]; TriangularLevels = [ false; false ]
                      SDimsPerArray = [ 2 ]; KernelInputRanks = [ 0; 0 ]; KernelOutputRank = 0
                      KernelTDims = []; SpeedupFactor = 1L; ReynoldsSpeedup = 1L; HasReynolds = false
                      OutputType = info.OutputType; IsCoIteration = false }
            let others =
                info.Arrays |> List.choose (function IRTranspose _ -> None | a -> Some (nameOfExpr a))
            let surviving =
                if others.IsEmpty then "the output's"
                else $"""the output's (= {others |> String.concat ", "}'s, which share its index space)"""
            let andOthers = if others.IsEmpty then "" else " and every untransposed operand"
            let ev =
                [ $"transpose absorbed into {consumer} (kernel `{c.SourceName}`)"
                  $"the surviving traversal is {surviving} row-major nest range<{extentTxt ix0}, {extentTxt ix1}>: forward and contiguous over the output{andOthers}; each transposed base is read once per cell at the swapped coordinate (a gather)"
                  "legal in every build, strict included: no fold, so no order question -- each output cell is the kernel applied to the same operand values, so every cell keeps its bits"
                  $"the transposed base(s): {whyNever} (the copy's snapshot is unobservable)"
                  "no transpose copy, no pool" ]
            if not enabled then
                recordTransposeNF b.Name (Blade.Effects.Declined "disabled by BLADE_TRANSPOSE_ELIDE") ev
                v
            else
                recordTransposeNF b.Name Blade.Effects.Applied ev
                mintedKernels.Add((c.Id, lam))
                IRCompute apply
        | IRMethodFor _, None, _ -> decline "the output is not a dense rank-2 array over two plain axes"
        | _, _, None -> decline "the map's kernel does not resolve to a callable"
        | _ -> decline "the map is not a method_for application"
    | _ -> v

/// FORM B of the fold absorption, the fused terminal (lifted from normalizeTransposesModule, cleanup
/// item 2): the setup through the context.
let private tnAbsorbFoldCompute (ctx: TnContext) (subject: string) (inRepro: bool) (prms: Set<IRId>) (comp: IRExpr) (k: IRExpr) (seed: IRExpr) : IRExpr =
    let { kernelOf = kernelOf; nameOfExpr = nameOfExpr; rank2Dense = rank2Dense; foldLicence = foldLicence;
          resolveExpr = resolveExpr; deferredOf = deferredOf; isDeferredId = isDeferredId; mayWrite = mayWrite;
          readsForeign = readsForeign; storedBase = storedBase; absorbedRecord = absorbedRecord; builder = builder } = ctx
    let asWritten = IRReduceCompute (comp, k, seed)
    match comp with
    | IRApplyCombinator mi when (match mi.Arrays with [ IRTranspose _ ] -> true | _ -> false) ->
        let (x, d1, d2) = match mi.Arrays with [ IRTranspose (x, d1, d2) ] -> (x, d1, d2) | _ -> failwith "unreachable"
        let kernel = kernelOf k
        let kname = match kernel with Some c -> $"`{c.SourceName}`" | None -> "its kernel"
        let consumer = $"the fused fold reduce(method_for(transpose({nameOfExpr x}, [{d1}, {d2}])) <@> .., {kname}, seeded)"
        let keep (why: string) =
            recordTransposeNF subject (Blade.Effects.Declined why)
                [ $"{consumer} keeps the transposed copy and folds it in ITS ascending storage order" ]
            asWritten
        match rank2Dense (try typeOf x with _ -> IRTUnit), (match mi.Loop with IRMethodFor mf -> Some mf | _ -> None) with
        | None, _ -> keep "v1 absorbs a transpose into a fold only over a dense rank-2 operand (two plain axes)"
        | _, None -> keep "the map is not a method_for application"
        | Some (ix0, ix1), Some mf ->
        if mi.IsCoIteration || mi.HasReynolds || mi.KernelInputRanks <> [ 0 ] || mi.KernelOutputRank <> 0 then
            keep "v1 absorbs only a one-operand scalar-cell map under the fold"
        else
        match foldLicence kname kernel None inRepro with
        | Error why -> keep why
        | Ok licence ->
        match kernelOf mi.Kernel with
        | None -> keep "the map kernel does not resolve to a callable"
        | Some mk ->
        match IRTotality.auditCallable resolveExpr deferredOf isDeferredId mk [] with
        | Error why -> keep $"the map kernel `{mk.SourceName}` is not proved total ({why}): its evaluations would move with the traversal"
        | Ok () ->
        if mayWrite mk.Body then keep $"the map kernel `{mk.SourceName}` may write"
        elif mayWrite seed then keep "the fold's seed may write (the transposed copy was taken before it ran)"
        elif readsForeign [ k; mi.Kernel; seed ] then keep "the kernel or seed reads a value bound in another module (its writes are not audited here)"
        else
        match storedBase prms x, stripUnits (try typeOf x with _ -> IRTUnit) with
        | Error why, _ -> keep why
        | Ok baseHow, ArrayElem xat ->
            let traversal = $"the map iterates {nameOfExpr x} itself, so the fused fold reads it forward and contiguously in its ascending SOURCE order (the map kernel `{mk.SourceName}` is total, so moving its evaluations moves no failure)"
            if not (absorbedRecord subject consumer (nameOfExpr x) licence ix0 ix1 baseHow traversal) then asWritten
            else
                let ident = coIterIdentityOf builder x
                let outElem = match stripUnits mi.OutputType with ArrayElem oat -> oat.ElemType | t -> t
                let out' =
                    match mi.OutputType with
                    | IRTUnitAnnotated (_, u) -> IRTUnitAnnotated (mkArrayArrow xat.IndexTypes outElem None, u)
                    | _ -> mkArrayArrow xat.IndexTypes outElem None
                let mf' = { mf with Arrays = [ x ]; Identities = [ ident ]; ArrayTypes = [ xat ] }
                IRReduceCompute (IRApplyCombinator { mi with Loop = IRMethodFor mf'; Arrays = [ x ]; Identities = [ ident ]; ArrayTypes = [ xat ]; OutputType = out' }, k, seed)
        | Ok _, _ -> keep "the transposed base has no array type"
    | _ -> asWritten

/// FORM A of the fold absorption, the rank-K desugar's nest (lifted from normalizeTransposesModule,
/// cleanup item 2): the setup through the context.
let private tnAbsorbFoldNest (ctx: TnContext) (subject: string) (inRepro: bool) (prms: Set<IRId>) (src: IRId) (tv: IRExpr) (body: IRExpr) : IRExpr option =
    let { kernelOf = kernelOf; nameOfExpr = nameOfExpr; rank2Dense = rank2Dense; foldLicence = foldLicence;
          mayWrite = mayWrite; readsForeign = readsForeign; storedBase = storedBase; extentTxt = extentTxt;
          enabled = enabled; strictRule = strictRule; absorbedRecord = absorbedRecord } = ctx
    match tv, body with
    | IRTranspose (x, d1, d2),
      IRLet (acc, seed,
             IRLet (dA, IRForRange (a, (IRLit (IRLitInt 0L) as loA), hiA,
                                    IRLet (dB, IRForRange (b, (IRLit (IRLitInt 0L) as loB), hiB,
                                                           IRLet (dC, IRAssign (IRVar (acc2, accTy), comb), IRLit IRLitUnit)),
                                           IRLit IRLitUnit)),
                    (IRVar (acc3, _) as res)))
        when acc = acc2 && acc = acc3 ->
        let readOf (e: IRExpr) =
            match e with
            | IndexOfNamed (s, _, [ IRVar (ia, ta); IRVar (ib, tb) ], _) when s = src && ia = a && ib = b -> Some (ta, tb)
            | _ -> None
        let shape =
            match comb with
            | IRBinOp (mode, op, IRVar (acc4, at), rd, loc) when acc4 = acc ->
                readOf rd |> Option.map (fun (ta, tb) -> (None, Some op, ta, tb, fun (newRead: IRExpr) -> IRBinOp (mode, op, IRVar (acc4, at), newRead, loc)))
            | IRApp (kf, [ IRVar (acc4, at); rd ], rt) when acc4 = acc ->
                readOf rd |> Option.map (fun (ta, tb) -> (Some kf, None, ta, tb, fun (newRead: IRExpr) -> IRApp (kf, [ IRVar (acc4, at); newRead ], rt)))
            | _ -> None
        match shape with
        | None -> None
        | Some (kf, op, ta, tb, rebuildComb) ->
        let kernel = kf |> Option.bind kernelOf
        let kname =
            match kernel, op with
            | Some c, _ -> $"`{c.SourceName}`"
            | None, Some o -> $"`{operatorFunctionName o}`"
            | _ -> "its kernel"
        let consumer = $"the full fold reduce(transpose({nameOfExpr x}, [{d1}, {d2}]), {kname}) (the rank-2 accumulator nest)"
        let keep (why: string) =
            recordTransposeNF subject (Blade.Effects.Declined why)
                [ $"{consumer} keeps the transposed copy and folds it in ITS ascending storage order" ]
            Some (IRLet (src, tv, body))
        match rank2Dense (try typeOf x with _ -> IRTUnit) with
        | None -> keep "v1 absorbs a transpose into a fold only over a dense rank-2 operand (two plain axes)"
        | Some (ix0, ix1) ->
        match foldLicence kname kernel (if kf.IsNone then op else None) inRepro with
        | Error why ->
            // PM25 (round 4 wave 3, B2): the STRICT fold's EXACT CHAIN. A
            // strict fold of transpose(X) must visit the transposed copy's
            // cells in ITS ascending order -- outer over X's second axis,
            // inner over its first -- and that sequence is X(b, a) read in
            // the SAME nest: the same terms, the same order, the same seed
            // and association, the same kernel applications (so the same
            // failures, in the same order), bitwise in every build. Only
            // the copy goes: legal when nothing the fold runs can write X
            // (the copy shielded the fold from such a write): a
            // write-free kernel and seed, and a base proved stored.
            if kf.IsSome && kernel.IsNone then keep why
            elif (match kernel with Some c -> mayWrite c.Body | None -> false) then
                keep $"{why}; the exact-chain walk declines too: the kernel {kname} may write (the transposed copy shields the fold from a write to its base)"
            elif mayWrite seed then
                keep $"{why}; the exact-chain walk declines too: the fold's seed may write (the transposed copy was taken before it ran)"
            elif readsForeign ((Option.toList kf) @ [ seed ]) then
                keep $"{why}; the exact-chain walk declines too: the kernel or seed reads a value bound in another module (its writes are not audited here)"
            else
            match storedBase prms x with
            | Error why2 -> keep $"{why}; the exact-chain walk declines too: {why2}"
            | Ok baseHow ->
            let ev =
                [ $"the strict fold keeps its exact chain: the nest still runs outer {extentTxt ix1} x inner {extentTxt ix0} -- the transposed copy's ascending order -- reading {nameOfExpr x}(b, a) in place (a strided walk); the same terms in the same order from the same seed, with the same association and the same kernel applications, so bitwise the copy's fold in every build (no reorder licence is used: {why})"
                  baseHow
                  $"the kernel {kname} and the seed write nothing, so no write can reach {nameOfExpr x} between the copy point and any read"
                  "no transpose copy, no pool" ]
            if not enabled then
                recordTransposeNF subject (Blade.Effects.Declined "disabled by BLADE_TRANSPOSE_ELIDE") ev
                Some (IRLet (src, tv, body))
            else
                recordTransposeNF subject Blade.Effects.Applied ($"transpose absorbed into {consumer} (strict exact chain)" :: ev)
                let xRead = match x with IRVar _ -> x | _ -> IRVar (src, typeOf x)
                let comb' = rebuildComb (IRIndex (xRead, [ IRVar (b, tb); IRVar (a, ta) ], None))
                let nest' =
                    IRLet (dA, IRForRange (a, loA, hiA,
                                           IRLet (dB, IRForRange (b, loB, hiB,
                                                                  IRLet (dC, IRAssign (IRVar (acc, accTy), comb'), IRLit IRLitUnit)),
                                                  IRLit IRLitUnit)),
                           res)
                let inner = IRLet (acc, seed, nest')
                Some (match x with IRVar _ -> inner | _ -> IRLet (src, x, inner))
        | Ok licence ->
        if kf.IsSome && kernel.IsNone then keep $"the fold kernel does not resolve to a callable, so its licence cannot be read: {strictRule}"
        elif mayWrite seed then keep "the fold's seed may write (the transposed copy was taken before it ran)"
        elif readsForeign ((Option.toList kf) @ [ seed ]) then keep "the kernel or seed reads a value bound in another module (its writes are not audited here)"
        else
        match storedBase prms x with
        | Error why -> keep why
        | Ok baseHow ->
        let traversal = $"the nest runs over {nameOfExpr x}'s own axes, outer {extentTxt ix0} x inner {extentTxt ix1}, reading it forward and contiguously in its ascending SOURCE order instead of the transposed copy's order"
        if not (absorbedRecord subject consumer (nameOfExpr x) licence ix0 ix1 baseHow traversal) then Some (IRLet (src, tv, body))
        else
            let xRead = match x with IRVar _ -> x | _ -> IRVar (src, typeOf x)
            let comb' = rebuildComb (IRIndex (xRead, [ IRVar (b, tb); IRVar (a, ta) ], None))
            let nest' =
                IRLet (dA, IRForRange (b, loB, hiB,
                                       IRLet (dB, IRForRange (a, loA, hiA,
                                                              IRLet (dC, IRAssign (IRVar (acc, accTy), comb'), IRLit IRLitUnit)),
                                              IRLit IRLitUnit)),
                       res)
            let inner = IRLet (acc, seed, nest')
            Some (match x with IRVar _ -> inner | _ -> IRLet (src, x, inner))
    | _ -> None

/// Rewrite every transpose chain of the module to its normal form (rule 1
/// above), including a chain THROUGH a named binding (`let C = transpose(M,
/// ..)` read by `transpose(C, ..)`, rule 1b). `foreignRefs`: the ids other
/// modules name (the closure's check). `singleModule`: the program is this
/// one module (a print selection then names its bindings, which decides
/// whether a named chain's intermediate is still printed).
let normalizeTransposesModule (builder: IRBuilder) (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : IRModule =
    let enabled = transposeElideEnabled ()
    let byId = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let indexOf = modul.Bindings |> List.mapi (fun i b -> (b.Id, i)) |> Map.ofList
    let chainOf = freshStoredChain modul
    let closure = lazy (aliasWriteClosure programFuncs foreignRefs modul)
    // the names a cost line spells (P5 step 2): bindings, then parameters
    let tnfNameOf =
        let m =
            (modul.Functions |> List.collect (fun f -> f.Params |> List.map (fun pp -> (pp.VarId, pp.Name))))
            @ (modul.Bindings |> List.map (fun b -> (b.Id, b.Name)))
            |> Map.ofList
        fun (id: IRId) -> match Map.tryFind id m with Some n -> n | None -> $"__v{id}"
    // P5 step 2: the legality clause every applied chain record carries
    let tnfLegality =
        Blade.CostModel.legalityLine "transpose permutation composition, T_s(T_r U) = T_(r o s) U rebuilt as its minimal swaps, an even count of payload actions cancelling exactly (an exact IR normal form every lane and the interpreter consume); an identity's storage by the evidence above"
    // NAMED CHAINS (rule 1b): a module binding whose (normalized) value is a
    // chain over an EARLIER module binding: id -> (ops over the root, the
    // root expression, the intermediate bindings from the root's side, self
    // last). A later chain over the name may splice the ops in.
    let mutable named : Map<IRId, TnfOp list * IRExpr * IRId list> = Map.empty
    // intermediate id -> the subjects whose chain spliced through it
    let mutable splicedThrough : Map<IRId, string list> = Map.empty
    // `copyBind` (in `go`): the node is the WHOLE value of an assignable array
    // binding or let, which deep-copies an existing array in every lane.
    let copyOnBind (b: IRBinding) =
        (b.IsMutable || modul.MutableArrayLets.Contains b.Id) && aliasableArrayType b.Type
    // ---- RULE 3, ABSORPTION into a consumer (plan section 3) ---------------
    let funcs =
        (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let kernelOf (k: IRExpr) : IRCallable option =
        match k with
        | IRVar (kid, _) -> Map.tryFind kid funcs
        | _ -> None
    let mayWrite = scheduleMayWriteIn programFuncs modul
    // Does code reachable from `roots` (through every resolved callee's body)
    // read a value bound in ANOTHER module? The shared write analysis sees
    // this module's lets only, so a foreign DEFERRED binding whose kernel
    // writes the transposed base would go unseen (B2 review): such a fold
    // keeps the copy (fail closed). Known = this module's bindings, every
    // let / parameter / loop or pattern variable of this module's callables,
    // and the binders inside the visited callee bodies.
    let moduleKnown =
        let s = System.Collections.Generic.HashSet<IRId>()
        for b in modul.Bindings do s.Add b.Id |> ignore
        let note (e: IRExpr) =
            iterIRExpr (fun n ->
                match n with
                | IRLet (id, _, _) | IRForRange (id, _, _, _) -> s.Add id |> ignore
                | IRMatch (_, cases) -> for c in cases do for v in patternBoundIds c.Pattern do s.Add v |> ignore
                | _ -> ()) e
        for b in modul.Bindings do for e in bindingPayload modul b do note e
        for f in modul.Functions do
            for p in f.Params do s.Add p.VarId |> ignore
            note f.Body
        s
    let readsForeign (roots: IRExpr list) : bool =
        let seen = System.Collections.Generic.HashSet<IRId>()
        let bound = System.Collections.Generic.HashSet<IRId>()
        let refs = ResizeArray<IRId>()
        let rec visit (e: IRExpr) =
            iterIRExpr (fun n ->
                match n with
                | IRLet (id, _, _) | IRForRange (id, _, _, _) -> bound.Add id |> ignore
                | IRMatch (_, cases) -> for c in cases do for v in patternBoundIds c.Pattern do bound.Add v |> ignore
                | IRVar (id, _) ->
                    (match Map.tryFind id funcs with
                     | Some c ->
                        if seen.Add id then
                            for p in c.Params do bound.Add p.VarId |> ignore
                            for cap in c.Captures do refs.Add cap.Id
                            visit c.Body
                     | None -> refs.Add id)
                | _ -> ()) e
        roots |> List.iter visit
        refs |> Seq.exists (fun id -> not (bound.Contains id) && not (moduleKnown.Contains id))
    let builtinOp (c: IRCallable) : IRBinOp option =
        match c.Params, c.Body with
        | [ p0; p1 ], IRBinOp (_, op, IRVar (a, _), IRVar (b, _), _)
            when (match op with IRAdd | IRMul | IRAnd | IROr -> true | _ -> false)
                 && ((a = p0.VarId && b = p1.VarId) || (a = p1.VarId && b = p0.VarId)) -> Some op
        | _ -> None
    // `foldReorderLicensed`'s rule (CodeGenExprSupport), restated over this
    // module's table as the sibling-fold pass restates it: a `where repro`
    // kernel (or the eta wrapper of one) vetoes; a writing kernel never
    // reaches the licence (its write declines first).
    let reproVetoed (c: IRCallable) =
        c.IsRepro
        || (match c.Body with
            | CallOfNamed (fid, _, _, _) -> (match Map.tryFind fid funcs with Some g -> g.IsRepro | None -> false)
            | _ -> false)
    let licenceText (c: IRCallable) =
        match builtinOp c with
        | Some op -> $"a builtin `{operatorFunctionName op}`" + (if c.IsOmpParallel then " under `where omp`" else "")
        | None when not c.CommGroups.IsEmpty -> "a `comm`-licensed kernel"
        | None -> "a commutative kernel"
    let isScalarTy (t: IRType) = match stripUnits t with ArrayElem _ -> false | IRTScalar _ | IRTIdxTagged _ -> true | _ -> false
    let rank2Dense (t: IRType) : (IRIndexType * IRIndexType) option =
        match stripUnits t with
        | ArrayElem at when not at.IsVirtual ->
            match at.IndexTypes with
            | [ a; b ] when [ a; b ] |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1) -> Some (a, b)
            | _ -> None
        | _ -> None
    let extentTxt (ix: IRIndexType) = match ix.Extent with IRLit (IRLitInt n) -> string n | _ -> "n"
    let nameOfExpr (e: IRExpr) =
        match e with
        | IRVar (id, _) when byId.ContainsKey id -> $"`{byId.[id].Name}`"
        | IRVar (id, _) -> $"`__v{id}`"
        | _ -> "its operand"
    // A transpose base that is STORED (a fold reads each of its cells once,
    // exactly as the transposed copy did, and its producer -- if any -- already
    // ran, so no failure moves past the seed; a deferred map, directly or
    // through an alias, would run its kernel in a different place): a module
    // binding with a PROVED fresh stored root through any alias chain
    // (`freshStoredChain`), a non-virtual function parameter (`params`), or a
    // fresh value. Anything else -- a block-local let (it may be an unforced
    // map), a call result -- fails closed (Codex review of the wave-3 pass:
    // `let e = d` over a deferred `d` passed the old one-level test and moved
    // d's division past the seed's).
    let storedBase (prms: Set<IRId>) (b: IRExpr) : Result<string, string> =
        match b with
        | IRVar (id, t) when byId.ContainsKey id ->
            match chainOf byId.[id] with
            | Ok (how, _) -> Ok $"`{byId.[id].Name}` is {how}"
            | Error why -> Error $"the transposed base is not proved stored: {why}"
        | IRVar (id, t) when prms.Contains id ->
            (match stripUnits t with
             | ArrayElem at when not at.IsVirtual -> Ok $"`__v{id}` is a parameter of the function (a stored array)"
             | _ -> Error "the transposed base has no stored cells")
        | IRVar _ -> Error "the transposed base is a local binding v1 does not prove stored (it may be an unforced map)"
        | e ->
            match freshRootForm e with
            | Some how -> Ok $"the transposed base is {how}, evaluated once either way"
            | None -> Error "the transposed base is neither a stored array nor a fresh value"
    // THE FOLD'S LICENCE under plan section 3's fold-order rule: Ok (what
    // licenses the permutation) or Error (the decline, naming the rule for a
    // strict fold). `kernel`: the fold kernel's callable, or None for a
    // builtin operator the rank-k desugar inlined (`op`). `inRepro`: the fold
    // sits in a `where repro` function's body.
    let strictRule = "plan section 3's FOLD-ORDER RULE: `reduce` is a left fold in ascending storage order of its operand, so a strict fold keeps the transpose unless an exact-order traversal is proved -- the consumer's operand sequence, association, seed, empty-input behaviour and failures preserved -- and none is (the base's source order is another operand sequence; an unrestricted strided walk is not admitted)"
    let resolveExpr (e: IRExpr) = kernelOf e
    let deferredOf (id: IRId) = match Map.tryFind id byId with Some b when isDeferredForm b.Value -> Some b.Value | _ -> None
    let isDeferredId = widenDeferred modul (modul.Functions @ programFuncs) (fun (id: IRId) -> (deferredOf id).IsSome)
    let foldLicence (kname: string) (kernel: IRCallable option) (op: IRBinOp option) (inRepro: bool) : Result<string, string> =
        if Blade.Toolchain.fpReproEnabled () then
            Error $"a strict fold (BLADE_FP_REPRO=1: every fold keeps its serial association): {strictRule}"
        elif inRepro then
            Error $"a strict fold (it sits in a `where repro` function's body): {strictRule}"
        else
        match kernel, op with
        | None, Some o ->
            if (match o with IRAdd | IRMul | IRAnd | IROr -> true | _ -> false) then
                Ok $"the fold's operator is a builtin `{operatorFunctionName o}` (commutative and associative: a builtin section's own licence, formalism 2.4), total"
            else Error $"a strict fold (the operator `{operatorFunctionName o}` has no reorder licence): {strictRule}"
        | Some c, _ ->
            if reproVetoed c then
                Error $"a strict fold (the kernel {kname} carries a `where repro` demand -- its own or its enclosing function's): {strictRule}"
            elif mayWrite c.Body then
                Error $"a strict fold (the kernel {kname} may write: its applications are ordered side effects, so no licence reorders them): {strictRule}"
            elif not (c.IsCommutative || not c.CommGroups.IsEmpty || (builtinOp c).IsSome) then
                Error $"a strict fold (the kernel {kname} has no reorder licence: not commutative, no `comm` group, not a builtin `+` / `*`): {strictRule}"
            else
            match IRTotality.auditCallable resolveExpr deferredOf isDeferredId c [] with
            | Error why -> Error $"the kernel {kname} is licensed but not proved total ({why}): a reordered fold would change which failure fires first"
            | Ok () -> Ok $"the kernel {kname} is {licenceText c} (no `where repro`, no write, total), which admits any permutation of the fold's operands"
        | None, None -> Error $"the fold kernel does not resolve to a callable, so its licence cannot be read: {strictRule}"
    let absorbedRecord (subject: string) (consumer: string) (baseTxt: string) (licence: string) (ix0: IRIndexType) (ix1: IRIndexType) (baseHow: string) (traversal: string) =
        let ev =
            [ traversal
              $"licence: {licence}, and this build reorders licensed folds (BLADE_FP_REPRO unset)"
              $"the logical domain is unchanged: the same {extentTxt ix0} x {extentTxt ix1} cells of {baseTxt}, each folded once, the same seed; an empty operand is empty either way"
              baseHow
              "no transpose copy, no pool" ]
        if not enabled then
            recordTransposeNF subject (Blade.Effects.Declined "disabled by BLADE_TRANSPOSE_ELIDE") ev
            false
        else
            recordTransposeNF subject Blade.Effects.Applied ($"transpose absorbed into {consumer}" :: ev)
            true
    // FORM A, THE RANK-K DESUGAR (TypeCheckInfer.inferReduceCore's
    // `rankKDesugar`): a full fold of a dense rank-2 array with literal
    // extents is a block `let __rksrc = transpose(X); let mut acc = seed;
    // for a { for b { acc = acc OP __rksrc(a, b) } }; acc` (OP a builtin
    // section inlined, or a call of the kernel). Absorbed: the nest runs over
    // X's own axes -- `for b { for a { acc = acc OP X(b, a) } }` -- so X is read
    // forward and contiguously, in its source order. Some = the rewrite or the
    // kept form (with its record); None = not this shape.
    let ctx : TnContext =
        { builder = builder; enabled = enabled; byId = byId; chainOf = chainOf; closure = closure;
          kernelOf = kernelOf; mayWrite = mayWrite; readsForeign = readsForeign; rank2Dense = rank2Dense;
          extentTxt = extentTxt; nameOfExpr = nameOfExpr; storedBase = storedBase; strictRule = strictRule;
          resolveExpr = resolveExpr; deferredOf = deferredOf; isDeferredId = isDeferredId;
          foldLicence = foldLicence; absorbedRecord = absorbedRecord }
    let absorbFoldNest = tnAbsorbFoldNest ctx
    // FORM B, THE FUSED TERMINAL: `reduce(transpose(X), k, s, axes = 2)` with
    // a `where omp` (or otherwise deferred-path) kernel is
    // `reduce(method_for(transpose(X)) <@> id, k, s)`: absorbed, the map
    // iterates X itself, so the fused fold reads X forward and contiguously.
    // The map kernel's evaluations move with the traversal, so it must be
    // proved total (a failure's coordinate would otherwise change).
    let absorbFoldCompute = tnAbsorbFoldCompute ctx
    // POINTWISE ABSORPTION (rule 3b): a computed top-level map whose operands
    // are dense rank-2 arrays, one or more of them `transpose(X, [0, 1])`,
    // read once per cell by a scalar kernel (`transpose(M, [0, 1]) + B` is
    // `method_for(zip(transpose(M), B)) <@> (+)`). No order question: each
    // output cell is the kernel on the same operand values in every build. The
    // rewrite is the range map over the OUTPUT's axes whose kernel reads X at
    // the swapped coordinate and every other operand at the cell's own:
    // `method_for(range<I, J>) <@> lambda(i, j) -> k(X(j, i), B(i, j))` -- the
    // surviving traversal is the output's row-major nest, forward and
    // contiguous over the output and over each untransposed operand (a zip's
    // operands share the output's index space); X is gathered. Minted
    // kernels: (origin kernel id, lambda).
    let mintedKernels = ResizeArray<IRId * IRCallable>()
    let absorbPointwise = tnAbsorbPointwise ctx mintedKernels
    // `site`: Some b = a module binding, None = a function body (subject = the
    // function's source name; `locals` = its parameters).
    let rewriteIn (subject: string) (site: IRBinding option) (locals: Set<IRId>) (inRepro: bool) (rootCopy: bool) (root: IRExpr) : IRExpr =
        // The normal form of `ops` over `base1` standing for the chain node `e`.
        let outcome (copyBind: bool) (ops: TnfOp list) (base1: IRExpr) (e: IRExpr) : TnfOutcome =
            if ops.Length < 2 then TnfKeep
            else
            let chainTxt = tnfChainText ops
            match dimCount (try typeOf base1 with _ -> IRTUnit) with
            | None -> TnfDecline ("the chain's base has no array type the normalizer can read", $"chain {chainTxt}")
            | Some rank ->
            let (p, nf, ok) = tnfNormal rank ops
            let nfExpr = tnfRebuild nf base1
            let sameType =
                match axisIds (try typeOf e with _ -> IRTUnit), axisIds (try typeOf nfExpr with _ -> IRTUnit) with
                | Some a, Some b -> a = b
                | _ -> false
            let count (pred: TnfOp -> bool) = ops |> List.filter pred |> List.length
            let nSwaps = count (function TnfSwap _ -> true | _ -> false)
            let nNeg = count (fun o -> o = TnfNeg)
            let nConj = count (fun o -> o = TnfConj)
            let shape =
                if nNeg = 0 && nConj = 0 then
                    let permTxt = p |> Array.map string |> String.concat ", "
                    if nf.IsEmpty then $"chain {chainTxt} ({nSwaps} swaps) composes to the IDENTITY permutation: the value is the base itself, {nSwaps} copies -> 0"
                    else $"chain {chainTxt} ({nSwaps} swaps) composes to the permutation [{permTxt}] = {nf.Length} swap(s) {tnfChainText nf}: {nSwaps} copies -> {nf.Length}"
                else
                    let actionNote (n: int) (what: string) (law: string) =
                        if n = 0 then []
                        elif n % 2 = 0 then [ $"{n} {what}s cancel ({law})" ]
                        else [ $"{n} {what}(s) leave ONE (kept: today's materialization into a fresh pool)" ]
                    let notes =
                        (if nSwaps = 0 then []
                         elif (nf |> List.exists (function TnfSwap _ -> true | _ -> false)) then [ $"{nSwaps} swap(s) compose to {nf |> List.filter (function TnfSwap _ -> true | _ -> false) |> List.length}" ]
                         else [ $"{nSwaps} swaps compose to the identity permutation" ])
                        @ actionNote nNeg "negation" "-(-x) = x exactly on every stored cell: a sign flip twice, signed zeros and NaN payloads included, integer negation wraps and wraps back; an antisymmetric group's implicit diagonal zero is never stored, so no read changes representation"
                        @ actionNote nConj "conjugation" "conj(conj(z)) = z exactly on every stored cell: the imaginary sign flipped twice; the identity on a real element"
                    let notesTxt = notes |> String.concat "; "
                    if nf.IsEmpty then $"chain {chainTxt} ({ops.Length} nodes) composes to the IDENTITY map and action -- {notesTxt}: the value is the base itself, {ops.Length} copies -> 0"
                    else $"chain {chainTxt} ({ops.Length} nodes) composes to {tnfChainText nf} -- {notesTxt}: {ops.Length} copies -> {nf.Length}"
            let hasSwap = nf |> List.exists (function TnfSwap _ -> true | _ -> false)
            let hasAction = nf |> List.exists (function TnfSwap _ -> false | _ -> true)
            if not ok || not sameType then TnfDecline ("internal: the normal form does not reproduce the chain's permutation or axis types", shape)
            elif nf.Length >= ops.Length then TnfAlready (shape, nf.Length)
            elif hasSwap && hasAction then
                TnfDecline ("a surviving coordinate permutation AND payload action keep their written order (v1 rebuilds a chain only when one of the two cancels)", shape)
            else
            // Only the identity normal form changes which storage the
            // reader sees; a surviving node is still a fresh copy.
            let evidence : Result<string, string> =
                if not nf.IsEmpty then Ok "each surviving node is still an eager fresh copy, evaluated where the chain's innermost copy was"
                else
                match freshRootForm base1 with
                | Some how -> Ok $"the base is {how}: a fresh value no binding names"
                | None ->
                let copyTxt = "the binding keeps its copy-on-bind contract (an assignable array let deep-copies its source in every lane), so it still owns fresh storage"
                match site, base1 with
                | None, IRVar (vid, _) when copyBind && locals.Contains vid ->
                    Ok $"{copyTxt}; the base is a parameter of the function"
                | None, _ when copyBind -> Error "the base is not a parameter of the function (a body-local base may be a staging pool the C++ lane lets the binding alias)"
                | None, _ -> Error "inside a function body v1 admits the identity only over a fresh base or as an assignable let's whole value (a returned operand would alias the caller's argument)"
                | Some _, IRVar (vid, _) when byId.ContainsKey vid ->
                    match chainOf byId.[vid] with
                    | Error why -> Error $"the base {why}"
                    | Ok (how, chain) ->
                        if copyBind then Ok $"{copyTxt}; the base is {how}; the alias plan decides whether that copy is needed"
                        else
                            match closure.Force() (chain |> List.map (fun c -> c.Id) |> Set.ofList) with
                            | Ok why -> Ok $"the base is {how}, read in place: {why}"
                            | Error why -> Error $"the base is read in place by a larger expression, and {why}"
                | Some _, _ -> Error "the base is neither a fresh value nor a stored top-level array"
            match evidence with
            | Error why -> TnfDecline (why, shape)
            | Ok why -> TnfApply (nfExpr, shape, why)
        let rec go (copyBind: bool) (e: IRExpr) : IRExpr =
            match e with
            | IRTranspose _ | IRArrayNegate _ | IRArrayConjugate _ ->
                let (ops0, base0) = tnfPeel e
                let base1 = go false base0
                let asWritten () = tnfRebuild ops0 base1
                // P5 step 2: the cost basis of a rebuilt chain -- every written
                // node is a pool copy (k), the normal form keeps m of them, plus
                // the copy-on-bind an identity chain's assignable binding still
                // makes (wave-3 merge: re-placed inside the wave-3 rewrite).
                let tnfCost (ops: TnfOp list) (baseExpr: IRExpr) =
                    let bty = (try typeOf baseExpr with _ -> IRTUnit)
                    let m =
                        match dimCount bty with
                        | Some rank -> let (_, nf, _) = tnfNormal rank ops in nf.Length
                        | None -> ops.Length
                    let bindCopies = if m = 0 && copyBind && (freshRootForm baseExpr).IsNone then 1 else 0
                    let baseName = match baseExpr with IRVar (vid, _) -> tnfNameOf vid | _ -> "the base"
                    Blade.CostModel.transposeChainLine tnfNameOf bty baseName ops.Length m bindCopies subject
                // RULE 1b, THE NAMED CHAIN: the base is a module binding whose
                // value is itself a chain over an earlier stored root. Splicing
                // its ops in reads the root instead; sound when nothing can
                // write the intermediates or the root (the closure), so the
                // intermediate's cells at this read are its chain of the root's.
                let spliced : (TnfOp list * IRExpr * string * IRId list) option =
                    match site, base1 with
                    | Some _, IRVar (cid, _) when named.ContainsKey cid ->
                        let (opsC, rootExpr, mids) = named.[cid]
                        let cName = byId.[cid].Name
                        let rootName = match rootExpr with IRVar (rid, _) when byId.ContainsKey rid -> byId.[rid].Name | _ -> "?"
                        let through = $"through `{cName}` (`{cName}` = {tnfChainText opsC} of `{rootName}`)"
                        let combined = opsC @ ops0
                        let rankOf () = dimCount (try typeOf rootExpr with _ -> IRTUnit)
                        match rankOf () with
                        | None -> None
                        | Some rank ->
                        let (_, nfC, _) = tnfNormal rank combined
                        let (_, nf0, _) = tnfNormal rank ops0
                        let declineSplice (why: string) =
                            recordTransposeNF subject (Blade.Effects.Declined $"the chain {through} is not spliced: {why}")
                                [ $"chain {tnfChainText ops0} over `{cName}` keeps reading `{cName}`'s storage" ]
                        // the chain is the identity on its own: nothing to splice
                        if nf0.IsEmpty then None
                        elif nfC.Length >= nf0.Length then
                            let nfTxt = if nfC.IsEmpty then "the root itself" else tnfChainText nfC
                            declineSplice $"its normal form over `{rootName}` ({nfTxt}) is no shorter than over `{cName}`"
                            None
                        else
                        match rootExpr with
                        | IRVar (rid, _) when byId.ContainsKey rid ->
                            match chainOf byId.[rid] with
                            | Error why -> declineSplice $"the root {why}"; None
                            | Ok (how, chain) ->
                                let seeds = Set.ofList (mids @ (chain |> List.map (fun c -> c.Id)))
                                match closure.Force() seeds with
                                | Error why -> declineSplice why; None
                                | Ok why ->
                                    Some (combined, rootExpr,
                                          $"named chain {through}: `{rootName}` is {how}; {why}, so at this read `{cName}` holds exactly its chain of `{rootName}`", mids)
                        | _ -> declineSplice "its root is not a stored top-level array"; None
                    | _ -> None
                let viaSplice =
                    match spliced with
                    | None -> None
                    | Some (ops, rootExpr, ev, mids) ->
                        match outcome copyBind ops rootExpr e with
                        | TnfApply (nfExpr, shape, why) when enabled ->
                            recordTransposeNF subject Blade.Effects.Applied [ shape; ev; why; tnfLegality; tnfCost ops rootExpr ]
                            for m in mids do
                                splicedThrough <- splicedThrough.Add (m, subject :: (Map.tryFind m splicedThrough |> Option.defaultValue []))
                            Some nfExpr
                        | TnfApply (_, shape, why) ->
                            recordTransposeNF subject (Blade.Effects.Declined "disabled by BLADE_TRANSPOSE_ELIDE") [ shape; ev; why; tnfLegality; tnfCost ops rootExpr ]
                            Some (asWritten ())
                        | TnfDecline (why, shape) ->
                            recordTransposeNF subject (Blade.Effects.Declined $"the chain through the named binding is not spliced: {why}") [ shape; ev ]
                            None
                        | TnfAlready _ | TnfKeep -> None
                match viaSplice with
                | Some r -> r
                | None ->
                match outcome copyBind ops0 base1 e with
                | TnfKeep ->
                    (match ops0 with
                     | [ (TnfNeg | TnfConj) as a ] ->
                        let what = if a = TnfNeg then "negation (an antisymmetric transpose, `negate_pool`)" else "conjugation (a Hermitian transpose or `conj`, `conjugate_pool`)"
                        recordTransposeNF subject
                            (Blade.Effects.Declined $"a single whole-array {what} is kept: one payload action is today's materialization into a fresh pool (each stored cell's exact image -- `-0.0` stays what it is today; nothing is applied to an unstored implicit zero); only a chain whose actions cancel loses it")
                            [ $"action {tnfOpText a}" ]
                     | _ -> ())
                    asWritten ()
                | TnfAlready (shape, n) ->
                    recordTransposeNF subject (Blade.Effects.Declined $"already normal: the chain's normal form needs {n} node(s)") [ shape ]
                    asWritten ()
                | TnfDecline (why, shape) ->
                    recordTransposeNF subject (Blade.Effects.Declined why) [ shape ]
                    asWritten ()
                | TnfApply (_, shape, why) when not enabled ->
                    recordTransposeNF subject (Blade.Effects.Declined "disabled by BLADE_TRANSPOSE_ELIDE") [ shape; why; tnfLegality; tnfCost ops0 base1 ]
                    asWritten ()
                | TnfApply (nfExpr, shape, why) ->
                    recordTransposeNF subject Blade.Effects.Applied [ shape; why; tnfLegality; tnfCost ops0 base1 ]
                    nfExpr
            | IRReduceCompute (comp, k, seed) ->
                absorbFoldCompute subject inRepro locals (go false comp) (go false k) (go false seed)
            // RULE 4, A STRUCTURAL-MAP RELATION in the sharing partition (plan
            // 2.2, 3): `f(A, transpose(A, [0, 1]))` into an identity-sensitive
            // callee (a comm / anticomm group over the two positions) hands f
            // ONE root under two maps. Not exchangeable, so the partition is
            // all-distinct and the transpose keeps its copy -- the checker
            // already decided so; this records why. (A SymIdx operand's
            // transpose is erased by its certificate at typecheck: that call
            // IS `f(A, A)`, the shared instance, and never reaches here.)
            | IRApp ((IRVar (fid, _) as h), args, rt) ->
                let args' = args |> List.map (go false)
                (match Map.tryFind fid funcs with
                 | Some callee when not (callee.CommGroups.IsEmpty && callee.AntisymGroups.IsEmpty) ->
                    let groups = callee.CommGroups @ callee.AntisymGroups
                    let indexed = args' |> List.indexed
                    for (p, a) in indexed do
                        match tnfPeel a with
                        | (ops, IRVar (rid, _)) when not ops.IsEmpty && ops |> List.forall (function TnfSwap _ -> true | _ -> false) ->
                            for (q, a2) in indexed do
                                match a2 with
                                | IRVar (rid2, _) when rid2 = rid && q <> p
                                                      && groups |> List.exists (fun g -> List.contains p g && List.contains q g) ->
                                    let rn = nameOfExpr (IRVar (rid, IRTUnit))
                                    let pn = (List.item p callee.Params).Name
                                    let qn = (List.item q callee.Params).Name
                                    recordTransposeNF subject
                                        (Blade.Effects.Declined $"`{callee.SourceName}`'s arguments {rn} (`{qn}`) and transpose({rn}, {tnfChainText ops}) (`{pn}`) share ONE root under two coordinate maps: a transposed argument is its own class of the sharing partition (plan sections 2.2 and 3: the same root with a different oriented key is not exchangeable), so the call keeps the all-distinct elaboration (the generic `{callee.SourceName}`) and the transpose its copy")
                                        [ "a symmetric certificate (a SymIdx operand, whose transpose is the identity) erases the transpose at typecheck: that call IS the shared-root call and takes the shared instance (rule `sharing-partition`)" ]
                                | _ -> ()
                        | _ -> ()
                 | _ -> ())
                IRApp (h, args', rt)
            // A block let's value: an assignable ARRAY let (MutableArrayLets)
            // deep-copies an existing array (genVarAliasBinding, Interp.Core,
            // EmitLlvm.bindNamed), like an assignable module binding.
            | IRLet (id, v, body) ->
                let isCopy =
                    modul.MutableArrayLets.Contains id
                    && (try aliasableArrayType (typeOf v) with _ -> false)
                let v' = go isCopy v
                let body' = go false body
                match absorbFoldNest subject inRepro locals id v' body' with
                | Some r -> r
                | None -> IRLet (id, v', body')
            | ExprShape ([], _) -> e
            | ExprShape (children, rebuild) -> rebuild (children |> List.map (go false))
        go rootCopy root
    let bindings' =
        modul.Bindings |> List.map (fun b ->
            let v' = rewriteIn b.Name (Some b) Set.empty false (copyOnBind b) b.Value |> absorbPointwise b
            // Register a named chain over an earlier stored module binding.
            let plain =
                not (modul.ProviderReads.ContainsKey b.Id) && not (modul.CompoundInits.ContainsKey b.Id)
                && not (modul.SparseInits.ContainsKey b.Id) && not (modul.RandomInits.ContainsKey b.Id)
                && (try aliasableArrayType b.Type with _ -> false)
            (match tnfPeel v' with
             | (ops, (IRVar (rid, _) as rootExpr)) when plain && not ops.IsEmpty
                                                       && byId.ContainsKey rid && indexOf.[rid] < indexOf.[b.Id] ->
                let entry =
                    match Map.tryFind rid named with
                    | Some (opsR, rootR, midsR) -> (opsR @ ops, rootR, midsR @ [ b.Id ])
                    | None -> (ops, rootExpr, [ b.Id ])
                named <- named.Add (b.Id, entry)
             | _ -> ())
            { b with Value = v' })
    let functions' =
        modul.Functions |> List.map (fun f ->
            let locals = f.Params |> List.map (fun p -> p.VarId) |> Set.ofList
            { f with Body = rewriteIn f.SourceName None locals f.IsRepro false f.Body })
    // RULE 1c, THE SPLICED INTERMEDIATE: once every chain through `C` reads
    // its root, C is dead when nothing else reads it -- no binding payload,
    // function body, provider write or other module -- and it is not printed:
    // without a print selection every top-level binding prints (so C is
    // materialized anyway); a single-module selection (BLADE_PRINT / --print)
    // that excludes C licenses dropping it. Its value is a transpose chain of
    // a stored array: no failure, no effect, so dropping it is unobservable.
    let printSelection =
        if not singleModule then None
        else
        Blade.PrintSelection.names ()
    // a type whose extents name the binding (a run-time extent read) is a reader too
    let typeNames (cid: IRId) (t: IRType) =
        match stripUnits t with
        | ArrayElem at -> at.IndexTypes |> List.exists (fun ix -> (collectVarRefsIR ix.Extent).Contains cid)
        | _ -> false
    let mutable bindingsOut = bindings'
    // every reader of a module binding: binding payloads and types, function
    // bodies and captures, the range kernels minted above, provider writes,
    // other modules
    let readersOf (cid: IRId) : string list =
        (bindingsOut |> List.filter (fun b -> b.Id <> cid && (bindingPayload modul b |> List.exists (fun p -> (collectVarRefsIR p).Contains cid) || typeNames cid b.Type))
                     |> List.map (fun b -> $"`{b.Name}`"))
        @ (functions' |> List.filter (fun f -> (collectVarRefsIR f.Body).Contains cid || (f.Captures |> List.exists (fun c -> c.Id = cid)))
                      |> List.map (fun f -> $"function `{f.SourceName}`"))
        @ (mintedKernels |> Seq.filter (fun (_, k) -> (collectVarRefsIR k.Body).Contains cid) |> Seq.map (fun (_, k) -> $"`{k.SourceName}`") |> List.ofSeq)
        @ (modul.ProviderWrites |> Map.toList |> List.filter (fun (_, w) -> w.SourceId = cid) |> List.map (fun _ -> "a provider write"))
        @ (if foreignRefs.Contains cid then [ "another module" ] else [])
    let mutable pending = splicedThrough |> Map.toList |> List.map fst |> List.sortDescending
    let mutable decided : Set<IRId> = Set.empty
    let mutable progress = true
    while progress do
        progress <- false
        for cid in pending do
            if not (decided.Contains cid) then
                match bindingsOut |> List.tryFind (fun b -> b.Id = cid) with
                | None -> decided <- decided.Add cid
                | Some cb ->
                    let readers = readersOf cid
                    let through = splicedThrough.[cid] |> List.rev |> List.distinct |> List.map (fun s -> $"`{s}`") |> String.concat ", "
                    let ev = $"the chain(s) of {through} spliced through `{cb.Name}` and read its root"
                    match readers, printSelection with
                    // decided below, once no further elision can free it (a
                    // reader may itself be an intermediate this loop drops)
                    | _ :: _, _ -> ()
                    | [], None ->
                        let multiTxt = if singleModule then "" else "; a multi-module program has none"
                        recordTransposeNF cb.Name (Blade.Effects.Declined $"`{cb.Name}` is kept: every top-level binding is printed (no print selection, BLADE_PRINT / --print, excludes it{multiTxt})") [ ev ]
                        decided <- decided.Add cid
                    | [], Some sel when Blade.PrintSelection.mayName sel cb.Name ->
                        recordTransposeNF cb.Name (Blade.Effects.Declined $"`{cb.Name}` is kept: the print selection names it") [ ev ]
                        decided <- decided.Add cid
                    | [], Some _ ->
                        recordTransposeNF cb.Name Blade.Effects.Applied
                            [ $"`{cb.Name}` is elided: no reader is left, the print selection (BLADE_PRINT / --print) excludes it, and its value is a transpose chain of a stored array (no failure, no effect): no allocation, no copy"; ev ]
                        bindingsOut <- bindingsOut |> List.filter (fun b -> b.Id <> cid)
                        decided <- decided.Add cid
                        progress <- true
    // the intermediates still read by something
    for cid in pending do
        if not (decided.Contains cid) then
            match bindingsOut |> List.tryFind (fun b -> b.Id = cid) with
            | None -> ()
            | Some cb ->
                let readers = readersOf cid
                let through = splicedThrough.[cid] |> List.rev |> List.distinct |> List.map (fun s -> $"`{s}`") |> String.concat ", "
                recordTransposeNF cb.Name (Blade.Effects.Declined $"""`{cb.Name}` is kept: it is still read by {readers |> List.distinct |> String.concat ", "} (its storage is needed there)""")
                    [ $"the chain(s) of {through} spliced through `{cb.Name}` and read its root" ]
    // Minted range-map kernels sit right after the kernel they call (the
    // IRModule.DerivedFuncOrigins contract: emitted at its program point).
    let functionsOut =
        if mintedKernels.Count = 0 then functions'
        else
            let byOrigin = mintedKernels |> Seq.groupBy fst |> Seq.map (fun (o, cs) -> (o, cs |> Seq.map snd |> List.ofSeq)) |> Map.ofSeq
            let own = functions' |> List.map (fun f -> f.Id) |> Set.ofList
            let loose = mintedKernels |> Seq.filter (fun (o, _) -> not (own.Contains o)) |> Seq.map snd |> List.ofSeq
            (functions' |> List.collect (fun f -> match Map.tryFind f.Id byOrigin with Some cs -> f :: cs | None -> [ f ])) @ loose
    let derivedOut =
        mintedKernels |> Seq.fold (fun acc (o, c) -> if functions' |> List.exists (fun f -> f.Id = o) then Map.add c.Id o acc else acc) modul.DerivedFuncOrigins
    { modul with Bindings = bindingsOut; Functions = functionsOut; DerivedFuncOrigins = derivedOut }

/// THE STORED ROOT of a top-level alias chain for the read-only alias plan:
/// `freshStoredChain`'s fresh roots, and -- plan 2.1's second step, so that
/// every alias the CHECKER gives its root's identity is one this plan may
/// share storage for (TypeCheckSupport.storedRootForm admits it) -- a CALL of
/// a resolvable callable. A call's result may share storage with what the
/// call reaches (an argument the callee returns, a module array it captures),
/// so a call root also returns its REACH: every module binding and callable
/// the root's payload names, transitively through module payloads and
/// callable bodies and captures. The reach joins the never-written closure's
/// seeds; with all of it never written, sharing is unobservable. Returns how
/// the root is stored, the chain (the named binding first, the root last) and
/// the reach (empty for a fresh root).
let private aliasPlanChain (programFuncs: IRCallable list) (modul: IRModule) : IRBinding -> Result<string * IRBinding list * Set<IRId>, string> =
    let fresh = freshStoredChain modul
    let arr = List.toArray modul.Bindings
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    let payloadOf = intervalPayloadOf modul
    let payById = arr |> Array.map (fun b -> (b.Id, payloadOf b.Id b.Value)) |> Map.ofArray
    let callables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let funcsById = callables |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    // The reach, or the ids it cannot account for. A reference that is
    // neither this module's binding, a callable, nor a binder local to a
    // visited body (a parameter, a let, a match or loop binder) is another
    // module's binding or something unjudged: the reach FAILS CLOSED on it
    // (Codex review of this rule, finding 3: `Lib.get()` returning Lib's own
    // array, written by `Lib.poke()`, used to drop out of the reach).
    let localBinders (e: IRExpr) : Set<IRId> =
        let mutable s = Set.empty
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, _, _) -> s <- s.Add id
            | _ -> ()) e
        s
    let reachOf (root: IRExpr) : Result<Set<IRId>, Set<IRId>> =
        let mutable seen = Set.empty
        let mutable unknown = Set.empty
        let rec visitIn (locals: Set<IRId>) (id: IRId) =
            if not (seen.Contains id) then
                match Map.tryFind id payById, Map.tryFind id funcsById with
                | Some p, _ ->
                    seen <- seen.Add id
                    let ls = localBinders p
                    for r in collectVarRefsIR p do visitIn ls r
                | None, Some f ->
                    seen <- seen.Add id
                    let ls = Set.union (localBinders f.Body) (f.Params |> List.map (fun q -> q.VarId) |> Set.ofList)
                    for r in collectVarRefsIR f.Body do visitIn ls r
                    for c in f.Captures do visitIn Set.empty c.Id
                | None, None ->
                    if not (locals.Contains id) then unknown <- unknown.Add id
        let rootLocals = localBinders root
        for r in collectVarRefsIR root do visitIn rootLocals r
        if unknown.IsEmpty then Ok seen else Error unknown
    let rec callRoot (seen: Set<IRId>) (b: IRBinding) : Result<string * IRBinding list * Set<IRId>, string> option =
        match b.Value with
        | IRVar (aid, _) ->
            match Map.tryFind aid indexOf, Map.tryFind b.Id indexOf with
            | Some ai, Some bi when ai < bi && not (seen.Contains aid) ->
                callRoot (seen.Add b.Id) arr.[ai]
                |> Option.map (Result.map (fun (how, chain, reach) -> ($"an alias of `{arr.[ai].Name}`, {how}", b :: chain, reach)))
            | _ -> None
        | v when not (Map.containsKey b.Id modul.ProviderReads)
                 && not (modul.CompoundInits.ContainsKey b.Id || modul.SparseInits.ContainsKey b.Id || modul.RandomInits.ContainsKey b.Id) ->
            let how =
                match v with
                | CallOfNamed (fid, _, _, _) when funcsById.ContainsKey fid ->
                    Some $"a call of `{funcsById.[fid].SourceName}` (its reach is never written either)"
                // A `|> compute` that is no fresh pool (freshStoredChain
                // declined it): it may be the identity on what it names.
                | IRCompute inner when (match inner with IRVar _ -> false | _ -> true) ->
                    Some "a `|> compute` that may share what it names (its reach is never written either)"
                | _ -> None
            let isVirtual = match stripUnits b.Type with ArrayElem at -> at.IsVirtual | _ -> false
            match how with
            | Some how when not isVirtual ->
                match reachOf v with
                | Ok reach -> Some (Ok (how, [ b ], reach))
                | Error unknown ->
                    let ids = unknown |> Set.toList |> List.map (fun i -> $"__v{i}") |> String.concat ", "
                    Some (Error $"`{b.Name}`'s value reaches storage this module cannot judge ({ids}: another module's binding or an unscoped binder)")
            | _ -> None
        | _ -> None
    fun (b: IRBinding) ->
        match fresh b with
        | Ok (how, chain) -> Ok (how, chain, Set.empty)
        | Error why ->
            match callRoot Set.empty b with
            | Some r -> r
            | None -> Error why

/// THE READ-ONLY ALIAS PLAN'S ADMISSION of one candidate, gate-independent:
/// alias id `aid` of type `aty` bound to module binding `src`. Ok evidence or
/// Error first failed condition. Shared by planAliasElision and the
/// identity cross-check (recordAliasIdentity).
let private aliasPlanAdmission (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) =
    let chainOf = aliasPlanChain programFuncs modul
    let closure = lazy (aliasWriteClosure programFuncs foreignRefs modul)
    fun (aid: IRId) (aty: IRType) (src: IRBinding) ->
        if not (aliasableArrayType aty) then Error "the alias is not a plain or compact stored array (compound, sparse, ragged, orbit and virtual shapes keep their own path)"
        else
        match chainOf src with
        | Error why -> Error why
        | Ok (how, chain, reach) ->
            match closure.Force() (Set.union reach (Set.add aid (chain |> List.map (fun c -> c.Id) |> Set.ofList))) with
            | Error why -> Error $"the copy keeps the two independent: {why}"
            | Ok why -> Ok [ $"storage of `{src.Name}`: {how}"; why; "main's top level: the shared pool lives to program exit" ]

/// Plan the read-only aliases of the module's top-level bindings (rule 2
/// above): every ASSIGNABLE `let X = Y` over a stored array records a
/// decision; an admitted one reaches AliasElideTable.
let planAliasElision (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let enabled = transposeElideEnabled ()
    let byId = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let admission = aliasPlanAdmission programFuncs foreignRefs modul
    // One candidate: alias id `aid` (named `aname` in the record) bound to
    // module binding `src`; `rootChain` = the module bindings whose storage
    // it would share (src's alias chain to its fresh root).
    let decide (subject: string) (aid: IRId) (aname: string) (aty: IRType) (src: IRBinding) =
        let where = if byId.ContainsKey aid then "" else $" (a block let of `{subject}`)"
        let what = $"`{aname} = {src.Name}`{where}: a read-only alias of `{src.Name}`'s storage (no allocation, no pool copy)"
        let decision : Result<string list, string> = admission aid aty src
        // P5 step 2: the legality clause and the cost basis (the copy-on-bind
        // copy is the materialized baseline)
        let extra =
            [ Blade.CostModel.legalityLine "the never-written closure (aliasWriteClosure: no assignment, writing statement, writing callee or other module names either side), so the copy is unobservable"
              Blade.CostModel.aliasCopyLine (fun id -> match Map.tryFind id byId with Some b -> b.Name | None -> $"__v{id}") aty src.Name aname ]
        match decision with
        | Error why -> recordTransposeNF subject (Blade.Effects.Declined why) [ $"`{aname} = {src.Name}` deep-copies (copy-on-bind)" ]
        | Ok ev when not enabled -> recordTransposeNF subject (Blade.Effects.Declined "disabled by BLADE_TRANSPOSE_ELIDE") ((what :: ev) @ extra)
        | Ok ev ->
            AliasElideTable.record aid
            recordTransposeNF subject Blade.Effects.Applied ((what :: ev) @ extra)
    let isArray (t: IRType) = match stripUnits t with ArrayElem _ -> true | _ -> false
    for b in modul.Bindings do
        match b.Value with
        | IRVar (srcId, _) when byId.ContainsKey srcId
                               && (b.IsMutable || modul.MutableArrayLets.Contains b.Id)
                               && isArray b.Type ->
            decide b.Name b.Id b.Name b.Type byId.[srcId]
        | _ ->
            // The binding's top-level let CHAIN (a block value: genLetChainBinding
            // emits each let at main's top level, nothing scope-freed): an
            // assignable block let over a module array copies like a binding.
            let rec chainLets (e: IRExpr) =
                match e with
                | IRLet (id, v, rest) -> (id, v) :: chainLets rest
                | _ -> []
            for (id, v) in chainLets b.Value do
                match v with
                | IRVar (srcId, vty) when byId.ContainsKey srcId
                                          && modul.MutableArrayLets.Contains id
                                          && isArray vty ->
                    decide b.Name id $"__v{id}" vty byId.[srcId]
                | _ -> ()

// ===========================================================================
// LU COLUMN-MAJOR COMPANION (BLADE_LU_COMPANION, plan rule `lu-companion v1`;
// round 4 wave 4 lane K, gap G109 / PM32 "factor once, stream RHS"). On the
// LAPACK route `m.lu(A)` runs `?getrf` on a column-major scratch and stores
// the row-major (LU, piv) VALUE; every `m.lu_solve[_t](f, b)` then rebuilt
// the column-major factor from that value (8 n^2 transient bytes and an
// O(n^2) strided transpose per right-hand side) because `?getrs` cannot read
// the row-major factor in place (viewed column-major it is the transpose of
// L\U, the reverse of `?getrs`' layout -- `trans` does not help, and a
// row-major `?trsv` runs another kernel, losing bitwise `m.solve`).
//
// The factor KEEPS the `?getrf` scratch (and LAPACK's pivots) as a companion
// object beside the value, and each admitted solve hands that buffer to
// `?getrs`. The value is an exact copy of the scratch and the old per-solve
// rebuild was its exact inverse, so every solve's bits are unchanged (bitwise
// `m.solve` = `?gesv` = `?getrf` + `?getrs`, as before). SOUND only while the
// factor value equals the companion, i.e. nothing ever writes it: the
// never-written closure (`aliasWriteClosure`, the read-only alias plan's
// proof) over the factor binding -- any assignment to it, to a view / alias /
// projection of it (`let L = f[0]; L(0, 0) = 9.0`), a writing statement or
// callee naming any of them, or another module naming them declines. Main's
// top level only (the factor is a module binding, never scope-freed, and the
// companion is a main-scope object); a solve is admitted when it is the whole
// value of a later top-level binding whose two factor operands are the
// factor's projections (`m.lu_solve(f, b)`, or the destructured `LU` / `piv`
// names of the same tuple). The companion is released right after the LAST
// admitted solve; a solve the emitter reaches after the release (or a factor
// whose routed arm did not build it) answers through the copy, so a schedule
// the plan did not foresee costs the copy, never a value.
//
// The NATIVE route (the default: BLADE_BLAS off) already solves against the
// row-major factor in place -- nothing to save, recorded as a decline. The
// LLVM lane refuses `IRLu` (recorded, C++ fallback); the interpreter runs the
// native twin. Declined, with a gap row: a factor in a function body, a solve
// nested inside a larger expression.
// ===========================================================================

/// THE ALIAS IDENTITY RECORD (docs/plans/plan-identity-transpose-compute.md
/// 2.1 second step, rule `alias-identity v1`): every top-level `let B = A`
/// over a module array that the CHECKER judged for identity transparency
/// (Types.StoredAliasIdentity, TypeCheckSupport.recordStoredAliasMember) is
/// recorded here, applied with the checker's evidence or declined with its
/// first failed condition. An applied one is CROSS-CHECKED against the
/// read-only alias plan's own admission (aliasPlanAdmission, gate-
/// independent): the checker's never-written proof is meant to be stricter
/// than the plan's at every point, so the plan admitting it is part of the
/// evidence; a decline there would be named (the identity would still be
/// sound -- both names hold the same cells -- but the storage would be
/// copied). Identity is a language fact: BLADE_TRANSPOSE_ELIDE changes the
/// storage decision only, never this record's outcome.
let recordAliasIdentity (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let entries = Blade.Types.StoredAliasIdentity.all ()
    if not entries.IsEmpty then
        let byId = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
        let admission = aliasPlanAdmission programFuncs foreignRefs modul
        for e in entries do
            match Map.tryFind e.AliasId byId with
            | None -> ()
            | Some b ->
                let record outcome evidence =
                    if Blade.Effects.Decisions.active () then
                        Blade.Effects.Decisions.record
                            { Blade.Effects.Rule = "alias-identity"; Version = 1
                              Span = declSpanOf b.Name; Subject = b.Name
                              Outcome = outcome; Evidence = evidence }
                match e.Verdict with
                | Error why ->
                    record (Blade.Effects.Declined why)
                        [ $"`{e.Alias}` keeps its own identity: a copy of `{e.Root}`, a second array for the identity groups and the comm witness" ]
                | Ok ev ->
                    let storage =
                        match b.Value with
                        | IRVar (srcId, _) when byId.ContainsKey srcId ->
                            match admission b.Id b.Type byId.[srcId] with
                            | Ok _ ->
                                "storage: the read-only alias plan admits the same alias (it shares the pool unless BLADE_TRANSPOSE_ELIDE=0; the identity does not depend on that knob)"
                            | Error why ->
                                // AN INVARIANT, not a record (Codex review of
                                // the rule: both disagreements it found were an
                                // unsound identity). The checker's proof is
                                // built to be stricter than the plan's; a
                                // decline here means it admitted a write the
                                // plan sees, and the program's types may rest
                                // on a false equality. Fail loudly.
                                failwith $"internal: stored-alias identity disagreement: the checker gave `{e.Alias}` the identity of `{e.Root}` (never written), but the read-only alias plan declines the same alias: {why}"
                        | _ -> "storage: after lowering the binding is no plain alias the read-only alias plan reads"
                    record Blade.Effects.Applied (ev @ [ storage ])
