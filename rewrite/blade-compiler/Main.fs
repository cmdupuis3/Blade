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
    | FpCppGenerated of IRProgram * string * string list  // ir, cppFile, codegenWarnings
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
                    let cppFile = Path.Combine(outputDir, safeName + ".cpp")
                    try
                        let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                        File.WriteAllText(cppFile, cppCode)
                        FpCppGenerated (ir, cppFile, codegenWarnings)
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
    | FpCppGenerated (ir, cppFile, codegenWarnings) ->
        for w in codegenWarnings do
            printfn "  [CodeGen Warning] %s" w

        // Step 3: Compile (outside lock — separate subprocess)
        let compileResult = compileCpp cppFile outputDir

        match compileResult with
        | Error e ->
            { TestName = testName; IRResult = Ok ir; CppGenerated = true;
              CppFile = Some cppFile; CompileResult = Error e; RunResult = Error "Compile failed";
              ValueCheckResult = Error ["Compile failed"]; HasExpectedValues = not expectedValues.IsEmpty }
        | Ok exeFile ->
            // Step 4: Run (outside lock)
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
              CppFile = Some cppFile; CompileResult = Ok exeFile; RunResult = runResult;
              ValueCheckResult = valueCheckResult; HasExpectedValues = not expectedValues.IsEmpty }

/// Print a full test result
let printFullTestResult (result: FullTestResult) (verbose: bool) (showFullError: bool) =
    let irStatus = match result.IRResult with Ok _ -> "OK" | Error _ -> "FAIL"
    let cppStatus = if result.CppGenerated then "OK" else "SKIP"
    let compileStatus = match result.CompileResult with Ok _ -> "OK" | Error "Skipped" -> "SKIP" | Error _ -> "FAIL"
    let runStatus = 
        match result.RunResult with 
        | Ok (0, _) -> "OK" 
        | Ok (code, _) -> sprintf "EXIT(%d)" code
        | Error "Skipped" -> "SKIP"
        | Error _ -> "FAIL"
    let valueStatus =
        if not result.HasExpectedValues then ""
        else match result.ValueCheckResult with
             | Ok () -> "OK"
             | Error _ -> "FAIL"
    
    // Only show value status if there were expected values to check
    let valueDisplay = if valueStatus = "" then "" else sprintf " [Val:%s]" valueStatus
    
    printfn "  [IR:%s] [Gen:%s] [Compile:%s] [Run:%s]%s %s" irStatus cppStatus compileStatus runStatus valueDisplay result.TestName
    
    if verbose then
        match result.IRResult with
        | Error e -> printfn "    IR Error: %s" e
        | Ok _ -> ()
        
        match result.CompileResult with
        | Error e when e <> "Skipped" && e <> "IR failed" -> 
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
        | Error e when e <> "Skipped" && e <> "IR failed" && e <> "Compile failed" -> 
            printfn "    Run Error: %s" e
        | _ -> ()
        
        // Show value check errors
        match result.ValueCheckResult with
        | Error errors when not (List.contains "Skipped" errors) && 
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
            let cppFile = Path.Combine(outputDir, safeName + ".cpp")
            try
                let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                for w in codegenWarnings do
                    printfn "  [CodeGen Warning] %s" w
                File.WriteAllText(cppFile, cppCode)
                printfn "Generated: %s" cppFile
                
                if gppAvailable then
                    match compileCpp cppFile outputDir with
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
            let status = match result.CompileResult with Ok _ -> "ok" | Error e when e = "IR failed" -> "IR fail" | Error _ -> "compile fail"
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
    
    // Find first compile failure to show full error
    let firstCompileFailure = 
        results |> List.tryFind (fun r -> 
            match r.CompileResult with 
            | Error e when e <> "Skipped" && e <> "IR failed" -> true 
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
    
    // Count value check results (only for tests that have expected values)
    let testsWithExpected = results |> List.filter (fun r -> r.HasExpectedValues)
    let valuesPassed = testsWithExpected |> List.filter (fun r -> 
        match r.ValueCheckResult with Ok () -> true | _ -> false) |> List.length
    
    printHeader "Test Summary"
    printfn "IR Lowering:  %d passed, %d failed" irPassed irFailed
    printfn "C++ Generated: %d / %d" generated results.Length
    if gppAvailable then
        printfn "Compiled:     %d / %d" compiled results.Length
        printfn "Full Pass:    %d / %d (IR + Compile + Run)" fullPassed results.Length
        if testsWithExpected.Length > 0 then
            printfn "Value Check:  %d / %d" valuesPassed testsWithExpected.Length
    else
        printfn "Generated files in: %s" (Path.GetFullPath outputDir)
    printfn "Total Tests:  %d" results.Length
    
    if irFailed > 0 then 1 else 0

/// Run all tests with full C++ pipeline.
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
    if r1 = 0 && r2 = 0 && attrsFailed = 0 && substFailed = 0 then 0 else 1

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
                    
                    // Check kernel comm groups
                    match info.Kernel with
                    | IRLambda linfo ->
                        printfn "    Kernel CommGroups: %A" linfo.CommGroups
                        printfn "    Kernel IsCommutative: %b" linfo.IsCommutative
                    | _ -> ()
                    
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
        let cppFile = Path.Combine(dir, baseName + ".cpp")
        File.WriteAllText(cppFile, cppCode)
        if verbose then
            eprintfn "[Emit] %s" cppFile
        match compileCpp cppFile dir with
        | Error e ->
            Error (sprintf "C++ compilation failed:\n%s" e)
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
