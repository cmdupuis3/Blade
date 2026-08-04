// Interpreter-backed REPL seam.
//
// Re-compiling (g++ -O2) and re-running the whole accumulated session on
// every input costs 1-5 s of toolchain latency per submission. This module
// instead lets the REPL evaluate the same lowered IR under the tree-walking
// interpreter (Blade.Interp.Run) -- the identical evaluator the differential
// gate (tests/InterpDiff.fs) pins byte-for-byte against the compiled binary --
// collapsing a typical turn to <100 ms and dropping the g++ dependency for
// the inputs the interpreter supports.
//
// Whole-session re-interpretation per input: SSA IRIds are freshly minted on
// every lowering pass, so a persistent cross-input Env keyed by IRId cannot
// be reused, and the REPL's rebind semantics already recompute downstream
// dependents by replacing source in place -- so a full re-lower + run is both
// correct and, with g++ gone, fast. Each input therefore lowers once
// (`lowerSession`, shared by Cli.ReplTypes's type-annotation map and this
// evaluation) and runs under runProgram; the caller (Cli.replLoop) still owns
// output filtering, annotation, and session bookkeeping unchanged.
//
// Compiled after Interp/Run.fs (it wraps runProgram) and before Cli.fs, which
// consumes it via short sibling-module names.
module Blade.Interp.Repl

/// Everything one front-end pass over the session source produces: the parsed
/// AST, the typed program + IR builder (Cli.ReplTypes builds its name->type
/// annotation map from these), and the validated IR program (the interpreter's
/// input -- the same post-validateIR IR the differential gate and CodeGen run).
/// Bundling them lets an input cost one lowering instead of two separate
/// passes plus a g++ build.
type LoweredSession =
    { Prog: Blade.Ast.Program
      Typed: Blade.TypedAst.TypedProgram
      Builder: Blade.IR.IRBuilder
      Ir: Blade.IR.IRProgram
      Warnings: string list }

/// Parse -> typecheck -> lower -> validateIR: byte-for-byte the chain
/// Cli.compileFile drives (lowerDiag's parse/typecheck/lower plus the BL6001
/// validate step), but capturing every intermediate so the caller can
/// annotate types and run the interpreter from one pass. On any rejection it
/// returns the rustc-style rendered diagnostics string, identical to what
/// compileFile prints. `useColor` mirrors `not Console.IsErrorRedirected`.
let lowerSession (fileName: string option) (useColor: bool) (source: string)
    : Result<LoweredSession, string> =
    let key = defaultArg fileName "<input>"
    let sm = Blade.Diagnostics.SourceMap.ofSources [ key, source ]
    let renderDs (ds: Blade.Diagnostics.Diagnostic list) =
        Blade.Diagnostics.Render.renderAll useColor (Some sm) ds
    match Blade.Parser.parseProgramWithFile fileName source with
    | Error e ->
        Error (renderDs [ Blade.Parser.diagnosticOfParseError fileName e ])
    | Ok prog ->
        match Blade.TypeCheck.typeCheck prog with
        | Error errors ->
            Error (renderDs (errors |> List.map Blade.TypeEnv.diagnosticOfCompileError))
        | Ok (tp, builder, warnings) ->
            // Lowering can THROW (not just return Error) when a provider load
            // fails at compile time (e.g. `netcdf.load("missing.nc")`). Catch
            // it here so the REPL surfaces a diagnostic instead of an
            // unhandled exception killing the session.
            match (try Ok (Blade.Lowering.lowerTypedProgram tp (Some prog) builder)
                   with ex -> Error ex.Message) with
            | Error msg ->
                Error (renderDs [ Blade.Diagnostics.mkError "BL6002" Blade.Diagnostics.PhIRValidate Blade.Ast.noSpan msg ])
            | Ok ir ->
            match Blade.IR.validateIR ir with
            | Error errs ->
                Error (renderDs (errs |> List.map (fun s ->
                    Blade.Diagnostics.mkError "BL6001" Blade.Diagnostics.PhIRValidate Blade.Ast.noSpan s)))
            | Ok validated ->
                Ok { Prog = prog; Typed = tp; Builder = builder; Ir = validated; Warnings = warnings }

/// How the REPL should treat one interpreter run.
type ReplOutcome =
    /// The interpreter produced authoritative output: normal completion
    /// (exit 0) OR a Blade runtime-guard panic (exit 1). Its stdout/stderr are
    /// used directly, NO g++ fallback -- a guard violation is a real program
    /// fault the compiled binary reports the same way (Run.formatPanic
    /// mirrors blade_rt::panic byte-for-byte).
    | InterpDone of Run.InterpResult
    /// The interpreter cannot evaluate some node yet (Run.ExitUnsupported=125)
    /// or hit its own bug (Run.ExitInterpBug=70). Neither is a program fault,
    /// so the caller falls back to the historical g++ compile+run for THIS
    /// input. `feature` carries the "interp-unsupported: <f>" / "interp-error:
    /// <m>" reason (for a telemetry line, if the caller wants one).
    | InterpFellShort of feature: string

/// Run the already-lowered session IR under the tree-walking interpreter and
/// classify the outcome for the REPL seam. runProgram already maps every
/// failure onto the 0/1/125/70 exit protocol; this only routes 125/70 to the
/// fallback lane and 0/1 to direct use. `sessionName` is the synthetic
/// program name used for the leading timing line (the caller strips it) and
/// panic frames.
let evalSession (lowered: LoweredSession) (sessionName: string) : ReplOutcome =
    let r = Run.runProgram lowered.Ir sessionName Value.defaultLimits
    if r.ExitCode = Run.ExitUnsupported || r.ExitCode = Run.ExitInterpBug then
        InterpFellShort (r.Stderr.Trim())
    else
        InterpDone r
