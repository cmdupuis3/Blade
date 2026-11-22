// THE PRINT FORM (BLADE_PRINT_FORM / --print-form) -- which spelling a
// program's top-level binding prints use (deep review P3-26, the user's ruling
// D10 of 2026-11-14 and the default decision of 2026-11-16).
//
//   * TEST form: the spelling every corpus `// EXPECT:` pin, the differential
//     gates and every harness read, byte for byte what Blade has always
//     printed: `name = value`, a Float64 at 15 significant digits, a Float32 at
//     6, no units, a bare expression under its `__exprN` label, and a sum-type
//     value (or a struct holding one) printing nothing.
//   * DISPLAY form: for people. Floats at their SHORTEST round-trip digits
//     (one shared C implementation in the compiled lanes, blade_shortest.h; the
//     interpreter's CppFormat.formatFloatShortest twin), always visibly a float
//     (`1.0`); a unit-carrying value followed by its unit (`speed = 3.5 mps`,
//     an array's unit once after its closing bracket); a bare expression's
//     value with no label; a sum-type value as its constructor with its
//     payload (`Circle(1.5)`, `Empty`) and a struct holding one spelled like
//     its literal. Everything else -- labels, the rank regime, the selection
//     (BLADE_PRINT) -- as in the test form. BLADE_PRINT_BITS is a test
//     instrument: it applies to the test form only.
//
// It is an EMISSION choice (unit names and labels are compile-time facts), so
// the executable cache keys on it for free. Every in-process reader --
// codegen's print pass, the LLVM lane's, the interpreter's -- reads `current`,
// whose UNSET reading is the test form: the REPL, notebooks, the IDE, MCP
// hosts and the test harness print the test form unless told otherwise. The
// human-facing CLI verbs (`run`, `compile`, `emit`, `plan`) resolve their own
// default first (`applyCliDefault`: the flag, then the environment, then the
// `blade.toolchain.json` key of the same name, then `cliDefault`) and pin it
// for the process, so the compiled program and the plan see one answer.
module Blade.PrintForm

type Form =
    | Test
    | Display

/// THE DEFAULT of the human-facing verbs (`blade run` / `compile` / `emit` /
/// `plan`): the user's decision of 2026-11-16 (P3-26). The one place it lives.
let cliDefault : Form = Display

/// The environment variable (and toolchain-file key) that carries the form.
let envVar = Blade.Knobs.printForm.Name

let name (f: Form) : string =
    match f with
    | Test -> "test"
    | Display -> "display"

/// `test` / `display` (trimmed, any case); anything else is not a form.
let parse (s: string) : Form option =
    match (if isNull s then "" else s.Trim().ToLowerInvariant()) with
    | "test" -> Some Test
    | "display" -> Some Display
    | _ -> None

/// The form the emitters and the interpreter's printer use. Read per call,
/// like every env gate; unset (or unparsable -- the CLI refuses that before
/// any emission) = the test form.
let current () : Form =
    match Blade.Knobs.raw Blade.Knobs.printForm with
    | null | "" -> Test
    | s -> defaultArg (parse s) Test

/// Where the CLI's resolution found the form, for `blade plan`.
let mutable private resolvedSource : string = ""

/// What `blade plan` says: the form `current` answers and where it came from.
let describe () : string =
    let f = current ()
    let src =
        match resolvedSource with
        | "" ->
            match Blade.Knobs.raw Blade.Knobs.printForm with
            | null | "" -> "unset: the in-process default"
            | _ -> envVar
        | s -> s
    $"{name f} ({src})"

/// The CLI verbs' resolution: an explicit `--print-form` (already pinned into
/// the environment by the argv pre-pass, `fromFlag = true`) or BLADE_PRINT_FORM
/// wins; else the toolchain file's key; else `cliDefault`. Pins the answer
/// into the environment for the rest of the process (the emitters read it
/// there) and returns an error for a value that is no form.
let applyCliDefault (fromFlag: bool) : Result<Form, string> =
    let bad (v: string) (where: string) =
        Error $"{where}: '{v}' is not a print form (expected test or display)"
    match Blade.Knobs.raw Blade.Knobs.printForm with
    | null | "" ->
        match Blade.Toolchain.getWithOrigin envVar with
        | Some (v, _) ->
            match parse v with
            | Some f ->
                System.Environment.SetEnvironmentVariable(envVar, name f)
                resolvedSource <- "blade.toolchain.json"
                Ok f
            | None -> bad v $"{envVar} in blade.toolchain.json"
        | None ->
            System.Environment.SetEnvironmentVariable(envVar, name cliDefault)
            resolvedSource <- "the default of the run / compile / emit / plan verbs"
            Ok cliDefault
    | v ->
        match parse v with
        | Some f ->
            resolvedSource <- (if fromFlag then "--print-form" else envVar)
            Ok f
        | None -> bad v (if fromFlag then "--print-form" else envVar)
