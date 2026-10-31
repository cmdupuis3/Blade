// Round-2 cell epilogues (B1 rank-1 producer epilogue, B2r row chain
// interleave): the LICENSED and threaded forms the differential gates cannot
// see (`test interp` / `opt-diff` / `diff-oracle` pin BLADE_FP_REPRO=1 with
// BLADE_FP_CONTRACT=off and the default thread setting), checked by RAW BITS.
//
// For each program and regime the C++ is emitted twice -- BLADE_CELL_EPILOGUE
// unset (NEW) and `0` (CTRL, the two-pass form) -- compiled, run, and the
// printed RAW bits compared: `%a` of every checked scalar and a bit hash of
// every checked producer pool (`y2` itself, not just the checksum: P1's
// product barrier fixes the squares' rounding, not the producer's, so the
// producer values being identical is part of the claim). Regimes:
//   lic_ser   BLADE_OMP_THREADS=1, contraction off and fast    -> bitwise
//   lic_mt    threads on at OMP_NUM_THREADS = 3 and 8, both modes -> bitwise
//             (the same ACTUAL team: Path B partials over chunk_lo chunks
//             whose starts are not vector-aligned at n = 2003)
//   rep_ser   BLADE_FP_REPRO=1, contraction off                -> bitwise
//   rep_mt    BLADE_FP_REPRO=1, threads on, contraction off    -> bitwise
//   rep_fast  BLADE_FP_REPRO=1, contraction fast               -> 1e-12 relative
//             (not a bitwise contract: GCC's contraction is context-dependent)
// plus, for each NEW emission, the needle that proves the arm actually
// fired, and the LLVM lane against the C++ lane under the identity regime
// (printed values, both with the gate on and off).
// `blade test cell-epilogue`; part of the default suite. Needs g++
// (Skipped without it); the LLVM half needs clang (Skipped without it).
module Blade.Tests.CellEpilogueTests

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

/// One checked program: its source, the scalars whose `%a` bits are printed,
/// the producer pools (name, cells) whose bit hashes are printed, and the
/// needle each regime's NEW emission must contain (None = the arm is
/// expected to decline in that regime; then CTRL == NEW by construction and
/// the bits are still compared).
type private Program = {
    Name: string
    Source: string
    Scalars: string list
    Pools: (string * int) list
    /// A FUNCTION-LOCAL rank-2 pool (function name, cells) hashed right
    /// before the function frees it -- recur's trajectory, which no source
    /// reader may touch (a reader would disable row-fold fusion itself).
    LocalPool: (string * int) option
    Needle: string -> string option
}

let private saxpySq =
    { Name = "saxpy_sq"
      Source =
        "type Ix = Idx<2003>\n"
        + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 13) / 7.0 + 0.1 |> compute\n"
        + "let y = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 11) / 3.0 |> compute\n"
        + "let y2 = method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1) -> 1.5 * u + v |> compute\n"
        + "let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q) where omp -> p + q, 0.0)\n"
      Scalars = [ "s" ]
      Pools = [ ("y2", 2003) ]
      LocalPool = None
      Needle = fun r ->
        match r with
        | "lic_ser" -> Some "blade_fold::feed_sq<double, 8>(__cep_ls"
        | "lic_mt" | "lic_mt_lim" -> Some "BLADE_OMP_PARALLEL_NUM_THREADS(__cep_T_"
        | "rep_ser" | "rep_fast" -> Some "__cep_acc_s = __wrap_"
        | _ -> None }

let private saxpyPlain =
    { Name = "saxpy_plain"
      Source =
        "type Ix = Idx<2003>\n"
        + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 17) / 9.0 |> compute\n"
        + "let y = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 5) / 7.0 - 0.3 |> compute\n"
        + "let y2 = method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1) -> 1.5 * u + v |> compute\n"
        + "let t = reduce(method_for(y2) <@> lambda(w) -> w, lambda(p, q) where omp -> p + q, 1.25)\n"
      Scalars = [ "t" ]
      Pools = [ ("y2", 2003) ]
      LocalPool = None
      Needle = fun r ->
        match r with
        | "lic_ser" -> Some "blade_fold::feed_sum<double, 8>(__cep_ls"
        | "lic_mt" | "lic_mt_lim" -> Some "BLADE_OMP_PARALLEL_NUM_THREADS(__cep_T_"
        | "rep_ser" | "rep_fast" -> Some "__cep_acc_t = __wrap_"
        | _ -> None }

let private stencil =
    { Name = "stencil"
      Source =
        "type Y = Idx<13>\n"
        + "type X = Idx<37>\n"
        + "let u = method_for(range<Y, X>) <@> lambda(i, j) -> Float64((Int64(i) * 31 + Int64(j) * 17) % 23) / 9.0 |> compute\n"
        + "let lap = method_for(range<halo<Y, [-1, 0, 1]>, halo<X, [-1, 0, 1]>>) <@> lambda(wr, wc) where omp(wr: 1) -> u(wr(-1), wc(0)) + u(wr(1), wc(0)) + u(wr(0), wc(-1)) + u(wr(0), wc(1)) - 4.0 * u(wr(0), wc(0)) |> compute\n"
        + "let ss = reduce(method_for(lap) <@> lambda(row) -> prodsum(row, row), lambda(p, q) where omp -> p + q, 0.0)\n"
      Scalars = [ "ss" ]
      Pools = [ ("lap", 11 * 35) ]
      LocalPool = None
      Needle = fun r ->
        match r with
        | "rep_ser" | "rep_mt" | "rep_fast" -> Some "__hrc_ss += __c * __c;"
        | _ -> None }

let private recur =
    { Name = "recur"
      Source =
        "type T = Idx<9>\n"
        + "type X = Idx<1003>\n"
        + "function total(u0: Array<Float64 like X>) -> Float64 = {\n"
        + "    let rec traj: Array<Float64 like T, X> =\n"
        + "        match traj with\n"
        + "        | zero -> zero\n"
        + "        | zero :: s -> zero :: u0\n"
        + "        | prefix :: n -> prefix :: prefix(n - 1) - 0.01 * prefix(n - 1) * prefix(n - 1) * prefix(n - 1)\n"
        + "    reduce(method_for(traj) <@> lambda(row) -> prodsum(row, row), (+))\n"
        + "}\n"
        + "let u0 = method_for(range<X>) <@> lambda(i) -> Float64(Int64(i) % 7) / 3.0 |> compute\n"
        + "let tot = total(u0)\n"
      Scalars = [ "tot" ]
      Pools = []
      LocalPool = Some ("total", 9 * 1003)
      Needle = fun r ->
        match r with
        | "rep_ser" | "rep_mt" | "rep_fast" -> Some "= __cfc_"
        | _ -> None }

let private programs = [ saxpySq; saxpyPlain; stencil; recur ]

/// One regime: name, contraction, OMP_NUM_THREADS, OMP_THREAD_LIMIT (null =
/// unset), threads emitted, repro, bitwise, the ACTUAL Path B team the
/// threaded saxpy forms must observe (None = no team asserted).
type private Regime = {
    RName: string; Contract: string; Threads: string; Limit: string
    ThreadsOn: bool; Repro: bool; Bitwise: bool; Team: int option }

let private regimes =
    let r n c t l th rp bw team = { RName = n; Contract = c; Threads = t; Limit = l; ThreadsOn = th; Repro = rp; Bitwise = bw; Team = team }
    [ r "lic_ser" "off" "1" null false false true None
      r "lic_ser" "fast" "1" null false false true None
      r "lic_mt" "off" "3" null true false true (Some 3)
      r "lic_mt" "fast" "3" null true false true (Some 3)
      r "lic_mt" "fast" "8" null true false true (Some 8)
      r "lic_mt" "off" "8" null true false true (Some 8)
      // requested team > actual team: 8 partials allocated, 2 threads run
      r "lic_mt_lim" "off" "8" "2" true false true (Some 2)
      r "rep_ser" "off" "1" null false true true None
      r "rep_mt" "off" "8" null true true true None
      r "rep_fast" "fast" "1" null false true false None ]

/// The raw-bit probe: `%a` of the scalars, a bit hash of each pool, the hash
/// of a function-local pool taken right before that function frees it, and
/// the actual team every Path B region observed (thread 0 records
/// `blade_omp_get_num_threads()`; -1 = no region ran), printed before main's
/// `return 0;`.
let private injectProbe (p: Program) (cpp0: string) : string option =
    let cpp = cpp0.Replace("\r\n", "\n")
    let marker = "\n    return 0;\n    } catch"
    let teamLine = "const int __rnt = blade_omp_get_num_threads();"
    let cpp = cpp.Replace(teamLine, teamLine + " if (blade_omp_get_thread_num() == 0) __cep_team_seen = __rnt;")
    // the function-local pool: hash it before its rank-2 deallocation
    let cppLocal =
        match p.LocalPool with
        | None -> Some cpp
        | Some (fname, cells) ->
            let fAt = cpp.IndexOf($"\ndouble {fname}(")
            if fAt < 0 then None else
            let defAt = cpp.IndexOf("{\n", fAt)
            let m = System.Text.RegularExpressions.Regex.Match(cpp.Substring(defAt), @"Array<double, 2> (__v\d+) = \{ allocate")
            if not m.Success then None else
            let buf = m.Groups.[1].Value
            let freeLine = $"    deallocate<typename promote<double, 2>::type, nullptr>({buf}.data, {buf}.extents);"
            let freeAt = cpp.IndexOf(freeLine, defAt)
            if freeAt < 0 then None else
            let hash = $"    {{ unsigned long long __h = 0; const double* __p = nested_array_utilities::pool_base({buf}.data); for (size_t __q = 0; __q < (size_t){cells}; __q++) {{ unsigned long long __b; std::memcpy(&__b, __p + __q, 8); __h = __h * 1000003ULL ^ __b; }} __cep_local_hash = __h; }}\n"
            Some (cpp.Substring(0, freeAt) + hash + cpp.Substring(freeAt))
    match cppLocal with
    | None -> None
    | Some cpp ->
    let at = cpp.IndexOf marker
    if at < 0 then None
    else
        let scalars =
            p.Scalars |> List.map (fun s -> $"    std::printf(\"RAW {s} %%a\\n\", (double)({s}));")
        let pools =
            p.Pools |> List.map (fun (n, cells) ->
                $"    {{ unsigned long long __h = 0; const double* __p = nested_array_utilities::pool_base({n}.data); for (size_t __q = 0; __q < (size_t){cells}; __q++) {{ unsigned long long __b; std::memcpy(&__b, __p + __q, 8); __h = __h * 1000003ULL ^ __b; }} std::printf(\"RAW {n} %%llx\\n\", __h); }}")
        let local =
            match p.LocalPool with
            | Some (fname, _) -> [ $"    std::printf(\"RAW {fname}.local %%llx\\n\", __cep_local_hash);" ]
            | None -> []
        let team = [ "    std::printf(\"RAW team %d\\n\", __cep_team_seen);" ]
        let probe = String.concat "\n" (scalars @ pools @ local @ team)
        let head = "#include <cstdio>\n#include <cstring>\nstatic int __cep_team_seen = -1;\nstatic unsigned long long __cep_local_hash = 0;\n"
        Some (head + cpp.Substring(0, at) + "\n" + probe + cpp.Substring(at))

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

let private rawLines (out: string) =
    out.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.StartsWith "RAW ") |> List.ofArray

/// The printed program values (the `name = value` lines), for the lane parity.
let private valueLines (out: string) =
    out.Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun l -> l.Contains " = " && not (l.StartsWith "RAW ") && not (l.Contains "completed in"))
    |> List.ofArray

let runCellEpilogueTests () : BlockResult =
    let blockName = "Cell Epilogue"
    printHeader "Cell Epilogues (B1 / B2r): licensed and threaded forms, raw bits NEW vs CTRL"
    let caps = Build.capabilities.Value
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile the cell-epilogue programs)."
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
    let work = Path.Combine(Path.GetTempPath(), "blade-cell-epilogue-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(work) |> ignore
    CodeGen.deployRuntimeHeaders work
    EmitLlvm.deployShim work
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
    use _lanes = pinEnv "BLADE_FOLD_LANES" null
    use _tune = pinEnv "BLADE_TILE_TUNE" null
    use _llvm = pinEnv "BLADE_LLVM" null
    let emitCpp (p: Program) (stem: string) : Result<string, string> =
        try
            match lower p.Source with
            | Error e -> Error ("lower: " + e)
            | Ok ir ->
                let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir stem
                CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                Ok cpp
        with ex -> Error ("codegen raised: " + ex.Message)
    let buildRunLim (stem: string) (cpp: string) (threads: string) (limit: string) : Result<string, string> =
        let file = Path.Combine(work, stem + ".cpp")
        File.WriteAllText(file, cpp)
        match Build.compileCpp file work with
        | Error e -> Error ("compile: " + (if e.Length > 400 then e.Substring(0, 400) else e))
        | Ok exe ->
            use _t = pinEnv "OMP_NUM_THREADS" threads
            use _l = pinEnv "OMP_THREAD_LIMIT" limit
            use _d = pinEnv "OMP_DYNAMIC" "FALSE"
            match runProc exe 120000 with
            | Error e -> Error e
            | Ok (code, out, err) when code <> 0 -> Error ($"exit {code}: {err.Trim()}")
            | Ok (_, out, _) -> Ok out
    let buildRun (stem: string) (cpp: string) (threads: string) = buildRunLim stem cpp threads null
    let mutable stemN = 0
    for p in programs do
        for rg in regimes do
            let regime, contract, threads, threadsOn, repro, bitwise = rg.RName, rg.Contract, rg.Threads, rg.ThreadsOn, rg.Repro, rg.Bitwise
            let limTxt = if isNull rg.Limit then "" else $" limit={rg.Limit}"
            let name = $"{p.Name} {regime} contract={contract} T={threads}{limTxt}"
            use _c = pinEnv "BLADE_FP_CONTRACT" contract
            use _r = pinEnv "BLADE_FP_REPRO" (if repro then "1" else null)
            use _o = pinEnv "BLADE_OMP_THREADS" (if threadsOn then null else "1")
            let emitWith (gate: string) =
                use _g = pinEnv "BLADE_CELL_EPILOGUE" gate
                stemN <- stemN + 1
                let stem = $"cep_{p.Name}_{stemN}"
                emitCpp p stem |> Result.map (fun cpp -> (stem, cpp))
            match emitWith null, emitWith "0" with
            | Error e, _ | _, Error e -> fail name e
            | Ok (sNew, cNew), Ok (sOld, cOld) ->
            // The arm fired (or, where a decline is expected, the form is CTRL's).
            let fired =
                match p.Needle regime with
                | Some needle -> cNew.Contains needle
                | None -> true
            if not fired then
                fail name ($"the expected arm did not fire: no `{(p.Needle regime).Value}` in the NEW emission")
            // Where the arm is EXPECTED to decline, the two emissions must be
            // the same text (the gate's only effect is the decision record).
            // Compared as text, not run: a declined licensed mt8 form can
            // itself be nondeterministic (an OpenMP `reduction` combines its
            // partials in arrival order), which bits would misreport.
            elif (p.Needle regime).IsNone && cNew.Replace(sNew, "STEM") = cOld.Replace(sOld, "STEM") then
                pass name "the arm declines in this regime: NEW and CTRL emit the same C++"
            elif (p.Needle regime).IsNone then
                fail name "the arm should decline in this regime, but NEW and CTRL emit different C++"
            else
            match injectProbe p cNew, injectProbe p cOld with
            | None, _ | _, None -> fail name "could not place the raw-bit probe in main"
            | Some pNew, Some pOld ->
            match buildRunLim sNew pNew threads rg.Limit, buildRunLim sOld pOld threads rg.Limit with
            | Error e, _ | _, Error e when e.StartsWith "compile: Skipped" -> skip name e
            | Error e, _ -> fail name ("NEW: " + e)
            | _, Error e -> fail name ("CTRL: " + e)
            | Ok oNew, Ok oOld ->
                let rNew, rOld = rawLines oNew, rawLines oOld
                let expected = p.Scalars.Length + p.Pools.Length + (if p.LocalPool.IsSome then 1 else 0) + 1
                // The ACTUAL team both arms ran (asserted wherever a Path B
                // form is expected: the threaded saxpy arms).
                let teamOf (rs: string list) =
                    rs |> List.tryPick (fun l -> if l.StartsWith "RAW team " then Some (int (l.Substring 9)) else None)
                let teamWanted =
                    match rg.Team with
                    | Some t when (p.Needle regime |> Option.exists (fun nd -> nd.Contains "PARALLEL_NUM_THREADS")) -> Some t
                    | _ -> None
                if rNew.Length <> expected || rOld.Length <> expected then
                    fail name ($"raw-bit probe printed {rNew.Length}/{rOld.Length} lines, wanted {expected}")
                elif teamWanted.IsSome && (teamOf rNew <> teamWanted || teamOf rOld <> teamWanted) then
                    fail name ($"actual team: NEW {teamOf rNew} CTRL {teamOf rOld}, wanted {teamWanted.Value} in both")
                elif bitwise then
                    if rNew = rOld then pass name ($"""bitwise NEW == CTRL: {String.concat "; " rNew}""")
                    else fail name ($"""raw bits differ: NEW [{String.concat "; " rNew}] CTRL [{String.concat "; " rOld}]""")
                else
                    // 1e-12 relative on the printed decimal values (15 digits).
                    let vals (o: string) =
                        valueLines o
                        |> List.filter (fun l -> p.Scalars |> List.exists (fun s -> l.StartsWith (s + " = ")))
                        |> List.map (fun l -> Double.Parse(l.Substring(l.IndexOf " = " + 3).Trim(), Globalization.CultureInfo.InvariantCulture))
                    let a, b = vals oNew, vals oOld
                    let close = a.Length = b.Length && a.Length > 0 && List.forall2 (fun (x: float) (y: float) -> abs (x - y) <= 1e-12 * max (abs x) (abs y)) a b
                    if close then pass name ($"""within 1e-12 relative (bits {(if rNew = rOld then "identical" else "differ")}): {String.concat "; " rNew}""")
                    else fail name ($"values differ beyond 1e-12: NEW {a} CTRL {b}")
    // The LLVM lane against the C++ lane under the identity regime, the gate
    // on and off (printed values: the LLVM lane has no raw-bit probe).
    let llvmParity () =
      match Build.resolveClang () with
      | None -> skip "llvm lane parity" "no clang (BLADE_LLVM_CLANG)"
      | Some _ ->
      for p in programs do
        for gate in [ null; "0" ] do
            let gname = if isNull gate then "on" else "off"
            let name = $"{p.Name} llvm == cpp (REPRO, contract=off, serial, gate {gname})"
            use _c = pinEnv "BLADE_FP_CONTRACT" "off"
            use _r = pinEnv "BLADE_FP_REPRO" "1"
            use _o = pinEnv "BLADE_OMP_THREADS" "1"
            use _g = pinEnv "BLADE_CELL_EPILOGUE" gate
            stemN <- stemN + 1
            let stem = $"cep_ll_{p.Name}_{stemN}"
            match lower p.Source with
            | Error e -> fail name ("lower: " + e)
            | Ok ir ->
            // These four programs exercise implemented LLVM arms: a refusal or
            // an exception is a FAILURE (skips are for a missing toolchain).
            match (try EmitLlvm.tryEmitProgramNamed stem ir with ex -> Error ("raised: " + ex.Message)) with
            | Error why -> fail name ($"the llvm lane refused a pinned supported program: {why}")
            | Ok ll ->
            let llFile = Path.Combine(work, stem + ".ll")
            File.WriteAllText(llFile, ll)
            match Build.compileLlvmProgram llFile work with
            | Error e when e.StartsWith "Skipped:" -> skip name e
            | Error e -> fail name ("llvm compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
            | Ok llExe ->
            match emitCpp p (stem + "_cpp") with
            | Error e -> fail name e
            | Ok cpp ->
            match buildRun (stem + "_cpp") cpp "1" with
            | Error e -> fail name ("C++: " + e)
            | Ok cOut ->
            use _t = pinEnv "OMP_NUM_THREADS" "1"
            match runProc llExe 120000 with
            | Error e -> fail name ("llvm run: " + e)
            | Ok (code, _, err) when code <> 0 -> fail name ($"llvm exit {code}: {err.Trim()}")
            | Ok (_, lOut, _) ->
                let a, b = valueLines lOut, valueLines cOut
                let wantScalars = p.Scalars |> List.forall (fun s -> b |> List.exists (fun l -> l.StartsWith (s + " = ")))
                if b.IsEmpty || not wantScalars then fail name ($"the C++ run printed no checked value ({b.Length} lines)")
                elif a = b then pass name ($"{a.Length} printed lines identical")
                else fail name ($"""llvm [{String.concat " | " (a |> List.truncate 4)}] cpp [{String.concat " | " (b |> List.truncate 4)}]""")
    llvmParity ()
    // The LICENSED LLVM lane, gate on vs off, executed: the checked values
    // agree at the lanes' tolerance (1e-9 relative: the licensed form's tree
    // is the vectorizer's, not a bitwise contract), and the B1 arm fires
    // (one loop fewer) where it applies; the B2r arms decline when licensed.
    let llvmLicensed () =
      match Build.resolveClang () with
      | None -> skip "llvm licensed on/off" "no clang (BLADE_LLVM_CLANG)"
      | Some _ ->
      for p in programs do
        let name = $"{p.Name} llvm licensed gate on vs off (1e-9 relative)"
        use _c = pinEnv "BLADE_FP_CONTRACT" null
        use _r = pinEnv "BLADE_FP_REPRO" null
        use _o = pinEnv "BLADE_OMP_THREADS" "1"
        let runWith (gate: string) =
            use _g = pinEnv "BLADE_CELL_EPILOGUE" gate
            stemN <- stemN + 1
            let stem = $"cep_lic_{p.Name}_{stemN}"
            match lower p.Source with
            | Error e -> Error ("lower: " + e)
            | Ok ir ->
            match (try EmitLlvm.tryEmitProgramNamed stem ir with ex -> Error ("raised: " + ex.Message)) with
            | Error why -> Error ($"the llvm lane refused a pinned supported program: {why}")
            | Ok ll ->
            let llFile = Path.Combine(work, stem + ".ll")
            File.WriteAllText(llFile, ll)
            match Build.compileLlvmProgram llFile work with
            | Error e -> Error ("llvm compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
            | Ok exe ->
            use _t = pinEnv "OMP_NUM_THREADS" "1"
            match runProc exe 120000 with
            | Ok (0, out, _) -> Ok (ll, out)
            | Ok (code, _, err) -> Error ($"llvm exit {code}: {err.Trim()}")
            | Error e -> Error e
        match runWith null, runWith "0" with
        | Error e, _ | _, Error e when e.StartsWith "llvm compile: Skipped" -> skip name e
        | Error e, _ | _, Error e -> fail name e
        | Ok (llOn, oOn), Ok (llOff, oOff) ->
            let loops (s: string) = s.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.StartsWith "loop.cond") |> Array.length
            let vals (o: string) =
                valueLines o
                |> List.filter (fun l -> p.Scalars |> List.exists (fun s -> l.StartsWith (s + " = ")))
                |> List.map (fun l -> Double.Parse(l.Substring(l.IndexOf " = " + 3).Trim(), Globalization.CultureInfo.InvariantCulture))
            let a, b = vals oOn, vals oOff
            let wantFewer = p.Name.StartsWith "saxpy"
            let armOk = if wantFewer then loops llOn = loops llOff - 1 else loops llOn = loops llOff
            let close = a.Length = p.Scalars.Length && a.Length = b.Length && List.forall2 (fun (x: float) (y: float) -> abs (x - y) <= 1e-9 * max (abs x) (abs y)) a b
            let wantTxt = if wantFewer then "one fewer" else "equal"
            if not armOk then fail name ($"arm needle: {loops llOn} loops on vs {loops llOff} off (wanted {wantTxt})")
            elif close then pass name ($"""values within 1e-9 ({(if a = b then "identical" else "differ")}); {loops llOn} vs {loops llOff} loops""")
            else fail name ($"values: on {a} off {b}")
    llvmLicensed ()
    // The LLVM lane's arms fire: under the identity regime every program's
    // .ll differs between the gate on and off (one loop fewer: the fold or
    // the row chain fused into the producer's loop).
    for p in programs do
        let name = $"{p.Name} llvm arm fires (REPRO, serial: gate on != gate off)"
        use _r = pinEnv "BLADE_FP_REPRO" "1"
        use _o = pinEnv "BLADE_OMP_THREADS" "1"
        let llWith (gate: string) =
            use _g = pinEnv "BLADE_CELL_EPILOGUE" gate
            match lower p.Source with
            | Error e -> Error e
            | Ok ir -> (try EmitLlvm.tryEmitProgramNamed "cep_arm" ir with ex -> Error ("raised: " + ex.Message))
        match llWith null, llWith "0" with
        | Ok on, Ok off ->
            let loops (s: string) = s.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.StartsWith "loop.cond") |> Array.length
            if loops on = loops off - 1 then pass name ($"{loops on} loops on vs {loops off} off")
            else fail name ($"expected one loop fewer with the gate on; got {loops on} on vs {loops off} off")
        // a pinned supported program: a refusal is a failure, not a skip
        | Error e, _ | _, Error e -> fail name ($"the llvm lane refused a pinned supported program: {e}")
    // A7c (round 3, gap G69): producer-INPUT legs riding B1's sweep, NEW vs
    // BLADE_EPILOGUE_LEGS=0 (the joined pass), serial (T = 1). Strict legs
    // (the repro build, or legs with no licence of their own) are bitwise in
    // both contraction modes -- each leg is the same ascending chain from the
    // same seed, only interleaved with the stores; licensed legs are their
    // own `blade_fold::sum` lane state against the joined pass's `omp simd
    // reduction` -- a licensed association change, held to 1e-12. The LLVM
    // lane: printed values gate on == gate off under the identity regime, and
    // the joined pass's loop is gone.
    let legsCall =
        { Name = "legs_call"
          Source =
            "type Ix = Idx<2003>\n"
            + "function kernel(x: Array<Float64 like Ix>, y: Array<Float64 like Ix>) -> Float64 = {\n"
            + "    let y2 = method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1) -> 1.5 * u + v |> compute\n"
            + "    let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q) where omp -> p + q, 0.0)\n"
            + "    s\n"
            + "}\n"
            + "let (s, ixs) = {\n"
            + "    let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 13) / 7.0 + 0.1 |> compute\n"
            + "    let y = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 11) / 3.0 |> compute\n"
            + "    (kernel(x, y), reduce(x, (+)) + reduce(y, (+)))\n"
            + "}\n"
          Scalars = [ "s"; "ixs" ]; Pools = []; LocalPool = None
          Needle = fun r ->
            match r with
            | "rep_ser" -> Some "__epl_acc_"
            | _ -> Some "blade_fold::feed_sum<double, 8>(__epl_ls_" }
    let legsStrict =
        { Name = "legs_strict"
          Source =
            "type Ix = Idx<2003>\n"
            + "let (s, d) = {\n"
            + "    let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 17) / 9.0 |> compute\n"
            + "    let y = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 5) / 7.0 - 0.3 |> compute\n"
            + "    let y2 = method_for(zip(x, y)) <@> lambda(u, v) -> 1.5 * u + v |> compute\n"
            + "    let s = reduce(method_for(y2) <@> lambda(w) -> w, lambda(p, q) -> p + q, 1.25)\n"
            + "    (s, reduce(x, lambda(a, b) -> a - b, 0.75) * 3.0 + reduce(y, lambda(a, b) -> a - 0.5 * b, -0.0))\n"
            + "}\n"
          Scalars = [ "s"; "d" ]; Pools = []; LocalPool = None
          Needle = fun r ->
            match r with
            | "rep_ser" -> Some "__epl_acc_"
            | _ -> Some "for (size_t __ek = __cep_b;" }
    let legRegimes =
        [ (legsCall, "rep_ser", "off", true, true)
          (legsCall, "lic_ser", "off", false, false)
          (legsCall, "lic_ser", "fast", false, false)
          (legsStrict, "rep_ser", "off", true, true)
          (legsStrict, "lic_ser", "off", false, true)
          (legsStrict, "lic_ser", "fast", false, true) ]
    for (p, regime, contract, repro, bitwise) in legRegimes do
        let name = $"{p.Name} epilogue legs {regime} contract={contract} T=1 (NEW vs BLADE_EPILOGUE_LEGS=0)"
        use _c = pinEnv "BLADE_FP_CONTRACT" contract
        use _r = pinEnv "BLADE_FP_REPRO" (if repro then "1" else null)
        use _o = pinEnv "BLADE_OMP_THREADS" "1"
        let emitWith (gate: string) =
            use _g = pinEnv "BLADE_EPILOGUE_LEGS" (if isNull gate then "1" else gate)
            stemN <- stemN + 1
            let stem = $"epl_{p.Name}_{stemN}"
            emitCpp p stem |> Result.map (fun cpp -> (stem, cpp))
        match emitWith null, emitWith "0" with
        | Error e, _ | _, Error e -> fail name e
        | Ok (sNew, cNew), Ok (sOld, cOld) ->
        let needle = (p.Needle regime).Value
        if not (cNew.Contains needle) then fail name ($"the legs did not ride: no `{needle}` in the NEW emission")
        elif cOld.Contains "__epl_" then fail name "BLADE_EPILOGUE_LEGS=0 still emitted a leg (`__epl_`)"
        else
        match injectProbe p cNew, injectProbe p cOld with
        | None, _ | _, None -> fail name "could not place the raw-bit probe in main"
        | Some pNew, Some pOld ->
        match buildRun sNew pNew "1", buildRun sOld pOld "1" with
        | Error e, _ | _, Error e when e.StartsWith "compile: Skipped" -> skip name e
        | Error e, _ -> fail name ("NEW: " + e)
        | _, Error e -> fail name ("CTRL: " + e)
        | Ok oNew, Ok oOld ->
            let rNew, rOld = rawLines oNew, rawLines oOld
            if rNew.Length <> p.Scalars.Length + 1 || rOld.Length <> rNew.Length then
                fail name ($"raw-bit probe printed {rNew.Length}/{rOld.Length} lines")
            elif bitwise then
                if rNew = rOld then pass name ($"""bitwise NEW == CTRL: {String.concat "; " rNew}""")
                else fail name ($"""raw bits differ: NEW [{String.concat "; " rNew}] CTRL [{String.concat "; " rOld}]""")
            else
                let vals (o: string) =
                    valueLines o
                    |> List.filter (fun l -> p.Scalars |> List.exists (fun s -> l.StartsWith (s + " = ")))
                    |> List.map (fun l -> Double.Parse(l.Substring(l.IndexOf " = " + 3).Trim(), Globalization.CultureInfo.InvariantCulture))
                let a, b = vals oNew, vals oOld
                let close = a.Length = p.Scalars.Length && a.Length = b.Length && List.forall2 (fun (x: float) (y: float) -> abs (x - y) <= 1e-12 * max (abs x) (abs y)) a b
                if close then pass name ($"""licensed legs (a licensed association change) within 1e-12 relative ON THIS FIXTURE, not a general bound (bits {(if rNew = rOld then "identical" else "differ")}): {String.concat "; " rNew}""")
                else fail name ($"values differ beyond 1e-12: NEW {a} CTRL {b}")
    // The LLVM lane: identity regime, gate on vs off.
    (match Build.resolveClang () with
     | None -> skip "llvm epilogue legs" "no clang (BLADE_LLVM_CLANG)"
     | Some _ ->
     for p in [ legsCall; legsStrict ] do
        let name = $"{p.Name} llvm epilogue legs (REPRO, contract=off, T=1): gate on == gate off"
        use _c = pinEnv "BLADE_FP_CONTRACT" "off"
        use _r = pinEnv "BLADE_FP_REPRO" "1"
        use _o = pinEnv "BLADE_OMP_THREADS" "1"
        let runWith (gate: string) =
            use _g = pinEnv "BLADE_EPILOGUE_LEGS" (if isNull gate then "1" else gate)
            stemN <- stemN + 1
            let stem = $"epl_ll_{p.Name}_{stemN}"
            match lower p.Source with
            | Error e -> Error ("lower: " + e)
            | Ok ir ->
            match (try EmitLlvm.tryEmitProgramNamed stem ir with ex -> Error ("raised: " + ex.Message)) with
            | Error why -> Error ($"the llvm lane refused a pinned supported program: {why}")
            | Ok ll ->
            let llFile = Path.Combine(work, stem + ".ll")
            File.WriteAllText(llFile, ll)
            match Build.compileLlvmProgram llFile work with
            | Error e -> Error ("llvm compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
            | Ok exe ->
            use _t = pinEnv "OMP_NUM_THREADS" "1"
            // RAW BITS (W2-3's instrument): every printed Float64 carries its
            // IEEE-754 pattern, so equal lines are equal bits.
            use _b = pinEnv "BLADE_PRINT_BITS" "1"
            match runProc exe 120000 with
            | Ok (0, out, _) -> Ok (ll, out)
            | Ok (code, _, err) -> Error ($"llvm exit {code}: {err.Trim()}")
            | Error e -> Error e
        match runWith null, runWith "0" with
        | Error e, _ | _, Error e when e.StartsWith "llvm compile: Skipped" -> skip name e
        | Error e, _ | _, Error e -> fail name e
        | Ok (llOn, oOn), Ok (llOff, oOff) ->
            let loops (s: string) = s.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.StartsWith "loop.cond") |> Array.length
            let a, b = valueLines oOn, valueLines oOff
            let bitsShown = a |> List.forall (fun l -> l.Contains " #")
            // the joined pass's loop is gone (the call form's inlined callee
            // is not emitted when nothing calls it any more)
            let armOk = loops llOn = loops llOff - 1
            if a.IsEmpty || not bitsShown then fail name ($"""no raw bits printed (BLADE_PRINT_BITS): [{String.concat " | " a}]""")
            elif a <> b then fail name ($"""raw bits differ: on [{String.concat " | " a}] off [{String.concat " | " b}]""")
            elif not armOk then fail name ($"arm needle: {loops llOn} loops on vs {loops llOff} off")
            else pass name ($"""bitwise gate on == off: {String.concat " | " a}; {loops llOn} vs {loops llOff} loops"""))
    // The failure fixtures, executed (W2-4 review finding 3): the full fault /
    // site / frame text with the gate on equals gate-off's, both lanes.
    //   sibling-folds/026: the legs RIDE (pinned in `blade test optimize`); the
    //     intervening BL8013 still fires at its own line, after the producer.
    //   sibling-folds/029: a B1-shaped THROWING callee is never inlined for the
    //     legs; BL8013 fires inside `kernel`'s own frame (`at kernel`).
    let corpusFile (rel: string) =
        [ Path.Combine(Directory.GetCurrentDirectory(), "tests", "corpus", rel)
          Path.Combine(AppContext.BaseDirectory, "tests", "corpus", rel) ] |> List.tryFind File.Exists
    for (rel, frame) in [ ("sibling-folds/026_input_legs_failure_between_aborts.blade", None)
                          ("sibling-folds/029_input_legs_throwing_callee_keeps_frame.blade", Some "at kernel")
                          ("sibling-folds/034_input_legs_function_caller_failure_after_return.blade", Some "at driver")
                          ("sibling-folds/037_input_legs_decline_alias_function_caller_aborts.blade", Some "at driver")
                          ("sibling-folds/041_input_legs_decline_nested_alias_function_caller_aborts.blade", Some "at driver")
                          ("sibling-folds/045_input_legs_decline_user_call_function_caller_aborts.blade", Some "at driver") ] do
      for lane in [ "cpp"; "llvm" ] do
        let name = $"{Path.GetFileNameWithoutExtension rel} {lane}: fault, site and frame equal gate on vs off"
        match corpusFile rel with
        | None -> fail name "corpus file not found"
        | Some path ->
        let src = File.ReadAllText path
        let src = src.Substring(src.IndexOf '\n' + 1)
        use _o = pinEnv "BLADE_OMP_THREADS" "1"
        let runWith (gate: string) : Result<int * string, string> =
            use _g = pinEnv "BLADE_EPILOGUE_LEGS" (if isNull gate then "1" else gate)
            stemN <- stemN + 1
            let stem = $"epl_fault_{lane}_{stemN}"
            match lower src with
            | Error e -> Error ("lower: " + e)
            | Ok ir ->
            let exeR =
                if lane = "cpp" then
                    match (try Ok (fst (CodeGen.genSelfContainedProgramFromIR ir stem)) with ex -> Error ex.Message) with
                    | Error e -> Error e
                    | Ok cpp ->
                        let file = Path.Combine(work, stem + ".cpp")
                        File.WriteAllText(file, cpp)
                        Build.compileCpp file work
                else
                    match (try EmitLlvm.tryEmitProgramNamed stem ir with ex -> Error ("raised: " + ex.Message)) with
                    | Error why -> Error ("the llvm lane refused: " + why)
                    | Ok ll ->
                        let llFile = Path.Combine(work, stem + ".ll")
                        File.WriteAllText(llFile, ll)
                        Build.compileLlvmProgram llFile work
            match exeR with
            | Error e -> Error e
            | Ok exe ->
                use _t = pinEnv "OMP_NUM_THREADS" "1"
                match runProc exe 120000 with
                | Ok (code, out, err) -> Ok (code, (out + "\n" + err).Replace("\r\n", "\n").Trim())
                | Error e -> Error e
        match runWith null, runWith "0" with
        | Error e, _ | _, Error e when e.Contains "Skipped" -> skip name e
        | Error e, _ | _, Error e -> fail name e
        | Ok (cOn, tOn), Ok (cOff, tOff) ->
            let frameOk = match frame with Some f -> tOn.Contains f | None -> true
            if cOn <> 0 && cOn = cOff && tOn = tOff && tOn.Contains "BL8013" && frameOk then
                let lineTxt = tOn.Split('\n') |> Array.filter (fun l -> l.Contains "BL8013" || l.Contains "-->" || l.Trim().StartsWith "at ") |> String.concat " / "
                pass name ($"exit {cOn} both; identical text: {lineTxt}")
            else fail name ($"exit on={cOn} off={cOff}; frame={frameOk}; on [{tOn}] off [{tOff}]")
    // The return-boundary releases (W2-4 second review NEW 1-2), executed:
    // sibling-folds/030 (the join inside the inlined callee, a later map
    // reading the producer) and 031 (two plans riding one inlined call), gate
    // on vs off, BLADE_POOL_REUSE=off. Under BLADE_MEMCHECK=1 (ASan, the pool
    // cache OFF) every release is a real free: no ASan report, and for 031 the
    // alloc / free / outstanding census equals gate-off's (one release per
    // allocation). For 030 gate-off keeps W1-D's own join-driven inline of
    // `later`, whose pools then live to exit, so gate-on may free MORE, never
    // less. Then the ordinary build (pool cache ON): printed values equal.
    for rel in [ "sibling-folds/030_input_legs_join_inside_inlined_callee.blade"
                 "sibling-folds/031_input_legs_two_plans_one_inlined_call.blade"
                 "sibling-folds/032_input_legs_unused_final_join_atom_return.blade"
                 "sibling-folds/033_input_legs_cse_barrier_function_caller.blade"
                 "sibling-folds/035_input_legs_scratch_reuse_inside_callee.blade" ] do
        let name = $"{Path.GetFileNameWithoutExtension rel}: return-boundary releases under ASan and with the pool cache"
        match corpusFile rel with
        | None -> fail name "corpus file not found"
        | Some path ->
        let src = File.ReadAllText path
        let src = src.Substring(src.IndexOf '\n' + 1)
        use _o = pinEnv "BLADE_OMP_THREADS" "1"
        use _r = pinEnv "BLADE_FP_REPRO" "1"
        use _c = pinEnv "BLADE_FP_CONTRACT" "off"
        // 035 is the scratch-reuse witness: reuse ON there (the default), off elsewhere.
        use _pr = pinEnv "BLADE_POOL_REUSE" (if rel.Contains "035" then null else "off")
        let runWith (gate: string) (memcheck: bool) : Result<int * string, string> =
            use _g = pinEnv "BLADE_EPILOGUE_LEGS" (if isNull gate then "1" else gate)
            use _m = pinEnv "BLADE_MEMCHECK" (if memcheck then "1" else null)
            stemN <- stemN + 1
            let stem = $"epl_rel_{stemN}"
            match emitCpp { saxpySq with Name = stem; Source = src } stem with
            | Error e -> Error e
            | Ok cpp ->
            let file = Path.Combine(work, stem + ".cpp")
            File.WriteAllText(file, cpp)
            match Build.compileCpp file work with
            | Error e -> Error ("compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
            | Ok exe ->
                use _t = pinEnv "OMP_NUM_THREADS" "1"
                match runProc exe 120000 with
                | Ok (code, out, err) -> Ok (code, (out + "\n" + err).Replace("\r\n", "\n"))
                | Error e -> Error e
        let census (t: string) =
            t.Split('\n') |> Array.tryFind (fun l -> l.Contains "BLADE-MEMCHECK:")
            |> Option.map (fun l ->
                let num (k: string) = let m = Text.RegularExpressions.Regex.Match(l, k + @"=(\d+)") in if m.Success then int64 m.Groups.[1].Value else -1L
                (num "allocs", num "frees", num "outstanding_bytes"))
        match runWith null true, runWith "0" true, runWith null false, runWith "0" false with
        | Error e, _, _, _ | _, Error e, _, _ when e.Contains "Skipped" || e.Contains "sanitize" -> skip name e
        | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> fail name e
        | Ok (aOn, tOn), Ok (aOff, tOff), Ok (nOn, vOn), Ok (nOff, vOff) ->
            let asanClean (t: string) = not (t.Contains "ERROR: AddressSanitizer") && not (t.Contains "double-free") && not (t.Contains "heap-use-after-free")
            let values (t: string) = t.Split('\n') |> Array.filter (fun l -> l.Contains " = " && not (l.Contains "completed in")) |> List.ofArray
            let cOn, cOff = census tOn, census tOff
            let censusOk =
                match cOn, cOff with
                | Some (a1, f1, o1), Some (a2, f2, o2) ->
                    // 031 / 033: the inline changes no lifetime -- identical census.
                    // 035: scratch reuse is forgone for the inlined callee's pools
                    // (the documented exclusion): one more allocation and one more
                    // release, nothing left outstanding that gate-off frees.
                    // 030 / 032: gate-off keeps W1-D's own join-driven inline, whose
                    // pools then live to exit (pre-existing): gate-on frees more.
                    if rel.Contains "031" || rel.Contains "033" then a1 = a2 && f1 = f2 && o1 = o2
                    elif rel.Contains "035" then a1 = a2 + 1L && f1 = f2 + 1L && o1 = o2
                    else a1 = a2 && f1 >= f2 && o1 <= o2
                | _ -> false
            if aOn <> 0 || aOff <> 0 || nOn <> 0 || nOff <> 0 then fail name ($"exit codes asan {aOn}/{aOff} plain {nOn}/{nOff}: {tOn}")
            elif not (asanClean tOn && asanClean tOff) then fail name ($"ASan reported: {tOn}")
            elif not censusOk then fail name ($"census on {cOn} off {cOff}")
            elif values vOn <> values vOff || values vOn <> values tOn then fail name "printed values differ between gate on / off or between the cache-on and cache-off builds"
            else pass name ($"ASan clean; census on {cOn} off {cOff} (allocs, frees, outstanding bytes); values equal with the pool cache on and off")
    // G54 (W4-2 + its fix round). memfree/027: a function-local `y` read ONLY
    // through a capture in a PROVED-SYNCHRONOUS fold kernel (the map kernel of
    // a scalar fold let: one reference, writes nothing, returns no function)
    // is freed at scope exit like its sibling `t`; `kernel` runs 10 times.
    //   (1) C++: the emitted `kernel` frees every pool it allocates;
    //   (2) LLVM: the native lane's `kernel` frees both pools before its
    //       `ret`, in allocation order (t's, then y's);
    //   (3) under BLADE_MEMCHECK=1 (ASan, the pool cache OFF) the run is
    //       clean, prints the pinned values, and leaves less than one call's
    //       worth of `y` (10 x 35 doubles = 2800 bytes) outstanding.
    // memfree/028, the ESCAPING controls (a closure over `y` stored by an
    // assignment, passed as an argument and stored, stored by a fold kernel,
    // returned -- each invoked after its helper returned): no helper frees its
    // `y`, and the ASan run is clean with the pinned values (the first W4-2
    // build freed one: heap-use-after-free). The LLVM lane refuses function
    // values, so 028 is a C++-lane pin only.
    (let count (needle: string) (s: string) =
         let mutable n = 0
         let mutable at = s.IndexOf needle
         while at >= 0 do
             n <- n + 1
             at <- s.IndexOf(needle, at + needle.Length)
         n
     let srcOf (rel: string) =
         corpusFile rel |> Option.map (fun path ->
             let src = File.ReadAllText path
             src.Substring(src.IndexOf '\n' + 1))
     let build (src: string) (memcheck: bool) : Result<string * string, string> =
         use _m = pinEnv "BLADE_MEMCHECK" (if memcheck then "1" else null)
         stemN <- stemN + 1
         let stem = $"g54_free_{stemN}"
         match emitCpp { saxpySq with Name = stem; Source = src } stem with
         | Error e -> Error e
         | Ok cpp ->
         let file = Path.Combine(work, stem + ".cpp")
         File.WriteAllText(file, cpp)
         match Build.compileCpp file work with
         | Error e -> Error ("compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
         | Ok exe ->
             match runProc exe 120000 with
             | Ok (code, out, err) when code = 0 -> Ok (cpp.Replace("\r\n", "\n"), (out + "\n" + err).Replace("\r\n", "\n"))
             | Ok (code, out, err) -> Error ($"exit {code}: {out} {err}")
             | Error e -> Error e
     // the DEFINITION (`<ret> name(...) {`), not the forward declaration
     let defBody (cpp: string) (fname: string) =
         let dm = Text.RegularExpressions.Regex.Match(cpp, "\n[^\n;]* " + fname + @"\([^;{]*\) \{\n")
         let defAt = if dm.Success then dm.Index + dm.Length - 2 else -1
         let endAt = if defAt < 0 then -1 else cpp.IndexOf("\n}\n", defAt)
         if endAt < 0 then None else Some (cpp.Substring(defAt, endAt - defAt))
     let census (t: string) =
         t.Split('\n') |> Array.tryFind (fun l -> l.Contains "BLADE-MEMCHECK:")
     let num (l: string) (k: string) =
         let m = Text.RegularExpressions.Regex.Match(l, k + @"=(\d+)")
         if m.Success then int64 m.Groups.[1].Value else -1L
     // ---- memfree/027 ----
     (let name = "memfree/027 (G54): fold-captured function local freed at scope exit; C++ frees, LLVM frees + order, ASan census"
      match srcOf "memfree/027_fold_captured_function_local_freed.blade" with
      | None -> fail name "corpus file not found"
      | Some src ->
      match build src false with
      | Error e -> fail name e
      | Ok (cpp, plain) ->
      let valuesOk (t: string) = t.Contains "k2 = 57155.0" && t.Contains "total = 2834895.0"
      match defBody cpp "kernel" with
      | None -> fail name "no `double kernel(...)` definition in the emission"
      | Some body ->
      let allocs = count "= { allocate<" body
      let frees = count "deallocate<" body
      if allocs < 2 || frees <> allocs then
          fail name ($"C++ kernel allocates {allocs} pools and frees {frees} (want every pool freed, >= 2)")
      elif not (valuesOk plain) then fail name ($"values: {plain}")
      else
      // LLVM: `@bl_kernel_*`'s two pool slots, freed before `ret` in allocation order
      let llvmVerdict =
          match lower src with
          | Error e -> Error ("lower: " + e)
          | Ok ir ->
          match (try EmitLlvm.tryEmitProgramNamed "g54_llvm" ir with ex -> Error ("raised: " + ex.Message)) with
          | Error why -> Error ("the llvm lane refused memfree/027: " + why)
          | Ok ll ->
          let ll = ll.Replace("\r\n", "\n")
          let fm = Text.RegularExpressions.Regex.Match(ll, @"\ndefine internal double @bl_kernel_\d+\([^\n]*\{\n")
          if not fm.Success then Error "no `@bl_kernel_*` definition in the LLVM emission" else
          let fEnd = ll.IndexOf("\n}\n", fm.Index)
          let fb = ll.Substring(fm.Index, fEnd - fm.Index)
          let slots =
              Text.RegularExpressions.Regex.Matches(fb, @"(%r\d+) = call ptr @blade_alloc_cells\([^\n]*\n\s*store ptr \1, ptr (%r\d+)")
              |> Seq.map (fun m -> m.Groups.[2].Value) |> List.ofSeq
          let freed =
              Text.RegularExpressions.Regex.Matches(fb, @"(%r\d+) = load ptr, ptr (%r\d+)\n\s*call void @blade_free\(ptr \1\)")
              |> Seq.map (fun m -> m.Groups.[2].Value) |> List.ofSeq
          let retAt = fb.LastIndexOf "\n  ret double"
          let lastFree = fb.LastIndexOf "call void @blade_free"
          if slots.Length <> 2 then Error ($"LLVM kernel allocates {slots.Length} pools (want 2): {slots}")
          elif freed <> slots then Error ($"LLVM kernel frees slots {freed}, want {slots} (both, allocation order)")
          elif lastFree < 0 || retAt < lastFree then Error "LLVM kernel's frees are not before its `ret`"
          else Ok ($"""LLVM frees {String.concat ", " freed} (allocation order) before `ret`""")
      match llvmVerdict with
      | Error e -> fail name e
      | Ok llvmNote ->
      match build src true with
      | Error e when e.Contains "Skipped" || e.Contains "sanitize" || e.Contains "vcvars" -> skip name e
      | Error e -> fail name e
      | Ok (_, t) ->
          match census t with
          | None -> fail name ($"no BLADE-MEMCHECK census line: {t}")
          | Some l ->
              let outstanding = num l "outstanding_bytes"
              if t.Contains "ERROR: AddressSanitizer" then fail name ($"ASan reported: {t}")
              elif not (valuesOk t) then fail name ($"ASan build values: {t}")
              elif outstanding < 0L || outstanding >= 2800L then
                  fail name ($"outstanding_bytes = {outstanding} (>= 2800: a per-call pool survives): {l.Trim()}")
              else pass name ($"C++ kernel frees all {frees} of its pools; {llvmNote}; ASan clean; census {l.Trim()}"))
     // ---- memfree/028: the escaping controls ----
     (let name = "memfree/028 (G54 controls): escaping closures keep their captured `y`; ASan clean"
      match srcOf "memfree/028_escaping_closure_captures_stay_alive.blade" with
      | None -> fail name "corpus file not found"
      | Some src ->
      match build src false with
      | Error e -> fail name e
      | Ok (cpp, plain) ->
      let want = [ "v1 = 60.0"; "v2 = 31.0"; "v3 = 38.0"; "v4 = 100.0" ]
      let freesIn =
          [ "store_direct"; "store_via_arg"; "store_in_kernel"; "mk" ]
          |> List.map (fun f -> (f, defBody cpp f |> Option.map (fun b -> (count "= { allocate<" b, count "deallocate<" b))))
      match freesIn |> List.tryFind (fun (_, r) -> match r with Some (a, fr) -> a <> 1 || fr <> 0 | None -> true) with
      | Some (f, r) -> fail name (sprintf "helper %s (allocs, frees) = %A: want its one `y` allocated and NOT freed" f r)
      | None ->
      if want |> List.exists (fun w -> not (plain.Contains w)) then fail name ($"values: {plain}") else
      match build src true with
      | Error e when e.Contains "Skipped" || e.Contains "sanitize" || e.Contains "vcvars" -> skip name e
      | Error e -> fail name e
      | Ok (_, t) ->
          if t.Contains "ERROR: AddressSanitizer" then fail name ($"ASan reported: {t}")
          elif want |> List.exists (fun w -> not (t.Contains w)) then fail name ($"ASan build values: {t}")
          else pass name ($"""no helper frees its captured `y`; ASan clean; {(census t |> Option.map (fun l -> l.Trim()) |> Option.defaultValue "no census")}"""))
     // ---- memfree/029-035, functions/545-547: the RETURN classified slot by slot (W4B-C) ----
     // Each file's `kernel` allocates and frees EXACTLY the pinned pool counts:
     // every pool freed where the captured `y` is read only by a proved-
     // synchronous kernel slot (031 the return-position fold, 035 an
     // object_for kernel counted once, 547 a fold let followed by a direct read
     // of `y`); `y` kept where a closure over it escapes (029 a scalar-
     // arithmetic tail store, 030 a tail call, 032 a return fold beside a
     // stored closure, 033 one kernel referenced twice, 034 a writing return
     // fold kernel, 545 a fold SEED that stores a closure -- each closure
     // invoked after `kernel` returned) or where the slot is not proved (546
     // one kernel in two object_for folds: two references, `y` pinned, a
     // conservative leak; `t` freed). Plain build: the emission + the pinned
     // values; BLADE_MEMCHECK=1: ASan clean with the same values.
     for (rel, fname, (wantAllocs, wantFrees), want) in
           [ ("memfree/029_scalar_return_call_stores_closure_keeps_capture.blade", "kernel", (1, 0), [ "r = 120.0"; "v = 60.0" ])
             ("memfree/030_tail_call_stores_closure_keeps_capture.blade", "kernel", (1, 0), [ "r = 0.0"; "v = 180.0" ])
             ("memfree/031_return_position_fold_kernel_capture_freed.blade", "kernel", (2, 2), [ "k2 = 57155.0"; "total = 2834895.0" ])
             ("memfree/032_return_fold_plus_stored_closure_keeps_capture.blade", "kernel", (1, 0), [ "r = 120.0"; "v = 60.0" ])
             ("memfree/033_kernel_referenced_twice_keeps_capture.blade", "kernel", (1, 0), [ "r = 120.0"; "v = 60.0" ])
             ("memfree/034_return_fold_writing_kernel_keeps_capture.blade", "kernel", (1, 0), [ "r = 40.0"; "v = 38.0" ])
             ("memfree/035_object_for_kernel_capture_freed.blade", "kernel", (2, 2), [ "k2 = 180.0"; "k3 = 390.0" ])
             ("functions/545_fold_seed_stores_closure_keeps_capture.blade", "kernel", (1, 0), [ "r = 120.0"; "v = 60.0" ])
             ("functions/546_one_kernel_two_object_for_folds_keeps_capture.blade", "kernel", (2, 1), [ "k2 = 361.0"; "k3 = 781.0" ])
             // (1, 1) since compute elision (BLADE_COMPUTE_ELIDE, plan-identity-
             // transpose-compute P3): `t`'s single pointwise consumer `y` reads
             // its map in place, so the kernel owns ONE pool (`y`), freed once;
             // it was (2, 2) with `t` stored.
             ("functions/547_fold_let_then_direct_read_of_captured_array.blade", "kernel", (1, 1), [ "k2 = 170.0"; "k3 = 342.0" ])
             ("functions/548_memfree_fold_below_scalar_if_return_freed.blade", "kernel", (1, 1), [ "yes = 55.0"; "no = 0.0"; "total = 5300.0" ])
             ("functions/548_memfree_fold_below_scalar_if_return_freed.blade", "kjoin", (1, 1), [ "s = 15.0"; "ss = 55.0"; "total = 5300.0" ])
             ("functions/548_memfree_fold_below_scalar_if_return_freed.blade", "kfma", (1, 1), [ "r = 31.0"; "total = 5300.0" ])
             ("functions/548_memfree_fold_below_scalar_if_return_freed.blade", "ktag", (1, 1), [ "rt = 4"; "total = 5300.0" ])
             ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "kernel", (1, 1), [ "b1 = 110.0"; "b0 = 1.0"; "total = 4300.0" ])
             ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "kcplx", (1, 1), [ "c = 30.0"; "total = 4300.0" ])
             ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "kunit", (1, 1), [ "u = 15.0"; "total = 4300.0" ])
             ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "kinv", (1, 1), [ "vi = 3.0"; "total = 4300.0" ])
             ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "kwr", (1, 1), [ "vw = 4.0"; "total = 4300.0" ])
             ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "kwrx", (1, 0), [ "vx = 4.0"; "vc = 32.0" ])
             ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "kesc", (1, 0), [ "re = 59.0"; "ve = 32.0" ])
             ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "kidxw", (1, 0), [ "ri = 4.0"; "cc = 2" ])
             ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "krhsw", (1, 0), [ "rr = 4.0"; "cc = 2" ]) ] do
         let name = $"""{rel.Substring(0, rel.IndexOf('_'))} (G54 slots): {fname} allocates {wantAllocs}, frees {wantFrees}; values; ASan clean"""
         match srcOf rel with
         | None -> fail name "corpus file not found"
         | Some src ->
         match build src false with
         | Error e -> fail name e
         | Ok (cpp, plain) ->
         match defBody cpp fname with
         | None -> fail name ($"no `{fname}(...)` definition in the emission")
         | Some body ->
         let allocs = count "= { allocate<" body
         let frees = count "deallocate<" body
         if allocs <> wantAllocs || frees <> wantFrees then
             fail name ($"C++ kernel allocates {allocs} pools and frees {frees} (want {wantAllocs} / {wantFrees})")
         elif want |> List.exists (fun w -> not (plain.Contains w)) then fail name ($"values: {plain}")
         else
         match build src true with
         | Error e when e.Contains "Skipped" || e.Contains "sanitize" || e.Contains "vcvars" -> skip name e
         | Error e -> fail name e
         | Ok (_, t) ->
             if t.Contains "ERROR: AddressSanitizer" then fail name ($"ASan reported: {t}")
             elif want |> List.exists (fun w -> not (t.Contains w)) then fail name ($"ASan build values: {t}")
             else pass name ($"""kernel allocates {allocs}, frees {frees}; ASan clean; {(census t |> Option.map (fun l -> l.Trim()) |> Option.defaultValue "no census")}"""))
    // ---- functions/548: no per-call growth (the second Codex look, finding 1) ----
    // The fold below a scalar `if` return frees `y` on every call: under
    // BLADE_MEMCHECK=1 the OUTSTANDING BLOCKS after 40 steps equal those after 4
    // (the same program, `Steps = Idx<4>`), both branches taken along the way;
    // the outstanding BYTES differ by exactly the longer trajectory (36 x 8 =
    // 288), where a per-call `y` would add 36 blocks / 1440 bytes.
    for (rel, wantTotal) in [ ("functions/548_memfree_fold_below_scalar_if_return_freed.blade", "total = 5300.0"); ("functions/549_memfree_fold_below_block_in_if_arm_freed.blade", "total = 4300.0") ] do
     let name = $"""{rel.Substring(0, rel.IndexOf('_'))} (G54 slots): outstanding blocks do not grow with the call count (40 vs 4 steps, ASan)"""
     let path =
         [ Path.Combine(Directory.GetCurrentDirectory(), "tests", "corpus", rel)
           Path.Combine(AppContext.BaseDirectory, "tests", "corpus", rel) ] |> List.tryFind File.Exists
     match path with
     | None -> fail name "corpus file not found"
     | Some p ->
     let src40 = (let s = File.ReadAllText p in s.Substring(s.IndexOf '\n' + 1))
     let src4 = src40.Replace("type Steps = Idx<40>", "type Steps = Idx<4>")
     let outstanding (tag: string) (src: string) =
         use _m = pinEnv "BLADE_MEMCHECK" "1"
         match emitCpp { saxpySq with Name = tag; Source = src } tag with
         | Error e -> Error e
         | Ok cpp ->
         let file = Path.Combine(work, tag + ".cpp")
         File.WriteAllText(file, cpp)
         match Build.compileCpp file work with
         | Error e -> Error ("compile: " + e)
         | Ok exe ->
         match runProc exe 120000 with
         | Ok (0, out, err) ->
             let t = out + "\n" + err
             let m = Text.RegularExpressions.Regex.Match(t, @"outstanding_bytes=(\d+) outstanding_blocks=(\d+)")
             if t.Contains "ERROR: AddressSanitizer" then Error ("ASan: " + t)
             elif m.Success then Ok ((int64 m.Groups.[1].Value, int64 m.Groups.[2].Value), t) else Error ("no census: " + t)
         | Ok (code, out, err) -> Error ($"exit {code}: {out} {err}")
         | Error e -> Error e
     let stem = rel.Substring(rel.IndexOf('/') + 1, 3)
     match outstanding $"g54_{stem}_40" src40, outstanding $"g54_{stem}_4" src4 with
     | Error e, _ | _, Error e when e.Contains "Skipped" || e.Contains "sanitize" || e.Contains "vcvars" -> skip name e
     | Error e, _ | _, Error e -> fail name e
     | Ok ((o40, b40), t40), Ok ((o4, b4), _) ->
         if b40 <> b4 || o40 - o4 <> 288L then fail name ($"outstanding {o40} bytes / {b40} blocks after 40 steps vs {o4} / {b4} after 4 (want equal blocks, +288 bytes): a per-call pool survives")
         elif not (t40.Contains wantTotal) then fail name ($"values: {t40}")
         else pass name ($"outstanding {b40} blocks after 40 steps = {b4} after 4 (bytes {o40} vs {o4}: the trajectory); ASan clean")
    // ---- G67 (W4B-C fix round): the zero-level compact application IS the call ----
    // File-backed twins: the DIRECT call `group_h(op)` vs the zero-level
    // application `object_for(group_h) <@> op` (bare and `|> compute`d, the
    // direct call `|> compute`d as its twin), with a FAILING compact-returning
    // kernel (kfail), a DEFERRED operand (dop), a deferred FAILING operand the
    // kernel ignores (dfign) and uses (dfuse), and two competing failing
    // deferred operands (compete: BL8013 in the first, BL8014 in the second).
    // Per scenario, the SELECTED normalized lines -- the printed `name = value`
    // lines (x0 / c0 excluded) and the `error[..]` / `--> <file>:<line>` /
    // `at <frame>` lines -- are the
    // SAME for every spelling in each lane, and the same in the C++ lane, the
    // NATIVE llvm lane (`tryEmitProgramNamed` must succeed) and the
    // interpreter. ONE named exception (gap G88, pre-existing, not the
    // zero-level rewrite): the C++ lane refuses `f(op) |> compute` over a
    // DEFERRED operand at g++ (`'<op>' was not declared`, BL9002) for the
    // direct call and the application alike, so there both spellings are
    // asserted refused with the same text and the C++ value is not compared.
    (let pre =
         "type P = Idx<3>\ntype S = Idx<4>\nlet x0 = Float64(0..3) |> compute\nlet zz = extents(x0) - 3\n"
         + "let c0 = method_for(range<P, S>) <@> lambda(a, s) -> Float64(a + s) |> compute\n"
         + "function group_h(jg: Array<Float64 like P, S>) = method_for(jg, jg) <@> lambda(ra, rb) where comm(ra, rb) -> prodsum(ra, rb)\n"
         + "function gfail(jg: Array<Float64 like P, S>) = method_for(jg, jg) <@> lambda(ra, rb) where comm(ra, rb) -> prodsum(ra, rb) * Float64(7 / zz)\n"
         + "function gign(jg: Array<Float64 like P, S>) = method_for(c0, c0) <@> lambda(ra, rb) where comm(ra, rb) -> prodsum(ra, rb)\n"
         + "function g2(ja: Array<Float64 like P, S>, jb: Array<Float64 like P, S>) = method_for(ja, ja) <@> lambda(ra, rb) where comm(ra, rb) -> prodsum(ra, rb)\n"
     // (scenario, operand lets, kernel, call arguments, deferred operand?, expected error line, `-->` line, frames)
     let scenarios =
         [ ("kfail", "let jg = method_for(range<P, S>) <@> lambda(a, s) -> Float64(24 + a * 4 + s) |> compute\nlet before = reduce(jg, (+))\n", "gfail", "jg", false,
            Some "error[BL8013]: integer division by zero", "--> <file>:7", [ "at gfail" ])
           ("dop", "let jd = method_for(range<P, S>) <@> lambda(a, s) -> Float64(24 + a * 4 + s)\nlet before: Float64 = 1.5\n", "group_h", "jd", true, None, "", [])
           ("dfign", "let jf = method_for(range<P, S>) <@> lambda(a, s) -> Float64(24 + a) * Float64(7 / zz)\nlet before: Float64 = 2.5\n", "gign", "jf", true,
            Some "error[BL8013]: integer division by zero", "--> <file>:10", [])
           ("dfuse", "let jf = method_for(range<P, S>) <@> lambda(a, s) -> Float64(24 + a) * Float64(7 / zz)\nlet before: Float64 = 2.5\n", "group_h", "jf", true,
            Some "error[BL8013]: integer division by zero", "--> <file>:10", [])
           ("compete", "let da = method_for(range<P, S>) <@> lambda(a, s) -> Float64(a) * Float64(7 / zz)\nlet db = method_for(range<P, S>) <@> lambda(a, s) -> Float64(Int64(floor(Float64(a) / 0.0 * 0.0)))\nlet before: Float64 = 3.5\n", "g2", "da, db", true,
            Some "error[BL8013]: integer division by zero", "--> <file>:10", []) ]
     let keep (l: string) =
         let l = l.Trim()
         let l = if l.StartsWith "[stderr]: " then l.Substring 10 else l
         if l.StartsWith "error[" || l.StartsWith "at " then Some l
         elif l.StartsWith "--> " then Some (Text.RegularExpressions.Regex.Replace(l, @"^--> .*?\.blade:", "--> <file>:"))
         elif l.Contains " = " && not (l.StartsWith "x0 =") && not (l.StartsWith "c0 =") then Some l
         else None
     let shape (code: int) (text: string) =
         (code <> 0, text.Replace("\r\n", "\n").Split('\n') |> Array.choose keep |> List.ofArray)
     let lanes (stem: string) (src: string) : Result<(string * Result<bool * string list, string>) list, string> =
         let path = Path.Combine(work, stem + ".blade")
         File.WriteAllText(path, src)
         let lowerIt () =
             match fst (Lowering.lowerDiag (Some path) src) with
             | Ok (ir, _) -> Ok ir
             | Error ds -> Error (ds |> List.map (fun d -> d.Message) |> String.concat "; ")
         match lowerIt () with
         | Error e -> Error ("lower: " + e)
         | Ok _ ->
         let run (exe: string) =
             use _t = pinEnv "OMP_NUM_THREADS" "1"
             match runProc exe 120000 with
             | Ok (code, out, err) -> Ok (shape code (out + "\n" + err))
             | Error e -> Error e
         let cpp =
             match lowerIt () with
             | Error e -> Error e
             | Ok ir ->
             let code = fst (CodeGen.genSelfContainedProgramFromIR ir (stem + "_c"))
             CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
             let file = Path.Combine(work, stem + "_c.cpp")
             File.WriteAllText(file, code)
             match Build.compileCpp file work with
             | Error e ->
                 let first =
                     e.Replace("\r\n", "\n").Split('\n')
                     |> Array.tryFind (fun l -> l.Contains "was not declared")
                     |> Option.map (fun l -> l.Substring(l.IndexOf "error:"))
                 Ok (true, [ "C++ COMPILE REFUSED: " + defaultArg first "(other)" ])
             | Ok exe -> run exe
         let llvm =
             match lowerIt () with
             | Error e -> Error e
             | Ok ir ->
             match (try EmitLlvm.tryEmitProgramNamed (stem + "_l") ir with ex -> Error ("raised: " + ex.Message)) with
             | Error why -> Error ("the llvm lane REFUSED (native required): " + why)
             | Ok ll ->
                 let llFile = Path.Combine(work, stem + "_l.ll")
                 File.WriteAllText(llFile, ll)
                 match Build.compileLlvmProgram llFile work with
                 | Error e -> Error ("llvm compile: " + e)
                 | Ok exe -> run exe
         let interp =
             match lowerIt () with
             | Error e -> Error e
             | Ok ir ->
                 let res = Blade.Interp.Run.runProgram ir stem Blade.Interp.Value.defaultLimits
                 Ok (shape res.ExitCode (res.Stdout + "\n" + res.Stderr))
         Ok [ ("C++", cpp); ("llvm", llvm); ("interpreter", interp) ]
     for (scen, ops, f, args, deferred, err, loc, frames) in scenarios do
         let name = $"G67 zero-level compact call twins ({scen}): direct call == application, every lane"
         let isPack = args.Contains ","
         let applied = if isPack then $"({args})" else args
         let spellings =
             [ ("direct", $"let h = {f}({args})"); ("bare", $"let h = object_for({f}) <@> {applied}")
               ("dircomp", $"let h = {f}({args}) |> compute"); ("comp", $"let h = object_for({f}) <@> {applied} |> compute") ]
         let results =
             spellings |> List.map (fun (sp, line) ->
                 (sp, lanes $"g67_{scen}_{sp}" (pre + ops + line + "\nlet after = h((0 : P), (1 : P))\n")))
         match results |> List.tryPick (fun (sp, r) -> match r with Error e -> Some ($"{sp}: {e}") | Ok _ -> None) with
         | Some e -> fail name e
         | None ->
         let byLane (sp: string) =
             match results |> List.find (fun (s, _) -> s = sp) |> snd with
             | Ok ls -> ls
             | Error _ -> []
         let laneErr =
             results |> List.tryPick (fun (sp, r) ->
                 match r with
                 | Ok ls -> ls |> List.tryPick (fun (lane, x) -> match x with Error e -> Some ($"{sp} {lane}: {e}") | Ok _ -> None)
                 | Error _ -> None)
         match laneErr with
         | Some e -> fail name e
         | None ->
         let get sp lane = byLane sp |> List.find (fun (l, _) -> l = lane) |> snd |> function Ok x -> x | Error _ -> (false, [])
         // (1) each lane: the application equals its direct-call twin
         let twinBad =
             [ for lane in [ "C++"; "llvm"; "interpreter" ] do
                 for (a, b) in [ ("direct", "bare"); ("dircomp", "comp") ] do
                     if get a lane <> get b lane then yield $"{lane}: {a} {get a lane} vs {b} {get b lane}" ]
         // (2) across lanes, per spelling (G88: the C++ `|> compute` over a deferred operand is a g++ refusal)
         let g88 (sp: string) = deferred && (sp = "dircomp" || sp = "comp")
         let laneBad =
             [ for (sp, _) in spellings do
                 let l, i = get sp "llvm", get sp "interpreter"
                 if l <> i then yield $"{sp}: llvm {l} vs interpreter {i}"
                 let c = get sp "C++"
                 if g88 sp then
                     let refusedOk =
                         match c with
                         | (true, [ r ]) -> r.StartsWith "C++ COMPILE REFUSED: error: '" && r.Contains "was not declared"
                         | _ -> false
                     if not refusedOk then yield $"{sp}: C++ {c}, want the G88 g++ refusal"
                 elif c <> i then yield $"{sp}: C++ {c} vs interpreter {i}" ]
         // (3) the expected first failure (or success) itself
         let (failed0, lines0) = get "direct" "interpreter"
         let wantBad =
             match err with
             | Some e ->
                 let want = [ e; loc ] @ frames
                 let got = lines0 |> List.filter (fun l -> l.StartsWith "error[" || l.StartsWith "--> " || l.StartsWith "at ")
                 if failed0 && got = want then [] else [ $"first failure {got}, want {want}" ]
             | None ->
                 if not failed0 && lines0 |> List.contains "h = [[2606.0, 3014.0, 3422.0], [3486.0, 3958.0], [4494.0]]" && lines0 |> List.contains "after = 3014.0"
                 then [] else [ $"values {lines0}" ]
         match twinBad @ laneBad @ wantBad with
         | [] ->
             let g88Note = if deferred then "; C++ `|> compute` over the deferred operand: the G88 g++ refusal for the direct call and the application alike" else ""
             pass name ($"""{(if failed0 then "fails" else "runs")}: {String.concat " | " lines0}; native llvm + interpreter + C++ agree{g88Note}""")
         | bad -> fail name (String.concat " ;; " bad)
     // EXPLICIT-WRAPPER CONTROLS (the second Codex look, finding 2): only the
     // COMPILER-SYNTHESIZED eta wrapper of a named kernel is reduced to the
     // callee. A user-written wrapper -- a plain eta wrapper, one with its own
     // `where comm` clause, one permuting or dropping arguments, a let-bound
     // lambda kernel -- is called as the lambda it is: its failure carries the
     // callee's frame AND the wrapper's (`at lambda in h`), the same in every
     // lane; the synthesized wrapper (`object_for(gfail)`) carries the callee's
     // only. Each a bare zero-level application over the stored `jg`.
     let etaPre =
         pre + "function g2fail(ja: Array<Float64 like P, S>, jb: Array<Float64 like P, S>) = method_for(ja, ja) <@> lambda(ra, rb) where comm(ra, rb) -> prodsum(ra, rb) * Float64(7 / zz)\n"
         + "let jg = method_for(range<P, S>) <@> lambda(a, s) -> Float64(24 + a * 4 + s) |> compute\n"
     let fr1 = [ "at gfail"; "at lambda in h" ]
     let fr2 = [ "at g2fail"; "at lambda in h" ]
     for (ctl, line, frames) in
           [ ("user_eta", "let h = object_for(lambda(j: Array<Float64 like P, S>) -> gfail(j)) <@> jg", fr1)
             ("user_clause", "let h = object_for(lambda(a: Array<Float64 like P, S>, b: Array<Float64 like P, S>) where comm(a, b) -> g2fail(a, b)) <@> (jg, jg)", fr2)
             ("user_permuted", "let h = object_for(lambda(a: Array<Float64 like P, S>, b: Array<Float64 like P, S>) -> g2fail(b, a)) <@> (jg, jg)", fr2)
             ("user_dropped", "let h = object_for(lambda(a: Array<Float64 like P, S>, b: Array<Float64 like P, S>) -> gfail(a)) <@> (jg, jg)", fr1)
             ("let_bound_lambda", "let gl = lambda(j: Array<Float64 like P, S>) -> gfail(j)\nlet h = object_for(gl) <@> jg", fr1)
             ("synthesized", "let h = object_for(gfail) <@> jg", [ "at gfail" ]) ] do
         let name = $"""G67 zero-level eta control ({ctl}): wrapper frames {String.concat ", " frames}, every lane"""
         match lanes $"g67_eta_{ctl}" (etaPre + line + "\nlet after = h((0 : P), (1 : P))\n") with
         | Error e -> fail name e
         | Ok ls ->
         match ls |> List.tryPick (fun (lane, x) -> match x with Error e -> Some ($"{lane}: {e}") | Ok _ -> None) with
         | Some e -> fail name e
         | None ->
         let outs = ls |> List.map (fun (lane, x) -> (lane, match x with Ok v -> v | Error _ -> (false, [])))
         let (_, first) = outs.Head
         let got = snd first |> List.filter (fun l -> l.StartsWith "at ")
         if outs |> List.exists (fun (_, v) -> v <> first) then fail name (sprintf "lanes disagree: %A" outs)
         elif not (fst first) || got <> frames then fail name ($"frames {got}, want {frames}: {snd first}")
         else pass name ($"""{String.concat " | " (snd first)} in C++, native llvm and the interpreter"""))
    (try Directory.Delete(work, true) with _ -> ())
    printFooter blockName [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = names }
