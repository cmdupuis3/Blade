// Test sources live on disk in tests/corpus (see Test_Static.fs). This
// module only names the category; edit the .blade files to change tests.
module Blade.Tests.Rand

open Blade.Tests.Corpus

/// rand-module tests (uniform/normal: determinism, statistical moments)
let randTests = category "rand"
