# Blade Feature Module: Equivariance and Equivariant Neural Networks

Status: **near-term** — design specification complete (`blade_ml_spec_v10.md`,
Draft 0.3), implementation upcoming. This module doc is the canonical home for
(a) the **core-language equivariance hook** (formerly formalism v10 §8 and
§4.15.4 — moved out of the core formalism because it is annotation-layer, not
core semantics), and (b) the **equivariant ML library** built on it. Detailed
construct listings remain in `blade_ml_spec_v10.md`; this doc states the
semantics and the contract.

Blade has two orthogonal symmetry systems:

| System | Group kind | Affects | Cost |
|--------|-----------|---------|------|
| Index types (`SymIdx`, `AntisymIdx`, ...) | Discrete permutations (Sₙ) | Storage layout + iteration | Real speedups |
| Equivariance annotations (this module) | Continuous groups (SO(3), SE(3), O(3), ...) | Type checking only | Zero runtime cost |

They compose: a stress tensor is `Array<Float like SymIdx<2, 3>> with
equiv(SO<3>, L2_even)` — triangular storage from the index type, `σ' = RσRᵀ`
transformation checking from the annotation.

---

## Part I — Core-language hook (annotation + inference framework)

The core language provides the annotation mechanism and inference framework
only; group-specific rules (which representations exist, what `cross` returns)
live in domain libraries. This is the boundary that keeps the core group-theory-free.

### 1. Annotations

```blade
let v: Array<Float like Idx<3>> with equiv(G, rep)
let energy: Float with invariant(G)          // sugar for equiv(G, trivial)

function norm(v: T^1 with equiv(G, rep)) -> T^0 with invariant(G)
function scale(s: T^0 with invariant(G), v: T^1 with equiv(G, rep)) -> T^1
// output annotation omitted → inferred: equiv(G, rep)
```

Unannotated values are non-equivariant: freely mixable with each other, not
passable to equivariant parameters. Adoption is gradual — if inference fails,
the result is non-equivariant, not an error.

### 2. Inference rules

| Operation | Inputs | Output |
|-----------|--------|--------|
| `a + b`, `a - b` | same rep ρ | ρ |
| `s * v`, `v / s` | invariant, ρ | ρ |
| `dot(a, b)` | ρ, ρ | invariant |
| `norm(v)` | ρ | invariant |
| `cross(a, b)` | ρ, ρ | domain-library rule (pseudovector for O(3) vectors) |
| `a ⊗ b` | ρ₁, ρ₂ | ρ₁ ⊗ ρ₂ (CG decomposition) |
| `sum`/`mean` over an index | ρ | ρ (rank reduced) |
| `min`/`max` | invariant only | invariant (ordering requires invariance) |

Inference flows through expressions; explicit annotations are checked against
the inferred representation.

### 3. Errors detected at compile time

1. **Representation mismatch**: `vector + pseudovector` under O(3).
2. **Equivariance breaking**: component extraction `v(0)` from an equivariant
   array (hint: use `norm`, `dot` with a reference direction).
3. **Wrong output declaration**: declaring `cross : vector` when inference gives
   pseudovector.
4. **Index/equivariance incompatibility**: `AntisymIdx` storage with a
   symmetric representation (`L2_even`), and vice versa.
5. **Missing annotation**: passing plain data to an equivariant parameter.

### 4. Equivariant index types

```blade
type EquivIdx<n, G, ρ>                       // dimension, group, representation
type VectorIdx       = EquivIdx<3, SO<3>, standard>
type PseudovectorIdx = EquivIdx<3, SO<3>, adjoint>
type ScalarIdx       = EquivIdx<1, SO<3>, trivial>
```

`EquivIdx` is the index-type-level carrier of representation data; the `with
equiv` annotation is the value-level carrier. Both feed the same checker.

---

## Part II — The equivariant ML library

Goal: type-safe, zero-overhead E(3)-equivariant networks. Positioning vs e3nn:

| Aspect | e3nn | Blade |
|--------|------|-------|
| Irreps spec | string `"16x0e + 8x1o"` | static spec array |
| Error detection | runtime (during training) | compile time |
| Tensor wrapping | `GeometricTensor` | native arrays with `IrrepsIdx` |
| CG iteration | dense matrices | sparse index types |

### 5. Irreps

```blade
type Parity = Even | Odd
type Irrep<L: Nat, p: Parity>                 // L0e, L1o, L2e, ... named aliases
static function dim(ir)    = 2 * L + 1
static spec = [(L0e, 16), (L1o, 8), (L2e, 4)] // 16 scalars, 8 vectors, 4 rank-2
                                              // total dim 16·1 + 8·3 + 4·5 = 60
```

Everything downstream is parameterized by static spec arrays — this is the
`static function` / `let const` machinery of the core language doing
representation theory at compile time.

### 6. `IrrepsIdx<spec>` — the block-structured index

```blade
type IrrepsIdx<spec> = DepIdx<
    Idx<length(spec)>,                                    // block
    lambda(b) -> Idx<mult(spec(b))>, Idx<dim(irrep(spec(b)))>  // (mult, m)
>
```

A specialization of the core `DepIdx`: iteration yields `(block, multiplicity,
m-component)` triples; extent is `total_dim(spec)`. Coiteration with edges/nodes
requires named index types (`type EdgeIdx = Idx<E>`) per the core structural
identity rules.

### 7. Clebsch-Gordan machinery

Dependent + constrained records (core features) encode the selection rules in
types:

```blade
struct CGPath {
    l1, l2: Nat<angular_momentum>,
    l_out:  Nat<angular_momentum, min=abs(l1-l2), max=l1+l2>,
    p1, p2, p_out: Parity
} where p_out == parity_mul(p1, p2)

struct CGIndex<path: CGPath> {
    m1:    Int<min=-path.l1,    max=path.l1>,
    m2:    Int<min=-path.l2,    max=path.l2>,
    m_out: Int<min=-path.l_out, max=path.l_out>
} where m1 + m2 == m_out
```

`cg<path>(idx)` is a static function; the compiler generates CG tables at
compile time for all paths actually used. Iteration over `CGIndex` visits only
the sparse nonzero support (`m1 + m2 = m_out`), never a dense (2l1+1)(2l2+1)(2l_out+1) box.

### 8. Operations

| Operation | Structure | Key property |
|-----------|-----------|--------------|
| `tensor_product<cfg>(in1, in2, weights)` | loop over `TensorPaths<cfg>` (SparseIdx of valid paths) → multiplicities → `CGIndex` | output irreps must be reachable (`all_valid_outputs`) |
| `SphericalHarmonics.Y<L>(v)` / `Y_to<L_max>(v)` | `Idx<3>` → `IrrepsIdx<sh_spec>` | the only L-raising primitive; explicit low-L polynomials, recurrence above |
| `linear<spec_in, spec_out>(input, weights)` | per-block matrix multiply over multiplicities, shared across m | mixes multiplicities within an irrep only (`all_irreps_present`); cross-irrep mixing would break equivariance |
| `gated_activation(features)` | scalars: `silu` directly; higher L: sigmoid-gated by scalar block | nonlinearities on L>0 components directly would break equivariance |
| `norm_activation(features)` | higher L scaled by `silu(‖v‖)/‖v‖` | norm is invariant, scaling is safe |
| `scatter_add(values, targets, n)` | edges → nodes accumulation | many-to-one message aggregation |
| `gather(features, sources)` | nodes → edges collection | one-to-one |

Weight shapes are `DepIdx` types over paths/blocks (`WeightIdx<cfg>`,
`LinearWeightIdx`), so a wrong-shaped weight array is a type error, not a
training-time surprise.

### 9. Reynolds interactions (why this module wants the core symmetry system)

- **Symmetric message passing**: undirected edges via `reynolds(interaction)` —
  symmetric output from an asymmetric kernel, 2× triangular savings, 4× with
  identical arrays.
- **CG exchange symmetry**: for L1 = L2, `cg[m1,m2,m] = ±cg[m2,m1,m]` gives
  2–4× on self-tensor-products (antisymmetric paths vanish).
- **Higher-order interactions**: n-body kernels under `reynolds` get n!
  (triangular) × n! (identity collapse) — 36× at n=3, 576× at n=4.
- **Antisymmetric Reynolds**: determinant-like alternating sums;
  diagonal terms vanish by construction.

Note (post-Coq correction): these factorial counts are per identity group over
the compound iteration space, per the corrected product-symmetry doctrine
(formalism §12, proofs.md). The n-body speedup table is the r! joint speedup —
it does not claim per-dimension factorization.

### 10. Worked example

The equivariant convolution (`blade_ml_spec_v10.md` §12): gather source
features per edge, `Y_to<2>` on edge vectors, `tensor_product` into messages,
scatter-add to targets. Composes items 5–8 plus core loop objects; also the
canonical consumer of [graphs-trees.md](graphs-trees.md) trace indices once
those land.

### 11. AD posture

**Reverse-mode `grad` is implemented (v7, v1 subset)** as an AST-level
source-to-source transform (`Grad.fs`, pre-typecheck): the synthesized
derivative is ordinary Blade source that flows through the standard
typechecker/lowering/codegen — so gradients of symmetric computations will
inherit triangular storage from the existing symmetry system rather than
from AD-specific logic.

**ABI.** `grad(f)` (call-shaped special form; `f` a same-module top-level
function returning `Float`) rewrites to `f__grad`, whose signature is f's
parameters followed by **one `mut` out-buffer per Float-array parameter**
(same type; ACCUMULATED into — callers zero them, PyTorch-style), returning
the primal, or `(primal, dscalar…)` when f has Float scalar parameters.
Int/int-array parameters (edge lists, sizes) are non-differentiable. Data
enters by module-scope capture, so a loss function's parameters are exactly
its trainables.

**v1 subset** (clean errors outside it): lets, additive accumulation
(`+=`/`-=`, scalar and array-element), element construction writes, nested
`for-in` over int ranges, scalar arithmetic and the math intrinsics, array
reads at data-dependent indices (gather; adjoint is scatter), and calls to
other AD-able functions (inlined). Adjoint loops run in the same direction
— exact for the accumulation subset; the discipline that makes it exact is
enforced (no non-additive scalar overwrites, no reads of loop-outliving
accumulators mid-loop, no array recurrences, no read-then-later-write).
Loop bodies are replayed inside the adjoint loop (recompute-based; no tape).

**Verification** follows the module's differential-oracle stance: hand VJPs
+ finite differences + gradient-rotation-invariance in `ml/`
(`Autodiff.fs`, `Tests_Autodiff.fs`), value-pinned corpus tests (`ad/`),
and the end-to-end training example (`ml-e2e/001`) whose loss trajectory,
gradient snapshots, and final weights reproduce `ml/TrainingOracle.fs` to
printed precision — including loss AND gradients invariant under rotated
inputs.

Remaining (see [future.md](../future.md) §2.1): combinator rules
(`<@>`/`>>@`/…), triangular-tape exploitation for symmetric intermediates,
forward mode, wrt-lists, if/match in differentiated code, taping for
nonlinear loop recurrences, stencil/decomposition interactions, framework
bindings.

### 11b. v7 implementation status (ops elaboration)

The ops landed in v7 (2026-07-12) as **compile-time elaboration to Blade
source** (`MLSpec.fs` + `MLElaborate.fs` + `WignerTables.fs`; user
decision over opaque builtins): for each op × static config used, the
compiler synthesizes an ordinary Blade function with real-basis CG tables
baked as constants, so `grad()` differentiates through the generated ops
via its normal inliner and codegen is unchanged.

Surface (v1 — ordinary required-static arguments; angle-bracket static
args are future sugar):

```blade
let static spec_h = [(0, 0, 2), (1, 1, 2), (2, 0, 1)]   // (l, parity, mult), parity 0=e/1=o
let static cfg1  = (spec_in, sh_spec(2), spec_h)         // (spec1, spec2, specOut)
let static w1dim = tp_weight_dim(cfg1)                   // sizing builtins:
let static w2dim = linear_weight_dim(spec_h, spec_h)     // total_dim, sh_spec, ...

let sh  = y_to(2, x, y, z)                    // real solid harmonics, lmax <= 2 (v1)
let out = tensor_product(cfg1, x1, sh, w)     // uvw fully-connected, path-validated
let z   = linear(spec_in, spec_out, w, x)     // block-diagonal, first-match blocks
let g   = gated(spec, x)                      // scalar double-duty gates (F2 rule)

// batched row forms over flat row-major storage (N static): the per-node
// case of graph networks, with no hand-written row-extract/write-back loops
let g1 = gated_rows(spec, N, x_rows)
let h1 = linear_rows(spec_in, spec_out, N, w, x_rows)
```

Checks at elaboration: `all_valid_outputs` for tensor products, block-0
scalars for `gated`, static-ness of configs — all clean compile errors.
Value pins: corpus `ml-ops/` (op-level) and `ml-e2e/002`, which re-runs the
entire §10 training example through elaborated ops and reproduces the
`ml/TrainingOracle` pins exactly (same loop order and product association
as the reference — agreement to the ulp).

**CGIndex basis decision (F1 resolution, user-guided)**: the complex-basis
rule `m1 + m2 == m_out` and the real-basis support are DIFFERENT
constraints, so they get different types. `CGIndex` = the real-basis
sparse support (what the spec's own real harmonics and `tensor_product`
need; iteration = the compiler's real-CG nonzero entries).
`CGIndexComplex` = the m-selection rule, reserved for complex-basis
pipelines. The §7 struct sketch's `where m1 + m2 == m_out` describes
`CGIndexComplex`, not the type the ops consume.

Not yet in v7: `IrrepsIdx<spec>` as an index type (primitive, SymIdx-style
— design settled), dependent records for user-defined `CGPath` (future.md
§1.9), `y_to` above lmax 2, angle-bracket static args, per-edge fused
convolution elaboration.

### 12. Open items

From ml-spec §13 plus module-level gaps:

1. Path filtering (skip zero-weight paths at compile time)
2. Equivariant attention
3. GPU fused-kernel codegen for tensor products
4. Sparse tensor products (compile-time path pruning from weight structure)
5. Memory layout choice: block-contiguous vs m-contiguous per operation
6. `poly(...)` × equivariance (arity-polymorphic equivariant kernels)
7. User-defined representations beyond built-in L0..Ln
8. Automatic CG path enumeration
