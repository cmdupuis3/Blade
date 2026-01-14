# Blade-DSL: A Formal Specification (v9)

## Abstract

We present a formal semantics for Blade-DSL, a domain-specific language for symmetric tensor computation. The motivating application is Joint Moment Component Analysis (JMCA)---extending PCA to higher-order statistics for detecting nonlinear climate phenomena---which requires comoment tensors at scales where naïve computation is infeasible.

Symmetric tensors exhibit factorial redundancy: a rank-r symmetric tensor has only 1/r! unique elements. For multidimensional data (e.g., latitude × longitude × time), this compounds to (r!)\^d speedup through *product symmetry*---independent triangular constraints on each dimension. This exponential improvement is the difference between feasible and impossible at petabyte scale, but exploiting it requires language abstractions that traditional array programming cannot express.

Blade-DSL embodies **S/T (structure-first)** computation---a paradigm inversion where iteration structure is primary and element operations are secondary---in contrast to the **T/S (collection-first)** orientation that has dominated array programming since FORTRAN. The core abstraction is the *loop object*: a reified nested iteration pattern that can be partially applied, composed via algebraic combinators, and fused. Unlike prior loop abstractions---iterators (single loops), polyhedral models (compiler IR), or scheduling languages like Halide (directives on syntax)---Blade's loop objects are first-class values with a complete combinator algebra including a MonadPlus structure.

We establish that three features form an inseparable **Structural Trinity**: *loop reification* (iteration patterns as first-class values), *arity polymorphism* (where input count determines output rank and symmetry), and *dimensional currying* (arrays as functions with compile-time rank tracking). Each requires the other two. We prove this trinity is inexpressible in T/S systems, establishing S/T orientation as a mathematical prerequisite for symmetric tensor computations with an (r!)\^d speedup.

## Table of Contents

- [1. Introduction](#1-introduction)
    - [1.1 What Problem Does Blade-DSL Solve?](#11-what-problem-does-blade-dsl-solve)
    - [1.2 What Makes Blade-DSL Different?](#12-what-makes-blade-dsl-different)
    - [1.3 Target Applications and Scale](#13-target-applications-and-scale)
    - [1.4 Relationship to Existing Array Languages](#14-relationship-to-existing-array-languages)
- [2. Computational Paradigms: S/T and T/S](#2-computational-paradigms-st-and-ts)
    - [2.1 Two Orientations Toward Array Computation](#21-two-orientations-toward-array-computation)
    - [2.2 Historical Context](#22-historical-context)
    - [2.3 Formal Characterization](#23-formal-characterization)
    - [2.4 The Duality Theorem](#24-the-duality-theorem)
    - [2.5 Non-Trivial T/S Combinators](#25-non-trivial-ts-combinators)
    - [2.6 The Fundamental Duality: Fusion and Factorization](#26-the-fundamental-duality-fusion-and-factorization)
    - [2.7 The Double Metamorphism](#27-the-double-metamorphism)
    - [2.8 Why S/T Enables Symmetry Exploitation](#28-why-st-enables-symmetry-exploitation)
    - [2.9 Linguistic Parallel](#29-linguistic-parallel)
    - [2.10 S/T as Mathematical Prerequisite: Syntactic Impossibility](#210-st-as-mathematical-prerequisite-syntactic-impossibility)
    - [2.11 S/T as Mathematical Prerequisite: The Necessity Theorems](#211-st-as-mathematical-prerequisite-the-necessity-theorems)
- [3. Preliminaries](#3-preliminaries)
    - [3.1 Notation](#31-notation)
    - [3.2 Arrays](#32-arrays)
    - [3.3 Extents](#33-extents)
    - [3.4 Value Types and Promotion](#34-value-types-and-promotion)
    - [3.5 Array Expressions](#35-array-expressions)
    - [3.6 Array Combinators](#36-array-combinators)
    - [3.7 Array Combinator Laws](#37-array-combinator-laws)
- [4. Index Types](#4-index-types)
    - [4.1 Design Principles](#41-design-principles)
    - [4.2 Base Index Types](#42-base-index-types)
    - [4.3 Dependent Index Types](#43-dependent-index-types)
    - [4.4 Compound Index Semantics](#44-compound-index-semantics)
    - [4.5 Sparse Index Types](#45-sparse-index-types)
    - [4.6 Generalized Dependent Index Types](#46-generalized-dependent-index-types)
    - [4.7 Index Transforms](#47-index-transforms)
    - [4.8 Files as Type Providers](#48-files-as-type-providers)
    - [4.9 Symmetry and Index Types](#49-symmetry-and-index-types)
    - [4.10 Currying by Index](#410-currying-by-index)
    - [4.11 Declaration Syntax](#411-declaration-syntax)
    - [4.12 Bounded Index Types](#412-bounded-index-types)
    - [4.13 Symmetric Index Types](#413-symmetric-index-types)
    - [4.14 Nested and Mixed Symmetry](#414-nested-and-mixed-symmetry)
    - [4.15 User-Defined Index Types](#415-user-defined-index-types)
    - [4.16 Index Type Summary](#416-index-type-summary)
- [5. Array Types](#5-array-types)
    - [5.1 Abstract vs Concrete Array Types](#51-abstract-vs-concrete-array-types)
    - [5.2 Array Type Identity](#52-array-type-identity)
    - [5.3 Arrays as Functions](#53-arrays-as-functions)
    - [5.4 Poly-Indexing](#54-poly-indexing)
    - [5.5 Lambda Indices](#55-lambda-indices)
- [6. Functions](#6-functions)
    - [6.1 Function Signatures](#61-function-signatures)
    - [6.2 Function Syntax](#62-function-syntax)
    - [6.3 Commutativity Groups](#63-commutativity-groups)
    - [6.4 Reynolds Operators](#64-reynolds-operators)
- [7. Core Operations](#7-core-operations)
    - [7.1 Arithmetic Operations](#71-arithmetic-operations)
    - [7.2 Geometric Primitives](#72-geometric-primitives)
    - [7.3 Reductions](#73-reductions)
    - [7.4 Operation Symmetry Summary](#74-operation-symmetry-summary)
- [8. Equivariance System](#8-equivariance-system)
    - [8.1 Relationship to Index Types](#81-relationship-to-index-types)
    - [8.2 Annotation Syntax](#82-annotation-syntax)
    - [8.3 Type Inference](#83-type-inference)
    - [8.4 Error Detection](#84-error-detection)
    - [8.5 Domain Libraries](#85-domain-libraries)
- [9. Loop Objects](#9-loop-objects)
    - [9.1 The Core Abstraction](#91-the-core-abstraction)
    - [9.2 S-Dimensions and T-Dimensions](#92-s-dimensions-and-t-dimensions)
    - [9.3 Method Loop Structure](#93-method-loop-structure)
    - [9.4 Object Loop Structure](#94-object-loop-structure)
    - [9.5 Partial Application Semantics](#95-partial-application-semantics)
    - [9.6 The Structural Trinity: Formal Necessity Proofs](#96-the-structural-trinity-formal-necessity-proofs)
    - [9.7 Uniqueness of method_for and object_for](#97-uniqueness-of-method_for-and-object_for)
    - [9.8 Virtual Arrays](#98-virtual-arrays)
    - [9.9 For-Loop Syntax](#99-for-loop-syntax)
- [10. Arity Polymorphism](#10-arity-polymorphism)
    - [10.1 Distinction from Rank Polymorphism](#101-distinction-from-rank-polymorphism)
    - [10.2 Why Arity Polymorphism Matters](#102-why-arity-polymorphism-matters)
    - [10.3 Arity and Commutativity](#103-arity-and-commutativity)
    - [10.4 Arity-Polymorphic Syntax](#104-arity-polymorphic-syntax)
    - [10.5 Formal Treatment](#105-formal-treatment)
    - [10.6 Comparison to Related Work](#106-comparison-to-related-work)
- [11. Dimensional Currying](#11-dimensional-currying)
    - [11.1 The Core Idea](#111-the-core-idea)
    - [11.2 Type-Level Encoding](#112-type-level-encoding)
    - [11.3 Cache Optimality by Construction](#113-cache-optimality-by-construction)
    - [11.4 Distinction from Slicing](#114-distinction-from-slicing)
    - [11.5 Enabling the Combinator Algebra](#115-enabling-the-combinator-algebra)
    - [11.6 Symmetry Integration](#116-symmetry-integration)
    - [11.7 Sparse Tensor Compatibility](#117-sparse-tensor-compatibility)
- [12. Combinator Algebra](#12-combinator-algebra)
    - [12.1 Core Combinators](#121-core-combinators)
    - [12.2 Parallel Combinators](#122-parallel-combinators)
    - [12.3 Collection Combinators](#123-collection-combinators)
    - [12.4 Evaluation](#124-evaluation)
    - [12.5 Combinator Laws](#125-combinator-laws)
    - [12.6 Composition Combinators and the Duality Theorem](#126-composition-combinators-and-the-duality-theorem)
    - [12.7 The Rank-0 Convergence Theorem](#127-the-rank-0-convergence-theorem)
    - [12.8 Additional Combinator Identities](#128-additional-combinator-identities)
    - [12.9 Zero Elements and Control Flow](#129-zero-elements-and-control-flow)
- [13. Symmetry System](#13-symmetry-system)
    - [13.1 Symmetry/Commutativity States](#131-symmetrycommutativity-states)
    - [13.2 State Computation](#132-state-computation)
    - [13.3 Output Symmetry Inference via Lowering](#133-output-symmetry-inference-via-lowering)
    - [13.4 The Symmetry Transformation (Lowering in Action)](#134-the-symmetry-transformation-lowering-in-action)
- [14. Triangular Iteration](#14-triangular-iteration)
    - [14.1 Cumulative Bound Computation](#141-cumulative-bound-computation)
    - [14.2 Left-Justified Indexing](#142-left-justified-indexing)
    - [14.3 Index Mapping for Access](#143-index-mapping-for-access)
    - [14.4 Complexity Analysis](#144-complexity-analysis)
    - [14.5 Product Symmetry Theorem](#145-product-symmetry-theorem)
- [15. Type System](#15-type-system)
    - [15.1 Judgments](#151-judgments)
    - [15.2 Array Rules](#152-array-rules)
    - [15.3 Function Rules](#153-function-rules)
    - [15.4 Loop Object Rules](#154-loop-object-rules)
    - [15.5 Application Rules](#155-application-rules)
    - [15.6 Combinator Rules](#156-combinator-rules)
- [16. Operational Semantics](#16-operational-semantics)
    - [16.1 Evaluation Model](#161-evaluation-model)
    - [16.2 Loop Level Types](#162-loop-level-types)
    - [16.3 Fusion Analysis](#163-fusion-analysis)
    - [16.4 Compute Semantics](#164-compute-semantics)
- [17. Concrete Syntax](#17-concrete-syntax)
    - [17.1 Array Declaration](#171-array-declaration)
    - [17.2 Array Literals](#172-array-literals)
    - [17.3 Function Declaration](#173-function-declaration)
    - [17.4 Lambda Expressions](#174-lambda-expressions)
    - [17.5 Static Values and Functions](#175-static-values-and-functions)
    - [17.6 Type-Returning vs Value-Returning Functions](#176-type-returning-vs-value-returning-functions)
    - [17.7 Mutability and Borrowing](#177-mutability-and-borrowing)
    - [17.8 Boolean Operators and Comparisons](#178-boolean-operators-and-comparisons)
    - [17.9 Fused Assignment Operators](#179-fused-assignment-operators)
    - [17.10 Conditionals and Pattern Matching](#1710-conditionals-and-pattern-matching)
    - [17.11 Tuple Syntax](#1711-tuple-syntax)
    - [17.12 Sum Types (Variants)](#1712-sum-types-variants)
    - [17.13 Structs](#1713-structs)
    - [17.14 Interfaces](#1714-interfaces)
    - [17.15 Loop Construction and Application](#1715-loop-construction-and-application)
    - [17.16 Combinators](#1716-combinators)
    - [17.17 Poly-Indexing Syntax](#1717-poly-indexing-syntax)
    - [17.18 Pseudo-Native Mathematics](#1718-pseudo-native-mathematics)
    - [17.19 Named Infix Operators](#1719-named-infix-operators)
- [18. Future Work](#18-future-work)
- [19. Related Work](#19-related-work)
    - [19.1 Array Languages and Rank Polymorphism](#191-array-languages-and-rank-polymorphism)
    - [19.2 Loop Abstractions and Scheduling](#192-loop-abstractions-and-scheduling)
    - [19.3 Parallel Loop Constructs](#193-parallel-loop-constructs)
    - [19.4 Multi-Dimensional Homomorphisms](#194-multi-dimensional-homomorphisms)
    - [19.5 Tensor Compilers](#195-tensor-compilers)
    - [19.6 Scientific Python Ecosystem](#196-scientific-python-ecosystem)
    - [19.7 Sparse and Masked Array Systems](#197-sparse-and-masked-array-systems)
    - [19.8 Novelty and Impact Assessment](#198-novelty-and-impact-assessment)
- [20. Conclusion](#20-conclusion)
    - [20.1 Summary of Results](#201-summary-of-results)
    - [20.2 What We Proved](#202-what-we-proved)
    - [20.3 Blade's Canonicity](#203-blades-canonicity)
    - [20.4 Metaprogramming Isomorphism](#204-metaprogramming-isomorphism)
    - [20.5 Final Statement](#205-final-statement)
- [Appendix A: Notation Summary](#appendix-a-notation-summary)
- [Appendix B: Glossary](#appendix-b-glossary)

## 1. Introduction

### 1.1 What Problem Does Blade-DSL Solve?

Climate variability is not symmetric. El Niño and La Niña are not mirror images---El Niño events are stronger but less frequent, La Niña weaker but more persistent. Extreme precipitation clusters in ways that droughts do not. Monsoon onset exhibits threshold behavior invisible to linear analysis.

These asymmetries are encoded in higher-order statistics: coskewness (third-order) captures asymmetric dependencies, cokurtosis (fourth-order) captures tail behavior and clustering. Principal Component Analysis, built on covariance (second-order), cannot see them. Joint Moment Component Analysis (JMCA) extends PCA to these higher-order tensors, but the computational cost is prohibitive.

Consider a climate dataset with 1000 spatial grid points. The covariance matrix has 1 million entries. The coskewness tensor has 1 billion. The cokurtosis tensor has 1 trillion. But these tensors are symmetric: coskewness at position (i, j, k) equals the value at all permutations (j, i, k), (k, j, i), etc. At most 1/6 of coskewness entries are unique; at most 1/24 of cokurtosis entries. For higher-order statistics, the savings grow factorially---and for multidimensional data (latitude × longitude × time), *product symmetry* compounds these savings exponentially across dimensions.

Blade-DSL was designed to make these optimizations automatic and composable.

### 1.2 What Makes Blade-DSL Different?

**1. Loops Are Values, Not Syntax**

In most languages, a `for` loop is syntax. In Blade-DSL, iteration patterns are first-class values that can be stored, composed, and reused:

```blade
let coskew_op = object_for(coskewness_kernel)
let cokurt_op = object_for(cokurtosis_kernel)

let result1 = coskew_op <@> (data, data, data) |> compute        // 3rd-order self-comoment
let result2 = cokurt_op <@> (data, data, data, data) |> compute  // 4th-order self-comoment
let result3 = coskew_op <@> (temp, temp, precip) |> compute      // cross-comoment
```

The same operator applies to different data configurations. Libraries of reusable iteration patterns become possible.

**2. The Type System Guarantees Cache Efficiency**

Most array libraries rely on the compiler to infer good memory access patterns. Blade-DSL encodes cache-optimal access in the type system via *dimensional currying*---treating arrays as functions from indices to values. A cache-inefficient traversal is not a performance bug; it is a type error.

**3. Symmetry Is Automatic**

Declaring a function commutative triggers automatic optimization:

-   Triangular iteration (skipping redundant index combinations)
-   Compact triangular storage allocation
-   Output symmetry inference

The programmer states the mathematical property; correct code emerges.

**4. Arity Polymorphism**

Traditional array languages support *rank polymorphism*: the same function works on vectors, matrices, and higher-rank tensors. Blade-DSL adds *arity polymorphism*: the same kernel accepts different numbers of input arrays, with input count determining output rank and symmetry. Unlike variadic functions, where arity affects only the computation's inputs, arity polymorphism propagates through the type system---a single comoment kernel computes covariance, coskewness, or cokurtosis depending on whether it receives two, three, or four arrays, producing rank-2, rank-3, or rank-4 output respectively.

### 1.3 Target Applications and Scale

**Primary application: Climate science**

Standard climate diagnostics rely on covariance-based methods---EOFs, PCA, linear regression---that assume Gaussian structure. Real climate variability is not Gaussian: El Niño/La Niña asymmetry, extreme precipitation clustering, and monsoon onset dynamics all involve nonlinear dependencies that second-order statistics cannot detect.

Joint Moment Component Analysis (JMCA) addresses this gap by extending PCA to higher-order comoment tensors. Where PCA finds directions of maximum variance, JMCA finds directions of maximum skewness, kurtosis, or higher-order structure. This enables detection of:

-   **Asymmetric oscillations**: El Niño events are fewer but stronger; La Niña more frequent but weaker. Coskewness captures this amplitude asymmetry.
-   **Extreme clustering**: Heavy precipitation events cluster spatially and temporally in ways invisible to covariance. Cokurtosis quantifies tail dependence.
-   **Threshold dynamics**: Monsoon onset, sea ice collapse, and vegetation dieback exhibit abrupt transitions encoded in higher-order moments.

The computational barrier is scale. At resolutions climate scientists require:

  Resolution   Grid Points   Coskewness Size   Cokurtosis Size
  ------------ ------------- ----------------- -----------------
  2°           16,200        709 GB            2.9 PB
  1°           64,800        45 TB             740 PB
  0.25°        1,036,800     186 PB            ---

At 1° resolution---standard for modern reanalyses---even the third-order comoment tensor is 45 terabytes. Without symmetry exploitation and distributed computation, JMCA at these scales is not merely slow---it is impossible.

**Secondary applications**

The abstractions generalize beyond climate:

-   **Quantum physics**: Higher-order correlation functions, entanglement measures
-   **Neuroscience**: Cross-frequency coupling, higher-order connectivity
-   **Finance**: Coskewness and cokurtosis for portfolio risk, tail dependence
-   **Genomics**: Higher-order epistasis, multi-way gene interactions

Any domain computing symmetric tensors over structured multi-dimensional data can benefit from product symmetry speedups.

**Intended audience**

This specification targets two audiences. For programming language researchers, Blade-DSL demonstrates novel type-theoretic constructs---loop reification, arity polymorphism, and dimensional currying---with formal semantics and impossibility proofs establishing their necessity. For scientific computing practitioners, it provides a practical language design for high-performance symmetric tensor computation, with applications extending beyond the motivating climate science use case to any domain requiring structured iteration over multi-dimensional data.

Familiarity with array programming concepts (ranks, shapes, broadcasting) and basic type theory (type judgments, inference rules) is assumed. No prior knowledge of Blade-DSL or climate science is required.

### 1.4 Relationship to Existing Array Languages

Blade-DSL is a general-purpose array programming language, not merely a library or DSL extension. It can express any computation expressible in NumPy, xarray, or similar systems---but its design priorities differ:

  Aspect          NumPy/xarray/Dask         Blade-DSL
  --------------- ------------------------- ------------------------
  Iteration       Hidden (vectorized ops)   Explicit loop objects
  Symmetry        Manual (if at all)        Automatic exploitation
  Memory layout   Runtime concern           Type-level guarantee
  Arity           Fixed per function        Polymorphic
  Fusion          Limited/heuristic         Algebraic combinators

The S/T (structure-first) orientation that enables these features is a paradigm shift, not an incremental improvement. Vectorized T/S systems excel when computations map naturally onto element-wise operations and reductions. Blade-DSL provides abstractions for computations where iteration structure itself is the primary concern---symmetric tensors being the motivating case, but the language is not limited to them.

------------------------------------------------------------------------

## 2. Computational Paradigms: S/T and T/S

### 2.1 Two Orientations Toward Array Computation

Blade-DSL embodies what we term **S/T (structure-first)** computation, in contrast to the **T/S (collection-first)** orientation that has dominated array programming since FORTRAN. The naming itself encodes the inversion: where T/S places the T-dimension (what you reduce over) first, S/T places the S-dimension (what you iterate over) first. This syntactic inversion forces a semantic inversion.

**T/S (collection-first):** Arrays are conceived as collections to be traversed. Computation specifies operations over collection elements; iteration structure is implicit or inferred.

```
# NumPy: collection-first
result = np.sum(A * B, axis=-1)
C = np.einsum('ij,jk->ik', A, B)
```

The programmer thinks: "I have these collections. Apply these operations across them."

**S/T (structure-first):** Arrays are conceived as functions from index spaces. Computation specifies iteration structure explicitly; operations are applied to that structure.

```blade
// Blade: structure-first
method_for(A, B) <@> kernel
```

The programmer thinks: "Here is the iteration structure. Apply this kernel to it."

### 2.2 Historical Context

All major array programming systems employ T/S orientation, though some show partial S/T tendencies:

  -----------------------------------------------------------------------------------------------------------------------------------------------------------------------------
  System                   Year      Orientation         S/T Score       Evidence
  ------------------------ --------- ------------------- --------------- ------------------------------------------------------------------------------------------------------
  FORTRAN                  1957      Pure T/S            0/5             Explicit loops over elements

  APL                      1962      Pure T/S            0/5             Implicit iteration via rank polymorphism

  R                        1993      T/S (partial S/T)   1/5             `apply` family reifies iteration choice; formula objects (`y ~ x`) separate structure from operation

  NumPy                    2006      Pure T/S            0/5             Vectorized element operations

  data.table               2008      T/S (partial S/T)   1/5             `by=` and `.SD` expose grouping structure before operation

  TensorFlow               2015      Pure T/S            0/5             Dataflow over tensor elements

  Halide                   2013      T/S + schedules     2/5             Separates algorithm from schedule, but schedules are directives on syntax

  Chapel                   2009      T/S (partial S/T)   2/5             Domains are first-class values, but no loop combinators

  Dex                      2021      T/S                 1/5             `for` as array builder (syntax, not value); arrays as functions

  Polyhedral (isl, MLIR)   various   Compiler IR         1/5             Loop nests as polyhedra---reified, but as IR, not user-facing

  **Blade-DSL**            2024      **Pure S/T**        **5/5**         Loop objects as first-class values; combinator algebra; arity polymorphism
  -----------------------------------------------------------------------------------------------------------------------------------------------------------------------------

The T/S orientation is so pervasive that it rarely appears as a choice---it is simply "how array programming works."

#### 2.2.1 Notable Systems with Partial S/T Tendencies

**Chapel (Domains):** Chapel is the closest existing system to S/T among mainstream languages. Domains are first-class values (`const D = {1..n, 1..m}`), and domain maps separate structure from layout (`dmapped Block(...)`). However, Chapel lacks composable loop combinators with algebraic laws, arity polymorphism, and loop objects that can be partially applied. Chapel's domains are *index sets*, not *iteration patterns*---you can describe *what* indices exist, but not *how* to compose nested iteration over multiple domains.

**Halide (Schedule Separation):** Halide's key insight---separating algorithm from schedule---is related to S/T. But schedules are **directives applied to syntax** (`.split()`, `.tile()`), not first-class values. You cannot store a schedule as a value, compose schedules algebraically, or apply the same schedule to different algorithms polymorphically.

**Dex (Arrays as Functions):** Dex's "arrays as functions" insight is closely related to dimensional currying. But Dex's `for` is **syntax**, not a value---you cannot abstract over nesting patterns or apply different kernels to the same iteration structure.

**Polyhedral Model:** The polyhedral model *does* reify iteration structure as integer polyhedra. This is genuine loop reification, but it is **compiler IR**, not user-facing. Users don't compose polyhedra; compilers transform them.

#### 2.2.2 Gap Analysis

  ------------------------------------------------------------------------------------------------------------------------------
  S/T Feature                     Chapel              Halide            Dex           Polyhedral           Blade
  ------------------------------- ------------------- ----------------- ------------- -------------------- ---------------------
  First-class iteration objects   Partial (domains)   No (directives)   No (syntax)   Yes (IR only)        **Yes**

  Composable loop combinators     No                  No                No            No (IR transforms)   **Yes**

  Arity → output structure        No                  No                No            N/A                  **Yes**

  Structure-before-kernel         Partial             Schedule-level    No            Yes (IR)             **Yes**

  Algebraic laws                  No                  No                No            Transform rules      **Yes** (MonadPlus)
  ------------------------------------------------------------------------------------------------------------------------------

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

-   **`method_for(A₁, ..., Aₙ)`** constructs iteration structure (a `MethodLoop`) from array signatures
-   **`object_for(f)`** constructs iteration structure from a kernel's arity requirements\
-   **`<@>`** applies a kernel to an iteration structure, producing a `Computation`

The iteration structure exists as a value *before* any kernel is applied.

### 2.4 The Duality Theorem

[]{#theorem-2-1}**Theorem 2.1 (S/T Completeness):** Any T/S computation can be expressed in S/T form.

*Proof sketch:* Given a T/S computation `f(A₁, ..., Aₙ) → B`, construct `L = method_for(A₁, ..., Aₙ)` and `K = λ(a₁, ..., aₙ). f_pointwise(...)`. Then `L <@> K` produces B. *(Full proof in Blade Proofs document.)*

[]{#lemma-2-2}**Lemma 2.2 (T-Dimension Relationality):** T-dimensions are defined *relationally* as "dimensions consumed from each array by the kernel"—they depend on both kernel and array signatures.

[]{#lemma-2-2}**Lemma 2.2b (S-Dimension Determinability):** Given input arrays and kernel signature, S-dimensions are fully determined.

[]{#theorem-2-3}**Theorem 2.3 (Iteration Object Impossibility in T/S):** No T/S system admits iteration objects.

*Proof sketch:* T-dimensions are relational (Lemma 2.2). Without knowing the kernel, iteration structure cannot be determined independently. *(Full proof in Blade Proofs document.)*

[]{#corollary-2-4}**Corollary 2.4:** T/S partial evaluation does not yield first-class iteration objects.

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

```blade
let L = method_for(A, A) where comm(A, A)    // S/T: triangular iteration
let K = λ(a, b). fold(+, 0)(a * b)           // T/S: dot product via fold
L <@> K |> compute                            // Compose S/T and T/S
```

This establishes genuine duality: S/T and T/S are complementary, not competing. S/T governs the outer iteration structure; T/S governs the inner reduction strategy.

### 2.6 The Fundamental Duality: Fusion and Factorization

The S/T and T/S duality has a deeper structural explanation.

**Iteration and indexing** are the two fundamental operations for working with arrays: - **Iteration:** Enumerating positions (the loop) - **Indexing:** Accessing values at positions (the subscript)

T/S and S/T differ in how they relate these two operations:

**T/S: Two primitives to one construct**

In T/S, iteration and indexing are separate primitives that the programmer combines:

```
for i in range(n):          # iteration (primitive 1)
    for j in range(i, n):   # iteration (primitive 1)
        x = A(i, j)         # indexing (primitive 2)
        f(x)                # operation
```

The `for` loop is a construct that handles both, but the programmer must manually specify the iteration bounds and the indexing expressions. The triangular structure emerges from explicit composition.

**S/T: One fused concept to two constructors**

In S/T, iteration and indexing are fused into a single concept: the loop object. This unified object can be constructed from two directions:

```blade
method_for(A, A)   // From structure: arrays determine iteration+indexing
object_for(f)      // From operation: kernel determines iteration+indexing
```

Both produce the same kind of object. The programmer does not combine iteration and indexing; they are already fused. Instead, the programmer chooses which information (structure or operation) to derive the fused object from.

**The structural relationship:**

|  | T/S | S/T |
|--|-----|-----|
| Iteration, indexing | Two primitives | One fused concept |
| Construct | One (`for` loop) | Two (`method_for`, `object_for`) |
| Direction | Combine parts to whole | Whole approached from two sides |
| Pattern | 2 to 1 | 1 to 2 |

**Why this matters:**

In T/S, there is no unified object representing "the iteration-indexing pattern." The programmer assembles it each time. Properties like symmetry, commutativity optimization, and triangular iteration must be manually implemented.

In S/T, the loop object *is* the iteration-indexing pattern. Its properties can be:

-   Represented (the symcomstate table)
-   Reasoned about (choosing between symmetry and commutativity paths)
-   Composed (`<*>`, `<&>` combinators)
-   Applied to different kernels (`<@>`)

**The `method_for`/`object_for` duality:**

Because iteration+indexing is fused, a loop object can be derived from either side:

-   **`method_for(A, A)`**: From structure: array shapes and symmetry determine what positions exist and how to enumerate them
-   **`object_for(f)`**: From operation: kernel arity and commutativity determine what iteration structure is required

The `<@>` operator connects them: a loop object from `method_for` is applied to a kernel; a loop object from `object_for` is applied to arrays. Both must agree: the structure must match what the operation requires.

**Connection to index anonymity:**

In T/S, the kernel explicitly names indices (`A(i, j)`). In S/T, the kernel receives values without naming indices (`fun(a, b). ...`). This is possible because the fused loop object handles both iteration *and* indexing internally. The kernel does not need to know how values are accessed; that is encapsulated in the loop object.

### 2.7 The Double Metamorphism

The loop object has deeper structure than a simple hylomorphism. It is a **double metamorphism with feedback**—two coupled cata→ana cycles where the index level drives the data level.

#### The Five Phases

At each iteration step:

| Phase | Level | Morphism | Action |
|-------|-------|----------|--------|
| 1 | Index | cata | Structure remaining index space (using current i) |
| 2 | Index | ana | Emit next index from structured space |
| 3 | Data | cata | Read from inputs at index i |
| 4 | Data | homo | Transform values |
| 5 | Data | ana | Write to output at index i |

Then loop back to phase 1.

#### The Feedback Loop

The index emitted in phase 2 is consumed by phases 3 and 5 (read and write need to know *where*). Crucially, the index also feeds back to phase 1—structuring the *remaining* index space for the next iteration.

In triangular iteration, this is explicit:

```blade
for i in 0..n:
    for j in i..n:    // j's domain depends on i
```

The bound `i..n` is the index-cata: structuring remaining space based on current index.

```
┌─────────────────────────────────────────────────────────────┐
│                                                             │
│   Index: cata ──> ana ──┐                                   │
│            ^            │ (emit i)                          │
│            │            v                                   │
│            │   Data: cata ──> homo ──> ana                  │
│            │            │               │                   │
│            │            └──── i ────────┘                   │
│            │                                                │
│            └──────── (remaining, i) ───────────────────────┘
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

#### Two Coupled Metamorphisms

Each level is cata→ana (a metamorphism):

| Level | Cata | Ana |
|-------|------|-----|
| Index | structure remaining space | emit next index |
| Data | read from input | write to output |

The index metamorphism *drives* the data metamorphism. The index emitted by index-ana parameterizes both data-cata (where to read) and data-ana (where to write).

#### Why "Double Metamorphism with Feedback"

- **Double**: two metamorphisms (index-level, data-level)
- **Metamorphism**: each is cata→ana
- **Feedback**: current index flows back to structure the next iteration

This is the complete structure of `nested_for`. The feedback loop explains why triangular iteration requires S/T: the dependent bound `j in i..n` references `i`, which only exists inside the loop. T/S cannot encapsulate this scope dependency as a first-class value.


### 2.8 Why S/T Enables Symmetry Exploitation

T/S orientation treats iteration as implicit---derived from element operations. This prevents explicit manipulation of iteration structure.

For symmetric tensors, we need:

1.  **Triangular bounds:** Iterate only over unique index combinations
2.  **Bound propagation:** Inner loop bounds depend on outer loop indices
3.  **Output symmetry inference:** Kernel commutativity implies output symmetry

These require *explicit representation* of iteration structure. In T/S, iteration structure is implicit and inaccessible. In S/T, it is a first-class value that can be:

-   Inspected for symmetry properties
-   Modified to enforce triangular bounds
-   Composed with other iteration structures

The Product Symmetry Theorem's (r!)\^d speedup requires preserving dimensional independence during iteration. This is only possible when iteration structure is explicit.

### 2.9 Linguistic Parallel

The S/T versus T/S distinction parallels a typological distinction in natural language grammar.

**Nominative/accusative languages** (English, most Indo-European): The agent receives consistent grammatical marking. "She sees him"---the subject is primary.

**Ergative/absolutive languages** (Basque, Georgian, Dyirbal): The patient receives consistent grammatical marking. The affected entity is the default.

| Linguistic | Computational | Primary Object |
|------------|---------------|----------------|
| Nominative/accusative | T/S | The operation (agent) |
| Ergative/absolutive | S/T | The structure (patient) |

T/S asks: "What does the operation do to the data?" S/T asks: "What structure does the data have, and what operations respect it?"

**Remark (Speculative Status)**: The ergative/absolutive parallel is a suggestive analogy that may aid intuition about the S/T versus T/S distinction. Whether this parallel reflects deeper cognitive or linguistic structure remains an open question. We note it as a potentially fruitful analogy, not a theoretical foundation for Blade's design. The technical results (impossibility theorems, Trinity inseparability) stand independently of any linguistic claims.

### 2.10 S/T as Mathematical Prerequisite: Syntactic Impossibility

The following theorems establish that certain constructs are syntactically impossible in fixed-text programs. Full proofs are in the **Blade Proofs** document.

[]{#theorem-2-5}**Theorem 2.5 (Cumulative Bound Dependency)**: In left-justified triangular iteration of arity r, the bound expression for loop k requires all k preceding index variables.

[]{#theorem-2-6}**Theorem 2.6 (Lexical Nesting Requirement)**: Expressing arity-r triangular iteration requires r textually nested loop constructs.

[]{#theorem-2-7}**Theorem 2.7 (Fixed-Text Impossibility)**: No fixed textual program can express triangular iteration for arbitrary arity r.

[]{#theorem-2-8}**Theorem 2.8 (Recursion Obscures Structure)**: Recursive encoding makes loop structure implicit and uninspectable.

[]{#theorem-2-9}**Theorem 2.9 (Reification Necessity)**: Loop structures for N-ary triangular iteration must be first-class runtime values.

### 2.11 S/T as Mathematical Prerequisite: The Necessity Theorems

The preceding sections establish that S/T and T/S are genuinely distinct, and §2.9 establishes syntactic impossibility results for variable-arity triangular iteration. We now prove a stronger result: the Structural Trinity (§9.6) is *only* expressible in S/T-oriented systems. This elevates S/T from a "design philosophy" to a **mathematical prerequisite** for symmetric tensor computation.

#### 2.10.1 T/S Impossibility for Trinity Components

Building on [Theorem 2.3](#theorem-2-3) (Iteration Object Impossibility), we establish that T/S cannot express any Trinity component:

**T/S Cannot Express Arity Polymorphism**: Arity polymorphism requires iteration depth to vary with input count. By [Theorem 2.3](#theorem-2-3), T/S cannot represent iteration structures as values that vary with input. More concretely: to express `method_for(A, A, A)` (3 arrays → 3-deep iteration), the system must represent "three nested loops" as a value---which T/S cannot do.

**T/S Cannot Express Rank-Safe Dimensional Currying**: Compile-time rank tracking requires knowing output rank before execution. Output rank = input rank − consumed dimensions. By [Lemma 2.2](#lemma-2-2), consumed dimensions are relational---they depend on the kernel. Without knowing the kernel at the currying site, consumed dimensions are unknown. T/S systems can provide *runtime* slicing (NumPy views), but not *compile-time typed* currying where `A(i)` has a statically-known lower rank.

**T/S Cannot Express Loop Reification**: Direct consequence of [Theorem 2.3](#theorem-2-3). Loop reification *is* the existence of iteration objects. T/S does not admit iteration objects.

#### 2.10.2 The S/T ↔︎ Trinity Equivalence

**Trinity Implies S/T**: Any system with the Structural Trinity is necessarily S/T-oriented. All three components require iteration structure to be *primary* and *explicit*---constructed before kernels are specified, composable independently of element operations. This is precisely S/T orientation.

**S/T Necessity for Trinity**: The Structural Trinity is expressible *only* in S/T-oriented systems. By contrapositive: T/S orientation implies no arity polymorphism, no rank-safe currying, and no loop reification (per §2.10.1). Therefore T/S → ¬Trinity. Contrapositive: Trinity → S/T.

**Corollary (S/T ↔︎ Trinity Equivalence)**: For systems capable of symmetric tensor computation with (r!)\^d speedup: *S/T orientation* ⟺ *Structural Trinity is expressible*. Blade-DSL demonstrates constructively that S/T permits all three; the above shows Trinity requires S/T.

#### 2.10.3 The Impossibility Cascade

These results establish a cascade of impossibilities in T/S systems:

```
T/S orientation
    ← Theorem 2.3
No first-class iteration objects
    ← §2.10.1
No arity polymorphism ∧ No rank-safe currying ∧ No loop reification
    ← Trinity Inseparability (Theorem 9.7)
No triangular iteration with dependent bounds
    ← Product Symmetry Theorem
No (r!)^d speedup for symmetric tensors
```

Each step is a proven implication. The cascade shows that T/S orientation is not merely "less convenient" for symmetric tensors---it is *fundamentally incapable* of expressing the abstractions required for factorial speedup.

#### 2.10.4 Index Anonymity Requires Runtime Reification

[]{#theorem-2-10}**Theorem 2.10 (Index Anonymity Requires Runtime Reification):**

Let L be a language supporting:

1.  **Symmetry optimization**: Triangular iteration for symmetric array dimensions
2.  **Commutativity optimization**: Triangular iteration for commutative functions over identical arrays
3.  **Dependent index types**: Index types parameterized by runtime values (extents, masks)
4.  **Combinator composition**: Arbitrary nesting and sequencing of operations

Then L must represent loop structures as first-class runtime values.

*Proof:* The proof proceeds through two independent lemmas, either of which suffices.

Before proving, we introduce two concepts formalized in later sections:

**SymcomState** (formalized in §13.1): For each array-dimension pair in a loop, a four-valued state tracking whether symmetry exploitation is possible:

-   *Neither*: No exploitable structure at this position
-   *Symmetric*: Array dimension symmetric with previous dimension of same array
-   *Commutative*: Kernel commutative in this argument and same array in previous position
-   *Both*: Both conditions hold

Computing SymcomState requires comparing arrays for identity (a runtime property) and checking kernel commutativity against array positions.

**Symmetry Tower**: Blade's type structure forms a hierarchy where each level's types are parameterized by values from the level below. The key observation is that type equality at each level reduces to value comparisons: `Idx<n> = Idx<m>` iff `n = m`. This runtime dependency propagates upward through the tower.

[]{#lemma-2-10-1}**Lemma 2.10.1 (Symmetry Tower Recursion Forces Runtime):**

Blade's symmetry structure forms a tower:

```
Level 4: Combinators         — composition of loop structures
Level 3: Loops/SymcomState   — triangular iteration structure  
Level 2: Kernel symmetry     — commutativity, array identity
Level 1: Array types         — concrete extents, masks, index types
Level 0: Elements            — base values
```

The tower tops out at Level 4 (combinators), which compose Level 3 objects to produce Level 3 objects---a closed recursion. Every index type is parameterized by runtime values: `Idx<n>` by extent n, `CompoundIdx<mask>` by mask array. Type equality reduces to value equality: `Idx<n> = Idx<m>` iff n = m. SymcomState construction requires concrete type equality (for symmetry) and array identity (for commutativity)---both runtime properties.

Combinator composition (`<&>`, `@>>`, `>>@`, `<*>`) is recursive: `compose(compose(L1, L2), L3)` requires evaluating inner compositions first, each demanding runtime information. The runtime requirement is incurred at every AST node, not once. Therefore, combinator expressions cannot be compile-time evaluated. ∎

[]{#lemma-2-10-2}**Lemma 2.10.2 (Runtime Selection Requires Runtime Values):**

A value is first-class if it can be stored, passed, returned, and selected conditionally at runtime. Compile-time code generation (templates, macros, staging) produces code, not values---after compilation, only execution results exist.

Optimal iteration (triangular vs rectangular) depends on array identity, a runtime property: arrays may be loaded from files, received as arguments, or computed. The same code path may receive identical or distinct arrays on different executions. To select between strategies at runtime, both must exist as runtime entities. A compile-time-only representation produces a single code path with nothing to select.

This argument is independent of metalevel count. Even infinite compile-time metalevels eventually produce a fixed artifact; runtime selection requires structures to persist as data. ∎

**By [Lemma 2.10.1](#lemma-2-10-1)**, requirements (3) and (4) make symcomState construction runtime-dependent. **By [Lemma 2.10.2](#lemma-2-10-2)**, first-class loop objects require runtime representation independently. Therefore, L must represent loop structures as first-class runtime values. ∎

[]{#corollary-2-10-1}**Corollary 2.10.1 (Index Anonymity):**

Runtime reification enables index anonymity. With variable arity, the index count varies. Named identifiers are fixed at compile time---a kernel with `i, j, k` cannot handle arity 4. Runtime loop structures provide uniform access: `args[k]`, `let (head, tail) = args`, `indices[k]`. The same kernel body works for all arities. ∎

[]{#corollary-2-10-2}**Corollary 2.10.2 (S/T Necessity):**

A system with first-class runtime loop structures treats iteration as primary: loops exist before kernels, can be manipulated independently, and provide anonymous index access. This is S/T orientation by definition. ∎

**Remark (Metalanguage Tower):**

One might attempt compile-time code generation via templates, macros, or staging. But manipulating symcomState tables at compile time requires meta-metaprogramming to compose across operations, ascending one metalevel per combinator nesting. Since users can compose arbitrarily many operations, no fixed metalevel suffices. Runtime reification collapses this tower: symcomState becomes data manipulated by ordinary functions.

**Remark (Degenerate Static Case):**

For programs where all array extents are compile-time literals, all variables statically bound, and no control flow affects array structure, the symmetry tower is compile-time evaluable. Such programs are degenerate---they compute fixed results over fixed shapes. Scientific computing fundamentally requires data-driven dimensions, parameterized functions, and conditional structure. The runtime requirement is the cost of being a useful language.

| Lemma | Core Claim | Impossibility Type |
|-------|------------|-------------------|
| 2.10.1 | Symmetry tower recurses; each level demands runtime info | Information unavailability |
| 2.10.2 | Loops must be selectable at runtime | Code ≠ values |

These independent barriers establish the necessity of runtime reification for optimal symmetric tensor computation.


#### 2.11.5 Zero-Cost Requirement: Runtime vs Compile-Time Symmetry

The preceding subsections establish that S/T is *necessary*. We now prove a stronger result: **zero-cost** `(r!)^d` speedup specifically requires compile-time symmetry tracking.

**The Symmetry Tower (Revisited):**

| Level | Symmetry | Example |
|-------|----------|---------|
| 0 | Elements | `a = a` |
| 1 | Arrays | `A[i,j] = A[j,i]` |
| 2 | Functions | `f(x,y) = f(y,x)` |
| 3 | Combinators | Associativity |

Triangular iteration depends on Level 2 (commutativity)—a *code property*, not data.

**Theorem 2.12 (Runtime Symmetry Induces Branching):**

Runtime symmetry tracking induces O(n^r / r!) conditional branches.

*Proof:* Without type-level symmetry knowledge, the compiler must generate code that handles both symmetric and non-symmetric cases:

```
for i₁ in 0..n:
    for i₂ in (symmetric ? i₁ : 0)..n:          // branch
        for i₃ in (symmetric ? i₂ : 0)..n:      // branch
            offset = symmetric ? triangular_offset(...) : rectangular_offset(...)
```

These branches execute O(n^r / r!) times. Branch misprediction prevents unrolling and blocks vectorization. This is not zero-cost. ∎

**Theorem 2.13 (Type-Level Symmetry Eliminates Branching):**

Compile-time symmetry tracking generates unconditional code.

*Proof:* With type-level symmetry, the compiler generates a single code path:

```
for i₁ in 0..n:
    for i₂ in i₁..n:
        for i₃ in i₂..n:
            result[triangular_offset(i₁,i₂,i₃)] = kernel(...)
```

The iteration strategy is selected once at compile time. No runtime branches. ∎

**Theorem 2.14 (Metaprogramming Rebuilds S/T):**

Any metaprogramming approach achieving zero-cost factorial speedup implements S/T.

*Proof:* Consider C++ templates with `if constexpr`:

```cpp
template<bool Symmetric>
void iterate() {
    if constexpr (Symmetric) {
        // triangular iteration
    } else {
        // rectangular iteration
    }
}
```

This places symmetry in the type system (`bool Symmetric` is a type parameter) and uses type-directed code generation. By definition, this is S/T orientation.

For composition, commutativity must propagate:

```cpp
template<bool Comm1, bool Comm2>
auto compose(Kernel<Comm1> k1, Kernel<Comm2> k2) {
    return Kernel<Comm1 && Comm2>(k1 >> k2);
}
```

This rebuilds Blade's commutativity type system inside C++ templates. ∎

**Theorem 2.15 (S/T Inevitability):**

Zero-cost `(r!)^d` speedup requires:

1. Compile-time symmetry tracking (not runtime)
2. Symmetry propagation through composition
3. Type-directed iteration structure generation

This is the definition of S/T.

*Proof:*

(1) Zero-cost → no per-iteration branching (Theorem 2.12) → unconditional bounds → compile-time symmetry.

(2) Composition of kernels requires knowing whether the composed kernel is commutative. For zero-cost, this must be computed at compile time → compile-time propagation.

(3) Different symmetry configurations require different loop structures. Generating the correct structure from type information → type-directed code generation.

Requirements (1)-(3) constitute S/T orientation. ∎

**Design Space Exhaustion:**

| Approach | Mechanism | Cost |
|----------|-----------|------|
| Native S/T (Blade) | Symmetry in type system | Zero-cost |
| Metaprogramming | Rebuild S/T in templates | Zero-cost, complex |
| Runtime tracking | Defer symmetry decisions | Per-iteration overhead |

Options 1 and 2 are isomorphic (Theorem 2.14). Option 3 sacrifices zero-cost (Theorem 2.12). **No fourth option exists.**

**Corollary 2.15.1:** T/S cannot achieve zero-cost factorial speedup. Any attempt to add this capability to T/S rebuilds S/T (via metaprogramming, staging, or embedded DSL).

**Corollary 2.15.2:** S/T is not a design choice—it is mathematically inevitable for zero-cost symmetric tensor computation.


------------------------------------------------------------------------

**Remark (Possible Contributing Factors):**

Several factors *may* explain why S/T orientation was not previously explored:

1.  **The Flattening Bias:** "Flatten for performance" became received wisdom from FORTRAN through NumPy. GPUs reinforced this with coalesced memory requirements.

2.  **Divided Communities:** The solution required synthesizing PL theory, numerical computing, group theory, HPC, and domain science. No single community spans all areas.

3.  **Obvious in Hindsight:** Once explained, S/T seems natural. This apparent simplicity obscures the difficulty of discovering it.

4.  **The Incremental Trap:** Existing tools are well-optimized for T/S. Fundamental redesigns require encountering problems that expose architectural limitations.

These are speculative observations, not proven claims. The technical results (Theorems 2.3--2.9) establish that S/T is mathematically required; why it wasn't discovered earlier is a separate, sociological question.

------------------------------------------------------------------------


**Remark (S/T Necessity via Scope):** An alternative proof of why T/S cannot express S/T's power derives from scope analysis of the feedback loop.

The feedback `index-cata(remaining, i)` depends on `i`, which is bound by the iteration. In T/S:

```python
for i in range(n):
    for j in range(i, n):    # j's bound depends on i
```

The expression `range(i, n)` references `i`, which is in scope only inside the loop. To extract the iteration as a first-class value, you'd need to encapsulate this scope dependency—but `i` doesn't exist until iteration begins.

S/T resolves this by making `i` internal to the loop object. The dependent bound becomes internal state, not exposed syntax. Composition operates on complete loop objects, not scope-dependent fragments.


---

## 3. Preliminaries

### 3.1 Notation

  Symbol       Meaning
  ------------ ----------------------------------------
  ℕ            Natural numbers
  T            Base types (float, int, complex, etc.)
  r, n ∈ ℕ     Ranks (dimensionality)
  σ, τ ∈ ℕ\*   Symmetry vectors
  ε, δ ∈ ℕ\*   Extent vectors
  c ∈ ℕ\*      Commutativity vectors
  A\*          Sequences of arrays

### 3.2 Arrays

An array is a tuple A = (T, r, σ, ε) where:

-   T is the element type
-   r ∈ ℕ is the rank
-   σ ∈ ℕʳ is the symmetry vector (\|σ\| = r)
-   ε ∈ ℕʳ is the extent vector (\|ε\| = r)

**Symmetry vector semantics**: σᵢ = σⱼ indicates dimensions i and j are symmetric (interchangeable). Values are local to each array---there is no global meaning across arrays.

**Examples**:

-   Dense matrix: σ = ⟨1, 2⟩ (dimensions independent)
-   Symmetric matrix: σ = ⟨1, 1⟩ (dimensions interchangeable)
-   3-tensor with partial symmetry: σ = ⟨1, 1, 2⟩ (dims 0,1 symmetric; dim 2 independent)

### 3.3 Extents

Extents (dimension sizes) are runtime values intrinsic to arrays. Users do not manage extent vectors explicitly; they are:

-   Inferred from data sources (e.g., file metadata)
-   Declared literally in array construction
-   Computed from input extents for T-dimensions

**Design principle**: Extent-passing is opaque to the user. When constructing loops, extents flow automatically from the bound arrays.

For T-dimensions, extents may be expressed as functions of input extents:

```
tdim_extent ::= literal
              | input.extent(dim)
              | tdim_extent op tdim_extent   where op ∈ {+, -, *, /}
```

**Example**: Real FFT output has extent `n/2 + 1` where `n` is the input extent.

### 3.4 Value Types and Promotion

Blade supports standard numeric value types with well-defined promotion rules.

#### 3.4.1 Base Value Types

| Type | Description | Size |
|------|-------------|------|
| `Int32` | Signed 32-bit integer | 4 bytes |
| `Int64` | Signed 64-bit integer | 8 bytes |
| `Float32` | IEEE 754 single precision | 4 bytes |
| `Float64` | IEEE 754 double precision | 8 bytes |
| `Complex64` | Complex with Float32 components | 8 bytes |
| `Complex128` | Complex with Float64 components | 16 bytes |

#### 3.4.2 Type Promotion Rules

When operations combine different value types, the result type follows standard numeric promotion:

| Type A | Type B | Result |
|--------|--------|--------|
| Int32 | Int32 | Int32 |
| Int32 | Int64 | Int64 |
| Int32 | Float32 | Float32 |
| Int32 | Float64 | Float64 |
| Int64 | Float32 | Float64 |
| Int64 | Float64 | Float64 |
| Float32 | Float32 | Float32 |
| Float32 | Float64 | Float64 |
| Float64 | Float64 | Float64 |

**Rules:**
- Float beats Int (conversion to float is implicit; reverse requires explicit cast)
- Wider beats narrower within the same category
- Complex promotion follows component type promotion

#### 3.4.3 Type Variables

In polymorphic contexts, type variables (single capital letters) represent unknown value types:

```
A, B, C, ...    -- type variables (universally quantified)
cast<A,B>       -- result type when A and B combine via promotion
```

**Scope:** Within a function signature, the same letter denotes the same type:

```blade
function add(a: A^0, b: A^0) -> A^0    // same element type required
function pair(a: A^0, b: B^0) -> (A, B) // different types allowed
function scale(s: A^0, v: B^1) -> cast<A,B>^1  // promotion applies
```

### 3.5 Array Expressions

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

```blade
let B = transpose(A, [1,0])    // A is Array, implicitly lifted; B is ArrayExpr
let C = zip(A, B)              // A lifted again; C is ArrayExpr
let D = C |> compute           // D is Array
```

**Implicit materialization**: `method_for` accepts both `Array` and `ArrayExpr`. When given `ArrayExpr`, it materializes before constructing the loop:

```blade
let B = transpose(A, [1,0])         // ArrayExpr
method_for(B, B) <@> f |> compute   // B materialized, then loop constructed
```

This ensures cache-optimal layout before iteration begins.

**Laws:**

```
pure A |> compute  ≡  A              -- round-trip identity
```

### 3.6 Array Combinators

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
-- At (i)(j): Tuple of (A(i)(j) : Float^1, B(i)(j) : Float)
```

**Tuple element type**: When `method_for` receives an array with `Tuple` element type, the kernel receives a single tuple argument that must be unpacked in the kernel body:

```blade
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

```blade
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

  -----------------------------------------------------------------------------------------------
  Combinator               Result type                     method_for behavior
  ------------------------ ------------------------------- --------------------------------------
  `zip(A, B, C)`           `ArrayExpr<Tuple(...), r, σ>`   Kernel receives 1 tuple argument

  `align(A, B, C, spec)`   `AlignedExpr`                   Kernel receives N separate arguments
  -----------------------------------------------------------------------------------------------

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

#### Array Fallback (\<\|:\>)

Provides nullptr-safe array access for sparse allocation patterns:

```
(<|:>) : ArrayExpr<T,r,σ> × ArrayExpr<T,r,σ> → ArrayExpr<T,r,σ>
```

**Semantics**: `(A <|:> B)(i)` returns `A(i)` if allocated, otherwise `B(i)`. The check occurs at each curry level during traversal.

**Ordering constraint**: The first argument's structure dominates iteration order. This preserves cache-optimality---we iterate in A's memory layout, falling back to B only for missing data.

**Symmetry note**: If A is symmetric, its allocation pattern must also be symmetric (if `A(i)(j)` is allocated, so is `A(j)(i)`). Access uses canonical (sorted) indices, ensuring consistent nullptr checks. If B lacks matching symmetry, the result may not satisfy the declared symmetry---this is user responsibility.

#### Transpose

Reorders dimensions:

```
transpose : ArrayExpr<T,r,σ> × Perm → ArrayExpr<T,r,σ'>
            where Perm is a permutation of [0..r-1]
                  σ' = permute(σ, Perm)
```

**Semantics**: `transpose(A, [1,0,2])(i)(j)(k) = A(j)(i)(k)`

Transpose produces a new array with reordered memory layout upon materialization. This is a "hard" transpose---actual data rearrangement, not a view.

#### Diagonal

Extracts elements where specified dimensions have equal indices:

```
diag : ArrayExpr<T,r,σ> × (Dim, Dim) → ArrayExpr<T,r-1,σ'>
```

**Semantics**: `diag(A, (0,1))(i)(k) = A(i)(i)(k)`

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

### 3.7 Array Combinator Laws

**Zip:**

```
zip(A) ≡ Tuple(A)                               (singleton wraps in tuple)
```

**Stack:**

```
stack(A)[0] ≡ A                                 (singleton)
stack(A, B, C)(i) ≡ [A, B, C][i]               (indexing selects array)
```

**Transpose:**

```
transpose(A, id) ≡ A                            (identity permutation)
transpose(transpose(A, p), q) ≡ transpose(A, q∘p)   (composition)
transpose(transpose(A, p), p⁻¹) ≡ A             (inverse)
```

**Join/Subset/Split:**

```blade
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

------------------------------------------------------------------------

## 4. Index Types

### 4.1 Design Principles

The index type system follows these core principles:

1. **Bounds are values, not type parameters** — enables runtime-determined shapes while maintaining type safety
2. **Index types compose** — compound and symmetric indices built from simpler ones via products, nesting, and symmetry combinators  
3. **Constraints are explicit** — symmetry structure is encoded in the index type itself, not as separate metadata
4. **Erasure to simple C++** — dependent type structure (like `BoundedIdx<i, n>`) compiles to runtime bounds checks, avoiding template explosion
5. **Currying preserved** — partial indexing works uniformly across all index types, producing appropriate dependent types

**Dimensions vs Index Types**: Dimensions (coordinate values like latitudes or timestamps) are ordinary 1D arrays. Index types determine iteration and storage structure. The association between a dimension array and the data arrays it describes is a user-level convention, not enforced by the type system. This separation keeps the core type system simple while allowing flexible metadata handling.

### 4.2 Base Index Types

  ------------------------------------------------------------------------------------------------------------------------------------
  Type                   Type Signature           Description                                   Hashable
  ---------------------- ------------------------ --------------------------------------------- --------------------------------------
  `Idx<n>`               `N`                      Contiguous integers 0..n-1                    Trivially (extent equality suffices)

  `EnumIdx<S>`           `N`                      Enumerated categories                         Yes (from set S)

  `RaggedIdx<lengths>`   `N`                      Variable extent per outer index               Yes (from lengths array)

  `CompoundIdx<mask>`    `N -> N -> ...`          Sparse combinations from k-dimensional mask   Yes (from mask)
  ------------------------------------------------------------------------------------------------------------------------------------

**Type signatures**: Most index types have signature `N` (single integer). `CompoundIdx` has signature `N -> N -> ...` matching the rank of its mask--it is internally curryable.

**Float indices are forbidden**: Floating-point values are not safely hashable due to precision issues. Coordinate values (latitudes, times, etc.) are stored as separate 1D arrays, not as index types.


#### 4.2.1 Structural Matching (Duck Typing)

Index types are structurally matched. Two index types are equal if:

1. **Extent**: Same number of elements
2. **Tag**: Same tag type and value (if tagged)
3. **Hash**: Same hash (for non-trivial index types)

This enables duck typing across files:

```blade
// Two files with same grid structure
type ERA5 = FileProvider<"era5.nc">
type MERRA = FileProvider<"merra.nc">

// If index structure matches, operations are valid
era5_temp + merra_temp  // OK if indices structurally equal
```


#### 4.2.2 Tagged Index Types

All index types may carry user-defined tags for type-level distinction:

```blade
Idx<n, Tag>
```

Tags are user-defined enum types or enum values:

```blade
enum LatPosition { Center, Left, Right }
enum LonPosition { Center, Left, Right }

// Using enum.member syntax
Idx<721, LatPosition.Center>
Idx<721, LatPosition.Left>

// Using enum values directly as tags
enum Parity { even, odd }

Idx<3, even>    // tag is the enum value 'even'
Idx<3, odd>     // tag is the enum value 'odd'
```

**Type equality requires tag equality**: `Idx<721, LatPosition.Center> != Idx<721, LatPosition.Left>` and `Idx<3, even> != Idx<3, odd>`. Untagged indices are compatible only with other untagged indices of the same extent.

### 4.3 Dependent Index Types

Certain index types depend on runtime arrays:

**Ragged indices**:

```blade
let obs_lengths: Array<Int like Idx<n_stations>> = ...  // observations per station
let readings: Array<Float like Idx<n_stations>, RaggedIdx<obs_lengths>>
```

**Compound index from mask** (for mutually-dependent sparsity):

When sparsity is mutually dependent across dimensions (e.g., only ocean points exist, not a Cartesian product of valid lats and valid lons), use a compound index:

```blade
let ocean_mask: Array<Bool like Idx<180>, Idx<360>> = ...
let ocean_temp: Array<Float like CompoundIdx<ocean_mask>, Idx<8760>>
```

Unlike simple index types with signature `N`, a `CompoundIdx` has signature `N -> N -> ...` matching the rank of its mask. This preserves currying through the compound structure.

### 4.4 Compound Index Semantics

For a k-dimensional mask, `CompoundIdx<mask>` has type `N → N → ... → N` (k arrows), preserving currying through the compound structure.

**Tuple indexing**:

```blade
let ocean_temp: Array<Float like CompoundIdx<mask>, Idx<8760>>
// where mask: Array<Bool like Idx<180>, Idx<360>>

// Full tuple index resolves compound dimension completely:
ocean_temp((lat, lon))        // → Array<Float like Idx<8760>>
ocean_temp((lat, lon))(t)     // → Float

// Partial tuple with wildcard curries through compound:
ocean_temp((lat, _))          // → Array<Float like N, Idx<8760>>  (valid lons at this lat)
ocean_temp((_, lon))          // → Array<Float like N, Idx<8760>>  (valid lats at this lon)
```

**Currying rules by resulting dimension:**

| Original | Partial index | Result type |
|----------|---------------|-------------|
| 2D `CompoundIdx` | `(lat, _)` or `(_, lon)` | `Idx<n>` (1D = regular index) |
| 3D `CompoundIdx` | `(a, _, _)` | 2D `CompoundIdx` |
| 3D `CompoundIdx` | `(a, b, _)` or `(a, _, c)` | `Idx<n>` (1D = regular index) |

**Hashing and identity**: A `CompoundIdx` is identified by a whole-mask hash. Two `CompoundIdx` types are equal iff their masks are identical. Data is stored contiguously for valid combinations only; the mask defines which combinations exist and provides the bijection between logical coordinates and storage offsets.

**Partial indexing cost**: When currying with wildcards (e.g., `ocean_temp((lat, _))`), the result index must be reconstituted:

1. Scan all valid `(lat, lon)` pairs in the mask where the first component equals `lat`
2. For each valid `lon`, compute `hash(lat, lon)` to map into the new index
3. Build the resulting `Idx<n>` where `n` = count of valid lons at this latitude

This is **O(n)** where n = number of valid combinations in the mask. The cost is unavoidable—we must enumerate which positions remain valid after fixing some coordinates. The resulting curried index identity derives from (original mask hash, fixed coordinate values).

### 4.5 Sparse Index Types

`SparseIdx` represents indices where only a subset of combinations are valid. Unlike `CompoundIdx` which derives validity from a mask array, `SparseIdx` explicitly enumerates valid entries.

#### 4.5.1 Declaration

```blade
// Sparse index over explicitly listed entries
type SparseIdx<entries> where entries : Array<(I₁, I₂, ..., Iₙ)>

// Example: valid Clebsch-Gordan triples
static cg_entries = [(−1, 0, −1), (−1, 1, 0), (0, −1, −1), (0, 0, 0), ...]
type CGIdx = SparseIdx<cg_entries>
```

#### 4.5.2 Storage Semantics

Sparse indices initialize as empty hash tables and expand on indexing:

```blade
let A: Array<Float like SparseIdx<entries>>

// First access at valid entry: allocates storage
A((i, j, k)) = 1.0    // creates entry if (i,j,k) in entries

// Access at invalid entry: compile-time or runtime error
A((i, j, k))          // error if (i,j,k) not in entries
```

**Indexing rules:**

- Tuple indexing required: `A((i, j, k))` not `A(i)(j)(k)`
- All indices must be present (no partial indexing without wildcards)
- Wildcards allowed: `A((i, _, k))` returns all entries matching pattern

#### 4.5.3 Iteration

Iteration visits only valid entries:

```blade
method_for(A : Array<T like SparseIdx<entries>>) <@> f
// Iterates over entries, not the full Cartesian product
```

The iteration count is `|entries|`, not `|I₁| × |I₂| × ... × |Iₙ|`.

#### 4.5.4 Applications

**Clebsch-Gordan coefficients**: Only entries where m₁ + m₂ = m_out are nonzero:

```blade
static function valid_cg(L1, L2, L_out) : Array<(Int, Int, Int)> =
    // Returns all (m1, m2, m_out) where m1 + m2 = m_out
    // and |m1| ≤ L1, |m2| ≤ L2, |m_out| ≤ L_out
    ...

type CGIdx<L1, L2, L_out> = SparseIdx<valid_cg(L1, L2, L_out)>
```

**Graph adjacency**: Only existing edges:

```blade
static edges = [(0, 1), (0, 2), (1, 2), (2, 3), ...]
type EdgeIdx = SparseIdx<edges>
let weights: Array<Float like EdgeIdx>
```

### 4.6 Generalized Dependent Index Types

`DepIdx<I, f>` generalizes dependent indexing where the inner index type depends on the outer index value:

```blade
type DepIdx<I : IndexType, f : I -> IndexType> : IndexType

// Iteration yields (i, j) pairs where j : f(i)
// Total extent is sum of |f(i)| for all i in I
```

#### 4.6.1 Definition

```blade
type DepIdx<I, f> where
    I : IndexType
    f : (i : I) -> IndexType
    
    // Extent is sum of inner extents
    static function extent() : Nat =
        sum(method_for(range<I>) <@> lambda(i) -> extent(f(i)))
    
    // Bijection to flat storage
    static function to_offset(i : I, j : f(i)) : Nat = ...
    static function from_offset(n : Nat) : (I, f(n)) = ...
```

#### 4.6.2 Examples

**Ragged arrays** (variable-length inner dimension):

```blade
let lengths: Array<Int like Idx<n>> = [3, 1, 4, 1, 5]
type RaggedIdx = DepIdx<Idx<n>, lambda(i) -> Idx<lengths(i)>>

let A: Array<Float like RaggedIdx>
// A has 3+1+4+1+5 = 14 elements total
```

**Triangular matrices** (inner bound depends on outer):

```blade
type TriIdx<n> = DepIdx<Idx<n>, lambda(i) -> Idx<n - i>>
// Equivalent to SymIdx<n> but explicit
```

**Block-structured irreps** (ML applications):

```blade
static spec = [(L0e, 16), (L1o, 8), (L2e, 4)]

type IrrepsIdx<spec> = DepIdx<
    Idx<length(spec)>,
    lambda(b) -> Idx<mult(spec(b))> * Idx<dim(irrep(spec(b)))>
>
```


### 4.7 Index Transforms

All structural transforms are explicit:

  Transform                 Effect
  ------------------------- --------------------------------------
  `flip(A, dim)`            Reverse ordering (changes hash)
  `rename(A, old -> new)`   Change tag
  `subset(A, dim=lo..hi)`   Extract range (new extent, new hash)
  `align(A, B, dim)`        Join arrays on common indices

No implicit conversions occur. Mismatched indices produce type errors.

### 4.8 Files as Type Providers

File metadata provides index types at compile time:

```
type ERA5 = NetCDFProvider<"era5.nc">
// ERA5.lat_idx : Idx<721>
// ERA5.lon_idx : Idx<1440>
// ERA5.time_idx : Idx<8760>
// ERA5.t2m : Array<Float like Idx<721>, Idx<1440>, Idx<8760>>
```

The compile step reads file metadata to instantiate types. Runtime reads actual data values. This works because:

1.  File structure is quasi-static (does not change during computation)
2.  Metadata inspection is cheap
3.  The structure (not values) determines types

### 4.9 Symmetry and Index Types

Symmetry is encoded directly in index types rather than as separate metadata. A symmetric matrix uses `SymIdx<n>` rather than two `Idx<n>` with a symmetry annotation:

``` blade
// Symmetric matrix — single symmetric index type
let cov: Array<Float like SymIdx<1000>>

// Non-symmetric matrix — two independent index types
let matrix: Array<Float like Idx<1000>, Idx<1000>>
```

The index type itself determines storage layout and iteration pattern:

  -------------------------------------------------------------------------------------------------
  Declaration                       Storage        Iteration          Access
  --------------------------------- -------------- ------------------ ---------------------------
  `Array<T like SymIdx<n>>`         Triangular     Triangular         `cov(i, j)` canonicalizes

  `Array<T like Idx<n>, Idx<n>>`    Rectangular    Full               `matrix(i, j)` direct
  -------------------------------------------------------------------------------------------------

**Indexing semantics**: For `SymIdx`-indexed arrays, `cov(3, 1)` and `cov(1, 3)` access the same storage location. The index type handles canonicalization transparently.

**Currying**: Symmetric index types curry to dependent bounded types (see §4.11):

``` blade
let cov: Array<Float like SymIdx<1000>>
cov(i)    // : Array<Float like BoundedIdx<i, 1000>>  — slice of length (1000 - i)
```

**Inference**: The symmetry system (§13) infers appropriate symmetric index types from kernel commutativity and array identity. When `method_for(A, A)` is applied with a commutative kernel, the output type is `SymIdx<n>` rather than `(Idx<n>, Idx<n>)`.

### 4.10 Currying by Index

Arrays curry by index type only:

```blade
let A: Array<Float like Idx<100>, Idx<200>, Idx<300>>

A(i)       // Array<Float like Idx<200>, Idx<300>>
A(i)(j)    // Array<Float like Idx<300>>
A(i)(j)(k) // Float
```

Index arithmetic works for contiguous integer indices:

```
A(i + 1)   // valid for Idx<n>
```

### 4.11 Declaration Syntax

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

```blade
// Arrays with index structure
let dense: Array<Float like LatIdx, LonIdx, TimeIdx>

// Symmetric arrays use SymIdx<r, n> for rank-r symmetry over n elements
let cov: Array<Float like SymIdx<2, 1000>>        // symmetric matrix
let coskew: Array<Float like SymIdx<3, 1000>>     // fully symmetric 3-tensor

// Block symmetry: symmetric in first two AND last two indices
let block: Array<Float like SymIdx<2, I>, SymIdx<2, K>>

// Partial symmetry: symmetric in first two, dense in third
let partial: Array<Float like SymIdx<2, I>, K>

// Dense matrix (no symmetry, uses regular Idx)
let matrix: Array<Float like Idx<n>, Idx<n>>
```

**Symmetric index types:**

| Type | Storage | Meaning |
|------|---------|---------|
| `Idx<n>, Idx<n>` | n² | Dense, no symmetry |
| `SymIdx<2, n>` | n(n+1)/2 | Symmetric pairs |
| `SymIdx<3, n>` | n(n+1)(n+2)/6 | Symmetric triples |
| `AntisymIdx<2, n>` | n(n-1)/2 | Antisymmetric pairs |

**Notes:** - File-derived arrays get types from type providers (deferred) - `type` for type aliases and definitions - `let` for value bindings

### 4.12 Bounded Index Types

The `BoundedIdx` type generalizes `Idx` to support dependent bounds, particularly for currying symmetric indices:

``` blade
// Index with lower and upper bounds
type BoundedIdx<lower, upper>

// Constraint: lower ≤ val < upper
make_bounded : (l : Nat, u : Nat, v : Nat) → BoundedIdx<l, u>  // requires l ≤ v < u

// Idx<n> is sugar for BoundedIdx<0, n>
Idx<n> ≡ BoundedIdx<0, n>

// Iteration
range_bounded<l, u> : Iterator<BoundedIdx<l, u>>
// Generates: l, l+1, ..., u-1
```

**Dependent bounds**: When currying symmetric indices, the lower bound depends on the curried index value:

``` blade
let S: Array<Float like SymIdx<n>>
S(i)    // : Array<Float like BoundedIdx<i, n>>  — lower bound is i, upper bound is n
```

**Erasure to C++**: The dependent structure compiles to runtime bounds, not compile-time type parameters:

```
// Blade: S(i) has type Array<Float like BoundedIdx<i, n>>
// C++: pointer + offset, with runtime bounds check (i <= j < n)
```

This avoids template metaprogramming explosion while preserving type safety at the Blade level.

### 4.13 Symmetric Index Types

Symmetric index types encode symmetry directly in the index structure, with explicit rank parameters.

#### 4.13.1 SymIdx: Sorted Index Tuples

`SymIdx<r, n>` represents r indices from 0..n-1, sorted in non-decreasing order:

```blade
// SymIdx<r, n>: r indices satisfying i₁ ≤ i₂ ≤ ... ≤ iᵣ
type SymIdx<r, n> : IndexType where
    rank = r
    base_extent = n
    cardinality = C(n + r - 1, r)  // multiset coefficient
```

**Projection and construction:**

```blade
// Access k-th component (0-indexed)
component : (idx : SymIdx<r, n>, k : Idx<r>) -> Idx<n>
// Invariant: component(idx, k) ≤ component(idx, k+1)

// Construction (automatically sorts)
make_sym : (i₁, ..., iᵣ : Idx<n>) -> SymIdx<r, n>
// make_sym(3, 1, 2) = make_sym(1, 2, 3)
```

**Examples by rank:**

```blade
SymIdx<2, n>    // Symmetric pairs: (i, j) where i ≤ j
                // Cardinality: n(n+1)/2

SymIdx<3, n>    // Symmetric triples: (i, j, k) where i ≤ j ≤ k  
                // Cardinality: C(n+2, 3) = n(n+1)(n+2)/6

SymIdx<4, n>    // Cokurtosis indices
                // Cardinality: C(n+3, 4)
```

**Isomorphism (not definition):** `SymIdx<2, n>` is isomorphic to `DepIdx<Idx<n>, λi. BoundedIdx<i, n>>`, but `SymIdx<r, n>` for r > 2 cannot be cleanly expressed as nested `DepIdx` due to scope issues with the recursive bound dependencies.

**Currying:** Currying a `SymIdx`-indexed array yields a lower-rank symmetric index:

```blade
let S: Array<Float like SymIdx<3, n>>
S(i)       // : Array<Float like SymIdx<2, n-i>>  — remaining pairs with j,k ≥ i
S(i)(j)    // : Array<Float like BoundedIdx<j, n>>
S(i)(j)(k) // : Float
```

#### 4.13.2 AntisymIdx: Strictly Ordered Index Tuples

`AntisymIdx<r, n>` represents r indices in strictly increasing order:

```blade
// AntisymIdx<r, n>: r indices satisfying i₁ < i₂ < ... < iᵣ
type AntisymIdx<r, n> : IndexType where
    rank = r
    base_extent = n
    cardinality = C(n, r)  // binomial coefficient (no repetition)
```

**Sign tracking:** Antisymmetric tensors negate under odd permutations. Construction tracks whether a swap occurred:

```blade
make_antisym : (i₁, ..., iᵣ : Idx<n>) -> (AntisymIdx<r, n>, sign : {+1, -1})
// Sorts indices and returns parity of the permutation

was_swapped : AntisymIdx<r, n> -> Bool  // for r=2 convenience
```

**Examples:**

```blade
AntisymIdx<2, n>    // Antisymmetric pairs: (i, j) where i < j
                    // Cardinality: n(n-1)/2 (no diagonal)

AntisymIdx<3, n>    // Antisymmetric triples (e.g., Levi-Civita)
                    // Cardinality: C(n, 3)
```

#### 4.13.3 HermitianIdx: Complex Symmetric

`HermitianIdx<n>` represents indices for Hermitian matrices where A[i,j] = conj(A[j,i]):

```blade
type HermitianIdx<n> : IndexType where
    rank = 2
    cardinality = n²  // stores full matrix, but with conjugate symmetry
    
    // Upper triangle stored directly, lower triangle derived
    canonical : (i : Idx<n>, j : Idx<n>) -> (HermitianIdx<n>, needs_conj : Bool)
```

#### 4.13.4 Summary

| Type | Constraint | Cardinality | Use case |
|------|------------|-------------|----------|
| `SymIdx<r, n>` | i₁ ≤ i₂ ≤ ... ≤ iᵣ | C(n+r-1, r) | Covariance, comoments |
| `AntisymIdx<r, n>` | i₁ < i₂ < ... < iᵣ | C(n, r) | Determinants, forms |
| `HermitianIdx<n>` | A[i,j] = conj(A[j,i]) | n² | Quantum mechanics |



### 4.14 Nested and Mixed Symmetry

#### 4.12.1 Product Index

Independent indices with no symmetry relationship:

``` blade
// Independent indices (no symmetry across)
type ProdIdx<I, J> ≅ (I, J)

// Sugar for tuples of index types
(Idx<n>, Idx<m>) ≡ ProdIdx<Idx<n>, Idx<m>>

// Currying example
let A: Array<Float like Idx<n>, Idx<m>>
A(i)    // : Array<Float like Idx<m>>
A(i)(j) // : Float
```

#### 4.12.2 Nested Symmetric (Wreath Product)

Symmetric structure at multiple levels --- symmetric pairs of symmetric pairs:

``` blade
// Symmetric over symmetric: S₂ ≀ S₂ (wreath product)
// Two pairs (i,j) and (k,l), each internally symmetric, pairs unordered
type NestedSymIdx<n>

// NestedSymIdx<n> ≅ SymIdx<|SymIdx<n>|>
//                 ≅ Σ(p1 : SymIdx<n>). Σ(p2 : SymIdx<n>). (p1 ≤ p2)
// Where p1 ≤ p2 is lexicographic on (fst, snd)

// Cardinality: S×(S+1)/2 where S = n×(n+1)/2
|NestedSymIdx<n>| = (n×(n+1)/2) × (n×(n+1)/2 + 1) / 2

// Projection to four components
components : NestedSymIdx<n> → (Idx<n>, Idx<n>, Idx<n>, Idx<n>)
// Returns (i, j, k, l) with i≤j, k≤l, (i,j) ≤lex (k,l)
```

**Application**: Fourth-order elasticity tensors with major and minor symmetries.

#### 4.12.3 Mixed Symmetry (Riemann Tensor)

``` blade
// Riemann tensor: antisym(0,1), antisym(2,3), sym((0,1),(2,3))
type RiemannIdx<n>

// RiemannIdx<n> ≅ Σ(p1 : AntisymIdx<n>). Σ(p2 : AntisymIdx<n>). (p1 ≤ p2)

// Cardinality: A×(A+1)/2 where A = n×(n-1)/2
|RiemannIdx<n>| = (n×(n-1)/2) × (n×(n-1)/2 + 1) / 2
// For n=4: 6 × 7 / 2 = 21 (not 256)
```

#### 4.12.4 Equivariant Index Types (Foundation)

For machine learning applications requiring equivariance under geometric transformations, index types can carry group representation annotations:

``` blade
// Index type with equivariance annotation
type EquivIdx<n, G, ρ>
// n: dimension
// G: symmetry group (SO<3>, SE<3>, S<n>, etc.)
// ρ: representation of G

// Example: 3D spatial coordinates (transform as vectors under rotation)
type VectorIdx = EquivIdx<3, SO<3>, standard>

// Example: 3D pseudovectors (cross products, angular momentum)
type PseudovectorIdx = EquivIdx<3, SO<3>, adjoint>

// Invariant scalar (unchanged under group action)
type ScalarIdx = EquivIdx<1, SO<3>, trivial>
```

**Array declarations with equivariance**:

``` blade
// Molecular positions: N atoms × 3D coordinates
let positions: Array<Float like Idx<N>, VectorIdx>

// Per-atom features (invariant under rotation)
let features: Array<Float like Idx<N>, Idx<F>>

// Pairwise distances (invariant scalar output from equivariant inputs)
let distances: Array<Float like SymIdx<N>>
```

**Equivariance composition**: When combining equivariant indices, the representation algebra determines the output:

``` blade
// Vector ⊗ Vector → Scalar (dot product) + Antisym (cross product) + Sym (outer product)
// The type system tracks which representation the output carries
```

**Scope**: This section defines the index type foundations for equivariant computation. The full equivariance verification system---including group declarations, representation constructors, automatic equivariance checking, and integration with kernels---is specified in §8 (Equivariance System).

### 4.15 User-Defined Index Types

#### 4.15.1 Three-Tier System

  ---------------------------------------------------------------------------------------------------------------------------------
  Tier                Capability                                          Safety                   Use Case
  ------------------- --------------------------------------------------- ------------------------ --------------------------------
  **Built-in**        `Idx`, `SymIdx`, `AntisymIdx`, `FullSymIdx`, ...    Fully verified           Common symmetry patterns

  **Compositional**   Products, nesting with `Sym<I,I>`, `Antisym<I,I>`   Derived from built-ins   Complex but regular structures

  **Unsafe**          Custom `canonical` + `transform` functions          User responsibility      Exotic symmetries
  ---------------------------------------------------------------------------------------------------------------------------------

#### 4.15.2 Compositional Building Blocks

Rather than arbitrary user-defined index types, most complex symmetries can be expressed compositionally:

``` blade
// Users compose built-in types
type MyIndex = (SymIdx<n>, Idx<m>)                    // product
type Nested = Sym<SymIdx<n>, SymIdx<n>>               // symmetric pair of symmetric pairs
type Mixed = (AntisymIdx<n>, AntisymIdx<m>) with Sym  // outer symmetry over antisymmetric

// Nesting combinators
Sym<I, I>      // symmetric pair of I-indexed structures
Antisym<I, I>  // antisymmetric pair
```

The compiler knows how to:

- Compute storage size for compositions
- Generate iteration patterns
- Optimize access patterns

#### 4.15.3 Unsafe Escape Hatch

For truly exotic symmetries not expressible compositionally:

``` blade
unsafe indextype NAME<params> {
    components = NAT
    extent = EXPR
    
```
// Returns Option<CanonicalIndices>
// None means implicit zero (not stored)
canonical(indices...) = EXPR

// Applied when accessing via non-canonical indices
// Default: identity
transform<T>(val : T, ...) -> T = EXPR
```
}
```

**Key insight**: `canonical` returns `Option`. `None` means "not stored, implicitly zero" --- this elegantly handles antisymmetric diagonals and other sparse patterns.

**Example --- Symmetric Index**:

``` blade
unsafe indextype SymIdx<n : Nat> {
    components = 2
    extent = (n, n)
    
```
canonical(i, j) = Some(min(i, j), max(i, j))
transform<T>(val : T, swapped : Bool) = val  // symmetric: no change on swap
```
}
```

**Example --- Antisymmetric Index**:

``` blade
unsafe indextype AntisymIdx<n : Nat> {
    components = 2
    extent = (n, n)
    
```
// Diagonal not stored — returns None (implicit zero)
canonical(i, j) =
    if i == j then None
    else Some(min(i, j), max(i, j))

// Negate on swap
transform<T : Signed>(val : T, swapped : Bool) =
    if swapped then -val else val
```
}
```

Access semantics: - `A(2, 5)` → `canonical(2, 5) = Some(2, 5)` → fetch `storage[to_flat(2, 5)]` - `A(5, 2)` → `canonical(5, 2) = Some(2, 5)` with swap → fetch and negate - `A(3, 3)` → `canonical(3, 3) = None` → return zero

#### 4.15.4 Recommendation

Use compositional building blocks for most cases. Reserve `unsafe indextype` for genuinely novel symmetry structures that cannot be expressed through composition of built-in types.

### 4.16 Index Type Summary

  -----------------------------------------------------------------------------------------------------
  Type                     Components           Cardinality            Use Case
  ------------------------ -------------------- ---------------------- --------------------------------
  `Idx<n>`                 1                    n                      Dense dimension

  `SymIdx<n>`              2                    n(n+1)/2               Symmetric matrices

  `AntisymIdx<n>`          2                    n(n-1)/2               Antisymmetric matrices

  `HermitianIdx<n>`        2                    n²                     Hermitian matrices (complex)

  `FullSymIdx<k, n>`       k                    C(n+k-1, k)            Higher-order symmetric tensors

  `FullAntisymIdx<k, n>`   k                    C(n, k)                Exterior algebra

  `NestedSymIdx<n>`        4                    S(S+1)/2               Nested symmetry (elasticity)

  `RiemannIdx<n>`          4                    A(A+1)/2               Mixed symmetry (curvature)

  `BoundedIdx<l, u>`       1                    u - l                  Dependent slices (see below)
  -----------------------------------------------------------------------------------------------------

**Note on BoundedIdx**: `BoundedIdx<l, u>` is not directly declared by users. It arises from partial indexing of symmetric index types:

```blade
let S: Array<Float like SymIdx<n>>
S(i)    // : Array<Float like BoundedIdx<i, n>>
```

The bounds `l` and `u` are dependent on the curried index value, providing type-safe triangular slicing.

**Note on HermitianIdx**: For complex-valued arrays, `HermitianIdx<n>` stores full n² elements but enforces the Hermitian constraint A(i,j) = conj(A(j,i)). Unlike `SymIdx` which reduces storage, `HermitianIdx` maintains full storage with conjugation semantics.

All symmetric index types share the property that they:

1. Accept multiple coordinate values
2. Canonicalize to a unique representative
3. Track any necessary transformation (e.g., sign for antisymmetric, conjugation for Hermitian)
4. Map to appropriate storage layout
5. Curry to dependent `BoundedIdx` types that erase to runtime bounds

## 5. Array Types

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
T : BaseType    r ∈ ℕ    σ ∈ ℕʳ
─────────────────────────────────
       T^r(σ) : ArrayType
```

### 5.1 Abstract vs Concrete Array Types

Blade distinguishes two levels of array typing:

**Abstract types: `T^r(σ)`**

Used in function signatures, typing rules, and arity-polymorphic contexts. Specifies element type, rank, and symmetry without committing to specific index types:

``` blade
function sum(a: T^r) -> T^0              // any rank, return scalar
function symmetric_op(a: T^r(σ)) -> ...  // with symmetry annotation
k : (τ^r → τ)                            // arity-polymorphic kernel
```

In arity-polymorphic typing, `r` is a type-level variable. The kernel type `(τ^r → τ)` means "takes r arguments of type τ, returns τ"---where r is determined by how many arrays are supplied to `method_for`.

The symmetry vector σ describes *which positions are interchangeable* without specifying storage representation. For example, σ = (1, 1) means positions 0 and 1 are symmetric; σ = (1, 1, 2, 2) means positions 0-1 are symmetric with each other, and positions 2-3 are symmetric with each other.

**Concrete types: `Array<T like I₁, ..., Iₙ>`**

Used for data declarations. Symmetry is encoded directly in index types (see §4.16):

``` blade
// Non-symmetric matrix — two independent indices
let matrix: Array<Float like Idx<1000>, Idx<1000>>

// Symmetric matrix — single symmetric index type
let cov: Array<Float like SymIdx<1000>>

// Coskewness tensor — fully symmetric rank-3
let coskew: Array<Float like FullSymIdx<3, 1000>>

// Mixed: symmetric in spatial dims, independent time
let spacetime: Array<Float like SymIdx<100>, Idx<8760>>
```

**The Three-Level Type Model**:

Blade array types exist at three levels of abstraction:

  ---------------------------------------------------------------------------------------------------------------------
  Level                Form                  What's Known                       Use Case
  -------------------- --------------------- ---------------------------------- ---------------------------------------
  **Fully abstract**   `T^r(σ)`              Rank, symmetry class               Arity-polymorphic kernel signatures

  **Index-typed**      `T^(I₁, I₂, ...)`     Index types (structure)            Kernel bodies, combinators, most code

  **Fully concrete**   `Array<V like I₁, ...>`   Value type, index types, extents   Data declarations, runtime
  ---------------------------------------------------------------------------------------------------------------------

Examples at each level:

``` blade
// Fully abstract — rank and symmetry only
k : (T^r → T) where comm              // arity-polymorphic kernel signature
f : T^2(1,1) → T^0                    // symmetric matrix to scalar

// Index-typed — structure known, value type polymorphic
T^SymIdx<n>                           // symmetric matrix
T^(Idx<N>, EquivIdx<3, SO<3>, std>)   // N points in 3D (equivariant)
T^(SymIdx<n>, Idx<t>)                 // symmetric spatial, independent time

// Fully concrete — everything resolved
Array<Float like SymIdx<1000>>            // actual symmetric matrix of floats
Array<Complex like AntisymIdx<100>>       // antisymmetric matrix of complex
```

**Transitions between levels**:

```
Fully abstract          Index-typed              Fully concrete
    T^r(σ)        →      T^(I₁,...)        →     Array<V like I₁,...>
                  ↑                        ↑
            symmetry              value type
            inference             instantiation
            (§13)
```

The symmetry system (§13) infers index types from kernel commutativity and array identity. Value type instantiation occurs at data declaration or when concrete operations require specific numeric types.

**Lowering abstract to index-typed**: The symmetry system selects appropriate symmetric index types:

  Abstract         Index-typed
  ---------------- ----------------------------
  `T^2(1,1)`       `T^SymIdx<n>`
  `T^2(1,2)`       `T^(Idx<n>, Idx<m>)`
  `T^3(1,1,1)`     `T^FullSymIdx<3,n>`
  `T^4(1,1,2,2)`   `T^(SymIdx<n>, SymIdx<m>)`

**Relationship**: The typing judgment for `method_for`:

```blade
Γ ⊢ A₁ : Array<τ, I>  ...  Γ ⊢ Aᵣ : Array<τ, I>
Γ ⊢ k : (τ^r → τ)
───────────────────────────────────────────────
Γ ⊢ method_for(A₁,...,Aᵣ) <@> k : Comp<τ^r(σ)>
```

Here concrete input types determine `r` by counting, and the abstract output type `τ^r(σ)` captures symmetry derived from kernel commutativity and array identity. This abstract type is then lowered to an index-typed form with appropriate symmetric index types.

**Extents are values, not types**: Index types like `Idx<n>` reference extent n, but extents are runtime values. The type system tracks *structure* (rank, symmetry, index type categories) while extents flow through value-level computation.


### 5.2 Array Type Identity

Multi-indexed arrays are definitionally equivalent to nested arrays. This identity is fundamental to dimensional currying.

**Type Identity:**

```blade
Array<T like I₁, I₂, ..., Iₙ>  ≡  Array<Array<T like I₁, ..., Iₙ₋₁> like Iₙ>
```

Or equivalently, building from the inside out:

```blade
Array<T like I₁, I₂>       ≡  Array<Array<T like I₁> like I₂>
Array<T like I₁, I₂, I₃>   ≡  Array<Array<Array<T like I₁> like I₂> like I₃>
```

**Consequences:**

1. **Currying is projection:** `A(i)` where `A : Array<T like I₁, I₂>` yields `Array<T like I₁>`, the inner array at position `i`.

2. **Rank is nesting depth:** A rank-r array is exactly r levels of nesting.

3. **Storage is flattened nesting:** The physical layout of `Array<T like I₁, I₂, I₃>` is the same as `Array<Array<Array<T>>>` laid out contiguously.

**With symmetric indices:**

```blade
Array<T like SymIdx<2, n>>  ≢  Array<Array<T like Idx<n>> like Idx<n>>
```

Symmetric index types are *not* equivalent to nested arrays of base indices—they represent a triangular subset. However, currying still works:

```blade
let S: Array<T like SymIdx<2, n>>
S(i)  // : Array<T like BoundedIdx<i, n>>  — NOT Array<T like Idx<n>>
```

The curried result has a dependent index type reflecting the triangular structure.


### 5.3 Arrays as Functions

An array `A : Array<T like I₁, I₂, ..., Iₙ>` is semantically a function:

```
A : I₁ → I₂ → ... → Iₙ → T
```

Indexing is function application, using `()` syntax to emphasize that arrays *are* functions:

```
A(i)       = A applied to i           : I₂ → ... → Iₙ → T
A(i)(j)    = (A applied to i) to j    : I₃ → ... → Iₙ → T
A(i)(j)(k) = ...                      : T
```

**Key insight:** The array doesn't care *how* you compute the index---only *what* value it resolves to. Any expression producing a valid index is a valid index:

```blade
A(42)                     -- literal
A(i + 1)                  -- arithmetic
A(f(x))                   -- function result
A(if c then i else j)     -- conditional
A(stencil_offset(k))      -- computed offset
```

**Compound indices** are tuples applied as a single unit:

```
B(lat, lon)               -- compound index (single application)
B(lat, lon)(time)         -- compound + scalar (two applications)
```

**Partial compound indexing** uses wildcards:

```
B(lat, _)                 -- view over all lon at this lat
B(_, lon)                 -- view over all lat at this lon
```

Index types define valid addresses---the *domain* of the array-as-function---not how addresses are computed.

**Intermixing indexing and function application:**

Since arrays are functions and indexing is application, array indexing and function application can be freely intermixed in a single expression. Consider a 2D array of parameterized models:

```blade
// Array of land model functions, indexed by location
let models: Array<(Params → TimeSeries) like LatIdx, LonIdx>

// Apply: index by location, then apply parameters, then index time
models(lat, lon)(land_model_params)(time)
```

The type progression:

```
models                           : LatIdx → LonIdx → (Params → TimeSeries)
models(lat, lon)                 : Params → TimeSeries
models(lat, lon)(params)         : TimeSeries
models(lat, lon)(params)(time)   : Float
```

Each `()` is application---whether to an array (indexing) or a function (calling). The syntax makes no distinction because semantically there is none: both are applying a function-like thing to an argument.

**Further examples:**

```blade
// Array of interpolators
let interp: Array<(Float → Float) like GridIdx>
interp(grid_cell)(query_point)           : Float

// Array of stencil functions  
let stencils: Array<(Neighborhood → Float) like LatIdx, LonIdx>
stencils(lat, lon)(neighbors)            : Float

// Nested: array of arrays of functions
let nested: Array<Array<(X → Y) like J> like I>
nested(i)(j)(x)                          : Y
```

This uniformity is a consequence of treating arrays as first-class functions. The `()` syntax reinforces that indexing *is* application, not a separate operation.

### 5.4 Poly-Indexing

Standard indexing uses named indices applied sequentially:

```
A(i)(j)(k)  -- three curried applications
```

**Poly-indexing** uses a variable-length tuple of anonymous indices:

```
A(indices)  -- indices : Tuple(Idx...), length = rank(A)
```

Note: Poly-indexing with tuples should only be used with index types that naturally take tuples (e.g., `CompoundIdx`). For standard arrays, use `all_indices(A)` iteration to preserve cache-optimal access patterns.

This parallels arity polymorphism for arrays:

  Arity Polymorphism                   Poly-Indexing
  ------------------------------------ ----------------------------------
  Variable number of arrays            Variable number of indices
  `args[k]` accesses k-th array        `indices[k]` accesses k-th index
  Kernel doesn't name arrays           Indexing doesn't name indices
  `poly(args)` declares polymorphism   Rank determines index count

**Example: Rank-polymorphic trace**

```blade
function trace(A)
{
    let out = 0
    for i in 0..extent(A, 0) {
        let indices = replicate(i, rank(A))  -- (i, i, i, ...) 
        out = out + A(indices)
    }
    out
}

trace(matrix)    -- sum of M[i,i]
trace(tensor3)   -- sum of T[i,i,i]
trace(tensor4)   -- sum of T[i,i,i,i]
```

**Example: Rank-polymorphic iteration**

```blade
function sum_all(A)
{
    let out = 0
    for indices in all_indices(A) {
        out = out + A(indices)
    }
    out
}
```

The `all_indices(A)` iterator generates all valid index tuples for array A, respecting its structure (dense, ragged, symmetric, etc.).


### 5.5 Lambda Indices

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

```blade
let A: Array<Float like Idx<100>>          // structural type

A(42)                                // plain Int
A(Dual(42, 1.0))                     // carries tangent for AD
A(Symbolic("i"))                     // unevaluated, resolves later
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

---

## 6. Functions

### 6.1 Function Signatures

A function f has signature:

```
f : (T₁^r₁, T₂^r₂, ..., Tₙ^rₙ) → T_out^r_out
```

with associated metadata:

-   **Commutativity vector** c ∈ ℕⁿ: cᵢ = cⱼ means arguments i and j are interchangeable
-   **Parallelism specification** p : Map(ArgName → ℕ): For each argument, the number of its S-dimension loops to parallelize. Since arrays are bound in order, their S-dimensions nest in order; typically the first array's loops are outermost and most beneficial to parallelize. The `omp` clause could be substituted with other parallel backends (e.g., `acc` for OpenACC, `cuda` for CUDA).
-   **T-dimension specification** (if r_out \> 0 and output dimensions don't derive from inputs): Each T-dimension is specified with its extent, symmetry class, and optional name.

### 6.2 Function Syntax

```blade
function name(
    x₁: T₁^r₁,
    x₂: T₂^r₂,
    ...
    xₙ: Tₙ^rₙ
)
where
    comm(xᵢ, xⱼ, ...),       // commutativity groups
    omp(xₗ: depth, ...),     // parallelism: depth levels per argument
    tdim(                     // T-dimension specification (optional)
        { extent: expr, symm: k, name: "freq" },
        { extent: expr, symm: k, name: "mode" },
        ...
    )
-> T_out^r_out               // optional return type annotation
{
    // kernel body
    expr                      // final expression is the return value
}
```

**Return value**: The final expression in the body is the return value. The optional `-> Type` annotation is checked against the inferred type.

### 6.3 Commutativity Groups

Given a commutativity specification `comm(x₁, x₂, ..., xₘ)`, we construct the commutativity vector c as:

```
cᵢ = cⱼ  iff  xᵢ and xⱼ appear in the same comm() clause
```

Arguments not appearing in any comm() clause are in singleton groups.

**Example**:

```
fn f(a, b, c, d) where comm(a, b, c)
```

yields c = ⟨1, 1, 1, 4⟩.

### 6.4 Reynolds Operators

The `where reynolds(S)` annotation declares that a kernel has Reynolds structure—it computes a symmetric sum over permutations of array arguments.

#### 6.4.1 Definition

From invariant theory, the Reynolds operator averages over a group action:

```
R(f)(A₁, ..., Aₙ)[i₁, ..., iₙ] = ⊕_{σ ∈ Sₙ} f(A_{σ(1)}[i₁], ..., A_{σ(n)}[iₙ])
```

where ⊕ is a symmetric aggregation (sum, product, max, etc.).

**Key property**: Reynolds kernels produce index-symmetric output even when arrays are distinct. The symmetry comes from kernel structure, not array identity.

#### 6.4.2 Syntax

```blade
let K = lambda(A, B) -> lambda(i, j) ->
    f(A[i], B[j]) + f(B[i], A[j])
where reynolds([A, B])
```

The argument lists which array parameters participate in the symmetry.

#### 6.4.3 Symmetric vs Antisymmetric

**Symmetric** (default): Sum over permutations.

```blade
let sym_K = lambda(A, B) -> lambda(i, j) ->
    f(A[i], B[j]) + f(B[i], A[j])
where reynolds([A, B])
// sym_K[i,j] = sym_K[j,i]
```

**Antisymmetric**: Alternating sum (signed by permutation parity).

```blade
let antisym_K = lambda(A, B) -> lambda(i, j) ->
    f(A[i], B[j]) - f(B[i], A[j])
where reynolds([A, B], Antisymmetric)
// antisym_K[i,j] = -antisym_K[j,i], antisym_K[i,i] = 0
```

#### 6.4.4 Interaction with Identity Commutativity

When a Reynolds kernel is called with identical arrays, both optimizations combine:

| Source | Effect | Speedup |
|--------|--------|---------|
| Reynolds alone | Triangular iteration | n!× |
| Identity alone | Term collapse | n!× |
| Both | Triangular + collapse | (n!)²× |

```blade
let K = lambda(A, B) -> lambda(i, j) ->
    f(A[i], B[j]) + f(B[i], A[j])
where reynolds([A, B])

K(X, Y)  // Reynolds: 2× (triangular iteration)
K(X, X)  // Reynolds + Identity: 4× (triangular + term collapse)
```

#### 6.4.5 Partial Symmetry

Only some arrays may participate:

```blade
let K = lambda(A, B, C) -> lambda(i, j, k) ->
    g(A[i], B[j], C[k]) + g(B[i], A[j], C[k])
where reynolds([A, B])  // C does not participate
```

Output is symmetric in (i, j) but not in k.

#### 6.4.6 Semantics

**Production**: The compiler trusts the annotation and emits triangular iteration. False annotations yield undefined behavior.

**Debug**: The compiler may verify by sampling random index tuples and checking permutation invariance.

------------------------------------------------------------------------

## 7. Core Operations

Blade wraps primitive operations in `object_for` to enable symmetry inference and equivariance tracking.

### 7.1 Arithmetic Operations

Primitive arithmetic carries symmetry annotations that enable automatic `comm`/`antisymm` inference:

```blade
// Arithmetic with symmetry annotations
let add = object_for(+) with Symmetric
let sub = object_for(-) with Antisymmetric
let mul = object_for(*) with Symmetric
let div = object_for(/) with Asymmetric
```

**Symmetry inference from operations:**

```blade
function f(a: T^0, b: T^0) { a + b }  
// Compiler knows (+) is Symmetric → infers comm(a, b)

function g(a: T^0, b: T^0) { a - b }
// Compiler knows (-) is Antisymmetric → infers antisymm(a, b)

function h(a: T^0, b: T^0) { a * b }
// Compiler knows (*) is Symmetric → infers comm(a, b)
```

**Equivariance signatures for arithmetic:**

```
(+) : (T with equiv(G, ρ), T with equiv(G, ρ)) → T with equiv(G, ρ)
(-) : (T with equiv(G, ρ), T with equiv(G, ρ)) → T with equiv(G, ρ)
(*) : (T with invariant(G), T with equiv(G, ρ)) → T with equiv(G, ρ)
(/) : (T with equiv(G, ρ), T with invariant(G)) → T with equiv(G, ρ)
```

Scalar multiplication preserves equivariance; division by a scalar preserves equivariance.

### 7.2 Geometric Primitives

```blade
// Norm: vector → scalar (invariant)
let norm = object_for(norm) 
    with signature: T^1 with equiv(G, L1) → T^0 with invariant(G)

// Dot product: vector × vector → scalar (invariant)
let dot = object_for(dot) with Symmetric
    with signature: (T^1 with equiv(G, ρ), T^1 with equiv(G, ρ)) → T^0 with invariant(G)

// Cross product: vector × vector → pseudovector
let cross = object_for(cross) with Antisymmetric
    with signature: (T^1 with equiv(O<3>, L1_odd), T^1 with equiv(O<3>, L1_odd)) 
                  → T^1 with equiv(O<3>, L1_even)
```

### 7.3 Reductions

```blade
// Sum preserves equivariance, reduces rank
sum<I> : T^(I, J) with equiv(G, ρ) → T^J with equiv(G, ρ)

// Mean preserves equivariance, reduces rank
mean<I> : T^(I, J) with equiv(G, ρ) → T^J with equiv(G, ρ)

// Min/Max only valid for invariants (ordering requires invariance)
min<I> : T^(I, J) with invariant(G) → T^J with invariant(G)
max<I> : T^(I, J) with invariant(G) → T^J with invariant(G)
```

### 7.4 Operation Symmetry Summary

| Operation | Symmetry | Equivariance |
|-----------|----------|--------------|
| `+` | Symmetric | Preserves (same rep) |
| `-` | Antisymmetric | Preserves (same rep) |
| `*` | Symmetric | Scalar × equivariant → equivariant |
| `/` | Asymmetric | Equivariant / scalar → equivariant |
| `norm` | — | Vector → invariant |
| `dot` | Symmetric | Vector × vector → invariant |
| `cross` | Antisymmetric | Vector × vector → pseudovector |
| `sum` | — | Preserves (reduces rank) |
| `mean` | — | Preserves (reduces rank) |
| `min`/`max` | — | Requires invariant |

---

## 8. Equivariance System

Blade provides compile-time tracking of value transformations under continuous groups, complementing the index type system for storage optimization.

### 8.1 Relationship to Index Types

Blade has two orthogonal symmetry systems:

**Index types** (§4):
- Handle discrete permutation symmetry (S₂, Sₙ)
- Affect storage layout and iteration
- `SymIdx<r, n>`, `AntisymIdx<r, n>`, etc.

**Equivariance annotations** (this section):
- Handle continuous group actions (SO(3), SE(3), etc.)
- Affect type checking only—zero runtime cost
- Pure compile-time verification

They combine naturally:

```blade
// Stress tensor: symmetric in indices, transforms as rank-2 tensor
let stress: Array<Float like SymIdx<2, 3>> with equiv(SO<3>, L2_even)

// Index symmetry: σ(i,j) = σ(j,i) → triangular storage
// Value equivariance: σ' = R σ Rᵀ under rotation → type checking
```

### 8.2 Annotation Syntax

Equivariance annotations use `with` clauses on types and declarations.

#### 8.2.1 Basic Annotations

```blade
// On variable declarations
let v: Array<Float like Idx<3>> with equiv(G, rep)

// Invariant (trivial representation) — shorthand
let energy: Float with invariant(G)
// Equivalent to: with equiv(G, trivial)

// On array declarations with index types
let positions: Array<Float like Idx<N>, Idx<3>> with equiv(SO<3>, vector)
// N vectors, each transforming as SO(3) vector
```

#### 8.2.2 Function Signatures

```blade
// Input and output annotations
function normalize(v: T^1 with equiv(G, rep)) -> T^1 with equiv(G, rep)

// Mixed: equivariant input, invariant output
function norm(v: T^1 with equiv(G, rep)) -> T^0 with invariant(G)

// Multiple inputs with same equivariance
function dot(a: T^1 with equiv(G, rep), 
             b: T^1 with equiv(G, rep)) -> T^0 with invariant(G)

// Output annotation can be omitted if inferable
function scale(s: T^0 with invariant(G),
               v: T^1 with equiv(G, rep)) -> T^1  // inferred: equiv(G, rep)
```

#### 8.2.3 Combining with Index Type Symmetry

Equivariance annotations combine with symmetric index types:

```blade
// Symmetric matrix that transforms as rank-2 tensor
let inertia: Array<Float like SymIdx<2, 3>> with equiv(SO<3>, L2_even)
// Storage: triangular (6 elements)
// Transformation: I' = R I Rᵀ

// Antisymmetric tensor (e.g., angular velocity as bivector)
let omega: Array<Float like AntisymIdx<2, 3>> with equiv(SO<3>, L1_odd)
// Storage: 3 elements (strictly upper triangle)
// Transformation: pseudovector under rotation

// Array of symmetric tensors
let stresses: Array<Float like Idx<N>, SymIdx<2, 3>> with equiv(SO<3>, L2_even)
// N symmetric 3×3 tensors, each transforming as rank-2 tensor
```

#### 8.2.4 No Annotation (Non-Equivariant)

Omitting annotations means no equivariance tracking:

```blade
let data: Array<Float like Idx<3>>  // no equiv annotation
// Compiler won't track transformations
// Can mix freely with other non-equivariant data
// Cannot pass to functions expecting equivariant input
```

### 8.3 Type Inference

The compiler infers output equivariance from input annotations and operation semantics.

#### 8.3.1 Inference Through Expressions

```blade
function message(pi: T^1 with equiv(SE<3>, vector),
                 pj: T^1 with equiv(SE<3>, vector))
{
    let diff = pj - pi          // inferred: equiv(SE<3>, vector)
    let dist = norm(diff)       // inferred: invariant(SE<3>)
    let dir = diff / dist       // inferred: equiv(SE<3>, vector)
    let weight = exp(-dist)     // inferred: invariant(SE<3))
    weight * dir                // inferred: equiv(SE<3>, vector)
}
// Return type inferred: T^1 with equiv(SE<3>, vector)
```

#### 8.3.2 Inference Rules

| Operation | Input Representations | Output Representation |
|-----------|----------------------|----------------------|
| `a + b`, `a - b` | Same rep ρ | ρ |
| `s * v` | invariant, ρ | ρ |
| `dot(a, b)` | ρ, ρ | invariant |
| `norm(v)` | ρ | invariant |
| `cross(a, b)` | ρ, ρ | Depends on ρ (see domain library) |
| `a ⊗ b` | ρ₁, ρ₂ | ρ₁ ⊗ ρ₂ (CG decomposition) |

Domain libraries define the specific rules for their groups.

#### 8.3.3 Explicit Annotations Override Inference

```blade
// Force specific representation (checked against inferred)
let result: T^1 with equiv(G, vector) = some_expression
// Compiler verifies inferred rep matches declared rep
```

### 8.4 Error Detection

The compiler catches equivariance errors at compile time.

#### 8.4.1 Representation Mismatch

```blade
function broken_add(v: T^1 with equiv(O<3>, vector),      // polar vector
                    w: T^1 with equiv(O<3>, pseudovector)) // axial vector
{
    v + w  
}
// ERROR: Cannot add 'vector' and 'pseudovector'
//        Representations must match for addition
```

#### 8.4.2 Breaking Equivariance

```blade
function broken_extract(v: T^1 with equiv(SO<3>, vector))
{
    v(0)  // extract x-coordinate
}
// ERROR: Indexing into equivariant array breaks SO<3> equivariance
// HINT: Use invariant operations like norm(v) or dot(v, reference_direction)
```

#### 8.4.3 Wrong Output Declaration

```blade
function broken_cross(a: T^1 with equiv(O<3>, vector),
                      b: T^1 with equiv(O<3>, vector))
    -> T^1 with equiv(O<3>, vector)  // WRONG: claims vector
{
    cross(a, b)
}
// ERROR: Return type mismatch
//        Declared: equiv(O<3>, vector)
//        Inferred: equiv(O<3>, pseudovector)
//        cross(vector, vector) produces pseudovector, not vector
```

#### 8.4.4 Index/Equivariance Incompatibility

```blade
// Antisymmetric storage but symmetric representation
let bad: Array<Float like AntisymIdx<2, 3>> with equiv(SO<3>, L2_even)
// ERROR: L2_even is symmetric under index exchange
//        but AntisymIdx stores antisymmetric data
// HINT: Use SymIdx<2, 3> for symmetric tensors
//       or L2_odd for antisymmetric tensors
```

#### 8.4.5 Missing Annotation

```blade
function needs_equiv(v: T^1 with equiv(SO<3>, vector)) { ... }

let plain_data: Array<Float like Idx<3>>  // no annotation
needs_equiv(plain_data)
// ERROR: Cannot pass non-equivariant value to equivariant parameter
// HINT: Add annotation: Array<Float like Idx<3>> with equiv(SO<3>, vector)
```


### 8.5 Domain Libraries

Specific groups, representations, and their algebra are defined in domain libraries rather than the core language:

- **ML/Physics**: SO(3), O(3), SE(3) with spherical harmonics representations (L0, L1, L2, ...) — see Blade ML Spec
- **Chemistry**: Point groups, molecular symmetry
- **Robotics**: SE(3), screw theory representations
- **Graphics**: SO(2), affine groups

The core language provides the annotation mechanism and inference framework; domain libraries supply the group-specific rules.



## 9. Loop Objects

### 9.1 The Core Abstraction

A *loop object* reifies an iteration pattern as a first-class value. There are two dual constructions:

**Method Loop (S-first)**: Binds arrays, awaits function

```blade
method_for : A* → MethodLoop
```

**Object Loop (kernel-first)**: Binds function, awaits arrays

```blade
object_for : Function → ObjectLoop
```

The method_for/object_for distinction is about *construction order* (bind arrays first vs. bind function first), not about different dimension types.

**Terminology note**: *Iteration object* (Definition, §2.4) is the abstract concept---a value satisfying kernel independence, kernel polymorphism, and composability. *Loop object* is Blade's concrete realization via `method_for` and `object_for`. *Loop reification* is the language feature that provides loop objects. [Theorem 2.3](#theorem-2-3) proves T/S cannot have iteration objects; Blade's loop objects are how S/T provides them.

### 9.2 S-Dimensions and T-Dimensions

Loop iteration involves two dimension types:

**S-dimensions (Spatial/Structural)**: Arise from iterating over input arrays. The iteration structure---nesting depth, bounds, triangular constraints---is determined by input array ranks and symmetries.

**T-dimensions (Temporal/Trailing)**: Added by the function's output when it produces dimensions not derived from iteration. The name reflects both the temporal nature (by analogy to spatial S-dimensions) and the trailing position in output arrays. Example: FFT transforms time → frequency.

For a function f applied to arrays A₁, ..., Aₙ:

-   S-dimension count = Σᵢ (rank(Aᵢ) - irank(f, i))
-   T-dimension count = f.ORank

Output rank = S-dimensions + T-dimensions

**Definition (Input Rank)**: `irank(f, i)` is the *input rank* of the i-th argument to kernel f---the rank of that argument as seen within the kernel scope after loop iteration has indexed into the outer dimensions. When a rank-r array is bound to a loop with k S-dimensions, the kernel receives elements of rank (r - k). If the kernel signature declares argument i with type `T^m`, then `irank(f, i) = m`.

For example, if `data` has rank 2 (a matrix) and the kernel signature expects `float^0` (scalars), then `irank = 0` and the loop iterates over both dimensions of the matrix, yielding 2 S-dimensions per array.

### 9.3 Method Loop Structure

A MethodLoop M contains:

-   **arrays**: A₁, ..., Aₙ (bound input arrays)
-   **S-structure**: The iteration pattern derived from array ranks/symmetries
-   **Awaiting**: A function to apply

```{=html}
<!-- -->
```
    M = method_for(A₁, ..., Aₙ)

The S-dimension structure is fixed at construction. Different functions can be applied to the same MethodLoop, sharing iteration structure.

### 9.4 Object Loop Structure

An ObjectLoop O contains:

-   **func**: f (bound function)
-   **Awaiting**: Arrays to iterate over

```{=html}
<!-- -->
```
    O = object_for(f)

The function is fixed at construction. Different arrays can be passed to the same ObjectLoop.

### 9.5 Partial Application Semantics

Loop objects are *partial*---they await completion:

```
MethodLoop × Function → Computation
ObjectLoop × Arrays → Computation
```

A Computation is a complete, executable specification.

### 9.6 The Structural Trinity: Formal Necessity Proofs

This section proves that **loop reification**, **arity polymorphism**, and **dimensional currying** form an inseparable trinity---each requires the other two.

#### 9.6.1 Definitions

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

#### 9.6.2 The Trinity Theorems

[]{#theorem-9-1}**Theorem 9.1 (Arity Polymorphism Requires Loop Reification)**: A system with arity-polymorphic kernels---where arity r determines r-deep nested iteration with cumulative bounds---must have first-class loop representations.

*Proof*: An arity-polymorphic kernel:

```blade
function moment(args) where poly(args), comm(args)
```

when applied to r arrays requires r-deep nested iteration with left-justified bounds and triangular optimization from commutativity.

By [Theorem 2.7](#theorem-2-7), no fixed textual program expresses this for arbitrary r. The loop structure must be generated dynamically based on r AND represented as a manipulable value for commutativity analysis and bound computation. This is precisely loop reification. ∎

[]{#theorem-9-2}**Theorem 9.2 (Arity Polymorphism Requires Dimensional Currying)**: A system where arity r determines output rank r must have arrays-as-functions typing.

*Proof*: An arity-polymorphic kernel produces output whose rank equals the input arity:

```blade
method_for(A, A)       → rank-2 output
method_for(A, A, A)    → rank-3 output  
method_for(A, A, A, A) → rank-4 output
```

To type "output rank equals arity r" requires a type parameterized by r:

```blade
Output : N^r → T    (r-ary function type)
```

This is the curried array type. Without dimensional currying, each arity requires a separate, unrelated output type (`T[n][n]`, `T[n][n][n]`, etc.) with no polymorphic relationship. ∎

[]{#theorem-9-3}**Theorem 9.3 (Loop Reification Requires Dimensional Currying with Dependent Index Types)**: Left-justified triangular iteration produces arrays whose index type extents depend on bound index values, requiring dimensional currying with dependent index types.

*Proof*: In left-justified triangular iteration of arity r over extent n:

```
for i₀ in Idx<n>:
    for i₁ in Idx<n - i₀>:
        for i₂ in Idx<n - i₀ - i₁>:
            ...
```

The output array under dimensional currying:

```
Output(i₀)       : Array<T like Idx<n - i₀>, ...>
Output(i₀)(i₁)   : Array<T like Idx<n - i₀ - i₁>, ...>
```

The leading index type's extent at each level depends on previously bound index values. The type of `Output(i₀)` varies with `i₀`:

-   `Output(0)` has leading index type `Idx<n>`
-   `Output(n-1)` has leading index type `Idx<1>`

Non-dependent typing assigns a fixed type to `Output(i₀)` regardless of `i₀`, which cannot express this variation.

Dimensional currying with dependent index types provides the correct typing:

```
Output : (i₀: Idx<n>) → Array<T like Idx<n - i₀>, ...>
```

The return type's leading extent depends on the argument value.

Without dependent index types, left-justified triangular arrays cannot be correctly typed---the system cannot verify that `Output(i₀)(i₁)` is in-bounds when the bound depends on `i₀`. ∎

[]{#theorem-9-4}**Theorem 9.4 (Dimensional Currying Requires Loop Reification for Bound Computation)**: Computing the dependent extent `n - Σ_{m<k} i_m` requires access to the loop structure.

*Proof*: The extent of `Output[i₀][i₁]...[i_{k-1}]` is `n - i₀ - i₁ - ... - i_{k-1}`.

To compute this extent, the system must know: 1. Which indices have been bound (i₀ through i\_{k-1}) 2. The values of those indices 3. The original extent n

This information constitutes the loop structure---specifically, which level of the nested iteration we're at and what index values have been fixed. This is loop reification: the loop state must exist as an inspectable value to compute dependent bounds. ∎

[]{#theorem-9-5}**Theorem 9.5 (Dimensional Currying Requires Arity Polymorphism)**: Dimensional currying for multi-array computations requires arity polymorphism.

*Proof*: Consider the typing judgment:

```blade
method_for(A₁, ..., Aᵣ) <@> k : Comp<T^r(σ)>
```

The output type `T^r(σ)` has rank `r` equal to the input arity. Constructing this type requires:

1.  Count inputs: `r = |A₁, ..., Aᵣ|` (term-level)
2.  Form output type: `T^r(σ)` (type-level)

Step (2) requires `r` at the type level. Without arity polymorphism, `r` exists only as a runtime value---the type system cannot express "output rank equals input count."

Therefore, dimensional currying with variable output rank requires arity polymorphism. ∎

**Remark (Symmetry Independence)**: This argument does not depend on symmetry. Symmetry determines σ (which dimensions are interchangeable) and enables triangular iteration, but the rank `r` is determined by arity alone. Commutativity provides the *option* for dependent triangular bounds; arity polymorphism is required regardless.

[]{#theorem-9-6}**Theorem 9.6 (Loop Reification Requires Arity Polymorphism)**: Loop reification with monoidal composition requires arity polymorphism.

*Proof*: Loop objects support the `<*>` combinator:

```blade
(<*>) : MethodLoop × MethodLoop → MethodLoop
method_for(A) <*> method_for(B) = method_for(A, B)
```

If loop objects are closed under `<*>`, then for any list of arrays:

```blade
fold(<*>, [method_for(A₁), ..., method_for(Aᵣ)]) = method_for(A₁, ..., Aᵣ)
```

This constructs arity-r loops for arbitrary r. The ability to fold `<*>` over a runtime-length list produces loops of variable arity.

Therefore, closure under `<*>` entails arity polymorphism. By [Theorem 2.3](#theorem-2-3), such loop reification cannot exist in T/S systems, hence arity polymorphism requires S/T orientation. ∎

#### 9.6.3 The Inseparability Theorem

[]{#theorem-9-7}**Theorem 9.7 (Trinity Inseparability)**: Loop reification, arity polymorphism, and dimensional currying are mutually necessary. Removing any one makes the other two inexpressible.

*Proof*: We show each feature requires the other two:

**(1) Arity polymorphism requires both:**

-   Requires loop reification by [Theorem 9.1](#theorem-9-1) (cannot generate r-deep nests otherwise)
-   Requires dimensional currying by [Theorem 9.2](#theorem-9-2) (cannot type rank-r output otherwise)

**(2) Loop reification requires both:**

-   Requires dimensional currying by [Theorem 9.3](#theorem-9-3) (cannot type left-justified output otherwise)
-   Requires arity polymorphism by [Theorem 9.6](#theorem-9-6) (closure under `<*>` entails variable arity)

**(3) Dimensional currying requires both:**

-   Requires loop reification by [Theorem 9.4](#theorem-9-4) (cannot compute dependent bounds otherwise)
-   Requires arity polymorphism by [Theorem 9.5](#theorem-9-5) (cannot type variable output rank otherwise)

The three features form a dependency cycle with no valid subset:

```
        Arity Polymorphism
              →   →
             /     \
            →       →
Loop Reification ←→ Dimensional Currying
```

Each edge represents a necessity proof (Theorems 5.1-5.4). ∎

[]{#corollary-9-8}**Corollary 9.8 (Unified Contribution)**: The three features constitute a single, indivisible contribution to programming language theory. Claims of novelty apply to the trinity as a whole, not to individual components.

#### 9.6.4 The Symmetry Tower and Lowering Homomorphisms

The trinity implements a deeper structure: **symmetry lowering** across a hierarchy of computational levels.

**Definition (Symmetry Levels)**: - **Level 0 (Elements)**: Symmetry is identity. `a = a`. - **Level 1 (Arrays)**: Symmetry is index permutation. `A[i,j] = A[j,i]` for σ = (1 2) ∈ S₁₂. - **Level 2 (Functions)**: Symmetry is argument permutation. `f(x,y) = f(y,x)` for commutative f. - **Level 3 (Combinators)**: Symmetry is composition structure. Associativity, MonadPlus laws.

**Definition (Symmetry Groups)**: - `Sym₀ = {id}` --- the trivial group - `Sym₁(r)` --- subgroups of Sᵣ acting on r index positions\
- `Sym₂(n)` --- subgroups of Sₙ acting on n argument positions

**Definition (Symmetric Objects)**:

A rank-r array A has symmetry H ∈ Sym₁(r) when:

```
∀σ ∈ H : A[i_{σ(1)}, ..., i_{σ(r)}] = A[i₁, ..., iᵣ]
```

An arity-n function f has symmetry H ∈ Sym₂(n) when:

```
∀σ ∈ H : f(x_{σ(1)}, ..., x_{σ(n)}) = f(x₁, ..., xₙ)
```

[]{#theorem-9-9}**Theorem 9.9 (Lowering `lower₂₁`: Commutativity → Array Symmetry)**:

Let `f : Tⁿ → T` have symmetry H ≤ Sₙ. Let `A : I → T` be an array. Define:

```
Out[i₁, ..., iₙ] = f(A[i₁], ..., A[iₙ])
```

Then Out has symmetry H.

*Proof*: Let σ ∈ H.

```blade
Out[i_{σ(1)}, ..., i_{σ(n)}]
  = f(A[i_{σ(1)}], ..., A[i_{σ(n)}])     [definition]
  = f(y_{σ(1)}, ..., y_{σ(n)})           [let yⱼ = A[iⱼ]]
  = f(y₁, ..., yₙ)                        [f has symmetry σ]
  = Out[i₁, ..., iₙ]                      ∎
```

[]{#corollary-9-10}**Corollary 9.10**: The map `lower₂₁ : Sym₂(n) → Sym₁(n)` defined by `lower₂₁(H) = H` is an isomorphism when all array arguments are identical.

*Proof*: When all arrays are identical (A₁ = ... = Aₙ = A), every permutation σ satisfies A\_{σ(j)} = Aⱼ, so Stab = Sₙ. Thus H ∩ Stab = H ∩ Sₙ = H. The map lower₂₁(H) = H is trivially injective. For surjectivity: any symmetry group G ≤ Sₙ acting on array indices arises from the commutative function f(x₁,...,xₙ) = symmetric combination with symmetry G. ∎

[]{#theorem-9-11}**Theorem 9.11 (Lowering with Distinct Arrays)**:

Let `f : Tⁿ → T` have symmetry H ≤ Sₙ. Let `A₁, ..., Aₙ : I → T` be arrays. Define:

```
Stab(A₁,...,Aₙ) = {σ ∈ Sₙ : ∀j. A_{σ(j)} = Aⱼ}
```

Then `Out[i₁, ..., iₙ] = f(A₁[i₁], ..., Aₙ[iₙ])` has symmetry `H ∩ Stab(A₁,...,Aₙ)`.

*Proof*: Let σ ∈ H ∩ Stab(A₁,...,Aₙ). We show Out\[i\_{σ(1)}, ..., i\_{σ(n)}\] = Out\[i₁, ..., iₙ\].

```
Out[i_{σ(1)}, ..., i_{σ(n)}]
  = f(A₁[i_{σ(1)}], ..., Aₙ[i_{σ(n)}])              [definition of Out]
  = f(A_{σ(1)}[i_{σ(1)}], ..., A_{σ(n)}[i_{σ(n)}])  [σ ∈ Stab: Aⱼ = A_{σ(j)}]
```

Now relabel: let yⱼ = A\_{σ(j)}\[i\_{σ(j)}\] for each j. Then:

```
  = f(y₁, ..., yₙ)
```

Since f has symmetry σ, we have f(y₁, ..., yₙ) = f(y\_{σ⁻¹(1)}, ..., y\_{σ⁻¹(n)}).

Substituting back: y\_{σ⁻¹(k)} = A\_{σ(σ⁻¹(k))}\[i\_{σ(σ⁻¹(k))}\] = Aₖ\[iₖ\].

Therefore:

```
  = f(A₁[i₁], ..., Aₙ[iₙ])
  = Out[i₁, ..., iₙ]  ∎
```

[]{#corollary-9-12}**Corollary 9.12**: - All arrays identical ⟹ Stab = Sₙ ⟹ `lower₂₁(H) = H` (full transfer) - All arrays distinct ⟹ Stab = {id} ⟹ `lower₂₁(H) = {id}` (no transfer)

[]{#theorem-9-13}**Theorem 9.13 (Lowering `lower₁₀`: Array Symmetry → Identity)**:

The map `lower₁₀ : Sym₁(r) → Sym₀` sending every permutation to identity is the unique homomorphism to the trivial group.

*Interpretation*: Reading elements from a symmetric array "consumes" the symmetry. The permutation σ ∈ Sym₁(r) guarantees `A(σ(i)) = A(i)`, but both sides denote the same element---this is just Level 0 identity.

[]{#theorem-9-14}**Theorem 9.14 (Input Symmetry Does Not Propagate)**:

Let `f : T² → T` have trivial symmetry (non-commutative). Let `A : I² → T` have symmetry S₁₂. Define `Out[i,j] = f(A[i,0], A[j,1])`.

Then Out has trivial symmetry.

*Proof*: `Out[j,i] = f(A[j,0], A[i,1]) ≠  f(A[i,0], A[j,1]) = Out[i,j]` in general, since f is non-commutative. The symmetry of A is irrelevant---it was consumed when elements were read. ∎

**Summary (The Lowering Principle)**:

  Homomorphism   Domain    Codomain   Structure
  -------------- --------- ---------- -------------------------------------
  `lower₁₀`      Sym₁(r)   Sym₀       Trivial (all symmetries → identity)
  `lower₂₁`      Sym₂(n)   Sym₁(n)    Isomorphism when arrays identical

Symmetry at level N lowers to level N-1 when objects are applied. Since `lower₁₀` is trivial, input array symmetry vanishes into element identity. Since `lower₂₁` is an isomorphism (for identical arrays), function commutativity transfers to output array symmetry. Both phenomena---input symmetry "quashing" and output symmetry "generation"---are instances of the same lowering structure.

[]{#theorem-9-15}**Theorem 9.15 (Trinity Implements Lowering)**:

The Structural Trinity provides the machinery to compute and exploit `lower₂₁`:

1.  **Arity polymorphism** determines the domain---arity n specifies which Sₙ we lower from
2.  **Dimensional currying** makes the codomain explicit---indices are arguments, so Sym₁ and Sym₂ share representation\
3.  **Loop reification** captures the lowered symmetry---the loop structure encodes which symmetries survived, determining triangular vs rectangular iteration

*Proof*: By [Theorem 9.7](#theorem-9-7), the three features are mutually necessary. Computing `lower₂₁` requires knowing arity (which Sₙ), treating indices as arguments (shared representation), and representing the result structurally (loop object with symmetry metadata). Each feature provides exactly one component. ∎

#### 9.6.5 Level 3 and Beyond: The First-Class Function Collapse

With first-class functions, there is no structural difference between a function taking values and a function taking functions:

```
f   : T² → T           -- binary on values (Level 2)
<&> : Comb² → Comb     -- binary on combinators (Level 3)
```

Both are binary operations on some type. The "level" is determined by what you feed in, not by intrinsic structure. This means **Level 3+ collapses into Level 2** --- it's the same Sₙ symmetry on arguments, applied recursively to higher-order functions.

**The Effective Tower**:

```
Level 0:  Elements (identity only)
Level 1:  Arrays (Sₙ on indices, physical storage)
Level 2+: Functions (Sₙ on arguments, all the way up)
```

Levels 0 and 1 are special: - Level 0 has no exploitable structure - Level 1 has *spatial* structure --- memory layout, cache behavior, triangular storage

Level 2+ is "just algebra." Symmetries are free to permute at runtime; the computational payoff comes from **lowering to Level 1** where symmetry becomes physical bytes and avoided FLOPS.

**Lifting Combinators via `method_for` and `object_for`**:

The `method_for`/`object_for` duality extends to combinators themselves:

```blade
method_for(<&>) : [f, g, h, ...] → f <&> g <&> h <&> ...
method_for(>>) : [f, g, h, ...] → f >> g >> h >> ...
```

This is precisely `fold` --- lifting a binary combinator to n-ary. Associativity of `<&>` and `>>` makes this well-defined (parenthesization doesn't matter).

```blade
object_for(<&>)(f) = λg. f <&> g     -- curried: build parallel composition incrementally
object_for(>>)(f) = λg. f >> g       -- curried: build pipeline incrementally
```

**Dynamic Kernel Construction**:

With `object_for(>>)`, kernels can be assembled programmatically at the top level:

```blade
let pipeline = object_for(>>)
let kernel = pipeline(normalize)(log_transform)(clip(0,1))(scale(255))

// kernel = normalize >> log_transform >> clip(0,1) >> scale(255)
// Now apply to data:
method_for(A) <@> kernel
```

The kernel is *data* --- a value constructed, inspected, and transformed --- until applied to arrays and lowered to Level 1.

**S/T All The Way Up**:

This extends the S/T philosophy to kernel construction itself:

  -------------------------------------------------------------------------------------------------
  Level                    S/T Pattern
  ------------------------ ------------------------------------------------------------------------
  3                        `method_for(<&>)([stats...])` --- build combinator structure from list

  2                        `method_for(A, A, A)` --- build iteration structure from arrays

  1                        Triangular storage --- physical realization of symmetry
  -------------------------------------------------------------------------------------------------

Structure is built top-down; data flows bottom-up. The entire computation is *shaped* before any data is touched.

### 9.7 Uniqueness of method_for and object_for

This section proves that `method_for` and `object_for` are the *unique* composable partial specifications of symmetric tensor computation.

#### 9.7.1 Definitions

**Definition 9.19 (Symmetric Tensor Computation):** Computation over `r` arrays of dimension `d` where identical arrays and commutative kernels enable triangular iteration, reducing iteration count from `n^(rd)` to `n^(rd) / (r!)^d`.

**Definition 9.20 (Currying):** A partial specification binding subset `S ⊆ {f, A₁, ..., Aᵣ}`:

```
Curry(S) = λ(remaining). nested_for(all)
```

#### 9.7.2 Triangular Validity

**Lemma 9.21 (Triangular Validity):** Triangular iteration is valid iff:

1. Arrays identical: `A₁ = A₂ = ... = Aᵣ`
2. Kernel commutative: `f(x_σ(1), ..., x_σ(r)) = f(x₁, ..., xᵣ)` for all σ ∈ Sᵣ

*Proof:* If both hold, all `r!` permutations of an index tuple produce the same result—compute one representative per equivalence class. If either fails, permuting arguments changes results. ∎

**Lemma 9.22 (Disjoint Sources):** Condition (1) depends only on arrays. Condition (2) depends only on the kernel. No information flows between them.

*Proof:* Array identity is a relation over array references. Commutativity is a property of the function definition. These are syntactically and semantically disjoint. ∎

#### 9.7.3 Detection Under Currying

**Lemma 9.23 (Identity Detection):** Identity `A₁ = ... = Aᵣ` is detectable iff all arrays are bound.

*Proof:* Identity is a relation over the complete tuple. Missing any `Aₖ` leaves identity undetermined—the missing array could be identical to the others or distinct. ∎

**Lemma 9.24 (Commutativity Detection):** Commutativity is detectable iff `f` is bound.

*Proof:* Commutativity is a property of the kernel alone. Without `f`, no commutativity information exists. ∎

**Lemma 9.25 (No Cross-Information):** Arrays provide no commutativity information. The kernel provides no identity information.

*Proof:* By Lemma 9.22, the two conditions have disjoint sources. ∎

#### 9.7.4 The Two Maximal Curryings

**Theorem 9.26 (Two Maximal Curryings):** The only curryings enabling symmetry detection with no redundancy are:

1. `Curry({A₁, ..., Aᵣ})` — all arrays, no kernel
2. `Curry({f})` — kernel only

*Proof:*

**Case 1:** `{A₁, ..., Aᵣ}` detects identity (Lemma 9.23). No proper subset works—missing any array leaves identity undetermined. This currying is maximal for identity detection. ✓

**Case 2:** `{f}` detects commutativity (Lemma 9.24). The singleton is already minimal. This currying is maximal for commutativity detection. ✓

**Case 3:** `{f, A₁}` detects commutativity, but `A₁` is wasted—identity is still undetectable (need all arrays). This strictly contains `{f}` with no additional detection power. Not maximal.

**Case 4:** `{A₁, ..., Aₖ}` for `k < r` cannot detect identity (Lemma 9.23). Not maximal.

**Case 5:** Other mixed curryings either have redundant elements or incomplete detection. ∎

#### 9.7.5 Composable Partial Specification

**Theorem 9.27 (Composable Partial Specification):** `method_for` and `object_for` are the unique maximal partial specifications of `nested_for` that:

1. Enable symmetry detection before full application
2. Admit closed composition (`<*>`, `>>@`)

*Proof:*

`nested_for(f, A₁, ..., Aᵣ)` achieves speedup directly but cannot compose—it is fully specified and terminal.

Composition requires partial specifications that combine before full application:

- `method_for(A, B) <*> method_for(C)` → `method_for(A, B, C)`
- `object_for(f) >>@ object_for(g)` → `object_for(f >> g)`

By Theorem 9.26, only all-arrays and kernel-only curryings enable detection. Mixed specifications cannot determine whether composition preserves triangular eligibility—they lack complete information for either condition. ∎

**Corollary 9.28 (nested_for Doesn't Compose):** `nested_for` achieves factorial speedup but cannot combine with other loops, pipeline kernels, or fuse parallel computations. Composition requires the `method_for`/`object_for` decomposition.

**Theorem 9.29 (Uniqueness):** For composable symmetric tensor computation with `(r!)^d` speedup:

1. `nested_for` achieves speedup (fully specified, terminal)
2. `method_for`/`object_for` are the unique composable partial specifications
3. Alternatives either: are fully specified (no composition), are non-maximal (cannot detect symmetry), or reduce to these two (isomorphic)

*Proof:* By Theorems 9.15 and 9.16. The design space is exhausted by enumeration. ∎

### 9.8 Virtual Arrays

A **virtual array** is a type-level construct describing an iteration pattern without allocating storage. Virtual arrays have `Unit` element type—they exist purely to emit indices.

```blade
range<I>              // iterate I in standard order
reverse<I>            // iterate I in reverse order
blocked<I, K>         // iterate I in K-sized cache blocks
neighbors_of<I>       // iterate adjacent positions in I
where<I>(mask)        // iterate I where mask is true
```

**Key property**: Virtual arrays are types, not values. They erase completely in code generation—no runtime representation, no allocation, just loop structure.

#### 9.8.1 Semantics

An index type `I` specifies extent, structure, and enumeration order. `range<I>` lifts an index type into something `method_for` can consume:

```blade
range<Idx<N>>         // emits 0, 1, 2, ..., N-1
range<SymIdx<2, N>>   // emits (0,0), (0,1), (1,1), (0,2), ...
range<BoundedIdx<k,N>> // emits 0, 1, ..., N-k-1
```

Semantically: `range<I> = λi:I. i`

#### 9.8.2 Composition with Real Arrays

Virtual arrays compose with real arrays in `method_for`:

```blade
// Indices only
method_for(range<SymIdx<2, N>>) <@> lambda(i, j) -> f(i, j)

// Indices with arrays
method_for(range<SymIdx<2, N>>, A, B) <@> lambda(i, j, a, b) -> f(i, j, a, b)

// Custom traversal
method_for(blocked<SymIdx<2, N>, 32>, A, A) where comm <@> lambda(i, j, a, b) -> a * b
```

### 9.9 For-Loop Syntax

The `for` construct provides clean syntax over `method_for` and `object_for`. Despite familiar appearance, it constructs iteration objects—not imperative control flow.

#### 9.9.1 Core Structure

```
for <source> [where <clauses>] <@> <source>
```

One side has a kernel, the other has an array/index specification. Two dual forms:

```blade
// method_for style: arrays/indices left, kernel right
for (A, B) in I <@> lambda(i, j, a, b) -> f(i, j, a, b)

// object_for style: kernel left, arrays/indices right  
for lambda(i, j, a, b) -> f(i, j, a, b) <@> (A, B) in I
```

#### 9.9.2 Forms

**Index iteration only**:
```blade
for SymIdx<2, N> <@> lambda(i, j) -> i * j
```
Desugars to: `method_for(range<SymIdx<2, N>>) <@> lambda(i, j) -> i * j`

**Value iteration only**:
```blade
for (A, B) <@> lambda(a, b) -> a * b
```
Desugars to: `method_for(A, B) <@> lambda(a, b) -> a * b`

**Both indices and values**:
```blade
for (A, B) in SymIdx<2,N> <@> lambda(i, j, a, b) -> f(i, j, a, b)
```
Desugars to: `method_for(range<SymIdx<2,N>>, A, B) <@> lambda(i, j, a, b) -> f(i, j, a, b)`

**Arity-polymorphic** (two packs for indices and values):
```blade
for args in SymIdx<arity(args), N> where poly(args), comm(args)
<@> lambda(is, xs) -> f(is, xs)
```

#### 9.9.3 Let-Binding

Both forms produce iteration objects that can be let-bound:

**method_for style** (arrays bound, awaits kernel):
```blade
let loop = for (A, A) in SymIdx<2,N> where comm
loop <@> lambda(i, j, a, b) -> f(i, j, a, b)
```

**object_for style** (kernel bound, awaits arrays):
```blade
let op = for lambda(a, b) where comm -> a * b
op <@> (X, X)
op <@> (Y, Z)  // reuse with different arrays
```

**Arity-polymorphic kernels**:
```blade
let moment = for lambda(is, xs) where poly(is, xs), comm(xs) -> product(xs)
let cov = moment <@> (data, data)
let coskew = moment <@> (data, data, data)
let cokurt = moment <@> (data, data, data, data)
```

#### 9.9.4 Custom Traversal

```blade
// Reversed
for (A, A) in reverse<SymIdx<2, N>> where comm <@> lambda(i, j, a, b) -> a * b

// Cache-blocked
for (A, A) in blocked<SymIdx<2, N>, 64> where comm <@> lambda(i, j, a, b) -> a * b

// Sparse
let valid = where<Idx<N>, Idx<M>>(mask)
for (data) in valid <@> lambda(i, j, x) -> process(i, j, x)
```

#### 9.9.5 Summary Table

| Want | method_for style | object_for style |
|------|------------------|------------------|
| Indices only | `for I <@> lambda(i,j) -> ...` | `for lambda(i,j) -> ... <@> I` |
| Values only | `for (A,B) <@> lambda(a,b) -> ...` | `for lambda(a,b) -> ... <@> (A,B)` |
| Custom traversal | `for (A,B) in I <@> lambda(a,b) -> ...` | `for lambda(a,b) -> ... <@> (A,B) in I` |
| Values + indices | `for (A,B) in I <@> lambda(i,j,a,b) -> ...` | `for lambda(i,j,a,b) -> ... <@> (A,B) in I` |
| Poly + indices | `for args in I <@> lambda(is, xs) -> ...` | `for lambda(is, xs) -> ... <@> args in I` |

------------------------------------------------------------------------

## 10. Arity Polymorphism

### 10.1 Distinction from Rank Polymorphism

Array programming languages have long supported *rank polymorphism*---the ability for functions to operate uniformly across arrays of different ranks (shapes). Systems like APL, J, and Remora (Slepak et al.) formalize how a scalar function lifts to operate on vectors, matrices, and higher-rank tensors.

**Rank polymorphism**: One array, varying shape

```
sum : T^r → T^0    // works for any rank r
```

Blade-DSL introduces a distinct concept: *arity polymorphism*---the ability for loop structures to adapt to varying numbers of input arrays, where the arity itself determines symmetry structure and output rank.

**Arity polymorphism**: Varying number of arrays, fixed kernel

```blade
method_for(A, A)       → rank-2 output, 2! = 2× speedup
method_for(A, A, A)    → rank-3 output, 3! = 6× speedup
method_for(A, A, A, A) → rank-4 output, 4! = 24× speedup
```

### 10.2 Why Arity Polymorphism Matters

For comoment tensors, arity is fundamental:

  Statistic      Arity   Output Rank   Symmetry
  -------------- ------- ------------- ----------
  Covariance     2       2             Full
  Coskewness     3       3             Full
  Cokurtosis     4       4             Full
  nth comoment   n       n             Full

The *same kernel* (product of elements) applied at *different arities* produces tensors of different orders with different symmetry exploitation. This is not expressible in rank-polymorphic systems, which vary the shape of a single input, not the number of inputs.

### 10.3 Arity and Commutativity

Arity polymorphism interacts with commutativity to determine loop structure:

```blade
function moment(args)
where
    poly(args),
    comm(args)    // all arguments commutative
{
    product(args)
}
```

When instantiated at arity n with the same array A:

-   Creates n-deep nested loop over A
-   All arguments in same commutativity group → fully symmetric output
-   Triangular iteration with n! speedup

When instantiated at arity n with different arrays:

-   Creates n-deep nested loop
-   Commutativity checked at runtime: same array in commutativity group → triangular; different arrays → rectangular
-   System validates and exploits what symmetry is actually present

### 10.4 Arity-Polymorphic Syntax

Arity-polymorphic kernels accept a variable number of arguments. The concrete syntax uses **tuples** to represent argument packs, avoiding the ellipsis (`...`) spread syntax common in other languages.

#### 10.4.1 Fixed Arity (Named Arguments)

For kernels with known, fixed arity, use named arguments:

```blade
function coskewness(a: T^0, b: T^0, c: T^0)
where comm(a, b, c)
{
    a * b * c
}
```

No `poly(...)` clause needed---arity is determined by the argument list.

#### 10.4.2 Variable Arity (Tuple-Based)

For kernels that operate on any number of arguments:

```blade
function product(args)
where poly(args), comm(args)
{
    let (head, tail) = args
    head * product(tail)    // base case automatic: product(()) = 1
}
```

The `poly(...)` clause marks which parameters are arity-polymorphic tuples.

#### 10.4.3 Tuple Destructuring

Left-associative tuple destructuring, paralleling dimensional currying:

```blade
let (head, tail) = args           // head: first element, tail: rest tuple
let (a, b, tail) = args           // a, b: first two, tail: rest
let (a, b, c) = args              // a, b: first two, c: rest (NOT exact match)
let (_, tail) = args              // discard first, get rest
let (head, _) = args              // get first, discard rest
```

**Pattern arity \> tuple arity**: Excess names bind to `()` (unit):

```blade
let (a, b, c) = (X)               // a=X, b=(), c=()
let (head, tail) = ()             // head=(), tail=()
```

-   In `poly(...)` scope: No warning (natural recursion base case)
-   In fixed-arity scope: **Warning** emitted

**No right-associative patterns** --- matches dimensional currying where indexing peels from the left.

#### 10.4.4 Indexed Access

Tuple indexing uses `[]` while array indexing uses `()`:

-   `args[k]` --- access k-th element of poly-tuple (structural access)
-   `A(i)` --- apply index i to array A (function application)

This distinction reflects the semantics: arrays are functions (application uses `()`), while poly-tuples are structural containers (access uses `[]`).

```
args[0]                         // first element of tuple
args[k]                         // kth element (k can be runtime variable)
args[arity - 1]                 // last element
```

#### 10.4.5 Iteration

```
for k in 0..arity {               // exclusive upper bound: k ∈ [0, arity)
    out = out + args[k] * weights[k]
}
```

#### 10.4.6 Scope Variables

  Variable   Meaning                              Available in
  ---------- ------------------------------------ ------------------------
  `arity`    Total argument count                 Any `poly(...)` kernel
  `nth`      Current recursion depth (0 at top)   Recursive kernels only

#### 10.4.7 Recursive Pattern

```blade
function product(args)
where poly(args), comm(args)
{
    let (head, tail) = args
    head * product(tail)    // base case automatic: product(()) = 1
}
```

No explicit base case needed --- `f(())` returns identity element for `f`.

#### 10.4.8 Iterative Pattern

```blade
function weighted_sum(args, weights: T^1)
where poly(args), comm(args)
{
    let out = 0
    for k in 0..arity {
        out = out + args[k] * weights[k]
    }
    out
}
```

#### 10.4.9 Syntax Summary

  ----------------------------------------------------------------------------------------
  Feature                              Syntax
  ------------------------------------ ---------------------------------------------------
  Arity-polymorphic declaration        `where poly(args)` or `where poly(data, weights)`

  Destructure                          `let (head, tail) = args`

  Index                                `args[k]`

  Count                                `arity`

  Recursion depth                      `nth`

  Iteration                            `for k in 0..arity { ... }`

  Identity base case                   Implicit via `f(())`
  ----------------------------------------------------------------------------------------

#### 10.4.10 Nested Tuples

Tuples can be nested, with structure preserved:

```
((A, B), C)                       // nested: 2 top-level elements
(A, B, C)                         // flat: 3 top-level elements
```

**Singleton collapse:** `(a) = a` --- singleton tuples collapse to their element.

**Access rules:** - Indexing `args[k]` accesses top-level positions only - Nested access requires destructuring: `let (ab, c) = args // ab is (A, B) let (a, b) = ab // now a, b accessible // NOT: args[0][1] // no deep indexing`

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

**Loop construct rules:** - `method_for(A, B, C)` --- always flat - `method_for(A, B) <*> method_for(C)` --- concatenates flat: `(A, B, C)` - `object_for(f) <@> tuple` --- single `<@>` only, tuple structure preserved - `object_for(f) <@> ((A, B), C)` --- explicitly nested, f sees 2 top-level args

**Commutativity:** - `comm(args)` applies to top-level positions only - Nested structure respected --- comm doesn't penetrate sub-tuples - Swapping only valid for same-typed positions

### 10.5 Formal Treatment

An arity-polymorphic function has signature:

```
f : (T^r)* → T^0
    where * indicates variable arity
```

When applied via a loop object:

```blade
method_for(A₁, A₂, ..., Aₙ) <@> f
```

The system computes:

1.  **Output rank**: Σᵢ (rank(Aᵢ) - irank(f, i))
2.  **Symmetry groups**: From commutativity annotation + which Aᵢ are identical
3.  **Loop structure**: Triangular where symmetry allows, rectangular otherwise

**Typing rule for arity-polymorphic application**:

```blade
Γ ⊢ M : MethodLoop[A₁...Aₙ]    f : arity-polymorphic
compatible(n, f)
c = commutativity(f, n)
σ' = OutputSymmetry(A₁...Aₙ, c)
──────────────────────────────────────────────────────
         Γ ⊢ M <@> f : Comp[T^n(σ')]
```

### 10.6 Comparison to Related Work

#### 10.6.1 Arity Polymorphism vs Variadic Functions

Variadic functions (C++, Scheme, etc.) and Blade's arity polymorphism both accept varying numbers of arguments, but solve fundamentally different problems.

**Variadic function schema** (Strickland et al. ESOP 2009):

```
variadic : ∈(τ ...). (τ ... → σ)
```

The output type σ is *fixed* regardless of how many arguments are supplied.

**Blade arity-polymorphic schema**:

```
arity_poly : ∀r. (τ^r → τ) → (Array<τ,n>)^r → Array<τ, n^r, σ_r>
```

The output type *depends on* r: rank = r, shape = n\^r, symmetry = σ_r.

**Theorem (Variadic Cannot Express Arity Polymorphism)**: Standard variadic typing cannot derive output rank from input count.

*Proof*: In variadic typing, the output type σ is determined before knowing argument count. To express "output rank equals input count" requires:

1.  **Dependent types**: Output type depends on term-level value r
2.  **Type-level naturals**: r available at the type level
3.  **Type-level arithmetic**: Computing n\^r as output shape

Standard variadic polymorphism provides none of these. ∎

**Theorem (Arity Polymorphism Requires Dependent Typing)**: Blade's arity polymorphism requires type-level representation of arity and type-level computation of output shape.

*Proof*: The typing judgment for `method_for`:

```blade
Γ ⊢ A₁ : Array<τ, n>  ...  Γ ⊢ Aᵣ : Array<τ, n>
Γ ⊢ k : (τ^r → τ) with comm(...)
─────────────────────────────────────────────────
Γ ⊢ method_for(A₁,...,Aᵣ) <@> k : Comp<Array<τ, n^r, σ>>
```

The output type contains r (from counting inputs), n\^r (type-level exponentiation), and σ (from commutativity analysis). This requires counting array arguments at the type level and computing output shape. ∎

**Summary of differences**:

  ----------------------------------------------------------------------------------
  Aspect            Variadic Functions            Blade Arity Polymorphism
  ----------------- ----------------------------- ----------------------------------
  Output type       Fixed, independent of arity   Depends on arity (rank = r)

  Output shape      Not affected                  n\^r (exponential in arity)

  Symmetry          Not tracked                   Derived from commutativity

  Iteration         Linear fold over arguments    Nested loops (depth = r)

  Type-level info   Argument count not in types   Arity reflected in output type

  Triangular opt.   N/A                           Automatic from commutativity
  ----------------------------------------------------------------------------------

**What they share**: Both accept varying argument counts, treat arguments uniformly, and require some iteration mechanism over the argument list.

**What's novel in Blade**: The specific integration where arity determines (1) output tensor rank, (2) loop nesting depth, (3) symmetry group structure, and (4) triangular iteration eligibility. Prior work on arity polymorphism (Moggi 2000, Weirich & Casinghino) addresses generic programming over arity, not arity-to-structure inference.

#### 10.6.2 Comparison to Rank Polymorphism

**Remora (Slepak et al.)**: Formalizes rank polymorphism with frame/cell decomposition. Functions lift across ranks via implicit mapping. Does not address varying arity or symmetry.

**Multidimensional Homomorphisms (Rasch)**: Generalizes structural recursion to multiple dimensions. Focuses on regular parallelism patterns. Does not address arity-dependent loop structure or symmetry.

**Blade-DSL**: Arity is a polymorphic axis. The number of inputs determines:

1.  Loop nest depth
2.  Output tensor rank\
3.  Symmetry group structure
4.  Triangular iteration eligibility

This combination is novel: treating arity as a first-class dimension of variation, with automatic symmetry inference based on which arrays occupy commutative positions.

------------------------------------------------------------------------

## 11. Dimensional Currying

### 11.1 The Core Idea

Traditional array languages treat slicing as a data operation: `A[i, :, :]` returns a view or copy of a subset of A. Blade-DSL takes a different approach: **arrays are functions**, and indexing is **partial application**.

A rank-r array of element type T is conceptually a function:

```
A : ℕ → ℕ → ... → ℕ → T    (r indices)
```

Applying one index yields a rank-(r-1) array:

```
A(i) : ℕ → ℕ → ... → ℕ → T    (r-1 indices)
```

This is *dimensional currying*: each indexing operation binds the outermost dimension, returning a function that awaits the remaining dimensions.

### 11.2 Type-Level Encoding

The `promote` template encodes dimensional currying at the type level:

```
promote<T, 0>::type = T           // scalar
promote<T, 1>::type = T*          // rank-1 array
promote<T, 2>::type = T**         // rank-2 array
promote<T, r>::type = T**...*     // r pointer levels
```

Indexing transforms types:

```
A    : promote<T, r>::type
A(i) : promote<T, r-1>::type
```

This is not merely pointer arithmetic---it's a type-level guarantee that each indexing operation peels off exactly one dimension.

### 11.3 Cache Optimality by Construction

The key insight: **if arrays are laid out with the outermost dimension varying slowest (row-major), then dimensional currying guarantees cache-optimal access**.

At each loop depth:

-   Depth 0: Full arrays, iterating outermost dimension
-   Depth 1: Curried arrays `A(i)`, iterating next dimension
-   Depth k: Curried arrays `A(i)(j)...(k)`, accessing contiguous memory

**The type system encodes cache-optimal access patterns. Non-optimal iteration order becomes a type error.**

Traditional array languages hope the compiler discovers good loop order. With dimensional currying, optimal order is the *only* order expressible in the type system.

### 11.4 Distinction from Slicing

  -----------------------------------------------------------------------------------------------
  Aspect           Slicing                                Dimensional Currying
  ---------------- -------------------------------------- ---------------------------------------
  Semantics        Data subset                            Function application

  Memory           View into original (may have stride)   Pointer to contiguous subarray

  Cache behavior   Depends on slice dimensions            Guaranteed optimal

  Type             Same array type, different shape       Different type (reduced rank)

  Composition      Ad-hoc                                 Enables combinator algebra
  -----------------------------------------------------------------------------------------------

**Example**:

```
# Slicing (NumPy): A[:, i, :] may have non-contiguous memory
A[:, i, :]  # Shape (n, m), but stride may skip elements
```

```
// Dimensional currying: A(i) is always contiguous
A(i)  // Type: promote<T, r-1>, pointing to contiguous block
```

### 11.5 Enabling the Combinator Algebra

Dimensional currying is what makes combinator fusion zero-overhead:

```
(loop <@> f) <&!> (loop <@> g)
```

At each iteration point, both f and g receive curried arrays at the same depth. No intermediate full-rank arrays are materialized. The fusion happens at the *iteration level*, not the data level.

The combinators work because partially-curried arrays have compatible types:

-   `A(i)` and `B(i)` both have type `promote<T, r-1>::type`
-   They can be passed together to any function expecting rank-(r-1) inputs
-   The type system guarantees this composition is valid

### 11.6 Symmetry Integration

Dimensional currying composes cleanly with symmetric storage. The `index` and `set_index` functions handle coordinate transformation:

```
A(i)(j)(k)  // User writes natural indices
// System transforms: sort within symmetry groups, left-justify
// Access: triangular storage at transformed coordinates
```

The currying abstraction (type-level rank reduction) is orthogonal to the symmetry abstraction (coordinate transformation). This separation of concerns keeps both systems simple.

### 11.7 Sparse Tensor Compatibility

Blade is not designed for sparse tensor computation, but provides primitives that enable user-defined sparsity patterns. See §2.6 for the `<|:>` array fallback combinator.

**Partial-Depth Allocation**: Arrays can be allocated to a specified depth, with deeper levels defaulting to nullptr:

```
// C++ allocator API
auto A = allocate_to_depth<3>(shape, depth=2);
// A(i) is allocated for all i
// A(i)(j) is nullptr by default
```

Users manage which slices to allocate based on their sparsity pattern:

```
for (auto [i,j] : my_sparse_indices) {
    A(i)(j) = allocate_leaf(shape[2]);
}
```

**Limitations**: Blade does not provide sparse storage formats (CSR, COO, etc.), automatic sparsity detection, or sparse-specific iteration. The `<|:>` combinator handles missing data gracefully but does not optimize iteration patterns for sparsity.

------------------------------------------------------------------------

## 12. Combinator Algebra

### 12.1 Core Combinators

#### Application (\<@\>)

Completes partial evaluation:

```blade
(<@>) : MethodLoop × Function → Computation
(<@>) : ObjectLoop × A* → Computation
```

**Typing**:

```blade
M : MethodLoop[A₁...Aₙ]    f : (T₁^r₁...Tₙ^rₙ) → T^r
compatible(M, f)
──────────────────────────────────────────────────────
            M <@> f : Computation T^r'(σ')
```

where r' and σ' are computed by OutputSymmetry (§13.3).

#### Monadic Bind (\>\>=)

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

#### Functor Map (\<\$\>)

Transforms the result without changing loop structure:

```
(<$>) : (α → β) × Computation α → Computation β
f <$> c  ≡  c >>= (pure ∘ f)
```

### 12.2 Parallel Combinators

#### Parallel Composition (\<&\>)

Execute both computations, potentially fusing isomorphic loop prefixes:

```
(<&>) : Computation α × Computation β → Computation (α × β)
```

**Semantics**: Given computations C₁ and C₂, determine the *fusion depth* d---the number of outermost loops with identical loop level types. Generate fused iteration for the common prefix, then separate continuations.

#### Mandatory Fusion (\<&!\>)

Mandatory fusion for computations sharing the same MethodLoop:

```
(<&!>) : Computation α × Computation β → Computation γ
        where both computations derive from the same MethodLoop
```

**Restriction**: Only valid for MethodLoop-derived computations. For ObjectLoop, the S-dimension is fixed at application time with no shared reference, so `<&!>` cannot verify structural identity.

**Semantics**: Given `(M <@> f) <&!> (M <@> g)`, generate a single loop nest applying both f and g at each iteration point.

#### Array Product (\<\*\>)

Combines array tuples into a single iteration space (MethodLoop only):

```
(<*>) : MethodLoop × MethodLoop → MethodLoop
```

**Semantics**: `M₁ <*> M₂` concatenates the array lists of both loops, creating a single loop that iterates over the combined index space. Crucially, this is *not* a Cartesian product of independent iteration spaces---it builds a single iteration space whose structure depends on the kernel's commutativity annotations.

```blade
method_for(A) <*> method_for(B)    == method_for(A, B)
method_for(A) <*> method_for(A)    == method_for(A, A)
method_for(A, B) <*> method_for(C) == method_for(A, B, C)
```

**Commutativity is determined by the kernel, not by `<*>`**: The `<*>` combinator itself does not know about commutativity---it merely concatenates array lists. When a kernel is later applied via `<@>`, the kernel's `comm(...)` clause determines which array positions are interchangeable. If positions with identical arrays fall within the same commutativity group, triangular iteration is enabled; otherwise, iteration is rectangular.

This design means `<*>` is a pure structural combinator: it builds the array tuple, and the kernel annotates the symmetry. The same MethodLoop can be applied to different kernels with different commutativity, yielding different iteration patterns:

```blade
let M = method_for(A) <*> method_for(A)

M <@> f where comm(x, y)   // triangular iteration (x,y commutative, same array)
M <@> g                     // rectangular iteration (no commutativity declared)
```

**Triangular vs Rectangular** (summary):

-   If the kernel declares `comm(...)` covering positions with identical arrays → triangular
-   Otherwise → rectangular

**Identity**: `method_for()` (the empty loop) is the identity element.

```blade
method_for() <*> M  ≡  M  ≡  M <*> method_for()
```

#### Fold over Arrays

The `<*>` combinator enables dynamic construction of loops via fold:

```blade
fold(<*>, map(method_for, [A, A, A, B, B]))
  == method_for(A) <*> method_for(A) <*> method_for(A) <*> method_for(B) <*> method_for(B)
  == method_for(A, A, A, B, B)
```

This is essential for arity-polymorphic computations where the array list is determined at runtime:

```blade
let k = runtime_value()
let arrays = replicate(k, A) ++ [B, B]
let loop = fold(<*>, map(method_for, arrays))
loop <@> arity_any_kernel |> compute
```

**Duality with ObjectLoop**: For `object_for`, the fold happens implicitly at application time. The following are equivalent:

```blade
// method_for path: explicit fold, then apply kernel
fold(<*>, map(method_for, arrays)) <@> f

// object_for path: bind kernel, apply array list (fold implicit)
object_for(f) <@> arrays
```

Both paths produce the same computation. The fold operation acts on array tuples in both cases---either explicitly via `<*>` on MethodLoops, or implicitly when `object_for` accepts an array list.

### 12.3 Collection Combinators

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

### 12.4 Evaluation

#### Compute

```
(|> compute) : Computation α → α
```

Triggers evaluation of the (lazy) computation graph.

### 12.5 Combinator Laws

**Parallel composition is commutative** (up to tuple reordering):

```
C₁ <&> C₂  ≡  swap <$> (C₂ <&> C₁)
```

**Parallel composition is associative** (up to tuple reassociation):

```
(C₁ <&> C₂) <&> C₃  ≡  assoc <$> (C₁ <&> (C₂ <&> C₃))
```

**Fusion distributes over parallel** (when applicable):

```blade
(M <@> f) <&!> (M <@> g)  ≡  (M <@> f) <&> (M <@> g)  // but with guaranteed fusion
```

**Application is not commutative**:

```blade
M <@> f  ≠¢  f <@> M  // second form is not syntactically valid
```

### 12.6 Composition Combinators and the Duality Theorem

#### Kernel Composition (\>\>@)

Composes ObjectLoops before array binding:

```
(>>@) : ObjectLoop × ObjectLoop → ObjectLoop
```

**Semantics**: `object_for(f) >>@ object_for(g)` creates a new ObjectLoop that, when applied to arrays, runs `f` then pipes the result to `g`.

```blade
let pipeline = object_for(normalize) >>@ object_for(variance)
let result = pipeline <@> (data, data) |> compute
```

**Type constraint**: Output type of first kernel must match input type of second.

#### Sequential Composition (@\>\>)

Composes computations within a shared loop structure:

```
(@>>) : Computation α × Computation β → Computation β
        where both derive from the same MethodLoop
```

**Semantics**: `(M <@> f) @>> (M <@> g)` executes `f` then `g` at each iteration point, with `f`'s output feeding `g`'s input.

```blade
let loop = method_for(data, data)
let result = (loop <@> demean) @>> (loop <@> variance) |> compute
```

**Restriction**: Requires same MethodLoop (verified structurally).

#### The Duality Theorem

The `>>@` and `@>>` combinators satisfy a fundamental duality:

[]{#theorem-12-1}**Theorem 12.1 (Compose-Apply Duality)**:

```blade
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

```blade
object_for(id) >>@ o  ≡  o  ≡  o >>@ object_for(id)
(M <@> id) @>> c        ≡  c  ≡  c @>> (M <@> id)
```

### 12.7 The Rank-0 Convergence Theorem

The `method_for`/`object_for` duality has a deeper consequence: at rank-0, the two constructions collapse to identical semantics.

**Theorem 12.2 (Rank-0 Convergence):** For any rank-0 function `f : T^0 → T^0 → T^0`:

```blade
object_for(f) <@> (A, B)  ≡  method_for(A, B) <@> f
```

*Proof:* We derive this from the double metamorphism structure (§2.7).

At rank-0, the index space has cardinality 1 (a single point) or 0. For cardinality 1:

- Index-cata: structure a singleton → trivial (nothing to structure)
- Index-ana: emit the single index → trivial (just emit it)
- Feedback: the single index "feeds back" but there's no next iteration
- The index metamorphism executes exactly once, trivially

What remains is a single pass through the data metamorphism:

```
data-cata(inputs, i₀) → data-homo(f) → data-ana(output, i₀)
```

Now, `method_for(A, B)` commits data-cata. `object_for(f)` commits data-homo.

With the index metamorphism trivial, the full structure is just:

```
data-cata → data-homo → data-ana
```

Both curryings, when completed, specify this same linear chain. The only difference was *which part* they committed first—but with no index dynamics, the order of commitment doesn't matter. ∎

**Corollary 12.3 (Idempotence):** Wrapping rank-0 values is idempotent:

```blade
object_for(object_for(f)) ≡ object_for(f)
method_for(method_for(A)) ≡ method_for(A)    // A rank-0
```

**Corollary 12.4 (Pseudo-Native Foundation):** `A + B` requires no commitment to `object_for` or `method_for`—both interpretations are equivalent for rank-0 `(+)`. This is the mathematical foundation for pseudo-native syntax.

#### Connection to the Duality Theorems

Rank-0 Convergence relates to the other dualities in the formalism:

| Duality | Statement |
|---------|-----------|
| S/T vs T/S (§2.6) | Iteration+indexing: two primitives→one construct vs one concept→two constructors |
| Compose-Apply (Theorem 12.1) | `(object_for(f) >>@ object_for(g)) <@> A ≡ (mloop <@> f) @>> (mloop <@> g)` |
| Rank-0 Convergence (Theorem 12.2) | `object_for(f) <@> (A,B) ≡ method_for(A,B) <@> f` when f is rank-0 |

The relationship:

- **S/T vs T/S** establishes that S/T has two constructors for one fused concept
- **Compose-Apply** shows the two constructors commute under composition
- **Rank-0 Convergence** shows the two constructors collapse at the base case

Rank-0 Convergence is the base case; Compose-Apply is the inductive case. Together they characterize when `method_for` and `object_for` are interchangeable (rank-0) versus when they provide genuinely different entry points (rank > 0).


### 12.8 Additional Combinator Identities

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

```blade
M₁ <*> M₂           ≡  M₂ <*> M₁                     (commutativity, up to index reordering)
(M₁ <*> M₂) <*> M₃  ≡  M₁ <*> (M₂ <*> M₃)           (associativity)
method_for() <*> M  ≡  M                             (identity: empty loop)
```

**Concatenation property**:

```blade
method_for(A₁, ..., Aₙ) <*> method_for(B₁, ..., Bₘ)  ≡  method_for(A₁, ..., Aₙ, B₁, ..., Bₘ)
```

**Fold equivalence**:

```blade
fold(<*>, [method_for(A₁), ..., method_for(Aₙ)])  ≡  method_for(A₁, ..., Aₙ)
```

#### Symmetry Preservation

Combinators preserve symmetry structure predictably:

```blade
σ(M <@> f)           = OutputSymmetry(M.arrays, f)
σ(C₁ <&> C₂)         = σ(C₁) × σ(C₂)               (product of symmetries)
σ(C >>= k)           = σ(k(⊥))                      (determined by continuation)
σ((M <@> f) <&!> (M <@> g))  = σ(M <@> f) × σ(M <@> g)   (fusion preserves)
```

### 12.9 Zero Elements and Control Flow

Just as `for` loops are reified into loop objects with algebraic structure, conditional control flow (`if`/`match`) can be reified into choice combinators. This enables compositional reasoning about branching computations.

#### Zero Array Tuple

The empty tuple `()` represents zero S-dimensions:

```blade
method_for() <@> f      ≡  pure (f())     // no arrays → scalar from f's zero-arity case
object_for(f) <@> ()    ≡  pure (f())     // dual construction, same result
```

This enables recursive definitions of arity-polymorphic functions:

```blade
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

```blade
zero                  // the zero kernel
M <@> zero           // produces array of zeros with M's S-dimensions
```

When applied via a loop:

```blade
M <@> zero            // produces array of zeros with shape from M's S-dimensions
object_for(zero) <@> (A, A)   // symmetric matrix of zeros
```

#### Zero Function Laws

```blade
(M <@> zero) >>= k                  ≡  M <@> zero       (left zero for >>=)
c >>= (λ_. M <@> zero)              ≡  M <@> zero       (right zero for >>=)
object_for(f) >>@ object_for(zero)  ≡  object_for(zero) (absorbs composition)
object_for(zero) >>@ object_for(f)  ≡  object_for(zero) (absorbs composition)
shape(M <@> zero)                   =  S-dims(M)        (no T-dimensions)
σ(M <@> zero)                       =  σ(M)             (symmetry from arrays only)
method_for() <@> zero               ≡  pure 0           (scalar zero)
```

#### Choice Combinator (\<\|\>)

The choice combinator selects between computations:

```
(<|>) : Computation α × Computation α → Computation α
```

**Semantics**: `c₁ <|> c₂` produces the result of `c₁` if non-zero, otherwise falls back to `c₂`.

#### Choice Laws

```blade
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

```blade
guard(true, c)              ≡  c
guard(false, c)             ≡  shape_of(c) <@> zero
guard(p, c₁ <|> c₂)         ≡  guard(p, c₁) <|> guard(p, c₂)
guard(p, c₁) <|> guard(!p, c₂)  ≡  c₁ <|> c₂          (exhaustive guards)
guard(p, guard(q, c))       ≡  guard(p && q, c)
```

#### MonadPlus Structure

With `zero` and `<|>`, computations form a **MonadPlus**:

  MonadPlus operation   Blade equivalent
  --------------------- ------------------
  `mzero`               `M <@> zero`
  `mplus`               `<|>`

The required laws are satisfied:

```
mzero >>= k        ≡  mzero                         (left zero)
mzero `mplus` m    ≡  m                             (left identity)
m `mplus` mzero    ≡  m                             (right identity)
(a `mplus` b) >>= k  ≡  (a >>= k) `mplus` (b >>= k) (left distribution)
```

#### Zero Element Summary

  -----------------------------------------------------------------------------------------------------------------
  Concept            Syntax                  Role                                        Preserves
  ------------------ ----------------------- ------------------------------------------- --------------------------
  Zero array tuple   `()` / `method_for()`   Identity for `<*>`, arity recursion base    T-dimensions from kernel

  Zero function      `zero`                  Annihilator for `>>=`, identity for `<|>`   S-dimensions from arrays
  -----------------------------------------------------------------------------------------------------------------

------------------------------------------------------------------------

## 13. Symmetry System

### 13.1 Symmetry/Commutativity States

For each (array, dimension) pair in a loop, we track a state:

```
data SymcomState = Neither | Symmetric | Commutative | Both
```

-   **Neither**: No exploitable structure at this position
-   **Symmetric**: Array dimension at this position is symmetric with the previous dimension of the same array (σᵢ\[j\] = σᵢ\[j-1\])
-   **Commutative**: Kernel is commutative in this argument and the same array appears in the previous argument position (cᵢ = cᵢ₋₁ ∧ Aᵢ = Aᵢ₋₁)
-   **Both**: Both conditions hold

### 13.2 State Computation

Given arrays A₁...Aₙ with symmetry vectors σ₁...σₙ and function commutativity c:

```blade
state(i, j) = 
    let sym = (j > 0) ∧ (σᵢ[j] = σᵢ[j-1])
    let com = (i > 0) ∧ (cᵢ = cᵢ₋₁) ∧ (Aᵢ = Aᵢ₋₁)
    match (sym, com) with
    | (false, false) → Neither
    | (true,  false) → Symmetric
    | (false, true)  → Commutative
    | (true,  true)  → Both
```

**Key insight**: Commutativity only yields triangular iteration when the *same array* appears in multiple commutative positions. Different arrays in a commutativity group don't enable triangular iteration---the validity check will fail and SymcomState won't signal commutativity.

### 13.3 Output Symmetry Inference via Lowering

Output symmetry inference is the computational realization of the lowering homomorphism `lower₂₁` (Theorems 7.9-7.12).

**Theoretical basis**: Function commutativity (Level 2 symmetry) lowers to array index symmetry (Level 1) when applied to identical arrays. The homomorphism `lower₂₁(H) = H ∩ Stab(A₁,...,Aₙ)` precisely characterizes which symmetries transfer ([Theorem 9.14](#theorem-9-14)).

**Algorithm**:

```blade
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

**From abstract symmetry to concrete index types**: The symmetry vector σ produced by `OutputSymmetry` is an abstract representation (see §5.1 Three-Level Type Model). This abstract type is then lowered to concrete index types:

  Abstract σ       Concrete Index Type
  ---------------- --------------------------
  `(1, 1)`         `SymIdx<n>`
  `(1, 2)`         `(Idx<n>, Idx<m>)`
  `(1, 1, 1)`      `FullSymIdx<3, n>`
  `(1, 1, 2, 2)`   `(SymIdx<n>, SymIdx<m>)`

The symmetry vector guides *inference*; the concrete index type determines *storage and iteration*.

### 13.4 The Symmetry Transformation (Lowering in Action)

The flow of symmetry through computation instantiates the lowering homomorphisms:

```
Input Array Symmetry   →  [read elements]  →  lower₁₀ (trivial) → consumed
Function Commutativity →  [apply to arrays] → lower₂₁ (iso if identical) → Output Symmetry
```

-   **Input symmetry quashing** ([Theorem 9.13](#theorem-9-13)): Input array symmetry is consumed when elements are read, because `lower₁₀` is trivial---all index permutations become element identity.

-   **Output symmetry generation** ([Corollary 9.12](#corollary-9-12)): Function commutativity transfers to output array symmetry via `lower₂₁`, which is an isomorphism when arrays are identical.

Both phenomena are instances of the same lowering structure---they differ only in which homomorphism applies.

------------------------------------------------------------------------

## 14. Triangular Iteration

### 14.1 Cumulative Bound Computation

For symmetric/commutative dimensions, iteration bounds subtract all prior indices in the same symmetry group:

```blade
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

### 14.2 Left-Justified Indexing

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

**Design rationale**: Standard triangular iteration (j ≥ i, k ≥ j) and left-justified iteration (shrinking upper bounds) cover the same set of canonical index tuples but produce different iteration coordinates. The standard form requires an offset calculation on every access; the left-justified form makes iteration coordinates equal storage coordinates, enabling zero-overhead writes. Random access (outside of iteration) requires a transformation, but bulk computation dominates the cost model. This coordinate system choice is non-obvious---the literature predominantly uses rising-lower-bound triangular iteration with offset formulas.

This allows `array(i)(j)(k)` to directly index into compactly allocated triangular storage.

#### 14.2.1 Two-Phase Algorithm

The transformation from arbitrary indices to storage coordinates is a two-phase algorithm:

**Phase 1 --- Fold (Canonicalize):** Sort indices within each symmetry group to canonical form.

```
foldIndices(indices, σ) =
    for each group G in symmetryGroups(σ):
        sort indices[G] in ascending order
    return indices
```

**Phase 2 --- Left-Justify:** Convert to storage coordinates by subtracting cumulative offsets.

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

1.  **Fold**: sort → (2, 5, 7)
2.  **Left-justify**: (2, 5-2, 7-5) = (2, 3, 2)
3.  **Access**: array\[2\]\[3\]\[2\]

### 14.3 Index Mapping for Access

To access a symmetrically-allocated array with arbitrary indices, apply the two-phase transformation:

```blade
index(array, [i, j, k]) where σ = ⟨1, 1, 1⟩:
    let [i', j', k'] = sort([i, j, k])           // Phase 1: canonicalize
    let i'' = i'
    let j'' = j' - i'                             // Phase 2: left-justify
    let k'' = k' - j'                             // cumulative subtraction
    return array[i''][j''][k'']
```

For mixed symmetry vectors (e.g., σ = ⟨1, 1, 2, 2⟩), each group is processed independently:

```blade
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

### 14.4 Complexity Analysis

For a fully symmetric n-dimensional tensor of extent N:

**Dense iteration**: O(Nⁿ)

**Triangular iteration**: O(Nⁿ/n!)

The n! factor comes from the volume ratio between an n-cube and an n-simplex.

**Derivation**: The number of unique elements in a symmetric tensor is the multiset coefficient:

```
((N choose n)) = (N + n - 1)! / (n! × (N - 1)!)  ≈  Nⁿ/n!  for large N
```

### 14.5 Product Symmetry Theorem

When computing over multi-dimensional arrays (e.g., lat × lon × time), the symmetry structure is richer than for 1D arrays.

**Product Symmetry S_r\^d**: For a computation with r inputs over d-dimensional arrays, product symmetry means each of the d dimensions has independent S_r symmetry. The symmetry group is the product:

```
S_r^d = S_r × S_r × ... × S_r   (d factors)
```

**Theorem (Product Symmetry)**: For a computation with product symmetry S_r\^d over arrays of extent n in each dimension:

```
Speedup = (r!)^d
```

This is exponentially better than the r! speedup from flattening to 1D.

  Configuration               Speedup
  --------------------------- ----------
  r=3, d=1 (coskewness, 1D)   6×
  r=3, d=2 (coskewness, 2D)   36×
  r=4, d=2 (cokurtosis, 2D)   576×
  r=4, d=4 (cokurtosis, 4D)   331,776×

**Design implication**: The Product Symmetry Theorem is not merely a performance result---it is the *forcing function* behind the Structural Trinity. Dimensional currying exists because flattening forfeits (r!)\^(d-1). Arity polymorphism exists because r varies across computations. Loop reification exists to represent the product-of-simplices iteration space as a composable value. The (r!)\^d speedup is the prize; the Trinity is what's required to claim it.

------------------------------------------------------------------------

## 15. Type System

### 15.1 Judgments

```
Γ ⊢ e : τ        Expression e has type τ in context Γ
Γ ⊢ L : Loop[S]  L is a loop with structure S
Γ ⊢ C : Comp[τ]  C is a computation producing type τ
```

### 15.2 Array Rules

```
Γ ⊢ T : BaseType    r ∈ ℕ    σ ∈ ℕʳ    ε ∈ ℕʳ
──────────────────────────────────────────────── (Array-Intro)
        Γ ⊢ array(T, r, σ, ε) : T^r(σ)
```

### 15.3 Function Rules

```
Γ, x₁:T₁^r₁, ..., xₙ:Tₙ^rₙ ⊢ body : T^r
metadata = (c, p, tdim)
well_formed(metadata)
──────────────────────────────────────────────── (Fun-Intro)
Γ ⊢ (fn(x₁...xₙ) where metadata -> T^r {body; expr}) : Function
```

### 15.4 Loop Object Rules

```blade
Γ ⊢ A₁ : T₁^r₁(σ₁)  ...  Γ ⊢ Aₙ : Tₙ^rₙ(σₙ)
S = computeStructure(A₁...Aₙ)
───────────────────────────────────────────── (MethodLoop-Intro)
     Γ ⊢ method_for(A₁...Aₙ) : MethodLoop[S]

Γ ⊢ f : Function
───────────────────────────────────────────── (ObjectLoop-Intro)
     Γ ⊢ object_for(f) : ObjectLoop[f]
```

### 15.5 Application Rules

```blade
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

### 15.6 Combinator Rules

```blade
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

------------------------------------------------------------------------

## 16. Operational Semantics

**Scope Note**: This section provides a high-level operational model. Full formalization of lazy evaluation semantics, fusion correctness proofs, and detailed reduction rules are deferred to future work. The current treatment establishes the conceptual framework without claiming completeness.

### 16.1 Evaluation Model

Computations are *lazy*---they build a computation graph until `|> compute` is applied.

```blade
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

The two leaf types correspond to the two loop construction paths: - **MethodLeaf**: From `method_for(arrays) <@> kernel` - **ObjectLeaf**: From `object_for(kernel) <@> arrays`

Both paths produce the same loop structure after lowering; they differ only in binding order.

### 16.2 Loop Level Types

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

-   An OpenMP-parallel loop and a non-parallel loop are different types
-   A loop with `Symmetric` state and one with `Commutative` state are different types
-   Loops over different extents are different types

### 16.3 Fusion Analysis

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

### 16.4 Compute Semantics

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

------------------------------------------------------------------------

## 17. Concrete Syntax

### 17.1 Array Declaration

**Type aliases with `type` keyword:**

```
type LatIdx = Idx<180>
type LonIdx = Idx<360>
type TimeIdx = Idx<8760>
type OceanIdx = CompoundIdx<ocean_mask>
```

**Array declarations with `let` keyword:**

```blade
// Concrete array with specific index types
let data: Array<Float like LatIdx, LonIdx, TimeIdx>

// Symmetric arrays use SymIdx<r, n>
let cov: Array<Float like SymIdx<2, n>>        // symmetric matrix
let coskew: Array<Float like SymIdx<3, n>>     // fully symmetric 3-tensor

// Block symmetry: symmetric in first two AND last two indices
let block: Array<Float like SymIdx<2, I>, SymIdx<2, K>>

// Dense (no symmetry, uses regular indices)
let matrix: Array<Float like Idx<n>, Idx<n>>
```

**Symmetric index types** (see §4.13):

| Type | Storage | Meaning |
|------|---------|---------|
| `Idx<n>, Idx<n>` | n² | Dense matrix |
| `SymIdx<2, n>` | n(n+1)/2 | Symmetric pairs |
| `SymIdx<3, n>` | n(n+1)(n+2)/6 | Symmetric triples |

**Abstract types in signatures** use `T^r(σ)` notation (see §5.1).

### 17.2 Array Literals

Arrays use bracket syntax with size inferred from the literal:

```blade
let v = [1, 2, 3]              // Array<Int like Idx<3>>
let m = [[1, 2], [3, 4]]       // Array<Int like Idx<2>, Idx<2>>
let empty : Array<Float like Idx<0>> = []  // empty needs type annotation
let typed : Array<Float like Idx<3>> = [1.0, 2.0, 3.0]
```

No separate `List` type—arrays with static size suffice for scientific computing. Dynamic collections can be a library type if needed.

### 17.3 Function Declaration

```blade
function <name>(
    <arg1>: <T1>^<r1>,
    ...
    <argN>: <TN>^<rN>
)
where
    comm(<arg_i>, <arg_j>, ...),
    omp(<arg_k>: <depth>, ...),
    tdim(
        { extent: <expr>, symm: <k>, name: "<dim>" },
        ...
    )
-> <T>^<r>                   // optional return type check
{
    <body>
    <expr>                   // final expression is return value
}
```

**Parallelism semantics**: `omp(a: 2, b: 1)` means "parallelize 2 levels of S-dimension loops from array a, and 1 level from array b." Since array arguments are bound in order, their S-dimension loops nest in that order---a's loops are outermost, making them natural parallelization targets. The `omp` clause can be substituted with other backends (e.g., `acc` for OpenACC).

**T-dimension semantics**: Each T-dimension is specified as a record with `extent` (size expression), `symm` (symmetry class), and `name` (optional label). Dimensions with the same `symm` value are interchangeable.

### 17.4 Lambda Expressions

Pure anonymous functions use the `lambda` keyword with arrow syntax:

```blade
lambda(args...) -> expression
```

**Examples:**

```blade
// Single argument
lambda(x) -> x * x

// Multiple arguments
lambda(a, b) -> a + b

// With tuple destructuring (single tuple argument)
lambda((x, y, z)) -> x * x + y * y + z * z

// Three separate arguments (not a tuple)
lambda(x, y, z) -> x + y + z

// Nested lambdas (currying)
lambda(x) -> lambda(y) -> x + y
```

Lambda functions are always pure: no side effects, same output for same input. They can be used anywhere a function is expected, including as kernel arguments to `method_for` and `object_for`.

**Type inference**: Lambda argument types are inferred from context:

```blade
method_for(A, B) <@> lambda(a, b) -> a + b
// Types of a, b inferred from element types of A, B
```

### 17.5 Static Values and Functions

The `static` keyword indicates compile-time evaluable values and functions:

```blade
// Static values: evaluated at compile time
static spec = [(L0e, 16), (L1o, 8), (L2e, 4)]
static num_blocks = 3
static total_dim = 60

// Static functions: evaluated at compile time if args are static
static function dim(ir : Irrep) : Nat = match ir {
    L0e -> 1, L0o -> 1,
    L1e -> 3, L1o -> 3,
    L2e -> 5, L2o -> 5,
    ...
}
```

**Properties:**

- Static values can be used in type construction (e.g., index type parameters)
- Static functions are evaluated at compile time when given static arguments
- `static` implies purity (no side effects, no I/O)
- Type providers (e.g., `NetCDF<"file.nc">`) are separate from `static`—they assume files are static as a build prerequisite

**Use in index types:**

```blade
static n = 1000
let A: Array<Float like Idx<n>>  // n is compile-time known

static mask_data = load_mask("ocean.nc")  // compile-time file load
type OceanIdx = CompoundIdx<mask_data>
```

### 17.6 Type-Returning vs Value-Returning Functions

Type-level and value-level functions use different syntax:

```blade
// Type-returning: angle brackets, result is a type
static type Vec<N : Nat> = Array<Float like Idx<N>>
static type RepIdx<L : Nat, P : Parity> = Idx<2*L + 1, P>

// Value-returning: parentheses, result is a value  
static function dim(L : Nat) -> Nat = 2 * L + 1
function dot(a : Vec<3>, b : Vec<3>) -> Float = sum(a * b)
```

**Calling convention:**

```blade
Vec<3>              // type application: returns a type
dim(2)              // function call: returns a value (5)

// In type position
let v : Vec<3> = [1.0, 2.0, 3.0]
let m : Array<Float like Idx<dim(2)>> = ...  // dim(2) evaluates to 5
```

**Static type functions** are evaluated at compile time and can be used wherever a type is expected. They cannot be:
- Stored in variables
- Passed as runtime arguments
- Returned from runtime functions

This restriction keeps the type system decidable.


---

### 17.7 Mutability and Borrowing

**Local variables** are mutable within their scope:

```blade
let x = 10
x += 5              // OK: locals are mutable

static N = 100
N += 1              // ERROR: static values are immutable
```

**Function parameters** are borrowed immutably by default:

```blade
// Immutable borrow (default) - can read, can't mutate
function foo(x : Int, arr : Array<Float like Idx<N>>) {
    let y = x + arr(0)  // OK: reading
    x += 1              // ERROR: immutable parameter
    arr(0) = 99         // ERROR: immutable parameter
}

// Mutable borrow - can read and mutate, changes persist
function bar(arr : mut Array<Float like Idx<N>>) {
    arr(0) = 99         // OK: mutable parameter
}

// Static parameter - compile-time constant, usable in types
function baz(N : static Nat) -> Array<Float like Idx<N>> {
    zeros<Idx<N>>()
}
```

**Summary:**

| Context | Syntax | Meaning |
|---------|--------|---------|
| Local variable | `let x = ...` | mutable in scope |
| Local constant | `static x = ...` | immutable (compile-time) |
| Parameter (default) | `x : T` | immutable borrow |
| Parameter mutable | `x : mut T` | mutable borrow |
| Parameter static | `x : static T` | compile-time constant |

All parameters are passed by reference. If a function takes `mut`, mutations persist after the call. If not, caller's data is safe.

### 17.8 Boolean Operators and Comparisons

**Comparisons** (return Bool):

```blade
a == b    // equality
a != b    // inequality
a < b     // less than
a > b     // greater than
a <= b    // less than or equal
a >= b    // greater than or equal
```

**Boolean operators** (short-circuit evaluation):

```blade
a && b    // and: if a is false, b not evaluated
a || b    // or: if a is true, b not evaluated
!a        // not
```

No `and`/`or`/`not` keywords—symbols only. Bitwise operators (`&`, `|`) not included.

### 17.9 Fused Assignment Operators

```blade
x += 1      // x = x + 1
x -= y      // x = x - y
x *= 2.0    // x = x * 2.0
x /= scale  // x = x / scale
```

Works on array elements:

```blade
A(i) += 1.0          // increment element
A(i, j) *= scale     // scale element
```

### 17.10 Conditionals and Pattern Matching

**Pattern matching** uses `match ... with` syntax:

```blade
// Basic value matching
match x with
| 0 -> "zero"
| 1 -> "one"
| _ -> "other"

// Pattern matching on tuples
match point with
| (0, 0) -> "origin"
| (0, y) -> "on y-axis"
| (x, 0) -> "on x-axis"
| (x, y) -> "general"

// Matching with guards
match n with
| x if x < 0 -> "negative"
| x if x > 0 -> "positive"
| _ -> "zero"

// Multi-statement branches use braces
match x with
| 0 -> {
    let y = compute()
    y + 1
}
| _ -> default_value
```

**Match is an expression** (returns a value):

```blade
let result = match x with
| 0 -> "zero"
| _ -> "other"
```

**If/then/else** is syntactic sugar for matching on Bool:

```blade
if condition then expr1 else expr2

// Equivalent to:
match condition with
| true -> expr1
| false -> expr2
```

### 17.11 Tuple Syntax

**Tuple literals** use parentheses and commas:

```blade
let t = (1, "hi", true)        // type is (Int, String, Bool)
let point = (3.0, 4.0)         // type is (Float, Float)
```

**Tuple access** is only through destructuring—no positional index syntax:

```blade
// Destructuring
let a, b, c = t

// Partial destructuring with wildcards
let x, _, _ = t                // only need first element
let _, y, _ = t                // only need second element

// In function returns
function divmod(a : Int, b : Int) -> (Int, Int) {
    (a / b, a % b)
}
let quotient, remainder = divmod(17, 5)

// As function argument
function distance(p : (Float, Float)) -> Float {
    let x, y = p
    sqrt(x*x + y*y)
}
```

**Parsing rules:**

```
()           → unit            // empty tuple (unit type)
(e)          → e               // parenthesized expression (grouping only)
(e₁, e₂)     → (e₁, e₂)        // 2-tuple
(e₁, e₂, e₃) → (e₁, e₂, e₃)    // 3-tuple, etc.
```

Single-element parentheses are grouping, not 1-tuples. This matches Python semantics.

**In type definitions:**

```blade
type Point2D = (Float, Float)
type IrrepSpec = (Nat, Parity, Nat)
```

**Usage in object_for path:**

```blade
object_for(f) <@> ()       // zero arrays - Unit case
object_for(f) <@> A        // one array - Ref case  
object_for(f) <@> (A, B)   // two arrays - Tuple case
object_for(f) <@> (A, B, C) // three arrays - Tuple case
```

**Nested tuples:**

```blade
object_for(f) <@> ((A, B), C)   // nested - f sees 2 top-level args
method_for(A, B, C)             // always flat - method_for doesn't nest
```

### 17.12 Sum Types (Variants)

Sum types use `|` to separate variants:

```blade
// Simple enumeration (no data)
type Bool = True | False
type Direction = North | South | East | West

// Variants with data use colon
type Option<T> = Some : T | None
type Result<T, E> = Ok : T | Err : E

// Mixed
type Tree<T> = 
    | Leaf : T 
    | Node : (Tree<T>, Tree<T>)

type Shape = 
    | Circle : Float 
    | Rectangle : (Float, Float)
    | Point
```

**Pattern matching on sum types**:

```blade
let area = match shape with
| Circle(r) -> pi * r * r
| Rectangle(w, h) -> w * h
| Point -> 0.0
```

**Usage**:

```blade
let x : Option<Int> = Some(42)
let y : Option<Int> = None

match x with
| Some(v) -> v + 1
| None -> 0
```

### 17.13 Structs

Structs are data types with named fields. No methods, no inheritance.

```blade
struct Point {
    x : Float,
    y : Float
}

// Construction
let p = Point { x = 1.0, y = 2.0 }

// Field access
let dx = p.x

// Destructuring
let Point { x, y } = p

// Functional update (creates new struct)
let p2 = Point { x = 3.0, ..p }  // keeps p.y
```

### 17.14 Interfaces

Interfaces define method signatures. No implementation, no data.

```blade
interface Measurable {
    function measure(self) -> Float
}

interface Transformable {
    function transform(self, t : Transform) -> Self
}

// Multiple interfaces
interface PhysicalObject : Measurable, Transformable {
    function mass(self) -> Float
}
```

**Implementation** is separate from definition:

```blade
struct Circle { radius : Float }

impl Measurable for Circle {
    function measure(self) -> Float {
        2.0 * pi * self.radius
    }
}
```

### 17.15 Loop Construction and Application

#### Core Constructs

```blade
let <loop> = method_for(<A₁>, ..., <Aₙ>)
let <loop> = object_for(<f>)

let <comp> = <loop> <@> <f>
let <comp> = <loop> <@> (<A₁>, ..., <Aₙ>)
```

#### For-Loop Syntax

The `for` keyword provides clean dual syntax over `method_for`/`object_for`:

```blade
// method_for style (arrays/indices left, kernel right)
for (A, B) <@> lambda(a, b) -> a * b
for (A, B) in SymIdx<2,N> <@> lambda(i, j, a, b) -> f(i, j, a, b)
for SymIdx<2,N> <@> lambda(i, j) -> i * j

// object_for style (kernel left, arrays/indices right)
for lambda(a, b) -> a * b <@> (A, B)
for lambda(i, j, a, b) -> f(i, j, a, b) <@> (A, B) in SymIdx<2,N>
for lambda(i, j) -> i * j <@> SymIdx<2,N>
```

#### Let-Bound Loops

```blade
// Bind arrays, await kernel
let loop = for (A, A) in SymIdx<2,N> where comm
loop <@> lambda(i, j, a, b) -> a * b

// Bind kernel, await arrays  
let op = for lambda(a, b) where comm -> a * b
op <@> (X, X)
op <@> (Y, Z)

// Arity-polymorphic
let moment = for lambda(is, xs) where poly(is, xs), comm(xs) -> product(xs)
let cov = moment <@> (data, data)
let coskew = moment <@> (data, data, data)
```

#### Virtual Arrays

```blade
range<I>              // standard iteration over index type I
reverse<I>            // reverse order
blocked<I, K>         // K-sized cache blocks
where<I>(mask)        // sparse iteration where mask is true
```

### 17.16 Combinators

```blade
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

------------------------------------------------------------------------

### 17.17 Poly-Indexing Syntax

**Standard indexing (sequential application):**

```
A(i)           -- curry first dimension
A(i)(j)        -- curry first two dimensions
A(i)(j)(k)     -- full index (if rank 3)
```

**Poly-indexing (tuple application):**

```
A(indices)     -- indices : Tuple of indices, length = rank(A)
```

**Index tuple construction:**

```blade
let indices = (i, j, k)              -- explicit tuple
let indices = replicate(i, rank(A)) -- replicated index (e.g., diagonal)
let indices = all_indices(A)        -- iterator over all valid tuples
```

**Rank-polymorphic iteration:**

```
for indices in all_indices(A) {
    ... A(indices) ...
}
```

The `all_indices(A)` iterator generates all valid index tuples for array A, respecting its structure (dense, ragged, symmetric, etc.).

**Lambda indices:**

```
A(Dual(i, 1.0))     -- AD dual number index
A(Symbolic("i"))    -- symbolic/deferred index
A[offset(k)]        -- computed stencil offset
```

All resolve to the structural index type; computational indices wrap the address with additional information.

------------------------------------------------------------------------

### 17.18 Pseudo-Native Mathematics

Blade provides a pseudo-native mathematical surface where common operations use conventional notation while preserving full access to the S/T substrate.

#### 17.7.1 Foundation: The Rank-0 Collapse

Pseudo-native binops work because of Theorem 12.2 (Rank-0 Convergence): at rank-0, `method_for` and `object_for` collapse to identical semantics. This means `A + B` requires no commitment to either constructor—both interpretations are equivalent for rank-0 `(+)`.

The paradigm boundary is not at binops—singleton collapse erases that distinction. The boundary is **rank**:

| Rank | Behavior |
|------|----------|
| Rank-0 | Singleton collapses, wrappers transparent, no iteration structure |
| Rank > 0 | Iteration structure exists, `method_for`/`object_for` define traversal |

#### 17.7.2 Primitive Operations

Pseudo-native syntax covers element-wise operations with signature `T^0 × T^0 → T^0`:

```blade
a + b      // object_for((+)) — element-wise addition
a - b      // object_for((-)) — element-wise subtraction
a * b      // object_for((*)) — element-wise multiplication
a / b      // object_for((/)) — element-wise division
-a         // object_for(negate) — element-wise negation
```

These lift automatically over arrays via `object_for`.

| Syntax | Types | Result | Operation |
|--------|-------|--------|-----------|
| `a * b` | `T^0 × T^0` | `T^0` | scalar multiplication |
| `a * b` | `T^1 × T^1` | `T^1` | element-wise multiplication |
| `a * b` | `T^2 × T^2` | `T^2` | element-wise multiplication |

The primitive `(*)` is always element-wise. Rank does not change the operation's meaning.

#### 17.7.3 Built Operations

Contractions and reductions are named functions built from primitives:

```blade
function sum(a: T^1) -> T^0 {
    reduce((+), a)
}

function dot(a: T^1, b: T^1) -> T^0 {
    sum(a * b)
}

function matvec(A: T^2, b: T^1) -> T^1 {
    method_for(A) <@> lambda(row) -> dot(row, b)
}

function matmul(A: T^2, B: T^2) -> T^2 {
    method_for(A) <@> lambda(row) -> matvec(transpose(B), row)
}
```

| Function | Types | Result | Built from |
|----------|-------|--------|------------|
| `sum(a)` | `T^1` | `T^0` | `reduce((+), a)` |
| `dot(a, b)` | `T^1 × T^1` | `T^0` | `sum(a * b)` |
| `matvec(A, b)` | `T^2 × T^1` | `T^1` | `method_for` + `dot` |
| `matmul(A, B)` | `T^2 × T^2` | `T^2` | `method_for` + `matvec` |

#### 17.7.4 Extension Operator

The `<*>` combinator provides outer products:

```blade
a : T^1, b : T^1    →  a <*> b : T^2     // outer product
```

| Syntax | Operation | Rank effect |
|--------|-----------|-------------|
| `a * b` | Element-wise | Preserved |
| `a <*> b` | Outer product | Increased |
| `dot(a, b)` | Contraction | Reduced |

#### 17.7.5 Structural Operations

Operations requiring explicit structure use full Blade syntax:

```blade
// Outer products
a <*> b

// Reductions
sum(a)
reduce((+), a)

// Contractions
dot(a, b)

// Custom iteration structure
method_for(A, B, C) <@> kernel

// Symmetry exploitation
method_for(A, A, A) <@> f where comm(a, b, c)

// Loop composition
M1 <*> M2
O1 >>@ O2

// Parallel/fused execution
c1 <&> c2
(M <@> f) <&!> (M <@> g)
```

#### 17.7.6 Pseudo-Native Inside Kernels

Pseudo-native syntax appears inside kernels:

```blade
let result = method_for(A, A) <@> lambda(a, b) -> {
    a * b    // element-wise at whatever rank a, b have after currying
} where comm(a, b) |> compute
```

The S/T structure (`method_for`, `comm`, `compute`) is explicit at the top level. Inside the kernel body, pseudo-native operations are concise and readable.

#### 17.7.7 Equivariance in Pseudo-Native Operations

Pseudo-native operations carry equivariance information:

```blade
a : V, b : V     // vectors with SO(3) equivariance

a * b            // : V, element-wise, equivariance preserved
dot(a, b)        // : S, scalar, invariant (contraction)
a <*> b          // : T2, transforms as L0 ⊕ L1 ⊕ L2

c : S            // scalar
c * a            // : V, scalar scales vector
```

The compiler infers equivariance through expressions. If inference fails, the result is non-equivariant (gradual adoption, not error).


### 17.19 Named Infix Operators

Named infix operators use the `:name:` syntax:

```blade
a :name: b    // parses as name(a, b)
```

**Properties:**

- All named infixes have uniform **lowest precedence** (below all native operators)
- Left-associative: `a :op: b :op: c` parses as `(a :op: b) :op: c`
- Requires parentheses when mixing with `*`, `+`, etc.

**Example:**

```blade
// Tensor product of representations
let rho = ((2 * L1o) :tp: (3 * L2e)) + (1 * L0e)

// Without parens, "2 * L1o :tp: 3 * L2e + 1 * L0e" 
// would parse as "(2 * L1o) :tp: ((3 * L2e) + (1 * L0e))"
// because :tp: binds looser than + and *
```

Named infixes provide domain-specific notation without requiring language-level operator extensions.



## 18. Future Work

Planned extensions and research directions are documented separately in **Blade Extensions Spec**. Topics include:

- Automatic differentiation preserving factorial speedups
- Tree and graph data structures via trace indices
- Non-deterministic iteration for Monte Carlo and message-passing
- Domain decomposition for distributed computing
- Stencil operations and halo exchange
- ML framework integration

## 19. Related Work

### 19.1 Array Languages and Rank Polymorphism

All systems in this section operate under **T/S (collection-first)** orientation---they treat arrays as collections and derive iteration from element operations. Blade's **S/T (structure-first)** orientation represents a paradigm inversion: iteration structure is primary, element operations secondary.

**APL/J/K**: Pioneered implicit iteration via rank polymorphism. Functions automatically lift across array ranks. Loops are invisible---iteration is never reified. Quintessentially T/S: the programmer specifies element operations; iteration is derived.

**Remora** (Slepak et al., 2014): Formalizes rank polymorphism with frame/cell decomposition. A function of type `T^m → T^n` lifts to `T^(m+k) → T^(n+k)` by mapping over k-frames. Does not address varying arity, symmetry, or triangular iteration. T/S orientation with rigorous type theory.

**Dex** (Paszke et al., 2021): Treats arrays as memoized functions; `for` builds arrays eagerly. The insight that "arrays are functions" parallels our dimensional currying. However, Dex loops are syntax (the `for` construct), not first-class composable values with algebraic laws. Dex focuses on typed indices and automatic differentiation. Still T/S: the `for` construct iterates over collections.

**Futhark** (Henriksen et al., 2017): Purely functional GPU programming with nested parallelism. Uses SOACs (Second-Order Array Combinators) like `map`, `reduce`, `scan` for parallelism. Plain loops are explicitly sequential. Nested parallelism is handled by "incremental flattening"---a compiler transformation, not user-facing loop composition. T/S with sophisticated compiler optimizations.

**Paradigm observation**: These systems span 60 years of array language design, yet all share T/S orientation. This is not because T/S is optimal---it reflects the historical path from FORTRAN's element-centric loops. S/T was simply never explored.

### 19.2 Loop Abstractions and Scheduling

**Polyhedral Model** (Feautrier, Bastoul, Bondhugula et al.): Represents loop nests as integer polyhedra for compiler analysis and transformation. Extremely powerful for automatic parallelization and optimization. However, polyhedra are *compiler IR*, not user-facing abstractions. Users don't compose polyhedra; compilers analyze and transform them.

**Halide** (Ragan-Kelley et al., 2013): Separates algorithm from schedule. The algorithm defines *what* to compute; the schedule defines *how* (loop order, tiling, fusion). Schedules are *directives* (`.split()`, `.tile()`, `.compute_at()`) applied to function definitions, not first-class composable values. No algebraic laws govern schedule composition.

**Distinction**: Blade's loop objects are first-class values with algebraic combinators (`<*>`, `<&!>`, `>>@`) satisfying provable laws (MonadPlus structure). Halide schedules are imperative modifications; polyhedral representations are compiler-internal.

### 19.3 Parallel Loop Constructs

**Kokkos/RAJA**: Parallel loop abstractions for HPC with portability across backends. Focus on `parallel_for`, `parallel_reduce`---single loops, not nested loop composition.

**OpenMP**: Pragma-based parallelization. Loops remain syntax; pragmas are annotations.

**Distinction**: These systems parallelize individual loops. Blade reifies *nested* loop structures as single composable values where arity determines nesting depth.

### 19.4 Multi-Dimensional Homomorphisms

**Multi-Dimensional Homomorphisms (MDH)** (Rasch & Gorlatch, 2018; Rasch, 2024): An algebraic formalism for data-parallel array computation. MDH expresses computations via higher-order functions with associative combine operators, enabling divide-and-conquer decomposition. Like Blade, MDH uses algebraic properties of operators to drive optimization and provides a formal foundation for array computation rather than ad-hoc transformations. The key difference: MDH's "homomorphisms" are list homomorphisms (functions distributing over concatenation); Blade's symmetry lowering uses group homomorphisms (maps between permutation groups). MDH does not address symmetric tensors or triangular iteration.

### 19.5 Tensor Compilers

**TACO** (Kjolstad et al., 2017): Format abstraction for sparse tensors. Generates code for arbitrary sparse formats via iteration graph algebra. Addresses *sparsity*, not *symmetry*.

**TVM** (Chen et al., 2018): End-to-end ML compiler with auto-tuning. Extends Halide's scheduling to deep learning workloads. Schedule-based, not combinator-based.

### 19.6 Scientific Python Ecosystem

**xarray**: Labeled multi-dimensional arrays with NetCDF interoperability. No symmetry support.

**Dask**: Lazy evaluation and parallel/distributed computation via task graphs. Graph optimization, not algebraic fusion.

**Complementary usage**: Blade-DSL can consume data from xarray and integrate with Dask for distribution.

### 19.7 Sparse and Masked Array Systems

**scipy.sparse**: COO, CSR, CSC formats for 2D sparse matrices. Runtime coordinate storage, no type-level identity, no currying.

**TileDB**: Multi-dimensional sparse arrays with R-tree indexing. Efficient storage and slicing, but no compile-time type safety, no currying, no type-level mask identity.

**pandas/xarray MultiIndex**: Tuple-of-arrays indexing for hierarchical dimensions. Key differences from Blade's `CompoundIdx`:

  ------------------------------------------------------------------------------------------
  Aspect              xarray MultiIndex              Blade CompoundIdx
  ------------------- ------------------------------ ---------------------------------------
  Type signature      None (runtime)                 `N -> N -> ...` (compile-time, k-ary)

  Identity check      O(n) array comparison          O(1) whole-mask hash

  Coordinate lookup   O(n) or O(log n) search        O(1) hash lookup

  Partial indexing    `.sel(lat=x)` runtime filter   `arr[(lat, _)]` compile-time typed

  Currying            No (flat structure)            Yes (wildcard `_` preserves currying)

  Storage             Tuple-of-arrays + data         Contiguous data + hash tables

  Cache order         Not enforced                   Enforced (can't skip dimensions)
  ------------------------------------------------------------------------------------------

**Dex index types**: Sophisticated typed indices with arrays as memoized functions. However, Dex focuses on dense iteration patterns with no mask-derived sparse index types, no curryable compound indices (`N -> N -> ...`), and no wildcard partial indexing.

**Key distinction**: Blade's `CompoundIdx` combines (1) mask-derived type identity via whole-mask hash, (2) curryable type signature matching mask rank, (3) wildcard partial indexing producing well-typed intermediates, and (4) O(1) coordinate lookup via per-element hashing. This combination appears to be novel--individual pieces exist, but not integrated into a coherent index type system.

### 19.8 Novelty and Impact Assessment

#### Features and Paradigms

  ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
  Contribution                                   N            I       Notes
  ----------------------------------------- ------------ ------------ -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
  **S/T paradigm (§2)**                         9.5          9.5      Structure-first orientation where iteration is primary, operations secondary. No prior system embraces this. Mathematically required for (r!)\^d speedup (see Theorems below).

  **S/T ↔︎ T/S duality (§2.6)**                  7.5          8.0      T/S combines two primitives (iteration, indexing) into one construct (2→1). S/T fuses them into one concept with two constructors (1→2). Explanatory framing that makes the paradigm teachable.

  **Loop reification**                          9.0          9.0      Part of **Structural Trinity** (§9.6). Nested loops as composable first-class values. **Prior art**: Polyhedral (isl, MLIR) reify as compiler IR; Halide schedules are directives.

  **Arity polymorphism**                        9.0          9.0      Part of **Structural Trinity** (§9.6). Arity determines output rank, loop depth, AND symmetry. **Prior art**: Variadic functions have fixed output types.

  **Dimensional currying**                      9.0          9.0      Part of **Structural Trinity** (§9.6). Arrays as N-ary functions; partial indexing yields lower-rank arrays. **Prior art**: Dex has arrays-as-functions but no dependent extents.

  **Poly-indexing (§10.4, §17.6)**               7.5          8.5      Variable-length anonymous index tuples enabling rank-polymorphic operations. Natural extension of arity polymorphism to indices. High practical impact: eliminates per-rank code duplication.

  **Combinator lifting (§9.6.4)**               7.5          8.5      `method_for(<&>)` and `object_for(>>)` extend duality to combinators. Enables dynamic kernel construction. Level 3+ collapses to Level 2 via first-class functions.

  **Left-justified triangular iteration**       7.5          8.5      Chooses orientation where iterator position = storage position, eliminating offset calculation on writes. Engineering insight atop theoretical foundation.

  **MonadPlus structure (§12.8)**               7.0          7.5      Computations form MonadPlus with `zero` and `<|>`. Enables algebraic reasoning about conditional/fallback computation patterns. Standard structure, novel application domain.

  **Dependent index types (§4)**                6.2          7.5      Index types depend on file metadata or runtime arrays. Tagged indices for staggered grids. Valuable but incremental over existing dependent type work.

  **CompoundIdx with currying (§4.4)**          7.0          8.0      Curryable `N → N → ...` signature and typed wildcard partial indexing are novel. Type-level integration, not just runtime mechanisms.

  **Overall (features)**                      **8.8**      **9.0**    Coherent feature set where each component enables the others.
  ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

#### Theorems and Proofs

  ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
  Contribution                                        N            I       Notes
  ---------------------------------------------- ------------ ------------ -----------------------------------------------------------------------------------------------------------------------------------------------------------------
  **Iteration Object Impossibility (Thm 2.3)**       9.0          9.5      T/S cannot express first-class iteration objects. T-dimensions are relational ([Lemma 2.2](#lemma-2-2)), preventing kernel-independent iteration specification.

  **Syntactic Impossibility (Thms 2.5--2.7)**        8.0          8.5      Fixed text cannot express variable-arity triangular iteration. Lexical scoping requires r nested loops for arity r.

  **Recursion Insufficient (Thm 2.8)**               8.0          8.5      Recursive encoding makes structure implicit and uninspectable. Connects to runtime necessity.

  **Reification Necessity (Thm 2.9)**                8.5          9.0      Loop structures must be first-class runtime values. Bridges syntactic impossibility to runtime representation.

  **S/T Necessity (Thm 2.10)**                       9.0          9.0      S/T orientation is necessary and sufficient for the Structural Trinity. Two independent lemmas (symmetry tower recursion, runtime selection).

  **Index Anonymity (Cor 2.10.1)**                   8.5          8.0      Arity-polymorphic kernels require anonymous indices, impossible with compile-time identifiers.

  **Trinity Theorems (Thms 7.1--7.6)**               9.0          9.0      Six theorems proving pairwise dependencies between Trinity components. Full cycle established.

  **Trinity Inseparability (Thm 9.7)**               9.5          9.5      Loop reification, arity polymorphism, and dimensional currying are mutually necessary. Full dependency cycle.

  **Symmetry Lowering (Thms 7.9--7.15)**             8.5          8.5      Homomorphism tower (Level 2 → 1 → 0) unifies output symmetry generation and input symmetry quashing. Stabilizer formula characterizes partial transfer.

  **Product Symmetry Theorem (§14.5)**               6.5          9.0      Math is standard combinatorics. Contribution: recognizing it as PL design constraint.

  **Compose-Apply Duality (Thm 12.1)**               7.0          7.5      Compose-then-apply equals apply-then-compose. Clean algebraic result.

  **Variadic Insufficiency (§10.6.1)**                8.0          8.0      Variadic typing has fixed output type; Blade requires arity-dependent output type.

  **Overall (theorems)**                           **9.0**      **9.2**    Multiple independent impossibility proofs converge on S/T necessity. Trinity inseparability via complete cycle.
  ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

**N** = Novelty (1-10), **I** = Impact (1-10)

#### Projected with Full Implementation

  -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
  Contribution                                 N            I       Notes
  --------------------------------------- ------------ ------------ -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
  Product-simplex decomposition (§19.3)       7.5          8.5      (n+1)\^d branching in native triangular space. Clean generalization but follows from product structure.

  Triangular distributed execution            8.2          9.2      End-to-end triangular preservation: iteration → storage → distribution. No existing system maintains triangular structure through the full stack.

  Triangular file format (§19.4)              6.8          8.2      Block-aligned I/O for symmetric tensors. Necessary infrastructure, moderate novelty.

  AD + domain decomposition                   7.8          8.5      Gradient halo exchange at simplex boundaries. Non-trivial but follows from combining AD with decomposition.

  AD + stencils with symmetry                 7.2          7.5      Symmetric scatter-add for stencil gradients. Open question in formalism.

  Trees as generalized arrays                 7.5          6.5      Arrays are trees with fixed depth/branching. Path-based indexing, flat storage with precomputed bijection, O(k) access without pointer chasing. Novel combination but unclear killer application.

  Symmetric trees                             8.0          5.5      Commutative children analogous to symmetric tensors. Storage reduction via canonical ordering. Genuinely novel---no prior work found---but speculative, needs concrete use case (phylogenetics? unordered ASTs?).

  **Overall (fully realized)**              **9.2**      **9.6**    Petabyte-scale differentiable symmetric tensor computation. End-to-end triangular coherence. Poly-indexing enables rank-polymorphic libraries. Tree generalization suggests broader applicability beyond tensors.
  -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

------------------------------------------------------------------------

## 20. Conclusion

### 20.1 Summary of Results

This document establishes the theoretical foundations for Blade-DSL, a domain-specific language for symmetric tensor computation. The key results form a chain of necessity:

```
(r!)^d speedup
    ↓ requires
Triangular iteration (identity ∧ commutativity)
    ↓ requires (§9.7)
Two entry points: method_for, object_for
    ↓ requires (§2.11)
Type-level symmetry tracking + propagation
    ↓ is
S/T orientation
    ↓ requires (Theorem 9.7)
The inseparable Trinity
    ↓ provided by
Blade
```

### 20.2 What We Proved

1. **method_for/object_for are unique** (Theorem 9.29) — the only decomposition enabling symmetry detection + composition

2. **S/T is inevitable** (Theorem 2.15) — T/S cannot achieve zero-cost; metaprogramming rebuilds S/T

3. **Type-level symmetry required** (Theorem 2.12) — runtime tracking induces per-iteration branching

4. **Trinity is inseparable** (Theorem 9.7) — loop reification, arity polymorphism, dimensional currying

5. **Blade is canonical** — exactly the required features, no more, no less

6. **Alternatives isomorphic** (Theorem 2.14) — any equivalent approach is Blade in different syntax

### 20.3 Blade's Canonicity

Parts I (§9.7) and II (§2.11) force specific design requirements:

| Requirement | Source | Blade Solution |
|-------------|--------|----------------|
| Two entry points | Maximal Unbundling (Theorem 9.26) | `method_for`, `object_for` |
| Type-level symmetry | Inevitability (Theorem 2.15) | `SymIdx<n>`, commutativity annotations |
| Arity polymorphism | Variable r | Poly-tuples, arity-dependent types |
| Dependent bounds | Triangular iteration | Loop reification, symcomState |
| Symmetry propagation | Composition | Inference through `<@>`, `<*>`, `>>@` |

**Theorem 20.1 (Blade Necessity and Uniqueness):**

For zero-cost `(r!)^d` speedup:

1. S/T is necessary (Theorem 2.15)
2. method_for/object_for are necessary (Theorem 9.29)
3. Type-level symmetry is necessary (Theorem 2.13)
4. The Trinity is necessary (Theorem 9.7)
5. Blade provides exactly these
6. Alternatives are isomorphic or inferior

*Proof:* Each component is either mathematically fixed or has Blade as minimal solution:

- Entry points: exactly two (Theorem 9.26). Names vary; structure cannot.
- Type-level symmetry: required (Theorem 2.15). Representation varies; compile-time tracking cannot.
- Arity polymorphism: output depends on input count. Blade uses poly-tuples; alternatives are more complex or less expressive.
- Dependent bounds: Blade uses runtime loop objects; alternatives (type-level naturals, staging) are isomorphic or more complex.
- Propagation: determined by symmetry tower mathematics, not design choice. ∎

### 20.4 Metaprogramming Isomorphism

Any metaprogramming approach achieving zero-cost factorial speedup is isomorphic to Blade:

| Blade | C++ Templates |
|-------|---------------|
| `method_for` | Variadic template loop builder |
| `object_for` | Kernel template with commutativity tag |
| `SymIdx<n>` | `std::integral_constant<bool, symmetric>` |
| `<@>` application | Template instantiation |
| Type inference | Template argument deduction |

The metaprogramming approach is Blade embedded in C++ types—same power, more implementation overhead.

### 20.5 Final Statement

**Blade is not merely *a* way to achieve `(r!)^d` speedup on symmetric tensors. It is *the* way.**

The mathematical structure forces specific design choices. Blade makes them explicit. Alternatives either rebuild Blade (isomorphic), accept overhead (inferior), or restrict arity (less general).

There is no simpler design with the same capability.

------------------------------------------------------------------------

## Appendix A: Notation Summary

  ----------------------------------------------------------------------------------------------
  Symbol                            Meaning
  --------------------------------- ------------------------------------------------------------
  T\^r(σ)                           Array type: element T, rank r, symmetry σ

  method_for                        S-first loop constructor

  object_for                        Function-first loop constructor

  ()                                Zero array tuple (identity for \<\*\>)

  zero                              Zero function (identity for \<\|\>, annihilator for \>\>=)

  \<@\>                             Application combinator

  \>\>=                             Monadic bind

  \<&\>                             Parallel composition

  \<&!\>                            Mandatory fusion

  \>\>@                             Compose ObjectLoops (compose-then-apply)

  @\>\>                             Compose within MethodLoop (apply-then-compose)

  \<\*\>                            Array product (MethodLoop concatenation)

  \<\$\>                            Functor map

  \<\|\>                            Choice combinator (MonadPlus, computation-level)

  \<\|:\>                           Array fallback combinator (first non-null allocation)

  Tuple(...)                        Product type, stays bundled in kernel

  AlignedExpr                       Wrapped zip + stencil metadata, unpacks to separate args

  zip                               Array tuple combinator (n-ary, produces Tuple elements)

  align                             Wrap arrays with stencil spec (produces AlignedExpr)

  stencil                           Sugar for align + shift

  stack                             Combine arrays along new leftmost dimension (n-ary)

  transpose                         Array dimension reordering

  diag                              Diagonal extraction

  join                              Array concatenation along dimension

  subset                            Array subrange extraction

  split                             Array splitting (sugar for subset)

  reverse                           Array index reversal

  shift                             Array index shifting (for stencils)

  guard(p, c)                       Conditional computation

  pure                              Lift to ArrayExpr or Computation

  sequence                          Collect computations

  replicate                         Repeat computation

  fold                              Fold combinator over array tuples

  \|\> compute                      Execute computation or materialize ArrayExpr

  comm(...)                         Declare commutativity group

  poly(args)                        Declare arity-polymorphic kernel

  arity                             In-scope total argument count

  nth                               In-scope recursion depth (recursive kernels only)

  let (a, b) = tuple                Destructure tuple: a gets first, b gets rest

  args\[k\]                         Access kth argument in arity-polymorphic tuple

  omp(x: n)                         Parallelize n S-dimension levels for argument x

  tdim(...)                         T-dimension specification
  ----------------------------------------------------------------------------------------------

## Appendix B: Glossary

  --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
  Term                       Definition
  -------------------------- -----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
  **Arity**                  Number of array arguments to a function or loop

  **Arity polymorphism**     Functions that work for any number of inputs, with output rank determined by input count

  **Commutativity**          Property where argument order doesn't affect result; enables triangular iteration

  **Computation**            Unevaluated result of loop application; materialized via `compute`

  **Dimensional currying**   Partial array indexing: `A(i)` on rank-3 yields rank-2

  **Iteration object**       Abstract value representing loop structure independent of kernel (formal term)

  **Kernel**                 Function applied within a loop; receives values, not indices

  **Loop object**            Blade's reified iteration structure; constructed via `method_for` or `object_for`

  **Loop reification**       Making iteration patterns first-class values that can be composed

  **Method loop**            Loop constructed from arrays: `method_for(A, B)`

  **Object loop**            Loop constructed from function: `object_for(f)`

  **Poly-indexing**          Indexing with tuple of indices: `A(idx1, idx2, ...)` matching rank

  **Poly-tuple**             Arity-polymorphic argument tuple; accessed via `args[k]`

  **Product symmetry**       Independent symmetry on each dimension; yields (r!)\^d speedup

  **S-dimension**            (Spatial/Structural) Dimension arising from iterating over input arrays. For array rank r and kernel input rank m, the outer (r − m) dimensions are S-dimensions. Determinable from ranks alone ([Lemma 2.2](#lemma-2-2)b).

  **S/T paradigm**           Structure-first: iteration primary, element operations secondary

  **Symmetry vector**        Tuple σ where equal values indicate interchangeable dimensions

  **T-dimension**            (Temporal/Trailing) Dimension introduced by kernel output, not derived from iteration. Example: FFT transforms time→frequency. Requires knowing what kernel produces.

  **T/S paradigm**           Traditional: element operations primary, iteration implicit

  **Triangular iteration**   Loop over ordered tuples (i ≤ j ≤ k); exploits symmetry
  --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
