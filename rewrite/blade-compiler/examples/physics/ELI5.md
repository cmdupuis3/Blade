# The Fuzzy Billiards Program, Explained Simply

*A plain-language tour of examples 01–34 and the ppl tower machinery — what
we built, what we found, and what it says about where classical physics
ends and quantum mechanics begins.*

## The one-paragraph version

Ordinary simulations store one number per quantity: this ball weighs 1.05.
In this program every quantity carries its whole *shape of uncertainty* —
the average, the spread, the lopsidedness, the tail-heaviness, as many
levels as you ask for. We call that stack of numbers a **tower** (the
compiler's `Dist` values). Blade knows the exact rules for how towers move
through arithmetic and physics, so instead of simulating a million
possible worlds you compute *once*, with the uncertainty riding along
exactly. Then we pointed that machinery at the simplest physics there is —
billiard balls whose masses we only fuzzily know — and kept asking one
question: **what does a world look like when your knowledge of it, not
the world itself, is the thing with error bars?** The answer turned out to
include working detectors for conservation laws, a map of where "before,"
"did it happen," and "which one is it" stop being answerable, a complete
anatomy of measurement, and — the part we didn't expect — a precise,
computable statement of what separates classical physics from quantum
mechanics.

## The toy world

Perfect billiard balls, perfect collisions, perfect determinism. The only
imperfect thing is *us*: we know each mass as a small menu of
possibilities instead of one value. Every experiment builds the full
ensemble of possible worlds (or, later, avoids needing to), computes what
we'd observe, and pins the numbers as tests — 397 of them across 34
example files, every one checked on every run.

## What we found, in order of increasing strangeness

**1. Laws live in the links, not the numbers.** Energy conservation
doesn't say "this number stays put" — in a fuzzy world it says "the
uncertainty in this ball is perfectly *linked* to the uncertainty in that
one." Same for "objects can't overlap": each possible world respects it,
but if you describe the balls with unlinked error bars, your description
happily puts them inside each other. Break the links (treat errors as
independent, like every lab notebook does) and you don't just lose
accuracy — you produce descriptions no physical world could have. We can
*prove* a description is unphysical from just three levels of its tower
(example 25). Physics, seen from inside the tower, is mostly a set of
rules about which uncertainties must move together.

**2. Some things refuse to blur.** Fuzz anything you like — masses,
angles, timing — and certain quantities come out of the machine with
exactly zero spread: energy, momentum, angular momentum, the
center-of-mass motion. The tower is a *conservation-law detector*: you
don't tell it the laws, they show up as the things that stay sharp. Run
it on planetary orbits and it rediscovers a famously hidden conserved
quantity of the Kepler problem (the Runge–Lenz vector) blind, and can
even measure how badly a law breaks when you perturb the physics
(examples 09–19).

**3. Order, existence, and identity can become unanswerable — by exactly
computable amounts.** With fuzzy masses, "which collision happened first?"
can have no answer (example 22), "did they collide at all?" can have no
answer (two equal-mass stories, bounce and pass-through, fit every
velocity record — example 24), and a sampling camera slower than the
collision loses the event with probability we can pin (a Nyquist rate for
*events*). None of this is the world being vague — the world always did
one definite thing. It's the *description* being lawfully unable to
decide, and the tower measures the undecidability like any other physical
quantity. Even "how much stuff is there" is observer-relative: a probe
striking a merged pair of balls reads an effective mass that is neither
ball nor their sum, because an unbound pair is a cascade, not an object
(example 27).

**4. Measurement is a kick plus bookkeeping.** When a probe ball hits a
system, two things happen: the system genuinely recoils (the kick — real
physics), and our description of it sharpens (Bayes — bookkeeping). In
classical physics these two are separable in principle; we separated them
(examples 06, 27). Quantum collapse is what you'd have if they *couldn't*
be separated. This session made the bookkeeping half a first-class
compiler citizen: the ppl module now has `dist_reweight` (a posterior is a
reweighted prior), `dist_expect` (evidence), and `dist_mix` (mixtures) —
and conditioning turns out to have a price: **each observation spends
stochastic order**. A sharper posterior is paid for in tower depth. Even
inference has a budget line.

**5. The observer is made of the same fuzz — and that has laws.** If every
meter is itself a fuzzy ball, calibration is a ladder: weigh the medium
ball with the small one, the big with the medium. Relative uncertainty
*adds* along the ladder, weighing something much bigger than your standard
has a cost that grows like the *square of the logarithm* of the ratio,
there's a universal best step size (about 4.7× per rung), and a floor that
no amount of repetition removes — because repeating a measurement with the
same probe re-uses the same inherited fuzz (example 32). And a chain of
collisions, remarkably, *forgets its own interior*: the fuzz of the middle
balls cancels out of the transmitted signal almost exactly, leaving only
the ends (example 34, where collisions became composable operators on
towers — we ran a cascade to depth 20 that would have needed 3.5 billion
simulated worlds, using twelve numbers).

**6. The q-fishing surprise: who owns the strange statistics.** Quantum
theory's mathematics of uncertainty is *noncommutative* — order matters.
We went fishing for a signature of that (crossing suppression, the "q"
deformation) in classical dynamics: not there. The dynamics just carries
whatever statistical shape you feed it (example 29 — flip the input's
shape and every output flips with it). But then we found it somewhere
unexpected: **in the observer**. Anyone who estimates many quantities
from few looks — many dials, few glances — has estimation noise whose
spectrum obeys *free probability*, the noncommutative uncertainty
calculus of quantum random matrices, with deformation strength exactly
(number of dials)/(number of looks) (example 30). No quantum mechanics
anywhere: it's forced by dimension counting alone. And it's useful: using
the free rules to *clean* an estimated spectrum recovered a 97-ball
chain's true vibration structure 74× better than the raw estimate, where
ordinary error-bar thinking failed outright (example 31).

## The classical/quantum connection — what we can now actually say

This is the part worth leaning in for.

**Quantum states are towers that can't be deepened.** Every tower in our
classical experiments could, in principle, be extended: more moments
exist, because a real ensemble of worlds stands behind the numbers.
Example 33 constructs a tower — four ±1 quantities with particular
correlations, the "Tsirelson tower" — that is perfectly legal at depth 2
(we exhibit the geometry that realizes it) but **provably has no depth-4
extension**. How provably? For these quantities there's an algebraic
identity: the famous CHSH combination B always satisfies B² = 4, in every
possible classical world (we check all sixteen). So any deepening of our
tower would have to give B a variance of 4 − (2√2)² = **−4**. Variances
are averages of squares; they cannot be negative. There is no deeper
story. That is a quantum state, met concretely: *numbers with no
underlying joint reality, living entirely in truncation*.

**Both famous bounds are one rule.** "Classical correlations obey CHSH ≤
2" is, in this language, nothing but *variance positivity* — the most
basic sanity check a tower has. And the quantum bound (2√2, Tsirelson's
bound) is the SAME sanity check after one change: allowing measurement
order to matter. Noncommutativity adds a term to the identity (B² = 4 +
commutator), the commutator's size is capped at 4, and variance positivity
then allows exactly 2√2 — no more. At maximum violation the variance is
exactly zero: the quantum state saturates the same inequality classical
determinism saturates at 2. **"Phases" — interference, the thing every
one of our classical experiments lacked — buy precisely four units of B²,
and that purchase is the entire quantum advantage.** The wall between the
regimes is one variance; phases are what rescue it.

**So the boundary is a permission, not a mechanism.** Classical physics
asserts that a deeper joint account always exists (every tower extends).
Quantum mechanics declines — and everything Bell-flavored is the shadow
of that refusal. In Blade terms, "extendable" is a *refinement type* on
`Dist` values: the classical/quantum boundary is, literally, something a
compiler could type-check.

**And yet the strange math isn't exclusively quantum.** The observer
result (example 30) shows noncommutative probability arising in a fully
classical world, from epistemics alone — from having more registers than
looks. Nature seems to reuse one piece of mathematics twice: quantum
theory puts it in the dynamics; a classical world puts it in the eye of
any finite beholder.

## What's honestly new here

The ingredients are known mathematics — moment problems, free probability
(Marchenko–Pastur, free deconvolution), the Landau identity behind
Tsirelson's bound, transfer operators. What we believe is fresh: the
*assembly* (one typed machine doing all of it exactly, with every claim a
pinned test); several framings (event-order statistics as a moment problem
on permutations; exclusion as a three-moment realizability test;
non-extendability as a computable negative-variance cell; q̂ = dials/looks
as the observer's deformation law; conditioning priced in tower order);
and a few measured laws we haven't found elsewhere (the calibration
ladder's ln²-cost with its universal step, the telescoping interior of
fuzzy chains, the exact 1/3 pedigree-floor shrink). The physics of the
program in one sentence: **classical mechanics, rewritten so that what a
state fails to determine is a first-class computable object, turns out to
contain precise, wall-by-wall coordinates of where quantum mechanics
begins — and the walls are all positivity.**

## Where it goes next

Multivariate conditioning (the current tower-Bayes primitives are
univariate); the flat-extension guard as an actual compiler type (laws as
types); eigenvector-resolved spectral cleaning for spiked systems; and
channels-plus-Bayes together: an observer *inside* the cascade, updating
towers with the same operators the physics uses.
