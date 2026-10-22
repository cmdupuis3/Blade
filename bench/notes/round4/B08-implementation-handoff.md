# B08 diagonal initialization: frozen source handoff

Parent `d2f50db7f37fa581d71721bf3de834068850be1e`, lane `b-measurement`. Root's accepted boundary is integration `bench/notes/round4/B08-review.md`. This is one C++ materializer change, authored and **unexecuted**. Content pins are in `B08-implementation-manifest.json`; the original census, baseline inputs/request/manifest, saved emissions and B07 artifacts are unchanged.

`src/CodeGenExpr.fs:3026` now selects diagonal-only zero initialization for a sole rank-two antisymmetric group with literal positive extent, exact unitless `IRTScalar ETFloat64` element, nonvirtual storage and a stored `IRVar`/`IRParam` source. This does not infer fresh ownership from the name: the source is read-only. All other inputs retain the exact old full-zero fallback. There is no new environment gate or general initialization framework.

For each strict pair, `a=i` and `b=j-i-1` uniquely identify every `i<j`. The unchanged loops write the original source expression to `(i,j)`, then its unary negation to `(j,i)`, with exactly the original order and signs. Those disjoint images cover every off-diagonal cell. The diagonal is never a scatter target and is explicitly initialized to positive zero, including N1's empty scatter. No output is read or published during initialization. `genDecompactBinding` still forces the source before allocating the private destination; allocation/extents/row metadata, `MatPool` registration and matched cleanup are unchanged. No initializer read, deferred source failure or source mutation has moved across that forcing boundary.

The source recipe changes from `N*N + N*(N-1)` logical payload writes to `N + N*(N-1)`. N5 changes from 25 zero writes followed by 20 scatter writes to 5 followed by 20; N1 remains 1 followed by 0. Output storage capacity is unchanged. These are source-recipe counts, not observed native stores. Native dead-store elimination may already remove the overwritten zeros; no capacity, RSS, bandwidth or timing saving is claimed.

Authored coverage uses existing harnesses:

- `OptimizeTests.fs` adds 11 local materializer cases: N5, N1, stored parameter, F32/I64/unit-bearing F64, dynamic/zero extent, virtual, leading-group and nontrivial-expression fallbacks. They check route, original strict-pair order, signed copy expressions, allocation and static/owned-extents registration. Raw fallback probes do not claim those synthetic shapes are executable front-end programs.
- Four whole-program emission cases verify the real front-end/forcing route and explicitly selected `anti,result` print roots.
- New index-types 554–557 require every compact and dense value. N1 observes the positive-zero diagonal by reciprocal sign. The fixed mixed-zero N3 source checks a negative-zero source cell and the signs of **all nine** dense cells by reciprocal comparisons; numeric zero pins alone are insufficient. I64 is a full-output fallback. Existing 034/041/048/049/066/109 remain symmetric, rank-three/higher, Hermitian and deferred-input controls in the category. Prefixes were selected from integration's actual inventory, which ended at 553.

## Staged Luna request after root review

Luna is the sole executor. Freeze/verify all six source pins before any child call; save complete logs/exit codes and exact compiler/source/header identities. Use this lane's absolute checkout root as working directory and a fresh absolute `bench/.build/B08-implementation` log/artifact directory beneath it. Create a private absolute `temp` directory and set TEMP/TMP **before** build or compiler calls. Follow the recorded campaign-clean C++ environment; no `--print` on `test`. Stop on any failure or unexpected route, retain raw evidence and return to root. No full/default suite, LLVM expansion, FSI bridge, sanitizer or benchmark is requested.

1. Root selects the compiler containing this exact changed owner. The accepted pre-change C05 compiler is a baseline control and cannot validate this new recipe. If root schedules a lane build, the command is `dotnet build C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/Blade.fsproj -c Release`, from the lane root, with logs in the explicit output directory. Do not reuse a stale compiler or build another checkout implicitly.
2. With the root-approved absolute compiler executable in `$b08Compiler`, run `& $b08Compiler test optimize`. The 15 new PM07 route verdicts must be present and pass; record the complete existing block totals and any skips, not just the new names.
3. Emit the **unchanged frozen baseline inputs** with the new compiler using exactly these argument vectors (logs/exit codes saved under the private output root). Keep baseline emissions separate and immutable:

```powershell
& $b08Compiler emit 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/notes/round4/B08-census-inputs/pm07_anti_n5.blade' -o 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-implementation/n5/emit.cpp' --print 'anti,result'
& $b08Compiler emit 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/notes/round4/B08-census-inputs/pm07_anti_n1.blade' -o 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-implementation/n1/emit.cpp' --print 'anti,result'
```

Create the absolute `n5`/`n1` directories before dispatch. Inspect actual initialization followed by unchanged paired scatter, source forcing, extents/allocation/ownership, both complete print paths and cleanup against root's accepted baseline hashes. N5 must have only diagonal zero stores; N1 must retain its one diagonal. Record structural evidence with source ranges; do not translate emitted statements into native-store or performance claims.

4. Run `& $b08Compiler test index-types` using the existing native corpus verdict/pin machinery from the checkout root. Require 554–557's complete outputs and reciprocal sign witnesses, and the existing fallback/deferred controls; report full pass/fail/skip totals. `blade run` alone would not validate EXPECT pins. No interpreter/LLVM/native sweep is added by this request.

Return saved artifacts, source review and test totals to root, then stop. Root remains final reviewer; implementation acceptance and native/performance claims are pending independent evidence.
