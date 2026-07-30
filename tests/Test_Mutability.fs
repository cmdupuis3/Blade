// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Mutability

open Blade.Tests.Corpus

/// Tests that should pass
let mutabilityTests = category "mutability"

/// Tests that should fail with a type error. Not part of the main-suite
/// `allTests`; reachable via `blade test mutability-errors`, which marks every
/// member as a reject-probe (their names carry no "(rejects)" of their own).
let mutabilityErrorTests = category "mutability-errors"
