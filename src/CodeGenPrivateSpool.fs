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

/// THE one validity predicate: the API (`validatePolicy`) and the
/// BLADE_PRIVATE_SPOOL parser both ask it, so nothing the parser accepts can
/// throw later. BlockCells 0 = the source chunk edge (resolved after the
/// certificate, and re-checked against 1..1048576 there).
let policyProblem (policy: PrivateSpoolPolicy) : string option =
    let root = policy.Root
    if String.IsNullOrEmpty root || root.Length >= 240 || root.Length < 3
       || not (Char.IsAsciiLetter root.[0]) || root.[1] <> ':' || (root.[2] <> '\\' && root.[2] <> '/')
       || (root |> Seq.exists (fun c -> c < ' ' || c > '~')) then
        Some "the root must be an absolute local-drive ASCII directory path shorter than 240 characters"
    elif policy.BlockCells > 1048576UL then Some "block cells must be 0 (the source chunk edge) or 1..1048576"
    elif policy.TrackedWorkspaceBytes = 0UL then Some "the tracked workspace allowance (mem) must be positive"
    elif policy.LogicalDiskBytes = 0UL then Some "the serialized disk allowance (disk) must be positive"
    else None

let validatePolicy (policy: PrivateSpoolPolicy) =
    match policyProblem policy with
    | Some why ->
        invalidArg "options.PrivateSpool" ("private-spool policy requires an absolute local ASCII drive root, positive tracked workspace/serialized disk allowances and 0 (the chunk edge) or 1..1048576 block cells: " + why)
    | None -> ()

/// BLADE_PRIVATE_SPOOL: the DECLARED grant, read per call and ONLY by the
/// CLI verb dispatch (run / compile / emit / plan), never by a toolchain file,
/// the REPL, a notebook, the IDE render lane or a test helper. Its VALUE is
/// the policy -- `root=<abs dir>,mem=<size>,disk=<size>[,resident=<size>]`,
/// size = bytes with an optional K / M / G (binary) suffix -- so setting it
/// names the scratch root and the budgets; nothing is inferred. unset / empty /
/// `0` / `off` / `false` = no grant (Ok None). Anything else that does not
/// parse is an Error the verb reports as a hard failure: a typo'd root never
/// silently runs resident.
let parseKnob (raw: string) : Result<PrivateSpoolPolicy option, string> =
    let v = if isNull raw then "" else raw.Trim()
    match v.ToLowerInvariant() with
    | "" | "0" | "off" | "false" -> Ok None
    | _ ->
    let size (key: string) (s: string) : Result<uint64, string> =
        let s = s.Trim()
        let mult, digits =
            if s.EndsWith "K" || s.EndsWith "k" then 1024UL, s.Substring(0, s.Length - 1)
            elif s.EndsWith "M" || s.EndsWith "m" then 1048576UL, s.Substring(0, s.Length - 1)
            elif s.EndsWith "G" || s.EndsWith "g" then 1073741824UL, s.Substring(0, s.Length - 1)
            else 1UL, s
        match UInt64.TryParse(digits, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
        | true, n when n <= UInt64.MaxValue / mult -> Ok (n * mult)
        | _ -> Error $"`{key}={s}` is not a byte count (digits with an optional K, M or G suffix)"
    let pairs = v.Split(',') |> Array.map (fun p -> p.Trim()) |> Array.filter (fun p -> p <> "")
    let parsed =
        pairs |> Array.fold (fun acc p ->
            match acc with
            | Error e -> Error e
            | Ok (m: Map<string, string>) ->
                match p.IndexOf '=' with
                | i when i > 0 ->
                    let k = p.Substring(0, i).Trim().ToLowerInvariant()
                    let x = p.Substring(i + 1).Trim()
                    if not (List.contains k [ "root"; "mem"; "disk"; "resident" ]) then Error $"unknown key `{k}` (root, mem, disk, resident)"
                    elif m.ContainsKey k then Error $"key `{k}` given twice"
                    else Ok (Map.add k x m)
                | _ -> Error $"`{p}` is not key=value") (Ok Map.empty)
    match parsed with
    | Error e -> Error e
    | Ok m ->
    match Map.tryFind "root" m, Map.tryFind "mem" m, Map.tryFind "disk" m with
    | None, _, _ -> Error "missing root=<absolute local-drive directory>"
    | _, None, _ -> Error "missing mem=<tracked workspace allowance>"
    | _, _, None -> Error "missing disk=<serialized disk allowance>"
    | Some root, Some mem, Some disk ->
    match size "mem" mem, size "disk" disk with
    | Error e, _ | _, Error e -> Error e
    | Ok memB, Ok diskB ->
    let resident = match Map.tryFind "resident" m with Some r -> size "resident" r | None -> Ok memB
    match resident with
    | Error e -> Error e
    | Ok residentB ->
    let policy = { Root = root; TrackedWorkspaceBytes = memB; LogicalDiskBytes = diskB; BlockCells = 0UL; ResidentBytes = residentB }
    match policyProblem policy with
    | Some why -> Error why
    | None -> Ok (Some policy)

/// The CLI's grant, read per call (never cached).
let knobPolicy () : Result<PrivateSpoolPolicy option, string> =
    parseKnob (Environment.GetEnvironmentVariable "BLADE_PRIVATE_SPOOL")

/// The selection's candidate lines, shared by the retain decline and the
/// spool record: retain (the resident two-pass form), replay (never proved
/// in v1), spool (D06). `chunk` = the resolved source chunk edge.
let selectionEvidence (p: Plan) (chunk: int64) : string list =
    let resident = 8UL * uint64 p.Cells
    [ $"retain: the resident intermediate p is {resident} bytes (8 N, the logical pool; the resident route also holds its decode windows and run table -- a heuristic, not a bound) against the resident allowance {p.Policy.ResidentBytes} bytes"
      "replay: not proved -- the source is a `.stream` provider read by path; its run-time identity is version (size + mtime), not content, so a second traversal is not proved to read the same values, and a second provider pass would add a failure site after the statistic that the two-pass program cannot have (no provider supplies a snapshot proof for `.stream` today)"
      $"spool: D06 private sequential spool under root `{p.Policy.Root}`, tracked workspace {p.Policy.TrackedWorkspaceBytes} bytes, serialized disk {p.Policy.LogicalDiskBytes} bytes, block = the source chunk edge {chunk} cells" ]

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
            // The run record's private_spool member (blade_run_record.hpp):
            // noted only after finish succeeded (scratch retired), with the
            // PLANNED reservation (reserved_disk() is 0 once idle) and the grant.
            "    { const auto __ps_c = ps::counters();"
            $"      blade_rr::note_private_spool({escapeStringLit p.Terminal.Name}, {p.Cells}ULL, {chunk}ULL, __ps_plan.blocks, __ps_c.payload_written, __ps_c.payload_read, __ps_c.logical_disk_high_water, __ps_plan.reserved_disk, {root}, {p.Policy.TrackedWorkspaceBytes}ULL, {p.Policy.LogicalDiskBytes}ULL); }}"
            "    return __ps.pending; // only publication, after replay/EOF/owned scratch retirement; normal print follows"
            "}();" ]
    Ok (lines |> List.map (fun line -> indentStr ctx + line))
