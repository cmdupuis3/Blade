// Test sources live on disk in tests/corpus; this module only names the
// category.
module Blade.Tests.StackJoin

open Blade.Tests.Corpus

/// stack / join — the rank-changing array-assembly combinators (formalism 2.6):
/// a fresh leading selector axis, and concatenation along a dimension.
let stackJoinTests = category "stack-join"
