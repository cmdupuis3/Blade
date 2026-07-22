// Test sources live on disk in tests/corpus (see Test_Static.fs). This
// module only names the category; edit the .blade files to change tests.
module Blade.Tests.Ppl

open Blade.Tests.Corpus

/// PPL moment-former elaboration tests (moments/comoments/independent)
let pplTests = category "ppl"
