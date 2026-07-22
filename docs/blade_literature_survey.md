# Blade-DSL: Related Literature

A curated bibliography of work relevant to Blade-DSL's design and positioning.

---

## Array Languages and Rank Polymorphism

**Slepak, J., Shivers, O., & Manolios, P. (2014).** An array-oriented language with static rank polymorphism. *ESOP 2014*.
- Formalizes rank polymorphism with frame/cell decomposition. Blade extends this with *arity* polymorphism (varying number of inputs, not just varying shape).

**Paszke, A., Johnson, D., Duvenaud, D., et al. (2021).** Getting to the Point: Index Sets and Parallelism-Preserving Autodiff for Pointful Array Computation. *ICFP 2021*.
- Dex treats arrays as memoized functions with typed indices. Related to Blade's dimensional currying, but Dex's `for` is syntax, not a first-class composable value.

**Henriksen, T., Serup, N. G. W., Elsman, M., Henglein, F., & Oancea, C. E. (2017).** Futhark: Purely Functional GPU-programming with Nested Parallelism and In-place Array Updates. *PLDI 2017*.
- Functional GPU language with SOACs (map, reduce, scan). Nested parallelism via compiler flattening, not user-facing loop composition. Loops are sequential syntax.

**Strickland, T. S., Tobin-Hochstadt, S., & Felleisen, M. (2009).** Practical Variable-Arity Polymorphism. *ESOP 2009*.
- Adds variable-arity polymorphism to a typed Scheme, giving uniform types to functions whose behavior varies with the number of arguments (e.g., `map`, `list`). Arity varies in the type system but does not drive output rank/symmetry the way Blade's arity polymorphism does; no notion of loop reification, commutativity, or symmetric storage.

**Moggi, E. (2000).** Arity Polymorphism and Dependent Types. *DTP 2000 (Workshop on Dependent Types in Programming)*.
- A four-level stratified type theory (Type ≻ Kind > Arity ≻ Kind′) giving arity-polymorphic functions (generic matrix multiply, zip) dependent types indexed by an `Arity` universe, with decidable typing and extensional type equality via prime coverings. Arities and indexes carry computational content (unlike DML), enabling case analysis over arity. Closest prior formalization of "arity" as a first-class type-level quantity; unlike Blade, arity here is a type-system device for uniform signatures over argument count, with no loop reification, no commutativity/identity-group detection, and no connection to symmetric storage or triangular iteration.

**Dzeng, H. & Haynes, C. T. (1994).** Type Reconstruction for Variable-Arity Procedures. *LFP 1994 (ACM Conference on LISP and Functional Programming)*.
- Encodes variable-arity procedures (optional arguments, arbitrarily-long argument sequences) in a core-ML variant using infinitary tuples, with an algebra of infinitary tuples, a unification algorithm over it, and a terminating type-reconstruction algorithm preserving principal typings. Earliest of the variable-arity lineage here; arity variation is handled entirely at the level of ML-style type inference for procedure arguments, with no array/tensor semantics, no output rank dependence, and no notion of loop objects, commutativity, or symmetry.

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

## Geometric and Combinatorial Foundations

**Schläfli, L. (1852/1901).** *Theorie der vielfachen Kontinuität*. Written 1850--1852; published posthumously, Denkschriften der Schweizerischen naturforschenden Gesellschaft, 38, 1901.
- Foundational work on higher-dimensional geometry. Introduces the Schläfli orthoscheme (path-simplex with mutually orthogonal edges) and establishes that the ordered simplex {x_1 <= x_2 <= ... <= x_n} is a fundamental domain for the action of S_n on the n-cube, tiling it into n! congruent copies. This is the geometric basis for Blade's r! iteration reduction per dimension. Publication was rejected by both the Vienna and Berlin academies due to length; portions appeared via Cayley in 1860.

**Coxeter, H. S. M. (1973).** *Regular Polytopes*. 3rd edition, Dover, New York.
- Comprehensive treatment of polytopes and their symmetry groups. Systematizes Schläfli's orthoscheme results: every d-dimensional hypercube can be dissected into d! congruent orthoschemes (Sec. 5.43 and Ch. 7). The characteristic orthoscheme is the fundamental region of the polytope's symmetry group. Provides the definitive reference for the n-cube-to-n!-simplices decomposition that underlies triangular iteration bounds.

**van der Laan, G. & Talman, A. J. J. (1982).** On the computation of fixed points in the product space of unit simplices and an application to noncooperative N-person games. *Mathematics of Operations Research*, 7(1), 1--13.
- Introduces simplicial algorithms on the *simplotope* (Cartesian product of unit simplices) for computing Nash equilibria. The simplotope is the geometric object corresponding to Blade's product-of-simplices iteration domain. However, this work addresses fixed-point computation via triangulation of the product space -- not iteration-space reduction for tensor computation. No connection is drawn to symmetry exploitation or factorial speedups.

**Talman, D. (1994).** Intersection Theorems on the Unit Simplex and the Simplotope. In: Gilles, R. P., Ruys, P. H. M. (eds) *Imperfections and Behavior in Economic Organizations*, Springer.
- Generalizes KKM-type intersection theorems from the unit simplex to the simplotope. Establishes combinatorial properties of the product-of-simplices structure relevant to game theory. Again, no connection to iteration domains or symmetry exploitation in computation.

### Relevance to product symmetry

The mathematical fact underlying Blade's (r!)^d speedup is classical: the volume of a product of simplices equals the product of individual simplex volumes, each being 1/r! of the corresponding cube. This follows from Fubini's theorem and was implicit in Schläfli's work. However, no reference in the geometric or combinatorial literature connects this volume factorization to *iteration-space reduction in programming*. The observation that dimensional currying is required to preserve the product structure (flattening collapses S_r^d to S_(r*d), losing the exponential speedup) appears to be original to Blade.

---

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

---

## Citation Format

For the formalism document, use:

```
[Henriksen17] Henriksen et al., "Futhark: Purely Functional GPU-programming," PLDI 2017.
[Paszke21] Paszke et al., "Getting to the Point," ICFP 2021.
[RaganKelley13] Ragan-Kelley et al., "Halide," PLDI 2013.
[Bastoul04] Bastoul, "Code Generation in the Polyhedral Model," PACT 2004.
[Kjolstad17] Kjolstad et al., "The Tensor Algebra Compiler," OOPSLA 2017.
[Slepak14] Slepak et al., "An array-oriented language with static rank polymorphism," ESOP 2014.
[Strickland09] Strickland, Tobin-Hochstadt, & Felleisen, "Practical Variable-Arity Polymorphism," ESOP 2009.
[Moggi00] Moggi, "Arity Polymorphism and Dependent Types," DTP 2000.
[Dzeng94] Dzeng & Haynes, "Type Reconstruction for Variable-Arity Procedures," LFP 1994.
[Kolda09] Kolda & Bader, "Tensor Decompositions and Applications," SIAM Review 2009.
[Ahlander03] Ahlander, "Supporting Tensor Symmetries in EinSum," Comput. Math. Appl. 2003.
[Schatz14] Schatz et al., "Exploiting Symmetry in Tensors for High Performance," SIAM J. Sci. Comput. 2014.
[Solomonik14] Solomonik et al., "A massively parallel tensor contraction framework," JPDC 2014.
[Solomonik13] Solomonik et al., "Cyclops Tensor Framework," IPDPS 2013.
[Ghorbani23] Ghorbani et al., "Compiling Structured Tensor Algebra," OOPSLA 2023.
[Patel25] Patel et al., "SySTeC: A Symmetric Sparse Tensor Compiler," CGO 2025.
[Ahrens25] Ahrens et al., "Finch: Sparse and Structured Tensor Programming with Control Flow," OOPSLA 2025.
[Shi21] Shi et al., "An Attempt to Generate Code for Symmetric Tensor Computations," arXiv 2021.
[Lai12] Lai et al., "Effective Utilization of Tensor Symmetry," Procedia Computer Science 2012.
[Spampinato16] Spampinato & Puschel, "A basic linear algebra compiler for structured matrices," CGO 2016.
[Rasch19] Rasch & Gorlatch, "Multi-Dimensional Homomorphisms," Int. J. Parallel Prog. 2019.
[Schlafli1852] Schläfli, "Theorie der vielfachen Kontinuität," written 1850-52, publ. 1901.
[Coxeter73] Coxeter, "Regular Polytopes," 3rd ed., Dover 1973.
[vanderLaan82] van der Laan & Talman, "Fixed points on the product space of unit simplices," Math. Oper. Res. 1982.
```
