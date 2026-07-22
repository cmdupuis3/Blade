# Blade Feature Module: Graphs and Trees

Status: **planned** (design settled in outline, no implementation). Source
material: `blade_extensions_v10.md` §2.3–2.4. This module extends Blade's
index-type story from rectangular/symmetric arrays to trees (variable depth and
branching) and graphs (cycles), reusing the same core ideas: the index type
defines the valid address domain, storage is flat with a precomputed bijection,
currying is partial application of the address, and symmetry is canonical
ordering.

The speculative far end (symmetric-tree storage theory, dynamic trees,
distributed trees) is tracked in [future.md](../future.md); this document records
the parts whose design direction is settled.

---

## 1. Trees as generalized arrays

An array is a tree with (1) uniform path depth (the rank) and (2) uniform
branching per level (the extents). Trees relax both:

| Structure | Depth | Branching | Index type |
|-----------|-------|-----------|------------|
| Vector | 1 | n | `Idx<n>` |
| Tensor | r | n₁ × ... × nᵣ | `Idx<n₁> × ... × Idx<nᵣ>` |
| Ragged array | r | variable per position | `RaggedIdx` |
| Tree | variable | variable per node | `TreeIdx<shape>` |

An array index is a fixed-length tuple; a tree index is a **path** — a
variable-length sequence of child selections.

### 1.1 Tree shape as index type

Just as `Idx<n>` defines valid positions, a tree **shape** defines valid paths.
The shape IS the index type:

```blade
Shape = Node(children: List<Shape>) | Leaf

example_shape = Node([
    Node([Leaf, Leaf]),           // path (0,) has 2 children
    Node([Leaf, Leaf, Leaf]),     // path (1,) has 3 children
])

T : Tree<Float, example_shape>
// valid paths: (0,0), (0,1), (1,0), (1,1), (1,2)
```

### 1.2 Flat storage with bijection

Trees are stored contiguously (depth-first layout) with a precomputed bijection —
the same contract every Blade index type satisfies:

```
TreeIdx<shape> = {
    forward  : Path → Offset          // Σ skipped-subtree sizes + local offsets
    backward : Offset → Path
    subtree  : PartialPath → (Offset, SubShape)
}
```

Path-to-offset is O(path length) arithmetic with precomputed subtree sizes — one
memory access, no pointer chasing. Versus pointer trees: k predictable-address
arithmetic steps + 1 cache miss instead of k random-address misses; cost is a
subtree-size table and O(n) rebuild on structural modification. Flat trees with
bijection are the right trade for static or rarely-modified structures with
frequent access — the scientific-data case.

### 1.3 Dimensional currying for trees

Currying is partial-path application, returning a view (offset + sub-shape):

```blade
T[(0,)]        // TreeView at child 0
T[(0,)][(1,)]  // same as T[(0,1)]
```

### 1.4 Symmetric trees (speculative end)

A symmetric tree has commutative children: `T[(0,1)] == T[(1,0)]` where children
are interchangeable — the tree analog of `A(i,j) = A(j,i)`. Canonical form =
sorted path; storage reduces to canonical orderings; the bijection
canonicalizes. The storage theory (the tree analog of triangular/left-justified
layout) is open — see [future.md](../future.md).

## 2. Graphs via trace index types

Trees allow branching; graphs allow **cycles**. The obstacle is that cyclic
structure makes the natural type infinite (`T^I^I^...`). Standard workarounds
(pointers, `Rc/RefCell`, node+edge arrays) hide the graph from the type system.

### 2.1 `Trace<N>`

`Trace<N>` is an index type permitting cycles. It **accumulates visited
addresses and collapses on revisit**:

```blade
A(i)             : T^Trace<{i}>
A(i)(j)          : T^Trace<{i, j}>
A(i)(j)(k)       : T^Trace<{i, j, k}>
A(i)(j)(k)(→i)   : T^Trace<{i}>        // cycle detected, collapse
```

Collapse (1) stops the type regress, (2) is the termination signal for
iteration, (3) forgets the path — regions can be revisited afterward.

### 2.2 Adjacency is the indexing structure

No separate adjacency list; edges are what you can access from where:

```blade
Idx<N>^Trace<N>                              // functional graph (one successor)
Idx<N>^Idx<K>^Trace<N>                       // K neighbors per node
{ to: Idx<N>, weight: Float }^Idx<K>^Trace<N> // weighted edges
Idx<N>^Idx<K>^SymTrace<N>                    // undirected (symmetric edges)
```

### 2.3 Algorithms as iteration patterns

S/T orientation makes the traversal the type; `method_for` executes it:

| Algorithm | Traditional | Blade |
|-----------|-------------|-------|
| DFS | stack/recursion | `method_for(graph) <@> f` |
| Cycle detection | manual visited set | collapse event (`on_collapse`) |
| Reachability | BFS/DFS + flags | did collapse occur? |
| Shortest path | BFS + queue | DFS + `track(min_depth)` |
| Connected components | union-find | `partition_on_collapse` |
| Topological sort | Kahn / DFS | post-order collect, reverse |

### 2.4 Index type hierarchy

```
Idx<N>       linear, no branching
TreeIdx<N>   branching, acyclic
DAGIdx<N>    joins allowed, no back-edges
Trace<N>     full cycles
```

Algorithms written against `Trace<N>` run on all of them; the more specific
types license optimizations.

### 2.5 Symmetry composes

`SymTrace<N>` gives undirected graphs (`edge(a,b) = edge(b,a)` by construction),
and pairwise operations over graphs get the standard triangular optimization
under `comm`.

### 2.6 Non-deterministic iteration

Graph traversal breaks the static-iteration-count assumption of `for`:
data-dependent (possibly unbounded) counts, revisits, and termination policies.
The design sketch:

```blade
for i in Idx<n> { f(i) }           // Array<T like Idx<n>>       (bounded)
for i in Trace<G> take k { f(i) }  // Array<T like Idx<k>>       (bounded by take)
for i in Trace<G> { f(i) }         // Stream<T> or compile error (unbounded)
```

Random walks, Monte Carlo sampling, and message-passing-until-convergence fit
this pattern. Full semantics deferred — see [future.md](../future.md).

### 2.7 Applications

- **Graph neural networks**: message passing over `T^Trace<N>` with natural
  cycle handling (connects to [equivariant-nn.md](equivariant-nn.md) §9
  scatter/gather).
- **Dependency resolution**: `Package^Trace<*>`; collapse = circular dependency.
- **Event loops / state machines**: `State^Trace<*>`; collapse = stuck state or
  oscillation.
- **Web crawling**: `Page^Trace<*>`; collapse = already visited.

## 3. Open questions

Tracked in [future.md](../future.md):

1. Dynamic trees (insertion/deletion vs flat storage; buffer/rebuild strategies)
2. Symmetric-tree storage theory (triangular analog for commutative children)
3. Tree × array hybrids (trees of matrices)
4. AD through tree/graph structures
5. Distributed trees (does product-simplex decomposition generalize?)
6. `DAGIdx` semantics (join handling without back-edges)
7. `Stream<T>` type and termination policies for non-deterministic iteration
