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
  against the program's printed output. Scalars, 1-D arrays, complex pairs
  `(re, im)`, and quoted strings are checked.
- **2-D `[[..]]` expectations parse but are NOT checked** — Expect.fs matches
  `ExpectedArray2D` and returns "no failure" unconditionally, so a nested EXPECT
  silently asserts nothing. Multi-dimensional results print FLAT anyway, so pin
  them as a flat 1-D list, which is really checked.
- Files run in ordinal filename order — keep the `NNN_` prefix.

## Categories

Loaded by tests/Corpus.fs; named in the Test_*.fs modules (e.g. Test_Basic.fs
maps `basicTests` to `basic/`). `multifile/` holds one subdirectory per test,
one `.blade` per module file (with `// MODULE:`), compiled together.

`mutability-errors/`, `struct-aborts/`, and `unit-errors/` are preserved
assets whose runners were removed as dead code; they are not currently run.

To add a test: create `<category>/NNN_<slug>.blade` with the next free number.
No recompilation is needed — the suite reads these files at run time. When run
from the repo root it reads `./tests/corpus` directly; elsewhere it falls back
to the copy deployed next to the binary at build time.
