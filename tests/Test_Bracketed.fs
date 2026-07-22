// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Bracketed

open Blade.Tests.Corpus

/// Bracketed (outer product) operator tests
let bracketedTests = category "bracketed"
