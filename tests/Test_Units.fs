// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Units

open Blade.Tests.Corpus

/// Unit of measure tests
let unitTests = category "units"

/// Negative tests: should fail type checking. Not part of the main-suite
/// `allTests`; reachable via `blade test unit-errors`, which marks every
/// member as a reject-probe (their names carry no "(rejects)" of their own).
let unitErrorTests = category "unit-errors"
