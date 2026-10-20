// Wave 3, E-G5 (bench gap G5): the threaded `let rec` step -- a `where omp`
// on the inductive arm's FINAL map runs the whole time loop on ONE team with
// fixed x ownership and no per-step synchronization (`Blade.Optimize.
// RecStepTable`, `CodeGenBinding.genRecStepTimeLoop`). The differential gates
// (`test interp`, `opt-diff`) run threads at the default count under
// BLADE_FP_REPRO=1, which proves the strict forms once; this block runs the
// threaded build against the THREADS-OFF build of the same source
// (BLADE_OMP_THREADS=1: the plain serial loop behind a census line) at fixed
// team sizes and checks RAW bits:
//   * the trajectory pool (bit hash, taken right before the function frees
//     it): identical in EVERY regime -- each cell is the same scalar chain
//     whoever owns it (the licensed forms change only how the ROW FOLD is
//     combined);
//   * the printed scalar (`%a`): identical under BLADE_FP_REPRO=1 with
//     contraction off (every row chain complete, the time chain in order --
//     the strict post-pass, serial or in parallel under the outer kernel's
//     request), and within 1e-12 relative in the licensed build (per-worker
//     row partials combined ascending: a different association, no bit
//     contract -- the licensed build has none);
//   * the ACTUAL team the region observed (thread 0 records it): the
//     requested count, or the thread limit when one is set;
//   * the needle proving the form fired (region, partials, slots, the serial
//     post-pass, the census line in the threads-off build).
// `blade test rec-step`; part of the default suite. Needs g++ (Skipped
// without it).
module Blade.Tests.RecStepTests

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

/// One checked program: the function holding the recursion (its rank-2
/// pool is hashed before it is freed), the printed scalar, and the needles
/// the threaded emission must contain per build (licensed / repro).
type private Program = {
    Name: string
    Source: string
    Func: string
    Cells: int
    Scalar: string
    NeedleLic: string list
    NeedleRep: string list
}

let private prelude =
    "type T = Idx<11>\n"
    + "type X = Idx<2003>\n"
    + "function rhs(u: Array<Float64 like X>) -> Array<Float64 like X> = u - u * u * u\n"
    + "function step(u: Array<Float64 like X>) -> Array<Float64 like X> = {\n"
    + "    let k1 = rhs(u)\n"
    + "    let k2 = rhs(u + 0.005 * k1)\n"
    + "    let k3 = rhs(u + 0.005 * k2)\n"
    + "    let k4 = rhs(u + 0.01 * k3)\n"
    + "    method_for(zip(u, k1, k2, k3, k4)) <@> lambda(v, a1, a2, a3, a4) where omp(v: 1) -> v + (0.01 / 6.0) * (a1 + 2.0 * a2 + 2.0 * a3 + a4) |> compute\n"
    + "}\n"
    + "let u0 = method_for(range<X>) <@> lambda(i) -> Float64(Int64(i) % 7) / 3.0 |> compute\n"

let private recursion (fname: string) (consumer: string) =
    $"function {fname}(u0: Array<Float64 like X>) -> Float64 = {{\n"
    + "    let rec traj: Array<Float64 like T, X> =\n"
    + "        match traj with\n"
    + "        | zero -> zero\n"
    + "        | zero :: s -> zero :: u0\n"
    + "        | prefix :: n -> prefix :: step(prefix(n - 1))\n"
    + $"    {consumer}\n"
    + "}\n"

let private region = "BLADE_OMP_PARALLEL_NUM_THREADS(__rsT_"

/// The self-dot row fold, no request on the outer kernel: licensed = in-team
/// partials; repro = complete rows serially after the team.
let private square =
    { Name = "square"
      Source = prelude + recursion "square" "reduce(method_for(traj) <@> lambda(row) -> prodsum(row, row), (+))" + "let q = square(u0)\n"
      Func = "square"; Cells = 11 * 2003; Scalar = "q"
      NeedleLic = [ region; "__rspart_"; "the row partials combined ascending" ]
      NeedleRep = [ region; "the row fold after the team, serial" ] }

/// The self-dot row fold WITH the outer kernel's request: repro = complete
/// rows into slots in parallel, the time chain in order.
let private outer =
    { Name = "outer"
      Source = prelude + recursion "outer" "reduce(method_for(traj) <@> lambda(row) -> prodsum(row, row), lambda(p, q) where omp -> p + q, 0.0)" + "let o = outer(u0)\n"
      Func = "outer"; Cells = 11 * 2003; Scalar = "o"
      NeedleLic = [ region; "__rspart_" ]
      NeedleRep = [ region; "__rsslot_"; "BLADE_OMP_PARALLEL_FOR" ] }

/// A non-self-dot row fold: complete rows serially after the team, every build.
let private plain =
    { Name = "plain"
      Source = prelude + recursion "plain" "reduce(method_for(traj) <@> lambda(row) -> reduce(row, (+)), (+))" + "let p = plain(u0)\n"
      Func = "plain"; Cells = 11 * 2003; Scalar = "p"
      NeedleLic = [ region; "the row fold after the team, serial" ]
      NeedleRep = [ region; "the row fold after the team, serial" ] }

/// The trajectory read after the fold too: no fused row fold, the team runs
/// with nothing inside, the fold afterwards.
let private stored =
    { Name = "stored"
      Source = prelude + recursion "stored" "reduce(method_for(traj) <@> lambda(row) -> reduce(row, (+)), (+)) + traj((10 : T), (1 : X))" + "let s = stored(u0)\n"
      Func = "stored"; Cells = 11 * 2003; Scalar = "s"
      NeedleLic = [ region; "persistent team" ]
      NeedleRep = [ region; "persistent team" ] }

let private programs = [ square; outer; plain; stored ]

/// One regime: name, repro, contraction, OMP_NUM_THREADS, OMP_THREAD_LIMIT
/// (null = unset), the actual team expected, bitwise on the scalar.
type private Regime = { RName: string; Repro: bool; Contract: string; Threads: string; Limit: string; Team: int; Bitwise: bool }

let private regimes =
    let r n rp c t l team bw = { RName = n; Repro = rp; Contract = c; Threads = t; Limit = l; Team = team; Bitwise = bw }
    [ r "lic" false "off" "1" null 1 false
      r "lic" false "off" "3" null 3 false
      r "lic" false "off" "8" null 8 false
      r "lic" false "fast" "8" null 8 false
      // requested 8, actual 2: partials sized by the request, filled by the team
      r "lic_lim" false "off" "8" "2" 2 false
      r "rep" true "off" "1" null 1 true
      r "rep" true "off" "3" null 3 true
      r "rep" true "off" "8" null 8 true
      r "rep_lim" true "off" "8" "2" 2 true ]

/// The raw-bit probe: `%a` of the scalar, the bit hash of the function's
/// rank-2 pool taken right before the function frees it, and the actual
/// team the region observed (thread 0; -1 = no region ran).
let private injectProbe (p: Program) (cpp0: string) : string option =
    let cpp = cpp0.Replace("\r\n", "\n")
    let teamLine = "const int __rnt = blade_omp_get_num_threads();"
    let cpp = cpp.Replace(teamLine, teamLine + " if (blade_omp_get_thread_num() == 0) __rs_team_seen = __rnt;")
    let fAt = cpp.IndexOf($"\ndouble {p.Func}(")
    if fAt < 0 then None else
    let defAt = cpp.IndexOf("{\n", fAt)
    let m = System.Text.RegularExpressions.Regex.Match(cpp.Substring(defAt), @"Array<double, 2> (__v\d+) = \{ allocate")
    if not m.Success then None else
    let buf = m.Groups.[1].Value
    let freeLine = $"    deallocate<typename promote<double, 2>::type, nullptr>({buf}.data, {buf}.extents);"
    let freeAt = cpp.IndexOf(freeLine, defAt)
    if freeAt < 0 then None else
    let hash = $"    {{ unsigned long long __h = 0; const double* __p = nested_array_utilities::pool_base({buf}.data); for (size_t __q = 0; __q < (size_t){p.Cells}; __q++) {{ unsigned long long __b; std::memcpy(&__b, __p + __q, 8); __h = __h * 1000003ULL ^ __b; }} __rs_pool_hash = __h; }}\n"
    let cpp = cpp.Substring(0, freeAt) + hash + cpp.Substring(freeAt)
    let marker = "\n    return 0;\n    } catch"
    let at = cpp.IndexOf marker
    if at < 0 then None else
    let probe =
        $"    std::printf(\"RAW {p.Scalar} %%a\\n\", (double)({p.Scalar}));\n"
        + "    std::printf(\"RAW pool %llx\\n\", __rs_pool_hash);\n"
        + "    std::printf(\"RAW team %d\\n\", __rs_team_seen);"
    let head = "#include <cstdio>\n#include <cstring>\nstatic int __rs_team_seen = -1;\nstatic unsigned long long __rs_pool_hash = 0;\n"
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

let private rawOf (out: string) (key: string) =
    out.Replace("\r\n", "\n").Split('\n')
    |> Array.tryPick (fun l -> if l.StartsWith ("RAW " + key + " ") then Some (l.Substring(5 + key.Length)) else None)

/// `%a` text back to a double (C99 hex float), for the licensed tolerance.
let private hexFloat (s: string) : float option =
    try
        let s = s.Trim()
        let neg = s.StartsWith "-"
        let s = if neg then s.Substring 1 else s
        if not (s.StartsWith "0x") then None else
        let pAt = s.IndexOf 'p'
        let mant = s.Substring(2, pAt - 2)
        let exp = int (s.Substring(pAt + 1))
        let dot = mant.IndexOf '.'
        let digits = if dot < 0 then mant else mant.Replace(".", "")
        let frac = if dot < 0 then 0 else digits.Length - dot
        let v = float (Convert.ToUInt64(digits, 16)) * Math.Pow(2.0, float (exp - 4 * frac))
        Some (if neg then -v else v)
    with _ -> None

let runRecStepTests () : BlockResult =
    let blockName = "Rec Step"
    printHeader "Threaded let-rec step (E-G5): threads-on vs threads-off, raw bits"
    let caps = Build.capabilities.Value
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile the rec-step programs)."
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
    let work = Path.Combine(Path.GetTempPath(), "blade-rec-step-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(work) |> ignore
    CodeGen.deployRuntimeHeaders work
    let mutable passed = 0
    let mutable failed = 0
    let mutable names = []
    let fail (n: string) (detail: string) =
        failed <- failed + 1
        names <- names @ [ n ]
        resultLine Fail n detail
    let pass (n: string) (detail: string) =
        passed <- passed + 1
        resultLine Pass n detail
    use _exeCache = pinEnv "BLADE_EXE_CACHE" "0"
    use _blas = pinEnv "BLADE_BLAS" "0"
    use _lanes = pinEnv "BLADE_FOLD_LANES" null
    use _tune = pinEnv "BLADE_TILE_TUNE" null
    use _llvm = pinEnv "BLADE_LLVM" null
    use _gate = pinEnv "BLADE_REC_STEP_OMP" null
    let emitCpp (p: Program) (stem: string) : Result<string, string> =
        try
            match lower p.Source with
            | Error e -> Error ("lower: " + e)
            | Ok ir ->
                let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir stem
                CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                Ok cpp
        with ex -> Error ("codegen raised: " + ex.Message)
    let buildRun (stem: string) (cpp: string) (threads: string) (limit: string) : Result<string, string> =
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
            | Ok (code, _, err) when code <> 0 -> Error ($"exit {code}: {err.Trim()}")
            | Ok (_, out, _) -> Ok out
    let mutable stemN = 0
    for p in programs do
        for rg in regimes do
            let limTxt = if isNull rg.Limit then "" else $" limit={rg.Limit}"
            let name = $"{p.Name} {rg.RName} contract={rg.Contract} T={rg.Threads}{limTxt}"
            use _c = pinEnv "BLADE_FP_CONTRACT" rg.Contract
            use _r = pinEnv "BLADE_FP_REPRO" (if rg.Repro then "1" else null)
            let emitWith (threadsOff: bool) =
                use _o = pinEnv "BLADE_OMP_THREADS" (if threadsOff then "1" else null)
                stemN <- stemN + 1
                let stem = $"rs_{p.Name}_{stemN}"
                emitCpp p stem |> Result.map (fun cpp -> (stem, cpp))
            match emitWith false, emitWith true with
            | Error e, _ | _, Error e -> fail name e
            | Ok (sNew, cNew), Ok (sCtl, cCtl) ->
            let needles = if rg.Repro then p.NeedleRep else p.NeedleLic
            let missing = needles |> List.filter (fun n -> not (cNew.Contains n))
            let missingTxt = String.concat "; " missing
            let census = "[omp] requested but emitted serial: BLADE_OMP_THREADS=1"
            if not missing.IsEmpty then fail name ($"the threaded emission lacks: [{missingTxt}]")
            elif not (cCtl.Contains census) then fail name "the threads-off emission has no census line for the fused request"
            elif cCtl.Contains region then fail name "the threads-off emission opens a team"
            else
            match injectProbe p cNew, injectProbe p cCtl with
            | None, _ | _, None -> fail name "could not place the raw-bit probe"
            | Some pNew, Some pCtl ->
            match buildRun sNew pNew rg.Threads rg.Limit, buildRun sCtl pCtl rg.Threads rg.Limit with
            | Error e, _ -> fail name ("NEW: " + e)
            | _, Error e -> fail name ("CTRL: " + e)
            | Ok oNew, Ok oCtl ->
                let sNewV, sCtlV = rawOf oNew p.Scalar, rawOf oCtl p.Scalar
                let hNew, hCtl = rawOf oNew "pool", rawOf oCtl "pool"
                let team = rawOf oNew "team" |> Option.map int
                match sNewV, sCtlV, hNew, hCtl, team with
                | Some sn, Some sc, Some hn, Some hc, Some t ->
                    if hn <> hc then fail name ($"trajectory bits differ: {hn} vs {hc}")
                    elif t <> rg.Team then fail name ($"the region observed a team of {t}, expected {rg.Team}")
                    elif rg.Bitwise && sn <> sc then fail name ($"{p.Scalar}: threads {sn} vs threads-off {sc} (bitwise expected)")
                    elif rg.Bitwise then pass name ($"{p.Scalar} bitwise {sn}; trajectory {hn}; team {t}")
                    else
                        match hexFloat sn, hexFloat sc with
                        | Some a, Some b when abs (a - b) <= 1e-12 * max (abs a) (abs b) ->
                            pass name ($"{p.Scalar} {sn} vs {sc} within 1e-12; trajectory {hn}; team {t}")
                        | Some a, Some b -> fail name ($"{p.Scalar}: {a:R} vs {b:R} beyond 1e-12 relative")
                        | _ -> fail name ($"unparsable %%a: {sn} / {sc}")
                | _ -> fail name "the probe's RAW lines are missing"
    (try Directory.Delete(work, true) with _ -> ())
    printfn "Rec step: %d passed, %d failed" passed failed
    { Block = blockName; Passed = passed; Failed = failed; Skipped = 0; FailedNames = names }
