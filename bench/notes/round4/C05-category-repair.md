# C05 category fixture repair

On source `02672fd0f4ac10c030ab1091014618d826158873`, fresh build/optimize757/0
and twelve native arms passed. The first sql-reduce category gate returned
52 pass / 1 fail / 0 skip. Only corpus051 was classified as a value test despite
its existing `// ABORT: late_sparse_parent` pin, because its TEST name omitted
the load-bearing `(aborts)` suffix. Corpus.fs39-52 strips the header before
running the source; Runner.fs368 classifies probes from the test-name suffix.

One fixture-only change appends ` (aborts)` to 051's TEST line. All bytes after
the first newline, including program text and every existing failure pin, are
identical before/after (exact byte-array comparison). The original newline is
preserved; file length increases by nine bytes. No NOPINS, harness, production,
expected value or failure-order change is made. Original manifests remain
historical checkpoint records.

- Original whole-file SHA256: `B5F27165D12705C08E779F8F44878E930F5F1B0EBD89C07BA54E9B883069AE3F`.
- Current whole-file SHA256: `567F9411B465C6AC5AF532CBAA8D92748D838A078326310053D4DD2B0221617B`.
- Unchanged body SHA256 (after first newline, both versions): `2FC061E3131E98ACC2BBD00112510AD72C71B0611DAD6EEA2B06EA2E7C7D0C57`.
- Preserved evidence: `bench/.build/C05-final-categories/sql-reduce/stdout.log`, SHA256 `37832BAEE65811964747CECB209294BBB3DF19B19332DFD0CC7ACC0C91C1F00D`; adjacent stderr/exit/provenance remain intact.

Root can reuse the source-matching02672 DLL (SHA256
`C7D8B405F8CAFE1561653D76C08F5B5A20EA509D19AA385A2CC91560616E0C11`)
without rebuilding because corpus files load from CWD at process start. Root
reviews/commits this checkpoint before dispatching Luna's category retry; preserve
prior logs and use a fresh output location. The coder performed byte editing,
comparison, source inspection and static hash/diff checks only. No build, test,
native execution, lowering, emission or git mutation was performed. `.r4tmp`
and prior artifacts are untouched.
