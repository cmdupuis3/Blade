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
/// ON the map still takes the scatter route -- no gather guard beyond the
/// gate-OFF emission's. (A window read at a COMPUTED offset is a position,
/// checked against its slot since G192 (1): those BL8006 guards are in both
/// emissions alike.)
let private computedOffsetDeclines () =
    let name = "halo_gather_declines_computed_offset"
    match corpusSource "033_halo_stencil_grad_computed_offset.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
        match cppOf name None src, cppOf (name + "_off") (Some "0") src with
        | Error e, _ | _, Error e -> resultLine Fail name e; false
        | Ok cpp, Ok off ->
            let guards = gatherGuardCount cpp
            let guardsOff = gatherGuardCount off
            if guards = guardsOff then resultLine Pass name $"no gather guard: the scatter route took it ({guards} subscript guards in both)"; true
            else resultLine Fail name $"expected the gate-off guard count {guardsOff}, found {guards}"; false

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

// ---------------------------------------------------------------------------
// PM40: the CHECKPOINTED recurrence reverse sweep (round 4 wave 4 J1; rule
// `ad-checkpoint v1`, knob BLADE_AD_CHECKPOINT). The checkpointed gradient
// must equal the full-tape gradient BIT FOR BIT in the reproducible build
// (BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off, raw bits through
// BLADE_PRINT_BITS) at every checkpoint interval, segment edges included.
// ---------------------------------------------------------------------------

let private withEnvs (pairs: (string * string option) list) (f: unit -> 'a) : 'a =
    let prev = pairs |> List.map (fun (k, _) -> (k, System.Environment.GetEnvironmentVariable k))
    for (k, value) in pairs do System.Environment.SetEnvironmentVariable(k, Option.toObj value)
    try f () finally for (k, value) in prev do System.Environment.SetEnvironmentVariable(k, value)

/// Scalar parameters only (no gradient buffers): the logistic map at r = 3.7
/// over 23 rows, the loss reading the seed, checkpoint, interior and last rows.
let private ckLogistic = """import ad as ad
function f(x: Float64, r: Float64) -> Float64 = {
    let rec s: Array<Float64 like Idx<23>> =
        match s with
        | zero -> zero
        | zero :: n -> zero :: x
        | prefix :: n -> prefix :: r * prefix(n - 1) * (1.0 - prefix(n - 1))
    s(22) + 0.5 * s(7) * s(7) + s(9) - s(0) * s(13)
}
let (gv, gx, gr) = ad.grad(f)(Float64(0.3), Float64(3.7))
"""

/// A step reading the step ordinal and an immutable module array (no
/// gradient buffers: nothing is written during the reverse sweep, so the
/// replay is admitted).
let private ckForcing = """import ad as ad
let xs = 0.25 + 0.125 * Float64(0..31)
function g(x: Float64, a: Float64) -> Float64 = {
    let rec s: Array<Float64 like Idx<31>> =
        match s with
        | zero -> zero
        | zero :: n -> zero :: x
        | prefix :: n -> prefix :: a * sin(prefix(n - 1)) + xs(n - 1) * Float64(n) * 0.01
    s(30) * s(11) + s(1) * 0.75 + 1.5 * s(30)
}
let (gv, gx, ga) = ad.grad(g)(Float64(0.4), Float64(0.9))
"""

/// The same step beside an array parameter OUTSIDE it (gradient buffers
/// exist): the module ARRAY `xs` read by the step (a total arithmetic form)
/// is SNAPSHOTTED before the forward loop and the replay reads the copy
/// (round 4 wave 6, gap G162; it declined in wave 5); a module scalar
/// constant is read directly.
let private ckForcingBuffers = """import ad as ad
let xs = 0.25 + 0.125 * Float64(0..31)
let c0: Float64 = 0.01
function g(x: Float64, a: Float64, w: Array<Float64 like Idx<3>>) -> Float64 = {
    let rec s: Array<Float64 like Idx<31>> =
        match s with
        | zero -> zero
        | zero :: n -> zero :: x
        | prefix :: n -> prefix :: a * sin(prefix(n - 1)) + xs(n - 1) * Float64(n) * c0
    s(30) * s(11) + s(1) * w(2) + w(0) * s(30)
}
let w0: Array<Float64 like Idx<3>> = [1.5, 2.0, 0.75]
let mut dw: Array<Float64 like Idx<3>> = [0.0, 0.0, 0.0]
let (gv, gx, ga) = ad.grad(g)(Float64(0.4), Float64(0.9), w0, dw)
let dw0 = dw(0)
let dw2 = dw(2)
"""

/// A step reading only scalars and a module scalar constant, beside an array
/// parameter (gradient buffers exist): admitted.
let private ckScalarBuffers = """import ad as ad
let c0: Float64 = 0.01 * 2.0
function g(x: Float64, a: Float64, w: Array<Float64 like Idx<3>>) -> Float64 = {
    let rec s: Array<Float64 like Idx<31>> =
        match s with
        | zero -> zero
        | zero :: n -> zero :: x
        | prefix :: n -> prefix :: a * sin(prefix(n - 1)) + Float64(n) * c0
    s(30) * s(11) + s(1) * w(2) + w(0) * s(30)
}
let w0: Array<Float64 like Idx<3>> = [1.5, 2.0, 0.75]
let mut dw: Array<Float64 like Idx<3>> = [0.0, 0.0, 0.0]
let (gv, gx, ga) = ad.grad(g)(Float64(0.4), Float64(0.9), w0, dw)
let dw0 = dw(0)
let dw2 = dw(2)
"""

/// The recurrence the snapshot witnesses share (`forcing` bound by the
/// program prefix). Fresh buffers since round 4 wave 8: a buffer aliasing an
/// array the derivative reads is refused (G190 (c), BL4005).
let private ckAliasTail = """function f(x: Float64, w: A) -> Float64 = {
    let rec s: Array<Float64 like Idx<5>> =
        match s with
        | zero -> zero
        | zero :: n -> zero :: x
        | prefix :: n -> prefix :: prefix(n - 1) * prefix(n - 1) + forcing(0)
    s(4) + w(0)
}
let w0: A = [4.0]
let mut dw: A = [0.0]
let (value, dx) = ad.grad(f)(Float64(1.0), w0, dw)
"""

let private ckDecisions (src: string) (knob: string option) : Result<Blade.Effects.Decision list, string> =
    withEnvs [ ("BLADE_AD_CHECKPOINT", knob) ] (fun () ->
        Blade.Effects.Decisions.start ()
        let r =
            try
                match lower src with
                | Error e -> Error ($"lower: {e}")
                | Ok _ -> Ok (Blade.Effects.Decisions.drain () |> List.filter (fun d -> d.Rule = "ad-checkpoint"))
            with ex -> Error ($"lower raised: {ex.Message}")
        Blade.Effects.Decisions.drain () |> ignore
        r)

/// Raw bits of `src` under each knob value equal the full tape's (knob 0);
/// each knob value's decision is the one expected (Ok = applied, Error =
/// declined with that substring).
let private ckDifferential (name: string) (src: string) (arms: (string * Result<string, string>) list) : bool option list =
    let repro = [ ("BLADE_FP_REPRO", Some "1"); ("BLADE_FP_CONTRACT", Some "off"); ("BLADE_PRINT_BITS", Some "1") ]
    let runArm (knob: string) =
        withEnvs (("BLADE_AD_CHECKPOINT", Some knob) :: repro) (fun () -> runUnder $"{name}_k{knob}" None src)
    match runArm "0" with
    | Error e when e.StartsWith "Skipped:" -> resultLine Skip name e; [ None ]
    | Error e -> resultLine Fail name e; [ Some false ]
    | Ok full ->
        [ for (knob, want) in arms do
            let caseName = $"{name}: BLADE_AD_CHECKPOINT={knob}"
            let decisionOk, seen =
                match ckDecisions src (Some knob) with
                | Error e -> false, e
                | Ok ds ->
                    let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                    let ok =
                        match want, ds with
                        | Ok ev, [ d ] -> d.Outcome = Blade.Effects.Applied && d.Evidence |> List.exists (fun e -> e.Contains ev)
                        | Error why, [ d ] -> (match d.Outcome with Blade.Effects.Declined r -> r.Contains why | _ -> false)
                        | _ -> false
                    ok, seen
            if not decisionOk then
                resultLine Fail caseName $"decision: wanted {want}; saw {seen}"; yield Some false
            else
            match runArm knob with
            | Error e when e.StartsWith "Skipped:" -> resultLine Skip caseName e; yield None
            | Error e -> resultLine Fail caseName e; yield Some false
            | Ok lines ->
                let bits = lines |> List.forall (fun l -> not (l.Contains " = ") || not (l.Contains ".") || l.Contains " #")
                if lines = full && not lines.IsEmpty && bits then
                    resultLine Pass caseName $"""raw bits equal the full tape's: {String.concat " | " lines}"""; yield Some true
                else
                    resultLine Fail caseName $"""full [{String.concat " | " full}] vs checkpointed [{String.concat " | " lines}]"""; yield Some false ]

/// The corpus witnesses' decisions under the opt-in arm (`1`; unset declines
/// since wave 5), and the emission of ad/048: no 5001-cell buffer once
/// checkpointed.
let private ckCorpusPins () : bool list =
    let table =
        [ "047_recarray_tape_100001_zero_fill.blade", Ok "segments of K = 317 steps"
          "048_recarray_checkpoint_5001_rows.blade", Ok "71 segments of K = 71 steps"
          "049_recarray_checkpoint_snapshots_array_parameter.blade", Ok "replay-input snapshot: w -> "
          "035_recarray_logistic_grad.blade", Error "a tape of 4 cells (<= 4096) is kept whole"
          "108_recarray_checkpoint_snapshots_module_identity_result.blade", Error "a tape of 5 cells (<= 4096) is kept whole" ]
    // unset: the opt-in decline (wave 5, the stop rule)
    let unset =
        [ "048_recarray_checkpoint_5001_rows.blade", Error "opt-in (BLADE_AD_CHECKPOINT=1)" ]
    // forced to K = 2, the size gate passes and the replay-input admission
    // decides: a plain dense stored or total array the step reads is
    // SNAPSHOTTED (round 4 wave 6, gap G162), anything else declines
    let forced =
        [ "028_recarray_carry_final_257.blade", Ok "replay-input snapshot: w -> "
          "035_recarray_logistic_grad.blade", Ok "2 segments of K = 2 steps (the last 1)"
          // Codex round 2 (wave 5): a fresh array a later mut binding
          // aliases (since round 4 wave 8 a PROVED COPY: an aliasing buffer
          // is refused, G190 (c), BL4005 -- ad/050, 052, 053, 060-067 and 075
          // are now that refusal's witnesses, pinned by the corpus)
          "051_recarray_checkpoint_snapshots_fresh_array_aliased_later.blade", Ok "replay-input snapshot: forcing -> "
          "054_recarray_checkpoint_declines_cast_under_fast_contraction.blade", Error "casts to an integer type under fast contraction"
          // the FRESH-BUFFER twins (round 4 wave 9, lane A, A1) of the
          // witnesses the G190 (c) refusal turned into (rejects) files: the
          // same step, the gradient buffer a fresh array, each decision of
          // the replay-input admission guarded again -- the snapshots of
          // ad/050, 052, 060, 061 and the declines of ad/053, 062-067, 075
          "108_recarray_checkpoint_snapshots_module_identity_result.blade", Ok "replay-input snapshot: forcing -> "
          "109_recarray_checkpoint_snapshots_local_alias_fresh_buffer.blade", Ok "replay-input snapshot: local_forcing -> "
          "110_recarray_checkpoint_declines_destructured_component_fresh_buffer.blade", Error "the module value 'hidden' records no value"
          "111_recarray_checkpoint_snapshots_parameter_default_fresh_buffer.blade", Ok "replay-input snapshot: forcing -> "
          "112_recarray_checkpoint_snapshots_user_function_named_like_intrinsic_fresh_buffer.blade", Ok "replay-input snapshot: forcing -> "
          "113_recarray_checkpoint_declines_tuple_parameter_fresh_buffer.blade", Error "the step reads 'forcing', which the replay cannot snapshot: its value is not a stored form"
          "114_recarray_checkpoint_declines_row_view_named_like_intrinsic_fresh_buffer.blade", Error "the step reads 'forcing', which the replay cannot snapshot: its value is not a stored form"
          "115_recarray_checkpoint_declines_unannotated_call_result_fresh_buffer.blade", Error "the call of 'make' does not return a plain dense array"
          "116_recarray_checkpoint_declines_literal_of_subscripts_fresh_buffer.blade", Error "the step reads 'vals', which the replay cannot snapshot: an array literal element is not a proved scalar"
          "117_recarray_checkpoint_declines_literal_of_tuples_fresh_buffer.blade", Error "the step reads 'tuples', which the replay cannot snapshot: an array literal element is not a proved scalar"
          "118_recarray_checkpoint_declines_step_value_may_fail_fresh_buffer.blade", Error "the step value may fail"
          "119_recarray_checkpoint_declines_uneven_nested_literal_fresh_buffer.blade", Error "not rectangular" ]
    // decision-only shapes (gap G190): a deferred module map read inside a
    // differentiated function (it compiles in every lane since round 4 wave 8,
    // G190 (b): its run is pinned by corpus ad/084). (The ragged array literal over a leading
    // `RaggedIdx<_>` is refused at lowering since wave 7, G190 (a):
    // corpus index-types/579.)
    let inlineForced : (string * Result<string, string> * string) list =
        [ "deferred_division", Error "its value is not a stored form",
          "import ad as ad\ntype A = Array<Float64 like Idx<1>>\nfunction identity(a: A) -> A = a\nlet mut backing: A = [4.0]\nlet forcing = method_for(backing) <@> lambda(e) -> 8.0 / e\n" + ckAliasTail ]
    let inlinePins =
        [ for (nm, want, src) in inlineForced do
            let name = "ad_checkpoint_inline: " + nm + " (BLADE_AD_CHECKPOINT=2)"
            match ckDecisions src (Some "2") with
            | Error e -> resultLine Fail name e; yield false
            | Ok ds ->
                let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                let ok =
                    ds.Length = 1
                    && (match want, ds.Head.Outcome with
                        | Ok ev, Blade.Effects.Applied -> ds.Head.Evidence |> List.exists (fun e -> e.Contains ev)
                        | Error why, Blade.Effects.Declined r -> r.Contains why
                        | _ -> false)
                if ok then resultLine Pass name seen else resultLine Fail name $"wanted {want}; saw {seen}"
                yield ok ]
    let decisionPins =
        [ for (file, want, knob) in (table |> List.map (fun (f, w) -> (f, w, Some "1"))) @ (unset |> List.map (fun (f, w) -> (f, w, None))) @ (forced |> List.map (fun (f, w) -> (f, w, Some "2"))) do
            let suffix = match knob with Some k -> " (BLADE_AD_CHECKPOINT=" + k + ")" | None -> ""
            let name = "ad_checkpoint_corpus: " + file + suffix
            match corpusSource file with
            | Error e -> resultLine Fail name e; yield false
            | Ok src ->
                match ckDecisions src knob with
                | Error e -> resultLine Fail name e; yield false
                | Ok ds ->
                    let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                    let ok =
                        ds.Length = 1
                        && (match want, ds.Head.Outcome with
                            | Ok ev, Blade.Effects.Applied -> ds.Head.Evidence |> List.exists (fun e -> e.Contains ev)
                            | Error why, Blade.Effects.Declined r -> r.Contains why
                            | _ -> false)
                    if ok then resultLine Pass name seen else resultLine Fail name $"wanted {want}; saw {seen}"
                    yield ok ]
    let emission =
        let name = "ad_checkpoint_emission: ad/048 holds no 5001-cell tape or cotangent; BLADE_AD_CHECKPOINT=0 does"
        match corpusSource "048_recarray_checkpoint_5001_rows.blade" with
        | Error e -> resultLine Fail name e; false
        | Ok src ->
            let full (cpp: string) =
                System.Text.RegularExpressions.Regex.Matches(cpp, "_extents\[1\] = \{ ?5001 ?\}").Count
            match withEnvs [ ("BLADE_AD_CHECKPOINT", Some "1") ] (fun () -> cppOf "ck048_on" None src),
                  withEnvs [ ("BLADE_AD_CHECKPOINT", Some "0") ] (fun () -> cppOf "ck048_off" None src) with
            | Error e, _ | _, Error e -> resultLine Fail name e; false
            | Ok on, Ok off ->
                // the jvp keeps its own primal and tangent buffers (forward
                // mode: no tape), so the gradient's two are the difference
                let a, b = full on, full off
                if b = a + 2 then resultLine Pass name $"5001-cell buffers: {a} checkpointed, {b} full"; true
                else resultLine Fail name $"5001-cell buffers: {a} checkpointed vs {b} full (want full = checkpointed + 2)"; false
    decisionPins @ inlinePins @ [ emission ]

/// ad-replay-prune v1 (PM59, round 4 wave 5 A4): the reverse sweep's replay
/// of the forward fold of ad/030 is dead work and is dropped under the
/// opt-in `1`; unset records the opt-in decline, `0` the disabled one. The printed
/// values are compared by `blade test opt-diff ad` (an optimizer gate).
let private replayPrunePins () : bool list =
    let decisions (knob: string option) (src: string) =
        withEnvs [ ("BLADE_AD_REPLAY_PRUNE", knob) ] (fun () ->
            Blade.Effects.Decisions.start ()
            let r =
                try
                    match lower src with
                    | Error e -> Error ($"lower: {e}")
                    | Ok _ -> Ok (Blade.Effects.Decisions.drain () |> List.filter (fun d -> d.Rule = "ad-replay-prune"))
                with ex -> Error ($"lower raised: {ex.Message}")
            Blade.Effects.Decisions.drain () |> ignore
            r)
    [ for (knob, applied, why) in [ (None, false, "opt-in (BLADE_AD_REPLAY_PRUNE=1)"); (Some "1", true, ""); (Some "0", false, "disabled by BLADE_AD_REPLAY_PRUNE") ] do
        let name = $"""ad_replay_prune: ad/030 {(match knob with None -> "default" | Some k -> "BLADE_AD_REPLAY_PRUNE=" + k)}"""
        match corpusSource "030_halo_stencil_grad_linear.blade" with
        | Error e -> resultLine Fail name e; yield false
        | Ok src ->
            match decisions knob src with
            | Error e -> resultLine Fail name e; yield false
            | Ok ds ->
                let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                let ok =
                    not ds.IsEmpty
                    && ds |> List.forall (fun d ->
                        match d.Outcome with
                        | Blade.Effects.Applied -> applied && d.Evidence |> List.exists (fun e -> e.Contains "replayed statements dropped")
                        | Blade.Effects.Declined r -> not applied && r.Contains why)
                if ok then resultLine Pass name seen else resultLine Fail name $"saw {seen}"
                yield ok ]
    // the typed grammar (round 4 wave 6): the inlined kernel loop of ad/070
    // drops the dead REAL division `half` and keeps the INTEGER division `q`
    @ [ let name = "ad_replay_prune_typed: ad/070 drops the real division half, keeps the integer division q"
        match corpusSource "070_replay_prune_drops_real_division.blade" with
        | Error e -> resultLine Fail name e; false
        | Ok src ->
            match decisions (Some "1") src with
            | Error e -> resultLine Fail name e; false
            | Ok ds ->
                let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                let targets =
                    ds |> List.collect (fun d -> d.Evidence)
                       |> List.filter (fun e -> e.Contains "replayed statements dropped")
                       |> String.concat " "
                let ok =
                    System.Text.RegularExpressions.Regex.IsMatch(targets, @"_half\b")
                    && not (System.Text.RegularExpressions.Regex.IsMatch(targets, @"_q\b"))
                if ok then resultLine Pass name seen else resultLine Fail name $"saw {seen}"
                ok ]
    // the TRAP witnesses' fresh-buffer twins (round 4 wave 9, lane A, A1):
    // ad/056, 058, 068 and 071 needed a gradient buffer sharing the primal
    // input to trap in the replay, which G190 (c) refuses since round 4 wave
    // 8 (BL4005), so the trapping-statement rules lost their witnesses. Each
    // twin keeps the dead statement that MAY fail (`unused`: an integer
    // division, a checked ascription, lgamma, a call through a local
    // function value) beside a trap-free dead `spare`: under the opt-in the
    // prune drops `spare` and keeps `unused` (the values are pinned by the
    // corpus and compared by `blade test opt-diff ad`)
    @ [ for file, what in [ "121_replay_prune_keeps_integer_division_fresh_buffer.blade", "an integer division"
                            "122_replay_prune_keeps_checked_ascription_fresh_buffer.blade", "a checked ascription"
                            "123_replay_prune_keeps_dead_lgamma_fresh_buffer.blade", "lgamma"
                            "124_replay_prune_keeps_call_through_local_function_fresh_buffer.blade", "a call through a local function value" ] do
        let name = $"ad_replay_prune_keeps_trap: ad/{file.Substring(0, 3)} keeps the dead {what} (unused), drops spare"
        match corpusSource file with
        | Error e -> resultLine Fail name e; yield false
        | Ok src ->
            match decisions (Some "1") src with
            | Error e -> resultLine Fail name e; yield false
            | Ok ds ->
                let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                let targets =
                    ds |> List.filter (fun d -> d.Outcome = Blade.Effects.Applied)
                       |> List.collect (fun d -> d.Evidence)
                       |> List.filter (fun e -> e.Contains "replayed statements dropped")
                       |> String.concat " "
                let ok =
                    System.Text.RegularExpressions.Regex.IsMatch(targets, @"_spare\b")
                    && not (System.Text.RegularExpressions.Regex.IsMatch(targets, @"_unused\b"))
                if ok then resultLine Pass name seen else resultLine Fail name $"saw {seen}"
                yield ok ]
    // ad/120 (round 4 wave 9, A1): the fresh-buffer twin of ad/046 -- the
    // gather fuse declines a primal-reading stencil statically, whatever
    // the buffer (ad/046 itself is the G190 (c) refusal witness)
    @ [ let name = "ad_gather_fuse_declines: ad/120 a primal-reading tap keeps the per-tap passes"
        match corpusSource "120_gather_fuse_declines_primal_read_fresh_buffer.blade" with
        | Error e -> resultLine Fail name e; false
        | Ok src ->
            Blade.Effects.Decisions.start ()
            let r =
                try
                    match lower src with
                    | Error e -> Error ($"lower: {e}")
                    | Ok _ -> Ok (Blade.Effects.Decisions.drain () |> List.filter (fun d -> d.Rule = "ad-gather-fuse"))
                with ex -> Error ($"lower raised: {ex.Message}")
            Blade.Effects.Decisions.drain () |> ignore
            match r with
            | Error e -> resultLine Fail name e; false
            | Ok ds ->
                let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                let ok =
                    ds.Length = 1
                    && (match ds.Head.Outcome with
                        | Blade.Effects.Declined why -> why.Contains "a tap reads storage or may fail"
                        | _ -> false)
                if ok then resultLine Pass name seen else resultLine Fail name $"saw {seen}"
                ok ]

/// Corpus witnesses whose step reads an array beside a gradient buffer: each
/// checkpointed arm against the full tape, raw bits. Since round 4 wave 8
/// the buffers are FRESH (a buffer sharing what the step reads is refused,
/// G190 (c)); ad/108-118 are the fresh-buffer twins of the snapshot
/// witnesses (round 4 wave 9, lane A, A1).
let private ckCorpusDifferential () : (string * (string * Result<string, string>) list) list =
    [ "049_recarray_checkpoint_snapshots_array_parameter.blade", [ "auto", Ok "replay-input snapshot: w -> "; "2", Ok "replay-input snapshot: w -> " ]
      "051_recarray_checkpoint_snapshots_fresh_array_aliased_later.blade", [ "2", Ok "replay-input snapshot: forcing -> " ]
      "108_recarray_checkpoint_snapshots_module_identity_result.blade", [ "2", Ok "replay-input snapshot: forcing -> "; "3", Ok "replay-input snapshot: forcing -> " ]
      "109_recarray_checkpoint_snapshots_local_alias_fresh_buffer.blade", [ "2", Ok "replay-input snapshot: local_forcing -> " ]
      "111_recarray_checkpoint_snapshots_parameter_default_fresh_buffer.blade", [ "2", Ok "replay-input snapshot: forcing -> " ]
      "112_recarray_checkpoint_snapshots_user_function_named_like_intrinsic_fresh_buffer.blade", [ "2", Ok "replay-input snapshot: forcing -> " ]
      "113_recarray_checkpoint_declines_tuple_parameter_fresh_buffer.blade", [ "2", Error "which the replay cannot snapshot" ]
      "114_recarray_checkpoint_declines_row_view_named_like_intrinsic_fresh_buffer.blade", [ "2", Error "which the replay cannot snapshot" ]
      "118_recarray_checkpoint_declines_step_value_may_fail_fresh_buffer.blade", [ "2", Error "the step value may fail" ]
      // synthesized-name hygiene (round 4 wave 9, A3 class-closure review):
      // a module array spelled like the checkpoint buffer (`__ck4`) is not
      // captured by it, at forced intervals and under the auto arm
      "126_recarray_checkpoint_buffer_does_not_capture_user_name.blade", [ "2", Ok "segments of K = 2 steps"; "3", Ok "segments of K = 3 steps" ]
      "127_recarray_checkpoint_auto_does_not_capture_user_name.blade", [ "auto", Ok "71 segments of K = 71 steps" ] ]

/// Gap G250 (round 4 wave 9, lane A, A2): programs the G190 (c) rule still
/// ACCEPTS although the gradient buffer shares storage the checkpointed step
/// reads -- the derivative called through a function-typed parameter whose
/// type omits `mut` (an unresolved head the rule does not judge). The
/// replay-input snapshot is what keeps the checkpointed gradient equal to
/// the full tape on them, so each is run raw-bit against it; once the rule
/// refuses the shape (BL4005, the G190 (c) / G240 owner) the case passes as
/// refused instead.
let private ckHoleTail = """function f(x: Float64, w: A) -> Float64 = {
    let rec s: Array<Float64 like Idx<5>> =
        match s with
        | zero -> zero
        | zero :: n -> zero :: x
        | prefix :: n -> prefix :: prefix(n - 1) * prefix(n - 1) + forcing(0)
    s(4) + w(0)
}
function call_twice(h: (Float64, A, A) -> (Float64, Float64), b: A) -> (Float64, Float64) = h(Float64(1.0), b, b)
"""

let private ckHoleSources : (string * string * (string * Result<string, string>) list) list =
    let head = "import ad as ad\ntype A = Array<Float64 like Idx<1>>\nfunction identity(a: A) -> A = a\n"
    let snap (x: string) = Ok ("replay-input snapshot: " + x + " -> ")
    let immutableLiteral =
        head + "let forcing: A = [2.0]\n" + ckHoleTail + "let (value, dx) = call_twice(ad.grad(f), forcing)\n"
    let moduleIdentityAlias =
        head + "let mut backing: A = [2.0]\nlet forcing = identity(backing)\n" + ckHoleTail
        + "let (value, dx) = call_twice(ad.grad(f), backing)\n"
    let arrayParameter =
        head + ckHoleTail.Replace("+ forcing(0)", "+ w(0)") + "let mut wv: A = [2.0]\nlet (value, dx) = call_twice(ad.grad(f), wv)\n"
    let localAlias =
        head + "let forcing: A = [2.0]\n"
        + ckHoleTail.Replace("    let rec s", "    let local_forcing = forcing\n    let rec s").Replace("+ forcing(0)", "+ local_forcing(0)")
        + "let (value, dx) = call_twice(ad.grad(f), forcing)\n"
    let wrapper =
        head + "let forcing: A = [2.0]\n" + ckHoleTail
        + "function wrapper(x: Float64, w: A, dw: mut A) -> (Float64, Float64) = ad.grad(f)(x, w, dw)\n"
        + "let (value, dx) = call_twice(wrapper, forcing)\n"
    [ ("immutable_literal", immutableLiteral, [ ("2", snap "forcing"); ("3", snap "forcing") ])
      ("module_identity_alias", moduleIdentityAlias, [ ("2", snap "forcing") ])
      ("array_parameter", arrayParameter, [ ("2", snap "w") ])
      ("local_alias", localAlias, [ ("2", snap "local_forcing") ])
      ("wrapper", wrapper, [ ("2", snap "forcing") ]) ]

let private ckHoleCases () : bool option list =
    [ for (nm, src, arms) in ckHoleSources do
        let name = "ad_checkpoint_hole_" + nm
        match lower src with
        // any BL4005 mut-argument refusal (the code where the error string
        // carries it; `lower` returns a lowering diagnostic's message alone,
        // and every G190 (c) message names the mut parameter: wave 8's "is
        // passed to a mut parameter", wave 9 lane C's "has a mut parameter
        // and is used here as a VALUE")
        | Error e when e.Contains "BL4005" || e.Contains "mut parameter" ->
            resultLine Pass name $"refused at lowering (G190 (c) now judges the shape): {e}"; yield Some true
        | Error e -> resultLine Fail name ("lower: " + e); yield Some false
        | Ok _ -> yield! ckDifferential name src arms ]

let private ckCases () : bool option list =
    if not Build.capabilities.Value.HasGpp then
        resultLine Skip "ad_checkpoint_differential" "g++ not available"
        [ None ]
    else
        ckDifferential "ad_checkpoint_logistic" ckLogistic
            [ "auto", Error "a tape of 23 cells (<= 4096) is kept whole"
              "2", Ok "11 segments of K = 2 steps (the last 2)"
              "3", Ok "8 segments of K = 3 steps (the last 1)"
              "5", Ok "5 segments of K = 5 steps (the last 2)"
              "7", Ok "4 segments of K = 7 steps (the last 1)"
              "21", Ok "2 segments of K = 21 steps (the last 1)"
              "22", Error "covers the whole recurrence of 22 steps" ]
        @ ckDifferential "ad_checkpoint_forcing" ckForcing
            [ "3", Ok "10 segments of K = 3 steps (the last 3)"
              "4", Ok "8 segments of K = 4 steps (the last 2)"
              "11", Ok "3 segments of K = 11 steps (the last 8)" ]
        @ ckDifferential "ad_checkpoint_forcing_buffers" ckForcingBuffers
            [ "3", Ok "replay-input snapshot: xs -> "
              "7", Ok "replay-input snapshot: xs -> " ]
        @ ckDifferential "ad_checkpoint_scalar_buffers" ckScalarBuffers
            [ "3", Ok "10 segments of K = 3 steps (the last 3)"
              "7", Ok "5 segments of K = 7 steps (the last 2)" ]
        // the replay-input snapshot (round 4 wave 6, gap G162): gradient
        // buffers that ALIAS the arrays the step reads; the checkpointed
        // gradient equals the full tape bit for bit
        @ (ckCorpusDifferential ()
           |> List.collect (fun (file, arms) ->
               match corpusSource file with
               | Error e -> resultLine Fail ("ad_checkpoint_snapshot: " + file) e; [ Some false ]
               | Ok src -> ckDifferential ("ad_checkpoint_snapshot_" + file.Substring(0, 3)) src arms))
        @ ckHoleCases ()

/// Gap G193: the AD pipeline's restored let annotations (G187) and its mode
/// are PER PROGRAM (GradCommon.FlowCell over AsyncLocal), not process
/// globals keyed by function name. Two syntheses of SAME-NAMED functions in
/// parallel tasks -- a host compiling two programs at once -- each install
/// their own state (as `Grad.expand` does), note `sin` with their own
/// annotation, wait for the other at a barrier, and must read back their own
/// annotation and mode. The barrier forces the interleaving the old globals
/// lost (the second task's reset cleared the first's entry; its note made
/// the name ambiguous).
let private gradStatePerProgram () : bool =
    let name = "ad_state_per_program: two same-named syntheses in parallel keep their own annotations (G193)"
    let barrier = new System.Threading.Barrier(2)
    let run (tag: string) (mode: string) =
        System.Threading.Tasks.Task.Run(fun () ->
            GradCommon.installProgramState ()
            GradCommon.errMode.Value <- mode
            GradCommon.resetLetAnnotations "f"
            GradCommon.noteLetAnnotation "f" "sin" (Some (Blade.Ast.TyNamed (tag, [])))
            GradCommon.letAnnotationOwner.Value <- "f"
            barrier.SignalAndWait()
            let back = GradCommon.restoredLetAnnotation "sin"
            barrier.SignalAndWait()
            (back, GradCommon.errMode.Value))
    try
        let a = run "A1" "grad"
        let b = run "A2" "jvp"
        System.Threading.Tasks.Task.WaitAll([| a :> System.Threading.Tasks.Task; b :> System.Threading.Tasks.Task |], 30000) |> ignore
        match a.Result, b.Result with
        | (Some (Blade.Ast.TyNamed ("A1", [])), "grad"), (Some (Blade.Ast.TyNamed ("A2", [])), "jvp") ->
            resultLine Pass name "each task read its own annotation and mode"; true
        | ra, rb -> resultLine Fail name $"task 1 read {ra}, task 2 read {rb}"; false
    with ex -> resultLine Fail name ex.Message; false

/// Gap G208 (4): `Ast.synthSpan`, the ambient span every synthesized AST node
/// takes (`syn`), is PER FLOW (AsyncLocal), like the G193 state above: two
/// syntheses stamping different declaration spans in parallel tasks, a
/// barrier between the stamp and the build, each build carries its own span.
let private synthSpanPerProgram () : bool =
    let name = "ad_synth_span_per_program: two parallel syntheses stamp their own spans (G208 (4))"
    let barrier = new System.Threading.Barrier(2)
    let run (line: int) =
        System.Threading.Tasks.Task.Run(fun () ->
            Blade.Ast.setSynthSpan { Blade.Ast.noSpan with StartLine = line; EndLine = line }
            barrier.SignalAndWait()
            let e = Blade.Ast.syn (Blade.Ast.ExprLit (Blade.Ast.LitInt 1L))
            barrier.SignalAndWait()
            e.Span.StartLine)
    try
        let a = run 11
        let b = run 22
        System.Threading.Tasks.Task.WaitAll([| a :> System.Threading.Tasks.Task; b :> System.Threading.Tasks.Task |], 30000) |> ignore
        if a.Result = 11 && b.Result = 22 then
            resultLine Pass name "each task's synthesized node took its own declaration span"; true
        else resultLine Fail name $"task 1 node at line {a.Result}, task 2 at line {b.Result}"; false
    with ex -> resultLine Fail name ex.Message; false

/// Round 4 wave 9 (lane A, A3 review round 6, Codex 5a8b7b7d): the jvp
/// tangent-name prefix (`GradSweeps.tangentPrefix`) is PER FLOW like the G193
/// state -- two syntheses choosing different prefixes in parallel tasks, a
/// barrier between the choice and the use, each reads its own prefix (the
/// process-global ref let one program's second-round prefix leak into
/// another's: a captured module binding, (15, 10, 25, 0) for (21, 14, 35, 0)).
let private tangentPrefixPerProgram () : bool =
    let name = "ad_tangent_prefix_per_program: two parallel syntheses keep their own tangent prefix"
    let barrier = new System.Threading.Barrier(2)
    let run (prefix: string) =
        System.Threading.Tasks.Task.Run(fun () ->
            GradSweeps.tangentPrefix.Value <- prefix
            barrier.SignalAndWait(30000) |> ignore
            let mine = GradSweeps.tName "x"
            barrier.SignalAndWait(30000) |> ignore
            mine)
    try
        let a = run "__t_"
        let b = run "__t2_"
        if not (System.Threading.Tasks.Task.WaitAll([| a :> System.Threading.Tasks.Task; b :> System.Threading.Tasks.Task |], 30000)) then
            resultLine Fail name "the two tasks did not finish within 30 s"; false
        elif a.Result = "__t_x" && b.Result = "__t2_x" then
            resultLine Pass name "each task named its tangent with its own prefix"; true
        else resultLine Fail name $"task 1 named {a.Result}, task 2 {b.Result}"; false
    with ex -> resultLine Fail name ex.Message; false

let runAccessTests () =
    printHeader "Blade-DSL: Halo Access Tests"
    let pins =
        [ recordPins ()
          routeEmission ()
          computedOffsetDeclines ()
          gradStatePerProgram ()
          synthSpanPerProgram ()
          tangentPrefixPerProgram () ]
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
    let diffs = diffs @ zeroFill @ ckCases ()
    let pins = pins @ ckCorpusPins () @ replayPrunePins ()
    let passed = (pins |> List.filter id |> List.length) + (diffs |> List.filter (fun d -> d = Some true) |> List.length)
    let failed = (pins |> List.filter not |> List.length) + (diffs |> List.filter (fun d -> d = Some false) |> List.length)
    let skipped = diffs |> List.filter Option.isNone |> List.length
    printFooter "Halo Access" [$"{passed} passed"; $"{failed} failed"; $"{skipped} skipped"]
    { Block = "Halo Access"; Passed = passed; Failed = failed; Skipped = skipped
      FailedNames = if failed = 0 then [] else ["see above"] }
