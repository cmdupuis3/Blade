# Round 2 microkernel record (2026-10-19/20)

The plan is `../round2-microkernel-plan.md`. This directory holds the evidence it cites:

| file | what |
|---|---|
| `research-preamble.md` | the brief every research agent followed (constraints, required sections) |
| `brief-A-ordered-scatter.md` | item A: G35 row-slot repro folds + the ordered orbit scatter (rank 2 / 3) |
| `brief-B-epilogues.md` | item B: P1/P2 runtime, saxpy rank-1 epilogue, chain interleave, pairkernel compact rows, maskagg compaction |
| `brief-C-sibling-folds.md` | item C: sibling-fold traversal sharing (Optimize.fs rewrite into the reduction join) |
| `brief-D-tile-runtime.md` | item D: antisym row groups, simplex edges, the dropped persistent team / weighted tasks, pairkernel mt8 diagnosis |
| `brief-E-parallel-permission.md` | item E: G7 thread-level join, G5 let-rec step threading (design) |
| `codex-review-DE.md`, `codex-review-AC.md`, `codex-review-B.md` | the independent Codex attack reviews of the briefs (verdict tables + numbered objections) |
| `drop-experiments.md` | MEASURED on the OLD binary 4136f0b5: hand-joined sibling folds (C passes), mt8 syrk_g sweep (null) |
| `baseline-summary.md` | the round-2 baseline gate on 4136f0b5 (6800/0/1) and the harness A/B recipe |
| `builder-preamble.md` | the rules every implementation agent followed |

Paths inside these files that start with `C:\Users\cdupu\AppData\Local\Temp\claude\...\scratchpad\round2\` refer to the orchestrating session's scratch area; the durable copies are the files in this directory (same names minus the `brief-` / directory prefixes).
