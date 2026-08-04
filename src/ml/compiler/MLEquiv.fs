/// The `where ml.equiv(G)` discipline: a function carrying the normalized
/// `__ml_equiv` conjunct is PROVED equivariant by construction -- its body
/// may compose only equivariance-preserving operations. The judgment is an
/// abstract interpretation over the surface AST, run by MLElaborate at the
/// seam between pass 1 (sizing normalization + resolveStatics) and pass 2
/// (op rewriting), where `ml.*` op calls are still surface-visible and
/// resolve through the same static machinery elaboration uses.
///
/// The certificate is a conditional theorem: IF every representation-typed
/// argument transforms as its declared IrrepsIdx spec (and, for y_to, the
/// coordinate scalars are components of the standard vector), and invariant
/// arguments (weights included) are held fixed, THEN the result transforms
/// as its declared spec. Violations are BL4008.
///
/// Abstract value domain: Rep spec (transforms as the block-diagonal rep
/// described by spec); Inv shape (invariant -- scalars, arrays, weights,
/// norms; may SCALE a rep only when provably a scalar, since an elementwise
/// product with an array is a diagonal matrix that does not commute with the
/// action); Opaque (unclassifiable, rejected at every rep-relevant position).
///
/// TWO BLOCK-SPEC MEMBERS, ONE LATTICE. `Group` is O3 | SO3 | Point of
/// <registry name>; a `Rep`'s payload says which block-spec family describes
/// the law -- an O(3) irreps spec (l, parity, mult) or a point-group spec
/// (LABEL_NAME, mult) against a frozen character table. The point-group
/// polarity table matches this one at every arm (unlike S_n, which diverges
/// and is a sibling registry member instead), so the second member is arms
/// here, not a walker beside it.
///
/// THE ASYMMETRY THAT DECIDES THE SIGNATURE RULES. Every registered point
/// group IS a subgroup of O(3), so an `IrrepsIdx<spec>` space carries a
/// genuine, generally NONTRIVIAL action of C4/D4 by restriction, and
/// classifying such a parameter `Inv` under `equiv(C4)` would falsely assert
/// C4-invariance -- it must first be DECOMPOSED into the group's own labels
/// (`ml.restrict`, named-not-shipped), so it is REJECTED at the signature. A
/// `PgIrrepsIdx<C4, spec>` space carries no O(3) action at all, so "held
/// fixed" is the only reading, a hypothesis the caller can discharge like a
/// weight buffer: `Inv` under equiv(O3)/equiv(SO3). Between two point groups
/// the registry has no subgroup-inclusion data, so a pg parameter whose
/// group differs from the certificate's is REJECTED -- the same
/// "certificates do not transfer" family as a cross-group call.
module Blade.ML.Equiv

open Blade.Ast
open Blade.StaticEval
open Blade.ML.Spec
// The walker shell: freeVars / patternVars / bindPatternVars / judgeEach /
// conjunctsOf -- the syntactic walk shared verbatim with MLGalilean and
// MLPerm. Every RULE below is this discipline's own.
open Blade.ML.CertShell

/// The engine's failure text, shared (not owned) by this file: the same four
/// constructors are called by `MLPolyExtractTyped`, reaching the same
/// `DischargeFailure` / `LieFailure` / `InversionFailure` records from the
/// typed front half -- hence one copy, not two.
module EM = Blade.ML.EquivMessages

type Group =
    | O3
    | SO3
    /// A registered point group, by MLPointSpec registry name (C4, D4).
    | Point of string

/// The payload of a `Rep` status -- WHICH block-spec family describes the
/// value's transformation law. The two cases never meet (a certificate names
/// one group, and `statusOfType` admits only that group's index family), so
/// one union rather than a parameterized `RepStatus` lets every arm below --
/// add, scale, if-join, escape -- stay one rule instead of two.
type RepSpec =
    | O3Spec of Spec
    | PgSpec of string * (string * int) list

/// Shape refinement carried by `Inv`. The rep lattice alone cannot decide the
/// scaling rule: `rep * c` is equivariant when `c` is a SCALAR (it commutes
/// with every block); `rep * w` for an invariant ARRAY of the same extent
/// scales each component independently, and a diagonal matrix with unequal
/// entries does not commute with D^l. So an invariant records whether it is
/// provably 0-dimensional, the same distinction MLPolyExtract gets
/// structurally from VScalar-vs-VVec.
type InvShape =
    /// Provably a scalar (scalar annotation, literal, full-rank element read).
    | InvScalar
    /// Provably an aggregate; `Some r` when the rank is known from a type
    /// annotation, `None` for aggregates whose rank was not established.
    | InvAgg of rank: int option
    /// Shape not established -- treated as non-scalar wherever scalarity is
    /// load-bearing.
    | InvShapeUnknown

type RepStatus =
    | Rep of RepSpec
    | Inv of InvShape
    | Opaque

/// Any invariant, whatever its shape.
let private isInv (st: RepStatus) = match st with Inv _ -> true | _ -> false

/// Shape of an elementwise binary combination (broadcast: scalar op aggregate
/// is the aggregate). Never claims scalar unless both operands are scalar.
let private binShape (a: InvShape) (b: InvShape) : InvShape =
    match a, b with
    | InvScalar, InvScalar -> InvScalar
    | InvAgg r, InvScalar | InvScalar, InvAgg r -> InvAgg r
    | InvAgg r1, InvAgg r2 -> InvAgg (if r1 = r2 then r1 else None)
    | _ -> InvShapeUnknown

/// Meet of two shapes reached on different control-flow paths.
let private meetShape (a: InvShape) (b: InvShape) : InvShape =
    if a = b then a
    else
        match a, b with
        | InvAgg _, InvAgg _ -> InvAgg None
        | _ -> InvShapeUnknown

/// Merge two statuses reached on different control-flow paths.
let private joinStatus (a: RepStatus) (b: RepStatus) : RepStatus option =
    match a, b with
    | Rep s1, Rep s2 when s1 = s2 -> Some (Rep s1)
    | Inv x, Inv y -> Some (Inv (meetShape x y))
    | Opaque, Opaque -> Some Opaque
    | _ -> None

/// Statuses agree for certification purposes. Invariant SHAPE is deliberately
/// excluded -- it exists only to decide the scaling rule; disagreeing shapes
/// are the type checker's to report.
let private statusAgrees (a: RepStatus) (b: RepStatus) : bool =
    joinStatus a b |> Option.isSome

type CertSig = {
    Group: Group
    /// Parameter name -> status, in declaration order.
    Params: (string * RepStatus) list
    Return: RepStatus
}

// Helpers

let private fuel = 100_000

let private bl4008 (span: Span) (msg: string) : Blade.Diagnostics.Diagnostic =
    Blade.Diagnostics.mkError "BL4008" (Blade.Diagnostics.Codes.phaseOfCode "BL4008") span msg

let private specStr (s: Spec) : string =
    s
    |> List.map (fun e -> sprintf "(%d, %d, %d)" e.L e.Parity e.Mult)
    |> String.concat ", "
    |> sprintf "[%s]"

/// A point-group spec as it READS at the surface: [("A", 1), ("E", 2)].
let private pgSpecStr (s: (string * int) list) : string =
    s
    |> List.map (fun (label, m) -> sprintf "(\"%s\", %d)" label m)
    |> String.concat ", "
    |> sprintf "[%s]"

/// How a rep payload names itself in a diagnostic -- the INDEX TYPE the user
/// would have to write. The O(3) rendering is byte-frozen: every message
/// below interpolates this, and must come out identical.
let private repStr (r: RepSpec) : string =
    match r with
    | O3Spec s -> sprintf "IrrepsIdx<%s>" (specStr s)
    | PgSpec (g, s) -> sprintf "PgIrrepsIdx<%s, %s>" g (pgSpecStr s)

let private statusStr (st: RepStatus) : string =
    match st with
    | Rep s -> sprintf "representation-typed (transforms as %s)" (repStr s)
    | Inv InvScalar -> "an invariant scalar"
    | Inv (InvAgg _) -> "an invariant array"
    | Inv InvShapeUnknown -> "invariant"
    | Opaque -> "unclassifiable"

/// How an invariant operand reads in the scaling diagnostic.
let private shapeStr (sh: InvShape) : string =
    match sh with
    | InvScalar -> "a scalar"
    | InvAgg _ -> "an array"
    | InvShapeUnknown -> "of unknown shape"

/// Names TypeCheck treats as builtin scalars (mirrors isBuiltinScalar minus
/// the aggregate constructors). Used only to establish 0-dimensionality.
let private isBuiltinScalarName (n: string) =
    List.contains n
        [ "Int"; "Int32"; "Int64"
          "Float"; "Float32"; "Float64"; "Double"
          "Complex64"; "Complex128"
          "Bool"; "Nat"; "Char" ]

let private groupStr (g: Group) = match g with O3 -> "O3" | SO3 -> "SO3" | Point n -> n

let private isPointGroup (g: Group) = match g with Point _ -> true | _ -> false

/// Mirror of MLElaborate.staticArg (keep in sync): an ML op's static
/// argument is a `let static` binding name or an inline int literal.
let private staticArgValue (statics: StaticEnv) (e: Expr) : Result<StaticValue, string> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (SVInt n)
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some sv -> Ok sv
        | None -> Error (sprintf "'%s' is not a `let static` binding" name)
    | _ -> Error "expected a `let static` binding name or literal"

let private specOfArg (statics: StaticEnv) (what: string) (e: Expr) : Result<Spec, string> =
    staticArgValue statics e
    |> Result.bind (Blade.ML.Statics.specOfStatic what)

/// Mirror of MLElaborate.pgGroupArg: the GROUP argument of a point-group op
/// is a bare registered name, a string literal, or a `let static` string
/// binding. Resolution against the registry is the CALLER's.
let private pgGroupArgName (statics: StaticEnv) (e: Expr) : Result<string, string> =
    match e.Kind with
    | ExprKind.ExprLit (LitString s) -> Ok s
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some (SVString s) -> Ok s
        | Some _ -> Error (sprintf "'%s' is a `let static` binding but not a STRING -- GROUP names a registered point group, e.g. \"C4\"" name)
        | None -> Ok name
    | _ -> Error "GROUP must be a point-group name (a bare C4 / D4, a string literal, or a `let static` string binding)"

/// THE TRIVIAL LABEL, identified from the frozen table and never by name: the
/// one whose every GENERATOR matrix is the identity (a product of identities
/// is an identity, so generators suffice). Reading the name would get D4
/// wrong: A2, B1 and B2 are 1-DIMENSIONAL but not invariant -- each flips
/// under some generator, so a cell of one is a pseudoscalar.
let private isTrivialLabel (grp: Blade.ML.PointSpec.PointGroup) (label: string) : bool =
    let ir = Blade.ML.PointSpec.pgIrrep grp label
    ir.DimR = 1
    && ir.Gens |> List.forall (fun m -> Blade.ML.PointSpec.matEq m (Blade.ML.PointSpec.matId ir.DimR))

/// Static-offset read admissibility: under O3 only (l=0, even) blocks hold
/// full invariants; under SO3 any l=0 block does (pseudoscalars are
/// SO(3)-invariant); l=0 blocks have dim 1, so block b spans [start_b ..
/// start_b + mult_b). The point-group reading replays the same theorem:
/// invariant cells are those of a TRIVIAL label, and the O3/SO3 parity split
/// becomes the trivial/non-trivial character split -- at D4 the A2, B1, B2
/// cells are 1-dimensional and still basis-dependent, the pseudoscalar
/// asymmetry as table data.
let private invariantOffsets (g: Group) (r: RepSpec) : Set<int> =
    match g, r with
    | (O3 | SO3), O3Spec s ->
        let starts = blockStarts s
        [ for b in 0 .. s.Length - 1 do
            let e = s.[b]
            if e.L = 0 && (g = SO3 || e.Parity = 0) then
                yield! [ starts.[b] .. starts.[b] + e.Mult - 1 ] ]
        |> Set.ofList
    | Point gn, PgSpec (gn2, s) when gn = gn2 ->
        let grp = Blade.ML.PointSpec.pointGroup gn
        let starts = Blade.ML.PointSpec.pgBlockStarts grp s
        [ for b in 0 .. s.Length - 1 do
            let (label, mult) = s.[b]
            if isTrivialLabel grp label then
                yield! [ starts.[b] .. starts.[b] + mult - 1 ] ]
        |> Set.ofList
    // A payload from the other member cannot arise (statusOfType admits only
    // the certificate group's index family); if it ever did, NO offset reads.
    | _ -> Set.empty

// Certified-signature table

/// Type aliases of this module (one-level chase for `type X = IrrepsIdx<..>`
/// inside Array annotations, mirroring registerTypeDecl's transparency).
let private aliasMapOf (decls: Located<Decl> list) : Map<string, TypeExpr> =
    decls
    |> List.fold (fun m d ->
        match d.Value with
        | DeclType (TyDeclAlias (n, [], body)) -> Map.add n body m
        | _ -> m) Map.empty

/// The registered point-group names, as they read in an `equiv(...)` conjunct.
let private pgRoster = String.concat ", " Blade.ML.PointSpec.pointGroupNames

let private parseGroup (funcName: string) (args: string list) : Result<Group, string> =
    match args with
    | [ "O3" ] -> Ok O3
    | [ "SO3" ] -> Ok SO3
    | [ g ] when List.contains g Blade.ML.PointSpec.pointGroupNames -> Ok (Point g)
    | [ g ] -> Error (sprintf "function '%s': equiv(%s) -- unknown group '%s'; supported: O3, SO3, %s" funcName g g pgRoster)
    | _ -> Error (sprintf "function '%s': equiv expects exactly one group argument -- equiv(O3), equiv(SO3), or a registered point group (%s)" funcName pgRoster)

/// The restricted spec as it would READ in an annotation, or "" when the spec
/// argument does not resolve. A diagnostic ENRICHMENT only -- wrapped in a
/// try/with since a speculative inference run may never crash on a malformed
/// static, and its absence costs the message one sentence and no verdict.
let private restrictHint (pg: string) (statics: StaticEnv) (specExpr: Expr) : string =
    try
        match evalExpr statics fuel specExpr
              |> Result.bind (Blade.ML.Statics.specOfStatic "equiv signature spec") with
        | Ok s ->
            let grp = Blade.ML.PointSpec.pointGroup pg
            let r = Blade.ML.PointSpec.restrictSpec grp (s |> List.map (fun e -> (e.L, e.Parity, e.Mult)))
            sprintf " That space restricts to PgIrrepsIdx<%s, %s>, which `ml.pg_restrict(\"%s\", SPEC)` names."
                pg (pgSpecStr r) pg
        | Error _ -> ""
    with _ -> ""

/// The two signature-level refusals of the point-group arm (annotation
/// position and alias-body position), stated once so they cannot drift
/// apart. The message can NAME the point-group module a parameter's space
/// becomes (MLPointSpec.restrictSpec / `ml.pg_restrict`), but may not
/// classify it `Rep (PgSpec ...)` on that strength: O(3) orders a block by m
/// = -l..l, so its G-invariant m = 0 component sits in the MIDDLE with the
/// m-pairs straddling it, while a point-group block is contiguous
/// (`pgBlockStarts`) -- already disagreeing at l = 1 under C4 (invariant at
/// index 1 vs index 0), and no ORDERING of the restricted spec repairs it,
/// since the E pair is split by the A component. Reading the buffer at the
/// restricted type needs a genuine change of basis, a value-level op this
/// checker does not ship, so the refusal stands.
let private restrictDeferral (pg: string) (hint: string) : string =
    sprintf "an IrrepsIdx parameter under equiv(%s) names an O(3) representation space, and %s ACTS on it -- by restriction along the inclusion %s -> O(3), nontrivially on every l > 0 block. Classifying it invariant would claim a vector is %s-invariant, so the judgment refuses instead.%s A decomposition is NOT a reinterpretation, though: the O(3) layout orders every block by m = -l..l, so the invariant m = 0 component sits in the MIDDLE of the block while a point-group block is contiguous, and the two layouts already disagree at l = 1. Reading this buffer at the restricted type needs a genuine change of basis -- a permutation with signs -- which is a value-level op this round does not ship. Declare the parameter as Array<_ like PgIrrepsIdx<%s, SPEC>> over data that is already in %s's layout"
        pg pg pg pg hint pg pg

let private pgGroupMismatch (declared: string) (pg: string) : string =
    sprintf "parameter type PgIrrepsIdx<%s, ...> names point group %s, but this function is certified for %s -- certificates do not transfer between groups. This checker knows each registered group's frozen table and NO map between two of them, so neither a restriction nor an induction of a %s-module to %s is available; declare the parameter over %s"
        declared declared pg declared pg pg

/// Classify a signature annotation. Certified functions must be fully
/// annotated; Rep needs `Array<T like IrrepsIdx<spec>>` (directly or via a
/// one-level type alias), scalars and plain arrays are Inv. The group
/// SELECTS the live index family: under O3/SO3 that is `IrrepsIdx` and a
/// `PgIrrepsIdx` annotation is an ordinary invariant buffer (the header's
/// asymmetry); under `Point g` it is `PgIrrepsIdx<g, _>` and both an
/// `IrrepsIdx` annotation and another group's `PgIrrepsIdx` are refusals.
let rec private statusOfType (g: Group) (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (t: TypeExpr)
    : Result<RepStatus, string> =
    match t with
    | TyArray (_, idxs) ->
        let irreps = idxs |> List.choose (function TyIrrepsIdx s -> Some s | _ -> None)
        match g with
        | Point pg ->
            let pgs = idxs |> List.choose (function TyPgIrrepsIdx (gn, s) -> Some (gn, s) | _ -> None)
            match irreps, pgs, idxs.Length with
            | specExpr :: _, _, _ -> Error (restrictDeferral pg (restrictHint pg statics specExpr))
            | [], [ (gn, _) ], _ when gn <> pg -> Error (pgGroupMismatch gn pg)
            | [], [ (_, specExpr) ], 1 ->
                evalExpr statics fuel specExpr
                |> Result.bind (Blade.ML.Statics.pgSpecOfStatic "equiv signature spec" (Blade.ML.PointSpec.pointGroup pg))
                |> Result.map (fun s -> Rep (PgSpec (pg, s)))
            | [], [], n -> Ok (Inv (InvAgg (Some n)))
            | _ ->
                Error "multi-index arrays mixing PgIrrepsIdx are not supported in equiv-certified signatures"
        | O3 | SO3 ->
        match irreps, idxs.Length with
        | [], n -> Ok (Inv (InvAgg (Some n)))
        | [ specExpr ], 1 ->
            evalExpr statics fuel specExpr
            |> Result.bind (Blade.ML.Statics.specOfStatic "equiv signature spec")
            |> Result.map (O3Spec >> Rep)
        | _ ->
            Error "multi-index arrays mixing IrrepsIdx are not supported in equiv-certified signatures"
    | TyNamed (n, []) ->
        match Map.tryFind n aliases with
        | Some body -> statusOfType g aliases statics (TyArray (TyNamed ("Float", []), [ body ]))
        // A builtin scalar name is provably 0-dimensional; any other named
        // type is invariant but of unestablished shape (may be an
        // aggregate), so it may not scale a rep.
        | None when isBuiltinScalarName n -> Ok (Inv InvScalar)
        | None -> Ok (Inv InvShapeUnknown)
    // A builtin scalar name carrying ARGUMENTS is still that scalar: a unit
    // (`Float<meters>`) or index tag (`Nat<LatIdx>`) refines a 0-dimensional
    // type, not a shape change, and all readings lower to a scalar. The
    // argument-FREE form alone would let a unit annotation decide an
    // equivariance verdict (`rep * s` wrongly refused for a scalar `s`) --
    // the typed twin cannot hit this, since DeduceRep.classifyTypeR sees
    // through `IRTUnitAnnotated`/`IRTIdxTagged` on the LOWERED type.
    | TyNamed (n, _) when isBuiltinScalarName n -> Ok (Inv InvScalar)
    | TyNamed (_, _) -> Ok (Inv InvShapeUnknown)
    // A bounded primitive is its base type: `min=`/`max=` (TyBounded) refine
    // the base -- a VALUE predicate, not a coordinate -- and erase before
    // codegen (`Float<min=0, max=1>` lowers exactly as `Float`), so two
    // annotations lowering to the same runtime type need the same verdict.
    // Without this arm a bounded parameter hit the catch-all and its Error
    // skipped the whole function, the same cross-lattice leak the unit
    // annotation had. Chasing the base composes with the unit reading:
    // `Float<meters, min=0, max=1>` keeps the unit as a positional argument
    // on the inner `TyNamed` node.
    | TyBounded (baseTy, _, _) -> statusOfType g aliases statics baseTy
    | TyIrrepsIdx specExpr ->
        // alias body position (`type X = IrrepsIdx<s>` chased above)
        match g with
        | Point pg -> Error (restrictDeferral pg (restrictHint pg statics specExpr))
        | _ ->
            evalExpr statics fuel specExpr
            |> Result.bind (Blade.ML.Statics.specOfStatic "equiv signature spec")
            |> Result.map (O3Spec >> Rep)
    | TyPgIrrepsIdx (gn, specExpr) when isPointGroup g ->
        // the pg twin of the arm above; under O3/SO3 this annotation falls
        // through to the default arm.
        let pg = groupStr g
        if gn <> pg then Error (pgGroupMismatch gn pg)
        else
            evalExpr statics fuel specExpr
            |> Result.bind (Blade.ML.Statics.pgSpecOfStatic "equiv signature spec" (Blade.ML.PointSpec.pointGroup pg))
            |> Result.map (fun s -> Rep (PgSpec (pg, s)))
    | TyInt32 | TyInt64 | TyFloat32 | TyFloat64 | TyBool | TyComplex128 -> Ok (Inv InvScalar)
    | _ -> Error "cannot classify this annotation in an equiv-certified signature (supported: scalars, plain arrays, Array<_ like IrrepsIdx<spec>>)"

/// Classify ONE function's signature under a group -- shared by CHECKING
/// (buildCertTable, group from the written conjunct) and DEDUCTION (the
/// inference channel, group as a hypothesis). Both must agree EXACTLY on
/// what "fully annotated" and "classifies Rep" mean, or a proposal the
/// checker would refuse at the signature breaks Propose subset-of
/// Check-accept. Errors are the strings buildCertTable wraps in BL4008;
/// inference discards them and stays silent.
let private certSigOf (g: Group) (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (fd: FunctionDecl)
    : Result<CertSig, string> =
    let paramSt =
        fd.Params
        |> List.fold (fun acc p ->
            acc |> Result.bind (fun ps ->
                match p.Type with
                | None ->
                    Error (sprintf "function '%s': an equiv-certified function must annotate every parameter and its return type ('%s' is unannotated)" fd.Name p.Name)
                | Some t ->
                    statusOfType g aliases statics t
                    |> Result.mapError (sprintf "function '%s', parameter '%s': %s" fd.Name p.Name)
                    |> Result.map (fun st -> ps @ [ (p.Name, st) ])))
            (Ok [])
    paramSt |> Result.bind (fun ps ->
        match fd.ReturnType with
        | None -> Error (sprintf "function '%s': an equiv-certified function must annotate its return type" fd.Name)
        | Some rt ->
            statusOfType g aliases statics rt
            |> Result.mapError (sprintf "function '%s', return type: %s" fd.Name)
            |> Result.map (fun r -> { Group = g; Params = ps; Return = r }))

/// Pre-scan: every DeclFunction carrying a normalized ("__ml_equiv", [g])
/// conjunct gets a certified signature. Errors are BL4008 at the decl.
let buildCertTable (statics: StaticEnv) (decls: Located<Decl> list)
    : Result<Map<string, CertSig>, Blade.Diagnostics.Diagnostic> =
    let aliases = aliasMapOf decls
    let certDecls =
        decls
        |> List.choose (fun d ->
            match d.Value with
            | DeclFunction fd ->
                let conjs = conjunctsOf "__ml_equiv" fd
                match conjs with
                | [] -> None
                | cs -> Some (d.Span, fd, cs)
            | _ -> None)
    certDecls
    |> List.fold (fun acc (span, fd, conjs) ->
        acc |> Result.bind (fun table ->
            let fail msg = Error (bl4008 span msg)
            match conjs with
            | _ :: _ :: _ -> fail (sprintf "function '%s': duplicate equiv constraints -- declare exactly one group" fd.Name)
            | [ (_, gArgs) ] ->
                match parseGroup fd.Name gArgs with
                | Error m -> fail m
                | Ok g ->
                    match certSigOf g aliases statics fd with
                    | Error m -> fail m
                    | Ok cs -> Ok (Map.add fd.Name cs table)
            | [] -> Ok table))
        (Ok Map.empty)

// The judgment

/// Shape of a module-level binding read syntactically from its value. Only
/// forms whose shape is manifest answer anything but `InvShapeUnknown`.
let rec private syntacticShape (e: Expr) : InvShape =
    match e.Kind with
    | ExprKind.ExprLit _ -> InvScalar
    | ExprKind.ExprArrayLit _ | ExprKind.ExprTuple _ | ExprKind.ExprDotDot _ -> InvAgg None
    | ExprKind.ExprUnaryOp (_, i) | ExprKind.ExprTyped (i, _) -> syntacticShape i
    | ExprKind.ExprBinOp (Elementwise, _, l, r) -> binShape (syntacticShape l) (syntacticShape r)
    | _ -> InvShapeUnknown

/// Shapes of module-level `let`/`static` bindings. Globals stay invariant by
/// the conditional-theorem reading (held fixed), but their SHAPE decides
/// whether they may scale a rep, so a global array is not waved through as
/// if it were a scalar.
let buildGlobalShapes (g: Group) (statics: StaticEnv) (decls: Located<Decl> list) : Map<string, InvShape> =
    let aliases = aliasMapOf decls
    let shapeOf (b: Binding) =
        match b.Type with
        | Some t ->
            match statusOfType g aliases statics t with
            | Ok (Inv sh) -> sh
            | Ok (Rep _) -> InvAgg (Some 1) // a rep-annotated global is an array
            | _ -> InvShapeUnknown
        | None -> syntacticShape b.Value
    decls
    |> List.fold (fun m d ->
        match d.Value with
        | DeclLet b | DeclStatic b ->
            let sh = shapeOf b
            Blade.ML.CertShell.patternVars b.Pattern
            |> List.fold (fun m2 n ->
                // A destructured component's shape is not the aggregate's.
                match b.Pattern.Kind with
                | PatternKind.PatVar _ -> Map.add n sh m2
                | _ -> Map.add n InvShapeUnknown m2) m
        | _ -> m) Map.empty

type private Ctx = {
    Group: Group
    FuncName: string
    Aliases: Set<string>
    Statics: StaticEnv
    Certs: Map<string, CertSig>
    /// Shapes of module-level bindings (see buildGlobalShapes).
    Globals: Map<string, InvShape>
}

/// Every `ml.*` operation whose arms below are stated in O(3) irreps specs --
/// the ops that MAKE, CONSUME or RESHAPE an `IrrepsIdx` value. Under a
/// point-group certificate they are refused BY NAME (a targeted message beats
/// the generic default arm, which would fire only when an argument happened
/// to be rep-typed). Under equiv(O3)/equiv(SO3) this list is never consulted.
let private o3OpNames =
    [ "y_to"; "tensor_product"; "linear"; "gated"; "scalars"; "norms"
      "derive_linear"; "derive_tp"; "derive_sym_tp"; "derive_alt_tp"; "derive_poly"
      "sym_lift"; "linear_rows"; "gated_rows"
      "tensor_to_irreps"; "sym_to_irreps"; "irreps_to_sym" ]

/// Shape of a folded static value. The `__ml_stat_*` sizing builtins are in
/// the scalar-builtin list but do NOT all return scalars -- sh_spec and
/// tp_spec fold to spec TUPLES -- so shape is read from the fold, not assumed.
let private shapeOfStatic (sv: StaticValue) : InvShape =
    match sv with
    | SVInt _ | SVFloat _ | SVBool _ -> InvScalar
    | SVTuple _ -> InvAgg None
    | _ -> InvShapeUnknown

/// The scalar builtins a certified body may apply to invariants.
let private isKnownScalarBuiltin (n: string) =
    n.StartsWith "__ml_stat_"
    || List.contains n [ "exp"; "log"; "sqrt"; "sin"; "cos"; "tan"; "tanh"; "abs"; "floor"; "ceil"; "min"; "max"; "pow" ]

let rec private judge (ctx: Ctx) (env: Map<string, RepStatus>) (e: Expr)
    : Result<RepStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4008 e.Span (sprintf "function '%s': %s" ctx.FuncName msg))
    let j = judge ctx env
    match e.Kind with
    | ExprKind.ExprLit _ -> Ok (Inv InvScalar)
    | ExprKind.ExprArrayLit es | ExprKind.ExprTuple es ->
        // constants and invariant aggregates are invariant; anything rep-
        // valued inside an aggregate loses its rep structure -> reject.
        es
        |> List.fold (fun acc x ->
            acc |> Result.bind (fun st ->
                j x |> Result.bind (fun sx ->
                    match sx with
                    | Inv _ -> Ok st
                    | Rep _ -> reject "a representation-typed value may not be packed into a literal aggregate -- the aggregate does not transform as a rep"
                    | Opaque -> Ok Opaque)))
            (Ok (Inv (InvAgg None)))
    | ExprKind.ExprVar n ->
        match Map.tryFind n env with
        | Some st -> Ok st
        // globals/constants/builtins: invariant by the conditional-theorem
        // reading; shape from the module-level table when it is known there.
        | None ->
            match Map.tryFind n ctx.Statics.Values with
            | Some sv -> Ok (Inv (shapeOfStatic sv))
            | None ->
                match Map.tryFind n ctx.Globals with
                | Some sh -> Ok (Inv sh)
                | None -> Ok (Inv InvShapeUnknown)
    | ExprKind.ExprDotDot _ -> Ok (Inv (InvAgg None))
    | ExprKind.ExprTyped (inner, _) -> j inner
    | ExprKind.ExprUnaryOp (_, inner) -> j inner
    // Former application must dispatch BEFORE the general binop arithmetic
    // arm (OpApply is a BinOp constructor): see judgeFormerApply for the
    // false accept its absence also left open, not just a false REJECT.
    | ExprKind.ExprBinOp (_, OpApply, loop, _) -> judgeFormerApply ctx env e loop
    | ExprKind.ExprBinOp (mode, op, l, r) ->
        j l |> Result.bind (fun sl ->
        j r |> Result.bind (fun sr ->
            // Outer mode ([+], [*]) cross-iterates, so the result has a
            // higher rank than either operand and cannot be the rep it was
            // built from.
            let outerRep () =
                reject "the outer-product form of this operator raises the rank of its result, so the result is not the representation its operand transforms as -- use the elementwise form"
            let nonScalarScale sh =
                reject (sprintf "only an invariant SCALAR may scale a representation-typed value, and this invariant is %s -- an elementwise product with an array scales each component of an irrep block independently, which does not commute with the group action (scale whole blocks with the learned block-diagonal map -- ml.linear under O3/SO3, ml.derive_pg_linear under a point group -- or gate them with ml.gated)" (shapeStr sh))
            match sl, sr, op with
            | (Rep _, _, _ | _, Rep _, _) when mode <> Elementwise -> outerRep ()
            | Rep s1, Rep s2, (OpAdd | OpSub) ->
                if s1 = s2 then Ok (Rep s1)
                else reject (sprintf "cannot add values of different representations -- left transforms as %s, right as %s" (repStr s1) (repStr s2))
            | Rep _, Rep _, OpMul ->
                reject "elementwise product of representation-typed values is not equivariant -- use ml.tensor_product, the Clebsch-Gordan-typed contraction"
            | Rep _, Rep _, _ ->
                reject "this operator is not equivariant on representation-typed values"
            // Scaling by an invariant SCALAR is the only admissible mixed
            // form: a scalar commutes with every block. An invariant ARRAY
            // of the same extent scales each component independently, which
            // does not -- a false certificate, not a scaling. Shape must be
            // PROVEN, so an unestablished shape is rejected too.
            | Rep s, Inv InvScalar, (OpMul | OpDiv) | Inv InvScalar, Rep s, OpMul -> Ok (Rep s)
            | Rep _, Inv sh, (OpMul | OpDiv) when sh <> InvScalar -> nonScalarScale sh
            | Inv sh, Rep _, OpMul when sh <> InvScalar -> nonScalarScale sh
            | (Rep _, Inv _, _) | (Inv _, Rep _, _) ->
                reject "mixing a representation-typed value with an invariant under this operator breaks equivariance (only scaling -- * and / by an invariant scalar -- preserves the rep)"
            | Inv shl, Inv shr, _ ->
                Ok (Inv (if mode = Elementwise then binShape shl shr else InvShapeUnknown))
            | Opaque, _, _ | _, Opaque, _ -> Ok Opaque))
    | ExprKind.ExprIf (c, t, f) ->
        j c |> Result.bind (fun sc ->
            match sc with
            | Inv _ ->
                j t |> Result.bind (fun st ->
                j f |> Result.bind (fun sf ->
                    match joinStatus st sf with
                    | Some s -> Ok s
                    | None -> reject (sprintf "if branches disagree: then-branch is %s, else-branch is %s" (statusStr st) (statusStr sf))))
            | _ -> reject "an if condition inside an equiv-certified body must be invariant")
    | ExprKind.ExprMatch (scrut, cases) ->
        j scrut |> Result.bind (fun ss ->
            match ss with
            | Inv _ ->
                cases
                |> judgeEach (fun c -> judge ctx (bindPatternVars (Inv InvShapeUnknown) env c.Pattern) c.Body)
                |> Result.bind (fun sts ->
                    match sts with
                    | [] -> Ok (Inv InvShapeUnknown)
                    | s :: rest ->
                        match rest |> List.fold (fun acc s2 -> acc |> Option.bind (fun a -> joinStatus a s2)) (Some s) with
                        | Some joined -> Ok joined
                        | None -> reject "match arms disagree on their representation status")
            | _ -> reject "a match scrutinee inside an equiv-certified body must be invariant")
    | ExprKind.ExprLet (binding, body) ->
        j binding.Value |> Result.bind (fun sv ->
            match binding.Pattern.Kind, sv with
            | PatternKind.PatVar n, _ -> judge ctx (Map.add n sv env) body
            | _, Inv _ -> judge ctx (bindPatternVars (Inv InvShapeUnknown) env binding.Pattern) body
            | _, _ -> reject "cannot destructure a representation-typed value -- its components are basis-dependent")
    | ExprKind.ExprLambda (ps, _, lamBody) ->
        // A lambda is admissible only when it never touches a rep -- free
        // vars must all be non-Rep; it is then an invariant helper.
        let captured = freeVars (Set.ofList (ps |> List.map (fun p -> p.Name))) lamBody
        let repCapture =
            captured |> Set.toList |> List.tryFind (fun n ->
                match Map.tryFind n env with Some (Rep _) -> true | _ -> false)
        match repCapture with
        | Some n -> reject (sprintf "lambda captures representation-typed '%s' -- factor rep work into equiv-certified functions instead" n)
        | None -> Ok (Inv InvShapeUnknown)
    | ExprKind.ExprAssign (l, r) ->
        judgeAssign ctx env e.Span l r |> Result.map (fun () -> Inv InvShapeUnknown)
    | ExprKind.ExprBlock (stmts, finalE) ->
        judgeStmts ctx env stmts
        |> Result.bind (fun env' ->
            match finalE with
            | Some fe -> judge ctx env' fe
            | None -> Ok (Inv InvShapeUnknown))
    | ExprKind.ExprApp (f, args) -> judgeApp ctx env e f args
    // Component reads out of an INVARIANT aggregate. SOUNDNESS: `Inv` means
    // the value is HELD FIXED by the group action, so if the aggregate `t`
    // and the selector are both fixed, `t[k]`/`t.f` is fixed too. The result
    // is `InvShapeUnknown`, NOT `InvScalar`, since a component of an
    // invariant array may itself be an array, and an unestablished shape is
    // refused by the only arm where shape is load-bearing (`nonScalarScale`),
    // so this can never become a false SCALING certificate. Stays `Opaque`
    // for: a `Rep` base (basis-dependent components, the `x(2)` rejection
    // one syntax over); an `Opaque` base (nothing known, nothing claimed); a
    // base whose judgment ERRORS (swallowed into `Opaque`, so this arm only
    // adds accepts, never new rejects); or a non-invariant selector (the
    // cell picked would move with the frame, as in `judgeAssign`).
    | ExprKind.ExprTupleIndex (baseE, idxE) ->
        (match j baseE, j idxE with
         | Ok (Inv _), Ok (Inv _) -> Ok (Inv InvShapeUnknown)
         | _ -> Ok Opaque)
    // A field name is a STATIC selector (an Ident, not an expression), so
    // there is no index to judge -- the base alone decides.
    | ExprKind.ExprField (baseE, _) ->
        (match j baseE with
         | Ok (Inv _) -> Ok (Inv InvShapeUnknown)
         | _ -> Ok Opaque)
    // Functional iteration: MLGalilean and MLPerm already carry these arms.
    // Virtual arrays enumerate INDICES, and an index carries no rep
    // structure: invariant.
    | ExprKind.ExprRange _ | ExprKind.ExprReverse _ | ExprKind.ExprHalo _ -> Ok (Inv (InvAgg None))
    // compute is a scheduling boundary, not a value transform.
    | ExprKind.ExprCompute x -> judge ctx env x
    // A fold over a rep sums BASIS-DEPENDENT COMPONENTS: the sum of an l > 0
    // vector's components is not a rotational invariant (the norm is), so
    // this is refused on the same grounds as raw component access. NOTE THE
    // POLARITY against MLPerm, where a reduce over a node power IS invariant
    // whenever the combiner is commutative, deferred only because the
    // combiner is not analysed here.
    | ExprKind.ExprReduce (src, _, init) ->
        judge ctx env src |> Result.bind (fun ss ->
            (match init with
             | Some i -> judge ctx env i
             | None -> Ok (Inv InvScalar)) |> Result.bind (fun si ->
                match ss, si with
                // a reduce collapses its source to one value
                | Inv _, Inv _ -> Ok (Inv InvScalar)
                | Rep _, _ | _, Rep _ ->
                    Error (bl4008 e.Span (sprintf "function '%s': reduce over a representation-typed value folds basis-dependent components into a number that is not rotation-invariant -- extract invariants with ml.scalars/ml.norms, or contract with ml.tensor_product" ctx.FuncName))
                | _ -> Ok Opaque))
    | _ -> Ok Opaque

/// `loop <@> kernel` under the equiv judgment. A former hands its kernel the
/// ELEMENTS of its sources, and an element of a rep is a COMPONENT -- the
/// basis-dependent number this discipline exists to refuse, the same
/// rejection as `x(2)`, one syntax over.
///
/// THIS ARM CLOSES A FALSE ACCEPT, not just a false reject. Without it,
/// `method_for(f) <@> k` fell through to the arithmetic binop arm, judging
/// the former source through the `| _ -> Ok Opaque` catch-all and returning
/// Opaque for the whole application; a READ out of that Opaque binding then
/// takes judgeApp's uncertified-callee path, which answers Inv for
/// invariant arguments -- so a binding built this way and read back later
/// handed back a component of a rep CERTIFIED ROTATION-INVARIANT. corpus
/// ml-equiv/049 is that program.
///
/// NOTE THE POLARITY against MLGalilean's judgeFormerApply, which BINDS
/// source statuses to the kernel's leading parameters and lets a BVar flow
/// in: an element of a boost-variant field is boost-variant too, since every
/// component shifts by the same u0. An element of a rep is NOT a rep, one
/// basis-dependent coordinate of one -- the same move here would be exactly
/// the unsoundness above.
and private judgeFormerApply (ctx: Ctx) (env: Map<string, RepStatus>) (e: Expr) (loop: Expr)
    : Result<RepStatus, Blade.Diagnostics.Diagnostic> =
    let sources =
        match loop.Kind with
        | ExprKind.ExprMethodFor arrays -> arrays
        | ExprKind.ExprFor (ForArrays (arrays, _), _, _) -> arrays
        | _ -> []
    sources
    |> judgeEach (judge ctx env)
    |> Result.bind (fun srcSts ->
        match srcSts |> List.tryFindIndex (function Rep _ -> true | _ -> false) with
        | Some i ->
            Error (bl4008 sources.[i].Span
                       (sprintf "function '%s': the kernel of this former would receive COMPONENTS of a representation-typed source, which are basis-dependent numbers -- extract invariants with ml.scalars/ml.norms, or contract with ml.tensor_product"
                            ctx.FuncName))
        | None ->
            // What the KERNEL captures, which judging the sources says nothing
            // about (the lambda arm never runs: this arm consumed the apply).
            let captured =
                freeVars Set.empty e |> Set.toList |> List.tryFind (fun n ->
                    match Map.tryFind n env with Some (Rep _) -> true | _ -> false)
            match captured with
            | Some n ->
                Error (bl4008 e.Span
                           (sprintf "function '%s': the kernel of this former captures representation-typed '%s' -- factor rep work into equiv-certified functions instead"
                                ctx.FuncName n))
            | None ->
                // a former builds an aggregate, whatever its sources were
                if srcSts |> List.exists ((=) Opaque) then Ok Opaque else Ok (Inv (InvAgg None)))

and private judgeStmts (ctx: Ctx) (env: Map<string, RepStatus>) (stmts: Stmt list)
    : Result<Map<string, RepStatus>, Blade.Diagnostics.Diagnostic> =
    stmts
    |> List.fold (fun acc s ->
        acc |> Result.bind (fun env ->
            match unwrapStmt s with
            | StmtLet binding ->
                judge ctx env binding.Value |> Result.bind (fun sv ->
                    match binding.Pattern.Kind, sv with
                    | PatternKind.PatVar n, _ -> Ok (Map.add n sv env)
                    | _, Inv _ -> Ok (bindPatternVars (Inv InvShapeUnknown) env binding.Pattern)
                    | _, _ ->
                        Error (bl4008 binding.Value.Span (sprintf "function '%s': cannot destructure a representation-typed value -- its components are basis-dependent" ctx.FuncName)))
            | StmtExpr e2 -> judge ctx env e2 |> Result.map (fun _ -> env)
            | StmtAssign (l, _, r) -> judgeAssign ctx env l.Span l r |> Result.map (fun () -> env)
            | StmtForIn (v, range, body) ->
                judge ctx env range |> Result.bind (fun sr ->
                    match sr with
                    | Rep _ ->
                        Error (bl4008 range.Span (sprintf "function '%s': cannot iterate over a representation-typed value's components -- they are basis-dependent" ctx.FuncName))
                    | _ ->
                        judgeStmts ctx (Map.add v (Inv InvShapeUnknown) env) body |> Result.map (fun _ -> env))
            | _ -> Ok env))
        (Ok env)

/// Assignments: whole-variable writes must preserve the target's status;
/// element writes into a rep are rejected (the raw access this discipline
/// forbids); element writes into invariants need invariant values.
and private judgeAssign (ctx: Ctx) (env: Map<string, RepStatus>) (span: Span) (l: Expr) (r: Expr)
    : Result<unit, Blade.Diagnostics.Diagnostic> =
    let fail msg = Error (bl4008 span (sprintf "function '%s': %s" ctx.FuncName msg))
    judge ctx env r |> Result.bind (fun sr ->
        match l.Kind with
        | ExprKind.ExprVar n ->
            match Map.tryFind n env with
            | Some st when st = sr -> Ok ()
            | Some st -> fail (sprintf "assignment changes '%s' from %s to %s -- a mut binding must keep one representation status" n (statusStr st) (statusStr sr))
            | None -> Ok ()
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, idxArgs) ->
            // element write: the INDICES are judged first, then the container
            // and the value. MLGalilean's judgeAssign already did this; this
            // copy and MLPerm's matched the target and walked past the index
            // expressions, so `a(f(0)) = v` went unchecked on the WRITE path
            // while the identical read `let z = f(0)` was rejected as a raw
            // component read: a write whose LOCATION is basis-dependent is
            // not equivariant.
            idxArgs
            |> List.fold (fun acc a ->
                acc |> Result.bind (fun () ->
                    judge ctx env a |> Result.bind (fun si ->
                        match si with
                        | Inv _ -> Ok ()
                        | Rep _ ->
                            Error (bl4008 a.Span
                                       (sprintf "function '%s': an array index must be invariant inside an equiv-certified body, but this one is %s -- the cell it selects moves with the frame"
                                            ctx.FuncName (statusStr si)))
                        | Opaque ->
                            Error (bl4008 a.Span
                                       (sprintf "function '%s': an array index must be invariant inside an equiv-certified body, and this one is unclassifiable -- the judgment cannot rule out that the cell it selects moves with the frame. Index with a static offset or a value the judgment can see"
                                            ctx.FuncName)))))
                (Ok ())
            |> Result.bind (fun () ->
                match Map.tryFind n env with
                | Some (Rep _) -> fail (sprintf "element-assignment into representation-typed '%s' writes a basis-dependent component -- build reps only through equivariant ops" n)
                | _ ->
                    match sr with
                    | Rep _ -> fail "cannot store a representation-typed value into an array element"
                    | _ -> Ok ())
        | _ ->
            match sr with
            | Rep _ -> fail "unsupported assignment target for a representation-typed value"
            | _ -> Ok ())

and private judgeApp (ctx: Ctx) (env: Map<string, RepStatus>) (e: Expr) (f: Expr) (args: Expr list)
    : Result<RepStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4008 e.Span (sprintf "function '%s': %s" ctx.FuncName msg))
    let judgeAll args = judgeEach (judge ctx env) args
    let requireRep (what: string) (expected: RepSpec) (argE: Expr) =
        judge ctx env argE |> Result.bind (fun s ->
            match s with
            | Rep sp when sp = expected -> Ok ()
            | Rep sp ->
                Error (bl4008 argE.Span (sprintf "function '%s': %s expects a value transforming as %s, got %s" ctx.FuncName what (repStr expected) (repStr sp)))
            | Inv _ ->
                Error (bl4008 argE.Span (sprintf "function '%s': %s expects a representation-typed value (transforming as %s) -- an invariant here would not co-rotate with the inputs" ctx.FuncName what (repStr expected)))
            | Opaque ->
                Error (bl4008 argE.Span (sprintf "function '%s': cannot classify the argument to %s" ctx.FuncName what)))
    let requireInv (what: string) (argE: Expr) =
        judge ctx env argE |> Result.bind (fun s ->
            match s with
            | Inv _ -> Ok ()
            | Rep _ ->
                Error (bl4008 argE.Span (sprintf "function '%s': %s must be invariant, but the argument is representation-typed -- extract invariants with ml.scalars/ml.norms or contract with ml.tensor_product" ctx.FuncName what))
            | Opaque ->
                Error (bl4008 argE.Span (sprintf "function '%s': cannot classify the argument to %s" ctx.FuncName what)))
    match f.Kind with
    // qualified ML ops (surface-visible pre-rewrite)
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) when Set.contains alias ctx.Aliases ->
        let specArg what e =
            specOfArg ctx.Statics what e
            |> Result.mapError (fun m -> bl4008 e.Span (sprintf "function '%s': %s: %s" ctx.FuncName what m))
        // A pg spec argument, decoded against the CERTIFICATE's table.
        // `pgSpecOfStatic` already prefixes `what`, so this does not repeat it.
        let pgSpecArg (grp: Blade.ML.PointSpec.PointGroup) (what: string) (e: Expr) =
            match staticArgValue ctx.Statics e with
            | Error m -> Error (bl4008 e.Span (sprintf "function '%s': %s: %s" ctx.FuncName what m))
            | Ok sv ->
                Blade.ML.Statics.pgSpecOfStatic what grp sv
                |> Result.mapError (fun m -> bl4008 e.Span (sprintf "function '%s': %s" ctx.FuncName m))
        match op, args with
        // The point-group arm: both arms below are GUARDED on a `Point`
        // certificate, so under equiv(O3)/equiv(SO3) every op falls through
        // to the arms it always did -- including `derive_pg_linear`, an
        // all-invariant computation to an O(3) certificate (nothing there
        // transforms under O(3)), so the default arm's `Ok Inv` is the
        // header's asymmetry at the op level.
        | _, _ when isPointGroup ctx.Group && List.contains op o3OpNames ->
            let gn = groupStr ctx.Group
            reject (sprintf "ml.%s is an O(3) operation -- it is stated in (l, parity, mult) irreps specs and its rep-typed arguments and results live in O(3) representation spaces, so it carries no %s-equivariance theorem this checker can use. Inside a `where ml.equiv(%s)` body build with the point-group ops (ml.derive_pg_linear) over PgIrrepsIdx<%s, SPEC> values"
                       op gn gn gn)
        // ml.derive_pg_linear(GROUP, SIN, SOUT, x, w), judged like
        // derive_linear: SIN on the input, an invariant weight buffer, SOUT
        // out. The extra premise is the GROUP argument, which must be the
        // certificate's own -- a C4 layer proves nothing about
        // D4-equivariance.
        | "derive_pg_linear", [ gE; sInE; sOutE; xE; wE ] when isPointGroup ctx.Group ->
            let gn = groupStr ctx.Group
            match pgGroupArgName ctx.Statics gE with
            | Error m ->
                Error (bl4008 gE.Span (sprintf "function '%s': derive_pg_linear GROUP: %s" ctx.FuncName m))
            | Ok argGroup when argGroup <> gn ->
                Error (bl4008 gE.Span (sprintf "function '%s': derive_pg_linear names point group %s, but this function is certified for %s -- the layer is %s-equivariant and says nothing about %s. Certificates do not transfer between groups"
                                           ctx.FuncName argGroup gn argGroup gn))
            | Ok _ ->
                let grp = Blade.ML.PointSpec.pointGroup gn
                pgSpecArg grp "derive_pg_linear SIN" sInE |> Result.bind (fun si ->
                pgSpecArg grp "derive_pg_linear SOUT" sOutE |> Result.bind (fun so ->
                    requireRep "derive_pg_linear input" (PgSpec (gn, si)) xE |> Result.bind (fun () ->
                    requireInv "derive_pg_linear weight buffer" wE |> Result.map (fun () ->
                        Rep (PgSpec (gn, so))))))
        | "derive_pg_linear", _ when isPointGroup ctx.Group ->
            reject (sprintf "%s: unrecognized call shape inside an equiv-certified body" op)
        | "y_to", [ lmaxE; xE; yE; zE ] ->
            requireInv "y_to coordinate x" xE |> Result.bind (fun () ->
            requireInv "y_to coordinate y" yE |> Result.bind (fun () ->
            requireInv "y_to coordinate z" zE |> Result.bind (fun () ->
                match staticArgValue ctx.Statics lmaxE with
                | Ok (SVInt lmax) when lmax >= 0L -> Ok (Rep (O3Spec (shSpec (int lmax))))
                | _ -> reject "y_to: lmax must be a static int")))
        | "tensor_product", [ cfgE; xE; yE; wE ] ->
            staticArgValue ctx.Statics cfgE
            |> Result.bind (Blade.ML.Statics.cfgOfStatic "tensor_product")
            |> Result.mapError (fun m -> bl4008 cfgE.Span (sprintf "function '%s': tensor_product: %s" ctx.FuncName m))
            |> Result.bind (fun cfg ->
                requireRep "tensor_product input 1" (O3Spec cfg.Spec1) xE |> Result.bind (fun () ->
                requireRep "tensor_product input 2" (O3Spec cfg.Spec2) yE |> Result.bind (fun () ->
                requireInv "tensor_product weight buffer" wE |> Result.map (fun () ->
                    Rep (O3Spec cfg.SpecOut)))))
        | "linear", [ sInE; sOutE; wE; xE ] ->
            specArg "linear specIn" sInE |> Result.bind (fun si ->
            specArg "linear specOut" sOutE |> Result.bind (fun so ->
                requireInv "linear weight buffer" wE |> Result.bind (fun () ->
                requireRep "linear input" (O3Spec si) xE |> Result.map (fun () -> Rep (O3Spec so)))))
        | "gated", [ specE; xE ] ->
            specArg "gated spec" specE |> Result.bind (fun spec ->
                if spec.IsEmpty || spec.Head.L <> 0 then
                    reject "gated: the first block must be scalars (L=0) -- the gates are read from it"
                // EVERY l=0 block, not just the head: gatedDecl silus every
                // (l=0) block in place, and silu is not sign-equivariant, so
                // ANY pseudoscalar l=0 block flips under improper rotations
                // while its gated value does not. The head-only check this
                // replaces was a live false accept (even gate block, odd
                // l=0 block elsewhere); the whole-spec sweep matches the
                // emitter's stamp predicate (MLElaborate.o3UnlessPseudoscalar)
                // and the sibling `scalars` arm.
                elif ctx.Group = O3 && spec |> List.exists (fun en -> en.L = 0 && en.Parity <> 0) then
                    reject "gated under equiv(O3): every (l=0) block is gated in place, and this spec has an (l=0, odd) block -- pseudoscalar cells flip under improper rotations while silu does not, breaking O(3) equivariance (SO3 admits them)"
                else
                    requireRep "gated input" (O3Spec spec) xE |> Result.map (fun () -> Rep (O3Spec spec)))
        | "scalars", [ specE; xE ] ->
            specArg "scalars spec" specE |> Result.bind (fun spec ->
                if ctx.Group = O3 && spec |> List.exists (fun en -> en.L = 0 && en.Parity = 1) then
                    reject "scalars under equiv(O3): the spec has (l=0, odd) blocks -- pseudoscalars flip under improper rotations and are not O(3) invariants (SO3 admits them)"
                else
                    requireRep "scalars input" (O3Spec spec) xE |> Result.map (fun () -> Inv (InvAgg (Some 1))))
        | "norms", [ specE; xE ] ->
            specArg "norms spec" specE |> Result.bind (fun spec ->
                requireRep "norms input" (O3Spec spec) xE |> Result.map (fun () -> Inv (InvAgg (Some 1))))
        | "derive_linear", [ sInE; sOutE; wE; xE ] ->
            specArg "derive_linear specIn" sInE |> Result.bind (fun si ->
            specArg "derive_linear specOut" sOutE |> Result.bind (fun so ->
                requireInv "derive_linear weight buffer" wE |> Result.bind (fun () ->
                requireRep "derive_linear input" (O3Spec si) xE |> Result.map (fun () -> Rep (O3Spec so)))))
        | "derive_tp", [ s1E; s2E; xE; yE; wE ] ->
            specArg "derive_tp spec1" s1E |> Result.bind (fun s1 ->
            specArg "derive_tp spec2" s2E |> Result.bind (fun s2 ->
                requireRep "derive_tp input 1" (O3Spec s1) xE |> Result.bind (fun () ->
                requireRep "derive_tp input 2" (O3Spec s2) yE |> Result.bind (fun () ->
                requireInv "derive_tp weight buffer" wE |> Result.map (fun () ->
                    Rep (O3Spec (tpSpec s1 s2)))))))
        // The S2-compacted self-TPs: one spec (both inputs), same derived
        // output spec as derive_tp(S, S, ...) -- a reparameterization of a
        // SUBSPACE of the same hom-space, so the judgment is derive_tp's
        // (group-agnostic); exchange symmetry is a property of the weights,
        // not of the equivariance claim.
        | "derive_sym_tp", [ specE; xE; yE; wE ] ->
            specArg "derive_sym_tp spec" specE |> Result.bind (fun s ->
                requireRep "derive_sym_tp input 1" (O3Spec s) xE |> Result.bind (fun () ->
                requireRep "derive_sym_tp input 2" (O3Spec s) yE |> Result.bind (fun () ->
                requireInv "derive_sym_tp weight buffer" wE |> Result.map (fun () ->
                    Rep (O3Spec (tpSpec s s))))))
        | "derive_alt_tp", [ specE; xE; yE; wE ] ->
            specArg "derive_alt_tp spec" specE |> Result.bind (fun s ->
                requireRep "derive_alt_tp input 1" (O3Spec s) xE |> Result.bind (fun () ->
                requireRep "derive_alt_tp input 2" (O3Spec s) yE |> Result.bind (fun () ->
                requireInv "derive_alt_tp weight buffer" wE |> Result.map (fun () ->
                    Rep (O3Spec (tpSpec s s))))))
        // derive_poly: the degree-K equivariant polynomial layer, judged like
        // derive_linear one degree up (input transforms as IrrepsIdx<SPEC>,
        // weight buffer invariant, result as the DECLARED output spec --
        // linear on Sym^K(V), whose coordinates never appear at the surface,
        // so ml.sym_lift's rep-exit problem does not arise). Group-agnostic:
        // parity rides at spec level (O(3) acts on Sym^j(V_{l,p}) by
        // (-1)^(j*p)), already accounted for by `sym_spec`.
        | "derive_poly", [ specE; kE; sOutE; xE; wE ] ->
            specArg "derive_poly SPEC" specE |> Result.bind (fun s ->
            specArg "derive_poly SOUT" sOutE |> Result.bind (fun sOut ->
                match staticArgValue ctx.Statics kE with
                | Ok (SVInt kk) when kk >= 1L && kk <= 4L ->
                    requireRep "derive_poly input" (O3Spec s) xE |> Result.bind (fun () ->
                    requireInv "derive_poly weight buffer" wE |> Result.map (fun () -> Rep (O3Spec sOut)))
                | _ -> reject "derive_poly: K must be a static int in 1..4"))
        // The monomial lift is a REP EXIT the lattice cannot name: its output
        // components are the degree-K monomials of the input's components,
        // co-rotating POLYNOMIALLY as Sym^K(V) = ml.sym_spec(SPEC, K), whose
        // action is not the block-diagonal one an `IrrepsIdx<spec>` value
        // carries here (the monomial basis is not the irreps basis). A
        // rep-typed input is rejected on that reason; an all-invariant call
        // is fine, since the monomials of an invariant are invariant.
        | "sym_lift", [ _; _; xE ] ->
            judge ctx env xE |> Result.bind (fun sx ->
                match sx with
                | Inv _ -> Ok (Inv (InvAgg (Some 1)))
                | Rep sp ->
                    Error (bl4008 xE.Span (sprintf "function '%s': ml.sym_lift's monomial coordinates are not representation-classified in the current checker -- the C(n+K-1, K) products of %s components co-rotate POLYNOMIALLY (as ml.sym_spec of that spec), not through a block-diagonal irreps action, so no {Rep spec, Inv, Opaque} status describes the result. Keep ml.sym_lift in uncertified assembly code for now; inside a certified body contract with ml.derive_sym_tp / ml.derive_tp / ml.tensor_product instead" ctx.FuncName (repStr sp)))
                | Opaque ->
                    Error (bl4008 xE.Span (sprintf "function '%s': cannot classify the argument to sym_lift" ctx.FuncName)))
        | ("derive_linear" | "derive_tp"), _ ->
            reject (sprintf "%s: inside an equiv-certified body use the full call form -- the 2-argument binding form is for uncertified assembly code" op)
        | ("linear_rows" | "gated_rows"), _ ->
            reject (sprintf "%s is not admitted in equiv-certified bodies (row-stacked buffers are not representation spaces); apply the single-vector op per row" op)
        // Cartesian bridges: rep-INTRODUCTION forms (the y_to shape).
        // Conditional premise: the invariant input really is the flat
        // row-major 3x3 (resp. packed symmetric) Cartesian tensor of a
        // physical rank-2 quantity.
        | "tensor_to_irreps", [ gE ] ->
            requireInv "tensor_to_irreps input (flat row-major 3x3 Cartesian tensor)" gE
            |> Result.map (fun () -> Rep (O3Spec Blade.ML.CartesianBridge.gradSpec))
        | "sym_to_irreps", [ sE ] ->
            requireInv "sym_to_irreps input (packed symmetric Cartesian tensor)" sE
            |> Result.map (fun () -> Rep (O3Spec Blade.ML.CartesianBridge.tauSpec))
        | "irreps_to_sym", _ ->
            reject "irreps_to_sym reads basis-dependent Cartesian components out of a representation -- a rep escape, for uncertified assembly code only (e.g. feeding a solver); inside a certified body stay in irreps space"
        | ("tensor_to_irreps" | "sym_to_irreps"), _ ->
            reject (sprintf "%s: unrecognized call shape inside an equiv-certified body" op)
        | ("tensor_product" | "linear" | "gated" | "scalars" | "norms" | "y_to" | "derive_sym_tp" | "derive_alt_tp" | "sym_lift" | "derive_poly"), _ ->
            reject (sprintf "%s: unrecognized call shape inside an equiv-certified body" op)
        | _ ->
            // other alias members (sizing etc. -- normalized already, so an
            // unnormalized leftover is unknown): invariant if args are.
            judgeAll args |> Result.bind (fun sts ->
                if sts |> List.forall isInv then Ok (Inv InvShapeUnknown)
                else reject (sprintf "ml.%s is not an equivariance-preserving operation on representation-typed values" op))
    // named callees
    | ExprKind.ExprVar fn ->
        match Map.tryFind fn ctx.Certs with
        | Some cert ->
            if cert.Group <> ctx.Group then
                reject (sprintf "call to '%s': it is certified for %s, this function for %s -- certificates do not transfer between groups" fn (groupStr cert.Group) (groupStr ctx.Group))
            elif List.length args <> List.length cert.Params then
                reject (sprintf "call to '%s': expected %d arguments" fn (List.length cert.Params))
            else
                (List.zip cert.Params args)
                |> List.fold (fun acc ((pName, pSt), argE) ->
                    acc |> Result.bind (fun () ->
                        match pSt with
                        | Rep sp -> requireRep (sprintf "'%s' parameter '%s'" fn pName) sp argE
                        | Inv _ -> requireInv (sprintf "'%s' parameter '%s'" fn pName) argE
                        | Opaque -> reject (sprintf "call to '%s': parameter '%s' is unclassifiable" fn pName)))
                    (Ok ())
                |> Result.map (fun () -> cert.Return)
        | None ->
            match Map.tryFind fn env with
            | Some (Rep spec) ->
                // indexing into a rep: admissible only at a static offset
                // inside an invariant (l=0) block.
                (match args with
                 | [ iE ] ->
                     (match evalExpr ctx.Statics fuel iE with
                      // one element of a rank-1 rep buffer: an invariant scalar
                      | Ok (SVInt i) when Set.contains (int i) (invariantOffsets ctx.Group spec) -> Ok (Inv InvScalar)
                      | Ok (SVInt _) ->
                          (match ctx.Group with
                           | Point gn ->
                               // Pseudoscalar asymmetry as table data: at D4
                               // the A2/B1/B2 cells are 1-DIMENSIONAL and
                               // still basis-dependent -- dimension is not
                               // the test, the trivial character is.
                               Error (bl4008 e.Span (sprintf "function '%s': raw indexing into a cell of '%s' outside a TRIVIAL-label block reads a basis-dependent number -- under equiv(%s) only the labels whose every generator matrix is the identity carry invariant cells, and a 1-dimensional label is not enough (%s's non-trivial characters flip under some generator, exactly as an O(3) pseudoscalar does under an improper rotation)" ctx.FuncName fn gn gn))
                           | _ ->
                               Error (bl4008 e.Span (sprintf "function '%s': raw indexing into an l>0 (or, under O3, parity-odd) component of '%s' reads a basis-dependent number -- extract invariants with ml.scalars/ml.norms or contract with ml.tensor_product" ctx.FuncName fn)))
                      | _ ->
                          (match ctx.Group with
                           | Point _ ->
                               Error (bl4008 e.Span (sprintf "function '%s': indexing into representation-typed '%s' requires a static offset inside a trivial-label block" ctx.FuncName fn))
                           | _ ->
                               Error (bl4008 e.Span (sprintf "function '%s': indexing into representation-typed '%s' requires a static offset inside an invariant (l=0) block" ctx.FuncName fn))))
                 | _ -> reject (sprintf "unsupported access into representation-typed '%s'" fn))
            | _ ->
                // uncertified callee (builtin, helper, plain array, lambda):
                // a function of invariants is invariant -- every argument
                // must be Inv, and reps must not escape.
                judgeAll args |> Result.bind (fun sts ->
                    match sts |> List.tryFindIndex (isInv >> not) with
                    | None ->
                        // Shape of the result. A full-rank read out of an
                        // invariant array of KNOWN rank is a scalar; a scalar
                        // builtin preserves its arguments' shape; anything
                        // else (helper call, partial index, unknown rank) has
                        // no established shape.
                        let argShapes = sts |> List.map (function Inv sh -> sh | _ -> InvShapeUnknown)
                        let sh =
                            match Map.tryFind fn env with
                            | Some (Inv (InvAgg (Some r))) when r = args.Length -> InvScalar
                            | Some (Inv _) -> InvShapeUnknown
                            | _ when fn.StartsWith "__ml_stat_" ->
                                (match evalExpr ctx.Statics fuel e with
                                 | Ok sv -> shapeOfStatic sv
                                 | Error _ -> InvShapeUnknown)
                            | _ when isKnownScalarBuiltin fn && not argShapes.IsEmpty ->
                                argShapes |> List.reduce binShape
                            | _ -> InvShapeUnknown
                        Ok (Inv sh)
                    | Some i ->
                        let argE = args.[i]
                        // `isInv >> not` catches BOTH failing statuses:
                        // `Rep` is established, so it keeps the escape
                        // messages verbatim; `Opaque` establishes NOTHING,
                        // so it gets requireRep/requireInv's vocabulary
                        // instead of naming a fact never proved.
                        match sts.[i] with
                        | Opaque when isKnownScalarBuiltin fn ->
                            Error (bl4008 argE.Span (sprintf "function '%s': cannot classify the argument to '%s' inside an equiv-certified body -- the judgment cannot rule out that it carries representation structure, and a nonlinearity may be applied only to invariants (ml.gated gates reps; ml.scalars/ml.norms extract invariants)" ctx.FuncName fn))
                        | Opaque ->
                            Error (bl4008 argE.Span (sprintf "function '%s': cannot classify the argument to '%s' inside an equiv-certified body -- the judgment cannot rule out that it carries representation structure, and '%s' carries no equiv certificate that would say what happens to it. Pass a value the judgment can classify, or certify '%s' with `where ml.equiv(%s)`" ctx.FuncName fn fn fn (groupStr ctx.Group)))
                        | _ when isKnownScalarBuiltin fn ->
                            Error (bl4008 argE.Span (sprintf "function '%s': applying '%s' to a representation-typed value is not equivariant -- nonlinearities act only on invariants (ml.gated gates reps; ml.scalars/ml.norms extract invariants)" ctx.FuncName fn))
                        | _ ->
                            Error (bl4008 argE.Span (sprintf "function '%s': representation-typed value escapes to '%s', which carries no equiv certificate -- certify it with `where ml.equiv(%s)` or pass only invariants" ctx.FuncName fn (groupStr ctx.Group))))
    | _ ->
        // computed callee over reps: not admissible
        judgeAll args |> Result.bind (fun sts ->
            judge ctx env f |> Result.bind (fun sf ->
                if isInv sf && sts |> List.forall isInv then Ok (Inv InvShapeUnknown)
                else reject "cannot classify this call inside an equiv-certified body"))

// The generator-based ENGINE, hooked on the rejection path. COMPOSITION RUNS
// FIRST -- cheaper, better diagnostics -- and never sees a body composition
// accepts. On a POINT-certified function's Error verdict, the engine tries
// the polynomial route: extraction FAILS, the original diagnostic surfaces
// untouched; extraction hits a CAP, it gets a note naming the cap; extraction
// and discharge PASS, the certificate HOLDS silently; discharge FAILS, the
// engine's own BL4008 REPLACES composition's diagnostic, naming the group
// element and first offending coefficient. `Propose subset-of Check-accept`
// is untouched, since the inference channel calls this same `judgeFunction`.
// TWO DISCHARGERS, ONE EXTRACTOR: `Point g` uses MLPolyExtract's FINITE
// discharge (the whole word set, pure rationals, no radicals); `O3`/`SO3`
// uses MLLieDischarge's RADICAL-VECTOR discharge (three so(3) generators,
// plus, under O3 only, the integer -I identity of pi_0). The only
// group-specific code is `pointActions`/`o3Actions`.

module private Engine =
    module PX = Blade.ML.PolyExtract
    module PS = Blade.ML.PointSpec
    module LD = Blade.ML.LieDischarge

    /// The scalar annotations whose SHAPE is decidable without a type-alias
    /// map. Anything else classified `Inv` becomes `PInvOpaque`: modelling an
    /// aliased array as one scalar atom would be unfaithful.
    let private scalarNames =
        [ "Float"; "Float32"; "Float64"; "Double"; "Int"; "Int32"; "Int64" ]

    let private invKind (t: TypeExpr option) : PX.ParamKind =
        match t with
        | Some (TyArray _) -> PX.PInvArray
        | Some (TyInt32 | TyInt64 | TyFloat32 | TyFloat64) -> PX.PInvScalar
        | Some (TyNamed (n, [])) when List.contains n scalarNames -> PX.PInvScalar
        | _ -> PX.PInvOpaque

    /// The real dimension of a rep payload, per block-spec member.
    let private repDim (r: RepSpec) : int option =
        match r with
        | PgSpec (g, s) -> Some (PS.pgTotalDim (PS.pointGroup g) s)
        | O3Spec s -> Some (totalDim s)

    /// The classified signature the extractor needs, or None when a parameter
    /// or the return is not describable (which is an extraction refusal).
    let polySig (cert: CertSig) (fd: FunctionDecl) : PX.PolySig option =
        let byName = fd.Params |> List.map (fun p -> (p.Name, p.Type)) |> Map.ofList
        let ps =
            cert.Params
            |> List.fold (fun acc (n, st) ->
                acc |> Option.bind (fun out ->
                    match st with
                    | Rep r -> repDim r |> Option.map (fun d -> out @ [ (n, PX.PRep d) ])
                    | Inv _ -> Some (out @ [ (n, invKind (defaultArg (Map.tryFind n byName) None)) ])
                    | Opaque -> None)) (Some [])
        ps |> Option.bind (fun ps ->
            match cert.Return with
            | Rep r -> repDim r |> Option.map (fun d -> PX.mkSig ps (Some d))
            | Inv _ -> Some (PX.mkSig ps None)
            | Opaque -> None)

    /// The word set as `ElementAction`s: ALL |G| <= 8 elements (word closure
    /// says the generators would suffice -- proofs/BladeWordClosure.v -- and
    /// at this size the redundancy is free).
    let pointActions (gn: string) (cert: CertSig) : PX.ElementAction list option =
        let grp = PS.pointGroup gn
        let pgOf (r: RepSpec) = match r with PgSpec (_, s) -> Some s | O3Spec _ -> None
        let repParams =
            cert.Params |> List.choose (fun (n, st) ->
                match st with Rep r -> Some (n, pgOf r) | _ -> None)
        if repParams |> List.exists (snd >> Option.isNone) then None
        else
            // A scalar (Inv) return is an INVARIANCE claim: the output
            // action is the 1x1 identity, i.e. the trivial rep.
            let outSpec = match cert.Return with Rep r -> pgOf r | Inv _ -> Some [] | Opaque -> None
            match outSpec with
            | None -> None
            | Some outS ->
                PS.groupElements grp
                |> List.map (fun el ->
                    let inMats =
                        repParams
                        |> List.map (fun (n, s) -> (n, PS.pgElementMatrix grp (Option.get s) el))
                        |> Map.ofList
                    let outMat =
                        match cert.Return with
                        | Inv _ -> [| [| 1 |] |]
                        | _ -> PS.pgElementMatrix grp outS el
                    PX.mkAction (PS.wordName grp el.Word) inMats outMat)
                |> Some

    /// The so(3) generators (and, under O3, the -I parity bookkeeping) as
    /// the radical-vector discharger consumes them. Same shape as
    /// `pointActions`. A scalar (Inv) return is an INVARIANCE claim, so the
    /// output action is the trivial rep: a 1x1 ZERO generator and parity
    /// EVEN -- which is what makes `-> Float` under equiv(O3) reject a
    /// pseudoscalar body while the same body certifies under equiv(SO3).
    let o3Actions (grp: Group) (cert: CertSig) : (LD.LieGenerator list * LD.InversionCheck option) option =
        let specOf (r: RepSpec) = match r with O3Spec s -> Some s | PgSpec _ -> None
        let repParams =
            cert.Params |> List.choose (fun (n, st) ->
                match st with Rep r -> Some (n, specOf r) | _ -> None)
        if repParams |> List.exists (snd >> Option.isNone) then None
        else
            let reps = repParams |> List.map (fun (n, s) -> (n, Option.get s))
            // `Some spec` -- a rep-typed return; `None` -- the trivial rep.
            let outSpec =
                match cert.Return with
                | Rep r -> specOf r |> Option.map Some
                | Inv _ -> Some None
                | Opaque -> None
            match outSpec with
            | None -> None
            | Some outS ->
                let gens =
                    LD.axes
                    |> List.map (fun ax ->
                        { LD.Name = LD.axisName ax
                          LD.InMats = reps |> List.map (fun (n, s) -> (n, LD.specGenerator ax s)) |> Map.ofList
                          LD.OutMat =
                            match outS with
                            | Some s -> LD.specGenerator ax s
                            | None -> [| [| LD.Radical.zero |] |] })
                let inv =
                    match grp with
                    | O3 ->
                        Some { LD.InPar = reps |> List.map (fun (n, s) -> (n, LD.specParity s)) |> Map.ofList
                               LD.OutPar = match outS with Some s -> LD.specParity s | None -> [| 0 |] }
                    | _ -> None
                Some (gens, inv)


/// Judge one certified function. Empty list = certificate holds. Two routes:
/// COMPOSITION (the abstract interpretation above), then -- only on its
/// rejection -- the polynomial ENGINE (see the `Engine` module's header for
/// the exact flow and why its verdict may replace composition's).
let judgeFunction (group: Group) (certs: Map<string, CertSig>) (statics: StaticEnv)
                  (globals: Map<string, InvShape>) (aliases: Set<string>) (fd: FunctionDecl)
    : Blade.Diagnostics.Diagnostic list =
    match Map.tryFind fd.Name certs with
    | None -> []
    | Some cert ->
        let ctx = { Group = group; FuncName = fd.Name; Aliases = aliases; Statics = statics; Certs = certs; Globals = globals }
        let env = cert.Params |> List.fold (fun m (n, st) -> Map.add n st m) Map.empty
        let composition =
            match judge ctx env fd.Body with
            | Error d -> [ d ]
            | Ok st ->
                if statusAgrees st cert.Return then []
                else
                    [ bl4008 fd.Body.Span
                          (sprintf "function '%s': the body is %s but the declared return type is %s -- the certificate requires them to agree" fd.Name (statusStr st) (statusStr cert.Return)) ]
        match composition, cert.Group with
        | [], _ -> []
        | (d :: _), Point gn ->
            // Total by construction: any escape from the registry or the
            // decoders reads as "the engine has nothing to say" and
            // composition's verdict stands.
            try
                match Engine.polySig cert fd, Engine.pointActions gn cert with
                | Some psig, Some actions ->
                    match Blade.ML.PolyExtract.extract psig statics fd with
                    | Error (Blade.ML.PolyExtract.OutsideFragment _) -> [ d ]
                    | Error (Blade.ML.PolyExtract.CapBreach why) ->
                        [ { d with Message = d.Message + EM.capNote fd.Name why } ]
                    | Ok form ->
                        match Blade.ML.PolyExtract.discharge form actions with
                        | Ok () -> []
                        | Error (Blade.ML.PolyExtract.DischargeCap why) ->
                            [ { d with Message = d.Message + EM.capNote fd.Name why } ]
                        | Error (Blade.ML.PolyExtract.GeneratorCheck f) ->
                            [ bl4008 fd.Body.Span (EM.failureMessage fd.Name gn f) ]
                | _ -> [ d ]
            with _ -> [ d ]
        | (d :: _), (O3 | SO3) ->
            // The SAME flow one discharger over, with ONE deliberate hole:
            // the post-accept float guard's `LieGuardFailure` is a
            // compiler-bug assert, not a decoder escape, so it is re-raised
            // rather than swallowed into "composition's verdict stands".
            try
                match Engine.polySig cert fd, Engine.o3Actions cert.Group cert with
                | Some psig, Some (gens, inv) ->
                    match Blade.ML.PolyExtract.extract psig statics fd with
                    | Error (Blade.ML.PolyExtract.OutsideFragment _) -> [ d ]
                    | Error (Blade.ML.PolyExtract.CapBreach why) ->
                        [ { d with Message = d.Message + EM.capNote fd.Name why } ]
                    | Ok form ->
                        match Blade.ML.LieDischarge.discharge form gens inv with
                        | Ok () -> []
                        | Error (Blade.ML.LieDischarge.DischargeCap why) ->
                            [ { d with Message = d.Message + EM.capNote fd.Name why } ]
                        | Error (Blade.ML.LieDischarge.GeneratorCheck f) ->
                            [ bl4008 fd.Body.Span (EM.lieFailureMessage fd.Name (groupStr cert.Group) f) ]
                        | Error (Blade.ML.LieDischarge.ParityCheck f) ->
                            [ bl4008 fd.Body.Span (EM.inversionFailureMessage fd.Name f) ]
                | _ -> [ d ]
            with
            | Blade.ML.LieDischarge.LieGuardFailure _ -> reraise ()
            | _ -> [ d ]

/// TEST HOOK for the DIFFERENTIAL obligation. Production runs the engine
/// only on composition's rejection path, so a body composition ACCEPTS is
/// never seen by it; this asks whether the two would agree if it were, by
/// running the engine alone. `None` = the engine has nothing to say (no
/// certificate, an unclassifiable signature, an extraction refusal or a cap
/// breach); `Some (Ok ())` = the polynomial discharges; `Some (Error msg)` =
/// it does not. Nothing in the compiler calls this -- it lives here rather
/// than in the test file because `Engine` is private and must stay so.
let engineVerdict (certs: Map<string, CertSig>) (statics: StaticEnv) (fd: FunctionDecl)
    : Result<unit, string> option =
    match Map.tryFind fd.Name certs with
    | None -> None
    | Some cert ->
        match Engine.polySig cert fd with
        | None -> None
        | Some psig ->
            match Blade.ML.PolyExtract.extract psig statics fd with
            | Error _ -> None
            | Ok form ->
                match cert.Group with
                | Point gn ->
                    match Engine.pointActions gn cert with
                    | None -> None
                    | Some actions ->
                        match Blade.ML.PolyExtract.discharge form actions with
                        | Ok () -> Some (Ok ())
                        | Error (Blade.ML.PolyExtract.DischargeCap _) -> None
                        | Error (Blade.ML.PolyExtract.GeneratorCheck f) ->
                            Some (Error (EM.failureMessage fd.Name gn f))
                | O3 | SO3 ->
                    match Engine.o3Actions cert.Group cert with
                    | None -> None
                    | Some (gens, inv) ->
                        match Blade.ML.LieDischarge.discharge form gens inv with
                        | Ok () -> Some (Ok ())
                        | Error (Blade.ML.LieDischarge.DischargeCap _) -> None
                        | Error (Blade.ML.LieDischarge.GeneratorCheck f) ->
                            Some (Error (EM.lieFailureMessage fd.Name (groupStr cert.Group) f))
                        | Error (Blade.ML.LieDischarge.ParityCheck f) ->
                            Some (Error (EM.inversionFailureMessage fd.Name f))

// The inference channel -- BL4011. Deduction mode is the CHECKING judgment
// run speculatively: hypothesize `where ml.equiv(G)` on a function that
// lacks it, run `certSigOf` + `judgeFunction` verbatim, and if the
// certificate holds, PROPOSE the pin as a warning. Nothing here is a new
// rule, so `Propose subset-of Check-accept` holds BY CONSTRUCTION.
//
// DEPENDENCY THREADING is Deduce.fs's resolver discipline transplanted:
// declarations fold in DECL ORDER against a speculative table holding every
// real certificate plus every speculative certificate already inferred THIS
// pass for an EARLIER declaration under the SAME group. No summary proves
// itself (a self-recursive body is skipped outright), and there is no
// fixpoint iteration: a forward call to a later uncertified function
// resolves to nothing, silently and correctly. Every BL4011 names its
// dependency closure, since a proposal may REST on other unwritten pins.
//
// The group is recorded under the STRONGEST passer only: O(3) is a superset
// of SO(3), but `judgeApp` refuses a cross-group call in BOTH directions, so
// a function proposed for O3 must NOT appear in the SO3 speculative table.

/// The inference suggestion side-channel, mirroring `TypeCheck.PinSuggestions`.
/// Each entry is (message, decl span) so editor tooling can ghost-render the
/// pin at its function. Reset by `MLElaborate.expand`, read by
/// `TypeCheck.typeCheck` (string twins into the warning list) and
/// `Ide.ideCheck` (structured, `warning[BL4011]`).
module CertSuggestions =
    let private slot = new System.Threading.AsyncLocal<(string * Span) list>()
    let reset () = slot.Value <- []
    let add (msg: string) (span: Span) = slot.Value <- (msg, span) :: slot.Value
    let get () : (string * Span) list =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value

/// The STRUCTURED twin of the suggestion strings, as data. `Discipline` is
/// "equiv" | "galilean"; `Group` is the group name for equiv and the
/// comma-joined velocity parameters for galilean; `Deps` is the dependency
/// closure the proposal RESTS on, in decl order. Surfaced by `ide check
/// --json` as `deduced[]` entries.
type CertFact = {
    Owner: string
    Discipline: string
    Group: string
    Deps: string list
}

module CertFacts =
    let private slot = new System.Threading.AsyncLocal<(CertFact * Span) list>()
    let reset () = slot.Value <- []
    let add (f: CertFact) (span: Span) = slot.Value <- (f, span) :: slot.Value
    let get () : (CertFact * Span) list =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value

/// Which index families a signature annotation MENTIONS -- the syntactic
/// gate that picks the candidate groups. Deliberately a mirror of
/// `statusOfType`'s REACH: a family this scan cannot see is a family the
/// classifier would not have classified `Rep` either.
let rec private sigFamilies (aliases: Map<string, TypeExpr>) (t: TypeExpr) : bool * Set<string> =
    match t with
    | TyArray (_, idxs) ->
        idxs
        |> List.fold (fun (ir, pgs) i ->
            match i with
            | TyIrrepsIdx _ -> (true, pgs)
            | TyPgIrrepsIdx (gn, _) -> (ir, Set.add gn pgs)
            | _ -> (ir, pgs)) (false, Set.empty)
    | TyNamed (n, []) ->
        match Map.tryFind n aliases with
        | Some body -> sigFamilies aliases (TyArray (TyNamed ("Float", []), [ body ]))
        | None -> (false, Set.empty)
    | TyIrrepsIdx _ -> (true, Set.empty)
    | TyPgIrrepsIdx (gn, _) -> (false, Set.singleton gn)
    // NO TyBounded arm, deliberately, even though `statusOfType` grew one:
    // the only signature where a bound hides an index family here is a bound
    // on an INDEX-TYPE ALIAS (`v: A<min=0, max=1>`, `type A = IrrepsIdx<S>`),
    // and that form is already broken (the bounded spelling slips past the
    // bare-`v: A` refusal and lowers to the tagged SCALAR `Nat<A>`, so every
    // call site dies on a rank mismatch). Chasing the bound would make the
    // channel propose `where ml.equiv(O3)` for a program that cannot
    // type-check at all; proposing nothing is the harmless failure mode.
    | _ -> (false, Set.empty)

/// Candidate groups for a signature, STRONGEST FIRST: any `IrrepsIdx`
/// annotation -> O3, then SO3 (mixed signatures land here too, since a
/// `PgIrrepsIdx` buffer classifies `Inv` under an O(3) certificate, which
/// subsumes it); `PgIrrepsIdx<g, _>` and NO `IrrepsIdx` -> `Point g`, only
/// when the signature names ONE registered group. Galilean and S_n inference
/// are deliberately absent: `perm_equiv`'s flat-extent keying is ambiguous
/// at a signature, so guessing N from an `Array<_ like Idx<n>>` would
/// propose noise.
let private candidatesFor (aliases: Map<string, TypeExpr>) (fd: FunctionDecl) : Group list =
    let tys = (fd.Params |> List.choose (fun p -> p.Type)) @ Option.toList fd.ReturnType
    let (ir, pgs) =
        tys
        |> List.fold (fun (a, b) t ->
            let (x, y) = sigFamilies aliases t
            (a || x, Set.union b y)) (false, Set.empty)
    if ir then [ O3; SO3 ]
    else
        match Set.toList pgs with
        | [ gn ] when List.contains gn Blade.ML.PointSpec.pointGroupNames -> [ Point gn ]
        | _ -> []

/// How a proposed signature READS in the suggestion -- the same vocabulary
/// `statusStr` uses, compressed to one line.
let private sigSummary (cs: CertSig) : string =
    let one (n, st) =
        match st with
        | Rep r -> sprintf "%s transforms as %s" n (repStr r)
        | Inv _ -> sprintf "%s invariant" n
        | Opaque -> sprintf "%s unclassifiable" n
    let ps =
        if cs.Params.IsEmpty then "(no parameters)"
        else cs.Params |> List.map one |> String.concat ", "
    let ret =
        match cs.Return with
        | Rep r -> repStr r
        | Inv _ -> "invariant"
        | Opaque -> "unclassifiable"
    sprintf "%s -> %s" ps ret

/// One candidate attempt: hypothesize the group, classify the signature and
/// run the judgment against `table` (real certificates + this group's
/// speculative ones). `Some cert` = the certificate holds. Total by
/// construction: a `failwith` from the spec decoders reads as "no proposal"
/// rather than crashing a compiling program.
let private tryCandidate (g: Group) (typeAliases: Map<string, TypeExpr>) (statics: StaticEnv)
                         (globals: Map<string, InvShape>) (mlAliases: Set<string>)
                         (table: Map<string, CertSig>) (fd: FunctionDecl)
    : CertSig option =
    try
        match certSigOf g typeAliases statics fd with
        | Error _ -> None
        | Ok cs ->
            // THE NON-VACUITY FILTER: a signature with nothing rep-typed
            // proposes nothing. `equiv(G)` on a scalar helper is vacuously
            // true and says nothing about any group action, so proposing it
            // would be noise with a theorem's face on.
            let isRep st = match st with Rep _ -> true | _ -> false
            if not ((cs.Params |> List.exists (snd >> isRep)) || isRep cs.Return) then None
            else
                match judgeFunction g (Map.add fd.Name cs table) statics globals mlAliases fd with
                | [] -> Some cs
                | _ :: _ -> None
    with _ -> None

/// Run the shipped judgment speculatively over a module's declarations and
/// return the BL4011 suggestions, in decl order. Never fails, never changes a
/// verdict: the caller records these as warnings and compiles exactly as it
/// would have.
let inferCertificates (statics: StaticEnv) (mlAliases: Set<string>)
                      (certs: Map<string, CertSig>) (decls: Located<Decl> list)
    : (string * Span) list =
    let typeAliases = aliasMapOf decls
    // Speculative certificates, their dependency closures, and the DECL ORDER
    // in which they were inferred -- all keyed by group name.
    let mutable spec : Map<string, Map<string, CertSig>> = Map.empty
    let mutable deps : Map<string, Map<string, string list>> = Map.empty
    let mutable order : Map<string, string list> = Map.empty
    let mutable out : (string * Span) list = []
    for d in decls do
        match d.Value with
        | DeclFunction fd when (conjunctsOf "__ml_equiv" fd).IsEmpty
                               && not (Map.containsKey fd.Name certs) ->
            let bound = Set.ofList (fd.Params |> List.map (fun p -> p.Name))
            let free = freeVars bound fd.Body
            // No summary proves itself: a body that names its own function
            // would be judged against its own hypothesis, which is exactly the
            // circularity Deduce.fs's resolver refuses. Skip; silence.
            if not (Set.contains fd.Name free) then
                let candidates = candidatesFor typeAliases fd
                // STRONGEST FIRST, and only the strongest passer is proposed.
                let hit =
                    candidates
                    |> List.tryPick (fun g ->
                        let gs = groupStr g
                        let specG = defaultArg (Map.tryFind gs spec) Map.empty
                        let table = specG |> Map.fold (fun m k v -> Map.add k v m) certs
                        let globals = buildGlobalShapes g statics decls
                        tryCandidate g typeAliases statics globals mlAliases table fd
                        |> Option.map (fun cs -> (g, gs, specG, cs)))
                match hit with
                | None -> ()
                | Some (_, gs, specG, cs) ->
                    // The dependency closure: which speculative pins this
                    // proposal RESTS on. Direct deps are the earlier
                    // speculatively-certified names the body reads; the
                    // closure adds each of those proposals' own deps
                    // (already computed -- decl order guarantees it).
                    let depsG = defaultArg (Map.tryFind gs deps) Map.empty
                    let orderG = defaultArg (Map.tryFind gs order) []
                    let direct = orderG |> List.filter (fun n -> Set.contains n free)
                    let closure =
                        direct
                        |> List.collect (fun n -> n :: defaultArg (Map.tryFind n depsG) [])
                        |> List.distinct
                    // Rendered in DECL order, not alphabetically -- it reads
                    // as the order the pins would be written in.
                    let ordered = orderG |> List.filter (fun n -> List.contains n closure)
                    let closureNote =
                        if ordered.IsEmpty then ""
                        else sprintf " (also requires pinning: %s)" (String.concat ", " ordered)
                    let msg =
                        sprintf "function '%s' judges equivariant under %s: add 'where ml.equiv(%s)' [signature: %s]%s"
                            fd.Name gs gs (sigSummary cs) closureNote
                    out <- (msg, d.Span) :: out
                    // The STRUCTURED twin of the very same proposal: same
                    // owner, group, closure and span. Emitted here rather
                    // than rebuilt by a consumer so the string and the fact
                    // can never disagree about what was proved.
                    CertFacts.add { Owner = fd.Name; Discipline = "equiv"; Group = gs; Deps = ordered } d.Span
                    spec <- Map.add gs (Map.add fd.Name cs specG) spec
                    deps <- Map.add gs (Map.add fd.Name closure depsG) deps
                    order <- Map.add gs (orderG @ [ fd.Name ]) order
        | _ -> ()
    // THE STRONGER-GROUP UPGRADE LINT. A function pinned `ml.equiv(SO3)`
    // whose body ALSO judges under O3 carries a weaker theorem than proved:
    // O(3) is a superset of SO(3), so the O3 certificate is strictly
    // stronger (it adds the improper-rotation half, the -I obligation the
    // engine discharges). This proposes EDITING the pin, not adding one, so
    // it emits a string only, no CertFact, hypothesized exactly as
    // `tryCandidate` runs every other one (real certificate table, this
    // function's entry replaced by its O3 classification).
    //
    // THE GUARD: certificates do not transfer between groups -- `judgeApp`
    // refuses a cross-group call in BOTH directions -- so editing this pin
    // to O3 would break every CERTIFIED caller today. The lint is therefore
    // suppressed as soon as the name occurs free in any OTHER
    // declared-certified body.
    let certifiedFree =
        decls
        |> List.choose (fun d ->
            match d.Value with
            | DeclFunction fd when Map.containsKey fd.Name certs ->
                Some (fd.Name, freeVars (Set.ofList (fd.Params |> List.map (fun p -> p.Name))) fd.Body)
            | _ -> None)
    for d in decls do
        match d.Value with
        | DeclFunction fd ->
            match Map.tryFind fd.Name certs with
            | Some cert ->
                match cert.Group with
                | SO3 ->
                    let usedByAnotherCert =
                        certifiedFree
                        |> List.exists (fun (n, fv) -> n <> fd.Name && Set.contains fd.Name fv)
                    if not usedByAnotherCert then
                        let globals = buildGlobalShapes O3 statics decls
                        match tryCandidate O3 typeAliases statics globals mlAliases certs fd with
                        | Some _ ->
                            let msg =
                                sprintf "function '%s' is pinned ml.equiv(SO3) but judges under O3: the stronger certificate is available"
                                    fd.Name
                            out <- (msg, d.Span) :: out
                        | None -> ()
                | _ -> ()
            | None -> ()
        | _ -> ()
    List.rev out

// Constraint-registry handler

/// `equiv(G)` is a callee-side theorem: Validate re-checks the conjunct
/// shape (the elaborator has already judged the body by the time
/// checkFunctionDecl runs), the license scope is unused, and call sites
/// carry no obligation.
let private equivHandler : Blade.Constraints.ConstraintHandler = {
    Describe = "equiv(G) -- certifies the function equivariant under G (O3, SO3, or a registered point group such as C4 / D4); the ML elaborator proves the body composes only equivariance-preserving operations"
    Validate = fun funcName _ args ->
        parseGroup funcName args |> Result.map ignore
    EnterBody = fun _ _ -> ()
    ExitBody = fun _ _ -> ()
    Discharge = fun _ _ _ -> Ok ()
}

let mutable private registered = false

let register () =
    if not registered then
        registered <- true
        Blade.Constraints.registerConstraint "__ml_equiv" equivHandler
