// The type checker's unit and arithmetic RESULT rules, outside the inference chain (cleanup round 2, K5): the
// scalar and array result types of the arithmetic operators, the unit algebra of operators, intrinsics and kernel
// bodies, the enum-key casts, and resolveTypedExpr(Deep). Nothing here calls back into TypeCheckInfer's recursive core,
// so these functions take no entry points; the chain calls them directly.
module Blade.TypeCheckInferUnits

open Blade.Ast
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.TypedAst
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk
open Blade.TypeCheckIde
open Blade.TypeLower
open Blade.TypeCheckSupport
open Blade.TypedBuilder

/// R3 (deep review; the D2 ruling): a value of an INTEGER EnumIdx `R` is one
/// of R's keys, represented as its POSITION. True when `t` is such a value
/// (`R` itself, resolved through the substitution).
let rec intEnumValueType (env: TypeEnv) (t: IRType) : bool =
    match IR.stripUnits (env.Subst.Resolve t) with
    | IntTaggedIndex tag -> (intEnumKeysOfTag env tag).IsSome
    | _ -> false

/// A typed value of an integer EnumIdx, OBSERVED: `Int64(t)`, the key (zonk
/// reads it off the position). Anything else -- and a literal, whose R type
/// comes from its context -- is returned as it is.
and observeEnumKeyTyped (env: TypeEnv) (t: TypedExpr) : TypedExpr =
    if intEnumValueType env t.Type && not t.Kind.IsTExprLit then
        mkTypedSpan (TExprUnaryOp (OpCast "Int64", t)) (IRTScalar ETInt64) t.Span
    else t

/// ...a DENSE array of them: every slot a plain (IxKPlain, unsymmetric) index
/// -- the storage the fused fold terminal and prodsum are built for. A
/// compound, sparse, ragged or grouped array of R values is not (lead review
/// of R3: the deferred key map over a compound reached BL7004 in the back
/// end, prodsum over one BL9002).
and intEnumDenseArrayType (env: TypeEnv) (t: IRType) : bool =
    match IR.stripUnits (env.Subst.Resolve t) with
    | ArrayElem at ->
        intEnumValueType env at.ElemType
        && at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone)
    | _ -> false

/// R13 (deep review): the STORAGE classes `prodsum` has no loop for. Its
/// emitters walk an operand as a dense rank-1 buffer (`.extents[0]` and
/// `[t]`); a compound selection (`compound(data, mask)`, any mask rank) and a
/// sparse array (`sparse(values, keys)`, a `range<SparseIdx<K>>` map) are
/// tabulated storage -- `Compound<T, R>` / `Sparse<T, R>` in the C++ lane,
/// neither of which has either -- so prodsum over one of them, as any operand
/// and at any element type, failed in g++ (BL9002). Keyed on the slot's
/// storage FAMILY (`IR.IxCompound`, which groups exactly those IxKinds), not on
/// how the operand is spelled: a let, a capture in a kernel, an operand beside
/// a dense one. Ragged and grouped rows are NOT in it: a row of either is a
/// dense run of cells, and prodsum over one is built (sql-group-by/071). The
/// answer names the storage, for the message.
and prodsumUnbuiltStorage (env: TypeEnv) (t: IRType) : string option =
    match IR.stripUnits (env.Subst.Resolve t) with
    | ArrayElem at ->
        at.IndexTypes |> List.tryPick (fun ix ->
            match ix with
            | IxCompound -> Some (if ix.IxKind = IxKSparse then "sparse" else "compound")
            | _ -> None)
    | _ -> None

/// ...and an ARRAY of them (any rank).
and intEnumArrayType (env: TypeEnv) (t: IRType) : bool =
    match IR.stripUnits (env.Subst.Resolve t) with
    | ArrayElem at -> intEnumValueType env at.ElemType
    | _ -> false

/// `Int64(e)`: the spelling that OBSERVES a value of an integer EnumIdx -- its
/// key (an array of them: each cell's key, the lifted cast). Built only where
/// the checker already knows `e` is one (the cast's own arm and zonk read the
/// key off the position, Zonk.intEnumEdges). A user binding named `Int64`
/// shadows the cast head, so the callers decline then.
and enumKeyCastAst (e: Expr) : Expr =
    let sp = e.Span
    mkExpr sp (ExprApp (mkExpr sp (ExprVar "Int64"), [ e ]))

/// The DEFERRED twin for a fold operand: `method_for(e) <@> lambda(c) ->
/// Int64(c)`, which the fold consumes as its fused terminal (no stored
/// temporary; the operand is read where the fold reads it, as before).
and enumKeyMapAst (env: TypeEnv) (e: Expr) : Expr =
    let sp = e.Span
    let pn = $"__ekv{env.Builder.FreshId()}"
    let p : LambdaParam = { Name = pn; Type = None; Default = None; NameSpan = noSpan }
    mkExpr sp (ExprBinOp (Elementwise, OpApply, mkExpr sp (ExprMethodFor [ e ]),
                          mkExpr sp (ExprLambda ([ p ], None, enumKeyCastAst (mkExpr sp (ExprVar pn))))))

/// Unit soundness of a fold, checked at the rank-1 reduce site. `reduce`
/// types its result as the array's ELEMENT type, right only if the kernel
/// PRESERVES the element's unit signature: folding n meters through `+`
/// gives meters (holds), through `*` gives meters^n (a grade that varies
/// with the EXTENT, no fixed signature). `+`/`-` are unit-endomorphic and
/// pass; `*`/`/` over a dimensioned element reject. (min/max are the other
/// textbook endomorphic members but exist only as bounded-type keywords,
/// not callable, so no kernel can name them; docs/features/sql.md's T x T
/// -> T fold kernel is exactly this restriction.)
///
/// The kernel's own ARROW can't answer this: sections are typed as strict
/// endomorphisms `(tau,tau)->tau` after the caller unifies params with the
/// element type, so every arrow says "unit-preserving" by construction.
/// Recompute the real signature instead (section: per-op unit table; lambda:
/// kernel-body walk with params already unified). A dimensionless element is
/// unconstrained (the `*`-fold escape hatch); a kernel whose form the walk
/// cannot read (named function, captured value) is left alone.
and reduceKernelUnitCheck (env: TypeEnv) (tKernel: TypedExpr) (elemTy: IRType) : TypeResult<unit> =
    match IR.getUnits (env.Subst.Resolve elemTy) with
    | Some eu when not (Map.isEmpty (unitNormalize eu).Dims) ->
        let kernelUnits =
            match (resolveTypedExpr env tKernel).Kind with
            | TExprSection op -> Some (unitRulesForOp op (Some eu) (Some eu))
            | TExprLambda info -> Some (kernelBodyUnits env Map.empty info.Body)
            | _ -> None
        match kernelUnits with
        | None -> Ok ()
        | Some computed ->
            computed |> Result.bind (fun ku ->
                let preserves =
                    match ku with
                    // Magnitude counts as part of "preserves": a fold is
                    // typed at its element's signature, so a kernel that
                    // returned a different magnitude would mislabel the
                    // accumulator with no factor anywhere to fix it.
                    | Some u -> unitCompatible u eu && unitSameScale u eu
                    | None -> false
                if preserves then Ok ()
                else
                    Error (UnitMismatch (
                                "reduce kernel (a fold is typed at its element's unit, so the kernel must preserve that unit -- `+` and `-` do; `*` and `/` do not, since folding n of them yields a grade that depends on the extent)",
                                ppUnitSig eu,
                                (match ku with Some u -> ppUnitSig u | None -> "dimensionless"))))
    | _ -> Ok ()

/// Rule 7 (round 6): a SAME-CLASS cast of a literal-only operand, or of an
/// array-literal nest whose every leaf is literal-only, is the operand
/// RETYPED to the cast's width (a scalar keeps a cast node that converts
/// nothing; an array literal is rebuilt at the width). None otherwise.
and castOfLiteralOnly (env: TypeEnv) (span: Span) (target: ElemType) (tArg: TypedExpr) : TypedExpr option =
    let subst = env.Subst
    let cls = litClassOf target
    let rec retypeElemDeep (t: IRType) =
        match subst.Resolve t with
        | ArrayElem a -> mkArrayLike { a with ElemType = retypeElemDeep a.ElemType }
        | IRTScalar et when litClassOf et = cls -> IRTScalar target
        | r -> r
    let rec nest (e: TypedExpr) : TypedExpr option =
        match e.Kind with
        | TExprArrayLit (elems, arrTy) when not elems.IsEmpty ->
            let elems' = elems |> List.map nest
            if elems' |> List.forall Option.isSome then
                let arrTy' =
                    match retypeElemDeep (mkArrayLike arrTy) with
                    | ArrayElem a -> a
                    | _ -> arrTy
                Some { e with Kind = TExprArrayLit (elems' |> List.map Option.get, arrTy'); Type = retypeElemDeep e.Type }
            else None
        | TExprArrayLit _ -> None
        | _ ->
            match literalOnlyElem subst e with
            | Some le when litClassOf le = cls -> retypeLiteralOnly subst target e
            | _ -> None
    if cls < 0 then None
    else
        ignore span
        nest tArg

and inferArithScalar (env: TypeEnv) (mode: BinOpMode) (op: BinOp) (tL: TypedExpr) (tR: TypedExpr) (lRes: IRType) (rRes: IRType) (isZipOp: bool) : TypeResult<TypedExpr> =
    // (Literal width adaptation -- `a32 * 1.0` stays Float32, `i32 + 1`
    // is Int32 -- happened above, adaptLiteralOperands, in both
    // directions; this site also serves the array/scalar broadcast arm
    // above, whose synthesized kernel body re-enters inferBinOp.)
    // TWO GENERIC VARIABLES MIXED BY ARITHMETIC are ONE variable
    // (formalism 2.4: there is no promotion type in signatures, so
    // the signature names one variable and the caller converts).
    // The promotion rules below answer the LEFT variable for `x + y`
    // over `x: T, y: U` (or two unannotated parameters), so an
    // instance `T = Int64, U = Float64` typed the Float64 sum Int64
    // (g++ refused the narrowing, BL9002, or the value truncated).
    // Unifying them makes each call give both operands one type, and
    // the arguments must agree EXACTLY (firstAbstractVarConflict):
    // `add(2.5, 1)` and `add(1, 2.5)` are both refused at the call,
    // literals included -- the caller converts. Only
    // bare generic variables that an instance makes scalars or
    // arrays alike (no caret, no rank bound -- those are shaped by
    // the paths above) and no literal's own variable; a comparison
    // mixes nothing into its Bool result.
    //
    // NOT `^`: its exponent is POLYMORPHIC (formalism 2.4) -- an
    // integer exponent keeps the base's type (repeated squaring), a
    // floating one is a floating pow -- so `pw(x, n) = x ^ n` keeps two
    // variables, is typed as the base's, and each call is judged by
    // GOPowExponent (an exponent that would promote the base away --
    // a Float64 exponent of an Int64 base -- is refused at the base).
    (if mode = Elementwise && op = OpCaret then
        let genVar (t: IRType) =
            match env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits with
            | IRTInfer vid when env.Subst.IsPolymorphicId vid
                                && (env.Subst.GetLiteralDefault vid).IsNone -> Some vid
            | _ -> None
        // A CONCRETE base beside a generic exponent (`2.0 ^ n`): typed
        // as the base, so the exponent must not promote it.
        let concreteElem (t: IRType) =
            match IR.stripUnits (env.Subst.Resolve t) with
            | IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128 as et) -> Some et
            | ArrayElem arr ->
                (match IR.stripUnits (env.Subst.Resolve arr.ElemType) with
                 | IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128 as et) -> Some et
                 | _ -> None)
            | _ -> None
        match env.CurrentGenericObligations with
        | Some acc when not env.InLambdaBody ->
            (match genVar tL.Type, genVar tR.Type, concreteElem tL.Type with
             | Some b, Some e, _ when b <> e -> acc.Add(GOPowExponent (b, e))
             | None, Some e, Some be -> acc.Add(GOPowBase (e, be))
             | _ -> ())
        | _ -> ()
     elif mode = Elementwise then
        let isArithOp =
            match op with
            | OpAdd | OpSub | OpMul | OpDiv | OpMod -> true
            | _ -> false
        let genVar (t: IRType) =
            match env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits with
            | IRTInfer vid when env.Subst.IsPolymorphicId vid
                                && (env.Subst.GetLiteralDefault vid).IsNone
                                && (env.Subst.GetArityConstraint vid).IsNone
                                && (match env.Subst.GetRankLowerBound vid with
                                    | Some k -> k < 1
                                    | None -> true) -> Some vid
            | _ -> None
        // A COMPARISON between two generic parameter variables (`a > b`
        // over `a: T, b: U`) meets them exactly as the arithmetic twin
        // below does: one variable, so two arrays of different rank are
        // refused at the call (they share no elementwise shape), and a
        // scalar beside an array is the call's elementwise lift.
        (match op with
         | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe | OpAnd | OpOr
                when env.CurrentGenericObligations.IsSome && not env.InLambdaBody ->
             (match genVar tL.Type, genVar tR.Type with
              | Some a, Some b when a <> b
                                   && (env.Subst.GetElemCast a).IsNone && (env.Subst.GetElemCast b).IsNone ->
                  unify env.Subst (IRTInfer a) (IRTInfer b) |> ignore
              | _ -> ())
         | _ -> ())
        if isArithOp then
            match genVar tL.Type, genVar tR.Type with
            | Some a, Some b when a <> b -> unify env.Subst (IRTInfer a) (IRTInfer b) |> ignore
            | _ ->
            // Two generic variables the arm above cannot make one --
            // a caret `T^k` stands for its whole array, so `a + b`
            // over `a: T^1, b: U^1` keeps two variables (their extents
            // and index structure may differ) and is typed as the
            // LEFT one. Their ELEMENTS still meet, and must agree
            // exactly at every call (GOElemAgree): an Int64 and a
            // Float64 array typed the Float64 sum Int64 (BL9002), an
            // Int32 and an Int64 one wrapped. Recorded only while the
            // declaration's body is checked, for parameter variables
            // (polymorphic, no literal's own).
            // A variable a PARAMETER is declared as (CurrentSignature
            // ends with the return type, which is no argument).
            let paramTop (vid: int) =
                let sigTys =
                    env.CurrentSignature
                    |> List.map (fun t -> env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits)
                let paramTys = if sigTys.IsEmpty then [] else List.take (sigTys.Length - 1) sigTys
                paramTys |> List.exists (fun t -> match t with IRTInfer r -> r = vid | _ -> false)
            let anyGenVar (t: IRType) =
                match env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits with
                | IRTInfer vid when env.Subst.IsPolymorphicId vid
                                    && (env.Subst.GetLiteralDefault vid).IsNone
                                    && paramTop vid -> Some vid
                | _ -> None
            match env.CurrentGenericObligations, anyGenVar tL.Type, anyGenVar tR.Type with
            | Some acc, Some a, Some b when a <> b ->
                let opName =
                    match op with
                    | OpAdd -> "+" | OpSub -> "-" | OpMul -> "*" | OpDiv -> "/"
                    | OpMod -> "%" | _ -> "^"
                acc.Add(GOElemAgree (a, b, opName))
            | _ -> ())
    // env.Builder: inferArithType mints fresh index-type ids for a
    // synthesized outer-product result (same allocator deduceOutputType
    // uses for the method_for output type).
    //
    // An OUTER op is typed on the RESOLVED operands: a caret operand
    // the demand above just shaped still carries its bare variable on
    // the typed node, and the outer rule read that as a non-array and
    // answered the left variable -- rank 1 for `a [*] b` over two
    // `T^1` parameters. (Elementwise keeps the node types: its
    // array-producing arms already read `lRes`/`rRes` above.)
    let lArith, rArith =
        if mode = Outer then env.Subst.Resolve tL.Type, env.Subst.Resolve tR.Type
        else tL.Type, tR.Type
    inferArithType env.Builder mode op lArith rArith (Some tR) |> Result.bind (fun resTy0 ->
        // (A mixed-elem-type op was refused above -- BL3020, the
        // mixing rule -- so no implicit conversion reaches here.)
        // S1 SEAM 2 (docs/plan-kernel-body-materialization.md, M-B, the
        // concrete-operand triple). One operand is a real array, the
        // other an UNRESOLVED inference var -- the shape an enclosing
        // kernel's own parameter has while its body is being inferred
        // (`ws <@> lambda(w) -> { let e = exp <@> (w * ts); ... }`).
        // inferArithType's Elementwise table has no arm for that pair,
        // so it fell through to `| _ -> lBare` and handed back THE VAR
        // ITSELF as the product's type. Two things followed, both
        // wrong: the nested `<@>` saw a non-array operand, degraded it
        // to a rank-0 record, and typed `e` a scalar, so `prodsum(e,e)`
        // refused a value that is an array; and any consumer that DID
        // issue an array demand (prodsum, reduce) satisfied it by
        // binding the ALIASED var -- i.e. by making the enclosing
        // kernel's scalar parameter an array, which lowering then
        // emitted as a bogus zip over the parameter.
        //
        // The result SHAPE is knowable without settling which arm
        // applies: whether `w` turns out to be a scalar (broadcast) or
        // an array (zip), an elementwise op against a rank-r array
        // yields that same rank-r shape -- a zip requires the shapes to
        // agree. So stamp the shape and nothing else. The node stays a
        // plain TExprBinOp, so lowering still reads the RESOLVED
        // operands and picks broadcast or zip exactly as before.
        //
        // Element type follows the array operand, which is
        // `promoteElem`'s own answer when the other side carries no
        // information. A var that later resolves complex against a real
        // array is the pre-existing mixed real/complex promotion gap
        // (issue #18), not something this seam can settle -- and the
        // spelling that pins it (`i * w * ts`, complex first) already
        // reaches the resolved-scalar broadcast arm above.
        //
        // Narrow on purpose: only the case where inferArithType handed
        // back a bare VAR is repaired. `ts * w` (array on the LEFT)
        // already answers with the array -- `| _ -> lBare` happens to be
        // right in that orientation -- and is left untouched, wrapper
        // and all.
        //
        // A caret-shorthand `T^k` var counts as unresolved here TOO, and
        // this is what carries M-B inside a NAMED function body, where
        // materializeArityVar deliberately abstains so HM keeps the
        // signature var (see its note). `examples/lswosa.blade`'s
        // `hanning` ends `(s_sub * w / sqrt(scale)) |> compute` with
        // `s_sub: U^1` and `w` a concrete array: repairing the SHAPE
        // makes the function's return array-typed -- which is all the
        // caller's `reduce(sw, (+))` ever needed -- while `U` stays free
        // for the monomorphizer. Nothing is bound; only this node's own
        // type is stamped.
        //
        // AN OUTER PRODUCT WITH AN OPEN GENERIC OPERAND (D4): a pack
        // `head` (rank k, its instance's array) beside the recursive
        // call's still-open result (`head [*] outer(tail)`). Its rank
        // is rank(head) + rank(partner), which only an instance knows
        // -- the left-operand convention answered `head`'s own
        // variable, so every instance was typed rank k. Answer a fresh
        // variable, kept open (polymorphic) for monomorphization to
        // decide per instance (IRMono learns it from the specialized
        // body, outer rank = sum of ranks), with a rank lower bound
        // when either side is known to be an array.
        let outerOpenResult =
            if mode <> Outer then None
            else
                let leaf (t: IRType) = IR.stripUnits (env.Subst.Resolve t)
                let openVar (t: IRType) =
                    match leaf t with IRTInfer v -> Some v | _ -> None
                let polyOpen (t: IRType) =
                    match openVar t with
                    | Some v -> env.Subst.IsPolymorphicId v
                    | None -> false
                let arrayish (t: IRType) =
                    match leaf t with
                    | ArrayElem _ -> true
                    | IRTInfer v ->
                        (match env.Subst.GetArityConstraint v, env.Subst.GetRankLowerBound v with
                         | Some k, _ when k >= 1 -> true
                         | _, Some k when k >= 1 -> true
                         | _ -> false)
                    | _ -> false
                // ...or any open operand inside a PACK function's
                // body (a settled base, `Poly<Float64^1>`, gives a
                // concrete head beside the call's open result).
                let inPackBody =
                    env.CurrentSignature
                    |> List.exists (fun t -> (env.Subst.Resolve t).IsIRTPoly)
                if (openVar lArith).IsSome || (openVar rArith).IsSome then
                    if polyOpen lArith || polyOpen rArith || inPackBody then
                        match env.Subst.Fresh() with
                        | IRTInfer r as fresh ->
                            env.Subst.MarkPolymorphic r
                            if arrayish lArith || arrayish rArith then env.Subst.AddRankLowerBound(r, 1)
                            Some fresh
                        | t -> Some t
                    else None
                else None
        let resTy =
            match outerOpenResult with
            | Some t -> t
            | None ->
            arithResultType env resTy0 op mode lRes rRes tL tR isZipOp
        // A generic ELEMENT variable (an array parameter's cell, the
        // `reduce(row, (+))` of a `row: T^1`) beside a concrete real
        // scalar keeps the promotion rules' SCALAR result (`/ 2.0`
        // types Float64). That is exact only at the instances whose
        // element promotes to that scalar -- a complex row's quotient
        // is complex, and would reach g++ as a Complex128 in a
        // Float64 slot (BL9002) -- so record what each call must keep
        // true (GOBroadcastElem, judged per instance); the mixing
        // rule's own GOPromotion refuses the integer instances.
        (if mode = Elementwise && not env.InLambdaBody then
            match env.CurrentGenericObligations, binOpMixName op, IR.stripUnits (env.Subst.Resolve resTy) with
            | Some acc, Some opName, IRTScalar result when op <> OpCaret && result <> ETBool ->
                let elemVar (t: IRType) =
                    match IR.stripUnits (env.Subst.Resolve t) with
                    | IRTInfer v when env.Subst.IsPolymorphicId v && (env.Subst.GetElemCast v).IsNone
                                      && (env.Subst.GetLiteralDefault v).IsNone -> Some v
                    | _ -> None
                let partner (t: IRType) =
                    match IR.stripUnits (env.Subst.Resolve t) with
                    | IRTScalar et when et <> ETBool && et <> ETString -> Some et
                    | _ -> None
                (match elemVar tL.Type, partner tR.Type, elemVar tR.Type, partner tL.Type with
                 | Some v, Some p, _, _ | _, _, Some v, Some p -> acc.Add(GOBroadcastElem (v, opName, p, result))
                 | _ -> ())
            | _ -> ())
        // THE conversion seam. `*` and `/` need nothing: unitMul and
        // unitDiv fold the magnitudes into the result TYPE, so the
        // emitted code is untouched. Only the ops that require two
        // operands to share a magnitude bridge one at run time.
        //
        // The target is the result's own signature, so the factor is
        // judged by what the expression is supposed to BE -- which is
        // the annotation when there is one, and otherwise the left
        // operand (unitJoin's existing preference). Comparisons read
        // the left operand directly: their result is Bool and carries
        // no signature to aim at.
        let natural =
            match op with
            | OpAdd | OpSub -> IR.getUnits (env.Subst.Resolve resTy)
            | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
                IR.getUnits (env.Subst.Resolve tL.Type)
            | _ -> None
        // An enclosing annotation names the magnitude the result is
        // SUPPOSED to be, so aim the operands straight at it: one
        // factor each, computed in the magnitude the programmer
        // chose. Guarded by unitCompatible, so an annotation of a
        // different DIMENSION never becomes a conversion -- it falls
        // back to the natural signature and rejects as it always did.
        let target =
            match env.UnitTarget, natural with
            | Some t, Some n when unitCompatible t n -> Some t
            | _ -> natural
        match target with
        | None -> Ok (mkTyped (TExprBinOp (mode, op, tL, tR)) resTy)
        | Some dst ->
            let ctx =
                match op with
                | OpAdd -> "addition" | OpSub -> "subtraction"
                | _ -> "comparison"
            // The result now carries the magnitude actually computed
            // in, which is `dst` whenever an annotation redirected
            // it; leaving resTy at the natural join would label an
            // hours value as days. Comparisons keep resTy (Bool).
            let resTy' =
                match op, env.Subst.Resolve resTy with
                | (OpAdd | OpSub), IRTUnitAnnotated (inner, _) -> IRTUnitAnnotated (inner, dst)
                | _ -> resTy
            convertScaleTo env ctx dst tL |> Result.bind (fun tL' ->
            convertScaleTo env ctx dst tR |> Result.map (fun tR' ->
                mkTyped (TExprBinOp (mode, op, tL', tR')) resTy')))

and arithResultType (env: TypeEnv) (resTy0: IRType) (op: BinOp) (mode: BinOpMode) (lRes: IRType) (rRes: IRType) (tL: TypedExpr) (tR: TypedExpr) (isZipOp: bool) : IRType =
    let unboundVar t = (IR.stripUnits t).IsIRTInfer
    let reshape (arr: IRArrayType) =
        match IR.getUnits resTy0 with
        | Some u -> mkArrayLike { arr with ElemType = IRTUnitAnnotated (IR.stripUnits arr.ElemType, u) }
        | None -> mkArrayLike arr
    // A VAR KNOWN TO BE AN ARRAY beside a scalar: a pack
    // element `head` that some call already demanded rank >= 1
    // of (the call judgment's rank lower bound -- `mean(head)`),
    // or a caret var. The promotion rules would answer the
    // SCALAR's type (`head - mean(head)` typed Float64 once
    // stats.mean returned a concrete Float64), and the enclosing
    // `* comoment_prod(tail)` then broadcast the wrong operand
    // (arity/031: `Array<double,1> * double` in g++). The
    // result is shaped like the var; its element stays the
    // var's (monomorphization substitutes it).
    let knownArrayVar (t: IRType) =
        match IR.stripUnits t with
        | IRTInfer vid ->
            (match env.Subst.GetRankLowerBound vid, env.Subst.GetArityConstraint vid with
             | Some k, _ when k >= 1 -> true
             | _, Some k when k >= 1 -> true
             | _ -> false)
        | _ -> false
    let isScalarTy (t: IRType) = (IR.stripUnits t).IsIRTScalar
    // A COMPARISON / LOGICAL op over a still-unshaped caret
    // var (`a > b` over two `T^1` parameters: neither side
    // pins a shape, so the demand above rightly declined and
    // the op stays deferred to lowerArrayBinOpsModule, which
    // after monomorphization co-iterates it with a Bool
    // kernel). The promotion rules answered the SCALAR
    // `Bool` -- so the function returned `bool` while its body
    // built an Array<bool>: g++ took the array's implicit
    // pointer conversion and printed `true` for an elementwise
    // result; the interpreter refused to print an array as a
    // scalar. Whichever way the other (unpinned) side
    // resolves -- a scalar broadcast or a zip partner -- the
    // result is a Bool array of the caret's exact rank, so say
    // so. Its axes are the abstract ones a `^k` annotation
    // mints (extents unknown here, read at run time); the
    // arithmetic twin needs none of this because its result
    // is the operand variable itself.
    let caretRank (t: IRType) =
        match IR.stripUnits t with
        | IRTInfer vid ->
            (match env.Subst.GetArityConstraint vid with
             | Some k when k >= 1 -> Some k
             | _ -> None)
        | _ -> None
    let isCmpOrLogical =
        match op with
        | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe | OpAnd | OpOr -> true
        | _ -> false
    let deferredBoolRank =
        if mode = Elementwise && isCmpOrLogical && IR.stripUnits resTy0 = IRTScalar ETBool then
            match caretRank lRes, caretRank rRes with
            // Two carets of DIFFERENT ranks share no
            // elementwise shape: claim none, and leave the
            // pair to the path that judges it.
            | Some k, Some j when k <> j -> None
            | Some k, _ | None, Some k -> Some k
            | None, None -> None
        else None
    let abstractBoolArray (k: int) =
        let axes =
            List.init k (fun _ ->
                { Id = env.Builder.FreshId(); Rank = 1
                  Extent = IRParam ("?", 0, IRTNat None)
                  Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                  Kind = SDimension; Dependencies = [] })
        mkArrayArrow axes (IRTScalar ETBool) None
    match deferredBoolRank with
    | Some k -> abstractBoolArray k
    | None ->
    // A COMPARISON / LOGICAL op over a bare PARAMETER variable
    // (`function gt0(a: T^0, b: T^0) = a > b`; `T^0` is `T`,
    // which an array instantiates whole). The promotion rules
    // answered the scalar Bool, so an all-array call returned
    // `bool` while the body built an Array<bool> -- the
    // compiled program printed `true`. Comparison is
    // elementwise at every rank (formalism 6.1, 15.6; the D1
    // ruling): the result is "the operand's shape with element
    // Bool", which each instance settles (Subst.ElemBoolVar --
    // a scalar instance's Bool, an array instance's Bool
    // array). Beside a scalar the variable decides the shape
    // (the scalar broadcasts); beside a second open variable
    // (`a: T, b: U`) whichever instance is the array does. A
    // body that USES the result as a scalar (a declared
    // `-> Bool`, an `if` condition) pins the variable to rank 0
    // (Subst.PinScalar), so an array call is refused rather
    // than truncated. Only a variable a PARAMETER is declared
    // as (or an element cast of one): an array's element var,
    // a caret `T^k` (deferredBoolRank above) and a lambda's
    // own parameter keep their answers.
    let genericBool =
        if mode <> Elementwise || not isCmpOrLogical || env.InLambdaBody
           || env.CurrentGenericObligations.IsNone
           || IR.stripUnits resTy0 <> IRTScalar ETBool then None
        else
            let sigTys =
                env.CurrentSignature
                |> List.map (fun t -> env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits)
            let paramTys = if sigTys.IsEmpty then [] else List.take (sigTys.Length - 1) sigTys
            let isTop (vid: int) (t: IRType) = match t with IRTInfer r -> r = vid | _ -> false
            let paramTop (vid: int) =
                List.exists (isTop vid) paramTys
                && sigTys |> List.forall (fun t ->
                    isTop vid t || not ((freeInferVars env.Subst t).Contains vid))
            let openVar (t: IRType) =
                match env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits with
                | IRTInfer vid when env.Subst.IsPolymorphicId vid
                                    && (env.Subst.GetArityConstraint vid).IsNone
                                    && not (env.Subst.IsScalarPinned vid)
                                    && (match env.Subst.GetRankLowerBound vid with
                                        | Some k -> k < 1
                                        | None -> true)
                                    && (paramTop vid || (env.Subst.GetElemCast vid).IsSome) -> Some vid
                | _ -> None
            let scalarSide (t: IRType) =
                match IR.stripUnits (env.Subst.Resolve t) with
                | IRTScalar _ | IRTNat _ | IRTIdxTagged _ -> true
                | _ -> false
            match openVar lRes, openVar rRes with
            | Some a, Some b -> Some (env.Subst.ElemBoolVar(a, b, ETBool))
            | Some a, None when scalarSide rRes -> Some (env.Subst.ElemBoolVar(a, a, ETBool))
            | None, Some b when scalarSide lRes -> Some (env.Subst.ElemBoolVar(b, b, ETBool))
            | _ -> None
    match genericBool with
    | Some r -> r
    | None ->
    // `zero + a` over an unshaped caret `a`: the promotion
    // rules answer the LEFT operand, which is the zero's own
    // open variable; the value is `a`'s shape and element (the
    // zero broadcasts), exactly as `a + zero` answers.
    let zeroBesideCaret =
        mode = Elementwise && not isCmpOrLogical
        && (match tL.Kind with TExprZero -> true | _ -> false)
        && (caretRank rRes).IsSome
    // A GENERIC operand beside a REAL scalar, in a named
    // generic body: `function addone(x: T) = x + 1.0`. The
    // promotion rules answered the scalar (`_, Float64 ->
    // Float64`) and left `T` open, so the deduced signature
    // read `(x: T) -> Float64` while the body, monomorphized
    // at an array instance, built an array: `addone([1.0,
    // 2.0])` checked clean and g++ refused `return` of an
    // Array<double,1> as a double (the same for a complex
    // instance), and the `-> T` spelling bound `T` to
    // Float64 and refused the array outright. `T` admits
    // arrays (a scalar broadcasts against one, formalism
    // 2.5's elementwise lifting), and for every element class
    // the partner promotes INTO -- Float64, complex, and
    // Float32 beside a literal -- the result is exactly `T`.
    // So answer `T`, carrying the op's unit, and record the
    // demand: an instance whose element the partner would
    // promote AWAY (an integer, or Float32 beside a typed
    // Float64) is refused at its call
    // (TypeCheckSupport.judgeGenericObligations), where the
    // T-typed result would otherwise truncate; the caller
    // converts the argument instead (`Float64(x)`, formalism
    // 2.4 -- there is no promotion type). Only a bare PARAMETER
    // variable (see sigTop; `x: T` and `x: T^0` are one
    // variable, the arity-liftable scalar): not a caret array
    // var (`T^1` is shaped against the partner above), not a
    // var a call already proved an array (the knownArrayVar
    // arm below), and not a lambda's own parameter (not a
    // signature variable; apply-site unification types it).
    let genericBesideReal =
        let isArithOp =
            match op with
            | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret -> true
            | _ -> false
        if mode <> Elementwise || not isArithOp || env.InLambdaBody
           || env.CurrentGenericObligations.IsNone then None
        else
            // Only a variable a PARAMETER is declared as,
            // bare (`x: T` = `x: T^0`, `x: T<u>`): the one kind
            // of variable an array can instantiate (the arity
            // lift). An array's ELEMENT var (the
            // `reduce(row, (+))` of a `row: T^1`) and an arrow
            // application's result (functions/207: one value)
            // are scalars whatever the instance, and keep the
            // promotion rules' scalar answer -- an Int64
            // element plus 1.0 is a Float64 there.
            //
            // A PARAMETER's: CurrentSignature ends with the
            // return type, and a variable only the RETURN
            // names (`-> T^0` beside `a: Poly<T^1>`) is no
            // parameter's type. Reading it as one typed the
            // recursive `mean(head) * moment(tail)` as the
            // return variable itself, which the base arm's
            // `1.0` then never pinned in the pack instances
            // (BL6001 unresolved type variable in the caller).
            let sigTop (vid: int) =
                let sigTys =
                    env.CurrentSignature
                    |> List.map (fun t -> env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits)
                let paramTys =
                    if sigTys.IsEmpty then [] else List.take (sigTys.Length - 1) sigTys
                let isTop t = match t with IRTInfer r -> r = vid | _ -> false
                List.exists isTop paramTys
                && sigTys |> List.forall (fun t ->
                    isTop t || not ((freeInferVars env.Subst t).Contains vid))
            let sigVar (literalPartner: bool) (t: IRType) =
                match env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits with
                | IRTInfer vid when env.Subst.IsPolymorphicId vid
                                    && (env.Subst.GetArityConstraint vid).IsNone
                                    && (match env.Subst.GetRankLowerBound vid with
                                        | Some k -> k < 1
                                        | None -> true)
                                    // ...or an element cast of one
                                    // (`Float64(x) * 0.5`): the
                                    // same open shape, its element
                                    // the cast's target.
                                    && (sigTop vid || (env.Subst.GetElemCast vid).IsSome || literalPartner) -> Some vid
                | _ -> None
            let realScalar (te: TypedExpr) =
                match IR.stripUnits (env.Subst.Resolve te.Type) with
                // An integer partner too (`x + 1`): the result is
                // `T` whatever the partner, and the mixing rule
                // judges each instance (an integer literal beside
                // a Float64 instance is refused at the call).
                | IRTScalar (ETFloat64 | ETFloat32 as et) -> Some et
                // (not `^`: its integer exponent is legal with
                // any base -- powpoly's GOPowExponent judges it)
                | IRTScalar (ETInt64 | ETInt32 as et) when op <> OpCaret -> Some et
                | _ -> None
            let isFloatLit (te: TypedExpr) = isNumericLiteralOperand te
            let scalarResult (vid: int) () =
                match IR.getUnits resTy0 with
                | Some u -> IRTUnitAnnotated (IRTInfer vid, u)
                | None -> IRTInfer vid
            // A literal also follows an open scalar element or arrow-result
            // variable: first(c) = c(0) + 1.0 keeps the cell width.
            // A typed non-literal partner retains the fixed-width judgment.
            match sigVar (isFloatLit tR) lRes, realScalar tR, sigVar (isFloatLit tL) rRes, realScalar tL with
            | Some vid, Some et, _, _ -> Some (vid, et, isFloatLit tR)
            // `2.0 ^ n`: the EXPONENT never types `^` (formalism
            // 2.4) -- the result is the real base's; GOPowBase
            // judges the exponent each call makes.
            | _, _, Some _, Some _ when op = OpCaret -> None
            | _, _, Some vid, Some et -> Some (vid, et, isFloatLit tL)
            | _ -> None
    match genericBesideReal with
    | Some (vid, partner, literal) ->
        let opName =
            match op with
            | OpAdd -> "+" | OpSub -> "-" | OpMul -> "*" | OpDiv -> "/"
            | OpMod -> "%" | _ -> "^"
        env.CurrentGenericObligations
        |> Option.iter (fun acc -> acc.Add(GOPromotion (vid, opName, partner, literal)))
        match IR.getUnits resTy0 with
        | Some u -> IRTUnitAnnotated (IRTInfer vid, u)
        | None -> IRTInfer vid
    | None ->
    // G275 (2): a LITERAL-ONLY operand beside a still-OPEN plain
    // variable (a let-bound lambda's own parameter, typed before
    // its first call decides it) takes that variable's width
    // (formalism 2.4: the literal says the class, its partner
    // the width). adaptLiteralOperands keeps the literal at the
    // class's WIDE width as the representation (IRMono's
    // adaptFloatLiteralsModule narrows it -- or refuses a value
    // that does not fit, R19 -- once the partner is concrete),
    // but the RESULT is the partner variable, not the literal's
    // representation: answering the wide width fixed the
    // lambda's result at Float64 while a later call made its
    // parameter Float32 -- an implicit conversion of the body's
    // value. A partner of another class is the mixing rule's
    // (the post-zonk sweep refuses it).
    let literalBesideOpen =
        let isArithOp =
            match op with
            | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret -> true
            | _ -> false
        if (mode <> Elementwise && mode <> Outer) || not isArithOp then None
        else
            let openPlain (t: IRType) =
                match env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits with
                | IRTInfer v when not (env.Subst.IsPolymorphicId v)
                                  && (env.Subst.GetLiteralDefault v).IsNone
                                  && (env.Subst.GetElemCast v).IsNone
                                  && (env.Subst.GetElemHint v).IsNone
                                  && not (env.Subst.IsIndexDefault v)
                                  && (env.Subst.GetArityConstraint v).IsNone
                                  && (env.Subst.GetRankLowerBound v).IsNone
                                  // a LET-BOUND lambda value's own
                                  // parameter (a kernel lambda's is
                                  // decided at its apply site, and a
                                  // generic body's by its instance)
                                  && (env.LetLambdaParamVars |> Seq.exists (fun p ->
                                        match env.Subst.Resolve (IRTInfer p) |> IR.stripUnits with
                                        | IRTInfer r -> r = v
                                        | _ -> false)) -> Some v
                | _ -> None
            // An integer or float literal (a COMPLEX literal makes
            // a real partner's result complex: not this rule).
            let literalOnly (te: TypedExpr) =
                match literalOnlyElem env.Subst te with
                | Some le -> litClassOf le = 0 || litClassOf le = 1
                | None -> false
            match openPlain tL.Type, literalOnly tR, openPlain tR.Type, literalOnly tL with
            | Some v, true, _, _ -> Some v
            // (`^` is typed as its BASE: only an open base takes
            // the result; a literal base keeps its own width)
            | _, _, Some v, true when op <> OpCaret -> Some v
            | _ -> None
    match literalBesideOpen with
    | Some vid ->
        match IR.getUnits resTy0 with
        | Some u -> IRTUnitAnnotated (IRTInfer vid, u)
        | None -> IRTInfer vid
    | None ->
    if zeroBesideCaret then rRes
    elif mode = Elementwise && isZipOp && isScalarTy resTy0
       && ((knownArrayVar lRes && isScalarTy rRes) || (knownArrayVar rRes && isScalarTy lRes)) then
        let arrSide = if knownArrayVar lRes then lRes else rRes
        // A COMPARISON keeps the var's SHAPE but not its
        // element. Answering the var itself typed `head > 0.0`
        // as the pack element's own array (Array<double>), so
        // the Bool result was copied into -- and printed from
        // -- a double array (`[1, 0, 1]` for `[true, false,
        // true]`); and beside a base arm that answered the Bool
        // array correctly, the two arms unified and re-typed
        // `head` itself as a Bool array (a false BL3009 on its
        // own `mean(head)`). Answer the Bool array the caret arm
        // above mints, at the rank the call judgment recorded.
        // A caret var never reaches here (deferredBoolRank took
        // it), so this is a var with only a rank LOWER BOUND,
        // and the bound is PINNED as the result's rank -- the
        // only rank known here. An instance of higher rank then
        // disagrees with this declared rank in the generated
        // code, and a static `rank(...)` of the comparison
        // folds to the bound.
        if isCmpOrLogical then
            match IR.stripUnits arrSide with
            | IRTInfer vid ->
                (match env.Subst.GetRankLowerBound vid with
                 | Some k when k >= 1 -> abstractBoolArray k
                 | _ -> resTy0)
            | _ -> resTy0
        else arrSide
    // The comparison twin of the reshape repair below: a REAL
    // array beside an unresolved var (a pack `head` an
    // intrinsic already materialized, compared with its own
    // `reduce` -- whose type is still the open element var).
    // The promotion rules answered the scalar Bool, so the
    // function returned `bool` while its body built an
    // Array<bool>. Whether the var resolves to a scalar
    // (broadcast) or an array (zip, which needs equal shapes),
    // the result is the array's shape with Bool elements.
    elif mode = Elementwise && isCmpOrLogical && IR.stripUnits resTy0 = IRTScalar ETBool
         && (match lRes, rRes with
             | ArrayElem _, other | other, ArrayElem _ -> unboundVar other
             | _ -> false) then
        match lRes, rRes with
        | ArrayElem arr, _ | _, ArrayElem arr -> mkArrayLike { arr with ElemType = IRTScalar ETBool }
        | _ -> resTy0
    elif mode <> Elementwise || not isZipOp || not (unboundVar resTy0) then resTy0
    else
        match lRes, rRes with
        | ArrayElem arr, other when unboundVar other -> reshape arr
        | other, ArrayElem arr when unboundVar other -> reshape arr
        | _ -> resTy0

/// Unit rules for one binary op, shared by scalar arithmetic
/// (inferArithType) and the array kernel-synthesis paths in inferBinOp
/// (both-array zip, array<->scalar broadcast). The synthesized kernels
/// infer their bodies against unresolved parameter types -- the params
/// only unify with the element types later, in buildApplyInfo -- so the
/// unit judgment must happen at the synthesis site, where the operand
/// units are still visible. Returns the RESULT unit signature (None =
/// no annotation): +/-/comparison require agreement, * and / compose
/// signatures, `^` scales them by its exponent (unitRulesForCaret -- it
/// needs the exponent's VALUE, so this signature can only reject), and
/// everything else (&&, ||, ...) drops units.
and unitRulesForOp (op: BinOp) (lUnits: UnitSig option) (rUnits: UnitSig option) : TypeResult<UnitSig option> =
    match op with
    | OpAdd | OpSub ->
        match lUnits, rUnits with
        | Some lu, Some ru ->
            // unitCompatible also demands NOMINAL agreement (same quantity,
            // or one side structural); unitJoin keeps whichever nominal is
            // present, so `speed + m/s-value` stays `speed`.
            if unitCompatible lu ru then Ok (Some (unitJoin lu ru))
            else Error (UnitMismatch ((if op = OpAdd then "addition" else "subtraction"), ppUnitSig lu, ppUnitSig ru))
        | Some u, None | None, Some u -> Ok (Some u)
        | None, None -> Ok None
    | OpMul ->
        // Multiplicative composition DROPS the nominal layer (a quantity is
        // an identity, not a factor): the one-sided arms strip it explicitly
        // since the sig passes through unitMul only when both sides carry one.
        match lUnits, rUnits with
        | Some lu, Some ru -> Ok (Some (unitMul lu ru))
        | Some u, None | None, Some u -> Ok (Some { u with Nominal = None })
        | None, None -> Ok None
    | OpDiv | OpMod ->
        match lUnits, rUnits with
        | Some lu, Some ru -> Ok (Some (unitDiv lu ru))
        | Some u, None -> Ok (Some { u with Nominal = None })
        | None, Some u -> Ok (Some (unitDiv unitDimensionless u))
        | None, None -> Ok None
    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
        match lUnits, rUnits with
        | Some lu, Some ru when not (unitCompatible lu ru) ->
            Error (UnitMismatch ("comparison", ppUnitSig lu, ppUnitSig ru))
        | _ -> Ok None
    // atan2(y, x) is the angle of the point (x, y): the operands enter only as
    // the RATIO y/x, so any shared signature cancels and the result is always
    // dimensionless -- `atan2(dy: m, dx: m)` is a perfectly good slope angle.
    // Two rejections, and both must live HERE rather than in the array-op
    // guard, because unlike `+` there is no rewrite site that could insert a
    // conversion factor into an intrinsic call:
    //   - incompatible DIMENSIONS (atan2(1 m, 1 s)): the ratio is not a number.
    //   - compatible dimensions at different SCALE (atan2(1 day, 86400 s)):
    //     the ratio is 1/86400 or 1 depending on a factor nobody applied.
    // A one-sided signature is accepted, mirroring `+`: an operand with no
    // annotation makes no claim.
    | OpMath2 "atan2" ->
        (match lUnits, rUnits with
         | Some lu, Some ru ->
             if not (unitCompatible lu ru) then
                 Error (UnitMismatch ("atan2(y, x) arguments (both must carry the same unit -- the intrinsic sees only the ratio y/x)",
                                      ppUnitSig lu, ppUnitSig ru))
             elif not (unitSameScale lu ru) then
                 requireSameScale "atan2(y, x) arguments" lu ru |> Result.map (fun () -> None)
             else Ok None
         | _ -> Ok None)
    // log_base(x, b) = log x / log b: two transcendentals, so BOTH operands are
    // dimensionless-only for exactly the reason unitRulesForUnaryOp gives for
    // `log`. Result is dimensionless.
    | OpMath2 _ ->
        let dimensioned u =
            match u with
            | Some s when not (Map.isEmpty (unitNormalize s).Dims) -> Some s
            | _ -> None
        (match dimensioned lUnits, dimensioned rUnits with
         | Some s, _ | _, Some s ->
             Error (UnitMismatch ("log_base(x, b) argument (a logarithm sums powers of its argument, so it is defined only on dimensionless values; divide by a reference quantity first)",
                                  "dimensionless", ppUnitSig s))
         | None, None -> Ok None)
    // `^` needs the exponent VALUE, which this signature does not carry.
    // Reaching the exponent-free form with a dimensioned base is therefore a
    // rejection, not a silent drop: a call site that can see the right
    // operand routes through unitRulesForOpWith instead and gets the real
    // answer. Erring loudly here keeps a future call site from
    // reintroducing the bug where `^` fell into the catch-all below, so
    // `d ^ 2` over meters typed as a bare Float.
    | OpCaret -> unitRulesForCaret lUnits rUnits None
    | _ -> Ok None

/// The static integer exponent of a `^` right operand, when it has one.
/// Only an integer-valued literal gives the result a unit signature:
/// `d ^ 2` over meters is meters^2, whereas `d ^ n` for a runtime n has a
/// grade that is not known until the value is. `x ^ 2.0` counts (same power
/// as `x ^ 2`); `x ^ 0.5` does not, since meters^(1/2) has no representation
/// in the integer-exponent grammar UnitSig uses. The bound keeps unitPow's
/// exponent multiplication away from overflow -- past it the answer is "no
/// static exponent", which the caller treats as a rejection, so the cap
/// cannot silently drop a signature.
and staticPowExponent (e: TypedExpr) : int option =
    match e.Kind with
    | TExprLit (LitInt n) when n >= -1024L && n <= 1024L -> Some (int n)
    | TExprLit (LitFloat f) when System.Double.IsFinite f && f = floor f && abs f <= 1024.0 ->
        Some (int f)
    | TExprUnaryOp (OpNeg, inner) -> staticPowExponent inner |> Option.map (fun n -> -n)
    | _ -> None

/// Unit rule for `^`. Split out of unitRulesForOp because it is the one op
/// whose result signature depends on the right operand's VALUE rather than
/// its signature: meters ^ 2 is meters^2, which is exactly unitPow -- a
/// function that until now was reachable only from the `Unit area =
/// meters^2` DECLARATION grammar, never from an expression.
///
/// Two rejections. A dimensioned EXPONENT is meaningless at any base
/// (`x ^ (2.0 : Float<seconds>)`). A dimensioned BASE with no static integer
/// exponent is rejected rather than silently stripped, because the result's
/// grade genuinely depends on a runtime value and no signature describes it.
/// A dimensionless base is unconstrained either way, so `x ^ y` over plain
/// Floats -- including the fractional and variable exponents the AD corpus
/// leans on -- is untouched.
and unitRulesForCaret (lUnits: UnitSig option) (rUnits: UnitSig option) (powExp: int option) : TypeResult<UnitSig option> =
    let dimensioned u =
        match u with
        | Some s when not (Map.isEmpty (unitNormalize s).Dims) -> Some s
        | _ -> None
    match dimensioned rUnits with
    | Some ru ->
        Error (UnitMismatch ("exponent of ^ (an exponent must be dimensionless)", "dimensionless", ppUnitSig ru))
    | None ->
        match dimensioned lUnits, powExp with
        | None, _ -> Ok None
        | Some lu, Some n -> Ok (Some (unitPow lu n))
        | Some lu, None ->
            // Phrased as expected-vs-found on the WHOLE power, since the
            // mismatch is not between two signatures but between a signature
            // and the absence of one.
            Error (UnitMismatch (
                        "^ (a dimensioned base needs a compile-time integer exponent -- the grade of the result depends on its value)",
                        $"{ppUnitSig lu} ^ <integer literal>",
                        $"{ppUnitSig lu} ^ <value known only at run time>"))

/// unitRulesForOp for call sites that hold the right operand's TYPED expr.
/// Only `^` reads it; every other op ignores the extra argument. Sites that
/// genuinely cannot see the operand (the comparison-only path) keep calling
/// unitRulesForOp, whose `^` arm rejects a dimensioned base rather than
/// answering from incomplete information.
and unitRulesForOpWith (op: BinOp) (lUnits: UnitSig option) (rUnits: UnitSig option) (rExpr: TypedExpr option) : TypeResult<UnitSig option> =
    match op with
    | OpCaret -> unitRulesForCaret lUnits rUnits (rExpr |> Option.bind staticPowExponent)
    | _ -> unitRulesForOp op lUnits rUnits

/// Unit rules for the ARRAY elementwise synthesis sites (zip and
/// array<->scalar). Identical to unitRulesForOpWith plus a magnitude guard:
/// those sites annotate the synthesized kernel's params with the element
/// type STRIPPED of units, so the body's binop sees no signatures and the
/// scalar conversion seam cannot fire inside the lambda. An element
/// magnitude difference therefore has to reject here rather than reach
/// codegen as arithmetic on raw numbers.
and unitRulesForArrayOp (context: string) (op: BinOp) (lu: UnitSig option) (ru: UnitSig option) (rExpr: TypedExpr option) : TypeResult<UnitSig option> =
    let scaleGuard =
        match op, lu, ru with
        | (OpAdd | OpSub | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe), Some a, Some b
                when unitCompatible a b -> requireSameScale context a b
        | _ -> Ok ()
    scaleGuard |> Result.bind (fun () -> unitRulesForOpWith op lu ru rExpr)

/// Materialize the MAGNITUDE conversion a seam needs: multiply `t` by the
/// exact factor carrying its own signature into `dst`. Returns `t` untouched
/// when the magnitudes already agree, which is every program written before
/// scaled units existed.
///
/// The factor is ONE ratio (src.Scale / dst.Scale) computed exactly and only
/// then rounded, so `day -> hour` is 24.0 rather than 86400.0/3600.0
/// evaluated in floating point. A value therefore crosses at most a single
/// multiply and never round-trips through a canonical base -- which is what
/// keeps a `Float64<nanosecond>` from being inflated to seconds and back,
/// losing at both ends.
///
/// INTEGER operands convert only by an exact INTEGER factor. `hour -> second`
/// (x2600) is exact; `second -> hour` would truncate, so it rejects and says
/// so rather than quietly returning zeros.
/// A source signature that is NOT convertible to `dst` is a plain unit
/// mismatch and must be reported as one. This matters because the ascription
/// caller reaches here having checked the value against the annotation with
/// its unit STRIPPED -- so this is the only remaining place a dimension
/// clash at that seam can be caught, and letting it fall through would
/// silently retype `Float<seconds>` as `Float<meters>`.
and convertScaleTo (env: TypeEnv) (context: string) (dst: UnitSig) (t: TypedExpr) : TypeResult<TypedExpr> =
    let resolved = env.Subst.Resolve t.Type
    match IR.getUnits resolved with
    // A BARE value carries no claim and adopts the target unit freely --
    // the literal ergonomics every existing program relies on.
    | None -> Ok t
    | Some src when not (unitCompatible src dst) ->
        Error (UnitMismatch (context, ppUnitSig dst, ppUnitSig src))
    // Inside a synthesized derivative (Constraints.adBodyConjunct) a factor
    // may not be inserted: the AD transform differentiated this site as
    // identity / linear, so a conversion by k would have derivative 1
    // instead of k. Refuse loudly; the user converts outside the
    // differentiated function.
    | Some src when unitCompatible src dst && not (unitSameScale src dst)
                    && Blade.Constraints.inAdBody () ->
        Error (UnitMagnitudeErr (sprintf
                "%s relates %s and %s inside a differentiated body (a function synthesized by ad.grad / ad.jvp): the magnitudes differ by the factor %s, and the derivative of that conversion is the factor itself, which the surface-level AD transform does not see. Convert outside the differentiated function, or declare its parameters and result in one magnitude"
                context (ppUnitSig src) (ppUnitSig dst) (ppUnitScale (unitConversionFactor src dst))))
    | Some src when unitCompatible src dst && not (unitSameScale src dst) ->
        let factor = unitConversionFactor src dst
        let scaled et lit =
            Ok (mkTyped (TExprBinOp (Elementwise, OpMul, t, mkTyped (TExprLit lit) (IRTScalar et)))
                        (IRTUnitAnnotated (IRTScalar et, dst)))
        match IR.stripUnits resolved with
        | IRTScalar ((ETInt32 | ETInt64) as et) ->
            let exact =
                factor.Den.IsOne && Map.isEmpty factor.Consts
                && abs factor.Num <= bigint System.Int64.MaxValue
            if exact then scaled et (LitInt (int64 factor.Num))
            else
                Error (UnitMagnitudeErr (sprintf
                        "converting %s to %s in %s would scale an integer by %s, which is not a whole number; use a float element type, or annotate the result as %s"
                        (ppUnitSig src) (ppUnitSig dst) context (ppUnitScale factor) (ppUnitSig src)))
        | IRTScalar ((ETFloat32 | ETFloat64) as et) -> scaled et (LitFloat (scaleToFloat factor))
        | other ->
            Error (UnitMagnitudeErr (sprintf
                    "%s relates %s and %s, which differ by the factor %s, but a %s value cannot carry a unit conversion"
                    context (ppUnitSig src) (ppUnitSig dst) (ppUnitScale factor) (IRPrint.ppIRType other)))
    | _ -> Ok t

/// Reject a magnitude difference at a seam that does NOT yet insert a
/// conversion (array elementwise, argument passing, ascription, folds).
/// unitCompatible is scale-blind on purpose -- it answers "convertible?" --
/// so without this guard those seams would accept `day` where `second` is
/// wanted and compute on the raw numbers. Loud beats silent.
and requireSameScale (context: string) (expected: UnitSig) (actual: UnitSig) : TypeResult<unit> =
    if unitSameScale expected actual then Ok ()
    else
        Error (UnitMagnitudeErr (sprintf
                "%s relates %s and %s: same dimensions, but magnitudes differing by the factor %s. Blade inserts a unit conversion only in scalar +, -, and comparisons; here the operands must already share a magnitude"
                context (ppUnitSig expected) (ppUnitSig actual)
                (ppUnitScale (unitConversionFactor actual expected))))

/// Overwrite the ELEMENT unit annotation of an array-typed result from a
/// synthesized kernel pipeline with the signature the unit rules computed.
/// Without this the kernel return type leaks the LEFT operand's unit
/// through * and / (meters * meters would stay meters). None strips --
/// comparisons produce Bool elements, and `^` over a dimensionless base
/// has no signature to stamp (a dimensioned base either composes through
/// unitPow or rejects; see unitRulesForCaret).
and stampElemUnits env (resUnits: UnitSig option) (t: TypedExpr) : TypedExpr =
    match env.Subst.Resolve t.Type with
    | ArrayElem arr ->
        let bare = IR.stripUnits arr.ElemType
        let elem =
            match resUnits with
            | Some u -> IRTUnitAnnotated (bare, u)
            | None -> bare
        { t with Type = mkArrayLike { arr with ElemType = elem } }
    | _ -> t

/// Unit rules for the unary and math-intrinsic ops, shared by
/// scalar-position intrinsic inference and the kernel-body walk. Split by
/// HOMOGENEITY, which decides whether a function of a dimensioned quantity
/// has a signature at all:
///   - degree 1 (f(cx)=c*f(x)): negation, abs, complex projections PRESERVE.
///   - degree 1/2: sqrt halves the dimension exponents AND the magnitude
///     together (sqrt(m^2) = m, sqrt(km^2) = km), and REFUSES when either
///     halving is inexact. An odd exponent would need a half-integer
///     dimension, and a non-square magnitude (`4047 * meters^2`) a rounded
///     scale; neither is representable. Refusing is not pedantry: the arm
///     used to answer "no signature" in the odd case, and an unconstrained
///     result unifies with ANY unit downstream, so `sqrt(m) + Float<seconds>`
///     typechecked.
///   - degree 0: arg, logical not are dimensionless-out for any operand.
///   - NOT homogeneous: floor/ceil and transcendentals REJECT a dimensioned
///     operand instead of inventing a result signature. floor is NOT lumped
///     with abs: rounding doesn't commute with scale (floor(3.7m)=3m, but
///     370cm floors to 3.7m). exp/log/sin/... are worse: their series add
///     powers of the argument, so a dimensioned argument is meaningless.
/// A dimensionless (or absent) signature is never a rejection -- keeps
/// `floor(x)`/`exp(x)` usable on ordinary numbers.
and unitRulesForUnaryOp (op: UnaryOp) (u: UnitSig option) : TypeResult<UnitSig option> =
    let dimensioned =
        match u with
        | Some s when not (Map.isEmpty (unitNormalize s).Dims) -> Some s
        | _ -> None
    let requireDimensionless (context: string) =
        match dimensioned with
        | Some s -> Error (UnitMismatch (context, "dimensionless", ppUnitSig s))
        | None -> Ok None
    match op with
    | OpNeg | OpConj | OpReal | OpImag -> Ok u
    | OpNot | OpArg -> Ok None
    | OpMath "abs" -> Ok u
    | OpMath "sqrt" ->
        // sqrt DROPS the nominal layer like the other non-degree-1
        // compositions (sqrt of a quantity is not that quantity), and is
        // EXACT-OR-REFUSE. unitSqrt halves the MAGNITUDE alongside the dims,
        // which is what makes sqrt the inverse of unitPow at n = 2: halving
        // dims alone stamped `sqrt(x: Float<km2>)` as meters-magnitude and
        // every later conversion was off by 1000.
        match u with
        | None -> Ok None
        | Some s ->
            match unitSqrt s with
            | Some r -> Ok (Some r)
            | None ->
                // Two distinct refusals, both phrased as "what the argument
                // would have to be" like the floor/transcendental arms below.
                let n = unitNormalize s
                let context, expected =
                    if n.Dims |> Map.forall (fun _ ex -> ex % 2 = 0) then
                        ($"sqrt() argument ({(ppUnitSig s)} has magnitude {(ppUnitScale n.Scale)}, which has no exact square root in the unit scale grammar -- declare the unit as a square instead, e.g. `Unit hectare = (100 * meters) ^ 2`)"),
                        "a unit whose magnitude is an exact square"
                    else
                        ($"sqrt() argument (the square root of {(ppUnitSig s)} is ({(ppUnitSig s)})^(1/2), and unit dimension exponents are integers, so that signature cannot be expressed -- square the argument, or divide by a reference quantity to get a dimensionless value first)"),
                        "a unit whose dimension exponents are all even"
                Error (UnitMismatch (context, expected, ppUnitSig s))
    | OpMath (("floor" | "ceil") as name) ->
        requireDimensionless
            $"{name}() argument (rounding is not scale-invariant: floor(3.7 m) = 3 m, but the same length as 370 cm floors to 370 cm = 3.7 m; divide by a reference quantity to get a dimensionless count first)"
    | OpCast _ ->
        // A numeric cast changes representation width, not the quantity:
        // units ride through unchanged. (float->int casts only exist
        // through floor/ceil, which already required a dimensionless
        // operand, so the int side composes.)
        Ok u
    | OpMath name ->
        requireDimensionless
            $"{name}() argument (a transcendental sums powers of its argument, so it is defined only on dimensionless values; divide by a reference quantity first)"

/// Unit-only second pass over a KERNEL BODY, run by buildApplyInfo after
/// parameter unification has bound the parameter types. The body was
/// type-inferred while its params were still unresolved inference
/// variables, so the cached node types carry no unit information (or a
/// leaked left-operand annotation) -- this walk recomputes the signature
/// bottom-up with the same per-op table the scalar path uses
/// (unitRulesForOp), so `t * t` over meters elements comes out meters^2
/// and a mismatched `a + b` over a hand-written zip REJECTS. `bound`
/// carries walk-computed signatures for kernel-local lets (their cached
/// var types are as stale as the intermediate nodes). Constructs the walk
/// doesn't model return None (no claim) -- only op rules error.
and kernelBodyUnits (env: TypeEnv) (bound: Map<IRId, UnitSig option>) (e: TypedExpr) : TypeResult<UnitSig option> =
    let combineBranches context (a: UnitSig option) (b: UnitSig option) =
        match a, b with
        | Some ua, Some ub when (not (unitCompatible ua ub) || not (unitSameScale ua ub))
                                && recordProbeEquality ua ub ->
            // Probe run: the branches must agree for the CALL's units.
            Ok (Some ua)
        | Some ua, Some ub ->
            if not (unitCompatible ua ub) then Error (UnitMismatch (context, ppUnitSig ua, ppUnitSig ub))
            // This walk RECOMPUTES signatures over an already-typed kernel
            // body; it rewrites nothing, so a magnitude difference between
            // branches has no place to put a factor.
            elif not (unitSameScale ua ub) then requireSameScale context ua ub |> Result.map (fun () -> None)
            else Ok (Some (unitJoin ua ub))
        | Some u, None | None, Some u -> Ok (Some u)
        | None, None -> Ok None
    let ofType (t: IRType) = IR.getUnits (env.Subst.Resolve t)
    let elemOfType (t: IRType) =
        match env.Subst.Resolve t with
        | ArrayElem at -> IR.getUnits at.ElemType
        | resolved -> IR.getUnits resolved
    // Walk a subtree only for its ERRORS (mismatched ops inside call args,
    // assignment right-hand sides, conditions), discarding the signature.
    let errorsOnly sub = kernelBodyUnits env bound sub |> Result.map ignore
    match e.Kind with
    | TExprLit _ | TExprComplexLit _ -> Ok None
    | TExprFma (a, b, c) ->
        // Dimensionless by construction (inference checks all three against
        // plain Float64); walk the operands for their own errors only.
        errorsOnly a |> Result.bind (fun () -> errorsOnly b)
                     |> Result.bind (fun () -> errorsOnly c)
                     |> Result.map (fun () -> None)
    | TExprVar (_, varId, _) ->
        match Map.tryFind varId bound with
        | Some u -> Ok u
        // ELEMENT-aware, like the TExprIndex / TExprReduce arms below: an
        // ARRAY-typed var carries no TOP-level annotation (`getUnits` only
        // reads IRTUnitAnnotated), so `ofType` reported None for one and a
        // kernel-body product involving it silently kept only the OTHER
        // operand's signature -- `let wt = w * ts` computed 1/day instead of
        // 1/day * day = dimensionless, and a nested `sin <@> wt` then
        // rejected on the param's own unit. A kernel body reads an array
        // elementwise, so the element signature is the right claim.
        | None -> Ok (elemOfType e.Type)
    | TExprBinOp (_, op, l, r) ->
        kernelBodyUnits env bound l |> Result.bind (fun lu ->
        kernelBodyUnits env bound r |> Result.bind (fun ru ->
        match unitRulesForOpWith op lu ru (Some r), op, lu, ru with
        // Probe run (funcUnitTransform): an equality op between two
        // probe-dependent signatures is an EQUATION the call must satisfy,
        // not a failure of the body -- record it and carry on.
        | Error _, (OpAdd | OpSub | OpMod), Some a, Some b when recordProbeEquality a b -> Ok (Some a)
        | Error _, (OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe | OpMath2 "atan2"), Some a, Some b
            when recordProbeEquality a b -> Ok None
        | res, _, _, _ -> res))
    | TExprUnaryOp (op, inner) ->
        kernelBodyUnits env bound inner |> Result.bind (fun u ->
            match unitRulesForUnaryOp op u, op, u with
            // A transcendental of a probe-dependent value: the argument must
            // be dimensionless at the call.
            | Error _, OpMath name, Some s when name <> "sqrt" && recordProbeEquality s unitDimensionless -> Ok None
            | res, _, _ -> res)
    | TExprIf (cond, thenBr, elseBr) ->
        errorsOnly cond |> Result.bind (fun () ->
        kernelBodyUnits env bound thenBr |> Result.bind (fun tu ->
        kernelBodyUnits env bound elseBr |> Result.bind (fun eu ->
        combineBranches "conditional branches" tu eu)))
    | TExprLet (_, varId, value, body) ->
        kernelBodyUnits env bound value |> Result.bind (fun vu ->
        kernelBodyUnits env (Map.add varId vu bound) body)
    | TExprBlock (stmts, finalOpt) ->
        let foldStmt acc stmt =
            acc |> Result.bind (fun (b: Map<IRId, UnitSig option>) ->
                match stmt with
                | TStmtLet binding ->
                    kernelBodyUnits env b binding.Value
                    |> Result.map (fun vu -> Map.add binding.VarId vu b)
                | TStmtAssign (_, rhs) ->
                    kernelBodyUnits env b rhs |> Result.map (fun _ -> b)
                | TStmtExpr sub ->
                    kernelBodyUnits env b sub |> Result.map (fun _ -> b)
                | TStmtForIn _ -> Ok b)
        stmts |> List.fold foldStmt (Ok bound) |> Result.bind (fun b ->
            match finalOpt with
            | Some f -> kernelBodyUnits env b f
            | None -> Ok None)
    // A CALL composes: the callee's own recorded unit transform, applied to the
    // signatures this walk computes for the arguments. Without it every call
    // was a dead end ("no claim"), which is why `sqrt(variance(row))` could say
    // nothing about its result even though both halves are known. Anything
    // unrecorded still returns None, so this only ever replaces silence.
    | TExprApp (f, args) ->
        args |> List.fold (fun acc a ->
            acc |> Result.bind (fun () -> errorsOnly a)) (Ok ())
        |> Result.bind (fun () ->
            // The callee's own recorded EQUALITIES, at the units this walk
            // computes for the arguments: a generic calling a generic
            // composes them (recorded again when still probe-dependent), and
            // a kernel body calling one with concrete units is judged here.
            let equalities =
                match f.Kind with
                | TExprVar (n, _, _) ->
                    (match calleeDeclId env f |> Option.map (fun fid -> env.FuncUnitEqualities.TryGetValue fid) |> Option.defaultValue (false, []) with
                     | true, eqs when not eqs.IsEmpty -> Some (n, eqs)
                     | _ -> None)
                | _ -> None
            let equalityCheck =
                match equalities with
                | None -> Ok ()
                | Some (n, eqs) ->
                    args
                    |> List.fold (fun acc a ->
                        acc |> Result.bind (fun (us: UnitSig option list) ->
                            kernelBodyUnits env bound a |> Result.map (fun u -> u :: us)))
                        (Ok [])
                    |> Result.bind (fun rev ->
                        let argUnits = List.rev rev
                        eqs
                        |> List.fold (fun acc (x, y) ->
                            acc |> Result.bind (fun () ->
                                match instantiateProbeSig x argUnits, instantiateProbeSig y argUnits with
                                | Some a, Some b when not (unitCompatible a b) || not (unitSameScale a b) ->
                                    if recordProbeEquality a b then Ok ()
                                    else Error (UnitMismatch ($"the arguments of '{n}', whose body combines them as one quantity", ppUnitSig a, ppUnitSig b))
                                | _ -> Ok ()))
                            (Ok ()))
            equalityCheck |> Result.bind (fun () ->
            match f.Kind with
            | TExprVar _ ->
                (match calleeDeclId env f |> Option.bind (lookupUnitTransform env) with
                 | None -> Ok None
                 | Some (exponents, residual) ->
                     // The argument signatures this walk computes, not the ones
                     // their TYPES carry: inside a body an argument is very
                     // often a parameter, whose unit lives in `bound`.
                     args
                     |> List.fold (fun acc a ->
                         acc |> Result.bind (fun (us: UnitSig option list) ->
                             kernelBodyUnits env bound a |> Result.map (fun u -> u :: us)))
                         (Ok [])
                     |> Result.map (fun rev ->
                         applyUnitTransform exponents residual (List.rev rev)))
            | _ -> Ok None))
    // ELEMENT-aware like the TExprVar arm: an array PARAMETER carries its unit
    // in `bound`, not on its type, so reading only the type reported "no claim"
    // for `reduce(row, (+))` -- the whole body of `mean`. Falling back to the
    // walk covers both that (a var head resolves through `bound`) and an
    // operand that is itself an expression (`reduce(centered * centered, (+))`,
    // the body of `variance`, whose element unit is the product's).
    | TExprIndex (arr, _, _) ->
        (match elemOfType arr.Type with
         | Some u -> Ok (Some u)
         | None -> kernelBodyUnits env bound arr)
    | TExprReduce (arr, _, _) ->
        (match elemOfType arr.Type with
         | Some u -> Ok (Some u)
         | None -> kernelBodyUnits env bound arr)
    | TExprField _ | TExprTupleIndex _ -> Ok (ofType e.Type)
    // NESTED ELEMENTWISE MAP inside a kernel body (`lambda(w) -> { let e =
    // exp <@> (i * w * ts); ... }`). Without an arm here this fell to the
    // no-claim catch-all, which is why the nested apply's OWN unit check had
    // to reject eagerly -- and it ran during the OUTER lambda's body
    // inference, while `w` was still an unresolved infer var contributing no
    // units, so it judged a PROVISIONAL element signature (exactly the
    // dimensioned capture's own, `day`) and rejected a product that cancels.
    // That inner check now defers (buildApplyInfo); this arm is the recheck
    // that keeps the deferral from becoming a false acceptance. It is also
    // strictly more informative than the old catch-all: an array-typed
    // operand carries no TOP-level unit annotation, so the plain scalar walk
    // read every nested map as "no claim".
    | TExprApply info -> nestedApplyElemUnits env bound info
    // `compute` is unit-transparent: it forces a computation to a value.
    | TExprCompute inner -> kernelBodyUnits env bound inner
    | _ -> Ok None

/// Derive, ONCE per declaration, how a function builds its return's unit out of
/// its arguments' -- the record `unitStampedReturn` consumes at every call site.
///
/// PROBE, don't guess. The body is walked with each GENERIC parameter seeded to
/// its own synthetic base dimension (`unitProbeBase`), so the exponent that
/// dimension carries out of the body is exactly the power the body applies to
/// that argument: `mean` returns `probe0^1`, `variance` returns `probe0^2`, and
/// `covariance(a, b)` returns `probe0 * probe1`. Concrete parameters are seeded
/// with the unit they actually declare instead, so they land in the RESIDUAL
/// (`residual * PROD argUnit_i ^ e_i`) rather than being invented as free
/// variables. This is the whole reason the fix is not "propagate the argument's
/// unit": three of those four answers are not the argument's unit.
///
/// A DERIVATION, NEVER A CHECK. `Ok None` (a construct the walk does not model)
/// and `Error` alike record nothing, leaving the call site silent. An `Error`
/// here is very often the probe's own fault rather than the program's -- a body
/// calling `exp` on a value that is dimensionless in every real call is
/// dimensioned under the probe, and `unitRulesForUnaryOp` rightly rejects that
/// -- so surfacing it would reject correct programs. The body's genuine unit
/// errors belong to the ordinary inference pass, which has already run.
///
/// Only a DEDUCED return is probed: a concrete return type either carries its
/// own signature or legitimately has none, and either way the call site leaves
/// it alone.
and funcUnitTransform (env: TypeEnv) (funcId: IRId) (unannotatedReturn: bool) (parms: TypedParam list)
                      (retTy: IRType) (body: TypedExpr) : unit =
    let openElem (t: IRType) =
        match env.Subst.Resolve t with
        | ArrayElem at -> (env.Subst.Resolve at.ElemType).IsIRTInfer
        | IRTInfer _ -> true
        | _ -> false
    let elemUnits (t: IRType) =
        match env.Subst.Resolve t with
        | ArrayElem at -> IR.getUnits (env.Subst.Resolve at.ElemType)
        | r -> IR.getUnits r
    // Probe whenever some parameter is generic: the return transform needs a
    // deduced return, but the EQUALITIES (FuncUnitEqualities) are demands on
    // the arguments whatever the return type is.
    if not parms.IsEmpty && (parms |> List.exists (fun p -> openElem p.Type)) then
        let bound =
            parms
            |> List.mapi (fun i p ->
                (p.VarId,
                 if openElem p.Type then Some (unitOfDims (Map.ofList [ (unitProbeBase i, 1) ]))
                 else elemUnits p.Type))
            |> Map.ofList
        let collected = ResizeArray<UnitSig * UnitSig>()
        let slot = unitProbeEqualities.Value
        let saved = slot.Value
        slot.Value <- Some collected
        let walked =
            try kernelBodyUnits env bound body
            finally slot.Value <- saved
        if collected.Count > 0 then
            env.FuncUnitEqualities.[funcId] <- List.ofSeq collected
        // The TRANSFORM is derived for a deduced return as before. A walk that
        // recorded equations still yields one (`hyp(a, b) = sqrt(a*a + b*b)`
        // returns unit(a)): the call judges the equations before the
        // transform is applied, so a call that reaches the stamp satisfies them.
        match walked with
        // ...or an UNANNOTATED return the body made concrete through a generic
        // cast (`Float64(reduce(row, (+))) / Float64(extents(row))`, stats.mean):
        // the cast keeps its operand's unit, so the result still measures what
        // the arguments measure, and nothing written on the signature says
        // otherwise.
        | Ok (Some u) when openElem retTy || unannotatedReturn ->
            let n = unitNormalize u
            let exponents =
                parms |> List.mapi (fun i _ ->
                    Map.tryFind (unitProbeBase i) n.Dims |> Option.defaultValue 0)
            // Strip the probes back out; the SCALE stays (a body that divides by
            // a `km` constant really does change the caller's magnitude).
            let residual =
                { n with
                    Nominal = None
                    Dims = n.Dims |> Map.filter (fun k _ -> not (k.StartsWith "__unit_probe_")) }
            env.FuncUnitTransform.[funcId] <- (exponents, residual)
        | _ -> ()

/// ELEMENT-level sibling of `kernelBodyUnits`, for the array operands of a
/// nested map. The scalar walk answers about SCALAR positions and reads a
/// node's TOP-level annotation, which an array type never carries (`getUnits`
/// only sees `IRTUnitAnnotated`), so it reports None for every array operand.
/// This reads through to the ELEMENT signature instead, and the two are
/// mutually recursive because an operand can itself be a synthesized
/// elementwise map (`i * w * ts` re-synthesizes as a broadcast map whose
/// stamped element annotation is the provisional one we must not trust).
///
/// Note this cannot be selected by testing an operand's TYPE for `ArrayElem`:
/// inside a kernel body an array-valued expression is typed at its ELEMENT
/// type (`w * ts` is a scalar `Float64<1/day>` node, and a nested apply node
/// is typed `Float64`), so the array-ness is in the expression SHAPE, not the
/// type. The walk dispatches on shape for exactly that reason.
and nestedOperandElemUnits (env: TypeEnv) (bound: Map<IRId, UnitSig option>) (e: TypedExpr) : TypeResult<UnitSig option> =
    let elemOfType (t: IRType) =
        match env.Subst.Resolve t with
        | ArrayElem at -> IR.getUnits at.ElemType
        | resolved -> IR.getUnits resolved
    match e.Kind with
    | TExprCompute inner -> nestedOperandElemUnits env bound inner
    | TExprApply info -> nestedApplyElemUnits env bound info
    // RECOMPUTE rather than read the stamp: a synthesized elementwise binop
    // (`w * ts`) carries a first-pass element annotation computed while the
    // captured param was unresolved. Leaves stay elem-aware, so a scalar
    // operand contributes its own signature and an array its element's.
    | TExprBinOp (_, op, l, r) ->
        nestedOperandElemUnits env bound l |> Result.bind (fun lu ->
        nestedOperandElemUnits env bound r |> Result.bind (fun ru ->
        unitRulesForOpWith op lu ru (Some r)))
    | TExprUnaryOp (op, inner) ->
        nestedOperandElemUnits env bound inner |> Result.bind (unitRulesForUnaryOp op)
    | TExprVar (_, varId, _) ->
        match Map.tryFind varId bound with
        | Some u -> Ok u
        | None -> Ok (elemOfType e.Type)
    | _ -> Ok (elemOfType e.Type)

/// The element signature a nested `<@>` map PRODUCES: bind the kernel's params
/// to its operands' element signatures (recomputed, not read off the
/// provisional stamps) and walk the kernel body with the ordinary machinery.
/// For an eta-expanded unary intrinsic (`exp <@> A` becomes
/// `lambda(__k) -> exp(__k)`) this bottoms out in the same
/// `unitRulesForUnaryOp` application the scalar path uses, so the transcendental
/// rule is enforced once, at the point where the operand units are finally real.
/// Anything not modelled (non-lambda kernel, arity disagreement, co-iteration
/// index params) returns None -- no claim, exactly as before.
and nestedApplyElemUnits (env: TypeEnv) (bound: Map<IRId, UnitSig option>) (info: TypedApplyInfo) : TypeResult<UnitSig option> =
    let rec kernelLambda (k: TypedExpr) =
        match k.Kind with
        | TExprLambda li -> Some li
        | TExprReynolds (inner, _) -> kernelLambda inner
        | _ -> None
    let rec collect acc rest =
        match rest with
        | [] -> Ok (List.rev acc)
        | a :: tl ->
            nestedOperandElemUnits env bound a
            |> Result.bind (fun u -> collect (u :: acc) tl)
    collect [] info.Arrays
    |> Result.bind (fun inputUnits ->
        match kernelLambda info.Kernel with
        | Some li when li.Params.Length = inputUnits.Length ->
            let bound' =
                List.fold2 (fun m (p: TypedParam) u -> Map.add p.VarId u m) bound li.Params inputUnits
            kernelBodyUnits env bound' li.Body
        | _ -> Ok None)

/// `rExpr` is the right operand's typed expr, carried only so the `^` unit
/// rule can read its exponent VALUE (see unitRulesForCaret); every other op
/// ignores it, and `None` is a safe caller default everywhere except a `^`
/// over a dimensioned base, which then rejects rather than guessing.
and inferArithType (builder: IRBuilder) mode op leftTy rightTy (rExpr: TypedExpr option) : TypeResult<IRType> =
    // Result type for an OUTER (bracketed) op over two arrays, shared by
    // `boolResultTy` and `bareResult`. Careful NOT to spell this as
    // `mkArrayLike { arrL with ... }`, which would smuggle three of the LEFT
    // OPERAND's properties onto a value that is not the left operand:
    //   - Identity: `None`, matching `deduceOutputType` -- an outer product
    //     is a fresh array with no source-level name, so wearing arrL's
    //     would make two different arrays indistinguishable.
    //   - IsVirtual: `false` -- an outer product is materialized
    //     (`genObjectForApplication` allocates and fills it), so inheriting
    //     `true` would describe real storage as virtual.
    //   - Index-type Ids: refreshed, not copied -- otherwise an axis of the
    //     product and an axis of an operand compare equal by id (consumers:
    //     IR's compound/mask prefix check, Lowering's dim-name lookup).
    // The refresh does NOT remap intra-record back-references (DepIdx inner
    // extent formulas, `Dependencies`); safe since this emitter only handles
    // rank-1 plain-dense operands, so ragged/dependent operands can't reach it.
    let mkOuterResult (arrL: IRArrayType) (arrR: IRArrayType) (elemTy: IRType) : IRType =
        mkArrayLike
            { arrL with
                ElemType = elemTy
                IndexTypes =
                    (arrL.IndexTypes @ arrR.IndexTypes)
                    |> List.map (fun ix -> { ix with Id = builder.FreshId() })
                IsVirtual = false
                Identity = None }
    // Boolean/comparison ops over ARRAYS lift to a Bool-ELEMENT array; only
    // the SHAPE rule differs per mode. Lowering already synthesizes the
    // object_for with a Bool kernel, so only the RESULT TYPE needs to become
    // the array here. Outer mode is the CROSS product of the operands'
    // index spaces (`A [<] B` with |A|=5, |B|=3 is a 5x3 Bool array, matching
    // codegen's deduceOutputType). Without this arm it falls through to the
    // scalar `IRTScalar ETBool` arm -- a silent miscompile: the loop nest and
    // Array<bool,2> allocation are emitted correctly, but the IR type says
    // "scalar Bool", so genPrintStatements takes the scalar `cout << x` path
    // and Array's implicit pointer-promotion operator prints an ADDRESS,
    // with nothing downstream seeing the real values. Array<->scalar
    // broadcast in Outer mode has no cross axis, so it still degrades to the
    // scalar Bool arm. A FUNCTION not a value: mints fresh index ids only
    // when reached, so an eagerly-bound `let` wouldn't burn ids on every
    // arithmetic binop that never calls it.
    let boolResultTy () =
        match mode, IR.stripUnits leftTy, IR.stripUnits rightTy with
        | Elementwise, ArrayElem arrL, ArrayElem _ ->
            mkArrayLike { arrL with ElemType = IRTScalar ETBool }
        | Elementwise, ArrayElem arrL, _ ->
            // array <op> scalar broadcast (`A > 2.0`): result shape follows the array
            mkArrayLike { arrL with ElemType = IRTScalar ETBool }
        | Elementwise, _, ArrayElem arrR ->
            // scalar <op> array broadcast (`2.0 < A`): result shape follows the array
            mkArrayLike { arrR with ElemType = IRTScalar ETBool }
        | Outer, ArrayElem arrL, ArrayElem arrR ->
            // Outer (bracketed) comparison / logical over two arrays: index
            // spaces concatenate (left axes then right axes), elements are Bool.
            // Mirrors the arithmetic Outer rule in `bareResult` below, which
            // keeps the left operand's ElemType instead.
            mkOuterResult arrL arrR (IRTScalar ETBool)
        | _ -> IRTScalar ETBool
    // Comparison over TUPLE operands. `std::tuple` supplies `==`/`!=`
    // (element-wise) and the ordered operators (lexicographic) for free, which
    // is what codegen emits and what the interpreter's tuple arm mirrors --
    // but only for components C++ compares the way Blade means them. Three
    // shapes are refused here, in the front end, rather than reaching g++ or
    // silently answering wrong:
    //
    //   - WIDTH disagreement. `(2, 3) == (2, 3, 4)` has no `operator==` at all
    //     and dies as a page of <tuple> template instantiation errors.
    //   - A tuple against a NON-tuple, for the same reason.
    //   - An ARRAY component. `Array` promotes to its data pointer, so
    //     `(x, 1) == (y, 1)` compares ADDRESSES: two structurally equal arrays
    //     answer `false` and the same array twice answers `true`. Comparing
    //     arrays at TOP level is elementwise and yields an ARRAY of Bool (see
    //     `boolResultTy`), and there is no scalar Bool for a tuple slot to
    //     hold -- so the component is refused rather than given a third meaning.
    //
    // Units are checked per COMPONENT, because `IR.getUnits` reads the top of
    // a type and a tuple carries none there: without this walk a `Float64<mps>`
    // slot compared against a `Float64<meters>` slot passed unremarked.
    // An unresolved operand makes no claim and is left to the ordinary
    // machinery, exactly as the scalar path leaves it.
    let rec peelAnnotations t =
        match t with
        | IRTUnitAnnotated (inner, _) -> peelAnnotations inner
        | _ -> t
    let isTupleTy t = (peelAnnotations t).IsIRTTuple
    let rec tupleCompareCheck (l: IRType) (r: IRType) : TypeResult<unit> =
        match peelAnnotations l, peelAnnotations r with
        | IRTInfer _, _ | _, IRTInfer _ -> Ok ()
        | IRTTuple ls, IRTTuple rs when ls.Length = rs.Length ->
            List.zip ls rs
            |> List.fold (fun acc (a, b) -> acc |> Result.bind (fun () -> tupleCompareCheck a b)) (Ok ())
        | IRTTuple _, _ | _, IRTTuple _ -> Error (TypeMismatch (l, r))
        | ArrayElem _, _ | _, ArrayElem _ ->
            Error (TypeMismatchErr "comparison of tuples whose components are arrays: an Array component \
compares as its data POINTER, not element-wise, so two structurally equal arrays would answer \
`false`. Compare the array components directly (`x == y` is elementwise and yields an array of \
Bool) and combine the results, or compare tuples of scalars.")
        | _ -> unitRulesForOp op (IR.getUnits l) (IR.getUnits r) |> Result.map ignore
    match op with
    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
        // Comparisons require compatible units (unitRulesForOp errors on
        // mismatch; the result carries no annotation). An OUTER comparison
        // compares ELEMENTS, whose units live on the element type (`getUnits`
        // of an array type is always None, so `a [<] b` over meters and
        // seconds was never judged).
        let lU, rU =
            match mode, IR.stripUnits leftTy, IR.stripUnits rightTy with
            | Outer, ArrayElem al, ArrayElem ar -> IR.getUnits al.ElemType, IR.getUnits ar.ElemType
            | _ -> IR.getUnits leftTy, IR.getUnits rightTy
        (if isTupleTy leftTy || isTupleTy rightTy then tupleCompareCheck leftTy rightTy else Ok ())
        |> Result.bind (fun () ->
            unitRulesForOp op lU rU
            |> Result.map (fun _ -> boolResultTy ()))
    | OpAnd | OpOr -> Ok (boolResultTy ())
    | _ ->
        // Extract unit annotations if present
        let lUnits = IR.getUnits leftTy
        let rUnits = IR.getUnits rightTy
        // A WILDCARD-tagged operand is arithmetic-transparent. The ban below
        // exists because an index type is a nominal label for a particular
        // space, and the escape hatch it points at -- "value-level position
        // arithmetic is reachable via virtual array iteration, which produces
        // plain ints" -- is exactly what a `Base<_>` parameter is consuming.
        // Declining to name the space is declining the label, so there is no
        // space to mis-mix and nothing to preserve: strip the wildcard here
        // and the operand promotes, unit-checks and shapes like its bare
        // inner type everywhere downstream.
        //
        // Concrete tags are untouched: `Nat<Lat> * 2.0` still errors. Note
        // this also makes the annotated form agree with the UNANNOTATED one,
        // which has always allowed the arithmetic by accident of ordering --
        // an unannotated kernel param is still an inference variable when its
        // body is typed, and only unifies with Nat<Lat> afterwards.
        let stripAnyTag t =
            match t with
            | IRTIdxTagged (inner, IRefAny) -> inner
            | _ -> t
        let lBare = IR.stripUnits leftTy |> stripAnyTag
        let rBare = IR.stripUnits rightTy |> stripAnyTag
        // No arithmetic on index types (named OR anonymous). Per the
        // formalism's nominal-type discipline, index types are nominal
        // labels -- arithmetic on them serves no useful purpose:
        // (1) value-level position arithmetic is reachable via virtual
        // array iteration, which produces plain ints; (2) type-level
        // construction of new index types from arithmetic is a separate
        // (deferred) workstream. So we simply reject. Post-Option-C, all
        // index references (named or anonymous, value or element position)
        // are represented uniformly as IRTIdxTagged.
        // Floats: by the same principle, index types are completely
        // incompatible with floating point -- no `Idx + Float` either.
        let isIndexType (t: IRType) = t.IsIRTIdxTagged
        let indexTypeName t =
            match t with
            | IRTIdxTagged (_, IRefNamed n) -> n
            | IRTIdxTagged (_, IRefAnon _) -> "<anonymous Idx>"
            | _ -> "?"
        let indexArithErr =
            match lBare, rBare with
            | IRTIdxTagged (_, IRefNamed ln), IRTIdxTagged (_, IRefNamed rn) when ln <> rn ->
                Some (CrossNominalIndexArith (ln, rn))
            | IRTIdxTagged (_, IRefAnon (lid, _)), IRTIdxTagged (_, IRefAnon (rid, _)) when lid <> rid ->
                Some (CrossAnonIndexArith (lid, rid))
            | l, r when isIndexType l || isIndexType r ->
                let n = if isIndexType l then indexTypeName l else indexTypeName r
                Some (IndexTypeArithForbidden n)
            | _ -> None
        match indexArithErr with
        | Some err -> Error err
        | None ->
        // Dist operands: checker-level operator dispatch (per-order cumulant
        // combination -- + adds, - flips odd orders, scalar * is c^k
        // multilinearity, all independence-gated) is still incomplete.
        // Until it lands, module-level dist operators still go
        // through the elaboration rewrites (which never reach here); any
        // OTHER position -- notably operators on Dist-typed function
        // parameters -- must error with steering rather than fall through
        // to scalar promotion and silently type nonsense.
        match lBare, rBare with
        | IRTDist _, _ | _, IRTDist _ ->
            Error (NotYetSupportedErr "operators on Dist values are not yet typed in this position (checker-level dist operator dispatch is in progress): combine dists where they are constructed (module-level d1 + d2, d1 - d2, c * d), or project a component with cumulant(d, k)")
        | _ ->
        // (Both-array Elementwise ops never reach here anymore: inferBinOp
        // re-synthesizes them as the zip co-iteration pipeline, which
        // handles packed and multi-rank storage the plain lowering could
        // not.)
        // Element-type promotion for array<->scalar broadcast: same rule as
        // the scalar-scalar cases below.
        let promoteElem (elemTy: IRType) (scalarTy: IRType) =
            match IR.stripUnits elemTy, IR.stripUnits scalarTy with
            // Complex mixed with real (or mixed-width complex) widens to the
            // appropriate complex type -- otherwise the element type would fall
            // back to the real side and the array would be typed real.
            | IRTScalar le, IRTScalar re
                when (match IR.promoteElemType le re with Some (ETComplex64 | ETComplex128) -> true | _ -> false) ->
                IRTScalar (IR.promoteElemType le re |> Option.get)
            | _ ->
                match elemTy, scalarTy with
                | IRTScalar ETFloat64, _ | _, IRTScalar ETFloat64 -> IRTScalar ETFloat64
                | IRTScalar ETFloat32, _ | _, IRTScalar ETFloat32 -> IRTScalar ETFloat32
                | _ -> elemTy
        // An OUTER product over two arrays combines ELEMENTS, so its unit rule
        // runs on the element signatures (the same rule gram / the zip arm
        // use, magnitude guard included) and the result's ELEMENT carries the
        // outcome: `a [*] b` over meters and seconds is meter*seconds, and
        // `a [+] b` over them is refused. Reading `getUnits` off the ARRAY
        // types (always None) accepted the sum and typed the product meters.
        let outerElemUnits =
            match mode, lBare, rBare with
            | Outer, ArrayElem arrL, ArrayElem arrR ->
                Some (unitRulesForArrayOp "the elements of an outer product" op
                        (IR.getUnits arrL.ElemType) (IR.getUnits arrR.ElemType) rExpr)
            | _ -> None
        match outerElemUnits with
        | Some (Error e) -> Error e
        | _ ->
        let bareResult =
            match mode with
            | Outer ->
                match lBare, rBare with
                | ArrayElem arrL, ArrayElem arrR ->
                    // Element type stays the LEFT operand's PRIMITIVE (the
                    // arithmetic-Outer convention, matched by lowering's
                    // kernelRetType = IRTScalar elemTypeL), wearing the
                    // element unit the rule above derived; everything else
                    // about the result is synthesized fresh -- see mkOuterResult.
                    let elem =
                        match outerElemUnits with
                        | Some (Ok (Some u)) -> IRTUnitAnnotated (IR.stripUnits arrL.ElemType, u)
                        | _ -> IR.stripUnits arrL.ElemType
                    mkOuterResult arrL arrR elem
                | _ -> lBare
            | Elementwise ->
                match lBare, rBare with
                // Array <op> scalar / scalar <op> array broadcast (`A + a`,
                // `2.0 / A`): the result follows the array's shape; the
                // ELEMENT type follows scalar promotion against the other
                // operand. Without this arm these fall into the scalar
                // rules below and type the whole result as a scalar (or
                // the bare left type), which codegen then emits as pointer
                // arithmetic on the Array wrapper. Lowering's broadcast
                // kernel path (lowerTypedBinOp) is the value-side pair of
                // this rule.
                | ArrayElem arrL, (IRTScalar _ as s) ->
                    mkArrayLike { arrL with ElemType = promoteElem arrL.ElemType s }
                | (IRTScalar _ as s), ArrayElem arrR ->
                    mkArrayLike { arrR with ElemType = promoteElem arrR.ElemType s }
                // UNRESOLVED operand beside a concrete array. An unannotated
                // kernel parameter is an inference var while its body is typed
                // -- it only unifies with the iterated element type later, in
                // buildApplyInfo -- so `w * xs` inside `ws <@> lambda(w) -> ...`
                // arrives here with `w` unresolved. BOTH readings of the var
                // agree on the SHAPE: if it resolves to a scalar this is a
                // broadcast over `xs`, and if it resolves to a row it is a zip
                // against `xs`; either way the result is an array shaped like
                // the concrete operand. Only the ELEMENT type is unknowable
                // now, so it stays the array's (the complex re-stamp in
                // buildApplyInfo is what upgrades a kernel result whose param
                // later resolves complex).
                //
                // The array-on-the-LEFT spelling (`xs * w`) has always answered
                // this way, by falling through to `lBare`; without the mirror,
                // `w * xs` answered with the VAR, and the difference was pure
                // operand order. Downstream that read as a scalar: the enclosing
                // `exp <@> (w * xs)` saw a non-array operand, mapped it as a
                // scalar, and a later `prodsum(xs, e)` refused an `e` that
                // should have been an array -- reported against the `<@>` site,
                // far from the multiplication that decided it.
                //
                // A caret (`T^k`, k >= 1) operand never reaches here as a var:
                // `materializeCaretOperand` above shapes it against the concrete
                // partner first, so this arm sees only genuinely unconstrained
                // vars. Elementwise only -- Outer keeps its own left-operand
                // convention.
                | IRTInfer _, ArrayElem _ -> rBare
                // Scalar complex promotion (mixed real/complex or mixed-width
                // complex): must precede the float rules so complex wins.
                | IRTScalar le, IRTScalar re
                    when (match IR.promoteElemType le re with Some (ETComplex64 | ETComplex128) -> true | _ -> false) ->
                    IRTScalar (IR.promoteElemType le re |> Option.get)
                | IRTScalar ETFloat64, _ | _, IRTScalar ETFloat64 -> IRTScalar ETFloat64
                | IRTScalar ETFloat32, _ | _, IRTScalar ETFloat32 -> IRTScalar ETFloat32
                | _ -> lBare
        // Apply unit rules based on operation (shared with the array
        // kernel-synthesis paths in inferBinOp)
        unitRulesForOpWith op lUnits rUnits rExpr |> Result.map (function
            | Some u -> IRTUnitAnnotated (bareResult, u)
            | None -> bareResult)

/// Resolve a TypedExpr through variable bindings to find the underlying
/// method_for / object_for / lambda.
and resolveTypedExpr (env: TypeEnv) (texpr: TypedExpr) : TypedExpr =
    match texpr.Kind with
    | TExprVar (name, _, _) ->
        match lookupVar name env with
        | Some info -> info.TypedValue |> Option.defaultValue texpr
        | None -> texpr
    | _ -> texpr

/// `resolveTypedExpr` to a FIXPOINT. The one-hop version is why `let P = (A,B)`
/// and `let Q = P` are different programs today (docs/plan-tuples-vs-arg-packs.md
/// 3.1, M1: `K <@> P` splats, `K <@> Q` does not, and the second miscompiles).
/// Substitution has to hold, so alias depth may not be observable. Fuelled
/// rather than cycle-tracked: a binding chain is acyclic by construction (a
/// `let` cannot mention itself), and the fuel is the cheap backstop if some
/// future binder form breaks that.
and resolveTypedExprDeep (env: TypeEnv) (texpr: TypedExpr) : TypedExpr =
    let rec chase (fuel: int) (e: TypedExpr) =
        if fuel <= 0 then e
        else
            match e.Kind with
            | TExprVar _ ->
                let r = resolveTypedExpr env e
                if System.Object.ReferenceEquals(r, e) then e else chase (fuel - 1) r
            | _ -> e
    chase 64 texpr
