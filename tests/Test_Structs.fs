// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Structs

open Blade.Tests.Corpus

/// Struct tests
let structTests = category "structs"

/// Tests that should abort at runtime (constraint violation). Each test
/// name carries the "(aborts)" suffix: the runner expects compile success
/// followed by a nonzero exit, with the message pinned by // ABORT:.
let structAbortTests = category "struct-aborts"

/// Mutually constrained alias groups (`type P1 = T and P2 = T where ...`):
/// joint annotated bindings, declared-return introduce-sites, and the
/// annotation-misuse compile errors.
let structMutualTests = category "struct-mutual"
