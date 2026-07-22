// Test sources live on disk in tests/corpus (see Test_Static.fs). This
// module only names the category; edit the .blade files to change tests.
module Blade.Tests.Sgs

open Blade.Tests.Corpus

/// Subgrid-closure-discovery tests (Cartesian bridge, filters, exact SGS
/// stress as a comoment, equivariant-closure training).
let sgsTests = category "sgs"
