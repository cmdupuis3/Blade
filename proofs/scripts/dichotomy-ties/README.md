# dichotomy-ties — scripts behind `proofs/BladeDichotomyTies.v`

Exact (rational / integer / GF(p)) computations on the per-dimension-canonical
scalar-access model with tie-breaking rules, below the free cell of the r = 3
storage dichotomy. No floats anywhere. The model is written out at the top of
`ties.py`; the mechanized part is `proofs/BladeDichotomyTies.v`.

Run everything with `C:\Python314\python.exe -I <script> ...` from this
directory (pure Python 3, no third-party packages). Never run two of the heavy
ones at once on this machine.

| script | what it does | what it prints |
|---|---|---|
| `ties.py` | library: S_r, tuples, sorters, rules, cells/orbits, exact Q and GF(p) linear algebra, the restriction lower bound `lower_bound()`, the flag (triangular-certificate) lower bound `flag_bound()`, the exhaustive GF(p) scheme search `search_p()` / `search_p_iter()` and the integer lift `lift_to_Q()` | — |
| `census_322.py` (50 s) | the 4096-rule census at (3,2,2): exact width-2 decision through the forced subspace K, the combinatorial criterion (R_001 and R_011 disjoint in both dimensions), the explicit scheme (1, chi) checked under all 1296 constant-tuple sorter choices | `width-2 admitting rules: 4 of 4096`, the four rules, LB histogram, `scheme failures: 0` |
| `check_certs.py` (1 s) | Python twin of the computed pins in the .v file: the pick3/pick4 certificate patterns, the coset representatives, the (3,2,3) scheme's orbit-name identity for all 1440 (x, sigma, tau) | `verified: 6`, `mismatches 0` |
| `map_322.py [limit]` (30 min) | first pass over all 4096 rules: LB, FB, exhaustive GF(3) search at m = FB, integer lift; incremental into `map_322.tsv` | per-rule rows + histogram |
| `orbits_322.py [limit] [N]` (10 min) | the 48-element symmetry group (slot relabeling x value flips x lat/lon swap) cuts the 4096 rules to 126 orbits; second pass on representatives: N GF(3) witnesses tried for a lift, exhaustive GF(5) at m = FB, exact upper witness at m = FB + 1; writes `map_322_orbits.tsv` | the final histogram (below) |
| `table.py [N]` | lex rule + N random rules at (2,2,2), (2,3,3), (3,2,2), (3,2,3), (3,3,3), (4,2,2); the constant-tuple dependence probe | per-case lines |
| `minwidth.py r L M lex\|all\|sample N [p=..] [limit=..]` | LB and GF(p) width (+ lift) for one rule / every rule / a sample | per-rule or histogram |
| `probe_sample.py r L M N [p]` | LB vs FB vs GF(p) width on N random rules; prints the certificate chain whenever FB > LB | per-rule lines |
| `count_disjoint.py r L` | one-dimension tie-break choices with pairwise-disjoint sorter sets, and whether they are sign-graded | counts |
| `resolve_322.py`, `resolve2_322.py` | earlier (slower, non-orbit) second passes; superseded by `orbits_322.py` | — |

## Results (2026-10-16)

(3,2,2), all 4096 rules with constant tuples' sorters = identity (minimal
width over Q; LB = restriction bound, FB = flag bound, both theorems):

| minimal width | rules | how |
|---|---|---|
| 2 | 4 | FB = 2, explicit scheme; the parity-graded rules (`census_322.py`, mechanized) |
| 3 | 3036 | FB = 3 and an exact integer witness |
| 4 | 720 | FB = 4 and an exact integer witness (384 + 96 of them have LB = 3 < FB) |
| 4 or 5 | 288 | LB = FB = 4; NO width-4 scheme over GF(3) (all 288) nor over GF(5) (192 of them, exhaustive); exact integer witness at width 5. Over Q open; the evidence says 5, i.e. the flag bound is not tight there |
| 5 | 48 | LB = FB = 5 and an exact integer witness |

Overlap pattern (|R_001 n R_011| in lat, lon) decides a lot but not all:
(0,0) is width 2; any (k,0)/(0,k) is width 3; (2,3)/(3,2) splits 72 at width 3
and 48 at width 5; (2,2) splits 372 / 240 / 288 across widths 3 / 4 / 4-or-5.

Constant-tuple sorters: irrelevant for the width-2 question (the theorem
quantifies over them), but above the counting bound they can change FB
(`table.py` constants probe; `probe_sample.py` prints certificates through
cells like (001, 000)).

Other cases (`table.py 12`, `case422.py 4 3e5`, `count_disjoint.py`):

| (r, L, M) | counting bound max d_c | minimal width |
|---|---|---|
| (2, 2, 2), (2, 3, 3) | 2 | 2 for every rule (lex + 12 random each, exact) — the Cauchy split; rules only break the constant tuples' ties and never matter |
| (3, 2, 2) | 2 | 2 / 3 / 4 / (4 or 5) / 5 as above |
| (3, 2, 3) | 3 | 3 iff the LAT rule is parity-graded (mechanized, any lon rule); otherwise 5: LB = FB = 5 with an exact witness for the lex rule and all 12 random rules. Not 6 |
| (3, 3, 3) | 6 | 6 for every rule (free cell; BladeDichotomy) — LB = FB = 6 confirmed on the lex rule |
| (4, 2, 2) | 3 = floor(4/2)+1 | 3 for every rule whose non-constant sorter sets are pairwise disjoint (>= 2000 such one-dimension choices exist at (4,2); none is sign-graded — grading by sign is a (3,2) accident of 3 + 3 = 6); the explicit scheme (1, orbit indicators per block) is exact. Lex rule: LB 5 with an exact witness. Random rules: LB 4-5 |

`count_disjoint.py 3 2` prints the 2 parity-graded choices (A_3 and its coset).
