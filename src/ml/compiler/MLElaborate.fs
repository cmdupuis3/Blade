/// ML-module elaboration: the equivariant ops as compile-time source
/// synthesis (user decision, IrrepsIdx/CGPath arc: "elaborate to Blade
/// source" over opaque builtins).
///
/// Surface (call-shaped, recognized when the name is not user-bound;
/// configs are `let static` bindings — ordinary required-static arguments):
///
///   y_to(LMAX, x, y, z)                 -> Array<Float like Idx<(LMAX+1)^2>>
///   tensor_product(CFG, x, y, w)        -> Array<Float like Idx<total_dim(specOut)>>
///   linear(SPEC_IN, SPEC_OUT, w, x)     -> Array<Float like Idx<total_dim(SPEC_OUT)>>
///   gated(SPEC, x)                      -> Array<Float like Idx<total_dim(SPEC)>>
///   scalars(SPEC, x)                    -> Array<Float like Idx<#l=0 entries>>
///   norms(SPEC, x)                      -> Array<Float like Idx<#(block, mu) slots>>
///
/// Functions carrying `where <alias>.equiv(O3|SO3)` are additionally JUDGED
/// (Blade.ML.Equiv) at the pass-1/pass-2 seam: the body must compose only
/// equivariance-preserving operations, else BL4008.
///
/// where a SPEC is a static array of (l, parity, mult) int triples
/// (parity: 0 = even, 1 = odd), and a CFG is a static triple
/// (spec1, spec2, specOut). `sh_spec(lmax)` builds the Y-expansion spec
/// statically, and `total_dim` / `tp_weight_dim` / `linear_weight_dim`
/// are static sizing functions for weight buffers — all registered by
/// ml/compiler/MLStatics.fs through StaticEval's builtin registry.
///
/// For each distinct (op, resolved config) the elaborator synthesizes ONE
/// Blade function (`__ml_tp_1`, ...) whose body is exactly the loop
/// structure of the ml/ reference implementation (same iteration order —
/// value agreement to the ulp), with path metadata and real-basis CG
/// coefficients (WignerTables, F1: the real support, not m1+m2=m3) baked
/// as literal tables. Call sites rewrite to the generated names.
///
/// Pipeline position: BEFORE Grad expansion (TypeCheck.typeCheck), so
/// grad() differentiates elaborated ops through its normal inliner — no
/// VJP registry, no new IR nodes, and the generated functions type-check
/// like user code.
module Blade.ML.Elaborate

open Blade.Ast
open Blade.StaticEval
open Blade.ML.Spec

// ============================================================================
// Elaboration errors
// ============================================================================

/// Elaboration failure: a message plus the BLxxxx code it surfaces under.
/// BL5000 = generic ML-elaboration failure; BL4007 = "no equivariant map
/// exists" (Schur selection-rule violations: unreachable tensor_product
/// output blocks, linear over specs sharing no (l, parity)). Both codes
/// live in Diagnostics.Codes; expand's boundary renders Code faithfully.
type private ElabError = { Code: string; Msg: string }
let private err5000 (msg: string) : ElabError = { Code = "BL5000"; Msg = msg }
let private err4007 (msg: string) : ElabError = { Code = "BL4007"; Msg = msg }

// Spec model lives in ml/compiler/MLSpec.fs; StaticValue conversions and
// the sizing builtins in ml/compiler/MLStatics.fs (shared seam). Local
// aliases wrap their string errors into coded ElabErrors:
let private specOfStatic what v = Blade.ML.Statics.specOfStatic what v |> Result.mapError err5000
let private cfgOfStatic what v = Blade.ML.Statics.cfgOfStatic what v |> Result.mapError err5000

// ============================================================================
// AST construction helpers (mirroring Grad.fs's style)
// ============================================================================

let private v (n: string) = syn (ExprVar n)
let private fLit (x: float) = syn (ExprLit (LitFloat x))
let private iLit (n: int) = syn (ExprLit (LitInt (int64 n)))
let private add a b = syn (ExprBinOp (Elementwise, OpAdd, a, b))
let private sub a b = syn (ExprBinOp (Elementwise, OpSub, a, b))
let private mul a b = syn (ExprBinOp (Elementwise, OpMul, a, b))
let private divE a b = syn (ExprBinOp (Elementwise, OpDiv, a, b))
let private idx (arr: string) (i: Expr) = syn (ExprApp (v arr, [i]))
let private sLet n value = StmtLet { Pattern = synPat (PatVar n); Type = None; Value = value; Mutability = BindLet }
let private sLetMut n value = StmtLet { Pattern = synPat (PatVar n); Type = None; Value = value; Mutability = BindMut }
let private sAccum lhs e = StmtExpr (syn (ExprAssign (lhs, add lhs e)))
let private sAssign lhs e = StmtExpr (syn (ExprAssign (lhs, e)))
let private sFor var lo hi body = StmtForIn (var, syn (ExprDotDot (iLit lo, iLit hi)), body)
let private zerosLit (n: int) = syn (ExprArrayLit (List.replicate n (fLit 0.0)))
let private intArrLit (xs: int list) = syn (ExprArrayLit (xs |> List.map iLit))
let private floatArrLit (xs: float list) = syn (ExprArrayLit (xs |> List.map fLit))
let private tyFloatArr (n: int) = TyArray (TyNamed ("Float", []), [ TyIdx (iLit n) ])

/// Array<Float like IrrepsIdx<[(l, p, m), ...]>> — irreps-typed signature
/// slot, the inline spec literal rebuilt from the RESOLVED Spec. Used for
/// the ops' feature params and results that genuinely ARE the irreps space
/// (single-vector forms); row-stacked `_rows` buffers (extent nRows *
/// total_dim) and path-major weight buffers are NOT irreps spaces and stay
/// plain Idx. Anonymous (no alias name), so a user's alias of the same
/// spec unifies by the name-permissive rule while a WRONG-spec annotation
/// or argument is a type error.
let private tyIrrepsArr (s: Spec) : TypeExpr =
    let specLit =
        syn (ExprArrayLit (s |> List.map (fun e ->
            syn (ExprTuple [ iLit e.L; iLit e.Parity; iLit e.Mult ]))))
    TyArray (TyNamed ("Float", []), [ TyIrrepsIdx specLit ])

let private mkFunc name (ps: (string * TypeExpr) list) retTy body : FunctionDecl =
    { Name = name
      TypeParams = []
      Params = ps |> List.map (fun (n, t) -> { Name = n; Type = Some t; Mutability = Immutable })
      WhereClause = None
      ReturnType = Some retTy
      Body = body
      IsStatic = false }

// ============================================================================
// Op synthesis
// ============================================================================

/// __ml_sigmoid: shared scalar helper for gated activations.
let private sigmoidDecl (name: string) : FunctionDecl =
    mkFunc name [ ("z", TyNamed ("Float", [])) ] (TyNamed ("Float", []))
        (divE (fLit 1.0) (add (fLit 1.0) (syn (ExprApp (v "exp", [ syn (ExprUnaryOp (OpNeg, v "z")) ])))))

/// y_to (closed forms, lmax <= 2 in v1): mirrors ml/SphericalHarmonics
/// component order (m ascending per l) and the orthonormalized real solid
/// harmonics constants pinned by ml/Tests_SphericalHarmonics.
let private yToDecl (name: string) (lmax: int) : Result<FunctionDecl, ElabError> =
    if lmax < 0 || lmax > 2 then
        Error (err5000 "y_to: lmax must be 0..2 in v1 (closed forms; the recurrence-generated form is future work)")
    else
    let dimTot = (lmax + 1) * (lmax + 1)
    let f = TyNamed ("Float", [])
    let stmts =
        [ yield sLetMut "sh" (zerosLit dimTot)
          yield sAssign (idx "sh" (iLit 0)) (fLit 0.28209479177387814)
          if lmax >= 1 then
              yield sAssign (idx "sh" (iLit 1)) (mul (fLit 0.4886025119029199) (v "y"))
              yield sAssign (idx "sh" (iLit 2)) (mul (fLit 0.4886025119029199) (v "z"))
              yield sAssign (idx "sh" (iLit 3)) (mul (fLit 0.4886025119029199) (v "x"))
          if lmax >= 2 then
              yield sLet "r2" (add (add (mul (v "x") (v "x")) (mul (v "y") (v "y"))) (mul (v "z") (v "z")))
              yield sAssign (idx "sh" (iLit 4)) (mul (fLit 1.0925484305920792) (mul (v "x") (v "y")))
              yield sAssign (idx "sh" (iLit 5)) (mul (fLit 1.0925484305920792) (mul (v "y") (v "z")))
              yield sAssign (idx "sh" (iLit 6)) (mul (fLit 0.31539156525252005) (sub (mul (fLit 3.0) (mul (v "z") (v "z"))) (v "r2")))
              yield sAssign (idx "sh" (iLit 7)) (mul (fLit 1.0925484305920792) (mul (v "x") (v "z")))
              yield sAssign (idx "sh" (iLit 8)) (mul (fLit 0.5462742152960396) (sub (mul (v "x") (v "x")) (mul (v "y") (v "y")))) ]
    Ok (mkFunc name [ ("x", f); ("y", f); ("z", f) ] (tyIrrepsArr (shSpec lmax))
            (syn (ExprBlock (stmts, Some (v "sh")))))

/// The tensor_product kernel's statement list, parameterized by the name of
/// the DENSE weight buffer it reads: returns (statements, result expression).
/// The loop order and the left-associated product `(((coef*w)*x)*y)` mirror
/// the ml/TensorProduct reference exactly, which is what makes
/// tensor_product / derive_tp agree with it to the ulp. The S₂-compacted
/// kernels do NOT go through here — they emit only the kept paths, with the
/// dropped contributions fused in (deriveS2TpDecl, plan §7 stage 1b).
let private tpBodyStmts (cfg: TPConfig) (wName: string) : Stmt list * Expr =
    let dO = totalDim cfg.SpecOut
    let paths = tpPaths cfg
    let s1 = blockStarts cfg.Spec1
    let s2 = blockStarts cfg.Spec2
    let so = blockStarts cfg.SpecOut
    // per-path metadata
    let pMult1 = paths |> List.map (fun (b1, _, _) -> cfg.Spec1.[b1].Mult)
    let pMult2 = paths |> List.map (fun (_, b2, _) -> cfg.Spec2.[b2].Mult)
    let pMultO = paths |> List.map (fun (_, _, bo) -> cfg.SpecOut.[bo].Mult)
    let pD1 = paths |> List.map (fun (b1, _, _) -> dim cfg.Spec1.[b1])
    let pD2 = paths |> List.map (fun (_, b2, _) -> dim cfg.Spec2.[b2])
    let pDO = paths |> List.map (fun (_, _, bo) -> dim cfg.SpecOut.[bo])
    let pS1 = paths |> List.map (fun (b1, _, _) -> s1.[b1])
    let pS2 = paths |> List.map (fun (_, b2, _) -> s2.[b2])
    let pSO = paths |> List.map (fun (_, _, bo) -> so.[bo])
    let pWOff =
        (0, paths) ||> List.scan (fun acc (b1, b2, bo) ->
            acc + cfg.SpecOut.[bo].Mult * cfg.Spec1.[b1].Mult * cfg.Spec2.[b2].Mult)
    let cgPerPath =
        paths |> List.map (fun (b1, b2, bo) ->
            Blade.ML.WignerTables.realCGSparse cfg.Spec1.[b1].L cfg.Spec2.[b2].L cfg.SpecOut.[bo].L)
    let cgOff = (0, cgPerPath) ||> List.scan (fun acc es -> acc + es.Length)
    let cgC1 = cgPerPath |> List.collect (fun es -> es |> Array.toList |> List.map (fun e -> e.C1))
    let cgC2 = cgPerPath |> List.collect (fun es -> es |> Array.toList |> List.map (fun e -> e.C2))
    let cgC3 = cgPerPath |> List.collect (fun es -> es |> Array.toList |> List.map (fun e -> e.C3))
    let cgCo = cgPerPath |> List.collect (fun es -> es |> Array.toList |> List.map (fun e -> e.Coef))
    let nPaths = paths.Length
    // out(pSO(p) + mo*pDO(p) + c3(t)) += coef(t) * w(woff(p) + (mo*m1 + u1)*m2 + u2)
    //                                     * x(pS1(p) + u1*pD1(p) + c1(t))
    //                                     * y(pS2(p) + u2*pD2(p) + c2(t))
    let stmts =
        [ sLetMut "out" (zerosLit dO)
          sLet "__t_m1" (intArrLit pMult1)
          sLet "__t_m2" (intArrLit pMult2)
          sLet "__t_mo" (intArrLit pMultO)
          sLet "__t_d1" (intArrLit pD1)
          sLet "__t_d2" (intArrLit pD2)
          sLet "__t_do" (intArrLit pDO)
          sLet "__t_s1" (intArrLit pS1)
          sLet "__t_s2" (intArrLit pS2)
          sLet "__t_so" (intArrLit pSO)
          sLet "__t_wo" (intArrLit pWOff)
          sLet "__t_co" (intArrLit cgOff)
          sLet "__cg_c1" (intArrLit cgC1)
          sLet "__cg_c2" (intArrLit cgC2)
          sLet "__cg_c3" (intArrLit cgC3)
          sLet "__cg_v" (floatArrLit cgCo)
          sFor "p" 0 nPaths
            [ StmtForIn ("mo", syn (ExprDotDot (iLit 0, idx "__t_mo" (v "p"))),
                [ StmtForIn ("u1", syn (ExprDotDot (iLit 0, idx "__t_m1" (v "p"))),
                    [ StmtForIn ("u2", syn (ExprDotDot (iLit 0, idx "__t_m2" (v "p"))),
                        [ sLet "wv" (idx wName (add (idx "__t_wo" (v "p"))
                                                  (add (mul (add (mul (v "mo") (idx "__t_m1" (v "p"))) (v "u1"))
                                                            (idx "__t_m2" (v "p")))
                                                       (v "u2"))))
                          StmtForIn ("t", syn (ExprDotDot (idx "__t_co" (v "p"), idx "__t_co" (add (v "p") (iLit 1)))),
                            // LEFT-associated product (((coef*w)*x)*y):
                            // exactly the ml/ reference's evaluation
                            // order, so values agree to the ulp.
                            [ sAccum (idx "out" (add (idx "__t_so" (v "p"))
                                                     (add (mul (v "mo") (idx "__t_do" (v "p")))
                                                          (idx "__cg_c3" (v "t")))))
                                     (mul (mul (mul (idx "__cg_v" (v "t")) (v "wv"))
                                               (idx "x" (add (idx "__t_s1" (v "p"))
                                                             (add (mul (v "u1") (idx "__t_d1" (v "p")))
                                                                  (idx "__cg_c1" (v "t"))))))
                                          (idx "y" (add (idx "__t_s2" (v "p"))
                                                        (add (mul (v "u2") (idx "__t_d2" (v "p")))
                                                             (idx "__cg_c2" (v "t")))))) ]) ]) ]) ]) ] ]
    (stmts, v "out")

/// tensor_product for a fixed config: path/mult loops over baked tables,
/// real-basis CG entries flattened path-major. Mirrors ml/TensorProduct
/// loop order (paths -> muO -> mu1 -> mu2 -> entries); the forward w<>0
/// skip is omitted (adding exact zeros in the same order is the identity).
let private tpDecl (name: string) (cfg: TPConfig) : FunctionDecl =
    let stmts, ret = tpBodyStmts cfg "w"
    mkFunc name
        [ ("x", tyIrrepsArr cfg.Spec1); ("y", tyIrrepsArr cfg.Spec2); ("w", tyFloatArr (tpWeightDim cfg)) ]
        (tyIrrepsArr cfg.SpecOut) (syn (ExprBlock (stmts, Some ret)))

/// derive_sym_tp / derive_alt_tp for a fixed spec: the S₂-compacted
/// self-tensor-product derive_tp(S, S, x, y, w), compacted in ARITHMETIC as
/// well as in parameters (plan §7 stage 1b). Only the KEPT paths (b1 ≤ b2,
/// minus the τ = −1 multiplicity-1 diagonals) are emitted at all; each kept
/// path's dropped counterpart — the b1 > b2 mirror path, or the (u2, u1) half
/// of a diagonal path's τ-symmetric weight block — folds into the kept term
/// as a SECOND product against the same baked CG entry, per MLSpec.S2TpCell:
///
///   out[oo + mo*do + c3] += (coef * w[wb + mo*ws])
///                         * ( x[oA + c1]*y[oB + c2]
///                           + pairSign * (y[oA + e2]*x[oB + e1]) )
///
/// so the dense path table, the dense weight buffer, and stage 1a's expansion
/// loop all disappear. The license for collapsing the mirror path
/// onto the kept path's CG table is the cross-block exchange identity
/// realCG(l2,l1,l3)[m2,m1,m3] = σ·realCG(l1,l2,l3)[m1,m2,m3], pinned
/// bit-exact in ml/Tests_Wigner.
///
/// Association: `(coef*w) * (x*y)` per term rather than tpDecl's
/// left-associated `((coef*w)*x)*y`, so that in the F(x, x) case the two
/// products of a mirror cell are bit-identical and Λ²'s `F(x, x) = 0` stays
/// EXACT (the diagonal cells cancel across the CG entry pairs instead). Values
/// therefore differ from stage 1a in the last ulps — §6.7's tolerance pin,
/// relative 1e-13 against derive_tp on the embedded dense weights.
let private deriveS2TpDecl (name: string) (s: Spec) (comp: S2Component) : Result<FunctionDecl, ElabError> =
    let cfg = selfTpConfig s
    let packedDim = match comp with S2Sym -> symTpWeightDim s | S2Alt -> altTpWeightDim s
    let cells = s2TpCells comp s
    // The S₂ split is a partition of the dense parameter space — cheap to
    // check here, and a violation would mis-size a user's weight buffer.
    if not (s2TpSplitIsPartition s) then
        Error (err5000 (sprintf "internal: the S2 split of the self-TP weight space is not a partition (sym %d + alt %d <> dense %d)"
                            (symTpWeightDim s) (altTpWeightDim s) (tpWeightDim cfg)))
    elif packedDim = 0 || cells.IsEmpty then
        Error (err5000 "internal: empty S2 component reached kernel synthesis (the call site must reject it as BL4007)")
    // Every packed slot must be read by exactly one (cell, mo): the cells
    // cover the buffer the user is asked to supply, or parameters are dead.
    elif cells |> List.sumBy (fun c -> c.MultO) <> packedDim then
        Error (err5000 (sprintf "internal: the fused S2 cell table reads %d of the %d packed weight slots"
                            (cells |> List.sumBy (fun c -> c.MultO)) packedDim))
    else
    let dO = totalDim cfg.SpecOut
    let paths = tpPaths cfg |> List.toArray
    // CG tables for the KEPT paths only, flattened in first-use order. `e1`/
    // `e2` are the partner term's component reads: the CG transpose on a
    // mirror path, the identity on a diagonal one (S2TpCell).
    let used = cells |> List.map (fun c -> (c.Path, c.IsMirror)) |> List.distinct
    let cgOf (p: int, isMirror: bool) =
        let (b1, b2, bo) = paths.[p]
        Blade.ML.WignerTables.realCGSparse s.[b1].L s.[b2].L cfg.SpecOut.[bo].L
        |> Array.toList
        |> List.map (fun e ->
            if isMirror then (e.C1, e.C2, e.C3, e.Coef, e.C2, e.C1)
            else (e.C1, e.C2, e.C3, e.Coef, e.C1, e.C2))
    let cgPerPath = used |> List.map cgOf
    let cgOff = (0, cgPerPath) ||> List.scan (fun acc es -> acc + es.Length)
    let cgRange =
        used |> List.mapi (fun i (p, _) -> (p, (cgOff.[i], cgOff.[i + 1]))) |> Map.ofList
    let flat = List.concat cgPerPath
    let pick f = flat |> List.map f
    let kI f = intArrLit (cells |> List.map f)
    let kIdx (t: string) = idx t (v "k")
    let tIdx (t: string) = idx t (v "t")
    let stmts =
        [ sLetMut "out" (zerosLit dO)
          sLet "__k_oa" (kI (fun c -> c.OffA))
          sLet "__k_ob" (kI (fun c -> c.OffB))
          sLet "__k_oo" (kI (fun c -> c.OutOff))
          sLet "__k_do" (kI (fun c -> c.OutDim))
          sLet "__k_nm" (kI (fun c -> c.MultO))
          sLet "__k_wb" (kI (fun c -> c.WBase))
          sLet "__k_ws" (kI (fun c -> c.WStride))
          sLet "__k_ps" (floatArrLit (cells |> List.map (fun c -> c.PairSign)))
          sLet "__k_cl" (kI (fun c -> fst (Map.find c.Path cgRange)))
          sLet "__k_ch" (kI (fun c -> snd (Map.find c.Path cgRange)))
          sLet "__cg_c1" (intArrLit (pick (fun (a, _, _, _, _, _) -> a)))
          sLet "__cg_c2" (intArrLit (pick (fun (_, a, _, _, _, _) -> a)))
          sLet "__cg_c3" (intArrLit (pick (fun (_, _, a, _, _, _) -> a)))
          sLet "__cg_v" (floatArrLit (pick (fun (_, _, _, a, _, _) -> a)))
          sLet "__cg_e1" (intArrLit (pick (fun (_, _, _, _, a, _) -> a)))
          sLet "__cg_e2" (intArrLit (pick (fun (_, _, _, _, _, a) -> a)))
          sFor "k" 0 cells.Length
            [ StmtForIn ("mo", syn (ExprDotDot (iLit 0, kIdx "__k_nm")),
                [ sLet "wv" (idx "w" (add (kIdx "__k_wb") (mul (v "mo") (kIdx "__k_ws"))))
                  StmtForIn ("t", syn (ExprDotDot (kIdx "__k_cl", kIdx "__k_ch")),
                    [ sAccum (idx "out" (add (kIdx "__k_oo")
                                             (add (mul (v "mo") (kIdx "__k_do")) (tIdx "__cg_c3"))))
                             (mul (mul (tIdx "__cg_v") (v "wv"))
                                  (add (mul (idx "x" (add (kIdx "__k_oa") (tIdx "__cg_c1")))
                                            (idx "y" (add (kIdx "__k_ob") (tIdx "__cg_c2"))))
                                       (mul (kIdx "__k_ps")
                                            (mul (idx "y" (add (kIdx "__k_oa") (tIdx "__cg_e2")))
                                                 (idx "x" (add (kIdx "__k_ob") (tIdx "__cg_e1"))))))) ]) ]) ] ]
    Ok (mkFunc name
            [ ("x", tyIrrepsArr s); ("y", tyIrrepsArr s); ("w", tyFloatArr packedDim) ]
            (tyIrrepsArr cfg.SpecOut) (syn (ExprBlock (stmts, Some (v "out")))))

/// The monomial lift x |-> its symmetric K-th power (plan §3.1's storage
/// identification, §6.6's convention fork): a flat Idx<C(n+K-1, K)> vector of
/// the UNWEIGHTED monomials ∏_j x(i_j) over the canonical multisets
/// i1 <= ... <= iK of 0..n-1, n = total_dim(SPEC), in LEX order (nondecreasing
/// tuples, ascending lexicographic — the SymIdx<K, ·> cell order, §6.8).
///
/// NO multiplicity weights: the cell IS the coefficient of the monomial
/// e_{i1}···e_{iK}, not the symmetrized-tensor component (which differs by
/// exactly multiplicity(idx) per cell). Stated once in the plan, implemented
/// once here.
///
/// The K tuple-position tables are baked flat, one per position, so the loop
/// is a single pass over cells with a left-associated K-fold product. The
/// input is the irreps space; the OUTPUT is a plain Idx<cells> — the monomial
/// space is not an irreps space (its O(3) action is Sym^K(V) =
/// ml.sym_spec(SPEC, K)).
///
/// Stage 3 made `SymIdx<K, IrrepsIdx<SPEC>>` writable, so the result COULD now
/// be declared as that type — the storage agrees exactly (§6.6 monomial
/// coordinates in §6.8 lex order = the SymIdx cell order, C(n+K-1, K) cells).
/// It is deliberately NOT: the declared type would be rank-K, and the emitted
/// body here is a rank-1 flat pass (`out(c)` over 0..cells). Those are
/// different C++ shapes — `Array<double, 1>` vs the rank-K compact
/// `Array<double, K>` with a symmetric allocator — so the retype compiles in
/// Blade (Unify's SymNone-is-a-wildcard permissiveness accepts one slot
/// against one slot) but produces C++ that does not build. Retyping the result
/// therefore requires REWRITING the body to K-ary canonical accesses; tracked
/// as a follow-up, not a type annotation change.
let private symLiftDecl (name: string) (s: Spec) (k: int) : Result<FunctionDecl, ElabError> =
    let n = totalDim s
    let cells = binomial (n + k - 1) k
    if cells > 100000L then
        Error (err5000 (sprintf "sym_lift: the degree-%d monomial space of a dim-%d input has C(%d, %d) = %d cells, over the 100000-cell limit — lift a smaller spec (ml.scalars/ml.linear first) or lower K"
                            k n (n + k - 1) k cells))
    else
    let tuples = symMultisets n k
    let nCells = tuples.Length
    let stmts =
        [ yield sLetMut "out" (zerosLit nCells)
          for j in 0 .. k - 1 do
            yield sLet (sprintf "__m%d" j) (intArrLit (tuples |> List.map (fun t -> t.[j])))
          yield StmtForIn ("c", syn (ExprDotDot (iLit 0, iLit nCells)),
                    [ sAssign (idx "out" (v "c"))
                              ([ 0 .. k - 1 ]
                               |> List.map (fun j -> idx "x" (idx (sprintf "__m%d" j) (v "c")))
                               |> List.reduce mul) ]) ]
    Ok (mkFunc name [ ("x", tyIrrepsArr s) ] (tyFloatArr nCells)
            (syn (ExprBlock (stmts, Some (v "out")))))

/// derive_poly(SPEC, K, SOUT, x, w) for a fixed (spec, K, specOut): the
/// COMPLETE basis of the degree-K homogeneous equivariant maps V -> W, one
/// weight per basis map, `polyWeightDim SPEC K SOUT` of them
/// (plan-transforms-as-types §3.3b construction, §7 stage 2b-iii emission).
/// K = 1 is derive_linear; K = 2 is derive_sym_tp's hom-space seen through the
/// UNIFORM label convention rather than through the kept-path layout.
///
/// THE EMITTED KERNEL, in the order the statements come out (everything is
/// ordinary Blade — lets, loops, baked tables, accumulate-only, so grad()
/// differentiates it through its normal inliner):
///
///  1. MONOMIALS. Per (copy, degree) actually used by some label — a copy is
///     one multiplicity slot of the spec (MLSpec.polyCopies, §3.3b move 1) —
///     the degree-j monomials of that copy's OWN 2l+1 components over the
///     canonical multisets (lex `symMultisets`, the symLiftDecl convention:
///     bare products, NO multiplicity weights, §6.6). Baked as j position
///     tables of ABSOLUTE x indices, one accumulate loop per (copy, degree).
///  2. OCCURRENCE FEATURES. The T_{j,l} matvec (SymPowerTables.symPowerTable,
///     rows baked as float literals in sparse (row, cell, coef) form):
///     f[row] = Σ_I T[occ][row, I]·mono_I — §3.3b identity (2), evaluation
///     carries NO /N_I factor (that lives in the Gram identity (3)). A label's
///     flat occurrence index indexes the table's `Occurrences` list directly;
///     the two orders are re-checked here against the label's own (L, copy).
///     At j = 1 there is no table: Sym¹(V_l) = V_l and the feature IS the
///     copy's slice of x, read in place — which is what makes K = 1 reduce to
///     derive_linear's arithmetic bit for bit.
///  3. CHAINS, SHARED BY PREFIX. The used copies' features are coupled
///     left-comb in copy order through baked `realCGSparse` tables at unit
///     weight. Labels within a sector form a TREE over (occurrence choices,
///     intermediate L's) and the enumeration is prefix-ordered, so emitting
///     each distinct prefix node once — on first appearance, which is
///     automatically a topological order — gives code size ~ O(#labels)
///     rather than O(#labels · K). Each node is its own `let mut` buffer:
///     one write, later reads, never a read-then-rewrite of one buffer
///     (Grad.checkWriteAfterRead would refuse that).
///  4. SECTOR CONSTANT. √(k!/∏_c j_c!) — §3.3b identity (1)'s cross-copy
///     multinomial, the ONLY place it appears — baked as an EXPLICIT scalar
///     multiplication in the weight product (§6.9(ii), resolved here in favour
///     of auditability: the constant is a literal in the emitted table, not
///     absorbed into a T row or a CG coefficient). It is 1.0 for every
///     single-copy sector, hence exactly 1.0 at K = 1.
///  5. WEIGHT MIXING. The per-label final features land in one flat `__lf`
///     buffer, label-major, and the accumulation into the SOUT layout is a
///     single table-driven loop.
///
/// WEIGHT LAYOUT (the documented surface contract). LABEL-MAJOR: labels in
/// `MLSpec.polyLabels` order (slowest), then the matching W copies — SOUT
/// blocks of the label's (L, parity) in block order, multiplicity index
/// inner. That is `polyWeightDimViaLabels`'s summation order; it agrees with
/// `polyWeightDim` = `homDim(sym_spec(SPEC,K), SOUT)` on the COUNT (asserted
/// here on every synthesis) but not on the ORDER — homBlocks, which
/// derive_linear follows, is OUTPUT-block-major with the input multiplicity
/// innermost. The two coincide exactly when no (l, parity) has both input
/// multiplicity > 1 and output multiplicity > 1 and the matched blocks are in
/// the same relative order, which covers the K = 1 corpus pin; in general
/// derive_poly follows the label-major order, because that is the order the
/// label basis itself is defined in.
///
/// §6.9(iv), restated where a reader of the emitted code can see it: the
/// copy-split label basis is NOT GL(m)-channel-covariant. Label indices name
/// multiplicity SLOTS, never channel structure.
let private derivePolyDecl (name: string) (s: Spec) (k: int) (sOut: Spec) : Result<FunctionDecl, ElabError> =
    let labels = polyLabels s k |> List.toArray
    let copies = polyCopies s |> List.toArray
    let inStarts = blockStarts s |> List.toArray
    let outStarts = blockStarts sOut |> List.toArray
    let copyOff (c: int) = inStarts.[copies.[c].Block] + copies.[c].MultIdx * (2 * copies.[c].L + 1)
    // The W copies a label can be mixed into: SOUT blocks of the label's
    // (L, parity), block order, multiplicity index inner.
    let matched (lb: PolyLabel) =
        [ for bo in 0 .. sOut.Length - 1 do
            if sOut.[bo].L = lb.L && sOut.[bo].Parity = lb.Parity then
              for mo in 0 .. sOut.[bo].Mult - 1 -> outStarts.[bo] + mo * (2 * lb.L + 1) ]
    // Labels with no matching W copy carry no parameter and are not emitted
    // (Schur: their contribution to every admissible map is zero).
    let used = labels |> Array.filter (fun lb -> not (matched lb).IsEmpty)
    let mutable lfAcc = 0
    let lfOff =
        used
        |> Array.map (fun lb ->
            let o = lfAcc
            lfAcc <- lfAcc + 2 * lb.L + 1
            (lb.Index, o))
        |> Map.ofArray
    let lfDim = lfAcc
    let slots =
        [ for lb in used do
            for oo in matched lb -> (lfOff.[lb.Index], oo, 2 * lb.L + 1, sqrt (float lb.Multinomial)) ]
    let wDim = slots.Length
    // The §3.3b convention pin the two files cannot check against each other
    // (MLSpec stays dependency-free): a label's flat occurrence index must be
    // a direct index into symPowerTable's `Occurrences`.
    let occProblem =
        used |> Array.tryPick (fun lb ->
            lb.Uses |> List.tryPick (fun u ->
                if u.Degree < 2 then None
                else
                    let occs = (Blade.ML.SymPowerTables.symPowerTable u.Degree u.CopyL).Occurrences |> List.toArray
                    if u.Occ < 0 || u.Occ >= occs.Length then
                        Some (sprintf "T_{%d,%d} has %d occurrences but a label selects index %d"
                                  u.Degree u.CopyL occs.Length u.Occ)
                    elif occs.[u.Occ].L <> u.OccL || occs.[u.Occ].Copy <> u.OccCopy then
                        Some (sprintf "T_{%d,%d} occurrence %d is (L=%d, copy=%d) but the label basis says (L=%d, copy=%d)"
                                  u.Degree u.CopyL u.Occ occs.[u.Occ].L occs.[u.Occ].Copy u.OccL u.OccCopy)
                    else None))
    if occProblem.IsSome then
        Error (err5000 (sprintf "internal: derive_poly occurrence order drift — %s" occProblem.Value))
    elif wDim <> polyWeightDim s k sOut then
        Error (err5000 (sprintf "internal: derive_poly enumerated %d weight slots label by label but poly_weight_dim says %d (spec %A, K %d, out %A)"
                            wDim (polyWeightDim s k sOut) s k sOut))
    else
    let stmts = ResizeArray<Stmt> ()
    let mutable ctr = 0
    let fresh (p: string) = ctr <- ctr + 1; sprintf "__%s%d" p ctr
    stmts.Add (sLetMut "out" (zerosLit (totalDim sOut)))
    stmts.Add (sLetMut "__lf" (zerosLit (max lfDim 1)))
    // --- 1. monomial buffers, one per used (copy, degree), degree >= 2 -------
    let monKeys =
        used
        |> Array.collect (fun lb -> lb.Uses |> List.filter (fun u -> u.Degree >= 2) |> List.map (fun u -> (u.Copy, u.Degree)) |> List.toArray)
        |> Array.toList
        |> List.distinct
    let mutable monNames : Map<int * int, string> = Map.empty
    for (c, j) in monKeys do
        let d = 2 * copies.[c].L + 1
        let off = copyOff c
        let tuples = symMultisets d j
        let nm = fresh "mn"
        monNames <- Map.add (c, j) nm monNames
        stmts.Add (sLetMut nm (zerosLit tuples.Length))
        for a in 0 .. j - 1 do
            stmts.Add (sLet (sprintf "%s_i%d" nm a) (intArrLit (tuples |> List.map (fun t -> off + t.[a]))))
        stmts.Add (StmtForIn ("c", syn (ExprDotDot (iLit 0, iLit tuples.Length)),
                     [ sAccum (idx nm (v "c"))
                              ([ 0 .. j - 1 ]
                               |> List.map (fun a -> idx "x" (idx (sprintf "%s_i%d" nm a) (v "c")))
                               |> List.reduce mul) ]))
    // --- 2. the T_{j,l} matvec, sparse (row, cell, coef) --------------------
    let emitMatvec (dstName: string) (dstBase: int) (u: PolyCopyUse) =
        let tbl = Blade.ML.SymPowerTables.symPowerTable u.Degree u.CopyL
        let occ = tbl.Occurrences |> List.item u.Occ
        let monNm = Map.find (u.Copy, u.Degree) monNames
        let entries =
            [ for r in 0 .. occ.Rows.Length - 1 do
                for i in 0 .. tbl.Cells.Length - 1 do
                  if abs occ.Rows.[r].[i] > 1e-12 then yield (dstBase + r, i, occ.Rows.[r].[i]) ]
        let nm = fresh "tt"
        stmts.Add (sLet (nm + "_d") (intArrLit (entries |> List.map (fun (a, _, _) -> a))))
        stmts.Add (sLet (nm + "_s") (intArrLit (entries |> List.map (fun (_, b, _) -> b))))
        stmts.Add (sLet (nm + "_v") (floatArrLit (entries |> List.map (fun (_, _, cc) -> cc))))
        stmts.Add (StmtForIn ("t", syn (ExprDotDot (iLit 0, iLit entries.Length)),
                     [ sAccum (idx dstName (idx (nm + "_d") (v "t")))
                              (mul (idx (nm + "_v") (v "t")) (idx monNm (idx (nm + "_s") (v "t")))) ]))
    // j = 1: the occurrence feature IS the copy's slice, copied verbatim into
    // the label buffer (0.0 + x is exact, so the K = 1 kernel reads exactly
    // the components derive_linear reads).
    let emitCopySlice (dstBase: int) (srcOff: int) (d: int) =
        stmts.Add (StmtForIn ("c", syn (ExprDotDot (iLit 0, iLit d)),
                     [ sAccum (idx "__lf" (add (iLit dstBase) (v "c")))
                              (idx "x" (add (iLit srcOff) (v "c"))) ]))
    // --- 3. one pairwise CG contraction, unit weight ------------------------
    let emitCouple (dstName: string) (dstBase: int)
                   (aName: string, aBase: int, la: int) (bName: string, bBase: int, lb: int) (lMid: int) =
        let cg = Blade.ML.WignerTables.realCGSparse la lb lMid |> Array.toList
        let nm = fresh "cg"
        stmts.Add (sLet (nm + "_1") (intArrLit (cg |> List.map (fun e -> aBase + e.C1))))
        stmts.Add (sLet (nm + "_2") (intArrLit (cg |> List.map (fun e -> bBase + e.C2))))
        stmts.Add (sLet (nm + "_3") (intArrLit (cg |> List.map (fun e -> dstBase + e.C3))))
        stmts.Add (sLet (nm + "_v") (floatArrLit (cg |> List.map (fun e -> e.Coef))))
        stmts.Add (StmtForIn ("t", syn (ExprDotDot (iLit 0, iLit cg.Length)),
                     [ sAccum (idx dstName (idx (nm + "_3") (v "t")))
                              (mul (mul (idx (nm + "_v") (v "t")) (idx aName (idx (nm + "_1") (v "t"))))
                                   (idx bName (idx (nm + "_2") (v "t")))) ]))
    // Shared nodes: occurrence features keyed by (copy, degree, occ), chain
    // prefixes keyed by the whole (uses, chain) prefix. First appearance IS a
    // topological order — a child key extends its parent's.
    let mutable nodes : Map<string, string> = Map.empty
    let useKey (u: PolyCopyUse) = sprintf "%d.%d.%d" u.Copy u.Degree u.Occ
    let prefixKey (lb: PolyLabel) (m: int) =
        let us = lb.Uses |> List.truncate m |> List.map useKey |> String.concat ","
        let ch = lb.Chain |> List.truncate (m - 1) |> List.map string |> String.concat ","
        us + "|" + ch
    // Occurrence feature of one use: an x slice at j = 1, else its own buffer.
    let occFeature (u: PolyCopyUse) : string * int =
        if u.Degree = 1 then ("x", copyOff u.Copy)
        else
            let key = "o" + useKey u
            match Map.tryFind key nodes with
            | Some nm -> (nm, 0)
            | None ->
                let nm = fresh "of"
                stmts.Add (sLetMut nm (zerosLit (2 * u.OccL + 1)))
                emitMatvec nm 0 u
                nodes <- Map.add key nm nodes
                (nm, 0)
    for lb in used do
        let uses = lb.Uses |> List.toArray
        let dstBase = lfOff.[lb.Index]
        if uses.Length = 1 then
            // A single-copy sector has degree K, so its occurrence feature is
            // final and unique to this label: emit it straight into __lf.
            let u = uses.[0]
            if u.Degree = 1 then emitCopySlice dstBase (copyOff u.Copy) (2 * u.OccL + 1)
            else emitMatvec "__lf" dstBase u
        else
            let mutable accName = ""
            let mutable accBase = 0
            let mutable accL = uses.[0].OccL
            let a0, b0 = occFeature uses.[0]
            accName <- a0
            accBase <- b0
            for i in 1 .. uses.Length - 1 do
                let bName, bBase = occFeature uses.[i]
                let lMid = lb.Chain.[i - 1]
                if i = uses.Length - 1 then
                    // The last coupling has total degree K, so it is final and
                    // never a prefix of anything: it writes into __lf.
                    emitCouple "__lf" dstBase (accName, accBase, accL) (bName, bBase, uses.[i].OccL) lMid
                else
                    let key = prefixKey lb (i + 1)
                    match Map.tryFind key nodes with
                    | Some nm ->
                        accName <- nm
                        accBase <- 0
                    | None ->
                        let nm = fresh "ch"
                        stmts.Add (sLetMut nm (zerosLit (2 * lMid + 1)))
                        emitCouple nm 0 (accName, accBase, accL) (bName, bBase, uses.[i].OccL) lMid
                        nodes <- Map.add key nm nodes
                        accName <- nm
                        accBase <- 0
                accL <- lMid
    // --- 5. weight mixing, one table-driven loop ----------------------------
    stmts.Add (sLet "__w_fo" (intArrLit (slots |> List.map (fun (a, _, _, _) -> a))))
    stmts.Add (sLet "__w_oo" (intArrLit (slots |> List.map (fun (_, b, _, _) -> b))))
    stmts.Add (sLet "__w_d" (intArrLit (slots |> List.map (fun (_, _, c, _) -> c))))
    stmts.Add (sLet "__w_sc" (floatArrLit (slots |> List.map (fun (_, _, _, d) -> d))))
    stmts.Add (sFor "kk" 0 wDim
                 [ sLet "wv" (mul (idx "__w_sc" (v "kk")) (idx "w" (v "kk")))
                   StmtForIn ("c", syn (ExprDotDot (iLit 0, idx "__w_d" (v "kk"))),
                     [ sAccum (idx "out" (add (idx "__w_oo" (v "kk")) (v "c")))
                              (mul (v "wv") (idx "__lf" (add (idx "__w_fo" (v "kk")) (v "c")))) ]) ])
    Ok (mkFunc name [ ("x", tyIrrepsArr s); ("w", tyFloatArr wDim) ]
            (tyIrrepsArr sOut) (syn (ExprBlock (List.ofSeq stmts, Some (v "out")))))

/// linear for fixed (specIn, specOut): block-diagonal multiplicity mixing,
/// first-match input block, ml/Linear loop order (blocks -> muO -> muI -> c).
/// `rows` is MLSpec.linearBlocks output: one (inputBlockIdx, eo, ei) per
/// OUTPUT block, in output-block order (list position = output index).
/// linear over nRows row vectors stored flat (row-major): the per-block
/// multiplicity mixing (first-match input block, ml/Linear loop order:
/// blocks -> muO -> muI -> c) inside an outer row loop, all x/out indices
/// offset by the row base. nRows = 1 is the single-vector `linear`; the
/// batched `linear_rows` form exists so callers do not hand-write
/// row-extract/write-back copy loops around the single-vector op.
let private linearDecl (name: string) (specIn: Spec) (specOut: Spec)
                       (rows: (int * SpecEntry * SpecEntry) list) (nRows: int) : FunctionDecl =
    let dIn = totalDim specIn
    let dOut = totalDim specOut
    let sIn = blockStarts specIn
    let sOut = blockStarts specOut
    let wDim = rows |> List.sumBy (fun (_, eo, ei) -> eo.Mult * ei.Mult)
    let baseIn = mul (v "rr") (iLit dIn)
    let baseOut = mul (v "rr") (iLit dOut)
    let mutable wOff = 0
    let blockStmts =
        rows |> List.mapi (fun b (bi, eo, ei) ->
            let d = dim eo
            let thisOff = wOff
            wOff <- wOff + eo.Mult * ei.Mult
            sFor "mo" 0 eo.Mult
                [ sFor "mi" 0 ei.Mult
                    [ sLet "wv" (idx "w" (add (iLit thisOff) (add (mul (v "mo") (iLit ei.Mult)) (v "mi"))))
                      sFor "c" 0 d
                        [ sAccum (idx "out" (add baseOut (add (iLit sOut.[b]) (add (mul (v "mo") (iLit d)) (v "c")))))
                                 (mul (v "wv")
                                      (idx "x" (add baseIn (add (iLit sIn.[bi]) (add (mul (v "mi") (iLit d)) (v "c")))))) ] ] ])
    let body =
        syn (ExprBlock (
            [ sLetMut "out" (zerosLit (nRows * dOut))
              sFor "rr" 0 nRows blockStmts ],
            Some (v "out")))
    // nRows = 1: x/out ARE the irreps spaces — stamp them. nRows > 1: the
    // row-stacked buffers (extent nRows * total_dim) are not irreps spaces.
    let tyIn = if nRows = 1 then tyIrrepsArr specIn else tyFloatArr (nRows * dIn)
    let tyOut = if nRows = 1 then tyIrrepsArr specOut else tyFloatArr (nRows * dOut)
    mkFunc name [ ("w", tyFloatArr wDim); ("x", tyIn) ] tyOut body

/// gated for a fixed spec: block-0 scalars silu'd AND reused as gates for
/// higher-L blocks (gate for multiplicity mu is sigmoid(x[mu % numGates])),
/// mirroring ml/Activations.gated including the scalar double-duty (F2).
/// gated over nRows row vectors stored flat: block-0 scalars silu'd AND
/// reused as gates for higher-L blocks (gate for multiplicity mu is
/// sigmoid(x[row_base + mu % numGates]) — the F2 double-duty rule, per
/// row), inside an outer row loop. nRows = 1 is the single-vector `gated`.
let private gatedDecl (name: string) (sigmoidName: string) (spec: Spec) (nRows: int) : Result<FunctionDecl, ElabError> =
    if spec.IsEmpty then Error (err5000 "gated: empty spec")
    elif spec.Head.L <> 0 then Error (err5000 "gated: the first block must be scalars (L=0)")
    else
    let dTot = totalDim spec
    let starts = blockStarts spec
    let numGates = spec.Head.Mult
    let sigCall e = syn (ExprApp (v sigmoidName, [e]))
    let baseE = mul (v "rr") (iLit dTot)
    let rowStmts =
        [ for b in 0 .. spec.Length - 1 do
            let e = spec.[b]
            let d = dim e
            if e.L = 0 then
                yield sFor "mu" 0 e.Mult
                    [ sAssign (idx "out" (add baseE (add (iLit starts.[b]) (v "mu"))))
                              (mul (idx "x" (add baseE (add (iLit starts.[b]) (v "mu"))))
                                   (sigCall (idx "x" (add baseE (add (iLit starts.[b]) (v "mu")))))) ]
            else
                yield sFor "mu" 0 e.Mult
                    [ sLet "g" (sigCall (idx "x" (add baseE (syn (ExprBinOp (Elementwise, OpMod, v "mu", iLit numGates))))))
                      sFor "c" 0 d
                        [ sAssign (idx "out" (add baseE (add (iLit starts.[b]) (add (mul (v "mu") (iLit d)) (v "c")))))
                                  (mul (v "g")
                                       (idx "x" (add baseE (add (iLit starts.[b]) (add (mul (v "mu") (iLit d)) (v "c")))))) ] ] ]
    // nRows = 1: x/out ARE the irreps space (same spec in and out) — stamp.
    let tyVec = if nRows = 1 then tyIrrepsArr spec else tyFloatArr (nRows * dTot)
    Ok (mkFunc name [ ("x", tyVec) ] tyVec
            (syn (ExprBlock (
                [ sLetMut "out" (zerosLit (nRows * dTot))
                  sFor "rr" 0 nRows rowStmts ],
                Some (v "out")))))

/// derive_linear for fixed (specIn, specOut): the COMPLETE Schur basis of
/// Hom_G(V_in, V_out) — every (l, parity)-matched (input, output) block
/// pair mixes multiplicities, weight layout pair-major (MLSpec.homBlocks
/// order) mult_out x mult_in per pair, ACCUMULATING (+=) so duplicate
/// matches add; output blocks with no matching input stay exactly zero,
/// the unique equivariant completion. "You declare what the layer must
/// respect; the compiler writes the layer." Mirrors ml/Linear.homLinear
/// loop order (pairs -> mo -> mi -> c) for ulp agreement.
let private deriveLinearDecl (name: string) (specIn: Spec) (specOut: Spec) : FunctionDecl =
    let dOut = totalDim specOut
    let sIn = blockStarts specIn
    let sOut = blockStarts specOut
    let pairs = homBlocks specIn specOut
    let wDim = pairs |> List.sumBy (fun (_, _, eo, ei) -> eo.Mult * ei.Mult)
    let mutable wOff = 0
    let pairStmts =
        pairs |> List.map (fun (bi, bo, eo, ei) ->
            let d = dim eo
            let thisOff = wOff
            wOff <- wOff + eo.Mult * ei.Mult
            sFor "mo" 0 eo.Mult
                [ sFor "mi" 0 ei.Mult
                    [ sLet "wv" (idx "w" (add (iLit thisOff) (add (mul (v "mo") (iLit ei.Mult)) (v "mi"))))
                      sFor "c" 0 d
                        [ sAccum (idx "out" (add (iLit sOut.[bo]) (add (mul (v "mo") (iLit d)) (v "c"))))
                                 (mul (v "wv")
                                      (idx "x" (add (iLit sIn.[bi]) (add (mul (v "mi") (iLit d)) (v "c"))))) ] ] ])
    let body =
        syn (ExprBlock (
            [ yield sLetMut "out" (zerosLit dOut)
              yield! pairStmts ],
            Some (v "out")))
    mkFunc name [ ("w", tyFloatArr wDim); ("x", tyIrrepsArr specIn) ] (tyIrrepsArr specOut) body

// ============================================================================
// The Sₙ INDEX-ACTION surface (plan-transforms-as-types §3.6, §7 stage 5a-ii)
// ============================================================================
//
// `ml.derive_perm_linear(K, L, N, x, w)` is deriveLinearDecl's sibling for a
// FINITE group acting on INDICES rather than on irrep blocks: the complete
// basis of Hom_{Sₙ}(ℝ^{N^K}, ℝ^{N^L}), one weight per basis map. There is no
// spec, no character table and no Clebsch–Gordan anything, because for
// PERMUTATION modules the layer algebra is orbit combinatorics:
//
//     dim Hom_{Sₙ}(ℝ^{N^K}, ℝ^{N^L}) = #orbits of Sₙ on [N]^{K+L}
//                                    = #partitions of [K+L] into ≤ N blocks
//
// and the basis is the set of ORBIT (= coarsening) INDICATORS B_γ, one per
// partition γ of the m = K + L axis positions — inputs 0..K−1, outputs
// K..K+L−1, the position convention fixed by MLPermSpec's header and baked
// into the loop nests below. The count is the theorem BECAUSE the basis is
// emitted (§3.6's house rule); MLPermSpec.permPartitions supplies the
// partitions in the canonical weight order and certifies, on every call, that
// the emitted order is a linear extension of refinement.
//
// BUFFERS ARE FLAT ROW-MAJOR — `x` is one `Idx<N^K>` axis, the result one
// `Idx<N^L>` axis, exactly the `_rows` house precedent (linearDecl). No rank-K
// parameters, so nothing here needs new index machinery. L = 0 is the
// INVARIANT READOUT and returns `Array<Float like Idx<1>>`, a ONE-CELL
// buffer read as `y(0)` — N^0 = 1 keeps the emission uniform, and it is the
// shape every other ml op already gives a scalar result (derive_poly into a
// single-(l=0) SOUT, ml.scalars of a one-scalar spec: a 1-cell array, never a
// bare `Float`).

/// The flat-buffer cell cap of the Sₙ ops. ORTHOGONAL to
/// `PermSpec.checkPermSizing`: that gate decides which BASIS the surface admits
/// (§3.6's no-silent-fork rule, shared verbatim with the sizing builtins), this
/// one is about emitted code size — `out` is materialized as a literal array of
/// N^L zeros, so the extent appears verbatim in the generated source. Same
/// number and same reason as symLiftDecl's monomial cap.
let private permCellCap = 100000

/// N^e, saturating just past `permCellCap`. N is bounded only by the caller's
/// sanity range, so the honest product can overflow int64 — and the only
/// question this answers is "is the buffer over the cap?".
let private permPow (n: int) (e: int) : int64 =
    let mutable acc = 1L
    let mutable i = 0
    while i < e && acc <= int64 permCellCap do
        acc <- acc * int64 n
        i <- i + 1
    acc

/// THE EMITTED KERNEL, shared by derive_perm_linear and derive_perm_bias: ONE
/// LOOP NEST PER PARTITION, in `permPartitions` order (= the canonical weight
/// order, so weight slot g is partition g).
///
/// For partition γ, one `let`-free block variable per γ-BLOCK, each ranging
/// over 0..N−1 — b(γ) loops deep — and the two flat indices are read off the
/// position convention directly:
///
///     inIdx  = Σ_{i=0..K−1}    v_{γ(i)} · N^{K−1−i}         (input positions)
///     outIdx = Σ_{i=K..K+L−1}  v_{γ(i)} · N^{K+L−1−i}       (output positions)
///     out(outIdx) += w(g) * x(inIdx)         [bias: += b(g), no x factor]
///
/// That IS the orbit indicator B_γ contracted with x: the nest visits exactly
/// the tuples of [N]^m that are constant on every block of γ, which is the
/// support of B_γ, and adds each one's x-cell into its out-cell.
///
/// THE SUM / GATHER / BROADCAST CLASSIFICATION NEEDS NO CODE — it falls out of
/// which of the two indices a block variable appears in:
///   * a block of INPUT-only positions is a variable that occurs in inIdx but
///     not in outIdx, so its loop accumulates many x-cells into one out-cell:
///     a SUMMATION (K=L=1's `sum(x)`, K=2/L=0's trace and total sum);
///   * a MIXED block occurs in both, so it selects the same coordinate on each
///     side: a GATHER (the identity map, the transpose, the diagonal read);
///   * an OUTPUT-only block occurs only in outIdx, so the same accumulated
///     value is written across its whole range: a BROADCAST.
/// Nothing below branches on this. It is one uniform nest and the semantics
/// are whatever the index expressions say.
///
/// Accumulate-only (`+=` into a zero-initialized `out`, never a read-then-
/// rewrite), so grad() differentiates it through its normal inliner.
/// Code size is Σ_γ b(γ) loops — 37 at the Maron point (K=L=2), and the cap
/// K+L ≤ 6 bounds it by Σ_γ b(γ) over Bell(6) = 203 partitions.
let private permNestStmts (k: int) (l: int) (n: int) (coefName: string) (readsX: bool)
                          (parts: int[] list) : Stmt list =
    parts |> List.mapi (fun g rgs ->
        let nBlocks = Blade.ML.PermSpec.blockCount rgs
        let bv (j: int) = sprintf "__pv%d_%d" g j
        // Σ_{i=lo..hi−1} v_{γ(i)} · N^{hi−1−i}: the flat row-major index of one
        // axis run. The EMPTY run (K = 0 bias inputs, or L = 0 outputs) is the
        // single cell 0 — N^0 = 1.
        let flat (lo: int) (hi: int) =
            if lo >= hi then iLit 0
            else
                [ for i in lo .. hi - 1 ->
                    let coef = pown n (hi - 1 - i)
                    if coef = 1 then v (bv rgs.[i]) else mul (iLit coef) (v (bv rgs.[i])) ]
                |> List.reduce add
        let term =
            if readsX then mul (idx coefName (iLit g)) (idx "x" (flat 0 k))
            else idx coefName (iLit g)
        let body = [ sAccum (idx "out" (flat k (k + l))) term ]
        // b(γ) block loops, block 0 outermost. b(γ) = 0 only at m = 0, where
        // the "nest" is the bare body: out(0) += b(0), the constant map.
        List.foldBack (fun j inner -> [ sFor (bv j) 0 n inner ]) [ 0 .. nBlocks - 1 ] body
        |> List.exactlyOne)

/// derive_perm_linear for a fixed (K, L, N): the complete Sₙ-equivariant linear
/// layer ℝ^{N^K} -> ℝ^{N^L}, `ml.perm_weight_dim(K, L, N)` weights, weight slot
/// g = the g-th partition in `permPartitions (K+L) N` order. See the block
/// comment above for the kernel and the position convention.
let private derivePermLinearDecl (name: string) (k: int) (l: int) (n: int)
    : Result<FunctionDecl, ElabError> =
    let inCells = permPow n k
    let outCells = permPow n l
    if inCells > int64 permCellCap || outCells > int64 permCellCap then
        Error (err5000 (sprintf "derive_perm_linear: the flat node-power buffers of K = %d, L = %d, N = %d are N^K and N^L cells, and at least one is over the %d-cell limit — the emitted kernel materializes the output as a literal zero array of that extent. Lower N (the node-axis extent), or lower K / L"
                            k l n permCellCap))
    else
    let parts = Blade.ML.PermSpec.permPartitions (k + l) n
    let wDim = List.length parts
    // The count is the theorem because the basis is emitted: one nest per
    // weight slot, checked against the number the user sized their buffer by.
    if wDim <> Blade.ML.PermSpec.permWeightDim k l n then
        Error (err5000 (sprintf "internal: derive_perm_linear emitted %d loop nests but perm_weight_dim(%d, %d, %d) says %d"
                            wDim k l n (Blade.ML.PermSpec.permWeightDim k l n)))
    else
        let stmts = sLetMut "out" (zerosLit (int outCells)) :: permNestStmts k l n "w" true parts
        Ok (mkFunc name [ ("x", tyFloatArr (int inCells)); ("w", tyFloatArr wDim) ]
                (tyFloatArr (int outCells)) (syn (ExprBlock (stmts, Some (v "out")))))

/// derive_perm_bias for a fixed (L, N): the REP-INTRODUCTION form — the
/// complete space of Sₙ-invariant constants in ℝ^{N^L}, `ml.perm_bias_dim(L, N)`
/// of them. It is derive_perm_linear at K = 0 (partitions of the L output
/// positions alone, every block output-only, hence every nest a pure
/// broadcast), which is exactly why K = 0 is REFUSED by the linear op with a
/// pointer here rather than silently accepted.
let private derivePermBiasDecl (name: string) (l: int) (n: int)
    : Result<FunctionDecl, ElabError> =
    let outCells = permPow n l
    if outCells > int64 permCellCap then
        Error (err5000 (sprintf "derive_perm_bias: the flat node-power buffer of L = %d, N = %d is N^L cells, over the %d-cell limit — the emitted kernel materializes it as a literal zero array of that extent. Lower N (the node-axis extent), or lower L"
                            l n permCellCap))
    else
    let parts = Blade.ML.PermSpec.permPartitions l n
    let bDim = List.length parts
    if bDim <> Blade.ML.PermSpec.permBiasDim l n then
        Error (err5000 (sprintf "internal: derive_perm_bias emitted %d loop nests but perm_bias_dim(%d, %d) says %d"
                            bDim l n (Blade.ML.PermSpec.permBiasDim l n)))
    else
        let stmts = sLetMut "out" (zerosLit (int outCells)) :: permNestStmts 0 l n "b" false parts
        Ok (mkFunc name [ ("b", tyFloatArr bDim) ] (tyFloatArr (int outCells))
                (syn (ExprBlock (stmts, Some (v "out")))))

/// scalars for a fixed spec: the l=0 blocks' entries copied into a plain
/// Idx array (block order, multiplicity order) — an invariant-exit op, the
/// compile-time twin of ml/Activations.scalars (pure copies, ulp-trivial).
/// Emits ALL l=0 entries regardless of parity; the equiv judgment governs
/// which callers may treat them as invariants (O3 rejects (0, odd) specs).
let private scalarsDecl (name: string) (spec: Spec) : Result<FunctionDecl, ElabError> =
    let starts = blockStarts spec
    let offs =
        [ for b in 0 .. spec.Length - 1 do
            if spec.[b].L = 0 then
                yield! [ starts.[b] .. starts.[b] + spec.[b].Mult - 1 ] ]
    if offs.IsEmpty then Error (err5000 "scalars: the spec has no l=0 blocks")
    else
        let stmts =
            [ yield sLetMut "out" (zerosLit offs.Length)
              for k in 0 .. offs.Length - 1 do
                yield sAssign (idx "out" (iLit k)) (idx "x" (iLit offs.[k])) ]
        Ok (mkFunc name [ ("x", tyIrrepsArr spec) ] (tyFloatArr offs.Length)
                (syn (ExprBlock (stmts, Some (v "out")))))

/// norms for a fixed spec: per-(block, multiplicity) 2-norms in (block, mu)
/// order — mirrors ml/Activations.norms exactly (sum of squares in
/// ascending component order, then sqrt). O(3)-invariant for every parity.
/// Squares accumulate into a scratch buffer and `out` is written ONCE per
/// slot: grad() differentiates `x = x + e` accumulation but rejects the
/// general read-then-rewrite `out(k) = sqrt(out(k))`.
let private normsDecl (name: string) (spec: Spec) : FunctionDecl =
    let starts = blockStarts spec
    let slots =
        [ for b in 0 .. spec.Length - 1 do
            for mu in 0 .. spec.[b].Mult - 1 ->
              (starts.[b] + mu * dim spec.[b], dim spec.[b]) ]
    let stmts =
        [ yield sLetMut "sq" (zerosLit slots.Length)
          yield sLetMut "out" (zerosLit slots.Length)
          // ALL square-accumulation first, THEN all sqrt reads: grad's
          // read-then-rewrite analysis is per-variable, so no write to sq
          // may follow any read of sq.
          yield! slots
                 |> List.mapi (fun k (off, d) ->
                     sFor "c" 0 d
                         [ sAccum (idx "sq" (iLit k))
                                  (mul (idx "x" (add (iLit off) (v "c")))
                                       (idx "x" (add (iLit off) (v "c")))) ])
          for k in 0 .. slots.Length - 1 do
            yield sAssign (idx "out" (iLit k)) (syn (ExprApp (v "sqrt", [ idx "sq" (iLit k) ]))) ]
    mkFunc name [ ("x", tyIrrepsArr spec) ] (tyFloatArr slots.Length)
        (syn (ExprBlock (stmts, Some (v "out"))))

/// Cartesian<->irreps bridge ops (rank-2, 3-D, v1): a dense matvec over the
/// baked orthonormal closed-form table (Blade.ML.CartesianBridge — the
/// single source of truth, fit-certified against SphericalHarmonics by the
/// ml/ `dump-cartesian` oracle). Loop order mirrors the oracle's matvec
/// (i ascending, j ascending) for ulp agreement with the sgs corpus pins.
let private bridgeDecl (name: string) (table: float list) (n: int)
                       (pName: string) (tyIn: TypeExpr) (tyOut: TypeExpr) : FunctionDecl =
    let body =
        syn (ExprBlock (
            [ sLetMut "out" (zerosLit n)
              sLet "__b" (floatArrLit table)
              sFor "i" 0 n
                [ sFor "j" 0 n
                    [ sAccum (idx "out" (v "i"))
                             (mul (idx "__b" (add (mul (v "i") (iLit n)) (v "j")))
                                  (idx pName (v "j"))) ] ] ],
            Some (v "out")))
    mkFunc name [ (pName, tyIn) ] tyOut body

// ============================================================================
// Call-site recognition + program expansion
// ============================================================================

let private opNames =
    Set.ofList [ "y_to"; "tensor_product"; "linear"; "gated"; "linear_rows"; "gated_rows"
                 "scalars"; "norms"; "derive_linear"; "derive_tp"
                 "derive_sym_tp"; "derive_alt_tp"; "sym_lift"; "derive_poly"
                 "derive_perm_linear"; "derive_perm_bias"
                 "tensor_to_irreps"; "sym_to_irreps"; "irreps_to_sym" ]

/// Static sizing builtins that make up the rest of the ML surface (used in
/// `let static` positions). Registered in the static evaluator under mangled
/// internal names (Blade.ML.Statics.statName); a qualified `ml.total_dim(...)`
/// is normalized to that internal name here, so bare `total_dim(...)` no
/// longer resolves. Keep in sync with the registrations in MLStatics.install.
let private sizingNames =
    Set.ofList [ "sh_spec"; "total_dim"; "tp_weight_dim"; "linear_weight_dim"
                 "tp_spec"; "hom_dim"; "tp_full_weight_dim"
                 "sym_tp_weight_dim"; "alt_tp_weight_dim"
                 "sym_spec"; "alt_spec"; "poly_weight_dim"
                 "perm_weight_dim"; "perm_bias_dim"
                 "irreps_len"; "irreps_l"; "irreps_parity"; "irreps_mult"
                 "irreps_dim"; "irreps_offset" ]

type private ElabState = {
    mutable Counter: int
    /// (op, config fingerprint) -> generated function name
    mutable Made: Map<string, string>
    /// generated decls in creation order
    mutable Decls: FunctionDecl list
    mutable SigmoidName: string option
}

let private fingerprint (op: string) (parts: obj) : string =
    sprintf "%s|%A" op parts

/// Resolve a static-argument expression: must be a plain variable naming a
/// `let static` binding (or an inline int literal for lmax).
let private staticArg (statics: StaticEnv) (what: string) (e: Expr) : Result<StaticValue, ElabError> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (SVInt n)
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some sv -> Ok sv
        | None -> Error (err5000 (sprintf "%s: '%s' is not a `let static` binding (ML op configs must be static)" what name))
    | _ -> Error (err5000 (sprintf "%s: config argument must be a `let static` binding name or literal" what))

let private ensureSigmoid (st: ElabState) : string =
    match st.SigmoidName with
    | Some n -> n
    | None ->
        let n = "__ml_sigmoid"
        st.SigmoidName <- Some n
        st.Decls <- st.Decls @ [ sigmoidDecl n ]
        n

let private ensure (st: ElabState) (key: string) (make: string -> Result<FunctionDecl, ElabError>)
    : Result<string, ElabError> =
    match Map.tryFind key st.Made with
    | Some n -> Ok n
    | None ->
        st.Counter <- st.Counter + 1
        let n = sprintf "__ml_%d" st.Counter
        make n |> Result.map (fun decl ->
            st.Made <- Map.add key n st.Made
            st.Decls <- st.Decls @ [ decl ]
            n)

/// Shared elaboration for linear / linear_rows (nRows = 1 is the
/// single-vector form; the fingerprint includes nRows so each batch size
/// gets its own generated function).
/// `site` is the surface call expression: the generated application inherits
/// its span (as y_to/tensor_product/scalars already do) so diagnostics — and
/// editor tooling reading back the checked call — land on what the user wrote
/// rather than on the ambient per-declaration synthetic span.
let private elabLinear (st: ElabState) (statics: StaticEnv) (what: string) (site: Expr)
                       (sInE: Expr) (sOutE: Expr) (nRows: int) (wE: Expr) (xE: Expr)
    : Result<Expr, ElabError> =
    staticArg statics (what + " specIn") sInE |> Result.bind (fun svi ->
    staticArg statics (what + " specOut") sOutE |> Result.bind (fun svo ->
    specOfStatic (what + " specIn") svi |> Result.bind (fun si ->
    specOfStatic (what + " specOut") svo |> Result.bind (fun so ->
    (match linearBlocks si so with
     | Ok rows -> Ok rows
     | Error detail ->
        // Two Schur failure grades: no shared (l, parity) at all means the
        // whole hom-space is zero; a partial miss keeps the classic
        // all_irreps_present framing from linearBlocks.
        if homDim si so = 0 then
            Error (err4007 (sprintf "%s: no equivariant linear map exists from the input spec to the output spec — by Schur's lemma an equivariant linear map can only connect irreps of identical (l, parity), and these specs share none: every admissible map is zero" what))
        else
            Error (err4007 (detail + " — the only equivariant map into that block is zero (Schur's lemma); ml.derive_linear gives the zero-completed complete basis")))
    |> Result.bind (fun rows ->
        ensure st (fingerprint "linear" (box (si, so, nRows))) (fun n -> Ok (linearDecl n si so rows nRows))
        |> Result.map (fun n -> inheritSpan site (ExprApp (v n, [ wE; xE ]))))))))

/// Shared elaboration for gated / gated_rows. `site`: see elabLinear.
let private elabGated (st: ElabState) (statics: StaticEnv) (what: string) (site: Expr)
                      (specE: Expr) (nRows: int) (xE: Expr)
    : Result<Expr, ElabError> =
    staticArg statics (what + " spec") specE |> Result.bind (fun sv ->
    specOfStatic what sv |> Result.bind (fun spec ->
        let sig_ = ensureSigmoid st
        ensure st (fingerprint "gated" (box (spec, nRows))) (fun n -> gatedDecl n sig_ spec nRows)
        |> Result.map (fun n -> inheritSpan site (ExprApp (v n, [ xE ])))))

/// Shared elaboration for derive_linear's call and binding forms: resolve
/// specs, refuse the Schur-zero case (BL4007), synthesize (or reuse) the
/// complete-basis layer, return the generated name.
let private elabDeriveLinear (st: ElabState) (statics: StaticEnv) (sInE: Expr) (sOutE: Expr)
    : Result<string, ElabError> =
    staticArg statics "derive_linear specIn" sInE |> Result.bind (fun svi ->
    staticArg statics "derive_linear specOut" sOutE |> Result.bind (fun svo ->
    specOfStatic "derive_linear specIn" svi |> Result.bind (fun si ->
    specOfStatic "derive_linear specOut" svo |> Result.bind (fun so ->
        if homDim si so = 0 then
            Error (err4007 "derive_linear: no equivariant linear map exists from the input spec to the output spec — by Schur's lemma an equivariant linear map can only connect irreps of identical (l, parity), and these specs share none: every admissible map is zero")
        else
            ensure st (fingerprint "derive_linear" (box (si, so))) (fun n -> Ok (deriveLinearDecl n si so))))))

/// Shared elaboration for derive_tp: the output spec is DERIVED as the full
/// CG decomposition (tpSpec), so allValidOutputs holds by construction.
/// Shares the "tp" fingerprint: an explicit full-config tensor_product
/// dedups to the same generated function.
let private elabDeriveTp (st: ElabState) (statics: StaticEnv) (s1E: Expr) (s2E: Expr)
    : Result<string, ElabError> =
    staticArg statics "derive_tp spec1" s1E |> Result.bind (fun sv1 ->
    staticArg statics "derive_tp spec2" s2E |> Result.bind (fun sv2 ->
    specOfStatic "derive_tp spec1" sv1 |> Result.bind (fun s1 ->
    specOfStatic "derive_tp spec2" sv2 |> Result.bind (fun s2 ->
        let cfg = { Spec1 = s1; Spec2 = s2; SpecOut = tpSpec s1 s2 }
        ensure st (fingerprint "tp" (box cfg)) (fun n -> Ok (tpDecl n cfg))))))

/// Shared elaboration for derive_sym_tp / derive_alt_tp: the S₂-compacted
/// self-TP, one spec argument (both inputs and the derived output follow from
/// it). BL4007 when the requested component is EMPTY — then every map of that
/// exchange symmetry is zero and there is nothing to parameterize, the exact
/// analogue of derive_linear's Schur-zero refusal. Fingerprints are distinct
/// from "tp" (different weight arity), so the dense and compacted kernels for
/// the same spec coexist.
let private elabDeriveS2Tp (st: ElabState) (statics: StaticEnv) (comp: S2Component) (site: Expr)
                           (specE: Expr) (xE: Expr) (yE: Expr) (wE: Expr)
    : Result<Expr, ElabError> =
    let what, key, thisName, otherName =
        match comp with
        | S2Sym -> "derive_sym_tp", "sym_tp", "symmetric", "antisymmetric"
        | S2Alt -> "derive_alt_tp", "alt_tp", "antisymmetric", "symmetric"
    staticArg statics (what + " spec") specE |> Result.bind (fun sv ->
    specOfStatic (what + " spec") sv |> Result.bind (fun s ->
        let packedDim = match comp with S2Sym -> symTpWeightDim s | S2Alt -> altTpWeightDim s
        if packedDim = 0 then
            Error (err4007 (sprintf "%s: no nonzero exchange-%s equivariant bilinear map exists on this spec — every map in the %s component is zero for this spec, so the whole hom-space sits in the %s component (use ml.derive_%s_tp, or ml.derive_tp for the uncompacted parameterization)"
                                what thisName thisName otherName
                                (match comp with S2Sym -> "alt" | S2Alt -> "sym")))
        else
            ensure st (fingerprint key (box s)) (fun n -> deriveS2TpDecl n s comp)
            |> Result.map (fun n -> inheritSpan site (ExprApp (v n, [ xE; yE; wE ])))))

/// Shared elaboration for derive_poly: resolve (SPEC, K, SOUT), gate K to
/// 1..4 and the label count to the 100000-cell cap (symLiftDecl's precedent —
/// the label basis is indexed by the same Sym^K cells), refuse the Schur-zero
/// case with BL4007 in derive_linear's framing, then synthesize the
/// complete degree-K basis.
let private elabDerivePoly (st: ElabState) (statics: StaticEnv) (site: Expr)
                           (specE: Expr) (kE: Expr) (sOutE: Expr) (xE: Expr) (wE: Expr)
    : Result<Expr, ElabError> =
    staticArg statics "derive_poly SPEC" specE |> Result.bind (fun sv ->
    specOfStatic "derive_poly SPEC" sv |> Result.bind (fun s ->
    staticArg statics "derive_poly K" kE |> Result.bind (fun kv ->
    staticArg statics "derive_poly SOUT" sOutE |> Result.bind (fun sov ->
    specOfStatic "derive_poly SOUT" sov |> Result.bind (fun sOut ->
        match kv with
        | SVInt kk when kk >= 1L && kk <= 4L ->
            let k = int kk
            let n = totalDim s
            let cells = binomial (n + k - 1) k
            if cells > 100000L then
                Error (err5000 (sprintf "derive_poly: the degree-%d symmetric power of a dim-%d input has C(%d, %d) = %d cells, over the 100000-cell limit — the label basis is one vector per cell, so the emitted kernel would be unusable; lower K, or reduce the input spec first (ml.scalars / ml.linear). The channel-shared degree-K op that amortizes one basis over many multiplicity slots is future work"
                                    k n (n + k - 1) k cells))
            elif polyWeightDim s k sOut = 0 then
                Error (err4007 (sprintf "derive_poly: no equivariant degree-%d polynomial map exists from the input spec to the output spec — by Schur's lemma a degree-%d homogeneous equivariant map is a linear map out of Sym^%d of the input (ml.sym_spec(SPEC, %d)), which can only connect irreps of identical (l, parity), and those specs share none: every admissible map is zero"
                                    k k k k))
            else
                ensure st (fingerprint "derive_poly" (box (s, k, sOut))) (fun n2 -> derivePolyDecl n2 s k sOut)
                |> Result.map (fun n2 -> inheritSpan site (ExprApp (v n2, [ xE; wE ])))
        | SVInt kk ->
            Error (err5000 (sprintf "derive_poly: K must be a static int in 1..4 (got %d) — the symmetric-power surface is capped at degree 4 (plan-transforms-as-types §6.5)" kk))
        | _ -> Error (err5000 "derive_poly: K must be a static int"))))))

/// The sanity range of the Sₙ ops' static arguments, mirroring the sizing
/// builtins' guard (MLStatics) so a wild literal is a clean message rather than
/// an int overflow inside `permPartitions`. The REAL gates — the K+L cap and
/// N >= K+L — are `PermSpec.checkPermSizing`'s, shared verbatim with
/// `perm_weight_dim` / `perm_bias_dim` per §3.6's no-silent-fork rule.
let private permRangeOk (k: int64) (l: int64) (n: int64) = k <= 64L && l <= 64L && n <= 1000000L

/// Shared elaboration for derive_perm_linear: three static ints, then the
/// shared precondition, then the complete Sₙ layer. K = 0 is refused BY NAME
/// (it is derive_perm_bias, whose weight buffer is sized by a different
/// builtin), and that check runs BEFORE the sizing gate so the diagnostic names
/// the op the user wants rather than an N that is beside the point.
let private elabDerivePermLinear (st: ElabState) (statics: StaticEnv) (site: Expr)
                                 (kE: Expr) (lE: Expr) (nE: Expr) (xE: Expr) (wE: Expr)
    : Result<Expr, ElabError> =
    staticArg statics "derive_perm_linear K" kE |> Result.bind (fun kv ->
    staticArg statics "derive_perm_linear L" lE |> Result.bind (fun lv ->
    staticArg statics "derive_perm_linear N" nE |> Result.bind (fun nv ->
        match kv, lv, nv with
        | SVInt kk, SVInt ll, SVInt nn ->
            if kk < 1L then
                Error (err5000 (sprintf "derive_perm_linear: K must be a static int >= 1 (got %d) — K = 0 has no input axes, so the map is a CONSTANT and its complete basis is the rep-introduction form ml.derive_perm_bias(L, N, b), whose buffer is sized by ml.perm_bias_dim(L, N) rather than ml.perm_weight_dim(0, L, N)" kk))
            elif ll < 0L then
                Error (err5000 (sprintf "derive_perm_linear: L must be a static int >= 0 (got %d) — L = 0 is the invariant readout, a one-cell Idx<1> result" ll))
            elif not (permRangeOk kk ll nn) then
                Error (err5000 (sprintf "derive_perm_linear: K, L and N are static ints out of any sane range (got %d, %d, %d)" kk ll nn))
            else
                let k, l, n = int kk, int ll, int nn
                Blade.ML.PermSpec.checkPermSizing "derive_perm_linear" "K + L" (k + l) n
                |> Result.mapError err5000
                |> Result.bind (fun () ->
                    ensure st (fingerprint "perm_linear" (box (k, l, n))) (fun nm ->
                        derivePermLinearDecl nm k l n)
                    |> Result.map (fun nm -> inheritSpan site (ExprApp (v nm, [ xE; wE ]))))
        | _ -> Error (err5000 "derive_perm_linear: K, L and N must be static ints"))))

/// Shared elaboration for derive_perm_bias — derive_perm_linear at K = 0, and
/// the same shared precondition with `L` spelling m.
let private elabDerivePermBias (st: ElabState) (statics: StaticEnv) (site: Expr)
                               (lE: Expr) (nE: Expr) (bE: Expr)
    : Result<Expr, ElabError> =
    staticArg statics "derive_perm_bias L" lE |> Result.bind (fun lv ->
    staticArg statics "derive_perm_bias N" nE |> Result.bind (fun nv ->
        match lv, nv with
        | SVInt ll, SVInt nn ->
            if ll < 0L then
                Error (err5000 (sprintf "derive_perm_bias: L must be a static int >= 0 (got %d)" ll))
            elif not (permRangeOk 0L ll nn) then
                Error (err5000 (sprintf "derive_perm_bias: L and N are static ints out of any sane range (got %d, %d)" ll nn))
            else
                let l, n = int ll, int nn
                Blade.ML.PermSpec.checkPermSizing "derive_perm_bias" "L" l n
                |> Result.mapError err5000
                |> Result.bind (fun () ->
                    ensure st (fingerprint "perm_bias" (box (l, n))) (fun nm ->
                        derivePermBiasDecl nm l n)
                    |> Result.map (fun nm -> inheritSpan site (ExprApp (v nm, [ bE ]))))
        | _ -> Error (err5000 "derive_perm_bias: L and N must be static ints")))

/// Rewrite ML-op calls in an expression. Same walker shape as
/// Grad.rewriteExpr; the two passes stay separate because this one carries
/// elaboration state and runs first.
let rec private rewriteExpr (st: ElabState) (statics: StaticEnv) (aliases: Set<string>) (opsEnabled: bool) (e: Expr)
    : Result<Expr, ElabError> =
    let r = rewriteExpr st statics aliases opsEnabled
    let rList es =
        es |> List.fold (fun acc x ->
            acc |> Result.bind (fun xs -> r x |> Result.map (fun x' -> xs @ [x'])))
            (Ok [])
    let rOpt (o: Expr option) =
        match o with
        | None -> Ok None
        | Some x -> r x |> Result.map Some
    match e.Kind with
    // Qualified ML sizing builtin: `alias.total_dim(...)` -> the mangled
    // internal registry name so the static evaluator folds it (and a bare
    // `total_dim(...)` no longer resolves anywhere). Normalized in every pass
    // — sizing must resolve before op configs fold.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, name) }, args)
        when Set.contains alias aliases && Set.contains name sizingNames ->
        rList args |> Result.map (fun args' -> inheritSpan e (ExprApp (v (Blade.ML.Statics.statName name), args')))
    // Qualified ML op: `alias.y_to(...)` -> generated specialized function.
    // Bare `y_to(...)` is no longer recognized: the ML surface is reachable
    // only through an `import ml` alias.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) }, args)
        when opsEnabled && Set.contains alias aliases && Set.contains op opNames ->
        rList args |> Result.bind (fun args' ->
            match op, args' with
            | "y_to", (lmaxE :: rest) when rest.Length = 3 ->
                staticArg statics "y_to lmax" lmaxE |> Result.bind (fun sv ->
                    match sv with
                    | SVInt lmax ->
                        ensure st (fingerprint "y_to" (box lmax)) (fun n -> yToDecl n (int lmax))
                        |> Result.map (fun n -> inheritSpan e (ExprApp (v n, rest)))
                    | _ -> Error (err5000 "y_to: lmax must be a static int"))
            | "y_to", _ -> Error (err5000 "y_to: expected y_to(LMAX, x, y, z)")
            | "tensor_product", [ cfgE; xE; yE; wE ] ->
                staticArg statics "tensor_product cfg" cfgE |> Result.bind (fun sv ->
                cfgOfStatic "tensor_product" sv |> Result.bind (fun cfg ->
                    if not (allValidOutputs cfg) then
                        let reachable = tpPaths cfg |> List.map (fun (_, _, bo) -> bo) |> Set.ofList
                        let missing =
                            cfg.SpecOut
                            |> List.mapi (fun i entry -> (i, entry))
                            |> List.filter (fun (i, _) -> not (Set.contains i reachable))
                        let names =
                            missing
                            |> List.map (fun (_, entry) ->
                                sprintf "(l=%d, %s)" entry.L (if entry.Parity = 0 then "even" else "odd"))
                            |> String.concat ", "
                        let plural = missing.Length > 1
                        Error (err4007 (sprintf "tensor_product: output irrep%s %s %s unreachable from the inputs — no Clebsch-Gordan path satisfies the triangle inequality |l1-l2| <= l <= l1+l2 with parity p1*p2, so by Schur's lemma the only equivariant map into %s is zero"
                                            (if plural then "s" else "") names
                                            (if plural then "are" else "is")
                                            (if plural then "those blocks" else "that block")))
                    else
                        ensure st (fingerprint "tp" (box cfg)) (fun n -> Ok (tpDecl n cfg))
                        |> Result.map (fun n -> inheritSpan e (ExprApp (v n, [ xE; yE; wE ])))))
            | "tensor_product", _ -> Error (err5000 "tensor_product: expected tensor_product(CFG, x, y, w)")
            | "linear", [ sInE; sOutE; wE; xE ] ->
                elabLinear st statics "linear" e sInE sOutE 1 wE xE
            | "linear", _ -> Error (err5000 "linear: expected linear(SPEC_IN, SPEC_OUT, w, x)")
            | "linear_rows", [ sInE; sOutE; nE; wE; xE ] ->
                staticArg statics "linear_rows nrows" nE |> Result.bind (fun sv ->
                    match sv with
                    | SVInt n when n >= 1L ->
                        elabLinear st statics "linear_rows" e sInE sOutE (int n) wE xE
                    | _ -> Error (err5000 "linear_rows: NROWS must be a static int >= 1"))
            | "linear_rows", _ -> Error (err5000 "linear_rows: expected linear_rows(SPEC_IN, SPEC_OUT, NROWS, w, x)")
            | "gated", [ specE; xE ] ->
                elabGated st statics "gated" e specE 1 xE
            | "gated", _ -> Error (err5000 "gated: expected gated(SPEC, x)")
            | "gated_rows", [ specE; nE; xE ] ->
                staticArg statics "gated_rows nrows" nE |> Result.bind (fun sv ->
                    match sv with
                    | SVInt n when n >= 1L ->
                        elabGated st statics "gated_rows" e specE (int n) xE
                    | _ -> Error (err5000 "gated_rows: NROWS must be a static int >= 1"))
            | "gated_rows", _ -> Error (err5000 "gated_rows: expected gated_rows(SPEC, NROWS, x)")
            | "scalars", [ specE; xE ] ->
                staticArg statics "scalars spec" specE |> Result.bind (fun sv ->
                specOfStatic "scalars" sv |> Result.bind (fun spec ->
                    ensure st (fingerprint "scalars" (box spec)) (fun n -> scalarsDecl n spec)
                    |> Result.map (fun n -> inheritSpan e (ExprApp (v n, [ xE ])))))
            | "scalars", _ -> Error (err5000 "scalars: expected scalars(SPEC, x)")
            | "norms", [ specE; xE ] ->
                staticArg statics "norms spec" specE |> Result.bind (fun sv ->
                specOfStatic "norms" sv |> Result.bind (fun spec ->
                    ensure st (fingerprint "norms" (box spec)) (fun n -> Ok (normsDecl n spec))
                    |> Result.map (fun n -> inheritSpan e (ExprApp (v n, [ xE ])))))
            | "norms", _ -> Error (err5000 "norms: expected norms(SPEC, x)")
            | "derive_linear", [ sInE; sOutE; wE; xE ] ->
                elabDeriveLinear st statics sInE sOutE
                |> Result.map (fun n -> inheritSpan e (ExprApp (v n, [ wE; xE ])))
            | "derive_linear", [ sInE; sOutE ] ->
                // Binding form: the derived layer as a function VALUE —
                // `let layer = ml.derive_linear(SIN, SOUT)` then
                // `layer(w, x)` through the normal FuncElem path (wrong-spec
                // calls hit the IrrepsIdx strictness seam, BL4003).
                elabDeriveLinear st statics sInE sOutE
                |> Result.map (fun n -> inheritSpan e (ExprVar n))
            | "derive_linear", _ -> Error (err5000 "derive_linear: expected derive_linear(SPEC_IN, SPEC_OUT[, w, x])")
            | "derive_tp", [ s1E; s2E; xE; yE; wE ] ->
                elabDeriveTp st statics s1E s2E
                |> Result.map (fun n -> inheritSpan e (ExprApp (v n, [ xE; yE; wE ])))
            | "derive_tp", [ s1E; s2E ] ->
                elabDeriveTp st statics s1E s2E
                |> Result.map (fun n -> inheritSpan e (ExprVar n))
            | "derive_tp", _ -> Error (err5000 "derive_tp: expected derive_tp(SPEC1, SPEC2[, x, y, w])")
            // S₂-compacted self-TP: same arithmetic as derive_tp(SPEC, SPEC,
            // x, y, ·) with a smaller weight buffer (sym/alt_tp_weight_dim).
            | "derive_sym_tp", [ specE; xE; yE; wE ] ->
                elabDeriveS2Tp st statics S2Sym e specE xE yE wE
            | "derive_sym_tp", _ -> Error (err5000 "derive_sym_tp: expected derive_sym_tp(SPEC, x, y, w) with w of extent ml.sym_tp_weight_dim(SPEC)")
            | "derive_alt_tp", [ specE; xE; yE; wE ] ->
                elabDeriveS2Tp st statics S2Alt e specE xE yE wE
            | "derive_alt_tp", _ -> Error (err5000 "derive_alt_tp: expected derive_alt_tp(SPEC, x, y, w) with w of extent ml.alt_tp_weight_dim(SPEC)")
            // The degree-K equivariant polynomial layer: the uniform Sym^K
            // label basis (plan §3.3b), one weight per basis map. K = 1 is
            // derive_linear; K = 2 is derive_sym_tp's hom-space in the uniform
            // convention rather than in the kept-path one.
            | "derive_poly", [ specE; kE; sOutE; xE; wE ] ->
                elabDerivePoly st statics e specE kE sOutE xE wE
            | "derive_poly", _ -> Error (err5000 "derive_poly: expected derive_poly(SPEC, K, SOUT, x, w) with x of type Array<Float like IrrepsIdx<SPEC>> and w of extent ml.poly_weight_dim(SPEC, K, SOUT)")
            // The Sₙ index-action layer: the complete Hom_{Sₙ}(ℝ^{N^K}, ℝ^{N^L})
            // basis over FLAT ROW-MAJOR node-power buffers, one loop nest per
            // partition of the K+L axis positions (plan §3.6, stage 5a-ii).
            | "derive_perm_linear", [ kE; lE; nE; xE; wE ] ->
                elabDerivePermLinear st statics e kE lE nE xE wE
            | "derive_perm_linear", _ -> Error (err5000 "derive_perm_linear: expected derive_perm_linear(K, L, N, x, w) with K, L, N static ints, x of extent N^K (flat row-major over the K node axes) and w of extent ml.perm_weight_dim(K, L, N); the result has extent N^L (Idx<1> at L = 0, the invariant readout)")
            | "derive_perm_bias", [ lE; nE; bE ] ->
                elabDerivePermBias st statics e lE nE bE
            | "derive_perm_bias", _ -> Error (err5000 "derive_perm_bias: expected derive_perm_bias(L, N, b) with L, N static ints and b of extent ml.perm_bias_dim(L, N); the result has extent N^L")
            // The monomial lift: the value-side half of the symmetric-power
            // bridge. Its type-side twin is ml.sym_spec(SPEC, K), and
            // ml.derive_linear(ml.sym_spec(SPEC, K), SPEC_OUT) composed with
            // it is degree-K equivariant synthesis (plan §3.1).
            | "sym_lift", [ specE; kE; xE ] ->
                staticArg statics "sym_lift spec" specE |> Result.bind (fun sv ->
                specOfStatic "sym_lift spec" sv |> Result.bind (fun s ->
                staticArg statics "sym_lift K" kE |> Result.bind (fun kv ->
                    match kv with
                    | SVInt k when k >= 1L && k <= 4L ->
                        ensure st (fingerprint "sym_lift" (box (s, int k))) (fun n -> symLiftDecl n s (int k))
                        |> Result.map (fun n -> inheritSpan e (ExprApp (v n, [ xE ])))
                    | SVInt k ->
                        Error (err5000 (sprintf "sym_lift: K must be a static int in 1..4 (got %d) — the symmetric-power surface is capped at degree 4 (plan-transforms-as-types §6.5)" k))
                    | _ -> Error (err5000 "sym_lift: K must be a static int"))))
            | "sym_lift", _ -> Error (err5000 "sym_lift: expected sym_lift(SPEC, K, x) with x of type Array<Float like IrrepsIdx<SPEC>>; the result is a plain Idx<C(total_dim(SPEC)+K-1, K)> monomial vector")
            | "tensor_to_irreps", [ gE ] ->
                ensure st (fingerprint "tensor_to_irreps" (box ())) (fun n ->
                    Ok (bridgeDecl n Blade.ML.CartesianBridge.bridge9Flat 9 "g"
                            (tyFloatArr 9) (tyIrrepsArr Blade.ML.CartesianBridge.gradSpec)))
                |> Result.map (fun n -> inheritSpan e (ExprApp (v n, [ gE ])))
            | "tensor_to_irreps", _ -> Error (err5000 "tensor_to_irreps: expected tensor_to_irreps(g) with g the flat row-major 3x3 Cartesian tensor (Idx<9>)")
            | "sym_to_irreps", [ sE ] ->
                ensure st (fingerprint "sym_to_irreps" (box ())) (fun n ->
                    Ok (bridgeDecl n Blade.ML.CartesianBridge.symToIrrFlat 6 "s"
                            (tyFloatArr 6) (tyIrrepsArr Blade.ML.CartesianBridge.tauSpec)))
                |> Result.map (fun n -> inheritSpan e (ExprApp (v n, [ sE ])))
            | "sym_to_irreps", _ -> Error (err5000 "sym_to_irreps: expected sym_to_irreps(s) with s the packed symmetric tensor [s00, s01, s02, s11, s12, s22] (Idx<6>)")
            | "irreps_to_sym", [ tE ] ->
                ensure st (fingerprint "irreps_to_sym" (box ())) (fun n ->
                    Ok (bridgeDecl n Blade.ML.CartesianBridge.irrToSymFlat 6 "t"
                            (tyIrrepsArr Blade.ML.CartesianBridge.tauSpec) (tyFloatArr 6)))
                |> Result.map (fun n -> inheritSpan e (ExprApp (v n, [ tE ])))
            | "irreps_to_sym", _ -> Error (err5000 "irreps_to_sym: expected irreps_to_sym(t) with t transforming as IrrepsIdx<[(0,0,1), (2,0,1)]>")
            | _ -> Error (err5000 (sprintf "%s: unrecognized ML-op call shape" op)))
    | ExprKind.ExprLit _ | ExprKind.ExprVar _ -> Ok e
    | ExprKind.ExprApp (f, args) ->
        r f |> Result.bind (fun f' -> rList args |> Result.map (fun args' -> inheritSpan e (ExprApp (f', args'))))
    | ExprKind.ExprBinOp (m, op, l, rr) ->
        r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> inheritSpan e (ExprBinOp (m, op, l', r'))))
    | ExprKind.ExprUnaryOp (op, inner) -> r inner |> Result.map (fun i -> inheritSpan e (ExprUnaryOp (op, i)))
    | ExprKind.ExprTyped (inner, t) -> r inner |> Result.map (fun i -> inheritSpan e (ExprTyped (i, t)))
    | ExprKind.ExprAssign (l, rr) ->
        r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> inheritSpan e (ExprAssign (l', r'))))
    | ExprKind.ExprTuple es -> rList es |> Result.map (fun es' -> inheritSpan e (ExprTuple es'))
    | ExprKind.ExprArrayLit es -> rList es |> Result.map (fun es' -> inheritSpan e (ExprArrayLit es'))
    | ExprKind.ExprDotDot (l, h) ->
        r l |> Result.bind (fun l' -> r h |> Result.map (fun h' -> inheritSpan e (ExprDotDot (l', h'))))
    | ExprKind.ExprIf (c, t, f) ->
        r c |> Result.bind (fun c' ->
        r t |> Result.bind (fun t' ->
        r f |> Result.map (fun f' -> inheritSpan e (ExprIf (c', t', f')))))
    | ExprKind.ExprLet (binding, body) ->
        r binding.Value |> Result.bind (fun v' ->
        r body |> Result.map (fun b' -> inheritSpan e (ExprLet ({ binding with Value = v' }, b'))))
    | ExprKind.ExprBlock (stmts, finalE) ->
        let rec rStmt (s: Stmt) : Result<Stmt, ElabError> =
            match s with
            | StmtSpanned (inner, sp) -> rStmt inner |> Result.map (fun i -> StmtSpanned (i, sp))
            | StmtLet binding -> r binding.Value |> Result.map (fun v' -> StmtLet { binding with Value = v' })
            | StmtExpr e2 -> r e2 |> Result.map StmtExpr
            | StmtAssign (l, op, rr) ->
                r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> StmtAssign (l', op, r')))
            | StmtForIn (var, range, body) ->
                r range |> Result.bind (fun range' ->
                    body |> List.fold (fun acc bs ->
                        acc |> Result.bind (fun ss -> rStmt bs |> Result.map (fun s' -> ss @ [s'])))
                        (Ok [])
                    |> Result.map (fun body' -> StmtForIn (var, range', body')))
        stmts |> List.fold (fun acc s ->
            acc |> Result.bind (fun ss -> rStmt s |> Result.map (fun s' -> ss @ [s'])))
            (Ok [])
        |> Result.bind (fun stmts' ->
            match finalE with
            | Some fe -> r fe |> Result.map (fun fe' -> inheritSpan e (ExprBlock (stmts', Some fe')))
            | None -> Ok (inheritSpan e (ExprBlock (stmts', None))))
    | ExprKind.ExprLambda (ps, w, body) -> r body |> Result.map (fun b -> inheritSpan e (ExprLambda (ps, w, b)))
    | ExprKind.ExprMatch (scrut, cases) ->
        r scrut |> Result.bind (fun s' ->
            cases |> List.fold (fun acc c ->
                acc |> Result.bind (fun cs ->
                    rOpt c.Guard |> Result.bind (fun g' ->
                    r c.Body |> Result.map (fun b -> cs @ [{ c with Guard = g'; Body = b }]))))
                (Ok [])
            |> Result.map (fun cs' -> inheritSpan e (ExprMatch (s', cs'))))
    // Recursive array (`let rec q: T = match q with ...`): the seed and
    // inductive slices are ordinary expressions and may contain qualified
    // ops. Without this arm they fell through untouched, and since this pass
    // DELETES the import that would bind the alias, the call reached the
    // checker as an unbound variable.
    | ExprKind.ExprRecArray def ->
        rOpt (def.SeedArm |> Option.map snd) |> Result.bind (fun seedE ->
        r def.SliceExpr |> Result.map (fun slice' ->
            let seed' = Option.map2 (fun (sv, _) se -> (sv, se)) def.SeedArm seedE
            inheritSpan e (ExprRecArray { def with SeedArm = seed'; SliceExpr = slice' })))
    // The rest of the expression algebra. Every constructor holding a
    // sub-expression is walked, and the catch-all wildcard is deliberately
    // GONE: an unhandled case is an FS0025 incomplete-match warning at build
    // time rather than a qualified call silently surviving unrewritten.
    | ExprKind.ExprCompute inner -> r inner |> Result.map (fun i -> inheritSpan e (ExprCompute i))
    | ExprKind.ExprRead inner -> r inner |> Result.map (fun i -> inheritSpan e (ExprRead i))
    | ExprKind.ExprPure inner -> r inner |> Result.map (fun i -> inheritSpan e (ExprPure i))
    | ExprKind.ExprStatic inner -> r inner |> Result.map (fun i -> inheritSpan e (ExprStatic i))
    | ExprKind.ExprRank inner -> r inner |> Result.map (fun i -> inheritSpan e (ExprRank i))
    | ExprKind.ExprExtents inner -> r inner |> Result.map (fun i -> inheritSpan e (ExprExtents i))
    | ExprKind.ExprUnique inner -> r inner |> Result.map (fun i -> inheritSpan e (ExprUnique i))
    | ExprKind.ExprObjectFor k -> r k |> Result.map (fun k' -> inheritSpan e (ExprObjectFor k'))
    | ExprKind.ExprReynolds (k, anti) -> r k |> Result.map (fun k' -> inheritSpan e (ExprReynolds (k', anti)))
    | ExprKind.ExprField (obj, fld) -> r obj |> Result.map (fun o -> inheritSpan e (ExprField (o, fld)))
    | ExprKind.ExprPartialApp (op, inner, isLeft) -> r inner |> Result.map (fun i -> inheritSpan e (ExprPartialApp (op, i, isLeft)))
    | ExprKind.ExprTranspose (a, d1, d2) -> r a |> Result.map (fun a' -> inheritSpan e (ExprTranspose (a', d1, d2)))
    | ExprKind.ExprDecompact (a, d) -> r a |> Result.map (fun a' -> inheritSpan e (ExprDecompact (a', d)))
    | ExprKind.ExprBlocked (t, inner) -> r inner |> Result.map (fun i -> inheritSpan e (ExprBlocked (t, i)))
    | ExprKind.ExprHalo (t, offs) -> r offs |> Result.map (fun o -> inheritSpan e (ExprHalo (t, o)))
    | ExprKind.ExprMethodFor es -> rList es |> Result.map (fun es' -> inheritSpan e (ExprMethodFor es'))
    | ExprKind.ExprZip es -> rList es |> Result.map (fun es' -> inheritSpan e (ExprZip es'))
    | ExprKind.ExprStack es -> rList es |> Result.map (fun es' -> inheritSpan e (ExprStack es'))
    | ExprKind.ExprSequence es -> rList es |> Result.map (fun es' -> inheritSpan e (ExprSequence es'))
    | ExprKind.ExprGroupKeys es -> rList es |> Result.map (fun es' -> inheritSpan e (ExprGroupKeys es'))
    | ExprKind.ExprAlign (es, spec) -> rList es |> Result.map (fun es' -> inheritSpan e (ExprAlign (es', spec)))
    | ExprKind.ExprJoin (es, d) -> rList es |> Result.map (fun es' -> inheritSpan e (ExprJoin (es', d)))
    | ExprKind.ExprTupleIndex (t, i) ->
        r t |> Result.bind (fun t' -> r i |> Result.map (fun i' -> inheritSpan e (ExprTupleIndex (t', i'))))
    | ExprKind.ExprGuard (c, b) ->
        r c |> Result.bind (fun c' -> r b |> Result.map (fun b' -> inheritSpan e (ExprGuard (c', b'))))
    | ExprKind.ExprReplicate (c, b) ->
        r c |> Result.bind (fun c' -> r b |> Result.map (fun b' -> inheritSpan e (ExprReplicate (c', b'))))
    | ExprKind.ExprMask (a, p) ->
        r a |> Result.bind (fun a' -> r p |> Result.map (fun p' -> inheritSpan e (ExprMask (a', p'))))
    | ExprKind.ExprCompound (d, m) ->
        r d |> Result.bind (fun d' -> r m |> Result.map (fun m' -> inheritSpan e (ExprCompound (d', m'))))
    | ExprKind.ExprIntersect (a, b) ->
        r a |> Result.bind (fun a' -> r b |> Result.map (fun b' -> inheritSpan e (ExprIntersect (a', b'))))
    | ExprKind.ExprUnion (a, b) ->
        r a |> Result.bind (fun a' -> r b |> Result.map (fun b' -> inheritSpan e (ExprUnion (a', b'))))
    | ExprKind.ExprContains (a, v) ->
        r a |> Result.bind (fun a' -> r v |> Result.map (fun v' -> inheritSpan e (ExprContains (a', v'))))
    | ExprKind.ExprGroupBy (v, g) ->
        r v |> Result.bind (fun v' -> r g |> Result.map (fun g' -> inheritSpan e (ExprGroupBy (v', g'))))
    | ExprKind.ExprSort (a, k) ->
        r a |> Result.bind (fun a' -> r k |> Result.map (fun k' -> inheritSpan e (ExprSort (a', k'))))
    | ExprKind.ExprGram (l, rr) ->
        r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> inheritSpan e (ExprGram (l', r'))))
    | ExprKind.ExprReduce (a, k, init) ->
        r a |> Result.bind (fun a' ->
        r k |> Result.bind (fun k' ->
        rOpt init |> Result.map (fun init' -> inheritSpan e (ExprReduce (a', k', init')))))
    | ExprKind.ExprStruct (nm, fields, spread) ->
        fields |> List.fold (fun acc (fn, fe) ->
            acc |> Result.bind (fun fs -> r fe |> Result.map (fun fe' -> fs @ [(fn, fe')])))
            (Ok [])
        |> Result.bind (fun fields' ->
        rOpt spread |> Result.map (fun spread' -> inheritSpan e (ExprStruct (nm, fields', spread'))))
    | ExprKind.ExprFor (src, cs, kern) ->
        (match src with
         | ForArrays (arrs, inClause) ->
             rList arrs |> Result.bind (fun arrs' ->
             rOpt inClause |> Result.map (fun ic' -> ForArrays (arrs', ic')))
         | ForKernel k -> r k |> Result.map ForKernel)
        |> Result.bind (fun src' ->
        rOpt kern |> Result.map (fun kern' -> inheritSpan e (ExprFor (src', cs, kern'))))
    // Leaves: no sub-expressions. Index/type arguments (range<I>, reverse<I>)
    // carry TypeExprs, not Exprs, and are never rewritten.
    | ExprKind.ExprWildcard | ExprKind.ExprQualified _ | ExprKind.ExprRange _
    | ExprKind.ExprReverse _ | ExprKind.ExprArity _ | ExprKind.ExprNth
    | ExprKind.ExprZero | ExprKind.ExprSection _ -> Ok e

/// `import ml [as _]` — the module this layer owns.
let private isMlImport (d: Located<Decl>) =
    match d.Value with
    | DeclImport (["ml"], _) -> true
    | _ -> false

/// Aliases bound to `ml` in this decl list. Errors on a selective
/// `from ml import ...`, which would reintroduce the global names the module
/// system is meant to remove.
let private mlAliasesOf (decls: Located<Decl> list) : Result<Set<string>, ElabError> =
    decls |> List.fold (fun acc d ->
        acc |> Result.bind (fun set ->
            match d.Value with
            | DeclImport (["ml"], ImportQualified aliasOpt) ->
                Ok (Set.add (aliasOpt |> Option.defaultValue "ml") set)
            | DeclImport (["ml"], ImportSelective _) ->
                Error (err5000 "`ml` supports only `import ml [as <alias>]`; a selective `from ml import ...` would reintroduce global names")
            | _ -> Ok set))
        (Ok Set.empty)

/// Module-expansion failure: either a decl-span coded message (the ambient
/// synthSpan boundary, existing behavior) or pre-spanned diagnostics from
/// the equiv judgment (expression-precise).
type private ExpandFailure = Choice<ElabError, Blade.Diagnostics.Diagnostic list>

let private expandModule (decls: Located<Decl> list) : Result<Located<Decl> list, ExpandFailure> =
    (mlAliasesOf decls |> Result.mapError Choice1Of2) |> Result.bind (fun aliases ->
    // Import-gated: with no `import ml`, this pass is a no-op — bare op names
    // are left unbound (a normal type error) and never rewritten.
    if Set.isEmpty aliases then Ok decls
    else
        let declsNoImport = decls |> List.filter (not << isMlImport)
        // Normalize `<alias>.equiv` where-conjuncts to the registered
        // internal name (mold: PplElaborate.stripConjunctName), so the
        // judgment and the checker's registry dispatch see one spelling.
        let normalizeConjunct (cname: string) =
            match cname.Split('.') with
            | [| a; "equiv" |] when Set.contains a aliases -> "__ml_equiv"
            | [| a; "galilean" |] when Set.contains a aliases -> "__ml_galilean"
            | _ -> cname
        let declsNoImport =
            declsNoImport |> List.map (fun d ->
                match d.Value with
                | DeclFunction fd ->
                    let w' =
                        fd.WhereClause
                        |> Option.map (fun w ->
                            { w with Custom = w.Custom |> List.map (fun (n, args) -> (normalizeConjunct n, args)) })
                    { d with Value = DeclFunction { fd with WhereClause = w' } }
                | _ -> d)
        let st = { Counter = 0; Made = Map.empty; Decls = []; SigmoidName = None }
        let emptyStatics : StaticEnv =
            { Values = Map.empty; Functions = Map.empty
              CalledFunctions = ref Set.empty; ProviderRoots = Map.empty
              Structs = Map.empty }
        // Run rewriteExpr over every expression-bearing decl.
        let mapDecls (statics: StaticEnv) (opsEnabled: bool) (ds: Located<Decl> list) =
            ds |> List.fold (fun acc d ->
                acc |> Result.bind (fun out ->
                    // Stamp the user decl's span so every syn-built node
                    // attributes to this declaration's source line.
                    Blade.Ast.synthSpan <- d.Span
                    let mapped =
                        match d.Value with
                        | DeclFunction fd ->
                            rewriteExpr st statics aliases opsEnabled fd.Body
                            |> Result.map (fun b -> DeclFunction { fd with Body = b })
                        | DeclLet binding ->
                            rewriteExpr st statics aliases opsEnabled binding.Value
                            |> Result.map (fun v' -> DeclLet { binding with Value = v' })
                        | DeclStatic binding ->
                            rewriteExpr st statics aliases opsEnabled binding.Value
                            |> Result.map (fun v' -> DeclStatic { binding with Value = v' })
                        | other -> Ok other
                    mapped |> Result.map (fun value -> out @ [{ d with Value = value }])))
                (Ok [])
        // Pass 1: normalize qualified sizing builtins (`ml.total_dim(...)`) to
        // their internal names so the static evaluator can fold them. Ops are
        // left untouched (opsEnabled = false); statics are unused here.
        (mapDecls emptyStatics false declsNoImport |> Result.mapError Choice1Of2) |> Result.bind (fun decls1 ->
        // Fold failures are the type-checker's to report; elaboration only
        // needs the successfully folded environment.
        match Blade.StaticEval.resolveStatics decls1 with
        | Error e -> Error (Choice1Of2 (err5000 (sprintf "ML elaboration: static resolution failed: %s" e)))
        | Ok (statics, _) ->
            // The equiv judgment runs HERE — the seam between passes: `ml.*`
            // op calls are still surface-visible, and specs resolve through
            // the identical static machinery pass 2 uses, so judgment and
            // synthesis cannot disagree about a spec.
            let judged =
                match Blade.ML.Equiv.buildCertTable statics decls1 with
                | Error d -> Error [ d ]
                | Ok certs when Map.isEmpty certs -> Ok ()
                | Ok certs ->
                    let diags =
                        decls1
                        |> List.collect (fun d ->
                            match d.Value with
                            | DeclFunction fd ->
                                match Map.tryFind fd.Name certs with
                                | Some cert ->
                                    Blade.ML.Equiv.judgeFunction cert.Group certs statics aliases fd
                                | None -> []
                            | _ -> [])
                    if diags.IsEmpty then Ok () else Error diags
            match judged with
            | Error ds -> Error (Choice2Of2 ds)
            | Ok () ->
            // The galilean judgment runs at the SAME seam (surface `sgs.*`
            // former calls are still visible — sgs elaborates after ml).
            // It is independent of the equiv judgment: a function may carry
            // both conjuncts, each judged in its own domain.
            let judgedGal =
                match Blade.ML.Galilean.buildCertTable decls1 with
                | Error d -> Error [ d ]
                | Ok gcerts when Map.isEmpty gcerts -> Ok ()
                | Ok gcerts ->
                    let sgsAliases = Blade.ML.Galilean.sgsAliasesOf decls1
                    let diags =
                        decls1
                        |> List.collect (fun d ->
                            match d.Value with
                            | DeclFunction fd ->
                                Blade.ML.Galilean.judgeFunction gcerts aliases sgsAliases fd
                            | _ -> [])
                    if diags.IsEmpty then Ok () else Error diags
            match judgedGal with
            | Error ds -> Error (Choice2Of2 ds)
            | Ok () ->
            // Pass 2: rewrite qualified ops into generated specialized functions.
            (mapDecls statics true decls1 |> Result.mapError Choice1Of2) |> Result.map (fun decls2 ->
                if st.Decls.IsEmpty then decls2
                else
                    // Generated functions are self-contained (literal tables,
                    // no captures): splice them at the FRONT so every use site
                    // (top-level lets included) sees them defined.
                    let span = { StartLine = 0; StartCol = 0; EndLine = 0; EndCol = 0; File = None }
                    let gen = st.Decls |> List.map (fun fd -> { Value = DeclFunction fd; Span = span })
                    gen @ decls2)))

/// Entry point: elaborate ML ops across a program (before Grad expansion).
/// Also installs the ML sizing builtins into the static evaluator —
/// expand runs unconditionally as the first pipeline stage, so this makes
/// sh_spec / total_dim / tp_weight_dim / linear_weight_dim visible to
/// every resolveStatics pass (the elaborator's own, checkModule's, and
/// Lowering's Phase 0) without the core evaluator knowing about ML.
let private expandStr (program: Program) : Result<Program, ExpandFailure> =
    Blade.ML.Statics.install ()
    Blade.ML.Equiv.register ()
    Blade.ML.Galilean.register ()
    program.Modules
    |> List.fold (fun acc m ->
        acc |> Result.bind (fun ms ->
            expandModule m.Decls |> Result.map (fun ds -> ms @ [{ m with Decls = ds }])))
        (Ok [])
    |> Result.map (fun ms -> { program with Modules = ms })

/// Boundary: coded internals -> diagnostics. For ElabError failures the
/// span is the ambient synthSpan -- stamped per-decl by mapDecls, so a
/// mid-elaboration failure points at the offending declaration; the Code
/// (BL5000 generic / BL4007 Schur) is rendered faithfully. Equiv-judgment
/// failures carry their own expression-precise diagnostics (BL4008).
let expand (program: Program) : Result<Program, Blade.Diagnostics.Diagnostic list> =
    Blade.Ast.synthSpan <- Blade.Ast.noSpan
    expandStr program
    |> Result.mapError (fun failure ->
        match failure with
        | Choice1Of2 err ->
            [ Blade.Diagnostics.mkError err.Code (Blade.Diagnostics.Codes.phaseOfCode err.Code) Blade.Ast.synthSpan err.Msg ]
        | Choice2Of2 ds -> ds)
