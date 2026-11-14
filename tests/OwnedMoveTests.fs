// Gap G92, the OWNED MOVE: a whole-array reassignment `a = <fresh>` of an
// owned array binding frees the previous pool before the binding takes the
// fresh one (`CodeGenLoopNest.computeOwnedMoveAssigns` / `renderOwnedMove`,
// `blade plan` rule `owned-move`, BLADE_OWNED_MOVE).
//
// For the corpus witnesses memfree/050-052: the plan record (applied, or the
// decline's reason), the C++ text (the free right before the move), the
// printed values, and -- under BLADE_MEMCHECK=1 (ASan, the pool cache off)
// -- that the OUTSTANDING BLOCKS do not grow with the trip count (40 vs 4
// steps / rows), while BLADE_OWNED_MOVE=0 (the pointer move) leaks one block
// per trip. `blade test owned-move`; part of the default suite. Needs g++
// (Skipped without it); the ASan arms need the memcheck toolchain.
module Blade.Tests.OwnedMoveTests

open System
open System.IO
open System.Diagnostics
open Blade
open Blade.Lowering
open Blade.Tests.TestHarness

let private pinEnv (name: string) (value: string) =
    let prior = Environment.GetEnvironmentVariable(name)
    Environment.SetEnvironmentVariable(name, value)
    { new IDisposable with
        member _.Dispose() = Environment.SetEnvironmentVariable(name, prior) }

let private runProc (exe: string) (timeoutMs: int) =
    let psi = ProcessStartInfo(exe, "")
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    use proc = Process.Start(psi)
    let o = Blade.Runtime.readToEndOffPool proc.StandardOutput
    let e = Blade.Runtime.readToEndOffPool proc.StandardError
    if proc.WaitForExit(timeoutMs) then Ok (proc.ExitCode, o.Result, e.Result)
    else
        (try proc.Kill(true) with _ -> ())
        Error "timed out"

let runOwnedMoveTests () : BlockResult =
    let blockName = "Owned Move"
    printHeader "Owned move (gap G92): a reassigned array frees its previous pool"
    let caps = Build.capabilities.Value
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile the owned-move programs)."
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
    let work = Path.Combine(Path.GetTempPath(), "blade-owned-move-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(work) |> ignore
    CodeGen.deployRuntimeHeaders work
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let mutable names = []
    let fail (n: string) (detail: string) =
        failed <- failed + 1
        names <- names @ [ n ]
        resultLine Fail n detail
    let pass (n: string) (detail: string) =
        passed <- passed + 1
        resultLine Pass n detail
    let skip (n: string) (detail: string) =
        skipped <- skipped + 1
        resultLine Skip n detail
    use _exeCache = pinEnv "BLADE_EXE_CACHE" "0"
    use _blas = pinEnv "BLADE_BLAS" "0"
    use _llvm = pinEnv "BLADE_LLVM" null
    use _om = pinEnv "BLADE_OWNED_MOVE" null
    let corpusSrc (rel: string) =
        [ Path.Combine(Directory.GetCurrentDirectory(), "tests", "corpus", rel)
          Path.Combine(AppContext.BaseDirectory, "tests", "corpus", rel) ]
        |> List.tryFind File.Exists
        |> Option.map (fun p -> let s = File.ReadAllText p in s.Substring(s.IndexOf '\n' + 1))
    let mutable stemN = 0
    /// Emit (decisions captured), optionally compile and run.
    let emit (src: string) : Result<string * Effects.Decision list, string> =
        try
            Effects.Decisions.start ()
            match lower src with
            | Error e -> ignore (Effects.Decisions.drain ()); Error ("lower: " + e)
            | Ok ir ->
                stemN <- stemN + 1
                let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir $"om_{stemN}"
                CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                Ok (cpp.Replace("\r\n", "\n"), Effects.Decisions.drain ())
        with ex ->
            ignore (Effects.Decisions.drain ())
            Error ("codegen raised: " + ex.Message)
    let run (src: string) (memcheck: bool) (ownedMove: string) : Result<string, string> =
        use _m = pinEnv "BLADE_MEMCHECK" (if memcheck then "1" else null)
        use _o = pinEnv "BLADE_OWNED_MOVE" ownedMove
        match emit src with
        | Error e -> Error e
        | Ok (cpp, _) ->
        let file = Path.Combine(work, $"om_run_{stemN}.cpp")
        File.WriteAllText(file, cpp)
        match Build.compileCpp file work with
        | Error e -> Error ("compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
        | Ok exe ->
            match runProc exe 120000 with
            | Ok (0, out, err) -> Ok ((out + "\n" + err).Replace("\r\n", "\n"))
            | Ok (code, out, err) -> Error ($"exit {code}: {out} {err}")
            | Error e -> Error e
    let blocks (t: string) =
        let m = Text.RegularExpressions.Regex.Match(t, @"outstanding_bytes=(\d+) outstanding_blocks=(\d+)")
        if m.Success then Some (int64 m.Groups.[2].Value) else None
    let unavailable (e: string) = e.Contains "Skipped" || e.Contains "sanitize" || e.Contains "vcvars" || e.Contains "memcheck"
    let records (ds: Effects.Decision list) = ds |> List.filter (fun d -> d.Rule = "owned-move")
    let outcomeText (d: Effects.Decision) =
        match d.Outcome with
        | Effects.Applied -> "applied"
        | Effects.Declined why -> "declined -- " + why
    // ---- the census: outstanding blocks vs the trip count -------------------
    let census (name: string) (src: string) (big: string) (small: string) (want: string list) =
        let srcSmall = src.Replace(big, small)
        match run src false "" with
        | Error e -> fail name e
        | Ok plain when want |> List.exists (fun w -> not (plain.Contains w)) -> fail name ($"values: {plain}")
        | Ok _ ->
        match run src true "", run srcSmall true "", run src true "0", run srcSmall true "0" with
        | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e when unavailable e -> skip name e
        | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> fail name e
        | Ok tBig, Ok tSmall, Ok offBig, Ok offSmall ->
            if [ tBig; tSmall; offBig; offSmall ] |> List.exists (fun t -> t.Contains "ERROR: AddressSanitizer") then
                fail name ($"ASan reported: {tBig}")
            else
            match blocks tBig, blocks tSmall, blocks offBig, blocks offSmall with
            | Some b, Some s, Some ob, Some os ->
                if b <> s then fail name ($"outstanding blocks {b} at {big} vs {s} at {small}: a per-trip pool survives")
                elif ob <= os then fail name ($"BLADE_OWNED_MOVE=0 control did not leak ({ob} vs {os} blocks): the witness does not exercise the move")
                elif want |> List.exists (fun w -> not (tBig.Contains w)) then fail name ($"ASan build values: {tBig}")
                else pass name ($"outstanding blocks {b} at {big} = {s} at {small}; the pointer move (BLADE_OWNED_MOVE=0) leaves {ob} vs {os}; ASan clean")
            | _ -> fail name ($"no census line: {tBig}")
    // ---- memfree/050: a function-body let mut, reassigned twice ---------------
    (match corpusSrc "memfree/050_owned_move_function_reassign.blade" with
     | None -> fail "memfree/050 (G92)" "corpus file not found"
     | Some src ->
        (let name = "memfree/050 (G92): the plan applies, the C++ frees the previous pool before each move"
         match emit src with
         | Error e -> fail name e
         | Ok (cpp, ds) ->
            let applied = records ds |> List.filter (fun d -> d.Outcome = Effects.Applied)
            let moves = Text.RegularExpressions.Regex.Matches(cpp, @"deallocate<typename promote<double, 1>::type, nullptr>\((__v\d+)\.data, \1\.extents\);( delete\[\] \w+;)? \1 = __v\d+;")
            if applied.Length <> 1 then fail name (sprintf "owned-move records: %A" (records ds |> List.map outcomeText))
            elif moves.Count < 2 then fail name ($"{moves.Count} owned moves in the emission (want >= 2)")
            else pass name ($"{moves.Count} owned moves (free, then move); {outcomeText applied.Head}"))
        census "memfree/050 (G92): outstanding blocks do not grow with the steps (ASan)" src "type Steps = Idx<40>" "type Steps = Idx<4>" [ "one = 42.0"; "total = 1680.0" ])
    // ---- memfree/051: the leading-axis fold's state, top level and function ---
    (match corpusSrc "memfree/051_owned_move_leading_axis_fold.blade" with
     | None -> fail "memfree/051 (G92)" "corpus file not found"
     | Some src ->
        (let name = "memfree/051 (G92): the fold state's move frees at main (ledger) and in a function"
         match emit src with
         | Error e -> fail name e
         | Ok (cpp, ds) ->
            let applied = records ds |> List.filter (fun d -> d.Outcome = Effects.Applied)
            let moves = Text.RegularExpressions.Regex.Matches(cpp, @"deallocate<typename promote<double, 1>::type, nullptr>\((__v\d+)\.data, \1\.extents\);( delete\[\] \w+;)? \1 = __v\d+;")
            if applied.Length < 2 then fail name (sprintf "owned-move records: %A" (records ds |> List.map outcomeText))
            elif moves.Count < 2 then fail name ($"{moves.Count} owned moves in the emission (want >= 2: main and fold_rows)")
            else pass name ($"{moves.Count} owned moves; {applied.Length} applied records"))
        census "memfree/051 (G92): outstanding blocks do not grow with the rows (ASan)" src "type Rows = Idx<40>" "type Rows = Idx<4>" [ "s = [40.0, 60.0, 80.0]"; "f = [40.0, 60.0, 80.0]" ])
    // ---- memfree/052: the declines ------------------------------------------
    (let name = "memfree/052 (G92): a captured and an aliased target keep the pointer move; values unchanged"
     match corpusSrc "memfree/052_owned_move_declines_capture_and_alias.blade" with
     | None -> fail name "corpus file not found"
     | Some src ->
     match emit src with
     | Error e -> fail name e
     | Ok (_, ds) ->
        let rs = records ds |> List.map outcomeText
        let declinedCapture = rs |> List.exists (fun r -> r.StartsWith "declined -- the target is not a let of the body that names it")
        let declinedAlias = rs |> List.exists (fun r -> r.StartsWith "declined -- it is read where its pool could be aliased")
        if rs |> List.exists (fun r -> r = "applied") then fail name (sprintf "an owned move applied: %A" rs)
        elif not (declinedCapture && declinedAlias) then fail name (sprintf "decline reasons: %A" rs)
        else
        match run src false "" with
        | Error e -> fail name e
        | Ok t when t.Contains "c = 140.0" && t.Contains "d = 140.0" -> pass name (String.concat " | " rs)
        | Ok t -> fail name ($"values: {t}"))
    // ---- memfree/053: a module binding is never the moved value -------------
    (let name = "memfree/053 (G92): a module binding as the moved value declines; every binding prints its cells"
     match corpusSrc "memfree/053_owned_move_declines_module_binding_value.blade" with
     | None -> fail name "corpus file not found"
     | Some src ->
     match emit src with
     | Error e -> fail name e
     | Ok (_, ds) ->
        let rs = records ds |> List.map outcomeText
        if not (rs |> List.exists (fun r -> r.StartsWith "declined -- the value `r1` is a module binding")) then fail name (sprintf "records %A" rs)
        else
        match run src false "" with
        | Error e -> fail name e
        | Ok t when t.Contains "r1 = [2.0, 3.0, 4.0]" && t.Contains "t = [3.0, 4.0, 5.0]" -> pass name (String.concat " | " rs)
        | Ok t -> fail name ($"values: {t}"))    // ---- memfree/054: a staged value borrowing its input's extents ----------
    (let name = "memfree/054 (G92): a staged pipeline value (borrowed extents table) -- ASan clean, values"
     match corpusSrc "memfree/054_owned_move_staged_value_borrowed_extents.blade" with
     | None -> fail name "corpus file not found"
     | Some src ->
     match run src false "", run src true "" with
     | Error e, _ -> fail name e
     | _, Error e when unavailable e -> skip name e
     | _, Error e -> fail name e
     | Ok plain, Ok asan ->
         if asan.Contains "ERROR: AddressSanitizer" then fail name ($"ASan reported: {asan}")
         elif [ plain; asan ] |> List.exists (fun t -> not (t.Contains "s = 50.0" && t.Contains "s2 = 70.0")) then fail name ($"values: {plain}")
         else pass name "ASan clean; s = 50.0, s2 = 70.0")    // ---- the knob ------------------------------------------------------------
    // ---- memfree/086: a retained target declines (gap G221 m8) --------------
    (let name = "memfree/086 (G221): a target a retaining row kernel names declines the move; ASan clean, values"
     match corpusSrc "memfree/086_retained_pool_declines_the_owned_move.blade" with
     | None -> fail name "corpus file not found"
     | Some src ->
     match emit src with
     | Error e -> fail name e
     | Ok (_, ds) ->
        let rs = records ds |> List.map outcomeText
        if not (rs |> List.exists (fun r -> r.StartsWith "declined -- it is read inside a computed map whose kernel may retain it")) then
            fail name (sprintf "records %A" rs)
        else
        match run src false "", run src true "" with
        | Error e, _ -> fail name e
        | _, Error e when unavailable e -> skip name e
        | _, Error e -> fail name e
        | Ok plain, Ok asan ->
            if asan.Contains "ERROR: AddressSanitizer" then fail name ($"ASan reported: {asan}")
            elif [ plain; asan ] |> List.exists (fun t -> not (t.Contains "result = 119.0" && t.Contains "saved = [8.0, 10.0, 12.0]")) then fail name ($"values: {plain}")
            else pass name (String.concat " | " rs + "; ASan clean"))
    (let name = "BLADE_OWNED_MOVE=0: every candidate declines, the emission keeps the pointer move"
     match corpusSrc "memfree/050_owned_move_function_reassign.blade" with
     | None -> fail name "corpus file not found"
     | Some src ->
     use _o = pinEnv "BLADE_OWNED_MOVE" "0"
     match emit src with
     | Error e -> fail name e
     | Ok (cpp, ds) ->
        let rs = records ds |> List.map outcomeText
        if rs |> List.forall (fun r -> r = "declined -- disabled by BLADE_OWNED_MOVE") && not rs.IsEmpty
           && not (Text.RegularExpressions.Regex.IsMatch(cpp, @"nullptr>\((__v\d+)\.data, \1\.extents\);( delete\[\] \w+;)? \1 = __v\d+;")) then
            pass name (String.concat " | " rs)
        else fail name (sprintf "records %A" rs))
    (try Directory.Delete(work, true) with _ -> ())
    printFooter blockName [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = names }
