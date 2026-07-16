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

## Running



```
blade run examples/physics/01_projectile_range.blade      # any single example
powershell examples/physics/tools/gen_invariants.ps1      # the discovery engine
powershell examples/physics/tools/validate_examples.ps1   # all EXPECT pins
```

Run on the Release build (`bin/Release`); the deep `dist_map` chains overflow
the Debug stack. `tools/validate_examples.ps1` checks every example against
its pins (229 EXPECTs across 28 files).
