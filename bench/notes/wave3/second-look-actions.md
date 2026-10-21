# The Codex second look (`codex-second-look.md`) -> what this session did with it

Requested by the user mid-wave: a second look at the options that did not show clear success.
Verdicts and the actions taken (MEASURED results are appended below as they come in).

| option | Codex verdict | action |
|---|---|---|
| G52 teardown | revisit the retention policy; the TLB-shootdown explanation is a hypothesis, "region parity" overstates (expert 5.38 vs Blade 5.9-6.3 pre-touched = 10-17%) | wording corrected in the notes ("near parity", "the dominant part"); the bounded large-block retention arm MEASURED as an instrument: a probe copy of `nested_array_utilities.hpp` with `kMaxBlock` / `kBudget` overridable (`-DBLADE_POOL_MAX_BLOCK`, `-DBLADE_POOL_BUDGET`; both raised together, keeping `kMaxBlock <= kBudget`), the phase-timed recur / stencil emissions rebuilt against it. No default changed: the policy stays the user's call, now with numbers |
| B2l | revisit: the ceiling is an estimate; a hand-patched serial licensed recur with the existing `lane_state` / `feed_sq` / `finish_sq` would settle it | MEASURED: `recur_orig` emitted C++ hand-patched into the blocked P1 form (blocks of 128 / 256 / 512 cells, same lane tree, stores kept), interleaved against the whole-row `sq`, raw bits compared |
| D1 | revisit the instrument: 201 pack panels, not 67; compare against the ~12 ms tile phase (then 2.0-2.1%, straddling the bar); try `nowait` on the final loop; a real `dsyrk_tri` instrument before any verdict | the instrument re-run with 201 panels (MEASURED below); the verdict softened to "deferred: instrument the real driver" and a follow-up card names the `nowait` experiment |
| stencil read-back | keep the licence; the DRAM diagnosis is not established (`bw.cpp` measured WRITES; a 24 MB read in 1.5-1.9 ms is already 12.6-16 GB/s) | a READ probe (`rd.cpp`: streaming read and the row-checksum shape, 24 MB and 80 MB, T = 1/2/4/8, pre-touched, team already running) MEASURED; the note's wording corrected |
| G51 | build now, narrowly (two admission barriers: `rowSlotAdmission` takes only `IRReduceCompute`, and `IRTotality.walk` has no `IRReduce` / row-view case); ~60-150 lines + tests | NOT built in this wave (it would re-open the gates); proposed as the top follow-up card with Codex's scoping |

## MEASURED on the quiet box (20:12, after the stray `find` was killed; `secondlook.ps1`, console `C:\bt_w3\ab\secondlook3.txt`)

**G52 retention arm** (`kMaxBlock` 64 MB, `kBudget` 96 MB, the pool header's two constants made
overridable in a probe copy of the runtime headers; the phase-timed emissions rebuilt against it;
5 runs per cell, two cells each, medians of the cumulative phase timestamps, ms):

| program | T | default: region / dealloc / end | retention: region / dealloc / end |
|---|---|---|---|
| recur (E-G5 source) | 1 | 9.3-9.4 / +1.8-1.9 / 11.3 | 9.5 / +0.03-0.1 / 9.6 |
| recur (E-G5 source) | 8 | 7.9-8.3 / **+3.4-3.5** / 11.5-11.8 | 7.8-8.1 / **+0.02** / **7.9-8.2** |
| stencil (licensed source) | 8 | producer 8.6-9.2 / +0.8-0.9 / 9.7-10.2 | 9.2 / +0.04 / 9.3-9.4 |

The teardown cost leaves the timed region entirely (the retained block is released at thread
exit, after the stop line -- exactly where the Fortran arms free theirs); recur mt8 lands at
7.9-8.2 ms against the expert's 7.5 (7.0-8.6) measured earlier on the loaded box. No default
changed: a bounded retention policy (both constants raised together, `kMaxBlock <= kBudget`) is
the user's call; it changes the harness's recorded peak working set only when later allocations
would have reused the pages.

**B2l hand patch** (serial licensed recur, the row self-dot fed per producer block through the
existing lane state; blocks of 128 / 256 / 512 cells; T=1, 7 interleaved runs): one-shot 13.3 ms
(11.1-16.2), blocked 14.0 / 13.7 / 13.0 (11.1-15.8); checksum bits IDENTICAL in all four
(`0x1.4f6d56b38d541p+22`: the streaming tree reproduces the one-shot tree, as the header's
invariant promises). Within noise; no measurable gain at the 0.35 ms ceiling: **B2l stays unbuilt**.

**D1 with 201 pack panels**: today 251-293 us per call; the persistent shape 284-335 us:
unchanged, **dropped**.

**READ bandwidth** (`rd.cpp`, pre-touched, team already running, best of 5): 24 MB stream read
22.2 GB/s at T=1, 36.5 at T=2, 53.0 at T=4, 33.0 at T=8; 80 MB 20.9 / 31.3 / 36.8 / 26.8; the
row-checksum shape within 10% of the stream. **Reads scale** (2.5x to four threads; eight
threads are slower than four). So the earlier "DRAM does not scale on this box" diagnosis was
wrong FOR READS -- it extended a WRITE probe, and writes really are flat (re-measured quiet: 8.6-10.8 GB/s at every team size) -- and stencil's input checksum
(1.5-1.9 ms for 24 MB = 12-16 GB/s, Path B over the rows with the lane-unrolled `__wrap` chain
per row) is SLOWER than a streaming read of the same bytes (0.7 ms at T=8): a real ~1 ms item,
re-measured on the quiet box in `reprobe.ps1` (1.8-2.1 ms there) and recorded as a follow-up (the emitted per-row
form, not the licence, is the suspect).
