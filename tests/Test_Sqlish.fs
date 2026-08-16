// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Sqlish

open Blade.Tests.Corpus
open Blade.Tests.TestHarness

/// Phase 1: foreign keys
let foreignKeyTests = category "sql-foreign-keys"

/// Phase 2: mask
let maskTests = category "sql-masks"

/// Phase 3: intersect / union
let setOpTests = category "sql-set-ops"

/// Phase 3.5: unique / contains — value-set primitives
let uniqueContainsTests = category "sql-unique-contains"

/// Phase 3.6: semijoin / antijoin pattern matcher
let semijoinTests = category "sql-semijoins"

/// Phase 4: group_by
let groupByTests = category "sql-group-by"

/// Phase 5: sort
let sortTests = category "sql-sort"

let reduceTests = category "sql-reduce"
let extentsTests = category "sql-extents"
let extentsMultiRankTests = category "sql-extents-multi-rank"
let regressionTests = category "sql-regressions"

/// Combined
let sqlCombinedTests = category "sql-combined"

/// Type-recovery regression guards — exercise pathways that two removed
/// CodeGen fallbacks once defended (IRFieldAccess scan + auto-fallback for
/// shape-bearing IRTUnit bindings). Should continue to typecheck and produce
/// verifiable values. If any start failing, the type pipeline has regressed.
let v24dProbes = category "sql-v24d-probes"

// ============================================================================
// Gather elision (pure codegen string checks; no toolchain, always run)
// ============================================================================
//
// A grouped array's only legal consumers are ragged peels, and a peel whose
// kernel touches its row solely through `extents(row)` reads only the row's
// LENGTH -- which comes from the gk offsets, not from the gathered buffer. So
// when every consumer is such a peel, `group_by` skips the per-group `new[]`
// and the O(n) copy and leaves the rows null (computeExtentsOnlyGroupBys).
//
// The corpus pins the VALUES on both sides of that decision; a value check
// cannot see the decision itself -- an elided and a gathered program print the
// same numbers, which is the whole point. These pin the EMISSION SHAPE, so an
// elision that silently stops firing (a correct-but-slow regression) is loud,
// and so is one that starts firing where a consumer really reads values --
// which would NOT be silent, but would be a segfault rather than a test.
let runGatherElisionTests () : BlockResult =
    printHeader "Group Gather Elision"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [name]
        resultLine Fail name detail
    let header =
        "let keys = [0, 1, 0, 2, 1, 0]\n\
         let vals = [10.0, 20.0, 30.0, 40.0, 50.0, 60.0]\n\
         let gk = group_keys(keys)\n\
         let g = group_by(vals, gk)\n"
    let sizesPeel =
        "let sizes = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> extents(r) |> compute\n"
    let sumsPeel =
        "let sums = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n"
    // The copy loop's two halves: the per-group allocation and the element
    // store. Matched on the emitted spelling with indentation stripped.
    let perRowAlloc = "g[__g] = new double[__sz];"
    let copyStore = "g[__g][__k] ="
    let nullRows = "g[__g] = nullptr;"
    let cases =
        [ // Sole consumer reads only extents -> gather is dead.
          "extents-only peel elides the gather",
          header + sizesPeel,
          [nullRows], [perRowAlloc; copyStore]
          // A second consumer reads VALUES -> the gather is live. This is the
          // load-bearing direction: eliding here emits a null dereference.
          "a values-reading consumer keeps the gather",
          header + sizesPeel + sumsPeel,
          [perRowAlloc; copyStore], [nullRows]
          // Values-only consumer: nothing to elide, unchanged behaviour.
          "a values-only peel is untouched",
          header + sumsPeel,
          [perRowAlloc; copyStore], [nullRows]
          // `extents(gk)` needs no grouped array at all -- the direct spelling
          // allocates nothing per group and copies nothing.
          "extents(gk) emits no group_by gather at all",
          "let keys = [0, 1, 0, 2, 1, 0]\n\
           let gk = group_keys(keys)\n\
           let sizes = extents(gk)\n",
          ["sizes[__g] = (int64_t)(gk__offsets[__g + 1] - gk__offsets[__g]);"],
          [perRowAlloc; copyStore; "new double*"] ]
    for (name, src, mustContain, mustNotContain) in cases do
        match Blade.Tests.Functions.cppOf "gather_elision" src with
        | Error e -> fail name e
        | Ok cpp ->
            let flat =
                cpp.Split('\n') |> Array.map (fun l -> l.TrimStart()) |> String.concat "\n"
            let missing = mustContain |> List.filter (fun s -> not (flat.Contains s))
            let present = mustNotContain |> List.filter flat.Contains
            if not missing.IsEmpty then
                fail name (sprintf "generated C++ lacks: %s" (String.concat " | " missing))
            elif not present.IsEmpty then
                fail name (sprintf "generated C++ still contains: %s" (String.concat " | " present))
            else
                passed <- passed + 1
                resultLine Pass name "emission shape as expected"
    { Block = "Group Gather Elision"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }
