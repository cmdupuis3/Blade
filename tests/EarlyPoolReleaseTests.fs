module Blade.Tests.EarlyPoolReleaseTests

open System
open System.Text.RegularExpressions
open Blade
open Blade.IR
open Blade.Types
open Blade.Tests.TestHarness

let private testName = "PM02 early release preserves snapshots and two live different-extent results"

let private source () =
    match Blade.Tests.Corpus.category "memfree" |> List.tryFind (fun (name, _) -> name = testName) with
    | Some (_, src) -> src
    | None -> failwith "missing independent A05 all-output corpus control"

let private withDefaults (gate: string) (body: unit -> 'a) : 'a =
    let pins =
        (Optimize.optimizerGates |> List.map (fun key -> key, None)) @
        [ "BLADE_POOL_EARLY_RELEASE", Some gate; "BLADE_FP_REASSOC", None
          "BLADE_FP_REPRO", Some "1"; "BLADE_FP_CONTRACT", Some "off"; "BLADE_BLAS", Some "off"
          "BLADE_OMP_THREADS", Some "1"; "BLADE_PRINT", None; "BLADE_TILE_CACHE", None
          "BLADE_LLVM_FACTS", None; "BLADE_LLVM_BRICKS", None; "BLADE_SYRK_TILE", None ]
    let saved = pins |> List.map (fun (k, _) -> k, Environment.GetEnvironmentVariable k)
    for k, value in pins do Environment.SetEnvironmentVariable(k, Option.toObj value)
    try body ()
    finally for k, value in saved do Environment.SetEnvironmentVariable(k, value)

let private lower (src: string) : IRProgram =
    match Lowering.lower src with
    | Error why -> failwith ("lowering: " + why)
    | Ok ir ->
        match IRValidate.validateIR ir with
        | Error errors -> failwith ("validation: " + String.concat "; " errors)
        | Ok valid -> valid

let private host (ir: IRProgram) =
    ir.Modules |> List.collect _.Functions |> List.find (fun f -> f.SourceName = "release_candidate")

let private eventCount (e: IRExpr) =
    let mutable n = 0
    iterIRExpr (function IRBeginPoolScope _ | IRReleasePools _ -> n <- n + 1 | _ -> ()) e
    n

let rec private withoutEvents e =
    match e with
    | IRLet (_, (IRBeginPoolScope _ | IRReleasePools _), rest) -> withoutEvents rest
    | IRLet (id, v, rest) -> IRLet (id, v, withoutEvents rest)
    | _ -> e

let private attempt (body: unit -> string) : Result<string, string> =
    try Ok (body ()) with ex -> Error ex.Message

let private report (name: string) (result: Result<string, string>) : bool =
    match result with
    | Ok detail -> resultLine Pass name detail; true
    | Error why -> resultLine Fail name why; false

let private check (ok: bool) (why: string) = if not ok then failwith why

let private matches (pattern: string) (text: string) = Regex.Matches(text, pattern)
let private escape (text: string) = Regex.Escape text

let private cppBody (cpp: string) : string =
    let m = Regex.Match(cpp, @"(?s)Array<double,\s*1> release_candidate\([^)]*\) \{.*?return __ret\d+;\s*\}")
    check m.Success "missing ordinary C++ release_candidate function"
    m.Value

let private cppProof (early: bool) (cpp: string) : unit =
    let body = cppBody cpp
    let pools = matches @"Array<double,\s*1> (?<name>__v\d+|__ret\d+) = \{ allocate<" body
    check (pools.Count = 2) "C++ must keep exactly p and the independent required result backing"
    let pool, result = pools.[0], pools.[1]
    let p, dest = pool.Groups.["name"].Value, result.Groups.["name"].Value
    let frees = matches (@"deallocate<[^\n]+>\(" + escape p + @"\.data, " + escape p + @"\.extents\);") body
    check (frees.Count = 1 && (matches @"deallocate<" body).Count = 1) "C++ did not release exactly the owned p backing once"
    let free = frees.[0].Index
    let middle = Regex.Match(body, escape p + @"\[3L?\]")
    check (middle.Success && middle.Index < free) "C++ middle observation must finish before release"
    check (if early then free < result.Index else free > result.Index) "C++ free is on the wrong side of result allocation"
    let writes = matches (escape ("__fp_" + dest) + @"\[[^\]]+\]\s*=") body
    check (writes.Count = 1 && (early || writes.[0].Index < free)) "C++ baseline cleanup must follow the final result fill"
    check (not (body.Substring(free + frees.[0].Length).Contains(p + "["))) "C++ later payload read after p release"
    check (not (body.Contains("deallocate<" + dest + ">")) && body.Contains("return " + dest + ";")) "C++ returned root must escape"
    check ((matches @"= release_candidate\(" cpp).Count = 2) "C++ must preserve two ordinary calls"

let private llvmBody (ll: string) : string * string =
    let m = Regex.Match(ll.Replace("\r\n", "\n"), @"(?ms)^define internal ptr (?<symbol>@bl_release_candidate_\d+)\([^\n]*\)[^\n]*\{\n.*?^\}")
    check m.Success "missing ordinary LLVM release_candidate function"
    m.Groups.["symbol"].Value, m.Value

// Compare the remaining complete instructions, including arithmetic operands,
// spill/load edges, stores and loop structure. Only cleanup's pointer load/free
// and comments are deleted, then SSA and generated loop labels are alpha-renamed.
// This is stronger than comparing opcodes/flags but is still no native proof.
let private normalizedWithoutCleanup (cleanupLoad: string) (body: string) : string =
    let lines =
        body.Split('\n') |> Array.map _.Trim() |> Array.filter (fun line ->
            line <> "" && not (line.StartsWith ";") && not (line.Contains "call void @blade_free(") &&
            line <> cleanupLoad.Trim())
    let names = Collections.Generic.Dictionary<string, string>()
    Regex.Replace(String.concat "\n" lines, @"%r\d+|(?<![A-Za-z0-9_])loop\.(?:cond|body|end)\d+", MatchEvaluator(fun m ->
        match names.TryGetValue m.Value with
        | true, name -> name
        | _ -> let name = "id" + string names.Count in names.Add(m.Value, name); name))

let private llvmProof (early: bool) (ll: string) : string =
    let symbol, body = llvmBody ll
    let pools = matches @"(?<root>%r\d+) = call ptr @blade_alloc_cells\(i64 (?<n>\d+), i64 8\)" body
    check (pools.Count = 2 && pools.[0].Groups.["n"].Value = "7" && pools.[1].Groups.["n"].Value = "11") "LLVM must keep exactly the original 7/11 payload allocations"
    let root, result = pools.[0].Groups.["root"].Value, pools.[1].Groups.["root"].Value
    let stores = matches (@"store ptr " + escape root + @", ptr (?<slot>%r\d+)(?![0-9])") body
    check (stores.Count = 1) "p root does not have exactly one tracking record"
    let slot = stores.[0].Groups.["slot"].Value
    let slotWrites = matches (@"store ptr (?<value>[^,\n]+), ptr " + escape slot + @"(?![0-9])") body
    check (slotWrites.Count = 2 && slotWrites.[0].Groups.["value"].Value = "null" &&
           slotWrites.[0].Index < pools.[0].Index && slotWrites.[1].Groups.["value"].Value = root)
          "p tracking slot must be initialized then hold only its own root, not null/another backing at cleanup"
    let loads = matches (@"(?<loaded>%r\d+) = load ptr, ptr " + escape slot + @"(?![0-9])") body
    check (loads.Count = 1) "p tracking slot must be loaded exactly once for cleanup"
    let loaded = loads.[0].Groups.["loaded"].Value
    let frees = matches (@"call void @blade_free\(ptr " + escape loaded + @"\)") body
    check (frees.Count = 1 && (matches @"call void @blade_free\(" body).Count = 1) "cleanup must free the exact p slot once, not another root/null slot"
    let free = frees.[0].Index
    check (free > loads.[0].Index && loads.[0].Index > stores.[0].Index) "slot store/load/free order is broken"
    check (if early then free < pools.[1].Index else free > pools.[1].Index) "LLVM free is on the wrong side of result allocation"
    let geps = matches (@"(?<addr>%r\d+) = getelementptr inbounds double, ptr " + escape root + @", i64 [^\n]+") body
    check (geps.Count >= 3 && (geps |> Seq.cast<Match> |> Seq.forall (fun m -> m.Index < free))) "p payload address is formed after release or observations disappeared"
    let mutable payloadLoads = 0
    for gep in geps do
        let address = gep.Groups.["addr"].Value
        let reads = matches (@"= load double, ptr " + escape address + @"(?![0-9])") body
        payloadLoads <- payloadLoads + reads.Count
        check (reads |> Seq.cast<Match> |> Seq.forall (fun m -> m.Index < free)) "p payload load follows its free"
        let writes = matches (@"store double [^,\n]+, ptr " + escape address + @"(?![0-9])") body
        check (writes |> Seq.cast<Match> |> Seq.forall (fun m -> m.Index < free)) "p payload store follows its free"
    check (payloadLoads = 3) "expected exactly fold first/step loads and p(3), not a hidden additional reader"
    check (Regex.IsMatch(body, @"ret ptr " + escape result + @"\b")) "the independent result must escape"
    let resultAddresses = matches (@"(?<addr>%r\d+) = getelementptr inbounds double, ptr " + escape result + @", i64 [^\n]+") body
    let resultStores =
        resultAddresses |> Seq.cast<Match> |> Seq.collect (fun gep ->
            matches (@"store double [^,\n]+, ptr " + escape gep.Groups.["addr"].Value + @"(?![0-9])") body |> Seq.cast<Match>) |> Seq.toList
    check (resultStores.Length = 1 && (early || resultStores.Head.Index < free)) "LLVM baseline cleanup must follow the final result fill"
    check (not (Regex.IsMatch(body.Substring(free + frees.[0].Length), escape root + @"(?![0-9])"))) "p backing is named again after free"
    check ((matches (@"call ptr " + escape symbol + @"\(") ll).Count = 2) "LLVM must preserve two ordinary calls"
    // A real free must participate in attribute resolution: its containing
    // function may never claim nofree even when its map kernels are total.
    let attr = Regex.Match(body, @"\) #(?<id>\d+) \{")
    check attr.Success "missing ordinary function attribute group"
    let definition = Regex.Match(ll, @"(?m)^attributes #" + attr.Groups.["id"].Value + @" = \{[^\n]+")
    check (definition.Success && not (definition.Value.Contains "nofree")) "function containing early release incorrectly claims nofree"
    normalizedWithoutCleanup loads.[0].Value body

let private emission (gate: string) : IRProgram * string * string * Blade.Effects.Decision list =
    withDefaults gate (fun () ->
        Effects.Decisions.start ()
        let ir = lower (source ())
        let cpp, _ = CodeGen.genSelfContainedProgramFromIR ir "facts"
        let ll = match EmitLlvm.tryEmitProgramNamed "facts" ir with Ok ll -> ll | Error why -> failwith why
        ir, cpp, ll, Effects.Decisions.drain ())

let llvmCases () : (string * Result<string, string>) list =
    [ "PM02 exact early backing cleanup before independent return", attempt (fun () ->
        let irOn, cppOn, on, decisions = emission "1"
        let irOff, cppOff, off, _ = emission "0"
        check (eventCount (host irOn).Body = 2 && eventCount (host irOff).Body = 0) "per-call gate did not switch the common typed lifetime schedule"
        check (withoutEvents (host irOn).Body = (host irOff).Body) "original producer/snapshots/tail changed"
        let applied = decisions |> List.filter (fun d -> d.Rule = "pool-early-release" && d.Subject = "release_candidate" && d.Outcome = Effects.Applied)
        check (applied.Length = 1) "expected one function-specific common admission"
        cppProof true cppOn
        cppProof false cppOff
        check (llvmProof true on = llvmProof false off) "noncleanup LLVM instructions/dataflow differ after alpha-renaming"
        "default schedulers; exact p slot freed once before 11-cell allocation; original remaining LLVM dataflow and C++ return ownership preserved") ]

let private replaceHost (m: IRModule) (f: IRCallable) : IRModule =
    { m with Functions = m.Functions |> List.map (fun k -> if k.Id = f.Id then f else k) }

let private parts (f: IRCallable) =
    match f.Body with
    | IRLet (pool, IRCompute (IRApplyCombinator producer), rest) -> pool, producer, rest
    | _ -> failwith "baseline no longer has direct fresh p; do not broaden this test"

let private mapFirstObservation (value: IRExpr -> IRExpr) (f: IRCallable) =
    match f.Body with
    | IRLet (pool, producer, IRLet (id, v, rest)) -> { f with Body = IRLet (pool, producer, IRLet (id, value v, rest)) }
    | _ -> failwith "missing observation"

let private mapTail (change: ApplyInfo -> ApplyInfo) (f: IRCallable) =
    let rec go e =
        match e with
        | IRLet (id, value, rest) -> IRLet (id, value, go rest)
        | IRCompute (IRApplyCombinator info) -> IRCompute (IRApplyCombinator (change info))
        | _ -> failwith "missing original computed tail"
    { f with Body = go f.Body }

let optimizerCases () : bool list =
    let refusal (label: string) (tweak: IRModule -> IRCallable -> IRModule) (extra: IRModule -> IRCallable -> IRModule list) =
        report ("pool early release refuses " + label) (attempt (fun () ->
            withDefaults "0" (fun () ->
                let ir = lower (source ())
                let f = host ir
                let m = ir.Modules |> List.find (fun m -> m.Functions |> List.exists (fun k -> k.Id = f.Id))
                let altered = tweak m f
                let before = altered.Functions |> List.find (fun k -> k.Id = f.Id)
                let scheduled = withDefaults "1" (fun () -> EarlyPoolRelease.scheduleModules (IRBuilder()) (altered :: extra altered f)) |> List.head
                let after = scheduled.Functions |> List.find (fun k -> k.Id = f.Id)
                check (eventCount after.Body = 0 && after.Body = before.Body) "unsupported context acquired cleanup or changed its expression"
                "original lifetime retained; no speculative evaluation")))
    let first value m f = replaceHost m (mapFirstObservation value f)
    let empty (_: IRModule) (_: IRCallable) = []
    let unknown = IRApp (IRVar (-900, IRTArrow ([SVal (IRTScalar ETFloat64)], IRTScalar ETFloat64, None)), [IRLit (IRLitFloat 1.0)], IRTScalar ETFloat64)
    let unusedIds (foreign: bool) =
        report (if foreign then "pool early release reserves unused foreign declarations" else "pool early release reserves unused local declarations")
            (attempt (fun () -> withDefaults "0" (fun () ->
                let ir = lower (source ())
                let f = host ir
                let m = ir.Modules.Head
                let high = 1000000000
                let b = { m.Bindings.Head with Id = high; Name = "unused_high"; Type = IRTUnit; Value = IRLet (high + 1, IRLit (IRLitFloat 1.0), IRLit IRLitUnit) }
                let modules =
                    if foreign then [m; { m with Name = "foreign_unused"; Bindings = [b]; Functions = []; Types = [] }]
                    else [{ m with Bindings = b :: m.Bindings }]
                let scheduled = withDefaults "1" (fun () -> EarlyPoolRelease.scheduleModules (IRBuilder()) modules)
                let after = host { Modules = scheduled; DeclSpans = Map.empty }
                match after.Body with
                | IRLet (beginId, IRBeginPoolScope _, _) ->
                    check (beginId > high + 1 && eventCount after.Body = 2) "fresh lifetime id collided with an unused binding/nested declaration"
                | _ -> failwith "safe unused declaration unexpectedly prevented the ordinary positive"
                "fresh builder raises its floor above both unused declarations without linear ID iteration")))
    [ report "pool early release gate, event validation and idempotence" (attempt (fun () ->
          withDefaults "1" (fun () ->
              let ir = lower (source ())
              let f = host ir
              check (eventCount f.Body = 2) "default common route did not admit"
              let m = ir.Modules |> List.find (fun m -> m.Functions |> List.exists (fun k -> k.Id = f.Id))
              let again = EarlyPoolRelease.scheduleModules (IRBuilder()) ir.Modules
              check (again = ir.Modules) "reapplying the pass duplicated or changed lifetime events"
              let rec corrupt e =
                  match e with
                  | IRLet (id, IRReleasePools (_, pools, label), rest) -> IRLet (id, IRReleasePools (IRVar (-900, IRTUnit), pools, label), rest)
                  | IRLet (id, v, rest) -> IRLet (id, v, corrupt rest)
                  | _ -> e
              let malformed = { ir with Modules = ir.Modules |> List.map (fun x -> if x.Name = m.Name then replaceHost x { f with Body = corrupt f.Body } else x) }
              check (IRValidate.validateIR malformed |> Result.isError) "stale cleanup scope was accepted"
              let pool, producer, _ = parts { f with Body = withoutEvents f.Body }
              let extra =
                  match f.Body with
                  | IRLet (sid, beginEvent, rest) -> IRLet (sid, beginEvent, IRLet (-904, IRCompute (IRApplyCombinator producer), rest))
                  | _ -> failwith "missing begin event"
              let extraProgram = { ir with Modules = ir.Modules |> List.map (fun x -> if x.Name = m.Name then replaceHost x { f with Body = extra } else x) }
              check (IRValidate.validateIR extraProgram |> Result.isError) "unnamed extra owned allocation in the nested scope was accepted"
              check (pool <> -904) "synthetic allocation id collided"
              "one validated event pair; repeated pass unchanged; stale scope rejected")))
      unusedIds false
      unusedIds true
      report "pool early release preserves the failure-order refusal source" (attempt (fun () ->
          withDefaults "1" (fun () ->
              let name = "PM02 unknown scalar failures keep their original function order (aborts)"
              let src =
                  match Blade.Tests.Corpus.category "memfree" |> List.tryFind (fun (n, _) -> n = name) with
                  | Some (_, src) -> src
                  | None -> failwith "missing independent failure-order control"
              let ir = lower src
              let f = ir.Modules |> List.collect _.Functions |> List.find (fun f -> f.SourceName = "no_early")
              check (eventCount f.Body = 0) "unknown/may-fail prefix acquired an early lifetime"
              "two original division failures remain ordered in the original outlined body")))
      refusal "caller-owned/unknown producer" (fun m f ->
          let pool, _, rest = parts f
          replaceHost m { f with Body = IRLet (pool, IRVar (f.Params.Head.VarId, f.Params.Head.Type), rest) }) empty
      refusal "alias observation" (fun m f -> let p, producer, _ = parts f in first (fun _ -> IRVar (p, producer.OutputType)) m f) empty
      refusal "deferred observation" (fun m f -> let _, producer, _ = parts f in first (fun _ -> IRApplyCombinator producer) m f) empty
      refusal "out-of-bounds observation" (fun m f -> let p, producer, _ = parts f in first (fun _ -> IRIndex (IRVar (p, producer.OutputType), [IRLit (IRLitInt 7L)], None)) m f) empty
      refusal "unknown/may-fail scalar call" (first (fun _ -> unknown)) empty
      refusal "extra allocation" (fun m f ->
          let pool, producer, rest = parts f
          replaceHost m { f with Body = IRLet (pool, IRCompute (IRApplyCombinator producer), IRLet (-901, IRCompute (IRApplyCombinator producer), rest)) }) empty
      refusal "duplicate local identity" (fun m f ->
          let pool, _, _ = parts f
          match f.Body with
          | IRLet (p, v, IRLet (_, obs, rest)) -> replaceHost m { f with Body = IRLet (p, v, IRLet (pool, obs, rest)) }
          | _ -> failwith "missing scalar snapshot") empty
      refusal "recursive/unknown body call" (fun m f -> first (fun _ -> IRApp (IRVar (f.Id, mkFuncArrow (f.Params |> List.map _.Type) f.RetType), f.Params |> List.map (fun p -> IRVar (p.VarId, p.Type)), f.RetType)) m f) empty
      refusal "tuple return" (fun m f -> replaceHost m { f with RetType = IRTTuple [f.RetType; IRTScalar ETFloat64]; Body = IRTuple [f.Body; IRLit (IRLitFloat 1.0)] }) empty
      refusal "captured pool" (fun m f ->
          let pool, producer, _ = parts f
          let k = m.Functions |> List.find (fun k -> k.Id <> f.Id)
          { m with Functions = m.Functions |> List.map (fun x -> if x.Id = k.Id then { x with Captures = { Id = pool; Name = "retained"; Type = producer.OutputType; IsMutable = true } :: x.Captures } else x) }) empty
      refusal "hidden index dependencies" (fun m f ->
          let pool, producer, _ = parts f
          match producer.OutputType with
          | ArrayElem at -> { m with Types = IRTDIndexType ("HiddenPool", { at.IndexTypes.Head with Dependencies = [pool] }) :: m.Types }
          | _ -> failwith "missing plain p type") empty
      refusal "hidden executable side payload" (fun m f ->
          let pool, producer, _ = parts f
          let b = { m.Bindings.Head with Id = -905; Name = "hidden_side"; Type = IRTUnit; Value = IRLit IRLitUnit }
          { m with Bindings = b :: m.Bindings; SparseInits = Map.add b.Id (IRVar (pool, producer.OutputType)) m.SparseInits }) empty
      refusal "hidden zero type dependency" (fun m f ->
          let pool, producer, _ = parts f
          match producer.OutputType with
          | ArrayElem at ->
              let ty = mkArrayLike { at with IndexTypes = [{ at.IndexTypes.Head with Dependencies = [pool] }] }
              let b = { m.Bindings.Head with Id = -907; Name = "hidden_zero"; Type = IRTUnit; Value = IRZero ty }
              { m with Bindings = b :: m.Bindings }
          | _ -> failwith "missing plain p type") empty
      refusal "hidden operand metadata" (fun m f ->
          let pool, _, _ = parts f
          replaceHost m (mapTail (fun info ->
              let at = info.ArrayTypes.Head
              { info with ArrayTypes = [{ at with IndexTypes = [{ at.IndexTypes.Head with Dependencies = [pool] }] }] }) f)) empty
      refusal "unit-wrapped return" (fun m f ->
          // Even an empty unit wrapper is not the admitted original type role.
          replaceHost m { f with RetType = IRTUnitAnnotated (f.RetType, unitDimensionless) }) empty
      refusal "unknown final input provenance" (fun m f ->
          replaceHost m (mapTail (fun info ->
              let arrays = [IRVar (-906, f.Params.[1].Type)]
              let loop = match info.Loop with IRMethodFor loop -> IRMethodFor { loop with Arrays = arrays } | _ -> failwith "missing method"
              { info with Arrays = arrays; Loop = loop }) f)) empty
      refusal "foreign hidden index dependency" (fun m _ -> m) (fun m f ->
          let pool, producer, _ = parts f
          match producer.OutputType with
          | ArrayElem at -> [{ m with Name = "foreign_dependency"; Bindings = []; Functions = []; Types = [IRTDIndexType ("ForeignPool", { at.IndexTypes.Head with Dependencies = [pool] })] }]
          | _ -> failwith "missing plain p type")
      refusal "donor used by another function's return plan" (fun m f ->
          let pool, _, _ = parts f
          PoolReuseTable.recordReturn -902 pool
          m) empty
      refusal "kernel/value function use" (fun m f ->
          let b = m.Bindings.Head
          { m with Bindings = { b with Id = -903; Name = "kernel_value"; Type = mkFuncArrow (f.Params |> List.map _.Type) f.RetType; Value = IRVar (f.Id, mkFuncArrow (f.Params |> List.map _.Type) f.RetType) } :: m.Bindings }) empty
      refusal "parallel host" (fun m f -> replaceHost m { f with IsOmpParallel = true }) empty ]
