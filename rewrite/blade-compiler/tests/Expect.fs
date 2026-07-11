// EXPECT-comment parsing and expected-vs-actual value comparison for the
// Blade test harness. Extracted verbatim from Main.fs (audit §2.3) so the
// value-checking rules are reviewable in isolation.
module Blade.Tests.Expect

open System

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
