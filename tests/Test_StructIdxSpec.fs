// Pins for the constrained-record COUNTING layer (src/StructIdxSpec.fs) —
// stage C1 of docs/plan-constrained-index-types.md §7. No types, no lowering,
// no emission: everything here is a solution-set enumeration or a count, and
// every number is an integer.
//
// Five families of pin.
//
//  1. CLOSED FORMS. An unconstrained record's card IS its box volume, for a
//     sweep of boxes including negative-offset and asymmetric ones. This is
//     the pin that catches a shift bug in the "no filtering happens" case,
//     where the certificate's two routes would agree with each other while
//     both being wrong about the box.
//  2. THE CGm112 ANCHOR (§5). l1 = l2 = 1, m1 + m2 == m_out: box 27, card 7,
//     and the lo-sweep 3/7/9 (lo = 0, 1, 2; 9 is the (2l1+1)(2l2+1)
//     saturation) — each one cross-checked against the SUM-CHECK, i.e. the
//     dense pair count Sigma_{m_out} #{(m1,m2) : m1+m2 = m_out} computed here
//     by a plain triple loop that shares nothing with the module. The lex
//     VALUE order of the 7 solutions is pinned entry by entry.
//  3. EMPTINESS. A parity conjunct with no solutions gives card 0, folds
//     SUCCESSFULLY, and emits no warning. Card 0 is C1-legal: the
//     derived-empty WARNING is C2's, where a `range<R>` no-op loop is
//     actually reachable.
//  4. THE FENCE AND `idx_card`, end to end through a hand-built declaration
//     list and StaticEval.resolveStatics — the same route the compiler takes,
//     minus the parser. Includes the statics-in-bounds case (§5's l1/l2/lo are
//     `let static` names, not literals) and the static-function-in-a-conjunct
//     case.
//  5. NEGATIVE CONTROLS, each of which must FAIL and must SAY WHY: the box
//     cap, a non-Int field, an unbounded field, a non-`static struct`, a
//     non-identifier argument to idx_card, and a conjunct that does not fold,
//     whose diagnostic must name the WITNESS CELL (§6 risk 3: the fold is
//     per-cell, so a cell-free message is unactionable).
//  6. THE FOLD BUDGET (block (i)), which is `StaticEval`'s and is pinned here
//     because this file is where its defect was recorded and the counting
//     layer is its second consumer. Family 5 used to say the plan's FUEL BOMB
//     could not be pinned at all: the budget was threaded as `fuel - 1` into
//     every CHILD, so it bounded DEPTH while being named and reported as a
//     step count, and a bomb overflowed the stack before the counter could
//     diagnose it — taking any test that tried with it. Now pinned in all
//     four shapes: the bare recursion, the bomb in a conjunct with its
//     witness cell, the WIDE-but-shallow fold that only a step bound catches
//     (with a fits-the-budget control beside it, so the pin bounds work
//     rather than banning branching), and the `idx_card` cycle that no depth
//     guard can see because a syntactic builtin necessarily restarts the fold.
//
// WHAT IS NOT HERE. The independent third-route enumerator lives in
// tests/Test_StructIdxOracle.fs and shares no code with either this file or
// the module — the 5a-i discipline. This file's own cross-check is the one
// StructIdxSpec runs internally on every call (flat filter vs arrow heads, set
// AND order), which every single `enumerateBox` below therefore exercises for
// free; a failure of it raises rather than returning, so the "certificate
// survives" pins below are testing that too.
module Blade.Tests.StructIdxSpecReview

open Blade.Ast
open Blade.StaticEval
open Blade.StructIdxFence
open Blade.StructIdxSpec
open Blade.Tests.TestHarness

// ---------------------------------------------------------------------------
// Small helpers (this file's own, deliberately trivial)
// ---------------------------------------------------------------------------

let private box3 (specs: (string * int64 * int64) list) : BoxField list =
    specs |> List.map (fun (n, lo, hi) -> { Field = n; Lo = lo; Hi = hi })

/// The always-true predicate: an UNCONSTRAINED record.
let private always : CellPredicate = fun _ -> Ok true

/// Look a field up by name in a cell. Name-keyed on purpose: a positional
/// destructure would silently pass if the module ever reordered the cell.
let private get (cell: (string * int64) list) (n: string) : int64 =
    cell |> List.find (fun (f, _) -> f = n) |> snd

let private okOr (d: 'a) (r: Result<'a, string>) = match r with Ok v -> v | Error _ -> d
let private isErr (r: Result<'a, string>) = match r with Error _ -> true | Ok _ -> false
let private msgOf (r: Result<'a, string>) = match r with Error m -> m | Ok _ -> ""

// ---------------------------------------------------------------------------
// The CGm112 family, expressed twice: as a predicate for the module, and as a
// TRIPLE LOOP for the sum-check. The triple loop is the closed-form oracle of
// §5 ("an F# triple-loop count sharing no code").
// ---------------------------------------------------------------------------

/// m1 + m2 == m_out.
let private cgPred : CellPredicate =
    fun cell -> Ok (get cell "m1" + get cell "m2" = get cell "m_out")

/// The dense pair count: for each admissible m_out, how many (m1, m2) pairs
/// sum to it. Nothing here knows about boxes, lex order or enumeration.
let private densePairCount (l1: int) (l2: int) (lo: int) : int =
    let mutable n = 0
    for m1 in -l1 .. l1 do
        for m2 in -l2 .. l2 do
            if m1 + m2 >= -lo && m1 + m2 <= lo then n <- n + 1
    n

let private cgBox (l1: int64) (l2: int64) (lo: int64) : BoxField list =
    box3 [ "m1", -l1, l1; "m2", -l2, l2; "m_out", -lo, lo ]

// ---------------------------------------------------------------------------
// AST construction for the fence / idx_card pins — the compiler's own route
// (resolveStatics over a decl list), minus the parser.
// ---------------------------------------------------------------------------

let private e (k: ExprKind) : Expr = mkExpr noSpan k
let private lit (n: int64) = e (ExprLit (LitInt n))
let private v (n: string) = e (ExprVar n)
let private bin op a b = e (ExprBinOp (Elementwise, op, a, b))

/// `f: Int in lo .. hi` — the HALF-OPEN spelling, so the inclusive box top is
/// `hi - 1`. Exercising the half-open spelling here is deliberate: it is the
/// normalization step where an off-by-one would hide.
let private fieldHalfOpen (name: string) (lo: Expr) (hi: Expr) : FieldDecl =
    { Name = name; Type = TyNamed ("Int", []); Default = None
      Bound = Some { Lo = Some lo; Hi = Some hi; HiInclusive = false } }

/// `f: Int<min=lo, max=hi>` — the INCLUSIVE spelling, normalized by the parser
/// into the same Bound channel with HiInclusive = true.
let private fieldInclusive (name: string) (lo: Expr) (hi: Expr) : FieldDecl =
    { Name = name; Type = TyNamed ("Int", []); Default = None
      Bound = Some { Lo = Some lo; Hi = Some hi; HiInclusive = true } }

let private structDecl (name: string) (isStatic: bool) (fields: FieldDecl list) (conjuncts: Expr list) =
    at noSpan (DeclType (TyDeclStruct (name, [], fields, conjuncts, isStatic)))

let private staticLet (name: string) (value: Expr) =
    at noSpan (DeclStatic { Mutability = BindLet; Pattern = mkPat noSpan (PatVar name); Type = None; Value = value })

let private staticFn (name: string) (ps: string list) (body: Expr) =
    at noSpan (DeclFunction {
        Name = name; TypeParams = []
        Params = ps |> List.map (fun p -> { Name = p; Type = None; Mutability = Immutable })
        WhereClause = None; ReturnType = None; Body = body; IsStatic = true })

/// Run the real static resolver over a decl list and hand back the env — the
/// environment `idx_card` sees at compile time.
let private envOf (decls: Located<Decl> list) : StaticEnv =
    match resolveStatics decls with
    | Ok (env, _) -> env
    | Error e -> failwithf "test setup: resolveStatics failed: %s" e

/// Fold `idx_card(NAME)` exactly as a `let static` would, through the
/// syntactic-builtin path registered by StructIdxSpec.install ().
let private foldIdxCard (env: StaticEnv) (arg: Expr) : Result<StaticValue, string> =
    evalExpr env maxSteps (e (ExprApp (v "idx_card", [ arg ])))

// ---------------------------------------------------------------------------

let runStructIdxSpecTests () : BlockResult =
    printHeader "Struct Idx (constrained-record counting, C1)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail

    // Every enumerateBox call below runs the internal certificate (flat filter
    // vs arrow heads, set AND ORDER, plus card = |entries|) and RAISES on
    // disagreement. Wrapping the whole block would hide which pin tripped, so
    // instead each pin calls through and a raise fails the run loudly.
    install ()

    // ---- 1. closed forms: unconstrained card = box volume ------------------
    let volumeCases =
        [ [ "a", 0L, 2L ]                                   // 3
          [ "a", -1L, 1L ]                                  // 3, negative offset
          [ "a", -1L, 1L; "b", -1L, 1L; "c", -1L, 1L ]      // 27
          [ "a", 5L, 5L ]                                   // 1, degenerate
          [ "a", 0L, 3L; "b", -2L, 4L ]                     // 4 * 7 = 28
          [ "a", -3L, -1L; "b", 10L, 11L ] ]                // 3 * 2 = 6, all-negative
    let volumeOk =
        volumeCases |> List.forall (fun spec ->
            let fs = box3 spec
            match enumerateBox "V" fs always with
            | Ok r -> int64 r.Card = boxVolume fs && List.length r.Entries = r.Card
            | Error _ -> false)
    check "unconstrained card = box volume (6 boxes, incl. negative and asymmetric)"
          volumeOk
          (volumeCases |> List.map (box3 >> boxVolume >> string) |> String.concat " ")

    // The volume identity is only meaningful if the ENTRIES are the box: pin
    // the full lex value list of a 2x2 negative-offset box explicitly.
    let smallBox = okOr Unchecked.defaultof<_> (enumerateBox "V2" (box3 [ "a", -1L, 0L; "b", -1L, 0L ]) always)
    check "the unconstrained entries ARE the box, in lex order of VALUES (first field outermost)"
          (smallBox.Entries = [ [ -1L; -1L ]; [ -1L; 0L ]; [ 0L; -1L ]; [ 0L; 0L ] ])
          (smallBox.Entries |> List.map (fun r -> r |> List.map string |> String.concat ",") |> String.concat " | ")

    // A zero-extent field empties the whole box; a zero-FIELD record is the
    // one-cell box (the empty tuple), which is the n = 0 base case both
    // enumeration routes have to agree about.
    check "an empty field range empties the box (card 0), and a 0-field record is the ONE-cell box"
          ((okOr Unchecked.defaultof<_> (enumerateBox "E" (box3 [ "a", 0L, 2L; "b", 3L, 2L ]) always)).Card = 0
           && (okOr Unchecked.defaultof<_> (enumerateBox "U" [] always)).Card = 1
           && (okOr Unchecked.defaultof<_> (enumerateBox "U" [] (fun _ -> Ok false))).Card = 0)
          ""

    // ---- 2. the CGm112 anchor and the lo-sweep -----------------------------
    let cg lo = enumerateBox "CGm112" (cgBox 1L 1L lo) cgPred
    let anchor = okOr Unchecked.defaultof<_> (cg 1L)
    check "CGm112 (l1 = l2 = 1, m_out in -1..1): box 27, card 7 — the plan's §5 anchor"
          (boxVolume (cgBox 1L 1L 1L) = 27L && anchor.Card = 7)
          (sprintf "box %d, card %d" (boxVolume (cgBox 1L 1L 1L)) anchor.Card)

    check "the lo-sweep is 3 / 7 / 9 (lo = 0, 1, 2), 9 being the (2l1+1)(2l2+1) saturation"
          ([ 0L; 1L; 2L ] |> List.map (fun lo -> (okOr Unchecked.defaultof<_> (cg lo)).Card) = [ 3; 7; 9 ])
          ([ 0L; 1L; 2L ] |> List.map (fun lo -> string (okOr Unchecked.defaultof<_> (cg lo)).Card) |> String.concat "/")

    // THE SUM-CHECK: the same three numbers from a triple loop that has never
    // heard of a box, a shift or an enumeration order.
    check "SUM-CHECK: every lo in 0..3 agrees with the dense pair count (independent triple loop)"
          ([ 0L; 1L; 2L; 3L ] |> List.forall (fun lo ->
                (okOr Unchecked.defaultof<_> (cg lo)).Card = densePairCount 1 1 (int lo)))
          (sprintf "dense = %d/%d/%d/%d"
               (densePairCount 1 1 0) (densePairCount 1 1 1) (densePairCount 1 1 2) (densePairCount 1 1 3))

    // Saturation, stated as an identity rather than a number: past lo = l1+l2
    // the constraint stops cutting and card is the (2l1+1)(2l2+1) pair count.
    check "saturation: for lo >= l1 + l2, card = (2l1+1)(2l2+1) regardless of lo"
          ([ (1L, 1L); (1L, 2L); (2L, 2L); (0L, 3L) ] |> List.forall (fun (l1, l2) ->
                let sat = (2L * l1 + 1L) * (2L * l2 + 1L)
                [ l1 + l2; l1 + l2 + 1L; l1 + l2 + 2L ] |> List.forall (fun lo ->
                    (okOr Unchecked.defaultof<_> (enumerateBox "CG" (cgBox l1 l2 lo) cgPred)).Card = int sat)))
          ""

    // THE VALUE PINS: the 7 solutions, in lex order, as VALUES. This is the
    // pin that a wrong offset vector breaks and a wrong count does not.
    let want7 =
        [ [ -1L; 0L; -1L ]; [ -1L;  1L; 0L ]
          [  0L; -1L; -1L ]; [  0L;  0L; 0L ]; [ 0L; 1L; 1L ]
          [  1L; -1L;  0L ]; [  1L;  0L; 1L ] ]
    check "the 7 CGm112 solutions are pinned by VALUE, in lex order, first field outermost"
          (anchor.Entries = want7)
          (anchor.Entries |> List.map (fun r -> r |> List.map string |> String.concat "") |> String.concat " ")

    // Independent structural properties of the entry list: in the box, sorted,
    // distinct, and every one actually satisfying the constraint.
    check "every entry lies in the box, satisfies m1 + m2 = m_out, is distinct, and the list is strictly ascending"
          (let es = anchor.Entries
           es |> List.forall (fun r -> match r with [a; b; c] -> a + b = c && abs a <= 1L && abs b <= 1L && abs c <= 1L | _ -> false)
           && (Set.ofList es).Count = es.Length
           && es = List.sort es)
          ""

    // ---- 3. emptiness is legal at C1 --------------------------------------
    // A parity conjunct with no solution over its box: 2*m1 = 2*m2 + 1 is
    // never satisfiable in the integers.
    let empty = enumerateBox "Empty" (box3 [ "m1", -2L, 2L; "m2", -2L, 2L ])
                    (fun cell -> Ok (2L * get cell "m1" = 2L * get cell "m2" + 1L))
    check "an unsatisfiable conjunct gives card 0 and SUCCEEDS (the emptiness warning is C2's, not C1's)"
          (match empty with Ok r -> r.Card = 0 && r.Entries.IsEmpty | Error _ -> false) ""

    // ---- 4. the fence and idx_card, end to end -----------------------------
    // The §5 declaration, written with `let static` bounds exactly as the plan
    // writes it — l1/l2/lo are static NAMES, not literals.
    let cgDecls (lo: int64) =
        [ staticLet "l1" (lit 1L)
          staticLet "l2" (lit 1L)
          staticLet "lo" (lit lo)
          structDecl "CGm112" true
              [ fieldHalfOpen "m1"    (e (ExprUnaryOp (OpNeg, v "l1"))) (bin OpAdd (v "l1") (lit 1L))
                fieldHalfOpen "m2"    (e (ExprUnaryOp (OpNeg, v "l2"))) (bin OpAdd (v "l2") (lit 1L))
                fieldHalfOpen "m_out" (e (ExprUnaryOp (OpNeg, v "lo"))) (bin OpAdd (v "lo") (lit 1L)) ]
              [ bin OpEq (bin OpAdd (v "m1") (v "m2")) (v "m_out") ] ]
    let cgEnv lo = envOf (cgDecls lo)

    check "the FENCE normalizes the half-open surface bounds to the INCLUSIVE box (-1..1, not -1..2)"
          (match structStaticFence (cgEnv 1L) "CGm112" with
           | Ok s -> s.Fields = [ { Field = "m1"; Lo = -1L; Hi = 1L }
                                  { Field = "m2"; Lo = -1L; Hi = 1L }
                                  { Field = "m_out"; Lo = -1L; Hi = 1L } ]
           | Error _ -> false)
          (match structStaticFence (cgEnv 1L) "CGm112" with
           | Ok s -> s.Fields |> List.map (fun f -> sprintf "%s:%d..%d" f.Field f.Lo f.Hi) |> String.concat " "
           | Error m -> m)

    check "the fence hands out the DECLARED conjuncts only (the box already enforces the bounds)"
          (match structStaticFence (cgEnv 1L) "CGm112" with Ok s -> s.Conjuncts.Length = 1 | Error _ -> false) ""

    check "idx_card(CGm112) folds to 7 through the real static evaluator, statics-in-bounds and all"
          (foldIdxCard (cgEnv 1L) (v "CGm112") = Ok (SVInt 7L))
          (sprintf "%A" (foldIdxCard (cgEnv 1L) (v "CGm112")))

    check "idx_card reproduces the whole 3/7/9 sweep through the fence, matching the bare-box route"
          ([ 0L; 1L; 2L ] |> List.forall (fun lo ->
                foldIdxCard (cgEnv lo) (v "CGm112") = Ok (SVInt (int64 (densePairCount 1 1 (int lo))))))
          ""

    check "the struct route's ENTRIES equal the bare-box route's, as values and in order"
          ((okOr Unchecked.defaultof<_> (structEntries (cgEnv 1L) "CGm112")).Entries = want7) ""

    // The INCLUSIVE surface spelling must land on the same box as the
    // half-open one — the translation law `in lo .. hi` = `min=lo, max=hi-1`.
    let inclDecls =
        [ structDecl "CGi" true
              [ fieldInclusive "m1"    (lit -1L) (lit 1L)
                fieldInclusive "m2"    (lit -1L) (lit 1L)
                fieldInclusive "m_out" (lit -1L) (lit 1L) ]
              [ bin OpEq (bin OpAdd (v "m1") (v "m2")) (v "m_out") ] ]
    check "the INCLUSIVE spelling `Int<min=-1, max=1>` lands on the same box and the same 7 solutions"
          ((okOr Unchecked.defaultof<_> (structEntries (envOf inclDecls) "CGi")).Entries = want7) ""

    // A static FUNCTION in a conjunct (§3's "static-function calls").
    let fnDecls =
        [ staticFn "is_even" [ "n" ] (bin OpEq (bin OpMod (v "n") (lit 2L)) (lit 0L))
          structDecl "Evens" true
              [ fieldHalfOpen "a" (lit 0L) (lit 6L) ]
              [ e (ExprApp (v "is_even", [ v "a" ])) ] ]
    check "a static FUNCTION call in a conjunct folds per cell (a in 0..5, even: 3 solutions)"
          (foldIdxCard (envOf fnDecls) (v "Evens") = Ok (SVInt 3L))
          (sprintf "%A" (foldIdxCard (envOf fnDecls) (v "Evens")))

    // An unconstrained STATIC struct: card = box volume, through the fence.
    let unconDecls =
        [ structDecl "Plain" true
              [ fieldHalfOpen "a" (lit -2L) (lit 3L); fieldHalfOpen "b" (lit 0L) (lit 4L) ] [] ]
    check "an unconstrained static struct folds to its box volume (5 * 4 = 20)"
          (foldIdxCard (envOf unconDecls) (v "Plain") = Ok (SVInt 20L)) ""

    // Emptiness through the fence, restated: no error, no warning, just 0.
    let emptyDecls =
        [ structDecl "Nope" true
              [ fieldHalfOpen "a" (lit 0L) (lit 4L); fieldHalfOpen "b" (lit 0L) (lit 4L) ]
              [ bin OpEq (bin OpMul (lit 2L) (v "a")) (bin OpAdd (bin OpMul (lit 2L) (v "b")) (lit 1L)) ] ]
    check "idx_card of an unsatisfiable static struct folds to 0 (no error, no warning)"
          (foldIdxCard (envOf emptyDecls) (v "Nope") = Ok (SVInt 0L)) ""

    // THE DEPENDENCY EDGE. `let static C = idx_card(R)` names no static at
    // all, yet folding it evaluates R's bounds — which may name statics. If
    // the dependency graph misses that edge the topological sort is free to
    // fold the call FIRST and the bound fails with "undefined variable"
    // against a perfectly good program. Declared here in the WORST order
    // (the call before the static it transitively needs) so a missing edge
    // cannot be masked by source order.
    // THE NAMES ARE THE TEST. `resolveStatics` never reads source order — it
    // topologically sorts, and `topoSort` drains a Map, so with the edge
    // MISSING both bindings are ready at once and fold in ORDINAL KEY order.
    // Declaring the static last is therefore necessary and nowhere near
    // sufficient: name the consumer `LO` and the static `CARD` and the static
    // wins the key race, the fold succeeds for the wrong reason, and the pin
    // is vacuous. So the consumer is `a_card` and the static `zz_lim`, chosen
    // so the CONSUMER sorts first under any comparer and only a real
    // dependency edge can reorder them. Do not rename these to something
    // descriptive.
    let depDecls =
        [ at noSpan (DeclStatic { Mutability = BindLet; Pattern = mkPat noSpan (PatVar "a_card")
                                  Type = None; Value = e (ExprApp (v "idx_card", [ v "CGdep" ])) })
          staticLet "zz_lim" (lit 1L)
          structDecl "CGdep" true
              [ fieldInclusive "m1"    (e (ExprUnaryOp (OpNeg, v "zz_lim"))) (v "zz_lim")
                fieldInclusive "m2"    (e (ExprUnaryOp (OpNeg, v "zz_lim"))) (v "zz_lim")
                fieldInclusive "m_out" (e (ExprUnaryOp (OpNeg, v "zz_lim"))) (v "zz_lim") ]
              [ bin OpEq (bin OpAdd (v "m1") (v "m2")) (v "m_out") ] ]
    let depRes = resolveStatics depDecls
    check "idx_card(R) DEPENDS on the statics R's bounds name — ADVERSARIAL key order, so only a real edge can pass it"
          (match depRes with
           | Ok (env, failures) -> failures.IsEmpty && Map.tryFind "a_card" env.Values = Some (SVInt 7L)
           | Error _ -> false)
          (match depRes with
           | Ok (_, fs) when not fs.IsEmpty -> fs |> List.map (fun f -> f.Reason) |> String.concat "; "
           | Ok (env, _) -> sprintf "%A" (Map.tryFind "a_card" env.Values)
           | Error m -> m)

    // ---- 5. negative controls ---------------------------------------------
    // (a) the BOX CAP — refused before any enumeration.
    let bigBox = enumerateBox "Big" (box3 [ "a", 0L, 999L; "b", 0L, 999L ]) always
    check "NEGATIVE: a box over the 100k-cell cap is REFUSED"
          (isErr bigBox) ""
    check "the box-cap diagnostic gives the cell count, the cap, and the per-field extents"
          (let m = msgOf bigBox
           m.Contains "1000000" && m.Contains "100000" && m.Contains "cap" && m.Contains "a: 0 .. 999")
          (msgOf bigBox)
    check "the cap is on the BOX, not the solution count: 100000 cells passes, 100001 does not"
          ((match enumerateBox "At" (box3 [ "a", 1L, 100000L ]) always with Ok r -> r.Card = 100000 | Error _ -> false)
           && isErr (enumerateBox "Over" (box3 [ "a", 0L, 100000L ]) always))
          ""

    // (b) a NON-INT field.
    let badTypeDecls =
        [ structDecl "BadTy" true
              [ { Name = "x"; Type = TyFloat64; Default = None
                  Bound = Some { Lo = Some (lit 0L); Hi = Some (lit 3L); HiInclusive = false } } ] [] ]
    let badTy = foldIdxCard (envOf badTypeDecls) (v "BadTy")
    check "NEGATIVE: a non-Int field is REFUSED, naming the field and the reason"
          (isErr badTy && (msgOf badTy).Contains "struct BadTy, field 'x'"
           && (msgOf badTy).Contains "non-enumerable field type" && (msgOf badTy).Contains "Float64")
          (msgOf badTy)

    // (c) an UNBOUNDED field.
    let unboundedDecls =
        [ structDecl "Unb" true
              [ { Name = "y"; Type = TyNamed ("Int", []); Default = None; Bound = None } ] [] ]
    let unb = foldIdxCard (envOf unboundedDecls) (v "Unb")
    check "NEGATIVE: an unbounded field is REFUSED, naming the struct, the field and the requirement"
          (isErr unb && (msgOf unb).Contains "struct Unb, field 'y'" && (msgOf unb).Contains "unbounded field"
           && (msgOf unb).Contains "static min and max")
          (msgOf unb)

    // A half-bounded field is unbounded too — the missing END must not be
    // silently defaulted to anything.
    let halfDecls =
        [ structDecl "Half" true
              [ { Name = "z"; Type = TyNamed ("Int", []); Default = None
                  Bound = Some { Lo = Some (lit 0L); Hi = None; HiInclusive = false } } ] [] ]
    check "NEGATIVE: a HALF-bounded field is unbounded too (no end is ever defaulted)"
          (isErr (foldIdxCard (envOf halfDecls) (v "Half"))) ""

    // (d) a NON-STATIC struct — the declared eligibility fence.
    let nonStaticDecls =
        [ structDecl "Plain2" false [ fieldHalfOpen "a" (lit 0L) (lit 3L) ] [] ]
    let nonStatic = foldIdxCard (envOf nonStaticDecls) (v "Plain2")
    check "NEGATIVE: a struct not declared `static struct` is REFUSED as an index type"
          (isErr nonStatic && (msgOf nonStatic).Contains "static struct") (msgOf nonStatic)

    // (e) a NON-STATIC BOUND expression.
    let dynBoundDecls =
        [ structDecl "Dyn" true
              [ { Name = "a"; Type = TyNamed ("Int", []); Default = None
                  Bound = Some { Lo = Some (lit 0L); Hi = Some (v "nope"); HiInclusive = false } } ] [] ]
    let dynBound = foldIdxCard (envOf dynBoundDecls) (v "Dyn")
    check "NEGATIVE: a non-static bound expression is REFUSED, naming which bound of which field"
          (isErr dynBound && (msgOf dynBound).Contains "struct Dyn, field 'a'"
           && (msgOf dynBound).Contains "max bound is not static")
          (msgOf dynBound)

    // (f) A CONJUNCT THAT DOES NOT FOLD. The diagnostic must name the
    // WITNESS CELL: the fence evaluates one cell at a time and cannot know
    // which of the box's cells it is on, and "a conjunct did not fold" with
    // no coordinates leaves the user nothing to look at.
    //
    // NOT A FUEL BOMB, deliberately, and this is a FINDING rather than a
    // convenience. `StaticEval.maxSteps` is threaded as `fuel - 1` into every
    // CHILD of a node (StaticEval.fs:287-438), so it bounds evaluation DEPTH,
    // not step count — and 100_000 frames of `evalExpr` overflow the .NET
    // stack long before the counter reaches zero. A static function that
    // recurses without a static bound therefore kills the compiler process
    // with an uncatchable StackOverflowException instead of producing the
    // §6-risk-3 diagnostic, in a conjunct or anywhere else `let static` folds.
    // That is a pre-existing StaticEval property, not something the counting
    // layer introduces; pinning a live fuel bomb here would pin a crash. The
    // WITNESS-CELL half of the requirement — the part C1 actually owns — is
    // pinned below on a non-folding conjunct that terminates.
    let unfoldableDecls =
        [ structDecl "Boom" true
              [ fieldHalfOpen "p" (lit 0L) (lit 3L); fieldHalfOpen "q" (lit 0L) (lit 3L) ]
              [ bin OpEq (v "not_a_static") (v "p") ] ]
    let bomb = foldIdxCard (envOf unfoldableDecls) (v "Boom")
    check "NEGATIVE: a conjunct that does not fold FAILS rather than silently excluding the cell"
          (isErr bomb) ""
    check "the non-folding diagnostic carries the WITNESS CELL (p = 0, q = 0) and names the conjunct"
          (let m = msgOf bomb
           m.Contains "(p = 0, q = 0)" && m.Contains "Boom" && m.Contains "conjunct 1")
          (msgOf bomb)

    // The witness cell must be the cell the failure was REACHED at, not the
    // box origin: a conjunct that folds for small p and fails past it must
    // report the first FAILING cell in lex order.
    let lateFailDecls =
        [ staticFn "guard" [ "n" ] (e (ExprIf (bin OpLt (v "n") (lit 2L), e (ExprLit (LitBool true)), v "undefined_past_two")))
          structDecl "Late" true
              [ fieldHalfOpen "p" (lit 0L) (lit 4L); fieldHalfOpen "q" (lit 0L) (lit 2L) ]
              [ e (ExprApp (v "guard", [ v "p" ])) ] ]
    let lateFail = foldIdxCard (envOf lateFailDecls) (v "Late")
    check "the witness cell is the FIRST FAILING cell in lex order, not the box origin (p = 2, q = 0)"
          (isErr lateFail && (msgOf lateFail).Contains "(p = 2, q = 0)")
          (msgOf lateFail)

    // (g) a conjunct that folds to a NON-BOOLEAN is an error, not an
    // exclusion — otherwise a typo reads as a tight constraint.
    let nonBoolDecls =
        [ structDecl "NB" true [ fieldHalfOpen "a" (lit 0L) (lit 3L) ] [ bin OpAdd (v "a") (lit 1L) ] ]
    let nonBool = foldIdxCard (envOf nonBoolDecls) (v "NB")
    check "NEGATIVE: a conjunct folding to a non-boolean is an ERROR, not a silent exclusion"
          (isErr nonBool && (msgOf nonBool).Contains "is not a boolean") (msgOf nonBool)

    // (h) idx_card's own argument discipline.
    let env7 = cgEnv 1L
    check "NEGATIVE: idx_card of an unknown name, of a non-identifier, and of the wrong arity all fail"
          (isErr (foldIdxCard env7 (v "NoSuchStruct"))
           && isErr (foldIdxCard env7 (lit 3L))
           && isErr (evalExpr env7 maxSteps (e (ExprApp (v "idx_card", [ v "CGm112"; lit 1L ])))))
          (msgOf (foldIdxCard env7 (lit 3L)))
    check "idx_card of a static VALUE says so, rather than reporting an unknown struct"
          (let r = foldIdxCard env7 (v "l1")
           isErr r && (msgOf r).Contains "static VALUE")
          (msgOf (foldIdxCard env7 (v "l1")))
    check "idx_card is a CORE builtin: it is in knownBuiltinNames with no ml import anywhere"
          (Set.contains "idx_card" (knownBuiltinNames ())) ""

    // ---- (i) THE FOLD BUDGET ITSELF ----------------------------------------
    // These pin `StaticEval`, not the counting layer, and they are here
    // because this file is where the defect they close was recorded: family 5
    // above used to say that the plan's fuel bomb could not be pinned at all.
    // The counting layer is also the budget's second consumer, and the only
    // one with a cost model of its own (`cellBudget`, per cell), so a change
    // to either number is felt here first.
    //
    // BOTH HALVES MATTER AND ONLY ONE OF THEM IS OBVIOUS. The budget used to
    // be a single number threaded as `fuel - 1` into every CHILD of a node:
    // both operands of a `+` received the same remainder from their parent,
    // which makes it a DEPTH bound named and reported as a step count. Two
    // things follow, and each gets a pin.
    let bombDecls =
        [ staticFn "bomb" [ "n" ] (e (ExprApp (v "bomb", [ bin OpAdd (v "n") (lit 1L) ])))
          structDecl "Bomb" true [ fieldHalfOpen "p" (lit 0L) (lit 2L) ]
              [ bin OpEq (v "p") (e (ExprApp (v "bomb", [ lit 0L ]))) ] ]
    let bombEnv = envOf bombDecls
    // (i.1) DEPTH. An unbounded recursion must come back as an Error. Under
    // the old threading this did not return at all: 100,000 nested evaluator
    // frames exhaust the compiler's 64 MB stack (Runtime.largeStackBytes)
    // long before the counter reaches zero, so the process died with an
    // uncatchable StackOverflowException. A test could not have caught that —
    // it would have taken the test runner with it — which is exactly why the
    // guard went unnoticed being unreachable for as long as it did.
    let depthBomb = evalExpr bombEnv maxSteps (e (ExprApp (v "bomb", [ lit 0L ])))
    check "NEGATIVE: an unbounded static recursion is DIAGNOSED, not a stack overflow"
          (isErr depthBomb && (msgOf depthBomb).Contains "depth limit exceeded") (msgOf depthBomb)
    // The same runaway inside a CONJUNCT, which is the plan's §6 risk 3 fuel
    // bomb — pinnable at last, and with the witness cell family 5 requires of
    // every per-cell failure. Corpus index-types/156 is this test's twin
    // through the full pipeline.
    let conjBomb = foldIdxCard bombEnv (v "Bomb")
    check "NEGATIVE: the fuel bomb IN A CONJUNCT is diagnosed, at its witness cell (§6 risk 3)"
          (isErr conjBomb
           && (msgOf conjBomb).Contains "depth limit exceeded"
           && (msgOf conjBomb).Contains "(p = 0)"
           && (msgOf conjBomb).Contains "conjunct 1") (msgOf conjBomb)
    // (i.2) STEPS, the half a depth bound cannot do and the reason the fix
    // was not simply "lower the depth number". `wide` doubles at every level
    // and bottoms out at depth 24 — nowhere near `maxDepth` — so a pure depth
    // bound would let it run 2^24 nodes to completion. It must be refused,
    // and refused by the STEP guard, because siblings now draw from one pool
    // instead of each inheriting a copy of the parent's remainder.
    //
    // 24 levels is chosen to be decisive in both directions: 2^24 = 16.7M
    // node visits against a 100,000-step budget is a 167x overrun, while the
    // work actually done before the guard fires is bounded by the budget, so
    // this pin costs a fraction of a second rather than the minutes the
    // unguarded fold would take.
    let wideDecls =
        [ for lvl in 0 .. 23 ->
            staticFn (sprintf "w%d" lvl) [ "n" ]
                (bin OpAdd (e (ExprApp (v (sprintf "w%d" (lvl + 1)), [ v "n" ])))
                           (e (ExprApp (v (sprintf "w%d" (lvl + 1)), [ v "n" ]))))
          yield staticFn "w24" [ "n" ] (v "n") ]
    let wide = evalExpr (envOf wideDecls) maxSteps (e (ExprApp (v "w0", [ lit 1L ])))
    check "NEGATIVE: a WIDE shallow fold (2^24 nodes at depth 24) is refused by the STEP budget"
          (isErr wide && (msgOf wide).Contains "step limit exceeded") (msgOf wide)
    // ...and the same shape just under the budget still folds, so the pin
    // above is bounding work rather than banning branching: 2^13 = 8192
    // leaves is comfortably inside 100,000 steps.
    let narrowDecls =
        [ for lvl in 0 .. 12 ->
            staticFn (sprintf "n%d" lvl) [ "n" ]
                (bin OpAdd (e (ExprApp (v (sprintf "n%d" (lvl + 1)), [ v "n" ])))
                           (e (ExprApp (v (sprintf "n%d" (lvl + 1)), [ v "n" ]))))
          yield staticFn "n13" [ "n" ] (lit 1L) ]
    let narrow = evalExpr (envOf narrowDecls) maxSteps (e (ExprApp (v "n0", [ lit 0L ])))
    check "a branching fold that FITS the step budget still folds (2^13 leaves = 8192)"
          (match narrow with Ok (SVInt n) -> n = 8192L | _ -> false)
          (match narrow with Ok vv -> ppStaticValue vv | Error m -> m)
    // (i.3) RE-ENTRANCY. `idx_card` is a syntactic builtin, so it is reachable
    // from the conjuncts of the struct it is counting — and because a
    // syntactic builtin receives a step COUNT rather than the live fold, every
    // hop through it restarts the budget at depth zero. The depth guard cannot
    // see this cycle; the layer that owns the re-entry owns the guard. Keyed
    // on the struct NAME, so an indirect cycle is the same check.
    let cycleDecls =
        [ structDecl "Cyc" true [ fieldHalfOpen "p" (lit 0L) (lit 3L) ]
              [ bin OpEq (v "p") (e (ExprApp (v "idx_card", [ v "Cyc" ]))) ] ]
    let cyc = foldIdxCard (envOf cycleDecls) (v "Cyc")
    check "NEGATIVE: idx_card reachable from the struct's OWN conjuncts is refused as circular"
          (isErr cyc && (msgOf cyc).Contains "idx_card(Cyc) again") (msgOf cyc)
    // The guard must not leak: a refused cycle leaves nothing behind, so the
    // very next call on this thread counts normally. `finally`, not a trailing
    // statement — enumerateBox's certificate failures raise.
    check "the cycle guard is unwound on failure: a later count on the same thread is unaffected"
          (match structCard (cgEnv 1L) "CGm112" with Ok 7 -> true | _ -> false) ""

    // ---- the certificate itself, exercised deliberately --------------------
    // Every call above ran it. This last pin makes the coverage explicit: a
    // constraint that keeps a SCATTERED subset (so the arrow route really does
    // prune interior prefixes) over a 4-field box, plus one whose solutions
    // are a single deep leaf (every prefix but one dies at level 1).
    let scattered =
        enumerateBox "Scatter" (box3 [ "a", -2L, 2L; "b", -2L, 2L; "c", -2L, 2L; "d", 0L, 1L ])
            (fun cell -> Ok ((get cell "a" + get cell "b" + get cell "c") % 3L = 0L
                             && get cell "d" = (if get cell "a" > 0L then 1L else 0L)))
    check "the certificate survives a 4-field scattered solution set (250-cell box, heads pruning active)"
          (match scattered with
           | Ok r -> r.Card = List.length r.Entries && r.Entries = List.sort r.Entries && r.Card > 0
           | Error _ -> false)
          (match scattered with Ok r -> sprintf "card %d of 250" r.Card | Error m -> m)
    let needle =
        enumerateBox "Needle" (box3 [ "a", 0L, 9L; "b", 0L, 9L; "c", 0L, 9L ])
            (fun cell -> Ok (get cell "a" = 7L && get cell "b" = 3L && get cell "c" = 5L))
    check "the certificate survives a single-solution box (999 of 1000 prefixes pruned by the heads route)"
          (match needle with Ok r -> r.Card = 1 && r.Entries = [ [ 7L; 3L; 5L ] ] | Error _ -> false) ""

    printFooter "Struct Idx" [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
    { Block = "Struct Idx"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
