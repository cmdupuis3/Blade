// The type checker's APPLY STAGES, outside the inference chain (cleanup round 2, K5): buildApplyInfo and the stage
// pipeline it hands off to (buildApplySymmetry, buildApplyKernelShape, buildApplyKernelGrid, buildApplyKernelBody,
// buildApplyBodyUnits, buildApplyResult), which turn an inferred `<@>` application into its typed node, with their
// helpers (the tuple-node spine, functor-map output types, operand element hints, inferApplyMethodForZero).
// Nothing here calls back into TypeCheckInfer's recursive core, so these functions take no entry points; the
// chain's inferApply / inferApplyObjectFor call them directly.
module Blade.TypeCheckInferApply

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

/// The operands and the kernel of one map application, handed from each buildApply* stage to the next
/// (buildApplySymmetry .. buildApplyResult). The caller builds it from its CURRENT bindings at the call (a
/// stage may rebind them: buildApplyInfo and buildApplyKernelShape rebind the operand lists, buildApplyInfo
/// the lambda); the callee binds every field back under its old name in one destructuring let. A carrier,
/// never compared. RequireQualifiedAccess keeps its labels out of the unqualified record-label scope, where
/// TypedApplyInfo, TypedMethodForInfo and TypedObjectForInfo use the same ones.
[<NoEquality; NoComparison; RequireQualifiedAccess>]
type ApplyOperands =
    { Arrays: TypedExpr list
      Identities: ArrayIdentity list
      ArrayTypes: IRArrayType list
      SharedIndexTypes: IRIndexType list
      LambdaInfo: TypedLambdaInfo
      Loop: TypedExpr
      Kernel: TypedExpr
      IsReynolds: bool
      IsReynoldsAntisym: bool }

/// The kernel's iteration groups (the comm groups, then the anticomm groups) and its antisymmetric storage
/// groups: known from buildApplySymmetry on, handed to the stages through buildApplyBodyUnits. A carrier,
/// never compared (see ApplyOperands).
[<NoEquality; NoComparison; RequireQualifiedAccess>]
type ApplyGroups =
    { IterGroups: int list list
      AntisymStorageGroups: int list list }

/// The kernel's grid shape, all of it known once buildApplyKernelGrid has computed it (the input ranks come
/// from buildApplyKernelShape, the rest from buildApplyKernelGrid): handed to buildApplyKernelBody,
/// buildApplyBodyUnits and buildApplyResult. A carrier, never compared (see ApplyOperands).
[<NoEquality; NoComparison; RequireQualifiedAccess>]
type ApplyKernelShape =
    { KernelInputRanks: int list
      GridArrayTypes: IRArrayType list
      KernelTDims: IRIndexType list
      KernelSignParities: KernelSignParity list
      States: SymcomState list
      TriLevels: bool list
      KernelOutputRank: int
      Speedup: int64 }

/// The TOP-LEVEL TUPLE WIDTH of a value, by its STATIC type, seen through any
/// number of alias hops (docs/plan-tuples-vs-arg-packs.md 6c, rule 1). `None`
/// for anything that is not a tuple. This is the only thing the matcher reads
/// off a node: its spine width, never its depth.
let rec tupleNodeWidth (env: TypeEnv) (e: TypedExpr) : int option =
    match env.Subst.Resolve e.Type with
    | IRTTuple ts when ts.Length >= 2 -> Some ts.Length
    | _ -> None

/// The COMPONENTS of a tuple node, if they can be named as expressions.
/// A tuple-typed value whose alias chain does not end at a written tuple (a
/// `<&!>` fusion result, say) has a width but no component expressions; the
/// callers report that rather than inventing projections.
and tupleNodeParts (env: TypeEnv) (e: TypedExpr) : TypedExpr list option =
    match (resolveTypedExprDeep env e).Kind with
    | TExprTuple es when es.Length >= 2 -> Some es
    | _ -> None

/// The SPINE of a pack (docs/plan-tuples-vs-arg-packs.md 6c, rules 1 and 3).
///
/// Each element of a written operand list is ONE NODE, and its internal
/// structure is DATA that survives -- this is the one-level rule that replaced
/// 6b's free-monoid deep-flatten. The single transformation applied here is
/// rule 3's ONE-LEVEL SPLICE: a pack that is a single tuple node opens into its
/// components, which is what makes `f((a, b)) == f(a, b)` and, with the alias
/// fixpoint below it, what makes `K <@> (A, B)`, `let P = (A, B); K <@> P` and
/// `let Q = P; K <@> Q` one program (3.1's M1, 3.2's M4).
///
/// It does NOT recurse: `(A, (B, C))` splices to the two nodes `A` and
/// `(B, C)`, and the second stays a tuple. Under 6b it became three leaves,
/// which silently equated `(A, (B, C))` with `(A, B, C)`, destroyed nested
/// tuples as data, and broke `Poly`'s documented "arity counts top level"
/// (formalism.md:787). 6c's ruling restores all three, at the cost of the
/// cross-level recount (`Tuple<4>` over `((a,b),(c,d))`, which is now an error
/// steering to the flat spelling).
///
/// A node that came through an alias is reported as the ALIAS EXPRESSION, not
/// the chased binding: identities (`AIDVariable name`) are read off these
/// nodes, and a plain non-tuple binding must keep naming itself exactly as it
/// does today. Only the outermost tuple structure is seen through.
and packSpine (env: TypeEnv) (operands: TypedExpr list) : TypedExpr list =
    match operands with
    | [single] ->
        match tupleNodeParts env single with
        | Some parts -> parts
        | None -> operands
    | _ -> operands

/// The `<$>` half of the compact-class inheritance check (see
/// compactClassInheritError). `f <$> c` applies f to every element of c, so f
/// must commute with the mirror involution of c's storage class before the
/// result may keep that class -- and it does keep it: the arms below copy c's
/// index-type record wholesale, `Symmetry` included, for ANY f.
///
/// The question is asked of the MAPPED-OVER computation `tC`, not of the
/// deduced output type, because the two can disagree. When f's return type is
/// still an inference variable the arms fall through to `retTy` and the output
/// carries no records at all at this point; codegen then folds f into the inner
/// kernel (applyFunctorWrappers / mapKernelInner) and allocates from the INNER
/// apply's type -- which was deduced before f existed and so certified only the
/// inner kernel's parity. Keying off `tC` covers both routes.
///
/// f is unary by construction here (it maps one element), so the law is always
/// its parameter 0's.
/// The type of `f <$> c` (both spellings, binary and `object_for(<$>)`): c's
/// array with f's result as its cell (formalism 10.1, `<$>` maps each value
/// c yields). A wrapper whose parameter is still OPEN -- a generic `lambda(v)
/// -> v * v` instantiated here -- takes c's cell, so its result resolves; and
/// an array keeps its shape when that result is still open. Before (R20) an
/// open result replaced the whole type: `sq <$> (reynolds map)` was typed a
/// scalar, and every lane printed the array as one (a heap pointer). A
/// wrapper over a map the pipeline fusion composes never reached here.
and functorMapOutputType (env: TypeEnv) (tF: TypedExpr) (tC: TypedExpr) : IRType =
    (match env.Subst.Resolve tF.Type, env.Subst.Resolve tC.Type with
     | FuncElem ([ p ], _), ArrayElem arr ->
         (match env.Subst.Resolve p with
          | IRTInfer _ -> unify env.Subst p arr.ElemType |> ignore
          | _ -> ())
     | _ -> ())
    // (F2: the wrapper's field reads / tuple projections of its now-known
    // parameter take their types before its return is read)
    settleOpenProjections env tF
    match env.Subst.Resolve tF.Type, env.Subst.Resolve tC.Type with
    | FuncElem (_, retTy), ArrayElem arr ->
        match env.Subst.Resolve retTy with
        | IRTScalar et ->
            // Array with updated element type. Wrap et as IRTScalar
            // since arr.ElemType is IRType post-B2.
            mkArrayLike { arr with ElemType = IRTScalar et }
        | IRTInfer _ as r -> mkArrayLike { arr with ElemType = r }
        | r -> r
    | FuncElem (_, retTy), _ -> retTy
    | _ -> tC.Type  // fallback: preserve computation type

and functorMapInheritError (env: TypeEnv) (tF: TypedExpr) (tC: TypedExpr)
                           : TypeError option =
    match env.Subst.Resolve tC.Type with
    // SymWreath joins the filter: a wreath class whose mirror negates (any '-'
    // level) needs the same certificate, and leaving it out would let an inner
    // '-' level inherit through a `<$>` map uncertified.
    | ArrayElem arr when
            arr.IndexTypes |> List.exists (fun ix ->
                ix.Rank > 1
                && (ix.Symmetry = SymAntisymmetric || ix.Symmetry = SymHermitian
                    || ix.Symmetry = SymWreath)) ->
        let signResolver (calleeId: IRId) =
            match env.FuncSignParities.TryGetValue calleeId with
            | true, ps -> Some ps
            | _ -> None
        // An f that surfaces as neither a lambda nor a summarized top-level
        // function certifies nothing and lands on the conservative refusal.
        let (signParities, conjCommutes, fName) =
            match (resolveTypedExpr env tF).Kind with
            | TExprLambda li ->
                (Blade.Deduce.deduceSignParities signResolver li.Params li.Body,
                 Blade.Deduce.deduceConjCommutes li.Params li.Body,
                 (match li.Params with
                  | (p: TypedParam) :: _ -> p.Name
                  | [] -> "the mapped element"))
            | TExprVar (name, id, _) ->
                // A top-level `function` is bound with TypedValue = None, so
                // resolveTypedExpr can never surface it as a lambda; read its
                // recorded interprocedural sign summary instead. There is no
                // conjugation side-channel, so a Hermitian input refuses here.
                ((match env.FuncSignParities.TryGetValue id with
                  | true, ps -> ps
                  | _ -> []), [], name)
            | _ -> ([], [], "the mapped element")
        arr.IndexTypes
        |> List.filter (fun ix -> ix.Rank > 1)
        |> List.tryPick (fun ix ->
            compactClassInheritError ix.Symmetry (orbitLevelsOf ix) 0 fName signParities conjCommutes)
    | _ -> None

/// Eta-expand a bare named-function reference used in KERNEL position:
///   lkm  ==>  lambda(__k0..__kn) -> lkm(__k0..__kn)
/// A top-level `function` is bound with TypedValue = None (bindVarSimple),
/// so resolveTypedExpr can never surface it as a TExprLambda -- hence
/// `method_for(...) <@> lkm` and `object_for(lkm)` never match a kernel arm.
/// This mirrors the prefix partial-application eta-expansion (the FuncElem
/// arm of the ExprApp case) but for the 0-args case that path deliberately
/// excludes, so the synthesized lambda rides the entire existing lambda
/// pipeline (captures, lifting, std::function emission, kernel wrappers).
/// Returns None when `kernelExpr` is not a bare function reference -- callers
/// then fall back to their ordinary `inferExpr env kernelExpr`. Gated to
/// kernel positions only, so bare function VALUES elsewhere are unaffected.
/// G114: the NARROW element (Float32 / Int32 / Complex64) a map operand
/// expression delivers, read syntactically WITHOUT typing it (the operand is
/// typed after its kernel): a named array (any rank) or scalar, elementwise
/// `+ - * /` and negation of such leaves and literals, every non-literal
/// leaf the same element. Anything else (a call, a mixed width) is None, so
/// the kernel parameter keeps today's wide literal partners. A hint only, so
/// a wrong guess cannot change a shape: the parameter stays open.
and operandElemHint (env: TypeEnv) (e: Expr) : ElemType option =
    // Some (Some et): an operand of element et; Some None: a literal.
    let rec guess (fuel: int) (e: Expr) : ElemType option option =
        if fuel <= 0 then None else
        match e.Kind with
        | ExprKind.ExprLit (LitInt _ | LitFloat _) -> Some None
        | ExprKind.ExprVar v ->
            (match lookupVar v env with
             | Some vi ->
                 let elemOf (t: IRType) =
                     match IR.stripUnits (env.Subst.Resolve t) with
                     | IRTScalar et when litClassOf et >= 0 -> Some (Some et)
                     | _ -> None
                 (match IR.stripUnits (env.Subst.Resolve vi.Type) with
                  | ArrayElem at -> elemOf at.ElemType
                  | t -> elemOf t)
             | None -> None)
        | ExprKind.ExprUnaryOp (OpNeg, x) -> guess (fuel - 1) x
        | ExprKind.ExprBinOp (Elementwise, (OpAdd | OpSub | OpMul | OpDiv), l, r) ->
            (match guess (fuel - 1) l, guess (fuel - 1) r with
             | Some (Some a), Some (Some b) when a = b -> Some (Some a)
             | Some (Some a), Some None | Some None, Some (Some a) -> Some (Some a)
             | Some None, Some None -> Some None
             | _ -> None)
        | _ -> None
    match guess 32 e with
    | Some (Some (ETFloat32 | ETInt32 | ETComplex64 as et)) -> Some et
    | _ -> None

/// Pick the zero literal for an element type. ETIndexRef _ requires looking up
/// the alias in env.TypeDefs: a string-valued EnumIdx needs LitString "" (the
/// empty string is the natural zero for std::string), an int-valued EnumIdx
/// uses LitInt 0L. ETString itself uses LitString "". Other types follow the
/// obvious pattern. This is consulted by every site that synthesizes a "zero
/// kernel" for a method_for / object_for with `<@> zero` -- the runtime value
/// is rarely meaningful semantically (no obvious string identity for fold),
/// but the literal kind must match the element type or codegen produces
/// invalid C++.
/// An element type still OPEN (a generic body's polymorphic element): the
/// implicit `(+)` seed of a fused fold is then a typed TExprZero, not a
/// literal of the Float64 default.
and isOpenElemType (env: TypeEnv) (t: IRType) : bool =
    match env.Subst.Resolve t with
    | IRTInfer _ | IRTUnitAnnotated (IRTInfer _, _) -> true
    | _ -> false

and zeroLitForElem (env: TypeEnv) (et: ElemType) : TypedExprKind =
    match et with
    | ETInt32 | ETInt64 -> TExprLit (LitInt 0L)
    | ETBool -> TExprLit (LitBool false)
    | ETString -> TExprLit (LitString "")
    | _ -> TExprLit (LitFloat 0.0)

and inferApplyMethodForZero (env: TypeEnv) (mfInfo: TypedMethodForInfo) (tLeft: TypedExpr) : TypeResult<TypedExpr> =
    // M <@> zero: synthesize a lambda that returns 0 for each index point.
    // Infer element type from first array, default to Float64. Extract
    // the primitive scalar for the literal-choice match below;
    // non-primitive elem types (struct/named) fall through to Float64 --
    // this path semantically requires a primitive-typed array anyway,
    // since `zero` produces literal 0/false values.
    let elemTypeIR =
        mfInfo.ArrayTypes |> List.tryHead
        |> Option.map (_.ElemType)
        |> Option.defaultValue (IRTScalar ETFloat64)
    let elemType =
        match elemTypeIR with
        | AnyPrimElem et -> et
        | _ -> ETFloat64
    let paramTy =
        match elemTypeIR with
        | AnyPrimElem _ -> elemTypeIR
        | _ -> IRTScalar ETFloat64
    let zeroLit = zeroLitForElem env elemType
    // Create one parameter per array (all rank-0 element types)
    let nArrays = mfInfo.Arrays.Length
    let params_ = List.init nArrays (fun i ->
        let pid = env.Builder.FreshId()
        { Name = $"__z{i}"; Type = paramTy; Index = i; VarId = pid; Default = None; NameSpan = noSpan })
    let lambdaInfo : TypedLambdaInfo = {
        Params = params_
        Body = mkTyped zeroLit paramTy
        ReturnType = paramTy
        CommGroups = []
        AntisymGroups = []
        SignParities = []
        Captures = []; IsCommutative = true
        Parallel = []  // synthesized (zero kernel): no user clause
        SelfBinding = None  // anonymous: cannot self-reference
    }
    let tZeroKernel = mkTyped (TExprLambda lambdaInfo) (IRTScalar elemType)
    buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexTypes lambdaInfo tLeft tZeroKernel false false

and buildApplyInfo (env: TypeEnv)
    (arrays: TypedExpr list) (identities: ArrayIdentity list)
    (arrayTypes: IRArrayType list) (sDimsPerArray: int list)
    (sharedIndexTypes: IRIndexType list)
    (lambdaInfo: TypedLambdaInfo)
    (tLoop: TypedExpr) (tKernel: TypedExpr)
    (isReynolds: bool) (isReynoldsAntisym: bool)
    : TypeResult<TypedExpr> = typed {

    // ---- THE SPINE MATCHER (docs/plan-tuples-vs-arg-packs.md 6c, rule 2) ----
    // The operand pack arrives here as a SPINE: one node per top-level element,
    // with nested tuples still intact. This is where the schema and the spine
    // meet -- and it has to be here rather than at the former, because
    // `method_for(...)` is built before the kernel is known.
    //
    // Greedy, left to right:
    //   * an UNANNOTATED parameter takes one non-tuple node. Facing a tuple
    //     node it is an error demanding an annotation -- 5.2's rule one level
    //     out: the pack reading and the whole-tuple reading both type and
    //     disagree, and the body cannot vote (5.1).
    //   * a `Tuple<k>` parameter PREFERS one tuple node of top-level width k
    //     (direct bind), and otherwise consumes k consecutive non-tuple nodes
    //     (regroup). Preferring the node is what makes `(A, (B,C))` and
    //     `(A, B, C)` agree against `lambda(x, y: Tuple<2>)` -- the CONDITIONAL
    //     equivalence 6c restores, in place of 6b's unconditional one.
    //
    // Greedy is deterministic, and the other grouping is now spellable, because
    // structure survives: `(t1, a, b)` against `[y: Tuple<2>, z]` takes t1 for
    // y, and `((t1, a), b)` is how you say the other thing (rule 4).
    //
    // Whatever a `Tuple<k>` parameter binds, the LOOP still iterates k operands
    // -- a tuple of arrays has nothing to iterate as a unit -- so a directly
    // bound tuple node opens into its k components here, and the tuple is
    // rebuilt per iteration by the surface rewrite further down. Direct bind
    // and regroup therefore produce the SAME loop; what differs is which
    // spellings are legal, which is the whole point of the ruling.
    //
    // Runs ONLY when some node is tuple-typed. Every pack without one is
    // byte-identical to before, which is what keeps this off the hot path.
    let spineMatch : TypeResult<(TypedExpr list * ArrayIdentity list * IRArrayType list * int list) option> =
        if not (arrays |> List.exists (fun a -> (tupleNodeWidth env a).IsSome)) then Ok None
        else
            let widthOf (p: TypedParam) =
                match env.DeclaredTupleWidths.TryGetValue p.VarId with
                | true, w -> w
                | _ -> 1
            // A SYNTHESIZED parameter (`__`-prefixed: the eta wrappers built for
            // a named-function kernel, the Poly deferred former, sections, zero)
            // is not a name the user can annotate, so the annotation demand is
            // not addressed to anyone -- it gets its own message naming the
            // OPERAND instead.
            //
            // `Poly` reaches here having already counted TOP-LEVEL arity
            // (formalism.md:787), which is the property 6c restores: the pack
            // width the deferred former eta-expands to is the number of NODES,
            // so `(A, (B,C))` is arity 2 with a tuple second argument, not
            // arity 3. Passing that tuple to a `Poly` pack is 3.8's M9 and is
            // broken in every spelling, so it is refused here rather than
            // allowed to reach codegen as an undeclared temporary (measured:
            // `psum_arity_2_...(A____i0, __v18)` with `__v18` never declared).
            let rec walk (ps: TypedParam list) (ns: TypedExpr list)
                         (acc: TypedExpr list) : TypeResult<TypedExpr list> =
                match ps, ns with
                | [], [] -> Ok (List.rev acc)
                | [], _ -> Ok (List.rev acc @ ns)   // arity is settled downstream
                | _, [] -> Ok (List.rev acc)        // ditto (defaults may fill)
                | p :: pt, n :: nt ->
                    let w = widthOf p
                    // An operator SECTION's two params are synthesized as plain
                    // `a`/`b` rather than `__`-prefixed, so it needs its own
                    // test: `(+)` has no parameter list to annotate either.
                    let synthetic =
                        p.Name.StartsWith "__"
                        || tKernel.Kind.IsTExprSection
                    match tupleNodeWidth env n, w with
                    | Some tw, 1 when not synthetic ->
                        Error (KernelPackArity
                                 ($"kernel parameter '{p.Name}' is unannotated but the operand in that position is a {tw}-tuple. Tuple-ness is never inferred: annotate the parameter `Tuple<{tw}>` to take the group as one tuple, or write {tw} parameters and pass the components separately."))
                    | Some tw, 1 ->
                        Error (KernelPackArity
                                 ($"this kernel takes the {tw}-tuple in operand position {(arrays.Length - nt.Length)} as ONE argument (its parameter list counts top-level operands), and a tuple of arrays has nothing to iterate. Pass the components as separate operands, or use a kernel whose parameter in that position is annotated `Tuple<{tw}>`."))
                    | Some tw, k when tw = k ->
                        // DIRECT BIND. Opened into components for the loop; the
                        // tuple is rebuilt at kernel entry by the surface rewrite.
                        (match tupleNodeParts env n with
                         | Some parts -> walk pt nt (List.rev parts @ acc)
                         | None ->
                             Error (KernelPackArity
                                      ($"kernel parameter '{p.Name}' is declared `Tuple<{k}>` and the operand in that position is a {tw}-tuple VALUE whose components cannot be named (a fused or computed tuple). Bind the components to their own names and pass them as separate operands.")))
                    | Some tw, k ->
                        Error (KernelPackArity
                                 ($"kernel parameter '{p.Name}' is declared `Tuple<{k}>` but the operand in that position is a {tw}-tuple. Widths are matched at the TOP LEVEL only -- a `Tuple<{k}>` cannot re-count across nesting. Write the operands flat, or change the annotation to `Tuple<{tw}>`."))
                    | None, 1 -> walk pt nt (n :: acc)
                    | None, k ->
                        // REGROUP: k consecutive nodes, none of which may itself
                        // be a tuple (that would be the cross-level recount).
                        let avail = n :: nt
                        let taken = avail |> List.truncate k
                        if taken.Length < k || (taken |> List.exists (fun t -> (tupleNodeWidth env t).IsSome)) then
                            Error (KernelPackArity
                                     ($"kernel parameter '{p.Name}' is declared `Tuple<{k}>` and cannot be filled from this position: the operands here are neither one {k}-tuple nor {k} plain operands. Parenthesize the grouping you mean -- matching is greedy left to right, so `((x, y), z)` and `(x, (y, z))` are different packs."))
                        else
                            walk pt (avail |> List.skip k) (List.rev taken @ acc)
            walk lambdaInfo.Params arrays []
            |> Result.map (fun expanded ->
                if expanded.Length = arrays.Length then None
                else
                    let ids =
                        expanded |> List.map (fun (ta: TypedExpr) ->
                            match ta.Kind with
                            | TExprVar (name, vid, _) -> loopOperandIdentity env name vid
                            | _ -> AIDLiteral (env.Builder.FreshId()))
                    let ats =
                        expanded |> List.map (fun (ta: TypedExpr) ->
                            match ta.Type with
                            | ArrayElem at -> at
                            | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
                    Some (expanded, ids, ats, computeSDimsPerArray ats))

    let! spineExpansion = spineMatch
    let (arrays, identities, arrayTypes, sDimsPerArray) =
        match spineExpansion with
        | Some (a, i, at, sd) -> (a, i, at, sd)
        | None -> (arrays, identities, arrayTypes, sDimsPerArray)

    // DEFAULTED TRAILING KERNEL PARAMS: when the kernel declares more params
    // than the sources supply rows and every EXCESS trailing param carries a
    // default, the apply is a defaults fill -- the omitted params become
    // body-entry lets over their (declaration-typed) default exprs, so each
    // kernel invocation evaluates them at entry with the row params bound:
    //   method_for(A) <@> lambda(x, s = 2.0) -> x * s
    //     ==  method_for(A) <@> lambda(x) -> { let s = 2.0; x * s }
    // The let reuses the param's own VarId, so body references resolve
    // unchanged. Done FIRST so every downstream consumer (deduction, ranks,
    // param unification, grouping) sees the shrunk, ordinary kernel. The
    // co-iteration INDEX-PARAM form (params = arrays + shared index slots)
    // is checked first and wins -- its trailing params are index receivers,
    // not omitted defaults.
    let lambdaInfo =
        let expectedRows =
            arrayTypes |> List.sumBy (fun (at: IRArrayType) ->
                if at.IsVirtual then at.IndexTypes |> List.sumBy (_.Rank) else 1)
        let idxParamCount = sharedIndexTypes |> List.sumBy (_.Rank)
        let coIterIndexForm =
            not sharedIndexTypes.IsEmpty
            && idxParamCount > 0
            && not (arrayTypes |> List.exists (_.IsVirtual))
            && lambdaInfo.Params.Length = arrays.Length + idxParamCount
        // WIDTH-AWARE prefix (docs/plan-tuples-vs-arg-packs.md 6c, 5.3's
        // "defaulted kernel params" seam): the satisfied prefix is the one
        // whose WIDTHS sum to expectedRows, not its first `expectedRows`
        // entries -- `lambda(x, y: Tuple<2>, s = 1.0)` over 3 rows keeps two
        // params, not three. With every width 1 this is `expectedRows`
        // exactly, so the ordinary case is unchanged.
        let widthOf (p: TypedParam) =
            match env.DeclaredTupleWidths.TryGetValue p.VarId with
            | true, w -> w
            | _ -> 1
        let satisfiedPrefix =
            let mutable acc = 0
            let mutable n = 0
            for p in lambdaInfo.Params do
                if acc < expectedRows then
                    acc <- acc + widthOf p
                    n <- n + 1
            if acc = expectedRows then Some n else None
        if not coIterIndexForm
           && expectedRows > 0
           && (match satisfiedPrefix with
               | Some n -> n < lambdaInfo.Params.Length
                           && (lambdaInfo.Params |> List.skip n |> List.forall (_.Default.IsSome))
               | None -> false) then
            let n = Option.get satisfiedPrefix
            let kept = lambdaInfo.Params |> List.truncate n
            let dropped = lambdaInfo.Params |> List.skip n
            let newBody =
                (dropped, lambdaInfo.Body)
                ||> List.foldBack (fun p body ->
                    { Kind = TExprLet (p.Name, p.VarId, Option.get p.Default, body)
                      Type = body.Type
                      Span = body.Span })
            { lambdaInfo with Params = kept; Body = newBody }
        else lambdaInfo

    // ---- TUPLE PARAMS: the schema's re-nesting half ----
    // docs/plan-tuples-vs-arg-packs.md 6c. A `Tuple<k>` parameter consumes k
    // consecutive leaves and receives them AS A TUPLE. It is realized here, in
    // the surface, by the same body-entry-let mechanism the defaults fill
    // above uses: the tuple param is replaced by k fresh row params and the
    // body opens with `let p = (__tp_0, ..., __tp_{k-1})`, reusing p's own
    // VarId so every body reference (`p[0]`) resolves unchanged.
    //
    // Doing it as a SURFACE rewrite rather than in codegen is what keeps this
    // ~30 lines: after it, the schema is all-width-1, so ranks, deduction,
    // grouping, unification, lowering and emission are the flattened path they
    // already were, and `t[i]` is the existing TExprTupleIndex -> std::get<i>.
    // It is the Poly whole-pack arm's move (2.5: eta-expand to the pack width,
    // re-absorb inside) at a width the annotation makes static. The
    // std::make_tuple the let builds is a register shuffle every optimizer
    // folds away -- and it is the ONLY shape that needs no new IR node.
    //
    // The synthesized params take p's OWN element types (its annotation
    // lowered to `IRTTuple [a1..ak]`), so unifying them against the rows
    // downstream also pins the annotation's element slots -- the tuple the let
    // builds then has exactly p's declared type, with nothing left to reconcile.
    let declaredParamCount = lambdaInfo.Params.Length
    let declaredParamWidths =
        lambdaInfo.Params |> List.map (fun p ->
            match env.DeclaredTupleWidths.TryGetValue p.VarId with
            | true, w -> w
            | _ -> 1)
    let declaredTotalWidth = List.sum declaredParamWidths
    // A `where` clause on a kernel that also declares a tuple parameter splits
    // by CONJUNCT CLASS, because the two classes address parameters in
    // different ways and only one of them survives the expansion below.
    //
    //   * `comm` / `anticomm` are POSITIONAL index lists (CommGroups /
    //     AntisymGroups). The expansion renumbers positions, so a surviving
    //     group would land on the wrong slot -- silently, since a misplaced
    //     comm group degrades to dense storage with no diagnostic. Worse, there
    //     is nothing settled to remap TO: "comm(p, q)" between two PAIRS has no
    //     agreed meaning. REFUSED.
    //
    //   * The PARALLEL STRATEGIES (`WhereClause.Parallel`: `omp`, `cuda`,
    //     `mpi`, and any future member of `ParallelStrategy`) are resolved BY
    //     NAME against the operand a parameter contributes, and under one-level
    //     structural matching (docs/plan-tuples-vs-arg-packs.md 6c) a `Tuple<k>`
    //     parameter is ONE schema node -- so the name still designates a
    //     well-defined unit of the nest after expansion. ALLOWED, with the
    //     licence rewritten onto the synthesized row params by
    //     `remapTupleParallel` below. Strategies carrying no variable names
    //     (`cuda`, `mpi`) are unaffected by the renaming and pass through.
    //
    // The split is on the CLASS, not on a per-strategy allowlist, so a new
    // `ParallelStrategy` case inherits the permitted behaviour automatically.
    let tupleParamWhereClash =
        if declaredTotalWidth = declaredParamCount then None
        elif not (List.isEmpty lambdaInfo.CommGroups)
             || not (List.isEmpty lambdaInfo.AntisymGroups) then
            Some (WhereClauseErr "a `comm`/`anticomm` clause on a kernel that also declares a `Tuple<N>` parameter is not supported: those clauses address parameters by POSITION, and a tuple parameter is expanded into its k row parameters (renumbering every later position). `comm` between two PAIRS has no settled meaning to remap to. Write the parameters flat (one per operand) to use `comm`/`anticomm` here. (The parallel strategies -- `omp`/`cuda`/`mpi` -- ARE supported on a tuple parameter: they resolve by name to the operand node.)")
        else None
    do! requireNone tupleParamWhereClash
    let lambdaInfo =
        if declaredTotalWidth = declaredParamCount then lambdaInfo
        else
            let uid = env.Builder.FreshId()
            let mutable nextIdx = 0
            let mutable wraps : (TypedParam * TypedParam list) list = []
            let newParams =
                List.map2 (fun (p: TypedParam) w ->
                    if w = 1 then
                        let p' = { p with Index = nextIdx }
                        nextIdx <- nextIdx + 1
                        [p']
                    else
                        let elemTys =
                            match env.Subst.Resolve p.Type with
                            | IRTTuple ts when ts.Length = w -> ts
                            | _ -> List.init w (fun _ -> env.Subst.Fresh())
                        let subs =
                            elemTys |> List.map (fun ty ->
                                let sp : TypedParam =
                                    { Name = $"__tp{uid}_{p.Name}_{nextIdx}"
                                      Type = ty; Index = nextIdx
                                      VarId = env.Builder.FreshId()
                                      Default = None; NameSpan = p.NameSpan }
                                nextIdx <- nextIdx + 1
                                sp)
                        wraps <- wraps @ [(p, subs)]
                        subs) lambdaInfo.Params declaredParamWidths
                |> List.concat
            let newBody =
                (wraps, lambdaInfo.Body)
                ||> List.foldBack (fun (p, subs) body ->
                    let tupleTy = IRTTuple (subs |> List.map (_.Type))
                    let tupleExpr =
                        { Kind = TExprTuple (subs |> List.map (fun sp ->
                                    { Kind = TExprVar (sp.Name, sp.VarId, None)
                                      Type = sp.Type; Span = body.Span }))
                          Type = tupleTy; Span = body.Span }
                    { Kind = TExprLet (p.Name, p.VarId, tupleExpr, body)
                      Type = body.Type; Span = body.Span })
            // PARALLEL LICENCE carry-over across the expansion. `omp(p: n)`
            // names the tuple parameter, which no longer exists in `newParams`
            // -- Lowering.extractParallelism resolves omp vars by NAME against
            // the callable's params, so leaving it alone would drop the whole
            // licence and emit a serial nest whose C++ is indistinguishable
            // from a program that never asked (the failure the refusal above
            // used to prevent by rejecting the program).
            //
            // A `Tuple<k>` parameter is one schema NODE, and the depth counts
            // levels OF THAT NODE, outermost first -- the same rule a rank-k
            // array parameter follows (`omp(a: 2)` on `a: T^2`). The node's
            // levels are its k rows in order, so `omp(p: n)` licenses its first
            // n rows: row j keeps the residual budget `n - j`, and rows past
            // the budget are dropped rather than licensed. `omp(p: 1)` on a
            // 2-tuple therefore threads one level, not two -- the co-iterated
            // tuple contributes ONE licensed axis, exactly as written.
            //
            // Conservative where a row is itself multi-rank: row j is offered
            // `n - j` levels when a strict level count would offer
            // `n - (levels of rows 0..j-1)`. Under-licensing is the safe
            // direction for a cap, and rows of a co-iterated tuple are rank-1
            // in every spelling that exists today.
            // Snapshot before the closure: `wraps` is a mutable local.
            let wrapsFinal = wraps
            let remapTupleParallel (strategies: ParallelStrategy list) =
                if List.isEmpty wrapsFinal then strategies
                else
                    let subNamesOf (n: string) =
                        wrapsFinal |> List.tryPick (fun ((p: TypedParam), subs) ->
                            if p.Name = n then Some (subs |> List.map (fun (s: TypedParam) -> s.Name))
                            else None)
                    strategies |> List.map (function
                        | Omp o ->
                            Omp { o with
                                    Vars =
                                      o.Vars |> List.collect (fun (n, dims) ->
                                        match subNamesOf n with
                                        | Some subs ->
                                            subs
                                            |> List.mapi (fun j s -> (s, dims - j))
                                            |> List.filter (fun (_, d) -> d > 0)
                                        | None -> [(n, dims)]) }
                        // `cuda` / `mpi` carry no variable names: nothing to remap.
                        | s -> s)
            { lambdaInfo with Params = newParams; Body = newBody
                              Parallel = remapTupleParallel lambdaInfo.Parallel }

    // Every buildApply* stage hand-off is BOUND (`let! applied = ..; return applied`), not `return!`:
    // a returned hand-off is a tail call, which makes the whole stage chain tail-call critical, so F#
    // marks every call into it `.tail` (inferApply's seven included) -- and a `.tail` call into an
    // 11-to-23-parameter stage goes through the runtime's tail-call helper. Bound, the chain makes no
    // tail call: about 1.5 % of `blade check` on lsdft (cleanup speed finding, 2026-11-21).
    let! applied = buildApplySymmetry env
                       { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
                         ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
                         ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
                         ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym }
                       sDimsPerArray declaredTotalWidth declaredParamCount
    return applied
}

and buildApplySymmetry (env: TypeEnv) (applyOps: ApplyOperands) (sDimsPerArray: int list) (declaredTotalWidth: int) (declaredParamCount: int) : TypeResult<TypedExpr> = typed {
    let { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
          ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
          ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
          ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym } = applyOps
    let commGroups = lambdaInfo.CommGroups
    // Declared `where anticomm(...)` positions. Same axis grouping and the
    // same iteration license as comm (the exchange is licensed either way --
    // only the SIGN and the diagonal differ), so `iterGroups` is what every
    // grouping consumer sees. `antisymStorageGroups` is the narrower list that
    // decides the licensed simplex is the STRICT one (AntisymIdx, no
    // diagonal): under `reynolds` the wrapper owns the output symmetry, so a
    // declared clause there degrades to an iteration license exactly as `comm`
    // does on a reynolds-wrapped kernel (index-types/050, symmetry/019).
    let iterGroups = commGroups @ lambdaInfo.AntisymGroups
    let antisymStorageGroups = if isReynolds then [] else lambdaInfo.AntisymGroups

    // ---- Stage 3: symmetry deduction (early tier), at the ONE seam every
    // apply arm funnels through. Deduce the kernel's adjacent-pair swap
    // parity; an eta-expanded named-function wrapper (body = f(p0..pn-1) in
    // order) defers to the function's recorded summary. Under reynolds a
    // `comm` clause is an ITERATION LICENSE over the signed permutation sum,
    // not a claim about the bare kernel -- validation and suggestions both
    // stand down (isReynolds).
    let (stage3Names, stage3Pairs) =
        if isReynolds || lambdaInfo.Params.Length < 2 then ([], [])
        else
            let etaSummary =
                match lambdaInfo.Body.Kind with
                | TExprApp ({ Kind = TExprVar (_, fnId, _) }, args)
                        when args.Length = lambdaInfo.Params.Length
                             && List.forall2 (fun (arg: TypedExpr) (p: TypedParam) ->
                                    match arg.Kind with
                                    | TExprVar (_, aid, _) -> aid = p.VarId
                                    | _ -> false) args lambdaInfo.Params ->
                    (match env.FuncDeducedPairs.TryGetValue fnId with
                     | true, (names, ps) when ps.Length = lambdaInfo.Params.Length - 1 ->
                         Some (names, ps)
                     | _ ->
                         // Fixed-arity wrapper over a PACK-summarized kernel
                         // (`lambda(x, y) -> comoment(x, y)`): invariance at
                         // every arity specializes to full pairwise symmetry
                         // at this one. Suggestion names the LAMBDA's own
                         // params -- that is where this spelling pins.
                         (match env.PackDeducedComm.TryGetValue fnId with
                          | true, (_, Blade.Deduce.PInv) ->
                              Some (lambdaInfo.Params |> List.map (_.Name),
                                    List.replicate (lambdaInfo.Params.Length - 1) Blade.Deduce.PInv)
                          | _ -> None))
                | _ -> None
            match etaSummary with
            | Some (names, ps) -> (names, ps)
            | None ->
                // Same sign-linearity resolver as checkFunctionDecl: a lambda
                // kernel calling an already-summarized helper (`lambda(x, y) ->
                // mymean(x - y)`) gets the callee's per-parameter sign law.
                let signResolver (calleeId: IRId) =
                    match env.FuncSignParities.TryGetValue calleeId with
                    | true, ps -> Some ps
                    | _ -> None
                (lambdaInfo.Params |> List.map (_.Name),
                 Blade.Deduce.deduceAdjacentPairs signResolver lambdaInfo.Params lambdaInfo.Body)
    // RECORDING ONLY (channel (f)): the kernel's proved adjacent-pair
    // parities, so the editor can distinguish "you declared this" from "the
    // checker proved it". Deliberately BROADER than the confirm-and-pin
    // suggestion below, which additionally requires the same array in both
    // positions -- that extra condition is about the STORAGE decision, while
    // provenance is worth surfacing whether or not compaction is on the table.
    // Synthesized `__`-prefixed wrapper params are filtered for the same reason
    // the suggestion filters them: they are not names a user can see.
    if List.isEmpty iterGroups then
        List.indexed stage3Pairs
        |> List.iter (fun (i, par) ->
            if (par = Blade.Deduce.PInv || par = Blade.Deduce.PNeg)
               && i + 1 < stage3Names.Length
               && not (stage3Names.[i].StartsWith "__")
               && not (stage3Names.[i + 1].StartsWith "__") then
                Blade.TypeEnv.DeducedFacts.add
                    (Blade.TypeEnv.DeducedPairSym
                        ("<kernel>", stage3Names.[i], stage3Names.[i + 1], i,
                         par = Blade.Deduce.PNeg))
                    (if tKernel.Span = noSpan then tLoop.Span else tKernel.Span))

    // IDE: record this kernel's deduction snapshot, span-keyed (`ide check
    // --json` kernels[]). Unconditional -- declared and reynolds kernels
    // record too (parities are empty under reynolds by construction). Skip
    // synthesized eta-wrappers over named functions (`__k...`/`__of...`
    // params): the named function's own binding entry covers those with
    // names a user can actually write.
    let kParams = lambdaInfo.Params |> List.map (_.Name)
    if not (kParams |> List.exists (fun n -> n.StartsWith "__")) then
        let renderGroups kw (groups: int list list) =
            groups |> List.choose (fun g ->
                let names = g |> List.choose (fun i ->
                    if i >= 0 && i < kParams.Length then Some kParams.[i] else None)
                if List.isEmpty names then None
                else Some ($"""{kw}({(String.concat ", " names)})"""))
        IdeDeductions.addKernel {
            KSpan = (if tKernel.Span = noSpan then tLoop.Span else tKernel.Span)
            KParams = kParams
            KParities = stage3Pairs
            KDeclared = renderGroups "comm" lambdaInfo.CommGroups
                        @ renderGroups "anticomm" lambdaInfo.AntisymGroups
            KRanks = lambdaInfo.Params |> List.map (fun p ->
                let rank =
                    match env.Subst.Resolve p.Type with
                    | ArrayElem arr -> arr.IndexTypes.Length
                    | _ -> 0
                (p.Name, rank)) }
    // Declared comm + provably antisymmetric body = the silent-corruption
    // case: triangular storage would drop the sign flips. Hard error;
    // PBottom stays trusted (status quo escape hatch). The mirror case --
    // declared anticomm + provably COMMUTATIVE body -- is the same error with
    // the signs exchanged: strict-simplex storage would drop the diagonal and
    // return the negation for half the reads. (Both stand down under
    // reynolds, where stage3Pairs is empty by construction.)
    // The Hermitian third of the family (needsComplexPair rows below): a
    // CONJUGATING body (deduced PConj, f(y,x) = conj(f(x,y))) contradicts
    // comm AND anticomm -- the identity mirror returns mirrored cells
    // un-conjugated, the sign mirror returns them negated -- but ONLY over
    // complex elements: conj is the identity on reals, where the same body
    // is genuinely invariant. Kernel param types are still unresolved
    // inference vars at this seam (unification with the operands comes
    // later), so the OPERAND ARRAYS' element types are the judges; an
    // unresolved element stays trusted, the same shape as PBottom's escape hatch.
    let pairElemIsComplex (i: int) =
        let rec complexElem (t: IRType) =
            match IR.stripUnits (env.Subst.Resolve t) with
            | IRTScalar (ETComplex64 | ETComplex128) -> true
            | ArrayElem arr -> complexElem arr.ElemType
            | _ -> false
        if lambdaInfo.Params.Length = arrayTypes.Length then
            // One param per operand: the pair's own operands decide.
            [i; i + 1] |> List.exists (fun k ->
                k >= 0 && k < arrayTypes.Length && complexElem arrayTypes.[k].ElemType)
        else
            // Rows and params are not 1:1 (virtual expansion, index-param
            // co-iteration, defaults): any complex operand keeps the refusal armed.
            arrayTypes |> List.exists (fun at -> complexElem at.ElemType)
    let pairElemIsInt (i: int) =
        let rec intElem (t: IRType) =
            match IR.stripUnits (env.Subst.Resolve t) with
            | IRTIdxTagged (inner, _) -> intElem inner
            | IRTScalar (ETInt32 | ETInt64) -> true
            | ArrayElem arr -> intElem arr.ElemType
            | _ -> false
        [i; i + 1] |> List.exists (fun k ->
            k >= 0 && k < arrayTypes.Length && intElem arrayTypes.[k].ElemType)
    // WITNESSED LAW FAILURE, every claimed pair at every arity. A group
    // `comm(p_1, ..., p_k)` claims invariance under the whole symmetric group
    // on those slots, which the transpositions of CONSECUTIVE members (in
    // parameter order) generate -- so checking each such pair checks the
    // claim, adjacent in the kernel or not (`comm(x, z)` on (x, y, z) is the
    // one pair (x, z)). `anticomm` is the same with the sign law: f is negated
    // by each transposition. The other parameters are held at fixed values
    // across the swap (Deduce.witnessSwapLawFailure); before, the evaluator
    // gave up on any third parameter, so no false claim on a kernel of arity
    // > 2 was ever refused, and the anticomm law had no witness at all. A
    // pair the parity tables already PROVED (adjacent, PInv for comm / PNeg
    // for anticomm) skips the search; complex pairs stay trusted (the
    // evaluator has no complex numbers).
    let claimedPairs (groups: int list list) =
        groups
        |> List.collect (fun g -> g |> List.distinct |> List.sort |> List.pairwise)
        |> List.distinct
    let slotElemIs (pred: IRType -> bool) (slots: int list) =
        if lambdaInfo.Params.Length = arrayTypes.Length then
            slots |> List.exists (fun k -> k >= 0 && k < arrayTypes.Length && pred arrayTypes.[k].ElemType)
        else
            arrayTypes |> List.exists (fun at -> pred at.ElemType)
    let rec complexElemT (t: IRType) =
        match IR.stripUnits (env.Subst.Resolve t) with
        | IRTScalar (ETComplex64 | ETComplex128) -> true
        | ArrayElem arr -> complexElemT arr.ElemType
        | _ -> false
    let rec intElemT (t: IRType) =
        match IR.stripUnits (env.Subst.Resolve t) with
        | IRTIdxTagged (inner, _) -> intElemT inner
        | IRTScalar (ETInt32 | ETInt64) -> true
        | ArrayElem arr -> intElemT arr.ElemType
        | _ -> false
    let swapWitnessError (groups: int list list) (negate: bool) (provedLaw: Blade.Deduce.Parity)
                         (mk: string * string * string -> TypeError) =
        let ps = lambdaInfo.Params
        if isReynolds || List.isEmpty groups || ps.Length < 2 then None
        else
            claimedPairs groups
            |> List.tryPick (fun (p, q) ->
                if p < 0 || q >= ps.Length || p = q then None
                else
                    let proved = q = p + 1 && p < stage3Pairs.Length && stage3Pairs.[p] = provedLaw
                    // Complexity is judged per pair exactly as before (an
                    // operand slot of the pair, or any operand when rows and
                    // params are not 1:1); integer semantics likewise.
                    let isComplex =
                        if ps.Length = arrayTypes.Length then slotElemIs complexElemT [ p; q ]
                        else slotElemIs complexElemT []
                    // A PROVED pair skips the swap search, but an anticomm
                    // pair still has its DIAGONAL checked: the sign law does
                    // not force f(a, a) = 0 (see witnessSwapLawFailure).
                    if isComplex || (proved && not negate) then None
                    else
                        let (a, b) = (ps.[p], ps.[q])
                        let others = ps |> List.indexed |> List.filter (fun (k, _) -> k <> p && k <> q) |> List.map snd
                        let isInt = [ p; q ] |> List.exists (fun k -> k < arrayTypes.Length && intElemT arrayTypes.[k].ElemType)
                        Blade.Deduce.witnessSwapLawFailure (witnessInlineFn env) (witnessArrayKey env) a.VarId b.VarId (others |> List.map (_.VarId)) negate proved isInt lambdaInfo.Body
                        |> Option.map (fun w ->
                            let named =
                                w.Fixed |> List.choose (fun (id, c) ->
                                    others |> List.tryFind (fun o -> o.VarId = id) |> Option.map (fun o -> (o.Name, c)))
                            let text = swapWitnessText a.Name b.Name named negate w
                            match w.Failure with
                            | Blade.Deduce.DiagonalNonzero _ -> AnticommDiagonalNonzero (a.Name, b.Name, text)
                            | Blade.Deduce.SwapLawFails _ -> mk (a.Name, b.Name, text)))
    let commWitnessError () =
        swapWitnessError commGroups false Blade.Deduce.PInv CommContradictsWitness
        |> Option.orElseWith (fun () ->
            swapWitnessError lambdaInfo.AntisymGroups true Blade.Deduce.PNeg AnticommContradictsWitness)
    let contradictsIn (groups: int list list) (wanted: Blade.Deduce.Parity)
                      (needsComplexPair: bool) (mk: string -> string -> TypeError) =
        if List.isEmpty stage3Pairs || List.isEmpty groups then None
        else
            List.indexed stage3Pairs
            |> List.tryPick (fun (i, par) ->
                if par = wanted
                   && (not needsComplexPair || pairElemIsComplex i)
                   && groups |> List.exists (fun g ->
                          List.contains i g && List.contains (i + 1) g) then
                    Some (mk stage3Names.[i] stage3Names.[i + 1])
                else None)
    let stage3Err =
        [ commGroups, Blade.Deduce.PNeg, false, (fun a b -> CommContradictsBody (a, b))
          lambdaInfo.AntisymGroups, Blade.Deduce.PInv, false, (fun a b -> AntisymmContradictsBody (a, b))
          commGroups, Blade.Deduce.PConj, true, (fun a b -> CommContradictsConjBody (a, b))
          lambdaInfo.AntisymGroups, Blade.Deduce.PConj, true, (fun a b -> AntisymmContradictsConjBody (a, b)) ]
        |> List.tryPick (fun (g, wanted, needsComplexPair, mk) ->
               contradictsIn g wanted needsComplexPair mk)
        // WITNESSED ASYMMETRY (see Deduce.witnessSwapLawFailure): a declared
        // comm pair whose body the parity tables leave at PBottom may still
        // be DISPROVABLE by a concrete counterexample -- `x / y`, a lone `x`,
        // `x - 2.0 * y`. Same refusal, same stand-down under reynolds (where
        // comm is an iteration license, not a claim about the bare kernel).
        |> Option.orElseWith (fun () -> commWitnessError ())
    do! requireNone stage3Err
    // Confirm-and-pin suggestion: kernel provably commutative (or provably
    // ANTI-commutative) in an adjacent pair, nothing declared for that pair,
    // and the SAME array occupies both positions (H  intersect  Stab would license
    // compaction). Output stays DENSE until the user pins -- the suggestion is
    // the compiler proposing, never deciding. PInv proposes the inclusive
    // triangle (`comm`), PNeg the strict one (`anticomm`, zero diagonal).
    //
    // OUTER PRODUCTS ONLY. The suggestion's whole content is "your output is a
    // square with a redundant half; pin the symmetry and store the triangle",
    // and that presupposes the two operand slots span two SEPARATE axes. A
    // ZIPPED apply co-iterates: `zip(A, A) <@> lambda(x, y) -> x * y` walks ONE
    // axis feeding both slots, so the output is rank-1 and there is no triangle
    // in existence to compact (pinning `where comm` on it changes nothing --
    // sql-reduce/017). `sharedIndexTypes` is non-empty exactly for the zipped
    // arms (both inferMethodFor zip shapes via zipSharedRecords, and
    // inferObjectFor's `hasZip` split); every outer-product former passes `[]`,
    // so this gate cannot reach a real square. Note `A * A` desugars to a zip
    // too, and is likewise co-iteration, not a suppressed true positive.
    let isCoIterApply = not (List.isEmpty sharedIndexTypes)
    // FOLD LEAVES STORE NOTHING. An apply that is the direct operand of a
    // `reduce` (see `FoldOperandApplies`) has no output storage to compact, and
    // pinning `where comm` there is refused (BL3026, reduce over a compact
    // computation) -- the suggestion would be both false ("DENSE today") and
    // unactionable (an unfixable build break under --strict-pins).
    let isFoldLeaf = FoldOperandApplies.contains tLoop.Span || FoldOperandApplies.contains tKernel.Span
    if List.isEmpty iterGroups && not (List.isEmpty stage3Pairs) && not isCoIterApply && not isFoldLeaf then
        List.indexed stage3Pairs
        |> List.iter (fun (i, par) ->
            if (par = Blade.Deduce.PInv || par = Blade.Deduce.PNeg)
               && i + 1 < identities.Length
               && sameIdentity identities.[i] identities.[i + 1]
               // Synthesized wrapper params (`__of13_0`, `__k13_0`) are not
               // names a user can write in a pin -- suppress; the pack-level
               // suggestion at the deferred-former seam names the REAL pack
               // param for exactly these kernels (and under --strict-pins an
               // unactionable suggestion would be an unfixable build break).
               && not (stage3Names.[i].StartsWith "__")
               && not (stage3Names.[i + 1].StartsWith "__") then
                let (n1, n2) = (stage3Names.[i], stage3Names.[i + 1])
                let msg =
                    if par = Blade.Deduce.PInv then
                        $"kernel deduces commutative in ({n1}, {n2}) and both positions receive the same array: output storage is DENSE today. Pin `where comm({n1}, {n2})` on the kernel to opt into compact symmetric (triangular) storage."
                    else
                        $"kernel deduces ANTIcommutative in ({n1}, {n2}) (f({n2}, {n1}) = -f({n1}, {n2})) and both positions receive the same array: output storage is DENSE today. Pin `where anticomm({n1}, {n2})` on the kernel to opt into compact anticommutative (strict-triangular, zero-diagonal) storage."
                // Synthesized kernels (eta wrappers, sections) carry noSpan;
                // fall back to the former expression's source span so the
                // ghost annotation lands on `object_for(f)` / `method_for(..)`.
                // Hoisted above the warning so BOTH channels carry one span.
                let span = if tKernel.Span = noSpan then tLoop.Span else tKernel.Span
                emitWarning env "BL4010" span msg
                PinSuggestions.add msg span)

    // The BL4010 suggestion's MIRROR: the user already wrote the clause, and it
    // licenses nothing.
    //
    // Compaction -- triangular storage AND triangular iteration -- keys on an
    // IDENTITY GROUP: the SAME array occupying the commuting operand slots
    // (docs/formalism.md 11.2/12.4; `rawAxisGroups.mergesWith.acrossArray`
    // requires `sameArrayIdentity`, and shared_units_insufficient refutes the
    // weaker shared-index-space rule). A VIRTUAL `range<...>` operand never
    // materializes, so `mkVirtualArrayArrow` forces `Identity = None` and the
    // formers hand each one a fresh `AIDLiteral` -- no two range operands can
    // ever be the same array. The comm group therefore merges no axes and is
    // dropped on the floor: the emitted C++ is BYTE-IDENTICAL to the same
    // kernel with no `where` clause at all (dense `Array<T, r>`, full
    // rectangular bounds). The user asked for a storage and iteration change
    // and silently got neither -- which is the bug this warning closes.
    //
    // WARNING, not an error, and deliberately NARROWER than "comm without an
    // identity group":
    //   * Nothing is disproved (BL4013's job) and nothing unsound is licensed
    //     (BL4016's job). The program is correct, just not the one asked for --
    //     the same shape as the dropped-`omp`-clause note below, which is also
    //     a warning.
    //   * symmetry/004 and /014 are the same inertness over two distinct REAL
    //     arrays and stay silent on purpose: a real array CAN be its own
    //     identity partner at a different call site (symmetry/003 is that call
    //     site), so the clause on the kernel is not dead, merely unused here.
    //     A range operand is incapable of it at EVERY call site. "Provably
    //     inert, always" is what earns a diagnostic; "inert at this call site"
    //     does not.
    //
    // Stands down under `reynolds` (the wrapper manufactures the symmetry and a
    // clause there is an iteration licence, not a storage claim) and on a
    // co-iterated apply (one axis feeds both slots -- no square exists to
    // compact, sql-reduce/017). The `Params.Length = identities.Length` gate
    // keeps this to the one-param-per-operand outer product, where a group's
    // parameter indices ARE operand indices: a multi-rank operand
    // (`range<SymIdx<2, N>>` -- the very fix suggested below) spends two
    // parameters on one array, and its `comm(i, j)` is redundant, not inert.
    if not isReynolds && not isCoIterApply
        && lambdaInfo.Params.Length = identities.Length then
        let nameOf i =
            if i >= 0 && i < lambdaInfo.Params.Length then lambdaInfo.Params.[i].Name
            else $"#{i}"
        let inertGroup (g: int list) =
            let slots = g |> List.filter (fun k -> k >= 0 && k < identities.Length)
            if slots.Length < 2 then None
            elif slots |> List.exists (fun k ->
                     slots |> List.exists (fun q ->
                         q <> k && sameIdentity identities.[k] identities.[q])) then None
            elif slots |> List.exists (fun k ->
                     k < arrayTypes.Length && arrayTypes.[k].IsVirtual) then Some slots
            else None
        let report (kw: string) (idxTy: string) (pool: string) (g: int list) =
            match inertGroup g with
            | None -> ()
            | Some slots ->
                let names = slots |> List.map nameOf |> String.concat ", "
                let span = if tKernel.Span = noSpan then tLoop.Span else tKernel.Span
                emitWarning env "BL4017" span (sprintf
                    "`where %s(%s)` licenses nothing on this apply and is DROPPED: compact storage and triangular iteration key on an IDENTITY GROUP -- the same array occupying the commuting slots -- and a virtual `range<...>` operand never materializes, so there is no identity to key on. Storage stays dense and iteration stays rectangular; the emitted C++ is identical to this kernel with no `where` clause. Declare the symmetry in the INDEX TYPE instead: `method_for(range<%s<%d, N>>) <@> lambda(%s) -> ...` allocates the %s and visits only canonical cells."
                    kw names idxTy slots.Length names pool)
        commGroups |> List.iter (report "comm" "SymIdx" "triangular pool")
        lambdaInfo.AntisymGroups
        |> List.iter (report "anticomm" "AntisymIdx" "strict-triangular pool (no stored diagonal)")

    // NON-ADJACENT REPEAT: the same array in two commuting slots that are not
    // neighbours (`method_for(A, B, A) <@> lambda(x, y, z) where comm(x, z)`).
    // Identity groups are runs of NEIGHBOURING identical arguments
    // (docs/formalism.md 8.3), so this pair licenses nothing and the result
    // is stored dense in operand order -- correct, and exactly what the
    // reader's `K(i, j, k)` means. The by-name quotient would be the optimal
    // count (proofs/BladeAttain.v `aba_orbit_count_forced`: n*C(n+1,2) cells
    // against n^3) and the user can have it by making the repeats adjacent;
    // until 2026-10-12 the compiler took it silently and typed the result in
    // the regrouped axis order, so source-order subscripts read the wrong
    // cells. ADVISORY (BL4010, the storage-suggestion channel; not a pin
    // suggestion, so `--strict-pins` leaves it alone): the program is right,
    // a faster spelling exists, and restructuring the user's operand list is
    // the user's call (plan-fastest-way-principles P3: advise, never rewrite).
    if not isReynolds && not isCoIterApply
        && lambdaInfo.Params.Length = identities.Length then
        let nameOf i =
            if i >= 0 && i < lambdaInfo.Params.Length then lambdaInfo.Params.[i].Name
            else $"#{i}"
        let operandName i =
            if i < 0 || i >= identities.Length then $"#{i + 1}"
            else
                match identities.[i] with
                | AIDVariable n -> n
                | AIDAlias (_, n) -> n
                | AIDParameter (n, _) -> n
                | _ -> $"operand {i + 1}"
        let real k = k >= 0 && k < identities.Length
                     && not (k < arrayTypes.Length && arrayTypes.[k].IsVirtual)
        // p and q (p < q) merge iff every step p..q-1 is an adjacent pair of
        // identical operands inside the same comm group -- the chain
        // rawAxisGroups.mergesWith.acrossArray builds.
        let inSameGroup (g: int list) a b = List.contains a g && List.contains b g
        let chained (g: int list) p q =
            [ p .. q - 1 ] |> List.forall (fun i ->
                inSameGroup g i (i + 1) && real i && real (i + 1)
                && sameIdentity identities.[i] identities.[i + 1])
        let splitPair (g: int list) =
            let slots = g |> List.filter real |> List.distinct |> List.sort
            slots |> List.tryPick (fun p ->
                slots |> List.tryPick (fun q ->
                    if p < q && sameIdentity identities.[p] identities.[q] && not (chained g p q)
                    then Some (p, q) else None))
        match commGroups |> List.tryPick splitPair with
        | None -> ()
        | Some (p, q) ->
            let span = if tKernel.Span = noSpan then tLoop.Span else tKernel.Span
            let operands = [ 0 .. identities.Length - 1 ] |> List.map operandName
            let shown = operands |> String.concat ", "
            // The suggested order: move operand q to right after operand p;
            // the kernel's parameters follow the same move.
            let moved (xs: string list) =
                let x = xs.[q]
                let without = xs |> List.indexed |> List.filter (fun (i, _) -> i <> q) |> List.map snd
                let before, after = List.splitAt (p + 1) without
                before @ [ x ] @ after
            let reordered = moved operands |> String.concat ", "
            let paramIdx = [ 0 .. lambdaInfo.Params.Length - 1 ]
            let reparams = moved (paramIdx |> List.map nameOf) |> String.concat ", "
            // The clause as it reads after the move: the group's members in
            // the moved parameter order.
            let group = commGroups |> List.find (fun g -> inSameGroup g p q)
            let reclause =
                moved (paramIdx |> List.map string)
                |> List.map int
                |> List.filter (fun i -> List.contains i group)
                |> List.map nameOf |> String.concat ", "
            emitWarning env "BL4010" span (sprintf
                "`where comm` pairs `%s` with `%s` (operands %d and %d, both `%s`), but a commuting pair compacts only when its two slots are ADJACENT -- in the operand list and in the parameter list (formalism 8.3: identity groups are runs of neighbouring identical operands) -- so `method_for(%s)` is stored dense in operand order. The compact form stores one cell per orbit (n*C(n+1, 2) cells instead of n^3 for three rank-1 operands): put the pair side by side -- `method_for(%s) <@> lambda(%s) where comm(%s)` -- and read the result in that order."
                (nameOf p) (nameOf q) (p + 1) (q + 1) (operandName p) shown reordered reparams reclause)

    // Dropped-parallel-clause guard. This is the apply seam -- the ONE place a
    // loop and a kernel meet -- so it is where "the kernel declared `omp(...)`"
    // and "the lambda reaching the loop carries it" can be compared at all.
    //
    // A named function used in kernel position is eta-expanded into a wrapper
    // lambda built with NO where-clause, so its `Parallel` starts empty and the
    // callee's clause has to be surfaced explicitly (etaExpandFunctionKernel /
    // the deferred-former eta). When that surfacing is missing the clause
    // vanishes with no other trace: `Parallel = []` becomes IsOmpParallel =
    // false, the nest is emitted serial, and the generated C++ is identical to
    // a program that never asked. That was a real, long-lived silent bug. This
    // is its regression guard -- it should never fire, and firing means some
    // apply path grew a wrapper that forgets the clause again.
    if List.isEmpty lambdaInfo.Parallel then
        match lambdaInfo.Body.Kind with
        | TExprApp ({ Kind = TExprVar (fname, fnId, _) }, args)
                when args.Length = lambdaInfo.Params.Length
                     && List.forall2 (fun (arg: TypedExpr) (p: TypedParam) ->
                            match arg.Kind with
                            | TExprVar (_, aid, _) -> aid = p.VarId
                            | _ -> false) args lambdaInfo.Params ->
            match env.FuncParallel.TryGetValue fnId with
            | true, (_, strategies) when not (List.isEmpty strategies) ->
                // Two different situations share this shape, and only one of
                // them is a compiler bug. A SYNTHESIZED eta wrapper's params
                // are all `__`-prefixed (`__k<uid>_<i>` / `__of<uid>_<i>`);
                // reaching here means a surfacing site forgot the clause. A
                // wrapper the USER wrote (`lambda(x, y) -> cov(x, y)`) has
                // ordinary names, and its empty `Parallel` is just an
                // unwritten clause -- blaming the compiler for that sends the
                // reader to a bug report instead of to their own where-clause.
                let synthesized =
                    lambdaInfo.Params |> List.forall (fun p -> p.Name.StartsWith "__")
                let span = if tKernel.Span = noSpan then tLoop.Span else tKernel.Span
                if synthesized then
                    // BL9001 -- it says "this is a compiler bug" in so many
                    // words, so it renders under the internal-compiler-error code.
                    emitWarning env "BL9001" span
                        ($"internal: `where` parallel clause on '{fname}' was dropped by kernel-position eta-expansion -- the loop nest will be emitted serial. This is a compiler bug, not a source error; please report it.")
                else
                    // BL4001 -- a source-level fact, same class as the other
                    // "this clause licenses nothing here" warnings. The clause
                    // on '%s' licenses a nest built around '%s' ITSELF; this
                    // nest is built around the LAMBDA, which declared none.
                    emitWarning env "BL4001" span
                        ($"the `where` parallel clause on '{fname}' does not carry through a hand-written wrapper lambda: this nest's kernel is the lambda, and it declares none, so the nest is emitted SERIAL. Write the clause on the lambda (`lambda(..) where omp(..) -> {fname}(..)`), or drop the wrapper and use '{fname}' as the kernel directly.")
            | _ -> ()
        | _ -> ()

    let! applied = buildApplyKernelShape env
                       { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
                         ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
                         ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
                         ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym }
                       { ApplyGroups.IterGroups = iterGroups; ApplyGroups.AntisymStorageGroups = antisymStorageGroups }
                       sDimsPerArray declaredTotalWidth declaredParamCount
    return applied
}

and buildApplyKernelShape (env: TypeEnv) (applyOps: ApplyOperands) (applyGroups: ApplyGroups) (sDimsPerArray: int list) (declaredTotalWidth: int) (declaredParamCount: int) : TypeResult<TypedExpr> = typed {
    let { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
          ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
          ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
          ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym } = applyOps
    let { ApplyGroups.IterGroups = iterGroups; ApplyGroups.AntisymStorageGroups = antisymStorageGroups } = applyGroups
    // Co-iteration INDEX-PARAM form: a co-iterated kernel may declare
    // N + R parameters -- one value per co-iterated array plus the R shared
    // iteration indices -- e.g. `for (uq, ph) in range<Y, X> <@>
    // lambda(zu, zp, i, j) -> ...`. The indices ride as a TRAILING synthetic
    // range<...> operand over the shared records: expandedRows then expands
    // it to one tagged Nat<...> param per slot (unifying + tag-checking the
    // index params exactly like an explicit range<> source), and the loop
    // builder's VirtualRange elements bind them to the loop indices. The
    // virtual operand is appended LAST so the value params keep their
    // positions. Values-only kernels (arity N) are untouched, as is every
    // non-co-iteration apply.
    let (arrays, identities, arrayTypes, sDimsPerArray) =
        let idxParamCount = sharedIndexTypes |> List.sumBy (_.Rank)
        let alreadyVirtual = arrayTypes |> List.exists (_.IsVirtual)
        if not sharedIndexTypes.IsEmpty
           && idxParamCount > 0
           && not alreadyVirtual
           && lambdaInfo.Params.Length = arrays.Length + idxParamCount then
            let elemT =
                match List.tryLast sharedIndexTypes with
                | Some i -> elemTypeForIterationIndex i
                | None -> IRTScalar ETInt64
            let vExpr = mkTyped (TExprRange sharedIndexTypes) (mkVirtualArrayArrow sharedIndexTypes elemT)
            let vAt =
                match vExpr.Type with
                | ArrayElem at -> at
                | _ -> { ElemType = elemT; IndexTypes = sharedIndexTypes; IsVirtual = true; Identity = None }
            (arrays @ [vExpr],
             identities @ [AIDLiteral (env.Builder.FreshId())],
             arrayTypes @ [vAt],
             sDimsPerArray @ [idxParamCount])
        else (arrays, identities, arrayTypes, sDimsPerArray)

    // Resolve param types through Subst before reading rank. A kernel
    // param may have started as IRTInfer at lambda
    // creation but been refined during the kernel body's typecheck (e.g.,
    // reduce's kernel-arg deduction synthesizes a rank-1 IRTArray binding).
    // If we read p.Type directly here, we'd see the stale IRTInfer and
    // compute kRank = 0, which makes perRowType think the kernel takes a
    // scalar -- leading to a shape mismatch when we later unify against
    // the real rank-N source per-row type.
    //
    // When the param's resolved type is
    // STILL IRTInfer after body typechecking -- i.e., the body didn't
    // structurally constrain the param's shape (e.g. `lambda(g) -> g(0)`,
    // where g(0) is ambiguous between array indexing and function
    // application) -- fall back to the array-side rank: the kernel sees
    // a slice of rank (array rank - iterated S-dimensions). For a
    // typical `method_for(arr)` with one iterated outer dim, kRank
    // becomes (arrTy.rank - 1). This recovers the array shape
    // information that the body alone couldn't supply, and lets the
    // subsequent perRowType unification pin the param to the correct
    // Array<T, N> type rather than collapsing to the scalar element.
    let resolvedParamTypes =
        lambdaInfo.Params |> List.map (fun p -> env.Subst.Resolve p.Type)
    // Param rank: if the param's resolved type is an array, read its
    // rank directly. If the param is still IRTInfer -- meaning the body
    // didn't structurally constrain the param's shape (e.g.
    // `lambda(g) -> g(0)`, where the application is ambiguous between
    // array indexing and function application) -- fall back to the
    // array-side rank.
    //
    // The array-side rank isn't naively `(array rank - iterated
    // S-dimensions)`. For ragged-inner-dim arrays (group_by results,
    // ragged literals, depidx-inner shapes), `computeSDimsPerArray`
    // counts every SDimension equally -- the inner ragged dim included.
    // But codegen's ragged-peel pass treats those inner dims as
    // KERNEL-SIDE (peeled into a per-row binding), not iterated. To
    // match codegen semantics here, we adjust: every index with a
    // ragged-family tag contributes to the kernel's rank, not to the
    // iterated count. The result is the kernel's effective rank as
    // codegen actually sees it.
    let isRaggedInnerKind (k: IxKind) : bool =
        isRaggedRowKind k || k = IxKErrorRaggedNoPrior
    // Does the kernel body use parameter `pname` AS AN ARRAY (indexed, applied,
    // or passed to reduce/extents/rank/arity)? Distinguishes a CONSUMING
    // kernel (`lambda(g) -> reduce(g, ...)`, param is a sub-array along a
    // ragged inner dim) from an ELEMENTWISE kernel (`lambda(e) -> e * 2.0`,
    // param is a scalar). Can't rely on the param's resolved scalar type: mixed
    // int/float arithmetic legitimately leaves an untyped scalar param
    // flexible (`i * 2.0` stays Int64, promoted, not unifiable to Float64).
    // The structural use is the reliable signal.
    let paramUsedAsArray (pname: string) (body: TypedExpr) : bool =
        let isParamVar (e: TypedExpr) =
            match e.Kind with TExprVar (n, _, _) -> n = pname | _ -> false
        let rec walk (e: TypedExpr) : bool =
            let here =
                match e.Kind with
                | TExprIndex (arr, _, _) when isParamVar arr -> true
                | TExprApp (f, _) when isParamVar f -> true            // g(0) as application
                | TExprReduce (arr, _, _) when isParamVar arr -> true
                | TExprProdSum args when List.exists isParamVar args -> true
                | TExprExtents arr when isParamVar arr -> true
                | TExprRank arr when isParamVar arr -> true
                | TExprArity n when n = pname -> true
                | _ -> false
            here || childrenAny e
        and childrenAny (e: TypedExpr) : bool =
            match e.Kind with
            | TExprBinOp (_, _, l, r) -> walk l || walk r
            | TExprUnaryOp (_, x) -> walk x
            | TExprApp (f, args) -> walk f || List.exists walk args
            | TExprIndex (a, idxs, _) -> walk a || List.exists walk idxs
            | TExprReduce (a, k, i) ->
                walk a || walk k || (match i with Some e -> walk e | None -> false)
            | TExprProdSum args -> List.exists walk args
            | TExprExtents a | TExprRank a | TExprArrayNegate a
            | TExprArrayConjugate a | TExprUnique a -> walk a
            | TExprMask (a, p) -> walk a || walk p
            | TExprCompound (a, p) | TExprSparse (a, p) -> walk a || walk p
            | TExprSort (a, k) -> walk a || walk k
            | TExprIf (c, t, e2) -> walk c || walk t || walk e2
            | TExprBlock (_, Some fe) -> walk fe
            | TExprSequence es -> List.exists walk es
            | TExprTuple es -> List.exists walk es
            | TExprLet (_, _, v, b) -> walk v || walk b
            | _ -> false
        walk body
    // When the param is passed as an ARGUMENT to a function whose parameter at
    // that position is an array, that callee fixes the rank the kernel
    // consumes: `rowsum(x)` with `rowsum : Array<_,1> -> _` means `x` is a
    // rank-1 fiber. Plain application does NOT unify arg types against param
    // types, so such a param stays IRTInfer -- recover the rank from the
    // callee's signature here so the former peels the right number of outer
    // dims and perRowType below pins the param to the concrete fiber.
    let rankOfCalleeParam (pty: IRType) : int option =
        // A `Poly<T^k>` pack's base is an arity-k inference VAR (from `T^k`), not
        // a concrete array -- read the rank off its arity constraint. `mean(row: T^1)`
        // is the same: a bare `T^1` param is an arity-1 var, not `Array<..>`.
        let rankOfTy t =
            match env.Subst.Resolve t with
            | ArrayElem arr -> Some arr.IndexTypes.Length
            | IRTInfer id ->
                match env.Subst.GetArityConstraint id with
                | Some k when k > 0 -> Some k
                | _ -> None
            | _ -> None
        match env.Subst.Resolve pty with
        | IRTPoly (baseTy, _) -> rankOfTy baseTy
        | other -> rankOfTy other
    let paramRankFromFuncArg (pname: string) (body: TypedExpr) : int option =
        let isParamVar (e: TypedExpr) =
            match e.Kind with TExprVar (n, _, _) -> n = pname | _ -> false
        let mutable found : int option = None
        let rec walk (e: TypedExpr) =
            (match e.Kind with
             | TExprApp (f, args) ->
                 match env.Subst.Resolve f.Type with
                 | FuncElem (paramTys, _) ->
                     args |> List.iteri (fun i a ->
                         if found.IsNone && isParamVar a then
                             // Poly packs are variadic: every arg maps to the
                             // single pack param's element rank.
                             let pIdx = if i < paramTys.Length then i else paramTys.Length - 1
                             if pIdx >= 0 then
                                 match rankOfCalleeParam paramTys.[pIdx] with
                                 | Some r -> found <- Some r
                                 | None -> ())
                 | _ -> ()
             | _ -> ())
            typedExprChildren e |> List.iter walk
        walk body
        found
    let kernelInputRanks =
        resolvedParamTypes |> List.mapi (fun i t0 ->
            // Match through the UNIT wrapper. A `T<day>^1` annotation resolves
            // to `IRTUnitAnnotated(IRTInfer _, day)`, so matching `t0` directly
            // sends every unit-carrying abstract param past the IRTInfer arms
            // to the rank-0 default -- the unit-free twin of the same program
            // took a different path. Units say what a value MEASURES, never
            // whether its shape is known. `ArrayElem` already sees through the
            // wrapper, so it keeps reading the original.
            let t = IR.stripUnits t0
            match t with
            | ArrayElem arr -> arr.IndexTypes.Length
            | IRTInfer _ when
                (let pn = if i < lambdaInfo.Params.Length then lambdaInfo.Params.[i].Name else ""
                 (paramRankFromFuncArg pn lambdaInfo.Body).IsSome) ->
                let pn = lambdaInfo.Params.[i].Name
                (paramRankFromFuncArg pn lambdaInfo.Body).Value
            | IRTInfer id when i < arrayTypes.Length && i < sDimsPerArray.Length ->
                let arrTy = arrayTypes.[i]
                let sDims = sDimsPerArray.[i]
                let raggedInnerCount =
                    arrTy.IndexTypes
                    |> List.filter (fun idx -> isRaggedInnerKind idx.IxKind)
                    |> List.length
                // Re-attribute ragged inner dims to the kernel side ONLY when the
                // param is structurally used as an array (consuming kernel). For
                // an elementwise scalar use, the inner dim is NOT consumed and
                // must stay on the iteration/output side -> kernel rank 0, so the
                // ragged/DepIdx inner dim propagates to the output.
                //
                // A WRITTEN rank-1 annotation overrides that body scan. `T^1`,
                // `T<day>^1` and friends leave the param an ARITY-CONSTRAINED
                // inference var rather than a concrete `Array<..>` (see
                // rankOfCalleeParam), so the `ArrayElem` arm above never fires
                // for them and the structural scan below never sees the intent:
                // a body that only forwards the row into a tuple or a call
                // argument (`hanning((trow, srow), ..)`) reads as elementwise
                // and the param silently binds one ELEMENT. The annotation is a
                // declaration of the row, not a hint -- honour it directly.
                let annotatedRank =
                    match env.Subst.GetArityConstraint id with
                    | Some k when k > 0 -> Some k
                    | _ -> None
                let pname =
                    if i < lambdaInfo.Params.Length then lambdaInfo.Params.[i].Name else ""
                if raggedInnerCount > 0 && annotatedRank.IsNone
                   && not (paramUsedAsArray pname lambdaInfo.Body) then
                    0
                else
                    let trueIteratedDims = max 0 (sDims - raggedInnerCount)
                    let structural = max 0 (arrTy.IndexTypes.Length - trueIteratedDims)
                    // ...and over a DENSE operand too. There every axis is an
                    // S-dim, so the structural count above is 0, and a `R^k`
                    // row param whose body only forwards the row into a tuple
                    // (`lambda(row: R^1) -> f((row, tt))`, `lsdft((row, ts),
                    // omegas)` -- a pack argument is not a direct argument,
                    // and plain application does not unify a callee's
                    // parameter into it) was bound one ELEMENT: the apply
                    // then unified the rank-1 claim with Float64 and refused
                    // the program (BL3999). The written rank is the row
                    // depth: honour it when it names a proper fiber of a
                    // scalar-element operand.
                    match annotatedRank with
                    | Some k when structural = 0 && raggedInnerCount = 0
                                  && k < arrTy.IndexTypes.Length
                                  && (match IR.stripUnits (env.Subst.Resolve arrTy.ElemType) with
                                      | IRTScalar _ -> true
                                      | _ -> false) -> k
                    | _ -> structural
            | _ -> 0)

    // MIXED ROW/ELEMENT ANNOTATIONS over an ALL-ragged operand pack.
    // The peel binds every param off ONE offsets table at one __g, so the
    // params must agree on what a "step" is: all rows, or all elements. A
    // rank-1 annotation on one param and a rank-0 (or absent) annotation on
    // another asks for both at once, which the peel cannot express -- and
    // which would otherwise reach codegen as a silently half-bound kernel.
    //
    // Every operand must be ragged for this to be the right diagnosis. A
    // ragged operand BESIDE a dense one is a different (and still refused)
    // shape whose refusal already lands in codegen, with a message about
    // mixing -- taking it over here would move that pin's stage.
    let allRaggedOperandPack =
        not (List.isEmpty arrayTypes)
        && arrayTypes |> List.forall (fun at ->
            at.IndexTypes |> List.exists (fun idx -> isRaggedInnerKind idx.IxKind))
    let mixedRowElementAnnotations =
        allRaggedOperandPack
        && arrayTypes.Length > 1
        && kernelInputRanks.Length = arrayTypes.Length
        && (kernelInputRanks |> List.exists (fun r -> r > 0))
        && (kernelInputRanks |> List.exists (fun r -> r = 0))

    if mixedRowElementAnnotations then
        return! Error (IndexSpaceErr "co-iterating ragged or grouped arrays needs every kernel parameter to bind the same way: all ROWS (each annotated rank-1 -- `T^1`, `T<unit>^1`, or `Array<T like RaggedIdx<_>>`) or all ELEMENTS (none annotated rank-1). One offsets table drives the shared walk, so a row parameter beside an element parameter has no single step to take. Annotate every parameter, or none.")
    else
    let! applied = buildApplyKernelGrid env
                       { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
                         ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
                         ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
                         ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym }
                       { ApplyGroups.IterGroups = iterGroups; ApplyGroups.AntisymStorageGroups = antisymStorageGroups }
                       kernelInputRanks resolvedParamTypes declaredTotalWidth declaredParamCount
    return applied
}

and buildApplyKernelGrid (env: TypeEnv) (applyOps: ApplyOperands) (applyGroups: ApplyGroups) (kernelInputRanks: int list) (resolvedParamTypes: IRType list) (declaredTotalWidth: int) (declaredParamCount: int) : TypeResult<TypedExpr> = typed {
    let { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
          ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
          ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
          ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym } = applyOps
    let { ApplyGroups.IterGroups = iterGroups; ApplyGroups.AntisymStorageGroups = antisymStorageGroups } = applyGroups

    // Infer T-dimensions from the kernel's resolved return type (section 9.2).
    // If the kernel returns an array, its index types become T-dimensions
    // in the output. If it returns a scalar, there are no T-dimensions.
    let (kernelTDims, kernelOutputRank) =
        let resolved = env.Subst.Resolve(lambdaInfo.ReturnType)
        match resolved with
        | ArrayElem arr ->
            // COMPILER-MINTED EXTENTS ARE NOT OUTPUT EXTENTS. A row kernel's
            // body shapes its `T^k` parameter before this seam unifies it
            // with the row: `r * 2.0` (the elementwise demand,
            // materializeArityVar) and `reduce(r, (+))` (the fold's own
            // demand) both mint index records whose Extent is a
            // `__..._inferred_n..` placeholder, and unify never compares --
            // so never replaces -- extents. The return of `r * 2.0` then
            // carried the placeholder into the output's T-dims and codegen
            // allocated `out_extents[1] = __elementwise_inferred_n_6`, a g++
            // error (`r - mean(r)` escaped only because its partner is an
            // unresolved var, which pins nothing). The placeholder HAS an
            // answer right here: the parameter it was minted for is aligned,
            // axis for axis, with the fiber of the operand it binds. Map each
            // placeholder the resolved params carry to that fiber record and
            // read the return's placeholder records through it. Records that
            // are not placeholders (loops/121's `fs`, a kernel returning a
            // row over a DIFFERENT index) are untouched.
            //
            // The fiber may be a placeholder ITSELF -- a generic `A: T^2`
            // parameter's axes are -- and it is still the right record to map
            // to (loops/213): it names the OPERAND's axis, which the
            // shape-specialized clone substitutes with the real extent and
            // the emitter reads at run time (`A.extents[1]`), where the
            // kernel's own placeholder named nothing anywhere.
            //
            // An ABSTRACT annotation axis (`row: R<u>^1`, `w: U<u>^1` -- the
            // unit-carrying caret builds a real array type whose axes are
            // unknown, rather than the arity var a bare `R^1` is) is the same
            // kind of record: it names one parameter axis and no length, via
            // the identity mkAbstractAxisExtent gives it. It is mapped and
            // followed exactly like a minted placeholder, keyed by that
            // identity; before it was, its `?` reached the output as
            // `out_extents[1] = ?` (BL9002).
            let placeholderKey (e: IRExpr) : string option =
                match e with
                | IRParam (n, _, _) when n.StartsWith "__" && n.Contains "_inferred_n" -> Some n
                | IRParam ("?", k, _) when isAbstractAxisExtent e -> Some $"?#{k}"
                | _ -> None
            let isMintedPlaceholder (e: IRExpr) = (placeholderKey e).IsSome
            let placeholderFibers : Map<string, IRIndexType> =
                lambdaInfo.Params
                |> List.mapi (fun i p ->
                    match IR.stripUnits (env.Subst.Resolve p.Type) with
                    | ArrayElem pArr when i < arrayTypes.Length && i < kernelInputRanks.Length ->
                        let irank = kernelInputRanks.[i]
                        let at = arrayTypes.[i]
                        if irank > 0 && pArr.IndexTypes.Length = irank && at.IndexTypes.Length >= irank then
                            let fiber = at.IndexTypes |> List.skip (at.IndexTypes.Length - irank)
                            List.zip pArr.IndexTypes fiber
                            |> List.choose (fun (pIx, fIx) ->
                                placeholderKey pIx.Extent |> Option.map (fun key -> (key, fIx)))
                        else []
                    | _ -> [])
                |> List.concat
                |> Map.ofList
            // A placeholder NO parameter carries: the return is a CALL of a
            // length-preserving callee (`lambda(x) -> unit_scale(center(x))`).
            // Plain application does not unify a callee's parameter into the
            // argument, so `x` stays unpinned and the return carries the
            // CALLEE's placeholder (`__elementwise_inferred_n_31`) -- emitted
            // as an undeclared `out_extents[1]`, at top level as much as in a
            // generic body. The callee's own signature says where that length
            // comes from: a parameter axis carrying the SAME record as the
            // returned axis. Follow it down the return spine to a kernel
            // parameter and read that operand's fiber axis. A callee whose
            // returned axis is not one of its parameter's (a different length)
            // stops the walk, and the record stays as it was.
            let paramFiberAxis (pname: string) (axis: int) : IRIndexType option =
                lambdaInfo.Params
                |> List.tryFindIndex (fun p -> p.Name = pname)
                |> Option.bind (fun i ->
                    if i < arrayTypes.Length && i < kernelInputRanks.Length then
                        let irank = kernelInputRanks.[i]
                        let at = arrayTypes.[i]
                        if irank > 0 && axis < irank && at.IndexTypes.Length >= irank then
                            List.tryItem (at.IndexTypes.Length - irank + axis) at.IndexTypes
                        else None
                    else None)
            // The spine can also end at a CAPTURED array rather than a kernel
            // parameter: `lambda(row: R^1) -> scaled(row, ws)` with
            // `scaled(v: T^1, w: U^1) = w <@> ...` returns a row as long as
            // `ws`. The walk used to stop there and the output kept the
            // callee's placeholder, which reached g++ as an undeclared
            // `__method_for_inferred_n_k` (BL9002). A captured array's own
            // type names its extent outright -- read it when it is a literal.
            let capturedAxis (e: TypedExpr) (axis: int) : IRIndexType option =
                match IR.stripUnits (env.Subst.Resolve e.Type) with
                | ArrayElem a when axis < a.IndexTypes.Length ->
                    let ix = a.IndexTypes.[axis]
                    (match ix.Extent with
                     | IRLit (IRLitInt _) -> Some ix
                     | _ -> None)
                | _ -> None
            let rec spineFiber (e: TypedExpr) (axis: int) (depth: int) : IRIndexType option =
                if depth > 32 then None else
                match e.Kind with
                | TExprVar (pn, _, _) ->
                    (match paramFiberAxis pn axis with
                     | Some f -> Some f
                     | None -> capturedAxis e axis)
                | TExprBlock (_, Some fe) -> spineFiber fe axis (depth + 1)
                | TExprLet (_, _, _, b) -> spineFiber b axis (depth + 1)
                | TExprApp (f, args) ->
                    (match env.Subst.Resolve f.Type with
                     | FuncElem (paramTys, retTy) ->
                         (match IR.stripUnits (env.Subst.Resolve retTy) with
                          | ArrayElem rArr when axis < rArr.IndexTypes.Length ->
                              let rec_ = rArr.IndexTypes.[axis]
                              if not (isMintedPlaceholder rec_.Extent) then None
                              else
                                  paramTys
                                  |> List.mapi (fun pi pt -> (pi, pt))
                                  |> List.tryPick (fun (pi, pt) ->
                                      match IR.stripUnits (env.Subst.Resolve pt) with
                                      | ArrayElem pArr when pi < args.Length ->
                                          pArr.IndexTypes
                                          |> List.tryFindIndex (fun ix -> ix.Extent = rec_.Extent)
                                          |> Option.bind (fun pAxis -> spineFiber args.[pi] pAxis (depth + 1))
                                      | _ -> None)
                          | _ -> None)
                     | _ -> None)
                | _ -> None
            let tDims =
                arr.IndexTypes |> List.mapi (fun j idx -> (j, idx)) |> List.map (fun (j, idx) ->
                    match placeholderKey idx.Extent with
                    | Some key when placeholderFibers.ContainsKey key ->
                        { placeholderFibers.[key] with Kind = TDimension }
                    | Some _ ->
                        (match spineFiber lambdaInfo.Body j 0 with
                         | Some f -> { f with Kind = TDimension }
                         | None -> { idx with Kind = TDimension })
                    | _ -> { idx with Kind = TDimension })
            (tDims, tDims.Length)
        | _ ->
            // ABSTRACT rank-k return (`T^k` / `T<u>^k`). `Resolve` leaves such a
            // return an ARITY-CONSTRAINED inference var rather than a concrete
            // `Array<..>` -- the identical situation `kernelInputRanks` above has
            // to read through `GetArityConstraint`, for the identical reason
            // (see rankOfCalleeParam). Reading only `ArrayElem` here meant a
            // row-in/row-out kernel deduced ZERO T-dims: the output came out one
            // rank short, its cells typed scalar, and codegen emitted
            // `out[i] = kernel(..)` assigning an `Array<T,1>` into a `double`.
            // That is docs/formalism.md sections 7.2 / 8.4 step 5 ("T-dims of output:
            // from the kernel's `T^m`") simply not being applied when the `T^m`
            // is written abstractly -- and it is the shape of quickstart-1's
            // section 9 covariance kernel, `(a - mean(a)) * (b - mean(b))`.
            //
            // The T-dims are the FIBER the kernel consumed. A body built only
            // from rank-k parameters is rank-k over those same index types: the
            // centered row above is a row over the row axis it was handed. This
            // arm is sound precisely because it is unreachable once the body's
            // own type is concrete -- a kernel returning a DIFFERENT extent
            // (loops/121's `fs`, rank 1 over Idx<4> from a row over Idx<3>)
            // resolves to `ArrayElem` and takes the arm above, which stays
            // authoritative.
            //
            // THE RETURN IS A PARAMETER. With no arity constraint the return
            // var can still BE a row parameter's own var: in a generic body
            // (`function f(A: T^2) = object_for(lambda(x) -> x - mean(x)) <@>
            // A`) nothing pins `x` to an array while the body is checked --
            // plain application does not unify `mean`'s `T^1` into it -- so
            // `x - m` simply returns `x`'s type, and the row's axis was
            // dropped (BL3001: the output came out rank 1). The parameter's
            // rank is known (kernelInputRanks, here from `mean`'s signature),
            // and a return that IS the parameter is a row over exactly the
            // fiber that parameter consumes -- so that operand's fiber, not
            // merely the first deep-enough one, supplies the T-dims.
            let returnIsParam : (int * int) option =
                match IR.stripUnits resolved with
                | IRTInfer id ->
                    resolvedParamTypes
                    |> List.mapi (fun i pt -> (i, pt))
                    |> List.tryPick (fun (i, pt) ->
                        match IR.stripUnits (env.Subst.Resolve pt) with
                        | IRTInfer pid when pid = id
                                            && i < kernelInputRanks.Length
                                            && kernelInputRanks.[i] >= 1 ->
                            Some (kernelInputRanks.[i], i)
                        | _ -> None)
                | _ -> None
            let abstractRank =
                match IR.stripUnits resolved with
                | IRTInfer id ->
                    match env.Subst.GetArityConstraint id with
                    | Some k when k >= 1 -> Some k
                    | _ -> returnIsParam |> Option.map fst
                | _ -> None
            match abstractRank with
            | None -> ([], 0)
            | Some k ->
                // First operand whose kernel actually consumes k or more dims;
                // its innermost k are the fiber. Indexed rather than zipped:
                // a Poly pack makes kernelInputRanks and arrayTypes differ in
                // length. A return that IS a parameter reads that parameter's
                // operand first.
                let fiberAt (i: int) (at: IRArrayType) =
                    let irank =
                        if i < kernelInputRanks.Length then kernelInputRanks.[i] else 0
                    if irank >= k && at.IndexTypes.Length >= k then
                        Some (at.IndexTypes
                              |> List.skip (at.IndexTypes.Length - k)
                              |> List.map (fun idx -> { idx with Kind = TDimension }))
                    else None
                let fiberOf =
                    let preferred =
                        match returnIsParam with
                        | Some (_, i) when i < arrayTypes.Length -> fiberAt i arrayTypes.[i]
                        | _ -> None
                    match preferred with
                    | Some _ -> preferred
                    | None -> arrayTypes |> List.mapi fiberAt |> List.tryPick id
                match fiberOf with
                | Some tDims -> (tDims, k)
                // No operand supplies a k-deep fiber: leave it alone rather
                // than invent an extent, so the pre-existing behaviour (and
                // whatever diagnostic follows from it) is unchanged.
                | None -> ([], 0)

    // Mark each array's consumed fiber dimensions as T-dimensions (section 9.2). The
    // kernel consumes its innermost irank(f,i) = kernelInputRanks.[i] dims as a
    // fiber argument (e.g. a TimeIdx fiber reduced inside the kernel). Those
    // dims are NOT part of the symmetric iteration grid: re-tagging them
    // Kind = TDimension makes every downstream consumer consistent at once --
    // computeSDimsPerArray (counts only SDimension) yields the grid count,
    // buildLoopLevelStructure (builds levels only for SDimension) emits grid-
    // depth loops and leaves the fiber as the kernel's array slice, and
    // deduceOutputType symmetrizes only the grid dims. For scalar kernels
    // irank = 0, so nothing is re-tagged and behavior is unchanged.
    let gridArrayTypes =
        arrayTypes |> List.mapi (fun i at ->
            let irank = if i < kernelInputRanks.Length then kernelInputRanks.[i] else 0
            if irank <= 0 then at
            else
                // The fiber is the innermost irank dims; mark them TDimension.
                let n = at.IndexTypes.Length
                let retagged =
                    at.IndexTypes |> List.mapi (fun j idx ->
                        if j >= n - irank then { idx with Kind = TDimension } else idx)
                { at with IndexTypes = retagged })

    // A deduced WREATH TIE (docs/plan-orbit-index-types.md section 9 step 4) takes the
    // whole application off the generic axis-group thread: its iteration is the
    // segment-peeled `orb_visit` nest, driven from the OUTPUT class, and none of
    // the three analyses below has a meaning for it. They are skipped rather
    // than computed-and-ignored because all three funnel into
    // `buildLoopLevelStructure`, which REFUSES a wreath input outright -- the
    // depth-3 shape `let P = f(A,A) in g(P,P)` would otherwise die here, before
    // `deduceOutputType` ever ran. Same predicate, same arguments, one rule
    // (`IRLoopStructure.deduceWreathTie`), so this cannot disagree with what the output type
    // ends up being.
    // Per-parameter SIGN parities of the kernel body, for the tie rule's
    // soundness gate (deduceWreathTie condition 6: a '-' inner level requires
    // the kernel provably sign-odd in every tied argument). Computed ONCE with
    // the same resolver checkFunctionDecl uses -- an eta wrapper over a named
    // function resolves through the callee's FuncSignParities summary -- and
    // used three ways: the verdict below, deduceOutputType's identical call,
    // and recorded on the kernel (resolvedLambdaInfo.SignParities ->
    // IRCallable.SignParities) so codegen and the interpreter hand the tie
    // rule the SAME values. Skipped where no tie can fire (reynolds, arity
    // < 2); the gate reads a missing list as all-unknown.
    let kernelSignParities : KernelSignParity list =
        if isReynolds || lambdaInfo.Params.Length < 2 then []
        else
            let signResolver (calleeId: IRId) =
                match env.FuncSignParities.TryGetValue calleeId with
                | true, ps -> Some ps
                | _ -> None
            Blade.Deduce.deduceSignParities signResolver lambdaInfo.Params lambdaInfo.Body
            |> List.map (function
                | Blade.Deduce.SOdd -> KspOdd
                | Blade.Deduce.SEven -> KspEven
                | Blade.Deduce.SUnknown -> KspUnknown)
    let wreathVerdict =
        deduceWreathTie gridArrayTypes identities iterGroups antisymStorageGroups
                        kernelTDims (kernelInputRanks |> List.exists (fun r -> r > 0)) isReynolds
                        kernelSignParities
    // Surface the gate's refusal HERE -- the one seam that can say it to the
    // user (codegen and the interpreter run downstream of a successful
    // typecheck; their arms are loud backstops). KspEven is the
    // CommContradictsBody analog one level up; KspUnknown refuses too, because
    // per-argument oddness is a claim no clause declares, so there is no user
    // word to trust -- the BL4015 inheritance-gate precedent (the rationale is
    // spelled out at deduceWreathTie condition 6).
    let wreathGateErr =
        match wreathVerdict with
        | IRLoopStructure.WreathKernelNotOdd (argPos, parity, innerLevels) ->
            let pname =
                match List.tryItem argPos lambdaInfo.Params with
                | Some (p: TypedParam) -> p.Name
                | None -> $"argument {argPos}"
            let proved =
                match parity with
                | KspEven -> "provably sign-EVEN (h(-p, q) = h(p, q))"
                | _ -> "of UNKNOWN sign parity"
            Some (WreathTieKernelNotOdd (pname, proved, ppOrbitLevels innerLevels))
        | _ -> None
    do! requireNone wreathGateErr
    let wreathTie =
        match wreathVerdict with IRLoopStructure.WreathTied t -> Some t | _ -> None
    // A wreath INPUT that did NOT earn a tie has no iteration at all: only
    // the segment-peeled `orb_visit` nest walks a wreath pool, driven by the
    // OUTPUT class, so an application that doesn't produce one (unary map,
    // non-comm binary, distinct wreath operands) can't be iterated.
    // `buildRawLoopLevels` already refuses it via `failwith` (surfaces as
    // BL9001 "internal compiler error"), but this is a language limitation,
    // not a compiler bug -- say so on the user-facing channel before the
    // three analyses below reach that backstop (kept as a loud fallback for
    // any future producer that slips past this gate).
    let wreathInputRefusal =
        if wreathTie.IsSome then None
        else
            gridArrayTypes
            |> List.tryPick (fun at ->
                at.IndexTypes |> List.tryFind (fun ix ->
                    ix.Kind = SDimension && ix.Symmetry = SymWreath))
            |> Option.map (fun ix ->
                OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix),
                                         "kernel application over a wreath-typed argument \
(only a comm/anticomm tie over EVERY argument produces a wreath output, and only that shape has a traversal nest)"))
    do! requireNone wreathInputRefusal
    // Grouping/iteration reads `iterGroups` (comm  union  anticomm): a declared
    // antisymmetric pair fuses its axes and iterates the simplex exactly like
    // a comm pair -- only the strictness (IR's StrictOffset) and the stored
    // symmetry class differ, and both of those are decided downstream.
    let states =
        if wreathTie.IsSome then []
        else computeAllSymcomStates identities gridArrayTypes iterGroups (computeSDimsPerArray gridArrayTypes)
    let triLevels =
        if wreathTie.IsSome then []
        else computeTriangularLevels gridArrayTypes identities iterGroups (computeSDimsPerArray gridArrayTypes)
    let speedup =
        if wreathTie.IsSome then 1L
        else computePartialProductSpeedup gridArrayTypes identities iterGroups (computeSDimsPerArray gridArrayTypes)

    // Unify each kernel parameter with the source array's per-row type.
    // This catches mismatches like a String-typed kernel param applied to
    // a Float64 array, which would otherwise silently miscompile because
    // elem types weren't compared.
    //
    // Per-row type: for a source array of rank R and kernel param of rank K,
    // the kernel sees the array sliced at the outer R-K dims: kRank=0 -> the
    // elem type; kRank=R -> the whole array (degenerate); 0<kRank<R -> an
    // array with the inner kRank dims preserved. Length mismatches (arity
    // errors) are caught earlier, so a defensive zip is sufficient.
    let perRowType (arrTy: IRArrayType) (kRank: int) : IRType =
        let r = arrTy.IndexTypes.Length
        if kRank <= 0 then
            arrTy.ElemType  // Scalar kernel param sees one element per iter.
        elif kRank >= r then
            mkArrayLike arrTy  // Kernel wants the whole array (degenerate).
        else
            let nOuter = r - kRank
            let innerDims = arrTy.IndexTypes |> List.skip nOuter
            mkArrayLike { arrTy with IndexTypes = innerDims }

    // Expand each source into its kernel-facing param row type(s). A REAL array
    // contributes ONE param (its per-row slice). A VIRTUAL source (range<...>)
    // contributes ONE param PER index-type slot -- the index value at that slot --
    // so range<I1, I2> presents (i1, i2) to the kernel. For a single-slot virtual
    // source this equals perRowType's single-slot result (the arrow's
    // ElemType), so single-index behavior is unchanged.
    let expandedRows =
        arrayTypes
        |> List.mapi (fun i at ->
            if at.IsVirtual then
                // One param per RANK SLOT: a multi-rank index type (e.g. SymIdx<2,N>,
                // or a CompoundIdx of mask-rank R) contributes Rank coordinate params,
                // per the rank rule (kernel index slots = iteration rank). For rank-1
                // (dense) indices this is one param per index type, unchanged from 1b.
                // CAVEAT for a TRIANGULAR slot: "the index value at that slot" is the
                // intent, but the lowering delivers the PACKED STORAGE COORDINATE
                // (prefix offset) -- and elemTypeForIterationIndex tags every component
                // with the GROUP's tag rather than the component space's. Both
                // divergences are assessed in docs/formalism.md 7.3 and pinned by
                // tests/corpus/loops/170-175.
                at.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank (elemTypeForIterationIndex idx))
            else
                let kRank = if i < kernelInputRanks.Length then kernelInputRanks.[i] else 0
                [perRowType at kRank])
        |> List.concat

    // ---- THE WIDTH-SCHEMA MATCH ----
    // docs/plan-tuples-vs-arg-packs.md 6c. The parameter
    // list is a WIDTH SCHEMA over the pack's flat leaf sequence: an
    // unannotated parameter consumes one leaf, a `Tuple<k>` parameter consumes
    // k. Totals must agree, or it is a hard error.
    //
    // `expandedRows` IS the leaf sequence at this seam. Its two existing rules
    // are preserved verbatim because they are what makes the leaf count right:
    // a REAL array contributes one row (its per-row slice), a VIRTUAL source
    // (`range<I, J>`) contributes one row PER INDEX SLOT -- which is why the
    // co-iteration index-param form needs no special case here (the synthetic
    // range operand appended above already widened the leaf sequence by
    // exactly the number of index params). `zip` contributed its children as
    // separate operands upstream, so it is already k leaves.
    //
    // The SLICING half already happened: the tuple-param expansion at the top
    // of this function turned every `Tuple<k>` param into k row params plus a
    // body-entry let. So by here the schema is all-width-1 and the match is a
    // length comparison again -- but now it is CHECKED (`declaredTotalWidth`
    // is what the pack must supply), where it used to be waved through.
    let schemaRows : IRType list option =
        if lambdaInfo.Params.Length <> expandedRows.Length then None
        else Some expandedRows

    // STRICT quantity slots for KERNEL parameters (BL3010, the <@> twin of
    // the dispatch-seam check in dispatchAppOrIndex): a lambda param DECLARED
    // with a quantity type (`lambda(x: Float<speed>) -> ...`) rejects a row
    // whose element signature does not carry that nominal. Unannotated params
    // (still inference vars, sig None) and structural-unit params keep the
    // permissive unification below exactly.
    let kernelQuantityClash =
        match schemaRows with
        | None -> None
        | Some expandedRows ->
            let sigOf (t: IRType) =
                match env.Subst.Resolve t with
                | ArrayElem at ->
                    let e = env.Subst.Resolve at.ElemType
                    (IR.getUnits e, not e.IsIRTInfer)
                | IRTInfer _ -> (None, false)
                | resolved -> (IR.getUnits resolved, true)
            List.zip resolvedParamTypes expandedRows
            |> List.mapi (fun i (paramTy, row) -> (i, sigOf paramTy, sigOf row))
            |> List.tryPick (fun (i, (pu, _), (ru, rConcrete)) ->
                match pu with
                | Some pu when pu.Nominal.IsSome
                               && rConcrete
                               && (match ru with
                                   | Some r -> r.Nominal <> pu.Nominal
                                   | None -> true) ->
                    let got =
                        match ru with
                        | None -> "bare (it carries no unit signature)"
                        | Some r ->
                            match r.Nominal with
                            | Some qn -> $"the quantity '{qn}'"
                            | None -> $"structurally dimensioned ({ppUnitSig r})"
                    Some (QuantityArgMismatch (i + 1, pu.Nominal.Value, got))
                | _ -> None)

    // STRICT static EXTENT agreement for KERNEL parameters (BL3016, the <@>
    // twin of the extentClash check at the dispatch seam). The unification
    // below is real, but it is blind here: `unify`'s ArrayElem arm compares
    // element type, tag and symmetry and explicitly NOT extents ("extents
    // never compared", Unify.fs) -- correct for inference, wrong as an
    // agreement check, because codegen does not treat a literal extent as a
    // runtime value. A parameter's LITERAL extent is baked into the emitted
    // subscripts and loop bounds, so `lambda(row: Array<Float64 like Idx<5>>)`
    // iterated over a 3-wide row emits `row[3]`/`row[4]` past the row -- and
    // past the whole allocation on the last row. Observed as silent wrong
    // data, not a reliable crash.
    //
    // Literal-vs-literal only, ranks equal. When either side's extent is
    // symbolic (`Idx<n>`, ragged/compound/opaque markers) codegen emits a
    // runtime `.extents[d]` read and is already correct; those keep the
    // historical looseness, as does an unresolved row.
    let kernelExtentClash =
        match schemaRows with
        | None -> None
        | Some expandedRows ->
            List.zip resolvedParamTypes expandedRows
            |> List.mapi (fun i pair -> (i, pair))
            |> List.tryPick (fun (i, (paramTy, row)) ->
                match env.Subst.Resolve paramTy, env.Subst.Resolve row with
                | ArrayElem pa, ArrayElem ra when pa.IndexTypes.Length = ra.IndexTypes.Length ->
                    List.zip pa.IndexTypes ra.IndexTypes
                    |> List.mapi (fun d pair -> (d, pair))
                    |> List.tryPick (fun (d, (pi, ri)) ->
                        match tryEvalIntIR pi.Extent, tryEvalIntIR ri.Extent with
                        | Some pe, Some re when pe <> re ->
                            Some (ExtentArgMismatch (i + 1, d + 1, pe, re))
                        | _ -> None)
                | _ -> None)

    // A PACK parameter (`Poly<T^k>`) absorbs the whole argument list and has no
    // fixed width, so the schema does not describe it. Every supported spelling
    // eta-expands to the pack width BEFORE reaching this seam (the deferred-
    // former arm in inferApply), so a Poly param surviving here means some
    // other route built the kernel -- stand down rather than invent a width.
    // Disjoint from the tuple arm by construction: Unify has no
    // `IRTPoly ~ IRTTuple` rule (5.3).
    let hasPolyParam =
        resolvedParamTypes |> List.exists (_.IsIRTPoly)

    let kernelParamUnifyResult =
        match kernelQuantityClash, kernelExtentClash with
        | Some err, _ -> Error err
        | None, Some err -> Error err
        | None, None ->
        match schemaRows with
        | Some rows ->
            // Use resolved types so the unify call sees the same shape we used
            // to compute kRank. (Reading param.Type directly could be stale.)
            // An INDEX VALUE fed to a parameter declared a plain INTEGER is
            // the integer position it is (formalism 3.10) -- the rule a call
            // already applies (`lambda(i) -> g(i)` over `range<I>` with
            // `g(i: Int64)`). Unifying `Int64` with `Nat<I>` refused the same
            // program spelled `method_for(range<I>) <@> g` (the named kernel
            // is eta-wrapped with its signature's parameter types) or with an
            // annotated `lambda(i: Int64)`. Only where `unify` refuses exactly
            // that pair (every pairing it accepts keeps its binding), and only
            // the tag is dropped: a float or a bool row is still refused.
            let indexMeetsIntParam (paramTy: IRType) (row: IRType) =
                match env.Subst.Resolve paramTy, env.Subst.Resolve row with
                | IRTScalar (ETInt64 | ETInt32),
                  (IRTNat _ | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _)) -> true
                | _ -> false
            (List.zip resolvedParamTypes rows)
            |> List.fold (fun acc (paramTy, row) ->
                acc |> Result.bind (fun () ->
                    match unify env.Subst paramTy row with
                    | Error _ when indexMeetsIntParam paramTy row -> Ok ()
                    | r -> r))
                (Ok ())
        | None when hasPolyParam || lambdaInfo.Params.IsEmpty || expandedRows.IsEmpty ->
            Ok ()
        | None ->
            // THE HARD ARITY ERROR. This slot used to read
            //   `Ok ()  // Arity mismatch handled elsewhere`
            // and "elsewhere" did not exist (2.4): under-arity silently dropped
            // operands and still iterated their axes (3.4's 12-cell program),
            // over-arity passed `check` and died in g++ on an undeclared temp.
            // Both are now this message.
            let widthDesc =
                if declaredTotalWidth = declaredParamCount then
                    $"""{declaredParamCount} parameter{(if declaredParamCount = 1 then "" else "s")}"""
                else
                    $"""{declaredParamCount} parameter{(if declaredParamCount = 1 then "" else "s")} of total width {declaredTotalWidth}"""
            let steer =
                if declaredParamCount = 1 && declaredTotalWidth = 1 && expandedRows.Length > 1 then
                    // 5.2's genuinely undecidable shape: one unannotated param
                    // over a width-k pack. The pack reading and the tuple
                    // reading are both well-formed and disagree, and the body
                    // cannot vote (it is inferred before the param is bound).
                    // Demand the annotation, exactly as BL3010 does for
                    // quantities.
                    $" Write {expandedRows.Length} parameters to take the operands separately, or annotate the single parameter `Tuple<{expandedRows.Length}>` to receive them as one tuple."
                elif declaredTotalWidth > expandedRows.Length then
                    " Drop the extra parameters, or supply more operands."
                else
                    " Add parameters for the extra operands, or group them into a parameter annotated `Tuple<N>`."
            Error (KernelPackArity
                     (sprintf "kernel arity: this application supplies %d operand%s to a kernel with %s.%s"
                              expandedRows.Length
                              (if expandedRows.Length = 1 then "" else "s")
                              widthDesc steer))

    do! kernelParamUnifyResult
    let! applied = buildApplyKernelBody env
                       { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
                         ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
                         ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
                         ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym }
                       { ApplyGroups.IterGroups = iterGroups; ApplyGroups.AntisymStorageGroups = antisymStorageGroups }
                       { ApplyKernelShape.KernelInputRanks = kernelInputRanks
                         ApplyKernelShape.GridArrayTypes = gridArrayTypes; ApplyKernelShape.KernelTDims = kernelTDims
                         ApplyKernelShape.KernelSignParities = kernelSignParities; ApplyKernelShape.States = states
                         ApplyKernelShape.TriLevels = triLevels; ApplyKernelShape.KernelOutputRank = kernelOutputRank
                         ApplyKernelShape.Speedup = speedup }
                       schemaRows
    return applied
}

and buildApplyKernelBody (env: TypeEnv) (applyOps: ApplyOperands) (applyGroups: ApplyGroups) (kernelShape: ApplyKernelShape) (schemaRows: IRType list option) : TypeResult<TypedExpr> = typed {
    let { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
          ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
          ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
          ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym } = applyOps
    let { ApplyGroups.IterGroups = iterGroups; ApplyGroups.AntisymStorageGroups = antisymStorageGroups } = applyGroups
    let { ApplyKernelShape.KernelInputRanks = kernelInputRanks
          ApplyKernelShape.GridArrayTypes = gridArrayTypes; ApplyKernelShape.KernelTDims = kernelTDims
          ApplyKernelShape.KernelSignParities = kernelSignParities; ApplyKernelShape.States = states
          ApplyKernelShape.TriLevels = triLevels; ApplyKernelShape.KernelOutputRank = kernelOutputRank
          ApplyKernelShape.Speedup = speedup } = kernelShape
    // Reject unsupported / miscompiling kernel-body shapes now that the
    // params are bound to the iterated element/row types:
    // (1) Complex accessor on a ROW param (`real(z)` etc. whose operand
    //     unified to an ARRAY, e.g. a zip row): DEFERRED at body-inference
    //     time (operand was still an infer var, typed scalar Float64
    //     without constraining it), so lowering would embed the
    //     uncaptured param in a broadcast kernel, giving an IR
    //     dangling-VarId (BL6001). Steer to a scalar-per-element map.
    // (2) Array-valued ELEMENTWISE body (`ra * rb` between two row
    //     params): re-synthesizes into compute(method_for(zip...)) with
    //     output rank >= 1, which codegen collapses to (sum ra)(sum rb)
    //     in expression position -- a silent miscompile. A bare
    //     array-param passthrough is fine; reject and steer to
    //     prodsum/reduce otherwise.
    let rec findBadComplexAccessor (e: TypedExpr) : string option =
        match e.Kind with
        | TExprUnaryOp ((OpReal | OpImag | OpArg) as op, operand)
                when (match env.Subst.Resolve operand.Type with ArrayElem _ -> true | _ -> false) ->
            Some (match op with OpReal -> "real" | OpImag -> "imag" | _ -> "arg")
        | _ -> typedExprChildren e |> List.tryPick findBadComplexAccessor
    // (3) A REAL-ONLY math intrinsic whose operand unified to COMPLEX.
    //     Scalar position rejects this eagerly, but in a kernel body the
    //     operand is a param, and both real-only families DEFER rather than
    //     pin so the unit walk can see the element's annotation
    //     (floor/ceil/log10's IRTInfer arm, inferBinaryIntrinsic's `pin`).
    //     Nothing downstream catches it: kernelBodyUnits is units-only, and
    //     the complex re-stamp below keys on the OPERAND's element type
    //     without consulting isComplexMathIntrinsic, so it would UPGRADE
    //     floor to complex and lower to std::floor(std::complex<double>)
    //     -- measured: g++ answers "no matching function", which is a C++
    //     error where a Blade one belongs. Reject here instead.
    //
    //     Membership in mathIntrinsics, not a name list, is what excludes
    //     `abs`: it is spelled OpMath "abs" but is not one of the
    //     intrinsics, and abs of a complex is the legal real magnitude.
    //     Every binaryMathIntrinsic is real-only by construction, so
    //     OpMath2 needs no such filter.
    //
    //     Only a RESOLVED complex operand fires. An unresolved one belongs
    //     to an enclosing kernel still being inferred, and its own second
    //     pass rechecks this body (see the NESTED-APPLY DEFERRAL below).
    let rec findBadComplexIntrinsic (e: TypedExpr) : string option =
        let isComplexOperand (o: TypedExpr) =
            match IR.stripUnits (env.Subst.Resolve o.Type) with
            | IRTScalar (ETComplex64 | ETComplex128) -> true
            | _ -> false
        match e.Kind with
        | TExprUnaryOp (OpMath name, operand)
                when isMathIntrinsic name && not (isComplexMathIntrinsic name)
                     && isComplexOperand operand -> Some name
        | TExprBinOp (_, OpMath2 name, l, r)
                when isComplexOperand l || isComplexOperand r -> Some name
        | _ -> typedExprChildren e |> List.tryPick findBadComplexIntrinsic
    // (3b) A CAST whose deferred operand resolved to a class the cast
    //     refuses eagerly in scalar position: a complex source under a
    //     real/int target, or the float->int rounding gate. Same
    //     deferral story as (3): in a kernel body the operand is a
    //     param, so inferNumericCast stamps the (fixed) target type and
    //     leaves the legality judgment to this walk, once apply-site
    //     unification has bound the param. The gate reads the operand's
    //     KIND -- `Int64(floor(v))` licenses, `Int64(v)` refuses -- which
    //     is final regardless of when the types resolved.
    let rec findBadDeferredCast (e: TypedExpr) : TypeError option =
        let srcElem (o: TypedExpr) =
            match IR.stripUnits (env.Subst.Resolve o.Type) with
            | IRTScalar et -> Some et
            | _ -> None
        let bad =
            match e.Kind with
            | TExprUnaryOp (OpCast cname, operand) ->
                (match srcElem operand, castTargetOf cname with
                 // The cast domain (formalism 2.4): numbers and Bool only.
                 | Some ((ETString | ETUnit) as s), Some _ ->
                     Some (InvalidCast $"{cname}() expects a numeric operand; got {ppIRType (IRTScalar s)}.")
                 | Some (ETComplex64 | ETComplex128), Some (ETInt32 | ETInt64 | ETFloat32 | ETFloat64) ->
                     Some (InvalidCast ($"{cname}() cannot cast a complex value: project a real component first -- "
                                        + "real(z), imag(z), abs(z), or arg(z)."))
                 | Some (ETFloat32 | ETFloat64), Some (ETInt32 | ETInt64) when
                         (match operand.Kind with
                          | TExprUnaryOp (OpMath ("floor" | "ceil"), _) -> false
                          | _ -> true) ->
                     Some (InvalidCast ($"{cname}() would truncate a float: spell the rounding at the cast site -- "
                                        + $"{cname}(floor(x)) or {cname}(ceil(x))."))
                 | _ -> None)
            | _ -> None
        match bad with
        | Some _ -> bad
        | None -> typedExprChildren e |> List.tryPick findBadDeferredCast
    // AN ARRAY-VALUED KERNEL RETURN IS NOW SUPPORTED (stage S3,
    // docs/plan-kernel-body-materialization.md manifestation M-C). The S0
    // guard that stood here -- "kernelOutputRank >= 1 and the body is not a
    // bare row passthrough" -> reject -- is gone, together with its pin
    // (diagnostics/069_array_valued_kernel_return_rejects, deleted; its
    // value twin is loops/121).
    //
    // What it was holding the line against, and what replaced it:
    // `kernelTDims` is computed just above and `deduceOutputType` has always
    // appended it to the output TYPE, so the deduced grid was already
    // rank-(outer+inner). Nothing sized the emitted grid to match: codegen
    // built the extents table from the LOOP BINDINGS alone, so a rank-2 grid
    // got a one-entry table ({ 2 }), the inner extent read as 0, and the
    // program printed `[[], []]` with no diagnostic anywhere -- the same
    // silent-wrong-answer class as func-arrays/011's rank-2 literal of
    // computed rows. S3 closes exactly that gap in CodeGen (the trailing
    // T-dim extents now come from the output type, and the nest writes a
    // whole row per outer cell); the type side needed no change at all.
    //
    // Rejection (2) below survives on its own terms: a complex ACCESSOR
    // (real/imag/arg) applied to an array operand is still refused, because
    // that is an elementwise array-valued body the inline path collapses to
    // a scalar -- a different failure from the one S3 fixed.
    do! requireNone (findBadComplexAccessor lambdaInfo.Body |> Option.map IntrinsicComplexScalarOnly)
    do! requireNone (findBadComplexIntrinsic lambdaInfo.Body |> Option.map IntrinsicNotComplex)
    do! requireNone (findBadDeferredCast lambdaInfo.Body)
    // (3c) THE MIXING RULE over the kernel body, now that apply-site
    //     unification has bound its parameters (formalism 2.4): an
    //     operator whose operand was an open parameter when the body was
    //     typed (`lambda(x) -> x * 2` over a Float64 array) is judged on
    //     the bound element -- refused when concrete, recorded as a
    //     generic obligation when the element is the enclosing
    //     declaration's own variable.
    let rec findImplicitMix (e: TypedExpr) : (Span * string) option =
        let here =
            match e.Kind with
            | TExprBinOp ((Elementwise | Outer), op, l, r) ->
                recordGenericMixObligation env op l r
                judgeNumericMix env op l r
                |> Option.map (fun (leftOff, msg) -> ((if leftOff then l.Span else r.Span), msg))
            | _ -> None
        match here with
        | Some _ -> here
        | None -> typedExprChildren e |> List.tryPick findImplicitMix
    do! (match findImplicitMix lambdaInfo.Body with
         | Some (sp, msg) ->
             if sp.StartLine > 0 then setCurrentExprSpan sp
             Error (ImplicitNumericMix msg)
         | None -> Ok ())
    // A kernel PARAMETER applied to arguments, now that apply-site
    // unification has bound it: `lambda(i, j, a, b) -> a(i, j)` over
    // `for (A, B) in range<M, N>` binds `a` to the M index (cells come
    // first, then indices) and applying it reached g++. See
    // TypeCheckSupport.nonCallableHead.
    let paramIds = lambdaInfo.Params |> List.map (_.VarId) |> Set.ofList
    let rec appliedParam (e: TypedExpr) : TypeError option =
        match e.Kind with
        | TExprApp ({ Kind = TExprVar (_, vid, _) } as f, args)
                when paramIds.Contains vid && not (List.isEmpty args) ->
            (match nonCallableHead env.Subst f with
             | Some t -> Some (InvalidApplication t)
             | None -> args |> List.tryPick appliedParam)
        | _ -> typedExprChildren e |> List.tryPick appliedParam
    do! requireNone (appliedParam lambdaInfo.Body)
    // A HALO WINDOW PASSED ON, now that apply-site unification has bound
    // the kernel's parameter to it: `lambda(w) -> d(w)` with a LAMBDA `d`
    // whose body applies its parameter (`d = lambda(w) -> A(w(1)) - ..`).
    // That call was judged while `w` was open, so nothing compared the
    // window with `d`'s parameter, which settled as a function of Int64
    // and g++ was handed the window's integer for it (BL9002). A window is
    // read only in the kernel that binds it (TypeEnv HaloWindowEscapes);
    // a declared callee is refused by dischargeArrowApp instead.
    let windowParams =
        lambdaInfo.Params
        |> List.filter (fun p ->
            match IR.stripUnits (env.Subst.Resolve p.Type) with
            | IRTIdxTagged (_, IRefNamed t) -> t.StartsWith haloWinTagPrefix
            | _ -> false)
        |> List.map (_.VarId)
        |> Set.ofList
    let rec windowEscape (e: TypedExpr) : TypedExpr option =
        let here =
            match e.Kind with
            | TExprApp (f, args) ->
                (match IR.stripUnits (env.Subst.Resolve f.Type) with
                 | FuncElem (slots, _) ->
                     let n = min slots.Length args.Length
                     List.zip (List.truncate n args) (List.truncate n slots)
                     |> List.tryPick (fun (a, s) ->
                         match a.Kind with
                         | TExprVar (_, vid, _) when windowParams.Contains vid ->
                             (match IR.stripUnits (env.Subst.Resolve s) with
                              | IRTInfer sid ->
                                  let applied =
                                      env.PendingArrowApps.Exists(fun app ->
                                          match IR.stripUnits (env.Subst.Resolve app.Head.Type) with
                                          | IRTInfer h -> h = sid
                                          | _ -> false)
                                  if applied then Some a else None
                              | ArrayElem _ | IRTArrow _ -> Some a
                              | _ -> None)
                         | _ -> None)
                 | _ -> None)
            | _ -> None
        match here with
        | Some a -> Some a
        | None -> typedExprChildren e |> List.tryPick windowEscape
    do! (match (if windowParams.IsEmpty then None else windowEscape lambdaInfo.Body) with
         | Some a ->
             if a.Span.StartLine > 0 then setCurrentExprSpan a.Span
             Error (HaloWindowEscapes ())
         | None -> Ok ())
    // A KERNEL PARAMETER PASSED TO A DECLARED INDEX PARAMETER of another
    // index type: `method_for(range<I>) <@> lambda(i) -> g(A, i)` with
    // `g(.., i: Nat<J>)`. The call was judged while `i` was open, so the
    // judgment compared nothing, and the kernel's binding of `i` to
    // `Nat<I>` (above) never met `g`'s declared `Nat<J>` -- the call
    // `g(A, (1 : I))` refuses was accepted here. Judged now, located at
    // the argument, with the same refusal (BL3001). Only a NAMED index on
    // both sides, and only a bare kernel parameter as the argument (the
    // one argument the judgment saw open).
    let kernelParamIds = lambdaInfo.Params |> List.map (_.VarId) |> Set.ofList
    let rec kernelIndexArgClash (e: TypedExpr) : (TypedExpr * TypeError) option =
        let here =
            match e.Kind with
            | TExprApp (f, args) ->
                (match IR.stripUnits (env.Subst.Resolve f.Type) with
                 | FuncElem (slots, _) when not (slots |> List.exists (fun s -> (env.Subst.Resolve s).IsIRTPoly)) ->
                     let n = min slots.Length args.Length
                     List.zip (List.truncate n args) (List.truncate n slots)
                     |> List.indexed
                     |> List.tryPick (fun (i, (a, s)) ->
                         match a.Kind with
                         | TExprVar (_, vid, _) when kernelParamIds.Contains vid ->
                             (match IR.stripUnits (env.Subst.Resolve a.Type), IR.stripUnits (env.Subst.Resolve s) with
                              | (IRTIdxTagged (_, IRefNamed an) as aTy), (IRTIdxTagged (_, IRefNamed sn) as sTy)
                                  when an <> sn && not (an.StartsWith "__") && not (sn.StartsWith "__") ->
                                  let desc = match f.Kind with TExprVar (nm, _, _) -> $"'{nm}'" | _ -> "this function"
                                  Some (a, ArgTypeMismatch (i + 1, desc, ppIRType sTy, ppIRType aTy))
                              | _ -> None)
                         | _ -> None)
                 | _ -> None)
            | _ -> None
        match here with
        | Some r -> Some r
        | None -> typedExprChildren e |> List.tryPick kernelIndexArgClash
    do! (match kernelIndexArgClash lambdaInfo.Body with
         | Some (a, err) ->
             if a.Span.StartLine > 0 then setCurrentExprSpan a.Span
             Error err
         | None -> Ok ())
    // KERNEL CO-ITERATION AGREEMENT (BL3016 extent / BL3027 name), the
    // `<@>` twin of the call-site ladder's coIterClash. A kernel body that
    // zips its parameters -- directly, through elementwise maps, or by
    // forwarding them to a function that does (the eta wrapper of a named
    // kernel is `lambda(__k0, __k1) -> cov(__k0, __k1)`) -- was inferred
    // while those parameters were open, so the zip had no extents and no
    // names to compare, and the call inside the wrapper saw open
    // arguments. Only HERE, after the parameters met their operands, are
    // the fibers known: `method_for(R, H) <@> cov` walked a DayIdx fiber
    // with an HourIdx one (and, with unequal literal extents, ran off the
    // shorter one's end) while `cov(R(0), H(0))` was refused. Derived now
    // rather than read from FuncCoIterObligations so a plain lambda kernel
    // gets the same judgment as a named one.
    let kernelCoIterClash =
        let obs = coIterObligations env (lambdaInfo.Params |> List.map (_.VarId)) lambdaInfo.Body
        if List.isEmpty obs then None
        else
            let owner =
                match lambdaInfo.Body.Kind with
                | TExprApp ({ Kind = TExprVar (fname, _, _) }, _)
                        when lambdaInfo.Params |> List.forall (fun p -> p.Name.StartsWith "__k") ->
                    $"the kernel '{fname}'"
                | _ -> "this kernel"
            // Judged on the ROWS this application hands each parameter,
            // not on the parameters' own types: a body that materialized
            // `a` and `b` while they were open gave them one UNNAMED type
            // (and unify is name-permissive and blind to extents), so after
            // the bind they still say nothing about the fibers.
            let typeAt (i: int) =
                match schemaRows with
                | Some rows -> List.tryItem i rows
                | None -> List.tryItem i lambdaInfo.Params |> Option.map (_.Type)
            coIterVerdict env obs typeAt
            |> Option.map (function
                | CoIterExtentArgs (a, b, ea, eb) -> CoIterKernelExtentMismatch (owner, a + 1, Some (b + 1), ea, eb)
                | CoIterExtentBody (a, ea, l) -> CoIterKernelExtentMismatch (owner, a + 1, None, ea, l)
                | CoIterNameArgs (a, b, ha, hb) ->
                    CoIterIndexMismatch ($"kernel operands {a + 1} and {b + 1}", owner,
                                         coIterHeadName ha, coIterHeadName hb, ppExtentOf ha.Extent)
                | CoIterNameBody (a, h, bh) ->
                    CoIterIndexMismatch ($"kernel operand {a + 1} and an array the kernel body fixes", owner,
                                         coIterHeadName h, coIterHeadName bh, ppExtentOf h.Extent))
    do! requireNone kernelCoIterClash
    // HALO-EXTENT AGREEMENT (BL3016, the halo twin of kernelExtentClash).
    // A halo's declared inner extent is written by hand while the array it
    // windows over has its own extent; nothing else ever compares them.
    // The window walk is bounded by the DECLARED extent, so an oversized
    // halo reads past the array's allocation (access violation) and an
    // undersized one silently emits fewer windows -- a wrong answer with
    // no symptom. Literal-vs-literal only, here: a runtime operand extent
    // (a group count) is guarded by the emitted runtime check in
    // genApplyCombinator's haloExtentGuards instead.
    //
    // A window read is `A(w(o))`: the index arg is an application whose
    // HEAD carries a "__halowin|" tag (the same shape Lowering's
    // window-read arm keys on). The declared extent is recovered from the
    // operand slot: the slot's extent is the interior-SHRUNK one, so
    // original = shrunk + shrink, with the shrink re-derived from the
    // tag's offset set (haloShrinkOfTag). Compound-inner halos ("c:") get
    // NO extent entry -- their extent is a runtime mask cardinality -- but
    // they are still walked, for the offset checks that do not need one.
    let haloExtentClash : TypeError option =
        // Is there any halo slot at all? The extent map below is
        // dense-only, so gating the walk on it would skip a
        // compound-only kernel entirely -- which is what let an
        // out-of-reach compound read through.
        let anyHalo =
            arrayTypes
            |> List.collect (_.IndexTypes)
            |> List.exists (fun ix ->
                match ix.Tag with
                | Some tag -> tag.StartsWith haloWinTagPrefix
                | None -> false)
        let haloDeclared =
            arrayTypes
            |> List.collect (_.IndexTypes)
            |> List.choose (fun ix ->
                match ix.Tag with
                | Some tag when tag.StartsWith (haloWinTagPrefix + "d:") ->
                    // N = the window's forward demand over the whole
                    // shrunk output, M + Shrink.
                    (match tryEvalIntIR ix.Extent, haloAccessOfTag tag with
                     | Some shrunk, Some h -> Some (tag, snd (haloDemand h (0L, shrunk)))
                     | _ -> None)
                | _ -> None)
            // The tag encodes inner NAME + offsets, not the extent, so two
            // anonymous halos with the same offsets but different extents
            // share one tag. Ambiguous -- drop the tag (fail-open) rather
            // than check one window against the other's extent.
            |> List.groupBy fst
            |> List.choose (fun (tag, entries) ->
                match entries |> List.map snd |> List.distinct with
                | [ n ] -> Some (tag, n)
                | _ -> None)
            |> Map.ofList
        if not anyHalo then None
        else
            // A window read's tag and, when the offset is a literal
            // (`w(1)`, `w(-1)`), the offset itself. BOTH inner kinds:
            // dense ("d:") and compound ("c:"). The reach check below
            // applies to each -- over a compound inner the ordinal walks
            // the PRESENT cells, and the interior is shrunk by the same
            // reach, so an out-of-reach offset runs off the rank table
            // exactly as the dense one runs off the pool. The two
            // literal shapes are the ones Lowering recognizes
            // (src/Lowering.fs, the window-read arm).
            let haloReadOfArg (arg: TypedExpr) : (string * int option) option =
                match arg.Kind with
                | TExprApp (f, offArgs) ->
                    (match env.Subst.Resolve f.Type with
                     | IRTIdxTagged (_, IRefNamed t) when t.StartsWith haloWinTagPrefix ->
                         let lit =
                             match offArgs with
                             | [ { Kind = TExprLit (LitInt k) } ] -> Some (int k)
                             | [ { Kind = TNegIntLit k } ] -> Some (int -k)
                             | _ -> None
                         Some (t, lit)
                     | _ -> None)
                | _ -> None
            let checkSite (arr: TypedExpr) (args: TypedExpr list) : TypeError option =
                let targetName =
                    match arr.Kind with
                    | TExprVar (n, _, _) -> n
                    | _ -> "<array>"
                match env.Subst.Resolve arr.Type with
                | ArrayElem at when args.Length <= at.IndexTypes.Length ->
                    args
                    |> List.mapi (fun d a -> (d, a))
                    |> List.tryPick (fun (d, a) ->
                        match haloReadOfArg a with
                        | None -> None
                        | Some (tag, lit) ->
                            // BL4019 first: a literal offset outside the
                            // declared REACH reads past the pool whatever
                            // the extents say. The reach is [min, max]
                            // over the declared offsets and 0 -- the
                            // interior shrink covers that whole span
                            // (haloShrinkOfTag), so an unlisted offset
                            // INSIDE it (`w(1)` under `[-2, 0, 2]`, `w(0)`
                            // under `[-2, -1]`) is safe and stays legal.
                            //
                            // A COMPOUND inner additionally needs its
                            // offset to be a literal at all: the neighbor
                            // is the (center + o)-th PRESENT cell, whose
                            // coordinate comes from the rank table, and
                            // nothing proves a runtime offset in reach.
                            // Lowering used to discover that with a bare
                            // `failwith` (an internal-error shape for a
                            // user mistake); refuse it here instead, in
                            // the same family as the non-static offset
                            // set (loops/075).
                            let outside =
                                match lit, haloAccessOfTag tag with
                                | Some o, Some h when not (haloOffsetInReach h o) ->
                                    Some (HaloOffsetOutsideSet (o, h.Offsets, targetName))
                                | None, Some h when h.IsCompound ->
                                    let set = h.Offsets |> List.map string |> String.concat ", "
                                    Some (HaloFormErr $"halo window read over a masked domain: the offset must be an integer literal (one of [{set}], e.g. w(0)). Over a CompoundIdx inner the window walks the PRESENT cells, so the neighbor's coordinate is looked up in the rank table -- a runtime offset cannot be shown to stay inside the interior the way a dense ordinal can.")
                                | _ -> None
                            match outside with
                            | Some e -> Some e
                            | None ->
                                match Map.tryFind tag haloDeclared with
                                | Some declared ->
                                    (match tryEvalIntIR at.IndexTypes.[d].Extent with
                                     | Some actual when actual <> declared ->
                                         Some (HaloExtentMismatch (declared, d + 1, targetName, actual))
                                     | _ -> None)
                                | None -> None)
                | _ -> None
            let rec walk (e: TypedExpr) : TypeError option =
                let self =
                    match e.Kind with
                    | TExprIndex (arr, args, _) -> checkSite arr args
                    | TExprApp (f, args) when (match env.Subst.Resolve f.Type with ArrayElem _ -> true | _ -> false) ->
                        checkSite f args
                    | _ -> None
                match self with
                | Some _ -> self
                | None -> typedExprChildren e |> List.tryPick walk
            walk lambdaInfo.Body
    do! requireNone haloExtentClash
    let! applied = buildApplyBodyUnits env
                       { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
                         ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
                         ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
                         ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym }
                       { ApplyGroups.IterGroups = iterGroups; ApplyGroups.AntisymStorageGroups = antisymStorageGroups }
                       { ApplyKernelShape.KernelInputRanks = kernelInputRanks
                         ApplyKernelShape.GridArrayTypes = gridArrayTypes; ApplyKernelShape.KernelTDims = kernelTDims
                         ApplyKernelShape.KernelSignParities = kernelSignParities; ApplyKernelShape.States = states
                         ApplyKernelShape.TriLevels = triLevels; ApplyKernelShape.KernelOutputRank = kernelOutputRank
                         ApplyKernelShape.Speedup = speedup }
                       paramIds
    return applied
}

and buildApplyBodyUnits (env: TypeEnv) (applyOps: ApplyOperands) (applyGroups: ApplyGroups) (kernelShape: ApplyKernelShape) (paramIds: Set<IRId>) : TypeResult<TypedExpr> = typed {
    let { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
          ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
          ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
          ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym } = applyOps
    let { ApplyGroups.IterGroups = iterGroups; ApplyGroups.AntisymStorageGroups = antisymStorageGroups } = applyGroups
    let { ApplyKernelShape.KernelInputRanks = kernelInputRanks
          ApplyKernelShape.GridArrayTypes = gridArrayTypes; ApplyKernelShape.KernelTDims = kernelTDims
          ApplyKernelShape.KernelSignParities = kernelSignParities; ApplyKernelShape.States = states
          ApplyKernelShape.TriLevels = triLevels; ApplyKernelShape.KernelOutputRank = kernelOutputRank
          ApplyKernelShape.Speedup = speedup } = kernelShape
    // After param-type unification, inference variables that flowed into
    // the body's TExprIndex sites may now resolve to nominally-tagged
    // types (e.g., `r` in `lambda(r) -> by_country(r)` is unified with
    // the iterated array's elem type `Nat<RegionIdx>`). Re-run the tag
    // check across the body so cross-tag indexing through kernel
    // parameters surfaces as a real type error rather than silently
    // typechecking. See revalidateBodyTagChecks for rationale.
    // (F2: and a field read / tuple projection of a parameter the body
    // typed while it was open takes the field's type now, before the
    // output cells are read off the kernel's return.)
    settleOpenProjections env lambdaInfo.Body
    do! revalidateBodyTagChecks env lambdaInfo.Body
    // Unit-only second pass over the kernel body, now that the params
    // are bound to the input element types (see kernelBodyUnits). The
    // computed signature replaces whatever annotation the return type
    // resolution leaked, and op mismatches inside the body reject here.
    // NESTED-APPLY DEFERRAL, the array sibling of the scalar OpMath
    // deferral. This apply may itself be nested inside an enclosing lambda
    // body that is STILL being inferred (`lambda(w) -> { let e =
    // exp <@> (i * w * ts); ... }`): our params have just unified with the
    // operands' element types, but every unit reachable from here was
    // computed while the ENCLOSING param was an unresolved infer var
    // contributing no units, so a signature judged now can be exactly the
    // dimensioned capture's own even when the product cancels. Defer the
    // REJECTION (never an acceptance) to the enclosing kernel's own second
    // pass, whose TExprApply arm rechecks this map with the outer params
    // bound and the enclosing kernel-local lets modelled in `bound`.
    //
    // Staleness reaches this apply by three routes, and the trigger is the
    // SHARED provisional-units predicate because only it sees all three:
    // an operand that is itself provisional; a LET-BOUND operand, a bare
    // var with no children, so a plain node walk never reaches the
    // unresolved param inside its defining expression; and a CAPTURE read
    // by the kernel body (`let w = 2.0 * f; ts <@> lambda(t) ->
    // sin(w * t)`), where every operand is fully resolved and only `w` is
    // stale. `typedExprHasProvisionalUnits` chases lets, so routes 2 and 3
    // are visible from the operands and the kernel body alike -- this is
    // the same predicate the scalar OpMath deferral uses, deliberately, so
    // the next manifestation of this shape has one place to be fixed.
    //
    // Second condition: defer only when pass 2 can actually RE-MODEL this
    // apply -- a lambda kernel (reynolds peeled) whose arity matches the
    // operand count, which is exactly what `nestedApplyElemUnits` handles.
    // Anything it would answer "no claim" for is judged here and now, so a
    // deferral cannot decay into silent acceptance. (The scalar site needs
    // no such condition: pass 2's TExprUnaryOp arm re-models every scalar
    // OpMath node unconditionally.) The one loss is the seam Theme C
    // already documents: a lambda that is never `<@>`-applied runs no
    // second pass at all.
    let pass2CanRemodel =
        let rec kernelLambda (k: TypedExpr) =
            match k.Kind with
            | TExprLambda li -> Some li
            | TExprReynolds (inner, _) -> kernelLambda inner
            | _ -> None
        match kernelLambda tKernel with
        | Some li -> li.Params.Length = arrays.Length
        | None -> false
    // LAZY, and asked last: the predicate chases every let-bound operand
    // into its defining expression, so evaluating it eagerly at EVERY apply
    // (each elementwise `A op B` is one) walked the whole history of a
    // chain of lifted bindings per binding -- exponential in the chain's
    // depth (examples/physics/29 spent 210 s here). It only matters on the
    // rejection path.
    let operandsProvisional =
        lazy ((arrays |> List.exists (typedExprHasProvisionalUnits env))
              || typedExprHasProvisionalUnits env lambdaInfo.Body)
    let! bodyUnits =
        match kernelBodyUnits env Map.empty lambdaInfo.Body with
        | Error _ when env.InLambdaBody && pass2CanRemodel && operandsProvisional.Value -> Ok None
        | r -> r
    // Infer output element type from kernel return type, falling back to input arrays.
    // Returns IRType (Phase B2). Primitives are wrapped IRTScalar.
    let restampScalar (t: IRType) =
        match IR.stripUnits t with
        | IRTScalar _ as bare ->
            match bodyUnits with
            | Some u -> IRTUnitAnnotated (bare, u)
            | None -> bare
        | _ -> t
    // Post-unification COMPLEX re-stamp: the kernel body was typed while
    // its params were unresolved, so scalar promotion collapsed
    // prematurely to real (`lambda(z) -> z * 2.0` over a complex array
    // stamped Float64 because z hadn't unified with Complex128 yet). Now
    // that params ARE unified, redo the promotion bottom-up. Conservative:
    // re-stamps ONLY when recomputed is complex and stamped is real --
    // and (R18) when an intrinsic over an INTEGER operand widened.
    // Set when such an intrinsic was re-stamped Float64, so adoptBodyElem
    // may adopt the floating return.
    let intMathWidened = ref false
    let restampedBody =
        let elemOfType (ty: IRType) : ElemType option =
            match IR.stripUnits (env.Subst.Resolve ty) with
            | IRTScalar et -> Some et
            | _ -> None
        let isComplexElem = function ETComplex64 | ETComplex128 -> true | _ -> false
        let isRealScalar = function
            | Some (ETFloat32 | ETFloat64 | ETInt32 | ETInt64) -> true
            | _ -> false
        // Upgrade a node's scalar type, preserving a unit-annotation wrapper.
        let withElem (node: TypedExpr) (et: ElemType) : TypedExpr =
            let newTy =
                match node.Type with
                | IRTUnitAnnotated (_, u) -> IRTUnitAnnotated (IRTScalar et, u)
                | _ -> IRTScalar et
            { node with Type = newTy }
        // Upgrade `node` iff its stamp is a real scalar and `computed` is
        // complex; otherwise keep it (byte-identical for real kernels).
        let maybeUpgrade (node: TypedExpr) (computed: ElemType option) : TypedExpr =
            match computed, elemOfType node.Type with
            | Some ce, cur when isComplexElem ce && isRealScalar cur -> withElem node ce
            // R18: the INTEGER alias of a real intrinsic's result. A
            // deferred intrinsic over an unresolved operand is typed AT
            // the operand's own variable (the complex arm of the scalar
            // rule needs that), so once the parameter unifies with an
            // integer element every node over the intrinsic's value --
            // a negation, arithmetic between two of them, a let, an if,
            // the kernel's return -- reads the INTEGER, while the scalar
            // rule (formalism 2.4: an integer operand widens to double)
            // gives Float64. A node stamped integer whose recomputed
            // operands are floating is exactly that alias: adopt the
            // floating element (an integer-valued node over floating
            // operands has no other source).
            | Some ((ETFloat32 | ETFloat64) as fe), Some (ETInt32 | ETInt64) -> withElem node fe
            | _ -> node
        // Let-bound values whose stamp the walk changed: a reference to
        // the binding reads the new element (`{ let e = exp(v); e }`).
        let restampedLets = System.Collections.Generic.Dictionary<IRId, ElemType>()
        let rec walk (t: TypedExpr) : TypedExpr =
            match t.Kind with
            | TExprVar (_, vid, _) when restampedLets.ContainsKey vid ->
                maybeUpgrade t (Some restampedLets.[vid])
            | TExprBinOp (bmode, ((OpAdd | OpSub | OpMul | OpDiv | OpCaret) as bop), l, r) ->
                let l2, r2 = walk l, walk r
                let node = { t with Kind = TExprBinOp (bmode, bop, l2, r2) }
                match elemOfType l2.Type, elemOfType r2.Type with
                | Some le, Some re ->
                    match IR.promoteElemType le re with
                    | Some pe when isComplexElem pe -> maybeUpgrade node (Some pe)
                    // R18: both operands floating after the re-stamp, node
                    // stamped INTEGER -- the integer alias (see
                    // maybeUpgrade). Guarded on the integer stamp so it
                    // never shadows the complex alias arm below (there a
                    // complex-stamped product of two real `abs` values goes
                    // real; intrinsics/024).
                    | Some ((ETFloat32 | ETFloat64) as pe)
                            when (match le, re, elemOfType node.Type with
                                  | (ETFloat32 | ETFloat64), (ETFloat32 | ETFloat64), Some (ETInt32 | ETInt64) -> true
                                  | _ -> false) -> withElem node pe
                    // BOTH operands real after the re-stamp, node stamped
                    // complex: the stamp is a deferred ALIAS, not a
                    // promotion. `abs(w) * abs(w)` over a complex `w`
                    // types each `abs` at w's own variable (the deferred
                    // arm of the scalar `abs` rule), so the product
                    // unified two aliases of w and resolved to w's
                    // Complex128 -- while the abs arm below corrects both
                    // operands to Float64. Arithmetic on two real
                    // operands is real: adopt the promotion.
                    | Some pe when (match elemOfType node.Type with
                                    | Some ne -> isComplexElem ne
                                    | None -> false) -> withElem node pe
                    | _ -> node
                | _ -> node
            | TExprUnaryOp (((OpNeg | OpConj) as uop), e) ->
                let e2 = walk e
                let node = { t with Kind = TExprUnaryOp (uop, e2) }
                match uop, elemOfType e2.Type, elemOfType node.Type with
                // The same alias through a negation: `-abs(w)`.
                | OpNeg, Some ee, Some ne when not (isComplexElem ee) && isComplexElem ne ->
                    withElem node ee
                | _ -> maybeUpgrade node (elemOfType e2.Type)
            | TExprUnaryOp (OpMath name, e) ->
                let e2 = walk e
                let node = { t with Kind = TExprUnaryOp (OpMath name, e2) }
                match name, elemOfType e2.Type with
                // abs of a complex is the real magnitude: correct a
                // deferred stamp (the operand's variable, now resolved
                // complex) to Float64.
                | "abs", Some (ETComplex64 | ETComplex128) ->
                    (match elemOfType node.Type with
                     | Some ETFloat64 -> node
                     | _ -> withElem node ETFloat64)
                | "abs", _ -> node
                // Transcendentals preserve a complex operand's type.
                | _, Some ((ETComplex64 | ETComplex128) as ce) ->
                    maybeUpgrade node (Some ce)
                // Deferred REAL operand (see the intrinsic's IRTInfer
                // arm): the real intrinsics are Float64-valued.
                | _, Some ((ETInt32 | ETInt64 | ETFloat32 | ETFloat64) as oe) ->
                    (match oe with
                     | ETInt32 | ETInt64 -> intMathWidened.Value <- true
                     | _ -> ())
                    (match elemOfType node.Type with
                     | Some ETFloat64 -> node
                     | _ -> withElem node ETFloat64)
                | _ -> node
            | TExprUnaryOp (((OpReal | OpImag) as uop), e) ->
                // Deferred-width correction: real/imag of a Complex64
                // yield Float32 components (the deferred arm stamped the
                // Complex128 answer, Float64).
                let e2 = walk e
                let node = { t with Kind = TExprUnaryOp (uop, e2) }
                (match elemOfType e2.Type, elemOfType node.Type with
                 | Some ETComplex64, Some ETFloat64 -> withElem node ETFloat32
                 | _ -> node)
            | TExprUnaryOp (OpCast cname, e) ->
                // A cast's stamp is always its target: never upgrade the
                // node itself; still walk the operand so ITS deferred
                // stamps are corrected. (Legality of a complex operand
                // under a real target is findBadDeferredCast's judgment,
                // not a stamp.)
                let e2 = walk e
                { t with Kind = TExprUnaryOp (OpCast cname, e2) }
            | TExprIf (c, a, b) ->
                let a2, b2 = walk a, walk b
                let node = { t with Kind = TExprIf (c, a2, b2) }
                match elemOfType a2.Type, elemOfType b2.Type with
                // R18: the integer alias only when BOTH arms are floating
                // (an integer arm beside a floating one is a mix, not an
                // alias, and keeps its stamp).
                | Some ((ETInt32 | ETInt64)), _ | _, Some ((ETInt32 | ETInt64)) ->
                    (match elemOfType a2.Type, elemOfType b2.Type with
                     | Some le, Some re when isComplexElem le || isComplexElem re ->
                         maybeUpgrade node (IR.promoteElemType le re)
                     | _ -> node)
                | Some le, Some re -> maybeUpgrade node (IR.promoteElemType le re)
                | _ -> node
            | TExprLet (name, vid, value, body) ->
                let v2 = walk value
                // R18: a value the walk re-stamped (an intrinsic over an
                // integer operand) is read at its new element below.
                (match elemOfType value.Type, elemOfType v2.Type with
                 | Some (ETInt32 | ETInt64), Some ((ETFloat32 | ETFloat64) as fe) -> restampedLets.[vid] <- fe
                 | _ -> ())
                let b2 = walk body
                let node = { t with Kind = TExprLet (name, vid, v2, b2) }
                maybeUpgrade node (elemOfType b2.Type)
            // A kernel body BLOCK: its plain lets carry their own declared
            // type, so an R18 re-stamp of a let's value re-stamps the
            // binding too (`{ let e = exp(v); e }`); every other statement
            // is walked as it is.
            // Untouched (as before R18) unless an intrinsic over an integer
            // operand widened inside it.
            | TExprBlock (stmts, final) ->
                let widenedBefore = intMathWidened.Value
                intMathWidened.Value <- false
                let stmts2 =
                    stmts |> List.map (fun st ->
                        match st with
                        | TStmtLet tb when tb.SubBindings.IsEmpty ->
                            let v2 = walk tb.Value
                            (match elemOfType tb.Value.Type, elemOfType v2.Type with
                             | Some (ETInt32 | ETInt64), Some ((ETFloat32 | ETFloat64) as fe) ->
                                 restampedLets.[tb.VarId] <- fe
                                 TStmtLet { tb with Value = v2; Type = (withElem { v2 with Type = tb.Type } fe).Type }
                             | _ -> st)
                        | other -> other)
                let final2 = final |> Option.map walk
                let widenedHere = intMathWidened.Value
                intMathWidened.Value <- widenedBefore || widenedHere
                if not widenedHere then t
                else
                    let node = { t with Kind = TExprBlock (stmts2, final2) }
                    (match final2 with
                     | Some f -> maybeUpgrade node (elemOfType f.Type)
                     | None -> node)
            | _ -> t
        walk lambdaInfo.Body
    // The re-stamped body overrides the resolved return type in BOTH
    // directions.
    //   real -> complex: the collapse hit the return type too (the body was
    //     typed while its params were unresolved).
    //   complex -> real: the four complex -> real intrinsics. `abs <@>
    //     complexArray` eta-expands to lambda(__k) -> abs(__k), and the
    //     scalar `abs` arm types a deferred operand's application AT the
    //     operand's own type var -- which is the lambda's return-type var.
    //     Unifying the param with Complex128 therefore made the RESOLVED
    //     return complex, so `abs` came out complex-with-zero-imag and
    //     propagated into every downstream binding. The walk above already
    //     corrected the body node to its real answer; adopt it. Narrow on
    //     purpose: only when the body's TOP node is one of those four
    //     intrinsics applied to a complex operand, or real arithmetic /
    //     negation over one (two aliased `abs` stamps multiplied together
    //     resolved complex the same way: `abs(w) * abs(w)`).
    let adoptBodyElem (r: IRType) =
        let isRealStamp (t: TypedExpr) =
            match IR.stripUnits (env.Subst.Resolve t.Type) with
            | IRTScalar (ETFloat32 | ETFloat64 | ETInt32 | ETInt64) -> true
            | _ -> false
        // One of the four complex -> real intrinsics over a complex
        // operand, or real arithmetic over one (the walk's alias arms:
        // `abs(w) * abs(w)`, `-abs(w)`), whose re-stamp is real.
        let rec complexToReal (t: TypedExpr) =
            match t.Kind with
            | TExprUnaryOp ((OpMath "abs" | OpReal | OpImag | OpArg), operand) ->
                (match IR.stripUnits (env.Subst.Resolve operand.Type) with
                 | IRTScalar (ETComplex64 | ETComplex128) -> true
                 | _ -> false)
            | TExprBinOp (_, (OpAdd | OpSub | OpMul | OpDiv | OpCaret), l, rr) ->
                isRealStamp t && isRealStamp l && isRealStamp rr
                && (complexToReal l || complexToReal rr)
            | TExprUnaryOp (OpNeg, e) -> isRealStamp t && complexToReal e
            | _ -> false
        let bodyIsComplexToReal = complexToReal restampedBody
        match IR.stripUnits r, IR.stripUnits restampedBody.Type with
        | IRTScalar (ETFloat32 | ETFloat64 | ETInt32 | ETInt64), (IRTScalar (ETComplex64 | ETComplex128) as ct) -> ct
        | IRTScalar (ETComplex64 | ETComplex128), (IRTScalar (ETFloat32 | ETFloat64) as rt) when bodyIsComplexToReal -> rt
        // R18: integer -> real. A real intrinsic over an INTEGER operand
        // is Float64 (formalism 2.4; the scalar rule's answer), and the
        // return var resolved integer only through the deferred alias
        // (maybeUpgrade above). `exp(A)` over an Int64 array lifts to
        // this kernel, as do `method_for(A) <@> exp`, `exp <@> A` and
        // `lambda(v) -> exp(v)`: one answer for all of them.
        | IRTScalar (ETInt32 | ETInt64), (IRTScalar (ETFloat32 | ETFloat64) as rt) when intMathWidened.Value -> rt
        | _ -> r
    let outputElemType =
        let resolved = adoptBodyElem (env.Subst.Resolve(lambdaInfo.ReturnType))
        match resolved with
        | IRTScalar _ as t -> restampScalar t                  // stamp walk-computed units
        | ArrayElem arr -> arr.ElemType                         // already IRType
        | IRTUnitAnnotated (IRTScalar _, _) as t -> restampScalar t
        | IRTNamed _ as t -> t                                 // struct/sum rows: Array<Struct> output
        | IRTTuple _ as t -> t                                 // tuple rows: Array<(..,..)> output
        // AN HM-POLYMORPHIC RETURN SURVIVES THE FALLBACK. A kernel that
        // reads a captured generic array (`lambda(k) -> a(k)` with
        // `a: Array<T like Idx<n>>`) types its body as the SIGNATURE var
        // `T`, which is unresolved on purpose -- monomorphization fills it
        // per call site. The fallback below would overwrite it with the
        // ITERATED array's element type, and when the iteration is a
        // `range<I>` those elements are `Nat<I>`: the map's output was
        // typed as the LOOP INDEX rather than what the body reads.
        // Measured on stdlib/plot.blade's decimation ladder, where the
        // helpers carry a `* 1.0` for exactly this reason -- which is also
        // why an integer grid used to decimate to floating-point JSON.
        // Only MARKED vars are kept: an ordinary unconstrained var is
        // monomorphic by construction and still takes the fallback.
        | IRTInfer vid when env.Subst.IsPolymorphicId vid -> resolved
        | _ ->
            // Fall back to common element type of input arrays when the
            // return type is unresolved (IRTInfer) or has no
            // element-position meaning.
            arrayTypes |> List.tryPick (fun at -> Some at.ElemType)
            |> Option.defaultValue (IRTScalar ETFloat64)
            |> restampScalar
    // A kernel CONSUMES an inner dimension when it has an array-typed
    // parameter of rank > 0 (e.g. reduce over a ragged row). A purely
    // elementwise kernel (all scalar params) consumes nothing, so the
    // consumed-dim filter in deduceOutputType must NOT drop ragged/dep
    // inner dims -- they propagate through the elementwise map.
    let kernelConsumesInner = kernelInputRanks |> List.exists (fun r -> r > 0)
    let outputType = deduceOutputType gridArrayTypes identities iterGroups antisymStorageGroups (computeSDimsPerArray gridArrayTypes) kernelTDims outputElemType isReynolds isReynoldsAntisym kernelConsumesInner kernelSignParities env.Builder

    // Compact-class inheritance (stage 3, unary seam): deduceOutputType's
    // rank-0 elementwise arm hands the input group's compact class to
    // the output VERBATIM. Certify the kernel commutes with that class's
    // mirror involution, or refuse (see compactClassInheritError). A
    // rank > 1 compact record never joins a CROSS-argument axis group,
    // so each belongs to exactly one array/parameter; a consuming
    // position (kernelInputRanks > 0) is skipped since its record is
    // re-tagged TDimension. Under reynolds the wrapper OWNS the output
    // class, so no input producer of an antisymmetric output is in scope.
    let compactInheritErr : TypeError option =
        let inheritedClaims =
            if isReynolds || lambdaInfo.Params.Length <> gridArrayTypes.Length then []
            else
                match outputType with
                | ArrayElem outArr ->
                    let claimed =
                        outArr.IndexTypes
                        // SymWreath joins the filter for the reason spelled
                        // out at compactClassInheritError: an inner '-'
                        // level negates on mirror, so an inherited wreath
                        // class needs the same BL4015 certificate.
                        |> List.filter (fun ix ->
                            ix.Rank > 1
                            && (ix.Symmetry = SymAntisymmetric || ix.Symmetry = SymHermitian
                                || ix.Symmetry = SymWreath))
                        |> List.map (_.Symmetry)
                        |> Set.ofList
                    if Set.isEmpty claimed then []
                    else
                        gridArrayTypes
                        |> List.indexed
                        |> List.collect (fun (i, at) ->
                            if (kernelInputRanks |> List.tryItem i |> Option.defaultValue 0) > 0
                            then []
                            else
                                at.IndexTypes
                                |> List.filter (fun ix ->
                                    ix.Kind = SDimension && ix.Rank > 1
                                    && Set.contains ix.Symmetry claimed)
                                // The level list travels with the class: two
                                // wreath records of the same Symmetry can
                                // need DIFFERENT verdicts (all-'+' needs no
                                // certificate, any '-' does), so distinct-ing
                                // on Symmetry alone would collapse them.
                                |> List.map (fun ix -> (i, ix.Symmetry, orbitLevelsOf ix)))
                        |> List.distinct
                | _ -> []
        if List.isEmpty inheritedClaims then None
        else
            let signResolver (calleeId: IRId) =
                match env.FuncSignParities.TryGetValue calleeId with
                | true, ps -> Some ps
                | _ -> None
            let signParities =
                Blade.Deduce.deduceSignParities signResolver lambdaInfo.Params restampedBody
            let conjCommutes =
                Blade.Deduce.deduceConjCommutes lambdaInfo.Params restampedBody
            // CO-ITERATION (zip) asks the JOINT question (G16). Every zip
            // operand reads the SAME cell of the one shared record, so the
            // antisymmetric mirror negates them TOGETHER: the output keeps
            // the strict class iff f(-x, -y, ..) = -f(x, y, ..). The
            // per-parameter summaries answer a different question --
            // `x * y` is odd in each argument alone yet jointly EVEN (the
            // product of two antisymmetric arrays is SYMMETRIC, and keeping
            // the strict class negated every mirrored read, silently);
            // `x + y` is odd in neither alone yet jointly ODD (refused
            // before, wrongly). An outer product keeps the per-parameter
            // rule: there each record belongs to one parameter only.
            let jointAntisym =
                if List.isEmpty sharedIndexTypes then None
                else
                    let positions =
                        inheritedClaims
                        |> List.choose (fun (i, cls, _) -> if cls = SymAntisymmetric then Some i else None)
                        |> List.distinct
                    let parms = positions |> List.choose (fun i -> List.tryItem i lambdaInfo.Params)
                    if parms.IsEmpty || parms.Length <> positions.Length then None
                    else Some (parms, Blade.Deduce.deduceJointSignParity signResolver parms restampedBody)
            let jointErr =
                match jointAntisym with
                | Some (_, Blade.Deduce.SOdd) | None -> None
                | Some (parms, verdict) ->
                    let names = parms |> List.map (_.Name) |> String.concat ", "
                    let proved =
                        match verdict with
                        | Blade.Deduce.SEven ->
                            "provably sign-EVEN when its co-iterated antisymmetric operands flip sign TOGETHER (f(-x, -y) = f(x, y): the result is SYMMETRIC)"
                        | _ -> "of UNKNOWN joint sign parity when its co-iterated antisymmetric operands flip sign together"
                    Some (AntisymMapNotOdd (names, proved))
            let inheritedClaims =
                if jointAntisym.IsSome then
                    inheritedClaims |> List.filter (fun (_, cls, _) -> cls <> SymAntisymmetric)
                else inheritedClaims
            if jointErr.IsSome then jointErr else
            inheritedClaims |> List.tryPick (fun (i, cls, lvls) ->
                let pname =
                    match List.tryItem i lambdaInfo.Params with
                    | Some (p: TypedParam) -> p.Name
                    | None -> $"argument {i}"
                compactClassInheritError cls lvls i pname signParities conjCommutes)
    do! requireNone compactInheritErr
    let! applied = buildApplyResult env
                       { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
                         ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
                         ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
                         ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym }
                       { ApplyKernelShape.KernelInputRanks = kernelInputRanks
                         ApplyKernelShape.GridArrayTypes = gridArrayTypes; ApplyKernelShape.KernelTDims = kernelTDims
                         ApplyKernelShape.KernelSignParities = kernelSignParities; ApplyKernelShape.States = states
                         ApplyKernelShape.TriLevels = triLevels; ApplyKernelShape.KernelOutputRank = kernelOutputRank
                         ApplyKernelShape.Speedup = speedup }
                       restampedBody adoptBodyElem outputType outputElemType paramIds
    return applied
}

and buildApplyResult (env: TypeEnv) (applyOps: ApplyOperands) (kernelShape: ApplyKernelShape) (restampedBody: TypedExpr) (adoptBodyElem: IRType -> IRType) (outputType: IRType) (outputElemType: IRType) (paramIds: Set<IRId>) : TypeResult<TypedExpr> = typed {
    let { ApplyOperands.Arrays = arrays; ApplyOperands.Identities = identities
          ApplyOperands.ArrayTypes = arrayTypes; ApplyOperands.SharedIndexTypes = sharedIndexTypes
          ApplyOperands.LambdaInfo = lambdaInfo; ApplyOperands.Loop = tLoop; ApplyOperands.Kernel = tKernel
          ApplyOperands.IsReynolds = isReynolds; ApplyOperands.IsReynoldsAntisym = isReynoldsAntisym } = applyOps
    let { ApplyKernelShape.KernelInputRanks = kernelInputRanks
          ApplyKernelShape.GridArrayTypes = gridArrayTypes; ApplyKernelShape.KernelTDims = kernelTDims
          ApplyKernelShape.KernelSignParities = kernelSignParities; ApplyKernelShape.States = states
          ApplyKernelShape.TriLevels = triLevels; ApplyKernelShape.KernelOutputRank = kernelOutputRank
          ApplyKernelShape.Speedup = speedup } = kernelShape

    let reynoldsSpeedup =
        if isReynolds then
            let r = identities.Length
            if r > 1 then factorial r else 1L
        else 1L

    // The substitution holds the refined param types but
    // `lambdaInfo.Params[i].Type` still holds the original (often
    // IRTInfer) values -- F# records are immutable, and lifted lambdas
    // can get `(double)` parameters where the substitution bound them
    // to `Array<...>`. Rebuild the lambda info with explicitly-resolved
    // param/return types and the body, so the resolved kernel carries
    // refined types directly, independent of downstream zonking.
    let resolvedLambdaInfo =
        { lambdaInfo with
            Params =
                lambdaInfo.Params |> List.map (fun p ->
                    { p with Type = env.Subst.Resolve p.Type })
            // Record the seam's sign summary so Lowering stamps it onto
            // the lifted IRCallable -- codegen's and the interpreter's
            // deduceWreathTie calls then judge from these same values.
            SignParities = kernelSignParities
            // The complex re-stamp above must flow into the lifted lambda
            // too: with a stale Float64 stamp the lifted C++ function
            // would declare a double return around a std::complex body.
            Body = restampedBody
            ReturnType = adoptBodyElem (env.Subst.Resolve lambdaInfo.ReturnType) }

    // Store the kernel with resolved types in the typed AST. Lowering
    // walks this typed lambda and emits a lifted IRCallable referenced
    // via IRVar(callable.Id) at the kernel slot.
    let resolvedKernel =
        let lambdaExpr = mkTyped (TExprLambda resolvedLambdaInfo) tKernel.Type
        if isReynolds then mkTyped (TExprReynolds (lambdaExpr, isReynoldsAntisym)) tKernel.Type
        else lambdaExpr

    // Co-iterated records = the leading (nRecords - kernelSliceRank)
    // shared records; the kernel consumes the trailing kernelSliceRank
    // records as a per-iteration slice. One trim serves three shapes:
    // scalar kernel (kR=0, FULL product co-iterated, multi-axis);
    // row-mode kernel (loops/085, kR=1, only outer records co-iterate,
    // inner rides as a rank-1 slice); single packed record (SymIdx,
    // kR=0, walked as its Rank flat canonical levels). Operand shape
    // agreement is enforced where records are collected, so
    // kernelInputRanks.[0] is representative.
    let coIterSharedRecords =
        match sharedIndexTypes with
        | [] -> []
        | full ->
            let kR = kernelInputRanks |> List.tryHead |> Option.defaultValue 0
            let nShared = full.Length - kR
            if nShared <= 0 then [] else full |> List.truncate nShared
    let isCoIter = not (List.isEmpty coIterSharedRecords)
    // For co-iteration, output type spans the co-iterated records (not the
    // operands' outer product) -- PLUS the kernel's T-dimensions, exactly as
    // `deduceOutputType`'s step 4 appends them on the outer-product path.
    //
    // S3, manifestation M-C. This override replaces deduceOutputType's
    // result wholesale, so before S3 it silently dropped the kernel's array
    // return: `method_for(zip(A, B)) <@> lambda(ra, rb) -> ra * rb` came out
    // rank 1 (just the co-iterated Y axis) even though every cell holds a
    // whole X row, and lswosa's `family_spectra` grid came out rank 1
    // Float64 instead of rank 2 Complex128 -- which is why `transpose(grid,
    // [0,1])` reported "axis 1 out of range" and `grid <@> mag2` reported a
    // Complex128/Float64 mismatch one line later. Both are the same missing
    // append. Fresh ids and Kind = TDimension mirror deduceOutputType so the
    // two paths produce structurally identical records.
    let outputType =
        if isCoIter then
            let outputTDims =
                kernelTDims
                |> List.map (fun idx -> { idx with Kind = TDimension; Id = env.Builder.FreshId() })
            mkArrayArrow (coIterSharedRecords @ outputTDims) outputElemType None
        else outputType
    // G67: a kernel returning a COMPACT array makes every output cell a
    // packed pool (a per-group SymIdx H: `method_for(range<G>) <@>
    // lambda(g) -> group_h(jt(g))` with group_h a `comm` self-contraction).
    // No lane stores that shape -- the C++ extents table is per group
    // (bad_array_new_length at run time), the LLVM lane refuses it, the
    // interpreter's row write panics -- so it is refused HERE, once, before
    // any lane (gap register G67; the grouped compact output is NOT BUILT).
    //
    // BOUNDARY (W4B-C, formalism 7.2 "Zero S-dimensions" / 8.4 step 5):
    // the refusal needs an OUTER ITERATION DIMENSION. A ZERO-LEVEL
    // application -- an `object_for` loop whose kernel consumes every
    // operand WHOLE (no S-dimension on any operand, each operand's rank
    // its parameter's), no co-iteration, no Reynolds -- has no outer cell
    // to hold a pool: `object_for(group_h) <@> jg` IS the call
    // `group_h(jg)`, its value the kernel's packed array. It is lowered to
    // that call HERE, before the refusal and before any lane, so every
    // lane runs the direct call (symmetry/174; the direct-call control is
    // symmetry/144). The predicate is Lowering.isZeroLevelWholeArrayApply's
    // (the scalar zero-level apply, G64) for a compact result; only a
    // compact-returning kernel reaches it, every other result keeps the
    // apply node exactly as before.
    let zeroLevelCompactCall =
        (match tLoop.Kind with TExprObjectFor _ -> true | _ -> false)
        && not (List.isEmpty arrays)
        && not isReynolds
        && not isCoIter
        && (match resolvedKernel.Kind with TExprLambda _ -> true | _ -> false)
        && gridArrayTypes.Length = arrays.Length
        && kernelInputRanks.Length = arrays.Length
        && computeSDimsPerArray gridArrayTypes |> List.forall ((=) 0)
        && List.forall2 (fun (at: IRArrayTypeG<IRExpr>) (k: int) -> k > 0 && (at.IndexTypes |> List.sumBy (fun ix -> ix.Rank)) = k)
               gridArrayTypes kernelInputRanks
    match kernelTDims |> List.tryFind (fun ix -> ix.Symmetry <> SymNone) with
    | Some _ when zeroLevelCompactCall ->
        // A NAMED kernel (`object_for(group_h)`) is called BY NAME -- the
        // direct call itself, so a failure inside it carries exactly the
        // direct call's frames (`at group_h`), not an extra one for the
        // apply's eta-wrapper; an inline lambda kernel is called as the
        // lambda it is.
        // The named kernel reaches here eta-expanded (`lambda(jg) ->
        // group_h(jg)`, etaExpandFunctionKernel): that COMPILER-SYNTHESIZED
        // wrapper -- recognised by its construction, every parameter named
        // `__k<uid>_<position>` with one shared uid, no captures, a body
        // that is exactly the call of the named callee with the
        // parameters in order, the callee's arity the wrapper's -- is
        // reduced back to that callee (its comm / anticomm / omp clauses
        // are the callee's own, surfaced, so nothing of the wrapper's is
        // lost). A USER-written wrapper (`object_for(lambda(jg) ->
        // gfail(jg))`, with or without its own `where` clause, permuted or
        // dropped arguments) is NOT reduced: it is called as the lambda it
        // is, its own frame kept.
        let synthesizedWrapper =
            let ps = resolvedLambdaInfo.Params
            let ms = ps |> List.map (fun p -> System.Text.RegularExpressions.Regex.Match(p.Name, @"^__k(\d+)_(\d+)$"))
            not ps.IsEmpty
            && List.isEmpty resolvedLambdaInfo.Captures
            && ms |> List.forall (fun m -> m.Success)
            && (ms |> List.map (fun m -> m.Groups.[1].Value) |> List.distinct |> List.length) = 1
            && (ms |> List.mapi (fun i m -> m.Groups.[2].Value = string i) |> List.forall id)
        let callee =
            let paramIds = resolvedLambdaInfo.Params |> List.map (fun p -> p.VarId)
            match resolvedLambdaInfo.Body.Kind with
            | TExprApp ({ Kind = TExprVar _ } as f, args)
                when synthesizedWrapper
                     && (match env.Subst.Resolve f.Type with
                         | FuncElem (pts, _) -> pts.Length = paramIds.Length
                         | _ -> false)
                     && args.Length = paramIds.Length
                     && List.forall2 (fun (a: TypedExpr) pid ->
                            match a.Kind with
                            | TExprVar (_, vid, _) -> vid = pid
                            | _ -> false) args paramIds ->
                f
            | _ -> resolvedKernel
        return mkTyped (TExprApp (callee, arrays)) resolvedLambdaInfo.ReturnType
    | Some ix ->
        let cell =
            match ix.Symmetry with
            | SymSymmetric -> "is a packed SymIdx group"
            | SymAntisymmetric -> "is a packed AntisymIdx group"
            | SymHermitian -> "is a packed HermitianIdx group"
            | _ -> "has a compact index group"
        return! Error (RowMapCompactCell cell)
    | None ->
    // G90 (rule group-rowmap v1, docs/plans/plan-group-rowmap-ungroup.md
    // section 3): a map over ONE group_by result whose OUTPUT keeps the
    // group-member axis is a per-group ROW MAP, and its rows must be
    // exactly as long as the group's. That is a TYPE fact, but not one the
    // index records carry: every group_by mints a structurally identical
    // member record (Tag "__group_member", Extent "__groupsz"), unify never
    // compares their ids or extents, and deduceOutputType refreshes every
    // output id -- so an `if` arm, a captured row of the same or another
    // grouping (`g(0)`), or a callee whose abstract return is positional
    // would all LOOK length-preserving. The proof is therefore the
    // kernel's SHAPE, a closed grammar (fail closed):
    //   * an ELEMENT parameter (kernel input rank 0): cell for cell;
    //   * a ROW parameter (rank 1): a spine of SCALAR lets ending in an
    //     elementwise expression whose every array operand is the row
    //     parameter itself (arithmetic / comparisons / unary ops, scalar
    //     partners broadcast) -- `r - reduce(r, (+)) / Float64(extents(r))`.
    // Anything else that keeps the member axis is refused here (before G90
    // every such map died in the C++ emitter with BL7004). A preserved map
    // registers its output OUTER slot as derived from the operand's, so
    // `ungroup(h)` and the group read guards follow it (transitively).
    let groupRowMapErr : TypeError option =
        let isGroupedOperand (at: IRArrayType) =
            match at.IndexTypes with
            | outer :: rest -> outer.IxKind = IxKGroupOuter && rest |> List.exists (fun ix -> ix.IxKind = IxKGroupMember)
            | [] -> false
        let groupShapedOut =
            match IR.stripUnits outputType with
            | ArrayElem oa ->
                (match oa.IndexTypes with
                 | outer :: rest when rest |> List.exists (fun ix -> ix.IxKind = IxKGroupMember) -> Some outer
                 | _ -> None)
            | _ -> None
        match arrayTypes, groupShapedOut with
        | [ at ], Some outOuter when isGroupedOperand at && not isCoIter && resolvedLambdaInfo.Params.Length = 1 ->
            let pid = resolvedLambdaInfo.Params.[0].VarId
            // A SCALAR operand, judged STRUCTURALLY: a literal's type is
            // unified with its partner's (the literal-width rule), so beside
            // the row a literal -- and arithmetic over one -- can carry the
            // row's ARRAY type at this seam. Scalars by construction: a
            // literal; a fold, prodsum or extents of a rank-1 operand; a
            // numeric cast, arithmetic or unary op of scalars; a captured name
            // or any other node whose resolved type is a concrete scalar.
            let concreteScalar (t: IRType) =
                match IR.stripUnits (env.Subst.Resolve t) with
                | IRTScalar _ | IRTIdxTagged _ | IRTNat _ -> true
                | _ -> false
            let rankOne (a: TypedExpr) =
                match a.Kind with
                | TExprVar (_, vid, _) when vid = pid -> true
                | _ ->
                    match IR.stripUnits (env.Subst.Resolve a.Type) with
                    | ArrayElem at -> at.IndexTypes.Length = 1
                    | _ -> false
            let rec holdsScalarExpr (e: TypedExpr) : bool =
                match e.Kind with
                | TExprLit _ -> true
                | TExprReduce (a, _, _) | TExprExtents a -> rankOne a
                | TExprProdSum args -> args |> List.forall rankOne
                | TExprBinOp (Elementwise, _, l, r) -> holdsScalarExpr l && holdsScalarExpr r
                | TExprUnaryOp (_, x) -> holdsScalarExpr x
                | TExprApp ({ Kind = TExprVar (n, _, _) }, [ x ])
                    when (match n with
                          | "Float64" | "Float32" | "Int64" | "Int32" -> true
                          | _ -> false) -> holdsScalarExpr x
                | TExprVar (_, vid, _) when vid = pid -> false
                | _ -> concreteScalar e.Type
            let rec rowExpr (e: TypedExpr) : bool =
                match e.Kind with
                | TExprVar (_, vid, _) -> vid = pid
                | TExprBinOp (Elementwise, _, l, r) ->
                    (rowExpr l && (rowExpr r || holdsScalarExpr r)) || (holdsScalarExpr l && rowExpr r)
                | TExprUnaryOp (_, x) -> rowExpr x
                // The checker has already lifted `r - s` / `r * r` into an
                // apply over the row (`object_for(k) <@> r`), and an explicit
                // `method_for(r) <@> lambda(x) -> ..` is the same node: a
                // CELLWISE map (every kernel input rank 0, a scalar kernel)
                // whose every operand is itself row-shaped and whose result is
                // rank 1 (co-iteration, never an outer product) is as long as
                // the row.
                | TExprApply info ->
                    not info.Arrays.IsEmpty
                    && info.Arrays |> List.forall rowExpr
                    && info.KernelInputRanks |> List.forall ((=) 0)
                    && info.KernelOutputRank = 0
                    && (match IR.stripUnits (env.Subst.Resolve e.Type) with
                        | ArrayElem a -> a.IndexTypes.Length = 1
                        | _ -> false)
                // `lambda(r) -> r * 2.0 |> compute`: the pipe binds INTO the
                // lambda, and forcing a row expression is the same row.
                | TExprCompute x -> rowExpr x
                | _ -> false
            let rec spine (e: TypedExpr) : bool =
                match e.Kind with
                | TExprLet (_, _, v, b) -> holdsScalarExpr v && spine b
                | TExprBlock (stmts, Some fe) ->
                    stmts |> List.forall (fun s ->
                        match s with
                        | TStmtLet b -> b.SubBindings.IsEmpty && holdsScalarExpr b.Value
                        | _ -> false)
                    && spine fe
                | TExprCompute x -> spine x
                | _ -> rowExpr e
            let rank = kernelInputRanks |> List.tryHead |> Option.defaultValue (-1)
            // An ELEMENT kernel must return a scalar: one that returns a row
            // (`lambda(e) -> g(0) * 2.0`, the element unused) adds axes.
            let admitted = (rank = 0 && List.isEmpty kernelTDims) || (rank = 1 && spine resolvedLambdaInfo.Body)
            if not admitted then
                Some (IndexSpaceErr "a per-group row map over a group_by result must return a row exactly as long as its group (G90, group-rowmap v1): its kernel takes one ELEMENT, or takes the ROW and returns an elementwise expression of that row and scalars, after scalar lets (`lambda(r) -> r - reduce(r, (+)) / Float64(extents(r))`). This kernel's result keeps the group-member axis without that proof (an `if` choosing a row, a row of a captured grouped array, a call or a nested map returning a row); reduce the row to a scalar, or map the values before grouping")
            else
                let srcOuter = at.IndexTypes.Head.Id
                env.GroupRowMapOuters.[outOuter.Id] <- srcOuter
                (match env.SegmentedOuters.TryGetValue srcOuter with
                 | true, a -> env.SegmentedOuters.[outOuter.Id] <- a
                 | _ -> ())
                (match env.GroupOuters.TryGetValue srcOuter with
                 | true, gk -> env.GroupOuters.[outOuter.Id] <- gk
                 | _ -> ())
                None
        | _ -> None
    do! requireNone groupRowMapErr
    let info : TypedApplyInfo = {
        Loop = tLoop; Kernel = resolvedKernel
        Arrays = arrays; Identities = identities
        ArrayTypes = gridArrayTypes; SharedIndexTypes = coIterSharedRecords
        SymcomStates = states; TriangularLevels = triLevels
        // Grid S-dim count from the fiber-retagged array types (consumed
        // fiber dims are now TDimension, excluded from the count). Matches
        // SymcomStates/TriangularLevels and the grid-depth loop nest codegen
        // builds from ArrayTypes. Scalar kernels: irank=0, unchanged.
        SDimsPerArray = computeSDimsPerArray gridArrayTypes
        KernelInputRanks = kernelInputRanks; KernelOutputRank = kernelOutputRank
        KernelTDims = kernelTDims
        SpeedupFactor = speedup; ReynoldsSpeedup = reynoldsSpeedup
        HasReynolds = isReynolds; OutputType = outputType
        IsCoIteration = isCoIter  // derived: non-empty co-iterated records
        IsComposeApply = false
    }
    return mkTyped (TExprApply info) outputType
}
