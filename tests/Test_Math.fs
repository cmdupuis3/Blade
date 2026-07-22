// Test sources live on disk in tests/corpus (see Test_Static.fs). This
// module only names the category; edit the .blade files to change tests.
module Blade.Tests.Math

open Blade.Tests.Corpus

/// Math-module elaboration tests (matmul/svd/eigh/unfold/mode_product/hosvd)
let mathTests = category "math"
