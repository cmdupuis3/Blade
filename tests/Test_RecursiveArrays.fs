// Test sources live on disk in tests/corpus; this module only names the
// category. Recursive arrays: `let rec` structural induction on the leading
// axis (formalism §7.5) — running reductions, lag schemes, RK4, DP tables.
module Blade.Tests.RecursiveArrays

open Blade.Tests.Corpus

let recursiveArrayTests = category "recursive-arrays"
