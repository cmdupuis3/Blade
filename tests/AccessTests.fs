// Halo access-description pins (src/Types.fs `HaloAccess`, src/IRAccess.fs)
// and the reverse-mode halo ROUTE differential (docs/plans/structural/02,
// sections 2.1 and 3.3).
//
// The corpus proves VALUES under whichever route the gate selects, and the
// two routes -- the kept map with a gather adjoint, the lowered map with a
// scatter adjoint -- are the same function by construction. That is exactly
// what the corpus cannot see: one route silently taking over from the
// other, or the gather drifting from the scatter by an accumulation-order
// change that only shows in the last bit. So these pins (a) read the
// emitted C++ for the ROUTE (the gather's guarded loops exist only with
// the gate on, and the kept map adds one halo nest), and (b) run BOTH
// routes' executables on the same programs and compare their printed
// output byte-for-byte, the timing line excepted (it is the one line that
// is not a value). The emission and record pins need no toolchain; the
// differential skips without g++ (`Build.capabilities.HasGpp`).
module Blade.Tests.AccessTests

open System.IO
open Blade
open Blade.Types
open Blade.Lowering
open Blade.Tests.TestHarness

let private gateVar = "BLADE_AD_HALO_GATHER"

/// Run `f` with the route gate set to `value` (None = unset: the default,
/// gather), restoring whatever was there. The gate is read per call by
/// design (GradCommon.haloGatherEnabled), which is what makes this work.
let private withGate (value: string option) (f: unit -> 'a) : 'a =
    let prev = System.Environment.GetEnvironmentVariable gateVar
    System.Environment.SetEnvironmentVariable(gateVar, (match value with Some s -> s | None -> null))
    try f () finally System.Environment.SetEnvironmentVariable(gateVar, prev)

let private cppOf (name: string) (gate: string option) (src: string) : Result<string, string> =
    withGate gate (fun () ->
        try
            match lower src with
            | Error e -> Error $"lower: {e}"
            | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir name))
        with ex -> Error $"codegen raised: {ex.Message}")

/// The gather loop's window guard, `((0LL <= __vN) && (__vN < MLL))`:
/// generated-name-anchored, so user identifiers cannot match it.
let private gatherGuardCount (cpp: string) =
    System.Text.RegularExpressions.Regex.Matches(cpp, @"\(\(0LL <= __v\d+\) && \(__v\d+ < \d+LL\)\)").Count

/// Halo nests, counted by their window-centre binding `int64_t w = (__iN + kLL);`
/// (a dense window reads its source directly, so no carousel marks it).
let private haloNestCount (cpp: string) =
    System.Text.RegularExpressions.Regex.Matches(cpp, @"int64_t \w+ = \(__i\d+ \+ \d+LL\);").Count

let private corpusSource (file: string) : Result<string, string> =
    let path = Path.Combine("tests", "corpus", "ad", file)
    if File.Exists path then Ok (File.ReadAllText path)
    else Error $"missing {path} (run from the repo root)"

// ---------------------------------------------------------------------------
// The record.
// ---------------------------------------------------------------------------

/// `HaloAccess` over the one-sided lag set `[-2, -4]` (loops/078, ad/032):
/// the reach includes the centre, the interior starts at 4 and loses 4,
/// the demand over the whole interior is the inner extent, and the
/// transpose of input ordinal 5 is the two windows that read it.
let private recordPins () =
    let name = "halo_access_record_lag_set"
    let h = haloAccessOf "T" [ -2; -4 ] false
    let parsed = haloAccessOfTag "__halowin|d:T|-2,-4"
    let reach = haloReach h
    let demand = haloDemand h (0L, 4L)
    let contributors = haloContributors h 4L 5L
    let checks =
        [ "Start = 4", h.Start = 4L
          "Shrink = 4", h.Shrink = 4L
          "reach = [-4; -2; 0]", reach = [ -4; -2; 0 ]
          "offset 0 in reach", haloOffsetInReach h 0
          "offset -3 in reach", haloOffsetInReach h -3
          "offset 1 outside", not (haloOffsetInReach h 1)
          "offset -5 outside", not (haloOffsetInReach h -5)
          "demand [0,4) = [0,8)", demand = (0L, 8L)
          "contributors of 5 = [(-2,3); (0,1)]", contributors = [ (-2, 3L); (0, 1L) ]
          "tag parses to the same record", parsed = Some { h with Inner = "T" } ]
    match checks |> List.tryFind (fun (_, ok) -> not ok) with
    | None -> resultLine Pass name $"{checks.Length} record facts"; true
    | Some (what, _) -> resultLine Fail name $"failed: {what}"; false

// ---------------------------------------------------------------------------
// Route emission.
// ---------------------------------------------------------------------------

/// Gate on: the map is kept (one more halo nest than the scatter route --
/// the differentiated function replays it) and the adjoint is gather loops
/// with the window guard, one per offset the kernel reads. Gate off: no
/// guard anywhere; the map has lowered into a construction loop.
let private routeEmission () =
    let name = "halo_route_emission"
    match corpusSource "030_halo_stencil_grad_linear.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
        match cppOf (name + "_gather") None src, cppOf (name + "_scatter") (Some "0") src with
        | Error e, _ | _, Error e -> resultLine Fail name e; false
        | Ok on, Ok off ->
            let guardsOn = gatherGuardCount on
            let guardsOff = gatherGuardCount off
            let nestsOn = haloNestCount on
            let nestsOff = haloNestCount off
            if guardsOn = 2 && guardsOff = 0 && nestsOn = nestsOff + 1 then
                resultLine Pass name $"gather: 2 guarded loops, {nestsOn} halo nests; scatter: no guard, {nestsOff} halo nests"
                true
            else
                resultLine Fail name $"expected gather 2 guards / scatter 0, halo nests on = off + 1; got guards {guardsOn}/{guardsOff}, halo nests {nestsOn}/{nestsOff}"
                false

/// A computed offset is outside the gather's read discipline: with the gate
/// ON the map still takes the scatter route (no guard emitted).
let private computedOffsetDeclines () =
    let name = "halo_gather_declines_computed_offset"
    match corpusSource "033_halo_stencil_grad_computed_offset.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
        match cppOf name None src with
        | Error e -> resultLine Fail name e; false
        | Ok cpp ->
            let guards = gatherGuardCount cpp
            if guards = 0 then resultLine Pass name "no gather guard: the scatter route took it"; true
            else resultLine Fail name $"expected no gather guard, found {guards}"; false

// ---------------------------------------------------------------------------
// The differential.
// ---------------------------------------------------------------------------

let private outputDir = Path.Combine("generated_cpp_tests", "access")

/// Compile and run `src` under `gate`: the printed lines minus the timing
/// line. `Error "Skipped: ..."` when the toolchain is missing.
let private runUnder (name: string) (gate: string option) (src: string) : Result<string list, string> =
    match cppOf name gate src with
    | Error e -> Error e
    | Ok cpp ->
        Directory.CreateDirectory outputDir |> ignore
        // the runtime headers live beside every generated .cpp, as the CLI
        // deploys them before each compile
        CodeGen.deployRuntimeHeaders outputDir
        let cppFile = Path.Combine(outputDir, name + ".cpp")
        File.WriteAllText(cppFile, cpp)
        match Build.compileCpp cppFile outputDir with
        | Error e when e.StartsWith "Skipped:" -> Error e
        | Error e -> Error $"compile ({cppFile}): {e}"
        | Ok exe ->
            match Build.runExecutable exe with
            | Error e -> Error $"run: {e}"
            | Ok (code, out) when code <> 0 -> Error $"exit {code}: {out.Trim()}"
            | Ok (_, out) ->
                out.Split('\n')
                |> Array.map (fun l -> l.TrimEnd('\r'))
                |> Array.filter (fun l -> not (l.Contains " completed in ") && l <> "")
                |> List.ofArray
                |> Ok

/// The gather route's executable under AddressSanitizer (the memcheck
/// profile, clang64): a gather loop that reads one cell past a buffer can
/// still print the right numbers when the heap happens to be zero there --
/// the lag-set extent defect was exactly that, and only ASan saw it. Some
/// verdict, or None when the memcheck toolchain is not available.
let private memcheckGather (name: string) (gate: string option) (src: string) : bool option =
    match cppOf name gate src with
    | Error e -> resultLine Fail (name + "_asan") e; Some false
    | Ok cpp ->
        let cppFile = Path.Combine(outputDir, name + "_asan.cpp")
        File.WriteAllText(cppFile, cpp)
        match Build.compileCppMemcheck (Some src) [] cppFile outputDir with
        | Error e when e.StartsWith "Skipped:" -> resultLine Skip (name + "_asan") e; None
        | Error e ->
            // the same C++ just compiled under g++; an ASan-profile compile
            // failure is the sanitizer toolchain's, not a route disagreement
            let first = e.Split('\n').[0]
            resultLine Skip (name + "_asan") $"memcheck compile unavailable: {first}"
            None
        | Ok exe ->
            match Build.runExecutable exe with
            | Ok (0, _) -> resultLine Pass (name + "_asan") "gather route clean under AddressSanitizer"; Some true
            | Ok (code, out) ->
                let summary =
                    out.Split('\n') |> Array.tryFind (fun l -> l.Contains "SUMMARY:" || l.Contains "ERROR:")
                    |> Option.defaultValue $"exit {code}"
                resultLine Fail (name + "_asan") (summary.Trim()); Some false
            | Error e -> resultLine Fail (name + "_asan") e; Some false

/// Both routes on one program; Some pass/fail, None = skipped.
let private differential (name: string) (src: string) : bool option =
    match runUnder (name + "_gather") None src with
    | Error e when e.StartsWith "Skipped:" -> resultLine Skip name e; None
    | Error e -> resultLine Fail name e; Some false
    | Ok gather ->
        match runUnder (name + "_scatter") (Some "0") src with
        | Error e when e.StartsWith "Skipped:" -> resultLine Skip name e; None
        | Error e -> resultLine Fail name e; Some false
        | Ok scatter ->
            if gather = scatter && not gather.IsEmpty then
                resultLine Pass name $"{gather.Length} printed line(s) byte-identical under both routes"
                (match memcheckGather name None src with
                 | Some false -> Some false
                 | _ -> Some true)
            else
                let firstDiff =
                    Seq.zip (Seq.append gather (Seq.initInfinite (fun _ -> "<none>")))
                            (Seq.append scatter (Seq.initInfinite (fun _ -> "<none>")))
                    |> Seq.truncate (max gather.Length scatter.Length)
                    |> Seq.tryFind (fun (g, s) -> g <> s)
                let detail =
                    match firstDiff with
                    | Some (g, s) -> $"first difference: gather `{g}` vs scatter `{s}`"
                    | None -> $"line counts {gather.Length} vs {scatter.Length}"
                resultLine Fail name detail
                Some false

/// A five-point nonlinear kernel over a non-power-of-two extent whose
/// gradient is analytically ZERO on the interior for linear data -- what
/// prints there is pure roundoff, so agreement is agreement of the
/// accumulation ORDER, the property the descending-offset gather claims.
let private wideProgram =
    "import ad as ad\n"
    + "type H = Idx<127>\n"
    + "function f(a: Array<Float64 like H>) -> Float64 = {\n"
    + "    let d = method_for(halo<H, [-2, 0, 2]>) <@> lambda(w) -> a(w(2)) * a(w(-2)) - a(w(0)) * a(w(0)) |> compute\n"
    + "    reduce(d, (+))\n"
    + "}\n"
    + "let a0: Array<Float64 like H> = 1.0 + 0.013 * Float64(0..127)\n"
    + "let mut da: Array<Float64 like H> = 0.0 * Float64(0..127)\n"
    + "let fv = ad.grad(f)(a0, da)\n"
    + "let gsum = reduce(da, (+))\n"

/// A captured differentiable SCALAR alongside the windowed array: the
/// gather's scalar accumulator loop against the scatter's inline
/// accumulation, plus two windowed targets.
let private scalarCaptureProgram =
    "import ad as ad\n"
    + "type H = Idx<9>\n"
    + "function f(a: Array<Float64 like H>, s: Float64) -> Float64 = {\n"
    + "    let d = method_for(halo<H, [-1, 0, 1]>) <@> lambda(w) -> s * a(w(1)) * a(w(-1)) - a(w(0)) |> compute\n"
    + "    reduce(d, (+))\n"
    + "}\n"
    + "let a0: Array<Float64 like H> = 1.0 + 0.5 * Float64(0..9)\n"
    + "let mut da: Array<Float64 like H> = 0.0 * Float64(0..9)\n"
    + "let (fv, ds) = ad.grad(f)(a0, Float64(0.7), da)\n"
    + "let gsum = reduce(da, (+))\n"

/// Two DIFFERENT windowed arrays, both differentiable: two cotangent
/// buffers, one gather loop each.
let private twoArrayProgram =
    "import ad as ad\n"
    + "type H = Idx<11>\n"
    + "function f(a: Array<Float64 like H>, b: Array<Float64 like H>) -> Float64 = {\n"
    + "    let d = method_for(halo<H, [-1, 0, 1]>) <@> lambda(w) -> a(w(1)) * b(w(-1)) + exp(b(w(0))) |> compute\n"
    + "    reduce(d, (+))\n"
    + "}\n"
    + "let a0: Array<Float64 like H> = 1.0 + 0.1 * Float64(0..11)\n"
    + "let b0: Array<Float64 like H> = 0.5 - 0.07 * Float64(0..11)\n"
    + "let mut da: Array<Float64 like H> = 0.0 * Float64(0..11)\n"
    + "let mut db: Array<Float64 like H> = 0.0 * Float64(0..11)\n"
    + "let fv = ad.grad(f)(a0, b0, da, db)\n"

// ---------------------------------------------------------------------------
// Gap G68: the cotangent ZERO FILL of a combinator-built diff local (C6).
//
// Reverse mode gives a combinator-built local (a halo map, a gram) a
// cotangent buffer that starts at +0. It used to be an n-cell ARRAY LITERAL
// (`static const double __vN__init[n] = { 0.0, ... }` + `std::copy_n`),
// whose lowering was super-linear in n; it is now the zero FORMER
// `method_for(range<Idx<d1>, ..>) <@> lambda(..) -> 0.0 |> compute`
// (GradExpand.zeroFillOfDims). The value differentials cannot see the
// difference (both forms are +0 everywhere), so these pins read the FORM, at
// two non-power-of-two extents per program: the lowered `__grad` function
// holds a materializing zero map over an `IRRange` of the cotangent's
// extents and no `IRArrayLit`; the C++ has a counted fill loop and no
// `__init[` image; the LLVM lane ACCEPTS the program (a refusal fails the
// pin -- it must not fall back silently) and emits a counted loop storing
// +0.0; and both lanes' text is the same size at both extents (an n-cell
// form grows with n). The programs read only a few cells of the local, so
// most of its cotangent is never written after the fill: the value pins
// (C++ always when g++ is present, LLVM when clang is) see those untouched
// cells through the gradient's exact zeros. No timing is asserted.
// ---------------------------------------------------------------------------

/// Rank 1: a halo map local over `Idx<n>` (cotangent extent n - 2) whose
/// loss reads two cells, `2 d(1) + d(5)`: da = 2a(3), 2a(1), a(7), a(5) at
/// cells 1, 3, 5, 7 (a(i) = 1 + 0.25 i), exactly zero everywhere else.
let private zeroFillHaloProgram (n: int) =
    "import ad as ad\n"
    + $"type H = Idx<{n}>\n"
    + "function f(a: Array<Float64 like H>) -> Float64 = {\n"
    + "    let d = method_for(halo<H, [-1, 0, 1]>) <@> lambda(w) -> a(w(1)) * a(w(-1)) |> compute\n"
    + "    d(1) * 2.0 + d(5)\n"
    + "}\n"
    + $"let a0: Array<Float64 like H> = 1.0 + 0.25 * Float64(0..{n})\n"
    + $"let mut da: Array<Float64 like H> = 0.0 * Float64(0..{n})\n"
    + "let fv = ad.grad(f)(a0, da)\n"
    + "let gsum = reduce(da, (+))\n"
    + "let gsq = reduce(da * da, (+))\n"
    + $"let gzero = da(0) + da(2) + da(4) + da(6) + da(8) + da({n - 1})\n"

/// Rank 2 (a multi-slot range): a gram local over `Idx<n> x Idx<3>`
/// (cotangent extents n x n) whose loss reads g(0, 1) and g(1, 0): only rows
/// 0 and 1 of da / db are nonzero (da row 0 = 2 b(1, .), row 1 = b(0, .);
/// db row 1 = 2 a(0, .), row 0 = a(1, .)).
let private zeroFillGramProgram (n: int) =
    "import ad as ad\n"
    + $"type I = Idx<{n}>\n"
    + "type K = Idx<3>\n"
    + "function floss(a: Array<Float64 like I, K>, b: Array<Float64 like I, K>) -> Float64 = {\n"
    + "    let g = gram(a, b)\n"
    + "    g(0, 1) * 2.0 + g(1, 0)\n"
    + "}\n"
    + "let a0 = method_for(range<I, K>) <@> lambda(i, k) -> 1.0 + Float64(i) + 0.5 * Float64(k) |> compute\n"
    + "let b0 = method_for(range<I, K>) <@> lambda(i, k) -> Float64(k) - 0.25 * Float64(i) |> compute\n"
    + "let mut da = method_for(range<I, K>) <@> lambda(i, k) -> Float64(0.0) |> compute\n"
    + "let mut db = method_for(range<I, K>) <@> lambda(i, k) -> Float64(0.0) |> compute\n"
    + "let gv = ad.grad(floss)(a0, b0, da, db)\n"
    + "let dasum = reduce(reduce(da, (+)), (+))\n"
    + "let dbsum = reduce(reduce(db, (+)), (+))\n"
    + $"let gzero = reduce(da(2), (+)) + reduce(db(2), (+)) + reduce(da({n - 1}), (+)) + reduce(db({n - 1}), (+))\n"

type private ZeroFillCase =
    { Label: string
      Extents: int list
      Program: int -> string
      /// the cotangent's extents at program size n
      CotDims: int -> int list
      Expected: (string * string) list }

let private zeroFillCases =
    [ { Label = "halo_rank1"; Extents = [ 37; 1013 ]; Program = zeroFillHaloProgram
        CotDims = (fun n -> [ n - 2 ])
        Expected = [ "fv", "10.5625"; "gsum", "11.0"; "gsq", "31.125"; "gzero", "0.0" ] }
      { Label = "gram_rank2"; Extents = [ 7; 23 ]; Program = zeroFillGramProgram
        CotDims = (fun n -> [ n; n ])
        Expected = [ "gv", "17.25"; "dasum", "7.5"; "dbsum", "16.5"; "gzero", "0.0" ] } ]

/// IR: the `__grad` callable holds an `IRCompute` over an `IRApplyCombinator`
/// whose subtree has an `IRRange` with exactly the cotangent's literal
/// extents, and no `IRArrayLit` at all (its inputs are parameters, so a
/// literal there could only be a zero buffer).
let private zeroFillIrFacts (ir: IR.IRProgram) (dims: int list) : Result<unit, string> =
    let gradFns =
        ir.Modules |> List.collect (fun m -> m.Functions) |> List.filter (fun f -> f.Name.EndsWith "__grad")
    match gradFns with
    | [] -> Error "no __grad callable in the lowered program"
    | fns ->
        let mutable lits = 0
        let mutable zeroMaps = 0
        let wantExt = dims |> List.map (fun d -> IR.IRLit (IR.IRLitInt (int64 d)))
        for f in fns do
            f.Body |> IR.iterIRExpr (fun e ->
                match e with
                | IR.IRArrayLit _ -> lits <- lits + 1
                | IR.IRCompute (IR.IRApplyCombinator _ as ac) ->
                    let mutable hit = false
                    ac |> IR.iterIRExpr (fun x ->
                        match x with
                        | IR.IRRange (ixs, _) when (ixs |> List.map (fun ix -> ix.Extent)) = wantExt -> hit <- true
                        | _ -> ())
                    if hit then zeroMaps <- zeroMaps + 1
                | _ -> ())
        if lits > 0 then Error $"{lits} IRArrayLit node(s) in the __grad callable (the n-cell literal form)"
        elif zeroMaps = 0 then Error $"no IRCompute(IRApplyCombinator ..) over an IRRange of extents {dims}"
        else Ok ()

/// C++: one counted loop per cotangent axis, each binding the zero lambda's
/// parameter, the innermost storing 0.0; and no static initializer image.
let private zeroFillCppFacts (cpp: string) (dims: int list) : Result<unit, string> =
    let rx (p: string) = System.Text.RegularExpressions.Regex.IsMatch(cpp, p)
    let r = dims.Length
    let axisOk =
        dims |> List.mapi (fun k d -> rx $@"__i{k} < {d}; __i{k}\+\+\) \{{\s*int64_t __zf\d+ = __i{k};")
    if cpp.Contains "__init[" then Error "a static `__init[` initializer image is emitted"
    elif axisOk |> List.exists not then Error $"no counted fill loop over every cotangent axis {dims}"
    elif not (rx $@"int64_t __zf\d+ = __i{r - 1};\s*[\w\[\]]+ = 0\.0;") then Error "the innermost fill loop does not store 0.0"
    else Ok ()

/// LLVM: a counted loop bound for every cotangent axis and a +0.0 store.
let private zeroFillLlvmFacts (ll: string) (dims: int list) : Result<unit, string> =
    let rx (p: string) = System.Text.RegularExpressions.Regex.IsMatch(ll, p)
    if dims |> List.exists (fun d -> not (rx $@"icmp slt i64 %%r\d+, {d}\b")) then Error $"no counted loop bound for every cotangent axis {dims}"
    elif not (rx @"store double 0x0000000000000000, ptr %r\d+") then Error "no +0.0 store"
    elif rx @"\[\d+ x double\]" then Error "an `[N x double]` aggregate is emitted"
    else Ok ()

let private zeroStoreCount (ll: string) =
    System.Text.RegularExpressions.Regex.Matches(ll, @"store double 0x0000000000000000").Count

/// The printed `name = value` lines for the expected names.
let private pickValues (names: string list) (lines: string list) =
    names |> List.map (fun nm ->
        nm, (lines |> List.tryPick (fun l -> if l.StartsWith (nm + " = ") then Some (l.Substring(nm.Length + 3).Trim()) else None)))

let private checkValues (lane: string) (expected: (string * string) list) (lines: string list) : Result<unit, string> =
    let got = pickValues (expected |> List.map fst) lines
    match List.zip expected got |> List.tryFind (fun ((_, want), (_, g)) -> g <> Some want) with
    | None -> Ok ()
    | Some ((nm, want), (_, g)) -> Error $"""{lane}: {nm} = {defaultArg g "<not printed>"}, expected {want}"""

/// One case: the form pins at both extents (no toolchain), then the value
/// pins in the C++ lane (g++) and the LLVM lane (clang). Some pass/fail per
/// executed check; None = skipped for a missing toolchain.
let private zeroFillCase (c: ZeroFillCase) : bool option list =
    let name = $"g68_zero_fill_{c.Label}"
    let forms =
        c.Extents |> List.map (fun n ->
            let src = c.Program n
            let dims = c.CotDims n
            match (try lower src with ex -> Error ex.Message) with
            | Error e -> Error $"n = {n}: lower: {e}"
            | Ok ir ->
            match zeroFillIrFacts ir dims with
            | Error e -> Error $"n = {n}: IR: {e}"
            | Ok () ->
            match (try Ok (fst (CodeGen.genSelfContainedProgramFromIR ir $"{name}_{n}")) with ex -> Error ex.Message) with
            | Error e -> Error $"n = {n}: codegen: {e}"
            | Ok cpp ->
            match zeroFillCppFacts cpp dims with
            | Error e -> Error $"n = {n}: C++: {e}"
            | Ok () ->
            match (try EmitLlvm.tryEmitProgramNamed $"{name}_{n}" ir with ex -> Error ("raised: " + ex.Message)) with
            | Error why -> Error $"n = {n}: the LLVM lane refused the program (it must take it): {why}"
            | Ok ll ->
            match zeroFillLlvmFacts ll dims with
            | Error e -> Error $"n = {n}: LLVM: {e}"
            | Ok () -> Ok (n, src, cpp, ll))
    match forms |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
    | Some e -> resultLine Fail (name + "_form") e; [ Some false ]
    | None ->
    let ok = forms |> List.choose (function Ok x -> Some x | Error _ -> None)
    let (n1, _, cpp1, ll1), (n2, _, cpp2, ll2) = ok.[0], ok.[1]
    let llLines (s: string) = s.Split('\n').Length
    // the extents change only numerals (and a small extent may unroll a
    // little differently), so the growth is a few digits per occurrence,
    // never the cotangent's cell count
    let bounded =
        cpp2.Length - cpp1.Length < 400
        && llLines ll1 = llLines ll2
        && zeroStoreCount ll1 = zeroStoreCount ll2
    let formResult =
        if bounded then
            resultLine Pass (name + "_form")
                $"n = {n1}, {n2}: zero map over IRRange{c.CotDims n2}, no IRArrayLit; C++ counted fill, no __init image ({cpp1.Length} vs {cpp2.Length} chars); LLVM taken, counted +0.0 fill ({llLines ll2} lines at both extents)"
            Some true
        else
            resultLine Fail (name + "_form") $"emitted size grows with the extent: C++ {cpp1.Length} -> {cpp2.Length} chars, LLVM {llLines ll1} -> {llLines ll2} lines, +0.0 stores {zeroStoreCount ll1} -> {zeroStoreCount ll2}"
            Some false
    let valueRuns =
        ok |> List.collect (fun (n, src, _, ll) ->
            let cppRun =
                if not Build.capabilities.Value.HasGpp then
                    resultLine Skip $"{name}_values_cpp_{n}" "g++ not available"; None
                else
                    match runUnder $"{name}_cpp_{n}" None src with
                    | Error e when e.StartsWith "Skipped:" -> resultLine Skip $"{name}_values_cpp_{n}" e; None
                    | Error e -> resultLine Fail $"{name}_values_cpp_{n}" e; Some false
                    | Ok lines ->
                        match checkValues "C++" c.Expected lines with
                        | Ok () -> resultLine Pass $"{name}_values_cpp_{n}" "pins hold, untouched cells exactly 0.0"; Some true
                        | Error e -> resultLine Fail $"{name}_values_cpp_{n}" e; Some false
            let llRun =
                match Build.resolveClang () with
                | None -> resultLine Skip $"{name}_values_llvm_{n}" "no clang (BLADE_LLVM_CLANG)"; None
                | Some _ ->
                    Directory.CreateDirectory outputDir |> ignore
                    CodeGen.deployRuntimeHeaders outputDir
                    EmitLlvm.deployShim outputDir
                    let llFile = Path.Combine(outputDir, $"{name}_ll_{n}.ll")
                    File.WriteAllText(llFile, ll)
                    match Build.compileLlvmProgram llFile outputDir with
                    | Error e when e.StartsWith "Skipped:" -> resultLine Skip $"{name}_values_llvm_{n}" e; None
                    | Error e -> resultLine Fail $"{name}_values_llvm_{n}" ("llvm compile: " + (if e.Length > 300 then e.Substring(0, 300) else e)); Some false
                    | Ok exe ->
                        match Build.runExecutable exe with
                        | Error e -> resultLine Fail $"{name}_values_llvm_{n}" e; Some false
                        | Ok (code, out) when code <> 0 -> resultLine Fail $"{name}_values_llvm_{n}" $"exit {code}: {out.Trim()}"; Some false
                        | Ok (_, out) ->
                            let lines = out.Split('\n') |> Array.map (fun l -> l.TrimEnd('\r')) |> List.ofArray
                            match checkValues "LLVM" c.Expected lines with
                            | Ok () -> resultLine Pass $"{name}_values_llvm_{n}" "pins hold, untouched cells exactly 0.0"; Some true
                            | Error e -> resultLine Fail $"{name}_values_llvm_{n}" e; Some false
            [ cppRun; llRun ])
    formResult :: valueRuns

// ---------------------------------------------------------------------------
// Gap G78: the LLVM lane's pool ownership across a YOUNGER loop scope.
//
// A pool a function allocates -- a combinator-built local, its reverse-mode
// tape and cotangent -- is read by loop scopes opened after it (the adjoint
// sweep's loops, a `let rec`'s trips). Such a read used to null the pool's
// tracking slot in EVERY open scope, the owning function scope included, so
// the function's exit `blade_free` loaded null and the pool leaked once per
// call (haloadjoint's `loss__grad`: the tape d and its cotangent at full
// size). Now only the younger scopes give the pool up; the owner keeps it, or
// receives a pool a younger scope allocated through a fresh slot of its own
// (`; G78: handed`). These pins read the emitted .ll of each named function:
// every pool it allocates is FREED through a slot that holds it, and no such
// slot is nulled after the allocation except as an explicit hand-off. No
// toolchain is needed (emission only); the values are the corpus's.
// ---------------------------------------------------------------------------

/// The body of every `define` whose symbol starts with `@<prefix>` (the
/// lane's `@bl_<name>_<id>`), each as its line array.
let private llvmFunctionBodies (ll: string) (prefix: string) : string[] list =
    let lines = ll.Replace("\r\n", "\n").Split('\n')
    [ for i in 0 .. lines.Length - 1 do
        if lines.[i].StartsWith "define " && lines.[i].Contains("@" + prefix) then
            let stop = (lines |> Array.skip i |> Array.findIndex (fun l -> l = "}")) + i
            yield lines.[i .. stop] ]

/// Ok (pools checked) when every pool the body allocates is freed through a
/// slot holding it and no slot holding it is nulled after its allocation
/// except where the next line hands it to another (freed) slot.
let private llvmPoolsFreed (body: string[]) : Result<int, string> =
    let rx (p: string) (l: string) = System.Text.RegularExpressions.Regex.Match(l, p)
    let allocs =
        body |> Array.mapi (fun i l -> (i, rx @"^\s*(%r\d+) = call ptr @blade_alloc_cells" l))
        |> Array.filter (fun (_, m) -> m.Success) |> Array.map (fun (i, m) -> (i, m.Groups.[1].Value))
    let freedSlot (slot: string) =
        body |> Array.mapi (fun i l -> (i, rx (@"^\s*(%r\d+) = load ptr, ptr " + System.Text.RegularExpressions.Regex.Escape slot + "$") l))
        |> Array.exists (fun (i, m) ->
            m.Success && i + 1 < body.Length && body.[i + 1].Trim() = $"call void @blade_free(ptr {m.Groups.[1].Value})")
    let check (at: int, pool: string) : string option =
        let after = body.[at + 1 ..]
        let slots =
            after |> Array.choose (fun l ->
                let m = rx (@"^\s*store ptr " + System.Text.RegularExpressions.Regex.Escape pool + @", ptr (%r\d+)") l
                if m.Success then Some m.Groups.[1].Value else None)
            |> Array.distinct
        if slots.Length = 0 then Some $"{pool} is never stored to a tracking slot"
        else
            let nulledBad =
                slots |> Array.tryPick (fun s ->
                    after |> Array.mapi (fun j l -> (j, l))
                    |> Array.tryPick (fun (j, l) ->
                        if l.Trim() = $"store ptr null, ptr {s}" then
                            let handed = j + 1 < after.Length && after.[j + 1].Contains $"store ptr {pool}, ptr " && after.[j + 1].Contains "G78: handed"
                            if handed then None else Some $"the slot {s} of {pool} is nulled after its allocation (the pool escapes its owner)"
                        else None))
            match nulledBad with
            | Some why -> Some why
            | None when slots |> Array.exists freedSlot -> None
            | None -> Some $"{pool} is never freed through its slot"
    match allocs |> Array.tryPick check with
    | Some why -> Error why
    | None -> Ok allocs.Length

let private g78Case (name: string) (src: Result<string, string>) (fns: string list) : bool =
    match src with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    match (try lower src with ex -> Error ex.Message) with
    | Error e -> resultLine Fail name $"lower: {e}"; false
    | Ok ir ->
    match (try EmitLlvm.tryEmitProgramNamed name ir with ex -> Error ("raised: " + ex.Message)) with
    | Error why -> resultLine Fail name $"the LLVM lane refused the program (it must take it): {why}"; false
    | Ok ll ->
        let results =
            fns |> List.map (fun fn ->
                match llvmFunctionBodies ll fn with
                | [] -> fn, Error "no such function in the emitted .ll"
                | bodies ->
                    bodies |> List.fold (fun acc b ->
                        match acc, llvmPoolsFreed b with
                        | Error e, _ | _, Error e -> Error e
                        | Ok n, Ok m -> Ok (n + m)) (Ok 0)
                    |> fun r -> fn, r)
        match results |> List.tryPick (fun (fn, r) -> match r with Error e -> Some $"@{fn}: {e}" | Ok 0 -> Some $"@{fn}: no pool allocated (the witness lost its shape)" | Ok _ -> None) with
        | Some why -> resultLine Fail name why; false
        | None ->
            let counts = results |> List.map (fun (fn, r) -> match r with Ok n -> $"@{fn} {n}" | Error _ -> "") |> String.concat ", "
            resultLine Pass name $"every pool freed by its owning scope, no escape-by-null ({counts} pools)"
            true

let private g78Pins () : bool list =
    // read relative to the CURRENT DIRECTORY (like corpusSource): run the
    // block from the repo root, or from a directory that links `bench`
    let benchFile = Path.Combine("bench", "kernels", "haloadjoint", "haloadjoint.blade")
    let bench = if File.Exists benchFile then Ok (File.ReadAllText benchFile) else Error $"missing {benchFile} (run from the repo root)"
    [ g78Case "g78_llvm_frees_halo_grad_linear" (corpusSource "030_halo_stencil_grad_linear.blade") [ "bl_f__grad" ]
      g78Case "g78_llvm_frees_grad_and_let_rec_reader" (corpusSource "044_grad_combinator_local_read_by_loop_scope.blade") [ "bl_f__grad"; "bl_g_" ]
      // the saved full-size shape (n = 1,000,003): the tape d and its cotangent
      g78Case "g78_llvm_frees_haloadjoint_full_size" bench [ "bl_loss__grad" ] ]

let runAccessTests () =
    printHeader "Blade-DSL: Halo Access Tests"
    let pins =
        [ recordPins ()
          routeEmission ()
          computedOffsetDeclines () ]
        @ g78Pins ()
    let zeroFill = zeroFillCases |> List.collect zeroFillCase
    let diffs =
        if not Build.capabilities.Value.HasGpp then
            resultLine Skip "halo_route_differential" "g++ not available"
            [ None ]
        else
            let fromCorpus (name: string) (file: string) =
                match corpusSource file with
                | Error e -> resultLine Fail name e; Some false
                | Ok src -> differential name src
            [ fromCorpus "halo_route_differential_linear" "030_halo_stencil_grad_linear.blade"
              fromCorpus "halo_route_differential_nonlinear" "031_halo_stencil_grad_nonlinear.blade"
              fromCorpus "halo_route_differential_lag_set" "032_halo_stencil_grad_lag_set.blade"
              differential "halo_route_differential_wide_127" wideProgram
              differential "halo_route_differential_scalar_capture" scalarCaptureProgram
              differential "halo_route_differential_two_arrays" twoArrayProgram ]
    let diffs = diffs @ zeroFill
    let passed = (pins |> List.filter id |> List.length) + (diffs |> List.filter (fun d -> d = Some true) |> List.length)
    let failed = (pins |> List.filter not |> List.length) + (diffs |> List.filter (fun d -> d = Some false) |> List.length)
    let skipped = diffs |> List.filter Option.isNone |> List.length
    printFooter "Halo Access" [$"{passed} passed"; $"{failed} failed"; $"{skipped} skipped"]
    { Block = "Halo Access"; Passed = passed; Failed = failed; Skipped = skipped
      FailedNames = if failed = 0 then [] else ["see above"] }
