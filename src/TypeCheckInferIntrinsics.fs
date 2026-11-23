// The type checker's SCALAR INTRINSIC rules, outside the inference chain (cleanup round 2, K5): the compound and
// provider reads and writes, the math intrinsics (exp, sqrt, ...), abs, the complex accessors and constructor,
// fma, leaves, the random families and their addressed draws, and the display emitters. They type their operands
// through the chain's inferExpr and checkExpr, reached through InferEntries (TypeCheckInferEntries.fs): each rule
// takes the record first and binds the entry points it calls. (The display unit label stays in the chain: it
// reads the chain's treeViewSpine.)
module Blade.TypeCheckInferIntrinsics

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

let rec inferLoadCompound ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (varE: Expr) (maskE: Expr) (alias: string) : TypeResult<TypedExpr> =
    inferExpr env varE |> Result.bind (fun tVar ->
    inferExpr env maskE |> Result.bind (fun tMask ->
        match env.Subst.Resolve(tVar.Type), env.Subst.Resolve(tMask.Type) with
        | ArrayElem varArr, ArrayElem maskArr ->
            (match compoundViewType (env.Builder.FreshId()) varArr maskArr (IRLit IRLitUnit) with
             | Ok compoundTy ->
                 let aliasVi = (lookupVar alias env).Value
                 let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                 let tField = mkTyped (TExprField (tAlias, "load_compound", 0)) compoundTy
                 Ok (mkTyped (TExprApp (tField, [tVar; tMask])) compoundTy)
             | Error msg -> Error (BuiltinArgErr msg))
        | _ -> Error (BuiltinArgErr "load_compound expects two array arguments: the variable and an integer mask")))

// ---- Windowed packed read: alias.read_window(view, lo, hi) ----
// Materializes only the cells with every coordinate in [lo, hi): a
// translated sub-simplex, typed with leading packed extent hi-lo.
// Bounds are integer literals (the window is a compile-time shape).
and inferProviderReadWindow ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (args: Expr list) (alias: string) : TypeResult<TypedExpr> =
    (match args with
     | [operand; { Kind = ExprKind.ExprLit (LitInt lo) }; { Kind = ExprKind.ExprLit (LitInt hi) }] ->
         inferExpr env operand |> Result.bind (fun tE ->
             match env.Subst.Resolve tE.Type with
             | ArrayElem at ->
                 (match at.IndexTypes with
                  // A wreath lead passes the packed test below, but has no
                  // window type: a sub-simplex of a wreath class is not a
                  // wreath class -- every level would need restricting at
                  // once, and level i's ground set is level (i-1)'s cells,
                  // not [lo,hi). spec_version 2 agrees on the store side.
                  | lead :: _ when lead.Symmetry = SymWreath ->
                      Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf lead),
                                                      "z.read_window (a wreath class has no translated sub-class to window into)"))
                  | lead :: rest when lead.Symmetry <> SymNone && lead.Rank >= 2 ->
                      (match lead.Extent with
                       | IRLit (IRLitInt n) when lo >= 0L && lo < hi && hi <= n ->
                           let winIdx = { lead with Id = env.Builder.FreshId()
                                                    Extent = IRLit (IRLitInt (hi - lo)) }
                           let winTy = mkArrayLike { at with IndexTypes = winIdx :: rest }
                           let aliasVi = (lookupVar alias env).Value
                           let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                           let tField = mkTyped (TExprField (tAlias, "read_window", 0)) winTy
                           let tLo = mkTyped (TExprLit (LitInt lo)) (IRTScalar ETInt64)
                           let tHi = mkTyped (TExprLit (LitInt hi)) (IRTScalar ETInt64)
                           Ok (mkTyped (TExprApp (tField, [tE; tLo; tHi])) winTy)
                       | IRLit (IRLitInt n) ->
                           Error (ProviderReadWindowBounds (alias, lo, hi, n))
                       | _ ->
                           Error (ProviderReadWindowLiteralExtent alias))
                  | _ ->
                      Error (ProviderReadWindowPacked alias))
             | _ -> Error (ProviderReadWindowNeedsVar alias))
     | _ -> Error (ProviderReadWindowArgs alias))

// ---- Provider write: alias.write("path", A) ----
// The source must be a named array binding (the store variable takes
// its name); the path must be a string literal (the store is created
// at a compile-time-known location, mirroring alias.load). Types as
// unit; the generic application node is kept -- lowering's
// tryProviderWrite intercepts the shape into a ProviderWriteSpec.
and inferProviderWrite ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (args: Expr list) (alias: string) : TypeResult<TypedExpr> =
    (match args with
     | [{ Kind = ExprKind.ExprLit (LitString path) }; valueE] ->
         (match valueE.Kind with
          | ExprKind.ExprVar _ ->
              inferExpr env valueE |> Result.bind (fun tValue ->
                  match env.Subst.Resolve(tValue.Type) with
                  | ArrayElem _ ->
                      let aliasVi = (lookupVar alias env).Value
                      let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                      let tField = mkTyped (TExprField (tAlias, "write", 0)) IRTUnit
                      let tPath = mkTyped (TExprLit (LitString path)) (IRTScalar ETString)
                      Ok (mkTyped (TExprApp (tField, [tPath; tValue])) IRTUnit)
                  | _ -> Error (ProviderWriteNeedsArray alias))
          | _ -> Error (ProviderWriteNamedBinding alias))
     | _ -> Error (ProviderWriteArgs alias))

// ---- Scalar math intrinsics: exp(x), sqrt(x), ... ----
// Surface form is a plain call; the name is rewritten to OpMath only
// when it is NOT user-bound (a user `function exp(...)` or a local
// binding named `exp` shadows the intrinsic). Scalar-only: mapping over
// an array is a kernel's job, so an array operand is a type error with
// steering. Result is Float64 (Float32 operands widen; Int operands are
// promoted by the C++ overload set).
and inferMathIntrinsicCall ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (arg: Expr) (name: string) : TypeResult<TypedExpr> =
    // R3: a value of an integer EnumIdx is observed as its key (Int64).
    inferExpr env arg |> Result.map (observeEnumKeyTyped env) |> Result.bind (fun tArg ->
        // THE NUMBERS-ONLY RULE's generic half (formalism 2.4): an operand whose
        // type is a generic declaration's variable -- a top-level function's, or
        // a NESTED one's (a lambda body: InLambdaBody does not matter here) --
        // must be a number at every instance, whatever reaches the return.
        (match IR.stripUnits (env.Subst.Resolve tArg.Type), env.CurrentGenericObligations with
         | IRTInfer vid, Some acc when env.Subst.IsPolymorphicId vid && (env.Subst.GetLiteralDefault vid).IsNone ->
             acc.Add(GONumericOperand (vid, name + "()", false))
         | _ -> ())
        recordOpenNumericDemand env (name + "()") false tArg
        match env.Subst.Resolve tArg.Type with
        | ArrayElem arr ->
            // ARRAY operand: lift elementwise. `cos(A)` / `A |> cos`
            // (same ExprApp after the pipe desugar) re-synthesizes as
            // `(method_for(A) <@> cos) |> compute` -- the kernel-position
            // spelling that already exists -- and re-infers, exactly like
            // the array/scalar broadcast arm for operators. EAGER like
            // that arm too: a call is a value everywhere else, and
            // `A + B` materializes, so `cos(A)` must not hand back a
            // deferred loop. The deferred spelling stays `cos <@> A`.
            //
            // Element-CLASS errors are judged here, where the message can
            // name the intrinsic against the array (the synthesized map
            // would report the same facts, one hop removed): floor/ceil
            // and friends still refuse complex ELEMENTS, and bool/string
            // elements are not numbers. An unresolved element type falls
            // through to the synthesis -- apply-site unification inside
            // the map resolves it the same way it does for `cos <@> A`.
            (match IR.stripUnits (env.Subst.Resolve arr.ElemType) with
             | IRTScalar (ETComplex64 | ETComplex128) when not (isComplexMathIntrinsic name) ->
                 Error (IntrinsicNotComplex name)
             | IRTScalar ETBool | IRTScalar ETString ->
                 Error (IntrinsicNeedsNumeric name)
             // The numbers-only rule (formalism 2.4): no other non-number either.
             | e when (nonNumericOperandOf env e).IsSome ->
                 Error (IntrinsicNeedsNumeric name)
             // R3: an array of integer EnumIdx keys: the intrinsic of each
             // KEY, as a Float64 (the intrinsic's result class; an Int64
             // array's lifted sqrt types its kernel Float64 as well, by
             // buildApplyInfo's R18 re-stamp).
             | e when intEnumValueType env e && not (List.contains (enumKeyStorageKind env tArg.Type) [ "dense"; "compound"; "grouped" ]) ->
                 if arg.Span.StartLine > 0 then setCurrentExprSpan arg.Span
                 Error (enumKeyStorageRefusal $"{name}()" (enumKeyStorageKind env tArg.Type))
             | e when intEnumValueType env e && (lookupVar "Float64" env).IsNone ->
                 let sp = arg.Span
                 let keysF = mkExpr sp (ExprApp (mkExpr sp (ExprVar "Float64"), [ arg ]))
                 (match inferExpr env (mkExpr sp (ExprApp (mkExpr sp (ExprVar name), [ keysF ]))) with Ok t -> Ok t | Error e -> Error e)
             | _ ->
                 let sp = arg.Span
                 let synth =
                     mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                         mkExpr sp (ExprMethodFor [arg]),
                         mkExpr sp (ExprVar name)))))
                 (match inferExpr env synth with Ok t -> Ok t | Error e -> Error e))
        | resolvedArg when (match IR.stripUnits resolvedArg with
                            | IRTScalar (ETComplex64 | ETComplex128) -> true
                            | _ -> false) ->
            // exp/log/sqrt and the trig/hyperbolic families have std::complex
            // overloads and preserve the complex type; floor/ceil have no
            // complex overload and stay rejected.
            //
            // Classified on the STRIPPED base, because a complex operand
            // routinely arrives UNIT-ANNOTATED: `exp(i * w * t_zero)` over
            // `w : 1/day`, `t_zero : day` types as `Complex128<...>`, which
            // a bare `IRTScalar` pattern does not match. That fell through
            // to the general arm below, which hardcodes a Float64 RESULT --
            // so the whole expression went real and a later `real(z)` was
            // rejected with BL3007 pointing at the accessor rather than at
            // the exp. Same table as the real path decides the signature;
            // only the WIDTH comes from the operand.
            if not (isComplexMathIntrinsic name) then
                Error (IntrinsicNotComplex name)
            elif env.InLambdaBody && typedExprHasProvisionalUnits env tArg then
                // Provisional annotation inside a lambda body: defer the
                // unit judgement to buildApplyInfo's kernelBodyUnits, for
                // the reason the real-operand arm below spells out. Deferring
                // means dropping the SIGNATURE, never the complex width.
                Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) (IR.stripUnits resolvedArg))
            else
                unitRulesForUnaryOp (OpMath name) (IR.getUnits resolvedArg)
                |> Result.map (fun resUnits ->
                    let baseTy = IR.stripUnits resolvedArg
                    let resTy =
                        match resUnits with
                        | Some u -> IRTUnitAnnotated (baseTy, u)
                        | None -> baseTy
                    mkTyped (TExprUnaryOp (OpMath name, tArg)) resTy)
        | IRTScalar ETBool | IRTScalar ETString ->
            Error (IntrinsicNeedsNumeric name)
        // The numbers-only rule (formalism 2.4): a struct, a tuple, a sum-type
        // value or a function is not a number either.
        | t when (nonNumericOperandOf env t).IsSome ->
            Error (IntrinsicNeedsNumeric name)
        | IRTInfer vid when isComplexMathIntrinsic name ->
            // Unresolved operand: DEFER, since apply-site unification may
            // later bind it COMPLEX (exp/log/sqrt/trig preserve complex),
            // so pinning Float64 now would reject complex kernels; the
            // kernel re-stamp in buildApplyInfo corrects the result type.
            //
            // In a named GENERIC body the operand is a signature variable
            // and nothing re-stamps: the result keeps the variable's type,
            // exact for every float and complex instance and a silent
            // truncation for an integer one (`function r(x: T^0) -> T^0 =
            // sqrt(x)` at Int64 emitted `int64_t r(int64_t x) { return
            // std::sqrt(x); }`). Record the demand; each call judges it.
            if not env.InLambdaBody && env.Subst.IsPolymorphicId vid then
                (match env.CurrentGenericObligations with
                 | Some acc ->
                     acc.Add(GOFractionalMath (vid, name))
                 | None -> ())
            Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) tArg.Type)
        // The same in a named GENERIC body for the REAL-only intrinsics
        // (floor/ceil/log10/lgamma/digamma): the operand is a signature
        // variable, and an array instance lifts the intrinsic over its
        // elements exactly as a concrete array operand does -- so it is
        // not pinned to a Float64 scalar (that refused every array call
        // of `f(x: T) = Int64(floor(x))`, while `Int64(floor(A))` keeps
        // A's shape, casts/541). The result keeps the variable's type and
        // each call judges the element (GOFractionalMath): an integer
        // instance would truncate a Float64 result, and a complex one
        // has no overload. The cast around it still sees the floor, so
        // its rounding license flows through (castRoundingLicensed).
        | IRTInfer vid when not env.InLambdaBody && env.Subst.IsPolymorphicId vid
                            && env.CurrentGenericObligations.IsSome ->
            env.CurrentGenericObligations.Value.Add(GOFractionalMath (vid, name))
            Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) tArg.Type)
        | IRTInfer _ when not env.InLambdaBody ->
            // floor/ceil/log10 have no complex overload -- the operand
            // really is real; pin it to Float64, the intrinsic's natural
            // domain, which also rejects a later complex binding here
            // instead of letting it reach codegen as std::floor(complex).
            unify env.Subst tArg.Type (IRTScalar ETFloat64) |> Result.bind (fun () ->
            Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) (IRTScalar ETFloat64)))
        | IRTInfer _ ->
            // NOT inside a lambda body, though -- the same carve-out
            // inferBinaryIntrinsic's `pin` makes. There the operand is a
            // kernel parameter that apply-site unification has not bound
            // yet, and binding it to BARE Float64 would erase the element's
            // unit annotation -- after which buildApplyInfo's
            // kernelBodyUnits walk re-runs this op's unit rule against no
            // signature and silently accepts `D: m <@> lambda(d) ->
            // floor(d)`. DEFER (what the complex arm above does, and why
            // `log` rejects that shape while the PINNING arm did not): the
            // param binds to the real element type and the unit walk sees
            // it. The complex operand the pin used to catch is caught after
            // unification instead, by findBadComplexIntrinsic.
            //
            // Result is bare Float64, not the operand's variable: these
            // intrinsics are Float64-valued at any operand width, it
            // matches the provisional-annotation defer arm below, and the
            // kernel re-stamp's real-operand arm normalizes to it anyway.
            Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) (IRTScalar ETFloat64))
        | IRTUnitAnnotated _ when env.InLambdaBody && typedExprHasProvisionalUnits env tArg ->
            // PROVISIONAL annotation inside a lambda body: something the
            // argument depends on is still an unresolved inference variable
            // (a kernel param before apply-site unification), and an
            // unresolved operand contributes "no units" to first-pass
            // typing -- so the annotation seen here can be exactly a
            // dimensioned CAPTURE's signature even when the product cancels
            // once the param's unit is known (`lambda(w) -> cos(w * tz)`,
            // w : 1/day, tz : day).
            // DEFER: buildApplyInfo's kernelBodyUnits reruns the same
            // per-op table after param unification and rejects for real.
            // Result is typed bare Float64, matching the accept path below.
            //
            // The dependency is chased THROUGH kernel-local lets
            // (typedExprHasProvisionalUnits): `let w = two_pi * fq;
            // cos(w * t_zero)` is the same situation with `w` in the way,
            // and the plain node-type walk stopped at `w` -- resolved, bare
            // -- and rejected. Pass 2 models those lets in its `bound` map,
            // so the deferred judgement is the correct one.
            Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) (IRTScalar ETFloat64))
        | resolvedArg ->
            // Unit propagation at scalar position, same table as the
            // kernel-body walk (unitRulesForUnaryOp): sqrt halves
            // all-even exponents; floor/ceil and the transcendentals
            // are not homogeneous and REJECT a dimensioned operand.
            // A FLOAT32 operand gives a Float32 result (formalism 2.4: a
            // Float32 transcendental is rounded once to Float32); Float64
            // and integer operands give Float64.
            let resElem =
                match IR.stripUnits resolvedArg with
                | IRTScalar ETFloat32 -> ETFloat32
                | _ -> ETFloat64
            unitRulesForUnaryOp (OpMath name) (IR.getUnits resolvedArg)
            |> Result.map (fun resUnits ->
                let resTy =
                    match resUnits with
                    | Some u -> IRTUnitAnnotated (IRTScalar resElem, u)
                    | None -> IRTScalar resElem
                // A literal-only call is widened by its exact value
                // (`exp(100.0)` is past Float32's range: Float64).
                mkTyped (TExprUnaryOp (OpMath name, tArg)) resTy |> widenLiteralOnlyByValue env.Subst))

// ---- abs(x): polymorphic numeric intrinsic ----
// Deliberately NOT in mathIntrinsics (those are real-valued, typed
// Float64, and carry derivative rules); abs preserves its operand's
// numeric type and renders as std::abs, whose C++ overload set covers
// int64 and double. Ubiquitous in dependent field bounds
// (`in abs(l1 - l2) .. l1 + l2 + 1`).
and inferAbsCall ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (arg: Expr) : TypeResult<TypedExpr> =
    // R3: a value of an integer EnumIdx is observed as its key (Int64).
    inferExpr env arg |> Result.map (observeEnumKeyTyped env) |> Result.bind (fun tArg ->
        match env.Subst.Resolve tArg.Type with
        | IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64) as sc ->
            Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) sc)
        | IRTScalar (ETComplex64 | ETComplex128 as cet) ->
            // abs of a complex is its real magnitude (std::abs(complex<T>)
            // returns T): the COMPONENT width -- Float32 for a Complex64,
            // Float64 for a Complex128 (round 6).
            Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) (IRTScalar (if cet = ETComplex64 then ETFloat32 else ETFloat64)))
        | IRTInfer vid ->
            // The numbers-only rule: a generic operand must be a number at
            // every instance (an open kernel operand: the post-zonk sweep).
            (match env.CurrentGenericObligations with
             | Some acc when env.Subst.IsPolymorphicId vid -> acc.Add(GONumericOperand (vid, "abs()", false))
             | _ -> ())
            Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) tArg.Type)
        | IRTUnitAnnotated (IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64), _) as t ->
            // abs preserves the operand's unit annotation -- a unitful
            // scalar is a perfectly good abs operand.
            Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) t)
        | ArrayElem arr ->
            // ARRAY operand: lift elementwise, same rule as the math
            // intrinsics and complex accessors above. The element class is
            // judged here so the message still names abs; anything abs
            // accepts as a scalar, it accepts as an element.
            (match IR.stripUnits (env.Subst.Resolve arr.ElemType) with
             | IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128)
             | IRTInfer _ ->
                 let sp = arg.Span
                 let synth =
                     mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                         mkExpr sp (ExprMethodFor [arg]),
                         mkExpr sp (ExprVar "abs")))))
                 (match inferExpr env synth with Ok t -> Ok t | Error e -> Error e)
             // R3: an array of integer EnumIdx keys: abs of each KEY.
             | t when intEnumValueType env t && not (List.contains (enumKeyStorageKind env tArg.Type) [ "dense"; "compound"; "grouped" ]) ->
                 if arg.Span.StartLine > 0 then setCurrentExprSpan arg.Span
                 Error (enumKeyStorageRefusal "abs()" (enumKeyStorageKind env tArg.Type))
             | t when intEnumValueType env t && (lookupVar "Int64" env).IsNone ->
                 (match inferExpr env (mkExpr arg.Span (ExprApp (mkExpr arg.Span (ExprVar "abs"), [ enumKeyCastAst arg ]))) with Ok t -> Ok t | Error e -> Error e)
             | other ->
                 Error (AbsNeedsNumericScalar $"an array of {ppIRType other} elements"))
        | other ->
            Error (AbsNeedsNumericScalar (ppIRType other)))

// ---- real(z) / imag(z) / arg(z): complex component/phase accessors ----
// Plain-call intrinsics (shadowable by a user binding, like abs). Require a
// complex scalar operand -- real/imag on a real value is trivially the
// identity/zero and a likely mistake, so we steer instead. real/imag yield
// the component width (Complex128 -> Float64, Complex64 -> Float32); arg is
// a Float64 angle. Emit std::real/std::imag/std::arg via the generic unary
// codegen arm.
and inferComplexPartCall ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (arg: Expr) (name: string) : TypeResult<TypedExpr> =
    inferExpr env arg |> Result.bind (fun tArg ->
        let op = match name with
                 | "real" -> OpReal
                 | "imag" -> OpImag
                 | _ -> OpArg
        match env.Subst.Resolve tArg.Type with
        | resolvedArg when (match IR.stripUnits resolvedArg with
                            | IRTScalar (ETComplex64 | ETComplex128) -> true
                            | _ -> false) ->
            // Stripped-base classification, for the reason the math-intrinsic
            // arm above gives: a complex value that carries a signature
            // (`z : Complex128<volts>`) is IRTUnitAnnotated, and the bare
            // patterns missed it -- so `real(z)` fell to the catch-all and
            // rejected its own operand as "not complex".
            //
            // The component keeps the signature (real/imag are degree 1);
            // `arg` is an angle and drops it. Same table, and the same
            // widths as before: Complex64 -> Float32 components, everything
            // else Float64.
            let width =
                match IR.stripUnits resolvedArg with
                | IRTScalar ETComplex64 when name <> "arg" -> ETFloat32
                | _ -> ETFloat64
            unitRulesForUnaryOp op (IR.getUnits resolvedArg)
            |> Result.map (fun resUnits ->
                let resTy =
                    match resUnits with
                    | Some u -> IRTUnitAnnotated (IRTScalar width, u)
                    | None -> IRTScalar width
                mkTyped (TExprUnaryOp (op, tArg)) resTy)
        | IRTInfer _ ->
            // Unresolved operand (unannotated kernel/lambda parameter):
            // DEFER the complex requirement -- the apply-site unification
            // binds the param to the iterated element type. Result is
            // provisionally Float64 (the Complex128-operand answer); the
            // kernel re-stamp corrects the Complex64 width (-> Float32
            // components).
            Ok (mkTyped (TExprUnaryOp (op, tArg)) (IRTScalar ETFloat64))
        | ArrayElem arr ->
            // COMPLEX array operand: lift elementwise, same synthesis and
            // same eagerness as the math-intrinsic arm above -- `real(Z)`
            // / `Z |> real` become `(method_for(Z) <@> real) |> compute`.
            // A REAL-elements array keeps the scalar arm's steer: real()
            // of a real value is the identity and a likely mistake, and
            // that judgment does not change because there are many of
            // them. Unresolved elements defer into the synthesis, where
            // apply-site unification settles them as it does for
            // `real <@> Z`.
            (match IR.stripUnits (env.Subst.Resolve arr.ElemType) with
             | IRTScalar (ETComplex64 | ETComplex128) | IRTInfer _ ->
                 let sp = arg.Span
                 let synth =
                     mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                         mkExpr sp (ExprMethodFor [arg]),
                         mkExpr sp (ExprVar name)))))
                 (match inferExpr env synth with Ok t -> Ok t | Error e -> Error e)
             | other ->
                 Error (IntrinsicNeedsComplex (name, $"an array of {ppIRType other} elements")))
        | other ->
            Error (IntrinsicNeedsComplex (name, ppIRType other)))

// ---- leaves(T): the tree pool, retyped onto its derived dense leaf axis ----
// ZERO-COPY and zero-node: it returns the SAME typed expression with a new
// `.Type`. A tree binding is already an ordinary rank-1 dense Array<T,1>
// whose cells are the leaves in preorder (P3), and LeafIdx<S> is a plain
// Idx<cardinality>, so the two types describe byte-identical storage and the
// retype is a statement about ADDRESSING, not about data. Nothing is
// emitted, nothing is copied, and neither back end learns a thing.
//
// This is what makes the method_for refusal permanently cheap rather than a
// standing limitation: `method_for(leaves(T)) <@> f` is an ordinary dense
// loop over an ordinary dense array, so vectorization, omp, BLAS routing and
// fusion all apply unchanged, and no tree slot ever enters a loop former.
//
// ONE-WAY on purpose. The inverse (`retree`) would have to CLAIM that a
// dense array's cells are a particular shape's leaves in preorder, and
// nothing checks that claim -- it is a `where`-clause-shaped assertion, not
// a transform. The tree type is where that claim gets made, at construction.
//
// Plain-call intrinsic, shadowable like abs/complex: `leaves` is a plausible
// user variable name, and the lookupVar guard is what keeps this from
// stealing it.
and inferLeavesCall ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (arg: Expr) : TypeResult<TypedExpr> =
    inferExpr env arg |> Result.bind (fun tArr ->
        match env.Subst.Resolve tArr.Type with
        | ArrayElem at ->
            (match at.IndexTypes with
             | [ ix ] when ix.IxKind = IxKTree ->
                 // The leaf axis IS this record's extent -- P2 stamps the
                 // leaf count there -- so there is no re-derivation and no
                 // second source of truth for the cardinality, and
                 // `leaves(T)` cannot disagree with `extents(T)`.
                 let leafAxis =
                     { ix with Id = env.Builder.FreshId()
                               Tag = None; IxKind = IxKPlain }
                 Ok { tArr with Type = mkArrayLike { at with IndexTypes = [ leafAxis ] } }
             // A tree slot BESIDE other slots is the hybrid form. The pool
             // is not a bare leaf sequence there, so the retype has no
             // meaning yet -- same residual-view boundary the hybrid READ
             // sits behind. Routed through TreeIdxUnsupported because there
             // IS a class to name.
             | _ when at.IndexTypes |> List.exists (fun ix -> ix.IxKind = IxKTree) ->
                 let rendered =
                     at.IndexTypes
                     |> List.tryPick (fun ix -> match ix with TreeIdxLike r -> Some r | _ -> None)
                     |> Option.defaultValue "TreeIdx<?>"
                 Error (TreeIdxUnsupported (rendered, "leaves(), which takes an array whose SOLE index slot is a tree"))
             // No tree slot at all: there is no class to name, so the
             // TreeIdxUnsupported template ("<X> is a declarable index
             // class...") would read as nonsense about the user's plain
             // array. Answer in its own words instead.
             | _ ->
                 Error (BuiltinArgErr $"leaves(A) takes a TREE-slotted array and retypes its flat leaf pool onto the derived dense leaf axis LeafIdx<shape>. This argument's type is {ppIRType (env.Subst.Resolve tArr.Type)}, which has no tree slot -- it is already an ordinary dense array, so there is nothing to retype: use it directly."))
        | _ -> Error (BuiltinArgErr "leaves(A) takes a tree-slotted ARRAY: it retypes the tree's flat leaf pool onto its derived dense leaf axis (LeafIdx<shape>), which is an ordinary Idx over the leaf count. This argument is not an array."))

and inferFmaCall ({ InferEntries.InferExpr = inferExpr; InferEntries.CheckExpr = checkExpr }) (env: TypeEnv) (aExpr: Expr) (bExpr: Expr) (cExpr: Expr) : TypeResult<TypedExpr> =
    // Round 6: three FLOAT32 scalars (a literal-only one taking the
    // others' width) are a Float32 fma; everything else is the Float64 one.
    let f32 =
        match inferExpr env aExpr, inferExpr env bExpr, inferExpr env cExpr with
        | Ok a, Ok b, Ok c ->
            let isF32 (t: TypedExpr) = (match env.Subst.Resolve t.Type with IRTScalar ETFloat32 -> true | _ -> false)
            let nonLit = [ a; b; c ] |> List.filter (fun t -> (literalOnlyElem env.Subst t).IsNone)
            if not nonLit.IsEmpty && nonLit |> List.forall isF32 then
                match adaptLiteralPeers env [ a; b; c ] with
                | [ a; b; c ] when isF32 a && isF32 b && isF32 c ->
                    Some (mkTyped (TExprFma (a, b, c)) (IRTScalar ETFloat32))
                | _ -> None
            else None
        | _ -> None
    match f32 with
    | Some node -> Ok node
    | None ->
    let f64 = IRTScalar ETFloat64
    checkExpr env f64 aExpr |> Result.bind (fun tA ->
    checkExpr env f64 bExpr |> Result.bind (fun tB ->
    checkExpr env f64 cExpr |> Result.map (fun tC ->
        mkTyped (TExprFma (tA, tB, tC)) f64)))

// ---- complex(re, im): complex literal constructor ----
// The one way to construct a complex value. As a plain call this
// composes under any operator without the precedence trap a 2-tuple
// cast form would hit (`a * (re, im) : T` binding the cast outside the
// multiply). Plain-call intrinsic, shadowable like abs/real. Components
// must be float-typed (no implicit int -> float promotion at
// construction time). Infers Complex128; checking against an expected
// Complex64 adopts the narrow width (checkExpr arm).
and inferComplexCtorCall ({ InferEntries.InferExpr = inferExpr; InferEntries.CheckExpr = checkExpr }) (env: TypeEnv) (reExpr: Expr) (imExpr: Expr) : TypeResult<TypedExpr> =
    // THE WIDTH COMES FROM THE COMPONENTS (round 6): two unit-free float
    // SCALARS of one width build that width's complex -- Float32 pair a
    // Complex64, Float64 pair a Complex128 -- a literal-only component
    // taking the other's width (adaptLiteralPeers), two literal-only ones
    // joining to the widest default (`complex(1.0, 2.0)` is a defaulted
    // Complex64). Anything else (arrays, units, open components, mixed
    // widths) takes the original Float64-checked path below.
    let scalarFloat (t: TypedExpr) =
        match env.Subst.Resolve t.Type with
        | IRTScalar (ETFloat32 | ETFloat64 as et) -> Some et
        | _ -> None
    let byWidth =
        match inferExpr env reExpr, inferExpr env imExpr with
        | Ok r0, Ok i0 when (scalarFloat r0).IsSome && (scalarFloat i0).IsSome ->
            (match adaptLiteralPeers env [ r0; i0 ] with
             | [ r; i ] ->
                 (match scalarFloat r, scalarFloat i with
                  | Some a, Some b when a = b ->
                      Some (mkTyped (TExprComplexLit (r, i)) (IRTScalar (if a = ETFloat32 then ETComplex64 else ETComplex128)))
                  | _ -> None)
             | _ -> None)
        | _ -> None
    match byWidth with
    | Some node -> Ok node
    | None ->
    (match checkExpr env (IRTScalar ETFloat64) reExpr, checkExpr env (IRTScalar ETFloat64) imExpr with
     | Ok tRe, Ok tIm -> Ok (mkTyped (TExprComplexLit (tRe, tIm)) (IRTScalar ETComplex128))
     | scalarRe, scalarIm ->
        // ARRAY LIFT. A component that is an ARRAY makes `complex` an
        // elementwise constructor, lifted exactly the way the elementwise
        // binops lift themselves (inferBinOp's zip and array/scalar arms):
        // re-synthesize as `method_for(...) <@> lambda(..) -> complex(..)
        // |> compute` and re-drive inference, so the zip/shape rules, the
        // loop machinery and codegen are the ones already proven for
        // `A + B` -- and the result is literally the workaround users
        // write by hand today. Both arrays co-iterate; one array against
        // a scalar broadcasts (the scalar's SURFACE expr is embedded so
        // capture analysis sees its variable references, per the
        // array/scalar binop arm's note). Only reached once the scalar
        // construction has already been rejected, so scalar `complex` is
        // unchanged.
        let arrayOperand (e: Expr) =
            match inferExpr env e with
            | Ok t -> (match env.Subst.Resolve t.Type with ArrayElem _ -> true | _ -> false)
            | Error _ -> false
        let sp = mergeSpan reExpr.Span imExpr.Span
        let cparam n : LambdaParam = { Name = n; Type = Some TyFloat64; Default = None; NameSpan = noSpan }
        let cvar n = mkExpr sp (ExprVar n)
        let cbody re im = mkExpr sp (ExprApp (mkExpr sp (ExprVar "complex"), [re; im]))
        let apply former ps body =
            match inferExpr env (mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                former, mkExpr sp (ExprLambda (ps, None, body))))))) with
            | Ok t -> Ok t
            | Error e -> Error e
        match arrayOperand reExpr, arrayOperand imExpr with
        | true, true ->
            apply (mkExpr sp (ExprMethodFor [mkExpr sp (ExprZip [reExpr; imExpr])]))
                  [cparam "__cre"; cparam "__cim"]
                  (cbody (cvar "__cre") (cvar "__cim"))
        | true, false ->
            apply (mkExpr sp (ExprMethodFor [reExpr])) [cparam "__cre"] (cbody (cvar "__cre") imExpr)
        | false, true ->
            apply (mkExpr sp (ExprMethodFor [imExpr])) [cparam "__cim"] (cbody reExpr (cvar "__cim"))
        | false, false ->
            // Neither component is an array: report the scalar rejection.
            match scalarRe, scalarIm with
            | Error e, _ -> Error e
            | _, Error e -> Error e
            | _ -> Error (ComplexArity 2))

// ---- __rand_<fam>(key, p1, .., pk, d1, ..., dn): rand module ----
// Compiler-internal (double-underscore reserved): emitted by the `rand`
// elaboration stage from `alias.<fam>(key, params.., shape)`. `key` is an
// Int64 stream key; the next `nPars` args are the family's RUNTIME Float64
// scalar parameters (any Float64-typed expression -- only the shape must be
// static); the trailing args are the (elaborator-resolved) static extents.
// Self-typed as a dense array of that shape -- no annotation needed. The
// element type is Float64 for every scalar-parameter family, including the
// integer-valued poisson and bernoulli, and Int64 for `categorical` alone
// (see the element-type note in cpp/rand_runtime.hpp: its draws are
// subscripts, not measurements). Lowering records (kind, key, pars, weights)
// in RandomInits; codegen emits allocate<> + the runtime blade_rand fill,
// and picks the C++ pool type straight off this ElemType.
//
// `categorical` also carries the ARRAY parameter channel: its single
// non-shape argument is a rank-1 Float64 weights array, and this arm is
// where its extent is PINNED -- the extent has to be a static literal
// because codegen passes a compile-time length beside the pool pointer and
// the interpreter mirror needs the same length to scan. A symbolic extent
// is refused here rather than producing a fill whose k is unknown.
//
// The per-family parameter count is fixed HERE (not by the elaborator), so
// it is enforced on the intrinsic itself and any future direct emitter of
// __rand_* -- e.g. the ppl module -- is held to the same arity.
and inferRandFamilyCall ({ InferEntries.InferExpr = inferExpr; InferEntries.CheckExpr = checkExpr }) (env: TypeEnv) (fn: string) (rest: Expr list) (keyE: Expr) : TypeResult<TypedExpr> =
    // nPars = scalar Float64 parameters; hasWeights = the array channel.
    // No family uses both today, but the two are counted independently so
    // the argument split does not assume that.
    //
    // The `_at` families (plan-fortran-killer-2 section 5) are the same
    // eight transforms ADDRESSED per sample: two more Int64 arguments
    // right after the key -- the stream key and the sample offset -- and
    // the runtime kind is the base kind with the suffix, so codegen and
    // the interpreter mirror dispatch on the name alone.
    let indexed = fn.EndsWith "_at"
    let baseFn = if indexed then fn.Substring(0, fn.Length - 3) else fn
    let kind0, nPars, hasWeights =
        match baseFn with
        | "__rand_uniform"     -> "uniform", 0, false
        | "__rand_normal"      -> "normal", 0, false
        | "__rand_exponential" -> "exponential", 1, false
        | "__rand_gamma"       -> "gamma", 2, false
        | "__rand_poisson"     -> "poisson", 1, false
        | "__rand_bernoulli"   -> "bernoulli", 1, false
        | "__rand_categorical" -> "categorical", 0, true
        | _                    -> "beta", 2, false
    let kind = if indexed then kind0 + "_at" else kind0
    // The address channel: (stream, offset) right after the key.
    let addrArgs, rest =
        if indexed && List.length rest >= 2 then List.splitAt 2 rest
        else ([], rest)
    if indexed && addrArgs.Length <> 2 then
        Error (ArgCountErr $"rand.{kind}: expected rand.{kind}(key, stream, offset, ..., shape) -- the stream key and the sample offset follow the key")
    else
    // Surface order is key, [stream, offset], [weights], scalar pars.., shape.
    let nLead = nPars + (if hasWeights then 1 else 0)
    if List.length rest <= nLead then
        Error (ArgCountErr $"rand.{kind}: expected {nLead} distribution parameter(s) and a shape")
    else
    let leadArgs, dimArgs = List.splitAt nLead rest
    let weightsArg = if hasWeights then Some (List.head leadArgs) else None
    let parArgs = if hasWeights then List.tail leadArgs else leadArgs
    // Extents must be static ints (the elaborator resolves them to literals).
    // Range-checked BEFORE the narrowing to `int`: a direct call with an
    // extent past Int32 wrapped silently (the rand/ppl surface paths
    // refuse it in their elaborators; this is the intrinsic's own check).
    let dimResults =
        dimArgs |> List.map (fun d ->
            match d.Kind with
            | ExprKind.ExprLit (LitInt n) when n > int64 System.Int32.MaxValue ->
                Error $"rand.{kind}: shape extent {n} is too large (at most {System.Int32.MaxValue} per axis)"
            | ExprKind.ExprLit (LitInt n) when n > 0L -> Ok (int n)
            | ExprKind.ExprLit (LitInt n) -> Error $"rand.{kind}: shape extents must be positive (got {n})"
            | _ -> Error $"rand.{kind}: shape must be a static positive int (or list of them)")
    match dimResults |> List.fold (fun acc r -> match acc, r with Ok xs, Ok x -> Ok (xs @ [x]) | Error e, _ -> Error e | _, Error e -> Error e) (Ok []) with
    | Error e -> Error (BuiltinArgErr e)
    | Ok dims ->
        // The weights channel: inferred (not checked against a demand --
        // its extent is what we are trying to LEARN), then required to be a
        // rank-1 Float64 array with a static positive extent. AnyPrimElem
        // so a unit-annotated Float64 still passes: units erase at codegen
        // and the pool is a `double` pool either way.
        // Split out of the pipeline below so each refusal is one flat arm:
        // rank, element type and extent-staticness are three separate
        // reasons and each gets its own message.
        let pinWeights (tW: TypedExpr) : TypeResult<TypedExpr * int> =
            match env.Subst.Resolve(tW.Type) with
            | ArrayElem arrTy ->
                let rank = arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
                if rank <> 1 then
                    Error (BuiltinArgErr $"rand.{kind}: weights must be a rank-1 array (got rank {rank})")
                else
                    match env.Subst.Resolve(arrTy.ElemType) with
                    | AnyPrimElem ETFloat64 ->
                        match arrTy.IndexTypes with
                        | [ix] ->
                            match ix.Extent with
                            | IRLit (IRLitInt k) when k > int64 System.Int32.MaxValue ->
                                Error (BuiltinArgErr $"rand.{kind}: weights extent {k} is too large (at most {System.Int32.MaxValue})")
                            | IRLit (IRLitInt k) when k > 0L -> Ok (tW, int k)
                            | IRLit (IRLitInt k) ->
                                Error (BuiltinArgErr $"rand.{kind}: weights extent must be positive (got {k})")
                            | _ ->
                                Error (StaticValueErr $"rand.{kind}: the weights array must have a STATIC extent -- codegen passes its length beside the pool pointer, so a symbolic or parameter extent cannot be filled")
                        | _ -> Error (BuiltinArgErr $"rand.{kind}: weights must be a rank-1 Float64 array")
                    | AnyPrimElem et ->
                        Error (BuiltinArgErr ($"rand.{kind}: weights must have Float64 elements (got {ppIRType (IRTScalar et)})"))
                    | _ ->
                        Error (BuiltinArgErr $"rand.{kind}: weights must be a rank-1 Float64 array")
            | _ ->
                Error (BuiltinArgErr $"rand.{kind}: weights must be a rank-1 Float64 array, not a scalar")
        let weightsResult : TypeResult<(TypedExpr * int) option> =
            match weightsArg with
            | None -> Ok None
            | Some wE ->
                inferExpr env wE
                |> Result.bind pinWeights
                |> Result.map Some
        weightsResult |> Result.bind (fun tWeights ->
        // Params check against Float64 (an int literal promotes; an
        // array-typed argument is refused by the check, not silently taken).
        let parResults =
            parArgs |> List.fold (fun acc p ->
                acc |> Result.bind (fun ps ->
                    checkExpr env (IRTScalar ETFloat64) p |> Result.map (fun tp -> ps @ [tp])))
                (Ok [])
        // The address channel: both Int64; a literal stream outside one
        // counter word or a literal negative offset is refused here (the
        // runtime aborts on the non-literal ones, BL8001, both lanes).
        let addrResult : TypeResult<(TypedExpr * TypedExpr) option> =
            match addrArgs with
            | [ sE; oE ] ->
                let literalBad =
                    match sE.Kind, oE.Kind with
                    | ExprKind.ExprLit (LitInt s), _ when s < 0L || s > 4294967295L ->
                        Some $"rand.{kind}: the stream key must be in [0, 2^32) (got {s}) -- it occupies one 32-bit word of the Philox counter"
                    | _, ExprKind.ExprLit (LitInt o) when o < 0L ->
                        Some $"rand.{kind}: the sample offset must be non-negative (got {o})"
                    | _ -> None
                (match literalBad with
                 | Some m -> Error (BuiltinArgErr m)
                 | None ->
                    checkExpr env (IRTScalar ETInt64) sE |> Result.bind (fun tS ->
                    checkExpr env (IRTScalar ETInt64) oE |> Result.map (fun tO -> Some (tS, tO))))
            | _ -> Ok None
        parResults |> Result.bind (fun tPars ->
        addrResult |> Result.bind (fun tAddr ->
        checkExpr env (IRTScalar ETInt64) keyE |> Result.map (fun tKey ->
            let indices =
                dims |> List.map (fun n ->
                    { Id = env.Builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 n))
                      Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] })
            // Element type: Int64 for categorical (its draws are indices),
            // Float64 for every other family.
            let elemTy = if kind0 = "categorical" then IRTScalar ETInt64 else IRTScalar ETFloat64
            let arrTy = mkArrayArrow indices elemTy None
            // AN ADDRESSED DRAW AS AN EXPRESSION (D13): its samples are a
            // pure function of their addresses, so wherever the fill
            // cannot go -- a function body, a nested expression -- they
            // are the map over the shape whose cell `(i, j, ..)` is
            // sample `offset + flat(i, j, ..)` (row-major, the fill's own
            // order), drawn by `__rand_sample_at`. The operands are bound
            // once, and their run-time guards (the fill's: stream key in
            // one counter word, offset non-negative, each parameter in its
            // domain) raise the same BL8001. A top-level `let` keeps the
            // fill (Lowering's intercept); this is typed beside it, and
            // only lowered where the fill is not.
            let inlineDraw =
                match addrArgs with
                | [ sE; oE ] ->
                    let sp = keyE.Span
                    let syn k = mkExpr sp k
                    let v (n: string) = syn (ExprKind.ExprVar n)
                    let ilit (i: int64) = syn (ExprKind.ExprLit (LitInt i))
                    let flit (f: float) = syn (ExprKind.ExprLit (LitFloat f))
                    let bin op a b = syn (ExprKind.ExprBinOp (Elementwise, op, a, b))
                    let sLet (n: string) (e: Expr) =
                        StmtLet { Pattern = { Kind = PatVar n; Span = sp }; Type = None; Value = e; Mutability = BindLet }
                    let guard (cond: Expr) (msg: string) =
                        StmtExpr (syn (ExprKind.ExprApp (v "__rand_guard", [ cond; syn (ExprKind.ExprLit (LitString msg)) ])))
                    let parNames = parArgs |> List.mapi (fun i _ -> $"__rand_p{i}")
                    let finite (x: Expr) = bin OpEq (bin OpSub x x) (flit 0.0)
                    let parGuards =
                        Blade.Rand.Elaborate.paramGuards kind
                        |> List.choose (fun (i, pname, dom) ->
                            if i >= parNames.Length then None
                            else
                                let x = v parNames.[i]
                                let cond =
                                    match dom with
                                    | Blade.Rand.Elaborate.Positive -> bin OpAnd (bin OpGt x (flit 0.0)) (finite x)
                                    | Blade.Rand.Elaborate.NonNegative -> bin OpAnd (bin OpGe x (flit 0.0)) (finite x)
                                    | Blade.Rand.Elaborate.UnitInterval -> bin OpAnd (bin OpGe x (flit 0.0)) (bin OpLe x (flit 1.0))
                                Some (guard cond (Blade.Rand.Elaborate.paramGuardMessage kind pname dom)))
                    let idxNames = dims |> List.mapi (fun i _ -> $"__rand_i{i}")
                    let flat =
                        List.zip idxNames dims
                        |> List.fold (fun (acc: Expr option) (n, d) ->
                            match acc with
                            | None -> Some (v n)
                            | Some a -> Some (bin OpAdd (bin OpMul a (ilit (int64 d))) (v n))) None
                        |> Option.defaultValue (ilit 0L)
                    let sampleArgs =
                        [ syn (ExprKind.ExprLit (LitString kind)); v "__rand_k"; v "__rand_s"; bin OpAdd (v "__rand_o") flat ]
                        @ (if weightsArg.IsSome then [ v "__rand_w" ] else [])
                        @ (parNames |> List.map v)
                    let kernel =
                        syn (ExprKind.ExprLambda (
                                idxNames |> List.map (fun n -> { Name = n; Type = None; Default = None; NameSpan = noSpan }),
                                None,
                                syn (ExprKind.ExprApp (v "__rand_sample_at", sampleArgs))))
                    let loop = syn (ExprKind.ExprMethodFor (dims |> List.map (fun d -> syn (ExprKind.ExprDotDot (ilit 0L, ilit (int64 d))))))
                    let body = syn (ExprKind.ExprCompute (syn (ExprKind.ExprBinOp (Elementwise, OpApply, loop, kernel))))
                    let stmts =
                        [ sLet "__rand_k" keyE; sLet "__rand_s" sE; sLet "__rand_o" oE ]
                        @ (match weightsArg with Some w -> [ sLet "__rand_w" w ] | None -> [])
                        @ (List.zip parNames parArgs |> List.map (fun (n, p) -> sLet n p))
                        @ [ guard (bin OpAnd (bin OpGe (v "__rand_s") (ilit 0L)) (bin OpLe (v "__rand_s") (ilit 4294967295L)))
                                  "rand stream key out of range -- the stream key of an _at family must be in [0, 2^32)"
                            guard (bin OpGe (v "__rand_o") (ilit 0L))
                                  "rand sample offset negative -- the sample offset of an _at family must be non-negative" ]
                        @ parGuards
                    match inferExpr env (syn (ExprKind.ExprBlock (stmts, Some body))) with
                    | Ok t -> Some t
                    | Error _ -> None
                | _ -> None
            mkTyped (TExprRandGen (kind, tKey, tPars, tWeights, tAddr, dims, inlineDraw)) arrTy))))

// ---- __rand_sample_at("<kind>_at", key, stream, index, [weights], pars..) ----
// Compiler-internal: ONE sample of an addressed family, the cell of an
// `_at` draw's inline map (above). A scalar: Float64, Int64 for
// categorical. The guards ride on the enclosing block.
and inferRandSampleAt ({ InferEntries.InferExpr = inferExpr; InferEntries.CheckExpr = checkExpr }) (env: TypeEnv) (kind: string) (rest: Expr list) (keyE: Expr) (sE: Expr) (iE: Expr) : TypeResult<TypedExpr> =
    let isCat = kind = "categorical_at"
    let weightsE, parEs = if isCat then (List.tryHead rest, List.tail rest) else (None, rest)
    checkExpr env (IRTScalar ETInt64) keyE |> Result.bind (fun tKey ->
    checkExpr env (IRTScalar ETInt64) sE |> Result.bind (fun tS ->
    checkExpr env (IRTScalar ETInt64) iE |> Result.bind (fun tI ->
    parEs |> List.fold (fun acc p ->
                acc |> Result.bind (fun ps -> checkExpr env (IRTScalar ETFloat64) p |> Result.map (fun tp -> ps @ [ tp ])))
             (Ok [])
    |> Result.bind (fun tPars ->
        let tW =
            match weightsE with
            | None -> Ok None
            | Some wE ->
                inferExpr env wE |> Result.bind (fun tw ->
                    match env.Subst.Resolve tw.Type with
                    | ArrayElem { IndexTypes = [ { Extent = IRLit (IRLitInt k) } ] } when k > 0L -> Ok (Some (tw, int k))
                    | _ -> Error (BuiltinArgErr "rand.categorical_at: the weights must be a rank-1 Float64 array of static extent"))
        tW |> Result.map (fun tWeights ->
            let ty = if isCat then IRTScalar ETInt64 else IRTScalar ETFloat64
            mkTyped (TExprRandGen (kind, tKey, tPars, tWeights, Some (tS, tI), [], None)) ty)))))

// ---- __display_emit(head, quoted, data, metaTail): display module ----
// Compiler-internal (double-underscore reserved): emitted by the `display`
// elaboration stage from `alias.emit(mime, data[, meta])`. Three of the
// four arguments are elaboration-time constants the elaborator already
// validated (the frame's JSON head, the payload-quoting flag, and the user
// meta object minus its braces -- see Blade.Display.Frame); only `data` is
// a runtime value, and it must be a String. Self-typed Bool: the call
// answers `true` so it can sit in an ordinary binding or echo as a bare
// REPL expression. A `data` of any other type is an ordinary type error
// reported by checkExpr against ETString.
and inferDisplayEmit ({ InferEntries.CheckExpr = checkExpr }) (env: TypeEnv) (headE: Expr) (quotedE: Expr) (metaE: Expr) (dataE: Expr) : TypeResult<TypedExpr> =
    (match headE.Kind, quotedE.Kind, metaE.Kind with
     | ExprKind.ExprLit (LitString head), ExprKind.ExprLit (LitBool quoted), ExprKind.ExprLit (LitString metaTail) ->
        checkExpr env (IRTScalar ETString) dataE
        |> Result.map (fun tData ->
            mkTyped (TExprDisplayEmit (head, quoted, tData, metaTail, None)) (IRTScalar ETBool))
     | _ ->
        Error (BuiltinArgErr "display.emit: internal marker arguments must be literals (this is a compiler bug -- write display.emit(mime, data) instead of calling __display_emit directly)"))

// ---- __display_emit_id(head, quoted, id, data, metaTail): display module ----
// `display.emit`'s twin with a caller-chosen `meta.id`. Same three
// elaboration-time constants and the same self-typed Bool; `id` is a
// SECOND runtime String, checked against ETString exactly like the payload
// (a chart identity that is not text has no meaning on the wire).
and inferDisplayEmitId ({ InferEntries.CheckExpr = checkExpr }) (env: TypeEnv) (headE: Expr) (quotedE: Expr) (metaE: Expr) (idE: Expr) (dataE: Expr) : TypeResult<TypedExpr> =
    (match headE.Kind, quotedE.Kind, metaE.Kind with
     | ExprKind.ExprLit (LitString head), ExprKind.ExprLit (LitBool quoted), ExprKind.ExprLit (LitString metaTail) ->
        checkExpr env (IRTScalar ETString) idE
        |> Result.bind (fun tId ->
        checkExpr env (IRTScalar ETString) dataE
        |> Result.map (fun tData ->
            mkTyped (TExprDisplayEmit (head, quoted, tData, metaTail, Some tId)) (IRTScalar ETBool)))
     | _ ->
        Error (BuiltinArgErr "display.emit_id: internal marker arguments must be literals (this is a compiler bug -- write display.emit_id(mime, id, data) instead of calling __display_emit_id directly)"))

// ---- __display_json_array(A): display module JSON serialization ----
// A rank-1 or rank-2 PLAIN-DENSE numeric array rendered as JSON text
// (String). The rank is pinned on the typed node so both back ends pick
// the 1-D/2-D serializer without re-resolving; unit annotations on the
// element type are transparent (stripUnits) -- a Float<meter> array
// serializes like a Float array. Compact/ragged storage is rejected with
// a decompact steer: JSON has no triangular layout, and silently
// densifying here would hide an allocation.
and inferDisplayJsonArray ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (arrE: Expr) : TypeResult<TypedExpr> =
    inferExpr env arrE |> Result.bind (fun tArr ->
        // An abstract-rank parameter (`x: T^1`) is still an
        // inference var carrying only its EXACT-rank arity constraint;
        // shape the var here exactly like requireArrayArgMinRank does,
        // so the body's serializer rank is pinned at the declaration and
        // shape monomorphization sees an ordinary array param.
        (match env.Subst.Resolve tArr.Type with
         | IRTInfer vid ->
             (match env.Subst.GetArityConstraint vid with
              | Some k when k = 1 || k = 2 ->
                  let freshIdx (i: int) = {
                      Id = env.Builder.FreshId(); Rank = 1
                      Extent = IRParam ($"__json_inferred_n{i}", 0, IRTNat None)
                      Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                      Kind = SDimension; Dependencies = []
                  }
                  // ELEMENT POLYMORPHISM SURVIVES THE SHAPE, same as
                  // requireArrayArgMinRank (TypeCheckSupport.fs): when the
                  // var being shaped is a signature var, the fresh element
                  // var must inherit the polymorphic mark, or zonk defaults
                  // it to Float64 and the enclosing function collapses to
                  // one element type. Measured on stdlib/plot.blade's
                  // `line(x: X^1, y: Y^1)`: a Float32 series argument met
                  // params already frozen at Array<double> and died in g++
                  // ("could not convert Array<float> to Array<double>"),
                  // because THIS arm shaped X/Y with a plain builder var.
                  let freshElem =
                      if env.Subst.IsPolymorphicId vid then
                          let e = env.Subst.Fresh()
                          (match e with
                           | IRTInfer eid -> env.Subst.MarkPolymorphic eid
                           | _ -> ())
                          e
                      else env.Builder.FreshInferType()
                  let freshArrType = {
                      ElemType = freshElem
                      IndexTypes = List.init k freshIdx
                      IsVirtual = false; Identity = None
                  }
                  unify env.Subst tArr.Type (mkArrayLike freshArrType) |> ignore
              | _ -> ())
         | _ -> ())
        match env.Subst.Resolve tArr.Type with
        | ArrayElem at when at.IndexTypes.Length >= 1 && at.IndexTypes.Length <= 2 ->
            if not (at.IndexTypes |> List.forall isPlainDenseIx) then
                Error (BuiltinArgErr "display.json_array: the array must be plain dense on every axis (compact symmetric/ragged storage has no JSON layout) -- decompact(A, d) first")
            else
                (match IR.stripUnits (env.Subst.Resolve at.ElemType) with
                 | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32)
                 // Unresolved element (an abstract-rank param shaped just
                 // above, or a generic slice): defer -- the concrete call
                 // site supplies it, and both serializers reject
                 // non-numeric elements there.
                 | IRTInfer _ ->
                     Ok (mkTyped (TExprDisplayJson (at.IndexTypes.Length, tArr)) (IRTScalar ETString))
                 | other ->
                     Error (BuiltinArgErr $"display.json_array: element type must be numeric (Float64/Float32/Int64/Int32), got {ppIRType other}"))
        | ArrayElem at ->
            Error (BuiltinArgErr $"display.json_array: rank-1 and rank-2 arrays only (got rank {at.IndexTypes.Length})")
        | other ->
            Error (BuiltinArgErr $"display.json_array: expected a rank-1 or rank-2 numeric array, got {ppIRType other}"))
