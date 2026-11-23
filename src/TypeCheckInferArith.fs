// The type checker's ARITHMETIC JUDGMENT, outside the inference chain (cleanup round 2, K5): the unary operators,
// the binary arithmetic operators (inferArithBinOp, inferArithJudged, inferArithShapes: operand shapes, units and
// the result type), the operators over distributions, and the binary math intrinsics (atan2, log_base, ...). They
// type their operands through the chain's inferExpr, reached through InferEntries (TypeCheckInferEntries.fs); the
// result rules they apply are in TypeCheckInferUnits.fs. (The numeric casts stay in the chain: they read the
// chain's recStepVars.)
module Blade.TypeCheckInferArith

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
open Blade.TypeCheckInferUnits
open Blade.TypeCheckInferApply
open Blade.TypeCheckInferEntries
open Blade.TypeCheckInferRecArray
open Blade.TypeCheckInferArrayOps
open Blade.TypeCheckInferIntrinsics

let rec inferUnaryOp ({ InferEntries.InferExpr = inferExpr } as ie) (env: TypeEnv) op operand : TypeResult<TypedExpr> =
    inferExpr env operand |> Result.bind (fun tOp ->
        // THE NUMBERS-ONLY RULE (formalism 2.4): `-` of a non-number (`-true`,
        // `-"a"`, `-p` over a struct) is a type error; an open operand is
        // judged by the post-zonk sweep, a generic one per instance.
        // Its mirror: `!` of a non-Bool (`!1`) is refused the same way.
        match (if op = OpNeg then judgeNonNumericNegation env tOp
               elif op = OpNot then judgeNonBoolNot env tOp
               else None) with
        | Some msg -> Error (TypeMismatchErr msg)
        | None ->
        if op = OpNeg then recordGenericNegationObligation env tOp
        if op = OpNeg then recordOpenNumericDemand env "-" false tOp
        if op = OpNot then recordGenericNotObligation env tOp
        // R3 (D2): negation OBSERVES a value of an INTEGER EnumIdx -- `-c` is
        // `-Int64(c)`, the negated key (as inferBinOpCore's observeKey does for
        // arithmetic); an array of them negates each cell's key. A literal is
        // left alone: its R type, if any, comes from its context later (a
        // negative key, `EnumIdx<[-1, 2]>`, folds at zonk).
        if op = OpNeg && intEnumValueType env tOp.Type && not tOp.Kind.IsTExprLit then
            let tKey = mkTypedSpan (TExprUnaryOp (OpCast "Int64", tOp)) (IRTScalar ETInt64) tOp.Span
            Ok (mkTyped (TExprUnaryOp (OpNeg, tKey)) (IRTScalar ETInt64))
        elif op = OpNeg && intEnumArrayType env tOp.Type && (lookupVar "Int64" env).IsNone then
            // (Whole-array negation is built over dense storage only: over a
            // ragged array it allocates a rectangle -- Codex review of the lead
            // round -- and over compound / sparse / grouped storage it fails.)
            match enumKeyStorageKind env tOp.Type with
            | "dense" -> inferUnaryOp ie env OpNeg (enumKeyCastAst operand)
            | kind ->
                if operand.Span.StartLine > 0 then setCurrentExprSpan operand.Span
                Error (enumKeyStorageRefusal "Negation" kind)
        else
        // OpConj: result type equals operand type (complex stays complex,
        // real is the identity), mirroring OpNeg -- no type guard. std::conj
        // emitted only for complex element types.
        //
        // WHOLE-ARRAY conj: a scalar IRUnaryOp(IRConj,_) on an array operand
        // would emit a scalar std::conj against an array value and pass
        // through with no conjugation applied. Route to TExprArrayConjugate
        // instead (same node Hermitian-transpose's TConjugatedCopy uses),
        // materializing a fresh same-shape array via a pool conjugation loop
        // -- this is what makes `hermitian(A) = conj(transpose(A,[0,1]))`
        // work. Both whole-array arms keep the operand's type (Symmetry
        // included) with no compactClassInheritError-style check: negation
        // and conjugation commute with BOTH mirror involutions, so every
        // compact class is genuinely preserved -- (-A)(j,i)=-A(j,i)=A(i,j)
        // keeps antisymmetry; conj(conj z)=z keeps Hermitian.
        match op, tOp.Type with
        | OpConj, ArrayElem _ ->
            Ok (mkTyped (TExprArrayConjugate tOp) tOp.Type)
        // The negation half of the same routing. Without it `-A` over an array
        // reached codegen as a scalar IRUnaryOp(IRNeg, _) and emitted `-arr`
        // against an Array value ("no match for 'operator-'"). It bites hardest
        // on `-(A [-] B)`: for a bracketed op the operand order fixes the AXIS
        // order, so an outer `-` is the only free way to correct the sign --
        // swapping the operands transposes instead.
        | OpNeg, ArrayElem _ ->
            Ok (mkTyped (TExprArrayNegate tOp) tOp.Type)
        | _ ->
            let resTy = match op with
                        | OpNot -> IRTScalar ETBool
                        | OpNeg -> tOp.Type
                        | OpConj -> tOp.Type
                        // OpReal/OpImag project a complex to its component
                        // width (identity on a real operand); OpArg is a real
                        // angle. Synthesized by the intrinsic intercept below.
                        | OpReal | OpImag ->
                            (match tOp.Type with
                             | IRTScalar ETComplex64 -> IRTScalar ETFloat32
                             | IRTScalar ETComplex128 -> IRTScalar ETFloat64
                             | other -> other)
                        | OpArg -> IRTScalar ETFloat64
                        // OpMath is synthesized by the ExprApp intrinsic
                        // intercept, never parsed as ExprUnaryOp -- this arm
                        // is exhaustiveness only. OpCast likewise (built only
                        // by the cast arm).
                        | OpMath _ -> IRTScalar ETFloat64
                        | OpCast name ->
                            (match castTargetOf name with
                             | Some et -> IRTScalar et
                             | None -> IRTScalar ETFloat64)
            Ok (mkTyped (TExprUnaryOp (op, tOp)) resTy))

// ---- Module-qualified value/function: `Math.pi`, `MathLib.double(x)` ----
// These two cases must precede the method-call and struct-field handlers
// because `Math` would otherwise be looked up as a value and fail. The
// DeclImport handler registers imported entries under qualified names
// (`Math.pi`, `MathLib.double`); when the form is `ExprVar n . field`
// and the qualified name resolves, we treat the access as a direct
// variable reference. Falls through to the existing struct/method
// handlers when the qualified name is not registered.

and inferArithBinOp ({ InferEntries.InferExpr = inferExpr } as ie) (env: TypeEnv) (left: Expr) (right: Expr) (op: BinOp) (mode: BinOpMode) : TypeResult<TypedExpr> =
    // Arithmetic, comparison, logical
    inferExpr env left |> Result.bind (fun tL ->
    inferExpr env right |> Result.bind (fun tR ->
        // A value of an INTEGER EnumIdx type is a KEY (D2): arithmetic or
        // a comparison OBSERVES it, as the plain Int64 its key is. It is
        // spelled as the cast `Int64(v)` here (an index value refuses
        // arithmetic otherwise), whose zonk reads the key off the value's
        // position representation (Zonk.intEnumEdges).
        let observeKey (t: TypedExpr) =
            match op, IR.stripUnits (env.Subst.Resolve t.Type) with
            | (OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe),
              IntTaggedIndex tag
                when mode = Elementwise && (intEnumKeysOfTag env tag).IsSome ->
                mkTypedSpan (TExprUnaryOp (OpCast "Int64", t)) (IRTScalar ETInt64) t.Span
            | _ -> t
        let tL, tR = observeKey tL, observeKey tR
        // NOTE (merge, 2026-08-09): master reached this same demand site
        // independently (fix/kernel-capture-module-binding) with a
        // `concreteOperand`/`materializeCaretOperand` pair. Both arrived at
        // the same load-bearing restriction -- demand a shape ONLY when the
        // other operand already pins it, because `packsum1`'s
        // `head + packsum1(tail)` has an arity-1 var on both sides and
        // shaping both emits an undeclared `arr1`. This branch's version is
        // kept: it additionally gates on `mode = Elementwise` and on the
        // operator, and tests the PRE-materialization snapshots so shaping
        // one side cannot make the other look pinned. See the S1 SEAM 1
        // block below.
        //
        // `zero` BESIDE A PARTNER is the partner's zero. It is typed a
        // fresh variable (any context may adopt it), and as an operand
        // nothing ever bound that variable: the promotion rules answer the
        // partner's type WITHOUT unifying, so the zero stayed open, zonk
        // defaulted it to Float64 and lowering emitted `0.0` -- `a + zero`
        // at an Int64 (or Float32) operand computed `int64 + double` and
        // narrowed on the way out (g++ -Werror, BL9002), and inside a
        // generic `addz(a: T^0) = a + zero` every instance got the same
        // `0.0`. Bind it to the partner's scalar type -- the element, for
        // an array partner (the zero broadcasts) -- with units stripped:
        // `zero` carries no signature, so `x_m * zero` stays meters, as
        // before. A generic partner (`T^0`, a polymorphic element) binds
        // the zero TO that variable, so HM monomorphization substitutes
        // it per instance and lowering emits each instance's own zero
        // (Lowering's TExprZero arm, IR.zeroLiteralOf).
        //
        // An unshaped `T^k` partner (k >= 1) has no element to name yet:
        // the zero keeps its own variable, marked polymorphic so zonk
        // leaves it open, and IRMono.resolveTypedZerosModule gives it the
        // array partner's element once monomorphization has made that
        // concrete. Any OTHER open partner (an unannotated kernel
        // parameter, which may still resolve to a row) is left alone.
        let zeroAdoptsPartner (z: TypedExpr) (partner: TypedExpr) =
            match z.Kind, env.Subst.Resolve z.Type with
            | TExprZero, IRTInfer zid ->
                let numericOrBool (t: IRType) =
                    match t with
                    | IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128 | ETBool) -> true
                    | _ -> false
                let rank0Generic (t: IRType) =
                    match t with
                    | IRTInfer pid ->
                        pid <> zid && env.Subst.IsPolymorphicId pid
                        && (env.Subst.GetArityConstraint pid).IsNone
                    | _ -> false
                match IR.stripUnits (env.Subst.Resolve partner.Type) with
                | t when numericOrBool t || rank0Generic t -> env.Subst.Bind(zid, t)
                | ArrayElem arr ->
                    (match IR.stripUnits (env.Subst.Resolve arr.ElemType) with
                     | e when numericOrBool e || rank0Generic e -> env.Subst.Bind(zid, e)
                     | _ -> ())
                | IRTInfer pid when pid <> zid ->
                    (match env.Subst.GetArityConstraint pid with
                     | Some k when k >= 1 -> env.Subst.MarkPolymorphic zid
                     | _ -> ())
                | _ -> ()
            | _ -> ()
        zeroAdoptsPartner tL tR
        zeroAdoptsPartner tR tL
        let lRes0 = env.Subst.Resolve tL.Type
        let rRes0 = env.Subst.Resolve tR.Type
        let isDist t = match t with IRTDist _ -> true | _ -> false
        if isDist lRes0 || isDist rRes0 then
            // Typed-Dist operator dispatch (checker-level; the surface
            // operand exprs are re-synthesized into the expansion, so
            // this works in any expression position -- see inferDistBinOp).
            inferDistBinOp ie env op left right lRes0 rRes0
        else
        inferArithJudged ie env op mode lRes0 rRes0 tL tR left right))

and inferArithJudged (ie: InferEntries) (env: TypeEnv) (op: BinOp) (mode: BinOpMode) (lRes0: IRType) (rRes0: IRType) (tL: TypedExpr) (tR: TypedExpr) (left: Expr) (right: Expr) : TypeResult<TypedExpr> = typed {
    // Two CARET parameters of DIFFERENT ranks (`a + b` over `a: T^1,
    // b: U^2`) meet elementwise: no instance can zip them -- the
    // operands' ranks are the carets', fixed by the declaration -- the
    // same refusal `A + B` gets over a concrete rank-1 and rank-2
    // array. Both stayed unshaped variables (neither pins the other),
    // so the declaration checked clean and every call reached g++
    // (BL9002).
    let caretRankOf (t: IRType) =
        match IR.stripUnits t with
        | IRTInfer v ->
            (match env.Subst.GetArityConstraint v with
             | Some k when k >= 1 -> Some k
             | _ -> None)
        | _ -> None
    let caretRankClash =
        match op with
        | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
        | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
        | OpAnd | OpOr when mode = Elementwise ->
            (match caretRankOf lRes0, caretRankOf rRes0 with
             | Some k, Some j when k <> j -> Some (k, j)
             | _ -> None)
        | _ -> None
    do! (match caretRankClash with
         | Some (k, j) ->
             Error (IndexSpaceErr ("elementwise operators on multi-axis arrays require both operands to have matching plain-dense index shapes (same axis tags and extents); mixed dense/packed or mismatched shapes are not zip-able"
                           + $": the left operand is a rank-{k} parameter (`^{k}`) and the right operand a rank-{j} one, so no call can give them one shape. "
                           + "For every pair of cells, use the outer operator (`a [+] b`); to combine a row with each row of a matrix, map over the matrix's rows."))
         | None -> Ok ())
    // S1 SEAM 1 (docs/plan-kernel-body-materialization.md, M-B).
    // Both array-producing arms below -- the two-array zip and the
    // array/scalar broadcast -- are gated on an operand RESOLVING to
    // an array. A caret-shorthand `T^k` row parameter never does on
    // its own, so `r * 2.0` inside `lambda(r: T^1) -> ...` fell to the
    // scalar fallback and typed the row product `IRTScalar Float64`;
    // every array consumer of that intermediate then refused it.
    // Issue the same demand the array intrinsics issue, BEFORE the
    // arms are chosen. See materializeArityVar: the arity constraint
    // already forces a rank-k array, so this changes when the shape
    // appears, not whether it may.
    //
    // ONLY WHEN THE OTHER OPERAND PINS THE SHAPE -- the second of the
    // two restrictions this demand carries (the first, lambda bodies
    // only, lives in materializeArityVar). When the other side is a
    // concrete array (zip) or a concrete scalar (broadcast), the rank is
    // already decided by this expression and unification would reach the
    // same binding, so the demand is not speculation. When BOTH sides
    // are unresolved -- `packsum1`'s `head + packsum1(tail)`, a plain
    // destructure var against the function's own generic return var --
    // nothing here knows the shape, and deferring to lowering (master's
    // behaviour) is the correct answer.
    let pinsShape (t: IRType) =
        match IR.stripUnits t with
        | ArrayElem _ -> true       // zip partner: rank decided
        | IRTScalar _ -> true       // broadcast partner: scalar by construction
        // `T<u>^0` lowers to a unit-annotated VAR but the caret already
        // said rank 0, so it pins just as a scalar does. A BARE var
        // pins nothing.
        | IRTInfer _ -> (match t with IRTUnitAnnotated (IRTInfer _, _) -> true | _ -> false)
        | _ -> false
    match op with
    | (OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
      | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
      | OpAnd | OpOr) when mode = Elementwise ->
        // Both tested against the PRE-materialization snapshots, so
        // materializing one operand can never make the other look
        // pinned (`r1 * r2` over two `T^1` params stays deferred).
        if pinsShape rRes0 then materializeArityVar env tL "elementwise"
        if pinsShape lRes0 then materializeArityVar env tR "elementwise"
    // OUTER (`a [*] b`): the product of two arrays is the all-pairs
    // array of rank (rank a + rank b) whose element is the left
    // operand's -- a shape an UNSHAPED `T^k` operand cannot express
    // while it is a bare variable (the whole array IS the variable,
    // so there is no element to name and no axes to concatenate).
    // Left unshaped, the fallback typed `a [*] b` over two `T^1`
    // parameters as `T` itself (rank 1) and lowering emitted the raw
    // `(a * b)` on two Arrays (g++: no operator*). Here each operand's
    // shape is pinned by its OWN caret -- an outer product never
    // relates the operands' axes -- so the demand is not speculation
    // even when both sides are variables; it is the same rank-k shape
    // the caret already forces (materializeArityVar), after which the
    // concrete-typed outer path (mkOuterResult, or the method_for
    // re-synthesis for multi-axis operands) applies unchanged. Only
    // when BOTH operands are arrays or caret vars: an outer op
    // against a scalar has no cross axis and keeps its old reading.
    | (OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
      | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
      | OpAnd | OpOr) when mode = Outer ->
        let arrayOrCaret (t: IRType) =
            match IR.stripUnits t with
            | ArrayElem _ -> true
            | IRTInfer vid ->
                (match env.Subst.GetArityConstraint vid with
                 | Some k when k >= 1 -> true
                 | _ -> false)
            | _ -> false
        if arrayOrCaret lRes0 && arrayOrCaret rRes0 then
            materializeArityVar env tL "outer"
            materializeArityVar env tR "outer"
    | _ -> ()
    // ROUND 6: a literal-only operand takes the width its partner
    // fixes (adaptLiteralOperands), before the mixing rule judges.
    let fitRefusal =
        if mode = Elementwise || mode = Outer then literalFitRefusal env op tL tR else None
    do! (match fitRefusal with
         | Some (litLeft, msg) ->
             let sp = if litLeft then left.Span else right.Span
             if sp.StartLine > 0 then setCurrentExprSpan sp
             Error (ImplicitNumericMix msg)
         | None -> Ok ())
    let tL, tR =
        if mode = Elementwise || mode = Outer then adaptLiteralOperands env op tL tR else tL, tR
    let lRes = env.Subst.Resolve tL.Type
    let rRes = env.Subst.Resolve tR.Type
    // THE NUMBERS-ONLY RULE (formalism 2.4; the user's ruling of
    // 2026-11-14): arithmetic on a Bool, a String (other than `+`
    // between two Strings), a struct, a sum-type value, a tuple or a
    // function -- or an array of them -- and a comparison of such a
    // value with a number, is a type error here, before any
    // broadcast/zip synthesis. An operand still open is judged by the
    // post-zonk sweep (collectNumericMixErrors) or, for a generic
    // signature variable, per instance (GONumericOperand).
    let nonNumericRefusal =
        if mode = Elementwise || mode = Outer then
            judgeNonNumericOperand env op tL tR
            |> Option.map (fun (leftOff, msg) -> ((if leftOff then left.Span else right.Span), msg))
        else None
    do! (match nonNumericRefusal with
         | Some (sp, msg) ->
             if sp.StartLine > 0 then setCurrentExprSpan sp
             Error (TypeMismatchErr msg)
         | None -> Ok ())
    recordGenericNumericObligation env op tL tR
    recordOpenNumericDemands env op tL tR
    // THE MIXING RULE (formalism 2.4): an arithmetic op or comparison
    // converts nothing implicitly -- an integer beside a float or a
    // complex is refused (literals included: `1` is an integer, `1.0` a
    // Float64), and so are two widths of one class unless one side is
    // a literal (which adapts). Judged here on the operands' ELEMENTS,
    // array or scalar, before any broadcast/zip synthesis, so the
    // refusal is anchored at the operand the conversion would touch.
    // Operands still open (a kernel parameter) are judged by the
    // post-zonk sweep (TypeCheckValidate.collectNumericMixErrors);
    // a generic body's own variables, per instance (GOPromotion /
    // GOBroadcastElem / GOElemAgree).
    let mixRefusal =
        // An index-typed operand (`Nat<I>`) beside a float is the
        // nominal rule's refusal (inferArithType's indexArithErr),
        // which keeps its own code; the mixing rule judges the rest.
        let idxTagged (t: IRType) =
            match IR.stripUnits t with
            | IRTIdxTagged (_, r) -> r <> IRefAny
            | _ -> false
        if (mode = Elementwise || mode = Outer) && not (idxTagged lRes || idxTagged rRes) then
            judgeNumericMix env op tL tR
            |> Option.map (fun (leftOff, msg) -> ((if leftOff then left.Span else right.Span), msg))
        else None
    do! (match mixRefusal with
         | Some (sp, msg) ->
             if sp.StartLine > 0 then setCurrentExprSpan sp
             Error (ImplicitNumericMix msg)
         | None -> Ok ())
    recordGenericMixObligation env op tL tR
    return! inferArithShapes ie env lRes rRes op mode tR left right tL
}

and inferArithShapes ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (lRes: IRType) (rRes: IRType) (op: BinOp) (mode: BinOpMode) (tR: TypedExpr) (left: Expr) (right: Expr) (tL: TypedExpr) : TypeResult<TypedExpr> =
    // Elementwise op on TWO ARRAYS: re-synthesize as the zip
    // co-iteration pipeline -- method_for(zip(l, r)) <@>
    // lambda(u, w) -> u op w |> compute -- and re-infer
    // (synthesize-and-infer, the inferDistBinOp pattern). The
    // direct TExprBinOp lowering hand-rolled a flat rank-1
    // object_for loop, which mis-iterates symmetry-PACKED storage
    // (row pointers into a scalar kernel -- silent miscompile) and
    // any rank > 1 operand; the co-iteration builder handles
    // packed, dense, and multi-rank uniformly. Outer mode ([+])
    // keeps its cross-iteration path.
    let bothArrays =
        (match lRes with ArrayElem _ -> true | _ -> false)
        && (match rRes with ArrayElem _ -> true | _ -> false)
    let isZipOp =
        match op with
        | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
        | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
        | OpAnd | OpOr -> true
        | _ -> false
    // Unit judgment for the synthesized kernel paths below happens
    // at THIS site, not in the kernel body: the lambda parameters
    // are still unresolved inference variables when the body's
    // binop is inferred (they only unify with the element types
    // later, in buildApplyInfo), so inferArithType's unit rules
    // see no units there. The operand ELEMENT units are visible
    // here -- unitRulesForOp checks/composes them and the result
    // element type is stamped over the inferred pipeline type.
    let elemUnits t =
        match t with ArrayElem at -> IR.getUnits at.ElemType | _ -> None
    // OUTER op on two arrays of which at least one is multi-axis:
    // `A [+] B` is `method_for(A, B) <@> lambda(a, b) -> a + b` --
    // the all-pairs product of the operands' CELLS (formalism 7.2:
    // output rank = rank A + rank B). The type rule (mkOuterResult)
    // already said so, but the direct TExprBinOp lowering walks one
    // loop level per OPERAND, handing two row pointers to a scalar
    // kernel -- quickstart-1's own `A [+] B` over two matrices died in
    // g++. Re-synthesize through the former (the zip arm's pattern)
    // so the loop nest is the one method_for already builds for any
    // rank. Rank-1 pairs keep the direct path, byte-identical.
    let multiAxis =
        match lRes, rRes with
        | ArrayElem aL, ArrayElem aR -> aL.IndexTypes.Length > 1 || aR.IndexTypes.Length > 1
        | _ -> false
    if mode = Outer && bothArrays && isZipOp && multiAxis then
        match unitRulesForArrayOp "an outer-product array operator" op (elemUnits lRes) (elemUnits rRes) (Some tR) with
        | Error e -> Error e
        | Ok resUnits ->
            let sp = mergeSpan left.Span right.Span
            let kbody = mkExpr sp (ExprBinOp (Elementwise, op, mkExpr sp (ExprVar "__ol"), mkExpr sp (ExprVar "__or")))
            let klam = mkExpr sp (ExprLambda ([{ Name = "__ol"; Type = None; Default = None; NameSpan = noSpan }; { Name = "__or"; Type = None; Default = None; NameSpan = noSpan }], None, kbody))
            let kfor = mkExpr sp (ExprMethodFor [left; right])
            let synth = mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply, kfor, klam))))
            inferExpr env synth |> Result.map (stampElemUnits env resUnits)
    elif mode = Elementwise && bothArrays && isZipOp then
        // Zip-able operand shapes: one index record per operand (dense
        // rank-1, or packed symmetry-class storage of any logical rank --
        // the co-iteration walks its flat canonical cells), or BOTH
        // operands multi-record all-plain-dense with structurally
        // matching shapes (dense rank >= 2 -- the co-iteration spans the
        // full product of the shared records). Mismatched or mixed
        // dense/packed multi-axis shapes reject clearly rather than
        // letting codegen emit a loop-object error.
        let zipable =
            match lRes, rRes with
            | ArrayElem aL, ArrayElem aR ->
                (aL.IndexTypes.Length = 1 && aR.IndexTypes.Length = 1)
                || (aL.IndexTypes |> List.forall isPlainDenseIx
                    && aR.IndexTypes |> List.forall isPlainDenseIx
                    && indexShapesAgree aL.IndexTypes aR.IndexTypes)
            | _ -> false
        // THE SIGN-CHARACTER RULE (G16; formalism 8.5). A compact class
        // reconstructs its unstored triangle through a mirror involution
        // on the VALUE: identity for SymIdx (character +1), negation for
        // AntisymIdx (character -1). An elementwise op over two compact
        // operands on ONE index space therefore has a result class fixed
        // by the operands' characters:
        //   *, /    the characters MULTIPLY: A*A and S*S are symmetric,
        //           A*S and S*A antisymmetric ((-a)(-b) = ab).
        //   +, -    equal characters keep theirs (A+A antisymmetric,
        //           S+S symmetric); MIXED characters (A+S) have no
        //           mirror law at all -- the result is DENSE.
        //   others  (%, ^, comparisons, logicals) respect no sign law:
        //           DENSE.
        // Only A±A and S∘S keep the operands' record, so only they
        // co-iterate (the zip below). Every class CHANGE is a gather
        // over the RESULT class's own index range, each cell reading the
        // operands by logical subscript (their reads apply their own
        // mirror and the zero antisymmetric diagonal), so the result is
        // stored in its true class: S = R * R is a SymIdx array with
        // its (zero) diagonal, and S(1, 0) = S(0, 1). Before this rule
        // `R * R` zipped the strict cells, kept the antisymmetric class,
        // and negated every mirrored read (a silent wrong value in all
        // three lanes).
        let signRule =
            match lRes, rRes with
            | ArrayElem aL, ArrayElem aR ->
                (match aL.IndexTypes, aR.IndexTypes with
                 | [ ixL ], [ ixR ]
                     when ixL.Rank >= 2 && ixL.Rank = ixR.Rank
                          && ixL.IxKind = IxKPlain && ixR.IxKind = IxKPlain
                          && (ixL.Symmetry = SymAntisymmetric || ixR.Symmetry = SymAntisymmetric)
                          && (ixL.Symmetry = SymAntisymmetric || ixL.Symmetry = SymSymmetric)
                          && (ixR.Symmetry = SymAntisymmetric || ixR.Symmetry = SymSymmetric) ->
                     let ch (ix: IRIndexType) = if ix.Symmetry = SymAntisymmetric then -1 else 1
                     let cL, cR = ch ixL, ch ixR
                     let result =
                         match op with
                         // S / A is sign-odd, but its diagonal is s / 0
                         // (inf/NaN, or BL8013 for integers), which a
                         // strict class would silently store as 0: the
                         // result is DENSE so the diagonal is computed.
                         // A / A keeps SYMMETRIC: that range has a stored
                         // diagonal, computed (0 / 0) like any cell.
                         | OpDiv when cR < 0 && cL > 0 -> None
                         | OpMul | OpDiv -> Some (if cL * cR < 0 then SymAntisymmetric else SymSymmetric)
                         | OpAdd | OpSub when cL = cR -> Some (if cL < 0 then SymAntisymmetric else SymSymmetric)
                         | _ -> None
                     // A ± A keeps the record: the zip's joint-parity
                     // gate certifies it (buildApplyInfo).
                     if result = Some SymAntisymmetric && cL < 0 && cR < 0 then None
                     else Some (ixL, ixR, result)
                 | _ -> None)
            | _ -> None
        // The base the result's range is spelled over: the operands'
        // shared named index type, or their shared static extent. A
        // base this site cannot spell (a runtime extent, two different
        // bases) leaves the old path, whose gates refuse (BL3027 across
        // two records; BL4015's joint-parity arm on one record).
        let signBase =
            match signRule with
            | Some (ixL, ixR, _) when ixL.Tag = ixR.Tag && ixL.Extent = ixR.Extent ->
                let sp = mergeSpan left.Span right.Span
                (match ixL.Tag with
                 | Some t ->
                     (match lookupTypeDef t env with
                      | Some (TDIIndexType _) -> Some (mkExpr sp (ExprVar t), TyNamed (t, []))
                      | _ -> None)
                 | None ->
                     (match ixL.Extent with
                      | IRLit (IRLitInt n) ->
                          Some (mkExpr sp (ExprLit (LitInt n)), TyIdx (mkExpr sp (ExprLit (LitInt n))))
                      | _ -> None))
            | _ -> None
        match signRule, signBase with
        | Some (ixL, _, result), Some (baseExpr, baseIdxTy) ->
            unitRulesForArrayOp "an elementwise array operator" op (elemUnits lRes) (elemUnits rRes) (Some tR)
            |> Result.bind (fun resUnits ->
                let sp = mergeSpan left.Span right.Span
                let synAt k = mkExpr sp k
                let uid = env.Builder.FreshId()
                let r = ixL.Rank
                let pNames = List.init r (fun m -> $"__sg{uid}_{m}")
                let pVar m = synAt (ExprVar pNames.[m])
                let lit (n: int) = synAt (ExprLit (LitInt (int64 n)))
                let add a b = synAt (ExprBinOp (Elementwise, OpAdd, a, b))
                // The kernel receives the range's PREFIX OFFSETS
                // (loops/170, 173): a SymIdx coordinate is
                // p0 + .. + pm, an AntisymIdx one adds the strict step m.
                // A dense range hands the coordinates themselves.
                let coords =
                    match result with
                    | None -> List.init r pVar
                    | Some cls ->
                        List.init r (fun m ->
                            let prefix = List.init (m + 1) pVar |> List.reduce add
                            if cls = SymAntisymmetric && m > 0 then add prefix (lit m) else prefix)
                let rangeTy =
                    match result with
                    | Some SymSymmetric -> [ TySymIdx (r, SymBaseExtent baseExpr) ]
                    | Some _ -> [ TyAntisymIdx (r, SymBaseExtent baseExpr) ]
                    | None -> List.replicate r baseIdxTy
                let bindName side = $"__sg{uid}_{side}"
                let lSrc = if left.Kind.IsExprVar then left else synAt (ExprVar (bindName "l"))
                let rSrc = if right.Kind.IsExprVar then right else synAt (ExprVar (bindName "r"))
                let body =
                    synAt (ExprBinOp (Elementwise, op,
                                      synAt (ExprApp (lSrc, coords)),
                                      synAt (ExprApp (rSrc, coords))))
                let klam =
                    synAt (ExprLambda (pNames |> List.map (fun n -> { Name = n; Type = None; Default = None; NameSpan = noSpan }), None, body))
                let gather =
                    synAt (ExprCompute (synAt (ExprBinOp (Elementwise, OpApply,
                                                          synAt (ExprMethodFor [ synAt (ExprRange rangeTy) ]), klam))))
                let lets =
                    [ if not left.Kind.IsExprVar then
                          yield StmtLet { Mutability = BindLet; Pattern = mkPat sp (PatVar (bindName "l")); Type = None; Value = left }
                      if not right.Kind.IsExprVar then
                          yield StmtLet { Mutability = BindLet; Pattern = mkPat sp (PatVar (bindName "r")); Type = None; Value = right } ]
                let synth = if lets.IsEmpty then gather else synAt (ExprBlock (lets, Some gather))
                inferExpr env synth |> Result.map (stampElemUnits env resUnits))
        | _ ->
        if zipable then
            match unitRulesForArrayOp "an elementwise array operator" op (elemUnits lRes) (elemUnits rRes) (Some tR) with
            | Error e -> Error e
            | Ok resUnits ->
                let sp = mergeSpan left.Span right.Span
                let kbody = mkExpr sp (ExprBinOp (Elementwise, op, mkExpr sp (ExprVar "__zl"), mkExpr sp (ExprVar "__zr")))
                let klam = mkExpr sp (ExprLambda ([{ Name = "__zl"; Type = None; Default = None; NameSpan = noSpan }; { Name = "__zr"; Type = None; Default = None; NameSpan = noSpan }], None, kbody))
                let kzip = mkExpr sp (ExprMethodFor [mkExpr sp (ExprZip [left; right])])
                let synth = mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply, kzip, klam))))
                inferExpr env synth |> Result.map (stampElemUnits env resUnits)
        else
            // Name which axis disagrees and how: the bare sentence left
            // the user to guess which of the store's dimensions moved
            // on a diverged-checkout program. Empty for a non-shape
            // cause (mixed dense/packed), where the sentence already
            // says it.
            let detail =
                match lRes, rRes with
                | ArrayElem aL, ArrayElem aR ->
                    Blade.TypeLower.indexShapeClashDetail
                        "the left operand" "the right operand" aL.IndexTypes aR.IndexTypes
                | _ -> ""
            Error (IndexSpaceErr ("elementwise operators on multi-axis arrays require both operands to have matching plain-dense index shapes (same axis tags and extents); mixed dense/packed or mismatched shapes are not zip-able" + detail))
    else
    // Elementwise op on ARRAY <-> SCALAR (`A + a`, `2.0 / A`,
    // `A > t`): re-synthesize as a 1-param kernel map over the array
    // operand -- method_for(A) <@> lambda(__bx) -> __bx op s |>
    // compute -- the same synthesize-and-infer route as the both-array
    // zip above. Embedding the scalar operand's SURFACE expr (not its
    // lowered IR) in the lambda body lets capture analysis see its
    // variable references, so the lifted kernel receives them as
    // explicit capture params and emits at file scope
    // (forward-declared) -- embedding the lowered IR directly would
    // make a variable reference a free var, forcing a main-local
    // std::function emitted AFTER its use site (invalid C++).
    let scalarish (t: IRType) = t.IsIRTScalar
    // `T<u>^0` -- the rank-0 abstract parameter -- lowers to a
    // unit-annotated inference VARIABLE, not an IRTScalar
    // (lowerTypeExpr's TyAbstractArray arm stamps the unit onto the
    // scalar type var). It is a scalar by construction: the caret says
    // rank 0. Without this it missed `scalarish`, the broadcast arm
    // never fired, and the fallback below stamped the op's unit around
    // the whole ARRAY result -- `Array<T<time> like Idx<n>><time>`, a
    // shape no `ArrayElem` match can see through, so every later array
    // demand ("prodsum() requires array arguments", "reduce() requires
    // an array") refused a value that IS an array. An UNANNOTATED
    // `T^0` never showed it: with no unit there is nothing to wrap, so
    // the fallback returned the bare array and the program typed.
    //
    // Deliberately narrow: a bare `IRTInfer` is NOT admitted here. An
    // unannotated kernel parameter is one of those and may still
    // resolve to an array, which is a zip, not a broadcast.
    let rankZeroQuantity t =
        match t with
        | IRTUnitAnnotated (IRTInfer _, _) -> true
        | _ -> false
    let arrayScalar =
        match lRes, rRes with
        | ArrayElem _, r when scalarish (IR.stripUnits r) || rankZeroQuantity r -> Some true    // array on left
        | l, ArrayElem _ when scalarish (IR.stripUnits l) || rankZeroQuantity l -> Some false   // array on right
        | _ -> None
    match arrayScalar with
    | Some arrayOnLeft when mode = Elementwise && isZipOp ->
        // Same synthesis-site unit judgment as the zip path above:
        // the kernel param annotation deliberately strips units
        // (elemAnn below), so the body's binop checks nothing --
        // judge the array's ELEMENT units against the scalar
        // operand's units here, in operand order.
        let arrU = elemUnits (if arrayOnLeft then lRes else rRes)
        let scalU = IR.getUnits (if arrayOnLeft then rRes else lRes)
        let luB, ruB = if arrayOnLeft then (arrU, scalU) else (scalU, arrU)
        match unitRulesForArrayOp "an elementwise array/scalar operator" op luB ruB (Some tR) with
        | Error e -> Error e
        | Ok resUnits ->
        let sp = mergeSpan left.Span right.Span
        let (arrExpr, body) =
            if arrayOnLeft then (left, mkExpr sp (ExprBinOp (Elementwise, op, mkExpr sp (ExprVar "__bx"), right)))
            else (right, mkExpr sp (ExprBinOp (Elementwise, op, left, mkExpr sp (ExprVar "__bx"))))
        // Annotate the kernel param with the array's element type:
        // at body-inference time an unannotated param is still an
        // unresolved infer var, and inferArithType's promotion rules
        // would fall back to the scalar side's type (`a * A` would
        // type Int64 elements for a double-computing body).
        let elemAnn =
            match (if arrayOnLeft then lRes else rRes) with
            | ArrayElem arr ->
                match IR.stripUnits arr.ElemType with
                | IRTScalar ETFloat64 -> Some TyFloat64
                | IRTScalar ETFloat32 -> Some TyFloat32
                | IRTScalar ETInt64 -> Some TyInt64
                | IRTScalar ETInt32 -> Some TyInt32
                | IRTScalar ETBool -> Some TyBool
                | IRTScalar ETComplex64 -> Some TyComplex64
                | IRTScalar ETComplex128 -> Some TyComplex128
                | IRTScalar ETString -> Some TyString
                | _ -> None
            | _ -> None
        let synth =
            mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                mkExpr sp (ExprMethodFor [arrExpr]),
                mkExpr sp (ExprLambda ([{ Name = "__bx"; Type = elemAnn; Default = None; NameSpan = noSpan }], None, body))))))
        // G276 (2): an array whose ELEMENT is still a let-bound lambda
        // value's open parameter (`[v, v] * 3.0` in a fused compose
        // stage) hands that dependency to the synthesized kernel: its
        // parameter is judged like the lambda value's (the literal
        // partner takes the width the later use decides).
        let elemIsLetLambdaParam =
            match (if arrayOnLeft then lRes else rRes) with
            | ArrayElem arr when elemAnn.IsNone ->
                (match env.Subst.Resolve arr.ElemType |> IR.stripUnits with
                 | IRTInfer ev ->
                     env.LetLambdaParamVars |> Seq.exists (fun p ->
                         match env.Subst.Resolve (IRTInfer p) |> IR.stripUnits with
                         | IRTInfer r -> r = ev
                         | _ -> false)
                 | _ -> false)
            | _ -> false
        if elemIsLetLambdaParam then env.PendingLetLambda.Value <- true
        let synthResult = inferExpr env synth
        env.PendingLetLambda.Value <- false
        synthResult |> Result.map (fun tSynth ->
            // A GENERIC array (`a: T^1`, element a signature variable)
            // beside a concrete scalar (`a * 0.5`): the kernel was typed
            // while the element was open, so its cells took the
            // partner's class. Record what each call must keep true
            // (GOBroadcastElem): an Int64 or Float64 row times 0.5 is
            // Float64 cells, exact; a complex row (or a Float64 row
            // times a Float32) is refused at the call instead of
            // reaching g++ (BL9002).
            (if not env.InLambdaBody then
                match env.CurrentGenericObligations with
                | Some acc ->
                    let arrTy, scalTe = if arrayOnLeft then lRes, tR else rRes, tL
                    match arrTy, IR.stripUnits (env.Subst.Resolve scalTe.Type), IR.stripUnits (env.Subst.Resolve tSynth.Type) with
                    | ArrayElem arr, IRTScalar partner, ArrayElem resArr ->
                        (match env.Subst.Resolve arr.ElemType |> IR.stripUnits,
                               env.Subst.Resolve resArr.ElemType |> IR.stripUnits with
                         | IRTInfer eid, IRTScalar result
                                when env.Subst.IsPolymorphicId eid && partner <> ETBool && partner <> ETString ->
                             let opName =
                                 match op with
                                 | OpAdd -> "+" | OpSub -> "-" | OpMul -> "*" | OpDiv -> "/"
                                 | OpMod -> "%" | OpCaret -> "^"
                                 | OpEq -> "==" | OpNeq -> "!=" | OpLt -> "<" | OpLe -> "<="
                                 | OpGt -> ">" | OpGe -> ">=" | _ -> "an operator"
                             let isCmp =
                                 match op with
                                 | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe | OpAnd | OpOr -> true
                                 | _ -> false
                             if not isCmp then acc.Add(GOBroadcastElem (eid, opName, partner, result))
                         | _ -> ())
                    | _ -> ()
                | None -> ())
            stampElemUnits env resUnits tSynth)
    | _ ->
    inferArithScalar env mode op tL tR lRes rRes isZipOp

/// Checker-level Dist operator dispatch.
/// Scalar * Dist (either side) is kappa_k(c*X) = c^k kappa_k(X) -- pure
/// multilinearity, exact with NO independence requirement -- so it
/// dispatches in ANY expression position, including on Dist-typed function
/// parameters: the surface operand exprs are packed into the expansion
/// DistSynth.scaleExpr builds, and the whole block is re-inferred
/// (synthesize-and-infer). Dist +/- Dist is exact ONLY for independent
/// operands; until function-boundary independence licenses land
/// (`where indep(...)`), provenance is invisible in checker positions, so
/// +/- here steers to the module-level elaboration path (which checks
/// declared independence). dist * dist steers to the Wick machinery message.
and inferDistBinOp ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (op: BinOp) (left: Expr) (right: Expr) (lTy: IRType) (rTy: IRType) : TypeResult<TypedExpr> =
    let isScalarish t =
        match t with
        | IRTScalar _ | IRTUnitAnnotated (IRTScalar _, _) | IRTInfer _ -> true
        | _ -> false
    match op, lTy, rTy with
    | OpMul, IRTDist _, IRTDist _ ->
        Error (DistAlgebraErr "dist * dist is not defined: cumulants are additive under independent sums and multilinear under scalar scaling; products of random variables need the moment (Wick/Faa di Bruno) machinery")
    | OpMul, IRTDist (order, _, _), c when isScalarish c ->
        (match inferExpr env (Blade.Ppl.Elaborate.DistSynth.scaleExpr (env.Builder.FreshId()) right left order) with Ok t -> Ok t | Error e -> Error e)
    | OpMul, c, IRTDist (order, _, _) when isScalarish c ->
        (match inferExpr env (Blade.Ppl.Elaborate.DistSynth.scaleExpr (env.Builder.FreshId()) left right order) with Ok t -> Ok t | Error e -> Error e)
    | (OpAdd | OpSub), IRTDist (lo, _, _), IRTDist (ro, _, _) ->
        // Exact ONLY for independent operands: every cross pair of the two
        // provenance sets must be related under the declared relation  union  the
        // active `where indep` licenses (PPL-owned state). Empty provenance
        // is un-provable, not vacuously independent.
        if lo <> ro then
            Error (DistOrderDisagree ((if op = OpAdd then "+" else "-"), lo, ro))
        else
        let provL = provenanceOfSurface env left
        let provR = provenanceOfSurface env right
        if Set.isEmpty provL || Set.isEmpty provR then
            Error (DistAlgebraErr "dist + / -: cannot establish the operands' provenance -- combine dist bindings (or expressions built from them) so independence of their sources can be verified")
        else
            let missing =
                [ for s1 in provL do
                    for s2 in provR do
                      if not (Blade.Ppl.Elaborate.Independence.isRelated s1 s2) then yield (s1, s2) ]
            match missing with
            | (s1, s2) :: _ ->
                // Token-shaped sources ("func.param") mean unlicensed
                // parameters -- steer to the signature license, not to a
                // module-level declaration over internal token names.
                let steering =
                    if s1.Contains "." || s2.Contains "." then
                        "add a `where <alias>.indep(...)` license (with `import ppl as <alias>`) naming the two parameters to the enclosing function's signature"
                    else
                        $"declare `let _ = ppl.independent({s1}, {s2})` (module level) or a struct `where ppl.indep(...)`"
                Error (DistNotIndependent ((if op = OpAdd then "+" else "-"), s1, s2, steering))
            | [] ->
                let weight = if op = OpAdd then (fun _ -> 1.0) else (fun k -> if k % 2 = 0 then 1.0 else -1.0)
                (match inferExpr env (Blade.Ppl.Elaborate.DistSynth.combineExpr (env.Builder.FreshId()) weight left right lo) with Ok t -> Ok t | Error e -> Error e)
    | _ ->
        Error (DistOpUndefined (ppIRType lTy, ppIRType rTy))

/// atan2(y, x) and log_base(x, b): the two BINARY math intrinsics. Surface
/// form is a plain call, shadowable by a user binding exactly like the unary
/// intrinsics and `complex`.
///
/// MECHANISM. The typed node is an ordinary `TExprBinOp (Elementwise,
/// OpMath2 name, ...)` -- not a dedicated 2-argument node -- so the entire
/// existing binary pipeline carries them: the unit tables above, lowering's
/// op map, and codegen's binop emission, where they join `^` as the second
/// binop that renders as a CALL rather than infix. Nothing about loops,
/// captures or kernels had to learn a new shape.
///
/// ARRAY LIFT. An array operand makes the intrinsic elementwise, re-synthesized
/// as `method_for(...) <@> lambda(..) -> name(..) |> compute` and re-inferred --
/// the identical synthesize-and-infer route `complex(A, B)` takes, so zip
/// co-iteration, array/scalar broadcast in BOTH orders, packed storage and
/// codegen are the ones already proven for `A + B`.
///
/// UNITS are judged HERE, at the synthesis site, for both routes: the
/// synthesized lambda annotates its parameters Float64, which strips the
/// element signatures, so a check deferred into the body would see nothing.
/// The rules themselves live in unitRulesForOp's OpMath2 arms (atan2: one
/// shared signature, cancels; log_base: both dimensionless). Result is
/// dimensionless either way, so nothing needs stamping back onto the pipeline.
and inferBinaryIntrinsic ({ InferEntries.InferExpr = inferExpr } as ie) (env: TypeEnv) (name: string) (aExpr: Expr) (bExpr: Expr) : TypeResult<TypedExpr> =
    let sp = mergeSpan aExpr.Span bExpr.Span
    inferExpr env aExpr |> Result.bind (fun tA0 ->
    inferExpr env bExpr |> Result.bind (fun tB0 ->
        // R3: integer EnumIdx operands are observed as their KEYS.
        let keyArrA, keyArrB = intEnumArrayType env tA0.Type, intEnumArrayType env tB0.Type
        let badKind =
            [ (keyArrA, tA0, aExpr); (keyArrB, tB0, bExpr) ]
            |> List.tryPick (fun (k, t, e) ->
                if k && enumKeyStorageKind env t.Type <> "dense" then Some (enumKeyStorageKind env t.Type, e) else None)
        match badKind with
        | Some (kind, e) ->
            if e.Span.StartLine > 0 then setCurrentExprSpan e.Span
            Error (enumKeyStorageRefusal $"{name}()" kind)
        | None ->
        if (keyArrA || keyArrB) && (lookupVar "Int64" env).IsNone then
            inferBinaryIntrinsic ie env name (if keyArrA then enumKeyCastAst aExpr else aExpr)
                                          (if keyArrB then enumKeyCastAst bExpr else bExpr)
        else
        let tA, tB = observeEnumKeyTyped env tA0, observeEnumKeyTyped env tB0
        let rA = env.Subst.Resolve tA.Type
        let rB = env.Subst.Resolve tB.Type
        let isArr t = match t with ArrayElem _ -> true | _ -> false
        // Element units for an array operand, scalar units otherwise -- the two
        // routes below judge units off the same pair.
        let opUnits t =
            match t with
            | ArrayElem at -> IR.getUnits at.ElemType
            | other -> IR.getUnits other
        unitRulesForArrayOp $"{name} arguments" (OpMath2 name) (opUnits rA) (opUnits rB) None
        |> Result.bind (fun _ ->
            if isArr rA || isArr rB then
                // The kernel parameters take a FLOAT32 array's element width
                // (round 6: atan2 over Float32 is Float32); anything else Float64.
                let paramTyOf (t: IRType) =
                    match t with
                    | ArrayElem at ->
                        (match IR.stripUnits (env.Subst.Resolve at.ElemType) with
                         | IRTScalar ETFloat32 -> TyFloat32
                         | _ -> TyFloat64)
                    | _ -> TyFloat64
                let bparam n : LambdaParam =
                    { Name = n; Type = Some (paramTyOf (if n = "__m2a" then rA else rB)); Default = None; NameSpan = noSpan }
                let bvar n = mkExpr sp (ExprVar n)
                let bbody x y = mkExpr sp (ExprApp (mkExpr sp (ExprVar name), [x; y]))
                let apply former ps body =
                    match inferExpr env (mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                        former, mkExpr sp (ExprLambda (ps, None, body))))))) with
                    | Ok t -> Ok t
                    | Error e -> Error e
                match isArr rA, isArr rB with
                | true, true ->
                    apply (mkExpr sp (ExprMethodFor [mkExpr sp (ExprZip [aExpr; bExpr])]))
                          [bparam "__m2a"; bparam "__m2b"]
                          (bbody (bvar "__m2a") (bvar "__m2b"))
                | true, false ->
                    // Array <-> scalar broadcast. The scalar's SURFACE expr is
                    // embedded (not its typed node) so capture analysis sees the
                    // variable references, per the array/scalar binop arm's note.
                    apply (mkExpr sp (ExprMethodFor [aExpr])) [bparam "__m2a"] (bbody (bvar "__m2a") bExpr)
                | _ ->
                    apply (mkExpr sp (ExprMethodFor [bExpr])) [bparam "__m2b"] (bbody aExpr (bvar "__m2b"))
            else
                // SCALAR. Neither intrinsic has a std::complex overload, so a
                // complex operand is rejected the way floor/ceil reject one.
                let reject (t: IRType) =
                    match IR.stripUnits t with
                    | IRTScalar (ETComplex64 | ETComplex128) -> Some (IntrinsicNotComplex name)
                    | IRTScalar ETBool | IRTScalar ETString -> Some (IntrinsicNeedsNumeric name)
                    | t when (nonNumericOperandOf env t).IsSome -> Some (IntrinsicNeedsNumeric name)
                    | _ -> None
                match reject rA, reject rB with
                | Some e, _ -> Error e
                | _, Some e -> Error e
                | None, None ->
                    // An UNRESOLVED operand pins to Float64 -- the operand
                    // really is real, so pinning rejects a complex binding here
                    // instead of letting it reach codegen as
                    // std::atan2(complex, complex), a C++ error rather than a
                    // Blade one.
                    //
                    // NOT inside a lambda body, though. There the operand is a
                    // kernel parameter that apply-site unification has not bound
                    // yet, and binding it to BARE Float64 would erase the
                    // element's unit annotation -- after which buildApplyInfo's
                    // kernelBodyUnits walk re-runs this same op's unit rule
                    // against no signatures and silently accepts
                    // `zip(D: m, T: s) <@> lambda(d, t) -> atan2(d, t)`. Defer
                    // instead (what `log` does, and why `log` rejects that shape
                    // while the PINNING floor/ceil arm above does not): the
                    // param binds to the real element type and the unit walk
                    // sees it. The result type is Float64 either way, so
                    // deferring costs nothing else.
                    let pin (t: TypedExpr) =
                        match env.Subst.Resolve t.Type with
                        | IRTInfer _ when not env.InLambdaBody ->
                            unify env.Subst t.Type (IRTScalar ETFloat64)
                        | _ -> Ok ()
                    // Round 6: a literal-only operand takes the other's width.
                    let tA, tB = adaptLiteralOperands env (OpMath2 name) tA tB
                    pin tA |> Result.bind (fun () ->
                    pin tB |> Result.map (fun () ->
                        // Two FLOAT32 operands give a Float32 (formalism 2.4);
                        // otherwise Float64 regardless of operand widths: the C++
                        // overload set promotes integers, so atan2(1, 1) is a double.
                        let resElem =
                            match IR.stripUnits (env.Subst.Resolve tA.Type), IR.stripUnits (env.Subst.Resolve tB.Type) with
                            | IRTScalar ETFloat32, IRTScalar ETFloat32 -> ETFloat32
                            | _ -> ETFloat64
                        mkTyped (TExprBinOp (Elementwise, OpMath2 name, tA, tB)) (IRTScalar resElem)
                        |> widenLiteralOnlyByValue env.Subst))
        )))
