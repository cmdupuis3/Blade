# Round-3 wave-4 Codex diff reviews (condensed)

The independent Codex diff reviews of the wave-4 (gap-cleanup) items: W4-1 three looks, W4-2 and W4-3 two looks each, W4-4 one look. Under the user's wave-4 fallback rule (2026-10-22 / 23) the last look of W4-1, W4-2 and W4-3 was DO NOT MERGE and each was merged with its regressions fixed at the merge and every remaining finding recorded as an OPEN gap row; no further fix round.

Sections (each is one former scratchpad file, verbatim): [`wave4-codex-reviews.md#codex-diff-w4-1`](#codex-diff-w4-1), [`wave4-codex-reviews.md#codex-diff-w4-1-2`](#codex-diff-w4-1-2), [`wave4-codex-reviews.md#codex-diff-w4-1-3`](#codex-diff-w4-1-3), [`wave4-codex-reviews.md#codex-diff-w4-2`](#codex-diff-w4-2), [`wave4-codex-reviews.md#codex-diff-w4-2-2`](#codex-diff-w4-2-2), [`wave4-codex-reviews.md#codex-diff-w4-3`](#codex-diff-w4-3), [`wave4-codex-reviews.md#codex-diff-w4-3-2`](#codex-diff-w4-3-2), [`wave4-codex-reviews.md#codex-diff-w4-4`](#codex-diff-w4-4)

---

## codex-diff-W4-1

_formerly the scratchpad's `round3/reviews/wave4-codex-reviews.md#codex-diff-w4-1` (appended at the wave-4 records commit)_

﻿VERDICT: DO NOT MERGE

Reviewed `5ea03056..1dd8bc18`; base gap rows and W4-1 brief read. Files/git only: no build, emission, test, or program execution. Witness outcomes below are source-derived, not new run evidence. Line references are at `1dd8bc18`.

Contracts chosen: (1) Top-level map aliases defer; folds fuse without printing; a module-level forcing use materializes the source and gives aliases independent assignable copies, with both names printed. Function-only forces stay private; block/function aliases instead copy at binding. §10.2 explicitly states that positional deviation (`docs/formalism.md:2130-2138`). (2) An all-deferred tuple stays deferred; every other tuple constructs its members left-to-right, recursively computing deferred members as copies without forcing their source bindings. Only the selected `if` arm runs. §10.2 explicitly names the never-projected-member first failure (`2149-2164`). These are written contracts, not established three-lane implementations. The tuple paragraph also qualifies the preceding paragraph's broad description of tuple members as binding-forcing uses.

1. **BLOCKER — G59 introduces a per-evaluation leak.** `src/CodeGenBinding.fs:1118-1121` pushes an allocation scope for each arm, then discards it with `popAllocScope () |> ignore`; `1140-1146` returns the wrapper without transferring ownership to the outer binding. `CodeGenLoopNest.fs:4707-4717` registers allocations only in that innermost frame; only `popAllocScopeFrees` renders frees (`4945-4954`). A function constructing an if-held tuple, projecting/reducing its array member, and returning a scalar leaks the selected arm's pools on every call. This is **introduced**, not a pre-existing runnable leak: the base had no such ordered-if path and the target shape failed C++ compilation. Calling it “bounded per evaluation” does not bound repeated calls. **Fix:** free nonescaping arm temporaries and transfer escaping component pools, under names accessible after the branch, to the enclosing allocation owner; free them once at last use/scope exit. Do not simply free the returned pools before returning. Pin repeated function/loop use and ownership in both compiled lanes, or refuse the unsupported shape in all lanes with one diagnostic.

2. **BLOCKER — whole reassignment drops the old producer's failure in LLVM.** `src/EmitLlvm.fs:2805-2809` only calls `markForced`, then overwrites the slot. `markForced` records printability, not evaluation (`6379-6395`). For named `Row = Idx<1>`, `z = [0]`, `y = [7]`:
   ```blade
   let g = method_for(z) <@> lambda(v) -> 1 / v
   g = y
   let answer = g(0)
   ```
   C++ forces `g` before assignment (`CodeGenBinding.fs:744-751`); the interpreter forces the old cell (`Interp/Core.fs:1313-1321`). Both encounter BL8013. LLVM replaces the producer with `y`, so neither the read nor the print evaluates the old division. With an alias, marking the source can instead postpone its failure until printing. `loops/543` and `546` use successful producers and miss this. **Fix:** evaluate and retain the old target at the assignment forcing point before replacing its slot; define and align target/RHS failure order too (`Core.fs:722-724` currently evaluates RHS first). Pin failing target, failing RHS, and both failing, with code and location in all three lanes.

3. **BLOCKER — forcing the source does not force/copy its aliases; subsequent writes diverge.** `CodeGenBinding.fs:3312-3349` walks alias → source only. Forcing `g` never visits `g2`; the interpreter likewise only walks upstream (`Interp/Loops.fs:305-317`). LLVM binds a deferred alias to the existing producer (`EmitLlvm.fs:4664-4673`, `5455-5457`). With `x = [1.0, 2.0, 3.0]`:
   ```blade
   let g = method_for(x) <@> lambda(v) -> v * 2.0
   let g2 = g
   g(0) = 100.0
   let r = g2(0)
   ```
   C++/interpreter copy the now-written source only when `g2` is read (`r = 100.0`); LLVM's alias still computes the original producer (`r = 2.0`). The chosen contract requires copying at the first force through **either** name, before that write. Even replacing the write with a read of `g` and never reading `g2` leaves `g2` unprinted, contrary to §10.2's “both names then print.” `loops/540` tests only downstream-to-upstream forcing. **Fix:** represent the alias relationship in all lanes and materialize/snapshot all participating aliases at the contract's first forcing event. Pin source-first, alias-first, sibling aliases, chains, and subsequent writes/rebinding in either direction.

4. **BLOCKER — G59's branch-local forcing disagrees on printing and can evaluate an untaken arm.** C++ builds arms at `Indent + 2` and discards their contexts (`CodeGenBinding.fs:1112`, `1146`); forces there never enter the printable set (`3346-3348`). LLVM emits both arms against the same mutable context (`EmitLlvm.fs:8262`, `8277`), and a positional read calls `consumeArrOperand` → `markForced`/`materializeExpr` (`2884-2885`, `6288-6341`). The interpreter registers an actually taken module-level force because its function depth is zero.
   ```blade
   let t = if c then (g(0), m |> compute) else (0.0, m |> compute)
   ```
   Here `g` and `m` are distinct pure deferred maps. If `c` is true, C++ omits `g` while the other lanes register it. If false, LLVM still records the then-arm's `g` during emission: printing can run its producer; a multi-read force can even be hoisted before the branch by `materializeExpr`. Make `g` fail to expose evaluation of an untaken arm. The existing G59 pins use explicit/bare compute members, not positional forces within an arm. **Fix:** make branch forcing state conditional, preserve branch-local evaluation and dominance, and implement one source-level module print rule in all lanes. Add taken/untaken positional and capture cases, including a failing untaken producer and a later read.

5. **BLOCKER — G57's ordered-call path skips evaluating array arguments.** `CodeGenBinding.fs:1183-1185` leaves every other array-valued expression in the final call. Thus `f(checked_array(a), g |> compute)` computes `g` before calling `checked_array`, although §10.2 promises left-to-right arguments for this call. Let `checked_array` fail a checked cast (BL8006) before returning its array, and let `g` divide by zero (BL8013): C++ emits the division first; the interpreter evaluates the first argument first (`Interp/Core.fs:1158-1173`). This is the targeted compute-argument case, not the acknowledged ordinary-scalar-call gap. **Fix:** bind every evaluating argument, including array-valued calls/operations, at its source position; leave only genuinely nonevaluating operands inline. Pin both failure orders and successful single evaluation with explicit lane routing.

6. **BLOCKER — the by-name write gate introduces the admitted shadowing divergence.** `TypeCheck.fs:162` throws away lexical scope information; `TypeCheckInfer.fs:2611-2619` vetoes fusion by spelling. A top-level deferred `g`, an unrelated function-local `let mut g` that is written, and a module `reduce(g, (+))` now lose the typed fused terminal although nobody writes the module binding. The builder reports C++ still fusing without printing versus LLVM/interpreter forcing and printing; the ordinary LLVM/interpreter fold paths indeed mark/force their named operand (`EmitLlvm.fs:7119-7133`, `Interp/Loops.fs:2375-2376`). The false veto is new in this commit; the base reduction did not consult this set. “Values correct” does not excuse a changed printed set. **Fix:** use resolved binding identities, or a lexical shadow-aware write analysis, for this veto; align the downstream forced-versus-fused decision. Pin an unused shadow-writing function, parameter shadowing, and a genuine module write. Keep G60 open until the divergence is removed.

7. **HIGH — G75 and the local-alias deviation are not fixed in three lanes.** The report and gap rows explicitly say LLVM skips `loops/544`, `545`, `547`. `tests/LlvmTests.fs:2189-2190` counts a lane refusal as a skip, not an execution or a shared language refusal. Falling back to C++ does not validate LLVM semantics. `docs/formalism.md:2145-2147` nevertheless says 540–547 are pinned “in every lane” and lists only 544 as an exception. The local-copy deviation is stated and has a C++/interpreter value pin; its LLVM implementation is not established. **Fix:** make minimal G75 and block/function-copy witnesses execute in LLVM, or impose the same diagnostic in all three lanes. Until then mark these rows partial/open and enumerate every lane refusal in formalism/features/records.

8. **HIGH — first-failure evidence is overstated.** `tuples/041` is correctly renamed/reclassified as an abort, with BL8013/message pins at lines 22–23; successful value coverage is in 052/053. It has neither an EXPECT nor a location pin. Likewise 045/047/049/050 and loops/542/545 omit location pins; 051 does include `.blade:14`. LLVM's differential compares error/location lines (`LlvmTests.fs:2237-2253`), but the interpreter abort gate checks only the supplied substrings (`InterpDiff.fs:417-422`). Thus the report's blanket “code, message and line” does not establish all-three-lane first-failure locations. **Fix:** add expected location pins to each failure witness, preserve the successful companion pins, and report exactly which routed comparisons passed. Do not count C++ fallback as LLVM evidence.

9. **MEDIUM — ordinary-call ordering remains a recorded pre-existing gap, but features overclaims it.** Formalism `2157-2160` accurately limits the new guarantee to calls containing explicit compute and acknowledges ordinary scalar-call order. `docs/features.md:232` instead says “call arguments evaluate left to right too.” **Fix:** narrow that sentence and give the remaining ordinary-call case an OPEN record/status instead of letting G57's FIXED label imply closure. This pre-existing gap alone is not the introduced-order blocker in finding 5.

Five report hooks checked:

| Claim | Exact hook | Result |
|---|---|---|
| Wrapped compute arguments run in source order | `src/CodeGenBinding.fs:1164-1187` | Explicit computes/scalars ordered; array-valued expressions excluded (5). |
| Named deferred tuple members recurse | `src/CodeGenBinding.fs:1040-1061` | Descends into named tuple members and computes bare array leaves. |
| If-held tuple arms are not scope-freed in a frame | `src/CodeGenBinding.fs:1118-1121` | Confirmed; new scope records are discarded (1). |
| Reduction resolution stops at module-written names | `src/TypeCheckInfer.fs:2611-2619` | Confirmed; lexical identity absent (6). |
| Function-only forces stop interpreter memoization/printing | `src/Interp/Core.fs:1219-1224`; `src/Interp/Loops.fs:287-317` | Capture-free-callable proxy confirmed; not a three-lane G75 proof (7). |

Scope: no wave-3 planner or scatter-header edits; outside loops/tuples, only sql-group-by/072 comments change and its pins are untouched. G62's literal/arithmetic fast path and CLI needles preserve those specific C++ forms; the reported baseline sweep covers C++ tuples and loops/001–200 only. No both-lane blanket emission-identity or whole-corpus printed-set guarantee is established; no additional existing-file printed-set regression was demonstrated by this read-only review.

---

## codex-diff-W4-1-2

_formerly the scratchpad's `round3/reviews/wave4-codex-reviews.md#codex-diff-w4-1-2` (appended at the wave-4 records commit)_

VERDICT: DO NOT MERGE

Reviewed `1dd8bc18..26bc7518` and `5ea03056..26bc7518`; references below are at `26bc7518`. Files/git only; no build, emission, tests or program execution. New witnesses are source-derived, not executed results.

First-look findings:
1. PARTIAL — discarded arm allocation scopes are gone and C++ fails closed in a frame (`CodeGenBinding.fs:1128-1143`); the promised common BL6002 is bypassable through tuple aliases (NEW 4).
2. CLOSED for the original whole-reassignment witness — lowering now refuses it with BL4005; 543 pins the refusal. This is a refusal, not implementation of assignment evaluation order; indirect-write coverage remains broken (NEW 1–2).
3. OPEN — the shared alias-class print rule exists, but LLVM marks aliases without redirecting their producers to the forced cells (NEW 3). Prohibiting writes through the class does not make its inputs immutable.
4. PARTIAL — direct positional/capture witnesses are refused; admitted closed copy members execute inside their selected arm. The lowering/emitter predicates differ (NEW 4); ordinary scalar-arm hoisting remains reachable and buried under G59's FIXED/done row.
5. PARTIAL — C++ now binds array-valued expressions at their argument positions; LLVM can still hoist a later named deferred argument ahead of earlier arguments (NEW 5). 057–059 do not exercise that path.
6. CLOSED for the reported shadowing bug — by-name veto reverted; block IDs recorded in inferBlock/inferForIn, reset by checkModule for each module (`TypeCheck.fs:243-248,663`). 552 covers local and parameter shadowing. No exhaustive destructuring/multifile proof is claimed.
7. OPEN — PARTIAL labeling and enumeration of LLVM refusals 544/545/547 correct the records, not the behavior. Wave 4 still requires LLVM execution or the same refusal in all lanes; the capture-free-callable proxy remains (`Interp/Core.fs:1221`).
8. CLOSED — code/message/line pins now exist on 041/045/047/049/050 and 542/545; new rejects pin BLxxxx @ l:c, new aborts pin lines. 545 remains C++/interpreter evidence only. LLVM's harness skips rejects and backend refusals (`LlvmTests.fs:2175-2190`); its PASS cases compare actual LLVM execution, not fallback.
9. CLOSED — features narrows calls to explicit-compute cases and ordinary-call order has an OPEN provisional G76. The narrowed guarantee still has NEW 5.

NEW findings:

1. BLOCKER — block-local views bypass BL4005. `src/Lowering.fs:3047` treats EVERY MutableArrayLets member as fresh storage, before examining its initializer. That set contains every assignable block array let (`1336-1338`), including `let row = m(0)`. The copy rule only copies IRVar initializers; indexing still produces a view (`CodeGenBinding.fs:841-855`, `Interp/Core.fs:633`). Move loops/551's `let row = m(0); row(1) = 9.0` into a top-level block: the new check returns None for row, even though it writes m. A following fused fold can still read m's original computation. The existing 551 uses a MODULE-level row and misses this. FIX: stop provenance traversal only at a proved copy/materialization, not membership in MutableArrayLets; follow views and their rebindings, without silently clearing an exhausted traversal budget. Pin block-local, function-local and chained-view refusals with the same BL4005/span.

2. BLOCKER — callableWriteSummaryWith is not an exhaustive parameter-write summary. The new consumer reads only `s.Params` and ignores unresolved callees (`src/Lowering.fs:3076-3086`). Its existing producer short-circuits the whole walk once External becomes true (`src/Optimize.fs:1152`); unknown calls contribute External without parameter provenance (`1184`). Starting from loops/550, make poke first update an unrelated captured/module counter, then write a(0). The summary stops before a(0), so passing deferred g escapes the new refusal; inspecting poke separately cannot connect its parameter ID to g. The same omission propagates through a second call level; unresolved callbacks are also explicitly ignored. FIX: compute all parameter effects even after an external write, propagate unknown-call effects onto storage arguments (or conservatively refuse deferred arguments), and resolve callback candidates. Pin external-write-before-parameter-write, two-hop forwarding, callback and parameter-view cases. Do not reuse a boolean early-exit analysis as complete provenance.

3. BLOCKER — LLVM's new alias printing can rerun a producer after its input changes. `src/EmitLlvm.fs:4660-4666` gives a deferred alias its own ArrVal producer; `6332-6334` upgrades only the forced ID. The NEW code at `8836-8838` changes Forced only, and `8910-8911` prints each alias's original ArrSlots entry. Witness: named rank-1 mutable integer x = [1,2]; g = map(x, lambda(v) -> 10 / v); g2 = g; read g(0) and g(1); then x(0) = 0. The two reads store g; g2 still holds the producer and the new print expansion evaluates division by zero while printing it. C++ declares g2 as a reference to stored g; the interpreter looks up g's stored cells. No write through a deferred binder occurs. FIX: canonicalize alias reads and printing to one forced class value at the forcing event, including aliases already bound and aliases declared later. Pin input mutation after source-first/alias-first forcing, successful values/printed set, and a producer that would fail if rerun. Merely marking all IDs is insufficient.

4. HIGH — the shared BL6002 predicate misses deferred-tuple aliases. `src/Lowering.fs:3093-3099` recognizes only an initializer that is directly IRTuple; C++ propagates a tuple alias's deferred RHS (`CodeGenBinding.fs:7304-7307`) and recognizes it at `914-925`. With deferred m, `let p = (m,m); let q = p`, arms `(q,1.0)` / `(q,2.0)` make lowering's armNeeds false, but C++'s ifNeedsMemberOrder true. Put that if in a function: it escapes the common frame refusal and reaches the backend-specific backstop/refusal instead. At top level the same mismatch skips the arm-read audit. FIX: normalize alias/let spines and share the structural predicate rather than maintaining approximations; include callable-local deferred tuple binders. Pin aliased tuples in frames and taken/untaken arms that contain deferred reads, with the common BL6002 text/span.

5. BLOCKER — later bare deferred call arguments still move before earlier arguments in LLVM. `src/EmitLlvm.fs:3815-3820` walks arguments in order but calls materializeExpr for a named array; `6312-6329` inserts its producer at the enclosing statement's start (`9353-9355`). Witness: `f(a((a(0) : Row)), bad, good |> compute)`, with the first argument failing BL8006 and bare deferred bad dividing by zero (BL8013). The explicit compute selects C++'s ordered path; the interpreter evaluates arguments in order. LLVM inserts bad's evaluation ahead of the first argument's check. 057/058 use an array-valued CALL and an explicit compute, neither the bare-IRVar hoist. FIX: establish an argument-local sequencing boundary in LLVM, preserving necessary dominance without moving later arguments before earlier evaluations; audit the capturing-callee path too (`3792` passes a producer directly). Pin both failure orders and side-effect order with a later bare deferred alias.

6. MEDIUM — contract publication and coverage claims remain incomplete. Formalism `docs/formalism.md:2116-2185` states the write prohibition, rationale, remedy and both BL6002 restrictions; G60 honestly says 546 is REFUSED. However `CLAUDE.md:277-279` adds only tuple construction: neither new restriction is in language essentials. The BL4005 suggestion also prints the non-code placeholder “a deferred map bound in a block” for local binders (`Lowering.fs:3025-3028,3068`). G59 still labels a reachable first-failure/print divergence “done”; scalar arms such as `if c then g(0) else 0` are neither ordered nor refused. FIX: publish the new language restrictions and usable local-name remedies; give the remaining branch divergence its own OPEN row. Preserve the sweep script/manifest and delimit its coverage: 3305 matches pre-existing corpus+examples (3316 current files minus 11 added witnesses), not stdlib or doc fences. Physics has 47 .blade files and is included only by recursive examples enumeration; no supplied sweep mechanism proves that enumeration. DocTests separately selects CLAUDE/docs pages (`tests/DocTests.fs:94-108`), skips sketches and non-opted-in plan fences; 133/0/79 is not all fences checked. No new claim of an actual existing-file refusal is made by this static audit.

Five hooks checked:

| Claim | Hook at 26bc7518 | Result |
|---|---|---|
| All lanes refuse deferred writes / unsafe ifs | `src/Lowering.fs:2990,3313` | Common pre-backend pass exists; provenance and tuple-alias holes above. |
| One alias-class rule, three print callers | `src/IRLift.fs:1473`; `src/CodeGen.fs:3031`; `src/Interp/Run.fs:624`; `src/EmitLlvm.fs:8837` | Shared graph, chains/siblings included; LLVM does not share forced storage. C++ alias declarations precede prints and reference module storage. |
| Every evaluating ordered-call argument bound | `src/CodeGenBinding.fs:1180-1218` | C++ array-expression arm added; bare deferred names force before the plain-name exemption. LLVM hoisting still breaks order. |
| Shadowing handled by binder identity | `src/TypeCheckInfer.fs:2614-2622,15275,15506`; `src/TypeCheck.fs:247` | By-name veto gone; per-module reset verified; 552 uses the generic differential route, whose refusals are skips. |
| Function-only interpreter forces stay private | `src/Interp/Core.fs:1221-1224`; `src/Interp/Loops.fs:293` | Capture-free-callable proxy unchanged; not proof of G75 in LLVM. |

Scope: no wave-3 planner edits, but blanket byte identity outside the three named shapes is FALSE: G62 still changes plain call tuples (001/021), and `CodeGenBinding.fs:8106-8112` now exempts ANY block-final tuple-valued if from the outer force pre-pass, including ordinary scalar tuples; a preceding failing block let can consequently run before a deferred arm read that the base hoisted first. Narrow the scope claim or narrow this gate, and obtain both-lane baseline evidence; the reported subset sweep is not that guarantee.

---

## codex-diff-W4-1-3

_formerly the scratchpad's `round3/reviews/wave4-codex-reviews.md#codex-diff-w4-1-3` (appended at the wave-4 records commit)_

VERDICT: DO NOT MERGE

Reviewed 26bc7518..d8c58d9e and 5ea03056..d8c58d9e; hooks below are at d8c58d9e. Files/git only. No builds, tests, emission or execution. New witnesses below are source-derived, not executed. NEW 1 is a correctness regression against 5ea03056 and blocks under the stated merge rule; the other holes are remaining gaps.

N1 PARTIAL — 553–555 close the original block/function/rebinding witnesses; a tuple carrying row views still loses provenance (NEW 2).
N2 CLOSED for the reported early-exit/forwarding/callback defects — the new monotone written-ID fixed point visits all sites, including after external writes, and propagates written parameters across calls; 556–559 pin these cases. This does not prove storageOf exhaustive.
N3 PARTIAL — materializeExpr now redirects already-bound direct alias chains/siblings to the forced pool; later aliases copy that pool. 560/561 address the reported rerun. The unchanged sole-read and block-local-alias paths remain outside that fix (NEW 4/5).
N4 PARTIAL — 060/061/062 use the shared structural predicate; the raw lowering view and flattened emitter view still disagree after the 16-link cutoff (NEW 3). The interpreter and LLVM retain separate tuple-deferral classifiers; NEW 1 is a consequence.
N5 CLOSED by LLVM refusal for the reported bare-name witnesses, not by implementing LLVM argument order. Both call paths encounter the guard. 063/064 pin both failures/locations; 065's interpreter order is bump(cnt), force seen2, compute good, hence 1 + 11 + 2 = 14. LLVM refusals are SKIPs (tests/LlvmTests.fs:2189), not executed differential passes. The detector is not exactly C++'s admission: LLVM tests any immediate IRCompute, C++ an explicit compute of an unforced array.
N6 PARTIAL — essentials, usable local-let remedy, G77, G62's 001/021 caveat, and the preserved sweep are present; broad FIXED/scope claims still need narrowing (NEW 6).

NEW findings:

1. REGRESSION vs 5ea03056 — BLOCKER: nested block-local deferred tuples now execute an unused failing producer in the interpreter.
   src/Interp/Core.fs:624 evaluates a local tuple into VTuple; :679 recognizes only VDeferred as a deferred named member; :685 then recursively forces that VTuple through forceTupleMember (:1104). Minimal witness:

   type Row = Idx<1>
   let z: Array<Int64 like Row> = [0]
   let g = method_for(z) <@> lambda(v) -> 1 / v
   let ok = {
       let p = (g, g)
       let q = (p, p)
       7
   }

   At the base, the local IRTuple arm only evaluated members: neither p nor q forced g, and ok was 7. At this head, p is VTuple of suspensions, so q takes the value-tuple arm and raises BL8013. C++ still classifies p and q as wholly deferred (CodeGenBinding.fs:827); LLVM recognizes a TupleSlots entry of producer parts (EmitLlvm.fs:8277). Module-level p/q alone do NOT expose this: evalBinding has its own deferral guard (:1467).
   FIX: preserve deferred-tuple identity/classification for local lets and aliases, consistently with IRLift, rather than treating every non-VDeferred runtime value as stored. Pin the unused failing witness and nested/local alias variants. If split out at merge: OPEN regression — block-local tuples of deferred tuples are eagerly forced by the interpreter.

2. GAP (pre-existing write/fold disagreement, still unrefused) — HIGH: an ordinary tuple of VIEWS hides the deferred owner.
   Lowering.fs:3101 follows an IRVar only when its type is an array or its ID is deferred; :3114 drops an ordinary tuple. :3119 follows a projection only as far as that dropped tuple. Starting with loops/551's m, replace its row binding with:
   let pair = (m(0), m(1))
   let row = pair[0]
   row(1) = 9.0
   let s = reduce(m, (+), axes = 2)
   pair is not a wholly deferred tuple: its members are row reads. These are views, and neither the tuple wrapper nor the projection is a proved deep copy. Nevertheless storageOf(row) stops at pair and never marks m. Iterating cannot restore an absent edge. The base already admitted this write/fused-fold mismatch; this is not a newly introduced wrong answer.
   GAP ROW: G60 / OPEN — writes through tuple/aggregate-held views can bypass BL4005 and disagree with fused reads. Follow aggregate carrier initializers/projections by binder identity; stop only at actual copies. Pin tuple-of-views and nested-carrier writes.

3. GAP (still divergent refusal coverage) — HIGH: sharing the predicate does not make the two DeferredViews equivalent.
   IRLift.fs:1536–1547 silently returns None after 16 tuple-alias lookups. Lowering.fs:3039 supplies the raw let chain; CodeGenBinding.fs:7274–7277 propagates the already-resolved deferred RHS. Extend 060/061 with p0 = (m,m), p1 = p0, ... p16 = p15 and use p16 in the arms: lowering misses the member-ordered shape, while C++ sees the tuple immediately. Function cases reach the backend backstop instead of common BL6002; top-level cases skip the arm-read audit. This family was already unsupported/divergent at the base.
   GAP ROW: G59 / OPEN — long deferred-tuple aliases bypass the common if refusal. Normalize both views with cycle detection, or refuse unresolved/exhausted provenance instead of returning “not deferred”; pin >16 links.

4. GAP (confirmed pre-existing, not a regression) — HIGH: LLVM's sole-read module map still prints a rerun after input mutation.
   EmitLlvm.fs:6317–6322 is byte-equivalent in behavior to the base consumeArrOperand: ReadOnce marks printable and returns the producer, without materializeExpr. With x = [1,2], g = map(x, 10/v), one read a = g(0), then x(0) = 0, printing g reruns division by zero. No alias is needed. The reference-equality upgrade is never reached.
   GAP ROW: G60 / OPEN — sole-read deferred module bindings do not retain their first-forcing snapshot in LLVM; printing can change values or fail after input mutation. Store the first forced value with dominating lifetime, or refuse this shape. Keep this separate from G75's function-only force rule.

5. GAP (pre-existing LLVM local-copy discrepancy) — HIGH: an alias in a top-level block is still not copied at its binding in LLVM.
   EmitLlvm.fs:4693–4702 copies an IRVar initializer only if it already holds APool; a producer is reused unchanged, without consulting MutableArrayLets. With x = [1,2], g = map(x, 10/v):
   let r = { let b = g; x(0) = 5; b(0) + b(1) }
   C++ forces/copies at b (CodeGenBinding.fs:7265); the new interpreter does likewise (Core.fs:642). LLVM leaves b deferred until its reads after the write, yielding 7 instead of 15 by source trace. The base LLVM had the same delayed alias path. 547's function-induced lane refusal does not establish this standalone block case.
   GAP ROW: G60 / OPEN — LLVM block-local assignable aliases of deferred arrays do not implement the binding-time copy. Force/copy at the local alias, separately from module alias classes; pin a standalone top-level block with an intervening input write.

6. GAP (records/coverage, not a correctness regression) — MEDIUM:
   Mark G57 PARTIAL: the row says FIXED/done despite 063–065 being LLVM refusals. Mark G60 PARTIAL with NEW 2/4/5 OPEN; qualify G59 for NEW 3 and G58/G61 for NEW 1. G75 PARTIAL and G76/G77 OPEN are honest; formalism 10.2's unconditional snapshot/local-copy claims need the LLVM exceptions above.
   The sweep script is reproducible from the repository root with its recorded binary location, but is not a baseline-emission proof. Read-only enumeration matches 3271 corpus + 60 examples = 3331, including physics 47. Its 17 hits/982 other errors are supplied evidence, not rerun here; stdlib, fences and successful multifile-program coverage are excluded. The 48-step write-budget refusal is reachable (even a cyclic array rebinding can exhaust it), and the text is present at Lowering.fs:3139; do not describe every exhaustion as a chain of >48 distinct views.

Write-analysis audit limits: all modules enter the common pass (Lowering.fs:3373); identity-based rebindings are collected before the fixed point, so source order does not hide a later alias/rebind. The proved-copy guard requires an assignable IRVar initializer and plain, nonsymmetric indices after stripping units (:3075); compound/sparse/ragged/halo tags do not pass it. Fresh let-rec storage is a copy boundary. A provider write consumes its array into external storage, rather than mutating that array. An unresolved tuple-selected callable with array arguments is conservatively marked by IRApp; this is not a proof for every higher-order/map form (the unresolved IRApplyCombinator kernel arm at :3178 is ignored).

Five hooks:

| Claim | Hook at d8c58d9e | Result |
|---|---|---|
| Common refusal before lane split | src/Lowering.fs:2992,3373 | Every module; fixed point closes the reported direct cases, aggregate provenance hole remains. |
| Alias-class storage and prints | src/IRLift.fs:1473; src/EmitLlvm.fs:6383; src/CodeGen.fs:3031; src/Interp/Run.fs:624 | Direct IRVar chains share the producer object; a later alias of APool copies stored cells. No universal proof for rewrite-generated carriers; sole-read/local-copy exceptions above. |
| Ordered call evaluation | src/CodeGenBinding.fs:1149; src/EmitLlvm.fs:3776; tests/LlvmTests.fs:2189 | C++ positional temporaries; LLVM refusal/skip for N5, not LLVM execution. |
| Shared if classification / binder identity | src/IRLift.fs:1569; src/Lowering.fs:3225; src/CodeGenBinding.fs:1066; src/TypeCheckInfer.fs:2614 | Shared structural function, unequal alias normalization; block-ID shadowing guard retained. |
| Function-only interpreter forces | src/Interp/Core.fs:1221; src/Interp/Loops.fs:293 | Capture-free-callable proxy unchanged; G75 remains PARTIAL, not an all-callables proof. |

Scope: block-final-if gate reverted to the base; no wave-3 planner edits. Blanket byte identity outside the named shapes is not established and is structurally false: besides G62 tuple temporaries, assignment RHS forcing (CodeGenBinding.fs:745) and deferred reads in function returns (CodeGen.fs:982) changed emission. The member-hoist alias_deferred route now requires actual LLVM execution and equal outputs across both gates/lanes (CliSelfTests.fs:1992–2014); that is a pinned improvement, not a regression. NEW 1 is the merge blocker.

---

## codex-diff-W4-2

_formerly the scratchpad's `round3/reviews/wave4-codex-reviews.md#codex-diff-w4-2` (appended at the wave-4 records commit)_

VERDICT: DO NOT MERGE

Review: `5ea03056..b91ff7d6`, W4-2. Read-only source/git review; no builds, tests, emit/run, or compilers executed. References below are at b91ff7d6. Reported runtime footers are builder evidence, not independently reproduced.

1. **BLOCKER — G54 drops escaping captures in assignment statements.** `src/CodeGenLoopNest.fs:4492-4502` excludes EVERY unit/scalar let from capture discovery, not just synchronous consumers. An assignment is unit-typed (`src/IR.fs:3212`). Consider a helper allocating fresh `y`, executing `out(0) = lambda(k: Float64) -> reduce(y, (+)) * k` into a caller-owned mutable closure array, then returning a scalar; the caller invokes `out(0)` after the helper returns. The relevant lowered statement is `IRAssign(index(out, 0), IRVar(closureId, fnType))`, and that callable captures `y`. Lowering explicitly puts the callable out of line and leaves only its ID (`src/Lowering.fs:1226-1236`). `assignSeeds` collects that ID, NOT its captures (`CodeGenLoopNest.fs:4446-4458`); `IRVar` is a traversal leaf (`IR.fs:2240-2243`), and propagation through scope lets cannot recover the out-of-line callable's `y`. Before this change captureSeeds pinned `y`; now it is freed at helper exit while the stored closure still holds its wrapper. Copy-by-value copies the pointer, not storage (`CodeGenExprSupport.fs:1050-1068`). BL4005's rebound-capture check does not save this case: `y` is never rebound (`TypeCheckValidate.fs:626-631`). This is a source-derived UAF counterexample, not an executed repro. **Fix:** retain transitive callable captures on assignment/argument escape channels, including stores hidden in invoked kernel bodies; suppress captures only for consumers positively proved synchronous and non-exporting. Add stored-closure-after-return and kernel-stores-closure ASan/value pins, plus returned closure/deferred-map controls. The interpreter retains managed values; native LLVM currently refuses function values (`EmitLlvm.fs:2700`), so a fallback is not an independent LLVM lifetime pin.

2. **BLOCKER — G64 changes all zero-level scalar nests in C++, but only gated inputs in the interpreter.** `CodeGenLoopNest.fs:2155` replaces `+=` with `=` without a compound-input guard. `Interp/Loops.fs:244-249,946` admits the new evaluator only when an input satisfies `gatedInputIndex`. Dense whole-array applications still initialize `acc` to zero and add the term (`Loops.fs:1038-1041,2120-2121`); their parameters also remain `VUnit` when no level peels them (`Loops.fs:1950-1953`). A minimal counterexample shape is:

   ```blade
   type I = Idx<2>
   let x = [1.0, 2.0]
   function negz(c: Array<Float64 like I>) -> Float64 = -0.0
   let z = object_for(negz) <@> x |> compute
   let negative = 1.0 / z < 0.0
   ```

   The unused parameter avoids the pre-existing unbound-parameter failure: the new C++ term assignment preserves -0; the interpreter's zero-plus-term loses it. A kernel actually reading `c` now works in C++ but still receives VUnit in that interpreter path. **Fix:** share the zero-level applicability predicate across lanes and bind all whole operands/evaluate the term once in each lane, or narrow the C++ repair to the authorized shape while obtaining agreement on a broader contract change. Pin dense and compound forms, used and unused operands, and the sign-observing Boolean in REPRO/CONTRACT=off. This also defeats the requested untargeted-emission identity: the C++ change reaches dense programs outside G64's compound witness.

3. **HIGH — G64 has no native LLVM repair or common refusal.** `src/EmitLlvm.fs` is unchanged; scalar IRApplyCombinator still reaches the value-position refusal at line 3105. The gap row explicitly calls C++ fallback the LLVM result. That does not satisfy this wave's all-three-lanes repair/common-diagnostic rule; a skipped LLVM differential cannot establish it. **Fix:** implement the zero-level scalar apply in native LLVM with whole-operand binding and direct term value, and assert native route plus values/first failure; alternatively refuse this shape before backend selection with one diagnostic in every lane. Do not mark G64 closed on fallback evidence.

4. **HIGH — required regression pins are missing, although the witness tests exist.** The new G54 test (`tests/CellEpilogueTests.fs:742-804`) emits/compiles C++ only: it does not pin LLVM's two frees or their ordering, and covers no escape control from finding 1. G64's `index-types/543:27` numeric `EXPECT: zn = -0.0` does not independently pin the sign: `tests/Expect.fs:645-652,670-674` accepts +0 and -0 as equal. The interpreter text differential detects disagreement, but would pass a shared regression to +0. No added test asserts the compaction plan's positions-required route. G55's test (`tests/OmpTests.fs:4725-4800`) asserts the normal Path A team, operand decline and REPRO bits, but not the claimed threads-off/REPRO `[omp]` census text or first-failure equivalence. **Fix:** add LLVM release-order assertions for memfree/027; escaping-capture controls; a sign-observing Boolean/raw-bit expectation; a positions-required plan assertion; and G55 census plus failing-seed/empty-fold location twins. Keep claims explicitly limited until those pins exist.

5. **MEDIUM — canonical contracts were not updated.** `docs/formalism.md` has no change. The new one-term signed-zero rule, function-body fold placement rule, and G67 compact-cell restriction appear only in implementation/comments, CLAUDE.md, or the gap register. **Fix:** state these in formalism terms, with the precise supported shape and failure boundary; connect value and first-failure pins. Diagnostic audit: no new BL number was introduced. The variant (`Unify.fs:269`), formatter (`TypeEnv.fs:1241`), BL4004 mapping (`TypeEnv.fs:1692`), and span pin (`symmetry/142:24`) exist; the canonical documentation touch point is missing. G67's shared typecheck refusal reaches both apply and for-in paths, and its gap row records N10 as NOT BUILT with a storage design. Its predicate nevertheless checks any symmetric kernel T-dimension, without requiring an outer iteration dimension (`TypeCheckInfer.fs:12472`): constrain/document that boundary and add a zero-level compact-return control rather than treating the 436-file survey as exhaustive scope proof.

Hook table (five report claims checked against source):

| Claim | Hook | Result |
|---|---|---|
| G54 drops scalar/unit captures; assigns still protect escapes | `src/CodeGenLoopNest.fs:4446,4492,4499` | Exclusion confirmed; assignment protection disproved by finding 1. |
| G55 whole-let scalar omp fold uses statement emission | `src/CodeGen.fs:527-567` | Confirmed for IRReduce/IRCompute(IRReduce), IRTScalar, resolvable two-parameter IsOmpParallel kernel; ordinary non-requesting scalar folds retain the old path. |
| G64 zero-level parameters bind positionally and scalar term uses assignment | `src/CodeGenLoopNest.fs:2111-2116,2155` | Confirmed; broader than interpreter admission and compound scope. |
| G67 refuses both construction paths with BL4004 | `src/TypeCheckInfer.fs:12472-12480,16474-16475`; `src/TypeEnv.fs:1692` | Confirmed; corpus/142 pins code/span, corpus/143 pins both dense alternatives' canonical/mirrored values. |
| G54 census pins both local pools and a leak bound | `tests/CellEpilogueTests.fs:777-804` | C++ allocation/free-count check and outstanding_bytes < 2800 confirmed; no LLVM lifetime assertion. |

Scope: existing corpus pins are unchanged (only five new files in the four named directories); `git diff --check` is clean. Untargeted emission identity is NOT established: the global zero-level C++ change and scalar/unit escape exclusion exceed the witnesses, and every materialized Path B fold gains test-mode instrumentation. LLVM emitter text is unchanged, but shared typecheck changes can alter acceptance; no base/head emission-identity test was added.

---

## codex-diff-W4-2-2

_formerly the scratchpad's `round3/reviews/wave4-codex-reviews.md#codex-diff-w4-2-2` (appended at the wave-4 records commit)_

VERDICT: DO NOT MERGE

Reviewed b91ff7d6..b3e0a1be and 5ea03056..b3e0a1be; references are at b3e0a1be. Files/git only; no builds, tests, Blade execution or compilers. Runtime results below remain builder evidence.

First-look 1 — PARTIAL: assignment/argument/fold-store/returned-function controls now seed captures, but scalar-return effects remain invisible; NEW 1 is another UAF path introduced by the synchronous exception.
First-look 2 — CLOSED for the admitted shape: Lowering emits one IRApp before the lanes split; the C++ zero-level nest edits are reverted and Interp/Loops.fs is identical to 5ea03056. Dense and compound used/unused operands now have sign-observing Boolean pins.
First-look 3 — PARTIAL: dense LLVM uses the ordinary direct-call route; compound LLVM remains OPEN. The G64 row honestly says NOT FIXED/fallback, but that does not satisfy this wave's all-three-lanes repair/common-refusal rule. Implement native compound parameters/calls, or refuse the unsupported application before backend selection with one diagnostic in every lane. The existing LLVM-only text is `parameter '...' of '...' has type Array<...> -- the llvm lane handles Int64/Float64/Bool/String scalars only` (EmitLlvm.fs:8343,106); it is a pre-existing ABI limitation, not a shared refusal. No features-document change accompanies this item.
First-look 4 — PARTIAL: 027 now asserts native LLVM frees/order; 028 adds C++ ASan escape controls; sign Booleans and positions-required assertions exist; G55 census reasons and code/message/frame twins exist. Native acceptance is not regression-pinned (NEW 2), nor are first-failure source lines: OmpTests.fs:2956 calls Lowering.lower, which uses Parser.parseProgram with File=None (Lowering.fs:3339; Parser.fs:718). C++ therefore receives nullptr for the filename (CodeGenState.fs:30), and blade_runtime.hpp:375 omits `-->`. Use file-backed lowering and assert actual locations; current tests establish no source-line equivalence.
First-look 5 — CLOSED as documentation: formalism §§2.4, 7.2 and 8.4 now state the contracts. G67 still has NO outer-iteration-dimension guard: any application's compact kernel return is refused, including zero-level applications; this broader boundary is now explicit. The direct-call control is outside that predicate. Grouped compact output remains NOT BUILT.

NEW findings:

1. BLOCKER — Scalar-return calls can export a closure after the only fold capture has been exempted. `CodeGenLoopNest.fs:4458-4461` includes the return in retSubs ONLY if its result type carries a function; `allRefs`/refCount/captureSeeds at 4497-4519 therefore omit every callable argument in a scalar-returning call. `assignSeeds` also walks only scope-let subexpressions. Reuse memfree/027's kernel, its local y and pure scalar fold s; add the out parameter and stash helper from 028, and replace the final `s` with:

   ```blade
   stash(out, lambda(k: Float64) -> reduce(y(0), (+)) * k) + s
   ```

   Invoke out(0) after that helper returns. The fold-map callable remains its sole counted reference, write-free and scalar-returning, so its y capture is exempted. The DIFFERENT callable passed to stash is never scanned because the enclosing return is Float64. No seed retains y. `CodeGen.fs:1098-1107` evaluates the scalar return (including the store), then frees y and returns; the stored closure now borrows freed storage. At 5ea03056 the preceding fold's capture pinned y. This is a source-derived counterexample, not an executed repro; 028's argument control uses `let s = stash(...); s`, which misses this boundary. Fix: inspect return-expression escape effects and callable references regardless of result type, keeping only positively proved synchronous occurrences exempt; include those references in the uniqueness count. Add tail-call and scalar-arithmetic-tail store controls invoked after return, with value/ASan and no-free assertions. Do not mark G54 FIXED meanwhile.

2. MEDIUM — Native G64 support can disappear while its test stays green. index-types/544's comment is not an assertion. `tests/LlvmTests.fs:2189-2190` maps ANY LLVM emission refusal to SkippedCase; neither 544 nor symmetry/144 has a required-native assertion. A reported per-file PASS means native execution, not fallback, but the aggregate footer cannot prove that file passed and future refusal does not fail the suite. Fix: add explicit tryEmitProgramNamed-success assertions for dense 544 and the claimed native direct-call control 144, followed by native value/sign checks; fail rather than skip when a supported fixture is refused. Once compound G64 is implemented, require native 543 too.

3. MEDIUM — G55 plan identity changes with the knob, and the new test conceals it. `CodeGen.fs:558` gives the statement binding its emitted `__v<id>` name; `CodeGenBinding.fs:4550` records that as the applied omp Subject, whereas declines use the enclosing function. `tests/OmpTests.fs:4806-4817` selects every omp record except top/fsum2 and tests substring presence, then claims record equality. Thus `blade plan` identifies the same fold as __v6 when applied and fsum when declined. Fix: carry a stable source-facing function/local-fold subject through both outcomes, and assert that exact subject plus the expected outcome/reason; do not identify fsum by excluding unrelated subjects.

Hook table (five claims):

| Claim | File:line hooks | Result |
|---|---|---|
| G54's four conditions prove synchronous use | CodeGenLoopNest.fs:4451,4480,4497; Optimize.fs:1152; CodeGen.fs:1106 | carriesFn recursively checks tuples and array elements; unknown types are conservative. refCount uses IRId, not names. A kernel reused as a visible value is counted again; a let-bound map is not a directly consumed map slot, so two folds do not gain the exemption. Writes of a DIFFERENT closure through captured/parameter storage count as writes, including transitive calls. Frees are at function-scope exit, after a team completes, not inside its fold. Return traversal breaks the proof: NEW 1. |
| G64 is one shared whole-operand call | Lowering.fs:549,779,910,1280; Optimize.fs:4037; OptimizeTests.fs:1483 | Object-for/rank/scalar/no-zip/no-Reynolds guards precede IRApp. No index-kind whitelist: qualifying compound/sparse/ragged/halo operands are passed whole, with each lane's existing call support/refusals. Compound positions remain required by the call-argument census. An inline TExprLambda IS lifted to IRVar by lowerTypedLambdaAs, so it is not automatically excluded; a genuinely non-IRVar kernel keeps the old nest. |
| G55 uses the top-level statement fold path | CodeGen.fs:527; CodeGenBinding.fs:4550; OmpTests.fs:4799,4845 | Confirmed for scalar IRReduce/IRCompute(IRReduce) with resolved binary omp kernel. The added first-failure twins compare C++ statement/expression forms and top-level messages, not three-lane located diagnostics. Plan-subject inconsistency remains. |
| G67 direct calls survive; applications refuse once | TypeCheckInfer.fs:12472,16474; TypeEnv.fs:1241,1692; tests/corpus/symmetry/144_packed_comm_contraction_direct_call_accepted.blade:10 | Shared typecheck refusal covers both construction paths, with no S>0 condition. Direct group_h(jg) bypasses it; 144 pins packed output and canonical/mirrored reads. Reported native/interpreter acceptance was not independently run; the LLVM route lacks a must-succeed fence. |
| G54's LLVM lifetime and escape controls are covered | CellEpilogueTests.fs:827,855; EmitLlvm.fs:1337,1353,2700,8343,8437 | 027 asserts two slots freed in allocation order before ret. LLVM has its OWN slot/nulling pool policy, not computeScopeEscapes. 028's array-of-function parameters, function arguments and function return/value are unsupported there; all four escape controls therefore supply C++ evidence only. They do not validate native LLVM closure lifetime. |

Scope: whole-item byte identity outside 543/544 is NOT established: G55 changes function-body fold emission, G67 changes acceptance, and G54 changes frees wherever its predicate matches (027 is the pinned leak-to-scope-exit-free witness; 028 pins retained captures; no exhaustive changed-free/ASan census is committed). The removed C++ nest patch and interpreter arm restore those base paths, but the temporary lowering marker and its exact placement/file manifest are absent, so the reported sweep proves neither arbitrary-program identity nor coverage of docs fences/physics. The final rank/object-for guard was not followed by the reported differential suites; the 436-file G67 check is correctly labelled a survey.

---

## codex-diff-W4-3

_formerly the scratchpad's `round3/reviews/wave4-codex-reviews.md#codex-diff-w4-3` (appended at the wave-4 records commit)_

VERDICT: DO NOT MERGE

Reviewed `git diff 5ea03056..754d58c5`, the brief, claims-W4-3.txt, and the base gap register. Static review only: no builds, tests, compiler execution, or generated-code execution. All execution results below are builder-reported, not independently reproduced. References are to 754d58c5.

1. **BLOCKER — the synthetic marker also rewrites ordinary source expressions.** `src/Lowering.fs:574` and `src/IRMono.fs:1680` match ANY `-(zero)`, not specifically a synthesized fold seed. `src/IR.fs:923` delegates complex identities to `zeroLiteralOf`, which constructs **positive** zero components (`:911`). Consequently a source expression such as `let z: Complex128 = -zero` loses its negation: the old `IRNeg(IRComplex(+0,+0))` becomes `IRComplex(+0,+0)`. `real(z)`/`imag(z)` change from -0 to +0; reciprocals expose the difference. The interpreter's ordinary negation explicitly negates both components (`src/Interp/Numerics.fs:1388`). Generic instances have the same collision. Even real source `-zero` changes IR/emission and can now pass the literal-only orbit seed admission. This violates both the “complex unchanged” claim and untargeted-program scope. **Fix:** use a dedicated synthesized-seed/unseeded representation, unavailable to source syntax; remove both global negated-zero rewrites. Pin source-written concrete/generic complex negation and explicit `-zero` fold seeds against the base, including signed components and route/emission preservation.

2. **BLOCKER — the claimed strict fix excludes `where repro`, which is part of its contract.** The changed seed reaches fused deferred folds inside a repro function, but C++ still selects licensed forms using the fold callable, without the enclosing function's repro demand (`src/CodeGenBinding.fs:5582`, `:6286`; `src/CodeGenExprSupport.fs:1779`). The builder reports C++ +0 versus LLVM -0 for this exact target. That is not G40: formalism §2.4 requires repro bodies to decline reordering. Calling it out of scope does not close G74. Nor does a correct zero on one LLVM witness prove LLVM honours enclosing repro generally: `EmitLlvm.fs:1191` also consults the fold callable and build policy, not an enclosing repro flag. **Fix:** propagate the enclosing repro veto through all fused-fold admissions/emissions in both compiled lanes; preserve the interpreter's ordered chain. Add routed three-lane witnesses with global REPRO unset, a repro enclosing function, all-negative-zero cells, and cancellation; pin bits and rejection of licensed forms. Keep G74 open until these pass.

3. **BLOCKER — licensed emission is changed despite being explicitly excluded by the brief.** The checker substitutions (`src/TypeCheckInfer.fs:2867`, `:3116`, `:3832`) are unconditional. C++ renders the supplied seed (`CodeGenBinding.fs:5509`, `:6402`); LLVM stores it (`EmitLlvm.fs:8155`). Thus default licensed seedless deferred folds change emitted constants too, including ordinary nonzero-result programs. The new default LLVM -0 versus C++ +0 is admitted in the record. **Judgment:** G40 permits that signed-zero difference numerically under a genuine reorder licence; it does NOT establish the brief's required “licensed forms untouched”/base emission identity. `fold-lanes` tests a C++ header against its lane-tree model (`tests/LinAlgTests.fs:1663`), not compiler emission before/after this change. **Fix:** retain unseeded provenance and select the strict treatment only on strict routes, preserving the base's licensed seed/emission. Add base-versus-head emission controls in both lanes for licensed deferred folds, joins, row helpers and epilogues. Do not claim an unchanged licensed path from unchanged emitter source.

4. **BLOCKER — G76 still has multiple failure texts for the targeted program.** `src/CliSelfTests.fs:1736`/`:1737` explicitly expect BL8003 frames `[at agg]` in C++ but `[at lambda in s; at agg]` in LLVM. These are the brief's ragged-peel witnesses, not unrelated failures. The interpreter change (`src/Interp/Core.fs:1153`) depends on ambient `declSpanOf`; that table is AsyncLocal (`src/IR.fs:2882`), and the builder explicitly says the normal interpreter differential cannot see it. New corpus `functions/535:14` consequently omits the location pin. For block-local callees, the record additionally admits no compiled location versus an interpreter enclosing-binding location. A per-lane pin preserves the discrepancy; it does not meet this wave's one-diagnostic rule. **Fix:** define and implement one guard frame/location rule across ordinary and planned paths in all three lanes; make declaration provenance travel with the program/callable or explicitly transfer it into the interpreter execution context. Pin identical complete failure text through plain/destructured calls, including the ragged peel and nested/block-local calls. Reopen G76/G71's closure claim until then.

5. **HIGH — the empty-fold contract change has no new value/first-failure pair of pins.** `docs/formalism.md:1473` explicitly makes the empty deferred terminal return its identity, now -0; `docs/features/sql.md:882` still says an unseeded empty fold has no defined result. No added test exercises an empty deferred `(+)`. The implementation preserves the old distinction: deferred empties return a value, stored dynamic empties raise BL8003; a statically empty stored row is actually refused with BL3007 (`tests/corpus/loops/531:10`). This diff does not demonstrably remove an existing BL8003 guard; the pre-existing spelling discrepancy must not be mislabeled as a newly removed guard. It DOES change the deferred empty value and publish an exception without the required pins. **Fix:** state the deferred/stored distinction consistently in both documents and pin the empty deferred result plus the stored spelling's first refusal/panic, including ordering against a later failing operation, in all lanes. If the intended rule is instead one unseeded-fold semantics across spellings, implement the brief's marker/head peel with a common empty-input rule and document that separate behavior change.

6. **HIGH — the required evidence matrix is incomplete.** The new CLI list has BL8002/8003/8006 and a BL8013 order case, but no BL8014 plain/destructured pair; A7c is explicitly untested. The pull leg varies teams at one tuning configuration (`tests/OrbitScatterTests.fs:665`, `:698`), whereas the G=1/3/4/8 sweep covers scatter and action; its LLVM comparator is the **serial scatter** (`:726`), not an LLVM threaded pull. Controls reported before the final prodsum/interpreter edits cannot validate the final tree. **Fix:** add the missing BL8014/order and A7c provenance witnesses, sweep pull row groups, and rerun affected controls on the final commit. Record exact routes and skips. Keep the reported LLVM 57/222/52 skips, docs 79 skips and opt-diff 38 skips visible; these are not all-corpus/all-lane passes. No final-tree cross-version sweep or whole-corpus LLVM run establishes the complete set of changed printed outputs.

Representation/consumer audit:

`IRReduceCompute` still has mandatory `init: IRExpr` (`src/IR.fs:215`); `Lowering.fs:991` merely transports `Some seed`. There is no unseeded bit, option or node. Concrete real identities are negative-zero literals; generic identities are ordinary negated typed zeros. No strict consumer added a head peel.

| Consumer | Actual treatment at this commit |
|---|---|
| C++ deferred walk / row callable / fused leaves / reduction joins | Retains supplied seed; synthesized real `(+)` now -0. `CodeGenBinding.fs:5509,6402,6846`. Explicit seeds remain seeds, apart from finding 1. |
| `OrbitFold.planStrict` / rank-2 and rank-3 scatter | Retains each level's seed; C++ passes it to the unchanged runtime (`CodeGenBinding.fs:5110`); no first-term marker. |
| Rank-3 pull leaves / middle / outer | Retains supplied level seeds (`CodeGenBinding.fs:5165,5188`; runtime initializes each owned row once). |
| `action2` | Retains supplied seed (`CodeGenBinding.fs:5288,5310`); `aseedless` now explicitly expects -0 by raw bits. |
| LLVM walk / scatter / action | Retains supplied seeds (`EmitLlvm.fs:8155,7815,7992`). LLVM uses serial scatter for the pull comparison; no independent threaded pull implementation. |
| Complete-row slots | Executes the same row callable, then ordered outer combine; receives the changed seed. Its seed/admission path is `CodeGenBinding.fs:5694`. |
| K-lanes / Path B / licensed row helpers | Keep licensed partial-tree identities/associations; enclosing accumulator now receives the changed synthesized seed. Stored-row helper still head-peels an absent seed (`CodeGenBinding.fs:6014`), with +0 helper partials permitted by G40. |
| Hot-row / cell epilogue | Hot-row forwards `Some seedExpr` (`CodeGenBinding.fs:562`; LLVM `:5405`). Strict cell chain initializes from `plan.Seed` (`CodeGenCuda.fs:2167`; LLVM `:5304`); licensed epilogues keep partial trees and combine with the supplied seed. Row prodsum remains a sum from +0. |
| Interpreter | Starts each deferred leg/domain at supplied seed, not its first cell (`Interp/Loops.fs:2205,2231,2261`). Stored unseeded reductions retain their separate head-peel behavior. |

No additional hard-coded +0 was found in these strict **real deferred** consumers; their correctness depends on the upstream seed and strict-route selection. Complex synthesized seeds remain +0, so “every seedless fold is the head-peeled chain” is broader than this implementation.

Bit-contract judgment: for nonempty real folds, round-to-nearest IEEE addition with gradual underflow makes `-0 + x` an identity for finite values (subnormals included), infinities and both zero signs. That makes this shortcut viable within §2.4; the absence of a literal head peel alone is not a numerical blocker. NaN payload/signalling state are explicitly outside §2.4 (`formalism.md:167`); the committed payload probes support the tested platform/routes, not a universal payload-preservation theorem. With contraction enabled, producer multiply/add fusion can differ by context/lane; §2.4 already excludes a cross-lane bit guarantee there. The new raw-bit tests pin REPRO plus contraction off. They include the poked quiet payload in OMP s8, scatter/action row-group sweeps and interpreter decimal signs, not interpreter raw payloads.

Five report hooks checked:

| Report claim | Exact hook | Result |
|---|---|---|
| Deferred synthesis changes +0 to -0, no head peel | `src/TypeCheckInfer.fs:2843,2867`; `src/Lowering.fs:991`; `src/IR.fs:215` | Verified; mandatory seed remains. |
| Generic seed resolves to instance identity | `src/TypedAst.fs:620`; `src/IRMono.fs:1680`; `src/Lowering.fs:574` | Mechanism verified; source/synthetic distinction is false (finding 1). |
| Licensed orbit accepts synthesized -0 literal; source `-0.0` still a unary node | `src/OrbitFold.fs:199`; `src/Lowering.fs:578` | Verified for the stated Float literal pattern; source `-zero` is a separate counterexample. |
| Inlined LLVM body uses the C++ declaration location | `src/EmitLlvm.fs:3456,3822,8282`; `src/CodeGenState.fs:257,275` | Same named-declaration fallback; arguments emitted before switching. T3 inherits it. This does not equalize frames or prove A7c. |
| Action seedless sign/payload is pinned | `tests/OrbitScatterTests.fs:1016`; `tests/OmpTests.fs:4294` | Explicit negative-zero/payload assertions; reported executions only. |

Records: G40 is untouched. G74/G76 are newly added (absent at the base); the base register also has no G73 row to edit. CLAUDE.md's action/sibling sentences and the W3-4 comments changed, but `tests/corpus/sibling-folds/011:18` still describes the old implicit +0 seed and removed test name; update that stale comment.

Scope: not byte-identical outside the intended strict fixes (findings 1 and 3); existing corpus pins outside functions/symmetry are unchanged in the diff (sql-group-by/066 changes comments only), which is not evidence that their emitted code or printed text is unchanged.

---

## codex-diff-W4-3-2

_formerly the scratchpad's `round3/reviews/wave4-codex-reviews.md#codex-diff-w4-3-2` (appended at the wave-4 records commit)_

VERDICT: DO NOT MERGE

Read-only review of 754d58c5..6406fa6c and 5ea03056..6406fa6c; file:line references below are at 6406fa6c. No builds, tests, Blade invocations or C++ compilation performed. Execution totals are builder-reported.

First-look 1 — CLOSED: both global negated-zero rewrites are removed; the synthetic seed has distinct provenance. Source real/complex, concrete/generic negation is pinned by basic/526; this is not an LLVM execution witness (NEW 5).
First-look 2 — PARTIAL: lambdas/sections lifted in a repro body inherit IsRepro; references to already-declared fold kernels do not (NEW 2).
First-look 3 — PARTIAL: ordinary licensed synthesis again retains the base seed; the claimed 949/251 cross-version sweep is not committed/reproducible evidence, and the broader scope claim is false (NEW 5; scope below).
First-look 4 — PARTIAL: targeted single-module ordinary/T3 texts now share expectations; multi-module provenance and named lambda-prefix functions still break the rule (NEW 3–4).
First-look 5 — CLOSED: formalism §6.4 and sql §10 now agree; sql-reduce/031–033 pin stored BL8003, deferred return, and later BL8013. Keeping the pre-existing two-spelling empty rule is an explicit, acceptable contract decision, not evidence that the spellings are equivalent. Default-interpreter sign coverage remains missing (NEW 5).
First-look 6 — PARTIAL: BL8014 pairs, order, A7c refusal-shaped fixtures and pull G=1/3/8 are added; final footers are reported, but sweep provenance, planned-route assertions and some lane coverage remain incomplete (NEW 5).

1. **BLOCKER — a strict-by-construction writing-map fold still receives +0.0.** `src/Lowering.fs:1170` tests only the build knob and InRepro, then destroys the marker. `src/CodeGenBinding.fs:5581` subsequently discovers map writes and sets licensedFold=false; LLVM makes the corresponding veto through FoldOperandWrites (`src/EmitLlvm.fs:1193`). Adapt existing `tests/corpus/loops/496_writing_map_fold_serial_association.blade:14`: retain its counter write but return -0.0 on every iteration. The default seed is +0.0, so the ordered chain answers +0.0; the head-peeled chain and REPRO build answer -0.0. This is the unresolved G74 defect shared by the lanes, NOT G40: no effective reorder licence permits this chain to resign zero. **Fix:** retain unseeded provenance until producer effects and the fold's effective licence are known; select the exact identity for every obligatorily serial route, preserving base seeds only for licensed routes. Pin default/REPRO sign bits and write counts in all supported lanes. Arbitrary noncommutative deferred kernels without an init are already rejected; they are not the reachable counterexample here.

2. **BLOCKER — enclosing repro still does not veto a named fold kernel.** `src/TypeCheckInfer.fs:3883` infers/instantiates the kernel reference without eta-expanding it; `src/Lowering.fs:556` lowers it unchanged, and `:992` passes it straight into IRReduceCompute. Example: declare `add(a: Float64, b: Float64) where comm(a, b)` outside a repro function, then inside that function evaluate `reduce(method_for(xs) <@> lambda(x) -> x, add, 0.0)`. The new map lambda is repro, but add is not; the map does not write. `foldReorderLicensed` (`src/CodeGenExprSupport.fs:1779,1806`) sees only add, and the default-build predicate (`src/CodeGenState.fs:808`) ignores the enclosing declaration. The fused licensed arms remain eligible; the stored spelling has the same hole (`src/CodeGenBinding.fs:4649`). This is a fold performed IN the repro body, not a fold inside an ordinary callee. **Fix:** carry the enclosing repro demand on each fold use, including named/aliased kernels, without globally changing that kernel's other uses; make every lane/plan consult it. Pin stored, deferred and joined named-kernel cases with cancellation and emitted-form assertions.

3. **HIGH — the attached declaration table is lost for every multi-module program.** `src/Lowering.fs:3286` attaches it, then `:3290` calls qualifyModuleNames. Its multi-module arm constructs a new record (`:2700`); the ConditionalWeakTable is keyed by object identity (`src/IR.fs:2929`). `installDeclSpansOf` silently does nothing on that returned object (`:2939`). In a fresh interpreter flow the named guard has no location; in a flow with another program's ambient table it can use stale provenance. **Fix:** attach to the FINAL returned object after qualification, or store provenance in IRProgram and preserve it through copies; clear rather than retain unrelated ambient state when no table exists. Add imported-function guard fixtures run in a separate flow, including execution after lowering another program.

4. **HIGH — user names beginning with lambda are misclassified as anonymous.** `src/Interp/Core.fs:1163` uses `n.StartsWith "lambda"`. A legal named function `lambda_guard` containing a failing match therefore bypasses its declaration locator; the caller/binding supplies the location instead. C++/LLVM still use lambda_guard's recorded declaration. The same applies to a let-bound `lambda_kernel`. **Fix:** distinguish named versus anonymous callables structurally (or match only the exact generated anonymous-name forms); pin these legal names through plain/destructured calls and block-local variants with identical complete diagnostics.

5. **HIGH — the closure evidence overclaims what the committed checks establish.** No W4-3 sweep script, manifest, directory lists, build hashes or per-file outcomes are committed; the commit message and `bench/notes/gap-register.md:122` merely report 949/251. Thus inclusion of sibling-folds, symmetry, omp-reduce, sql-reduce and loops cannot be checked. `tests/OrbitScatterTests.fs:1004` onward checks presence/absence of a negative-zero constant, not base/head byte equality. Its interpreter empty probe is REPRO-only; the default interpreter's sign is not pinned. basic/526 embeds Complex128, while `src/EmitLlvm.fs:106` refuses complex scalar types, so even its real checks do not run in LLVM. CLI's new cases assert the LLVM lane (`src/CliSelfTests.fs:1572,1794`), but not T3 admission or A7c's expected decline; A7c's failing callee stays a call. **Fix:** commit the exact sweep script/manifest with both source/binary identities, knobs, directories, exclusions/refusals and per-file comparisons; split out an LLVM-routable real/generic -zero fixture; add default-interpreter empty sign and generic identity-type probes; assert T3 admission and the A7c refusal reason. Preserve the reported skips (docs 79; LLVM symmetry/functions 57/222, including 19/91 refusals; opt-diff symmetry/functions 38/131). Do not turn these counts into an all-corpus/all-lane claim.

Marker/pass audit: no collision found among source-span producers. Lexer positions start at 1 (`src/Lexer.fs:298`); ParserCore spanOfToken/headSpan/rangeSpan (`:278–295`), Parser's declaration constructors (`src/Parser.fs:658,693`) and ParserGrammar statement spans (`:99,1504`) use those positions. Ast.noSpan (`src/Ast.fs:17`) is all-zero/File=None; mergeSpan (`:913`) merges existing spans. Desugars/static expansion preserve input spans or use noSpan; math/ml/sgs/spectra elaborators synthesize all-zero/File=None spans. REPL's synthetic declaration span (`src/ReplSession.fs:838`) uses columns 1/0; notebook line mapping and IDE spans do not manufacture column -74. Stdlib uses the ordinary parser. Generic substitution copies spans. None produces the sentinel's combined line-0/column-minus-74/file tuple. Its intentional producers are TypedAst.markUnseeded and the rank-k surface desugar; inferExpr transports that surface mark (`src/TypeCheckInfer.fs:212`). Lowering is the sole semantic selector, not literally the sole equality reader.

IRAddIdentity carries type substitution (`src/IR.fs:2759`), survives ExprShape/IRLift, and resolves (`src/IRMono.fs:1662`) before Optimize (`src/Lowering.fs:3161,3189`); real, complex-component and integer constructors are present (`src/IR.fs:930`). Unresolved-open fallback still guesses Float64 -0.0 (`src/IRMono.fs:1688`); no exhaustive generic-instance proof follows from these tests. Sibling reconstruction does NOT recreate +0 after consuming the marker: existing IRReduceCompute legs retain s (`src/Optimize.fs:8553–8557`); newly joined stored unseeded legs use the pre-existing sfIdentitySeed, -(0.0)/1.0, only for literal n>=1 (`:7821,8464`). Prodsum's +0 is intentional (`:8550`). OrbitFold's zero-literal check accepts strict -0; ordinary source -zero still lowers as negation.

Repro/licence audit: the inherited flag reaches lambdas, all three section constructors and nested declarations (`src/Lowering.fs:1265,1490,1553,1623,1780`). Consumers consult it through the shared predicate, PROVIDED the fold callable carries it; NEW 2 is the missing use-context.

| Form | Veto hook |
|---|---|
| C++ ordinary/K-lanes and Path B | CodeGenBinding.fs:4366,4649,5582,5621 — foldReorderLicensed |
| Row-view helper | CodeGenBinding.fs:5878 — inner kernel licence; enclosing fold uses licensedFold |
| Hot-row / cell epilogue / producer-input legs | CodeGenBinding.fs:2869; CodeGenCuda.fs:2115,2240 — same licence |
| Joined simd / thread plan | CodeGenFusion.fs:555,598,607 — per-leg licence/repro veto |
| Orbit-class fold | OrbitFold.fs:469 — every level's licence |
| Compact row classes | CodeGenCuda.fs:2332; EmitLlvm.fs:4749 — OrbitFold.recognise, hence the same veto |
| LLVM ordinary/row/join/cell/hot-row | EmitLlvm.fs:1204,5306,5407,7133,8171 — foldFmfDecorable → shared licence |

Formalism §2.4 (`docs/formalism.md:255`) says: “per function, `where repro`: the emitted body is compiled without contraction, never re-inlined into a contracting caller, and declines reordered folds and library routes”. It does not promise transitive repro for an independently declared function called normally; that builder limitation is reasonable. It does require the caller's own reduce to decline reordering (NEW 2). The route-leg witness covers freshly lifted sections only. Its C++ record check counts >=4 matches and accepts the generic “a leg has no reorder licence” text; it does not identify four distinct repro subjects.

Guard/empty audit: T3's reserved-path edit removes only framePush/framePop and updates the comment (`src/EmitLlvm.fs:4468,4485`); ordinary ragged peeling uses applyKernelFramed false (`:6977`). CLI failureLines compares all selected error/location/frame lines in order, after basename normalization, and rejects LLVM fallback; it filters other output, so “complete” means those diagnostic lines, not all stdout/stderr. runProgram delegates to runProgramMemo (`src/Interp/Run.fs:738`); REPL/session evaluation reaches the same entry (`src/Interp/Repl.fs:148,163`, `src/ReplSession.fs:1289`). No separate missing installer was found for these entry paths; object provenance is the defect. functions/535's restored location substrings are checked by the interpreter differential (`tests/InterpDiff.fs:420`), and the single-module object survives validation unchanged; its reported pass does not cover imports. Empty deferred value versus stored BL3007/BL8003 is now deliberately documented; no existing deferred BL8003 was removed.

| Five claim hooks | Finding |
|---|---|
| Dedicated marker and only lowering seed selection — TypedAst.fs:620; Lowering.fs:545,1170 | Source isolation holds; route selection incomplete (NEW 1). |
| Generic identity resolves before optimization — IR.fs:930,2759; IRMono.fs:1662; Lowering.fs:3161 | Mechanism present, including complex/int; sweep is not a proof of every instance. |
| Repro lifts veto licensed forms — Lowering.fs:1265; CodeGenExprSupport.fs:1779 | Fresh lifts covered; previously declared fold references escape (NEW 2). |
| Declaration table travels with program — Lowering.fs:3286; IR.fs:2936 | False after multi-module qualification (NEW 3). |
| One diagnostic across lanes — CliSelfTests.fs:1775,1794,1807; EmitLlvm.fs:3443 | Targeted comparison exists; name heuristic and multi-module cases absent (NEW 3–4). |

Scope: default-build byte identity to 5ea03056 is not established beyond the unarchived reported sweep; guard text changes are intended, but propagating IsRepro also changes explicitly SEEDED folds' association and lifted arithmetic's contraction, so “REPRO/where repro changes bits only for seedless folds” is too narrow and must be corrected/pinned.

---

## codex-diff-W4-4

_formerly the scratchpad's `round3/reviews/wave4-codex-reviews.md#codex-diff-w4-4` (appended at the wave-4 records commit)_

﻿VERDICT: MERGE AFTER FIXES

Reviewed `5ea03056..d2092835`; files/git and existing artifacts only. No builds, tests, compiler invocations, or oracle execution. Paths/lines below are at d2092835 unless marked BASE. Reported test footers and timings are builder evidence, not independently rerun results.

1. **MEDIUM — the repaired form has no durable regression assertion.** `src/Grad.fs:404`, `src/GradExpand.fs:1556`; no `tests/` file changes. The listed value differentials would also pass with the original n-cell literal, and optimizer off/on comparisons do not compare this unconditional AD rewrite against BASE. Thus none pins the defect being closed. **Fix:** add an emission/IR regression for a routed C6 local at two non-power-of-two extents, asserting a materializing zero map rather than a cotangent `IRArrayLit`, bounded emitted size, and a counted fill in both C++ and LLVM (assert actual LLVM routing). Include a multidimensional C6 case and retain value pins with untouched cotangent cells. Do not use elapsed-time thresholds. This implements the builder preamble's requirement that emission needles live in the owning test block.

2. **MEDIUM — the size change omits the audit explicitly required by the pre-fix gap row.** BASE `bench/notes/gap-register.md:118` makes a fresh per-arm peak-live/store audit the condition for the next size. New `bench/kernels/haloadjoint/spec.md:190-206` substitutes seven-array arithmetic and single-run peak working sets and expressly says no peak-live audit was done. These are scoped observations, but they do not discharge that condition: working-set residency does not establish allocation lifetime or retained named stores. **Fix:** attach the full-size per-arm audit (inputs/named outputs, compiler temporaries, lifetimes, image, thread scratch), and keep any machine-code store verification explicitly pending for the integration measurement gate until performed. Reconcile the LLVM row's “not audited beyond the smoke run” with the actual full-size evidence. Retain the new size only with its required memory audit recorded; do not label the arithmetic estimate as the completed audit.

   Concrete out-of-scope lifetime issue found during this inspection: the saved LLVM artifact below allocates the tape at line 151 and cotangent at 231, clears their ownership slots at 261 and 267/269 during loop reads, then loads those null slots for `blade_free` at 960-963. The pools consequently escape the function cleanup. This is **pre-existing**, not a demonstrated W4-4 regression: unchanged `EmitLlvm.fs:6340` calls `keepPool` for a binding known to any open scope, `keepPool` at 1340 clears its tracking entry in every scope, and BASE's literal also allocates through `allocPool` (`5938`). Record a separate ownership gap; its eventual fix must preserve the owning outer scope while escaping only younger scopes. Repeated gradient calls need a lifetime regression there. Do not silently fold that unrelated repair into W4-4.

Five claim hooks:

| Builder claim | File:line checked | Result |
|---|---|---|
| Only C6 cotangent creation switches; rank-sized AST | `src/Grad.fs:398-404`; `src/GradExpand.fs:1556-1565`; `src/GradNormalize.fs:116,405,525` | Confirmed. One call site; one range slot/parameter per dimension. Literal/ConstFill cotangents and route S retain their old paths and 65536-cell cap. |
| Reuses the large let-rec former | `src/TypeCheckInfer.fs:14115-14125` | Confirmed shape and >4096 threshold. That implementation also types its element zero at `14037-14043`; this AD helper retains the old `fLit 0.0` convention. |
| Replicate would rebuild the literal | `src/Lowering.fs:854-873` | Confirmed for literal counts 0..1,000,000, including the new 999,999-cell cotangent. The new form instead reaches `IRCompute(IRApplyCombinator ...)`, with an `IRRange` input. No new IR node. |
| All lanes fill, with +0.0 | saved C++ artifact `:273-279`; saved LLVM artifact `:231-246`; `src/EmitLlvm.fs:5574-5577,5956-5965,6000-6005`; `src/Interp/Loops.fs:899,1078-1084` | Confirmed by source and existing artifacts. LLVM is a counted loop storing `double 0x0000000000000000`, not an n-element global or unrolled literal; C++ stores `0.0`; interpreter allocates and executes the map. |
| Catalog size and ordered-oracle pin updated, smoke preserved | `bench/kernels/haloadjoint/kernel.json:6-12`; `bench/validation/ref_haloadjoint.py:42,55-76`; `bench/validation/pins-B.json:45-46`; `bench/notes/kernel-list.md:310,376` | Confirmed declarations and pin diff: full 1,000,003 / 982246.50221908255 / 999284.5857284111; smoke 1013 unchanged. Oracle ordering is unchanged. Regeneration itself is reported, not rerun. |

Artifact paths relative to the reviewed worktree:
`bench/.build/haloadjoint/full/serial-repro-native/blade-cpp/generated/haloadjoint.cpp` and `bench/.build/haloadjoint/full/serial-repro-native/blade-llvm/generated/haloadjoint.ll`. The saved LLVM build log names `.ll` emission; these are local build evidence, not committed golden files.

Value/coverage limits: both old and new C6 constructors use `fLit 0.0`; the zero's sign is positive and its default scalar type remains Float64. This does not establish new Float32 cotangent support: `Grad.fs:112-128` already builds Float64 cotangent types, and the listed C6 witnesses are not Float32 coverage. Complex parameters remain explicitly refused by shared AD classification (`GradNormalize.fs:1061-1074`). The reported LLVM differential exercises five of fourteen changed witnesses; nine pre-existing unsupported forms are not nine verified LLVM executions. No new semantic contract or first-failure change was identified by inspection, so a formalism rewrite is not required merely for this representation change. `docs/features.md:607` has no size cap; leaving it and CLAUDE.md unchanged is consistent.

Timing/records: G68 records all five after sizes, four fresh before sizes, the historical unfinished million-cell run, affinity, shared load, and single-run limitations. For a 199.88x extent increase (5003 -> 1,000,003), after `lower` is 1.21x and compileFile 1.17x: evidence of approximately flat compiler work over these samples, not a measured linear slope or a general proof. No generated-program speedup is established. The full-size all-regime check and smoke ordered comparison are reported separately; checksum equality is not a raw-bit signed-zero test. The dead forward-tape re-add and lost forward-fold licence remain explicitly OPEN inside the FIXED zero-buffer row.

Scope: untargeted programs never execute the sole changed C6 call, so their AST/fresh-ID stream and both unchanged emitters remain identical; this is the source-level isolation mechanism, not a new exhaustive two-lane census. All corpus pins are unchanged, and the sole benchmark pin edit is haloadjoint/full. `git diff --check` is clean; the two F# files retain index LF/worktree CRLF.
