/// The constrained-record INDEX FENCE: the semantic half of
/// the retired constrained-index-types plan's C1 stage.
///
/// This module answers three questions and nothing else:
///   1. is this struct STATIC — are all its fields StaticValue-representable
///      (the `static struct` decl form's own eligibility rule);
///   2. is this struct INDEX-ELIGIBLE — additionally, is every field an Int
///      with statically-foldable bounds, so its solution set can be found by
///      enumerating a rectangular box;
///   3. at one cell of that box, do the struct's conjuncts hold.
///
/// It does NOT enumerate, order, count, or cap. That is the counting layer's
/// job (StructIdxSpec), deliberately: the plan's certificate discipline wants
/// two independent enumeration routes over ONE shared cell predicate, and a
/// shared enumerator here would make the two routes agree for the wrong
/// reason.
///
/// THE TWO READINGS (plan §3, the semantic law of this feature). One
/// conjunct list, two readings:
///   - at CONSTRUCTION a false conjunct is an ERROR (§2.4 assert-not-solve,
///     unchanged in both worlds — the runtime guard in
///     TypeCheck.synthesizeStructChecks and the fold-time check in
///     StaticEval's ExprStruct arm);
///   - at ENUMERATION a false conjunct is EXCLUSION — membership.
/// `evalConjunctsAtCell` below implements the SECOND reading only, which is
/// why it returns `Ok false` rather than an error for a violated conjunct.
/// Construction must never be routed through it.
module Blade.StructIdxFence

open Blade.Ast
open Blade.StaticEval

// ============================================================================
// The box
// ============================================================================

/// One field's box range. INCLUSIVE ON BOTH ENDS, whichever surface spelling
/// the field was written with — the fence normalizes `in lo .. hi`
/// (half-open) and `<min=a, max=b>` (inclusive) to this one representation so
/// no consumer ever has to know which was written.
type FieldBox = {
    Field: string
    Lo: int64
    Hi: int64
}

/// A struct that passed the index fence: the box, plus the conjuncts to
/// filter it by.
type StructBoxSpec = {
    Name: string
    /// DECLARATION order, which is also lex nesting order — first field
    /// outermost (plan §3, "Rank = #fields ... declaration order = lex
    /// nesting order").
    Fields: FieldBox list
    /// The DECLARED where-conjuncts only. The desugared field-bound
    /// conjuncts are deliberately absent: the box already enforces them
    /// exactly (see `fieldBox` — the normalization and the desugar read the
    /// same `FieldDecl.Bound` through the same `Ast.fieldBoxBounds`), so
    /// re-folding them at every cell is pure cost with no possible effect.
    ///
    /// Conjunct numbering agrees with the construction reading's: both count
    /// 1-based over `Ast.structConjuncts`' order, whose declared conjuncts
    /// come FIRST — so index i here is index i there.
    Conjuncts: Expr list
}

/// Number of values a field's box admits. Zero when the range is inverted —
/// an EMPTY box is a legitimate (warned-about) outcome, not an error.
let extent (b: FieldBox) : int64 =
    if b.Hi < b.Lo then 0L else b.Hi - b.Lo + 1L

// ============================================================================
// Type classification
// ============================================================================

/// Short human-readable label for a field's declared type, for diagnostics.
/// Deliberately lossy: bounds and type arguments collapse, because the
/// message is naming a REJECTION REASON, not reconstructing source.
let rec typeExprLabel (ty: TypeExpr) : string =
    match ty with
    | TyInt32 -> "Int32"
    | TyInt64 -> "Int64"
    | TyFloat32 -> "Float32"
    | TyFloat64 -> "Float64"
    | TyComplex64 -> "Complex64"
    | TyComplex128 -> "Complex128"
    | TyBool -> "Bool"
    | TyString -> "String"
    | TyChar -> "Char"
    | TyUnit -> "Unit"
    | TyNamed (n, []) -> n
    | TyNamed (n, _) -> n + "<...>"
    | TyBounded (b, _, _) -> typeExprLabel b
    | TyConstrained (inner, _) -> typeExprLabel inner
    | TyArray _ -> "an array type"
    | TyAbstractArray _ -> "an abstract array type"
    | TyFunc _ -> "a function type"
    | TyTuple ts -> sprintf "a %d-tuple type" (List.length ts)
    | TyVar (v, _) -> sprintf "type variable %s" v
    | _ -> "a non-enumerable type"

/// Is this field type an Int? Unit/tag arguments are transparent — a tagged
/// `Int<angular_momentum>` enumerates exactly like a bare `Int`, and the tag
/// is the consumption layer's business (plan §3's per-field §3.10 units).
/// `Nat` is deliberately EXCLUDED: the motivating boxes are shifted and
/// carry negative values (m ∈ [-l, l]), and admitting a non-negative type
/// whose box straddles zero would be a contradiction the fence cannot see.
let rec private isIntFieldType (ty: TypeExpr) : bool =
    match ty with
    | TyInt32 | TyInt64 -> true
    | TyNamed (("Int" | "Int32" | "Int64"), _) -> true
    | TyBounded (b, _, _) -> isIntFieldType b
    | TyConstrained (inner, _) -> isIntFieldType inner
    | _ -> false

// THE WEAK FENCE (`static struct` eligibility — is every field type
// StaticValue-representable?) DELIBERATELY DOES NOT LIVE HERE. It is
// implemented once, at its only call site, in TypeCheck's TyDeclStruct
// registration arm (`staticFieldErr`, raising `StaticStructField`), where it
// can consult the type environment and therefore tell a non-static struct
// from a sum type from an alias — distinctions this module could only make
// by taking a callback and a coarser answer. A second, weaker copy here
// would be a second definition of one rule with nothing calling it, which is
// exactly the drift this feature's one-shared-conjunct-list discipline
// exists to prevent. The INDEX fence below consumes that decision through
// `StructStaticInfo.IsStatic` rather than recomputing it.

// ============================================================================
// The strong (index) fence
// ============================================================================

let private seqResult (rs: Result<'a, string> list) : Result<'a list, string> =
    rs |> List.fold (fun acc r ->
        acc |> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ [x]))) (Ok [])

/// Fold a bound expression to an int64 in the static environment. Bounds may
/// name statics and call static functions (that is exactly what the shipped
/// decl-time bound-scope check permits, TypeCheck's `StructBoundScope`); a
/// bound naming an EARLIER FIELD fails here with "undefined variable", which
/// is the correct diagnosis — same-record dependent bounds are out of the BOX
/// grammar (plan §3, deferred as a tight-heads efficiency item).
let private foldBound (env: StaticEnv) (sname: string) (fname: string) (side: string) (e: Expr) : Result<int64, string> =
    match evalExpr env maxSteps e with
    | Ok (SVInt n) -> Ok n
    | Ok v -> Error (sprintf "struct %s, field '%s': %s bound is not static — it folded to %s, not an integer" sname fname side (ppStaticValue v))
    | Error why -> Error (sprintf "struct %s, field '%s': %s bound is not static — %s" sname fname side why)

/// One field's normalized inclusive box.
let private fieldBox (env: StaticEnv) (sname: string) (f: FieldDecl) : Result<FieldBox, string> =
    if not (isIntFieldType f.Type) then
        Error (sprintf "struct %s, field '%s': non-enumerable field type %s — an index-eligible struct needs every field Int with static bounds"
                   sname f.Name (typeExprLabel f.Type))
    else
        // `Ast.fieldBoxBounds` is the ONE definition of a field's box, shared
        // with the surface layer: it hands back a HALF-OPEN pair whichever
        // spelling was written (`max=b` becomes `b + 1`). Subtracting one
        // from the folded exclusive endpoint is therefore the whole of the
        // inclusive normalization — there is no second reading of the
        // grammar here that could drift from the desugar.
        match fieldBoxBounds f with
        | Some loE, Some hiExclE ->
            foldBound env sname f.Name "min" loE |> Result.bind (fun lo ->
            foldBound env sname f.Name "max" hiExclE |> Result.map (fun hiExcl ->
                { Field = f.Name; Lo = lo; Hi = hiExcl - 1L }))
        | _ ->
            Error (sprintf "struct %s, field '%s': unbounded field — an index-eligible struct needs a static min and max"
                       sname f.Name)

/// STRONG FENCE, over an explicit declaration. Use this when the caller
/// already holds the struct's fields (the type checker's registration arm,
/// or any decl table that is not StaticEval's registry).
///
/// `isStatic` is the DECLARED `static struct` marker: index-eligibility is
/// an OPT-IN, not a property a struct can acquire by accident. Adding a
/// field to a plain struct must never silently change whether it can be
/// used in index position.
let structStaticFenceOf
        (env: StaticEnv)
        (name: string)
        (isStatic: bool)
        (fields: FieldDecl list)
        (declared: Expr list)
        : Result<StructBoxSpec, string> =
    if not isStatic then
        Error (sprintf "struct %s is not declared static — write `static struct %s` to make it index-eligible" name name)
    elif List.isEmpty fields then
        Error (sprintf "struct %s has no fields — an index-eligible struct needs at least one" name)
    else
        fields
        |> List.map (fieldBox env name)
        |> seqResult
        |> Result.map (fun boxes -> { Name = name; Fields = boxes; Conjuncts = declared })

/// STRONG FENCE, by name, resolved against the static evaluator's struct
/// registry (populated by `resolveStatics`' full decl pre-scan, so
/// declaration order is irrelevant).
let structStaticFence (env: StaticEnv) (name: string) : Result<StructBoxSpec, string> =
    match Map.tryFind name env.Structs with
    | None -> Error (sprintf "'%s' is not a declared struct" name)
    | Some info -> structStaticFenceOf env name info.IsStatic info.FieldDecls info.Declared

// ============================================================================
// The enumeration reading
// ============================================================================

/// Fold the struct's conjuncts at ONE cell of the box.
///
///   `Ok true`  — the cell satisfies every conjunct: a MEMBER of the
///                solution set;
///   `Ok false` — some conjunct is false: EXCLUDED. This is the enumeration
///                reading and it is NOT an error. Construction-false is an
///                error and lives elsewhere (see the module header);
///   `Error _`  — a conjunct did not fold to a boolean within its budget. The
///                reason is raw; the caller owns the witness-cell suffix,
///                since only the caller knows which route reached the cell.
///
/// The budget is `StaticEval.cellBudget`, spent afresh PER CELL, and it is
/// deliberately far smaller than the `let static` folding budget: a cell
/// predicate is a boolean over a handful of already-bound integers, so the
/// slack is enormous either way, and the small budget is what keeps the worst
/// case at the box cap from being 100,000 steps x 100,000 cells.
///
/// The per-cell cost is also smaller than plan §6 risk 3 assumed, in the
/// direction that matters: `StructIdxSpec.routeFlat` stops at the FIRST
/// erroring cell, so a conjunct that cannot fold is paid once, not once per
/// cell. What risk 3 got wrong was the other end — the budget it was counting
/// on could not fire at all; see `StaticEval.maxSteps`' own comment.
let evalConjunctsAtCell
        (env: StaticEnv)
        (spec: StructBoxSpec)
        (cell: (string * int64) list)
        : Result<bool, string> =
    // A cell must bind every field: a partial cell would silently read a
    // same-named static from the ambient environment instead of failing.
    let cellNames = cell |> List.map fst
    let fieldNames = spec.Fields |> List.map (fun b -> b.Field)
    if List.length cellNames <> List.length fieldNames
       || not (fieldNames |> List.forall (fun f -> List.contains f cellNames)) then
        Error (sprintf "struct %s: cell binds {%s} but the box has fields {%s}"
                   spec.Name (String.concat ", " cellNames) (String.concat ", " fieldNames))
    else
        let cellEnv =
            { env with Values = cell |> List.fold (fun m (n, v) -> Map.add n (SVInt v) m) env.Values }
        let rec go i (cs: Expr list) =
            match cs with
            | [] -> Ok true
            | c :: rest ->
                // Skipped in BOTH readings via the one shared predicate.
                if isPplLicenseConjunct c then go (i + 1) rest
                else
                    match evalExprWith cellEnv cellBudget c with
                    | Ok (SVBool true) -> go (i + 1) rest
                    | Ok (SVBool false) -> Ok false
                    | Ok _ -> Error (sprintf "conjunct %d of %s is not a boolean at compile time" i spec.Name)
                    | Error why -> Error (sprintf "conjunct %d of %s did not fold: %s" i spec.Name why)
        go 1 spec.Conjuncts
