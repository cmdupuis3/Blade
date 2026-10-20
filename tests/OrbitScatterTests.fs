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
    (try Directory.Delete(work, true) with _ -> ())
    printFooter blockName [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = names }
