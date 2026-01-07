# Blade-DSL: A Formal Specification

## Abstract

We present a formal semantics for Blade-DSL, a domain-specific language for symmetric tensor computation. Blade-DSL embodies **S/T (structure-first)** computation—a paradigm inversion where iteration structure is primary and element operations are secondary—in contrast to the **T/S (collection-first)** orientation that has dominated array programming since FORTRAN. We prove that while S/T can express any T/S computation, the reverse does not hold: T/S partial evaluation cannot yield first-class iteration objects because T-dimensions are defined relationally. Non-trivial T/S combinators (fold, scan, reduce) exist within the S/T framework, establishing a duality where S/T governs outer structure and T/S governs inner reduction strategies.

The core abstraction is the *loop object*: a reified nested iteration pattern that can be partially applied, composed via algebraic combinators, and fused. Unlike prior loop abstractions—iterators (single loops), polyhedral models (compiler IR), or scheduling languages like Halide (directives on syntax)—Blade's loop objects are first-class values with a complete combinator algebra including a MonadPlus structure.

We introduce *arity polymorphism*—distinct from the rank polymorphism of APL, Remora, and similar languages—where the number of input arrays determines output rank, nesting depth, and symmetry structure. We formalize *dimensional currying*, treating arrays as functions where indexing is partial application (related to Dex's "arrays as memoized functions" but with static cache-optimality guarantees at the type level).

The (r!)^d speedup from product symmetry over d-dimensional grids is well-known in the tensor literature. Our contribution is a *programming language design* that preserves this structure: dimensional currying prevents accidental flattening that would lose (r!)^(d-1) speedup, and the combinator algebra enables compositional exploitation of symmetry.

---

## Table of Contents

1. [Introduction](#1-introduction)
   - [1.1 What Problem Does Blade-DSL Solve?](#11-what-problem-does-blade-dsl-solve)
   - [1.2 What Makes Blade-DSL Different?](#12-what-makes-blade-dsl-different)
   - [1.3 Target Applications and Scale](#13-target-applications-and-scale)
   - [1.4 How Does It Compare to NumPy/xarray/Dask?](#14-how-does-it-compare-to-numpyxarraydask)

2. [Computational Paradigms: S/T and T/S](#2-computational-paradigms-st-and-ts)
   - [2.1 Two Orientations Toward Array Computation](#21-two-orientations-toward-array-computation)
   - [2.2 Historical Context](#22-historical-context)
   - [2.3 Formal Characterization](#23-formal-characterization)
   - [2.4 The Duality Theorem](#24-the-duality-theorem)
   - [2.5 Non-Trivial T/S Combinators](#25-non-trivial-ts-combinators)
   - [2.6 Why S/T Enables Symmetry Exploitation](#26-why-st-enables-symmetry-exploitation)
   - [2.7 Linguistic Parallel](#27-linguistic-parallel)
   - [2.8 Summary](#28-summary)
   - [2.9 S/T as Mathematical Prerequisite](#29-st-as-mathematical-prerequisite-the-necessity-theorems)

3. [Preliminaries](#3-preliminaries)
   - [3.1 Notation](#31-notation)
   - [3.2 Arrays](#32-arrays)
   - [3.3 Extents](#33-extents)
   - [3.4 Index Types](#34-index-types)
   - [3.5 Array Types](#35-array-types)
   - [3.6 Array Expressions](#36-array-expressions)
   - [3.7 Array Combinators](#37-array-combinators)
   - [3.8 Array Combinator Laws](#38-array-combinator-laws)

4. [Functions](#4-functions)
   - [4.1 Function Signatures](#41-function-signatures)
   - [4.2 Function Syntax](#42-function-syntax)
   - [4.3 Commutativity Groups](#43-commutativity-groups)

5. [Loop Objects](#5-loop-objects)
   - [5.1 The Core Abstraction](#51-the-core-abstraction)
   - [5.2 S-Dimensions and T-Dimensions](#52-s-dimensions-and-t-dimensions)
   - [5.3 Method Loop Structure](#53-method-loop-structure)
   - [5.4 Object Loop Structure](#54-object-loop-structure)
   - [5.5 Partial Application Semantics](#55-partial-application-semantics)
   - [5.6 The Structural Trinity](#56-the-structural-trinity-formal-necessity-proofs)

6. [Arity Polymorphism](#6-arity-polymorphism)
   - [6.1 Distinction from Rank Polymorphism](#61-distinction-from-rank-polymorphism)
   - [6.2 Why Arity Polymorphism Matters](#62-why-arity-polymorphism-matters)
   - [6.3 Arity and Commutativity](#63-arity-and-commutativity)
   - [6.4 Arity-Polymorphic Syntax](#64-arity-polymorphic-syntax)
   - [6.5 Formal Treatment](#65-formal-treatment)
   - [6.6 Comparison to Related Work](#66-comparison-to-related-work)

7. [Dimensional Currying](#7-dimensional-currying)
   - [7.1 The Core Idea](#71-the-core-idea)
   - [7.2 Type-Level Encoding](#72-type-level-encoding)
   - [7.3 Cache Optimality by Construction](#73-cache-optimality-by-construction)
   - [7.4 Distinction from Slicing](#74-distinction-from-slicing)
   - [7.5 Enabling the Combinator Algebra](#75-enabling-the-combinator-algebra)
   - [7.6 Symmetry Integration](#76-symmetry-integration)
   - [7.7 Sparse Tensor Compatibility](#77-sparse-tensor-compatibility)

8. [Combinator Algebra](#8-combinator-algebra)
   - [8.1 Core Combinators](#81-core-combinators)
   - [8.2 Parallel Combinators](#82-parallel-combinators)
   - [8.3 Collection Combinators](#83-collection-combinators)
   - [8.4 Evaluation](#84-evaluation)
   - [8.5 Combinator Laws](#85-combinator-laws)
   - [8.6 Composition Combinators and the Duality Theorem](#86-composition-combinators-and-the-duality-theorem)
   - [8.7 Additional Combinator Identities](#87-additional-combinator-identities)
   - [8.8 Zero Elements and Control Flow](#88-zero-elements-and-control-flow)

9. [Symmetry System](#9-symmetry-system)
   - [9.1 Symmetry/Commutativity States](#91-symmetrycommutativity-states)
   - [9.2 State Computation](#92-state-computation)
   - [9.3 Output Symmetry Inference via Lowering](#93-output-symmetry-inference-via-lowering)
   - [9.4 The Symmetry Transformation](#94-the-symmetry-transformation-lowering-in-action)

10. [Triangular Iteration](#10-triangular-iteration)
    - [10.1 Cumulative Bound Computation](#101-cumulative-bound-computation)
    - [10.2 Left-Justified Indexing](#102-left-justified-indexing)
    - [10.3 Index Mapping for Access](#103-index-mapping-for-access)
    - [10.4 Complexity Analysis](#104-complexity-analysis)
    - [10.5 Product Symmetry Theorem](#105-product-symmetry-theorem)

11. [Type System](#11-type-system)
    - [11.1 Judgments](#111-judgments)
    - [11.2 Array Rules](#112-array-rules)
    - [11.3 Function Rules](#113-function-rules)
    - [11.4 Loop Object Rules](#114-loop-object-rules)
    - [11.5 Application Rules](#115-application-rules)
    - [11.6 Combinator Rules](#116-combinator-rules)

12. [Operational Semantics](#12-operational-semantics)
    - [12.1 Evaluation Model](#121-evaluation-model)
    - [12.2 Loop Level Types](#122-loop-level-types)
    - [12.3 Fusion Analysis](#123-fusion-analysis)
    - [12.4 Compute Semantics](#124-compute-semantics)

13. [Concrete Syntax](#13-concrete-syntax)
    - [13.1 Array Declaration](#131-array-declaration)
    - [13.2 Function Declaration](#132-function-declaration)
    - [13.3 Loop Construction and Application](#133-loop-construction-and-application)
    - [13.4 Combinators](#134-combinators)
    - [13.5 Tuple Syntax](#135-tuple-syntax)
    - [13.6 Poly-Indexing Syntax](#136-poly-indexing-syntax)

14. [Open Design Questions](#14-open-design-questions)
    - [14.1 Error Handling](#141-error-handling)
    - [14.2 Additional Considerations](#142-additional-considerations)

15. [Future Work](#15-future-work)
    - [15.1 Automatic Differentiation](#151-automatic-differentiation)
    - [15.2 Stencils and Halo Exchange](#152-stencils-and-halo-exchange)
    - [15.3 Domain Decomposition for Distributed Computation](#153-domain-decomposition-for-distributed-computation)
    - [15.4 Triangular File Format](#154-triangular-file-format)
    - [15.5 Domain Decomposition Summary](#155-domain-decomposition-summary)
    - [15.6 Remaining Open Questions](#156-remaining-open-questions)
    - [15.7 Tree Structures](#157-tree-structures)

16. [Related Work](#16-related-work)
    - [16.1 Array Languages and Rank Polymorphism](#161-array-languages-and-rank-polymorphism)
    - [16.2 Loop Abstractions and Scheduling](#162-loop-abstractions-and-scheduling)
    - [16.3 Parallel Loop Constructs](#163-parallel-loop-constructs)
    - [16.4 Multi-Dimensional Homomorphisms](#164-multi-dimensional-homomorphisms)
    - [16.5 Tensor Compilers](#165-tensor-compilers)
    - [16.6 Scientific Python Ecosystem](#166-scientific-python-ecosystem)
    - [16.7 Sparse and Masked Array Systems](#167-sparse-and-masked-array-systems)
    - [16.8 Novelty and Impact Assessment](#168-novelty-and-impact-assessment)

17. [Conclusion](#17-conclusion)

**Appendices:** [A. Notation Summary](#appendix-a-notation-summary) · [B. Symmetry Vector Examples](#appendix-b-symmetry-vector-examples) · [C. Complexity Table](#appendix-c-complexity-table)

---

## 1. Introduction

### 1.1 What Problem Does Blade-DSL Solve?

Consider computing a coskewness tensor—a three-dimensional generalization of covariance that captures asymmetric dependencies between variables. For a dataset with 1000 variables, naively computing all entries requires 1 billion operations. But coskewness is symmetric: the value at position (i, j, k) equals the value at (j, i, k), (k, j, i), and all other permutations. Only about 167 million entries are unique—a 6× reduction.

For higher-order statistics, the savings grow factorially. A fourth-order comoment tensor (cokurtosis) has 24× redundancy. Sixth-order has 720×. Eighth-order has 40,320×. These aren't minor optimizations; they're the difference between feasible and infeasible computation.

Blade-DSL was designed to make these optimizations automatic and composable.

### 1.2 What Makes Blade-DSL Different?

**1. Loops Are Values, Not Syntax**

In most languages, a `for` loop is syntax—you write it, the compiler sees it, and that's that. In Blade-DSL, iteration patterns are first-class values you can store, pass around, and compose:

```
// object_for binds a kernel, then accepts different array configurations
let coskew_op = object_for(coskewness_kernel)
let cokurt_op = object_for(cokurtosis_kernel)

let result1 = coskew_op <@> (data, data, data) |> compute   // 3rd-order self-comoment
let result2 = cokurt_op <@> (data, data, data, data) |> compute  // 4th-order self-comoment
let result3 = coskew_op <@> (temp, temp, precip) |> compute  // cross-comoment
```

This separation means you can build libraries of reusable operators and apply them to different data configurations.

**2. The Type System Guarantees Cache Efficiency**

Most array libraries hope the compiler figures out good memory access patterns. Blade-DSL takes a different approach: arrays are treated as functions (we call this *dimensional currying*), and the type system encodes cache-optimal access. Writing a cache-inefficient loop isn't a performance bug—it's a type error that won't compile.

**3. Symmetry Is Automatic**

You declare that a function is commutative (order of arguments doesn't matter), and the system automatically:

- Generates triangular iteration (skipping redundant computations)
- Allocates compact triangular storage
- Infers the symmetry structure of the output

You don't manually write nested loops with carefully adjusted bounds. You state the mathematical property, and correct code emerges.

**4. Arity Polymorphism**

Traditional array languages support *rank polymorphism*: the same function works on vectors, matrices, and higher-rank tensors. Blade-DSL adds *arity polymorphism*: the same kernel works with different numbers of input arrays, and the number of inputs determines the output's rank and symmetry.

This is essential for comoment tensors, where the same "multiply elements together" kernel produces:

- Covariance (2 inputs → rank-2 output)
- Coskewness (3 inputs → rank-3 output)
- Cokurtosis (4 inputs → rank-4 output)

### 1.3 Target Applications and Scale

**Primary application: Climate science**

Blade-DSL was developed for Joint Moment Component Analysis (JMCA) in climate and atmospheric science. JMCA extends Principal Component Analysis to higher-order statistics, enabling detection of nonlinear climate patterns—El Niño/La Niña asymmetry, extreme precipitation clustering, monsoon onset triggers—that are invisible to covariance-based methods.

The challenge is computational scale. At the resolutions climate scientists actually want:

| Resolution | Grid Points | Coskewness Size | Cokurtosis Size |
|------------|-------------|-----------------|-----------------|
| 2° | 16,200 | 709 GB | 2.9 PB |
| 1° | 64,800 | 45 TB | 740 PB |
| 0.25° | 1,036,800 | 186 PB | — |

At 1° resolution, even the third-order comoment tensor is 45 terabytes. Without symmetry exploitation and distributed computation, these analyses are not merely slow—they are impossible.

**Secondary applications**

The abstractions generalize beyond climate:

- **Quantum physics**: Higher-order correlation functions, entanglement measures
- **Neuroscience**: Cross-frequency coupling, higher-order connectivity
- **Finance**: Coskewness and cokurtosis for portfolio risk, tail dependence
- **Genomics**: Higher-order epistasis, multi-way gene interactions

Any domain computing symmetric tensors over structured multi-dimensional data can benefit from product symmetry speedups.

**Intended audience**

This specification assumes familiarity with:

- Array programming concepts (ranks, shapes, broadcasting)
- Basic type theory (type judgments, inference rules)
- Symmetric tensors and their index symmetries

No prior knowledge of Blade-DSL or climate science is required.

### 1.4 How Does It Compare to NumPy/xarray/Dask?

These are excellent tools for array computation, but they make different tradeoffs:

| Aspect | NumPy/xarray/Dask | Blade-DSL |
|--------|-------------------|-----------|
| Iteration | Hidden (vectorized ops) | Explicit loop objects |
| Symmetry | Manual (if at all) | Automatic exploitation |
| Memory layout | Runtime concern | Type-level guarantee |
| Arity | Fixed per function | Polymorphic |
| Fusion | Limited/heuristic | Algebraic combinators |

If your computation fits naturally into vectorized operations, use NumPy or xarray. If you need fine control over iteration patterns, symmetry exploitation, or arity-polymorphic kernels, Blade-DSL offers abstractions those tools don't provide.

---

## 2. Computational Paradigms: S/T and T/S

### 2.1 Two Orientations Toward Array Computation

Blade-DSL embodies what we term **S/T (structure-first)** computation, in contrast to the **T/S (collection-first)** orientation that has dominated array programming since FORTRAN. The naming itself encodes the inversion: where T/S places the T-dimension (what you reduce over) first, S/T places the S-dimension (what you iterate over) first. This syntactic inversion forces a semantic inversion.

**T/S (collection-first):** Arrays are conceived as collections to be traversed. Computation specifies operations over collection elements; iteration structure is implicit or inferred.

```python
# NumPy: collection-first
result = np.sum(A * B, axis=-1)
C = np.einsum('ij,jk->ik', A, B)
```

The programmer thinks: "I have these collections. Apply these operations across them."

**S/T (structure-first):** Arrays are conceived as functions from index spaces. Computation specifies iteration structure explicitly; operations are applied to that structure.

```
// Blade: structure-first
method_for(A, B) <@> kernel
```

The programmer thinks: "Here is the iteration structure. Apply this kernel to it."

### 2.2 Historical Context

All major array programming systems employ T/S orientation, though some show partial S/T tendencies:

| System | Year | Orientation | S/T Score | Evidence |
|--------|------|-------------|-----------|----------|
| FORTRAN | 1957 | Pure T/S | 0/5 | Explicit loops over elements |
| APL | 1962 | Pure T/S | 0/5 | Implicit iteration via rank polymorphism |
| R | 1993 | T/S (partial S/T) | 1/5 | `apply` family reifies iteration choice; formula objects (`y ~ x`) separate structure from operation |
| NumPy | 2006 | Pure T/S | 0/5 | Vectorized element operations |
| data.table | 2008 | T/S (partial S/T) | 1/5 | `by=` and `.SD` expose grouping structure before operation |
| TensorFlow | 2015 | Pure T/S | 0/5 | Dataflow over tensor elements |
| Halide | 2013 | T/S + schedules | 2/5 | Separates algorithm from schedule, but schedules are directives on syntax |
| Chapel | 2009 | T/S (partial S/T) | 2/5 | Domains are first-class values, but no loop combinators |
| Dex | 2021 | T/S | 1/5 | `for` as array builder (syntax, not value); arrays as functions |
| Polyhedral (isl, MLIR) | various | Compiler IR | 1/5 | Loop nests as polyhedra—reified, but as IR, not user-facing |
| **Blade-DSL** | 2024 | **Pure S/T** | **5/5** | Loop objects as first-class values; combinator algebra; arity polymorphism |

The T/S orientation is so pervasive that it rarely appears as a choice—it is simply "how array programming works."

#### 2.2.1 Notable Systems with Partial S/T Tendencies

**Chapel (Domains):** Chapel is the closest existing system to S/T among mainstream languages. Domains are first-class values (`const D = {1..n, 1..m}`), and domain maps separate structure from layout (`dmapped Block(...)`). However, Chapel lacks composable loop combinators with algebraic laws, arity polymorphism, and loop objects that can be partially applied. Chapel's domains are *index sets*, not *iteration patterns*—you can describe *what* indices exist, but not *how* to compose nested iteration over multiple domains.

**Halide (Schedule Separation):** Halide's key insight—separating algorithm from schedule—is related to S/T. But schedules are **directives applied to syntax** (`.split()`, `.tile()`), not first-class values. You cannot store a schedule as a value, compose schedules algebraically, or apply the same schedule to different algorithms polymorphically.

**Dex (Arrays as Functions):** Dex's "arrays as functions" insight is closely related to dimensional currying. But Dex's `for` is **syntax**, not a value—you cannot abstract over nesting patterns or apply different kernels to the same iteration structure.

**Polyhedral Model:** The polyhedral model *does* reify iteration structure as integer polyhedra. This is genuine loop reification, but it is **compiler IR**, not user-facing. Users don't compose polyhedra; compilers transform them.

#### 2.2.2 Gap Analysis

| S/T Feature | Chapel | Halide | Dex | Polyhedral | Blade |
|-------------|--------|--------|-----|------------|-------|
| First-class iteration objects | Partial (domains) | No (directives) | No (syntax) | Yes (IR only) | **Yes** |
| Composable loop combinators | No | No | No | No (IR transforms) | **Yes** |
| Arity → output structure | No | No | No | N/A | **Yes** |
| Structure-before-kernel | Partial | Schedule-level | No | Yes (IR) | **Yes** |
| Algebraic laws | No | No | No | Transform rules | **Yes** (MonadPlus) |

Each predecessor got *part* of the way: Chapel reified index sets; Halide separated structure from algorithm; Dex treated arrays as functions; polyhedral models represented loops as polyhedra. But no prior system combined these insights into a **programming paradigm** where nested loops are first-class values with algebraic combinators, arity determines output structure, and symmetry exploitation is automatic.

### 2.3 Formal Characterization

We can characterize the distinction in terms of what is held primary:

**T/S primacy:** The collection (array contents) is primary. Iteration structure is derived from element operations and array shapes.

```
T/S: Collection × Operation → Result
     (iteration implicit)
```

**S/T primacy:** The iteration structure is primary. It is constructed explicitly, then operations are applied.

```
S/T: Structure × Kernel → Result
     (iteration explicit, first-class)
```

In Blade-DSL, this manifests as:

- **`method_for(A₁, ..., Aₙ)`** constructs iteration structure (a `MethodLoop`) from array signatures
- **`object_for(f)`** constructs iteration structure from a kernel's arity requirements  
- **`<@>`** applies a kernel to an iteration structure, producing a `Computation`

The iteration structure exists as a value *before* any kernel is applied.

### 2.4 The Duality Theorem

A natural question: is S/T merely T/S in different notation, or are they genuinely distinct?

**Theorem 2.1 (S/T Completeness):** Any T/S computation can be expressed in S/T form.

*Proof:* Given a T/S computation `f(A₁, ..., Aₙ) → B`, construct:

1. `L = method_for(A₁, ..., Aₙ)` — derive iteration structure from arrays
2. `K = λ(a₁, ..., aₙ). f_pointwise(a₁, ..., aₙ)` — pointwise kernel
3. `L <@> K |> compute` — apply and execute

The S/T form makes iteration structure explicit that was implicit in T/S. ∎

**Attempted T/S Completeness:** Can we go the other direction? Can we express S/T constructs in pure T/S terms?

Consider attempting to construct a "partial T/S object":

```
fold(+, axis=-1)  // T/S: "fold with + along last axis"
```

This appears to be a reusable object. But examine what information it requires:

- Which axis is "last"? Depends on the array it will be applied to.
- What is the extent of that axis? Unknown until application.
- What are the output dimensions? Derived from input dimensions.

**Lemma 2.2 (T-Dimension Relationality):** T-dimensions (those consumed by a kernel) are not independently specifiable; they are defined *relationally* as "dimensions consumed from each array by the kernel."

*Proof:* The statement "axis -1" has no meaning without an array to apply it to. The T-structure of a computation depends on *both* the kernel signature and the array signatures. Neither alone determines it.

In contrast, S-dimensions (those iterated over) can be determined from array signatures alone—they are the dimensions remaining after kernel consumption.

**Definition (Iteration Object):** An *iteration object* is a value I satisfying:
1. **Kernel independence:** I can be constructed without specifying a kernel
2. **Kernel polymorphism:** I can be applied to different kernels: `I <@> k₁`, `I <@> k₂`
3. **Composability:** I can be composed with other iteration objects: `I₁ <&> I₂`

**Theorem 2.3 (Iteration Object Impossibility in T/S):** No T/S system admits iteration objects.

*Proof:* In T/S, iteration structure is determined by:

- Array dimensions (which axes exist)
- Kernel consumption (which axes the kernel reduces over)

By Lemma 2.2, T-dimensions are relational—they depend on the kernel. Without knowing the kernel:

- We cannot determine which dimensions are S (iterated) vs T (reduced)
- We cannot determine iteration depth (how many nested loops)
- We cannot determine iteration bounds (which depend on S-dimension extents)

Therefore no kernel-independent iteration specification exists. Conditions (1) and (2) of the definition cannot be satisfied simultaneously.

For composability (3): composition `I₁ <&> I₂` requires both I₁ and I₂ to exist as values. Since neither can exist independently, composition is impossible. ∎

**Corollary 2.4:** T/S partial evaluation does not yield first-class iteration objects.

### 2.5 Non-Trivial T/S Combinators

Although T/S does not support the same factorization as S/T, non-trivial T/S combinators exist within the S/T framework:

**Reduction combinators:** `fold`, `scan`, `reduce` specify *how* T-dimensions are consumed:

```
fold(⊕, init)      -- left fold with binary operator
scan(⊕, init)      -- prefix scan (cumulative)  
tree_reduce(⊕)     -- balanced tree reduction
```

These are T/S combinators: they specify element-level operations and reduction strategies. They exist as values within Blade-DSL.

**The duality:** S/T provides the *outer* structure (iteration, parallelism, symmetry). T/S combinators can specify *inner* structure (reduction strategy within kernels). The two compose:

```
let L = method_for(A, A) where comm(A, A)    // S/T: triangular iteration
let K = λ(a, b). fold(+, 0)(a * b)           // T/S: dot product via fold
L <@> K |> compute                            // Compose S/T and T/S
```

This establishes genuine duality: S/T and T/S are complementary, not competing. S/T governs the outer iteration structure; T/S governs the inner reduction strategy.

### 2.6 Why S/T Enables Symmetry Exploitation

T/S orientation treats iteration as implicit—derived from element operations. This prevents explicit manipulation of iteration structure.

For symmetric tensors, we need:

1. **Triangular bounds:** Iterate only over unique index combinations
2. **Bound propagation:** Inner loop bounds depend on outer loop indices
3. **Output symmetry inference:** Kernel commutativity implies output symmetry

These require *explicit representation* of iteration structure. In T/S, iteration structure is implicit and inaccessible. In S/T, it is a first-class value that can be:

- Inspected for symmetry properties
- Modified to enforce triangular bounds
- Composed with other iteration structures

The Product Symmetry Theorem's (r!)^d speedup requires preserving dimensional independence during iteration. This is only possible when iteration structure is explicit.

### 2.7 Linguistic Parallel

The S/T versus T/S distinction parallels a typological distinction in natural language grammar.

**Nominative/accusative languages** (English, most Indo-European): The agent receives consistent grammatical marking. "She sees him"—the subject is primary.

**Ergative/absolutive languages** (Basque, Georgian, Dyirbal): The patient receives consistent grammatical marking. The affected entity is the default.

| Linguistic | Computational | Primary Object |
|------------|---------------|----------------|
| Nominative/accusative | T/S | The operation (agent) |
| Ergative/absolutive | S/T | The structure (patient) |

T/S asks: "What does the operation do to the data?"
S/T asks: "What structure does the data have, and what operations respect it?"

This parallel is not merely metaphorical. It suggests that the dominance of T/S in programming languages may reflect cognitive-linguistic patterns in the communities that designed them, rather than inherent computational superiority.

### 2.8 Summary

| Property | T/S (collection-first) | S/T (structure-first) |
|----------|------------------------|------------------------|
| Arrays are | Collections of elements | Functions from index spaces |
| Iteration is | Implicit/derived | Explicit/first-class |
| Loops are | Syntax | Values with algebraic structure |
| Symmetry is | Invisible or manual | Automatic from commutativity |
| Partial application | Not supported for iteration | `method_for`, `object_for` |
| Composition | Via element operations | Via loop combinators |

Blade-DSL is not merely "NumPy with symmetry optimization." It embodies a different computational paradigm—one where structure comes first and element operations are applied to structure. The symmetric tensor speedups are a *consequence* of this paradigm, not its definition.

### 2.9 S/T as Mathematical Prerequisite: The Necessity Theorems

The preceding sections establish that S/T and T/S are genuinely distinct. We now prove a stronger result: the Structural Trinity (§5.6) is *only* expressible in S/T-oriented systems. This elevates S/T from a "design philosophy" to a **mathematical prerequisite** for symmetric tensor computation.

**Theorem 2.5 (T/S Cannot Express Arity Polymorphism):**

Arity polymorphism requires: given arity r, produce r-deep nested iteration with output rank r.

*Claim:* T/S cannot express arity polymorphism.

*Proof:*

1. Arity polymorphism requires iteration depth to vary with input count
2. Iteration depth is a property of iteration structure
3. By Theorem 2.3, T/S cannot represent iteration structure independently of kernels
4. Therefore T/S cannot parameterize iteration depth by arity

More concretely: in T/S, to express `method_for(A, A, A)` (3 arrays → 3-deep iteration), the system must know that three arrays means three nested loops. But "three nested loops" is an iteration structure—and T/S cannot represent iteration structures as values that vary with input. ∎

**Theorem 2.6 (T/S Cannot Express Rank-Safe Dimensional Currying):**

Dimensional currying treats arrays as functions: `Array<T, n>` behaves as `Idx → Array<T, n-1>`, with compile-time rank tracking.

*Claim:* T/S cannot provide compile-time rank-safe dimensional currying.

*Proof:*

1. Rank safety requires knowing output rank at compile time
2. Output rank = input rank ∑ consumed dimensions
3. By Lemma 2.2, consumed dimensions (T-dimensions) are relational—they depend on the kernel
4. Without knowing the kernel at the currying site, consumed dimensions are unknown
5. Therefore output rank cannot be determined at compile time
6. Therefore no compile-time rank safety

Note: T/S systems can provide *runtime* slicing (NumPy views), but not *compile-time typed* currying where `A[i]` has a statically-known lower rank. The type of `A[i]` in NumPy is `ndarray`—the same type regardless of how many indices are applied. ∎

**Theorem 2.7 (T/S Cannot Express Loop Reification):**

Loop reification requires nested iteration patterns to exist as first-class values with algebraic structure.

*Claim:* T/S cannot express loop reification.

*Proof:* Direct consequence of Theorem 2.3. Loop reification *is* the existence of iteration objects. T/S does not admit iteration objects. ∎

**Theorem 2.8 (Trinity Implies S/T):**

Any system with the Structural Trinity (loop reification, arity polymorphism, dimensional currying) is necessarily S/T-oriented.

*Proof:* Examine what each Trinity component requires:

1. **Loop reification:** Iteration structure exists as first-class value
   → Iteration is explicit, not derived from element operations

2. **Arity polymorphism:** Iteration depth varies with input count
   → Structure is parameterized independently of kernel

3. **Dimensional currying:** Arrays are functions with compile-time rank tracking
   → Structure (rank) is primary; operations are applied to structure

All three features require iteration structure to be *primary* and *explicit*—constructed before kernels are specified, composable independently of element operations. This is precisely the definition of S/T orientation. ∎

**Theorem 2.9 (S/T Necessity for Trinity):**

The Structural Trinity is expressible *only* in S/T-oriented systems.

*Proof:* By contrapositive.

Assume T/S orientation. Then by Theorems 2.5, 2.6, 2.7:

- No arity polymorphism (Theorem 2.5)
- No rank-safe dimensional currying (Theorem 2.6)
- No loop reification (Theorem 2.7)

Therefore: T/S → ¬Trinity

Contrapositive: Trinity → ¬T/S → S/T ∎

**Corollary 2.10 (S/T ↔ Trinity Equivalence):**

For systems capable of symmetric tensor computation with (r!)^d speedup:

*S/T orientation* ⟺ *Structural Trinity is expressible*

*Proof:*

- (→) S/T enables Trinity: Blade-DSL demonstrates constructively that S/T orientation permits all three Trinity components.
- (←) Trinity requires S/T: Theorem 2.9. ∎

**The Impossibility Cascade:**

These theorems establish a cascade of impossibilities in T/S systems:

```
T/S orientation
    ← Theorem 2.3
No first-class iteration objects
    ← Theorems 2.5, 2.6, 2.7
No arity polymorphism ∧ No rank-safe currying ∧ No loop reification
    ← Trinity Inseparability (Theorem 5.10)
No triangular iteration with dependent bounds
    ← Product Symmetry Theorem
No (r!)^d speedup for symmetric tensors
```

Each step is a proven implication. The cascade shows that T/S orientation is not merely "less convenient" for symmetric tensors—it is *fundamentally incapable* of expressing the abstractions required for factorial speedup.

**Theorem 2.10 (Index Anonymity Requires Runtime Reification):**

Let L be a system where kernel arity is variable (number of input arrays determined at runtime) and kernels access array elements at the current iteration position. Then either:
1. Indices are runtime values accessed through a uniform interface (index anonymity), or
2. Separate kernel code must be written for each arity (no polymorphism)

*Proof:*

1. With variable arity, the number of iteration indices varies. A kernel operating at arity r has r arrays, each contributing dimensions to the iteration space.

2. To access array elements, the kernel must reference the current index values.

3. In a compile-time system, indices are named identifiers—lexical tokens in source code. The kernel body is written with explicit references to these names.

4. If arity varies, the number of index names varies. A kernel body written for arity 2 references `i0, i1, j0, j1`; a kernel for arity 3 references `i0, i1, k0, k1, j0, j1`; and so on.

5. A single kernel body cannot contain references to a variable number of named identifiers. Identifier names are syntactic, not semantic—they must exist in the source text.

6. Therefore, either:
   - (a) Indices are accessed uniformly as runtime values: `args[|k|]`, `indices[k]`, etc. The kernel body contains no arity-dependent identifier names.
   - (b) Separate kernel code exists for each arity, each with its own identifier names. This is code generation, not polymorphism.

Option (a) is index anonymity via runtime reification. Option (b) abandons arity polymorphism. There is no third option. ∎

**Theorem 2.11 (First-Class Traversal Necessity):**

Let L be a system supporting:
1. **Array-arity polymorphism:** same kernel applicable to variable numbers of arrays, with arity determined at runtime
2. **Traversal composition:** iteration patterns combinable, both at the level of loop objects and internal loop reordering
3. **Commutativity optimization:** triangularization selected based on runtime array identity

Then L must represent traversal structures as first-class runtime values.

*Proof:*

1. Array-arity polymorphism requires traversal depth to vary with input count. Each array contributes dimensions to the iteration space. If the number of arrays is determined at runtime, the traversal structure—including its depth and the symcomstate table shape—cannot be fully resolved at compile time.

2. Traversal composition operates at two levels:
   - *Loop object composition:* combining iteration patterns from different sources (e.g., `method_for(A, A) <*> method_for(B, B)`)
   - *Internal reordering:* transposing loops within commutativity groups to achieve optimal triangularization
   
   Both are operations on traversal structures; their operands must exist as values.

3. Commutativity optimization requires inspecting array identity (`&A == &B`) to select between triangularization strategies. Array identity is a runtime property. Therefore, traversal selection occurs at runtime, operating on traversal representations.

4. By Theorem 2.10, array-arity polymorphism requires either runtime index access or separate kernel code per arity. The Structural Trinity (Theorem 5.10) provides index anonymity: kernels operate on values at positions (`args[|k|]`, `head`, `tail`) without naming iteration indices. This abstraction is **impossible** when indices are compile-time identifiers—a single kernel body cannot reference a variable number of named identifiers.

5. Therefore, traversals must be runtime values: storable, passable, composable, and supporting anonymous index access. ∎

**Corollary 2.12 (Runtime Reification Necessity):**

Theorem 5.10 establishes that loop reification, arity polymorphism, and dimensional currying are mutually necessary. Theorem 2.11 establishes that loop reification must be *runtime* reification—compile-time reification (metalanguages, staging) is insufficient because it cannot provide index anonymity for arity-polymorphic kernels.

**Corollary 2.13 (S/T Necessity from First-Class Traversals):**

A system with first-class runtime traversal structures treats iteration as primary: traversals exist before kernels are applied, can be manipulated independently, and provide anonymous index access for kernels. This is S/T orientation by definition. Therefore, any system satisfying requirements (1-3) of Theorem 2.11 is necessarily S/T-oriented.

| Theorem | Key Insight | Impossibility Claim |
|---------|-------------|---------------------|
| Index Anonymity (2.10) | Variable arity requires variable number of indices | A single kernel body cannot reference a variable number of compile-time identifiers |
| First-Class Traversal (2.11) | Optimal symmetric tensor computation requires composable, optimizable traversal | Compile-time metalanguages cannot provide index anonymity, traversal composition, or runtime commutativity selection |

These theorems establish that S/T orientation is not merely a design preference but a **mathematical necessity** for systems achieving (r!)^d speedup with array-arity polymorphism and commutativity optimization.

---

**Remark (Why 60 Years of T/S):**

Several factors explain why S/T orientation was never explored:

1. **The Flattening Bias:** "Flatten for performance" became received wisdom from FORTRAN through NumPy. GPUs reinforced this with coalesced memory requirements.

2. **Divided Communities:** The solution required synthesizing PL theory, numerical computing, group theory, HPC, and domain science. No single community spans all areas.

3. **Obvious in Hindsight:** Once explained, S/T seems natural. This apparent simplicity obscures the difficulty of discovering it.

4. **The Incremental Trap:** Existing tools are well-optimized for T/S. Fundamental redesigns require encountering problems that expose architectural limitations.

Symmetric tensor operations at scale—where the (r!)^d speedup becomes essential—provide precisely the forcing function needed to expose T/S limitations. Without such problems, T/S remained unchallenged.

---

## 3. Preliminaries

### 3.1 Notation

| Symbol | Meaning |
|--------|---------|
| ℕ | Natural numbers |
| T | Base types (float, int, complex, etc.) |
| r, n ∈ ℕ | Ranks (dimensionality) |
| σ, τ ∈ ℕ* | Symmetry vectors |
| ε, δ ∈ ℕ* | Extent vectors |
| c ∈ ℕ* | Commutativity vectors |
| A* | Sequences of arrays |

### 3.2 Arrays

An array is a tuple A = (T, r, σ, ε) where:

- T is the element type
- r ∈ ℕ is the rank
- σ ∈ ℕÊ³ is the symmetry vector (|σ| = r)
- ε ∈ ℕÊ³ is the extent vector (|ε| = r)

**Symmetry vector semantics**: σáµ¢ = σⱼ indicates dimensions i and j are symmetric (interchangeable). Values are local to each array—there is no global meaning across arrays.

**Examples**:

- Dense matrix: σ = ⟨1, 2⟩ (dimensions independent)
- Symmetric matrix: σ = ⟨1, 1⟩ (dimensions interchangeable)
- 3-tensor with partial symmetry: σ = ⟨1, 1, 2⟩ (dims 0,1 symmetric; dim 2 independent)

### 3.3 Extents

Extents (dimension sizes) are runtime values intrinsic to arrays. Users do not manage extent vectors explicitly; they are:

- Inferred from data sources (e.g., file metadata)
- Declared literally in array construction
- Computed from input extents for T-dimensions

**Design principle**: Extent-passing is opaque to the user. When constructing loops, extents flow automatically from the bound arrays.

For T-dimensions, extents may be expressed as functions of input extents:

```
tdim_extent ::= literal
              | input.extent(dim)
              | tdim_extent op tdim_extent   where op ∈ {+, -, *, /}
```

**Example**: Real FFT output has extent `n/2 + 1` where `n` is the input extent.

### 3.4 Index Types

Index types define the structure of array dimensions. They determine how iteration proceeds and how elements are addressed in storage. Index types are the only dependently-typed component of Blade's type system--they may depend on file contents or runtime arrays.

#### 3.4.1 Base Index Types

| Type | Type Signature | Description | Hashable |
|------|----------------|-------------|----------|
| `Idx<n>` | `N` | Contiguous integers 0..n-1 | Trivially (extent equality suffices) |
| `EnumIdx<S>` | `N` | Enumerated categories | Yes (from set S) |
| `RaggedIdx<lengths>` | `N` | Variable extent per outer index | Yes (from lengths array) |
| `CompoundIdx<mask>` | `N -> N -> ...` | Sparse combinations from k-dimensional mask | Yes (from mask) |

**Type signatures**: Most index types have signature `N` (single integer). `CompoundIdx` has signature `N -> N -> ...` matching the rank of its mask--it is internally curryable.

**Float indices are forbidden**: Floating-point values are not safely hashable due to precision issues. Coordinate values (latitudes, times, etc.) are stored as separate 1D arrays, not as index types.

#### 3.4.2 Tagged Index Types

Index types may carry user-defined tags for type-level distinction:

```
Idx<n, Tag>
```

Tags are user-defined enum types:

```
enum LatPosition { Center, Left, Right }
enum LonPosition { Center, Left, Right }

Idx<721, LatPosition.Center>
Idx<721, LatPosition.Left>
```

**Type equality requires tag equality**:

```
Idx<721, LatPosition.Center> != Idx<721, LatPosition.Left>   // different tags
Idx<721, LatPosition.Center> != Idx<721, LonPosition.Center> // different enum types
```

Untagged indices are compatible only with other untagged indices of the same extent.

#### 3.4.3 Dependent Index Types

Certain index types depend on runtime arrays:

**Ragged indices**:
```
let obs_lengths: Array<Int, Idx<n_stations>> = ...  // observations per station
let readings: Array<Float, Idx<n_stations>, RaggedIdx<obs_lengths>>
```

**Compound index from mask** (for mutually-dependent sparsity):

When sparsity is mutually dependent across dimensions (e.g., only ocean points exist, not a Cartesian product of valid lats and valid lons), use a compound index:

```
let ocean_mask: Array<Bool, Idx<180>, Idx<360>> = ...
let ocean_temp: Array<Float, CompoundIdx<ocean_mask>, Idx<8760>>
```

Unlike simple index types with signature `N`, a `CompoundIdx` has signature `N -> N -> ...` matching the rank of its mask. This preserves currying through the compound structure.

#### 3.4.4 Compound Index Semantics

**Type signature**: For a k-dimensional mask, `CompoundIdx<mask>` has type `N -> N -> ... -> N` (k arrows).

**Tuple indexing**: Index into a compound dimension using tuples:

```
let ocean_temp: Array<Float, CompoundIdx<mask>, Idx<8760>>
// where mask: Array<Bool, Idx<180>, Idx<360>>

// Full tuple index resolves compound dimension completely:
ocean_temp[(lat, lon)]        -> Array<Float, Idx<8760>>
ocean_temp[(lat, lon)][t]     -> Float

// Partial tuple with wildcard curries through compound:
ocean_temp[(lat, _)]          -> Array<Float, N, Idx<8760>>  // valid lons at this lat
ocean_temp[(_, lon)]          -> Array<Float, N, Idx<8760>>  // valid lats at this lon
ocean_temp[(lat, _)][t]       -> Array<Float, N>             // slice at time t
```

**Indexing order**: Cache-optimal order is preserved. You cannot skip the compound index:

```
ocean_temp[t]                 -> ERROR: must index CompoundIdx first
```

**Coordinate access**: Hash-based O(1) lookup by coordinate values:

```
ocean_temp.at(45.5, -122.3)   -> Array<Float, Idx<8760>>  // by lat/lon coords
ocean_temp.at(45.5, -122.3)[t] -> Float
```

**Identity**: Two `CompoundIdx` types are equal iff their masks are identical (compared via whole-mask hash).

**Storage**: Data is stored contiguously for valid combinations only. The mask defines:

- Which combinations are valid
- The hashing scheme for coordinate lookup
- The structure for partial (wildcard) indexing

**Coordinate recovery**: Given a flat position, recover the original coordinates:

```
ocean_temp.coords[i] -> (lat, lon)  // reverse lookup
```

**Rank contribution**: A `CompoundIdx<mask>` where mask has rank k contributes k to the effective dimensionality for currying purposes, but occupies one "slot" in the array type.

The hash of a dependent index type derives from its defining array, ensuring that arrays sharing the same mask or length array have compatible types.

#### 3.4.4b CompoundIdx Currying Clarification

When a `CompoundIdx` is partially indexed with wildcards, the result type depends on the resulting dimensionality:

**Currying rules by resulting dimension:**

| Original | Partial index | Result type |
|----------|---------------|-------------|
| 2D `CompoundIdx` | `(lat, _)` or `(_, lon)` | `Idx<n>` (1D = regular index) |
| 3D `CompoundIdx` | `(a, _, _)` | 2D `CompoundIdx` |
| 3D `CompoundIdx` | `(_, b, _)` | 2D `CompoundIdx` |
| 3D `CompoundIdx` | `(a, b, _)` | `Idx<n>` (1D = regular index) |
| 3D `CompoundIdx` | `(a, _, c)` | `Idx<n>` (1D = regular index) |

**When curried to 1D, result is `Idx<n>`** — the base index type, which is already hashable.

**Hash computation for currying:**

To evaluate `cpidx[(lat, _)]`:
1. Iterate all valid `(lat, lon)` pairs in mask where first component equals `lat`
2. For each valid `lon`, compute `hash(lat, lon)`
3. Build new index mapping `lon -> hash(lat, lon)`
4. Result extent `n` = count of valid lons at this lat

This is O(n) where n = number of valid combinations. Not fast, but necessary for producing a well-typed slice.

**Type identity for curried indices:**

```
cpidx1[(lat, _)] == cpidx2[(lat, _)]   iff   mask1 == mask2 && lat1 == lat2
```

The curried type identity derives from (original mask hash, fixed coordinate values).

**Wildcards at any position:** Both `(lat, _)` and `(_, lon)` are valid because the hash computation works in either direction — we filter the mask for matching coordinates and build the resulting index.


#### 3.4.5 Structural Matching (Duck Typing)

Index types are structurally matched. Two index types are equal if:

1. **Extent**: Same number of elements
2. **Tag**: Same tag type and value (if tagged)
3. **Hash**: Same hash (for non-trivial index types)

This enables duck typing across files:

```
// Two files with same grid structure
type ERA5 = FileProvider<"era5.nc">
type MERRA = FileProvider<"merra.nc">

// If index structure matches, operations are valid
era5_temp + merra_temp  // OK if indices structurally equal
```

#### 3.4.6 Index Transforms

All structural transforms are explicit:

| Transform | Effect |
|-----------|--------|
| `flip(A, dim)` | Reverse ordering (changes hash) |
| `rename(A, old -> new)` | Change tag |
| `subset(A, dim=lo..hi)` | Extract range (new extent, new hash) |
| `align(A, B, dim)` | Join arrays on common indices |

No implicit conversions occur. Mismatched indices produce type errors.

#### 3.4.7 Dimensions vs Index Types

**Dimensions** (coordinate values like latitudes or timestamps) are ordinary 1D arrays:

```
let lat_coords: Array<Float, Idx<721>>     // the latitude values
let time_coords: Array<DateTime, Idx<8760>> // the timestamps
```

**Index types** determine iteration and storage structure. The association between a dimension array and the data arrays it describes is a user-level convention, not enforced by the type system.

This separation keeps the core type system simple while allowing flexible metadata handling.

#### 3.4.8 Files as Type Providers

File metadata provides index types at compile time:

```
type ERA5 = NetCDFProvider<"era5.nc">
// ERA5.lat_idx : Idx<721>
// ERA5.lon_idx : Idx<1440>
// ERA5.time_idx : Idx<8760>
// ERA5.t2m : Array<Float, Idx<721>, Idx<1440>, Idx<8760>>
```

The compile step reads file metadata to instantiate types. Runtime reads actual data values. This works because:

1. File structure is quasi-static (does not change during computation)
2. Metadata inspection is cheap
3. The structure (not values) determines types

#### 3.4.9 Symmetry and Index Types

For symmetric arrays, index types remain distinct but the symmetry vector indicates which dimensions are interchangeable:

```
let cov: Float^2(1,1)   // symmetric matrix: dimensions 0 and 1 are interchangeable
```

Using explicit index types:

```
let cov: Array<Float, Idx<1000>, Idx<1000>, <1,1>>
```

The two `Idx<1000>` are structurally the same, and the symmetry vector `<1,1>` declares they are interchangeable. Indexing is transparent--`cov[i,j]` and `cov[j,i]` access the same canonical storage location. Writes canonicalize silently; reads from either index order return the same value.

#### 3.4.10 Currying by Index

Arrays curry by index type only:

```
let A: Array<Float, Idx<100>, Idx<200>, Idx<300>>

A[i]       // Array<Float, Idx<200>, Idx<300>>
A[i][j]    // Array<Float, Idx<300>>
A[i][j][k] // Float
```

Index arithmetic works for contiguous integer indices:

```
A[i + 1]   // valid for Idx<n>
```


#### 3.4.11 Declaration Syntax

**Type declarations with `type` keyword:**

```
// Basic index types
type LatIdx = Idx<180>
type LonIdx = Idx<360>
type TimeIdx = Idx<8760>

// Enum types (for tags)
type GridPosition = enum { Center, Left, Right }
type Month = enum { Jan, Feb, Mar, Apr, May, Jun, Jul, Aug, Sep, Oct, Nov, Dec }

// Tagged index types
type LatCenter = Idx<721, GridPosition.Center>
type LatLeft = Idx<720, GridPosition.Left>

// Dependent index types (reference arrays)
type OceanIdx = CompoundIdx<ocean_mask>
type ObsIdx = RaggedIdx<obs_counts>
```

**Value declarations with `let` keyword:**

```
// Arrays with index structure
let dense: Array<Float like [LatIdx, LonIdx, TimeIdx]>

// Symmetric (shorthand - identical index types are mutually symmetric)
let cov: Array<Float like [I, I] where symmetry>
let coskew: Array<Float like [I, I, I] where symmetry>

// Explicit symmetry groups by position
let block: Array<Float like [I, I, K, K] where symmetry=[0=1, 2=3]>
let partial: Array<Float like [I, I, K] where symmetry=[0=1]>
let weird: Array<Float like [I, I, I, I] where symmetry=[0=2, 1=3]>

// Same type, no symmetry (dense)
let matrix: Array<Float like [I, I]>
```

**Symmetry clause summary:**

| Clause | Meaning |
|--------|---------|
| (none) | Dense, no symmetry |
| `where symmetry` | All identical index types are mutually symmetric |
| `where symmetry=[0=1=2]` | Explicit symmetry groups by position |

**Notes:**
- File-derived arrays get types from type providers (deferred)
- `type` for type aliases and definitions
- `let` for value bindings

#### 3.4.12 Arrays as Lambdas

An array `A : Array<T, I₁, I₂, ..., Iₙ>` is semantically a function:

```
A : I₁ → I₂ → ... → Iₙ → T
```

Indexing is function application:

```
A[i]       = A applied to i           : I₂ → ... → Iₙ → T
A[i][j]    = (A applied to i) to j    : I₃ → ... → Iₙ → T
A[i][j][k] = ...                      : T
```

**Key insight:** The array doesn't care *how* you compute the index—only *what* value it resolves to. Any expression producing a valid index is a valid index:

```
A[42]                     -- literal
A[i + 1]                  -- arithmetic
A[f(x)]                   -- function result
A[if c then i else j]     -- conditional
A[stencil_offset(k)]      -- computed offset
```

Index types define valid addresses—the *domain* of the array-as-function—not how addresses are computed.

#### 3.4.13 Poly-Indexing

Standard indexing uses named indices applied sequentially:

```
A[i][j][k]  -- three named indices
```

**Poly-indexing** uses a variable-length tuple of anonymous indices:

```
A[indices]  -- indices : Tuple(Idx...), length = rank(A)
```

This parallels arity polymorphism for arrays:

| Arity Polymorphism | Poly-Indexing |
|--------------------|---------------|
| Variable number of arrays | Variable number of indices |
| `args[|k|]` accesses k-th array | `indices[|k|]` accesses k-th index |
| Kernel doesn't name arrays | Indexing doesn't name indices |
| `poly(args)` declares polymorphism | Rank determines index count |

**Example: Rank-polymorphic trace**

```
function trace(A) -> Float
{
    out = 0
    for i in 0..extent(A, 0) {
        let indices = replicate(i, rank(A))  -- (i, i, i, ...) 
        out = out + A[indices]
    }
}

trace(matrix)    -- sum of M[i,i]
trace(tensor3)   -- sum of T[i,i,i]
trace(tensor4)   -- sum of T[i,i,i,i]
```

**Example: Rank-polymorphic iteration**

```
function sum_all(A) -> Float
{
    out = 0
    for indices in all_indices(A) {
        out = out + A[indices]
    }
}
```

The `all_indices(A)` iterator generates all valid index tuples for array A, respecting its structure (dense, ragged, symmetric, etc.).

#### 3.4.14 Lambda Indices

If indices are just "the thing you apply to get a value," they can be computations, not just values:

| Index Kind | Description |
|------------|-------------|
| `Int` | Standard direct index |
| `Expr` | Symbolic/deferred index |
| `Offset → Int` | Stencil pattern |
| `Dual` | Forward-mode AD (value + tangent) |
| `() → Int` | Lazy/thunk index |

**The array doesn't care what it receives—only that it resolves to a valid address.**

**Structural vs Computational Indices:**

Two concerns must be separated:

1. **Structural index type:** Determines memory layout, hashable, used for type checking
2. **Computational index:** What you pass at access time, can be richer

Example:
```
A : Array<Float, Idx<100>>          -- structural type

A[42]                                -- plain Int
A[Dual(42, 1.0)]                     -- carries tangent for AD
A[Symbolic("i")]                     -- unevaluated, resolves later
```

All resolve to positions in `Idx<100>`. The structural index is the "skeleton"; computational indices wrap it with additional information.

**The Bijection Requirement:**

For fast access, the structural index type must provide a bijection:

```
IndexType = {
    forward  : LogicalPosition → StorageOffset
    backward : StorageOffset → LogicalPosition
}
```

- `forward`: Given a logical position, compute where in memory
- `backward`: Given a storage offset, recover logical position (for iteration)

This is exactly what `CompoundIdx<mask>` provides for sparse ocean data, and what left-justification provides for symmetric arrays.

### 3.5 Array Types

We write array types as:

```
T^r(σ)
```

where T is the element type, r is the rank, and σ is the symmetry vector. When σ consists of all distinct elements (dense array), we may omit it:

```
T^r ≡ T^r(1, 2, ..., r)
```

**Type formation rule**:

```
T : BaseType    r ∈ ℕ    σ ∈ ℕÊ³
─────────────────────────────────
       T^r(σ) : ArrayType
```

#### 3.5.1 Abstract vs Concrete Array Types

Blade distinguishes two levels of array typing:

**Abstract types: `T^r(σ)`**

Used in function signatures, typing rules, and arity-polymorphic contexts. Specifies element type, rank, and symmetry without committing to specific index types:

```
function sum(a: T^r) -> out: T^0           // any rank
function symmetric_op(a: T^r(σ)) -> ...    // with symmetry
k : (τ^r → τ)                              // arity-polymorphic kernel
```

In arity-polymorphic typing, `r` is a type-level variable. The kernel type `(τ^r → τ)` means "takes r arguments of type τ, returns τ"—where r is determined by how many arrays are supplied to `method_for`.

**Concrete types: `Array<T, I₁, ..., Iₙ, σ>`**

Used for data declarations. Specifies actual index types:

```
let data: Array<Float, LatIdx, LonIdx, TimeIdx>
let readings: Array<Float, Idx<n>, RaggedIdx<lengths>>
let cov: Array<Float, Idx<1000>, Idx<1000>, <1,1>>
```

**Relationship**: A concrete type satisfies an abstract type when element types match, ranks match, and symmetries are compatible. The arity-polymorphic typing judgment:

```
Γ ⊢ A₁ : Array<τ, n>  ...  Γ ⊢ Aáµ£ : Array<τ, n>
Γ ⊢ k : (τ^r → τ)
───────────────────────────────────────────────
Γ ⊢ method_for(A₁,...,Aáµ£) <@> k : Comp<τ^r(σ)>
```

Here concrete input types (`Array<τ, n>`) determine `r` by counting, and the abstract output type (`τ^r(σ)`) captures that the result has rank r with symmetry σ derived from commutativity analysis.

**Extents are values, not types**: Index types like `Idx<n>` reference extent n, but extents are runtime values. The type system tracks *structure* (rank, symmetry, index type categories) while extents flow through value-level computation.

### 3.6 Array Expressions

Array expressions (`ArrayExpr`) represent unevaluated array transformations. They enable compositional array manipulation while preserving Blade's cache-optimality guarantees through explicit materialization.

```
ArrayExpr<T, r, σ>    -- unevaluated array of element type T, rank r, symmetry σ
```

**Lifting and evaluation:**

```
pure    : Array<T,r,σ> → ArrayExpr<T,r,σ>       -- lift array to expression (implicit)
compute : ArrayExpr<T,r,σ> → Array<T,r,σ>       -- materialize expression
```

**Implicit lifting**: The `pure` lift is transparent to users. Array combinators accept both `Array` and `ArrayExpr`, automatically lifting as needed:

```
let B = transpose(A, [1,0])    // A is Array, implicitly lifted; B is ArrayExpr
let C = zip(A, B)              // A lifted again; C is ArrayExpr
let D = C |> compute           // D is Array
```

**Implicit materialization**: `method_for` accepts both `Array` and `ArrayExpr`. When given `ArrayExpr`, it materializes before constructing the loop:

```
let B = transpose(A, [1,0])         // ArrayExpr
method_for(B, B) <@> f |> compute   // B materialized, then loop constructed
```

This ensures cache-optimal layout before iteration begins.

**Laws:**

```
pure A |> compute  ≡  A              -- round-trip identity
```

### 3.7 Array Combinators

Array combinators operate on `ArrayExpr` and produce new `ArrayExpr` values. Materialization via `|> compute` produces actual arrays with cache-optimal layout.

#### Zip

Combines elements from multiple arrays along their common prefix dimensions into tuples (n-ary):

```
zip : ArrayExpr* → ArrayExpr
zip : ArrayExpr<T₁,r₁,σ₁> × ... × ArrayExpr<Tₙ,rₙ,σₙ> → ArrayExpr<Tuple(T₁^(r₁-k), ..., Tₙ^(rₙ-k)), k, σ₁>
      where k = min(r₁, ..., rₙ)
            σ₁ = intersect_symmetry(σ₁[0..k], ..., σₙ[0..k])
```

**Semantics**: At each position in the shared index space, zip produces a tuple of the remaining slices:

```
zip(A, B, C)[i₁]...[iₗ] = Tuple(A[i₁]...[iₗ], B[i₁]...[iₗ], C[i₁]...[iₗ])
```

**Symmetry intersection**: The output symmetry is where all inputs agree on the shared dimensions:

```
intersect_symmetry(⟨1,1⟩, ⟨1,1⟩) = ⟨1,1⟩    -- both symmetric → symmetric
intersect_symmetry(⟨1,1⟩, ⟨1,2⟩) = ⟨1,2⟩    -- disagree → no symmetry
intersect_symmetry(⟨1,2⟩, ⟨1,2⟩) = ⟨1,2⟩    -- both dense → dense
```

**Example:**

```
A : Float^3(1,1,2)   -- rank 3, dims 0,1 symmetric
B : Float^2(1,1)     -- rank 2, symmetric

zip(A, B) : Tuple(Float^1, Float)^2(1,1)
-- At [i][j]: Tuple of (A[i][j] : Float^1, B[i][j] : Float)
```

**Tuple element type**: When `method_for` receives an array with `Tuple` element type, the kernel receives a single tuple argument that must be unpacked in the kernel body:

```
let paired = zip(A, B)
method_for(paired) <@> f
// f : (Tuple(T,U)^0) -> T^0, must unpack tuple internally
```

#### Align

Wraps `zip` with stencil metadata, producing an `AlignedExpr` that `method_for` unpacks into separate kernel arguments:

```
align : ArrayExpr* × StencilSpec → AlignedExpr

StencilSpec = {
    dims: [Dim],              -- which dimensions have stencil structure
    offsets: [[Offset]],      -- offsets per dimension
    boundary: Boundary        -- boundary handling
}

Boundary = Shrink | Pad(T) | Periodic | Reflect
```

**Semantics**: `align` bundles arrays that should iterate together and arrive as separate kernel arguments, along with metadata for bounds adjustment and halo inference.

```
let windowed = align(
    shift(A, 0, -1),
    A,
    shift(A, 0, 1),
    spec: {dims: [0], offsets: [[-1, 0, 1]], boundary: Shrink}
)

method_for(windowed) <@> f
// f : (T^0, T^0, T^0) -> T^0, receives 3 separate arguments
```

**Contrast with zip**:

| Combinator | Result type | method_for behavior |
|------------|-------------|---------------------|
| `zip(A, B, C)` | `ArrayExpr<Tuple(...), r, σ>` | Kernel receives 1 tuple argument |
| `align(A, B, C, spec)` | `AlignedExpr` | Kernel receives N separate arguments |

#### Stencil (sugar)

Syntactic sugar for constructing aligned shifted arrays:

```
stencil : ArrayExpr<T,r,σ> × Dict[Dim, List[Offset]] × Boundary → AlignedExpr
```

**Desugaring**:

```
stencil(A, {0: [-1, 0, 1]}, Shrink)

// desugars to:
align(
    shift(A, 0, -1, Shrink),
    A,
    shift(A, 0, 1, Shrink),
    spec: {dims: [0], offsets: [[-1, 0, 1]], boundary: Shrink}
)
```

**Multi-dimensional stencils**:

```
stencil(A, {0: [-1, 0, 1], 1: [-1, 0, 1]}, Shrink)
// 3×3 stencil, kernel receives 9 arguments
```

#### Stack

Combines arrays along a new leftmost dimension (n-ary):

```
stack : ArrayExpr<T,r,σ>* → ArrayExpr<T, r+1, ⟨fresh⟩++σ>
```

**Semantics**:

```
stack(A, B, C)[0] = A
stack(A, B, C)[1] = B
stack(A, B, C)[2] = C
```

The new dimension has extent equal to the number of arguments. Its symmetry class is fresh (distinct from all classes in σ), since index 0, 1, 2 refer to semantically different arrays.

#### Array Fallback (<|:>)

Provides nullptr-safe array access for sparse allocation patterns:

```
(<|:>) : ArrayExpr<T,r,σ> × ArrayExpr<T,r,σ> → ArrayExpr<T,r,σ>
```

**Semantics**: `(A <|:> B)[i]` returns `A[i]` if allocated, otherwise `B[i]`. The check occurs at each curry level during traversal.

**Ordering constraint**: The first argument's structure dominates iteration order. This preserves cache-optimality—we iterate in A's memory layout, falling back to B only for missing data.

**Symmetry note**: If A is symmetric, its allocation pattern must also be symmetric (if `A[i][j]` is allocated, so is `A[j][i]`). Access uses canonical (sorted) indices, ensuring consistent nullptr checks. If B lacks matching symmetry, the result may not satisfy the declared symmetry—this is user responsibility.

#### Transpose

Reorders dimensions:

```
transpose : ArrayExpr<T,r,σ> × Perm → ArrayExpr<T,r,σ'>
            where Perm is a permutation of [0..r-1]
                  σ' = permute(σ, Perm)
```

**Semantics**: `transpose(A, [1,0,2])[i][j][k] = A[j][i][k]`

Transpose produces a new array with reordered memory layout upon materialization. This is a "hard" transpose—actual data rearrangement, not a view.

#### Diagonal

Extracts elements where specified dimensions have equal indices:

```
diag : ArrayExpr<T,r,σ> × (Dim, Dim) → ArrayExpr<T,r-1,σ'>
```

**Semantics**: `diag(A, (0,1))[i][k] = A[i][i][k]`

Reduces rank by collapsing two dimensions into one.

#### Join

Joins arrays along a dimension:

```
join : ArrayExpr<T,r,σ> × ArrayExpr<T,r,σ> × Dim → ArrayExpr<T,r,σ>
```

**Semantics**: `join(A, B, d)` produces an array where dimension d has extent `extent_A[d] + extent_B[d]`.

#### Subset

Extracts a contiguous subrange along a dimension:

```
subset : ArrayExpr<T,r,σ> × Dim × (Start, End) → ArrayExpr<T,r,σ>
```

**Semantics**: `subset(A, d, (s, e))` produces an array with indices `[s..e)` along dimension d.

#### Split

Divides an array along a dimension (syntactic sugar):

```
split : ArrayExpr<T,r,σ> × Dim × Index → (ArrayExpr<T,r,σ>, ArrayExpr<T,r,σ>)
```

**Semantics**: `split(A, d, i)` produces `(subset(A, d, (0, i)), subset(A, d, (i, extent)))`.

#### Reverse

Reverses indices along a dimension:

```
reverse : ArrayExpr<T,r,σ> × Dim → ArrayExpr<T,r,σ>
```

**Semantics**: `reverse(A, d)[..., i, ...] = A[..., n-1-i, ...]` where n is the extent of dimension d.

#### Shift

Shifts indices along a dimension (for stencil construction):

```
shift : ArrayExpr<T,r,σ> × Dim × Offset × Boundary → ArrayExpr<T,r,σ>

Boundary = Shrink | Pad(T) | Periodic | Reflect
```

**Semantics**: `shift(A, d, k)[..., i, ...] = A[..., i+k, ...]` with boundary handling.

### 3.8 Array Combinator Laws

**Zip:**

```
zip(A) ≡ Tuple(A)                               (singleton wraps in tuple)
```

**Stack:**

```
stack(A)[0] ≡ A                                 (singleton)
stack(A, B, C)[i] ≡ [A, B, C][i]               (indexing selects array)
```

**Transpose:**

```
transpose(A, id) ≡ A                            (identity permutation)
transpose(transpose(A, p), q) ≡ transpose(A, q∘p)   (composition)
transpose(transpose(A, p), p⁻¹) ≡ A             (inverse)
```

**Join/Subset/Split:**

```
let (left, right) = split(A, d, i)
join(left, right, d) ≡ A                        (split-join round-trip)

join(subset(A,d,(0,i)), subset(A,d,(i,n)), d) ≡ A    (subset-join round-trip)
```

**Fallback:**

```
A <|:> A ≡ A                                    (idempotent)
(A <|:> B) <|:> C ≡ A <|:> (B <|:> C)          (associative)
```

**Reverse:**

```
reverse(reverse(A, d), d) ≡ A                   (involution)
```

---

## 4. Functions

### 4.1 Function Signatures

A function f has signature:

```
f : (T₁^r₁, T₂^r₂, ..., Tₙ^rₙ) → T_out^r_out
```

with associated metadata:

- **Commutativity vector** c ∈ ℕⁿ: cáµ¢ = cⱼ means arguments i and j are interchangeable
- **Parallelism specification** p : Map(ArgName → ℕ): For each argument, the number of its S-dimension loops to parallelize. Since arrays are bound in order, their S-dimensions nest in order; typically the first array's loops are outermost and most beneficial to parallelize. The `omp` clause could be substituted with other parallel backends (e.g., `acc` for OpenACC, `cuda` for CUDA).
- **T-dimension specification** (if r_out > 0 and output dimensions don't derive from inputs): Each T-dimension is specified with its extent, symmetry class, and optional name.

### 4.2 Function Syntax

```
function name(
    x₁: T₁^r₁,
    x₂: T₂^r₂,
    ...
    xₙ: Tₙ^rₙ
) -> out: T_out^r_out
where
    comm(xáµ¢, xⱼ, ...),       // commutativity groups
    omp(xₗ: depth, ...),     // parallelism: depth levels per argument
    tdim(                     // T-dimension specification (optional)
        { extent: expr, symm: k, name: "freq" },
        { extent: expr, symm: k, name: "mode" },
        ...
    )
{
    // kernel body (user-written code)
}
```

### 4.3 Commutativity Groups

Given a commutativity specification `comm(x₁, x₂, ..., xₘ)`, we construct the commutativity vector c as:

```
cáµ¢ = cⱼ  iff  xáµ¢ and xⱼ appear in the same comm() clause
```

Arguments not appearing in any comm() clause are in singleton groups.

**Example**:
```
fn f(a, b, c, d) where comm(a, b, c)
```
yields c = ⟨1, 1, 1, 4⟩.

---

## 5. Loop Objects

### 5.1 The Core Abstraction

A *loop object* reifies an iteration pattern as a first-class value. There are two dual constructions:

**Method Loop (S-first)**: Binds arrays, awaits function
```
method_for : A* → MethodLoop
```

**Object Loop (kernel-first)**: Binds function, awaits arrays
```
object_for : Function → ObjectLoop
```

The method_for/object_for distinction is about *construction order* (bind arrays first vs. bind function first), not about different dimension types.

### 5.2 S-Dimensions and T-Dimensions

Loop iteration involves two dimension types:

**S-dimensions (Spatial/Structural)**: Arise from iterating over input arrays. The iteration structure—nesting depth, bounds, triangular constraints—is determined by input array ranks and symmetries.

**T-dimensions (Temporal/Trailing)**: Added by the function's output when it produces dimensions not derived from iteration. The name reflects both the temporal nature (by analogy to spatial S-dimensions) and the trailing position in output arrays. Example: FFT transforms time → frequency.

For a function f applied to arrays A₁, ..., Aₙ:

- S-dimension count = Σáµ¢ (rank(Aáµ¢) - irank(f, i))
- T-dimension count = f.ORank

Output rank = S-dimensions + T-dimensions

**Definition (Input Rank)**: `irank(f, i)` is the *input rank* of the i-th argument to kernel f—the rank of that argument as seen within the kernel scope after loop iteration has indexed into the outer dimensions. When a rank-r array is bound to a loop with k S-dimensions, the kernel receives elements of rank (r - k). If the kernel signature declares argument i with type `T^m`, then `irank(f, i) = m`.

For example, if `data` has rank 2 (a matrix) and the kernel signature expects `float^0` (scalars), then `irank = 0` and the loop iterates over both dimensions of the matrix, yielding 2 S-dimensions per array.

### 5.3 Method Loop Structure

A MethodLoop M contains:

- **arrays**: A₁, ..., Aₙ (bound input arrays)
- **S-structure**: The iteration pattern derived from array ranks/symmetries
- **Awaiting**: A function to apply

```
M = method_for(A₁, ..., Aₙ)
```

The S-dimension structure is fixed at construction. Different functions can be applied to the same MethodLoop, sharing iteration structure.

### 5.4 Object Loop Structure

An ObjectLoop O contains:

- **func**: f (bound function)
- **Awaiting**: Arrays to iterate over

```
O = object_for(f)
```

The function is fixed at construction. Different arrays can be passed to the same ObjectLoop.

### 5.5 Partial Application Semantics

Loop objects are *partial*—they await completion:

```
MethodLoop × Function → Computation
ObjectLoop × Arrays → Computation
```

A Computation is a complete, executable specification.

### 5.6 The Structural Trinity: Formal Necessity Proofs

This section proves that **loop reification**, **arity polymorphism**, and **dimensional currying** form an inseparable trinity—each requires the other two.

#### 5.6.1 Definitions

**Loop Reification**: Iteration patterns exist as first-class values that can be constructed, inspected, composed, and transformed.

**Arity Polymorphism**: The number of input arrays (arity r) determines output tensor rank, loop nesting depth, and symmetry structure.

**Dimensional Currying**: Arrays are functions from indices to values; a rank-r array has type `N → N → ... → N → T` (r arrows), and partial indexing yields lower-rank arrays.

**Left-Justified Triangular Iteration**: For arity r with commutativity, bounds are:
```
for i₀ in [0, n):
    for i₁ in [0, n - i₀):
        for i₂ in [0, n - i₀ - i₁):
            ...
```

The bound for loop k is `bound_k = n - Σ_{m=0}^{k-1} i_m`.

#### 5.6.2 Scope and Expressibility Theorems

**Theorem 5.1 (Cumulative Scope Dependency)**: In left-justified triangular iteration of arity r, the bound expression for loop k requires simultaneous access to all k preceding index variables {i₀, ..., i_{k-1}}.

*Proof*: The expression `bound_k = n - Σ_{m<k} i_m` contains k distinct free variables. Evaluating `bound_k` requires all k variables simultaneously in scope. ∎

**Theorem 5.2 (Lexical Nesting Requirement)**: In a language with lexical scoping, expressing arity-r left-justified triangular iteration requires r textually nested loop constructs.

*Proof*: By Theorem 5.1, `bound_k` requires {i₀, ..., i_{k-1}} in scope. In lexical scoping, variable i_m is in scope only within loop m's body. For all preceding indices to be in scope at loop k, loop k must be textually nested inside loops 0 through k-1.

By induction: Loop 0 has no dependencies. Loop k requires {i₀, ..., i_{k-1}}, so must be inside loop k-1, which inductively is inside loops 0..k-2. Therefore arity r requires r nesting levels. ∎

**Theorem 5.3 (Fixed-Text Impossibility)**: No fixed textual program (without metaprogramming) can express left-justified triangular iteration for arbitrary arity r.

*Proof*: By Theorem 5.2, arity r requires r nested loop constructs. A fixed program has fixed nesting depth N. For r > N, the program cannot express arity-r iteration. No single fixed definition works for all r. ∎

**Theorem 5.4 (Recursion Obscures Structure)**: Encoding N-ary left-justified iteration via recursion prevents static determination of loop bounds, iteration counts, commutativity eligibility, and fusion opportunities.

*Proof sketch*: In recursive encoding, structure is implicit in recursion depth and parameter passing. Static bound analysis requires tracing all recursive calls—undecidable in general. Explicit r-nested loops have syntactically visible, statically analyzable bounds. ∎

**Theorem 5.5 (Index Names Require Positions)**: Meaningful index names (for declarations like `comm(a, b)`) require either textually distinct loop constructs or a reified structure with identified positions.

*Proof*: Resolving `comm(a, b)` requires identifying which indices correspond to positions a and b. Recursive encoding uses implicit list indices (`indices[0]`, `indices[1]`), forcing positional syntax. Named indices require either:
1. Textual loop constructs (one per index), failing for variable arity by Theorem 5.3, or
2. First-class loop structures with identified positions. ∎

#### 5.6.3 The Trinity Theorems

**Theorem 5.6 (Arity Polymorphism Requires Loop Reification)**: A system with arity-polymorphic kernels—where arity r determines r-deep nested iteration with cumulative bounds—must have first-class loop representations.

*Proof*: An arity-polymorphic kernel:
```
function moment(args) where arity(any), comm(args)
```
when applied to r arrays requires r-deep nested iteration with left-justified bounds and triangular optimization from commutativity.

By Theorem 5.3, no fixed textual program expresses this for arbitrary r. The loop structure must be generated dynamically based on r AND represented as a manipulable value for commutativity analysis and bound computation. This is precisely loop reification. ∎

**Theorem 5.7 (Arity Polymorphism Requires Dimensional Currying)**: A system where arity r determines output rank r must have arrays-as-functions typing.

*Proof*: An arity-polymorphic kernel produces output whose rank equals the input arity:
```
method_for(A, A)       → rank-2 output
method_for(A, A, A)    → rank-3 output  
method_for(A, A, A, A) → rank-4 output
```

To type "output rank equals arity r" requires a type parameterized by r:
```
Output : N^r → T    (r-ary function type)
```

This is the curried array type. Without dimensional currying, each arity requires a separate, unrelated output type (`T[n][n]`, `T[n][n][n]`, etc.) with no polymorphic relationship. ∎

**Theorem 5.8 (Loop Reification Requires Dimensional Currying for Left-Justified Output)**: Left-justified triangular iteration produces arrays whose extents depend on index values, requiring dependent curried types.

*Proof*: In left-justified iteration:
```
for i in [0, n):
    Output[i] has extent (n - i)
    for j in [0, n-i):
        Output[i][j] has extent (n - i - j)
```

The extent of `Output[i]` depends on the value of `i`. Standard array typing gives `Output[i]` a fixed type regardless of `i`.

With dimensional currying, we can express the dependent type:
```
Output : (i: N) → Array<T, n-i>
```

The type of `Output[i]` depends on `i`. This dependent function type is exactly what dimensional currying provides—arrays as functions with potentially dependent return types.

Without this, left-justified indexing cannot be correctly typed: the compiler cannot verify that `Output[i][j]` is in-bounds when the bound depends on `i`. ∎

**Theorem 5.9 (Dimensional Currying Requires Loop Reification for Bound Computation)**: Computing the dependent extent `n - Σ_{m<k} i_m` requires access to the loop structure.

*Proof*: The extent of `Output[i₀][i₁]...[i_{k-1}]` is `n - i₀ - i₁ - ... - i_{k-1}`.

To compute this extent, the system must know:
1. Which indices have been bound (i₀ through i_{k-1})
2. The values of those indices
3. The original extent n

This information constitutes the loop structure—specifically, which level of the nested iteration we're at and what index values have been fixed. This is loop reification: the loop state must exist as an inspectable value to compute dependent bounds. ∎

#### 5.6.4 The Inseparability Theorem

**Theorem 5.10 (Trinity Inseparability)**: Loop reification, arity polymorphism, and dimensional currying are mutually necessary. Removing any one makes the other two inexpressible.

*Proof*: We show each feature requires the other two:

**(1) Arity polymorphism requires both:**

- Requires loop reification by Theorem 5.6 (cannot generate r-deep nests otherwise)
- Requires dimensional currying by Theorem 5.7 (cannot type rank-r output otherwise)

**(2) Loop reification requires both:**

- Requires dimensional currying by Theorem 5.8 (cannot type left-justified output otherwise)
- Requires arity polymorphism for generality: without variable arity, loop reification is limited to fixed depths, reducible to textual nesting

**(3) Dimensional currying requires both:**

- Requires loop reification by Theorem 5.9 (cannot compute dependent bounds otherwise)
- Requires arity polymorphism: without variable arity, currying depth is fixed and dependent extents become simple constants

The three features form a dependency cycle with no valid subset:
```
        Arity Polymorphism
              →   →
             /     \
            →       →
Loop Reification ←→ Dimensional Currying
```

Each edge represents a necessity proof (Theorems 5.6-5.9). ∎

**Corollary 5.11 (Unified Contribution)**: The three features constitute a single, indivisible contribution to programming language theory. Claims of novelty apply to the trinity as a whole, not to individual components.

#### 5.6.5 The Symmetry Tower and Lowering Homomorphisms

The trinity implements a deeper structure: **symmetry lowering** across a hierarchy of computational levels.

**Definition (Symmetry Levels)**:
- **Level 0 (Elements)**: Symmetry is identity. `a = a`.
- **Level 1 (Arrays)**: Symmetry is index permutation. `A[i,j] = A[j,i]` for σ = (1 2) ∈ S₁₂.
- **Level 2 (Functions)**: Symmetry is argument permutation. `f(x,y) = f(y,x)` for commutative f.
- **Level 3 (Combinators)**: Symmetry is composition structure. Associativity, MonadPlus laws.

**Definition (Symmetry Groups)**:
- `Sym₀ = {id}` — the trivial group
- `Sym₁(r)` — subgroups of Sáµ£ acting on r index positions  
- `Sym₂(n)` — subgroups of Sₙ acting on n argument positions

**Definition (Symmetric Objects)**:

A rank-r array A has symmetry H ∈ Sym₁(r) when:
```
∀σ ∈ H : A[i_{σ(1)}, ..., i_{σ(r)}] = A[i₁, ..., iáµ£]
```

An arity-n function f has symmetry H ∈ Sym₂(n) when:
```
∀σ ∈ H : f(x_{σ(1)}, ..., x_{σ(n)}) = f(x₁, ..., xₙ)
```

**Theorem 5.12 (Lowering `lower₂₁`: Commutativity → Array Symmetry)**:

Let `f : Tⁿ → T` have symmetry H ≤ Sₙ. Let `A : I → T` be an array. Define:
```
Out[i₁, ..., iₙ] = f(A[i₁], ..., A[iₙ])
```

Then Out has symmetry H.

*Proof*: Let σ ∈ H.
```
Out[i_{σ(1)}, ..., i_{σ(n)}]
  = f(A[i_{σ(1)}], ..., A[i_{σ(n)}])     [definition]
  = f(y_{σ(1)}, ..., y_{σ(n)})           [let yⱼ = A[iⱼ]]
  = f(y₁, ..., yₙ)                        [f has symmetry σ]
  = Out[i₁, ..., iₙ]                      ∎
```

**Corollary 5.13**: The map `lower₂₁ : Sym₂(n) → Sym₁(n)` defined by `lower₂₁(H) = H` is an isomorphism when all array arguments are identical.

**Theorem 5.14 (Lowering with Distinct Arrays)**:

Let `f : Tⁿ → T` have symmetry H ≤ Sₙ. Let `A₁, ..., Aₙ : I → T` be arrays. Define:
```
Stab(A₁,...,Aₙ) = {σ ∈ Sₙ : ∀j. A_{σ(j)} = Aⱼ}
```

Then `Out[i₁, ..., iₙ] = f(A₁[i₁], ..., Aₙ[iₙ])` has symmetry `H ∩ Stab(A₁,...,Aₙ)`.

*Proof*: Let σ ∈ H ∩ Stab(A₁,...,Aₙ).
```
Out[i_{σ(1)}, ..., i_{σ(n)}]
  = f(A₁[i_{σ(1)}], ..., Aₙ[i_{σ(n)}])
  = f(A_{σ(1)}[i_{σ(1)}], ..., A_{σ(n)}[i_{σ(n)}])   [σ ∈ Stab]
  = f(y₁, ..., yₙ)                                     [yⱼ = A_{σ(j)}[i_{σ(j)}], then apply σ⁻¹]
  = Out[i₁, ..., iₙ]                                   [f has symmetry σ]  ∎
```

**Corollary 5.15**:
- All arrays identical ⟹ Stab = Sₙ ⟹ `lower₂₁(H) = H` (full transfer)
- All arrays distinct ⟹ Stab = {id} ⟹ `lower₂₁(H) = {id}` (no transfer)

**Theorem 5.16 (Lowering `lower₁₀`: Array Symmetry → Identity)**:

The map `lower₁₀ : Sym₁(r) → Sym₀` sending every permutation to identity is the unique homomorphism to the trivial group.

*Interpretation*: Reading elements from a symmetric array "consumes" the symmetry. The permutation σ ∈ Sym₁(r) guarantees `A[σ(i)] = A[i]`, but both sides denote the same element—this is just Level 0 identity.

**Theorem 5.17 (Input Symmetry Does Not Propagate)**:

Let `f : T² → T` have trivial symmetry (non-commutative). Let `A : I² → T` have symmetry S₁₂. Define `Out[i,j] = f(A[i,0], A[j,1])`.

Then Out has trivial symmetry.

*Proof*: `Out[j,i] = f(A[j,0], A[i,1]) ≠  f(A[i,0], A[j,1]) = Out[i,j]` in general, since f is non-commutative. The symmetry of A is irrelevant—it was consumed when elements were read. ∎

**Summary (The Lowering Principle)**:

| Homomorphism | Domain | Codomain | Structure |
|--------------|--------|----------|-----------|
| `lower₁₀` | Sym₁(r) | Sym₀ | Trivial (all symmetries → identity) |
| `lower₂₁` | Sym₂(n) | Sym₁(n) | Isomorphism when arrays identical |

Symmetry at level N lowers to level N-1 when objects are applied. Since `lower₁₀` is trivial, input array symmetry vanishes into element identity. Since `lower₂₁` is an isomorphism (for identical arrays), function commutativity transfers to output array symmetry. Both phenomena—input symmetry "quashing" and output symmetry "generation"—are instances of the same lowering structure.

**Theorem 5.18 (Trinity Implements Lowering)**:

The Structural Trinity provides the machinery to compute and exploit `lower₂₁`:

1. **Arity polymorphism** determines the domain—arity n specifies which Sₙ we lower from
2. **Dimensional currying** makes the codomain explicit—indices are arguments, so Sym₁ and Sym₂ share representation  
3. **Loop reification** captures the lowered symmetry—the loop structure encodes which symmetries survived, determining triangular vs rectangular iteration

*Proof*: By Theorem 5.10, the three features are mutually necessary. Computing `lower₂₁` requires knowing arity (which Sₙ), treating indices as arguments (shared representation), and representing the result structurally (loop object with symmetry metadata). Each feature provides exactly one component. ∎

#### 5.6.6 Level 3 and Beyond: The First-Class Function Collapse

With first-class functions, there is no structural difference between a function taking values and a function taking functions:

```
f   : T² → T           -- binary on values (Level 2)
<&> : Comb² → Comb     -- binary on combinators (Level 3)
```

Both are binary operations on some type. The "level" is determined by what you feed in, not by intrinsic structure. This means **Level 3+ collapses into Level 2** — it's the same Sₙ symmetry on arguments, applied recursively to higher-order functions.

**The Effective Tower**:
```
Level 0:  Elements (identity only)
Level 1:  Arrays (Sₙ on indices, physical storage)
Level 2+: Functions (Sₙ on arguments, all the way up)
```

Levels 0 and 1 are special:
- Level 0 has no exploitable structure
- Level 1 has *spatial* structure — memory layout, cache behavior, triangular storage

Level 2+ is "just algebra." Symmetries are free to permute at runtime; the computational payoff comes from **lowering to Level 1** where symmetry becomes physical bytes and avoided FLOPS.

**Lifting Combinators via `method_for` and `object_for`**:

The `method_for`/`object_for` duality extends to combinators themselves:

```
method_for(<&>) : [f, g, h, ...] → f <&> g <&> h <&> ...
method_for(>>) : [f, g, h, ...] → f >> g >> h >> ...
```

This is precisely `fold` — lifting a binary combinator to n-ary. Associativity of `<&>` and `>>` makes this well-defined (parenthesization doesn't matter).

```
object_for(<&>)(f) = λg. f <&> g     -- curried: build parallel composition incrementally
object_for(>>)(f) = λg. f >> g       -- curried: build pipeline incrementally
```

**Dynamic Kernel Construction**:

With `object_for(>>)`, kernels can be assembled programmatically at the top level:

```
let pipeline = object_for(>>)
let kernel = pipeline(normalize)(log_transform)(clip(0,1))(scale(255))

// kernel = normalize >> log_transform >> clip(0,1) >> scale(255)
// Now apply to data:
method_for(A) <@> kernel
```

The kernel is *data* — a value constructed, inspected, and transformed — until applied to arrays and lowered to Level 1.

**S/T All The Way Up**:

This extends the S/T philosophy to kernel construction itself:

| Level | S/T Pattern |
|-------|-------------|
| 3 | `method_for(<&>)([stats...])` — build combinator structure from list |
| 2 | `method_for(A, A, A)` — build iteration structure from arrays |
| 1 | Triangular storage — physical realization of symmetry |

Structure is built top-down; data flows bottom-up. The entire computation is *shaped* before any data is touched.

---

## 6. Arity Polymorphism

### 6.1 Distinction from Rank Polymorphism

Array programming languages have long supported *rank polymorphism*—the ability for functions to operate uniformly across arrays of different ranks (shapes). Systems like APL, J, and Remora (Slepak et al.) formalize how a scalar function lifts to operate on vectors, matrices, and higher-rank tensors.

**Rank polymorphism**: One array, varying shape
```
sum : T^r → T^0    // works for any rank r
```

Blade-DSL introduces a distinct concept: *arity polymorphism*—the ability for loop structures to adapt to varying numbers of input arrays, where the arity itself determines symmetry structure and output rank.

**Arity polymorphism**: Varying number of arrays, fixed kernel
```
method_for(A, A)       → rank-2 output, 2! = 2× speedup
method_for(A, A, A)    → rank-3 output, 3! = 6× speedup
method_for(A, A, A, A) → rank-4 output, 4! = 24× speedup
```

### 6.2 Why Arity Polymorphism Matters

For comoment tensors, arity is fundamental:

| Statistic | Arity | Output Rank | Symmetry |
|-----------|-------|-------------|----------|
| Covariance | 2 | 2 | Full |
| Coskewness | 3 | 3 | Full |
| Cokurtosis | 4 | 4 | Full |
| nth comoment | n | n | Full |

The *same kernel* (product of elements) applied at *different arities* produces tensors of different orders with different symmetry exploitation. This is not expressible in rank-polymorphic systems, which vary the shape of a single input, not the number of inputs.

### 6.3 Arity and Commutativity

Arity polymorphism interacts with commutativity to determine loop structure:

```
function moment(args) -> out: float^0
where
    arity(any),
    comm(args)    // all arguments commutative
{
    out = product(args)
}
```

When instantiated at arity n with the same array A:

- Creates n-deep nested loop over A
- All arguments in same commutativity group → fully symmetric output
- Triangular iteration with n! speedup

When instantiated at arity n with different arrays:

- Creates n-deep nested loop
- Commutativity checked at runtime: same array in commutativity group → triangular; different arrays → rectangular
- System validates and exploits what symmetry is actually present

### 6.4 Arity-Polymorphic Syntax

Arity-polymorphic kernels accept a variable number of arguments. The concrete syntax uses **tuples** to represent argument packs, avoiding the ellipsis (`...`) spread syntax common in other languages.

#### 6.4.1 Fixed Arity (Named Arguments)

For kernels with known, fixed arity, use named arguments:

```
function coskewness(a: T^0, b: T^0, c: T^0) -> out: T^0
where comm(a, b, c)
{
    out = a * b * c
}
```

No `arity(any)` clause needed—arity is determined by the argument list.

#### 6.4.2 Variable Arity (Tuple-Based)

For kernels that operate on any number of arguments:

```
function product(args) -> out: T^0
where arity(any), comm(args)
{
    let (head, tail) = args
    out = head * product(tail)    // base case automatic: product(()) = 1
}
```

The `arity(any)` clause marks which parameters are variadic tuples.

#### 6.4.3 Tuple Destructuring

Left-associative tuple destructuring, paralleling dimensional currying:

```
let (head, tail) = args           // head: first element, tail: rest tuple
let (a, b, tail) = args           // a, b: first two, tail: rest
let (a, b, c) = args              // a, b: first two, c: rest (NOT exact match)
let (_, tail) = args              // discard first, get rest
let (head, _) = args              // get first, discard rest
```

**Pattern arity > tuple arity**: Excess names bind to `()` (unit):

```
let (a, b, c) = (X)               // a=X, b=(), c=()
let (head, tail) = ()             // head=(), tail=()
```

- In `arity(any)` scope: No warning (natural recursion base case)
- In fixed-arity scope: **Warning** emitted

**No right-associative patterns** — matches dimensional currying where indexing peels from the left.

#### 6.4.4 Indexed Access

Tuple indexing uses `[|k|]` to visually distinguish from array indexing:

```
args[|0|]                         // first element
args[|k|]                         // kth element (k can be runtime variable)
args[|arity - 1|]                 // last element
```

#### 6.4.5 Iteration

```
for k in 0..arity {               // exclusive upper bound: k ∈ [0, arity)
    out = out + args[|k|] * weights[k]
}
```

#### 6.4.6 Scope Variables

| Variable | Meaning | Available in |
|----------|---------|--------------|
| `arity` | Total argument count | Any `arity(any)` kernel |
| `nth` | Current recursion depth (0 at top) | Recursive kernels only |

#### 6.4.7 Recursive Pattern

```
function product(args) -> out: T^0
where arity(any), comm(args)
{
    let (head, tail) = args
    out = head * product(tail)    // base case automatic: product(()) = 1
}
```

No explicit base case needed — `f(())` returns identity element for `f`.

#### 6.4.8 Iterative Pattern

```
function weighted_sum(args, weights: T^1) -> out: T^0
where arity(any, args), comm(args)
{
    out = 0
    for k in 0..arity {
        out = out + args[|k|] * weights[k]
    }
}
```

#### 6.4.9 Syntax Summary

| Feature | Syntax |
|---------|--------|
| Variadic declaration | `where arity(any)` or `where arity(any, paramName)` |
| Destructure | `let (head, tail) = args` |
| Index | `args[|k|]` |
| Count | `arity` |
| Recursion depth | `nth` |
| Iteration | `for k in 0..arity { ... }` |
| Identity base case | Implicit via `f(())` |

#### 6.4.10 Nested Tuples

Tuples can be nested, with structure preserved:

```
((A, B), C)                       // nested: 2 top-level elements
(A, B, C)                         // flat: 3 top-level elements
```

**Singleton collapse:** `(a) = a` — singleton tuples collapse to their element.

**Access rules:**
- Indexing `args[|k|]` accesses top-level positions only
- Nested access requires destructuring:
  ```
  let (ab, c) = args              // ab is (A, B)
  let (a, b) = ab                 // now a, b accessible
  // NOT: args[|0|][|1|]          // no deep indexing
  ```

**`arity` counts top-level:**
```
args = ((A, B), C, D)             // arity = 3
args = (A, B, C)                  // arity = 3
```

**`<*>` concatenates at top-level:**
```
((A, B), C) <*> (D)               // = ((A, B), C, D)
((A, B), C) <*> ((E, F))          // = ((A, B), C, (E, F))
```

**Loop construct rules:**
- `method_for(A, B, C)` — always flat
- `method_for(A, B) <*> method_for(C)` — concatenates flat: `(A, B, C)`
- `object_for(f) <@> tuple` — single `<@>` only, tuple structure preserved
- `object_for(f) <@> ((A, B), C)` — explicitly nested, f sees 2 top-level args

**Commutativity:**
- `comm(args)` applies to top-level positions only
- Nested structure respected — comm doesn't penetrate sub-tuples
- Swapping only valid for same-typed positions

### 6.5 Formal Treatment

An arity-polymorphic function has signature:

```
f : (T^r)* → T^0
    where * indicates variable arity
```

When applied via a loop object:

```
method_for(A₁, A₂, ..., Aₙ) <@> f
```

The system computes:

1. **Output rank**: Σáµ¢ (rank(Aáµ¢) - irank(f, i))
2. **Symmetry groups**: From commutativity annotation + which Aáµ¢ are identical
3. **Loop structure**: Triangular where symmetry allows, rectangular otherwise

**Typing rule for arity-polymorphic application**:

```
Γ ⊢ M : MethodLoop[A₁...Aₙ]    f : arity-polymorphic
compatible(n, f)
c = commutativity(f, n)
σ' = OutputSymmetry(A₁...Aₙ, c)
──────────────────────────────────────────────────────
         Γ ⊢ M <@> f : Comp[T^n(σ')]
```

### 6.6 Comparison to Related Work

#### 6.6.1 Arity Polymorphism vs Variadic Functions

Variadic functions (C++, Scheme, etc.) and Blade's arity polymorphism both accept varying numbers of arguments, but solve fundamentally different problems.

**Variadic function schema** (Strickland et al. ESOP 2009):
```
variadic : ∈(τ ...). (τ ... → σ)
```
The output type σ is *fixed* regardless of how many arguments are supplied.

**Blade arity-polymorphic schema**:
```
arity_poly : ∀r. (τ^r → τ) → (Array<τ,n>)^r → Array<τ, n^r, σ_r>
```
The output type *depends on* r: rank = r, shape = n^r, symmetry = σ_r.

**Theorem (Variadic Cannot Express Arity Polymorphism)**: Standard variadic typing cannot derive output rank from input count.

*Proof*: In variadic typing, the output type σ is determined before knowing argument count. To express "output rank equals input count" requires:

1. **Dependent types**: Output type depends on term-level value r
2. **Type-level naturals**: r available at the type level
3. **Type-level arithmetic**: Computing n^r as output shape

Standard variadic polymorphism provides none of these. ∎

**Theorem (Arity Polymorphism Requires Dependent Typing)**: Blade's arity polymorphism requires type-level representation of arity and type-level computation of output shape.

*Proof*: The typing judgment for `method_for`:
```
Γ ⊢ A₁ : Array<τ, n>  ...  Γ ⊢ Aáµ£ : Array<τ, n>
Γ ⊢ k : (τ^r → τ) with comm(...)
─────────────────────────────────────────────────
Γ ⊢ method_for(A₁,...,Aáµ£) <@> k : Comp<Array<τ, n^r, σ>>
```

The output type contains r (from counting inputs), n^r (type-level exponentiation), and σ (from commutativity analysis). This requires counting array arguments at the type level and computing output shape. ∎

**Summary of differences**:

| Aspect | Variadic Functions | Blade Arity Polymorphism |
|--------|-------------------|-------------------------|
| Output type | Fixed, independent of arity | Depends on arity (rank = r) |
| Output shape | Not affected | n^r (exponential in arity) |
| Symmetry | Not tracked | Derived from commutativity |
| Iteration | Linear fold over arguments | Nested loops (depth = r) |
| Type-level info | Argument count not in types | Arity reflected in output type |
| Triangular opt. | N/A | Automatic from commutativity |

**What they share**: Both accept varying argument counts, treat arguments uniformly, and require some iteration mechanism over the argument list.

**What's novel in Blade**: The specific integration where arity determines (1) output tensor rank, (2) loop nesting depth, (3) symmetry group structure, and (4) triangular iteration eligibility. Prior work on arity polymorphism (Moggi 2000, Weirich & Casinghino) addresses generic programming over arity, not arity-to-structure inference.

#### 6.6.2 Comparison to Rank Polymorphism

**Remora (Slepak et al.)**: Formalizes rank polymorphism with frame/cell decomposition. Functions lift across ranks via implicit mapping. Does not address varying arity or symmetry.

**Multidimensional Homomorphisms (Rasch)**: Generalizes structural recursion to multiple dimensions. Focuses on regular parallelism patterns. Does not address arity-dependent loop structure or symmetry.

**Blade-DSL**: Arity is a polymorphic axis. The number of inputs determines:

1. Loop nest depth
2. Output tensor rank  
3. Symmetry group structure
4. Triangular iteration eligibility

This combination is novel: treating arity as a first-class dimension of variation, with automatic symmetry inference based on which arrays occupy commutative positions.

---

## 7. Dimensional Currying

### 7.1 The Core Idea

Traditional array languages treat slicing as a data operation: `A[i, :, :]` returns a view or copy of a subset of A. Blade-DSL takes a different approach: **arrays are functions**, and indexing is **partial application**.

A rank-r array of element type T is conceptually a function:

```
A : ℕ → ℕ → ... → ℕ → T    (r indices)
```

Applying one index yields a rank-(r-1) array:

```
A[i] : ℕ → ℕ → ... → ℕ → T    (r-1 indices)
```

This is *dimensional currying*: each indexing operation binds the outermost dimension, returning a function that awaits the remaining dimensions.

### 7.2 Type-Level Encoding

The `promote` template encodes dimensional currying at the type level:

```cpp
promote<T, 0>::type = T           // scalar
promote<T, 1>::type = T*          // rank-1 array
promote<T, 2>::type = T**         // rank-2 array
promote<T, r>::type = T**...*     // r pointer levels
```

Indexing transforms types:

```
A    : promote<T, r>::type
A[i] : promote<T, r-1>::type
```

This is not merely pointer arithmetic—it's a type-level guarantee that each indexing operation peels off exactly one dimension.

### 7.3 Cache Optimality by Construction

The key insight: **if arrays are laid out with the outermost dimension varying slowest (row-major), then dimensional currying guarantees cache-optimal access**.

At each loop depth:

- Depth 0: Full arrays, iterating outermost dimension
- Depth 1: Curried arrays `A[i]`, iterating next dimension
- Depth k: Curried arrays `A[i][j]...[k]`, accessing contiguous memory

**The type system encodes cache-optimal access patterns. Non-optimal iteration order becomes a type error.**

Traditional array languages hope the compiler discovers good loop order. With dimensional currying, optimal order is the *only* order expressible in the type system.

### 7.4 Distinction from Slicing

| Aspect | Slicing | Dimensional Currying |
|--------|---------|---------------------|
| Semantics | Data subset | Function application |
| Memory | View into original (may have stride) | Pointer to contiguous subarray |
| Cache behavior | Depends on slice dimensions | Guaranteed optimal |
| Type | Same array type, different shape | Different type (reduced rank) |
| Composition | Ad-hoc | Enables combinator algebra |

**Example**:
```python
# Slicing (NumPy): A[:, i, :] may have non-contiguous memory
A[:, i, :]  # Shape (n, m), but stride may skip elements
```

```cpp
// Dimensional currying: A[i] is always contiguous
A[i]  // Type: promote<T, r-1>, pointing to contiguous block
```

### 7.5 Enabling the Combinator Algebra

Dimensional currying is what makes combinator fusion zero-overhead:

```cpp
(loop <@> f) <&!> (loop <@> g)
```

At each iteration point, both f and g receive curried arrays at the same depth. No intermediate full-rank arrays are materialized. The fusion happens at the *iteration level*, not the data level.

The combinators work because partially-curried arrays have compatible types:

- `A[i]` and `B[i]` both have type `promote<T, r-1>::type`
- They can be passed together to any function expecting rank-(r-1) inputs
- The type system guarantees this composition is valid

### 7.6 Symmetry Integration

Dimensional currying composes cleanly with symmetric storage. The `index` and `set_index` functions handle coordinate transformation:

```cpp
A[i][j][k]  // User writes natural indices
// System transforms: sort within symmetry groups, left-justify
// Access: triangular storage at transformed coordinates
```

The currying abstraction (type-level rank reduction) is orthogonal to the symmetry abstraction (coordinate transformation). This separation of concerns keeps both systems simple.

### 7.7 Sparse Tensor Compatibility

Blade is not designed for sparse tensor computation, but provides primitives that enable user-defined sparsity patterns. See §2.6 for the `<|:>` array fallback combinator.

**Partial-Depth Allocation**: Arrays can be allocated to a specified depth, with deeper levels defaulting to nullptr:

```cpp
// C++ allocator API
auto A = allocate_to_depth<3>(shape, depth=2);
// A[i] is allocated for all i
// A[i][j] is nullptr by default
```

Users manage which slices to allocate based on their sparsity pattern:

```cpp
for (auto [i,j] : my_sparse_indices) {
    A[i][j] = allocate_leaf(shape[2]);
}
```

**Limitations**: Blade does not provide sparse storage formats (CSR, COO, etc.), automatic sparsity detection, or sparse-specific iteration. The `<|:>` combinator handles missing data gracefully but does not optimize iteration patterns for sparsity.

---

## 8. Combinator Algebra

### 8.1 Core Combinators

#### Application (<@>)

Completes partial evaluation:

```
(<@>) : MethodLoop × Function → Computation
(<@>) : ObjectLoop × A* → Computation
```

**Typing**:
```
M : MethodLoop[A₁...Aₙ]    f : (T₁^r₁...Tₙ^rₙ) → T^r
compatible(M, f)
──────────────────────────────────────────────────────
            M <@> f : Computation T^r'(σ')
```

where r' and σ' are computed by OutputSymmetry (§5.3).

#### Monadic Bind (>>=)

Sequential composition with dependency:

```
(>>=) : Computation α × (α → Computation β) → Computation β
```

**Laws**:
```
pure a >>= f  ≡  f a                           (left identity)
m >>= pure    ≡  m                             (right identity)
(m >>= f) >>= g  ≡  m >>= (λx. f x >>= g)     (associativity)
```

#### Pure

Lifts a value into a computation:

```
pure : α → Computation α
```

#### Functor Map (<$>)

Transforms the result without changing loop structure:

```
(<$>) : (α → β) × Computation α → Computation β
f <$> c  ≡  c >>= (pure ∘ f)
```

### 8.2 Parallel Combinators

#### Parallel Composition (<&>)

Execute both computations, potentially fusing isomorphic loop prefixes:

```
(<&>) : Computation α × Computation β → Computation (α × β)
```

**Semantics**: Given computations C₁ and C₂, determine the *fusion depth* d—the number of outermost loops with identical loop level types. Generate fused iteration for the common prefix, then separate continuations.

#### Mandatory Fusion (<&!>)

Mandatory fusion for computations sharing the same MethodLoop:

```
(<&!>) : Computation α × Computation β → Computation γ
        where both computations derive from the same MethodLoop
```

**Restriction**: Only valid for MethodLoop-derived computations. For ObjectLoop, the S-dimension is fixed at application time with no shared reference, so `<&!>` cannot verify structural identity.

**Semantics**: Given `(M <@> f) <&!> (M <@> g)`, generate a single loop nest applying both f and g at each iteration point.

#### Array Product (<*>)

Combines array tuples into a single iteration space (MethodLoop only):

```
(<*>) : MethodLoop × MethodLoop → MethodLoop
```

**Semantics**: `M₁ <*> M₂` concatenates the array lists of both loops, creating a single loop that iterates over the combined index space. Crucially, this is *not* a Cartesian product of independent iteration spaces—it builds a single iteration space whose structure depends on the kernel's commutativity annotations.

```
method_for(A) <*> method_for(B)    == method_for(A, B)
method_for(A) <*> method_for(A)    == method_for(A, A)
method_for(A, B) <*> method_for(C) == method_for(A, B, C)
```

**Commutativity is determined by the kernel, not by `<*>`**: The `<*>` combinator itself does not know about commutativity—it merely concatenates array lists. When a kernel is later applied via `<@>`, the kernel's `comm(...)` clause determines which array positions are interchangeable. If positions with identical arrays fall within the same commutativity group, triangular iteration is enabled; otherwise, iteration is rectangular.

This design means `<*>` is a pure structural combinator: it builds the array tuple, and the kernel annotates the symmetry. The same MethodLoop can be applied to different kernels with different commutativity, yielding different iteration patterns:

```
let M = method_for(A) <*> method_for(A)

M <@> f where comm(x, y)   // triangular iteration (x,y commutative, same array)
M <@> g                     // rectangular iteration (no commutativity declared)
```

**Triangular vs Rectangular** (summary):

- If the kernel declares `comm(...)` covering positions with identical arrays → triangular
- Otherwise → rectangular

**Identity**: `method_for()` (the empty loop) is the identity element.

```
method_for() <*> M  ≡  M  ≡  M <*> method_for()
```

#### Fold over Arrays

The `<*>` combinator enables dynamic construction of loops via fold:

```
fold(<*>, map(method_for, [A, A, A, B, B]))
  == method_for(A) <*> method_for(A) <*> method_for(A) <*> method_for(B) <*> method_for(B)
  == method_for(A, A, A, B, B)
```

This is essential for arity-polymorphic computations where the array list is determined at runtime:

```
let k = runtime_value()
let arrays = replicate(k, A) ++ [B, B]
let loop = fold(<*>, map(method_for, arrays))
loop <@> arity_any_kernel |> compute
```

**Duality with ObjectLoop**: For `object_for`, the fold happens implicitly at application time. The following are equivalent:

```
// method_for path: explicit fold, then apply kernel
fold(<*>, map(method_for, arrays)) <@> f

// object_for path: bind kernel, apply array list (fold implicit)
object_for(f) <@> arrays
```

Both paths produce the same computation. The fold operation acts on array tuples in both cases—either explicitly via `<*>` on MethodLoops, or implicitly when `object_for` accepts an array list.

### 8.3 Collection Combinators

#### Sequence

```
sequence : [Computation α] → Computation [α]
```

Collects a list of computations into a computation producing a list.

#### Replicate

```
replicate : ℕ × Computation α → Computation [α]
```

Executes the same computation n times. Useful for resampling, bootstrap methods, and Monte Carlo simulations.

### 8.4 Evaluation

#### Compute

```
(|> compute) : Computation α → α
```

Triggers evaluation of the (lazy) computation graph.

### 8.5 Combinator Laws

**Parallel composition is commutative** (up to tuple reordering):
```
C₁ <&> C₂  ≡  swap <$> (C₂ <&> C₁)
```

**Parallel composition is associative** (up to tuple reassociation):
```
(C₁ <&> C₂) <&> C₃  ≡  assoc <$> (C₁ <&> (C₂ <&> C₃))
```

**Fusion distributes over parallel** (when applicable):
```
(M <@> f) <&!> (M <@> g)  ≡  (M <@> f) <&> (M <@> g)  // but with guaranteed fusion
```

**Application is not commutative**: 
```
M <@> f  ≠¢  f <@> M  // second form is not syntactically valid
```

### 8.6 Composition Combinators and the Duality Theorem

#### Kernel Composition (>>@)

Composes ObjectLoops before array binding:

```
(>>@) : ObjectLoop × ObjectLoop → ObjectLoop
```

**Semantics**: `object_for(f) >>@ object_for(g)` creates a new ObjectLoop that, when applied to arrays, runs `f` then pipes the result to `g`.

```
let pipeline = object_for(normalize) >>@ object_for(variance)
let result = pipeline <@> (data, data) |> compute
```

**Type constraint**: Output type of first kernel must match input type of second.

#### Sequential Composition (@>>)

Composes computations within a shared loop structure:

```
(@>>) : Computation α × Computation β → Computation β
        where both derive from the same MethodLoop
```

**Semantics**: `(M <@> f) @>> (M <@> g)` executes `f` then `g` at each iteration point, with `f`'s output feeding `g`'s input.

```
let loop = method_for(data, data)
let result = (loop <@> demean) @>> (loop <@> variance) |> compute
```

**Restriction**: Requires same MethodLoop (verified structurally).

#### The Duality Theorem

The `>>@` and `@>>` combinators satisfy a fundamental duality:

**Theorem (Compose-Apply Duality)**:
```
(object_for(f) >>@ object_for(g)) <@> A  ≡  (mloop <@> f) @>> (mloop <@> g)
    where mloop = method_for(A)
```

That is: *compose-then-apply* equals *apply-then-compose*.

**Corollary (Associativity)**:

Both combinators are associative:

```
(o₁ >>@ o₂) >>@ o₃  ≡  o₁ >>@ (o₂ >>@ o₃)
(c₁ @>> c₂) @>> c₃  ≡  c₁ @>> (c₂ @>> c₃)
```

**Identity elements**:

```
object_for(id) >>@ o  ≡  o  ≡  o >>@ object_for(id)
(M <@> id) @>> c        ≡  c  ≡  c @>> (M <@> id)
```

### 8.7 Additional Combinator Identities

#### Naturality of Map

The functor map `<$>` satisfies naturality:

```
f <$> (g <$> c)  ≡  (f ∘ g) <$> c                    (functor composition)
id <$> c         ≡  c                                (functor identity)
```

#### Applicative Structure

Computations form an applicative functor with `pure` and `<*>`:

```
pure f <*> pure x  ≡  pure (f x)                     (homomorphism)
u <*> pure x       ≡  pure (λf. f x) <*> u           (interchange)
pure id <*> c      ≡  c                              (identity)
```

#### Array Product Laws

The array product combinator `<*>` satisfies:

```
M₁ <*> M₂           ≡  M₂ <*> M₁                     (commutativity, up to index reordering)
(M₁ <*> M₂) <*> M₃  ≡  M₁ <*> (M₂ <*> M₃)           (associativity)
method_for() <*> M  ≡  M                             (identity: empty loop)
```

**Concatenation property**:
```
method_for(A₁, ..., Aₙ) <*> method_for(B₁, ..., Bₘ)  ≡  method_for(A₁, ..., Aₙ, B₁, ..., Bₘ)
```

**Fold equivalence**:
```
fold(<*>, [method_for(A₁), ..., method_for(Aₙ)])  ≡  method_for(A₁, ..., Aₙ)
```

#### Symmetry Preservation

Combinators preserve symmetry structure predictably:

```
σ(M <@> f)           = OutputSymmetry(M.arrays, f)
σ(C₁ <&> C₂)         = σ(C₁) × σ(C₂)               (product of symmetries)
σ(C >>= k)           = σ(k(⊥))                      (determined by continuation)
σ((M <@> f) <&!> (M <@> g))  = σ(M <@> f) × σ(M <@> g)   (fusion preserves)
```

### 8.8 Zero Elements and Control Flow

Just as `for` loops are reified into loop objects with algebraic structure, conditional control flow (`if`/`match`) can be reified into choice combinators. This enables compositional reasoning about branching computations.

#### Zero Array Tuple

The empty tuple `()` represents zero S-dimensions:

```
method_for() <@> f      ≡  pure (f())     // no arrays → scalar from f's zero-arity case
object_for(f) <@> ()    ≡  pure (f())     // dual construction, same result
```

This enables recursive definitions of arity-polymorphic functions:

```
method_for() <@> moment           ≡  pure 1              // base case: identity
method_for(A) <@> moment          ≡  A                   // arity 1
method_for(A, A) <@> moment       ≡  covariance          // arity 2
method_for(A, A, A) <@> moment    ≡  coskewness          // arity 3
```

#### Zero Function

The `zero` kernel produces zero values while preserving S-dimensional structure:

```
zero : T^r → T^0      // consumes input, produces no T-dimensions
```

**Concrete syntax:**
```
zero                  // the zero kernel
M <@> zero           // produces array of zeros with M's S-dimensions
```


When applied via a loop:

```
M <@> zero            // produces array of zeros with shape from M's S-dimensions
object_for(zero) <@> (A, A)   // symmetric matrix of zeros
```

#### Zero Function Laws

```
(M <@> zero) >>= k                  ≡  M <@> zero       (left zero for >>=)
c >>= (λ_. M <@> zero)              ≡  M <@> zero       (right zero for >>=)
object_for(f) >>@ object_for(zero)  ≡  object_for(zero) (absorbs composition)
object_for(zero) >>@ object_for(f)  ≡  object_for(zero) (absorbs composition)
shape(M <@> zero)                   =  S-dims(M)        (no T-dimensions)
σ(M <@> zero)                       =  σ(M)             (symmetry from arrays only)
method_for() <@> zero               ≡  pure 0           (scalar zero)
```

#### Choice Combinator (<|>)

The choice combinator selects between computations:

```
(<|>) : Computation α × Computation α → Computation α
```

**Semantics**: `c₁ <|> c₂` produces the result of `c₁` if non-zero, otherwise falls back to `c₂`.

#### Choice Laws

```
(M <@> zero) <|> c  ≡  c                            (left identity)
c <|> (M <@> zero)  ≡  c                            (right identity)
(c₁ <|> c₂) <|> c₃  ≡  c₁ <|> (c₂ <|> c₃)          (associativity)
c <|> c             ≡  c                            (idempotence)
(c₁ <|> c₂) >>= k   ≡  (c₁ >>= k) <|> (c₂ >>= k)   (left distribution)
(c₁ <|> c₂) <&> c₃  ≡  (c₁ <&> c₃) <|> (c₂ <&> c₃) (right distribution)
c₃ <&> (c₁ <|> c₂)  ≡  (c₃ <&> c₁) <|> (c₃ <&> c₂) (left distribution)
```

#### Guard

The `guard` combinator conditionally executes a computation:

```
guard : (Condition, Computation α) → Computation α
```

**Semantics**: `guard(p, c)` produces `c` if `p` is true, otherwise produces zeros with `c`'s shape.

#### Guard Laws

```
guard(true, c)              ≡  c
guard(false, c)             ≡  shape_of(c) <@> zero
guard(p, c₁ <|> c₂)         ≡  guard(p, c₁) <|> guard(p, c₂)
guard(p, c₁) <|> guard(!p, c₂)  ≡  c₁ <|> c₂          (exhaustive guards)
guard(p, guard(q, c))       ≡  guard(p && q, c)
```

#### MonadPlus Structure

With `zero` and `<|>`, computations form a **MonadPlus**:

| MonadPlus operation | Blade equivalent |
|---------------------|------------------|
| `mzero` | `M <@> zero` |
| `mplus` | `<|>` |

The required laws are satisfied:

```
mzero >>= k        ≡  mzero                         (left zero)
mzero `mplus` m    ≡  m                             (left identity)
m `mplus` mzero    ≡  m                             (right identity)
(a `mplus` b) >>= k  ≡  (a >>= k) `mplus` (b >>= k) (left distribution)
```

#### Zero Element Summary

| Concept | Syntax | Role | Preserves |
|---------|--------|------|-----------|
| Zero array tuple | `()` / `method_for()` | Identity for `<*>`, arity recursion base | T-dimensions from kernel |
| Zero function | `zero` | Annihilator for `>>=`, identity for `<|>` | S-dimensions from arrays |


---

## 9. Symmetry System

### 9.1 Symmetry/Commutativity States

For each (array, dimension) pair in a loop, we track a state:

```
data SymcomState = Neither | Symmetric | Commutative | Both
```

- **Neither**: No exploitable symmetry
- **Symmetric**: Array has internal symmetry at this dimension
- **Commutative**: Function is commutative in this argument (and same array appears in multiple positions)
- **Both**: Array symmetry and function commutativity both present

### 9.2 State Computation

Given arrays A₁...Aₙ with symmetry vectors σ₁...σₙ and function commutativity c:

```
state(i, j) = 
    let sym = (j > 0) ∧ (σáµ¢[j] = σáµ¢[j-1])
    let com = (i > 0) ∧ (cáµ¢ = cáµ¢₋₁) ∧ (Aáµ¢ = Aáµ¢₋₁)
    match (sym, com) with
    | (false, false) → Neither
    | (true,  false) → Symmetric
    | (false, true)  → Commutative
    | (true,  true)  → Both
```

**Key insight**: Commutativity only yields triangular iteration when the *same array* appears in multiple commutative positions. Different arrays in a commutativity group don't enable triangular iteration—the validity check will fail and SymcomState won't signal commutativity.

### 9.3 Output Symmetry Inference via Lowering

Output symmetry inference is the computational realization of the lowering homomorphism `lower₂₁` (Theorems 5.12-5.15).

**Theoretical basis**: Function commutativity (Level 2 symmetry) lowers to array index symmetry (Level 1) when applied to identical arrays. The homomorphism `lower₂₁(H) = H ∩ Stab(A₁,...,Aₙ)` precisely characterizes which symmetries transfer.

**Algorithm**:

```
OutputSymmetry(A₁...Aₙ, f) =
    // Group arrays by commutativity (determines Stab)
    let groups = groupBy(c, [A₁...Aₙ])
    
    // For each group, extract S-dimension symmetry
    let sSymms = for group in groups:
        let levels = rank(group[0]) - irank(f, group[0])
        replicate(|group|, group[0].σ[0..levels-1])
    
    // Reindex to create global symmetry vector
    let sSymm = reindex(concat(sSymms))
    
    // Append T-dimension symmetry
    let tSymm = offset(f.TDimSymm, max(sSymm))
    
    return concat(sSymm, tSymm)
```

**Reindexing**: Ensures symmetry values from different groups don't accidentally collide:

```
reindex([⟨1,1⟩, ⟨1,2⟩]) = ⟨1, 1, 3, 4⟩
```

### 9.4 The Symmetry Transformation (Lowering in Action)

The flow of symmetry through computation instantiates the lowering homomorphisms:

```
Input Array Symmetry   →  [read elements]  →  lower₁₀ (trivial) → consumed
Function Commutativity →  [apply to arrays] → lower₂₁ (iso if identical) → Output Symmetry
```

- **Input symmetry quashing** (Theorem 5.17): Input array symmetry is consumed when elements are read, because `lower₁₀` is trivial—all index permutations become element identity.

- **Output symmetry generation** (Theorem 5.12): Function commutativity transfers to output array symmetry via `lower₂₁`, which is an isomorphism when arrays are identical.

Both phenomena are instances of the same lowering structure—they differ only in which homomorphism applies.

---

## 10. Triangular Iteration

### 10.1 Cumulative Bound Computation

For symmetric/commutative dimensions, iteration bounds subtract all prior indices in the same symmetry group:

```
bound(i, j) = 
    let group = symmetryGroup(i, j)
    let priorIndices = [(i', j') | (i', j') ∈ group, (i', j') < (i, j)]
    extent[j] - Σ_{(i',j') ∈ priorIndices} index[i'][j']
```

**Example**: For a 3D symmetric tensor (σ = ⟨1, 1, 1⟩):

```
for i₀ in [0, n):
    for i₁ in [0, n - i₀):
        for i₂ in [0, n - i₀ - i₁):
            // kernel
```

### 10.2 Left-Justified Indexing

Triangular allocation stores only unique elements. To enable direct indexing, each loop starts at 0 (left-justified) rather than at the previous index value:

**Not this** (standard triangular):
```
for i in [0, n):
    for j in [i, n):     // j starts at i
        for k in [j, n): // k starts at j
```

**But this** (left-justified):
```
for i in [0, n):
    for j in [0, n-i):       // j starts at 0, bound reduced
        for k in [0, n-i-j): // k starts at 0, bound cumulatively reduced
```

This allows `array[i][j][k]` to directly index into compactly allocated triangular storage.

#### 10.2.1 Two-Phase Algorithm

The transformation from arbitrary indices to storage coordinates is a two-phase algorithm:

**Phase 1 — Fold (Canonicalize):** Sort indices within each symmetry group to canonical form.

```
foldIndices(indices, σ) =
    for each group G in symmetryGroups(σ):
        sort indices[G] in ascending order
    return indices
```

**Phase 2 — Left-Justify:** Convert to storage coordinates by subtracting cumulative offsets.

```
leftJustify(folded, σ) =
    justified[0] = folded[0]
    for i in 1..n-1:
        if σ[i] = σ[i-1]:       // same symmetry group
            justified[i] = folded[i] - folded[i-1]
        else:
            justified[i] = folded[i]
    return justified
```

**Combined transformation:**

```
transformIndices = leftJustify ∘ foldIndices
```

**Example**: For σ = ⟨1, 1, 1⟩ and indices (5, 2, 7):

1. **Fold**: sort → (2, 5, 7)
2. **Left-justify**: (2, 5-2, 7-5) = (2, 3, 2)
3. **Access**: array[2][3][2]

### 10.3 Index Mapping for Access

To access a symmetrically-allocated array with arbitrary indices, apply the two-phase transformation:

```
index(array, [i, j, k]) where σ = ⟨1, 1, 1⟩:
    let [i', j', k'] = sort([i, j, k])           // Phase 1: canonicalize
    let i'' = i'
    let j'' = j' - i'                             // Phase 2: left-justify
    let k'' = k' - j'                             // cumulative subtraction
    return array[i''][j''][k'']
```

For mixed symmetry vectors (e.g., σ = ⟨1, 1, 2, 2⟩), each group is processed independently:

```
index(array, [a, b, c, d]) where σ = ⟨1, 1, 2, 2⟩:
    // Group 1: positions 0, 1 (σ = 1)
    let [a', b'] = sort([a, b])
    let a'' = a'
    let b'' = b' - a'
    
    // Group 2: positions 2, 3 (σ = 2)
    let [c', d'] = sort([c, d])
    let c'' = c'
    let d'' = d' - c'
    
    return array[a''][b''][c''][d'']
```

### 10.4 Complexity Analysis

For a fully symmetric n-dimensional tensor of extent N:

**Dense iteration**: O(Nⁿ)

**Triangular iteration**: O(Nⁿ/n!)

The n! factor comes from the volume ratio between an n-cube and an n-simplex.

**Derivation**: The number of unique elements in a symmetric tensor is the multiset coefficient:
```
((N choose n)) = (N + n - 1)! / (n! × (N - 1)!)  ≠ˆ  Nⁿ/n!  for large N
```

### 10.5 Product Symmetry Theorem

When computing over multi-dimensional arrays (e.g., lat × lon × time), the symmetry structure is richer than for 1D arrays. 

**Product Symmetry S_r^d**: For a computation with r inputs over d-dimensional arrays, product symmetry means each of the d dimensions has independent S_r symmetry. The symmetry group is the product:

```
S_r^d = S_r × S_r × ... × S_r   (d factors)
```

**Theorem (Product Symmetry)**: For a computation with product symmetry S_r^d over arrays of extent n in each dimension:

```
Speedup = (r!)^d
```

This is exponentially better than the r! speedup from flattening to 1D.

| Configuration | Speedup |
|---------------|---------|
| r=3, d=1 (coskewness, 1D) | 6× |
| r=3, d=2 (coskewness, 2D) | 36× |
| r=4, d=2 (cokurtosis, 2D) | 576× |
| r=4, d=4 (cokurtosis, 4D) | 331,776× |

---

## 11. Type System

### 11.1 Judgments

```
Γ ⊢ e : τ        Expression e has type τ in context Γ
Γ ⊢ L : Loop[S]  L is a loop with structure S
Γ ⊢ C : Comp[τ]  C is a computation producing type τ
```

### 11.2 Array Rules

```
Γ ⊢ T : BaseType    r ∈ ℕ    σ ∈ ℕÊ³    ε ∈ ℕÊ³
──────────────────────────────────────────────── (Array-Intro)
        Γ ⊢ array(T, r, σ, ε) : T^r(σ)
```

### 11.3 Function Rules

```
Γ, x₁:T₁^r₁, ..., xₙ:Tₙ^rₙ ⊢ body : T^r
metadata = (c, p, tdim)
well_formed(metadata)
──────────────────────────────────────────────── (Fun-Intro)
Γ ⊢ (fn(x₁...xₙ) -> out:T^r where metadata {body}) : Function
```

### 11.4 Loop Object Rules

```
Γ ⊢ A₁ : T₁^r₁(σ₁)  ...  Γ ⊢ Aₙ : Tₙ^rₙ(σₙ)
S = computeStructure(A₁...Aₙ)
───────────────────────────────────────────── (MethodLoop-Intro)
     Γ ⊢ method_for(A₁...Aₙ) : MethodLoop[S]


Γ ⊢ f : Function
───────────────────────────────────────────── (ObjectLoop-Intro)
     Γ ⊢ object_for(f) : ObjectLoop[f]
```

### 11.5 Application Rules

```
Γ ⊢ M : MethodLoop[S]    Γ ⊢ f : (T₁^r₁...Tₙ^rₙ) → T^r
compatible(S, f)
σ' = OutputSymmetry(M.arrays, f)
r' = S.dims + f.ORank
──────────────────────────────────────────────────────── (App-Method)
              Γ ⊢ M <@> f : Comp[T^r'(σ')]


Γ ⊢ O : ObjectLoop[f]    Γ ⊢ A₁ : T₁^r₁(σ₁) ... Γ ⊢ Aₙ : Tₙ^rₙ(σₙ)
compatible(f, A₁...Aₙ)
S = computeStructure(A₁...Aₙ)
σ' = OutputSymmetry(A₁...Aₙ, f)
r' = S.dims + f.ORank
──────────────────────────────────────────────────────── (App-Object)
           Γ ⊢ O <@> (A₁...Aₙ) : Comp[T^r'(σ')]
```

### 11.6 Combinator Rules

```
Γ ⊢ C₁ : Comp[α]    Γ ⊢ C₂ : Comp[β]
────────────────────────────────────── (Parallel)
    Γ ⊢ C₁ <&> C₂ : Comp[α × β]


Γ ⊢ M : MethodLoop[S]
Γ ⊢ M <@> f : Comp[α]
Γ ⊢ M <@> g : Comp[β]
─────────────────────────────────────── (Fusion)
  Γ ⊢ (M <@> f) <&!> (M <@> g) : Comp[α × β]


Γ ⊢ C : Comp[α]    Γ ⊢ k : α → Comp[β]
──────────────────────────────────────── (Bind)
        Γ ⊢ C >>= k : Comp[β]


Γ ⊢ v : α
─────────────────── (Pure)
Γ ⊢ pure v : Comp[α]
```

---

## 12. Operational Semantics

### 12.1 Evaluation Model

Computations are *lazy*—they build a computation graph until `|> compute` is applied.

```
data CompGraph =
    | MethodLeaf of LoopSpec × Function      // method_for path
    | ObjectLeaf of LoopSpec × Function      // object_for path
    | Parallel of CompGraph × CompGraph × FusionDepth
    | MethodFused of LoopSpec × [Function]   // fused method_for computations
    | ObjectFused of LoopSpec × [Function]   // fused object_for computations
    | Bind of CompGraph × (Value → CompGraph)
    | Pure of Value
    | Choice of CompGraph × CompGraph        // MonadPlus alternative
    | Guard of Predicate × CompGraph         // conditional computation
```

The two leaf types correspond to the two loop construction paths:
- **MethodLeaf**: From `method_for(arrays) <@> kernel`
- **ObjectLeaf**: From `object_for(kernel) <@> arrays`

Both paths produce the same loop structure after lowering; they differ only in binding order.

### 12.2 Loop Level Types

Each level of a loop nest has a type:

```
LoopLevelType = {
    extent: ℕ,
    symcomState: SymcomState,
    parallelism: ParallelKind    // None | OpenMP | OpenACC | ...
}
```

Two loop levels are **fusable** iff ALL components match:

```
levelsCompatible(l₁, l₂) ≡ 
    l₁.extent = l₂.extent ∧
    l₁.symcomState = l₂.symcomState ∧
    l₁.parallelism = l₂.parallelism
```

This means:

- An OpenMP-parallel loop and a non-parallel loop are different types
- A loop with `Symmetric` state and one with `Commutative` state are different types
- Loops over different extents are different types

### 12.3 Fusion Analysis

Fusion depth is the length of the longest common prefix of loop level types:

```
fusionDepth : CompGraph × CompGraph → ℕ

fusionDepth(MethodLeaf(L₁, f₁), MethodLeaf(L₂, f₂)) =
    longestCommonPrefix(L₁.levelTypes, L₂.levelTypes)

fusionDepth(ObjectLeaf(L₁, f₁), ObjectLeaf(L₂, f₂)) =
    longestCommonPrefix(L₁.levelTypes, L₂.levelTypes)

longestCommonPrefix([], _) = 0
longestCommonPrefix(_, []) = 0
longestCommonPrefix(t₁::rest₁, t₂::rest₂) =
    if levelsCompatible(t₁, t₂)
    then 1 + longestCommonPrefix(rest₁, rest₂)
    else 0
```

This ensures loops only fuse when they have identical structure at each level, including parallelism annotations and symmetry/commutativity states.

### 12.4 Compute Semantics

The `|> compute` combinator triggers evaluation of the computation graph:

```
compute : CompGraph → Value

compute(Pure(v)) = v
compute(MethodLeaf(L, f)) = evaluate loop L applying f at each point
compute(ObjectLeaf(L, f)) = evaluate loop L applying f at each point
compute(Parallel(g₁, g₂, d)) = (compute(g₁), compute(g₂)) with fusion to depth d
compute(MethodFused(L, fs)) = evaluate loop L applying all fs at each point
compute(ObjectFused(L, fs)) = evaluate loop L applying all fs at each point
compute(Bind(g, k)) = compute(k(compute(g)))
compute(Choice(g₁, g₂)) = compute(g₁) <|> compute(g₂)   // MonadPlus
compute(Guard(p, g)) = if p then compute(g) else zero
```


---

## 13. Concrete Syntax

### 13.1 Array Declaration

```
array <name>: <type>^<rank> {
    extents: [e₁, ..., eáµ£],
    symm: [s₁, ..., sáµ£]       // optional; default all-distinct
}
```

**Shorthand**:
```
array <name>: <type>^<rank>[e₁, ..., eáµ£]  // dense array
```

### 13.2 Function Declaration

```
function <n>(
    <arg1>: <T1>^<r1>,
    ...
    <argN>: <TN>^<rN>
) -> <out>: <T>^<r>
where
    comm(<arg_i>, <arg_j>, ...),
    omp(<arg_k>: <depth>, ...),
    tdim(
        { extent: <expr>, symm: <k>, name: "<dim>" },
        ...
    )
{
    <body>
}
```

**Parallelism semantics**: `omp(a: 2, b: 1)` means "parallelize 2 levels of S-dimension loops from array a, and 1 level from array b." Since array arguments are bound in order, their S-dimension loops nest in that order—a's loops are outermost, making them natural parallelization targets. The `omp` clause can be substituted with other backends (e.g., `acc` for OpenACC).

**T-dimension semantics**: Each T-dimension is specified as a record with `extent` (size expression), `symm` (symmetry class), and `name` (optional label). Dimensions with the same `symm` value are interchangeable.

### 13.3 Loop Construction and Application

```
let <loop> = method_for(<A₁>, ..., <Aₙ>)
let <loop> = object_for(<f>)

let <comp> = <loop> <@> <f>
let <comp> = <loop> <@> (<A₁>, ..., <Aₙ>)
```

### 13.4 Combinators

```
<comp₁> <&> <comp₂>       // parallel composition
<comp₁> <&!> <comp₂>      // mandatory fusion (same MethodLoop required)
<loop₁> <*> <loop₂>       // array product (method_for only)
<comp> >>= <k>            // bind
pure <v>                  // lift
<f> <$> <comp>            // functor map
guard(<cond>, <comp>)     // conditional computation
sequence [<c₁>, ...]      // collect computations
replicate <n> <comp>      // repeat n times

<comp₁> @>> <comp₂>       // sequential composition (same MethodLoop required)
<obj₁> >>@ <obj₂>         // kernel composition (ObjectLoop only)

<comp> |> compute         // execute
```

**Guard semantics**: `guard(p, c)` executes computation `c` only when predicate `p` is true. When `p` is false, returns the zero element for `c`'s output type.

**Functor map semantics**: `f <$> c` is equivalent to `c >>= (λx. pure (f x))`. Applies pure function `f` to computation result.

**Sequential composition constraint**: Both computations in `c₁ @>> c₂` must derive from the same MethodLoop. This is a compile-time structural check.

**Kernel composition constraint**: `>>@` is only valid between ObjectLoops. Creates a composed ObjectLoop that pipes the first kernel's output to the second kernel's input.

---


### 13.5 Tuple Syntax

**Expression-level tuple parsing:**

```
()           → Tuple []        // empty tuple (unit)
(e)          → e               // parenthesized expression (grouping only)
(e₁, e₂)     → Tuple [e₁; e₂]  // 2-tuple
(e₁, e₂, e₃) → Tuple [e₁; e₂; e₃]  // 3-tuple, etc.
```

**Combinator-level tuple parsing:**

```
()           → Unit            // zero array tuple
(c)          → c               // parenthesized combinator (grouping only)
```

**Design rationale:** Single-element parentheses are grouping, not 1-tuples. This matches Python semantics and common expectations. A trailing comma syntax `(e,)` could be added if 1-tuples are ever needed.

**Usage in object_for path:**

```
object_for(f) <@> ()       // zero arrays - Unit case
object_for(f) <@> A        // one array - Ref case  
object_for(f) <@> (A, B)   // two arrays - Tuple case
object_for(f) <@> (A, B, C) // three arrays - Tuple case
```

**Nested tuples:**

```
object_for(f) <@> ((A, B), C)   // nested - f sees 2 top-level args
method_for(A, B, C)             // always flat - method_for doesn't nest
```

### 13.6 Poly-Indexing Syntax

**Standard indexing (sequential application):**

```
A[i]           -- curry first dimension
A[i][j]        -- curry first two dimensions
A[i][j][k]     -- full index (if rank 3)
```

**Poly-indexing (tuple application):**

```
A[indices]     -- indices : Tuple of indices, length = rank(A)
```

**Index tuple construction:**

```
let indices = (i, j, k)              -- explicit tuple
let indices = replicate(i, rank(A)) -- replicated index (e.g., diagonal)
let indices = all_indices(A)        -- iterator over all valid tuples
```

**Rank-polymorphic iteration:**

```
for indices in all_indices(A) {
    ... A[indices] ...
}
```

The `all_indices(A)` iterator generates all valid index tuples for array A, respecting its structure (dense, ragged, symmetric, etc.).

**Lambda indices:**

```
A[Dual(i, 1.0)]     -- AD dual number index
A[Symbolic("i")]    -- symbolic/deferred index
A[offset(k)]        -- computed stencil offset
```

All resolve to the structural index type; computational indices wrap the address with additional information.

---

## 14. Open Design Questions

### 14.1 Error Handling

No formal treatment of error cases (incompatible shapes, invalid symmetry vectors, etc.).

### 14.2 Additional Considerations

1. **Broadcasting**: Behavior when arrays have different extents in non-symmetric dimensions is unspecified.

2. **Memory management**: Allocation and deallocation of intermediate results is implicit.

---

## 15. Future Work

This section describes extensions that are planned but not yet part of the core specification. These features involve significant additional complexity and are considered speculative.

### 15.1 Automatic Differentiation

Blade computations are differentiable when their kernels are differentiable. Both forward mode (tangent propagation) and reverse mode (gradient accumulation) preserve the r! speedup from triangular iteration. This section formalizes AD through the S/T paradigm.

#### 15.1.1 Differentiable Computation Types

We extend the type system with differentiated computation types:

```
DComp[τ, δτ]    -- Computation with tangent type δτ (forward mode)
GComp[τ, ∇τ]    -- Computation with gradient type ∇τ (reverse mode)
```

**Definition**: A computation `c : Comp[τ]` is differentiable if its kernel is a differentiable function.

#### 15.1.2 Forward Mode (Tangent Propagation)

Forward mode propagates tangents (directional derivatives) through the computation:

```
forward : Comp[τ] → δInput → DComp[τ, δτ]
```

**Typing rule**:
```
Γ ⊢ c : Comp[τ]    Γ ⊢ δA : δInput
───────────────────────────────────
Γ ⊢ forward c δA : DComp[τ, δτ]
```

**Semantics**: For `c = loop <@> k`:
```
forward (loop <@> k) δA = loop <@> (forward_kernel k δA)
```

The loop structure is preserved—only the kernel is differentiated. At each iteration point:
```
output[i] = f(A[i], B[i])
d_output[i] = ∂f/∂A(A[i], B[i]) · dA[i] + ∂f/∂B(A[i], B[i]) · dB[i]
```

**Cost**: O(p · I(n,r)) for p input parameters, where I(n,r) = C(n+r-1, r).

#### 15.1.3 Reverse Mode (Gradient Accumulation)

Reverse mode computes gradients by backpropagating adjoints:

```
reverse : Comp[τ] → ∇Output → GComp[τ, ∇Input]
```

**Typing rule**:
```
Γ ⊢ c : Comp[τ]    Γ ⊢ ∇O : ∇Output
───────────────────────────────────
Γ ⊢ reverse c ∇O : GComp[τ, ∇Input]
```

**Cost**: O(I(n,r)) regardless of input dimension—the standard reverse-mode advantage.

**Tape storage**: For triangular iteration, the tape has C(n+r-1, r) entries instead of n^r. The same r! reduction applies to memory for intermediate storage.

#### 15.1.4 Symmetric Gradient Accumulation

For triangular iteration over symmetric computations, gradients accumulate symmetrically:

```cpp
// For method_for(A, A) with commutativity
for (auto (i, j) in loop.triangular_indices()) {
    float g_out = grad_out[tri_idx(i, j)];
    
    // Gradient flows to BOTH indices (same underlying array)
    grad_A[i] += ∂f/∂arg0(A[i], A[j]) * g_out;
    grad_A[j] += ∂f/∂arg1(A[i], A[j]) * g_out;
}
```

This is correct because each unique pair (i,j) with i ≤ j contributes to gradients at both positions.

#### 15.1.5 Jacobian Symmetry Theorem

**Theorem (Jacobian Symmetry)**: If a computation produces symmetric output, its Jacobian inherits corresponding symmetry structure.

Let `c = method_for(A, A, A) <@> k` with `comm(a, b, c)`, producing output O with symmetry σ = ⟨1, 1, 1⟩.

Then ∂O/∂A has symmetry in its first three indices (corresponding to output symmetry) and is dense in its last index (corresponding to input).

*Proof*: Let O[i,j,k] = k(A[i], A[j], A[k]) with comm(a,b,c). Then:

```
∂O[i,j,k]/∂A[m] = (∂k/∂a)·δ_im + (∂k/∂b)·δ_jm + (∂k/∂c)·δ_km
```

By commutativity, ∂k/∂a = ∂k/∂b = ∂k/∂c at symmetric points. The Jacobian inherits the output symmetry in its first r indices. □

**Corollary (Gradient Speedup)**: Computing gradients of symmetric computations benefits from the same r! (or (r!)^d for product symmetry) speedup as the forward computation.

#### 15.1.6 AD Through Combinators

AD interacts with combinators as follows:

**Application (`<@>`)**:
```
forward (loop <@> k) = loop <@> (forward k)
reverse (loop <@> k) = loop <@> (reverse k)
```

**Sequential composition (`@>>`)**:
```
forward (c₁ @>> c₂) = (forward c₁) @>> (forward c₂)
reverse (c₁ @>> c₂) = (reverse c₂) @>> (reverse c₁)    // Reversed order
```

**Parallel composition (`<&>`)**:
```
forward (c₁ <&> c₂) = (forward c₁) <&> (forward c₂)
reverse (c₁ <&> c₂) = (reverse c₁) <&> (reverse c₂)
```

**Pipeline composition (`>>@`)**:
```
forward (o₁ >>@ o₂) = (forward o₁) >>@ (forward o₂)
reverse (o₁ >>@ o₂) = (reverse o₂) >>@ (reverse o₁)    // Reversed order
```

**Array product (`<*>`)**:
```
forward (L₁ <*> L₂) = (forward L₁) <*> (forward L₂)
reverse (L₁ <*> L₂) = (reverse L₁) <*> (reverse L₂)
```

#### 15.1.7 Conditional Combinators in AD

**Choice (`<|>`)**: Requires recording which branch was taken.
```
forward (c₁ <|> c₂) = (forward c₁) <|> (forward c₂)
reverse (c₁ <|> c₂) = branch_taken ? reverse c₁ : reverse c₂
```

The branch selection must be recorded during the forward pass to route gradients correctly in the backward pass.

**Array fallback (`<|:>`)**: Gradient flows only to the array that was actually used.
```
forward (A <|:> B) = (forward A) <|:> (forward B)
reverse (A <|:> B) = was_A_used ? reverse A : reverse B
```

For sparse patterns, this means gradients are naturally sparse—they flow only to allocated slices.

#### 15.1.8 AD and Arity Polymorphism

For arity-polymorphic kernels using tuple destructuring, gradients flow to all arguments:

```
// Forward: product(args) where let (head, tail) = args; out = head * product(tail)
// Reverse:
reverse product(args) =
    let (head, tail) = args
    grad_head = product(tail) * grad_out
    grad_tail = reverse product(tail) with upstream = head * grad_out
```

The recursive structure naturally handles arbitrary arity.

#### 15.1.9 AD and Stencils

**Open question**: How do stencil operations interact with AD?

For `stencil(A, offsets) <@> k`, the backward pass must:

1. Compute local gradients at each stencil position
2. Scatter-add gradients back to source positions (inverse of gather)

This is well-understood for rectangular iteration but requires care with triangular bounds—gradients from symmetric stencil positions may need symmetric accumulation.

#### 15.1.10 AD and Domain Decomposition

**Open question**: How does AD interact with distributed triangular execution?

For blocked computation:

- **Forward pass**: Standard block-parallel execution
- **Backward pass**: Blocks must exchange "gradient halos" at boundaries

The gradient halo exchange pattern differs from the forward halo pattern because gradients flow in the reverse direction. For triangular blocks, boundary structure is more complex than rectangular.

#### 15.1.11 Implementation Approaches

**Dual numbers (forward mode)**:
```
Dual[τ] = (τ, τ)     // (primal, tangent)

lift : τ → Dual[τ]
lift x = (x, 0)

(a, da) + (b, db) = (a + b, da + db)
(a, da) * (b, db) = (a * b, a * db + da * b)
```

**Tape-based (reverse mode)**:
```
Tape = [(Operation, Inputs, Output)]

record : Comp[τ] → (τ, Tape)
replay : Tape → ∇Output → ∇Input
```

For triangular iteration, the tape has C(n+r-1, r) entries—the same r! reduction.

#### 15.1.12 Summary

| Aspect | Standard AD | Blade AD |
|--------|------------|----------|
| Iteration pattern | Dense (all elements) | Triangular (unique elements) |
| Gradient accumulation | Standard indexing | Symmetric accumulation |
| Forward speedup | 1× | r! (triangular forward) |
| Backward speedup | 1× | r! (triangular backward) |
| Tape storage | O(n^r) | O(n^r / r!) |
| Jacobian structure | Dense | Inherits output symmetry |

**Status**: Theoretical framework established. Implementation requires:

1. Code generation for forward/reverse kernels
2. Tape management for triangular iteration
3. Integration with stencils and domain decomposition
4. Framework bindings (PyTorch, JAX)

### 15.2 Stencils and Halo Exchange

The core stencil machinery (`shift`, `align`, `stencil` sugar) is defined in §2.6. The remaining work involves:

**Chunking interaction**: For cache-friendly 2D+ stencils, arrays should be chunked so that stencil neighborhoods fit in cache. The `AlignedExpr` type carries stencil metadata that can inform chunk sizing.

**Halo exchange**: For distributed computation, `AlignedExpr` metadata declares which neighboring elements are needed. The runtime can use this to:

- Determine halo regions at chunk boundaries  
- Generate communication patterns for halo exchange
- Overlap computation with communication

**Open questions**:

- Stencil interaction with triangular iteration (symmetric dimensions)
- Automatic halo width inference from nested stencils
- Grid topology for staggered grids (xgcm-style)
- Integration with chunked storage formats

### 15.3 Domain Decomposition for Distributed Computation

For petabyte-scale computation, the iteration space must be partitioned into blocks that can be distributed across nodes. This section specifies how product-symmetric tensors are decomposed while preserving their mathematical structure.

#### 15.3.1 Native Space vs Flattened Space

The iteration space for an n-ary symmetric operation over d-dimensional arrays is a **product of d simplices**:

Î”^(n-1)₀ × Î”^(n-1)₁ × ⋯ × Î”^(n-1)_{d-1}

where each simplex Î”^(n-1)_j is the region satisfying i₁ ≤ i₂ ≤ ⋯ ≤ iₙ for spatial dimension j.

**Flattening observation**: If array indices are linearized (e.g., for storage or single-loop iteration), different dimensions cycle at different rates. The valid region in flattened index space exhibits a fractal pattern of "holes"—triangular gaps nested at multiple scales, with nesting depth d. This is an artifact of flattening, not an intrinsic property of the iteration space.

**Native representation**: In the natural product-of-simplices space, there are no holes. Each factor is a complete simplex.

**Design decision**: Blade uses **native-space decomposition** because:

1. All blocks are structurally identical (no special cases)
2. No exclusion logic required (all block combinations valid)
3. Matches the mathematical structure of product symmetry

#### 15.3.2 Simplex Subdivision

For a single n-simplex with extent m, subdivision proceeds by halving all n axes simultaneously, creating 2^n cells. Each cell is labeled by an n-tuple (L,H)^n indicating low/high half membership.

**Valid cells**: A cell is valid iff its pattern has the form L^a H^b (a copies of L followed by b copies of H). Once an index is in the high half, all subsequent indices must also be high due to the ordering constraint.

**Count**: Exactly (n+1) valid cells out of 2^n.

| Arity | Valid cells | Invalid cells |
|-------|-------------|---------------|
| n=2 | 3 (LL, LH, HH) | 1 (HL) |
| n=3 | 4 (LLL, LLH, LHH, HHH) | 4 |
| n=4 | 5 | 11 |
| n | n+1 | 2^n ∑ (n+1) |

Each valid cell is itself an n-simplex with extent m/2, enabling recursive subdivision.

#### 15.3.3 Product Decomposition

For the full iteration space (product of d simplices), decomposition proceeds independently per factor:

1. Each of d simplices subdivides into (n+1) valid children
2. The Cartesian product yields (n+1)^d child blocks
3. Every child block is itself a product of d smaller simplices

**Key property**: All (n+1)^d combinations are valid. No exclusion logic is needed because the decomposition respects the native product-of-simplices structure.

**At depth k**:

| Metric | Formula |
|--------|---------|
| Total blocks | (n+1)^(kd) |
| Block extent per dimension | extent[j] / 2^k |
| Elements per block | ∏_j C(extent[j]/2^k + n ∑ 1, n) |

**Maximum depth**: Limited by ⌊log₂(min_j extent[j] / n)⌋ to ensure blocks remain meaningful.

#### 15.3.4 Block Addressing

A block is identified by d paths, one per spatial dimension:

```
BlockId = (path₀, path₁, ..., path_{d-1})
```

Each path is a sequence of length k (depth), with elements in {0, 1, ..., n} representing the valid L^(n-a)H^a patterns.

**Linear index**: Paths can be serialized to a linear index using mixed-radix encoding, either dimension-major or level-major (interleaved).

#### 15.3.5 Mixed Symmetry

Not all dimensions require the same symmetry. A symmetry vector **s** = (s₀, s₁, ..., s_{d-1}) specifies arity per dimension:

- s_j = 1: No symmetry (rectangular, 2 children per level)
- s_j > 1: s_j-way symmetry (s_j + 1 children per level)

**Branching factor**: ∏_{j=0}^{d-1} (2 if s_j=1, else s_j+1)

#### 15.3.6 Distributed Execution

The decomposition supports Bulk Synchronous Parallel (BSP) execution:

1. **Scatter**: Distribute blocks to workers (round-robin or locality-aware)
2. **Compute**: Workers process blocks independently (embarrassingly parallel)
3. **Reduce**: Aggregate via tree reduction

**Load balance**: Perfect by construction—all blocks have identical structure and element count.

### 15.4 Triangular File Format

A triangular-native storage format enables efficient I/O for symmetric tensors.

#### 15.4.1 Design Goals

1. **No redundancy**: Store only unique elements ((n!)^d savings)
2. **Block-aligned**: Each decomposition block maps to one storage chunk
3. **Parallel I/O**: Workers read/write independent chunks
4. **Self-describing**: Metadata encodes symmetry and decomposition

#### 15.4.2 Zarr Extension Schema

```json
{
  "blade_version": "3.0",
  "symmetry": {
    "arity": 3,
    "rank": 2,
    "extents": [1000, 1000],
    "symmetry_vector": [3, 3]
  },
  "decomposition": {
    "depth": 5,
    "branching_factor": 16,
    "block_count": 1048576,
    "addressing": "interleaved"
  },
  "dtype": "float64",
  "chunks": "triangular_product"
}
```

#### 15.4.3 Chunk Layout

Each chunk stores one block's data in triangular format:

- **Within a block**: standard triangular (row-major with varying row lengths)
- **Across blocks**: linear block index → chunk index

**Access pattern**: Reading block b requires exactly one chunk read. No gathering from scattered rectangular chunks.

#### 15.4.4 Streaming Construction

Triangular files can be constructed via streaming ETL:

1. Process rectangular source data in chunks
2. Compute block assignment for each element
3. Write directly to block files
4. No central aggregation required

This enables out-of-core construction for datasets larger than available memory.

### 15.5 Domain Decomposition Summary

| Property | Native Decomposition |
|----------|---------------------|
| Iteration space | Product of d n-simplices |
| Branching factor | (n+1)^d per level |
| Block structure | Uniform (all blocks identical) |
| Exclusion logic | None (all combinations valid) |
| Load balance | Perfect by construction |
| I/O alignment | One block = one chunk |

The native-space approach is preferred because it eliminates exclusion logic while preserving self-similarity and perfect load balance.

### 15.6 Remaining Open Questions

- Halo exchange at triangular block boundaries
- Communication patterns for tensor-vector multiplication
- Stencil interaction with triangular iteration

### 15.7 Tree Structures

Arrays and trees are points on a spectrum of indexed data structures. This section explores how Blade's abstractions might extend to tree-structured data.

#### 15.7.1 Trees as Generalized Arrays

An array is a tree where:
1. All paths have the same depth (fixed rank)
2. All nodes at the same depth have the same branching factor (extents)

**Trees relax both constraints:**
1. Variable depth—paths can terminate at different levels
2. Variable branching—each node can have different numbers of children

For arrays, the index type is a product: `Idx<n₁> × Idx<n₂> × ... × Idx<nᵣ>`

For trees, the index type is a *path*: a variable-length sequence of child selections:

```
Array index: (i, j, k)           -- fixed length 3
Tree path:   (p₀, p₁, ..., pₖ)   -- variable length
```

#### 15.7.2 Tree Shape as Index Type

Just as `Idx<n>` defines valid array positions, a **tree shape** defines valid paths:

```
Shape = Node(children: List<Shape>) | Leaf

example_shape = Node([
    Node([Leaf, Leaf]),           -- path (0,) has 2 children
    Node([Leaf, Leaf, Leaf]),     -- path (1,) has 3 children  
])

T : Tree<Float, example_shape>
-- valid paths: (0,0), (0,1), (1,0), (1,1), (1,2)
```

The shape IS the index type—it defines what paths are valid.

#### 15.7.3 Flat Storage with Bijection

Trees can be stored in flat contiguous memory with a precomputed bijection:

```
TreeIdx<shape> = {
    forward  : Path → Offset
    backward : Offset → Path
    subtree  : PartialPath → (Offset, SubShape)
}
```

**Depth-first layout:**
```
Tree:           Storage:
    root        [root, child0, child00, child01, child1, child10, child11, child12]
   /    \        0      1        2        3        4       5        6        7
  c0    c1
 / \   /|\
00 01 10 11 12
```

**Path to offset:** Precompute subtree sizes. To find offset of path (p₀, p₁, ..., pₖ):
```
offset = Σ (sizes of skipped subtrees) + Σ (local offsets)
```

This is O(k) where k is path length—just arithmetic, no pointer chasing.

#### 15.7.4 Dimensional Currying for Trees

Currying works for trees via partial paths:

```
T[(0,)]        -- returns TreeView at child 0
T[(0,)][(1,)]  -- same as T[(0,1)]
```

The subtree operation in the bijection supports this:
```
subtree((0,)) = (offset_of_child0, shape_of_subtree_at_child0)
```

Currying returns a view: a pointer offset plus the sub-shape metadata.

#### 15.7.5 Symmetric Trees

A **symmetric tree** has commutative children—swapping children at any node doesn't change the value:

```
SymmetricTree<Float, shape>
T[(0, 1)] == T[(1, 0)]  -- if children are interchangeable
```

This is analogous to symmetric arrays where `A[i,j] == A[j,i]`.

For symmetric trees:
- Storage can be reduced (only store canonical orderings)
- Left-justification applies (canonical form = sorted path)
- The bijection handles canonicalization

#### 15.7.6 Unification: Arrays and Trees

| Structure | Depth | Branching | Index Type |
|-----------|-------|-----------|------------|
| Vector | 1 | n | `Idx<n>` |
| Matrix | 2 | n × m | `Idx<n> × Idx<m>` |
| Tensor | r | n₁ × ... × nᵣ | `Idx<n₁> × ... × Idx<nᵣ>` |
| Ragged array | r | Variable per position | `RaggedIdx` |
| Tree | Variable | Variable per node | `TreeIdx<shape>` |

**The common abstraction:**
- All are functions from some index domain to values
- All support dimensional currying (partial indexing)
- All can have symmetry (commutative indices/children)
- All can be stored with a bijection to flat memory

**Poly-indexing unifies access:**
```
x[indices]  -- works for any structure
            -- indices is a path/tuple appropriate to the structure
```

#### 15.7.7 Performance Characteristics

**Array access:**
```
A[i₁, i₂, ..., iᵣ] → offset = Σ iₖ × strideₖ
```
- O(r) multiplications and additions
- Single memory access
- Cache-friendly for sequential access

**Tree access:**
```
T[(p₁, p₂, ..., pₖ)] → offset = Σ (subtree_size[pⱼ] for skipped children)
```
- O(k) additions with precomputed subtree sizes
- Single memory access
- No pointer chasing (unlike linked trees)

**Comparison with pointer-based trees:**

| Operation | Pointer Tree | Flat Tree with Bijection |
|-----------|--------------|--------------------------|
| Access path of length k | O(k) pointer chases | O(k) arithmetic + 1 access |
| Cache behavior | k cache misses (random) | 1 cache miss (predictable) |
| Memory overhead | 2-3 pointers per node | Subtree size table |
| Insertion | O(1) at position | O(n) rebuild |

Flat trees with bijection excel for static or rarely-modified structures with frequent access—exactly the case for scientific data.

#### 15.7.8 Open Questions for Trees

1. **Dynamic trees:** Can we support efficient insertion/deletion while maintaining flat storage? (Probably requires buffer/rebuild strategies)

2. **Symmetric tree storage:** What's the analog of triangular storage for trees with commutative children?

3. **Tree × Array hybrids:** What about structures that are trees at some levels and arrays at others? (e.g., a tree of matrices)

4. **Autodiff through trees:** How do tangents/gradients flow through tree-structured computation?

5. **Distributed trees:** Can product-simplex decomposition generalize to trees?

---

## 16. Related Work

### 16.1 Array Languages and Rank Polymorphism

All systems in this section operate under **T/S (collection-first)** orientation—they treat arrays as collections and derive iteration from element operations. Blade's **S/T (structure-first)** orientation represents a paradigm inversion: iteration structure is primary, element operations secondary.

**APL/J/K**: Pioneered implicit iteration via rank polymorphism. Functions automatically lift across array ranks. Loops are invisible—iteration is never reified. Quintessentially T/S: the programmer specifies element operations; iteration is derived.

**Remora** (Slepak et al., 2014): Formalizes rank polymorphism with frame/cell decomposition. A function of type `T^m → T^n` lifts to `T^(m+k) → T^(n+k)` by mapping over k-frames. Does not address varying arity, symmetry, or triangular iteration. T/S orientation with rigorous type theory.

**Dex** (Paszke et al., 2021): Treats arrays as memoized functions; `for` builds arrays eagerly. The insight that "arrays are functions" parallels our dimensional currying. However, Dex loops are syntax (the `for` construct), not first-class composable values with algebraic laws. Dex focuses on typed indices and automatic differentiation. Still T/S: the `for` construct iterates over collections.

**Futhark** (Henriksen et al., 2017): Purely functional GPU programming with nested parallelism. Uses SOACs (Second-Order Array Combinators) like `map`, `reduce`, `scan` for parallelism. Plain loops are explicitly sequential. Nested parallelism is handled by "incremental flattening"—a compiler transformation, not user-facing loop composition. T/S with sophisticated compiler optimizations.

**Paradigm observation**: These systems span 60 years of array language design, yet all share T/S orientation. This is not because T/S is optimal—it reflects the historical path from FORTRAN's element-centric loops. S/T was simply never explored.

### 16.2 Loop Abstractions and Scheduling

**Polyhedral Model** (Feautrier, Bastoul, Bondhugula et al.): Represents loop nests as integer polyhedra for compiler analysis and transformation. Extremely powerful for automatic parallelization and optimization. However, polyhedra are *compiler IR*, not user-facing abstractions. Users don't compose polyhedra; compilers analyze and transform them.

**Halide** (Ragan-Kelley et al., 2013): Separates algorithm from schedule. The algorithm defines *what* to compute; the schedule defines *how* (loop order, tiling, fusion). Schedules are *directives* (`.split()`, `.tile()`, `.compute_at()`) applied to function definitions, not first-class composable values. No algebraic laws govern schedule composition.

**Distinction**: Blade's loop objects are first-class values with algebraic combinators (`<*>`, `<&!>`, `>>@`) satisfying provable laws (MonadPlus structure). Halide schedules are imperative modifications; polyhedral representations are compiler-internal.

### 16.3 Parallel Loop Constructs

**Kokkos/RAJA**: Parallel loop abstractions for HPC with portability across backends. Focus on `parallel_for`, `parallel_reduce`—single loops, not nested loop composition.

**OpenMP**: Pragma-based parallelization. Loops remain syntax; pragmas are annotations.

**Distinction**: These systems parallelize individual loops. Blade reifies *nested* loop structures as single composable values where arity determines nesting depth.

### 16.4 Multi-Dimensional Homomorphisms

**Multi-Dimensional Homomorphisms (MDH)** (Rasch & Gorlatch, 2018; Rasch, 2024): An algebraic formalism for data-parallel array computation. MDH expresses computations via higher-order functions with associative combine operators, enabling divide-and-conquer decomposition. Like Blade, MDH uses algebraic properties of operators to drive optimization and provides a formal foundation for array computation rather than ad-hoc transformations. The key difference: MDH's "homomorphisms" are list homomorphisms (functions distributing over concatenation); Blade's symmetry lowering uses group homomorphisms (maps between permutation groups). MDH does not address symmetric tensors or triangular iteration.

### 16.5 Tensor Compilers

**TACO** (Kjolstad et al., 2017): Format abstraction for sparse tensors. Generates code for arbitrary sparse formats via iteration graph algebra. Addresses *sparsity*, not *symmetry*.

**TVM** (Chen et al., 2018): End-to-end ML compiler with auto-tuning. Extends Halide's scheduling to deep learning workloads. Schedule-based, not combinator-based.

### 16.6 Scientific Python Ecosystem

**xarray**: Labeled multi-dimensional arrays with NetCDF interoperability. No symmetry support.

**Dask**: Lazy evaluation and parallel/distributed computation via task graphs. Graph optimization, not algebraic fusion.

**Complementary usage**: Blade-DSL can consume data from xarray and integrate with Dask for distribution.

### 16.7 Sparse and Masked Array Systems

**scipy.sparse**: COO, CSR, CSC formats for 2D sparse matrices. Runtime coordinate storage, no type-level identity, no currying.

**TileDB**: Multi-dimensional sparse arrays with R-tree indexing. Efficient storage and slicing, but no compile-time type safety, no currying, no type-level mask identity.

**pandas/xarray MultiIndex**: Tuple-of-arrays indexing for hierarchical dimensions. Key differences from Blade's `CompoundIdx`:

| Aspect | xarray MultiIndex | Blade CompoundIdx |
|--------|-------------------|-------------------|
| Type signature | None (runtime) | `N -> N -> ...` (compile-time, k-ary) |
| Identity check | O(n) array comparison | O(1) whole-mask hash |
| Coordinate lookup | O(n) or O(log n) search | O(1) hash lookup |
| Partial indexing | `.sel(lat=x)` runtime filter | `arr[(lat, _)]` compile-time typed |
| Currying | No (flat structure) | Yes (wildcard `_` preserves currying) |
| Storage | Tuple-of-arrays + data | Contiguous data + hash tables |
| Cache order | Not enforced | Enforced (can't skip dimensions) |

**Dex index types**: Sophisticated typed indices with arrays as memoized functions. However, Dex focuses on dense iteration patterns with no mask-derived sparse index types, no curryable compound indices (`N -> N -> ...`), and no wildcard partial indexing.

**Key distinction**: Blade's `CompoundIdx` combines (1) mask-derived type identity via whole-mask hash, (2) curryable type signature matching mask rank, (3) wildcard partial indexing producing well-typed intermediates, and (4) O(1) coordinate lookup via per-element hashing. This combination appears to be novel--individual pieces exist, but not integrated into a coherent index type system.

### 16.8 Novelty and Impact Assessment

| Contribution | N | I | Notes |
|:-------------|:----:|:----:|:------|
| **S/T paradigm (§2)** | 9.5 | 9.5 | Structure-first orientation where iteration is primary, operations secondary. No prior system embraces this. **Key proofs**: Theorems 2.3-2.9 prove T/S *cannot* express iteration objects (2.3), arity polymorphism (2.5), or rank-safe currying (2.6). S/T isn't a preference—it's mathematically required for (r!)^d speedup. |
| **S/T ↔ T/S duality (§2.5)** | 7.2 | 7.0 | S/T and T/S are complementary, not competing. S/T governs outer structure (iteration, symmetry); T/S governs inner reduction (fold, scan, reduce within kernels). They compose: `method_for(A,A) <@> λ(a,b). fold(+,0)(a*b)`. Genuine duality. |
| **Product Symmetry Theorem** | 6.5 | 9.2 | Simplex volume ratios are known mathematics. Contribution is recognizing this as a *PL design principle*: flattening forfeits (r!)^(d-1). Prior work focused on storage, not language-level iteration. |
| **Loop reification** | 9.0 | 9.0 | Part of **Structural Trinity** (§5.6). Nested loops as composable first-class values. **Prior art**: Polyhedral (isl, MLIR) reify as compiler IR; Halide schedules are directives. **Key proofs**: Theorem 5.10 (trinity inseparability), Theorem 2.7 (T/S cannot express). |
| **Arity polymorphism** | 9.0 | 9.0 | Part of **Structural Trinity** (§5.6). Arity determines output rank, loop depth, AND symmetry. **Prior art**: Variadic functions have fixed output types. **Key proofs**: §6.6.1 (variadic insufficient), Theorems 5.6-5.7 (requires loop reification + currying), Theorem 2.5 (T/S cannot express). |
| **Dimensional currying** | 9.0 | 9.0 | Part of **Structural Trinity** (§5.6). Arrays as N-ary functions; partial indexing yields lower-rank arrays. **Prior art**: Dex has arrays-as-functions but no dependent extents. **Key proofs**: Theorems 5.8-5.9 (required for left-justified typing), Theorem 2.6 (T/S cannot express rank-safe version). |
| **Symmetry lowering (§5.6.5, §9.3)** | 8.0 | 8.5 | Function commutativity (Level 2) lowers to array symmetry (Level 1). **Key proofs**: Theorems 5.12-5.18 prove `lower₂₁` is isomorphism for identical arrays, `lower₁₀` trivial. Unifies output symmetry generation and input symmetry quashing as same algebraic structure. |
| **Combinator lifting (§5.6.6)** | 7.5 | 8.5 | `method_for(<&>)` and `object_for(>>)` extend duality to combinators. Enables dynamic kernel construction—kernels from config/user input. Level 3+ collapses to Level 2 via first-class functions. |
| **Left-justified triangular iteration** | 6.5 | 8.5 | T/S *can* flatten triangular iteration, but loses structure (no composition, no arity generalization, no currying). The Trinity *enables* structural triangular iteration with dependent bounds. Left-justification *chooses* the orientation where iterator position = storage position, eliminating offset calculation on every write. Engineering insight atop theoretical foundation—could have chosen upper-triangular and forfeited zero-overhead writes. |
| **AD through S/T (§15.1)** | 7.5 | 7.2 | Jacobian Symmetry Theorem: symmetric inputs → symmetric Jacobian blocks, r! backward speedup. Straightforward application of AD theory to S/T context. |
| **Dependent index types (§3.4)** | 6.2 | 7.5 | Index types depend on file metadata or runtime arrays. Tagged indices for staggered grids catch real bugs. Valuable but incremental over existing dependent type work. |
| **CompoundIdx with currying (§3.4.4)** | 8.0 | 8.5 | Curryable `N -> N -> ...` signature, whole-mask hash identity, wildcard partials, O(1) lookup. Literature search (TileDB, pandas, Dex) found no prior system with this combination. |
| **Overall (current)** | **9.0** | **9.2** | Coherent paradigm shift backed by impossibility proofs. S/T necessity (§2.9) proves the paradigm is mathematically required. Trinity inseparability (§5.6) proves the features form an indivisible system. Multiple independent proof paths converge. |

**N** = Novelty (1-10), **I** = Impact (1-10)

#### Projected with Full Implementation

| Contribution | N | I | Notes |
|:-------------|:----:|:----:|:------|
| Product-simplex decomposition (§15.3) | 7.5 | 8.5 | (n+1)^d branching in native triangular space. Clean generalization but follows from product structure. |
| Triangular distributed execution | 8.2 | 9.2 | End-to-end triangular preservation: iteration → storage → distribution. No existing system maintains triangular structure through the full stack. |
| Triangular file format (§15.4) | 6.8 | 8.2 | Block-aligned I/O for symmetric tensors. Necessary infrastructure, moderate novelty. |
| AD + domain decomposition | 7.8 | 8.5 | Gradient halo exchange at simplex boundaries. Non-trivial but follows from combining AD with decomposition. |
| AD + stencils with symmetry | 7.2 | 7.5 | Symmetric scatter-add for stencil gradients. Open question in formalism. |
| **Overall (fully realized)** | **9.0** | **9.5** | Petabyte-scale differentiable symmetric tensor computation. End-to-end triangular coherence would be a genuine systems contribution. |

#### What's Genuinely Novel

1. **S/T paradigm with impossibility proofs (§2, §2.9)**: Structure-first computation is not merely unexplored—it is *mathematically required*. Theorems 2.3-2.9 prove T/S fundamentally cannot express iteration objects, arity polymorphism, or rank-safe currying. This transforms S/T from "design preference" to proven prerequisite for (r!)^d speedup.

2. **The Structural Trinity (§5.6)**: Loop reification, arity polymorphism, and dimensional currying form an *inseparable system*. Theorem 5.10 proves mutual necessity via dependency cycle. This isn't "combining known techniques"—N-ary triangular iteration with dependent bounds is *impossible* without all three.

3. **S/T ↔ T/S duality (§2.5)**: S/T and T/S are complementary. S/T governs outer structure; T/S governs inner reduction. They compose rather than compete. This duality is distinct from the S/T necessity result.

4. **Symmetry lowering (§5.6.5)**: Theorems 5.12-5.18 formally characterize how commutativity lowers to array symmetry. Unifies output symmetry generation and input symmetry quashing as same algebraic structure.

5. **Product symmetry as PL design principle**: The mathematics is known; recognizing that language design must preserve dimensional structure to avoid (r!)^(d-1) loss is a genuine insight.

6. **CompoundIdx combination (§3.4.4)**: Curryable signature, whole-mask hash identity, wildcard partials, O(1) lookup. Literature search found no prior system with this combination.

#### What's Not Novel (But Valuable)

- Triangular iteration itself (well-known optimization, cf. SySTeC, taco, Cyclops)
- Symmetric tensor storage (established technique, cf. STUR, sBLACs)
- Polyhedral iteration space representation (isl, Omega, MLIR Affine dialect)
- Schedule separation (Halide's key contribution)
- Combinator patterns (functional programming heritage)
- Arrays-as-functions concept in isolation (APL tradition, Dex)
- Dependent types for array bounds in isolation (Idris, Agda, Dex)
- Sparse array indexing (databases, TileDB, scipy.sparse)
- Variable-arity typing in isolation (Strickland et al. ESOP 2009, C++ variadic templates)
- Left-justified indexing (engineering choice, not theoretical)

The contribution is *coherent integration* backed by impossibility proofs—the Structural Trinity's mutual necessity (§5.6) and T/S impossibility theorems (§2.9) show this is mathematical necessity, not mere combination.

---

## 17. Conclusion

Blade-DSL introduces **S/T (structure-first)** computation—a paradigm where iteration structure is primary and element operations are secondary. This inverts the **T/S (collection-first)** orientation that has characterized array programming from FORTRAN through NumPy to modern systems like Dex and Futhark.

**The central theoretical result** is that S/T orientation is not merely a design choice but a *mathematical prerequisite* for symmetric tensor computation. The impossibility cascade (§2.9) proves:

- T/S cannot express first-class iteration objects (Theorem 2.3)
- T/S cannot express arity polymorphism (Theorem 2.5)
- T/S cannot express rank-safe dimensional currying (Theorem 2.6)
- Therefore T/S cannot achieve (r!)^d speedup for symmetric tensors

The S/T ↔ Trinity equivalence (Theorem 2.10) establishes that S/T orientation is necessary and sufficient for the Structural Trinity—loop reification, arity polymorphism, and dimensional currying—which itself is proven inseparable (Theorem 5.10).

The key contributions are:

1. **S/T paradigm as mathematical necessity**: Structure-first computation is not just an alternative to collection-first—it is the *only* orientation capable of expressing the abstractions required for factorial speedup. Multiple independent proof paths (§2.9) converge on this result.

2. **The Structural Trinity (§5.6)**: Loop reification, arity polymorphism, and dimensional currying form a mutually necessary, inseparable system. Removing any one makes the other two inexpressible.

3. **First-class nested loop objects**: `method_for` and `object_for` create multi-level loop nests as single composable values—not syntax, not compiler IR, not schedule directives.

4. **Algebraic combinator laws**: A complete set of operators (`<@>`, `<*>`, `<&!>`, `>>@`, `<|>`) satisfying provable identities including MonadPlus structure. This enables equational reasoning about loop composition.

5. **Symmetry lowering (§5.6.5)**: Formal characterization of how function commutativity lowers to array symmetry via homomorphisms, unifying output symmetry generation and input symmetry quashing.

6. **Dimensional currying**: Related to Dex's "arrays as memoized functions," but with static type-level guarantees of cache-optimal access. This preserves the (r!)^d product symmetry speedup that would be lost by flattening.

7. **Integrated symmetry inference**: Commutativity annotations automatically determine output symmetry and enable triangular iteration.

8. **Dependent index types with CompoundIdx**: Index types may depend on file metadata or runtime arrays (masks, lengths). `CompoundIdx<mask>` provides a curryable type signature (`N -> N -> ...`) with O(1) type identity via whole-mask hash, O(1) coordinate lookup, and wildcard partial indexing.

The (r!)^d speedup from product symmetry is mathematically well-known. Our contribution is proving that exploiting this speedup *requires* a paradigm shift—from T/S to S/T—and providing a complete programming language design that makes this shift practical.

---

## Appendix A: Notation Summary

| Symbol | Meaning |
|--------|---------|
| T^r(σ) | Array type: element T, rank r, symmetry σ |
| method_for | S-first loop constructor |
| object_for | Function-first loop constructor |
| () | Zero array tuple (identity for <*>) |
| zero | Zero function (identity for <\|>, annihilator for >>=) |
| <@> | Application combinator |
| >>= | Monadic bind |
| <&> | Parallel composition |
| <&!> | Mandatory fusion |
| >>@ | Compose ObjectLoops (compose-then-apply) |
| @>> | Compose within MethodLoop (apply-then-compose) |
| <*> | Array product (MethodLoop concatenation) |
| <$> | Functor map |
| <\|> | Choice combinator (MonadPlus, computation-level) |
| <\|:> | Array fallback combinator (first non-null allocation) |
| Tuple(...) | Product type, stays bundled in kernel |
| AlignedExpr | Wrapped zip + stencil metadata, unpacks to separate args |
| zip | Array tuple combinator (n-ary, produces Tuple elements) |
| align | Wrap arrays with stencil spec (produces AlignedExpr) |
| stencil | Sugar for align + shift |
| stack | Combine arrays along new leftmost dimension (n-ary) |
| transpose | Array dimension reordering |
| diag | Diagonal extraction |
| join | Array concatenation along dimension |
| subset | Array subrange extraction |
| split | Array splitting (sugar for subset) |
| reverse | Array index reversal |
| shift | Array index shifting (for stencils) |
| guard(p, c) | Conditional computation |
| pure | Lift to ArrayExpr or Computation |
| sequence | Collect computations |
| replicate | Repeat computation |
| fold | Fold combinator over array tuples |
| \|> compute | Execute computation or materialize ArrayExpr |
| comm(...) | Declare commutativity group |
| arity(any) | Declare variable-arity kernel |
| arity | In-scope total argument count |
| nth | In-scope recursion depth (recursive kernels only) |
| head, tail | First argument and remaining arguments (recursive pattern) |
| args[&#124;k&#124;] | Access kth argument in variadic list |
| omp(x: n) | Parallelize n S-dimension levels for argument x |
| tdim(...) | T-dimension specification |

## Appendix B: Symmetry Vector Examples

| Array | σ | Meaning |
|-------|---|---------|
| float^2(1,2) | Dense matrix | No symmetry |
| float^2(1,1) | Symmetric matrix | A[i,j] = A[j,i] |
| float^3(1,1,1) | Fully symmetric 3-tensor | All permutations equal |
| float^3(1,1,2) | Partially symmetric | Dims 0,1 symmetric; 2 independent |
| float^4(1,1,2,2) | Block symmetric | (0,1) symmetric; (2,3) symmetric |

## Appendix C: Complexity Table

### C.1 Single-Dimension Symmetry (S_r)

| Rank r | Dense | Triangular | Speedup |
|--------|-------|------------|---------|
| 2 | N² | N²/2 | 2× |
| 3 | N³ | N³/6 | 6× |
| 4 | N⁴ | N⁴/24 | 24× |
| r | NÊ³ | NÊ³/r! | r!× |

### C.2 Product Symmetry (S_r^d)

| Rank r | Dims d | Dense | Triangular | Speedup |
|--------|--------|-------|------------|---------|
| 2 | 2 | N⁴ | N⁴/4 | 4× |
| 2 | 3 | N⁶ | N⁶/8 | 8× |
| 3 | 2 | N⁶ | N⁶/36 | 36× |
| 3 | 3 | N⁹ | N⁹/216 | 216× |
| 4 | 2 | N⁸ | N⁸/576 | 576× |
| 4 | 4 | N¹⁶ | N¹⁶/331776 | 331,776× |
| r | d | N^(rd) | N^(rd)/(r!)^d | (r!)^d × |

### C.3 Flattening Loss

| Rank r | Dims d | Product Speedup | Flattened Speedup | Loss Factor |
|--------|--------|-----------------|-------------------|-------------|
| 3 | 2 | 36× | 6× | 6× |
| 4 | 2 | 576× | 24× | 24× |
| 4 | 3 | 13,824× | 24× | 576× |
| 4 | 4 | 331,776× | 24× | 13,824× |
| r | d | (r!)^d | r! | (r!)^(d-1) |
