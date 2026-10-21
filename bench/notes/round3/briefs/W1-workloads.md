# W1-5 / C -- two new bench workloads: N07 (resident two-FIR / sibling-map fusion) and N09 (tensorop)

Read `BUILDER-PREAMBLE.md` first. Brief: `research/C-workloads.md` sections N07 and N09 (complete sources in
`scratchpad/c-work/n07.blade`, `n09.blade`; all ten `blade check` today). Review: `reviews/codex-review-C.md` -- per-workload
verdicts N07 ADD AFTER FIXES, N09 ADD AFTER FIXES; findings 1 (BLOCKER, N09 outputs), 8 (storage claims), 10 (regimes /
sizes / licences), 11 (inputonly policy), 13 (N07 scope). No compiler change in this item: it adds two kernels to the suite.

Templates: `bench/kernels/<k>/{kernel.json,spec.md,<k>.blade,natural.f90,expert.f90}` for stencil (a halo kernel), dot /
fusedchain (inputonly twins), gram (matmul-class expert); protocol `bench/README.md` and `bench/kernels/README.md` (no
power-of-two extent anywhere; named outputs stored in every arm; inputonly twin when input generation is > 5% of the
time; checksum reads every stored output; pins at smoke size in `bench/harness/validation/pins*.json` -- see how the
harness generates / checks them, `bench/harness/run.py check <kernel>`). Fortran: gfortran 16.2 ucrt64 and ifx via
`bench/fortran/build_ifx.cmd`; the harness builds and runs the twins (`run.py check` at smoke size is your gate; it
compiles Fortran -- fine on this box now).

## N07 -- `twofir`: the honest name is "resident two-output FIR / sibling-map fusion"

Not ghost reuse (review C/13: in-memory `Chunked` adds nothing; the per-segment ghost path exists only for streamed
sources). Two direct FIR maps over one stored input with DIFFERENT output domains (y1's six-only fringe centres outside
y2's domain); both y1 and y2 named outputs, stored; separate +0 `prodsum` checksum chains, combined s1 + s2; an inputonly
twin (input generation is a large share). Natural twin: two separate loops (one per output). Expert twin: one fused direct
loop producing both outputs from the same loads (a 17-cell ring is an OPTIONAL reviewed alternative, not the expert).
Sizes: non-power-of-two (the brief's n ~ 3,000,017 or similar); smoke size small; both ~48 MB principal payload -- check
the 64 MB rule with the live input included (review C/8). Hypothesis: parity (Blade already fuses sibling maps that read
the same input? -- verify with `blade plan`; if the two maps are emitted as two passes, that is the round-3 transformation
this workload measures: say so in spec.md).

## N09 -- `tensorop`: named-axis tensor-product operator action

Outputs: X, A, B, C and Y ONLY (review C/1: the intermediates T1 / T2 are the compiler's; never required stores). The
contraction order I then J then K is the specification; the expert may choose intermediate lifetimes / layouts but not the
factor order. Baseline Blade spelling = the existing `m.mode_product` / unfold-`matmul` idiom (`MathElaborate.fs` ~349-367;
`m.matmul` routes to the native 6x8 `dgemm_nn` tile) -- measure THAT spelling first; a strided-contraction recognizer is a
later item, not this one. Strict pins from an ordered scalar oracle (not numpy einsum with free reassociation); the two
unseeded reduce chains start from their first product (keep that seed distinction in the oracle). Natural twin: three
textbook triple loops in the specified order; expert twin: blocked / dgemm-shaped contractions with the same factor order.
Sizes non-power-of-two, ~28 MB for the four tensors plus factors; smoke small. Regimes: serial + mt8, licensed + repro
(fastmath if the kernel has a fold the vendor flags change). Hypothesis: today 3-10x LOSS (brief) -- record the mechanism.

## Both

Add every legal `where omp` request on independent maps, generation and checksums (review C/10) with no captured writes;
document each loop's form from `blade plan`. Record the storage estimate per arm (payload + scratch) in spec.md.
Validate: `python bench/harness/run.py check twofir` and `check tensorop` at smoke size -> `0 failure(s)` for all arms
(Blade both lanes, gfortran natural / expert, ifx natural / expert, inputonly twins); pins generated per the harness's
procedure (read `bench/harness/README.md`); `blade test` has no block for new kernels -- run `blade check` on both sources
and `blade run` once at smoke size in both regimes. No full-size timing (the orchestrator's A/B does that). Report the
checksums, sizes, the forms `blade plan` reports, and anything the harness flagged.
