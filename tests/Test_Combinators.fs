// Test sources live on disk in tests/corpus; this module only names the
// categories. The object_for combinator family beyond guards: zero, sequence,
// replicate, and the anonymous-range sugar.
module Blade.Tests.Combinators

open Blade.Tests.Corpus

let zeroCombinatorTests = category "zero-combinators"
let sequenceCombinatorTests = category "sequence-combinators"
let replicateTests = category "replicate"
let anonRangeTests = category "anon-ranges"
