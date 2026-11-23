// Membership hoisting, rule member-hoist, and its side table (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeMemberHoist

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage

/// One planned membership hoist (`planMembershipHoist`, below): the structure
/// over set `SetId` built before host `HostId`, read by the probes of `SetId`
/// in the host's kernels `Kernels` (and in the host's own emission, where a
/// lane inlines those kernels). Defined here, ahead of compaction fusion,
/// which reads it: the plan is the PROOF INPUT of T4c's membership-probe
/// certificate (`Admission` is the planner's own five-party record).
type MemberHoistPlan = {
    HostId: IRId
    HostName: string
    SetId: IRId
    SetName: string
    Kernels: Set<IRId>
    /// 0 = the chooser, 1 / 2 / 3 = BLADE_MEMBER_HOIST=linear / bitmap / hash.
    Forced: int
    /// The admission record (`membershipAdmits`' matrix) that let it through.
    Admission: string
    /// G185 (b): admitted, but NOTHING is built -- a literal set of at most
    /// 16 keys keeps the as-written scan. Only the admission readers
    /// (`admittedOf`: compaction fusion's probe certificate) see it; every
    /// build / probe / ownership reader (`plansOf`, `tryProbe`, `all`) skips it.
    ScanOnly: bool
}

/// AsyncLocal like CompactionTable; reset once per program in
/// `lowerTypedProgram` (let ids restart per compile).
module MemberHoistTable =
    open System.Threading
    let private cell = new AsyncLocal<Map<IRId, MemberHoistPlan list>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    let reset () = cell.Value <- Map.empty
    let record (p: MemberHoistPlan) =
        let s = get ()
        let prev = Map.tryFind p.HostId s |> Option.defaultValue []
        cell.Value <- Map.add p.HostId (prev @ [ p ]) s
    /// The structures to build before host `hostId`, in plan order.
    let plansOf (hostId: IRId) : MemberHoistPlan list =
        Map.tryFind hostId (get ()) |> Option.defaultValue [] |> List.filter (fun p -> not p.ScanOnly)
    /// Every ADMITTED probe of host `hostId`, scan-only ones included (the
    /// proof input of compaction fusion's membership-probe certificate).
    let admittedOf (hostId: IRId) : MemberHoistPlan list = Map.tryFind hostId (get ()) |> Option.defaultValue []
    /// The plan whose structure a probe of set `setId` may read while the
    /// declaration `declId` is being emitted: the host itself, or one of its
    /// probing kernels.
    let tryProbe (declId: IRId) (setId: IRId) : MemberHoistPlan option =
        get () |> Map.toSeq |> Seq.collect snd
        |> Seq.tryFind (fun p -> not p.ScanOnly && p.SetId = setId && (p.HostId = declId || p.Kernels.Contains declId))
    /// Every plan, hosts in id order then plan order.
    let all () : MemberHoistPlan list = get () |> Map.toList |> List.collect snd |> List.filter (fun p -> not p.ScanOnly)

// --- Membership hoisting (round 3, item B-T1; plan rule `member-hoist`) -----
//
// `contains(B, x)` is a linear scan of B. Inside a kernel run once per cell --
// a `mask(src, pred)` predicate, a map kernel, a fold kernel -- that is
// O(n M) compares for an n-cell host over an M-cell set (the semijoin shape
// `keep = mask(cust, lambda(c) -> contains(elig, c))`). The plan builds ONE
// search structure over B immediately before the HOST binding (the mask / map
// / fold whose kernels hold the probes), frees it right after, and each probe
// reads it: blade_member.h's linear / bitmap / hash representation, chosen
// DETERMINISTICALLY from (M, min, max) and the knob only (never from timing),
// all three answering exactly integer `==` against every cell. The IR is
// untouched: the interpreter runs the as-written scan, so `blade test interp`
// compares the as-written program with the hoisted physical form, and
// `blade test opt-diff` compares the gate off with it on.
//
// THE ONE TARGET-EVALUATION CONTRACT (every lane, hoisted or not): the set
// operand is evaluated first, then the target exactly ONCE, before any cell
// is read -- for an empty set too (the interpreter's Loops.fs order; the
// C++ IIFE used to interpolate the target inside the scan, once per scanned
// cell and never for an empty set). A failing target therefore fails before
// any element is read, in every lane.
//
// Shapes (v1): the host is an EAGER top-level binding (not a deferred map;
// of the side-table bindings only a `compound(dense, mask)`, whose site runs
// its inline mask -- the canonical semijoin); the probe sits directly in the body of a
// kernel the host's value names (mask predicate, map kernel, fold kernel),
// that kernel is referenced exactly once in the program (so it runs only
// inside the host), and it is neither a CUDA nor an MPI kernel; the set is a
// NAMED, earlier, STORED top-level binding -- rank 1, dense or a rank-1
// compound, Int64 keys -- that nothing assigns (and not `let mut`). A
// deferred set (a proved-pure producer forced once at the host) is v1.1.
//
// Admission `membershipAdmits` -- the five parties of the motion (the build
// moves up to the host's first statement), each through the shared write
// analysis (`scheduleMayWriteIn`, beside the conservative `cseFactsOf`): the
// set producer (its payload; stored, so not moved -- a writing producer still
// declines), the consumer kernel(s) holding the probes, the enclosing fold
// kernel(s) and seed(s), the host's whole payload (its source, kernels,
// seeds and every statement before its cell loop, all of which now run after
// the build), and the intervening bindings (none: the build is the host's
// first statement). A parallel (`where omp`) kernel is admitted -- the build
// is serial, before the team, and every thread only reads it -- once every
// `callbackTable` candidate of its function-valued parameters is proved
// write-free. A writing party declines to the as-written per-cell scan.
//
// Gate: BLADE_MEMBER_HOIST -- unset / `1` / `on` = the chooser; `linear`,
// `bitmap`, `hash` force a representation for A/B (a forced bitmap whose span
// exceeds its 2^26-bit cap is built as a hash; never an abort); `0` / `off`
// keeps the as-written scan everywhere (the analysis still runs and records
// `declined -- disabled by BLADE_MEMBER_HOIST`).

/// BLADE_MEMBER_HOIST's reading (per call).
type MemberHoistMode =
    | MemberHoistOff
    | MemberHoistAuto
    /// 1 = linear, 2 = bitmap, 3 = hash (blade_member.h's kind numbers).
    | MemberHoistForce of rep: int

let memberHoistMode () : MemberHoistMode =
    match Blade.Toolchain.envSwitch "BLADE_MEMBER_HOIST" with
    | Blade.Toolchain.SwitchOff -> MemberHoistOff
    | Blade.Toolchain.SwitchOther v ->
        match v.Trim().ToLowerInvariant() with
        | "linear" -> MemberHoistForce 1
        | "bitmap" -> MemberHoistForce 2
        | "hash" -> MemberHoistForce 3
        | _ -> MemberHoistAuto
    | _ -> MemberHoistAuto

let memberHoistEnabled () = memberHoistMode () <> MemberHoistOff

/// blade_member.h's representation numbers, spelled.
let memberRepName (k: int) : string =
    match k with
    | 1 -> "linear"
    | 2 -> "bitmap"
    | 3 -> "hash"
    | _ -> "none"

/// blade_member.h's chooser (`blade_member_choose_i64`) restated, for the plan
/// record of a LITERAL set: the PREFERRED representation (the build makes the
/// same choice at run time -- keep the two in step -- and an allocation it
/// cannot make degrades it, bitmap -> hash -> linear, reported by the build,
/// never by this function): M = 0 -> linear (before min / max exist); a forced
/// representation (a forced bitmap over a span past its 2^26-bit cap ->
/// hash); else M <= 16 -> none, the as-written scan (a LITERAL set that small is
/// not hoisted at all: `planMembershipHoist` declines it, G185), a span within the 2^26-bit
/// cap -> bitmap, otherwise hash. The span is the unsigned difference max - min,
/// never max - min + 1 (which overflows for the full Int64 range).
let memberChooseRep (m: int64) (lo: int64) (hi: int64) (forced: int) : int =
    if m <= 0L then 1
    else
        let spanM1 = uint64 hi - uint64 lo
        let fits = spanM1 < (1UL <<< 26)
        if forced = 1 then 1
        elif forced = 2 then (if fits then 2 else 3)
        elif forced = 3 then 3
        // G185 (b), the user's decision of 2026-11-08: a small set is the
        // as-written scan (0 = none: nothing built, the probe scans the set)
        elif m <= 16L then 0
        // the bitmap whenever
        // its span fits the 2^26-bit cap (it never lost to the pick in the
        // wave-5 controls), the hash beyond
        elif fits then 2
        else 3

/// Plan membership hoisting over the module's top-level bindings. Records
/// into `MemberHoistTable` and the decision collector (`member-hoist`); the
/// IR is untouched.
let planMembershipHoist (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let mode = memberHoistMode ()
    let enabled = mode <> MemberHoistOff
    let forced = match mode with MemberHoistForce k -> k | _ -> 0
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let callableById = allCallables |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let wrote (e: IRExpr) = (facts e).MayWrite || writes e
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "member-hoist"; Version = 1
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let arr = List.toArray modul.Bindings
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    // How many times each id is NAMED across every executable root: binding
    // payloads (values and side-table operands) and every callable body.
    let refCount = System.Collections.Generic.Dictionary<IRId, int>()
    let countRefs (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRVar (id, _) ->
                refCount.[id] <- (match refCount.TryGetValue id with | true, k -> k + 1 | _ -> 1)
            | _ -> ()) e
    for b in arr do
        for e in bindingPayload modul b do countRefs e
    for f in allCallables do countRefs f.Body
    let refsOf (id: IRId) = match refCount.TryGetValue id with | true, k -> k | _ -> 0
    let refs (e: IRExpr) = collectVarRefsIR e
    let assignsTo (id: IRId) (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n ->
            match n with
            | IRAssign (target, _) when Set.contains id (refs target) -> hit <- true
            | _ -> ()) e
        hit
    let assignedAnywhere (id: IRId) =
        (arr |> Array.exists (fun b -> bindingPayload modul b |> List.exists (assignsTo id)))
        || (allCallables |> List.exists (fun f -> assignsTo id f.Body))
    let isPlaceholder (b: IRBinding) =
        modul.CompoundInits.ContainsKey b.Id || modul.SparseInits.ContainsKey b.Id || modul.RandomInits.ContainsKey b.Id
    // THE POSITIVE STORAGE PROOF of a set at the build point: the shared
    // `storedArrayProof` (defined ahead of compaction fusion, which applies
    // it to the mask source and every companion source of a shared selection).
    let storedProof = storedArrayProof modul
    let storageOf (_: Set<IRId>) (b: IRBinding) : Result<string * IRBinding list, string> = storedProof b
    // The callback table over the module (parallel kernels' function-valued
    // parameters): computed once, on demand.
    let cbTable =
        lazy (
            let resolve (e: IRExpr) =
                match e with
                | IRVar (id, _) -> Map.tryFind id callableById
                | _ -> None
            callbackTable resolve allCallables (arr |> Array.map (fun b -> (b.Id, bindingPayloadExpr modul b)) |> List.ofArray))
    // A kernel reference resolved to its callable, through a top-level let
    // alias of a lambda (`let pred = lambda ..`, then `mask(xs, pred)`); the
    // alias's own id is kept, because every use of the alias runs the kernel.
    let aliasOf = System.Collections.Generic.Dictionary<IRId, IRId>()
    let resolveKernel (k: IRExpr) : IRCallable option =
        match fst (peelReynolds k) with
        | IRVar (id, _) as head ->
            match resolveCallableIn funcs head with
            | Some c -> Some c
            | None ->
                match Map.tryFind id indexOf with
                | Some i ->
                    match resolveCallableIn funcs arr.[i].Value with
                    | Some c ->
                        aliasOf.[c.Id] <- id
                        Some c
                    | None -> None
                | None -> None
        | other -> resolveCallableIn funcs other
    // How many evaluations can run a kernel: its own references, or -- for a
    // kernel reached through a let alias -- the alias's.
    let kernelRefs (c: IRCallable) =
        match aliasOf.TryGetValue c.Id with
        | true, a -> max (refsOf c.Id) (refsOf a)
        | _ -> refsOf c.Id
    // The kernels a host's value names, with their role: what runs per cell.
    let kernelsOf (v: IRExpr) : (IRCallable * string) list =
        let acc = ResizeArray<IRCallable * string>()
        let add (k: IRExpr) (role: string) =
            match resolveKernel k with
            | Some c when not (acc |> Seq.exists (fun (x, _) -> x.Id = c.Id)) -> acc.Add((c, role))
            | _ -> ()
        iterIRExpr (fun n ->
            match n with
            | IRMask (_, k) -> add k "mask predicate"
            | IRApplyCombinator info -> add info.Kernel "map kernel"
            | IRReduce (_, k, _) | IRReduceCompute (_, k, _) -> add k "fold kernel"
            | _ -> ()) v
        List.ofSeq acc
    // The enclosing folds of a host's value: (kernel, seed) per fold.
    let foldsOf (v: IRExpr) : (IRExpr * IRExpr option) list =
        let acc = ResizeArray<IRExpr * IRExpr option>()
        iterIRExpr (fun n ->
            match n with
            | IRReduce (_, k, init) -> acc.Add((k, init))
            | IRReduceCompute (_, k, init) -> acc.Add((k, Some init))
            | _ -> ()) v
        List.ofSeq acc
    let probesIn (c: IRCallable) : (IRExpr * IRExpr) list =
        let acc = ResizeArray<IRExpr * IRExpr>()
        iterIRExpr (fun n ->
            match n with
            | IRContains (a, x) -> acc.Add((a, x))
            | _ -> ()) c.Body
        List.ofSeq acc
    // The set's integer key class (v1: Int64, through units and index tags).
    let keyClass (t: IRType) : Result<bool, string> =
        match stripUnits t with
        | ArrayElem at ->
            let rec scalarOf (t: IRType) =
                match stripUnits t with
                | IRTIdxTagged (inner, _) -> scalarOf inner
                | IRTScalar e -> Some e
                | _ -> None
            if at.IndexTypes.Length <> 1 then Error $"the set has rank {at.IndexTypes.Length} (v1: rank 1)"
            else
            let ix = at.IndexTypes.[0]
            let compound = ix.IxKind = IxKCompound
            if not (ix.IxKind = IxKPlain || compound) || ix.Symmetry <> SymNone || ix.Rank <> 1 then
                Error "the set's index is neither a plain dense axis nor a rank-1 compound (v1)"
            else
            match scalarOf at.ElemType with
            | Some ETInt64 -> Ok compound
            | Some e ->
                let shown =
                    match e with
                    | ETInt32 -> "Int32" | ETFloat32 -> "Float32" | ETFloat64 -> "Float64"
                    | ETComplex64 -> "Complex64" | ETComplex128 -> "Complex128" | ETBool -> "Bool"
                    | ETString -> "String" | ETUnit -> "Unit" | ETInt64 -> "Int64"
                Error $"the set's elements are {shown} (v1 hoists Int64 keys; Int32 and float keys -- a canonical -0.0, a never-matching NaN -- are v1.1)"
            | None -> Error "the set's elements are not scalars (v1 hoists Int64 keys)"
        | _ -> Error "the set is not an array"
    // A literal set's (M, min, max), for the record.
    let literalKeys (v: IRExpr) : int64 list option =
        // integer literals and their wrapping + - * (formalism 2.4: integers
        // wrap -- `-9223372036854775807 - 1` is Int64's minimum)
        let rec lit (e: IRExpr) =
            match e with
            | IRLit (IRLitInt n) -> Some n
            | IRUnaryOp (IRNeg, x) -> lit x |> Option.map (fun n -> -n)
            | IRBinOp (_, IRAdd, l, r, _) -> Option.map2 (+) (lit l) (lit r)
            | IRBinOp (_, IRSub, l, r, _) -> Option.map2 (-) (lit l) (lit r)
            | IRBinOp (_, IRMul, l, r, _) -> Option.map2 (*) (lit l) (lit r)
            | _ -> None
        match v with
        | IRArrayLit (elems, _) ->
            let ks = elems |> List.map lit
            if ks |> List.forall Option.isSome then Some (ks |> List.map Option.get) else None
        | _ -> None
    let repLine (setB: IRBinding) =
        let ruleText = "none -- the as-written scan, nothing built -- when M <= 16 (a literal set that small is not hoisted at all), bitmap when the span max - min + 1 <= 2^26 bits, else hash"
        let fallbackText = "the ACTUAL representation can differ: an allocation the build cannot make degrades it (bitmap -> hash -> linear, the same booleans), and the build reports actual vs preferred and why (stderr under BLADE_MEMBER_VERBOSE=1, both lanes; the C++ run record's member_hoist)"
        match literalKeys setB.Value with
        | Some ks ->
            let m = int64 ks.Length
            let rep =
                if ks.IsEmpty then 1
                else memberChooseRep m (List.min ks) (List.max ks) forced
            let span =
                if ks.IsEmpty then "no keys"
                else
                    let s = uint64 (List.max ks) - uint64 (List.min ks)
                    if s = System.UInt64.MaxValue then "span 2^64" else $"span {s + 1UL}"
            let how =
                if forced = 0 then $"the chooser over the literal set: {ruleText}"
                elif forced = rep then $"forced by BLADE_MEMBER_HOIST={memberRepName forced}"
                else $"BLADE_MEMBER_HOIST={memberRepName forced} forced, outside its domain (the span exceeds the bitmap's 2^26-bit cap): built as {memberRepName rep}"
            $"preferred representation: {memberRepName rep} ({span}, M {m}) -- {how}; {fallbackText}"
        | None ->
            if forced = 0 then $"preferred representation: chosen at run time from (M, min, max) only, never from timing -- {ruleText}; {fallbackText}"
            else $"preferred representation: forced {memberRepName forced} (BLADE_MEMBER_HOIST={memberRepName forced}; a forced bitmap whose span exceeds its 2^26-bit cap is built as a hash at run time); {fallbackText}"
    // What a host EXECUTES at its own site: its value -- or, for a
    // `compound(dense, mask)` binding (whose value is a placeholder), the two
    // operands its site runs, so the canonical semijoin
    // `compound(A, mask(A, lambda(x) -> contains(B, x)))` with its mask left
    // inline is a host too. Other side-table bindings (sparse values, random
    // fills) host nothing in v1.
    let hostExprs (b: IRBinding) : IRExpr list =
        if modul.CompoundInits.ContainsKey b.Id then bindingSideOperands modul b.Id
        elif isPlaceholder b then []
        else [ b.Value ]
    let distinctKernels (ks: (IRCallable * string) list) = ks |> List.distinctBy (fun (c, _) -> c.Id)
    for hi in 0 .. arr.Length - 1 do
        let host = arr.[hi]
        let hostSite = hostExprs host
        let allKernels = hostSite |> List.collect kernelsOf |> distinctKernels
        let kernels = allKernels |> List.filter (fun (c, _) -> not (probesIn c).IsEmpty)
        if not kernels.IsEmpty then
            // Host-level refusals, shared by every set probed in the host.
            let hostWhy : string option =
                if hostSite |> List.exists isDeferredForm then
                    Some $"the host `{host.Name}` is a deferred computation (its cells run at each consumer, not at its own site)"
                else
                match kernels |> List.tryFind (fun (c, _) -> kernelRefs c <> 1) with
                | Some (c, _) -> Some $"the kernel `{c.SourceName}` is named {kernelRefs c} times in the program (v1: a kernel that runs only inside its one host)"
                | None ->
                match kernels |> List.tryFind (fun (c, _) -> c.IsCudaKernel || c.IsMpiParallel) with
                | Some (c, _) ->
                    let kind = if c.IsCudaKernel then "CUDA" else "MPI"
                    Some $"the kernel `{c.SourceName}` is a {kind} kernel (its body runs on a device / another rank)"
                | None ->
                if modul.ProviderWrites.ContainsKey host.Id then Some $"the host `{host.Name}` is a provider write (an external effect)"
                else None
            // Each probe's set, in first-probe order.
            let probes =
                [ for (c, _) in kernels do
                    for (a, _) in probesIn c do
                        yield (c, a) ]
            let setIds =
                probes |> List.choose (fun (_, a) -> match a with IRVar (id, _) -> Some id | _ -> None) |> List.distinct
            let unnamed =
                probes |> List.filter (fun (_, a) -> match a with IRVar _ -> false | _ -> true)
            if not unnamed.IsEmpty then
                let (c, _) = unnamed.Head
                decide host.Name (Blade.Effects.Declined $"a probe in `{c.SourceName}` scans a set expression, not a named stored set (its scan stays as written)")
                    [ $"{unnamed.Length} probe site(s) over an unnamed set" ]
            for sid in setIds do
                let probingKernels =
                    probes |> List.filter (fun (_, a) -> match a with IRVar (id, _) -> id = sid | _ -> false)
                           |> List.map fst |> List.distinctBy (fun c -> c.Id)
                let sites = probes |> List.filter (fun (_, a) -> match a with IRVar (id, _) -> id = sid | _ -> false) |> List.length
                let kernelNames = probingKernels |> List.map (fun c -> $"`{c.SourceName}`") |> String.concat ", "
                let setLabel =
                    match Map.tryFind sid indexOf with
                    | Some si -> arr.[si].Name
                    | None -> $"v{sid}"
                let setWhy : Result<IRBinding * bool * IRBinding list * string, string> =
                    match Map.tryFind sid indexOf with
                    | None -> Error $"the set `{setLabel}` is not a top-level binding (a parameter, capture or local: v1 hoists a module set)"
                    | Some si when si >= hi -> Error $"the set `{setLabel}` is bound after the host"
                    | Some si ->
                        let sb = arr.[si]
                        match keyClass sb.Type with
                        | Error why -> Error why
                        | Ok compound ->
                            match storageOf Set.empty sb with
                            | Error why ->
                                Error $"the set `{sb.Name}` is not proved stored at the build point: {why} (v1 hoists a STORED set; a proved-pure producer forced once at the host is v1.1)"
                            | Ok (how, chain) ->
                                match chain |> List.tryPick (fun b ->
                                        if modul.MutableArrayLets.Contains b.Id then Some (b, $"`{b.Name}` is `let mut`")
                                        elif assignedAnywhere b.Id then Some (b, $"`{b.Name}` is assigned somewhere in the program")
                                        elif modul.ProviderWrites.ContainsKey b.Id then Some (b, $"`{b.Name}` is a provider write")
                                        else None) with
                                | Some (b, why) when b.Id = sb.Id -> Error $"the set {why}"
                                | Some (_, why) -> Error $"the set `{sb.Name}` may change: {why} (its alias chain)"
                                | None -> Ok (sb, compound, chain, how)
                match hostWhy, setWhy with
                | Some why, _ | None, Error why ->
                    decide host.Name (Blade.Effects.Declined why)
                        [ $"set `{setLabel}`: {sites} probe site(s) in {kernelNames} keep the as-written scan" ]
                | None, Ok (sb, compound, chain, how) ->
                    // ---- membershipAdmits: the five parties ----
                    let cellsWord = if compound then "compact buffer" else "cells"
                    let setShape = if compound then "a rank-1 compound's compact buffer" else "rank-1 dense"
                    let producerParty =
                        // every binding of the alias chain is the producer:
                        // the root makes the cells, each alias names them
                        match chain |> List.tryFind (fun b -> bindingPayload modul b |> List.exists wrote) with
                        | Some b ->
                            Error $"the set producer `{b.Name}` may write (its initializer or side-table payload, through the shared write analysis)"
                        | None -> Ok $"set producer `{sb.Name}`: stored at its own site -- {how} -- (not moved; the build reads its {cellsWord} once), no write"
                    let consumerParty =
                        match probingKernels |> List.tryFind (fun c -> wrote c.Body) with
                        | Some c -> Error $"the consumer kernel `{c.SourceName}` may write"
                        | None -> Ok $"consumer kernel(s) {kernelNames}: no write"
                    let folds = hostSite |> List.collect foldsOf
                    let foldParty =
                        let kernelWrites (k: IRExpr) =
                            match resolveCallableIn funcs (fst (peelReynolds k)) with
                            | Some kc -> wrote kc.Body
                            | None -> wrote k
                        match folds |> List.tryFind (fun (k, _) -> kernelWrites k) with
                        | Some _ -> Error $"an outer fold kernel of `{host.Name}` may write"
                        | None ->
                        match folds |> List.tryFind (fun (_, s) -> match s with Some e -> wrote e | None -> false) with
                        | Some _ -> Error $"a fold seed of `{host.Name}` may write"
                        | None ->
                            if folds.IsEmpty then Ok "outer fold kernel(s) / seed(s): none (no fold in the host)"
                            else
                                let seeds = folds |> List.filter (fun (_, s) -> s.IsSome) |> List.length
                                Ok $"outer fold kernel(s): {folds.Length}, no write; seed(s): {seeds}, no write (both stay at their own sites, after the build)"
                    let hostParty =
                        if bindingPayload modul host |> List.exists lifetimeBarrier then
                            Error $"the host `{host.Name}` contains an inline lifetime boundary"
                        elif bindingPayload modul host |> List.exists wrote then
                            Error $"the host `{host.Name}` may write (its source, a kernel, a seed or a statement of its payload -- all of which run after the build)"
                        else Ok $"host `{host.Name}` (its source, kernels, seeds and every statement of its payload, all evaluated after the build): no write"
                    let betweenParty = Ok $"between the build and `{host.Name}`: no binding (the build is `{host.Name}`'s first statement)"
                    let parallelParty =
                        // every parallel body of the host (a probing kernel,
                        // or a `where omp` fold the probes run inside)
                        let par = allKernels |> List.filter (fun (c, _) -> c.IsOmpParallel) |> List.map fst
                        if par.IsEmpty then Ok "parallel: none (a serial host)"
                        else
                            let table = cbTable.Force()
                            let cbParams = par |> List.collect (fun c -> c.Params |> List.filter (fun p -> isFunctionValuedType p.Type) |> List.map (fun p -> (c, p)))
                            match cbParams |> List.tryPick (fun (c, p) ->
                                    match Map.tryFind p.VarId table with
                                    | None -> Some $"the callback parameter `{p.Name}` of the parallel kernel `{c.SourceName}` has an unknown candidate set"
                                    | Some cands ->
                                        cands |> List.tryPick (fun cand ->
                                            match resolveCallableIn (callableById) cand with
                                            | Some cc when not (wrote cc.Body) -> None
                                            | _ -> Some $"a callback candidate of `{p.Name}` in the parallel kernel `{c.SourceName}` may write")) with
                            | Some why -> Error why
                            | None ->
                                let names = par |> List.map (fun c -> $"`{c.SourceName}`") |> String.concat ", "
                                let cbs = if cbParams.IsEmpty then "no function-valued parameter" else $"{cbParams.Length} callback parameter(s), every callbackTable candidate write-free"
                                Ok $"parallel: {names} `where omp` -- the build runs serially before the team and every thread only reads it; {cbs}"
                    let parties = [ producerParty; consumerParty; foldParty; hostParty; betweenParty; parallelParty ]
                    let matrix =
                        "membershipAdmits (five parties, each through Optimize.scheduleMayWriteIn): "
                        + (parties |> List.map (function Ok s -> s | Error s -> "DECLINES: " + s) |> String.concat "; ")
                    let setLine =
                        $"set `{sb.Name}` ({setShape}, Int64 keys): ONE search structure built immediately before `{host.Name}` and freed after it; the {sites} probe site(s) in {kernelNames} read it"
                    let targetLine = "target: evaluated exactly once per probe, before any cell is read (the one evaluation contract of every lane; an empty set still evaluates it)"
                    let sameLine = "identical booleans in every representation (integer ==; build-side duplicates collapse; the probe side keeps its order, multiplicity and short circuits); the build is serial, total, and writes nothing the program reads"
                    match parties |> List.tryPick (function Error s -> Some s | Ok _ -> None) with
                    | Some why ->
                        decide host.Name (Blade.Effects.Declined why) [ setLine; matrix ]
                    | None ->
                        let evidence = [ setLine; repLine sb; targetLine; matrix; sameLine ]
                        // G185 (b), the user's decision of 2026-11-08: a
                        // LITERAL set of at most 16 keys keeps the as-written
                        // per-cell scan (measured faster than every built
                        // structure at M = 11); a forced representation
                        // (`linear` / `bitmap` / `hash`) still builds
                        // M known at compile time: the literal at the ROOT of
                        // the set's alias chain, or a dense set's static
                        // extent (`Array<Int64 like Idx<11>>`, a computed set);
                        // a compound's cardinality is a run-time count (the
                        // C core's chooser answers `none` for it instead)
                        let staticM =
                            match chain |> List.tryPick (fun b -> literalKeys b.Value) with
                            | Some ks -> Some (int64 ks.Length)
                            | None when not compound ->
                                (match Blade.IR.stripUnits sb.Type with
                                 | ArrayElem at ->
                                     (match at.IndexTypes with
                                      | [ ix ] -> (match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
                                      | _ -> None)
                                 | _ -> None)
                            | None -> None
                        let smallLiteral =
                            forced = 0 && (match staticM with Some m -> m <= 16L | None -> false)
                        if not enabled then
                            decide host.Name (Blade.Effects.Declined "disabled by BLADE_MEMBER_HOIST") evidence
                        elif smallLiteral then
                            // nothing is built, but the five-party admission
                            // stands: recorded SCAN-ONLY, so compaction
                            // fusion's membership-probe certificate (T4c)
                            // still reads it as its proof input
                            MemberHoistTable.record
                                { HostId = host.Id; HostName = host.Name; SetId = sb.Id; SetName = sb.Name
                                  Kernels = probingKernels |> List.map (fun c -> c.Id) |> Set.ofList
                                  Forced = forced; Admission = matrix; ScanOnly = true }
                            let scanLine =
                                $"set `{sb.Name}` ({setShape}, Int64 keys): NO structure is built -- the {sites} probe site(s) in {kernelNames} keep the as-written scan (the admission below stands, recorded scan-only: compaction fusion's membership-probe certificate reads it)"
                            decide host.Name (Blade.Effects.Declined "small set: the as-written scan is faster (G185)")
                                [ scanLine; repLine sb; targetLine; matrix; sameLine ]
                        else
                            MemberHoistTable.record
                                { HostId = host.Id; HostName = host.Name; SetId = sb.Id; SetName = sb.Name
                                  Kernels = probingKernels |> List.map (fun c -> c.Id) |> Set.ofList
                                  Forced = forced; Admission = matrix; ScanOnly = false }
                            decide host.Name Blade.Effects.Applied evidence
