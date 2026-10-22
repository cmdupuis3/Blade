// Exactly one policy-bound statistical-barrier region. All strings/preflight
// complete before CodeGen commits omission of any original binding.
module Blade.CodeGenPrivateSpool

open System
open Blade.Types
open Blade.IR
open Blade.CodeGenState
open Blade.CodeGenExpr
open Blade.CodeGenExprSupport
open Blade.OptimizePrivateSpool

let record (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Rule="private-statistic-spool"; Version=1; Span=decisionSpan (); Subject=subject; Outcome=outcome; Evidence=evidence }

let validatePolicy (policy: PrivateSpoolPolicy) =
    let root = policy.Root
    if String.IsNullOrEmpty root || root.Length >= 240 || root.Length < 3
       || not (Char.IsAsciiLetter root.[0]) || root.[1] <> ':' || (root.[2] <> '\\' && root.[2] <> '/')
       || (root |> Seq.exists (fun c -> c < ' ' || c > '~'))
       || policy.BlockCells = 0UL || policy.BlockCells > 1048576UL
       || policy.TrackedWorkspaceBytes = 0UL || policy.LogicalDiskBytes = 0UL then
        invalidArg "options.PrivateSpool" "private-spool policy requires an absolute local ASCII drive root, positive tracked workspace/serialized disk allowances and 1..1048576 block cells"

let fpSupported () =
    fpReproEnabled ()
    && String.Equals(Environment.GetEnvironmentVariable "BLADE_FP_CONTRACT", "off", StringComparison.OrdinalIgnoreCase)

let checkEmissionOrder (ctx: CodeGenContext) (bindings: IRBinding list) =
    match ctx.PrivateSpoolRegion with
    | Some region when not (emissionOrderMatches region.Plan bindings) ->
        record region.Plan.Producer.Name (Blade.Effects.Declined "actual emitted binding order differs from the certified adjacent region") []
        {ctx with PrivateSpoolRegion=None}
    | _ -> ctx

let tryGenerate (ctx: CodeGenContext) (p: Plan) : Result<string list,string> =
    match ctx.StreamTerminalModule with
    | None -> Error "whole-program certificate unavailable"
    | Some modul when not (stillValid p modul) -> Error "stale graph or overlapping storage certificate"
    | Some _ when splitTimingModeEnabled () -> Error "split timing placement is not admitted"
    | Some _ when not (fpSupported ()) -> Error "requires reproducible serial folds AND explicit BLADE_FP_CONTRACT=off"
    | Some _ when printSelection () <> Some (Set.singleton p.Terminal.Name) -> Error "print demand changed after certification"
    | Some _ when not ctx.TilePlans.IsEmpty || not ctx.TileReads.IsEmpty -> Error "revision tile ownership is not admitted"
    | Some _ ->
    match Blade.ZarrProvider.CppZarr.rank1TerminalChunk p.SourceSpec with
    | Error why -> Error why
    | Ok (chunk, _) when uint64 chunk <> p.Policy.BlockCells -> Error "policy block cells must equal the source chunk edge"
    | Ok (chunk, _) ->
    let sourceName = Map.tryFind p.Source.Id ctx.VarNames
    match sourceName with
    | None -> Error "source has no registered identity"
    | Some source when Map.tryFind source ctx.StreamedArrays <> Some p.SourceSpec -> Error "source prologue/capability is not active"
    | Some source ->
    match Blade.ZarrProvider.CppZarr.genPrivateSpoolRows p.SourceSpec source "__ps.window.get()"
                "__ps.decode.get()" chunk "__ps_lo" "__ps_hi" with
    | Error why -> Error why
    | Ok readLines ->
    let producerNames = Map.add p.ProducerKernel.Params.Head.VarId "__ps.window[__ps_i]" ctx.VarNames
    let replayNames =
        ctx.VarNames |> Map.add p.TransformKernel.Params.Head.VarId "__ps.window[__ps_i]"
                     |> Map.add p.Barrier.Id "__ps.mean"
    let barrier = mapIRExpr (function IRExtent (IRVar (id,_),0) when id = p.Producer.Id -> IRLit (IRLitInt p.Cells) | e -> e) p.Barrier.Value
    let produced = exprToCpp producerNames p.ProducerKernel.Body
    let transformed = exprToCpp replayNames p.TransformKernel.Body
    let mean = exprToCpp (Map.add p.Statistic.Id "__ps.sum" ctx.VarNames) barrier
    let firstSeed = exprToCpp ctx.VarNames p.FirstSeed
    let secondSeed = exprToCpp ctx.VarNames p.SecondSeed
    let name = Blade.CodeGenFusion.bindingCppName p.Terminal
    let root = escapeStringLit p.Policy.Root
    let panic = declPanicArgs ()
    let lines =
        [ "// private-statistic-spool-v1: exact F64 cells; normal windows; policy-bound artifact"
          $"double {name} = [&]() -> double {{"
          "    namespace ps = blade_private_spool;"
          "    struct __ps_state {"
          "        std::unique_ptr<double[]> window, decode;"
          "        ps::Writer writer; ps::Sealed sealed; ps::Reader reader;"
          "        ps::Status first, cleanup; unsigned owner = 0; bool cleanup_attempted = false;"
          "        uint64_t count = 0; double sum, mean, pending;"
          "    };"
          "    __ps_state __ps; // tokens allocate nothing; capacities checked before allocations below"
          "    auto __ps_fail = [&](ps::Status status, const char* stage) -> void {"
          "        if (__ps.owner != 0) {"
          "            __ps.cleanup_attempted = true;"
          "            if (__ps.owner == 1) __ps.cleanup = __ps.writer.cancel();"
          "            else if (__ps.owner == 2) __ps.cleanup = __ps.sealed.cancel();"
          "            else __ps.cleanup = __ps.reader.cancel();"
          "        }"
          "        std::cerr << \"private spool \" << stage << \" code=\" << int(status.code) << \" os=\" << status.os_error << \" cleanup_attempted=\" << __ps.cleanup_attempted;"
          "        if (__ps.cleanup_attempted) std::cerr << \" cleanup=\" << int(__ps.cleanup.code) << \" cleanup_os=\" << __ps.cleanup.os_error;"
          "        std::cerr << std::endl; // report cleanup AFTER an explicit owned-token attempt; exit hook remains backstop"
          $"        blade_rt::panic(\"BL8016\", \"private intermediate resource or protocol failure\", {panic});"
          "    };"
          // Tracked owning buffers/fixed state only; provider stream internals,
          // allocator/filesystem metadata and normal printing are excluded.
          "    // Tracked allowance excludes provider stream internals/metadata and output-print buffers; no global RSS cap."
          $"    constexpr uint64_t __ps_extra = 16ULL * {chunk}ULL + sizeof(__ps_state) + sizeof(ps::Grant) + sizeof(ps::Plan) + 512ULL;"
          $"    if ({p.Policy.TrackedWorkspaceBytes}ULL < __ps_extra) __ps_fail({{ps::Code::memory_quota}}, \"aggregate workspace\");"
          $"    ps::Grant __ps_grant{{true, {root}, {p.Policy.TrackedWorkspaceBytes}ULL - __ps_extra, {p.Policy.LogicalDiskBytes}ULL, {p.Cells}ULL, {chunk}ULL, {p.SourceSpec.VarType.IndexTypes.Head.Id}ULL}};"
          "    ps::Plan __ps_plan;"
          "    auto __ps_status = ps::plan(__ps_grant, __ps_plan);"
          "    if (!__ps_status) __ps_fail(__ps_status, \"policy plan\");"
          "    if (!ps::supported()) __ps_fail({ps::Code::unsupported}, \"platform\");"
          $"    __ps.window.reset(new (std::nothrow) double[{chunk}ULL]);"
          $"    __ps.decode.reset(new (std::nothrow) double[{chunk}ULL]);"
          "    if (!__ps.window || !__ps.decode) __ps_fail({ps::Code::allocation}, \"retained source workspace\");"
          "    __ps_status = __ps.writer.begin(__ps_grant);"
          "    if (__ps_status || (__ps_status.code != ps::Code::busy && ps::reserved_disk() != 0)) __ps.owner = 1;"
          "    if (!__ps_status) __ps_fail(__ps_status, \"begin\");"
          $"    __ps.sum = {firstSeed};"
          $"    for (uint64_t __ps_next = 0; __ps_next < {p.Cells}ULL; ) {{"
          "        const uint64_t __ps_lo = __ps_next;"
          $"        const uint64_t __ps_remaining = {p.Cells}ULL - __ps_lo;"
          $"        const uint64_t __ps_step = std::min(uint64_t({chunk}ULL), __ps_remaining);"
          "        const uint64_t __ps_hi = __ps_lo + __ps_step;"
          "        __ps_next = __ps_hi; // positive capped advance, even in fault drain"
          "        // Original normal window assembly/validation runs even after append failure." ]
        @ (readLines |> List.map (fun line -> "        " + line))
        @ [ "        if (__ps.first) {"
            "            for (uint64_t __ps_i = 0; __ps_i < __ps_step; ++__ps_i) {"
            $"                const double __ps_cell = {produced};"
            "                __ps.window[__ps_i] = __ps_cell; // stored F64; contraction OFF required"
            "                __ps.sum = __ps.sum + __ps.window[__ps_i];"
            "            }"
            "            __ps_status = __ps.writer.append(__ps.window.get(), __ps_step);"
            "            if (!__ps_status) __ps.first = __ps_status;"
            "        }"
            "    }"
            "    if (!__ps.first) {"
            "        const auto __ps_primary = __ps.first;"
            "        __ps_fail(__ps_primary, \"append after original source drain\");"
            "    }"
            "    __ps_status = __ps.writer.seal(__ps.sealed);"
            "    if (__ps_status) __ps.owner = 2;"
            "    if (!__ps_status) __ps_fail(__ps_status, \"seal\");"
            $"    __ps.mean = {mean};"
            $"    __ps.pending = {secondSeed};"
            "    __ps_status = __ps.sealed.open(__ps.reader);"
            "    if (__ps_status) __ps.owner = 3;"
            "    if (!__ps_status) __ps_fail(__ps_status, \"replay open\");"
            "    for (;;) {"
            $"        __ps_status = __ps.reader.next(__ps.window.get(), {chunk}ULL, __ps.count);"
            "        if (!__ps_status) __ps_fail(__ps_status, \"replay validation\");"
            "        if (__ps.count == 0) break; // footer and EOF validated by this terminal read"
            "        for (uint64_t __ps_i = 0; __ps_i < __ps.count; ++__ps_i) {"
            $"            const double __ps_term = {transformed};"
            "            __ps.pending = __ps.pending + __ps_term;"
            "        }"
            "    }"
            "    __ps_status = __ps.reader.finish();"
            "    __ps.cleanup_attempted = true; __ps.cleanup = __ps_status;"
            "    if (__ps_status || ps::reserved_disk() == 0) __ps.owner = 0;"
            "    if (!__ps_status) __ps_fail(__ps_status, \"finish before publication\");"
            "    return __ps.pending; // only publication, after replay/EOF/owned scratch retirement; normal print follows"
            "}();" ]
    Ok (lines |> List.map (fun line -> indentStr ctx + line))
