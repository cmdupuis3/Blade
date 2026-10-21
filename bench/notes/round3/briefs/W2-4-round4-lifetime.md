# W2-4 round 4 -- close the lifetime class: a cleanup EVENT every pass, every renderer and both lanes honour

Context: your round 2 (merged as 3a3925ac on worktree-bench-fortran) replaced the join-inferred release with
`InlineReturnTable`. The third Codex look (`round3/reviews/codex-diff-W2-4-3.md`) is DO NOT MERGE: NEW 2 resolved, NEW 1
partial, and FOUR holes of the same class remain. Read it in full first. The class: the release boundary is a side-table fact
about an IR position, so every pass that reshapes the IR after it is recorded, every renderer that bypasses `genBinding`, and
the other lane's scope machinery can each silently invalidate it. The fix must make the boundary a FIRST-CLASS part of the IR.

## The four holes (all must close)

1. The sibling rewrite can delete the recorded boundary: the key is the last SfInl unit chosen BEFORE the joins are rebuilt;
   an unused explicit per-leg join at the end of the callee is accepted as a member, dropped (`dropUnits`), and the entry
   dangles -- no release in main (witness: 031's two extents, the callee ending with an unused Jx join and returning the
   earlier scalar s).
2. CSE runs AFTER the table is recorded (Lowering ~3136 then ~3172): its available-value map ignores cleanup, so a caller's
   map equal to the callee's producer is merged INTO the callee's pool and its later fold reads the freed pool (witness: the
   same named pure cell kernel for y2 and a caller z, z folded by a cube kernel, serial repro, BLAS off, reuse off, CSE on);
   independently CSE can drop the fresh return let (a duplicate `helper(a)` call) and the key dies.
3. Ordinary function-body return lets never reach the new first dispatch arm: `genFuncBodyScoped` dispatches its own lets
   (CodeGen.fs ~214 / ~232), scalar expressions go through exprToCpp (~749-756), plain computed maps bypass genBinding
   (~329-358) -- a function caller gets no release (witness: the existing function-body test with the return `s + 1.0`, plus
   a caller BL8013 after the call: the as-written program had released y2, the inline never does).
4. The LLVM lane DOES free the uninlined callee's pools: a non-capturing scalar call is outlined, its body opens a pool
   scope, B1 allocates through allocPool, and the scalar return pops the scope with emitFrees (EmitLlvm ~7605 / ~4721 /
   ~7649 / ~1352). The inline moves the allocation into main where no scope records it and the lane never reads the table:
   019 / saxpy's y2 is retained to exit. The "frees no pool in either form" claim and the LLVM lifetime-matched attribution
   are WITHDRAWN until implemented; a no-free assertion would pin the wrong behaviour.

## Required design: an IR-level cleanup event

- A new IR statement node (name it `IRReleasePools` or similar: the inlined call's identity + the ordered pool bindings it
  owns), INSERTED by the sibling pass after join reconstruction and after the scalar return has been evaluated into its
  final let -- at the position the as-written call returned. Not a table: a node in the let chain.
- Pass discipline: NO pass removes or moves it. CSE treats it as a barrier for the pools it names (no value equivalence
  may cross it: a later expression equal to a callee computation is NOT merged into the callee's pool; the fresh return let
  is kept); let-hoisting, freeze-idiom, slice-write fusion, kernel-invariant hoisting, pool reuse and the join rewrite
  must not move a read of a named pool past it, nor drop it. Scratch pool reuse must not join a callee pool with a caller
  pool across the event (retain lifetime regions in reuse planning, or exclude donors / reusers across the boundary and
  say so). Add an IR validation rule (`IRValidate`): no reference to a named pool after its release node; a release node
  names only pools bound inside the inline it closes.
- Every renderer honours it: the C++ dispatch (`genBinding`), `genFuncBodyScoped`, expression / loop-body let renderers;
  the LLVM lane renders it as the pool frees plus removal from the enclosing scope's tracking (so the scope's own pop does
  not free twice); the interpreter executes it as a no-op (values unaffected) but must accept the node (`test interp`).
- Fail closed: where a lane or renderer cannot honour the event (document which), the planner DECLINES the kept inline for
  that program (the call stays a call; no legs; recorded `declined -- lifetime event not honoured by <where>`), never a
  silent retention.
- First-failure: the release sits after the scalar return's evaluation and before the next caller statement, so a caller
  panic after the call sees the pools released exactly as the as-written program did; a panic inside the callee leaks on
  both (equal).

## Witnesses (corpus + Optimize / CellEpilogue / LLVM tests, both lanes, gate on vs off, ASan where executable)

The four above (unused final join + earlier atom return; the CSE-merged caller map with a cube fold, CSE on; the function
caller with a non-atom return and a caller BL8013 after the call, release before the failing binding; LLVM 019 / 031
alloc / free relationships and ORDER in main and in a function caller), plus: scratch reuse donor / reuser inside the callee
(no lifetime change vs gate off, or the documented exclusion); the omp-test-mode arm asserting the ACTUAL decline (no fused
route), not just safe releases; the IR validation rule firing on a hand-built violation. Keep 030 / 031.

## Records

G69: the event design, the four closed holes, what is declined where; CLAUDE.md row and docs/features.md: "released once
per inlined call at an IR-level return boundary honoured by every pass and both lanes" ONLY if that is what landed; the
LLVM lifetime-matched saxpy attribution stays withdrawn until the orchestrator re-measures the saxpy cells after this round.

## Scope / timing

ONE commit on top of the integration tip at launch (the orchestrator names it; it will be after the wave-2 A/B). No full
suite; blocks: sibling-folds, cell-epilogue, optimize, omp-coverage, omp-reduce, cli, llvm facts, llvm sibling-folds,
interp sibling-folds, opt-diff sibling-folds, docs, corpus-wiring, plus orbit-scatter, sql, index-types once. A fourth Codex
look follows; nothing merges before it passes.
