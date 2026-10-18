// Blade interpreter <-> C++ output parity layer: top-level binding printer.
//
// The compiled C++ binary's main() prints the module's top-level bindings, in
// declaration order, each as `cout << "<name> = " << value << endl;` (see
// CodeGen.genPrintScalar / genPrintStatements). This module reproduces that
// stdout for the tree-walking interpreter so interpreter and compiled-binary
// output are indistinguishable to the differential gate.
//
// SCOPE: Milestone M0 (scalars) + M2 (dense array binding print). Supported:
//   Float64 / Float32 / Int64 / Int32 / Bool / String / Complex128 (Complex64)
//   scalars, and index-tagged scalars (Nat<I> etc., which print as their int).
// TUPLE and STRUCT bindings print spelled like their literals, `t = (1, 2.0)`,
// `p = P { a = 1.0, b = 2.0 }`, through the classification both lanes share
// (CodeGen.aggregatePrintOf); one with no print form (a variant, a component
// that is a function, ...) and UNIT bindings emit nothing, as
// genPrintStatements does. FUNCTION-valued bindings also print nothing (their
// IRTArrow type is not an array and falls through).
//
// ARRAY bindings (M2) dispatch exactly as CodeGen.genPrintStatements' ArrayElem
// arms do -- this module owns the kind dispatch, Interp/ArrayOps owns the cell/
// row traversal and the low-level array-line emitters. This wave renders:
//   * dense arrays          -> ArrayOps.emitFlat (genPrintArrayFlat: rank 2
//                              nested, every other rank >= 1 one flat run)
//   * rank 0                -> `<rank-0>` placeholder
//   * rank-1 struct arrays -> per-field print loop
//       `name = [{f1: V1, f2: [a, b]}, ...]` (mirrors genPrintStatements;
//       array fields print their values, CodeGen.structFieldPrint)
// and mirrors CodeGen's no-stdout (C++ comment only) arms as zero output:
//   compound arrays, function-valued arrays, struct arrays (rank>1/no fields),
//   ragged sub-views.
// The still-unrendered stdout-producing kinds are gated with PrintUnsupported so
// the caller classifies the whole program SKIP-UNSUPPORTED (never wrong bytes).
// No kind prints a POINTER in either lane any more, and the differential
// normalizers no longer mask one: a pointer in output is a divergence.
//
// TIMING LINE. genMainWrapper prints `<testName> completed in <elapsed>s` first,
// then the binding prints. `testName` is the source file stem, not IRModule.Name,
// so it is taken here as the `progName` parameter. `elapsed` is nondeterministic
// and the differential gate strips every line containing "completed in"
// (DiffOracle.normalize), so we emit a constant 0.
//
// LINE ENDINGS. C++ `endl` writes '\n' (the CRT may expand it to '\r\n' on a
// Windows text-mode stdout). The gate normalizes '\r\n' -> '\n' before
// comparing, so this printer emits '\n' for every line.
//
// Every scalar is rendered through Interp.CppFormat, the byte-pinned iostream
// mirror; this module contributes only the per-binding dispatch and the
// "<name> = " / newline framing.
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
/// would, driven by the binding's declared ElemType -- the ElemType fixes the
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

/// Render a tuple or struct Value in its literal spelling, walking the
/// shared classification (CodeGen.aggregatePrintOf) beside the value's
/// declared type -- the type fixes each scalar's operator<< overload, exactly
/// as it fixes a scalar binding's. Byte-twin of CodeGen.genPrintAggregate.
let rec private formatAggregate (name: string) (types: IRTypeDef list) (ap: Blade.CodeGen.AggregatePrint) (t: IRType) (v: Value) : string =
    let bad what =
        raise (PrintUnsupported $"binding '{name}': {what} (value case {v.GetType().Name})")
    match ap with
    | Blade.CodeGen.APScalar ->
        let et =
            match stripUnits t with
            | IRTNat _ -> Some ETInt64
            | t' -> elemThrough t'
        match et with
        | Some et when isPrintableScalarEt et -> formatScalar name et v
        | _ -> bad "a tuple/struct component is not a printable scalar"
    // An integer EnumIdx value: the binding's value was already mapped to its
    // KEY by printBindingsOnly's typed lookup (CodeGen's twin indexes the key
    // table), so it prints as the Int64 it now is.
    | Blade.CodeGen.APKeyed _ -> formatScalar name ETInt64 v
    | Blade.CodeGen.APDenseArray _ | Blade.CodeGen.APKeyedArray _ ->
        match stripUnits t, v with
        | ArrayElem at, VArray fa ->
            match elemThrough at.ElemType with
            | Some et -> ArrayOps.formatDenseArrayText fa et
            | None -> bad "an array component has no scalar element type"
        | _ -> bad "an array component holds no array value"
    | Blade.CodeGen.APOpaque text -> text
    | Blade.CodeGen.APTuple parts ->
        match stripUnits t, v with
        | IRTTuple ts, VTuple vs when ts.Length = parts.Length && vs.Length = parts.Length ->
            let items = List.map3 (fun p ct cv -> formatAggregate name types p ct cv) parts ts (List.ofArray vs)
            "(" + String.concat ", " items + ")"
        | _ -> bad "a tuple component holds no tuple value of the declared arity"
    | Blade.CodeGen.APStruct (sn, fields) ->
        let declared =
            types |> List.tryPick (function
                | IRTDStruct (n, fs) when n = sn -> Some fs
                | _ -> None)
        match declared, v with
        | Some fts, VStruct (_, fvs) ->
            let items =
                fields |> List.map (fun (f, p) ->
                    let ft = fts |> List.find (fun (n, _) -> n = f) |> snd
                    match fvs |> Array.tryPick (fun (n, x) -> if n = f then Some x else None) with
                    | Some fv -> f + " = " + formatAggregate name types p ft fv
                    | None -> bad $"struct value has no field '{f}'")
            Blade.Types.TypeOrigins.sourceName sn + " { " + String.concat ", " items + " }"
        | _ -> bad $"no struct value of type '{sn}'"

/// The interpreter twin of genPrintStatements' `--print` refusal: an AMBIENT
/// selection (BLADE_PRINT) naming something that is not a top-level binding,
/// or a binding that never prints (a deferred loop value, a streamed read),
/// would print nothing -- which reads as "computed nothing". Codegen splices a
/// BL7004 refusal; here it is raised as PrintUnsupported, so the notebook
/// lane falls through to the compiled lane, which reports that BL7004.
/// Same printability gate `printBindingsOnly` applies below.
let checkAmbientSelection (forcedIds: System.Collections.Generic.HashSet<IRId>) (irModule: IRModule) (names: Set<string>) : unit =
    let deferredIds = Blade.CodeGen.computeDeferredIds irModule.Bindings
    let rec printableValue (v: IRExpr) =
        match v with
        | IRCompute (IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRVar _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _) -> true
        | IRCompute inner -> printableValue inner
        | IRMethodFor _ | IRObjectFor _ -> false
        | _ -> true
    let isPrintable (b: IRBinding) =
        if Set.contains b.Id deferredIds && not (forcedIds.Contains b.Id) then false
        elif (match Map.tryFind b.Id irModule.ProviderReads with
              | Some spec -> spec.Streamed
              | None -> false) then false
        else printableValue b.Value
    // Same order as genPrintStatements: a name the program never bound, then
    // a function-valued binding (incl. an eliminated generic alias, which is
    // still a binding the source has), then a never-materialized one.
    // The masked read's raw half is not selectable (CodeGen's twin).
    let declared =
        irModule.Bindings
        |> List.filter (fun b ->
            match Map.tryFind b.Id irModule.ProviderReads with
            | Some spec -> not spec.AllowMissing
            | None -> true)
        |> List.map (fun b -> b.Name) |> Set.ofList
        |> Set.union irModule.FunctionAliasNames
    let unknown = Set.difference names declared
    if not unknown.IsEmpty then
        let ns = String.concat ", " unknown
        raise (PrintUnsupported $"--print: {ns} not a top-level binding")
    let functions = Set.intersect names (Blade.CodeGen.functionValuedBindingNames irModule) |> Set.toList
    if not functions.IsEmpty then
        raise (PrintUnsupported (Blade.CodeGen.functionPrintRefusal functions))
    let silent =
        irModule.Bindings
        |> List.filter (fun b -> Set.contains b.Name names && not (isPrintable b))
        |> List.map (fun b -> b.Name)
    if not silent.IsEmpty then
        let ns = String.concat ", " silent
        raise (PrintUnsupported $"--print: {ns} never materialized (deferred loop value)")
    let formless =
        irModule.Bindings
        |> List.filter (fun b ->
            Set.contains b.Name names && isPrintable b && Blade.CodeGen.hasNoPrintedForm irModule b
            && not (List.contains b.Name functions))
        |> List.map (fun b -> b.Name) |> List.distinct
    if not formless.IsEmpty then
        raise (PrintUnsupported (Blade.CodeGen.formlessPrintRefusal irModule formless))

/// Append to `sb` exactly what the compiled binary's main() prints -- the timing
/// line followed by the module's top-level binding prints, in declaration order.
///
///   progName  - the compiled binary's testName (source file stem); used only
///               for the (gate-stripped) `<progName> completed in 0s` line.
///   lookup    - fetches a binding's evaluated Value by its IRId.
///   forcedIds - module-level deferred bindings actually forced during
///               evaluation (InterpState.ForcedDeferred): one that ended up
///               materialized auto-prints (mirrors genPrintStatements'
///               forcedDeferredIdsCell); one that stayed deferred prints nothing.
///   irModule  - the lowered module whose Bindings drive print order and the
///               same skip/kind decisions CodeGen.genPrintStatements makes.
///   sb        - output sink; lines are '\n'-terminated (see module header).
///
/// Raises PrintUnsupported for binding kinds not handled in M0 (arrays, or a
/// scalar binding with a missing / mistyped value) so the caller can classify.
/// `only`: when Some, print ONLY bindings whose names are in the set. The
/// REPL/notebook lane reads a handful of `name = value` lines out of a run and
/// discards the rest, and formatting every session binding is O(total data)
/// per submission -- 600+ KB per eval on a modest field notebook. One-shot
/// callers (the differential gate, `blade test interp`) pass None and keep the
/// full compiled-parity output. A filtered-out binding is skipped BEFORE its
/// value is looked at, so a binding whose PRINT the interpreter does not
/// support cannot fail a run that never asked to see it.
let printBindingsOnly (progName: string) (lookup: IRId -> Value option) (forcedIds: System.Collections.Generic.HashSet<IRId>) (irModule: IRModule) (only: Set<string> option) (sb: StringBuilder) : unit =
    // A value of an INTEGER EnumIdx type prints its KEY: it is a key
    // represented as its position (D2) -- the twin of CodeGen's key-table
    // printers. Driven by the binding's TYPE, through arrays (any store:
    // dense, nested, ragged, boxed), tuples and struct fields.
    let lookup =
        let keysOf (t: IRType) =
            match stripUnits t with
            | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), IRefNamed tag) ->
                Blade.CodeGen.intEnumKeysOfModule irModule.Types tag |> Option.map List.toArray
            | _ -> None
        let structFieldsOf (n: string) =
            irModule.Types |> List.tryPick (fun td ->
                match td with
                | IRTDStruct (sn, fs) when sn = n -> Some fs
                | _ -> None)
        let key (keys: int64[]) (v: Value) =
            match v with
            | VInt p when p >= 0L && p < int64 keys.Length -> VInt keys.[int p]
            | VInt32 p when p >= 0 && p < keys.Length -> VInt keys.[p]
            | _ -> v
        let rec mentionsKeys (fuel: int) (t: IRType) =
            fuel > 0 &&
            (match stripUnits t with
             | ArrayElem at -> mentionsKeys (fuel - 1) at.ElemType
             | IRTTuple ts -> ts |> List.exists (mentionsKeys (fuel - 1))
             | IRTNamed n -> structFieldsOf n |> Option.exists (List.exists (fun (_, ft) -> mentionsKeys (fuel - 1) ft))
             | t -> (keysOf t).IsSome)
        let rec mapValue (fuel: int) (t: IRType) (v: Value) : Value =
            if fuel <= 0 then v else
            match keysOf t, stripUnits t, v with
            | Some keys, _, _ -> key keys v
            | None, ArrayElem at, VArray arr when mentionsKeys 8 at.ElemType ->
                let rec mapStore (st: Store) =
                    match st with
                    | SInt a ->
                        (match keysOf at.ElemType with
                         | Some keys -> SInt (a |> Array.map (fun p -> if p >= 0L && p < int64 keys.Length then keys.[int p] else p))
                         | None -> st)
                    | SObj a -> SObj (a |> Array.map (mapValue (fuel - 1) at.ElemType))
                    | SNested a -> SNested (a |> Array.map mapStore)
                    | SRagged (rows, lens, offs) -> SRagged (rows |> Array.map mapStore, lens, offs)
                    | _ -> st
                VArray { arr with Data = mapStore arr.Data }
            | None, IRTTuple ts, VTuple vs when ts.Length = vs.Length ->
                VTuple (Array.map2 (mapValue (fuel - 1)) (List.toArray ts) vs)
            | None, IRTNamed n, VStruct (sn, fs) ->
                (match structFieldsOf n with
                 | Some decl ->
                     VStruct (sn, fs |> Array.map (fun (fname, fv) ->
                         match decl |> List.tryFind (fun (dn, _) -> dn = fname) with
                         | Some (_, ft) -> (fname, mapValue (fuel - 1) ft fv)
                         | None -> (fname, fv)))
                 | None -> v)
            | _ -> v
        let keyedTypes =
            irModule.Bindings
            |> List.choose (fun b -> if mentionsKeys 8 b.Type then Some (b.Id, b.Type) else None)
            |> Map.ofList
        if keyedTypes.IsEmpty then lookup
        else
            fun (id: IRId) ->
                match lookup id, Map.tryFind id keyedTypes with
                | Some v, Some t -> Some (mapValue 16 t v)
                | v, _ -> v
    // Timing line first, mirroring genMainWrapper, where `timing` precedes
    // `printCode`. Constant elapsed (gate-stripped); see module header.
    sb.Append(progName).Append(" completed in 0s").Append('\n') |> ignore

    // Deferred combinator/compose/parallel/fusion/zip bindings emit no C++ code
    // (and no output). Reuse CodeGen's own computation to stay in lock-step.
    let deferredIds = Blade.CodeGen.computeDeferredIds irModule.Bindings

    let emitScalar (b: IRBinding) (et: ElemType) : unit =
        match lookup b.Id with
        // (A `0x0` arm stood here: a scalar-TYPED binding holding an array
        // printed a fake pointer to match the compiled binary's `cout << arr`
        // under the differential normalizers' pointer mask. The bracketed
        // outer forms that produced it now type as arrays and print values,
        // and the mask is gone, so a pointer in either lane's output is a
        // divergence the gate reports instead of hides.)
        | Some v ->
            let text = formatScalar b.Name et v
            sb.Append(b.Name).Append(" = ").Append(text).Append('\n') |> ignore
        | None ->
            raise (PrintUnsupported $"binding '{b.Name}': no evaluated value")

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
    // in C++ -- CodeGen does not auto-print it (comment only). Partial reads
    // materialize real dense arrays and print normally.
    let isCompoundRowSubview (b: IRBinding) : bool =
        match b.Value with
        | IRIndex (a, (IRTuple coords) :: _, _) ->
            (match Blade.IR.typeOf a with
             | ArrayElem at when Blade.CodeGen.isCompoundArrayType at || Blade.CodeGen.isSparseArrayType at ->
                 let k = at.IndexTypes |> List.tryFind (fun ix -> ix.IxKind = IxKCompound || ix.IxKind = IxKSparse)
                         |> Option.map _.Rank |> Option.defaultValue coords.Length
                 (match Blade.IR.classifyCompoundIndexTuple k coords with
                  | Blade.IR.CompoundFull -> true | Blade.IR.CompoundPartial _ -> false)
             | _ -> false)
        | _ -> false

    let printArrayBinding (b: IRBinding) (arrType: IRArrayType) : unit =
        let rank = Blade.CodeGen.arrayRank arrType
        if Blade.CodeGen.isCompoundArrayType arrType then
            // G22: a compound prints its present cells (CodeGen's compound arm);
            // a non-scalar element has no printed form (comment only).
            match Blade.CodeGen.compoundPrintShape arrType with
            | Some nested ->
                match lookup b.Id with
                | Some (VCompound cv) -> ArrayOps.printCompoundCells b.Name cv nested sb
                | Some _ -> raise (PrintUnsupported $"compound binding '{b.Name}': value is not a VCompound")
                | None -> raise (PrintUnsupported $"compound binding '{b.Name}': no evaluated value")
            | None -> ()
        elif Blade.CodeGen.isSparseArrayType arrType then
            ()   // CodeGen emits a diagnostic C++ comment only -> zero stdout.
        elif isCompoundRowSubview b then
            ()   // raw trailing-row T* view: not auto-printed.
        else
        match arrType.ElemType with
        | FuncElem _ ->
            ()   // arrays of function values: comment only (std::function unstreamable).
        | IRTNamed _ ->
            // A rank-1 array of structs prints each ELEMENT spelled like its
            // literal through the aggregate formatter a struct value uses
            // (formatAggregate over CodeGen.aggregatePrintOf) -- the twin of
            // genPrintStatements' arm: `pts = [P { x = 1.0, y = 2.0 }, ...]`.
            // No element print form, or rank > 1 -> CodeGen comment, nothing.
            match Blade.CodeGen.aggregatePrintOf irModule.Types arrType.ElemType with
            | Some ap when rank = 1 ->
                match lookup b.Id with
                | Some (VArray ba) ->
                    let n = if ba.Extents.Length = 0 then 0L else ba.Extents.[0]
                    // Render every element FIRST: an element this printer
                    // cannot render defers the whole program before any byte
                    // of the line is written.
                    let items =
                        [ for i in 0L .. n - 1L ->
                            formatAggregate b.Name irModule.Types ap arrType.ElemType (ArrayOps.readCell ba [ i ]) ]
                    sb.Append(b.Name).Append(" = [").Append(String.concat ", " items).Append("]").Append('\n') |> ignore
                | Some _ ->
                    raise (PrintUnsupported $"rank-1 struct array '{b.Name}': value is not a VArray")
                | None ->
                    raise (PrintUnsupported $"rank-1 struct array '{b.Name}': no evaluated value")
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
                && b.Value.IsIRArrayLit
            let isRaggedPeelOutput =
                Blade.CodeGen.isRaggedArrayType arrType
                && (match unwrapMaterialization b.Value with IRApplyCombinator _ -> true | _ -> false)
            let isRaggedRowBinding =
                Blade.CodeGen.isRaggedRowType arrType
                && b.Value.IsIRIndex
            if isRaggedPeelOutput || isRaggedRowBinding || isRaggedLiteralBinding then
                // Ragged literals, rank-1 ragged ROWS, and apply-produced ragged
                // PEEL OUTPUTS all render as the flat backing-pool value sequence,
                // which ArrayOps.printArrayBinding (byte-verified on the literal
                // path) reproduces: a ragged LITERAL / rank>=2 elementwise-map
                // peel output shares lens+prefix-offsets metadata (CodeGen
                // iterates via .lens, ArrayOps flattens SRagged identically); a
                // rank-1 PEEL OUTPUT is rank-1 RECTANGULAR at runtime, matching
                // genPrintArrayFlat 1 byte-for-byte. GATED on a materialized
                // VArray: if the ragged-apply layer has not produced one, the
                // whole program SKIP-classifies before Print runs.
                match lookup b.Id with
                | Some (VArray ba) -> ArrayOps.printArrayBinding b ba sb
                | _ -> raise (PrintUnsupported $"ragged/dep-idx array '{b.Name}' print: no materialized value yet (M2.7 ragged apply gated upstream)")
            elif Blade.CodeGen.isRaggedArrayType arrType then
                ()   // ragged sub-view: CodeGen comment only.
            else
                // DENSE (rank 2 nested, every other rank flat) OR
                // symmetric-aware: delegate the array-LINE emission to
                // ArrayOps.printArrayBinding (byte-verified against the
                // compiled binary; owns the flat/nested +
                // genPrintArraySymAware formats, and its own
                // ragged/non-scalar backstop -> ArrayOpUnsupported).
                // The materialized value must be a VArray (else defer: the
                // interpreter has not produced a printable image yet).
                match lookup b.Id with
                | Some (VArray ba) ->
                    ArrayOps.printArrayBinding b ba sb
                | Some _ ->
                    raise (PrintUnsupported $"array binding '{b.Name}': value is not a VArray")
                | None ->
                    raise (PrintUnsupported $"array binding '{b.Name}': no evaluated value")

    for b in irModule.Bindings do
        // isPrintable: a faithful mirror of CodeGen.genPrintStatements'
        // per-binding gate. A binding does not print if it is deferred, is a
        // streamed provider read, or is an unmaterialized loop value
        // (IRMethodFor / IRObjectFor, computed or not).
        // |> compute of a DEFERRED combinator is a forced materialization and
        // always prints; |> compute of anything ELSE prints exactly when the
        // wrapped value itself would (mirrors genPrintStatements'
        // printableValue byte-for-byte -- an eager reduce/scalar is unchanged
        // by compute). Unmaterialized loop values never print.
        let rec printableValue (v: IRExpr) =
            match v with
            | IRCompute (IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRVar _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _) -> true
            | IRCompute inner -> printableValue inner
            | IRMethodFor _ | IRObjectFor _ -> false
            | _ -> true
        let isPrintable =
            if Set.contains b.Id deferredIds && not (forcedIds.Contains b.Id) then false
            elif (match Map.tryFind b.Id irModule.ProviderReads with
                  | Some spec -> spec.Streamed
                  | None -> false) then false
            else printableValue b.Value

        let wanted =
            match only with
            | Some names -> Set.contains b.Name names
            // Elaborator-internal `__` names are not echoed (genPrintStatements'
            // twin, CodeGen.isInternalBindingName).
            | None -> not (Blade.CodeGen.isInternalBindingName b.Name)
        if isPrintable && wanted then
            // Type dispatch mirrors genPrintStatements after IR.stripUnits.
            match stripUnits b.Type with
            | IRTScalar et when isPrintableScalarEt et ->
                emitScalar b et
            | IRTScalar _ ->
                // ETUnit (and any future non-printable scalar): genPrintStatements
                // falls through to [] (no output).
                ()
            | IRTIdxTagged (inner, _) ->
                // A nominal index-tagged value (Nat<I>, ...) prints as its
                // underlying int scalar (genPrintStatements: genPrintScalar).
                match elemThrough inner with
                | Some et -> emitScalar b et
                | None -> ()
            | IRTNat _ ->
                // Type-level natural in value position (a Nat tuple component
                // from destructuring, a `Nat<unit>` scalar after stripUnits):
                // genPrintStatements renders it as size_t and prints; mirror
                // with the int scalar path.
                emitScalar b ETInt64
            | ArrayElem arrType ->
                printArrayBinding b arrType
            | IRTTuple _ | IRTNamed _ when not (Blade.CodeGen.isDestructureCarrier b) ->
                // Tuples and structs in their literal spelling; one with no
                // print form (a variant, a function component) prints nothing,
                // as genPrintStatements' twin arm does.
                match Blade.CodeGen.aggregatePrintOf irModule.Types b.Type with
                | Some ap ->
                    match lookup b.Id with
                    | Some v ->
                        let text = formatAggregate b.Name irModule.Types ap b.Type v
                        sb.Append(b.Name).Append(" = ").Append(text).Append('\n') |> ignore
                    | None -> raise (PrintUnsupported $"binding '{b.Name}': no evaluated value")
                | None -> ()
            | IRTTuple _ -> ()   // a destructuring carrier: its components print by name
            | IRTNamed _ -> ()
            | IRTUnit -> ()      // genPrintStatements: [] -- unit prints nothing
            | _ -> ()            // function values (IRTArrow non-array), inference vars, etc.

