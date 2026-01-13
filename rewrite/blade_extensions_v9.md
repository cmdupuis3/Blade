# Blade-DSL: Extensions and Future Work

**Status**: Planned extensions, not yet implemented  
**Parent Document**: Blade Formalism v8.19

This document describes planned extensions to Blade-DSL, organized by implementation complexity. These are separated from the core formalism to distinguish stable specification from speculative design.

---

## Table of Contents

- [1. Overview](#overview)
- [2. Near-Term Extensions](#near-term-extensions)
    - [2.1 Open Design Questions](#open-design-questions)
    - [2.2 Automatic Differentiation](#automatic-differentiation)
    - [2.3 Tree Structures](#tree-structures)
    - [2.4 Graph Types via Trace Indices](#graph-types-via-trace-indices)
    - [2.5 Equivariance Extensions](#equivariance-extensions)
    - [2.6 Domain Decomposition](#domain-decomposition)
    - [2.7 Triangular File Format](#triangular-file-format)
    - [2.8 Domain Decomposition Summary](#domain-decomposition-summary)
- [3. Research Directions](#research-directions)
    - [3.1 Stencils and Halo Exchange](#stencils-and-halo-exchange)
    - [3.2 Advanced Integration Topics](#advanced-integration-topics)
    - [3.3 ML Library Integration](#ml-library-integration)
    - [3.4 Remaining Open Questions](#remaining-open-questions)

---




## 1. Overview

This section describes planned extensions to Blade-DSL, organized by implementation complexity.

## 2. Near-Term Extensions

These extensions build directly on the existing formalism and require moderate implementation effort.

### 2.1 Open Design Questions

**Error handling**: No formal treatment of error cases (incompatible shapes, invalid symmetry specifications, etc.).

**Broadcasting**: Behavior when arrays have different extents in non-symmetric dimensions is unspecified.

**Memory management**: Allocation and deallocation of intermediate results is implicit.

### 2.2 Automatic Differentiation

Blade computations are differentiable when their kernels are differentiable. Both forward mode (tangent propagation) and reverse mode (gradient accumulation) preserve the r! speedup from triangular iteration. This section formalizes AD through the S/T paradigm.

#### 2.2.1 Differentiable Computation Types

We extend the type system with differentiated computation types:

```
DComp[τ, δτ]    -- Computation with tangent type δτ (forward mode)
GComp[τ, ∇τ]    -- Computation with gradient type ∇τ (reverse mode)
```

**Definition**: A computation `c : Comp[τ]` is differentiable if its kernel is a differentiable function.

#### 2.2.2 Forward Mode (Tangent Propagation)

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

```blade
forward (loop <@> k) δA = loop <@> (forward_kernel k δA)
```

The loop structure is preserved---only the kernel is differentiated. At each iteration point:

```
output(i) = f(A(i), B(i))
d_output(i) = ∂f/∂A(A(i), B(i)) · dA(i) + ∂f/∂B(A(i), B(i)) · dB(i)
```

**Cost**: O(p · I(n,r)) for p input parameters, where I(n,r) = C(n+r-1, r).

#### 2.2.3 Reverse Mode (Gradient Accumulation)

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

**Cost**: O(I(n,r)) regardless of input dimension---the standard reverse-mode advantage.

**Tape storage**: For triangular iteration, the tape has C(n+r-1, r) entries instead of n\^r. The same r! reduction applies to memory for intermediate storage.

#### 2.2.4 Symmetric Gradient Accumulation

For triangular iteration over symmetric computations, gradients accumulate symmetrically:

```
// For method_for(A, A) with commutativity
for (auto (i, j) in loop.triangular_indices()) {
    float g_out = grad_out[tri_idx(i, j)];
    
```
// Gradient flows to BOTH indices (same underlying array)
grad_A(i) += ∂f/∂arg0(A(i), A(j)) * g_out;
grad_A(j) += ∂f/∂arg1(A(i), A(j)) * g_out;
```
}
```

This is correct because each unique pair (i,j) with i ≤ j contributes to gradients at both positions.

#### 2.2.5 Jacobian Symmetry Theorem

**Theorem (Jacobian Symmetry)**: If a computation produces symmetric output, its Jacobian inherits corresponding symmetry structure.

Let `c = method_for(A, A, A) <@> k` with `comm(a, b, c)`, producing output O with symmetry σ = ⟨1, 1, 1⟩.

Then ∂O/∂A has symmetry in its first three indices (corresponding to output symmetry) and is dense in its last index (corresponding to input).

*Proof*: Let O(i,j,k) = k(A(i), A(j), A(k)) with comm(a,b,c). Then:

```
∂O[i,j,k]/∂A[m] = (∂k/∂a)·δ_im + (∂k/∂b)·δ_jm + (∂k/∂c)·δ_km
```

By commutativity, ∂k/∂a = ∂k/∂b = ∂k/∂c at symmetric points. The Jacobian inherits the output symmetry in its first r indices. □

**Corollary (Gradient Speedup)**: Computing gradients of symmetric computations benefits from the same r! (or (r!)\^d for product symmetry) speedup as the forward computation.

#### 2.2.6 AD Through Combinators

AD interacts with combinators as follows:

**Application (`<@>`)**:

```blade
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

#### 2.2.7 Conditional Combinators in AD

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

For sparse patterns, this means gradients are naturally sparse---they flow only to allocated slices.

#### 2.2.8 AD and Arity Polymorphism

For arity-polymorphic kernels using tuple destructuring, gradients flow to all arguments:

```blade
// Forward: product(args) where let (head, tail) = args; out = head * product(tail)
// Reverse:
reverse product(args) =
    let (head, tail) = args
    grad_head = product(tail) * grad_out
    grad_tail = reverse product(tail) with upstream = head * grad_out
```

The recursive structure naturally handles arbitrary arity.

#### 2.2.9 AD and Stencils

**Open question**: How do stencil operations interact with AD?

For `stencil(A, offsets) <@> k`, the backward pass must:

1.  Compute local gradients at each stencil position
2.  Scatter-add gradients back to source positions (inverse of gather)

This is well-understood for rectangular iteration but requires care with triangular bounds---gradients from symmetric stencil positions may need symmetric accumulation.

#### 2.2.10 AD and Domain Decomposition

**Open question**: How does AD interact with distributed triangular execution?

For blocked computation:

-   **Forward pass**: Standard block-parallel execution
-   **Backward pass**: Blocks must exchange "gradient halos" at boundaries

The gradient halo exchange pattern differs from the forward halo pattern because gradients flow in the reverse direction. For triangular blocks, boundary structure is more complex than rectangular.

#### 2.2.11 Implementation Approaches

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

For triangular iteration, the tape has C(n+r-1, r) entries---the same r! reduction.

#### 2.2.12 Summary

  -----------------------------------------------------------------------------------
  Aspect                  Standard AD                  Blade AD
  ----------------------- ---------------------------- ------------------------------
  Iteration pattern       Dense (all elements)         Triangular (unique elements)

  Gradient accumulation   Standard indexing            Symmetric accumulation

  Forward speedup         1×                           r! (triangular forward)

  Backward speedup        1×                           r! (triangular backward)

  Tape storage            O(n\^r)                      O(n\^r / r!)

  Jacobian structure      Dense                        Inherits output symmetry
  -----------------------------------------------------------------------------------

**Status**: Theoretical framework established. Implementation requires:

1.  Code generation for forward/reverse kernels
2.  Tape management for triangular iteration
3.  Integration with stencils and domain decomposition
4.  Framework bindings (PyTorch, JAX)

### 2.3 Tree Structures

Arrays and trees are points on a spectrum of indexed data structures. This section explores how Blade's abstractions might extend to tree-structured data.

#### 2.3.1 Trees as Generalized Arrays

An array is a tree where: 1. All paths have the same depth (fixed rank) 2. All nodes at the same depth have the same branching factor (extents)

**Trees relax both constraints:** 1. Variable depth---paths can terminate at different levels 2. Variable branching---each node can have different numbers of children

For arrays, the index type is a product: `Idx<n₁> × Idx<n₂> × ... × Idx<nᵣ>`

For trees, the index type is a *path*: a variable-length sequence of child selections:

```
Array index: (i, j, k)           -- fixed length 3
Tree path:   (p₀, p₁, ..., pₖ)   -- variable length
```

#### 2.3.2 Tree Shape as Index Type

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

The shape IS the index type---it defines what paths are valid.

#### 2.3.3 Flat Storage with Bijection

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

This is O(k) where k is path length---just arithmetic, no pointer chasing.

#### 2.3.4 Dimensional Currying for Trees

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

#### 2.3.5 Symmetric Trees

A **symmetric tree** has commutative children---swapping children at any node doesn't change the value:

```
SymmetricTree<Float, shape>
T[(0, 1)] == T[(1, 0)]  -- if children are interchangeable
```

This is analogous to symmetric arrays where `A[i,j] == A[j,i]`.

For symmetric trees: - Storage can be reduced (only store canonical orderings) - Left-justification applies (canonical form = sorted path) - The bijection handles canonicalization

#### 2.3.6 Unification: Arrays and Trees

  Structure      Depth      Branching               Index Type
  -------------- ---------- ----------------------- ---------------------------
  Vector         1          n                       `Idx<n>`
  Matrix         2          n × m                   `Idx<n> × Idx<m>`
  Tensor         r          n₁ × ... × nᵣ           `Idx<n₁> × ... × Idx<nᵣ>`
  Ragged array   r          Variable per position   `RaggedIdx`
  Tree           Variable   Variable per node       `TreeIdx<shape>`

**The common abstraction:** - All are functions from some index domain to values - All support dimensional currying (partial indexing) - All can have symmetry (commutative indices/children) - All can be stored with a bijection to flat memory

**Poly-indexing unifies access:**

```
x[indices]  -- works for any structure
            -- indices is a path/tuple appropriate to the structure
```

#### 2.3.7 Performance Characteristics

**Array access:**

```
A[i₁, i₂, ..., iáµ£] → offset = Σ iₖ × strideₖ
```

-   O(r) multiplications and additions
-   Single memory access
-   Cache-friendly for sequential access

**Tree access:**

```
T[(p₁, p₂, ..., pₖ)] → offset = Σ (subtree_size[pⱼ] for skipped children)
```

-   O(k) additions with precomputed subtree sizes
-   Single memory access
-   No pointer chasing (unlike linked trees)

**Comparison with pointer-based trees:**

  ---------------------------------------------------------------------------------------
  Operation                 Pointer Tree              Flat Tree with Bijection
  ------------------------- ------------------------- -----------------------------------
  Access path of length k   O(k) pointer chases       O(k) arithmetic + 1 access

  Cache behavior            k cache misses (random)   1 cache miss (predictable)

  Memory overhead           2-3 pointers per node     Subtree size table

  Insertion                 O(1) at position          O(n) rebuild
  ---------------------------------------------------------------------------------------

Flat trees with bijection excel for static or rarely-modified structures with frequent access---exactly the case for scientific data.

#### 2.3.8 Open Questions for Trees

1.  **Dynamic trees:** Can we support efficient insertion/deletion while maintaining flat storage? (Probably requires buffer/rebuild strategies)

2.  **Symmetric tree storage:** What's the analog of triangular storage for trees with commutative children?

3.  **Tree × Array hybrids:** What about structures that are trees at some levels and arrays at others? (e.g., a tree of matrices)

4.  **Autodiff through trees:** How do tangents/gradients flow through tree-structured computation?

5.  **Distributed trees:** Can product-simplex decomposition generalize to trees?

### 2.4 Graph Types via Trace Indices

Trees extend arrays by allowing variable branching and depth. Graphs extend trees by allowing cycles. This section introduces **trace index types** that encode cyclic structures directly in the type system.

#### 2.4.1 The Problem with Cyclic Types

Most type systems cannot express cyclic structures directly. The type of a self-referential structure wants to be infinite: `T^I^I^I^...`. Common workarounds include:

- Unsafe pointers (C++)
- Runtime-checked references (Rust `Rc`/`RefCell`)
- Encoding graphs as separate node + edge arrays

None of these make the graph structure visible to the type system.

#### 2.4.2 Trace Index Types

`Trace<N>` is an index type that permits cycles, analogous to how `Idx<N>` indexes linear arrays:

```blade
T^Idx<N>      // Array: linear, no self-reference
T^Trace<N>    // Graph: cycles allowed
```

**Trace accumulates visited addresses, then collapses on cycle:**

```blade
A(i)             : T^Trace<{i}>
A(i)(j)          : T^Trace<{i, j}>
A(i)(j)(k)       : T^Trace<{i, j, k}>
A(i)(j)(k)(→i)   : T^Trace<{i}>        // cycle detected, collapse
```

When the next address is already in the trace:

1. Type collapses to just that address
2. No infinite type regress
3. Iteration can terminate

After collapse, no memory of previous path—like a Lorenz attractor, regions can be revisited.

#### 2.4.3 Graph Structure from Types

Adjacency is implicit in what you can access from where:

```blade
// Each node has one successor (functional graph)
Idx<N>^Trace<N>

// K neighbors per node  
Idx<N>^Idx<K>^Trace<N>

// Weighted edges
{ to: Idx<N>, weight: Float }^Idx<K>^Trace<N>

// Undirected (symmetric edges)
Idx<N>^Idx<K>^SymTrace<N>
```

No separate adjacency list. The edges *are* the indexing structure.

#### 2.4.4 Graph Algorithms as Iteration Patterns

S/T orientation makes iteration primary. Graph algorithms are traversal patterns. The traversal *is* the type.

```blade
// DFS — just follow indices (natural order)
method_for(graph : T^Trace<N>) <@> f

// Cycle detection — collapse is the signal
method_for(graph) <@> f 
    |> on_collapse { cycle_found }

// Connected components — partition on collapse
method_for(graph) <@> f 
    |> partition_on_collapse

// Shortest paths — DFS with depth tracking
method_for(graph) <@> f 
    with track(min_depth)

// Topological sort — post-order DFS, reverse
method_for(dag) <@> collect 
    with post_order |> reverse
```

| Algorithm | Traditional | Blade |
|-----------|-------------|-------|
| DFS | Implement stack/recursion | `method_for(graph)` |
| Cycle detection | Track visited set manually | Collapse event |
| Reachability | BFS/DFS + visited flag | Did collapse occur? |
| Shortest path | BFS with queue | DFS + min depth |
| Components | Union-find | Partition on collapse |

The "algorithm" is choosing the index type. `method_for` executes it.

#### 2.4.5 Index Type Hierarchy

```
Idx<N>         Linear, no branching
   ↓
TreeIdx<N>     Branching, acyclic
   ↓
DAGIdx<N>      Joins allowed, no back-edges
   ↓
Trace<N>       Full cycles
```

Algorithms on `Trace<N>` work on all. Specific types enable optimization.

#### 2.4.6 Symmetry Applies

```blade
// Undirected graph
graph : T^SymTrace<N>
// edge(a,b) = edge(b,a) by construction

// Pairwise operations get triangular optimization
method_for(graph, graph) <@> f 
    where comm(a, b)
// Only computes upper triangle
```

#### 2.4.7 Applications

**Graph Neural Networks**: Message passing over `T^Trace<N>` with natural cycle handling.

**Event loops**: Program state as `State^Trace<*>`. Cycle = detected loop (stuck state, oscillation).

**Dependency resolution**: `Package^Trace<*>`. Collapse = circular dependency.

**Web crawling**: `Page^Trace<*>`. Collapse = already visited.

#### 2.4.8 The Insight

Graphs aren't data structures you build and then traverse. They're types with iteration semantics built in.

This works because S/T makes iteration primary. Graph algorithms are *about* iteration—which nodes, what order, don't revisit. That's exactly what `Trace<N>` encodes.

The algorithm is the type. The code is just `method_for`.


#### 2.4.8 Non-Deterministic Iteration

For graph types like `Trace<G>`, basic for-loops extend beyond deterministic iteration:

```blade
for i in Trace<G> { f(i) }
```

Unlike `Idx<n>` where iteration count is known statically, graph traversal has:
- Data-dependent iteration count (possibly unbounded)
- Cycles allowing repeated visits to the same index  
- Termination policies (max steps, convergence, stochastic break)

This extends the for-loop semantics from §17.21:

```blade
// Bounded (deterministic) — output is Array
for i in Idx<n> { f(i) }           // Array<T like Idx<n>>

// Unbounded (non-deterministic) — requires bound or produces Stream
for i in Trace<G> take k { f(i) }  // Array<T like Idx<k>>
for i in Trace<G> { f(i) }         // Stream<T> or compile error
```

Random walks, Monte Carlo sampling, and message-passing until convergence all fit this pattern. Full specification is deferred to future work.

### 2.5 Equivariance Extensions

The core equivariance system is defined in §8. Remaining extensions include:

**Interaction with arity polymorphism**: How does `poly(...)` interact with equivariance? Arity-polymorphic equivariant kernels need further specification.

**Advanced representation theory**: User-defined representations beyond the built-in L0, L1, L2, etc.

**Automatic Clebsch-Gordan paths**: Automatic enumeration of valid tensor product decomposition paths.

### 2.6 Domain Decomposition

For petabyte-scale computation, the iteration space must be partitioned into blocks that can be distributed across nodes. This section specifies how product-symmetric tensors are decomposed while preserving their mathematical structure.

#### 2.5.1 Native Space vs Flattened Space

The iteration space for an n-ary symmetric operation over d-dimensional arrays is a **product of d simplices**:

Δ?\^(n-1)₀ × Δ?\^(n-1)₁ × ⋯ × Δ?\^(n-1)\_{d-1}

where each simplex Δ?\^(n-1)\_j is the region satisfying i₁ ≤ i₂ ≤ ⋯ ≤ iₙ for spatial dimension j.

**Flattening observation**: If array indices are linearized (e.g., for storage or single-loop iteration), different dimensions cycle at different rates. The valid region in flattened index space exhibits a fractal pattern of "holes"---triangular gaps nested at multiple scales, with nesting depth d. This is an artifact of flattening, not an intrinsic property of the iteration space.

**Native representation**: In the natural product-of-simplices space, there are no holes. Each factor is a complete simplex.

**Design decision**: Blade uses **native-space decomposition** because:

1.  All blocks are structurally identical (no special cases)
2.  No exclusion logic required (all block combinations valid)
3.  Matches the mathematical structure of product symmetry

#### 2.5.2 Simplex Subdivision

For a single n-simplex with extent m, subdivision proceeds by halving all n axes simultaneously, creating 2\^n cells. Each cell is labeled by an n-tuple (L,H)\^n indicating low/high half membership.

**Valid cells**: A cell is valid iff its pattern has the form L\^a H\^b (a copies of L followed by b copies of H). Once an index is in the high half, all subsequent indices must also be high due to the ordering constraint.

**Count**: Exactly (n+1) valid cells out of 2\^n.

  Arity   Valid cells              Invalid cells
  ------- ------------------------ ---------------
  n=2     3 (LL, LH, HH)           1 (HL)
  n=3     4 (LLL, LLH, LHH, HHH)   4
  n=4     5                        11
  n       n+1                      2\^n ∑ (n+1)

Each valid cell is itself an n-simplex with extent m/2, enabling recursive subdivision.

#### 2.5.3 Product Decomposition

For the full iteration space (product of d simplices), decomposition proceeds independently per factor:

1.  Each of d simplices subdivides into (n+1) valid children
2.  The Cartesian product yields (n+1)\^d child blocks
3.  Every child block is itself a product of d smaller simplices

**Key property**: All (n+1)\^d combinations are valid. No exclusion logic is needed because the decomposition respects the native product-of-simplices structure.

**At depth k**:

  Metric                       Formula
  ---------------------------- -------------------------------------
  Total blocks                 (n+1)\^(kd)
  Block extent per dimension   extent\[j\] / 2\^k
  Elements per block           ∏\_j C(extent\[j\]/2\^k + n ∑ 1, n)

**Maximum depth**: Limited by ⌊log₂(min_j extent\[j\] / n)⌋ to ensure blocks remain meaningful.

#### 2.5.4 Block Addressing

A block is identified by d paths, one per spatial dimension:

```
BlockId = (path₀, path₁, ..., path_{d-1})
```

Each path is a sequence of length k (depth), with elements in {0, 1, ..., n} representing the valid L^(n-a)H^a patterns.

**Linear index**: Paths can be serialized to a linear index using mixed-radix encoding, either dimension-major or level-major (interleaved).

#### 2.5.5 Mixed Symmetry

Not all dimensions require the same symmetry. A symmetry vector **s** = (s₀, s₁, ..., s\_{d-1}) specifies arity per dimension:

-   s_j = 1: No symmetry (rectangular, 2 children per level)
-   s_j \> 1: s_j-way symmetry (s_j + 1 children per level)

**Branching factor**: ∏\_{j=0}\^{d-1} (2 if s_j=1, else s_j+1)

#### 2.5.6 Distributed Execution

The decomposition supports Bulk Synchronous Parallel (BSP) execution:

1.  **Scatter**: Distribute blocks to workers (round-robin or locality-aware)
2.  **Compute**: Workers process blocks independently (embarrassingly parallel)
3.  **Reduce**: Aggregate via tree reduction

**Load balance**: Perfect by construction---all blocks have identical structure and element count.

### 2.7 Triangular File Format

A triangular-native storage format enables efficient I/O for symmetric tensors.

#### 2.6.1 Design Goals

1.  **No redundancy**: Store only unique elements ((n!)\^d savings)
2.  **Block-aligned**: Each decomposition block maps to one storage chunk
3.  **Parallel I/O**: Workers read/write independent chunks
4.  **Self-describing**: Metadata encodes symmetry and decomposition

#### 2.6.2 Zarr Extension Schema

```
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

#### 2.6.3 Chunk Layout

Each chunk stores one block's data in triangular format:

-   **Within a block**: standard triangular (row-major with varying row lengths)
-   **Across blocks**: linear block index → chunk index

**Access pattern**: Reading block b requires exactly one chunk read. No gathering from scattered rectangular chunks.

#### 2.6.4 Streaming Construction

Triangular files can be constructed via streaming ETL:

1.  Process rectangular source data in chunks
2.  Compute block assignment for each element
3.  Write directly to block files
4.  No central aggregation required

This enables out-of-core construction for datasets larger than available memory.

### 2.8 Domain Decomposition Summary

  Property           Native Decomposition
  ------------------ --------------------------------
  Iteration space    Product of d n-simplices
  Branching factor   (n+1)\^d per level
  Block structure    Uniform (all blocks identical)
  Exclusion logic    None (all combinations valid)
  Load balance       Perfect by construction
  I/O alignment      One block = one chunk

The native-space approach is preferred because it eliminates exclusion logic while preserving self-similarity and perfect load balance.

## 3. Research Directions

These extensions require significant research and implementation effort, involving interactions between multiple system components.

### 3.1 Stencils and Halo Exchange

The core stencil machinery (`shift`, `align`, `stencil` sugar) is defined in §2.6. The remaining work involves:

**Chunking interaction**: For cache-friendly 2D+ stencils, arrays should be chunked so that stencil neighborhoods fit in cache. The `AlignedExpr` type carries stencil metadata that can inform chunk sizing.

**Halo exchange**: For distributed computation, `AlignedExpr` metadata declares which neighboring elements are needed. The runtime can use this to:

-   Determine halo regions at chunk boundaries\
-   Generate communication patterns for halo exchange
-   Overlap computation with communication

**Open questions**:

-   Stencil interaction with triangular iteration (symmetric dimensions)
-   Automatic halo width inference from nested stencils
-   Grid topology for staggered grids (xgcm-style)
-   Integration with chunked storage formats

### 3.2 Advanced Integration Topics

Building on the near-term extensions, these topics require additional research:

**Halo exchange at triangular boundaries**: Communication patterns when stencil neighborhoods cross triangular block boundaries.

**Communication patterns for tensor-vector multiplication**: Optimizing distributed tensor contractions.

**Stencil interaction with triangular iteration**: Combining spatial stencils with symmetric tensor computation.

**AD + Domain decomposition**: Gradient halo exchange at simplex boundaries.

**AD + Stencils with symmetry**: Symmetric scatter-add for stencil gradients.

### 3.3 ML Library Integration

Integrating Blade's symmetric tensor capabilities with machine learning frameworks:

**Equivariant neural network layers**: Using §18.1.4 foundations to build E(3)-equivariant layers compatible with PyTorch/JAX.

**Automatic kernel generation**: Deriving equivariant kernels from scalar operations with verified transformation properties.

**Interoperability**: Bridging Blade arrays with PyTorch/JAX tensors for hybrid workflows.

### 3.4 Remaining Open Questions

- Optimal block sizes for distributed triangular iteration
- Cache-oblivious triangular algorithms  
- Dynamic load balancing for irregular workloads
- Fault tolerance and checkpointing for long-running computations

---

