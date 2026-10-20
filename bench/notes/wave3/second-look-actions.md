# The Codex second look (`codex-second-look.md`) -> what this session did with it

Requested by the user mid-wave: a second look at the options that did not show clear success.
Verdicts and the actions taken (MEASURED results are appended below as they come in).

| option | Codex verdict | action |
|---|---|---|
| G49 teardown | revisit the retention policy; the TLB-shootdown explanation is a hypothesis, "region parity" overstates (expert 5.38 vs Blade 5.9-6.3 pre-touched = 10-17%) | wording corrected in the notes ("near parity", "the dominant part"); the bounded large-block retention arm MEASURED as an instrument: a probe copy of `nested_array_utilities.hpp` with `kMaxBlock` / `kBudget` overridable (`-DBLADE_POOL_MAX_BLOCK`, `-DBLADE_POOL_BUDGET`; both raised together, keeping `kMaxBlock <= kBudget`), the phase-timed recur / stencil emissions rebuilt against it. No default changed: the policy stays the user's call, now with numbers |
| B2l | revisit: the ceiling is an estimate; a hand-patched serial licensed recur with the existing `lane_state` / `feed_sq` / `finish_sq` would settle it | MEASURED: `recur_orig` emitted C++ hand-patched into the blocked P1 form (blocks of 128 / 256 / 512 cells, same lane tree, stores kept), interleaved against the whole-row `sq`, raw bits compared |
| D1 | revisit the instrument: 201 pack panels, not 67; compare against the ~12 ms tile phase (then 2.0-2.1%, straddling the bar); try `nowait` on the final loop; a real `dsyrk_tri` instrument before any verdict | the instrument re-run with 201 panels (MEASURED below); the verdict softened to "deferred: instrument the real driver" and a follow-up card names the `nowait` experiment |
| stencil read-back | keep the licence; the DRAM diagnosis is not established (`bw.cpp` measured WRITES; a 24 MB read in 1.5-1.9 ms is already 12.6-16 GB/s) | a READ probe (`rd.cpp`: streaming read and the row-checksum shape, 24 MB and 80 MB, T = 1/2/4/8, pre-touched, team already running) MEASURED; the note's wording corrected |
| G48 | build now, narrowly (two admission barriers: `rowSlotAdmission` takes only `IRReduceCompute`, and `IRTotality.walk` has no `IRReduce` / row-view case); ~60-150 lines + tests | NOT built in this wave (it would re-open the gates); proposed as the top follow-up card with Codex's scoping |
