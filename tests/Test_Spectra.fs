// Test sources live on disk in tests/corpus (see Test_Static.fs). This
// module only names the category; edit the .blade files to change tests.
module Blade.Tests.Spectra

open Blade.Tests.Corpus

/// Spectra-module elaboration tests (fft/ifft/power/polyspec)
let spectraTests = category "spectra"
