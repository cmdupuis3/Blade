// The BLADE_LLVM lane's own test block (`blade test llvm`).
//
// Four gates, because the lane can drift in four independent ways:
//
//   0. COMPARE RULES (runLlvmCompareRuleTests) -- assertions on the output
//      comparator itself, in both directions: what it must accept AND what it
//      must reject. A differential that cannot say no is decoration, and the
//      specific way this lane's earlier ad-hoc sweeps broke was a silently
//      empty capture reading as 100% agreement.
//
//   1. EMISSION PINS (runLlvmGoldenTests) -- three tiny programs under
//      tests/fixtures/llvm/ whose emitted .ll text is pinned byte for byte
//      against a committed golden. Needs NO toolchain: it is pure emission, so
//      it asserts something even on a machine with neither clang nor g++. The
//      pins are against OUR OWN output, not against any external notion of
//      correct IR -- their job is to make an emitter change LOUD, not to judge
//      it. Regenerate deliberately with BLADE_LLVM_GOLDEN_UPDATE=1.
//
//   2. FACT LAYER (runLlvmFactTests) -- what the emission pins structurally
//      cannot say. A pin records ONE environment and reports drift as "line 14
//      differs"; this block names each fact and each knob, and asserts every
//      one in both directions, because the fact-layer failures that matter
//      (`reassoc` on a map, `readonly` on a written pool) change no output
//      until an optimizer believes them. Toolchain-free, like the pins.
//
//   3. DIFFERENTIAL (runLlvmDifferentialTests) -- for each .blade file of a
//      configurable corpus category list, compile and run BOTH back ends and
//      compare stdout. This is the gate that says the lane is CORRECT, and it
//      is deliberately a lane-vs-lane comparison rather than a re-check of the
//      corpus EXPECT pins: the C++ lane is the byte-pinned oracle already, so
//      agreeing with it is the strictest available statement, and it covers
//      every printed value including the ones no pin mentions.
//
// Why this block drives the two back ends DIRECTLY instead of setting
// BLADE_LLVM and calling the ordinary runner: the whole existing harness
// reaches codegen through `CliCommands.compileFile` /
// `CodeGen.genSelfContainedProgramFromIR`, which are the C++ lane by
// construction -- BLADE_LLVM has no effect on any of it, and `CliCommands` is
// compiled AFTER the test modules anyway, so it is not even nameable here. So
// this file owns a two-lane driver: one front end pass per lane, then
// `EmitLlvm.tryEmitProgramNamed` on one side and
// `CodeGen.genSelfContainedProgramFromIR` on the other.
//
// SKIP POLICY (the house rule: a missing capability skips, never fails):
//   * no clang            -> the whole differential block skips (Skipped = 1).
//   * no g++              -> ditto; without the oracle there is nothing to
//                            compare against.
//   * llvm lane REFUSES   -> that file skips, named, and its reason is counted
//                            into the histogram printed at the end. A refusal
//                            is the lane working as designed (whole-program or
//                            nothing), so it must never redden the block.
//   * front end refuses   -> that file skips (reject probes live in these
//                            categories too).
// A MISMATCH is the only interesting failure, and it fails loudly with the
// first three differing tokens and the paths of both kept artifacts.
module Blade.Tests.LlvmTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open Blade
open Blade.Tests.TestHarness

// ---------------------------------------------------------------------------
// Output comparison: tokenized near-equality
// ---------------------------------------------------------------------------

/// Split program output into comparison tokens. Whitespace separates, and the
/// printer's structural punctuation is split OFF into tokens of its own, so a
/// number never carries a bracket into its parse: `x = [1, 2]` tokenizes as
/// `x` `=` `[` `1` `,` `2` `]` rather than as `[1,` and `2]`. A minus sign is
/// deliberately NOT a separator -- it belongs to the number it precedes.
let private tokenize (s: string) : string[] =
    let sb = Text.StringBuilder()
    let toks = ResizeArray<string>()
    let flush () =
        if sb.Length > 0 then
            toks.Add(sb.ToString())
            sb.Clear() |> ignore
    for ch in s do
        if Char.IsWhiteSpace ch then flush ()
        elif ch = '[' || ch = ']' || ch = '(' || ch = ')' || ch = ',' || ch = '=' then
            flush ()
            toks.Add(string ch)
        else sb.Append ch |> ignore
    flush ()
    toks.ToArray()

/// Parse a token as a FINITE double, or None. `nan`/`inf`/`-inf` deliberately
/// come back None so they fall through to the exact-string path: comparing
/// them numerically would make NaN unequal to itself, and would let `inf`
/// match `-inf` through no arithmetic anyone wants.
let private parseNum (t: string) : float option =
    match Double.TryParse(t, Globalization.NumberStyles.Float,
                          Globalization.CultureInfo.InvariantCulture) with
    | true, v when not (Double.IsNaN v) && not (Double.IsInfinity v) -> Some v
    | _ -> None

/// Near-equality for one token pair: identical text always passes; two finite
/// numbers pass within relative 1e-9, falling back to absolute 1e-12 once both
/// magnitudes are below 1e-9 (where a relative test is meaningless).
///
/// ZERO IS EXCLUDED FROM THE TOLERANCE on purpose. `0` and `-0` are one ULP
/// apart and would pass any absolute test, but the two lanes print them
/// DIFFERENTLY by design (the printer census pins `-0.0` to "-0.0"), so a signed
/// zero divergence is exactly the kind of printer drift this block exists to
/// catch. Both-zero therefore demands identical text, which the fast path
/// above already granted when it is true.
///
/// THE FLOAT MARKER IS NOT TOLERATED EITHER. A floating value prints with a
/// decimal point (`2.0`; CppFormat.markFloat), an integer as a bare digit run
/// (`2`), so `2` against `2.0` is a TYPE divergence between the lanes, not a
/// near-equal pair: one lane lost the marker.
let private isBareInt (t: string) : bool =
    let start = if t.StartsWith "-" then 1 else 0
    t.Length > start && t |> Seq.skip start |> Seq.forall Char.IsAsciiDigit

let private tokenEq (a: string) (b: string) : bool =
    if String.Equals(a, b, StringComparison.Ordinal) then true
    else
        match parseNum a, parseNum b with
        | Some _, Some _ when isBareInt a <> isBareInt b -> false   // Int vs Float spelling
        | Some x, Some y when x = 0.0 && y = 0.0 -> false   // signed zero: text decides
        | Some x, Some y ->
            let scale = max (abs x) (abs y)
            if scale < 1e-9 then abs (x - y) <= 1e-12
            else abs (x - y) <= 1e-9 * scale
        | _ -> false

/// Drop the `<name> completed in <t>s` line both lanes print. It is a wall
/// clock reading and never matches.
let private stripTiming (s: string) : string =
    s.Replace("\r\n", "\n").Trim().Split('\n')
    |> Array.filter (fun l -> not (l.Contains "completed in"))
    |> String.concat "\n"

/// Compare two runs. Ok () or the first `maxDiffs` differing token positions,
/// rendered as `#<idx> cpp=<tok> llvm=<tok>`. A length difference is reported
/// at the position where one side ran out, with `<end>` for the missing token.
let private compareRuns (maxDiffs: int) (cppOut: string) (llvmOut: string) : Result<unit, string list> =
    let a = tokenize (stripTiming cppOut)
    let b = tokenize (stripTiming llvmOut)
    let n = max a.Length b.Length
    let diffs = ResizeArray<string>()
    let mutable i = 0
    while i < n && diffs.Count < maxDiffs do
        let ta = if i < a.Length then a.[i] else "<end>"
        let tb = if i < b.Length then b.[i] else "<end>"
        if ta = "<end>" || tb = "<end>" || not (tokenEq ta tb) then
            diffs.Add($"#{i} cpp={ta} llvm={tb}")
        i <- i + 1
    if diffs.Count = 0 then Ok () else Error (List.ofSeq diffs)

// ---------------------------------------------------------------------------
// Block 0: the comparator's own rules
// ---------------------------------------------------------------------------
//
// A differential block is only worth its runtime if its comparator can say NO.
// The failure mode that motivates this block is specific and has already
// happened once on this lane: a driver whose output capture was silently broken
// compared "" with "" and reported 100% agreement. Every rule below is
// therefore asserted in BOTH directions -- what must match, and what must not.

/// Assertions over `compareRuns`, run in-process with no toolchain at all.
let runLlvmCompareRuleTests () : BlockResult =
    printHeader "LLVM Compare Rules"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let check (name: string) (cpp: string) (llvm: string) (shouldMatch: bool) =
        let got = compareRuns 3 cpp llvm
        match got, shouldMatch with
        | Ok (), true ->
            passed <- passed + 1
            resultLine Pass name "agrees"
        | Error diffs, false ->
            passed <- passed + 1
            resultLine Pass name ($"differs at {(List.head diffs)}")
        | Ok (), false ->
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name "the comparator accepted output it must reject"
        | Error diffs, true ->
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name ($"""the comparator rejected matching output: {(String.concat " | " diffs)}""")
    // The trap this block exists for: an output capture that silently returns
    // nothing must NOT read as agreement with a program that printed values.
    check "empty vs printed" "" "x = 1" false
    check "printed vs empty" "x = 1" "" false
    check "both empty" "" "" true
    // Exact agreement, including through the printer's punctuation.
    check "identical scalars" "x = 42\ny = true" "x = 42\ny = true" true
    check "identical arrays" "x = [1, 2, 3]" "x = [1, 2, 3]" true
    check "identical nested rank 2" "x = [[1, 2], [3, 4]]" "x = [[1, 2], [3, 4]]" true
    // The wall-clock line differs on every single run and must be invisible.
    check "timing line ignored" "p completed in 1e-07s\nx = 1" "p completed in 9.5s\nx = 1" true
    // Relative tolerance: 1e-10 apart passes, 1e-3 apart does not.
    check "relative 1e-10 apart" "x = 1.0" "x = 1.0000000001" true
    check "relative 1e-3 apart" "x = 1.0" "x = 1.001" false
    // The real near-equal this lane produces: the C++ oracle's FMA residual
    // against the llvm lane's exact zero (corpus loops/143). Absolute floor.
    check "fma residual vs exact zero" "d = -3.5527136788005e-15" "d = 0.0" true
    check "absolute floor respected" "d = 1e-9" "d = 2e-9" false
    // The float marker is part of the value's spelling: an Int against the
    // same Float is a lane disagreeing about the TYPE.
    check "int vs float spelling differs" "x = 2" "x = 2.0" false
    check "float vs int array cell differs" "x = [1.0, 2.0]" "x = [1.0, 2]" false
    // Non-finite tokens compare as TEXT, never as arithmetic.
    check "nan equals nan" "x = nan" "x = nan" true
    check "nan differs from zero" "x = nan" "x = 0.0" false
    check "inf differs from -inf" "x = inf" "x = -inf" false
    check "inf equals inf" "x = inf" "x = inf" true
    // Signed zero is a printer fact, not a numeric one: the census pins -0.0
    // to "-0.0", so the two lanes disagreeing here is a real regression.
    check "signed zero is a difference" "x = 0.0" "x = -0.0" false
    // Structure: element values, element COUNT, and non-numeric text.
    check "array element differs" "x = [1, 2, 3]" "x = [1, 9, 3]" false
    check "array length differs" "x = [1, 2, 3]" "x = [1, 2]" false
    check "binding name differs" "x = 1" "y = 1" false
    check "bool differs" "x = true" "x = false" false
    check "string differs" "s = hello" "s = world" false
    printFooter "LLVM Compare Rules"
        [ $"{passed} passed"; $"{failed} failed"; "0 skipped" ]
    { Block = "LLVM Compare Rules"; Passed = passed; Failed = failed; Skipped = 0
      FailedNames = failedNames }

// ---------------------------------------------------------------------------
// Skip-reason histogram
// ---------------------------------------------------------------------------

/// Collapse a skip/refusal reason into a bucket key: quoted identifiers become
/// `'~'` and digit runs become `#`, so `the binding 'arr1d' has type
/// Array<Float64 like Idx<3>>` and its thousand siblings land in ONE row.
/// Truncated, because refusal reasons are full sentences by design.
let private reasonKey (r: string) : string =
    let sb = Text.StringBuilder()
    let mutable inQuote = false
    let mutable prevHash = false
    for ch in r do
        if ch = '\'' then
            if not inQuote then sb.Append "'~'" |> ignore
            inQuote <- not inQuote
            prevHash <- false
        elif inQuote then ()
        elif Char.IsDigit ch then
            if not prevHash then sb.Append '#' |> ignore
            prevHash <- true
        else
            sb.Append ch |> ignore
            prevHash <- false
    let s = sb.ToString().Trim()
    if s.Length > 78 then s.Substring(0, 78) + "..." else s

/// Print the reason histogram, most frequent first. This is the block's real
/// coverage report: every row is a construct the lane does not yet emit.
let private printReasonHistogram (reasons: string list) =
    if not reasons.IsEmpty then
        printfn ""
        printfn "  Skip reasons (%d skipped, by frequency):" reasons.Length
        reasons
        |> List.countBy reasonKey
        |> List.sortByDescending snd
        |> List.iter (fun (k, n) -> printfn "    %4d  %s" n k)

// ---------------------------------------------------------------------------
// Process helper
// ---------------------------------------------------------------------------

/// Run a produced executable capturing stdout and stderr SEPARATELY (the
/// shared `Build.runExecutable` merges them, which would make a stderr-only
/// difference read as a stdout mismatch). 60s guard: every corpus program in
/// these categories finishes in well under a second, so a longer run is a hang,
/// and a hung differential sweep is worse than a failed one.
let private runExeCapture (exeFile: string) : Result<int * string * string, string> =
    try
        let full = Path.GetFullPath exeFile
        let psi = ProcessStartInfo(full)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- Path.GetDirectoryName full
        use proc = Process.Start psi
        let outTask = Blade.Runtime.readToEndOffPool proc.StandardOutput
        let errTask = Blade.Runtime.readToEndOffPool proc.StandardError
        if proc.WaitForExit 60000 then Ok (proc.ExitCode, outTask.Result, errTask.Result)
        else
            (try proc.Kill() with _ -> ())
            Error "execution timed out after 60s"
    with ex -> Error ($"execution exception: {ex.Message}")

// ---------------------------------------------------------------------------
// Block 1: emission golden pins
// ---------------------------------------------------------------------------

/// Directory holding the pinned programs and their .ll goldens. Same two-root
/// rule as tests/Corpus.fs: the source tree wins when running from the repo
/// root (so a pin edit takes effect with no rebuild), the deployed copy
/// answers otherwise.
let private goldenRoot : Lazy<string> =
    lazy
        let candidates =
            [ Path.Combine(".", "tests", "fixtures", "llvm")
              Path.Combine(AppContext.BaseDirectory, "tests", "fixtures", "llvm") ]
        match candidates |> List.tryFind Directory.Exists with
        | Some d -> d
        | None ->
            failwith $"""llvm golden fixtures not found. Looked in: {(candidates |> List.map Path.GetFullPath |> String.concat " ; ")}"""

/// The pinned programs, in report order. Scalars, an outlined function with a
/// branch, and a dense array with a fold -- one representative of each emission
/// shape the lane has.
let private goldenNames = [ "scalar_arith"; "function_branch"; "array_reduce" ]

/// Env gate, read per call (never a module-level `let`, per the repo's
/// environment discipline): rewrite the goldens instead of asserting them.
let private goldenUpdateEnabled () : bool =
    match Environment.GetEnvironmentVariable "BLADE_LLVM_GOLDEN_UPDATE" with
    | "1" | "on" -> true
    | _ -> false

/// Line endings are normalized on BOTH sides before comparing. The emitter
/// joins with '\n', but `core.autocrlf=true` gives this repo CRLF working
/// trees, so a literal byte comparison would fail on a fresh checkout for a
/// reason that has nothing to do with the emitter. Everything else -- spacing,
/// register numbering, declaration order, blank lines -- is compared exactly.
let private normalizeLl (s: string) = s.Replace("\r\n", "\n")

/// Run `body` with a set of environment variables pinned, restoring every one
/// afterwards (including back to UNSET, which `null` does).
///
/// Needed by two blocks for two different reasons: the emission pins are
/// against the DEFAULT numeric policy (the fast regime since 2026-10-17), so a
/// developer whose shell happens to carry `BLADE_FP_REPRO=1` must not see
/// three phantom failures; and the fact block asserts each knob's effect,
/// which means setting it.
let private withEnv (pins: (string * string option) list) (body: unit -> 'a) : 'a =
    let saved = pins |> List.map (fun (k, _) -> k, Environment.GetEnvironmentVariable k)
    for (k, v) in pins do
        Environment.SetEnvironmentVariable(k, (match v with Some s -> s | None -> null))
    try body ()
    finally for (k, v) in saved do Environment.SetEnvironmentVariable(k, v)

/// The knobs that change emitted text. Pinned OFF around any assertion about
/// default emission; listed once so a knob added later is added in one place.
let private numericKnobs =
    [ "BLADE_FP_REASSOC"; "BLADE_FP_REPRO"; "BLADE_FP_CONTRACT"; "BLADE_LLVM_FACTS"; "BLADE_LLVM_BRICKS"; "BLADE_SYRK_TILE" ]

let private defaultEmissionEnv = numericKnobs |> List.map (fun k -> k, None)

let runLlvmGoldenTests () : BlockResult =
    printHeader "LLVM Emission Pins"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let updating = goldenUpdateEnabled ()
    if updating then
        printfn "BLADE_LLVM_GOLDEN_UPDATE is on: pins are being REWRITTEN, not asserted.\n"
    let pass name detail =
        passed <- passed + 1
        resultLine Pass name detail
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [name]
        resultLine Fail name detail
    let root = goldenRoot.Value
    for name in goldenNames do
        let srcPath = Path.Combine(root, name + ".blade")
        let pinPath = Path.Combine(root, name + ".ll")
        try
            if not (File.Exists srcPath) then
                fail name ($"missing pinned program {srcPath}")
            else
                let source = File.ReadAllText srcPath
                match Lowering.lower source with
                | Error e -> fail name ($"lowering refused the pinned program: {e}")
                | Ok ir ->
                    match IRValidate.validateIR ir with
                    | Error errs -> fail name ($"""IR validation refused: {(String.concat "; " errs)}""")
                    | Ok ir ->
                        // The program NAME reaches the emitted text (it labels
                        // the timing line), so it must be the fixture stem here
                        // and in the update path alike, or the pin would never
                        // match what `blade emit` produces for the same file.
                        // The pins record DEFAULT emission, so the numeric
                        // knobs are forced off for the call itself -- an
                        // inherited BLADE_FP_REPRO would otherwise fail all
                        // three pins for a reason that is not a drift.
                        match withEnv defaultEmissionEnv (fun () -> EmitLlvm.tryEmitProgramNamed name ir) with
                        | Error reason ->
                            // A pinned program is chosen to be inside the
                            // lane's coverage. Refusing one is a regression,
                            // not a skip.
                            fail name ($"the llvm lane refused a PINNED program: {reason}")
                        | Ok ll when updating ->
                            File.WriteAllText(pinPath, ll)
                            pass name ($"pin rewritten ({(normalizeLl ll |> fun s -> s.Split('\n').Length)} lines)")
                        | Ok ll ->
                            if not (File.Exists pinPath) then
                                fail name ($"no pin at {pinPath} -- run with BLADE_LLVM_GOLDEN_UPDATE=1 to create it")
                            else
                                let expected = normalizeLl (File.ReadAllText pinPath)
                                let actual = normalizeLl ll
                                if expected = actual then
                                    pass name ($"{(actual.Split('\n').Length)} lines pinned")
                                else
                                    // Park the actual beside the pin so the
                                    // reader can diff two files instead of
                                    // reading a truncated message.
                                    let actualPath = pinPath + ".actual"
                                    (try File.WriteAllText(actualPath, ll) with _ -> ())
                                    let el = expected.Split('\n')
                                    let al = actual.Split('\n')
                                    let firstDiff =
                                        let n = max el.Length al.Length
                                        let mutable i = 0
                                        let mutable found = -1
                                        while i < n && found < 0 do
                                            let e = if i < el.Length then el.[i] else "<end>"
                                            let a = if i < al.Length then al.[i] else "<end>"
                                            if e <> a then found <- i
                                            i <- i + 1
                                        found
                                    let e = if firstDiff < el.Length then el.[firstDiff] else "<end>"
                                    let a = if firstDiff < al.Length then al.[firstDiff] else "<end>"
                                    fail name
                                        (sprintf "emission drifted at line %d: pinned %s | emitted %s (wrote %s; BLADE_LLVM_GOLDEN_UPDATE=1 re-pins)"
                                            (firstDiff + 1) e a actualPath)
        with ex -> fail name ($"exception: {ex.Message}")
    printFooter "LLVM Emission Pins"
        [ $"{passed} passed"; $"{failed} failed"; "0 skipped" ]
    { Block = "LLVM Emission Pins"; Passed = passed; Failed = failed; Skipped = 0
      FailedNames = failedNames }

// ---------------------------------------------------------------------------
// Block 2: the fact layer
// ---------------------------------------------------------------------------
//
// The emission pins above would catch a fact-layer change, but only as "line
// 14 differs" -- they cannot say WHICH fact moved, and they say nothing at all
// about the three knobs, because a pin records exactly one environment. This
// block asserts each fact and each knob by name, in BOTH directions: the text
// that must appear, and the text that must NOT.
//
// The negative half is the load-bearing half. `reassoc` on a map's `fmul` and
// `readonly` on a written-through pool are both silent miscompiles: they
// change no output until an optimizer believes them, and then they change the
// wrong one. So every fact here is paired with a program where it must be
// absent.

/// Emit one source under a pinned environment, or fail loudly. Emission only:
/// no toolchain is touched, so this block runs anywhere.
let private emitUnder (pins: (string * string option) list) (name: string) (source: string) : Result<string, string> =
    withEnv (defaultEmissionEnv @ pins) (fun () ->
        match Lowering.lower source with
        | Error e -> Error ("lowering refused: " + e)
        | Ok ir ->
            match IRValidate.validateIR ir with
            | Error errs -> Error ("IR validation refused: " + String.concat "; " errs)
            | Ok ir ->
                match EmitLlvm.tryEmitProgramNamed name ir with
                | Error r -> Error ("the llvm lane refused: " + r)
                | Ok ll -> Ok ll)

/// A licensed fold (`(+)` is a recognized builtin body, so `foldReorderLicensed`
/// holds) over a map whose arithmetic carries NO license -- both shapes in one
/// program, which is what makes "exactly the accumulator chain" checkable.
let private licensedFoldSource = """
type Ix = Idx<8>
let xs: Array<Float like Ix> = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let scaled = xs * 0.5
let total = reduce(scaled, (+))
"""

/// An UNLICENSED fold: the kernel is neither a recognized builtin body nor
/// `comm`-declared, so no build reassociates it.
let private unlicensedFoldSource = """
type Ix = Idx<8>
function skew(a: Float64, b: Float64) -> Float64 = { a * 0.75 + b }
let xs: Array<Float like Ix> = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let total = reduce(xs, skew)
"""

/// An array parameter nothing writes through (`readonly` must appear) next to
/// a self-recursive function (`norecurse` must not, and the module loses its
/// termination claim).
let private readonlyAndRecursionSource = """
type Ix = Idx<6>
function total(row: T^1) -> T^0 = reduce(row, (+))
function fact(n: Int64) -> Int64 = { if n <= 1 then 1 else n * fact(n - 1) }
let xs: Array<Float like Ix> = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let s = total(xs)
let f = fact(5)
"""

/// A module WITH an element write: no array parameter anywhere may claim
/// `readonly`, not even the one that is only read.
/// Written in the corpus's own spelling (`Idx<3>` inline, not a named alias):
/// a NAMED index type would want a tagged subscript and this block's output
/// would carry three BL4003 warnings that have nothing to do with the facts.
let private elementWriteSource = """
function accumulate_into(x: Array<Float like Idx<3>>, out: mut Array<Float like Idx<3>>) -> Float = {
    out(0) = x(0)
    x(0)
}
let src = [1.0, 2.0, 3.0]
let mut dst = [0.0, 0.0, 0.0]
let r = accumulate_into(src, dst)
"""

/// `^` at each exponent kind (formalism 2.4): Float ^ Int, Float ^ Float,
/// Int ^ Int, all with run-time operands so no literal shortcut applies.
let private powKindsSource = """
let x = 1.1
let n = 7
let a = x ^ n
let b = x ^ 2.5
let i = n ^ n
"""

/// A band over a static struct (structural/06): the fold streams the nest.
let private domainBandFoldSource = """
let static n = 23
let static w = 3
static struct Band {
    i: Int<min=0, max=n-1>,
    j: Int<min=0, max=n-1>
} where abs(i - j) <= w
let total = reduce(method_for(range<Band>) <@> lambda(i, j) -> Float64(i * j) * 0.5, (+))
"""

/// The same band materialized and read by key.
let private domainBandKeyedSource = """
let static n = 23
let static w = 3
static struct Band {
    i: Int<min=0, max=n-1>,
    j: Int<min=0, max=n-1>
} where abs(i - j) <= w
let R = method_for(range<Band>) <@> lambda(i, j) -> i * 100 + j |> compute
let k = 7
let v = R((k, k + 2))
"""

/// One row of membership hoisting's ROUTE TABLE (`memberHoistRoutes`).
type MemberHoistRoute =
    { /// The literal corpus directory.
      Category: string
      /// The file's `// TEST:` name.
      Test: string
      /// The planned hosts, in the order they are bound: each one structure,
      /// built before the host, probed inside it, freed after it. Empty = no
      /// plan: no member call at all, the as-written scan.
      Hosts: string list
      /// How many of those builds are over a COMPOUND set (the C++ build reads
      /// its cardinality and compact buffer).
      CompoundBuilds: int
      /// None: the LLVM lane emits the program. Some reason: the lane refuses
      /// it with exactly this reason (the program runs in the C++ lane) -- an
      /// unrelated refusal, a lowering or a validation error FAILS the row.
      LlvmRefusal: string option }

/// MEMBERSHIP HOISTING'S ROUTE TABLE (round 3 B-T1; Codex diff reviews of
/// W2-1, finding 4 and the second look's NEW 1-2), shared by this block (the
/// LLVM route) and `blade test optimize` (the C++ route): one row per corpus
/// file that calls `contains` -- the corpus/table name sets are asserted
/// EQUAL (`memberHoistRouteCoverage`), so a new unlisted file fails too.
let memberHoistRoutes : MemberHoistRoute list =
    let row cat test hosts compound refusal = { Category = cat; Test = test; Hosts = hosts; CompoundBuilds = compound; LlvmRefusal = refusal }
    let compoundParam = Some "parameter 'a' of 'own_hit' has type Array<Int64 like Idx<?>> -- the llvm lane handles Int64/Float64/Bool/String scalars only"
    [ row "sql-unique-contains" "Contains Present" [] 0 None
      row "sql-unique-contains" "Contains Absent" [] 0 None
      row "sql-unique-contains" "Contains Empty After Mask" [] 0 None
      row "sql-unique-contains" "Contains In Mask Predicate" [ "in_B" ] 0 None
      row "sql-unique-contains" "Unique Of Rank-1 Compound" [ "keep" ] 0 (Some "group_keys over a key array that is not a dense rank-1 axis")
      row "sql-unique-contains" "Member Hoist Small Set Linear" [ "keep"; "outside" ] 0 None
      row "sql-unique-contains" "Member Hoist Literal Set Bitmap" [ "hit"; "next_hit" ] 0 None
      row "sql-unique-contains" "Member Hoist Extreme Keys Hash" [ "hit" ] 0 None
      row "sql-unique-contains" "Member Hoist Empty Compound Set" [ "hit"; "cnt" ] 2 None
      row "sql-unique-contains" "Contains Target Evaluated Once Per Probe" [] 0 None
      row "sql-unique-contains" "Contains Failing Target Over Empty Set (aborts)" [] 0 None
      row "sql-unique-contains" "Member Hoist Failing Target (aborts)" [ "keep" ] 0 None
      row "sql-unique-contains" "Member Hoist Fold Kernel Short Circuit" [ "guarded"; "weighted" ] 0 None
      row "sql-unique-contains" "Member Hoist OMP Map Hash" [ "flags" ] 0 None
      row "sql-unique-contains" "Member Hoist Compound Set" [ "hit" ] 1 None
      row "sql-unique-contains" "Member Hoist Declines Keep Values" [] 0 None
      row "sql-unique-contains" "Member Hoist Semiagg Probe" [ "keep"; "keep2" ] 0 None
      row "sql-unique-contains" "Contains Target Mutates A Dense Set" [] 0 None
      row "sql-unique-contains" "Contains Target Mutates A Compound Set" [] 0 compoundParam
      row "sql-unique-contains" "Member Hoist Fold Kernel Probe" [ "picked" ] 0 None
      row "sql-unique-contains" "Member Hoist Stored Alias Chain" [ "flags_s" ] 0 None
      row "sql-semijoins" "Semijoin Preserves Multiplicity" [ "r" ] 0 None
      row "sql-semijoins" "Antijoin Basic" [ "r" ] 0 None
      row "sql-semijoins" "Antijoin Preserves Multiplicity" [ "r" ] 0 None
      row "sql-semijoins" "Pattern Does Not Fire On Conjunction" [ "r" ] 0 None
      row "sql-semijoins" "Semijoin Float64" [] 0 None
      row "sql-semijoins" "Unique Of Semijoin" [ "inB" ] 0 (Some "the IR node IRUnique in array position -- no arm in the llvm lane")
      row "sql-semijoins" "Semijoin Then Mask" [ "inB" ] 0 None
      // compaction fusion T4c's membership-probe certificate (round 3 W2-2)
      row "index-types" "Compaction fusion T4c: a membership-probe predicate certified by member-hoist's admission" [ "keep" ] 0 None
      row "index-types" "Compaction fusion T4c: a probe membership hoisting declines is not certified" [] 0 None
      row "index-types" "Compaction fusion T4c: the antijoin `!contains` certified, two companions" [ "drop" ] 0 None
      row "memfree" "Memfree Inline Sparse Gather In Every Scalar Position" [] 0 (Some "a sparse-index initializer")
      row "reynolds" "Reynolds Dedup Sees Contains" [] 0 None
      row "reynolds" "Reynolds Antisym Dedup Sees Contains" [] 0 None ]

/// The table covers EXACTLY the corpus's contains programs: every non-reject
/// file of every single-file corpus directory whose source calls `contains(`
/// has a row, and every row names an existing such file. Returns (missing
/// rows, stale rows) as "category / test" names.
let memberHoistRouteCoverage () : string list * string list =
    let onDisk =
        Blade.Tests.Corpus.singleFileCategories ()
        |> List.collect (fun cat ->
            (try Blade.Tests.Corpus.category cat with _ -> [])
            |> List.filter (fun (n, src) -> not (n.EndsWith "(rejects)") && src.Contains "contains(")
            |> List.map (fun (n, _) -> $"{cat} / {n}"))
        |> Set.ofList
    let inTable = memberHoistRoutes |> List.map (fun r -> $"{r.Category} / {r.Test}") |> Set.ofList
    (Set.difference onDisk inTable |> Set.toList, Set.difference inTable onDisk |> Set.toList)

/// Drop `;` comment lines before asserting on emitted text. The module banner
/// NAMES the knobs it documents ("per-instruction `contract`"), so a bare
/// substring search over the whole file would find `contract` in a module that
/// emitted none -- a false pass in the direction that matters least and a
/// false failure in the direction that matters most.
let private instructionsOnly (ll: string) =
    ll.Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun l -> not (l.TrimStart().StartsWith ";"))
    |> String.concat "\n"

// THE ACCUMULATOR, identified structurally (Codex re-re-review): an
// `fadd` whose result is stored back into a slot the function also LOADS
// (`%acc` = load ptr %s ... %n = fadd ... ; store %n, ptr %s). A counter
// bump stores through a freshly computed element pointer, never a loaded
// slot, so it is not mistaken for the fold's chain.
let private accumulatorFadds (ll: string) : string list =
    let lines = (instructionsOnly ll).Split('\n') |> Array.map (fun s -> s.Trim())
    let loadedSlots =
        lines |> Array.choose (fun s ->
            let m = System.Text.RegularExpressions.Regex.Match(s, @"= load double, ptr (%[\w.]+)")
            if m.Success then Some m.Groups.[1].Value else None) |> Set.ofArray
    let stores =
        lines |> Array.choose (fun s ->
            let m = System.Text.RegularExpressions.Regex.Match(s, @"^store double (%[\w.]+), ptr (%[\w.]+)")
            if m.Success then Some (m.Groups.[1].Value, m.Groups.[2].Value) else None)
    lines |> Array.choose (fun s ->
        let m = System.Text.RegularExpressions.Regex.Match(s, @"^(%[\w.]+) = fadd ")
        if m.Success && (stores |> Array.exists (fun (v, slot) -> v = m.Groups.[1].Value && loadedSlots.Contains slot))
        then Some s else None)
    |> List.ofArray
let private flagsOf (fadd: string) =
    let m = System.Text.RegularExpressions.Regex.Match(fadd, @"= fadd ((?:[a-z]+ )*)double")
    if m.Success then m.Groups.[1].Value.Trim() else "?"

let runLlvmFactTests () : BlockResult =
    printHeader "LLVM Fact Layer"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let pass name detail =
        passed <- passed + 1
        resultLine Pass name detail
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [ name ]
        resultLine Fail name detail
    /// `name`: what is asserted. `want`/`deny`: substrings that must appear /
    /// must not. Both lists are checked, so one case states a fact and its
    /// boundary together.
    ///
    /// The emitted PROGRAM NAME is a fixed "facts", never the case name: the
    /// name becomes a string constant in the module (it labels the timing
    /// line), so a case called "contract=off is off" would plant the word
    /// `contract` in its own module and fail itself. That is not hypothetical
    /// -- it is how the first run of this block failed.
    let check name pins source (want: string list) (deny: string list) =
        match emitUnder pins "facts" source with
        | Error e -> fail name e
        | Ok raw ->
            let ll = instructionsOnly raw
            let missing = want |> List.filter (fun s -> not (ll.Contains s))
            let present = deny |> List.filter ll.Contains
            if not missing.IsEmpty then
                fail name ($"""missing from the emitted .ll: {(String.concat " | " missing)}""")
            elif not present.IsEmpty then
                fail name ($"""present in the emitted .ll but must not be: {(String.concat " | " present)}""")
            else
                pass name ($"{want.Length} asserted, {deny.Length} denied")

    printSubHeader "the kill switch"
    // The switch's own rules, asserted directly rather than through emission:
    // a bisection knob that lies about which class it disabled is worse than
    // no knob.
    let switchCase name (value: string option) (expect: (string * bool) list) =
        withEnv [ "BLADE_LLVM_FACTS", value ] (fun () ->
            let wrong =
                expect
                |> List.filter (fun (cls, want) -> EmitLlvm.factEnabled cls <> want)
                |> List.map fst
            if wrong.IsEmpty then pass name "as declared"
            else fail name ($"""wrong answer for: {(String.concat ", " wrong)}"""))
    switchCase "unset licenses every class" None
        [ "fnattrs", true; "paramattrs", true; "fmf", true ]
    switchCase "a bare off kills every class" (Some "off")
        [ "fnattrs", false; "paramattrs", false; "fmf", false ]
    switchCase "0 is a spelling of off" (Some "0")
        [ "fnattrs", false; "paramattrs", false; "fmf", false ]
    switchCase "class:off kills exactly one class" (Some "fmf:off")
        [ "fnattrs", true; "paramattrs", true; "fmf", false ]
    switchCase "two classes at once" (Some "fmf:off,paramattrs:off")
        [ "fnattrs", true; "paramattrs", false; "fmf", false ]
    switchCase "an unrecognized token disables nothing" (Some "banana")
        [ "fnattrs", true; "paramattrs", true; "fmf", true ]

    printSubHeader "closed-form enumerable domains (structural/06, bench G4)"
    // A fold over a `range<R>` of a static struct with linear conjuncts is the
    // affine nest itself -- bounds by compare + select on the outer field, an
    // accumulator, no pool and no table. The differential sweep cannot say
    // this: a refusal there is a quiet SKIP, so the lane's acceptance of the
    // shape is asserted here by name.
    check "a band fold streams the affine nest (no pool, no table)" [] domainBandFoldSource
        [ "icmp sgt i64"; "select i1"; "fadd reassoc nsz contract double" ]
        [ "@.blade.roff"; "@blade_alloc_cells" ]
    // A KEYED read of a materialized domain array addresses it by lex rank
    // through a prefix table of the OUTER field only (one entry per row,
    // never per cell), behind the C++ key index's BL8006 membership abort.
    check "a keyed read ranks through a per-row prefix table" [] domainBandKeyedSource
        [ "@.blade.roff"; "index out of bounds: a key outside the sparse key set" ] []

    printSubHeader "function and parameter attributes"
    check "a non-recursive module claims termination" [] licensedFoldSource
        [ "define i32 @main() #0"
          "attributes #0 = { mustprogress nofree norecurse nosync nounwind willreturn }" ]
        // `noalias` on anything but the allocator's return is the miscompile
        // this lane is most exposed to; assert it appears nowhere else.
        [ "ptr noalias %a"; "define internal noalias" ]
    check "the allocator's return is the one noalias" [] licensedFoldSource
        [ "declare noalias align 64 ptr @blade_alloc_cells(i64 noundef, i64 noundef)" ] []
    check "recursion costs the module its termination claim" [] readonlyAndRecursionSource
        [ "attributes #1 = { nofree norecurse nosync nounwind }"   // the callers
          "attributes #2 = { nofree nosync nounwind }"             // fact itself
          "ptr noundef readonly %a0" ]                             // and it is read-only
        // Group 0's text is the termination claim; nothing may carry it while
        // a possibly-non-terminating callee is in the module.
        [ "norecurse nosync nounwind willreturn" ]
    check "an element write anywhere denies every readonly" [] elementWriteSource
        [ "ptr noundef %a0" ] [ "readonly" ]
    check "paramattrs:off strips the parameter facts" [ "BLADE_LLVM_FACTS", Some "paramattrs:off" ]
        readonlyAndRecursionSource
        [ "attributes #1 ="; "(ptr %a0)" ] [ "noundef"; "readonly"; "noalias" ]
    check "fnattrs:off strips the groups" [ "BLADE_LLVM_FACTS", Some "fnattrs:off" ]
        licensedFoldSource
        [ "define i32 @main() {" ] [ "attributes #" ]

    printSubHeader "fast-math flags"
    // THE DEFAULT IS THE FAST REGIME (2026-10-17, plan-fp-default-fastest.md):
    // `reassoc nsz` on the licensed accumulator and `contract` on every
    // fadd/fmul -- the g++ lane's `-ffp-contract=fast` plus the licensed fold
    // forms. Byte identity with the interpreter is the opt-in in both lanes,
    // BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off.
    check "default emission: licensed accumulator reassociates, everything contracts" [] licensedFoldSource
        [ "fadd reassoc nsz contract double"; "fmul contract double" ] [ "fmul reassoc" ]
    check "the license reaches exactly the accumulator chain"
        [ "BLADE_FP_REASSOC", Some "1"; "BLADE_FP_CONTRACT", Some "off" ] licensedFoldSource
        // The fold's add is tagged; the map's multiply -- same program, same
        // element type, no license -- is not.
        [ "fadd reassoc nsz double"; "fmul double" ] [ "fmul reassoc" ]
    check "an unlicensed kernel stays ordered in the default build"
        [ "BLADE_FP_CONTRACT", Some "off" ] unlicensedFoldSource
        [ "fadd double" ] [ "reassoc" ]
    check "BLADE_FP_REPRO=1 strips reassoc and nothing else"
        [ "BLADE_FP_REPRO", Some "1" ] licensedFoldSource
        [ "fadd contract double"; "fmul contract double" ] [ "reassoc"; "nsz" ]
    check "BLADE_FP_REASSOC=0 is the repro spelling"
        [ "BLADE_FP_REASSOC", Some "0" ] licensedFoldSource
        [ "fadd contract double" ] [ "reassoc"; "nsz" ]
    check "BLADE_FP_REPRO=1 outranks BLADE_FP_REASSOC=1"
        [ "BLADE_FP_REPRO", Some "1"; "BLADE_FP_REASSOC", Some "1" ] licensedFoldSource
        [ "fadd contract double" ] [ "reassoc" ]
    check "the byte-identity build is flag-free"
        [ "BLADE_FP_REPRO", Some "1"; "BLADE_FP_CONTRACT", Some "off" ] licensedFoldSource
        [ "fadd double"; "fmul double" ] [ "reassoc"; "nsz"; "contract" ]
    check "contract is per-instruction"
        [ "BLADE_FP_CONTRACT", Some "fast"; "BLADE_FP_REPRO", Some "1" ] licensedFoldSource
        [ "fmul contract double"; "fadd contract double" ] [ "reassoc" ]
    check "contract=off is off" [ "BLADE_FP_CONTRACT", Some "off" ] licensedFoldSource
        [ "fmul double" ] [ "contract" ]
    check "both licenses compose on the accumulator"
        [ "BLADE_FP_REASSOC", Some "1"; "BLADE_FP_CONTRACT", Some "fast" ] licensedFoldSource
        [ "fadd reassoc nsz contract double"; "fmul contract double" ] []
    check "fmf:off outranks the numeric knobs"
        [ "BLADE_FP_REASSOC", Some "1"; "BLADE_FP_CONTRACT", Some "fast"
          "BLADE_LLVM_FACTS", Some "fmf:off" ] licensedFoldSource
        [ "fadd double" ] [ "reassoc"; "contract" ]

    printSubHeader "the arithmetic contract: `^` by exponent kind"
    // docs/formalism.md 2.4: the exponent of `^` is polymorphic. Float ^ Int
    // is the shim's repeated squaring (blade_fpowi, the twin of
    // blade_arith::fpowi) -- NEVER llvm.powi, whose multiply order LangRef
    // leaves unspecified, whose exponent is i32, and which clang lowers to a
    // libm pow call on x86_64-w64-mingw32 (it differed from the squaring loop
    // in most of a 300k-case sweep). Float ^ Float stays libm (blade_fpow);
    // Int ^ Int stays the exact blade_ipow. The VALUES are pinned bit for bit
    // by corpus basic/501, which the differential below sweeps.
    check "float ^ int squares in the shim, float ^ float is libm" [] powKindsSource
        [ "call double @blade_fpowi("; "call double @blade_fpow("; "call i64 @blade_ipow(" ]
        [ "llvm.powi"; "@pow(" ]

    printSubHeader "shadow frames: kept where a panic is reachable, dropped elsewhere"
    // The twin of the C++ lane's BLADE_FRAME elision (EmitLlvm.resolveFrames):
    // a panic prints `  at <name>` per live frame, so a frame that can see a
    // panic must be stored, and one that cannot is dead weight in a hot loop.
    // Corpus functions/280 and 420-425 pin the printed traces.
    let frameStore = "ptr @blade_frame_depth"
    check "an Int64 division kernel keeps its frame, named as its binding" []
        "let xs = [4, 2, 1]\nlet r = reduce(xs, lambda(a, b) -> a / b)\n"
        [ frameStore; "@blade_frame_stack"; "c\"lambda in r\\00\"" ] [ "__BLADE_FRAME" ]
    check "a Float64 division kernel reaches no panic and has no frame" []
        "let xs = [4.0, 2.0, 1.0]\nlet r = reduce(xs, lambda(a, b) -> a / b)\n"
        [ "fdiv " ] [ frameStore; "lambda in r"; "__BLADE_FRAME" ]
    check "a caller keeps its frame through a callee that panics" []
        "function divide(x: Int64, y: Int64) -> Int64 = x / y\nfunction twice(x: Int64, y: Int64) -> Int64 = divide(x, y) * 2\nlet z: Int64 = 1\nlet b = twice(4, z)\n"
        [ frameStore; "c\"divide\\00\""; "c\"twice\\00\"" ] [ "__BLADE_FRAME" ]
    check "a literal nonzero divisor cannot fault, so no frame" []
        "function half(x: Int64) -> Int64 = x / 2\nlet z: Int64 = 5\nlet b = half(z)\n"
        [ "sdiv i64" ] [ frameStore; "c\"half\\00\""; "__BLADE_FRAME" ]
    // Elision is only sound while EmitLlvm.shimPanicSymbols names every shim
    // export that can reach blade_panic: a call to an unlisted one would look
    // panic-free and lose its frames, silently dropping trace lines. The
    // allocators are the one deliberate exception -- their out-of-memory exit
    // goes untraced, as `new` does in the C++ lane.
    (let name = "every shim export that can panic is in shimPanicSymbols"
     let allocators = set [ "blade_alloc_cells"; "blade_alloc_f64"; "blade_alloc_i64" ]
     let defRe =
         System.Text.RegularExpressions.Regex(
             @"^(?:BLADE_NORETURN\s+)?(static\s+)?[A-Za-z_][A-Za-z0-9_ \*]*?\b(blade_[a-z0-9_]+)\s*\([^;{]*\)\s*\{")
     let lines = EmitLlvm.shimSourceText().Replace("\r\n", "\n").Split('\n')
     // (name, exported, body text up to the next definition)
     let defs = ResizeArray<string * bool * System.Text.StringBuilder>()
     for l in lines do
         let m = defRe.Match l
         if m.Success then defs.Add((m.Groups.[2].Value, not m.Groups.[1].Success, System.Text.StringBuilder(l)))
         elif defs.Count > 0 then
             let (_, _, sb) = defs.[defs.Count - 1]
             sb.Append('\n').Append(l) |> ignore
     let panics = HashSet<string>([ "blade_panic" ])
     let mutable changed = true
     while changed do
         changed <- false
         for (n, _, sb) in defs do
             let body = sb.ToString()
             if not (panics.Contains n)
                && (body.Contains "blade_panic" || panics |> Seq.exists (fun p -> body.Contains(p + "("))) then
                 panics.Add n |> ignore
                 changed <- true
     let exportedPanickers =
         defs |> Seq.filter (fun (n, exported, _) -> exported && panics.Contains n)
         |> Seq.map (fun (n, _, _) -> n) |> Set.ofSeq
     let unlisted = Set.difference (Set.difference exportedPanickers allocators) EmitLlvm.shimPanicSymbols
     let stale = Set.difference EmitLlvm.shimPanicSymbols exportedPanickers
     if defs.Count < 10 then fail name ($"the shim scan found only {defs.Count} definitions -- the pattern no longer matches")
     elif not unlisted.IsEmpty then
         fail name ($"""can panic but is not in shimPanicSymbols (its callers would lose their frames): {(String.concat ", " unlisted)}""")
     elif not stale.IsEmpty then
         fail name ($"""in shimPanicSymbols but the shim never panics there: {(String.concat ", " stale)}""")
     else pass name ($"{exportedPanickers.Count} panicking exports, {defs.Count} definitions scanned"))

    printSubHeader "the --print selection (G12)"
    // The print pass honours BLADE_PRINT exactly as genPrintStatements does:
    // only the selected bindings print. It used to ignore the selection and
    // echo every binding (bench llvm-acceptance-B F1). A selected name the
    // lane would not print -- here a typo -- refuses, so the program reaches
    // the C++ lane, which owns the BL7004 text. The differential cannot see
    // either half: it never sets the selection.
    let printSource = "let x = [1.0, 2.0, 3.0]\nlet y = 4.0\nlet s = reduce(x, (+))\n"
    check "a selection prints only the selected binding" [ "BLADE_PRINT", Some "s" ] printSource
        [ "c\"s\\00\""; "call void @blade_print_f64(" ]
        // An array prints through its own `x = [` label.
        [ "c\"x = [\\00\""; "c\"y\\00\"" ]
    check "no selection prints every binding" [] printSource
        [ "c\"s\\00\""; "c\"x = [\\00\""; "c\"y\\00\"" ] []
    (let name = "a selected name that is no binding refuses to the C++ lane"
     match emitUnder [ "BLADE_PRINT", Some "nope" ] "facts" printSource with
     | Error e when e.Contains "the --print selection names 'nope'" -> pass name "refused"
     | Error e -> fail name ($"refused for another reason: {e}")
     | Ok _ -> fail name "emitted a module that prints nothing for the selected typo")

    printSubHeader "the whole switch"
    // FACTS=off must return the lane to the shape that passed its differential
    // before this layer existed -- the property that makes the switch a usable
    // bisection tool rather than a partial rollback.
    (let name = "FACTS=off emits the pre-fact module"
     let hostile =
         [ "BLADE_FP_REASSOC", Some "1"; "BLADE_FP_CONTRACT", Some "fast"
           "BLADE_LLVM_FACTS", Some "off" ]
     match emitUnder hostile "facts_off" licensedFoldSource with
     | Error e -> fail name e
     | Ok raw ->
         let off = instructionsOnly raw
         let bad =
             [ "attributes #"; "noundef"; "noalias"; "readonly"; "reassoc"; "contract"; " #0" ]
             |> List.filter off.Contains
         if bad.IsEmpty then pass name "no fact text survives"
         else fail name ($"""fact text survived the kill switch: {(String.concat " | " bad)}"""))

    // ---- the omp census (bench gap G11) -------------------------------------
    // The lane has no thread runtime, so a `where omp` licence compiles serial
    // here; the dropped threading half must not be silent. The census phrase is
    // the C++ lane's, as a module comment naming the kernel; the reordering half
    // is spent as `reassoc nsz` (absent in the REPRO build, whose census says
    // so). Negative control: a program with no omp request carries no line.
    (let src = """
type Ix = Idx<9>
let xs: Array<Float like Ix> = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0]
let total = reduce(xs, lambda(a, b) where omp -> a + b, 0.0)
"""
     let census = "; [omp] requested but emitted serial: the LLVM lane has no thread-level form"
     let name = "omp census: a `where omp` fold names its dropped team"
     match emitUnder [] "facts" src, emitUnder [ ("BLADE_FP_REPRO", Some "1") ] "facts" src,
           emitUnder [] "facts" licensedFoldSource with
     | Error e, _, _ | _, Error e, _ | _, _, Error e -> fail name e
     | Ok ll, Ok repro, Ok noOmp ->
         if not (ll.Contains (census + "; a licensed fold is reassociated instead (one whose kernel or deferred map writes keeps its serial chain) (lambda in total)")) then
             fail name "default build: no census comment for the dropped thread team"
         elif not ((instructionsOnly ll).Contains "reassoc nsz") then
             fail name "default build: the licence's reordering half was not spent (no `reassoc nsz`)"
         elif not (repro.Contains (census + ", and BLADE_FP_REPRO=1 keeps every fold serial")) then
             fail name "repro build: the census does not say the fold stays serial"
         elif noOmp.Contains "[omp]" then
             fail name "a program with no omp request carries a census line"
         else pass name "census comment in both builds, none without a request")

    // ---- gap G41: a fold over a WRITING deferred map keeps its serial chain --
    // The map kernel writes a captured counter, so the accumulator's `fadd`
    // carries no fold fast-math (the C++ lane's `licensedFold` declines the
    // same fold to its serial chain); the pure twin keeps `reassoc nsz`.
    (let writing = """
type Ix = Idx<9>
let mut G = [0.0]
let total = reduce(method_for(range<Ix>) <@> lambda(i) -> {
    G(0) = G(0) + 1.0
    G(0) * 1.5
}, (+))
"""
     let pureSrc = """
type Ix = Idx<9>
let total = reduce(method_for(range<Ix>) <@> lambda(i) -> Float64(i) * 1.5, (+))
"""
     let name = "G41: a writing deferred map's fold carries no fold FMF; the pure twin does"
     match emitUnder [] "g41w" writing, emitUnder [] "g41p" pureSrc with
     | Error e, _ | _, Error e -> fail name e
     | Ok w, Ok p ->
         if (instructionsOnly w).Contains "reassoc" then fail name "writing map: the fold accumulator still carries `reassoc`"
         elif not ((instructionsOnly p).Contains "reassoc nsz") then fail name "pure twin: no `reassoc nsz` (the control is vacuous)"
         else pass name "no `reassoc` over the writing map; `reassoc nsz` over the pure one")

    // ---- G41 (Codex re-review, finding 11): EVERY fold entry, not only the
    // fused terminal. An ordinary `reduce` over a CHOICE whose left map writes
    // (lowered as IRReduce; the choice stays a virtual producer), and a fold
    // KERNEL whose inner `reduce` reads a named writing map: neither
    // accumulator may carry fold fast-math; the pure twins keep it. The
    // accumulator's instruction is pinned exactly.
    (let choice (body: string) = $"""
type Ix = Idx<9>
let mut G = [0.0]
let x = method_for(range<Ix>) <@> lambda(i) -> Float64(i) * 0.5 |> compute
let total = reduce((method_for(x) <@> lambda(v) -> {body}) <|> (method_for(x) <@> lambda(v) -> v), (+))
"""
     let kernel (body: string) = $"""
type Ix = Idx<3>
let mut G = [0.0]
let m = method_for(range<Ix>) <@> lambda(i) -> {body}
let k = lambda(a: Float64, b: Float64) where comm(a, b) -> {{
    let z = reduce(m, (+))
    a + b + z
}}
let total = reduce(method_for(range<Ix>) <@> lambda(i) -> Float64(i), k, 0.0)
"""
     let writing = "{\n    G(0) = G(0) + 1.0\n    v * 1.5\n}"
     let writingI = "{\n    G(0) = G(0) + 1.0\n    G(0) * 1.5\n}"
     // Gap G45: a NAMED writing map is evaluated once at its definition, so a
     // fold kernel's inner reduce over it reads STORED cells and may
     // reassociate like any stored array (the counter is pinned by
     // corpus loops/497 and 518); the guard below stays covered by the
     // INLINE writing choice -- not a `let`, so neither G45 nor G50 (which
     // stores a NAMED writing choice) forces it.
     (let name = "G45: a fold kernel's inner reduce over a named writing map reads its stored cells"
      match emitUnder [] "g45k" (kernel writingI) with
      | Error e -> fail name e
      | Ok wl ->
          if not (accumulatorFadds wl |> List.exists (fun f -> flagsOf f = "reassoc nsz contract")) then
              fail name "the inner fold over the stored map has no `reassoc nsz contract` accumulator (was the map left virtual?)"
          else pass name "the named writing map is stored at its let; its fold reassociates")
     for (name, w, p) in [ ("G41: ordinary reduce over a writing choice keeps the serial chain", choice writing, choice "v * 1.5") ] do
         match emitUnder [] "g41c" w, emitUnder [] "g41cp" p with
         | Error e, _ | _, Error e -> fail name e
         | Ok wl, Ok pl ->
             let wAcc = accumulatorFadds wl
             let pAcc = accumulatorFadds pl
             if wAcc.IsEmpty then fail name "writing producer: no accumulator `fadd` identified"
             elif wAcc |> List.exists (fun f -> flagsOf f <> "contract") then
                 fail name ($"""writing producer: an accumulator carries flags other than `contract`: {(String.concat " | " wAcc)}""")
             elif not (pAcc |> List.exists (fun f -> flagsOf f = "reassoc nsz contract")) then
                 fail name "pure twin: no `reassoc nsz contract` accumulator (the control is vacuous)"
             else pass name "the accumulator is `fadd contract` over the writing producer; `fadd reassoc nsz contract` over the pure twin")

    // ---- G41 (Codex re-re-review, finding 12): a NAMED PARTIAL VIEW of a
    // writing deferred value (`let row = m(0)`) was virtual too: folding it ran
    // the writer, so the accumulator had to keep the serial chain. Since gap
    // G45 a named writing MAP, and since gap G50 a named writing CHOICE, is
    // stored at its definition, so a view of either is stored data, like the
    // `|> compute` control, and may reassociate: no NAMED writing value is
    // virtual any more, and the deferred chase through views stays a backstop.
    // The virtual-writer guard is the inline writing choice above ("G41:
    // ordinary reduce over a writing choice keeps the serial chain").
    (let mapRhs (body: string) = $"method_for(range<I>, range<J>) <@> lambda(i, j) -> {body}"
     let view (rhs: string) = $"""
type I = Idx<2>
type J = Idx<3>
let mut G = [0.0]
let m = {rhs}
let row = m((0 : I))
let total = reduce(row, (+), 0.0)
"""
     let writer = "{\n    G(0) = G(0) + 1.0\n    G(0)\n}"
     let zeroRhs = mapRhs "0.0"
     let choiceRhs = "(" + mapRhs writer + ") <|> (" + zeroRhs + ")"
     let name = "G41-12 / G45 / G50: a fold over a named partial view of a writing value reads stored cells"
     match emitUnder [] "g4112w" (view choiceRhs), emitUnder [] "g4112p" (view (mapRhs "Float64(i + j) * 0.5")),
           emitUnder [] "g4112m" (view (mapRhs writer + " |> compute")), emitUnder [] "g4112n" (view (mapRhs writer)) with
     | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> fail name e
     | Ok w, Ok p, Ok m, Ok n ->
         if not (accumulatorFadds w |> List.exists (fun f -> flagsOf f = "reassoc nsz contract")) then fail name "named writing choice (G50: stored at its let): no reassoc accumulator"
         elif not (accumulatorFadds p |> List.exists (fun f -> flagsOf f = "reassoc nsz contract")) then fail name "pure view control: no reassoc accumulator"
         elif not (accumulatorFadds m |> List.exists (fun f -> flagsOf f = "reassoc nsz contract")) then fail name "materialized writing control: no reassoc accumulator (a stored view may reassociate)"
         elif not (accumulatorFadds n |> List.exists (fun f -> flagsOf f = "reassoc nsz contract")) then fail name "named writing map (G45: stored at its let): no reassoc accumulator"
         else pass name "views of the named writing choice (G50) and map (G45), the materialized writer and the pure control: `fadd reassoc nsz contract`")

    // ---- G41 (Codex review W2F-4, finding 19): a view held INSIDE a named
    // tuple (`let pair = (m(0), 0.0); let row = pair[0]`) is still a view of
    // the value behind it. The writer is a named CHOICE, which gap G50 stores
    // at its definition (G45 stores a named writing map), so the tuple-held
    // view is stored data and may reassociate, like the pure control.
    (let tupleView (rhs: string) = $"""
type I = Idx<2>
type J = Idx<3>
let mut G = [0.0]
let m = {rhs}
let pair = (m((0 : I)), 0.0)
let row = pair[0]
let total = reduce(row, (+), 0.0)
"""
     let mapRhs (body: string) = $"method_for(range<I>, range<J>) <@> lambda(i, j) -> {body}"
     let writer = "{\n    G(0) = G(0) + 1.0\n    G(0)\n}"
     let choiceRhs = "(" + mapRhs writer + ") <|> (" + mapRhs "0.0" + ")"
     let name = "G41-19 / G50: a fold over a tuple-held view of a named writing choice reads stored cells"
     match emitUnder [] "g4119w" (tupleView choiceRhs), emitUnder [] "g4119p" (tupleView (mapRhs "Float64(i + j) * 0.5")) with
     | Error e, _ | _, Error e -> fail name e
     | Ok w, Ok p ->
         if not (accumulatorFadds w |> List.exists (fun f -> flagsOf f = "reassoc nsz contract")) then fail name "named writing choice (G50: stored at its let): no reassoc accumulator"
         elif not (accumulatorFadds p |> List.exists (fun f -> flagsOf f = "reassoc nsz contract")) then fail name "pure control: no reassoc accumulator"
         else pass name "tuple-held views of the stored writing choice (G50) and of the pure control: `fadd reassoc nsz contract`")

    // ---- G41 (Codex re-re-review, finding 13): the orbit-class fold
    // evaluates each canonical cell once and weights it, so it must never run
    // over a source whose evaluation writes (it would drop evaluations: the
    // logical walk prints total 10 / counter 4, the orbit arm printed 8 / 3).
    // The writing virtual source takes the logical walk; a pure virtual source
    // keeps the orbit form; a MATERIALIZED writing source is stored data.
    (let orbit (body: string) (forced: string) = $"""
type I = Idx<2>
let x = method_for(range<I>) <@> lambda(i) -> Float64(i) |> compute
let g0 = method_for(x, x) <@> lambda(a: Float64, b: Float64) where comm(a, b) -> a + b |> compute
let mut G = [0.0]
let g = method_for(g0) <@> lambda(v: Float64) -> {body}{forced}
let total = reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> g(i, j), (+), 0.0), (+), 0.0)
let seen = G(0)
"""
     // Gap G45: the NAMED writing source is now evaluated once at its
     // definition -- the same as its `|> compute` twin, counter 3, logical sum
     // 8 in every lane (corpus loops/524) -- so through a name the orbit fold
     // never meets a writing VIRTUAL source; `tryOrbitFold`'s decline stays a
     // backstop (a writing choice never takes the orbit form at all). The
     // named writer and the materialized one must emit the same fold.
     let writer = "{\n    G(0) = G(0) + 1.0\n    G(0)\n}"
     let orbitLine = "the logical sum over the pool of"
     let name = "G41-13 / G45: a named writing orbit source is stored at its let; the pure virtual source keeps the orbit fold"
     match emitUnder [] "g4113w" (orbit writer ""), emitUnder [] "g4113p" (orbit "v * 2.0" ""),
           emitUnder [] "g4113m" (orbit writer " |> compute") with
     | Error e, _, _ | _, Error e, _ | _, _, Error e -> fail name e
     | Ok w, Ok p, Ok m ->
         if not (p.Contains orbitLine) then fail name "pure virtual control: no orbit-class fold (the control is vacuous)"
         elif String.IsNullOrWhiteSpace m then fail name "materialized writing control: no module emitted"
         elif w.Contains orbitLine <> m.Contains orbitLine then
             fail name "the named writing source and its `|> compute` twin took different fold forms (is the named one still virtual?)"
         else pass name "named writer folds like its materialized twin; pure virtual source: orbit-class fold")

    // ---- gap G42: the three-leaf join projections run NATIVELY here ---------
    // corpus tuples/032's shape must be emitted by this lane, not handed back
    // to C++ (its `blade test llvm tuples` pass would otherwise compare the C++
    // lane against itself).
    (let src = """
let x = [1.0, 2.0, 3.0]
let l = method_for(x)
let t = reduce((l <@> lambda(v) -> v) <&!> (l <@> lambda(v) -> 2.0 * v) <&!> (l <@> lambda(v) -> 3.0 * v), (+))
let q1 = t[1]
let q01 = t[0][1]
let ((n0, n1), n2) = t
let (m0, m1, m2) = t
"""
     let name = "G42: tuples/032's three-leaf projections emit natively"
     match emitUnder [] "g42" src with
     | Error e -> fail name ($"the lane refused it: {e}")
     | Ok ll -> if ll.Contains "define" then pass name "native module emitted" else fail name "no function emitted")    // ---- the syrk tile core (microkernel review item M6) --------------------
    // A same-array comm row contraction at or above the crossover (p >= 16,
    // k >= 4) materializes through ONE call to blade_llvm_tiles.c, with the
    // emitter's argument recipe: p, k, the operand pool, ldx = k, the output
    // pool, strict 0, the kc cap, the scale mode + value, the scratch pool and
    // its capacity (EmitLlvm.syrkTileScratchCells). The declaration carries the
    // core's honest attributes. Below the crossover, under BLADE_SYRK_TILE=0,
    // or for a contraction of two DIFFERENT arrays, the nest stays.
    printSubHeader "the syrk tile core (M6)"
    let gramSrc (p: int) (k: int) (tail: string) (ys: string) = $"""
type V = Idx<{p}>
type S = Idx<{k}>
let x = method_for(range<V>, range<S>) <@> lambda(i, s) -> Float64((i * 7 + s * 3) %% 5) - 2.5 |> compute
let y = method_for(range<V>, range<S>) <@> lambda(i, s) -> Float64((i * 5 + s) %% 7) |> compute
let g = method_for(x, {ys}) <@> lambda(a: Array<Float64 like S>, b: Array<Float64 like S>) where comm(a, b) -> prodsum(a, b){tail} |> compute
let t = reduce(method_for(range<V>) <@> lambda(i) -> g(i, i), (+))
"""
    let tileSym = "@" + EmitLlvm.tilesSymbol
    let rowG = Blade.CodeGenExprSupport.syrkRowGroup ()
    let triEdge = Blade.CodeGenExprSupport.triTileEdge ()
    let scratch6131 = EmitLlvm.syrkTileScratchCells 61L (Blade.CodeGenExprSupport.syrkTileKc 31L) rowG
    check "syrk tile: gram's shape (61 x 31) calls the core with the emitter's recipe"
        [ "BLADE_SYRK_TILE", None ] (gramSrc 61 31 "" "x")
        [ $"call void {tileSym}(i64 61, i64 31, ptr "
          ", i64 31, ptr "
          $", i64 0, i64 {Blade.CodeGenExprSupport.syrkTileKcMax}, i64 {rowG}, i64 {triEdge}, i64 0, double 0x3FF0000000000000, ptr "
          $", i64 {scratch6131})"
          $"declare void {tileSym}(i64 noundef, i64 noundef, ptr noundef readonly captures(none), i64 noundef, ptr noundef captures(none),"
          "mustprogress nofree nosync nounwind willreturn memory(argmem: readwrite)" ]
        [ "readnone" ]
    check "syrk tile: the `/ d` tail rides the call as mode 1 and the divisor"
        [ "BLADE_SYRK_TILE", None ] (gramSrc 97 257 " / 4.0" "x")
        [ $"call void {tileSym}(i64 97, i64 257, ptr "; ", i64 1, double 0x4010000000000000, ptr " ]
        []
    check "syrk tile: the `* s` tail rides the call as mode 2"
        [ "BLADE_SYRK_TILE", None ] (gramSrc 24 8 " * 0.5" "x")
        [ $"call void {tileSym}(i64 24, i64 8, ptr "; ", i64 2, double 0x3FE0000000000000, ptr " ]
        []
    check "syrk tile: below the crossover (13 x 31) keeps the nest"
        [ "BLADE_SYRK_TILE", None ] (gramSrc 13 31 "" "x") [] [ tileSym ]
    check "syrk tile: k below the crossover (61 x 3) keeps the nest"
        [ "BLADE_SYRK_TILE", None ] (gramSrc 61 3 "" "x") [] [ tileSym ]
    check "syrk tile: BLADE_SYRK_TILE=0 keeps the nest (the A/B knob)"
        [ "BLADE_SYRK_TILE", Some "0" ] (gramSrc 61 31 "" "x") [] [ tileSym ]
    check "syrk tile: two DIFFERENT arrays are not a same-array contraction"
        [ "BLADE_SYRK_TILE", None ] (gramSrc 61 31 "" "y") [] [ tileSym ]
    check "syrk tile: the fact kill switch strips the declaration's attributes"
        [ "BLADE_SYRK_TILE", None; "BLADE_LLVM_FACTS", Some "off" ] (gramSrc 61 31 "" "x")
        [ $"declare void {tileSym}(i64, i64, ptr, i64, ptr, i64, i64, i64, i64, i64, double, ptr, i64)" ]
        [ "captures(none)"; "memory(argmem" ]

    // ---- the antisym pair and rank-3 simplex tile cores (M7 / M8) ---------
    // The C++ lane's `(|RowContraction|_|)` shapes at or above their
    // crossovers (antisym p >= 24, n >= 8; simplex p >= 24, n >= 4)
    // materialize through ONE call each, with the emitter's recipe: extents,
    // the operand pool and its leading dims, the output pool, the schedule /
    // kc cap, the scale mode + value, the scratch and its capacity. ON under
    // BLADE_FP_REPRO=1 (no reorder licence used); off by knob, below the
    // crossover, and in a module with a `where repro` function.
    printSubHeader "the antisym and simplex tile cores (M7 / M8)"
    let antiSrc (p: int) (n: int) (tail: string) (extra: string) = $"""
type P = Idx<{p}>
type C = Idx<2>
type S = Idx<{n}>
{extra}
let z = method_for(range<P>, range<C>, range<S>) <@> lambda(i, c, s) -> Float64((i * 5 + c * 3 + s) %% 7) - 3.0 |> compute
let l = method_for(z, z) <@> lambda(a: Array<Float64 like C, S>, b: Array<Float64 like C, S>) where anticomm(a, b) -> (prodsum(a((0 : C)), b((1 : C))) - prodsum(b((0 : C)), a((1 : C)))){tail} |> compute
let t = reduce(method_for(range<P>) <@> lambda(j) -> l(0, j), (+))
"""
    let sym3Src (p: int) (n: int) (tail: string) = $"""
type V = Idx<{p}>
type S = Idx<{n}>
let static nn = {n}
let x = method_for(range<V>, range<S>) <@> lambda(i, s) -> Float64((i * 7 + s * 3) %% 5) - 2.0 |> compute
let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like S>, b: Array<Float64 like S>, c: Array<Float64 like S>) where comm(a, b, c) -> prodsum(a, b, c){tail} |> compute
let u = reduce(method_for(range<V>) <@> lambda(k) -> t(0, 0, k), (+))
"""
    let antiSym = "@" + EmitLlvm.antisymTileSymbol
    let sym3Sym = "@" + EmitLlvm.simplexTileSymbol
    let antiOff = [ "BLADE_ANTISYM_TILE", None; "BLADE_FP_REPRO", None ]
    let sym3Off = [ "BLADE_SIMPLEX_TILE", None; "BLADE_FP_REPRO", None ]
    // The row group rides the call and sizes the scratch (unset: the SERIAL
    // regime's default G = 1 -- this lane has no thread runtime; anti_g=0: the
    // cache budget's G = 4 at n = 401 for both schedules); BLADE_TILE_TUNE
    // anti_g=1..8 forces it.
    let antiG6 = fst (Blade.CodeGenExprSupport.antisymRowGroup 61L 401L false false)
    let antiG4 = fst (Blade.CodeGenExprSupport.antisymRowGroup 61L 401L true false)
    check "antisym tile: 61 x 401 calls the core with the emitter's recipe"
        antiOff (antiSrc 61 401 "" "")
        [ $"call void {antiSym}(i64 61, i64 401, ptr "
          ", i64 802, i64 401, i64 0, i64 1, ptr "
          $", i64 0, i64 0, i64 {Blade.CodeGenExprSupport.antisymKcMax ()}, i64 {antiG6}, i64 0, double 0x3FF0000000000000, ptr "
          $", i64 {EmitLlvm.antisymTileScratchCells 61L 401L false antiG6})"
          $"declare void {antiSym}(i64 noundef, i64 noundef, ptr noundef readonly captures(none), i64 noundef,"
          "mustprogress nofree nosync nounwind willreturn memory(argmem: readwrite)" ]
        []
    check "antisym tile: BLADE_ANTISYM_TILE=4x4 passes the paired schedule"
        [ "BLADE_ANTISYM_TILE", Some "4x4"; "BLADE_FP_REPRO", None ] (antiSrc 61 401 "" "")
        [ $"call void {antiSym}(i64 61, i64 401, ptr "; $", i64 1, i64 0, i64 {Blade.CodeGenExprSupport.antisymKcMax ()}, i64 {antiG4}, i64 0, double "
          $", i64 {EmitLlvm.antisymTileScratchCells 61L 401L true antiG4})" ]
        []
    check "antisym tile: the default is row groups of 1 with the G = 1 scratch"
        [ "BLADE_ANTISYM_TILE", None; "BLADE_FP_REPRO", None; "BLADE_TILE_TUNE", None ] (antiSrc 61 401 "" "")
        [ ", i64 1, i64 0, double 0x3FF0000000000000, ptr "
          $", i64 {EmitLlvm.antisymTileScratchCells 61L 401L false 1L})" ]
        []
    check "antisym tile: anti_g=0's cache budget picks row groups of 4 at n = 401, the scratch sized for them"
        [ "BLADE_ANTISYM_TILE", None; "BLADE_FP_REPRO", None; "BLADE_TILE_TUNE", Some "anti_g=0" ] (antiSrc 61 401 "" "")
        [ ", i64 4, i64 0, double 0x3FF0000000000000, ptr "
          $", i64 {EmitLlvm.antisymTileScratchCells 61L 401L false 4L})" ]
        []
    check "antisym tile: BLADE_TILE_TUNE anti_g=1 passes G = 1 and the G = 1 scratch"
        [ "BLADE_ANTISYM_TILE", None; "BLADE_FP_REPRO", None; "BLADE_TILE_TUNE", Some "anti_g=1" ] (antiSrc 61 401 "" "")
        [ ", i64 1, i64 0, double 0x3FF0000000000000, ptr "
          $", i64 {EmitLlvm.antisymTileScratchCells 61L 401L false 1L})" ]
        [ $", i64 {EmitLlvm.antisymTileScratchCells 61L 401L false 4L})" ]
    check "antisym tile: the `/ d` tail rides the call as mode 1 and the divisor"
        antiOff (antiSrc 29 11 " / 4.0" "")
        [ $"call void {antiSym}(i64 29, i64 11, ptr "; ", i64 1, double 0x4010000000000000, ptr " ]
        []
    check "antisym tile: ON under BLADE_FP_REPRO=1 (no reorder licence used)"
        [ "BLADE_ANTISYM_TILE", None; "BLADE_FP_REPRO", Some "1" ] (antiSrc 61 401 "" "")
        [ $"call void {antiSym}(i64 61, i64 401, ptr " ] []
    check "antisym tile: below the crossover (23 x 11) keeps the nest"
        antiOff (antiSrc 23 11 "" "") [] [ antiSym ]
    check "antisym tile: BLADE_ANTISYM_TILE=0 keeps the nest (the A/B knob)"
        [ "BLADE_ANTISYM_TILE", Some "0"; "BLADE_FP_REPRO", None ] (antiSrc 61 401 "" "") [] [ antiSym ]
    check "antisym tile: a `where repro` function in the module keeps the nest"
        antiOff (antiSrc 61 401 "" "function axpy1(a: Float64, b: Float64) where repro = 1.0 + a * b\nlet w = axpy1(1.0, 2.0)\n") [] [ antiSym ]
    check "simplex tile: 29 x 67 / Float64(n) calls the core with the emitter's recipe"
        sym3Off (sym3Src 29 67 " / Float64(nn)")
        [ $"call void {sym3Sym}(i64 29, i64 67, ptr "
          $", i64 67, ptr "
          $", i64 {Blade.CodeGenExprSupport.syrkTileKcMax}, i64 {triEdge}, i64 1, double "
          $", i64 {EmitLlvm.sym3TileScratchCells 29L 67L 67L})"
          $"declare void {sym3Sym}(i64 noundef, i64 noundef, ptr noundef readonly captures(none), i64 noundef, ptr noundef captures(none)," ]
        []
    check "simplex tile: three K blocks (29 x 700) size the scratch at KC = 234"
        sym3Off (sym3Src 29 700 "")
        [ $"call void {sym3Sym}(i64 29, i64 700, ptr "; ", i64 0, double 0x3FF0000000000000, ptr "
          $", i64 {EmitLlvm.sym3TileScratchCells 29L 700L 234L})" ]
        []
    check "simplex tile: ON under BLADE_FP_REPRO=1 (no reorder licence used)"
        [ "BLADE_SIMPLEX_TILE", None; "BLADE_FP_REPRO", Some "1" ] (sym3Src 29 67 "")
        [ $"call void {sym3Sym}(i64 29, i64 67, ptr " ] []
    check "simplex tile: below the crossover (13 x 201) keeps the nest"
        sym3Off (sym3Src 13 201 "") [] [ sym3Sym ]
    check "simplex tile: BLADE_SIMPLEX_TILE=0 keeps the nest (the A/B knob)"
        [ "BLADE_SIMPLEX_TILE", Some "0"; "BLADE_FP_REPRO", None ] (sym3Src 29 67 "") [] [ sym3Sym ]
    check "simplex tile: the inner level first (`prodsum(c, a, b)`) declines"
        sym3Off ((sym3Src 29 67 "").Replace("prodsum(a, b, c)", "prodsum(c, a, b)")) [] [ sym3Sym ]

    // ---- contains and membership hoisting (round 3, B-T1) ------------------
    // The lane has a real IRContains arm (it used to refuse the node, so every
    // `contains` program fell back to C++): a probe in a planned host reads
    // the structure emitMain builds before the host statement through the
    // shared C core (blade_member.h via the shim) -- init / has / fini, the
    // forced representation as init's last argument -- and every other probe
    // is the linear scan with early exit (`has.cond`), the target evaluated
    // once before it. The knob off keeps the scan everywhere.
    printSubHeader "contains and membership hoisting (B-T1)"
    let memberSrc = """
type Row = Idx<16>
let probes: Array<Int64 like Row> = [-3, 0, 1, 2, 7, 12, 30, 31, 44, 45, 46, 57, 58, 60, 61, 62]
let evens = [0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 44, 46, 58, 60]
let hit = mask(probes, lambda(p) -> contains(evens, p))
let one = contains(evens, 44)
"""
    let memberOn = [ "BLADE_MEMBER_HOIST", None ]
    check "member hoist: a planned mask host builds, probes and frees through the shim"
        memberOn memberSrc
        [ "call void @blade_member_init_i64(ptr "; ", i64 20, i64 0)"; "call i32 @blade_member_has_i64(ptr "
          "call void @blade_member_fini_i64(ptr "
          "declare i32 @blade_member_has_i64(ptr noundef, i64 noundef) #10"
          "attributes #10 = { mustprogress nofree nosync nounwind willreturn memory(read) }" ]
        []
    check "member hoist: a top-level contains (no host) is the linear scan, in the same module"
        memberOn memberSrc [ "has.cond"; "icmp eq i64" ] []
    check "member hoist: BLADE_MEMBER_HOIST=hash forces the representation (init's last argument)"
        [ "BLADE_MEMBER_HOIST", Some "hash" ] memberSrc [ ", i64 20, i64 3)" ] []
    check "member hoist: BLADE_MEMBER_HOIST=0 keeps the linear scan everywhere"
        [ "BLADE_MEMBER_HOIST", Some "0" ] memberSrc [ "has.cond" ] [ "blade_member_" ]
    check "contains over a Float64 set: IEEE == in the scan"
        memberOn "let fs = [1.5, -0.0, 2.5]\nlet z = contains(fs, 0.0)\n" [ "fcmp oeq double" ] [ "blade_member_" ]
    // THE ROUTE of every contains program in the corpus (Codex diff reviews
    // of W2-1, finding 4 and the second look's NEW 1-2): the table covers the
    // corpus's contains programs EXACTLY (no missing, no stale row); a row
    // whose program the lane emits has exactly its planned hosts -- each
    // `init`'s report naming that host, in binding order -- with init ->
    // report -> every probe of the slot -> fini; a row with no plan carries
    // no member call at all; a refusing row is refused with EXACTLY its
    // recorded reason. A lowering or validation error, or an exception, fails
    // the row whatever it says.
    (let (missing, stale) = memberHoistRouteCoverage ()
     let name = "member hoist route table: the corpus's contains programs, exactly"
     if missing.IsEmpty && stale.IsEmpty then pass name ($"{memberHoistRoutes.Length} rows = the corpus's contains programs")
     else fail name ($"""missing rows: [{String.concat "; " missing}]; stale rows: [{String.concat "; " stale}]"""))
    (for r in memberHoistRoutes do
        let name = $"member hoist route (llvm): {r.Category} `{r.Test}` -- {r.Hosts.Length} build(s)"
        match (try Blade.Tests.Corpus.category r.Category |> List.tryFind (fun (n, _) -> n = r.Test) with _ -> None) with
        | None -> fail name "the corpus file is missing (renamed?)"
        | Some (_, src) ->
            let lowered =
                try
                    withEnv defaultEmissionEnv (fun () ->
                        match fst (Lowering.lowerCaptured src) with
                        | Error e -> Error ("lowering refused: " + e)
                        | Ok ir ->
                            match IRValidate.validateIR ir with
                            | Error errs -> Error ("IR validation refused: " + String.concat "; " errs)
                            | Ok ir -> Ok (EmitLlvm.tryEmitProgramNamed "facts" ir))
                with ex -> Error ("raised: " + ex.Message)
            match lowered, r.LlvmRefusal with
            | Error e, _ -> fail name e
            | Ok (Error why), Some want ->
                if why.Contains want then pass name ($"refused with the recorded reason ({want})")
                else fail name ($"refused for ANOTHER reason: {why} (recorded: {want})")
            | Ok (Ok _), Some want -> fail name ($"emitted, but the table records the refusal `{want}` (update the table)")
            | Ok (Error why), None -> fail name ("the lane refused: " + why)
            | Ok (Ok raw), None ->
                let lines = (instructionsOnly raw).Split('\n')
                let initRe = System.Text.RegularExpressions.Regex(@"call void @blade_member_init_i64\(ptr (%[A-Za-z0-9_.]+),")
                let inits = lines |> Array.indexed |> Array.choose (fun (i, l) -> let m = initRe.Match l in if m.Success then Some (i, m.Groups.[1].Value) else None)
                // the host each report names: its string global's bytes
                let strOf (sym: string) =
                    lines |> Array.tryPick (fun l ->
                        let m = System.Text.RegularExpressions.Regex.Match(l, "^" + System.Text.RegularExpressions.Regex.Escape sym + @" = .*c""([^""]*)\\00""")
                        if m.Success then Some m.Groups.[1].Value else None)
                let slotHost (i, slot: string) : string option =
                    let has = lines |> Array.indexed |> Array.filter (fun (_, l) -> l.Contains($"@blade_member_has_i64(ptr {slot},")) |> Array.map fst
                    let fini = lines |> Array.tryFindIndex (fun l -> l.Contains($"call void @blade_member_fini_i64(ptr {slot})"))
                    let noteRe = System.Text.RegularExpressions.Regex("call void @blade_member_note_llvm\\(ptr " + System.Text.RegularExpressions.Regex.Escape slot + @", ptr (@[A-Za-z0-9_.]+),")
                    let note = lines |> Array.indexed |> Array.tryPick (fun (k, l) -> let m = noteRe.Match l in if m.Success then Some (k, m.Groups.[1].Value) else None)
                    match fini, note with
                    | Some f, Some (n, hostSym) when has.Length > 0 && n > i && Array.min has > n && Array.max has < f -> strOf hostSym
                    | _ -> None
                let anyMember = lines |> Array.exists (fun l -> l.Contains "blade_member_")
                if r.Hosts.IsEmpty then
                    if anyMember then fail name "a member call in a program with no plan" else pass name "no structure, the scan"
                else
                    let hosts = inits |> Array.map slotHost |> List.ofArray
                    if hosts |> List.exists Option.isNone then fail name "an init / report / probe / fini out of order (or a slot never probed)"
                    else
                        let got = hosts |> List.map Option.get
                        if got <> r.Hosts then fail name ($"""hosts [{String.concat ", " got}], want [{String.concat ", " r.Hosts}]""")
                        else pass name ($"""{String.concat ", " got}: init -> report -> probes -> fini"""))

    printSubHeader "call argument order (gaps G57 / G79)"
    // G79: an ORDINARY call (no `|> compute` argument) with two failing
    // arguments evaluates them LEFT TO RIGHT in this lane -- the checked cast
    // (BL8006) before the later division by zero (BL8013) -- on the
    // out-of-line and the capturing-inline path alike (the interpreter agrees;
    // the C++ lane's order is unspecified there, g++ runs right to left: the
    // open G79 row). Run, not read: the emitted guard order is not the
    // executed order. Skipped without clang.
    let mutable skipped = 0
    let ordinaryCall (inlined: bool) =
        "type Row = Idx<1>\n"
        + "let a: Array<Int64 like Row> = [1]\n"
        + "let z: Array<Int64 like Row> = [0]\n"
        + "let base: Array<Int64 like Row> = [100]\n"
        + (if inlined then "let f = lambda(k: Int64, m: Int64) -> k + m + base(0)\n"
           else "function f(k: Int64, m: Int64) -> Int64 = k + m\n")
        + "let r = f(a((a(0) : Row)), 1 / z(0))\n"
    let firstAbort name (source: string) (want: string) =
        match Build.resolveClang () with
        | None -> skipped <- skipped + 1; resultLine Skip name "no clang (BLADE_LLVM_CLANG)"
        | Some _ ->
        match emitUnder [] "facts" source with
        | Error e -> fail name e
        | Ok ll ->
            let dir = Path.Combine("generated_cpp_tests", "llvm_facts")
            Directory.CreateDirectory dir |> ignore
            EmitLlvm.deployShim dir
            let llFile = Path.Combine(dir, "facts_" + name + ".ll")
            File.WriteAllText(llFile, ll)
            match Build.compileLlvmProgram llFile dir with
            | Error e when e.StartsWith "Skipped:" -> skipped <- skipped + 1; resultLine Skip name e
            | Error e -> fail name ("llvm compile: " + (if e.Length > 300 then e.Substring(0, 300) else e))
            | Ok exe ->
                match runExeCapture exe with
                | Error e -> fail name e
                | Ok (code, _, err) ->
                    let first = err.Replace("\r\n", "\n").Split('\n') |> Array.tryFind (fun l -> l.StartsWith "error[")
                    match first with
                    | Some l when code <> 0 && l.StartsWith ("error[" + want + "]") -> pass name l
                    | other -> fail name ($"exit {code}, first error {other}, wanted {want}")
    firstAbort "g79_ordinary_call_left_to_right_out_of_line" (ordinaryCall false) "BL8006"
    firstAbort "g79_ordinary_call_left_to_right_inlined" (ordinaryCall true) "BL8006"
    // G57: the ordered path (a call holding an explicit compute and a bare
    // deferred argument) needs the call to be its statement's WHOLE value in
    // main; nested in an expression it is refused by name, fail closed.
    let nestedOrdered =
        "type Row = Idx<1>\n"
        + "let a: Array<Int64 like Row> = [1]\n"
        + "let z: Array<Int64 like Row> = [0]\n"
        + "let bad = method_for(z) <@> lambda(v) -> 1 / v\n"
        + "let good = method_for(a) <@> lambda(v) -> v + 1\n"
        + "function h(k: Int64, p: Array<Int64 like Row>, q: Array<Int64 like Row>) -> Int64 = k + p(0) + q(0)\n"
        + "let r = 1 + h(a((a(0) : Row)), bad, good |> compute)\n"
    (match emitUnder [] "facts" nestedOrdered with
     | Error e when e.Contains "the ordered argument path needs the call to be a statement's whole value" ->
         pass "g57_ordered_call_nested_in_an_expression_refuses" "refused by name"
     | Error e -> fail "g57_ordered_call_nested_in_an_expression_refuses" ("refused for another reason: " + e)
     | Ok _ -> fail "g57_ordered_call_nested_in_an_expression_refuses" "emitted (the hoisted force would run before the cast)")
    // G75: a function body evaluates a deferred module map privately; a call
    // from main AFTER main forced the map is refused by name (the forced
    // snapshot is the class value, which the private evaluation would not
    // read), and one BEFORE any force is taken.
    let g75 (forceFirst: bool) =
        "type Row = Idx<3>\n"
        + "let z: Array<Int64 like Row> = [1, 2, 3]\n"
        + "let lazy = method_for(z) <@> lambda(v) -> v * 10\n"
        + "function at(n: Int64) -> Int64 = lazy(1) + n\n"
        + (if forceFirst then "let w = lazy(0) + lazy(2)\n" else "")
        + "let one = at(1)\n"
    (match emitUnder [] "facts" (g75 true) with
     | Error e when e.Contains "reads the deferred module binding 'lazy', which main forced before this call" ->
         pass "g75_call_after_main_forced_the_map_refuses" "refused by name"
     | Error e -> fail "g75_call_after_main_forced_the_map_refuses" ("refused for another reason: " + e)
     | Ok _ -> fail "g75_call_after_main_forced_the_map_refuses" "emitted")
    (match emitUnder [] "facts" (g75 false) with
     | Ok ll when ll.Contains "define internal i64 @bl_at_" -> pass "g75_function_reads_unforced_map_privately" "taken (out-of-line `at`)"
     | Ok _ -> fail "g75_function_reads_unforced_map_privately" "no out-of-line `at` in the emitted .ll"
     | Error e -> fail "g75_function_reads_unforced_map_privately" e)

    printFooter "LLVM Fact Layer"
        [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = "LLVM Fact Layer"; Passed = passed; Failed = failed; Skipped = skipped
      FailedNames = failedNames }

// ---------------------------------------------------------------------------
// Block 2b: the syrk tile core, raw bits across the lanes (M6)
// ---------------------------------------------------------------------------
//
// The LLVM lane's tile core (blade_llvm_tiles.c, compiled by clang through
// Build.buildLlvmTilesObject -- the object the lane links) and the C++ lane's
// tile (blade_pgemm::dsyrk_tri, compiled by g++ with Build.optFlags) must
// produce the SAME BITS for the same operand, in both contraction modes. One
// polyglot source (src/cpp/llvm_tiles_rawbit.c) is built both ways; each build
// self-checks every cell bitwise against its own serial chain, and the runner
// compares the two builds' per-case hashes of the raw packed pool. Both modes
// build into ONE directory, which also asserts the policy keying: the =off and
// =fast tile objects must be different files.

/// A sample block under a cap, the header's balanced rule (tri_kc).
let private balancedKc (k: int64) (cap: int64) : int64 =
    if k <= 0L then 0L
    else
        let nkb = (k + cap - 1L) / cap
        (k + nkb - 1L) / nkb

let runLlvmTilesRawBitTests () : BlockResult =
    let blockName = "LLVM Tiles Raw-Bit"
    printHeader "LLVM Tile Core vs C++ Tile (raw bits, both contraction modes)"
    let skipBlock (why: string) =
        printfn "  Skipped: %s" why
        printFooter blockName [ "0 passed"; "0 failed"; "1 skipped" ]
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    match Build.resolveClang () with
    | None -> skipBlock "no clang found (set BLADE_LLVM_CLANG)."
    | Some _ when not Build.capabilities.Value.HasGpp -> skipBlock "requires g++ for the C++ lane's tile."
    | Some clang ->
    let cppDir = Path.Combine(AppContext.BaseDirectory, "cpp")
    let testSrc = Path.Combine(cppDir, "llvm_tiles_rawbit.c")
    if not (File.Exists testSrc) then skipBlock $"llvm_tiles_rawbit.c not deployed at {testSrc}"
    else
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let pass name detail =
        passed <- passed + 1
        resultLine Pass name detail
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [ name ]
        resultLine Fail name detail
    let work = Path.Combine(Path.GetTempPath(), "blade-llvm-tiles-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory work |> ignore
    // The cases, from the EMITTER's numbers: the kc cap it passes, and the
    // scratch it allocates for that cap. (p, k, cap, mode, scale, data, short
    // scratch?) -- smoke sizes around every edge the driver has: the
    // crossover itself, ragged row (p mod 6) and column (p mod 8) panels,
    // several K blocks (k > 336, and a cap of 7), a second NC column block
    // (p = 2057), both scale tails, and the core's scalar fallback.
    // (p, k, cap, row panels per group, mode, scale, data, scratch kind):
    // the group (BLADE_TILE_TUNE syrk_g) is the driver's visiting order and
    // sizes the A scratch; every group size the drivers accept (1..8) appears,
    // with groups straddling the ragged last row panel and the top-aligned
    // ragged first column panel.
    // ... and the edge tiles' kloop (BLADE_TILE_TUNE tri_edge, blade_tiles.h
    // blade_tri_tile_edge): every case under BOTH 0 (the full 6 x 8 kloop)
    // and 1 (live accumulators only), plus every p from 13 to 30 (p mod 8 =
    // 0..7: the top-aligned first column panel holds nr = 1..8 lanes; p mod 6
    // = 0..5: ragged row panels) and the simplex bench's p = 61 / 171.
    // SCRATCH KINDS: 0 = the emitter's exact size (the tile route, asserted),
    // 1 = none (NULL), 2 = one cell short of the core's need (both the scalar
    // route, asserted where a route is observable).
    let baseCases =
        [ 16, 4, 336, 1, 0, 1.0, 0, 0
          17, 5, 336, 4, 0, 1.0, 1, 0
          23, 31, 336, 2, 0, 1.0, 2, 0
          24, 8, 336, 4, 2, 0.37, 0, 0
          29, 1001, 336, 4, 0, 1.0, 1, 0
          61, 31, 336, 4, 0, 1.0, 1, 0      // the bench's smoke size
          61, 257, 336, 4, 1, 4.0, 0, 0
          61, 701, 336, 8, 0, 1.0, 2, 0
          97, 257, 336, 1, 1, 3.0, 1, 0
          97, 513, 336, 4, 2, 0.1, 3, 0
          130, 337, 336, 5, 0, 1.0, 4, 0
          29, 50, 7, 4, 0, 1.0, 0, 0
          31, 23, 5, 2, 1, 7.0, 2, 0
          31, 23, 5, 3, 1, 0.0, 0, 0        // divide by zero, three K blocks: the tail waits for the last
          2057, 5, 336, 4, 0, 1.0, 0, 0
          40, 20, 336, 4, 0, 1.0, 2, 1
          40, 20, 336, 4, 0, 1.0, 2, 2 ]
    let edgeShapes =
        [ for p in 13 .. 30 do
            yield p, 17, 336, 4, 0, 1.0, 2, 0
            yield p, 19, 7, 2, 1, 3.0, 0, 0
          for p in [ 61; 171 ] do
            yield p, 29, 336, 4, 0, 1.0, 2, 0 ]
    let cases =
        [ for edge in [ 0; 1 ] do
            for (p, k, cap, g, mode, scale, data, sk) in baseCases @ edgeShapes do
                yield p, k, cap, g, edge, mode, scale, data, sk ]
    let caseLines =
        cases |> List.map (fun (p, k, cap, g, edge, mode, scale, data, sk) ->
            let kc =
                if cap = 336 then Blade.CodeGenExprSupport.syrkTileKc (int64 k)
                else balancedKc (int64 k) (int64 cap)
            let exact = EmitLlvm.syrkTileScratchCells (int64 p) kc (int64 g)
            let scratch = match sk with 0 -> exact | 1 -> 0L | _ -> exact - 1L
            let route = if p = 0 || k = 0 then -1 elif sk = 0 then 1 else 0
            let sc = scale.ToString("R", Globalization.CultureInfo.InvariantCulture)
            let sc = if sc.Contains "." || sc.Contains "E" then sc else sc + ".0"
            $"    {{ {p}, {k}, {cap}, {g}, {edge}, {scratch}, {route}, {mode}, {sc}, {data} }},")
    if Blade.CodeGenExprSupport.syrkTileKcMax <> 336L then
        fail "kc cap" $"the cases assume the emitter's cap 336; syrkTileKcMax is {Blade.CodeGenExprSupport.syrkTileKcMax}"
    let fmtScale (scale: float) =
        let sc = scale.ToString("R", Globalization.CultureInfo.InvariantCulture)
        if sc.Contains "." || sc.Contains "E" then sc else sc + ".0"
    // THE ANTISYM PAIR (M7): (p, n, comps, cx, cy, variant, nc cap, mode,
    // scale, data, short scratch?). Every p from 0 to 30 over short chains
    // (the strict triangle's empty last row; p = 0 / 1 empty pools), every
    // p mod 24 over two periods (ragged row panels mod 6 / 4, column panels
    // mod 8 / 4), several column blocks (nc cap 16), the component pairs
    // (0,1) (1,0) (2,0) (1,1), both scale tails incl. a divide by zero, long
    // chains around the simplex's KC and the bench's own shape, every data
    // class, both schedules, and the core's scalar fallback.
    // ... and the sample-block cap inside a tile visit (BLADE_TILE_TUNE
    // anti_kc; 0 = the whole chain in one block): forced small (5, 7, 16:
    // ragged chains of several blocks, the P / Q partials spilled and
    // reloaded exactly), the tuned sizes over the bench chain, a cap above n.
    // ... and the ROW GROUP (BLADE_TILE_TUNE anti_g): every case above takes
    // one of g = 1, 2, 3, 4, 8 in turn (so every shape class meets every
    // group size), then a dedicated block runs every g over group tails (p mod
    // 6g / 4g), several column blocks (nc cap 16), p <= 1 and n = 0, and the
    // route cases: the emitter's exact scratch FOR THAT g (the tile, asserted),
    // one cell short (the scalar chain, asserted), and a scratch sized for
    // g = 1 handed to a g = 4 call -- the emitter/core mismatch the core
    // answers silently with its scalar chain.
    // (p, n, comps, cx, cy, variant, nc cap, kc cap, mode, scale, data, scratch kind)
    let antiBase =
        [ for v in [ 0; 1 ] do
            for p in 0 .. 30 do
                for n in [ 0; 1; 2; 3; 5; 8; 13; 17 ] do
                    yield p, n, 2, 0, 1, v, 0, 0, 0, 1.0, (if (p + n) % 2 = 0 then 0 else 2), 0
                    yield p, n, 2, 0, 1, v, 0, 7, 0, 1.0, 2, 0
            for n in 0 .. 17 do
                for d in [ 0; 3; 4; 5 ] do
                    yield 9 + n, n, 2, 0, 1, v, 0, 0, 0, 1.0, d, 0
                    yield 9 + n, n, 2, 1, 0, v, 0, 5, 0, 1.0, d, 0
            yield 37, 23, 3, 2, 0, v, 0, 0, 0, 1.0, 2, 0
            yield 37, 23, 3, 1, 1, v, 0, 7, 0, 1.0, 2, 0
            for p in [ 1; 2; 13; 29; 61 ] do
                yield p, 41, 2, 0, 1, v, 0, 0, 1, 401.0, 0, 0
                yield p, 41, 2, 0, 1, v, 0, 16, 2, 0.25, 2, 0
                yield p, 41, 2, 0, 1, v, 0, 0, 1, 0.0, 0, 0
                yield p, 0, 2, 0, 1, v, 0, 7, 1, 0.0, 0, 0      // (+0 - +0) / 0
            for p in 24 .. 71 do
                yield p, 19, 2, 0, 1, v, 0, 0, 0, 1.0, 2, 0
                yield p, 33, 2, 0, 1, v, 16, 16, 0, 1.0, 0, 0    // several column blocks AND sample blocks
            for p in [ 101; 193 ] do
                for n in [ 335; 337; 401; 600 ] do
                    for kc in [ 0; 100; 128 ] do
                        yield p, n, 2, 0, 1, v, 0, kc, 0, 1.0, 2, 0
            yield 24, 8, 2, 0, 1, v, 0, 0, 0, 1.0, 1, 0          // the crossover
            yield 40, 20, 2, 0, 1, v, 0, 7, 0, 1.0, 2, 1          // no scratch: the scalar chain
            yield 61, 401, 2, 0, 1, v, 0, 0, 1, 7.0, 0, 1
            yield 61, 401, 2, 0, 1, v, 0, 0, 1, 7.0, 0, 2 ]      // one cell short: the scalar chain
    let groupCycle = [| 1; 2; 3; 4; 8 |]
    // (..., g, scratch kind) -- kind 3 = sized for g = 1 whatever the call's g
    let antiGroupCases =
        [ for g in groupCycle do
            for v in [ 0; 1 ] do
                for p in [ 0; 1; 2; 7; 23; 24; 25; 29; 31; 47; 48; 49; 53; 61; 97 ] do
                    yield p, 13, 2, 0, 1, v, 0, 0, g, 0, 1.0, 2, 0
                    yield p, 0, 2, 0, 1, v, 0, 0, g, 1, 0.0, 0, 0
                    yield p, 21, 2, 1, 0, v, 16, 5, g, 2, 0.5, 0, 0      // column blocks of 16, sample blocks
                yield 61, 401, 2, 0, 1, v, 0, 0, g, 0, 1.0, 1, 0        // the exact scratch for this g: the tile
                yield 61, 401, 2, 0, 1, v, 0, 0, g, 0, 1.0, 1, 2        // one short: the scalar chain
          for v in [ 0; 1 ] do
              yield 61, 401, 2, 0, 1, v, 0, 0, 4, 0, 1.0, 1, 3          // sized for g = 1, called with g = 4
          for g in [ 1; 4 ] do
              yield 1201, 401, 2, 0, 1, 0, 0, 0, g, 0, 1.0, 1, 0        // the bench's full shape
              yield 1201, 401, 2, 0, 1, 0, 0, 128, g, 0, 1.0, 1, 0 ]    // ... in blocks of 101
    let antiCases =
        (antiBase |> List.mapi (fun i (p, n, comps, cx, cy, v, ncmax, kcmax, mode, scale, data, sk) ->
            (p, n, comps, cx, cy, v, ncmax, kcmax, groupCycle.[i % groupCycle.Length], mode, scale, data, sk)))
        @ antiGroupCases
    let antiLines =
        antiCases |> List.map (fun (p, n, comps, cx, cy, v, ncmax, kcmax, g, mode, scale, data, sk) ->
            let exact = EmitLlvm.antisymTileScratchCells (int64 p) (int64 n) (v = 1) (int64 g)
            let scratch =
                match sk with
                | 0 -> exact
                | 1 -> 0L
                | 2 -> exact - 1L
                | _ -> EmitLlvm.antisymTileScratchCells (int64 p) (int64 n) (v = 1) 1L
            let route = if p <= 1 || n = 0 then -1 elif sk = 0 then 1 else 0
            $"    {{ {p}, {n}, {comps}, {cx}, {cy}, {v}, {ncmax}, {kcmax}, {g}, {scratch}, {route}, {mode}, {fmtScale scale}, {data} }},")
    // THE RANK-3 SIMPLEX (M8): (p, n, kc cap, mode, scale, data, short?).
    // Every p from 0 to 30 (the short rows near k = p; every equality
    // stratum i = j = k, i = j < k, i < j = k, i < j < k), every p mod 24 with
    // FORCED K blocks (caps 7 and 16: the stored unscaled partial reloaded,
    // then the scale once), KC - 1 / KC / KC + 1 / 2 KC + 1 around the
    // emitter's cap, both tails incl. a divide by zero, every data class
    // (6 = subnormal TRIPLE products), the bench shape, the scalar fallback.
    // ... every case under BOTH edge kloops (BLADE_TILE_TUNE tri_edge 0 / 1):
    // p = 0..30 covers p mod 8 = 0..7 (the top-aligned first k panel holds
    // nr = 1..8 lanes, nr = 1..4 the left-only family) and every mr tail
    // 1..6, plus p = 61 / 171 (the bench's p = 171: 3-lane first panel).
    // Scratch kinds as the syrk cases' (0 exact, 1 none, 2 one short).
    let sym3Base =
        [ for p in 0 .. 30 do
            for n in [ 0; 1; 2; 3; 5; 8; 13; 17 ] do
                yield p, n, 336, 0, 1.0, (if (p + n) % 2 = 0 then 0 else 2), 0
          for n in 0 .. 17 do
            for d in [ 0; 3; 6; 5 ] do
                yield 9 + n, n, 336, 0, 1.0, d, 0
          for p in [ 1; 2; 13; 29; 61 ] do
            yield p, 67, 336, 1, 201.0, 0, 0
            yield p, 67, 336, 2, 0.125, 2, 0
            yield p, 67, 336, 1, 0.0, 0, 0
            yield p, 0, 336, 1, 0.0, 0, 0                     // +0 / 0
          for p in 24 .. 71 do
            yield p, 19, 7, 1, 3.0, 2, 0                      // 3 K blocks of 7 / 6 / 6
            yield p, 33, 16, 0, 1.0, 0, 0                     // 3 blocks of 11
          for n in [ 335; 336; 337; 673 ] do
            for p in [ 29; 37 ] do
                yield p, n, 336, 1, 201.0, 2, 0
          for p in [ 97; 101; 171 ] do
            yield p, 201, 336, 1, 201.0, 1, 0                 // 171 x 201: the bench shape
          yield 171, 23, 7, 0, 1.0, 2, 0                      // the bench's p, 4 K blocks
          yield 24, 4, 336, 0, 1.0, 1, 0                      // the crossover
          yield 29, 67, 336, 1, 67.0, 0, 1                    // no scratch: the scalar chain
          yield 29, 67, 336, 1, 67.0, 0, 2                    // one cell short: the scalar chain
          yield 61, 201, 336, 0, 1.0, 2, 1 ]
    let sym3Cases =
        [ for edge in [ 0; 1 ] do
            for (p, n, cap, mode, scale, data, sk) in sym3Base do
                yield p, n, cap, edge, mode, scale, data, sk ]
    let sym3Lines =
        sym3Cases |> List.map (fun (p, n, cap, edge, mode, scale, data, sk) ->
            let kc =
                if cap = 336 then Blade.CodeGenExprSupport.syrkTileKc (int64 n)
                else balancedKc (int64 n) (int64 cap)
            let exact = EmitLlvm.sym3TileScratchCells (int64 p) (int64 n) kc
            let scratch = match sk with 0 -> exact | 1 -> 0L | _ -> exact - 1L
            let route = if p = 0 || n = 0 then -1 elif sk = 0 then 1 else 0
            $"    {{ {p}, {n}, {cap}, {edge}, {scratch}, {route}, {mode}, {fmtScale scale}, {data} }},")
    let totalCases = cases.Length + antiCases.Length + sym3Cases.Length
    File.WriteAllText(Path.Combine(work, "llvm_tiles_cases.h"),
                      "static const tile_case tile_cases[] = {\n" + String.concat "\n" caseLines + "\n};\n"
                      + "static const anti_case anti_cases[] = {\n" + String.concat "\n" antiLines + "\n};\n"
                      + "static const sym3_case sym3_cases[] = {\n" + String.concat "\n" sym3Lines + "\n};\n")
    File.Copy(testSrc, Path.Combine(work, "llvm_tiles_rawbit.c"), true)
    EmitLlvm.deployShim work
    let exeExt = if Platforms.os = Platforms.Windows then ".exe" else ".out"
    let runText (exe: string) (args: string) =
        let psi = ProcessStartInfo(exe, args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- work
        use pr = Process.Start psi
        let o = Blade.Runtime.readToEndOffPool pr.StandardOutput
        let e = Blade.Runtime.readToEndOffPool pr.StandardError
        if pr.WaitForExit 300000 then (Some pr.ExitCode, o.Result.Replace("\r\n", "\n"), e.Result)
        else
            (try pr.Kill true with _ -> ())
            (None, o.Result, e.Result)
    let caseHashes (out: string) =
        out.Split('\n') |> Array.filter (fun l -> l.StartsWith "CASE ") |> Array.toList
    let objects = ResizeArray<string>()
    let modeHashes = ResizeArray<string list>()
    for (mode, contract) in [ "off", Some "off"; "fast", None ] do
        withEnv [ "BLADE_FP_CONTRACT", contract; "BLADE_LLVM_FACTS", None ] (fun () ->
            let fmaRef = if mode = "fast" then " -DBLADE_TEST_FMA_REF" else ""
            match Build.buildLlvmTilesObject clang work with
            | Error e -> fail $"tile object contract={mode}" e
            | Ok (obj, _) ->
                objects.Add obj
                let src = Path.Combine(work, "llvm_tiles_rawbit.c")
                let llvmExe = Path.Combine(work, $"tiles_llvm_{mode}{exeExt}")
                let cppExe = Path.Combine(work, $"tiles_cpp_{mode}{exeExt}")
                // The C side: the lane's tile flags (same contraction) for the
                // reference chain too, linked with the REAL object.
                let llvmBuild =
                    runText clang ($"""-x c -std=c11 {(Build.llvmOptFlags ())}{(EmitLlvm.tilesContractFlag ())}{fmaRef} -I "{work}" -o "{llvmExe}" "{src}" -x none "{obj}" """)
                let cppBuild =
                    runText "g++" ($"""-x c++ -std=c++17 {(Build.optFlags ())}{fmaRef} -I "{work}" -I "{cppDir}" -o "{cppExe}" "{src}" """)
                match llvmBuild, cppBuild with
                | (Some 0, _, _), (Some 0, _, _) ->
                    let (lc, lo, _) = runText llvmExe ""
                    let (cc, co, _) = runText cppExe ""
                    let summary (o: string) =
                        o.Split('\n') |> Array.tryFind (fun l -> l.StartsWith "LLVM TILES RAWBIT") |> Option.defaultValue "(no summary)"
                    let firstFails (o: string) =
                        o.Split('\n') |> Array.filter (fun l -> l.StartsWith "[FAIL]") |> Array.truncate 3 |> String.concat " | "
                    (if lc = Some 0 then pass $"llvm tile core vs its serial chain, contract={mode}" (summary lo)
                     else fail $"llvm tile core vs its serial chain, contract={mode}" (firstFails lo + " " + summary lo))
                    (if cc = Some 0 then pass $"C++ tile vs its serial chain, contract={mode}" (summary co)
                     else fail $"C++ tile vs its serial chain, contract={mode}" (firstFails co + " " + summary co))
                    let lh, ch = caseHashes lo, caseHashes co
                    let subtotal (o: string) =
                        o.Split('\n') |> Array.tryFind (fun l -> l.StartsWith "SUBTOTALS") |> Option.defaultValue ""
                    printfn "      %s | %s" (subtotal lo) (subtotal co)
                    // THE ROUTE: the llvm build asserted, per case, which path
                    // its core took (the tile writes its scratch, the scalar
                    // chain leaves it untouched); the line must exist and
                    // count positive tile-route assertions, or a silent
                    // scratch mismatch could pass on bits alone.
                    let routes =
                        lo.Split('\n') |> Array.tryFind (fun l -> l.StartsWith "ROUTES (llvm)")
                    match routes with
                    | Some l when lc = Some 0 && not (l.Contains "asserted on 0 cases") ->
                        pass $"llvm tile route asserted, contract={mode}" (l.Trim())
                    | Some l -> fail $"llvm tile route asserted, contract={mode}" (firstFails lo + " " + l.Trim())
                    | None -> fail $"llvm tile route asserted, contract={mode}" "no ROUTES line in the llvm build's output"
                    if lh.IsEmpty || lh.Length <> totalCases then
                        fail $"cross-lane bits, contract={mode}" $"the llvm build printed {lh.Length} case hashes, expected {totalCases}"
                    elif lh = ch then
                        modeHashes.Add lh
                        pass $"cross-lane bits, contract={mode}" $"{lh.Length} cases: every packed pool byte-identical"
                    else
                        let diffs = List.zip lh ch |> List.filter (fun (a, b) -> a <> b) |> List.truncate 3
                        fail $"cross-lane bits, contract={mode}"
                            (diffs |> List.map (fun (a, b) -> $"llvm `{a}` vs cpp `{b}`") |> String.concat " | ")
                | (code, _, err), (Some 0, _, _) -> fail $"llvm driver build, contract={mode}" ($"exit {code}: " + err.Trim())
                | _, (code, _, err) -> fail $"C++ driver build, contract={mode}" ($"exit {code}: " + err.Trim()))
    for (mode, contract) in [ "off", Some "off"; "fast", None ] do
        withEnv [ "BLADE_FP_CONTRACT", contract; "BLADE_LLVM_FACTS", None ] (fun () ->
            // THE VFMADD CENSUS of the tile object, per entry point: clang -S
            // of blade_llvm_tiles.c with the object's own flags. =off must
            // fuse nothing (mul + add, as the serial loop); =fast must fuse
            // in every core (the tile s loops: 12 packed fma per step).
            let asmOut = Path.Combine(work, $"tiles_{mode}.s")
            let sFlags = (Build.llvmTilesFlags ()).Replace("-c ", "-S ")
            match runText clang ($"""{sFlags} -I "{work}" -o "{asmOut}" "{Path.Combine(work, EmitLlvm.tilesSourceFileName)}" """) with
            | (Some 0, _, _) when File.Exists asmOut ->
                let lines = File.ReadAllLines asmOut
                let census =
                    EmitLlvm.tilesSymbols |> List.map (fun sym ->
                        let start = lines |> Array.tryFindIndex (fun l -> l.StartsWith (sym + ":"))
                        match start with
                        | None -> sym, -1
                        | Some s ->
                            let body =
                                lines |> Array.skip (s + 1)
                                |> Array.takeWhile (fun l -> not (l.Contains "-- End function"))
                            sym, body |> Array.filter (fun l -> l.Contains "vfmadd" || l.Contains "vfmsub" || l.Contains "vfnmadd") |> Array.length)
                let txt = census |> List.map (fun (s, k) -> $"{s} {k}") |> String.concat ", "
                if census |> List.exists (fun (_, k) -> k < 0) then
                    fail $"vfmadd census contract={mode}" $"an entry point is missing from the assembly: {txt}"
                elif mode = "off" then
                    if census |> List.forall (fun (_, k) -> k = 0) then pass "vfmadd census contract=off" txt
                    else fail "vfmadd census contract=off" ($"fused instructions under -ffp-contract=off: {txt}")
                else
                    if census |> List.forall (fun (_, k) -> k >= 12) then pass "vfmadd census contract=fast" txt
                    else fail "vfmadd census contract=fast" ($"a core did not contract: {txt}")
            | (code, o, e) -> fail $"vfmadd census contract={mode}" ($"clang -S exit {code}: {(o + e).Trim()}"))
    // The test has teeth only if the two modes give DIFFERENT bits somewhere
    // (else a tile that never contracted would pass the fma reference too).
    if modeHashes.Count = 2 then
        let differing = List.zip modeHashes.[0] modeHashes.[1] |> List.filter (fun (a, b) -> a <> b) |> List.length
        if differing > 0 then
            pass "the two contraction modes are distinguishable" $"{differing}/{modeHashes.[0].Length} cases differ between =off and =fast"
        else fail "the two contraction modes are distinguishable" "=off and =fast gave identical bits in every case"
    // Same directory, two policies, two objects (the M6 keying requirement).
    if objects.Count = 2 then
        if objects.[0] <> objects.[1] && File.Exists objects.[0] && File.Exists objects.[1] then
            pass "policy keying: =off and =fast build distinct tile objects in one directory"
                $"{Path.GetFileName objects.[0]} / {Path.GetFileName objects.[1]}"
        else fail "policy keying: =off and =fast build distinct tile objects in one directory" $"both resolved to {objects.[0]}"
    (try Directory.Delete(work, true) with _ -> ())
    printFooter blockName [ $"{passed} passed"; $"{failed} failed"; "0 skipped" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }

// ---------------------------------------------------------------------------
// Block 3: the simplex-blocks agreement pins
// ---------------------------------------------------------------------------
//
// `Blade.SimplexBlocksCore` and `Blade.ZarrProvider.SimplexBlocks` are TWO
// COPIES of the same decomposition, kept apart on purpose: the provider's
// consumer is the zarr/MPI ownership split, which needs equal-size triangular
// quadtree units, while the compute side coarsens off-diagonal triangle pairs
// into dense bricks and therefore produces UNEQUAL units
// (docs/plans/plan-simplex-blocked-compute.md section 6, the MPI row: "do not
// unify ... the two schemes share the leaf structure and the SimplexBlocks
// identities, nothing more").
//
// Two copies of anything drift. So this block is the differential-twin
// discipline applied module against module: over a grid of (n, B, r,
// symmetry), every shared identity must answer the SAME value in both, and
// the decomposition must actually decompose -- cells summing to the pool
// cardinality, blocks partitioning the canonical set, bricks canonical by
// construction. Pure integer arithmetic, so it runs anywhere with no
// toolchain at all.

let runSimplexAgreementTests () : BlockResult =
    printHeader "Simplex Blocks Agreement"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let check (name: string) (ok: bool) (detail: string) =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name detail
    let core = "Blade.SimplexBlocksCore"
    let extents = [ 1L; 2L; 3L; 4L; 5L; 6L; 7L; 8L; 9L; 11L; 13L; 16L; 17L; 31L ]
    let edges = [ 1L; 2L; 3L; 4L; 5L; 8L ]
    let ranks = [ 2; 3 ]

    printSubHeader "identities against the provider's copy"
    // Every shared function, over the whole grid, in both symmetries.
    let mutable mismatches = []
    let mutable cases = 0
    for strict in [ false; true ] do
        for r in ranks do
            for n in extents do
                for b in edges do
                    let t = SimplexBlocksCore.tileCount n b
                    cases <- cases + 1
                    if SimplexBlocksCore.blockCount r t <> ZarrProvider.SimplexBlocks.blockCount r t then
                        mismatches <- $"blockCount r={r} T={t}" :: mismatches
                    for tile in 0L .. t - 1L do
                        if SimplexBlocksCore.tileWidth n b tile <> ZarrProvider.SimplexBlocks.tileWidth n b tile then
                            mismatches <- $"tileWidth n={n} B={b} t={tile}" :: mismatches
                    for tiles in SimplexBlocksCore.blockSequence r t do
                        let mine = SimplexBlocksCore.blockCellCount strict n b tiles
                        let theirs = ZarrProvider.SimplexBlocks.blockCellCount strict n b tiles
                        if mine <> theirs then
                            mismatches <-
                                sprintf "blockCellCount strict=%b n=%d B=%d tiles=%A (%d vs %d)"
                                    strict n b tiles mine theirs :: mismatches
                        let myCells = SimplexBlocksCore.enumBlockCells strict n b tiles |> Seq.toList
                        let theirCells = ZarrProvider.SimplexBlocks.enumBlockCells strict n b tiles |> Seq.toList
                        if myCells <> theirCells then
                            mismatches <- sprintf "enumBlockCells strict=%b n=%d B=%d tiles=%A" strict n b tiles :: mismatches
    check ($"{core} == provider over {cases} (n, B, r, symmetry) cases")
        mismatches.IsEmpty
        (if mismatches.IsEmpty then "blockCount, tileWidth, blockCellCount, enumBlockCells all agree"
         else $"{mismatches.Length} disagreements, first: {(List.head (List.rev mismatches))}")

    // rank/unrank is the other half of the shared surface: it is what names a
    // pool cell, so a divergence here would mean the two modules disagree
    // about WHICH cell a coordinate tuple is.
    let mutable rankBad = []
    for strict in [ false; true ] do
        for r in ranks do
            for n in [ 3L; 4L; 5L; 7L; 11L ] do
                let total =
                    if strict then SimplexBlocksCore.binom n r
                    else SimplexBlocksCore.binom (n + int64 r - 1L) r
                for k in 0L .. total - 1L do
                    let mine = SimplexBlocksCore.unrankToCoords strict n r k
                    let theirs = ZarrProvider.SimplexBlocks.unrankToCoords strict n r k
                    if mine <> theirs then rankBad <- sprintf "unrank strict=%b n=%d r=%d k=%d" strict n r k :: rankBad
                    if SimplexBlocksCore.rankOfCoords strict n mine <> k then
                        rankBad <- sprintf "rank round-trip strict=%b n=%d r=%d k=%d" strict n r k :: rankBad
                    if ZarrProvider.SimplexBlocks.rankOfCoords strict n mine <> k then
                        rankBad <- sprintf "provider rank round-trip strict=%b n=%d r=%d k=%d" strict n r k :: rankBad
    check "rankOfCoords / unrankToCoords agree and round-trip" rankBad.IsEmpty
        (if rankBad.IsEmpty then "every cell of every grid, both directions"
         else $"{rankBad.Length} failures, first: {(List.head (List.rev rankBad))}")

    printSubHeader "the decomposition decomposes"
    // Sum of block cell counts = pool cardinality. This is the identity the
    // whole design rests on: if the blocks did not exactly tile the simplex,
    // a bricked traversal would miss cells or visit them twice.
    let mutable sumBad = []
    for strict in [ false; true ] do
        for r in ranks do
            for n in extents do
                for b in edges do
                    let t = SimplexBlocksCore.tileCount n b
                    let summed =
                        SimplexBlocksCore.blockSequence r t
                        |> Seq.sumBy (SimplexBlocksCore.blockCellCount strict n b)
                    let pool =
                        if strict then SimplexBlocksCore.binom n r
                        else SimplexBlocksCore.binom (n + int64 r - 1L) r
                    if summed <> pool then
                        sumBad <- sprintf "strict=%b r=%d n=%d B=%d: %d blocks-cells vs %d pool" strict r n b summed pool :: sumBad
    check "sum of blockCellCount = C(n+r-1, r) / C(n, r)" sumBad.IsEmpty
        (if sumBad.IsEmpty then "every grid point, both symmetries" else $"{sumBad.Length} failures, first: {(List.head (List.rev sumBad))}")

    // The blocks PARTITION the canonical set, and their cells arrive in an
    // order whose ranks are a permutation of [0, N) -- no cell twice, none
    // missing, none outside the pool.
    let mutable partBad = []
    for strict in [ false; true ] do
        for r in ranks do
            for n in [ 3L; 5L; 8L; 11L ] do
                for b in edges do
                    let t = SimplexBlocksCore.tileCount n b
                    let ranksSeen =
                        SimplexBlocksCore.blockSequence r t
                        |> Seq.collect (SimplexBlocksCore.enumBlockCells strict n b)
                        |> Seq.map (SimplexBlocksCore.rankOfCoords strict n)
                        |> Seq.toList
                    let pool =
                        if strict then SimplexBlocksCore.binom n r
                        else SimplexBlocksCore.binom (n + int64 r - 1L) r
                    if List.sort ranksSeen <> [ 0L .. pool - 1L ] then
                        partBad <- sprintf "strict=%b r=%d n=%d B=%d" strict r n b :: partBad
    check "blocks partition the canonical cell set exactly once" partBad.IsEmpty
        (if partBad.IsEmpty then "ranks are a permutation of [0, pool)" else $"{partBad.Length} failures, first: {(List.head (List.rev partBad))}")

    printSubHeader "the rank-2 compute additions"
    // packedOffset2 is the closed form the emitter lays down; it must name the
    // same cell the combinadic rank does, or the emitted GEP is simply wrong.
    let mutable offBad = []
    for strict in [ false; true ] do
        for n in extents do
            let s = if strict then 1L else 0L
            for i in 0L .. n - 1L do
                for j in i + s .. n - 1L do
                    if SimplexBlocksCore.packedOffset2 strict n i j <> SimplexBlocksCore.rankOfCoords strict n [| i; j |] then
                        offBad <- sprintf "strict=%b n=%d (%d,%d)" strict n i j :: offBad
            if SimplexBlocksCore.poolCells2 strict n <> (if strict then SimplexBlocksCore.binom n 2 else SimplexBlocksCore.binom (n + 1L) 2) then
                offBad <- sprintf "poolCells2 strict=%b n=%d" strict n :: offBad
    check "packedOffset2 = rankOfCoords (the emitter's closed form)" offBad.IsEmpty
        (if offBad.IsEmpty then "every canonical rank-2 cell of every extent" else $"{offBad.Length} failures, first: {(List.head (List.rev offBad))}")

    // THE RANK-r GENERALIZATION, held to the same standard: the hockey-stick
    // closed form must name the cell the combinadic rank names, at every rank
    // the lane can emit -- and it must PARTITION, i.e. the offsets of the
    // canonical cells are exactly 0 .. poolCells-1 with no gap and no
    // collision. A closed form that is merely injective would still corrupt a
    // pool; the surjectivity half is what proves no cell is stranded.
    let mutable offRBad = []
    for strict in [ false; true ] do
        for r in 1 .. 5 do
            // Small extents on purpose: the cell count is C(n+r-1, r), so this
            // grid is already ~10^4 tuples at r = 5 and it is exhaustive.
            for n in [ 1L; 2L; 3L; 4L; 5L; 7L; 8L ] do
                let cells = SimplexBlocksCore.poolCellsR strict n r
                let seen = System.Collections.Generic.HashSet<int64>()
                for k in 0L .. cells - 1L do
                    let coords = SimplexBlocksCore.unrankToCoords strict n r k
                    let closed = SimplexBlocksCore.packedOffsetR strict n coords
                    if closed <> k then
                        offRBad <- sprintf "strict=%b r=%d n=%d rank=%d -> %d" strict r n k closed :: offRBad
                    // the per-coordinate split the canonical read tabulates (G34)
                    if r >= 2 then
                        let split = SimplexBlocksCore.packedOffsetSplit strict n coords
                        if split <> k then
                            offRBad <- sprintf "split strict=%b r=%d n=%d rank=%d -> %d" strict r n k split :: offRBad
                    if not (seen.Add closed) then
                        offRBad <- sprintf "collision strict=%b r=%d n=%d at %d" strict r n k :: offRBad
                if int64 seen.Count <> cells then
                    offRBad <- sprintf "coverage strict=%b r=%d n=%d: %d of %d" strict r n seen.Count cells :: offRBad
    check "packedOffsetR = packedOffsetSplit = rankOfCoords, and partitions the pool (ranks 1-5)" offRBad.IsEmpty
        (if offRBad.IsEmpty then "every canonical cell, ranks 1-5, sym and antisym" else $"{offRBad.Length} failures, first: {(List.head (List.rev offRBad))}")

    // The bricks themselves: they cover the domain exactly once, an
    // off-diagonal brick is a FULL RECTANGLE every one of whose cells is
    // canonical (that is the entire point -- no mask, no guard), and the
    // enumeration order is the canonical ascending-lex block order.
    let mutable brickBad = []
    for strict in [ false; true ] do
        for n in extents do
            for b in edges do
                let bricks = SimplexBlocksCore.bricks2 strict n b
                let s = if strict then 1L else 0L
                let cells =
                    [ for br in bricks do
                        for i in br.RowLo .. br.RowHi - 1L do
                            let lo = if br.IsDiagonal then i + s else br.ColLo
                            for j in lo .. br.ColHi - 1L -> (i, j) ]
                let expected =
                    [ for i in 0L .. n - 1L do for j in i + s .. n - 1L -> (i, j) ]
                if List.sort cells <> expected then
                    brickBad <- sprintf "coverage strict=%b n=%d B=%d" strict n b :: brickBad
                for br in bricks do
                    let (t1, t2) = br.Tiles
                    if not br.IsDiagonal then
                        // Dense rectangle, every cell canonical, no exceptions.
                        let rows = br.RowHi - br.RowLo
                        let cols = br.ColHi - br.ColLo
                        if rows * cols <> SimplexBlocksCore.blockCellCount strict n b [| t1; t2 |] then
                            brickBad <- sprintf "dense-brick count strict=%b n=%d B=%d tiles=(%d,%d)" strict n b t1 t2 :: brickBad
                        if not (SimplexBlocksCore.isDenseBrick [| t1; t2 |]) then
                            brickBad <- sprintf "isDenseBrick disagrees strict=%b n=%d B=%d tiles=(%d,%d)" strict n b t1 t2 :: brickBad
                        if br.RowHi > br.ColLo then
                            brickBad <- $"off-diagonal brick is not strictly ordered n={n} B={b} tiles=({t1},{t2})" :: brickBad
                    elif SimplexBlocksCore.isDenseBrick [| t1; t2 |] then
                        brickBad <- $"diagonal brick claims distinct tiles n={n} B={b} tiles=({t1},{t2})" :: brickBad
                // Ascending-lex block order, filtered to the non-empty blocks.
                let order = bricks |> List.map (_.Tiles)
                let expectedOrder =
                    SimplexBlocksCore.blockSequence 2 (SimplexBlocksCore.tileCount n b)
                    |> Seq.filter (fun ts -> SimplexBlocksCore.blockCellCount strict n b ts > 0L)
                    |> Seq.map (fun ts -> (ts.[0], ts.[1]))
                    |> Seq.toList
                if order <> expectedOrder then
                    brickBad <- sprintf "block order strict=%b n=%d B=%d" strict n b :: brickBad
    check "bricks2 covers once, densely, in ascending-lex block order" brickBad.IsEmpty
        (if brickBad.IsEmpty then "coverage, density, ordering" else $"{brickBad.Length} failures, first: {(List.head (List.rev brickBad))}")

    // The profitability table in plan section 3 -- the numbers the B/depth
    // policy is argued from. Pinned so a change to the formula has to face
    // them.
    let fracCases =
        [ 2, 4L, 0.75; 2, 8L, 0.875; 2, 16L, 0.9375; 2, 32L, 0.969
          3, 4L, 0.375; 3, 8L, 0.656; 3, 16L, 0.820; 4, 4L, 0.094; 4, 16L, 0.666 ]
    let fracBad =
        fracCases
        |> List.filter (fun (r, t, want) -> abs (SimplexBlocksCore.denseBrickFraction r t - want) > 0.001)
    check "denseBrickFraction reproduces the plan's table" fracBad.IsEmpty
        (if fracBad.IsEmpty then $"{fracCases.Length} rows"
         else $"{fracBad.Length} rows disagree")

    // The derived policy after the 2026-08-18 measurement: never block by
    // default. S0 bricks lost or tied at every benchmarked extent, so the
    // serial triangle -- the fastest measured path -- is the only default;
    // bricked emission is opt-in via an explicit BLADE_LLVM_BRICKS=<B>.
    check "autoTileEdge never blocks by default (measured 2026-08-18 policy)"
        (SimplexBlocksCore.autoTileEdge 8L = None && SimplexBlocksCore.autoTileEdge 64L = None
         && SimplexBlocksCore.autoTileEdge 65L = None && SimplexBlocksCore.autoTileEdge 997L = None)
        "None at every extent"

    // The fold's combine order, as arithmetic. `emitCompactFold` accumulates
    // ONE PARTIAL PER BRICK and joins the partials in ascending-lex block
    // order; over exact (integer-valued) data that must equal the serial pool-
    // order walk cell for cell, which is the invariant the licensed-path
    // testing policy asks for instead of a float tolerance (plan section 7).
    let mutable foldBad = []
    for strict in [ false; true ] do
        for n in [ 3L; 5L; 8L; 11L; 16L; 17L ] do
            for b in edges do
                let cell (i: int64) (j: int64) = i * 1000L + j + 7L
                let s = if strict then 1L else 0L
                let serial =
                    [ for i in 0L .. n - 1L do for j in i + s .. n - 1L -> cell i j ] |> List.sum
                let bricked =
                    SimplexBlocksCore.bricks2 strict n b
                    |> List.sumBy (fun br ->
                        // The brick's own partial, then the join.
                        [ for i in br.RowLo .. br.RowHi - 1L do
                            let lo = if br.IsDiagonal then i + s else br.ColLo
                            for j in lo .. br.ColHi - 1L -> cell i j ] |> List.sum)
                if serial <> bricked then
                    foldBad <- sprintf "strict=%b n=%d B=%d: %d vs %d" strict n b serial bricked :: foldBad
    check "per-brick partials in block order = the serial pool-order fold" foldBad.IsEmpty
        (if foldBad.IsEmpty then "exact arithmetic, every grid point, both symmetries"
         else $"{foldBad.Length} failures, first: {(List.head (List.rev foldBad))}")

    printSubHeader "the licence gate and the measurement knob"
    // THE GATE, asserted by name in both directions. A fold whose kernel is
    // not `comm`-licensed must never brick, because grouping a fold by brick
    // reassociates it -- and no environment variable may buy that licence.
    // (A map passes `licensed = true` unconditionally: distinct cells,
    // independent writes, plan section 7.)
    check "an unlicensed fold never bricks, at any extent or knob setting"
        ([ None; Some "off"; Some "2"; Some "64"; Some "banana" ]
         |> List.forall (fun knob ->
                withEnv [ "BLADE_LLVM_BRICKS", knob ] (fun () ->
                    [ 4L; 65L; 997L; 100000L ] |> List.forall (fun n -> EmitLlvm.brickTileEdge false 0L n = None))))
        "licensed = false answers None for every (n, BLADE_LLVM_BRICKS)"
    check "a licensed fold bricks exactly where the derived policy says"
        (withEnv [ "BLADE_LLVM_BRICKS", None ] (fun () ->
            EmitLlvm.brickTileEdge true 0L 8L = None
            && EmitLlvm.brickTileEdge true 0L 64L = None
            && EmitLlvm.brickTileEdge true 0L 65L = None
            && EmitLlvm.brickTileEdge true 0L 997L = None))
        "unset + no reuse hint = the derived policy (serial everywhere; bricks are knob-only)"
    check "BLADE_LLVM_BRICKS off / <number> / junk"
        (withEnv [ "BLADE_LLVM_BRICKS", Some "off" ] (fun () -> EmitLlvm.brickTileEdge true 0L 997L = None)
         && withEnv [ "BLADE_LLVM_BRICKS", Some "0" ] (fun () -> EmitLlvm.brickTileEdge true 0L 997L = None)
         && withEnv [ "BLADE_LLVM_BRICKS", Some "3" ] (fun () -> EmitLlvm.brickTileEdge true 0L 7L = Some 3L)
         // A pinned edge that leaves ONE tile is not a decomposition, so it
         // declines rather than emitting a block loop with a single block.
         && withEnv [ "BLADE_LLVM_BRICKS", Some "9" ] (fun () -> EmitLlvm.brickTileEdge true 0L 7L = None)
         && withEnv [ "BLADE_LLVM_BRICKS", Some "banana" ] (fun () -> EmitLlvm.brickTileEdge true 0L 997L = None))
        "off forces serial, a number pins B (T >= 2 still required), junk defers to the (serial) policy"
    // The reuse hint (plan-simplex-blocked-compute.md section 0, third block):
    // a row-operand working set at or past 8 MiB flips BrickAuto to the
    // divisor-preferred reuse edge; below it, and for every plain map
    // (RowOpBytes = 0), the serial default stands; the licence still
    // dominates the hint.
    check "the reuse hint bricks past the threshold, divisor preferred"
        (withEnv [ "BLADE_LLVM_BRICKS", None ] (fun () ->
            EmitLlvm.brickTileEdge true 9000000L 6006L = Some 66L
            && EmitLlvm.brickTileEdge true 9000000L 6007L = Some 64L
            && EmitLlvm.brickTileEdge true 8388607L 6006L = None
            && EmitLlvm.brickTileEdge false 9000000L 6006L = None))
        "hint >= 8 MiB bricks (66 divides 6006; prime 6007 falls back to 64 ragged); one byte under stays serial; unlicensed stays None"

    printSubHeader "what the emitter does with it"
    let countOf (needle: string) (text: string) =
        let mutable n = 0
        let mutable i = text.IndexOf(needle, StringComparison.Ordinal)
        while i >= 0 do
            n <- n + 1
            i <- text.IndexOf(needle, i + 1, StringComparison.Ordinal)
        n
    // A four-element symmetric map: one tile under any policy, so its emission
    // must be identical whether or not bricking is switched off -- which is
    // what keeps every symmetric corpus file on exactly its pre-brick nest.
    let smallSym = """
type Ix = Idx<4>
let v: Array<Float like Ix> = [1.0, 2.0, 3.0, 4.0]
let s = method_for(v, v) <@> lambda(x, y) where comm(x, y) -> x * y |> compute
"""
    // Large enough that the pre-measurement auto policy would have bricked it;
    // after 2026-08-18 the default is serial at EVERY extent, so the default
    // emission must equal forced-serial here too -- and an explicit tile edge
    // must still decompose it (that is how the next variant gets A/B'd).
    let bigSym = """
type Bx = Idx<97>
let v = method_for(range<Bx>) <@> lambda(i) -> Float64(i) |> compute
let s = method_for(v, v) <@> lambda(x, y) where comm(x, y) -> x * y |> compute
"""
    let emitBrick knob name src = emitUnder [ "BLADE_LLVM_BRICKS", knob ] name src
    (match emitBrick None "blocks" smallSym, emitBrick (Some "off") "blocks" smallSym,
           emitBrick (Some "2") "blocks" smallSym with
     | Ok dflt, Ok off, Ok pinned ->
         check "a single-tile domain emits the serial triangle by default" (dflt = off)
             "default emission == BLADE_LLVM_BRICKS=off, byte for byte"
         check "pinning B below the extent does decompose it" (pinned <> off)
             "BLADE_LLVM_BRICKS=2 changes the emitted nest"
     | a, b, c ->
         let err = [ a; b; c ] |> List.tryPick (function Error e -> Some e | _ -> None)
         check "small symmetric map emits" false (defaultArg err "unknown"))
    (match emitBrick None "blocks" bigSym, emitBrick (Some "off") "blocks" bigSym,
           emitBrick (Some "48") "blocks" bigSym with
     | Ok dflt, Ok off, Ok pinned ->
         check "the default emission is the serial triangle at every extent" (dflt = off)
             "default emission == BLADE_LLVM_BRICKS=off, byte for byte, at n = 97"
         check "an explicit tile edge still decomposes past the old threshold" (pinned <> off)
             "BLADE_LLVM_BRICKS=48 changes the emitted nest at n = 97"
     | a, b, c ->
         let err = [ a; b; c ] |> List.tryPick (function Error e -> Some e | _ -> None)
         check "large symmetric map emits" false (defaultArg err "unknown"))
    // THE FLAT-ELEMENTWISE PROMISE (plan section 6, row 1). Row-base
    // arithmetic is exactly one `sdiv` per compact coordinate computation, so
    // adding an elementwise map over a packed pool must add exactly ONE -- the
    // one its own printer spends -- and none for the map itself. Anything more
    // means the map grew a triangular nest it has no use for.
    (match emitBrick None "blocks" smallSym,
           emitBrick None "blocks" (smallSym + "let t = s * 3.0\n") with
     | Ok bare, Ok mapped ->
         // "sdiv " matches both spellings: the binomial row bases are
         // `sdiv exact` (exact by construction, bench G34).
         let d = countOf "sdiv " (instructionsOnly mapped) - countOf "sdiv " (instructionsOnly bare)
         check "an elementwise map over a packed pool costs no coordinate math" (d = 1)
             ($"row-base divisions added by `s * 3.0`: {d} (1 = its printer's, 0 = the map's)")
     | a, b ->
         let err = [ a; b ] |> List.tryPick (function Error e -> Some e | _ -> None)
         check "elementwise-over-compact emits" false (defaultArg err "unknown"))

    printFooter "Simplex Blocks Agreement"
        [ $"{passed} passed"; $"{failed} failed"; "0 skipped" ]
    { Block = "Simplex Blocks Agreement"; Passed = passed; Failed = failed; Skipped = 0
      FailedNames = failedNames }

// ---------------------------------------------------------------------------
// Block 4: the two-lane differential
// ---------------------------------------------------------------------------

/// Which corpus categories the differential sweeps by default. `basic`,
/// `functions` and `loops` are the scalar/dense core; `symmetry`,
/// `index-types` and `reynolds` are where the packed simplex pools live, and
/// they joined the default sweep when the lane learned to emit them;
/// `recursive-arrays` joined with the IRForRange arm (the ordered serial
/// loop: `let rec` and whole-rank folds); `sql-group-by` with the group_by
/// port (2026-10), so the CSR build and its BL8006 aborts stay gated; `domains`
/// with the closed-form domain nest (structural/06, bench G4); `sibling-folds`
/// with the sibling-fold rewrite (round 2), whose joins every lane consumes.
/// Every other category is reachable standalone with `blade test llvm <category>`.
let defaultCategories = [ "basic"; "functions"; "loops"; "symmetry"; "index-types"; "reynolds"; "recursive-arrays"; "sql-group-by"; "domains"; "sibling-folds" ]

/// What happened to one file. `Matched` carries a note so an agreement that
/// asserts NOTHING -- both lanes printed no values at all -- is visible in the
/// log rather than indistinguishable from a real comparison.
type private CaseOutcome =
    | Matched of note: string
    | SkippedCase of reason: string
    | FailedCase of detail: string

/// The scratch root, CWD-relative like every other block's (see CLAUDE.md:
/// two `blade test` processes must not share a working directory).
let private outputDir = "./generated_cpp_tests"

/// Remove this case's artifacts from a previous run, so a compile that fails
/// cannot be papered over by a stale executable of the same name.
let private clearArtifacts (dir: string) (stems: string list) =
    for stem in stems do
        for ext in [ ".cpp"; ".cu"; ".ll"; ".exe"; ".out" ] do
            let f = Path.Combine(dir, stem + ext)
            try if File.Exists f then File.Delete f with _ -> ()

/// Run ONE corpus file through both back ends.
///
/// Order is deliberate: the llvm lane gets the first look, because a refusal
/// is in-process and free while the C++ oracle costs a g++ invocation. Each
/// lane lowers its OWN IRProgram from the same source, so neither back end can
/// observe state the other left on a shared tree.
let private runCase (dir: string) (name: string) (source: string) : CaseOutcome =
    let stem = Build.sanitizeFileName name
    let cppStem = "llvmdiff_cpp_" + stem
    let llStem = "llvmdiff_ll_" + stem
    try
        // A `(rejects)` probe has no program to run. An `(aborts)` probe
        // DOES: both lanes must abort the same way (below), which is the only
        // place a dropped runtime guard (BL8006, BL8013, ...) can show up --
        // skipping these is how the group_keys guards went missing unseen.
        let isAbortProbe = name.EndsWith "(aborts)"
        if name.EndsWith "(rejects)" then
            SkippedCase "a reject probe (nothing to compare)"
        else
        // lowerCaptured, not lower: it resolves FILE-BACKED imports (stdlib
        // `stats`, `units.SI`, `plot`) the way the corpus runner and `blade
        // run` do, and hands warnings back instead of printing them. `lower`
        // refused every stdlib-importing test, which this block then counted
        // as a front-end skip rather than as lane coverage.
        match fst (Lowering.lowerCaptured source) with
        | Error _ -> SkippedCase "the front end refuses this program"
        | Ok ir0 ->
        match IRValidate.validateIR ir0 with
        | Error _ -> SkippedCase "IR validation refuses this program"
        | Ok ir0 ->
        match EmitLlvm.tryEmitProgramNamed stem ir0 with
        | Error reason -> SkippedCase ("llvm lane refused: " + reason)
        | Ok ll ->
            clearArtifacts dir [ cppStem; llStem ]
            // ---- C++ oracle -------------------------------------------------
            // A SECOND lowering, not a reuse of ir0: the two back ends install
            // their own analysis contexts over the IR they consume, and the
            // oracle has to be exactly what `blade test basic` would have
            // compiled -- not an IRProgram another emitter has already walked.
            match fst (Lowering.lowerCaptured source) with
            | Error _ -> SkippedCase "the front end refuses this program"
            | Ok ir1 ->
            match IRValidate.validateIR ir1 with
            | Error _ -> SkippedCase "IR validation refuses this program"
            | Ok ir1 ->
            let (cppCode, _warnings) = CodeGen.genSelfContainedProgramFromIR ir1 stem
            // BOTH refusal channels are drained unconditionally -- they are
            // process-wide cells, and leaving one loaded would attribute this
            // file's refusal to the next file in the sweep.
            let unhandled = CodeGen.takeUnhandledIRNodeDiagnostics ()
            let refusals = CodeGen.takeCodegenRefusalDiagnostics cppCode
            let cudaSide = CodeGen.getCudaFileContent ()
            if not unhandled.IsEmpty then
                SkippedCase "the C++ oracle has no arm for this program"
            elif not refusals.IsEmpty then
                SkippedCase "the C++ oracle refuses this program"
            elif cudaSide.IsSome then
                SkippedCase "the C++ oracle emits a device kernel (no serial oracle)"
            elif Build.inferBackendReq cppCode <> Build.CpuOnly then
                SkippedCase "the C++ oracle needs a non-CPU backend"
            else
            let cppFile = Path.Combine(dir, cppStem + ".cpp")
            File.WriteAllText(cppFile, cppCode)
            match Build.compileCpp cppFile dir with
            | Error e when e.StartsWith "Skipped:" -> SkippedCase ("C++ oracle: " + e)
            | Error _ -> SkippedCase "the C++ oracle does not compile"
            | Ok cppExe ->
            match runExeCapture cppExe with
            | Error e -> SkippedCase ("the C++ oracle does not run: " + e)
            | Ok (code, _, _) when code <> 0 && not isAbortProbe -> SkippedCase ($"the C++ oracle exits {code}")
            | Ok (cppCode, cppOut, cppErr) ->
            // ---- llvm lane --------------------------------------------------
            let llFile = Path.Combine(dir, llStem + ".ll")
            File.WriteAllText(llFile, ll)
            match Build.compileLlvmProgram llFile dir with
            | Error e when e.StartsWith "Skipped:" -> SkippedCase e
            | Error e -> FailedCase ($"""llvm compile failed ({llFile}): {(e.Replace("\n", " ") |> fun s -> if s.Length > 400 then s.Substring(0, 400) + "..." else s)}""")
            | Ok llExe ->
            // An abort's identity: the `error[BLxxxx]: message` line and its
            // `  --> file:line` location, in order. (The trace lines under them
            // are the shadow-frame machinery's own business.)
            let abortLines (err: string) =
                err.Replace("\r\n", "\n").Split('\n')
                |> Array.filter (fun l -> l.StartsWith "error[" || l.StartsWith "  --> ")
                |> List.ofArray
            match runExeCapture llExe with
            | Error e -> FailedCase ($"llvm run failed ({llFile}): {e}")
            | Ok (code, _, err) when cppCode <> 0 && (code <> cppCode || abortLines err <> abortLines cppErr) ->
                let show (e: string) = String.concat " / " (abortLines e)
                FailedCase ($"abort differs -- cpp exits {cppCode} with [{show cppErr}]; llvm exits {code} with [{show err}] ({llFile})")
            | Ok (code, _, err) when code <> 0 && cppCode = 0 ->
                FailedCase ($"llvm exe exits {code} ({llFile}): {(err.Trim())}")
            | Ok (_, llvmOut, _) ->
                match compareRuns 3 cppOut llvmOut with
                | Ok () ->
                    // Keep only what a reader would want: matched artifacts
                    // are regenerable and 300 of them fill a scratch dir.
                    clearArtifacts dir [ cppStem; llStem ]
                    Matched (if cppCode <> 0 then "both lanes aborted with the same error"
                             elif stripTiming cppOut = "" then "agreed, but neither lane printed a value"
                             else "")
                | Error diffs ->
                    FailedCase
                        ($"""stdout differs -- {(String.concat " | " diffs)} (kept {cppFile} and {llFile})""")
    with ex -> FailedCase ($"harness exception: {ex.Message}")

/// The differential sweep over a category list.
let runLlvmDifferentialTestsFor (categories: string list) : BlockResult =
    printHeader "LLVM Differential (llvm lane vs C++ lane)"
    let block = "LLVM Differential"
    let skipBlock reason =
        printfn "Skipped: %s" reason
        printFooter block [ "0 passed"; "0 failed"; "1 skipped" ]
        { Block = block; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    // Toolchain gates FIRST, and both of them skip rather than fail: without
    // clang there is no lane, without g++ there is no oracle.
    match Build.resolveClang () with
    | None ->
        skipBlock "no clang found (set BLADE_LLVM_CLANG, or install C:\\msys64\\clang64)."
    | Some clang ->
    if not Build.capabilities.Value.HasGpp then
        skipBlock "requires g++ for the C++ oracle lane."
    else
    printfn "clang: %s" clang
    printfn "categories: %s\n" (String.concat ", " categories)
    let dir = outputDir
    Directory.CreateDirectory dir |> ignore
    // Both lanes' runtimes, deployed once: the C++ headers the oracle
    // #includes with bare quotes, and the C shim every .ll links against.
    CodeGen.deployRuntimeHeaders dir
    EmitLlvm.deployShim dir
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let mutable failedNames = []
    let mutable reasons = []
    for cat in categories do
        let tests =
            try Ok (Blade.Tests.Corpus.category cat)
            with ex -> Error ex.Message
        match tests with
        | Error msg ->
            printSubHeader ($"{cat} -- UNAVAILABLE")
            printfn "  %s" msg
            failed <- failed + 1
            failedNames <- failedNames @ [ $"category {cat}" ]
        | Ok tests ->
            printSubHeader ($"{cat} ({tests.Length} files)")
            for (name, source) in tests do
                match runCase dir name source with
                | Matched note ->
                    passed <- passed + 1
                    resultLine Pass name note
                | SkippedCase reason ->
                    skipped <- skipped + 1
                    reasons <- reason :: reasons
                    resultLine Skip name reason
                | FailedCase detail ->
                    failed <- failed + 1
                    failedNames <- failedNames @ [ name ]
                    resultLine Fail name detail
    printReasonHistogram (List.rev reasons)
    // What a sweep is FOR, stated as a number rather than left to be inferred
    // from a skip count.
    //
    // The denominator is the programs the lane could in principle have taken:
    // skips that are not the lane's doing -- reject/abort probes with nothing
    // to compare, programs the shared FRONT END refuses, oracle-side declines
    // -- are excluded rather than counted against it. Including them would
    // move the coverage figure every time the corpus gained a reject probe,
    // which says nothing about the back end and would make the one number here
    // worth reading untrustworthy.
    let laneRefusals =
        reasons |> List.filter (fun r -> r.StartsWith "llvm lane refused:") |> List.length
    let emitted = passed + failed
    let comparable = emitted + laneRefusals
    if comparable > 0 then
        printfn "\n  Lane coverage: %d of %d comparable programs emitted by the llvm lane (%.1f%%)"
            emitted comparable (100.0 * float emitted / float comparable)
        printfn "    %d refused by the lane (the histogram above is the worklist); %d skipped for reasons that are not the lane's"
            laneRefusals (skipped - laneRefusals)
    printFooter block
        [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = block; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = failedNames }

/// The default sweep (`blade test llvm`).
let runLlvmDifferentialTests () : BlockResult =
    runLlvmDifferentialTestsFor defaultCategories

/// Gap G60 (review codex-diff-W4B-B-2 finding 1): ordinary positional reads
/// of a deferred module map inside a top-level BLOCK keep the statement's
/// force boundary in the llvm lane -- forced at the START of the whole
/// statement, as the C++ lane's block pre-pass forces them -- so the two
/// compiled lanes agree on the value twin (the write after the force: r = 15,
/// g = [10, 5]) and the first-failure twin (g's BL8013 before the block's
/// earlier cast). The interpreter evaluates both at the read (r = 7; BL8006):
/// the inherited C++ / interpreter order gap is OPEN in G60, so these twins
/// are C++-vs-LLVM differentials here rather than corpus files (`test interp`
/// would fail them). Skipped without clang or g++.
let runLlvmBlockForceTwins () : BlockResult =
    printHeader "LLVM Block Force Boundary (C++ vs llvm, G60)"
    let block = "LLVM Block Force Boundary"
    let twins =
        [ "block_reads_after_write_keep_statement_boundary",
          "type Row = Idx<2>\nlet mut x: Array<Int64 like Row> = [1, 2]\nlet g = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    x(0) = 5\n    g(0) + g(1)\n}\n"
          "block_reads_after_failing_statement_keep_statement_boundary (aborts)",
          "type Row = Idx<2>\nlet x: Array<Int64 like Row> = [1, 0]\nlet g = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    let early = x((x(0) + 5 : Row))\n    g(0) + g(1) + early\n}\n"
          // codex-diff-W4B-B-3 finding 1: an alias AND later direct reads --
          // the pre-pass forces g at the statement start, the alias copies
          "block_alias_with_later_reads_keeps_statement_boundary",
          "type Row = Idx<2>\nlet mut x: Array<Int64 like Row> = [1, 2]\nlet g = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    x(0) = 5\n    let b = g\n    g(0) + g(1)\n}\n"
          "block_alias_with_later_reads_after_failing_statement (aborts)",
          "type Row = Idx<2>\nlet x: Array<Int64 like Row> = [1, 0]\nlet g = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    let early = x((x(0) + 5 : Row))\n    let b = g\n    g(0) + g(1) + early\n}\n"
          // finding 2: a tuple INITIALIZER inside the block is covered by the
          // pre-pass, not member-ordered
          "block_tuple_initializer_keeps_statement_boundary",
          "type Row = Idx<2>\nlet mut x: Array<Int64 like Row> = [1, 2]\nlet g = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    x(0) = 5\n    let t = (0, g(0) + g(1))\n    t[0] + t[1]\n}\n"
          "block_tuple_initializer_after_failing_statement (aborts)",
          "type Row = Idx<2>\nlet x: Array<Int64 like Row> = [1, 0]\nlet g = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    let early = x((x(0) + 5 : Row))\n    let t = (0, g(0) + g(1))\n    t[0] + t[1] + early\n}\n"
          // codex-diff-W4B-B-4 finding 1: prodsum operands are pre-pass reads
          "block_alias_with_later_prodsum_keeps_statement_boundary",
          "type Row = Idx<2>\nlet mut x: Array<Int64 like Row> = [1, 2]\nlet g = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    x(0) = 5\n    let b = g\n    prodsum(g, g)\n}\n"
          "block_alias_with_later_prodsum_after_failing_statement (aborts)",
          "type Row = Idx<2>\nlet x: Array<Int64 like Row> = [1, 0]\nlet g = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    let early = x((x(0) + 5 : Row))\n    let b = g\n    prodsum(g, g) + early\n}\n"
          // finding 2: an alias of g must not suppress h's statement-start force
          "block_alias_of_one_map_reads_of_another",
          "type Row = Idx<2>\nlet mut x: Array<Int64 like Row> = [1, 2]\nlet g = method_for(x) <@> lambda(v) -> v + 1\nlet h = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    x(0) = 5\n    let b = g\n    h(0) + h(1) + b(0) + b(1)\n}\n"
          // codex-diff-W4B-B-5 finding 1: a TOTAL kernel over a FAILING
          // deferred input is not "cannot fail"; every collected map is
          // forced up front in collector order (h, then later)
          "block_total_kernel_over_failing_input_forced_in_order (aborts)",
          "type Row = Idx<2>\nlet x: Array<Int64 like Row> = [1, 0]\nlet bad = method_for(x) <@> lambda(v) -> 10 / v\nlet h = method_for(bad) <@> lambda(v) -> v + 1\nlet later = method_for(x) <@> lambda(v) -> x((v + 5 : Row))\nlet r = {\n    let first = h(0)\n    first + h(1) + later(0) + later(1)\n}\n"
          "block_total_kernel_over_input_values",
          "type Row = Idx<2>\nlet x: Array<Int64 like Row> = [1, 2]\nlet bad = method_for(x) <@> lambda(v) -> 10 / v\nlet h = method_for(bad) <@> lambda(v) -> v + 1\nlet later = method_for(x) <@> lambda(v) -> x((v - 1 : Row))\nlet r = {\n    let first = h(0)\n    first + h(1) + later(0) + later(1)\n}\n"
          "block_alias_of_one_map_reads_of_another_after_failing_statement (aborts)",
          "type Row = Idx<2>\nlet x: Array<Int64 like Row> = [1, 0]\nlet g = method_for(x) <@> lambda(v) -> v + 1\nlet h = method_for(x) <@> lambda(v) -> 10 / v\nlet r = {\n    let early = x((x(0) + 5 : Row))\n    let b = g\n    h(0) + h(1) + b(0) + b(1) + early\n}\n" ]
    match Build.resolveClang () with
    | None ->
        printfn "Skipped: no clang found."
        printFooter block [ "0 passed"; "0 failed"; $"{twins.Length} skipped" ]
        { Block = block; Passed = 0; Failed = 0; Skipped = twins.Length; FailedNames = [] }
    | Some _ when not Build.capabilities.Value.HasGpp ->
        printfn "Skipped: requires g++ for the C++ oracle lane."
        printFooter block [ "0 passed"; "0 failed"; $"{twins.Length} skipped" ]
        { Block = block; Passed = 0; Failed = 0; Skipped = twins.Length; FailedNames = [] }
    | Some _ ->
    let dir = outputDir
    Directory.CreateDirectory dir |> ignore
    CodeGen.deployRuntimeHeaders dir
    EmitLlvm.deployShim dir
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let mutable names = []
    for (name, src) in twins do
        match runCase dir name src with
        | Matched note -> passed <- passed + 1; resultLine Pass name (if note = "" then "C++ and llvm agree (whole stdout / abort identity)" else note)
        | SkippedCase why -> skipped <- skipped + 1; resultLine Skip name why
        | FailedCase why -> failed <- failed + 1; names <- names @ [ name ]; resultLine Fail name why
    printFooter block [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = block; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = names }

// ---------------------------------------------------------------------------
// Block 5: the three-way blocked-simplex gate
// ---------------------------------------------------------------------------
//
// The ordinary differential compares TWO arms and would pass a lane that never
// bricked anything: the derived policy declines to block a domain of eight
// elements, and every symmetric corpus file has one. So this block runs each
// symmetric program THREE ways --
//
//   C++ oracle | llvm with BLADE_LLVM_BRICKS=off | llvm with the tile edge
//   pinned small enough to actually decompose the corpus extents
//
// -- and demands agreement across all three. The middle arm is the control:
// if it and the C++ lane agree but the bricked arm does not, the failure is in
// the decomposition and nowhere else. Two pinned edges (2 and 3) are run
// because they land the RAGGED LAST TILE differently on the same extent, and
// the last tile is where a block decomposition goes wrong.
//
// A fourth assertion has nothing to do with values: the bricked .ll must
// DIFFER from the serial one. A knob that silently did nothing would make
// every comparison above pass while proving nothing at all.

/// The programs this gate runs: symmetric corpus files by their `// TEST:`
/// name, plus the two synthesized fixtures. Named rather than pattern-matched
/// so a corpus file that moves or is renamed FAILS here instead of quietly
/// dropping out of the gate.
let private simplexCorpusCases =
    [ "symmetry", "Triangular Iteration"
      "symmetry", "Output Type: Same Array + Comm"
      "symmetry", "Reduce Inside Inlined Fiber Kernel"
      "symmetry", "Antisymm pin (declared) - strict triangle without reynolds"
      "symmetry", "Antisymmetric triangular storage (1D strict simplex)"
      "symmetry", "Flat Elementwise Sym Compact"
      "loops", "Range SymIdx Prefix Offsets (r=2)"
      "loops", "Range SymIdx Named Alias Carries The Component Tag"
      // RANK 3. The blocked arms run serial at rank 3 (the blocked schedule is
      // rank-2 only), so for these the gate is really "the rank-r nest agrees
      // with the C++ lane three times over" -- which is exactly what wants
      // watching while the arithmetic is new. `Range SymIdx Prefix Offsets
      // (r=3)` is the sharpest of them: it pins the packed COORDINATE the
      // emitter hands every cell, so a wrong prefix term shows up as a wrong
      // printed value rather than as a crash. The literals cover the
      // canonicalizing read (mirrors and the diagonal); the antisym pair
      // covers the cumulative strict shift that a previous rank-3 build got
      // wrong (index-types/040's header records that bug).
      "loops", "Range SymIdx Prefix Offsets (r=3)"
      "index-types", "Rank-3 Symmetric Literal"
      "index-types", "Unannotated Triangular Literal Infers Rank-3 Symmetric"
      "index-types", "AntisymIdx Rank-3 Consumed By Kernel"
      "symmetry", "Output Type: Three-Way Same" ]

/// The synthesized programs (tests/fixtures/llvm/): a whole packed pool
/// printed cell by cell at ragged extents, and the large prime-extent case.
let private simplexFixtures = [ "simplex_pool"; "simplex_large" ]

/// One arm of the three-way run: a name and the `BLADE_LLVM_BRICKS` value.
let private brickArms = [ "serial", "off"; "bricked-b2", "2"; "bricked-b3", "3" ]

let runLlvmSimplexGate () : BlockResult =
    printHeader "LLVM Blocked Simplex (three-way)"
    let block = "LLVM Blocked Simplex"
    let skipBlock reason =
        printfn "Skipped: %s" reason
        printFooter block [ "0 passed"; "0 failed"; "1 skipped" ]
        { Block = block; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    match Build.resolveClang () with
    | None -> skipBlock "no clang found (set BLADE_LLVM_CLANG, or install C:\\msys64\\clang64)."
    | Some _ ->
    if not Build.capabilities.Value.HasGpp then skipBlock "requires g++ for the C++ oracle lane."
    else
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let mutable failedNames = []
    let pass name detail = passed <- passed + 1; resultLine Pass name detail
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [ name ]
        resultLine Fail name detail
    let dir = outputDir
    Directory.CreateDirectory dir |> ignore
    CodeGen.deployRuntimeHeaders dir
    EmitLlvm.deployShim dir
    // Gather the sources: corpus by name, then the fixtures from disk.
    let sources =
        [ for (cat, testName) in simplexCorpusCases do
            let found =
                try Blade.Tests.Corpus.category cat |> List.tryFind (fun (n, _) -> n = testName)
                with _ -> None
            yield ($"{cat}/{testName}", found)
          for fx in simplexFixtures do
            let path = Path.Combine(goldenRoot.Value, fx + ".blade")
            let found =
                if File.Exists path then Some (fx, File.ReadAllText path) else None
            yield ($"fixture/{fx}", found) ]
    for (label, found) in sources do
        match found with
        | None -> fail label "source not found (a corpus test was renamed, or a fixture is missing)"
        | Some (name, source) ->
            let stem = Build.sanitizeFileName name
            try
                // ---- the C++ oracle, once -------------------------------
                let cppStem = "simplex_cpp_" + stem
                clearArtifacts dir [ cppStem ]
                let cppOut =
                    match Lowering.lower source with
                    | Error e -> Error ("front end: " + e)
                    | Ok ir ->
                        match IRValidate.validateIR ir with
                        | Error errs -> Error ("IR validation: " + String.concat "; " errs)
                        | Ok ir ->
                            let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir stem
                            CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                            CodeGen.takeCodegenRefusalDiagnostics cppCode |> ignore
                            CodeGen.getCudaFileContent () |> ignore
                            let f = Path.Combine(dir, cppStem + ".cpp")
                            File.WriteAllText(f, cppCode)
                            match Build.compileCpp f dir with
                            | Error e -> Error ("C++ oracle does not compile: " + e)
                            | Ok exe ->
                                match runExeCapture exe with
                                | Error e -> Error ("C++ oracle does not run: " + e)
                                | Ok (code, _, _) when code <> 0 -> Error ($"C++ oracle exits {code}")
                                | Ok (_, out, _) -> Ok out
                match cppOut with
                | Error reason ->
                    skipped <- skipped + 1
                    resultLine Skip label reason
                | Ok cppText ->
                    // ---- the llvm arms --------------------------------
                    let arms =
                        brickArms
                        |> List.map (fun (armName, knob) ->
                            let llStem = $"simplex_ll_{(Build.sanitizeFileName armName)}_{stem}"
                            clearArtifacts dir [ llStem ]
                            let emitted =
                                withEnv (defaultEmissionEnv @ [ "BLADE_LLVM_BRICKS", Some knob ]) (fun () ->
                                    match Lowering.lower source with
                                    | Error e -> Error ("front end: " + e)
                                    | Ok ir ->
                                        match IRValidate.validateIR ir with
                                        | Error errs -> Error ("IR validation: " + String.concat "; " errs)
                                        | Ok ir ->
                                            match EmitLlvm.tryEmitProgramNamed stem ir with
                                            | Error r -> Error ("llvm lane refused: " + r)
                                            | Ok ll -> Ok ll)
                            match emitted with
                            | Error e -> (armName, Error e, "")
                            | Ok ll ->
                                let f = Path.Combine(dir, llStem + ".ll")
                                File.WriteAllText(f, ll)
                                match Build.compileLlvmProgram f dir with
                                | Error e -> (armName, Error ($"""compile failed: {(e.Replace("\n", " "))}"""), ll)
                                | Ok exe ->
                                    match runExeCapture exe with
                                    | Error e -> (armName, Error ("run failed: " + e), ll)
                                    | Ok (code, _, err) when code <> 0 ->
                                        (armName, Error ($"exits {code}: {(err.Trim())}"), ll)
                                    | Ok (_, out, _) -> (armName, Ok out, ll))
                    let refused = arms |> List.tryPick (fun (a, r, _) -> match r with Error e -> Some (a, e) | _ -> None)
                    match refused with
                    | Some (armName, e) when e.StartsWith "llvm lane refused" || e.StartsWith "front end" ->
                        skipped <- skipped + 1
                        resultLine Skip label ($"{armName}: {e}")
                    | Some (armName, e) -> fail label ($"{armName}: {e}")
                    | None ->
                        let diffs =
                            arms
                            |> List.choose (fun (armName, r, _) ->
                                match r with
                                | Ok out ->
                                    (match compareRuns 3 cppText out with
                                     | Ok () -> None
                                     | Error ds -> Some ($"""{armName}: {(String.concat " | " ds)}"""))
                                | Error _ -> None)
                        if not diffs.IsEmpty then
                            fail label ($"""stdout differs from the C++ lane -- {(String.concat " ;; " diffs)}""")
                        else
                            // The knob must be LIVE: a bricked emission that
                            // is textually the serial one proves nothing.
                            let text a = arms |> List.pick (fun (n, _, t) -> if n = a then Some t else None)
                            let serial = text "serial"
                            let bricked = text "bricked-b2"
                            let liveness =
                                if name = "simplex_large" || name = "simplex_pool" then
                                    if serial = bricked then Some "the bricked arm emitted the SAME .ll as the serial arm"
                                    else None
                                else None
                            match liveness with
                            | Some why -> fail label why
                            | None -> pass label ($"3 arms agree ({(stripTiming cppText).Length} chars of output)")
            with ex -> fail label ($"harness exception: {ex.Message}")
    printFooter block
        [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = block; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = failedNames }

// ---------------------------------------------------------------------------
// Block 6: the benchmark (`blade test llvm-bench`)
// ---------------------------------------------------------------------------
//
// Two questions, two protocols, one block. Neither half can fail on a slow
// number -- a benchmark that reddens on a ratio is a flake generator, and the
// `timing` block already set that precedent here. Both halves DO fail on a
// refusal, a build error or a value disagreement, because those are coverage
// regressions wearing a benchmark's clothes: every fixture below is known to
// compile on both lanes, so one of them ceasing to is news.
//
// (a) CODEGEN SPEED -- the question the lane was actually built to answer
//     (plan-llvm-backend.md R6: "skipping the C++ front end entirely" is the
//     strongest honest case for this backend). Timed region is IRProgram to
//     executable: emit the text, write it, invoke the toolchain, link. LOWERING
//     IS OUTSIDE THE STOPWATCH and is redone per arm per rep, because it is
//     shared work neither back end can be credited or charged for.
//
//     THE EXECUTABLE CACHE IS PINNED OFF. `compileCppWithExtraSource` consults
//     a content-addressed exe cache under %LOCALAPPDATA%\Blade\exe-cache
//     (Build.fs:837-846) and `compileLlvmProgram` has no such thing, so leaving
//     it on would time a file copy against a compiler. It is pinned off for the
//     table and measured separately in one extra row, since "how long until I
//     have an exe" with the shipped defaults is a fair question with a
//     different answer.
//
// (b) RUNTIME -- three shapes at non-power-of-two extents (the ~7x cache
//     artifact at 2^k is documented in CLAUDE.md and would drown every effect
//     here), arms rotated round-robin so no arm is always first into a cold
//     cache, one warmup discarded per arm per round, 9 reps x 3 rounds = 27
//     samples per arm, medians reported with the observed spread.
//
//     TWO CLOCKS ARE REPORTED. The primary is the program's own
//     `<name> completed in <t>s` line, which both lanes emit around the same
//     region (all bindings, before the auto-print pass): C++ through
//     std::chrono::high_resolution_clock, the llvm lane through the shim's
//     `blade_now`. The secondary is the wall time of the whole process, which
//     is the same measurement on both sides and carries ~10-15 ms of Windows
//     process startup that compresses every ratio toward 1.0. Where they
//     disagree, the internal clock is the one about compute and the external
//     one is the one a user feels.
//
//     Values are compared across arms BEFORE any timing is reported. A fast
//     wrong answer is not a result.

/// Median of a sample; even counts average the two middle values. Sorts a
/// copy, so the caller's list is untouched.
let private median (xs: float list) : float =
    match xs with
    | [] -> nan
    | _ ->
        let a = xs |> List.sort |> Array.ofList
        if a.Length % 2 = 1 then a.[a.Length / 2]
        else (a.[a.Length / 2 - 1] + a.[a.Length / 2]) / 2.0

/// Rotate a list left by `n` (negative and oversized `n` both wrap). Used to
/// move a different arm to the front of every rep, so the arm that pays for a
/// cold page cache is not always the same one.
let private rotate (n: int) (xs: 'a list) : 'a list =
    match xs with
    | [] -> []
    | _ ->
        let k = ((n % xs.Length) + xs.Length) % xs.Length
        List.skip k xs @ List.take k xs

/// Load a bench fixture from tests/fixtures/llvm/ (same two-root rule as the
/// goldens: the source tree wins from the repo root, so a fixture edit takes
/// effect with no rebuild).
let private benchSource (name: string) : Result<string, string> =
    let path = Path.Combine(goldenRoot.Value, name + ".blade")
    if File.Exists path then
        try Ok (File.ReadAllText path) with ex -> Error ($"unreadable: {ex.Message}")
    else Error ($"fixture missing: {path}")

/// Parse the `<name> completed in <t>s` line the two lanes both print. None
/// when the line is absent, which the caller treats as a failed measurement
/// rather than as a zero.
let private parseCompleted (out: string) : float option =
    let marker = "completed in "
    out.Replace("\r\n", "\n").Split('\n')
    |> Array.tryPick (fun line ->
        let i = line.IndexOf marker
        if i < 0 then None
        else
            let tail = line.Substring(i + marker.Length).Trim()
            let tail = if tail.EndsWith "s" then tail.Substring(0, tail.Length - 1) else tail
            match Double.TryParse(tail, Globalization.NumberStyles.Float,
                                  Globalization.CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | _ -> None)

/// Which back end an arm drives.
type private RtLane =
    | LaneCpp
    | LaneLlvm

/// One timed build: how long the whole IRProgram-to-executable half took, and
/// how many bytes of source text the back end produced on the way.
type private BuildSample = { Ms: float; Bytes: int64 }

/// Build one program through one lane, timing emit + write + toolchain.
///
/// `progName` is the name baked into the artifact (it reaches the `completed
/// in` line and, on the llvm lane, a string constant in the .ll); `stem` names
/// the files, and must differ between arms or two arms would fight over one
/// .exe path.
let private timedBuild (lane: RtLane) (dir: string) (progName: string) (stem: string)
                       (source: string) : Result<BuildSample, string> =
    match Lowering.lower source with
    | Error e -> Error ("front end: " + e)
    | Ok ir ->
    match IRValidate.validateIR ir with
    | Error errs -> Error ("IR validation: " + String.concat "; " errs)
    | Ok ir ->
        clearArtifacts dir [ stem ]
        let sw = Stopwatch.StartNew()
        let outcome =
            match lane with
            | LaneCpp ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir progName
                // Both refusal channels are process-wide cells: leaving one
                // loaded would attribute this program's refusal to the next.
                CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                CodeGen.takeCodegenRefusalDiagnostics cppCode |> ignore
                CodeGen.getCudaFileContent () |> ignore
                let f = Path.Combine(dir, stem + ".cpp")
                File.WriteAllText(f, cppCode)
                match Build.compileCpp f dir with
                | Error e -> Error ("g++: " + e.Replace("\n", " "))
                | Ok _ -> Ok f
            | LaneLlvm ->
                match EmitLlvm.tryEmitProgramNamed progName ir with
                | Error r -> Error ("llvm lane refused: " + r)
                | Ok ll ->
                    let f = Path.Combine(dir, stem + ".ll")
                    File.WriteAllText(f, ll)
                    match Build.compileLlvmProgram f dir with
                    | Error e -> Error ("clang: " + e.Replace("\n", " "))
                    | Ok _ -> Ok f
        sw.Stop()
        match outcome with
        | Error e -> Error e
        | Ok f -> Ok { Ms = sw.Elapsed.TotalMilliseconds; Bytes = FileInfo(f).Length }

/// The six programs the codegen table covers, in report order: two pure
/// scalar, three dense (the pipeline being the large-kernel one of the set),
/// one compact symmetric.
let private codegenPrograms =
    [ "bench_scalar_chain",      "scalar"
      "bench_scalar_calls",      "scalar + calls"
      "bench_dense_pipeline",    "dense, large kernel"
      "bench_dense_elementwise", "dense 1e7 map"
      "bench_dense_fold",        "dense 1e7 fold"
      "bench_sym_map",           "symmetric n=2003" ]

let private codegenReps = 5

/// Env pins for the codegen table: the numeric knobs at their defaults (so a
/// developer's shell cannot move the numbers) and the exe cache OFF (see the
/// block banner -- it exists on one lane only).
let private codegenEnv = defaultEmissionEnv @ [ "BLADE_EXE_CACHE", Some "0" ]

let runLlvmBenchCodegen () : BlockResult =
    printHeader "LLVM Bench: codegen speed (IRProgram -> executable)"
    let block = "LLVM Bench Codegen"
    let skipBlock reason =
        printfn "Skipped: %s" reason
        printFooter block [ "0 passed"; "0 failed"; "1 skipped" ]
        { Block = block; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    match Build.resolveClang () with
    | None -> skipBlock "no clang found (set BLADE_LLVM_CLANG, or install C:\\msys64\\clang64)."
    | Some clang ->
    if not Build.capabilities.Value.HasGpp then skipBlock "requires g++ for the C++ lane."
    else
    printfn "clang: %s" clang
    printfn "protocol: %d alternating reps per program, median; exe cache pinned OFF.\n" codegenReps
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let dir = outputDir
    Directory.CreateDirectory dir |> ignore
    CodeGen.deployRuntimeHeaders dir
    EmitLlvm.deployShim dir
    // Warm both toolchains before the first measured rep: the first invocation
    // of a 100 MB compiler binary pages it in, and the llvm lane's shim .o is
    // compiled once per output directory. Charging either to whichever program
    // happens to be first in the list would be an artifact, not a result.
    let rows = ResizeArray<string * float * float * int64 * int64 * float>()
    withEnv codegenEnv (fun () ->
        match benchSource (fst codegenPrograms.[0]) with
        | Error _ -> ()
        | Ok warmSrc ->
            timedBuild LaneCpp dir "warmup" "bench_warm_cpp" warmSrc |> ignore
            timedBuild LaneLlvm dir "warmup" "bench_warm_ll" warmSrc |> ignore)
    for (prog, kind) in codegenPrograms do
        match benchSource prog with
        | Error e ->
            failed <- failed + 1
            failedNames <- failedNames @ [ prog ]
            resultLine Fail prog e
        | Ok source ->
            let cppStem = "bench_cpp_" + Build.sanitizeFileName prog
            let llStem = "bench_ll_" + Build.sanitizeFileName prog
            let cppSamples = ResizeArray<BuildSample>()
            let llSamples = ResizeArray<BuildSample>()
            let mutable err = None
            withEnv codegenEnv (fun () ->
                for rep in 0 .. codegenReps - 1 do
                    // Alternate which lane goes first: a rep's second build
                    // runs against a warmer file cache than its first, and
                    // fixing the order would hand that to one lane every time.
                    let order =
                        if rep % 2 = 0 then [ LaneCpp, cppStem, cppSamples; LaneLlvm, llStem, llSamples ]
                        else [ LaneLlvm, llStem, llSamples; LaneCpp, cppStem, cppSamples ]
                    for (lane, stem, sink) in order do
                        if err.IsNone then
                            match timedBuild lane dir prog stem source with
                            | Error e -> err <- Some e
                            | Ok s -> sink.Add s)
            match err with
            | Some e ->
                failed <- failed + 1
                failedNames <- failedNames @ [ prog ]
                resultLine Fail prog e
            | None ->
                let cppMs = median [ for s in cppSamples -> s.Ms ]
                let llMs = median [ for s in llSamples -> s.Ms ]
                let cppBytes = (Seq.head cppSamples).Bytes
                let llBytes = (Seq.head llSamples).Bytes
                let ratio = if cppMs > 0.0 then llMs / cppMs else nan
                rows.Add($"{prog} ({kind})", cppMs, llMs, cppBytes, llBytes, ratio)
                passed <- passed + 1
                // The medians drive the table; the extremes go on the per-test
                // line, where a reader deciding whether to believe a 4x ratio
                // can see whether the two samples even overlap.
                resultLine Pass prog
                    (sprintf "g++ %.0f ms (%.0f-%.0f), clang %.0f ms (%.0f-%.0f), ratio %.2fx"
                        cppMs (Seq.min [ for s in cppSamples -> s.Ms ]) (Seq.max [ for s in cppSamples -> s.Ms ])
                        llMs (Seq.min [ for s in llSamples -> s.Ms ]) (Seq.max [ for s in llSamples -> s.Ms ])
                        ratio)
    if rows.Count > 0 then
        printfn ""
        printfn "  %-44s %9s %9s %7s %10s %10s" "program" "g++ ms" "clang ms" "ratio" "cpp bytes" "ll bytes"
        printfn "  %s" (String.replicate 94 "-")
        for (name, cppMs, llMs, cppB, llB, ratio) in rows do
            printfn "  %-44s %9.0f %9.0f %6.2fx %10d %10d" name cppMs llMs ratio cppB llB
        let ratios = [ for (_, _, _, _, _, r) in rows -> r ]
        let medRatio = median ratios
        printfn "  %s" (String.replicate 94 "-")
        printfn "  median ratio (clang lane / g++ lane): %.2fx  -- %s"
            medRatio
            (if medRatio < 1.0 then sprintf "the llvm lane reaches an executable %.2fx FASTER" (1.0 / medRatio)
             else sprintf "the llvm lane is %.2fx SLOWER to an executable" medRatio)
        // One extra row for the shipped defaults, where the C++ lane has a
        // content-addressed exe cache and the llvm lane does not. This is not
        // a codegen measurement; it is what a user re-running an unchanged
        // program actually waits for.
        match benchSource "bench_dense_pipeline" with
        | Error _ -> ()
        | Ok src ->
            let cachedMs =
                withEnv (defaultEmissionEnv @ [ "BLADE_EXE_CACHE", None ]) (fun () ->
                    // Prime the cache, then measure hits.
                    timedBuild LaneCpp dir "bench_dense_pipeline" "bench_cache_cpp" src |> ignore
                    [ for _ in 1 .. codegenReps ->
                        match timedBuild LaneCpp dir "bench_dense_pipeline" "bench_cache_cpp" src with
                        | Ok s -> s.Ms
                        | Error _ -> nan ] |> median)
            printfn "  exe cache (C++ lane only, shipped default ON): a WARM rebuild of"
            printfn "    bench_dense_pipeline costs %.0f ms instead of a g++ invocation." cachedMs
        // The one llvm-lane cost the table above amortizes away: the C shim's
        // object file, compiled once per output directory and reused by every
        // link after it. Deleting it and timing one more build prices it, so
        // the 4x claim is not resting on a hidden subsidy.
        match benchSource "bench_scalar_chain" with
        | Error _ -> ()
        | Ok src ->
            for f in Directory.GetFiles(dir, "blade_llvm_shim.*") do
                if not (f.EndsWith ".c") then (try File.Delete f with _ -> ())
            let coldMs =
                withEnv codegenEnv (fun () ->
                    match timedBuild LaneLlvm dir "bench_scalar_chain" "bench_shim_ll" src with
                    | Ok s -> s.Ms
                    | Error _ -> nan)
            printfn "  llvm shim: the build that also has to compile blade_llvm_shim.c costs"
            printfn "    %.0f ms (once per output directory; every later link reuses the .o)." coldMs
    printFooter block [ $"{passed} passed"; $"{failed} failed"; "0 skipped" ]
    { Block = block; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }

// --- runtime ---------------------------------------------------------------

/// One runtime arm: a label, the lane, and the env pins in force during
/// EMISSION. Nothing is pinned at run time -- every knob here is read at
/// codegen time and baked into the artifact, which is exactly why an arm is a
/// separate executable rather than a separate invocation.
type private RtArm =
    { Label: string
      Lane: RtLane
      Pins: (string * string option) list }

/// One runtime shape: a fixture and the arms to race over it. `Arms` must lead
/// with the C++ arm -- it is the value oracle every other arm is compared to.
type private RtShape =
    { Name: string
      Fixture: string
      Note: string
      Arms: RtArm list }

let private rtRounds = 3
let private rtReps = 9

let private armCpp = { Label = "cpp"; Lane = LaneCpp; Pins = [] }
let private armLlvm = { Label = "llvm"; Lane = LaneLlvm; Pins = [] }

let private rtShapes =
    [ { Name = "dense elementwise, n = 9,999,991"
        Fixture = "bench_dense_elementwise"
        Note = "memory-bandwidth bound: two ~80 MB pools"
        Arms = [ armCpp; armLlvm ] }
      { Name = "dense licensed fold, n = 9,999,991"
        Fixture = "bench_dense_fold"
        Note = "integer-valued cells: exact under any association, so the reassociated (default) and serial (repro) arms are value-comparable"
        Arms =
          [ armCpp
            { Label = "cpp-repro"; Lane = LaneCpp; Pins = [ "BLADE_FP_REPRO", Some "1" ] }
            armLlvm
            { Label = "llvm-repro"; Lane = LaneLlvm; Pins = [ "BLADE_FP_REPRO", Some "1" ] } ] }
      { Name = "symmetric map, n = 2003 (C(2004,2) = 2,007,006 cells)"
        Fixture = "bench_sym_map"
        Note = "the shape the brief named; ~3 ms of compute, close to first-touch noise"
        Arms =
          [ armCpp
            { Label = "llvm-serial"; Lane = LaneLlvm; Pins = [ "BLADE_LLVM_BRICKS", Some "off" ] }
            { Label = "llvm-bricked"; Lane = LaneLlvm; Pins = [ "BLADE_LLVM_BRICKS", Some "64" ] } ] }
      { Name = "symmetric map, n = 6007 (C(6008,2) = 18,048,028 cells)"
        Fixture = "bench_sym_large"
        Note = "9x the work: this is where the bricks verdict is read"
        Arms =
          [ armCpp
            { Label = "llvm-serial"; Lane = LaneLlvm; Pins = [ "BLADE_LLVM_BRICKS", Some "off" ] }
            { Label = "llvm-bricked"; Lane = LaneLlvm; Pins = [ "BLADE_LLVM_BRICKS", Some "64" ] } ] }
      { Name = "symmetric map, n = 6006 (2*3*7*11*13; C(6007,2) = 18,039,021 cells)"
        Fixture = "bench_sym_divisible"
        Note = "the divisibility control: B=66 divides 6006 exactly, B=64 leaves a ragged 54-wide last tile"
        Arms =
          [ armCpp
            { Label = "llvm-serial"; Lane = LaneLlvm; Pins = [ "BLADE_LLVM_BRICKS", Some "off" ] }
            { Label = "llvm-brick-64-ragged"; Lane = LaneLlvm; Pins = [ "BLADE_LLVM_BRICKS", Some "64" ] }
            { Label = "llvm-brick-66-exact"; Lane = LaneLlvm; Pins = [ "BLADE_LLVM_BRICKS", Some "66" ] } ] }
      // THE GRAM (row-operand reuse) SHAPE. Removed from this block once,
      // when a single run cost ~10 s because `reduce(x * y, (+))` allocated
      // and refilled a 402-cell temp PER OUTPUT CELL. The fold-fusion fix
      // consumes the producer instead, a run costs ~0.2-0.9 s, and thirty
      // reps across four arms is a normal bench again. It earns its place:
      // this is the shape where the derived brick policy fires (bm = 9.2 MB
      // clears the 8 MiB reuse threshold) and where bricks WIN -- the map
      // shapes above are the control that says they lose without reuse.
      { Name = "symmetric gram, n = 3001, d = 402 (row-operand reuse; bm = 9.2 MB)"
        Fixture = "bench_sym_gram_small"
        Note = "the shape class where S0 bricks win: serial re-streams bm per output row, bricked once per row-tile; all arms reassoc-licensed (cells are exact quarters, so association cannot move the printed value)"
        Arms =
          [ { armCpp with Pins = [ "BLADE_FP_REASSOC", Some "1" ] }
            { Label = "llvm-auto"; Lane = LaneLlvm; Pins = [ "BLADE_FP_REASSOC", Some "1" ] }
            { Label = "llvm-serial"; Lane = LaneLlvm; Pins = [ "BLADE_FP_REASSOC", Some "1"; "BLADE_LLVM_BRICKS", Some "off" ] }
            { Label = "llvm-brick-64"; Lane = LaneLlvm; Pins = [ "BLADE_FP_REASSOC", Some "1"; "BLADE_LLVM_BRICKS", Some "64" ] } ] } ]

/// Build one arm's executable, or say why not.
let private buildRtArm (dir: string) (shape: RtShape) (arm: RtArm) (source: string) : Result<string, string> =
    let stem = Build.sanitizeFileName ($"rtb_{arm.Label}_{shape.Fixture}")
    clearArtifacts dir [ stem ]
    withEnv (defaultEmissionEnv @ arm.Pins @ [ "BLADE_EXE_CACHE", Some "0" ]) (fun () ->
        match Lowering.lower source with
        | Error e -> Error ("front end: " + e)
        | Ok ir ->
        match IRValidate.validateIR ir with
        | Error errs -> Error ("IR validation: " + String.concat "; " errs)
        | Ok ir ->
            match arm.Lane with
            | LaneCpp ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir shape.Fixture
                CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                CodeGen.takeCodegenRefusalDiagnostics cppCode |> ignore
                CodeGen.getCudaFileContent () |> ignore
                let f = Path.Combine(dir, stem + ".cpp")
                File.WriteAllText(f, cppCode)
                match Build.compileCpp f dir with
                | Error e -> Error ("g++: " + e.Replace("\n", " "))
                | Ok exe -> Ok exe
            | LaneLlvm ->
                match EmitLlvm.tryEmitProgramNamed shape.Fixture ir with
                | Error r -> Error ("llvm lane refused: " + r)
                | Ok ll ->
                    let f = Path.Combine(dir, stem + ".ll")
                    File.WriteAllText(f, ll)
                    match Build.compileLlvmProgram f dir with
                    | Error e -> Error ("clang: " + e.Replace("\n", " "))
                    | Ok exe -> Ok exe)

let runLlvmBenchRuntime () : BlockResult =
    printHeader "LLVM Bench: runtime"
    let block = "LLVM Bench Runtime"
    let skipBlock reason =
        printfn "Skipped: %s" reason
        printFooter block [ "0 passed"; "0 failed"; "1 skipped" ]
        { Block = block; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    match Build.resolveClang () with
    | None -> skipBlock "no clang found (set BLADE_LLVM_CLANG, or install C:\\msys64\\clang64)."
    | Some _ ->
    if not Build.capabilities.Value.HasGpp then skipBlock "requires g++ for the C++ lane."
    else
    printfn "protocol: %d rounds x %d reps (+1 warmup discarded per arm per round), arms rotated; medians.\n"
        rtRounds rtReps
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let dir = outputDir
    Directory.CreateDirectory dir |> ignore
    CodeGen.deployRuntimeHeaders dir
    EmitLlvm.deployShim dir
    for shape in rtShapes do
        printSubHeader shape.Name
        printfn "  (%s)" shape.Note
        match benchSource shape.Fixture with
        | Error e ->
            failed <- failed + 1
            failedNames <- failedNames @ [ shape.Fixture ]
            resultLine Fail shape.Fixture e
        | Ok source ->
            let built = shape.Arms |> List.map (fun a -> a, buildRtArm dir shape a source)
            let buildErr = built |> List.tryPick (fun (a, r) -> match r with Error e -> Some (a.Label, e) | _ -> None)
            match buildErr with
            | Some (label, e) ->
                failed <- failed + 1
                failedNames <- failedNames @ [ $"{shape.Fixture}/{label}" ]
                resultLine Fail shape.Fixture ($"arm {label} did not build: {e}")
            | None ->
                let exes = built |> List.map (fun (a, r) -> a, (match r with Ok e -> e | Error _ -> ""))
                // ---- values before timing --------------------------------
                let firstRuns = exes |> List.map (fun (a, exe) -> a, runExeCapture exe)
                let runErr =
                    firstRuns |> List.tryPick (fun (a, r) ->
                        match r with
                        | Error e -> Some (a.Label, e)
                        | Ok (code, _, err) when code <> 0 -> Some (a.Label, $"exits {code}: {(err.Trim())}")
                        | _ -> None)
                match runErr with
                | Some (label, e) ->
                    failed <- failed + 1
                    failedNames <- failedNames @ [ $"{shape.Fixture}/{label}" ]
                    resultLine Fail shape.Fixture ($"arm {label} did not run: {e}")
                | None ->
                    let outs = firstRuns |> List.map (fun (a, r) -> a.Label, (match r with Ok (_, o, _) -> o | Error _ -> ""))
                    let oracle = snd outs.Head
                    let mismatches =
                        outs
                        |> List.skip 1
                        |> List.choose (fun (label, o) ->
                            match compareRuns 3 oracle o with
                            | Ok () -> None
                            | Error ds -> Some ($"""{label}: {(String.concat " | " ds)}"""))
                    if not mismatches.IsEmpty then
                        failed <- failed + 1
                        failedNames <- failedNames @ [ shape.Fixture ]
                        resultLine Fail shape.Fixture
                            ($"""arms disagree BEFORE timing -- {(String.concat " ;; " mismatches)}""")
                    else
                    // ---- the rotated timing loop -----------------------
                    let inner = Dictionary<string, ResizeArray<float>>()
                    let outer = Dictionary<string, ResizeArray<float>>()
                    for (a, _) in exes do
                        inner.[a.Label] <- ResizeArray<float>()
                        outer.[a.Label] <- ResizeArray<float>()
                    let mutable measureErr = None
                    let runOnce (label: string) (exe: string) (record: bool) =
                        let sw = Stopwatch.StartNew()
                        let r = runExeCapture exe
                        sw.Stop()
                        match r with
                        | Ok (0, out, _) ->
                            if record then
                                match parseCompleted out with
                                | Some secs ->
                                    inner.[label].Add(secs * 1000.0)
                                    outer.[label].Add(sw.Elapsed.TotalMilliseconds)
                                | None -> measureErr <- Some (label, "no `completed in` line in the output")
                        | Ok (code, _, err) -> measureErr <- Some (label, $"exits {code}: {(err.Trim())}")
                        | Error e -> measureErr <- Some (label, e)
                    for round in 0 .. rtRounds - 1 do
                        for (a, exe) in exes do
                            if measureErr.IsNone then runOnce a.Label exe false
                        for rep in 0 .. rtReps - 1 do
                            for (a, exe) in rotate (round * rtReps + rep) exes do
                                if measureErr.IsNone then runOnce a.Label exe true
                    match measureErr with
                    | Some (label, e) ->
                        failed <- failed + 1
                        failedNames <- failedNames @ [ $"{shape.Fixture}/{label}" ]
                        resultLine Fail shape.Fixture ($"arm {label} failed mid-measurement: {e}")
                    | None ->
                        let baseline = median (List.ofSeq inner.[(fst exes.Head).Label])
                        printfn ""
                        printfn "    %-14s %11s %11s %11s %9s %11s"
                            "arm" "inner med" "inner min" "inner max" "vs cpp" "outer med"
                        printfn "    %s" (String.replicate 76 "-")
                        for (a, _) in exes do
                            let ins = List.ofSeq inner.[a.Label]
                            let outs2 = List.ofSeq outer.[a.Label]
                            let m = median ins
                            printfn "    %-14s %9.3f ms %9.3f ms %9.3f ms %8.2fx %9.3f ms"
                                a.Label m (List.min ins) (List.max ins)
                                (if baseline > 0.0 then m / baseline else nan)
                                (median outs2)
                        printfn ""
                        passed <- passed + 1
                        resultLine Pass shape.Fixture
                            ($"{exes.Length} arms agree; {(rtRounds * rtReps)} samples each")
    printFooter block [ $"{passed} passed"; $"{failed} failed"; "0 skipped" ]
    { Block = block; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }

/// `blade test llvm-bench` -- the two benchmark halves and a roll-up. Like
/// `blade test llvm`, standalone only: it spawns hundreds of native compiler
/// and executable invocations and has no business inside the default suite.
let runLlvmBench () : int =
    let blocks = [ runLlvmBenchCodegen (); runLlvmBenchRuntime () ]
    printGrandTotal blocks
    if blocks |> List.sumBy (_.Failed) = 0 then 0 else 1

// ---------------------------------------------------------------------------
// Block: the runtime shim's build identity and its retained-block cache
// (bench/notes/microkernel-sketches-review.md (removed 2026-10-24; in git history at cf2028cc) M0 + M1)
// ---------------------------------------------------------------------------

/// A C driver over the shim's allocator ABI. It reads the cache counters
/// (`blade_pool_cache_stats`, compiled in only under BLADE_POOL_CACHE_STATS)
/// and asserts per mode: with the cache ON a released pool comes back to the
/// next request of the same size and a recur-shaped workload (300 steps x 8
/// rows of 20011 doubles) makes at most 8 fresh allocations; OFF, every
/// allocation is fresh. Both modes: 64-byte payloads, null-safe free,
/// zero-cell pools, the size band, the budget.
let private shimPoolDriver = """
#include <stdio.h>
void *blade_alloc_cells(long long n, long long cellBytes);
void blade_free(void *p);
void blade_pool_cache_stats(long long *out);
static int pass = 0, total = 0;
static void check(const char *name, int ok) {
    total++;
    if (ok) { pass++; printf("  [PASS]: %s\n", name); } else printf("  [FAIL]: %s\n", name);
}
static long long st(int k) { long long s[5]; blade_pool_cache_stats(s); return s[k]; }
int main(void) {
    long long s0[5]; blade_pool_cache_stats(s0);
    int on = (int)s0[4];
    printf("  [info]: cache %s\n", on ? "on" : "off");
    blade_free((void *)0);
    check("free_null_is_noop", 1);
    void *z = blade_alloc_cells(0, 8);
    check("zero_cell_pool_nonnull_aligned", z != 0 && ((unsigned long long)z & 63u) == 0);
    blade_free(z);
    void *a = blade_alloc_cells(3000, 8);
    check("payload_aligned_64", ((unsigned long long)a & 63u) == 0);
    ((double *)a)[2999] = 1.0;
    blade_free(a);
    long long r0 = st(1);
    void *b = blade_alloc_cells(3000, 8);
    if (on) check("same_size_reuses_released_pool", b == a && st(1) == r0 + 1);
    else    check("off_never_reuses", st(1) == 0);
    blade_free(b);
    long long t0 = st(2);
    void *small = blade_alloc_cells(100, 8);
    void *huge = blade_alloc_cells(600000, 8);
    blade_free(small); blade_free(huge);
    check("small_and_huge_not_retained", st(2) == t0);
    void *x = blade_alloc_cells(393216, 8), *y = blade_alloc_cells(393216, 8), *w = blade_alloc_cells(393216, 8);
    long long e0 = st(3);
    blade_free(x); blade_free(y); blade_free(w);
    /* the 24 KB pool above is still retained too: the third 3 MB block evicts
     * it AND the first 3 MB block to stay within the 8 MB budget */
    if (on) check("budget_evicts_oldest", st(3) == e0 + 2);
    long long f0 = st(0), u0 = st(1);
    double sum = 0.0;
    int step, k, i;
    for (step = 0; step < 300; step++) {
        double *t[8];
        for (k = 0; k < 8; k++) {
            t[k] = (double *)blade_alloc_cells(20011, 8);
            for (i = 0; i < 20011; i++) t[k][i] = (double)(step + k);
        }
        for (k = 0; k < 8; k++) { sum += t[k][20010]; blade_free(t[k]); }
    }
    long long fresh = st(0) - f0, reused = st(1) - u0;
    printf("  [info]: recur-shaped tripwire: fresh=%lld reused=%lld (of 2400 allocations)\n", fresh, reused);
    if (on) check("recur_tripwire_fresh_le_8", fresh <= 8 && reused >= 2392);
    else    check("recur_tripwire_off_all_fresh", fresh == 2400 && reused == 0);
    check("recur_values", sum == (double)(8 * (299 * 300 / 2) + 300 * 28));
    printf("SHIM POOL TESTS: %d/%d passed\n", pass, total);
    return pass == total ? 0 : 1;
}
"""

/// BLADE_POOL_RETAIN (round-3 A4, gap G52) through the real shim source: the
/// 48 MB recur trajectory released once. Under a policy whose block cap admits
/// it, it is RETAINED (not freed), handed back to the next request of its
/// size, and EVICTED by the shim's atexit flush -- observed by an atexit hook
/// registered before the shim's own (so it runs after it). Under the default,
/// or a cap below 48 MB, it is freed at once and nothing is evicted at exit.
let private shimRetainDriver = """
#include <stdio.h>
#include <stdlib.h>
void *blade_alloc_cells(long long n, long long cellBytes);
void blade_free(void *p);
void blade_pool_cache_stats(long long *out);
void blade_pool_cache_policy(long long *out);
static int pass = 0, total = 0;
static long long evicted_before_exit = 0;
static void check(const char *name, int ok) {
    total++;
    if (ok) { pass++; printf("  [PASS]: %s\n", name); } else printf("  [FAIL]: %s\n", name);
}
static long long st(int k) { long long s[5]; blade_pool_cache_stats(s); return s[k]; }
static void after_shim_flush(void) {
    printf("  [atexit]: evicted_at_exit=%lld\n", st(3) - evicted_before_exit);
    fflush(stdout);
}
int main(void) {
    long long pol[3];
    long long traj = 301LL * 20011LL;
    int kept;
    void *a, *b;
    long long t0, r0;
    atexit(after_shim_flush);              /* before the shim's: runs AFTER it */
    blade_pool_cache_policy(pol);
    kept = traj * 8 <= pol[0];
    printf("  [info]: shim policy max_block=%lld budget=%lld arm=%lld; a 48 MB release is %s\n",
           pol[0], pol[1], pol[2], kept ? "RETAINED" : "freed at once");
#if defined(EXPECT_MAX)
    check("shim_policy_reads_the_arm_defines", pol[2] == 1 && pol[0] == EXPECT_MAX && pol[1] == EXPECT_BUDGET);
#else
    check("shim_policy_default_is_4mb_8mb", pol[2] == 0 && pol[0] == (4LL << 20) && pol[1] == (8LL << 20));
#endif
    t0 = st(2); r0 = st(1);
    a = blade_alloc_cells(traj, 8);
    ((double *)a)[traj - 1] = 1.0;
    blade_free(a);
    if (kept) {
        int retained = st(2) == t0 + 1;
        b = blade_alloc_cells(traj, 8);
        check("shim_48mb_release_retained_then_reused", retained && b == a && st(1) == r0 + 1);
        blade_free(b);                      /* retained again: the exit flush evicts it */
    } else {
        check("shim_48mb_release_freed_inside", st(2) == t0);
    }
    evicted_before_exit = st(3);
    printf("SHIM RETAIN TESTS: %d/%d passed\n", pass, total);
    return pass == total ? 0 : 1;
}
"""

let runLlvmRuntimeShimTests () : BlockResult =
    printHeader "LLVM Runtime Shim (object identity, retained-block cache)"
    let block = "LLVM Runtime Shim"
    match Build.resolveClang () with
    | None ->
        printfn "Skipped: no clang found (set BLADE_LLVM_CLANG, or install C:\\msys64\\clang64)."
        printFooter block [ "0 passed"; "0 failed"; "1 skipped" ]
        { Block = block; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    | Some clang ->
    let dir = Path.GetFullPath(Path.Combine(outputDir, "llvm_shim_identity"))
    (try if Directory.Exists dir then Directory.Delete(dir, true) with _ -> ())
    Directory.CreateDirectory dir |> ignore
    EmitLlvm.deployShim dir
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let record (name: string) (ok: bool) (detail: string) =
        if ok then
            passed <- passed + 1
            resultLine Pass name ""
        else
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name detail
    // M0: a normal, a memcheck, then a normal build of the shim in ONE
    // directory. The memcheck object must be its own (the cache compiled
    // out), and the second normal build must reuse the first's object rather
    // than recompile or pick up the memcheck one.
    let build memcheck =
        withEnv [ "BLADE_MEMCHECK", (if memcheck then Some "1" else None) ] (fun () -> Build.buildLlvmShim clang dir)
    match build false, build true with
    | Ok (o1, id1), Ok (o2, id2) ->
        let t1 = File.GetLastWriteTimeUtc o1
        record "memcheck_shim_is_a_distinct_object" (o1 <> o2 && id1 <> id2 && File.Exists o1 && File.Exists o2)
               $"normal {o1} / memcheck {o2}"
        (match build false with
         | Ok (o3, id3) ->
             record "same_configuration_reuses_its_object" (o3 = o1 && id3 = id1 && File.GetLastWriteTimeUtc o3 = t1)
                    $"rebuilt or switched object: {o3}"
         | Error e -> record "same_configuration_reuses_its_object" false e)
    | r1, r2 ->
        let err = function Error (e: string) -> e | Ok _ -> "ok"
        record "memcheck_shim_is_a_distinct_object" false ($"{err r1} / {err r2}")
    // M1: the allocator itself, both modes, through the real shim source.
    let driver = Path.Combine(dir, "shim_pool_driver.c")
    File.WriteAllText(driver, shimPoolDriver)
    let shimSrc = Path.Combine(dir, EmitLlvm.shimFileName)
    for (mode, defines) in [ "on", "-DBLADE_POOL_CACHE_STATS=1"; "off", "-DBLADE_POOL_CACHE_STATS=1 -DBLADE_POOL_CACHE_OFF=1" ] do
        let exe = Path.Combine(dir, $"shim_pool_{mode}{Platforms.exeExtension}")
        match Build.runProc clang ($"-O2 {defines} -o \"{exe}\" \"{driver}\" \"{shimSrc}\"") 120000 with
        | Error e -> record ($"shim_pool_cache_{mode}") false ($"driver compile failed: {e}")
        | Ok () ->
            match runExeCapture exe with
            | Error e -> record ($"shim_pool_cache_{mode}") false e
            | Ok (code, out, _) ->
                printf "%s" out
                let ok = code = 0 && out.Contains "SHIM POOL TESTS:" && not (out.Contains "[FAIL]")
                record ($"shim_pool_cache_{mode}") ok ($"exit {code}")
    // BLADE_POOL_RETAIN (round-3 A4, gap G52). (a) The policy is part of the
    // shim object's identity (so of llvmExeCacheKey): the arm and a pair each
    // build their own object, `0` and a malformed value build the DEFAULT one,
    // and the defines are exactly the twin pair the C++ lane emits.
    // Every default case says `off` explicitly: an UNSET variable falls back to
    // blade.toolchain.json, which may have persisted the arm. Unset is tested
    // once, against an isolated empty toolchain file (selector and parse cache
    // restored afterwards).
    let buildWith (v: string option) =
        withEnv [ "BLADE_MEMCHECK", None; "BLADE_POOL_RETAIN", v ] (fun () -> Build.llvmShimDefines (), Build.buildLlvmShim clang dir)
    let emptyToolchain = Path.Combine(dir, "pool_tc_empty.json")
    File.WriteAllText(emptyToolchain, "{}")
    let buildUnsetIsolated () =
        withEnv [ "BLADE_TOOLCHAIN_FILE", Some emptyToolchain ] (fun () ->
            Blade.Toolchain.refresh ()
            try buildWith None finally Blade.Toolchain.refresh ())
    (match buildWith (Some "off"), buildWith (Some "1"), buildWith (Some "32:64"), buildWith (Some "0"), buildWith (Some "bogus"), buildUnsetIsolated () with
     | (d0, Ok (o0, i0)), (d1, Ok (o1, i1)), (d2, Ok (o2, i2)), (dz, Ok (oz, iz)), (db, Ok (ob, ib)), (du, Ok (ou, iu)) ->
         record "pool_retain_default_adds_no_shim_define" (d0 = "" && dz = "" && db = "" && du = "") $"defines '{d0}' / '{dz}' / '{db}' / unset '{du}'"
         record "pool_retain_unset_isolated_is_the_default_identity" (iu = i0 && ou = o0) $"unset {iu} vs off {i0}"
         record "pool_retain_arm_shim_defines" (d1 = " -DBLADE_POOL_MAX_BLOCK=67108864 -DBLADE_POOL_BUDGET=100663296"
                                                && d2 = " -DBLADE_POOL_MAX_BLOCK=33554432 -DBLADE_POOL_BUDGET=67108864") $"'{d1}' / '{d2}'"
         record "pool_retain_changes_the_shim_identity" (i1 <> i0 && i2 <> i0 && i1 <> i2 && o1 <> o0 && o2 <> o0)
                $"default {i0} / arm {i1} / pair {i2}"
         record "pool_retain_off_and_malformed_keep_the_default_identity" (iz = i0 && ib = i0 && oz = o0 && ob = o0)
                $"off {iz} / malformed {ib} vs default {i0}"
     | _ -> record "pool_retain_shim_builds" false "a shim build under BLADE_POOL_RETAIN failed")
    // (b) The allocator under each policy: the 48 MB release retained and
    // evicted at exit under the arm, freed at once under the default and a
    // 32 MB cap.
    let retainDriver = Path.Combine(dir, "shim_retain_driver.c")
    File.WriteAllText(retainDriver, shimRetainDriver)
    for (mode, setting, keptExpected) in [ "default", None, false; "retain1", Some "1", true; "retain32_64", Some "32:64", false ] do
        let p = Blade.Toolchain.parsePoolRetention setting "test"
        let defs =
            (Blade.Toolchain.poolRetentionDefines p |> List.map (fun (n, b) -> $" -D{n}={b}") |> String.concat "")
            + (if p.IsDefault then "" else $" -DEXPECT_MAX={p.MaxBlockBytes}LL -DEXPECT_BUDGET={p.BudgetBytes}LL")
        let exe = Path.Combine(dir, $"shim_retain_{mode}{Platforms.exeExtension}")
        let name = $"shim_pool_retain_{mode}"
        match Build.runProc clang ($"-O2 -DBLADE_POOL_CACHE_STATS=1{defs} -o \"{exe}\" \"{retainDriver}\" \"{shimSrc}\"") 120000 with
        | Error e -> record name false ($"driver compile failed: {e}")
        | Ok () ->
            match runExeCapture exe with
            | Error e -> record name false e
            | Ok (code, out, _) ->
                printf "%s" out
                let evictLine = if keptExpected then "evicted_at_exit=1" else "evicted_at_exit=0"
                let ok = code = 0 && out.Contains "SHIM RETAIN TESTS:" && not (out.Contains "[FAIL]") && out.Contains evictLine
                record name ok ($"exit {code}; wanted {evictLine}")
    printFooter block [ $"{passed} passed"; $"{failed} failed"; "0 skipped" ]
    { Block = block; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }

// ---------------------------------------------------------------------------
// Block: MUST-NATIVE fixtures (round 3 wave 4, W4B-C; gaps G64 / G67)
// ---------------------------------------------------------------------------
//
// The differential maps ANY llvm emission refusal to a skip, so a program
// claimed native could lose the lane while `blade test llvm <dir>` stays
// green. The files below are claimed NATIVE: each is lowered,
// `EmitLlvm.tryEmitProgramNamed` must SUCCEED (a refusal FAILS here),
// the .ll is compiled and run, and its stdout must carry every `// EXPECT:`
// pin of the file verbatim -- index-types/544's sign Booleans (`dn_neg`,
// `dk_neg`) included. Runs after the differential of the category it names.
let private mustNativeFixtures : (string * string) list =
    [ "index-types", "544_whole_array_kernel_over_dense_is_call.blade"
      "symmetry", "144_packed_comm_contraction_direct_call_accepted.blade"
      "symmetry", "146_zero_level_compact_application_is_call.blade" ]

let runLlvmMustNativeTestsFor (categories: string list) : BlockResult =
    let block = "LLVM Must-Native"
    let wanted = mustNativeFixtures |> List.filter (fun (c, _) -> List.contains c categories)
    if wanted.IsEmpty then { Block = block; Passed = 0; Failed = 0; Skipped = 0; FailedNames = [] } else
    printHeader "LLVM Must-Native (a refusal fails)"
    match Build.resolveClang () with
    | None ->
        printfn "Skipped: no clang found (set BLADE_LLVM_CLANG, or install C:\\msys64\\clang64)."
        printFooter block [ "0 passed"; "0 failed"; "1 skipped" ]
        { Block = block; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    | Some _ ->
    let dir = Path.GetFullPath(Path.Combine(outputDir, "llvm_must_native"))
    Directory.CreateDirectory dir |> ignore
    EmitLlvm.deployShim dir
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    for (cat, file) in wanted do
        let name = $"{cat}/{file}: native llvm route, EXPECT pins"
        let verdict =
            let candidates =
                [ Path.Combine(Directory.GetCurrentDirectory(), "tests", "corpus", cat, file)
                  Path.Combine(AppContext.BaseDirectory, "tests", "corpus", cat, file) ]
            match candidates |> List.tryFind File.Exists with
            | None -> Error "corpus file not found"
            | Some path ->
            let source = File.ReadAllText path
            let pins =
                source.Replace("\r\n", "\n").Split('\n')
                |> Array.choose (fun l -> if l.StartsWith "// EXPECT: " then Some (l.Substring("// EXPECT: ".Length).Trim()) else None)
                |> List.ofArray
            if pins.IsEmpty then Error "the fixture carries no EXPECT pin" else
            match fst (Lowering.lowerCaptured source) with
            | Error e -> Error ($"lower: {e}")
            | Ok ir0 ->
            match IRValidate.validateIR ir0 with
            | Error errs -> Error ($"""IR validation: {String.concat "; " errs}""")
            | Ok ir ->
            let stem = "mustnative_" + Build.sanitizeFileName (Path.GetFileNameWithoutExtension file)
            match (try EmitLlvm.tryEmitProgramNamed stem ir with ex -> Error ("raised: " + ex.Message)) with
            | Error why -> Error ($"the llvm lane REFUSED a must-native fixture: {why}")
            | Ok ll ->
            let llFile = Path.Combine(dir, stem + ".ll")
            File.WriteAllText(llFile, ll)
            match Build.compileLlvmProgram llFile dir with
            | Error e -> Error ($"llvm compile: {e}")
            | Ok exe ->
            match runExeCapture exe with
            | Error e -> Error e
            | Ok (code, out, err) when code <> 0 -> Error ($"exit {code}: {err.Trim()}")
            | Ok (_, out, _) ->
                let lines = out.Replace("\r\n", "\n").Split('\n') |> Array.map (fun l -> l.Trim())
                match pins |> List.filter (fun p -> not (Array.contains p lines)) with
                | [] -> Ok ($"""native; {pins.Length} pins: {String.concat "; " pins}""")
                | missing -> Error ($"""native output lacks {String.concat "; " missing}: {out.Trim()}""")
        match verdict with
        | Ok note ->
            passed <- passed + 1
            resultLine Pass name note
        | Error e ->
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name e
    printFooter block [ $"{passed} passed"; $"{failed} failed"; "0 skipped" ]
    { Block = block; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }

// ---------------------------------------------------------------------------
// Entry points for `blade test llvm [category]`
// ---------------------------------------------------------------------------

/// `blade test llvm` -- both blocks plus a combined roll-up. NOT part of the
/// default `blade test` suite (same policy as cuda/mpi/omp/timing): it spawns
/// two native compilers per corpus file, and it must not be able to redden a
/// run of the C++ lane's own suite.
let runLlvmTests () : int =
    let blocks =
        [ runLlvmCompareRuleTests ()
          runLlvmGoldenTests ()
          runLlvmFactTests ()
          runLlvmTilesRawBitTests ()
          runSimplexAgreementTests ()
          runLlvmSimplexGate ()
          runLlvmRuntimeShimTests ()
          runLlvmDifferentialTests ()
          runLlvmMustNativeTestsFor defaultCategories ]
    printGrandTotal blocks
    if blocks |> List.sumBy (_.Failed) = 0 then 0 else 1

/// The reserved words `runLlvmCategory` takes in place of a corpus directory,
/// after its normalization (lower-cased, leading dashes dropped). The CLI's
/// unknown-category guard reads this list, so a word added to the match below
/// must be added here too -- a word missing here is refused before it runs
/// (CI's `test llvm goldens` step was, once).
let llvmCategoryWords =
    [ "all"; "corpus"; "goldens"; "pins"; "facts"; "tiles"; "blocks"; "simplex"; "bricks"; "bench"; "shim" ]

let isLlvmCategoryWord (cat: string) =
    List.contains (cat.ToLower().TrimStart('-')) llvmCategoryWords

/// `blade test llvm <category>` -- the differential over one named corpus
/// directory (the literal tests/corpus/<dir> name, as `test interp <dir>`
/// takes). The reserved word `all` (alias `corpus`) sweeps EVERY single-file
/// category instead of the seven the lane was grown against, and reports what
/// fraction of the corpus the lane can actually emit. The reserved word
/// `goldens` (alias `pins`) runs the THREE
/// toolchain-free blocks instead -- compare rules, emission pins and the fact
/// layer -- which finish instantly and are what you want while iterating on
/// the emitter. `facts` runs the fact block alone.
let runLlvmCategory (cat: string) : int =
    match cat.ToLower().TrimStart('-') with
    | "all" | "corpus" ->
        // The differential over EVERY single-file corpus category, not the
        // seven `defaultCategories` sweeps. Those seven are the ones the lane
        // was grown against, so `blade test llvm` measures the lane where it is
        // known to work; this measures it against the whole corpus, which is
        // the only way the coverage figure means what it sounds like.
        //
        // Expect a much lower one, and expect that to be the POINT: the skip
        // histogram it prints is the ranked worklist for making the lane a
        // back end the suite could actually run on. Long -- two native
        // compilers per file across ~1900 files -- so it stays a verb you ask
        // for, never part of `blade test`.
        let r = runLlvmDifferentialTestsFor (Blade.Tests.Corpus.singleFileCategories ())
        if r.Failed = 0 then 0 else 1
    | "goldens" | "pins" ->
        let r = runLlvmCompareRuleTests ()
        let g = runLlvmGoldenTests ()
        let f = runLlvmFactTests ()
        let s = runSimplexAgreementTests ()
        if r.Failed + g.Failed + f.Failed + s.Failed = 0 then 0 else 1
    | "facts" ->
        let f = runLlvmFactTests ()
        let t = runLlvmBlockForceTwins ()
        if f.Failed + t.Failed = 0 then 0 else 1
    | "shim" ->
        let s = runLlvmRuntimeShimTests ()
        if s.Failed = 0 then 0 else 1
    | "tiles" ->
        // The tile core's cross-lane raw-bit block (M6): needs clang AND g++.
        let t = runLlvmTilesRawBitTests ()
        if t.Failed = 0 then 0 else 1
    | "blocks" | "simplex" | "bricks" ->
        let a = runSimplexAgreementTests ()
        let g = runLlvmSimplexGate ()
        if a.Failed + g.Failed = 0 then 0 else 1
    | "bench" ->
        // The same thing `blade test llvm-bench` runs, spelled as a
        // sub-word for symmetry with `goldens` / `facts` / `blocks`.
        runLlvmBench ()
    | _ ->
        let r = runLlvmDifferentialTestsFor [ cat ]
        let m = runLlvmMustNativeTestsFor [ cat ]
        if r.Failed + m.Failed = 0 then 0 else 1
