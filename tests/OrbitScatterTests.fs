// The ORDERED ORBIT SCATTER (src/OrbitFold.fs planStrict; C++
// blade_orbit_scatter.hpp, LLVM EmitLlvm.emitOrbitScatter) against the logical
// walk it replaces, EXECUTED, in both lanes, under the identity regime
// BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off.
//
// One generated program holds, for every case, a binding `a<k>` the scatter
// takes and a WALK-FORCED twin `b<k>` computing the same nest whose body also
// reads the pool at the MIRROR coordinate order (`g(i, j) * g(j, i)` is
// bitwise `x * x`; the other bodies wrap the read in an `if` on the mirror
// cell whose taken branch is the same expression) -- both forms decline it
// ("reads the compact array at more than one coordinate order"), so it is the
// serial walk in every build. Each case prints
//     e<k> = ((a == b) || (a != a && b != b)) && ((a != 0.0) || (1.0 / a == 1.0 / b))
// which is FINITE-VALUE IDENTITY (float `==`), SIGNED ZERO (the reciprocal)
// and NaN-EQUIVALENCE (decimal text and `==` carry no NaN payload) -- not raw
// bits. Cases: rank 2 at p in {1, 2, 5, 7, 13, 23, 37}, rank 3 at p in {1, 2,
// 5, 7, 11}, bodies x*x / x / abs(x) - 0.25 / x*x*x, per-level seeds cycling
// +0 / -0 / 0.75 and kernels cycling builtin `(+)` (the C++ lane's vector row
// chains), a `where repro` `a + b` (inline), the non-commutative
// `b - a * 0.5` (any order change shows) and an `if`-max; plus a NaN pool
// (must print nan) and an all -0.0 fold with -0.0 seeds (1.0 / a = -inf).
//
// Builds: C++ at the default row group and at scatter_g = 1 / 3 / 8, C++ with
// BLADE_ORBIT_SCATTER=0 (every `a` is the walk too), LLVM at the default and
// at scatter_g = 3. Every build: every e<k> true, the special values right,
// the route census (scatter calls in the scatter builds, none with the knob
// off), and the printed text IDENTICAL to the C++ default build.
// Plus the ROW-OWNED PULL (round 3, D-P3): a header-level RAW-BIT leg
// (cpp/orbit_pull_tests.cpp: every H cell and the total, memcmp, at T = 1, 3,
// 8 observed and on a restricted team, with the executed form and the form
// census) and an emitted-form leg that compares RAW RESULT BITS
// (BLADE_PRINT_BITS: a controlled NaN payload and a signed zero included)
// across the ordinary pull build at T = 1, 3, 8 and on a restricted team
// (OMP_THREAD_LIMIT=1; the run record's `forms` names each site's executed
// form), the test-mode pull build (teams observed), the pull off, the serial
// scatter, the walk and the LLVM lane.
// Plus the ROW ORBIT ACTION (round 3, D-P2 / gap G73): a generated program of
// rank-2 row actions, each beside its walk-forced twin, compared by RAW BITS
// (BLADE_PRINT_BITS) within and across C++ / LLVM builds at several row
// groups and with the knob off, and against the interpreter's decimal text.
// Plus G74's SEEDLESS folds (a seedless `(+)` is the left fold of its cells:
// the checker seeds it with the exact identity -0.0): seedless cases in the
// main program, the pull and the action legs, and a raw-bit leg (all -0.0 ->
// -0.0 exactly, a NaN first cell's payload kept; walk and scatter, both
// lanes, scatter_g 1 / 3 / 4 / 8; the interpreter's decimal text), and the
// ROUTE leg: the strict seed only on a strict route -- `where repro` bodies
// with REPRO unset are strict in both lanes and the interpreter, the licensed
// default build keeps the base +0.0 seed, an empty deferred fold is its seed.
// `blade test orbit-scatter`; part of the default suite. Needs g++ (Skipped
// without it); the LLVM half needs clang (Skipped without it).
module Blade.Tests.OrbitScatterTests

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

let private seeds = [| "0.0"; "-0.0"; "0.75" |]
let private kernels = [| "(+)"; "radd"; "rhalf"; "rmax" |]

/// (scatter body over the read `r`, walk-forced twin over the read and its mirror)
let private bodies : ((string -> string) * (string -> string -> string)) list =
    [ (fun r -> $"{r} * {r}"), (fun r m -> $"{r} * {m}")
      (fun r -> r), (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r}")
      (fun r -> $"abs({r}) - 0.25"), (fun r m -> $"if {m} > 1.0e300 then 0.0 else abs({r}) - 0.25")
      (fun r -> $"{r} * {r} * {r}"), (fun r m -> $"{r} * {m} * {r}") ]

/// The nest `reduce(range<V> ... f ...)` over `rank` levels, outermost first,
/// with per-level kernels and seeds; an EMPTY seed spells the level SEEDLESS
/// (`reduce(.., (+))`: the left fold of its cells, gap G74).
let private nest (v: string) (vars: string list) (ks: string list) (ss: string list) (body: string) =
    let rec go (lvl: int) =
        let var = vars.[lvl]
        let inner = if lvl = vars.Length - 1 then body else go (lvl + 1)
        let seed = if ss.[lvl] = "" then "" else $", {ss.[lvl]}"
        $"reduce(method_for(range<{v}>) <@> lambda({var}) -> {inner}, {ks.[lvl]}{seed})"
    go 0

let private header =
    "type Obs = Idx<3>\n"
    + "function radd(a: Float64, b: Float64) where repro -> Float64 = a + b\n"
    + "function rhalf(a: Float64, b: Float64) where repro -> Float64 = b - a * 0.5\n"
    + "function rmax(a: Float64, b: Float64) where repro -> Float64 = if b > a then b else a\n"

/// The x generator: mixed magnitudes (1e3-scaled rows beside unit rows),
/// non-dyadic, signed; `nanRow` puts a NaN in one row.
let private xDecl (tag: string) (v: string) (nanRow: int option) =
    let nanTerm =
        match nanRow with
        | Some r -> $" + (if i == {r} then sqrt(Float64(s) - 5.0) else 0.0)"
        | None -> ""
    $"let x{tag} = method_for(range<{v}>, range<Obs>) <@> lambda(i, s) -> (Float64((i * 7 + s * 3) %% 11) * 0.37 + 0.1 / Float64(i + 1)) * (1.0 + 999.0 * Float64((i %% 4) / 3)) - 0.55{nanTerm} |> compute\n"

/// The generated program and its case names (`a<k>` / `e<k>`), plus the
/// special bindings.
let private program () : string * int =
    let sb = Text.StringBuilder(header)
    let mutable k = 0
    let add (rank: int) (v: string) (pool: string) (ks: string list) (ss: string list) (fBody: string -> string) (twin: string -> string -> string) =
        let vars = if rank = 2 then [ "i"; "j" ] else [ "i"; "j"; "k" ]
        let read = $"""{pool}({String.concat ", " vars})"""
        let mirror = $"""{pool}({String.concat ", " (List.rev vars)})"""
        sb.Append($"let a{k} = {nest v vars ks ss (fBody read)}\n") |> ignore
        sb.Append($"let b{k} = {nest v vars ks ss (twin read mirror)}\n") |> ignore
        sb.Append($"let e{k} = ((a{k} == b{k}) || (a{k} != a{k} && b{k} != b{k})) && ((a{k} != 0.0) || (1.0 / a{k} == 1.0 / b{k}))\n") |> ignore
        k <- k + 1
    let rank2 = [ 1; 2; 5; 7; 13; 23; 37 ]
    let rank3 = [ 1; 2; 5; 7; 11 ]
    for p in List.distinct (rank2 @ rank3) do
        sb.Append($"type V{p} = Idx<{p}>\n") |> ignore
        sb.Append(xDecl (string p) $"V{p}" None) |> ignore
    for p in rank2 do
        sb.Append($"let g{p} = method_for(x{p}, x{p}) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n") |> ignore
    for p in rank3 do
        sb.Append($"let t{p} = method_for(x{p}, x{p}, x{p}) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n") |> ignore
    for (rank, ps, poolOf) in [ (2, rank2, (fun p -> $"g{p}")); (3, rank3, (fun p -> $"t{p}")) ] do
        for p in ps do
            bodies |> List.iteri (fun bi (f, twin) ->
                let ks = [ for l in 0 .. rank - 1 -> kernels.[(bi + l + p) % kernels.Length] ]
                let ss = [ for l in 0 .. rank - 1 -> seeds.[(2 * bi + l + p) % seeds.Length] ]
                add rank $"V{p}" (poolOf p) ks ss f twin)
    // The NaN pool (rank 2 extent 7, rank 3 extent 5): the fold must say nan.
    sb.Append(xDecl "n7" "V7" (Some 3)) |> ignore
    sb.Append(xDecl "n5" "V5" (Some 2)) |> ignore
    sb.Append("let gn = method_for(xn7, xn7) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n") |> ignore
    sb.Append("let tn = method_for(xn5, xn5, xn5) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n") |> ignore
    let nanStart = k
    add 2 "V7" "gn" [ "radd"; "radd" ] [ "0.0"; "0.75" ] (fun r -> $"{r} * {r}") (fun r m -> $"{r} * {m}")
    add 3 "V5" "tn" [ "(+)"; "radd"; "rhalf" ] [ "0.0"; "-0.0"; "0.75" ] (fun r -> r) (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r}")
    // All -0.0 cells, all -0.0 seeds: the fold is -0.0 (1.0 / a = -inf).
    let zeroStart = k
    add 2 "V13" "g13" [ "radd"; "(+)" ] [ "-0.0"; "-0.0" ] (fun r -> $"-0.0 * abs({r})") (fun r m -> $"if {m} > 1.0e300 then 0.0 else -0.0 * abs({r})")
    add 3 "V7" "t7" [ "radd"; "radd"; "(+)" ] [ "-0.0"; "-0.0"; "-0.0" ] (fun r -> $"-0.0 * abs({r})") (fun r m -> $"if {m} > 1.0e300 then 0.0 else -0.0 * abs({r})")
    sb.Append($"let zn2 = a{nanStart}\nlet zn3 = a{nanStart + 1}\nlet zz2 = 1.0 / a{zeroStart}\nlet zz3 = 1.0 / a{zeroStart + 1}\n") |> ignore
    // SEEDLESS builtin `(+)` at every level (gap G74): the left fold of the
    // cells, every extent; then all -0.0 cells, where the fold is -0.0 (it was
    // +0.0 when the checker seeded a seedless deferred `(+)` with +0.0).
    for p in rank2 do
        add 2 $"V{p}" $"g{p}" [ "(+)"; "(+)" ] [ ""; "" ] (fun r -> $"{r} * {r}") (fun r m -> $"{r} * {m}")
    for p in rank3 do
        add 3 $"V{p}" $"t{p}" [ "(+)"; "(+)"; "(+)" ] [ ""; ""; "" ] (fun r -> $"abs({r}) - 0.25") (fun r m -> $"if {m} > 1.0e300 then 0.0 else abs({r}) - 0.25")
    let seedlessZero = k
    add 2 "V13" "g13" [ "(+)"; "(+)" ] [ ""; "" ] (fun r -> $"-0.0 * abs({r})") (fun r m -> $"if {m} > 1.0e300 then 0.0 else -0.0 * abs({r})")
    add 3 "V7" "t7" [ "(+)"; "(+)"; "(+)" ] [ ""; ""; "" ] (fun r -> $"-0.0 * abs({r})") (fun r m -> $"if {m} > 1.0e300 then 0.0 else -0.0 * abs({r})")
    sb.Append($"let zs2 = 1.0 / a{seedlessZero}\nlet zs3 = 1.0 / a{seedlessZero + 1}\n") |> ignore
    (sb.ToString(), k)

let runOrbitScatterTests () : BlockResult =
    let blockName = "Orbit Scatter"
    printHeader "Ordered Orbit Scatter (both lanes vs the walk: finite-value identity, signed zero, NaN-equivalence)"
    let caps = Build.capabilities.Value
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile the scatter programs)."
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
    let work = Path.Combine(Path.GetTempPath(), "blade-orbit-scatter-" + Guid.NewGuid().ToString("N"))
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
    let (src, cases) = program ()
    let printed =
        [ for k in 0 .. cases - 1 do
              yield $"a{k}"
              yield $"e{k}"
          yield! [ "zn2"; "zn3"; "zz2"; "zz3"; "zs2"; "zs3" ] ]
    use _exeCache = pinEnv "BLADE_EXE_CACHE" "0"
    use _blas = pinEnv "BLADE_BLAS" "0"
    use _repro = pinEnv "BLADE_FP_REPRO" "1"
    use _contract = pinEnv "BLADE_FP_CONTRACT" "off"
    use _threads = pinEnv "BLADE_OMP_THREADS" "1"
    use _print = pinEnv "BLADE_PRINT" (String.concat "," printed)
    use _orbit = pinEnv "BLADE_ORBIT_FOLD" null
    let valueText (out: string) =
        out.Replace("\r\n", "\n").Split('\n')
        |> Array.filter (fun l -> l.Contains " = " && not (l.Contains "completed in"))
        |> String.concat "\n"
    /// The checks one build's output must pass on its own.
    let checkOutput (out: string) : string option =
        let lines = valueText out
        let eqs = lines.Split('\n') |> Array.filter (fun l -> Text.RegularExpressions.Regex.IsMatch(l, @"^e\d+ = "))
        let bad = eqs |> Array.filter (fun l -> not (l.EndsWith "= true"))
        if eqs.Length <> cases then Some $"{eqs.Length} e<k> lines printed, wanted {cases}"
        elif bad.Length > 0 then Some $"""{bad.Length} case(s) differ from the walk: {String.concat " | " (bad |> Array.truncate 6)}"""
        elif not (Text.RegularExpressions.Regex.IsMatch(lines, @"(?m)^zn2 = -?nan$") && Text.RegularExpressions.Regex.IsMatch(lines, @"(?m)^zn3 = -?nan$")) then
            Some "the NaN pool's folds must print nan"
        elif not (lines.Contains "zz2 = -inf" && lines.Contains "zz3 = -inf") then
            Some "the all -0.0 folds must keep their sign (1.0 / a = -inf)"
        elif not (lines.Contains "zs2 = -inf" && lines.Contains "zs3 = -inf") then
            Some "the SEEDLESS all -0.0 folds must be -0.0, the left fold of the cells (1.0 / a = -inf; G74)"
        else None
    let buildSrc (source: string) (lane: string) (tune: string) (knob: string) (stem: string) : Result<string * string, string> =
        use _t = pinEnv "BLADE_TILE_TUNE" tune
        use _k = pinEnv "BLADE_ORBIT_SCATTER" knob
        match (try lower source with ex -> Error ("lower raised: " + ex.Message)) with
        | Error e -> Error ("lower: " + e)
        | Ok ir ->
        if lane = "cpp" then
            let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir stem
            CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
            let file = Path.Combine(work, stem + ".cpp")
            File.WriteAllText(file, cpp)
            match Build.compileCpp file work with
            | Error e -> Error ("compile: " + (if e.Length > 400 then e.Substring(0, 400) else e))
            | Ok exe -> Ok (exe, cpp)
        else
            match (try EmitLlvm.tryEmitProgramNamed stem ir with ex -> Error ("raised: " + ex.Message)) with
            | Error why -> Error ($"the llvm lane refused the program: {why}")
            | Ok ll ->
            let llFile = Path.Combine(work, stem + ".ll")
            File.WriteAllText(llFile, ll)
            match Build.compileLlvmProgram llFile work with
            | Error e -> Error ("llvm compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
            | Ok exe -> Ok (exe, ll)
    let build = buildSrc src
    let run (exe: string) =
        use _t = pinEnv "OMP_NUM_THREADS" "1"
        match runProc exe 300000 with
        | Error e -> Error e
        | Ok (code, _, err) when code <> 0 -> Error ($"exit {code}: {err.Trim()}")
        | Ok (_, out, _) -> Ok out
    let routeCount (lane: string) (text: string) =
        let needle = if lane = "cpp" then "blade_scatter::scatter" else "; ordered orbit scatter (rank"
        Text.RegularExpressions.Regex.Matches(text, Text.RegularExpressions.Regex.Escape needle).Count
    // (lane, BLADE_TILE_TUNE, BLADE_ORBIT_SCATTER, label, expected scatter routes)
    let builds =
        [ ("cpp", null, null, "C++ default (scatter_g=4)", Some cases)
          ("cpp", "scatter_g=1", null, "C++ scatter_g=1", Some cases)
          ("cpp", "scatter_g=3,scatter_b=8", null, "C++ scatter_g=3, scatter_b=8", Some cases)
          ("cpp", "scatter_g=8", null, "C++ scatter_g=8", Some cases)
          ("cpp", null, "0", "C++ BLADE_ORBIT_SCATTER=0 (the walk)", Some 0)
          ("llvm", null, null, "LLVM default (scatter_g=4)", Some cases)
          ("llvm", "scatter_g=3,scatter_b=8", null, "LLVM scatter_g=3, scatter_b=8", Some cases)
          ("llvm", "scatter_g=8", null, "LLVM scatter_g=8", Some cases) ]
    let mutable reference : string option = None
    let mutable n = 0
    for (lane, tune, knob, label, routes) in builds do
        n <- n + 1
        let name = $"{label}: {cases} cases == the walk (REPRO, contract=off)"
        let clangMissing = lane = "llvm" && (Build.resolveClang ()).IsNone
        if clangMissing then skip name "no clang (BLADE_LLVM_CLANG)"
        else
        match build lane tune knob $"oscat_{n}" with
        | Error e when e.Contains "Skipped:" -> skip name e
        | Error e -> fail name e
        | Ok (exe, text) ->
        let rc = routeCount lane text
        match routes with
        | Some _ when lane = "llvm" && (isNull tune || tune = "scatter_g=8") && not (text.Contains "fadd <4 x double>") ->
            fail name "the LLVM row chains of a builtin `+` level did not take the <4 x double> form (G % 4 = 0)"
        | Some want when rc <> want ->
            fail name $"route census: {rc} scatter site(s) in the emitted text, wanted {want} (the comparison would not test the form)"
        | _ ->
        match run exe with
        | Error e -> fail name ("run: " + e)
        | Ok out ->
        match checkOutput out with
        | Some why -> fail name why
        | None ->
        let txt = valueText out
        match reference with
        | None ->
            reference <- Some txt
            pass name $"every e<k> true; NaN cases print nan; -0.0 cases keep their sign; {rc} scatter site(s)"
        | Some r when r = txt ->
            pass name $"every e<k> true; printed text identical to the C++ default build; {rc} scatter site(s)"
        | Some r ->
            let diff =
                Seq.zip (r.Split('\n')) (txt.Split('\n')) |> Seq.tryFind (fun (x, y) -> x <> y)
                |> Option.map (fun (x, y) -> $"{x} vs {y}") |> Option.defaultValue "line counts differ"
            fail name ($"printed text differs from the C++ default build: {diff}")
    // ---- the review regressions (Codex diff review of W2-A) ------------------
    let llvmOn = (Build.resolveClang ()).IsSome
    // (1) A where-repro fold kernel keeps its CALLABLE boundary under the
    // contracting default: x = 1 + 2^-27 squares to 1 + 2^-26 + 2^-54, which
    // the call rounds to 1 + 2^-26; the inner seed -(1 + 2^-26) cancels it to
    // 0 exactly. A kernel inlined into the contracting caller could fuse
    // `seed + x * x` and keep the 2^-54. Scatter vs the walk-forced twin, both
    // lanes, contraction FAST (the block's identity regime is contract=off).
    let reproSrc =
        "type One = Idx<1>\ntype Two = Idx<2>\n"
        + "function radd(a: Float64, b: Float64) where repro -> Float64 = a + b\n"
        + "let v1 = method_for(range<One>) <@> lambda(i) -> 1.0 + 7.450580596923828125e-9 |> compute\n"
        + "let g1 = method_for(v1, v1) <@> lambda(a: Float64, b: Float64) where comm(a, b) -> if a > b then a else b |> compute\n"
        + "let v2 = method_for(range<Two>) <@> lambda(i) -> 1.0 + 7.450580596923828125e-9 |> compute\n"
        + "let g2 = method_for(v2, v2) <@> lambda(a: Float64, b: Float64) where comm(a, b) -> if a > b then a else b |> compute\n"
        + "let a1 = reduce(method_for(range<One>) <@> lambda(i) -> reduce(method_for(range<One>) <@> lambda(j) -> g1(i, j) * g1(i, j), radd, -1.0 - 1.4901161193847656e-8), radd, 0.0)\n"
        + "let b1 = reduce(method_for(range<One>) <@> lambda(i) -> reduce(method_for(range<One>) <@> lambda(j) -> g1(i, j) * g1(j, i), radd, -1.0 - 1.4901161193847656e-8), radd, 0.0)\n"
        + "let a2 = reduce(method_for(range<Two>) <@> lambda(i) -> reduce(method_for(range<Two>) <@> lambda(j) -> g2(i, j) * g2(i, j), radd, -1.0 - 1.4901161193847656e-8), radd, 0.0)\n"
        + "let b2 = reduce(method_for(range<Two>) <@> lambda(i) -> reduce(method_for(range<Two>) <@> lambda(j) -> g2(i, j) * g2(j, i), radd, -1.0 - 1.4901161193847656e-8), radd, 0.0)\n"
        + "let r1 = a1 * 18014398509481984.0\nlet e1 = a1 == b1\nlet e2 = a2 == b2\n"
    for lane in [ "cpp"; "llvm" ] do
      for repro in [ "1"; null ] do
        let name = $"""{lane}: a where-repro kernel keeps its call boundary under contract=fast ({(if isNull repro then "default build" else "REPRO")})"""
        if lane = "llvm" && not llvmOn then skip name "no clang (BLADE_LLVM_CLANG)"
        else
        use _c = pinEnv "BLADE_FP_CONTRACT" null
        use _r = pinEnv "BLADE_FP_REPRO" repro
        use _p = pinEnv "BLADE_PRINT" "r1,e1,e2"
        n <- n + 1
        match buildSrc reproSrc lane null null $"oscat_fast_{n}" with
        | Error e when e.Contains "Skipped:" -> skip name e
        | Error e -> fail name e
        | Ok (exe, text) ->
        let sites = routeCount lane text
        if sites <> 2 then fail name $"route census: {sites} scatter site(s), wanted 2"
        else
        match run exe with
        | Error e -> fail name ("run: " + e)
        | Ok out ->
            let v = valueText out
            if v.Contains "r1 = 0.0" && v.Contains "e1 = true" && v.Contains "e2 = true" then
                pass name "the repro addition sees the ROUNDED square (r1 = 0.0); scatter == walk at one and two rows"
            else fail name ($"""wanted r1 = 0.0, e1 = true, e2 = true: {v.Replace("\n", " | ")}""")
    // (2) An UNFORCED map over a stored SymIdx pool is NOT a stored pool: the
    // LLVM lane keeps the walk (its producer would run inside the scatter, in
    // the grouped order), with the walk's forcing / printing rules and its
    // FIRST error. The C++ lane declines it too (a deferred computation);
    // these programs run in the LLVM lane only (the C++ lane rejects this
    // walk shape today, pre-existing BL9002 on 6b3e5293).
    let unforcedSrc =
        "type Var = Idx<5>\ntype Obs = Idx<3>\n"
        + "function radd(a: Float64, b: Float64) where repro -> Float64 = a + b\n"
        + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 7 + s * 3) % 11) * 0.37 + 0.1 / Float64(i + 1) |> compute\n"
        + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
        + "let h = method_for(g) <@> lambda(v: Float64) -> v * 0.25 + 1.0\n"
        + "let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> h(j, i), radd, 0.5), radd, -0.0)\n"
        + "let t = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> if h(i, j) > 1.0e300 then 0.0 else h(j, i), radd, 0.5), radd, -0.0)\n"
        + "let e = s == t\n"
    // (1, 1) holds 201 (a modulo by zero), (0, 4) holds 400 (a division by
    // zero): the walk reaches (0, 4) first (row 0); a grouped scatter's
    // diagonal block would reach (1, 1) first.
    let firstErrSrc =
        "type Var = Idx<5>\n"
        + "function iadd(a: Int64, b: Int64) where repro -> Int64 = a + b\n"
        + "let v = method_for(range<Var>) <@> lambda(i) -> Int64(i) |> compute\n"
        + "let gi = method_for(v, v) <@> lambda(a: Int64, b: Int64) where comm(a, b) -> (a + b) * 100 + a * b |> compute\n"
        + "let hp = method_for(gi) <@> lambda(x: Int64) -> if x == 400 then 1 / (x - x) else if x == 201 then 1 % (x - x) else x\n"
        + "let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> hp(i, j), iadd, 0), iadd, 0)\n"
    if not llvmOn then skip "llvm: an unforced source keeps the walk" "no clang (BLADE_LLVM_CLANG)"
    else
        use _c = pinEnv "BLADE_FP_CONTRACT" "off"
        use _pAll = pinEnv "BLADE_PRINT" null
        let unforcedCase (knob: string) : string option =
            n <- n + 1
            let name = $"""llvm: an unforced map over a stored pool keeps the walk (BLADE_ORBIT_SCATTER={(if isNull knob then "on" else "0")})"""
            match buildSrc unforcedSrc "llvm" null knob $"oscat_unforced_{n}" with
            | Error e -> fail name e; None
            | Ok (exe, ll) ->
                let sites = routeCount "llvm" ll
                if sites <> 0 then
                    fail name $"{sites} scatter site(s) over an unforced producer"
                    None
                else
                match run exe with
                | Error e -> fail name ("run: " + e); None
                | Ok out ->
                    let v = valueText out
                    if v.Contains "h = [[" && v.Contains "s = " && v.Contains "e = true" then
                        pass name "no scatter site; the deferred `h` still prints; s == the walk-forced twin"
                        Some v
                    else
                        fail name ($"""output list: wanted h, s and e = true: {v.Replace("\n", " | ")}""")
                        None
        let outs = [ unforcedCase null; unforcedCase "0" ]
        match outs with
        | [ Some a; Some b ] when a <> b -> fail "llvm: unforced source, knob on == knob off" "printed text differs"
        | [ Some _; Some _ ] -> pass "llvm: unforced source, knob on == knob off" "printed text identical"
        | _ -> ()
        for knob in [ null; "0" ] do
            n <- n + 1
            let name = $"""llvm: the first error is the walk's over an unforced producer (BLADE_ORBIT_SCATTER={(if isNull knob then "on" else "0")})"""
            use _p = pinEnv "BLADE_PRINT" "s"
            match buildSrc firstErrSrc "llvm" null knob $"oscat_ferr_{n}" with
            | Error e -> fail name e
            | Ok (exe, ll) ->
            if routeCount "llvm" ll <> 0 then fail name "a scatter site over an unforced producer"
            else
            use _t = pinEnv "OMP_NUM_THREADS" "1"
            match runProc exe 120000 with
            | Error e -> fail name e
            | Ok (code, out, err) ->
                let all = out + err
                if code <> 0 && all.Contains "integer division by zero" && not (all.Contains "modulo") then
                    pass name "BL8013 integer division by zero (cell (0, 4), reached first in row order)"
                else fail name ($"""wanted the division-by-zero abort: exit {code}: {all.Trim().Replace("\n", " | ")}""")
    // ---- THE ROW-OWNED PULL SCATTER (round 3, D-P3) -------------------------
    // (1) HEADER LEVEL, RAW BITS: cpp/orbit_pull_tests.cpp over the shipped
    // blade_orbit_scatter.hpp compares, by memcmp, EVERY leaf chain of the
    // packed H and the total of the serial slice form, of the pull form at
    // T = 1, 3, 8 (team observed: exactly min(T, p) with dynamic teams off)
    // and of the logical walk (see its header for the case list; one NaN
    // payload -- which payload survives two NaNs is outside the contract).
    let pullTestSrc = Path.Combine(AppContext.BaseDirectory, "cpp", "orbit_pull_tests.cpp")
    let pullHeaderLeg () =
        let name = "pull form, header level: every H cell and total == the serial slice form == the walk (raw bits, T = 1, 3, 8, a restricted team), the executed form and the form census"
        if not (File.Exists pullTestSrc) then fail name $"orbit_pull_tests.cpp not found at {pullTestSrc}"
        else
        let exe = Path.Combine(work, "orbit_pull_tests.exe")
        let psi = ProcessStartInfo("g++", $"-std=c++17 -O3 -march=native -fno-math-errno -fwrapv -fopenmp -ffp-contract=off -o \"{exe}\" \"{pullTestSrc}\"")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use cproc = Process.Start(psi)
        let co = Blade.Runtime.readToEndOffPool cproc.StandardOutput
        let ce = Blade.Runtime.readToEndOffPool cproc.StandardError
        if not (cproc.WaitForExit(180000)) then
            (try cproc.Kill(true) with _ -> ())
            fail name "compile timed out (180s)"
        elif cproc.ExitCode <> 0 then fail name ("compile: " + (co.Result + ce.Result).Trim())
        else
        let rpsi = ProcessStartInfo(exe, "")
        rpsi.RedirectStandardOutput <- true
        rpsi.RedirectStandardError <- true
        rpsi.UseShellExecute <- false
        rpsi.CreateNoWindow <- true
        rpsi.Environment.["OMP_DYNAMIC"] <- "false"
        rpsi.Environment.Remove("OMP_THREAD_LIMIT") |> ignore
        rpsi.Environment.Remove("OMP_NUM_THREADS") |> ignore
        use rproc = Process.Start(rpsi)
        let ro = Blade.Runtime.readToEndOffPool rproc.StandardOutput
        let re = Blade.Runtime.readToEndOffPool rproc.StandardError
        if not (rproc.WaitForExit(300000)) then
            (try rproc.Kill(true) with _ -> ())
            fail name "run timed out (300s)"
        else
        let out = ro.Result.Replace("\r\n", "\n")
        let m = Text.RegularExpressions.Regex.Match(out, @"ORBIT PULL TESTS:\s*(\d+)/(\d+)\s*passed")
        let teams = out.Split('\n') |> Array.tryFind (fun l -> l.StartsWith "teams observed") |> Option.defaultValue ""
        let fails = out.Split('\n') |> Array.filter (fun l -> l.StartsWith "[FAIL]") |> Array.truncate 5 |> String.concat " | "
        let forms = out.Split('\n') |> Array.tryFind (fun l -> l.StartsWith "executed forms") |> Option.defaultValue ""
        let census = out.Split('\n') |> Array.tryFind (fun l -> l.StartsWith "form census") |> Option.defaultValue ""
        if m.Success && rproc.ExitCode = 0 && m.Groups.[1].Value = m.Groups.[2].Value
           && teams.Contains "T=1 -> 1, T=3 -> 3, T=8 -> 8" && forms <> "" && census <> "" then
            pass name $"{m.Groups.[1].Value}/{m.Groups.[2].Value} identities, forms and census rows bitwise / exact; {teams.Trim()}; {forms.Trim()}; {census.Trim()}"
        elif m.Success then fail name ($"{m.Groups.[1].Value}/{m.Groups.[2].Value} passed; {teams.Trim()}; {fails} {re.Result.Trim()}")
        else fail name ($"no summary line (exit {rproc.ExitCode}): {re.Result.Trim()}")
    pullHeaderLeg ()
    // (2) THE EMITTED FORM, both lanes, BY RAW BITS: a generated rank-3
    // program whose OUTER level carries the request (the fold kernel's `where
    // omp`, or the map's `omp(i: 1)`), each case `a<k>` beside its walk-forced
    // twin `b<k>`, under REPRO + contract=off with `pull_min=0` (so p = 13 / 23
    // qualify). Every run prints with BLADE_PRINT_BITS=1, so each Float64
    // binding carries its raw IEEE-754 pattern (` #<16 hex>`), and the
    // patterns of EVERY a<k> (a controlled NaN -- one payload, from the pool's
    // own invalid sqrt -- and a signed zero among them) must be identical
    // across: the ordinary pull build at T = 1, 3, 8 and on a RESTRICTED team
    // (OMP_NUM_THREADS=8 OMP_THREAD_LIMIT=1), the omp-test-mode pull build at
    // T = 1, 3, 8 (every pull site's team observed: min(T, p)), the C++ lane
    // with the pull off, the serial scatter (threads off), the walk (scatter
    // off) and the LLVM lane's serial scatter. The ordinary pull build's run
    // record (`forms`) must name the EXECUTED form of every site: `row-pull`
    // with actual = min(T, p) at T = 3 / 8, `serial-leaf3` with actual 1 at
    // T = 1 and on the restricted team. e<k> (each binary against its own walk
    // twin) is kept as a second check. Routes asserted per build.
    let pullProgram () : string * int * int list * int * int =
        let sb = Text.StringBuilder(header)
        let mutable k = 0
        let exts = Collections.Generic.List<int>()
        let add (p: int) (pool: string) (ks: string list) (ss: string list) (outer: string) (mapReq: bool) (fBody: string -> string) (twin: string -> string -> string) =
            let vars = [ "i"; "j"; "k" ]
            let read = $"""{pool}({String.concat ", " vars})"""
            let mirror = $"""{pool}({String.concat ", " (List.rev vars)})"""
            let mk (body: string) =
                let inner = nest $"V{p}" [ "j"; "k" ] (List.tail ks) (List.tail ss) body
                let lam = if mapReq then "lambda(i) where omp(i: 1)" else "lambda(i)"
                $"reduce(method_for(range<V{p}>) <@> {lam} -> {inner}, {outer}, {ss.Head})"
            sb.Append($"let a{k} = {mk (fBody read)}\n") |> ignore
            sb.Append($"let b{k} = {mk (twin read mirror)}\n") |> ignore
            sb.Append($"let e{k} = ((a{k} == b{k}) || (a{k} != a{k} && b{k} != b{k})) && ((a{k} != 0.0) || (1.0 / a{k} == 1.0 / b{k}))\n") |> ignore
            exts.Add p
            k <- k + 1
        for p in [ 13; 23; 171 ] do
            sb.Append($"type V{p} = Idx<{p}>\n") |> ignore
            sb.Append(xDecl (string p) $"V{p}" None) |> ignore
            sb.Append($"let t{p} = method_for(x{p}, x{p}, x{p}) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n") |> ignore
        let foldReq = "lambda(u, v) where omp -> u + v"
        for p in [ 13; 23 ] do
            bodies |> List.iteri (fun bi (f, twin) ->
                // leaf / middle kernels cycle (+) / radd / rhalf / rmax; the
                // outer is licensed (the request's gate), its seed nonzero or -0
                let ks = [ "(+)"; kernels.[(bi + p) % kernels.Length]; kernels.[(bi + p + 1) % kernels.Length] ]
                let ss = [ seeds.[(bi + 1) % seeds.Length]; seeds.[(2 * bi + p) % seeds.Length]; seeds.[(bi + 2) % seeds.Length] ]
                add p $"t{p}" (if bi % 2 = 0 then foldReq :: List.tail ks else ks) ss (if bi % 2 = 0 then foldReq else "(+)") (bi % 2 = 1) f twin)
        // comoment3's extent (smoke) with the bench body
        add 171 "t171" [ foldReq; "(+)"; "(+)" ] [ "0.0"; "0.0"; "0.0" ] foldReq false (fun r -> $"{r} * {r}") (fun r m -> $"{r} * {m}")
        // a NaN pool (ONE payload: the pool's own invalid sqrt) and an all
        // -0.0 fold (-0.0 seeds) through the pull
        sb.Append("type V11 = Idx<11>\n") |> ignore
        sb.Append(xDecl "n11" "V11" (Some 4)) |> ignore
        sb.Append("let tn11 = method_for(xn11, xn11, xn11) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n") |> ignore
        let nanAt = k
        add 11 "tn11" [ foldReq; "radd"; "radd" ] [ "0.75"; "-0.0"; "0.5" ] foldReq false (fun r -> r) (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r}")
        // ...and through the order-sensitive max (a NaN operand is dropped or
        // kept by its position: no `nan` expected, the twin decides)
        add 11 "tn11" [ foldReq; "rmax"; "rmax" ] [ "0.75"; "-0.0"; "0.5" ] foldReq false (fun r -> r) (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r}")
        let zeroAt = k
        add 13 "t13" [ foldReq; "radd"; "(+)" ] [ "-0.0"; "-0.0"; "-0.0" ] foldReq false (fun r -> $"-0.0 * abs({r})") (fun r m -> $"if {m} > 1.0e300 then 0.0 else -0.0 * abs({r})")
        sb.Append($"let pn = a{nanAt}\nlet pz = 1.0 / a{zeroAt}\n") |> ignore
        // G74: SEEDLESS middle and leaf `(+)` through the pull -- all -0.0
        // cells (the next-to-last case: -0.0 exactly), and a pool whose x row
        // 0 is NaN, so every leaf chain's FIRST cell T(a, b, 0) with a or b = 0
        // is a NaN and the total carries that cell's payload (the last case;
        // `ph` prints the first cell T(0, 0, 0))
        add 13 "t13" [ foldReq; "(+)"; "(+)" ] [ "-0.0"; ""; "" ] foldReq false (fun r -> $"-0.0 * abs({r})") (fun r m -> $"if {m} > 1.0e300 then 0.0 else -0.0 * abs({r})")
        sb.Append(xDecl "h11" "V11" (Some 0)) |> ignore
        sb.Append("let th11 = method_for(xh11, xh11, xh11) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n") |> ignore
        add 11 "th11" [ foldReq; "(+)"; "(+)" ] [ "0.75"; ""; "" ] foldReq false (fun r -> r) (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r}")
        sb.Append("let ph = th11(0, 0, 0)\n") |> ignore
        (sb.ToString(), k, List.ofSeq exts, nanAt, zeroAt)
    let (psrc, pcases, pexts, nanAt, zeroAt) = pullProgram ()
    let pprinted = [ for k in 0 .. pcases - 1 do yield $"a{k}"; yield $"e{k}" ] @ [ "pn"; "pz"; "ph" ]
    let seedZeroAt = pcases - 2
    let seedNanAt = pcases - 1
    let pullCheck (out: string) : string option =
        let lines = valueText out
        let eqs = lines.Split('\n') |> Array.filter (fun l -> Text.RegularExpressions.Regex.IsMatch(l, @"^e\d+ = "))
        let bad = eqs |> Array.filter (fun l -> not (l.EndsWith "= true"))
        if eqs.Length <> pcases then Some $"{eqs.Length} e<k> lines printed, wanted {pcases}"
        elif bad.Length > 0 then Some $"""{bad.Length} case(s) differ from the walk: {String.concat " | " (bad |> Array.truncate 6)}"""
        elif not (Text.RegularExpressions.Regex.IsMatch(lines, @"(?m)^pn = -?nan( #[0-9a-f]{16})?$")) then Some "the NaN pool's fold must print nan"
        elif not (lines.Contains "pz = -inf") then Some "the all -0.0 fold must keep its sign (1.0 / a = -inf)"
        else None
    // name -> the raw 64-bit pattern of every printed Float64 binding
    let bitsOf (out: string) : Map<string, string> =
        Text.RegularExpressions.Regex.Matches(out.Replace("\r\n", "\n"), @"(?m)^([A-Za-z_][A-Za-z_0-9]*) = [^\n]* #([0-9a-f]{16})$")
        |> Seq.map (fun m -> (m.Groups.[1].Value, m.Groups.[2].Value))
        |> Map.ofSeq
    // site -> (form, requested, actual) from the run record's `forms` census
    let formsOf (record: string) : Map<string, string * int * int> =
        Text.RegularExpressions.Regex.Matches(record, "\\{\"site\":\"([^\"]+)\",\"form\":\"([^\"]+)\",\"requested\":(\\d+),\"actual\":(\\d+),\"calls\":(\\d+)\\}")
        |> Seq.map (fun m -> (m.Groups.[1].Value, (m.Groups.[2].Value, int m.Groups.[3].Value, int m.Groups.[4].Value)))
        |> Map.ofSeq
    let pullCount (text: string) = Text.RegularExpressions.Regex.Matches(text, Text.RegularExpressions.Regex.Escape "blade_scatter::pull3<").Count
    let scatter3Count (text: string) = Text.RegularExpressions.Regex.Matches(text, Text.RegularExpressions.Regex.Escape "blade_scatter::scatter3<").Count
    let llvmScatterCount (text: string) = Text.RegularExpressions.Regex.Matches(text, Text.RegularExpressions.Regex.Escape "; ordered orbit scatter (rank 3").Count
    let buildPull (lane: string) (pins: (string * string) list) (testMode: bool) (stem: string) : Result<string * string, string> =
        let guards = pins |> List.map (fun (k, v) -> pinEnv k v)
        try
            if testMode then CodeGen.setOmpTestMode true
            try
                match (try lower psrc with ex -> Error ("lower raised: " + ex.Message)) with
                | Error e -> Error ("lower: " + e)
                | Ok ir ->
                if lane = "cpp" then
                    let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir stem
                    CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                    let file = Path.Combine(work, stem + ".cpp")
                    File.WriteAllText(file, cpp)
                    match Build.compileCpp file work with
                    | Error e -> Error ("compile: " + (if e.Length > 400 then e.Substring(0, 400) else e))
                    | Ok exe -> Ok (exe, cpp)
                else
                    match (try EmitLlvm.tryEmitProgramNamed stem ir with ex -> Error ("raised: " + ex.Message)) with
                    | Error why -> Error ($"the llvm lane refused the program: {why}")
                    | Ok ll ->
                    let llFile = Path.Combine(work, stem + ".ll")
                    File.WriteAllText(llFile, ll)
                    match Build.compileLlvmProgram llFile work with
                    | Error e -> Error ("llvm compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
                    | Ok exe -> Ok (exe, ll)
            finally
                if testMode then CodeGen.setOmpTestMode false
        finally
            for g in List.rev guards do g.Dispose()
    // Run at OMP_NUM_THREADS=threads (dynamic teams off; `limit` = an
    // OMP_THREAD_LIMIT, else none inherited), printing raw bits; returns the
    // output and the run record's text.
    let runAt (exe: string) (threads: string) (limit: string option) : Result<string * string, string> =
        let record = Path.Combine(work, Path.GetFileNameWithoutExtension(exe) + $"_t{threads}" + (match limit with Some l -> $"_l{l}" | None -> "") + ".json")
        (try File.Delete record with _ -> ())
        let psi = ProcessStartInfo(exe, "")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.Environment.["OMP_NUM_THREADS"] <- threads
        psi.Environment.["OMP_DYNAMIC"] <- "false"
        (match limit with
         | Some l -> psi.Environment.["OMP_THREAD_LIMIT"] <- l
         | None -> psi.Environment.Remove("OMP_THREAD_LIMIT") |> ignore)
        psi.Environment.["BLADE_PRINT_BITS"] <- "1"
        psi.Environment.["BLADE_RUN_RECORD"] <- record
        use proc = Process.Start(psi)
        let o = Blade.Runtime.readToEndOffPool proc.StandardOutput
        let e = Blade.Runtime.readToEndOffPool proc.StandardError
        if not (proc.WaitForExit(300000)) then
            (try proc.Kill(true) with _ -> ())
            Error "timed out"
        elif proc.ExitCode <> 0 then Error ($"exit {proc.ExitCode}: {e.Result.Trim()}")
        else Ok (o.Result, (try File.ReadAllText record with _ -> ""))
    // every pull site's observed team and executed form (omp test mode)
    let teamsOf (out: string) =
        Text.RegularExpressions.Regex.Matches(out, @"\[omp-coverage\] region=a(\d+) teamsz=(\d+) distinct=(\d+) maxth=(\d+) form=(\S+) requested=(\d+) actual=(\d+)")
        |> Seq.map (fun m -> (int m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value, m.Groups.[5].Value, int m.Groups.[7].Value))
        |> List.ofSeq
    use _pp = pinEnv "BLADE_PRINT" (String.concat "," pprinted)
    let commonPins = [ ("BLADE_TILE_TUNE", "pull_min=0"); ("BLADE_ORBIT_FOLD", null); ("BLADE_FOLD_ROW_SLOTS", null) ]
    let caseNames = [ for k in 0 .. pcases - 1 -> $"a{k}" ]
    // THE REFERENCE: the raw patterns of the first run; every later run must
    // reproduce them exactly (missing names fail too)
    let mutable bitsRef : Map<string, string> option = None
    let compareBits (name: string) (out: string) (detail: string) =
        let bits = bitsOf out
        let missing = caseNames |> List.filter (fun c -> not (bits.ContainsKey c))
        if not missing.IsEmpty then fail name $"""no raw bits printed for {String.concat ", " (List.truncate 4 missing)} (BLADE_PRINT_BITS)"""
        else
        match bitsRef with
        | None ->
            let nanKey = $"a{nanAt}"
            let zeroKey = $"a{zeroAt}"
            let nanBits = Convert.ToUInt64(bits.[nanKey], 16)
            let isNan = ((nanBits >>> 52) &&& 0x7FFUL) = 0x7FFUL && (nanBits &&& 0xFFFFFFFFFFFFFUL) <> 0UL
            if not isNan then fail name $"the NaN case a{nanAt} printed #{bits.[nanKey]}, not a NaN pattern"
            elif bits.[zeroKey] <> "8000000000000000" then fail name $"the signed-zero case a{zeroAt} printed #{bits.[zeroKey]}, not -0.0 (8000000000000000)"
            elif bits.[$"a{seedZeroAt}"] <> "8000000000000000" then
                fail name $"""the SEEDLESS signed-zero case a{seedZeroAt} printed #{bits.[$"a{seedZeroAt}"]}, not -0.0 (8000000000000000; G74: the left fold of the cells)"""
            elif not (bits.ContainsKey "ph") || bits.[$"a{seedNanAt}"] <> bits.["ph"] then
                fail name $"""the SEEDLESS NaN-head case a{seedNanAt} printed #{bits.[$"a{seedNanAt}"]}, not its first cell's payload #{(match Map.tryFind "ph" bits with Some b -> b | None -> "<missing>")}"""
            else
                bitsRef <- Some bits
                pass name $"{detail}; {bits.Count} raw patterns recorded (NaN a{nanAt} #{bits.[nanKey]}, -0.0 a{zeroAt} #8000000000000000)"
        | Some r ->
            let diffs = caseNames |> List.filter (fun c -> r.[c] <> bits.[c])
            if diffs.IsEmpty then pass name $"{detail}; all {caseNames.Length} raw patterns identical to the reference (NaN payload and -0.0 included)"
            else
                let c = diffs.Head
                fail name $"raw bits differ from the reference at {diffs.Length} case(s): {c} #{bits.[c]} vs #{r.[c]}"
    // (a) the ORDINARY pull build (no instrumentation): T = 1, 3, 8 and a
    // restricted team; bits AND the executed form from the run record
    let pullOrdinaryLeg () =
        let pins = commonPins @ [ ("BLADE_OMP_THREADS", null); ("BLADE_ORBIT_PULL", null); ("BLADE_ORBIT_SCATTER", null) ]
        match buildPull "cpp" pins false "opull_plain" with
        | Error e -> fail "pull build (C++, threads on, REPRO, contract=off)" e
        | Ok (exe, cpp) ->
        let routes = pullCount cpp
        if routes <> pcases then fail "pull build: route" $"{routes} pull3 site(s) in the emitted C++, wanted {pcases} (the comparison would not test the form)"
        else
        pass "pull build: route" $"{routes} pull3 site(s), the needle `blade_scatter::pull3<` at every case, each naming its site for the form census"
        for (t, limit) in [ (1, None); (3, None); (8, None); (8, Some "1") ] do
            let label = match limit with Some l -> $"T = {t}, OMP_THREAD_LIMIT={l} (restricted team)" | None -> $"T = {t}"
            let name = $"pull build at {label}: {pcases} cases, raw bits == the reference; executed form from the run record"
            match runAt exe (string t) limit with
            | Error e -> fail name ("run: " + e)
            | Ok (out, record) ->
            match pullCheck out with
            | Some why -> fail name why
            | None ->
            let forms = formsOf record
            let wrong =
                pexts |> List.indexed |> List.choose (fun (k, p) ->
                    let actualWant = if limit.IsSome then 1 else min t p
                    let formWant = if actualWant > 1 then "row-pull" else "serial-leaf3"
                    match Map.tryFind $"a{k}" forms with
                    | Some (form, _, actual) when form = formWant && actual = actualWant -> None
                    | Some (form, req, actual) -> Some $"a{k} (p = {p}): {form} requested {req} actual {actual}, wanted {formWant} actual {actualWant}"
                    | None -> Some $"a{k}: no `forms` row in the run record")
            if not wrong.IsEmpty then fail name ($"""executed form: {String.concat " | " (List.truncate 4 wrong)}""")
            else
                let form = if limit.IsSome || t = 1 then "serial-leaf3, actual 1" else $"row-pull, actual min({t}, p)"
                compareBits name out $"every e<k> true; run record: every site {form}"
    pullOrdinaryLeg ()
    // (a') the ordinary pull build at the OTHER row groups (the default above
    // is scatter_g = 4): G = 1 / 3 / 8 at T = 8, raw bits == the reference
    // (the seedless all -0.0 and NaN-head cases included, gap G74)
    for g in [ 1; 3; 8 ] do
        let name = $"pull build, scatter_g={g}, at T = 8: {pcases} cases, raw bits == the reference"
        let pins = [ ("BLADE_TILE_TUNE", $"pull_min=0,scatter_g={g}"); ("BLADE_ORBIT_FOLD", null); ("BLADE_FOLD_ROW_SLOTS", null)
                     ("BLADE_OMP_THREADS", null); ("BLADE_ORBIT_PULL", null); ("BLADE_ORBIT_SCATTER", null) ]
        match buildPull "cpp" pins false $"opull_g{g}" with
        | Error e -> fail name e
        | Ok (exe, cpp) ->
        if pullCount cpp <> pcases then fail name $"{pullCount cpp} pull3 site(s), wanted {pcases}"
        else
        match runAt exe "8" None with
        | Error e -> fail name ("run: " + e)
        | Ok (out, _) ->
        match pullCheck out with
        | Some why -> fail name why
        | None -> compareBits name out $"every e<k> true; {pcases} pull3 sites at row groups of {g}"
    // (b) the omp-test-mode pull build: teams observed, forms in the census
    let pullTeamLeg () =
        let pins = commonPins @ [ ("BLADE_OMP_THREADS", null); ("BLADE_ORBIT_PULL", null); ("BLADE_ORBIT_SCATTER", null) ]
        match buildPull "cpp" pins true "opull_on" with
        | Error e -> fail "pull build, omp test mode" e
        | Ok (exe, _) ->
        for t in [ 1; 3; 8 ] do
            let name = $"pull build, omp test mode, at T = {t}: teams observed, raw bits == the reference"
            match runAt exe (string t) None with
            | Error e -> fail name ("run: " + e)
            | Ok (out, _) ->
            match pullCheck out with
            | Some why -> fail name why
            | None ->
            let teams = teamsOf out
            let wrong =
                teams |> List.filter (fun (k, tz, d, form, actual) ->
                    let want = min t pexts.[k]
                    tz <> want || d <> want || actual <> want || form <> (if want > 1 then "row-pull" else "serial-leaf3"))
            if teams.Length <> pcases then fail name $"{teams.Length} observed pull team(s), wanted {pcases}"
            elif not wrong.IsEmpty then
                let (k, tz, d, form, actual) = wrong.Head
                fail name $"pull team / form not min(T, p): a{k} (p = {pexts.[k]}) teamsz={tz} distinct={d} form={form} actual={actual} at T = {t}"
            else compareBits name out $"every e<k> true; {teams.Length} teams observed = min(T, p), each [omp-coverage] line naming its executed form"
    pullTeamLeg ()
    // (c)-(e) the other C++ forms, (f) the LLVM lane -- which has no thread
    // runtime, so its comparator is its SERIAL rank-3 scatter, not a pull
    let others =
        [ ("C++ BLADE_ORBIT_PULL=0 (threads on: the walk's other strict forms)", "cpp", [ ("BLADE_OMP_THREADS", null); ("BLADE_ORBIT_PULL", "0"); ("BLADE_ORBIT_SCATTER", null) ], (fun (t: string) -> if pullCount t <> 0 then Some "a pull3 site with the knob off" else None))
          ("C++ BLADE_OMP_THREADS=1 (the serial scatter)", "cpp", [ ("BLADE_OMP_THREADS", "1"); ("BLADE_ORBIT_PULL", null); ("BLADE_ORBIT_SCATTER", null) ], (fun t -> if pullCount t <> 0 then Some "a pull3 site with threads off" elif scatter3Count t <> pcases then Some $"{scatter3Count t} scatter3 site(s), wanted {pcases}" else None))
          ("C++ walk (BLADE_ORBIT_SCATTER=0, BLADE_FOLD_ROW_SLOTS=0)", "cpp", [ ("BLADE_OMP_THREADS", null); ("BLADE_ORBIT_PULL", null); ("BLADE_ORBIT_SCATTER", "0"); ("BLADE_FOLD_ROW_SLOTS", "0") ], (fun t -> if pullCount t + scatter3Count t <> 0 then Some "a scatter site with the scatter off" else None))
          ("LLVM (its serial scatter)", "llvm", [ ("BLADE_OMP_THREADS", null); ("BLADE_ORBIT_PULL", null); ("BLADE_ORBIT_SCATTER", null) ], (fun t -> if llvmScatterCount t <> pcases then Some $"{llvmScatterCount t} LLVM rank-3 scatter site(s), wanted {pcases}" else None)) ]
    for (label, lane, pins, routeWhy) in others do
        let name = $"{label}: {pcases} cases, raw bits == the pull build's"
        if lane = "llvm" && not llvmOn then skip name "no clang (BLADE_LLVM_CLANG)"
        else
        n <- n + 1
        match buildPull lane (commonPins @ pins) false $"opull_{n}" with
        | Error e -> fail name e
        | Ok (exe, text) ->
        match routeWhy text with
        | Some why -> fail name ("route: " + why)
        | None ->
        match runAt exe "8" None with
        | Error e -> fail name ("run: " + e)
        | Ok (out, _) ->
        match pullCheck out with
        | Some why -> fail name why
        | None -> compareBits name out "every e<k> true; routes as expected"
    // ---- G74: SEEDLESS folds, RAW BITS (walk and scatter, both lanes) -------
    // An unseeded builtin `(+)` at every level of a rank-2 / rank-3 nest over a
    // stored SymIdx pool: all -0.0 cells (s0, s1) must fold to -0.0 EXACTLY
    // (8000000000000000: the left fold of the cells; the checker's former
    // +0.0 seed gave 0000000000000000), and a pool whose every row's FIRST
    // cell is a NaN (x row 0 is NaN, so S(i, 0) is) must carry that cell's
    // payload as the head (s2, s3 == the printed bits of the first cell, h2 /
    // h3). Each s<k> sits beside its walk-forced twin w<k> (the mirror read),
    // and every build -- C++ at scatter_g 1 / 3 (scatter_b 8) / 4 / 8, the C++
    // walk (BLADE_ORBIT_SCATTER=0), the LLVM lane at the same row groups and
    // its walk -- prints the SAME raw bits (BLADE_PRINT_BITS); the interpreter
    // the same decimal text. REPRO + contract=off, T = 1.
    let seedlessLeg () =
        let sb = Text.StringBuilder(header)
        sb.Append("type V13 = Idx<13>\ntype V7 = Idx<7>\n") |> ignore
        for (tag, v, nanRow) in [ ("13", "V13", None); ("7", "V7", None); ("h13", "V13", Some 0); ("h7", "V7", Some 0) ] do
            sb.Append(xDecl tag v nanRow) |> ignore
        sb.Append("let g13 = method_for(x13, x13) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n") |> ignore
        sb.Append("let t7 = method_for(x7, x7, x7) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n") |> ignore
        sb.Append("let gh = method_for(xh13, xh13) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n") |> ignore
        sb.Append("let th = method_for(xh7, xh7, xh7) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n") |> ignore
        let cases =
            [ (2, "V13", "g13", (fun r -> $"-0.0 * abs({r})"), (fun r m -> $"if {m} > 1.0e300 then 0.0 else -0.0 * abs({r})"))
              (3, "V7", "t7", (fun r -> $"-0.0 * abs({r})"), (fun r m -> $"if {m} > 1.0e300 then 0.0 else -0.0 * abs({r})"))
              (2, "V13", "gh", (fun r -> r), (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r}"))
              (3, "V7", "th", (fun r -> r), (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r}")) ]
        cases |> List.iteri (fun k (rank, v, pool, f, twin) ->
            let vars = if rank = 2 then [ "i"; "j" ] else [ "i"; "j"; "k" ]
            let read = $"""{pool}({String.concat ", " vars})"""
            let mirror = $"""{pool}({String.concat ", " (List.rev vars)})"""
            let ks = List.replicate rank "(+)"
            let ss = List.replicate rank ""
            sb.Append($"let s{k} = {nest v vars ks ss (f read)}\n") |> ignore
            sb.Append($"let w{k} = {nest v vars ks ss (twin read mirror)}\n") |> ignore)
        sb.Append("let h2 = gh(0, 0)\nlet h3 = th(0, 0, 0)\n") |> ignore
        let src = sb.ToString()
        let names = [ "s0"; "w0"; "s1"; "w1"; "s2"; "w2"; "s3"; "w3"; "h2"; "h3" ]
        use _sp = pinEnv "BLADE_PRINT" (String.concat "," names)
        let sbuilds =
            [ ("cpp", "scatter_g=1", null, 2); ("cpp", "scatter_g=3,scatter_b=8", null, 2); ("cpp", null, null, 2); ("cpp", "scatter_g=8", null, 2)
              ("cpp", null, "0", 0)
              ("llvm", "scatter_g=1", null, 2); ("llvm", "scatter_g=3,scatter_b=8", null, 2); ("llvm", null, null, 2); ("llvm", "scatter_g=8", null, 2)
              ("llvm", null, "0", 0) ]
        let mutable sref : Map<string, string> option = None
        let mutable sir : Blade.IR.IRProgram option = None
        for (lane, tune, knob, routesPerRank) in sbuilds do
            n <- n + 1
            let label = $"""{(if lane = "cpp" then "C++" else "LLVM")} {(if knob = "0" then "walk (BLADE_ORBIT_SCATTER=0)" elif isNull tune then "scatter_g=4" else tune)}"""
            let name = $"G74 seedless folds, {label}: all -0.0 -> 8000000000000000, NaN head kept, == the walk twins and the reference (raw bits)"
            if lane = "llvm" && not llvmOn then skip name "no clang (BLADE_LLVM_CLANG)"
            else
            match buildSrc src lane tune knob $"oseed_{n}" with
            | Error e -> fail name e
            | Ok (exe, text) ->
            let rc = routeCount lane text
            if rc <> 2 * routesPerRank then fail name $"route census: {rc} scatter site(s), wanted {2 * routesPerRank}"
            else
            match runAt exe "1" None with
            | Error e -> fail name ("run: " + e)
            | Ok (out, _) ->
            let bits = bitsOf out
            let missing = names |> List.filter (fun c -> not (bits.ContainsKey c))
            if not missing.IsEmpty then fail name $"""no raw bits for {String.concat ", " missing}"""
            else
            let isNan (b: string) = let u = Convert.ToUInt64(b, 16) in ((u >>> 52) &&& 0x7FFUL) = 0x7FFUL && (u &&& 0xFFFFFFFFFFFFFUL) <> 0UL
            let twinBad = [ 0 .. 3 ] |> List.filter (fun k -> bits.[$"s{k}"] <> bits.[$"w{k}"])
            if not twinBad.IsEmpty then
                let k = twinBad.Head
                fail name $"""s{k} #{bits.[$"s{k}"]} differs from its walk twin #{bits.[$"w{k}"]}"""
            elif bits.["s0"] <> "8000000000000000" || bits.["s1"] <> "8000000000000000" then
                fail name $"""the seedless all -0.0 folds must be -0.0 exactly: s0 #{bits.["s0"]}, s1 #{bits.["s1"]} (0000000000000000 is the former +0.0 seed)"""
            elif not (isNan bits.["h2"] && isNan bits.["h3"]) then fail name "the NaN pools' first cells are not NaN (the probe would not test the head)"
            elif bits.["s2"] <> bits.["h2"] || bits.["s3"] <> bits.["h3"] then
                fail name $"""the NaN head's payload must survive: s2 #{bits.["s2"]} vs first cell #{bits.["h2"]}, s3 #{bits.["s3"]} vs #{bits.["h3"]}"""
            else
            match sref with
            | None ->
                sref <- Some bits
                (match (try lower src with _ -> Error "") with Ok ir -> sir <- Some ir | Error _ -> ())
                pass name $"""s0 = s1 = -0.0 (#8000000000000000); s2 / s3 carry the first cell's NaN #{bits.["h2"]}; every s<k> == its walk twin; {rc} scatter site(s)"""
            | Some r ->
                let diffs = names |> List.filter (fun c -> r.[c] <> bits.[c])
                if diffs.IsEmpty then pass name $"all {names.Length} raw patterns identical to the reference build; {rc} scatter site(s)"
                else fail name $"raw bits differ from the reference at {diffs.Head}: #{bits.[diffs.Head]} vs #{r.[diffs.Head]}"
        let iname = "G74 seedless folds, interpreter: -0.0 and nan, the reference's decimal text"
        match sir with
        | None -> skip iname "no reference build"
        | Some ir ->
            match (try Ok (Blade.Interp.Run.runProgram ir "oseed_interp" Blade.Interp.Value.defaultLimits) with ex -> Error ex.Message) with
            | Error e -> fail iname ("interpreter raised: " + e)
            | Ok res when res.ExitCode <> 0 -> fail iname ($"interpreter exit {res.ExitCode}: {res.Stderr.Trim()}")
            | Ok res ->
                let ls =
                    res.Stdout.Replace("\r\n", "\n").Split('\n')
                    |> Array.choose (fun l -> let m = Text.RegularExpressions.Regex.Match(l, @"^([a-z0-9]+) = (.*)$") in if m.Success then Some (m.Groups.[1].Value, m.Groups.[2].Value) else None)
                    |> Map.ofArray
                let want = [ for c in [ "s0"; "w0"; "s1"; "w1" ] -> (c, "-0.0") ] @ [ for c in [ "s2"; "w2"; "s3"; "w3" ] -> (c, "nan") ]
                let bad = want |> List.filter (fun (c, v) -> Map.tryFind c ls <> Some v)
                if bad.IsEmpty then pass iname "s0..w1 print -0.0, s2..w3 print nan"
                else
                    let (c, v) = bad.Head
                    fail iname $"""{c} = {(match Map.tryFind c ls with Some x -> x | None -> "<missing>")}, wanted {v}"""
    seedlessLeg ()
    // ---- G74: THE ROUTE DECIDES THE SEED ---------------------------------------
    // (1) a `where repro` function with the build's REPRO UNSET: its fused
    // deferred folds (plain and joined) are STRICT -- the lifted kernels
    // inherit the demand -- so all -0.0 cells fold to -0.0 and the
    // cancellation row [1e16, 1, -1e16, 1, 0] to exactly 1.0 (the ascending
    // chain; a reordered tree gives 2.0) in the C++ lane, the LLVM lane and the
    // interpreter, and neither compiled lane takes a licensed form for them:
    // the program's ONE `omp simd reduction` is the top-level licensed control
    // `lic`, and no LLVM function but main carries `reassoc`. (2) the LICENSED
    // route keeps the BASE seed byte for byte: the same unseeded spellings at
    // top level -- a deferred fold, a join, a row walk over a SymIdx pool, a
    // cell-epilogue consumer -- emit no `-0.0` in the default build (C++ text;
    // LLVM: no 0x8000000000000000 constant), and do under BLADE_FP_REPRO=1 --
    // a presence / absence check of the identity constant, NOT base-vs-head
    // byte equality (that is `bench/harness/emission-sweep.ps1`). The rank-2
    // full fold left this list: its accumulator loop is serial on every
    // route, so it takes the identity in every build (leg 4e). (3) the EMPTY deferred fold is its identity: -0.0
    // under REPRO, the licensed base +0.0 in the default build (formalism 6.4;
    // the STORED spelling raises BL8003 -- corpus sql-reduce/031).
    let routeLeg () =
        let linesOfOut (out: string) : Map<string, string> =
            out.Replace("\r\n", "\n").Split('\n')
            |> Array.choose (fun l ->
                let m = Text.RegularExpressions.Regex.Match(l, @"^([A-Za-z_][A-Za-z_0-9]*) = (.*)$")
                if m.Success then Some (m.Groups.[1].Value, m.Groups.[2].Value) else None)
            |> Map.ofArray
        use _sp0 = pinEnv "BLADE_FP_REPRO" null
        use _sc0 = pinEnv "BLADE_FP_CONTRACT" null
        use _st0 = pinEnv "BLADE_OMP_THREADS" null
        let reproSrc =
            "type N = Idx<5>\ntype M = Idx<7>\n"
            + "let v: Array<Float64 like N> = [1.0e16, 1.0, -1.0e16, 1.0, 0.0]\n"
            + "let z: Array<Float64 like N> = [-0.0, -0.0, -0.0, -0.0, -0.0]\n"
            + "function cancel() where repro -> Float64 = reduce(method_for(v) <@> lambda(x) -> x * 1.0, (+))\n"
            + "function negz() where repro -> Float64 = reduce(method_for(z) <@> lambda(x) -> x * 1.0, (+))\n"
            + "function jz() where repro -> Float64 = {\n    let a, b = object_for(<&!>) <@> (reduce(method_for(z) <@> lambda(x) -> x * 1.0, (+)), reduce(method_for(v) <@> lambda(x) -> x * 1.0, (+)))\n    a\n}\n"
            + "function jc() where repro -> Float64 = {\n    let a, b = object_for(<&!>) <@> (reduce(method_for(z) <@> lambda(x) -> x * 1.0, (+)), reduce(method_for(v) <@> lambda(x) -> x * 1.0, (+)))\n    b\n}\n"
            + "let c = cancel()\nlet n = negz()\nlet ja = jz()\nlet jb = jc()\n"
            + "let lic = reduce(method_for(v) <@> lambda(x) -> x * 1.0, (+))\n"
        let names = [ "c"; "n"; "ja"; "jb" ]
        let want = Map.ofList [ ("c", "3ff0000000000000"); ("n", "8000000000000000"); ("ja", "8000000000000000"); ("jb", "3ff0000000000000") ]
        use _pr = pinEnv "BLADE_PRINT" (String.concat "," (names @ [ "lic" ]))
        let llvmDefines (ll: string) =
            Text.RegularExpressions.Regex.Matches(ll.Replace("\r\n", "\n"), @"(?ms)^define [^\n]*?@([A-Za-z_0-9.]+)\(.*?^\}")
            |> Seq.map (fun m -> (m.Groups.[1].Value, m.Value)) |> List.ofSeq
        let mutable reproIr : Blade.IR.IRProgram option = None
        for lane in [ "cpp"; "llvm" ] do
            let name = $"""G74 route, {(if lane = "cpp" then "C++" else "LLVM")} default build: folds in `where repro` functions are strict (cancellation 1.0, all -0.0 -> -0.0, plain and joined; raw bits) and take no licensed form"""
            if lane = "llvm" && not llvmOn then skip name "no clang (BLADE_LLVM_CLANG)"
            else
            n <- n + 1
            match buildSrc reproSrc lane null null $"oroute_{n}" with
            | Error e -> fail name e
            | Ok (exe, text) ->
            (match (try lower reproSrc with _ -> Error "") with Ok ir -> reproIr <- Some ir | Error _ -> ())
            let licensedFormOk =
                if lane = "cpp" then
                    let k = Text.RegularExpressions.Regex.Matches(text, "BLADE_OMP_SIMD_REDUCTION").Count
                    if k = 1 then None else Some $"{k} `omp simd reduction` site(s), wanted exactly 1 (the top-level licensed `lic`)"
                else
                    let bad = llvmDefines text |> List.filter (fun (f, body) -> f <> "main" && body.Contains "reassoc") |> List.map fst
                    let mainReassoc = llvmDefines text |> List.exists (fun (f, body) -> f = "main" && body.Contains "reassoc")
                    if not bad.IsEmpty then Some $"""`reassoc` inside {String.concat ", " bad}"""
                    elif not mainReassoc then Some "the top-level licensed control `lic` carries no `reassoc` (the control would not test the lane)"
                    else None
            // the C++ fold-form records name WHY the repro bodies' folds are serial
            let recordWhy =
                if lane <> "cpp" then None
                else
                    Blade.Effects.Decisions.start ()
                    try
                        (match (try lower reproSrc with _ -> Error "") with
                         | Ok ir -> CodeGen.genSelfContainedProgramFromIR ir "oroute_plan" |> ignore
                         | Error _ -> ())
                        let ds = Blade.Effects.Decisions.drain ()
                        let named =
                            ds |> List.filter (fun d ->
                                d.Rule = "fold-form"
                                && (match d.Outcome with
                                    | Blade.Effects.Declined why -> why.Contains "a `where repro` demand" || why.Contains "a leg has no reorder licence"
                                    | _ -> false))
                        if named.Length >= 4 then None
                        else Some $"{named.Length} fold-form record(s) name the repro demand (or an unlicensed join leg), wanted 4 (cancel, negz, jz, jc)"
                    finally Blade.Effects.Decisions.drain () |> ignore
            match (match licensedFormOk with Some w -> Some w | None -> recordWhy) with
            | Some why -> fail name ("route: " + why)
            | None ->
            match runAt exe "8" None with
            | Error e -> fail name ("run: " + e)
            | Ok (out, _) ->
            let bits = bitsOf out
            let bad = names |> List.filter (fun k -> Map.tryFind k bits <> Some want.[k])
            if bad.IsEmpty then pass name $"""c = jb = 1.0 (#3ff0000000000000), n = ja = -0.0 (#8000000000000000); {(if lane = "cpp" then "the one simd reduction is `lic`" else "no `reassoc` outside main")}"""
            else
                let k = bad.Head
                fail name $"""{k} #{(match Map.tryFind k bits with Some b -> b | None -> "<missing>")}, wanted #{want.[k]}"""
        let iname = "G74 route, interpreter: folds in `where repro` functions print 1.0 / -0.0 / -0.0 / 1.0"
        match reproIr with
        | None -> skip iname "no reference build"
        | Some ir ->
            match (try Ok (Blade.Interp.Run.runProgram ir "oroute_interp" Blade.Interp.Value.defaultLimits) with ex -> Error ex.Message) with
            | Error e -> fail iname ("interpreter raised: " + e)
            | Ok res when res.ExitCode <> 0 -> fail iname ($"interpreter exit {res.ExitCode}: {res.Stderr.Trim()}")
            | Ok res ->
                let ls = linesOfOut res.Stdout
                let expectTxt = Map.ofList [ ("c", "1.0"); ("n", "-0.0"); ("ja", "-0.0"); ("jb", "1.0") ]
                let bad = names |> List.filter (fun k -> Map.tryFind k ls <> Some expectTxt.[k])
                if bad.IsEmpty then pass iname "c = jb = 1.0, n = ja = -0.0 (the ordered chain)"
                else fail iname $"""{bad.Head} = {(match Map.tryFind bad.Head ls with Some x -> x | None -> "<missing>")}, wanted {expectTxt.[bad.Head]}"""
        // (2) the licensed route's seeds: base in the default build, identity under REPRO
        let licSrc =
            "type V = Idx<7>\ntype Obs = Idx<3>\ntype I = Idx<1003>\ntype R = Idx<5>\n"
            + "let x = method_for(range<V>, range<Obs>) <@> lambda(i, s) -> Float64((i * 5 + s * 3) % 7) - 2.5 |> compute\n"
            + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
            + "let u = method_for(range<I>) <@> lambda(i) -> Float64(Int64(i) % 7) * 0.5 |> compute\n"
            + "let w = method_for(range<I>) <@> lambda(i) -> Float64(Int64(i) % 5) * 0.25 |> compute\n"
            + "let m = method_for(range<R>, range<R>) <@> lambda(i, j) -> Float64(i + j) * 0.5 |> compute\n"
            + "let d = reduce(method_for(u) <@> lambda(q) -> q * 2.0, (+))\n"
            + "let (ja, jb) = object_for(<&!>) <@> (reduce(method_for(u) <@> lambda(q) -> q * 2.0, (+)), reduce(method_for(w) <@> lambda(q) -> q * 3.0, (+)))\n"
            + "let walk = reduce(method_for(range<V>) <@> lambda(i) -> reduce(method_for(range<V>) <@> lambda(j) -> g(i, j) * g(i, j), (+)), (+))\n"
            + "let y2 = method_for(zip(u, w)) <@> lambda(p, q) -> 2.0 * p + q |> compute\n"
            + "let ep = reduce(method_for(y2) <@> lambda(q) -> q * q, (+))\n"
        use _pl = pinEnv "BLADE_PRINT" "d,ja,jb,walk,ep"
        for (lane, repro) in [ ("cpp", false); ("llvm", false); ("cpp", true); ("llvm", true) ] do
            let lname = if lane = "cpp" then "C++" else "LLVM"
            let name = $"""G74 route, {lname} {(if repro then "REPRO" else "default (licensed)")}: the unseeded seeds are {(if repro then "the identity -0.0" else "the BASE +0.0 (no -0.0 in the emission)")} -- deferred fold, join, row walk, cell epilogue"""
            if lane = "llvm" && not llvmOn then skip name "no clang (BLADE_LLVM_CLANG)"
            else
            use _rp = pinEnv "BLADE_FP_REPRO" (if repro then "1" else null)
            match (try lower licSrc with ex -> Error ex.Message) with
            | Error e -> fail name ("lower: " + e)
            | Ok ir ->
            let text =
                if lane = "cpp" then (try Ok (fst (CodeGen.genSelfContainedProgramFromIR ir "olic")) with ex -> Error ex.Message)
                else (try EmitLlvm.tryEmitProgramNamed "olic" ir with ex -> Error ex.Message)
            CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
            match text with
            | Error e -> fail name ($"emit: {e}")
            | Ok t ->
            let negZero = if lane = "cpp" then t.Contains "-0.0" else t.Contains "0x8000000000000000"
            if repro && not negZero then fail name "no identity seed in the REPRO emission (the strict route would keep +0.0)"
            elif not repro && negZero then fail name "a -0.0 seed in the LICENSED emission (the base seed must be kept byte for byte)"
            else pass name (if repro then "identity seeds present" else "no -0.0 anywhere in the emission")
        // (3) the empty deferred fold: its identity on the strict route, the base on the licensed one
        let emptySrc =
            "type Z = Idx<0>\nlet empty: Array<Float64 like Z> = []\n"
            + "let s = reduce(method_for(empty) <@> lambda(q) -> q * 1.0, (+))\n"
        use _pe = pinEnv "BLADE_PRINT" "s"
        for (lane, repro) in [ ("cpp", true); ("cpp", false); ("llvm", true); ("llvm", false) ] do
            let lname = if lane = "cpp" then "C++" else "LLVM"
            let wantBits = if repro then "8000000000000000" else "0000000000000000"
            let name = $"""G74 route, {lname} {(if repro then "REPRO" else "default")}: an EMPTY deferred unseeded fold is #{wantBits}"""
            if lane = "llvm" && not llvmOn then skip name "no clang (BLADE_LLVM_CLANG)"
            else
            use _rp = pinEnv "BLADE_FP_REPRO" (if repro then "1" else null)
            n <- n + 1
            match buildSrc emptySrc lane null null $"oempty_{n}" with
            | Error e when lane = "llvm" && e.Contains "refused" -> skip name ("the LLVM lane refuses the program: " + e)
            | Error e -> fail name e
            | Ok (exe, _) ->
            match runAt exe "1" None with
            | Error e -> fail name ("run: " + e)
            | Ok (out, _) ->
            match Map.tryFind "s" (bitsOf out) with
            | Some b when b = wantBits -> pass name $"s = #{b}"
            | other -> fail name $"""s = #{(match other with Some b -> b | None -> "<missing>")}, wanted #{wantBits}"""
        // (4) THE EFFECTIVE ROUTE, witnessed in every lane (W4B-D; the second
        // Codex look of W4-3, NEW 1 / NEW 2 / scope): one program per case,
        // built C++ and LLVM and run through the interpreter, in the default
        // build (REPRO unset) and/or under REPRO; raw bits per Float64 binding
        // in the compiled lanes, decimal text in the interpreter.
        let stripBitsOf (s: string) = Text.RegularExpressions.Regex.Replace(s, @" #[0-9a-f]{16}", "")
        let threeLane (label: string) (src: string) (names: string list) (reproB: bool)
                      (wantBits: (string * string) list) (routeWhy: string -> string -> string option) =
            use _rp3 = pinEnv "BLADE_FP_REPRO" (if reproB then "1" else null)
            use _pp3 = pinEnv "BLADE_PRINT" (String.concat "," names)
            let build = if reproB then "REPRO" else "default build"
            for lane in [ "cpp"; "llvm" ] do
                let lname = if lane = "cpp" then "C++" else "LLVM"
                let name = $"G74 route, {label} ({lname}, {build})"
                if lane = "llvm" && not llvmOn then skip name "no clang (BLADE_LLVM_CLANG)"
                else
                n <- n + 1
                match buildSrc src lane null null $"o3l_{n}" with
                | Error e when lane = "llvm" && e.Contains "refused" -> skip name ("the LLVM lane refuses the program: " + e)
                | Error e -> fail name e
                | Ok (exe, text) ->
                match routeWhy lane text with
                | Some why -> fail name ("route: " + why)
                | None ->
                match runAt exe "8" None with
                | Error e -> fail name ("run: " + e)
                | Ok (out, _) ->
                    let bits = bitsOf out
                    match wantBits |> List.filter (fun (k, b) -> Map.tryFind k bits <> Some b) with
                    | [] -> pass name (String.concat ", " (wantBits |> List.map (fun (k, b) -> $"{k} #{b}")))
                    | (k, b) :: _ -> fail name $"""{k} #{(match Map.tryFind k bits with Some x -> x | None -> "<missing>")}, wanted #{b}"""
            let iname = $"G74 route, {label} (interpreter, {build}-lowered)"
            match (try lower src with ex -> Error ex.Message) with
            | Error e -> fail iname ("lower: " + e)
            | Ok ir ->
                match (try Ok (Blade.Interp.Run.runProgram ir "o3l_interp" Blade.Interp.Value.defaultLimits) with ex -> Error ex.Message) with
                | Error e -> fail iname ("interpreter raised: " + e)
                | Ok res when res.ExitCode <> 0 -> fail iname ($"interpreter exit {res.ExitCode}: {res.Stderr.Trim()}")
                | Ok res ->
                    let ls = linesOfOut res.Stdout |> Map.map (fun _ v -> stripBitsOf v)
                    let decimalOf (b: string) =
                        match b with
                        | "8000000000000000" -> "-0.0"
                        | "0000000000000000" -> "0.0"
                        | "fff0000000000000" -> "-inf"
                        | "3ff0000000000000" -> "1.0"
                        | "4000000000000000" -> "2.0"
                        | "4010000000000000" -> "4.0"
                        | "4022000000000000" -> "9.0"
                        | other -> "#" + other
                    let want = wantBits |> List.map (fun (k, b) -> (k, decimalOf b))
                    match want |> List.filter (fun (k, v) -> Map.tryFind k ls <> Some v) with
                    | [] -> pass iname (String.concat ", " (want |> List.map (fun (k, v) -> $"{k} = {v}")))
                    | (k, v) :: _ -> fail iname $"""{k} = {(match Map.tryFind k ls with Some x -> x | None -> "<missing>")}, wanted {v}"""
        let noRoute = fun (_: string) (_: string) -> (None: string option)
        // (4a) a WRITING map (G74, NEW 1): the kernel writes a captured counter,
        // so its fold keeps the SERIAL chain in every build and lane (gap G41,
        // corpus loops/496) -- no effective reorder licence even in the default
        // build -- and its unseeded seed is the exact identity there too: nine
        // -0.0 terms fold to -0.0 in the DEFAULT build as under REPRO (the base
        // +0.0 seed gave +0.0, base 94487a90), and all nine writes happen.
        let wmSrc =
            "type Ix = Idx<9>\nlet mut G = [0.0]\n"
            + "let total = reduce(method_for(range<Ix>) <@> lambda(i) -> {\n    G(0) = G(0) + 1.0\n    let r = -0.0 * G(0)\n    r\n}, (+))\n"
            + "let writes = G(0)\n"
        for reproB in [ false; true ] do
            threeLane "writing map, all -0.0 terms" wmSrc [ "total"; "writes" ] reproB
                [ ("total", "8000000000000000"); ("writes", "4022000000000000") ] noRoute
        // (4b) G80: a NAMED fold kernel declared OUTSIDE a repro body, folded
        // INSIDE it -- stored, deferred and joined -- declines reordering there:
        // the cancellation row [1e16, 1, -1e16, 1, 0, 0, 0, 0, 1] is exactly
        // 2.0 (the ascending chain; the C++ K-lane tree gave 1.0 at base
        // 94487a90), while the same kernel's top-level use `lics` stays
        // licensed. C++: the fold-form records of `stored` / `deferred` name the
        // repro demand, the join is declined for an unlicensed leg, and `lics`
        // takes a licensed form; LLVM: no function but main carries `reassoc`.
        let nkSrc =
            "type N = Idx<9>\nlet v: Array<Float64 like N> = [1.0e16, 1.0, -1.0e16, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0]\n"
            + "function add(a: Float64, b: Float64) where comm(a, b), omp -> Float64 = a + b\n"
            + "function stored() where repro -> Float64 = reduce(v, add, 0.0)\n"
            + "function deferred() where repro -> Float64 = reduce(method_for(v) <@> lambda(x) -> x * 1.0, add, 0.0)\n"
            + "function joined() where repro -> Float64 = {\n    let a, b = object_for(<&!>) <@> (reduce(v, add, 0.0), reduce(method_for(v) <@> lambda(x) -> x * 1.0, (+)))\n    a + b\n}\n"
            + "let s = stored()\nlet d = deferred()\nlet j = joined()\nlet lics = reduce(v, add, 0.0)\n"
        let nkRoute (lane: string) (text: string) =
            if lane <> "cpp" then
                let bad = llvmDefines text |> List.filter (fun (f, body) -> f <> "main" && body.Contains "reassoc") |> List.map fst
                if bad.IsEmpty then None else Some $"""`reassoc` inside {String.concat ", " bad}"""
            else
                Blade.Effects.Decisions.start ()
                try
                    (match (try lower nkSrc with _ -> Error "") with
                     | Ok ir -> CodeGen.genSelfContainedProgramFromIR ir "onk_plan" |> ignore
                     | Error _ -> ())
                    let all = Blade.Effects.Decisions.drain ()
                    let ds = all |> List.filter (fun d -> d.Rule = "fold-form")
                    let declinedWith (rule: string) (subj: string option) (needle: string) =
                        all |> List.exists (fun d ->
                            d.Rule = rule && (match subj with Some s -> d.Subject = s | None -> true)
                            && (match d.Outcome with Blade.Effects.Declined why -> why.Contains needle | _ -> false))
                    let reproForms =
                        ds |> List.filter (fun d -> match d.Outcome with Blade.Effects.Declined why -> why.Contains "a `where repro` demand" | _ -> false)
                    let licApplied = ds |> List.exists (fun d -> d.Subject = "lics" && (match d.Outcome with Blade.Effects.Applied -> true | _ -> false))
                    // the fold-form subjects are the emitted accumulators (`stored`,
                    // `__ret..` for the deferred body, `__v..` for the join); the
                    // omp census lines (the copied `where omp` request, declined,
                    // never dropped) name the functions
                    if reproForms.Length < 2 then Some $"{reproForms.Length} fold-form record(s) name the repro demand, wanted 2 (stored, deferred)"
                    elif not (declinedWith "fold-form" (Some "stored") "a `where repro` demand") then Some "no fold-form record declines `stored`'s fold for the repro demand"
                    elif not (declinedWith "fold-form" None "a leg has no reorder licence") then Some "the join in `joined` is not declined for its unlicensed leg"
                    elif not (declinedWith "omp" (Some "deferred") "`where repro`") then Some "no omp census line declines `deferred`'s request for the repro demand"
                    elif not (declinedWith "omp" (Some "joined") "`where repro`") then Some "no omp census line declines `joined`'s request for the repro leg"
                    elif not licApplied then Some "the top-level licensed control `lics` took no licensed form (the control would not test the veto)"
                    else None
                finally Blade.Effects.Decisions.drain () |> ignore
        threeLane "named kernel folded in a repro body (stored, deferred, joined)" nkSrc [ "s"; "d"; "j" ] false
            [ ("s", "4000000000000000"); ("d", "4000000000000000"); ("j", "4010000000000000") ] nkRoute
        // (4c) SCOPE of the repro demand (second look, scope): it also covers an
        // explicitly SEEDED fold (the cancellation row, exactly 2.0) and the
        // CONTRACTION of a lambda lifted from the body: `x * x - c` with x = 1 +
        // 2^-27 and c = round(x * x) is exactly 0.0 rounded twice, 2^-54 if fused
        let scSrc =
            "type N = Idx<9>\ntype One = Idx<1>\n"
            + "let v: Array<Float64 like N> = [1.0e16, 1.0, -1.0e16, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0]\nlet u: Array<Float64 like One> = [1.0000000074505806]\n"
            + "function seeded(w: Array<Float64 like N>) where repro -> Float64 = reduce(method_for(w) <@> lambda(x) -> x * 1.0, (+), 0.0)\n"
            + "function contracted(w: Array<Float64 like One>) where repro -> Float64 = reduce(method_for(w) <@> lambda(x) -> x * x - 1.0000000149011612, (+), 0.0)\n"
            + "let sd = seeded(v)\nlet ct = contracted(u)\n"
        threeLane "seeded fold and lifted contraction in a repro body" scSrc [ "sd"; "ct" ] false
            [ ("sd", "4000000000000000"); ("ct", "0000000000000000") ] noRoute
        // (4d) GENERIC identity types: a generic unseeded fold's strict seed is
        // its INSTANCE's identity -- Float64 -0.0, Float32 -0.0f -- under REPRO
        // (the reciprocals carry the signs)
        let gtSrc =
            "function negsum(v: T^1) = reduce(method_for(v) <@> lambda(x) -> -(x * zero), (+))\n"
            + "let a64: Array<Float64 like Idx<2>> = [1.0, 2.0]\nlet a32: Array<Float32 like Idx<2>> = [Float32(1.0), Float32(2.0)]\n"
            + "let r64 = 1.0 / negsum(a64)\nlet r32 = 1.0 / Float64(negsum(a32))\n"
        threeLane "generic instance identity types" gtSrc [ "r64"; "r32" ] true
            [ ("r64", "fff0000000000000"); ("r32", "fff0000000000000") ] noRoute
        // ...and its Float64 instance alone, which the LLVM lane takes (it
        // refuses the Float32 array literal above)
        let gt64Src =
            "function negsum(v: T^1) = reduce(method_for(v) <@> lambda(x) -> -(x * zero), (+))\n"
            + "let a64: Array<Float64 like Idx<2>> = [1.0, 2.0]\nlet r64 = 1.0 / negsum(a64)\n"
        threeLane "generic instance identity, Float64" gt64Src [ "r64" ] true [ ("r64", "fff0000000000000") ] noRoute
        // (4e) the rank-2 FULL fold `reduce(m, (+), axes = 2)`: a plain
        // accumulator loop, serial on every route, so its unseeded seed is the
        // identity in EVERY build -- all -0.0 cells fold to -0.0 by default too
        // (base 94487a90: +0.0 by default, -0.0 under REPRO)
        let rkSrc =
            "type A = Idx<3>\ntype B = Idx<5>\n"
            + "let m: Array<Float64 like A, B> = method_for(range<A, B>) <@> lambda(i, j) -> -0.0 * Float64(i + j + 1) |> compute\n"
            + "let full = reduce(m, (+), axes = 2)\n"
        for reproB in [ false; true ] do
            threeLane "rank-2 full fold, all -0.0 cells" rkSrc [ "full" ] reproB [ ("full", "8000000000000000") ] noRoute
        // (4f) the EMPTY deferred fold in the DEFAULT interpreter: the licensed
        // base seed, +0.0 (the compiled default lanes are pinned above; the
        // REPRO interpreter below)
        let edName = "G74 route, interpreter (default-lowered): an EMPTY deferred unseeded fold prints 0.0"
        // (4g) G80 twin hygiene (second look of W4B-D, finding 2): a named
        // kernel whose body lifts a lambda capturing its PARAMETER, folded in a
        // repro body -- the twin shares the parameter ids, so the capture binds
        // -- stored and deferred, 2.0 in every lane (the C++ K-lane tree gave
        // 1.0 at base); its ordinary top-level use `t` stays licensed (unpinned)
        let capSrc =
            "type N = Idx<9>\nlet v: Array<Float64 like N> = [1.0e16, 1.0, -1.0e16, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0]\n"
            + "function addc(a: Float64, b: Float64) where comm(a, b) -> Float64 = {\n    let h = lambda(x: Float64) -> a + x\n    h(b)\n}\n"
            + "function rf() where repro -> Float64 = reduce(v, addc, 0.0)\n"
            + "function rd() where repro -> Float64 = reduce(method_for(v) <@> lambda(x) -> x * 1.0, addc, 0.0)\n"
            + "let s = rf()\nlet sd = rd()\nlet t = reduce(v, addc, 0.0)\n"
        threeLane "a parameter-capturing named kernel folded in a repro body (its twin)" capSrc [ "s"; "sd" ] false
            [ ("s", "4000000000000000"); ("sd", "4000000000000000") ] noRoute
        // (4h) a deferred fold INSIDE A SIDE TABLE (finding 1): a random fill's
        // parameter. The unseeded mark there is resolved like any other (the
        // first W4B-D cut left it in the RandomInits payload, which the
        // interpreter refused as an unresolved node); the interpreter draws the
        // same values as with the literal parameter 3.0. (Both compiled lanes
        // refuse the shape, BL7004 -- a deferred fold in expression position,
        // the LLVM lane falling back -- before and after this change.)
        let sideName = "G74 route, a deferred fold in a random-fill parameter (interpreter): the mark resolved, the draws those of the literal parameter"
        let sideProbe () =
            let sideSrc =
                "import rand as r\nlet w: Array<Float64 like Idx<2>> = [1.0, 2.0]\n"
                + "let u = r.gamma(7, reduce(method_for(w) <@> lambda(x) -> x, (+)), 1.0, 4)\n"
                + "let uc = r.gamma(7, 3.0, 1.0, 4)\nlet su = reduce(u, (+))\nlet suc = reduce(uc, (+))\n"
            use _sp = pinEnv "BLADE_PRINT" "su,suc"
            for reproB in [ false; true ] do
                use _rs = pinEnv "BLADE_FP_REPRO" (if reproB then "1" else null)
                let nm = $"""{sideName} ({(if reproB then "REPRO" else "default")})"""
                match (try lower sideSrc with ex -> Error ex.Message) with
                | Error e -> fail nm ("lower: " + e)
                | Ok ir ->
                    match (try Ok (Blade.Interp.Run.runProgram ir "oside_interp" Blade.Interp.Value.defaultLimits) with ex -> Error ex.Message) with
                    | Error e -> fail nm ("interpreter raised: " + e)
                    | Ok res when res.ExitCode <> 0 -> fail nm ($"interpreter exit {res.ExitCode}: {res.Stderr.Trim()}")
                    | Ok res ->
                        let ls = linesOfOut res.Stdout
                        match Map.tryFind "su" ls, Map.tryFind "suc" ls with
                        | Some a, Some b when a = b -> pass nm $"su = suc = {a}"
                        | a, b -> fail nm $"su = {a}, suc = {b}"
        sideProbe ()
        let edProbe () =
            use _rpd = pinEnv "BLADE_FP_REPRO" null
            match (try lower emptySrc with ex -> Error ex.Message) with
            | Error e -> fail edName ("lower: " + e)
            | Ok ir ->
                match (try Ok (Blade.Interp.Run.runProgram ir "oempty_interp_d" Blade.Interp.Value.defaultLimits) with ex -> Error ex.Message) with
                | Error e -> fail edName ("interpreter raised: " + e)
                | Ok res ->
                    match Map.tryFind "s" (linesOfOut res.Stdout) with
                    | Some "0.0" -> pass edName "s = 0.0"
                    | other -> fail edName $"""s = {(match other with Some x -> x | None -> "<missing>")}, wanted 0.0"""
        edProbe ()
        let ename = "G74 route, interpreter (REPRO-lowered): an EMPTY deferred unseeded fold prints -0.0"
        use _rp = pinEnv "BLADE_FP_REPRO" "1"
        match (try lower emptySrc with ex -> Error ex.Message) with
        | Error e -> fail ename ("lower: " + e)
        | Ok ir ->
            match (try Ok (Blade.Interp.Run.runProgram ir "oempty_interp" Blade.Interp.Value.defaultLimits) with ex -> Error ex.Message) with
            | Error e -> fail ename ("interpreter raised: " + e)
            | Ok res ->
                match Map.tryFind "s" (linesOfOut res.Stdout) with
                | Some "-0.0" -> pass ename "s = -0.0"
                | other -> fail ename $"""s = {(match other with Some x -> x | None -> "<missing>")}, wanted -0.0"""
    routeLeg ()
    // ---- THE ROW ORBIT ACTION (round 3, D-P2 / gap G73) ---------------------
    // A generated program of rank-2 ROW ACTIONS `a<k> = method_for(range<V>)
    // <@> lambda(i) -> reduce(method_for(range<V>) <@> lambda(j) -> BODY, K, S)
    // |> compute` over stored SymIdx<2, p> pools at p in {1, 2, 3, 5, 7, 13, 23,
    // 37}, bodies x / x * v(j) / x * (v(j) - y(i)) / (x - y(i)) * v(j) * v(i)
    // (own and partner roles of one weight), kernels cycling (+) / radd /
    // rhalf / rmax, seeds cycling 0.0 / -0.0 / 0.75 / -1.25; plus a NaN pool,
    // an all -0.0 action with an EXPLICIT -0.0 seed (explicit-seed
    // preservation, raw bits), the SEEDLESS all -0.0 witness `aseedless` (the
    // G74 record's witness: a seedless `(+)` row is the left fold of its
    // cells, -0.0 in every row -- it was +0.0 before G74's fix, the checker's
    // former seed -- pinned equal across the action, the walk and the
    // interpreter), the seedless NaN-head rows `anan` (every row's first cell
    // a NaN: its payload survives) and D/5's
    // mixed-nest row [1e16, -1e16, 1] (every row
    // of `amn` must be EXACTLY 1.0, the ascending chain -- a regrouped chain
    // gives 0.0 -- and the weighted walk `wmn` over it, declined, 3.0). Each
    // a<k> sits beside a WALK-FORCED twin b<k> whose body also reads the
    // mirror cell (declined: the pool read at two coordinate orders). Every
    // build runs with BLADE_PRINT_BITS=1, so each printed Float64 carries its
    // raw pattern: within the build, a<k>'s line must equal b<k>'s
    // byte-for-byte (the action IS the walk, raw bits, Float64); across builds
    // -- C++ at scatter_g 1 / 3 (scatter_b 8) / 4 / 8, C++ with
    // BLADE_ORBIT_ACTIONS=0 (all walk), LLVM at 1 / 3 (scatter_b 8) / 4 / 8 and
    // LLVM with BLADE_ORBIT_ACTIONS=0 (the identical-source knob-off A/B in that
    // lane, route count 0) -- every a<k> line equals the reference build's;
    // the interpreter (the oracle, the walk) must print the same DECIMAL text
    // (it has no bit channel). REPRO + contract=off, T = 1 (v1's action is
    // serial). Routes asserted per build.
    let actionProgram () : string * int =
        let sb = Text.StringBuilder(header)
        let mutable k = 0
        let aKernels = [| "(+)"; "radd"; "rhalf"; "rmax" |]
        let aSeeds = [| "0.0"; "-0.0"; "0.75"; "-1.25" |]
        // (action body over the read `r`, twin body over the read and its mirror `m`)
        let aBodies (p: int) : ((string -> string) * (string -> string -> string)) list =
            [ (fun r -> r), (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r}")
              (fun r -> $"{r} * v{p}(j)"), (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r} * v{p}(j)")
              (fun r -> $"{r} * (v{p}(j) - y{p}(i))"), (fun r m -> $"if {m} > 1.0e300 then 0.0 else {r} * (v{p}(j) - y{p}(i))")
              (fun r -> $"({r} - y{p}(i)) * v{p}(j) * v{p}(i)"), (fun r m -> $"if {m} > 1.0e300 then 0.0 else ({r} - y{p}(i)) * v{p}(j) * v{p}(i)") ]
        let add (p: int) (pool: string) (kern: string) (seed: string) (body: string) (twin: string) =
            let mk (b: string) = $"method_for(range<V{p}>) <@> lambda(i) -> reduce(method_for(range<V{p}>) <@> lambda(j) -> {b}, {kern}, {seed}) |> compute"
            sb.Append($"let a{k} = {mk body}\n") |> ignore
            sb.Append($"let b{k} = {mk twin}\n") |> ignore
            k <- k + 1
        let ps = [ 1; 2; 3; 5; 7; 13; 23; 37 ]
        for p in ps do
            sb.Append($"type V{p} = Idx<{p}>\n") |> ignore
            sb.Append(xDecl (string p) $"V{p}" None) |> ignore
            sb.Append($"let g{p} = method_for(x{p}, x{p}) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n") |> ignore
            sb.Append($"let v{p} = method_for(range<V{p}>) <@> lambda(i) -> Float64((i * 5) %% 7) * 0.31 - 0.83 |> compute\n") |> ignore
            sb.Append($"let y{p} = method_for(range<V{p}>) <@> lambda(i) -> Float64((i * 3) %% 5) * 0.47 + 0.125 / Float64(i + 1) |> compute\n") |> ignore
        for p in ps do
            aBodies p |> List.iteri (fun bi (f, twin) ->
                let kern = aKernels.[(bi + p) % aKernels.Length]
                let seed = aSeeds.[(2 * bi + p) % aSeeds.Length]
                add p $"g{p}" kern seed (f $"g{p}(i, j)") (twin $"g{p}(i, j)" $"g{p}(j, i)"))
        // the S(j, i) read order
        add 13 "g13" "rhalf" "0.75" "g13(j, i) * v13(j)" "if g13(i, j) > 1.0e300 then 0.0 else g13(j, i) * v13(j)"
        // a NaN pool (one payload: the pool's own invalid sqrt)
        sb.Append(xDecl "n7" "V7" (Some 3)) |> ignore
        sb.Append("let gn = method_for(xn7, xn7) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n") |> ignore
        add 7 "gn" "radd" "0.5" "gn(i, j) * v7(j)" "if gn(j, i) > 1.0e300 then 0.0 else gn(i, j) * v7(j)"
        // all -0.0 terms from -0.0 seeds: every row is -0.0
        add 13 "g13" "(+)" "-0.0" "-0.0 * abs(g13(i, j)) * abs(v13(j))" "if g13(j, i) > 1.0e300 then 0.0 else -0.0 * abs(g13(i, j)) * abs(v13(j))"
        // D/5's mixed-nest row: S = ones, V = [1e16, -1e16, 1]
        sb.Append("type Three = Idx<3>\n") |> ignore
        sb.Append("let ones = method_for(range<Three>) <@> lambda(i) -> 1.0 |> compute\n") |> ignore
        sb.Append("let s1 = method_for(ones, ones) <@> lambda(a: Float64, b: Float64) where comm(a, b) -> a * b |> compute\n") |> ignore
        sb.Append("let vmn = method_for(range<Three>) <@> lambda(i) -> if i == 0 then 1.0e16 else if i == 1 then -1.0e16 else 1.0 |> compute\n") |> ignore
        sb.Append("let amn = method_for(range<Three>) <@> lambda(i) -> reduce(method_for(range<Three>) <@> lambda(j) -> s1(i, j) * vmn(j), (+)) |> compute\n") |> ignore
        sb.Append("let wmn = reduce(method_for(range<Three>) <@> lambda(i) -> reduce(method_for(range<Three>) <@> lambda(j) -> s1(i, j) * vmn(j), (+)), radd, 0.0)\n") |> ignore
        // THE SEEDLESS all -0.0 witness (the G74 record's, Codex diff review of
        // 719a2bce finding 2). A seedless `(+)` row is the left fold of its
        // cells; the checker's synthesized seed is the EXACT identity -0.0
        // (gap G74, FIXED), so every row is -0.0 -- in the action, the
        // knob-off walk and the interpreter alike. It FLIPPED from +0.0 by
        // design: the checker used to seed a seedless deferred `(+)` with
        // +0.0. `anan` / `bnan`: every row's FIRST cell (j = 0, vn13(0) is a
        // NaN) is a NaN, whose payload must survive as the head.
        sb.Append("let aseedless = method_for(range<V13>) <@> lambda(i) -> reduce(method_for(range<V13>) <@> lambda(j) -> -0.0 * abs(g13(i, j)) * abs(v13(j)), (+)) |> compute\n") |> ignore
        sb.Append("let bseedless = method_for(range<V13>) <@> lambda(i) -> reduce(method_for(range<V13>) <@> lambda(j) -> if g13(j, i) > 1.0e300 then 0.0 else -0.0 * abs(g13(i, j)) * abs(v13(j)), (+)) |> compute\n") |> ignore
        sb.Append("let vn13 = method_for(range<V13>) <@> lambda(i) -> if i == 0 then sqrt(Float64(i) - 1.0) else Float64(i) * 0.25 - 1.5 |> compute\n") |> ignore
        sb.Append("let anan = method_for(range<V13>) <@> lambda(i) -> reduce(method_for(range<V13>) <@> lambda(j) -> g13(i, j) * vn13(j), (+)) |> compute\n") |> ignore
        sb.Append("let bnan = method_for(range<V13>) <@> lambda(i) -> reduce(method_for(range<V13>) <@> lambda(j) -> if g13(j, i) > 1.0e300 then 0.0 else g13(i, j) * vn13(j), (+)) |> compute\n") |> ignore
        sb.Append("let vnh = vn13(0)\n") |> ignore
        (sb.ToString(), k)
    let (asrc, acases) = actionProgram ()
    let aprinted = [ for k in 0 .. acases - 1 do yield $"a{k}"; yield $"b{k}" ] @ [ "amn"; "wmn"; "aseedless"; "bseedless"; "anan"; "bnan"; "vnh" ]
    let actionRoutes (lane: string) (text: string) =
        let needle = if lane = "cpp" then "blade_scatter::action2<" else "; orbit action (rank 2"
        Text.RegularExpressions.Regex.Matches(text, Text.RegularExpressions.Regex.Escape needle).Count
    // name -> its printed line (the decimal text, raw bits included when printed)
    let linesOf (out: string) : Map<string, string> =
        out.Replace("\r\n", "\n").Split('\n')
        |> Array.choose (fun l ->
            let m = Text.RegularExpressions.Regex.Match(l, @"^([A-Za-z_][A-Za-z_0-9]*) = (.*)$")
            if m.Success then Some (m.Groups.[1].Value, m.Groups.[2].Value) else None)
        |> Map.ofArray
    let stripBits (s: string) = Text.RegularExpressions.Regex.Replace(s, @" #[0-9a-f]{16}", "")
    let actionPins = [ ("BLADE_FP_REPRO", "1"); ("BLADE_FP_CONTRACT", "off"); ("BLADE_OMP_THREADS", "1"); ("BLADE_PRINT", String.concat "," aprinted)
                       ("BLADE_ORBIT_FOLD", null); ("BLADE_FOLD_ROW_SLOTS", null); ("BLADE_ORBIT_SCATTER", null) ]
    let buildAction (lane: string) (pins: (string * string) list) (stem: string) : Result<string * string * Blade.IR.IRProgram, string> =
        let guards = pins |> List.map (fun (k, v) -> pinEnv k v)
        try
            match (try lower asrc with ex -> Error ("lower raised: " + ex.Message)) with
            | Error e -> Error ("lower: " + e)
            | Ok ir ->
            if lane = "cpp" then
                let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir stem
                CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                let file = Path.Combine(work, stem + ".cpp")
                File.WriteAllText(file, cpp)
                match Build.compileCpp file work with
                | Error e -> Error ("compile: " + (if e.Length > 400 then e.Substring(0, 400) else e))
                | Ok exe -> Ok (exe, cpp, ir)
            else
                match (try EmitLlvm.tryEmitProgramNamed stem ir with ex -> Error ("raised: " + ex.Message)) with
                | Error why -> Error ($"the llvm lane refused the program: {why}")
                | Ok ll ->
                let llFile = Path.Combine(work, stem + ".ll")
                File.WriteAllText(llFile, ll)
                match Build.compileLlvmProgram llFile work with
                | Error e -> Error ("llvm compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
                | Ok exe -> Ok (exe, ll, ir)
        finally
            for g in List.rev guards do g.Dispose()
    // (lane, extra pins, label, expected action routes)
    let actionBuilds =
        [ ("cpp", [ ("BLADE_TILE_TUNE", null); ("BLADE_ORBIT_ACTIONS", null) ], "C++ action, default (scatter_g=4)", acases + 3)
          ("cpp", [ ("BLADE_TILE_TUNE", "scatter_g=1"); ("BLADE_ORBIT_ACTIONS", null) ], "C++ action, scatter_g=1", acases + 3)
          ("cpp", [ ("BLADE_TILE_TUNE", "scatter_g=3,scatter_b=8"); ("BLADE_ORBIT_ACTIONS", null) ], "C++ action, scatter_g=3, scatter_b=8", acases + 3)
          ("cpp", [ ("BLADE_TILE_TUNE", "scatter_g=8"); ("BLADE_ORBIT_ACTIONS", null) ], "C++ action, scatter_g=8", acases + 3)
          ("cpp", [ ("BLADE_TILE_TUNE", null); ("BLADE_ORBIT_ACTIONS", "0") ], "C++ BLADE_ORBIT_ACTIONS=0 (every a<k> the walk)", 0)
          ("llvm", [ ("BLADE_TILE_TUNE", null); ("BLADE_ORBIT_ACTIONS", null) ], "LLVM action, default (scatter_g=4)", acases + 3)
          ("llvm", [ ("BLADE_TILE_TUNE", "scatter_g=1"); ("BLADE_ORBIT_ACTIONS", null) ], "LLVM action, scatter_g=1", acases + 3)
          ("llvm", [ ("BLADE_TILE_TUNE", "scatter_g=3,scatter_b=8"); ("BLADE_ORBIT_ACTIONS", null) ], "LLVM action, scatter_g=3, scatter_b=8", acases + 3)
          ("llvm", [ ("BLADE_TILE_TUNE", "scatter_g=8"); ("BLADE_ORBIT_ACTIONS", null) ], "LLVM action, scatter_g=8", acases + 3)
          ("llvm", [ ("BLADE_TILE_TUNE", null); ("BLADE_ORBIT_ACTIONS", "0") ], "LLVM BLADE_ORBIT_ACTIONS=0 (every a<k> the walk)", 0) ]
    let mutable actionRef : Map<string, string> option = None
    let mutable actionIr : Blade.IR.IRProgram option = None
    let caseKeys = [ for k in 0 .. acases - 1 -> $"a{k}" ] @ [ "amn"; "aseedless"; "anan" ]
    for (lane, pins, label, routes) in actionBuilds do
        n <- n + 1
        let name = $"{label}: {acases} actions == their walk-forced twins and the reference (raw bits, REPRO, contract=off)"
        if lane = "llvm" && not llvmOn then skip name "no clang (BLADE_LLVM_CLANG)"
        else
        match buildAction lane (actionPins @ pins) $"oact_{n}" with
        | Error e -> fail name e
        | Ok (exe, text, ir) ->
        actionIr <- Some ir
        let rc = actionRoutes lane text
        if rc <> routes then fail name $"route census: {rc} action site(s) in the emitted text, wanted {routes}"
        elif lane = "llvm" && routes > 0 && isNull (snd (pins |> List.find (fun (k, _) -> k = "BLADE_TILE_TUNE"))) && not (text.Contains "fadd <4 x double>") then
            fail name "the LLVM row chains of a builtin `+` action did not take the <4 x double> form (G % 4 = 0)"
        else
        match runAt exe "1" None with
        | Error e -> fail name ("run: " + e)
        | Ok (out, _) ->
        let ls = linesOf out
        let missing = (caseKeys @ [ "wmn"; "bseedless"; "bnan"; "vnh" ]) |> List.filter (fun c -> not (ls.ContainsKey c))
        if not missing.IsEmpty then fail name $"""not printed: {String.concat ", " (List.truncate 4 missing)}"""
        else
        let twinDiff = [ for k in 0 .. acases - 1 do if ls.[$"a{k}"] <> ls.[$"b{k}"] then yield k ]
        // the seedless witness (G74, FIXED): -0.0 in every row, the left fold
        // of the cells (it was +0.0, the checker's former seed), equal to its
        // walk twin; the NaN head: every row the first cell's NaN pattern
        let seedlessOk =
            ls.["aseedless"] = ls.["bseedless"]
            && ls.["aseedless"] = "[" + String.concat ", " (List.replicate 13 "-0.0 #8000000000000000") + "]"
        let nanHeadOk =
            let head = ls.["vnh"]
            ls.["anan"] = ls.["bnan"] && head.StartsWith "nan #"
            && ls.["anan"] = "[" + String.concat ", " (List.replicate 13 head) + "]"
        let bitsShown = ls.[$"a0"].Contains " #"
        let amnOk = ls.["amn"] = "[1.0 #3ff0000000000000, 1.0 #3ff0000000000000, 1.0 #3ff0000000000000]"
        let wmnOk = ls.["wmn"] = "3.0 #4008000000000000"
        let nanOk = ls.[$"a{acases - 2}"].Contains "nan"
        let zeroOk =
            let z = ls.[$"a{acases - 1}"]
            z.StartsWith "[-0.0 #8000000000000000" && not (z.Contains "#0000000000000000")
        if not bitsShown then fail name "no raw bits printed (BLADE_PRINT_BITS)"
        elif not twinDiff.IsEmpty then
            let k = twinDiff.Head
            fail name ($"a{k} differs from its walk-forced twin b{k}: " + ls.[$"a{k}"] + " vs " + ls.[$"b{k}"])
        elif not seedlessOk then fail name ("the SEEDLESS all -0.0 witness must be -0.0 in every row (the left fold of the cells; G74) and equal its walk twin: aseedless = " + ls.["aseedless"] + " / bseedless = " + ls.["bseedless"])
        elif not nanHeadOk then fail name ("the SEEDLESS NaN-head rows must each carry the first cell's NaN (" + ls.["vnh"] + ") and equal their walk twin: anan = " + ls.["anan"] + " / bnan = " + ls.["bnan"])
        elif not amnOk then fail name ("the mixed-nest row action must be the ascending chain, every row exactly 1.0: amn = " + ls.["amn"])
        elif not wmnOk then fail name ("the weighted walk over the mixed-nest rows must be 3.0: wmn = " + ls.["wmn"])
        elif not nanOk then fail name "the NaN pool's action must print nan"
        elif not zeroOk then fail name ("the all -0.0 action must keep its sign in every row: " + ls.[$"a{acases - 1}"])
        else
        match actionRef with
        | None ->
            actionRef <- Some ls
            pass name $"every a<k> line byte-identical to its walk twin (raw bits); amn = three exact 1.0 (ascending chains); NaN and -0.0 rows right; {rc} action site(s)"
        | Some r ->
            let diffs = caseKeys |> List.filter (fun c -> r.[c] <> ls.[c])
            if diffs.IsEmpty then pass name $"every a<k> == its walk twin AND the reference build, raw bits; {rc} action site(s)"
            else
                let c = diffs.Head
                fail name $"raw bits differ from the reference at {diffs.Length} case(s): {c} = {ls.[c]} vs {r.[c]}"
    // the interpreter (the oracle): the same decimal text as the reference
    let interpName = $"interpreter (the walk): {acases} actions print the reference build's decimal text"
    match actionRef, actionIr with
    | Some r, Some ir ->
        let guards = actionPins |> List.map (fun (k, v) -> pinEnv k v)
        try
            match (try Ok (Blade.Interp.Run.runProgram ir "oact_interp" Blade.Interp.Value.defaultLimits) with ex -> Error ex.Message) with
            | Error e -> fail interpName ("interpreter raised: " + e)
            | Ok res when res.ExitCode <> 0 -> fail interpName ($"interpreter exit {res.ExitCode}: {res.Stderr.Trim()}")
            | Ok res ->
                let ls = linesOf res.Stdout
                let diffs = (caseKeys @ [ "wmn" ]) |> List.filter (fun c -> not (ls.ContainsKey c) || ls.[c] <> stripBits r.[c])
                if diffs.IsEmpty then pass interpName "every a<k>, amn and wmn line equal (decimal, 15 significant digits: the interpreter has no bit channel)"
                else
                    let c = diffs.Head
                    fail interpName $"""{diffs.Length} line(s) differ: {c} = {(match Map.tryFind c ls with Some v -> v | None -> "<missing>")} vs {stripBits r.[c]}"""
        finally
            for g in List.rev guards do g.Dispose()
    | _ -> skip interpName "no reference build"
    (try Directory.Delete(work, true) with _ -> ())
    printFooter blockName [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = names }
