# hashjoin — an inner equijoin of two keyed tables, the relation stored (round 3, W3-2)

Group B. Directory `bench/kernels/hashjoin/`. Gap register row G72. Research:
`bench/notes/round3/research/B-relational.md` section 2 "T2"; attack review
`bench/notes/round3/reviews/codex-review-B.md` findings 5, 6, 10, 11 and 13.

## Object and operation

Two keyed tables: LEFT (lk, la) over i in [0, N) and RIGHT (rk, ra) over j in [0, M), keys
integer. The inner equijoin with payload `la(i) * ra(j)`:

    matched = ( la(i) * ra(j) : lk(i) == rk(j) )    in LEX order: i ascending, then j ascending

**The relation is a BAG.** Every left row pairs with EVERY right row of equal key; duplicates
on both sides are kept (no dedup), in left-major / right-minor order. The smallest duplicate
case (corpus `sql-joins/010`): left keys [1, 1] with amounts [1, 10], right keys [1, 1] with
amounts [2, 3] give [2, 3, 20, 30]. A key with no partner contributes nothing; a negative key
is an ordinary key (equality has no negative-key drop).

**Named output: `matched`** (J cells, J = the number of matching pairs, unknown beforehand),
stored in every arm -- AUDITED in the optimized code of blade-cpp and both gfortran tiers
(and the input stores of every twin), NOT for ifx (unbuilt here): see "Store audit" below.
Scalar outputs: `match_count = J`, `total = Σ matched`, and

    fp = left fold over matched, from 0, of  a -> (3 a + b) mod 1000000007

the ORDER fingerprint: non-commutative, so a permuted relation (a right-major probe, a
reversed bucket) changes it. Every cell is in [0, 2^20) and a < 1000000007, so 3 a + b < 2^32:
exact, with no Int64 wrap in any language (review B/13 — the unbounded `3 a + b` overflows
Int64 after ~40 cells, and Fortran signed overflow is not defined). Blade spells the modulus
as three conditional subtractions (the totality census of the fold kernel admits no integer
`%`); the value is mod()'s.

## Inputs

Integers from the suite generator (README §3.2): `floor(gen(k, seed, 0, w))` is EXACT (w has
<= 22 significant bits, so (hi − lo)·u is exact; floor of an exact value).

| array | shape | k | seed | value |
|---|---|---|---|---|
| lk | (N) | i | 10000019 | floor(gen · K): uniform over [0, K) |
| la | (N) | i | 20000038 | floor(gen · 1024): [0, 1024) |
| rk | (M) | j | 30000057 | floor(gen · K): uniform over [0, K) |
| ra | (M) | j | 40000076 | floor(gen · 1024): [0, 1024) |

**The key distribution is BOUNDED by construction** (review B/6): keys uniform over K values, so
E[J] = N·M / K. A general all-equal join has J = N·M and could not satisfy the 64 MB rule
while storing J; this kernel's J is stated per size below.

## Checksum

    checksum       = total + J + fp        (exact integers, summed as Float64)
    input_checksum = Σ lk + Σ la + Σ rk + Σ ra

`fp` verifies the order at every size; the dump variant (`validation/dump/hashjoin_dump.blade`)
prints `matched` cell by cell at smoke size for `validate_B.py`.

## Sizes

| variant | N | M | K | pairs P = N·M | J (these seeds) | inputs | matched |
|---|---|---|---|---|---|---|---|
| smoke | 211 | 101 | 41 | 21,311 | 522 | 5 KB | 4 KB |
| full (= the research PILOT) | 2003 | 1009 | 401 | 2,021,027 | 5000 (E[J] ≈ 5040) | 48 KB | 40 KB |

**Pilot only this round.** The research full size (N 100,003, M 20,011, K 10,007, J ≈ 2.0e5)
is NOT benched: the as-written OLD Blade form needs ~16 GB of payload pool there (infeasible,
research §3), and the review asked for the full-size run only after a footprint / store audit.

## Tiers (README §1)

- **natural**: the textbook nested loops — a counting double loop sizes `matched`, a second
  double loop appends every matching payload in lex order (O(N·M) comparisons, no N×M matrix
  formed); then the total and the fingerprint over the stored relation.
- **expert**: the counting-sort CSR over the key domain [0, K): count, prefix, FORWARD fill
  (stable buckets, every duplicate kept); a counting pass over the left keys sizes J; the
  probe i ascending, every bucket row ascending, storing `matched(q)`, with the total and the
  fingerprint FUSED into the probe (the stores still happen). It trusts the generated key
  domain (a direct-index table of K, no range check); Blade's build measures the key span.
- **Blade** (`hashjoin.blade`): the INLINE spelling — `compound(method_for(la, ra) <@> pk,
  mask(method_for(lk, rk) <@> lambda(l, r) -> l == r, lambda(e) -> e))`. The two outer
  products are compiler temporaries; the C++ lane's hash join (`Blade.Optimize.planEquiJoin`,
  rule `hash-join v1`) builds a stable CSR over rk (here the counting sort: span ≤ 401 <
  max(4 M, 4096)), counts, and probes; `matched` is stored (its compact buffer, with a
  cardinality-only rank-2 index); the folds stay at their own sites and read it.
  With `BLADE_HASH_JOIN=0` (the OLD form) it is the six passes over all P pairs.
  **Why the inline spelling.** The NAMED spelling (`equal_keys`, `keep`, `products` as
  let-bound arrays) keeps every named array STORED (bench/README.md: named arrays are
  outputs of the arm), so no N×M representation can disappear there; the hash join declines
  it by name (corpus `sql-joins/013`). The inline spelling names only the relation.
- **No mt8 arm.** A legal OUTPUT-OWNED threaded form exists (per-left-row output offsets from
  the counting pass, each thread storing its own rows), but Blade's v1 is serial; no arm
  builds it.
- LLVM lane: refuses the rank-2 compound (`hash-join` declined -- llvm lane, v1.1) and the
  program falls back to the C++ lane: blade-llvm runs the C++ build (kernel.json
  `expected_llvm: fallback`).

## Cost accounting at the pilot (OLD = BLADE_HASH_JOIN=0, NEW = the hash join)

Counted separately (review B/6, and the W3-2 diff review's finding 4): OPERATIONS, LOGICAL
TRAFFIC (the SOURCE-LOGICAL convention, applied to every arm alike: every array-element read or write the
source statement performs on each execution, a repeated read of one element within one statement counted
once, scalar locals and struct fields not counted, no register reuse assumed across statements, cache
residency ignored), ALLOCATED / LIVE bytes,
and the MEASURED peak working set -- each for the CONSTRUCTION of the relation alone and for
the COMPLETE kernel (construction + the consumer folds `total` and `fp`; `match_count` is the
stored cardinality, O(1)). ONE operation convention for every arm: one executed element step
of one logical computation (a key read for the span, a count increment, a prefix step, a fill
store, a lookup, a payload evaluation + store, a fold step) -- a fold FUSED into a probe still
counts its J steps. Pilot: N 2003, M 1009, K 401 (the right keys' measured span, nb = 401
buckets), P = N M = 2,021,027, J = 5000; Blade's CSR cells are 8 B, the expert's 4 B.

| | OLD (six passes) | NEW (hash join) | expert |
|---|---|---|---|
| operations, construction | 6 P (`==`, identity mask, bit copy, enumeration test, payload, scatter) + 2 J (hash insert, tuple push) ≈ 12.13e6 | span M + count M + prefix nb + fill M = 3,428; counting pass N = 2,003; probe N lookups + J = 7,003; **12,434** | count M + prefix K + cursor copy K + fill M + sizing N + probe N + J = **11,826** |
| operations, complete kernel | + `total` J + `fp` J = **12.14e6** | + `total` J + `fp` J = **22,434** | + J + J (fused into the probe) = **21,826** |
| logical traffic, construction | per pair: the `==` pass lk 8 R + rk 8 R + Bool 1 W; the identity mask 1 R + 1 W; the bit-vector copy 1 R + 1/8 W; the enumeration 1/8 R; the payload pass la 8 R + ra 8 R + 8 W; the scatter 8 R + 1/8 R + 8 W (unconditional) = 60.375 B x P = **122.0 MB** (the J hash-node inserts and tuple pushes are allocator / hash-table traffic, not counted) | build (the C core): the span pass rk (M + 1) x 8 = 8,080 R; the offsets memset (nb + 2) x 8 = 3,224 W; the count rk 8 R + off 8 R + off 8 W per row = 24,216; the prefix b = 2..nb, 400 x (2 R + 1 W) x 8 = 9,600; the fill rk 8 R + off 8 R + off 8 W + perm 8 W per row = 32,288: 77,408. counting pass: lk 8 R + off[b + 1] 8 R + off[b] 8 R per row = 48,072. probe: per row lk 8 R + 2 offset reads 16 R = 48,072; per match perm 8 R + la[i] 8 R + ra 8 R + matched 8 W = 160,000: 208,072. **333,552 B** | `off = 0` 1,608 W; count rk 8 R + off 4 R + 4 W per row = 16,144; prefix 401 x (2 R + 1 W) x 4 = 4,812; `cur = off` 401 x (4 R + 4 W) = 3,208; fill rk 8 R + cur 4 R + perm 4 W + cur 4 R + 4 W per row = 24,216; sizing lk 8 R + 2 x 4 R per row = 32,048; probe per row lk 8 + la 8 (`li = la(i)`, hoisted in the source) + 2 x 4 offset R = 48,072, per match perm 4 R + ra 8 R + matched 8 W = 100,000: **230,108 B** |
| logical traffic, complete kernel | + 80,000 B (two fold passes over `matched`) = **122.1 MB** | + `total` 40,000 R + `fp` 40,000 R = **413,552 B** | + 0 (the folds are scalar statements on the probe's `v`) = **230,108 B** |
| allocated / live beyond the inputs | payload pool 16.17 MB + Bool pool 2.02 + mask pool 2.02 + bit vector 0.25 + tuple table J × 16 + reverse hash ~J × 40 + matched 40 KB ≈ **20.7 MB** | during construction: the CSR block (nb + 2 + M) × 8 = 11,296 B + matched (J + 1) × 8 = 40,008 B + the index object ≈ **51.3 KB**; after it (the CSR freed): 40 KB + the index | off (K + 1) + cur K + perm M (int32) + matched J × 8 = **47,248 B** (retained to the end) |
| measured peak WS (one core, 11 rounds; process, not live data) | 25.48 MiB | 5.48 MiB (inputonly twin 5.39) | 5.28 MiB (twin 5.28) |

## Store audit (README §1.5; W3-2 diff review finding 2)

The optimized code of the measured pilot builds, inspected per arm. Blade: `blade compile`
(C++ lane, `--print checksum,input_checksum`), the kept translation unit re-assembled with the
same command (`g++ -std=c++17 -O3 -march=native -ffp-contract=fast -fno-math-errno -fwrapv
-fopenmp -S`; g++ (Rev4, MSYS2) 16.2.0). gfortran: GNU Fortran (Rev4, MSYS2) 16.2.0, the
directional's own flags `-cpp -O3 -march=native` (the timed executables, `objdump -d`, plus
`-S` listings of the same sources and flags), and the harness's licensed set (`-O3
-march=native -ffp-contract=fast -fno-external-blas -fno-external-blas64 -cpp -DBENCH_LICENSED
-fopenmp-simd`, `-S`): the same loops. No dump mode in any audited build.

| arm | input stores (lk, la, rk, ra) | `matched` stores | notes |
|---|---|---|---|
| blade-cpp NEW | survive: four fill loops, each `vcvttsd2siq` then `movq %rax, (%rcx)` | survive: the probe loop loads perm, gathers ra, `imulq` by la(i), `movq %r10, (%r8,%rax,8)` | the folds read the stored buffer after the probe |
| blade-cpp inputonly | survive (the same four fills) | -- (no join) | |
| gfortran expert | survive: the fills are fused two arrays per loop, two `movq` stores each | survive: in the probe loop `.L38`, `movq %r15, (%r11,%r8,8)` beside the fused total add and the mod-1000000007 chain (in the timed executable: `mov %r15,(%r11,%r8,8)` next to `imul $0x3b9aca07`) | no reader of `matched` follows in the timed build (the dump reader is BENCH_DUMP-only); gfortran keeps the stores anyway |
| gfortran natural | survive (fused fills, two stores per loop) | survive: the append loop stores `movq %r8, 0(%rbp,%r11,8)`; the fold loop reads them | |
| gfortran twins (natural, expert) | survive (fused fills) | -- (no join) | |
| ifx (both tiers, twins) | NOT AUDITED (not built in this session) | NOT AUDITED | provisional until the runner's audit |

No audited arm lost a store, so none is "checksum-only"; the expert / NEW and natural / NEW
ratios below compare store-audited arms. The OLD arm (`BLADE_HASH_JOIN=0`, the six passes) was
NOT audited, so **OLD / NEW = 108x is EXCLUDED from the audited ratios** (it is a compiler
A/B, not a bench arm). Timings are bc3ddac9's emission; the fix rounds' overflow guards are
not re-timed.

## Directional timing (W3-2, one core, 11 interleaved rounds, medians; NOT harness-measured)

Measured 2026-10-21 on the shared box (load 7% at the start), every arm pinned to one CPU,
the program's own timed region:

| arm | median | incremental (− own twin) |
|---|---|---|
| Blade NEW (hash join) | 86.7 µs | 54.9 µs |
| Blade OLD (six passes, `BLADE_HASH_JOIN=0`) | 9400.9 µs | 9369 µs |
| Blade inputonly | 31.8 µs | — |
| gfortran expert (`-O3 -march=native`) | 72.8 µs | 41.1 µs |
| gfortran expert inputonly | 31.7 µs | — |
| gfortran natural | 1296.4 µs | ~1265 µs |

OLD / NEW = 108x; natural / NEW = 15x. **The DROP criterion is the TOTAL-region ratio**
expert / NEW (the preamble's expert / Blade convention, the research's 0.8 line): 72.8 / 86.7
= **0.84 clears it**. The input-net (incremental) ratio 41.1 / 54.9 = 0.75 does NOT clear a
0.8 line; it is reported, not used as the criterion. The clearance is DIRECTIONAL and
PROVISIONAL: one shared box, one core, 11 rounds, gfortran only (no ifx arm built or
store-audited), not harness-measured; the NEW figures are the bc3ddac9 emission (the fix
round's overflow guards add one compare per left row to the counting pass, not re-timed).
The remaining gap is the consumer folds: Blade keeps `total` and `fp` at their own sites (a
separate pass and a separate serial chain, ~11 µs here; 80,000 B of the complete kernel's
traffic), the expert fuses them into the probe; a fold-into-probe is v1.1 (it needs B4a's
snapshot proof). An earlier NEW emission at 0.76 lost ~14 µs to first touch of allocations
the result never needed (a cursor array, a per-row bucket array) and ~4 µs to the first
`getenv`; v1 builds ONE CSR block and emits the `[join]` report only under the compile-time
instrumentation gate BLADE_JOIN_REPORT (unset in every measured build).
