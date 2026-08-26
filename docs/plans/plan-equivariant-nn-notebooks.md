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

**AS BUILT 2026-08-26** — `examples/tetris_shapes.bladenb` (16 md + 15 code
cells) + `examples/tools/make_tetris_zarr.fsx` + `examples/data/tetris.zarr`
(96 train / 48 test clouds, 12+6 seeded rotations per shape). All pins green
via concatenated `blade run` (11.3 s) AND a full ide-serve drive: every
passing cell `lane:"interp"` + `kept:true` (training cell 16.2 s, others
≤0.9 s), refusal cells fail without disturbing the session, the plot cell
delivers a live `{"event":"display"}` stream frame — §4 verified end-to-end
in a real eval. Deviations that matter: (a) the certified body is FROZEN at
a seeded+calibrated point and only the 112-weight invariant readout trains —
convex, 48/48 reproducible, and inside the interpreter budget (full-stack
training plateaued with class T mis-ranked at LOWER loss: one-vs-rest
least-squares pathology); (b) training uses ONE orientation per shape,
narrated honestly — the readout channels are certified invariants, so the
other 88 rotated copies contribute identical gradients; (c) the chirality
channel is a two-step certified TP chain (HSPEC⊗HSPEC→l1-even, ⊗HSPEC→l0-odd)
because `chiral_shape_1` has a C2 axis — every l=1 feature of it is
collinear, so any raw triple product vanishes identically; (d) the even-
ablation "ties at 50%" became the STRONGER exact pin `even_gap = 0`
(byte-identical mirror-pair scores) plus a sixth refusal cell (`head_o3`):
the chirality theorem AS a compile error, BL4008 naming the parity-odd read.

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

**AS BUILT 2026-08-26** — `examples/aspirin_energy.bladenb` (1072 lines) +
`examples/tools/make_aspirin_zarr.fsx` (hand-written BCL-only .npy reader;
fortran-order PROVEN by a bidirectional physical check: the column-major read
gives covalent bond lengths, the C-major misread provably does not) +
`examples/data/aspirin.zarr` (962 KB, 13 vars; both coordinate orders;
standardization stats stored; perm + rotation tables). FULL-STACK training —
all 249 parameters, batch 20, 50 steps, momentum 0.9 — measured 25.7 s in
the interpreter lane; complete ide-serve drive green (all cells
`lane:"interp"` + `kept:true`, refusal cell exits 1 without killing the
session, two live stream frames on channel `aspirin_loss`). Result, honest:
test MAE 5.057 kcal/mol vs 5.486 constant-predictor baseline (18 % of energy
variance; small train/test gap), energy invariance ≤5e-15, force
equivariance F(R·x)=R·F(x) ≤1.5e-15 with forces from first-order ad.grad
w.r.t. positions, f_cos vs DFT forces 0.45 (framed as "transforms
correctly", not "accurate"). Deviations that matter: (a) **N_TRAIN=500, not
100** — measured, not guessed: at N=100 every descriptor family tried
(centroid multipoles, pair histograms, local-Y, sGDML's own 210 inverse
pair distances) lands ABOVE the constant baseline on held-out geometries;
signal appears between 200 and 500; both subsets strided to span the
trajectory (the first-K frames of each split are different MD stretches with
an unlearnable offset); (b) **the descriptor is a pair-distance histogram +
centroid multipoles** — pooled per-atom Y alone carries essentially no
conformational signal; the 18 invariant histogram channels are where the
energy lives (the load-bearing lesson for NB2); (c) the deduction suite here
is one refusal + BL4011 with a pointer to NB1a.

## 3. NB2 — cell plan

**FLESHED OUT 2026-08-26** after NB1b's measured lesson (conformational
energy lives in pair-distance structure, not centroid multipoles) and three
further probes (P8/P9, this session). Same store as NB1b —
`examples/data/aspirin.zarr`, byte-identical data, split, budget — no new
data tooling.

### 3.0 The redesign

The original jet — comoments of the per-species POSITION cloud — is exactly a
generalized centroid multipole, and NB1b measured that family carrying almost
no conformational signal. So NB2 computes its headline jets over the **pair-
difference cloud**: for each species pair (C-C, C-O, C-H, O-O, O-H, H-H),
the set of difference vectors {rᵢ − rⱼ}. This is the MTP/ACE object (those
descriptors are built from neighbor vectors), and the probes make it
principled:

- The symmetrized pair cloud {±v} kills every odd order IDENTICALLY —
  ✅PROBED P9: `jet3_max_abs = 0`, exact. The notebook pins this as a parity
  THEOREM (order-3 jet of a pair cloud is the zero vector, machine-exact).
- The surviving even orders carry the pair-DISTANCE distribution in their
  l=0 slots — ✅PROBED P9: `K=2 l0 × √3 = −mean|v|²` and
  `K=4 l0 × √5 = +mean|v|⁴`, both exact. The jet is an unbinned, certified
  cousin of the histogram that won in NB1b, plus anisotropy (l=2, l=4) the
  histogram cannot see.

### 3.1 The descriptor, precisely

Per sample, per species-pair channel c ∈ 6: center nothing (pair differences
are translation-invariant by construction — one markdown sentence), take
each unordered pair once, and average `ml.derive_poly(PT, K, sym_spec(PT,K),
v_yzx, ones)` over the channel's pairs for K = 2 and 4. Aspirin's channel
pair-counts are static (C9 O4 H8 → 36/36/72/6/32/28 = 210 total). Per
channel the jet spec is `[(0,0,2),(2,0,2),(4,0,1)]` (dim 21, ✅PROBED P8);
six channels concatenate to `JETPAIR = [(0,0,12),(2,0,12),(4,0,6)]`
(dim 126, ✅PROBED). Assembly into the concatenated array is top-level
uncertified block-major writes at `ml.irreps_offset` positions (the
ml-ops/016 ascribed-literal idiom; NB1a/NB1b both ship the pattern). Feed
vectors in the (y,z,x) order (the store's `pos_*_yzx` copies exist already;
pair differences inherit the permutation).

The ablation's first arm keeps the ORIGINAL per-species position jet
(K = 2,3,4 of the centered per-species position clouds, spec
`[(0,0,2),(1,1,1),(2,0,2),(3,1,1),(4,0,1)]` dim 31/species, 93
concatenated) — expected weak, and that expectation is now a REPORTABLE
measurement, not a hedge.

### 3.2 Specs and arithmetic (probe-confirmed)

`poly_weight_dim(PT, K, sym_spec(PT,K))` = 2 / 2 / 3 for K = 2/3/4; ones
weights give the plain orthonormal decomposition. `ml.gated` under O(3)
ACCEPTS odd l=1/l=3 blocks (✅PROBED P8 — only odd SCALARS are the ml-equiv/008
trap), so the position-jet arm reuses the standard block. Dead-channel trap:
`JETPAIR` has no l=1 content, so the pair-arm's embed target spec must not
carry l=1 blocks at the embed (they would be zero-filled dead weights —
`hom_dim(JETPAIR, [(0,0,6),(1,1,2),(2,0,2)]) = 96` counts only l0/l2 maps);
mid-stack odd l can still arise from cross-copy derive_poly paths if wanted.
Parameter matching to NB1b's 249 is by l=0 multiplicity adjustment, counts
printed per arm (approximate parity + printed counts, never a claim of exact
parity).

### 3.3 Cell arc (~16 cells)

md thesis (MTP/ACE positioning; the NB1b lesson stated plainly) · code:
store + index types + specs + sizing prints · md the parity theorem · code:
per-channel pair tables (static `let rec` index tables), the jet former ⚑
(one featurization cell per arm family, measured EARLY — budget note below),
the order-3 zero pin, the K=2/K=4 l0 moment pins, and the three-route order-2
pin on the position cloud (P3's identity in-notebook: derive_poly mean vs
`sym_to_irreps ∘ ppl.comoments` vs centered `ppl.moments`) · code: jet
introspection (`ml.irreps_len/_l/_mult/_offset` narrated) · md + code: the
certified network (embed `derive_linear` → gated blocks → `derive_poly` K=2
head so l=4 content reaches the invariant readout — a linear head silently
drops it, one markdown sentence) · ONE refusal cell (a raw jet-component
read, BL4008) with pointers to NB1a's full suite · code ⚑⚑ per arm: three
training cells (position-jet / pair-K2 / pair-K2+4), EACH ITS OWN CELL —
each ~NB1b-sized descent stays under the gap-#30 interpreter cliff and gets
its own memo entry; `plot.stream` channels `jet_pos`, `jet_k2`, `jet_k24`
(separate bindings) · code: the comparison table — NB1b's numbers pasted as
literals beside the three arms' computed test MSE/MAE (kcal/mol,
un-standardized via `e_stats`), param counts, featurization + training
wall-clock · code: verification — energy invariance under the store's
rotations for the BEST arm (indicator-sum max, ≤1e-12 pin) + the jet's own
equivariance (rotate positions, recompute the jet, apply the D-matrix... NO:
simpler and stronger, recompute-and-compare the INVARIANT readout, the
NB1b/07 shape) · md + code: Phase-B teaser — one learnable scalar β scaling
the pair vectors upstream of the former, `d(loss)/dβ` by `ad.jvp` with an FD
cross-check (`ad-jvp-comb/079` shape; reverse mode through the former is §5)
· md closing: what the ablation showed, and the §5/§7 road.

### 3.4 Budget

Featurization: ~210 pairs × 500 samples × 2 derive_poly calls ≈ 2×10⁵ small
fixed kernels — fine compiled; interp cost UNKNOWN and measured FIRST (the
builder's step 1; if a featurization cell approaches the cliff, halve it by
splitting per-arm cells or subsetting channels — never by silent sampling).
Training: 3 descents ≈ 3 × ~25 s interp in separate cells. Total notebook
ide-serve drive target: ≤ 2.5 min.

**AS BUILT 2026-08-26** — `examples/aspirin_moment_jet.bladenb` (18 md + 23
code cells; same committed store, byte-untouched). Full ide-serve drive
113.8 s, 22/22 passing cells `lane:"interp"` + `kept:true`, stream frames on
all three channels; canaries green. Every theorem pinned exact: order-3 jet
of the symmetrized pair cloud = 0 exactly (all six channels); K2/K4 l=0
moment pins at 3.2e-12; three-route ratios exactly ±1 per block; invariance
of jets RECOMPUTED from rotated geometries ≤5.5e-15; `ad.jvp` vs FD agree.
Featurization engineering that mattered: channel means as differences of a
rank-2 running-sum prefix table (6 subtractions, not 6 padded folds), arm-2's
descriptor as a baked gather out of arm-3's row, and all `ml.*` elaboration
hoisted ahead of expensive cells (census #40: new elaboration invalidates
the WHOLE session memo — 17 s/cell until reordered). **THE RESULT — §3.5
outcome (c), a loss, reported straight**:

| arm | test MSE | test MAE kcal/mol | params | train wall |
|---|---|---|---|---|
| constant | 1.0905 | 5.486 | 0 | — |
| NB1b histogram+multipoles | 0.892 | 5.057 | 249 | 25.7 s |
| jet_pos (position K=2,3,4) | 1.0505 | 5.373 | 253 | 22.5 s |
| jet_k2 (pair K=2) | 1.0289 | 5.312 | 248 | 14.6 s |
| jet_k24 (pair K=2,4) | 1.0520 | 5.345 | 265 | 18.9 s |

All arms beat the constant floor by 3–6 %; all lose to the histogram; l=4
anisotropy did not pay (inter-arm spread inside 50-step noise). The
unplanned correlation-diagnostic cell makes the loss clean: no pair-jet
feature exceeds |r| = 0.1 against energy; the sum-of-r² linear ceiling is
0.113 (pair) / 0.126 (position) — the models found what was there. The
notebook's stated conclusion: two moments are not a distribution — a
dihedral moves specific contacts while barely moving mean|v|² and mean|v|⁴
of a 36-pair channel, which is exactly the shape NB1b's Gaussian shells
keep.

**RADIAL-WINDOWED ARM BUILT 2026-08-26** (user-directed: first FOUR moments,
each a moment of the sample tensor as a whole). As shipped: 6 channels × 2
Gaussian shells (25th/75th-percentile centres, baked) × K=1..4 — odd orders
on the three HETERO channels only, where species ordering makes v = r_A−r_B
canonical (K=1 = windowed mean bond vector; homo-channel odd moments pinned
exactly zero, 78 slots); `RWSPEC` dim 330; 312 params; one `derive_poly` per
(pair, order) with windows as scalar weights in running-sum tables (712
calls/geometry). Whole-tensor pin: √w-scaled symmetrized O-O cloud through
`ppl.comoments → sym_to_irreps` vs the weighted derive_poly mean, ratio
exactly ±1 per block. **The diagnostic got corrected**: sum-of-r² grows with
feature count, so a shuffled-label null (via the store's own perm table) now
calibrates it — the unwindowed jet's 0.113 ceiling is barely above its own
noise floor (excess ≈0.02), while the windowed jet measures excess 0.23 with
10 features over |r|=0.1 (vs ≤1 under the null): **windowing bought ~10× the
linear signal, before any gradient step**. Result: outcome (b), sharp — best
jet MAE 5.207 (vs 5.057 histogram, 5.31–5.37 other arms) but worst MSE
1.063 and a doubled train/test gap (0.20 vs 0.11): the representation
improved measurably; 50 steps of plain SGD could not collect it — **the
binding constraint moved from representation to optimization** (more steps /
regularization / the §5-unlocked training improvements are the named next
lever). Full drive 201.4 s, 31/31 passing cells interp+kept, four stream
channels live; canaries green.

### 3.5 Honest outcomes

Three publishable endings, all real: (a) pair jets rival or beat the
histogram+multipole baseline — the headline; (b) they land between the
position jets and the baseline — the ablation IS the result (order-2 → +4
monotonicity, l=0-only vs full-jet tells whether anisotropy pays); (c) they
lose — then the certified-descriptor story stands on the theorems (parity
zero, moment pins, refusals) and the wall-clock, and the notebook says so.
The K=4 anisotropy content (l=4 blocks) is the one thing no histogram
carries; whether it pays at N=500 is precisely the experiment.

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
| 27 | BL4003's suggested remedy `(expr : Tag)` does not typecheck for a non-literal Int (BL3001 expected Nat) — only literals cast | BUG (message) | 3-line repro, NB1a build | no (`__`-prefix exemption) | message text at `TypeCheckInfer.fs:3939` / `TypeCheckSupport.fs:1114` |
| 28 | REWRITTEN 2026-08-26 (idiom audit): the originally recorded trigger does NOT reproduce — top-level combinator arrays read from function bodies, kernels, rec arms, and even grad'd bodies all work. The REAL trigger (12-line repro): a function whose body is a `method_for … \|> compute` kernel capturing an ARRAY PARAMETER, calling a helper that reads a MODULE-LEVEL array — the module array's C++ declaration is omitted (raw g++ error, no BL code) | BUG (exact repro) | idiom-audit minimization; bit `rot_flat` in NB1b | NB-soft (one site reverted + annotated) | the main-local capture seam (`fix/kernel-capture-module-binding`) |
| 29 | Differentiable `let rec` requires a LITERAL extent; `let static` from sizing builtins rejected (BL5500) | MF | every slice fn in NB1a | NB-soft (write literals) | Grad/GradExpand rec-array arm |
| 30 | BL8005 step-budget exhaustion does NOT trigger the g++ fallback (`Interp/Repl.fs:132,147` route only 125/70); `MaxSteps` hard-coded | BUG/MF | measured, NB1a build | **NB1b-hard** (cells must be sized to interp) | `Interp/Repl.fs` classification, or settable `MaxSteps` |
| 31 | Scalar × array AND array + array elementwise inside a reverse-differentiated body: BL7004 kernel-body refusal | MF/WE | NB1a `scores_of`; NB1b probe pr3 | no (componentwise literals) | CodeGen kernel-body arm / `GradExpand.fs` |
| 32 | `sqrt`/division inside `ml.equiv` bodies leaves the polynomial fragment → component reads then BL4008; normalised pseudoscalars unwritable with reads | BD | NB1a probe | no | doc note in equivariant-nn.md |
| 33 | Braceless function body cannot continue on a `+`-led line (BL1999) | BD/quirk | NB1a build | no | doc |
| 34 | BL4014 galilean suggester fires on plain squared-difference and distance-of-two-positions helpers — every bond-length notebook collects warnings | WE (noise) | NB1a + NB1b builds | no | suggester scoping (skip non-velocity params) |
| 35 | No certified scalar×irreps rescale: `x(i) * d` with `d` from `ml.norms` is BL4008 under a clause — RMS block normalization inexpressible in certified bodies | MF | NB1b probe pr8 | NB-soft (fold scale into bilinear weights) | new `ml.scale(SPEC, s, x)` |
| 36 | `let rec` with an `IrrepsIdx<SPEC>` leading axis refused in grad'd code (BL5500 demands literal `Idx<n>`) — irreps values need array-literal assembly | MF | NB1b probe pr6 | NB-soft | Grad rec-array arm (same seam as #29) |
| 37 | `f(args)(i)` — indexing a call result — inside a differentiated body: BL5500 "only named calls and array reads" | WE | NB1b `readout` | no (bind, then index) | `GradExpand.fs` |
| 38 | `let rec` of `Idx<22500>` SEGFAULTS the compiled lane (exit 139, no diagnostic) | BUG | NB1b build | no (rank-2 rec arrays) | codegen stack/alloc |
| 39 | `Int` param in Int64 arithmetic needs explicit `Int64(q)` (BL3001) — the two do not unify at that seam | BUG/quirk | NB1b `tri_j` | no | TypeCheck numeric unify |

| 40 | A cell triggering NEW `ml.*` elaboration invalidates the WHOLE interpreter session memo (17 s/cell measured); `ad.grad` elaboration does not | BUG/perf | NB2 probe bisection | NB-soft (order all `ml.*` elaboration before expensive cells) | `Interp/Run.fs` memo restore vs `ReplSession.fs` prefix rule + MLElaborate generated decls |
| 41 | `ppl.comoments`/`ml.sym_to_irreps` take Cartesian (x,y,z) while `ml.derive_poly` takes (y,z,x): same array to both → l=0 agrees, l=2 comes out rotated | BD (undocumented trap) | NB2 s8/s9 probes | no (one explicit permutation) | doc at the ops / equivariant-nn.md |
| 42 | Nested `if…else (if…)` with declared `Int` return: inner arms default Int64, BL3001 | BUG/quirk (family of #39) | NB2 3-line repro | no | TypeCheck numeric unify |
| 43 | `ad.jvp` refuses rank-2 recursive arrays (BL5501 rank-1-only) — running-sum tables not forward-differentiable | MF | NB2 Phase-B teaser | NB2-soft | Grad rec-array arm (seam of #29/#36) |
| 44 | `reduce(A * B, (+))` in expression position: interpreter ACCEPTS, codegen refuses BL7004 — silent lane divergence for interp-only notebooks | BUG | NB2 `signal_ceiling` | no (bind each reduce) | CodeGen deferred-reduce arm, or make interp refuse identically |
| 45 | `plot.line` is single-series; overlays need N figures | MF | NB2 build | no | `stdlib/plot.blade` multi-series factory |
| 46 | Multi-line array literal whose `[` opens on the line after `=` is BL1999 | BD/quirk (family of #33) | NB2 `__pi`; re-hit in the windowed arm | no | doc |
| 47 | `ide serve` interleaves live `{"event":"display"}` lines with eval responses on one stdout stream; a one-line-per-request client desynchronises silently and blocks | BD/trap | cost a 10-min hang in the windowed-arm drive harness | no (loop until a non-event line) | doc the read loop in display-frames.md §3 |
| 48 | Notebook eval responses give the cell-result binding an empty `name` and truncate `value` past ~5 elements — automated drives cannot read full printed values from the protocol | MF | raw response inspected | no (verify via `blade run`) | `IdeServe.handleEval` binding renderer |
| 49 | Sizing builtins outside `let static` position leak an internal name: `BL2001: Unbound variable: __ml_stat_total_dim` | BUG (message) | 1-line repro | no (use `let static`) | `MLElaborate` static-op arm |
| 50 | No combinator expresses SEGMENTED assembly: `join` concatenates whole arrays only (segments-at-offsets need a per-segment `subset`; permutation gathers aren't concatenation). Sub-item: heterogeneous-extent `join` WORKS but has zero corpus coverage — a `stack-join/014` pin is owed | MF + missing pin | idiom audit probes | no | `subset`-then-`join` sugar, or a segmented `join` form; corpus pin |
| 51 | No combinator can produce an `IrrepsIdx`-typed value (operands fenced to plain slots, results hard-code plain/`__seq` indices) — every irreps assembly is an annotated array literal | MF | `TypeCheckInfer.fs:3446/3517/4227` | NB-soft | an `ml.assemble(SPEC, …)` producer, or `join` inheriting an irreps operand's index record |
| 52 | `replicate(1, pure(x)) \|> compute` collapses to a SCALAR, not a 1-element array; `join` then rejects it BL4004 | BUG/trap | idiom audit probe | no | replicate extent-1 arm |

Confirmed-working (recorded for NB2's builder): rank-2 `let rec` reads with
computed indices inside grad'd bodies; Int64 gathers (rank 1 and 2) inside
grad; nested `let rec` + `reduce` inside grad; `ad.grad` called from inside a
plain function, so a rec array can drive repeated force evaluations. Also
measured, unattributed: a plain interpreted forward pass costs ~6× a
grad-expanded forward+backward per evaluation (~130 ms vs ~22 ms in NB2's
cells 12/13) — the grad-expanded lane is the fast one.

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
4. ✅ **NB1a built + verified 2026-08-26** (see §2a AS BUILT). ✅ **NB1b
   built + verified 2026-08-26** (see §2b AS BUILT; full-stack, 25.7 s
   interp — inside the gap-#30 cliff).
5. ✅ **NB2 built + verified 2026-08-26** (see §3 AS BUILT; outcome (c) with
   the correlation diagnostic making the loss clean; census grown to 46).
6. **Reverse-mode combinator AD (§5)** — next; unlocks NB2 Phase B as a real
   trained arm rather than a jvp teaser.
7. ✅ **Radial-windowed pair jets built 2026-08-26** (§3 AS BUILT): 10× the
   null-excess linear signal; best jet MAE; the constraint is now
   optimization, not representation — §5 and longer/regularized training
   are the levers.
8. ✅ **Idiom audit of NB1b done 2026-08-26**: 64 `let rec` → 23 (41
   rewritten to loop objects, 22 blocked with in-file gap citations, 1
   genuine recurrence kept); the 499-slot `sgd_step` assembly is now
   heterogeneous-extent `join`; stdout byte-identical; census #28 rewritten
   with the exact minimized trigger; #50–#52 filed. Follow-ups owed: the
   same sweep over tetris (26 index-driven sites) and the moment-jet
   notebook (~197, incl. 13 running-sum scans to re-examine), the
   `stack-join/014` heterogeneous-join corpus pin, and a CLAUDE.md
   style-table addition (proposed text in the audit report).
9. Revisit: outputs persistence decision (#9), `ml.rotate` (#14), the memo
   bug (#40) and the interp/codegen divergence (#44) as the notebook-UX
   bugfixes worth doing first (delegated).

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
