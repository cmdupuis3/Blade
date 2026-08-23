/// The `where ml.galilean(u, ...)` discipline: a function carrying the
/// (normalized) `__ml_galilean` conjunct is PROVED invariant under a
/// constant Galilean boost of the listed parameters -- its body may combine
/// boost-variant values only through boost-cancelling operations. The
/// judgment runs at the same pass-1/pass-2 seam as the equiv judgment (sgs
/// elaborates AFTER ml, so surface `sgs.*` former calls are still visible
/// and carry axiomatic rules).
///
/// The certificate is a conditional theorem: IF the listed parameters are
/// velocity-typed (each shifts u -> u + U0 under the SAME constant boost U0,
/// componentwise) and all other parameters are held fixed, THEN the result
/// is unchanged. Scope is honest: a CONSTANT boost only -- rotations are
/// ml.equiv's theorem; no time-dependent boosts, no coordinate shift
/// x -> x - U0 t. Units are deliberately NOT the seed (a velocity DIFFERENCE
/// still carries the velocity unit but is boost-invariant); the conjunct
/// names the boost-variant parameters instead.
///
/// Abstract value domain (BVar tracks U0-coefficient EXACTLY 1):
///   BVar    -- boosted quantity + boost-independent part; indexing a BVar
///             array yields BVar elements (per-component, index-stable --
///             unlike the equiv judgment, where raw indexing is forbidden);
///   BInv    -- boost-invariant (differences of BVars, gradients, stresses,
///             constants, everything else);
///   BOpaque -- unclassifiable; rejected where it matters.
///
/// v1 rules: BVar - BVar -> BInv (the central rule); BVar +/- BInv -> BVar;
/// everything that scales or nonlinearizes a BVar is BL4009 (v2: rational
/// U0-coefficient tracking would admit static-weight averages and
/// BVar-returning steppers). Certified functions must RETURN BInv in v1.
///
/// Axiomatic op rules (surface-visible at this seam):
///   sgs.grad(U, DX)      : U any -> BInv   (difference weights sum to 0)
///   sgs.stress(U, W)     : U any -> BInv   (a central comoment)
///   sgs.box_filter(U, W) : U st  -> st     (weights sum to 1: preserves)
///   every ml.* op        : all-BInv args -> BInv; a BVar arg is a reject
/// Violations are BL4009 at the offending expression's span.
module Blade.ML.Galilean

open Blade.Ast
// The walker shell (freeVars / patternVars / bindPatternVars / judgeEach /
// conjunctsOf) is shared verbatim with MLEquiv and MLPerm.
open Blade.ML.CertShell

type BoostStatus =
    | BVar
    | BInv
    | BOpaque

type GalSig = {
    /// Parameter name -> status, in declaration order.
    Params: (string * BoostStatus) list
}

// Helpers

let private bl4009 (span: Span) (msg: string) : Blade.Diagnostics.Diagnostic =
    Blade.Diagnostics.mkError "BL4009" (Blade.Diagnostics.Codes.phaseOfCode "BL4009") span msg

let private statusStr (st: BoostStatus) : string =
    match st with
    | BVar -> "boost-variant (shifts with the frame velocity)"
    | BInv -> "boost-invariant"
    | BOpaque -> "unclassifiable"

// Certified-signature table

/// Pre-scan: the conjunct's args NAME the boost-variant parameters; every
/// other parameter is boost-invariant. Errors are BL4009 at the decl.
let buildCertTable (decls: Located<Decl> list)
    : Result<Map<string, GalSig>, Blade.Diagnostics.Diagnostic> =
    decls
    |> List.fold (fun acc d ->
        acc |> Result.bind (fun table ->
            match d.Value with
            | DeclFunction fd ->
                let conjs = conjunctsOf "__ml_galilean" fd
                let fail msg = Error (bl4009 d.Span msg)
                match conjs with
                | [] -> Ok table
                | _ :: _ :: _ -> fail $"function '{fd.Name}': duplicate galilean constraints -- declare one, listing every boost-variant parameter"
                | [ (_, args) ] ->
                    if args.IsEmpty then
                        fail $"function '{fd.Name}': galilean(...) must name at least one boost-variant (velocity) parameter"
                    else
                        let pNames = fd.Params |> List.map _.Name
                        match args |> List.tryFind (fun a -> not (List.contains a pNames)) with
                        | Some bad ->
                            fail $"function '{fd.Name}': galilean argument '{bad}' is not a parameter of this function"
                        | None ->
                            let ps =
                                fd.Params
                                |> List.map (fun p ->
                                    (p.Name, if List.contains p.Name args then BVar else BInv))
                            Ok (Map.add fd.Name { Params = ps } table)
            | _ -> Ok table))
        (Ok Map.empty)

/// The galilean-certificate suggestion side-channel -- BL4014's channel,
/// mirroring `Equiv.CertSuggestions` (BL4011). AsyncLocal, like the others.
module GalCertSuggestions =
    let private slot = new System.Threading.AsyncLocal<(string * Blade.Ast.Span) list>()
    let reset () = slot.Value <- []
    let add (msg: string) (span: Blade.Ast.Span) = slot.Value <- (msg, span) :: slot.Value
    let get () : (string * Blade.Ast.Span) list =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value

/// Aliases bound to `sgs`; without `import sgs` the axioms are absent.
let sgsAliasesOf (decls: Located<Decl> list) : Set<string> =
    decls |> List.fold (fun set d ->
        match d.Value with
        | DeclImport (["sgs"], ImportQualified aliasOpt) ->
            Set.add (aliasOpt |> Option.defaultValue "sgs") set
        | _ -> set) Set.empty

// The judgment

type private Ctx = {
    FuncName: string
    /// ml-module aliases (every ml.* op is BInv-only).
    MlAliases: Set<string>
    /// sgs-module aliases (grad/stress/box_filter axioms).
    SgsAliases: Set<string>
    Certs: Map<string, GalSig>
}

let rec private judge (ctx: Ctx) (env: Map<string, BoostStatus>) (e: Expr)
    : Result<BoostStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4009 e.Span $"function '{ctx.FuncName}': {msg}")
    let j = judge ctx env
    match e.Kind with
    | ExprKind.ExprLit _ -> Ok BInv
    | ExprKind.ExprArrayLit es | ExprKind.ExprTuple es ->
        // mixing statuses inside one aggregate loses the U0-coefficient.
        es
        |> judgeEach j
        |> Result.bind (fun sts ->
            match sts with
            | [] -> Ok BInv
            | s :: rest when rest |> List.forall ((=) s) -> Ok s
            | _ -> reject "an aggregate mixing boost-variant and boost-invariant elements has no single U0-coefficient -- split it")
    | ExprKind.ExprVar n ->
        match Map.tryFind n env with
        | Some st -> Ok st
        | None -> Ok BInv // globals/constants/builtins: held fixed by the conditional theorem
    | ExprKind.ExprDotDot _ -> Ok BInv
    | ExprKind.ExprTyped (inner, _) -> j inner
    | ExprKind.ExprUnaryOp (_, inner) ->
        j inner |> Result.bind (fun si ->
            match si with
            | BInv -> Ok BInv
            | BVar -> reject "negating a boost-variant value flips its U0-coefficient to -1 -- difference two velocities instead"
            | BOpaque -> Ok BOpaque)
    // Former application dispatches BEFORE the general binop arm (OpApply is
    // a BinOp constructor).
    | ExprKind.ExprBinOp (_, OpApply, loop, kern) ->
        judgeFormerApply ctx env e loop kern
    | ExprKind.ExprBinOp (_, op, l, r) ->
        j l |> Result.bind (fun sl ->
        j r |> Result.bind (fun sr ->
            match sl, sr, op with
            | BVar, BVar, OpSub -> Ok BInv // THE rule: the boost cancels
            | BVar, BVar, OpAdd ->
                reject "adding two boost-variant values doubles the U0-coefficient -- subtract them (differences are boost-invariant) or average through sgs.box_filter"
            | BVar, BVar, _ ->
                reject "this operator is nonlinear in the frame velocity -- take differences first"
            | BVar, BInv, (OpAdd | OpSub) -> Ok BVar
            | BInv, BVar, OpAdd -> Ok BVar
            | BInv, BVar, OpSub ->
                reject "invariant - velocity carries U0-coefficient -1 -- write (velocity - invariant) or difference two velocities"
            | BVar, _, (OpMul | OpDiv) | _, BVar, (OpMul | OpDiv) ->
                reject "scaling a boost-variant value scales the U0-coefficient -- only differences of velocities (and the sgs formers) are boost-invariant"
            | BVar, _, _ | _, BVar, _ ->
                reject "this operator does not preserve the U0-coefficient of a boost-variant value"
            | BInv, BInv, _ -> Ok BInv
            | BOpaque, _, _ | _, BOpaque, _ -> Ok BOpaque))
    | ExprKind.ExprIf (c, t, f) ->
        j c |> Result.bind (fun sc ->
            match sc with
            | BInv ->
                j t |> Result.bind (fun st ->
                j f |> Result.bind (fun sf ->
                    if st = sf then Ok st
                    else reject $"if branches disagree: then-branch is {statusStr st}, else-branch is {statusStr sf}"))
            | _ -> reject "an if condition inside a galilean-certified body must be boost-invariant -- branching on a frame-dependent value makes the result frame-dependent")
    | ExprKind.ExprMatch (scrut, cases) ->
        j scrut |> Result.bind (fun ss ->
            match ss with
            | BInv ->
                cases
                |> judgeEach (fun c -> judge ctx (bindPatternVars BInv env c.Pattern) c.Body)
                |> Result.bind (fun sts ->
                    match sts with
                    | [] -> Ok BInv
                    | s :: rest when rest |> List.forall ((=) s) -> Ok s
                    | _ -> reject "match arms disagree on their boost status")
            | _ -> reject "a match scrutinee inside a galilean-certified body must be boost-invariant")
    | ExprKind.ExprLet (binding, body) ->
        j binding.Value |> Result.bind (fun sv ->
            match binding.Pattern.Kind, sv with
            | PatternKind.PatVar n, _ -> judge ctx (Map.add n sv env) body
            | _, BInv -> judge ctx (bindPatternVars BInv env binding.Pattern) body
            | _, _ -> reject "cannot destructure a boost-variant value -- bind it whole")
    | ExprKind.ExprLambda (ps, _, lamBody) ->
        let captured = freeVars (Set.ofList (ps |> List.map _.Name)) lamBody
        let varCapture =
            captured |> Set.toList |> List.tryFind (fun n ->
                match Map.tryFind n env with Some BVar -> true | _ -> false)
        match varCapture with
        | Some n -> reject $"lambda captures boost-variant '{n}' -- factor velocity work into galilean-certified functions instead"
        | None -> Ok BInv
    | ExprKind.ExprAssign (l, r) ->
        judgeAssign ctx env e.Span l r |> Result.map (fun () -> BInv)
    | ExprKind.ExprBlock (stmts, finalE) ->
        judgeStmts ctx env stmts
        |> Result.bind (fun env' ->
            match finalE with
            | Some fe -> judge ctx env' fe
            | None -> Ok BInv)
    | ExprKind.ExprApp (f, args) -> judgeApp ctx env e f args
    | ExprKind.ExprField (_, _) -> Ok BOpaque
    // Virtual arrays enumerate indices: frame-independent by nature.
    | ExprKind.ExprRange _ | ExprKind.ExprReverse _ | ExprKind.ExprHalo _ -> Ok BInv
    // compute is a scheduling boundary, not a value transform.
    | ExprKind.ExprCompute x -> judge ctx env x
    // A fold over boost-variant values SCALES the frame shift, so it rejects.
    | ExprKind.ExprReduce (src, _, init, _) ->
        judge ctx env src |> Result.bind (fun ss ->
            (match init with
             | Some i -> judge ctx env i
             | None -> Ok BInv) |> Result.bind (fun si ->
                match ss, si with
                | BInv, BInv -> Ok BInv
                | BVar, _ | _, BVar ->
                    Error (bl4009 e.Span $"function '{ctx.FuncName}': reduce over a boost-variant value scales the frame shift -- fold only boost-invariant combinations (differences, sgs.grad, sgs.stress)")
                | _ -> Ok BOpaque))
    | _ -> Ok BOpaque

/// `loop <@> kernel` under the galilean judgment: source statuses bind to the
/// kernel's leading params (one per source array; remaining params are the
/// co-iteration ordinals, boost-invariant). Non-former applies stay opaque.
and private judgeFormerApply (ctx: Ctx) (env: Map<string, BoostStatus>) (e: Expr) (loop: Expr) (kern: Expr)
    : Result<BoostStatus, Blade.Diagnostics.Diagnostic> =
    let srcsOf (l: Expr) =
        match l.Kind with
        | ExprKind.ExprMethodFor arrays -> Some arrays
        | ExprKind.ExprFor (ForArrays (arrays, _), _, _) -> Some arrays
        | _ -> None
    match srcsOf loop, kern.Kind with
    | Some arrays, ExprKind.ExprLambda (ps, _, body) ->
        arrays
        |> judgeEach (judge ctx env)
        |> Result.bind (fun srcSts ->
            let env' =
                ps |> List.mapi (fun i p -> (i, p.Name))
                   |> List.fold (fun m (i, name) ->
                        let st = if i < srcSts.Length then srcSts.[i] else BInv
                        Map.add name st m) env
            judge ctx env' body)
    | _ -> Ok BOpaque

and private judgeStmts (ctx: Ctx) (env: Map<string, BoostStatus>) (stmts: Stmt list)
    : Result<Map<string, BoostStatus>, Blade.Diagnostics.Diagnostic> =
    stmts
    |> List.fold (fun acc s ->
        acc |> Result.bind (fun env ->
            match unwrapStmt s with
            | StmtLet binding ->
                judge ctx env binding.Value |> Result.bind (fun sv ->
                    match binding.Pattern.Kind, sv with
                    | PatternKind.PatVar n, _ -> Ok (Map.add n sv env)
                    | _, BInv -> Ok (bindPatternVars BInv env binding.Pattern)
                    | _, _ ->
                        Error (bl4009 binding.Value.Span $"function '{ctx.FuncName}': cannot destructure a boost-variant value -- bind it whole"))
            | StmtExpr e2 -> judge ctx env e2 |> Result.map (fun _ -> env)
            | StmtAssign (l, _, r) -> judgeAssign ctx env l.Span l r |> Result.map (fun () -> env)
            | StmtForIn (v, range, body) ->
                judge ctx env range |> Result.bind (fun sr ->
                    match sr with
                    | BVar ->
                        Error (bl4009 range.Span $"function '{ctx.FuncName}': cannot iterate a boost-variant value as a range")
                    | _ ->
                        judgeStmts ctx (Map.add v BInv env) body |> Result.map (fun _ -> env))
            | _ -> Ok env))
        (Ok env)

/// Whole-variable writes must preserve boost status; element writes must
/// match the container's (else it breaks the container's uniform shift).
and private judgeAssign (ctx: Ctx) (env: Map<string, BoostStatus>) (span: Span) (l: Expr) (r: Expr)
    : Result<unit, Blade.Diagnostics.Diagnostic> =
    let fail msg = Error (bl4009 span $"function '{ctx.FuncName}': {msg}")
    judge ctx env r |> Result.bind (fun sr ->
        match l.Kind with
        | ExprKind.ExprVar n ->
            match Map.tryFind n env with
            | Some st when st = sr -> Ok ()
            | Some st -> fail $"assignment changes '{n}' from {statusStr st} to {statusStr sr} -- a mut binding must keep one boost status"
            | None -> Ok ()
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, idxArgs) ->
            // indices must be boost-invariant; the value matches the container.
            idxArgs
            |> List.fold (fun acc a ->
                acc |> Result.bind (fun () ->
                    judge ctx env a |> Result.bind (fun si ->
                        if si = BInv then Ok ()
                        else fail "array indices must be boost-invariant")))
                (Ok ())
            |> Result.bind (fun () ->
                match Map.tryFind n env, sr with
                | Some BVar, BVar -> Ok ()
                | Some BVar, _ -> fail $"writing a non-boost-variant element into boost-variant '{n}' breaks its uniform frame shift"
                | _, BVar -> fail "cannot store a boost-variant value into a boost-invariant container"
                | _, _ -> Ok ())
        | _ ->
            match sr with
            | BVar -> fail "unsupported assignment target for a boost-variant value"
            | _ -> Ok ())

and private judgeApp (ctx: Ctx) (env: Map<string, BoostStatus>) (e: Expr) (f: Expr) (args: Expr list)
    : Result<BoostStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4009 e.Span $"function '{ctx.FuncName}': {msg}")
    let judgeAll args = judgeEach (judge ctx env) args
    match f.Kind with
    // sgs formers: the axiomatic rules
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) when Set.contains alias ctx.SgsAliases ->
        (match op, args with
         | "grad", [ uE; dxE ] ->
             judge ctx env uE |> Result.bind (fun su ->
             judge ctx env dxE |> Result.bind (fun sdx ->
                 if sdx <> BInv then reject "grad: the grid spacing must be boost-invariant"
                 elif su = BOpaque then reject "grad: cannot classify the field argument"
                 else Ok BInv)) // difference weights sum to 0: kills the boost
         | "stress", [ uE; wE ] ->
             judge ctx env uE |> Result.bind (fun su ->
             judge ctx env wE |> Result.bind (fun sw ->
                 if sw <> BInv then reject "stress: the tile width must be boost-invariant"
                 elif su = BOpaque then reject "stress: cannot classify the field argument"
                 else Ok BInv)) // a central comoment: boost-invariant by construction
         | "box_filter", [ uE; wE ] ->
             judge ctx env uE |> Result.bind (fun su ->
             judge ctx env wE |> Result.bind (fun sw ->
                 if sw <> BInv then reject "box_filter: the tile width must be boost-invariant"
                 else Ok su)) // weights sum to 1: preserves the boost status
         | _ ->
             judgeAll args |> Result.bind (fun sts ->
                 if sts |> List.forall ((=) BInv) then Ok BInv
                 else reject $"sgs.{op} carries no galilean axiom for boost-variant arguments"))
    // ml ops: invariants in, invariants out
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) when Set.contains alias ctx.MlAliases ->
        judgeAll args |> Result.bind (fun sts ->
            if sts |> List.forall ((=) BInv) then Ok BInv
            else reject $"ml.{op} does not accept boost-variant arguments -- velocities enter models only through boost-invariant combinations (differences, sgs.grad, sgs.stress)")
    // named callees
    | ExprKind.ExprVar fn ->
        match Map.tryFind fn ctx.Certs with
        | Some cert ->
            if List.length args <> List.length cert.Params then
                reject $"call to '{fn}': expected {List.length cert.Params} arguments"
            else
                (List.zip cert.Params args)
                |> List.fold (fun acc ((pName, pSt), argE) ->
                    acc |> Result.bind (fun () ->
                        judge ctx env argE |> Result.bind (fun sa ->
                            if sa = pSt then Ok ()
                            else
                                Error (bl4009 argE.Span
                                           ($"function '{ctx.FuncName}': '{fn}' parameter '{pName}' is {(statusStr pSt)}, but the argument is {(statusStr sa)}")))))
                    (Ok ())
                |> Result.map (fun () -> BInv) // v1: certified functions return boost-invariant values
        | None ->
            match Map.tryFind fn env with
            | Some BVar ->
                // indexing is index-stable: elements are themselves BVar.
                judgeAll args |> Result.bind (fun sts ->
                    if sts |> List.forall ((=) BInv) then Ok BVar
                    else reject $"indexing into boost-variant '{fn}' requires boost-invariant indices")
            | Some BInv | None ->
                // a BVar argument escapes the discipline.
                judgeAll args |> Result.bind (fun sts ->
                    match sts |> List.tryFindIndex (fun s -> s <> BInv) with
                    | None -> Ok BInv
                    | Some i ->
                        // TWO different failures share this arm: a BVar
                        // argument is a real ESCAPE (the callee carries no
                        // theorem about it), while BOpaque is the judgment's
                        // OWN blind spot -- it must not be reported as variant.
                        match sts.[i] with
                        | BVar ->
                            Error (bl4009 args.[i].Span
                                       $"function '{ctx.FuncName}': a boost-variant value escapes to '{fn}', which carries no galilean certificate -- certify it with `where ml.galilean(...)` or pass only boost-invariant combinations (differences, sgs.grad, sgs.stress)")
                        | _ ->
                            Error (bl4009 args.[i].Span
                                       $"function '{ctx.FuncName}': an argument to '{fn}' cannot be classified as boost-invariant or boost-variant -- a galilean-certified body admits a call only when every argument is provably boost-invariant, so rewrite this argument in terms the judgment reads (parameters, differences, sgs.grad, sgs.stress)"))
            | Some BOpaque -> reject $"cannot classify the callee '{fn}'"
    | _ ->
        judgeAll args |> Result.bind (fun sts ->
            judge ctx env f |> Result.bind (fun sf ->
                if sf = BInv && sts |> List.forall ((=) BInv) then Ok BInv
                else reject "cannot classify this call inside a galilean-certified body"))

/// Judge one certified function: seed the env from the conjunct, require the
/// body boost-invariant (v2: velocity-returning steppers).
let judgeFunction (certs: Map<string, GalSig>) (mlAliases: Set<string>) (sgsAliases: Set<string>)
                  (fd: FunctionDecl)
    : Blade.Diagnostics.Diagnostic list =
    match Map.tryFind fd.Name certs with
    | None -> []
    | Some cert ->
        let ctx = { FuncName = fd.Name; MlAliases = mlAliases; SgsAliases = sgsAliases; Certs = certs }
        let env = cert.Params |> List.fold (fun m (n, st) -> Map.add n st m) Map.empty
        match judge ctx env fd.Body with
        | Error d -> [ d ]
        | Ok BInv -> []
        | Ok st ->
            [ bl4009 fd.Body.Span
                  $"function '{fd.Name}': the body is {statusStr st} -- a galilean-certified function must return a boost-invariant value" ]

// The inference channel -- BL4014. Transplanted from the equiv channel:
// hypothesize `where ml.galilean(S)` on an uncertified function, run
// `judgeFunction` verbatim, and PROPOSE the pin as a warning when it holds.
// No new rule is introduced, so `Propose subset-of Check-accept` holds BY
// CONSTRUCTION.
//
// A `GalSig` is built from the conjunct and parameter NAMES alone (no
// annotations needed), but the hypothesis space is the power set of the
// parameters, not a two-element group list. v1 searches two slices: every
// SINGLETON {p} that OCCURS free in the body (independent hypotheses, so
// every passer is proposed), and, if no singleton passed and >= 2 params
// occur, the FULL occurring set once -- the velocity-DIFFERENCE shape
// (`u - v`), where every singleton fails but the joint boost cancels.
// Intermediate subsets are not searched (combinatorial, empirically empty).
//
// OCCURRENCE IS THE VACUITY GUARD: a parameter the body never names is `BVar`
// in an environment nothing reads, so `galilean(unused)` would pass
// vacuously; restricting candidates to free-occurring params removes that.
//
// DEPENDENCY THREADING: declarations fold in DECL ORDER against a
// speculative table of real certs plus earlier-inferred ones; every proposal
// RESTING on unwritten pins names its closure. A function with SEVERAL
// passing candidates is proposed several times but threaded ZERO times -- a
// closure note can name the callee but not which pin it used.

/// One candidate attempt: hypothesize `where ml.galilean(velocities)` on `fd`
/// and run `judgeFunction` against `table` plus that hypothesis. `Some cert` =
/// the certificate holds. Total by construction: any exception reads as "no
/// proposal".
let private tryGalCandidate (mlAliases: Set<string>) (sgsAliases: Set<string>)
                            (table: Map<string, GalSig>) (fd: FunctionDecl)
                            (velocities: string list)
    : GalSig option =
    try
        let vs = Set.ofList velocities
        let cert =
            { Params =
                fd.Params
                |> List.map (fun p -> (p.Name, if Set.contains p.Name vs then BVar else BInv)) }
        match judgeFunction (Map.add fd.Name cert table) mlAliases sgsAliases fd with
        | [] -> Some cert
        | _ :: _ -> None
    with _ -> None

/// Run the galilean judgment speculatively over a module's declarations and
/// return the BL4014 suggestions, in decl order. Never fails or changes a
/// verdict: the caller records these as warnings only.
let inferGalileanCertificates (mlAliases: Set<string>) (sgsAliases: Set<string>)
                              (gcerts: Map<string, GalSig>) (decls: Located<Decl> list)
    : (string * Blade.Ast.Span) list =
    // Speculative certificates, their dependency closures, and decl order.
    let mutable spec : Map<string, GalSig> = Map.empty
    let mutable deps : Map<string, string list> = Map.empty
    let mutable order : string list = []
    let mutable out : (string * Blade.Ast.Span) list = []
    for d in decls do
        match d.Value with
        | DeclFunction fd when (conjunctsOf "__ml_galilean" fd).IsEmpty
                               && not (Map.containsKey fd.Name gcerts) ->
            let pNames = fd.Params |> List.map _.Name
            let bound = Set.ofList pNames
            let free = freeVars bound fd.Body
            // Skip self-recursive bodies -- the circularity Deduce.fs refuses.
            if not (Set.contains fd.Name free) then
                let table = spec |> Map.fold (fun m k v -> Map.add k v m) gcerts
                // The candidate params: those the body actually READS.
                let occurring = freeVars Set.empty fd.Body
                let cands = pNames |> List.filter (fun n -> Set.contains n occurring)
                let attempt vs = (tryGalCandidate mlAliases sgsAliases table fd vs).IsSome
                let singles = cands |> List.filter (fun p -> attempt [ p ]) |> List.map (fun p -> [ p ])
                let hits =
                    if not singles.IsEmpty then singles
                    elif List.length cands >= 2 && attempt cands then [ cands ]
                    else []
                if not hits.IsEmpty then
                    // Direct deps are earlier speculatively-certified names
                    // the body reads; the closure adds their own deps too.
                    let direct = order |> List.filter (fun n -> Set.contains n free)
                    let closure =
                        direct
                        |> List.collect (fun n -> n :: defaultArg (Map.tryFind n deps) [])
                        |> List.distinct
                    let ordered = order |> List.filter (fun n -> List.contains n closure)
                    let closureNote =
                        if ordered.IsEmpty then ""
                        else $""" (also requires pinning: {(String.concat ", " ordered)})"""
                    for vs in hits do
                        let ps = String.concat ", " vs
                        let msg =
                            $"function '{fd.Name}' judges boost-invariant with velocity parameter(s) {ps}: add 'where ml.galilean({ps})'{closureNote}"
                        out <- (msg, d.Span) :: out
                        // Structured twin, hosted on MLEquiv's channel so one
                        // `deduced[]` array carries both disciplines.
                        Blade.ML.Equiv.CertFacts.add
                            { Owner = fd.Name
                              Discipline = "galilean"
                              Group = ps
                              Deps = ordered } d.Span
                    // Thread ONLY an unambiguous proposal (see the header note).
                    match hits with
                    | [ vs ] ->
                        let cert =
                            { Params =
                                fd.Params
                                |> List.map (fun p ->
                                    (p.Name, if List.contains p.Name vs then BVar else BInv)) }
                        spec <- Map.add fd.Name cert spec
                        deps <- Map.add fd.Name closure deps
                        order <- order @ [ fd.Name ]
                    | _ -> ()
        | _ -> ()
    List.rev out

// Constraint-registry handler

/// `galilean(u, ...)` is a callee-side theorem: Validate re-checks the
/// conjunct shape (the elaborator has already judged the body); call sites
/// carry no obligation.
let private galileanHandler : Blade.Constraints.ConstraintHandler = {
    Describe = "galilean(u, ...) -- certifies the function invariant under a constant Galilean boost of the listed velocity parameters; the ML elaborator proves the body combines them only boost-invariantly"
    Validate = fun funcName paramNames args ->
        if args.IsEmpty then
            Error $"function '{funcName}': galilean(...) must name at least one boost-variant (velocity) parameter"
        else
            match args |> List.tryFind (fun a -> not (List.contains a paramNames)) with
            | Some bad -> Error $"function '{funcName}': galilean argument '{bad}' is not a parameter of this function"
            | None -> Ok ()
    EnterBody = fun _ _ -> ()
    ExitBody = fun _ _ -> ()
    Discharge = fun _ _ _ -> Ok ()
}

let mutable private registered = false

let register () =
    if not registered then
        registered <- true
        Blade.Constraints.registerConstraint "__ml_galilean" galileanHandler
