# Blade-DSL: Equivariant Machine Learning Extensions

**Version**: Draft 0.3  
**Status**: Design specification  
**Prerequisites**: Blade formalism v10

---

## Table of Contents

- [1. Overview](#1-overview)
    - [1.1 Goals](#11-goals)
    - [1.2 Comparison with e3nn](#12-comparison-with-e3nn)
- [2. Representations and Irreps](#2-representations-and-irreps)
    - [2.1 Basic Types](#21-basic-types)
    - [2.2 Irreps Specification](#22-irreps-specification)
    - [2.3 Parity Multiplication](#23-parity-multiplication)
- [3. Index Types for Irreps](#3-index-types-for-irreps)
    - [3.1 IrrepsIdx](#31-irrepsidx)
    - [3.2 DepIdx Review](#32-depidx-review)
    - [3.3 Relationship to Other Index Types](#33-relationship-to-other-index-types)
    - [3.4 Index Type Compatibility with range<>](#34-index-type-compatibility-with-range)
- [4. Clebsch-Gordan Coefficients](#4-clebsch-gordan-coefficients)
    - [4.1 Sparse CG Index](#41-sparse-cg-index)
    - [4.2 CG Coefficient Tables](#42-cg-coefficient-tables)
    - [4.3 CG Selection Rules](#43-cg-selection-rules)
- [5. Tensor Product Operation](#5-tensor-product-operation)
    - [5.1 Contributing Pairs](#51-contributing-pairs)
    - [5.2 Path Indexing](#52-path-indexing)
    - [5.3 Weight Array Structure](#53-weight-array-structure)
    - [5.4 Tensor Product Implementation](#54-tensor-product-implementation)
- [6. Spherical Harmonics](#6-spherical-harmonics)
    - [6.1 Type Signature](#61-type-signature)
    - [6.2 Implementation](#62-implementation)
- [7. Equivariant Linear Layer](#7-equivariant-linear-layer)
    - [7.1 Weight Structure](#71-weight-structure)
    - [7.2 Implementation](#72-implementation)
- [8. Activation Functions](#8-activation-functions)
    - [8.1 Gated Activation](#81-gated-activation)
    - [8.2 Norm Activation](#82-norm-activation)
    - [8.3 Standard Nonlinearities (Scalars Only)](#83-standard-nonlinearities-scalars-only)
- [9. Message Passing Primitives](#9-message-passing-primitives)
    - [9.1 Scatter](#91-scatter)
    - [9.2 Gather](#92-gather)
- [10. Automatic Differentiation](#10-automatic-differentiation)
    - [10.1 Design Approach](#101-design-approach)
    - [10.2 Primitive Differentiation Rules](#102-primitive-differentiation-rules)
    - [10.3 Symmetry Preservation](#103-symmetry-preservation)
- [11. Type Checking Rules](#11-type-checking-rules)
    - [11.1 Tensor Product Validity](#111-tensor-product-validity)
    - [11.2 Equivariant Linear Validity](#112-equivariant-linear-validity)
    - [11.3 Equivariance Annotations](#113-equivariance-annotations)
- [12. Complete Example: Equivariant Convolution](#12-complete-example-equivariant-convolution)
- [13. Future Work](#13-future-work)
    - [13.1 P1 Features (Not Yet Specified)](#131-p1-features-not-yet-specified)
    - [13.2 Open Questions](#132-open-questions)
- [14. Reynolds Operator Applications](#14-reynolds-operator-applications)
    - [14.1 Symmetric Message Passing](#141-symmetric-message-passing)
    - [14.2 CG Tensor Product Speedup Analysis](#142-cg-tensor-product-speedup-analysis)
    - [14.3 Higher-Order Interactions](#143-higher-order-interactions)
    - [14.4 Antisymmetric Applications](#144-antisymmetric-applications)
- [Appendix A: Summary of Constructs](#appendix-a-summary-of-constructs)
    - [A.1 Types](#a1-types)
    - [A.2 Static Functions](#a2-static-functions)
    - [A.3 Operations](#a3-operations)

---

## 1. Overview

This document specifies extensions to Blade-DSL for equivariant machine learning, enabling type-safe, efficient implementations of E(3)-equivariant neural networks. The design builds on Blade's existing loop combinators, index types, and symmetry tracking.

### 1.1 Goals

1. **Type-safe irreps**: Representation structure encoded in types, errors at compile time
2. **Zero-overhead equivariance**: No runtime cost for symmetry tracking
3. **Composable primitives**: Build complex layers from simple, verified components
4. **Automatic differentiation**: AD preserves symmetry and efficiency

### 1.2 Comparison with e3nn

| Aspect | e3nn | Blade |
|--------|------|-------|
| Irreps specification | String (`"16x0e + 8x1o"`) | Static spec array |
| Error detection | Runtime (during training) | Compile time |
| Tensor wrapping | `GeometricTensor` wrapper | Native arrays with `IrrepsIdx` |
| Symmetry tracking | Runtime metadata | Type system |
| CG iteration | Dense matrices | Sparse index types |

---

## 2. Representations and Irreps

### 2.1 Basic Types

```blade
// Parity under spatial inversion
type Parity = Even | Odd

// Irrep parameterized by angular momentum L and parity p
type Irrep<L: Nat, p: Parity>

// Named irrep types
type L0e = Irrep<0, Even>
type L0o = Irrep<0, Odd>
type L1e = Irrep<1, Even>
type L1o = Irrep<1, Odd>
type L2e = Irrep<2, Even>
type L2o = Irrep<2, Odd>

// Static functions for irrep properties
static function dim<L, p>(ir: Irrep<L, p>) -> Nat = 2 * L + 1

static function parity<L, p>(ir: Irrep<L, p>) -> Parity = p

static function angular_momentum<L, p>(ir: Irrep<L, p>) -> Nat = L
```

### 2.2 Irreps Specification

An irreps specification is a static array of `(Irrep, Multiplicity)` pairs:

```blade
// Spec entry: (irrep, multiplicity)
type IrrepSpec = (Irrep, Nat)

// Example specs (static)
static spec = [(L0e, 16), (L1o, 8), (L2e, 4)]
// Meaning: 16 scalars, 8 vectors, 4 rank-2 tensors
// Total dimension: 16*1 + 8*3 + 4*5 = 60

// Accessors
static function irrep(entry: IrrepSpec) -> Irrep =
    let (ir, _) = entry
    ir

static function mult(entry: IrrepSpec) -> Nat =
    let (_, m) = entry
    m

// Derived properties
static function block_dim(entry: IrrepSpec) -> Nat =
    mult(entry) * dim(irrep(entry))

static function total_dim(spec: Array<IrrepSpec>) -> Nat =
    method_for(spec) <@> lambda(entry) -> block_dim(entry) |> sum
```

### 2.3 Parity Multiplication

```blade
static function parity_mul(p1: Parity, p2: Parity) -> Parity =
    match (p1, p2) with
    | (Even, Even) -> Even
    | (Odd, Odd) -> Even
    | (Even, Odd) -> Odd
    | (Odd, Even) -> Odd
```

---

## 3. Index Types for Irreps

### 3.1 IrrepsIdx

`IrrepsIdx<spec>` is a dependent index type over a static spec array:

```blade
type BlockIdx<spec> = Idx<length(spec)>

// IrrepsIdx: for each block, a (multiplicity, m-component) pair
type IrrepsIdx<spec> = DepIdx<
    BlockIdx<spec>,
    lambda(b) -> Idx<mult(spec(b))>, Idx<dim(irrep(spec(b)))>
>

// Total extent
extent(IrrepsIdx<spec>) = total_dim(spec)
```

Iteration over `IrrepsIdx` yields three-level indices: `(block, multiplicity, m-component)`.

### 3.2 DepIdx Review

`DepIdx<I, f>` iterates over `(i, j)` pairs where `j : f(i)`:

```blade
type DepIdx<I: IndexType, f: I -> IndexType>: IndexType where
    // extent is sum of f(i) extents for all i in I
    static function extent<I, f>() -> Nat =
        sum(method_for(range<I>) <@> lambda(i) -> extent(f(i)))
```

For `IrrepsIdx`, the inner index type is a product of multiplicity and m-component indices.

### 3.3 Relationship to Other Index Types

| Type | Description | 
|------|-------------|
| `Idx<n>` | Contiguous integers 0..n-1 |
| `Idx<m>, Idx<n>` | Two dimensions (curried) |
| `DepIdx<I, f>` | Dependent: `(i, j)` where `j: f(i)` |
| `IrrepsIdx<spec>` | `DepIdx` specialized for irreps block structure |
| `SparseIdx<entries>` | Sparse: only specified entries valid |

### 3.4 Index Type Compatibility with `range<>`

Coiteration requires shared named index types (see Formalism §4.3.2). Anonymous `Idx<n>` occurrences each get distinct identities:

```blade
// Define named index types for coiteration
type EdgeIdx = Idx<E>
type BlockIdx = Idx<length(spec)>

let values: Array<Float like EdgeIdx, IrrepsIdx<spec>>
let targets: Array<Nat like EdgeIdx>

for (range<EdgeIdx>, targets, values)
<@> lambda(e, tgt, val) ->
    // body uses e, tgt, val
```

The implementations in this spec elide these definitions for brevity.

---

## 4. Clebsch-Gordan Coefficients

### 4.1 CG Path Type

A tensor product path bundles the angular momenta with their selection rule constraints:

```blade
struct CGPath {
    l1: Nat<angular_momentum>,
    l2: Nat<angular_momentum>,
    l_out: Nat<angular_momentum, min=abs(l1 - l2), max=l1 + l2>,
    p1: Parity,
    p2: Parity,
    p_out: Parity
} where p_out == parity_mul(p1, p2)
```

The `l_out` bounds and parity constraint are enforced by the type.

### 4.2 CG Index Type

The CG index is parameterized by a path, inheriting its L values for bounds:

```blade
struct CGIndex<path: CGPath> {
    m1: Int<m_component, min=-path.l1, max=path.l1>,
    m2: Int<m_component, min=-path.l2, max=path.l2>,
    m_out: Int<m_component, min=-path.l_out, max=path.l_out>
} where m1 + m2 == m_out
```

### 4.3 CG Coefficient Lookup

```blade
static function cg<path: CGPath>(idx: CGIndex<path>) -> Float
```

The compiler generates CG tables at compile time for all paths used.

---

## 5. Tensor Product Operation

### 5.1 Configuration

A tensor product configuration defines the specs and derives all dependent structure:

```blade
struct TensorProductConfig {
    spec1: Array<IrrepSpec>,
    spec2: Array<IrrepSpec>,
    spec_out: Array<IrrepSpec>
}
```

### 5.2 Paths and Block Indices

Paths are derived statically from the config. Each path includes block indices:

```blade
struct TensorPath<cfg: TensorProductConfig> {
    path: CGPath,
    in1_b: Idx<length(cfg.spec1)>,
    in2_b: Idx<length(cfg.spec2)>,
    out_b: Idx<length(cfg.spec_out)>
} where path.l1 == angular_momentum(irrep(cfg.spec1(in1_b)))
    && path.l2 == angular_momentum(irrep(cfg.spec2(in2_b)))
    && path.l_out == angular_momentum(irrep(cfg.spec_out(out_b)))
    && path.p1 == parity(irrep(cfg.spec1(in1_b)))
    && path.p2 == parity(irrep(cfg.spec2(in2_b)))
    && path.p_out == parity(irrep(cfg.spec_out(out_b)))

// All valid paths for a config (static)
static function paths<cfg: TensorProductConfig>() -> Array<TensorPath<cfg>>

type TensorPaths<cfg: TensorProductConfig> = SparseIdx<paths<cfg>()>
```

### 5.3 Input and Weight Types

Inputs and weights are parameterized by the config:

```blade
type In1<cfg: TensorProductConfig> = Array<Float like IrrepsIdx<cfg.spec1>>
type In2<cfg: TensorProductConfig> = Array<Float like IrrepsIdx<cfg.spec2>>
type Weights<cfg: TensorProductConfig> = Array<Float like WeightIdx<cfg>>
type Out<cfg: TensorProductConfig> = Array<Float like IrrepsIdx<cfg.spec_out>>

type WeightIdx<cfg: TensorProductConfig> = DepIdx<
    TensorPaths<cfg>,
    lambda(tp) -> 
        Idx<mult(cfg.spec_out(tp.out_b))>, 
        Idx<mult(cfg.spec1(tp.in1_b))>, 
        Idx<mult(cfg.spec2(tp.in2_b))>
>
```

### 5.4 Tensor Product Implementation

```blade
function tensor_product<cfg: TensorProductConfig>(
    in1: In1<cfg>,
    in2: In2<cfg>,
    weights: Weights<cfg>
) -> Out<cfg> =
    
    let out = zero<IrrepsIdx<cfg.spec_out>>
    
    for range<TensorPaths<cfg>>
    <@> lambda(tp) ->
        for (range<MultIdx<tp>>, weights(tp))
        <@> lambda((mult_out, mult1, mult2), w) ->
            for range<CGIndex<tp.path>>
            <@> lambda(idx) ->
                let cg_val = cg<tp.path>(idx)
                out(tp.out_b, mult_out, idx.m_out + tp.path.l_out) +=
                    cg_val * w * 
                    in1(tp.in1_b, mult1, idx.m1 + tp.path.l1) * 
                    in2(tp.in2_b, mult2, idx.m2 + tp.path.l2)
    
    out
```

---

## 6. Spherical Harmonics

### 6.1 Type Signature

```blade
module SphericalHarmonics {
    // Single degree L
    static function Y<L: Nat>(v: Array<Float like Idx<3>>) 
        -> Array<Float like Idx<2*L+1>>
    
    // All degrees 0..L_max concatenated
    static function Y_to<L_max: Nat>(v: Array<Float like Idx<3>>)
        -> Array<Float like IrrepsIdx<sh_spec<L_max>>>
}

// Spec for spherical harmonics up to L_max: one copy of each L with alternating parity
// sh_spec<L_max> = [(L0e, 1), (L1o, 1), (L2e, 1), ...]
static function sh_spec<L_max: Nat> -> Array<IrrepSpec>
```

### 6.2 Implementation

```blade
static function Y_to<L_max>(v: Array<Float like Idx<3>>) 
    -> Array<Float like IrrepsIdx<sh_spec<L_max>>> =
    
    let x = v(0)
    let y = v(1)
    let z = v(2)
    let out = zero<IrrepsIdx<sh_spec<L_max>>>
    
    // L = 0
    out(0, 0, 0) = 0.28209479                           // Y_0^0
    
    // L = 1
    out(1, 0, 0) = 0.48860251 * y                       // Y_1^{-1}
    out(1, 0, 1) = 0.48860251 * z                       // Y_1^0
    out(1, 0, 2) = 0.48860251 * x                       // Y_1^1
    
    // L = 2
    out(2, 0, 0) = 1.09254843 * x * y                   // Y_2^{-2}
    out(2, 0, 1) = 1.09254843 * y * z                   // Y_2^{-1}
    out(2, 0, 2) = 0.31539157 * (3*z*z - (x*x+y*y+z*z)) // Y_2^0
    out(2, 0, 3) = 1.09254843 * x * z                   // Y_2^1
    out(2, 0, 4) = 0.54627421 * (x*x - y*y)             // Y_2^2
    
    // Higher L computed via recurrence
    
    out
```

---

## 7. Equivariant Linear Layer

Equivariant linear layers mix multiplicities within each irrep block but cannot mix between different irreps (that would break equivariance).

### 7.1 Weight Structure

```blade
// Find block index for a given irrep in a spec
static function find_block_idx(spec, ir: Irrep) -> Nat

type BlockIdx<spec> = Idx<length(spec)>

// Weights: for each output block, a [mult_out, mult_in] matrix
type LinearWeightIdx<spec_in, spec_out> = DepIdx<
    BlockIdx<spec_out>,
    lambda(b) -> 
        let ir = irrep(spec_out(b))
        let in_b = find_block_idx(spec_in, ir)
        Idx<mult(spec_out(b))>, Idx<mult(spec_in(in_b))>
>
```

### 7.2 Implementation

```blade
function linear<spec_in, spec_out>(
    input: Array<Float like IrrepsIdx<spec_in>>,
    weights: Array<Float like LinearWeightIdx<spec_in, spec_out>>
) -> Array<Float like IrrepsIdx<spec_out>> =
    
    let out = zero<IrrepsIdx<spec_out>>
    
    // Iterate over output blocks
    for range<BlockIdx<spec_out>>
    <@> lambda(out_b) ->
        let ir = irrep(spec_out(out_b))
        let in_b = find_block_idx(spec_in, ir)
        let L = angular_momentum(ir)
        
        // Matrix multiply over multiplicities, shared across m-components
        for range<MultIdx<spec_out, out_b>>
        <@> lambda(m_out) ->
            for range<MultIdx<spec_in, in_b>>
            <@> lambda(m_in) ->
                let w = weights(out_b, m_out, m_in)
                for range<MIdx<L>>
                <@> lambda(c) ->
                    out(out_b, m_out, c) += w * input(in_b, m_in, c)
    
    out
```

---

## 8. Activation Functions

Nonlinearities can only be applied directly to scalars (L=0) without breaking equivariance. Higher-L features are either gated by scalars or scaled by a function of their norm.

### 8.1 Gated Activation

```blade
function gated_activation<spec>(
    features: Array<Float like IrrepsIdx<spec>>
) -> Array<Float like IrrepsIdx<spec>> =
    
    let out = zero<IrrepsIdx<spec>>
    let num_gates = mult(spec(0))  // first block is scalars, used as gates
    
    for range<BlockIdx<spec>>
    <@> lambda(b) ->
        let L = angular_momentum(irrep(spec(b)))
        
        if L == 0 then
            // Scalars: apply nonlinearity directly
            for range<MultIdx<spec, b>>
            <@> lambda(m) ->
                for range<MIdx<L>>
                <@> lambda(c) ->
                    out(b, m, c) = silu(features(b, m, c))
        else
            // Higher-L: gate by corresponding scalar
            for range<MultIdx<spec, b>>
            <@> lambda(m) ->
                let gate_idx = m % num_gates
                let g = sigmoid(features(0, gate_idx, 0))
                for range<MIdx<L>>
                <@> lambda(c) ->
                    out(b, m, c) = g * features(b, m, c)
    
    out
```

### 8.2 Norm Activation

```blade
function norm_activation<spec>(features: Array<Float like IrrepsIdx<spec>>)
    -> Array<Float like IrrepsIdx<spec>> =
    
    let out = zero<IrrepsIdx<spec>>
    
    for range<BlockIdx<spec>>
    <@> lambda(b) ->
        let L = angular_momentum(irrep(spec(b)))
        
        if L == 0 then
            for range<MultIdx<spec, b>>
            <@> lambda(m) ->
                out(b, m, 0) = silu(features(b, m, 0))
        else
            for range<MultIdx<spec, b>>
            <@> lambda(m) ->
                let v = features(b, m)
                let norm_sq = for v <@> lambda(x) -> x * x |> sum
                let norm = sqrt(norm_sq)
                let scale = silu(norm) / (norm + 1e-8)
                for range<MIdx<L>>
                <@> lambda(c) ->
                    out(b, m, c) = features(b, m, c) * scale
    
    out
```

### 8.3 Standard Nonlinearities (Scalars Only)

```blade
static function relu(x: Float) -> Float = if x > 0 then x else 0
static function silu(x: Float) -> Float = x * sigmoid(x)
static function sigmoid(x: Float) -> Float = 1 / (1 + exp(-x))
```

---

## 9. Message Passing Primitives

### 9.1 Scatter

Accumulate values at target indices (many-to-one):

```blade
type EdgeIdx = Idx<E>
type NodeIdx = Idx<num_targets>

function scatter_add<spec>(
    values: Array<Float like EdgeIdx, IrrepsIdx<spec>>,
    targets: Array<Nat like EdgeIdx>,
    num_targets: Nat
) -> Array<Float like NodeIdx, IrrepsIdx<spec>> =
    
    let out = zero<NodeIdx, IrrepsIdx<spec>>
    
    for (range<EdgeIdx>, targets, values)
    <@> lambda(e, tgt, val) ->
        for range<IrrepsIdx<spec>>
        <@> lambda((b, m, c)) ->
            out(tgt, b, m, c) += val(b, m, c)
    
    out
```

### 9.2 Gather

Collect values from source indices (one-to-one):

```blade
function gather<spec>(
    features: Array<Float like NodeIdx, IrrepsIdx<spec>>,
    sources: Array<Nat like EdgeIdx>
) -> Array<Float like EdgeIdx, IrrepsIdx<spec>> =
    
    let out = zero<EdgeIdx, IrrepsIdx<spec>>
    
    for (range<EdgeIdx>, sources)
    <@> lambda(e, src) ->
        for range<IrrepsIdx<spec>>
        <@> lambda((b, m, c)) ->
            out(e, b, m, c) = features(src, b, m, c)
    
    out
```

---

## 10. Automatic Differentiation

### 10.1 Design Approach

AD is a **compiler-supported library**: built from Blade primitives, but the compiler can recognize and optimize AD patterns.

### 10.2 Primitive Differentiation Rules

```blade
module AD {
    module forward {
        let add = lambda((a, da), (b, db)) -> (a + b, da + db)
        let mul = lambda((a, da), (b, db)) -> (a * b, a * db + da * b)
        let exp = lambda((a, da)) -> let e = exp(a) in (e, e * da)
        
        // CG coefficients are constants: zero derivative
        let cg_deriv = lambda(L1, L2, L_out, m1, m2, m_out) ->
            (cg<L1, L2, L_out>(m1, m2, m_out), 0.0)
    }
    
    module reverse {
        let add = lambda(a, b, g) -> (g, g)
        let mul = lambda(a, b, g) -> (b * g, a * g)
        let exp = lambda(a, g) -> exp(a) * g
        
        // CG coefficients: no gradient (constants)
        let cg = lambda(L1, L2, L_out, c1, c_out, g) -> ()
    }
}
```

### 10.3 Symmetry Preservation

From the formalism: if a computation produces symmetric output, its Jacobian inherits corresponding symmetry. The sparse CG structure means:

- Forward: iterate only over nonzero CG entries
- Backward: same sparse iteration in reverse
- Tape storage: proportional to sparse CG size, not dense

---

## 11. Type Checking Rules

### 11.1 Tensor Product Validity

```blade
tensor_product<spec1, spec2, spec_out>(input1, input2, weights)
    where all_valid_outputs(spec1, spec2, spec_out)

// All output irreps must be reachable from some input pair
static function all_valid_outputs(spec1, spec2, spec_out) -> Bool
```

### 11.2 Equivariant Linear Validity

```blade
linear<spec_in, spec_out>(input, weights)
    where all_irreps_present(spec_in, spec_out)

// All output irreps must exist in input
static function all_irreps_present(spec_in, spec_out) -> Bool
```

### 11.3 Equivariance Annotations

```blade
let v: Array<Float like Idx<3>> with equiv(SO<3>, L1)
let s: Array<Float like Idx<1>> with equiv(SO<3>, L0)

v + v    // OK: same representation
v + s    // Compile error: cannot add L1 and L0

Y<2>(v)  // OK: L1 -> L2 via spherical harmonics
```

---

## 12. Complete Example: Equivariant Convolution

```blade
static spec_in = [(L0e, 16), (L1o, 8)]
static spec_sh = [(L0e, 1), (L1o, 1), (L2e, 1)]
static spec_out = [(L0e, 32), (L1o, 16), (L2e, 8)]

type NodeIdx = Idx<N>
type EdgeIdx = Idx<E>

function equivariant_conv(
    node_features: Array<Float like NodeIdx, IrrepsIdx<spec_in>>,
    edge_index: Array<Nat like EdgeIdx, Idx<2>>,
    edge_vectors: Array<Float like EdgeIdx, Idx<3>>,
    weights: Array<Float like WeightIdx<spec_in, spec_sh, spec_out>>
) -> Array<Float like NodeIdx, IrrepsIdx<spec_out>> =
    
    let out = zero<NodeIdx, IrrepsIdx<spec_out>>
    
    for (range<EdgeIdx>, edge_index, edge_vectors)
    <@> lambda(e, edge_idx, edge_vec) ->
        let src = edge_idx(0)
        let tgt = edge_idx(1)
        
        let edge_sh = SphericalHarmonics.Y_to<2>(edge_vec)
        let src_feat = node_features(src)
        
        let message = tensor_product<spec_in, spec_sh, spec_out>(
            src_feat, edge_sh, weights)
        
        for range<IrrepsIdx<spec_out>>
        <@> lambda((b, m, c)) ->
            out(tgt, b, m, c) += message(b, m, c)
    
    out
```

---

## 13. Future Work

### 13.1 P1 Features (Not Yet Specified)

| Feature | Status | Notes |
|---------|--------|-------|
| Path filtering | Not started | Skip zero-weight paths at compile time |
| Attention mechanisms | Not started | Equivariant attention |
| GPU code generation | Not started | Fused kernels |

### 13.2 Open Questions

1. **Sparse tensor products**: Compile-time path pruning based on weight structure?
2. **Memory layout**: Block-contiguous vs m-contiguous for different operations?

---

## 14. Reynolds Operator Applications

The Reynolds operator (see Formalism §6.4) enables additional optimizations in equivariant ML beyond identity commutativity.

### 14.1 Symmetric Message Passing

Undirected edge computations have natural Reynolds structure:

```blade
// Non-commutative interaction kernel: order of arguments matters
let interaction = lambda(a, b) -> 
    let diff = a - b
    let dist = sqrt(dot(diff, diff))
    a * exp(-dist)  // asymmetric: weighted by first argument

// Reynolds wraps the kernel to symmetrize over argument permutations
let symmetric_message = method_for(feat_i, feat_j) <@> reynolds(interaction)
// Computes: interaction(feat_i, feat_j) + interaction(feat_j, feat_i)
// Result is symmetric even though interaction itself is not
```

Benefits: 2× iteration savings (triangular), additional 2× if arrays identical at call site.

### 14.2 CG Tensor Product Speedup Analysis

The CG tensor product structure is:

```
output[m_out] = Σ_{m1 + m2 = m_out} cg[m1, m2, m_out] * in1[m1] * in2[m2]
```

CG coefficients satisfy exchange symmetry when L1 = L2:

```
cg[m1, m2, m_out] = ±cg[m2, m1, m_out]
```

**Speedup analysis:**

| Configuration | Speedup | Mechanism |
|---------------|---------|-----------|
| General (diff arrays, diff L) | 1× | No symmetry |
| Same spec, diff arrays | ~2× | Block pair identity |
| Self-TP, same-block, CG sym | 4× | Mult identity × CG identity |
| Self-TP, same-block, CG antisym | 0 | Vanishes |

**Overall: 2×–4× for typical self-tensor-products.**

### 14.3 Higher-Order Interactions

For n-way interactions, Reynolds provides dramatic speedups:

| Arity | Reynolds Only | Reynolds + Identity |
|-------|---------------|---------------------|
| n=2 | 2× | 4× |
| n=3 | 6× | 36× |
| n=4 | 24× | 576× |

Example of 3-way symmetric interaction:

```blade
// 3-body interaction kernel (non-commutative)
let three_body = lambda(a, b, c) ->
    let ab = dot(a, b)
    let bc = dot(b, c)
    let ca = dot(c, a)
    ab * bc + ca  // asymmetric in arguments

// Reynolds symmetrizes: sums over all 6 permutations of S₃
let symmetric_3body = method_for(A, B, C) <@> reynolds(three_body)
// Computes: Σ_{σ ∈ S₃} three_body(args_{σ(1)}, args_{σ(2)}, args_{σ(3)})
// = three_body(a,b,c) + three_body(a,c,b) + three_body(b,a,c) 
//   + three_body(b,c,a) + three_body(c,a,b) + three_body(c,b,a)
```

### 14.4 Antisymmetric Applications

Antisymmetric Reynolds applies to cross-product-like computations:

```blade
// Asymmetric 3-argument kernel
let wedge_term = lambda(a, b, c) ->
    a(0) * b(1) * c(2)  // single term of determinant

// Antisymmetric Reynolds: alternating sum over S₃ weighted by permutation sign
let determinant_like = method_for(A, B, C) <@> reynolds(wedge_term, Antisymmetric)
// Computes: Σ_{σ ∈ S₃} sign(σ) · wedge_term(args_{σ(1)}, args_{σ(2)}, args_{σ(3)})
// = wedge_term(a,b,c) - wedge_term(a,c,b) - wedge_term(b,a,c) 
//   + wedge_term(b,c,a) + wedge_term(c,a,b) - wedge_term(c,b,a)
```

Properties: Diagonal terms vanish (when any two arguments equal), transpose terms negated.

---

## Appendix A: Summary of Constructs

### A.1 Types

| Type | Description |
|------|-------------|
| `Parity` | `Even \| Odd` |
| `Irrep<L, p>` | Parameterized by angular momentum L and parity p |
| `IrrepSpec` | `(Irrep, Nat)` — irrep with multiplicity |
| `IrrepsIdx<spec>` | `DepIdx` over static spec array |
| `TensorProductConfig` | Bundles spec1, spec2, spec_out |
| `CGPath` | Dependent record: L values and parities with selection rules |
| `CGIndex<path>` | Constrained record for CG indices, parameterized by path |
| `TensorPath<cfg>` | CGPath with block indices, parameterized by config |
| `In1<cfg>, In2<cfg>, Weights<cfg>, Out<cfg>` | Arrays parameterized by config |

### A.2 Static Functions

| Function | Signature | Description |
|----------|-----------|-------------|
| `dim` | `Irrep -> Nat` | Dimension of irrep (2L+1) |
| `parity` | `Irrep -> Parity` | Parity of irrep |
| `angular_momentum` | `Irrep -> Nat` | L value of irrep |
| `total_dim` | `Array<IrrepSpec> -> Nat` | Total dimension of spec |
| `paths<cfg>` | `() -> Array<TensorPath<cfg>>` | Valid paths for a config |
| `cg<path>` | `CGIndex<path> -> Float` | Clebsch-Gordan coefficient |

### A.3 Operations

| Function | Description |
|----------|-------------|
| `tensor_product` | Equivariant tensor product with CG coefficients |
| `SphericalHarmonics.Y<L>` | Single-degree spherical harmonics |
| `SphericalHarmonics.Y_to<L_max>` | Concatenated spherical harmonics |
| `EquivariantLinear.linear` | Block-diagonal equivariant linear |
| `gated_activation` | Scalar-gated nonlinearity |
| `norm_activation` | Norm-based nonlinearity |
| `scatter_add` | Accumulate values at target indices |
| `gather` | Collect values from source indices |
