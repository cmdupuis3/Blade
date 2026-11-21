# Wave 2 workstream A -- migration manifest (sharing partition, declared layout)

The P1 sweep of [plan-identity-transpose-compute.md](plan-identity-transpose-compute.md)
§2.2-§2.4 (wave 2, workstream A), against a scratch build of 2908d95d (the
branch tip before the change). A LANGUAGE CHANGE with no knob is invisible to
`opt-diff`, `test interp` and `diff-oracle` (§8), so this sweep and the corpus
pins are its gate.

## Method

- Candidates: every `tests/corpus/**` and `examples/**` `.blade` file holding
  `where comm`, `where anticomm`, `gram(`, `reynolds(` (the sites that make a
  declaration identity-sensitive, and `gram`'s same-array test) or a
  `(rejects)` probe (a refusal the declared-layout re-check could turn into an
  acceptance): **1252 files**.
- Per file: `blade plan` with the new compiler (every `[sharing-partition v1]`
  record), `blade check` with both compilers (exit code and first `error[BL..]`
  line), and `blade emit` with both compilers compared on stdout for every file
  either compiler accepts (**335 files**).
- Not swept file by file: the multi-file directories (run whole by `blade test
  interp multifile`, 45/0, values pinned) and the stdlib (`stdlib/stats.blade`
  mentions `where comm` only in a comment). Elaborator-generated declarations
  (math / ml / ppl / AD) are compiler-synthesized and never specialized, so
  they can change a plan record, never an elaboration.

## Result

No pre-existing file other than `symmetry/149` (the P0 before-case, pinned to
change) changes its emission, its accepted / refused status, or its first
diagnostic. No pre-existing file gains a `[sharing-partition v1]` record. Every
row below is the before-case or a new witness.

| File | Change | Before (2908d95d) | After |
|---|---|---|---|
| symmetry/149_comm_helper_two_params_dense_today | output + type + plan (pin updated) | `PAA = f(A, A)` dense 3x3 `Array<Float64 like I, I>`; `[symmetric-storage v1] declined -- ... different arrays (x, y)` | packed `SymIdx<2, I>` `[[1.0, 2.0, 3.0], [4.0, 6.0], [9.0]]`; `[sharing-partition v1] f: applied [partition [0,0] of (x, y); instance f__share1 ...]`, `[symmetric-storage v1] applied ... operands: x, y`, `[triangular-iteration v1] applied ... speedup x2` |
| symmetry/161_sharing_partition_same_vs_distinct_args | new | `SymIdx` ascription of `f(A, A)` BL3001 | `f(A, A)` packed, `f(A, B)` / `f(A, C)` dense; applied record |
| symmetry/162_sharing_partition_transitive_helper | new | `g(A, A)` dense | `g(A, A)` packed (g's and f's [0,0] instances); two applied records |
| symmetry/163_sharing_partition_three_params_adjacent_only | new | `SymIdx<3, I>` / `SymIdx<2, I>, I` ascriptions BL3001 | `[0,0,0]` packed `SymIdx<3, I>`, `[0,0,1]` `SymIdx<2, I>, I`, `[0,1,0]` dense (adjacent-only rule), BL4010 on the direct `(A, B, A)` spelling |
| symmetry/164_sharing_partition_declared_dense_return | new | `mk(a0, a0)` dense | `make_g(a0, a0)` dense, unchanged (`declined -- the declared return type fixes the layout ...`); `mk(a0, a0)` packed |
| symmetry/165_dense_ascription_accepted_dense_elaboration | new | BL3001 (`let P: Array<Float64 like I, I> = method_for(A, A) <@> comm ...`, `gram(M, M)`) | accepted, dense; `[sharing-partition v1] f: declined -- a dense ascription fixes the layout ...` |
| symmetry/166_sharing_partition_callbacks_stay_distinct | new | -- | dense through a callable parameter; `r: declined -- a recursive declaration` |
| symmetry/167_sharing_partition_alias_heads_and_dense_bodies | new | -- | alias head keeps the generic dispatch; declared-dense bodies (plain, branch, tuple) re-checked dense |
| ad-jvp-comb/115_gram_body_at_shared_args_independent_slots | new | values identical | AD values and per-slot cotangents identical; `floss__grad` / `floss__jvp: declined -- a compiler-synthesized declaration ... keeps independent argument slots`; the ordinary `floss(a0, a0)` is the [0,0] instance (same primal) |
| multifile/043_imported_comm_helper_sharing_instance | new | -- | `PairLib.pair(xs, xs)` packed, reading PairLib's `scale` |

10 rows; 1 pre-existing file changed (by design), 9 new witnesses.
