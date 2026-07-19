// Test sources live on disk in tests/corpus (see Test_Static.fs). This
// module only names the category; edit the .blade files to change tests.
module Blade.Tests.Fallback

open Blade.Tests.Corpus

/// <|:> allocated-fallback tests (storage-keyed read-A-where-allocated-else-B:
/// dense nullptr-chain + compound-mask regimes, and the reject probes that
/// keep it distinct from value-keyed <|>)
let fallbackTests = category "fallback"
