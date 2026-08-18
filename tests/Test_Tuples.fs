// Test sources live on disk in tests/corpus; this module only names the
// categories.
module Blade.Tests.Tuples

open Blade.Tests.Corpus

/// tuples — the tuple SURFACE layer of docs/plan-tuples-vs-arg-packs.md 6b
/// (Design C): the `Tuple<N>` width-only annotation and the bare-comma let
/// construction `let t = b, c`. Distinct from `tuple-views`, which is about
/// the tuple VIEWS a loop produces, not about how a tuple is written.
let tupleTests = category "tuples"
let tupleViewTests = category "tuple-views"
