// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.IndexTypes

open Blade.Tests.Corpus
open Blade.Tests.TestHarness

/// Index type tests (AntisymIdx, HermitianIdx)
let indexTypeTests = category "index-types"

// ============================================================================
// Array-instance subscript guards (pure codegen string checks; no toolchain)
// ============================================================================
//
// A `T^k` / `Float64^k` parameter the body reads at a computed position is
// re-checked as an ARRAY INSTANCE when a call hands it a NAMED axis
// (TypeEnv.FuncAxisSubscriptParams), so the read is guarded against the name
// -- or proven, and then unguarded. The corpus pins the values and the
// aborts (index-types/330-339); only the emission shows WHICH reads carry a
// guard: none for a proven position, none for an anonymous caller (formalism
// 3.10 does not cover anonymous axes, and the generic body stays as it was).

let runInstanceGuardEmissionTests () : BlockResult =
    printHeader "Array-Instance Subscript Guards"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name cond detail =
        if cond then
            passed <- passed + 1
            resultLine Pass name ""
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail
    let guardText = "index out of bounds: a position outside"
    let header =
        "type L = Idx<3>\n\
         let A: Array<Float64 like L> = [1.0, 2.0, 3.0]\n\
         let v = [1.0, 2.0, 3.0]\n"
    let cases =
        [ "a computed Int64 read through T^1 at a named axis is guarded", true,
          header + "function g(m: T^1, k: Int64) = m(k)\nlet s = g(A, 1)\n"
          "the same function called only with an anonymous array carries no guard", false,
          header + "function g(m: T^1, k: Int64) = m(k)\nlet s = g(v, 1)\n"
          "a Nat<L> parameter read fed by range<L> is proven: no guard", false,
          header + "function g(m: T^1, k: Nat<L>) = m(k)\nlet r = method_for(range<L>) <@> lambda(i) -> g(A, i)\nlet t = reduce(r, (+))\n"
          "a Nat<_> parameter read is not proven: guarded", true,
          header + "function g(m: T^1, k: Nat<_>) = m(k)\nlet r = method_for(range<L>) <@> lambda(i) -> g(A, i)\nlet t = reduce(r, (+))\n"
          "a forwarded T^1 read at a named axis is guarded", true,
          header + "function g(m: T^1, k: Int64) = m(k)\nfunction h(m: T^1, k: Int64) = g(m, k)\nlet s = h(A, 1)\n" ]
    for (name, wantGuard, src) in cases do
        match Blade.Tests.Functions.cppOf "instance_guards" src with
        | Ok cpp ->
            let has = cpp.Contains guardText
            check name (has = wantGuard)
                (if has then "the emitted C++ carries a subscript guard" else "the emitted C++ carries no subscript guard")
        | Error e -> check name false e
    { Block = "Array-Instance Subscript Guards"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }
