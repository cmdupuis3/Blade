// File-based module resolution (src/ModuleResolve.fs) and the stdlib it was
// built for (stdlib/units/SI.blade).
//
// Unlike the `multifile` corpus -- which hands `lowerMultiSource` a
// hand-assembled (moduleName, source) list and therefore never touches
// resolution at all -- every case here starts from a FILE ON DISK, which is
// the only way to exercise the search path, the transitive walk, the cycle
// and duplicate checks, and the .fsproj's stdlib deployment.
//
// Two claims carry the block:
//
//   * A file with nothing to resolve must compile to the SAME C++ it compiled
//     to before the module layer existed. `no_imports_is_byte_identical`
//     asserts that against `lowerDiag` directly, so "zero risk to existing
//     users" is a test, not a promise.
//   * A unit that crossed a module boundary must still be the unit it was.
//     The mismatch probes are the load-bearing ones: an import that silently
//     brought in NOTHING would leave `Float<newton>` unannotated, and every
//     positive case would still pass.
//
// The value case needs g++ and reports Skipped without it; everything else is
// pure front-end and always runs.
module Blade.Tests.ModuleResolveTests

open System
open System.IO
open Blade
open Blade.Build
open Blade.Tests.TestHarness

// Scratch tree

/// One temp directory per run, removed on dispose. Every case writes its
/// fixtures under here, so nothing in the repo is touched and parallel runs of
/// different suites cannot collide.
type private Scratch() =
    let root =
        Path.Combine(Path.GetTempPath(),
                     sprintf "blade_modres_%s" (Guid.NewGuid().ToString("N").Substring(0, 12)))
    do Directory.CreateDirectory root |> ignore
    member _.Root = root
    /// Write `rel` (a relative path, '/'-separated) with `text`, creating
    /// intermediate directories. Returns the absolute path.
    member _.Write (rel: string) (text: string) : string =
        let full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text)
        full
    interface IDisposable with
        member _.Dispose() = try Directory.Delete(root, true) with _ -> ()

/// Pin an environment variable for a scope. Same idiom as ShapeSpecTests.pinEnv;
/// it works here because `ModuleResolve.stdlibRoots` memoizes on the variable's
/// VALUE rather than freezing the first answer.
let private pinEnv (name: string) (value: string option) =
    let prior = Environment.GetEnvironmentVariable name
    Environment.SetEnvironmentVariable(name, (match value with Some v -> v | None -> null))
    { new IDisposable with
        member _.Dispose() = Environment.SetEnvironmentVariable(name, prior) }

// Assertion plumbing

let private results = ResizeArray<string * Outcome>()

let private check (name: string) (ok: bool) (detail: string) =
    results.Add((name, (if ok then Pass else Fail)))
    resultLine (if ok then Pass else Fail) name detail

let private skip (name: string) (detail: string) =
    results.Add((name, Skip))
    resultLine Skip name detail

/// Every diagnostic's code + message + notes, flattened, so a case can ask
/// "did the report say this" without caring which note carried it.
let private textOf (ds: Blade.Diagnostics.Diagnostic list) =
    ds
    |> List.collect (fun d -> d.Code :: d.Message :: (d.Notes |> List.map snd))
    |> String.concat "\n"

// Fixtures

/// A program that uses the stdlib SI units and prints values a run can check.
let private siProgram =
    "import units.SI\n"
    + "Unit accel = meter / second^2\n"
    + "let mass: Float<kilogram> = 2.0\n"
    + "let a: Float<accel> = 3.0\n"
    + "let force: Float<newton> = mass * a\n"
    + "let extra: Float<newton> = 4.0\n"
    + "let total = force + extra\n"

/// The same program written against a SELECTIVE import. `newton` alone is not
/// enough: the accel unit needs `meter` and `second` too, which is the point --
/// selective import must bring in exactly the names listed.
let private siSelectiveProgram =
    "from units.SI import newton, kilogram, meter, second\n"
    + "Unit accel = meter / second^2\n"
    + "let mass: Float<kilogram> = 2.0\n"
    + "let a: Float<accel> = 3.0\n"
    + "let force: Float<newton> = mass * a\n"

let private cppOf (name: string) (ir: IR.IRProgram) =
    fst (CodeGen.genSelfContainedProgramFromIR ir name)

// The block

let runModuleResolveTests () : BlockResult =
    printHeader "Blade-DSL: Module Resolution + units.SI Tests"
    results.Clear()
    use scratch = new Scratch()
    // No inherited override: every search-path case states its own environment.
    use _neutral = pinEnv "BLADE_STDLIB" None

    // -- name -> path mapping --------------------------------------------
    check "dotted_name_maps_to_nested_blade_file"
        (Blade.ModuleResolve.relativePathOf [ "units"; "SI" ]
            = Path.Combine("units", "SI.blade"))
        (Blade.ModuleResolve.relativePathOf [ "units"; "SI" ])

    // -- the stdlib ships next to the binary ------------------------------
    let roots = Blade.ModuleResolve.stdlibRoots ()
    let siOnDisk =
        roots |> List.tryPick (fun r ->
            let p = Path.Combine(r, "units", "SI.blade")
            if File.Exists p then Some p else None)
    check "stdlib_is_findable_from_the_running_binary"
        siOnDisk.IsSome
        (match siOnDisk with
         | Some p -> p
         | None -> sprintf "roots: %s" (String.concat " ; " roots))

    // -- SOURCE beats the deployed copy, in a checkout ---------------------
    // Blade.fsproj copies stdlib/ next to the binary and the probe is
    // nearest-first, so that copy used to shadow <repo>/stdlib: edits did
    // nothing until a rebuild, and a restored-OLDER file did nothing even
    // then (PreserveNewest compares timestamps). Search ORDER is what fixes
    // it, so search order is what gets pinned.
    //
    // The guard is conditional, the assertion is not: run from a DEPLOYED tree
    // there is no checkout root to prefer and nothing here to say. The marker
    // is Blade.fsproj beside the stdlib directory -- exactly the gate
    // `upwardRepoStdlibCandidates` applies.
    let isSourceRoot (r: string) =
        match (try Path.GetDirectoryName r with _ -> null) with
        | null -> false
        | d -> File.Exists (Path.Combine(d, "Blade.fsproj"))
    if roots |> List.exists isSourceRoot then
        check "stdlib_source_root_precedes_the_deployed_copy"
            (roots |> List.head |> isSourceRoot)
            (sprintf "roots: %s" (String.concat " ; " roots))

    // -- nothing to resolve: the single-file path, unchanged ---------------
    let plainPath = scratch.Write "plain.blade" "let x = 1.0\nlet y = x + 2.0\n"
    let plainSrc = File.ReadAllText plainPath
    let plainRes = Blade.ModuleResolve.resolveEntry plainPath plainSrc
    check "no_imports_resolves_to_exactly_the_entry_file"
        (plainRes.Errors.IsEmpty && plainRes.Files.Length = 1)
        (sprintf "%d file(s), %d error(s)" plainRes.Files.Length plainRes.Errors.Length)

    // The byte-identity claim, stated as C++ text: whatever `lowerDiag` would
    // have produced for a file with no imports, `lowerFileDiag` produces.
    let identical =
        match fst (Blade.Lowering.lowerDiag (Some plainPath) plainSrc),
              fst (Blade.Lowering.lowerFileDiag plainPath plainSrc) with
        | Ok (a, _), Ok (b, _) -> Some (cppOf "plain" a = cppOf "plain" b)
        | _ -> None
    check "no_imports_is_byte_identical_to_the_pre_module_path"
        (identical = Some true)
        (match identical with
         | Some true -> "identical C++"
         | Some false -> "emitted C++ differs"
         | None -> "one of the two lowerings failed")

    // -- builtin pseudo-modules are never looked for on disk ---------------
    for builtin in [ "math"; "ml"; "ppl"; "rand"; "sgs"; "spectra"; "ad"; "netcdf"; "zarr"; "csv" ] do
        let p = scratch.Write (sprintf "builtin_%s.blade" builtin)
                    (sprintf "import %s as bb\nlet x = 1.0\n" builtin)
        let r = Blade.ModuleResolve.resolveEntry p (File.ReadAllText p)
        check (sprintf "builtin_module_%s_is_not_searched_for" builtin)
            (r.Errors.IsEmpty && r.Files.Length = 1)
            (if r.Errors.IsEmpty then "" else textOf r.Errors)

    // -- the stdlib import ------------------------------------------------
    let siPath = scratch.Write "si_main.blade" siProgram
    let siRes = Blade.ModuleResolve.resolveEntry siPath siProgram
    check "import_units_SI_resolves_to_two_files_entry_last"
        (siRes.Errors.IsEmpty
         && siRes.Files.Length = 2
         && (List.last siRes.Files).Path = Path.GetFullPath siPath
         && siRes.Files.Head.Declared = "units.SI")
        (if siRes.Errors.IsEmpty then
            siRes.Files |> List.map (fun f -> sprintf "%s(%s)" (Path.GetFileName f.Path) f.Declared)
                        |> String.concat " -> "
         else textOf siRes.Errors)

    let siLowered = fst (Blade.Lowering.lowerFileDiag siPath siProgram)
    check "units_SI_program_lowers"
        (match siLowered with Ok _ -> true | Error _ -> false)
        (match siLowered with
         | Ok (ir, _) -> sprintf "%d module(s)" ir.Modules.Length
         | Error ds -> textOf ds)

    // The REJECT probe. If the import brought in nothing, `Float<meter>` and
    // `Float<second>` would both degrade to no annotation and this would pass
    // the checker -- which is exactly the failure every positive case above is
    // blind to.
    let siBadPath =
        scratch.Write "si_bad.blade"
            ("import units.SI\n"
             + "let a: Float<meter> = 1.0\n"
             + "let b: Float<second> = 2.0\n"
             + "let c = a + b\n")
    let siBad = fst (Blade.Lowering.lowerFileDiag siBadPath (File.ReadAllText siBadPath))
    check "imported_units_still_reject_a_dimension_mismatch"
        (match siBad with
         | Error ds -> ds |> List.exists (fun d -> d.Code = "BL3006")
         | Ok _ -> false)
        (match siBad with Error ds -> textOf ds | Ok _ -> "accepted a meter + second sum")

    // -- selective import --------------------------------------------------
    let selPath = scratch.Write "si_selective.blade" siSelectiveProgram
    let sel = fst (Blade.Lowering.lowerFileDiag selPath siSelectiveProgram)
    check "from_units_SI_import_brings_the_named_units"
        (match sel with Ok _ -> true | Error _ -> false)
        (match sel with Ok _ -> "" | Error ds -> textOf ds)

    let selBadPath =
        scratch.Write "si_selective_bad.blade"
            ("from units.SI import newton, kilogram, meter, second\n"
             + "Unit accel = meter / second^2\n"
             + "let m: Float<kilogram> = 2.0\n"
             + "let a: Float<accel> = 3.0\n"
             + "let bad: Float<newton> = m + a\n")
    let selBad = fst (Blade.Lowering.lowerFileDiag selBadPath (File.ReadAllText selBadPath))
    check "selectively_imported_units_reject_a_dimension_mismatch"
        (match selBad with
         | Error ds -> ds |> List.exists (fun d -> d.Code = "BL3006")
         | Ok _ -> false)
        (match selBad with Error ds -> textOf ds | Ok _ -> "accepted a kilogram + accel sum")

    // -- user modules beside the entry file --------------------------------
    scratch.Write "mylib/helpers.blade" "module mylib.helpers\nlet base_rate = 5.0\n" |> ignore
    let userPath =
        scratch.Write "user_main.blade"
            ("import mylib.helpers\n"
             + "let doubled = helpers.base_rate * 2.0\n")
    let userRes = Blade.ModuleResolve.resolveEntry userPath (File.ReadAllText userPath)
    check "user_module_resolves_relative_to_the_entry_file"
        (userRes.Errors.IsEmpty && userRes.Files.Length = 2
         && userRes.Files.Head.Declared = "mylib.helpers")
        (if userRes.Errors.IsEmpty then "" else textOf userRes.Errors)
    let userLowered = fst (Blade.Lowering.lowerFileDiag userPath (File.ReadAllText userPath))
    check "user_module_lowers_through_the_resolved_set"
        (match userLowered with Ok _ -> true | Error _ -> false)
        (match userLowered with Ok _ -> "" | Error ds -> textOf ds)

    // -- transitive discovery ----------------------------------------------
    scratch.Write "chain/C.blade" "module chain.C\nlet c_val = 1.0\n" |> ignore
    scratch.Write "chain/B.blade" "module chain.B\nimport chain.C\nlet b_val = C.c_val + 1.0\n" |> ignore
    scratch.Write "chain/A.blade" "module chain.A\nimport chain.B\nlet a_val = B.b_val + 1.0\n" |> ignore
    let chainPath = scratch.Write "chain_main.blade" "import chain.A\nlet top = A.a_val + 1.0\n"
    let chainRes = Blade.ModuleResolve.resolveEntry chainPath (File.ReadAllText chainPath)
    let chainOrder = chainRes.Files |> List.map (fun f -> f.Declared)
    check "transitive_imports_come_back_in_dependency_order"
        (chainRes.Errors.IsEmpty
         && chainOrder = [ "chain.C"; "chain.B"; "chain.A"; "Main" ])
        (if chainRes.Errors.IsEmpty then String.concat " -> " chainOrder else textOf chainRes.Errors)
    let chainLowered = fst (Blade.Lowering.lowerFileDiag chainPath (File.ReadAllText chainPath))
    check "transitive_chain_lowers"
        (match chainLowered with Ok _ -> true | Error _ -> false)
        (match chainLowered with Ok _ -> "" | Error ds -> textOf ds)

    // -- missing module ----------------------------------------------------
    let missPath = scratch.Write "missing.blade" "import nope.here\nlet x = 1.0\n"
    let miss = Blade.ModuleResolve.resolveEntry missPath (File.ReadAllText missPath)
    let missText = textOf miss.Errors
    check "missing_module_is_BL2004"
        (miss.Errors |> List.exists (fun d -> d.Code = "BL2004"))
        missText
    check "missing_module_names_every_searched_path"
        (missText.Contains "searched:"
         && missText.Contains (Path.Combine("nope", "here.blade"))
         // the entry's own directory is one of them, and so is a stdlib root
         && missText.Contains (Path.GetFullPath scratch.Root)
         && (roots |> List.exists (fun r -> missText.Contains r)))
        missText
    check "missing_module_error_reaches_the_lowering_entry_point"
        (match fst (Blade.Lowering.lowerFileDiag missPath (File.ReadAllText missPath)) with
         | Error ds -> ds |> List.exists (fun d -> d.Code = "BL2004")
         | Ok _ -> false)
        ""

    // -- import cycle ------------------------------------------------------
    scratch.Write "cyc/A.blade" "module cyc.A\nimport cyc.B\nlet a1 = 1.0\n" |> ignore
    scratch.Write "cyc/B.blade" "module cyc.B\nimport cyc.A\nlet b1 = 2.0\n" |> ignore
    let cycPath = scratch.Write "cyc_main.blade" "import cyc.A\nlet z = 1.0\n"
    let cyc = Blade.ModuleResolve.resolveEntry cycPath (File.ReadAllText cycPath)
    let cycText = textOf cyc.Errors
    check "import_cycle_is_BL2005"
        (cyc.Errors |> List.exists (fun d -> d.Code = "BL2005"))
        cycText
    check "import_cycle_message_names_the_cycle"
        (cycText.Contains "cyc.A -> cyc.B -> cyc.A")
        cycText
    // A file importing itself is the degenerate cycle, and it must not hang.
    let selfPath = scratch.Write "selfcyc/S.blade" "module selfcyc.S\nimport selfcyc.S\nlet s = 1.0\n"
    let selfMain = scratch.Write "self_main.blade" "import selfcyc.S\nlet q = 1.0\n"
    ignore selfPath
    let selfRes = Blade.ModuleResolve.resolveEntry selfMain (File.ReadAllText selfMain)
    check "self_import_is_reported_rather_than_looping"
        (selfRes.Errors |> List.exists (fun d -> d.Code = "BL2005"))
        (textOf selfRes.Errors)

    // -- header / import-name agreement + duplicates ------------------------
    scratch.Write "wrong/name.blade" "module some.other\nlet w = 1.0\n" |> ignore
    let wrongPath = scratch.Write "wrong_main.blade" "import wrong.name\nlet x = 1.0\n"
    let wrong = Blade.ModuleResolve.resolveEntry wrongPath (File.ReadAllText wrongPath)
    check "module_header_must_agree_with_the_import_name"
        (wrong.Errors |> List.exists (fun d ->
            d.Code = "BL2006" && d.Message.Contains "declares 'module some.other'"))
        (textOf wrong.Errors)

    // A dependency that forgot its `module` header parses as `Main`, which the
    // entry file already is. Two files, one module name: refuse.
    scratch.Write "dup/thing.blade" "let d = 1.0\n" |> ignore
    let dupPath = scratch.Write "dup_main.blade" "import dup.thing\nlet x = 1.0\n"
    let dup = Blade.ModuleResolve.resolveEntry dupPath (File.ReadAllText dupPath)
    check "two_files_declaring_one_module_are_refused"
        (dup.Errors |> List.exists (fun d ->
            d.Code = "BL2006" && d.Message.Contains "is declared by two files"))
        (textOf dup.Errors)

    // -- BLADE_STDLIB override ---------------------------------------------
    let altRoot = Path.Combine(scratch.Root, "alt_stdlib")
    scratch.Write "alt_stdlib/fake/Mod.blade" "module fake.Mod\nlet fake_val = 7.0\n" |> ignore
    let altMain = scratch.Write "alt_main.blade" "import fake.Mod\nlet v = Mod.fake_val\n"
    let overrideOk =
        use _pin = pinEnv "BLADE_STDLIB" (Some altRoot)
        let r = Blade.ModuleResolve.resolveEntry altMain (File.ReadAllText altMain)
        (r.Errors.IsEmpty && r.Files.Length = 2 && r.Files.Head.Declared = "fake.Mod",
         (if r.Errors.IsEmpty then "" else textOf r.Errors))
    check "BLADE_STDLIB_overrides_the_search_root" (fst overrideOk) (snd overrideOk)
    // ...and the override really is scoped: with it gone the same file fails.
    let afterOverride = Blade.ModuleResolve.resolveEntry altMain (File.ReadAllText altMain)
    check "BLADE_STDLIB_is_re_read_rather_than_frozen"
        (afterOverride.Errors |> List.exists (fun d -> d.Code = "BL2004"))
        (textOf afterOverride.Errors)

    // -- values, end to end -------------------------------------------------
    if not (checkGppAvailable ()) then
        skip "units_SI_program_computes_the_right_values" "g++ not available"
    else
        let outDir = Path.Combine(scratch.Root, "out")
        Directory.CreateDirectory outDir |> ignore
        CodeGen.deployRuntimeHeaders outDir
        match fst (Blade.Lowering.lowerFileDiag siPath siProgram) with
        | Error ds -> check "units_SI_program_computes_the_right_values" false (textOf ds)
        | Ok (ir, _) ->
            let cpp = cppOf "si_main" ir
            let cppFile = Path.Combine(outDir, "si_main.cpp")
            File.WriteAllText(cppFile, cpp)
            match compileForBackendSource (Some cpp) capabilities.Value (inferBackendReq cpp) cppFile outDir with
            | Error e when isSkipError e -> skip "units_SI_program_computes_the_right_values" e
            | Error e -> check "units_SI_program_computes_the_right_values" false (sprintf "compile: %s" e)
            | Ok exe ->
                match runExecutable exe with
                | Error e -> check "units_SI_program_computes_the_right_values" false (sprintf "run: %s" e)
                | Ok (_, output) ->
                    let want = [ "force = 6"; "total = 10" ]
                    let missing = want |> List.filter (fun w -> not (output.Contains w))
                    check "units_SI_program_computes_the_right_values"
                        missing.IsEmpty
                        (if missing.IsEmpty then String.concat ", " want
                         else sprintf "missing %s in: %s" (String.concat " | " missing)
                                  (output.Replace("\r\n", " ").Replace("\n", " ")))

    let passed = results |> Seq.filter (fun (_, o) -> o = Pass) |> Seq.length
    let failed = results |> Seq.filter (fun (_, o) -> o = Fail) |> Seq.length
    let skipped = results |> Seq.filter (fun (_, o) -> o = Skip) |> Seq.length
    printFooter "Module Resolution"
        [ sprintf "%d passed" passed; sprintf "%d failed" failed
          sprintf "%d skipped" skipped ]
    { Block = "Module Resolution"; Passed = passed; Failed = failed; Skipped = skipped
      FailedNames = results |> Seq.filter (fun (_, o) -> o = Fail) |> Seq.map fst |> List.ofSeq }
