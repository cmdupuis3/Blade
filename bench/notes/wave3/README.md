# Wave 3 microkernel record (2026-10-20)

The task (from the round-2 orchestrator): run wave 3 on a worktree cut from
`worktree-bench-fortran` (ccde7e4e) -- E-G5 recur step threading, the stencil mt8
attribution, B2l, the antisym `anti_g` re-measure and D1 -- with the round-2 process
(brief -> Codex attack review -> build -> review -> merge -> gates -> harness A/B with knob-off
mirrors). RESULTS section 16 is the summary; this directory holds the evidence it cites.

| file | what |
|---|---|
| `brief-E-G5-wave3.md` | the build brief: the two measurements that fix the form (team entry / barrier ~40 us on this box) and the barrier-free persistent team |
| `codex-review-E-G5.md` | the independent Codex attack review of the brief (verdict table, 19 objections; all folded in) |
| `codex-diff-E-G5.md` | the Codex adversarial review of the implementation diff (two findings, both fixed before the gates: the repro outer-kernel boundary, the lane count of the in-team partial) |
| `recur-mt8-attribution.md` | MEASURED: phase timers in the emitted C++, the expert's region alone, raw write bandwidth -- the region is at parity; the gap is the teardown inside the timed region |
| `stencil-mt8-attribution.md` | MEASURED: the same instrument on stencil (teardown 1.0 ms, DRAM-bound read phases), the read-back licence, the D1 instrument (dropped), B2l (not built) |
| `results-16-draft.md` | the section-16 draft as the wave ran |
| `w3-scripts/` | `gates.ps1` (the five suites from `C:\bt_w3`, tests/docs junctions), `mirrors.ps1`, `smoke.ps1`, `driver.ps1`, `analyze.py` (the A/B: w2 = OLD binary + original sources; w3 = this tip; w3a = `BLADE_REC_STEP_OMP=0`; w3g = `anti_g=0`) |

Scratch (the session's area, not durable): `scratchpad\w3\probe\` holds the probe sources
(`forkcost.cpp`, `bw.cpp`, the phase-timed emissions, the region-only expert copies,
`d1cost.cpp`) -- every number in the attribution notes names its probe.
