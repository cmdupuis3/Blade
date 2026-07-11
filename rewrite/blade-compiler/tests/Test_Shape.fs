// F#-level unit tests for the canonical ExprShape traversal (audit §3.2):
// childrenOf/rebuildWith round-trips, mapIRExpr identity/rewrite behavior,
// and collectVarRefsIR completeness — including the positions the old
// hand-maintained walkers silently skipped via `| _ ->` catchalls
// (IRSlice, IRZip, IRShift's BndPad, IRRange's offset, match guards).
module Blade.Tests.Shape

open Blade
open Blade.IR
open Blade.Tests.TestHarness

let private f64 = IRTScalar ETFloat64
let private vX = IRVar (101, f64)
let private vY = IRVar (102, f64)
let private vZ = IRVar (103, f64)
let private lit n = IRLit (IRLitInt (int64 n))
let private add a b = IRBinOp (IRElementwise, IRAdd, a, b)

/// A battery of expressions covering the structurally interesting shapes:
/// fixed arities, head+list, lists, records-with-children, optional
/// children (guards, pads, offsets), and binders.
let private battery : (string * IRExpr) list = [
    "leaf lit", lit 7
    "leaf var", vX
    "binop", add vX vY
    "if", IRIf (vX, vY, vZ)
    "let", IRLet (7, vX, add (IRVar (7, f64)) vY)
    "app", IRApp (vX, [vY; vZ], f64)
    "index", IRIndex (vX, [vY; lit 0], None)
    "tuple", IRTuple [vX; vY; vZ]
    "struct lit", IRStructLit ("P", [("a", vX); ("b", vY)])
    "slice", IRSlice (vX, 0, vY, vZ)
    "curry", IRCurry (vX, vY, 1)
    "zip", IRZip [vX; vY]
    "stack", IRStack [vX; vY]
    "join", IRJoin ([vX; vY], 0)
    "subset", IRSubset (vX, 0, vY, vZ)
    "shift pad", IRShift (vX, 0, vY, BndPad vZ)
    "shift shrink", IRShift (vX, 0, vY, BndShrink)
    "align pad", IRAlign ([vX; vY], { Offsets = [(0, 1)]; Boundary = BndPad vZ })
    "for-range", IRForRange (9, lit 0, vX, add (IRVar (9, f64)) vY)
    "match guarded",
        IRMatch (vX,
                 [ { Pattern = IRPatVar 8; Guard = Some (add (IRVar (8, f64)) vY); Body = vZ }
                   { Pattern = IRPatWild; Guard = None; Body = vY } ])
    "gram", IRGram (vX, vY, false)
    "sequence", IRSequence [vX; vY]
]

let runShapeTests () : BlockResult =
    printHeader "ExprShape Traversal Tests"
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

    // 1. rebuildWith e (childrenOf e) reproduces e exactly.
    for (name, e) in battery do
        check (sprintf "round-trip: %s" name) (rebuildWith e (childrenOf e) = e) ""

    // 2. mapIRExpr id is the identity.
    for (name, e) in battery do
        check (sprintf "mapIRExpr id: %s" name) (mapIRExpr id e = e) ""

    // 3. mapIRExpr rewrites reach EVERY child position, including the ones
    //    the old per-walker matches skipped (BndPad, IRRange offset).
    let renumber = mapIRExpr (function IRVar (101, t) -> IRVar (201, t) | e -> e)
    let hits e = Set.contains 201 (collectVarRefsIR (renumber e))
    check "rewrite reaches BndPad" (hits (IRShift (vY, 0, vZ, BndPad vX))) ""
    check "rewrite reaches IRRange offset" (hits (IRRange ([], Some vX))) ""
    check "rewrite reaches align pad"
        (hits (IRAlign ([vY], { Offsets = []; Boundary = BndPad vX }))) ""

    // 4. collectVarRefsIR completeness on previously-skipped variants.
    let sees ids e = collectVarRefsIR e = Set.ofList ids
    check "collect: slice" (sees [101; 102; 103] (IRSlice (vX, 0, vY, vZ))) ""
    check "collect: curry" (sees [101; 102] (IRCurry (vX, vY, 1))) ""
    check "collect: zip" (sees [101; 102] (IRZip [vX; vY])) ""
    check "collect: shift pad" (sees [101; 102; 103] (IRShift (vX, 0, vY, BndPad vZ))) ""
    check "collect: match guard" (sees [101; 8; 102; 103]
        (IRMatch (vX, [ { Pattern = IRPatVar 8; Guard = Some (IRVar (8, f64)); Body = add vY vZ } ]))) ""
    // IRForRange's loop var is the one binder this collector subtracts.
    check "collect: for-range binder excluded"
        (sees [101; 102] (IRForRange (9, lit 0, vX, add (IRVar (9, f64)) vY))) ""

    // 5. exprAttrs binder scoping via BinderShape.
    let letAttrs = exprAttrs (IRLet (7, vX, add (IRVar (7, f64)) vY))
    check "attrs: let scopes its id"
        (letAttrs.FreeVars = Set.ofList [101; 102] && Set.contains 7 letAttrs.BoundVars) ""
    let matchAttrs =
        exprAttrs (IRMatch (vX, [ { Pattern = IRPatVar 8
                                    Guard = Some (IRVar (8, f64))
                                    Body = add (IRVar (8, f64)) vY } ]))
    check "attrs: pattern id scoped in guard+body"
        (matchAttrs.FreeVars = Set.ofList [101; 102] && Set.contains 8 matchAttrs.BoundVars) ""

    // 6. Canonical typing (typeOf, audit §2.2) — spot checks on each
    //    active-pattern family plus the rules that diverged between the old
    //    CodeGen.inferExprType and IR.liftInferType copies.
    let idx1 : IRIndexType =
        { Id = 50; Rank = 1; Extent = lit 3; Symmetry = SymNone
          Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    let arrTy = mkArrayArrow [idx1] f64 None
    let vA = IRVar (110, arrTy)
    check "typeOf: literal (CarriedType)" (typeOf (lit 7) = IRTScalar ETInt64) ""
    check "typeOf: var (CarriedType)" (typeOf vX = f64) ""
    check "typeOf: let body (TypeVia)" (typeOf (IRLet (7, lit 1, vX)) = f64) ""
    check "typeOf: extent (IntValued)" (typeOf (IRExtent (vA, 0)) = IRTScalar ETInt64) ""
    check "typeOf: arithmetic promotion"
        (typeOf (IRBinOp (IRElementwise, IRAdd, lit 1, IRLit (IRLitFloat 2.0))) = IRTScalar ETFloat64) ""
    check "typeOf: comparison is Bool"
        (typeOf (IRBinOp (IRElementwise, IRLt, vX, vY)) = IRTScalar ETBool) ""
    check "typeOf: full index peels to element" (typeOf (IRIndex (vA, [lit 0], None)) = f64) ""
    check "typeOf: mask is Bool over same index space"
        (match typeOf (IRMask (vA, vX)) with
         | ArrayElem a -> a.ElemType = IRTScalar ETBool && a.IndexTypes = [idx1]
         | _ -> false) ""
    check "typeOf: sort preserves type (was lift-only rule)" (typeOf (IRSort (vA, vX)) = arrTy) ""
    check "typeOf: contains is Bool" (typeOf (IRContains (vA, vX)) = IRTScalar ETBool) ""
    // exprTypeIfKnown stays the CarriedType tier: no reconstruction.
    check "exprTypeIfKnown: carried" (exprTypeIfKnown vX = Some f64) ""
    check "exprTypeIfKnown: no reconstruction"
        (exprTypeIfKnown (IRBinOp (IRElementwise, IRAdd, vX, vY)) = None) ""

    printFooter "ExprShape" [sprintf "%d passed" passed; sprintf "%d failure(s)" failed]
    { Block = "ExprShape"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
