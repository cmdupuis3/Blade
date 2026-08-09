# The halo<> ⇄ CuTe bridge

Thesis: Blade's `halo<>` is already a CuTe **coordinate layout** (basis-stride
layout, Cecka §2.4.2 Fig. 4) — not merely analogous to one. The bridge is
term-for-term for the dense case, needs one accessor trick for the compound
case, and the two systems compose at three distinct seams. Blade keeps the
language-level truth (typed domain shrink, static stencils, symmetry gates);
CuTe supplies the chip-level truth (thread partitioning, smem staging,
vectorization legality). Nothing in Blade's surface semantics changes — the
bridge is purely a lowering choice.

Reference: Cecka, "CuTe Layout Representation and Algebra" (arXiv:2603.02298).
Section numbers below are that paper's.

## 1. What halo<> actually is (verified against corpus + codegen)

From `tests/corpus/loops/072–076` and the CodeGen halo arms:

- `halo<I, [o1..ok]>` is a **traversal transformer**: at each ordinal position
  of `I` it exposes a window `w`, and `w(o)` yields the NEIGHBOR INDEX at
  signed offset `o` (center = 0). The kernel applies that index to any array:
  `A(w(1)) - A(w(-1))`. **The window generates coordinates, not elements** —
  no data view exists at the surface.
- The iteration domain is shrunk to the interior (`BndShrink`):
  span `W = max−min+1`, interior extent `n − (max−min)`.
- Offsets are compile-time (BL3999, corpus 075) — a static stencil.
- Multi-axis is per-axis separable: `range<halo<Lat,..>, halo<Lon,..>>`
  (corpus 076) — one window per axis slot.
- Dense codegen may plan a **carousel** (rotating window locals; spans ≤ 8,
  static start offsets, no Reynolds/MPI-slab/streamed sources).
- The **compound** variant (`halo<CompoundIdx<m>>`) makes `w` a pointer into
  the materialized index's contiguous `rank_to_tuple` table at the center
  cell; `w(o)` unhashes the neighbor-in-enumeration-order's coordinate
  (IRHaloUnhash). The interior shrink is on the RANK axis.

## 2. The dictionary

With `mn = min offset`, `mx = max offset`, `W = mx−mn+1`, interior `M = n−W+1`:

| Blade | CuTe |
|---|---|
| `halo<Idx<n>, offs>` | coordinate layout `H = (W, M) : (e0, e0)`, accessor `{0}` counting from coordinate 0; `H(o', i) = (i + o')·e0` |
| `w(o)` (signed, center 0) | static coordinate `o − mn` into mode 0: `H(o−mn, i)` |
| `BndShrink` interior | the shape arithmetic `M = n−W+1` itself |
| `A(w(o))` | composition: data layout of `A` applied to the coordinate — `(A ∘ H)(o−mn, i)`, an overlapped data layout `(W, M) : (1, 1)` |
| `range<halo<Lat>, halo<Lon>>` | by-mode tiler `⟨H_lat, H_lon⟩` with strides `e0`, `e1` (§3.3.5) |
| non-uniform offsets `[-2,0,3]` | span-dense window of extent `W = 6` + STATIC coordinate selection {0, 2, 5}; unused lanes never referenced (this is what Blade codegen emits today — signed subscripts into a contiguous span) |
| span ≤ 8 gate | register-fragment size bound |
| carousel | NOT a layout — an intra-thread schedule (see §5) |

Two facts make the dense bridge exact rather than approximate:

1. **The interior of a box is a box.** Blade's type-level domain restriction
   and CuTe's shape arithmetic are the same operation: `halo` needs ZERO
   predication in CuTe, because `(W, n−W+1) : (1, 1)` already IS the shrunk
   domain. Predication would only enter for boundary policies (wrap/clamp/
   reflect), which Blade deliberately does not have. If they ever come:
   coordinate-layout predication on boundary tiles only, unpredicated bulk —
   both derivable from an interior/boundary split.
2. **Blade already separated pattern from data.** `w` yields indices; data
   composition happens per-use in the kernel body. That is precisely CuTe's
   coordinate-tensor discipline (used for TMA and predication), so the
   lowering does not have to invent a factoring — the surface language
   already has it.

## 3. The three composition seams

### Seam A — element level (registers)
Window mode 0 lives in registers. `A ∘ H` restricted to a thread's interior
positions is the per-thread fragment; Blade's static offsets select lanes at
compile time. The ≤ 8 span cap maps directly onto fragment sizing.

### Seam B — tile level (CTA / shared memory)
Overlapped smem staging is **composition, not logical_divide**: divides are
disjoint by construction (complement, §3.5), but a halo tile overlaps its
neighbor. The overlapped tiling of a length-n axis into tiles of interior T is

    B = (T + W − 1, nTiles) : (1, T)        // adjacent tiles share W−1 cells
    staged = A ∘ B                          // per-CTA copy = slice of `staged`

This is the standard CuTe conv idiom (im2col, §2.6.2 CONV). Note the
self-similarity: `halo<>` reappears at every level of the memory hierarchy —
element window (registers), tile window (smem), slab window (MPI). Blade
already owns the outermost (MPI slab) and innermost (surface halo) levels;
CuTe supplies the middle level plus the TV-layout thread partitioning and the
right-inverse vectorization check (§3.4.2) for the staging copies.

### Seam C — compound/masked domains (the non-affine part)
The compound halo iterates a window over the RANK axis — a 1-D box
`[0, card−W+1)` — and unhashes per lane. CuTe cannot express the unhash as a
layout (non-affine), but it does not need to: the paper's accessor concept
(§2.5) explicitly admits transform/gather iterators. So:

    accessor  e  = { rank_to_tuple table }   // dereference = unhash (Blade's table)
    layouts      = the full CuTe algebra ON THE RANK AXIS

Every layout operation — tiling, TV partition, divide, product — applies
unimpeded in rank space, because the box is rectilinear there. Vectorization
inference stays HONEST: right-inverse contiguity in rank space is real
contiguity of the compact pool's `.data` (so pool sweeps vectorize), while
reads through the gather are correctly reported non-contiguous. This is the
element-granularity seam, strictly better than the earlier tile-granularity
proposal. The existing CUDA combinadic partitioner (CodeGen.fs ~:8072 —
balanced flat-rank ranges, unrank per cell) is already this design,
hand-rolled; the bridge names it.

## 4. Worked lowerings

Corpus 072 (central difference, `halo<Idx<5>, [-1,0,1]>`):

    // Blade: method_for(halo<Idx<5>, [-1,0,1]>) <@> lambda(w) -> A(w(1)) - A(w(-1))
    Tensor A   = make_tensor(ptrA, make_layout(Int<5>{}));            // 5 : 1
    auto  win  = make_layout(make_shape(Int<3>{}, Int<3>{}),          // (W, M)=(3,3)
                             make_stride(Int<1>{}, Int<1>{}));        //  : (1, 1)
    Tensor Wd  = composition(A, win);                                 // Wd(o', i) = A[i+o']
    for (int i = 0; i < size<1>(Wd); ++i)
        d(i) = Wd(2, i) - Wd(0, i);        // w(1) -> o'=2, w(-1) -> o'=0 (static)

Corpus 076 (2-D Laplacian) is the by-mode form — one overlapped mode per axis:

    auto win2 = make_layout(make_shape (make_shape(Int<3>{}, Int<1>{}), ...),
    // or idiomatically: composition(A, make_tile(win_lat, win_lon))
    // giving Wd((or', oc'), (i, j)) = A(i+or', j+oc') — the four stencil taps
    // are the static coordinates (0,1),(2,1),(1,0),(1,2), center (1,1).

GPU shape (per-CTA staging + per-thread work), dense 1-D:

    auto tiler   = make_layout(make_shape(Int<T + 2>{}, Int<NT>{}),
                               make_stride(Int<1>{}, Int<T>{}));       // seam B
    Tensor tiles = composition(A, tiler);
    Tensor tile  = tiles(_, blockIdx.x);            // (T+2) cells incl. halo
    // cooperative copy tile -> smem (vectorization width from right-inverse)
    // per-thread: win over smem, static taps, carousel = register rotation loop

## 5. What stays Blade's (CuTe is silent here)

- **BndShrink as a type-level fact** — the output extent `Idx<n−W+1>` is part
  of the array's record and downstream inference (ppl formers infer it,
  corpus 068). CuTe's shape arithmetic produces the number; Blade's types
  carry it.
- **The staticness gate** (BL3999) and the symmetry/oddness gates (BL4015
  family) — admissibility is Blade's, CuTe checks only divisibility.
- **The carousel** — layout algebra says nothing about schedules; the
  rotation (warm-up before the innermost header, rotate at loop tail) becomes
  the intra-thread register-reuse loop and remains codegen's job.
- **MPI slab exchange** — CuTe stops at the node.

## 6. Route and open questions

1. This is a GPU-backend story. On CPU the current halo emission (carousel +
   OpenMP) already occupies the niche; CuTe's wins (TV partition, smem
   staging, copy-atom vectorization) need the CUDA target that
   blade_linalg_cuda work is opening. Land the bridge as the halo arm of that
   backend, not as a CPU rewrite.
2. Emit-CuTe vs adopt-the-algebra: the lowerings above are mechanical enough
   to emit as CuTe C++ (nvcc dependency, template-error surface) OR to
   reimplement the three needed ops (composition, by-mode tiler, overlapped
   staging) in F# and emit plain CUDA. The §3.3.2 divisibility checks are the
   part worth taking verbatim either way.
3. Compound-halo accessor: needs `rank_to_tuple` resident on device (it is
   already materialized host-side; the linearized_storage.hpp shared-memory
   unranking-table question is the same trade — measure, don't assume).
4. Ragged extents (the type-equal/extent-differing `stack` direction): the
   tile-level seam B generalizes — per-block `M_b` with the same window `W` —
   but CuTe's static-shape sweet spot argues for per-width instantiation via
   the existing shape monomorphization, not symbolic tiles.
