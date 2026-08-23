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
/// works for the same reason: `IRMono.shapeSpecCap` re-reads the environment at
/// every consultation rather than freezing a module value.
let private pinEnv (name: string) (value: string option) =
    let prior = System.Environment.GetEnvironmentVariable(name)
    System.Environment.SetEnvironmentVariable(name, (match value with Some v -> v | None -> null))
    { new System.IDisposable with
        member _.Dispose() = System.Environment.SetEnvironmentVariable(name, prior) }

let private cppOfSource (testName: string) (src: string) : Result<string, string> =
    try
        match lower src with
        | Error e -> Error ($"lower: {e}")
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    with ex -> Error ($"codegen raised: {ex.Message}")

/// The multi-module twin. `lowerMultiSource` is the only door to a program with
/// more than one `IRModule`, which is the whole point of the cross-module case.
let private cppOfModules (testName: string) (sources: (string * string) list) : Result<string, string> =
    try
        match lowerMultiSource sources with
        | Error e -> Error ($"lower: {e}")
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    with ex -> Error ($"codegen raised: {ex.Message}")

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

/// A symbolic-extent function that ITERATES (the benefit gate wants a
/// loop-producing node) and returns a scalar, so nothing downstream depends on
/// the return shape.
let private sumFn (name: string) =
    $"function {name}(v: Array<Float64 like Idx<n>>) -> Float64 = reduce(v, (+))\n"

let private vec5 = "let w5: Array<Float64 like Idx<5>> = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
let private vec3 = "let w3: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n"

/// The fiberdot shape: a row-mapped kernel LAMBDA closing over a weight
/// vector. `p` is the outer (row) extent, `n` the inner (fiber) extent, and
/// the kernel's own loop runs over `n`. Before co-specialization that was the
/// one bound in the whole specialized nest still read from `.extents[]`: the
/// lambda is a separate `IRFuncDef`, reached from the spec's body only as a
/// value in the combinator's kernel slot, so the spec's type rewrite stopped
/// at the reference.
let private rowdotFn =
    "function rowdot(A: Array<Float64 like Idx<p>, Idx<n>>, w: Array<Float64 like Idx<n>>)"
    + " -> Array<Float64 like Idx<p>> =\n"
    + "    method_for(A) <@> lambda(row: Array<Float64 like Idx<n>>) -> prodsum(row, w) |> compute\n"

let private mat43 =
    "let M: Array<Float64 like Idx<4>, Idx<3>> ="
    + " [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0], [7.0, 8.0, 9.0], [1.0, 1.0, 1.0]]\n"

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
                (if missing.IsEmpty then [] else [$"""missing: {(String.concat " | " missing)}"""])
                @ (if present.IsEmpty then [] else [$"""unexpected: {(String.concat " | " present)}"""])
            resultLine Fail name (String.concat "; " parts)
            false

// ---------------------------------------------------------------------------
// Kernel-lambda co-specialization
//
// The block above asks whether a COPY was made. This one asks whether the copy
// is baked all the way DOWN, which needs a scoped assertion: "the spec's kernel
// loop reads a literal" is a claim about one function's body, and a whole-file
// `Contains` cannot make it -- the generic copy three lines above says the
// opposite and would satisfy either polarity of the same needle.
// ---------------------------------------------------------------------------

/// The text of one emitted C++ function DEFINITION, brace-matched from its
/// signature. The forward declaration carrying the same signature is skipped:
/// the definition is the occurrence whose line ends in `{`.
let private bodyOf (cpp: string) (signature: string) : string option =
    let rec findDef (from: int) =
        if from >= cpp.Length then -1 else
        let i = cpp.IndexOf(signature, from)
        if i < 0 then -1
        else
            let eol = match cpp.IndexOf('\n', i) with | -1 -> cpp.Length | k -> k
            if cpp.Substring(i, eol - i).TrimEnd().EndsWith "{" then i else findDef (i + 1)
    let start = findDef 0
    if start < 0 then None else
    let mutable depth = 0
    let mutable j = cpp.IndexOf('{', start)
    let mutable fin = -1
    if j < 0 then None else
    while fin < 0 && j < cpp.Length do
        (if cpp.[j] = '{' then depth <- depth + 1
         elif cpp.[j] = '}' then
             depth <- depth - 1
             if depth = 0 then fin <- j)
        j <- j + 1
    if fin < 0 then None else Some (cpp.Substring(start, fin - start + 1))

/// (name, sources, [(function signature, mustContain, mustNotContain)]).
/// Every assertion is scoped to one emitted function.
let private scopedCases
        : (string * (string * string) list
                  * (string * string list * string list) list) list =
    [ // The single-module fiberdot. Both bounds are literals inside the spec:
      // `4` from the spec's own parameter record, `3` from the CO-SPECIALIZED
      // kernel's. `.extents[` must not appear at all -- the peel writes
      // `A.extents + 1`, which is a pointer bump, not a bound read.
      ("lifted_kernel_lambda_bakes_the_inner_bound",
       [ ("Main", rowdotFn
                  + "let W: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n"
                  + mat43 + "let r = rowdot(M, W)\n") ],
       [ ("Array<double, 1> rowdot_shape_n3_p4(", [ "__i0 < 4"; "__pt < 3" ], [ ".extents[" ])
         // The generic copy is untouched: it still serves any call site that
         // pins nothing, and every one of its bounds is a runtime read.
         ("Array<double, 1> rowdot(", [ "A.extents[0]"; "A____i0.extents[0]" ], [])
         // …as does the generic kernel. The clone is PRIVATE to the spec.
         ("double __lambda_", [ "row.extents[0]" ], []) ])

      // The same shape across a module boundary: the kernel lambda lives in
      // the defining module (it was lifted out of a body there), the literal
      // comes from an importer, and the clone is placed beside its origin.
      ("cross_module_lifted_kernel_lambda_bakes_the_inner_bound",
       [ ("Fibers", "module Fibers\n" + rowdotFn)
         ("Main", "module Main\nimport Fibers\n"
                  + "let W: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n"
                  + mat43 + "let r = Fibers.rowdot(M, W)\n") ],
       [ ("Array<double, 1> rowdot_shape_n3_p4(", [ "__i0 < 4"; "__pt < 3" ], [ ".extents[" ])
         ("Array<double, 1> rowdot(", [ "A____i0.extents[0]" ], []) ])

      // PROVENANCE NEGATIVE. A source-level function used as a kernel declares
      // its OWN `Idx<n>` -- identically spelled, unrelated axis -- so its names
      // are not the caller's to bake, and baking them is the very
      // out-of-bounds class ff3ad88's gate exists to refuse. The enclosing
      // function still specializes; the kernel keeps its runtime bound.
      ("named_function_kernel_is_not_co_specialized",
       [ ("Main",
          "function krn(row: Array<Float64 like Idx<n>>) -> Float64 = reduce(row, (+))\n"
          + "function rowsum(A: Array<Float64 like Idx<p>, Idx<n>>) -> Array<Float64 like Idx<p>> =\n"
          + "    method_for(A) <@> krn |> compute\n"
          + mat43 + "let r = rowsum(M)\n") ],
       [ ("Array<double, 1> rowsum_shape_n3_p4(", [ "__i0 < 4"; "krn(A____i0)" ], [])
         ("double krn(", [ "row.extents[0]" ], []) ]) ]

/// Whole-file negatives, where the claim IS about the whole file.
let private absenceCases : (string * (string * string) list * string list) list =
    [ // NEGATIVE CONTROL. Every call site is symbolic (the forwarding wrapper
      // pins nothing), so no function specializes and therefore no kernel is
      // cloned -- co-specialization is strictly downstream of a spec.
      ("symbolic_call_site_clones_no_kernel",
       [ ("Main",
          rowdotFn
          + "function driver(B: Array<Float64 like Idx<q>, Idx<r>>, v: Array<Float64 like Idx<r>>)"
          + " -> Array<Float64 like Idx<q>> = rowdot(B, v)\n"
          + "let W: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n"
          + mat43 + "let r = driver(M, W)\n") ],
       [ "_shape" ])
      // A named kernel earns no clone under any name.
      ("named_function_kernel_earns_no_copy",
       [ ("Main",
          "function krn(row: Array<Float64 like Idx<n>>) -> Float64 = reduce(row, (+))\n"
          + "function rowsum(A: Array<Float64 like Idx<p>, Idx<n>>) -> Array<Float64 like Idx<p>> =\n"
          + "    method_for(A) <@> krn |> compute\n"
          + mat43 + "let r = rowsum(M)\n") ],
       [ "krn_shape" ]) ]

let private runScopedCase
        ((name, sources, checks)
            : string * (string * string) list * (string * string list * string list) list) =
    match (if List.length sources = 1 then cppOfSource name (snd sources.[0]) else cppOfModules name sources) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let problems =
            checks |> List.collect (fun (signature, mustContain, mustNotContain) ->
                match bodyOf cpp signature with
                | None -> [ $"no definition of `{signature}`" ]
                | Some body ->
                    (mustContain |> List.filter (body.Contains >> not)
                                 |> List.map (sprintf "`%s` missing %s" signature))
                    @ (mustNotContain |> List.filter body.Contains
                                      |> List.map (sprintf "`%s` unexpectedly holds %s" signature)))
        if problems.IsEmpty then resultLine Pass name ""; true
        else resultLine Fail name (String.concat "; " problems); false

let private runAbsenceCase
        ((name, sources, forbidden) : string * (string * string) list * string list) =
    match (if List.length sources = 1 then cppOfSource name (snd sources.[0]) else cppOfModules name sources) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        match forbidden |> List.filter cpp.Contains with
        | [] -> resultLine Pass name ""; true
        | present -> resultLine Fail name ($"""unexpected: {(String.concat " | " present)}"""); false

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
            let elems = [1 .. k] |> List.map (fun i -> $"{i}.0") |> String.concat ", "
            $"let v{k}: Array<Float64 like Idx<{k}>> = [{elems}]\nlet s{k} = vsum(v{k})\n")
       |> String.concat "")

/// How many `vsum_shape_n<k>` DEFINITIONS the emitted program holds.
let private specCountOf (cpp: string) =
    [1 .. 5] |> List.filter (fun k -> cpp.Contains ($"double vsum_shape_n{k}(Array")) |> List.length

let private runCapCase (name: string) (envValue: string option) (expected: int) =
    use _cap = pinEnv "BLADE_SHAPE_SPEC_CAP" envValue
    match cppOfSource name capProbeSource with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let got = specCountOf cpp
        if got = expected then
            resultLine Pass name ($"{got} spec(s)")
            true
        else
            resultLine Fail name ($"expected {expected} spec(s), got {got}")
            false

/// The same five shapes, but against a function whose body applies a lifted
/// kernel. Kernel clones ride the same cap: they are minted per (lambda,
/// signature), so a bound on the signatures is a bound on the copies, and the
/// cap stays the standing termination backstop for both worklists at once.
let private lambdaCapProbeSource =
    rowdotFn
    + ([1 .. 5]
       |> List.map (fun k ->
            let elems = [1 .. k] |> List.map (fun i -> $"{i}.0") |> String.concat ", "
            $"let w{k}: Array<Float64 like Idx<{k}>> = [{elems}]\n"
            + $"let m{k}: Array<Float64 like Idx<2>, Idx<{k}>> = [[{elems}], [{elems}]]\n"
            + $"let r{k} = rowdot(m{k}, w{k})\n")
       |> String.concat "")

/// How many co-specialized KERNEL definitions the emitted program holds. The
/// clone's name embeds its origin's synthesized id, which is not stable across
/// unrelated edits, so this counts definition lines by shape instead.
let private lambdaCloneCountOf (cpp: string) =
    cpp.Split('\n')
    |> Array.filter (fun l ->
        let l = l.Trim()
        l.StartsWith "double __lambda_" && l.Contains "_shape_" && l.EndsWith "{")
    |> Array.length

let private runLambdaCapCase (name: string) (envValue: string option) (expected: int) =
    use _cap = pinEnv "BLADE_SHAPE_SPEC_CAP" envValue
    match cppOfSource name lambdaCapProbeSource with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let got = lambdaCloneCountOf cpp
        if got = expected then
            resultLine Pass name ($"{got} kernel clone(s)")
            true
        else
            resultLine Fail name ($"expected {expected} kernel clone(s), got {got}")
            false

let runShapeSpecTests () =
    printHeader "Blade-DSL: Shape Specialization Tests"
    // The cap is ambient state for every case here, so neutralize any inherited
    // export before the emission block runs against the default.
    use _neutral = pinEnv "BLADE_SHAPE_SPEC_CAP" None
    let emission = emissionCases |> List.map runEmissionCase
    let scoped = (scopedCases |> List.map runScopedCase) @ (absenceCases |> List.map runAbsenceCase)
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
          runCapCase "cap_unparseable_falls_back_to_default" (Some "banana") 4
          // kernel clones honour the same cap, one per surviving signature
          runLambdaCapCase "cap_bounds_kernel_clones_too" (Some "2") 2
          runLambdaCapCase "cap_default_bounds_kernel_clones" None 4 ]
    let results = emission @ scoped @ caps
    let passed = results |> List.filter id |> List.length
    let failed = results.Length - passed
    printFooter "Shape Specialization" [$"{passed} passed"; $"{failed} failed"]
    { Block = "Shape Specialization"; Passed = passed; Failed = failed; Skipped = 0
      FailedNames = if failed = 0 then [] else ["see above"] }
