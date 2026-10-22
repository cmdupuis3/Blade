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
/// with per-level kernels and seeds.
let private nest (v: string) (vars: string list) (ks: string list) (ss: string list) (body: string) =
    let rec go (lvl: int) =
        let var = vars.[lvl]
        let inner = if lvl = vars.Length - 1 then body else go (lvl + 1)
        $"reduce(method_for(range<{v}>) <@> lambda({var}) -> {inner}, {ks.[lvl]}, {ss.[lvl]})"
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
          yield! [ "zn2"; "zn3"; "zz2"; "zz3" ] ]
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
        (sb.ToString(), k, List.ofSeq exts, nanAt, zeroAt)
    let (psrc, pcases, pexts, nanAt, zeroAt) = pullProgram ()
    let pprinted = [ for k in 0 .. pcases - 1 do yield $"a{k}"; yield $"e{k}" ] @ [ "pn"; "pz" ]
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
    // (c)-(e) the other C++ forms, (f) the LLVM lane
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
    // ---- THE ROW ORBIT ACTION (round 3, D-P2 / gap G73) ---------------------
    // A generated program of rank-2 ROW ACTIONS `a<k> = method_for(range<V>)
    // <@> lambda(i) -> reduce(method_for(range<V>) <@> lambda(j) -> BODY, K, S)
    // |> compute` over stored SymIdx<2, p> pools at p in {1, 2, 3, 5, 7, 13, 23,
    // 37}, bodies x / x * v(j) / x * (v(j) - y(i)) / (x - y(i)) * v(j) * v(i)
    // (own and partner roles of one weight), kernels cycling (+) / radd /
    // rhalf / rmax, seeds cycling 0.0 / -0.0 / 0.75 / -1.25; plus a NaN pool,
    // an all -0.0 action with an EXPLICIT -0.0 seed (explicit-seed
    // preservation, raw bits), the SEEDLESS all -0.0 witness `aseedless` (the
    // G74 record's witness: the checker seeds a seedless deferred `(+)` with
    // +0.0, so the CURRENT behaviour -- +0.0 in every row -- is pinned equal
    // across the action, the walk and the interpreter; seedless first-term
    // semantics are NOT established here and G74 is not closed) and D/5's
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
        // 719a2bce finding 2): the checker seeds a seedless deferred `(+)` with
        // +0.0, so every row is +0.0 -- in the action, the knob-off walk and the
        // interpreter alike. Pinned as the CURRENT behaviour; it does not
        // establish first-term / -0.0 seedless semantics (G74 stays open).
        sb.Append("let aseedless = method_for(range<V13>) <@> lambda(i) -> reduce(method_for(range<V13>) <@> lambda(j) -> -0.0 * abs(g13(i, j)) * abs(v13(j)), (+)) |> compute\n") |> ignore
        sb.Append("let bseedless = method_for(range<V13>) <@> lambda(i) -> reduce(method_for(range<V13>) <@> lambda(j) -> if g13(j, i) > 1.0e300 then 0.0 else -0.0 * abs(g13(i, j)) * abs(v13(j)), (+)) |> compute\n") |> ignore
        (sb.ToString(), k)
    let (asrc, acases) = actionProgram ()
    let aprinted = [ for k in 0 .. acases - 1 do yield $"a{k}"; yield $"b{k}" ] @ [ "amn"; "wmn"; "aseedless"; "bseedless" ]
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
        [ ("cpp", [ ("BLADE_TILE_TUNE", null); ("BLADE_ORBIT_ACTIONS", null) ], "C++ action, default (scatter_g=4)", acases + 2)
          ("cpp", [ ("BLADE_TILE_TUNE", "scatter_g=1"); ("BLADE_ORBIT_ACTIONS", null) ], "C++ action, scatter_g=1", acases + 2)
          ("cpp", [ ("BLADE_TILE_TUNE", "scatter_g=3,scatter_b=8"); ("BLADE_ORBIT_ACTIONS", null) ], "C++ action, scatter_g=3, scatter_b=8", acases + 2)
          ("cpp", [ ("BLADE_TILE_TUNE", "scatter_g=8"); ("BLADE_ORBIT_ACTIONS", null) ], "C++ action, scatter_g=8", acases + 2)
          ("cpp", [ ("BLADE_TILE_TUNE", null); ("BLADE_ORBIT_ACTIONS", "0") ], "C++ BLADE_ORBIT_ACTIONS=0 (every a<k> the walk)", 0)
          ("llvm", [ ("BLADE_TILE_TUNE", null); ("BLADE_ORBIT_ACTIONS", null) ], "LLVM action, default (scatter_g=4)", acases + 2)
          ("llvm", [ ("BLADE_TILE_TUNE", "scatter_g=1"); ("BLADE_ORBIT_ACTIONS", null) ], "LLVM action, scatter_g=1", acases + 2)
          ("llvm", [ ("BLADE_TILE_TUNE", "scatter_g=3,scatter_b=8"); ("BLADE_ORBIT_ACTIONS", null) ], "LLVM action, scatter_g=3, scatter_b=8", acases + 2)
          ("llvm", [ ("BLADE_TILE_TUNE", "scatter_g=8"); ("BLADE_ORBIT_ACTIONS", null) ], "LLVM action, scatter_g=8", acases + 2)
          ("llvm", [ ("BLADE_TILE_TUNE", null); ("BLADE_ORBIT_ACTIONS", "0") ], "LLVM BLADE_ORBIT_ACTIONS=0 (every a<k> the walk)", 0) ]
    let mutable actionRef : Map<string, string> option = None
    let mutable actionIr : Blade.IR.IRProgram option = None
    let caseKeys = [ for k in 0 .. acases - 1 -> $"a{k}" ] @ [ "amn"; "aseedless" ]
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
        let missing = (caseKeys @ [ "wmn"; "bseedless" ]) |> List.filter (fun c -> not (ls.ContainsKey c))
        if not missing.IsEmpty then fail name $"""not printed: {String.concat ", " (List.truncate 4 missing)}"""
        else
        let twinDiff = [ for k in 0 .. acases - 1 do if ls.[$"a{k}"] <> ls.[$"b{k}"] then yield k ]
        // the seedless witness: the CURRENT behaviour (+0.0 in every row, the checker's seed), equal to its walk twin
        let seedlessOk =
            ls.["aseedless"] = ls.["bseedless"]
            && ls.["aseedless"] = "[" + String.concat ", " (List.replicate 13 "0.0 #0000000000000000") + "]"
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
        elif not seedlessOk then fail name ("the SEEDLESS all -0.0 witness must be +0.0 in every row (the checker's +0.0 seed; G74) and equal its walk twin: aseedless = " + ls.["aseedless"] + " / bseedless = " + ls.["bseedless"])
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
