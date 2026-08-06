# Blade test corpus

One `.blade` file per test, one directory per category. These files are the
compiler's regression suite **and** the planned differential oracle for the
rewrite (same corpus through both compilers, compare emitted values), so treat
them as assets: edit deliberately, never regenerate mechanically.

## File format

```
// TEST: <exact test name>        <- required first line
// MODULE: <module file name>     <- multi-file tests only (see below)
<optional design-rationale comments>
<Blade source, with // EXPECT: comments>
```

- The `// TEST:` name is what the harness reports and — for names ending in
  `(rejects)` — what marks an intentional reject-probe: the test PASSES when
  the compiler refuses it. Renaming a test changes its semantics; see the
  guard-combinators/007 header for a cautionary tale about duplicate names.
- `// EXPECT: <var> = <value>` lines are parsed by tests/Expect.fs and checked
  against the program's printed output. Scalars, 1-D arrays, 2-D `[[..]]`
  arrays, complex pairs `(re, im)`, and quoted strings are all checked.
- **2-D `[[..]]` expectations** are compared against nested actual output
  (`[[0, 1], [20, 21]]`) row count first, then per-row length, then elements.
  If the printer instead emits a flat run — `genPrintArrayFlat` /
  `genPrintArraySymAware` do this for rank-2+ arrays, since the nested loops
  they walk produce one comma-separated run with no row boundaries — the pin
  is compared against its own row-major flattening instead; every element and
  the total count are still checked, only the row split is unobservable.
- Files run in ordinal filename order — keep the `NNN_` prefix.

## Pin forms

Every assertion a test makes is a `//` comment line, parsed by tests/Expect.fs
and enforced by the one verdict function in tests/Runner.fs. A pin that does
not parse FAILS the test — a dropped assertion is worse than no assertion.

| Pin | Asserts |
| --- | --- |
| `// EXPECT: <var> = <value>` | the program printed this value |
| `// ABORT: <substring>` | an `(aborts)` probe's output contains this |
| `// REJECT-AT: lower \| codegen` | the stage a `(rejects)` probe must be refused at |
| `// ERROR: BLxxxx [@ l:c[-l:c]]` | a `(rejects)` probe is refused with this diagnostic |
| `// ERROR-CONTAINS: <substring>` | the refusal message contains this |
| `// WARN: BLxxxx` | the compiler emits this warning CODE |
| `// WARN-CODEGEN: <substring>` | codegen emits a warning containing this |

`// WARN:` and `// WARN-CODEGEN:` are the warning-side pins, and unlike the
others they are enforced in **both** directions:

- A warning that fires with no pin FAILS the test
  (`unpinned warning[BL4003]: ...`). Warnings used to be printed straight to
  the console from inside the compiler — un-attributed, interleaved with
  parallel progress lines, ~754 per run — which made them indistinguishable
  from noise. A test that means to warn must now say so.
- A pin that never fires FAILS the test
  (`expected // WARN: BL4010 but no such warning fired`), so a pin cannot
  outlive the rule that motivated it: delete the check and its pins go loud
  rather than silently becoming comments.

A **pinned** warning is not printed at all; an unpinned one appears only in the
failing test's detail line. A clean run therefore prints no warning text.

Notes:

- `// WARN:` takes a bare code — no `@ line:col` span. Text after the code is
  ignored as prose, so `// WARN: BL4010  (storage suggestion)` is fine.
- Matching is count-insensitive: one `// WARN: BL4003` licenses every BL4003
  the file emits, because multiplicity tracks how many sites trip the rule,
  which is not what the pin is asserting.
- `(rejects)` probes are held to the same rule. The checker's warning channels
  survive its error path, so a program refused at typecheck has still earned
  whatever it emitted before the refusal.
- Multi-file tests take the **union** of the pins across their member sources:
  a cross-module program is typechecked as one program, so its warnings cannot
  be attributed to a single file.

## Categories

Loaded by tests/Corpus.fs; named in the Test_*.fs modules (e.g. Test_Basic.fs
maps `basicTests` to `basic/`). `multifile/` holds one subdirectory per test,
one `.blade` per module file (with `// MODULE:`), compiled together.

`mutability-errors/` and `unit-errors/` are preserved assets for a future
expected-error runner; they are not currently run. `struct-aborts/` IS run
(via `structAbortTests` in tests/RunAll.fs's `allTests`, also reachable as
`blade test struct-aborts`) — its tests expect compile success followed by a
nonzero runtime exit, pinned by `// ABORT:`.

To add a test: create `<category>/NNN_<slug>.blade` with the next free number.
No recompilation is needed — the suite reads these files at run time. When run
from the repo root it reads `./tests/corpus` directly; elsewhere it falls back
to the copy deployed next to the binary at build time.
