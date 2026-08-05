// Shape-monomorphization REACH tests — Phase 4 of
// docs/plan-cpp-perf-exploitation.md, second increment.
//
// The corpus proves the VALUES (a specialized program computes what the
// generic one did). This block proves the DECISIONS, which the values cannot
// see: whether a copy was made at all, whether a call site was rewritten to it,
// and — as much as anything here — whether the cases that must DECLINE still
// decline. A regression that silently stopped specializing leaves every value
// test green and costs up to 1.77x on short-fiber kernels; a regression that
// silently started specializing something unsound leaves them green too, right
// up until the out-of-bounds read lands on a page that is not zero.
//
// Emission text is the only witness for both. The baked literal appears as a
// loop BOUND (`__i0 < 5`) where the generic copy reads `.extents[0]`, and the
// copy itself appears as a `<name>_shape_<name><literal>` definition, so a
// positive case asserts the literal bound inside the spec and a negative case
// asserts that no `_shape` definition exists at all.
//
// Pure lowering + codegen: no g++, no toolchain. Always runs.
module Blade.Tests.ShapeSpecTests

open Blade
open Blade.Lowering
open Blade.Tests.TestHarness

/// Pin one environment variable for the duration of a scope, restoring the
/// prior value on exit. Same use-guard idiom as `LinAlgTests.pinEnv`, and it
/// works for the same reason: `IR.shapeSpecCap` re-reads the environment at
/// every consultation rather than freezing a module value.
let private pinEnv (name: string) (value: string option) =
    let prior = System.Environment.GetEnvironmentVariable(name)
    System.Environment.SetEnvironmentVariable(name, (match value with Some v -> v | None -> null))
    { new System.IDisposable with
        member _.Dispose() = System.Environment.SetEnvironmentVariable(name, prior) }

let private cppOfSource (testName: string) (src: string) : Result<string, string> =
    try
        match lower src with
        | Error e -> Error (sprintf "lower: %s" e)
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    with ex -> Error (sprintf "codegen raised: %s" ex.Message)

/// The multi-module twin. `lowerMultiSource` is the only door to a program with
/// more than one `IRModule`, which is the whole point of the cross-module case.
let private cppOfModules (testName: string) (sources: (string * string) list) : Result<string, string> =
    try
        match lowerMultiSource sources with
        | Error e -> Error (sprintf "lower: %s" e)
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    with ex -> Error (sprintf "codegen raised: %s" ex.Message)

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

/// A symbolic-extent function that ITERATES (the benefit gate wants a
/// loop-producing node) and returns a scalar, so nothing downstream depends on
/// the return shape.
let private sumFn (name: string) =
    sprintf "function %s(v: Array<Float64 like Idx<n>>) -> Float64 = reduce(v, (+))\n" name

let private vec5 = "let w5: Array<Float64 like Idx<5>> = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
let private vec3 = "let w3: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n"

/// (name, sources, mustContain, mustNotContain). A single-element source list
/// goes through the single-module pipeline; two or more exercise
/// `lowerMultiSource` and the module merge.
let private emissionCases : (string * (string * string) list * string list * string list) list =
    [ // ---- Cross-module ----
      // The call site is in Main, the definition is in Shapes, and the spec is
      // placed in Shapes (next to its origin). Before this increment the pass
      // ran per IRModule and this call kept the generic copy.
      ("cross_module_call_specializes_defining_modules_function",
       [ ("Shapes", "module Shapes\n" + sumFn "vsum")
         ("Main", "module Main\nimport Shapes\n" + vec5 + "let s = Shapes.vsum(w5)\n") ],
       [ "vsum_shape_n5"; "__ri < 5" ],
       [])
      // Two importers, two shapes, two copies — and the generic copy survives
      // for anything that pins nothing.
      ("cross_module_two_shapes_two_specs",
       [ ("Shapes", "module Shapes\n" + sumFn "vsum")
         ("Main", "module Main\nimport Shapes\n" + vec5 + vec3
                  + "let a = Shapes.vsum(w5)\nlet b = Shapes.vsum(w3)\n") ],
       [ "vsum_shape_n5"; "vsum_shape_n3" ],
       [])
      // Going cross-module does not relax the per-NAME agreement gate: both
      // occurrences of `n` are pinned here, but to different literals (which
      // typechecks, since unify never compares extents). Baking either would
      // install a wrong bound on the other's loop, so the call declines.
      ("cross_module_disagreeing_occurrences_decline",
       [ ("Shapes", "module Shapes\n"
                    + "function vsum2(u: Array<Float64 like Idx<n>>, v: Array<Float64 like Idx<n>>) -> Float64 =\n"
                    + "    reduce(u, (+)) + reduce(v, (+))\n")
         ("Main", "module Main\nimport Shapes\n" + vec5 + vec3
                  + "let s = Shapes.vsum2(w5, w3)\n") ],
       [ "u.extents[0]" ],
       [ "vsum2_shape" ])

      // ---- Recursion ----
      // Self-recursive and SHAPE-PRESERVING: `a` is forwarded to the recursive
      // call unchanged, so the signature is closed under the recursion and the
      // spec's own recursive call targets the spec.
      ("self_recursion_shape_preserving_specializes",
       [ ("Main",
          "function rec_sum(a: Array<Float64 like Idx<n>>, k: Int64) -> Float64 =\n"
          + "    if k <= 0 then 0.0 else reduce(a, (+)) + rec_sum(a, k - 1)\n"
          + vec5 + "let s = rec_sum(w5, 2)\n") ],
       [ "rec_sum_shape_n5"
         // the baked bound, and the recursive call rewritten to the spec
         "__ri < 5"; "rec_sum_shape_n5(a," ],
       [])
      // Self-recursive and extent-CHANGING: the recursive call hands over a
      // DIFFERENT array, so the signature is not closed and chasing it would
      // install a bound the array does not have. Must still decline.
      ("self_recursion_extent_changing_declines",
       [ ("Main",
          vec3
          + "function shrink(a: Array<Float64 like Idx<n>>, k: Int64) -> Float64 =\n"
          + "    if k <= 0 then 0.0 else reduce(a, (+)) + shrink(w3, k - 1)\n"
          + vec5 + "let s = shrink(w5, 2)\n") ],
       // the surviving generic copy still reads its bound at runtime
       [ "a.extents[0]" ],
       [ "shrink_shape" ])

      // ---- Name provenance (the collision this increment closes) ----
      // `scale` and `f` both write `Idx<n>`; they are the same placeholder in
      // the IR. `f` specialized at n = 5 would bake 5 into the type of a local
      // holding `scale(w3)` — a 3-cell array — and emit a 5-iteration loop over
      // it. `f` must decline; `scale` (whose own signature IS pinned) must not.
      ("foreign_extent_name_declines_but_callee_still_specializes",
       [ ("Main",
          vec3
          + "function scale(A: Array<Float64 like Idx<n>>) -> Array<Float64 like Idx<n>> = A * 2.0\n"
          + "function f(B: Array<Float64 like Idx<n>>) -> Float64 = {\n"
          + "    let z = scale(w3)\n"
          + "    reduce(z, (+)) + reduce(B, (+))\n"
          + "}\n"
          + vec5 + "let v = f(w5)\n") ],
       [ "scale_shape_n3" ],
       [ "f_shape" ]) ]

let private runEmissionCase
        ((name, sources, mustContain, mustNotContain)
            : string * (string * string) list * string list * string list) =
    match (if List.length sources = 1 then cppOfSource name (snd sources.[0]) else cppOfModules name sources) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let missing = mustContain |> List.filter (fun s -> not (cpp.Contains s))
        let present = mustNotContain |> List.filter cpp.Contains
        if missing.IsEmpty && present.IsEmpty then
            resultLine Pass name ""
            true
        else
            let parts =
                (if missing.IsEmpty then [] else [sprintf "missing: %s" (String.concat " | " missing)])
                @ (if present.IsEmpty then [] else [sprintf "unexpected: %s" (String.concat " | " present)])
            resultLine Fail name (String.concat "; " parts)
            false

// ---------------------------------------------------------------------------
// Cap plumbing
// ---------------------------------------------------------------------------

/// Five distinct call-site shapes against one function. The cap decides how
/// many copies get made; every site not specialized keeps the generic copy, so
/// only the COUNT changes, never the values.
let private capProbeSource =
    sumFn "vsum"
    + ([1 .. 5]
       |> List.map (fun k ->
            let elems = [1 .. k] |> List.map (fun i -> sprintf "%d.0" i) |> String.concat ", "
            sprintf "let v%d: Array<Float64 like Idx<%d>> = [%s]\nlet s%d = vsum(v%d)\n" k k elems k k)
       |> String.concat "")

/// How many `vsum_shape_n<k>` DEFINITIONS the emitted program holds.
let private specCountOf (cpp: string) =
    [1 .. 5] |> List.filter (fun k -> cpp.Contains (sprintf "double vsum_shape_n%d(Array" k)) |> List.length

let private runCapCase (name: string) (envValue: string option) (expected: int) =
    use _cap = pinEnv "BLADE_SHAPE_SPEC_CAP" envValue
    match cppOfSource name capProbeSource with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let got = specCountOf cpp
        if got = expected then
            resultLine Pass name (sprintf "%d spec(s)" got)
            true
        else
            resultLine Fail name (sprintf "expected %d spec(s), got %d" expected got)
            false

let runShapeSpecTests () =
    printHeader "Blade-DSL: Shape Specialization Tests"
    // The cap is ambient state for every case here, so neutralize any inherited
    // export before the emission block runs against the default.
    use _neutral = pinEnv "BLADE_SHAPE_SPEC_CAP" None
    let emission = emissionCases |> List.map runEmissionCase
    let caps =
        [ // unset -> the measured-safe default of 4
          runCapCase "cap_default_is_four" None 4
          // a lower cap is honoured verbatim
          runCapCase "cap_env_lowers_to_two" (Some "2") 2
          // 5 sites, cap 8: every site gets its copy
          runCapCase "cap_env_raises_to_eight" (Some "8") 5
          // "0" is NOT unlimited -- it clamps to 64, which for 5 sites is 5
          runCapCase "cap_zero_clamps_rather_than_unlimited" (Some "0") 5
          // garbage falls back to the default rather than to no cap at all
          runCapCase "cap_unparseable_falls_back_to_default" (Some "banana") 4 ]
    let results = emission @ caps
    let passed = results |> List.filter id |> List.length
    let failed = results.Length - passed
    printFooter "Shape Specialization" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "Shape Specialization"; Passed = passed; Failed = failed; Skipped = 0
      FailedNames = if failed = 0 then [] else ["see above"] }
