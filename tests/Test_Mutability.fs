// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Mutability

open Blade.Tests.Corpus

/// Tests that should pass
let mutabilityTests = category "mutability"

/// Tests that should fail with a type error. Each member's name ends in
/// "(rejects)", which is what makes refusal the PASSING outcome (Runner's
/// isRejectProbe / classifyWithDetail), so this rides in `allTests` next to
/// `mutabilityTests` rather than needing an expected-error runner of its own.
/// Every file also carries an `// ERROR: BLxxxx` pin recording which code the
/// refusal is supposed to be.
let mutabilityErrorTests = category "mutability-errors"
