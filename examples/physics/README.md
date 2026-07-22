# Physics with moment jets

These examples treat physical **values and parameters as moment jets** (`Dist`)
rather than single numbers, and push them through ordinary physics with
`dist_map` (the Faà-di-Bruno pushforward). A value carries not just a number
but its variance, skew, and higher structure, and the jet *degree* is an
explicit accuracy dial. Every example is pinned with `EXPECT` values checked
against an independent PowerShell ensemble (transform the samples, take
empirical cumulants).

## The progression

| File | Idea |
|------|------|
| `01_projectile_range` | Higher-order delta method: the naive "plug in the means" range is **biased**; the tower gives the bias, variance, and skew. |
| `02_pendulum_period`  | The freshman error-propagation formula is the *degree-1 slice*; degree 2 adds the concavity bias (the pendulum runs fast on average). |
| `03_clock_amplitude_drift` | For a **polynomial** map the pushforward is exact: amplitude jitter drifts a clock a precisely-known 42.9 s/day. |
| `04_energy_conservation` | Conservation holds **distribution-wise**: the energy tower is invariant under time evolution while the coordinate covariance rotates. |
| `05_ensemble_dephasing` | Classical decoherence; the jet degree bounds how far into the decay the description stays valid, and *diverges* when it fails. |
| `06_collision_fuzzy_mass` | Elastic collision, fuzzy target mass. Momentum/energy come out as **degenerate towers** — conservation as a correlation constraint. |
| `07_collision_com_frame` | Both masses fuzzy: all uncertainty localizes to one scalar jet (the COM velocity); the relative velocity stays sharp. Uncertainty lives in the *frame*. |
| `08_collision_self_mass` | We are the fuzzy object: 63% uncertain of our own recoil energy, yet exactly certain of our motion relative to the target. |
| `09_invariant_detector_collision` | The **invariant detector** (validation): fuzz a parameter, and quantities whose towers refuse to spread are the invariants. Recovers relative velocity, momentum, energy. |
| `10_invariant_detector_kepler` | Detector (discovery): fuzzing the true anomaly recovers energy, angular momentum, and the **hidden Laplace–Runge–Lenz vector**, while rejecting speed. |
| `11_invariant_discovery` | Verifies a *generated* invariant: the combination `tools/gen_invariants.ps1` discovered, confirmed degenerate by the tower. |
| `12_invariant_detector_oscillator` | The detector on a second system: fuzzing the 2D oscillator's phase recovers energy, angular momentum, and the Fradkin tensor. |
| `13_symmetry_breaking_precession` | The detector as a symmetry-breaking meter: a precessing orbit keeps energy and angular momentum sharp but the Laplace–Runge–Lenz vector spreads. |
| `14_chaos_no_second_invariant` | The honest negative: on real trajectory data, an integrable quartic keeps a second invariant while chaotic Hénon–Heiles keeps only energy. |
| `15_lagged_cumulant_former` | The lagged-cumulant former built from the tower: C(τ) = Cov(x(t), x(t+τ)) of a sine recovers its frequency (autocovariance = spectrum by cross-covariance theorem). |
| `16_galilean_boost_tenth_invariant` | Conservation laws **linear in time**: G = m₁x₁ + m₂x₂ − Pt, invisible to every state-only detector, verified sharp by fused zip/reduce towers while X_cm spreads. |
| `17_spectral_persistence_torus_vs_chaos` | Regular vs chaotic Hénon–Heiles through one `cumulants` call on strided pair samples: discrete spectrum persists (torus), continuous decays (mixing). |
| `18_bohr_spectroscopy` | Quantum ⟨x⟩(t) towers: the harmonic ladder is a **rank-1** Hankel matrix (one Bohr gap), anharmonicity splits it; in-language Prony + Newton-arccos reads the gaps. |
| `19_kramers_moyal_pawula` | Stochastic generators: drift/diffusion are conditional increment cumulants, and κ₄/κ₂² is the **Pawula verdict** — jet order 2 (diffusion) vs ∞ (jumps). |
| `20_fuzzy_geometry_contact` | Known density leaks the mass jet into **geometry**: fuzzy radii shift the mean contact plane (concavity bias) and anti-correlate timing with recoil — a 33% variance credit no independent-error budget sees. |
| `21_collision_time_ambiguity` | Both radii fuzzy: the COM tower **cannot tell the collision happened** (Noether immunity), while "has it happened yet?" becomes a fuzzy proposition — a skew spike exactly inside the contact band. |
| `22_event_order_tower` | The **order** of collisions is a distribution on S₃: three fuzzy-radius balls, the first A–B and B–C contacts a photo finish. Pairwise order probabilities are the first moments; the causal skeleton (BC2 always last, AB1 vs BC1 the coin flip) is the invariant detector applied to the causal relation; the witness W = 1.368 sits inside the classical bound [1,2]. |
| `23_bell_for_temporal_order` | **Both sides of the divide** (ZCPB-style): the same race statistics η wired into a mixture of orders vs a coherent superposition of orders — classical CHSH pinned at S = 2 for every η, coherent S = 2√(1+4η(1−η)) → 2√2 at the tie. At the tie, 99.8% of the downstream variance is pure which-order uncertainty. |
| `24_sampling_identity_swap` | **The collision that wasn't**: fast fuzzy-mass balls observed stroboscopically — bounce and pass-through worlds differ by D = 20\|Δm\|/M, which vanishes at equal masses (event *existence* becomes gauge); sampling slower than the crossing window, the event fades from the record (P ≈ 0.52). |
| `25_envelopment_rind` | **The rind**: a ball whose size is known to 2 decades can sit inside the *mean* ball, wrapped in its own shell of possible surfaces — never inside the actual one. Exclusion is a correlation law (joint overlap 0, marginals 0.31), and the decorrelated gap tower *fails the Stieltjes test* from three moments. |
| `26_causal_chain` | Eight balls, two cascades: several concurrent order races (a **multivariate** order tower), exact spacelike factorization outside the causal diamond (cov = 0 to the bit), coupling inside, a cone edge that disperses with depth, and ~1 bit of causal entropy per collision. |
| `27_merged_mass_probe` | Probe a "merged" fuzzy pair and ask its recoil what it hit: never the sum (1.45 vs 2.16), never one ball (vs 1.08) — an unbound pair is a **cascade**, the answer is a tower, and the probe re-measures the which-is-where bit the merged record erased. |
| `28_rough_spin_collision` | Rotation enters, Blade-only: glancing fuzzy contact makes the trans/rot **partition** variant while total J stays exact (conservation towers are the in-file oracle, ~1e-31); collisions manufacture spin-orbit correlation; and spin is a **hidden register** — silent until touch, then near-invisible in the angle but tripling the speed. |

## The invariant discovery engine

`tools/gen_invariants.ps1` is candidate **generation**. It enumerates every
monomial in the state variables up to a degree, forms the dictionary's
covariance under the fuzzed parameter (an order-2 moment tower), and reads the
conserved quantities off as the **null space** of that covariance — a
zero-variance combination is an invariant. It intersects across several orbits
so only genuine constants of motion survive (orbit-specific identities like the
velocity hodograph drop out). With no conjectures supplied it returns, for the
Kepler problem:

- **degree 2:** angular momentum `x vy − y vx` and energy `½vx² + ½vy² − 1/r`;
- **degree 3:** those plus both components of the **Laplace–Runge–Lenz vector**
  (`vy·Lz − x/r` and `−vx·Lz − y/r`) — the hidden invariant behind Kepler's
  SO(4) symmetry.

The one-line idea: **variance is a measure of non-invariance, so a
representation built from variances (the moment tower) is a native detector of
invariants — and invariants are what physics is built from.**

## Symmetry group recovery

`tools/group_recovery.ps1` goes one level higher: it recovers the symmetry
**group**, not just individual invariants. Each conserved quantity generates a
symmetry flow (Noether), and how those flows fail to commute — the
**Poisson-bracket algebra** `{Q_i, Q_j}` — is the Lie algebra of the group. The
tool computes the brackets (derivatives by finite difference, so it works on
any invariant the detector returns) and fits each back into the invariant
basis; the fit coefficients are the structure constants. It recovers:

- **Kepler (planar):** `{Lz,Ax}=Ay`, `{Lz,Ay}=−Ax`, `{Ax,Ay}=−2 E·Lz` → on the
  bound shell, **so(3)** (the 3-D problem lifts this to the famous SO(4));
- **2D oscillator:** `{Lz,T1}=2T2`, `{Lz,T2}=−2T1`, `{T1,T2}=2Lz` → **su(2) ≅
  so(3)**, the hidden symmetry behind the oscillator's degeneracy.

So the pipeline is end to end: fuzz a parameter → the towers that stay sharp
are the conserved quantities → their bracket algebra is the symmetry group.

## Symmetry breaking as a meter

`tools/symmetry_breaking.ps1` runs the detector in reverse: on a system where a
symmetry is *broken*, it measures how badly. Perturbing Kepler by `−β/r²` keeps
the force central and time-independent, so energy and angular momentum survive
exactly, but the orbit precesses and the Laplace–Runge–Lenz vector is no longer
conserved. Sweeping β shows E_pert and Lz pinned at ~1e-16 for every β, while
the LRL dispersion grows **linearly in β** and collapses to machine zero as
β → 0 — recovering the exact Kepler invariants. The detector reads out not just
*which* symmetry broke but *how much*, and `13_symmetry_breaking_precession`
verifies the same split through the Blade tower.

## The real test: unknown invariants, including chaos

`tools/unknown_invariants.ps1` points the discovery engine at systems whose
invariants are *not* known in advance — real trajectories, integrated
symplectically, with no closed form. Over several trajectories (centered per
trajectory, so only genuine constants of motion survive) it takes the null
space of a degree-4 monomial dictionary and reports the count:

- **separable quartic** `½p² + ½r² + λ(x⁴+y⁴)` (integrable) → **2** constants of
  motion: energy *and* a second invariant (`E_x − E_y`);
- **Hénon–Heiles** `½p² + ½r² + (x²y − y³/3)` (chaotic, E ≈ 0.127) → **1**:
  energy, and nothing else.

That negative result is the point: pointed at a chaotic system, the tower
honestly reports that no second polynomial invariant exists.
`14_chaos_no_second_invariant` confirms the split through the Blade tower on the
trajectory data.

## Non-polynomial invariants, and equation discovery

The polynomial null space is blind to non-polynomial invariants — but the fix
is a richer *dictionary*, not deeper machinery. `15_lagged_cumulant_former`
builds the lagged cumulant `C(τ) = Cov(x(t), x(t+τ))` from the existing tower
(the cross term of `cumulants(·,2)` on the signal paired with its shift); by the
cross-covariance theorem its Fourier transform is the power spectrum, and for a
sine it recovers the frequency. `tools/nonpoly_discovery.ps1` then shows the
payoff on the pendulum `x'' = −sin x`, whose energy `½x'² − cos x` is
non-polynomial: a polynomial dictionary finds **0** exact invariants, but adding
the atoms `cos x, sin x` makes the same zero-variance null space return
`½x'² − cos x` **as a formula**. Choosing which atoms to add is exactly the
SINDy / equation-discovery step — invariant discovery is the eigenvalue-1 slice
of the Koopman operator whose full spectrum is the dynamics.

`tools/koopman_edmd.ps1` closes that loop. It fits the Koopman generator L from
trajectory data (regressing finite-difference derivatives of a dictionary onto
the dictionary), then reads **both** answers out of the *same* operator: the
coordinate rows of L give the equations of motion, and the left null space of L
(vectors `c` with `cᵀL = 0`) gives the conserved quantities. On the pendulum it
recovers `ẋ = p, ṗ = −sin x` **and** `½p² − cos x`; on the Duffing oscillator
`ẋ = p, ṗ = −x − x³` **and** `½p² + ½x² + ¼x⁴` — the law and the invariant, from
one decomposition, for a non-polynomial and a polynomial system.

## Honest bookkeeping: the count is a ring, and zero-mean invariants

Two corrections baked into the tools and examples:

- **Nullity is not the number of invariants.** Products of invariants are
  invariants, so the null-space dimension inflates as soon as the dictionary
  degree admits them — on Duffing, a degree-8 dictionary holds both E and E²
  (nullity 2) though the system has exactly one constant of motion.
  `tools/unknown_invariants.ps1` therefore reports both the nullity *and* the
  number of **functionally independent generators** (the Jacobian rank of the
  null-space combinations at generic points): ∇(E²) = 2E∇E is parallel to ∇E
  everywhere, so the generator count stays 1 while the nullity climbs. The
  degree-4 counts in `14` were safe only because the generators have degree
  3–4, keeping their products above the cap.
- **Zero-mean invariants and the dispersion floor.** The relative dispersion
  divides by |κ₁|, so an invariant whose value is zero (LRL_y on an orbit
  oriented along x) reads nine orders worse than its peers — a normalization
  artifact. All detector verdicts now accept absolute degeneracy first; see
  the note in `10_invariant_detector_kepler`.

## The Jordan block: conservation laws linear in time

`tools/galilean_boost.ps1` opens the sector every state-only detector misses.
A two-body system conserves `G = m₁x₁ + m₂x₂ − Pt` — the Galilean-boost /
center-of-mass law, sibling of the N-body "ten first integrals". G depends on
*time*, so no dictionary of phase-space functions contains it; it hides in the
**structure** of the fitted generator: `L(m₁x₁ + m₂x₂) = P, LP = 0` — the
eigenvalue-0 sector is *not diagonalizable*, and `ker(L²)/ker(L)` returns the
Jordan chain. Three payoffs, all verified on three mass configurations:

- the chain, normalized so its image is P in the data's canonical momentum
  units, is exactly `(m₁, 0, m₂, 0)`: **the fitted generator's Jordan
  structure returns the masses** from kinematics alone;
- `G = chain − Pt` is conserved to 1e-13 along every trajectory while X_cm
  spreads (example `16` verifies this through fused zip/reduce towers, with
  time embedded as an ordinary data column);
- the Poisson bracket of the discovered laws is a **number**, not a function:
  `{G, P} = m₁ + m₂` at every phase-space point. The Galilei algebra closes
  only up to a central term, and the central charge *is* the total mass
  (Bargmann). The pipeline weighs the system from its symmetry algebra.

## The spectrum is a moment problem

`tools/spectral_quadrature.ps1` makes the polyspectra thread exact. Since
cos(nθ) = Tₙ(cos θ), the lagged-cumulant sequence C(nΔ) = Σ wₖcos(ωₖnΔ) is the
**Chebyshev-moment tower** of a measure with atoms at cos(ωₖΔ) — so frequency
extraction is the classical truncated moment problem: Wheeler's algorithm
turns the tower into Jacobi coefficients, β-termination counts the spectral
lines (finite = torus, alias-immune; never = chaos), and Golub–Welsch nodes
*are* the frequencies, weights the line powers. No FFT, no bins, no leakage.
On the quartic 2-torus it recovers both fundamentals (1.20, 1.04 vs
zero-crossing references 1.194, 1.013) — frequency-module rank 2 = 2 actions,
matching the generator count. Pointed at **quantum** expectation data ⟨x⟩(t),
the quadrature nodes are the **Bohr transition energies**: 13 of 15
participating lines of an anharmonic oscillator come back node-by-node with
intensities (isolated lines to machine precision), and the harmonic ladder
collapses to a **rank-1** spectral measure (β₁ ~ 1e-16) — all gaps equal,
which is simultaneously why classical SHM has one frequency. Anharmonicity is
the Hankel rank climbing above 1. Example `18` runs the moment arithmetic
(rank verdicts 13 orders apart, 2-atom Prony, Newton-arccos) in-language;
example `17` gets the torus-vs-chaos persistence verdict from real
Hénon–Heiles trajectories through one former call — including the honest
subtlety that at E ≈ 0.13 many "chaotic-energy" trajectories are sticky, so
the mixing IC (E ≈ 0.153) was verified to decay.

## Stochastic dynamics: the generator is the jet of the propagator

`tools/kramers_moyal.ps1` extends the arc to noise. The Kramers–Moyal
coefficients are the **conditional cumulants of increments** — drift is κ₁/τ,
diffusion κ₂/τ — so the tower formers estimate stochastic generators directly
from data (the state-resolved read recovers drift −x and diffusion 0.25 for
both test processes). Pawula's theorem then says the propagator's cumulant
tower truncates at order 1, at order 2, or **not at all**: the witness is
κ₄/κ₂², identically 0 for a diffusion but 3/(ρτ) for a jump process,
*diverging* as τ → 0 (measured 119.1, 58.9, 22.5, 10.8, 4.9 against predicted
120, 60, 24, 12, 6). Example `19`: two processes built to agree exactly
through order 2 separate by a factor of 700 at order 4 — the first result in
the arc that structurally *needs* the tower above κ₂.

## Fuzzy geometry: WHERE and WHEN inherit the mass jet

`tools/fuzzy_geometry.ps1` closes an inconsistency in the collision examples:
if the density is known, a fuzzy mass *is* a fuzzy volume (R = m^⅓ in natural
units), so the collision's geometry — contact time `t* = (d − R₁ − R₂)/u₁`
and contact point `X_c = d − R₂` (the target's size only; the projectile's
never enters) — inherits the mass tower through a cube root. Four findings:

- **geometric bias**: R(m) is concave, so mass variance pulls the mean
  contact plane −Var(m)/9 shy of the naive answer — sign-definite, and the
  degree-3 jet in `20` reproduces the leading-order term exactly;
- **the correlation credit**: a heavier target both recoils slower *and*
  makes contact earlier, so the timing and velocity channels of
  `x₂(T) = d + v₂(T − t*)` anti-cooperate: the naive channel-sum error
  budget overstates Var(x₂) by **33%**. Geometry and dynamics stop being
  independent error sources the moment density ties them together, and the
  joint jet collects the credit automatically;
- **Noether immunity**: the COM moves uniformly through the event no matter
  when or where contact happened — per realization, X_cm(T) equals its
  free-flight prediction to machine precision. The invariant sector cannot
  tell that a collision occurred *at all*; the boost law of `16` survives
  the entire rabbit hole untouched;
- **temporal-ordering ambiguity**: observed *inside* the fuzzy contact band,
  "has it happened yet?" has no definite answer, and the tower says so — a
  skew/kurtosis spike in T (skew 4.7, excess kurtosis 20 at the band edge,
  ~0 outside): the bifurcation detector of `06`, firing along the time axis.
  With chains of fuzzy-sized balls, the *order* of events itself becomes a
  distribution the tower carries natively.

## The event-order tower

`tools/order_tower.ps1` carries out the closing outlook of examples 20–21:
with a chain of fuzzy-sized balls the **order of collisions** is itself a
distribution the tower represents natively. Three balls on a line — A
(projectile), a **light** middle ball B, a heavier faster C incoming — radii
tied to mass by the known density R = m^⅓, masses a 5×5×5 epistemic grid. The
geometry is tuned so the first A–B contact (**AB1**) and the first B–C contact
(**BC1**) are a genuine **photo finish** — mean times 5.2425 vs 5.2383, their
fuzzy bands overlapping — while the light B rattles and C strikes it a second
time (**BC2**). The three universal events E1 = AB1, E2 = BC1, E3 = BC2 carry a
measure on the six permutations of S₃ (here supported on two: `AB1<BC1<BC2`
with weight 0.368 and `BC1<AB1<BC2` with 0.632), and its moment tower is:

- **P-matrix (first moments).** P(AB1<BC1) = 0.368, P(BC1<BC2) = 1,
  P(AB1<BC2) = 1 — the coordinates of the linear-ordering polytope.
- **Causal skeleton.** Two relations are **degenerate** (P = 0 or 1): BC2 is
  always last, so the almost-sure partial order is `AB1 < BC2` and `BC1 < BC2`.
  The one **ambiguous** relation is the AB1-vs-BC1 coin flip (P = 0.368),
  dynamically controlled — whoever reaches B first is set by whether A or C is
  the larger ball. The skeleton is the invariant detector of examples 09–16
  pointed at the causal relation itself: the order propositions whose towers
  refuse to spread are the invariant sector.
- **Polytope facets = causal inequalities.** The two definite relations sit
  exactly **on** the trivial faces P = 1 (the poset lives on the polytope
  boundary); the triangle / 3-cycle facets 0 ≤ P_ij + P_jk − P_ik ≤ 1 are
  satisfied with margin **0.368** — strictly inside the causal inequalities.
- **Causal witness.** W = P(E1<E2) + P(E2<E3) + P(E3<E1) = **1.368**, inside
  the classical bound **[1, 2]**. Any single definite order gives W = 1 or 2;
  a convex mixture stays between. W outside [1, 2] would be causal-inequality-
  violating — noncausal, unreachable even by a quantum switch. This is a
  causally **separable** process (a dynamically-controlled classical mixture
  of orders, the QC-CC class): the classical stratum below the switch.

This is the n = 3 germ of the Sₙ ordering moment problem; example 21's
two-event skew spike ("has it happened yet?") is its n = 2 marginal. Example
`22` recomputes the P-matrix, the triangle facet, and the witness in-language
from the emitted order-indicator columns.

## Bell's theorem for temporal order: both sides of the divide

`tools/temporal_bell.ps1` puts the fuzzy billiards on one side of the
Zych–Costa–Pikovski–Brukner construction and the quantum switch on the other.
Sliding ball C's start position sweeps the two first-contact dispersions from
disjoint to exact overlap: η = P(AB first) runs 1 → ½, and at the tie the
order entropy, the causal-inequality margins, *and* the downstream
branch-mixture variance all peak (Var of B's post-collision velocity spikes
600×; 99.8% of it is which-order uncertainty). Three tie-adjacent notions
must not be confused: the **ensemble tie** (η = ½ — the *center* of the
classical order polytope, maximally far from any causal-inequality
violation; what fails there is any definite causal *narrative*, wrong half
the time), the **ontic tie** (t₁ = t₂ exactly — a genuine triple collision
where hard-sphere dynamics is singular; a measure-zero surface struck 25/125
by a symmetric grid and never by the asymmetric sweep, so every *observable*
tie is an ensemble tie), and a **violation** (statistics outside the
polytope, unreachable by any mixture). Wiring the *same* η into the ZCPB
pair construction — mixture ρ = η|++⟩⟨++| + (1−η)|−−⟩⟨−−| versus coherent
√η|++⟩ + √(1−η)|−−⟩, identical order marginals — the Horodecki CHSH maximum
says: classical mixtures sit **at** the separable bound S = 2 for *every* η,
while coherent order gives S = 2√(1+4η(1−η)), maximal (2√2) exactly at the
tie. The tie is where the divide is widest: maximum classical safety and
maximum quantum order-entanglement, on identical marginals. Locally the two
sides are indistinguishable (each wing is mixed; the single-switch witness
D = −1 vs 0 needs the coherent control in hand) — the nonclassicality of
causal order lives only in correlations, which is why it takes a Bell test.
`23_bell_for_temporal_order` computes the classical side from embedded race
data and both witness values from the same η, in-language.

## The rind: inside a ball that might be there

`tools/envelopment.ps1` + example `25`: mass ratio 1000, the small ball's
mass known only to two orders of magnitude — so the band of possible contact
surfaces (the **rind**, built from the small ball's *own* size uncertainty)
is wider than the ball itself. Per realization the gap is always ≥ 0: joint
overlap probability exactly 0. Decorrelate position from size (the
product-of-marginals any independent-error treatment uses) and the forbidden
region revives: overlap 15/49. **Impenetrability is a correlation law**,
invisible in marginals — and the tower can prove a description illegal:
a gap law must be a Stieltjes moment sequence (support [0,∞)); the joint and
decorrelated towers share the same mean, but the shifted Hankel m₁m₃−m₂² is
+0.086 (joint) vs −1.485 (decorrelated) — not a hard-sphere world, from
three moments. "Inside, in some sense" resolves to: inside the *mean* ball
(P = 4/7), fully below the outermost possible surface (P = 4/7), inside the
*actual* ball never. The ensemble of hard turning points is a diffuse
sigmoid — the classical shadow of the optical-model surface diffuseness.
Quantum contrast: tunneling is *joint* leakage into the forbidden region;
classical fuzz only ever leaks *marginally* — the Stieltjes test on the
jointly-measured gap is the discriminator.

## The collision that wasn't: sampling, identity, and gauge

Example `24`: both masses drawn from the same overlapping jet, fast head-on
approach, stroboscopic observation. Across a frame gap containing the
collision, the record admits two worlds — bounce, or pass-through with
swapped identities — whose unordered velocity records differ by
D = 20|m_A−m_B|/(m_A+m_B): **zero at equal masses**, where "did they
collide?" loses all observable content (event *existence* becomes gauge, as
order did at the tie). The classical skeleton of quantum
indistinguishability: which-trajectory propositions stop being certifiable,
and the surviving record is the label-free, symmetrized sector. Sampling
enters as a **Nyquist rate for events**: one frame inside the pass-through
crossing window w = 2(R_A+R_B)/v_rel decides the question with ball-scale
position evidence; sampled slower, the event evaporates from the record with
probability 1 − w/Δ (measured: P_undecidable = 0 at Δ = 0.1, 0.44 at Δ = 1,
0.52 at Δ = 4). At the gauge point one discriminator survives — one contact
diameter of position offset — so sub-diameter imaging decides where
velocities cannot.

## The causal chain: factorization, coupling, entropy

`tools/causal_chain.ps1` + example `26`: eight balls, two cascades driven
inward, six fuzzy masses on a true product grid (729 exact event-driven
cascades, 1.4 s — the event iteration is inherently sequential; every
ensemble statistic is computed in Blade). What emerges at this scale:
several *concurrent* ambiguous races (the S_n measure grows a second-moment
sector — three balls had a single lonely Bernoulli), **exact spacelike
factorization** (over the embedded product sub-grid the left and right race
bits have covariance 0 to the bit — the tower factorizes outside the causal
diamond — while the post-merge bit couples to both sides), a collision-front
cone whose edge *disperses* with depth (std 0.04 → 0.25 → 0.33), and
**causal entropy production**: itinerary prefix entropy grows 0 → 6.35 bits
over twelve events (162 distinct causal histories from six 3-point jets),
with the event *count* itself fuzzy. One subtlety the full grid exposes: at
wider mass fuzz the races grow timelike tails and the "spacelike" covariance
comes back nonzero (−0.005) — the causal diamond is a property of the
epistemic state, not of the configuration.

## The merged-pair probe: how much mass does the third ball encounter?

`tools/merged_probe.ps1` + example `27`: a probe striking a close pair of
fuzzy-mass balls never encounters "a mass" — it encounters a **cascade**.
The recoil-inferred mass E[m_eff] = 1.45 sits between the single-hit model
(the near ball, 1.08) and the rigid-composite model (m_A + m_B = 2.16),
0.71 away from the sum: an unbound pair weighs as its sum only for momentum
bookkeeping, never for kinematic inversion. The cascade branch is fuzzy
(44% of realizations rattle twice), so the answer is a tower whose variance
carries the branch structure — the classical mixture where quantum
scattering off two centers would put path *interference*. And the probe
**re-sharpens merged identity**: heavy-near vs light-near sub-ensembles
report different effective masses (1.40 vs 1.64 — a light near ball
mediates *more* of the far ball's inertia into the probe), recovering the
which-is-where bit the merged record had erased. A collision is a
measurement, and it can measure what the record forgot.

## Rough spin: the hidden register

Example `28` (the first fully Blade-only experiment: closed-form rough-disk
collision staged through `|> compute` kernel columns, embedded literals, and
**conservation as the oracle** — total momentum, energy, and angular momentum
deviation towers pinned at ~1e-31, plus an in-file analytic limit test).
Rotation changes the fuzzy world in four ways: (1) total J joins the
invariant sector but the **translational/rotational partition becomes
variant** — glancing fuzzy contact opens an energy channel the smooth arc
never had; (2) collisions **manufacture spin-orbit correlation** from
spinless initial data (Cov(ω₂′, sinθ₁) = 0.039) — law-bearing structure in
cross-cumulants again; (3) spin is a **hidden internal register**: it never
touches the trajectory between contacts (identical records until touch),
and at contact it is nearly invisible in the deflection *angle* (gap 0.015 —
near equal masses the direction cancels it) while nearly **tripling** the
probe's retained *speed* (0.39 → 1.05): which observables an internal
register couples to is itself structured; (4) deflection angles live on the
**circle** — their tower is trigonometric moments with Toeplitz (not Hankel)
realizability, the third moment-problem geometry in the arc, with the
resultant length ρ = 0.944 as the angular-dephasing meter.

## Fishing for q: does the classical world deform its partition lattice?

The q-deformation question (Bożejko–Speicher: weight pair-partitions by
q^crossings; q = 1 classical Wick, q = 0 free/non-crossing) cast as two
Blade-only experiments — one at the dynamics, one at the observer.

Example `29` (dynamics; no external data — the 3^6 = 729 ensemble is built
in-language from `range` + `floor` digits): a 7-ball fixed-itinerary cradle
with alternating wide/tight mass jets, observables = the transmitted front
velocities X₁..X₆, and for each of the 15 time-ordered quadruples the
connected 4-cumulant and its attribution to the crossing (interleaved,
"out-of-time-order") pairing channel. **All fifteen κ₄ are negative — but
it is not crossing suppression.** The would-be deformation constant spans
q̂ = −0.37..+5.50 with sign changes (near-null *anticorrelated* covariance
channels: consecutive collisions share a mass whose two roles partially
cancel), 2/5 of the clean quadruples land *below free* (the deficit exceeds
the entire crossing product — no q ∈ [0,1] can produce it), and the
cross/nested normalizers never separate (ratios 0.97..1.03: multiplicative
cascades nearly factorize their covariance, so the channel question is
dynamically ill-posed). The control nails the mechanism: reweight the SAME
grid from platykurtic (digit kurtosis −2/3) to leptokurtic jets (+1/12) and
**all fifteen κ₄ flip positive** — kurtosis in, kurtosis out. Verdict:
`INHERITED_NOT_DYNAMICAL`. Classical few-body dynamics transports the
input tower's shape; it does not deform partitions. (In quantum chaos,
crossing-suppressed correlators — the free structure of ETH, OTOC decay —
are a hallmark of the *dynamics*; classically that signature simply is not
available without phases. That claim is tested, and confirmed, in
example 42.)

Example `30` (the observer; ±1 LCG tables as embedded literals, matrix
algebra as chained row-fiber `prodsum` products — `T = XXᵀ` exact in
integers, powers `M₂ = T·T`, `M₃ = M₂·T`, traces as Frobenius zips): the
internal observer estimating a d = 48-register covariance tower from N
samples. The estimate's *spectral* noise is where q lives, and its law is
pinned: **q̂ = d/N** — measured (0.976, 0.637, 0.121) at sample budgets
giving γ = (1, ½, ¼), one dataset read at three budgets. The noise's free
cumulants are flat (γ, γ², γ³) — free Poisson / Marchenko–Pastur: at γ = ¼
the third free cumulant lands at 0.0631 vs γ² = 0.0625 — while the
*classical* fourth cumulant of the same data is negative: flat in the free
lattice, wrong in the classical one. The species is decided at third order
(b₃ = γ² > 0: skewed free-Poisson; every q-Gaussian is symmetric — the
lure was q-Gaussian, the fish is free-Poisson). One matrix, two readings:
ENTRIES are classical (kurtosis-q = 0.955 ≈ 1, the CLT) while the SPECTRUM
is deformed (q = 0.637 ≈ γ) — the deformation is invisible basis-aware and
unavoidable basis-blind. And freeness itself is measured in-language:
independent noises compose by *free* convolution (free-cumulant additivity
holds, miss −0.10; classical additivity fails by the predicted 2γ_Aγ_B,
miss −0.65; a dependent copy would give 16×, the measured value sits at
the independent 2×). The counterpoint to 29: the ±1 inputs are themselves
platykurtic, yet the spectrum comes out free-Poisson regardless —
**dynamics transports, spectra emerge** (Marchenko–Pastur universality).
Consequence for the whole arc: every spectral/invariant object here —
Jacobian ranks, discovered-generator spectra, Hankel/Stieltjes tests —
carries FREE error bars in the register-rich regime; honest tower
inference there is free deconvolution (subtract the flat γ-tower), and the
non-crossing lattice already in the language (`free_cumulants`, ppl) is
the physically correct calculus for the tower-of-towers. A noncommutative
probability arising classically, from epistemics alone: no phases, no
dynamics — just finitely many looks at too many registers.

## Free deconvolution: the observer's calculus, used

Example `31` puts example 30's law to work on the arc's noise-robustness
gap. An internal observer estimates the covariance tower of the 96
bond-transmission registers rt_j = 2m_j/(m_j+m_{j+1}) of a **97-ball fuzzy
chain** from N = 192 realizations (γ = ½). Bond-locality makes the true
covariance *exactly* tridiagonal Toeplitz, so the truth is fully analytic
and computed in-language: entries as exact 9- and 27-term digit sums,
spectrum λ_k = v0 + 2v1·cos(πk/97) — path-graph cosine bands (and
v1/v0 = −0.4999997: the transfer kernel is log-additive to O(w⁴); any
additive antisymmetric bond kernel gives exactly −½). The estimated
spectrum is badly biased (m2 +33.5%, m3 +103%, m4 +129%); **free
deconvolution** — inverting the free forward model, a triangular system in
the moments — recovers the exact spectral moments to 0.45%/2.7%/5.8%
(improvement 74×/38×/40×), while the **classical-additive noise model
fails structurally** (residuals 12.5×/15.5× the free ones at k = 3/4: the
signal-noise coupling terms 3γm1m2 and γ(4m1m3+2m2²) are invisible to
eigenvalue-additive thinking — spectral noise is additive on free
cumulants, not on eigenvalues). The forward model is confirmed in-data to
0.05% at k = 2. Honest boundary, kept in the file: a first attempt on the
6-mass cascade's 24 lag-family registers barely improved (~1.4×) — a
~6-spike truth (24 observables of 6 latents) puts its error in spike
*fluctuation*, not MP bias; flat-bulk truths are where moment-level
cleaning shines, spiked ones need eigenvector-resolved estimators
(Ledoit–Péché), the next rung. Arc consequence: every downstream verdict
that runs on an *estimated* spectrum — invariant detection, Hankel rank,
generator spectra — should run on free-deconvolved moments whenever
registers are many and looks are few.

## The calibration ladder: a classical uncertainty floor

Example `32` closes the loop on the observer (gap #3 of the arc): every
mass is measured by colliding a calibrated probe, every probe was itself
calibrated by an earlier collision, and the pedigree floats on one exact
anchor. The estimate is a product of noisy factors, so relative variances
**add along the ladder** (measured on the full 3⁶ noise grid to 0.03% of
the linearized law). With an instrument of absolute velocity resolution σ
and a fixed collision budget N, weighing M from the anchor costs
r = (2σ²/3)·K²g(ρ)²/N with g(ρ) = (1+ρ)²/2ρ and K = ln M/ln ρ rungs — a
**ln²M law with a universal optimal step ρ\* = 4.68** (A\* = 4.99, pinned
by an in-language h-scan; the measured ρ = 4/8/16/64 sweep confirms the
valley, and the ρ = 4 ladder beats the single jump by 1.2×10⁴ — while the
jump's own simulation *breaks*: the signal falls below the resolution and
the readout returns negative masses; the catastrophe is not a big
variance, it's no measurement at all). And the **pedigree floor**:
averaging repeated reads with the same probe shrinks only the instrument
part — the within-probe variance falls by exactly 0.3333333333 with three
reads (the multiplicative pedigree makes between/within splitting exact)
while the inherited probe fuzz stays put. The classical, resource-shaped
shadow of a quantum measurement limit: no ħ, but a floor priced in anchor
pedigree and collisions. Bonus: reading through noise systematically
overweighs (E[1/v̂] > 1/v) — the ladder's mean sits +1.25e-4 above truth,
example 20's contact bias reborn as instrument epistemics.

## The tower that cannot be deepened: quantum as truncation

Example `33` builds, as a plain degree-2 moment object, the **Tsirelson
tower**: four ±1 values, means 0, correlations (q, q, q, −q)/q = 1/√2 —
exactly what a packed `Dist<2,·>` literal carries. An explicit rank-2
Euclidean realization certifies degree-2 realizability (extremal, on the
cone's boundary); its CHSH functional is 2√2. Then the wall, from one
variance: over all 16 sign assignments the CHSH observable satisfies
**B² = 4 identically** (pinned on the full hypercube — the six degree-4
cross terms cancel pairwise), so *every* extendable tower has E[B²] = 4,
and any depth-4 extension of the Tsirelson tower would need
Var(B) = 4 − 8 = **−4**: realizable at depth 2, provably no depth-4
extension — a quantum-shaped state living entirely in truncation. The
classical CHSH bound is rederived as *variance positivity* (|s| ≤ 2, with
Var = 0 exactly at s = 2 — ex 23's classical wall), and switching on the
commutator term (B² = 4 + K, |K| ≤ 4, identically zero in any commuting
world) gives |s| ≤ 2√2 — **the Tsirelson bound from the same inequality**;
at maximal violation the variance is exactly zero (the quantum state is a
B-eigenstate). The supra-classical shell 2 < s ≤ 2√2 is mapped two-sided
at s = 2.4: an explicit rank-4 realization certifies depth-2 legality
while Var = −1.76 kills extension. The arc's "missing phases" now have an
exact algebraic address — the commutator — and "extendable" is revealed
as a refinement type on `Dist` values: the compiler slot where the
classical/quantum boundary literally type-checks.

## Collisions as channels: composing towers without ensembles

Example `34` closes the composition gap — the reconstruction program's
missing operation. Conditioning on the shared mass turns the transmitted-
front collision into an **exact linear operator on towers**:
M′[j][p] = ⅓Σᵢ ρᵢⱼᵖ M[i][p], one 3×3 transfer matrix per moment order,
stacked into a block-diagonal 12×12 channel. Composition = matrix powers
(five squarings to depth 20). The channel matches the exact 3⁶-grid
ensemble at depth 6 to ~3×10⁻¹⁵ — it *is* the ensemble's tower,
transported — and then keeps going to depth 20, where a grid would need
3²⁰ = 3.5×10⁹ realizations, with a state that is 12 numbers at every
depth. The spectrum hands over the physics: the naive model's
mean-transfer identity E[ρ] = 1 (ρᵢⱼ + ρⱼᵢ = 2, pinned exact) is broken
by coupling at exactly the **AM–GM tax**: λ₁ = 1 − v0/2 (half of ex 31's
bond variance — that cell's third appearance, with E[ρ²] − 1 = v0 pinned
too). And the hierarchy is nearly **marginal**: λ₂/λ₁² − 1 = 1.7×10⁻⁶,
because consecutive log-ratios anticorrelate with the exact −½ additive
structure (ex 31's v1/v0) — the cascade's log-variance *telescopes* to an
endpoint effect (relative variance saturates: 0.00108 at depth 6 → 0.00110
at depth 20) instead of random-walking. A cascade of fuzzy masses forgets
its interior; the surviving fuzz belongs to the first and last balls, and
the intermittent residue sits at fourth order in the jet width. Part B
isolates the **closure dial** where exactness must end (rational maps on
continuous-support noise): jet channels truncated at r = 1/2/4 against an
exact 5-point reference show monotone truncation errors (κ₁: 1.2e-5 →
3.9e-10 → 2.2e-14) compounding additively with depth. The language angle:
these conditional-representation transfer matrices are the compiled form
of what a vector-valued `dist_map`/dist-through-pool should generate —
towers meeting dynamics without ever touching realizations.

## The observer inside the cascade: kick + Bayes, tower-native

Example `35` is the arc's two halves running as one loop — ex 34's channel
(the kick) and the ppl tower-Bayes primitives (the bookkeeping), with **no
ensemble anywhere in the inference path**. A hidden ball (mass ∈ {0.92, 1,
1.08}, uniform prior) is buried behind four layers of fresh fuzzy medium;
each shot drives a projectile through the chain into it, and the observer
reads the target's exit velocity through a σ = 0.03 instrument (knock-on
spectroscopy). First, a pinned negative that ex 34 predicted:
**identifiability is positional** — an *interior* hidden ball is gauge to
the far-end observer (hypothesis means collapse to within 0.00017, the
telescope cancelling its two roles to O(w²)) while the *endpoint* ball
separates fully (0.080 — **467×** more signal). The chain forgets its
interior; you can only weigh what the product cannot cancel. Then the
loop: per-hypothesis predictive towers fall out of the channel's
conditional state (conditioning on the last-struck mass *is* the branch
representation — the hypothesis bank was already in the state), exact vs
3⁴ grids to 7e-16; the likelihood of each noisy read is an *expectation
under the predictive tower* (jet-smeared instrument: σ_eff² = σ² + κ₂ +
non-Gaussian corrections), matching exact grid likelihoods **19,216×**
closer than the spread-blind mean-field — towers make better observers,
not just better predictions; and Bayes on the 3-point latent is *exact* by
`dist_reweight` with Lagrange-interpolated runtime coefficients.
**Conditioning spends stochastic order**: the depth-6 prior affords
exactly two quadratic observations (6 → 4 → 2), and the surviving order-2
posterior is spent whole on the categorical readout (`dist_expect` of the
degree-2 Lagrange indicators, partitioning unity exactly). Result: the
true hypothesis at 83.3%, posterior mean 1.0666 ± 0.030, within **1e-7**
of the exact all-ensemble Bayes posterior. A third shot would need order
the tower no longer has — the elaborator refuses (ppl/048): an observer's
evidence budget is the depth of their tower.

## Adaptive protocols: aiming each shot from the posterior

Example `36` puts experiment design inside the loop (the Fisher-dual
thread). A hidden mass ∈ {1.0, 1.3, 512.0} packs a *coarse* question and a
*vernier* question into one latent; the knock-on read w = 2p/(p+M)
resolves a pair only near its matched probe p\* = √(MᵢMⱼ), so which probe
you fire decides what you can learn. Pinned: the posterior-weighted
**design surface follows the posterior** (argmax at p = 25 under the
prior — the coarse pair's matched point — moving to p = 1.14 after shot
one, the vernier's); the greedy rule p_next = exp(E_post[ln M]) **retunes**
the probe 8.73 → 1.20 → 1.28, opening the close pair's read gap from 1.1σ
to 2.6σ; and on identical noise draws the adaptive observer ends at
belief 0.99828 in the truth vs 0.80985 for the repeated ex-ante-optimal
probe — **110× less residual doubt**, with the fixed protocol's second
shot provably moving *backward* (its vernier gap sits under the noise).
New composition en route: **order renewal** — each quadratic likelihood
spends two of the prior's four orders, but a finite-support posterior *is*
its categorical readout, so the observer re-lifts after every shot
(probabilities out via `dist_expect` of the Lagrange indicators, point
towers back in via `dist_mix`; relift exact at 9e-16). The
disintegrate-remix round trip of ppl/047, promoted to the recursion that
makes unbounded sequential inference legal on finite support — ex 35's
budget wall binds only irreducibly continuous latents.

## The wall from inside: a quantum family crosses the boundary

Example `37` approaches the edge from the quantum side. A Werner state
ρ(v) = v|Bell⟩⟨Bell| + (1−v)I/4 with optimal CHSH settings walks the dial
v = 0.2 → 1, and every structural meter the arc owns is computed in
closed form along the way — including the *underneath* view: the
minimal-negativity depth-4 extension of the behavior (the zero-coefficient
Fourier ansatz has cells (1 ± 2c)/16 and achieves the LP bound
N = max(0, (s−2)/4) by duality). The verdict on discontinuity: **there is
none.** The behavior tower is analytic in v; the required-classical
variance 4 − 8v² crosses zero transversally at v\* = 1/√2; the extension
does not cease to exist at the wall — its smallest cell passes through
zero and continues *negative* (a signed quasi-distribution), with
negativity onset N ≈ 0.707·(v − v\*): a **second-order transition**
(continuous, derivative kink). Only binary verdicts ("extendable?") jump —
the discontinuity lives in the questions, not the physics. Sharper still:
**quantumness has no threshold** — the commutator excess ⟨B²⟩ − 4 = 4v
violates the hypercube identity at *every* v > 0, deep inside the
"classical" region; the wall is merely where variance positivity stops
being able to hide it (|⟨B⟩| crosses 2 exactly where negativity leaves
zero, pinned equal). Both walls carry the same inside signature —
**extremality = zero variance** (classical: deterministic points at
s = 2; quantum: Var_q(B) = 4 + 4v − 8v² hits zero at v = 1, the maximal
violator is a B-eigenstate) — because positivity boundaries are always
met as some smooth variance's transversal zero. The TLM/Tsirelson
criterion 4·asin(v/√2) − π (arcsine by in-language Newton) reaches zero
exactly at v = 1: for CHSH correlations the quantum edge *is* the cone
edge, so the strict quantum-vs-cone gap needs richer scenarios (the
I3322 expedition, next). And the three boundaries sit at three places —
separability v = 1/3, local model v\* = 0.7071, cone edge v = 1: the
tower only ever sees the second.

## The top wall exists: I3322 and the supra-quantum shell

Example `38` exhibits the strict gap that CHSH cannot show (there,
quantum edge = cone edge). In the I3322 scenario (3×3 binary settings)
all three strata land in one file: the **classical wall certified
in-language** (the I3322 functional swept over all 64 deterministic
strategies: max = 0, exactly 20 saturating — ex 33's hypercube sweep one
scenario up); a **quantum point constructed** (maximally entangled qubits
with explicit Bloch frames: I = (√2−1)/2 ≈ 0.20711 — any zero-marginal 3D
dot-product correlation is singlet-realizable); and a **tower point
beyond everything quantum**: seven explicit unit vectors in ℝ⁴ whose Gram
is a legal degree-2 moment matrix (realization = certificate, norms
pinned) with I3322 = **0.34090** — past the quantum supremum 0.2508754
(the one imported anchor: Pál–Vértesi 2010). The supra-quantum shell
T \ Q is *inhabited*, by an explicit object the `Dist` type would accept.
The exit is shallow: along the tilt dial connecting the two constructed
points, the path crosses the quantum ceiling at s\* ≈ 0.048 — a
five-percent marginal tilt of a perfectly quantum correlation pattern
already exceeds anything quantum mechanics can do in this scenario.
Honest scope: the tilt family is a level-1 ansatz, not the cone's true
edge (~0.366); whether the quantum edge here shows ex 37's variance-death
signature needs in-language SDP certificates — the certificate machine is
the named next build.

## Quasi-distributions as values (ppl)

The signed-extension finding of ex 37 is now compiler machinery:
`dist_atoms(r, x1, w1, ...)` builds the order-r tower of a possibly
**signed** atomic measure (non-classical towers become carryable,
reweightable, mixable values; all-positive weights subsume the
point-tower renewal of ex 36), and `dist_negativity(d, x1, ..., xs)`
reads the L1 negativity off a claimed support (Lagrange cells, strict
order accounting) — **the classical wall as a meter reading**. The
Tsirelson B-marginal is corpus test ppl/050: a 2-atom quasi-tower
carrying κ₁ = 2√2 and κ₂ = **−4** as first-class values, with negativity
0.2071 = ex 37's order parameter at v = 1, N = (s−2)/4 in the shell, and
exactly zero inside the wall.

## The certificate machine: walls proven, not imported

Example `39` builds the missing instrument — an in-language verifier for
positive-semidefiniteness (guarded LDLᵀ pivots with zero-pivot residual
checks) and for dual sum-of-squares certificates. Four pinned
demonstrations: the negative-variance Hankel **refuted by pivot** (second
pivot −4); the Tsirelson moment matrix verified **extremal PSD rank 3**
(pivots 1,1,1,0,0, zero residuals) — ex 33's boundary point machine-
audited rather than exhibited; ex 38's supra-quantum Gram verified
**rank-4 PSD** (four positive pivots, three exact zeros — how any claimed
tower state gets audited from here on); and the star: **Tsirelson's bound
certified in-language** — the analytic dual Λ = (p₁p₁ᵀ + p₂p₂ᵀ)/√2 checks
out PSD (rank 2), matches the negated CHSH coefficients to 5e-15 with no
spurious cells (the anticommutator cancellation, verified numerically),
and has tr Λ = 2√2, so ⟨Λ, M⟩ = 2√2 − CHSH(M) ≥ 0 for every unit-diagonal
moment matrix: the quantum ceiling of the arc's central scenario is now a
*checked proof*, not a trusted constant — and the Tsirelson tower
saturates it exactly (complementary slackness at 2e-15). Still imported:
only I3322's ceiling; its dual verifier is this same machine at 10×10+,
awaiting a certificate from an external solve.

## Negativity flow: what channels do to nonclassicality

Example `40` runs ex 37's order parameter against ex 34's channels, the
whole loop in four ppl primitives (cells out via `dist_expect`, kernel
arithmetic, rebuild via `dist_atoms`, re-meter via `dist_negativity`).
Pinned: **physical channels only destroy negativity** — a stochastic
kernel contracts the signed tower N = 0.2 → 0.075 → 0 (full
classicalization in two steps; data processing for nonclassicality: a
column-stochastic map is an L1-contraction on the negative part — N is a
monotone under classical dynamics, the resource-theory shadow of
CP-divisibility). **Unlawful kernels manufacture it** — one signed column
creates N = 0.1 from a perfectly classical point tower: kernel positivity
⇔ negativity monotonicity; "physical" and "cannot create nonclassicality"
are the same predicate. And **observation is exempt: Bayes amplifies** —
a legal positive likelihood emphasizing the negative cell turns N = 0.2
into N = 5.0 (25×, evidence Z = 0.04): the less likely the observation
under the quasi-model, the more nonclassical the conditioned state —
post-selection distillation, the classical shadow of weak-value
amplification, diverging as Z → 0. Negativity behaves exactly like a
resource: monotone under free operations, creatable only by leaving the
theory, concentratable by post-selection. The wall, restated one last
time: classical worlds are the ones whose dynamics can never push this
meter off zero.

## The ceiling, certified: I3322 without import

Example `41` retires the arc's last trusted constant. An untrusted
external solve (alternating projections; `scratchpad` F# script) produced
a dual certificate for the NPA level-1+AB relaxation of I3322; the file
embeds only its **Gram factor G** and proves everything else from
scratch. The certificate matrix Λ = GGᵀ is computed in-language by one
row-fiber prodsum — **PSD by construction**, nothing to eliminate or
eigensolve. A 58×256 histogram values-product collapses Λ's cells into
its 58 moment-label sums (LBL tags which cells share a moment: basis
words reduced by involution, symmetrized by joint reversal), and the
measured deviation from the targets (t′, −Bell, 0, …) is **absorbed into
the bound, not waved away**: since every non-identity word is a product
of ±1 observables, |⟨W⟩| ≤ 1, so quantum I3322 ≤ (t′ + Σ|dev|)/4 − 1.
Pinned: t′ = 5.08000000025, Σ|dev| = 1.7e-9 (matching the external
oracle's residual exactly), **ceiling 0.2700000005** — strictly below
ex 38's constructed tower point 0.3409 (recomputed here; margin 0.0709).
The sandwich classical (0.25) < quantum (≤ 0.27) < tower (0.3409) is now
self-contained: every wall in the arc is checked in-language, none
trusted. (Tighter ceilings — level-1+AB's optimum ≈ 0.2513, higher NPA
→ 0.2508754 — drop into the same file unchanged; 5.08 buys a fat 1e-4
interior margin, robustness over sharpness.) Engineering note: the first
version verified PSD via an unguarded 16×16 LDLᵀ scalar chain and never
compiled — deep scalar dependency chains with high fan-out inline
exponentially in codegen. The Gram-factor design *is* the fix: the
factor is the certificate.

## The dynamical q: quantum chaos reshapes the partition lattice

Example `42` closes the dichotomy example 29 opened. Ex 29's parenthetical
claim — dynamical crossing suppression is a genuinely quantum signature —
is tested with ex 29's own estimator and ex 29's own mirrored control, on
an exactly diagonalized quantum spin chain (mixed-field Ising, L = 6,
d = 64, irregular fields to kill reflection parity; the h = 0 twin is
free-fermion integrable). Everything is in-language via the arc's
established moves: H, A = σx₁, B = σz₆ built from Int64 bit algebra (ex
29's digit idiom), the eigenfactors embedded but **certified in-file** (ex
41's pattern — residuals |HV − VE|² and |VVᵀ − I|² pinned at ~1e-24, so
the external solve's provenance is proof-irrelevant), and time evolution
with no matrix exponential: in the eigenbasis A(t) = X + iY with
X = Ã ∘ cos(ΔE·t), Y = Ã ∘ sin(ΔE·t), every trace real by symmetry. The
estimator is the crossing channel verbatim on the time-ordered quadruple
(A(t), B, A(t), B) — the crossing pairing (13)(24) *is* the
out-of-time-order contraction, so q̂ is the normalized OTOC. Findings,
all pinned: **q̂(0) = 1 exactly and cannot fall before the Lieb-Robinson
front arrives** (q̂(0.5) = 1 to twelve digits — ex 26's causal cone,
quantum side); under chaotic evolution it **collapses to the scrambling
floor and equilibrates** (six incommensurate late times: mean 0.0891,
variance 1.5e-4); the same six times in the integrable twin give mean
0.470, variance 0.146 — **950× the variance, swinging −0.17..+0.96**:
unitarity alone does not scramble the crossing channel (ex 17's
persistence dichotomy at fourth order). The bulk gap-ratio statistic,
computed from the same embedded spectrum, agrees: r = 0.4731 (toward GOE
0.536) vs 0.2919 (below Poisson — exact free-fermion degeneracies). And
the mirrored control: deform the state's shape exactly as ex 29 deformed
the jets (thermal e^{−0.3E} and a spectrally leptokurtic reweight, both
computed in-language from the certified spectrum) — the floor moves
(0.089 → 0.080 / 0.172) but **every κ₄ stays negative, 12/12 sign
checks**, where the identical move in ex 29 flipped 15/15. Verdict:
`DYNAMICAL_NOT_INHERITED`, the exact mirror of ex 29's
`INHERITED_NOT_DYNAMICAL`. Classical mixing transports kurtosis and
cannot suppress crossings; quantum chaos suppresses crossings and the
state cannot restore them. (Honest scope: freeness-from-ETH/OTOC decay is
known physics; what is new is the same estimator + control run on both
sides of the divide, exact and pinned. Every number was verified against
an independent complex-matmul route, agreement ~1e-14, before pinning.)

## Cleaning the spikes: eigenvector-resolved estimation

Example `43` crosses ex 31's honest boundary — free deconvolution cleans
flat-bulk spectra but gained only ~1.4× on spiked truths, because the
error lives in spike *rotation* and moments carry no basis information.
The named next rung was Ledoit–Péché eigenvector-resolved cleaning; it
needed an eigensolver, and the math module now supplies one (`m.eigh`).
Setup: a fuzzy chain of d = 40 registers whose collective modes are the
chain's sine normal modes (exactly orthogonal by the DST identity, built
in-language from `sin`), four latent modes with strengths ℓ = (10, 5,
2.5, 1.3) on unit white register fuzz, N = 160 looks (γ = 1/4). Pinned:
the **BBP wall, measured** — modes 1–3 emerge biased (λ̂ = ℓ(1+γ/(ℓ−1)))
and rotated (squared overlaps 0.961/0.909/0.716 vs the BBP law), while
mode 4 (ℓ = 1.3 < 1+√γ) is *swallowed*: best overlap with any sample
eigenvector 0.133, top bulk eigenvalue under the edge — below the wall a
collective mode is invisible to the observer's spectrum. Spike strengths
recovered to 1.4–2.9% by inverting the BBP map in-language. The
**cleaning ladder**: raw 18.09 > naive-with-true-strengths 8.53 > RIE
8.26 > oracle bound 8.15 — eigenvector-resolved cleaning improves raw
2.19× and lands within 1.3% of the information bound. The moral pin:
**RIE beats god-given true strengths** — sample eigenvectors are rotated
and the Ledoit–Péché value prices the rotation in; plugging in the truth
overshoots. And ex 31's route, run here for contrast: free deconvolution
fixes the spectral moment (13.2% → 1.9% error) and cannot move the matrix
error at all. The 2.19× came from basis information alone.

## The detector that survives noise: TICA/VAC in-language

Example `44` closes review-gap 3 and retires a structural weakness of the
arc's oldest instrument: the invariant detector (ex 09–16) reads
conservation as *zero variance*, a criterion that dies at any
measurement-noise floor. The noise-robust reformulation is the
generalized eigenproblem C(τ)v = λC(0)v (TICA/VAC): white observation
noise is lag-uncorrelated, so it inflates C(0) *only*. This file is also
the arc's first fully **in-language dynamics**: 8 velocity-Verlet
trajectories of the 2D isotropic oscillator (12,000 symplectic steps)
with an exact-double LCG observation channel, run inside the new
imperative blocks — no external data, not even emitted tables. The clean
VAC spectrum of the 14-monomial dictionary is exactly {1×4, cos τ ×4,
cos 2τ ×6} — the eigenvalue-1 quartet is the u(2) invariant algebra
{E_x+E_y, E_x−E_y, L_z, xy+p_xp_y} (superintegrability made spectral) —
and the measured clusters land on the predicted cosines. Pinned: σ = 0.1
observation noise lifts the per-trajectory dispersion of E three orders
(the zero-variance verdict flips VARIANT — the old detector is blind
here), while the invariant quartet's eigenvalues drop but **plateau in
τ** (top-4 lag-12 vs lag-24 differences sum to 0.014 while the 5th
eigenvalue moves 0.46). The per-quantity verdict is reborn as a Rayleigh
quotient in the whitened metric: the energy direction reads 0.881/0.884
at the two lags (plateau) while the oscillating probe swings +0.35 → −0.72.
τ-independence, not magnitude, is the invariant's signature — and C(τ)
never saw the noise. (Koopman *eigenvalues* for non-symmetric generators
remain open — `eigh` is symmetric-only.)

## The spectrum of the law: Koopman eigenvalues

Example `45` retires the EDMD thread's oldest limitation: the fitted
generator's *kernel* found invariants (ex 15–16), but the rest of its
spectrum was unreadable without a non-symmetric eigensolver. `m.eig`
(Francis QR) reads it. Generator EDMD — L = (Σḋdᵀ)(Σddᵀ)⁻¹ over the 9
monomials of degree ≤ 3 in (x, p), a dictionary *exactly* closed under
linear flow, with exact chain-rule derivatives and closed-form
trajectories — makes the whole experiment analytic. Pinned: the
conservative oscillator's spectrum is exactly {±3i, ±2i, ±i (twice), 0}
— the harmonic ladder plus the invariant at zero, real parts at 1e-15;
the damped twin's eigenvalues land on the exact lattice aμ₊ + bμ₋ with
μ± = −γ ± i√(1−γ²); and **the invariant's eigenvalue is displaced to
exactly μ₊ + μ₋ = −2γ** — the zero-variance/kernel verdict of the old
detector is the γ → 0 limit of an eigenvalue displacement the spectrum
now measures directly. Frequencies from data, no FFT, no phase unwrap.

## The Ehrenfest loop: Bohr frequencies from expectation data

Example `46` closes review item 7 — the arc's only never-closed loop.
Ehrenfest makes d⟨A⟩/dt a linear flow on expectation values, so the same
operator machinery that reads classical laws should read a quantum
spectrum from nothing but ⟨x(t)⟩. It does: the 16-level anharmonic
oscillator (H built in-language from ladder formulas, eigenfactor
certified in-file at 1e-25), a wavepacket's ⟨x(t)⟩ generated from the
certified spectrum, delay-embedded and fitted; `m.eig` of the transfer
operator returns e^{±i·gap·δ} — the **Bohr gaps, on the unit circle**,
matching cos(gap·δ) from the same file's certified eigenvalues to 5e-4.
The anharmonic ladder's unevenness (gap₁₂ − gap₀₁ = 0.0998) is resolved
at 400× the instrument precision, and the Δn = 3 satellite line — 250×
weaker, ex 18's forbidden-line territory — appears within 6e-4 of its
certified position (two instruments, one spectrum: ex 18 read these gaps
from moment towers). The classical twin — a 32-trajectory Verlet
ensemble's ⟨x(t)⟩ — dephases (ex 05), and its fitted operator has every
eigenvalue strictly inside the disk, the top modulus *being* the
dephasing meter e^{−δ/τ}. Planck's discreteness, read as a
Koopman-modulus dichotomy: the spectral face of ex 42's verdict.

## The tower counts, locates, and weighs: flat extension

Example `47` prototypes the flat-extension guard (Curto–Fialkow) — the
compiler slot the arc has pointed at since ex 33. Golub–Welsch runs
in-language end to end: moments → Chebyshev recursion → Jacobi
tridiagonal → `m.eigh`; eigenvalues are the atoms, first-row eigenvector
squares the weights, Hankel rank the count. Pinned: six moments of a
3-atom law return its atoms and weights to 1e-15, with **flatness
certifying exactness** (the 4×4 Hankel's smallest eigenvalue ~1e-17 —
rank stalls at 3 — while the 3×3's is finite): rank stall = "the
extension is flat" = the tower *is* a measure — the exact complement of
ex 33, where no extension of any depth exists. The physics payoff closes
review-gap 4: 24 damped double-well trajectories integrated in-language
leave a t→∞ ensemble whose tower is 2-atomic — rank **counts** the
attractors, nodes land on the wells at ±1 to 1e-7 (the residual is the
e^{−γt} ring-down, physics not error), and the recovered weights equal
the directly-counted basin fractions to 1e-14 (**weighs** — an even
split off an asymmetric grid, a fact of the interleaved spiral basins).
And the guard refuses: the Tsirelson B-marginal's Hankel has eigenvalue
−0.7016 — no measure, no atoms, refusal by spectrum. Atoms for classical
towers, obstructions for quantum ones: that pair of behaviors is the
flat-extension guard the compiler slot wants.

## Running



```
blade run examples/physics/01_projectile_range.blade      # any single example
powershell examples/physics/tools/gen_invariants.ps1      # the discovery engine
powershell examples/physics/tools/validate_examples.ps1   # all EXPECT pins
```

Run on the Release build (`bin/Release`); the deep `dist_map` chains overflow
the Debug stack. `tools/validate_examples.ps1` checks every example against
its pins (710 EXPECTs across 47 files).
