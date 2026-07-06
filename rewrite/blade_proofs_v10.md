# Blade-DSL: Formal Theorems and Proofs

**Status**: Mathematical foundations for the Blade formalism
**Parent Document**: Blade Formalism v10

This document contains the formal theorems, lemmas, corollaries, and their proofs that establish Blade's mathematical foundations. Section numbering matches the parent formalism for easy cross-reference.

## Table of Contents

- [1. Introduction](#1-introduction)
- [2. Computational Paradigms: S/T and T/S](#2-computational-paradigms-st-and-ts)
    - [2.4 The Duality Theorem](#24-the-duality-theorem)
    - [2.5 Loop-Indexing Fusion](#25-loop-indexing-fusion-the-primitive-foundation)
    - [2.10 Syntactic Impossibility Theorems](#210-syntactic-impossibility-theorems)
    - [2.11 The Necessity Theorems](#211-the-necessity-theorems)
- [9. Loop Objects](#9-loop-objects)
    - [9.6 Structural Trinity Proofs](#96-structural-trinity-proofs)
    - [9.7 Symmetry Lowering Theorems](#97-symmetry-lowering-theorems)
    - [9.8 Uniqueness Theorems](#98-uniqueness-theorems)
- [10. Arity Polymorphism](#10-arity-polymorphism)
    - [10.3 Variadic vs Arity Polymorphism Theorems](#103-variadic-vs-arity-polymorphism-theorems)
- [12. Combinator Algebra](#12-combinator-algebra)
    - [12.6 The Duality Theorem](#126-the-duality-theorem)
    - [12.7 Rank-0 Convergence Theorem](#127-rank-0-convergence-theorem)
    - [12.8 Additional Combinator Identities](#128-additional-combinator-identities)
    - [12.8.5 The Structure-Computation Adjunction](#1285-the-structure-computation-adjunction)
    - [12.9 Zero Elements and Control Flow](#129-zero-elements-and-control-flow)
- [14. Triangular Iteration](#14-triangular-iteration)
    - [14.5 Product Symmetry Theorem](#145-product-symmetry-theorem)
- [20. Conclusion](#20-conclusion)
    - [20.1 Necessity and Uniqueness Theorem](#201-necessity-and-uniqueness-theorem)
    - [20.2 Generalization Beyond Arrays](#202-generalization-beyond-arrays)

## 1. Introduction

This document extracts the formal mathematical content from the Blade specification. Each theorem is presented with its full proof. References to other theorems use the same numbering as the parent formalism.

## 2. Computational Paradigms: S/T and T/S

### 2.4 The Duality Theorem

A natural question: is S/T merely T/S in different notation, or are they genuinely distinct?

[]{#theorem-2-1}**Theorem 2.1 (S/T Completeness):** Any T/S computation can be expressed in S/T form.

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

[]{#lemma-2-2}**Lemma 2.2 (T-Dimension Relationality):** T-dimensions (those consumed by a kernel) are not independently specifiable; they are defined *relationally* as "dimensions consumed from each array by the kernel."

*Proof:* The statement "axis -1" has no meaning without an array to apply it to. The T-structure of a computation depends on *both* the kernel signature and the array signatures. Neither alone determines it.

In contrast, S-dimensions (those iterated over) can be determined from array signatures alone—they are the dimensions remaining after kernel consumption.

[]{#lemma-2-2}**Lemma 2.2b (S-Dimension Determinability):** Given a set of input arrays A₁, ..., Aₙ and a kernel signature specifying input ranks irank(f, i), the S-dimensions are fully determined: for each array Aᵢ with rank rᵢ, the outer (rᵢ − irank(f, i)) dimensions become S-dimensions.

*Proof:* S-dimensions are defined as "dimensions iterated over"—those not consumed by the kernel. The kernel signature fixes how many dimensions each input contributes to iteration. This determination requires only array ranks and kernel input ranks—no knowledge of what the kernel *computes*, only its arity signature. ∎

**Contrast**: T-dimensions depend on what the kernel *produces*—its output rank and structure. S-dimensions depend only on what the kernel *consumes*—its input signature. This asymmetry is why S-structure can be determined before kernel binding (enabling `method_for`), while T-structure requires kernel specification.

**Definition (Iteration Object):** An *iteration object* is a value I satisfying: 1. **Kernel independence:** I can be constructed without specifying a kernel 2. **Kernel polymorphism:** I can be applied to different kernels: `I <@> k₁`, `I <@> k₂` 3. **Composability:** I can be composed with other iteration objects: `I₁ <&> I₂`

[]{#theorem-2-3}**Theorem 2.3 (Iteration Object Impossibility in T/S):** No T/S system admits iteration objects.

*Proof:* In T/S, iteration structure is determined by:

- Array dimensions (which axes exist)

- Kernel consumption (which axes the kernel reduces over)

By [Lemma 2.2](#lemma-2-2), T-dimensions are relational—they depend on the kernel. Without knowing the kernel:

- We cannot determine which dimensions are S (iterated) vs T (reduced)

- We cannot determine iteration depth (how many nested loops)

- We cannot determine iteration bounds (which depend on S-dimension extents)

Therefore no kernel-independent iteration specification exists. Conditions (1) and (2) of the definition cannot be satisfied simultaneously.

For composability (3): composition `I₁ <&> I₂` requires both I₁ and I₂ to exist as values. Since neither can exist independently, composition is impossible. ∎

**Remark (Hybrid Systems)**: A T/S system that added kernel-independent iteration objects would no longer be purely T/S—it would have adopted S/T characteristics. The impossibility is for *pure* T/S; hybrid systems are possible but are partially S/T by definition.

[]{#corollary-2-4}**Corollary 2.4:** T/S partial evaluation does not yield first-class iteration objects.

### 2.5 Loop-Indexing Fusion: The Primitive Foundation

The S/T paradigm rests on a fundamental isomorphism that we call *loop-indexing fusion*:

[]{#theorem-2-4-1}**Theorem 2.4.1 (Loop-Indexing Fusion):** Arrays are isomorphic to curried functions from indices to values:

```
Array<T, I, J> ≅ I → J → T
```

*Proof:* An array is a finite map from index tuples to values. Currying this map gives a function `I → (J → T)`. The isomorphism is witnessed by:

- Forward: `A ↦ λi. λj. A(i,j)`

- Backward: `f ↦ [f(i)(j) | i ∈ I, j ∈ J]`

Both directions preserve structure. ∎

**Significance:** This isomorphism is the primitive that grounds the entire S/T paradigm. Without it:
- "Indexing into an array" and "applying a function to indices" would be distinct operations

- Iteration structure could not be separated from data access

- Virtual arrays (structure without content) would be incoherent

[]{#definition-content-hole}**Definition (Content Hole):** The symbol `_` represents unspecified content. A *virtual array* is a function `I → _` — index structure awaiting a kernel to fill content.

[]{#theorem-2-4-2}**Theorem 2.4.2 (T/S Lacks Fusion):** In T/S systems, loop-indexing fusion is unavailable.

*Proof:* In T/S, arrays and iteration are separate concepts:

- Arrays are storage containers with shape metadata

- Iteration is a control-flow construct (for-loops, map, fold)

The "index array" `range(n)` exists, but `array[range(n)]` is slicing, not function application. There is no coherent notion of "a loop as an array" or "an array as a deferred loop." Without fusion, `I → _` has no meaning — there is no array-like object that is "structure only."

By [Theorem 2.3](#theorem-2-3), T/S cannot have iteration objects. This is a consequence: without fusion, structure and computation cannot be separated, so iteration cannot exist independently. ∎

**Corollary 2.4.3:** The Trinity (Theorem 9.5) requires loop-indexing fusion.

*Proof:* The Trinity states that loop objects, symmetry groups, and index types are equivalent views of iteration structure. All three depend on treating indices as first-class values that can be reasoned about independently of the data they access. This is precisely what fusion provides. ∎

### 2.10 Syntactic Impossibility Theorems

The preceding sections establish that S/T and T/S are genuinely distinct. We now prove preliminary results showing that certain constructs are syntactically impossible in fixed-text programs. These lemmas lead to [Theorem 2.10](#theorem-2-10) (§2.10), which proves the Structural Trinity requires S/T orientation.

#### 2.9.1 Syntactic Impossibility

[]{#theorem-2-5}**Theorem 2.5 (Cumulative Bound Dependency)**: In left-justified triangular iteration of arity r, the bound expression for loop k requires simultaneous access to all k preceding index variables {i₀, ..., i_{k-1}}.

*Proof*: The expression `bound_k = n - Σ_{m<k} i_m` contains k distinct free variables. Evaluating `bound_k` requires all k variables simultaneously in scope. ∎

[]{#theorem-2-6}**Theorem 2.6 (Lexical Nesting Requirement)**: In a language with lexical scoping, expressing arity-r left-justified triangular iteration requires r textually nested loop constructs.

*Proof*: By [Theorem 2.5](#theorem-2-5), `bound_k` requires {i₀, ..., i_{k-1}} in scope. In lexical scoping, variable i_m is in scope only within loop m's body. For all preceding indices to be in scope at loop k, loop k must be textually nested inside loops 0 through k-1.

By induction: Loop 0 has no dependencies. Loop k requires {i₀, ..., i_{k-1}}, so must be inside loop k-1, which inductively is inside loops 0..k-2. Therefore arity r requires r nesting levels. ∎

[]{#theorem-2-7}**Theorem 2.7 (Fixed-Text Impossibility)**: No fixed textual program (without metaprogramming) can express left-justified triangular iteration for arbitrary arity r.

*Proof*: By [Theorem 2.6](#theorem-2-6), arity r requires r nested loop constructs. A fixed program has fixed nesting depth N. For r > N, the program cannot express arity-r iteration. No single fixed definition works for all r. ∎

#### 2.9.2 Recursion Insufficient

[]{#theorem-2-8}**Theorem 2.8 (Recursion Obscures Structure)**: Encoding N-ary left-justified iteration via recursion makes loop structure implicit and uninspectable.

*Proof*: In recursive encoding, structure is embedded in recursion depth and parameter passing rather than exposed as data. By [Lemma 2.10.1](#lemma-2-10-1) (§2.10), computing symcomState and triangular bounds requires inspecting loop structure at each level. Implicit structure cannot be inspected without unfolding the recursion, which either reintroduces the fixed-text problem ([Theorem 2.7](#theorem-2-7)) or requires reifying the unfolded structure as data.

Furthermore, static analysis of recursive encodings—determining bounds, iteration counts, commutativity eligibility, and fusion opportunities—requires tracing all recursive calls. For arbitrary recursion, such analysis loses the guarantees available with explicit loop structure. ∎

#### 2.9.3 Runtime Necessity

[]{#theorem-2-9}**Theorem 2.9 (Reification Necessity)**: Loop structures for N-ary triangular iteration must be first-class runtime values.

*Proof*: By Theorems 2.7--2.8, neither fixed text nor recursion suffices to express N-ary triangular iteration with inspectable structure.

By [Lemma 2.10.2](#lemma-2-10-2) (§2.10), runtime selection between triangular and rectangular iteration depends on array identity—a runtime property. Both strategies must exist as runtime entities to enable selection.

Therefore, loop structures must be reified as first-class runtime values. ∎

### 2.11 The Necessity Theorems

#### 2.11.5 Zero-Cost Requirement

The preceding subsections establish that S/T is *necessary*. We now prove a stronger result: **zero-cost** `(r!)^d` speedup specifically requires compile-time symmetry tracking.

**The Symmetry Tower (Revisited):**

| Level | Symmetry | Example |
|---|----|---|
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
|----|-----|--|
| Native S/T (Blade) | Symmetry in type system | Zero-cost |
| Metaprogramming | Rebuild S/T in templates | Zero-cost, complex |
| Runtime tracking | Defer symmetry decisions | Per-iteration overhead |

Options 1 and 2 are isomorphic (Theorem 2.14). Option 3 sacrifices zero-cost (Theorem 2.12). **No fourth option exists.**

**Corollary 2.15.1:** T/S cannot achieve zero-cost factorial speedup. Any attempt to add this capability to T/S rebuilds S/T (via metaprogramming, staging, or embedded DSL).

**Corollary 2.15.2:** S/T is not a design choice—it is mathematically inevitable for zero-cost symmetric tensor computation.

————————————————————————

**Remark (Possible Contributing Factors):**

Several factors *may* explain why S/T orientation was not previously explored:

1. **The Flattening Bias:** "Flatten for performance" became received wisdom from FORTRAN through NumPy. GPUs reinforced this with coalesced memory requirements.

2. **Divided Communities:** The solution required synthesizing PL theory, numerical computing, group theory, HPC, and domain science. No single community spans all areas.

3. **Obvious in Hindsight:** Once explained, S/T seems natural. This apparent simplicity obscures the difficulty of discovering it.

4. **The Incremental Trap:** Existing tools are well-optimized for T/S. Fundamental redesigns require encountering problems that expose architectural limitations.

These are speculative observations, not proven claims. The technical results (Theorems 2.3--2.9) establish that S/T is mathematically required; why it wasn't discovered earlier is a separate, sociological question.

————————————————————————

**Remark (S/T Necessity via Scope):** An alternative proof of why T/S cannot express S/T's power derives from scope analysis of the feedback loop.

The feedback `index-cata(remaining, i)` depends on `i`, which is bound by the iteration. In T/S:

```python
for i in range(n):
    for j in range(i, n):    # j's bound depends on i
```

The expression `range(i, n)` references `i`, which is in scope only inside the loop. To extract the iteration as a first-class value, you'd need to encapsulate this scope dependency—but `i` doesn't exist until iteration begins.

S/T resolves this by making `i` internal to the loop object. The dependent bound becomes internal state, not exposed syntax. Composition operates on complete loop objects, not scope-dependent fragments.

## 9. Loop Objects

### 9.6 Structural Trinity Proofs

This section proves that **loop reification**, **arity polymorphism**, and **dimensional currying** form an inseparable trinity—each requires the other two.

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

[]{#theorem-9-1}**Theorem 9.1 (Arity Polymorphism Requires Loop Reification)**: A system with arity-polymorphic kernels—where arity r determines r-deep nested iteration with cumulative bounds—must have first-class loop representations.

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

- `Output(0)` has leading index type `Idx<n>`
- `Output(n-1)` has leading index type `Idx<1>`

Non-dependent typing assigns a fixed type to `Output(i₀)` regardless of `i₀`, which cannot express this variation.

Dimensional currying with dependent index types provides the correct typing:

```
Output : (i₀: Idx<n>) → Array<T like Idx<n - i₀>, ...>
```

The return type's leading extent depends on the argument value.

Without dependent index types, left-justified triangular arrays cannot be correctly typed—the system cannot verify that `Output(i₀)(i₁)` is in-bounds when the bound depends on `i₀`. ∎

[]{#theorem-9-4}**Theorem 9.4 (Dimensional Currying Requires Loop Reification for Bound Computation)**: Computing the dependent extent `n - Σ_{m<k} i_m` requires access to the loop structure.

*Proof*: The extent of `Output[i₀][i₁]...[i_{k-1}]` is `n - i₀ - i₁ - ... - i_{k-1}`.

To compute this extent, the system must know: 1. Which indices have been bound (i₀ through i_{k-1}) 2. The values of those indices 3. The original extent n

This information constitutes the loop structure—specifically, which level of the nested iteration we're at and what index values have been fixed. This is loop reification: the loop state must exist as an inspectable value to compute dependent bounds. ∎

[]{#theorem-9-5}**Theorem 9.5 (Dimensional Currying Requires Arity Polymorphism)**: Dimensional currying for multi-array computations requires arity polymorphism.

*Proof*: Consider the typing judgment:

```blade
method_for(A₁, ..., Aᵣ) <@> k : Comp<T^r(σ)>
```

The output type `T^r(σ)` has rank `r` equal to the input arity. Constructing this type requires:

1. Count inputs: `r = |A₁, ..., Aᵣ|` (term-level)

2. Form output type: `T^r(σ)` (type-level)

Step (2) requires `r` at the type level. Without arity polymorphism, `r` exists only as a runtime value—the type system cannot express "output rank equals input count."

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

- Requires loop reification by [Theorem 9.1](#theorem-9-1) (cannot generate r-deep nests otherwise)

- Requires dimensional currying by [Theorem 9.2](#theorem-9-2) (cannot type rank-r output otherwise)

**(2) Loop reification requires both:**

- Requires dimensional currying by [Theorem 9.3](#theorem-9-3) (cannot type left-justified output otherwise)

- Requires arity polymorphism by [Theorem 9.6](#theorem-9-6) (closure under `<*>` entails variable arity)

**(3) Dimensional currying requires both:**

- Requires loop reification by [Theorem 9.4](#theorem-9-4) (cannot compute dependent bounds otherwise)

- Requires arity polymorphism by [Theorem 9.5](#theorem-9-5) (cannot type variable output rank otherwise)

The three features form a dependency cycle with no valid subset:

```
        Arity Polymorphism
              →   →
             /     \
            →       →
Loop Reification → Dimensional Currying
```

Each edge represents a necessity proof (Theorems 5.1-5.4). ∎

[]{#corollary-9-8}**Corollary 9.8 (Unified Contribution)**: The three features constitute a single, indivisible contribution to programming language theory. Claims of novelty apply to the trinity as a whole, not to individual components.

#### 9.6.4 The Symmetry Tower and Lowering Homomorphisms

The trinity implements a deeper structure: **symmetry lowering** across a hierarchy of computational levels.

**Definition (Symmetry Levels)**: - **Level 0 (Elements)**: Symmetry is identity. `a = a`. - **Level 1 (Arrays)**: Symmetry is index permutation. `A[i,j] = A[j,i]` for σ = (1 2) ∈ S₁₂. - **Level 2 (Functions)**: Symmetry is argument permutation. `f(x,y) = f(y,x)` for commutative f. - **Level 3 (Combinators)**: Symmetry is composition structure. Associativity, MonadPlus laws.

**Definition (Symmetry Groups)**: - `Sym₀ = {id}` — the trivial group - `Sym₁(r)` — subgroups of Sᵣ acting on r index positions\
- `Sym₂(n)` — subgroups of Sₙ acting on n argument positions

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

*Proof*: When all arrays are identical (A₁ = ... = Aₙ = A), every permutation σ satisfies A_{σ(j)} = Aⱼ, so Stab = Sₙ. Thus H ∩ Stab = H ∩ Sₙ = H. The map lower₂₁(H) = H is trivially injective. For surjectivity: any symmetry group G ≤ Sₙ acting on array indices arises from the commutative function f(x₁,...,xₙ) = symmetric combination with symmetry G. ∎

[]{#theorem-9-11}**Theorem 9.11 (Lowering with Distinct Arrays)**:

Let `f : Tⁿ → T` have symmetry H ≤ Sₙ. Let `A₁, ..., Aₙ : I → T` be arrays. Define:

```
Stab(A₁,...,Aₙ) = {σ ∈ Sₙ : ∀j. A_{σ(j)} = Aⱼ}
```

Then `Out[i₁, ..., iₙ] = f(A₁[i₁], ..., Aₙ[iₙ])` has symmetry `H ∩ Stab(A₁,...,Aₙ)`.

*Proof*: Let σ ∈ H ∩ Stab(A₁,...,Aₙ). We show Out\[i_{σ(1)}, ..., i_{σ(n)}\] = Out\[i₁, ..., iₙ\].

```
Out[i_{σ(1)}, ..., i_{σ(n)}]
  = f(A₁[i_{σ(1)}], ..., Aₙ[i_{σ(n)}])              [definition of Out]
  = f(A_{σ(1)}[i_{σ(1)}], ..., A_{σ(n)}[i_{σ(n)}])  [σ ∈ Stab: Aⱼ = A_{σ(j)}]
```

Now relabel: let yⱼ = A_{σ(j)}\[i_{σ(j)}\] for each j. Then:

```
  = f(y₁, ..., yₙ)
```

Since f has symmetry σ, we have f(y₁, ..., yₙ) = f(y_{σ⁻¹(1)}, ..., y_{σ⁻¹(n)}).

Substituting back: y_{σ⁻¹(k)} = A_{σ(σ⁻¹(k))}\[i_{σ(σ⁻¹(k))}\] = Aₖ\[iₖ\].

Therefore:

```
  = f(A₁[i₁], ..., Aₙ[iₙ])
  = Out[i₁, ..., iₙ]  ∎
```

[]{#corollary-9-12}**Corollary 9.12**: - All arrays identical ⟹ Stab = Sₙ ⟹ `lower₂₁(H) = H` (full transfer) - All arrays distinct ⟹ Stab = {id} ⟹ `lower₂₁(H) = {id}` (no transfer)

[]{#theorem-9-13}**Theorem 9.13 (Lowering `lower₁₀`: Array Symmetry → Identity)**:

The map `lower₁₀ : Sym₁(r) → Sym₀` sending every permutation to identity is the unique homomorphism to the trivial group.

*Interpretation*: Reading elements from a symmetric array "consumes" the symmetry. The permutation σ ∈ Sym₁(r) guarantees `A(σ(i)) = A(i)`, but both sides denote the same element—this is just Level 0 identity.

[]{#theorem-9-14}**Theorem 9.14 (Input Symmetry Does Not Propagate)**:

Let `f : T² → T` have trivial symmetry (non-commutative). Let `A : I² → T` have symmetry S₁₂. Define `Out[i,j] = f(A[i,0], A[j,1])`.

Then Out has trivial symmetry.

*Proof*: `Out[j,i] = f(A[j,0], A[i,1]) ≠  f(A[i,0], A[j,1]) = Out[i,j]` in general, since f is non-commutative. The symmetry of A is irrelevant—it was consumed when elements were read. ∎

**Summary (The Lowering Principle)**:

  Homomorphism   Domain    Codomain   Structure
  ————-- ——— ———- ————————————-
  `lower₁₀`      Sym₁(r)   Sym₀       Trivial (all symmetries → identity)
  `lower₂₁`      Sym₂(n)   Sym₁(n)    Isomorphism when arrays identical

Symmetry at level N lowers to level N-1 when objects are applied. Since `lower₁₀` is trivial, input array symmetry vanishes into element identity. Since `lower₂₁` is an isomorphism (for identical arrays), function commutativity transfers to output array symmetry. Both phenomena—input symmetry "quashing" and output symmetry "generation"—are instances of the same lowering structure.

[]{#theorem-9-15}**Theorem 9.15 (Trinity Implements Lowering)**:

The Structural Trinity provides the machinery to compute and exploit `lower₂₁`:

1. **Arity polymorphism** determines the domain—arity n specifies which Sₙ we lower from
2. **Dimensional currying** makes the codomain explicit—indices are arguments, so Sym₁ and Sym₂ share representation\
3. **Loop reification** captures the lowered symmetry—the loop structure encodes which symmetries survived, determining triangular vs rectangular iteration

*Proof*: By [Theorem 9.7](#theorem-9-7), the three features are mutually necessary. Computing `lower₂₁` requires knowing arity (which Sₙ), treating indices as arguments (shared representation), and representing the result structurally (loop object with symmetry metadata). Each feature provides exactly one component. ∎

#### 9.6.5 Level 3 and Beyond: The First-Class Function Collapse

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

Levels 0 and 1 are special: - Level 0 has no exploitable structure - Level 1 has *spatial* structure — memory layout, cache behavior, triangular storage

Level 2+ is "just algebra." Symmetries are free to permute at runtime; the computational payoff comes from **lowering to Level 1** where symmetry becomes physical bytes and avoided FLOPS.

**Lifting Combinators via `method_for` and `object_for`**:

The `method_for`/`object_for` duality extends to combinators themselves:

```blade
method_for(<&>) : [f, g, h, ...] → f <&> g <&> h <&> ...
method_for(>>) : [f, g, h, ...] → f >> g >> h >> ...
```

This is precisely `fold` — lifting a binary combinator to n-ary. Associativity of `<&>` and `>>` makes this well-defined (parenthesization doesn't matter).

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

The kernel is *data* — a value constructed, inspected, and transformed — until applied to arrays and lowered to Level 1.

**S/T All The Way Up**:

This extends the S/T philosophy to kernel construction itself:

  ————————————————————————————————-
  Level                    S/T Pattern
  ———————— ————————————————————————
  3                        `method_for(<&>)([stats...])` — build combinator structure from list

  2                        `method_for(A, A, A)` — build iteration structure from arrays

  1                        Triangular storage — physical realization of symmetry
  ————————————————————————————————-

Structure is built top-down; data flows bottom-up. The entire computation is *shaped* before any data is touched.

#### 9.6.6 Symmetry Raising Homomorphisms

While lowering transfers symmetry *downward* when structure is applied, **raising** transfers symmetry *upward* when structure is constructed. Raising is the dual of lowering—it derives higher-level symmetry from lower-level facts.

**Definition (Raising Homomorphisms)**:

| Homomorphism | Domain | Codomain | Structure |
|--------------|--------|----------|-----------|
| `raise₀₁` | Sym₀ (identity) | Sym₁(r) | Identity → array symmetry |
| `raise₁₂` | Sym₁(r) | Sym₂(r) | Array symmetry → function commutativity |

**Intuition**: Lowering asks "given this symmetry, what can I exploit?" Raising asks "given this structure, what symmetry emerges?"

##### 9.6.6.1 Raising `raise₀₁`: Identity → Array Symmetry

<a id="theorem-9-16"></a>**Theorem 9.16 (Raising `raise₀₁`: Identity → Array Symmetry)**:

Let `A : I → T` be an array. Let `f : T² → U` be a binary operation. Consider the computation:

```
Out[i, j] = f(A[i], A[j])
```

where both indices access the *same* array A. Then:

1. If `I` has a named index type (unit), Out is indexed by `(Nat<I>, Nat<I>)`—same unit on both positions
2. The iteration space is inherently permutable: `(i, j)` and `(j, i)` access the same data source
3. If `f` is commutative, then `Out[i, j] = Out[j, i]`, so Out has symmetry S₂

*Interpretation*: The identity `A == A` (a Level 0 fact—same object) **raises** to output symmetry (Level 1) when combined with operation commutativity.

*Proof*:

```
Out[i, j] = f(A[i], A[j])
Out[j, i] = f(A[j], A[i])
         = f(A[i], A[j])    [f commutative]
         = Out[i, j]        ∎
```

**Key insight**: The identity of `A` is doing the work. Without `A == A`, we'd have `f(A[i], B[j])` and `f(A[j], B[i])`, which are unrelated even if `f` is commutative.

<a id="theorem-9-17"></a>**Theorem 9.17 (Raising with Shared Index Spaces)**:

Let `A : I × J → T` and `B : I × K → T` be arrays sharing index space `I`. Let `f : T² → U` be commutative. Consider:

```
Out[i₁, i₂, j, k] = f(A[i₁, j], B[i₂, k])
```

Then Out has symmetry S₂ in the `(i₁, i₂)` positions (both indexed by `I`), but no symmetry in `(j, k)`.

*Proof*: The shared index space `I` means `i₁` and `i₂` have the same unit. For any permutation σ = (1 2) acting on the I-positions:

```
Out[i₂, i₁, j, k] = f(A[i₂, j], B[i₁, k])
```

This equals `Out[i₁, i₂, j, k]` only if `A[i₂, j] = B[i₁, k]` in general—which doesn't hold for distinct arrays. **Partial raising fails for distinct arrays**, even with shared index spaces.

However, if `A == B`:

```
Out[i₂, i₁, j, k] = f(A[i₂, j], A[i₁, k])
                  = f(A[i₁, k], A[i₂, j])    [f commutative]
```

This still doesn't equal `Out[i₁, i₂, j, k] = f(A[i₁, j], A[i₂, k])` unless `j == k`.

**Conclusion**: `raise₀₁` requires identity (`A == A`) in addition to shared index spaces. Shared units alone are insufficient. ∎

##### 9.6.6.2 Raising `raise₁₂`: Array Symmetry → Function Commutativity

<a id="theorem-9-18"></a>**Theorem 9.18 (Raising `raise₁₂`: Array Symmetry → Commutativity)**:

Let `S : SymIdx<2, I> → T` be a symmetric array. Define the function:

```
f(i, j) = S(i, j)
```

Then `f` is commutative: `f(i, j) = f(j, i)`.

*Proof*: By definition of `SymIdx<2, I>`, we have `S(i, j) = S(j, i)`. Therefore:

```
f(i, j) = S(i, j) = S(j, i) = f(j, i)  ∎
```

*Interpretation*: A symmetric array **is** a commutative function. The symmetry of storage (Level 1) raises to commutativity of access (Level 2). This is the array-function duality made explicit.

<a id="corollary-9-19"></a>**Corollary 9.19 (Commutativity Propagation via Raising)**:

Let `S : SymIdx<2, I> → T` be symmetric. Let `g : T → U` be any function. Then:

```
h(i, j) = g(S(i, j))
```

is commutative in `(i, j)`.

*Proof*: `h(i, j) = g(S(i, j)) = g(S(j, i)) = h(j, i)`. The commutativity of `S` (raised from its symmetry) propagates through `g`. ∎

<a id="theorem-9-20"></a>**Theorem 9.20 (Raising Enables Deduced Commutativity)**:

Let `S : SymIdx<2, I> → T` be symmetric. Let `A : I → U` be any array. Let `*` be a commutative operation. Consider:

```
kernel(i, j) = S(i, j) * A(i) * A(j)
```

Then `kernel` is commutative in `(i, j)`.

*Proof*: Analyze each factor:
- `S(i, j) = S(j, i)` by symmetry of S (raised to commutativity)
- `A(i) * A(j) = A(j) * A(i)` by commutativity of `*` and identity `A == A`

Therefore:
```
kernel(i, j) = S(i, j) * A(i) * A(j)
kernel(j, i) = S(j, i) * A(j) * A(i)
             = S(i, j) * A(i) * A(j)    [both factors commute]
             = kernel(i, j)             ∎
```

##### 9.6.6.3 The Raising-Lowering Duality

| Aspect | Lowering (S/T) | Raising (T/S) |
|--------|----------------|---------------|
| Direction | Level 2 → Level 1 → Level 0 | Level 0 → Level 1 → Level 2 |
| Input | Declared symmetry | Observed identity/structure |
| Output | Iteration strategy | Discovered symmetry |
| Typical use | Optimization | Deduction |

**Theorem 9.21 (Raising is T/S)**:

True symmetry raising—discovering structure from unstructured computation—is a T/S concept. In S/T systems, structure is declared before computation, so "raising" reduces to early detection of lowering conditions.

*Argument*: In S/T (Blade), structure is declared *before* the kernel executes. Same unit on both sides implies symmetric iteration is available. The kernel can preserve or break this structure, but the structure was always there.

In T/S, no structure is declared. The programmer writes dense loops. But identity combined with commutativity means the output *happens to be* symmetric. A compiler must **raise** this fact from the computation pattern.

**Corollary 9.22**: In S/T systems, raising is subsumed by:
1. Nominal index typing (same unit = permutable)  
2. Identity detection (same array = full stabilizer)
3. Operation commutativity checking

These provide the information that T/S systems would discover via raising.

##### 9.6.6.4 Summary: The Complete Symmetry Homomorphism Structure

```
Level 2 (Commutativity)  ←—raise₁₂——  Level 1 (Array Symmetry)
         |                                      ↑
         |                                      |
    lower₂₁                               raise₀₁
         |                                      |
         ↓                                      |
Level 1 (Array Symmetry)  ——lower₁₀——→  Level 0 (Identity)
```

| Homomorphism | Direction | Condition | Effect |
|--------------|-----------|-----------|--------|
| `lower₂₁` | 2 → 1 | Commutativity declared | Triangular iteration |
| `lower₁₀` | 1 → 0 | Array accessed | Symmetry consumed |
| `raise₀₁` | 0 → 1 | Identity + same unit + comm op | Output symmetry |
| `raise₁₂` | 1 → 2 | Symmetric array accessed | Commutativity propagates |

**The key insight**: Lowering and raising are duals. Lowering realizes declared structure; raising discovers emergent structure. S/T systems use lowering; T/S systems would need raising. Blade (S/T) subsumes raising by making structure explicit in the type system, enabling automatic deduction without annotation.

### 9.7 Symmetry Lowering Theorems

### 9.8 Uniqueness Theorems

## 10. Arity Polymorphism

### 10.3 Variadic vs Arity Polymorphism Theorems

**Theorem (Variadic Cannot Express Arity Polymorphism)**: Standard variadic typing cannot derive output rank from input count.

*Proof*: In variadic typing, the output type σ is determined before knowing argument count. To express "output rank equals input count" requires:

1. **Dependent types**: Output type depends on term-level value r
2. **Type-level naturals**: r available at the type level
3. **Type-level arithmetic**: Computing n\^r as output shape

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

## 12. Combinator Algebra

### 12.6 The Duality Theorem

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

### 12.7 Rank-0 Convergence Theorem

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
|---|-----|
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

### 12.8.5 The Structure-Computation Adjunction

The `method_for`/`object_for` duality has a deeper categorical structure. By stripping content from both sides, we obtain an adjunction between structure and computation.

#### The Functors V and P

**Definition (V : Struct → Array):** Given an index structure S, define:

```
V(S) = S → S    (identity function on indices)
```

Equivalently: `V(S) = method_for(range<S>) <@> id`

V creates a "virtual array" — pure structure with trivial content (indices map to themselves).

**Definition (P : Kernel → Struct):** Given a kernel k, define:

```
P(k) = (arity(k), commutativity(k))
```

Equivalently: `P(k) = structure_of(object_for(k))`

P extracts the structural skeleton of a kernel — its arity and symmetry — forgetting the actual computation.

[]{#theorem-12-5}**Theorem 12.5 (V ⊣ P Adjunction):**

```
Hom(V(S), C) ≅ Hom(S, P(C))
```

*Proof:* Both sides express "S is compatible with C":

- Left: morphisms from the virtual array V(S) to computation C

- Right: morphisms from structure S to the structural skeleton of C

A virtual array V(S) can feed into computation C iff the structure S matches what C expects. This is the same as S mapping into P(C). The isomorphism is natural in both S and C. ∎

[]{#theorem-12-6}**Theorem 12.6 (Rank-0 Adjunction Collapse):** At rank 0, the adjunction V ⊣ P collapses to an equivalence.

*Proof:* At rank 0:

- V(S) for trivial S is a scalar — no structure

- P(k) for rank-0 k is trivial — no iteration metadata

Both functors become trivial, so the adjunction degenerates. This is the categorical content of Theorem 12.2 (Rank-0 Convergence). ∎

[]{#theorem-12-7}**Theorem 12.7 (Non-Faithfulness of P):**

```
P(a + b) = P(a * b) = SymmetricBinaryLoop
```

*Proof:* P extracts only arity (2) and commutativity (symmetric). It forgets:

- The actual operation (+, *, etc.)

- Any runtime parameters

- The computational semantics

Many distinct kernels map to the same structure. ∎

**Significance:** P's non-faithfulness is not a defect — it is precisely what enables optimization. By forgetting computational content, P exposes the symmetry structure that allows triangular iteration. The kernel `a + b` and `a * b` compile to identical loop structures; only the inner operation differs.

#### Layered Structure

The S/T paradigm has a layered organization:

```
Loop-indexing fusion (primitive, §2.5)
        ↓
method_for / object_for (primary duality)
        ↓ strip content
V ⊣ P adjunction (skeleton)
```

V and P are derived from `method_for`/`object_for` by removing content:
- `method_for` binds arrays → V binds pure structure
- `object_for` binds kernels → P extracts pure structure

The adjunction is the "skeleton" of the full duality — what remains when we forget the data and retain only the shape.

#### Unified Function Type View

[]{#theorem-12-8}**Theorem 12.8 (Everything is Function Types):**

All constructs in Blade reduce to function types `I → J → K → T` with different parenthesizations:

| Construct | Type | Reading |
|-----|--|---|
| Array | `I → (J → T)` | Indices to values |
| Kernel | `T → T` | Values to values |
| Loop object | `(I → T) → S` | Array consumer |
| Virtual array | `I → _` | Structure awaiting content |

*Proof:* Loop-indexing fusion (Theorem 2.4.1) establishes that arrays are functions. Kernels are functions by definition. Loop objects are higher-order functions that consume arrays. Virtual arrays are partial functions awaiting completion. ∎

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

#### Choice Combinator (\<\|>)

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
  ——————— ——————
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

  —————————————————————————————————————--
  Concept            Syntax                  Role                                        Preserves
  —————— ———————-- ——————————————- ————————--
  Zero array tuple   `()` / `method_for()`   Identity for `<*>`, arity recursion base    T-dimensions from kernel

  Zero function      `zero`                  Annihilator for `>>=`, identity for `<|>`   S-dimensions from arrays
  —————————————————————————————————————--

————————————————————————

## 14. Triangular Iteration

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
  ————————— ———-
  r=3, d=1 (coskewness, 1D)   6×
  r=3, d=2 (coskewness, 2D)   36×
  r=4, d=2 (cokurtosis, 2D)   576×
  r=4, d=4 (cokurtosis, 4D)   331,776×

**Design implication**: The Product Symmetry Theorem is not merely a performance result—it is the *forcing function* behind the Structural Trinity. Dimensional currying exists because flattening forfeits (r!)\^(d-1). Arity polymorphism exists because r varies across computations. Loop reification exists to represent the product-of-simplices iteration space as a composable value. The (r!)\^d speedup is the prize; the Trinity is what's required to claim it.

————————————————————————

## 20. Conclusion

### 20.1 Necessity and Uniqueness Theorem

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

### 20.2 Generalization Beyond Arrays

The structural principles established here extend beyond rectangular arrays to other indexed collections:

| Collection | Index Type | V functor | Symmetry |
|----|-----|-----|----|
| Array | `Idx<N>` | `range<I>` | `SymIdx<r,N>` |
| Tree | `TreeIdx<shape>` | `paths<shape>` | Commutative children |
| Graph | `Trace<N>` | `dfs<G>` | `SymTrace` |

**Key insight:** The `(r!)^d` speedup is array-specific (arising from product structure), but the *structural separation* enabled by loop-indexing fusion is universal:

1. Any collection with a notion of "index" admits fusion: `Collection<T, I> ≅ I → T`

2. Given fusion, V and P exist: structure can be separated from computation

3. Given separation, symmetry can be detected and exploited

The S/T paradigm is the general framework; arrays provide the richest symmetry structure and largest speedups.
