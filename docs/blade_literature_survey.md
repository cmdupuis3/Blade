# Blade-DSL: Related Literature

A curated bibliography of work relevant to Blade-DSL's design and positioning.

The sections on index types as group actions, recursion schemes, lower bounds
and equivariant learning, and the canonicalization, Tensor Contraction Engine
and bilinear-algorithm entries, were added 2026-10-13 from a four-slice
literature search run for the one-model theory (`proofs/BladeObject.v` and
following). Its verdict and the two corrections it forces are recorded in
`docs/plans/plan-paper-1-comoment.md` §8.1 ("Added 2026-10-13").

## Summary Table

| Work | Relevance to Blade |
|------|-------------------|
| Remora (Slepak 2014) | Rank polymorphism; Blade adds arity polymorphism |
| Dex (Paszke 2021) | Arrays as functions; Blade adds composable loop objects |
| Futhark (Henriksen 2017) | Functional parallelism; Blade adds loop composition |
| Strickland et al. (2009) | Variable-arity polymorphism in types; Blade ties arity to output rank/symmetry via loop objects |
| Moggi (2000) | Arity as a dependent-type universe; Blade ties arity to loop reification, commutativity, symmetric storage |
| Dzeng & Haynes (1994) | Variable-arity procedures via infinitary-tuple type inference; Blade ties arity to array rank/symmetry |
| Halide (Ragan-Kelley 2013) | Schedule separation; Blade adds algebraic combinators |
| Polyhedral (Bastoul, Bondhugula) | Compiler IR; Blade makes loops user-facing |
| Kokkos/RAJA | Single-loop parallelism; Blade handles nested loops |
| TACO (Kjolstad 2017) | Sparse tensors; Blade handles symmetric dense tensors |
| Kolda & Bader (2009) | Tensor mathematics; Blade provides language support |
| Ahlander (2003) | Index-space symmetry formalism; Blade adds type-level tracking + composition |
| Schatz et al. (2014) | Blocked symmetric storage; Blade adds general iteration, arity polymorphism |
| CTF (Solomonik 2013, 2014) | Distributed symmetric contractions; Blade adds type-level symmetry, product symmetry |
| STUR (Ghorbani 2023) | Compile-time structure inference IR; Blade adds user-facing composable abstractions |
| SySTeC (Patel 2025) | Symmetric + sparse compiler; Blade adds type-level guarantees, loop objects, product symmetry |
| Finch (Ahrens 2025) | Structured tensor compiler; Blade adds algebraic loop composition |
| MDH (Rasch 2019) | Algebraic array computation; Blade uses group (not list) homomorphisms |
| Schläfli (1852/1901) | Orthoscheme = fundamental domain of S_n on n-cube; geometric basis for r! reduction |
| Coxeter (1973) | Definitive reference for hypercube-to-orthoscheme decomposition (d! simplices) |
| van der Laan & Talman (1982) | Simplicial algorithms on simplotopes; geometric product-of-simplices, no symmetry exploitation |
| Gibbons (2017) | Naperian functors: an array is a function from its index type; no symmetry on the index |
| Mullin (1988) | Mathematics of Arrays: partial index = subarray, layout derived from a denotational normal form; no symmetry, no loop objects |
| Abbott, Altenkirch, Ghani & McBride (2004) | Quotient containers: positions carry a group action; Blade adds storage on a transversal, enumeration order and a compiler |
| Joram & Veltri (2024) | Action containers: groups act on positions directly, in HoTT; semantic, no layout |
| Joyal (1981); Yorgey (2014) | Species: structures with symmetry as functors on finite sets; no arrays, no storage |
| Chapel domains / ZPL regions | First-class index sets as iteration spaces; no symmetry quotient, no kernel-independence theorem |
| Hirata (2003) | Tensor Contraction Engine: symmetry propagated through generated contraction code, not typed |
| Manssur & Portugal (2001); Martín-García (2008) | Double-coset tensor canonicalization: slot symmetry against a stabilizer, the same computation as Blade's licence group, used to decide expression equality rather than to license a quotient |
| Solomonik & Demmel (2021) | Fast bilinear symmetric contractions: fewer multiplications than orbits in the arithmetic model; the honest limit of Blade's black-box query bound |
| Meijer, Fokkinga & Paterson (1991); Coutts et al. (2007) | Fold/unfold fusion laws and stream fusion; Blade's fusion and multi-accumulator joins are these laws over index types |
| Gibbons (2007) | Metamorphisms are fold-then-unfold; the DMWF's name must be defined against this sense |
| Uustalu & Vene (1999); Kabanov & Vene (2006) | Course-of-value recursion and dynamorphisms: the scheme behind `let rec` reading its own prefix |
| `Data.Vector.constructN` | A vector built by a function of its own prefix: `let rec`'s feedback as a library function, without the leading-axis type or the zero extension |
| Ben-Or (1983); Eberl (2017) | Decision-tree lower bounds; Eberl's AFP entry is the nearest mechanized oracle model, with no group action |
| Liu, Bernstein, Chlipala & Ragan-Kelley (2022) | Coq-verified tensor-program rewrites: semantic preservation only, no optimality, no symmetric storage |
| Knuth TAOCP 4A §7.2.1.3; Read (1978); McKay (1998) | Generating sorted tuples and the combinadic offset; one representative per orbit by canonical construction |
| Finzi, Welling & Wilson (2021) | EMLP: the equivariant basis solved numerically from the group action; Blade's S₂ partition count is the exact analogue |
| Puny et al. (2022) | Frame averaging is the Reynolds operator in equivariant learning; Blade's `reynolds(g)` manufactures a licence with it |

---

## Array Languages and Rank Polymorphism

**Slepak, J., Shivers, O., & Manolios, P. (2014).** An array-oriented language with static rank polymorphism. *ESOP 2014*.
- Formalizes rank polymorphism with frame/cell decomposition. Blade extends this with *arity* polymorphism (varying number of inputs, not just varying shape).

**Paszke, A., Johnson, D., Duvenaud, D., et al. (2021).** Getting to the Point: Index Sets and Parallelism-Preserving Autodiff for Pointful Array Computation. *ICFP 2021*.
- Dex treats arrays as memoized functions with typed indices. Related to Blade's dimensional currying, but Dex's `for` is syntax, not a first-class composable value.

**Gibbons, J. (2017).** APLicative Programming with Naperian Functors. *ESOP 2017*, LNCS 10201, 568--583.
- Multi-dimensional data as Naperian (representable) functors: an array is a function from its index type, and liftings are generated type-directed. The "array = function on its index type" thesis, without any symmetry, orbit or quotient structure on the index and without loop reification. Blade's dimensional currying is this thesis with the index carrying a group action.

**Mullin, L. M. R. (1988).** A Mathematics of Arrays. *PhD thesis, Syracuse University*. See also Mullin, L. & Raynolds, J. (2009), Tensors and n-d Arrays: A Mathematics of Arrays (MoA), psi-Calculus and the Composition of Tensor and Array Operations, *arXiv:0907.0796*.
- The psi calculus: a partial index selects a subarray (the earliest precise statement of partial indexing as currying), and a shape-based denotational normal form from which operational normal forms and memory layouts are derived by rewriting. The "layout derived from the denotation, never hand-authored" idea predates Blade by decades. No symmetric index types, no group actions, no loop objects.

**Henriksen, T., Serup, N. G. W., Elsman, M., Henglein, F., & Oancea, C. E. (2017).** Futhark: Purely Functional GPU-programming with Nested Parallelism and In-place Array Updates. *PLDI 2017*.
- Functional GPU language with SOACs (map, reduce, scan). Nested parallelism via compiler flattening, not user-facing loop composition. Loops are sequential syntax.

**Strickland, T. S., Tobin-Hochstadt, S., & Felleisen, M. (2009).** Practical Variable-Arity Polymorphism. *ESOP 2009*.
- Adds variable-arity polymorphism to a typed Scheme, giving uniform types to functions whose behavior varies with the number of arguments (e.g., `map`, `list`). Arity varies in the type system but does not drive output rank/symmetry the way Blade's arity polymorphism does; no notion of loop reification, commutativity, or symmetric storage.

**Moggi, E. (2000).** Arity Polymorphism and Dependent Types. *DTP 2000 (Workshop on Dependent Types in Programming)*.
- A four-level stratified type theory (Type ≻ Kind > Arity ≻ Kind′) giving arity-polymorphic functions (generic matrix multiply, zip) dependent types indexed by an `Arity` universe, with decidable typing and extensional type equality via prime coverings. Arities and indexes carry computational content (unlike DML), enabling case analysis over arity. Closest prior formalization of "arity" as a first-class type-level quantity; unlike Blade, arity here is a type-system device for uniform signatures over argument count, with no loop reification, no commutativity/identity-group detection, and no connection to symmetric storage or triangular iteration.

**Dzeng, H. & Haynes, C. T. (1994).** Type Reconstruction for Variable-Arity Procedures. *LFP 1994 (ACM Conference on LISP and Functional Programming)*.
- Encodes variable-arity procedures (optional arguments, arbitrarily-long argument sequences) in a core-ML variant using infinitary tuples, with an algebra of infinitary tuples, a unification algorithm over it, and a terminating type-reconstruction algorithm preserving principal typings. Earliest of the variable-arity lineage here; arity variation is handled entirely at the level of ML-style type inference for procedure arguments, with no array/tensor semantics, no output rank dependence, and no notion of loop objects, commutativity, or symmetry.

---

## Index Types as Group Actions: Quotient Containers and Species

The closest existing theory to Blade's one model (an index type is a finite set
with a group action; an array is a function on it; storage is a function on the
orbit transversal). Every item here is a theory of data types or of counting;
none has a storage layout, an enumeration order, or a compiler.

**Abbott, M., Altenkirch, T., Ghani, N., & McBride, C. (2004).** Constructing Polymorphic Programs with Quotient Types. *MPC 2004*, LNCS 3125, 2--15.
- Containers whose positions carry a subgroup of the symmetric group, so that unordered pairs, bags and cyclic lists are container types; for finitary positions these are Joyal's analytic functors. A theory of polymorphic data types: no storage on an orbit transversal, no enumeration order, no dimension-by-dimension licence, no compiler.

**Joram, P., & Veltri, N. (2024).** Data Types with Symmetries via Action Containers. *TYPES 2024 post-proceedings*, LIPIcs 336, 6:1--6:21.
- Groups acting directly on container positions, closed under products and exponentials, with a 2-category of containers in a homotopy-type-theoretic setting. Semantic, not about layout.

**Joyal, A. (1981).** Une théorie combinatoire des séries formelles. *Advances in Mathematics*, 42, 1--82. **Yorgey, B. A. (2014).** Combinatorial Species and Labelled Structures. *PhD thesis, University of Pennsylvania*.
- Species are functors from finite sets and bijections: structures with symmetry, counted by Pólya theory. Yorgey ports species to constructive type theory as a foundation for data structures with symmetry. No work in this line compiles a packed layout from the quotient or treats arrays over symmetric index sets. Pólya (1937) and Redfield (1927) are the sources for orbit counting of product and wreath actions, so Blade's input-product wreath counts are classical.

---

## Loop Abstractions and Scheduling

**Ragan-Kelley, J., Barnes, C., Adams, A., Paris, S., Durand, F., & Amarasinghe, S. (2013).** Halide: A Language and Compiler for Optimizing Parallelism, Locality, and Recomputation in Image Processing Pipelines. *PLDI 2013*.
- Separates algorithm from schedule. Schedules are directives (`.split()`, `.tile()`) on syntax, not first-class values with algebraic laws. Blade's combinators have provable identities.

**Ragan-Kelley, J., Adams, A., Sharlet, D., Barnes, C., Paris, S., Levoy, M., Amarasinghe, S., & Durand, F. (2017).** Halide: Decoupling Algorithms from Schedules for High-Performance Image Processing. *Communications of the ACM*.
- Extended treatment of Halide's algorithm/schedule separation. Demonstrates the power of schedule abstraction but not algebraic composition.

**Bastoul, C. (2004).** Code Generation in the Polyhedral Model Is Easier Than You Think. *PACT 2004*.
- Foundational polyhedral code generation. Polyhedra are compiler IR for loop analysis/transformation, not user-facing abstractions.

**Bondhugula, U., Hartono, A., Ramanujam, J., & Sadayappan, P. (2008).** A Practical Automatic Polyhedral Parallelizer and Locality Optimizer. *PLDI 2008*.
- PLuTo: automatic polyhedral parallelization. Demonstrates polyhedral power for compiler optimization; users don't compose polyhedra directly.

**Benabderrahmane, M.-W., Pouchet, L.-N., Cohen, A., & Bastoul, C. (2010).** The Polyhedral Model Is More Widely Applicable Than You Think. *CC 2010*.
- Extends polyhedral model to general control flow. Still compiler IR, not user-facing.

**Chapel Language Specification**, chapter "Domains" (chapel-lang.org/docs/language/spec/domains.html); the concept descends from ZPL regions.
- A domain is a first-class representation of an index set, used as an iteration space, including irregular, associative and sparse domains. The nearest prior art for an iteration SPACE as a first-class typed object; it carries no tuple-symmetry quotient and no theorem that the index level is independent of the kernel.

---

## Parallel Loop Constructs

**Edwards, H. C., Trott, C. R., & Sunderland, D. (2014).** Kokkos: Enabling Manycore Performance Portability through Polymorphic Memory Access Patterns. *JPDC*.
- Parallel loop abstraction for HPC portability. Single-loop `parallel_for`, not nested loop composition.

**Hornung, R. D., & Keasler, J. A. (2014).** The RAJA Portability Layer: Overview and Status. *LLNL Technical Report*.
- Portable parallel loop abstraction. Like Kokkos, focuses on individual loops, not composable nested structures.

---

## Tensor Compilers

**Kjolstad, F., Kamil, S., Chou, S., Lugato, D., & Amarasinghe, S. (2017).** The Tensor Algebra Compiler. *OOPSLA 2017*.
- TACO: format abstraction for sparse tensors via iteration graphs. Addresses sparsity structure, not symmetry. Different optimization target than Blade.

**Chen, T., Moreau, T., Jiang, Z., et al. (2018).** TVM: An Automated End-to-End Optimizing Compiler for Deep Learning. *OSDI 2018*.
- ML compiler with auto-tuning. Extends Halide scheduling to deep learning. Schedule-based, not combinator-based.

---

## Product Symmetry and Tensor Mathematics

**Kolda, T. G., & Bader, B. W. (2009).** Tensor Decompositions and Applications. *SIAM Review*.
- Comprehensive tensor survey. Documents symmetric tensor properties including the (r!)^d factor. Blade's contribution is language design preserving this structure.

---

## Symmetric Tensor Computation

**Ahlander, K. (2003).** Supporting Tensor Symmetries in EinSum. *Computers and Mathematics with Applications*, 45, 789--803.
- Establishes permutation-group-based formalism for tensor symmetries: orbits, canonical elements, sign functions on index spaces. C++ implementation in EinSum with symmetry trees for nested/mixed symmetries. Closest prior art to Blade's index-level symmetry machinery, but entirely runtime -- no type-level tracking, no loop reification, no arity polymorphism, no product symmetry.

**Schatz, M. D., Low, T. M., van de Geijn, R. A., & Kolda, T. G. (2014).** Exploiting Symmetry in Tensors for High Performance: Multiplication with Symmetric Tensors. *SIAM J. Sci. Comput.*, 36(5), C453--C479.
- Proposes Blocked Compact Symmetric Storage (BCSS) for symmetric tensors. Achieves O(m!) storage reduction and O((m+1)!/2^m) computational reduction for tensor-times-matrix. Identifies the fundamental tension between symmetry exploitation and memory access pattern regularity. Fixed arity, single operation (TTM), no general iteration composition.

**Solomonik, E., Matthews, D., Hammond, J. R., Stanton, J. F., & Demmel, J. (2014).** A massively parallel tensor contraction framework for coupled-cluster computations. *J. Parallel Distrib. Comput.*, 74(12), 3176--3190.
- Cyclops Tensor Framework (CTF): distributed-memory library with cyclic decomposition preserving tensor symmetry. Supports packed symmetric storage and Einstein notation DSL. Symmetry is a runtime attribute (SY/AS/NS per dimension), not type-level. Contraction-only (reduces to GEMM). No composable loop objects, no product symmetry across independent dimensions, limited to one sparse tensor at a time.

**Solomonik, E., Matthews, D., Hammond, J., & Demmel, J. (2013).** Cyclops Tensor Framework: Reducing Communication and Eliminating Load Imbalance in Massively Parallel Contractions. *IPDPS 2013*.
- Conference paper introducing CTF's cyclic decomposition algorithm and topology-aware mapping framework.

**Ghorbani, M., Shaikhha, A., & Alipour, M. A. (2023).** Compiling Structured Tensor Algebra. *Proc. ACM Program. Lang.*, 7(OOPSLA2).
- StructTensor framework with STUR (Structured Tensor Unified Representation), an IR capturing both computation and sparsity/redundancy structure symbolically. Infers structure at compile time via unique sets and redundancy maps. Compile-time structure reasoning aligns with Blade's philosophy, but STUR is a compiler IR, not a user-facing language with composable abstractions.

**Patel, R., Ahrens, W., & Amarasinghe, S. (2025).** SySTeC: A Symmetric Sparse Tensor Compiler. *CGO 2025*, 47--62.
- First compiler to automatically generate code for both symmetric and sparse tensor kernels. Introduces taxonomy of visible/invisible input/output symmetries, simplicial lookup tables, and diagonal splitting. Built on Finch. Achieves 1.36x--30.4x over non-symmetric baselines. Bottom-up compiler approach (term rewriting on kernel IR) vs. Blade's top-down type-system approach. No loop objects, no arity polymorphism, no product symmetry.

**Ahrens, W., Collin, T. F., Patel, R., Deeds, K., Hong, C., & Amarasinghe, S. (2025).** Finch: Sparse and Structured Tensor Programming with Control Flow. *Proc. ACM Program. Lang.*, 9(OOPSLA1).
- General-purpose sparse/structured tensor compiler combining control flow with data structure co-optimization via looplets. Substrate for SySTeC. Handles symmetry, triangles, RLE, banding as structural properties but does not provide algebraic composition of iteration patterns.

**Shi, J., Ahrens, W., Kjolstad, F., & Amarasinghe, S. (2021).** An Attempt to Generate Code for Symmetric Tensor Computations. *arXiv:2110.00186*.
- Documents a TACO-based approach to symmetric tensor code generation. Notable for acknowledging that the resulting code is "not performant when the symmetries are misaligned" -- validating that T/S compiler approaches struggle with symmetry when iteration structure doesn't align with storage structure.

**Lai, P., Sabin, G., & Sadayappan, P. (2012).** Effective Utilization of Tensor Symmetry in Operation Optimization of Tensor Contraction Expressions. *Procedia Computer Science*, 9, 412--421.
- Develops rules for detecting symmetries in intermediate tensors and cost models for symmetric contractions. Optimizes contraction ordering but does not generate code. Demonstrates that symmetry reasoning can substantially reduce operation counts in coupled-cluster methods.

**Spampinato, D. G. & Puschel, M. (2016).** A basic linear algebra compiler for structured matrices. *CGO 2016*.
- sBLACs: generates optimized code for structured matrix operations including symmetric matrices. Dense only, no sparse support, limited to matrices (rank 2).

**Hirata, S. (2003).** Tensor Contraction Engine: Abstraction and Automated Parallel Implementation of Configuration-Interaction, Coupled-Cluster, and Many-Body Perturbation Theories. *J. Phys. Chem. A*, 107(46), 9887--9897.
- Symbolic generation of coupled-cluster code exploiting spin, Abelian point-group and index-permutation symmetry, with symmetry propagated through intermediates. Symmetry is a property of generated code, not of a type.

**Manssur, L. R. U., & Portugal, R. (2001).** Group-theoretic Approach for Symbolic Tensor Manipulation: II. Dummy Indices. *arXiv:math-ph/0107032*. **Martín-García, J. M. (2008).** xPerm: fast index canonicalization for tensor computer algebra. *Computer Physics Communications*, 179, 597--603 (arXiv:0803.0862).
- Tensor canonicalization as a double-coset problem: the canonical form of an indexed object is the canonical representative of S·g·D, with S the slot-symmetry group and D the dummy-renaming group, computed with Butler's (1984) algorithm. This is the same family of computation as Blade's licence group H ∩ Stab (declared position symmetries restricted to the positions that actually coincide). There it decides equality of expressions; in Blade it licenses an iteration and storage quotient. Also implemented in Cadabra (Peeters) and SymPy's `tensor_can`.

**Solomonik, E., & Demmel, J. (2021).** Fast Bilinear Algorithms for Symmetric Tensor Contractions. *Computational Methods in Applied Mathematics*, 21(1), 211--231. **Solomonik, E., Demmel, J., & Hoefler, T. (2017).** Communication Lower Bounds of Bilinear Algorithms for Symmetric Tensor Contractions. *arXiv:1707.04618*.
- A symmetry-preserving bilinear algorithm computes a symmetric contraction with fewer multiplications than there are distinct output cells, by forming products of sums of operand entries and subtracting the unwanted partial sums: a saving of up to (s+t+v)!/(s!t!v!) in the arithmetic (bilinear-rank) model. Those products are not evaluations of a kernel at index tuples, so the result does not contradict Blade's orbit bound, which counts black-box kernel evaluations; it is the honest limit of that bound, and every statement of the bound must name its model. The 2017 paper shows the arithmetic saving can cost more communication.

---

## Multi-Dimensional Homomorphisms

**Rasch, A. & Gorlatch, S. (2019).** Multi-Dimensional Homomorphisms and Their Implementation in OpenCL. *Int. J. Parallel Prog.*, 47, 999--1017.
- Algebraic formalism for data-parallel array computation using list homomorphisms (functions distributing over concatenation). Like Blade, MDH uses algebraic properties to drive optimization. Key difference: MDH's homomorphisms are over concatenation; Blade's symmetry lowering uses group homomorphisms over permutation groups. MDH does not address symmetric tensors or triangular iteration.

---

## Functional Programming Abstractions

**Moggi, E. (1991).** Notions of Computation and Monads. *Information and Computation*.
- Foundational work on monads for computation. Blade's Computation type is monadic; the MonadPlus structure enables choice and failure.

**Wadler, P. (1992).** The Essence of Functional Programming. *POPL 1992*.
- Monads for structuring programs. Blade extends with MonadPlus (zero + choice) for conditional computation.

---

## Recursion Schemes and Program Calculation

The DMWF's components map onto named schemes. The combination (a
dependent-residual unfold over a symmetric index type, a fold, and a
prefix-reading feedback arm, with the quotient property proved) is not a named
scheme in this literature, and the name itself collides with Gibbons' usage.

**Meijer, E., Fokkinga, M., & Paterson, R. (1991).** Functional Programming with Bananas, Lenses, Envelopes and Barbed Wire. *FPCA 1991*, 124--144. **Takano, A., & Meijer, E. (1995).** Shortcut Deforestation in Calculational Form. *FPCA 1995*, 306--313. **Coutts, D., Leshchinskiy, R., & Stewart, D. (2007).** Stream Fusion: From Lists to Streams to Nothing at All. *ICFP 2007*.
- Catamorphism, anamorphism and hylomorphism laws, fold tupling (banana-split), hylomorphism fusion, and stream fusion. Blade's map-chain fusion, several-reductions-in-one-pass joins and kernel-invariant hoisting are these laws (and ordinary loop-invariant code motion) instantiated over index types: cite, do not claim.

**Gibbons, J. (2007).** Metamorphisms: Streaming Representation-Changers. *Science of Computer Programming*, 65(2), 108--139 (earlier *MPC 2004*, LNCS 3125, 142--168).
- A metamorphism is an unfold AFTER a fold (a representation changer), with streaming conditions under which the unfold starts before the fold finishes. Blade's Double Metamorphism With Feedback has a different shape (an unfold with a state-dependent coalgebra, a fold, and a feedback arm), so the term must be defined against Gibbons' sense on first use or renamed. No source uses "double metamorphism" or "metamorphism with feedback".

**Uustalu, T., & Vene, V. (1999).** Primitive (Co)Recursion and Course-of-Value (Co)Iteration, Categorically. *Informatica*, 10(1), 5--26. **Kabanov, J., & Vene, V. (2006).** Recursion Schemes for Dynamic Programming. *MPC 2006*, LNCS 4014, 235--252. **Hinze, R., & Wu, N. (2013).** Histo- and Dynamorphisms Revisited. *WGP 2013*.
- Histomorphisms (course-of-value recursion) and dynamorphisms (hylomorphisms with histomorphic memoization) are the formal account of a value defined from its own earlier values; Hinze and Wu set them against array-based dynamic programming. Blade's `let rec` on the leading axis is this scheme specialized to one array axis, with a static extent, a pre-allocated buffer, implicit zeros past the prefix, a `while` guard, and the N × orbits work theorem; none of those are in the scheme literature.

**Leshchinskiy, R., et al.** `Data.Vector.constructN` (the Haskell `vector` package).
- Builds a vector of n elements by repeatedly applying the generator to the part already built: the prefix-reading construction of `let rec` as a library function, without the leading-axis type, the zero extension, or any link to index types.

---

## Lower Bounds and Mechanized Optimality

**Ben-Or, M. (1983).** Lower Bounds for Algebraic Computation Trees. *STOC 1983*.
- The classical decision-tree and algebraic-computation-tree lower bounds (topological, by connected components). The "every output must be computed" and adversary arguments behind Blade's orbit bound are textbook; no published bound was found that is stated in terms of orbits of a group action on the index set.

**Eberl, M. (2017).** Lower bound on comparison-based sorting algorithms. *Archive of Formal Proofs*.
- Mechanizes a query-counting lower bound in a shallow embedding where an algorithm either returns or queries an oracle with a continuation: structurally the same oracle model as `BladeOptimal`, for one problem, with no group action, no stabilizers, no sign character and no pipelines.

**Liu, A., Bernstein, G. L., Chlipala, A., & Ragan-Kelley, J. (2022).** Verified Tensor-Program Optimization via High-level Scheduling Rewrites. *POPL 2022*.
- Coq-verified semantic preservation of array-compiler scheduling rewrites. Correctness only: no optimality statement, no symmetric storage. With Eberl, the nearest mechanized neighbours; no proof-assistant development was found that proves an array compiler's layout or iteration decision optimal.

---

## Equivariant Learning

**Finzi, M., Welling, M., & Wilson, A. G. (2021).** A Practical Method for Constructing Equivariant Multilayer Perceptrons for Arbitrary Matrix Groups. *ICML 2021* (arXiv:2104.09459).
- Builds representations compositionally and solves for the equivariant basis numerically as the nullspace of stacked generator constraints; for S_n the basis size tends to the Bell number. Blade's S₂ partition of a self-tensor weight space (`BladeSymPower`, `BladePartition`) is the exact-combinatorial analogue, derived as a theorem rather than computed.

**Puny, O., Atzmon, M., Ben-Hamu, H., Misra, I., Grover, A., Smith, E. J., & Lipman, Y. (2022).** Frame Averaging for Invariant and Equivariant Network Design. *ICLR 2022* (arXiv:2110.03336).
- Group averaging (the Reynolds operator of classical invariant theory) to build invariant and equivariant networks. Blade's `reynolds(g)` applies the same operator to manufacture a commutative kernel, that is, a licence for the compiler's iteration quotient.

---

## Geometric and Combinatorial Foundations

**Schläfli, L. (1852/1901).** *Theorie der vielfachen Kontinuität*. Written 1850--1852; published posthumously, Denkschriften der Schweizerischen naturforschenden Gesellschaft, 38, 1901.
- Foundational work on higher-dimensional geometry. Introduces the Schläfli orthoscheme (path-simplex with mutually orthogonal edges) and establishes that the ordered simplex {x_1 <= x_2 <= ... <= x_n} is a fundamental domain for the action of S_n on the n-cube, tiling it into n! congruent copies. This is the geometric basis for Blade's r! iteration reduction per dimension. Publication was rejected by both the Vienna and Berlin academies due to length; portions appeared via Cayley in 1860.

**Coxeter, H. S. M. (1973).** *Regular Polytopes*. 3rd edition, Dover, New York.
- Comprehensive treatment of polytopes and their symmetry groups. Systematizes Schläfli's orthoscheme results: every d-dimensional hypercube can be dissected into d! congruent orthoschemes (Sec. 5.43 and Ch. 7). The characteristic orthoscheme is the fundamental region of the polytope's symmetry group. Provides the definitive reference for the n-cube-to-n!-simplices decomposition that underlies triangular iteration bounds.

**van der Laan, G. & Talman, A. J. J. (1982).** On the computation of fixed points in the product space of unit simplices and an application to noncooperative N-person games. *Mathematics of Operations Research*, 7(1), 1--13.
- Introduces simplicial algorithms on the *simplotope* (Cartesian product of unit simplices) for computing Nash equilibria. The simplotope is the geometric object corresponding to Blade's product-of-simplices iteration domain. However, this work addresses fixed-point computation via triangulation of the product space -- not iteration-space reduction for tensor computation. No connection is drawn to symmetry exploitation or factorial speedups.

**Talman, D. (1994).** Intersection Theorems on the Unit Simplex and the Simplotope. In: Gilles, R. P., Ruys, P. H. M. (eds) *Imperfections and Behavior in Economic Organizations*, Springer.
- Generalizes KKM-type intersection theorems from the unit simplex to the simplotope. Establishes combinatorial properties of the product-of-simplices structure relevant to game theory. Again, no connection to iteration domains or symmetry exploitation in computation.

**Knuth, D. E. (2011).** *The Art of Computer Programming*, Vol. 4A, §7.2.1.3 "Generating all combinations". Addison-Wesley.
- Lexicographic generation of combinations with residual ranges, and the combinatorial number system (Lehmer 1964; the name is Knuth's) that ranks a sorted tuple by a sum of binomials. `BladeDMWF`'s enumeration and left-justified storage bijection are this material with a mechanized proof attached.

**Read, R. C. (1978).** Every One a Winner. *Annals of Discrete Mathematics*, 2, 107--120. **McKay, B. D. (1998).** Isomorph-Free Exhaustive Generation. *Journal of Algorithms*, 26, 306--324.
- Orderly generation and canonical augmentation: emit exactly one representative per orbit by a canonical construction. Sorting as the canonical form for S_n is the degenerate case. The theorem that the packed enumeration is the image of the box under canonical form with fibres the orbits appears here as combinatorics, never as a statement about a compiler's storage.

### Relevance to product symmetry

The mathematical fact underlying Blade's (r!)^d speedup is classical: the volume of a product of simplices equals the product of individual simplex volumes, each being 1/r! of the corresponding cube. This follows from Fubini's theorem and was implicit in Schläfli's work. However, no reference in the geometric or combinatorial literature connects this volume factorization to *iteration-space reduction in programming*. The observation that dimensional currying is required to preserve the product structure (flattening collapses S_r^d to S_(r*d), losing the exponential speedup) appears to be original to Blade.
