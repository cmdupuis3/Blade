/// THE POLYNOMIAL EXTRACTOR AND FINITE (WORD-SET) DISCHARGE, the
/// generator-based certification engine's front half. MLEquiv checks that a
/// body COMPOSES equivariance-preserving operations; this module checks that
/// it IS an equivariant map, by normalizing it to an exact polynomial and
/// testing the equivariance identity coefficientwise. Complementary: this
/// one runs only where MLEquiv has already said no.
///
/// DELIBERATE POLARITY DIVERGENCE (read before diffing the three walkers).
/// MLEquiv's aggregate rule REJECTS a rep-typed value packed into a literal
/// aggregate, and its access rule REJECTS a raw index into a non-trivial-label
/// cell. THIS FILE ADMITS BOTH, on purpose:
///
///     function rot(x: Array<Float like PgIrrepsIdx<C4, [("E", 1)]>>)
///              where ml.equiv(C4) -> Array<Float like PgIrrepsIdx<C4, [("E", 1)]>>
///         = [0.0 - x(1), x(0)]
///
/// is exactly the shape composition refuses (raw component reads assembled
/// into an array literal) and exactly the shape this engine exists to
/// certify: composition is a sound-and-cheap SYNTACTIC sufficient condition,
/// and when it fails this engine asks the SEMANTIC question directly. A
/// three-way diff of MLEquiv/MLGalilean/MLPerm against this file must read
/// the mismatch as design, not drift -- those three are abstract
/// interpretations over a status lattice, this one is a normalizer over
/// Q[rep components][invariant atoms] -- and an admitted shape still has to
/// PASS the discharge below, a stronger obligation than the lattice imposed.
///
/// THE NORMAL FORM. A body becomes one `Poly` per output component (a scalar
/// return is a one-component vector). A `Poly` is a sparse `Mono -> Rat` map
/// with no zero coefficients; a `Mono` is a pair of exponent maps: REP
/// exponents over (parameter name, component index), the variables the
/// group acts on, and INV exponents over `InvAtom`s, an invariant parameter
/// or one static cell of an invariant array. Invariants are held fixed by
/// the certificate's hypothesis, so they enter as opaque transcendental
/// atoms and the coefficient ring is Q[atoms] rather than Q -- without that,
/// `w * cross(u, v)` and every weighted hand-written layer would be out of
/// scope.
///
/// EXACTNESS, TWO CONVENTIONS. A float literal enters as its exact dyadic
/// value (the rational the IEEE double actually denotes: `0.5` is 1/2, `0.1`
/// is 3602879701896397/36028797018963968, not 1/10); a literal division is
/// evaluated exactly in Q, so `3.0 / 10.0` IS 3/10. The two differ on
/// purpose: a coefficient meant to be a rational must be WRITTEN as the
/// division. The near-miss note on a failed discharge (`nearMiss` below)
/// catches the trap: it fires when the residual's float image is negligible
/// against the coefficient scale, i.e. the author wrote a truncated decimal
/// for an exact value. A `let static` binding is folded by the static
/// evaluator before the judgment seam, in floating point, so it enters here
/// as the exact dyadic image of that fold; only source-level division inside
/// the certified body gets the exact-Q treatment.
///
/// THE V1 CLOSED-WORLD FRAGMENT. Admitted, and nothing else: (1) literals,
/// dyadic-exact; (2) static indexing into a Rep or Inv parameter; (3) scalar
/// + - *, and / by a nonzero literal-or-static constant (Q[atoms] has no
/// inverses, so an atom divisor is out of the ring); (4) whole-array + -
/// between equal-length vectors, and invariant*array (a scalar factor of
/// rep-degree 0); (5) `let` (binding expression or block statements);
/// (6) array literals of scalar polynomials, as the assembled return.
/// Everything else -- calls, loops, mutation, `if`, `match`, lambdas, field
/// access, nested aggregates -- is outside the fragment and yields
/// `OutsideFragment`, on which the engine stays silent and the composition
/// verdict surfaces untouched. Two conveniences beyond the literal list:
/// unary `-` (scalar/array subtraction from zero), and indexing into any
/// vector-valued BINDING rather than a parameter alone.
///
/// CAPS: total rep-degree <= 4 and <= 100000 expanded terms, enforced during
/// extraction and during the discharge's substitution. A breach returns
/// `CapBreach`, whose note the caller appends to the composition diagnostic.
///
/// GROUP-AGNOSTIC BY CONSTRUCTION. Nothing below knows what a point group,
/// an irrep or a spec is. The discharge consumes a list of `ElementAction`s
/// (a name, one integer matrix per rep parameter, one output matrix), so
/// stage 6c's radical-vector Lie discharger is a second consumer of the same
/// normal form, not a second extractor. The finite half checks ALL |G| <= 8
/// elements rather than the generators: cheap, simpler, and the Coq lemma
/// (proofs/BladeWordClosure.v) proves the generator checks would suffice.
module Blade.ML.PolyExtract

open System.Numerics
open Blade.Ast
open Blade.StaticEval

// Minimal exact rationals over BigInteger, a local copy of the SymPowerTables
// pattern (must not drag Sym-power tables or MLSpec in front of MLEquiv in
// the build order). Always normalized, so structural equality is value equality.

type Rat = { Num: bigint; Den: bigint }

[<RequireQualifiedAccess>]
module Rat =
    let make (n: bigint) (d: bigint) : Rat =
        if d.IsZero then failwith "internal: MLPolyExtract rational with zero denominator"
        let n, d = if d.Sign < 0 then -n, -d else n, d
        let g = BigInteger.GreatestCommonDivisor(n, d)
        if g.IsOne then { Num = n; Den = d } else { Num = n / g; Den = d / g }
    let zero = { Num = BigInteger.Zero; Den = BigInteger.One }
    let one = { Num = BigInteger.One; Den = BigInteger.One }
    let ofBigInt (n: bigint) = { Num = n; Den = BigInteger.One }
    let ofInt (n: int) = ofBigInt (bigint n)
    let isZero (a: Rat) = a.Num.IsZero
    let add (a: Rat) (b: Rat) = make (a.Num * b.Den + b.Num * a.Den) (a.Den * b.Den)
    let sub (a: Rat) (b: Rat) = make (a.Num * b.Den - b.Num * a.Den) (a.Den * b.Den)
    let mul (a: Rat) (b: Rat) = make (a.Num * b.Num) (a.Den * b.Den)
    let div (a: Rat) (b: Rat) =
        if b.Num.IsZero then failwith "internal: MLPolyExtract rational division by zero"
        make (a.Num * b.Den) (a.Den * b.Num)
    let neg (a: Rat) = { a with Num = -a.Num }
    let toFloat (a: Rat) = float a.Num / float a.Den

    /// The exact dyadic value of an IEEE double, read off the bit pattern
    /// rather than through any decimal round-trip. `None` at NaN/+-inf.
    let tryOfFloatExact (f: float) : Rat option =
        if System.Double.IsNaN f || System.Double.IsInfinity f then None
        elif f = 0.0 then Some zero
        else
            let bits = System.BitConverter.DoubleToInt64Bits f
            let negative = bits < 0L
            let expo = int ((bits >>> 52) &&& 0x7FFL)
            let frac = bits &&& 0xFFFFFFFFFFFFFL
            // Subnormals carry no implicit leading bit and sit at 2^-1074.
            let mantissa, e2 =
                if expo = 0 then bigint frac, -1074
                else bigint (frac ||| 0x10000000000000L), expo - 1075
            let m = if negative then -mantissa else mantissa
            let two = BigInteger(2)
            if e2 >= 0 then Some (ofBigInt (m * BigInteger.Pow(two, e2)))
            else Some (make m (BigInteger.Pow(two, -e2)))

    /// How a coefficient reads in a diagnostic: an integer when it is one,
    /// `n/d` otherwise.
    let render (a: Rat) : string =
        if a.Den.IsOne then string a.Num else sprintf "%O/%O" a.Num a.Den

/// An invariant quantity the group does not move: a whole invariant scalar
/// parameter (`Index = None`) or one statically-indexed cell of an invariant
/// array. Opaque -- never evaluated or compared, only carried.
type InvAtom = { Name: string; Index: int option }

/// A monomial: exponents over rep components (parameter name, component
/// index) and over invariant atoms. Zero exponents are never stored, so
/// structural equality is monomial equality and the empty monomial is 1.
type Mono = {
    Rep: Map<string * int, int>
    Inv: Map<InvAtom, int>
}

/// A sparse polynomial. INVARIANT: no entry carries a zero coefficient, so
/// `Map.isEmpty` is "is the zero polynomial".
type Poly = Map<Mono, Rat>

[<RequireQualifiedAccess>]
module Mono =
    let one : Mono = { Rep = Map.empty; Inv = Map.empty }

    let repDegree (m: Mono) : int = m.Rep |> Map.fold (fun acc _ e -> acc + e) 0

    let mul (a: Mono) (b: Mono) : Mono =
        { Rep = b.Rep |> Map.fold (fun acc k e -> acc |> Map.change k (fun c -> Some (defaultArg c 0 + e))) a.Rep
          Inv = b.Inv |> Map.fold (fun acc k e -> acc |> Map.change k (fun c -> Some (defaultArg c 0 + e))) a.Inv }

    let repVar (p: string) (i: int) : Mono = { one with Rep = Map.ofList [ ((p, i), 1) ] }
    let invAtom (a: InvAtom) : Mono = { one with Inv = Map.ofList [ (a, 1) ] }

    /// How a monomial reads in a diagnostic: `x(0)^2 * w(3)`, or `1` for the
    /// constant. Rep factors first, in key order, so rendering is deterministic.
    let render (m: Mono) : string =
        let pow (s: string) (e: int) = if e = 1 then s else $"{s}^{e}"
        let reps = [ for KeyValue ((p, i), e) in m.Rep -> pow $"{p}({i})" e ]
        let invs =
            [ for KeyValue (a, e) in m.Inv ->
                pow (match a.Index with Some i -> $"{a.Name}({i})" | None -> a.Name) e ]
        match reps @ invs with
        | [] -> "1"
        | fs -> String.concat " * " fs

[<RequireQualifiedAccess>]
module Poly =
    let zero : Poly = Map.empty

    let ofRat (c: Rat) : Poly = if Rat.isZero c then Map.empty else Map.ofList [ (Mono.one, c) ]

    let ofMono (m: Mono) : Poly = Map.ofList [ (m, Rat.one) ]

    let terms (p: Poly) : int = Map.count p

    let repDegree (p: Poly) : int =
        p |> Map.fold (fun acc m _ -> max acc (Mono.repDegree m)) 0

    /// Add one term, pruning an exact cancellation (the zero-coefficient
    /// invariant is what makes `Map.isEmpty` mean "zero polynomial").
    let addTerm (m: Mono) (c: Rat) (p: Poly) : Poly =
        if Rat.isZero c then p
        else
            match Map.tryFind m p with
            | None -> Map.add m c p
            | Some old ->
                let s = Rat.add old c
                if Rat.isZero s then Map.remove m p else Map.add m s p

    let add (a: Poly) (b: Poly) : Poly = b |> Map.fold (fun acc m c -> addTerm m c acc) a
    let neg (a: Poly) : Poly = a |> Map.map (fun _ c -> Rat.neg c)
    let sub (a: Poly) (b: Poly) : Poly = b |> Map.fold (fun acc m c -> addTerm m (Rat.neg c) acc) a
    let scale (c: Rat) (a: Poly) : Poly =
        if Rat.isZero c then zero else a |> Map.map (fun _ x -> Rat.mul c x)

    let mul (a: Poly) (b: Poly) : Poly =
        let mutable out = zero
        for KeyValue (ma, ca) in a do
            for KeyValue (mb, cb) in b do
                out <- addTerm (Mono.mul ma mb) (Rat.mul ca cb) out
        out

    /// The constant a polynomial is, when it is one (no rep components and no
    /// invariant atoms) -- the only shape admissible as a divisor.
    let asConstant (p: Poly) : Rat option =
        if Map.isEmpty p then Some Rat.zero
        else
            match Map.toList p with
            | [ (m, c) ] when Map.isEmpty m.Rep && Map.isEmpty m.Inv -> Some c
            | _ -> None

    /// Every monomial of two polynomials, in a DETERMINISTIC order (rep degree
    /// ascending, then rendered form) -- diagnostics name "the first offending
    /// coefficient", so order must be a property of the polynomials, not a hash walk.
    let unionMonos (a: Poly) (b: Poly) : Mono list =
        Set.union (a |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                  (b |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
        |> Set.toList
        |> List.sortBy (fun m -> (Mono.repDegree m, Mono.render m))

    let coeff (m: Mono) (p: Poly) : Rat = defaultArg (Map.tryFind m p) Rat.zero

/// The caps. Degree is TOTAL rep degree -- invariant atoms are constants and
/// do not count, since the equivariance identity splits per rep degree, not
/// per atom degree.
let maxRepDegree = 4
let maxTerms = 100_000

type ExtractError =
    /// A construct outside the v1 closed-world fragment, or a shape the
    /// normal form cannot represent. The engine is SILENT on this.
    | OutsideFragment of string * Span
    /// A cap breach. The composition diagnostic surfaces WITH this note
    /// appended, so a body that fell off the engine says so.
    | CapBreach of string

/// What a parameter is, as far as the normal form cares. `PRep n` is a
/// rep-typed array with n real components (the group moves them); the
/// `PInv*` cases are held fixed, split by whether the parameter reads as one
/// atom or a family of statically-indexed atoms. `PInvOpaque` is the
/// conservative arm (shape undecidable from the caller's classifier) and
/// refuses every operation -- modelling an array as a scalar atom would be
/// unfaithful, and faithfulness is what the engine's soundness rests on.
type ParamKind =
    | PRep of int
    | PInvScalar
    | PInvArray
    | PInvOpaque

type PolySig = {
    Params: (string * ParamKind) list
    /// `Some n` -- a rep-typed array return of n components; `None` -- a
    /// scalar, which the discharge treats as the trivial rep (invariance).
    ReturnDim: int option
}

/// A constructor, so a caller never has to qualify a record label across a
/// module boundary.
let mkSig (ps: (string * ParamKind) list) (ret: int option) : PolySig =
    { Params = ps; ReturnDim = ret }

// THE VALUE ALGEBRA -- public, because there are two front halves. `Val`,
// `Budget`, `charge`, `chargeVec` and `binOp` are the part of the extractor
// that does NOT walk syntax: given two abstract values and an operator, what
// polynomial comes out and whether it fits under the caps. A divergence
// between two copies of this would be a SOUNDNESS bug, not a recall
// difference (a wrong extraction makes the discharge certify a polynomial
// that is not the body), which is why it is public here rather than
// restated in `MLPolyExtractTyped`. The two walkers' *contexts* stay
// separate -- this module's carries a `StaticEnv` to fold `let static`
// reads, the typed one has none left to fold by typecheck -- so the shared
// surface is keyed on a bare `Budget` cell, the only context the algebra touches.

/// The extractor's abstract value. Deliberately NOT a status lattice: every
/// case carries the actual polynomial data, since the verdict comes from the
/// coefficients and not from a type.
type Val =
    | VScalar of Poly
    /// An assembled vector of scalar polynomials: a rep parameter, an array
    /// literal, or anything built from those by the array arms.
    | VVec of Poly []
    /// An invariant ARRAY parameter. It has no polynomial of its own -- only
    /// its statically-indexed cells do, and each of those is an atom.
    | VInvArr of string
    /// An invariant of undecided shape: every use leaves the fragment.
    | VOpaque of string

/// The term budget, shared across a whole extraction so a body cannot dodge
/// the cap by spreading the blow-up over many components. A mutable cell so
/// both front halves can hand the same budget to the shared algebra without
/// agreeing on a context type.
type Budget = { mutable Remaining: int }

let mkBudget () : Budget = { Remaining = maxTerms }

let private fuel = 100_000

let private outside (msg: string) (span: Span) = Error (OutsideFragment (msg, span))

/// Charge a freshly built polynomial against the caps -- checked here and
/// nowhere else. The `CapBreach` strings ARE user-visible
/// (`MLEquiv.judgeFunction` appends them via `EquivMessages.capNote`) and
/// are pinned by tests/corpus/diagnostics/024.
let charge (bud: Budget) (p: Poly) : Result<Poly, ExtractError> =
    let n = Poly.terms p
    if n > maxTerms then
        Error (CapBreach $"the expanded form exceeded the {maxTerms}-term cap")
    elif Poly.repDegree p > maxRepDegree then
        Error (CapBreach $"the body's degree in the representation components exceeds the degree-{maxRepDegree} cap")
    else
        bud.Remaining <- bud.Remaining - n
        if bud.Remaining < 0 then
            Error (CapBreach $"the expanded form exceeded the {maxTerms}-term cap")
        else Ok p

let chargeVec (bud: Budget) (ps: Poly []) : Result<Val, ExtractError> =
    ps
    |> Array.fold (fun acc p -> acc |> Result.bind (fun out -> charge bud p |> Result.map (fun q -> out @ [ q ])))
        (Ok [])
    |> Result.map (List.toArray >> VVec)

/// The binary algebra: the rules, in one place, for both front halves. Its
/// `OutsideFragment` strings are never rendered by either consumer, so they
/// are phrased for whichever walker is reading a backtrace -- "a nonzero
/// constant" covers the surface's literal-or-static case and the typed
/// side's literal-only one alike.
let binOp (bud: Budget) (span: Span) (op: BinOp) (vl: Val) (vr: Val)
    : Result<Val, ExtractError> =
    let bad msg = outside msg span
    match vl, vr with
    | VOpaque n, _ | _, VOpaque n ->
        bad $"the shape of invariant '{n}' is not decidable from its declared type"
    | VInvArr _, _ | _, VInvArr _ ->
        bad "an invariant array has no polynomial form -- read its cells at static indices"
    | VScalar a, VScalar b ->
        match op with
        | OpAdd -> charge bud (Poly.add a b) |> Result.map VScalar
        | OpSub -> charge bud (Poly.sub a b) |> Result.map VScalar
        | OpMul -> charge bud (Poly.mul a b) |> Result.map VScalar
        | OpDiv ->
            // Q[atoms] has no inverses: the divisor must be a nonzero
            // constant, and this is where `3.0 / 10.0` becomes 3/10 exactly.
            match Poly.asConstant b with
            | Some c when not (Rat.isZero c) ->
                charge bud (a |> Map.map (fun _ x -> Rat.div x c)) |> Result.map VScalar
            | Some _ -> bad "division by zero"
            | None -> bad "division is admitted only by a nonzero constant -- an invariant atom has no inverse in the coefficient ring"
        | _ -> bad "this operator is outside the polynomial fragment"
    | VVec a, VVec b ->
        if a.Length <> b.Length then
            bad $"whole-array arithmetic needs equal shapes ({a.Length} vs {b.Length} components)"
        else
            match op with
            | OpAdd -> Array.map2 Poly.add a b |> chargeVec bud
            | OpSub -> Array.map2 Poly.sub a b |> chargeVec bud
            | _ -> bad "only + and - are admitted between two arrays"
    | VScalar s, VVec v | VVec v, VScalar s ->
        // `invariant * array`: the scalar factor must be INVARIANT in the
        // polynomial sense (rep-degree 0), exactly the composition rule's
        // `Rep s, Inv, OpMul` arm read over coefficients.
        match op with
        | OpMul when Poly.repDegree s = 0 -> v |> Array.map (Poly.mul s) |> chargeVec bud
        | OpMul -> bad "scaling an array is admitted only by an INVARIANT scalar (rep-degree 0)"
        | _ -> bad "only invariant scaling is admitted between a scalar and an array"

type private Ctx = {
    Statics: StaticEnv
    Bud: Budget
}

let private constPoly (ctx: Ctx) (c: Rat) = charge ctx.Bud (Poly.ofRat c) |> Result.map VScalar

/// A static index expression: an int literal or anything the SHIPPED static
/// evaluator folds to an int (the `let static` machinery MLEquiv reads with).
let private staticIndex (ctx: Ctx) (e: Expr) : int option =
    match evalExpr ctx.Statics fuel e with
    | Ok (SVInt n) -> Some (int n)
    | _ -> None

let rec private extractVal (ctx: Ctx) (env: Map<string, Val>) (e: Expr) : Result<Val, ExtractError> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> constPoly ctx (Rat.ofBigInt (bigint n))
    | ExprKind.ExprLit (LitFloat f) ->
        match Rat.tryOfFloatExact f with
        | Some r -> constPoly ctx r
        | None -> outside "a non-finite float literal is not a polynomial coefficient" e.Span
    | ExprKind.ExprLit _ -> outside "only numeric literals are polynomial coefficients" e.Span
    | ExprKind.ExprTyped (inner, _) -> extractVal ctx env inner
    | ExprKind.ExprVar n ->
        match Map.tryFind n env with
        | Some v -> Ok v
        | None ->
            // A `let static` scalar reads as its (already float-folded) value;
            // anything else is a name the normal form cannot account for.
            match Map.tryFind n ctx.Statics.Values with
            | Some (SVInt i) -> constPoly ctx (Rat.ofBigInt (bigint i))
            | Some (SVFloat f) ->
                match Rat.tryOfFloatExact f with
                | Some r -> constPoly ctx r
                | None -> outside $"static '{n}' is not a finite number" e.Span
            | _ -> outside $"'{n}' is not a parameter, a let binding or a numeric static" e.Span
    | ExprKind.ExprUnaryOp (OpNeg, inner) ->
        extractVal ctx env inner |> Result.bind (fun v ->
            match v with
            | VScalar p -> Ok (VScalar (Poly.neg p))
            | VVec ps -> Ok (VVec (ps |> Array.map Poly.neg))
            | VInvArr _ -> outside "an invariant array has no polynomial form -- read its cells at static indices" e.Span
            | VOpaque n -> outside $"the shape of invariant '{n}' is not decidable from its declared type" e.Span)
    | ExprKind.ExprUnaryOp _ -> outside "this unary operator is outside the polynomial fragment" e.Span
    | ExprKind.ExprBinOp (Elementwise, op, l, r) ->
        extractVal ctx env l |> Result.bind (fun vl ->
        extractVal ctx env r |> Result.bind (fun vr ->
            binOp ctx.Bud e.Span op vl vr))
    | ExprKind.ExprBinOp _ -> outside "outer-product operators are outside the polynomial fragment" e.Span
    | ExprKind.ExprArrayLit es ->
        // The arm MLEquiv refuses (see the header): an array literal of
        // scalar polynomials is the assembled rep-valued return.
        es
        |> List.fold (fun acc x ->
            acc |> Result.bind (fun ps ->
                extractVal ctx env x |> Result.bind (fun v ->
                    match v with
                    | VScalar p -> Ok (ps @ [ p ])
                    | _ -> outside "an array literal must be built from SCALAR polynomials -- nested aggregates are outside the fragment" x.Span)))
            (Ok [])
        |> Result.map (List.toArray >> VVec)
    | ExprKind.ExprLet (binding, body) ->
        match binding.Pattern.Kind with
        | PatternKind.PatVar n ->
            extractVal ctx env binding.Value
            |> Result.bind (fun v -> extractVal ctx (Map.add n v env) body)
        | _ -> outside "destructuring bindings are outside the polynomial fragment" binding.Value.Span
    | ExprKind.ExprBlock (stmts, Some final) ->
        stmts
        |> List.fold (fun acc s ->
            acc |> Result.bind (fun env ->
                match unwrapStmt s with
                | StmtLet binding ->
                    match binding.Pattern.Kind with
                    | PatternKind.PatVar n ->
                        extractVal ctx env binding.Value |> Result.map (fun v -> Map.add n v env)
                    | _ -> outside "destructuring bindings are outside the polynomial fragment" binding.Value.Span
                | _ -> outside "only `let` statements are admitted in a polynomial body" e.Span))
            (Ok env)
        |> Result.bind (fun env' -> extractVal ctx env' final)
    | ExprKind.ExprBlock (_, None) -> outside "a polynomial body must end in an expression" e.Span
    | ExprKind.ExprApp (f, args) ->
        // The only admitted application shape is a static index into a
        // vector binding or an invariant array; every genuine call is a v1
        // deferral.
        match f.Kind, args with
        | ExprKind.ExprVar n, [ idxE ] ->
            match Map.tryFind n env with
            | Some (VVec ps) ->
                match staticIndex ctx idxE with
                | Some i when i >= 0 && i < ps.Length -> Ok (VScalar ps.[i])
                | Some i -> outside $"index {i} is outside '{n}' ({ps.Length} components)" e.Span
                | None -> outside $"indexing '{n}' needs a static offset" e.Span
            | Some (VInvArr name) ->
                match staticIndex ctx idxE with
                | Some i -> charge ctx.Bud (Poly.ofMono (Mono.invAtom { Name = name; Index = Some i })) |> Result.map VScalar
                | None -> outside $"indexing the invariant '{name}' needs a static offset" e.Span
            | Some (VScalar _) -> outside $"'{n}' is a scalar, not an array" e.Span
            | Some (VOpaque name) ->
                outside $"the shape of invariant '{name}' is not decidable from its declared type" e.Span
            | None -> outside $"call to '{n}': calls are outside the polynomial fragment" e.Span
        | _ -> outside "calls are outside the polynomial fragment" e.Span
    | _ -> outside "this expression is outside the polynomial fragment" e.Span

/// The extracted normal form: one polynomial per output component.
type PolyForm = { Components: Poly [] }

/// Extract a function body to its polynomial normal form under a classified
/// signature. Total: every refusal is an `ExtractError`, never an exception.
let extract (psig: PolySig) (statics: StaticEnv) (fd: FunctionDecl) : Result<PolyForm, ExtractError> =
    let ctx = { Statics = statics; Bud = mkBudget () }
    let env =
        psig.Params
        |> List.fold (fun acc (name, kind) ->
            match kind with
            | PRep n -> Map.add name (VVec (Array.init n (fun i -> Poly.ofMono (Mono.repVar name i)))) acc
            | PInvArray -> Map.add name (VInvArr name) acc
            | PInvScalar -> Map.add name (VScalar (Poly.ofMono (Mono.invAtom { Name = name; Index = None }))) acc
            | PInvOpaque -> Map.add name (VOpaque name) acc)
            Map.empty
    extractVal ctx env fd.Body
    |> Result.bind (fun v ->
        match v, psig.ReturnDim with
        | VScalar p, None -> Ok { Components = [| p |] }
        | VVec ps, Some n when ps.Length = n -> Ok { Components = ps }
        | VVec ps, Some n ->
            outside $"the body assembles {ps.Length} components but the declared return has {n}" fd.Body.Span
        | VVec _, None -> outside "the body is an array but the declared return is a scalar" fd.Body.Span
        | VScalar _, Some _ -> outside "the body is a scalar but the declared return is a representation-typed array" fd.Body.Span
        | (VInvArr _ | VOpaque _), _ -> outside "the body is an invariant parameter with no polynomial form" fd.Body.Span)

/// One group element as it acts on THIS function's data: a printable name
/// (the word), the input matrix per rep parameter, and the output matrix.
/// Nothing here names a group -- assembling these from a registry is the
/// caller's job, which keeps 6c a second discharger rather than a second engine.
type ElementAction = {
    Name: string
    InMats: Map<string, int [][]>
    OutMat: int [][]
}

let mkAction (name: string) (inMats: Map<string, int [][]>) (outMat: int [][]) : ElementAction =
    { Name = name; InMats = inMats; OutMat = outMat }

/// The first coefficient at which the equivariance identity fails, with
/// everything a diagnostic needs to name it.
type DischargeFailure = {
    /// The group element (word) at which the identity broke.
    Element: string
    /// Which output component.
    Component: int
    /// The offending monomial, rendered.
    Monomial: string
    /// Its total degree in the rep components -- 0 means a constant term,
    /// the trivial-label obligation rather than a coefficient slip.
    RepDegree: int
    /// f(rho_in(w).x) coefficient vs (rho_out(w).f(x)) coefficient.
    Lhs: Rat
    Rhs: Rat
    /// The residual is negligible against the coefficient scale (a truncated
    /// decimal for an exact rational). Group-agnostic, so 6c inherits it.
    NearMiss: bool
}

type DischargeError =
    | GeneratorCheck of DischargeFailure
    | DischargeCap of string

/// A near miss: nonzero but negligible relative to the scale of the two
/// coefficients compared -- the signature of `0.3333333` written where
/// `1.0/3.0` was meant, never of a genuinely wrong sign or factor of two.
let private nearMissThreshold = 1e-6

let private isNearMiss (lhs: Rat) (rhs: Rat) : bool =
    let residual = abs (Rat.toFloat (Rat.sub lhs rhs))
    let scale = max 1.0 (max (abs (Rat.toFloat lhs)) (abs (Rat.toFloat rhs)))
    residual > 0.0 && residual <= nearMissThreshold * scale

/// Substitute x_(p,i) -> sum_j M_p[i][j] * x_(p,j) into one polynomial. The
/// matrices are integer ({0, +-1} for every shipped point group), so the
/// substitution stays in Q and the invariant atoms ride through untouched.
let private substitute (budget: int ref) (images: Map<string * int, Poly>) (p: Poly)
    : Result<Poly, DischargeError> =
    let mutable failed : DischargeError option = None
    let mutable out = Poly.zero
    for KeyValue (m, c) in p do
        if failed.IsNone then
            // The rep part expands; the invariant part and the coefficient are
            // carried as a single starting term.
            let mutable acc = Map.ofList [ ({ Mono.one with Inv = m.Inv }, c) ]
            for KeyValue (key, e) in m.Rep do
                if failed.IsNone then
                    // A rep variable with no image would be treated as FIXED
                    // (a weaker identity). Cannot arise -- caller builds
                    // images from the same Rep params the extractor used.
                    match Map.tryFind key images with
                    | None ->
                        failed <- Some (DischargeCap $"internal: no group action supplied for '{fst key}' component {snd key}")
                    | Some img ->
                        for _ in 1 .. e do
                            if failed.IsNone then
                                acc <- Poly.mul acc img
                                budget.Value <- budget.Value - Poly.terms acc
                                if budget.Value < 0 || Poly.terms acc > maxTerms then
                                    failed <- Some (DischargeCap $"the substituted form exceeded the {maxTerms}-term cap")
            if failed.IsNone then out <- Poly.add out acc
    match failed with
    | Some e -> Error e
    | None -> Ok out

/// THE FINITE DISCHARGE. For every enumerated group element w, compare
/// f(rho_in(w).x) with rho_out(w).f(x) coefficientwise over Q[atoms]; `Ok ()`
/// means the certificate holds, and the first failure names the element,
/// component and offending coefficient. All |G| elements are checked, not
/// just the generators -- word closure (proofs/BladeWordClosure.v) proves
/// the generators would suffice, but at |G| <= 8 the extra checks are free.
let discharge (form: PolyForm) (elements: ElementAction list) : Result<unit, DischargeError> =
    let budget = ref maxTerms
    let n = form.Components.Length
    let rec loop (els: ElementAction list) =
        match els with
        | [] -> Ok ()
        | el :: rest ->
            // rho_in per rep parameter, as the linear image of each variable.
            let images =
                el.InMats
                |> Map.fold (fun acc pname (m: int [][]) ->
                    let dim = m.Length
                    Seq.fold (fun acc2 i ->
                        let img =
                            Seq.fold (fun p j ->
                                let c = m.[i].[j]
                                if c = 0 then p
                                else Poly.addTerm (Mono.repVar pname j) (Rat.ofInt c) p)
                                Poly.zero (seq { 0 .. dim - 1 })
                        Map.add (pname, i) img acc2) acc (seq { 0 .. dim - 1 })) Map.empty
            let rec comps i =
                if i >= n then loop rest
                else
                    match substitute budget images form.Components.[i] with
                    | Error e -> Error e
                    | Ok lhs ->
                        let rhs =
                            Seq.fold (fun p j ->
                                let c = el.OutMat.[i].[j]
                                if c = 0 then p else Poly.add p (Poly.scale (Rat.ofInt c) form.Components.[j]))
                                Poly.zero (seq { 0 .. n - 1 })
                        let offending =
                            Poly.unionMonos lhs rhs
                            |> List.tryPick (fun m ->
                                let a = Poly.coeff m lhs
                                let b = Poly.coeff m rhs
                                if a = b then None else Some (m, a, b))
                        match offending with
                        | None -> comps (i + 1)
                        | Some (m, a, b) ->
                            Error (GeneratorCheck
                                { Element = el.Name
                                  Component = i
                                  Monomial = Mono.render m
                                  RepDegree = Mono.repDegree m
                                  Lhs = a
                                  Rhs = b
                                  NearMiss = isNearMiss a b })
            comps 0
    if elements |> List.exists (fun el -> el.OutMat.Length <> n) then
        Error (DischargeCap "internal: the output action does not match the extracted component count")
    else loop elements
