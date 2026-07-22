// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Mutability

open Blade.Tests.Corpus

/// Tests that should pass
let mutabilityTests = category "mutability"

/// Tests that should fail with a type error. Currently unreferenced;
/// preserved as corpus assets for a future expected-error runner.
let mutabilityErrorTests = category "mutability-errors"
