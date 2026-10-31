// THE STATIC COST MODEL v1 (docs/plans/plan-identity-transpose-compute.md
// section 6 P5; rule 7: eliminating a buffer is never ASSUMED cheaper).
//
// A small, pure, lane-neutral pricing of a dense map, fold or co-iteration
// over known extents, mirroring the `cost` record of
// proofs/BladeCompositionalOptimization.v field for field:
//
//   Record cost := { work : nat; traffic : nat; peak : nat }
//   compose a b := { work a + work b; traffic a + traffic b;
//                    max (peak a) (peak b) }
//
//   WORK     scalar operations: per cell, the kernel body's operations (each
//            `+ - *`, comparison, `&&`/`||`, negation, cast, abs / floor /
//            ceil, the two halves of an fma: 1; a DIVISION -- float or integer
//            `/`, `%`, `sqrt` -- `divisionOps`; a TRANSCENDENTAL -- every other
//            libm intrinsic, `^` (pow), atan2 / log_base -- `transcendentalOps`;
//            an `if` its condition plus its dearer arm), times the cells. A
//            fold of N cells is N combines (convention: an unseeded fold's head
//            counts as one, which is exact for the elided form's seeded chain).
//            A data movement (a copy) is no work: it is traffic.
//   TRAFFIC  cells moved: each stored operand cell read (one read per operand
//            per output cell -- no cache reuse is modelled) plus each pool cell
//            written, kept BY OPERAND so a record can say whose cells moved; a
//            range operand is computed, not read (0).
//   PEAK     live cells of the pools the priced region allocates, at the
//            statement (inputs excluded: they are live under every candidate);
//            model v1 frees nothing inside a statement, and a pool stays live
//            through every later step that still reads it.
//
// Quantities are POLYNOMIALS in extent symbols: a literal extent is a
// constant, a NAMED extent (a scalar name, a parameter, `extents(a)`) is a
// symbol, so an unknown-but-named extent prices exactly as a formula. Any
// other extent (a compound mask's cardinality, a ragged lookup, arithmetic the
// model cannot spell) is NOT PRICED -- the model never guesses. A comparison
// of two symbolic quantities is decided coefficient-wise (a <= b for every
// assignment of naturals iff every coefficient of b - a is >= 0); anything it
// cannot decide is "not proved", which every caller reads as "keep the
// baseline". Optimality is claimed only for an enumerated candidate family
// under this model; the model is a deterministic accounting, not a measured
// time.
module Blade.CostModel

open Blade.Types
open Blade.IR

/// The model's version, printed in every cost line (`model v1`).
let version = 1

/// Declared weights, in scalar operations (model v1).
let divisionOps = 4L
let transcendentalOps = 16L

/// The recompute selection's cheap-producer cap, in model operations per
/// cell (BLADE_RECOMPUTE admission): a producer above it is not a candidate.
let cheapProducerOps = 4L

// ---------------------------------------------------------------------------
// Polynomials in extent symbols (integer coefficients; a monomial is the
// sorted list of its symbols, [] the constant term).
// ---------------------------------------------------------------------------

type Poly = { Terms: Map<string list, int64> }

module Poly =
    let private norm (m: Map<string list, int64>) = { Terms = m |> Map.filter (fun _ c -> c <> 0L) }
    let zero : Poly = { Terms = Map.empty }
    let konst (n: int64) : Poly = norm (Map.ofList [ ([], n) ])
    let sym (s: string) : Poly = { Terms = Map.ofList [ ([ s ], 1L) ] }
    // CHECKED arithmetic: an overflow raises (System.OverflowException), which
    // every pricing entry point turns into "not priced" -- a wrapped count
    // would make `leq` prove nonsense (Codex review).
    let add (a: Poly) (b: Poly) : Poly =
        b.Terms |> Map.fold (fun (m: Map<string list, int64>) k c ->
            Map.add k (Checked.(+) c (match Map.tryFind k m with Some x -> x | None -> 0L)) m) a.Terms
        |> norm
    let scale (k: int64) (a: Poly) : Poly = a.Terms |> Map.map (fun _ c -> Checked.(*) c k) |> norm
    let neg (a: Poly) = scale -1L a
    let sub (a: Poly) (b: Poly) = add a (neg b)
    let mul (a: Poly) (b: Poly) : Poly =
        seq { for KeyValue (ka, ca) in a.Terms do
                for KeyValue (kb, cb) in b.Terms do
                    yield (List.sort (ka @ kb), Checked.(*) ca cb) }
        |> Seq.fold (fun (m: Map<string list, int64>) (k, c) ->
            Map.add k (Checked.(+) c (match Map.tryFind k m with Some x -> x | None -> 0L)) m) Map.empty
        |> norm
    let sum (xs: Poly list) = xs |> List.fold add zero
    let isZero (a: Poly) = a.Terms.IsEmpty
    /// The value when the polynomial is a constant.
    let tryConst (a: Poly) : int64 option =
        if a.Terms.IsEmpty then Some 0L
        elif a.Terms.Count = 1 && a.Terms.ContainsKey [] then Some a.Terms.[[]]
        else None
    /// a <= b for EVERY assignment of naturals to the symbols, proved
    /// coefficient-wise (sufficient; false means "not proved").
    let leq (a: Poly) (b: Poly) : bool = (sub b a).Terms |> Map.forall (fun _ c -> c >= 0L)
    let render (a: Poly) : string =
        if a.Terms.IsEmpty then "0"
        else
            let terms =
                a.Terms |> Map.toList
                |> List.sortBy (fun (k, _) -> (-(List.length k), k))
            let one (k: string list, c: int64) =
                let mag = abs c
                let body =
                    match k with
                    | [] -> string mag
                    | _ ->
                        let syms = String.concat "*" k
                        if mag = 1L then syms else $"{mag}*{syms}"
                (c < 0L, body)
            terms |> List.mapi (fun i t ->
                let (negative, body) = one t
                if i = 0 then (if negative then "-" + body else body)
                else (if negative then " - " + body else " + " + body))
            |> String.concat ""

// ---------------------------------------------------------------------------
// The cost record (BladeCompositionalOptimization.v `cost`, `compose`,
// `cost_le`). PEAK is a max of polynomials, kept as an antichain under
// `Poly.leq` (a max the model cannot simplify stays a max).
// ---------------------------------------------------------------------------

type Peak = Poly list

module Peak =
    let ofPoly (p: Poly) : Peak = [ p ]
    let zero : Peak = [ Poly.zero ]
    let max (a: Peak) (b: Peak) : Peak =
        let all = a @ b |> List.distinct
        let kept = all |> List.filter (fun x -> not (all |> List.exists (fun y -> y <> x && Poly.leq x y)))
        if kept.IsEmpty then zero else kept
    let leq (a: Peak) (b: Peak) : bool = a |> List.forall (fun x -> b |> List.exists (fun y -> Poly.leq x y))
    let render (a: Peak) : string =
        match a with
        | [] -> "0"
        | [ p ] -> Poly.render p
        | ps -> "max(" + (ps |> List.map Poly.render |> List.sort |> String.concat ", ") + ")"

type Cost = { Work: Poly; Traffic: Poly; Peak: Peak }

let zeroCost : Cost = { Work = Poly.zero; Traffic = Poly.zero; Peak = Peak.zero }

/// Sequential composition: work and traffic add, peak is the max (each step's
/// peak is its ABSOLUTE live footprint, as in the Rocq model).
let compose (a: Cost) (b: Cost) : Cost =
    { Work = Poly.add a.Work b.Work; Traffic = Poly.add a.Traffic b.Traffic; Peak = Peak.max a.Peak b.Peak }

/// `cost_le`: componentwise, each component proved by `Poly.leq`.
let costLe (a: Cost) (b: Cost) : bool =
    Poly.leq a.Work b.Work && Poly.leq a.Traffic b.Traffic && Peak.leq a.Peak b.Peak

/// A priced step with its traffic kept BY OPERAND (in first-mention order);
/// `costOf` forgets the breakdown into the Rocq record.
type Step = { SWork: Poly; STraffic: (string * Poly) list; SPeak: Peak }

let emptyStep : Step = { SWork = Poly.zero; STraffic = []; SPeak = Peak.zero }

let private mergeTraffic (a: (string * Poly) list) (b: (string * Poly) list) =
    b |> List.fold (fun (acc: (string * Poly) list) (k, v) ->
        if acc |> List.exists (fun (k2, _) -> k2 = k) then acc |> List.map (fun (k2, v2) -> if k2 = k then (k2, Poly.add v2 v) else (k2, v2))
        else acc @ [ (k, v) ]) a

let seqStep (a: Step) (b: Step) : Step =
    { SWork = Poly.add a.SWork b.SWork; STraffic = mergeTraffic a.STraffic b.STraffic; SPeak = Peak.max a.SPeak b.SPeak }

let seqSteps (xs: Step list) : Step = xs |> List.fold seqStep emptyStep

let costOf (s: Step) : Cost =
    { Work = s.SWork; Traffic = s.STraffic |> List.map snd |> Poly.sum; Peak = s.SPeak }

// ---------------------------------------------------------------------------
// Pricing IR (pure; every failure is a "not priced" reason).
// ---------------------------------------------------------------------------

let private kindName (e: IRExpr) : string =
    let n = e.GetType().Name
    if n.StartsWith "IR" then n.Substring 2 else n

/// The polynomial of one extent expression. `nameOf` spells a named extent
/// (a scalar variable, a parameter, an array whose extent is read).
let rec extentPoly (nameOf: IRId -> string) (e: IRExpr) : Result<Poly, string> =
    match e with
    | IRLit (IRLitInt n) -> Ok (Poly.konst n)
    | IRVar (id, ty) when (Blade.IRTotality.scalarElem ty).IsSome -> Ok (Poly.sym (nameOf id))
    | IRParam (nm, _, ty) when (Blade.IRTotality.scalarElem ty).IsSome -> Ok (Poly.sym nm)
    // a SIZE VARIABLE (`IRTNat`): an extent named but not yet a number -- the
    // canonical symbolic `n`; the lowering's sentinels for run-time
    // cardinalities (a compound / equivalence class, a group size, an error
    // marker, the anonymous `?`) are not sizes the model can name
    | IRParam (nm, _, IRTNat _)
        when nm <> "" && nm <> "?" && nm <> "compound" && nm <> "equiv"
             && (not (nm.StartsWith "__") || nm.StartsWith "__hm" || nm.Contains "_inferred_n") ->
        Ok (Poly.sym nm)
    | IRExtent (IRVar (id, _), d) -> Ok (Poly.sym (if d = 0 then $"extents({nameOf id})" else $"extents({nameOf id}, {d})"))
    | IRExtent (IRParam (nm, _, _), d) -> Ok (Poly.sym (if d = 0 then $"extents({nm})" else $"extents({nm}, {d})"))
    | IRBinOp (_, (IRAdd | IRSub | IRMul as op), a, b, _) ->
        match extentPoly nameOf a, extentPoly nameOf b with
        | Ok pa, Ok pb ->
            Ok (match op with IRAdd -> Poly.add pa pb | IRSub -> Poly.sub pa pb | _ -> Poly.mul pa pb)
        | Error why, _ | _, Error why -> Error why
    | IRExtent (inner, _) -> Error $"an extent read of a {kindName inner} value the model cannot name (known only at run time)"
    | _ -> Error $"an extent known only at run time that the model cannot spell (a {kindName e}: a computed bound, a mask's cardinality, a ragged lookup)"

let private binom (n: int64) (r: int) : int64 option =
    if n < 0L || r < 0 then None
    elif int64 r > n then Some 0L
    else
        let mutable acc = System.Numerics.BigInteger.One
        for i in 1 .. r do
            acc <- acc * System.Numerics.BigInteger(n - int64 r + int64 i) / System.Numerics.BigInteger(i)
        if acc > System.Numerics.BigInteger(System.Int64.MaxValue / 4L) then None else Some (int64 acc)

/// The STORED cells of an array type (its pool): a dense record contributes
/// its extent to the rank-th power; a SymIdx<r, n> pool C(n + r - 1, r) cells,
/// an AntisymIdx<r, n> pool C(n, r) (no stored diagonal); a compact record
/// over a symbolic extent, a Hermitian, structured or virtual record is not
/// priced.
let cellsOf (nameOf: IRId -> string) (t: IRType) : Result<Poly, string> =
    match stripUnits t with
    | ArrayElem at when at.IsVirtual -> Error "a virtual array has no stored cells"
    | ArrayElem at ->
        at.IndexTypes |> List.fold (fun (acc: Result<Poly, string>) (ix: IRIndexType) ->
            match acc with
            | Error _ -> acc
            | Ok p ->
                if ix.IxKind <> IxKPlain then Error "a structured index record (compound, sparse, ragged, orbit) the model does not price"
                else
                match extentPoly nameOf ix.Extent with
                | Error why -> Error why
                | Ok ext ->
                    match ix.Symmetry with
                    | SymNone -> Ok (Poly.mul p (List.replicate (max 1 ix.Rank) ext |> List.fold Poly.mul (Poly.konst 1L)))
                    | SymSymmetric | SymAntisymmetric when ix.Rank = 1 -> Ok (Poly.mul p ext)
                    | (SymSymmetric | SymAntisymmetric) as s ->
                        match Poly.tryConst ext with
                        | Some n ->
                            let cells = if s = SymSymmetric then binom (n + int64 ix.Rank - 1L) ix.Rank else binom n ix.Rank
                            (match cells with
                             | Some c -> Ok (Poly.mul p (Poly.konst c))
                             | None -> Error "a compact pool too large to count")
                        | None -> Error "a compact pool over a symbolic extent (its binomial count is not a polynomial with integer coefficients)"
                    | _ -> Error "a Hermitian or wreath pool the model does not count") (Ok (Poly.konst 1L))
    | _ -> Error "not an array"

/// The cells a map WRITES: its output type's pool when that is priced, else
/// (an output extent the deduction left as a size variable) its iteration
/// space -- a co-iteration's shared operand cells (the apply checks they
/// agree), a cross product's product of operand cells -- for a scalar kernel.
let mapCells (nameOf: IRId -> string) (p: ApplyInfo) : Result<Poly, string> =
    match cellsOf nameOf p.OutputType with
    | Ok n -> Ok n
    | Error why when p.KernelOutputRank = 0 && not p.ArrayTypes.IsEmpty ->
        let each = p.ArrayTypes |> List.map (fun at -> cellsOf nameOf (IRTArrow (at.IndexTypes |> List.map SIdx, at.ElemType, None)))
        match each |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
        | Some e -> Error $"{why}; its operands: {e}"
        | None ->
            let cells = each |> List.map (function Ok c -> c | Error _ -> Poly.zero)
            if p.IsCoIteration || not p.SharedIndexTypes.IsEmpty || cells.Length = 1 then Ok cells.Head
            else Ok (cells |> List.fold Poly.mul (Poly.konst 1L))
    | Error why -> Error why

/// Operations of one scalar expression, per evaluation, and the HEAVY
/// operations it contains (a division or a transcendental, by name).
type Ops = { Count: int64; Heavy: string list }

let private opsZero = { Count = 0L; Heavy = [] }
let private opsAdd (a: Ops) (b: Ops) = { Count = a.Count + b.Count; Heavy = a.Heavy @ b.Heavy |> List.distinct }
let private opsOne (n: int64) (heavy: string list) = { Count = n; Heavy = heavy }

let rec private exprOps (resolve: IRExpr -> IRCallable option) (visiting: Set<IRId>) (e: IRExpr) : Result<Ops, string> =
    let go = exprOps resolve visiting
    let all (xs: IRExpr list) (own: Ops) =
        xs |> List.fold (fun acc x ->
            match acc, go x with
            | Ok a, Ok b -> Ok (opsAdd a b)
            | (Error _ as err), _ -> err
            | _, Error why -> Error why) (Ok own)
    let real (x: IRExpr) =
        match (try Blade.IRTotality.scalarElem (typeOf x) with _ -> None) with
        | Some et -> Blade.IRTotality.isReal et || et = ETBool
        | None -> false
    match e with
    | IRLit _ | IRVar _ | IRParam _ -> Ok opsZero
    | IRBinOp (_, op, a, b, _) ->
        if not (real a && real b) then Error "array-level or complex arithmetic (model v1 prices real scalar arithmetic only)"
        else
        let own =
            match op with
            | IRDiv -> opsOne divisionOps [ "division `/`" ]
            | IRMod -> opsOne divisionOps [ "division `%`" ]
            | IRCaret -> opsOne transcendentalOps [ "transcendental `^`" ]
            | IRMath2 f -> opsOne transcendentalOps [ $"transcendental `{f}`" ]
            | _ -> opsOne 1L []
        all [ a; b ] own
    | IRUnaryOp (op, a) ->
        if not (real a) then Error "a complex or non-scalar operand (model v1 prices real scalar arithmetic only)"
        else
        match op with
        | IRNeg | IRNot | IRCast _ -> all [ a ] (opsOne 1L [])
        | IRMath ("abs" | "floor" | "ceil") -> all [ a ] (opsOne 1L [])
        | IRMath "sqrt" -> all [ a ] (opsOne divisionOps [ "division-class `sqrt`" ])
        | IRMath f -> all [ a ] (opsOne transcendentalOps [ $"transcendental `{f}`" ])
        | _ -> Error "a complex accessor (model v1 prices real scalar arithmetic only)"
    | IRFma (a, b, c) -> all [ a; b; c ] (opsOne 2L [])
    | IRIf (c, a, b) ->
        match go c, go a, go b with
        | Ok oc, Ok oa, Ok ob ->
            let dear = if oa.Count >= ob.Count then oa else ob
            Ok { Count = oc.Count + dear.Count; Heavy = oc.Heavy @ oa.Heavy @ ob.Heavy |> List.distinct }
        | Error why, _, _ | _, Error why, _ | _, _, Error why -> Error why
    | IRLet (_, v, body) -> all [ v; body ] opsZero
    | IRApp (f, args, _) ->
        match resolve f with
        | Some cl when visiting.Contains cl.Id -> Error "a recursive call (model v1 does not price recursion)"
        | Some cl ->
            match exprOps resolve (visiting.Add cl.Id) cl.Body with
            | Ok body -> all args body
            | Error why -> Error why
        | None -> Error "a call that does not resolve to a callable"
    | IRIndex _ -> Error "a keyed read inside the kernel (a gather; model v1 prices streaming reads only)"
    | other -> Error $"a {other.GetType().Name} node the model does not price"

/// Operations of one kernel application (its body, parameters free).
let kernelOps (resolve: IRExpr -> IRCallable option) (c: IRCallable) : Result<Ops, string> =
    exprOps resolve (Set.singleton c.Id) c.Body

/// Operations of one scalar expression evaluated once (a callee's scalar let).
let scalarOps (resolve: IRExpr -> IRCallable option) (e: IRExpr) : Result<Ops, string> =
    exprOps resolve Set.empty e

/// A map evaluated IN PLACE, per output cell: its operations (operand maps
/// read in place included) and the stored cells it reads, by operand.
type PerCell = { CellOps: Ops; Reads: (string * int64) list }

/// `storedName` names an operand that is a STORED array read once per cell
/// (None: not one); a range is computed, a nested `|> compute` map or a
/// deferred map operand is priced in place, recursively; anything else is not
/// priced.
let private mergeReads (a: (string * int64) list) (b: (string * int64) list) =
    b |> List.fold (fun (acc: (string * int64) list) (k, v) ->
        if acc |> List.exists (fun (k2, _) -> k2 = k) then acc |> List.map (fun (k2, v2) -> if k2 = k then (k2, v2 + v) else (k2, v2))
        else acc @ [ (k, v) ]) a

let rec mapPerCell (resolve: IRExpr -> IRCallable option) (storedName: IRExpr -> string option) (p: ApplyInfo) : Result<PerCell, string> =
    match resolve p.Kernel with
    | None -> Error "the map kernel does not resolve to a callable"
    | Some k ->
    match kernelOps resolve k with
    | Error why -> Error $"the kernel: {why}"
    | Ok kops ->
    p.Arrays |> List.fold (fun (acc: Result<PerCell, string>) (a: IRExpr) ->
        match acc with
        | Error _ -> acc
        | Ok pc ->
            match storedName a with
            | Some nm -> Ok { pc with Reads = mergeReads pc.Reads [ (nm, 1L) ] }
            | None ->
            match a with
            | IRRange _ -> Ok pc
            | IRCompute (IRApplyCombinator q) | IRApplyCombinator q ->
                (match mapPerCell resolve storedName q with
                 | Ok inner -> Ok { CellOps = opsAdd pc.CellOps inner.CellOps; Reads = mergeReads pc.Reads inner.Reads }
                 | Error why -> Error why)
            | _ -> Error "an operand form the model does not price (a call, a view or a deferred name)") (Ok { CellOps = kops; Reads = [] })

/// Total stored cells read per output cell.
let readsPerCell (pc: PerCell) : int64 = pc.Reads |> List.sumBy snd

// ---------------------------------------------------------------------------
// Steps of the realization forms (`live`: cells of earlier pools still live).
// ---------------------------------------------------------------------------

/// A map MATERIALIZED into `out` (n cells): kernel work per cell, its reads,
/// n writes of `out`; `out` live on top of `live`.
let materializedMap (out: string) (n: Poly) (pc: PerCell) (live: Poly) : Step =
    { SWork = Poly.scale pc.CellOps.Count n
      STraffic = (pc.Reads |> List.map (fun (nm, k) -> (nm, Poly.scale k n))) @ [ (out, n) ]
      SPeak = Peak.ofPoly (Poly.add live n) }

/// A fold over a STORED operand of n cells: one combine and one read per cell.
let storedFold (operand: string) (n: Poly) (foldOps: Ops) (live: Poly) : Step =
    { SWork = Poly.scale foldOps.Count n; STraffic = [ (operand, n) ]; SPeak = Peak.ofPoly live }

/// A fold reading a map IN PLACE (the fused terminal): the map's work and the
/// combine per cell, the map's operand reads, no pool.
let inPlaceFold (n: Poly) (pc: PerCell) (foldOps: Ops) (live: Poly) : Step =
    { SWork = Poly.scale (pc.CellOps.Count + foldOps.Count) n
      STraffic = pc.Reads |> List.map (fun (nm, k) -> (nm, Poly.scale k n))
      SPeak = Peak.ofPoly live }

/// A whole-array COPY of n cells from `src` into `dst` (a transpose copy, a
/// copy-on-bind): no work, n reads and n writes, `dst` live on top of `live`.
let copyStep (src: string) (dst: string) (n: Poly) (live: Poly) : Step =
    { SWork = Poly.zero; STraffic = [ (src, n); (dst, n) ]; SPeak = Peak.ofPoly (Poly.add live n) }

/// Constant work evaluated once (a callee's scalar lets).
let scalarStep (ops: Ops) : Step = { emptyStep with SWork = Poly.konst ops.Count }

// ---------------------------------------------------------------------------
// The record line (P5: every realization record states its cost basis).
// ---------------------------------------------------------------------------

let private trafficBy (before: Step) (after: Step) : string =
    let names = (before.STraffic @ after.STraffic) |> List.map fst |> List.distinct
    let find (s: Step) nm = s.STraffic |> List.tryFind (fun (k, _) -> k = nm) |> Option.map snd |> Option.defaultValue Poly.zero
    names |> List.map (fun nm -> $"{nm} {Poly.render (find before nm)} -> {Poly.render (find after nm)}") |> String.concat ", "

/// `cost: work <b> -> <a>, traffic <b> -> <a>, peak <b> -> <a> (model v1:
/// <basis>; traffic by operand: ...)`: the BEFORE side is the materialized
/// baseline the decision replaced, the AFTER side the chosen form.
let renderTransition (before: Step) (after: Step) (basis: string) : string =
    let b, a = costOf before, costOf after
    $"cost: work {Poly.render b.Work} -> {Poly.render a.Work}, traffic {Poly.render b.Traffic} -> {Poly.render a.Traffic}, peak {Peak.render b.Peak} -> {Peak.render a.Peak} (model v{version}: {basis}; traffic by operand: {trafficBy before after})"

/// The line of a form the model cannot price.
let notPriced (why: string) : string = $"cost: not priced ({why})"

/// The uniform legality line.
let legalityLine (rule: string) : string = $"legality: {rule}"

/// `n` cells, spelled for a basis.
let cellsText (n: Poly) : string =
    match Poly.tryConst n with
    | Some 1L -> "1 cell"
    | Some c -> $"{c} cells"
    | None -> $"{Poly.render n} cells"

/// Ops per cell, spelled for a basis.
let opsText (o: Ops) : string =
    let heavy = if o.Heavy.IsEmpty then "" else " incl. " + String.concat ", " o.Heavy
    $"""{o.Count} op{(if o.Count = 1L then "" else "s")}/cell{heavy}"""

// ---------------------------------------------------------------------------
// The realization records' cost lines (P5 step 2): each returns the record's
// `cost: ...` evidence item, priced by the model above, or `cost: not priced
// (<why>)`. The BEFORE side is always the materialized baseline the rule
// replaces; nothing here decides anything.
// ---------------------------------------------------------------------------

let private bindR (r: Result<'a, string>) (f: 'a -> Result<'b, string>) : Result<'b, string> =
    match r with
    | Ok x -> f x
    | Error e -> Error e

let private guarded (f: unit -> Result<'a, string>) : Result<'a, string> =
    try f () with :? System.OverflowException -> Error "a count overflows a 64-bit integer"

let private lineOf (r: unit -> Result<string, string>) : string =
    match guarded r with
    | Ok s -> s
    | Error why -> notPriced why

/// A STORED array operand's name for a cost line: a variable of array type,
/// spelled by `nameOf`; None for anything else (a range, an in-place map).
let storedArrayName (nameOf: IRId -> string) (e: IRExpr) : string option =
    match e with
    | IRVar (id, ty) -> (match stripUnits ty with ArrayElem _ -> Some (nameOf id) | _ -> None)
    | _ -> None

/// Scalar lets evaluated once in BOTH forms (a call elision's inlined spine).
let private letsStep (resolve: IRExpr -> IRCallable option) (lets: IRExpr list) : Result<Step, string> =
    lets |> List.fold (fun acc v ->
        bindR acc (fun (s: Step) ->
            match scalarOps resolve v with
            | Ok o -> Ok (seqStep s (scalarStep o))
            | Error why -> Error $"a scalar let: {why}")) (Ok emptyStep)

let private foldOpsOf (resolve: IRExpr -> IRCallable option) (fold: IRCallable option) : Result<Ops, string> =
    match fold with
    | Some f -> kernelOps resolve f |> Result.mapError (fun w -> $"the fold kernel: {w}")
    | None -> Error "the fold kernel does not resolve to a callable"

/// FOLD ELISION (compute-elision's and call-elision's fold consumer): the
/// baseline writes the map into `pool` and folds the stored pool; the elided
/// form folds the map in place. `lets`: scalar lets evaluated in both.
let foldElisionLine (resolve: IRExpr -> IRCallable option) (nameOf: IRId -> string) (storedName: IRExpr -> string option)
                    (pool: string) (lets: IRExpr list) (p: ApplyInfo) (fold: IRCallable option) : string =
    lineOf (fun () ->
        bindR (mapCells nameOf p) (fun n ->
        bindR (mapPerCell resolve storedName p) (fun pc ->
        bindR (foldOpsOf resolve fold) (fun fops ->
        bindR (letsStep resolve lets) (fun ls ->
            let before = seqSteps [ ls; materializedMap pool n pc Poly.zero; storedFold pool n fops n ]
            let after = seqSteps [ ls; inPlaceFold n pc fops Poly.zero ]
            Ok (renderTransition before after
                    $"{cellsText n}; producer {opsText pc.CellOps}, fold {opsText fops}; baseline: `{pool}` written, then folded from storage"))))))

/// FOLD-OF-MAP ELISION (round 4 wave 2, PM12): the baseline writes the
/// producer into `pool`, then the fold reads its operand map `h` over the
/// stored pool in place; the elided form folds `h'` (the producer spliced in)
/// in place. The fold's own work is the same on both sides.
let foldMapElisionLine (resolve: IRExpr -> IRCallable option) (nameOf: IRId -> string)
                       (storedBefore: IRExpr -> string option) (storedAfter: IRExpr -> string option)
                       (pool: string) (p: ApplyInfo) (h: ApplyInfo) (h': ApplyInfo) (fold: IRCallable option) : string =
    lineOf (fun () ->
        bindR (mapCells nameOf p) (fun n ->
        bindR (mapCells nameOf h) (fun nFold ->
        bindR (mapPerCell resolve storedBefore p) (fun pcP ->
        bindR (mapPerCell resolve storedBefore h) (fun pcH ->
        bindR (mapPerCell resolve storedAfter h') (fun pcF ->
        bindR (foldOpsOf resolve fold) (fun fops ->
            let before = seqSteps [ materializedMap pool n pcP Poly.zero; inPlaceFold nFold pcH fops n ]
            let after = seqSteps [ inPlaceFold nFold pcF fops Poly.zero ]
            Ok (renderTransition before after
                    $"{cellsText n}; producer {opsText pcP.CellOps}, fold map {opsText pcH.CellOps}, fold {opsText fops}; baseline: `{pool}` written, then read by the fold's map; fused: the fold reads the producer in place"))))))))

/// MAP ELISION (the pointwise consumer, realized by elementwise fusion): the
/// baseline writes the producer into `pool`, then the consumer `h` reads it
/// into `out`; the fused form `h'` reads the producer in place. `storedBefore`
/// names `h`'s pool operand `pool`.
let mapElisionLine (resolve: IRExpr -> IRCallable option) (nameOf: IRId -> string)
                   (storedBefore: IRExpr -> string option) (storedAfter: IRExpr -> string option)
                   (pool: string) (out: string) (lets: IRExpr list) (p: ApplyInfo) (h: ApplyInfo) (h': ApplyInfo) : string =
    lineOf (fun () ->
        bindR (mapCells nameOf p) (fun n ->
        bindR (mapCells nameOf h) (fun nOut ->
        bindR (mapPerCell resolve storedBefore p) (fun pcP ->
        bindR (mapPerCell resolve storedBefore h) (fun pcH ->
        bindR (mapPerCell resolve storedAfter h') (fun pcF ->
        bindR (letsStep resolve lets) (fun ls ->
            let before = seqSteps [ ls; materializedMap pool n pcP Poly.zero; materializedMap out nOut pcH n ]
            let after = seqSteps [ ls; materializedMap out nOut pcF Poly.zero ]
            Ok (renderTransition before after
                    $"{cellsText n}; producer {opsText pcP.CellOps}, consumer {opsText pcH.CellOps}; baseline: `{pool}` written, then read into `{out}`; fused: one nest into `{out}`"))))))))

/// TRANSPOSE CHAIN: `k` eager swap copies of the base (`n` cells each, all
/// live through the statement in model v1) become `m` (the minimal swaps),
/// plus `bindCopies` copy-on-bind copies of the base an identity chain's
/// assignable binding keeps (the alias plan prices that copy separately).
let transposeChainLine (nameOf: IRId -> string) (baseTy: IRType) (baseName: string) (k: int) (m: int) (bindCopies: int) (binder: string) : string =
    lineOf (fun () ->
        bindR (cellsOf nameOf baseTy) (fun n ->
            let chain (count: int) =
                [ 1 .. count ] |> List.fold (fun (s: Step, src: string, live: Poly) i ->
                    let dst = $"swap copy {i}"
                    (seqStep s (copyStep src dst n live), dst, Poly.add live n)) (emptyStep, baseName, Poly.zero)
            let (beforeS, _, _) = chain k
            let (afterChain, _, liveA) = chain m
            let afterS =
                if bindCopies > 0 then seqStep afterChain (copyStep baseName $"{binder} (copy-on-bind)" n liveA)
                else afterChain
            let bindTxt = if bindCopies > 0 then $"; after: `{binder}`'s copy-on-bind copy of the base (the read-only alias plan decides whether it stays)" else ""
            Ok (renderTransition beforeS afterS
                    $"{cellsText n} per copy; a copy is traffic, not work; {k} swap copies -> {m}{bindTxt}")))

/// READ-ONLY ALIAS: the copy-on-bind copy of `src` into `alias` (its pool's
/// cells) vs sharing `src`'s storage.
let aliasCopyLine (nameOf: IRId -> string) (aliasTy: IRType) (src: string) (alias: string) : string =
    lineOf (fun () ->
        bindR (cellsOf nameOf aliasTy) (fun n ->
            Ok (renderTransition (copyStep src alias n Poly.zero) emptyStep
                    $"{cellsText n} in the pool; baseline: `{alias}`'s copy-on-bind copy of `{src}`; after: `{alias}` reads `{src}`'s storage")))

// ---------------------------------------------------------------------------
// THE RECOMPUTE SELECTION's pricing (BLADE_RECOMPUTE, P5 step 3): a stored
// intermediate `x` of n cells with several consumers, candidate family
// {MATERIALIZE (today), RECOMPUTE per consumer}. Each consumer is a fold over
// x or a pointwise map reading x's cell once (`h` as written, `h'` with x's
// map in place, `out` its result pool).
// ---------------------------------------------------------------------------

type RecomputeConsumer =
    | RcFold of fold: IRCallable option
    | RcMap of h: ApplyInfo * h': ApplyInfo * out: string

type RecomputeVerdict = {
    /// true = recompute per consumer; false = materialize (the baseline).
    Recompute: bool
    /// The `cost: ...` evidence item (both candidates).
    CostLine: string
    /// The selection's stated inequality, evaluated.
    Selection: string
    /// For a materialized verdict: `recompute not dominant: ...`.
    Reason: string
}

/// The exact rule (model v1 prices one scalar operation and one cell of
/// traffic equally): RECOMPUTE iff
///     work(recompute) - work(materialize) <= traffic(materialize) - traffic(recompute)
///  and peak(recompute) <= peak(materialize),
/// both PROVED (coefficient-wise); a tie in both (equal sums, equal peaks)
/// keeps MATERIALIZE, as does anything the model cannot decide.
let recomputeRule =
    "recompute iff work(recompute) - work(materialize) <= traffic(materialize) - traffic(recompute) and peak(recompute) <= peak(materialize), both proved; a tie in both keeps materialize (model v1 prices one scalar operation and one cell of traffic equally)"

let recomputeSelection (resolve: IRExpr -> IRCallable option) (nameOf: IRId -> string) (stored: IRExpr -> string option)
                       (x: string) (p: ApplyInfo) (consumers: RecomputeConsumer list) : Result<RecomputeVerdict, string> =
    guarded (fun () ->
    bindR (mapCells nameOf p) (fun n ->
    bindR (mapPerCell resolve stored p) (fun pc ->
        // a consumer's result pool stays live through the later consumers
        // (model v1 frees nothing inside the priced region: Codex review)
        let priced =
            consumers |> List.fold (fun acc c ->
                bindR acc (fun (ms: Step list, rs: Step list, txt: string list, outs: Poly) ->
                    match c with
                    | RcFold fold ->
                        bindR (foldOpsOf resolve fold) (fun fops ->
                            Ok (ms @ [ storedFold x n fops (Poly.add n outs) ], rs @ [ inPlaceFold n pc fops outs ], txt @ [ $"fold {opsText fops}" ], outs))
                    | RcMap (h, h', out) ->
                        bindR (mapCells nameOf h) (fun nOut ->
                        bindR (mapPerCell resolve stored h) (fun pcH ->
                        bindR (mapPerCell resolve stored h') (fun pcF ->
                            Ok (ms @ [ materializedMap out nOut pcH (Poly.add n outs) ], rs @ [ materializedMap out nOut pcF outs ],
                                txt @ [ $"map into `{out}` {opsText pcH.CellOps}" ], Poly.add outs nOut)))))) (Ok ([ materializedMap x n pc Poly.zero ], [], [], Poly.zero))
        bindR priced (fun (ms, rs, txt, _) ->
            let m, r = seqSteps ms, seqSteps rs
            let cm, cr = costOf m, costOf r
            let d = Poly.sub cr.Work cm.Work
            let t = Poly.sub cm.Traffic cr.Traffic
            let signed (prefix: string) (q: Poly) =
                match Poly.tryConst q with
                | Some v when v < 0L -> (if prefix = "+" then "-" else "+") + string (-v)
                | _ -> prefix + Poly.render q
            let workTxt, trafficTxt = signed "+" d, signed "-" t
            let workOk = Poly.leq d t
            let peakOk = Peak.leq cr.Peak cm.Peak
            let tie = d = t && cr.Peak = cm.Peak
            let recompute = workOk && peakOk && not tie
            let consumersTxt = String.concat ", " txt
            let basis =
                $"{cellsText n}; producer {opsText pc.CellOps}, reading {readsPerCell pc} stored cell(s) per cell; {consumers.Length} consumers ({consumersTxt}); baseline: `{x}` written once, read by each consumer"
            let costLine =
                if recompute then renderTransition m r basis
                else
                    $"cost: materialize (kept) work {Poly.render cm.Work}, traffic {Poly.render cm.Traffic}, peak {Peak.render cm.Peak}; recompute (rejected) work {Poly.render cr.Work}, traffic {Poly.render cr.Traffic}, peak {Peak.render cr.Peak} (model v{version}: {basis})"
            let cmp =
                if workOk then "<="
                else (match Poly.tryConst d, Poly.tryConst t with Some _, Some _ -> ">" | _ -> "not proved <=")
            let chosen = if recompute then "recompute" else "materialize"
            let selection = $"selection: work {workTxt} {cmp} traffic {trafficTxt}, peak {Peak.render cm.Peak} -> {Peak.render cr.Peak}: {chosen} ({recomputeRule})"
            let reason =
                if not workOk then $"recompute not dominant: work {workTxt} {cmp} traffic {trafficTxt}"
                elif not peakOk then $"recompute not dominant: peak {Peak.render cr.Peak} not proved <= {Peak.render cm.Peak}"
                elif tie then $"recompute not dominant: work {workTxt} = traffic {trafficTxt} and equal peaks (a tie keeps materialize)"
                else ""
            Ok { Recompute = recompute; CostLine = costLine; Selection = selection; Reason = reason }))))
