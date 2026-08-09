/// The constrained-record INDEX FENCE: the semantic half of the
/// constrained-index-types feature. This module answers three questions
/// and nothing else: (1) is this struct STATIC -- are all its fields
/// StaticValue-representable; (2) is this struct INDEX-ELIGIBLE --
/// additionally, is every field an Int with statically-foldable bounds,
/// so its solutions can be found by enumerating a rectangular box; (3) at
/// one cell of that box, do the struct's conjuncts hold.
///
/// It does NOT enumerate, order, count, or cap. That is the counting
/// layer's job (StructIdxSpec), deliberately: the certificate discipline
/// wants two independent enumeration routes over ONE shared cell
/// predicate, and a shared enumerator here would make them agree for the
/// wrong reason.
///
/// THE TWO READINGS, the semantic law of this feature: one conjunct list,
/// two readings. At CONSTRUCTION a false conjunct is an ERROR
/// (assert-not-solve, unchanged in both worlds -- the runtime guard in
/// TypeCheck.synthesizeStructChecks and the fold-time check in
/// StaticEval's ExprStruct arm); at ENUMERATION a false conjunct is
/// EXCLUSION (membership). `evalConjunctsAtCell` below implements the
/// SECOND reading only, returning `Ok false` rather than an error for a
/// violated conjunct. Construction must never be routed through it.
module Blade.StructIdxFence

open Blade.Ast
open Blade.StaticEval

// The box

/// One field's box range. INCLUSIVE ON BOTH ENDS -- the fence normalizes
/// `in lo .. hi` (half-open) and `<min=a, max=b>` (inclusive) alike.
type FieldBox = {
    Field: string
    Lo: int64
    Hi: int64
}

/// A struct that passed the index fence: the box, plus the conjuncts to
/// filter it by.
type StructBoxSpec = {
    Name: string
    /// DECLARATION order = lex nesting order, first field outermost.
    Fields: FieldBox list
    /// The DECLARED where-conjuncts only: desugared field-bound conjuncts
    /// are absent since the box already enforces them exactly. Numbering
    /// agrees with the construction reading's (both count 1-based over
    /// `Ast.structConjuncts`' order, declared conjuncts FIRST).
    Conjuncts: Expr list
}

/// Number of values a field's box admits. Zero when the range is inverted:
/// an EMPTY box is a legitimate (warned-about) outcome, not an error.
let extent (b: FieldBox) : int64 =
    if b.Hi < b.Lo then 0L else b.Hi - b.Lo + 1L

// Type classification

/// Short human-readable label for a field's declared type, for
/// diagnostics. Deliberately lossy: names a REJECTION REASON, not source.
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

/// Is this field type an Int? Unit/tag arguments are transparent -- a
/// tagged `Int<angular_momentum>` enumerates like a bare `Int`. `Nat` is
/// deliberately EXCLUDED: motivating boxes are shifted and carry negative
/// values (m in [-l, l]), and a non-negative type whose box straddles
/// zero would be a contradiction the fence cannot see.
let rec private isIntFieldType (ty: TypeExpr) : bool =
    match ty with
    | TyInt32 | TyInt64 -> true
    | TyNamed (("Int" | "Int32" | "Int64"), _) -> true
    | TyBounded (b, _, _) -> isIntFieldType b
    | TyConstrained (inner, _) -> isIntFieldType inner
    | _ -> false

// THE WEAK FENCE (`static struct` eligibility) is implemented once, at its
// only call site, in TypeCheck's TyDeclStruct registration arm; the INDEX
// fence below consumes that decision through `StructStaticInfo.IsStatic`.

// The strong (index) fence

let private seqResult (rs: Result<'a, string> list) : Result<'a list, string> =
    rs |> List.fold (fun acc r ->
        acc |> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ [x]))) (Ok [])

/// Fold a bound expression to an int64 in the static environment. Bounds
/// may name statics and call static functions (TypeCheck's
/// `StructBoundScope`); a bound naming an EARLIER FIELD fails here with
/// "undefined variable" -- same-record dependent bounds are out of the
/// BOX grammar.
let private foldBound (env: StaticEnv) (sname: string) (fname: string) (side: string) (e: Expr) : Result<int64, string> =
    match evalExpr env maxSteps e with
    | Ok (SVInt n) -> Ok n
    | Ok v -> Error (sprintf "struct %s, field '%s': %s bound is not static -- it folded to %s, not an integer" sname fname side (ppStaticValue v))
    | Error why -> Error (sprintf "struct %s, field '%s': %s bound is not static -- %s" sname fname side why)

/// One field's normalized inclusive box.
let private fieldBox (env: StaticEnv) (sname: string) (f: FieldDecl) : Result<FieldBox, string> =
    if not (isIntFieldType f.Type) then
        Error (sprintf "struct %s, field '%s': non-enumerable field type %s -- an index-eligible struct needs every field Int with static bounds"
                   sname f.Name (typeExprLabel f.Type))
    else
        // `Ast.fieldBoxBounds` hands back a HALF-OPEN pair whichever
        // spelling was written (`max=b` becomes `b + 1`); subtracting one
        // from the folded exclusive endpoint is the inclusive normalization.
        match fieldBoxBounds f with
        | Some loE, Some hiExclE ->
            foldBound env sname f.Name "min" loE |> Result.bind (fun lo ->
            foldBound env sname f.Name "max" hiExclE |> Result.map (fun hiExcl ->
                { Field = f.Name; Lo = lo; Hi = hiExcl - 1L }))
        | _ ->
            Error (sprintf "struct %s, field '%s': unbounded field -- an index-eligible struct needs a static min and max"
                       sname f.Name)

/// STRONG FENCE, over an explicit declaration. `isStatic` is the DECLARED
/// `static struct` marker: index-eligibility is an OPT-IN, not a property
/// a struct can acquire by accident.
let structStaticFenceOf
        (env: StaticEnv)
        (name: string)
        (isStatic: bool)
        (fields: FieldDecl list)
        (declared: Expr list)
        : Result<StructBoxSpec, string> =
    if not isStatic then
        Error (sprintf "struct %s is not declared static -- write `static struct %s` to make it index-eligible" name name)
    elif List.isEmpty fields then
        Error (sprintf "struct %s has no fields -- an index-eligible struct needs at least one" name)
    else
        fields
        |> List.map (fieldBox env name)
        |> seqResult
        |> Result.map (fun boxes -> { Name = name; Fields = boxes; Conjuncts = declared })

/// STRONG FENCE, by name, resolved against the static evaluator's struct
/// registry (declaration order is irrelevant).
let structStaticFence (env: StaticEnv) (name: string) : Result<StructBoxSpec, string> =
    match Map.tryFind name env.Structs with
    | None -> Error (sprintf "'%s' is not a declared struct" name)
    | Some info -> structStaticFenceOf env name info.IsStatic info.FieldDecls info.Declared

// The enumeration reading

/// Fold the struct's conjuncts at ONE cell of the box. `Ok true`: the cell
/// satisfies every conjunct, a MEMBER of the solution set. `Ok false`:
/// some conjunct is false, EXCLUDED -- the enumeration reading, NOT an
/// error (construction-false is an error and lives elsewhere). `Error _`:
/// a conjunct did not fold to a boolean within its budget; the reason is
/// raw, since the caller owns the witness-cell suffix.
///
/// The budget is `StaticEval.cellBudget`, spent afresh PER CELL and far
/// smaller than the `let static` folding budget, keeping the worst case
/// at the box cap from being 100,000 steps x 100,000 cells.
/// `StructIdxSpec.routeFlat` stops at the FIRST erroring cell, so a
/// conjunct that cannot fold is paid once, not once per cell.
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
