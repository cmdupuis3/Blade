VERDICT: DO NOT MERGE

Reviewed `667250cd..0bc965eb`; source/git inspection only. No builds, tests, emission commands or executions. Reproducers below are source-derived, not executed. `git diff --check` is clean. File:line references are at `0bc965eb` unless qualified.

1. **BLOCKER — the unchanged interpreter violates the newly documented contract for a compound set and a writing target.** `src/Interp/Loops.fs:2491-2498` calls `compoundToDense` BEFORE evaluating the target. That helper (`src/Interp/ArrayOps.fs:1713-1719`) reads every compact cell and constructs a fresh store. C++ now binds the set by reference, evaluates the target, then reads the original buffer (`src/CodeGenExpr.fs:572-615`); LLVM likewise obtains the pool, evaluates the target, then scans (`src/EmitLlvm.fs:3020-3071`). Thus this legal mutation shape, already supported by `index-types/520`, gives interpreter `false` versus compiled `true`:

   ```blade
   let ids = [1, 2, 3]
   let keep = mask(ids, lambda(v) -> v > 0)
   let mut B = compound(ids, keep)
   function replace(a: mut Array<Int64 like CompoundIdx<keep>>) -> Int64 = {
       a(0) = 9
       9
   }
   let hit = contains(B, replace(B))
   ```

   Hoist admission cannot repair this: it is an ordinary scan. **Fix:** in interpreter `IRContains`, retain the evaluated compound descriptor, evaluate the target once, then compare its compact cells directly; do not snapshot through `compoundToDense`. Audit the analogous `sparseToDense` branch. Add a three-lane pin for this mutation, including a target that mutates a different kept cell and misses/hits. Under the stated target-before-cell-read contract, `hit` must be `true`. The old scalar-counter pin never observes this snapshot.

2. **HIGH — “stored” is not proved through aliases; C++ can build before the set exists.** `src/Optimize.fs:4357` only tests `isDeferredForm sb.Value`. That predicate (`:912-924`) returns false for `IRVar`; `keyClass` (`:4229-4254`) also accepts `ArrayElem` without checking `IsVirtual`. Meanwhile `src/CodeGenBinding.fs:6126-6129` deliberately propagates deferral through an alias and emits only a comment. Example:

   ```blade
   type Three = Idx<3>
   let lazy = method_for(range<Three>) <@> lambda(i) -> Int64(i) + 1
   let B = lazy
   let src = [1, 4, 3]
   let flags = method_for(src) <@> lambda(v) -> contains(B, v) |> compute
   ```

   The planner admits `B` and `flags`. `memberHoistBracket` emits a read of `B.data` before all host code (`src/CodeGen.fs:1978-1988,2201-2205`), but `genComputeBinding` materializes the deferred capture inside that host's prelude (`src/CodeGenBinding.fs:1307-1309`). This creates a use before declaration/materialization; gate-off retains the existing capture-force path. LLVM's pool guard silently skips the build instead (`src/EmitLlvm.fs:8168-8186`), despite an applied planner decision. This differs from the reported pre-existing unforced-mask defect: use a compute host, which already forces captures.

   **Fix:** establish storage positively at the build point, chasing alias chains and virtual forms; decline deferred aliases in v1. Add a fail-closed C++ emission guard against deferred/streamed/unavailable set storage, with a truthful decline record. Pin direct and multi-hop aliases with the gate on/off and both lanes; require no build before materialization. Do not fix it by moving an unproved producer earlier.

3. **HIGH — runtime representation changes are unrecorded.** `src/cpp/blade_member.h:154-194` silently changes bitmap to hash after `calloc` failure, and hash to linear after allocation/capacity failure. `memberChooseRep` and the literal plan record (`src/Optimize.fs:4069-4082,4272-4292`) report only the preferred representation. `blade_member_kind_i64` (`src/cpp/blade_member.h:231`) has no production reporting caller. Equal `(M,min,max,knob)` therefore need not give equal *actual* representations; the Boolean contract survives, but the plan/measurement claim does not. The CLI driver tests successful allocations, not these branches.

   **Fix:** label the plan's result as preferred, explicitly describe the allocation fallback, and record actual kind plus fallback reason after each build in both lanes' runtime/run-record diagnostics. Add deterministic allocator-failure injection tests for bitmap failure and either hash allocation failing, checking equality, cleanup, and the reported route. Keep the probe read-only; report from the build/caller.

4. **HIGH — several central pins are route-less.** `tests/LlvmTests.fs:1225-1240` accepts any successful LLVM emission, including an entirely linear scan. The C++ build/probe/free needles (`tests/OptimizeTests.fs:3699-3749`) cover one dense mask fixture. Corpus 014/017/019/020 and the CLI multi-host output comparison can pass if their hoists disappear. In particular, 018 places membership in the MAP kernel of a fold, not the fold kernel advertised in the coverage summary. Compound 020 does not demonstrate a positions-free set: `positionsDemand` requires positions for an uncertified `IRContains` operand and for a capture (`src/Optimize.fs:3373-3376,3425-3426`), and its inline mask is not the named-mask fusion fixture.

   **Fix:** pair the actual corpus fixtures with per-host C++ and LLVM emission assertions: build before host, probe inside host, free afterward; negative cases require no build/probe. Cover empty compound, failing target, OMP map, a genuine fold-kernel probe, and compound cardinality/data access. For positions-free coverage, either add a narrowly justified census admission plus a joint-plan fixture asserting positions elision AND membership use, or explicitly record that this combination is currently unreachable and stop claiming it is tested. Add the alias and mutation fixtures from findings 1–2.

Contract/history check (a/g): formalism §6.5 (`docs/formalism.md:1478-1485`) delegates relational semantics to `docs/features/sql.md`; the new paragraph at `sql.md:232` records the once-target decision. There is no new paragraph in formalism itself, but this is not merely a pin flip. The baseline specification did not explicitly settle multiplicity/order; the builder brief selects the interpreter's once-target behavior, making the old C++ per-cell evaluation the defect being repaired. Files 015/016 are NEW in this diff, not repinned pre-existing tests. Baseline corpus/examples/stdlib searches found no effectful/failing targets: existing targets are literals or simple variables. Thus the supplied baseline 27/false observations concern new probes, not an old harness assertion of those values. Finding 1 qualifies the claim that the interpreter already implements the whole chosen contract.

Hook verification (five hooks):

| Hook | Exact locations | Static result |
|---|---|---|
| Target evaluation / old unwired path | `src/CodeGenExpr.fs:572`; `src/Interp/Loops.fs:2487`; `src/CodeGenExpr.fs:2249`; `src/CodeGenLoopNest.fs:37` | C++ set then target before scan; interpreter compound copy is the exception. Mask still calls by name; new routing is the side table/global, not SubstMap. The “any contains … linear scan” comment at CodeGenExpr:2251 is now stale. |
| Admission and registration | `src/Optimize.fs:4121,4371-4427`; `src/Lowering.fs:3193` | Shared write predicate covers producer payload, target-containing kernels, folds/seeds, complete host payload, and callback candidates; no interval between build and host. Storage proof fails for aliases (2). Mutable capture reads alone do not invalidate a stored-set lookup; writes decline. |
| C++ placement / lifetime | `src/CodeGen.fs:1963,2197,2315,3856`; `src/cpp/blade_member.h:123` | Build precedes host/team; free follows its completion and zeroes kind. Outside that window the probe scans. No positions access: rank-1 compound uses data/cardinality. |
| Shared chooser / shim | `src/cpp/blade_member.h:102,131,204`; `src/cpp/blade_llvm_shim.c:543`; `src/Build.fs:1268` | Unsigned differences/bit offsets handle negatives and extremes; duplicates and independent occupancy are sound. Checked capacity/byte bounds fall back. Header is deployed and hashed. Actual allocation fallback is unrecorded (3). |
| LLVM lowering / lifetime | `src/EmitLlvm.fs:2999,329,8167,8196`; `src/cpp/blade_llvm_shim.c:82` | Real scan/probe arm; `_has` only reads, so `memory(read)` fits. Normal completion finalizes. Panic exits the process, bypassing fini: exit-time leak, no later dangling probe. |

Composition/knob check (d/f): membership planning follows compaction planning. A directly deferred host declines; an inline compound mask is bracketed at the compound. Current compaction `totalScalar` refuses contains/captures (`src/Optimize.fs:3256-3269,3486`), so a membership predicate does not secretly migrate into the earlier fused mask pass. The gate is registered (`:167`), cleared/restored through `suiteClearedKnobs` (`src/CliSelfTests.fs:96`), and forced values appear in plan evidence. `CLAUDE.md:205` describes those settings and fallback, but “deterministically” applies only to the preferred chooser, not the actual allocation-dependent representation.

(i) No contains-free C++/.ll emission change identified by static diff inspection: empty plan tables add no text and new state cells do not consume IDs; LLVM shim/header contents and cache identity DO change globally. Byte identity was not executed or measured.