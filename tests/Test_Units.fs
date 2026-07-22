// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Units

open Blade.Tests.Corpus

/// Unit of measure tests
let unitTests = category "units"

/// Negative tests: should fail type checking. Currently unreferenced;
/// preserved as corpus assets for a future expected-error runner.
let unitErrorTests = category "unit-errors"
