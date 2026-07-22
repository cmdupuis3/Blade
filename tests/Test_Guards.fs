// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Guards

open Blade.Tests.Corpus

/// Extended guard tests
let guardTests = category "guards"
let guardCombinatorTests = category "guard-combinators"
let zeroCombinatorTests = category "zero-combinators"
let sequenceCombinatorTests = category "sequence-combinators"
let tupleViewTests = category "tuple-views"
let replicateTests = category "replicate"
let anonRangeTests = category "anon-ranges"
let recursiveArrayTests = category "recursive-arrays"

/// stack / join — the rank-changing array-assembly combinators (formalism 2.6):
/// a fresh leading selector axis, and concatenation along a dimension.
let stackJoinTests = category "stack-join"
