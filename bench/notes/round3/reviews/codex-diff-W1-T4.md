VERDICT: DO NOT MERGE

Reviewed 66e04875..9012128c. Read-only source/git review; no builds, emission, tests, executables, or timings run. Reproducers below are source-derived probes, not executed results. File:line references are in the builder worktree at 9012128c.

1. **BLOCKER — an imported lookup hidden in RandomInits gets a positions-free compound.** `src/Lowering.fs:3086-3094` collects foreign references from binding values, function bodies, CompoundInits and ProviderWrites only. RandomInits and SparseInits are absent. A random binding's executable key is moved into RandomInits and its binding value becomes IRUnit (`:2455-2474`). Importing a variable creates an environment mapping, not an alias binding (`:2267-2271`). The exporting module's census therefore sees no reference; its local RandomInits scan (`Optimize.fs:3195-3202`) cannot see the importing module.

   Minimal two-file probe:
   
   ```blade
   // Data.blade
   module Data
   let x = [12345]
   let keep = mask(x, lambda(v) -> v > 0)
   let y = compound(x, keep)
   ```

   ```blade
   // Main.blade
   module Main
   import Data
   import rand as r
   let u = r.uniform(Data.y(0), 1)
   ```

   B4 builds positions and supplies key 12345. V2 elides them; `CodeGenBinding.fs:2048` renders the key expression at the fill, and the keyed Compound read reaches `index_types.h:483-484` → `:472` → BL9002. This is a valid-program runtime ICE, not an unreachable assertion. This particular probe is C++: LLVM explicitly refuses RandomInits (`EmitLlvm.fs:7471`), so requesting LLVM can fall back to the same defective C++.

   **Fix/probe:** compute foreign demand from ALL executable side-table expressions and all relevant type/index expressions, using one complete module traversal shared with the local census. Include random key, parameters, weights, address, FillModulus, SparseInits and provider dependencies. Add this multifile regression and variants with a derived map exported as the key source. Pin b4/default output identity and a REQUIRED-positions decision; repeat with `sel_cap=0` to isolate T4b. Multifile/037 exercises an ordinary binding reference and misses this case.

2. **BLOCKER — the “between” party audits placeholders, allowing T4a across a real mutation.** `Optimize.fs:3442` calls W/F only on `b.Value`; the inherited B4 check at `:3265-3267` does the same. RandomInits and CompoundInits execute hidden operands at that binding's site, while their values are IRUnit (`Lowering.fs:2468,2488`). W is genuinely called, but on the wrong expression. `scheduleMayWriteIn`'s initializer map also contains only those placeholders (`Optimize.fs:1455-1456`).

   Probe:
   
   ```blade
   import rand as r
   type Ix = Idx<2>
   function bump(a: mut Array<Float64 like Ix>) -> Int64 = {
       a(0) = 9.0
       7
   }
   let mut x: Array<Float64 like Ix> = [1.0, 2.0]
   let keep = mask(x, lambda(v) -> v > 0.0)
   let noise = r.uniform(bump(x), 1)
   let y = compound(x, keep)
   let s = reduce(y, (+))
   ```

   Source-derived outcomes: off/B4 gather after `bump`, giving `y = [9.0, 2.0]`, `s = 11.0`; v2 gathers at keep, giving `y = [1.0, 2.0]`, `s = 3.0`. The mask remains all true, so this isolates the newly moved gather rather than a pre-existing stale-count problem. The named y store contains the wrong snapshot. The source initializer is a literal, the predicate is total, no fold is fed, and the intervening noise placeholder passes all recorded checks.

   **Fix/probe:** audit a binding's actual execution payload, including side tables, with W/F and mask-reference collection. Feed that payload to the shared initializer resolver too. For this case decline T4a to B4; do not disable otherwise legal B4. Add a second probe with `compound(bump_returning_array(x), other_keep)` between keep and y, using a DISTINCT mask so shared-mask refusal cannot hide the bug; this also targets the compound side table and supported LLVM paths. Add a provider-write/read barrier probe and an imported mutating helper variant.

3. **MEDIUM — the traffic claim conflates executed traffic with a cache model.** `CodeGenExpr.fs:2278-2287` emits two x reads per source cell: one in the predicate loop and another in the gather loop. It also rereads keep and performs a y store for EVERY source cell, including rejects. LLVM does the same (`EmitLlvm.fs:4394-4410`). “m+1 touched cells” does not mean m stores.

   For Float64, n source cells and m kept cells, counting one load per self-square operand value, B4's licensed loops execute approximately `34n + 8m` bytes (x twice, keep write/read, n y stores, n position stores, fold read); positions-elided v2 executes `26n + 8m`. At m≈n/2 these are about 38→30 MB, not 30→17 MB. The smaller published numbers model retained cache lines / traffic beyond L1 and omit repeated writes and cached reads. Repro's chain reads are likewise real operations even when cache-resident.

   **Fix/probe:** publish executed scalar-cell loads/stores separately from touched capacity, modeled cache/memory traffic and measured peak working set. Label 30→17 / 26→13 as cache-model estimates with assumptions; do not call them logical bytes counted from emitted loops.

**Demand audit (a).** “Required” below describes this census, not whether the primitive inherently needs positions.

| Consumer | Census / runtime consequence |
|---|---|
| Direct keyed read; keyed read through a tracked elementwise map | Required (`Optimize.fs:3136`); maps share the input index. |
| Alias, let-mut copy, assignment, tuple then projection, returned value, ordinary callable argument | Required at the alias/assignment/tuple/call/uncertified-variable occurrence (`:3137-3148,3179`). No escape analysis needs to follow the subsequent lookup. |
| Capture into a kernel, including one performing keyed reads | Required via capture lists or body references (`:3182-3189`), including captures that only query extents. |
| `<|:>` / present, a prefix/index consumer explicitly naming y | Required by the generic operand rule. Rank-1 compound partial/prefix indexing is not a supported surface read; the C++ prefix helper is nevertheless guarded. |
| Coordinate enumeration / unhash / halo over `CompoundIdx<keep>` | A range constructs its OWN index from keep (`CodeGenCuda.fs:3034-3069`, `CodeGenLoopNest.fs:259-290`); it does not borrow y's elided table. An explicit y operand is uncertified and requires positions. |
| Printing y or a tracked map | Correctly elided: `CodeGen.fs:3133-3136` prints `.size()` cells of `.data`; it does not unhash. `Compound::size()` uses cardinality × trailing_stride. |
| `extents(y)`; ordinary scalar folds | Correctly elided: rank-1 extent is m, not dense extent n; folds walk stored values. Both n and m remain in the C++ index. |
| unique / intersect / union, sort, group_by | Required conservatively: their y operand reaches the generic rule. Positions-free set walks added by W1-3 do not themselves authorize this census to elide; this commit is not that sibling implementation. |
| Provider output; local CompoundInits / SparseInits / RandomInits operands | Required when they mention a tracked value (`:3190-3204`). |
| Imported reference | Required for ordinary bodies/CompoundInits/provider writes; **wrongly elided for the hidden foreign random key in finding 1**. |
| Type extents / unknown uses | Binding and function-signature extent expressions are scanned. The blanket “all types/all side tables” claim is too broad: foreign types are absent from moduleRefs, and generic childrenOf deliberately excludes embedded index types (`IR.fs:2200-2202`). Add type-only and local-expression-type probes. |

Additional narrow census probe: `derivedMap` (`Optimize.fs:3118-3122`) checks output rank zero but never checks scalar INPUT ranks or that the output preserves the compound axis. Exercise `object_for(at0) <@> y |> compute` with a whole-array typed kernel performing `c(0)`. Require positions for any nonzero/unknown kernel input rank. I am not claiming a runnable B4→v2 regression for that probe: zero-iteration kernel emission itself needs checking; findings 1–2 do not depend on it.

**Guard and legality checks (b–f).**

- BL9002 is the diagnostic registry's “internal codegen invariant violated” (`Diagnostics.fs:536,597`), so it is a defensible defensive ICE code even though g++ rejection is its usual documented use. It is not an acceptable semantic fallback. `blade_runtime.hpp:397-403` preserves that code at runtime. Finding 1 disproves unreachability. Fix demand; retain the guards.
- In admitted predicates, a mid-block Blade panic is excluded by `totalScalar` (`Optimize.fs:3021-3038`): integer division/modulo/power, checked casts, calls and captures are rejected. Predicate-panic probes should assert DECLINED, including a failure after cell 256. Pin 528 alone does not establish that refusal.
- In the absence of finding 2, predicate and keep writes occur once in ascending cell order; branchless y writes occur n times, including slack overwrites, exactly as B4. The interleaving changes intentionally. Both strict-chain helpers use the same 256 SOURCE-cell blocks, first-kept-square peel, ascending completed-cell feed and product-rounding barrier; no chain reassociation was found. BL8003 remains at the fold.
- The runtime cap decision precedes the first block/allocation; fallback never resumes a partially fed chain. It uses untouched initialized accumulators and fresh m+1 buffers. Byte admission uses division; static admission is n < capCells, dynamic admission tests the same relation.
- Explicit seeds are excluded at `Optimize.fs:3330`; “seeds: none” is accurate for planned folds. Ordinary source aliases/views are covered by the shared transitive W analysis, but hidden initializer payloads are not (finding 2). A source-language read of y before its declaration is unavailable. The pass adds no parallel callback body; unresolved callback effects are conservative.
- Opt-diff's mirror really compares default vs default-with-b4 under REPRO=1 and CONTRACT=off (`tests/OptDiff.fs:142,180-188`); compilation remains inside that environment. Other optimizer gates have the same unset defaults. `suiteClearedKnobs` includes optimizerGates and clears them to NULL/unset, which selects v2, not off (`CliSelfTests.fs:96,118`). No mirror-knob mismatch found.
- `git ls-files --eol src/cpp/index_types.h` reports `i/crlf w/crlf`, not mixed. The constructor/tag/guards are unconditional host C++17 code, with <stdexcept> already included; they are not hidden behind GCC-only macros. They are parsed by MSVC and nvcc host compilation wherever the header is included. No MSVC/CUDA compile was performed or inferred successful.

**Five hook checks (h).**

| Claim | Hook at 9012128c | Finding |
|---|---|---|
| Complete positions demand / imported references | `Optimize.fs:3109`; `Lowering.fs:3086` | Local census present; foreign coverage incomplete, finding 1. |
| Mask-site gather, both named stores retained | `CodeGenExpr.fs:2278`; `CodeGenBinding.fs:2378` | Block predicate/gather emitted; compound adopts buffers. Motion barrier incomplete, finding 2. |
| LLVM twin / same strict-chain blocks | `EmitLlvm.fs:4255,4337,7868` | Shared block/chain helper and guarded fallback present; not executed. |
| Cardinality-only index and lookup guards | `src/cpp/index_types.h:443,468,471,483,491,498` | n/m preserved; all listed lookup entry points guard; reachable guard in finding 1. |
| b4 differential mirror | `Optimize.fs:174`; `tests/OptDiff.fs:180` | Registered and scheduled against defaults in identity regime. |

**T4a decision (g): DROP from the default for this round; retain corrected T4b.** The stated independent 0.18–0.36 ms gain meets the brief's explicit <0.4 ms drop condition. “v2 fastest overall” does not override that condition, particularly without allocation-inclusive runner results and peak working set. Reconsider only after the harness A/B supplies contrary evidence under an explicitly revised acceptance decision.

**Shared-mask defect (i): confirmed; b4 still records “applied” for the overwritten first plan.** `CompactionTable.record` keeps one plan per mask; `Optimize.fs:3290-3307` deliberately repairs the record only for v2. This is historical reporting compatibility, not an honest b4 plan; correcting its diagnostic would not alter round-2 emission.

