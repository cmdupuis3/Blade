// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Sqlish

open Blade.Tests.Corpus

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
