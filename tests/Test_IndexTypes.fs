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
// guard: none for a proven position. An ANONYMOUS caller's read is checked
// too since G192 (1) (user decision 2026-11-09: formalism 3.10 covers
// anonymous slots), against the array's own extent in the generic body.

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
         let v = Float64([1.0, 2.0, 3.0])\n"
    let cases =
        [ "a computed Int64 read through T^1 at a named axis is guarded", true,
          header + "function g(m: T^1, k: Int64) = m(k)\nlet s = g(A, 1)\n"
          "the same function called only with an anonymous array is guarded too (G192 (1))", true,
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
    // F5: an integer EnumIdx observation reads the per-type static key table
    // (`blade_arith::key_table<...>::tab[p]`), not an O(n) if-chain per read.
    let enumSrc =
        "type R = EnumIdx<[100, 200, 300]>\n"
        + "let codes: Array<R like Idx<3>> = [300, 100, 200]\n"
        + "let a = codes <@> lambda(c) -> c + 1 |> compute\n"
    match Blade.Tests.Functions.cppOf "enum_key_table" enumSrc with
    | Ok cpp ->
        check "an integer EnumIdx observation reads the per-type key table (F5)"
            (cpp.Contains "blade_arith::key_table<100LL, 200LL, 300LL>::tab[" && not (cpp.Contains "== 1LL) ?"))
            "expected a key_table read and no position if-chain"
    | Error e -> check "an integer EnumIdx observation reads the per-type key table (F5)" false e
    // G192 (1): ANONYMOUS slots are proven or checked, in BOTH lanes. A
    // proven read -- an iteration index of a range whose static bounds lie in
    // the slot, a literal, a proven value of a named type that fits -- emits
    // no compare; anything else carries the guard (its message names the
    // array). The LLVM lane is read off its module text (the guard's panic
    // message is a string global there).
    let anonGuard = "a position outside the array"
    let anonHeader = "let v: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n"
    let llvmOf (name: string) (src: string) =
        match fst (Blade.Lowering.lowerCaptured src) with
        | Error e -> Error ($"lower: {e}")
        | Ok ir -> (try Blade.EmitLlvm.tryEmitProgramNamed name ir with ex -> Error ("raised: " + ex.Message))
    let anonCases =
        [ "anonymous slot: a 0..3 iteration index is proven (no compare)", false,
          anonHeader + "let r = method_for(0..3) <@> lambda(i) -> v(i) * 2.0 |> compute\n"
          "anonymous slot: a range<Idx<3>> iteration index is proven (no compare)", false,
          anonHeader + "let s = reduce(method_for(range<Idx<3>>) <@> lambda(i) -> v(i), (+))\n"
          "anonymous slot: a named Nat<L> of a fitting extent is proven (no compare)", false,
          "type L = Idx<3>\n" + anonHeader + "let s = reduce(method_for(range<L>) <@> lambda(i) -> v(i), (+))\n"
          "anonymous slot: a literal is proven (no compare)", false,
          anonHeader + "let s = v(2) + v(0)\n"
          "anonymous slot: a data subscript is checked", true,
          anonHeader + "let ks: Array<Int64 like Idx<2>> = [1, 2]\nlet r = method_for(ks) <@> lambda(k) -> v(k) |> compute\n"
          "anonymous slot: a 0..4 iteration index is checked (the range overruns the slot)", true,
          anonHeader + "let r = method_for(0..4) <@> lambda(i) -> v(i % 3) |> compute\n"
          "anonymous slot: a let-bound lambda fed by 0..3 and also called directly is checked", true,
          anonHeader + "let k = lambda(i) -> v(i)\nlet a = method_for(0..3) <@> k |> compute\nlet b = k(5)\n"
          "anonymous slot: a stored anonymous range read at data is checked", true,
          "let w = 10..13\nlet k: Int64 = 5\nlet r = w(k)\n"
          "anonymous slot: a halo window read at a literal offset in reach is proven", false,
          "let a: Array<Float64 like Idx<10>> = Float64(0..10) * 1.0 |> compute\nlet d = method_for(halo<Idx<10>, [-1, 0, 1]>) <@> lambda(w) -> a(w(1)) - a(w(-1)) |> compute\n"
          "anonymous slot: a halo window wider than the slot is checked (two halos sharing one tag)", true,
          anonHeader + "let r = method_for(halo<Idx<3>, [0]>, halo<Idx<5>, [0]>) <@> lambda(w, z) -> v(z(0)) |> compute\n"
          "anonymous slot: a reynolds kernel over 0..3 and 0..5 is checked (operands permute)", true,
          anonHeader + "let r = method_for(0..3, 0..5) <@> reynolds(lambda(i, j) where comm(i, j) -> v(i)) |> compute\n"
          "anonymous slot: a named Nat<L> wider than the slot is checked", true,
          "type L = Idx<4>\n" + anonHeader + "let s = reduce(method_for(range<L>) <@> lambda(i) -> v(i), (+))\n" ]
    for (name, wantGuard, src) in anonCases do
        match Blade.Tests.Functions.cppOf "anon_guards" src, llvmOf "anon_guards" src with
        | Ok cpp, Ok ll ->
            let has = cpp.Contains anonGuard
            let hasLl = ll.Contains anonGuard
            check name (has = wantGuard && hasLl = wantGuard)
                $"C++ guard {has}, LLVM guard {hasLl}, wanted {wantGuard}"
        | Error e, _ | _, Error e -> check name false e
    // A plain slot beside a SPARSE key (the LLVM lane refuses the sparse range
    // map; the C++ emission carries the guard).
    let sparseSrc =
        "let static K = [(0, 0), (1, 1)]\nlet s = method_for(range<SparseIdx<K>>, range<Idx<2>>) <@> lambda(i, j, k) -> Float64(k) |> compute\n"
        + "let js: Array<Int64 like Idx<2>> = [1, 0]\nlet r = method_for(js) <@> lambda(j) -> s((1, 1), j) |> compute\n"
    let sparseName = "anonymous slot: a plain slot beside a sparse key is checked (C++)"
    match Blade.Tests.Functions.cppOf "anon_sparse_guard" sparseSrc with
    | Ok cpp -> check sparseName (cpp.Contains anonGuard) "no guard in the C++ emission"
    | Error e -> check sparseName false e
    { Block = "Array-Instance Subscript Guards"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }
