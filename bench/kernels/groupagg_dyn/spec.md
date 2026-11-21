# groupagg_dyn — aggregate-only GROUP BY over dynamic keys (round 3, W3-1)

Group B. The definition, chain order, tiers, regimes and risks are
`bench/kernels/groupagg/spec.md`'s; this file states only what differs.

## Keys and group order

    key(k) = 7919 · floor(ngroups · u(k)) + 13,   u(k) = gen(k, 10000019, 0, 1)

Sparse Int64 keys with no declared index type: the DYNAMIC regime. Groups are numbered by
**first occurrence** (the first row with a new key opens the next group), ~1009 of them
(every value of floor(ngroups · u) occurs at these sizes; the reference reports the count).
s, q, c are indexed in that order; `checksum` and `input_checksum` as in groupagg (with
Σ key over these key values). Keys < 2^24, so the expert's multiplicative hash cannot
overflow.

## Tiers

- **natural**: the distinct keys discovered in first-occurrence order with `ANY` over the keys
  found so far, then one masked intrinsic per group and output: O(n · G) twice.
- **expert**: ONE pass over the rows: an open-addressing hash (power-of-two capacity,
  multiplicative hashing, linear probing, load ≤ 1/2, doubled on growth) maps each key to
  its first-occurrence group number; the direct-indexed s, q, c grow with it.

## Expected emission

- **repro**: the aggregate-only one pass with ONE find-or-insert per row in the C core's key
  → group map (`blade_member.h`, both lanes), instead of the CSR's discovery, count and
  scatter passes (three hash operations per row in the C++ lane).
- **licensed**: declined as in groupagg (licensed float folds): the CSR runs. In the LLVM
  lane the ordinary dynamic CSR discovers its groups through the same C-core map (the separate
  LLVM dynamic-discovery change of round 3 W3-1 retired its O(n Â· G) linear scan; gap G71).

## Sizes

As groupagg: smoke n = 30011, full n = 1,000,003, ngroups = 1009 (the keys' source range).
