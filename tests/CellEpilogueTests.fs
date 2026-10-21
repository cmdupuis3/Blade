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
            use _g = pinEnv "BLADE_EPILOGUE_LEGS" gate
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
            use _g = pinEnv "BLADE_EPILOGUE_LEGS" gate
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
                          ("sibling-folds/029_input_legs_throwing_callee_keeps_frame.blade", Some "at kernel") ] do
      for lane in [ "cpp"; "llvm" ] do
        let name = $"{Path.GetFileNameWithoutExtension rel} {lane}: fault, site and frame equal gate on vs off"
        match corpusFile rel with
        | None -> fail name "corpus file not found"
        | Some path ->
        let src = File.ReadAllText path
        let src = src.Substring(src.IndexOf '\n' + 1)
        use _o = pinEnv "BLADE_OMP_THREADS" "1"
        let runWith (gate: string) : Result<int * string, string> =
            use _g = pinEnv "BLADE_EPILOGUE_LEGS" gate
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
    (try Directory.Delete(work, true) with _ -> ())
    printFooter blockName [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = names }
