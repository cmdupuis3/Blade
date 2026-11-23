// The type checker's ARRAY-OPERATION rules, outside the inference chain (cleanup round 2, K5): prodsum, the
// distribution packs and cumulant projections, decompact, gram / gram_apply, matmul, the linear-algebra
// intrinsics (eigh, solve, lu, lu_solve), stack / join / transpose, the grouping family (group_by, group_keys,
// ungroup, group_bucket) and replicate. They type their operands through the chain's inferExpr, reached through
// InferEntries (TypeCheckInferEntries.fs): each rule takes the record first and binds the entry point it calls.
module Blade.TypeCheckInferArrayOps

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

let rec inferProdSum ({ InferEntries.InferExpr = inferExpr } as ie) (env: TypeEnv) (args: Expr list) : TypeResult<TypedExpr> =
    args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
        // R13: an operand in storage prodsum has no loop for is refused first,
        // whatever its element type (prodsumUnbuiltStorage), naming the fold
        // that IS built over that storage.
        match tArgs |> List.tryPick (fun t -> prodsumUnbuiltStorage env t.Type |> Option.map (fun s -> t, s)) with
        | Some (t, storage) ->
            if t.Span.StartLine > 0 then setCurrentExprSpan t.Span
            let wayOut =
                if storage = "sparse" then
                    "Store the products over its keys and fold them: `let p = method_for(range<SparseIdx<K>>) <@> "
                    + "lambda(i, j) -> a((i, j)) * b((i, j)) |> compute` (one parameter per key component), then "
                    + "`reduce(p, (+))`."
                else
                    "Fold the products instead: `reduce(a * b, (+))` (three operands: `reduce(a * b * c, (+))`)."
            let what =
                if storage = "sparse" then "a sparse array (`sparse(values, keys)` or a `range<SparseIdx<K>>` map)"
                else "a compound selection (`compound(data, mask)`)"
            Error (IndexTypeErr
                    ($"prodsum() over an operand stored as {what} is not built: prodsum is built for dense rank-1 "
                     + $"operands only, at every element type. {wayOut}"))
        | None ->
        // R3 (D2): prodsum multiplies and adds its operands' cells, so an
        // operand of INTEGER EnumIdx values contributes its KEYS -- each such
        // operand is observed, `Int64(codes)`, and prodsum typed again over
        // the observed operands (multiplying the positions that represent the
        // keys was refused, BL4003).
        let keyed = tArgs |> List.map (fun t -> intEnumArrayType env t.Type)
        // (Only a DENSE operand: compound / sparse storage is refused above
        // for every element type; a ragged or grouped row of R values is
        // refused here -- its key observation is not built for prodsum.)
        match List.zip tArgs keyed |> List.tryFind (fun (t, k) -> k && not (intEnumDenseArrayType env t.Type)) with
        | Some (t, _) ->
            if t.Span.StartLine > 0 then setCurrentExprSpan t.Span
            Error (EnumKeyObserved
                    ("prodsum over an array of EnumIdx values multiplies their KEYS, but this operand is stored as a "
                     + "ragged or grouped array, and prodsum observes the keys of dense rank-1 operands only."))
        | None ->
        if List.contains true keyed && (lookupVar "Int64" env).IsNone then
            inferProdSum ie env (List.map2 (fun a k -> if k then enumKeyCastAst a else a) args keyed)
        else
        // Every operand must be a rank-1 array over free (SymNone) storage;
        // element types unify across operands (the product's type).
        let rec go (elemTy: IRType option) (staticN: int64 option) (rest: TypedExpr list) : Result<IRType, TypeError> =
            match rest with
            | [] ->
                (match elemTy with
                 | Some e -> Ok e
                 | None -> Error (ArgCountErr "prodsum() requires at least one array argument"))
            | t :: more ->
                // A `T^k` parameter is an arity-k inference VAR, not an
                // IRTArray (Subst.LookupOrCreateTypeVar): the caret pins the
                // RANK, and the array shape is only materialized when some
                // demand supplies it. `requireArrayArg` is that demand for
                // every other array intrinsic, and prodsum used to skip it and
                // read the raw resolution instead -- so `prodsum(a, b)` over
                // `a: T^1, b: T^1` answered "requires array arguments" unless
                // something ELSE in the body (an `extents(a)`, a call whose
                // callee's parameter is concrete) happened to pin the var
                // first. That is why `covariance` above typechecks and the
                // same call one line up does not.
                //
                // Only an UNBOUND operand is synthesized, and only where the
                // rank it was pinned to is the rank prodsum folds. A concrete
                // non-array, and a `T^k` with k <> 1, both fall through to the
                // honest refusals below -- synthesizing a rank-1 shape against
                // an arity-2 var would refuse in `unify` instead, which reports
                // the raw IRTArrow rather than prodsum's own rank guidance.
                let materialize =
                    match env.Subst.Resolve t.Type with
                    | IRTInfer vid when env.Subst.GetArityConstraint vid = Some 1
                                        || (env.Subst.GetArityConstraint vid).IsNone ->
                        requireArrayArg env t "prodsum" |> Result.map ignore
                    | _ -> Ok ()
                materialize |> Result.bind (fun () ->
                // THE NUMBERS-ONLY RULE (formalism 2.4): prodsum multiplies and
                // adds its operands' cells, so a Bool (or other non-numeric)
                // array is refused as `*` over it is. A generic element is
                // judged per instance.
                let nonNumericCells =
                    match env.Subst.Resolve t.Type with
                    | ArrayElem _ -> nonNumericOperandOf env t.Type
                    | _ -> None
                match nonNumericCells with
                | Some n ->
                    if t.Span.StartLine > 0 then setCurrentExprSpan t.Span
                    Error (TypeMismatchErr ($"arithmetic is defined on numbers only: prodsum() multiplies and adds the cells "
                                            + $"of its operands, and this operand is an array of {n.NWhat.Substring(n.NWhat.IndexOf ' ' + 1)} cells, which are not "
                                            + "numbers -- Blade converts nothing implicitly. Convert the cells explicitly with a "
                                            + "numeric cast (`prodsum(Float64(m), v)`)."))
                | None ->
                (match env.CurrentGenericObligations, env.Subst.Resolve t.Type with
                 | Some acc, ArrayElem arr ->
                     (match IR.stripUnits (env.Subst.Resolve arr.ElemType) with
                      | IRTInfer v when env.Subst.IsPolymorphicId v && (env.Subst.GetLiteralDefault v).IsNone ->
                          acc.Add(GONumericOperand (v, "prodsum", false))
                      | _ -> ())
                 | _ -> ())
                match env.Subst.Resolve t.Type with
                // Fold refusal first, for reduce()'s reason (see there).
                | ArrayElem arrTy when arrTy.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) ->
                    let ix = arrTy.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
                    Error (OrbitFoldUnsupported (ppOrbitLevels (orbitLevelsOf ix), "prodsum"))
                | ArrayElem arrTy when arrTy.IndexTypes.Length = 1
                                       && (match arrTy.IndexTypes.[0].Symmetry with
                                           | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                                           | SymWreath -> true   // unreachable: taken above
                                           | SymNone -> false) ->
                    // Same ambiguity as reduce over compact storage: canonical
                    // vs logical cells differ. Mirror its guidance.
                    Error (CompactStorageErr "prodsum() over compact symmetric/antisymmetric/Hermitian storage is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. decompact(A, d) first for the logical fold.")
                | ArrayElem arrTy when arrTy.IndexTypes.Length = 1 ->
                    // Provably mismatched static extents are an error now;
                    // unknown extents are trusted (codegen loops over the
                    // first operand's extent).
                    let thisN = tryEvalIntIR arrTy.IndexTypes.[0].Extent
                    (match staticN, thisN with
                     | Some a, Some b when a <> b ->
                         Error (ProdsumExtentMismatch (a, b))
                     | _ ->
                        // ELEMENT TYPES PROMOTE, THEY DO NOT UNIFY (issue #18,
                        // docs/plan-array-expression-fixes.md row 18).
                        //
                        // `prodsum` is sum_i a_i * b_i, and `*` between a real
                        // and a complex operand promotes -- the operands keep
                        // their own element types and only the RESULT widens.
                        // Unifying them instead made the two operands' element
                        // types the same type, which is a claim prodsum never
                        // makes. In `lsdft`, `prodsum(s, e)` with `s : U^1` and
                        // `e` complex bound `U := Complex128` from inside the
                        // function's own body: `blade check` still said OK for a
                        // real-valued caller, but the emitted signature read
                        // `lsdft(tuple<Array<complex<double>>, ...>)` while the
                        // call site passed `Array<double>` -- a check-time
                        // soundness gap that only g++ caught.
                        //
                        // So: two concrete element types JOIN through the same
                        // `promoteElemType` the scalar and broadcast seams use
                        // (Float64 |_| Complex128 = Complex128), and a still-
                        // generic operand element contributes nothing and is
                        // LEFT FREE for the call site to fix. Anything
                        // promotion cannot join (Float64 with String) falls
                        // through to `unify`, which reports it exactly as
                        // before -- the refusals are unchanged, only the
                        // accepted mixed-numeric case moves.
                        let joinElem (acc: IRType) (next: IRType) : Result<IRType, TypeError> =
                            match IR.stripUnits (env.Subst.Resolve acc),
                                  IR.stripUnits (env.Subst.Resolve next) with
                            | IRTScalar a, IRTScalar b when a = b -> Ok acc
                            | IRTScalar a, IRTScalar b ->
                                (match IR.promoteElemType a b with
                                 | Some p -> Ok (IRTScalar p)
                                 | None -> unify env.Subst acc next |> Result.map (fun () -> acc))
                            // One side still generic: the concrete side is the
                            // best answer available, and binding the var to it
                            // is the #18 miscompile.
                            | IRTScalar _, IRTInfer _ -> Ok acc
                            | IRTInfer _, IRTScalar _ -> Ok next
                            | _ -> unify env.Subst acc next |> Result.map (fun () -> acc)
                        // UNITS MULTIPLY: prodsum is a sum of PRODUCTS, so the
                        // result's element unit is the product of the operands'
                        // (as for `a * b`), not the first operand's -- the
                        // promotion join above keeps `acc` whole, unit included,
                        // which typed prodsum(meters, seconds) as meters.
                        let joinElemUnits (acc: IRType) (next: IRType) : Result<IRType, TypeError> =
                            joinElem acc next |> Result.bind (fun joined ->
                                unitRulesForOpWith OpMul (IR.getUnits (env.Subst.Resolve acc))
                                                         (IR.getUnits (env.Subst.Resolve next)) None
                                |> Result.map (fun u ->
                                    let bare = IR.stripUnits (env.Subst.Resolve joined)
                                    match u with
                                    | Some u -> IRTUnitAnnotated (bare, u)
                                    | None -> bare))
                        (match elemTy with
                         | Some e -> joinElemUnits e arrTy.ElemType
                         | None -> Ok arrTy.ElemType)
                        |> Result.bind (fun joined ->
                            go (Some joined)
                               (match staticN with Some _ -> staticN | None -> thisN)
                               more))
                | ArrayElem _ ->
                    Error (ReductionFormErr "prodsum() supports only rank-1 arrays (fibers); pass each operand's innermost slice")
                // A `T^k` operand with k <> 1: the caret already declared the
                // rank, so this is the same refusal as a concrete rank-k array
                // and reads better than "requires array arguments" would.
                | IRTInfer vid when (env.Subst.GetArityConstraint vid |> Option.exists (fun k -> k <> 1)) ->
                    Error (ReductionFormErr "prodsum() supports only rank-1 arrays (fibers); pass each operand's innermost slice")
                | _ ->
                    Error (ReductionFormErr "prodsum() requires array arguments"))
        go None None tArgs |> Result.map (fun elemTy ->
            // WIDEST OPERAND LEADS. `IRProdSum` carries no result type of its
            // own: codegen sizes the accumulator from the FIRST operand
            // (`inferExprType (List.head args)`, CodeGen.fs's IRProdSum arm),
            // which was exactly right while every operand shared one element
            // type and is wrong the moment they promote -- `prodsum(s, e)` with
            // real `s` and complex `e` accumulated into a `double` and g++
            // refused `__ps += s[t] * e[t]`.
            //
            // Rotating the operand that already carries the joined type to the
            // front costs nothing and asserts nothing new: prodsum is
            // sum_t prod_L a_L[t], the product is commutative, and the
            // summation order over t -- the only order floating-point
            // accumulation is sensitive to -- is untouched. It fires ONLY when
            // the operands' element types actually differ, so no program that
            // compiles today changes shape or rounding.
            //
            // Ragged/grouped operands opt out: codegen also reads the loop
            // BOUND off the head (`.len` for a peeled row, `.extents[0]`
            // otherwise), and swapping a peeled row out of that position would
            // change which rule applies.
            let bareElemOf (t: TypedExpr) =
                match env.Subst.Resolve t.Type with
                | ArrayElem a -> Some (IR.stripUnits (env.Subst.Resolve a.ElemType))
                | _ -> None
            let anyRagged =
                tArgs |> List.exists (fun t ->
                    match env.Subst.Resolve t.Type with
                    | ArrayElem a ->
                        a.IndexTypes |> List.exists (fun ix ->
                            isRaggedRowKind ix.IxKind || isRaggedFamilyKind ix.IxKind)
                    | _ -> false)
            let joined = IR.stripUnits (env.Subst.Resolve elemTy)
            let ordered =
                if anyRagged || tArgs.Length < 2 || bareElemOf (List.head tArgs) = Some joined then tArgs
                else
                    match tArgs |> List.tryFindIndex (fun a -> bareElemOf a = Some joined) with
                    | Some idx when idx > 0 ->
                        tArgs.[idx] :: (tArgs |> List.indexed
                                              |> List.filter (fun (j, _) -> j <> idx)
                                              |> List.map snd)
                    | _ -> tArgs
            mkTyped (TExprProdSum ordered) elemTy))

/// __dist_pack(kappa1, ..., kappar): construct a Dist<r, tau like axes> value from
/// its cumulant component arrays. Compiler-internal -- the PPL elaboration
/// stage emits it after building the fused tower. kappa_1 (the mean tensor over
/// the variable axes as-declared) fixes tau and the axes; the component count
/// fixes r. The typed node is a plain TExprTuple -- the exact representation
/// the type erases to at zonk -- so no new lowering or codegen path exists;
/// only the TYPE is nominal. Unification stays strict (Unify has no
/// tuple<->Dist coercion), so this intrinsic and Dist-typed operators are the
/// only producers of Dist values.
and inferDistPack ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (args: Expr list) : TypeResult<TypedExpr> =
    args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
        match env.Subst.Resolve tArgs.Head.Type with
        | ArrayElem a1 ->
            let order = tArgs.Length
            Ok (mkTyped (TExprTuple tArgs) (IRTDist (order, a1.ElemType, a1.IndexTypes)))
        | t ->
            Error (BuiltinArgErr $"__dist_pack: components must be arrays (kappa_1 lowered to {ppIRType t}) -- this intrinsic is emitted by the PPL elaboration stage, not written by hand"))

/// cumulant(d, k): the order-k component of a Dist value, as an ordinary
/// array. THE ORDER GUARD AS A TYPE ERROR: k must be a static int (the
/// replicate-count contract) in 1..r. Result type comes from
/// distComponentType, so the projection is fully typed at any expression
/// position -- including on Dist-typed function parameters, where the old
/// elaboration-level registry could never reach.
and inferCumulantProj ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (dExpr: Expr) (kExpr: Expr) : TypeResult<TypedExpr> =
    inferExpr env dExpr |> Result.bind (fun tD ->
        match env.Subst.Resolve tD.Type with
        | IRTDist (order, elem, axes) ->
            (match evalStaticIntExpr env kExpr with
             | None ->
                 Error (StaticValueErr "cumulant: the order must be a compile-time integer (a literal, `let static`, or static-function call)")
             | Some k when k < 1 ->
                 Error (CumulantOrderPositive k)
             | Some k when k > order ->
                 Error (CumulantOrderExceeds (k, order))
             | Some k ->
                 let compTy = distComponentType k elem axes
                 let idxLit = mkTyped (TExprLit (LitInt (int64 (k - 1)))) (IRTScalar ETInt64)
                 Ok (mkTyped (TExprTupleIndex (tD, idxLit)) compTy))
        | t ->
            Error (CumulantNeedsDist (ppIRType t)))

and inferDecompact ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) array d : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
        requireArrayArg env tArr "decompact" |> Result.bind (fun arrTy ->
            // WREATH DECOMPACTION, ahead of the dimension resolution below --
            // `d` does not MEAN a dimension for a wreath class. The generic
            // path splits a compact slot into left-remainder/freed Idx/
            // right-remainder groups of the SAME class, which is ill-defined
            // here: a wreath's fission must say which LEVEL is peeled, and
            // the residual is a JUXTAPOSITION of depth-(d-1) classes, not one
            // group of the same shape (`{ ix with Rank = a }` would produce a
            // Rank that no longer matches the level-list product).
            //
            // Only the ENDPOINT is implemented: FULL decompaction,
            // `decompact(W, 0)`, where the second arg is LEVELS TO KEEP
            // (docs/plan-orbidx-decompaction.md section 4.3) -- the dense
            // rank-prod(ri) tensor, every cell the class's own section 2 read. Any
            // other `d` asks for the section 3 peel lattice and is refused by name.
            match arrTy.IndexTypes with
            | [ ix ] when ix.Symmetry = SymWreath ->
                let levels = ppOrbitLevels (orbitLevelsOf ix)
                if d <> 0 then Error (OrbitDecompactPartial (levels, d))
                else
                    let axes = max 1 ix.Rank
                    let baseExtent = orbitBaseExtent ix
                    // The dense record is built from scratch, NOT `{ ix with
                    // ... }`: a wreath record carries IxKOrbit, the "__orbidx"
                    // sentinel Tag and an IROrbitClass extent marker, and every
                    // one of those would be a lie on a plain axis (the IR
                    // validator enforces the kind/Tag agreement, and
                    // orbitLevelsOf would keep answering with the level list).
                    let denseAxis () =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = baseExtent
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    let newIndexTypes = List.init axes (fun _ -> denseAxis ())
                    let resultType = mkArrayArrow newIndexTypes arrTy.ElemType None
                    Ok (mkTyped (TExprDecompact (tArr, d)) resultType)
            | _ when arrTy.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) ->
                let ix = arrTy.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
                Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix),
                                                "decompact() of a wreath group combined with other index slots"))
            | _ ->
            // Resolve the logical dimension d to (slotIndex, slotArity,
            // posInSlot). A compact slot of arity r spans r consecutive
            // dimensions; posInSlot in [0, r) says which component within
            // the group d targets -- that position decides peel-first /
            // peel-last / peel-middle.
            let totalDims = arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            let dimToSlot (dd: int) : Result<int * int * int, TypeError> =
                if dd < 0 || dd >= totalDims then
                    Error (DecompactDimRange (dd, totalDims))
                else
                    let rec walk slotIdx acc remaining =
                        match remaining with
                        | [] -> Error (InternalErr $"decompact: dimension {dd} out of range (internal)")
                        | (ix: IRIndexType) :: rest ->
                            let ar = max 1 ix.Rank
                            if dd < acc + ar then Ok (slotIdx, ar, dd - acc)
                            else walk (slotIdx + 1) (acc + ar) rest
                    walk 0 0 arrTy.IndexTypes
            dimToSlot d |> Result.bind (fun (slot, r, posInSlot) ->
                let ix = arrTy.IndexTypes.[slot]
                if r < 2 || ix.Symmetry = SymNone then
                    Error (DecompactPlainAxis d)
                // Unreachable: the two wreath arms above (sole slot / combined
                // with others) ran first and between them cover every record
                // list containing a SymWreath. Kept spelled out so the fission
                // code below can go on assuming `{ ix with Rank = a }` builds a
                // consistent record -- for a wreath it would not (the Rank
                // would stop matching the product of its level list).
                elif ix.Symmetry = SymWreath then
                    Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix), "decompact()"))
                else
                // Codegen scope: the group being decompacted must be the LAST
                // index slot, preceding slots plain free Idx singletons --
                // the shape a chained "to-the-right-only" peel produces
                // (accumulating free dims on the left while the residual
                // group stays last), enabling composed full densification.
                // Free dims become an outer loop product wrapping the
                // last-slot fission scatter. Other arrangements deferred.
                let leadingSlots = arrTy.IndexTypes |> List.take slot
                let groupIsLast = (slot = arrTy.IndexTypes.Length - 1)
                let leadingAllFreeSingletons =
                    leadingSlots |> List.forall (fun s -> (max 1 s.Rank) = 1 && s.Symmetry = SymNone)
                if not (groupIsLast && leadingAllFreeSingletons) then
                    Error (DecompactLastSlotOnly (arrTy.IndexTypes.Length, slot))
                elif not (List.isEmpty leadingSlots) && ix.Symmetry <> SymSymmetric then
                    // Surrounding-dim wrapping is currently wired only for the
                    // symmetric fission scatter (the gather form). Antisym /
                    // Hermitian fission with leading free dims is not yet
                    // emitted, so reject rather than miscompile.
                    Error (NotYetSupportedErr "decompact: surrounding free dimensions are currently supported only for symmetric groups; antisymmetric/Hermitian groups with preceding free dimensions are not yet wired.")
                elif ix.Symmetry = SymHermitian && r >= 3 then
                    // Rank-2 Hermitian (the only Hermitian arrays a producer
                    // makes today, via gram) dissolves to a dense nxn with the
                    // lower triangle conjugated -- handled below. Rank >= 3
                    // Hermitian has no producer yet and its compact-residual
                    // conjugate semantics aren't worked out, so reject.
                    Error (NotYetSupportedErr "decompact: rank >= 3 SymHermitian is not yet supported (no producer exists for rank >= 3 Hermitian arrays; gram produces rank-2 Hermitian, which decompacts to a dense conjugate-mirrored matrix).")
                else
                    // SYMMETRIC: general fission, any rank/cut, via the
                    // gather materializer. ANTISYMMETRIC: rank 2 dissolves to
                    // dense nxn; rank >= 3 boundary cuts leave one residual
                    // antisym group, allocatable via allocate_strict with the
                    // sign applied lazily on read (canon_* transform).
                    // Replacement slots: left remainder (arity posInSlot) +
                    // extracted Idx + right remainder (arity r-1-posInSlot);
                    // each remainder of arity a>=2 becomes a fresh SymIdx<a>
                    // of the SAME class (fresh Id: the two halves are now
                    // independent relations); a=1 -> plain Idx, a=0 -> omitted.
                    let mkRemainder (a: int) : IRIndexType list =
                        if a <= 0 then []
                        elif a = 1 then
                            [ { ix with Id = env.Builder.FreshId(); Rank = 1; Symmetry = SymNone } ]
                        else
                            [ { ix with Id = env.Builder.FreshId(); Rank = a } ]
                    let extracted =
                        { ix with Id = env.Builder.FreshId(); Rank = 1; Symmetry = SymNone }
                    let leftRem = mkRemainder posInSlot
                    let rightRem = mkRemainder (r - 1 - posInSlot)
                    let replacement = leftRem @ [extracted] @ rightRem
                    let newIndexTypes =
                        arrTy.IndexTypes
                        |> List.mapi (fun i s -> (i, s))
                        |> List.collect (fun (i, s) -> if i = slot then replacement else [s])
                    let resultType = mkArrayArrow newIndexTypes arrTy.ElemType None
                    Ok (mkTyped (TExprDecompact (tArr, d)) resultType))))

and inferGram ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) leftE rightE : TypeResult<TypedExpr> =
    // gram(A, B) = A * B^H: result[i][j] = sum_k A[i][k] * conj(B[j][k]).
    // A: m x n, B: p x n (shared contracted dim n) -> result: m x p. Element
    // type complex iff EITHER operand is (conj is identity on reals). When A
    // and B are the SAME array (syntactically -- conservative, never claims
    // false symmetry), result is square and SymHermitian (complex) /
    // SymSymmetric (real) via the triangular upper-half scatter; otherwise a
    // general dense m x p array (SymNone).
    inferExpr env leftE |> Result.bind (fun tL ->
    inferExpr env rightE |> Result.bind (fun tR ->
        // minRank 2: gram's contract IS rank-2-on-both-sides (the check just
        // below), so an unannotated operand should deduce rank 2 rather than
        // be pinned to rank 1 and then rejected by that check.
        requireArrayArgMinRank env tL "gram" 2 |> Result.bind (fun lTy ->
        requireArrayArgMinRank env tR "gram" 2 |> Result.bind (fun rTy ->
            let lDims = lTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            let rDims = rTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            if lDims <> 2 || rDims <> 2 then
                Error (GramNeedsRank2 (lDims, rDims))
            // With the dims SUM pinned at 2, an operand is either two plain
            // slots or ONE compact rank-2 group (SymIdx / Hermitian, e.g. a
            // gram result). The extent reads below need a second slot, so the
            // compact spelling must be refused here, not crash on `.[1]`
            // (inferMatmul's two-PLAIN-axes rule, stated for gram).
            elif lTy.IndexTypes.Length <> 2 || rTy.IndexTypes.Length <> 2 then
                let side =
                    match lTy.IndexTypes.Length <> 2, rTy.IndexTypes.Length <> 2 with
                    | true, true -> "both operands carry"
                    | true, false -> "the left operand carries"
                    | _ -> "the right operand carries"
                Error (GramCompactOperand side)
            else
                // Extents: outer (m / p) and inner contracted (n) per operand.
                let lOuter = lTy.IndexTypes.[0].Extent
                let lInner = lTy.IndexTypes.[1].Extent
                let rOuter = rTy.IndexTypes.[0].Extent
                let rInner = rTy.IndexTypes.[1].Extent
                // Static contracted-dim mismatch is a hard error; otherwise trust.
                let innerMismatch =
                    match tryEvalIntIR lInner, tryEvalIntIR rInner with
                    | Some a, Some b -> a <> b
                    | _ -> false
                if innerMismatch then
                    Error (ExtentErr "gram(A, B): the contracted (trailing) dimensions of A and B must match.")
                else
                // ONE element type, both operands, and the operands must
                // ALREADY agree on it. The old rule picked "the complex one,
                // else the left one", which chose a RESULT width and
                // converted nothing: a Complex64 A against a Float64 B
                // checked clean and then failed in g++ (`complex<float> *
                // double` has no overload), and a Float32 A against a
                // Float64 B accumulated double products into a float
                // accumulator. Blade has explicit numeric casts
                // (`Float64(B)`, which lifts elementwise over an array), and
                // those are the licensed widening pathway: an implicit
                // promotion inside a contraction would change the arithmetic
                // the caller wrote without naming the conversion anywhere.
                // Units are stripped first, since they combine through
                // `unitRulesForOp` below; an open element variable is simply
                // bound, so a `T^2` pair stays generic.
                let isComplexElem (t: IRType) =
                    match stripUnits t with
                    | IRTScalar (ETComplex64 | ETComplex128) -> true
                    | _ -> false
                match unify env.Subst (stripUnits lTy.ElemType) (stripUnits rTy.ElemType) with
                | Error _ ->
                    Error (BuiltinArgErr (sprintf "gram(A, B): the operands must share one element type, got %s and %s. Convert one operand explicitly (e.g. `Float64(B)` / `Complex128(A)`, which lift elementwise) so the contraction's width is named in the source." (ppIRType (env.Subst.Resolve (stripUnits lTy.ElemType))) (ppIRType (env.Subst.Resolve (stripUnits rTy.ElemType)))))
                | Ok () ->
                    // The contraction sum_k A[i][k]*conj(B[j][k]) is
                    // multiplicative, so the result's unit signature follows
                    // `*`'s rule (unitMul when both sides carry one, nominal
                    // dropped one-sided; conj never changes a unit) and is
                    // re-attached to the agreed bare type.
                    let outBare = env.Subst.Resolve (stripUnits lTy.ElemType)
                    let isComplex = isComplexElem outBare
                    unitRulesForOp OpMul (getUnits lTy.ElemType) (getUnits rTy.ElemType) |> Result.bind (fun outUnit ->
                    let outElem =
                        match outUnit with
                        | Some u -> IRTUnitAnnotated (outBare, u)
                        | None -> outBare
                    // Conservative same-array test: both bare vars of ONE
                    // identity (loopOperandIdentity: the spelled name, a
                    // deferred alias class or a never-written stored alias
                    // (plan 2.1), a sharing instance's parameter class (plan
                    // 2.2); FRESH under a dense ascription (plan 2.3), which
                    // is why the same spelling alone does not count), exactly
                    // as the loop formers' identity groups see them.
                    let sameArray =
                        match tL.Kind, tR.Kind with
                        | TExprVar (n1, v1, _), TExprVar (n2, v2, _) ->
                            sameIdentity (loopOperandIdentity env n1 v1) (loopOperandIdentity env n2 v2)
                        | _ -> false
                    let freshSlot (ext: IRExpr) (sym: SymmetryClass) (rank: int) =
                        { Id = env.Builder.FreshId(); Rank = rank; Extent = ext
                          Symmetry = sym; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
                    let resultType =
                        if sameArray then
                            // Square m x m, compact group of arity 2 carrying the
                            // (anti-)symmetry: Hermitian (complex) or symmetric.
                            let sym = if isComplex then SymHermitian else SymSymmetric
                            let grp = { (freshSlot lOuter sym 2) with Extent = lOuter }
                            mkArrayArrow [grp] outElem None
                        else
                            // General dense m x p (two independent plain axes).
                            let s0 = freshSlot lOuter SymNone 1
                            let s1 = freshSlot rOuter SymNone 1
                            mkArrayArrow [s0; s1] outElem None
                    Ok (mkTyped (TExprGram (tL, tR, sameArray)) resultType))))))

and inferGramApply ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) leftE rightE vecE : TypeResult<TypedExpr> =
    // gram_apply(A, B, x) = A * (B^H * x): the ACTION of gram(A, B) on x
    // without forming the m x p matrix (docs/plans/structural/05, 3.2).
    // A: m x n, B: p x n, x: p -> y: m. The factors are held to gram's own
    // rules (rank 2, two PLAIN slots each, one element type, static
    // contracted extents agree); x is a rank-1 plain array over B's leading
    // extent; units multiply through both contractions. The result carries
    // no symmetry claim (it is a vector), so there is no same-array mode.
    inferExpr env leftE |> Result.bind (fun tL ->
    inferExpr env rightE |> Result.bind (fun tR ->
    inferExpr env vecE |> Result.bind (fun tX ->
        requireArrayArgMinRank env tL "gram_apply" 2 |> Result.bind (fun lTy ->
        requireArrayArgMinRank env tR "gram_apply" 2 |> Result.bind (fun rTy ->
        requireArrayArgMinRank env tX "gram_apply" 1 |> Result.bind (fun xTy ->
            let dimsOf (t: IRArrayType) = t.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            if dimsOf lTy <> 2 || dimsOf rTy <> 2 then
                Error (BuiltinArgErr $"gram_apply(A, B, x): A and B must be rank-2 (matrix) arrays; got rank-{dimsOf lTy} and rank-{dimsOf rTy}. gram_apply contracts B's trailing axis with x and A's trailing axis with the result: A (m x n), B (p x n), x (p) -> y (m).")
            elif lTy.IndexTypes.Length <> 2 || rTy.IndexTypes.Length <> 2 then
                Error (CompactStorageErr "gram_apply(A, B, x): A and B must carry two PLAIN index axes; a compact symmetric/antisymmetric/Hermitian rank-2 group is not a factor. decompact(A, d) first.")
            elif xTy.IndexTypes.Length <> 1 || xTy.IndexTypes.Head.Rank <> 1 || xTy.IndexTypes.Head.Symmetry <> SymNone then
                Error (BuiltinArgErr "gram_apply(A, B, x): x must be a rank-1 array over one plain axis (B's leading axis).")
            else
                let lOuter = lTy.IndexTypes.[0].Extent
                let lInner = lTy.IndexTypes.[1].Extent
                let rOuter = rTy.IndexTypes.[0].Extent
                let rInner = rTy.IndexTypes.[1].Extent
                let xExt = xTy.IndexTypes.[0].Extent
                let staticDisagree (a: IRExpr) (b: IRExpr) =
                    match tryEvalIntIR a, tryEvalIntIR b with
                    | Some va, Some vb -> va <> vb
                    | _ -> false
                if staticDisagree lInner rInner then
                    Error (ExtentErr "gram_apply(A, B, x): the contracted (trailing) dimensions of A and B must match.")
                elif staticDisagree rOuter xExt then
                    Error (ExtentErr "gram_apply(A, B, x): x must have B's leading extent (B is p x n, so x has p cells).")
                else
                let sameElem (a: IRType) (b: IRType) = unify env.Subst (stripUnits a) (stripUnits b)
                match sameElem lTy.ElemType rTy.ElemType |> Result.bind (fun () -> sameElem lTy.ElemType xTy.ElemType) with
                | Error _ ->
                    Error (BuiltinArgErr (sprintf "gram_apply(A, B, x): the operands must share one element type, got %s, %s and %s. Convert explicitly (e.g. `Float64(B)`, `Complex128(x)`, which lift elementwise) so the contraction's width is named in the source." (ppIRType (env.Subst.Resolve (stripUnits lTy.ElemType))) (ppIRType (env.Subst.Resolve (stripUnits rTy.ElemType))) (ppIRType (env.Subst.Resolve (stripUnits xTy.ElemType)))))
                | Ok () ->
                    let outBare = env.Subst.Resolve (stripUnits lTy.ElemType)
                    unitRulesForOp OpMul (getUnits lTy.ElemType) (getUnits rTy.ElemType) |> Result.bind (fun u1 ->
                    unitRulesForOp OpMul u1 (getUnits xTy.ElemType) |> Result.bind (fun outUnit ->
                    let outElem =
                        match outUnit with
                        | Some u -> IRTUnitAnnotated (outBare, u)
                        | None -> outBare
                    let slot =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = lOuter
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
                    Ok (mkTyped (TExprGramApply (tL, tR, tX)) (mkArrayArrow [slot] outElem None))))))))))

and inferMatmul ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) leftE rightE : TypeResult<TypedExpr> =
    // matmul(A, B): A is m x k, B is k x n -> DENSE m x n.
    //
    // The checks below fix the intrinsic's domain:
    //   * two operands, each rank-2 with two PLAIN axes (a compact/symmetric
    //     rank-2 operand cannot unify with two separate Idx slots);
    //   * both element types Float64 -- blade_gemm is dgemm/native-double only;
    //   * the contracted extents agree, when both are statically known.
    // The math elaborator already rejects a shape mismatch with its own
    // message before inference runs; these are the backstop for a marker
    // that reaches the checker some other way.
    inferExpr env leftE |> Result.bind (fun tL ->
    inferExpr env rightE |> Result.bind (fun tR ->
        requireArrayArgMinRank env tL "matmul" 2 |> Result.bind (fun lTy ->
        requireArrayArgMinRank env tR "matmul" 2 |> Result.bind (fun rTy ->
            let plainRank2 (a: IRArrayType) =
                a.IndexTypes.Length = 2 && a.IndexTypes |> List.forall (fun ix -> ix.Rank <= 1)
            if not (plainRank2 lTy) || not (plainRank2 rTy) then
                Error (BuiltinArgErr "matmul: both arguments must be rank-2 dense matrices (m x k and k x n, two plain index axes each).")
            else
            let isFloat64 (t: IRType) = match t with IRTScalar ETFloat64 -> true | _ -> false
            if not (isFloat64 lTy.ElemType) || not (isFloat64 rTy.ElemType) then
                Error (BuiltinArgErr "matmul: both arguments must have Float64 elements (Array<Float64 like Idx<m>, Idx<k>>).")
            else
                let lInner = lTy.IndexTypes.[1].Extent
                let rOuter = rTy.IndexTypes.[0].Extent
                let innerMismatch =
                    match tryEvalIntIR lInner, tryEvalIntIR rOuter with
                    | Some a, Some b -> a <> b
                    | _ -> false
                if innerMismatch then
                    Error (ExtentErr "matmul(A, B): A's trailing dimension and B's leading dimension must match.")
                else
                    // Dense m x n: two independent plain axes, exactly the
                    // shape the synthesized routine's return type declared.
                    let freshSlot (ext: IRExpr) =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = ext
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    let resultType =
                        mkArrayArrow [ freshSlot lTy.IndexTypes.[0].Extent
                                       freshSlot rTy.IndexTypes.[1].Extent ] lTy.ElemType None
                    Ok (mkTyped (TExprMatmul (tL, tR)) resultType)))))

and inferEigh ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (operandE: Expr) : TypeResult<TypedExpr> =
    // eigh(S) -> (Q, LAM): the symmetric / Hermitian eigendecomposition, as a
    // first-class intrinsic (docs/plan-cpp-perf-exploitation.md).
    //
    // THE ADMISSIBILITY RULE IS `LinAlgPatterns.classifyEigh`, NOT A
    // RESTATEMENT: eigh's domain is the (precision x SYMMETRY) matrix, where
    // symmetry selects the routine FAMILY and one row (complex +
    // SymSymmetric) has no routine at all -- a restatement would be a
    // second place for that matrix to be wrong. Checks below establish
    // SHAPE (rank-2, square); the classifier decides the family, and a
    // `None` is a type error (this node exists only when a route does).
    // The classifier is CONSULTED, never explained: diagnostic text is
    // derived from the operand's own record so each decline names its
    // actual reason instead of one generic "not supported".
    //
    // Since direct application does not unify param types with argument
    // types, an f32/complex/int operand here would typecheck and then die
    // in g++ without this rule -- it accepts strictly more programs, and
    // rejects, with a named reason, exactly the ones that would otherwise
    // hit a C++ template error.
    inferExpr env operandE |> Result.bind (fun tA ->
        requireArrayArgMinRank env tA "eigh" 2 |> Result.bind (fun aTy ->
            // Rank-2 in either admissible spelling: ONE compact slot of arity 2
            // (SymIdx / Hermitian storage -- the zero-conversion packed route),
            // or TWO plain dense axes. Anything else is not a matrix.
            let shapeResult =
                match aTy.IndexTypes with
                | [ ix ] when ix.Rank = 2 ->
                    // A rank-2 compact group is square BY CONSTRUCTION: one
                    // extent covers both dimensions.
                    Ok ix.Extent
                | [ i0; i1 ] when i0.Rank <= 1 && i1.Rank <= 1 ->
                    let squareMismatch =
                        match tryEvalIntIR i0.Extent, tryEvalIntIR i1.Extent with
                        | Some a, Some b -> a <> b
                        | _ -> false
                    if squareMismatch then
                        Error (ExtentErr "eigh: the argument must be SQUARE (n x n); symmetry is assumed, not checked.")
                    else Ok i0.Extent
                | _ ->
                    Error (BuiltinArgErr "eigh: the argument must be rank-2 square -- either two plain axes (Array<Float64 like Idx<n>, Idx<n>>) or one compact arity-2 group (Array<Float64 like SymIdx<2, n>>).")
            shapeResult |> Result.bind (fun nExtent ->
                match Blade.LinAlgPatterns.classifyEigh aTy with
                | None ->
                    // Explain the specific decline. Order matters: the element
                    // type is checked first because it is the coarsest gate,
                    // then the two symmetry rows that have no routine.
                    let isComplexElem =
                        match aTy.ElemType with
                        | IRTScalar (ETComplex64 | ETComplex128) -> true
                        | _ -> false
                    if aTy.IsVirtual then
                        Error (BuiltinArgErr "eigh: the argument must be a materialized array -- a virtual (range / reverse) view has no pool for the eigensolver to read; bind it with |> compute first.")
                    elif (Blade.LinAlgPatterns.precisionOf aTy.ElemType).IsNone then
                        Error (BuiltinArgErr "eigh: the element type has no eigensolver -- expected Float32, Float64, Complex64 or Complex128.")
                    else
                        match aTy.IndexTypes with
                        | [ ix ] when ix.Rank = 2 && ix.Symmetry = SymSymmetric && isComplexElem ->
                            // THE COMPLEX-SYMMETRIC TRAP, refused by name. A
                            // complex array carrying SymSymmetric is
                            // complex-SYMMETRIC (A = A^T, no conjugation): not
                            // Hermitian, not normal, complex spectrum,
                            // non-orthogonal eigenvectors. There is no `zsyev`
                            // and no `zspev`; the right routine is the general
                            // `zgeev`, which is a different operation with a
                            // different result TYPE.
                            Error (BuiltinArgErr "eigh: a COMPLEX SYMMETRIC matrix (A = A^T, without conjugation) is not Hermitian and has no symmetric eigensolver -- its spectrum is complex and its eigenvectors are not orthogonal. Use eig for the general decomposition, or declare the operand Hermitian storage.")
                        | [ ix ] when ix.Rank = 2 && ix.Symmetry = SymAntisymmetric ->
                            Error (BuiltinArgErr "eigh: an ANTISYMMETRIC (skew) operand has a purely imaginary spectrum and no symmetric eigensolver. Use eig on its decompacted form.")
                        | _ ->
                            Error (BuiltinArgErr "eigh: the argument's index structure has no eigensolver route -- expected a plain dense n x n matrix or a rank-2 symmetric/Hermitian compact group.")
                | Some _ ->
                    // THE MIXED-ELEMENT RESULT. Q inherits the operand's element
                    // type; LAM does NOT -- a symmetric/Hermitian matrix has REAL
                    // eigenvalues, so a Complex128 operand yields
                    // `(Array<Complex128 ...>, Array<Float64 ...>)`. That is the
                    // first tuple the surface produces whose elements differ in
                    // element type, and it is exactly what `blade_lapack`'s
                    // signatures say (`std::complex<double>** V` beside
                    // `double* lam`). Typing LAM complex would be a silent
                    // storage-width error at the shim boundary.
                    let lamElem =
                        match aTy.ElemType with
                        | IRTScalar ETComplex128 -> IRTScalar ETFloat64
                        | IRTScalar ETComplex64 -> IRTScalar ETFloat32
                        | t -> t
                    let freshSlot () =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = nExtent
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    // Q is DENSE n x n whatever the operand's storage was: the
                    // eigenvectors of a symmetric matrix carry no symmetry of
                    // their own (Q is orthogonal, not symmetric), so claiming a
                    // compact class for the packed route's output would be
                    // false. The shim writes through `V[i][k]`, a dense row
                    // skeleton, which is the same statement one level down.
                    let qTy = mkArrayArrow [ freshSlot (); freshSlot () ] aTy.ElemType None
                    let lamTy = mkArrayArrow [ freshSlot () ] lamElem None
                    Ok (mkTyped (TExprEigh tA) (IRTTuple [qTy; lamTy])))))

and inferSolve ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (matrixE: Expr) (rhsE: Expr) : TypeResult<TypedExpr> =
    // solve(A, b) -> x with A.x = b: the general dense linear solve by
    // partial-pivoted LU. A is rank-2 square n x n, b is rank-1 of extent n,
    // and x comes back rank-1 of extent n.
    //
    // The domain is deliberately NARROWER than eigh's, and the narrowness is
    // the point: eigh's admissibility is a (precision x SYMMETRY) matrix
    // because symmetry picks the routine FAMILY, so it delegates to
    // `classifyEigh`. LU has no symmetry axis -- `dgesv` is the routine for
    // ANY square matrix -- so the only questions are shape and element type,
    // and both are settled here. `LinAlgPatterns.classifySolve` still answers
    // the ROUTE (and thus the precision letter), but it cannot decline an
    // operand this function accepted.
    //
    // Float64 ONLY, matching `inferMatmul`'s surface rule rather than eigh's
    // four precisions. Widening is a real option (dgesv has s/c/z siblings)
    // but it is a LANGUAGE decision about what the emitted native arm must
    // then also cover byte-identically, so it is not taken silently here.
    //
    // As with matmul, the math elaborator already rejected a shape mismatch
    // with its own message before inference runs; these are the backstop for a
    // marker that reaches the checker some other way (and the only check a
    // hand-written `__math_solve` ever meets).
    inferExpr env matrixE |> Result.bind (fun tA ->
    inferExpr env rhsE |> Result.bind (fun tB ->
        requireArrayArgMinRank env tA "solve" 2 |> Result.bind (fun aTy ->
        requireArrayArgMinRank env tB "solve" 1 |> Result.bind (fun bTy ->
            // A: two PLAIN axes. A rank-2 compact (symmetric / antisymmetric)
            // group is refused rather than densified: LU overwrites its copy
            // with a factor that is NOT symmetric, so the packed pool would be
            // the wrong shape to read and the right answer would need a dense
            // staging copy this node does not make. `decompact` first.
            let plainRank2 (a: IRArrayType) =
                a.IndexTypes.Length = 2 && a.IndexTypes |> List.forall (fun ix -> ix.Rank <= 1)
            let plainRank1 (a: IRArrayType) =
                a.IndexTypes.Length = 1 && a.IndexTypes.Head.Rank <= 1
            if not (plainRank2 aTy) then
                Error (BuiltinArgErr "solve: A must be a rank-2 dense SQUARE matrix (two plain index axes, Array<Float64 like Idx<n>, Idx<n>>).")
            elif not (plainRank1 bTy) then
                Error (BuiltinArgErr "solve: b must be a rank-1 dense vector (Array<Float64 like Idx<n>>).")
            elif aTy.IsVirtual || bTy.IsVirtual then
                Error (BuiltinArgErr "solve: both arguments must be materialized arrays -- a virtual (range / reverse) view has no pool for the factorization to read; bind it with |> compute first.")
            else
            let isFloat64 (t: IRType) = match t with IRTScalar ETFloat64 -> true | _ -> false
            if not (isFloat64 aTy.ElemType) || not (isFloat64 bTy.ElemType) then
                Error (BuiltinArgErr "solve: both arguments must have Float64 elements (Array<Float64 like Idx<n>, Idx<n>> and Array<Float64 like Idx<n>>).")
            else
                // EXTENT AGREEMENT, checked only where BOTH sides are statically
                // known -- the same discipline `inferMatmul`'s contracted-extent
                // check uses, and for the same reason: unify never compares
                // extents, so a static disagreement is the only kind this seam
                // can see. A dynamic mismatch is not silently wrong either; the
                // emitted loops and the shim both take n from A's extents table
                // and read b at those indices.
                let n0 = aTy.IndexTypes.[0].Extent
                let n1 = aTy.IndexTypes.[1].Extent
                let bn = bTy.IndexTypes.Head.Extent
                let disagree (l: IRExpr) (r: IRExpr) =
                    match tryEvalIntIR l, tryEvalIntIR r with
                    | Some a, Some b -> a <> b
                    | _ -> false
                if disagree n0 n1 then
                    Error (ExtentErr "solve: A must be SQUARE (n x n); its two extents disagree.")
                elif disagree n0 bn then
                    Error (ExtentErr "solve(A, b): b's extent must match A's dimension (A is n x n, b must be length n).")
                else
                    // x is a fresh DENSE rank-1 pool of A's leading extent --
                    // taken from A, not from b, because A's is the extent the
                    // factorization iterates and the shim's `n`.
                    let freshSlot (ext: IRExpr) =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = ext
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    let resultType = mkArrayArrow [ freshSlot n0 ] aTy.ElemType None
                    Ok (mkTyped (TExprSolve (tA, tB)) resultType)))))

/// `m.lu(A)` -> (LU, piv): A held to exactly `inferSolve`'s rules for the
/// matrix (rank-2, two plain axes, square where static, Float64,
/// materialized), the value a tuple of two fresh pools: the packed L\U
/// factor over A's shape and the pivot rows as Int64 over A's leading axis.
/// The factorization is the one `solve` computes, kept -- so it can be
/// applied to several right-hand sides and transposed without refactoring
/// (plan-fortran-killer-2 section 6.2; the operator-value form of
/// docs/plans/structural/05 step 2, carried by the NODE and an ordinary
/// tuple, no new type).
and inferLu ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (matrixE: Expr) : TypeResult<TypedExpr> =
    inferExpr env matrixE |> Result.bind (fun tA ->
        requireArrayArgMinRank env tA "lu" 2 |> Result.bind (fun aTy ->
            let plainRank2 (a: IRArrayType) =
                a.IndexTypes.Length = 2 && a.IndexTypes |> List.forall (fun ix -> ix.Rank <= 1)
            if not (plainRank2 aTy) then
                Error (BuiltinArgErr "lu: A must be a rank-2 dense SQUARE matrix (two plain index axes, Array<Float64 like Idx<n>, Idx<n>>).")
            elif aTy.IsVirtual then
                Error (BuiltinArgErr "lu: A must be a materialized array -- a virtual (range / reverse) view has no pool for the factorization to read; bind it with |> compute first.")
            elif (match aTy.ElemType with IRTScalar ETFloat64 -> false | _ -> true) then
                Error (BuiltinArgErr "lu: A must have Float64 elements (Array<Float64 like Idx<n>, Idx<n>>).")
            else
                let n0 = aTy.IndexTypes.[0].Extent
                let n1 = aTy.IndexTypes.[1].Extent
                let disagree (l: IRExpr) (r: IRExpr) =
                    match tryEvalIntIR l, tryEvalIntIR r with
                    | Some a, Some b -> a <> b
                    | _ -> false
                if disagree n0 n1 then
                    Error (ExtentErr "lu: A must be SQUARE (n x n); its two extents disagree.")
                else
                    let freshSlot (ext: IRExpr) =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = ext
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    let luTy = mkArrayArrow [ freshSlot n0; freshSlot n0 ] aTy.ElemType None
                    let pivTy = mkArrayArrow [ freshSlot n0 ] (IRTScalar ETInt64) None
                    Ok (mkTyped (TExprLu tA) (IRTTuple [ luTy; pivTy ]))))

/// `m.lu_solve(LU, piv, b)` / `m.lu_solve_t(LU, piv, b)`: apply a stored
/// factorization. LU rank-2 plain square Float64, piv rank-1 Int64 of the
/// same extent, b rank-1 Float64 of the same extent; the answer is a fresh
/// rank-1 Float64 pool over LU's leading axis (the solve twin's rule).
and inferLuSolve ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (luE: Expr) (pivE: Expr) (rhsE: Expr) (transposed: bool) : TypeResult<TypedExpr> =
    let what = if transposed then "lu_solve_t" else "lu_solve"
    inferExpr env luE |> Result.bind (fun tL ->
    inferExpr env pivE |> Result.bind (fun tP ->
    inferExpr env rhsE |> Result.bind (fun tB ->
        requireArrayArgMinRank env tL what 2 |> Result.bind (fun lTy ->
        requireArrayArgMinRank env tP what 1 |> Result.bind (fun pTy ->
        requireArrayArgMinRank env tB what 1 |> Result.bind (fun bTy ->
            let plainRank2 (a: IRArrayType) =
                a.IndexTypes.Length = 2 && a.IndexTypes |> List.forall (fun ix -> ix.Rank <= 1)
            let plainRank1 (a: IRArrayType) =
                a.IndexTypes.Length = 1 && a.IndexTypes.Head.Rank <= 1
            let isF64 (t: IRType) = match t with IRTScalar ETFloat64 -> true | _ -> false
            let isI64 (t: IRType) = match t with IRTScalar ETInt64 -> true | _ -> false
            if not (plainRank2 lTy) then
                Error (BuiltinArgErr $"{what}: LU must be the rank-2 dense square factor `m.lu(A)` returns (Array<Float64 like Idx<n>, Idx<n>>).")
            elif not (plainRank1 pTy) || not (isI64 pTy.ElemType) then
                Error (BuiltinArgErr $"{what}: piv must be the rank-1 Int64 pivot vector `m.lu(A)` returns (Array<Int64 like Idx<n>>).")
            elif not (plainRank1 bTy) then
                Error (BuiltinArgErr $"{what}: b must be a rank-1 dense vector (Array<Float64 like Idx<n>>).")
            elif lTy.IsVirtual || pTy.IsVirtual || bTy.IsVirtual then
                Error (BuiltinArgErr $"{what}: every argument must be a materialized array; bind a virtual view with |> compute first.")
            elif not (isF64 lTy.ElemType) || not (isF64 bTy.ElemType) then
                Error (BuiltinArgErr $"{what}: LU and b must have Float64 elements.")
            else
                let n0 = lTy.IndexTypes.[0].Extent
                let n1 = lTy.IndexTypes.[1].Extent
                let pn = pTy.IndexTypes.Head.Extent
                let bn = bTy.IndexTypes.Head.Extent
                let disagree (l: IRExpr) (r: IRExpr) =
                    match tryEvalIntIR l, tryEvalIntIR r with
                    | Some a, Some b -> a <> b
                    | _ -> false
                if disagree n0 n1 then
                    Error (ExtentErr $"{what}: LU must be SQUARE (n x n); its two extents disagree.")
                elif disagree n0 pn then
                    Error (ExtentErr $"{what}: piv's extent must match the factor's dimension (LU is n x n, piv must be length n).")
                elif disagree n0 bn then
                    Error (ExtentErr $"{what}(LU, piv, b): b's extent must match the factor's dimension (LU is n x n, b must be length n).")
                else
                    let freshSlot (ext: IRExpr) =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = ext
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    let resultType = mkArrayArrow [ freshSlot n0 ] lTy.ElemType None
                    Ok (mkTyped (TExprLuSolve (tL, tP, tB, transposed)) resultType)))))))

/// A slot that can take part in a stack/join copy: one dense dimension, no
/// symmetry, no ragged/compound/group kind.
and isDenseStackableSlot (ix: IRIndexType) : bool =
    ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain

/// The statically-known extent of a slot, when there is one. Extents that are
/// runtime expressions answer None and are simply not compared (the emitted
/// C++ reads `.extents[d]` either way; this is a compile-time courtesy check,
/// not a soundness requirement).
///
/// "Reads `.extents[d]` either way" holds for the stack/join operands this
/// serves, NOT in general: at a PARAMETER position codegen treats a literal
/// extent as ground truth and bakes it into subscripts, loop bounds and result
/// allocations. There the same comparison is a soundness requirement, which is
/// what BL3016 (`extentClash` / `kernelExtentClash`) enforces -- do not
/// generalize this docstring's permissiveness to that seam.
and staticExtentOf (e: IRExpr) : int64 option =
    match e with
    | IRLit (IRLitInt n) -> Some n
    | _ -> None

/// Shared operand fence: all arrays, all dense slots, all equal rank. Returns
/// the operands' array types in order. `mkNeedsArrays` builds the op-specific
/// error so stack and join each report in their own words.
and stackJoinOperandTypes
        (env: TypeEnv) (opName: string) (tExprs: TypedExpr list)
        (mkNeedsArrays: int -> string -> TypeError) : TypeResult<IRArrayType list> =
    let rec go i acc (rest: TypedExpr list) =
        match rest with
        | [] -> Ok (List.rev acc)
        | te :: tl ->
            match env.Subst.Resolve te.Type with
            | ArrayElem at ->
                match at.IndexTypes |> List.tryFindIndex (isDenseStackableSlot >> not) with
                | Some bad -> Error (StackJoinCompactSlot (opName, bad))
                | None -> go (i + 1) (at :: acc) tl
            | other -> Error (mkNeedsArrays i (ppIRType other))
    go 1 [] tExprs

and inferStack ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (exprs: Expr list) : TypeResult<TypedExpr> =
    exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
        if tExprs.IsEmpty then
            Error (ArgCountErr "stack() needs at least one array: stack(A1, ..., An) adds a fresh leading axis of extent n.")
        else
        stackJoinOperandTypes env "stack" tExprs (fun i g -> StackNeedsArrays (i, g))
        |> Result.bind (fun arrTys ->
            let first = List.head arrTys
            // Every operand must have the SAME shape -- the fresh leading axis
            // selects among them, so a ragged selection has no rank-(r+1) type.
            let shapeCheck =
                arrTys |> List.tail |> List.mapi (fun i at -> (i + 2, at))
                |> List.fold (fun acc (pos, at) ->
                    acc |> Result.bind (fun () ->
                        if at.IndexTypes.Length <> first.IndexTypes.Length then
                            Error (StackShapeMismatch (pos, $"rank {at.IndexTypes.Length} vs rank {first.IndexTypes.Length}"))
                        else
                            let extentClash =
                                List.zip first.IndexTypes at.IndexTypes
                                |> List.tryPick (fun (a, b) ->
                                    match staticExtentOf a.Extent, staticExtentOf b.Extent with
                                    | Some x, Some y when x <> y -> Some (x, y)
                                    | _ -> None)
                            match extentClash with
                            | Some (x, y) -> Error (StackShapeMismatch (pos, $"extent {y} vs extent {x}"))
                            | None ->
                                match unify env.Subst first.ElemType at.ElemType with
                                | Ok () -> Ok ()
                                | Error _ -> Error (StackShapeMismatch (pos, "element types differ")))) (Ok ())
            shapeCheck |> Result.map (fun () ->
                let leadIdx = {
                    Id = env.Builder.FreshId()
                    Rank = 1
                    Extent = IRLit (IRLitInt (int64 tExprs.Length))
                    Symmetry = SymNone
                    Tag = None; IxKind = IxKPlain
                    Kind = SDimension
                    Dependencies = []
                }
                let resultType = mkArrayArrow (leadIdx :: first.IndexTypes) first.ElemType None
                mkTyped (TExprStack tExprs) resultType)))

and inferJoin ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (arrays: Expr list) (dim: int) : TypeResult<TypedExpr> =
    arrays |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
        stackJoinOperandTypes env "join" tExprs (fun i g -> JoinNeedsArrays (i, g))
        |> Result.bind (fun arrTys ->
            let first = List.head arrTys
            let totalDims = first.IndexTypes.Length
            if dim < 0 || dim >= totalDims then
                Error (JoinDimRange (dim, totalDims))
            else
            // Equal rank + equal extents off the joined axis; the joined axis
            // is the only one allowed to differ, and its extents add.
            let shapeCheck =
                arrTys |> List.tail |> List.mapi (fun i at -> (i + 2, at))
                |> List.fold (fun acc (pos, at) ->
                    acc |> Result.bind (fun () ->
                        if at.IndexTypes.Length <> totalDims then
                            Error (JoinShapeMismatch (pos, $"rank {at.IndexTypes.Length} vs rank {totalDims}"))
                        else
                            let offAxisClash =
                                List.zip first.IndexTypes at.IndexTypes
                                |> List.indexed
                                |> List.tryPick (fun (d, (a, b)) ->
                                    if d = dim then None else
                                    match staticExtentOf a.Extent, staticExtentOf b.Extent with
                                    | Some x, Some y when x <> y -> Some (d, x, y)
                                    | _ -> None)
                            match offAxisClash with
                            | Some (d, x, y) -> Error (JoinShapeMismatch (pos, $"dimension {d} has extent {y}, not {x}"))
                            | None ->
                                match unify env.Subst first.ElemType at.ElemType with
                                | Ok () -> Ok ()
                                | Error _ -> Error (JoinShapeMismatch (pos, "element types differ")))) (Ok ())
            shapeCheck |> Result.map (fun () ->
                // Joined extent: the literal sum when every operand's extent is
                // static (the overwhelmingly common case, and what pins/prints
                // depend on), otherwise a runtime addition chain.
                let dimExtents = arrTys |> List.map (fun at -> at.IndexTypes.[dim].Extent)
                let joinedExtent =
                    let statics = dimExtents |> List.map staticExtentOf
                    if statics |> List.forall Option.isSome then
                        IRLit (IRLitInt (statics |> List.sumBy Option.get))
                    else
                        dimExtents |> List.reduce (fun a b -> IRBinOp (IRElementwise, IRAdd, a, b, SrcLoc.Nowhere))
                let joinedIdx =
                    { first.IndexTypes.[dim] with
                        Id = env.Builder.FreshId()
                        Extent = joinedExtent
                        Tag = None }
                let resultIndexTypes =
                    first.IndexTypes |> List.mapi (fun d ix -> if d = dim then joinedIdx else ix)
                let resultType = mkArrayArrow resultIndexTypes first.ElemType None
                mkTyped (TExprJoin (tExprs, dim)) resultType)))

and inferTranspose ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) array d1 d2 : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
        // minRank: transpose needs at least 2 dimensions, and specifically
        // enough of them to contain the requested axes -- the axes are already
        // in scope here, so synthesize exactly the rank they need rather than
        // a flat 2. Strictly more permissive than today (an unannotated arg
        // was pinned to rank 1 and then always failed TransposeAxisRange), so
        // it cannot regress a program that compiles.
        requireArrayArgMinRank env tArr "transpose" (max 2 (max d1 d2 + 1)) |> Result.bind (fun arrTy ->
            // Map a logical DIMENSION index to its INDEX-TYPE slot. A slot
            // of arity k occupies k consecutive dimensions; we walk the
            // slot list accumulating arities until the target dimension
            // falls inside a slot. Returns (slotIndex, slotArity, dimWithinSlot).
            // For the first cut every reachable slot is arity-1, so this is
            // identity -- but writing it properly keeps the gate correct in
            // the presence of compound groups elsewhere in the array.
            let totalDims = arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            let dimToSlot (d: int) : Result<int * int * int, TypeError> =
                if d < 0 || d >= totalDims then
                    Error (TransposeAxisRange (d, totalDims))
                else
                    let rec walk slotIdx acc remaining =
                        match remaining with
                        | [] -> Error (InternalErr $"transpose: axis {d} out of range (internal)")
                        | (ix: IRIndexType) :: rest ->
                            let ar = max 1 ix.Rank
                            if d < acc + ar then Ok (slotIdx, ar, d - acc)
                            else walk (slotIdx + 1) (acc + ar) rest
                    walk 0 0 arrTy.IndexTypes
            if d1 = d2 then
                Error (TransposeAxesEqual (d1, d2))
            else
                dimToSlot d1 |> Result.bind (fun (slot1, ar1, _) ->
                dimToSlot d2 |> Result.bind (fun (slot2, ar2, _) ->
                    let ix1 = arrTy.IndexTypes.[slot1]
                    let ix2 = arrTy.IndexTypes.[slot2]
                    if slot1 = slot2 then
                        // Both dimensions lie INSIDE one index type -- an intra-
                        // group swap. The index-type class decides the behavior
                        // (storage-preserving): symmetric -> identity, antisym ->
                        // whole-array negation, hermitian -> whole-array
                        // conjugation. No decompaction, no dense blow-up.
                        (match (behaviorOf ix1).TransposeWithin () with
                         | TIdentity ->
                            // A(i,j) = A(j,i): storage unchanged. Erase the
                            // transpose; the result IS the source array.
                            Ok tArr
                         | TNegatedCopy ->
                            Ok (mkTyped (TExprArrayNegate tArr) tArr.Type)
                         | TConjugatedCopy ->
                            Ok (mkTyped (TExprArrayConjugate tArr) tArr.Type)
                         | TDataMove ->
                            // A plain (SymNone) slot of arity >= 2 swapped
                            // within itself: a genuine dimensional swap inside a
                            // rectangular compound. Not yet emitted (the data-
                            // move materializer handles cross-slot rank-1 only).
                            Error (TransposeWithinGroup ar1)
                         | TRequiresDecompaction reason ->
                            Error (CompactStorageErr $"transpose: {reason}"))
                    else
                        // Different slots. The structure-preserving case is two
                        // plain (arity-1 SymNone) axes -> physical data move.
                        // Anything else means one axis is bound in a compact
                        // group and the other is outside it: swapping them would
                        // break that group's symmetry. That is a structure-
                        // changing operation requiring explicit decompaction
                        // (decompact then transpose), not a silent transpose.
                        let plain ar (ix: IRIndexType) = ar = 1 && ix.Symmetry = SymNone
                        if plain ar1 ix1 && plain ar2 ix2 then
                            let swapped =
                                arrTy.IndexTypes
                                |> List.mapi (fun i ix ->
                                    if i = slot1 then arrTy.IndexTypes.[slot2]
                                    elif i = slot2 then arrTy.IndexTypes.[slot1]
                                    else ix)
                            let resultType = mkArrayArrow swapped arrTy.ElemType None
                            Ok (mkTyped (TExprTranspose (tArr, d1, d2)) resultType)
                        else
                            let culprit, cd, car, cix =
                                if not (plain ar1 ix1) then "first", d1, ar1, ix1 else "second", d2, ar2, ix2
                            Error (CompactStorageErr (sprintf "transpose: the %s axis (dim %d) is bound in a %s index group (rank %d), and the other axis is outside it. Swapping across a group boundary would decompose the group's symmetry. Decompact the axis first (decompact then transpose)." culprit cd (match cix.Symmetry with SymSymmetric -> "symmetric" | SymAntisymmetric -> "antisymmetric" | SymHermitian -> "Hermitian" | SymWreath -> "orbit" | SymNone -> "plain") car))))))

and inferGroupBy ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) values grouping : TypeResult<TypedExpr> =
    inferExpr env values |> Result.bind (fun tVals ->
    inferExpr env grouping |> Result.bind (fun tGrouping ->
        requireArrayArg env tVals "group_by" |> Result.bind (fun arrTy ->
            // A GRID grouping (`segments(C0, C1)`): the values must be rank 2
            // over exactly those two axes, in slot order; the result is one
            // ragged row per tile (docs/plans/structural/07 §4.1b).
            let gridCase =
                match env.Subst.Resolve(tGrouping.Type) with
                | IRTGroupKeys (outer, _, _) ->
                    (match outer.Tag with
                     | Some t when t.StartsWith "__segmentsgrid|" -> Some (outer, t.Substring "__segmentsgrid|".Length)
                     | _ -> None)
                | _ -> None
            match gridCase with
            | Some (outer, aliasKey) ->
                let aliases = aliasKey.Split(',') |> List.ofArray
                let segs = aliases |> List.choose (fun a -> Map.tryFind a env.Segmentations)
                if arrTy.IndexTypes.Length <> 2 then
                    Error (SegmentedDomainErr $"group_by over segments({aliasKey}): the values must be a rank-2 array over those two axes (got rank {arrTy.IndexTypes.Length})")
                elif segs.Length <> 2 then
                    Error (SegmentedDomainErr $"group_by over segments({aliasKey}): an alias is no longer in scope")
                else
                    let slotMatches (d: int) =
                        let ix = arrTy.IndexTypes.[d]
                        let src = segs.[d].Source
                        ix.Id = src.Id || (ix.Tag.IsSome && ix.Tag = src.Tag) || (match ix.Extent, src.Extent with IRLit (IRLitInt a), IRLit (IRLitInt b) -> a = b && ix.Tag.IsNone | _ -> false)
                    if not (slotMatches 0 && slotMatches 1) then
                        Error (SegmentedDomainErr $"group_by over segments({aliasKey}): the values' slots must be the segmented axes {aliasKey}, in that order")
                    else
                        let member_ = {
                            Id = env.Builder.FreshId(); Rank = 1
                            Extent = IRParam ("__groupsz", 0, IRTNat None)
                            Symmetry = SymNone; Tag = Some "__group_member"; IxKind = IxKGroupMember
                            Kind = SDimension; Dependencies = []
                        }
                        let outer' = { outer with Id = env.Builder.FreshId(); Tag = Some "__group_outer"; IxKind = IxKGroupOuter }
                        env.SegmentedOuters.[outer'.Id] <- aliasKey
                        let resultType = mkArrayArrow [outer'; member_] arrTy.ElemType None
                        Ok (mkTyped (TExprGroupBy (tVals, tGrouping)) resultType)
            | None ->
            // Extract group structure from GroupKeys type, or fall back for raw key arrays
            let (outerIdx, memberIdx) =
                match env.Subst.Resolve(tGrouping.Type) with
                | IRTGroupKeys (outer, _, _) ->
                    let member_ = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__groupsz", 0, IRTNat None)
                        Symmetry = SymNone; Tag = Some "__group_member"; IxKind = IxKGroupMember
                        Kind = SDimension; Dependencies = []
                    }
                    let outer' = { outer with Id = env.Builder.FreshId(); Tag = Some "__group_outer"; IxKind = IxKGroupOuter }
                    // The grouping behind this slot, for the read guards (G1).
                    if tGrouping.Kind.IsTExprVar then env.GroupOuters.[outer'.Id] <- tGrouping
                    // A STRUCTURAL grouping (`segments(A)`, docs/plans/
                    // structural/07): remember which axis this outer slot
                    // partitions, so `ungroup(G)` can restore it.
                    (match outer.Tag with
                     | Some t when t.StartsWith "__segments|" ->
                         env.SegmentedOuters.[outer'.Id] <- t.Substring "__segments|".Length
                     | _ -> ())
                    (outer', member_)
                | _ ->
                    // Fallback: treat second arg as raw key array (backward compat)
                    let outer = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__ngroups", 0, IRTNat None)
                        Symmetry = SymNone; Tag = Some "__group_outer"; IxKind = IxKGroupOuter
                        Kind = SDimension; Dependencies = []
                    }
                    let member_ = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__groupsz", 0, IRTNat None)
                        Symmetry = SymNone; Tag = Some "__group_member"; IxKind = IxKGroupMember
                        Kind = SDimension; Dependencies = []
                    }
                    (outer, member_)
            let resultType = mkArrayArrow [outerIdx; memberIdx] arrTy.ElemType None
            Ok (mkTyped (TExprGroupBy (tVals, tGrouping)) resultType))))

and inferGroupKeys ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) keys : TypeResult<TypedExpr> =
    match keys with
    | [] ->
        Error (ArgCountErr "group_keys requires at least one key array; got empty argument list")
    | [singleKey] ->
        // Existing single-key path, unchanged.
        inferExpr env singleKey |> Result.bind (fun tKeys ->
            requireArrayArg env tKeys "group_keys" |> Result.bind (fun arrTy ->
                let sourceIdx =
                    if not (List.isEmpty arrTy.IndexTypes) then arrTy.IndexTypes.[0]
                    else {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__src", 0, IRTNat None)
                        Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                        Kind = SDimension; Dependencies = []
                    }
                let namedRef =
                    match arrTy.ElemType with
                    | IRTIdxTagged (_, IRefNamed name) -> Some name
                    | _ -> None
                let (outerIdx, enumValues) =
                    match namedRef with
                    | Some name ->
                        match lookupTypeDef name env with
                        | Some (TDIIndexType (_, idx, _)) ->
                            ({ idx with Id = env.Builder.FreshId(); Tag = Some name; IxKind = ixKindOfTag (Some name) }, None)
                        | Some (TDIEnumIdx (_, idx, values, _)) ->
                            // An INTEGER EnumIdx value is represented as its
                            // position (D2), so the key array already holds
                            // the bucket numbers: the reverse lookup maps each
                            // position to itself.
                            let values =
                                if values |> List.forall (function EVInt _ -> true | _ -> false)
                                then values |> List.mapi (fun i _ -> EVInt (int64 i))
                                else values
                            ({ idx with Id = env.Builder.FreshId(); Tag = Some name; IxKind = ixKindOfTag (Some name) }, Some values)
                        | _ ->
                            ({ Id = env.Builder.FreshId(); Rank = 1
                               Extent = IRParam ("__ngroups", 0, IRTNat None)
                               Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                               Kind = SDimension; Dependencies = [] }, None)
                    | None ->
                        ({ Id = env.Builder.FreshId(); Rank = 1
                           Extent = IRParam ("__ngroups", 0, IRTNat None)
                           Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                           Kind = SDimension; Dependencies = [] }, None)
                let gkType = IRTGroupKeys (outerIdx, sourceIdx, enumValues)
                Ok (mkTyped (TExprGroupKeys [tKeys]) gkType)))
    | multipleKeys ->
        // Compound case: infer all keys, verify shared outer index,
        // build a GroupKeys result with dynamic compound outer.
        let inferAll =
            multipleKeys
            |> List.fold (fun accRes k ->
                accRes |> Result.bind (fun acc ->
                    inferExpr env k |> Result.bind (fun tk ->
                        requireArrayArg env tk "group_keys" |> Result.map (fun arrTy ->
                            acc @ [(tk, arrTy)]))))
                (Ok [])
        inferAll |> Result.bind (fun pairs ->
            // Precondition check: all key arrays must be rank-1 and
            // share an outer index. We compare extent expressions
            // structurally -- Blade's typechecker is structural enough
            // that the same Idx<N> annotation produces equal Extent
            // values across multiple bindings.
            let firstSource =
                pairs |> List.head |> snd |> fun ty -> ty.IndexTypes.[0]
            let allShareOuter =
                pairs |> List.forall (fun (_, ty) ->
                    ty.IndexTypes.Length = 1
                    && extentsAgree ty.IndexTypes.[0].Extent firstSource.Extent)
            if not allShareOuter then
                Error (GroupKeysRank1 ())
            else
                // Compound outer: dynamic extent, tagged so codegen
                // recognizes "this is compound-dynamic, dispatch via
                // tuple unordered_map". The tag name reserves
                // `__compoundidx_static` for the future mask-derived
                // path (TyCompoundIdx) which is statically evaluable
                // -- that case would have Extent = IRLit (cardinality)
                // and a different codegen story. Component types are
                // not carried in IRTGroupKeys today; codegen recovers
                // them from the IRGroupKeys node's keys list at emit
                // time, and IDE tooltips would do the same.
                let outerIdx = {
                    Id = env.Builder.FreshId(); Rank = 1
                    Extent = IRParam ("__ngroups", 0, IRTNat None)
                    Symmetry = SymNone
                    Tag = Some "__compoundidx_dynamic"; IxKind = IxKCompoundDynamic
                    Kind = SDimension; Dependencies = []
                }
                let gkType = IRTGroupKeys (outerIdx, firstSource, None)
                let tKeys = pairs |> List.map fst
                Ok (mkTyped (TExprGroupKeys tKeys) gkType))

// group_bucket(gk) -- the grouping's row -> bucket map, as ordinary data.
//
// Result: Array<Int64 like SourceIdx>, over the SAME index slot the key array
// carried, so `bucket` co-iterates with the values that were grouped. A row a
// negative key dropped (docs/features/sql.md, "Negative keys select rows out")
// reads -1; it appears in no bucket, and the CSR permutation never names it.
//
// This is the inverse of the (perm, offsets) pair, which is otherwise reachable
// only from inside a ragged peel. It is what lets a per-group aggregation be
// re-expressed as a dense gather through `bucket` -- the shape reverse-mode AD
// needs (docs/plans/plan-ad-combinators.md 2.17a).
//
// The argument MUST be a bare name. A grouping is not a first-class value: the
// whole runtime structure lives in C++ locals named after the binding
// (`<gk>__ngroups`, `<gk>__offsets`, `<gk>__perm` -- see the ABI comment in
// genGroupKeysBinding), and same-`gk` co-iteration is discharged on expression
// name identity, not on the type. So `let gk2 = gk`, passing a gk as a
// parameter, or an inline `group_bucket(group_keys(k))` have no emittable
// name to suffix. Refuse them HERE, with an explanation, rather than emitting
// C++ that names an undeclared symbol.
/// Shared front half of every grouping accessor: require a BARE NAME, infer it,
/// and hand back the grouping's index pair. `intrinsic` only names the caller in
/// the diagnostic. Used by group_bucket and by extents(gk).
and requireGroupingName ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (intrinsic: string) (grouping: Expr)
        : TypeResult<TypedExpr * IRIndexType * IRIndexType> =
    let describe (e: Expr) =
        match e.Kind with
        | ExprKind.ExprGroupKeys _ -> "an inline `group_keys(...)` call"
        | ExprKind.ExprApp _ -> "a call/index expression"
        | ExprKind.ExprTupleIndex _ -> "a tuple element"
        | ExprKind.ExprTuple _ -> "a tuple"
        | _ -> "a non-name expression"
    match grouping.Kind with
    | ExprKind.ExprVar name ->
        inferExpr env grouping |> Result.bind (fun tGk ->
            match env.Subst.Resolve(tGk.Type) with
            | IRTGroupKeys (outerIdx, sourceIdx, _) -> Ok (tGk, outerIdx, sourceIdx)
            | _ -> Error (GroupBucketNotGrouping name))
    | _ -> Error (GroupingNeedsName (intrinsic, describe grouping))

/// ungroup([r1, .., rF], A): the per-file arrays of a file-segmented axis
/// assembled over the axis (§2.7). This is how a variable that lives in
/// several stores is named over the tiled axis: the inverse of `files(A)`
/// applied to explicit rows, one per store in declaration order, each a
/// bare name over its own store's axis with that store's extent.
and inferUngroupRows ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (rows: Expr list) (alias: string) : TypeResult<TypedExpr> =
    match Map.tryFind alias env.Segmentations with
    | None -> Error (SegmentedDomainErr $"ungroup([..], {alias}): '{alias}' is not a `Chunked<..>` index alias in scope")
    | Some seg ->
        match segmentationFileOffsets seg with
        | None -> Error (SegmentedDomainErr $"ungroup([..], {alias}): '{alias}' has no file level; ungroup takes a grouped array for a regular chunk grid")
        | Some offsets ->
            let extents = offsets |> List.pairwise |> List.map (fun (lo, hi) -> hi - lo)
            if rows.Length <> extents.Length then
                Error (SegmentedDomainErr $"ungroup([..], {alias}): {alias} tiles {extents.Length} store(s) but {rows.Length} row(s) were given")
            else
            rows |> List.map (fun r ->
                match r.Kind with
                | ExprKind.ExprVar _ -> inferExpr env r
                | _ -> Error (SegmentedDomainErr "ungroup([..], A): each row must be a bare name (bind each store's array first)"))
            |> List.fold (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ [ x ]))) (Ok [])
            |> Result.bind (fun tRows ->
                let checkRow (i: int) (tr: TypedExpr) : TypeResult<unit> =
                    match env.Subst.Resolve tr.Type with
                    | ArrayElem at when not at.IndexTypes.IsEmpty ->
                        (match at.IndexTypes.Head.Extent with
                         | IRLit (IRLitInt n) when n = extents.[i] -> Ok ()
                         | IRLit (IRLitInt n) -> Error (SegmentedDomainErr $"ungroup([..], {alias}): row {i} has extent {n} but store {i} of {alias} has extent {extents.[i]}")
                         | _ -> Ok ())
                    | _ -> Error (SegmentedDomainErr $"ungroup([..], {alias}): row {i} is not an array")
                tRows |> List.mapi checkRow
                |> List.fold (fun acc r -> acc |> Result.bind (fun () -> r)) (Ok ())
                |> Result.bind (fun () ->
                    match env.Subst.Resolve tRows.Head.Type with
                    | ArrayElem at ->
                        let rest = at.IndexTypes |> List.tail
                        let resultType = mkArrayArrow (seg.Source :: rest) at.ElemType None
                        Ok (mkTyped (TExprUngroupRows (tRows, offsets, seg.Source)) resultType)
                    | _ -> Error (SegmentedDomainErr "ungroup([..], A): rows must be arrays")))

/// ungroup(G[, A]): rows of a segment-grouped array reassembled over the
/// source axis (§2.3, §3.3) -- the only materializing operation of the
/// design, and the inverse of `group_by(_, segments(A))`. G's outer slot
/// must be the outer of such a grouping (its id was recorded by
/// inferGroupBy); when the outer slot was minted by a transform that did not
/// keep the id, the two-argument form names the axis explicitly.
and inferUngroup ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (grouped: Expr) (aliasOpt: string option) : TypeResult<TypedExpr> =
    inferExpr env grouped |> Result.bind (fun tG ->
        match env.Subst.Resolve(tG.Type) with
        | ArrayElem arrTy when arrTy.IndexTypes.Length >= 2 ->
            let outer = arrTy.IndexTypes.[0]
            let member_ = arrTy.IndexTypes.[1]
            let alias =
                match aliasOpt with
                | Some a -> Ok a
                | None ->
                    match env.SegmentedOuters.TryGetValue outer.Id with
                    | true, a -> Ok a
                    | _ -> Error (SegmentedDomainErr "ungroup(G): G is not (directly) the result of `group_by(_, segments(A))`; if it derives from one, name the axis: ungroup(G, A)")
            alias |> Result.bind (fun a ->
                if a.Contains "," then
                    // a grid grouping's result: two axes come back
                    let aliases = a.Split(',') |> List.ofArray
                    let segs = aliases |> List.choose (fun x -> Map.tryFind x env.Segmentations)
                    if segs.Length <> 2 then Error (SegmentedDomainErr $"ungroup: the tile grouping's axes {a} are not both in scope")
                    else
                        let bounds = segs |> List.map segmentationOffsets
                        let g = int64 ((bounds.[0].Length - 1) * (bounds.[1].Length - 1))
                        (match outer.Extent with
                         | IRLit (IRLitInt n) when n <> g -> Error (SegmentedDomainErr $"ungroup: the outer slot has {n} tiles but segments({a}) has {g}")
                         | _ ->
                             let sources = segs |> List.map (fun s -> s.Source)
                             let rest = arrTy.IndexTypes |> List.skip 2
                             let resultType = mkArrayArrow (sources @ rest) arrTy.ElemType None
                             Ok (mkTyped (TExprUngroupGrid (tG, sources, bounds)) resultType))
                else
                // A KEY grouping's rows (`group_by(v, group_keys(k))`) are in
                // PERMUTED order: putting them back is a scatter by the
                // grouping's permutation (rows a negative key dropped have no
                // cell at all), which is v2 of G90 (plan section 5.4) -- and
                // the lanes did not agree on it (the C++ lane scattered, the
                // interpreter concatenated). Only a structural grouping's
                // outer slot (or a row map's derived from one) is restored.
                if outer.IxKind = IxKGroupOuter && member_.IxKind = IxKGroupMember
                   && not (env.SegmentedOuters.ContainsKey outer.Id) then
                    let what = if env.GroupRowMapOuters.ContainsKey outer.Id then "a per-group row map over a KEY grouping" else "a KEY grouping (group_keys)"
                    Error (SegmentedDomainErr $"ungroup(_, {a}): the grouped array is {what}, whose rows are in the grouping's permuted order; restoring the axis is a scatter by its permutation, not built (G90 v2). ungroup restores the axis of a structural grouping (`group_by(_, segments(A))`) and of row maps over one")
                else
                match Map.tryFind a env.Segmentations with
                | None -> Error (SegmentedDomainErr $"ungroup(_, {a}): '{a}' is not a `Chunked<..>` index alias in scope")
                | Some seg ->
                    let g = int64 (List.length (segmentationOffsets seg) - 1)
                    let outerOk = (match outer.Extent with IRLit (IRLitInt n) -> n = g | _ -> true)
                    if not outerOk then
                        Error (SegmentedDomainErr $"ungroup: the outer slot has {outer.Extent} groups but '{a}' has {g} segments")
                    elif member_.IxKind <> IxKGroupMember && member_.IxKind <> IxKRagged && member_.IxKind <> IxKRaggedInline then
                        Error (SegmentedDomainErr "ungroup: the second slot must be the ragged member slot of a group_by result")
                    else
                        let rest = arrTy.IndexTypes |> List.skip 2
                        let resultType = mkArrayArrow (seg.Source :: rest) arrTy.ElemType None
                        Ok (mkTyped (TExprUngroup (tG, seg.Source)) resultType))
        | _ -> Error (SegmentedDomainErr "ungroup expects a segment-grouped array (rank >= 2: groups x members)"))

and inferGroupBucket (ie: InferEntries) (env: TypeEnv) (grouping: Expr) : TypeResult<TypedExpr> =
    let gridRefusal =
        match grouping.Kind with
        | ExprKind.ExprVar n ->
            (match lookupVar n env with
             | Some vi ->
                 (match env.Subst.Resolve vi.Type with
                  | IRTGroupKeys (outer, _, _) when (match outer.Tag with Some t -> t.StartsWith "__segmentsgrid|" | None -> false) -> true
                  | _ -> false)
             | None -> false)
        | _ -> false
    if gridRefusal then Error (SegmentedDomainErr "group_bucket over a tile grouping (`segments(C0, C1)`) is not supported: its source is two-dimensional; use extents(gk) for the tile sizes") else
    requireGroupingName ie env "group_bucket" grouping
    |> Result.map (fun (tGk, _, sourceIdx) ->
        // Reuse the source slot verbatim (same Id, tag and extent): the bucket
        // map spans exactly the rows the keys did, so it is the SAME index
        // space, not a fresh one -- which is what makes
        // `zip(v, group_bucket(gk))` typecheck.
        mkTyped (TExprGroupBucket tGk) (mkArrayArrow [sourceIdx] (IRTScalar ETInt64) None))

and inferReplicate ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) count body : TypeResult<TypedExpr> =
    inferExpr env count |> Result.bind (fun tC ->
    // A count is index-like (round 6): a literal-only count is an Int64.
    let tC =
        match literalOnlyElem env.Subst tC with
        | Some le when litClassOf le = 0 -> retypeLiteralOnly env.Subst ETInt64 tC |> Option.defaultValue tC
        | _ -> tC
    inferExpr env body |> Result.bind (fun tB ->
        // The count must be compile-time known. Accept a bare literal, or any
        // statically evaluable integer expression (a `let static` value or a
        // static-function call), resolved via the same StaticEval the lowering
        // phase uses. env.StaticValues/StaticFunctions were populated in the
        // checkModule pre-pass.
        let n =
            match tC.Kind with
            | TExprLit (LitInt v) -> Some (int v)
            | _ ->
                let staticEnv : StaticEval.StaticEnv =
                    { Values = env.StaticValues
                      Globals = env.StaticValues
                      Functions =
                        env.StaticFunctions
                        |> Map.map (fun _ (fd: FunctionDecl) -> StaticEval.staticFuncDefOf fd)
                      CalledFunctions = ref Set.empty
                      ProviderRoots = Map.empty
                      Segments = Map.empty; Widths = Map.empty
                      Structs = Map.empty }
                match StaticEval.evalExpr staticEnv StaticEval.maxSteps count with
                | Ok (StaticEval.SVInt v) -> Some (int v)
                | _ -> None
        // Normalize the resolved count to a literal in the typed tree, so the
        // lowering unroll (List.replicate n) sees a concrete factor regardless
        // of how the count was written at the source level.
        let litCount k = mkTyped (TExprLit (LitInt (int64 k))) tC.Type
        match n with
        | None ->
            Error (StaticValueErr "replicate count must be a compile-time integer (a literal, `let static`, or static-function call)")
        | Some 1 ->
            // replicate(1, c) == c
            Ok tB
        | Some n when n >= 2 ->
            let resolved = env.Subst.Resolve(tB.Type)
            // Create anonymous Idx<N> for the replicate dimension
            let seqIdx = {
                Id = env.Builder.FreshId()
                Rank = 1
                Extent = IRLit (IRLitInt (int64 n))
                Symmetry = SymNone
                Tag = Some "__seq"; IxKind = IxKSeq
                Kind = SDimension
                Dependencies = []
            }
            let resultType =
                match resolved with
                | ArrayElem arrTy ->
                    mkArrayLike { arrTy with IndexTypes = seqIdx :: arrTy.IndexTypes }
                | IRTScalar et ->
                    mkArrayArrow [seqIdx] (IRTScalar et) None
                | _ ->
                    // Same fallback as the sequence case above.
                    mkArrayArrow [seqIdx] (IRTScalar ETFloat64) None
            Ok (mkTyped (TExprReplicate (litCount n, tB)) resultType)
        | _ ->
            Error (BuiltinArgErr (match n with Some k -> $"replicate count must be >= 1, got {k}" | None -> "replicate count must be a compile-time integer >= 1"))))
