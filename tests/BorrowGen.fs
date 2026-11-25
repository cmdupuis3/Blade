// The BORROW GENERATOR (borrow round, 2026-11-23; review row T1 narrowed to
// the borrow class): seeded straight-line programs over a `let mut` array and
// the uncomputed values built over it, fed to the borrow oracle
// (tests/BorrowOracle.fs: interpreter readings A and B) and to the compiled
// C++ lane against the interpreter's own schedule.
//
// The grammar. A fixed prelude -- two `let mut` rank-1 arrays `x`, `y` over
// `I = Idx<5>`, a `let mut` rank-2 `m` over `J = Idx<3>, I`, a function that
// writes through a `mut` parameter (`bump`) and one that reads (`rd`) -- then
// 3 to 10 statements, each one of:
//   a map `method_for(s) <@> lambda(v) -> ...` over an array or an earlier
//   uncomputed value, its kernel affine or reading a captured array / map cell;
//   a co-iteration `method_for(zip(s, t)) <@> lambda(a, b) -> ...`;
//   an applied loop object (`object_for(k)` bound, then `o <@> s`);
//   an alias `let g2 = g`; a deferred tuple `(g, h)`; `|> compute`;
//   a stored row view `let r = m(i)`;
//   an element write `x(i) = e`, a write through a view `r(i) = e`, a rank-2
//   cell write `m(i, j) = e`, a whole reassignment `x = [...]`;
//   a call that writes (`bump(x)`), a call that reads (`rd(g)`), a lambda
//   that reads a captured value, called;
//   a function whose body is a bare fold over an uncomputed value, called
//   where it is declared and maybe again after a write; a block-bodied kernel;
//   a use: a fold `reduce(g, (+))`, a positional read `g(i)`, a reduction join
//   `reduce(g <&!> h, (+))`, a tuple member fold, a kernel capture
//   `reduce(method_for(y) <@> lambda(v) -> v + g(i), (+))`.
// Every number is a small integer-valued Float64, so no fold association or
// contraction can move a printed digit.
//
// Per program: the front end first (a refused program is counted by its
// codes); an accepted one runs through the oracle (A against B) and, unless
// `--interp-only`, is compiled and run (the C++ lane, the reproducible build
// the interpreter differential pins) and compared with the interpreter under
// its own schedule. A program that DIFFERS (A vs B), MISMATCHES (C++ vs
// interpreter) or is a COMPILER-BUG (g++ rejects the generated code, BL9002:
// its own verdict, counted, never a value mismatch) is shrunk -- statements
// removed one at a time while the same verdict holds -- and both forms are
// saved with their seed under `--out`.
//
// `blade test borrow-gen [--seed N] [--count K] [--out <dir>] [--interp-only]
// [--strict]`: program k of seed N is generated from its own Random(N, k), so
// any one program is reproducible alone. Census contract by default (a DIFFER
// is a finding; the rule is not merged yet); a MISMATCH between the C++ lane
// and the interpreter always fails, and so does a DIFFER of a program that
// writes nothing; `--strict` also fails every DIFFER. A COMPILER-BUG does not
// fail the block: it is listed (seed, program, shrunk text) for the C++ lane;
// so is a PRINT-SET difference (the lanes print different sets of bindings,
// the same value for every binding both print).
module Blade.Tests.BorrowGen

open System
open System.IO
open System.Text
open Blade
open Blade.IR
open Blade.Build
open Blade.Interp
open Blade.Tests.TestHarness
open Blade.Tests.Runner
open Blade.Tests.BorrowOracle

// ---------------------------------------------------------------------------
// The grammar
// ---------------------------------------------------------------------------

/// What the generator knows about the names in scope.
type private Scope =
    { /// Rank-1 arrays over I with storage: x, y, and stored row views.
      Stored: string list
      /// Uncomputed rank-1 values over I (maps, co-iterations, aliases,
      /// applied loop objects).
      Deferred: string list
      /// Computed rank-1 arrays over I.
      Computed: string list
      /// Deferred pairs of uncomputed values.
      Tuples: string list
      /// Float64 scalars.
      Scalars: string list
      /// Row views of m (also in Stored).
      Views: string list
      /// Functions whose body is a bare fold over an uncomputed value.
      Funcs: string list
      Next: int }

let private prelude =
    [ "type I = Idx<5>"
      "type J = Idx<3>"
      "let mut x: Array<Float64 like I> = [1.0, 2.0, 3.0, 4.0, 5.0]"
      "let mut y: Array<Float64 like I> = [2.0, 0.0, 1.0, 3.0, 1.0]"
      "let mut m: Array<Float64 like J, I> = [[1.0, 0.0, 2.0, 0.0, 1.0], [0.0, 3.0, 0.0, 1.0, 0.0], [2.0, 1.0, 1.0, 0.0, 2.0]]"
      "function bump(c: mut Array<Float64 like I>) -> Float64 = {"
      "    c(0) = c(0) + 1.0"
      "    c(0)"
      "}"
      "function rd(a: Array<Float64 like I>) -> Float64 = a(1) + a(2)" ]

let private pick (rng: Random) (xs: 'a list) : 'a = xs.[rng.Next xs.Length]

/// A small integer-valued Float64 literal.
let private lit (rng: Random) : string = sprintf "%d.0" (rng.Next(0, 4))

let private idx (rng: Random) : int = rng.Next(0, 5)

/// Any rank-1 array value over I a map may iterate.
let private source (rng: Random) (s: Scope) : string =
    pick rng (s.Stored @ s.Deferred @ s.Computed)

/// A Float64 expression reading the current scope.
let private scalarExpr (rng: Random) (s: Scope) : string =
    let cell () = sprintf "%s(%d)" (source rng s) (idx rng)
    match rng.Next 5 with
    | 0 -> lit rng
    | 1 | 2 -> sprintf "%s + %s" (cell ()) (lit rng)
    | 3 when not s.Scalars.IsEmpty -> sprintf "%s + %s" (pick rng s.Scalars) (lit rng)
    | _ -> cell ()

/// A map kernel body over the parameter `v`.
let private mapKernel (rng: Random) (s: Scope) : string =
    match rng.Next 5 with
    | 0 | 1 -> sprintf "v * %s + %s" (lit rng) (lit rng)
    // a block-bodied kernel
    | 2 -> sprintf "{ let t = v * %s; t + %s(%d) }" (lit rng) (source rng s) (idx rng)
    | _ -> sprintf "v + %s(%d)" (source rng s) (idx rng)

let private fresh (prefix: string) (s: Scope) : string * Scope =
    (sprintf "%s%d" prefix s.Next, { s with Next = s.Next + 1 })

/// One statement (one or two source lines) and the scope after it.
let private statement (rng: Random) (s: Scope) : string list * Scope =
    let roll = rng.Next 100
    let deferredPair () = (pick rng s.Deferred, pick rng s.Deferred)
    match roll with
    // a map
    | r when r < 16 ->
        let (g, s) = fresh "g" s
        [ sprintf "let %s = method_for(%s) <@> lambda(v) -> %s" g (source rng s) (mapKernel rng s) ],
        { s with Deferred = s.Deferred @ [ g ] }
    // a co-iteration
    | r when r < 22 ->
        let (g, s) = fresh "g" s
        [ sprintf "let %s = method_for(zip(%s, %s)) <@> lambda(a, b) -> a + b * %s" g (source rng s) (source rng s) (lit rng) ],
        { s with Deferred = s.Deferred @ [ g ] }
    // an applied loop object
    | r when r < 26 ->
        let (o, s) = fresh "o" s
        let (g, s) = fresh "g" s
        [ sprintf "let %s = object_for(lambda(v) -> v + %s)" o (lit rng)
          sprintf "let %s = %s <@> %s" g o (source rng s) ],
        { s with Deferred = s.Deferred @ [ g ] }
    // an alias of an uncomputed value
    | r when r < 30 && not s.Deferred.IsEmpty ->
        let (g, s) = fresh "g" s
        [ sprintf "let %s = %s" g (pick rng s.Deferred) ], { s with Deferred = s.Deferred @ [ g ] }
    // a deferred tuple
    | r when r < 33 && not s.Deferred.IsEmpty ->
        let (t, s) = fresh "t" s
        let (a, b) = deferredPair ()
        [ sprintf "let %s = (%s, %s)" t a b ], { s with Tuples = s.Tuples @ [ t ] }
    // compute
    | r when r < 38 && not s.Deferred.IsEmpty ->
        let (c, s) = fresh "c" s
        [ sprintf "let %s = %s |> compute" c (pick rng s.Deferred) ], { s with Computed = s.Computed @ [ c ] }
    // a stored row view of m
    | r when r < 41 ->
        let (v, s) = fresh "r" s
        [ sprintf "let %s = m(%d)" v (rng.Next 3) ], { s with Stored = s.Stored @ [ v ]; Views = s.Views @ [ v ] }
    // an element write
    | r when r < 54 ->
        [ sprintf "%s(%d) = %s" (pick rng [ "x"; "y" ]) (idx rng) (scalarExpr rng s) ], s
    // a write through a row view
    | r when r < 58 && not s.Views.IsEmpty ->
        [ sprintf "%s(%d) = %s" (pick rng s.Views) (idx rng) (scalarExpr rng s) ], s
    // a rank-2 cell write
    | r when r < 61 ->
        [ sprintf "m(%d, %d) = %s" (rng.Next 3) (idx rng) (scalarExpr rng s) ], s
    // a whole reassignment
    | r when r < 65 ->
        [ sprintf "%s = [%s, %s, %s, %s, %s]" (pick rng [ "x"; "y" ]) (lit rng) (lit rng) (lit rng) (lit rng) (lit rng) ], s
    // a call that writes through its mut parameter
    | r when r < 70 ->
        let (k, s) = fresh "s" s
        [ sprintf "let %s = bump(%s)" k (pick rng [ "x"; "y" ]) ], { s with Scalars = s.Scalars @ [ k ] }
    // a call that reads
    | r when r < 74 ->
        let (k, s) = fresh "s" s
        [ sprintf "let %s = rd(%s)" k (source rng s) ], { s with Scalars = s.Scalars @ [ k ] }
    // a lambda that reads a captured value, or folds one, called
    | r when r < 77 ->
        let (f, s) = fresh "f" s
        let (k, s) = fresh "s" s
        let body =
            if s.Deferred.IsEmpty || rng.Next 2 = 0 then sprintf "q + %s(%d)" (source rng s) (idx rng)
            else sprintf "q + reduce(%s, (+))" (pick rng s.Deferred)
        [ sprintf "let %s = lambda(q: Float64) -> %s" f body
          sprintf "let %s = %s(1.0)" k f ], { s with Scalars = s.Scalars @ [ k ] }
    // a FUNCTION BODY that uses an uncomputed value (the shapes lane r's
    // rule must see through: a bare fold, a positional read, a kernel
    // capture, a map over it), or that binds its own map over x, writes x and
    // folds the map; called here and maybe again later (the next arm)
    | r when r < 82 ->
        let (f, s) = fresh "h" s
        let (k, s) = fresh "s" s
        let body =
            match (if s.Deferred.IsEmpty then 3 else rng.Next 5) with
            | 0 -> sprintf "reduce(%s, (+))" (pick rng s.Deferred)
            | 1 -> sprintf "%s(%d)" (pick rng s.Deferred) (idx rng)
            | 2 -> sprintf "reduce(method_for(y) <@> lambda(v) -> v + %s(%d), (+))" (pick rng s.Deferred) (idx rng)
            | 3 ->
                sprintf "{ let l = method_for(x) <@> lambda(v) -> v + %s; x(%d) = %s; reduce(l, (+)) }"
                    (lit rng) (idx rng) (lit rng)
            | _ -> sprintf "{ let l = method_for(%s) <@> lambda(v) -> v * %s; reduce(l, (+)) }" (pick rng s.Deferred) (lit rng)
        [ sprintf "function %s() -> Float64 = %s" f body
          sprintf "let %s = %s()" k f ], { s with Funcs = s.Funcs @ [ f ]; Scalars = s.Scalars @ [ k ] }
    // a later call of such a function
    | r when r < 85 && not s.Funcs.IsEmpty ->
        let (k, s) = fresh "s" s
        [ sprintf "let %s = %s()" k (pick rng s.Funcs) ], { s with Scalars = s.Scalars @ [ k ] }
    // a fold
    | r when r < 88 && not s.Deferred.IsEmpty ->
        let (k, s) = fresh "s" s
        [ sprintf "let %s = reduce(%s, (+))" k (pick rng s.Deferred) ], { s with Scalars = s.Scalars @ [ k ] }
    // a positional read
    | r when r < 91 && not s.Deferred.IsEmpty ->
        let (k, s) = fresh "s" s
        [ sprintf "let %s = %s(%d)" k (pick rng s.Deferred) (idx rng) ], { s with Scalars = s.Scalars @ [ k ] }
    // a reduction join
    | r when r < 94 && not s.Deferred.IsEmpty ->
        let (k1, s) = fresh "s" s
        let (k2, s) = fresh "s" s
        let (a, b) = deferredPair ()
        [ sprintf "let (%s, %s) = reduce(%s <&!> %s, (+))" k1 k2 a b ], { s with Scalars = s.Scalars @ [ k1; k2 ] }
    // a tuple member fold
    | r when r < 97 && not s.Tuples.IsEmpty ->
        let (k, s) = fresh "s" s
        [ sprintf "let %s = reduce(%s[%d], (+))" k (pick rng s.Tuples) (rng.Next 2) ], { s with Scalars = s.Scalars @ [ k ] }
    // a kernel capture
    | _ when not s.Deferred.IsEmpty ->
        let (k, s) = fresh "s" s
        [ sprintf "let %s = reduce(method_for(y) <@> lambda(v) -> v + %s(%d), (+))" k (pick rng s.Deferred) (idx rng) ],
        { s with Scalars = s.Scalars @ [ k ] }
    // nothing uncomputed yet: a map
    | _ ->
        let (g, s) = fresh "g" s
        [ sprintf "let %s = method_for(%s) <@> lambda(v) -> %s" g (source rng s) (mapKernel rng s) ],
        { s with Deferred = s.Deferred @ [ g ] }

/// Program `k` of seed `seed`: its statements (each a list of lines; the
/// shrinker removes whole statements).
let generate (seed: int) (k: int) : string list list =
    let rng = Random(seed * 1000003 + k)
    let n = rng.Next(3, 11)
    let empty = { Stored = [ "x"; "y" ]; Deferred = []; Computed = []; Tuples = []; Scalars = []; Views = []; Funcs = []; Next = 1 }
    let rec go i (s: Scope) acc =
        if i = n then List.rev acc
        else
            let (lines, s') = statement rng s
            go (i + 1) s' (lines :: acc)
    go 0 empty []

/// The program text of a statement list.
let render (header: string) (stmts: string list list) : string =
    let sb = StringBuilder()
    sb.Append("// TEST: ").Append(header).Append('\n') |> ignore
    for l in prelude do sb.Append(l).Append('\n') |> ignore
    for st in stmts do
        for l in st do sb.Append(l).Append('\n') |> ignore
    sb.ToString()

// ---------------------------------------------------------------------------
// Checking one program
// ---------------------------------------------------------------------------

/// What one program came to.
type GenOutcome =
    /// The front end refused it, with its codes.
    | GenRefused of string list
    /// Accepted: the oracle's verdict, and the C++ lane against the
    /// interpreter (None when not compiled).
    | GenAccepted of Verdict * CompiledVerdict option

/// The compiled C++ lane against the interpreter's own schedule.
and CompiledVerdict =
    | Same
    /// The normalized outputs differ: the first entry that differs.
    | Mismatch of string
    /// g++ rejects the generated code (BL9002): a compiler bug of the C++
    /// lane, its own verdict (the lead's ruling), never a value mismatch.
    | CompilerBug of string
    /// The two lanes print DIFFERENT SETS of bindings but the same value for
    /// every binding both print (the lead's ruling): a print-rule divergence
    /// between the lanes, its own verdict, counted and saved, never a value
    /// mismatch. The names only one lane prints.
    | PrintSetOnly of string
    /// The compiled side could not be judged (a skip-class toolchain error or
    /// an interpreter skip): why.
    | NotJudged of string

/// The refusal codes of a source the front end refuses.
let private refusalCodes (source: string) : string list =
    underPipelineLock (fun () ->
        Blade.Runtime.runOnLargeStack (fun () ->
            let entryPath = Path.Combine(Directory.GetCurrentDirectory(), "__borrow_gen__.blade")
            match fst (Blade.Lowering.lowerFileDiag entryPath source) with
            | Error ds -> ds |> List.map _.Code |> List.distinct
            | Ok _ -> [ "(lowered on the second pass)" ]))

/// The interpreter's own schedule as a reading outcome (the compiled lanes'
/// twin), for the C++ comparison.
let private interpToday (program: IRProgram) (name: string) : Run.InterpResult =
    underPipelineLock (fun () ->
        Run.runProgramReading Blade.Interp.Value.ReadingToday program name Blade.Interp.Value.defaultLimits)

let private compareCompiled (compiledExit: int) (compiledOut: string) (interp: Run.InterpResult) : CompiledVerdict =
    if interp.ExitCode = Run.ExitUnsupported || interp.ExitCode = Run.ExitInterpBug then
        NotJudged ("interpreter: " + interp.Stderr.Trim())
    elif compiledExit <> 0 || interp.ExitCode <> 0 then
        // both failing is agreement on WHETHER; the codes are compared, the
        // location is not (R6 leaves failure order open)
        let code (s: string) =
            let m = Text.RegularExpressions.Regex.Match(s, @"BL\d{4}")
            if m.Success then m.Value else "?"
        if compiledExit <> 0 && interp.ExitCode <> 0 then
            if code compiledOut = code interp.Stderr then Same
            else Mismatch ($"both fail: compiled {code compiledOut}, interpreter {code interp.Stderr}")
        elif compiledExit <> 0 then Mismatch ($"compiled exits {compiledExit} ({code compiledOut}), interpreter runs")
        else Mismatch ($"interpreter fails ({code interp.Stderr}), compiled runs")
    else
        let a = printedEntries compiledOut
        let b = printedEntries interp.Stdout
        match firstDifference a b with
        | None -> Same
        | Some (n, vc, vi) ->
            let am = Map.ofList a
            let bm = Map.ofList b
            let sameOnShared = a |> List.forall (fun (k, v) -> match Map.tryFind k bm with Some w -> w = v | None -> true)
            if sameOnShared then
                let onlyC = a |> List.choose (fun (k, _) -> if bm.ContainsKey k then None else Some k)
                let onlyI = b |> List.choose (fun (k, _) -> if am.ContainsKey k then None else Some k)
                PrintSetOnly ($"""printed only by the C++ lane: {String.concat ", " onlyC}; only by the interpreter: {String.concat ", " onlyI}""")
            else Mismatch ($"`{n}` is `{vc}` compiled, `{vi}` interpreted")

/// Check one program: the front end, the oracle, and (when `compiled`) the C++
/// lane against the interpreter.
let checkProgram (compiled: bool) (outDir: string) (name: string) (source: string) : GenOutcome =
    let r = runFullTest name source outDir compiled
    match r.IRResult with
    | Error _ -> GenRefused (refusalCodes source)
    | Ok ir ->
        // reading A over the program lowered for reading A (a fold keeps the
        // name of the uncomputed value it reads), B over the ordinary lowering
        let irA = withReadingAFrontEnd (fun () -> (runFullTest name source outDir false).IRResult)
        let verdict = judgeProgram irA ir name
        let cv =
            if not compiled then None
            else
                match r.RunResult with
                | Error e when isSkipError e -> Some (NotJudged e)
                // a back-end refusal (an emitted `#error`, BL7xxx) is the C++
                // lane declining the program, not a disagreement
                | Error _ when r.EmittedErrorGuard -> Some (NotJudged "the C++ lane refuses the program (#error guard)")
                | Error e ->
                    // g++ rejecting generated code is BL9002: name the first
                    // error line of the compile, not the run step's summary
                    let why =
                        match r.CompileResult with
                        | Error ce ->
                            ce.Replace("\r\n", "\n").Split('\n')
                            |> Array.tryFind (fun l -> l.Contains "error")
                            |> Option.defaultValue (ce.Split('\n').[0])
                        | Ok _ -> e.Split('\n').[0]
                    Some (CompilerBug ($"BL9002, g++ rejects the generated code: {why.Trim()}"))
                | Ok (exitCode, out) -> Some (compareCompiled exitCode out (interpToday ir name))
        GenAccepted (verdict, cv)

/// The class a shrink must keep: the oracle's DIFFER, or the C++ mismatch.
type Target =
    | KeepDiffer
    | KeepMismatch
    | KeepCompilerBug
    | KeepPrintSet

let private holds (target: Target) (o: GenOutcome) : bool =
    match target, o with
    | KeepDiffer, GenAccepted (Differ _, _) -> true
    | KeepMismatch, GenAccepted (_, Some (Mismatch _)) -> true
    | KeepCompilerBug, GenAccepted (_, Some (CompilerBug _)) -> true
    | KeepPrintSet, GenAccepted (_, Some (PrintSetOnly _)) -> true
    | _ -> false

/// Remove statements one at a time while the target verdict holds, until no
/// single removal keeps it (a 1-minimal statement list). `budget` caps the
/// trial programs (a C++ trial compiles).
let shrink (check: string list list -> GenOutcome) (target: Target) (stmts: string list list) (budget: int) : string list list =
    let mutable current = stmts
    let mutable trials = 0
    let mutable progress = true
    while progress && trials < budget do
        progress <- false
        let mutable i = 0
        while i < current.Length && trials < budget do
            let candidate = current |> List.removeAt i
            trials <- trials + 1
            if holds target (check candidate) then
                current <- candidate
                progress <- true
            else i <- i + 1
    current

// ---------------------------------------------------------------------------
// The block
// ---------------------------------------------------------------------------

/// The FP pins of the interpreter differential (byte identity with the
/// compiled side needs the reproducible build), restored on dispose.
let private pinReproBuild () =
    let pins = [ "BLADE_FP_CONTRACT", "off"; "BLADE_FP_REPRO", "1" ]
    let prior = pins |> List.map (fun (k, _) -> (k, Environment.GetEnvironmentVariable k))
    for (k, v) in pins do Environment.SetEnvironmentVariable(k, v)
    { new IDisposable with
        member _.Dispose() = for (k, v) in prior do Environment.SetEnvironmentVariable(k, v) }

/// One saved finding.
type private Finding =
    { Name: string
      Kind: string
      Detail: string
      Shrunk: string list list
      NoWrites: bool }

let runBorrowGen (seed: int) (count: int) (outDir: string) (compiled: bool) (strict: bool) : BlockResult =
    let lane = if compiled then "C++ lane compared" else "interpreter only"
    printHeader $"Borrow Generator (seed {seed}, {count} programs, {lane})"
    let blockName = "Borrow Generator"
    use _pin = pinReproBuild ()
    Directory.CreateDirectory outDir |> ignore
    let tmpRoot = Path.Combine(Path.GetTempPath(), $"blade_borrow_gen_{Diagnostics.Process.GetCurrentProcess().Id}")
    (try Directory.Delete(tmpRoot, true) with _ -> ())
    Directory.CreateDirectory tmpRoot |> ignore
    CodeGen.deployRuntimeHeaders tmpRoot
    let compiledOk = compiled && capabilities.Value.HasGpp
    if compiled && not compiledOk then printfn "  (no g++: the C++ comparison is skipped)"
    let refusedCodes = Collections.Generic.Dictionary<string, int>()
    let mutable refused = 0
    let mutable agree = 0
    let mutable failureOnly = 0
    let mutable unsupported = 0
    let mutable same = 0
    let mutable notJudged = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let findings = Collections.Generic.List<Finding>()
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [ name ]
        resultLine Fail name detail
    let save (file: string) (text: string) = File.WriteAllText(Path.Combine(outDir, file), text)
    for k in 0 .. count - 1 do
        let name = $"borrow-gen seed {seed} program {k}"
        let stmts = generate seed k
        let source = render name stmts
        let check (c: bool) (st: string list list) = checkProgram c tmpRoot name (render name st)
        match checkProgram compiledOk tmpRoot name source with
        | GenRefused codes ->
            refused <- refused + 1
            for c in codes do refusedCodes.[c] <- (match refusedCodes.TryGetValue c with | true, n -> n + 1 | _ -> 1)
        | GenAccepted (verdict, cv) ->
            (match verdict with
             | Agree -> agree <- agree + 1
             | FailureOnly _ -> failureOnly <- failureOnly + 1
             | Unsupported d ->
                 unsupported <- unsupported + 1
                 // saved with its seed: a reading-A front end that refuses a
                 // program the ordinary one accepts is a finding about the rule
                 save $"unsupported_s{seed}_p{k}.blade" source
                 resultLine Skip name ("UNSUPPORTED: " + d)
             | Differ (b, va, vb, noWrites) ->
                 // shrink through the oracle alone (no g++ per trial)
                 let small = shrink (check false) KeepDiffer stmts 200
                 let file = $"differ_s{seed}_p{k}"
                 save (file + ".blade") source
                 save (file + "_min.blade") (render (name + " (shrunk)") small)
                 let detail = $"DIFFER: `{b}` is `{va}` under A, `{vb}` under B; {small.Length} of {stmts.Length} statements after shrinking"
                 findings.Add({ Name = file; Kind = "DIFFER"; Detail = detail; Shrunk = small; NoWrites = noWrites })
                 if strict || noWrites then fail name ((if noWrites then "NO-WRITES " else "") + detail)
                 else resultLine Skip name ("FINDING " + detail))
            match cv with
            | None -> ()
            | Some Same -> same <- same + 1
            | Some (NotJudged d) -> notJudged <- notJudged + 1; resultLine Skip name ("C++ NOT JUDGED: " + d.Split('\n').[0])
            | Some (PrintSetOnly d) ->
                // counted and saved with its seed; not a failure of the block
                // (a print-rule divergence between the lanes, not a value)
                let small = shrink (check true) KeepPrintSet stmts 40
                let file = $"printset_s{seed}_p{k}"
                save (file + ".blade") source
                save (file + "_min.blade") (render (name + " (shrunk)") small)
                findings.Add({ Name = file; Kind = "PRINT-SET"; Detail = d; Shrunk = small; NoWrites = false })
                resultLine Skip name ($"PRINT-SET: {d}; {small.Length} of {stmts.Length} statements after shrinking")
            | Some (CompilerBug d) ->
                // counted and saved with its seed; not a failure of the block
                // (a known C++-lane bug is listed in the report instead)
                let small = shrink (check true) KeepCompilerBug stmts 40
                let file = $"compilerbug_s{seed}_p{k}"
                save (file + ".blade") source
                save (file + "_min.blade") (render (name + " (shrunk)") small)
                findings.Add({ Name = file; Kind = "COMPILER-BUG"; Detail = d; Shrunk = small; NoWrites = false })
                resultLine Skip name ($"COMPILER-BUG: {d}; {small.Length} of {stmts.Length} statements after shrinking")
            | Some (Mismatch d) ->
                let small = shrink (check true) KeepMismatch stmts 40
                let file = $"mismatch_s{seed}_p{k}"
                save (file + ".blade") source
                save (file + "_min.blade") (render (name + " (shrunk)") small)
                findings.Add({ Name = file; Kind = "MISMATCH"; Detail = d; Shrunk = small; NoWrites = false })
                fail name ($"C++ vs interpreter MISMATCH: {d}; {small.Length} of {stmts.Length} statements after shrinking")
    (try Directory.Delete(tmpRoot, true) with _ -> ())
    let accepted = count - refused
    printfn ""
    printfn "  generated %d: %d refused, %d accepted" count refused accepted
    if refused > 0 then
        let codes = refusedCodes |> Seq.sortByDescending _.Value |> Seq.map (fun kv -> $"{kv.Key} x{kv.Value}") |> String.concat ", "
        printfn "  refused by code: %s" codes
    printfn "  oracle: %d agree, %d differ, %d failure-only, %d unsupported" agree (findings |> Seq.filter (fun f -> f.Kind = "DIFFER") |> Seq.length) failureOnly unsupported
    if compiledOk then
        printfn "  C++ vs interpreter: %d same, %d mismatch, %d compiler-bug (BL9002), %d print-set, %d not judged" same (findings |> Seq.filter (fun f -> f.Kind = "MISMATCH") |> Seq.length) (findings |> Seq.filter (fun f -> f.Kind = "COMPILER-BUG") |> Seq.length) (findings |> Seq.filter (fun f -> f.Kind = "PRINT-SET") |> Seq.length) notJudged
    if findings.Count > 0 then
        printfn "  smallest findings (saved under %s):" outDir
        findings
        |> Seq.sortBy (fun f -> f.Shrunk |> List.sumBy List.length)
        |> Seq.truncate 10
        |> Seq.iter (fun f ->
            printfn "    %s (%s, %d lines): %s" f.Name f.Kind (f.Shrunk |> List.sumBy List.length) f.Detail
            for st in f.Shrunk do
                for l in st do printfn "        %s" l)
    let differs = findings |> Seq.filter (fun f -> f.Kind = "DIFFER") |> Seq.length
    let differFailed = findings |> Seq.filter (fun f -> f.Kind = "DIFFER" && (strict || f.NoWrites)) |> Seq.length
    let compilerBugs = findings |> Seq.filter (fun f -> f.Kind = "COMPILER-BUG" || f.Kind = "PRINT-SET") |> Seq.length
    printFooter blockName
        [ $"{count} generated"; $"{refused} refused"; $"{agree} agree"; $"{differs} differ"
          $"{failureOnly} failure-only"; $"{unsupported} unsupported"; $"{compilerBugs} compiler-bug or print-set"; $"{failed} failed" ]
    { Block = blockName
      Passed = agree + failureOnly
      Failed = failed
      Skipped = refused + unsupported + (differs - differFailed) + compilerBugs
      FailedNames = failedNames }

/// The words after `test borrow-gen`: `--seed N`, `--count K`, `--out <dir>`,
/// `--interp-only`, `--strict`. Returns the process exit code.
let dispatchBorrowGen (args: string list) : int =
    let rec parse (seed, count, out, compiled, strict) rest =
        match rest with
        | [] -> Ok (seed, count, out, compiled, strict)
        | "--seed" :: n :: tail ->
            (match Int32.TryParse n with
             | true, v -> parse (v, count, out, compiled, strict) tail
             | _ -> Error $"--seed takes an integer, not '{n}'")
        | "--count" :: n :: tail ->
            (match Int32.TryParse n with
             | true, v when v > 0 -> parse (seed, v, out, compiled, strict) tail
             | _ -> Error $"--count takes a positive integer, not '{n}'")
        | "--out" :: d :: tail -> parse (seed, count, d, compiled, strict) tail
        | "--interp-only" :: tail -> parse (seed, count, out, false, strict) tail
        | "--strict" :: tail -> parse (seed, count, out, compiled, true) tail
        | w :: _ -> Error $"unrecognized argument '{w}'"
    let defaultOut = Path.Combine(Path.GetTempPath(), "blade_borrow_gen_findings")
    match parse (1, 40, defaultOut, true, false) args with
    | Error e -> eprintfn "test borrow-gen: %s" e; 2
    | Ok (seed, count, out, compiled, strict) ->
        let r = runBorrowGen seed count out compiled strict
        if r.Failed = 0 then 0 else 1
