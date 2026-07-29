/// THE POLYNOMIAL EXTRACTOR AND THE FINITE (WORD-SET) DISCHARGE — the
/// generator-based certification engine's front half (plan-transforms-as-types
/// §3.5's uniform rule, §7 stage 6b). MLEquiv checks that a body COMPOSES
/// equivariance-preserving operations; this module checks that a body IS an
/// equivariant map, by normalizing it to an exact polynomial and testing the
/// equivariance identity coefficientwise. The two are complementary, and the
/// second runs only where the first has already said no.
///
/// ---------------------------------------------------------------------------
/// THE DELIBERATE POLARITY DIVERGENCE — READ THIS BEFORE DIFFING THE THREE
/// WALKERS (the 5c drift-catalog lesson, and §7's explicit instruction)
/// ---------------------------------------------------------------------------
/// MLEquiv's aggregate rule REJECTS a representation-typed value packed into a
/// literal aggregate ("the aggregate does not transform as a rep"), and its
/// access rule REJECTS a raw index into a non-trivial-label cell ("reads a
/// basis-dependent number"). THIS FILE ADMITS BOTH, on purpose:
///
///     function rot(x: Array<Float like PgIrrepsIdx<C4, [("E", 1)]>>)
///              where ml.equiv(C4) -> Array<Float like PgIrrepsIdx<C4, [("E", 1)]>>
///         = [0.0 - x(1), x(0)]
///
/// is exactly the shape composition refuses — raw component reads assembled
/// into an array literal — and it is exactly the shape this engine exists to
/// certify (it is J = R₉₀, hand-written). The divergence is INTENTIONAL and is
/// the engine's whole purpose: the composition rule is a sound-and-cheap
/// SYNTACTIC sufficient condition, and when it fails the engine asks the
/// SEMANTIC question directly. A future three-way diff of MLEquiv / MLGalilean
/// / MLPerm against this file must read the mismatch as design, not drift:
/// those three are abstract interpretations over a status lattice, this one is
/// a normalizer over ℚ[rep components][invariant atoms]. Nothing here weakens
/// any rule there — an admitted shape still has to PASS the discharge below,
/// which is a stronger obligation than the lattice ever imposed.
///
/// ---------------------------------------------------------------------------
/// THE NORMAL FORM
/// ---------------------------------------------------------------------------
/// A body becomes one `Poly` per OUTPUT COMPONENT (a scalar return is a
/// one-component vector). A `Poly` is a sparse `Mono -> Rat` map with no zero
/// coefficients, and a `Mono` is a pair of exponent maps:
///
///   * REP exponents over (parameter name, component index) — the variables
///     the group acts on;
///   * INV exponents over `InvAtom`s — an invariant parameter, or one static
///     cell of an invariant array. Invariants are HELD FIXED by the
///     certificate's own hypothesis, so they enter as opaque transcendental
///     ATOMS and the coefficient ring is ℚ[atoms] rather than ℚ. Without that,
///     `w · cross(u, v)` and every weighted hand-written layer would be out of
///     scope; with it, the discharge simply carries the atoms through
///     untouched (the group acts on rep components, never on an atom).
///
/// Requiring the identity for ALL atom values is STRONGER than requiring it at
/// the caller's actual weights, which is the correct reading: "held fixed but
/// arbitrary" is what the conditional theorem promises.
///
/// ---------------------------------------------------------------------------
/// EXACTNESS — TWO CONVENTIONS, AND WHY THEY DIFFER (§3.5)
/// ---------------------------------------------------------------------------
///   * A FLOAT LITERAL enters as its EXACT DYADIC value — the rational the
///     IEEE double actually denotes. `0.5` is 1/2; `0.1` is
///     3602879701896397/36028797018963968, not 1/10.
///   * A LITERAL DIVISION is evaluated EXACTLY in ℚ: `3.0 / 10.0` IS 3/10.
///
/// The two differ, and that difference is the point: `0.3` and `3.0/10.0` are
/// different numbers, so a coefficient meant to be a rational must be WRITTEN
/// as the division. The NEAR-MISS note on a failed discharge (`nearMiss`
/// below) is what catches the trap — it fires when the residual's float image
/// is negligible against the coefficient scale, i.e. when the author wrote a
/// truncated decimal for an exact value.
///
/// A `let static` binding is folded by the SHIPPED static evaluator before the
/// judgment seam, in floating point; its value therefore enters here as the
/// exact dyadic image of that fold. Only source-level division INSIDE the
/// certified body gets the exact-ℚ treatment. (Same near-miss net.)
///
/// ---------------------------------------------------------------------------
/// THE v1 CLOSED-WORLD FRAGMENT (§7 stage 6b, implemented exactly)
/// ---------------------------------------------------------------------------
/// Admitted, and nothing else:
///   1. literals (int and float, dyadic-exact);
///   2. static indexing into a Rep or Inv parameter;
///   3. scalar `+` `-` `*`, and `/` by a NONZERO literal-or-static constant
///      (ℚ[atoms] has no inverses, so an atom divisor is out of the ring);
///   4. whole-array `+` `-` between equal-length vectors, and
///      invariant · array (a scalar factor of rep-degree 0);
///   5. `let` (as a binding expression or as block statements);
///   6. ARRAY LITERALS of scalar polynomials, as the assembled return.
///
/// Everything else — calls (including `ml.*` ops and certified callees),
/// loops, mutation, `if`, `match`, lambdas, field access, nested aggregates —
/// is OUTSIDE the fragment and yields `OutsideFragment`, on which the ENGINE
/// STAYS SILENT and the composition verdict surfaces untouched. Two v1
/// conveniences beyond the literal wording of the list, both trivially
/// faithful on the normal form and both noted in the stage report: unary `-`
/// (it is scalar/array subtraction from zero), and indexing into any
/// vector-valued BINDING rather than a parameter alone (`let` is in the
/// fragment, and `(let y = x + x in y)(0)` is the same polynomial as
/// `x(0) + x(0)` — the normal form cannot tell them apart).
///
/// CAPS: total rep-degree ≤ 4 and ≤ 100000 expanded terms, enforced during
/// extraction AND during the discharge's substitution. A breach is NOT
/// silence: it returns `CapBreach`, whose note the caller appends to the
/// composition diagnostic, so a body that fell off the engine says so.
///
/// ---------------------------------------------------------------------------
/// GROUP-AGNOSTIC BY CONSTRUCTION
/// ---------------------------------------------------------------------------
/// Nothing below knows what a point group, an irrep or a spec is. The
/// discharge consumes a list of `ElementAction`s — a name, one integer matrix
/// per rep parameter, one output matrix — so stage 6c's radical-vector Lie
/// discharger is a SECOND consumer of the same normal form, not a second
/// extractor. The finite half checks ALL |G| ≤ 8 elements rather than the
/// generators: it is microseconds either way, it is simpler, and the Coq
/// lemma (proofs/BladeWordClosure.v) proves the generator checks would have
/// sufficed — belt and braces pointing in opposite directions.
module Blade.ML.PolyExtract

open System.Numerics
open Blade.Ast
open Blade.StaticEval

// ---------------------------------------------------------------------------
// Minimal exact rationals over BigInteger. A LOCAL copy of the SymPowerTables
// pattern, for the same reason it is local there: nothing else compiler-side
// needs exact fractions, and this module must not drag the Sym-power tables
// (or MLSpec) in front of MLEquiv in the build order. Always normalized
// (Den > 0, gcd = 1), so structural equality is value equality.
// ---------------------------------------------------------------------------

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

    /// The EXACT dyadic value of an IEEE double — §3.5's "every float literal
    /// is a dyadic rational", read off the bit pattern rather than through any
    /// decimal round-trip. `None` at NaN/±∞ (which are not coefficients).
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

// ---------------------------------------------------------------------------
// Monomials and polynomials
// ---------------------------------------------------------------------------

/// An invariant quantity the group does not move: a whole invariant scalar
/// parameter (`Index = None`) or one statically-indexed cell of an invariant
/// array (`Index = Some i`). Opaque — never evaluated, never compared to a
/// number, only carried.
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
    /// constant. Rep factors first, each in declaration-independent key order
    /// so the rendering is deterministic.
    let render (m: Mono) : string =
        let pow (s: string) (e: int) = if e = 1 then s else sprintf "%s^%d" s e
        let reps = [ for KeyValue ((p, i), e) in m.Rep -> pow (sprintf "%s(%d)" p i) e ]
        let invs =
            [ for KeyValue (a, e) in m.Inv ->
                pow (match a.Index with Some i -> sprintf "%s(%d)" a.Name i | None -> a.Name) e ]
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
    /// invariant atoms) — the only shape admissible as a divisor.
    let asConstant (p: Poly) : Rat option =
        if Map.isEmpty p then Some Rat.zero
        else
            match Map.toList p with
            | [ (m, c) ] when Map.isEmpty m.Rep && Map.isEmpty m.Inv -> Some c
            | _ -> None

    /// Every monomial of two polynomials, in a DETERMINISTIC order: rep degree
    /// ascending, then rendered form. Diagnostics name "the first offending
    /// coefficient", so the order has to be a property of the polynomials and
    /// not of a hash walk.
    let unionMonos (a: Poly) (b: Poly) : Mono list =
        Set.union (a |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                  (b |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
        |> Set.toList
        |> List.sortBy (fun m -> (Mono.repDegree m, Mono.render m))

    let coeff (m: Mono) (p: Poly) : Rat = defaultArg (Map.tryFind m p) Rat.zero

// ---------------------------------------------------------------------------
// Caps and the failure vocabulary
// ---------------------------------------------------------------------------

/// §7's caps. Degree is TOTAL REP degree (invariant atoms are constants and do
/// not count — the equivariance identity splits per rep degree, not per atom
/// degree).
let maxRepDegree = 4
let maxTerms = 100_000

type ExtractError =
    /// A construct outside the v1 closed-world fragment, or a shape the normal
    /// form cannot represent. The engine is SILENT on this: the composition
    /// diagnostic surfaces untouched.
    | OutsideFragment of string * Span
    /// A cap breach. The composition diagnostic surfaces WITH this note
    /// appended — a body that fell off the engine must say so rather than look
    /// like a body the engine never looked at.
    | CapBreach of string

// ---------------------------------------------------------------------------
// The signature the extractor is handed
// ---------------------------------------------------------------------------

/// What a parameter is, as far as the normal form cares. `PRep n` is a
/// rep-typed array with n REAL components (the group moves them); the `PInv*`
/// cases are held fixed, and the scalar/array split decides whether the
/// parameter reads as ONE atom or as a family of statically-indexed atoms.
///
/// `PInvOpaque` is the conservative arm: an invariant whose SHAPE the caller's
/// classifier could not decide. It is bound to a value that refuses every
/// operation, because modelling an array as a scalar atom would be UNFAITHFUL
/// — and faithfulness of the normal form is the single thing the soundness of
/// this whole engine rests on.
type ParamKind =
    | PRep of int
    | PInvScalar
    | PInvArray
    | PInvOpaque

type PolySig = {
    Params: (string * ParamKind) list
    /// `Some n` — the return is a rep-typed array of n components; `None` — a
    /// scalar (which the discharge treats as the one-dimensional trivial rep,
    /// i.e. an invariance claim).
    ReturnDim: int option
}

/// Constructors, so a caller never has to qualify a record label across a
/// module boundary (and so the field names stay this module's business).
let mkSig (ps: (string * ParamKind) list) (ret: int option) : PolySig =
    { Params = ps; ReturnDim = ret }

// ---------------------------------------------------------------------------
// The extractor
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// THE VALUE ALGEBRA — public, because there are two front halves
// ---------------------------------------------------------------------------
//
// `Val`, `Budget`, `charge`, `chargeVec` and `binOp` below are the part of the
// extractor that does NOT walk syntax: given two abstract values and an
// operator, what polynomial comes out, and does it fit under the caps. Both
// front halves need exactly this and nothing else of each other —
// `MLPolyExtractTyped` used to restate `binOp` arm for arm, with a note saying
// a divergence there would be a SOUNDNESS bug rather than a recall difference
// (a wrong extraction makes the discharge certify a polynomial that is not the
// body). It is public here so that there is one copy and the note is moot.
//
// The two walkers' *contexts* stay separate and different — this module's
// carries a `StaticEnv` it needs to fold `let static` reads, the typed one has
// no statics left to fold by typecheck — so the shared surface is keyed on a
// bare `Budget` cell, which is the only piece of context the algebra touches.

/// The extractor's abstract value. Deliberately NOT a status lattice: every
/// case carries the actual polynomial data, because the whole point is that
/// the verdict comes from the coefficients and not from a type.
type Val =
    | VScalar of Poly
    /// An assembled vector of scalar polynomials: a rep parameter, an array
    /// literal, or anything built from those by the array arms.
    | VVec of Poly []
    /// An invariant ARRAY parameter. It has no polynomial of its own — only
    /// its statically-indexed cells do, and each of those is an atom.
    | VInvArr of string
    /// An invariant of undecided shape: every use leaves the fragment.
    | VOpaque of string

/// The term budget, shared across a whole extraction so a body cannot dodge
/// the cap by spreading the blow-up over many components. A mutable CELL and
/// not a field, so both front halves can hand the same budget to the shared
/// algebra without agreeing on a context type.
type Budget = { mutable Remaining: int }

let mkBudget () : Budget = { Remaining = maxTerms }

let private fuel = 100_000

let private outside (msg: string) (span: Span) = Error (OutsideFragment (msg, span))

/// Charge a freshly built polynomial against the caps. Both caps are checked
/// here and nowhere else, so every polynomial in the normal form is known to
/// satisfy them.
///
/// The `CapBreach` strings ARE user-visible — `MLEquiv.judgeFunction` appends
/// them through `EquivMessages.capNote` — so they are pinned by
/// tests/corpus/diagnostics/024.
let charge (bud: Budget) (p: Poly) : Result<Poly, ExtractError> =
    let n = Poly.terms p
    if n > maxTerms then
        Error (CapBreach (sprintf "the expanded form exceeded the %d-term cap" maxTerms))
    elif Poly.repDegree p > maxRepDegree then
        Error (CapBreach (sprintf "the body's degree in the representation components exceeds the degree-%d cap" maxRepDegree))
    else
        bud.Remaining <- bud.Remaining - n
        if bud.Remaining < 0 then
            Error (CapBreach (sprintf "the expanded form exceeded the %d-term cap" maxTerms))
        else Ok p

let chargeVec (bud: Budget) (ps: Poly []) : Result<Val, ExtractError> =
    ps
    |> Array.fold (fun acc p -> acc |> Result.bind (fun out -> charge bud p |> Result.map (fun q -> out @ [ q ])))
        (Ok [])
    |> Result.map (List.toArray >> VVec)

/// The binary algebra: THE rules, in one place, for both front halves.
///
/// Its refusal strings are `OutsideFragment`, which neither consumer ever
/// renders (the seam falls back to composition's verdict, the typed side
/// answers `None`), so they are phrased for whichever walker is reading a
/// backtrace — "a nonzero constant" covers the surface's literal-or-static
/// case and the typed side's literal-only one alike.
let binOp (bud: Budget) (span: Span) (op: BinOp) (vl: Val) (vr: Val)
    : Result<Val, ExtractError> =
    let bad msg = outside msg span
    match vl, vr with
    | VOpaque n, _ | _, VOpaque n ->
        bad (sprintf "the shape of invariant '%s' is not decidable from its declared type" n)
    | VInvArr _, _ | _, VInvArr _ ->
        bad "an invariant array has no polynomial form — read its cells at static indices"
    | VScalar a, VScalar b ->
        match op with
        | OpAdd -> charge bud (Poly.add a b) |> Result.map VScalar
        | OpSub -> charge bud (Poly.sub a b) |> Result.map VScalar
        | OpMul -> charge bud (Poly.mul a b) |> Result.map VScalar
        | OpDiv ->
            // ℚ[atoms] has no inverses: the divisor must be a nonzero constant
            // — §7's "/ by nonzero literal/static" — and THIS is where
            // `3.0 / 10.0` becomes 3/10 exactly.
            match Poly.asConstant b with
            | Some c when not (Rat.isZero c) ->
                charge bud (a |> Map.map (fun _ x -> Rat.div x c)) |> Result.map VScalar
            | Some _ -> bad "division by zero"
            | None -> bad "division is admitted only by a nonzero constant — an invariant atom has no inverse in the coefficient ring"
        | _ -> bad "this operator is outside the polynomial fragment"
    | VVec a, VVec b ->
        if a.Length <> b.Length then
            bad (sprintf "whole-array arithmetic needs equal shapes (%d vs %d components)" a.Length b.Length)
        else
            match op with
            | OpAdd -> Array.map2 Poly.add a b |> chargeVec bud
            | OpSub -> Array.map2 Poly.sub a b |> chargeVec bud
            | _ -> bad "only + and - are admitted between two arrays"
    | VScalar s, VVec v | VVec v, VScalar s ->
        // §7's `invariant · array`: the scalar factor must be INVARIANT in the
        // polynomial sense — rep-degree 0 — which is exactly the composition
        // rule's `Rep s, Inv, OpMul` arm read over coefficients.
        match op with
        | OpMul when Poly.repDegree s = 0 -> v |> Array.map (Poly.mul s) |> chargeVec bud
        | OpMul -> bad "scaling an array is admitted only by an INVARIANT scalar (rep-degree 0)"
        | _ -> bad "only invariant scaling is admitted between a scalar and an array"

// ---------------------------------------------------------------------------
// The surface walk
// ---------------------------------------------------------------------------

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
                | None -> outside (sprintf "static '%s' is not a finite number" n) e.Span
            | _ -> outside (sprintf "'%s' is not a parameter, a let binding or a numeric static" n) e.Span
    | ExprKind.ExprUnaryOp (OpNeg, inner) ->
        extractVal ctx env inner |> Result.bind (fun v ->
            match v with
            | VScalar p -> Ok (VScalar (Poly.neg p))
            | VVec ps -> Ok (VVec (ps |> Array.map Poly.neg))
            | VInvArr _ -> outside "an invariant array has no polynomial form — read its cells at static indices" e.Span
            | VOpaque n -> outside (sprintf "the shape of invariant '%s' is not decidable from its declared type" n) e.Span)
    | ExprKind.ExprUnaryOp _ -> outside "this unary operator is outside the polynomial fragment" e.Span
    | ExprKind.ExprBinOp (Elementwise, op, l, r) ->
        extractVal ctx env l |> Result.bind (fun vl ->
        extractVal ctx env r |> Result.bind (fun vr ->
            binOp ctx.Bud e.Span op vl vr))
    | ExprKind.ExprBinOp _ -> outside "outer-product operators are outside the polynomial fragment" e.Span
    | ExprKind.ExprArrayLit es ->
        // THE ARM MLEquiv REFUSES (see the header): an array literal of scalar
        // polynomials is the assembled rep-valued return.
        es
        |> List.fold (fun acc x ->
            acc |> Result.bind (fun ps ->
                extractVal ctx env x |> Result.bind (fun v ->
                    match v with
                    | VScalar p -> Ok (ps @ [ p ])
                    | _ -> outside "an array literal must be built from SCALAR polynomials — nested aggregates are outside the fragment" x.Span)))
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
        // The ONLY admitted application shape is a static index into a vector
        // binding or an invariant array. Every genuine CALL — `ml.*` ops,
        // certified callees, scalar builtins — is a v1 deferral.
        match f.Kind, args with
        | ExprKind.ExprVar n, [ idxE ] ->
            match Map.tryFind n env with
            | Some (VVec ps) ->
                match staticIndex ctx idxE with
                | Some i when i >= 0 && i < ps.Length -> Ok (VScalar ps.[i])
                | Some i -> outside (sprintf "index %d is outside '%s' (%d components)" i n ps.Length) e.Span
                | None -> outside (sprintf "indexing '%s' needs a static offset" n) e.Span
            | Some (VInvArr name) ->
                match staticIndex ctx idxE with
                | Some i -> charge ctx.Bud (Poly.ofMono (Mono.invAtom { Name = name; Index = Some i })) |> Result.map VScalar
                | None -> outside (sprintf "indexing the invariant '%s' needs a static offset" name) e.Span
            | Some (VScalar _) -> outside (sprintf "'%s' is a scalar, not an array" n) e.Span
            | Some (VOpaque name) ->
                outside (sprintf "the shape of invariant '%s' is not decidable from its declared type" name) e.Span
            | None -> outside (sprintf "call to '%s': calls are outside the v1 polynomial fragment" n) e.Span
        | _ -> outside "calls are outside the v1 polynomial fragment" e.Span
    | _ -> outside "this expression is outside the v1 polynomial fragment" e.Span

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
            outside (sprintf "the body assembles %d components but the declared return has %d" ps.Length n) fd.Body.Span
        | VVec _, None -> outside "the body is an array but the declared return is a scalar" fd.Body.Span
        | VScalar _, Some _ -> outside "the body is a scalar but the declared return is a representation-typed array" fd.Body.Span
        | (VInvArr _ | VOpaque _), _ -> outside "the body is an invariant parameter with no polynomial form" fd.Body.Span)

// ---------------------------------------------------------------------------
// The finite discharge
// ---------------------------------------------------------------------------

/// One group element as it acts on THIS function's data: a printable name (the
/// word), the input matrix for every rep parameter, and the output matrix.
/// Nothing here names a group — assembling these from a registry is the
/// caller's job, which is what keeps 6c a second discharger rather than a
/// second engine.
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
    /// Its total degree in the rep components — 0 means a CONSTANT term, which
    /// is the π₀/trivial-label obligation rather than a coefficient slip.
    RepDegree: int
    /// f(ρ_in(w)·x) coefficient vs (ρ_out(w)·f(x)) coefficient.
    Lhs: Rat
    Rhs: Rat
    /// The residual is negligible against the coefficient scale — §3.5's
    /// mandatory near-miss net (a truncated decimal written for an exact
    /// rational). Group-agnostic, so 6c inherits it unchanged.
    NearMiss: bool
}

type DischargeError =
    | GeneratorCheck of DischargeFailure
    | DischargeCap of string

/// The near-miss test, stated once. A residual is a near miss when it is
/// nonzero but its float image is negligible relative to the scale of the two
/// coefficients being compared: exactly the signature of `0.3333333` written
/// where `1.0/3.0` was meant, and never the signature of a genuinely wrong
/// sign or a factor of two.
let private nearMissThreshold = 1e-6

let private isNearMiss (lhs: Rat) (rhs: Rat) : bool =
    let residual = abs (Rat.toFloat (Rat.sub lhs rhs))
    let scale = max 1.0 (max (abs (Rat.toFloat lhs)) (abs (Rat.toFloat rhs)))
    residual > 0.0 && residual <= nearMissThreshold * scale

/// Substitute x_(p,i) ↦ Σ_j M_p[i][j] · x_(p,j) into one polynomial. The
/// matrices are integer ({0, ±1} for every shipped point group), so the whole
/// substitution stays in ℚ and the invariant atoms ride through untouched.
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
                    // A rep variable with no image would be treated as FIXED,
                    // which would verify a different (weaker) identity. It
                    // cannot arise — the caller builds the images from the same
                    // Rep parameters the extractor made variables of — so it is
                    // an internal error rather than a silent widening.
                    match Map.tryFind key images with
                    | None ->
                        failed <- Some (DischargeCap (sprintf "internal: no group action supplied for '%s' component %d" (fst key) (snd key)))
                    | Some img ->
                        for _ in 1 .. e do
                            if failed.IsNone then
                                acc <- Poly.mul acc img
                                budget.Value <- budget.Value - Poly.terms acc
                                if budget.Value < 0 || Poly.terms acc > maxTerms then
                                    failed <- Some (DischargeCap (sprintf "the substituted form exceeded the %d-term cap" maxTerms))
            if failed.IsNone then out <- Poly.add out acc
    match failed with
    | Some e -> Error e
    | None -> Ok out

/// THE FINITE DISCHARGE. For every enumerated group element w, compare
/// f(ρ_in(w)·x) with ρ_out(w)·f(x) COEFFICIENTWISE over ℚ[atoms]. `Ok ()` =
/// the certificate holds; the first failure carries the element, the
/// component and the offending coefficient.
///
/// All |G| elements are checked, not just the generators. Word closure says
/// the generators would suffice (proofs/BladeWordClosure.v proves it); at
/// |G| ≤ 8 the extra checks cost nothing and the redundancy is deliberate.
let discharge (form: PolyForm) (elements: ElementAction list) : Result<unit, DischargeError> =
    let budget = ref maxTerms
    let n = form.Components.Length
    let rec loop (els: ElementAction list) =
        match els with
        | [] -> Ok ()
        | el :: rest ->
            // ρ_in per rep parameter, as the linear image of each variable.
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
