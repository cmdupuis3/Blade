# Equivariant-NN showcase notebooks

Status: IN FLIGHT — probes green; branch `feat/eqnn-notebooks` (off master);
live-plot infra IMPLEMENTED 2026-08-25 (§4); Blade-REPL session
bugfixes DONE (uncommitted in that repo); raw datasets fetched to
`C:\Users\cdupu\Data\{md17_aspirin,tetris}`; reverse-mode combinator AD is a
committed workstream (§5); notebooks not yet built. Planned 2026-08-25 by a
five-agent fan-out (ML core, data/verification, plot infra, moment jet,
dataset web-search) plus serialized `blade run` probes; every capability claim
below was verified against source or corpus by file:line, and every idiom
marked ✅PROBED actually ran.

## 0. The pitch

Three `.bladenb` notebooks that are the killer-app demo:

- **NB1a — deduction (Tetris).** The eight e3nn tetromino shapes (two of them
  a chiral mirror pair), classified by a small certified network trained in
  seconds, zero download. This notebook front-loads *deduction*: cells whose
  payload is the compiler refusing a non-equivariant layer (BL4008 naming the
  sanctioned op), the BL4011 cell where the compiler **writes the
  `where ml.equiv(O3)` clause itself** (strongest group first), and the
  inversion-refusal message that proves a body is SO(3)- but not
  O(3)-equivariant and names both repairs
  (`src/ml/compiler/MLEquivMessages.fs:81-86`, pinned
  `tests/corpus/diagnostics/027`). The chiral pair is the punchline: parity is
  in the type, and separating the mirror twins *requires* the odd channel.
- **NB1b — the benchmark (MD17 aspirin).** A deep O(3)-equivariant network
  trained on a real, famous benchmark molecule in the low-data regime, with
  the per-batch loss curve refreshing live in the notebook (§4 infra), and a
  verification phase whose money cell is force vectors F = −∂E/∂positions
  transforming exactly as vectors under rotation — obtained from first-order
  `ad.grad`, never trained on.
- **NB2 — the moment jet.** NB1b's task, but the raw point cloud is first
  summarized as a jet of central comoments (orders 2–4), decomposed into
  irreps **by the compiler's own certified Sym^K tables**, and fed through the
  same network. Moment-tensor-potential/ACE-style descriptors and the network
  in ONE type system, with the descriptor-to-network layout contract a
  compile-time constant instead of a convention boundary. Baseline = NB1b.

Prior art in-repo: `examples/07_subgrid_closure_discovery.blade` is the
order-2 version of NB2 end-to-end; `tests/corpus/ml-e2e/001+002` are full
training runs (forward, `ad.grad` reverse, SGD as a recursive array)
reproducing an F# oracle to the ulp.

## 1. Established facts (planning verdicts)

**Training surface.** Reverse-mode `ad.grad(loss)` works today: ABI = primals
then one `mut` accumulated buffer per Float-array param; Int primals are
non-differentiable and get no buffer ✅PROBED (P1 — this is the mini-batch
gate: batch index enters as a trailing Int). `%` works inside differentiated
bodies ✅PROBED (P7a). `ad.grad` differentiates a loss that captures a
provider-read array ✅PROBED (P5) — so real ingestion and training compose.
Combinators are refused in reverse mode (`GradExpand.fs:478,518`), so losses
are flat-index straight-line code; `if`/`match` refused in reverse-diffed code.

**The flat-state training idiom** ✅PROBED (P2), replacing `examples/07`'s
91-slot hand-enumeration: one `Array<Float like State>` packing all weights
plus a loss slot; `sgd_step` = `let mut ds = replicate(...); let lv =
ad.grad(loss)(s, ds); ((s - LR * ds) * KEEP) + SLOT * lv` with literal masks;
trajectory = `let rec traj: Array<Float like Steps, State>` seeded from a
rand row; loss column extracted by
`method_for(range<Steps>) <@> lambda(n) -> traj(n)((LOSS : State)) |> compute`.
All of it runs. Two quirks: `rand.<fam>` is only legal as a **bare** top-level
binding value (BL6002 — bind `let n0 = r.normal(7, N)` then scale), and
untagged slot reads warn BL4003 (cast `(k : State)` or accept the pinned
warn, as `ml-e2e/001:29-32` does).

**NB2's premise** ✅PROBED (P3, the decisive one): for centered points,
`mean_p ml.derive_poly(V3, K, sym_spec(V3,K), x_p, ones)` equals
`ml.sym_to_irreps(ppl.comoments(A, 2))` **exactly, per-block ratio +1 on l=2
and −1 on l=0** (fixed sign convention between the Sym^K label basis and
`symToIrrRows`). The compiler's `SymPowerTables.fs` already builds exact
T_{j,l} bases for K ≤ 4 with build-time self-certification, so orders 3 and 4
need **zero compiler work** — the remembered "sym3/sym4_to_irreps missing" gap
is real (`CartesianBridge.fs:1` is rank-2-only) but fully bypassed. Convention
confirmed: feed `derive_poly` the point permuted to the real l=1 order
**(y, z, x)** (`MLElaborate.fs:217-219`).

**Live emission** ✅PROBED (P4): `display.emit` inside a function called from a
rec-array slice compiles and emits one ordered frame per step; in the compiled
lane frames hit stdout as the recursion runs. So per-batch streaming from
inside the training loop is real — what's missing is transport and rendering
(§4): the interpreter lane buffers frames to end-of-run
(`DisplayFrame.fs:174-177`), ids are per-run ordinals with no stable identity
(`DisplayFrame.fs:164-166`), the `{"event":"display"}` streaming channel is
spec'd (`Blade-REPL/docs/display-frames.md` §3) and **parsed** by the client
(`protocol/client.js:233-241`) but no compiler-side writer exists, and the
notebook contributes **no renderer** for the plotly MIME (cells show a text
summary; the chart lives in the Plots panel webview).

**Session-memo rules** (govern all cell layout): each cell evaluation re-runs
the session; a name-keyed memo makes that linear, but a binding that emits a
frame is excluded and re-runs every pass (`src/Interp/Run.fs:461-490`) —
**training and plotting must be separate top-level bindings**; the memo only
survives clean interpreter runs — a g++ fallback drops it entirely
(`ReplSession.fs:1125-1130`), so cells must be sized to the interpreter
(budgets: 1e9 steps; `ml-e2e/002`-scale training measured 25.4 s Release,
`tests/InterpDiff.fs:243-244`). The notebook eval timeout defaults to 180 s
(`Blade-REPL/package.json:88-92`) — now fixed by the new
`blade.notebookEvalTimeoutSeconds` setting (§7 item 1).

**Sizing verdict:** no message-passing GNN (edge loops are what made
`ml-e2e/002` cost 25 s at toy scale). NB1 is a **per-sample deep stack**:
per-atom features pooled once, then embed + 3 certified blocks
(`tensor_product → gated → derive_linear`) + `norms` readout, hidden spec
around `[(0,0,4),(1,1,2),(2,0,1)]` (dim 15). Print
`ml.tp_weight_dim`/`ml.hom_dim` in an early cell — parameter counts are
Schur's lemma, not hyperparameters, and that *is* a showcase beat.

**Datasets (both in, web-verified, raw copies under `C:\Users\cdupu\Data\`):**
- **MD17 aspirin CCSD** (`C:\Users\cdupu\Data\md17_aspirin\`) —
  `https://sgdml.org/secure_proxy.php?file=data/npz/aspirin_ccsd.zip`, ~1.4 MB,
  1,500 samples × fixed 21 atoms (C9 O4 H8 — static extents, no ragged),
  energies + forces, the NequIP/MACE/sGDML benchmark lineage. NB1b trains in
  the famous low-data regime (N≈100) — also exactly where descriptor methods
  classically shine, the fair fight NB2 wants. `.npz` = zip-of-npy, readable
  with BCL-only F# (~150 lines; the repo's only NuGet dep stays
  `ZstdSharp.Port`). **No HDF5 anywhere.** Schema (verified on disk
  2026-08-25): the zip holds TWO npz archives — the benchmark's own split,
  `train` (1000) and `test` (500), extracted as `train_*/test_*` .npy files;
  `R`/`F` are float64 shape (N,21,3) **fortran_order=True (column-major — the
  reader must honor strides or transpose, silent-corruption trap)**; `E` is
  (N,1) C-order; `z` is uint8 (21,); `name/theory/type/md5` are scalar
  strings.
- **e3nn Tetris** (`C:\Users\cdupu\Data\tetris\pieces.csv`) — the 8 canonical
  tetromino shapes (4 points each, integer coords, chiral pair included)
  archived from the e3nn source; NB1a replicates them under baked sampled
  rotations (`oracles/ml/Rotations.fs`) into a classification set. Zero
  network dependency at notebook time.
- Fallback if aspirin sours: synthetic 5-particle N-body via `.fsx` generator
  (EGNN task family, zero download).
The `.fsx` generators read the raw data from `C:\Users\cdupu\Data\` and write
committed Zarr stores under `examples/data/` (station_temps precedent), so the
notebooks themselves never touch the network or machine-local paths.

**RaggedIdx is out** for NB1/NB2: no provider integration, no AD, no `let rec`,
no ML-op coverage; lens must be compile-time (BL4018). Fixed-size everything.

## 2. NB1a and NB1b — cell plans

### 2a. NB1a — Tetris, the deduction notebook

Emphasis: the type system doing the thinking; training is almost incidental
(seconds, full-batch, 8 shapes × ~32 baked rotations each). Cell arc:

1. *md* — thesis + the eight shapes; the chiral pair introduced.
2. code — `zarr` load of the tiny tetris store; specs; sizing builtins
   printed (parameter counts as Schur's lemma).
3–8. **The deduction suite** (this notebook owns it): the Hadamard refusal
   (BL4008 naming `ml.tensor_product`), the raw-component-read refusal, the
   **inversion refusal** (`u·(v×w) -> Float` fails "IS SO(3)- but not
   O(3)-equivariant"; the `IrrepsIdx<[(0,1,1)]>` twin certifies right below),
   and the **BL4011 cell** where the compiler writes the missing
   `where ml.equiv(O3)` clause verbatim. Failing cells never join the session
   (`ReplSession.fs:1180-1182`), so these are safe.
9. code — the certified classifier: embed TP + 2 blocks + `norms` + linear
   head to 8 one-vs-rest scores (squared-error loss — no softmax, no `if` in
   the loss). An O(3)-certified variant whose features are all parity-even
   **provably cannot separate the chiral pair** (its scores are inversion-
   invariant); the working model routes an odd channel through. Show both:
   the failure is a theorem, not a bug.
10. code ⚑ — training (flat-state idiom, full-batch, ~200 steps, seconds) +
   `plot.stream` loss curve as the infra's hello-world.
11. code — verification: every shape re-classified identically under fresh
   baked rotations; the chiral pair separates; an even-only ablation ties at
   exactly 50 % on the pair. All EXPECT-pinnable.
12. *md* — closing: what the compiler knew before the first gradient step.

### 2b. NB1b — MD17 aspirin, the benchmark notebook

Emphasis: real data, live training telemetry, and the force-vector
certificate. Deduction cells appear once, briefly (one refusal + one BL4011),
with a pointer to NB1a for the full suite.

Ingestion: one `.fsx` (`examples/tools/make_aspirin_zarr.fsx`, following
`make_qg_zarr.fsx`'s `#load` discipline) reads the raw npz members from
`C:\Users\cdupu\Data\md17_aspirin\` and writes **one Zarr store** with
everything downstream pre-shaped, because there is no reshape and AD bans
combinators in the loss:

```
aspirin.zarr/
  pos_train (samp_tr, atom, xyz)   # centered per sample; ALSO a (y,z,x)-
  pos_test  (samp_te, atom, xyz)   #   permuted copy for irreps feeding
  e_train   (samp_tr,)             # energy, standardized (dimensionless —
  e_test    (samp_te,)             #   Grad.fs:110 refuses unit-carrying losses)
  f_test    (samp_te, atom, xyz)   # forces, for the verification phase only
  perm      (epoch, samp_tr)       # per-epoch permutation table (no rand.shuffle)
  rot3      (rotk, nine)           # K=16 sampled rotations (oracles/ml/Rotations.fs)
  d_l2      (rotk, 25)             # baked D-matrices for any l=2 checks
```

Separate train/test **variables** give separate named index types — mixing
them is a compile error, a refusal worth one markdown cell.

Cells (≈18, trimmed against NB1a — the deduction suite shrinks to cells 7–8;
heavy ones marked ⚑):

1. *md* — thesis: the hypothesis space is a type.
2. code — imports, specs, sizing builtins printed (`tp_weight_dim`,
   `hom_dim`, `total_dim`): the parameter-count-as-theorem cell.
3. *md* — the dataset and the low-data regime.
4. code ⚑ — Zarr loads (`z.load` / `.vars` / `|> z.read`); per-species
   pooled features: per-atom `ml.y_to(2, …)` weighted by 2–3 fixed radial
   channels, summed per species (C/O/H) as flat additive `let rec` slices —
   uncertified assembly, same shape as `ml-e2e/001`'s conv, differentiable.
5. *md* — the four admissible moves (CG product, gate, Schur linear, norms)
   and what each is not allowed to be.
6. code — the certified model: embed TP + 3 × `eq_block` + `ml.norms` +
   invariant readout, all under `where ml.equiv(O3)`. No output; compile-time
   acceptance is the payload.
7–8. *md* + code — **refusal cell**: Hadamard `h * g` under `ml.equiv(O3)` →
   BL4008 naming `ml.tensor_product` (failing cells are never committed to
   the session — `ReplSession.fs:1180-1182` — so this is safe).
9–10. *md* + code — **the inversion refusal**: `u·(v×w) -> Float` fails with
   the "IS SO(3)-equivariant but not O(3)" message; the fixed twin
   (`-> IrrepsIdx<[(0,1,1)]>`) certifies right below. Best cell in the deck.
11–12. *md* + code — **BL4011**: the uncertified twin of `eq_block`; the
   compiler prints the exact `where ml.equiv(O3)` clause + deduced signature.
13. *md* — training: no loops in the language; the epoch axis is a recursive
   array; the optimizer is whole-array arithmetic.
14. code ⚑⚑ — the training cell (flat-state idiom, §1): mini-batch loss takes
   `(ep, b)` as trailing Int primals reading `perm`; per-step loss lands in
   the State slot. **No frame emission from this binding** (memo rule).
   Target envelope: N=100, B=20, ~25 epochs ⇒ 125 steps; interpreter cost to
   be measured first thing at build time and N/epochs trimmed to keep the
   cell interactive.
15. code — the live plot binding (separate!): per-epoch
   `plot.stream(...)` calls carrying that epoch's batch losses (x = global
   batch index), epoch boundaries marked; final `plot.line` persists as the
   cell output. §4 carries the transport.
16. code — held-out test loss at the final weight row.
17. code ⚑ — verification: (a) rotate `pos_test` by each of the 16 baked
   rotations, re-run, pin max relative invariance error via an
   indicator-count (no `max` fold exists — BL2001, ✅PROBED P7b);
   (b) **forces**: `ad.grad` of the energy w.r.t. the *positions* param gives
   F = −∂E/∂r with no second-order AD; pin F(R·pos) = R·F(pos) numerically —
   an equivariant-vector certificate on a real molecule, without ever
   training on forces.
18. *md* — closing: refusal, deduction, numeric shadow = three views of one
   compile-time fact.

## 3. NB2 — cell plan

Shares the store, split, budget, and model shape with NB1 (byte-identical
data is the load-bearing comparability requirement). Original cells:

- **Jet former** ⚑: per point, three `ml.derive_poly(V3, K, sym_spec(V3,K),
  x_yzx, ones)` calls (K=2,3,4), mean-pooled per sample (and per species) at
  top level → `Array<Float like SampleIdx, IrrepsIdx<JETSPEC>>`,
  `JETSPEC = 2×l0 ⊕ l1 ⊕ 2×l2 ⊕ l3 ⊕ l4` (dim 31). The l=3/l=4 content
  exceeds the `y_to` lmax≤2 cap — the jet sees angular structure NB1 cannot.
- **Three-route pin cell**: the ✅PROBED P3 identity in-notebook (derive_poly
  mean vs `sym_to_irreps ∘ ppl.comoments` vs centered `ppl.moments(·,k)`),
  the `sgs/009` "one quantity, three subsystems" move — this is what licenses
  trusting orders 3–4 where only one route exists.
- **Spec-introspection cell**: `ml.irreps_len/_l/_mult/_offset(JETSPEC, b)`
  printed as statics — the compiler narrating the jet's decomposition.
- Network input = `derive_linear(JETSPEC, MSPEC, …)`; downstream identical to
  NB1. A `derive_poly` K=2 head lets l=3/l=4 reach the invariant readout (a
  linear head would silently drop them — one markdown sentence).
- **Ablation** ⚑⚑⚑: JET2 (dim 6) / JET23 (16) / JET234 (31), parameters
  approximately matched by widening MSPEC's l=0 multiplicity, counts printed.
  Metrics: test MSE, equivariance residual, wall-clock, param count. Non-
  power-of-two extents, medians (CLAUDE.md bench discipline).
- Phase-B teaser: one learnable scalar upstream of the former via `ad.jvp`
  (forward mode differentiates pack comoments — `ad-jvp-comb/079`; reverse
  mode refuses all combinators, so end-to-end moment learning is future work).
- Narrative cell: MTP/ACE positioning — the descriptor and the model in one
  certified type system; a mis-transposed order-3 basis is not a wrong
  number, it is a program that does not exist.

Honest hedge: comoments discard point identity; if NB2 loses on MSE, the
monotone order-2→3→4 ablation plus "certified descriptor at a fraction of the
wall-clock" is still the result. The low-data regime is the fair fight.

## 4. Live-plot infrastructure — IMPLEMENTED 2026-08-25 (this repo committed; Blade-REPL half pending in that repo)

Landed in the working trees and verified green: Blade full suite 5119/0 (zero
skips), display/interp-display/display-frames/ide-eval/surface all green,
smoke sentinel bytes match the contract below exactly (escaped-channel case
included); Blade-REPL hermetic suites all green (only the pre-existing
`group_bucket` grammar drift remains red in `npm test`, fixed in a separate
session). Implementation notes: `display.emit_id` consumes NO ordinal (adding
a stream never renumbers neighboring charts); streamed-but-sunk frames still
count in `Frame.emitted()` so streaming bindings stay excluded from the
session memo; a g++-lane fallback re-delivers stream frames in `display[]`,
which the panel degrades to a merge-redraw (documented at both ends); the
notebook renderer resolves the existing `media/plotly.min.js` by relative URL
(no re-vendoring) — one manual VS Code run still owed to observe it live.

Target UX: training cell runs minutes; a plotly chart refreshes per batch,
x-axis spans all epochs with boundary markers; the final chart persists.

**Frozen wire contract** (both repos implement against this, verbatim):
- Stream MIME: `application/vnd.blade.plotstream.v1+json` (inline-JSON data,
  `+json` rule of display-frames.md §1).
- Frame `data`: `{"channel":"<name>","epoch":<int>,"x":[...],"y":[...],
  "title":"...","xlabel":"...","ylabel":"..."}` — `epoch` −1 = unmarked;
  label fields carried whenever provided.
- Frame `meta`: `{"id":"<channel-name>","stream":true,"backend":"plotly"}` —
  the id IS the channel name, stable across calls and session replays; the
  panel merges on it.
- During an `ide serve` eval, each emission is forwarded immediately as one
  flushed NDJSON line `{"event":"display","id":<evalRequestId>,"frame":{…}}`
  (the §3 event the client already parses). With no sink installed
  (`blade run`, `blade repl`, corpus, differential gates) the frame is an
  ordinary sentinel stdout line, byte-identical between interp and compiled
  lanes — every existing pin survives.
- Blade surface: `plot.stream(name, x, y [, e: epoch, t: title, xl: xlabel,
  yl: ylabel])` → Bool in `stdlib/plot.blade`; new primitive
  `display.emit_id(mime, id, data[, meta])` with mime/meta elaboration-time
  literals, id/data runtime Strings.

Winner design (full detail in the planning transcript):

Blade repo:
- `display.emit_id(mime, id, data, meta)` — runtime `id` spliced where the
  ordinal goes (`DisplayFrame.fs:164-166` + C++ mirror `:274-282`, one arm in
  `DisplayElaborate.elabOp`). Gives any chart a stable identity; incidentally
  fixes ordinal drift when earlier cells add plots.
- A frame **sink** hook in `DisplayFrame.fs`: when set and the mime is the
  stream mime, forward the composed line instead of buffering. Installed
  ONLY by `IdeServe.handleEval`, which wraps it as
  `{"event":"display","id":…,"frame":…}` per the already-parsed §3 protocol.
  With no sink, both lanes stay byte-identical — every existing stdout pin
  and the interp↔g++ differential gate survive untouched.
- `stdlib/plot.blade`: `plot.stream(name, x, y, e: epoch, …)` following the
  existing factory shape — rank-1 arrays cover per-epoch calls (the proven
  placement) and single points (the P4-proven in-recursion placement).
  stdlib is runtime-read: iterating needs no rebuild.
- Corpus: `display/012_plot_stream.blade` EXPECT pins + `Test_Display.fs`
  wire-byte pins.

Blade-REPL repo:
- `plots.js`: stream-mime path in `appendFrame` (concat x/y, epoch
  boundaries), throttle `reveal`/`log`, 100 ms coalescing before
  `postMessage`, `Plotly.extendTraces` in the webview, stride decimation
  ≥20k points.
- `notebook.js`: subscribe during execution to animate the cell output
  (~2 Hz), unsubscribe in finally.
- **Renderer contribution** (`contributes.notebookRenderers`) for the plotly
  and stream MIMEs — without it, cells show a text summary and charts live
  only in the Plots panel.
- Config: `blade.notebookEvalTimeoutSeconds` (see §6 bugs).

Fallback that works with zero infra: per-epoch training chunks in separate
cells, each ending in `plot.line` — merged by `meta.id` in the panel. Ugly
but real; keeps NB1 unblocked while the sink lands.

Product decision left open deliberately: notebook outputs are transient by
design (`notebook.js:861`, `transientOutputs: true`; `.bladenb` stays a valid
Blade program with no output slots). "Final chart persists across reopen"
needs either `transientOutputs: false` + a sidecar outputs file, or accepting
re-run-to-see. Decide before NB1 ships.

## 5. Reverse-mode combinator AD (the C-track reverse patch)

Committed workstream (user decision 2026-08-25), amending
`plan-ad-combinators.md` §4 C6's "do not build a general
reverse-through-combinators pass — and stop." The amendment is deliberately
narrower than a general pass, and C6's own reasoning still bounds it:

1. **C1 linear-closure lowering into grad's lane** (C6's own first
   recommendation): `pure`, `|> compute`, `<*>`, `stack`, `sequence`,
   `replicate`, `transpose`, `guard`, `<|:>`, hoisted-mask `compound` — stop
   refusing programs the existing mut-buffer lane already handles.
2. **Eager map pipelines lowered pre-grad**: `method_for(range/zip/arrays)
   <@> lambda (rank-0 kernel) |> compute` rewritten to the element-write loop
   form that is ALREADY in grad's v1 subset (element construction writes +
   additive accumulation), then differentiated by the existing machinery —
   no new adjoint theory. This is the piece that unlocks `ad.grad` through
   the ppl formers (they elaborate to exactly this shape,
   `PplElaborate.fs:462-514`) — i.e. **NB2 Phase B: end-to-end learnable
   transforms upstream of the moment jet in reverse mode**, and grad through
   `LOSS_SLOT`-style mask constructions without literal fallbacks.
3. **The four C6 adjoints** where the surface inverse exists and the win is
   BLAS or storage: `gram` first (both adjoints are matmuls), additive
   `reduce ↔ broadcast`, `compound ↔ <|:>`, `stack`/`transpose`.
4. Keep the refusals for the no-surface-inverse set (`join`, `halo`-reverse,
   `group_by`-reverse, `decompact`) and for `<|>` — with messages naming the
   nearest supported spelling, per C6/§2.13.

Verification stance: every new reverse rule gets the jvp-vs-grad residual pin
(the `ad-jvp-comb` differential gate) plus finite differences; the ppl-former
case additionally pins against the Phase-A fixed-featurization gradient
(which must be identical when the learnable pre-transform is the identity).
Sequenced AFTER the §4 infra lands (§7).

## 6. Merged gap census

Classes: BUG / MISSING-FEATURE (MF) / WORKAROUND-EXISTS (WE) / BY-DESIGN (BD).
Blocks: hard/soft per notebook; INFRA = §4 workstream.

| # | Gap | Class | Evidence | Blocks | Fix site |
|---|-----|-------|----------|--------|----------|
| 1 | Notebook eval timeout 180 s kills real training cells | BUG | `Blade-REPL/src/notebook.js:418-420`, `package.json:88-92` | NB1-hard | **FIXED 2026-08-25**: `blade.notebookEvalTimeoutSeconds` (default 1800), notebook path only |
| 2 | Eval timeout/crash never sets `needsReplay`; next cell runs on an empty session | BUG | `Blade-REPL/src/notebook.js:466-473` vs `:489-495` | NB1-hard | **FIXED 2026-08-25**: non-`protocolError` rejections with kept history set `needsReplay` (+ hermetic tests) |
| 3 | No compiler-side writer for the spec'd `{"event":"display"}` stream | MF | reader `protocol/client.js:233-241`; no emitter in `IdeServe.fs` | NB1-hard (live refresh) | INFRA: `DisplayFrame.fs` sink + `IdeServe.handleEval` |
| 4 | Interpreter buffers frames to end-of-run | MF | `DisplayFrame.fs:174-177`; `Interp/Run.fs:477-478` | NB1-hard (live) | INFRA: sink bypass |
| 5 | Frame ids are per-run ordinals; `meta` must be literal — no stable runtime chart identity | MF | `DisplayFrame.fs:164-166`; `DisplayElaborate.fs:10-13` | NB1-hard (live) | INFRA: `display.emit_id` |
| 6 | No notebook renderer for plotly MIME (cells show text summary) | MF | `Blade-REPL/package.json` contributes; `notebook.js:212-214` | NB1-hard (charts in cells) | INFRA: renderer contribution |
| 7 | Frame-emitting bindings excluded from session memo (re-run every cell) | WE — separate bindings | `Interp/Run.fs:461-490` | NB1-soft | notebook structure |
| 8 | g++ fallback drops the whole memo | WE — stay interp-sized | `ReplSession.fs:1125-1130` | NB1-soft | size cells; later: persist memo |
| 9 | Notebook outputs transient; nothing survives reopen | MF (product choice) | `notebook.js:861`, `:168-179` | NB1-soft | decision + sidecar file |
| 10 | `let rec` carries scalar element types only — no tuple-of-arrays state | MF | zero tuple-typed `let rec` in corpus | NB1-soft | ✅flat-state idiom PROBED (P2) replaces it |
| 11 | `rand.<fam>` legal only as bare top-level binding value (BL6002) | BD/quirk | ✅PROBED P2 | no | bind-then-scale; document |
| 12 | No `rand.shuffle`/permutation (categorical = with-replacement) | MF | `RandElaborate.fs:90-98` | NB1-soft | `.fsx` perm table; later: rand op |
| 13 | No `max` intrinsic / max-fold (`reduce(A, max)` = BL2001 unbound) | MF | ✅PROBED P7b | NB1-soft | indicator-sum idiom; later: intrinsic |
| 14 | No in-language rotation/Wigner-D application (`ml.rotate`/`rep_matrix`); certificates bake D literals | MF | `WignerTables.fs` is CG-only; `MLElaborate.fs:1228-1234` op set | NB1/NB2-soft (`.fsx` tables from `oracles/ml/Rotations.fs:33-148`) | new ml op mirroring oracle `repMatrix` |
| 15 | Reverse-mode `ad.grad` refuses all combinators ⇒ no AD through ppl formers / `method_for` | MF | `GradExpand.fs:478,518`; `ad-jvp-comb/083` | NB2-hard for Phase B only | `GradExpand.fs` (the C-track); `ad.jvp` works now |
| 16 | `ppl.comoments(A, k)` is k=2 only | MF/WE | `PplElaborate.fs:446,466` | no — center + `ppl.moments(·,k)`; better: derive_poly route | `PplElaborate.fs` subset-lattice expansion |
| 17 | Cartesian bridge rank-2 only (no sym3/4_to_irreps) | MF/WE | `CartesianBridge.fs:1,54-76` | no — ✅PROBED P3 derive_poly route | optional: source rows from `SymPowerTables` |
| 18 | `ml.y_to` capped at lmax ≤ 2 | MF | `MLElaborate.fs:207-209` | no (depth from stacking; jet carries l=3,4) | `yToDecl` closed forms |
| 19 | `if`/`match` unsupported in reverse-differentiated code | MF | `equivariant-nn.md:386-387` | NB1-soft (keep loss branch-free) | `Grad.fs` |
| 20 | `reduce` over a call-result binding refused in diffed code (BL5500) | WE | `ad/013:14-17` | NB1-soft | inline/rec-array reduce |
| 21 | No `EnumIdx` from provider data columns (only CSV headers) | MF | `CsvProvider.fs:12,219`; `NetcdfProvider.fs:348-355` | no (Int64 gather keys) | provider attr, someday |
| 22 | No reshape/view builtin | WE | no hits in `src/*.fs` | no (shape in the writer) | — |
| 23 | RaggedIdx: no providers, no AD, no rec, no ML; static lens only | MF | zero hits in providers/ad/ml corpus; BL4018 | no (fixed-size tasks) | out of scope |
| 24 | No optimizer beyond plain GD | MF | `ml-e2e/001:273-277` | no (momentum = extra state slots, userland) | stdlib later |
| 25 | Remembered "gram compact-operand ICE" | UNKNOWN | no corroborating source/corpus evidence found | no | needs a repro before any fix |
| 26 | Losses must be dimensionless (unit-carrying returns refused) | BD | `Grad.fs:110` | no (standardize in `.fsx`) | document in notebook |

Cross-resolutions from planning: NB1's "no rank≥3 bridge blocks NB2" concern
is closed by #17's probe; the certifier refusing user-space bridge matrices
(`ml-equiv/022/023`) is *by design* and the derive_poly route keeps all
constants compiler-owned.

## 7. Sequencing

1. ✅ **Bugfixes:** #1 and #2 fixed in the Blade-REPL working tree
   (uncommitted) with hermetic tests, 2026-08-25.
2. ✅ **INFRA workstream (§4)** — implemented and verified 2026-08-25 on
   `feat/eqnn-notebooks` (Blade) + main (Blade-REPL), uncommitted. Owed: one
   manual VS Code session observing the renderer + a live stream end-to-end.
3. **Data tooling:** `make_tetris_zarr.fsx` (pieces.csv → rotated/labelled
   store) and `make_aspirin_zarr.fsx` (npz reader, centering, (y,z,x) copies,
   standardization, perm + rotation/D tables); raw inputs from
   `C:\Users\cdupu\Data\`. Fallback `make_nbody_zarr.fsx` if aspirin sours.
4. **NB1a build** (fast lane — exercises the whole stack at second-scale
   cost), then **NB1b** (measure the training cell in the interpreter FIRST;
   trim N/epochs to interactive).
5. **NB2 build:** three-route pin cell first (it gates everything), then the
   jet former, ablation last (three descents — the wall-clock budget cell).
6. **Reverse-mode combinator AD (§5)** — after infra; unlocks NB2 Phase B as
   a real trained arm rather than a jvp teaser.
7. Revisit: outputs persistence decision (#9), `ml.rotate` (#14).

## 8. Risks

- Interpreter wall-clock at NB1 scale is extrapolated, not measured — the
  25.4 s `InterpDiff` datum is a GNN with edge loops; the pooled stack is far
  cheaper per sample, but 100 samples × 125 steps must be measured before
  the cell plan is frozen (mitigations: shrink N, hidden spec, epochs).
- The aspirin schema is now eyeball-verified (§1), but the fortran_order=True
  R/F arrays remain the top data-corruption risk — the `.fsx` must assert a
  known coordinate row (e.g. print atom 0 of sample 0 both ways) against an
  independent read before writing the store.
- Per-species pooled radial×Y features may underfit real PES — acceptable
  for the demo (loss decreasing credibly + verification passing is the bar),
  and it keeps NB1 and NB2 in the same information class for a fair fight.
- Zombie `Blade.exe` processes can hold `bin/Release` DLLs and fail builds
  (hit during planning — MSB3027); check before blaming source.
