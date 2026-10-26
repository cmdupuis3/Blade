// Loop-nest codegen: element-binding peel/streamed scaffolding, OpenMP
// pragma placement, the loop-nest core (genLoopNest and the flat-
// elementwise and BLAS/LAPACK dispatch), runtime-header deployment,
// tracked-allocation bookkeeping, and array-literal/scalar bindings.
module Blade.CodeGenLoopNest

open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.EmitCpp
open Blade.ReynoldsCore
open Blade.CodeGenState
open Blade.CodeGenExprSupport
open Blade.CodeGenExpr

/// Convert IRExpr to C++ using context
let exprToCppCtx (ctx: CodeGenContext) (expr: IRExpr) : string =
    exprToCpp ctx.VarNames expr

/// Render an IRExpr with a contains-substitution map active. Intended for a
/// mask renderer that walks its predicate, hoists builds for each hoistable
/// contains, builds the substitution map, and then calls this function to
/// produce the predicate's C++ string with `set.count(...)` substituted for
/// each hoisted IRContains node.
///
/// With an empty substitution this is byte-identical to `exprToCpp`.
///
/// The substitution propagates through every renderer in the rec group:
/// `exprToCppCore`, `exprToCppWithVarCore`, `genApplyCombinatorExpr`, and
/// `materializeInlineForm`. That means a contains nested inside a method_for
/// kernel inside a mask predicate gets the same treatment as a contains
/// directly in the predicate. NOT YET WIRED UP: every current caller of
/// `materializeInlineForm` / `genApplyCombinatorExpr` (the binding-level
/// entry points) passes `emptySubst`; a future mask renderer would populate
/// a real subst map when materializing a mask whose predicate carries probes.
let exprToCppWithSubst (subst: SubstMap) (names: Map<IRId, string>) (expr: IRExpr) : string =
    exprToCppCore subst names expr

// Loop Nest Code Generation

// ---------------------------------------------------------------------------
// CLOSED-FORM ENUMERABLE DOMAINS AS AN AFFINE NEST (docs/plans/structural/06,
// bench gap G4). A `range<R>` over a `static struct` whose conjuncts are linear
// carries `IRSparseKeys (SkDomain plan)`. Its ONE loop level is rendered as r
// nested `for`s -- field k over [max(lower forms), min(upper forms)], the ends
// affine in the outer fields -- with the level's own counter `__iL` (the cell's
// lex rank, which IS its storage offset in the sparse-shaped output) declared
// in the OUTERMOST init and stepped in the INNERMOST. One opening line, one
// closing brace: every nest emitter that renders a level through
// genForLoopHeader gets the nest unchanged, and kernel params bind the FIELD
// VALUES (`__iL_dk`) -- never an unhash of a key table, never a box scan.
// ---------------------------------------------------------------------------

/// The plan when this level iterates a closed-form domain through a VIRTUAL
/// `range<R>` element. A level driven only by a materialized domain array keeps
/// its flat walk over the compact buffer (`R.data[__iL]`), which is already
/// O(card) and needs no coordinates.
let domainPlanOfLevel (level: LoopIndexBinding) : DomainPlan option =
    match level.Extent with
    | IRSparseKeys (SkDomain plan)
        when level.Elements |> List.exists (fun e -> e.Virtual.IsVirtualRange) -> Some plan
    | _ -> None

/// The C++ name of field k's loop variable at this level.
let domainFieldVar (level: LoopIndexBinding) (k: int) : string = $"{level.IndexName}_d{k}"

/// One affine form `c + sum a_j x_j` over the field variables, written the way
/// a reader writes it (`__i0_d0 - 2LL`, not `(-2LL) + 1LL * __i0_d0`).
let domainAffineCpp (fieldVar: int -> string) (f: DomainAffine) : string =
    let parts =
        f.Coefs
        |> List.mapi (fun n (j, a) ->
            let v = fieldVar j
            let mag = abs a
            let body = if mag = 1L then v else $"{mag}LL * {v}"
            if n = 0 then (if a < 0L then "-" + body else body)
            else (if a < 0L then " - " + body else " + " + body))
        |> String.concat ""
    match parts, f.Const with
    | "", c -> $"{c}LL"
    | p, 0L -> p
    | p, c when c < 0L -> $"{p} - {-c}LL"
    | p, c -> $"{p} + {c}LL"

/// A level's end: the MAX of its lower forms, the MIN of its upper forms.
let domainBoundCpp (fieldVar: int -> string) (isLower: bool) (b: DomainBound) : string =
    let fn = if isLower then "std::max<int64_t>" else "std::min<int64_t>"
    // Constant forms fold to their tightest one at plan time; affine forms stay.
    let consts, affine = b |> List.distinct |> List.partition (fun f -> f.Coefs.IsEmpty)
    let constForm =
        match consts with
        | [] -> []
        | cs -> [ { Const = (if isLower then cs |> List.map _.Const |> List.max else cs |> List.map _.Const |> List.min); Coefs = [] } ]
    match (constForm @ affine) |> List.map (domainAffineCpp fieldVar) with
    | [] -> if isLower then "INT64_MIN" else "INT64_MAX"
    | [ one ] -> one
    | first :: rest -> rest |> List.fold (fun acc t -> $"{fn}({acc}, {t})") first

/// The single-line header of a domain level: r nested `for`s, each hoisting its
/// upper end into `__iL_ek`, the counter carried from the outermost init to the
/// innermost step when `withCounter` (a fold has no output to address and
/// leaves it out, which keeps the innermost loop in OpenMP canonical form).
let genDomainLoopHeaderWith (withCounter: bool) (level: LoopIndexBinding) (plan: DomainPlan) : string =
    let r = plan.Levels.Length
    let v = domainFieldVar level
    plan.Levels
    |> List.mapi (fun k lvl ->
        let lo = domainBoundCpp v true lvl.Lo
        let hi = domainBoundCpp v false lvl.Hi
        let e = $"{level.IndexName}_e{k}"
        let counterInit = if withCounter && k = 0 then $", {level.IndexName} = 0" else ""
        let counterStep = if withCounter && k = r - 1 then $", ++{level.IndexName}" else ""
        $"for (int64_t {v k} = {lo}, {e} = {hi}{counterInit}; {v k} <= {e}; ++{v k}{counterStep})")
    |> String.concat " "
    |> fun s -> s + " {"

let genDomainLoopHeader (level: LoopIndexBinding) (plan: DomainPlan) : string =
    genDomainLoopHeaderWith true level plan

/// The LICENSED fold over a closed-form domain, as C++ statements (unindented,
/// one block): the walk `order` chooses (domainFoldOrder) with the licence
/// spent as `omp simd reduction` on the innermost run, the nest carrying no
/// cell counter so that run stays in OpenMP canonical form.
///
/// `threaded` (bench gap G31: a `where omp` fold kernel, threads emitted, the
/// licence live) wraps the nest in one explicit team with PRIVATE partials and
/// a FIXED-ORDER combine, Path B's discipline: the OUTERMOST enumerated
/// variable (k on a diagonal walk, the first field otherwise) is cut into
/// contiguous per-thread blocks, and when the team outnumbers its trips the
/// innermost run of every outer value is cut instead -- so a 3-diagonal band
/// still splits across 8 threads. Partials combine in thread order after the
/// region: for a given team size the value is a fixed function of the data,
/// run to run. The outermost level's bounds are constants by construction
/// (nothing encloses it), which is what lets it be partitioned up front.
let genDomainFoldCpp (level: LoopIndexBinding) (plan: DomainPlan) (order: DomainFoldOrder)
        (name: string) (elemStr: string) (opStr: string) (threaded: bool)
        (prelude: string list) (peels: string list) (bodyExpr: string) : string list =
    let v = domainFieldVar level
    let plus (c: int64) = if c = 0L then "" elif c < 0L then $" - {-c}LL" else $" + {c}LL"
    // (variable, lower end, upper end) per nesting level, the outermost's
    // constant ends, and the statements that open the innermost body.
    let levels, outerLo, outerHi, innerPost =
        match order with
        | DomainDiagonalOrder dg ->
            let kv = $"{level.IndexName}_k"
            [ (kv, $"{dg.KLo}LL", $"{dg.KHi}LL")
              (v 0, $"std::max<int64_t>({dg.ILo}LL, {kv}{plus dg.JLo})", $"std::min<int64_t>({dg.IHi}LL, {kv}{plus dg.JHi})") ],
            dg.KLo, dg.KHi, [ $"const int64_t {v 1} = {v 0} - {kv};" ]
        | DomainLexOrder _ ->
            let lv0 = List.head plan.Levels
            let zeros = Array.zeroCreate<int64> plan.Levels.Length
            (plan.Levels |> List.mapi (fun k lvl -> (v k, domainBoundCpp v true lvl.Lo, domainBoundCpp v false lvl.Hi))),
            Blade.Types.domainLower lv0.Lo zeros, Blade.Types.domainUpper lv0.Hi zeros, []
    let r = levels.Length
    let acc = if threaded then "__dacc" else name
    let pad (d: int) (s: string) = String.replicate d "    " + s
    let body (d: int) =
        (innerPost @ peels |> List.map (pad d)) @ [ pad d $"{acc} = {acc} {opStr} {bodyExpr};" ]
    // The nest from level `k` down, at depth `d`.
    let rec nest (k: int) (d: int) : string list =
        let (var, lo, hi) = levels.[k]
        let e = $"{level.IndexName}_e{k}"
        let isOuter = k = 0 && threaded
        if k = r - 1 then
            let header =
                if isOuter then
                    [ pad d $"BLADE_OMP_SIMD_REDUCTION({opStr}:{acc})"
                      pad d $"for (int64_t {var} = __olo; {var} < __ohi; ++{var}) {{" ]
                elif threaded then
                    [ pad d $"int64_t {var}_lo = {lo};"
                      pad d $"int64_t {e} = {hi};"
                      pad d $"if (!__byOuter && {e} >= {var}_lo) {{ const int64_t __s = {var}_lo, __len = {e} - __s + 1; {var}_lo = __s + (__len * __rt) / __rnt; {e} = __s + (__len * (__rt + 1)) / __rnt - 1; }}"
                      pad d $"BLADE_OMP_SIMD_REDUCTION({opStr}:{acc})"
                      pad d $"for (int64_t {var} = {var}_lo; {var} <= {e}; ++{var}) {{" ]
                else
                    [ pad d $"const int64_t {e} = {hi};"
                      pad d $"BLADE_OMP_SIMD_REDUCTION({opStr}:{acc})"
                      pad d $"for (int64_t {var} = {lo}; {var} <= {e}; ++{var}) {{" ]
            header @ body (d + 1) @ [ pad d "}" ]
        else
            let header =
                if isOuter then pad d $"for (int64_t {var} = __olo; {var} < __ohi; ++{var}) {{"
                else pad d $"for (int64_t {var} = {lo}, {e} = {hi}; {var} <= {e}; ++{var}) {{"
            [ header ] @ nest (k + 1) (d + 1) @ [ pad d "}" ]
    if not threaded then
        [ "{" ] @ (prelude |> List.map (pad 1)) @ nest 0 1 @ [ "}" ]
    else
        (ompApiUsedCell ()).Value <- true
        let ident = if opStr = "*" then $"static_cast<{elemStr}>(1)" else $"static_cast<{elemStr}>(0)"
        [ "{" ]
        @ (prelude |> List.map (pad 1))
        @ [ pad 1 $"const int64_t __dlo = {outerLo}LL, __dn = {outerHi - outerLo + 1L}LL;"
            pad 1 "int __dT = blade_omp_get_max_threads();"
            pad 1 "if (__dT < 1) __dT = 1;"
            pad 1 $"{elemStr}* __dpart = new {elemStr}[__dT];"
            pad 1 $"for (int __t = 0; __t < __dT; ++__t) __dpart[__t] = {ident};"
            pad 1 "BLADE_OMP_PARALLEL_NUM_THREADS(__dT)"
            pad 1 "{"
            pad 2 "const int __rnt = blade_omp_get_num_threads();"
            pad 2 "const int __rt = blade_omp_get_thread_num();"
            pad 2 (if r = 1 then "const bool __byOuter = true;" else "const bool __byOuter = __dn >= (int64_t)__rnt;")
            pad 2 "const int64_t __olo = __byOuter ? __dlo + (__dn * __rt) / __rnt : __dlo;"
            pad 2 "const int64_t __ohi = __byOuter ? __dlo + (__dn * (__rt + 1)) / __rnt : __dlo + __dn;"
            // The thread's walk runs in a BY-VALUE closure: inside the outlined
            // region every captured array is a reference into shared memory, so
            // g++ re-read `x.data` through it per cell and turned the run's
            // contiguous loads into emulated gathers (vpextrq + vmovsd; band mt8
            // 1.9 ms against 0.7 ms serial). A per-thread copy of each captured
            // Array header (pointer + extents, never the pool) keeps them in
            // registers. `mutable`: a called wrapper may take an `Array&`. The
            // fold kernel writes no capture (BL4005 refuses a captured write in
            // an omp body), so the copies are read-only in effect.
            pad 2 $"const {elemStr} __dacc = [=]() mutable -> {elemStr} {{"
            pad 3 $"{elemStr} __dacc = {ident};" ]
        @ nest 0 3
        @ [ pad 3 "return __dacc;"
            pad 2 "}();"
            pad 2 "if (__rt < __dT) __dpart[__rt] = __dacc;"
            pad 1 "}"
            pad 1 $"for (int __t = 0; __t < __dT; ++__t) {name} = {name} {opStr} __dpart[__t];"
            pad 1 "delete[] __dpart;"
            "}" ]

/// `blade plan` / comment text for a plan's bounds: `i in [0, 10], j in
/// [max(0, i - 2), min(10, i + 2)]` in the struct's own field names.
let domainPlanSummary (plan: DomainPlan) : string =
    let names = plan.Levels |> List.map _.Field |> Array.ofList
    let v k = names.[k]
    let ends isLower (b: DomainBound) =
        let s = (domainBoundCpp v isLower b).Replace("std::max<int64_t>", "max").Replace("std::min<int64_t>", "min")
        System.Text.RegularExpressions.Regex.Replace(s, @"(\d)LL\b", "$1")
    plan.Levels
    |> List.map (fun lvl -> $"{lvl.Field} in [{ends true lvl.Lo}, {ends false lvl.Hi}]")
    |> String.concat ", "

/// Generate the element binding expression for a single array at a loop level
/// Returns (cppCode, newPeeledName) where newPeeledName is used for subsequent levels
///
/// `rawRowPeel`: when true, a
/// peel that leaves exactly ONE dimension is emitted as a raw
/// `T* BLADE_RESTRICT row = parent.data[i];` instead of the `Array<T,1>` wrapper.
/// The caller (`restrictPeelSites`) is what establishes the precondition: the
/// peeled local must be consumed EXCLUSIVELY by a deeper scalar-leaf peel
/// (`row[j]`), whose rendered text is byte-identical for a wrapper and a raw
/// pointer. Never set for a local that escapes as an `Array<T,1>` value (fiber
/// kernels, `.extents[0]` intrinsic bounds, streamed binds, captures).
///
/// Soundness of the qualifier itself: every kernel output is a fresh
/// `allocate<>`/`allocate_strict<>` pool, so no written pointer ever aliases a
/// read row; read/read aliasing (`f(A, A)`) is permitted under restrict.
let genElementBindingPeel (rawRowPeel: bool) (level: LoopIndexBinding) (elem: ElementBinding) (currentName: string)
    : string * string =
    match elem.Virtual with
    | VirtualRange offset when
        (level.Extent.IsIRCompoundMask) &&
        (match elem.SlotTag with Some t when t.StartsWith "__halowin|" -> true | _ -> false) ->
        // halo<CompoundIdx<m>>: the kernel param is a POINTER into the
        // materialized compound index's contiguous rank_to_tuple table at the
        // CENTER cell (ordinal i + start). Body reads w(o) then step it by a
        // signed subscript (IRHaloUnhash: `w[(o)][0]`) -- param-local, so the
        // reads survive kernel lifting to a standalone function. The interior
        // bound shrink is on the loop header (StrictOffset).
        let centerExpr =
            match offset with
            | None -> level.IndexName
            | Some (IRLit (IRLitInt n)) -> $"({level.IndexName} + {n}L)"
            | Some off -> $"({level.IndexName} + {(exprToCpp Map.empty off)})"
        let code =
            $"const std::array<size_t, 1>* {elem.ParamName} = &{elem.ArrayName}_cidx->rank_to_tuple[{centerExpr}];"
        (code, elem.ParamName)
    | VirtualRange _ when (domainPlanOfLevel level).IsSome ->
        // range<R> over a closed-form static-struct domain: the level is the
        // affine nest genDomainLoopHeader renders, and each kernel param binds
        // its FIELD's loop variable -- the coordinate itself, no key table.
        (sprintf "int64_t %s = %s;" elem.ParamName (domainFieldVar level elem.RankComponent), elem.ParamName)
    | VirtualRange _ when (match level.Extent with IRCompoundMask _ | IRSparseKeys _ -> true | _ -> false) ->
        // range<CompoundIdx<m>> / range<SparseIdx<keys>>: ONE loop level over
        // the present cells (compound: lex order; sparse: given key order); each
        // kernel param binds one COORDINATE of the current cell's tuple via
        // the materialized index's O(1) unhash (rank_to_tuple lookup). The
        // index variable `<name>_cidx` is emitted by the caller's
        // materialization step (genCompoundIndexFromMask) before the nest.
        // Offsets are not meaningful on a masked product space; TypeCheck
        // never produces one for a compound range slot.
        let code = $"int64_t {elem.ParamName} = {elem.ArrayName}_cidx->unhash({level.IndexName})[{elem.RankComponent}];"
        (code, elem.ParamName)
    | VirtualRange offset ->
        // range<I>: kernel param gets the loop index, plus offset if present.
        //
        // MULTI-RANK SLOTS LAND HERE TOO, and this arm is rank-1 shaped: it
        // binds the RAW loop counter and never consults level.BoundDependencies
        // / level.StrictOffset, which the RealArray arms below apply to reach
        // the ABSOLUTE coordinate. So range<SymIdx<r,N>> hands the kernel the
        // cell's left-justified PACKED STORAGE COORDINATES -- prefix offsets --
        // where canonical[m] = p0 + ... + pm (+ m for AntisymIdx's strict step).
        // The kernel spelling A(p0) * A(p0 + p1) is correct; A(p0) * A(p1) is
        // the silent trap. Interp.Loops.peelElement mirrors this arm, so the
        // differential gates see agreement, not the divergence from
        // TypeCheck.expandedRows ("the index value at that slot") and
        // docs/formalism.md 7.3 -- which now records this as observed behaviour
        // pending a decision, with the convention pinned by
        // tests/corpus/loops/170-173. Folding `deps + strict` in here (the
        // expression the dense arm already builds) is the canonical-index fix.
        //
        // The binding must be int64_t, NOT size_t: the param is Int64-typed in
        // Blade (and the standalone lambda signature), and a size_t binding
        // makes negative intermediates wrap unsigned before any Float64
        // conversion -- 0.5 * (i - 1) at i=0 came out as 0.5 * 2^64-1.
        // Same signedness rule for the unhash and reverse arms above/below.
        let valueExpr =
            match offset with
            | None -> level.IndexName
            | Some (IRLit (IRLitInt n)) -> $"({level.IndexName} + {n}L)"
            | Some off -> $"({level.IndexName} + {(exprToCpp Map.empty off)})"
        let code = $"int64_t {elem.ParamName} = {valueExpr};"
        (code, elem.ParamName)
    | VirtualReverse ->
        // reverse<I>: kernel param gets (extent - 1 - i)
        let extentStr =
            match level.Extent with
            | IRLit (IRLitInt n) -> string n
            | _ -> $"{elem.ArrayName}.extents[{elem.DimIndex}]"
        let code = $"int64_t {elem.ParamName} = ({extentStr} - 1 - {level.IndexName});"
        (code, elem.ParamName)
    | RealArray when level.FusedRank.IsSome ->
        // Arc 1 fused JOINT level (see IRLoopStructure.fuseJointSLevels): this single loop
        // level spans the argument's whole plain-dense S-block (d dims), so the
        // grouped triangular iteration ranges over whole argument index tuples --
        // the joint symmetry, the only one an identity group licenses
        // (docs/formalism.md section 12.4). The loop var is left-justified-relative
        // under triangular chaining, so component 0 first shifts it to the
        // ABSOLUTE compound coordinate p (deps + strict offset, mirroring the
        // dense arm's case 1) and binds it once per (level, array); every
        // component then decodes its per-dim coordinate row-major
        //   coord_rc = (p / prod_{j>rc} n_j) % n_rc
        // (matching lex enumeration and the storage bijection) and peels
        // exactly one dimension of the array.
        let d = level.FusedRank.Value
        let rc = elem.RankComponent
        let extAt j = $"{elem.ArrayName}.extents[{j}]"
        let strideAfter k =
            if k >= d - 1 then "1"
            else [k + 1 .. d - 1] |> List.map extAt |> String.concat " * "
        let pAbs = $"__p{level.Level}_a{elem.ArrayPosition}"
        let pAbsDecl =
            if rc = 0 then
                let depParts = level.BoundDependencies |> List.map (sprintf "__i%d")
                let offsetParts = if level.StrictOffset > 0 then [string level.StrictOffset] else []
                let sum =
                    match depParts @ offsetParts with
                    | [] -> level.IndexName
                    | shifts -> $"""{level.IndexName} + {(String.concat " + " shifts)}"""
                $"size_t {pAbs} = {sum}; "
            else ""
        let coordName = $"{pAbs}_c{rc}"
        let coordExpr =
            if d = 1 then pAbs
            elif rc = 0 then $"{pAbs} / ({(strideAfter 0)})"
            elif rc = d - 1 then sprintf "%s %% %s" pAbs (extAt rc)
            else sprintf "(%s / (%s)) %% %s" pAbs (strideAfter rc) (extAt rc)
        let levelsConsumed = rc + 1
        let resultRank = elem.ArrayRank - levelsConsumed
        let elemTypeStr = elemTypeToCpp elem.ArrayElemType
        let newName = $"{currentName}__{level.IndexName}_{rc}"
        let peel =
            if resultRank <= 0 then
                $"{elemTypeStr} {newName} = {currentName}[{coordName}];"
            elif rawRowPeel then
                // resultRank = 1 and every consumer is `newName[j]`: drop the
                // wrapper for a restrict-qualified row pointer.
                $"{elemTypeStr}* BLADE_RESTRICT {newName} = {currentName}.data[{coordName}];"
            else
                $"Array<{elemTypeStr}, {resultRank}> {newName} = {{ {currentName}.data[{coordName}], {currentName}.extents + 1 }};"
        let code = $"{pAbsDecl}size_t {coordName} = {coordExpr}; {peel}"
        (code, newName)
    | RealArray when (match level.Extent with IRCompoundMask _ | IRSparseKeys _ -> true | _ -> false) ->
        // Tabulated (compound/sparse) axis: peel the present-cell index `r` against the COMPACT
        // buffer (.data), not the dense .extents grid. A compound axis has no
        // bound-dependency / strict shifts (it is its own group) and the compound
        // level is the array's first level, so the index is just the loop var.
        // With no trailing dims (all-dims mask, resultRank <= 0) this is the
        // scalar leaf data[r]; with a trailing dim it yields the trailing ROW
        // base pointer (data + r*trailing_stride), which the ordinary dense peel
        // then indexes for the (single) trailing level. Ragged trailing (variable
        // per-cell extent) needs a cell_offset table and is not yet emitted.
        let levelsConsumed = elem.RankComponent + 1
        let resultRank = elem.ArrayRank - levelsConsumed
        let elemTypeStr = elemTypeToCpp elem.ArrayElemType
        let r = level.IndexName
        let newName = $"{currentName}__{r}"
        let code =
            if resultRank <= 0 then
                $"{elemTypeStr} {newName} = {currentName}.data[{r}];"
            else
                // Already a raw row pointer; the restrict qualifier is free
                // here -- a raw `T*` can never have been forwarded as
                // an `Array<T,1>` value, so no consumer analysis is needed.
                $"{elemTypeStr}* BLADE_RESTRICT {newName} = {currentName}.data + {r} * {currentName}.trailing_stride;"
        (code, newName)
    | RealArray ->
        // After indexing once, remaining rank decreases
        let levelsConsumed = elem.RankComponent + 1  // How many levels of this array consumed so far
        let resultRank = elem.ArrayRank - levelsConsumed
        let elemTypeStr = elemTypeToCpp elem.ArrayElemType
        
        // Array index into THIS level, distinguished by whether the array has
        // already been peeled (sliced) at an outer level:
        //  (1) ORIGINAL array (currentName == elem.ArrayName): still flat, so
        //      the index is the ABSOLUTE coordinate = loop var + sum of
        //      bound-dependency vars + strict offset (producer-style flat
        //      read, e.g. A[__i1 + __i0]).
        //  (2) ALREADY-SLICED sub-array: each outer peel already consumed its
        //      index via `data[__ik]`, so the within-row index is the LOCAL
        //      loop var alone -- re-adding dependency vars double-counts the
        //      outer index and reads out of bounds (the compact-symmetric
        //      elementwise-read bug: sym____i0[__i1 + __i0] should be
        //      sym____i0[__i1]).
        //      StrictOffset is likewise dropped: a peeled row of a
        //      strict-packed array is ALREADY diagonal-free (build_skeleton
        //      seeds each antisym child at `i + lastIndex + 1` and shortens
        //      the row to match, so the 0-based loop var IS the slot); adding
        //      the strict offset would walk one cell past the row's canonical
        //      span, off the pool's end on the last row. (canon_left_justify,
        //      cpp:863, uses p[k] - p[k-1] - 1 for strict; this is that
        //      expression left-justified.)
        let isSliced = currentName <> elem.ArrayName
        let arrayIndex =
            if isSliced then level.IndexName   // outer index + strictness baked into the row
            else
            let depParts = level.BoundDependencies |> List.map (sprintf "__i%d")
            let offsetParts = if level.StrictOffset > 0 then [string level.StrictOffset] else []
            match depParts @ offsetParts with
            | [] -> level.IndexName
            | shifts -> $"""{level.IndexName} + {(String.concat " + " shifts)}"""
        
        let newName = $"{currentName}__{level.IndexName}"
        let code =
            if resultRank <= 0 then
                // Scalar leaf: peel returns the element value directly.
                $"{elemTypeStr} {newName} = {currentName}[{arrayIndex}];"
            elif rawRowPeel then
                // resultRank = 1 and the ONLY consumer is the deeper
                // scalar-leaf peel `newName[__ik]` -- which renders identically
                // against a raw pointer and against the wrapper's operator[].
                // Dropping the wrapper hands g++ a restrict-qualified row and,
                // with the output row hoisted the same way, a provable
                // no-alias pair.
                $"{elemTypeStr}* BLADE_RESTRICT {newName} = {currentName}.data[{arrayIndex}];"
            else
                // Sub-array peel: construct a wrapper so the sub still
                // carries shape information (.extents shifted one level
                // deeper). The wrapper's data pointer comes from indexing
                // the parent's data; the extents pointer is parent's
                // extents+1. Indexing transparency works through operator[].
                $"Array<{elemTypeStr}, {resultRank}> {newName} = {{ {currentName}.data[{arrayIndex}], {currentName}.extents + 1 }};"
        (code, newName)

/// Wrapper-preserving entry point. Kept as the public name so out-of-module
/// callers (unit tests) are unaffected; the in-module nest emitters call
/// `genElementBindingPeel` with the analysed flag.
let genElementBindingNew (level: LoopIndexBinding) (elem: ElementBinding) (currentName: string)
    : string * string =
    genElementBindingPeel false level elem currentName

/// The set of `(nest level index, ArrayPosition)` sites whose peel may drop
/// the `Array<T,1>` wrapper for a raw `T* BLADE_RESTRICT` row pointer. Qualifies
/// only when the emission context PROVES the peeled local is consumed
/// exclusively via `name[subscript]`:
///   1. the peel leaves exactly one dimension (`resultRank = 1`), and
///   2. the chain successor (same ArrayPosition, higher RankComponent, at this
///      level or deeper) is a RealArray peel bottoming out at a SCALAR leaf
///      (`name[__ik]`) -- every level of a real-array chain shares the SAME
///      `ParamVarId`, so the successor's name overwrites this one in
///      `paramFinalNames` and the intermediate never reaches the kernel body,
///   3. no level up to and including this one is a tabulated (compound/sparse)
///      axis -- that arm's peel emits `parent.data + ...`, whose parent must
///      stay a wrapper.
///
/// Elements sharing `(ArrayName, RankComponent)` at one level emit the SAME
/// C++ declaration name (`f(A, A)` dedup via `declaredNames`), so the decision
/// is per-group and granted only when every member qualifies -- else two
/// positions would emit two DIFFERENT declarations of one name (redeclaration
/// error).
let restrictPeelSites (bindings: LoopIndexBinding list) : Set<int * int> =
    let arr = List.toArray bindings
    let n = arr.Length
    let isTabulated (b: LoopIndexBinding) =
        match b.Extent with IRCompoundMask _ | IRSparseKeys _ -> true | _ -> false
    let isReal (e: ElementBinding) = e.Virtual.IsRealArray
    let chainEligible (li: int) (e: ElementBinding) =
        if not (isReal e) then false
        elif e.ArrayRank - (e.RankComponent + 1) <> 1 then false
        elif [ for lj in 0 .. li do
                 for e2 in arr.[lj].Elements do
                   if e2.ArrayPosition = e.ArrayPosition && e2.RankComponent <= e.RankComponent then
                     yield isTabulated arr.[lj] ] |> List.exists id then false
        else
            let successors =
                [ for lj in li .. n - 1 do
                    for e2 in arr.[lj].Elements do
                      if e2.ArrayPosition = e.ArrayPosition && e2.RankComponent > e.RankComponent then
                        yield (arr.[lj], e2) ]
            match successors with
            | [ (b2, e2) ] ->
                isReal e2
                && e2.RankComponent = e.RankComponent + 1
                && e2.ArrayRank - (e2.RankComponent + 1) <= 0
                && not (isTabulated b2)
            | _ -> false
    seq {
        for li in 0 .. n - 1 do
            for (_, grp) in arr.[li].Elements |> List.groupBy (fun e -> (e.ArrayName, e.RankComponent)) do
                if grp |> List.forall (chainEligible li) then
                    for e in grp do yield (li, e.ArrayPosition)
    } |> Set.ofSeq

/// Streamed-source element binding: the source is a `alias.stream` provider
/// read -- NO materialized array exists, so instead of peeling, accumulate
/// this level's ABSOLUTE site coordinate, and at the FIBER level (exactly
/// one trailing axis remaining) emit the provider's in-nest fiber read into
/// this element's dedicated buffer plus a rank-1 wrapper bind (the same
/// `Array<T,1>` shape a fiber peel would produce, so the kernel body is
/// untouched). Returns (lines, Some wrapperName at the fiber level, updated
/// accumulated site coordinates).
let genElementBindingStreamed (level: LoopIndexBinding) (elem: ElementBinding) (spec: ProviderReadSpec) (accSites: string list)
    : string list * string option * string list =
    let fiberGen =
        match Blade.ProviderRegistry.tryFind spec.Provider with
        | Some p ->
            (match p.GenStreamFiber with
             | Some g -> g
             | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"provider '{spec.Provider}' does not support streamed reads (variable '{spec.VarName}')"))))
        | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"provider '{spec.Provider}' is not registered")))
    let litExtents =
        spec.VarType.IndexTypes
        |> List.collect (fun ix -> List.replicate ix.Rank ix.Extent)
        |> List.map (fun e ->
            match e with
            | IRLit (IRLitInt n) -> n
            | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"streamed variable '{spec.VarName}' requires literal extents"))))
    let elemTypeStr = elemTypeToCpp elem.ArrayElemType
    let bufName = $"{elem.ArrayName}_fb_p{elem.ArrayPosition}"
    let emitFiber (sites: string list) (newName: string) : string list =
        fiberGen spec.FilePath spec.VarName elem.ArrayName bufName sites spec.VarType
        @ [ $$"""Array<{{elemTypeStr}}, 1> {{newName}} = { {{bufName}}, {{elem.ArrayName}}_fiber_ext };""" ]
    match elem.Virtual with
    | RealArray when level.FusedRank.IsSome ->
        // Fused joint level (identity-group comm over multi-dim sites): keep
        // the absolute-compound-coordinate shift and the row-major per-dim
        // decode, but with LITERAL extents (there is no array to consult),
        // and replace the chained peels with coordinate accumulation.
        let d = level.FusedRank.Value
        let rc = elem.RankComponent
        let strideAfter k =
            if k >= d - 1 then 1L
            else [k + 1 .. d - 1] |> List.fold (fun acc j -> acc * litExtents.[j]) 1L
        let pAbs = $"__p{level.Level}_a{elem.ArrayPosition}"
        let pAbsDecl =
            if rc = 0 then
                let depParts = level.BoundDependencies |> List.map (sprintf "__i%d")
                let offsetParts = if level.StrictOffset > 0 then [string level.StrictOffset] else []
                let sum =
                    match depParts @ offsetParts with
                    | [] -> level.IndexName
                    | shifts -> $"""{level.IndexName} + {(String.concat " + " shifts)}"""
                [ $"size_t {pAbs} = {sum};" ]
            else []
        let coordName = $"{pAbs}_c{rc}"
        let coordExpr =
            if d = 1 then pAbs
            elif rc = 0 then $"{pAbs} / {strideAfter 0}UL"
            elif rc = d - 1 then sprintf "%s %% %dUL" pAbs litExtents.[rc]
            else sprintf "(%s / %dUL) %% %dUL" pAbs (strideAfter rc) litExtents.[rc]
        let coordDecl = $"size_t {coordName} = {coordExpr};"
        let resultRank = elem.ArrayRank - (rc + 1)
        let sites' = accSites @ [coordName]
        if resultRank <= 0 then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"streamed variable '{spec.VarName}': elementwise consumption is not stream-eligible -- use a fiber kernel (rank-1 array parameter over the trailing axis) or bind with .read")))
        elif resultRank = 1 then
            let newName = $"{elem.ArrayName}__{level.IndexName}_{rc}"
            (pAbsDecl @ [coordDecl] @ emitFiber sites' newName, Some newName, sites')
        else
            (pAbsDecl @ [coordDecl], None, sites')
    | RealArray ->
        // Plain dense/triangular level: the ABSOLUTE coordinate (the source
        // is never sliced -- deps + strict offset always apply).
        let arrayIndex =
            let depParts = level.BoundDependencies |> List.map (sprintf "__i%d")
            let offsetParts = if level.StrictOffset > 0 then [string level.StrictOffset] else []
            match depParts @ offsetParts with
            | [] -> level.IndexName
            | shifts -> $"""{level.IndexName} + {(String.concat " + " shifts)}"""
        let resultRank = elem.ArrayRank - (elem.RankComponent + 1)
        let sites' = accSites @ [arrayIndex]
        if resultRank <= 0 then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"streamed variable '{spec.VarName}': elementwise consumption is not stream-eligible -- use a fiber kernel (rank-1 array parameter over the trailing axis) or bind with .read")))
        elif resultRank = 1 then
            let newName = $"{elem.ArrayName}__{level.IndexName}"
            (emitFiber sites' newName, Some newName, sites')
        else
            ([], None, sites')
    | _ ->
        raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"streamed variable '{spec.VarName}': virtual/compound binding shapes are not stream-eligible")))

/// Fiber destination buffers already declared in the CURRENT program --
/// a program with several nests over one streamed source must declare
/// each `<v>_fb_p<pos>` buffer once and reuse it (sequential nests; same
/// source => same length and element type). AsyncLocal like the symm-decl
/// collector: parallel test tasks get their own cell; program assembly
/// resets it.
let internal streamBufDeclsStorage =
    System.Threading.AsyncLocal<Set<string> ref>()

let streamBufDeclsCell () : Set<string> ref =
    let v = streamBufDeclsStorage.Value
    if isNull (box v) then
        let fresh = ref Set.empty
        streamBufDeclsStorage.Value <- fresh
        fresh
    else v

/// Streamed inputs across a set of leaf nests: the streamed map restricted
/// to their inputs, plus the per-argument fiber destination buffer
/// declarations to emit before the (merged) nest -- skipping buffers already
/// declared earlier in this program. Shared by the single-leaf and fused
/// paths so buffer naming cannot drift.
///
/// Third result: the buffer names THIS call newly declared. Callers running
/// under a deterministic-deallocation scope register them there: a
/// declaration emitted inside a function/loop body is scope-local -- its
/// C++ name dies at the closing brace -- so the frame must both delete[] it
/// on scope exit (a loop-frame declaration re-executes its `new` every
/// iteration, making the per-iteration delete a recycle) and retire it from
/// the dedup set above, or a later nest in a DIFFERENT scope would skip the
/// declaration and reference an out-of-scope name. Main-top declarations
/// (no live frame) keep the program-lifetime status quo.
let streamedNestSetup (streamedArrays: Map<string, ProviderReadSpec>) (ind: string) (leafCgs: LoopNestCodeGen list) : Map<string, ProviderReadSpec> * string list * string list =
    let streamedMap =
        leafCgs
        |> List.collect (_.InputArrayNames)
        |> List.distinct
        |> List.choose (fun n -> Map.tryFind n streamedArrays |> Option.map (fun s -> (n, s)))
        |> Map.ofList
    let declared =
        if Map.isEmpty streamedMap then []
        else
            let cell = streamBufDeclsCell ()
            leafCgs
            |> List.collect (_.Bindings)
            |> List.collect (_.Elements)
            |> List.choose (fun e ->
                match Map.tryFind e.ArrayName streamedMap with
                | Some spec when e.ArrayRank - (e.RankComponent + 1) = 1 ->
                    let bufName = $"{e.ArrayName}_fb_p{e.ArrayPosition}"
                    if Set.contains bufName cell.Value then None
                    else
                        cell.Value <- Set.add bufName cell.Value
                        let elemCpp = elemTypeToCpp e.ArrayElemType
                        let fiberLen =
                            match (List.last spec.VarType.IndexTypes).Extent with
                            | IRLit (IRLitInt n) -> n
                            | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"streamed variable '{spec.VarName}' requires literal extents")))
                        Some ($"{ind}{elemCpp}* {bufName} = new {elemCpp}[{fiberLen}];", bufName)
                | _ -> None)
            |> List.distinct
    (streamedMap, declared |> List.map fst, declared |> List.map snd)

/// Decide the OpenMP pragma for a loop NEST, based on the bound structure of
/// its levels.
///
///   - RECTANGULAR leading levels (BoundDependencies empty AND StrictOffset =
///     0) that are also COLLAPSE-ELIGIBLE fuse into
///     `#pragma omp parallel for collapse(d)`, d = count of leading
///     rectangular+eligible levels (d >= 2 to be worth it) -- valuable for
///     short outer dimensions (a 3x3x3 nest exposes only 3 threads without
///     collapse, 27 with collapse(3)).
///   - TRIANGULAR levels (inner bound depends on outer indices, e.g. j < N -
///     i) are non-rectangular, so `collapse` is unsafe; only the OUTERMOST
///     loop is parallelized, with `schedule(dynamic)` since triangular work
///     is unbalanced across the outer index.
///
/// COLLAPSE-ELIGIBILITY is a real gate, not a formality: OpenMP 5.0 permits
/// intervening code between collapsed loop headers (Blade's per-level element
/// peels rely on this, and gcc compiles/threads it correctly -- verified
/// serial-identical across repeated 4-thread runs), but code with
/// cross-iteration side effects (an OMP API call, a batch boundary) is NOT
/// permitted and makes the level collapse-INELIGIBLE; see the
/// collapseEligible predicate below for the exact rule.
///
/// Returns the pragma string (with trailing newline+indent) for the OUTERMOST
/// loop, or "" if the nest should not be parallelized. Inner loops never carry
/// a pragma. The decision is driven entirely by per-level bound structure
/// already present in the bindings -- no index-type tag is consulted, so new
/// index types get a sensible strategy from their bound shape automatically.
let genNestPragma (bindings: LoopIndexBinding list) (pragmaIndent: string) : string =
    match bindings with
    | [] -> ""
    | outer :: rest ->
        // BUILD KNOB. Every construct this function can return creates a TEAM,
        // so a serial-emission build returns "" from all of them -- the same
        // answer an unlicensed nest gets, which is the point: the nest is then
        // byte-identical to the one the same source without `omp` produces.
        // The decline is NOT silent: the caller's `ompSuppressedMarker` names
        // this knob (see `ompThreadsSuppressedReason`). See
        // `ompThreadEmissionEnabled` for why the licence stays in source.
        if not (ompThreadEmissionEnabled ()) then ""
        elif not outer.IsParallel then ""
        // a closed-form domain level's cell counter is sequential (pragmaLevelOf)
        elif (domainPlanOfLevel outer).IsSome then ""
        else
            // A level is "rectangular" iff its bound is independent of outer indices.
            let isRectangular (b: LoopIndexBinding) =
                b.BoundDependencies.IsEmpty && b.StrictOffset = 0
            // COLLAPSE-ELIGIBILITY GATE. Currently equals rectangularity, but is
            // a SEPARATE predicate by design: it is the single extension point
            // for the future "no inter-level injection" constraint (see header).
            // When a binding gains an inter-level-injection marker (batch
            // boundary, streaming stage), add that exclusion HERE -- e.g.
            //   isRectangular b && not b.HasInterLevelInjection
            // and the collapse prefix below will correctly stop before it.
            // A level must also be LICENSED by the `omp(a: n)` depth to join the
            // collapse: `n` is a per-argument permission ("up to n dimensions of
            // this argument may carry threads"), and collapse(d) threads all d
            // fused levels. Without this the depth was inert -- `omp(a: 1)` on a
            // 2-level nest emitted collapse(2), threading a dimension belonging
            // to an argument that granted nothing. IsParallel carries the
            // licence per level (IRStorage.buildLoopNestCodeGen).
            let collapseEligible (b: LoopIndexBinding) =
                isRectangular b && b.IsParallel
            // Collapse depth = length of the leading prefix that is BOTH
            // rectangular and collapse-eligible. (takeWhile stops at the first
            // level failing either condition -- that is the gate doing its job.)
            let collapseDepth =
                bindings |> List.takeWhile collapseEligible |> List.length
            // Any triangular level anywhere below the outer loop means the
            // per-outer-iteration work is unbalanced (inner extents shrink with
            // the outer index), so dynamic scheduling is warranted.
            let hasTriangularBelow =
                rest |> List.exists (fun b -> not (isRectangular b))
            if collapseDepth >= 2 then
                // Perfect, collapse-eligible rectangular prefix of >=2 levels:
                // fuse them. (A collapsed rectangular prefix is balanced; static.)
                $"BLADE_OMP_PARALLEL_FOR_COLLAPSE({collapseDepth})\n{pragmaIndent}"
            elif hasTriangularBelow then
                // Outer loop rectangular (or single), but triangular work below:
                // parallelize the outer loop with dynamic schedule for balance.
                //
                // THE OUTER LOOP STAYS ASCENDING, AND THAT IS THE LOAD-BALANCING
                // CHOICE, NOT THE ABSENCE OF ONE. A performance audit proposed
                // emitting this level DESCENDING (`for (i = n; i-- > 0;)`) on the
                // theory that OpenMP hands out `schedule(dynamic)` chunks in
                // iteration order, so descending would be largest-chunk-first
                // (the LPT heuristic). That is backwards for the loop shape this
                // arm actually governs, and reversing would PESSIMIZE it:
                //
                //   * `genForLoopHeader` renders a triangular level as
                //     `for (__i1 = 0; __i1 < N - __i0; __i1++)` -- the bound
                //     SUBTRACTS the outer index. Work per outer iteration is
                //     therefore DECREASING in `__i0` (at rank 3: ~C(N-i, 2)).
                //     Ascending order already hands out the largest chunk first.
                //   * The makespan of dynamic list-scheduling is about
                //     `ideal + (duration of the LAST chunk started)`. Ascending
                //     ends on the 1-cell row; descending ends on the ~C(N,2)-cell
                //     row, i.e. it converts the audit's own good bound into the
                //     bad one it was trying to escape.
                //
                // Verified against emitted C++ for a rank-3 `comm(x, y, z)` nest
                // at N = 13: levels are `__i1 < 13 - __i0` and
                // `__i2 < 13 - __i1 - __i0`.
                //
                // (Independently, `for (i = n; i-- > 0;)` is not an OpenMP
                // canonical loop form -- the test-expr must be `var op b` and
                // there must be an incr-expr -- so that spelling could not carry
                // this pragma at all. Any future descending experiment needs a
                // signed counter or a reversed-index body, and needs a REASON,
                // which the analysis above says does not exist for this shape.)
                $"BLADE_OMP_PARALLEL_FOR_DYNAMIC\n{pragmaIndent}"
            else
                // Outer loop parallel, remaining work balanced (rectangular or
                // none): plain static parallel for.
                $"BLADE_OMP_PARALLEL_FOR\n{pragmaIndent}"

/// Position (index into `bindings`) of the level that should carry the nest's
/// pragma: the OUTERMOST LICENSED one, which is not always level 0.
///
/// `omp(a: n)` licenses levels of argument `a`, and the argument owning the
/// outermost level need not be the one carrying the clause -- `omp(b: 1)` where
/// `b` owns level 1 licenses an INNER level and nothing outside it. Emitting at
/// level 0 there would thread a dimension the user never granted; emitting
/// nothing would silently ignore a clause they did write. So the pragma moves
/// inward to the first licensed level. Each outer iteration then opens a team
/// over that loop -- correct, because the licence is a statement about which
/// dimensions may be threaded, not about where the team is created.
///
/// Returns None when no level is licensed (no clause, or a clause covering
/// nothing), in which case the nest is serial.
let pragmaLevelOf (bindings: LoopIndexBinding list) : int option =
    // A closed-form domain level is a whole affine nest carrying ONE sequential
    // cell counter; a parallel-for over its outer field would race on it, so
    // it is never a pragma level (the omp census marker then says the licence
    // found no level to spend on).
    bindings |> List.tryFindIndex (fun b -> b.IsParallel && (domainPlanOfLevel b).IsNone)

/// Explain, IN THE GENERATED C++, why a nest whose kernel asked for OpenMP is
/// nonetheless emitted serial.
///
/// Parallelism is opt-in, so "no pragma" is the correct and expected output for
/// the vast majority of nests -- which is exactly why a DROPPED `omp(...)` clause
/// is invisible: the emitted code for "never asked" and "asked, couldn't honour"
/// is byte-identical. Suppression is legitimate in the cases below, but silent
/// suppression is not: a user who wrote `where omp(...)` and got serial code has
/// no way to tell which happened. The marker costs one comment line and lands
/// where someone debugging this actually looks.
///
/// Returns [] when the kernel never requested omp (the common case) or when the
/// pragma was in fact emitted.
let ompSuppressedMarker (requested: bool) (pragmaEmitted: bool) (reason: string)
                        (markerIndent: string) : string list =
    if requested && not pragmaEmitted then
        [ markerIndent + "// " + ompSuppressedPhrase reason ]
    else []

/// THE PATH B TEAM PROLOGUE, shared by every chunked fold region (the
/// single-fold chunk plan and its row-slot form in `genLoopNestStreamed`, and
/// the threaded reduction join in `CodeGenFusion.genFusedLoopNestStreamedWith`,
/// bench gap G7), so the partition, team sizing and empty-range rules cannot
/// drift between them:
///   * the outermost extent `bound` hoisted once into `rn`;
///   * the requested team `tVar` = omp_get_max_threads, bounded by the extent
///     (never more threads than indices) and by 1 from below;
///   * an empty range opens NO region (the caller's seeded accumulators are
///     the value);
///   * the range partitioned with the ACTUAL team size the runtime handed back
///     (`num_threads` is a request) through `blade_fold::chunk_lo` -- the ONE
///     function every Path B site spells.
/// Opens three scopes (block / non-empty guard / parallel region); the body
/// continues at `depth + 3`. `beforeRegion` lands inside the non-empty guard
/// (shared allocations), `inRegion` after the chunk bounds (private state).
/// `pathBTeamClose` closes the three scopes.
let pathBTeamOpen (depth: int) (rn: string) (tVar: string) (bound: string)
                  (beforeRegion: string list) (inRegion: string list) : string list =
    let ind n = String.replicate n "    "
    [ ind depth + "{"
      ind (depth + 1) + $"const size_t {rn} = {bound};"
      ind (depth + 1) + $"int {tVar} = blade_omp_get_max_threads();"
      ind (depth + 1) + $"if ((size_t){tVar} > {rn}) {tVar} = (int){rn};"
      ind (depth + 1) + $"if ({tVar} < 1) {tVar} = 1;"
      ind (depth + 1) + $$"""if ({{rn}} > 0) {""" ]
    @ (beforeRegion |> List.map (fun s -> ind (depth + 2) + s))
    @ [ ind (depth + 2) + $"BLADE_OMP_PARALLEL_NUM_THREADS({tVar})"
        ind (depth + 2) + "{"
        ind (depth + 3) + "const int __rnt = blade_omp_get_num_threads();"
        ind (depth + 3) + "const int __rt = blade_omp_get_thread_num();"
        ind (depth + 3) + $"const size_t __rlo = blade_fold::chunk_lo({rn}, (size_t)__rt, (size_t)__rnt);"
        ind (depth + 3) + $"const size_t __rhi = blade_fold::chunk_lo({rn}, (size_t)__rt + 1, (size_t)__rnt);" ]
    @ (inRegion |> List.map (fun s -> ind (depth + 3) + s))

/// The epilogue matching `pathBTeamOpen`, called with the REGION BODY's depth:
/// `inRegionTail` (each worker publishing its partials) at that depth, the
/// region closed, `afterRegion` (the fixed ascending-chunk combine, frees)
/// inside the non-empty guard, then the guard and the block closed.
let pathBTeamClose (depth: int) (inRegionTail: string list) (afterRegion: string list) : string list =
    let ind n = String.replicate n "    "
    (inRegionTail |> List.map (fun s -> ind depth + s))
    @ [ ind (depth - 1) + "}" ]
    @ (afterRegion |> List.map (fun s -> ind (depth - 1) + s))
    @ [ ind (depth - 2) + "}"
        ind (depth - 3) + "}" ]

/// Pragma (or decline marker) for the ROW LOOP of the ragged / grouped peels --
/// `tryRaggedPeel` and `tryGroupedZipPeel` in `genApplyCombinator`.
///
/// WHY THIS IS NOT `genNestPragma`. That function takes a `LoopIndexBinding list`
/// and picks collapse-vs-dynamic from per-level bound structure. The peels build
/// no bindings at all -- they bypass the loop-nest builder PRECISELY BECAUSE the
/// inner extent is ragged (per row, from an offsets table or a `lens[]` load) and
/// no `LoopIndexBinding` can describe it. Handed a single synthesized binding,
/// `genNestPragma`'s `rest` is empty, so `hasTriangularBelow` is always false and
/// it can only ever return a plain `parallel for`: it structurally cannot express
/// "one level, data-dependent imbalance", which is the entire shape here.
/// Synthesizing a dummy binding (11 fields, including an `IRExpr` extent and a
/// `SymcomState`) to extract a string this function returns in one line would be
/// a fragile lie about the nest's structure.
///
/// `schedule(dynamic)` for the same reason `genNestPragma`'s triangular arm uses
/// it: per-iteration work is proportional to the row length, and group/row lengths
/// are DATA -- arbitrarily uneven, so a static split hands one thread the long
/// rows. Unlike the triangular case the imbalance is not even monotone in `__g`,
/// so there is no ascending/descending ordering argument to make instead; dynamic
/// is the only schedule that says anything true about this shape.
///
/// NEVER SILENT WHEN THE KERNEL ASKED. The `else` branch is a marker, not `[]`,
/// because "requested but licensed nothing" is REACHABLE: `Parser.parseOmpArgs`
/// accepts any `TokInt`, so `omp(g: 0)` parses and yields `IsOmpParallel = true`
/// with a maximum depth of 0. Returning `[]` there would recreate exactly the
/// silent-drop this function exists to fix.
///
/// The decline reasons are DISTINCT STRINGS, and the knob comes first -- the same
/// order `genLoopNestStreamed` and `genFusedLoopNestStreamed` use -- so a
/// `grep "[omp]"` census over generated code can tell a knob decline from an
/// emission-shape decline from a licence decline without reading the source.
///
/// `blocker` is "this particular emission cannot be threaded whatever the licence
/// says", carrying its own reason: a streamed provider source, or a row pool that
/// only iteration 0 can allocate. An OPTION rather than a bool so a new blocker
/// arrives with its explanation attached and cannot silently reuse a wrong one.
let peelRowPragma (requested: bool) (licensed: bool) (blocker: string option)
                  (ind: string) : string list =
    if licensed && blocker.IsNone && ompThreadEmissionEnabled () then
        recordOmpConstruct "" "BLADE_OMP_PARALLEL_FOR_DYNAMIC" "peeled ragged / grouped row loop"
        [ ind + "BLADE_OMP_PARALLEL_FOR_DYNAMIC" ]
    else
        let reason =
            if not (ompThreadEmissionEnabled ()) then ompThreadsSuppressedReason ()
            else
                match blocker with
                | Some r -> r
                | None -> "the omp(...) licence covers no level of the peeled row loop"
        ompSuppressedMarker requested false reason ind

/// Does this kernel body lower to something containing a LOOP of its own?
///
/// The nest machinery sees loop LEVELS; a body's own iteration is invisible to
/// it, because every construct below lowers to an IIFE (or an inline
/// materialization) that hides a `for` inside an expression position. That gap
/// is what made `BLADE_IVDEP` land on innermost headers whose bodies contain a
/// loop -- see `ivdepEligible`. Vectorization is a statement about the loop's
/// own iterations; a loop whose body is itself a loop cannot be vectorized by
/// any compiler, so the pragma there is dead text that READS like the
/// optimization already happened.
///
/// Deliberately a whole-subtree walk over `ExprShape`, not a hand-listed set of
/// positions: a construct that hides a loop is disqualifying wherever it sits
/// (a `prodsum` under an `if`, inside a `let` value, in a call argument), and
/// the generic walker cannot silently miss a variant the way a hand-maintained
/// match arm can.
///
/// CONSERVATIVE BY CONSTRUCTION: false negatives cost only a missed
/// (pre-existing) opportunity, so the list is the constructs that ALWAYS emit a
/// loop -- the two reduction intrinsics, the membership scan, and a nested
/// combinator application, which materializes a whole nest.
let rec kernelBodyContainsInnerLoop (e: IRExpr) : bool =
    match e with
    // `[&]() { ... for (__pt) ... }()`  -- CodeGen `IRProdSum` / `reduceBound`.
    | IRProdSum _ | IRReduce _ | IRReduceCompute _
    // `[&]() { for (...) if (== x) return true; ... }()` -- `IRContains` scan.
    | IRContains _
    // A nested apply/compose materializes an entire loop nest in place.
    | IRApplyCombinator _ | IRComposeApply _ -> true
    | ExprShape (children, _) -> children |> List.exists kernelBodyContainsInnerLoop

/// Generate a for-loop header (no pragma; pragmas are nest-level, see
/// genNestPragma, and are prepended only at the outermost level by the caller).
/// Bounds are computed as: extent - sum of all dependency indices
/// Array names that are compound in this loop nest: those carrying an
/// IRCompoundMask binding. A sibling binding referencing the same array with a
/// non-mask Extent is that compound's trailing dim (bound = trailing_stride).
let compoundArrayNamesOf (bindings: LoopIndexBinding list) : Set<string> =
    bindings
    |> List.choose (fun b -> match b.Extent with IRCompoundMask _ | IRSparseKeys _ -> Some b.ExtentArrayRef | _ -> None)
    |> Set.ofList

/// The C++ expression for a loop level's (un-subtracted) upper bound. Shared
/// by genForLoopHeader (the `for` header renderer) and the MPI-dense slab
/// prologue (which needs the OUTERMOST level's extent to compute per-rank
/// [lo, hi) bounds) -- factored so the two can never drift.
let genLoopBoundExpr (compoundArrays: Set<string>) (binding: LoopIndexBinding) : string =
    match binding.Extent with
    | IRLit (IRLitInt n) -> string n
    // A closed-form domain driver's cardinality is the plan's count, known at
    // compile time -- and the fold path builds no `_cidx` index to read it off.
    | IRSparseKeys (SkDomain plan) when (domainPlanOfLevel binding).IsSome -> string plan.Card
    // A compound axis iterates its present cells, so its bound is the
    // runtime cardinality of the compact index, not a dense .extents entry.
    // (The compound level carries IRCompoundMask as its Extent; ExtentArrayRef
    // is the compound array's name -> `<arr>.idx->cardinality`.) A VIRTUAL
    // compound source (range<CompoundIdx<m>>) has no Compound value to hang
    // `.idx` off; its bound is the standalone materialized index
    // `<name>_cidx->cardinality` (see genCompoundIndexFromMask).
    // IRSparseKeys rides the same arm: a sparse axis also iterates its
    // cardinality, its virtual driver uses the same `_cidx` name suffix, and
    // the Sparse wrapper exposes the same `.idx->cardinality` shape.
    | IRCompoundMask _ | IRSparseKeys _ ->
        let isVirtualCompound =
            binding.Elements
            |> List.exists (fun e ->
                e.ArrayName = binding.ExtentArrayRef &&
                (e.Virtual.IsVirtualRange))
        if isVirtualCompound
        then $"{binding.ExtentArrayRef}_cidx->cardinality"
        else $"{binding.ExtentArrayRef}.idx->cardinality"
    // A compound array's trailing dim has no dense .extents; its (single)
    // trailing extent is the compact buffer's trailing_stride. A literal
    // trailing extent already took the IRLit arm above, so this catches a
    // NON-literal trailing extent, where `.extents[dim]` would reference a
    // member the Compound layout does not have. (Multi-trailing is not yet
    // supported: trailing_stride is the product, not a per-dim extent.)
    | _ when Set.contains binding.ExtentArrayRef compoundArrays ->
        $"{binding.ExtentArrayRef}.trailing_stride"
    // Arc 1 fused JOINT level: the axis spans the array's first d dense
    // dims; its bound is the product of those extents. A literal product
    // was already folded to IRLit by IRLoopStructure.fuseJointSLevels (first arm); this
    // renders the runtime form.
    | _ when binding.FusedRank.IsSome ->
        [0 .. binding.FusedRank.Value - 1]
        |> List.map (sprintf "%s.extents[%d]" binding.ExtentArrayRef)
        |> String.concat " * "
    | _ -> $"{binding.ExtentArrayRef}.extents[{binding.ExtentDimRef}]"

let genForLoopHeader (compoundArrays: Set<string>) (binding: LoopIndexBinding) : string =
    match domainPlanOfLevel binding with
    | Some plan -> genDomainLoopHeader binding plan
    | None ->
    let extentStr = genLoopBoundExpr compoundArrays binding

    // Compute bound subtraction from dependencies
    let subtraction =
        if binding.BoundDependencies.IsEmpty && binding.StrictOffset = 0 then ""
        else
            let depParts = binding.BoundDependencies |> List.map (sprintf "__i%d")
            let offsetParts = if binding.StrictOffset > 0 then [string binding.StrictOffset] else []
            depParts @ offsetParts |> String.concat " - " |> sprintf " - %s"

    $"for (size_t {binding.IndexName} = 0; {binding.IndexName} < {extentStr}{subtraction}; {binding.IndexName}++) {{"

// ---------------------------------------------------------------------------
// FLAT-POOL WRITES FOR CANONICAL COMPACT FILL NESTS
// ---------------------------------------------------------------------------
// docs/plans/plan-simplex-blocked-compute.md section 0b, finding 2, is the
// measurement this implements. A compact fill nest used to reach its output row
// through the ALLOCATION-TIME ILIFFE SKELETON (`__orow = A[__i0][__i1]`), which
// costs one pointer dereference per level per loop ENTRY. Those dereferences
// amortize over the innermost run, and a simplex's innermost run is short and
// gets shorter as rank rises (mean trip count ~ n/r), so the skeleton measured
// 1.07x at rank 3 and 1.53x at rank 4 against hand-erased C++ twins that write
// the flat pool directly -- same loops, same kernel, only the addressing
// changed.
//
// The replacement is the SAME closed form the llvm lane emits
// (`EmitLlvm.emitSimplexSerialR` over `SimplexBlocksCore.prefixTerm`, which is
// property-pinned equal to `rankOfCoords` at ranks 1-5). Level k contributes
//
//     strict:     C(n-lo,   m+1) - C(n-lo-s,   m+1)
//     symmetric:  C(n-lo+m, m+1) - C(n-lo-s+m, m+1)      (m = r-k-1)
//
// where `lo` is the level's canonical floor and `s` its STORAGE coordinate --
// which is exactly the loop counter, because the nest already iterates storage
// coordinates (`for (__i1 = 0; __i1 < n - __i0; ...)`; genForLoopHeader renders
// the floor as the bound subtraction). Every term is invariant under the levels
// inside it, so each hoists to its own level and the nest pays O(1) per cell at
// any rank. At the LAST level m = 0 and the term degenerates to the loop
// counter itself -- which is why the innermost run stays affine and
// pool-contiguous, and why the store is still `__orow[__i(r-1)] = ...` with
// BLADE_IVDEP intact.
//
// CLOSED FORM, NOT A RUNNING CURSOR. The canonical nest visits the pool in
// order, so a running `*cur++` would be exact and cost no arithmetic at all --
// and it measured the same (1.54 vs 1.57 ns/cell at rank 4, inside the noise).
// A cursor makes the rows serially dependent; the closed form leaves them
// independent, so it is the one that composes with the
// `#pragma omp parallel for schedule(dynamic)` this nest already puts on its
// outer level. Free parallelism beats a rounding error.
//
// SCOPE: the fill's WRITE POINTER, and nothing else. The skeleton is still
// allocated, and every other consumer -- lazy canonical reads, printing,
// deallocation, BLAS/CUDA streaming -- still goes through it.

/// `C(x, m)` over a C++ `size_t` expression, as the unclamped falling factorial
/// `x(x-1)...(x-m+1) / m!` (folded to a literal when `x` is one).
///
/// UNCLAMPED IS SOUND HERE, which is not self-evident: `SimplexBlocksCore.binom`
/// answers 0 for x < m, and so does this product, because one of its first m
/// factors is then exactly zero and a zero factor annihilates the unsigned
/// wraparound in the factors after it. Every evaluation point the plan below
/// emits has x >= 0 (`compactFlatWritePlan` argues each one), so the emitted
/// arithmetic and the F# reference agree on every cell the nest visits.
/// Overflow is out of reach for the same reason it is in the llvm lane: the
/// pool has to fit in memory, which bounds the falling factorial by m! times
/// the pool cardinality.
let private cppFallingBinom (x: string) (m: int) : string =
    if m <= 0 then "1"
    else
        match System.Int64.TryParse x with
        | true, v -> string (Blade.SimplexBlocksCore.binom v m)
        | _ ->
            let factors =
                [ for j in 0 .. m - 1 -> if j = 0 then x else $"({x} - {j})" ]
                |> String.concat " * "
            let mutable f = 1L
            for j in 2 .. m do f <- f * int64 j
            if f = 1L then factors else $"({factors}) / {f}"

/// The emitted pieces of a canonical compact fill nest's flat-pool write.
type CompactFlatWrite = {
    /// Hoisted once, just outside the nest: the contiguous pool underneath the
    /// skeleton that `allocate<>` built.
    PoolDecl: string
    /// Lines to emit inside loop level k's body, for k = 0 .. r-2, keyed by
    /// level. The innermost level has no entry: its term IS its loop counter.
    Hoists: Map<int, string list>
    /// Replaces the skeleton row walk at the innermost row-hoist site.
    RowDecl: string
}

/// Build the closed-form flat-write plan for `codeGen`, or None when the nest is
/// not a canonical compact fill -- in which case the caller keeps the skeleton
/// row walk, which is correct for every shape.
///
/// The gate is deliberately narrow; each clause is a fact the closed form needs:
///  1. ONE compact group spanning the WHOLE array (`IndexTypes = [ix]`,
///     `ix.Rank = r`). A mixed shape (a dense axis beside a compact group)
///     addresses through a Horner chain over groups that this does not spell,
///     so it keeps the skeleton. `classifyOutputStorage` is consulted too, so
///     the plan and the ALLOCATOR cannot disagree about the layout.
///  2. A COMPILE-TIME extent, equal at every level. A compact group has one
///     extent by construction; requiring the literal is what lets the outermost
///     binomial fold to a constant and keeps the hoisted polynomials cheap.
///  3. The nest IS the canonical simplex the formula describes: level k's bound
///     subtracts exactly the outer counters 0..k-1 plus `k` for a strict group,
///     i.e. its canonical floor is `sum(outer counters) + k*strict`. A
///     reordered, fused or slabbed nest fails this and keeps the skeleton.
///
/// Hermitian rides the symmetric arm: it shares the packed upper triangle
/// (`buildSymmVec` groups it with SymSymmetric) and differs only in the
/// conjugate-on-swap READ, which this does not touch.
let compactFlatWritePlan (codeGen: LoopNestCodeGen) (outRowName: string)
                         : CompactFlatWrite option =
    match codeGen.OutputType with
    | ArrayElem at ->
        let r = List.length codeGen.Bindings
        // (1) one whole-array compact group, agreeing with the allocator.
        let strictOpt =
            match at.IndexTypes, classifyOutputStorage codeGen.OutputType with
            | [ ix ], AllocSymmetric when ix.Rank = r && r >= 2 ->
                (match ix.Symmetry with
                 | SymSymmetric | SymHermitian -> Some false
                 | _ -> None)
            | [ ix ], AllocAntisymmetric when ix.Rank = r && r >= 2 ->
                (match ix.Symmetry with SymAntisymmetric -> Some true | _ -> None)
            | _ -> None
        // (2) one compile-time extent, shared by every level.
        let ns =
            codeGen.Bindings
            |> List.map (fun b -> match b.Extent with IRLit (IRLitInt v) -> Some v | _ -> None)
        let nOpt =
            match ns with
            | Some n0 :: _ when n0 > 0L && ns |> List.forall (fun e -> e = Some n0) -> Some n0
            | _ -> None
        match strictOpt, nOpt with
        | Some strict, Some n ->
            let sInc = if strict then 1 else 0
            // (3) the bounds ARE the canonical floors the formula assumes.
            let canonical =
                codeGen.Bindings
                |> List.mapi (fun k b ->
                    b.FusedRank.IsNone
                    && b.StrictOffset = k * sInc
                    && Set.ofList b.BoundDependencies = Set.ofList [ 0 .. k - 1 ])
                |> List.forall id
            if not canonical then None
            else
                let elemCpp = elemTypeToCpp at.ElemType
                let tag = sanitizeCppName codeGen.OutputName
                let poolName = $"__opool_{tag}"
                let hiName k = $"__ohi_{tag}_{k}"
                let endName k = $"__oend_{tag}_{k}"
                let offName k = $"__ooff_{tag}_{k}"
                let names = codeGen.Bindings |> List.map (_.IndexName)
                // Level 0's upper evaluation point is the pool's own cardinality
                // argument -- `n + (r-1)` symmetric, `n` strict -- and it is a
                // literal, so `C(., r)` folds and level 0 costs one subtraction
                // plus one polynomial.
                let hi0 = n + (if strict then 0L else int64 (r - 1))
                let hoists =
                    [ for k in 0 .. r - 2 ->
                        let m1 = r - k        // = m + 1, the binomial's lower argument
                        let hiHere = if k = 0 then string hi0 else hiName k
                        let lines =
                            [ // `hi_k = n + d_k - lo_k`, and `d` falls by one per
                              // level exactly as `lo` climbs by one under
                              // strictness, so `hi_k = lowEnd_{k-1} - 1` in BOTH
                              // classes: one chain, no per-class arithmetic. It is
                              // evaluated only inside level k's body, which runs
                              // only when level k has a trip -- precisely when
                              // this is positive.
                              if k > 0 then
                                  yield $"const size_t {(hiName k)} = {(endName (k - 1))} - 1;"
                              // `lowEnd_k = n + d_k - i_k >= d_k + 1 >= 1`: an
                              // absolute coordinate never reaches the extent.
                              yield $"const size_t {(endName k)} = {hiHere} - {(List.item k names)};"
                              yield sprintf "const size_t %s = %s(%s) - (%s);"
                                        (offName k)
                                        (if k = 0 then "" else offName (k - 1) + " + ")
                                        (cppFallingBinom hiHere m1)
                                        (cppFallingBinom (endName k) m1) ]
                        (k, lines) ]
                    |> Map.ofList
                Some { PoolDecl =
                         $"{elemCpp}* {poolName} = nested_array_utilities::pool_base({codeGen.OutputName}.data);"
                       Hoists = hoists
                       RowDecl =
                         $"{elemCpp}* BLADE_RESTRICT {outRowName} = {poolName} + {(offName (r - 2))};" }
        | _ -> None
    | _ -> None

/// Generate complete loop nest as C++ code
/// Tracks peeled names across levels and generates element bindings for all arrays at each level

// permutations / permSign moved to ReynoldsCore.fs (shared term-plan core).

// `isCommutativeOp` / `isAssociativeOp` / `foldKernelBuiltinOp` /
// `foldReorderLicensed` -- the parallel-fold reorder LICENCE -- are defined
// with the BLADE_FP_REASSOC gate near the top of this file: the expression-form
// reduce (`renderReduceExpr`) has to consult the same licence before it may
// reassociate, and F# is order-dependent. One licence predicate, one answer.

// `ompReductionOperator` -- the `reduction(<op>:acc)` operator for a builtin
// fold body -- is defined with the BLADE_FP_REASSOC gate near the top of this
// file too, because the knob's `omp simd reduction` form reads it through
// `fpReassocSimdOp`, which lives with the rest of that machinery, and F# is
// order-dependent. Path A below and the knob name the SAME operator table on
// purpose: one table, one answer, whichever construct spends it.

// `foldLaneCount` -- the lane count Path B's flat form uses below -- is defined
// with the BLADE_FP_REASSOC gate near the top of this file, because the
// `prodsum` IIFE emitter in `exprToCppCore` reads it too and F# is
// order-dependent. One definition, one evaluation order.

/// Flatten nested applications of the same commutative+associative op into a list of operands.
/// E.g. (a * b) * c -> [a; b; c]
let rec flattenAssocOp (mode: IRBinOpMode) (op: IRBinOp) (expr: IRExpr) : IRExpr list =
    match expr with
    | IRBinOp (m, o, l, r, _) when o = op && m = mode ->
        flattenAssocOp mode op l @ flattenAssocOp mode op r
    | _ -> [expr]

/// Reynolds term identity: the canonical form of a kernel body under one
/// parameter permutation. Two permutations merge into one coefficient-
/// weighted term EXACTLY when their canonical forms are structurally equal,
/// so the form must be injective up to the normalizations that are
/// bit-exact:
///
///   * a variable the name map names becomes a marker carrying that NAME
///     (the permutation's renaming); every other node keeps its full
///     structure, payload included -- the recursion is `mapIRExpr`, the
///     ExprShape fold, so no variant is keyed by anything less than itself.
///     (The old string key ended in a catch-all keyed by the node's hash,
///     which ignored the name map: every permutation of a `contains`,
///     `reduce`, `prodsum`, ... body got the same key and distinct terms
///     merged -- `reynolds(lambda(x, y) -> if contains(S, x) ...)` summed
///     2*g(x,y) instead of g(x,y) + g(y,x).)
///   * float literals by BIT PATTERN (structural `=` calls 0.0 and -0.0 equal);
///     string literals escaped so no literal can spell a marker;
///   * the two operands of an ELEMENTWISE `+`, `*`, `==`, `!=` sorted: IEEE
///     `a+b` and `b+a` are the same bits, so this is exact (likewise fma's two
///     factors, and a two-operand prodsum over same-typed operands -- see the
///     arms below). Not an OUTER op (`x [+] y` is the transpose of `y [+] x`)
///     and not a complex product (its two cross products round differently
///     under FP contraction). `&&` / `||` are NOT sorted: they
///     short-circuit, and `p(y) && p(x)` evaluates a different operand first
///     than `p(x) && p(y)` -- merging them would drop an evaluation (and any
///     abort it raises);
///   * an associative `+` / `*` chain is FLATTENED (operands sorted across the
///     whole chain) only where reassociation is exact or licensed: integer and
///     Bool chains (two's-complement wrap is associative) always, float chains
///     only under an EXPLICIT BLADE_FP_REASSOC=1 (`fpReassocExplicitOptIn`:
///     this merge is not a source licence, so the 2026-10-17 fast default does
///     not reach it). `(x + y) + z` and `x + (y + z)` are
///     different float values, so merging them unlicensed would be exactly the
///     reassociation that gate exists to withhold.
///
/// Shared by codegen, the interpreter (Interp/Loops.fs) and the LLVM emitter,
/// so every lane computes the same term plan by construction.
let canonicalKey (nameMap: Map<int, string>) (expr: IRExpr) : IRExpr =
    let reassoc = fpReassocExplicitOptIn ()
    let rec exactScalar (t: IRType) =
        match t with
        | IRTScalar (ETInt32 | ETInt64 | ETBool) -> true
        | IRTUnitAnnotated (inner, _) | IRTIdxTagged (inner, _) -> exactScalar inner
        | _ -> false
    let chainExact (e: IRExpr) =
        reassoc
        || (try exactScalar (typeOf e) with _ -> false)
    // A complex product is NOT bit-symmetric: `(a+bi)(c+di)` rounds ad+bc and
    // cb+da as different fused products under -ffp-contract=fast.
    let complexOperand (e: IRExpr) =
        let rec isComplex (t: IRType) =
            match t with
            | IRTScalar (ETComplex64 | ETComplex128) -> true
            | IRTUnitAnnotated (inner, _) | IRTIdxTagged (inner, _) -> isComplex inner
            | ArrayElem at -> isComplex at.ElemType
            | _ -> false
        match e with
        | IRBinOp (_, _, l, r, _) -> (try isComplex (typeOf l) || isComplex (typeOf r) with _ -> true)
        | _ -> true
    // Two array operands with the same element type and structurally equal,
    // non-opaque extents at every axis (identity records may differ: two
    // kernel parameters over the same index space carry distinct ids).
    let sameExtents (a: IRExpr) (b: IRExpr) =
        try
            match typeOf a, typeOf b with
            | ArrayElem at, ArrayElem bt ->
                at.ElemType = bt.ElemType
                && at.IndexTypes.Length = bt.IndexTypes.Length
                && List.forall2 (fun (ia: IRIndexType) (ib: IRIndexType) ->
                        extentsAgree ia.Extent ib.Extent
                        && (match ia.Extent with IROpaqueExtent -> false | _ -> true))
                       at.IndexTypes bt.IndexTypes
            | _ -> false
        with _ -> false
    let marker (tag: string) (s: string) = IRLit (IRLitString ("\u0001" + tag + ":" + s))
    let sortOperands (xs: IRExpr list) = List.sortWith compare xs
    let rebuildChain mode op (xs: IRExpr list) =
        xs |> List.reduce (fun l r -> IRBinOp (mode, op, l, r, SrcLoc.Nowhere))
    // The name map is consulted on the ORIGINAL ids, before any rewrite, so
    // chain typing (typeOf) sees the real operands.
    let keyed =
        expr |> mapIRExpr (fun n ->
            match n with
            | IRVar (id, _) ->
                (match Map.tryFind id nameMap with
                 | Some name -> marker "var" name
                 | None -> n)
            | IRLit (IRLitFloat f) -> marker "f64" (string (System.BitConverter.DoubleToInt64Bits f))
            | IRLit (IRLitFloat32 f) -> marker "f32" (string (System.BitConverter.SingleToInt32Bits f))
            | IRLit (IRLitString s) -> IRLit (IRLitString ("\u0002" + s))
            | _ -> n)
    // Commutative normalization runs over the keyed tree, bottom-up, so a
    // parent sorts children that are already canonical. Chain exactness is
    // judged on the matching ORIGINAL node (same shape, walked in lockstep).
    let rec norm (orig: IRExpr) (k: IRExpr) : IRExpr =
        let (ExprShape (oKids, _)) = orig
        let (ExprShape (kKids, rebuild)) = k
        let k' =
            match kKids with
            | [] -> k
            | _ -> rebuild (List.map2 norm oKids kKids)
        match k' with
        | IRBinOp (IRElementwise as mode, op, l, r, _) when (op = IRAdd || op = IRMul) && chainExact orig ->
            flattenAssocOp mode op k' |> sortOperands |> rebuildChain mode op
        | IRBinOp (IRElementwise as mode, op, l, r, _)
            when (op = IRAdd || op = IREq || op = IRNeq || (op = IRMul && not (complexOperand orig))) ->
            (match sortOperands [l; r] with
             | [a; b] -> IRBinOp (mode, op, a, b, SrcLoc.Nowhere)
             | _ -> k')
        // fma(a, b, c) = a*b + c with ONE rounding: a*b is b*a exactly.
        | IRFma (a, b, c) ->
            (match sortOperands [a; b] with
             | [a'; b'] -> IRFma (a', b', c)
             | _ -> k')
        // prodsum(x, y) = sum_t x(t)*y(t): each product commutes exactly and
        // the summation order is the same, so the two-operand form is
        // symmetric bit for bit. Only when both operands provably have the
        // SAME extents: the emitter takes the trip count from the FIRST
        // operand, so swapping operands of different extents would change
        // the loop. (Three or more operands multiply left to right -- not
        // sorted.)
        | IRProdSum [x; y] ->
            (match orig with
             | IRProdSum [ox; oy] when sameExtents ox oy ->
                 IRProdSum (sortOperands [x; y])
             | _ -> k')
        | _ -> k'
    norm expr keyed

// RANK-RAISING MAP: a kernel body that IS an array literal of scalars.
//
// `A <@> lambda(t) -> [f(t), g(t)]` deduces a rank+1 output, and the emitter
// allocates it with the trailing literal extents already in `<name>_extents`
// (that part was always right). What had no path was the ROW VALUE: the body
// is inlined at the write site as a C++ *expression*, and an array literal has
// no expression form -- it needs an extents table, an allocate and per-cell
// stores, which are statements. exprToCpp's catch-all therefore fired and
// spliced a refusal into the row slot.
//
// Rendering it as an allocating IIFE would work and be wrong: it puts a heap
// allocation + a pool copy + a free in the innermost loop of what is otherwise
// a flat store. The row's cells are known statically and the destination is a
// contiguous span, so the literal writes STRAIGHT INTO the output pool -- no
// temporary, no copy, no free. That is what makes this shape usable as the
// per-row output of a multi-accumulator join, which is the reason it exists.
//
// The helper answers the one question the emitter needs: what are this
// literal's leaf expressions, in row-major (storage) order?

/// Leaves of a (possibly nested) array literal, row-major, when EVERY leaf is
/// a scalar. `None` for a non-literal body, for a leaf that is itself
/// array-valued (the row-typed-element literal is a different construction --
/// see `inferArrayLitType`'s `rowTypedElemArr` branch -- and does not flatten
/// to a fixed cell count here), and for a ragged nest (rows of differing
/// width, whose leaves would not tile the destination row).
let rec arrayLitScalarLeaves (e: IRExpr) : IRExpr list option =
    match e with
    | IRArrayLit (es, _) when not es.IsEmpty ->
        let subs = es |> List.map arrayLitScalarLeaves
        if subs |> List.exists Option.isNone then None
        else
            let got = subs |> List.map Option.get
            // Rectangular only: sub-literals must all contribute the same
            // number of cells (a scalar element contributes exactly one).
            let widths = got |> List.map List.length |> List.distinct
            if widths.Length > 1 then None else Some (List.concat got)
    | IRArrayLit _ -> None
    | _ ->
        match inferExprType e with
        | ArrayElem _ -> None
        | IRTTuple _ | IRTUnit -> None
        | _ -> Some [e]

/// Static cell count of an array type's axes `[first .. last]` (inclusive),
/// when every one of them has a literal extent. `None` otherwise -- a runtime
/// extent cannot be matched against a literal's fixed leaf count.
let staticCellsOfAxes (at: IRArrayType) (first: int) (last: int) : int option =
    let axes = at.IndexTypes |> List.indexed |> List.filter (fun (i, _) -> i >= first && i <= last)
    if axes.Length <> last - first + 1 then None
    else
        let ns =
            axes |> List.map (fun (_, ix) ->
                match ix.Extent with
                | IRLit (IRLitInt n) when n > 0L -> Some (int n)
                | _ -> None)
        if ns |> List.exists Option.isNone then None
        else Some (ns |> List.map Option.get |> List.fold (*) 1)

/// Reynolds kernel codegen result: C++ expression + dedup statistics.
type ReynoldsResult = {
    CppExpr: string
    TotalPerms: int
    UniqueTerms: int
}

/// Generate the kernel expression string, applying Reynolds permutation sum if needed.
/// For non-Reynolds kernels, just returns `exprToCpp nameMap kernelExpr`.
/// For Reynolds kernels, generates the sum over all permutations of the kernel parameters,
/// deduplicating structurally equivalent permutations via canonical keys.

let genKernelExprWithReynolds
    (kernelExpr: IRExpr)
    (kernelParams: IRParam list)
    (hasReynolds: bool)
    (isAntisymmetric: bool)
    (nameMap: Map<int, string>)
    (paramFinalNames: Map<int, string>) : ReynoldsResult =
    if hasReynolds && kernelParams.Length >= 2 then
        let n = kernelParams.Length
        let paramCppNames =
            kernelParams |> List.map (fun p ->
                Map.tryFind p.VarId paramFinalNames
                |> Option.defaultValue ($"__p{p.VarId}"))
        // Name map for a permutation: each kernel param's VarId maps to the
        // C++ name of the parameter it is permuted to (layered over nameMap).
        let permNameMap (perm: int list) =
            kernelParams |> List.mapi (fun i p ->
                (p.VarId, paramCppNames.[perm.[i]]))
            |> List.fold (fun acc (vid, name) -> Map.add vid name acc) nameMap
        // Enumerate + dedup the permutation terms (canonical key normalizes
        // commutative ops). The plan is rendering-independent, so a future IR
        // interpreter can reuse the exact enumeration/dedup/ordering.
        let plan = reynoldsTermPlan n isAntisymmetric (fun perm -> canonicalKey (permNameMap perm) kernelExpr)
        let totalPerms = plan.TotalPerms
        let uniqueTerms = plan.Terms.Length
        // Build the sum expression with multiplicity coefficients
        let formatTerm coeff expr =
            match isAntisymmetric with
            | true ->
                if abs coeff = 1 then
                    if coeff > 0 then expr else $"(-{expr})"
                else $"({coeff} * {expr})"
            | false ->
                if coeff = 1 then expr else $"({coeff} * {expr})"
        let sumExpr =
            plan.Terms |> List.mapi (fun i (coeff, perm) ->
                let expr = exprToCpp (permNameMap perm) kernelExpr
                let term = formatTerm coeff expr
                if i = 0 then term
                elif isAntisymmetric && coeff < 0 then
                    $" - {(formatTerm (abs coeff) expr)}"
                else $" + {term}")
            |> String.concat ""
        let cppExpr =
            if plan.Terms.IsEmpty then
                "0.0"  // Complete cancellation (e.g. antisymmetrization of symmetric kernel)
            else
                $"({sumExpr})"
        { CppExpr = cppExpr; TotalPerms = totalPerms; UniqueTerms = uniqueTerms }
    else
        { CppExpr = exprToCpp nameMap kernelExpr; TotalPerms = 1; UniqueTerms = 1 }

/// Compact output subscript for a compound-output loop nest. The present-cell
/// axis (the IRCompoundMask binding) contributes r*trailing_stride; a trailing
/// binding contributes the dense within-cell offset. Mirrors the read-side
/// addressing in genElementBindingNew (data + r*stride, then [t]). Supports <= 1
/// trailing dim (the realistic load_compound shape); multi-trailing needs a
/// strided sum and is deferred. All-dims (no trailing) -> .data[r].
let compoundOutputSubscript (bindings: LoopIndexBinding list) (outName: string) : string =
    let isComp (b: LoopIndexBinding) = match b.Extent with IRCompoundMask _ | IRSparseKeys _ -> true | _ -> false
    match bindings |> List.tryFind isComp with
    | None -> ""
    | Some cb ->
        match bindings |> List.filter (isComp >> not) with
        | [] -> $".data[{cb.IndexName}]"
        | [tb] -> $".data[{cb.IndexName} * {outName}.trailing_stride + {tb.IndexName}]"
        | tbs -> $""".data[{cb.IndexName} * {outName}.trailing_stride + {(tbs |> List.map (fun b -> b.IndexName) |> String.concat " + ")}]"""

/// A segment caller owns these windows; no global Array alias exists. The
/// certificate is tied to the final kernel and exact scalar peel sites.
type internal Rank1WindowReads = private {
    Kernel: IRExpr
    Bindings: LoopIndexBinding list
    Reads: (IRExpr * IRExpr * int64 * string) list
    Peels: (ElementBinding * string) list
}

/// Pure, complete admission before any source sentinel is masked or rendered.
/// The closed scalar grammar also refuses array escapes and hidden side forms.
let internal certifyRank1WindowReads (cg: LoopNestCodeGen) (kind: string)
                                    (sources: (IRId * string * ProviderReadSpec * string) list)
                                    (reachLo: int64) (reachHi: int64) (start: int64)
                                    (sideTypes: IRType list) : Result<Rank1WindowReads, string> =
    let scalar ty =
        let rec go = function
            | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool) -> true
            | IRTIdxTagged (t, _) | IRTUnitAnnotated (t, _) -> go t
            | _ -> false
        go ty
    let literal = function IRLit (IRLitInt n) -> n >= 0L | _ -> false
    // Only passive scalar/plain-index types: no dependent/sparse/ragged side
    // tables or hidden executable extents. Identities/tags are provenance.
    let rec passiveType = function
        | IRTScalar _ -> true
        | IRTUnitAnnotated (t, _) -> passiveType t
        | IRTIdxTagged (t, tag) ->
            passiveType t && (match tag with IRefAnon (_, e) -> literal e | _ -> true)
        | IRTArrow (slots, result, _) ->
            passiveType result && (slots |> List.forall (function
                | SIdx ix | SIdxVirt ix ->
                    ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain
                    && ix.Dependencies.IsEmpty && literal ix.Extent
                | SVal _ -> false))
        | _ -> false
    let literalExtents ty =
        passiveType ty && (Blade.Optimize.irTypeExtentExprs ty |> List.forall literal)
    let sourceIds = sources |> List.map (fun (id, _, _, _) -> id) |> Set.ofList
    let mutable reads = []
    let mutable peels = []
    let mutable why = None
    let refuse msg = if why.IsNone then why <- Some msg
    let types = sideTypes @ [cg.OutputType] @ (cg.KernelParams |> List.map (_.Type))
                @ (cg.Captures |> List.map (_.Type))
                @ (sources |> List.map (fun (_, _, s, _) -> mkArrayLike s.VarType))
    if types |> List.exists (literalExtents >> not) then refuse "nonliteral type/extent payload"
    if cg.HasReynolds || cg.FoldWrapper.IsSome || cg.FoldChunk.IsSome || cg.ShareDecl.IsSome then
        refuse "Reynolds, fold or shared-value form"
    match cg.OutputType with
    | ArrayElem at when arrayRank at = 1 && scalar at.ElemType
                        && isFreeableDenseArrayType at && not at.IsVirtual -> ()
    | _ -> refuse "requires a dense rank-one scalar-cell output"
    let level =
        match cg.Bindings with
        | [b] when b.FusedRank.IsNone && b.BoundDependencies.IsEmpty && b.StrictOffset = 0
                   && b.Level = 0 && b.ExtentDimRef = 0 ->
            if not (b.Elements |> List.forall (fun e -> scalar e.ArrayElemType && literalExtents e.ArrayElemType)) then
                refuse "unsupported operand element type"
            match sources with
            | (_, _, s, _) :: _ ->
                match s.VarType.IndexTypes with
                | [ix] ->
                    match ix.Extent, b.Extent with
                    | IRLit (IRLitInt n), IRLit (IRLitInt m)
                        when bigint m = bigint n - (bigint reachHi - bigint reachLo) && m >= 0L -> ()
                    | _ -> refuse "loop extent disagrees with the certified window geometry"
                | _ -> refuse "unsupported source rank"
            | [] -> refuse "no rank-one source evidence"
            Some b
        | _ -> refuse "requires one unshifted dense loop level"; None
    for c in cg.Captures do
        // A normal `let A` may be reassignable in its enclosing scope. Its
        // capture descriptor is allowed ONLY because the complete body proof
        // below consumes every A occurrence as a read, never forwards/writes it.
        if not (sourceIds.Contains c.Id) && (c.IsMutable || not (scalar c.Type)) then
            refuse "unsupported mutable/array capture"
    for (_, vn, spec, win) in sources do
        if spec.VarType.IsVirtual then refuse "virtual source storage is not certified"
        match spec.VarType.IndexTypes with
        | [ix] when ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain
                    && ix.Dependencies.IsEmpty && scalar spec.VarType.ElemType ->
            match ix.Extent with
            | IRLit (IRLitInt n) when n >= 0L -> ()
            | _ -> refuse "requires a checked static source extent"
        | _ -> refuse "requires plain dense rank-one source storage"
        match kind, level with
        | "elementwise", Some b ->
            let sites = b.Elements |> List.filter (fun e -> e.ArrayName = vn)
            if sites.IsEmpty then refuse "streamed operand has no scalar peel"
            for e in sites do
                if e.Virtual <> RealArray || e.ArrayRank <> 1 || e.RankComponent <> 0 || e.DimIndex <> 0 then
                    refuse "unsupported streamed operand peel"
                else peels <- (e, win) :: peels
        | "stencil", Some b ->
            match b.Elements, cg.KernelParams with
            | [e], [p] when e.ParamVarId = p.VarId ->
                match e.Virtual with
                | VirtualRange (Some (IRLit (IRLitInt s))) when s = start -> ()
                | VirtualRange None when start = 0L -> ()
                | _ -> refuse "unsupported halo center binding"
            | _ -> refuse "requires one dense halo window parameter"
        | _ -> refuse "unsupported rank-one segment form"
    // Bounds/offsets are kept as Int64. IRAccess.Offset's int conversion is
    // deliberately NOT used as evidence for a potentially huge literal.
    let signedOffset = function
        | IRLit (IRLitInt n) -> Some n
        | IRUnaryOp (IRNeg, IRLit (IRLitInt n)) when n <> System.Int64.MinValue -> Some (-n)
        | _ -> None
    let windowMatches w =
        match level, cg.KernelParams with
        | Some b, [p] ->
            let sameParam =
                match w with
                | IRVar (id, _) -> id = p.VarId
                | IRParam (nm, idx, _) -> nm = p.Name && idx = p.Index
                | _ -> false
            sameParam && Blade.IRAccess.denseHaloTagOf w = (b.Elements |> List.tryHead |> Option.bind (_.SlotTag))
            && (Blade.IRAccess.denseHaloTagOf w).IsSome
        | _ -> false
    let rec body (ids: Set<IRId>) (paramNames: Set<string>) (visiting: Set<IRId>) e =
        let go = body ids paramNames visiting
        match e with
        | IRLit (IRLitInt _ | IRLitFloat _ | IRLitFloat32 _ | IRLitBool _) -> true
        | IRVar (id, ty) -> ids.Contains id && not (sourceIds.Contains id) && scalar ty && literalExtents ty
        | IRParam (nm, _, ty) -> paramNames.Contains nm && scalar ty && literalExtents ty
        | IRBinOp (IRElementwise, _, a, b, _) -> go a && go b
        | IRUnaryOp (_, a) -> go a
        | IRIf (c, a, b) -> go c && go a && go b
        | IRLet (id, v, b) -> go v && body (ids.Add id) paramNames visiting b
        | IRIndex (IRVar (aid, ty), [IRBinOp (IRElementwise, IRAdd, w, off, _)], _)
            when kind = "stencil" && sourceIds.Contains aid && windowMatches w && literalExtents ty ->
            match signedOffset off, sources |> List.tryFind (fun (id, _, _, _) -> id = aid) with
            | Some o, Some (_, _, _, win) when o >= reachLo && o <= reachHi ->
                reads <- (e, w, o, win) :: reads
                true
            | _ -> false
        | IRApp (f, args, ret) when scalar ret && literalExtents ret ->
            match resolveCallable f with
            | Some cl when cl.Captures.IsEmpty && not (visiting.Contains cl.Id)
                           && cl.Params.Length = args.Length && scalar cl.RetType && literalExtents cl.RetType
                           && (cl.Params |> List.forall (fun p -> scalar p.Type && literalExtents p.Type))
                           && (Blade.Optimize.embeddedExtentExprs cl.Body |> List.forall literal) ->
                (args |> List.forall go)
                && body (cl.Params |> List.map (_.VarId) |> Set.ofList)
                        (cl.Params |> List.map (_.Name) |> Set.ofList) (visiting.Add cl.Id) cl.Body
            | _ -> false
        | _ -> false
    let ids = (cg.KernelParams |> List.map (_.VarId)) @ (cg.Captures |> List.map (_.Id)) |> Set.ofList
    let names = cg.KernelParams |> List.map (_.Name) |> Set.ofList
    if not (body ids names Set.empty cg.KernelExpr) then
        refuse "uncertified source read, escape, callee or scalar expression"
    // Callee bodies cannot consume this caller's reference-keyed substitutions.
    // Only capture-free scalar callees were accepted above; require all halo
    // recipes to belong to the final rendered tree, not a callee body.
    let nodes = ResizeArray<IRExpr>()
    iterIRExpr (fun node -> nodes.Add node) cg.KernelExpr
    if reads |> List.exists (fun (node, _, _, _) -> not (nodes |> Seq.exists (fun n -> obj.ReferenceEquals(n, node)))) then
        refuse "a callee contains a streamed load"
    let extentRoots = Blade.Optimize.embeddedExtentExprs cg.KernelExpr
    if extentRoots |> List.exists (function IRLit (IRLitInt n) when n >= 0L -> false | _ -> true) then
        refuse "nonliteral embedded type/extent payload"
    if kind = "stencil" && reads.IsEmpty then refuse "no certified halo loads"
    match why with
    | Some msg -> Error msg
    | None -> Ok { Kernel = cg.KernelExpr; Bindings = cg.Bindings; Reads = reads; Peels = peels }

/// Per-emission proof for exactly two plain scalar zip operands. Only this
/// module constructs it; the segment owner consumes the same checked value.
type internal Rank2BandPeels = private {
    FinalCodeGen: LoopNestCodeGen
    OuterNames: Map<int, string>
    Sources: (IRId * string * ProviderReadSpec * string) list
    CapacityRows: int64
    Columns: int64
    OuterPeels: (ElementBinding * string) list
    FinalParams: Map<int, string>
    Callees: (IRExpr * IRCallable) list
}

/// Nonadmission is an ordinary I1 fallback, never a new language refusal.
/// Metadata is loaded for this compile-time check, not pinned as an immutable
/// source version. The ordinary provider still loads/validates in its own path.
let internal tryCertifyRank2BandPeels (info: ApplyInfo) (cg: LoopNestCodeGen)
                                    (sources: (IRId * string * ProviderReadSpec * string) list)
                                    (runs: int64 list) (names: Map<int, string>)
                                    : Result<Rank2BandPeels, string> =
    try
        let require condition why = if not condition then invalidOp why
        let f64 = IRTScalar ETFloat64
        let literal = function IRLit (IRLitInt n) when n > 0L -> Some n | _ -> None
        let plain (ix: IRIndexType) =
            ix.Rank = 1 && ix.Kind = SDimension && ix.Symmetry = SymNone
            && ix.IxKind = IxKPlain && ix.Dependencies.IsEmpty && ix.Tag.IsNone
            && (literal ix.Extent).IsSome
        let dense (at: IRArrayType) =
            not at.IsVirtual && at.ElemType = f64 && at.IndexTypes.Length = 2
            && (at.IndexTypes |> List.forall plain) && isFreeableDenseArrayType at
        require (cg.MpiSlab && cg.Captures.IsEmpty && cg.KernelParams.Length = 2
                 && cg.InputArrayNames.Length = 2 && cg.SpeedupFactor = 1L && cg.OutputSymmVec = [1; 2]
                 && not cg.HasReynolds && not cg.IsAntisymmetric && not cg.OmpRequested
                 && cg.FoldWrapper.IsNone && cg.FoldChunk.IsNone && cg.ShareDecl.IsNone
                 && cg.RowEpilogue.IsNone && cg.FlatEpilogue.IsNone && cg.CompactRowEpilogue.IsNone
                 && cg.FoldOmpDecline.IsNone)
                "requires the final ordinary capture-free scalar zip renderer"
        require (info.IsCoIteration && info.Arrays.Length = 2 && info.ArrayTypes.Length = 2
                 && info.Identities.Length = 2 && info.SharedIndexTypes.Length = 2
                 && info.KernelInputRanks = [0; 0] && info.KernelOutputRank = 0
                 && info.KernelTDims.IsEmpty && info.SDimsPerArray = [2; 2]
                 && info.SymcomStates = [SCNeither; SCNeither; SCNeither; SCNeither]
                 && info.TriangularLevels = [false; false; false; false] && not info.HasReynolds
                 && info.SpeedupFactor = 1L && info.ReynoldsSpeedup = 1L
                 && info.OutputType = cg.OutputType)
                "unsupported zip metadata"
        match info.Loop with
        | IRMethodFor loop ->
            require (loop.Arrays = info.Arrays && loop.ArrayTypes = info.ArrayTypes
                     && loop.Identities = info.Identities && loop.SDimsPerArray = [2; 2]
                     && loop.TotalSDims = 4 && loop.SharedIndexTypes = info.SharedIndexTypes)
                    "method_for payload differs from the zip"
        | _ -> invalidOp "requires direct method_for zip provenance"
        let output =
            match cg.OutputType with
            | ArrayElem at when dense at && at.Identity.IsNone
                                && at.IndexTypes = info.SharedIndexTypes -> at
            | _ -> invalidOp "requires the exact plain shared output axes"
        let n = (literal output.IndexTypes.[0].Extent).Value
        let m = (literal output.IndexTypes.[1].Extent).Value
        // Conservative for supported 32/64-bit targets: bounds provider int
        // narrowing, all padded chunk strides/bytes, size_t and ptrdiff_t,
        // window/output cells and the retained owned output row skeleton.
        let fitsBytes cells = cells > 0I && cells * 8I <= bigint System.Int32.MaxValue
        require (fitsBytes (bigint n * bigint m) && fitsBytes (bigint n))
                "shape/allocation exceeds conservative checked arithmetic"
        require (sources.Length >= 1 && sources.Length <= 2
                 && (sources |> List.map (fun (id, _, _, _) -> id) |> List.distinct).Length = sources.Length
                 && (sources |> List.map (fun (_, vn, _, _) -> vn) |> List.distinct).Length = sources.Length)
                "ambiguous source identities"
        let mutable edge = None
        for sourceIndex, (id, vn, spec, win) in List.indexed sources do
            require (Map.tryFind id names = Some vn && vn <> cg.OutputName
                     && win = $"{cg.OutputName}__win{sourceIndex}"
                     && spec.Provider = "zarr" && spec.Streamed && not spec.AllowMissing
                     && System.IO.Path.IsPathFullyQualified spec.FilePath
                     && System.IO.Directory.Exists spec.FilePath
                     && spec.Window.IsNone && spec.MaskName.IsNone && spec.MaskType.IsNone
                     && dense spec.VarType && spec.VarType.IndexTypes = info.SharedIndexTypes)
                    "source capability/type does not match the final zip"
            let store = Blade.ZarrProvider.load spec.FilePath
            let meta =
                Blade.ZarrProvider.tryFindArray store spec.VarName
                |> Option.defaultWith (fun () -> invalidOp "source metadata is absent")
            require (store.Version = 2 && meta.Version = 2 && meta.Blade.IsNone
                     && meta.Codec = Blade.ZarrProvider.CodecIdentity
                     && meta.Dtype.Code = "f8" && meta.Dtype.ByteSize = 8
                     && meta.Dtype.Elem = ETFloat64 && meta.Dtype.IsFloat
                     && meta.Shape = [n; m] && meta.Chunks.Length = 2
                     && (meta.Chunks |> List.forall (fun c -> c > 0L && c <= int64 System.Int32.MaxValue))
                     && fitsBytes (meta.Chunks |> List.fold (fun a c -> a * bigint c) 1I))
                    "requires representable local v2 identity Float64 chunks"
            let k = meta.Chunks.Head
            require (edge.IsNone || edge = Some k) "source chunk edges differ"
            edge <- Some k
        let k = edge.Value
        let w = min n k
        require (fitsBytes (bigint w * bigint m) && runs.Length >= 2 && runs.Head = 0L && List.last runs = n)
                "invalid run capacity/endpoints"
        for lo, hi in List.pairwise runs do
            // No new boundary table or unchecked lo+K arithmetic.
            require (lo >= 0L && lo < n && hi > lo && hi <= n
                     && lo % k = 0L && bigint hi = min (bigint n) (bigint lo + bigint k))
                    "run descriptor differs from the physical chunk grid"
        let sourceForPosition pos =
            match info.Arrays.[pos] with
            | IRVar (id, ty) ->
                let (_, vn, spec, win) =
                    sources |> List.tryFind (fun (sid, _, _, _) -> sid = id)
                    |> Option.defaultWith (fun () -> invalidOp "operand has no streamed source evidence")
                require (ty = mkArrayLike spec.VarType && info.ArrayTypes.[pos] = spec.VarType
                         && cg.InputArrayNames.[pos] = vn
                         && info.Identities.[pos] = AIDVariable vn)
                        "operand identity/type/name differs"
                vn, win
            | _ -> invalidOp "requires direct streamed operands"
        let operands = [sourceForPosition 0; sourceForPosition 1]
        require ((operands |> List.map fst |> List.distinct) = (sources |> List.map (fun (_, vn, _, _) -> vn)))
                "ordered/deduplicated sources differ"
        let outer, inner =
            match cg.Bindings with
            | [a; b] -> a, b
            | _ -> invalidOp "requires exactly two final levels"
        for level, binding in List.indexed cg.Bindings do
            require (binding.Level = level && binding.Extent = output.IndexTypes.[level].Extent
                     && binding.ExtentArrayRef = cg.InputArrayNames.Head && binding.ExtentDimRef = level
                     && binding.BoundDependencies.IsEmpty && binding.StrictOffset = 0
                     && binding.FusedRank.IsNone && not binding.IsParallel && binding.State = SCNeither
                     && binding.Elements.Length = 2)
                    "unsupported final bounds or extra peel sites"
        require (outer.IndexName <> inner.IndexName && outer.IndexName <> "" && inner.IndexName <> "")
                "ambiguous loop coordinates"
        let restrictSites = restrictPeelSites cg.Bindings
        let mutable peels = []
        let mutable finalParams = Map.empty
        for pos in 0..1 do
            let p = cg.KernelParams.[pos]
            let vn, win = operands.[pos]
            require (p.Type = f64 && p.Index = pos
                     && (cg.KernelParams |> List.filter (fun q -> q.VarId = p.VarId)).Length = 1
                     && not (sources |> List.exists (fun (id, _, _, _) -> id = p.VarId)))
                    "ambiguous/non-Float64 scalar parameter"
            let site level (binding: LoopIndexBinding) =
                let e = binding.Elements.[pos]
                require (e.ArrayPosition = pos && e.ArrayName = vn && e.ParamName = p.Name
                         && e.ParamVarId = p.VarId && e.ArrayRank = 2 && e.ArrayElemType = f64
                         && e.RankComponent = level && e.DimIndex = level
                         && e.Virtual = RealArray && e.SlotTag.IsNone)
                        "final peel identity/provenance differs"
                e
            let a = site 0 outer
            site 1 inner |> ignore
            require (restrictSites.Contains (0, pos)) "outer row is not consumed by exactly one scalar successor"
            peels <- peels @ [(a, win)]
            finalParams <- Map.add p.VarId ($"{vn}__{outer.IndexName}__{inner.IndexName}") finalParams
        let flags (f: IRCallable) =
            not f.IsStatic && not f.IsArityPoly && f.ArityParam.IsNone && f.Parallelism.IsEmpty
            && not f.IsOmpParallel && not f.IsCudaKernel && not f.IsMpiParallel
            && not f.IsCommutative && f.CommGroups.IsEmpty && f.AntisymGroups.IsEmpty
            && f.Captures.IsEmpty && f.RetType = f64
            && (f.Params |> List.forall (fun p ->
                p.Type = f64 && not (sources |> List.exists (fun (id, _, _, _) -> id = p.VarId))))
            && (f.Params |> List.map (_.VarId) |> List.distinct).Length = f.Params.Length
            && (f.Params |> List.map (_.Name) |> List.distinct).Length = f.Params.Length
            && (f.Params |> List.mapi (fun i p -> p.Index = i) |> List.forall id)
        let mutable callees = []
        let rec body (kparams: IRParam list) (locals: Set<IRId>) (visiting: Set<IRId>) e =
            let go = body kparams locals visiting
            match e with
            | IRLit (IRLitFloat _) -> true
            | IRVar (id, ty) ->
                ty = f64 && ((kparams |> List.exists (fun p -> p.VarId = id)) || locals.Contains id)
                && not (sources |> List.exists (fun (sid, _, _, _) -> sid = id))
            | IRParam (nm, i, ty) -> ty = f64 && (kparams |> List.exists (fun p -> p.Name = nm && p.Index = i))
            | IRBinOp (IRElementwise, (IRAdd | IRSub | IRMul | IRDiv), a, b, _) ->
                typeOf e = f64 && go a && go b
            | IRUnaryOp (IRNeg, a) -> typeOf e = f64 && go a
            | IRLet (id, value, rest) ->
                not (locals.Contains id) && not (kparams |> List.exists (fun p -> p.VarId = id))
                && not (sources |> List.exists (fun (sid, _, _, _) -> sid = id))
                && typeOf e = f64 && go value && body kparams (locals.Add id) visiting rest
            | IRApp (f, args, ret) when ret = f64 ->
                match resolveCallable f with
                | Some cl when flags cl && not (visiting.Contains cl.Id) && args.Length = cl.Params.Length ->
                    let referenceMatches =
                        match f with
                        | IRVar (id, ty) -> id = cl.Id && ty = mkFuncArrow (cl.Params |> List.map (_.Type)) cl.RetType
                        | _ -> false
                    callees <- (f, cl) :: callees
                    referenceMatches && (args |> List.forall go)
                    && body cl.Params Set.empty (visiting.Add cl.Id) cl.Body
                | _ -> false
            | _ -> false
        require (body cg.KernelParams Set.empty Set.empty cg.KernelExpr
                 && (Blade.Optimize.embeddedExtentExprs cg.KernelExpr).IsEmpty)
                "uncertified scalar body/capture/type payload"
        match resolveKernel info.Kernel with
        | Some rk ->
            let referenceMatches =
                match info.Kernel with
                | IRVar (id, ty) -> id = rk.Callable.Id && ty = mkFuncArrow (cg.KernelParams |> List.map (_.Type)) f64
                | _ -> false
            require (flags rk.Callable && rk.Callable.Params = cg.KernelParams
                     && referenceMatches && obj.ReferenceEquals(rk.Callable.Body, cg.KernelExpr))
                    "kernel metadata/body differs from the final scalar renderer"
            callees <- (info.Kernel, rk.Callable) :: callees
        | None -> invalidOp "unresolved kernel"
        Ok {
            FinalCodeGen = cg
            OuterNames = names
            Sources = sources
            CapacityRows = w
            Columns = m
            OuterPeels = peels
            FinalParams = finalParams
            Callees = callees
        }
    with ex -> Error ex.Message

/// Checked owner contract: only the same ordered source windows/capacity can
/// consume this certificate after its renderer succeeded.
let internal rank2BandOwnsWindows (plan: Rank2BandPeels)
                                 (outputName: string) (sources: (string * ProviderReadSpec) list) (capacity: int64) =
    outputName = plan.FinalCodeGen.OutputName && capacity = plan.CapacityRows
    && sources = (plan.Sources |> List.map (fun (_, vn, spec, _) -> vn, spec))
    && (plan.Sources |> List.mapi (fun k (_, _, _, win) -> win = $"{outputName}__win{k}") |> List.forall id)

let private rank2BandStillValid (plan: Rank2BandPeels) (cg: LoopNestCodeGen) (names: Map<int, string>)
                                (streamed: Map<string, ProviderReadSpec>) =
    obj.ReferenceEquals(plan.FinalCodeGen, cg) && plan.OuterNames = names
    && (plan.Sources |> List.forall (fun (id, vn, _, _) -> Map.tryFind id names = Some vn && not (streamed.ContainsKey vn)))
    && (plan.Callees |> List.forall (fun (expr, cl) ->
        match resolveCallable expr with Some now -> obj.ReferenceEquals(now, cl) | None -> false))

let private genLoopNestStreamedCore (windowReads: Rank1WindowReads option) (bandPeels: Rank2BandPeels option)
                                   (streamed: Map<string, ProviderReadSpec>) (codeGen: LoopNestCodeGen)
                                   (outerNames: Map<int, string>) (indent: int) : string list =
    match windowReads with
    | Some p when not (obj.ReferenceEquals(p.Kernel, codeGen.KernelExpr))
                  || not codeGen.MpiSlab
                  || p.Bindings.Length <> codeGen.Bindings.Length
                  || not (List.forall2 (fun a b -> obj.ReferenceEquals(a, b)) p.Bindings codeGen.Bindings) ->
        invalidOp "rank-one window certificate belongs to a different kernel"
    | _ -> ()
    let ind n = String.replicate n "    "
    let mutable lines = []
    let mutable depth = indent

    // Track current peeled name for each array position
    let mutable currentNames : Map<int, string> =
        codeGen.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList

    // Track final peeled name for each param VarId (for kernel body substitution)
    let mutable paramFinalNames : Map<int, string> = Map.empty

    // Streamed sources: accumulated ABSOLUTE site coordinates per array
    // position (consumed by the fiber read at the S/T boundary).
    let mutable streamSites : Map<int, string list> = Map.empty
    
    // Generate nested loops with element bindings
    // Nest-level OpenMP pragma (collapse for rectangular, dynamic for triangular)
    // is prepended only at the outermost level.
    //
    // OpenMP thread-coverage instrumentation (test mode only): records the set
    // of distinct OpenMP threads that actually executed the outer parallel
    // region, and prints the count afterward. This empirically answers "did the
    // runtime distribute this generated loop across multiple threads?" -- the
    // ground-truth question, not a heuristic on pragma text. Race-free: each
    // thread writes ONLY its own slot in __omp_seen[], so no two threads touch
    // the same address. Gated behind ompTestModeEnabled() so user codegen is
    // never polluted.
    let ompInstrument = ompTestModeEnabled ()
    // Which level carries the nest's pragma (None = serial). Computed once: the
    // licence is a property of the nest, not of the level being emitted. Also
    // gates the coverage instrumentation -- which must follow the PRAGMA, not
    // level 0, since `omp(a: n)` can licence an inner level and leave the
    // outermost serial. Gating on the head binding would then instrument a nest
    // whose parallel region lives further in, and report it as never-threaded.
    //
    // The BUILD KNOB (`ompThreadEmissionEnabled`) forces this to None, which is
    // what makes a serial-emission build emit EXACTLY the unlicensed nest: no
    // pragma, `ompLastLevel = -1` so `BLADE_IVDEP` lands where it would have
    // without the clause, and no coverage instrumentation (there is no team to
    // observe). The dropped clause is reported by the marker below.
    // G41 (Codex findings 18 / 20): a kernel that may write storage outside
    // itself (an unresolved callback, a view of a captured array) gets no
    // team: its cells would race. Lowering refuses what it can prove; this is
    // the emitter's own veto, reported by the census marker below.
    let nestKernelWrites =
        codeGen.OmpRequested && codeGen.FoldWrapper.IsNone && exprMayWrite codeGen.KernelExpr
    let pragmaLevel =
        if codeGen.FoldWrapper.IsSome || not (ompThreadEmissionEnabled ()) || nestKernelWrites then None
        else pragmaLevelOf codeGen.Bindings
    // "This nest runs inside a parallel team", which gates the thread-coverage
    // instrumentation. A Path B fold nest qualifies without carrying a `for`
    // pragma on any level: its team is the explicit region opened below, and the
    // per-thread markers land in the innermost body exactly as they do for a
    // `parallel for` nest -- which is what lets `blade test omp-coverage` answer
    // "is the reduce's parallel region genuine" with ground truth instead of
    // pragma text.
    // (`ompThreadEmissionEnabled` guards the FoldChunk half for the same reason
    // it guards `pragmaLevel`: with the knob off no team is opened anywhere in
    // this nest, so there is nothing for the coverage instrumentation to see.)
    // The REPRO build (BLADE_FP_REPRO=1) drops the chunk plan too: Path B's
    // per-thread partials reassociate the fold, and that build keeps the serial
    // association of every fold. Same consumer-side drop as the threads knob.
    // EXCEPT the threaded complete-row form (ChunkRowSlots, bench gap G35),
    // the one chunk form that splits no chain: each outer index's whole fold
    // runs on one thread into its slot, and the outer chain runs serially
    // after the region. It is planned ONLY in the repro build, so it is exempt
    // from the last conjunct (and still dies with the threads knob).
    let rowSlots =
        match codeGen.FoldChunk with
        | Some { ChunkForm = ChunkRowSlots (rows, cells) } -> Some (rows, cells)
        | _ -> None
    let foldChunkLive =
        codeGen.FoldChunk.IsSome && ompThreadEmissionEnabled ()
        && (fpReassocEnabled () || rowSlots.IsSome)
    let outerIsParallel =
        pragmaLevel.IsSome || foldChunkLive
    // Path B: a comm-licensed fold nest. `pragmaLevel` stays None on
    // purpose -- a `parallel for` over the outer level would race on the shared
    // accumulator, which is exactly why folds were blanket-suppressed. What
    // replaces it is an explicit team with PRIVATE accumulators and a
    // fixed-order combine, emitted around the nest below. Names are tagged with
    // the fold binding so several folds can share one C++ scope.
    // The BUILD KNOB drops the chunk plan HERE, at the consumer, and nowhere
    // else: the region below is an explicit `#pragma omp parallel` calling
    // `omp_get_max_threads`/`omp_get_num_threads`, i.e. exactly the thread
    // machinery a serial-emission build must contain none of, and this is the
    // only place it is spent. The producer (`genReduceComputeBinding`'s
    // `chunkable`) deliberately still SETS the plan, because `FoldChunk.IsSome`
    // is read elsewhere as a FACT about the fold -- see the note there.
    // Without the plan the nest is the ordinary serial fold.
    let foldChunk = if foldChunkLive then codeGen.FoldChunk else None
    let fcTag = match foldChunk with Some p -> p.Tag | None -> ""
    let fcRn = $"__rn_{fcTag}"
    let fcT = $"__rT_{fcTag}"
    let fcPart = $"__rpart_{fcTag}"
    let fcHad = $"__rhad_{fcTag}"
    let fcAcc = $"__racc_{fcTag}"
    let fcHas = $"__rhas_{fcTag}"
    let fcSlot = $"__rslot_{fcTag}"
    let rowSlotsLive = foldChunk.IsSome && rowSlots.IsSome
    // The accumulator the nest body writes: the thread-private one under Path B,
    // the caller-declared shared scalar otherwise.
    let foldAccName = if foldChunk.IsSome then fcAcc else codeGen.OutputName
    // Unique region tag derived from the (unique) output name.
    let regionTag = codeGen.OutputName
    // The test-mode observation's locals carry the region tag: two
    // instrumented nests can share one C++ scope (several top-level folds in
    // `main`), and unsuffixed names were redeclarations (g++ error) there.
    let covSfx = "_" + (regionTag |> String.map (fun c -> if System.Char.IsLetterOrDigit c || c = '_' then c else '_'))
    let covMaxth = "__omp_maxth" + covSfx
    let covSeen = "__omp_seen" + covSfx
    let covTeam = "__omp_team" + covSfx
    if ompInstrument && outerIsParallel then
        lines <- lines @ [
            ind depth + "// [omp-coverage] thread observation (test mode)"
            ind depth + $"int {covMaxth} = blade_omp_get_max_threads();"
            ind depth + $"bool* {covSeen} = new bool[{covMaxth}]();"
            ind depth + $"int* {covTeam} = new int[{covMaxth}]();"
        ]

    let mutable atOuterLevel = true
    let lastBindingIdx = (List.length codeGen.Bindings) - 1
    let mutable bidx = 0
    let compoundArrays = compoundArrayNamesOf codeGen.Bindings
    // Path B prologue: hoist the outermost extent, size the team, and
    // open the explicit parallel region. Three scopes open here (block / non-
    // empty guard / parallel region) and are closed by the epilogue after the
    // nest, so `depth` advances by 3 before the first loop header.
    match foldChunk with
    // G35 prologue: the same team sizing and the same partition as Path B,
    // but no private accumulator -- one slot per outer index, written by the
    // index's owner. Three scopes open (block / non-empty guard / parallel
    // region), closed by the epilogue, exactly like Path B's. BLOCK comments:
    // nothing here may swallow a following statement if the lines are joined.
    | Some plan when rowSlotsLive ->
        let (rows, cells) = rowSlots.Value
        recordOmpConstruct codeGen.OutputName "omp parallel num_threads(T) + serial ascending outer chain"
            $"threaded complete rows (repro): {rows} outer indices partitioned per thread (Path B partition), each index's whole fold on one thread into its slot; the outer chain serial afterwards ({cells} inner fold steps)"
        let outerBound = genLoopBoundExpr compoundArrays (List.head codeGen.Bindings)
        (ompApiUsedCell ()).Value <- true
        (foldLanesUsedCell ()).Value <- true
        lines <- lines @ [
            ind depth + "/* reduce over computation: threaded complete rows (repro) -- each row's fold on one thread into its slot, the outer chain serial after the region */" ]
            @ pathBTeamOpen depth fcRn fcT outerBound
                [ $"{plan.ElemCpp}* {fcSlot} = new {plan.ElemCpp}[{fcRn}];" ] []
        depth <- depth + 3
    | Some plan ->
        // M14 (gap G25): the form inside each chunk, decided by the producer.
        let chunkPhrase =
            match plan.ChunkForm with
            | ChunkSimd op -> $"omp simd reduction({op}) within each chunk"
            | ChunkSerial why -> $"serial within each chunk: {why}"
            | ChunkRowSlots _ -> "threaded complete rows"
        recordOmpConstruct codeGen.OutputName "omp parallel num_threads(T) + fixed-order combine"
            $"comm-licensed parallel fold: outer level chunked per thread (Path B), {chunkPhrase}"
        let outerBound = genLoopBoundExpr compoundArrays (List.head codeGen.Bindings)
        (ompApiUsedCell ()).Value <- true
        // The partition is blade_fold::chunk_lo (blade_fold_lanes.hpp): the ONE
        // function every Path B site spells, so this nest's chunks coincide
        // with the materialized fold's and the threaded vector arm's.
        (foldLanesUsedCell ()).Value <- true
        // ChunkSimd: the chunk's rank-1 real-array operands are read through
        // restrict pool aliases (the vectorizer cannot otherwise prove the
        // sweep dependence-free behind the Array struct's subscript -- the
        // same aliases the serial licensed form uses), and the accumulator is
        // seeded with the operator's identity so the reduction clause has a
        // private copy to start from. Read-only sharing is permitted under
        // restrict, `zip(x, x)` included.
        let simdAliases =
            match plan.ChunkForm with
            | ChunkSimd _ ->
                (List.head codeGen.Bindings).Elements
                |> List.filter (fun e ->
                    e.Virtual.IsRealArray && e.ArrayRank = 1
                    && (match e.SlotTag with Some t -> not (t.StartsWith "__halowin") | None -> true)
                    && not (Map.containsKey e.ArrayName streamed))
                |> List.map (fun e -> e.ArrayName)
                |> List.distinct
                |> List.mapi (fun i nm -> (nm, $"__rsrc{i}_{plan.Tag}"))
            | ChunkSerial _ | ChunkRowSlots _ -> []
        for (nm, alias) in simdAliases do
            for e in (List.head codeGen.Bindings).Elements do
                if e.ArrayName = nm && e.Virtual.IsRealArray && e.ArrayRank = 1
                   && (match e.SlotTag with Some t -> not (t.StartsWith "__halowin") | None -> true) then
                    currentNames <- Map.add e.ArrayPosition alias currentNames
        let accSeed =
            match plan.ChunkForm with
            | ChunkSimd "*" -> "1"
            | ChunkSimd _ -> "0"
            | ChunkSerial _ | ChunkRowSlots _ -> $"{plan.ElemCpp}()"
        lines <- lines @ [
            ind depth + "// reduce over computation: comm-licensed parallel fold, outer level chunked" ]
            @ pathBTeamOpen depth fcRn fcT outerBound
                ([ $"{plan.ElemCpp}* {fcPart} = new {plan.ElemCpp}[{fcT}];"
                   // Zero-initialized: it marks which slots a thread actually wrote, so
                   // the combine is correct for ANY team size the runtime hands back
                   // (num_threads is a request -- see the flat path's note) and skips a
                   // chunk whose inner nest contributed nothing.
                   $"bool* {fcHad} = new bool[{fcT}]();" ]
                 @ (simdAliases |> List.map (fun (nm, alias) ->
                        $"const {plan.ElemCpp}* BLADE_RESTRICT {alias} = {nm}.data;")))
                // ChunkSerial: value-initialized, never READ before __rhas turns
                // true (the initializer only keeps -Wmaybe-uninitialized quiet).
                // ChunkSimd: the operator's identity, the reduction's seed.
                [ $"{plan.ElemCpp} {fcAcc} = {accSeed};"
                  $"bool {fcHas} = false;" ]
        depth <- depth + 3
    | None -> ()
    // Which rank-1 input peels may drop the Array<T,1> wrapper for a
    // raw `BLADE_RESTRICT` row pointer (see restrictPeelSites for the proof
    // obligation). Streamed positions are handled by genElementBindingStreamed
    // and never consult this set -- their fiber bind IS an Array<T,1> value.
    let restrictSites = restrictPeelSites codeGen.Bindings
    // Hoist the innermost OUTPUT row as a raw `BLADE_RESTRICT` pointer
    // so the write target is provably distinct from every read row. Gated to
    // the shape the nest builder guarantees to be a scalar cell write:
    // a plain (non-tabulated) dense/compact array output whose rank is exactly
    // the nest depth, so `out[__i0]..[__i(n-2)]` is a `T*` row and the
    // innermost subscript is the only one left. Fold nests (scalar
    // accumulator), scalar outputs and compound/sparse outputs (flat `.data`
    // subscript) are excluded; nothing else about the nest changes.
    let outRowName = $"__orow_{(sanitizeCppName codeGen.OutputName)}"
    let outRowDecl : string option =
        match codeGen.OutputType with
        | ArrayElem at when codeGen.FoldWrapper.IsNone
                            && not (isCompoundArrayType at)
                            && not (isSparseArrayType at)
                            && List.length codeGen.Bindings >= 2
                            && arrayRank at = List.length codeGen.Bindings ->
            let prefix =
                codeGen.Bindings
                |> List.take (List.length codeGen.Bindings - 1)
                |> List.map (fun b -> $"[{b.IndexName}]")
                |> String.concat ""
            Some ($"{(elemTypeToCpp at.ElemType)}* BLADE_RESTRICT {outRowName} = {codeGen.OutputName}{prefix};")
        | _ -> None
    // `BLADE_IVDEP` (`#pragma GCC ivdep` under g++, nothing under cl/clang --
    // see cpp/blade_portability.hpp) on the innermost header, after the
    // row-peel restrict qualifiers: g++ 15.2 drops a restrict qualifier on a
    // BLOCK-SCOPE LOCAL (it only feeds restrict into its points-to solver for
    // function PARAMETERS), so
    // `ivdep` is the assertion GCC actually acts on, removing the runtime
    // alias check and scalar fallback from the vectorized loop.
    //
    // `ivdep` claims NO loop-carried dependence at all; every gate clause is load-bearing:
    //  1. `outRowDecl.IsSome` -- exactly one scalar cell written per iteration
    //     at the innermost loop variable (excludes fold/`+=`/compound writes).
    //  2. no omp coverage instrumentation -- its `__omp_seen` marker rewrites
    //     the same slot every iteration (a WAW chain); test-mode only.
    //  3. the kernel body contains no loop of its own. This clause is not about
    //     SOUNDNESS like the two above -- it is about the pragma being
    //     MEANINGFUL. A `prodsum` / `reduce` / `contains` body, or a nested
    //     combinator application, lowers to an IIFE with a `for` inside it, and
    //     the nest machinery cannot see that loop (it counts nest LEVELS). The
    //     emitted `ivdep` then sat on a loop whose body is a loop, which no
    //     compiler can vectorize -- inert text that reads, to anyone inspecting
    //     the emission, as though the vectorization had already happened. That
    //     misreading is exactly what a performance audit of these emitters
    //     reported. Declining costs nothing (the loop never vectorized) and the
    //     marker below says why, in the generated C++, where someone looking at
    //     this actually looks.
    //
    // Reads are unconstrained (inputs are distinct pools from the fresh
    // output, EmitCpp.fs:48-55). Fused nests are out of scope: every leaf
    // writes its own row, so clause 1 fails by construction there.
    let bodyHasInnerLoop = kernelBodyContainsInnerLoop codeGen.KernelExpr
    // 4. (G41, Codex re-re-review finding 15) the kernel WRITES nothing outside
    //    itself: a body that stores to captured storage (a counter, a shared
    //    cell) carries exactly the loop-carried memory dependence `ivdep`
    //    asserts away. A writer keeps the ordered loop.
    let ivdepEligible =
        outRowDecl.IsSome
        && not (ompInstrument && outerIsParallel)
        && not bodyHasInnerLoop
        && not (exprMayWrite codeGen.KernelExpr)
    // Last nest level that belongs to the OpenMP construct. A `collapse(d)`
    // prefix FUSES its levels into one iteration space, and g++ then rejects a
    // pragma on any inner header of that prefix outright:
    //   error: loop not permitted in intervening code in OpenMP loop body
    //   error: not enough nested loops
    // (element peels between the headers are fine -- a PRAGMA is not.) The depth
    // is read back off the pragma the nest will actually emit rather than
    // re-derived, so this cannot drift from genNestPragma's collapse rule.
    // Levels strictly below this index are ordinary nested loops and may carry
    // ivdep; a `schedule(dynamic)` outer with a triangular inner collapses
    // nothing, so its inner levels stay eligible.
    let ompLastLevel =
        match pragmaLevel with
        | None -> -1
        | Some pl ->
            let txt = genNestPragma (List.skip pl codeGen.Bindings) ""
            // The macro spelling (blade_portability.hpp): BLADE_OMP_PARALLEL_FOR_COLLAPSE(d).
            let m = System.Text.RegularExpressions.Regex.Match(txt, @"_COLLAPSE\((\d+)\)")
            if m.Success then pl + int m.Groups.[1].Value - 1 else pl
    // CANONICAL COMPACT FILL: write the flat pool through the closed-form
    // offsets instead of walking the Iliffe skeleton (see compactFlatWritePlan
    // for the math and the gate; plan-simplex-blocked-compute.md section 0b
    // finding 2 for the measurement). Declined outright when the nest carries a
    // MULTI-LEVEL `collapse` -- the per-level hoists are statements BETWEEN loop
    // headers, which g++ rejects inside a collapsed prefix ("loop not permitted
    // in intervening code in OpenMP loop body"). No compact nest can reach that
    // case today (level 1 of a simplex is triangular, so genNestPragma's
    // collapse prefix stops at 1 and the nest gets `schedule(dynamic)` on level
    // 0 alone), which is exactly why the guard is cheap to keep honest.
    let compactFlat =
        if outRowDecl.IsNone || ompLastLevel > (defaultArg pragmaLevel 0) then None
        else compactFlatWritePlan codeGen outRowName
    match compactFlat with
    | Some plan -> lines <- lines @ [ind depth + plan.PoolDecl]
    | None -> ()
    // HOT-ROW FOLD SCHEDULING (Blade.Optimize.planHotRowFolds): the consumer's
    // per-row fold runs behind each produced row, inside this nest's
    // outermost loop. Only the plain materializing nest admits it: no fold
    // accumulator, no slab / segment / tile substitution of the outer level
    // (the row sums would cover one rank's rows while Allgatherv restores the
    // whole array), no streamed source, no shared-value leaf, a hoisted output
    // row (the rank-2 epilogue reads the row through that restrict pointer),
    // and no `collapse` pragma spanning level 0 and 1 (OpenMP forbids
    // intervening code inside a collapsed prefix). A declined site publishes
    // nothing, and the consumer keeps the two-pass form.
    let hotRow =
        match codeGen.RowEpilogue with
        | Some ep when codeGen.FoldWrapper.IsNone && not codeGen.MpiSlab && codeGen.ShareDecl.IsNone
                       && Map.isEmpty streamed && List.length codeGen.Bindings >= 2
                       && outRowDecl.IsSome && compactFlat.IsNone
                       && not (pragmaLevel = Some 0 && ompLastLevel > 0) ->
            Some ep
        | _ -> None
    // B2r (round-2 cell epilogues): the row's fold advanced per cell inside
    // the inner level (CellStep), when the epilogue offers it and this nest's
    // form admits it: exactly one cell loop per row (rank 2), a plain
    // rectangular inner level, and that loop NOT threaded (a threaded cell
    // loop must finish the row before its ordered chain runs). A refusal is
    // recorded and the epilogue's ordinary Body is rendered instead.
    let cellStep =
        match hotRow with
        | Some ep when ep.CellStep.IsSome ->
            let inner = List.last codeGen.Bindings
            if List.length codeGen.Bindings <> 2 then
                ep.OnCellStepDeclined "the producer nest is not one cell loop per row"; None
            elif ompLastLevel >= 1 || pragmaLevel = Some 1 then
                ep.OnCellStepDeclined "the cell loop is threaded (its row must finish before the ordered chain runs)"; None
            elif not inner.BoundDependencies.IsEmpty || inner.StrictOffset <> 0 || (domainPlanOfLevel inner).IsSome then
                ep.OnCellStepDeclined "the cell loop is not a plain rectangular sweep"; None
            else ep.CellStep
        | _ -> None
    match hotRow with
    | Some ep ->
        lines <- lines @ [ ind depth + $"// hot-row fold: `{ep.RowSumName}` holds the row fold of each row of `{codeGen.OutputName}`, computed right after the row is produced (Blade.Optimize.planHotRowFolds)" ]
        if cellStep.IsSome then
            lines <- lines @ [ ind depth + $"// cell epilogue: each row's fold advances on each cell right behind its store (`{ep.ChainName}`, register-consume), same +0 seed, same ascending order, same products" ]
        for l in ep.Prelude do lines <- lines @ [ind depth + l]
    | None -> ()
    let cellBlock =
        match hotRow, cellStep with
        | Some ep, Some _ when ep.ChainBlock > 0L -> Some ep.ChainBlock
        | _ -> None
    // COMPACT ROW CLASSES (Blade.Optimize.planCompactRowClasses): the orbit
    // fold's per-row class partials behind each packed row. The compact
    // flat-write twin of the hot-row admission above: a rank-2 compact pool
    // written through the hoisted row pointer (`compactFlat`), and none of
    // the forms that would split or reorder the outer level. A declined site
    // publishes nothing; the consumer keeps the orbit-class pass.
    let compactRow =
        match codeGen.CompactRowEpilogue with
        | Some ep when hotRow.IsNone && codeGen.FoldWrapper.IsNone && not codeGen.MpiSlab && codeGen.ShareDecl.IsNone
                       && Map.isEmpty streamed && List.length codeGen.Bindings = 2
                       && compactFlat.IsSome
                       && not (pragmaLevel = Some 0 && ompLastLevel > 0) ->
            Some ep
        | Some ep ->
            recordCodegenDecision "compact-row-classes" "" (Blade.Effects.Declined "the producer is not emitted by the plain compact row nest (sliced, streamed, fused-fold or collapsed form)") [ $"orbit fold of `{codeGen.OutputName}`" ]
            ep.OnDeclined ()
            None
        | None -> None
    match compactRow with
    | Some ep -> for l in ep.Prelude do lines <- lines @ [ind depth + l]
    | None -> ()
    for binding in codeGen.Bindings do
        let levelIdx = bidx
        // Generate the loop header (pragma only on the outermost loop).
        // Fused-fold nests accumulate into shared scalars -- not race-safe
        // under a parallel-for -- so the pragma is suppressed entirely
        // (an omp `reduction(...)` clause is the future upgrade path).
        let isOuter = atOuterLevel
        // The pragma goes on the outermost LICENSED level, which `omp(a: n)`
        // does not force to be level 0 (see pragmaLevelOf). genNestPragma is
        // handed the suffix starting there, so its collapse/dynamic reasoning
        // runs over exactly the levels the pragma governs.
        let pragmaPrefix =
            if codeGen.FoldWrapper.IsNone && pragmaLevel = Some bidx
            then genNestPragma (List.skip bidx codeGen.Bindings) (ind depth) else ""
        recordOmpConstruct codeGen.OutputName pragmaPrefix $"loop nest level {bidx} ({binding.IndexName})"
        // Mark a requested-but-suppressed pragma at the outer level so the
        // dropped clause is visible rather than silent (see ompSuppressedMarker).
        let suppressedMarker =
            if isOuter then
                let reason =
                    // The BUILD KNOB is reported FIRST when it is what declined:
                    // with serial emission on, every other reason is unreachable
                    // as an explanation (the nest would have been serial anyway),
                    // and naming the knob is what tells a reader that the same
                    // source built without it WOULD be threaded.
                    if not (ompThreadEmissionEnabled ()) then ompThreadsSuppressedReason ()
                    elif codeGen.FoldWrapper.IsSome && codeGen.FoldOmpDecline.IsSome then
                        codeGen.FoldOmpDecline.Value
                    elif codeGen.FoldWrapper.IsSome then
                        "fold accumulates into a shared scalar, which is not race-safe"
                    elif nestKernelWrites then
                        "the kernel may write storage outside itself (G41: a writing producer keeps an ordered loop)"
                    elif codeGen.Bindings |> List.exists (fun b -> (domainPlanOfLevel b).IsSome) then
                        "a closed-form domain nest carries one sequential cell counter and has no thread-level form yet"
                    else "the omp(...) depth licenses no level of this nest"
                // "Emitted" means the nest gets a pragma SOMEWHERE, not
                // necessarily at this level -- an inner-licensed nest is
                // parallelized and must not be reported as serial. A Path B
                // fold nest counts as emitted: the parallel region is the
                // prologue above, not a `for` pragma on any level.
                // A MAP licence the fold turned into a chunk plan (bench gap
                // G27) is reported by the FoldChunk lines below when the plan
                // is dead, so it is excluded here -- one census line per drop.
                ompSuppressedMarker (codeGen.OmpRequested && codeGen.FoldChunk.IsNone)
                                    (pragmaLevel.IsSome || foldChunk.IsSome) reason (ind depth)
                // A Path B fold nest requested omp through `FoldChunk`, not
                // through `OmpRequested` (which the fold path leaves unset), so
                // a knob-suppressed chunk plan would otherwise vanish without a
                // trace. This is the census line for exactly that case.
                @ (if codeGen.FoldChunk.IsSome && not (ompThreadEmissionEnabled ())
                   then [ ind depth + $"// {(ompSuppressedPhrase (ompThreadsSuppressedReason ()))}" ]
                   // The row-slot form SPENDS the request (a live team), so
                   // a repro build that took it has no drop to report.
                   elif codeGen.FoldChunk.IsSome && not (fpReassocEnabled ()) && not rowSlotsLive
                   then [ ind depth + $"// {(ompSuppressedPhrase (fpReproReason ()))}" ]
                   else [])
            else []
        atOuterLevel <- false
        // MPI slab mode: the outermost level iterates this rank's slab
        // [__blade_mpi_lo_<out>, __blade_mpi_hi_<out>) -- bounds declared by
        // the slab prologue genApplyCombinator emitted before the nest.
        // Inner levels are untouched.
        let header =
            // A closed-form domain level is its own affine nest; it is never
            // chunked (chunkable excludes it) and never MPI-sliced.
            match domainPlanOfLevel binding with
            | Some plan -> genDomainLoopHeader binding plan
            | None ->
            if isOuter && codeGen.MpiSlab then
                sprintf "for (size_t %s = __blade_mpi_lo_%s; %s < __blade_mpi_hi_%s; %s++) {"
                    binding.IndexName codeGen.OutputName
                    binding.IndexName codeGen.OutputName
                    binding.IndexName
            // Path B: the outermost level iterates this thread's chunk
            // [__rlo, __rhi) of [0, extent). Same substitution shape as the MPI
            // slab above; inner levels are untouched, so a triangular inner nest
            // runs exactly as it would serially for each outer index owned here.
            elif isOuter && foldChunk.IsSome then
                $"for (size_t {binding.IndexName} = __rlo; {binding.IndexName} < __rhi; {binding.IndexName}++) {{"
            // B2r mini-blocks (epi_blk_r > 0): the cell loop walks the block
            // [__cbk, __cbe) that the strip-mine loop opened just above it.
            elif bidx = lastBindingIdx && cellBlock.IsSome then
                $"for (size_t {binding.IndexName} = __cbk; {binding.IndexName} < __cbe; {binding.IndexName}++) {{"
            else genForLoopHeader compoundArrays binding
        // Output row hoist: just outside the innermost header, re-taken per
        // outer iteration. The
        // flat-pool plan replaces the skeleton walk with `pool + <closed-form
        // offset>`; everything downstream (the `__orow[__i(r-1)]` store, the
        // restrict qualifier, BLADE_IVDEP) is unchanged, because the innermost
        // level's own term IS its loop counter at every rank.
        if bidx = lastBindingIdx then
            match compactFlat, outRowDecl with
            | Some plan, _ -> lines <- lines @ [ind depth + plan.RowDecl]
            | None, Some d -> lines <- lines @ [ind depth + d]
            | None, None -> ()
            // B2r: the row's chain, seeded +0 like the IIFE's `T __ps = 0`,
            // declared in the outer body (thread-private under a threaded
            // outer level), then the strip-mine loop when epi_blk_r > 0.
            match hotRow, cellStep with
            | Some ep, Some _ ->
                lines <- lines @ [ ind depth + $"{ep.ChainElem} {ep.ChainName} = 0;" ]
                match cellBlock with
                | Some r ->
                    let bound = genLoopBoundExpr compoundArrays binding
                    lines <- lines @ [ ind depth + $"for (size_t __cbk = 0; __cbk < (size_t)({bound}); __cbk += {r}) {{"
                                       ind (depth + 1) + $"const size_t __cbe = __cbk + {r} < (size_t)({bound}) ? __cbk + {r} : (size_t)({bound});" ]
                    depth <- depth + 1
                | None -> ()
            | _ -> ()
        // `BLADE_IVDEP` must be the LAST thing before the `for`, and only
        // on a header the OpenMP construct does not own (see ompLastLevel).
        // It is the portable spelling -- cpp/blade_portability.hpp expands it
        // to `_Pragma("GCC ivdep")` under g++ and to nothing elsewhere (cl.exe
        // warns C4068 on it and clang does not implement it). `_Pragma` and not
        // `#pragma` because a `#pragma` line cannot come out of a macro, and
        // the emission site cannot `#ifdef` it: nothing may sit between the
        // annotation and its `for`.
        // Not under the B2r cell step: the loop then carries the row's ordered
        // chain, and `ivdep` is no place to make claims about a reduction.
        let ivdepPrefix =
            if ivdepEligible && bidx = lastBindingIdx && bidx > ompLastLevel && cellStep.IsNone
            then $"BLADE_IVDEP\n{(ind depth)}" else ""
        // Say IN THE GENERATED C++ that the vectorization annotation was
        // declined and why -- same rationale as `ompSuppressedMarker`, and the
        // same one-comment-line cost. This case only: the three SOUNDNESS
        // clauses are self-evident from the emitted code around them (a fold's
        // `+=`, the `__omp_seen` write are both visible), whereas "the body hides a loop" is precisely the fact the
        // emission cannot show, which is why the inert pragma read as a real one.
        let ivdepSuppressedMarker =
            if bodyHasInnerLoop && outRowDecl.IsSome
               && not (ompInstrument && outerIsParallel)
               && bidx = lastBindingIdx && bidx > ompLastLevel
            then [ ind depth + "// [ivdep] declined: kernel body contains an inner loop (prodsum/reduce/contains/nested apply), so this header cannot vectorize" ]
            else []
        // M14: the chunk's own `omp simd reduction` on the chunked outer loop
        // (a SIMD construct inside the explicit team; it opens no team).
        let chunkSimdPragma =
            match foldChunk with
            | Some plan when isOuter ->
                (match plan.ChunkForm with
                 | ChunkSimd op -> [ ind depth + $"BLADE_OMP_SIMD_REDUCTION({op}:{fcAcc})" ]
                 | ChunkSerial _ | ChunkRowSlots _ -> [])
            | _ -> []
        lines <- lines @ suppressedMarker @ ivdepSuppressedMarker @ chunkSimdPragma @ [ind depth + pragmaPrefix + ivdepPrefix + header]
        depth <- depth + 1
        // This level's share of the compact output's pool offset, hoisted here
        // because the term is invariant under every level inside it -- which is
        // what makes the addressing O(1) per cell instead of O(r). Levels
        // 0 .. r-2 only; the innermost term is its own loop counter and is spent
        // by the `__orow[...]` subscript.
        match compactFlat with
        | Some plan ->
            (match Map.tryFind levelIdx plan.Hoists with
             | Some hs -> lines <- lines @ (hs |> List.map (fun h -> ind depth + h))
             | None -> ())
        | None -> ()
        // Thread-coverage marker: record this thread as seen and the team size
        // it observes. Each thread writes ONLY its own slot (race-free). Team
        // size is captured per-slot (not a single guarded write) because
        // schedule(dynamic) does not guarantee any thread runs any iteration --
        // taking the max over slots afterward recovers the true team size.
        //
        // Placed inside the INNERMOST loop body (after ALL loop headers), not
        // the outer body: OpenMP `collapse(d)` requires the collapsed loops to
        // be perfectly nested with no intervening code (and OMP API calls are
        // explicitly forbidden between collapsed headers). Marking in the
        // innermost body is past any collapsed prefix and always legal. The
        // marker is idempotent (each thread re-sets its own slot to the same
        // values), so running it per innermost-iteration is harmless.
        if ompInstrument && outerIsParallel && bidx = lastBindingIdx then
            lines <- lines @ [
                ind depth + $"{{ int __tn = blade_omp_get_thread_num(); {covSeen}[__tn] = true; {covTeam}[__tn] = blade_omp_get_num_threads(); }}"
            ]
        bidx <- bidx + 1
        
        // Generate element bindings for all arrays at this level. Zipping an
        // array WITH ITSELF puts two operand slots on the same (array, index):
        // both peel to the byte-identical declaration, so an identical
        // (name, code) pair is emitted once (the second slot's params resolve
        // to the first slot's binding via paramFinalNames below); the second
        // `double A____i0 = A[__i0];` was a g++ redeclaration error.
        let mutable declaredNames : Map<string, string> = Map.empty
        for elem in binding.Elements do
            match Map.tryFind elem.ArrayName streamed with
            | Some sspec ->
                // Streamed source: no array to peel -- accumulate the site
                // coordinate; at the fiber level this emits the provider
                // read + the rank-1 wrapper the kernel body consumes.
                let acc = Map.tryFind elem.ArrayPosition streamSites |> Option.defaultValue []
                let (codeLines, fiberBound, acc') = genElementBindingStreamed binding elem sspec acc
                streamSites <- Map.add elem.ArrayPosition acc' streamSites
                for c in codeLines do
                    lines <- lines @ [ind depth + c]
                (match fiberBound with
                 | Some fname ->
                     currentNames <- Map.add elem.ArrayPosition fname currentNames
                     paramFinalNames <- Map.add elem.ParamVarId fname paramFinalNames
                 | None -> ())
            | None ->
                let currentName =
                    Map.tryFind elem.ArrayPosition currentNames
                    |> Option.defaultValue elem.ArrayName
                let rawRow = Set.contains (levelIdx, elem.ArrayPosition) restrictSites
                let (code, newName) =
                    match windowReads |> Option.bind (fun p -> p.Peels |> List.tryFind (fun (e, _) -> obj.ReferenceEquals(e, elem))) with
                    | Some (_, win) ->
                        let newName = $"{currentName}__{binding.IndexName}"
                        ($"{(elemTypeToCpp elem.ArrayElemType)} {newName} = {win}[static_cast<size_t>(static_cast<int64_t>({binding.IndexName}) - static_cast<int64_t>(__w_lo))];", newName)
                    | None ->
                        match bandPeels |> Option.bind (fun p -> p.OuterPeels |> List.tryFind (fun (e, _) -> obj.ReferenceEquals(e, elem))) with
                        | Some (_, win) ->
                            let newName = $"{currentName}__{binding.IndexName}"
                            let columns = bandPeels.Value.Columns
                            ($"double* BLADE_RESTRICT {newName} = {win} + static_cast<size_t>(static_cast<int64_t>({binding.IndexName}) - static_cast<int64_t>(__w_lo)) * {columns}UL;", newName)
                        | None -> genElementBindingPeel rawRow binding elem currentName
                if Map.tryFind newName declaredNames <> Some code then
                    lines <- lines @ [ind depth + code]
                declaredNames <- Map.add newName code declaredNames
                currentNames <- Map.add elem.ArrayPosition newName currentNames
                // Record mapping for kernel body
                match elem.Virtual with
                | VirtualRange _ | VirtualReverse ->
                    paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
                | RealArray ->
                    paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
    
    match bandPeels with
    | Some p when paramFinalNames <> p.FinalParams ->
        invalidOp "rank-two band certificate did not consume every final scalar peel"
    | _ -> ()
    // Build name map for kernel body from final peeled names
    // Start from outer scope, then overlay kernel params (kernel params take priority)
    let nameMap = paramFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
    // Captures are a FALLBACK, not an override: `c.Name` is the SOURCE-level
    // identifier, which is wrong whenever the captured binding was renamed on
    // emission (a block-local `let a = ...` inside a function body flattens to
    // `auto __v4 = ...`). `outerNames`/`paramFinalNames` already carry the
    // emitted spelling, so only fill in ids the enclosing scope doesn't know --
    // same precedence rule as `captureForwardName`.
    let nameMap =
        withCaptureNames EnclosingNamesWin codeGen.Captures nameMap
    
    // Generate kernel assignment (with Reynolds permutation sum if applicable).
    // The shape of the assignment depends on output type:
    //   - Array output: indexed slot assignment, `name[i][j]... = kernelBody;`.
    //     Each loop binding contributes one bracketed index. This is the standard
    //     case for `method_for(...) <@> kernel` returning a tensor.
    //   - Scalar output: sum accumulation, `name += kernelBody;`. The loop nest
    //     still iterates over input dimensions, but the kernel result is summed
    //     into a scalar accumulator declared by the caller (genApplyCombinator).
    //     Used when the function's return type is scalar even though the
    //     `<@>` kernel produces a per-iteration value (the Cartesian-sum-reduce
    //     pattern).
    let outputIdx =
        match codeGen.OutputType with
        | IRTScalar _ -> ""
        // Tabulated (compound/sparse) output inherits the input's index: write
        // into the compact buffer (.data[r*stride + t]), not a dense [i][j] slot.
        | ArrayElem at when isCompoundArrayType at || isSparseArrayType at ->
            compoundOutputSubscript codeGen.Bindings codeGen.OutputName
        | _ ->
            codeGen.Bindings
            |> List.map (fun b -> $"[{b.IndexName}]")
            |> String.concat ""
    let assignOp =
        match codeGen.OutputType with
        | IRTScalar _ -> "+="
        | _ -> "="

    // LAZY, because rendering is not free of side effects: `exprToCpp`'s
    // refusal path appends to the expression-sentinel cell (which becomes an
    // `#error` in the translation unit) whether or not the caller keeps the
    // string. The literal-row arm below discards this value, and an eagerly
    // rendered array-literal body left a spurious `#error` behind in an
    // otherwise perfectly good program.
    let reynoldsResult =
        lazy (
            match windowReads with
            | Some p ->
                let subst =
                    p.Reads |> List.map (fun (node, w, offset, win) ->
                        let center = exprToCpp nameMap w
                        node, $"{win}[static_cast<size_t>((static_cast<int64_t>({center}) + ({offset}LL)) - static_cast<int64_t>(__w_lo))]")
                { CppExpr = exprToCppWithSubst subst nameMap codeGen.KernelExpr; TotalPerms = 1; UniqueTerms = 1 }
            | None -> genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams codeGen.HasReynolds codeGen.IsAntisymmetric nameMap paramFinalNames)
    if codeGen.HasReynolds && reynoldsResult.Value.UniqueTerms < reynoldsResult.Value.TotalPerms then
        lines <- lines @ [ind depth + $"// Reynolds: {reynoldsResult.Value.UniqueTerms}/{reynoldsResult.Value.TotalPerms} perms unique (dedup {reynoldsResult.Value.TotalPerms / max 1 reynoldsResult.Value.UniqueTerms}x)"]
    // When the output row was hoisted, the write goes through the
    // restrict-qualified row pointer and only the innermost subscript remains.
    let (writeTarget, writeIdx) =
        match outRowDecl with
        | Some _ -> (outRowName, $"[{(List.last codeGen.Bindings).IndexName}]")
        | None -> (codeGen.OutputName, outputIdx)
    // ARRAY-VALUED KERNEL RETURN (stage S3, manifestation M-C1). When the
    // kernel returns an array, the output type carries the kernel's
    // T-dimensions after the iterated ones, so `arrayRank > nest depth` and the
    // per-iteration product is a whole ROW, not a cell. `out[i] = <call>` (what
    // the pre-S3 emitter wrote) assigns an `Array<T,m>` wrapper into a slot the
    // C++ side types as a scalar or a row pointer -- the shape that compiled,
    // ran, and printed `[[], []]`.
    //
    // Instead: evaluate the row once, copy its pool into the output pool at the
    // row's offset, and free it when the callee owned it. The destination is
    // computed from `<name>_extents` -- the SAME table the allocation used, now
    // carrying the trailing T-dims -- rather than from a chained `out[i][j]`
    // subscript, so it is one formula for any inner rank and needs nothing from
    // the wrapper's operator[] typing.
    //
    // Gated to DENSE rectangular outputs: the flat row-major offset below is
    // exact only when no axis is packed. A symmetric/compound/sparse output
    // with an array-valued kernel falls through to the old cell assignment,
    // which the C++ compiler then rejects -- loud, and out of scope here.
    //
    // FREEING is conditional on `freshReturnOf`. A kernel that CONSTRUCTS its
    // row (the S2 call form over a materializing body) hands back storage this
    // call allocated, so not freeing it leaks one row per outer cell. A kernel
    // that PASSES ITS INPUT ROW THROUGH (`lambda(r) -> r`) hands back a view of
    // the operand -- freeing that would destroy the input. `NotFresh` (which is
    // also what an unresolvable callee reads as) leaks rather than guesses,
    // matching the surrounding block's rule 3.
    let rowWriteLines : string list option =
        match codeGen.OutputType with
        | ArrayElem at when codeGen.FoldWrapper.IsNone
                            && not (isCompoundArrayType at)
                            && not (isSparseArrayType at)
                            && not (hasRealSymmetry codeGen.OutputSymmVec)
                            && not (List.isEmpty codeGen.Bindings)
                            && arrayRank at > List.length codeGen.Bindings ->
            let rank = arrayRank at
            let outer = List.length codeGen.Bindings
            let extentsName = $"{codeGen.OutputName}_extents"
            let elemStr = elemTypeToCpp at.ElemType
            let innerCells =
                [ outer .. rank - 1 ] |> List.map (sprintf "%s[%d]" extentsName) |> String.concat " * "
            // Row-major flattening of the OUTER indices (inner indices are 0):
            // (((i0) * e1 + i1) * e2 + i2) ...
            let flatOuter =
                codeGen.Bindings
                |> List.mapi (fun i b -> (i, b.IndexName))
                |> List.fold (fun acc (i, nm) ->
                        if i = 0 then nm
                        else $"({acc}) * {extentsName}[{i}] + {nm}") ""
            let freeLine =
                let callee = match codeGen.KernelExpr with IRApp (f, _, _) -> Some f | _ -> None
                match callee with
                | Some f when freshReturnOf f = FreshPool ->
                    [ $"    deallocate<typename promote<{elemStr}, {(rank - outer)}>::type, nullptr>(__rowv.data, __rowv.extents);" ]
                | _ -> []
            // Literal row: store the cells directly, no temporary. Reynolds is
            // excluded -- symmetrizing a body means SUMMING its permuted
            // copies, and a per-cell sum of array literals is not what the
            // stores below write; that shape falls through to the generic arm.
            //
            // A BLOCK-bodied literal kernel -- scalar lets ahead of the array
            // literal (`lambda(w) -> { let x = ...; [x + y, x - y] }`, the
            // halo/segment shape) -- takes the same direct-store path: the
            // lets render as statements inside the row-write scope, visible
            // to the leaf stores. The shape check is PURE (no rendering), so
            // a declined shape leaves no sentinel side effect behind.
            let rec literalTailOf e = match e with IRLet (_, _, b) -> literalTailOf b | t -> t
            let literalLeaves =
                match literalTailOf codeGen.KernelExpr with
                | IRArrayLit _ as tail when not codeGen.HasReynolds ->
                    match arrayLitScalarLeaves tail,
                          staticCellsOfAxes at outer (rank - 1) with
                    // Leaf count MUST equal the destination row's cell count:
                    // the output extents are deduced from the literal's shape,
                    // so a disagreement means the deduction and the body have
                    // drifted apart, and writing anyway would run off the row.
                    | Some leaves, Some cells when leaves.Length = cells -> Some leaves
                    | _ -> None
                | _ -> None
            match literalLeaves with
            | Some leaves ->
                let subst = emptySubst
                // Render the let prefix (empty for a bare literal body): each
                // non-unit value becomes a scoped local named __v<id> --
                // renderLetExpr's convention, so the leaves' IRVar references
                // resolve -- and unit values keep their statement form.
                let rec renderLetPrefix nm acc e =
                    match e with
                    | IRLet (lid, v, b) ->
                        let nm' = Map.add lid ($"__v{lid}") nm
                        let lines =
                            if isUnitExpr v then
                                match renderUnitStmts subst nm v with
                                | "" -> []
                                | s -> [$"    {s}"]
                            else [$"    auto __v{lid} = {(exprToCppCore subst nm v)};"]
                        renderLetPrefix nm' (acc @ lines) b
                    | _ -> (nm, acc)
                let (leafMap, letLines) = renderLetPrefix nameMap [] codeGen.KernelExpr
                Some ([
                        "{"
                      ] @ letLines @ [
                        $"    const size_t __rowc = (size_t)({innerCells});"
                        $"    {elemStr}* __rowd = nested_array_utilities::pool_base({codeGen.OutputName}.data) + (({flatOuter}) * __rowc);"
                      ] @ (leaves |> List.mapi (fun k leaf ->
                                $"    __rowd[{k}] = {(exprToCppCore subst leafMap leaf)};"))
                        @ [ "}" ])
            | None ->
            Some ([
                    "{"
                    $"    auto __rowv = {reynoldsResult.Value.CppExpr};"
                    $"    const {elemStr}* __rows = nested_array_utilities::pool_base(__rowv.data);"
                    $"    const size_t __rowc = (size_t)({innerCells});"
                    $"    {elemStr}* __rowd = nested_array_utilities::pool_base({codeGen.OutputName}.data) + (({flatOuter}) * __rowc);"
                    "    for (size_t __rk = 0; __rk < __rowc; __rk++) __rowd[__rk] = __rows[__rk];"
                  ] @ freeLine @ [ "}" ])
        | _ -> None
    // Lazy for `reynoldsResult`'s reason: `rowWriteLines`, when it fires, is
    // the whole write, and rendering this line anyway would re-enter the
    // kernel expression -- and its sentinel side effect -- for nothing.
    let assignLine = lazy (
        match codeGen.FoldWrapper, foldChunk with
        // Path B: accumulate into the THREAD-PRIVATE scalar, seeding it
        // from the chunk's first contributed value. The cell value is bound once
        // (the kernel expression can be a large Reynolds sum) and the branch is
        // loop-invariant after the first iteration.
        | Some wname, Some plan ->
            match plan.ChunkForm with
            // G35: the row's complete value into its own slot, indexed by the
            // outer level's loop counter (the nest is one level: the row
            // kernel's body is the whole fold, rendered as it is serially).
            | ChunkRowSlots _ ->
                $"{fcSlot}[{(List.head codeGen.Bindings).IndexName}] = {reynoldsResult.Value.CppExpr};"
            // ChunkSimd: the reduction statement in the `x = x op expr` shape
            // the clause requires, into the identity-seeded private copy.
            | ChunkSimd op -> $"{foldAccName} = {foldAccName} {op} {reynoldsResult.Value.CppExpr};"
            | ChunkSerial _ ->
            sprintf "{ %s __rv = %s; if (%s) %s = %s(%s, __rv); else { %s = __rv; %s = true; } }"
                plan.ElemCpp reynoldsResult.Value.CppExpr
                fcHas foldAccName wname foldAccName
                foldAccName fcHas
        // Fused fold: accumulate the kernel value through the fold-kernel
        // wrapper into the caller-declared scalar accumulator.
        | Some wname, None -> $"{codeGen.OutputName} = {wname}({codeGen.OutputName}, {reynoldsResult.Value.CppExpr});"
        | None, _ ->
            match cellStep with
            // B2r register-consume: the cell bound once (its own element
            // type), stored, then handed to the row's chain.
            | Some step ->
                let cellElem = match codeGen.OutputType with ArrayElem at -> elemTypeToCpp at.ElemType | _ -> "double"
                let stepText = step "__c"
                $"{{ const {cellElem} __c = {reynoldsResult.Value.CppExpr}; {writeTarget}{writeIdx} {assignOp} __c; {stepText} }}"
            | None -> $"{writeTarget}{writeIdx} {assignOp} {reynoldsResult.Value.CppExpr};")
    match rowWriteLines with
    | Some rw -> for l in rw do lines <- lines @ [ind depth + l]
    | None -> lines <- lines @ [ind depth + assignLine.Value]

    // Close all loops. Under the hot-row schedule the inner levels close
    // first, the row's fold runs on the just-completed slice inside the
    // outermost loop, then the outermost level closes.
    match hotRow with
    | Some ep ->
        for _ in List.tail codeGen.Bindings do
            depth <- depth - 1
            lines <- lines @ [ind depth + "}"]
        // B2r mini-blocks: close the strip-mine loop around the cell loop.
        if cellBlock.IsSome then
            depth <- depth - 1
            lines <- lines @ [ind depth + "}"]
        let (elemStr, rank) =
            match codeGen.OutputType with
            | ArrayElem at -> (elemTypeToCpp at.ElemType, arrayRank at)
            | _ -> ("double", 2)
        let outerIdx = (List.head codeGen.Bindings).IndexName
        // Rank 2: the slice IS the hoisted output row, read through the
        // restrict pointer it was written through (any other access path to a
        // row written through a restrict pointer in the same scope would
        // violate the qualifier's contract). Rank >= 3: the slice is the
        // skeleton's leading entry, and the row pointer's scope (the level
        // r-2 body) has already closed.
        let viewData = if rank = 2 then outRowName else $"{codeGen.OutputName}.data[{outerIdx}]"
        lines <- lines @ [ ind depth + "{"
                           ind depth + $"    Array<{elemStr}, {rank - 1}> {ep.RowViewName} = {{ {viewData}, {codeGen.OutputName}.extents + 1 }};" ]
        let epBody = if cellStep.IsSome then ep.ChainBody () else ep.Body ()
        for l in epBody do lines <- lines @ [ind depth + "    " + l]
        if cellStep.IsSome then
            let blocks = match cellBlock with Some r -> $"mini-blocks of {r}" | None -> "one fused loop"
            Blade.Optimize.recordCellEpilogue ep.Subject Blade.Effects.Applied
                [ $"row chain interleave ({blocks}): each row's prodsum(row, row) chain advances on each cell right behind its store (register-consume) into `{ep.ChainName}`, stored as the row's value"
                  "strict-legal: the same products of the same stored values added in the same ascending order to the same +0; the outer combine is unchanged" ]
        lines <- lines @ [ ind depth + "}" ]
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]
        ep.OnEmitted ()
    | None ->
        match compactRow with
        | Some ep ->
            // Compact row classes: the packed row is complete once the inner
            // level closes; its class partials run on it through the row
            // pointer it was written through, still inside the outermost loop
            // (each row by the one thread that produced it).
            for _ in List.tail codeGen.Bindings do
                depth <- depth - 1
                lines <- lines @ [ind depth + "}"]
            let outerIdx = (List.head codeGen.Bindings).IndexName
            for l in ep.Body outRowName outerIdx do lines <- lines @ [ind depth + l]
            depth <- depth - 1
            lines <- lines @ [ind depth + "}"]
            ep.OnEmitted ()
        | None ->
            for _ in codeGen.Bindings do
                depth <- depth - 1
                lines <- lines @ [ind depth + "}"]

    // Path B epilogue: publish this thread's partial, close the region,
    // then combine in THREAD ORDER through the same wrapper -- a fixed order, so
    // a fixed OMP_NUM_THREADS reproduces bit-for-bit. Chunks that contributed
    // nothing (an outer index whose inner nest is empty) are skipped, and the
    // caller's seed -- already in the shared accumulator -- enters the fold first,
    // which is what makes this the serial left fold up to associativity.
    match foldChunk, codeGen.FoldWrapper with
    // G35 epilogue: close the region, then the OUTER chain exactly as the
    // serial walk runs it -- the caller's seed (already in the accumulator)
    // first, every slot in ascending index order through the same wrapper.
    // No partial combine: nothing here is a reassociation.
    | Some _, Some wname when rowSlotsLive ->
        lines <- lines @ pathBTeamClose depth []
            [ $"for (size_t __ri = 0; __ri < {fcRn}; __ri++) {codeGen.OutputName} = {wname}({codeGen.OutputName}, {fcSlot}[__ri]);"
              $"delete[] {fcSlot};" ]
        depth <- depth - 3
    | Some plan, Some wname ->
        // ChunkSimd: a one-level chunk contributed exactly when it was
        // non-empty (the identity seed is never published for an empty one).
        let hadExpr =
            match plan.ChunkForm with
            | ChunkSimd _ -> "(__rhi > __rlo)"
            | ChunkSerial _ | ChunkRowSlots _ -> fcHas
        lines <- lines @ pathBTeamClose depth
            [ $"{fcPart}[__rt] = {fcAcc};"
              $"{fcHad}[__rt] = {hadExpr};" ]
            [ $"for (int __rt = 0; __rt < {fcT}; __rt++) if ({fcHad}[__rt]) {codeGen.OutputName} = {wname}({codeGen.OutputName}, {fcPart}[__rt]);"
              $"delete[] {fcPart}; delete[] {fcHad};" ]
        depth <- depth - 3
    | _ -> ()

    // [omp-coverage] after the nest: report the parallel team size and the
    // number of threads that actually did work. The harness uses:
    //   - teamsz > 1               => a genuine parallel region was created
    //   - maxth > 1 && teamsz == 1 => ERROR: pragma not honored (serial region)
    //   - maxth > 1 && teamsz > 1 && distinct == 1 => WARNING: region parallel
    //                                but scheduler put all work on one thread
    //                                (an allowed scheduler choice, not a bug)
    //   - maxth == 1               => single-core context, correctly serial
    if ompInstrument && outerIsParallel then
        lines <- lines @ [
            ind depth + "{ int __omp_distinct = 0; int __omp_teamsz = 0;"
            ind depth + $"  for (int __t = 0; __t < {covMaxth}; __t++) {{"
            ind depth + $"    if ({covSeen}[__t]) __omp_distinct++;"
            ind depth + $"    if ({covTeam}[__t] > __omp_teamsz) __omp_teamsz = {covTeam}[__t];"
            ind depth + "  }"
            ind depth + $"  std::cout << \"[omp-coverage] region={regionTag} teamsz=\" << __omp_teamsz << \" distinct=\" << __omp_distinct << \" maxth=\" << {covMaxth} << std::endl;"
            ind depth + $"  delete[] {covSeen}; delete[] {covTeam}; }}"
        ]

    lines

/// The ordinary (no streamed sources) nest emitter -- every existing call
/// site goes through here; only genApplyCombinator's provider-aware paths
/// use genLoopNestStreamed directly.
let genLoopNestStreamed (streamed: Map<string, ProviderReadSpec>) (codeGen: LoopNestCodeGen) (outerNames: Map<int, string>) (indent: int) : string list =
    genLoopNestStreamedCore None None streamed codeGen outerNames indent

/// Only the segment caller that certified the final body/peels supplies this.
let internal genLoopNestWindowed (plan: Rank1WindowReads) (streamed: Map<string, ProviderReadSpec>)
                                (codeGen: LoopNestCodeGen) (outerNames: Map<int, string>) (indent: int) : string list =
    genLoopNestStreamedCore (Some plan) None streamed codeGen outerNames indent

/// Stale proof is rejected before entering the mutating renderer. Callers may
/// then render the ordinary I1 route with all source owners still present.
let internal tryGenLoopNestRank2Band (plan: Rank2BandPeels) (streamed: Map<string, ProviderReadSpec>)
                                   (codeGen: LoopNestCodeGen) (outerNames: Map<int, string>) (indent: int)
                                   : Result<string list, string> =
    if not (rank2BandStillValid plan codeGen outerNames streamed) then
        Error "stale final rank-two band certificate"
    else Ok (genLoopNestStreamedCore None (Some plan) streamed codeGen outerNames indent)

let genLoopNest (codeGen: LoopNestCodeGen) (outerNames: Map<int, string>) (indent: int) : string list =
    genLoopNestStreamed Map.empty codeGen outerNames indent


// Flat-pool elementwise mode: when a materializing nest is INDEX-FREE
// ELEMENTWISE -- every operand read as a full-depth peel at the loop indices
// in order, nothing in the body mentions a loop index -- the nest collapses
// to ONE flat loop over the contiguous backing pools:
//
//     double* BLADE_RESTRICT __fp_out = pool_base(out.data);
//     const double* BLADE_RESTRICT __fp_A = pool_base(A.data);
//     BLADE_IVDEP                          // or: #pragma omp parallel for simd
//     for (size_t __fk = 0; __fk < N; __fk++)
//         __fp_out[__fk] = <body with A[coords] -> __fp_A[__fk]>;
//
// SOUNDNESS, three facts:
//  1. POOL CONTIGUITY: allocate<>/allocate_strict<> thread ONE offset through
//     ONE pool in DFS leaf order; ragged/gather pools are excluded (they fail
//     the literal-extent/IxKPlain gates and can never reach here).
//  2. VISIT ORDER == POOL ORDER: each level peels at the raw loop variable and
//     every bound equals the allocator's row length there, so the flat index
//     __fk is every operand's (and the output's) DFS pool offset alike.
//  3. PER-CELL EVALUATION IS UNCHANGED: the kernel expression is byte-identical;
//     only the leaf-read spelling changes (`__fp_A[__fk]` vs a per-iteration
//     local). Reads can't observe a write: the output pool is a fresh
//     allocation distinct from every input (EmitCpp.fs:48-55).
//
// The gate is deliberately CONSERVATIVE -- the nested fallback is always
// correct, so every uncertain shape returns None.

/// Binomial C(m, k) in int64, 0 when k < 0 or m < k (OrbRank.binomOrZero).
let internal flatBinom (m: int64) (k: int) : int64 = Blade.OrbRank.binomOrZero m k

/// One storage GROUP of a flat-eligible array: (rank, symmetry class, literal
/// extent). Two arrays whose group lists are structurally equal have
/// byte-identical pool layouts, which is exactly the agreement the flat rewrite
/// needs (spec gate 3 + gate 4 -- extents are part of the signature, so
/// "provably equal extents" is decided here rather than assumed; Blade's unify
/// does not compare extents).
type internal FlatGroup = { GRank: int; GSym: SymmetryClass; GExtent: int64 }

/// Project an array type into a flat-eligible shape signature, or None.
///
/// Refused outright: virtual arrays; any non-plain index KIND (compound,
/// sparse, ragged, dep, group, irreps, orbit -- none of which is a plain
/// contiguous skeleton the loop bounds describe); `__halowin`-prefixed tags
/// (a halo window's reads are offset from the loop index, so the flat index is
/// NOT the operand's pool offset); dependent extents; non-literal extents (only a
/// compile-time constant cell count is emitted); Hermitian and wreath classes
/// (Hermitian shares symmetric STORAGE but its mirror conjugates, and the
/// wreath pool is the section 4 iterated-binomial fold, not this product) -- both are
/// simply future work, not unsoundness.
let internal flatShapeSignature (arr: IRArrayType) : FlatGroup list option =
    if arr.IsVirtual then None else
    let groups =
        arr.IndexTypes
        |> List.map (fun ix ->
            // `__halowin`, not a blanket `__`: every OTHER reserved tag
            // (__orbidx / __compoundidx / __seq / __group_* / __sparseidx /
            // __raggedidx*) carries a non-plain IxKind and is already refused by
            // the `IxKind <> IxKPlain` test beside this one. The blanket form
            // therefore refused exactly one thing on tag grounds alone: the
            // anonymous range's `__anon` (Lowering.fs, TypeCheckInfer.fs), which
            // IS a plain contiguous skeleton -- so `x0 + dx * Float64(0..n)`,
            // the documented top-rung idiom, missed the flat path that the
            // identical `Float64(range<I>)` took. Same predicate shape as the
            // lane-parallel gate in CodeGenBinding.fs.
            let tagOk = match ix.Tag with Some t -> not (t.StartsWith "__halowin") | None -> true
            if ix.IxKind <> IxKPlain || not tagOk || not ix.Dependencies.IsEmpty then None
            else
                match ix.Extent with
                | IRLit (IRLitInt n) when n > 0L ->
                    (match ix.Symmetry with
                     | SymNone when ix.Rank = 1 -> Some { GRank = 1; GSym = SymNone; GExtent = n }
                     | SymSymmetric | SymAntisymmetric when ix.Rank >= 2 ->
                         Some { GRank = ix.Rank; GSym = ix.Symmetry; GExtent = n }
                     | _ -> None)
                | _ -> None)
    if groups |> List.forall Option.isSome && not groups.IsEmpty
    then Some (groups |> List.map Option.get)
    else None

/// Number of scalars in the pool of an array with this signature = the number
/// of leaves allocate<> emits = the number of iterations the nest runs.
///   dense axis (rank 1)      -> n
///   symmetric group rank r   -> C(n + r - 1, r)   (inclusive simplex)
///   antisymmetric group r    -> C(n, r)           (STRICT simplex: rows are
///                                                  SHORTENED, no dead diagonal)
/// Groups multiply: each is a contiguous block of dims and the skeleton nests
/// them left to right.
let internal flatCellCount (sg: FlatGroup list) : int64 =
    sg |> List.fold (fun acc g ->
        let cells =
            match g.GSym with
            | SymNone -> g.GExtent
            | SymSymmetric -> flatBinom (g.GExtent + int64 g.GRank - 1L) g.GRank
            | SymAntisymmetric -> flatBinom g.GExtent g.GRank
            | _ -> 0L
        acc * cells) 1L

/// Does the built nest iterate EXACTLY the space this signature describes, one
/// leaf per innermost iteration, in DFS order? Compares every loop level's
/// extent, bound dependencies and strict offset against what the allocator's
/// recurrence implies for the group the level belongs to.
///
/// Dependency LISTS are compared as SETS: the co-iteration builder emits
/// `[base .. level-1]` ascending while the outer-product builder emits the
/// `iminMap` chain descending. Both render as the same bound subtraction (a
/// sum), so set equality is the right identity here.
let internal flatNestMatchesSignature (sg: FlatGroup list) (bindings: LoopIndexBinding list) : bool =
    let expected =
        sg
        |> List.fold (fun (baseDim, acc) g ->
            let triangular = (g.GSym = SymSymmetric || g.GSym = SymAntisymmetric)
            let levels =
                [ for k in 0 .. g.GRank - 1 ->
                    let deps = if triangular && k > 0 then Set.ofList [ baseDim .. baseDim + k - 1 ] else Set.empty
                    let strict = if g.GSym = SymAntisymmetric then k else 0
                    (g.GExtent, deps, strict) ]
            (baseDim + g.GRank, acc @ levels)) (0, [])
        |> snd
    List.length expected = List.length bindings
    && List.forall2 (fun (i, n, deps, strict) (b: LoopIndexBinding) ->
            b.FusedRank.IsNone
            // Level IS the position: `expected` is indexed by position while
            // BoundDependencies name LEVELS, so a reordered nest would compare
            // the two against each other. (Both builders emit them in order;
            // this pins that rather than assuming it.)
            && b.Level = i
            && b.IndexName = $"__i{i}"
            && (match b.Extent with IRLit (IRLitInt e) -> e = n | _ -> false)
            && Set.ofList b.BoundDependencies = deps
            && b.StrictOffset = strict)
        (expected |> List.mapi (fun i (n, d, s) -> (i, n, d, s))) bindings

/// B1 (round-2 cell epilogues): the rank-1 flat producer with its consumer's
/// fold fused behind the stores, in the form `ep` names (decided per build by
/// CodeGenCuda.buildFlatEpilogue). `head` is the flat block's opening lines
/// (comment, `{`, the pool pointers, `__fp_cells`); the producer statement is
/// the SAME text the plain flat loop writes. That makes the stored cells the
/// same values under -ffp-contract=off (and in practice under fast, which
/// `blade test cell-epilogue` checks by raw bits), but it is not a proof
/// under fast contraction: strip-mining changes the context g++ contracts
/// in. Identical producer values are a PRECONDITION of the licensed forms'
/// bit identity, not a consequence of it. None = the plain flat
/// loop (the caller records nothing; the apply emitter's post-check records
/// the decline, so the consumer keeps the two-pass fold atomically).
///   FlatLanesSerial  P1: produce E cells, feed them (E / B whole blocks) from
///                    L1, ..., produce the tail, finish -> PartName
///   FlatLanesChunks  P1 per Path B chunk inside one team: thread t produces
///                    and feeds [chunk_lo(n,t,T), chunk_lo(n,t+1,T)); partials
///                    PartName[t] / HadName[t] for the consumer's thread-order
///                    combine (seed first, empty chunks skipped)
///   FlatChain        P2: one loop, each cell bound once, stored, then handed
///                    to the fold's ordered chain (no simd pragma: the chain is
///                    an in-order reduction; g++ vectorizes it as a fold-left)
let tryGenFlatEpilogueNest (ep: FlatEpilogue) (outName: string) (outPool: string) (outElem: string) (cellExpr: string) (cells: int64)
                           (allParallel: bool) (threadsOn: bool) (producerWrites: bool) (pragmaMarker: string list)
                           (head: string list) (operandPools: string list) (indent: int) : string list option =
    let ind n = String.replicate n "    "
    let i1 = ind (indent + 1)
    let i2 = ind (indent + 2)
    let i3 = ind (indent + 3)
    // A7c (gap G69): the producer-input legs (attachEpilogueLegs). Their
    // declarations go before the sweep's scope (the join reads them after it);
    // a strict leg is its ascending chain over the operand cells, a licensed
    // leg its own lane state over the operand's blocks.
    let legPool (fl: FlatLeg) = List.item fl.OperandPos operandPools
    let legPreludes = ep.Legs |> List.collect (fun fl -> fl.Prelude |> List.map (fun l -> ind indent + l))
    let legLaneInits (i: string) =
        ep.Legs |> List.choose (fun fl ->
            fl.LaneK |> Option.map (fun k -> i + $"blade_fold::lane_state<{fl.ElemCpp}, {k}> {fl.StateName} = blade_fold::lane_init<{fl.ElemCpp}, {k}>();"))
    let legBlock (i: string) (lo: string) (hi: string) =
        ep.Legs |> List.collect (fun fl ->
            match fl.LaneK with
            | Some k -> [ i + $"blade_fold::feed_sum<{fl.ElemCpp}, {k}>({fl.StateName}, {legPool fl} + {lo}, {ep.Block / fl.LaneBlock});" ]
            | None ->
                let step = fl.ChainStep (legPool fl + "[__ek]")
                [ i + $"for (size_t __ek = {lo}; __ek < {hi}; __ek++) {step}" ])
    let legTail (i: string) =
        ep.Legs |> List.collect (fun fl ->
            match fl.LaneK with
            | Some k -> [ i + $"{fl.PartName} = blade_fold::finish_sum<{fl.ElemCpp}, {k}>({fl.StateName}, {legPool fl} + __cep_b, __fp_cells - __cep_b);" ]
            | None ->
                let step = fl.ChainStep (legPool fl + "[__ek]")
                [ i + $"for (size_t __ek = __cep_b; __ek < __fp_cells; __ek++) {step}" ])
    let legCellSteps = ep.Legs |> List.map (fun fl -> fl.ChainStep (legPool fl + "[__fk]"))
    let legNote (i: string) =
        if ep.Legs.IsEmpty then []
        else [ i + $"// A7c: {ep.Legs.Length} producer-input leg(s) ride this sweep (Blade.Optimize.producerEpilogueLegAdmission)" ]
    // G41 (finding 15): a producer that WRITES captured storage keeps an
    // ordered loop -- no `ivdep` (an omp-licensed producer cannot write: BL4005).
    let simdPragma = if allParallel then "BLADE_OMP_SIMD" elif producerWrites then "" else "BLADE_IVDEP"
    let feed = if ep.Square then "feed_sq" else "feed_sum"
    let finish = if ep.Square then "finish_sq" else "finish_sum"
    let st = $"blade_fold::lane_state<{ep.ElemCpp}, {ep.K}>"
    let nb = ep.Block / ep.LaneBlock
    let produceLoop (i: string) (lo: string) (hi: string) =
        [ i + simdPragma
          i + $"for (size_t __fk = {lo}; __fk < {hi}; __fk++)"
          i + $"    {outPool}[__fk] = {cellExpr};" ]
        |> List.filter (fun s -> s.Trim() <> "")
    match ep.Form with
    | FlatLanesSerial when not (allParallel && threadsOn) && ep.Block % ep.LaneBlock = 0L ->
        (foldLanesUsedCell ()).Value <- true
        if allParallel then
            recordOmpConstruct outName simdPragma "cell epilogue: the flat producer's blocks (omp simd), each fed into the fold's lane state"
        Some (
            [ ind indent + $"{ep.ElemCpp} {ep.PartName};   // cell epilogue: the fold's lane-tree partial, fed behind the stores below" ]
            @ legPreludes
            @ head
            @ pragmaMarker
            @ legNote i1
            @ [ i1 + $"{st} __cep_ls = blade_fold::lane_init<{ep.ElemCpp}, {ep.K}>();" ]
            @ legLaneInits i1
            @ [ i1 + "size_t __cep_b = 0;"
                i1 + $"for (; __cep_b + {ep.Block} <= __fp_cells; __cep_b += {ep.Block}) {{" ]
            @ produceLoop i2 "__cep_b" $"__cep_b + {ep.Block}"
            @ [ i2 + $"blade_fold::{feed}<{ep.ElemCpp}, {ep.K}>(__cep_ls, {outPool} + __cep_b, {nb});" ]
            @ legBlock i2 "__cep_b" $"__cep_b + {ep.Block}"
            @ [ i1 + "}" ]
            @ produceLoop i1 "__cep_b" "__fp_cells"
            @ [ i1 + $"{ep.PartName} = blade_fold::{finish}<{ep.ElemCpp}, {ep.K}>(__cep_ls, {outPool} + __cep_b, __fp_cells - __cep_b);" ]
            @ legTail i1
            @ [ ind indent + "}" ])
    | FlatLanesChunks when allParallel && threadsOn && ep.Block % ep.LaneBlock = 0L && ep.Legs.IsEmpty ->
        (foldLanesUsedCell ()).Value <- true
        (ompApiUsedCell ()).Value <- true
        // The OLD fold's team sizing, verbatim (laneLines' threaded arm):
        // max threads, clamped to the sweep, at least one.
        let teamDecls =
            [ ind indent + $"int {ep.TeamName} = blade_omp_get_max_threads();   // cell epilogue: the fold's Path B team runs the producer"
              ind indent + $"if ((size_t){ep.TeamName} > (size_t){cells}UL) {ep.TeamName} = (int){cells}UL;"
              ind indent + $"if ({ep.TeamName} < 1) {ep.TeamName} = 1;"
              ind indent + $"{ep.ElemCpp}* {ep.PartName} = new {ep.ElemCpp}[{ep.TeamName}];"
              ind indent + $"bool* {ep.HadName} = new bool[{ep.TeamName}]();" ]
        recordOmpConstruct outName "omp parallel num_threads(T) + fixed-order combine"
            "cell epilogue: the fold's Path B team produces and folds its chunk_lo chunk of the flat sweep"
        Some (
            teamDecls
            @ head
            @ [ i1 + $"BLADE_OMP_PARALLEL_NUM_THREADS({ep.TeamName})"
                i1 + "{"
                i2 + "const int __rnt = blade_omp_get_num_threads();"
                i2 + "const int __rt = blade_omp_get_thread_num();"
                i2 + "const size_t __rlo = blade_fold::chunk_lo(__fp_cells, (size_t)__rt, (size_t)__rnt);"
                i2 + "const size_t __rhi = blade_fold::chunk_lo(__fp_cells, (size_t)__rt + 1, (size_t)__rnt);"
                i2 + $"{st} __cep_ls = blade_fold::lane_init<{ep.ElemCpp}, {ep.K}>();"
                i2 + "size_t __cep_b = __rlo;"
                i2 + $"for (; __cep_b + {ep.Block} <= __rhi; __cep_b += {ep.Block}) {{" ]
            @ produceLoop i3 "__cep_b" $"__cep_b + {ep.Block}"
            @ [ i3 + $"blade_fold::{feed}<{ep.ElemCpp}, {ep.K}>(__cep_ls, {outPool} + __cep_b, {nb});"
                i2 + "}" ]
            @ produceLoop i2 "__cep_b" "__rhi"
            @ [ i2 + $"if (__rhi > __rlo) {{ {ep.PartName}[__rt] = blade_fold::{finish}<{ep.ElemCpp}, {ep.K}>(__cep_ls, {outPool} + __cep_b, __rhi - __cep_b); {ep.HadName}[__rt] = true; }}"
                i1 + "}"
                ind indent + "}" ])
    | FlatChain when not (allParallel && threadsOn) ->
        let stepText = ep.ChainStep "__c"
        let cellStmt (i: string) =
            [ i + "for (size_t __fk = __cep_b; __fk < __cep_e; __fk++) {"
              i + $"    const {outElem} __c = {cellExpr};"
              i + $"    {outPool}[__fk] = __c;"
              i + $"    {stepText}" ]
            @ (legCellSteps |> List.map (fun s -> i + "    " + s))
            @ [ i + "}" ]
        let loop =
            if ep.Block > 0L then
                [ i1 + $"for (size_t __cep_b = 0; __cep_b < __fp_cells; __cep_b += {ep.Block}) {{"
                  i2 + $"const size_t __cep_e = __cep_b + {ep.Block} < __fp_cells ? __cep_b + {ep.Block} : __fp_cells;" ]
                @ cellStmt i2
                @ [ i1 + "}" ]
            else
                [ i1 + "const size_t __cep_b = 0, __cep_e = __fp_cells;" ]
                @ cellStmt i1
        Some (
            (ep.ChainPrelude |> List.map (fun l -> ind indent + l))
            @ legPreludes
            @ head
            @ pragmaMarker
            @ [ i1 + "// cell epilogue: the fold's ordered chain advances on each cell right behind its store (no simd pragma: an in-order reduction)" ]
            @ legNote i1
            @ loop
            @ [ ind indent + "}" ])
    | _ -> None

/// Flat elementwise-nest detection + emission. Returns the flat loop's lines,
/// or None to fall through to `genLoopNestStreamed` unchanged.
///
/// `operandTypes` is `ApplyInfo.ArrayTypes`, positionally parallel to
/// `codeGen.InputArrayNames` (both are indexed by `ElementBinding.ArrayPosition`).
/// The flat elementwise path's PLAN (its gates, operand pointers, body and
/// pragma), shared by the single-map renderer below and the sibling-map
/// sweep (round 4 wave 2, PM04), which writes several outputs in one loop.
type internal FlatPlan =
    { Depth: int; AllDense: bool; Cells: int64; ShapeNote: string
      OutPool: string; OutElem: string; OutDecl: string
      OperandDecls: string list; OperandPools: string list
      Body: string; AllParallel: bool; ThreadsOn: bool; ProducerWrites: bool
      Pragma: string; PragmaMarker: string list; Head: string list }

let internal flatElementwisePlanOf
        (streamed: Map<string, ProviderReadSpec>)
        (operandTypes: IRArrayType list)
        (codeGen: LoopNestCodeGen)
        (outerNames: Map<int, string>)
        (indent: int) : FlatPlan option =
    let ind n = String.replicate n "    "
    let bindings = codeGen.Bindings
    let depth = List.length bindings
    let nArrays = List.length codeGen.InputArrayNames
    // ---- Gate 1: the nest is a plain materializing single-leaf map ----------
    //
    // FoldWrapper/FoldChunk: a fold writes a scalar accumulator, not one output
    // cell per iteration. MpiSlab: the outer level iterates a rank slab, not
    // [0, extent). HasReynolds: the body reads PERMUTED coordinates of its
    // operands, which is the opposite of index-free. Streamed sources have no
    // materialized pool at all. Multi-leaf fused trees never reach here
    // (genFusedLoopNestStreamed is a different emitter).
    //
    // ompTestModeEnabled: the omp-coverage instrumentation writes a per-thread
    // marker INSIDE the innermost body and reports ground truth about the nest
    // that ran. Rather than grow a second instrumentation site whose numbers
    // would have to be kept in step, the fast path stands down in test mode --
    // `blade test omp-coverage` then measures exactly the nest it always has.
    if codeGen.FoldWrapper.IsSome || codeGen.FoldChunk.IsSome then None
    elif codeGen.MpiSlab || codeGen.HasReynolds || codeGen.IsAntisymmetric then None
    elif not (Map.isEmpty streamed) then None
    elif ompTestModeEnabled () then None
    elif depth = 0 || nArrays = 0 then None
    elif List.length operandTypes <> nArrays then None
    else
    // ---- Gate 2: the output is a plain (non-tabulated) array of nest rank ---
    match codeGen.OutputType with
    | ArrayElem outTy when not (isCompoundArrayType outTy)
                           && not (isSparseArrayType outTy)
                           && arrayRank outTy = depth ->
        // ---- Gate 3+4: one shape signature shared by the output and EVERY
        // operand. Structural equality covers the storage class, the group
        // decomposition AND the literal extents in one comparison.
        match flatShapeSignature outTy with
        | None -> None
        | Some sg when operandTypes |> List.forall (fun t -> flatShapeSignature t = Some sg) ->
            // Storage-class restriction (spec gate 3): all-dense-rectangular,
            // or ONE compact group spanning the whole array. The mixed shape
            // (a dense axis beside an antisym residual, AllocPerGroupStrict) is
            // contiguous too, but is left to a later pass -- its nest is not
            // exercised by the corpus and is out of scope here.
            let allDense = sg |> List.forall (fun g -> g.GSym = SymNone)
            let singleCompact =
                match sg with
                | [ g ] -> g.GSym = SymSymmetric || g.GSym = SymAntisymmetric
                | _ -> false
            if not (allDense || singleCompact) then None
            elif not (flatNestMatchesSignature sg bindings) then None
            else
            // ---- Gate 2 (reads): every operand is a RealArray scalar
            // full-depth peel at exactly the loop indices in order. Level k
            // must carry one element per operand position, all at
            // RankComponent = k over an array of rank = depth. This is what
            // rules out cross-indexing (A[j][i] reorders RankComponents),
            // partial peels and fiber arguments (ArrayRank > depth leaves an
            // Array<T,1> at the last level), index-variable params
            // (VirtualRange/VirtualReverse bind the loop index INTO the body)
            // and outer-product nests (a level owned by one array only).
            let elementsOk =
                bindings |> List.forall (fun b ->
                    let els = b.Elements
                    List.length els = nArrays
                    && (els |> List.map (_.ArrayPosition) |> Set.ofList) = Set.ofList [ 0 .. nArrays - 1 ]
                    && els |> List.forall (fun e ->
                            (e.Virtual.IsRealArray)
                            && e.RankComponent = b.Level
                            && e.ArrayRank = depth
                            && e.ArrayName = (codeGen.InputArrayNames |> List.item e.ArrayPosition)
                            // A `__halowin` slot tag marks a halo window, whose
                            // reads are OFFSET from the loop index; flatShapeSignature
                            // has already refused those on the array types, so this
                            // is a belt-and-braces read of the SAME tag through
                            // the element record -- and it must stay the same
                            // predicate, or the pair disagrees about `__anon`.
                            && (match e.SlotTag with Some t -> not (t.StartsWith "__halowin") | None -> true)))
            let cells = flatCellCount sg
            // OpenMP licensing. The flat loop FUSES every level, so threading
            // it threads every dimension -- which only the full licence grants.
            // A PARTIAL licence (`omp(a: 1)` over a rank-2 nest) is honoured by
            // falling back to the nest, where the pragma lands on exactly the
            // licensed prefix; over-threading here would silently exceed what
            // the user granted. Likewise a requested-but-unlicensed nest falls
            // back so genLoopNestStreamed's `// [omp] requested but emitted
            // serial` marker still appears.
            let allParallel = bindings |> List.forall (_.IsParallel)
            if not elementsOk then None
            elif cells <= 0L then None
            elif codeGen.OmpRequested && not allParallel then None
            else
            let outElem =
                match codeGen.OutputType with
                | ArrayElem at -> elemTypeToCpp at.ElemType
                | t -> irTypeToCpp t
            // Distinct operand NAMES get one pool pointer each: `f(A, A)` puts
            // two ArrayPositions on one array, and two declarations of one
            // pointer name would be a g++ redeclaration error.
            let poolOf (n: string) = $"__fp_{(sanitizeCppName n)}"
            let operandDecls =
                codeGen.InputArrayNames
                |> List.mapi (fun i n -> (i, n))
                |> List.distinctBy snd
                |> List.map (fun (i, n) ->
                    let elemCpp =
                        operandTypes |> List.tryItem i
                        |> Option.map (fun (t: IRArrayType) -> elemTypeToCpp t.ElemType)
                        |> Option.defaultValue outElem
                    $"const {elemCpp}* BLADE_RESTRICT {(poolOf n)} = nested_array_utilities::pool_base({n}.data);")
            // Kernel-body name map: each operand's leaf param renders as a pool
            // subscript instead of the per-iteration peel local. Subscript binds
            // tighter than every operator, so no parenthesization is needed.
            // Captures fill in only ids the enclosing scope does not know --
            // the same precedence rule genLoopNestStreamed uses.
            let paramFinalNames =
                bindings
                |> List.collect (_.Elements)
                |> List.fold (fun acc (e: ElementBinding) ->
                        Map.add e.ParamVarId ($"{(poolOf e.ArrayName)}[__fk]") acc)
                   Map.empty
            let nameMap = paramFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
            let nameMap =
                withCaptureNames EnclosingNamesWin codeGen.Captures nameMap
            let body =
                genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams
                                          false false nameMap paramFinalNames
            // Pragma. Exactly one of the two, never both, and always the LAST
            // line before the `for` (an OpenMP construct rejects any
            // intervening code between itself and its loop).
            //
            // A restrict qualifier on a BLOCK-SCOPE LOCAL is dropped by GCC (it only
            // feeds restrict into its points-to solver for function
            // PARAMETERS), so the qualifiers above document intent and the
            // PRAGMA carries the dependence assertion. Both forms are
            // discharged by the same fact: exactly one array write per
            // iteration, through the fresh output pool, at the monotone flat
            // index -- no loop-carried dependence of any kind.
            //
            // BUILD KNOB. A serial-emission build drops the THREAD half and
            // keeps the VECTOR half: `#pragma omp simd`. That is not a
            // compromise, it is the decomposition -- `parallel for simd` is two
            // constructs, only the first of which outlines the body and opens a
            // team, and the dependence fact both rest on (one write per
            // iteration, fresh output pool, monotone flat index) is the same
            // fact. Falling back to `BLADE_IVDEP` here would have thrown away
            // vectorization the knob was never about. See
            // `ompThreadEmissionEnabled`.
            let threadsOn = ompThreadEmissionEnabled ()
            // G41 (finding 15): the dependence fact above is about the OUTPUT
            // pool; a kernel that also stores to captured storage has a real
            // loop-carried dependence, so a writer gets no `ivdep` (an
            // omp-licensed kernel cannot write: BL4005).
            let producerWrites = exprMayWrite codeGen.KernelExpr
            // The write veto DOMINATES the omp routes too (Codex finding 18):
            // lowering refuses a parallel kernel whose visible writes reach
            // captured storage (BL4005 through provenance), so this only
            // fires on a conservative "may" (an unresolved call) -- and then
            // the loop stays ordered, with the dropped request on record.
            let ompDroppedForWrites = allParallel && producerWrites
            let allParallel = allParallel && not producerWrites
            let pragma =
                if allParallel && threadsOn then "BLADE_OMP_PARALLEL_FOR_SIMD"
                elif allParallel then "BLADE_OMP_SIMD"
                elif producerWrites then ""
                else "BLADE_IVDEP"
            // (The OpenMP construct is recorded once the FORM is chosen below:
            // the cell-epilogue forms spend the licence differently.)
            // Census line, so a licensed-but-serialized flat loop is not silent.
            // It sits BEFORE the pragma: nothing may come between an OpenMP
            // construct and the `for` it governs.
            let pragmaMarker =
                if ompDroppedForWrites
                then [ ind (indent + 1) + "// " + ompSuppressedPhrase "the kernel may write storage outside itself (G41: a writing producer keeps an ordered loop)" ]
                elif allParallel && not threadsOn
                then [ ind (indent + 1) + $"// {(ompSuppressedPhrase (ompThreadsSuppressedReason ()))}" ]
                else []
            let shapeNote =
                sg
                |> List.map (fun g ->
                    match g.GSym with
                    | SymNone -> string g.GExtent
                    | SymSymmetric -> $"sym<{g.GRank},{g.GExtent}>"
                    | SymAntisymmetric -> $"antisym<{g.GRank},{g.GExtent}>"
                    | s -> sprintf "%A" s)
                |> String.concat " x "
            let outPool = poolOf codeGen.OutputName
            let head =
                [ ind indent + $"// flat elementwise: {cells} cells [{shapeNote}] (pool DFS order == nest order)"
                  ind indent + "{" ]
                @ [ ind (indent + 1) + $"{outElem}* BLADE_RESTRICT {outPool} = nested_array_utilities::pool_base({codeGen.OutputName}.data);" ]
                @ (operandDecls |> List.map (fun d -> ind (indent + 1) + d))
                @ [ ind (indent + 1) + $"const size_t __fp_cells = {cells}UL;" ]
            Some { Depth = depth; AllDense = allDense; Cells = cells; ShapeNote = shapeNote
                   OutPool = outPool; OutElem = outElem
                   OutDecl = $"{outElem}* BLADE_RESTRICT {outPool} = nested_array_utilities::pool_base({codeGen.OutputName}.data);"
                   OperandDecls = operandDecls; OperandPools = codeGen.InputArrayNames |> List.map poolOf
                   Body = body.CppExpr; AllParallel = allParallel; ThreadsOn = threadsOn
                   ProducerWrites = producerWrites; Pragma = pragma; PragmaMarker = pragmaMarker; Head = head }
        | Some _ -> None
    | _ -> None

/// The flat elementwise nest (above's plan, rendered): one loop over the
/// contiguous pool, or the planned rank-1 cell epilogue riding it.
let tryGenFlatElementwiseNest
        (streamed: Map<string, ProviderReadSpec>)
        (operandTypes: IRArrayType list)
        (codeGen: LoopNestCodeGen)
        (outerNames: Map<int, string>)
        (indent: int) : string list option =
    let ind n = String.replicate n "    "
    match flatElementwisePlanOf streamed operandTypes codeGen outerNames indent with
    | None -> None
    | Some fp ->
    let epilogue =
        match codeGen.FlatEpilogue with
        | Some ep when fp.Depth = 1 && fp.AllDense ->
            (match tryGenFlatEpilogueNest ep codeGen.OutputName fp.OutPool fp.OutElem fp.Body fp.Cells fp.AllParallel fp.ThreadsOn fp.ProducerWrites fp.PragmaMarker fp.Head fp.OperandPools indent with
             | Some ls -> ep.OnEmitted (); Some ls
             | None -> None)
        | _ -> None
    match epilogue with
    | Some lines -> Some lines
    | None ->
    if fp.AllParallel then
        recordOmpConstruct codeGen.OutputName fp.Pragma "flat elementwise sweep over the whole pool"
    Some (
        fp.Head
        @ fp.PragmaMarker
        @ (if fp.Pragma = "" then [] else [ ind (indent + 1) + fp.Pragma ])
        @ [ ind (indent + 1) + "for (size_t __fk = 0; __fk < __fp_cells; __fk++)"
            ind (indent + 2) + $"{fp.OutPool}[__fk] = {fp.Body};"
            ind indent + "}" ])

// --- Sibling maps (round 4 wave 2, PM04; Blade.Optimize.planSiblingMaps) -----
// The C++ realization of a planned group: every member's site emits its own
// prechecks, extents, allocation and registrations as usual; at the LOOP slot
// of genApplyCombinator, a member before the last stashes its final nest
// record and emits no loop, and the LAST member emits ONE sweep writing every
// member's cells. The whole group is PREFLIGHTED at its first member (every
// member's nest record built from its IR with a throwaway builder, every
// gate checked) before any loop is suppressed; a member whose actual record
// still fails at its own site (not expected: the gates are the preflight's)
// flushes the stashed loops individually right there, in member order,
// before its own -- the loops still only move DOWN, which the admission
// licenses -- and the record says so. A stash still open when its last
// member's arm returns is flushed after it the same way.

type internal SibStash =
    { SName: string; SCodeGen: LoopNestCodeGen; SNames: Map<int, string>
      SOperandTypes: IRArrayType list; SIndent: int }

type internal SibEmission =
    { Group: Blade.Optimize.SiblingMapGroup
      mutable Committed: bool
      mutable Stash: SibStash list
      mutable Completed: bool }

let internal siblingEmissionStorage =
    System.Threading.AsyncLocal<System.Collections.Generic.Dictionary<int, SibEmission>>()
let internal siblingEmissionCell () : System.Collections.Generic.Dictionary<int, SibEmission> =
    let v = siblingEmissionStorage.Value
    if isNull (box v) then
        let fresh = System.Collections.Generic.Dictionary<int, SibEmission>()
        siblingEmissionStorage.Value <- fresh
        fresh
    else v
/// The member being emitted, keyed by its C++ output name (the apply emitter
/// knows the name, not the let id): (group id, member position).
let internal siblingPendingStorage =
    System.Threading.AsyncLocal<System.Collections.Generic.Dictionary<string, int * int>>()
let internal siblingPendingCell () : System.Collections.Generic.Dictionary<string, int * int> =
    let v = siblingPendingStorage.Value
    if isNull (box v) then
        let fresh = System.Collections.Generic.Dictionary<string, int * int>()
        siblingPendingStorage.Value <- fresh
        fresh
    else v

let internal siblingSubject (g: Blade.Optimize.SiblingMapGroup) = $"""{g.Region}: {String.concat ", " g.Names}"""

/// The halo member's single level, when its record is exactly the planned
/// walk: one level of the member's extent, one window element starting at
/// the member's first centre, no fold / slab / Reynolds / shared-value mode.
let internal siblingHaloLevel (cg: LoopNestCodeGen) (start: int64) (extent: int64) : (LoopIndexBinding * ElementBinding) option =
    match cg.Bindings with
    | [ b ] when b.FusedRank.IsNone && b.BoundDependencies.IsEmpty && b.StrictOffset = 0
                 && b.Extent = IRLit (IRLitInt extent)
                 && cg.FoldWrapper.IsNone && cg.FoldChunk.IsNone && not cg.MpiSlab && not cg.HasReynolds
                 && cg.ShareDecl.IsNone && cg.RowEpilogue.IsNone && cg.CompactRowEpilogue.IsNone ->
        (match b.Elements with
         | [ e ] ->
             (match e.Virtual with
              | VirtualRange (Some (IRLit (IRLitInt s))) when s = start -> Some (b, e)
              | VirtualRange None when start = 0L -> Some (b, e)
              | _ -> None)
         | _ -> None)
    | _ -> None

/// Does this member's FINAL nest record admit the group's sweep?
let internal siblingMemberGate (g: Blade.Optimize.SiblingMapGroup) (j: int) (operandTypes: IRArrayType list)
                               (cg: LoopNestCodeGen) (names: Map<int, string>) (indent: int) : Result<unit, string> =
    if cg.RowEpilogue.IsSome || cg.CompactRowEpilogue.IsSome then Error "a row epilogue rides this member's nest"
    else
    match g.Kind with
    | Blade.Optimize.SibFlat cells ->
        if cg.FlatEpilogue.IsSome then Error "a cell epilogue rides this member's flat sweep"
        else
        match flatElementwisePlanOf Map.empty operandTypes cg names indent with
        | Some fp when fp.Cells = cells && fp.AllDense && not fp.ProducerWrites -> Ok ()
        | Some _ -> Error "the member's flat sweep differs from the group's (cells, storage or a writing kernel)"
        | None -> Error "the member does not take the flat elementwise path"
    | Blade.Optimize.SibHalo _ ->
        match siblingHaloLevel cg (List.item j g.Starts) (List.item j g.Extents) with
        | Some _ -> Ok ()
        | None -> Error "the member's nest is not the planned single-level halo walk"

/// One member's own loop, rendered as it would have been without the group
/// (the fallback flush): the flat sweep, else the nest.
let internal siblingSingleLoop (s: SibStash) : string list =
    match tryGenFlatElementwiseNest Map.empty s.SOperandTypes s.SCodeGen s.SNames s.SIndent with
    | Some lines -> lines
    | None -> genLoopNestStreamed Map.empty s.SCodeGen s.SNames s.SIndent

/// THE FUSED SWEEP over every member (stash, in member order).
let internal siblingFusedLoop (g: Blade.Optimize.SiblingMapGroup) (members: SibStash list) : string list =
    let ind n = String.replicate n "    "
    let indent = (List.last members).SIndent
    let k = members.Length
    match g.Kind with
    | Blade.Optimize.SibFlat cells ->
        let fps = members |> List.map (fun s -> (s, (flatElementwisePlanOf Map.empty s.SOperandTypes s.SCodeGen s.SNames s.SIndent).Value))
        let (_, fp0) = List.head fps
        let distinct (xs: string list) = xs |> List.distinct
        for (s, fp) in fps do
            if fp.AllParallel then
                recordOmpConstruct s.SName fp.Pragma $"flat elementwise sweep shared by {k} sibling outputs (one team)"
        [ ind indent + $"// sibling maps: {k} outputs in one flat sweep of {cells} cells [{fp0.ShapeNote}] (each cell its own kernel)"
          ind indent + "{" ]
        @ (fps |> List.map (fun (_, fp) -> ind (indent + 1) + fp.OutDecl))
        @ (fps |> List.collect (fun (_, fp) -> fp.OperandDecls) |> distinct |> List.map (fun d -> ind (indent + 1) + d))
        @ [ ind (indent + 1) + $"const size_t __fp_cells = {cells}UL;" ]
        @ (fps |> List.collect (fun (_, fp) -> fp.PragmaMarker) |> distinct)
        @ (if fp0.Pragma = "" then [] else [ ind (indent + 1) + fp0.Pragma ])
        @ [ ind (indent + 1) + "for (size_t __fk = 0; __fk < __fp_cells; __fk++) {" ]
        @ (fps |> List.map (fun (_, fp) -> ind (indent + 2) + $"{fp.OutPool}[__fk] = {fp.Body};"))
        @ [ ind (indent + 1) + "}"
            ind indent + "}" ]
    | Blade.Optimize.SibHalo (inner, baseN) ->
        // per member: its level, its first centre and extent, its block body
        let blocks =
            members |> List.mapi (fun j s ->
                let start = List.item j g.Starts
                let ext = List.item j g.Extents
                let (b, e) = (siblingHaloLevel s.SCodeGen start ext).Value
                let (code, _) = genElementBindingPeel false b e e.ArrayName
                let paramFinalNames = Map.ofList [ (e.ParamVarId, e.ParamName) ]
                let nameMap = paramFinalNames |> Map.fold (fun acc key v -> Map.add key v acc) s.SNames
                let nameMap = withCaptureNames EnclosingNamesWin s.SCodeGen.Captures nameMap
                let body = genKernelExprWithReynolds s.SCodeGen.KernelExpr s.SCodeGen.KernelParams false false nameMap paramFinalNames
                (start, start + ext, b, fun (d: int) ->
                    [ ind d + "{"
                      ind (d + 1) + $"const size_t {b.IndexName} = __sib_c - {start}UL;"
                      ind (d + 1) + code
                      ind (d + 1) + $"{s.SCodeGen.OutputName}[{b.IndexName}] = {body.CppExpr};"
                      ind d + "}" ]))
        let points = blocks |> List.collect (fun (a, z, _, _) -> [ a; z ]) |> List.distinct |> List.sort
        let segments = List.pairwise points |> List.filter (fun (a, z) -> z > a)
        let (_, _, b0, _) = List.head blocks
        let cg0 = (List.head members).SCodeGen
        let lo = blocks |> List.map (fun (a, _, _, _) -> a) |> List.max
        let hi = blocks |> List.map (fun (_, z, _, _) -> z) |> List.min
        // the pragma rides the segment where EVERY member is active (the
        // intersection); the fringes are a few cells each and stay serial
        let pragmaPrefix = genNestPragma cg0.Bindings (ind indent)
        for s in members do
            recordOmpConstruct s.SName pragmaPrefix $"sibling halo walk shared by {k} outputs, centres [{lo}, {hi}) (one team)"
        let marker =
            let reason = if not (ompThreadEmissionEnabled ()) then ompThreadsSuppressedReason () else "the omp(...) depth licenses no level of this nest"
            ompSuppressedMarker cg0.OmpRequested (pragmaPrefix <> "") reason (ind indent)
        let axis = if inner = "" then "anonymous" else inner
        [ ind indent + $"// sibling maps: {k} halo outputs in one walk over the {axis} axis ({baseN} cells), centres peeled into {segments.Length} segment(s) by member domain (each cell its own kernel)" ]
        @ (segments |> List.collect (fun (a, z) ->
            let active = blocks |> List.filter (fun (s0, z0, _, _) -> s0 <= a && z <= z0)
            if active.IsEmpty then []
            else
                let shared = active.Length = blocks.Length
                (if shared then marker else [])
                @ [ ind indent + (if shared then pragmaPrefix else "") + $"for (size_t __sib_c = {a}UL; __sib_c < {z}UL; __sib_c++) {{" ]
                @ (active |> List.collect (fun (_, _, _, render) -> render (indent + 1)))
                @ [ ind indent + "}" ]))
        |> fun ls -> ignore b0; ls

/// The hook at genApplyCombinator's loop slot.
type internal SiblingLoopDecision =
    | SibNormal
    | SibReplace of string list
    | SibPrefix of string list

let internal siblingLoopHook (name: string) (routeFree: bool) (streamed: Map<string, ProviderReadSpec>)
                             (operandTypes: IRArrayType list) (cg: LoopNestCodeGen) (names: Map<int, string>) (indent: int) : SiblingLoopDecision =
    match (siblingPendingCell ()).TryGetValue name with
    | false, _ -> SibNormal
    | true, (gid, j) ->
    match (siblingEmissionCell ()).TryGetValue gid with
    | true, st when st.Committed && not st.Completed ->
        let g = st.Group
        let subject = siblingSubject g
        let gate =
            if not routeFree then Error "the member's apply took a dispatched, sliced, tiled or segmented route"
            elif not (Map.isEmpty streamed) then Error "a streamed provider operand"
            elif ompTestModeEnabled () then Error "omp test mode instruments the single nests"
            else siblingMemberGate g j operandTypes cg names indent
        let me = { SName = name; SCodeGen = cg; SNames = names; SOperandTypes = operandTypes; SIndent = indent }
        match gate with
        | Error why ->
            st.Completed <- true
            Blade.Optimize.recordSiblingMaps subject
                (Blade.Effects.Declined $"C++ lane: {why} at member {j + 1} ({name}); the earlier members' loops run here, individually, before its own (they only move down)") []
            SibPrefix (st.Stash |> List.collect siblingSingleLoop)
        | Ok () when j < g.Members.Length - 1 ->
            st.Stash <- st.Stash @ [ me ]
            SibReplace [ String.replicate indent "    " + $"// sibling maps: {name}'s cells are written by the shared sweep at {List.last g.Names}" ]
        | Ok () ->
            st.Completed <- true
            let lines = siblingFusedLoop g (st.Stash @ [ me ])
            Blade.Optimize.recordSiblingMaps subject Blade.Effects.Applied
                [ (match g.Kind with
                   | Blade.Optimize.SibFlat _ -> $"C++ lane: one flat sweep writes {g.Members.Length} outputs"
                   | Blade.Optimize.SibHalo _ -> $"C++ lane: one halo walk writes {g.Members.Length} outputs, peeled by member domain")
                  "each member's prechecks, allocation and registrations stayed at its own site" ]
            SibReplace lines
    | _ -> SibNormal

/// The arms' entry (CodeGen's function-body arm, genComputeBinding's top-level
/// arm): at the group's FIRST member, preflight the whole group and commit or
/// decline it for this emission; mark the member pending for the apply hook.
let internal siblingArmBegin (id: IRId) (name: string) (preflight: Blade.Optimize.SiblingMapGroup -> Result<unit, string>) : unit =
    match Blade.Optimize.SiblingMapTable.tryMember id with
    | None -> ()
    | Some (g, j) ->
        let cell = siblingEmissionCell ()
        if j = 0 then
            let ok =
                match preflight g with
                | Ok () -> true
                | Error why ->
                    Blade.Optimize.recordSiblingMaps (siblingSubject g)
                        (Blade.Effects.Declined $"C++ lane: preflight -- {why}; every member keeps its own loop") []
                    false
            cell.[g.GroupId] <- { Group = g; Committed = ok; Stash = []; Completed = false }
        match cell.TryGetValue g.GroupId with
        | true, st when st.Committed && not st.Completed -> (siblingPendingCell ()).[name] <- (g.GroupId, j)
        | _ -> ()

/// The arms' exit: clears the pending mark; after the LAST member, a group
/// whose sweep was never emitted (its apply took another path) flushes the
/// stashed loops right after it.
let internal siblingArmEnd (id: IRId) (name: string) : string list =
    (siblingPendingCell ()).Remove name |> ignore
    match Blade.Optimize.SiblingMapTable.tryMember id with
    | Some (g, j) when j = g.Members.Length - 1 ->
        match (siblingEmissionCell ()).TryGetValue g.GroupId with
        | true, st when st.Committed && not st.Completed ->
            st.Completed <- true
            Blade.Optimize.recordSiblingMaps (siblingSubject g)
                (Blade.Effects.Declined $"C++ lane: the last member ({name}) never reached the shared sweep; the earlier members' loops run right after it, individually") []
            st.Stash |> List.collect siblingSingleLoop
        | _ -> []
    | _ -> []

/// THE PREFLIGHT at the group's first member: every member's nest record
/// rebuilt from its planned apply (a throwaway builder, placeholder operand
/// names) and checked against the member gate, the flat members' pragmas
/// equal -- before any loop is suppressed.
let internal siblingPreflight (g: Blade.Optimize.SiblingMapGroup) : Result<unit, string> =
    if ompTestModeEnabled () then Error "omp test mode instruments the single nests"
    else
    let built =
        g.Applies |> List.mapi (fun j info ->
            let names = info.Arrays |> List.mapi (fun i _ -> $"__sibpf{j}_{i}")
            let cg = routeKernelBodyThroughCall info (buildLoopNestCodeGen info names $"__sibpf{j}" (IRBuilder()))
            (j, info, cg))
    match built |> List.tryPick (fun (j, info, cg) ->
            match siblingMemberGate g j info.ArrayTypes cg Map.empty 0 with
            | Error why -> Some $"member {j + 1}: {why}"
            | Ok () -> None) with
    | Some why -> Error why
    | None ->
    match g.Kind with
    | Blade.Optimize.SibFlat _ ->
        let pragmas =
            built |> List.map (fun (_, info, cg) ->
                (flatElementwisePlanOf Map.empty info.ArrayTypes cg Map.empty 0) |> Option.map (fun fp -> fp.Pragma))
        if (pragmas |> List.distinct |> List.length) > 1 then Error "the members' flat sweeps take different pragmas"
        else Ok ()
    | Blade.Optimize.SibHalo _ ->
        // one team for the shared segment: every member's own nest must take
        // the SAME pragma (a member whose depth licenses no level stays serial)
        let pragmas = built |> List.map (fun (_, _, cg) -> genNestPragma cg.Bindings "")
        if (pragmas |> List.distinct |> List.length) > 1 then Error "the members' halo walks take different pragmas"
        else Ok ()

/// --- Row-fold jam (unroll-and-jam over the row axis) ------------------------
/// The partial fold `reduce(A, op[, init])` (TypeCheck's partialFold desugars it
/// to `method_for(A) <@> lambda(row) -> reduce(row, op[, init])`) and the row-map
/// contraction `method_for(A) <@> lambda(row) -> prodsum(row, x)` both reach the
/// nest emitter as ONE row level whose kernel is a fold IIFE -- one dependent
/// accumulator chain per row, while the row axis outside it is independent and
/// contributes nothing to instruction-level parallelism.
///
/// This emits R rows per tile with one NAMED accumulator each (never an array;
/// see the dense gram arm's measurement), all walking the fold axis together.
/// BITWISE by construction: every row keeps its own accumulator, its own seed
/// and its own ascending fold order -- the tile reinterleaves INDEPENDENT rows
/// and reassociates nothing, which is why it needs no licence (the dense gram
/// arm's argument, CodeGenExpr.fs `materializeGramForm`). Measured against the
/// emitted IIFE text at full mantissa, byte-identical output throughout:
///   row fold  1003x517 4.7x, 4001x2003 3.3x, 20011x61 1.5x
///   matvec    1003x517 4.2x, 4001x2003 3.2x, 20011x61 2.3x
///
/// Gate (None = the ordinary nest, which is always correct): a single serial,
/// unlicensed, non-streamed, non-Reynolds row level over rank-2 plain dense
/// real-scalar operands, a rank-1 dense output, and a kernel that is EXACTLY
/// `reduce(row, <builtin op>[, init])` or `prodsum(...)` over row params and
/// invariant NAMED rank-1 arrays. A recognised builtin op keeps the jam from
/// interleaving any user code (whose panics' order would become observable);
/// an omp request leaves the nest to the path that places (or marks) its pragma.
///
/// IN EVERY BUILD, including the 2026-10-17 fast default. This gate used to
/// decline under BLADE_FP_REASSOC (leaving each row to its per-row lane/simd
/// fold). When that knob's forms became the default the choice was MEASURED
/// again (12 row folds per program, OMP_NUM_THREADS=1, medians of 9-15
/// interleaved runs): at 2003 x 1009 (bandwidth-bound) the jam beat the
/// per-row forms 1.10x (`reduce`) / 1.30x (`prodsum`); at 403 x 251
/// (cache-resident) the per-row forms won 1.31x / 1.09x. No winner, so the
/// jam -- the shape's old default, and BITWISE, so legal in the repro build
/// too -- stays the default (plan-fp-default-fastest.md section 6.2).
let tryGenRowFoldJamNest
        (streamed: Map<string, ProviderReadSpec>)
        (operandTypes: IRArrayType list)
        (codeGen: LoopNestCodeGen)
        (outerNames: Map<int, string>)
        (indent: int) : string list option =
    let ind n = String.replicate n "    "
    let isRealScalar (t: IRType) =
        match stripUnits t with
        | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32) -> true
        | _ -> false
    let isPlainDense (ix: IRIndexType) =
        ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 && ix.Dependencies.IsEmpty
    if codeGen.FoldWrapper.IsSome || codeGen.FoldChunk.IsSome || codeGen.ShareDecl.IsSome then None
    elif codeGen.MpiSlab || codeGen.HasReynolds || codeGen.IsAntisymmetric then None
    elif not (Map.isEmpty streamed) || ompTestModeEnabled () then None
    elif codeGen.OmpRequested then None
    elif List.length operandTypes <> List.length codeGen.InputArrayNames then None
    else
    match codeGen.Bindings, codeGen.OutputType with
    | [ lvl ], ArrayElem outTy
        when not lvl.IsParallel && lvl.FusedRank.IsNone && lvl.BoundDependencies.IsEmpty
             && lvl.StrictOffset = 0
             && (match lvl.Extent with IRCompoundMask _ | IRSparseKeys _ -> false | _ -> true)
             && arrayRank outTy = 1 && outTy.IndexTypes.Length = 1
             && isPlainDense outTy.IndexTypes.Head
             && not (isCompoundArrayType outTy) && not (isSparseArrayType outTy)
             && isRealScalar outTy.ElemType ->
        // Every element is a whole-row peel of a rank-2 plain dense operand.
        let rowOperand (e: ElementBinding) : (ElementBinding * IRArrayType) option =
            match List.tryItem e.ArrayPosition operandTypes with
            | Some at when e.Virtual.IsRealArray && e.RankComponent = 0 && e.ArrayRank = 2
                           && (match e.SlotTag with Some t -> not (t.StartsWith "__halowin") | None -> true)
                           && at.IndexTypes.Length = 2
                           && at.IndexTypes |> List.forall isPlainDense
                           && isRealScalar at.ElemType
                           && e.ArrayName = (codeGen.InputArrayNames |> List.item e.ArrayPosition) ->
                Some (e, at)
            | _ -> None
        let rows = lvl.Elements |> List.map rowOperand
        if rows.IsEmpty || rows |> List.exists Option.isNone then None
        else
        let rows = rows |> List.map Option.get
        let rowOf (id: IRId) = rows |> List.tryFind (fun (e, _) -> e.ParamVarId = id)
        let rec mentionsRow (e: IRExpr) =
            (match e with IRVar (id, _) -> (rowOf id).IsSome | _ -> false)
            || (childrenOf e |> List.exists mentionsRow)
        // Kernel-scope names for the INVARIANT pieces (init, prodsum operands),
        // with the precedence rule every nest emitter uses: the enclosing
        // scope's emitted names first, captures only fill gaps.
        let nameMap =
            withCaptureNames EnclosingNamesWin codeGen.Captures outerNames
        // A fold operand is either a row param or an invariant NAMED rank-1
        // plain dense array (hoisted once to a restrict pointer; a computed
        // operand could be a temporary, which a hoisted `&x[0]` would outlive).
        let operandOf (a: IRExpr) : Choice<ElementBinding * IRArrayType, string * IRArrayType> option =
            match a with
            | IRVar (id, _) when (rowOf id).IsSome -> Some (Choice1Of2 (rowOf id).Value)
            | (IRVar _ | IRParam _) when not (mentionsRow a) ->
                (match inferExprType a with
                 | ArrayElem at when at.IndexTypes.Length = 1 && isPlainDense at.IndexTypes.Head
                                     && not (isRaggedRowType at) && isRealScalar at.ElemType ->
                    Some (Choice2Of2 (exprToCppCore emptySubst nameMap a, at))
                 | _ -> None)
            | _ -> None
        // (elem type, seed per row, first fold index, per-row fold statement,
        //  prelude, needs the empty-row panic, head operand)
        let fold : (IRType * (int -> string) * string * (int -> string -> string) * string list * bool * Choice<ElementBinding * IRArrayType, string * IRArrayType>) option =
            match codeGen.KernelExpr with
            | IRReduce (IRVar (rid, _), kernelExpr, initOpt) when (rowOf rid).IsSome ->
                let (re, rat) = (rowOf rid).Value
                (match resolveCallable kernelExpr with
                 | Some callable when callable.Params.Length = 2 && (foldKernelBuiltinOp callable).IsSome ->
                    if (match initOpt with Some ie -> mentionsRow ie | None -> false) then None
                    else
                    let (wrapperCode, wname) = genCallableWrapper nameMap "" callable
                    let seed, start =
                        match initOpt with
                        | Some ie -> (fun (_: int) -> exprToCppCore emptySubst nameMap ie), "0"
                        | None -> (fun k -> $"__jr{re.ArrayPosition}_{k}[0]"), "1"
                    let step k (acc: string) = $"{acc} = {wname}({acc}, __jr{re.ArrayPosition}_{k}[__jk]);"
                    Some (rat.ElemType, seed, start, step, [ String.concat " " wrapperCode ],
                          initOpt.IsNone, Choice1Of2 (re, rat))
                 | _ -> None)
            | IRProdSum args when not args.IsEmpty ->
                let ops = args |> List.map operandOf
                if ops |> List.exists Option.isNone then None
                elif not (ops |> List.exists (function Some (Choice1Of2 _) -> true | _ -> false)) then None
                else
                let ops = ops |> List.map Option.get
                let head = List.head ops
                let elemTy = match head with Choice1Of2 (_, at) -> at.ElemType | Choice2Of2 (_, at) -> at.ElemType
                let invIdx = ops |> List.choose (function Choice2Of2 (n, _) -> Some n | _ -> None) |> List.distinct
                let spell k (o: Choice<ElementBinding * IRArrayType, string * IRArrayType>) =
                    match o with
                    | Choice1Of2 (e, _) -> $"__jr{e.ArrayPosition}_{k}[__jk]"
                    | Choice2Of2 (n, _) -> $"__jx{(List.findIndex ((=) n) invIdx)}[__jk]"
                // Same operand order as the IIFE's product (left-associated
                // `a * b * c` rounds in that order), same `+=` into a zero seed.
                let step k (acc: string) = $"""{acc} += {(ops |> List.map (spell k) |> String.concat " * ")};"""
                let prelude =
                    invIdx |> List.mapi (fun i n ->
                        let at = ops |> List.pick (function Choice2Of2 (m, at) when m = n -> Some at | _ -> None)
                        $"const {(elemTypeToCpp at.ElemType)}* BLADE_RESTRICT __jx{i} = &{n}[0];")
                Some (elemTy, (fun _ -> "0"), "0", step, prelude, false, head)
            | _ -> None
        match fold with
        | None -> None
        | Some (elemTy, seed, start, step, prelude, needsEmptyGuard, head) ->
        let elemStr = elemTypeToCpp elemTy
        // The fold length: the head operand's extent, the rule the IIFE uses
        // (a row's extent is its operand's trailing axis, literal when known).
        let foldLen =
            match head with
            | Choice1Of2 (e, at) -> literalOrRuntimeExtentOfArray at e.ArrayName 1
            | Choice2Of2 (n, at) -> literalOrRuntimeExtentOfArray at n 0
        let rowBound = genLoopBoundExpr (compoundArrayNamesOf codeGen.Bindings) lvl
        let usedPositions =
            let rec collect (e: IRExpr) =
                (match e with
                 | IRVar (id, _) -> (match rowOf id with Some (r, _) -> [ r.ArrayPosition ] | None -> [])
                 | _ -> [])
                @ (childrenOf e |> List.collect collect)
            collect codeGen.KernelExpr |> List.distinct |> List.sort
        let rowArrayName pos = rows |> List.pick (fun (e, _) -> if e.ArrayPosition = pos then Some e.ArrayName else None)
        // Tile width. Registers hold R accumulators plus R row pointers per
        // row stream, so a second row stream (`zip(A, B)` rows) halves the
        // ceiling. A static row count takes the gram jam's divisor rule so no
        // remainder runs at base speed: R = rows when it fits, else the
        // largest divisor in [4..maxR]; otherwise a fixed width.
        let maxR = if List.length usedPositions <= 1 then 8 else 4
        let fixedR = if List.length usedPositions <= 1 then 6 else 4
        let jamR =
            match lvl.Extent with
            | IRLit (IRLitInt m) when m < 2L -> 1
            | IRLit (IRLitInt m) when m <= int64 maxR -> int m
            | IRLit (IRLitInt m) ->
                (match [ maxR .. -1 .. 4 ] |> List.tryFind (fun d -> m % int64 d = 0L) with
                 | Some d -> d
                 | None -> fixedR)
            | _ -> fixedR
        if jamR < 2 then None
        else
        let iv = lvl.IndexName
        let out = codeGen.OutputName
        let rowDecls (k: int) (rowExpr: string) =
            usedPositions |> List.map (fun p ->
                let at = rows |> List.pick (fun (e, at) -> if e.ArrayPosition = p then Some at else None)
                $"const {(elemTypeToCpp at.ElemType)}* BLADE_RESTRICT __jr{p}_{k} = &{(rowArrayName p)}[{rowExpr}][0];")
        let guard =
            match needsEmptyGuard, head with
            | false, _ -> []
            | _, Choice1Of2 (_, at) when (match at.IndexTypes.[1].Extent with IRLit (IRLitInt n) -> n > 0L | _ -> false) -> []
            | _ ->
                // The IIFE's empty-row panic, raised where it would have been:
                // at the first row, before any row is folded.
                [ $$"""if (__jm > 0 && __jn == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", {{declPanicArgs ()}}); }""" ]
        let tile =
            [ $$"""for (; {{iv}} + {{jamR}} <= __jm; {{iv}} += {{jamR}}) {""" ]
            @ ([ 0 .. jamR - 1 ] |> List.collect (fun k -> rowDecls k $"{iv} + {k}") |> List.map (fun s -> "    " + s))
            @ ([ 0 .. jamR - 1 ] |> List.map (fun k -> $"    {elemStr} __ja{k} = {seed k};"))
            @ [ $$"""    for (size_t __jk = {{start}}; __jk < __jn; __jk++) {""" ]
            @ ([ 0 .. jamR - 1 ] |> List.map (fun k -> "        " + step k $"__ja{k}"))
            @ [ "    }" ]
            @ ([ 0 .. jamR - 1 ] |> List.map (fun k -> $"    {out}[{iv} + {k}] = __ja{k};"))
            @ [ "}" ]
        let remainder =
            [ $$"""for (; {{iv}} < __jm; {{iv}}++) {""" ]
            @ (rowDecls 0 iv |> List.map (fun s -> "    " + s))
            @ [ $"    {elemStr} __ja0 = {seed 0};"
                $$"""    for (size_t __jk = {{start}}; __jk < __jn; __jk++) {"""
                "        " + step 0 "__ja0"
                "    }"
                $"    {out}[{iv}] = __ja0;"
                "}" ]
        let kind = match codeGen.KernelExpr with IRReduce _ -> "reduce" | _ -> "prodsum"
        recordCodegenDecision "microkernel" codeGen.OutputName Blade.Effects.Applied
            [ (if kind = "reduce" then "row-fold jam (partial reduce over rows)" else "matvec jam (row-map prodsum)")
              $"{jamR} rows per tile, one accumulator per row (bitwise)" ]
        recordFoldForm codeGen.OutputName $"row-fold jam({jamR} rows, bitwise)" $"{kind} over the rows of a row map"
        Some (
            [ ind indent + $"// row-fold jam: {kind} over rows, {jamR} rows per tile, one accumulator per row (bitwise: each row keeps its own ascending fold)"
              ind indent + "{" ]
            @ ([ $"const size_t __jm = {rowBound};"; $"const size_t __jn = {foldLen};" ]
               @ guard @ prelude @ [ $"size_t {iv} = 0;" ] @ tile @ remainder
               |> List.map (fun s -> ind (indent + 1) + s))
            @ [ ind indent + "}" ])
    | _ -> None


/// L2 dispatch at the apply-combinator site. On a `LinAlgPatterns.(|BlasL2|_|)`
/// match AND the BLAS gate on (`shimEntryPoint`), emits ONE `blade_linalg::`
/// call in place of the whole per-row nest; a decline returns None and the
/// chain in `tryGenLinAlgNest` carries on.
///
/// CodeGen decides NOTHING about the shape: the pattern owns which nests are
/// gemv, this only turns the descriptor into text (resolving the vector
/// operand's name through the kernel body's map, computing extents the same
/// way the nest's own bounds are computed).
let internal tryGenGemvDispatch
        (streamed: Map<string, ProviderReadSpec>)
        (operandTypes: IRArrayType list)
        (codeGen: LoopNestCodeGen)
        (outerNames: Map<int, string>)
        (indent: int) : string list option =
    match (Map.count streamed, codeGen.OmpRequested, operandTypes, codeGen) with
    | Blade.LinAlgPatterns.BlasL2 call ->
        let entryOpt = Blade.LinAlgPatterns.shimEntryPoint Blade.LinAlgPatterns.HostBlas call
        Blade.LinAlgPatterns.recordRoute (decisionSpan ()) codeGen.OutputName call
            (entryOpt |> Option.map (fun e -> (Blade.LinAlgPatterns.HostBlas, e)))
        match entryOpt with
        | None -> None
        | Some entry ->
            // Name map: enclosing scope first, captures filling only ids it does
            // not know -- the same precedence rule genLoopNestStreamed and the
            // flat path use, so the vector resolves to the identifier the
            // kernel body would have rendered.
            let nameMap =
                withCaptureNames EnclosingNamesWin codeGen.Captures outerNames
            let resolve src =
                match src with
                | Blade.LinAlgPatterns.FromNestArray n
                | Blade.LinAlgPatterns.FromNestOutput n -> Some n
                | Blade.LinAlgPatterns.FromKernelRef (IRVar (id, _)) -> Map.tryFind id nameMap
                | Blade.LinAlgPatterns.FromKernelRef _ -> None
            let named =
                call.NestOperands |> List.map (fun (role, src) -> (role, resolve src))
            let pick role =
                named |> List.tryPick (fun (r, n) -> if r = role then n else None)
            match pick Blade.LinAlgPatterns.RoleA,
                  pick Blade.LinAlgPatterns.RoleB,
                  pick Blade.LinAlgPatterns.RoleC with
            | Some aName, Some xName, Some yName ->
                // m = the row loop's own bound (literal after shape
                // monomorphization, else the runtime extent read) -- byte-for-
                // byte what the nest would have emitted.
                let mExtent =
                    genLoopBoundExpr (compoundArrayNamesOf codeGen.Bindings)
                                     (List.head codeGen.Bindings)
                // n = A's TRAILING extent: the peel gives the row
                // `A.extents + 1`, and prodsum bounds itself by that row's
                // extents[0]. Rendered from the operand TYPE when literal,
                // matching how the nest renders its own bounds -- through the
                // shared rule, which is where this site's own copy of it went.
                let nExtent =
                    match operandTypes with
                    | [ aTy ] -> literalOrRuntimeExtentOfArray aTy aName 1
                    | _ -> $"{aName}.extents[1]"
                // Pool capacity for the shim's contiguity probe.
                // `blade_gemv` stages A through an `in_view`, exactly as the L3
                // adapters do, so it needs the same bound -- the pattern above
                // already proved A dense rank-2, and this states the cell count
                // through the one shared derivation rather than restating it.
                let aCells =
                    match operandTypes with
                    | [ aTy ] -> denseCellCountOfArray aTy aName
                    | _ -> "0"
                (linalgUsedCell ()).Value <- true
                // BLOCK comment, not `//`: an inline-form materialization
                // space-joins its lines into a single-line IIFE, where a line
                // comment would swallow the rest of the statement.
                Some [ String.replicate indent "    "
                       + $"/* linalg dispatch: gemv y = {aName} * {xName} (per-row prodsum fiber) */ {entry}({mExtent}, {nExtent}, {aName}.data, {aCells}, {xName}.data, {yName}.data);" ]
            | _ -> None
    | _ -> None


/// L3 dispatch at the apply-combinator site: the comm-licensed
/// packed-covariance nest becomes ONE `blade_pgemm::dsyrk_tri` call (the
/// packed-triangle 6 x 8 register tile, per cell bitwise the serial prodsum
/// under -ffp-contract=off) when the literal extents clear `packedTriWorth`
/// and BLADE_SYRK_TILE is not off -- with the BLAS gate on or off
/// (`syrkTilePreferredOverBlas`) -- else, with the gate on, ONE
/// `blade_gram_same_*` (`?syrk`) call; otherwise None and the ordinary nest.
///
/// The twin of `tryGenGemvDispatch` one level up, and it turns the SAME
/// descriptor kind into text -- `LinAlgPatterns.(|BlasL3|_|)` owns which nests
/// are syrk, this owns nothing but the spelling.
///
/// C NEEDS NO STAGING AND NO CAPACITY ARGUMENT. `blade_gram_same_*` writes
/// `Crows[i][jr]` with `jr < m - i`, which is Blade's packed upper-triangular
/// row footprint exactly, so the freshly-allocated output pool is handed over
/// as it stands (`blade_linalg.hpp` records the layout proof). Only A, which
/// the shim stages through an `in_view`, carries the pool capacity the
/// contiguity probe cannot derive from a row skeleton.
///
/// THE KERNEL'S SCALAR (`prodsum(x, y) / N`) is applied AFTER the call, over
/// the same `[i][jr]` footprint the shim just wrote -- not folded into syrk's
/// `alpha`. `alpha = 1/N` would round a reciprocal and then multiply; the nest
/// this replaces divides the finished sum, and dividing the finished triangle
/// reproduces that operation for operation.
let internal tryGenSyrkDispatch
        (streamed: Map<string, ProviderReadSpec>)
        (operandTypes: IRArrayType list)
        (codeGen: LoopNestCodeGen)
        (outerNames: Map<int, string>)
        (indent: int) : string list option =
    match (Map.count streamed, codeGen.OmpRequested, operandTypes, codeGen) with
    | Blade.LinAlgPatterns.BlasL3 call ->
        let libraryEntry = Blade.LinAlgPatterns.shimEntryPoint Blade.LinAlgPatterns.HostBlas call
        // BLAS-ON PRECEDENCE (bench gap G28; BLADE_BLAS_PREFER): at a shape
        // the packed tile takes anyway, `LinAlgPatterns.blasPreference` picks
        // the side -- the tile under `native` (the default), the library under
        // `library`. The literal extents are read the same way the arms below
        // read them (the outer level's bound; A's trailing extent from its
        // TYPE -- a literal needs no operand name). BlasL3 admits real f64
        // only, so no precision test is needed here.
        let (eligibleShape, shapeTxt) =
            match libraryEntry, codeGen.Bindings, operandTypes with
            | Some _, b0 :: _, aTy :: _ ->
                let p = genLoopBoundExpr (compoundArrayNamesOf codeGen.Bindings) b0
                let k = literalOrRuntimeExtentOfArray aTy "" 1
                (syrkTileEligibleShape p k, $"P={p} K={k}")
            | _ -> (false, "")
        let pref = Blade.LinAlgPatterns.blasPreference ()
        let tilePreempts = eligibleShape && pref.Prefer = Blade.LinAlgPatterns.PreferNative
        let entryOpt = if tilePreempts then None else libraryEntry
        if tilePreempts then
            Blade.LinAlgPatterns.recordTilePreferred (decisionSpan ()) codeGen.OutputName call libraryEntry.Value shapeTxt pref
        elif eligibleShape then
            Blade.LinAlgPatterns.recordLibraryPreferred (decisionSpan ()) codeGen.OutputName call libraryEntry.Value shapeTxt pref
        else
            Blade.LinAlgPatterns.recordRoute (decisionSpan ()) codeGen.OutputName call
                (entryOpt |> Option.map (fun e -> (Blade.LinAlgPatterns.HostBlas, e)))
        // Gate on or off, the operands, extents and scale tail are derived the
        // same way; only the final call differs (the BLAS shim, or the
        // packed-triangle tile below).
        match () with
        | () ->
            let aName =
                call.NestOperands
                |> List.tryPick (fun (r, src) ->
                    match r, src with
                    | Blade.LinAlgPatterns.RoleA, Blade.LinAlgPatterns.FromNestArray n -> Some n
                    | _ -> None)
            let cName =
                call.NestOperands
                |> List.tryPick (fun (r, src) ->
                    match r, src with
                    | Blade.LinAlgPatterns.RoleC, Blade.LinAlgPatterns.FromNestOutput n -> Some n
                    | _ -> None)
            match aName, cName with
            | Some aName, Some cName ->
                // m = the outer level's own bound (literal after shape
                // monomorphization, else the runtime extent read) -- byte-for-
                // byte what the nest would have emitted. The inner level is the
                // triangle over the SAME m, which the pattern proved.
                let mExtent =
                    genLoopBoundExpr (compoundArrayNamesOf codeGen.Bindings)
                                     (List.head codeGen.Bindings)
                // n = A's TRAILING extent -- the contracted axis. Same rule as
                // gemv: rendered from the operand TYPE when literal.
                let nExtent =
                    match operandTypes with
                    | aTy :: _ -> literalOrRuntimeExtentOfArray aTy aName 1
                    | _ -> $"{aName}.extents[1]"
                let aCells =
                    match operandTypes with
                    | aTy :: _ -> denseCellCountOfArray aTy aName
                    | _ -> "0"
                // The scale, re-derived from the kernel body through the
                // pattern module's OWN peel so the two cannot disagree about
                // what a scaled contraction is.
                let nameMap =
                    withCaptureNames EnclosingNamesWin codeGen.Captures outerNames
                let renderScalar (e: IRExpr) : string option =
                    match e with
                    | IRVar (id, _) when not (Map.containsKey id nameMap) -> None
                    | _ ->
                        let r = genKernelExprWithReynolds e codeGen.KernelParams
                                                          false false nameMap Map.empty
                        Some r.CppExpr
                // Three answers, hence the nested option: `None` = DECLINE the
                // whole dispatch (a scale this site cannot spell), `Some None`
                // = no scale to apply, `Some (Some (kind, text))` = the scale:
                // kind 1 = divide, 2 = multiply (blade_tiles.h's
                // BLADE_TILE_SCALE_* codes), text = the scalar's C++.
                let scaleArgs : (int * string) option option =
                    match codeGen.KernelExpr with
                    | Blade.LinAlgPatterns.ProdSumScaled (_, None) -> Some None
                    | Blade.LinAlgPatterns.ProdSumScaled (_, Some sc) ->
                        let (kind, sExpr) =
                            match sc with
                            | Blade.LinAlgPatterns.ScaleDiv d -> (1, d)
                            | Blade.LinAlgPatterns.ScaleMul s -> (2, s)
                        match renderScalar sExpr with
                        | None -> None                       // unresolvable -> decline
                        | Some sTxt -> Some (Some (kind, sTxt))
                    | _ -> None
                match scaleArgs with
                | None -> None
                | Some scale ->
                // The BLAS route's scaling pass (the shim has no tail): the
                // kernel's own scalar over the same packed triangle, after.
                let tail =
                    scale |> Option.map (fun (kind, sTxt) ->
                        let opStr = if kind = 1 then "/=" else "*="
                        String.replicate indent "    "
                        + $"/* ... then the kernel's own scalar, over the same packed triangle */ for (size_t __sy_i = 0; __sy_i < {mExtent}; __sy_i++) for (size_t __sy_j = 0; __sy_j < {mExtent} - __sy_i; __sy_j++) {cName}[__sy_i][__sy_j] {opStr} ({sTxt});")
                match entryOpt with
                | Some entry ->
                    (linalgUsedCell ()).Value <- true
                    // The shim's REPACK (dense staging -> packed triangle,
                    // blade_linalg.hpp blade_repack_upper) runs inside an
                    // OpenMP team exactly when the source licensed the nest
                    // (`omp` on the kernel) AND this build emits thread-level
                    // OpenMP -- the same rule the packed tile driver follows.
                    // The syrk itself is the library's (its own threads); the
                    // census line names the repack as this site's construct.
                    // Bench gap G28: the serial staging + repack was the whole
                    // 18-21% loss under 8 library threads.
                    let threadedArg =
                        if codeGen.OmpRequested && ompThreadEmissionEnabled () then "true" else "false"
                    let ompCensus =
                        if not codeGen.OmpRequested then ""
                        elif ompThreadEmissionEnabled () then
                            recordOmpConstruct cName "omp parallel for inside the BLAS shim's repack"
                                "blade_repack_upper: the dense staging's upper triangle copied into the packed rows, rows split across the team (the syrk itself runs on the library's threads)"
                            ""
                        else $"/* {(ompSuppressedPhrase (ompThreadsSuppressedReason ()))} */ "
                    // BLOCK comment, not `//`: an inline-form materialization
                    // space-joins its lines into a single-line IIFE, where a
                    // line comment would swallow the rest of the statement.
                    Some ([ String.replicate indent "    "
                            + ompCensus
                            + $"/* linalg dispatch: syrk C = A * A^T (packed upper, from comm nest) */ {entry}({mExtent}, {nExtent}, {aName}.data, {aCells}, {cName}.data, {threadedArg});" ]
                          @ Option.toList tail)
                | None ->
                // BLAS GATE OFF, OR ON WITH THE TILE PREFERRED (`tilePreempts`
                // above): the packed-triangle SYRK tile
                // (blade_packed_gemm.hpp dsyrk_tri over blade_tiles.h's 6 x 8
                // core), or -- below the crossover, on non-literal extents, or
                // under BLADE_SYRK_TILE=0 -- the ordinary nest (None).
                //
                // NO LICENCE NEEDED, so this arm is the same in the default and
                // the BLADE_FP_REPRO=1 build: every cell is still ONE chain,
                // +0.0 then `acc += X[i][s] * X[j][s]` for s ascending, the
                // serial prodsum's operation sequence. K blocks store and
                // reload the exact partial; the tile is vector extensions, so
                // contraction follows -ffp-contract as the scalar loop's does.
                // Bitwise the serial prodsum under =off (the differential
                // gates' mode), the one-fma-per-step chain under =fast --
                // cpp/syrk_tile_tests.cpp (`blade test syrk-tile`). The
                // default build's per-cell dot was a REASSOCIATED simd
                // reduction, so the tile changes the default's bits by ulps
                // (toward the serial chain) and the repro build's not at all
                // under =off.
                //
                // The kernel's scalar tail, if any, is the SAME operation the
                // BLAS route's post-pass applies (divide the finished sum) --
                // applied by the LAST K block's tile as it stores, so the
                // triangle is written once and never re-read for it.
                let lit (s: string) = match System.Int64.TryParse s with | true, v -> Some v | _ -> None
                let declineTile (why: string) (shape: string) =
                    recordCodegenDecision "microkernel" cName
                        (Blade.Effects.Declined why) [ "packed syrk tile"; shape ]
                    None
                if not (syrkTileEnabled ()) then
                    declineTile "BLADE_SYRK_TILE=0/off: the plain per-cell nest (A/B knob)" ""
                else
                match lit mExtent, lit nExtent with
                | Some p, Some k when packedTriWorth p k ->
                    (packedGemmUsedCell ()).Value <- true
                    let kc = syrkTileKc k
                    let g = syrkRowGroup ()
                    let edge = triTileEdge ()
                    // Threads only where the source licenses them (`omp` on
                    // the kernel) AND the build emits thread-level OpenMP;
                    // cells are split across the team, sums never are. The
                    // omp census: the team is the DRIVER's (row groups under
                    // schedule(dynamic)), recorded as applied; a licence the
                    // build knob suppresses gets the one census phrase.
                    let threadedArg =
                        if codeGen.OmpRequested && ompThreadEmissionEnabled () then "true" else "false"
                    let census =
                        if not codeGen.OmpRequested then ""
                        elif ompThreadEmissionEnabled () then
                            recordOmpConstruct cName "omp parallel num_threads(T) inside the tile driver"
                                "dsyrk_tri row groups: work units under schedule(dynamic), cells split across the team, sums never"
                            ""
                        else $"/* {(ompSuppressedPhrase (ompThreadsSuppressedReason ()))} */ "
                    let (sk, scTxt, scaleNote) =
                        match scale with
                        | None -> (0, "0.0", "")
                        | Some (1, t) -> (1, t, ", then / d per cell (fused into the last block's store)")
                        | Some (_, t) -> (2, t, ", then * s per cell (fused into the last block's store)")
                    recordCodegenDecision "microkernel" cName Blade.Effects.Applied
                        [ "packed syrk tile (blade_pgemm::dsyrk_tri, 6x8 register tile over the packed triangle)"
                          $"P={p} K={k} KC={kc} G={g}" ]
                    recordFoldForm cName $"syrk tile 6x8, KC={kc}, row groups of {g}{triEdgeNote edge}{scaleNote}"
                        "same-array comm row contraction (one ascending chain per cell; no reorder licence used)"
                    Some [ String.replicate indent "    "
                           + census
                           + $"/* comm nest: packed-triangle syrk tile (blade_packed_gemm.hpp dsyrk_tri, 6x8, KC={kc}), row groups of {g}, per cell the serial prodsum */ blade_pgemm::dsyrk_tri((size_t)({mExtent}), (size_t)({nExtent}), {aName}.data, {cName}.data, 0, {threadedArg}, (size_t){syrkTileKcMax}, {sk}, (double)({scTxt}), (size_t){g}, {edge});" ]
                | Some p, Some k ->
                    declineTile "below the syrk-tile crossover (p >= 16, k >= 4): the per-cell nest wins" $"P={p} K={k}"
                | _ ->
                    declineTile "non-literal extents: the per-cell nest" "non-literal extents"
            | _ -> None
    | _ -> None


/// The antisym pair and the rank-3 simplex (`LinAlgPatterns.(|RowContraction|_|)`):
/// the comm / anticomm nest becomes ONE call of Blade's own packed tile
/// (`blade_pgemm::dantisym_tri` -- review item M7 -- or `blade_pgemm::dsym3_tri`
/// -- M8; cores in blade_tiles.h) when the literal extents clear
/// `packedAntiWorth` / `packedSym3Worth` and the knob (BLADE_ANTISYM_TILE /
/// BLADE_SIMPLEX_TILE) is not off; otherwise None and the ordinary nest.
///
/// NO LICENCE NEEDED, so this arm is the same in the default and the
/// BLADE_FP_REPRO=1 build, and the BLAS gate does not enter: every cell keeps
/// its operation sequence (antisym: two ascending chains -- P in factor order
/// X(i) * Y(j), Q in X(j) * Y(i) -- then ONE subtraction; simplex: one
/// ascending chain of `(a*b)*c` terms with the first product formed once per
/// (i, j) prefix, the W panel), then the kernel's own scalar ONCE on the
/// finished value. Bitwise the serial per-cell form under -ffp-contract=off
/// (the differential gates' mode), the one-fma-per-step chain under =fast --
/// cpp/row_contraction_tile_tests.cpp (`blade test row-tile`). The default
/// build's per-cell dots were REASSOCIATED simd reductions, so the tiles move
/// the default's bits by ulps (toward the serial chain) and the repro build's
/// not at all under =off.
let internal tryGenRowContractionTile
        (streamed: Map<string, ProviderReadSpec>)
        (operandTypes: IRArrayType list)
        (codeGen: LoopNestCodeGen)
        (outerNames: Map<int, string>)
        (indent: int) : string list option =
    match (Map.count streamed, operandTypes, codeGen) with
    | Blade.LinAlgPatterns.RowContraction rc ->
        let aName = rc.ArrayName
        let cName = rc.OutputName
        let aTy = List.head operandTypes
        let mExtent =
            genLoopBoundExpr (compoundArrayNamesOf codeGen.Bindings) (List.head codeGen.Bindings)
        let (tileName, sampleDim) =
            match rc.Shape with
            | Blade.LinAlgPatterns.AntiPair _ -> ("antisym tile", 2)
            | Blade.LinAlgPatterns.Sym3 -> ("simplex tile", 1)
        let nExtent = literalOrRuntimeExtentOfArray aTy aName sampleDim
        let declineTile (why: string) (shape: string) =
            recordCodegenDecision "microkernel" cName (Blade.Effects.Declined why) [ tileName; shape ]
            None
        let nameMap = withCaptureNames EnclosingNamesWin codeGen.Captures outerNames
        let renderScalar (e: IRExpr) : string option =
            match e with
            | IRVar (id, _) | IRUnaryOp (_, IRVar (id, _)) when not (Map.containsKey id nameMap) -> None
            | _ ->
                let r = genKernelExprWithReynolds e codeGen.KernelParams false false nameMap Map.empty
                Some r.CppExpr
        // (kind, scalar text): 0 none, 1 divide, 2 multiply -- the header's
        // BLADE_TILE_SCALE_* codes. None = a scale this site cannot spell.
        let scaleArgs : (int * string) option =
            match rc.Scale with
            | None -> Some (0, "0.0")
            | Some (Blade.LinAlgPatterns.ScaleDiv d) -> renderScalar d |> Option.map (fun t -> (1, t))
            | Some (Blade.LinAlgPatterns.ScaleMul s) -> renderScalar s |> Option.map (fun t -> (2, t))
        let lit (s: string) = match System.Int64.TryParse s with | true, v -> Some v | _ -> None
        let threaded = codeGen.OmpRequested && ompThreadEmissionEnabled ()
        let threadedArg = if threaded then "true" else "false"
        // The omp census: a licensed kernel's team is the DRIVER's (row panels
        // / i sub-simplices under schedule(dynamic); cells split across the
        // team, sums never), recorded as applied; a licence the build knob
        // suppresses gets the one census phrase, as a block comment (the
        // call may be space-joined into an inline IIFE).
        let ompCensus (driver: string) : string =
            if not codeGen.OmpRequested then ""
            elif ompThreadEmissionEnabled () then
                recordOmpConstruct cName "omp parallel num_threads(T) inside the tile driver"
                    $"{driver}: work units under schedule(dynamic), cells split across the team, sums never"
                ""
            else $"/* {(ompSuppressedPhrase (ompThreadsSuppressedReason ()))} */ "
        let scaleNote (sk: int) =
            match sk with
            | 1 -> ", then / d per cell (fused into the last block's store)"
            | 2 -> ", then * s per cell (fused into the last block's store)"
            | _ -> ""
        match scaleArgs with
        | None -> declineTile "the kernel's scale is not renderable at this site" ""
        | Some (sk, scTxt) ->
        match rc.Shape with
        | Blade.LinAlgPatterns.AntiPair (cx, cy) ->
            match antisymTileSchedule () with
            | AntiOff ->
                declineTile "BLADE_ANTISYM_TILE=0/off: the plain per-cell nest (A/B knob)" ""
            | sched ->
            // The component subscripts must be PROVEN in range on the literal
            // component extent: the driver reads Z[i][cx] / Z[i][cy] with no
            // check, where the nest would have kept its BL8006 guard.
            let compsOk =
                match lit (literalOrRuntimeExtentOfArray aTy aName 1) with
                | Some c -> cx < c && cy < c
                | None -> false
            if not compsOk then
                declineTile "component subscripts not proven inside the literal component extent" $"components=({cx},{cy})"
            else
            match lit mExtent, lit nExtent with
            | Some p, Some n when packedAntiWorth p n ->
                (packedGemmUsedCell ()).Value <- true
                let (variant, form) =
                    match sched with
                    | AntiPaired4x4 -> (1, "paired 4x4 (P and Q in one pass)")
                    | _ -> (0, "sequential 6x8 (P pass, spill, Q pass)")
                let kcMax = antisymKcMax ()
                let chainNote =
                    if kcMax = 0L || kcMax >= n then "whole chain per tile"
                    else $"whole chain per tile in sample blocks of <= {kcMax}"
                // Row-panel groups (BLADE_TILE_TUNE anti_g; unset = per
                // regime: 1 for a serial tile, the cache budget for one
                // ELIGIBLE for the driver's team -- `threaded` here plus the
                // driver's static work cap, checked inside; 0 = the cache
                // budget): the visiting order only, never a bit. The driver
                // argument `threaded` itself is unchanged.
                let (g, groupNote) = antisymRowGroup p n (variant = 1) threaded
                recordCodegenDecision "microkernel" cName Blade.Effects.Applied
                    [ $"antisym tile (blade_pgemm::dantisym_tri, {form}, strict packed triangle)"
                      $"P={p} N={n} components=({cx},{cy}) KC={kcMax} G={g}" ]
                recordFoldForm cName $"antisym tile {form}, {chainNote}, {groupNote}{scaleNote sk}"
                    "same-array anticomm row-contraction pair (two ascending chains per cell, one subtraction; no reorder licence used)"
                Some [ String.replicate indent "    "
                       + ompCensus "dantisym_tri row groups"
                       + $"/* anticomm nest: antisym pair tile (blade_packed_gemm.hpp dantisym_tri, {form}), row groups of {g}, per cell the serial prodsum - prodsum */ blade_pgemm::dantisym_tri((size_t)({mExtent}), (size_t)({nExtent}), {aName}.data, (size_t){cx}, (size_t){cy}, nested_array_utilities::pool_base({cName}.data), {sk}, (double)({scTxt}), {threadedArg}, {variant}, 0, (size_t){kcMax}, (size_t){g});" ]
            | Some p, Some n ->
                declineTile "below the antisym-tile crossover (p >= 24, n >= 8): the per-cell nest wins" $"P={p} N={n}"
            | _ -> declineTile "non-literal extents: the per-cell nest" "non-literal extents"
        | Blade.LinAlgPatterns.Sym3 ->
            if not (simplexTileEnabled ()) then
                declineTile "BLADE_SIMPLEX_TILE=0/off: the plain per-cell nest (A/B knob)" ""
            else
            match lit mExtent, lit nExtent with
            | Some p, Some n when packedSym3Worth p n ->
                (packedGemmUsedCell ()).Value <- true
                let kc = syrkTileKc n
                let edge = triTileEdge ()
                recordCodegenDecision "microkernel" cName Blade.Effects.Applied
                    [ "simplex tile (blade_pgemm::dsym3_tri, W = X(i) * X(j) panel x 6x8 register tile over k, packed SymIdx<3> pool)"
                      $"P={p} N={n} KC={kc}" ]
                recordFoldForm cName $"simplex tile 6x8 with W panel, KC={kc}{triEdgeNote edge}{scaleNote sk}"
                    "same-array 3-way comm row contraction (one ascending (a*b)*c chain per cell; no reorder licence used)"
                Some [ String.replicate indent "    "
                       + ompCensus "dsym3_tri i sub-simplices"
                       + $"/* comm nest: rank-3 simplex tile (blade_packed_gemm.hpp dsym3_tri, W panel + 6x8, KC={kc}), per cell the serial prodsum */ blade_pgemm::dsym3_tri((size_t)({mExtent}), (size_t)({nExtent}), {aName}.data, nested_array_utilities::pool_base({cName}.data), {sk}, (double)({scTxt}), {threadedArg}, (size_t){syrkTileKcMax}, {edge});" ]
            | Some p, Some n ->
                declineTile "below the simplex-tile crossover (p >= 24, n >= 4): the per-cell nest wins" $"P={p} N={n}"
            | _ -> declineTile "non-literal extents: the per-cell nest" "non-literal extents"
    | _ -> None


/// The linear-algebra dispatch chain at the apply-combinator site: each
/// recognised NEST shape in turn, falling through to the flat elementwise path
/// (`tryGenFlatElementwiseNest`) and then `genLoopNestStreamed`, unchanged --
/// the ordinary emitted nest the interpreter differential already covers.
///
/// Order is not load-bearing: the shapes are disjoint by depth (gemv is a
/// depth-1 nest over a rank-2 operand, syrk a depth-2 triangular nest over
/// two slots naming one array), so at most one can match.
///
/// Stands down in test mode for the same reason the flat path does: omp-
/// coverage instrumentation needs an innermost body to mark, which a
/// dispatched call has none of.
let tryGenLinAlgNest
        (streamed: Map<string, ProviderReadSpec>)
        (operandTypes: IRArrayType list)
        (codeGen: LoopNestCodeGen)
        (outerNames: Map<int, string>)
        (indent: int) : string list option =
    if ompTestModeEnabled () then None
    else
    match tryGenGemvDispatch streamed operandTypes codeGen outerNames indent with
    | Some lines -> Some lines
    | None ->
    match tryGenSyrkDispatch streamed operandTypes codeGen outerNames indent with
    | Some lines -> Some lines
    | None -> tryGenRowContractionTile streamed operandTypes codeGen outerNames indent


// Symmetry Vector Generation


// Array Allocation Generation


// C++ runtime headers
//
// The Blade C++ runtime lives in cpp/*.hpp at the source root and is read
// from disk at codegen time, not embedded in the F# binary as string
// literals. Blade.fsproj copies the cpp/ directory into the build output
// via <CopyToOutputDirectory>, so AppContext.BaseDirectory + "cpp" resolves
// to the correct location regardless of where dotnet run is invoked from.
//
// The generated C++ test output picks up the headers when Main.fs writes
// them into each test's output directory alongside the .cpp file (the
// existing pattern; see Main.fs's writes of headerFile / arrayTypesHeaderFile).
// g++ then resolves `#include "nested_array_utilities.hpp"` relative to
// the .cpp file's directory -- no -I flag needed, no build-output paths
// leaked into the C++ compile line.

/// Resolve the path of a runtime header file shipped in the cpp/ directory
/// next to the compiler binary. Used by readCppRuntimeHeader (and through it
/// deployRuntimeHeaders); centralized here so the AppContext.BaseDirectory
/// and "cpp" subpath assumptions live in one place.
let internal cppRuntimeHeaderPath (filename: string) : string =
    System.IO.Path.Combine(System.AppContext.BaseDirectory, "cpp", filename)

/// Memo for `readCppRuntimeHeader`. The cpp/ headers are static files shipped
/// beside the binary: within one process their contents cannot change, so each
/// is read from disk at most once however many programs get compiled (the test
/// harness compiles ~1500 of them, each deploying the same 13 headers,
/// ~264 KB in total). Concurrent because the harness generates in parallel.
let internal cppRuntimeHeaderCache =
    System.Collections.Concurrent.ConcurrentDictionary<string, string>()

/// Read a Blade C++ runtime header from disk (memoized). Fails loudly if the
/// build hasn't copied cpp/ into the output directory -- this is a
/// configuration error rather than a compiler bug, so the message points at
/// .fsproj. Deliberately a FUNCTION over a lazily-populated table rather than
/// an eagerly-built map, so the not-found diagnostic still fires at the first
/// *use* of a missing header and never for headers nobody asked for.
let internal readCppRuntimeHeader (filename: string) : string =
    match cppRuntimeHeaderCache.TryGetValue filename with
    | true, cached -> cached
    | _ ->
        let path = cppRuntimeHeaderPath filename
        if not (System.IO.File.Exists path) then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf
                "C++ runtime header not found at: %s\n\
                 The build should copy cpp/%s into the output directory.\n\
                 Check that Blade.fsproj contains a <None Include=\"cpp/%s\">\n\
                 item with <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>."
                path filename filename)))
        let text = System.IO.File.ReadAllText path
        cppRuntimeHeaderCache.[filename] <- text
        text

/// The C++ runtime header set. SINGLE SOURCE OF TRUTH: a header newly
/// depended on by the runtime is added here once (and to Blade.fsproj's copy
/// set), after which it reaches every emit site via deployRuntimeHeaders.
/// Exposed as names so callers that clean up after a compile (Cli.fs) know
/// exactly which files were deployed.
let runtimeHeaderNames : string list =
    [ // BLADE_RESTRICT / BLADE_IVDEP: the portable spellings of the annotations
      // codegen emits. Bare `__restrict__` / `#pragma GCC ivdep` are rejected by
      // cl.exe, which the Windows CUDA path drives through nvcc for the HOST
      // half. Pulled in by nested_array_utilities.hpp and blade_linalg_views.hpp
      // rather than by a generated `#include`, but it must still be DEPLOYED
      // beside them.
      "blade_portability.hpp"
      "nested_array_utilities.hpp"
      "nested_array_types.hpp"
      "index_types.h"
      // Host combinadic linearize/unlinearize -- included only by MPI-mode
      // programs (genMpiNestSimplicial), but deployed unconditionally so the
      // deploy/cleanup bookkeeping stays uniform.
      "linearized_storage.hpp"
      // OrbIdx (iterated-wreath) storage: orb_cell_count / the segment-peeled
      // orb_visit nest / orb_canon / orb_rank / orb_unrank. Included only by
      // programs that deduce a wreath class (genWreathApply), but deployed
      // unconditionally so the deploy/cleanup bookkeeping stays uniform.
      "orbit_wreath_utilities.hpp"
      // `rand` module runtime (blade_rand::uniform/normal). Deployed
      // unconditionally (header-only, cheap); referenced by every program's
      // include list.
      "rand_runtime.hpp"
      // Runtime error support: blade_rt shadow call stack + panic() and the
      // BLADE_FRAME macro. Header-only, host-only (device passes see
      // no-op stubs); deployed unconditionally and included by every program.
      "blade_runtime.hpp"
      // Explicit private-spool artifacts include this protocol header. Keep
      // deployment/cache/cleanup on the shared registry like other runtimes;
      // deploying its bytes alone grants no disk or compiler admission.
      "blade_private_spool.hpp"
      // The run record (BLADE_RUN_RECORD): the baked input manifest paired
      // at exit with what the run observed and how it ended. Header-only,
      // host-only; deployed unconditionally and included by every program.
      "blade_run_record.hpp"
      // Revision reuse (docs/plans/structural/04): the local tile store's
      // probe/load/store. INCLUDED only by a program with a tiled binding
      // (tilesUsedCell); deployed unconditionally like the rest.
      "blade_tilecache.hpp"
      // The native (BLAS-off) matmul kernel, packed: blade_pgemm::dgemm_nn.
      // INCLUDED only by a program whose matmul arm called it
      // (packedGemmUsedCell); deployed unconditionally like the rest.
      "blade_packed_gemm.hpp"
      // The pure C99/C++ arithmetic core of the packed-triangle tiles
      // (blade_tri_tile_*), included by blade_packed_gemm.hpp -- so deployed
      // beside it, and hashed into the exe cache key with every other name in
      // this list.
      "blade_tiles.h"
      // The explicit K-lane vector fold forms (blade_fold::dot / sq / dot3 /
      // sum, review items M9 / M14) and the Path B chunk partition
      // (blade_fold::chunk_lo). INCLUDED only by a program with a licensed
      // dot-class fold (foldLanesUsedCell); deployed unconditionally like the
      // rest.
      "blade_fold_lanes.hpp"
      // The ordered orbit scatter (blade_scatter::scatter2 / scatter3,
      // OrbitFold.planStrict). INCLUDED only by a program whose fold took it
      // (orbitScatterUsedCell); deployed unconditionally like the rest.
      "blade_orbit_scatter.hpp"
      // Membership hoisting's C core (blade_member_build_i64 / _test_i64,
      // Optimize.planMembershipHoist). INCLUDED only by a program with a
      // planned hoist (memberUsedCell); deployed unconditionally like the
      // rest. The LLVM shim includes the same file.
      "blade_member.h"
      // Dense linear-algebra dispatch: blade_gemm / blade_syrk plus the
      // gram/matmul adapters, resolving to cblas under -DBLADE_HAS_BLAS and to
      // native fallbacks otherwise. INCLUDED only by programs that actually
      // emit a `blade_linalg::` call (the linalgUsedCell collector), so a
      // program using neither gram nor matmul carries no dependency surface at
      // all -- but DEPLOYED unconditionally, like linearized_storage.hpp and
      // orbit_wreath_utilities.hpp, so the deploy/cleanup bookkeeping stays
      // uniform.
      "blade_linalg.hpp"
      // The BLAS-free half of the same layer: the contiguity probe
      // and the two staging views. `blade_linalg.hpp` includes it, so it must
      // be deployed beside it -- and it is separately includable, which is what
      // lets cpp/linalg_probe_tests.cpp run without BLAS.
      "blade_linalg_views.hpp"
      // DEVICE (cuBLAS) dispatch. Two halves in one file, selected by
      // `__CUDACC__`: `extern "C"` prototypes for the host `.cpp` (g++ cannot
      // include the CUDA headers), definitions for the companion `.cu` that
      // Build.fs writes and nvcc compiles. Included only by programs that emit
      // a `blade_cuda_*` call, so a host program carries no CUDA toolchain
      // dependency -- deployed unconditionally like every other runtime header,
      // and it must be, since the `.cu` lands in the same directory and
      // includes it by relative path (as does `blade_linalg_views.hpp`, which
      // the device arm reuses for the shared contiguity probe).
      "blade_linalg_cuda.hpp"
      // DLL-side failure exit: a Blade-built DLL (the cuBLAS shim above, the
      // mpi+cuda hybrid's kernels) fails through the panic its host binds in,
      // never through a second copy of blade_runtime.hpp. Included by the
      // DLL's own translation unit by relative path, so deployed beside it.
      "blade_dll_panic.hpp"
      // Eigensolver dispatch: the `?spev`/`?hpev`/`?syev`/
      // `?heev` adapters. LAPACK-ONLY (its own `#ifndef BLADE_HAS_LAPACK
      // #error`) and included only by programs that emit a `blade_lapack::`
      // call, so a BLAS program carries no LAPACK dependency -- deployed
      // unconditionally like every other runtime header.
      "blade_lapack.hpp"
      // ASan allocator-stats export bracketing main() (one BLADE-MEMCHECK
      // line on stderr at exit). Included only by programs generated under
      // BLADE_MEMCHECK=1 -- Build.fs pairs the include with a Debug+ASan
      // compile -- but deployed unconditionally so the deploy/cleanup
      // bookkeeping stays uniform.
      "blade_memcheck.hpp" ]

/// The shipped text of one runtime header, through the same per-process memo
/// `deployRuntimeHeaders` uses -- so a consumer that only wants to *read* the
/// header set (Build.fs's executable cache hashes all 13 into its key) pays no
/// extra disk I/O beyond the one read the deploy already did. Same
/// fail-loudly-on-missing contract as the deploy path.
let runtimeHeaderText (filename: string) : string = readCppRuntimeHeader filename

/// Deploy every C++ runtime header next to a generated .cpp so its `#include`s
/// resolve at g++ time with no -I flag. These are pre-existing static files in
/// cpp/, copied verbatim -- nothing is generated or transformed.
///
/// ALL headers are deployed uniformly, unconditionally (see runtimeHeaderNames:
/// the deploy/cleanup bookkeeping is deliberately not per-program) -- but a
/// deployment whose destination already holds byte-identical content is not
/// rewritten. Only the physically redundant write is skipped: rewriting ~264 KB
/// beside every generated .cpp on every compile re-triggers an antivirus scan
/// of each file (measured multi-second variance on a one-line `blade compile`)
/// for no change in what g++ sees.
///
/// This preserves the hand-edit workflow: a deployed header edited in place
/// DIFFERS from the shipped one, so the next `blade run` overwrites it exactly
/// as before -- "if changed" is a content test, not a timestamp test.
///
/// CONCURRENT WRITERS: two compiles sharing an output directory (parallel
/// sweeps, two agents in one scratch dir) can collide on the same destination,
/// and on Windows the loser gets a sharing violation. That is NOT a failure
/// here: both processes deploy the SAME shipped bytes, so the only question is
/// whether the file on disk ends up correct. Re-check the content after a
/// failed write, retry briefly (the other writer's handle lives for
/// microseconds), and surface the IO error only if the destination genuinely
/// does not hold the header -- in which case the compile must not proceed to a
/// g++ that would read a truncated include.
let deployRuntimeHeaders (outputDir: string) : unit =
    runtimeHeaderNames
    |> List.iter (fun name ->
        let dest = System.IO.Path.Combine(outputDir, name)
        let text = readCppRuntimeHeader name
        let alreadyDeployed () =
            try System.IO.File.Exists dest && System.IO.File.ReadAllText dest = text
            with _ -> false
        if not (alreadyDeployed ()) then
            let rec attempt (retriesLeft: int) =
                try
                    System.IO.File.WriteAllText(dest, text)
                with
                // A concurrent writer got there first with identical bytes.
                | _ when alreadyDeployed () -> ()
                | _ when retriesLeft > 0 ->
                    System.Threading.Thread.Sleep 10
                    attempt (retriesLeft - 1)
                // No rule matches when the retries are spent and the file is
                // still wrong: F# re-raises, and the driver reports it.
            attempt 5)

/// Generate includes that reference external header
let genIncludesExternal () : string list =
    // BLADE_POOL_RETAIN (round-3 A4, gap G52): a NON-default pool-retention
    // policy is written into the program ahead of every include, so
    // nested_array_utilities.hpp's blade_pool (and the run record, which
    // reports it) compile with it and the exe cache keys on it through this
    // text. The default writes nothing: its emission is unchanged.
    let retention = Blade.Toolchain.poolRetention ()
    Blade.Toolchain.warnPoolRetention retention
    (match Blade.Toolchain.poolRetentionDefines retention with
     | [] -> []
     | defines ->
         [ $"// BLADE_POOL_RETAIN={retention.Setting}: the opt-in pool-retention arm ({retention.MaxBlockMB} MB blocks / {retention.BudgetMB} MB per thread; nested_array_utilities.hpp blade_pool)" ]
         @ (defines |> List.map (fun (name, bytes) -> $"#define {name} {bytes}")))
    @
    ["#include <cstdint>"
     "#include <cstdlib>"
     "#include <cmath>"
     "#include <complex>"
     "#include <functional>"
     "#include <tuple>"
     "#include <variant>"
     "#include <string>"
     "#include <iostream>"
     "#include <iomanip>"
     "#include <chrono>"
     "#include <algorithm>"  // std::stable_sort (used by sort())
     "#include <numeric>"    // std::iota (used by sort())
     "#include <vector>"     // solve()'s LU working copy (materializeSolveForm)
     "#include <unordered_map>"  // group_keys Case 3 (dynamic ngroups via hash discovery)
     "#include <unordered_set>"  // unique() dedup, contains() hoist (future)
     "#include <omp.h>"
     "#include \"nested_array_utilities.hpp\""
     "#include \"nested_array_types.hpp\""
     // OrbIdx (iterated-wreath) storage. Header-only and dependency-free
     // (<cstdint>/<cstddef>/<cassert>), so it is included unconditionally
     // alongside the other runtime headers rather than gated on whether this
     // particular program deduced a wreath class.
     "#include \"orbit_wreath_utilities.hpp\""
     "#include \"rand_runtime.hpp\""
     "#include <exception>"                 // std::exception for main()'s BL8005 catch
     "#include \"blade_runtime.hpp\""        // blade_rt::panic + BLADE_FRAME shadow stack
     "#include \"blade_run_record.hpp\""     // BLADE_RUN_RECORD: manifest + observed inputs + status at exit
     ]
    // Memcheck instrumentation -- see the sibling include block above.
    @ (if memcheckEnabled () then ["#include \"blade_memcheck.hpp\""] else [])
    @
    ["using std::cout;"
     "using std::endl;"
     ""]
    // Display-frame emitter -- see the sibling include block above.
    @ Blade.Display.Frame.cppRuntime ()
    @
    [""
     "#define TIME std::chrono::steady_clock::now()"
     "#define TIME_DIFF std::chrono::duration_cast<std::chrono::nanoseconds>(__blade_t1 - __blade_t0).count()"
     ""]


// Array Literal Generation

/// One scalar leaf of an array literal, KEPT IN ITS OWN DOMAIN. An integer
/// leaf must never detour through `float`: a double has 53 significand bits,
/// so `[9007199254740993, 3]` used to print `9007199254740992`, and
/// `[9223372036854775807, 1]` stored INT64_MIN (2^63 as a double converts
/// back out of range). docs/formalism.md section 2.4, "Arithmetic semantics".
type LiteralLeaf =
    | LeafFloat of float
    | LeafInt of int64

/// Extract the scalar leaves of an array literal, in row-major order.
let rec extractLiteralValues (expr: IRExpr) : LiteralLeaf list =
    match expr with
    | IRLit (IRLitFloat f) -> [LeafFloat f]
    | IRLit (IRLitFloat32 f) -> [LeafFloat (float f)]
    | IRLit (IRLitInt n) -> [LeafInt n]
    | IRLit (IRLitBool b) -> [LeafInt (if b then 1L else 0L)]
    | IRUnaryOp (IRNeg, IRLit (IRLitFloat f)) -> [LeafFloat -f]
    | IRUnaryOp (IRNeg, IRLit (IRLitFloat32 f)) -> [LeafFloat (float -f)]
    // Two's-complement negation: wraps at INT64_MIN like every integer op.
    | IRUnaryOp (IRNeg, IRLit (IRLitInt n)) -> [LeafInt (0L - n)]
    | IRArrayLit (elements, _) -> elements |> List.collect extractLiteralValues
    | _ -> []

/// Render one literal leaf for a store into an element of C++ type
/// `elemType`. Floating elements take the round-trip double spelling
/// (floatToCppLiteral); integral elements take the EXACT integer spelling --
/// verbatim decimal, with INT64_MIN written as an expression because the
/// token 9223372036854775808 does not fit a signed 64-bit literal. (The old
/// `%g` spelling here printed 1234567 as `1.23457e+06`, a narrowing error in
/// a braced initializer.)
let renderLiteralLeaf (elemType: string) (leaf: LiteralLeaf) : string =
    let isFloating = elemType.Contains "double" || elemType.Contains "float"
    match leaf with
    | LeafFloat v -> floatToCppLiteral v
    // A float element takes the decimal with an `f` suffix: ONE rounding,
    // straight to float, as C++ converts int64 -> float (via double would
    // round twice above 2^53).
    | LeafInt n when isFloating && not (elemType.Contains "double") -> $"{n}.0f"
    | LeafInt n when isFloating -> floatToCppLiteral (float n)
    | LeafInt n when n = System.Int64.MinValue -> "(-9223372036854775807LL - 1)"
    | LeafInt n -> string n

/// Compute dimensions of an array literal
let rec computeArrayDims (expr: IRExpr) : int list =
    match expr with
    | IRArrayLit (elements, _) ->
        let thisLen = elements.Length
        match elements with
        | first :: _ -> thisLen :: computeArrayDims first
        | [] -> [0]
    | _ -> []

/// Compute per-row lengths for a ragged literal. Returns the inner sub-array
/// length for each outer entry. For [[1,2,3], [4,5], [6,7,8,9]] returns [3; 2; 4].
/// For non-ragged or non-nested input, returns the empty list.
let computeRaggedRowLengths (elements: IRExpr list) : int list =
    elements |> List.choose (fun e ->
        match e with
        | IRArrayLit (inner, _) -> Some inner.Length
        | _ -> None)

// Deterministic deallocation: fresh-return facts, scope escapes, alloc registry.
//
// Module-level heap allocations are left to leak (compute-print-exit, OS
// reclaims) -- harmless for a module-level binding, but not for a FUNCTION BODY
// or `for`-range LOOP BODY array (re-allocated per call/iteration, unbounded
// growth). This block lets exactly those two scopes emit a matching
// `deallocate<>`/`deallocate_strict<>` at scope exit.
//
// Three rules make it sound:
//  1. ONLY the two scope drivers push a frame; an EMPTY stack (main()'s top
//     level) makes registerAlloc a no-op, so module bindings survive to be
//     auto-printed/EXPECT-pinned -- structural, not a per-site guard.
//  2. A registration always lands on the TOP frame, so a loop-body free can
//     only name storage allocated in THAT iteration (stack discipline, not
//     id-based reasoning).
//  3. Anything unproven stays leaked: a leak is a bug we already have, but a
//     double free or a free of a pool something still reads is a crash or
//     silently wrong numbers. Escape seeds are deliberately WIDE, propagation
//     barriers deliberately NARROW, and template arguments are recorded
//     verbatim at the allocation site instead of re-derived at exit.
//
// Deliberate exclusions (each a distinct code path, none an oversight):
// ragged/compound/dep-idx outputs; provider reads/writes; RandomInits;
// CompoundInits; CUDA/MPI *device* allocations (already own themselves); and
// streamed `_fb_p` fiber buffers registered outside a live frame.
//
// The statement-level `materializeInlineForm` consumers (site 7) each pick
// their (AllocSpec, SYMM, STRICT) triple from form-specific masks the consumer
// never sees, so each builder returns `string list * MaterializedAlloc list`
// instead of the consumer re-deriving the triple (which risks `deallocate`
// walking a different skeleton than `allocate` built). Statement-position
// consumers call `registerMaterializedAllocs`; IIFE consumers drop the list
// and leak (no scope exit to hang a free on).

/// Flatten a body's nested IRLet chains into (id, value) pairs plus the residual
/// return expression. Deliberately a verbatim mirror of genFuncBody's local
/// `deepUnroll`, including the nested-value hoist: the escape analysis must see
/// EXACTLY the statement list codegen will emit, or an allocation could land in a
/// frame whose escape set was computed from a different shape.
let rec deepUnrollBody (expr: IRExpr) : (IRId * IRExpr) list * IRExpr =
    match expr with
    | IRLet (id, value, body) ->
        let (innerLets, innerFinal) = deepUnrollBody value
        let (restLets, restFinal) = deepUnrollBody body
        match innerLets with
        | [] -> ((id, value) :: restLets, restFinal)
        | _ -> (innerLets @ [(id, innerFinal)] @ restLets, restFinal)
    | _ -> ([], expr)

/// Every sub-expression of `e`, `e` itself included. Recursion is the canonical
/// ExprShape fold, so no new IRExpr variant can hide a nested IRAssign or
/// callable reference from the seed rules below the way a hand-maintained walker
/// with a `| _ ->` arm would.
let rec allSubExprs (e: IRExpr) : IRExpr list =
    match e with
    | ExprShape (children, _) -> e :: (children |> List.collect allSubExprs)

/// A return that hands the caller an INTERIOR VIEW of storage this scope owns.
///
/// `traj((9999 : T))` at the tail of a function-local `let rec` renders as
/// `Array<T,1>{ traj.data[9999], traj.extents + 1 }` -- a wrapper POINTING INTO
/// the trajectory's pool. Nobody can free that pool: not the scope (the
/// returned view still reads it) and not the caller (it never sees the base
/// wrapper, only one row of it), so the whole array leaks on every call. The
/// escape analysis makes that explicit -- seeding the base pins it -- but
/// pinning is not a fix, it is the leak spelled out.
///
/// Returns the base binding's id when the return takes that shape AND the
/// slice can be copied out, which is the actual repair: materialize the slice
/// into its own pool before the frees, and the base becomes an ordinary scope
/// temporary. Three consumers share this ONE predicate so they cannot drift --
/// computeScopeEscapes (drop the return seed), genFuncBodyScoped's return arm
/// (emit the copy), and computeFreshReturnFacts (tell callers they now own a
/// fresh pool). A disagreement between the first two would be a use-after-free,
/// so a single decision point is not a stylistic preference here.
///
/// Guards, each load-bearing for the flat `std::copy_n` the materialization
/// emits:
///   * the base is one of THIS scope's lets. A parameter, capture, or module
///     binding is someone else's storage, correctly returned as a view (and
///     copying it would hand the caller a pool it has no reason to free);
///   * base and slice are both plain dense -- no symmetry, compact, ragged,
///     sparse, dep-idx, or virtual storage -- so the sub-block a leading index
///     selects is CONTIGUOUS in the pool and `pool_base` on the sub-skeleton
///     lands on its first cell rather than the whole pool's;
///   * every subscript is scalar and they form a strict LEADING prefix
///     (`idxs` exactly consumes the rank the slice drops), which is what makes
///     the selection a sub-block rather than a strided gather.
let returnedInteriorView (scopeLets: (IRId * IRExpr) list) (retExpr: IRExpr) : IRId option =
    let denseUnsymmetric (t: IRType) =
        match t with
        | ArrayElem at ->
            isFreeableDenseArrayType at && not at.IsVirtual
            && classifyOutputStorage t = AllocDense
            && not (hasRealSymmetry (buildSymmVec t))
        | _ -> false
    match retExpr with
    | IRIndex ((IRVar (srcId, _)) as baseExpr, idxs, _)
            when scopeLets |> List.exists (fun (id, _) -> id = srcId) ->
        let baseTy = inferExprType baseExpr
        let sliceTy = inferExprType retExpr
        match baseTy, sliceTy with
        | ArrayElem bat, ArrayElem sat
                when denseUnsymmetric baseTy && denseUnsymmetric sliceTy
                     && not (List.isEmpty idxs)
                     && arrayRank sat > 0
                     && List.length idxs = arrayRank bat - arrayRank sat
                     && idxs |> List.forall (fun i ->
                            (inferExprType i).IsIRTScalar) ->
            Some srcId
        | _ -> None
    | _ -> None

/// Classify every array-returning module function as FreshPool or NotFresh.
/// Only array-typed returns are in the domain; anything else is absent and reads
/// as NotFresh. The iteration is a fixpoint because a function may return the
/// result of calling another one; it is monotone (facts only move NotFresh ->
/// FreshPool) so it terminates.
let computeFreshReturnFacts (modul: IRModule) : Map<IRId, FreshReturn> =
    let isLifted (e: IRExpr) =
        // The two return forms genFuncBody LIFTS into a named local before
        // returning it (10277 / 10282). Both allocate under that local's name,
        // so the storage is unaliased and the caller owns it.
        match e with
        | IRCompute (IRApplyCombinator _) | IRArrayLit _ -> true
        | _ -> false
    let unrolled =
        modul.Functions
        |> List.filter (fun f -> match f.RetType with ArrayElem _ -> true | _ -> false)
        |> List.map (fun f -> (f.Id, deepUnrollBody f.Body))
    let classify (facts: Map<IRId, FreshReturn>) (lets: (IRId * IRExpr) list) (retExpr: IRExpr) : FreshReturn =
        if isLifted retExpr then FreshPool
        else
            match retExpr with
            | IRVar (rid, _) ->
                // `return r` where r is bound to a lifted form AND appears in no
                // OTHER let's value: the let's storage IS the return value, and
                // no second name can be holding it. An alias-shaped second use
                // (`let s = r(0)` before the return) demotes to NotFresh.
                let boundToLifted = lets |> List.exists (fun (id, v) -> id = rid && isLifted v)
                // A use that is the base of a FULL, scalar-typed element access
                // -- a cell store `r(i, j) = v` or a cell read `r(i, j)` -- makes
                // no second name for the pool, so it does not demote. Every
                // other mention (a row view, an alias, an argument, a whole-
                // array assign) still does. Without this, a function that fills
                // its result cell by cell (`let mut out = zeros; out(i, j) =
                // ...; out`, the spectra transforms' output since the batched
                // FFT rewrite) was NotFresh, and its caller never freed the
                // result: one 32 / 64 KB field per sp.ifft2 / sp.fft2 call on
                // examples/09_qg_atmosphere (1.25 GB over the run).
                let aliasingUse (v: IRExpr) =
                    let subs = allSubExprs v
                    let refs = subs |> List.sumBy (fun e -> match e with IRVar (i, _) when i = rid -> 1 | _ -> 0)
                    let cellAccesses =
                        subs |> List.sumBy (fun e ->
                            match e with
                            | IRIndex (IRVar (i, _), _, _) when i = rid ->
                                (match inferExprType e with
                                 | IRTScalar _ | IRTUnitAnnotated (IRTScalar _, _) -> 1
                                 | _ -> 0)
                            | _ -> 0)
                    refs > cellAccesses
                let otherUses =
                    lets |> List.filter (fun (id, v) -> id <> rid && Set.contains rid (collectVarRefsIR v) && aliasingUse v)
                if boundToLifted && List.isEmpty otherUses then FreshPool else NotFresh
            // An interior view of a scope-local array is no longer returned as
            // a view: genFuncBodyScoped's return arm copies the slice into its
            // own pool (see returnedInteriorView), so the caller DOES own the
            // storage it receives. Classifying it NotFresh here would leave the
            // caller-side free off and simply move the leak one frame out.
            | IRIndex _ when (returnedInteriorView lets retExpr).IsSome -> FreshPool
            // A returned `stack(...)` / `join(...)` leaves in the fresh `__retN`
            // pool the statement-shaped return arm materializes (a dense alloc
            // plus per-source copies, spared from this frame's frees), so the
            // CALLER owns it. NotFresh here leaked one result per call: the
            // recursion step of examples/09_qg_atmosphere returns its stacked
            // state, and the time loop copied it into the trajectory row and
            // dropped it, every step (1.07 GB over the run).
            | IRStack _ | IRJoin _ -> FreshPool
            | IRApp (f, _, _) ->
                match resolveCallable f with
                | Some c -> Map.tryFind c.Id facts |> Option.defaultValue NotFresh
                | None -> NotFresh
            // Params, captures, IRIndex/IRSlice/IRCurry views, IRIf/IRMatch,
            // IRTuple, materializer forms, unresolved callees: all NotFresh.
            | _ -> NotFresh
    let rec fix (facts: Map<IRId, FreshReturn>) =
        let facts' =
            unrolled |> List.fold (fun acc (fid, (lets, retExpr)) ->
                match Map.tryFind fid acc with
                | Some FreshPool -> acc
                | _ ->
                    match classify acc lets retExpr with
                    | FreshPool -> Map.add fid FreshPool acc
                    | NotFresh -> acc) facts
        if facts' = facts then facts else fix facts'
    fix Map.empty

/// Does this let's value OWN a freshly allocated pool that nothing else in the
/// scope can reach? Only such a value STOPS escape propagation: when the binding
/// escapes, its inputs need not also be pinned. The classification is the ONE
/// exhaustive definition in IR.fs (`isFreshPoolFormWith`, shared with the
/// optimizer's pool-reuse planner); a call is fresh when its callee's fresh-
/// return fact says so.
let isFreshPoolForm (e: IRExpr) : bool =
    isFreshPoolFormWith (fun f -> freshReturnOf f = FreshPool) e

/// May a binding whose value is a bare reference to a scope-local STAGING let be
/// emitted as a plain ALIAS, instead of genVarAliasBinding's defensive deep copy?
///
/// The copy exists because an ASSIGNABLE binding must not share storage with a
/// value some other name can still reach: `let mut a = Z` followed by
/// `a(i) = ...` would otherwise corrupt `Z`. When the source is a scope-local
/// staging let that SOLELY OWNS a freshly allocated pool, there is no `Z` to
/// protect and the copy is pure duplication -- and that is precisely the
/// recursive-array elaboration's shape (`{ let buf = zeros(...); ...; buf }`),
/// whose double materialization cost 2x the resident footprint of every
/// `let rec` trajectory.
///
/// `scopeLets` is the enclosing let list: a block's own chain at module level,
/// or the FLATTENED function body inside a frame (genFuncBody's deepUnroll
/// dissolves the block, so the same shape arrives as an ordinary sibling let).
/// `selfId` is the alias binding's own id where it appears in that list, so it
/// is not counted as a rival name. Conditions:
///  (1) the staging let OWNS its pool (isFreshPoolForm: a view/alias value could
///      be sharing a USER binding's storage, which the copy exists to protect);
///  (2) no assign in the scope can have leaked that storage to a target OUTSIDE
///      the scope (an outer whole-array or row assign aliases pools; assigns
///      whose target is scope-internal die with the scope); and
///  (3) no OTHER let in the scope is a bare reference to the same staging let.
///      Two aliases of one pool would each act as its owner, so a write through
///      either would be visible through the other. A block chain cannot pose
///      this (it has exactly one value, which is not itself one of `scopeLets`);
///      a flattened function body can.
///  (4) nothing evaluated AFTER the alias reads the staging let, directly or
///      through a let derived from it (a deferred combinator or lambda bound
///      before the alias reads its operand only where it is consumed). After
///      the alias, a write through it is a write to the source's pool, so a
///      later read of the source would see it (N1: `let q = x * 2.0; let mut
///      q2 = q; q2(0) = 100.0; q(0)` returned 100.0 where the interpreter's
///      copy gives 2.0). "After" is the lets following `selfId` in `scopeLets`
///      plus `tailExprs` (a function body's return expression). A block chain
///      (selfId = None) is the source's last mention by construction: its value
///      IS the reference and nothing in the chain follows it. Derivation is
///      over-approximated (any let whose value mentions the source or a
///      derived let, before or after), which can only keep a copy.
let canAliasStagingLet (scopeLets: (IRId * IRExpr) list) (selfId: IRId option) (srcId: IRId) (tailExprs: IRExpr list) : bool =
    let scopeIds = scopeLets |> List.map fst |> Set.ofList
    let derived =
        let rec grow (acc: Set<IRId>) =
            let acc' =
                scopeLets |> List.fold (fun s (id, v) ->
                    if Some id = selfId || Set.contains id s then s
                    elif Set.isEmpty (Set.intersect (collectVarRefsIR v) s) then s
                    else Set.add id s) acc
            if acc' = acc then acc else grow acc'
        grow (Set.singleton srcId)
    let readAfterAlias =
        let laterLets =
            match selfId with
            | Some sid ->
                match List.tryFindIndex (fun (id, _) -> id = sid) scopeLets with
                | Some k -> scopeLets |> List.skip (k + 1) |> List.map snd
                | None -> scopeLets |> List.map snd   // self not in scope: assume everything follows
            | None -> []
        (laterLets @ tailExprs)
        |> List.exists (fun e -> not (Set.isEmpty (Set.intersect (collectVarRefsIR e) derived)))
    let srcOwnsPool =
        scopeLets |> List.exists (fun (id, v) -> id = srcId && isFreshPoolForm v)
    let assignLeaksSrc =
        scopeLets
        |> List.collect (fun (_, v) -> allSubExprs v)
        |> List.exists (fun e ->
            match e with
            | IRAssign (target, value) when Set.contains srcId (collectVarRefsIR value) ->
                let targetInScope =
                    match target with
                    | LVVar tid -> Set.contains tid scopeIds
                    | LVIndex (IRVar (tid, _), _) -> Set.contains tid scopeIds
                    | _ -> false
                not targetInScope
            | _ -> false)
    let rivalAlias =
        scopeLets
        |> List.exists (fun (id, v) ->
            Some id <> selfId
            && (match v with IRVar (vid, _) -> vid = srcId | _ -> false))
    Set.contains srcId scopeIds && srcOwnsPool && not assignLeaksSrc && not rivalAlias
    && not readAfterAlias

// Whole-array mut reassignment as copy-into-place.
//
// `STG = t` inside a step function REBINDS the wrapper by default (`STG.data`
// repointed at t's pool, original abandoned), making a captured mut a per-call
// allocation sink: the RHS temp must be escape-seeded, so every step leaks a
// whole array. When the mut is the SOLE OWNER of its pool (no other binding
// names its wrapper, no sub-view, never returned), rebinding and copying are
// OBSERVATIONALLY IDENTICAL -- copy t's elements INTO the mut's existing pool
// (allocated once at main level, never scope-freed) and the RHS temp stays
// iteration-owned, dying with its own scope.
//
// Sole ownership is the entire safety argument, so the position analysis is an
// ALLOWLIST: the mut may occur only as (1) a whole-array assign target, (2) a
// FULL scalar-index base, (3) an `extents` read base, or (4) a reduce/prodsum
// operand. Everything else -- a bare `IRVar` anywhere, a PARTIAL index (a
// sub-view aliasing the pool), any unlisted form -- disqualifies. A missed
// disqualification silently changes numbers; a spurious one only leaves the
// leak in place.
//
// Scope is module-level `let mut` only: a block-local mut is already
// scope-freed at its own frame's exit, with far less to gain.

/// The per-slot literal extents of a plain-dense, statically-shaped array type.
/// None whenever ANY slot is symmetric, compact, ragged, dep-indexed, virtual,
/// dependent, rank>1, or has a non-literal extent -- in all of those the pool
/// cardinality is not the product of the extents, so neither the shape-identity
/// test nor the `std::copy_n` count would be sound.
let internal staticDenseExtents (at: IRArrayType) : int list option =
    if not (isFreeableDenseArrayType at) || at.IsVirtual then None
    else
        let slots =
            at.IndexTypes |> List.map (fun ix ->
                if ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain
                   && List.isEmpty ix.Dependencies then
                    match ix.Extent with
                    | IRLit (IRLitInt n) when n > 0L -> Some (int n)
                    | _ -> None
                else None)
        if List.isEmpty slots || slots |> List.exists Option.isNone then None
        else Some (slots |> List.map Option.get)

/// Values that emit as a REAL C++ array variable owning a fresh pool under the
/// binding's own name -- so `pool_base(<name>.data)` is meaningful. Note the
/// deliberate absence of a bare `IRApplyCombinator` / `IRComposeApply`: those are
/// DEFERRED (their statement arms emit nothing), so the name does not exist yet.
let internal isMaterializedFreshArray (v: IRExpr) : bool =
    match v with
    | IRCompute inner -> isFreshPoolForm inner
    | IRArrayLit _ -> true
    | IRMask _ | IRSort _ | IRUnique _ | IRIntersect _ | IRUnion _
    | IRTranspose _ | IRDecompact _ | IRStack _ | IRJoin _ | IRGram _ | IRGramApply _ | IRMatmul _ | IRSolve _ | IRLuSolve _
    | IRArrayNegate _ | IRArrayConjugate _ -> true
    | _ -> false

/// Every `let` value reachable from an expression, at any nesting depth.
let rec internal letValuesIn (e: IRExpr) : (IRId * IRExpr) list =
    let here = match e with IRLet (id, v, _) -> [(id, v)] | _ -> []
    match e with
    | ExprShape (children, _) -> here @ (children |> List.collect letValuesIn)

/// Does `IRVar(mid)` occur anywhere in `e` OUTSIDE the four blessed positions?
/// See the allowlist rationale above; the trailing generic arm recurses through
/// ExprShape, and reaching a bare `IRVar(mid)` there IS the disqualification.
let rec internal occursOutsideBlessedPositions (mid: IRId) (e: IRExpr) : bool =
    let scan (c: IRExpr) = occursOutsideBlessedPositions mid c
    // A slot in which a BARE `IRVar(mid)` is a transient read: accept it there,
    // but keep scanning any other expression that occupies the slot.
    let blessed (c: IRExpr) =
        match c with
        | IRVar (id, _) when id = mid -> false
        | _ -> scan c
    match e with
    | IRVar (id, _) -> id = mid
    | IRAssign (target, value) ->
        (match target with
         | LVVar _ -> false                              // the whole-array write itself
         | LVIndex (b, idxs) -> blessed b || (idxs |> List.exists scan)
         | _ -> scan target)
        || scan value
    | IRIndex (b, idxs, _) ->
        // A FULL read yields a scalar and copies the cell out; a PARTIAL read
        // yields a sub-view that ALIASES the pool (memfree/016's `brow = STG(0)`).
        let isFullRead = (inferExprType e).IsIRTScalar
        (if isFullRead then blessed b else scan b) || (idxs |> List.exists scan)
    | IRExtent (b, _) -> blessed b
    | IRReduce (a, k, i) ->
        blessed a || scan k || (match i with Some x -> scan x | None -> false)
    | IRReduceCompute (c, k, i) -> blessed c || scan k || scan i
    | IRProdSum args -> args |> List.exists blessed
    | ExprShape (children, _) -> children |> List.exists scan

/// Module-level mut array bindings eligible for copy-into-place, mapped to the
/// static element count of their pool. Installed alongside computeFreshReturnFacts
/// (it needs the facts for `isFreshPoolForm`'s IRApp arm, so it must run after).
let computeCopyInPlaceMuts (modul: IRModule) : Map<IRId, int> =
    let exprs =
        (modul.Bindings |> List.map (_.Value))
        @ (modul.Functions |> List.map (_.Body))
    // Both whole-module walks are LAZY: a program with no mut array bindings --
    // the overwhelming majority -- must not pay a tree traversal for a candidate
    // set that turns out empty.
    let allValues =
        lazy ((modul.Bindings |> List.map (fun b -> (b.Id, b.Value)))
              @ (exprs |> List.collect letValuesIn))
    let valueOf (id: IRId) =
        allValues.Force () |> List.tryPick (fun (i, v) -> if i = id then Some v else None)
    let subs = lazy (exprs |> List.collect allSubExprs)
    // (a) array-typed, dense, statically shaped, and owning a pool of its own.
    let candidates =
        modul.Bindings
        |> List.choose (fun b ->
            if not b.IsMutable then None
            else
                match b.Type with
                | ArrayElem at ->
                    match staticDenseExtents at with
                    | Some dims ->
                        let ownsPool =
                            Map.containsKey b.Id modul.RandomInits
                            || (match b.Value with
                                // `let mut a = Z` deep-copies (site 6), so the
                                // binding owns the copy, not Z's pool.
                                | IRVar _ -> true
                                | v -> isMaterializedFreshArray v)
                        if ownsPool then Some (b.Id, (at, dims)) else None
                    | None -> None
                | _ -> None)
    // (c) every whole-array assign to it takes a plain IRVar of a MATERIALIZED
    // array with identical static shape and element type.
    let assignsOk (mid: IRId) (at: IRArrayType) (dims: int list) =
        let rhss =
            subs.Force ()
            |> List.choose (fun e ->
                match e with
                | IRAssign (LVVar tid, value) when tid = mid -> Some value
                | _ -> None)
        not (List.isEmpty rhss)
        && rhss |> List.forall (fun v ->
            match v with
            | IRVar (rid, _) ->
                (match inferExprType v with
                 | ArrayElem rat ->
                     staticDenseExtents rat = Some dims && rat.ElemType = at.ElemType
                 | _ -> false)
                && (match valueOf rid with
                    | Some rv -> isMaterializedFreshArray rv
                    | None -> false)
            | _ -> false)
    candidates
    |> List.choose (fun (mid, (at, dims)) ->
        // (b) sole ownership: no alias-shaped occurrence anywhere in the module.
        if exprs |> List.exists (occursOutsideBlessedPositions mid) then None
        elif not (assignsOk mid at dims) then None
        else Some (mid, dims |> List.fold (*) 1))
    |> Map.ofList

/// Which scope a frame is being computed for. A function scope may have a return
/// expression whose reachable ids must be pinned; a loop body never returns.
type ScopeKind =
    | FuncScope of retExpr: IRExpr option
    | LoopScope

/// Binding ids in this scope whose allocations must NOT be freed at scope exit.
/// Seeds are intentionally over-approximate (a missed seed frees too early; an
/// extra seed only leaks), then propagated backwards through every non-barrier
/// let so that an escaping value pins whatever storage it may alias.
let computeScopeEscapes (ctx: CodeGenContext) (kind: ScopeKind) (scopeLets: (IRId * IRExpr) list) : Set<IRId> =
    // Deep, so an assign or a callable reference buried in a nested loop body,
    // match arm, or ApplyInfo subtree cannot be missed.
    // The RETURN expression is one of the scope's statements for the assign
    // and capture seeds below, WHATEVER its type (gap G54, the second Codex
    // look of W4-2, NEW 1): a scalar-returning call can export a closure
    // into caller-owned storage (`stash(out, lambda(k) -> reduce(y(0), (+))
    // * k) + s`), so its assigns and its callable references are scanned like
    // a let's, and its references COUNT toward a kernel's uniqueness.
    let retRoots =
        match kind with
        | FuncScope (Some retExpr) -> [ retExpr ]
        | _ -> []
    let letSubs = scopeLets |> List.collect (fun (_, v) -> allSubExprs v)
    let subs = letSubs @ (retRoots |> List.collect allSubExprs)
    // Seed 1 -- whole-array reassignment. `a = someOther;` makes a.data alias
    // someOther's pool, so BOTH sides must survive: the target because its
    // original pool is no longer the one it names, the RHS because the target now
    // shares it. Widened past LVVar-only to every assign shape with a
    // not-obviously-scalar RHS, which also covers `rec(i) = <row>`.
    let assignSeeds =
        subs |> List.collect (fun e ->
            match e with
            // Copy-in-place: an assign that will compile to `std::copy_n` into the
            // target's OWN pool creates no aliasing at all -- that is the whole
            // point of the transform -- so it seeds NEITHER side. The RHS temp
            // becomes ordinary scope-owned storage and is freed at scope exit;
            // the target keeps the single pool it has always had.
            | IRAssign (target, value) when (copyInPlaceAssign target value).IsSome -> []
            | IRAssign (target, value) ->
                let targetIds = match target with LVVar tid -> [tid] | _ -> []
                let valueIds =
                    match inferExprType value with
                    | IRTScalar _ | IRTUnit -> []
                    | _ -> collectVarRefsIR value |> Set.toList
                targetIds @ valueIds
            | _ -> [])
    // Seed 2 -- captures. A step function's `[&]` capture of an outer mut (and
    // any kernel lambda's captures) keeps reading that storage after the scope's
    // own use of it ends.
    // The RETURN expression counts too (it is in `subs`, whatever its type):
    // a closure returned from the function (`function mk(n) = { let v = ...;
    // lambda(k) -> reduce(v, (+)) + k }`) copies v's wrapper and keeps reading
    // v's pool after the frame is gone (CodeGenExprSupport, "CLOSURE VALUES
    // CAPTURE BY VALUE"), and a scalar-returning call can STORE one
    // (`stash(out, <closure>)`); the return seed below only sees ids, never
    // what a callable captured.
    let rec carriesFn (t: IRType) =
        match t with
        | IRTScalar _ | IRTUnit | IRTIdxTagged _ -> false
        | IRTUnitAnnotated (inner, _) -> carriesFn inner
        | ArrayElem at -> carriesFn at.ElemType
        | IRTTuple ts -> ts |> List.exists carriesFn
        | _ -> true
    // SYNCHRONOUS KERNEL SLOTS (gap G54; a POSITIVE classification of every
    // statement root, slot by slot, fail closed). EVERY callable occurrence
    // in the scope -- each let's value and the return: an assign's value
    // (`out(0) = <closure>`: assignSeeds names only the callable's id, so its
    // captures are seeded HERE), a call argument (`stash(out, <closure>)`, in
    // a let or in the return), a deferred map, a returned closure, a direct
    // call of a local function -- pins its captures, EXCEPT an occurrence
    // proved to run and die inside its statement:
    //   * it is a KERNEL SLOT of a statement root:
    //       - of a scope LET: only the base whitelist -- the let's WHOLE value
    //         is a SCALAR fold (`IRReduce` / `IRReduceCompute`, `IRCompute`-
    //         wrapped or not): its fold kernel (each join leg) and the kernel
    //         of the map it consumes directly (the seed is not walked);
    //       - of the RETURN: found by the GENERIC walk `slotsOf` below
    //         (every child of a scalar node; a fold's kernel legs and
    //         directly consumed map kernel; a joined fold's fusion leaves),
    //         or the kernel of the return's whole materialized map
    //         `IRCompute (IRApplyCombinator)` (run into `__retN` BEFORE the
    //         scope frees, CodeGen's statement-shaped return arm);
    //   * that occurrence is the callable's ONLY reference among all the
    //     scope's sub-expressions, the return's included (an object_for
    //     application names its kernel twice, `Kernel` and the loop's
    //     provenance `IRObjectFor.Kernel`: one reference);
    //   * it writes nothing outside itself (`callableMayWrite`: transitive
    //     through every callable it names, an unresolved callee a write -- so
    //     a closure stored from its body counts);
    //   * its return type cannot hold a function.
    // Only such a callable's captures are not seeded; the captured array then
    // takes the ordinary scope-exit free beside its siblings. A let-bound
    // computed map's kernel (`let z = method_for(..) <@> k |> compute`) is NOT
    // admitted: its captures stay seeded, as before (a possible leak, never a
    // use after free).
    // POOL-FREE VALUE TYPES (the fifth Codex look of W4B-C: decided by what a
    // scope free covers, not by a list of "scalar" spellings). A node is
    // walked by the return classification iff its inferred type can hold
    // NEITHER array storage NOR a function. EVERY IRType constructor is named
    // (no wildcard), so a new one fails to compile here instead of silently
    // stopping the walk:
    //   * a plain scalar of any numeric / Bool element; a String too -- its
    //     C++ value owns heap characters but never an array POOL, so no scope
    //     free can cover or be pinned by it; ETUnit is the unit value
    //     (assignments) and is NOT walked;
    //   * a unit-annotated or INDEX-TAGGED type over a pool-free inner
    //     (`Float<meters>`, `Nat<I>`), peeled recursively;
    //   * NOT walked: an array / function (IRTArrow), a tuple (the joined-fold
    //     rule below is the one tuple admission), a loop object, a suspended
    //     computation, unit, an arity-polymorphic, type-level natural,
    //     distribution (erased to packed cumulant pools), named (a struct may
    //     hold arrays), group-keys or still-unresolved type.
    let rec isScalarTy (t: IRType) =
        match t with
        | IRTScalar et ->
            (match et with
             | ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128 | ETBool | ETString -> true
             | ETUnit -> false)
        | IRTUnitAnnotated (inner, _) -> isScalarTy inner
        | IRTIdxTagged (inner, _) -> isScalarTy inner
        | IRTTuple _ | IRTLoop _ | IRTComputation _ | IRTUnit | IRTPoly _ | IRTNat _
        | IRTDist _ | IRTNamed _ | IRTInfer _ | IRTGroupKeys _ | IRTArrow _ -> false
    let legs (k: IRExpr) = match k with IRTuple ks -> ks | k -> [ k ]
    let mapKernel (a: IRExpr) =
        match a with
        | IRApplyCombinator info | IRCompute (IRApplyCombinator info) -> [ info.Kernel ]
        | _ -> []
    // The scope-let whitelist: exactly the merge's (a whole-value scalar fold).
    let letSlots (value: IRExpr) : IRExpr list =
        match inferExprType value with
        | IRTScalar _ ->
            (match (match value with IRCompute e -> e | e -> e) with
             | IRReduce (arr, k, _) -> legs k @ mapKernel arr
             | IRReduceCompute (comp, k, _) -> legs k @ mapKernel comp
             | _ -> [])
        | _ -> []
    // A function-free TUPLE OF SCALARS (nested tuples of scalars included):
    // the accumulators of a joined fold.
    let rec scalarTuple (t: IRType) =
        match t with
        | IRTTuple ts -> not ts.IsEmpty && ts |> List.forall (fun t -> isScalarTy t || scalarTuple t)
        | _ -> false
    // The map kernels of a FUSED fold's directly consumed `<&!>` tree: each
    // leaf map of the IRFusion tree (the joined traversal runs them all inside
    // the one fold).
    let rec fusionLeafKernels (e: IRExpr) : IRExpr list =
        match e with
        | IRFusion (l, r) -> fusionLeafKernels l @ fusionLeafKernels r
        | IRApplyCombinator info | IRCompute (IRApplyCombinator info) -> [ info.Kernel ]
        | _ -> []
    // THE RETURN WALK, GENERIC (the fourth Codex look of W4B-C: no container
    // list -- `if` / `match` / `let`, `fma`, complex constructors, casts,
    // comparisons, a call's scalar arguments, and any node added later). A
    // SCALAR-typed node is evaluated, whole, inside the return statement, which
    // a scalar return evaluates into a local BEFORE the scope frees (CodeGen's
    // value-first return shape; a branching return runs the arm it takes as an
    // immediately-invoked body there too), so it is walked through EVERY
    // ExprShape child; the walk stops at any child that is not scalar-typed --
    // a function-typed node (a callable VALUE: a call's callee, a callable
    // argument, a closure -- stays seeded), an array-typed node (an array call
    // argument, a deferred map), a unit-typed assignment -- and lambda / kernel
    // bodies are never children (lifted, IRVar leaves). A FOLD contributes its
    // kernel legs and the kernel of the map it consumes directly as slots; a
    // JOINED fold (a function-free tuple of scalars, IRReduceCompute over an
    // `<&!>` IRFusion tree) is its own boundary: its legs and every fusion
    // leaf's map kernel. Nothing is hoisted or evaluated differently: this only
    // classifies, and every occurrence it does not name stays in `subs`.
    // The callee ids the return walk admitted as IMMEDIATELY INVOKED lambdas
    // (the only candidates of the owned-cell write proof below).
    let iifeCallees = System.Collections.Generic.HashSet<IRId>()
    let rec slotsOf (e: IRExpr) : IRExpr list =
        let ty = inferExprType e
        if isScalarTy ty then
            match e with
            | IRCompute (IRReduce _ as inner) | IRCompute (IRReduceCompute _ as inner) -> slotsOf inner
            | IRReduce (arr, k, seed) -> legs k @ mapKernel arr @ (seed |> Option.toList |> List.collect slotsOf)
            | IRReduceCompute (comp, k, seed) -> legs k @ mapKernel comp @ slotsOf seed
            // An IMMEDIATELY INVOKED anonymous lambda, `(lambda(i) -> y(i))(k)`
            // (the sixth Codex look of W4B-C): the CALLEE occurrence itself is
            // a slot -- the lifted lambda runs synchronously inside this
            // pool-free call, which the scalar return evaluates before the
            // frees -- under the same four gates (one effective reference, no
            // transitive write, no function-holding return). Only a directly
            // resolved ANONYMOUS lambda (`lambda` / `lambda in <scope>`): a
            // let-bound local function called directly keeps its captures
            // seeded (the pre-existing conservative case), and every callable
            // ARGUMENT stays a non-pool-free child, never walked.
            | IRApp (IRVar _ as f, args, _) ->
                let calleeSlot =
                    match resolveCallable f with
                    | Some c when c.SourceName = "lambda" || c.SourceName.StartsWith "lambda in " ->
                        (match f with IRVar (fid, _) -> iifeCallees.Add fid |> ignore | _ -> ())
                        [ f ]
                    | _ -> []
                calleeSlot @ (args |> List.collect slotsOf)
            | _ ->
                let (ExprShape (kids, _)) = e
                kids |> List.collect slotsOf
        elif scalarTuple ty then
            match e with
            | IRReduceCompute (comp, k, seed) | IRCompute (IRReduceCompute (comp, k, seed)) ->
                legs k @ fusionLeafKernels comp @ slotsOf seed
            | _ -> []
        else []
    let returnSlots (root: IRExpr) : IRExpr list =
        match root with
        | IRCompute (IRApplyCombinator info) when not (carriesFn (inferExprType root)) -> [ info.Kernel ]
        // A JOINED fold as the whole return (the third Codex look of W4B-C):
        // evaluated into `__retN` by CodeGen's IRReduceCompute return arm
        // BEFORE the scope frees. Any OTHER tuple-valued return takes the
        // generic tuple arm (frees before `return`, suppressed only for the
        // allocation names its text mentions), so it gets no slot: slotsOf
        // admits a tuple only as a joined fold.
        | _ -> slotsOf root
    // THE OWNED-CELL WRITE PROOF (the seventh Codex look of W4B-C; its index
    // effects closed by the eighth), for an immediately invoked lambda of the
    // return ONLY: `(lambda(i) -> { y(i) = y(i) + 1.0; y(i) })(k)` writes, so
    // the four gates' no-write condition fails, yet nothing escapes -- the call
    // is synchronous, its result is pool-free, and the write lands in a CELL of
    // the scope's own array. A closed proof, every condition positive:
    //   * every assignment in the callee's body is a SCALAR store into one
    //     element, `a(i..) = v` (`LVIndex` on a variable, every index and the
    //     value pool-free), never a whole-array or variable assignment;
    //   * its target is a CAPTURE of the callee that is a let of THIS scope
    //     whose value is an EAGER fresh pool -- a `|> compute`d form or an
    //     array literal that `isFreshPoolForm` accepts (`isFreshPoolForm`
    //     alone also accepts a bare deferred combinator, so a deferred map is
    //     excluded explicitly) -- never a parameter, a `mut` parameter or an
    //     alias;
    //   * with ONLY the stores themselves removed -- each replaced by its
    //     OPERANDS, every index expression and the stored value, which still
    //     run -- the body writes NOTHING by the one write analysis
    //     (`exprMayWrite`: transitive through every callable it names, an
    //     unresolved callee a write). So a named call in an index or in the
    //     value that exports a closure or writes anything keeps the captures
    //     seeded, as does any closure / pointer store, provider write,
    //     mut-parameter call or unknown write elsewhere in the body.
    // Anything else keeps the callee's captures seeded (memfree/034's
    // exporting fold kernel is not even a candidate: it is no lambda callee).
    let ownedCellWritesOnly (c: IRCallable) : bool =
        let eagerFreshPool (v: IRExpr) =
            match v with
            | IRCompute _ | IRArrayLit _ -> isFreshPoolForm v
            | _ -> false
        let owned =
            c.Captures |> List.map (_.Id)
            |> List.filter (fun cid -> scopeLets |> List.exists (fun (id, v) -> id = cid && eagerFreshPool v))
            |> Set.ofList
        let assigns = allSubExprs c.Body |> List.choose (fun e -> match e with IRAssign (t, v) -> Some (t, v) | _ -> None)
        let ownedCellStore (t: IRExpr) (v: IRExpr) =
            match t with
            | LVIndex (IRVar (tid, _), idxs) ->
                owned.Contains tid
                && isScalarTy (inferExprType v)
                && idxs |> List.forall (fun i -> isScalarTy (inferExprType i))
            | _ -> false
        not assigns.IsEmpty
        && assigns |> List.forall (fun (t, v) -> ownedCellStore t v)
        && not (exprMayWrite (c.Body |> mapIRExpr (fun e ->
                match e with
                | IRAssign ((LVIndex (_, idxs)) as t, v) when ownedCellStore t v -> IRTuple (idxs @ [ v ])
                | e -> e)))
    let objectForDuplicates (id: IRId) =
        subs |> List.sumBy (fun e ->
            match e with
            | IRApplyCombinator info ->
                (match info.Kernel, info.Loop with
                 | IRVar (k1, _), IRObjectFor { Kernel = IRVar (k2, _) } when k1 = id && k2 = id -> 1
                 | _ -> 0)
            | _ -> 0)
    let refCount (id: IRId) =
        (subs |> List.sumBy (fun e -> match e with IRVar (i, _) when i = id -> 1 | _ -> 0))
        - objectForDuplicates id
    let provedSync : Set<IRId> =
        ((scopeLets |> List.collect (fun (_, v) -> letSlots v))
         @ (retRoots |> List.collect returnSlots))
        |> List.choose (fun k ->
            match k with
            | IRVar (kid, _) ->
                (match resolveCallable k with
                 | Some c when refCount kid = 1
                               && (not (callableMayWrite c)
                                   || (iifeCallees.Contains kid && ownedCellWritesOnly c))
                               && not (carriesFn c.RetType) -> Some kid
                 | _ -> None)
            | _ -> None)
        |> Set.ofList
    let captureSeeds =
        subs |> List.collect (fun e ->
            match e with
            | IRVar (id, _) when provedSync.Contains id -> []
            | _ ->
            match resolveCallable e with
            | Some c -> c.Captures |> List.map (_.Id)
            | None -> [])
    // Seed 3 -- provider write sources. Program-global and cheap; a written array
    // is flattened and handed to a provider writer emitted elsewhere.
    let providerSeeds =
        ctx.ProviderWrites |> Map.toList |> List.map (fun (_, spec) -> spec.SourceId)
    // Function-scope return seed. A scalar/unit return provably cannot carry
    // array storage, so it contributes nothing -- without that narrowing
    // `return reduce(B, +)` would pin every `|> compute` temporary in the helper
    // and the whole feature would be inert inside functions. The two LIFTED
    // return forms are skipped too: they are handled by suppressing the `__retN`
    // NAME at the splice point, and seeding their inputs would defeat the point.
    let retSeeds =
        match kind with
        | LoopScope | FuncScope None -> []
        | FuncScope (Some retExpr) ->
            match retExpr with
            | IRCompute (IRApplyCombinator _) | IRArrayLit _ -> []
            // `return f(x, y, z)` where f is a FreshPool callee: the returned
            // wrapper is a fresh pool with its own extents, so it can alias
            // none of the argument bindings -- and the fallthrough return arm
            // evaluates the call into a local (`auto __rv = f(...);`) BEFORE
            // the scope frees are emitted, so x/y/z are dead by the time they
            // are freed. Seeding them only pinned one array per helper call
            // (measured on 09_qg_atmosphere: three 64x64 fields leaked per
            // H_single invocation, every timestep). NotFresh callees keep the
            // wide seeding: their return may hand back an argument itself.
            | IRApp (f, _, _) when freshReturnOf f = FreshPool -> []
            // An INTERIOR VIEW of a scope-local array (`return traj(k)`). The
            // return arm materializes the slice into its own pool before these
            // frees are emitted, so the base is ordinary scope-owned storage
            // from here on. Seeding it would pin the WHOLE array for the life
            // of the program -- and since a function frame runs per CALL, that
            // is an unbounded leak, not a one-off (measured: a 10000x4
            // trajectory per `integrate` invocation). Paired with
            // genFuncBodyScoped's matching arm through returnedInteriorView;
            // narrowing here WITHOUT that arm would be a use-after-free.
            | IRIndex _ when (returnedInteriorView scopeLets retExpr).IsSome -> []
            // A returned `stack(...)` / `join(...)`: genFuncBodyScoped's
            // statement-shaped return arm materializes it into a FRESH `__retN`
            // pool (genStackJoinBinding: a dense alloc plus a per-source copy;
            // never a view) and only THEN emits the scope frees, so every
            // source is dead before it is freed. Seeding the sources pinned the
            // whole step state a time-stepping function stacks (measured on
            // examples/09_qg_atmosphere: `bstep` returned `stack(q1n, q2n, h1n,
            // g1, h2n, j1)` and leaked its four computed fields, 64 KB each,
            // every step -- 1.4 GB over the run). `sequence` is NOT in this
            // arm: it ALIASES its children (isFreshPoolForm says so).
            | IRStack _ | IRJoin _ -> []
            | _ ->
                match inferExprType retExpr with
                | IRTScalar _ | IRTUnit -> []
                | _ -> collectVarRefsIR retExpr |> Set.toList
    // A SCALAR-typed let holds no storage, so nothing it read can be aliased
    // through it: propagation stops there exactly as at a fresh pool. Without
    // this barrier a kernel capturing `m` in `let m = reduce(y, (+)) / n`
    // seeded m (capture seed), propagation walked into m's value, and y --
    // merely READ to compute a scalar -- was pinned and leaked on every call
    // (measured: the demean / normalize shape leaked its input-sized pool
    // per invocation).
    let scalarValued (value: IRExpr) =
        match inferExprType value with
        | IRTScalar _ | IRTUnit -> true
        | _ -> false
    let rec propagate (acc: Set<IRId>) =
        let acc' =
            scopeLets |> List.fold (fun s (id, value) ->
                if Set.contains id s && not (isFreshPoolForm value) && not (scalarValued value)
                then Set.union s (collectVarRefsIR value)
                else s) acc
        // Scratch reuse: an escaping reuser's storage IS its donor's, so the
        // donor escapes with it (chains resolve to the root by iteration).
        let acc'' =
            Blade.Types.PoolReuseTable.donors ()
            |> Map.fold (fun s reuser donor -> if Set.contains reuser s then Set.add donor s else s) acc'
        if acc'' = acc then acc else propagate acc''
    propagate (Set.ofList (assignSeeds @ captureSeeds @ providerSeeds @ retSeeds))

/// Hoist FreshPool-returning calls out of ARGUMENT position into fresh lets.
/// `g(f(x))` evaluates f's fresh array as a C++ temporary no let ever names,
/// so the scope tracker cannot register it and its pool leaks every call
/// (measured on 09_qg_atmosphere: `__spectra_1(uhat(ll, ph))` and
/// `__spectra_2(flux(u, 0.0, q))` each dropped a 64x64 field per timestep).
/// Hoisted into `let t = f(x)`, the binding takes the ordinary site-3/3b
/// registration and scope-exit free. Guards are exactly site 3's: FreshPool
/// callee, dense + nullptr, no symmetry -- anything else stays inline (and
/// keeps leaking rather than risking a wrong free). Only IRApp trees are
/// walked: a lambda in argument position must keep its body expression-shaped.
/// C++ argument evaluation order is unspecified, so serializing sibling
/// arguments through lets changes nothing observable.
let hoistFreshPoolCallArgs (builder: IRBuilder) (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
    let hoistable (arg: IRExpr) =
        match arg with
        | IRApp (f, _, _) when freshReturnOf f = FreshPool ->
            (match inferExprType arg with
             | ArrayElem at ->
                 isFreeableDenseArrayType at
                 && classifyOutputStorage (inferExprType arg) = AllocDense
                 && not (hasRealSymmetry (buildSymmVec (inferExprType arg)))
             | _ -> false)
        | _ -> false
    let rec go (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRApp (f, args, ty) ->
            let processed = args |> List.map go
            let innerLets = processed |> List.collect fst
            let (extraLets, finalArgs) =
                processed |> List.fold (fun (ls, acc) (_, a) ->
                    if hoistable a then
                        let tmp = builder.FreshId()
                        (ls @ [(tmp, a)], acc @ [IRVar (tmp, inferExprType a)])
                    else (ls, acc @ [a])) ([], [])
            (innerLets @ extraLets, IRApp (f, finalArgs, ty))
        | _ -> ([], e)
    go e

/// One tracked allocation, with everything needed to free it recorded at
/// REGISTRATION time. TemplateArgs in particular is stored verbatim rather than
/// recomputed at scope exit, so a later hoist cannot make the free's template
/// arguments drift from the allocation's.
type TrackedAlloc =
    /// allocate<> / allocate_strict<> pool + pointer skeleton, freed by the
    /// mirrored runtime routine. OwnedExtentsName is Some only where this site
    /// itself did `new size_t[R]` (genApplyCombinator and the fused leaves);
    /// static-constexpr and borrowed-alias extents are None.
    | PoolAlloc of
        ArrayName: string *
        Routine: string *
        TemplateArgs: string *
        OwnedExtentsName: string option *
        OwnerBindingId: IRId option
    /// Streamed `_fb_p` fiber buffers: plain `delete[] <name>;`.
    /// Registered by the streamedNestSetup call sites when a frame is live.
    | RawAlloc of Name: string * OwnerBindingId: IRId option
    /// An `Array<T,1> N = { new T[n], ext }` whose backing is a raw `new[]`
    /// rather than an allocate<> pool (the mask / sort / unique / union /
    /// intersect family): `delete[] N.data;`. The tracked NAME is the wrapper
    /// `N`, not `N.data`, so the return-suppression token test in genFuncBody
    /// still recognises `return N;`. OwnedExtentsName mirrors PoolAlloc's --
    /// Some where the site did its own `new size_t[R]` -- and is deleted under
    /// the SAME spare/free decision as the backing, which is the whole reason
    /// it is a field here instead of a separate RawAlloc registration.
    | RawArrayData of Name: string * OwnedExtentsName: string option * OwnerBindingId: IRId option
    /// Ragged / compound teardown. Unlike PoolAlloc there is no template-argument
    /// mirror to preserve: the ragged and compound layouts have no per-level span
    /// formula, so the free is a fixed runtime call whose arguments are all NAMES
    /// chosen at the emission site. Storing the fully rendered argument list
    /// (rather than a wrapper name plus a convention) is what keeps a site that
    /// shares its input's tables from being freed as though it owned them.
    /// Name is the wrapper (for suppression/token tests); Routine is the
    /// unqualified nested_array_utilities function; Args the complete call text.
    | ShapedAlloc of
        Name: string *
        Routine: string *
        Args: string *
        OwnerBindingId: IRId option

let internal trackedAllocName (t: TrackedAlloc) : string =
    match t with
    | PoolAlloc (n, _, _, _, _) -> n
    | RawAlloc (n, _) -> n
    | RawArrayData (n, _, _) -> n
    | ShapedAlloc (n, _, _, _) -> n

let internal trackedAllocOwner (t: TrackedAlloc) : IRId option =
    match t with
    | PoolAlloc (_, _, _, _, o) -> o
    | RawAlloc (_, o) -> o
    | RawArrayData (_, _, o) -> o
    | ShapedAlloc (_, _, _, o) -> o

type AllocScopeKind = SFunc | SLoop

/// One live scope's bookkeeping. Mutable because registration happens deep inside
/// string-building code that cannot thread a return value back out; the frame is
/// reached through the AsyncLocal stack instead.
type AllocScope = {
    Kind: AllocScopeKind
    /// Newest FIRST (prepend). Plain iteration at pop time is therefore
    /// registration-REVERSE order, which is what guarantees a result that borrows
    /// an operand's `.extents` is freed before that operand.
    mutable Allocs: TrackedAlloc list
    mutable Escapes: Set<IRId>
    mutable SuppressNames: Set<string>
    mutable CurrentOwner: IRId option
    /// Streamed fiber buffers (`_fb_p*`) DECLARED while this frame was live.
    /// Their C++ names die with the frame's closing brace, so the pop (and the
    /// exception-path truncate) retires them from streamBufDeclsCell -- a later
    /// nest in a different scope must re-declare, not reference a dead name.
    mutable StreamBufNames: Set<string>
}

let internal allocScopeStackStorage =
    System.Threading.AsyncLocal<AllocScope list ref>()

let allocScopeStackCell () : AllocScope list ref =
    let v = allocScopeStackStorage.Value
    if isNull (box v) then
        let fresh = ref []
        allocScopeStackStorage.Value <- fresh
        fresh
    else v

/// Fresh refs, never a mutation of the one found (see CodeGenState.freshCell).
let resetAllocScopeStack () : unit =
    freshCell allocScopeStackStorage []

/// This file's per-program cells; called at every program-assembly entry.
let resetLoopNestCells () : unit =
    freshCell streamBufDeclsStorage Set.empty
    resetAllocScopeStack ()

let currentAllocScope () : AllocScope option =
    match (allocScopeStackCell ()).Value with
    | top :: _ -> Some top
    | [] -> None

let allocScopeDepth () : int = (allocScopeStackCell ()).Value.Length

/// Drop any frames left above `depth`. A codegen path that raises (a
/// BladeDiagnosticException from a provider or backend limit) must not leave a
/// stale frame active: the NEXT module-level binding's allocations would register
/// into it and its frees would be emitted in the wrong scope.
let truncateAllocScopeStack (depth: int) : unit =
    let cell = allocScopeStackCell ()
    let n = cell.Value.Length
    if n > depth then
        // Frames dropped on an exception path still retire their streamed
        // fiber-buffer names from the program-wide dedup set (see
        // AllocScope.StreamBufNames) -- the declarations they cover were never
        // (or will never be) emitted into a scope the next binding can see.
        let sc = streamBufDeclsCell ()
        for f in cell.Value |> List.take (n - depth) do
            sc.Value <- Set.difference sc.Value f.StreamBufNames
        cell.Value <- cell.Value |> List.skip (n - depth)

let pushAllocScope (kind: AllocScopeKind) (escapes: Set<IRId>) : unit =
    let cell = allocScopeStackCell ()
    let frame = { Kind = kind; Allocs = []; Escapes = escapes
                  SuppressNames = Set.empty; CurrentOwner = None; StreamBufNames = Set.empty }
    cell.Value <- frame :: cell.Value

let popAllocScope () : AllocScope option =
    let cell = allocScopeStackCell ()
    match cell.Value with
    | top :: rest -> cell.Value <- rest; Some top
    | [] -> None

// deallocArgsFor (the free-side mirror of emitAllocRhs) lives in CodeGenState.

/// Record an allocation against the innermost live scope. An EMPTY stack (main's
/// top level) is a silent no-op -- that single rule is what keeps module bindings
/// alive for auto-print / EXPECT pins without a per-site guard.
let registerAlloc (t: TrackedAlloc) : unit =
    match currentAllocScope () with
    | None -> ()
    | Some frame ->
        let stamped =
            match t with
            | PoolAlloc (n, r, a, ex, _) -> PoolAlloc (n, r, a, ex, frame.CurrentOwner)
            | RawAlloc (n, _) -> RawAlloc (n, frame.CurrentOwner)
            | RawArrayData (n, ex, _) -> RawArrayData (n, ex, frame.CurrentOwner)
            | ShapedAlloc (n, r, a, _) -> ShapedAlloc (n, r, a, frame.CurrentOwner)
        frame.Allocs <- stamped :: frame.Allocs

/// The one call every allocation site makes. Template arguments are resolved
/// here, at the allocation, so the emitted free mirrors the emitted allocate
/// character for character. `extentsName` is consumed only for hoist naming, so
/// dense/`nullptr` sites may pass `<name>_extents` harmlessly.
let registerPoolAlloc
        (spec: AllocSpec) (elemType: string) (rank: int) (symmArg: string)
        (extentsName: string) (arrayName: string) (ownedExtents: string option) : unit =
    match currentAllocScope () with
    | None -> ()   // main top level: nothing is scope-freed
    | Some _ ->
        match deallocArgsFor spec elemType rank symmArg extentsName with
        | Ok (routine, args) -> registerAlloc (PoolAlloc (arrayName, routine, args, ownedExtents, None))
        | Error _ -> ()

/// Register the allocations a `materialize*Form` builder reported (site 7).
/// The template arguments come STRAIGHT from the descriptor -- the builder
/// already resolved SYMM / STRICT (hoisting the masks it needed), so this is a
/// character-for-character mirror of `EmitCpp.arrayAlloc` rather than a second
/// derivation from an AllocSpec. Statement-position consumers call this;
/// expression / IIFE consumers discard their descriptor list.
let registerMaterializedAllocs (ms: MaterializedAlloc list) : unit =
    match currentAllocScope () with
    | None -> ()   // main top level: nothing is scope-freed
    | Some _ ->
        for m in ms do
            match m with
            | MatPool (n, elem, rank, symm, strict, ownedExtents) ->
                let (routine, args) =
                    match strict with
                    | Some strictArg ->
                        ("deallocate_strict",
                         $"typename promote<{elem}, {rank}>::type, {symm}, {strictArg}")
                    | None ->
                        ("deallocate", $"typename promote<{elem}, {rank}>::type, {symm}")
                registerAlloc (PoolAlloc (n, routine, args, ownedExtents, None))
            | MatPoolSpec (n, spec, elem, rank, symm, extentsName, ownedExtents) ->
                match deallocArgsFor spec elem rank symm extentsName with
                | Ok (routine, args) -> registerAlloc (PoolAlloc (n, routine, args, ownedExtents, None))
                // The site emitted `#error`; there is no valid free for the pool.
                // The extents table IS valid (it was built before the refusal),
                // but a translation unit carrying `#error` never links, so
                // dropping it too keeps the two halves of a refused site
                // together rather than emitting a lone delete for a shape
                // nothing will read.
                | Error _ -> ()
            | MatRawData (n, ownedExtents) -> registerAlloc (RawArrayData (n, ownedExtents, None))
            | MatRawBuf n -> registerAlloc (RawAlloc (n, None))

/// Register a ragged/compound teardown (the W2 runtime routines). No
/// template-argument derivation: the emission site already knows the exact
/// call, and the rendered argument text is what keeps a site that SHARES its
/// input's tables (ragged map metadata, compound map idx) from being freed as
/// though it owned them. No-op with no live frame, exactly like
/// registerPoolAlloc (module bindings stay alive for auto-print / EXPECT pins).
let registerShapedAlloc (wrapperName: string) (routine: string) (args: string) : unit =
    match currentAllocScope () with
    | None -> ()
    | Some _ -> registerAlloc (ShapedAlloc (wrapperName, routine, args, None))

/// Register an allocation whose template arguments were emitted DIRECTLY through
/// `EmitCpp.arrayAlloc` (the CUDA / MPI host peels: their SYMM and STRICT masks
/// are hoisted locally, exactly like the materializer forms'). Mirrors arrayAlloc
/// argument for argument; `ownedExtents` is Some where the site did its own
/// `new size_t[R]`.
let registerArrayAlloc
        (arrayName: string) (elem: string) (rank: int)
        (symm: string) (strict: string option) (ownedExtents: string option) : unit =
    match currentAllocScope () with
    | None -> ()   // main top level: nothing is scope-freed
    | Some _ ->
        let (routine, args) =
            match strict with
            | Some strictArg ->
                ("deallocate_strict",
                 $"typename promote<{elem}, {rank}>::type, {symm}, {strictArg}")
            | None ->
                ("deallocate", $"typename promote<{elem}, {rank}>::type, {symm}")
        registerAlloc (PoolAlloc (arrayName, routine, args, ownedExtents, None))

/// Register the fiber destination buffers a streamedNestSetup call NEWLY
/// declared. Under a live frame each gets a scope-exit `delete[]`
/// (per-iteration recycle when the frame is a loop body) and is remembered for
/// dedup retirement at pop. With no frame (main top level) this is a no-op and
/// the buffer keeps its program lifetime. The buffers are plain host `new[]`
/// staging on every backend path, so the delete is uniformly safe.
let registerStreamBufDecls (names: string list) : unit =
    match currentAllocScope () with
    | None -> ()
    | Some frame ->
        for n in names do
            frame.StreamBufNames <- Set.add n frame.StreamBufNames
            registerAlloc (RawAlloc (n, None))

/// SCRATCH REUSE (Blade.Optimize.planPoolReuse, Types.PoolReuseTable): turn
/// `name`'s pool declaration -- `Array<E, R> name = { allocate<typename
/// promote<E, R>::type, nullptr>(ext), ext };`, the one line every dense
/// allocation site emits for a plan-admitted shape -- into an alias of the
/// donor's data with the reuser's own extents table. Anchored on the NAME,
/// so it is indifferent to which emitter wrote the line. The flag says
/// whether a line matched: the caller spares a scope free ONLY then, so an
/// unexpected declaration shape keeps its own pool and its own free rather
/// than leaking.
let rewritePoolAlias (lines: string list) (name: string) (donor: string) : string list * bool =
    let pat =
        System.Text.RegularExpressions.Regex(
            @"^(\s*)Array<(.+?), (\d+)> " + System.Text.RegularExpressions.Regex.Escape name
            + @" = \{ allocate<typename promote<.+?>::type, nullptr>\((\w+)\), (\w+) \};\s*$")
    let mutable matched = false
    let out =
        lines |> List.map (fun l ->
            let m = pat.Match l
            if m.Success && not matched then
                matched <- true
                $"{m.Groups.[1].Value}Array<{m.Groups.[2].Value}, {m.Groups.[3].Value}> {name} = {{ {donor}.data, {m.Groups.[4].Value} }}; /* pool reuse: {name} takes {donor}'s dead pool */"
            else l)
    (out, matched)

/// Exempt one emitted C++ name from this scope's frees (used for the lifted
/// `__retN` return temporaries, whose storage leaves with the return value).
let suppressAllocName (n: string) : unit =
    match currentAllocScope () with
    | None -> ()
    | Some frame -> frame.SuppressNames <- Set.add n frame.SuppressNames

/// A cursor into the live frame's registration list, and its companion that
/// spares EVERYTHING registered after it (`suppressAllocsSince`).
///
/// Why the pair exists rather than another `suppressAllocName` call. A RETURN
/// position lifts its value into a `__retN` and must spare that value's storage
/// from the scope's frees, but "that value's storage" is not always one pool
/// named `__retN`: `materializeEighForm` declares TWO under derived names
/// (`__retN__q` / `__retN__lam`) and binds `__retN` to a make_tuple of the two
/// wrappers, and `genSequenceBinding` declares one per child (`__retN_0`, ...).
/// Sparing only the binding name emitted `deallocate(...)` for the real pools
/// and then returned wrappers pointing at freed memory -- a use-after-free the
/// caller reads as garbage (eigh reproduces it whenever the BLAS route is live;
/// the synthesized Jacobi fallback hides it by never taking this arm).
///
/// Naming conventions are not a sound basis for the spare decision -- every
/// multi-pool form invents its own suffixes, and the next one to be added would
/// reintroduce the bug silently. What IS sound is the registration record
/// itself: an allocation emitted while the return binding was being rendered
/// belongs to the returned value. Mark the list, render, spare the delta.
///
/// The trade this makes is deliberate and one-directional. A form that
/// registers a genuine SCRATCH pool inside its own emission would now leak that
/// pool for the frame's lifetime instead of freeing it. A leak bounded by one
/// call is strictly preferable to handing the caller a dangling pointer, and
/// the frame's other allocations (everything registered before the mark) are
/// unaffected, so nothing that used to be freed on the ordinary let path stops
/// being freed.
///
/// The FRAME is captured with the cursor rather than re-read at the suppress
/// call: an emitter that pushed a nested scope and left it live would otherwise
/// stamp the suppression onto the wrong frame.
let allocRegistrationMark () : (AllocScope * int) option =
    match currentAllocScope () with
    | None -> None
    | Some frame -> Some (frame, List.length frame.Allocs)

let suppressAllocsSince (mark: (AllocScope * int) option) : unit =
    match mark with
    | None -> ()
    | Some (frame, before) ->
        let now = List.length frame.Allocs
        // Newest-FIRST list, so the delta is the prefix.
        if now > before then
            for a in frame.Allocs |> List.truncate (now - before) do
                frame.SuppressNames <- Set.add (trackedAllocName a) frame.SuppressNames

let registeredAllocNames () : string list =
    match currentAllocScope () with
    | None -> []
    | Some frame -> frame.Allocs |> List.map trackedAllocName

/// Stamp the owning binding id for allocations emitted from here on. Set ONLY by
/// the two scope drivers (genFuncBody's per-let fold, genForRangeBinding's body
/// fold) -- never re-stamped by genBinding -- so every allocation emitted while
/// one source-level let is being rendered (including __lhs / __rhs / __s1 /
/// __gbody sub-temporaries minted with fresh ids) carries THAT let's id.
/// Suppression is therefore all-or-nothing per let, which is what keeps a result
/// borrowing an operand's `.extents` pointer from outliving that operand's free.
let setAllocOwner (owner: IRId option) : unit =
    match currentAllocScope () with
    | None -> ()
    | Some frame -> frame.CurrentOwner <- owner

/// Whole-identifier-token occurrence test. Used only by genFuncBody's fallthrough
/// return arm, where the returned expression is already rendered to text and the
/// question is whether it NAMES a registered allocation (`return r;`,
/// `return std::make_tuple(r0, r1);`) -- a substring test would false-positive on
/// `r_extents` or `rows`.
let containsIdentToken (text: string) (name: string) : bool =
    System.Text.RegularExpressions.Regex.IsMatch(
        text,
        "(?<![A-Za-z0-9_])" + System.Text.RegularExpressions.Regex.Escape(name) + "(?![A-Za-z0-9_])")

/// Render one frame's frees at `ind`. Iteration is over the newest-first
/// list, i.e. registration-reverse order. An allocation is skipped when its
/// owning let escapes or its name was suppressed. An empty survivor list
/// emits no lines at all, so unaffected programs are byte-identical.
let internal renderFrameFrees (ind: string) (frame: AllocScope) : string list =
    frame.Allocs
    |> List.collect (fun a ->
        let ownerEscapes =
            match trackedAllocOwner a with
            | Some oid -> Set.contains oid frame.Escapes
            | None -> false
        if ownerEscapes || Set.contains (trackedAllocName a) frame.SuppressNames then []
        else
            match a with
            | PoolAlloc (n, routine, args, ownedExtents, _) ->
                [ $"{ind}{routine}<{args}>({n}.data, {n}.extents);" ]
                @ (match ownedExtents with
                   | Some ex -> [$"{ind}delete[] {ex};"]
                   | None -> [])
            | RawAlloc (n, _) -> [$"{ind}delete[] {n};"]
            | RawArrayData (n, ownedExtents, _) ->
                [ $"{ind}delete[] {n}.data;" ]
                @ (match ownedExtents with
                   | Some ex -> [$"{ind}delete[] {ex};"]
                   | None -> [])
            | ShapedAlloc (_, routine, args, _) ->
                [ $"{ind}nested_array_utilities::{routine}({args});" ])

/// Pop the innermost scope and render its frees at `ind`.
let popAllocScopeFrees (ind: string) : string list =
    match popAllocScope () with
    | None -> []
    | Some frame ->
        // Retire this frame's streamed fiber-buffer declarations from the
        // program-wide dedup set: their C++ names die at this scope's closing
        // brace, so a later nest must re-declare its own buffer.
        let sc = streamBufDeclsCell ()
        sc.Value <- Set.difference sc.Value frame.StreamBufNames
        renderFrameFrees ind frame

/// Render the innermost frame's frees at `ind` WITHOUT popping -- the early-exit
/// path of a `while`-guarded rec-array loop (IRBreakIf) leaves the loop before
/// the bottom-of-body frees run, so the break emits the frees accumulated SO FAR
/// this iteration and then breaks. The frame stays live (the non-breaking path
/// still frees at the bottom), and streamed fiber-buffer declarations are NOT
/// retired (their C++ names remain in scope past the break).
let peekAllocScopeFrees (ind: string) : string list =
    match currentAllocScope () with
    | None -> []
    | Some frame -> renderFrameFrees ind frame

/// A7c (gap G69, W2-4 review finding 2): free ONE pool now, at an inlined
/// callee's IRReleasePools return event, and spare it from
/// the scope's own frees.
///   * a live frame that tracks `n` (a function body / loop scope): its own
///     rendered free, the name then suppressed (no double free at scope exit);
///   * a live frame that does NOT track it (suppressed, escaping, unregistered)
///     : nothing -- whatever owned it before still owns it;
///   * no frame at all (main's top level, which frees nothing at exit): the
///     plain dense free `fallback` -- nothing else would ever free it.
let freeAllocNow (ind: string) (n: string) (fallback: string) : string list =
    match (allocScopeStackCell ()).Value with
    | [] -> [ ind + fallback ]
    | frames ->
        // innermost first: the frame that registered the pool owns its free
        match frames |> List.tryFind (fun f -> f.Allocs |> List.exists (fun a -> trackedAllocName a = n)) with
        | None -> []
        | Some frame ->
            if Set.contains n frame.SuppressNames then []
            else
                match frame.Allocs |> List.tryFind (fun a -> trackedAllocName a = n) with
                | Some a when (match trackedAllocOwner a with Some oid -> Set.contains oid frame.Escapes | None -> false) -> []
                | Some a ->
                    let lines = renderFrameFrees ind { frame with Allocs = [ a ]; Escapes = Set.empty; SuppressNames = Set.empty }
                    frame.SuppressNames <- Set.add n frame.SuppressNames
                    lines
                | None -> []

/// IRReleasePools is a statement, shared by the module, function and loop
/// renderers. Reuse is excluded for lifetime regions by the planner. Missing
/// pool names are a compiler error, never a silently retained allocation.
let releaseInlinePools (ind: string) (names: Map<IRId, string>) (pools: IRExpr list) (call: string) : string list =
    // BL7005 (the user's decision): this lane RETAINED an opt-in A7c kept
    // inline; once per region. Other regions (join-driven inlines, early
    // releases) never warn.
    Blade.Optimize.noteReleaseRetained "cpp" pools call
    pools |> List.rev |> List.collect (function
        | IRVar (pid, pty) ->
            let n =
                match Map.tryFind pid names with
                | Some n -> n
                | None -> failwith $"IRReleasePools: pool v{pid} has no emitted name"
            let elem =
                match stripUnits pty with
                | ArrayElem at when arrayRank at = 1 -> elemTypeToCpp at.ElemType
                | _ -> failwith "IRReleasePools: only owned rank-one dense pools are supported"
            let lines = freeAllocNow ind n $"deallocate<typename promote<{elem}, 1>::type, nullptr>({n}.data, {n}.extents);"
            if lines.IsEmpty then
                failwith $"IRReleasePools: pool {n} is not owned by the current allocation frame"
            $"{ind}// lifetime region `{call}` (an inlined call or an early release) closes here; its pool `{n}` is released at that return boundary" :: lines
        | _ -> failwith "IRReleasePools: an owned pool must be a binding reference")

/// Generate code to allocate and initialize an array from literal values
/// `sourceName` is the binding's BLADE name, for the runtime guard's message
/// (the C++ spelling `varName` can be a rename, `a_extents_` / `M__x`).
let genArrayLiteral (ctx: CodeGenContext) (varName: string) (sourceName: string) (elements: IRExpr list) (arrType: IRArrayType) : string list =
    let ind = indentStr ctx
    let elemType = elemTypeToCpp arrType.ElemType
    let rank = arrayRank arrType
    
    // Ragged literal path (RaggedIdx-tagged inner index): emits offsets table,
    // flat backing buffer, and row-pointer table from the literal's structure.
    // Storage is all STACK-allocated in the enclosing function's scope, which
    // works only while the literal doesn't outlive its declaration scope
    // (construct-print-exit in main()); returning it or storing it longer-lived
    // needs upgrading to `new T[total]` / `new T*[n]` heap allocation -- local
    // to this case, doesn't affect the type system.
    //
    // DepIdx-annotated literal path (DepIdx-inner-tagged index): the lens table
    // comes from evaluating the inner record's Extent formula (e.g. `Idx<3-i>`)
    // for each i; once computed, the runtime layout matches ragged
    // (`_lens`/`_offsets`/flat backing/row pointers), so downstream codegen uses
    // the same machinery. evalDepIdxExtent only reduces static arithmetic with
    // the outer IRVar substituted for `i`; formulas referencing free/runtime
    // values surface as a codegen error (runtime-extent formulas deferred).
    if isDepIdxArrayType arrType then
        // Find the outer record (its IRId is the one substituted for `i` in
        // the inner extent) and the inner record (carries the formula).
        let outerOpt =
            arrType.IndexTypes |> List.tryFind (fun idx -> idx.IxKind = IxKDepOuter)
        let innerOpt =
            arrType.IndexTypes |> List.tryFind (fun idx -> idx.IxKind = IxKDepInner)
        match outerOpt, innerOpt with
        | Some outer, Some inner ->
            let outerExtentOpt = tryEvalIntIR outer.Extent
            match outerExtentOpt with
            | None ->
                [refusalErrorLine ind ($"Blade codegen: DepIdx outer extent is not a compile-time integer for binding '{varName}'")]
            | Some n ->
                // Evaluate the inner formula for each i in [0..n).
                let lenResults =
                    [0 .. (int n) - 1]
                    |> List.map (fun i -> evalDepIdxExtent outer.Id i inner.Extent)
                if lenResults |> List.exists Option.isNone then
                    [refusalErrorLine ind ($"Blade codegen: DepIdx inner extent formula not statically evaluable for binding '{varName}' (runtime-extent formulas are not yet supported)")]
                else
                    let lens = lenResults |> List.map Option.get
                    // Verify literal row counts match the formula-computed lens.
                    let actualRowLengths = computeRaggedRowLengths elements
                    let mismatch =
                        actualRowLengths.Length <> lens.Length ||
                        List.zip actualRowLengths lens |> List.exists (fun (a, b) -> a <> b)
                    if mismatch then
                        let expected = lens |> List.map string |> String.concat ", "
                        let actual = actualRowLengths |> List.map string |> String.concat ", "
                        [refusalErrorLine ind ($"Blade codegen: DepIdx literal row lengths [{actual}] do not match formula-computed lens [{expected}] for binding '{varName}'")]
                    else
                        let total = lens |> List.sum
                        let allValues = extractLiteralValues (IRArrayLit (elements, arrType))
                        if allValues.Length <> total then
                            [refusalErrorLine ind ($"Blade codegen: DepIdx literal value count ({allValues.Length}) does not match sum of formula-computed lens ({total}) for binding '{varName}'")]
                        else
                            // Layout is identical to ragged from here on.
                            let nRows = lens.Length
                            let lensList = lens |> List.map string |> String.concat ", "
                            let offsets = lens |> List.scan (fun acc len -> acc + len) 0
                            let offsetsList = offsets |> List.map string |> String.concat ", "
                            // Float elements need round-trip literals (see
                            // floatToCppLiteral); integral elements keep the
                            // bare spelling (a `.0` suffix would be a C++
                            // narrowing error in the braced initializer).
                            let renderFlat = renderLiteralLeaf elemType
                            let flatValues = allValues |> List.map renderFlat |> String.concat ", "
                            let extentsDecl = $"{ind}static constexpr const size_t {varName}_extents[1] = {{{nRows}}};"
                            let lensDecl = $"{ind}static constexpr const size_t {varName}_lens[{nRows}] = {{{lensList}}};"
                            let offsetsDecl = $"{ind}static constexpr const size_t {varName}_offsets[{nRows + 1}] = {{{offsetsList}}};"
                            let flatDecl = $"{ind}{elemType} {varName}__flat[{total}] = {{{flatValues}}};"
                            // Row pointer array (stack-allocated). The Ragged
                            // wrapper holds a pointer to this array.
                            let rowPtrsDecl = $"{ind}{elemType}* {varName}__rows[{nRows}];"
                            let rowPtrsInit =
                                [ $$"""{{ind}}for (size_t __ri = 0; __ri < {{nRows}}; __ri++) {"""
                                  // Reads from the static-constexpr global declared above; the wrapper isn't yet constructed.
                                  $"{ind}    {varName}__rows[__ri] = &{varName}__flat[{varName}_offsets[__ri]];"
                                  $"{ind}}}" ]
                            // Wrap into Ragged<T>: data + extents + lens + offsets.
                            let wrapperDecl = $"{ind}Ragged<{elemType}> {varName} = {{ {varName}__rows, {varName}_extents, {varName}_lens, {varName}_offsets }};"
                            [extentsDecl; lensDecl; offsetsDecl; flatDecl; rowPtrsDecl] @ rowPtrsInit @ [wrapperDecl]
        | _ ->
            [refusalErrorLine ind ($"Blade codegen: DepIdx array type missing outer or inner record for binding '{varName}' (typechecker bug)")]
    elif isRaggedArrayType arrType then
        let rowLengths = computeRaggedRowLengths elements
        let n = rowLengths.Length
        let total = rowLengths |> List.sum
        // Flat list of all element values, in row-major order
        let allValues = extractLiteralValues (IRArrayLit (elements, arrType))
        if allValues.Length <> total then
            // Sanity check: number of leaf values must match sum of row lengths.
            [refusalErrorLine ind ($"Blade codegen: ragged literal value count ({allValues.Length}) does not match sum of row lengths ({total}) for binding '{varName}'")]
        else
            let lensList = rowLengths |> List.map string |> String.concat ", "
            let offsets =
                rowLengths |> List.scan (fun acc len -> acc + len) 0
            let offsetsList = offsets |> List.map string |> String.concat ", "
            // Same float/integral literal split as the DepIdx branch above.
            let renderFlat = renderLiteralLeaf elemType
            let flatValues = allValues |> List.map renderFlat |> String.concat ", "
            let extentsDecl = $"{ind}static constexpr const size_t {varName}_extents[1] = {{{n}}};"
            let lensDecl = $"{ind}static constexpr const size_t {varName}_lens[{n}] = {{{lensList}}};"
            let offsetsDecl = $"{ind}static constexpr const size_t {varName}_offsets[{n + 1}] = {{{offsetsList}}};"
            let flatDecl = $"{ind}{elemType} {varName}__flat[{total}] = {{{flatValues}}};"
            // Row pointer array (stack-allocated, separate name from the
            // wrapper so they don't collide). The Ragged<T> wrapper bundles
            // it with the lens/offsets/extents.
            let rowPtrsDecl = $"{ind}{elemType}* {varName}__rows[{n}];"
            let rowPtrsInit =
                [ $$"""{{ind}}for (size_t __ri = 0; __ri < {{n}}; __ri++) {"""
                  // Reads from the static-constexpr `<name>_offsets` global
                  // declared just above. The Ragged wrapper itself isn't yet
                  // in scope -- it's constructed AFTER this loop runs.
                  $"{ind}    {varName}__rows[__ri] = &{varName}__flat[{varName}_offsets[__ri]];"
                  $"{ind}}}" ]
            let wrapperDecl = $"{ind}Ragged<{elemType}> {varName} = {{ {varName}__rows, {varName}_extents, {varName}_lens, {varName}_offsets }};"
            [extentsDecl; lensDecl; offsetsDecl; flatDecl; rowPtrsDecl] @ rowPtrsInit @ [wrapperDecl]
    elif arrType.IndexTypes |> List.exists (fun ix ->
             ix.Rank >= 2 &&
             (match ix.Symmetry with
              | SymSymmetric | SymAntisymmetric | SymHermitian -> true
              | SymNone | SymWreath -> false)) then
        // COMPACT path: a SymIdx / AntisymIdx / HermitianIdx group is ONE axis
        // over r dimensions whose stored cells are a left-justified simplex.
        // Two things separate it from the rectangular path below:
        //
        //   1. The extents table is the group's COMPONENT extents (n repeated
        //      r times), not the literal's own row lengths. `computeArrayDims`
        //      reads the first row, which for a strict (antisym) group is n-1
        //      wide -- an extents table that would make every compact read
        //      compute the wrong storage bound.
        //   2. The allocation carries the group's SYMM mask (and DIAGONALS =
        //      false for antisym), so `allocate<>` builds the shrinking
        //      skeleton the literal was checked against.
        //
        // The ASSIGNMENTS are then the plain nested ones: `checkCompactArrayLit`
        // accepted the literal only in the storage's own shape, so the literal's
        // index path IS the storage coordinate -- `A[1][0]` is the canonical cell
        // (1,1) for SymIdx<2,n> (canon_left_justify: p1 = p0 + c1).
        let ty = mkArrayLike arrType
        let componentExtents =
            arrType.IndexTypes |> List.collect (fun ix -> List.replicate (max 1 ix.Rank) ix.Extent)
        let rank = componentExtents.Length
        let extentInts =
            componentExtents |> List.map (fun e ->
                match e with IRLit (IRLitInt n) -> Some (int n) | _ -> None)
        let rec walkLeaves (idxPath: int list) (e: IRExpr) : (int list * IRExpr) list =
            match e with
            | IRArrayLit (children, _) ->
                children |> List.mapi (fun i c -> walkLeaves (idxPath @ [i]) c) |> List.concat
            | leaf -> [(idxPath, leaf)]
        let leaves = walkLeaves [] (IRArrayLit (elements, arrType))
        if extentInts |> List.exists Option.isNone then
            [refusalErrorLine ind ($"Blade codegen: compact array literal for '{varName}' needs compile-time extents")]
        elif leaves |> List.exists (fun (path, _) -> path.Length <> rank) then
            // Array-VALUED leaves (a computed row) would need a deep copy into a
            // shrinking row; the checker refuses that shape, so reaching here is
            // a front-end change, not user input.
            [refusalErrorLine ind ($"Blade codegen: compact array literal for '{varName}' has a leaf at the wrong depth (typechecker bug)")]
        else
            let extentsName = $"{varName}_extents"
            // `static constexpr`, exactly like the rectangular literal path
            // below and unlike the fill_random compact path (whose stack table
            // is safe only because fill_random is confined to a top-level let):
            // the Array wrapper keeps a POINTER to this table, and a literal is
            // also the statement form of a FUNCTION RETURN -- a stack table
            // would dangle the moment the frame died, handing the caller
            // garbage extents rather than a compile error.
            let extentsDecl =
                sprintf "%sstatic constexpr const size_t %s[%d] = { %s };" ind extentsName rank
                    (extentInts |> List.map (Option.get >> string) |> String.concat ", ")
            let symmVec = buildSymmVec ty
            let symmArg =
                if hasRealSymmetry symmVec then hoistSymmDecl ($"{varName}_symm") symmVec
                else "nullptr"
            let allocLines =
                match emitAllocRhs (classifyOutputStorage ty) elemType rank symmArg extentsName with
                | Ok rhs -> [$"{ind}Array<{elemType}, {rank}> {varName} = {rhs};"]
                | Error msg -> [refusalErrorLine ind ($"compact literal '{varName}': {msg}")]
            let initCode =
                leaves |> List.map (fun (path, leaf) ->
                    let suffix = path |> List.map (sprintf "[%d]") |> String.concat ""
                    $"{ind}{varName}{suffix} = {(exprToCpp ctx.VarNames leaf)};")
            [extentsDecl] @ allocLines @ initCode
    else
        // Rectangular path: existing behavior.
        let structuralDims = computeArrayDims (IRArrayLit (elements, arrType))
        // Rows-of-computed-arrays: when the literal's nesting is shallower
        // than the declared rank -- elements are array-VALUED expressions
        // (e.g. `method_for(..) |> compute` results bound to names) rather
        // than nested bracket literals -- computeArrayDims only sees the
        // bracket levels. The missing inner extents come from the array
        // type's trailing IndexTypes (the typechecker has already verified
        // each element against exactly those index types). Without this,
        // the extents table was emitted short ({2} for a rank-2 array, so
        // extents[1] read as 0) and every downstream shape consumer -- the
        // auto-print loop, method_for fibers over rows -- saw zero-length
        // rows: a silent miscompile (M = [], prodsum = 0).
        let dims =
            if structuralDims.Length >= rank then structuralDims
            elif arrType.IndexTypes |> List.forall (fun ix -> ix.Rank = 1) then
                let tail =
                    arrType.IndexTypes
                    |> List.skip structuralDims.Length
                    |> List.map (fun ix ->
                        match ix.Extent with
                        | IRLit (IRLitInt n) -> Some (int n)
                        | _ -> None)
                if tail |> List.forall Option.isSome
                then structuralDims @ (tail |> List.map Option.get)
                else structuralDims
            else structuralDims
        if dims.IsEmpty then
            [$"{ind}// Empty array literal"]
        elif dims.Length < rank then
            // Inner extents couldn't be recovered statically (parametric or
            // compound index types). Refuse loudly rather than emit the
            // short-extents table that silently reads as empty.
            [refusalErrorLine ind ($"Blade codegen: array literal for '{varName}' nests {dims.Length} level(s) but the declared rank is {rank}, and the missing inner extents are not static -- bind the rows to a fully-literal array or annotate with static Idx<n> extents")]
        else
            // Generate extents declaration
            let extentsValues = dims |> List.map string |> String.concat ", "
            let extentsDecl = $"{ind}static constexpr const size_t {varName}_extents[{rank}] = {{{extentsValues}}};"
            
            // Generate allocation as Array<T,N> wrapper. Single brace-init
            // bundles the data pointer (from allocate<>) with the extents
            // pointer (the static-constexpr global emitted above).
            let allocDecl =
                arrayAlloc { Ind = ind; Elem = elemType; Rank = rank; Name = varName
                             Symm = "nullptr"; Strict = None; Extents = varName + "_extents" }
            // Deterministic deallocation, site 4: the rectangular literal path
            // only. The extents table is `static constexpr` (never owned); the
            // ragged/DepIdx literal paths above and the #error paths never reach
            // here. Symm is nullptr by construction, so the free mirrors trivially.
            if isFreeableDenseArrayType arrType then
                registerPoolAlloc AllocDense elemType rank "nullptr" (varName + "_extents") varName None

            // Generate initialization
            let values = extractLiteralValues (IRArrayLit (elements, arrType))
            
            // Decide between fast scalar path and per-element expression path.
            // The fast path uses `%g` formatting and is correct only when all
            // elements extracted to a complete float list (length matches the
            // dim product). Struct literals, computed values, or any element
            // that extractLiteralValues returns nothing for falls into the
            // expression path, which renders via exprToCpp (handles
            // IRStructLit, IRApp, etc. uniformly).
            //
            // Pre-Phase-D, the fast path was the only path: when any element
            // wasn't a scalar literal, no init was emitted at all (silent
            // miscompile reading uninitialized memory).
            let totalExpected = dims |> List.fold (*) 1
            let useFastScalarPath = values.Length = totalExpected && totalExpected > 0
            
            // Generalize over rank N. The two paths diverge only in how each
            // leaf is rendered:
            //   - fast path: %g-formatted floats from extractLiteralValues,
            //     in row-major order
            //   - per-element path: walk the nested IRArrayLit, render each
            //     leaf via exprToCpp (covers struct lits, computed values)
            // Both produce assignments of the form `name[i_0][i_1]...[i_n-1] = E;`.
            //
            // Pattern follows extractLiteralValues / computeArrayDims -- recurse
            // through IRArrayLit nesting, treating non-IRArrayLit nodes as
            // leaves. The old rank-1 / rank-2 / TODO-for-higher dispatch is
            // gone; rank-3+ now works at parity with rank-1 and rank-2.
            let rec enumerateIndexPaths (ds: int list) : int list list =
                match ds with
                | [] -> [[]]
                | n :: rest ->
                    let tails = enumerateIndexPaths rest
                    [for i in 0 .. n - 1 do
                        for t in tails do
                            yield i :: t]
            let rec walkLeaves (idxPath: int list) (e: IRExpr) : (int list * IRExpr) list =
                match e with
                | IRArrayLit (children, _) ->
                    children
                    |> List.mapi (fun i c -> walkLeaves (idxPath @ [i]) c)
                    |> List.concat
                | leaf -> [(idxPath, leaf)]
            let formatIndexPath (path: int list) : string =
                path |> List.map (sprintf "[%d]") |> String.concat ""
            
            // A literal of CONSTANT scalar leaves (a baked table: spectra's
            // twiddles and chirps, a data literal) is one `static const`
            // initializer copied into the pool, not one statement per element
            // (F6): O(1) statements, the table lives in read-only data, and a
            // function that bakes one initializes it once. Dense rectangular
            // pools are contiguous in row-major order (pool_base), so the
            // flat initializer is the cell order.
            let constLeaf (e: IRExpr) =
                match e with
                | IRLit _ -> true
                | IRUnaryOp (IRNeg, IRLit _) -> true
                | IRComplex ((IRLit _ | IRUnaryOp (IRNeg, IRLit _)), (IRLit _ | IRUnaryOp (IRNeg, IRLit _))) -> true
                | _ -> false
            let leaves = walkLeaves [] (IRArrayLit (elements, arrType))
            let staticInit =
                isFreeableDenseArrayType arrType && totalExpected >= 2
                && (match stripUnits arrType.ElemType with
                    | IRTScalar et -> et <> ETString
                    | _ -> false)
                && (useFastScalarPath
                    || (leaves.Length = totalExpected
                        && leaves |> List.forall (fun (path, leaf) -> path.Length = rank && constLeaf leaf)))
            let initCode =
                if staticInit then
                    let cells =
                        if useFastScalarPath then values |> List.map (renderLiteralLeaf elemType)
                        else leaves |> List.map (fun (_, leaf) -> exprToCpp ctx.VarNames leaf)
                    let joined = String.concat ", " cells
                    [ $"{ind}static const {elemType} {varName}__init[{totalExpected}] = {{ {joined} }};"
                      $"{ind}std::copy_n({varName}__init, {totalExpected}, nested_array_utilities::pool_base({varName}.data));" ]
                elif useFastScalarPath then
                    // Row-major enumeration of (i0,...,i(n-1)) tuples zipped with
                    // the flat value list. extractLiteralValues already walks
                    // in row-major order, so the alignment is exact.
                    let paths = enumerateIndexPaths dims
                    List.zip paths values |> List.map (fun (path, v) ->
                        // Exact per-domain spelling (renderLiteralLeaf): an
                        // integer leaf never detours through double (the
                        // old float list stored 2^53+1 as 2^53).
                        $"{ind}{varName}{(formatIndexPath path)} = {(renderLiteralLeaf elemType v)};")
                else
                    // Per-element path: walk the nested IRArrayLit. Index path
                    // accumulates as we descend; leaves render via exprToCpp.
                    walkLeaves [] (IRArrayLit (elements, arrType))
                    |> List.collect (fun (path, leaf) ->
                        if path.Length >= rank then
                            [$"{ind}{varName}{(formatIndexPath path)} = {(exprToCpp ctx.VarNames leaf)};"]
                        else
                            // Array-valued leaf (a computed row): deep-copy the
                            // remaining dims elementwise. Assigning the Array
                            // wrapper into the row slot would alias the row to
                            // the source's buffer instead of copying (and for
                            // deeper rank gaps doesn't even compile).
                            //
                            // The loop bounds come from the DECLARED extents;
                            // annotation-vs-actual extent mismatches are not
                            // (yet) rejected by unify for computed arrays, so
                            // guard each copied dim at runtime -- a mismatch
                            // must be a loud exit(1), not an OOB read.
                            let subDims = dims |> List.skip path.Length
                            let srcName = $"""__cpsrc_{(path |> List.map string |> String.concat "_")}"""
                            let loopVars = subDims |> List.mapi (fun j _ -> $"__cp{j}")
                            let idxSuffix = loopVars |> List.map (sprintf "[%s]") |> String.concat ""
                            let srcDecl =
                                $"{ind}    const auto& {srcName} = ({(exprToCpp ctx.VarNames leaf)});"
                            let guards =
                                subDims |> List.mapi (fun j n ->
                                    $"{ind}    if ({srcName}.extents[{j}] != {n}) {{ std::cerr << \"Blade runtime: array literal row {(formatIndexPath path)} of '{sourceName}' has extent \" << {srcName}.extents[{j}] << \" in dim {j}, but the declared type expects {n}\" << std::endl; blade_rt::panic(\"BL8006\", \"array literal extent mismatch\", {(declPanicArgs ())}); }}")
                            let opens =
                                List.zip loopVars subDims
                                |> List.mapi (fun j (v, n) ->
                                    $$"""{{ind}}    {{(String.replicate j "    ")}}for (size_t {{v}} = 0; {{v}} < {{n}}; {{v}}++) {""")
                            let body =
                                sprintf "%s    %s%s%s%s = %s%s;"
                                    ind (String.replicate subDims.Length "    ")
                                    varName (formatIndexPath path) idxSuffix
                                    srcName idxSuffix
                            let closes =
                                [for j in subDims.Length - 1 .. -1 .. 0 -> $"""{ind}    {(String.replicate j "    ")}}}"""]
                            [$"{ind}{{"] @ [srcDecl] @ guards @ opens @ [body] @ closes @ [$"{ind}}}"])
            
            [extentsDecl; allocDecl] @ initCode

/// Generate code for a scalar binding
let genScalarBinding (ctx: CodeGenContext) (name: string) (value: IRExpr) (ty: IRType) : string list =
    let ind = indentStr ctx
    // Defense for upstream type-inference cache misses: when the binding's
    // declared type is IRTInfer or IRTUnit but the value isn't actually
    // unit-valued, re-derive the type from the value via inferExprType.
    // This catches cases where the lift pass's structFieldsCache may have
    // missed an IRFieldAccess and labeled the binding IRTUnit.
    let resolvedTy = 
        match ty with 
        | IRTInfer _ -> inferExprType value 
        | IRTUnit when not (isUnitExpr value) ->
            let inferred = inferExprType value
            if inferred = IRTUnit then ty else inferred
        | t -> t
    // No `auto <name> = <expr>;` fallback for a shape-bearing RHS (IRMask/
    // IRSort/IRVar/IRFieldAccess/IRIntersect/IRUnion) that resolves to
    // IRTUnit: those RHS shapes always resolve to a non-IRTUnit type by this
    // point (there is ONE struct-fields cache -- IR.fs's AsyncLocal registry,
    // which `setCodegenStructFieldsCache` merely forwards into -- and it is
    // per-flow specifically so parallel tasks can't race and return a stale
    // IRTUnit). If a regression reaches this branch anyway, the
    // expression-statement form below deliberately produces invalid C++
    // rather than papering over it with auto-deduction -- diagnose the
    // upstream resolution bug instead.
    // A reference to a variable that IS unit -- the result of a provider write
    // (`let saved = csv.write(...)`), or a name bound to one. Such a variable
    // has no C++ declaration (the write is a statement, not a value), so the
    // expression-statement form below would name a symbol that does not exist:
    // `let again = saved`, or a bare `saved` at top level (its `__exprN`
    // desugar), passed typecheck and died in g++ as BL9002. There is nothing
    // to evaluate and nothing to print, so it emits nothing.
    //
    // Keyed on the VARIABLE's own type, not the binding's: that keeps the
    // deliberate tripwire below intact for the case it exists for, a binding
    // mislabeled unit whose value is really a shape-bearing variable.
    let isUnitVarRef =
        match value with
        | IRVar (_, (IRTUnit | IRTScalar ETUnit)) -> true
        | IRVar (id, _) when Map.containsKey id ctx.ProviderWrites -> true
        | _ -> false
    match resolvedTy with
    | IRTUnit | IRTScalar ETUnit when isUnitVarRef -> []
    | IRTUnit ->
        if isUnitExpr value then
            []
        else
            // Genuinely unit-valued: emit as expression statement
            let valueStr = exprToCppCtx ctx value
            [$"{ind}{valueStr};"]
    | _ ->
        // Array-typed bindings render as Array<T,N>/Ragged<T> wrappers when the
        // RHS produces one (IRFieldAccess, IRVar resolving to a wrapper,
        // IRMask/IRSort/IRIntersect/IRUnion, and IRApp -- function calls
        // returning IRTArray emit `Array<T, N>` at genFuncDef, so the let-bound
        // result must match, not the raw `promote<T, N>::type` pointer that
        // would lose `.extents`). RHSes producing bare pointers (IRIndex
        // peeling a sub-array, IRApp returning T*) render bare instead, to
        // avoid a brace-init mismatch.
        //
        // Rank-1 ragged-family SUB-VIEW binding (`let row = r(i)` on a ragged/
        // DepIdx parent, or `let g0 = grouped(i)` on group_by): binding as a
        // raw T* would lose the row LENGTH, so downstream length-dependent ops
        // (reduce, extents, print) lack accessors. Bind as RaggedRow<T>
        // instead: a ragged/DepIdx parent's operator[] already returns
        // RaggedRow{ptr,len}; a grouped parent's length comes from the
        // group_keys offsets table (ctx.GroupedArrays), same as the peel path.
        let raggedRowSubview =
            match value, resolvedTy with
            | IRIndex (parent, [idxExpr], _), ArrayElem rowTy when isRaggedRowType rowTy ->
                let valueStr = exprToCppCtx ctx value
                let elemStr = elemTypeToCpp rowTy.ElemType
                let isGroupRow = rowTy.IndexTypes.[0].IxKind = IxKGroupMember
                if isGroupRow then
                    let parentName = exprToCppCtx ctx parent
                    match Map.tryFind parentName ctx.GroupedArrays with
                    | Some gkName ->
                        let idxStr = exprToCppCtx ctx idxExpr
                        Some [$"{ind}RaggedRow<{elemStr}> {name} = {{ {valueStr}, {gkName}__offsets[({idxStr}) + 1] - {gkName}__offsets[{idxStr}] }};"]
                    | None -> None  // grouped parent not registered (non-var producer): fall through to raw
                else
                    Some [$"{ind}RaggedRow<{elemStr}> {name} = {valueStr};"]
            | _ -> None
        if raggedRowSubview.IsSome then raggedRowSubview.Value
        else
        // Plain dense PARTIAL positional read: `let r0 = A(i)` supplies FEWER
        // subscripts than the array's rank, so the result is a row/slab sub-view
        // (residual rank >= 1), not a scalar. The raw path below would bind it as
        // `promote<T, R>::type` -- a bare data pointer that has lost `.extents`,
        // so every downstream `.extents` consumer (auto-print, method_for/zip)
        // fails to compile. Bind the Array<T, R> wrapper directly, mirroring the
        // loop-peel slice idiom (genElementBindingNew): the data pointer steps
        // through the consumed leading dims and the extents pointer shifts past
        // them, e.g. `Array<double,1> r0 = { A.data[0L], A.extents + 1 };`.
        //
        // Scoped by subviewAxesOk: consumed axes plain rank-1 (so the
        // consumed-dims count equals the subscript count and the extents shift
        // is exact), residual axes plain or WHOLE compact groups (`T(r)` of
        // `Idx<m>, SymIdx<2, n>` is the packed row a standalone group holds; it
        // used to fall to the raw path and bind a `promote<>::type` pointer,
        // BL9002). Compound partial reads take the IRTuple arm in
        // producesWrapper; ragged/dep-idx rows take raggedRowSubview above; and
        // a subscript INTO a compact group is refused at typecheck
        // (compactCoordWalk) -- all excluded by the axis predicate.
        // NOT gated on `ix.Kind = SDimension` -- Kind describes one apply, not
        // the value's storage (see densePartialSubviewExpr's note; both twins
        // must answer identically or a let-bound slice and an inline slice
        // diverge in shape).
        let densePartialSubview =
            match value, resolvedTy with
            | IRIndex (arr, indices, _), ArrayElem residTy
                    when not (List.isEmpty indices)
                         && indices |> List.forall (function IRTuple _ -> false | _ -> true) ->
                // Residual axes may also be WHOLE compact groups
                // (subviewAxesOk, shared with densePartialSubviewExpr).
                match inferExprType arr with
                | ArrayElem arrTy when subviewAxesOk arrTy indices.Length ->
                    let arrStr = exprToCppCtx ctx arr
                    let subscripts =
                        indices
                        |> List.map (fun i -> $"[{(exprToCppCtx ctx i)}]")
                        |> String.concat ""
                    Some [$"{ind}{(cppArrayTypeStr residTy)} {name} = {{ {arrStr}.data{subscripts}, {arrStr}.extents + {indices.Length} }};"]
                | _ -> None
            | _ -> None
        if densePartialSubview.IsSome then densePartialSubview.Value
        else
        let rec producesWrapperOf value =
            match value with
            | IRFieldAccess _ -> true
            | IRVar _ -> true                // assume wrapper (most producers migrated)
            | IRMask _ | IRSort _ | IRIntersect _ | IRUnion _ | IRUnique _ -> true
            | IRApp _ -> true                // function-call returns wrapped Array
            | IRTupleProj _ -> true          // tuple elements carry wrappers (irTypeToCpp IRTTuple)
            // An array-valued SELECT is a wrapper exactly when both branches
            // are -- it renders as a C++ ternary, whose type is the branches'
            // common type. This is what a recursive array's out-of-prefix lag
            // read binds (`if n - 3 >= 0 then __lag0_m else __zs0_m`, both
            // Array<T, N> wrappers); declaring it on the raw `promote<>::type`
            // path instead would drop the extents every `.data`/`.extents`
            // consumer downstream needs. Branch DISAGREEMENT keeps the raw
            // path, so no arm gets a wrapper it cannot produce.
            | IRIf (_, t, e) -> producesWrapperOf t && producesWrapperOf e
            | IRIndex (a, (IRTuple coords) :: _, _) ->
                // A PARTIAL sparse read (formalism 3.5) produces a wrapper:
                // Sparse<T, RR> for a residual-rank >= 2 read
                // (make_partial_sparse_gather) or Array<T, 1|2> for a dense
                // rank-1 residual (make_sparse_gather_dense[_trail]). A FULL
                // tabulated read stays on the raw path (scalar, or a
                // trailing-row T* sub-view via .row()), as does ordinary
                // positional peeling on non-tabulated arrays. (Compound heads
                // never classify as partial since the flat-subscript
                // conversion -- the compound arm here is vestigial but
                // harmless, and keeps the predicate kind-agnostic.)
                (match inferExprType a with
                 | ArrayElem at when isCompoundArrayType at || isSparseArrayType at ->
                     let k =
                         at.IndexTypes
                         |> List.tryFind (fun ix -> ix.IxKind = IxKCompound || ix.IxKind = IxKSparse)
                         |> Option.map (_.Rank)
                         |> Option.defaultValue coords.Length
                     (match classifyCompoundIndexTuple k coords with
                      | CompoundPartial _ -> true
                      | CompoundFull -> false)
                 | _ -> false)
            | _ -> false
        let producesWrapper = producesWrapperOf value
        let cppType =
            match resolvedTy with
            | ArrayElem arr when producesWrapper -> cppArrayTypeStr arr
            | _ -> irTypeToCpp resolvedTy
        let valueStr = exprToCppCtx ctx value
        // Deterministic deallocation, site 3: `let r = f(a)` where the CALLEE
        // allocated the pool. Restricted to FreshPool callees -- a NotFresh return
        // may hand back its own parameter, so `r.data == a.data` and registering
        // `r` would double-free. Restricted further to AllocDense with no real
        // symmetry: the callee allocated with ITS output type and we only know the
        // binding's, and for a symmetric/strict return a disagreement is heap
        // corruption rather than a compile error. Dense + nullptr is the one
        // combination that cannot disagree. The extents came from inside the callee
        // and travel with the wrapper, so nothing is owned here.
        // The `IRVar` arm of producesWrapper (plain `let b = a`, a wrapper copy
        // sharing the pool) is deliberately NOT covered.
        (match value, resolvedTy with
         | IRApp (fn, _, _), ArrayElem at when
                producesWrapper
                && freshReturnOf fn = FreshPool
                && isFreeableDenseArrayType at
                && classifyOutputStorage resolvedTy = AllocDense
                && not (hasRealSymmetry (buildSymmVec resolvedTy)) ->
             registerPoolAlloc AllocDense (elemTypeToCpp at.ElemType) (arrayRank at)
                 "nullptr" (name + "_extents") name None
         | _ -> ())
        // Deterministic deallocation, partial SPARSE reads: `let g = S((k, _))`
        // binds a gather that deep-copied into storage it OWNS (a fresh
        // buffer, plus a fresh index / row table / extents per shape), so the
        // binding owns it outright and its teardown is the producer's own
        // routine. Escapes and `return g;` spare it exactly as they spare a
        // pool (owner-id stamping + return-token suppression).
        (match partialSparseGatherTeardown value with
         | Some routine when producesWrapper -> registerShapedAlloc name routine name
         | _ -> ())
        [$"{ind}{cppType} {name} = {valueStr};"]

// Loop Application Code Generation

/// Build a simple (no symmetry) ApplyInfo for applying a unary kernel to arrays.
/// Used by >>@ and @>> to construct stage-2 pipeline applications.
let defaultIndexType () = { Id = 0; Rank = 1; Extent = IRLit (IRLitInt 0); Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
/// Build a default IRArrayType.
let defaultArrayType (et: IRType) = { ElemType = et; IndexTypes = [defaultIndexType ()]; IsVirtual = false; Identity = None }

let buildSimpleApplyInfo (arrays: IRExpr list) (kernel: IRExpr) (outputType: IRType) : ApplyInfo =
    let arrayTypes = arrays |> List.map (fun a -> 
        match inferExprType a with 
        | ArrayElem arr -> arr 
        | _ -> defaultArrayType (IRTScalar ETFloat64))
    let identities = arrays |> List.mapi (fun i _ -> AIDLiteral i)
    let sDims = arrayTypes |> List.map arrayRank
    let totalSDims = List.sum sDims
    {
        Loop = IRMethodFor {
            Arrays = arrays
            Identities = identities
            ArrayTypes = arrayTypes
            SDimsPerArray = sDims
            TotalSDims = totalSDims
            SharedIndexTypes = []
        }
        Kernel = kernel
        Arrays = arrays
        Identities = identities
        ArrayTypes = arrayTypes
        SharedIndexTypes = []
        SymcomStates = List.replicate totalSDims SCNeither
        TriangularLevels = List.replicate totalSDims false
        SDimsPerArray = sDims
        KernelInputRanks = List.replicate (List.length arrays) 0
        KernelOutputRank = 0
        KernelTDims = []
        SpeedupFactor = 1L
        ReynoldsSpeedup = 1L
        HasReynolds = false
        OutputType = outputType
        IsCoIteration = false
    }

