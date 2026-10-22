# D01 orchestrator review

2026-10-22. Reviewed the five-file implementation in `d-streaming`, base `361504b6`.
Disposition: accepted after sole-runner build and standalone Zarr validation:
389 passed, zero failed, one NetCDF differential skipped (library unavailable).
Source checkpoint `9b36f1d1`, integrated as `f0ee9797`. No memory/performance claim.

The missing path is concrete: explicit joined scalar folds over a named deferred
producer reach a fiber renderer that requires an array result. D01 connects the existing
shared scalar producer and separate seeded fold wrappers to one rank-one Zarr block
loop. It does not erase a materialized binding. Each fold remains one ascending chain.

Review corrections incorporated:

- Establish source dominance and intervening barriers by binding-list position, never
  numeric binding ID.
- Require whole-program demand evidence through an explicit private assembler parameter.
  Direct public module emitters have no such evidence and decline. No ambient flag.
- Reuse shared demand roots and inspect declared types, hidden operands and planner
  ownership. Remove the temporary local type-root supplement after C01 integrates.
- Account for both converted input and the full padded disk chunk, plus bounded scalar
  state. The payload model excludes filesystem/iostream bookkeeping and is not RSS.
- Accumulate missing-value diagnostics over the complete original read, preserving a
  later corrupt-chunk error before missing-value reporting and scalar publication.

The arithmetic whitelist and serial/backend/shape restrictions are deliberately narrow.
The new reader reuses existing chunk lookup, conversion, fill and short-read handling.
Generated sharing uses the existing per-cell shared variable, with independent fold
wrappers; Reynolds results carry no ignored statement prelude. Default deferred/streamed
bindings are unprinted by the existing print gate; explicit demands are refused.

The test coverage includes actual route markers, independent known values, materialized
read twins, zero and odd tails, v2/v3 stores, refusal cases and paired missing/short-read
failures. The provenance test freeze
adds direct-module calls after normal assembly and split-timing assembly assertions,
and restores the timing flag in `finally`. The first build exposed multiline-string
indentation in the fixture helper. Its construction changed to an indented line
list; emitter files stayed unchanged. The tested ZarrTests SHA256 is
`A85ED13D07EAB93FF4AFC1B50A7DFA2637A500004F42D63C63D57C0C57FA33C6`.
Interpreter and LLVM provider refusals passed as explicit refusal assertions;
they are not backend execution passes. See [runner results](D01-tests.md).

During integration, the temporary local declared-type demand supplement was
removed: C01's shared `compactionRoots` now supplies all those roots, including
index extents. The combined code still needs targeted optimize/Zarr validation.
