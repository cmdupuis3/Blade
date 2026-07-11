// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Structs

open Blade.Tests.Corpus

/// Struct tests
let structTests = category "structs"

/// Tests that should abort at runtime (constraint violation).
/// Currently unreferenced (the abort runner was deleted as dead code);
/// preserved as corpus assets for a future runner.
let structAbortTests = category "struct-aborts"
