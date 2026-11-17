// THE PRINT FORM (deep review P3-26; src/PrintForm.fs) -- `blade test print-form`.
//
// The TEST form is what every corpus pin reads, so it needs no block of its
// own beyond one fact: an emission with BLADE_PRINT_FORM unset or `test` is
// byte-identical to what Blade always emitted (no display switch, no unit, the
// `__exprN = ` label). The DISPLAY form is checked here:
//   1. emission facts (no toolchain): the switch, the units, the labels, the
//      sum-type print, the CLI resolution of the default;
//   2. the three lanes run under the display form -- the C++ lane (g++), the
//      LLVM lane (clang; it refuses sum types, so those programs are C++ and
//      the interpreter) and the interpreter -- print the SAME lines, and those
//      lines are the exact expected text below (the shortest round-trip
//      digits over a battery: thirds, 0.1 + 0.2, subnormals, the extremes,
//      powers of ten across the fixed / scientific boundary, Float32's own
//      digits).
// Skips without g++ / clang, never fails for a missing toolchain.
module Blade.Tests.PrintFormTests

open System
open System.Diagnostics
open System.IO
open Blade
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

/// The printed binding lines (the timing line dropped).
let private bindingLines (text: string) : string list =
    text.Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun l -> l <> "" && not (l.Contains " completed in "))
    |> List.ofArray

/// The shortest round-trip battery: every value with its exact DISPLAY text.
let private numsSource =
    String.concat "\n"
        [ "let a: Float64 = 1.0 / 3.0"
          "let b: Float64 = 0.1 + 0.2"
          "let c: Float64 = 1e20"
          "let d: Float64 = 5e-324"
          "let e: Float64 = 1e-5"
          "let f: Float64 = 0.0001"
          "let g: Float64 = 123456789.0"
          "let h: Float64 = 1e16"
          "let i: Float64 = 1e17"
          "let j: Float64 = 100.0"
          "let k: Float64 = -0.0"
          "let l: Float64 = 1.7976931348623157e308"
          "let m: Float64 = 2.2250738585072014e-308"
          "let n: Float64 = 2.0 / 3.0"
          "let o: Float64 = -1.5e-7"
          // 2^-44: its nearest 16-digit decimal does not read back, its other
          // neighbour does (.NET's "R" answers 17 digits here)
          "let p44: Float64 = 5.684341886080802e-14"
          "let p1022: Float64 = 2.2250738585072014e-308 * 2.0"
          "let fa: Float32 = 1.0 / 3.0"
          "let fb: Float32 = 0.1"
          "let fc: Float32 = 16777216.0"
          "let fd: Float32 = 3.4028235e38"
          "let fe: Float32 = 1e-45"
          "let ff: Float32 = 1e-5"
          "let fg: Float32 = -2.5"
          "let arr: Array<Float64 like Idx<3>> = [0.1, 1.0 / 3.0, 1e-7]"
          "let arr2: Array<Float64 like Idx<2>, Idx<2>> = [[1.0, 0.5], [0.25, 1e21]]"
          "let z: Int64 = 42"
          "let w: Bool = true"
          "" ]

let private numsExpected =
    [ "a = 0.3333333333333333"
      "b = 0.30000000000000004"
      "c = 1e+20"
      "d = 5e-324"
      "e = 1e-05"
      "f = 0.0001"
      "g = 123456789.0"
      "h = 10000000000000000.0"
      "i = 1e+17"
      "j = 100.0"
      "k = -0.0"
      "l = 1.7976931348623157e+308"
      "m = 2.2250738585072014e-308"
      "n = 0.6666666666666666"
      "o = -1.5e-07"
      "p44 = 5.684341886080802e-14"
      "p1022 = 4.450147717014403e-308"
      "fa = 0.33333334"
      "fb = 0.1"
      "fc = 16777216.0"
      "fd = 3.4028235e+38"
      "fe = 1e-45"
      "ff = 1e-05"
      "fg = -2.5"
      "arr = [0.1, 0.3333333333333333, 1e-07]"
      "arr2 = [[1.0, 0.5], [0.25, 1e+21]]"
      "z = 42"
      "w = true" ]

/// Units and a bare expression.
let private unitsSource =
    String.concat "\n"
        [ "Unit meters"
          "Unit seconds"
          "let dist: Float64<meters> = 10.0"
          "let t: Float64<seconds> = 4.0"
          "let v = dist / t"
          "let lens: Array<Float64<meters> like Idx<3>> = [1.0, 2.5, 3.0]"
          "let ratio = dist / dist"
          "let plain: Float64 = 1.0 / 3.0"
          "let pair = (dist, t)"
          "plain * 3.0"
          "" ]

let private unitsExpected =
    [ "dist = 10.0 meters"
      "t = 4.0 seconds"
      "v = 2.5 meters / seconds"
      "lens = [1.0, 2.5, 3.0] meters"
      "ratio = 1.0"
      "plain = 0.3333333333333333"
      "pair = (10.0 meters, 4.0 seconds)"
      "1.0" ]

/// Sum types and a struct holding one (the LLVM lane refuses variant types).
let private sumsSource =
    String.concat "\n"
        [ "type Shape = Circle : Float64 | Square : Float64 | Empty"
          "type Color = Red | Green | Blue"
          "struct Tagged { name: Int64, shape: Shape }"
          "let s1 = Circle(1.5)"
          "let s2: Shape = Empty"
          "let c = Green"
          "let tg = Tagged { name = 7, shape = Square(2.0) }"
          "let pair = (Circle(0.25), 3)"
          "type R = EnumIdx<[100, 200, 300]>"
          "type Keyed = Tag : Nat<R> | Untagged"
          "let codes: Array<Nat<R> like Idx<1>> = [300]"
          "let kv = Tag(codes(0))"
          // an array payload is a raw pointer in the C++ variant: no print form,
          // in either form (the binding prints nothing)
          "type Samples = Values : Array<Float64 like Idx<2>> | NoSamples"
          "let smp = Values([1.0, 2.0])"
          "" ]

let private sumsExpected =
    [ "s1 = Circle(1.5)"
      "s2 = Empty"
      "c = Green"
      "tg = Tagged { name = 7, shape = Square(2.0) }"
      "pair = (Circle(0.25), 3)"
      "codes = [300]"
      "kv = Tag(300)" ]

let runPrintFormTests () : BlockResult =
    printHeader "Print Form (the display form for people, the test form for pins)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let failedNames = ResizeArray<string>()
    let check (name: string) (cond: bool) (detail: string) =
        if cond then
            passed <- passed + 1
            resultLine Pass name ""
        else
            failed <- failed + 1
            failedNames.Add name
            resultLine Fail name detail
    let skip (name: string) (why: string) =
        skipped <- skipped + 1
        resultLine Skip name why
    let emitWith (form: string option) (src: string) (label: string) : Result<string, string> =
        use _f = pinEnv PrintForm.envVar (match form with Some f -> f | None -> null)
        match Lowering.lower src with
        | Error e -> Error e
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir label))

    // ---------------- 1. emission ----------------
    (match emitWith None unitsSource "pf_units", emitWith (Some "test") unitsSource "pf_units",
           emitWith (Some "display") unitsSource "pf_units" with
     | Ok unset, Ok test, Ok display ->
         check "emission: unset == test (the in-process default is the test form)" (unset = test) ""
         check "emission: the test form has no display switch, no unit, the __expr label"
             (not (test.Contains "print_display_flag") && not (test.Contains "\" meters\"") && test.Contains "\"__expr1 = \"") ""
         check "emission: the display form switches the float spelling after the timing line"
             (display.Contains "blade_rt::print_display_flag() = true;"
              && display.IndexOf "completed in" < display.IndexOf "print_display_flag() = true") ""
         check "emission: the display form closes a quantity with its unit, an array's after the bracket"
             (display.Contains "<< \" meters\" << endl;" && display.Contains "cout << \"]\" << \" meters\" << endl;"
              && display.Contains "<< \" meters / seconds\" << endl;") ""
         check "emission: a dimensionless quotient prints no unit"
             (display.Contains "cout << \"ratio = \" << blade_rt::pv(ratio) << endl;") ""
         check "emission: a bare expression has no label in the display form"
             (display.Contains "cout << \"\" << blade_rt::pv(__expr1) << endl;" && not (display.Contains "\"__expr1 = \"")) ""
     | a, b, c -> check "emission: the units program lowers in every form" false (sprintf "%A / %A / %A" a b c))
    (match emitWith (Some "test") sumsSource "pf_sums", emitWith (Some "display") sumsSource "pf_sums" with
     | Ok test, Ok display ->
         check "emission: a sum type prints nothing in the test form (its bytes are unchanged)"
             (not (test.Contains "\"s1 = \"") && not (test.Contains "\"Circle(\"")) ""
         check "emission: a sum type prints its constructor and payload in the display form"
             (display.Contains "\"Circle(\"" && display.Contains "\"Empty\"" && display.Contains "{\"Red\", \"Green\", \"Blue\"}"
              && display.Contains "\"Tagged { \"") ""
     | a, b -> check "emission: the sums program lowers" false (sprintf "%A / %A" a b))
    // the CLI resolution: the flag / environment wins, else the default
    // (under an EMPTY toolchain file: the resolution consults the real one
    // after the environment, and a persisted form would answer instead)
    (let saved = Environment.GetEnvironmentVariable PrintForm.envVar
     let savedTc = Environment.GetEnvironmentVariable "BLADE_TOOLCHAIN_FILE"
     let tcFile = Path.Combine(Path.GetTempPath(), $"blade_print_form_{Guid.NewGuid():N}.json")
     try
         File.WriteAllText(tcFile, "{}")
         Environment.SetEnvironmentVariable("BLADE_TOOLCHAIN_FILE", tcFile)
         Toolchain.refresh ()
         Environment.SetEnvironmentVariable(PrintForm.envVar, null)
         let r = PrintForm.applyCliDefault false
         check "cli: unset resolves to the verbs' default (display) and pins it"
             (r = Ok PrintForm.Display && PrintForm.cliDefault = PrintForm.Display
              && Environment.GetEnvironmentVariable PrintForm.envVar = "display"
              && (PrintForm.describe ()).StartsWith "display (the default")
             (sprintf "%A %s" r (PrintForm.describe ()))
         Environment.SetEnvironmentVariable(PrintForm.envVar, "TEST")
         check "cli: BLADE_PRINT_FORM=test wins (any case)" (PrintForm.applyCliDefault false = Ok PrintForm.Test) ""
         Environment.SetEnvironmentVariable(PrintForm.envVar, "pretty")
         check "cli: a value that is no form is refused, by name"
             (match PrintForm.applyCliDefault true with Error e -> e.Contains "'pretty'" | Ok _ -> false) ""
         check "in-process: an unparsable value reads as the test form" (PrintForm.current () = PrintForm.Test) ""
         // a persisted form answers when the environment is silent
         File.WriteAllText(tcFile, "{\"BLADE_PRINT_FORM\": \"test\"}")
         Toolchain.refresh ()
         Environment.SetEnvironmentVariable(PrintForm.envVar, null)
         check "cli: the toolchain file's key answers after the environment"
             (PrintForm.applyCliDefault false = Ok PrintForm.Test && (PrintForm.describe ()).Contains "blade.toolchain.json") (PrintForm.describe ())
     finally
         Environment.SetEnvironmentVariable(PrintForm.envVar, saved)
         Environment.SetEnvironmentVariable("BLADE_TOOLCHAIN_FILE", savedTc)
         (try File.Delete tcFile with _ -> ())
         Toolchain.refresh ())

    // ---------------- 2. the three lanes ----------------
    let caps = Build.capabilities.Value
    let work = Path.Combine("generated_cpp_tests", "print_form")
    Directory.CreateDirectory work |> ignore
    // the runtime headers beside the generated .cpp, the shim beside the .ll
    CodeGen.deployRuntimeHeaders work
    EmitLlvm.deployShim work
    let lanes (stem: string) (src: string) (llvmExpected: bool) =
        use _f = pinEnv PrintForm.envVar "display"
        use _b = pinEnv "BLADE_PRINT_BITS" "1"   // a test-form instrument: inert here
        let lowerIt () =
            match Lowering.lower src with
            | Ok ir -> Ok ir
            | Error e -> Error ("lower: " + e)
        let cpp =
            if not caps.HasGpp then None
            else
                Some (match lowerIt () with
                      | Error e -> Error e
                      | Ok ir ->
                          let code = fst (CodeGen.genSelfContainedProgramFromIR ir (stem + "_c"))
                          let file = Path.Combine(work, stem + "_c.cpp")
                          File.WriteAllText(file, code)
                          match Build.compileCpp file work with
                          | Error e -> Error ("compile: " + e)
                          | Ok exe ->
                              match runProc exe 120000 with
                              | Ok (0, out, _) -> Ok (bindingLines out)
                              | Ok (code, out, err) -> Error $"exit {code}: {out} {err}"
                              | Error e -> Error e)
        let llvm =
            if not llvmExpected then None
            else
                match Build.resolveClang () with
                | None -> None
                | Some _ ->
                    Some (match lowerIt () with
                          | Error e -> Error e
                          | Ok ir ->
                              match (try EmitLlvm.tryEmitProgramNamed (stem + "_l") ir with ex -> Error ("raised: " + ex.Message)) with
                              | Error why -> Error ("the llvm lane refused: " + why)
                              | Ok ll ->
                                  let llFile = Path.Combine(work, stem + "_l.ll")
                                  File.WriteAllText(llFile, ll)
                                  match Build.compileLlvmProgram llFile work with
                                  | Error e -> Error ("llvm compile: " + e)
                                  | Ok exe ->
                                      match runProc exe 120000 with
                                      | Ok (0, out, _) -> Ok (bindingLines out)
                                      | Ok (code, out, err) -> Error $"exit {code}: {out} {err}"
                                      | Error e -> Error e)
        let interp =
            match lowerIt () with
            | Error e -> Error e
            | Ok ir ->
                let res = Blade.Interp.Run.runProgram ir stem Blade.Interp.Value.defaultLimits
                if res.ExitCode = Blade.Interp.Run.ExitOk then Ok (bindingLines res.Stdout)
                else Error $"{res.ExitCode}: {res.Stderr}"
        (cpp, llvm, interp)
    let judge (what: string) (stem: string) (src: string) (expected: string list) (llvmExpected: bool) =
        let (cpp, llvm, interp) = lanes stem src llvmExpected
        let show (r: Result<string list, string>) =
            match r with
            | Ok ls -> String.concat " | " ls
            | Error e -> "ERROR " + e
        (match interp with
         | Ok ls -> check $"{what}: interpreter prints the expected display text" (ls = expected) (show interp)
         | Error _ -> check $"{what}: interpreter prints the expected display text" false (show interp))
        (match cpp with
         | None -> skip $"{what}: C++ lane prints the expected display text" "g++ not found"
         | Some r -> check $"{what}: C++ lane prints the expected display text" (r = Ok expected) (show r))
        (match llvm with
         | None when llvmExpected -> skip $"{what}: LLVM lane prints the expected display text" "no clang (BLADE_LLVM_CLANG)"
         | None -> ()
         | Some r -> check $"{what}: LLVM lane prints the expected display text" (r = Ok expected) (show r))
    judge "numbers (shortest round-trip, both widths)" "pf_nums" numsSource numsExpected true
    judge "units and a bare expression" "pf_units" unitsSource unitsExpected true
    judge "sum types and a struct holding one" "pf_sums" sumsSource sumsExpected false
    // The test form is untouched by the display machinery: the same battery
    // under `test` is today's spelling in the interpreter (the corpus pins
    // hold the compiled lanes to it).
    (use _f = pinEnv PrintForm.envVar "test"
     match Lowering.lower numsSource with
     | Error e -> check "test form: the battery lowers" false e
     | Ok ir ->
         let res = Blade.Interp.Run.runProgram ir "pf_nums_test" Blade.Interp.Value.defaultLimits
         let ls = bindingLines res.Stdout
         check "test form: 15 / 6 significant digits, as always"
             (List.contains "a = 0.333333333333333" ls && List.contains "fa = 0.333333" ls && List.contains "b = 0.3" ls)
             (String.concat " | " ls))
    { Block = "Print Form"; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = List.ofSeq failedNames }
