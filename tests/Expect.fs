// EXPECT-comment parsing and expected-vs-actual value comparison for the
// Blade test harness. Extracted verbatim from Main.fs (audit §2.3) so the
// value-checking rules are reviewable in isolation.
module Blade.Tests.Expect

open System

// ============================================================================
// Value Checking Infrastructure
// ============================================================================

/// Where a "(rejects)" probe is required to be rejected.
///
/// A reject-probe asserts that the compiler REFUSES a program. Without a
/// pinned stage, "refused" degenerates into "something, somewhere, went
/// wrong" — a dead toolchain, garbage generated C++, or a runtime crash all
/// look identical to a deliberate rejection, and the probe reports green
/// while nothing was actually verified. Pinning the stage makes the probe
/// assert a specific compiler behaviour instead of mere failure:
///
///   RejectAtLower   — parse / typecheck / IR lowering must reject it.
///   RejectAtCodegen — lowering succeeds and codegen deliberately emits a
///                     `#error` guard, which the C++ compiler then hits.
///
/// Corpus syntax (one line, anywhere in the file):
///   // REJECT-AT: lower
///   // REJECT-AT: codegen
type RejectStage =
    | RejectAtLower
    | RejectAtCodegen

/// Read the `// REJECT-AT:` directive out of a test source.
///
/// The directive is optional: the overwhelming majority of reject-probes are
/// rejected during lowering, so an ABSENT directive means RejectAtLower. An
/// unrecognized value is NOT defaulted — silently reading `// REJECT-AT: lowr`
/// as "lower" would reintroduce exactly the class of bug the directive exists
/// to close, so a bad value is a corpus authoring error and fails loudly.
/// Repeated directives must agree, for the same reason.
let parseRejectStage (source: string) : RejectStage =
    let stages =
        source.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            let trimmed = line.Trim()
            if trimmed.StartsWith("// REJECT-AT:") then
                Some (trimmed.Substring(13).Trim())
            else None)
        |> Array.toList
    match stages |> List.distinct with
    | [] -> RejectAtLower
    | [ one ] ->
        match one.ToLowerInvariant() with
        | "lower" -> RejectAtLower
        | "codegen" -> RejectAtCodegen
        | other ->
            failwithf "// REJECT-AT: '%s' is not a known reject stage (expected 'lower' or 'codegen')" other
    | many ->
        failwithf "conflicting // REJECT-AT: directives in one test: %s" (String.concat ", " many)

/// Expected value for a variable
type ExpectedValue =
    | ExpectedScalar of string * float
    | ExpectedBool of string * bool
    | ExpectedArray1D of string * float list
    | ExpectedArray1DBool of string * bool list
    | ExpectedArray2D of string * float list list
    | ExpectedComplex of string * float * float
    | ExpectedArray1DComplex of string * (float * float) list
    | ExpectedString of string * string
    | ExpectedArray1DString of string * string list

/// Split the BODY of a bracketed list at the commas that sit at nesting
/// depth zero, so `[0, 1], [20, 21]` yields the two row texts rather than
/// four element texts. Depth counts `[` and `(` alike, and commas inside a
/// quoted region are ignored, so this is safe for every list flavour the
/// EXPECT grammar admits.
let private splitTopLevelCommas (inner: string) : string list =
    let parts = ResizeArray<string>()
    let cur = System.Text.StringBuilder()
    let mutable depth = 0
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
        elif inQuotes then
            cur.Append(c) |> ignore
        else
            match c with
            | '[' | '(' ->
                depth <- depth + 1
                cur.Append(c) |> ignore
            | ']' | ')' ->
                depth <- depth - 1
                cur.Append(c) |> ignore
            | ',' when depth = 0 ->
                parts.Add(cur.ToString())
                cur.Clear() |> ignore
            | _ -> cur.Append(c) |> ignore
    if cur.Length > 0 then parts.Add(cur.ToString())
    parts |> List.ofSeq

/// Parse a bracketed float list `[1.0, 2.0, 3.0]`.
///
/// Returns None — rather than substituting a value — when ANY element fails
/// to parse. The previous version mapped an unparseable element to 0.0, which
/// turned a typo into a silent expectation of zero: `// EXPECT: v = [1, x, 3]`
/// became "the middle element must be 0". A pin either parses in full or is
/// reported as malformed; there is no third, quietly-weakened state.
let private tryParseFloatList (s: string) : float list option =
    let t = s.Trim()
    if not (t.StartsWith("[") && t.EndsWith("]")) then None
    else
        let inner = t.Substring(1, t.Length - 2).Trim()
        if String.IsNullOrWhiteSpace(inner) then Some []
        else
            let parsed =
                splitTopLevelCommas inner
                |> List.map (fun x ->
                    match Double.TryParse(x.Trim()) with
                    | true, v -> Some v
                    | false, _ -> None)
            if parsed |> List.forall Option.isSome then Some (parsed |> List.map Option.get)
            else None

/// Parse a nested float list `[[a, b], [c, d]]`. Bracket-aware (rows are
/// split on depth-0 commas, not on a literal "], [" spelling) and strict in
/// the same sense as tryParseFloatList: one bad element fails the whole pin.
let internal tryParse2DList (s: string) : float list list option =
    let t = s.Trim()
    if not (t.StartsWith("[") && t.EndsWith("]")) then None
    else
        let inner = t.Substring(1, t.Length - 2).Trim()
        if String.IsNullOrWhiteSpace(inner) then Some []
        else
            let rows = splitTopLevelCommas inner |> List.map tryParseFloatList
            if rows |> List.forall Option.isSome then Some (rows |> List.map Option.get)
            else None

/// Parse a single complex pair `(re, im)` returning (re, im) on success.
/// Tolerates surrounding whitespace and accepts both `1` and `1.0` for
/// each component (matching the existing Double.TryParse convention).
let private parseComplexPair (s: string) : (float * float) option =
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
let private parseComplexArray (s: string) : (float * float) list option =
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
let private tryParseQuotedString (s: string) : string option =
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
let private tryParseStringArray (s: string) : string list option =
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

/// Does this value text look like a BOOL array pin (`[true, false, ...]`)?
///
/// Dispatch in tryParseExpectPin is by leading characters, and `[` alone is
/// already claimed by the numeric path. Neither `true` nor `false` can begin a
/// float, a quoted string or a complex pair, so "a `[` whose first element
/// starts with a boolean literal" is an unambiguous discriminator that cannot
/// steal `[1, 2]` (numeric), `["a"]` (string) or `[(1,0)]` (complex) from the
/// branches above it. Deliberately NOT triggered by an empty `[]`: see
/// tryParseBoolListPin.
let private looksLikeBoolArray (value: string) : bool =
    if not (value.StartsWith("[")) then false
    else
        let afterBracket = value.Substring(1).TrimStart().ToLowerInvariant()
        afterBracket.StartsWith("true") || afterBracket.StartsWith("false")

/// Parse a bracketed bool list `[true, false, true]` from the PIN side.
///
/// Only the literal spellings `true`/`false` are accepted here (case-
/// insensitively), exactly as the scalar `ExpectedBool` pin does — a pin is
/// hand-written, so there is no reason to admit the `1`/`0` spelling and every
/// reason to keep one canonical way to write it. The tolerance for `1`/`0`
/// belongs on the ACTUAL side, where the value comes from whatever the
/// generated printer chose to emit (see tryParse1DBoolArray).
///
/// Strict in the same sense as tryParseFloatList: one unparseable element
/// fails the whole pin, which parseMalformedExpectLines then reports and the
/// runner turns into a test failure. There is no element-level fallback.
///
/// Returns None for an empty `[]` rather than Some []. `[]` carries no
/// evidence that it is a bool array, and it has always been parsed as an empty
/// ExpectedArray1D; the two behave identically against an empty printed array
/// (both only check "zero elements"), so re-routing it here would change which
/// DU case an existing pin produces for no gain.
let private tryParseBoolListPin (s: string) : bool list option =
    let t = s.Trim()
    if not (t.Length >= 2 && t.StartsWith("[") && t.EndsWith("]")) then None
    else
        let inner = t.Substring(1, t.Length - 2).Trim()
        if String.IsNullOrWhiteSpace(inner) then None
        else
            let parsed =
                splitTopLevelCommas inner
                |> List.map (fun x ->
                    match x.Trim().ToLowerInvariant() with
                    | "true" -> Some true
                    | "false" -> Some false
                    | _ -> None)
            if parsed |> List.forall Option.isSome then Some (parsed |> List.map Option.get)
            else None

/// Every `// EXPECT:` line of a source, as (verbatim trimmed line, payload)
/// where the payload is the text after the marker. Both the pin parser and
/// the malformed-line reporter walk THIS list, so the two can never disagree
/// about which lines are assertions in the first place.
let private expectLines (source: string) : (string * string) list =
    source.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun line ->
        let trimmed = line.Trim()
        if trimmed.StartsWith("// EXPECT:") then Some (trimmed, trimmed.Substring(10).Trim())
        else None)
    |> Array.toList

/// Parse one `// EXPECT:` payload (`varname = value`) into a pin.
/// None means the line is NOT a usable assertion — either it has no `=` at
/// all, or its value side failed to parse. Callers must treat None as a
/// reportable condition (see parseMalformedExpectLines), never as "no pin
/// here": a dropped pin is an assertion that silently stops being checked.
let private tryParseExpectPin (payload: string) : ExpectedValue option =
    match payload.Split([|'='|], 2) with
    | [| name; value |] ->
        let name = name.Trim()
        let value = value.Trim()
        if name = "" then None
        elif value.StartsWith("[[") then
            match tryParse2DList value with
            | Some rows -> Some (ExpectedArray2D (name, rows))
            | None -> None
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
        elif looksLikeBoolArray value then
            // Bool array: [true, false, ...]. Must be tested BEFORE the bare
            // `[` numeric branch below, which would otherwise swallow it and
            // fail on Double.TryParse "true". Tested AFTER `[[`, `[(` and `["`
            // because looksLikeBoolArray only fires on a leading boolean
            // literal, which none of those three can produce.
            match tryParseBoolListPin value with
            | Some bs -> Some (ExpectedArray1DBool (name, bs))
            | None -> None
        elif value.StartsWith("[") then
            match tryParseFloatList value with
            | Some xs -> Some (ExpectedArray1D (name, xs))
            | None -> None
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

/// Parse expected values from test source comments
/// Format: // EXPECT: varname = value
/// Format: // EXPECT: varname = [1.0, 2.0, 3.0]
/// Format: // EXPECT: varname = [[1.0, 2.0], [3.0, 4.0]]
/// Format: // EXPECT: varname = (1.0, 2.0)                        (Complex scalar)
/// Format: // EXPECT: varname = [(1.0, 0.0), (0.0, 1.0)]          (Complex array)
/// Format: // EXPECT: varname = "hello"                           (String scalar — quotes required)
/// Format: // EXPECT: varname = ["a", "b", "c"]                   (String array — quotes around each element)
/// Format: // EXPECT: varname = [true, false, true]               (Bool array — literal true/false per element)
let parseExpectedValues (source: string) : ExpectedValue list =
    expectLines source |> List.choose (snd >> tryParseExpectPin)

/// The `// EXPECT:` lines that parseExpectedValues could NOT turn into a pin,
/// returned verbatim so the runner can FAIL the test instead of ignoring the
/// assertion.
///
/// This is the other half of parseExpectedValues, and it exists because
/// dropping an unparseable pin is silently destructive: a test whose pins ALL
/// fail to parse reports HasExpectedValues = false, the value gate goes
/// vacuous, and the test then passes on "compiled and exited 0" alone — the
/// exact compiles-clean-but-unverified hole EXPECT checks exist to close.
///
/// Note this deliberately also reports lines with no `=` (they cannot be
/// assertions). Some probes use `// EXPECT:` as free prose; deciding which
/// tests are allowed to do that needs the test NAME, which this function does
/// not have, so that policy lives in the runner.
let parseMalformedExpectLines (source: string) : string list =
    expectLines source
    |> List.filter (fun (_, payload) -> (tryParseExpectPin payload).IsNone)
    |> List.map fst

/// Parse the expected-abort message substrings from test source comments.
/// Format: // ABORT: <substring>   (repeatable — ALL pins must be contained)
/// Used by "(aborts)" probes: the test must compile, run, and exit nonzero
/// with every <substring> present in its output (runExecutable merges stderr
/// into the output, so std::cerr abort messages are visible here). Multiple
/// pins let stack-trace probes assert message + frame lines together.
let parseAbortExpectations (source: string) : string list =
    source.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun line ->
        let trimmed = line.Trim()
        if trimmed.StartsWith("// ABORT:") then
            Some (trimmed.Substring(9).Trim())
        else None)
    |> Array.toList

/// Diagnostics-corpus pins (tests/corpus/diagnostics). Formats:
///   // ERROR: BL2001                     code only
///   // ERROR: BL2001 @ 4:12              code + start position
///   // ERROR: BL2001 @ 4:12-4:20         code + full span
///   // ERROR-CONTAINS: <substring>       message substring (any diagnostic)
/// Line/col are 1-based positions in the source as the corpus loader returns
/// it (the // TEST: line is stripped; pin comment lines themselves count).
type DiagPin = {
    PinCode: string
    PinStart: (int * int) option
    PinEnd: (int * int) option
}

let parseDiagPins (source: string) : DiagPin list * string list =
    let parseLC (s: string) =
        match s.Split(':') with
        | [| l; c |] ->
            match Int32.TryParse l, Int32.TryParse c with
            | (true, l), (true, c) -> Some (l, c)
            | _ -> None
        | _ -> None
    let pins = ResizeArray<DiagPin>()
    let contains = ResizeArray<string>()
    for raw in source.Split('\n') do
        let t = raw.TrimEnd('\r').Trim()
        if t.StartsWith "// ERROR-CONTAINS:" then
            contains.Add (t.Substring(18).Trim())
        elif t.StartsWith "// ERROR:" then
            let spec = t.Substring(9).Trim()
            let parts = spec.Split([|'@'|], 2)
            let pin = { PinCode = parts.[0].Trim(); PinStart = None; PinEnd = None }
            let pin =
                if parts.Length = 2 then
                    let ends = parts.[1].Trim().Split([|'-'|], 2)
                    let pin = { pin with PinStart = parseLC (ends.[0].Trim()) }
                    if ends.Length = 2 then { pin with PinEnd = parseLC (ends.[1].Trim()) }
                    else pin
                else pin
            pins.Add pin
    (List.ofSeq pins, List.ofSeq contains)

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

/// Parse a 1D bool array out of PROGRAM OUTPUT, e.g. "[true, false, true]".
///
/// Two spellings are accepted per element, `true`/`false` and `1`/`0`, for the
/// same reason the scalar ExpectedBool matcher accepts both: whether a printed
/// Bool reads as a word or a digit depends on whether the generated printer put
/// `std::boolalpha` on that stream, which is a codegen detail the pin author
/// cannot see. Anything else is a parse failure, not a silent `false` — a bool
/// pin that quietly matched garbage would be worse than no pin at all.
///
/// Nested rows are flattened: `[[true, false], [true, false]]` parses as four
/// elements. This mirrors the flat/nested duality already accepted by the
/// ExpectedArray2D matcher, and it exists because the rank-2 array printers
/// disagree about whether they emit row brackets. Every element and the total
/// count are still compared; only the row split is unobservable.
let rec tryParse1DBoolArray (s: string) : bool list option =
    let t = s.Trim()
    if not (t.Length >= 2 && t.StartsWith("[") && t.EndsWith("]")) then None
    else
        let inner = t.Substring(1, t.Length - 2).Trim()
        if String.IsNullOrWhiteSpace(inner) then Some []
        else
            let parsed =
                splitTopLevelCommas inner
                |> List.map (fun x ->
                    let e = x.Trim()
                    if e.StartsWith("[") then tryParse1DBoolArray e
                    else
                        match e.ToLowerInvariant() with
                        | "true" | "1" -> Some [ true ]
                        | "false" | "0" -> Some [ false ]
                        | _ -> None)
            if parsed |> List.forall Option.isSome then Some (parsed |> List.collect Option.get)
            else None

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

                | ExpectedArray1DBool (name, expectedVals) ->
                    // Bools are exact, so there is no tolerance to apply here:
                    // report the element COUNT first (a length mismatch makes
                    // per-element diffs meaningless) and then the first
                    // differing element with its index, matching the shape of
                    // the numeric-array and complex-array diagnostics.
                    match actual.TryFind name with
                    | Some actualStr ->
                        match tryParse1DBoolArray actualStr with
                        | Some actualVals ->
                            if actualVals.Length <> expectedVals.Length then
                                Some (sprintf "%s: expected %d bool elements, got %d ('%s')"
                                              name expectedVals.Length actualVals.Length actualStr)
                            else
                                List.zip expectedVals actualVals
                                |> List.mapi (fun i (e, a) -> (i, e, a))
                                |> List.tryPick (fun (i, e, a) ->
                                    if e = a then None
                                    else Some (sprintf "%s: element %d expected %b, got %b" name i e a))
                        | None -> Some (sprintf "%s: could not parse '%s' as a bool array" name actualStr)
                    | None -> Some (sprintf "%s: not found in output" name)

                | ExpectedArray2D (name, expectedRows) ->
                    // Two actual-side shapes are accepted, because the
                    // generated printers do not all agree:
                    //
                    //   nested — `name = [[0, 1], [20, 21]]`. Shape is
                    //     carried by the output, so rows and per-row lengths
                    //     are compared before elements.
                    //   flat   — `name = [0, 1, 20, 21]`. genPrintArrayFlat /
                    //     genPrintArraySymAware walk a rank-2+ array with
                    //     nested loops but emit ONE comma-separated run, so
                    //     the printed text carries no row boundaries. The pin
                    //     is then compared against its own row-major
                    //     flattening: every element and the total count are
                    //     still checked, only the row split is unobservable.
                    //
                    // Anything else is a hard error. The previous behaviour —
                    // returning None for every 2D pin — meant these
                    // assertions were parsed and then thrown away, so the
                    // tests carrying them counted as value-check passes
                    // without a single element ever being compared.
                    match actual.TryFind name with
                    | Some actualStr ->
                        let compareRows (actualRows: float list list) =
                            if actualRows.Length <> expectedRows.Length then
                                Some (sprintf "%s: expected %d rows, got %d" name expectedRows.Length actualRows.Length)
                            else
                                let rowIssue =
                                    List.zip expectedRows actualRows
                                    |> List.mapi (fun i (e, a) -> (i, e, a))
                                    |> List.tryPick (fun (i, e, a) ->
                                        if e.Length <> a.Length then
                                            Some (sprintf "%s: row %d expected %d elements, got %d" name i e.Length a.Length)
                                        else
                                            List.zip e a
                                            |> List.mapi (fun j (ev, av) -> (j, ev, av))
                                            |> List.tryPick (fun (j, ev, av) ->
                                                if floatEquals ev av tolerance then None
                                                else Some (sprintf "%s: [%d][%d] expected %.17g, got %.17g (diff=%.3e)"
                                                                   name i j ev av (abs (ev - av)))))
                                rowIssue
                        match tryParse2DList actualStr with
                        | Some actualRows -> compareRows actualRows
                        | None ->
                            match tryParse1DArray actualStr with
                            | Some flatActual ->
                                let flatExpected = List.concat expectedRows
                                if flatActual.Length <> flatExpected.Length then
                                    Some (sprintf "%s: expected %d elements (%d rows, row-major), got %d"
                                                  name flatExpected.Length expectedRows.Length flatActual.Length)
                                else
                                    List.zip flatExpected flatActual
                                    |> List.mapi (fun k (ev, av) -> (k, ev, av))
                                    |> List.tryPick (fun (k, ev, av) ->
                                        if floatEquals ev av tolerance then None
                                        else Some (sprintf "%s: element %d (row-major) expected %.17g, got %.17g (diff=%.3e)"
                                                           name k ev av (abs (ev - av))))
                            | None -> Some (sprintf "%s: could not parse '%s' as a 2D array" name actualStr)
                    | None -> Some (sprintf "%s: not found in output" name)

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
