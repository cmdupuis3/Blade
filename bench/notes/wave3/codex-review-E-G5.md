**Overall verdict: REJECT the brief as a complete build specification; ACCEPT WITH CHANGES the narrow persistent same-x schedule.**

Reviewed the wave-3 brief, round-2 section 3, objections 14–16, `CLAUDE.md`, and the named hooks. No files were edited, and no builds or tests were run.

**Source snapshot:** tracked files were clean at the start, at `ccde7e4e318a529ee9432b578c3e615ca1bbd73f`. `src/Optimize.fs` began changing concurrently during inspection. Its citations below refer to that immutable commit, verified with `git show HEAD:src/Optimize.fs`; they do not assess the incomplete concurrent implementation. Counterexamples below are deductions from source guards, not executed reproductions.

| Claim | Verdict | Evidence / qualification |
|---|---|---|
| Fixed x ownership permits barrier-free same-x earlier-row dependencies | **ACCEPT** | The expander reads each admitted row at `k`; fixed actual-team partitions preserve that ownership. `src/Optimize.fs:3280–3282`; `src/CodeGenLoopNest.fs:857–860`. |
| `c >= 1` or literal `r < lo` sufficiently proves safe history reads | **REJECT** | Missing lower bounds, initialization, buffer provenance, and enclosing-loop validation. |
| Seed row and its fold remain before the region | **ACCEPT WITH CHANGES** | Existing row-fold fusion constructs this ordering, but the proposed admission must retain its prerequisites. `src/Optimize.fs:3701–3708`. |
| Root-only omp admission preserves spatial permission | **ACCEPT WITH CHANGES** | Requires explicit root propagation and parameter-to-axis validation; the global `tilePlainKernel` must remain unchanged. |
| `scheduleMayWriteIn` must be false on the whole time-loop body | **REJECT** | An isolated loop body writes an externally bound trajectory buffer and therefore fails this predicate. |
| Licensed `Square` row partials, combined ascending, are legal | **ACCEPT WITH CHANGES** | Inner builtin `+` permits reassociation, subject to repro veto, type checks, actual-team handling, and lane configuration. |
| Complete-row slots preserve strict numerical chains | **ACCEPT WITH CHANGES** | Correct for independently evaluable, pure, total rows with preserved FP context and original seeds. Existing row-fold admission does not establish all of these. |
| The spatial omp request authorizes the parallel row post-pass | **REJECT** | The post-pass threads the time/row axis; the request names x. Existing G35 permission checks do not establish this transfer. |
| Threads-off serial emission stays byte-identical to wave 2 | **REJECT as written** | Blanket suppression of `CellFoldTable` changes the repro serial schedule; the mandatory census comment also changes literal emitted bytes. |
| A `CellFoldTable`-style table automatically survives later passes | **REJECT** | Keys can survive while bodies, references, owners, and emission contexts change. |
| The listed fallbacks cover every silent omp loss | **REJECT** | Expression emission, missing metadata, LLVM callable-only census, and emission-time protocol failures need explicit coverage. |
| Existing `CellEpilogueTests` machinery directly proves the new contract | **ACCEPT WITH CHANGES** | Useful infrastructure, but its controls, team assertions, and tolerance branch need adaptation. |

1. **The proposed whole-body write predicate rejects the intended recurrence.**

   `exprMayWriteWith` analyzes its argument with empty parameter maps. Its local-storage set contains only lets **inside that argument** (`src/Optimize.fs:950–953,1118–1121`). An indexed assignment strips down to its base; an array variable not locally bound is external storage (`src/Optimize.fs:988–1026`).

   Consequently, asking `writes timeLoopBody` about `BUF[n][k] <- value` returns true: `BUF` was allocated outside that body. After row-fold fusion, `acc <- F(acc,p)` introduces another outside binding.

   Existing row-fold fusion deliberately asks `writes block`, where the buffer allocation is included (`src/Optimize.fs:3598,3634–3640`).

   **Required correction:** analyze an ownership-aware enclosing region, then independently whitelist the exact trajectory stores and, where applicable, accumulator update. Do not fix this by ignoring arbitrary assignments. Keep the slice, callees, deferred producers, and intervening evaluations under the shared write analysis.

2. **“Strictly earlier” is not “in bounds and initialized.”**

   For unguarded `n-c`, require `1 <= c <= lo`, as well as valid time-loop bounds. For an unguarded constant row, require **`0 <= r < lo`**, not merely `r < lo`.

   The frontend explicitly makes these distinctions: `guardsFor` tests `loopStart-c < 0`, and a constant is safe only within `[0,loopStart)` (`src/TypeCheckInfer.fs:14154–14174`). Its comment explicitly identifies negative history indexing as reading before the buffer (`src/TypeCheckInfer.fs:14140–14147`).

   The brief correctly notes that guarded lag-2 currently declines. That is a reason to preserve the guard/refusal, not evidence that `c >= 1` alone is sufficient.

   **Refuse:** negative constants, lags reaching before zero, current/future rows, unknown arithmetic, dynamic bounds, and any guard whose semantics the new form cannot preserve.

3. **Freshness, full coverage, and seed readiness must apply even when no row fold fuses.**

   The existing slice-copy matcher checks the last index and copy-loop shape; it does not establish a fresh rank-2 trajectory, full initialization, or time-loop coverage (`src/Optimize.fs:3336–3346`).

   The stronger checks currently belong to **row-fold fusion**: fresh-buffer shape, final counted loop, matching leading extent, `lo` restricted to 0/1, permitted stores, and seed handling (`src/Optimize.fs:3594–3643`). A trajectory-only RecStep entry cannot borrow those checks from a pass that never fires.

   Also, `seedWrites` is implemented as “some pre-statement mentions the buffer,” not a complete-row initialization proof (`src/Optimize.fs:3630–3631`). Normal desugaring supplies the stronger invariant through `sliceCopyStmts` (`src/TypeCheckInfer.fs:14116–14138`).

   **Required correction:** retain trusted recursive-array provenance or prove allocation, exact rank/layout, matching extents, one complete store per `(n,k)`, and initialization of every pre-region row read. Preserve row-zero folding exactly once for `lo=1`; add none for `lo=0`.

4. **The brief needs an explicit inherited refusal inventory.**

   Relaxing only the omp part must retain these existing refusals:

   | Area | Conditions that must still decline |
   |---|---|
   | Row/storage | Nonliteral extent; nonplain, symmetric, higher-rank, virtual, or nonscalar row; unequal operand extents. `src/Optimize.fs:3212–3221,3304–3305`. |
   | Map structure | Reynolds, speedup metadata, tdims, nonzero kernel input/output ranks, structured/triangular levels, unsupported loop form, incompatible multioperand traversal, inconsistent metadata lengths. `src/IRMono.fs:2547–2563`. |
   | Kernel | Comm/anticomm metadata, CUDA/MPI, static/arity-polymorphic callable, wrong arity; captures, unresolved kernels, calls/matches/loops in scalar kernels, impurity, possible abort. `src/IRMono.fs:2565–2569`; `src/Optimize.fs:3229–3232,3298–3316`. |
   | Inlined function | Foreign-module, recursive, capturing, static, arity-polymorphic or parallel callee; non-row arguments/results. `src/Optimize.fs:3317–3329`. |
   | Producer | Deferred-value forcing, whole-row conditionals/matches, nonpointwise forms, unknown aliases/binders, slice escaping into the remaining body. `src/Optimize.fs:3287–3293,3330–3332,3373–3393`. |
   | Time body | Additional writers, extra cell loops, nonunit tails, early breaks, explicit guards and recognized freeze scaffolding, unsupported intervening statements. `src/Optimize.fs:3632–3633,3671–3684,3710–3711`; `src/TypeCheckInfer.fs:14308–14337`. |

   These conditions must be checked before erasing the requesting map, or retained as mandatory final-emission validation with an explicit fallback census.

5. **`outsideOk` is a local alias-shape check, not a complete dependence certificate.**

   It follows direct aliases `let x = y`, but accepts other initializers merely when they do not syntactically mention `bufId` (`src/Optimize.fs:3384–3388`). Thus checking only the direct `BUF(idx)` arm does not establish the provenance of every accepted outside operand.

   For example, the predicate itself does not resolve `r = alias_of_BUF(index)` through the indexed base before taking the “does not mention BUF” branch. This is a limitation of the predicate, not a demonstrated source-level exploit.

   **Required correction:** prove each accepted input is either immutable storage disjoint from the fresh trajectory or an exact same-x view of an admitted history row. Refuse unresolved aliases, whole-prefix access, shifted/reversed/gathered history, current/future-row access, or an escaping trajectory alias. Verify all lag-view lets retained in the emitted body, including unused ones.

6. **Root permission must follow the returned value, never operand traversal.**

   The current expander traverses let initializers, let bodies, map operands, call arguments, and inlined callee bodies (`src/Optimize.fs:3294–3309,3317–3327`). A mutable “found omp somewhere” flag would accidentally promote a nested stage request.

   Root eligibility must pass through the slice’s compute wrapper and returned callee/let-body spine; operand and initializer traversal must disable it. A final result alias needs an explicit rule—otherwise refuse it while retaining the original request.

   `IsOmpParallel` also is not the complete axis licence. Lowering separately produces `(parameter index, depth)` pairs (`src/Lowering.fs:279–294`), and loop construction interprets those pairs against actual operand levels (`src/IRStorage.fs:1015–1071`).

   **Required correction:** preserve the requesting callable, resolved spatial level, and provenance. Validate parameter indices/depths; explicitly handle or decline unresolved/bare requests. Do not globally weaken `tilePlainKernel`.

7. **The claim that every worker private is a scalar let is stronger than the expander proves.**

   The expander checks scalar **return type**, forbids calls/matches/loops, and applies purity/abort predicates (`src/Optimize.fs:3229–3232,3300–3305`). Those predicates do not constitute a closed scalar-expression whitelist: `tilePureBody` otherwise accepts nodes, and `irNodeMayAbort` classifies array literals as nonaborting on their own (`src/IRMono.fs:2457–2474`; `src/IR.fs:2551`).

   Thus these checks alone do not prove that every local binding is scalar or that a worker cannot allocate.

   **Required correction:** validate the expanded cell chain itself. Refuse unsupported aggregate/array temporaries, deferred materialization, provider/display operations, unknown calls, or any allocation/evaluation outside the promised worker model. Keep proven scalar conditionals legal; they are distinct from selecting whole rows.

8. **`where repro` metadata can disappear during body splicing.**

   Neither the plain-kernel predicate nor slice callee admission rejects `IsRepro` (`src/IRMono.fs:2565–2569`; `src/Optimize.fs:3317–3327`). Scalarization substitutes the callable’s body directly. Row-fold fusion similarly freshens and substitutes K/F bodies (`src/Optimize.fs:3555–3572`).

   But repro emission is attached to callable context and its function attribute (`src/CodeGen.fs:1534–1556`; `src/cpp/blade_portability.hpp:276–280`). `rowKernelCellTerm` records only the `IRProdSum` shape; it does not preserve K’s repro demand (`src/Optimize.fs:2300–2304`).

   A global `fpReassocEnabled()` check does not recover erased per-callable metadata: it is simply the inverse of the build-wide repro predicate (`src/CodeGenState.fs:744`; `src/Toolchain.fs:138–140`).

   **Required correction:** preserve repro/contraction context for the root kernel, every inlined stage/function, K, and F—or decline such inlining. Preserve the `BLADE_FP_REASSOC=off` compatibility veto too. The sibling-fold pass already explicitly refuses a repro callee (`src/Optimize.fs:4190`).

9. **The strict post-pass needs new totality admission for K and F.**

   Existing row-fold fusion explicitly permits K/F to abort; its documentation accepts changed first-abort ordering between serial evaluations (`src/Optimize.fs:3467–3472,3490–3501`). The five-party write checks do not turn this into a no-abort guarantee.

   G35 intentionally has a stronger contract: its concurrent row evaluator uses a closed totality whitelist because panic coordinates would otherwise become arrival-order dependent. It audits **both** the row body and outer fold kernel (`src/CodeGenBinding.fs:35–49,97–110`).

   **Refuse unless separately proved safe:** integer division/modulo, potentially negative integer powers, float-to-int conversion, failing special functions, unproven subscripts, recursive/unknown calls, writes, deferred evaluation, and unsupported array-level operations. See `src/IRTotality.fs:119–163,173–243`.

   Reusing `tileMayAbort` naively also will not admit the target: `IRProdSum` itself is classified as possibly aborting (`src/IR.fs:2556`). The new proof must specifically discharge a complete self-dot’s extent/index safety.

10. **The strict post-pass changes the licensed parallel axis.**

    The source request is on the final **spatial** map. The proposed `BLADE_OMP_PARALLEL_FOR over n` evaluates K concurrently across **rows/time**.

    Existing Path B accepts a map request only when it licenses the traversal’s actual outer level; G35 is reached through that request gate (`src/CodeGenBinding.fs:4392–4410,4422–4429,4450–4453`). Round-2 section 3 explicitly says the proposed request names X, never Step (`bench/notes/round2/brief-E-parallel-permission.md:135–144`).

    The completed trajectory removes numerical dependencies between these K evaluations, but does not itself establish the claimed permission transfer.

    **Required correction:** retain a serial post-pass under the existing spatial permission, obtain a request covering the row evaluation, or explicitly specify and justify a new structural threading policy. Calling it “G35’s discipline” is insufficient.

11. **The `double`/`Square` sketch needs type and lane guards.**

    Slice admission permits any scalar element type, and row-fold admission permits any scalar return (`src/Optimize.fs:3217–3220,3497–3501`). The sketch nevertheless allocates `double` slots and calls `sq<double,8>`.

    Existing prodsum emission derives its element type and selects lanes only for supported float/double storage, respecting `BLADE_FOLD_LANES` and configured lane count (`src/CodeGenExpr.fs:379–397,469–496`). Existing chunk planning explicitly preserves the lane-off case (`src/CodeGenBinding.fs:4475–4479`).

    **Required correction:** either restrict v1 explicitly to dense Float64 trajectories and compatible scalar results, or derive all partial/slot/accumulator types and rounding points. Honor lane-off and K=4 configurations. `Square` proves the expression shape; it is not a type, FP-policy, or totality certificate.

12. **Ascending partial combination is legal only under the inner row licence; it is not repro.**

    The builtin-add interpretation of prodsum is explicit (`src/CodeGenExpr.fs:398–405`). Therefore splitting an eligible self-dot into contiguous worker partials is defensible in licensed mode. The original ascending outer F chain and its seed must remain unchanged.

    This does **not** extend to arbitrary K merely because the spatial map requests omp or F is commutative. Fold permission and request are separate, and repro vetoes reassociation (`src/CodeGenExprSupport.fs:1737–1776`).

    The brief correctly avoids promising licensed bit identity across team sizes. Retain that distinction; fixed-order combination only promises a fixed schedule for a fixed actual team and implementation.

13. **Empty work and actual-team behavior need explicit semantics.**

    `pathBTeamOpen` bounds the requested team, guards empty extents, and partitions using the **actual** team (`src/CodeGenLoopNest.fs:845–860`). Existing Path B records whether chunks contributed and skips noncontributors during combination (`src/CodeGenLoopNest.fs:2382–2393`).

    The brief’s prose “clamp to `[1,E]`” is undefined when `E=0`. A zero-width trajectory also does not necessarily eliminate row folds: K on each empty row and F’s repeated updates still have their original semantics.

    **Required correction:** distinguish no spatial cells from no time iterations; preserve all required empty-row fold evaluations. Use actual-team bounds for publication/consumption, test requested 8 versus actual 2, and release both partial and slot allocations. Existing slot emission explicitly frees its storage (`src/CodeGenLoopNest.fs:2377–2380`).

14. **Suppressing `CellFoldTable` at admission breaks the serial-control promise.**

    In repro builds, current code uses `CellFoldTable` to accumulate the row self-dot immediately behind each cell store and replaces the later fold let with that accumulator (`src/CodeGenBinding.fs:5957–5965,5996–6007,6014–6037`). This is not merely a decision annotation.

    If brief lines 66–67 mean “do not record B2r for any admitted RecStep entry,” then threads-off, LLVM, and failed-emission fallbacks lose that existing optimization. The numerical result may remain equal under contract-off, but the serial emission and locality change.

    **Required correction:** suppress B2r only for an actually selected persistent emission, with atomic restoration on fallback. Keep the original serial schedule available.

    Also distinguish **numeric bit identity**, **normalized computational emission**, and **literal text identity**: the required new census comment already makes literal wave-2/wave-3 text differ.

15. **An IRId lookup must not substitute for final proof validation.**

    The following are the actual rewrite boundaries:

    | Boundary | What can change; required handling |
    |---|---|
    | Row-fold fusion | Freshens K/F lets, introduces row/fold/update IDs, replaces the time-loop tail. Attach facts from the final transformed loop. `src/Optimize.fs:3551–3572,3653–3661`. |
    | Invariant hoisting | Creates new callable IDs, bodies, captures, and operand lets. A stored callable/body snapshot can become stale. `src/Optimize.fs:2023–2041`. |
    | Sibling-fold sharing | Runs after hoisting and before IRLift/CSE. Audit changed fold structure; its current loop/assignment barriers limit applicability to the recurrence itself. `src/Optimize.fs:4306,4897–4902`. |
    | IRLift | Floats nested lets, inserts temporaries, wraps callable bodies. Loop iterator IDs are retained, so a table lookup may still succeed after shape changes. `src/IRLift.fs:696–709,1227–1228,1355–1363`. |
    | `forceCallableBody` | Rewrites deferred/compute structure after IRLift. `src/Lowering.fs:3041–3064`. |
    | CSE | Removes duplicate lets and rewrites references/captures. **It currently skips bodies containing assignments or counted loops**, so it does not renumber this benchmark’s recurrence. Its module-wide reference fixup remains relevant to stored dependencies. `src/Optimize.fs:1450–1457,1533–1574`. |
    | Later planners | Pool reuse, hot-row folds, compaction, and compact-row plans introduce competing storage/scheduling decisions, not general ID renumbering. `src/Lowering.fs:3073–3088`. |
    | Dead-polymorph elimination | Can remove unreachable polymorphic owners/bindings. It does **not** remove every unreachable concrete callable. Prune plans for removed owners. `src/IRValidate.fs:128–187`. |
    | Qualification/disambiguation | Rename functions/bindings without renumbering IDs. Preserve source provenance; resolve emitted names from current context. `src/Lowering.fs:2631–2652`; `src/CodeGen.fs:3679–3695`. |
    | Module assembly | Explicitly merges module metadata. A new IRModule field needs merge wiring; an AsyncLocal table instead needs program-lifetime discipline. These are different designs. `src/CodeGen.fs:3867–3885`. |

    **Required correction:** validate the final owner, bounds, buffer, exact stores, all reads, fold/update bodies, and referenced IDs before emission. A matching iterator key plus superficially matching let-chain is insufficient.

16. **Table lifetime, multiple sites, and emission handshakes need a defined protocol.**

    Existing `CellFoldTable` is AsyncLocal and reset once per lowering because IDs restart across compilations (`src/Optimize.fs:2286–2298`; `src/Lowering.fs:2873–2881`). Its emission handshake is set only after emitting the chain and cleared when consumed (`src/CodeGenBinding.fs:5998–6001,6031–6033`).

    A RecStep table keyed by both time and cell iterator must define:

    - Whether multiple candidate cell loops under one time loop are refused before insertion.
    - How owner deletion or cloning invalidates every associated key/reference.
    - Reset behavior between compilations and repeated emission of one IR.
    - Exception-safe restoration of temporary chunk bounds and selected-fold state.
    - Atomic fallback: no suppressed fold, changed cell bounds, or lost B2r merely because planning succeeded.

    The existing table pattern supplies storage and a handshake, not these proofs.

17. **The brief misses concrete silent-census escape routes.**

    Every live erased request needs a final disposition. Cover each of these paths:

    | Escape route | Source evidence and required response |
    |---|---|
    | Request erased before plan commitment | Successful slice fusion replaces the map/call with plain `IRForRange`; normal request discovery then has no map to inspect. Commit permission/proof atomically or retain the original expression. `src/Optimize.fs:3395–3400`. |
    | Missing/stale table entry | Existing `genForRangeBinding` emits a plain counted loop by default. A missing lookup cannot distinguish “never requested” from “metadata lost.” Preserve request provenance separately from schedule validity. `src/CodeGenBinding.fs:5929–5943,6045–6055`. |
    | Expression-context recurrence | `renderUnitStmts` has its own ordinary `IRForRange` emitter and does not call `genForRangeBinding`. Add threading support or a block-form census there. `src/CodeGenExpr.fs:1729–1740`. |
    | Threads disabled before request handling | Consult the request even when threading is disabled; include all canonical suppression spellings, not just `"1"`. `src/Toolchain.fs:157–163`. |
    | Shape/type/totality/permission decline after inlining | Every such refusal needs the census, not only the three reasons listed in the brief. The shared helper records both comment and decision. `src/CodeGenState.fs:951–955`. |
    | LLVM discovers only callables | `ompRequestedKernels` scans surviving functions’ `IsOmpParallel`, not side-table entries or erased sites. Union retained request provenance into the census. Do not assume inlining necessarily deletes concrete lambdas—the current dead pass retains them. `src/EmitLlvm.fs:7976–7981`; `src/IRValidate.fs:186–187`. |
    | Construct emitted without plan record | `pathBTeamOpen` emits strings; callers separately invoke `recordOmpConstruct`. Reusing the helper alone does not create an applied census record. `src/CodeGenLoopNest.fs:845–861,1638–1648`. |

    Conversely, knob-off or fusion refusal may leave a genuinely threaded original map. Report **persistent transformation declined**, not falsely “omp emitted serial.” Round-2 already requires that distinction (`bench/notes/round2/brief-E-parallel-permission.md:242–245`).

18. **A pragma needle cannot prove a team ran.**

    The portability header intentionally expands `BLADE_OMP_PARALLEL_NUM_THREADS` to nothing without `_OPENMP`; query wrappers then report one thread (`src/cpp/blade_portability.hpp:249–272`). Normal g++ compilation requests `-fopenmp` (`src/Build.fs:902`), so this is not evidence that ordinary builds silently lose OpenMP. It is a real boundary for generated-source/manual/CUDA-host compilation.

    Separately, requested team size is not actual team size—the Path B helper explicitly accounts for this.

    **Required correction:** distinguish an emitted construct from observed execution. Instrument both the persistent trajectory region and the strict row post-pass independently. A later post-pass team must not conceal a serial trajectory computation.

19. **The existing raw-bit harness must change before it can validate this feature.**

    Current recur has no requesting step map, uses `9 × 1003`, and expects the B2r `__cfc_` form (`tests/CellEpilogueTests.fs:109–130`). Current comparisons toggle `BLADE_CELL_EPILOGUE` while keeping the same threading regime (`tests/CellEpilogueTests.fs:279–315`).

    Important traps:

    - Team assertions are conditional on a `PARALLEL_NUM_THREADS` needle; current repro recur does not assert a team (`tests/CellEpilogueTests.fs:141–153,324–333`).
    - Bitwise equality currently includes `RAW team`; a threaded-versus-threads-off comparison must compare team observations separately (`tests/CellEpilogueTests.fs:196,320–336`).
    - The tolerance branch checks scalar decimal values but does **not require trajectory hashes to match** (`tests/CellEpilogueTests.fs:337–345`).
    - Scalar `%a` is useful for finite values but is not a complete NaN-payload bit encoding; pools use a hash, not an exact byte dump (`tests/CellEpilogueTests.fs:179,187–191`).

    **Required additions:** threads-on T=1/3/8 against an independently emitted threads-off control; requested 8/actual 2; separate observation of each region; exact trajectory comparison even when the total uses tolerance; repro scope tests; non-Square pure-total K; nonassociative F and nonzero seed; seed-only/empty cases; every admission refusal above; repeated compilation/emission, multiple sites/modules, and expression-context fallback census.

    Keep `opt-diff`: interpreter agreement alone cannot detect a shared optimizer error (`CLAUDE.md:130–134`).

Codex session ID: 01a15b18-234f-79f0-bc94-ea7d067f13ce
Resume in Codex: codex resume 01a15b18-234f-79f0-bc94-ea7d067f13ce
