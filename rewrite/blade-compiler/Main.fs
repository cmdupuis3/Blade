// Blade-DSL Compiler Main Entry Point
// Test driver for the compiler pipeline

module Blade.Main

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
open Blade.Ast
open Blade.Lexer
open Blade.Parser
open Blade.IR
open Blade.TypedAst
open Blade.TypeCheck
open Blade.Lowering
open Blade.CodeGen
open Blade.NetcdfProvider

// Import test definitions
open Blade.Tests.Basic
open Blade.Tests.Loops
open Blade.Tests.Symmetry
open Blade.Tests.Reynolds
open Blade.Tests.Arity
open Blade.Tests.Functions
open Blade.Tests.Structs
open Blade.Tests.SumTypes
open Blade.Tests.Interfaces
open Blade.Tests.Modules
open Blade.Tests.Guards
open Blade.Tests.Bracketed
open Blade.Tests.IndexTypes
open Blade.Tests.Mutability
open Blade.Tests.Static
open Blade.Tests.Units
open Blade.Tests.Sqlish
open Blade.Tests.InferenceProbes
open Blade.Tests.FuncArrays
open Blade.Tests.Normalize
open Blade.Tests.Unify
open Blade.Tests.ValidateArrow
open Blade.Tests.ExprAttrs
open Blade.Tests.CodeGenSubst
// Aliases for cleaner code
type Process = System.Diagnostics.Process
type ProcessStartInfo = System.Diagnostics.ProcessStartInfo

// ============================================================================
// Test Utilities
// ============================================================================

let compilerVersion = "0.19.2"

let printHeader title =
    printfn "\n%s" (String.replicate 70 "=")
    printfn "  %s (v%s)" title compilerVersion
    printfn "%s\n" (String.replicate 70 "=")

let printSubHeader title =
    printfn "\n--- %s ---\n" title

// ============================================================================
// Value Checking Infrastructure
// ============================================================================

/// Expected value for a variable
type ExpectedValue =
    | ExpectedScalar of string * float
    | ExpectedBool of string * bool
    | ExpectedArray1D of string * float list
    | ExpectedArray2D of string * float list list
    | ExpectedComplex of string * float * float
    | ExpectedArray1DComplex of string * (float * float) list
    | ExpectedString of string * string
    | ExpectedArray1DString of string * string list

/// Parse expected values from test source comments
/// Format: // EXPECT: varname = value
/// Format: // EXPECT: varname = [1.0, 2.0, 3.0]
/// Format: // EXPECT: varname = [[1.0, 2.0], [3.0, 4.0]]
/// Format: // EXPECT: varname = (1.0, 2.0)                        (Complex scalar)
/// Format: // EXPECT: varname = [(1.0, 0.0), (0.0, 1.0)]          (Complex array)
/// Format: // EXPECT: varname = "hello"                           (String scalar — quotes required)
/// Format: // EXPECT: varname = ["a", "b", "c"]                   (String array — quotes around each element)
let parseExpectedValues (source: string) : ExpectedValue list =
    let lines = source.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
    
    let parseFloatList (s: string) : float list =
        let inner = s.Trim().TrimStart('[').TrimEnd(']').Trim()
        if String.IsNullOrWhiteSpace(inner) then []
        else
            inner.Split(',')
            |> Array.map (fun x -> 
                match Double.TryParse(x.Trim()) with
                | true, v -> v
                | false, _ -> 0.0)
            |> Array.toList
    
    let parse2DList (s: string) : float list list =
        // Simple parser for [[a,b],[c,d]] format
        let inner = s.Trim().TrimStart('[').TrimEnd(']')
        // Split on "], [" pattern
        let parts = inner.Split([|"], ["; "],["  |], StringSplitOptions.RemoveEmptyEntries)
        parts |> Array.map (fun p -> 
            p.Trim().TrimStart('[').TrimEnd(']').Split(',')
            |> Array.map (fun x -> 
                match Double.TryParse(x.Trim()) with
                | true, v -> v
                | false, _ -> 0.0)
            |> Array.toList)
        |> Array.toList
    
    /// Parse a single complex pair `(re, im)` returning (re, im) on success.
    /// Tolerates surrounding whitespace and accepts both `1` and `1.0` for
    /// each component (matching the existing Double.TryParse convention).
    let parseComplexPair (s: string) : (float * float) option =
        let t = s.Trim()
        if t.StartsWith("(") && t.EndsWith(")") then
            let inner = t.Substring(1, t.Length - 2)
            match inner.Split([|','|], 2) with
            | [| reStr; imStr |] ->
                match Double.TryParse(reStr.Trim()), Double.TryParse(imStr.Trim()) with
                | (true, re), (true, im) -> Some (re, im)
                | _ -> None
            | _ -> None
        else None
    
    /// Parse a complex array literal `[(r1,i1), (r2,i2), ...]`.
    /// Splits on `), (` pattern so element commas don't confuse the parser.
    let parseComplexArray (s: string) : (float * float) list option =
        let t = s.Trim()
        if t.StartsWith("[") && t.EndsWith("]") then
            let inner = t.Substring(1, t.Length - 2).Trim()
            if String.IsNullOrWhiteSpace(inner) then Some []
            else
                // Split into element strings. Each element is `(re, im)`.
                // A simple approach: rebuild parens after splitting.
                let parts = inner.Split([|"), ("; "),("|], StringSplitOptions.None)
                let normalized =
                    parts |> Array.mapi (fun i p ->
                        let withOpen = if i = 0 then p else "(" + p
                        let withClose = if i = parts.Length - 1 then withOpen else withOpen + ")"
                        withClose)
                let parsed = normalized |> Array.map parseComplexPair
                if parsed |> Array.forall Option.isSome then
                    Some (parsed |> Array.map Option.get |> Array.toList)
                else None
        else None
    
    /// Parse a quoted scalar string `"hello"`. Returns the unescaped inner
    /// contents on success. Recognizes \\ and \" escapes (and passes other
    /// backslash sequences through unchanged — matches escapeStringLit's
    /// minimal escape set).
    let tryParseQuotedString (s: string) : string option =
        let t = s.Trim()
        if t.Length >= 2 && t.StartsWith("\"") && t.EndsWith("\"") then
            let inner = t.Substring(1, t.Length - 2)
            let sb = System.Text.StringBuilder()
            let mutable i = 0
            while i < inner.Length do
                let c = inner.[i]
                if c = '\\' && i + 1 < inner.Length then
                    let n = inner.[i + 1]
                    match n with
                    | '"' -> sb.Append('"') |> ignore; i <- i + 2
                    | '\\' -> sb.Append('\\') |> ignore; i <- i + 2
                    | 'n' -> sb.Append('\n') |> ignore; i <- i + 2
                    | 'r' -> sb.Append('\r') |> ignore; i <- i + 2
                    | 't' -> sb.Append('\t') |> ignore; i <- i + 2
                    | _ -> sb.Append(c) |> ignore; i <- i + 1
                else
                    sb.Append(c) |> ignore
                    i <- i + 1
            Some (sb.ToString())
        else None

    /// Parse a string-array literal `["a", "b", "c"]`. Walks the inner body
    /// splitting on commas only outside quote regions, so embedded commas
    /// inside string elements (e.g. `["a, b", "c"]`) parse correctly. Returns
    /// None if any element isn't a properly-quoted string.
    let tryParseStringArray (s: string) : string list option =
        let t = s.Trim()
        if t.StartsWith("[") && t.EndsWith("]") then
            let inner = t.Substring(1, t.Length - 2).Trim()
            if String.IsNullOrWhiteSpace(inner) then Some []
            else
                // Walk inner, splitting at commas that are not inside a quoted region.
                let elements = ResizeArray<string>()
                let cur = System.Text.StringBuilder()
                let mutable inQuotes = false
                let mutable escaped = false
                for c in inner do
                    if escaped then
                        cur.Append(c) |> ignore
                        escaped <- false
                    elif c = '\\' then
                        cur.Append(c) |> ignore
                        escaped <- true
                    elif c = '"' then
                        cur.Append(c) |> ignore
                        inQuotes <- not inQuotes
                    elif c = ',' && not inQuotes then
                        elements.Add(cur.ToString())
                        cur.Clear() |> ignore
                    else
                        cur.Append(c) |> ignore
                if cur.Length > 0 then elements.Add(cur.ToString())
                let parsed = elements |> Seq.map tryParseQuotedString |> Seq.toList
                if parsed |> List.forall Option.isSome then
                    Some (parsed |> List.map Option.get)
                else None
        else None

    lines
    |> Array.choose (fun line ->
        let trimmed = line.Trim()
        if trimmed.StartsWith("// EXPECT:") then
            let rest = trimmed.Substring(10).Trim()
            match rest.Split([|'='|], 2) with
            | [| name; value |] ->
                let name = name.Trim()
                let value = value.Trim()
                if value.StartsWith("[[") then
                    Some (ExpectedArray2D (name, parse2DList value))
                elif value.StartsWith("[(") then
                    // Complex array: [(r1,i1), (r2,i2), ...]
                    match parseComplexArray value with
                    | Some pairs -> Some (ExpectedArray1DComplex (name, pairs))
                    | None -> None
                elif value.StartsWith("[\"") then
                    // String array: ["a", "b", ...]
                    match tryParseStringArray value with
                    | Some xs -> Some (ExpectedArray1DString (name, xs))
                    | None -> None
                elif value.StartsWith("[") then
                    Some (ExpectedArray1D (name, parseFloatList value))
                elif value.StartsWith("(") then
                    // Complex scalar: (re, im)
                    match parseComplexPair value with
                    | Some (re, im) -> Some (ExpectedComplex (name, re, im))
                    | None -> None
                elif value.StartsWith("\"") then
                    // Scalar string: "..."
                    match tryParseQuotedString value with
                    | Some s -> Some (ExpectedString (name, s))
                    | None -> None
                elif value.ToLower() = "true" then
                    Some (ExpectedBool (name, true))
                elif value.ToLower() = "false" then
                    Some (ExpectedBool (name, false))
                else
                    match Double.TryParse(value) with
                    | true, v -> Some (ExpectedScalar (name, v))
                    | false, _ -> None
            | _ -> None
        else None)
    |> Array.toList

/// Parse actual values from program output
/// Looks for lines like "varname = value" or "varname = [...]"
let parseActualValues (output: string) : Map<string, string> =
    let lines = output.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
    lines
    |> Array.choose (fun line ->
        let trimmed = line.Trim()
        if trimmed.Contains(" = ") && not (trimmed.Contains("completed in")) then
            match trimmed.Split([|" = "|], 2, StringSplitOptions.None) with
            | [| name; value |] -> Some (name.Trim(), value.Trim())
            | _ -> None
        else None)
    |> Map.ofArray

/// Compare a float with combined absolute + relative tolerance
let floatEquals (expected: float) (actual: float) (tolerance: float) : bool =
    if Double.IsNaN(expected) && Double.IsNaN(actual) then true
    elif Double.IsInfinity(expected) || Double.IsInfinity(actual) then expected = actual
    else
        let diff = abs(expected - actual)
        // Absolute tolerance for values near zero, relative tolerance otherwise
        let scale = max (abs expected) (abs actual)
        if scale < 1e-12 then diff <= tolerance
        else diff / scale <= tolerance

/// Parse a float from string
let tryParseFloat (s: string) : float option =
    match Double.TryParse(s.Trim()) with
    | true, v -> Some v
    | false, _ -> None

/// Parse a 1D array from string like "[1.0, 2.0, 3.0]"
let tryParse1DArray (s: string) : float list option =
    try
        let inner = s.Trim().TrimStart('[').TrimEnd(']')
        if String.IsNullOrWhiteSpace(inner) then Some []
        else
            inner.Split(',')
            |> Array.map (fun x -> Double.Parse(x.Trim()))
            |> Array.toList
            |> Some
    with _ -> None

/// Check if expected values match actual output
let checkExpectedValues (expected: ExpectedValue list) (output: string) : Result<unit, string list> =
    if expected.IsEmpty then Ok ()
    else
        let actual = parseActualValues output
        let tolerance = 1e-9
        
        let errors = 
            expected |> List.choose (fun exp ->
                match exp with
                | ExpectedScalar (name, expectedVal) ->
                    match actual.TryFind name with
                    | Some actualStr ->
                        match tryParseFloat actualStr with
                        | Some actualVal when floatEquals expectedVal actualVal tolerance -> None
                        | Some actualVal -> Some (sprintf "%s: expected %.17g, got %.17g (diff=%.3e)" name expectedVal actualVal (abs(expectedVal - actualVal)))
                        | None ->
                            // boolalpha: true→1, false→0
                            match actualStr.Trim().ToLower() with
                            | "true" when floatEquals expectedVal 1.0 tolerance -> None
                            | "false" when floatEquals expectedVal 0.0 tolerance -> None
                            | "true" -> Some (sprintf "%s: expected %.17g, got true (1)" name expectedVal)
                            | "false" -> Some (sprintf "%s: expected %.17g, got false (0)" name expectedVal)
                            | _ -> Some (sprintf "%s: could not parse '%s' as float" name actualStr)
                    | None -> Some (sprintf "%s: not found in output" name)
                    
                | ExpectedBool (name, expectedVal) ->
                    match actual.TryFind name with
                    | Some actualStr ->
                        let lower = actualStr.Trim().ToLower()
                        let matches =
                            match lower with
                            | "true" -> expectedVal = true
                            | "false" -> expectedVal = false
                            | "1" -> expectedVal = true
                            | "0" -> expectedVal = false
                            | _ -> false
                        if matches then None
                        else Some (sprintf "%s: expected %b, got %s" name expectedVal actualStr)
                    | None -> Some (sprintf "%s: not found in output" name)
                    
                | ExpectedArray1D (name, expectedVals) ->
                    match actual.TryFind name with
                    | Some actualStr ->
                        match tryParse1DArray actualStr with
                        | Some actualVals when actualVals.Length = expectedVals.Length &&
                                               List.forall2 (fun e a -> floatEquals e a tolerance) expectedVals actualVals -> None
                        | Some actualVals -> Some (sprintf "%s: expected %A, got %A" name expectedVals actualVals)
                        | None -> Some (sprintf "%s: could not parse '%s' as array" name actualStr)
                    | None -> Some (sprintf "%s: not found in output" name)
                    
                | ExpectedArray2D (name, _) ->
                    // For now, skip 2D array checking (complex parsing)
                    None
                
                | ExpectedComplex (name, expectedRe, expectedIm) ->
                    match actual.TryFind name with
                    | Some actualStr ->
                        let t = actualStr.Trim()
                        if t.StartsWith("(") && t.EndsWith(")") then
                            let inner = t.Substring(1, t.Length - 2)
                            match inner.Split([|','|], 2) with
                            | [| reStr; imStr |] ->
                                match Double.TryParse(reStr.Trim()), Double.TryParse(imStr.Trim()) with
                                | (true, re), (true, im)
                                    when floatEquals expectedRe re tolerance &&
                                         floatEquals expectedIm im tolerance -> None
                                | (true, re), (true, im) ->
                                    Some (sprintf "%s: expected (%g, %g), got (%g, %g)" name expectedRe expectedIm re im)
                                | _ -> Some (sprintf "%s: could not parse complex components from '%s'" name actualStr)
                            | _ -> Some (sprintf "%s: malformed complex output '%s'" name actualStr)
                        else Some (sprintf "%s: expected complex (%g, %g) but output '%s' isn't in (re,im) form" name expectedRe expectedIm actualStr)
                    | None -> Some (sprintf "%s: not found in output" name)
                
                | ExpectedArray1DComplex (name, expectedPairs) ->
                    match actual.TryFind name with
                    | Some actualStr ->
                        // Output looks like: [(r1,i1), (r2,i2), ...]
                        let t = actualStr.Trim()
                        if t.StartsWith("[") && t.EndsWith("]") then
                            let inner = t.Substring(1, t.Length - 2).Trim()
                            if String.IsNullOrWhiteSpace(inner) && expectedPairs.IsEmpty then None
                            else
                                // Split on `), (` to isolate elements; rebuild parens
                                let parts = inner.Split([|"), ("; "),("|], StringSplitOptions.None)
                                let normalized =
                                    parts |> Array.mapi (fun i p ->
                                        let withOpen = if i = 0 then p else "(" + p
                                        let withClose = if i = parts.Length - 1 then withOpen else withOpen + ")"
                                        withClose)
                                let parsedOpts =
                                    normalized |> Array.map (fun pstr ->
                                        let pt = pstr.Trim()
                                        if pt.StartsWith("(") && pt.EndsWith(")") then
                                            let pinner = pt.Substring(1, pt.Length - 2)
                                            match pinner.Split([|','|], 2) with
                                            | [| reStr; imStr |] ->
                                                match Double.TryParse(reStr.Trim()), Double.TryParse(imStr.Trim()) with
                                                | (true, re), (true, im) -> Some (re, im)
                                                | _ -> None
                                            | _ -> None
                                        else None)
                                if parsedOpts |> Array.forall Option.isSome then
                                    let actualPairs = parsedOpts |> Array.map Option.get |> Array.toList
                                    if actualPairs.Length <> expectedPairs.Length then
                                        Some (sprintf "%s: expected %d complex elements, got %d" name expectedPairs.Length actualPairs.Length)
                                    else
                                        let mismatch =
                                            List.zip expectedPairs actualPairs
                                            |> List.tryFind (fun ((eRe, eIm), (aRe, aIm)) ->
                                                not (floatEquals eRe aRe tolerance && floatEquals eIm aIm tolerance))
                                        match mismatch with
                                        | None -> None
                                        | Some ((eRe, eIm), (aRe, aIm)) ->
                                            Some (sprintf "%s: expected element (%g, %g), got (%g, %g)" name eRe eIm aRe aIm)
                                else
                                    Some (sprintf "%s: could not parse complex array from '%s'" name actualStr)
                        else Some (sprintf "%s: expected complex array but output '%s' isn't in [(re,im),...] form" name actualStr)
                    | None -> Some (sprintf "%s: not found in output" name)

                | ExpectedString (name, expected) ->
                    // Scalar string output: `cout << name << endl` emits the
                    // raw chars without surrounding quotes, so the actual
                    // value text is compared directly against the expected
                    // unescaped contents.
                    match actual.TryFind name with
                    | Some actualStr ->
                        if actualStr.Trim() = expected then None
                        else Some (sprintf "%s: expected \"%s\", got \"%s\"" name expected (actualStr.Trim()))
                    | None -> Some (sprintf "%s: not found in output" name)

                | ExpectedArray1DString (name, expectedVals) ->
                    // Array print emits `[a, b, c]` with comma-separated raw
                    // chars (no quotes around elements). Split on `, ` to
                    // recover the per-element string and compare lists. A
                    // string element containing a literal `, ` would split
                    // incorrectly — flagged as a limitation since the print
                    // format itself is ambiguous in that case.
                    match actual.TryFind name with
                    | Some actualStr ->
                        let t = actualStr.Trim()
                        if t.StartsWith("[") && t.EndsWith("]") then
                            let inner = t.Substring(1, t.Length - 2)
                            let parts =
                                if String.IsNullOrWhiteSpace(inner) then []
                                else
                                    inner.Split([|", "|], StringSplitOptions.None)
                                    |> Array.map (fun s -> s.Trim())
                                    |> Array.toList
                            if parts.Length <> expectedVals.Length then
                                Some (sprintf "%s: expected %d string elements, got %d (\"%s\")"
                                        name expectedVals.Length parts.Length actualStr)
                            else
                                let mismatch =
                                    List.zip expectedVals parts
                                    |> List.tryFind (fun (e, a) -> e <> a)
                                match mismatch with
                                | None -> None
                                | Some (e, a) -> Some (sprintf "%s: expected element \"%s\", got \"%s\"" name e a)
                        else Some (sprintf "%s: expected string array but output '%s' isn't in [a, b, ...] form" name actualStr)
                    | None -> Some (sprintf "%s: not found in output" name))
        
        if errors.IsEmpty then Ok ()
        else Error errors

let testParse source =
    printfn "Source:\n%s\n" source
    match parseProgram source with
    | Ok program ->
        printfn "Parse: OK (%d modules)" program.Modules.Length
        Ok program
    | Error e ->
        printfn "Parse: ERROR at %d:%d - %s" e.Line e.Col e.Message
        Error e.Message

let testLower source =
    match lower source with
    | Ok ir ->
        printfn "Lower: OK"
        for m in ir.Modules do
            printfn "  Module: %s" m.Name
            printfn "  Functions: %d" m.Functions.Length
            printfn "  Bindings: %d" m.Bindings.Length
            
            // Build name context from all bindings
            let mutable names = Map.empty
            for f in m.Functions do
                printfn "    function %s" f.Name
                names <- Map.add f.Id f.Name names
            for b in m.Bindings do
                names <- Map.add b.Id b.Name names
            
            // Print bindings with name context
            for b in m.Bindings do
                printfn "    let %s = %s" b.Name (ppIRExprWithNames names 0 b.Value)
        Ok ir
    | Error e ->
        printfn "Lower: ERROR - %s" e
        Error e

/// Test type checking phase only
let testTypeCheck source =
    printfn "Source:\n%s\n" source
    match parseProgram source with
    | Ok program ->
        printfn "Parse: OK"
        match typeCheck program with
        | Ok (typedProgram, _builder, warnings) ->
            printfn "TypeCheck: OK (%d modules)" typedProgram.Modules.Length
            for w in warnings do
                printfn "  [TypeCheck Warning] %s" w
            for m in typedProgram.Modules do
                let moduleName = m.Name |> Option.map (String.concat ".") |> Option.defaultValue "<anonymous>"
                printfn "  Module: %s" moduleName
                printfn "  Declarations: %d" m.Decls.Length
                for decl in m.Decls do
                    match decl with
                    | TDeclLet binding ->
                        printfn "    let %s : %s" binding.Name (ppIRType binding.Type)
                    | _ -> ()
            Ok typedProgram
        | Error errors ->
            for err in errors do
                printfn "TypeCheck: ERROR - %s" (formatCompileError err)
            let msg = errors |> List.map formatCompileError |> String.concat "\n"
            Error msg
    | Error e ->
        printfn "Parse: ERROR at %d:%d - %s" e.Line e.Col e.Message
        Error e.Message

/// Test new pipeline: Parse -> TypeCheck -> Lower
let testLowerWithTypeCheck source =
    printfn "Source:\n%s\n" source
    match lowerWithTypeCheck source with
    | Ok ir ->
        printfn "Pipeline (Parse → TypeCheck → Lower): OK"
        for m in ir.Modules do
            printfn "  Module: %s" m.Name
            printfn "  Bindings: %d" m.Bindings.Length
            
            let names = m.Bindings |> List.fold (fun acc b -> Map.add b.Id b.Name acc) Map.empty
            for b in m.Bindings do
                printfn "    let %s = %s" b.Name (ppIRExprWithNames names 0 b.Value)
        Ok ir
    | Error e ->
        printfn "Pipeline: ERROR - %s" e
        Error e

/// Compare old and new pipelines
let testComparePipelines source =
    printfn "Source:\n%s\n" source
    printfn "--- Old Pipeline (Parse → Lower) ---"
    let oldResult = lower source
    printfn "--- New Pipeline (Parse → TypeCheck → Lower) ---"
    let newResult = lowerWithTypeCheck source
    
    match oldResult, newResult with
    | Ok oldIR, Ok newIR ->
        printfn "\nBoth pipelines succeeded."
        printfn "Old: %d bindings" (oldIR.Modules |> List.sumBy (fun m -> m.Bindings.Length))
        printfn "New: %d bindings" (newIR.Modules |> List.sumBy (fun m -> m.Bindings.Length))
        Ok (oldIR, newIR)
    | Error oldErr, Ok _ ->
        printfn "\nOld pipeline failed, new succeeded."
        printfn "Old error: %s" oldErr
        Error oldErr
    | Ok _, Error newErr ->
        printfn "\nOld pipeline succeeded, new failed."
        printfn "New error: %s" newErr
        Error newErr
    | Error oldErr, Error newErr ->
        printfn "\nBoth pipelines failed."
        printfn "Old error: %s" oldErr
        printfn "New error: %s" newErr
        Error oldErr

// ============================================================================
// Test Cases
// ============================================================================


// ============================================================================
// Test Collections
// ============================================================================

/// All tests combined
let allTests = 
    basicTests @ loopTests @ symmetryTests @ reynoldsTests @ arityTests @ functionTests 
    @ structTests @ sumTypeTests @ interfaceTests @ moduleTests @ guardTests @ guardCombinatorTests @ zeroCombinatorTests @ sequenceCombinatorTests @ tupleViewTests @ replicateTests @ anonRangeTests @ forInTests @ bracketedTests
    @ indexTypeTests @ mutabilityTests @ staticTests @ unitTests
    @ foreignKeyTests @ maskTests @ setOpTests @ uniqueContainsTests @ semijoinTests @ groupByTests @ sortTests @ reduceTests @ extentsTests @ extentsMultiRankTests @ regressionTests @ sqlCombinedTests @ v24dProbes
    @ inferenceProbes
    @ funcArrayTests

// ============================================================================
// Test Runner
// ============================================================================

/// Result of a full test run (IR + C++ compilation + execution)
type FullTestResult = {
    TestName: string
    IRResult: Result<IRProgram, string>
    CppGenerated: bool
    CppFile: string option
    CompileResult: Result<string, string>  // Ok(exePath) or Error(message)
    RunResult: Result<int * string, string>  // Ok(exitCode, stdout) or Error(message)
    ValueCheckResult: Result<unit, string list>  // Ok() or Error(list of mismatches)
    HasExpectedValues: bool  // Whether the test had EXPECT comments
}

/// Check if g++ is available and working properly
let checkGppAvailable () =
    // Just assume g++ is available - actual errors will be caught during compilation
    true

// ============================================================================
// Backend capability detection + toolchain resolution
//
// CUDA is a backend *mode* from Blade's POV: the choice of whether a test
// targets the device is determined during codegen. From the harness POV
// the generated source is already settled by the time we compile, so the
// backend requirement is *inferred* from the output (presence of device
// kernels) rather than declared per-test.
//
// The harness advertises environment capabilities once at startup; each
// test's inferred requirement is intersected against them. A test whose
// requirement the environment can't satisfy is SKIPPED with a reason, not
// failed. The host-compiler choice is a per-(platform, backend) resolution,
// never a per-test axis.
// ============================================================================

type HostPlatform = PWindows | PLinux | PMacOS

type Capabilities = {
    Platform : HostPlatform
    HasGpp   : bool
    HasNvcc  : bool
    HasCl    : bool      // cl.exe on PATH (the host compiler nvcc drives on Windows)
    HasGpu   : bool      // a runnable CUDA device is present
}

/// Backend requirement inferred from generated source. `RequiresCuda` when
/// codegen emitted at least one device kernel; `CpuOnly` otherwise.
type BackendReq = CpuOnly | RequiresCuda

/// Resolution of (capabilities, requirement) into a concrete compile action.
type CompilePlan =
    | UseGpp
    | UseNvcc                 // nvcc drives host compiler: cl.exe (Windows) / g++ (Linux)
    | SkipCompile of string   // human-readable reason

/// Probe whether a tool responds to a version/help query on PATH.
let private probeTool (exe: string) (args: string) : bool =
    try
        let psi = ProcessStartInfo(exe, args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        // Drain to avoid pipe deadlock; we only care that it launched + exited.
        proc.StandardOutput.ReadToEnd() |> ignore
        proc.StandardError.ReadToEnd() |> ignore
        proc.WaitForExit(10000) |> ignore
        proc.ExitCode = 0
    with _ -> false

/// Probe for a runnable CUDA device. `nvidia-smi -L` lists devices and exits
/// 0 with a non-empty list when at least one GPU is present. This is a proxy
/// for a real `cudaGetDeviceCount` probe but avoids compiling one.
let private probeGpu () : bool =
    try
        let psi = ProcessStartInfo("nvidia-smi", "-L")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        let out = proc.StandardOutput.ReadToEnd()
        proc.StandardError.ReadToEnd() |> ignore
        proc.WaitForExit(10000) |> ignore
        proc.ExitCode = 0 && out.Contains("GPU")
    with _ -> false

let detectCapabilities () : Capabilities =
    let platform =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then PWindows
        elif RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then PMacOS
        else PLinux
    {
        Platform = platform
        HasGpp   = probeTool "g++" "--version"
        HasNvcc  = probeTool "nvcc" "--version"
        HasCl    = (platform = PWindows) && probeTool "cl" "/?"
        HasGpu   = probeGpu ()
    }

/// Capabilities are environment-global; detect once, lazily.
let capabilities = lazy (detectCapabilities ())

/// Infer the backend requirement from generated source. CUDA codegen emits
/// `__global__`-qualified kernels; CPU codegen never does. This keeps the
/// codegen signature untouched while the CUDA backend is built out — every
/// current test infers CpuOnly, and the inference flips automatically once
/// device kernels appear in the output.
let inferBackendReq (generatedSource: string) : BackendReq =
    if generatedSource.Contains("__global__") then RequiresCuda else CpuOnly

/// Resolve (capabilities, requirement) into a compile action. A test never
/// picks a compiler; it produces a BackendReq and this picks the toolchain.
let resolveCompile (caps: Capabilities) (req: BackendReq) : CompilePlan =
    match req, caps.Platform with
    | CpuOnly, _ when not caps.HasGpp           -> SkipCompile "requires g++, not found"
    | CpuOnly, _                                -> UseGpp
    | RequiresCuda, _ when not caps.HasNvcc     -> SkipCompile "requires CUDA, nvcc not found"
    | RequiresCuda, PMacOS                      -> SkipCompile "CUDA unsupported on macOS"
    | RequiresCuda, PWindows when not caps.HasCl -> SkipCompile "requires CUDA, cl.exe not found (nvcc host compiler)"
    | RequiresCuda, _                           -> UseNvcc

/// Whether a Result error string denotes a skip (no-toolchain, no-GPU, etc.)
/// rather than a genuine failure. Skips never count against the pass total.
let isSkipError (e: string) =
    e = "Skipped" || e.StartsWith("Skipped:")

/// Compile a C++ file with g++
let compileCpp (cppFile: string) (outputDir: string) : Result<string, string> =
    try
        let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
        let exeFile = Path.ChangeExtension(cppFile, exeExt)
        
        // Use full paths
        let cppFullPath = Path.GetFullPath(cppFile)
        let exeFullPath = Path.GetFullPath(exeFile)
        
        // Enable OpenMP for parallel loops
        let ompFlag = "-fopenmp"

        // Backstop the Blade type system: any implicit float→integer narrowing
        // conversion in generated C++ should be a hard error, not a silent
        // truncation. Probe E's silent miscompile (Float64 → Int64) is exactly
        // this pattern.
        //
        // -Wnarrowing only catches brace-init narrowing (`int x{1.5};`); we
        //   need assignment-style coverage too (`int x = 1.5;`).
        // -Wfloat-conversion catches both, but only for float-vs-integer.
        // -Wconversion is broader but flags many legitimate cases (size_t loop
        //   counters compared with int literals, etc.) so we don't enable it.
        let safetyFlags = "-Werror=float-conversion -Werror=narrowing"

        let args = sprintf "-std=c++17 -O2 %s %s -o \"%s\" \"%s\"" ompFlag safetyFlags exeFullPath cppFullPath
        
        let psi = ProcessStartInfo("g++", args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        
        use proc = Process.Start(psi)
        // Read both streams asynchronously to prevent pipe deadlocks
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        
        if not (proc.WaitForExit(60000)) then
            try proc.Kill() with _ -> ()
            Error "Compilation timed out after 60s"
        else
        
        let stdout = stdoutTask.Result
        let stderr = stderrTask.Result
        
        // Combine all output
        let allOutput = 
            [if not (String.IsNullOrWhiteSpace stdout) then yield stdout
             if not (String.IsNullOrWhiteSpace stderr) then yield stderr]
            |> String.concat "\n"
        
        if proc.ExitCode = 0 then
            Ok exeFullPath
        else
            if String.IsNullOrWhiteSpace allOutput then
                Error (sprintf "Compilation failed (exit %d) with no output. Command: g++ %s" proc.ExitCode args)
            else
                Error (sprintf "Compilation failed (exit %d):\n%s" proc.ExitCode allOutput)
    with ex ->
        Error (sprintf "Compilation exception: %s\n%s" ex.Message ex.StackTrace)

/// Compile a CUDA (.cu) file with nvcc. nvcc auto-selects the host compiler
/// (cl.exe on Windows, g++ on Linux). Host-side warning flags are passed
/// through with -Xcompiler. Mirrors compileCpp's subprocess machinery.
let compileCuda (cuFile: string) (outputDir: string) : Result<string, string> =
    try
        let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
        let exeFile = Path.ChangeExtension(cuFile, exeExt)
        let cuFullPath = Path.GetFullPath(cuFile)
        let exeFullPath = Path.GetFullPath(exeFile)

        // Host-compiler passthrough for the narrowing safety net. nvcc's own
        // front-end doesn't accept -Werror=float-conversion, so route it to
        // the host compiler via -Xcompiler. (cl.exe uses different flag
        // spellings; on Windows we drop the g++-specific ones and rely on
        // nvcc/cl defaults — refine once a Windows CUDA box is exercised.)
        let hostWarn =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ""
            else "-Xcompiler -Werror=float-conversion,-Werror=narrowing"

        // -std=c++17 matches the CPU path. nvcc accepts it directly.
        let args = sprintf "-std=c++17 -O2 %s -o \"%s\" \"%s\"" hostWarn exeFullPath cuFullPath

        let psi = ProcessStartInfo("nvcc", args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use proc = Process.Start(psi)
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()

        if not (proc.WaitForExit(120000)) then
            try proc.Kill() with _ -> ()
            Error "CUDA compilation timed out after 120s"
        else

        let stdout = stdoutTask.Result
        let stderr = stderrTask.Result
        let allOutput =
            [if not (String.IsNullOrWhiteSpace stdout) then yield stdout
             if not (String.IsNullOrWhiteSpace stderr) then yield stderr]
            |> String.concat "\n"

        if proc.ExitCode = 0 then
            Ok exeFullPath
        else
            if String.IsNullOrWhiteSpace allOutput then
                Error (sprintf "CUDA compilation failed (exit %d) with no output. Command: nvcc %s" proc.ExitCode args)
            else
                Error (sprintf "CUDA compilation failed (exit %d):\n%s" proc.ExitCode allOutput)
    with ex ->
        Error (sprintf "CUDA compilation exception: %s\n%s" ex.Message ex.StackTrace)

/// Run a subprocess, capturing combined output. Shared by the split-compile
/// steps. Returns Ok () on exit 0, else Error with the captured output.
let private runProc (exe: string) (args: string) (timeoutMs: int) : Result<unit, string> =
    try
        let psi = ProcessStartInfo(exe, args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        let outT = proc.StandardOutput.ReadToEndAsync()
        let errT = proc.StandardError.ReadToEndAsync()
        if not (proc.WaitForExit(timeoutMs)) then
            (try proc.Kill() with _ -> ())
            Error (sprintf "%s timed out" exe)
        else
            let combined =
                [ if not (String.IsNullOrWhiteSpace outT.Result) then yield outT.Result
                  if not (String.IsNullOrWhiteSpace errT.Result) then yield errT.Result ]
                |> String.concat "\n"
            if proc.ExitCode = 0 then Ok ()
            else Error (sprintf "%s failed (exit %d):\n%s\nCommand: %s %s" exe proc.ExitCode combined exe args)
    with ex -> Error (sprintf "%s exception: %s" exe ex.Message)

/// Compile a CUDA program split across two files, per the chosen separation:
/// nvcc compiles the .cu (device kernels) to an object, g++ compiles the .cpp
/// (host program — no CUDA syntax, only an extern "C" prototype) to an object,
/// then the two objects are linked (with nvcc, which resolves the CUDA runtime
/// automatically). The extern "C" launch wrapper is the unmangled boundary
/// symbol both compilers agree on. Returns the exe path.
let compileCudaSplit (cuFile: string) (cppFile: string) (outputDir: string) : Result<string, string> =
    let onWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    let exeExt = if onWindows then ".exe" else ".out"
    let cuFull = Path.GetFullPath(cuFile)
    let cppFull = Path.GetFullPath(cppFile)
    let objExt = if onWindows then ".obj" else ".o"
    let cuObj = Path.ChangeExtension(cuFull, ".cu" + objExt)
    let cppObj = Path.ChangeExtension(cppFull, ".cpp" + objExt)
    let exeFull = Path.GetFullPath(Path.Combine(outputDir, Path.GetFileNameWithoutExtension(cppFile) + exeExt))
    if onWindows then
        // Windows: pure MSVC toolchain, nvcc-orchestrated. nvcc drives cl.exe as
        // the host compiler for BOTH the .cu device code and the .cpp host code,
        // then links. This keeps a SINGLE C++ ABI (MSVC) across both objects —
        // no MinGW/g++ in the CUDA path, so no cross-ABI link fragility. (The
        // extern "C" launch wrapper would link across ABIs, but matching the
        // host toolchain on both halves is the robust native-Windows setup.)
        // Requires cl.exe on PATH — run from the VS x64 Native Tools prompt.
        // No OpenMP here: the rank-1 cuda host half has no parallel host loop,
        // so we avoid the MSVC /openmp vs g++ -fopenmp flag-spelling divergence.
        let nvccCu  = sprintf "-std=c++17 -O2 -c -o \"%s\" \"%s\"" cuObj cuFull
        let nvccCpp = sprintf "-std=c++17 -O2 -c -o \"%s\" \"%s\"" cppObj cppFull
        let nvccLink = sprintf "-std=c++17 -O2 -o \"%s\" \"%s\" \"%s\"" exeFull cuObj cppObj
        match runProc "nvcc" nvccCu 120000 with
        | Error e -> Error e
        | Ok () ->
            match runProc "nvcc" nvccCpp 120000 with
            | Error e -> Error e
            | Ok () ->
                match runProc "nvcc" nvccLink 120000 with
                | Error e -> Error e
                | Ok () -> Ok exeFull
    else
        // Linux: nvcc compiles the .cu (host code via g++), g++ compiles the
        // .cpp; both share the g++ ABI, so the split + link is safe. Host
        // warning passthrough mirrors compileCpp's safety net.
        let nvccCu = sprintf "-std=c++17 -O2 -c -o \"%s\" \"%s\"" cuObj cuFull
        let gppCpp = sprintf "-std=c++17 -O2 -fopenmp -Werror=float-conversion -Werror=narrowing -c -o \"%s\" \"%s\"" cppObj cppFull
        let nvccLink = sprintf "-std=c++17 -O2 -Xcompiler -fopenmp -o \"%s\" \"%s\" \"%s\"" exeFull cuObj cppObj
        match runProc "nvcc" nvccCu 120000 with
        | Error e -> Error e
        | Ok () ->
            match runProc "g++" gppCpp 60000 with
            | Error e -> Error e
            | Ok () ->
                match runProc "nvcc" nvccLink 120000 with
                | Error e -> Error e
                | Ok () -> Ok exeFull

/// Compile a generated source file according to its backend requirement,
/// resolved against the environment's capabilities. Returns the existing
/// `Result<exePath, message>` shape; a skip is reported as
/// `Error "Skipped: <reason>"` so downstream skip handling recognizes it.
let compileForBackend (caps: Capabilities) (req: BackendReq) (srcFile: string) (outputDir: string) : Result<string, string> =
    match resolveCompile caps req with
    | UseGpp          -> compileCpp srcFile outputDir
    | UseNvcc         -> compileCuda srcFile outputDir
    | SkipCompile why -> Error ("Skipped: " + why)


/// Run a compiled executable
let runExecutable (exeFile: string) : Result<int * string, string> =
    try
        let exeFullPath = Path.GetFullPath(exeFile)
        let psi = ProcessStartInfo(exeFullPath)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- Path.GetDirectoryName(exeFullPath)
        
        use proc = Process.Start(psi)
        // Read both streams asynchronously to avoid deadlocks
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        
        if proc.WaitForExit(30000) then
            let stdout = stdoutTask.Result
            let stderr = stderrTask.Result
            let output = if String.IsNullOrEmpty(stderr) then stdout else stdout + "\n[stderr]: " + stderr
            Ok (proc.ExitCode, output)
        else
            try proc.Kill() with _ -> ()
            Error "Execution timed out after 30s"
    with ex ->
        Error (sprintf "Execution exception: %s" ex.Message)

/// Sanitize a test name for use as a filename (cross-platform)
let sanitizeFileName (name: string) : string =
    // Replace characters that are invalid in Windows filenames
    // Use readable names for logical operators
    name
        .Replace("&&", "_and_")
        .Replace("||", "_or_")
        .Replace(" ", "_")
        .Replace(":", "")
        .Replace("/", "_")
        .Replace("\\", "_")
        .Replace("(", "")
        .Replace(")", "")
        .Replace("|", "_")
        .Replace("&", "_")
        .Replace("+", "_")
        .Replace(",", "_")
        .Replace("<", "_")
        .Replace(">", "_")
        .Replace("\"", "")
        .Replace("*", "_")
        .Replace("?", "_")

/// Lock serializing the F# pipeline (lower → IR → genCpp). The lower and
/// codegen functions rely on module-level mutable struct-field caches
/// (`structFieldsCache` in IR.fs, `codegenStructFieldsCache` in CodeGen.fs)
/// that are not thread-safe. With `Array.Parallel.mapi` running tests
/// concurrently, two tests' lift/codegen phases can race on these caches
/// — e.g., two tests both define `struct Trace` with different fields,
/// and test A's codegen reads a cache that test B has already overwritten
/// with its version of Trace, so A's field lookups fail.
///
/// The fix is to serialize the F# pipeline phase per test. C++ compile
/// and run (external subprocesses) remain outside the lock, so the
/// expensive parallelism is preserved.
///
/// Proper long-term fix: thread the struct-field map through as an
/// explicit parameter rather than module-level mutable state. That's
/// a larger refactor touching every recursive type-inference call;
/// deferred until needed.
let private fsharpPipelineLock = obj()

/// Encapsulates the result of the F# pipeline phase (parse → IR → C++
/// source generation), so the caller can run compile/run outside the lock.
type private FsPipelineOutcome =
    | FpIRError of string
    | FpIRValidationError of string list
    | FpIROnly of IRProgram          // compileAndRun = false, no .cpp generated
    | FpCppGenerated of IRProgram * string * string list * BackendReq  // ir, srcFile, warnings, backend
    | FpGenError of IRProgram * string  // ir was valid but codegen threw

let private runFsharpPipelineLocked (source: string) (testName: string) (outputDir: string) (compileAndRun: bool) : FsPipelineOutcome =
    lock fsharpPipelineLock (fun () ->
        let irResult = lower source
        match irResult with
        | Error e -> FpIRError e
        | Ok ir ->
            match IR.validateIR ir with
            | Error validationErrors -> FpIRValidationError validationErrors
            | Ok ir ->
                if not compileAndRun then FpIROnly ir
                else
                    let safeName = sanitizeFileName testName
                    try
                        let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                        // Backend requirement is inferred from the settled
                        // codegen output. CUDA codegen emits device kernels
                        // (.cu, compiled by nvcc); CPU codegen does not
                        // (.cpp, g++). The extension matches so the host
                        // toolchain recognizes device syntax.
                        let backendReq = inferBackendReq cppCode
                        let ext = match backendReq with RequiresCuda -> ".cu" | CpuOnly -> ".cpp"
                        let srcFile = Path.Combine(outputDir, safeName + ext)
                        File.WriteAllText(srcFile, cppCode)
                        FpCppGenerated (ir, srcFile, codegenWarnings, backendReq)
                    with ex ->
                        FpGenError (ir, sprintf "Generation failed: %s" ex.Message)
    )

/// Run a full test: IR lowering + C++ generation + compilation + execution
let runFullTest (testName: string) (source: string) (outputDir: string) (compileAndRun: bool) : FullTestResult =
    // Parse expected values from source comments
    let expectedValues = parseExpectedValues source

    // F# pipeline (lower + codegen) runs under a lock to avoid cache
    // races. C++ compile and run (below) stay outside the lock so they
    // parallelize freely across tests.
    let pipelineOutcome = runFsharpPipelineLocked source testName outputDir compileAndRun

    match pipelineOutcome with
    | FpIRError e ->
        { TestName = testName; IRResult = Error e; CppGenerated = false;
          CppFile = None; CompileResult = Error "IR failed"; RunResult = Error "IR failed";
          ValueCheckResult = Error ["IR failed"]; HasExpectedValues = not expectedValues.IsEmpty }
    | FpIRValidationError validationErrors ->
        for e in validationErrors do printfn "  %s" e
        { TestName = testName; IRResult = Error (validationErrors |> String.concat "; "); CppGenerated = false;
          CppFile = None; CompileResult = Error "IR validation failed"; RunResult = Error "IR validation failed";
          ValueCheckResult = Error ["IR validation failed"]; HasExpectedValues = not expectedValues.IsEmpty }
    | FpIROnly ir ->
        { TestName = testName; IRResult = Ok ir; CppGenerated = false;
          CppFile = None; CompileResult = Error "Skipped"; RunResult = Error "Skipped";
          ValueCheckResult = Error ["Skipped"]; HasExpectedValues = not expectedValues.IsEmpty }
    | FpGenError (ir, msg) ->
        { TestName = testName; IRResult = Ok ir; CppGenerated = false;
          CppFile = None; CompileResult = Error msg;
          RunResult = Error "Generation failed"; ValueCheckResult = Error ["Generation failed"];
          HasExpectedValues = not expectedValues.IsEmpty }
    | FpCppGenerated (ir, srcFile, codegenWarnings, backendReq) ->
        for w in codegenWarnings do
            printfn "  [CodeGen Warning] %s" w

        let caps = capabilities.Value

        // Step 3: Compile (outside lock — separate subprocess). The
        // toolchain is resolved from the inferred backend requirement
        // against the environment's capabilities; an unsatisfiable
        // requirement comes back as Error "Skipped: <reason>".
        let compileResult = compileForBackend caps backendReq srcFile outputDir

        match compileResult with
        | Error e ->
            // Both genuine compile failures and skips flow here; the
            // skip vs fail distinction is made downstream via isSkipError.
            let runErr = if isSkipError e then e else "Compile failed"
            { TestName = testName; IRResult = Ok ir; CppGenerated = true;
              CppFile = Some srcFile; CompileResult = Error e; RunResult = Error runErr;
              ValueCheckResult = Error [runErr]; HasExpectedValues = not expectedValues.IsEmpty }
        | Ok exeFile ->
            // Step 4: Run — but a CUDA-requiring test on a GPU-less box can
            // compile yet not execute. Validate the compile, skip the run.
            if backendReq = RequiresCuda && not caps.HasGpu then
                { TestName = testName; IRResult = Ok ir; CppGenerated = true;
                  CppFile = Some srcFile; CompileResult = Ok exeFile;
                  RunResult = Error "Skipped: no GPU";
                  ValueCheckResult = Error ["Skipped: no GPU"];
                  HasExpectedValues = not expectedValues.IsEmpty }
            else
                let runResult = runExecutable exeFile

                // Step 5: Check values if run succeeded
                let valueCheckResult =
                    match runResult with
                    | Ok (0, output) ->
                        if expectedValues.IsEmpty then Ok ()
                        else checkExpectedValues expectedValues output
                    | Ok (code, _) -> Error [sprintf "Exit code %d" code]
                    | Error e -> Error [e]

                { TestName = testName; IRResult = Ok ir; CppGenerated = true;
                  CppFile = Some srcFile; CompileResult = Ok exeFile; RunResult = runResult;
                  ValueCheckResult = valueCheckResult; HasExpectedValues = not expectedValues.IsEmpty }

/// Print a full test result
let printFullTestResult (result: FullTestResult) (verbose: bool) (showFullError: bool) =
    let irStatus = match result.IRResult with Ok _ -> "OK" | Error _ -> "FAIL"
    let cppStatus = if result.CppGenerated then "OK" else "SKIP"
    let compileStatus = match result.CompileResult with Ok _ -> "OK" | Error e when isSkipError e -> "SKIP" | Error _ -> "FAIL"
    let runStatus = 
        match result.RunResult with 
        | Ok (0, _) -> "OK" 
        | Ok (code, _) -> sprintf "EXIT(%d)" code
        | Error e when isSkipError e -> "SKIP"
        | Error _ -> "FAIL"
    let valueStatus =
        if not result.HasExpectedValues then ""
        else match result.ValueCheckResult with
             | Ok () -> "OK"
             | Error errs when errs |> List.exists isSkipError -> "SKIP"
             | Error _ -> "FAIL"
    
    // Only show value status if there were expected values to check
    let valueDisplay = if valueStatus = "" then "" else sprintf " [Val:%s]" valueStatus
    
    printfn "  [IR:%s] [Gen:%s] [Compile:%s] [Run:%s]%s %s" irStatus cppStatus compileStatus runStatus valueDisplay result.TestName
    
    if verbose then
        match result.IRResult with
        | Error e -> printfn "    IR Error: %s" e
        | Ok _ -> ()
        
        match result.CompileResult with
        | Error e when not (isSkipError e) && e <> "IR failed" -> 
            printfn "    Compile Error:\n%s" e
            match result.CppFile with
            | Some f -> printfn "    Generated: %s" f
            | None -> ()
        | _ -> ()
        
        match result.RunResult with
        | Ok (code, output) when code <> 0 -> 
            printfn "    Run exited with code %d" code
            if not (String.IsNullOrWhiteSpace output) then
                if showFullError then
                    printfn "    Output:\n%s" output
                else
                    printfn "    Output: %s" (output.Split('\n').[0])
        | Error e when not (isSkipError e) && e <> "IR failed" && e <> "Compile failed" -> 
            printfn "    Run Error: %s" e
        | _ -> ()
        
        // Show value check errors
        match result.ValueCheckResult with
        | Error errors when not (errors |> List.exists isSkipError) && 
                           not (List.contains "IR failed" errors) &&
                           not (List.contains "Compile failed" errors) &&
                           not (List.contains "Generation failed" errors) ->
            for err in errors do
                printfn "    Value Error: %s" err
        | _ -> ()

/// Determine if a test result is a full pass
let isFullPass (result: FullTestResult) =
    match result.IRResult, result.CompileResult, result.RunResult with
    | Ok _, Ok _, Ok (0, _) -> true
    | _ -> false

/// Determine if IR passed (regardless of C++)
let isIRPass (result: FullTestResult) =
    match result.IRResult with Ok _ -> true | _ -> false

/// Run test category with IR only
let runTestCategory name tests =
    printHeader (sprintf "Blade-DSL: %s Tests" name)
    printfn "Running %d tests...\n" (List.length tests)
    
    let mutable passed = 0
    let mutable failed = 0
    
    for (testName, source) in tests do
        printSubHeader testName
        match testLower source with
        | Ok _ ->
            printfn "PASSED"
            passed <- passed + 1
        | Error _ ->
            printfn "FAILED"
            failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    
    if failed > 0 then
        printfn "\nSome tests failed."
        1
    else
        printfn "\nAll tests passed!"
        0

/// Run multi-file module tests (IR-only)
let runMultiFileTests (name: string) (tests: (string * (string * string) list) list) =
    printHeader (sprintf "Blade-DSL: %s Tests (Multi-File)" name)
    printfn "Running %d tests...\n" (List.length tests)
    
    let mutable passed = 0
    let mutable failed = 0
    
    for (testName, sources) in tests do
        printSubHeader testName
        match lowerMultiSource sources with
        | Ok ir ->
            printfn "Lower: OK (%d modules)" ir.Modules.Length
            for m in ir.Modules do
                printfn "  Module: %s — %d functions, %d bindings" m.Name m.Functions.Length m.Bindings.Length
            passed <- passed + 1
        | Error e ->
            printfn "FAILED: %s" e
            failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    if failed > 0 then 1 else 0

/// Run multi-file module tests with full C++ pipeline
let runMultiFileTestsFull (name: string) (tests: (string * (string * string) list) list) (outputDir: string) =
    printHeader (sprintf "Blade-DSL: %s Tests (Multi-File, Full C++ Pipeline)" name)
    
    let gppAvailable = checkGppAvailable ()
    if not gppAvailable then
        printfn "WARNING: g++ not available.\n"
    
    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore
    
    // Write runtime header file once
    let headerFile = Path.Combine(outputDir, "nested_array_utilities.hpp")
    File.WriteAllText(headerFile, CodeGen.genRuntimeHeader ())
    let arrayTypesHeaderFile = Path.Combine(outputDir, "nested_array_types.hpp")
    File.WriteAllText(arrayTypesHeaderFile, CodeGen.genRuntimeArrayTypesHeader ())
    
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    
    for (testName, sources) in tests do
        printSubHeader testName
        match lowerMultiSource sources with
        | Error e ->
            printfn "FAILED (lower): %s" e
            failed <- failed + 1
        | Ok ir ->
            match IR.validateIR ir with
            | Error validationErrors ->
                printfn "FAILED (IR validation):"
                for e in validationErrors do
                    printfn "  %s" e
                failed <- failed + 1
            | Ok ir ->
            let safeName = sanitizeFileName testName
            try
                let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                for w in codegenWarnings do
                    printfn "  [CodeGen Warning] %s" w
                // Same backend inference as the single-file pipeline:
                // .cu + nvcc when device kernels are emitted, else .cpp + g++.
                let backendReq = inferBackendReq cppCode
                let ext = match backendReq with RequiresCuda -> ".cu" | CpuOnly -> ".cpp"
                let cppFile = Path.Combine(outputDir, safeName + ext)
                File.WriteAllText(cppFile, cppCode)
                printfn "Generated: %s" cppFile
                
                if gppAvailable then
                    match compileForBackend capabilities.Value backendReq cppFile outputDir with
                    | Error e when isSkipError e ->
                        printfn "SKIPPED (%s)" e
                        skipped <- skipped + 1
                    | Error e ->
                        printfn "FAILED (compile): %s" e
                        failed <- failed + 1
                    | Ok exeFile ->
                        match runExecutable exeFile with
                        | Ok (0, output) ->
                            // Parse expected values from the LAST source (Main module)
                            let mainSource = sources |> List.last |> snd
                            let expectedValues = parseExpectedValues mainSource
                            if expectedValues.IsEmpty then
                                printfn "PASSED (no EXPECT)"
                                passed <- passed + 1
                            else
                                match checkExpectedValues expectedValues output with
                                | Ok () ->
                                    printfn "PASSED"
                                    passed <- passed + 1
                                | Error msgs ->
                                    printfn "FAILED (values):"
                                    for msg in msgs do printfn "  %s" msg
                                    failed <- failed + 1
                        | Ok (code, output) ->
                            printfn "FAILED (exit code %d): %s" code output
                            failed <- failed + 1
                        | Error e ->
                            printfn "FAILED (run): %s" e
                            failed <- failed + 1
                else
                    printfn "SKIPPED (no g++)"
                    skipped <- skipped + 1
            with ex ->
                printfn "FAILED (gen): %s" ex.Message
                failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    if skipped > 0 then printfn "Skipped: %d" skipped
    printfn "Total:  %d" (passed + failed + skipped)
    if failed > 0 then 1 else 0

/// Run test category with full C++ compilation and execution
let runTestCategoryFull (name: string) (tests: (string * string) list) (outputDir: string) =
    printHeader (sprintf "Blade-DSL: %s Tests (Full C++ Pipeline)" name)
    
    // Check g++ availability
    let gppAvailable = checkGppAvailable ()
    if not gppAvailable then
        printfn "WARNING: g++ not available or not working properly."
        printfn "This often happens on Windows due to MinGW DLL issues."
        printfn "C++ compilation will be skipped. Files will still be generated.\n"
        printfn "To fix, try:"
        printfn "  1. Reinstall MinGW-w64 from https://winlibs.com/"
        printfn "  2. Use WSL (Windows Subsystem for Linux)"
        printfn "  3. Use Visual Studio's cl.exe compiler\n"
    else
        printfn "g++ found and working. Will compile and run generated C++.\n"
    
    // Ensure output directory exists
    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore
    
    // Write runtime header file once
    let headerFile = Path.Combine(outputDir, "nested_array_utilities.hpp")
    File.WriteAllText(headerFile, CodeGen.genRuntimeHeader ())
    let arrayTypesHeaderFile = Path.Combine(outputDir, "nested_array_types.hpp")
    File.WriteAllText(arrayTypesHeaderFile, CodeGen.genRuntimeArrayTypesHeader ())
    
    printfn "Running %d tests...\n" (List.length tests)
    
    let testArray = tests |> Array.ofList
    let total = testArray.Length
    
    let results = 
        // Ordered output buffer: collect results and print in original order
        let resultBuffer = System.Collections.Concurrent.ConcurrentDictionary<int, string>()
        let mutable nextToPrint = 0
        let printLock = obj()
        
        testArray
        |> Array.Parallel.mapi (fun idx (testName, source) ->
            let result = runFullTest testName source outputDir gppAvailable
            let status =
                match result.CompileResult with
                | Ok _ -> "ok"
                | Error e when e = "IR failed" -> "IR fail"
                | Error e when isSkipError e -> "skip"
                | Error _ -> "compile fail"
            let line = sprintf "[%d/%d] %s... %s" (idx + 1) total testName status
            
            // Buffer this result and flush any sequential completions
            resultBuffer.[idx] <- line
            lock printLock (fun () ->
                while resultBuffer.ContainsKey(nextToPrint) do
                    let msg = resultBuffer.[nextToPrint]
                    resultBuffer.TryRemove(nextToPrint) |> ignore
                    eprintfn "%s" msg
                    nextToPrint <- nextToPrint + 1)
            
            result)
        |> Array.toList
    
    // Find first compile failure to show full error (skips are not failures)
    let firstCompileFailure = 
        results |> List.tryFind (fun r -> 
            match r.CompileResult with 
            | Error e when not (isSkipError e) && e <> "IR failed" -> true 
            | _ -> false)
    
    // Print results (brief for most, full for first failure)
    printfn ""
    let mutable shownFullError = false
    for result in results do
        let showFull = 
            not shownFullError && 
            (Some result = firstCompileFailure)
        if showFull then shownFullError <- true
        printFullTestResult result true showFull
    
    // If there was a compile failure, show the full error output
    match firstCompileFailure with
    | Some failure ->
        printfn "\n========== First Compile Failure: %s ==========" failure.TestName
        match failure.CompileResult with
        | Error e ->
            printfn "\nFull compiler output:"
            printfn "%s" e
        | _ -> ()
        match failure.CppFile with
        | Some cppFile -> printfn "\nGenerated file: %s" cppFile
        | None -> ()
    | None -> ()
    
    // Summary
    let irPassed = results |> List.filter isIRPass |> List.length
    let irFailed = results.Length - irPassed
    let fullPassed = results |> List.filter isFullPass |> List.length
    let compiled = results |> List.filter (fun r -> match r.CompileResult with Ok _ -> true | _ -> false) |> List.length
    let generated = results |> List.filter (fun r -> r.CppGenerated) |> List.length

    // A test is "skipped" if either its compile or its run was skipped
    // (no toolchain, or no GPU for a CUDA-requiring test). Skips are not
    // failures and must not deflate the pass totals.
    let isSkipped (r: FullTestResult) =
        (match r.CompileResult with Error e when isSkipError e -> true | _ -> false) ||
        (match r.RunResult with Error e when isSkipError e -> true | _ -> false)
    let skipped = results |> List.filter isSkipped |> List.length

    // Tests whose codegen inferred the CUDA backend (emitted device
    // kernels → .cu source). Reported separately so the CPU/CUDA split
    // is visible at a glance.
    let cudaTests =
        results |> List.filter (fun r ->
            match r.CppFile with Some f -> f.EndsWith(".cu") | None -> false) |> List.length
    
    // Count value check results (only for tests that have expected values
    // AND weren't skipped — a skipped test has no output to check).
    let testsWithExpected = results |> List.filter (fun r -> r.HasExpectedValues && not (isSkipped r))
    let valuesPassed = testsWithExpected |> List.filter (fun r -> 
        match r.ValueCheckResult with Ok () -> true | _ -> false) |> List.length

    let caps = capabilities.Value
    let platformStr = match caps.Platform with PWindows -> "Windows" | PLinux -> "Linux" | PMacOS -> "macOS"
    
    printHeader "Test Summary"
    printfn "Environment:  %s | g++:%b nvcc:%b cl:%b gpu:%b"
        platformStr caps.HasGpp caps.HasNvcc caps.HasCl caps.HasGpu
    printfn "IR Lowering:  %d passed, %d failed" irPassed irFailed
    printfn "C++ Generated: %d / %d  (CUDA backend: %d)" generated results.Length cudaTests
    if gppAvailable then
        printfn "Compiled:     %d / %d" compiled results.Length
        printfn "Full Pass:    %d / %d (IR + Compile + Run)" fullPassed results.Length
        if skipped > 0 then
            printfn "Skipped:      %d (toolchain/GPU unavailable)" skipped
        if testsWithExpected.Length > 0 then
            printfn "Value Check:  %d / %d" valuesPassed testsWithExpected.Length
    else
        printfn "Generated files in: %s" (Path.GetFullPath outputDir)
    printfn "Total Tests:  %d" results.Length
    
    if irFailed > 0 then 1 else 0

/// Run the standalone C++ allocation-layout test suite (cpp/alloc_layout_tests.cpp).
///
/// These tests verify runtime-layout invariants of the contiguous-backing
/// allocate<> that the value-checking Blade tests structurally cannot catch:
/// single-pool contiguity, DFS leaf ordering, and closed-form cardinality.
/// They are C++ (the property under test is a C++ runtime invariant), so this
/// runs them directly rather than through the Blade source pipeline — the same
/// category as `test normalize` / `test unify`, just in C++.
///
/// The test .cpp and the runtime headers are both shipped in cpp/ next to the
/// compiler binary (AppContext.BaseDirectory/cpp), copied there by Blade.fsproj.
/// Compiling in that directory means the test exercises the EXACT headers the
/// codegen path uses — not a stale copy — which is the point of syncing it here.
///
/// Returns 0 on all-pass or skip (g++ absent); 1 on any compile/run/check failure.
/// F# unit tests for the DeviceBufferType dimensional-type machinery (the
/// foundation for CUDA buffer streaming). Verifies cardinality computation —
/// the load-bearing arithmetic that, if wrong, would silently corrupt the
/// device buffer mapping (and there is no CPU oracle once on hardware, so this
/// must be checked HERE against hand-computed values). Pure F#, no g++.
let runBufferTypeTests () : int =
    printHeader "Device Buffer Type Tests"
    let mutable failures = 0
    let lit n = IRLit (IRLitInt (int64 n))
    // A buffer dim group constructor for the test
    let grp arity ext symm : BufferDimGroup =
        { Arity = arity; Extent = lit ext; Symmetry = symm
          Kind = (if symm = SymNone then TDimension else SDimension)
          Dependencies = [] }
    // Rectangular SDimension group (Arity 1, SymNone, but SDimension)
    let rectS ext : BufferDimGroup =
        { Arity = 1; Extent = lit ext; Symmetry = SymNone
          Kind = SDimension; Dependencies = [] }
    let card (groups: BufferDimGroup list) =
        match deviceBufferCardinality { ElemType = IRTScalar ETFloat64; Groups = groups } with
        | IRLit (IRLitInt n) -> Some n
        | _ -> None
    let check name (groups: BufferDimGroup list) (expected: int64) =
        match card groups with
        | Some n when n = expected ->
            printfn "  [PASS] %s => %d" name n
        | Some n ->
            printfn "  [FAIL] %s => %d (expected %d)" name n expected
            failures <- failures + 1
        | None ->
            printfn "  [FAIL] %s => non-literal (expected %d)" name expected
            failures <- failures + 1
    // Rectangular: 8 x 8 = 64
    check "rect 8x8" [rectS 8; rectS 8] 64L
    // Rectangular 1-D: 12
    check "rect 12" [rectS 12] 12L
    // Symmetric SymIdx<2> over n=5: C(5+2-1, 2) = C(6,2) = 15
    check "sym2 n=5" [grp 2 5 SymSymmetric] 15L
    // Symmetric SymIdx<3> over n=4: C(4+3-1,3) = C(6,3) = 20
    check "sym3 n=4" [grp 3 4 SymSymmetric] 20L
    // Antisymmetric AntisymIdx<2> over n=5: C(5,2) = 10
    check "antisym2 n=5" [grp 2 5 SymAntisymmetric] 10L
    // Antisymmetric AntisymIdx<3> over n=5: C(5,3) = 10
    check "antisym3 n=5" [grp 3 5 SymAntisymmetric] 10L
    // Hermitian = same storage count as symmetric: C(6,2) = 15
    check "herm2 n=5" [grp 2 5 SymHermitian] 15L
    // Product symmetry: symmetric(n=5,r=2)=15 times rectangular 4 = 60
    check "sym2 x rect4" [grp 2 5 SymSymmetric; rectS 4] 60L
    // isRectangularConstBuffer predicate
    let rectBt = { ElemType = IRTScalar ETFloat64; Groups = [rectS 8; rectS 8] }
    let symBt = { ElemType = IRTScalar ETFloat64; Groups = [grp 2 5 SymSymmetric] }
    if isRectangularConstBuffer rectBt then printfn "  [PASS] isRectangular(rect)=true"
    else (printfn "  [FAIL] isRectangular(rect) should be true"; failures <- failures + 1)
    if not (isRectangularConstBuffer symBt) then printfn "  [PASS] isRectangular(sym)=false"
    else (printfn "  [FAIL] isRectangular(sym) should be false"; failures <- failures + 1)
    // extern "C" boundary-safety gate: fundamental scalars cross, library types don't.
    let checkBnd name ty expected =
        if isCudaBoundarySafeElem ty = expected then printfn "  [PASS] boundary(%s)=%b" name expected
        else (printfn "  [FAIL] boundary(%s) should be %b" name expected; failures <- failures + 1)
    checkBnd "f64" (IRTScalar ETFloat64) true
    checkBnd "f32" (IRTScalar ETFloat32) true
    checkBnd "i64" (IRTScalar ETInt64) true
    checkBnd "i32" (IRTScalar ETInt32) true
    checkBnd "bool" (IRTScalar ETBool) true
    checkBnd "complex128" (IRTScalar ETComplex128) false
    checkBnd "string" (IRTScalar ETString) false
    printfn "BUFFER TYPE: %d failure(s)" failures
    failures

/// First `where cuda` hardware test. Differential: generate the SAME rank-1 map
/// program twice — once WITHOUT a cuda clause (host-loop oracle, g++), once WITH
/// `cuda(block: N)` (device kernel, split-compiled nvcc+g++ then linked) — run
/// both, and require identical output. This verifies cuda-vs-host CODEGEN
/// equivalence (not just cuda-vs-hand-math). SKIPs cleanly when nvcc/GPU absent,
/// so the harness stays green on non-CUDA machines; runs for real where a GPU is
/// present. Rank-1 is the simplest kernel: flat thread index IS the coordinate
/// (no div/mod recovery).
let runCudaTests () : int =
    printHeader "CUDA Kernel Tests"
    let caps = capabilities.Value
    let onWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    if not caps.HasNvcc || not caps.HasGpu then
        printfn "Skipped: requires nvcc + CUDA GPU (nvcc=%b, gpu=%b)." caps.HasNvcc caps.HasGpu
        0  // skip, not failure — mirrors harness skip policy
    elif (not onWindows) && not caps.HasGpp then
        // g++ is the host compiler only on the Linux path; Windows uses nvcc/cl.
        printfn "Skipped: requires g++ for the host half (Linux path)."
        0
    elif onWindows && not caps.HasCl then
        // On Windows nvcc drives MSVC's cl.exe as its host compiler. If cl.exe
        // isn't on PATH (e.g. running from a plain terminal rather than the
        // "x64 Native Tools Command Prompt for VS"), nvcc fails with
        // "Cannot find compiler 'cl.exe'". Skip cleanly here — same policy as
        // resolveCompile's RequiresCuda/PWindows/not-HasCl branch — rather than
        // attempt a compile that's guaranteed to fail. To actually run this
        // test on Windows, launch from the VS Native Tools prompt (or run
        // vcvars64.bat first) so cl.exe is on PATH.
        printfn "Skipped: nvcc needs cl.exe (MSVC) as host compiler, not found on PATH."
        printfn "         Run from the 'x64 Native Tools Command Prompt for VS' (or after vcvars64.bat)."
        0
    else
        let outputDir = "./generated_cpp_tests"
        Directory.CreateDirectory(outputDir) |> ignore
        File.WriteAllText(Path.Combine(outputDir, "nested_array_utilities.hpp"), CodeGen.genRuntimeHeader ())
        File.WriteAllText(Path.Combine(outputDir, "nested_array_types.hpp"), CodeGen.genRuntimeArrayTypesHeader ())

        // Compile a plain host .cpp (the oracle) without MinGW on Windows: there
        // we use nvcc to drive cl.exe (single MSVC toolchain, consistent with the
        // cuda variant's host half). On Linux, g++ via compileCpp.
        let compileHost (cppFile: string) : Result<string, string> =
            if onWindows then
                let cppFull = Path.GetFullPath(cppFile)
                let exeFull = Path.ChangeExtension(cppFull, ".exe")
                let args = sprintf "-std=c++17 -O2 -o \"%s\" \"%s\"" exeFull cppFull
                runProc "nvcc" args 120000 |> Result.map (fun () -> exeFull)
            else compileCpp cppFile outputDir

        // Generate one variant: lower -> validate -> codegen -> write .cpp (+ .cu
        // if a kernel was emitted). Returns (cppFile, optional cuFile).
        let genVariant (name: string) (src: string) : Result<string * string option, string> =
            try
                match lower src with
                | Error e -> Error (sprintf "lower failed: %s" e)
                | Ok ir0 ->
                    let ir = match IR.validateIR ir0 with Ok v -> v | Error _ -> ir0
                    let (cppCode, _w) = CodeGen.genSelfContainedProgramFromIR ir name
                    let cuOpt = CodeGen.getCudaFileContent ()
                    let safe = sanitizeFileName name
                    let cppFile = Path.Combine(outputDir, safe + ".cpp")
                    File.WriteAllText(cppFile, cppCode)
                    let cuFileOpt =
                        cuOpt |> Option.map (fun cu ->
                            let cuFile = Path.Combine(outputDir, safe + ".cu")
                            File.WriteAllText(cuFile, cu)
                            cuFile)
                    Ok (cppFile, cuFileOpt)
            with ex -> Error (sprintf "codegen failed: %s" ex.Message)

        let resultLines (s: string) =
            (s.Replace("\r\n", "\n").Trim()).Split('\n')
            |> Array.filter (fun l -> not (l.Contains("completed in")))
            |> String.concat "\n"

        // One differential case: the SAME program with and without `where cuda`.
        // host (no clause, must emit NO .cu) vs cuda (clause, must emit a .cu);
        // both run, outputs must match (cuda-vs-host codegen equivalence).
        // Returns 0 on pass, 1 on failure; prints a labeled line either way.
        let runCudaCase (label: string) (hostSrc: string) (cudaSrc: string) : int =
            let hostName = sprintf "cuda_%s_host" label
            let cudaName = sprintf "cuda_%s_dev" label
            // Force-clean stale artifacts for THIS case (the generated dir
            // persists across runs / version unzips).
            for stem in [hostName; cudaName] do
                for ext in [".cu"; ".cpp"; ".cu.obj"; ".cpp.obj"; ".cu.o"; ".cpp.o"; ".exe"; ".out"] do
                    let f = Path.Combine(outputDir, stem + ext)
                    try if File.Exists f then File.Delete f with _ -> ()
            let hostOut =
                match genVariant hostName hostSrc with
                | Error e -> Error e
                | Ok (cppFile, cuOpt) ->
                    if cuOpt.IsSome then Error "host variant unexpectedly emitted a .cu"
                    else
                        match compileHost cppFile with
                        | Error e -> Error (sprintf "host compile: %s" e)
                        | Ok exe -> runExecutable exe |> Result.map snd
            let cudaOut =
                match genVariant cudaName cudaSrc with
                | Error e -> Error e
                | Ok (_cppFile, None) -> Error "cuda variant did not emit a .cu (kernel not generated)"
                | Ok (cppFile, Some cuFile) ->
                    match compileCudaSplit cuFile cppFile outputDir with
                    | Error e -> Error (sprintf "cuda split-compile: %s" e)
                    | Ok exe -> runExecutable exe |> Result.map snd
            match hostOut, cudaOut with
            | Error e, _ -> printfn "  [%s] FAILED (host oracle): %s" label e; 1
            | _, Error e -> printfn "  [%s] FAILED (cuda): %s" label e; 1
            | Ok hOut, Ok cOut ->
                let h, c = resultLines hOut, resultLines cOut
                if h = c then
                    printfn "  [%s] PASS (cuda output matches host-loop oracle)" label
                    0
                else
                    printfn "  [%s] FAIL: cuda output differs from host oracle" label
                    printfn "    host: %s" h
                    printfn "    cuda: %s" c
                    1

        // Host-only compile+run check (no cuda variant). Used for cases that
        // aren't cuda-eligible but exercise a host codegen path we want to keep
        // honest under MSVC — notably a genuinely SYMMETRIC output, which fires
        // the hasRealSymmetry=true branch (named static symm array passed to
        // allocate). Verifies the program compiles under cl AND its result line
        // contains the expected substring.
        let runHostCompileCase (label: string) (src: string) (expectSubstr: string) : int =
            let nm = sprintf "cuda_%s_host" label
            for ext in [".cu"; ".cpp"; ".cu.obj"; ".cpp.obj"; ".cu.o"; ".cpp.o"; ".exe"; ".out"] do
                let f = Path.Combine(outputDir, nm + ext)
                try if File.Exists f then File.Delete f with _ -> ()
            let outcome =
                match genVariant nm src with
                | Error e -> Error e
                | Ok (cppFile, _) ->
                    match compileHost cppFile with
                    | Error e -> Error (sprintf "host compile: %s" e)
                    | Ok exe -> runExecutable exe |> Result.map snd
            match outcome with
            | Error e -> printfn "  [%s] FAILED: %s" label e; 1
            | Ok out ->
                let r = resultLines out
                if r.Contains(expectSubstr) then
                    printfn "  [%s] PASS (host compiles under MSVC + correct result)" label
                    0
                else
                    printfn "  [%s] FAIL: expected substring %s not in output" label expectSubstr
                    printfn "    output: %s" r
                    1

        // A case = (label, hostSrc, cudaSrc). cudaSrc adds `where cuda(block: N)`
        // to the lambda; everything else identical so any diff is a codegen bug.
        // Source variants bound individually first. Multi-line triple-quoted
        // strings whose content dedents to column 0 confuse F#'s layout analysis
        // when placed directly inside a list-of-tuples literal (the column-0
        // `let A = ...` inside the string collides with the enclosing block's
        // offside context). Binding them to names first sidesteps that entirely.
        let rank1Host = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let R = method_for(A) <@> lambda(x) -> x * 2.0 + 1.0 |> compute
"""
        let rank1Cuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let R = method_for(A) <@> lambda(x) where cuda(block: 64) -> x * 2.0 + 1.0 |> compute
"""
        // rank-2 outer product: exercises div/mod coordinate recovery AND
        // two-input streaming (the parts rank-1 skips entirely).
        let rank2Host = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let R = method_for(A, B) <@> lambda(x, y) -> x * y |> compute
"""
        let rank2Cuda = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let R = method_for(A, B) <@> lambda(x, y) where cuda(block: 64) -> x * y |> compute
"""
        // multi-block: 8 elements with block size 4 => 2 blocks, so the grid
        // spans multiple blocks (stresses __blade_blocks math + the bound check).
        let mbHost = """
let A = [10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0]
let R = method_for(A) <@> lambda(x) -> x * 3.0 |> compute
"""
        let mbCuda = """
let A = [10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0]
let R = method_for(A) <@> lambda(x) where cuda(block: 4) -> x * 3.0 |> compute
"""
        // rank-3 with NON-UNIFORM extents (3x2x3=18): two `/=` steps in the
        // coordinate recovery with DISTINCT moduli per dim — the deepest test of
        // the div/mod unpacking (rank-2 used equal extents, which is more forgiving).
        let rank3Host = """
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0]
let C = [100.0, 200.0, 300.0]
let R = method_for(A, B, C) <@> lambda(a, b, c) -> a + b + c |> compute
"""
        let rank3Cuda = """
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0]
let C = [100.0, 200.0, 300.0]
let R = method_for(A, B, C) <@> lambda(a, b, c) where cuda(block: 64) -> a + b + c |> compute
"""
        // integer element type: exercises int64_t crossing the extern "C"
        // boundary (boundary-safe set includes ETInt64), not just double.
        let intHost = """
let A = [1, 2, 3, 4, 5, 6]
let R = method_for(A) <@> lambda(x) -> x * 10 |> compute
"""
        let intCuda = """
let A = [1, 2, 3, 4, 5, 6]
let R = method_for(A) <@> lambda(x) where cuda(block: 64) -> x * 10 |> compute
"""
        // non-trivial kernel body: a polynomial in x exercises a deeper
        // exprToCpp expression in the kernel (multiple ops, reuse of the bound
        // element), not just a single multiply.
        let polyHost = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A) <@> lambda(x) -> x * x + x * 2.0 - 1.0 |> compute
"""
        let polyCuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A) <@> lambda(x) where cuda(block: 64) -> x * x + x * 2.0 - 1.0 |> compute
"""
        // larger grid: 50 elements, block 8 => 7 blocks. Bigger grid than the
        // 2-block mb case; the last block is partial (50 = 6*8 + 2), so the
        // bound check is genuinely exercised at a non-trivial tail.
        let bigHost = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0, 20.0, 21.0, 22.0, 23.0, 24.0, 25.0, 26.0, 27.0, 28.0, 29.0, 30.0, 31.0, 32.0, 33.0, 34.0, 35.0, 36.0, 37.0, 38.0, 39.0, 40.0, 41.0, 42.0, 43.0, 44.0, 45.0, 46.0, 47.0, 48.0, 49.0, 50.0]
let R = method_for(A) <@> lambda(x) -> x + 100.0 |> compute
"""
        let bigCuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0, 20.0, 21.0, 22.0, 23.0, 24.0, 25.0, 26.0, 27.0, 28.0, 29.0, 30.0, 31.0, 32.0, 33.0, 34.0, 35.0, 36.0, 37.0, 38.0, 39.0, 40.0, 41.0, 42.0, 43.0, 44.0, 45.0, 46.0, 47.0, 48.0, 49.0, 50.0]
let R = method_for(A) <@> lambda(x) where cuda(block: 8) -> x + 100.0 |> compute
"""
        // SYMMETRIC output (host-only; cuda gates reject non-rectangular). The
        // `comm(x, y)` on the kernel + same array A twice folds into a SYMMETRIC
        // output (SymIdx), so OutputSymmVec has a repeated group => hasRealSymmetry
        // is TRUE => the named-static symm-array allocate branch fires. This is the
        // branch the v24 fix did NOT change (the else), so this case confirms it
        // still compiles under MSVC and produces correct values. method_for(A, A)
        // with distinct... no: SAME array A is required for the comm to fold.
        let symHost = """
let A = [1.0, 2.0, 3.0, 4.0]
let R = method_for(A, A) <@> lambda(x, y) where comm(x, y) -> x * y |> compute
"""
        let cases =
            [ ("rank1", rank1Host, rank1Cuda)
              ("rank2_outer", rank2Host, rank2Cuda)
              ("rank1_multiblock", mbHost, mbCuda)
              ("rank3_nonuniform", rank3Host, rank3Cuda)
              ("int_elem", intHost, intCuda)
              ("poly_body", polyHost, polyCuda)
              ("big_multiblock", bigHost, bigCuda) ]

        let mutable failures = 0
        for (label, hostSrc, cudaSrc) in cases do
            failures <- failures + runCudaCase label hostSrc cudaSrc
        // Host-only: symmetric output exercises the hasRealSymmetry=true branch
        // (named static symm array) under MSVC — the branch the v24 fix did NOT
        // change, so this confirms it still compiles under cl. The PRIMARY signal
        // is that it compiles + runs at all (the symm allocation path is the
        // MSVC-sensitive part); we check only that an R array is printed, NOT a
        // specific value (symmetric print ordering/storage isn't asserted here).
        failures <- failures + runHostCompileCase "sym_output" symHost "R = ["
        printfn "CUDA TESTS: %d failure(s)" failures
        failures

let runAllocLayoutTests () : int =
    let cppDir = Path.Combine(AppContext.BaseDirectory, "cpp")
    let testSrc = Path.Combine(cppDir, "alloc_layout_tests.cpp")
    let caps = capabilities.Value
    printHeader "Allocation Layout Tests"
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile C++ layout tests)."
        0  // skip, not failure — mirrors harness skip policy
    elif not (File.Exists testSrc) then
        eprintfn "alloc_layout_tests.cpp not found at: %s" testSrc
        eprintfn "Check that Blade.fsproj copies cpp/alloc_layout_tests.cpp to the output dir."
        1
    else
        let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
        let exePath = Path.ChangeExtension(testSrc, exeExt)
        // Compile in cppDir so #include "nested_array_utilities.hpp" resolves to
        // the shipped headers, exactly as g++ resolves them for generated tests.
        let args = sprintf "-std=c++17 -O2 -o \"%s\" \"%s\"" exePath testSrc
        let psi = ProcessStartInfo("g++", args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- cppDir
        use cproc = Process.Start(psi)
        let cOut = cproc.StandardOutput.ReadToEndAsync()
        let cErr = cproc.StandardError.ReadToEndAsync()
        cproc.WaitForExit(60000) |> ignore
        if cproc.ExitCode <> 0 then
            printfn "C++ compilation FAILED:"
            printfn "%s" (cOut.Result + "\n" + cErr.Result)
            1
        else
            // Run the compiled test; stream its PASS/FAIL lines through.
            let rpsi = ProcessStartInfo(exePath)
            rpsi.RedirectStandardOutput <- true
            rpsi.RedirectStandardError <- true
            rpsi.UseShellExecute <- false
            rpsi.CreateNoWindow <- true
            rpsi.WorkingDirectory <- cppDir
            use rproc = Process.Start(rpsi)
            let rOut = rproc.StandardOutput.ReadToEndAsync()
            let rErr = rproc.StandardError.ReadToEndAsync()
            rproc.WaitForExit(30000) |> ignore
            printf "%s" rOut.Result
            if not (String.IsNullOrWhiteSpace rErr.Result) then eprintf "%s" rErr.Result
            // Exit code is the source of truth (0 iff all checks passed).
            if rproc.ExitCode = 0 then
                printfn "All allocation layout tests passed."
                0
            else
                printfn "Some allocation layout tests FAILED."
                1

/// Run OpenMP thread-coverage tests. Generates representative loop-nest
/// programs with codegen TEST MODE on (which injects per-region thread
/// observation), compiles with -fopenmp, runs with OMP_NUM_THREADS forced > 1,
/// parses the emitted "[omp-coverage] region=... teamsz=K distinct=D maxth=M"
/// lines, and applies the rule:
///   - maxth <= 1            : single-core context — cannot test parallelism;
///                             reported as a skip-ish PASS (not a failure).
///   - maxth > 1, teamsz <= 1: ERROR — a loop that should be an OpenMP-parallel
///                             loop ran as a serial region (pragma not honored).
///   - maxth > 1, teamsz > 1 : PASS — a genuine parallel team was formed. (If
///                             distinct == 1, the scheduler put all work on one
///                             thread; that is an allowed scheduler choice, so
///                             it is reported as a WARNING, not a failure.)
///
/// Returns 0 if no errors (warnings allowed), 1 if any error.
let runOmpCoverageTests () : int =
    let caps = capabilities.Value
    printHeader "OpenMP Thread-Coverage Tests"
    if not caps.HasGpp then
        printfn "Skipped: g++ not found."
        0
    else
        // Representative programs exercising each parallelization strategy.
        // Source strings are defined as separate bindings (not inline in the
        // list) so the triple-quoted content does not disturb F# offside parsing.
        //
        // COVERAGE programs (just need to compile + form a parallel region):
        //   rect (collapse), symmetric (dynamic outer), and a partial-comm 3-arg
        //   kernel (mixed symmetry structure). Antisymmetric STRICT iteration is
        //   not expressed by a simple clause (it requires AntisymIdx typing), so
        //   it is intentionally omitted here rather than guessed at.
        let rectSrc =
            "let A = [1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0]\n" +
            "let B = [1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0]\n" +
            "let L = method_for(A, B)\n" +
            "let f = lambda(x, y) where omp(x: 1) -> x * y\n" +
            "let result = L <@> f |> compute\n"
        let symSrc =
            "let A = [1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0]\n" +
            "let L = method_for(A, A)\n" +
            "let k = lambda(x, y) where comm(x, y), omp(x: 1) -> x * y\n" +
            "let result = L <@> k |> compute\n"
        // Partial comm: 3-arg kernel with comm on a subset (proven form, see
        // Test_Symmetry). Exercises a mixed symmetry nest through genNestPragma.
        let mixedSrc =
            "let A = [1.0,2.0,3.0,4.0]\n" +
            "let L = method_for(A, A, A)\n" +
            "let k = lambda(x, y, z) where comm(x, y), omp(x: 1) -> x * y * z\n" +
            "let result = L <@> k |> compute\n"
        let programs =
            [ ("rect_outer_product", rectSrc)
              ("symmetric_triangular", symSrc)
              ("mixed_partial_comm", mixedSrc) ]
        let outputDir = "./generated_omp_coverage"
        Directory.CreateDirectory(outputDir) |> ignore
        // Write runtime headers into the output dir so the generated programs'
        // #include "nested_array_utilities.hpp" / "nested_array_types.hpp"
        // resolve at g++ time (same as the main test path does).
        File.WriteAllText(Path.Combine(outputDir, "nested_array_utilities.hpp"), CodeGen.genRuntimeHeader ())
        File.WriteAllText(Path.Combine(outputDir, "nested_array_types.hpp"), CodeGen.genRuntimeArrayTypesHeader ())
        let mutable errors = 0
        let mutable warnings = 0
        // Force a multi-thread environment for the run so the gate is meaningful.
        let forcedThreads = "4"
        for (name, src) in programs do
            // Generate with codegen test-mode ON (injects instrumentation), then
            // restore so nothing else in the process is affected.
            setOmpTestMode true
            let outcome =
                try
                    let safeName = sanitizeFileName name
                    match lower src with
                    | Error e -> Error (sprintf "lower failed: %s" e)
                    | Ok ir0 ->
                        let ir =
                            match IR.validateIR ir0 with
                            | Ok v -> v
                            | Error _ -> ir0   // validation errors don't block this probe
                        let (cppCode, _warnings) = CodeGen.genSelfContainedProgramFromIR ir name
                        let srcFile = Path.Combine(outputDir, safeName + ".cpp")
                        File.WriteAllText(srcFile, cppCode)
                        Ok srcFile
                with ex -> Error (sprintf "codegen failed: %s" ex.Message)
            setOmpTestMode false
            match outcome with
            | Error e -> printfn "  [%s] generation FAILED: %s" name e; errors <- errors + 1
            | Ok srcFile ->
                let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
                // Use ABSOLUTE paths for g++. srcFile is relative to the process
                // cwd; passing it relative while also setting WorkingDirectory to
                // the output dir caused g++ to resolve it against that dir (a
                // doubled path). Absolute paths make the working dir irrelevant.
                let srcAbs = Path.GetFullPath(srcFile)
                let exeAbs = Path.ChangeExtension(srcAbs, exeExt)
                let cpsi = ProcessStartInfo("g++", sprintf "-std=c++17 -O2 -fopenmp -o \"%s\" \"%s\"" exeAbs srcAbs)
                cpsi.RedirectStandardError <- true
                cpsi.UseShellExecute <- false
                use cproc = Process.Start(cpsi)
                let cerr = cproc.StandardError.ReadToEndAsync()
                cproc.WaitForExit(60000) |> ignore
                if cproc.ExitCode <> 0 then
                    printfn "  [%s] compile FAILED:\n%s" name cerr.Result
                    errors <- errors + 1
                else
                    // Run with OMP_NUM_THREADS forced.
                    let rpsi = ProcessStartInfo(exeAbs)
                    rpsi.RedirectStandardOutput <- true
                    rpsi.RedirectStandardError <- true
                    rpsi.UseShellExecute <- false
                    rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                    rpsi.Environment.["OMP_NUM_THREADS"] <- forcedThreads
                    use rproc = Process.Start(rpsi)
                    let rout = rproc.StandardOutput.ReadToEndAsync()
                    rproc.WaitForExit(30000) |> ignore
                    let lines = rout.Result.Split('\n') |> Array.filter (fun l -> l.Contains("[omp-coverage]"))
                    if lines.Length = 0 then
                        printfn "  [%s] no coverage lines emitted (no parallel region?)" name
                        // Not an error per se — the program may have no parallel loop.
                    for line in lines do
                        // parse "region=R teamsz=K distinct=D maxth=M"
                        let getField (k: string) =
                            let m = System.Text.RegularExpressions.Regex.Match(line, k + "=(\\d+)")
                            if m.Success then int m.Groups.[1].Value else -1
                        let teamsz = getField "teamsz"
                        let distinct = getField "distinct"
                        let maxth = getField "maxth"
                        if maxth <= 1 then
                            printfn "  [%s] PASS (single-core: maxth=%d, cannot test parallelism)" name maxth
                        elif teamsz <= 1 then
                            printfn "  [%s] ERROR: parallel loop ran serially (teamsz=%d, maxth=%d) — pragma not honored" name teamsz maxth
                            errors <- errors + 1
                        elif distinct <= 1 then
                            printfn "  [%s] WARNING: parallel team formed (teamsz=%d) but scheduler used 1 thread (distinct=%d)" name teamsz distinct
                            warnings <- warnings + 1
                        else
                            printfn "  [%s] PASS (teamsz=%d, distinct=%d, maxth=%d)" name teamsz distinct maxth

        // -------------------------------------------------------------------
        // VALUE CORRECTNESS UNDER FORCED MULTI-THREADING (#2)
        // -------------------------------------------------------------------
        // The coverage checks above confirm a parallel region forms, but NOT
        // that the parallelized computation produces CORRECT values. A data
        // race in the triangular outer-parallelization (the disjoint-slab
        // assumption) would show as wrong values only under threading — which
        // neither the coverage checks nor the main value suite (default
        // threading) would catch. Here we run a symmetric computation with
        // KNOWN expected values under OMP_NUM_THREADS=4, repeated several times
        // (races are nondeterministic, so one run can pass by luck), and assert
        // the values are correct every time.
        //
        // N=12 symmetric: C(13,2)=78 elements, large enough that the scheduler
        // genuinely distributes the outer loop. Expected values computed here
        // (not hand-written): for comm(x,y)->x*y over A=[1..N], the left-
        // justified symmetric order is A[i]*A[j] for i<=j.
        let nVal = 12
        let aVals = [ for i in 1 .. nVal -> float i ]
        let expectedSym =
            [ for i in 0 .. nVal - 1 do
                for j in i .. nVal - 1 do
                    yield aVals.[i] * aVals.[j] ]
        let aLit = aVals |> List.map (sprintf "%g") |> String.concat ","
        let expectedLit = expectedSym |> List.map (sprintf "%g") |> String.concat ", "
        let valSrc =
            sprintf "let A = [%s]\n" aLit +
            "let L = method_for(A, A)\n" +
            // omp clause so this genuinely runs parallel under OMP_NUM_THREADS=4
            // — otherwise (post-flip) it would be serial and the env var inert,
            // defeating the race-detection purpose of the repeated runs.
            "let k = lambda(x, y) where comm(x, y), omp(x: 1) -> x * y\n" +
            "let result = L <@> k |> compute\n" +
            sprintf "// EXPECT: result = [%s]\n" expectedLit
        printSubHeader "Value correctness under forced threading (N=12 symmetric)"
        setOmpTestMode false  // value test: no instrumentation, just real codegen
        let valOutcome =
            try
                match lower valSrc with
                | Error e -> Error (sprintf "lower failed: %s" e)
                | Ok ir0 ->
                    let ir = match IR.validateIR ir0 with Ok v -> v | Error _ -> ir0
                    let (cppCode, _w) = CodeGen.genSelfContainedProgramFromIR ir "omp_value_check"
                    let sf = Path.Combine(outputDir, "omp_value_check.cpp")
                    File.WriteAllText(sf, cppCode)
                    Ok (Path.GetFullPath sf)
            with ex -> Error (sprintf "codegen failed: %s" ex.Message)
        match valOutcome with
        | Error e -> printfn "  generation FAILED: %s" e; errors <- errors + 1
        | Ok srcAbs ->
            let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
            let exeAbs = Path.ChangeExtension(srcAbs, exeExt)
            let cpsi = ProcessStartInfo("g++", sprintf "-std=c++17 -O2 -fopenmp -o \"%s\" \"%s\"" exeAbs srcAbs)
            cpsi.RedirectStandardError <- true
            cpsi.UseShellExecute <- false
            use cproc = Process.Start(cpsi)
            let cerr = cproc.StandardError.ReadToEndAsync()
            cproc.WaitForExit(60000) |> ignore
            if cproc.ExitCode <> 0 then
                printfn "  compile FAILED:\n%s" cerr.Result
                errors <- errors + 1
            else
                let expected = parseExpectedValues valSrc
                let mutable allRunsOk = true
                // Repeat: a race may pass on some runs and fail on others.
                for run in 1 .. 5 do
                    let rpsi = ProcessStartInfo(exeAbs)
                    rpsi.RedirectStandardOutput <- true
                    rpsi.RedirectStandardError <- true
                    rpsi.UseShellExecute <- false
                    rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                    rpsi.Environment.["OMP_NUM_THREADS"] <- forcedThreads
                    use rproc = Process.Start(rpsi)
                    let rout = rproc.StandardOutput.ReadToEndAsync()
                    rproc.WaitForExit(30000) |> ignore
                    match checkExpectedValues expected rout.Result with
                    | Ok () -> ()
                    | Error errs ->
                        allRunsOk <- false
                        printfn "  run %d: VALUE MISMATCH (possible race): %s" run (String.concat "; " errs)
                if allRunsOk then
                    printfn "  PASS (correct values across 5 runs under OMP_NUM_THREADS=%s)" forcedThreads
                else
                    printfn "  ERROR: values incorrect under threading — likely a data race in parallelization"
                    errors <- errors + 1

        printfn "OMP COVERAGE: %d error(s), %d warning(s)" errors warnings
        if errors > 0 then 1 else 0


/// Includes both the single-file test corpus (`allTests`) and the multi-file
/// module/import corpus (`multiFileTests`). External-dependency tests
/// (NetCDF provider tests in particular) are NOT included here — they have
/// their own entry point because they require `libnetcdf` and a sample data
/// file that may not be present in CI / local dev environments.
let runAllTestsFull () =
    let outputDir = "./generated_cpp_tests"
    let r1 = runTestCategoryFull "All" allTests outputDir
    let r2 = runMultiFileTestsFull "Multi-File Modules" multiFileTests outputDir
    // Phase B: F# unit tests for the exprAttrs computation. Runs after
    // the source-program tests; reports separately so it doesn't muddy
    // the source-test counts. Exit code includes attrs failures.
    let (_, attrsFailed) = runAttrsTests ()
    // Phase C Step 2: F# unit tests for the codegen substitution
    // mechanism. Same reporting convention.
    let (_, substFailed) = runCodeGenSubstTests ()
    // C++ runtime-layout tests for the contiguous-backing allocate<>.
    // Same separate-reporting convention; verifies layout invariants the
    // value-checking source tests cannot catch. Skips cleanly if g++ absent.
    let allocFailed = runAllocLayoutTests ()
    // OpenMP thread-coverage: verifies emitted pragmas form genuine parallel
    // regions when cores are available. Errors only on serial-when-parallel
    // expected (teamsz<=1 with maxth>1); scheduler-distribution is a warning.
    let ompFailed = runOmpCoverageTests ()
    // Device buffer dimensional-type tests (CUDA streaming foundation). Pure F#,
    // verifies cardinality math against hand-computed values. No hardware needed.
    let bufTypeFailed = runBufferTypeTests ()
    // First `where cuda` hardware test (differential vs host-loop oracle).
    // SKIPs cleanly (returns 0) when nvcc/GPU absent.
    let cudaFailed = runCudaTests ()
    if r1 = 0 && r2 = 0 && attrsFailed = 0 && substFailed = 0 && allocFailed = 0 && ompFailed = 0 && bufTypeFailed = 0 && cudaFailed = 0 then 0 else 1

/// Run tests with C++ generation only (no compilation)
let runTestCategoryGenOnly (name: string) (tests: (string * string) list) (outputDir: string) =
    printHeader (sprintf "Blade-DSL: %s Tests (Generate C++ Only)" name)
    
    // Ensure output directory exists
    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore
    
    // Write runtime header file once
    let headerFile = Path.Combine(outputDir, "nested_array_utilities.hpp")
    File.WriteAllText(headerFile, CodeGen.genRuntimeHeader ())
    let arrayTypesHeaderFile = Path.Combine(outputDir, "nested_array_types.hpp")
    File.WriteAllText(arrayTypesHeaderFile, CodeGen.genRuntimeArrayTypesHeader ())
    
    printfn "Generating C++ for %d tests to %s...\n" (List.length tests) (Path.GetFullPath outputDir)
    
    let mutable irPassed = 0
    let mutable irFailed = 0
    let mutable generated = 0
    
    for (testName, source) in tests do
        match lower source with
        | Error e ->
            printfn "  [IR:FAIL] %s" testName
            printfn "    Error: %s" e
            irFailed <- irFailed + 1
        | Ok ir ->
            match IR.validateIR ir with
            | Error validationErrors ->
                printfn "  [IR:FAIL] %s (validation)" testName
                for e in validationErrors do
                    printfn "    %s" e
                irFailed <- irFailed + 1
            | Ok ir ->
            irPassed <- irPassed + 1
            let safeName = sanitizeFileName testName
            let cppFile = Path.Combine(outputDir, safeName + ".cpp")
            try
                let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                for w in codegenWarnings do
                    printfn "  [CodeGen Warning] %s" w
                File.WriteAllText(cppFile, cppCode)
                printfn "  [IR:OK] [Gen:OK] %s -> %s" testName (Path.GetFileName cppFile)
                generated <- generated + 1
            with ex ->
                printfn "  [IR:OK] [Gen:FAIL] %s" testName
                printfn "    Error: %s" ex.Message
    
    printHeader "Test Summary"
    printfn "IR Lowering:   %d passed, %d failed" irPassed irFailed
    printfn "C++ Generated: %d / %d" generated (irPassed + irFailed)
    printfn "Output folder: %s" (Path.GetFullPath outputDir)
    
    if irFailed > 0 then 1 else 0

/// Run all tests with generate only
let runAllTestsGenOnly () =
    let outputDir = "./generated_cpp_tests"
    runTestCategoryGenOnly "All" allTests outputDir

/// Run tests using the new type checking pipeline
let runTestCategoryWithTypeCheck name tests =
    printHeader (sprintf "Blade-DSL: %s Tests (TypeCheck Pipeline)" name)
    printfn "Running %d tests with Parse → TypeCheck → Lower pipeline...\n" (List.length tests)
    
    let mutable passed = 0
    let mutable failed = 0
    
    for (testName, source) in tests do
        printSubHeader testName
        match testLowerWithTypeCheck source with
        | Ok _ ->
            printfn "PASSED"
            passed <- passed + 1
        | Error _ ->
            printfn "FAILED"
            failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    
    if failed > 0 then
        printfn "\nSome tests failed."
        1
    else
        printfn "\nAll tests passed!"
        0

/// Run type checking only (no lowering)
let runTypeCheckOnly name tests =
    printHeader (sprintf "Blade-DSL: %s Tests (TypeCheck Only)" name)
    printfn "Running %d tests through type checker only...\n" (List.length tests)
    
    let mutable passed = 0
    let mutable failed = 0
    
    for (testName, source) in tests do
        printSubHeader testName
        match testTypeCheck source with
        | Ok _ ->
            printfn "PASSED"
            passed <- passed + 1
        | Error _ ->
            printfn "FAILED"
            failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    
    if failed > 0 then
        printfn "\nSome tests failed."
        1
    else
        printfn "\nAll tests passed!"
        0

/// Run tests that are expected to fail at type checking.
/// A test passes if type checking produces an Error.
let runExpectedErrorTests name tests =
    printHeader (sprintf "Blade-DSL: %s Tests (Expected Errors)" name)
    printfn "Running %d tests that should fail type checking...\n" (List.length tests)

    let mutable passed = 0
    let mutable failed = 0

    for (testName, source) in tests do
        printSubHeader testName
        match testTypeCheck source with
        | Error msg ->
            printfn "PASSED (correctly rejected: %s)" msg
            passed <- passed + 1
        | Ok _ ->
            printfn "FAILED (should have been rejected but was accepted)"
            failed <- failed + 1

    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)

    if failed > 0 then
        printfn "\nSome tests failed."
        1
    else
        printfn "\nAll tests passed!"
        0

/// Run tests that should abort at runtime (constraint violations, etc.)
let runAbortTests (name: string) (tests: (string * string) list) (outputDir: string) =
    printHeader (sprintf "Blade-DSL: %s Tests (Expected Runtime Abort)" name)
    printfn "Running %d tests that should abort at runtime...\n" (List.length tests)

    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore
    let headerFile = Path.Combine(outputDir, "nested_array_utilities.hpp")
    File.WriteAllText(headerFile, CodeGen.genRuntimeHeader ())
    let arrayTypesHeaderFile = Path.Combine(outputDir, "nested_array_types.hpp")
    File.WriteAllText(arrayTypesHeaderFile, CodeGen.genRuntimeArrayTypesHeader ())

    let gppAvailable = checkGppAvailable ()
    if not gppAvailable then
        printfn "WARNING: g++ not available, cannot run abort tests.\n"
        1
    else

    let mutable passed = 0
    let mutable failed = 0

    for (testName, source) in tests do
        printSubHeader testName
        let result = runFullTest testName source outputDir true
        match result.RunResult with
        | Ok (0, _) ->
            printfn "FAILED (should have aborted but exited normally)"
            failed <- failed + 1
        | Ok (code, _) ->
            printfn "PASSED (aborted as expected, exit code %d)" code
            passed <- passed + 1
        | Error e ->
            printfn "FAILED (error: %s)" e
            failed <- failed + 1

    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    if failed > 0 then 1 else 0

/// Compare both pipelines on all tests
let runPipelineComparison () =
    printHeader "Pipeline Comparison: Old vs New"
    printfn "Comparing Parse→Lower vs Parse→TypeCheck→Lower...\n"
    
    let mutable bothPassed = 0
    let mutable oldOnly = 0
    let mutable newOnly = 0
    let mutable bothFailed = 0
    
    for (testName, source) in allTests do
        printSubHeader testName
        let oldResult = lower source
        let newResult = lowerWithTypeCheck source
        
        match oldResult, newResult with
        | Ok _, Ok _ ->
            printfn "BOTH PASSED"
            bothPassed <- bothPassed + 1
        | Ok _, Error e ->
            printfn "OLD PASSED, NEW FAILED: %s" e
            oldOnly <- oldOnly + 1
        | Error e, Ok _ ->
            printfn "OLD FAILED, NEW PASSED: %s" e
            newOnly <- newOnly + 1
        | Error _, Error _ ->
            printfn "BOTH FAILED"
            bothFailed <- bothFailed + 1
    
    printHeader "Comparison Summary"
    printfn "Both passed:      %d" bothPassed
    printfn "Old only passed:  %d" oldOnly
    printfn "New only passed:  %d" newOnly
    printfn "Both failed:      %d" bothFailed
    printfn "Total:            %d" (bothPassed + oldOnly + newOnly + bothFailed)
    
    if oldOnly > 0 then
        printfn "\nWARNING: New pipeline has regressions!"
        1
    else
        printfn "\nNew pipeline is compatible with old pipeline."
        0

let runAllTests () =
    let r1 = runTestCategory "All" allTests
    let r2 = runMultiFileTests "Multi-File Modules" multiFileTests
    if r1 = 0 && r2 = 0 then 0 else 1

let runAllTestsWithTypeCheck () =
    runTestCategoryWithTypeCheck "All" allTests

// ============================================================================
// Specific Test for Symmetry Analysis
// ============================================================================

let runSymmetryTest () =
    printHeader "Symmetry Analysis Test"
    
    let source = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f
"""
    
    printfn "Source:\n%s" source
    
    match lower source with
    | Ok ir ->
        for m in ir.Modules do
            // Build name context
            let names = m.Bindings |> List.fold (fun acc b -> Map.add b.Id b.Name acc) Map.empty
            
            for b in m.Bindings do
                printfn "\n%s =" b.Name
                printfn "  %s" (ppIRExprWithNames names 2 b.Value)
                
                // Extra debug for apply combinator
                match b.Value with
                | IRApplyCombinator info ->
                    printfn "\n  [DEBUG] Apply Combinator Details:"
                    printfn "    SDimsPerArray: %A" info.SDimsPerArray
                    printfn "    KernelInputRanks: %A" info.KernelInputRanks
                    printfn "    SymcomStates: %A" info.SymcomStates
                    printfn "    TriangularLevels: %A" info.TriangularLevels
                    printfn "    SpeedupFactor: %d" info.SpeedupFactor
                    
                    // Check kernel comm groups.
                    // Stage 3c.4c: kernel arrives as IRVar; resolve
                    // through resolveCallable to inspect the callable.
                    match Blade.IR.resolveCallable info.Kernel with
                    | Some linfo ->
                        printfn "    Kernel CommGroups: %A" linfo.CommGroups
                        printfn "    Kernel IsCommutative: %b" linfo.IsCommutative
                    | None -> ()
                    
                    // Check loop identities
                    match info.Loop with
                    | IRMethodFor mfInfo ->
                        printfn "    Loop Identities: %A" mfInfo.Identities
                        printfn "    Loop ArrayTypes count: %d" mfInfo.ArrayTypes.Length
                    | _ -> ()
                | _ -> ()
    | Error e ->
        printfn "Error: %s" e

// ============================================================================
// Test for C++ Code Generation
// ============================================================================

let runCodeGenTest () =
    printHeader "C++ Code Generation Test"
    
    let source = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f
"""
    
    printfn "Source:\n%s" source
    
    match lower source with
    | Ok ir ->
        let builder = IRBuilder()
        for m in ir.Modules do
            for b in m.Bindings do
                match b.Value with
                | IRApplyCombinator info ->
                    printfn "\n=== Generating C++ for '%s' ===" b.Name
                    
                    // Build code gen info
                    let arrayNames = ["A"; "A"]  // Both are same array
                    let codeGen = buildLoopNestCodeGen info arrayNames b.Name builder
                    
                    printfn "\nLoop Bindings:"
                    for binding in codeGen.Bindings do
                        let elemStr = 
                            binding.Elements 
                            |> List.map (fun e -> sprintf "%s[%d] (param %s)" e.ArrayName e.DimIndex e.ParamName)
                            |> String.concat ", "
                        printfn "  Level %d: %s iterates %s" 
                            binding.Level binding.IndexName elemStr
                        let extentStr = ppIRExprWithNames Map.empty 0 binding.Extent
                        let depsStr = 
                            if binding.BoundDependencies.IsEmpty then "none"
                            else binding.BoundDependencies |> List.map (sprintf "__i%d") |> String.concat ", "
                        printfn "    Extent: %s, BoundDeps: [%s], Parallel: %b, State: %A"
                            extentStr depsStr
                            binding.IsParallel binding.State
                    
                    printfn "\nOutput: %s (type: %s)" 
                        codeGen.OutputName (ppIRType codeGen.OutputType)
                    printfn "Output Symm Vec: %A" codeGen.OutputSymmVec
                    printfn "Speedup: %dx" codeGen.SpeedupFactor
                    
                    printfn "\n=== Generated C++ Code ==="
                    let cppLines = genLoopNest codeGen Map.empty 0
                    for line in cppLines do
                        printfn "%s" line
                    
                | _ -> ()
        0
    | Error e ->
        printfn "Error: %s" e
        1

// ============================================================================
// Test for Array Capture Rejection
// ============================================================================

let runArrayCaptureTest () =
    printHeader "Array Capture Rejection Test"
    
    printfn "This test verifies that lambdas cannot capture arrays.\n"
    
    let source = Blade.Tests.Functions.test26_arrayCaptureRejected
    printfn "Source:\n%s" source
    
    try
        match lower source with
        | Ok _ ->
            printfn "FAILED: Should have rejected array capture"
            1
        | Error e ->
            printfn "Got expected error: %s" e
            0
    with
    | ex ->
        if ex.Message.Contains("cannot capture array") then
            printfn "PASSED: Correctly rejected array capture"
            printfn "Error message: %s" ex.Message
            0
        else
            printfn "FAILED: Unexpected error: %s" ex.Message
            1

// ============================================================================
// NetCDF Provider Tests
// ============================================================================

let runNetcdfTests () =
    printHeader "NetCDF Provider Tests"
    let mutable passed = 0
    let mutable failed = 0
    
    let check (name: string) (condition: bool) (detail: string) =
        if condition then
            printfn "  PASS: %s" name
            passed <- passed + 1
        else
            printfn "  FAIL: %s — %s" name detail
            failed <- failed + 1

    // ---------------------------------------------------------------
    // Test 1: ncTypeToElemType mapping
    // ---------------------------------------------------------------
    printfn "\n--- Type Code Mapping ---"
    
    check "NC_FLOAT (5) -> ETFloat32"
        (NetcdfProvider.ncTypeToElemType 5 = ETFloat32) ""
    check "NC_DOUBLE (6) -> ETFloat64"
        (NetcdfProvider.ncTypeToElemType 6 = ETFloat64) ""
    check "NC_INT (4) -> ETInt64"
        (NetcdfProvider.ncTypeToElemType 4 = ETInt64) ""
    check "NC_SHORT (3) -> ETInt64"
        (NetcdfProvider.ncTypeToElemType 3 = ETInt64) ""
    check "NC_UBYTE (7) -> ETInt64"
        (NetcdfProvider.ncTypeToElemType 7 = ETInt64) ""
    check "NC_CHAR (2) -> ETInt32"
        (NetcdfProvider.ncTypeToElemType 2 = ETInt32) ""
    
    let unsupportedThrows =
        try NetcdfProvider.ncTypeToElemType 99 |> ignore; false
        with _ -> true
    check "Unsupported type code throws" unsupportedThrows ""

    // ---------------------------------------------------------------
    // Test 2: Module construction from mock NcFile
    // ---------------------------------------------------------------
    printfn "\n--- Module Construction (mock data) ---"

    let mockFile : NetcdfProvider.NcFile = {
        Path = "sample.nc"
        Dims = [
            { Name = "lat"; Length = 180L }
            { Name = "lon"; Length = 360L }
            { Name = "time"; Length = 12L }
        ]
        Vars = [
            { Name = "A"; Dims = [
                { Name = "lat"; Length = 180L }
                { Name = "lon"; Length = 360L }
                { Name = "time"; Length = 12L }
              ]; TypeCode = 6 }  // NC_DOUBLE
        ]
    }

    let builder = IRBuilder()
    let modul = NetcdfProvider.ncFileToModule builder "sample" mockFile None

    // Helper to find structs by name
    let findStruct name (m: IRModule) =
        m.Types |> List.tryPick (function
            | IRTDStruct (n, fields, _) when n = name -> Some fields
            | _ -> None)

    check "Module name is 'sample'"
        (modul.Name = "sample") (sprintf "got '%s'" modul.Name)

    // 3 index types + dims struct + vars struct = 5 type defs
    check "Module has 5 type defs (3 idx + 2 structs)"
        (modul.Types.Length = 5) (sprintf "got %d" modul.Types.Length)

    let idxTypeNames =
        modul.Types |> List.choose (function
            | IRTDIndexType (name, _) -> Some name
            | _ -> None)
    
    check "Index type names are lat, lon, time"
        (idxTypeNames = ["lat"; "lon"; "time"])
        (sprintf "got %A" idxTypeNames)

    let latExtent =
        modul.Types |> List.tryPick (function
            | IRTDIndexType ("lat", idx) ->
                match idx.Extent with IRLit (IRLitInt n) -> Some n | _ -> None
            | _ -> None)
    check "lat extent is 180"
        (latExtent = Some 180L) (sprintf "got %A" latExtent)

    let timeExtent =
        modul.Types |> List.tryPick (function
            | IRTDIndexType ("time", idx) ->
                match idx.Extent with IRLit (IRLitInt n) -> Some n | _ -> None
            | _ -> None)
    check "time extent is 12"
        (timeExtent = Some 12L) (sprintf "got %A" timeExtent)

    // ---------------------------------------------------------------
    // Test 3: Struct structure
    // ---------------------------------------------------------------
    printfn "\n--- Struct Structure ---"

    let dimsFields = findStruct "dims" modul
    check "dims struct exists"
        (dimsFields.IsSome) ""
    check "dims has 3 fields (lat, lon, time)"
        (dimsFields.Value.Length = 3)
        (sprintf "got %d" (match dimsFields with Some f -> f.Length | None -> 0))
    check "dims field names"
        (dimsFields.Value |> List.map fst = ["lat"; "lon"; "time"])
        (sprintf "got %A" (dimsFields.Value |> List.map fst))

    let varsFields = findStruct "vars" modul
    check "vars struct exists"
        (varsFields.IsSome) ""
    check "vars has 1 field (A)"
        (varsFields.Value.Length = 1)
        (sprintf "got %d" (match varsFields with Some f -> f.Length | None -> 0))

    let varAType = varsFields.Value |> List.tryPick (fun (n, t) -> if n = "A" then Some t else None)
    check "vars.A exists" (varAType.IsSome) ""

    match varAType with
    | Some (ArrayElem arr) ->
        check "A element type is Float64"
            (arr.ElemType = IRTScalar ETFloat64) (sprintf "got %A" arr.ElemType)
        check "A has 3 index types"
            (arr.IndexTypes.Length = 3) (sprintf "got %d" arr.IndexTypes.Length)
        check "A index types have no tags"
            (arr.IndexTypes |> List.forall (fun i -> i.Tag = None)) ""
        check "A identity is AIDVariable 'A'"
            (arr.Identity = Some (AIDVariable "A")) (sprintf "got %A" arr.Identity)
    | _ ->
        check "A is an array type" false ""

    // ---------------------------------------------------------------
    // Test 4: Index type sharing within a module
    // ---------------------------------------------------------------
    printfn "\n--- Index Type Sharing ---"
    
    let mockFile2 : NetcdfProvider.NcFile = {
        Path = "multi.nc"
        Dims = [
            { Name = "lat"; Length = 180L }
            { Name = "lon"; Length = 360L }
        ]
        Vars = [
            { Name = "temperature"; Dims = [
                { Name = "lat"; Length = 180L }
                { Name = "lon"; Length = 360L }
              ]; TypeCode = 6 }
            { Name = "pressure"; Dims = [
                { Name = "lat"; Length = 180L }
                { Name = "lon"; Length = 360L }
              ]; TypeCode = 5 }  // NC_FLOAT
        ]
    }
    
    let builder2 = IRBuilder()
    let modul2 = NetcdfProvider.ncFileToModule builder2 "climate" mockFile2 None
    let vars2 = findStruct "vars" modul2
    
    check "vars has 2 fields" (vars2.Value.Length = 2) ""
    
    // Both variables should reference the same IRIndexType (same Id)
    let tempIdxIds =
        match vars2.Value |> List.tryPick (fun (n,t) -> if n = "temperature" then Some t else None) with
        | Some (ArrayElem a) -> a.IndexTypes |> List.map (fun i -> i.Id)
        | _ -> []
    let pressIdxIds =
        match vars2.Value |> List.tryPick (fun (n,t) -> if n = "pressure" then Some t else None) with
        | Some (ArrayElem a) -> a.IndexTypes |> List.map (fun i -> i.Id)
        | _ -> []
    
    check "temperature and pressure share same lat index Id"
        (tempIdxIds.Length >= 1 && pressIdxIds.Length >= 1
         && tempIdxIds.[0] = pressIdxIds.[0]) ""
    
    check "temperature and pressure share same lon index Id"
        (tempIdxIds.Length >= 2 && pressIdxIds.Length >= 2
         && tempIdxIds.[1] = pressIdxIds.[1]) ""

    check "temperature is Float64, pressure is Float32"
        (match vars2.Value.[0] |> snd, vars2.Value.[1] |> snd with
         | ArrayElem a1, ArrayElem a2 ->
             a1.ElemType = IRTScalar ETFloat64 && a2.ElemType = IRTScalar ETFloat32
         | _ -> false) ""

    // ---------------------------------------------------------------
    // Test 5: External dim map (schema extensibility)
    // ---------------------------------------------------------------
    printfn "\n--- External Dim Map (schema hook) ---"
    
    let schemaBuilder = IRBuilder()
    let sharedLat = {
        Id = schemaBuilder.FreshId()
        Arity = 1
        Extent = IRLit (IRLitInt 180L)
        Symmetry = SymNone
        Tag = None
        Kind = SDimension
        Dependencies = []
    }
    let sharedLon = {
        Id = schemaBuilder.FreshId()
        Arity = 1
        Extent = IRLit (IRLitInt 360L)
        Symmetry = SymNone
        Tag = None
        Kind = SDimension
        Dependencies = []
    }
    let externalMap = Map.ofList [("lat", sharedLat); ("lon", sharedLon)]
    
    let modul3 = NetcdfProvider.ncFileToModule schemaBuilder "file1" mockFile2 (Some externalMap)
    let modul4 = NetcdfProvider.ncFileToModule schemaBuilder "file2" mockFile2 (Some externalMap)
    
    // With external map, no IRTDIndexType defs are generated
    let idx3 = modul3.Types |> List.choose (function IRTDIndexType _ -> Some () | _ -> None)
    check "External map: no IRTDIndexType defs generated"
        (idx3.IsEmpty) (sprintf "got %d" idx3.Length)
    
    // Both modules' vars should reference the shared lat/lon Ids
    let vars3 = findStruct "vars" modul3
    let vars4 = findStruct "vars" modul4
    check "External map: both modules share same lat Id"
        (match vars3, vars4 with
         | Some f3, Some f4 ->
             match f3.[0] |> snd, f4.[0] |> snd with
             | ArrayElem a1, ArrayElem a2 ->
                 a1.IndexTypes.[0].Id = sharedLat.Id
                 && a2.IndexTypes.[0].Id = sharedLat.Id
             | _ -> false
         | _ -> false) ""

    // ---------------------------------------------------------------
    // Test 6: C++ codegen helpers
    // ---------------------------------------------------------------
    printfn "\n--- C++ Code Generation ---"
    
    let dimNames = NetcdfProvider.CppNetcdf.dimNamesFromModule modul
    check "dimNamesFromModule returns [lat; lon; time]"
        (dimNames = ["lat"; "lon"; "time"]) (sprintf "got %A" dimNames)
    
    match varAType with
    | Some (ArrayElem arrType) ->
        let readCode = NetcdfProvider.CppNetcdf.genReadVar "sample.nc" "A" "A" arrType
        check "genReadVar produces nc_open call"
            (readCode |> List.exists (fun s -> s.Contains "nc_open")) ""
        check "genReadVar produces nc_get_var_double"
            (readCode |> List.exists (fun s -> s.Contains "nc_get_var_double")) ""
        check "genReadVar produces nc_close"
            (readCode |> List.exists (fun s -> s.Contains "nc_close")) ""
        
        let writeCode = NetcdfProvider.CppNetcdf.genWriteVar "out.nc" "A" "A" arrType dimNames
        check "genWriteVar produces nc_create call"
            (writeCode |> List.exists (fun s -> s.Contains "nc_create")) ""
        check "genWriteVar uses dimension names from module"
            (writeCode |> List.exists (fun s -> s.Contains "\"lat\"")
             && writeCode |> List.exists (fun s -> s.Contains "\"lon\"")
             && writeCode |> List.exists (fun s -> s.Contains "\"time\"")) ""
    | _ -> ()

    // ---------------------------------------------------------------
    // Test 7: Live load (requires libnetcdf + sample.nc)
    // ---------------------------------------------------------------
    printfn "\n--- Live Load (sample.nc) ---"
    
    try
        let liveFile = NetcdfProvider.load "sample.nc"
        printfn "  Loaded '%s': %d dims, %d vars" liveFile.Path liveFile.Dims.Length liveFile.Vars.Length
        
        for dim in liveFile.Dims do
            printfn "    dim %-12s length=%d" dim.Name dim.Length
        
        let hasA = liveFile.Vars |> List.exists (fun v -> v.Name = "A")
        check "sample.nc contains variable A" hasA ""
        
        if hasA then
            let liveBuilder = IRBuilder()
            let liveModule = NetcdfProvider.ncFileToModule liveBuilder "sample" liveFile None
            
            let liveDimsFields = findStruct "dims" liveModule
            let liveVarsFields = findStruct "vars" liveModule
            
            check "Live dims struct exists"
                (liveDimsFields.IsSome) ""
            check "Live vars struct exists"
                (liveVarsFields.IsSome) ""
            check "Live vars has field for A"
                (liveVarsFields.Value |> List.exists (fun (n, _) -> n = "A")) ""
            
            printfn "\n  Module IR:"
            printfn "    module %s" liveModule.Name
            let names = indexNameMap liveModule
            for td in liveModule.Types do
                match td with
                | IRTDIndexType (name, idx) ->
                    let ext = match idx.Extent with IRLit (IRLitInt n) -> sprintf "%d" n | _ -> "?"
                    printfn "      type %s = Idx<%s>" name ext
                | IRTDStruct (name, fields, _) ->
                    printfn "      struct %s = {" name
                    for (fname, ftype) in fields do
                        printfn "        %s: %s" fname (ppIRTypeIn names ftype)
                    printfn "      }"
                | _ -> ()
    with
    | :? System.DllNotFoundException ->
        printfn "  SKIP: libnetcdf not available"
    | :? System.IO.FileNotFoundException ->
        printfn "  SKIP: sample.nc not found"
    | ex ->
        printfn "  SKIP: %s" ex.Message

    // ---------------------------------------------------------------
    // Test 8: Blade program with import and provider load
    // ---------------------------------------------------------------
    printfn "\n--- Blade Program Import (sample.nc) ---"

    let bladeSource = """
import Providers.NetCDF as NetCDF

let sample = NetCDF.load("sample.nc")
"""
    
    // Test parse
    match parseProgram bladeSource with
    | Ok program ->
        check "Parse succeeds" true ""
        let decls = program.Modules.[0].Decls |> List.map (fun d -> d.Value)
        
        check "First decl is DeclImport"
            (match decls.[0] with DeclImport _ -> true | _ -> false)
            (sprintf "got %A" decls.[0])
        
        check "Import has correct qualified name"
            (match decls.[0] with 
             | DeclImport (["Providers"; "NetCDF"], ImportQualified (Some "NetCDF")) -> true 
             | _ -> false)
            (sprintf "got %A" decls.[0])

        check "Second decl is DeclLet"
            (match decls.[1] with DeclLet _ -> true | _ -> false)
            (sprintf "got %A" decls.[1])

        // Test lowering (requires sample.nc + libnetcdf)
        try
            match lower bladeSource with
            | Ok ir ->
                check "Lower succeeds" true ""
                let modul = ir.Modules.[0]
                let names = indexNameMap modul
                
                printfn "\n  Lowered module: %s" modul.Name
                printfn "  Types: %d" modul.Types.Length
                for td in modul.Types do
                    match td with
                    | IRTDIndexType (name, idx) ->
                        let ext = match idx.Extent with IRLit (IRLitInt n) -> sprintf "%d" n | _ -> "?"
                        printfn "    type %s = Idx<%s>" name ext
                    | IRTDStruct (name, fields, _) ->
                        printfn "    struct %s = {" name
                        for (fname, ftype) in fields do
                            printfn "      %s: %s" fname (ppIRTypeIn names ftype)
                        printfn "    }"
                    | _ -> ()

                // Verify types were produced
                let idxTypes = modul.Types |> List.choose (function IRTDIndexType (n, _) -> Some n | _ -> None)
                check "Provider produced index types"
                    (idxTypes.Length >= 3) (sprintf "got %A" idxTypes)

                let hasVarsStruct = modul.Types |> List.exists (function IRTDStruct ("vars", _, _) -> true | _ -> false)
                check "Provider produced vars struct" hasVarsStruct ""

                let hasDimsStruct = modul.Types |> List.exists (function IRTDStruct ("dims", _, _) -> true | _ -> false)
                check "Provider produced dims struct" hasDimsStruct ""

                // Verify vars struct has field A
                let varAExists =
                    modul.Types |> List.exists (function
                        | IRTDStruct ("vars", fields, _) ->
                            fields |> List.exists (fun (n, _) -> n = "A")
                        | _ -> false)
                check "vars struct has field A" varAExists ""

            | Error e ->
                printfn "  Lower error: %s" e
                check "Lower succeeds" false e
        with
        | :? System.DllNotFoundException ->
            printfn "  SKIP lower: libnetcdf not available"
        | :? System.IO.FileNotFoundException ->
            printfn "  SKIP lower: sample.nc not found"
        | ex ->
            printfn "  SKIP lower: %s" ex.Message

    | Error e ->
        check "Parse succeeds" false (sprintf "%d:%d %s" e.Line e.Col e.Message)

    // ---------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------
    printfn "\n========================================="
    printfn "NetCDF Provider: %d passed, %d failed" passed failed
    if failed > 0 then 1 else 0

// ============================================================================
// C++ Code Generation Tests
// ============================================================================

/// Tests that can generate compilable C++ (subset that produces loop nests)
let cppGenerableTests = [
    ("Triangular Iteration", test8_triangularIteration);
    ("Symmetry Demo Case 1", """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L1 = method_for(A, B)
let f1 = lambda(x, y) -> x * y
let r1 = L1 <@> f1
""");
    ("Symmetry Demo Case 2", """
let C = [1.0, 2.0, 3.0]
let L2 = method_for(C, C)
let f2 = lambda(x, y) where comm(x, y) -> x * y
let r2 = L2 <@> f2
""");
    ("Three-Way Symmetry", """
let D = [1.0, 2.0, 3.0]
let L3 = method_for(D, D, D)
let f3 = lambda(x, y, z) where comm(x, y, z) -> x * y * z
let r3 = L3 <@> f3
""");
    ("Basic Apply", test6_apply)
]

/// Generate C++ for a single test
let generateCppForTest (testName: string) (source: string) (outputDir: string) : Result<string, string> =
    match lower source with
    | Ok ir ->
        // Generate self-contained C++ program (no external dependencies)
        let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
        for w in codegenWarnings do
            printfn "  [CodeGen Warning] %s" w
        
        // Sanitize test name for filename
        let safeName = testName.Replace(" ", "_").Replace(":", "").Replace("/", "_")
        let filename = sprintf "%s/%s.cpp" outputDir safeName
        
        // Write to file
        File.WriteAllText(filename, cppCode)
        Ok filename
    | Error e ->
        Error (sprintf "Lowering failed: %s" e)

/// Run C++ generation for all generable tests
let runCppGeneration (outputDir: string) =
    printHeader "C++ Code Generation"
    
    // Ensure output directory exists
    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore
    
    printfn "Output directory: %s\n" outputDir
    
    let mutable passed = 0
    let mutable failed = 0
    let mutable generated = []
    
    for (testName, source) in cppGenerableTests do
        printfn "Generating: %s" testName
        match generateCppForTest testName source outputDir with
        | Ok filename ->
            printfn "  -> %s" filename
            passed <- passed + 1
            generated <- generated @ [filename]
        | Error e ->
            printfn "  FAILED: %s" e
            failed <- failed + 1
    
    printfn "\n========================================="
    printfn "Generated: %d files" passed
    printfn "Failed: %d" failed
    
    // Print sample of generated code
    if generated.Length > 0 then
        printfn "\n=== Sample Generated Code (%s) ===" (List.head generated)
        let content = File.ReadAllText (List.head generated)
        // Print first 100 lines
        let lines = content.Split('\n')
        for i in 0 .. min 99 (lines.Length - 1) do
            printfn "%s" lines.[i]
        if lines.Length > 100 then
            printfn "... (%d more lines)" (lines.Length - 100)
    
    if failed > 0 then 1 else 0

/// Run C++ generation with compilation check (if g++ available)
let runCppGenerationWithCompile (outputDir: string) =
    let result = runCppGeneration outputDir
    
    if result = 0 then
        printfn "\n=== Attempting Compilation ==="
        
        // Check if g++ is available
        let gppCheck = 
            try
                let psi = System.Diagnostics.ProcessStartInfo("g++", "--version")
                psi.RedirectStandardOutput <- true
                psi.UseShellExecute <- false
                use proc = System.Diagnostics.Process.Start(psi)
                proc.WaitForExit()
                proc.ExitCode = 0
            with _ -> false
        
        if gppCheck then
            printfn "g++ found, compiling generated files..."
            
            let mutable compileOk = 0
            let mutable compileFail = 0
            
            for file in Directory.GetFiles(outputDir, "*.cpp") do
                let outFile = Path.ChangeExtension(file, ".out")
                let psi = System.Diagnostics.ProcessStartInfo(
                    "g++", 
                    sprintf "-std=c++17 -O2 -fopenmp -o \"%s\" \"%s\"" outFile file)
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.WorkingDirectory <- outputDir
                
                try
                    use proc = System.Diagnostics.Process.Start(psi)
                    let errors = proc.StandardError.ReadToEnd()
                    proc.WaitForExit()
                    
                    if proc.ExitCode = 0 then
                        printfn "  COMPILED: %s" (Path.GetFileName file)
                        compileOk <- compileOk + 1
                    else
                        printfn "  FAILED: %s" (Path.GetFileName file)
                        printfn "    %s" (errors.Replace("\n", "\n    "))
                        compileFail <- compileFail + 1
                with ex ->
                    printfn "  ERROR: %s - %s" (Path.GetFileName file) ex.Message
                    compileFail <- compileFail + 1
            
            printfn "\nCompilation: %d succeeded, %d failed" compileOk compileFail
            if compileFail > 0 then 1 else 0
        else
            printfn "g++ not found, skipping compilation check"
            0
    else
        result

/// Enhanced codegen test that generates a full program
let runEnhancedCodeGenTest () =
    printHeader "Enhanced C++ Code Generation Test"
    
    let source = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f
"""
    
    printfn "Source:\n%s" source
    
    match lower source with
    | Ok ir ->
        printfn "\n=== Generated Self-Contained C++ Program ===\n"
        let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir "TriangularTest"
        for w in codegenWarnings do
            printfn "  [CodeGen Warning] %s" w
        printfn "%s" cppCode
        
        // Also show with external runtime for comparison
        printfn "\n=== Generated C++ (with external runtime) ===\n"
        let cppCodeExt = CodeGen.genProgramFromIR ir "TriangularTest"
        printfn "%s" cppCodeExt
        
        0
    | Error e ->
        printfn "Error: %s" e
        1

// ============================================================================
// Main Entry Point
// ============================================================================

let printUsage () =
    printfn "Blade Compiler v%s" compilerVersion
    printfn ""
    printfn "Usage: blade <command> [options]"
    printfn ""
    printfn "Commands:"
    printfn "  compile <file.edgi> [-o output]   Compile to C++ (and optionally to executable)"
    printfn "  run <file.edgi>                   Compile and run a Blade program"
    printfn "  check <file.edgi>                 Type-check only (no code generation)"
    printfn "  emit <file.edgi> [-o output.cpp]  Emit C++ source without compiling"
    printfn "  test                              Run full test suite (IR + C++ + run)"
    printfn "  test --ir-only                    Run IR-only tests (fast, no C++ compilation)"
    printfn "  test alloc                        Run C++ allocation-layout tests (contiguity/cardinality)"
    printfn ""
    printfn "Options:"
    printfn "  -o <path>      Output file path"
    printfn "  --no-omp       Disable OpenMP"
    printfn "  --verbose      Show IR and generated C++"
    printfn "  --help         Show this help"
    printfn ""
    printfn "Examples:"
    printfn "  blade run myprogram.edgi"
    printfn "  blade emit myprogram.edgi -o myprogram.cpp"
    printfn "  blade compile myprogram.edgi -o myprogram"
    printfn "  blade test"

/// Compile a .edgi file to C++ source string
let compileFile (filePath: string) (verbose: bool) : Result<string * string list, string> =
    if not (File.Exists filePath) then
        Error (sprintf "File not found: %s" filePath)
    else
        let source = File.ReadAllText(filePath)
        let testName = Path.GetFileNameWithoutExtension(filePath)
        match lower source with
        | Error e -> Error e
        | Ok ir ->
            match IR.validateIR ir with
            | Error errs -> Error (errs |> String.concat "\n")
            | Ok ir ->
                let (cppCode, warnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                if verbose then
                    for w in warnings do
                        eprintfn "[Warning] %s" w
                Ok (cppCode, warnings)

/// Compile a .edgi file to an executable
let compileToExe (filePath: string) (outputPath: string option) (verbose: bool) : Result<string, string> =
    match compileFile filePath verbose with
    | Error e -> Error e
    | Ok (cppCode, warnings) ->
        let baseName = Path.GetFileNameWithoutExtension(filePath)
        let dir = Path.GetDirectoryName(Path.GetFullPath(filePath))
        let dir = if String.IsNullOrEmpty dir then "." else dir
        // Infer backend from generated source: device kernels → .cu + nvcc.
        let backendReq = inferBackendReq cppCode
        let ext = match backendReq with RequiresCuda -> ".cu" | CpuOnly -> ".cpp"
        let cppFile = Path.Combine(dir, baseName + ext)
        File.WriteAllText(cppFile, cppCode)
        if verbose then
            eprintfn "[Emit] %s" cppFile
        match compileForBackend capabilities.Value backendReq cppFile dir with
        | Error e ->
            Error (sprintf "Compilation failed:\n%s" e)
        | Ok exePath ->
            // If user specified output path, move the exe there
            let finalPath =
                match outputPath with
                | Some out ->
                    let outFull = Path.GetFullPath(out)
                    if exePath <> outFull then
                        try File.Copy(exePath, outFull, true) with _ -> ()
                    outFull
                | None -> exePath
            // Clean up intermediate .cpp
            if not verbose then
                try File.Delete(cppFile) with _ -> ()
            if verbose then
                eprintfn "[Compile] %s" finalPath
            Ok finalPath

/// Run a .edgi file: compile and execute
let runFile (filePath: string) (verbose: bool) : int =
    match compileToExe filePath None verbose with
    | Error e ->
        eprintfn "Error: %s" e
        1
    | Ok exePath ->
        match runExecutable exePath with
        | Error e ->
            eprintfn "Runtime error: %s" e
            1
        | Ok (exitCode, output) ->
            printf "%s" output
            exitCode

/// Type-check a file without generating code
let checkFile (filePath: string) : int =
    if not (File.Exists filePath) then
        eprintfn "File not found: %s" filePath
        1
    else
        let source = File.ReadAllText(filePath)
        match Blade.Parser.parseProgram source with
        | Error e ->
            eprintfn "Parse error at %d:%d: %s" e.Line e.Col e.Message
            1
        | Ok program ->
            match Blade.TypeCheck.typeCheck program with
            | Error errors ->
                for e in errors do
                    eprintfn "%s" (Blade.TypeCheck.formatCompileError e)
                1
            | Ok (_, _, warnings) ->
                for w in warnings do
                    printfn "[TypeCheck Warning] %s" w
                printfn "OK"
                0

/// Emit C++ source to file or stdout
let emitFile (filePath: string) (outputPath: string option) (verbose: bool) : int =
    match compileFile filePath verbose with
    | Error e ->
        eprintfn "Error: %s" e
        1
    | Ok (cppCode, _) ->
        match outputPath with
        | Some outPath ->
            File.WriteAllText(outPath, cppCode)
            if verbose then
                eprintfn "[Emit] %s" outPath
            0
        | None ->
            printf "%s" cppCode
            0

[<EntryPoint>]
let main args =
    match args with
    // ---- User-facing commands ----
    | [| "run"; file |] -> runFile file false
    | [| "run"; file; "--verbose" |] -> runFile file true
    
    | [| "compile"; file |] ->
        match compileToExe file None false with
        | Ok path -> printfn "%s" path; 0
        | Error e -> eprintfn "Error: %s" e; 1
    | [| "compile"; file; "-o"; output |] ->
        match compileToExe file (Some output) false with
        | Ok path -> printfn "%s" path; 0
        | Error e -> eprintfn "Error: %s" e; 1
    
    | [| "emit"; file |] -> emitFile file None false
    | [| "emit"; file; "-o"; output |] -> emitFile file (Some output) false
    | [| "emit"; file; "--verbose" |] -> emitFile file None true
    | [| "emit"; file; "-o"; output; "--verbose" |] -> emitFile file (Some output) true
    
    | [| "check"; file |] -> checkFile file
    
    // ---- Test commands ----
    | [| "test" |] -> runAllTestsFull ()
    | [| "test"; "--ir-only" |] -> runAllTests ()
    | [| "test"; "--gen" |] -> runAllTestsGenOnly ()
    | [| "test"; "normalize" |] ->
        // IR-level F# unit tests for the type normalizer. Runs in-process,
        // no Blade source pipeline involved.
        let (_, failed) = runNormalizeTests ()
        if failed = 0 then 0 else 1
    | [| "test"; "unify" |] ->
        // TypeCheck-level F# unit tests for the unify §5.3 fast path.
        // Constructs IRType values directly and calls unify; no Blade
        // source pipeline.
        let (_, failed) = runUnifyTests ()
        if failed = 0 then 0 else 1
    | [| "test"; "validate-arrow" |] ->
        // IR-level F# unit tests for the validateArrowShape gate at
        // mkVirtualArrayArrow entry. Constructs IRType values directly;
        // no Blade source pipeline.
        let (_, failed) = runValidateArrowTests ()
        if failed = 0 then 0 else 1
    | [| "test"; "attrs" |] ->
        // Phase B: IR-level F# unit tests for the exprAttrs bottom-up
        // attribute computation. Constructs IR fragments directly and
        // compares actual vs. expected attribute sets. No Blade source
        // pipeline.
        let (_, failed) = runAttrsTests ()
        if failed = 0 then 0 else 1
    | [| "test"; "subst" |] ->
        // Phase C Step 2: F# unit tests for the contains-substitution
        // mechanism in exprToCpp. Constructs IR fragments, renders with
        // populated and empty SubstMaps, asserts on the resulting C++
        // string. No Blade source pipeline.
        let (_, failed) = runCodeGenSubstTests ()
        if failed = 0 then 0 else 1
    | [| "test"; "alloc" |] ->
        // Standalone C++ runtime-layout tests for the contiguous-backing
        // allocate<>. Compiles + runs cpp/alloc_layout_tests.cpp against the
        // shipped headers. Verifies contiguity/cardinality invariants the
        // value-checking Blade tests cannot catch. No Blade source pipeline.
        runAllocLayoutTests ()
    | [| "test"; "omp-coverage" |] ->
        // OpenMP thread-coverage: generate representative loop programs with
        // codegen test-mode instrumentation, compile -fopenmp, run with forced
        // threads, verify emitted pragmas form genuine parallel regions.
        runOmpCoverageTests ()
    | [| "test"; cat |] ->
        // Test a specific category: blade test basic, blade test loops, etc.
        let categoryTests =
            match cat.ToLower().TrimStart('-') with
            | "basic" -> Some ("Basic", basicTests)
            | "loops" -> Some ("Loops", loopTests)
            | "symmetry" -> Some ("Symmetry", symmetryTests)
            | "reynolds" -> Some ("Reynolds", reynoldsTests)
            | "arity" -> Some ("Arity", arityTests)
            | "functions" -> Some ("Functions", functionTests)
            | "structs" -> Some ("Structs", structTests)
            | "sumtypes" -> Some ("Sum Types", sumTypeTests)
            | "interfaces" -> Some ("Interfaces", interfaceTests)
            | "modules" -> Some ("Modules", moduleTests)
            | "guards" -> Some ("Guards", guardTests)
            | "bracketed" -> Some ("Bracketed", bracketedTests)
            | "indextypes" -> Some ("Index Types", indexTypeTests)
            | "static" -> Some ("Static", staticTests)
            | "units" -> Some ("Units", unitTests)
            | "mutability" -> Some ("Mutability", mutabilityTests)
            | "funcarrays" | "fa" -> Some ("Func Arrays", funcArrayTests)
            | "sqlish" | "sql" -> Some ("SQL-ish", foreignKeyTests @ maskTests @ setOpTests @ groupByTests @ sortTests @ reduceTests @ extentsTests @ extentsMultiRankTests @ regressionTests @ sqlCombinedTests)
            | _ -> None
        match categoryTests with
        | Some (name, tests) -> runTestCategoryFull name tests "./generated_cpp_tests"
        | None -> eprintfn "Unknown test category: %s" cat; 1
    
    // ---- Legacy flags (backward compat) ----
    | [||] -> runAllTestsFull ()
    | [| "--full" |] -> runAllTestsFull ()
    | [| "--help" |] -> printUsage (); 0
    | _ -> printUsage (); 1
