# B08: two C++ emissions, pending root dispatch

Source-only frozen inputs at lane HEAD `e85599718e81b6c2736191e7d886dfb887f5faef`. Root accepted the original census (`B08-census.md`) and integration `B08-review.md`. This request authorizes no production edit, native compilation/execution, test category, LLVM/FSI probe or benchmark. No command below has been executed by the coder.

Root selects the exact previously built compiler after its primary acceptance and source/provenance review; the fresh C05 compiler is a possible reuse, **not an assumed identity or a request for a new build**. Record its absolute executable path, SHA256, compiler source revision and root's approval. Compare the selected compiler's materializer/forcing/runtime sources with the actual byte hashes in `manifest.json`; document any root-approved owner-span equivalence rather than claiming different full files match. Stop if identity or equivalence is unapproved. Do not rebuild or silently substitute another compiler.

Input/manifest SHA256 (actual worktree bytes):

| File in this directory | SHA256 |
|---|---|
| `pm07_anti_n5.blade` | `0FD86F6D1D82FE75B2D92376895F0FEACD676A8007B8FA3886C2E1094FFBC4AB` |
| `pm07_anti_n1.blade` | `441A1753924BE40B68B1FE161B0115D7850C7A754F4A9D01D0A0A1D0B8A04F9B` |
| `manifest.json` | `69BF1F0F8B6B0EBB00727FFDD776D0FE11613D4B372E659E4C4B011E1183F106` |

Luna alone dispatches after root approval. Verify pins first. Use checkout-root working directory `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement`, a fresh absolute `bench/.build/B08-emit-census` output root beneath it, separate `n5`/`n1` subdirectories for deployed headers and a private absolute `temp` subdirectory. Create these before any child call and set both TEMP/TMP to that private directory. Use a recorded campaign-clean child environment with `BLADE_LLVM` unset (ordinary C++ route), `BLADE_TILE_CACHE` unset and explicit CLI print selection below; record effective compiler/FP/environment settings. No policy matrix is requested.

These are the **only two compiler invocations**. `$b08Compiler` is the absolute `.exe` path explicitly supplied by root; it must be populated before dispatch. Every source, output and log path is absolute. After each call record its exit code before advancing; stop on nonzero or unexpected output. If root selects a DLL-only launcher, obtain its explicit launch-vector approval instead of treating it as an executable or guessing a prefix.

```powershell
& $b08Compiler emit 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/notes/round4/B08-census-inputs/pm07_anti_n5.blade' -o 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n5/emit.cpp' --print 'anti,result' 1> 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n5/stdout.log' 2> 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n5/stderr.log'
$b08N5Exit = $LASTEXITCODE
Set-Content -LiteralPath 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n5/exit-code.txt' -Value $b08N5Exit
if ($b08N5Exit -ne 0) { throw 'B08 N5 emission refused; stop for root review.' }
& $b08Compiler emit 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/notes/round4/B08-census-inputs/pm07_anti_n1.blade' -o 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n1/emit.cpp' --print 'anti,result' 1> 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n1/stdout.log' 2> 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n1/stderr.log'
$b08N1Exit = $LASTEXITCODE
Set-Content -LiteralPath 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n1/exit-code.txt' -Value $b08N1Exit
if ($b08N1Exit -ne 0) { throw 'B08 N1 emission refused; stop for root review.' }
```

Save complete emitted sources, deployed runtime headers and their hashes, logs, return codes, exact arguments, input/manifest hashes and selected-compiler provenance. No rewriting emitted text or input to fit an expectation. If N1's empty compact source refuses or emission specializes away a stage, retain that evidence and stop for root review; do not replace the case.

Review the actual emitted dataflow, not allocation names or markers: both `anti` and complete `result` must reach their full print paths; preserve source forcing before destination construction, allocation/extents/row metadata, private initialization before escape, original ascending strict-pair order, `+anti`/`-anti` stores and matched lifetimes/cleanup. For N5, check the full zero pass precedes the paired scatter. The ten strict pairs uniquely cover twenty off-diagonal cells; all five diagonal cells still require positive zero. The current recipe predicts 25 zero writes plus 20 scatter writes. N1 has zero pairs and one required zero write. These are analytical source-recipe counts, not native store observations. The EXPECT comments are hand-derived small contracts; no pin generation or value-validation execution is requested.

Return the two emissions and a compact source review to root, then stop. No zero-elision implementation is authorized here. Native dead-store elimination may already remove redundant writes; emitted zero elision alone would not establish a measured win, change output capacity or establish RSS savings. Original B08 census and all B07 artifacts remain unchanged.
