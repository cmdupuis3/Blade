# Wave 2 workstream B migration manifest (stored-alias identity, sampler, minting sites)

Companion to [plan-identity-transpose-compute.md](plan-identity-transpose-compute.md)
§2.1 (second step), §2.5, §2.7. Method: every `.blade` under `tests/corpus/**` and
`examples/**` with a top-level bare alias line (`let X = Y`; 129 files, the new tests
included) was run through `blade plan` and `blade emit` with a scratch build of
2908d95d (the base) and with this change; files whose plan or emission differed were
also run (`blade run`) on both. Comparison drops the new `[alias-identity v1]` records,
timings and minted ids (`__v123`, `#123`). A file with no top-level bare alias cannot be
changed by the identity rule (§2.1) or by the sampler rule (§2.5, which only joins
aliases the identity rule admits); the §2.7 changes alter identities no emission prints.

## Changed files (outside the new tests)

| File | Changed | Classification |
|---|---|---|
| symmetry/146, 147, 148 | `[symmetric-storage v1]` gains the evidence `identity-transparent aliases: g2 = g (...)` | record text only; pins and output unchanged |
| symmetry/149 | `[alias-identity v1] B: applied` (new record) | `f(A, B)` stays dense (helper parameters, plan §2.2); output unchanged; its comment "B is a stored copy" is now stale (B is identity-transparent) and is left for workstream A, which owns the §2.2 before-case |
| sql-unique-contains/026 | `[alias-identity v1] S1, S2: applied` (new records) | output unchanged |
| multifile/010, 019, 027 (`*_Main.blade` run alone) | the missing-module error text names each binary's own stdlib path | noise: the two binaries live in different directories |

No existing corpus or example output, expected type or pin changed: today no corpus or
example file feeds a never-written stored alias into a comm, gram or comm-witness
application (plan §8's observation, re-confirmed).

## Records that changed shape (no outcome change)

- `[transpose-normal-form v1]` over an alias of a CALL result: the read-only alias plan
  now admits a call root (or a `|> compute` that is no fresh pool) when its whole reach is
  never written, so a decline that used to read "`E`'s value is not proved fresh (a
  call's result ...)" now names the written member when there is one (symmetry/155:
  "`X` is assigned in the program").

## New tests (each a before/after pin)

| Test | Before (2908d95d) | After |
|---|---|---|
| symmetry/154 | BL3001 (the `SymIdx<2, I>` ascriptions; products dense) | packed, values pinned |
| symmetry/155 | dense | dense (fail-closed edges, including a call root whose callee hands back a written argument, plain and under `|> compute`) |
| symmetry/156 | BL3001 (gram ascription); chain dense | packed chain, three-way group, same-array gram |
| symmetry/157 | BL4013 (captures sampled per binder) | accepted, packed |
| symmetry/158 | BL4013 | BL4013 (a written alias is sampled independently) |
| symmetry/159 | BL4013 | BL4013 (formal arguments independent) |
| symmetry/160 | BL4013 | BL4013 (a write inside a lambda default is seen) |
| loops/598 | same values | same values (non-variable co-iteration operands now distinct identities; `blade test optimize` pins the identities) |

The full per-file table (129 rows: file, applied / declined alias records, plan diff,
emission diff, output diff) was produced by the sweep script and is not committed.
