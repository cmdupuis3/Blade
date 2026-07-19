// ============================================================================
// Blade interpreter <-> C++ output PARITY layer: top-level binding printer.
//
// The compiled C++ binary's main() prints the module's top-level bindings, in
// declaration order, each as `cout << "<name> = " << value << endl;` (see
// CodeGen.genPrintScalar / genPrintStatements). This module reproduces that
// stdout for the tree-walking interpreter so interpreter output and compiled-
// binary output are indistinguishable to the differential gate.
//
// SCOPE — Milestone M0 (scalars) + M2 (dense array binding print). Supported:
//   Float64 / Float32 / Int64 / Int32 / Bool / String / Complex128 (Complex64)
//   scalars, and index-tagged scalars (Nat<I> etc., which print as their int).
// TUPLE, STRUCT (named), and UNIT bindings emit NOTHING — genPrintStatements
// returns [] for IRTTuple / IRTNamed / IRTUnit (verified empirically: a
// top-level `let pair = (1,2)` produces no output). FUNCTION-valued bindings
// also print nothing (their IRTArrow type is not an array and falls through).
//
// ARRAY bindings (M2) dispatch exactly as CodeGen.genPrintStatements' ArrayElem
// arms do — this module owns the KIND dispatch, Interp/ArrayOps owns the cell/
// row traversal and the low-level array-LINE emitters (see ArrayOps header for
// the assumed division of labor). This wave renders:
//   * DENSE flat rank 1-3   -> ArrayOps.emitFlatArray  (genPrintArrayFlat 1..3)
//   * DENSE rank 4 grid     -> ArrayOps.emitGrid4Array (genPrintArrayFlat 4)
//   * rank 0 / rank >= 5    -> ArrayOps.emitRankNPlaceholder (`<rank-N array>`)
//   * rank-1 struct arrays, all-scalar fields -> per-field print loop (M2.6)
//       `name = [{f1: V1, f2: V2}, ...]` (mirrors genPrintStatements)
// and mirrors CodeGen's NO-STDOUT (C++ comment only) arms as zero output:
//   * compound arrays, function-valued arrays, struct arrays (rank>1/no fields),
//     ragged SUB-VIEWS.
// The still-unrendered stdout-producing kinds are GATED with PrintUnsupported so
// the caller classifies the whole program SKIP-UNSUPPORTED (never wrong bytes):
//   * symmetric-aware arrays (rank 2-8)            -> M2.5
//   * ragged / dep-idx LITERALS / peel / row       -> M2.7
//   * rank-1 struct arrays with a NON-scalar field -> M2.6 (address-valued;
//       no faithful interpreter image — CodeGen streams it as a raw pointer)
//
// TIMING LINE. genMainWrapper prints `<testName> completed in <elapsed>s` FIRST,
// then the binding prints. `testName` is the SOURCE FILE STEM (Cli.compileFile:
// Path.GetFileNameWithoutExtension), not IRModule.Name, so it is taken here as
// the `progName` parameter. `elapsed` is nondeterministic and the differential
// gate strips every line containing "completed in" (DiffOracle.normalize), so
// we emit a constant 0. Emitting the line keeps standalone interp output shaped
// like a real run; the gate discards it either way.
//
// LINE ENDINGS. C++ `endl` writes '\n' (the CRT may expand it to '\r\n' on a
// Windows text-mode stdout). The differential gate normalizes '\r\n' -> '\n'
// before comparing (DiffOracle.normalize), so this printer emits '\n' — the
// canonical logical newline — for every line.
//
// Every scalar is rendered through Interp.CppFormat, the byte-pinned iostream
// mirror; this module contributes only the per-binding dispatch and the
// "<name> = " / newline framing.
// ============================================================================
module Blade.Interp.Print

open System.Text
open Blade.Types
open Blade.IR
open Blade.Interp.Value
open Blade.Interp.CppFormat

/// Raised for top-level binding kinds the M0 printer does not yet render
/// (arrays and other materialized aggregates), or when an evaluated value is
/// missing / of an unexpected shape for its declared scalar type. The caller
/// classifies this and falls back to the compiled binary for the whole program.
exception PrintUnsupported of string

/// The scalar element types genPrintStatements auto-prints via genPrintScalar.
/// Everything else (ETUnit) falls through to no output.
let private isPrintableScalarEt (et: ElemType) : bool =
    match et with
    | ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool
    | ETComplex64 | ETComplex128 | ETString -> true
    | ETUnit -> false

/// Project the primitive ElemType out of a scalar type, seeing through unit
/// annotations and nominal index-tag wrappers (Nat<I> = IRTIdxTagged(IRTScalar
/// ETInt64, _)). Returns None for non-scalar types.
let rec private elemThrough (ty: IRType) : ElemType option =
    match ty with
    | IRTScalar et -> Some et
    | IRTUnitAnnotated (inner, _) -> elemThrough inner
    | IRTIdxTagged (inner, _) -> elemThrough inner
    | _ -> None

/// Render one scalar Value exactly as the compiled binary's `cout << value`
/// would, driven by the binding's declared ElemType — the ElemType fixes the
/// C++ variable's static type and therefore which operator<< overload runs. The
/// value's numeric content is coerced to that width (mirroring an implicit
/// promotion the evaluator may not have widened), so an int stored in a Float64
/// binding still prints via the %.15g path, matching `cout << (double)`.
let private formatScalar (name: string) (et: ElemType) (v: Value) : string =
    let bad () =
        // NEVER %A the Value: a VDeferred/VClosure embeds an Env whose ValueRef
        // graph is cyclic, and F#'s structured printer recurses unboundedly on
        // it (observed as a runaway-memory process kill). The runtime case name
        // is diagnostic enough.
        raise (PrintUnsupported
                (sprintf "binding '%s': value case %s not printable as scalar %A"
                         name (v.GetType().Name) et))
    match et with
    | ETFloat64 ->
        match v with
        | VFloat f -> formatFloat15 f
        | VFloat32 f -> formatFloat15 (float f)
        | VInt n -> formatFloat15 (float n)
        | VInt32 n -> formatFloat15 (float n)
        | _ -> bad ()
    | ETFloat32 ->
        match v with
        | VFloat32 f -> formatFloat32 f
        | VFloat f -> formatFloat32 (float32 f)
        | VInt n -> formatFloat32 (float32 n)
        | VInt32 n -> formatFloat32 (float32 n)
        | _ -> bad ()
    | ETInt64 ->
        match v with
        | VInt n -> formatInt64 n
        | VInt32 n -> formatInt64 (int64 n)
        | _ -> bad ()
    | ETInt32 ->
        match v with
        | VInt32 n -> formatInt32 n
        | VInt n -> formatInt32 (int32 n)
        // Char literals lower to ETInt32 in this compiler (Value.fs); a
        // VChar reaching an int32 binding prints its numeric code, matching
        // `cout << (int32_t)`.
        | VChar c -> formatInt32 (int32 c)
        | _ -> bad ()
    | ETBool ->
        match v with
        | VBool b -> formatBool b
        | _ -> bad ()
    | ETString ->
        match v with
        | VString s -> formatString s
        | _ -> bad ()
    | ETComplex128 | ETComplex64 ->
        match v with
        | VComplex (re, im) -> formatComplex re im
        // A real promoted into a complex binding prints with a zero imaginary
        // component, matching std::complex<double>(x, 0).
        | VFloat f -> formatComplex f 0.0
        | VFloat32 f -> formatComplex (float f) 0.0
        | VInt n -> formatComplex (float n) 0.0
        | VInt32 n -> formatComplex (float n) 0.0
        | _ -> bad ()
    // ETUnit is never a printable scalar (skipped before reaching here).
    | ETUnit -> bad ()

/// Append to `sb` EXACTLY what the compiled binary's main() prints — the timing
/// line followed by the module's top-level binding prints, in declaration order.
///
///   progName  — the compiled binary's testName (SOURCE FILE STEM, e.g.
///               "001_basic_expression" for 001_basic_expression.blade); used
///               only for the (gate-stripped) `<progName> completed in 0s` line.
///   lookup    — fetches a binding's evaluated Value by its IRId.
///   irModule  — the lowered module whose Bindings drive the print order and the
///               same skip/kind decisions CodeGen.genPrintStatements makes.
///   sb        — output sink; lines are '\n'-terminated (see module header).
///
/// Raises PrintUnsupported for binding kinds not handled in M0 (arrays, or a
/// scalar binding with a missing / mistyped value) so the caller can classify.
let printBindings (progName: string) (lookup: IRId -> Value option) (irModule: IRModule) (sb: StringBuilder) : unit =
    // Timing line first — mirrors genMainWrapper, where `timing` precedes
    // `printCode`. Constant elapsed (gate-stripped); see module header.
    sb.Append(progName).Append(" completed in 0s").Append('\n') |> ignore

    // Deferred combinator/compose/parallel/fusion/zip bindings emit no C++ code
    // (and no output). Reuse CodeGen's own computation to stay in lock-step.
    let deferredIds = Blade.CodeGen.computeDeferredIds irModule.Bindings

    let emitScalar (b: IRBinding) (et: ElemType) : unit =
        match lookup b.Id with
        // A scalar-TYPED binding whose value is an ARRAY: the object_for OUTER
        // comparison/logical forms (`A [<] B`, `P [&&] Q`) collapse to scalar
        // Bool at the checker, but genObjectForApplication still materializes an
        // Array<bool,N>; the compiled binary prints its raw data pointer via
        // `cout << arr`, which the InterpDiff normalizer masks to 0xPTR. Emit a
        // matching hex token (the value is moot under the mask).
        | Some (VArray _) ->
            sb.Append(b.Name).Append(" = 0x0").Append('\n') |> ignore
        | Some v ->
            let text = formatScalar b.Name et v
            sb.Append(b.Name).Append(" = ").Append(text).Append('\n') |> ignore
        | None ->
            raise (PrintUnsupported (sprintf "binding '%s': no evaluated value" b.Name))

    // Peel |> compute wrappers to reach the underlying materialization node
    // (mirrors genPrintStatements' unwrapMaterialization).
    let rec unwrapMaterialization (e: IRExpr) : IRExpr =
        match e with
        | IRCompute inner -> unwrapMaterialization inner
        | _ -> e

    // Emit for an array-typed binding EXACTLY what CodeGen.genPrintStatements'
    // ArrayElem arms produce (byte-for-byte), routing to the ArrayOps emitters
    // for the dense cases and mirroring CodeGen's no-stdout / unsupported arms.
    // Print owns this dispatch; ArrayOps owns the traversal + line formatting.
    // A FULL compound read bound to a dense trailing-row type is a raw T* view
    // in C++ — CodeGen does not auto-print it (comment only). Partial reads
    // materialize real dense arrays and print normally.
    let isCompoundRowSubview (b: IRBinding) : bool =
        match b.Value with
        | IRIndex (a, (IRTuple coords) :: _, _) ->
            (match Blade.IR.typeOf a with
             | ArrayElem at when Blade.CodeGen.isCompoundArrayType at ->
                 let k = at.IndexTypes |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
                         |> Option.map (fun ix -> ix.Rank) |> Option.defaultValue coords.Length
                 (match Blade.IR.classifyCompoundIndexTuple k coords with
                  | Blade.IR.CompoundFull -> true | Blade.IR.CompoundPartial _ -> false)
             | _ -> false)
        | _ -> false

    let printArrayBinding (b: IRBinding) (arrType: IRArrayType) : unit =
        let rank = Blade.CodeGen.arrayRank arrType
        if Blade.CodeGen.isCompoundArrayType arrType then
            ()   // CodeGen emits a diagnostic C++ comment only -> zero stdout.
        elif isCompoundRowSubview b then
            ()   // raw trailing-row T* view: not auto-printed.
        else
        match arrType.ElemType with
        | FuncElem _ ->
            ()   // arrays of function values: comment only (std::function unstreamable).
        | IRTNamed structName ->
            // Rank-1 struct arrays with known, all-scalar fields print a
            // per-field loop (stdout) — mirror genPrintStatements
            // (CodeGen.fs ~10430-10457):
            //   name = [{f1: V1, f2: V2}, {f1: V1, f2: V2}, ...]
            // Each field value is streamed by the compiled binary via
            // `cout << name[i].field`, so the field's DECLARED ElemType fixes
            // the C++ operator<< overload and therefore its formatting (reuse
            // formatScalar, the same CppFormat mirror the scalar path uses).
            // Rows are ", "-separated inside `[...]`; fields ", "-separated
            // inside `{...}`; field ORDER follows the declared IRTDStruct list.
            // Everything else is deferred / comment-only:
            //   * rank>1, unknown or empty field list -> CodeGen comment (no
            //     stdout), so emit nothing;
            //   * ANY non-scalar field (an array / tuple / nested-struct member
            //     that C++ would stream as a raw address) -> CodeGen still emits
            //     the loop, but the interpreter has no faithful image of that
            //     address, so GATE (PrintUnsupported -> SKIP-UNSUPPORTED) rather
            //     than risk wrong bytes. (structs/013 'Trace.samples' is such a
            //     field and must keep skipping.)
            let structFields =
                irModule.Types |> List.tryPick (fun td ->
                    match td with
                    | IRTDStruct (n, fs) when n = structName -> Some fs
                    | _ -> None)
            match structFields with
            | Some fields when rank = 1 && not (List.isEmpty fields) ->
                // Resolve each field to a printable scalar ElemType FIRST (before
                // touching sb): a non-scalar field defers the whole program.
                let fieldEts =
                    fields |> List.map (fun (fname, ftype) ->
                        match elemThrough ftype with
                        | Some et when isPrintableScalarEt et -> (fname, et)
                        | _ ->
                            raise (PrintUnsupported
                                    (sprintf "rank-1 struct array '%s' print: field '%s' is not a printable scalar (M2.6)"
                                             b.Name fname)))
                match lookup b.Id with
                | Some (VArray ba) ->
                    sb.Append(b.Name).Append(" = [") |> ignore
                    let n = if ba.Extents.Length = 0 then 0L else ba.Extents.[0]
                    for i in 0L .. n - 1L do
                        if i > 0L then sb.Append(", ") |> ignore
                        let rowFields =
                            match ArrayOps.readCell ba [ i ] with
                            | VStruct (_, fs) -> fs
                            | _ ->
                                raise (PrintUnsupported
                                        (sprintf "rank-1 struct array '%s' print: row %d is not a struct value" b.Name i))
                        sb.Append("{") |> ignore
                        fieldEts |> List.iteri (fun j (fname, et) ->
                            if j > 0 then sb.Append(", ") |> ignore
                            let fv =
                                match rowFields |> Array.tryPick (fun (nm, v) -> if nm = fname then Some v else None) with
                                | Some v -> v
                                | None ->
                                    raise (PrintUnsupported
                                            (sprintf "rank-1 struct array '%s' print: row missing field '%s'" b.Name fname))
                            sb.Append(fname).Append(": ").Append(formatScalar b.Name et fv) |> ignore)
                        sb.Append("}") |> ignore
                    sb.Append("]").Append('\n') |> ignore
                | Some _ ->
                    raise (PrintUnsupported (sprintf "rank-1 struct array '%s': value is not a VArray" b.Name))
                | None ->
                    raise (PrintUnsupported (sprintf "rank-1 struct array '%s': no evaluated value" b.Name))
            | _ -> ()
        | IRTTuple _ ->
            // Arrays of TUPLE elements are comment-only in CodeGen
            // (genPrintStatements: "std::tuple has no operator<<") -> zero
            // stdout. The tuple-returning kernel (e.g. loops/070's
            // `lambda(x) -> (x, x*10)`) already MATERIALIZES correctly; only the
            // auto-print is suppressed, and value-checks read components via
            // destructuring (`let (a, b) = T(i)`). Emit nothing to match.
            ()
        | _ ->
            // Ragged-family classification (mirrors genPrintStatements): the
            // three stdout-producing shapes defer (M2.7); a bare ragged sub-view
            // is comment-only (no stdout).
            let isRaggedLiteralBinding =
                (Blade.CodeGen.isRaggedArrayType arrType || Blade.CodeGen.isDepIdxArrayType arrType)
                && (match b.Value with IRArrayLit _ -> true | _ -> false)
            let isRaggedPeelOutput =
                Blade.CodeGen.isRaggedArrayType arrType
                && (match unwrapMaterialization b.Value with IRApplyCombinator _ -> true | _ -> false)
            let isRaggedRowBinding =
                Blade.CodeGen.isRaggedRowType arrType
                && (match b.Value with IRIndex _ -> true | _ -> false)
            if isRaggedPeelOutput || isRaggedRowBinding || isRaggedLiteralBinding then
                // Ragged literals, rank-1 ragged ROWS, and apply-produced ragged
                // PEEL OUTPUTS all render as the flat backing-pool value sequence,
                // which the single ArrayOps.printArrayBinding emitter (byte-verified
                // on the literal path) reproduces:
                //   * a ragged LITERAL / rank>=2 elementwise-map peel output shares
                //     lens+prefix-offsets metadata; CodeGen iterates rows via .lens
                //     (genPrintStatements case (a) / (b)-rank>=2) and ArrayOps
                //     flattens the SRagged pool identically;
                //   * a rank-1 PEEL OUTPUT is rank-1 RECTANGULAR at runtime — one
                //     scalar per outer iteration — which CodeGen prints via
                //     genPrintArrayFlat 1 and ArrayOps.emitFlatArray on the rank-1
                //     dense value matches byte-for-byte.
                // GATED on a materialized VArray: if the ragged-apply layer has not
                // produced one (the M2.7 ragged-apply path is gated UPSTREAM in the
                // Loops layer today — "apply over ragged/grouped/compound input"),
                // the whole program SKIP-classifies before Print runs; this arm
                // activates as that layer materializes ragged apply outputs.
                match lookup b.Id with
                | Some (VArray ba) -> ArrayOps.printArrayBinding b ba sb
                | _ -> raise (PrintUnsupported (sprintf "ragged/dep-idx array '%s' print: no materialized value yet (M2.7 ragged apply gated upstream)" b.Name))
            elif Blade.CodeGen.isRaggedArrayType arrType then
                ()   // ragged sub-view: CodeGen comment only.
            else
                // DENSE flat / grid / placeholder OR symmetric-aware (rank 2-8) —
                // delegate the array-LINE emission to ArrayOps.printArrayBinding
                // (byte-verified against the compiled binary; owns the flat/grid/
                // placeholder + genPrintArraySymAware formats, and its own
                // ragged/non-scalar backstop -> ArrayOpUnsupported).
                // The materialized value must be a VArray (else defer: the
                // interpreter has not produced a printable image yet).
                match lookup b.Id with
                | Some (VArray ba) ->
                    ArrayOps.printArrayBinding b ba sb
                | Some _ ->
                    raise (PrintUnsupported (sprintf "array binding '%s': value is not a VArray" b.Name))
                | None ->
                    raise (PrintUnsupported (sprintf "array binding '%s': no evaluated value" b.Name))

    for b in irModule.Bindings do
        // isPrintable — a faithful mirror of CodeGen.genPrintStatements'
        // per-binding gate. A binding does not print if it is deferred, is a
        // streamed provider read, or is an unmaterialized loop/compute value
        // (IRMethodFor / IRObjectFor / a bare IRCompute of a non-combinator).
        // IRCompute of a combinator IS materialized and prints (an array — M2).
        let isPrintable =
            if Set.contains b.Id deferredIds then false
            elif (match Map.tryFind b.Id irModule.ProviderReads with
                  | Some spec -> spec.Streamed
                  | None -> false) then false
            else
                match b.Value with
                | IRCompute (IRApplyCombinator _) -> true
                | IRCompute (IRComposeApply _) -> true
                | IRCompute (IRParallel _) -> true
                | IRCompute (IRFusion _) -> true
                | IRCompute (IRVar _) -> true
                | IRCompute (IRFunctorMap _) -> true
                | IRCompute (IRChoice _) -> true
                | IRCompute (IRFallback _) -> true
                | IRCompute (IRComposeMeth _) -> true
                | IRCompute (IRBind _) -> true
                | IRCompute (IRGuard _) -> true
                | IRCompute (IRSequence _) -> true
                | IRCompute _ | IRMethodFor _ | IRObjectFor _ -> false
                | _ -> true

        if isPrintable then
            // Type dispatch mirrors genPrintStatements after IR.stripUnits.
            match stripUnits b.Type with
            | IRTScalar et when isPrintableScalarEt et ->
                emitScalar b et
            | IRTScalar _ ->
                // ETUnit (and any future non-printable scalar) — genPrintStatements
                // falls through to [] (no output).
                ()
            | IRTIdxTagged (inner, _) ->
                // A nominal index-tagged value (Nat<I>, ...) prints as its
                // underlying int scalar (genPrintStatements: genPrintScalar).
                match elemThrough inner with
                | Some et -> emitScalar b et
                | None -> ()
            | ArrayElem arrType ->
                // M2: dense-array print (dispatch mirrors genPrintStatements).
                printArrayBinding b arrType
            | IRTTuple _ -> ()   // genPrintStatements: [] — top-level tuples print nothing
            | IRTNamed _ -> ()   // genPrintStatements: [] — structs/sum types print nothing
            | IRTUnit -> ()      // genPrintStatements: [] — unit prints nothing
            | _ -> ()            // function values (IRTArrow non-array), inference vars, etc.
