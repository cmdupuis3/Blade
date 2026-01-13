# Blade-DSL: Equivariant Machine Learning Extensions

**Version**: Draft 0.2  
**Status**: Design specification  
**Prerequisites**: Blade formalism v8.17+

---

## Table of Contents

- [1. Overview](#overview)
    - [1.1 Goals](#goals)
    - [1.2 Comparison with e3nn](#comparison-with-e3nn)
- [2. Representations and Irreps](#representations-and-irreps)
    - [2.1 Basic Types](#basic-types)
    - [2.2 Irreps Specification](#irreps-specification)
    - [2.3 Parity Multiplication](#parity-multiplication)
- [3. Index Types for Irreps](#index-types-for-irreps)
    - [3.1 IrrepsIdx](#irrepsidx)
    - [3.2 DepIdx Review](#depidx-review)
    - [3.3 Relationship to Other Index Types](#relationship-to-other-index-types)
- [4. Clebsch-Gordan Coefficients](#clebsch-gordan-coefficients)
    - [4.1 Sparse CG Index](#sparse-cg-index)
    - [4.2 CG Coefficient Tables](#cg-coefficient-tables)
    - [4.3 CG Selection Rules](#cg-selection-rules)
- [5. Tensor Product Operation](#tensor-product-operation)
    - [5.1 Contributing Pairs](#contributing-pairs)
    - [5.2 Path Indexing](#path-indexing)
    - [5.3 Weight Array Structure](#weight-array-structure)
    - [5.4 Tensor Product Implementation](#tensor-product-implementation)
- [6. Spherical Harmonics](#spherical-harmonics)
    - [6.1 Type Signature](#type-signature)
    - [6.2 Implementation](#implementation)
- [7. Equivariant Linear Layer](#equivariant-linear-layer)
    - [7.1 Weight Structure](#weight-structure)
    - [7.2 Implementation](#implementation)
- [8. Activation Functions](#activation-functions)
    - [8.1 Gated Activation](#gated-activation)
    - [8.2 Norm Activation](#norm-activation)
    - [8.3 Standard Nonlinearities (Scalars Only)](#standard-nonlinearities-scalars-only)
- [9. Message Passing Primitives](#message-passing-primitives)
    - [9.1 Scatter](#scatter)
    - [9.2 Gather](#gather)
- [10. Automatic Differentiation](#automatic-differentiation)
    - [10.1 Design Approach](#design-approach)
    - [10.2 Primitive Differentiation Rules](#primitive-differentiation-rules)
    - [10.3 Symmetry Preservation](#symmetry-preservation)
- [11. Type Checking Rules](#type-checking-rules)
    - [11.1 Tensor Product Validity](#tensor-product-validity)
    - [11.2 Equivariant Linear Validity](#equivariant-linear-validity)
    - [11.3 Equivariance Annotations](#equivariance-annotations)
- [12. Complete Example: Equivariant Convolution](#complete-example-equivariant-convolution)
- [13. Future Work](#future-work)
    - [13.1 P1 Features (Not Yet Specified)](#p1-features-not-yet-specified)
    - [13.2 Open Questions](#open-questions)
- [Appendix A: Summary of Constructs](#appendix-a-summary-of-constructs)
    - [A.1 Syntax Extensions](#a1-syntax-extensions)
    - [A.2 Types](#a2-types)
    - [A.3 Static Functions](#a3-static-functions)
    - [A.4 Operations](#a4-operations)

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

// Irrep type with associated dimension
type Irrep = L0e | L0o | L1e | L1o | L2e | L2o | ...

// Static functions for irrep properties
static function dim(ir : Irrep) : Nat = match ir {
    L0e -> 1, L0o -> 1,
    L1e -> 3, L1o -> 3,
    L2e -> 5, L2o -> 5,
    ...
}

static function parity(ir : Irrep) : Parity = match ir {
    L0e -> Even, L0o -> Odd,
    L1e -> Even, L1o -> Odd,
    L2e -> Even, L2o -> Odd,
    ...
}

static function angular_momentum(ir : Irrep) : Nat = match ir {
    L0e -> 0, L0o -> 0,
    L1e -> 1, L1o -> 1,
    L2e -> 2, L2o -> 2,
    ...
}
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
static function irrep(entry : IrrepSpec) : Irrep = entry.0
static function mult(entry : IrrepSpec) : Nat = entry.1

// Derived properties
static function block_dim(entry : IrrepSpec) : Nat =
    mult(entry) * dim(irrep(entry))

static function total_dim(spec : Array<IrrepSpec>) : Nat =
    method_for(spec) <@> lambda(entry) -> block_dim(entry) |> sum
```

### 2.3 Parity Multiplication

```blade
static function parity_mul(p1 : Parity, p2 : Parity) : Parity =
    match (p1, p2) {
        (Even, Even) -> Even,
        (Odd, Odd) -> Even,
        (Even, Odd) -> Odd,
        (Odd, Even) -> Odd,
    }
```

---

## 3. Index Types for Irreps

### 3.1 IrrepsIdx

`IrrepsIdx<spec>` is a dependent index type over a static spec array:

```blade
// IrrepsIdx: dependent index over the spec
type IrrepsIdx<spec> = DepIdx<
    Idx<length(spec)>,                              // block index
    lambda(b) -> Idx<mult(spec(b))>, Idx<dim(irrep(spec(b)))>  // (mult, m) within block
>

// Total extent
extent(IrrepsIdx<spec>) = total_dim(spec)
```

Iteration over `IrrepsIdx` yields three-level indices: `(block, multiplicity, m-component)`.

### 3.2 DepIdx Review

`DepIdx<I, f>` iterates over `(i, j)` pairs where `j : f(i)`:

```blade
type DepIdx<I : IndexType, f : I -> IndexType> : IndexType where
    // extent is sum of f(i) extents for all i in I
    static function extent<I, f>() : Nat =
        object_for((+)) <@> lambda(i) -> extent(f(i))
```

For `IrrepsIdx`, the inner index type is a product of multiplicity and m-component indices.

### 3.3 Relationship to Other Index Types

| Type | Description | 
|------|-------------|
| `Idx<n>` | Contiguous integers 0..n-1 |
| `Idx<m>, Idx<n>` | Two dimensions (curried) |
| `Idx<m> * Idx<n>` | Product index (single compound) |
| `DepIdx<I, f>` | Dependent: `(i, j)` where `j : f(i)` |
| `IrrepsIdx<spec>` | `DepIdx` specialized for irreps block structure |
| `SparseIdx<entries>` | Sparse: only specified entries valid |

---

## 4. Clebsch-Gordan Coefficients

### 4.1 Sparse CG Index

The CG coefficients for `(L1, L2, L_out)` are sparse—only entries where `m1 + m2 = m_out` are nonzero:

```blade
// Valid CG triples for given angular momenta
static function valid_cg_triples(L1 : Nat, L2 : Nat, L_out : Nat) 
    : Array<(Int, Int, Int)> =
    // All (c1, c2, c_out) where c1 + c2 = c_out
    // and |c1| <= L1, |c2| <= L2, |c_out| <= L_out
    ...

// Sparse index type for CG iteration
type CGIdx<L1, L2, L_out> = SparseIdx<valid_cg_triples(L1, L2, L_out)>
```

### 4.2 CG Coefficient Tables

CG coefficients are static arrays indexed by the sparse CG index:

```blade
// Sparse CG array: only valid (c1, c2, c_out) combinations
static cg_sparse<L1, L2, L_out> : Array<Float like CGIdx<L1, L2, L_out>>

// Lookup (poly-indexing into sparse structure)
static function cg(L1, L2, L_out, c1, c_out) : Float =
    let c2 = c_out - c1
    cg_sparse<L1, L2, L_out>((c1, c2, c_out))
```

The compiler generates these tables at compile time for all `(L1, L2, L_out)` combinations used in the program.

### 4.3 CG Selection Rules

```blade
// Valid output L values for tensor product L1 ⊗ L2
static function valid_L_out(L1 : Nat, L2 : Nat) : Array<Nat> =
    // |L1 - L2| <= L_out <= L1 + L2
    range(abs(L1 - L2), L1 + L2 + 1)

// Check if path is valid
static function valid_path(ir1 : Irrep, ir2 : Irrep, ir_out : Irrep) : Bool =
    let L1 = angular_momentum(ir1)
    let L2 = angular_momentum(ir2)
    let L_out = angular_momentum(ir_out)
    let p_out = parity_mul(parity(ir1), parity(ir2))
    
    L_out in valid_L_out(L1, L2) and parity(ir_out) == p_out
```

---

## 5. Tensor Product Operation

### 5.1 Contributing Pairs

For each output block, determine which input block pairs contribute:

```blade
static function contributing_pairs(spec1, spec2, spec_out, out_b : Nat) 
    : Array<(Nat, Nat)> =
    let ir_out = irrep(spec_out(out_b))
    let pairs = []
    for b1 in 0..length(spec1) {
        for b2 in 0..length(spec2) {
            if valid_path(irrep(spec1(b1)), irrep(spec2(b2)), ir_out) {
                pairs = append(pairs, (b1, b2))
            }
        }
    }
    pairs
```

### 5.2 Path Indexing

```blade
// Collect all valid paths across all output blocks
static function all_paths(spec1, spec2, spec_out) : Array<(Nat, Nat, Nat)> =
    let paths = []
    for out_b in 0..length(spec_out) {
        let pairs = contributing_pairs(spec1, spec2, spec_out, out_b)
        for i in 0..length(pairs) {
            let pair = pairs(i)
            paths = append(paths, (pair.0, pair.1, out_b))
        }
    }
    paths

// Total number of paths
static function num_paths(spec1, spec2, spec_out) : Nat =
    length(all_paths(spec1, spec2, spec_out))

// Get path info by index
static function path_info(spec1, spec2, spec_out, p : Nat) : (Nat, Nat, Nat) =
    all_paths(spec1, spec2, spec_out)(p)

// Map (in1_block, in2_block, out_block) to sequential path index
static function path_idx(spec1, spec2, spec_out, in1_b, in2_b, out_b) : Nat =
    let paths = all_paths(spec1, spec2, spec_out)
    for i in 0..length(paths) {
        let p = paths(i)
        if p.0 == in1_b and p.1 == in2_b and p.2 == out_b {
            return i
        }
    }
    // error: path not found
```

### 5.3 Weight Array Structure

```blade
// Weights indexed by: path, output_mult, input1_mult, input2_mult
type WeightIdx<spec1, spec2, spec_out> = DepIdx<
    Idx<num_paths(spec1, spec2, spec_out)>,
    lambda(p) -> 
        let info = path_info(spec1, spec2, spec_out, p)
        Idx<mult(spec_out(info.2))>, Idx<mult(spec1(info.0))>, Idx<mult(spec2(info.1))>
>
```

### 5.4 Tensor Product Implementation

```blade
function tensor_product<spec1, spec2, spec_out>(
    in1 : Array<Float like IrrepsIdx<spec1>>,
    in2 : Array<Float like IrrepsIdx<spec2>>,
    weights : Array<Float like WeightIdx<spec1, spec2, spec_out>>
) : Array<Float like IrrepsIdx<spec_out>> =
    
    let out = zero<IrrepsIdx<spec_out>>
    
    for out_b in 0..length(spec_out) {
        let L_out = angular_momentum(irrep(spec_out(out_b)))
        let pairs = contributing_pairs(spec1, spec2, spec_out, out_b)
        
        for p in 0..length(pairs) {
            let pair = pairs(p)
            let in1_b = pair.0
            let in2_b = pair.1
            let L1 = angular_momentum(irrep(spec1(in1_b)))
            let L2 = angular_momentum(irrep(spec2(in2_b)))
            
            let w_slice = weights(path_idx(spec1, spec2, spec_out, in1_b, in2_b, out_b))
            let in1_block = in1(in1_b)
            let in2_block = in2(in2_b)
            let cg = cg_sparse<L1, L2, L_out>
            
            // Enumerated iteration over multiplicities
            method_for(w_slice) <@(m_out, m1, m2)> lambda(w) ->
                // Sparse iteration over CG coefficients
                method_for(cg) <@(c1, c2, c_out)> lambda(cg_val) ->
                    out(out_b, m_out, c_out + L_out) +=
                        cg_val * w * 
                        in1_block(m1, c1 + L1) * 
                        in2_block(m2, c2 + L2)
        }
    }
    out
```

---

## 6. Spherical Harmonics

### 6.1 Type Signature

```blade
module SphericalHarmonics {
    // Single degree L
    static function Y<L : Nat>(v : Array<Float like Idx<3>>) 
        : Array<Float like Idx<2*L+1>>
    
    // All degrees 0..L_max concatenated
    static function Y_to<L_max : Nat>(v : Array<Float like Idx<3>>)
        : Array<Float like IrrepsIdx<sh_spec<L_max>>>
}

// Spec for spherical harmonics up to L_max
static function sh_spec<L_max : Nat> : Array<IrrepSpec> =
    [(L0e, 1), (L1o, 1), (L2e, 1), ..., (L_max_irrep, 1)]
```

### 6.2 Implementation

```blade
static function Y_to<L_max>(v : Array<Float like Idx<3>>) 
    : Array<Float like IrrepsIdx<sh_spec<L_max>>> =
    
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
    
    // Higher L via recurrence or compile-time generation
    ...
    
    out
```

---

## 7. Equivariant Linear Layer

Equivariant linear layers mix multiplicities within each irrep block but cannot mix between different irreps (that would break equivariance).

### 7.1 Weight Structure

```blade
// Find block index for a given irrep in a spec
static function find_block_idx(spec, ir : Irrep) : Nat =
    for b in 0..length(spec) {
        if irrep(spec(b)) == ir {
            return b
        }
    }
    // error: irrep not found

// Weights: for each output block, a [mult_out, mult_in] matrix
type LinearWeightIdx<spec_in, spec_out> = DepIdx<
    Idx<length(spec_out)>,
    lambda(b) -> 
        let ir = irrep(spec_out(b))
        let in_b = find_block_idx(spec_in, ir)
        Idx<mult(spec_out(b))>, Idx<mult(spec_in(in_b))>
>
```

### 7.2 Implementation

```blade
function linear<spec_in, spec_out>(
    input : Array<Float like IrrepsIdx<spec_in>>,
    weights : Array<Float like LinearWeightIdx<spec_in, spec_out>>
) : Array<Float like IrrepsIdx<spec_out>> =
    
    let out = zero<IrrepsIdx<spec_out>>
    
    for out_b in 0..length(spec_out) {
        let ir = irrep(spec_out(out_b))
        let in_b = find_block_idx(spec_in, ir)
        let L = angular_momentum(ir)
        
        // Matrix multiply over multiplicities, shared across m-components
        for m_out in 0..mult(spec_out(out_b)) {
            for m_in in 0..mult(spec_in(in_b)) {
                let w = weights(out_b, m_out, m_in)
                for c in 0..(2*L+1) {
                    out(out_b, m_out, c) += w * input(in_b, m_in, c)
                }
            }
        }
    }
    out
```

---

## 8. Activation Functions

Nonlinearities can only be applied directly to scalars (L=0) without breaking equivariance. Higher-L features are either gated by scalars or scaled by a function of their norm.

### 8.1 Gated Activation

```blade
function gated_activation<spec>(
    features : Array<Float like IrrepsIdx<spec>>
) : Array<Float like IrrepsIdx<spec>> =
    
    let out = zero<IrrepsIdx<spec>>
    
    // Assume first block is scalars (L0), used as gates
    let num_gates = mult(spec(0))
    
    for b in 0..length(spec) {
        let L = angular_momentum(irrep(spec(b)))
        
        if L == 0 {
            // Scalars: apply nonlinearity directly
            for m in 0..mult(spec(b)) {
                for c in 0..dim(irrep(spec(b))) {
                    out(b, m, c) = silu(features(b, m, c))
                }
            }
        } else {
            // Higher-L: gate by corresponding scalar
            for m in 0..mult(spec(b)) {
                let gate_idx = m % num_gates  // wrap if more features than gates
                let g = sigmoid(features(0, gate_idx, 0))
                
                for c in 0..dim(irrep(spec(b))) {
                    out(b, m, c) = g * features(b, m, c)
                }
            }
        }
    }
    out
```

### 8.2 Norm Activation

```blade
function norm_activation<spec>(features : Array<Float like IrrepsIdx<spec>>)
    : Array<Float like IrrepsIdx<spec>> =
    
    let out = zero<IrrepsIdx<spec>>
    
    for b in 0..length(spec) {
        let L = angular_momentum(irrep(spec(b)))
        
        if L == 0 {
            // Scalars: apply nonlinearity directly
            for m in 0..mult(spec(b)) {
                out(b, m, 0) = silu(features(b, m, 0))
            }
        } else {
            // Higher-L: scale by f(norm) / norm
            for m in 0..mult(spec(b)) {
                // Compute norm over m-components
                let v = features(b, m)
                let norm_sq = method_for(v) <@> lambda(x) -> x * x |> sum
                let norm = sqrt(norm_sq)
                let scale = silu(norm) / (norm + 1e-8)
                
                for c in 0..dim(irrep(spec(b))) {
                    out(b, m, c) = features(b, m, c) * scale
                }
            }
        }
    }
    out
```

### 8.3 Standard Nonlinearities (Scalars Only)

```blade
static function relu(x : Float) : Float = if x > 0 then x else 0
static function silu(x : Float) : Float = x * sigmoid(x)
static function sigmoid(x : Float) : Float = 1 / (1 + exp(-x))
```

---

## 9. Message Passing Primitives

### 9.1 Scatter

Accumulate values at target indices (many-to-one):

```blade
function scatter_add<spec>(
    values : Array<Float like Idx<E>, IrrepsIdx<spec>>,
    targets : Array<Nat like Idx<E>>,
    num_targets : Nat
) : Array<Float like Idx<num_targets>, IrrepsIdx<spec>> =
    
    let out = zero<Idx<num_targets>, IrrepsIdx<spec>>
    
    for e in 0..E {
        let tgt = targets(e)
        for b in 0..length(spec) {
            for m in 0..mult(spec(b)) {
                for c in 0..dim(irrep(spec(b))) {
                    out(tgt, b, m, c) += values(e, b, m, c)
                }
            }
        }
    }
    
    out
```

### 9.2 Gather

Collect values from source indices (one-to-one):

```blade
function gather<spec>(
    features : Array<Float like Idx<N>, IrrepsIdx<spec>>,
    sources : Array<Nat like Idx<E>>
) : Array<Float like Idx<E>, IrrepsIdx<spec>> =
    
    let out = zero<Idx<E>, IrrepsIdx<spec>>
    
    for e in 0..E {
        let src = sources(e)
        for b in 0..length(spec) {
            for m in 0..mult(spec(b)) {
                for c in 0..dim(irrep(spec(b))) {
                    out(e, b, m, c) = features(src, b, m, c)
                }
            }
        }
    }
    
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
        let cg = lambda(L1, L2, L_out, c1, c_out) ->
            (cg_sparse<L1, L2, L_out>((c1, c_out - c1, c_out)), 0.0)
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
tensor_product<spec1, spec2, spec_out>(...)
    where all_valid_outputs(spec1, spec2, spec_out)

// Helper: check all output irreps are reachable
static function all_valid_outputs(spec1, spec2, spec_out) : Bool =
    for out_b in 0..length(spec_out) {
        let ir_out = irrep(spec_out(out_b))
        let found = false
        for b1 in 0..length(spec1) {
            for b2 in 0..length(spec2) {
                if valid_path(irrep(spec1(b1)), irrep(spec2(b2)), ir_out) {
                    found = true
                }
            }
        }
        if not found { return false }
    }
    true
```

### 11.2 Equivariant Linear Validity

```blade
linear<spec_in, spec_out>(...)
    where all_irreps_present(spec_in, spec_out)

// Helper: check all output irreps exist in input
static function all_irreps_present(spec_in, spec_out) : Bool =
    for out_b in 0..length(spec_out) {
        let ir = irrep(spec_out(out_b))
        let found = false
        for in_b in 0..length(spec_in) {
            if irrep(spec_in(in_b)) == ir {
                found = true
            }
        }
        if not found { return false }
    }
    true
```

### 11.3 Equivariance Annotations

```blade
let v : Array<Float like Idx<3>> with equiv(SO<3>, L1)
let s : Array<Float like Idx<1>> with equiv(SO<3>, L0)

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

function equivariant_conv(
    node_features : Array<Float like Idx<N>, IrrepsIdx<spec_in>>,
    edge_index : Array<Nat like Idx<E>, Idx<2>>,
    edge_vectors : Array<Float like Idx<E>, Idx<3>>,
    weights : Array<Float like WeightIdx<spec_in, spec_sh, spec_out>>
) : Array<Float like Idx<N>, IrrepsIdx<spec_out>> =
    
    let out = zero<Idx<N>, IrrepsIdx<spec_out>>
    
    // For each edge
    for e in 0..E {
        let src = edge_index(e, 0)
        let tgt = edge_index(e, 1)
        
        // Compute spherical harmonics of edge vector
        let edge_sh = SphericalHarmonics.Y_to<2>(edge_vectors(e))
        
        // Get source features
        let src_feat = node_features(src)
        
        // Tensor product: features × edge_sh -> message
        let message = tensor_product<spec_in, spec_sh, spec_out>(
            src_feat, edge_sh, weights
        )
        
        // Accumulate at target node
        for b in 0..length(spec_out) {
            for m in 0..mult(spec_out(b)) {
                for c in 0..dim(irrep(spec_out(b))) {
                    out(tgt, b, m, c) += message(b, m, c)
                }
            }
        }
    }
    
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

## Appendix A: Summary of Constructs

### A.1 Syntax Extensions

| Construct | Syntax | Description |
|-----------|--------|-------------|
| Lambda | `lambda(args...) -> expr` | Pure anonymous function |
| Named infix | `a :name: b` | Parses as `name(a, b)`, lowest precedence |
| Static value | `static x = expr` | Compile-time constant |
| Static function | `static function f(...) = ...` | Compile-time evaluable |
| Enumerated iteration | `<@(i, j, k)>` | Expose iteration indices |

### A.2 Types

| Type | Description |
|------|-------------|
| `Parity` | `Even \| Odd` |
| `Irrep` | Sum type: `L0e \| L0o \| L1e \| L1o \| ...` |
| `IrrepSpec` | `(Irrep, Nat)` — irrep with multiplicity |
| `IrrepsIdx<spec>` | `DepIdx` over static spec array |
| `CGIdx<L1, L2, L_out>` | Sparse index for valid CG triples |
| `SparseIdx<entries>` | Index over sparse entry set |

### A.3 Static Functions

| Function | Signature | Description |
|----------|-----------|-------------|
| `dim` | `Irrep -> Nat` | Dimension of irrep (2L+1) |
| `parity` | `Irrep -> Parity` | Parity of irrep |
| `angular_momentum` | `Irrep -> Nat` | L value of irrep |
| `total_dim` | `Array<IrrepSpec> -> Nat` | Total dimension of spec |
| `valid_path` | `(Irrep, Irrep, Irrep) -> Bool` | CG selection rule |

### A.4 Operations

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
