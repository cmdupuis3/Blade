// `blade test ir-traversal` (cleanup K10): the generic IR traversal is the
// class closure of IRExpr's fields.
//
// IR.(|ExprShape|) is THE one place that knows every IRExpr variant's
// immediate expression children (a wildcard-free match, so a new variant does
// not compile until it is listed there), and every generic walker --
// childrenOf / rebuildWith / mapIRExpr / iterIRExpr / collectVarRefsIR / the
// validators -- folds over it. Exhaustive over CONSTRUCTORS is not the same as
// exhaustive over FIELDS: a variant can be listed with a child forgotten. This
// block checks the fields, with an oracle that does not read ExprShape at all:
// reflection over the union's own declaration.
//
// THE ORACLE (`oracleChildren`). For a node, every field value is walked by its
// DECLARED type: an IRExpr is a child; a list, an option or a tuple is walked
// element by element in order; the CARRIER records and unions (the info
// records of the combinators, a match case, the boundary and sparse-key
// sources) are walked field by field in declaration order. The IR's TYPES
// (IRTypeG / IRIndexTypeG / IRArrayTypeG and what they hold) are OPAQUE --
// ExprShape's documented scope decision: extents carried in types are reached
// by the dedicated extent paths, never by generic traversal. A field type that
// can hold an IRExpr and is none of the above is an UNCLASSIFIED type, a
// failure: a new carrier nobody classified cannot slip through.
//
// The checks, over every single-file corpus program that lowers (every node
// reachable from a binding value, a function body or a module side table):
//   (a) rebuild identity: `rebuildWith e (childrenOf e)` is `e` -- the same
//       variant, every non-child field equal, every child the SAME reference;
//   (b) the oracle finds exactly the children ExprShape returns, as the same
//       references, in the same order;
// both checks again on a SYNTHESIZED instance of every IRExpr case, in two
// field shapes (each option Some / None, each carrier union's richest case /
// its first), so a constructor no corpus program reaches is covered too; and,
// over the union's declaration alone, every field of every IRExpr case has a
// classified type.
module Blade.Tests.IRTraversalTests

open System
open System.Collections.Generic
open Microsoft.FSharp.Reflection
open Blade
open Blade.IR
open Blade.Tests.TestHarness

let private exprType = typeof<IRExpr>

/// The IR's type family: opaque to generic traversal (see the header).
let private opaqueGenerics : Type list =
    [ typedefof<Blade.Types.IRTypeG<_>>
      typedefof<Blade.Types.IRIndexTypeG<_>>
      typedefof<Blade.Types.IRArrayTypeG<_>> ]

let private isOpaque (t: Type) =
    t.IsGenericType && (opaqueGenerics |> List.contains (t.GetGenericTypeDefinition()))

/// The records and unions whose fields are walked in declaration order.
let private carriers : Type list =
    [ typeof<IRMatchCase>; typeof<MethodForInfo>; typeof<ObjectForInfo>; typeof<ApplyInfo>
      typeof<ComposeApplyInfo>; typeof<AlignSpec>; typeof<BoundaryMode>; typeof<SparseKeysSource> ]

let private isList (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>>
let private isOption (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>

/// The types a value of type `t` is made of: generic arguments, tuple
/// elements, record fields, union case fields.
let private partsOf (t: Type) : Type list =
    if t.IsPrimitive || t = typeof<string> || t.IsEnum then []
    else
    let generic = if t.IsGenericType then t.GetGenericArguments() |> List.ofArray else []
    let fields =
        if FSharpType.IsTuple t then FSharpType.GetTupleElements t |> List.ofArray
        elif FSharpType.IsRecord(t, true) then
            FSharpType.GetRecordFields(t, true) |> Array.map _.PropertyType |> List.ofArray
        elif FSharpType.IsUnion(t, true) then
            FSharpType.GetUnionCases(t, true)
            |> Array.collect (fun c -> c.GetFields() |> Array.map _.PropertyType) |> List.ofArray
        else []
    generic @ fields

/// Can a value of type `t` hold an IRExpr? Decided ONCE over the closed set
/// of types reachable from IRExpr's declaration (every field type the walks
/// below can meet is in it), as the least fixpoint of "is IRExpr, or one of
/// its parts can".
let private holdsTable : Lazy<IReadOnlyDictionary<Type, bool>> =
    lazy
        let universe = HashSet<Type>()
        let rec reach (t: Type) = if universe.Add t then partsOf t |> List.iter reach
        reach exprType
        let holding = HashSet<Type>([ exprType ])
        let mutable changed = true
        while changed do
            changed <- false
            for t in universe do
                if not (holding.Contains t) && partsOf t |> List.exists holding.Contains then
                    holding.Add t |> ignore
                    changed <- true
        let table = Dictionary<Type, bool>()
        for t in universe do table.[t] <- holding.Contains t
        table :> IReadOnlyDictionary<Type, bool>

let private holds (t: Type) =
    match holdsTable.Value.TryGetValue t with
    | true, v -> v
    | _ -> failwith $"type {t.FullName} is outside the types reachable from IRExpr's declaration"

/// How the oracle treats a field of declared type `t`; None = unclassified.
type private Kind =
    | KExpr
    | KNone          // holds no IRExpr, or opaque (the IR's types)
    | KList of Type
    | KOption of Type
    | KTuple of Type[]
    | KCarrier

let private kindOf (t: Type) : Kind option =
    if t = exprType then Some KExpr
    elif isOpaque t || not (holds t) then Some KNone
    elif isList t then Some (KList (t.GetGenericArguments().[0]))
    elif isOption t then Some (KOption (t.GetGenericArguments().[0]))
    elif FSharpType.IsTuple t then Some (KTuple (FSharpType.GetTupleElements t))
    elif carriers |> List.contains t then Some KCarrier
    else None

let private unclassified (t: Type) : 'a =
    failwith $"unclassified field type {t.FullName}: it can hold an IRExpr and is neither a child, a list / option / tuple of children, a carrier record or union, nor one of the IR's (opaque) types"

/// Every child expression held by value `v` of declared type `t`, in order.
let rec private exprsIn (t: Type) (v: obj) (acc: List<IRExpr>) : unit =
    match kindOf t with
    | Some KExpr -> acc.Add(v :?> IRExpr)
    | Some KNone -> ()
    | Some (KList et) -> for x in (v :?> System.Collections.IEnumerable) do exprsIn et x acc
    | Some (KOption et) ->
        if not (isNull v) then
            let _, fs = FSharpValue.GetUnionFields(v, t)
            exprsIn et fs.[0] acc
    | Some (KTuple ets) -> Array.iter2 (fun et x -> exprsIn et x acc) ets (FSharpValue.GetTupleFields v)
    | Some KCarrier ->
        if FSharpType.IsRecord(t, true) then
            Array.iter2 (fun (p: Reflection.PropertyInfo) x -> exprsIn p.PropertyType x acc)
                (FSharpType.GetRecordFields(t, true)) (FSharpValue.GetRecordFields(v, true))
        else
            let case, fs = FSharpValue.GetUnionFields(v, t, true)
            Array.iter2 (fun (p: Reflection.PropertyInfo) x -> exprsIn p.PropertyType x acc) (case.GetFields()) fs
    | None -> unclassified t

/// IRExpr's cases, with a precomputed tag reader and field reader per case
/// (reflection is per node here, so it is read once).
let private exprCases = FSharpType.GetUnionCases(exprType)
let private exprTag = FSharpValue.PreComputeUnionTagReader(exprType)
let private exprReaders = exprCases |> Array.map (fun c -> FSharpValue.PreComputeUnionReader c)
let private exprFieldTypes = exprCases |> Array.map (fun c -> c.GetFields() |> Array.map _.PropertyType)

/// The oracle: the immediate children of a node, from its declaration alone.
let private oracleChildren (e: IRExpr) : List<IRExpr> =
    let acc = List<IRExpr>()
    let tag = exprTag (box e)
    Array.iter2 (fun t x -> exprsIn t x acc) exprFieldTypes.[tag] (exprReaders.[tag] (box e))
    acc

/// Equal as values with every CHILD compared by reference: the rebuild
/// identity of check (a), at the cost of one node (not a deep compare).
let rec private sameShallow (t: Type) (a: obj) (b: obj) : bool =
    match kindOf t with
    | Some KExpr -> obj.ReferenceEquals(a, b)
    | Some KNone -> obj.Equals(a, b)
    | Some (KList et) ->
        let xs = [ for x in (a :?> System.Collections.IEnumerable) -> x ]
        let ys = [ for y in (b :?> System.Collections.IEnumerable) -> y ]
        xs.Length = ys.Length && List.forall2 (sameShallow et) xs ys
    | Some (KOption et) ->
        match isNull a, isNull b with
        | true, true -> true
        | false, false -> sameShallow et (snd (FSharpValue.GetUnionFields(a, t))).[0] (snd (FSharpValue.GetUnionFields(b, t))).[0]
        | _ -> false
    | Some (KTuple ets) ->
        Array.forall id (Array.map3 sameShallow ets (FSharpValue.GetTupleFields a) (FSharpValue.GetTupleFields b))
    | Some KCarrier ->
        if FSharpType.IsRecord(t, true) then
            Array.forall id
                (Array.map3 (fun (p: Reflection.PropertyInfo) x y -> sameShallow p.PropertyType x y)
                    (FSharpType.GetRecordFields(t, true)) (FSharpValue.GetRecordFields(a, true)) (FSharpValue.GetRecordFields(b, true)))
        else
            let ca, fa = FSharpValue.GetUnionFields(a, t, true)
            let cb, fb = FSharpValue.GetUnionFields(b, t, true)
            ca.Tag = cb.Tag
            && Array.forall id (Array.map3 (fun (p: Reflection.PropertyInfo) x y -> sameShallow p.PropertyType x y) (ca.GetFields()) fa fb)
    | None -> unclassified t

let private sameNode (a: IRExpr) (b: IRExpr) : bool =
    let ta, tb = exprTag (box a), exprTag (box b)
    ta = tb
    && Array.forall id (Array.map3 sameShallow exprFieldTypes.[ta] (exprReaders.[ta] (box a)) (exprReaders.[tb] (box b)))

/// Every root expression of a lowered program: binding values, function
/// bodies, and the expression-valued module side tables.
let private rootsOf (ir: IRProgram) : IRExpr list =
    [ for m in ir.Modules do
        for b in m.Bindings do yield b.Value
        for f in m.Functions do yield f.Body
        for KeyValue (_, (d, k)) in m.CompoundInits do yield d; yield k
        for KeyValue (_, s) in m.SparseInits do yield s
        for KeyValue (_, r) in m.RandomInits do
            match r with
            | FillModulus e -> yield e
            | RandGen (_, key, pars, weights, address) ->
                yield key
                yield! pars
                match weights with Some (w, _) -> yield w | None -> ()
                match address with Some (s, o) -> yield s; yield o | None -> () ]

type private ProgramOutcome =
    | NotLowered
    | Checked of nodes: int * cases: Set<int> * failures: string list

/// Checks (a) and (b) on one node: None, or why it fails.
let private checkNode (e: IRExpr) : string option =
    let name = exprCases.[exprTag (box e)].Name
    try
        let children = childrenOf e
        let oracle = oracleChildren e
        if oracle.Count <> children.Length
           || not (Seq.forall2 (fun (a: IRExpr) b -> obj.ReferenceEquals(a, b)) oracle children) then
            Some $"{name}: ExprShape gives {children.Length} children, the field oracle {oracle.Count} (or another order)"
        elif not (sameNode (rebuildWith e children) e) then
            Some $"{name}: rebuildWith e (childrenOf e) is not e"
        else None
    with ex -> Some $"{name}: {ex.Message}"

/// Check every node reachable from the roots. Iterative (a long let spine
/// is deep), each node once per occurrence.
let private checkProgram (ir: IRProgram) : int * Set<int> * string list =
    let failures = List<string>()
    let mutable nodes = 0
    let mutable cases = Set.empty
    let stack = Stack<IRExpr>(rootsOf ir |> List.rev)
    while stack.Count > 0 && failures.Count < 5 do
        let e = stack.Pop()
        nodes <- nodes + 1
        let tag = exprTag (box e)
        cases <- cases.Add tag
        match checkNode e with
        | Some why -> failures.Add why
        | None -> ()
        for c in List.rev (childrenOf e) do stack.Push c
    nodes, cases, List.ofSeq failures

/// SYNTHESIZED instances of every IRExpr case, so a constructor no corpus
/// program reaches is checked too. Every IRExpr field is a distinct leaf, a
/// list holding expressions has two, the IR's types and every other field
/// that holds no expression take their default (a null reference: ExprShape
/// never looks at them). `full` picks, in each carrier union, a case holding
/// the most expressions and Some for every option; otherwise the first case
/// and None -- so both arms of IRShift / IRAlign / IRDisplayEmit / IRReduce /
/// IRRange / IRSparseKeys / IRRandAt / a match guard are built.
let private synthesize (full: bool) (case: UnionCaseInfo) : IRExpr =
    let next = ref 0L
    let leaf () =
        next.Value <- next.Value + 1L
        IRLit (IRLitInt next.Value)
    let rec value (t: Type) : obj =
        if t = exprType then box (leaf ())
        elif isList t then
            let et = t.GetGenericArguments().[0]
            let cases = FSharpType.GetUnionCases t
            let empty = FSharpValue.MakeUnion(cases |> Array.find (fun c -> c.Name = "Empty"), [||])
            let cons = cases |> Array.find (fun c -> c.Name = "Cons")
            if holds et then
                FSharpValue.MakeUnion(cons, [| value et; FSharpValue.MakeUnion(cons, [| value et; empty |]) |])
            else empty
        elif isOpaque t || not (holds t) then
            if t.IsValueType then Activator.CreateInstance t else null
        elif isOption t then
            if full then FSharpValue.MakeUnion(FSharpType.GetUnionCases(t).[1], [| value (t.GetGenericArguments().[0]) |])
            else null
        elif FSharpType.IsTuple t then FSharpValue.MakeTuple(FSharpType.GetTupleElements t |> Array.map value, t)
        elif FSharpType.IsRecord(t, true) then
            FSharpValue.MakeRecord(t, FSharpType.GetRecordFields(t, true) |> Array.map (fun p -> value p.PropertyType), true)
        elif FSharpType.IsUnion(t, true) then
            let cases = FSharpType.GetUnionCases(t, true)
            let weight (c: UnionCaseInfo) = c.GetFields() |> Array.filter (fun p -> holds p.PropertyType) |> Array.length
            let c = if full then cases |> Array.maxBy weight else cases.[0]
            FSharpValue.MakeUnion(c, c.GetFields() |> Array.map (fun p -> value p.PropertyType), true)
        else unclassified t
    FSharpValue.MakeUnion(case, case.GetFields() |> Array.map (fun p -> value p.PropertyType)) :?> IRExpr

/// The union's declaration alone: every field of every case has a classified type.
let private declarationFailures () : string list =
    [ for c in exprCases do
        for p in c.GetFields() do
            let rec classified (t: Type) =
                match kindOf t with
                | None -> false
                | Some (KList et) | Some (KOption et) -> classified et
                | Some (KTuple ets) -> Array.forall classified ets
                | Some KCarrier ->
                    if FSharpType.IsRecord(t, true) then
                        FSharpType.GetRecordFields(t, true) |> Array.forall (fun f -> classified f.PropertyType)
                    else
                        FSharpType.GetUnionCases(t, true) |> Array.forall (fun uc -> uc.GetFields() |> Array.forall (fun f -> classified f.PropertyType))
                | Some KExpr | Some KNone -> true
            if not (classified p.PropertyType) then
                yield $"{c.Name}.{p.Name}: {p.PropertyType.FullName}" ]

/// The block. The two declaration checks are fast and run in the default suite
/// (`runIrTraversalFastTests`); the corpus pass (minutes) runs only under
/// `blade test ir-traversal` (`runIrTraversalTests`, all three).
let private runBlock (withCorpus: bool) : BlockResult =
    let blockName = if withCorpus then "IR Traversal" else "IR Traversal (declaration)"
    printHeader "IR traversal, declaration checks: ExprShape against a reflection oracle over IRExpr's fields (K10)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable names = []
    let record (ok: bool) (name: string) (detail: string) =
        if ok then passed <- passed + 1; resultLine Pass name detail
        else failed <- failed + 1; names <- names @ [ name ]; resultLine Fail name detail
    // 1. the declaration
    let caseCount = exprCases.Length
    let decl = declarationFailures ()
    record decl.IsEmpty "every IRExpr field has a classified type"
        (if decl.IsEmpty then $"{caseCount} cases" else String.concat "; " decl)
    // 2. every constructor, synthesized (reached by the corpus or not)
    let synthFailures =
        [ for c in exprCases do
            for full in [ false; true ] do
                match (try checkNode (synthesize full c) with ex -> Some $"{c.Name}: cannot synthesize ({ex.Message})") with
                | Some why -> yield why
                | None -> () ]
    record synthFailures.IsEmpty "rebuild identity and the field oracle on every constructor, synthesized"
        (if synthFailures.IsEmpty then $"{caseCount} cases x 2 field shapes" else String.concat "; " synthFailures)
    // 3. the corpus (standalone key only)
    if withCorpus then
        // Its own header, so the Timings table times the fast pair and the corpus pass apart.
        printHeader "IR traversal, corpus pass: every node of every single-file program that lowers (K10)"
        let programs =
            Blade.Tests.Corpus.singleFileCategories ()
            |> List.collect (fun cat -> Blade.Tests.Corpus.category cat |> List.map (fun (n, src) -> $"{cat}/{n}", src))
            |> Array.ofList
        let outcomes = Array.create programs.Length NotLowered
        let options = Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = 4)
        Threading.Tasks.Parallel.For(0, programs.Length, options, fun i ->
            let _, src = programs.[i]
            // On a 64 MB stack, as every front end in the binary runs (Main, the
            // harness): a pool thread's 1 MB overflows on the deep programs.
            outcomes.[i] <-
                Blade.Runtime.runOnLargeStack (fun () ->
                    try
                        match fst (Lowering.lowerCaptured src) with
                        | Ok ir -> Checked (checkProgram ir)
                        | Error _ -> NotLowered
                    with _ -> NotLowered)) |> ignore
        let mutable lowered = 0
        let mutable nodes = 0L
        let mutable seen = Set.empty
        let mutable bad = 0
        for i in 0 .. programs.Length - 1 do
            match outcomes.[i] with
            | NotLowered -> ()
            | Checked (n, cs, fs) ->
                lowered <- lowered + 1
                nodes <- nodes + int64 n
                seen <- Set.union seen cs
                if not fs.IsEmpty then
                    bad <- bad + 1
                    record false (fst programs.[i]) (String.concat "; " fs)
        let unreached =
            exprCases
            |> Array.filter (fun c -> not (seen.Contains c.Tag)) |> Array.map _.Name |> List.ofArray
        record (lowered > 0 && bad = 0)
            "rebuild identity and the field oracle over the corpus"
            ($"{lowered} of {programs.Length} programs lowered, {nodes} nodes, {seen.Count} of {caseCount} constructors reached"
             + (if unreached.IsEmpty then "" else "; not reached: " + String.concat ", " unreached))
    printFooter blockName [ $"{passed} passed"; $"{failed} failed"; "0 skipped" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = 0; FailedNames = names }

/// `blade test ir-traversal`: the declaration checks and the corpus pass.
let runIrTraversalTests () : BlockResult = runBlock true

/// The default suite's part: the two fast declaration checks only.
let runIrTraversalFastTests () : BlockResult = runBlock false
