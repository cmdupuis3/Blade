**The Fortran experts are credible implementations, but I would not approve publication yet.** `comoment3` changes the specified arithmetic grouping, and `maskagg` gives Blade reassociation permission that Fortran is explicitly denied. Several smaller omissions also disadvantage Fortran.

I read all requested sources and checked relevant archived Blade emissions. No files were changed or created. Proposed patches are uncompiled and unmeasured; the numerical counterexample below was evaluated in memory.

The five performance changes I would test first, ranked by likely benefit in their applicable regimes, are:

| Rank | Change | Finding | Expected mechanism |
|---:|---|---|---|
| 1 | Give each `recur` thread ownership of complete trajectories | F4 | Remove hundreds of barriers and reductions |
| 2 | Match Blade’s licensed input-checksum reductions | F3 | Remove long scalar addition chains in allocation-inclusive totals |
| 3 | Correct `maskagg`’s licence map and vectorize its sums | F2 | SIMD checksum and input sum; compaction remains sequential |
| 4 | Vectorize contraction-output checksums | F5 | Use already-permitted reassociation in gram/comoment3/antisym |
| 5 | Sum Gram input rows in small contiguous groups | F6 | Better cache-line use and independent SIMD accumulators without reassociation |

**F1 — SEVERITY: blocker — `bench/kernels/comoment3/expert.f90:82,95,183`: the expert changes each term’s multiplication grouping.**

Blade’s canonical cell uses `(Xi * Xj) * Xk`. The expert precomputes `Xj * Xk` and accumulates `Xi * (Xj * Xk)`. Its comment at lines 12–13 justifies this by permuting the tensor indices, but mathematical symmetry does not make floating-point multiplication associative. The licensed permission concerns accumulation over samples; it does not authorize this regrouping.

Concrete evidence:

- `comoment3.blade:26`: `prodsum(a, b, c) / Float64(n)`.
- Archived repro C++ at `bench/.build/comoment3/smoke/serial-repro-native/blade-cpp/generated/comoment3.cpp:310` accumulates `a[s] * b[s] * c[s]` for ascending canonical indices.
- Using the actual smoke generator, ascending samples, and an FMA for the final multiply-add, canonical cell **`(0,0,6)`** differs:

```text
(Xi * Xj) * Xk: 0x1.63320f8361ea8p-3
Xi * (Xj * Xk): 0x1.63320f8361ea9p-3
```

The current checksum tolerance hides this one-ULP defect.

Keep the microkernel, but form an `Xi * Xj` panel and reuse it across the eligible `k` panels. This preserves ascending accumulation and the canonical product grouping in both regimes. The extra private panel is only 12,864 bytes at full size.

```diff
--- a/bench/kernels/comoment3/expert.f90
+++ b/bench/kernels/comoment3/expert.f90
@@ -5,12 +5,10 @@
 ! Expert choices (bench/notes/fortran-notes.md, "comoment3"):
 !  * canonical cells only, i <= j <= k, packed simplex ordered by k, then j, then i:
 !    T(i,j,k) at tp((k-1)k(k+1)/6 + (j-1)j/2 + i)  (1-based) -- i contiguous;
-!  * for each middle index j the canonical slab {i <= j} x {k >= j} is one rectangular
-!    product  T(:, j, :) = X(1:j, :) . Y_j^T  with  Y_j(k, s) = X(j,s) X(k,s)  (formed once
-!    per (j, k, s), shared by every i): the same register-blocked MR x NR micro-kernel as gram,
-!    vectorized over i, every cell ONE sequential FMA chain over s = 1..n (strict-safe).  The
-!    term X(i,s) * (X(j,s) X(k,s)) is the spec's left-to-right triple product for the index
-!    order (j, k, i) of this symmetric tensor;
+!  * for each middle index j, pack (X(i,s) * X(j,s)) for one MR-wide i panel,
+!    then multiply against raw X(k,s) panels for k >= j. The MR x NR micro-kernel
+!    vectorizes independent cells over i, retaining ascending s and the canonical
+!    product grouping (X(i,s) * X(j,s)) * X(k,s);
 !  * one division by n per cell after the sum (as the Blade source: prodsum(...) / n);
 !  * checksum read back from the simplex with multiplicities 1 / 3 / 6;
 !  * mt8: schedule(dynamic) over j (the kernel loop); each thread owns its Y_j panel buffer;
@@ -33,7 +31,7 @@
   integer, parameter :: ncell = p * (p + 1) * (p + 2) / 6
 
   real(real64), allocatable :: x(:,:), ap(:,:,:), bp(:,:,:), tp(:), rs(:)
-  real(real64) :: c(mr, nr), cs, ics, w, rn
+  real(real64) :: c(mr, nr), weighted(mr, n), cs, ics, w, rn
   integer :: i, j, k, s, ip, kp, ii, kk, i0, k0, nip, nkp, m, off
 
   call bench_start()
@@ -67,11 +65,11 @@
 
   ! ---- the simplex, one slab per middle index j
   rn = real(n, real64)
-  !$omp parallel private(bp, j, nkp, nip, kp, k0, s, kk, k, ip, i0, c, m, off)
+  !$omp parallel private(bp, j, nkp, nip, kp, k0, s, kk, k, ip, i0, ii, c, weighted, m, off)
   allocate(bp(nr, n, npbmax))
   !$omp do schedule(dynamic)
   do j = 1, p
-    ! Y_j panels: bp(kk, s, kp) = X(j,s) * X(k,s), k = j + (kp-1)*nr + kk - 1 <= p
+    ! Raw X(k,s) panels, k = j + (kp-1)*nr + kk - 1 <= p
     nkp = (p - j) / nr + 1
     do kp = 1, nkp
       k0 = j + (kp - 1) * nr - 1
@@ -79,7 +77,7 @@
         do kk = 1, nr
           k = k0 + kk
           if (k <= p) then
-            bp(kk, s, kp) = x(j, s) * x(k, s)
+            bp(kk, s, kp) = x(k, s)
           else
             bp(kk, s, kp) = 0.0_real64
           end if
@@ -87,12 +85,19 @@
       end do
     end do
     nip = (j - 1) / mr + 1
-    do kp = 1, nkp
-      k0 = j + (kp - 1) * nr - 1
-      do ip = 1, nip
-        i0 = (ip - 1) * mr
+    do ip = 1, nip
+      i0 = (ip - 1) * mr
+      ! Form (Xi * Xj) once, retaining the canonical product grouping.
+      do s = 1, n
+        !$omp simd
+        do ii = 1, mr
+          weighted(ii, s) = ap(ii, s, ip) * x(j, s)
+        end do
+      end do
+      do kp = 1, nkp
+        k0 = j + (kp - 1) * nr - 1
         c = 0.0_real64
-        call tile(ap(1, 1, ip), bp(1, 1, kp), c)
+        call tile(weighted, bp(1, 1, kp), c)
         m = min(mr, j - i0)
         do kk = 1, nr
           k = k0 + kk
```

Two associated specification corrections are needed:

- `comoment3/spec.md:12` says “multiply by 1/n”; both Fortran tiers correctly **divide by n once per cell**, matching Blade. Keep the division.
- A dense natural arm evaluates different permutations independently, whereas packed arms reuse one canonical result. The protocol must distinguish that permitted permutation-rounding difference from regrouping the canonical cell itself. It cannot promise universal bitwise equality merely from ascending sample order.

**F2 — SEVERITY: blocker — `bench/kernels/maskagg/spec.md:79` and `expert.f90:43,52`: the claimed unlicensed exception is false in the archived Blade implementation. Performance rank 3.**

The spec says licensed and repro coincide because the knob does not reach the compound fold. The expert consequently leaves both sums sequential.

The matching archived sources contradict that:

- Licensed `bench/.build/maskagg/smoke/serial-licensed-native/blade-cpp/generated/maskagg.cpp:259` contains eight `__rlane` accumulators for the output checksum.
- Its input checksum is explicitly labelled an eager eight-lane reduction at line 262.
- Repro’s corresponding line 259 contains one sequential accumulator; its input sum is sequential too.
- Both archived `.blade` sources match the current source except for size.
- `bench/notes/llvm-acceptance-B.md:436` independently records both licensed folds.

Correct the README exception and the spec’s matched-permission list, then apply the following. This changes only licensed sums; the stable compaction stays sequential and materializes `y(1:m)`.

```diff
--- a/bench/kernels/maskagg/expert.f90
+++ b/bench/kernels/maskagg/expert.f90
@@ -40,6 +40,9 @@
     y(m + 1) = v
     m = m + merge(1, 0, v > 1.0_real64)
   end do
+#ifdef BENCH_LICENSED
+  !$omp simd reduction(+:acc)
+#endif
   do k = 1, m
     acc = acc + y(k) * y(k)
   end do
@@ -49,6 +52,9 @@
 #endif
 
   ics = 0.0_real64
+#ifdef BENCH_LICENSED
+  !$omp simd reduction(+:ics)
+#endif
   do k = 1, n
     ics = ics + x(k)
   end do
```

The existing branch-free compaction is otherwise good. At iteration `k`, `m <= k-1`, so the unconditional `y(m+1)` store is within the allocated `y(n)`, even for all-kept or all-rejected inputs. Rejected values never corrupt the retained prefix.

**F3 — SEVERITY: major — expert input-checksum loops omit matched licensed SIMD permission. Performance rank 2.**

Affected examples:

- `fusedchain/expert.f90:60`
- `onepass/expert.f90:65`
- `dot/expert.f90:51`
- `saxpy/expert.f90:53`
- `stencil/expert.f90:66`
- `recur/expert.f90:105`

These are substantial timed passes, particularly the 40 MB `onepass` input sum and the two-input streaming controls. Keeping their accumulation serial can mask improvements to the actual kernel.

This is supported by the recorded Blade emission, not an inferred licence: `llvm-acceptance-B.md:430–435` lists the licensed input folds; `llvm-acceptance-A.md:670–673` records them for group A too.

The protocol’s permission inventory needs to explicitly include **input-checksum folds**, retaining the existing grouping between separate inputs and rows. Do not interpret the current “Nowhere else” lists as permission to add these silently. Once corrected, these placements match Blade:

```diff
--- a/bench/kernels/fusedchain/expert.f90
+++ b/bench/kernels/fusedchain/expert.f90
@@ -57,6 +57,9 @@
 
-  ! input checksum: sequential, ascending k (the order of the Blade source's reduce)
+  ! input checksum: ascending in repro; matched SIMD reduction in licensed
   ics = 0.0_real64
+#ifdef BENCH_LICENSED
+  !$omp simd reduction(+:ics)
+#endif
   do k = 1, n
     ics = ics + a(k)
   end do
--- a/bench/kernels/onepass/expert.f90
+++ b/bench/kernels/onepass/expert.f90
@@ -62,6 +62,9 @@
 
-  ! input checksum: sequential, ascending k (the order of the Blade source's reduce)
+  ! input checksum: ascending in repro; matched SIMD reduction in licensed
   ics = 0.0_real64
+#ifdef BENCH_LICENSED
+  !$omp simd reduction(+:ics)
+#endif
   do k = 1, n
     ics = ics + x(k)
   end do
--- a/bench/kernels/dot/expert.f90
+++ b/bench/kernels/dot/expert.f90
@@ -48,6 +48,9 @@
-  ! input checksum in the order of the Blade source: (sum a) + (sum b), each ascending
+  ! input checksum: separate sums; ascending in repro, matched SIMD in licensed
   sa = 0.0_real64
   sb = 0.0_real64
+#ifdef BENCH_LICENSED
+  !$omp simd reduction(+:sa, sb)
+#endif
   do k = 1, n
     sa = sa + a(k)
     sb = sb + b(k)
--- a/bench/kernels/saxpy/expert.f90
+++ b/bench/kernels/saxpy/expert.f90
@@ -50,6 +50,9 @@
-  ! input checksum in the order of the Blade source: (sum x) + (sum y), each ascending
+  ! input checksum: separate sums; ascending in repro, matched SIMD in licensed
   sx = 0.0_real64
   sy = 0.0_real64
+#ifdef BENCH_LICENSED
+  !$omp simd reduction(+:sx, sy)
+#endif
   do k = 1, n
     sx = sx + x(k)
     sy = sy + y(k)
--- a/bench/kernels/stencil/expert.f90
+++ b/bench/kernels/stencil/expert.f90
@@ -63,6 +63,9 @@
   !$omp parallel do private(j, w)
   do i = 1, ny
     w = 0.0_real64
+#ifdef BENCH_LICENSED
+    !$omp simd reduction(+:w)
+#endif
     do j = 1, nx
       w = w + f(j, i)
     end do
--- a/bench/kernels/recur/expert.f90
+++ b/bench/kernels/recur/expert.f90
@@ -102,6 +102,9 @@
 #endif
 
   ics = 0.0_real64
+#ifdef BENCH_LICENSED
+  !$omp simd reduction(+:ics)
+#endif
   do x = 1, nx
     ics = ics + u(x, 1)
   end do
```

These patches leave repro unchanged. They also apply consistently to input-only twins because the sums remain outside `BENCH_INPUTONLY`.

**F4 — SEVERITY: major — `bench/kernels/recur/expert.f90:48–101`: mt8 synchronizes independent trajectories at every step. Performance rank 1.**

The persistent parallel region is good, but each of 300 steps ends both a production worksharing loop and a checksum-reduction loop. Together with the initial checksum, that is **601 worksharing synchronization points**, plus the enclosing parallel-region completion.

There is no cross-trajectory dependency. Each thread can own a contiguous range of `x` for every time step and combine its checksum once at region exit. That preserves every trajectory cell’s arithmetic and stores the entire trajectory.

The notes’ rejected serial blocking experiments do not establish that this threaded version is slower: this change targets synchronization, leaves serial code unchanged, and gives each of eight threads roughly 2,500 contiguous elements per step.

```diff
--- a/bench/kernels/recur/expert.f90
+++ b/bench/kernels/recur/expert.f90
@@ -28,6 +28,9 @@
 program recur_expert
   use, intrinsic :: iso_fortran_env, only: real64
   use bench_support
+#ifdef _OPENMP
+  use omp_lib, only: omp_get_thread_num, omp_get_num_threads
+#endif
   implicit none
   integer, parameter :: nt = 301     ! SIZE nt
   integer, parameter :: nx = 20011   ! SIZE nx
@@ -35,7 +38,7 @@
   real(real64), parameter :: lo = 0.0_real64, hi = 2.0_real64
   real(real64), allocatable :: u(:,:)
   real(real64) :: cs, ics, v, a1, a2, a3, a4, y, w
-  integer :: t, x
+  integer :: t, x, tid, team, xlo, xhi
 
   call bench_start()
   allocate(u(nx, nt))
@@ -45,6 +48,45 @@
 
   cs = 0.0_real64
 #ifndef BENCH_INPUTONLY
+#ifdef _OPENMP
+  ! Each thread owns whole trajectories; no cross-thread time-step dependence.
+  !$omp parallel private(t, x, tid, team, xlo, xhi, v, a1, a2, a3, a4, y, w) reduction(+:cs)
+  tid = omp_get_thread_num()
+  team = omp_get_num_threads()
+  xlo = 1 + (nx * tid) / team
+  xhi = (nx * (tid + 1)) / team
+  w = 0.0_real64
+#ifdef BENCH_LICENSED
+  !$omp simd reduction(+:w)
+#endif
+  do x = xlo, xhi
+    w = w + u(x, 1) * u(x, 1)
+  end do
+  cs = cs + w
+  do t = 2, nt
+    !$omp simd private(v, a1, a2, a3, a4, y)
+    do x = xlo, xhi
+      v = u(x, t - 1)
+      a1 = v - v * v * v
+      y = v + 0.005_real64 * a1
+      a2 = y - y * y * y
+      y = v + 0.005_real64 * a2
+      a3 = y - y * y * y
+      y = v + 0.01_real64 * a3
+      a4 = y - y * y * y
+      u(x, t) = v + (0.01_real64 / 6.0_real64) * (a1 + 2.0_real64 * a2 + 2.0_real64 * a3 + a4)
+    end do
+    w = 0.0_real64
+#ifdef BENCH_LICENSED
+    !$omp simd reduction(+:w)
+#endif
+    do x = xlo, xhi
+      w = w + u(x, t) * u(x, t)
+    end do
+    cs = cs + w
+  end do
+  !$omp end parallel
+#else
   !$omp parallel private(t, v, a1, a2, a3, a4, y)
 #ifdef _OPENMP
 #ifdef BENCH_LICENSED
@@ -100,6 +142,7 @@
   end do
   !$omp end parallel
 #endif
+#endif
 
   ics = 0.0_real64
   do x = 1, nx
```

The non-reduction SIMD directive on the stage loop operates across independent trajectories. The checksum’s SIMD reduction remains guarded. Thread-level checksum regrouping is already permitted in mt8 repro.

The nested `simd reduction(+:w)` reduces a thread-private scalar; it is not a worksharing reduction on a variable private in the enclosing parallel region. That distinction matters. [OpenMP reduction semantics](https://www.openmp.org/spec-html/5.2/openmpsu52.html).

**F5 — SEVERITY: minor — contraction experts leave permitted output-checksum SIMD unused. Performance rank 4.**

Locations:

- `gram/expert.f90:129–136`
- `comoment3/expert.f90:112–127`
- `antisym/expert.f90:111–115`

The specs explicitly license these checksum folds. Their current scalar chains are unnecessary in the headline regime. The microkernels themselves need no reassociation: independent output-cell accumulators already supply parallelism.

```diff
--- a/bench/kernels/gram/expert.f90
+++ b/bench/kernels/gram/expert.f90
@@ -129,6 +129,9 @@
   !$omp parallel do private(jo, i) reduction(+:cdiag, coff)
   do j = 1, p
     jo = int(j, int64) * int(j - 1, int64) / 2
+#ifdef BENCH_LICENSED
+    !$omp simd reduction(+:coff)
+#endif
     do i = 1, j - 1
       coff = coff + gp(jo + i) * gp(jo + i)
     end do
--- a/bench/kernels/comoment3/expert.f90
+++ b/bench/kernels/comoment3/expert.f90
@@ -114,6 +114,9 @@
     do j = 1, k
       off = (k - 1) * k * (k + 1) / 6 + (j - 1) * j / 2
       w = 0.0_real64
+#ifdef BENCH_LICENSED
+      !$omp simd reduction(+:w)
+#endif
       do i = 1, j - 1
         w = w + tp(off + i) * tp(off + i)
       end do
--- a/bench/kernels/antisym/expert.f90
+++ b/bench/kernels/antisym/expert.f90
@@ -108,11 +108,16 @@
 
   ! ---- checksum over the full logical matrix: 2 x the strict triangle (zero diagonal)
   cs = 0.0_real64
+#if defined(BENCH_LICENSED) && defined(_OPENMP)
+  !$omp parallel do simd reduction(+:cs)
+#elif defined(BENCH_LICENSED)
+  !$omp simd reduction(+:cs)
+#else
   !$omp parallel do reduction(+:cs)
+#endif
   do jo = 1, ncell
     cs = cs + lp(jo) * lp(jo)
   end do
-  !$omp end parallel do
   cs = 2.0_real64 * cs
 
 #else
```

No output cell changes. Only licensed checksum association changes. Omitting the optional end directive in the last patch avoids mismatching a conditional `parallel do simd` with `end parallel do`.

The BLAS variant has the same omission:

```diff
--- a/bench/kernels/gram/expert_blas.f90
+++ b/bench/kernels/gram/expert_blas.f90
@@ -59,6 +59,9 @@
   !$omp parallel do private(i) reduction(+:cdiag, coff)
   do j = 1, p
+#ifdef BENCH_LICENSED
+    !$omp simd reduction(+:coff)
+#endif
     do i = 1, j - 1
       coff = coff + g(i, j) * g(i, j)
     end do
```

**F6 — SEVERITY: minor — `bench/kernels/gram/expert.f90:146–153`: the input checksum traverses the transposed storage at a large stride. Performance rank 5.**

The expert stores `x(p,n)`, but sums with fixed `i` and advancing `s`: a **12,808-byte stride** at full size. Unlike its packed kernel, this pass neither consumes adjacent values together nor exposes independent row accumulators explicitly.

Sum eight adjacent variables together. Each row still receives samples in exactly ascending order, and the final `rs` fold stays unchanged. This is legal in repro and licensed.

```diff
--- a/bench/kernels/gram/expert.f90
+++ b/bench/kernels/gram/expert.f90
@@ -43,7 +43,7 @@
 
   real(real64), allocatable :: x(:,:), ap(:,:,:), bp(:,:,:), gp(:), rs(:)
   real(real64) :: cs, ics, cdiag, coff
-  real(real64) :: c(mr, nr)
+  real(real64) :: c(mr, nr), sums(mr)
   integer :: i, s, j, ip, jp, ii, jj, i0, j0, nip, sb, m
   integer(int64) :: jo
 
@@ -143,12 +143,18 @@
 
   ! ---- input checksum: per-variable sums over s, then the sum over variables (rows are
   ! independent, the final sum is serial: the order is the same in every build)
-  !$omp parallel do private(s)
-  do i = 1, p
-    rs(i) = 0.0_real64
+  !$omp parallel do private(i0, m, s, ii, sums)
+  do ip = 1, npa
+    i0 = (ip - 1) * mr
+    m = min(mr, p - i0)
+    sums = 0.0_real64
     do s = 1, n
-      rs(i) = rs(i) + x(i, s)
+      !$omp simd
+      do ii = 1, m
+        sums(ii) = sums(ii) + x(i0 + ii, s)
+      end do
     end do
+    rs(i0 + 1:i0 + m) = sums(1:m)
   end do
   !$omp end parallel do
   ics = 0.0_real64
```

The benefit is better use of adjacent cache-line contents and two vector accumulators for eight independent row sums. It does not promise perfect cache-line alignment. The corresponding strided passes in `antisym/expert.f90:124–134` and `comoment3/expert.f90:134–141` deserve the same consideration; Gram is the largest single-input case.

**F7 — SEVERITY: major — `bench/kernels/fusedchain/expert.f90:10` and `bench/notes/fortran-notes.md:378`: documented input-generation fusion conflicts with the contract.**

Both locations say ifx fuses input generation, the kernel, and the input sum, while retaining input stores.

`bench/README.md:50–52` separately requires materialized inputs and prohibits fusing input generation into the consumer. Retaining stores satisfies the first condition, but not the second. Such fusion can eliminate input reloads and overlap generator work with kernel arithmetic, affecting both the total and total-minus-twin comparison.

This needs a production-artifact disposition before publication:

- If that fusion remains in the actual benchmark executable, enforce the materialization boundary uniformly or explicitly revise the protocol to permit compiler-generated fusion for every language.
- If the observation is obsolete, replace it with evidence for the frozen executable.
- A surviving store alone is insufficient evidence that the prohibited fusion did not happen.

I did not independently disassemble the current ifx binary, so this is a documented contract conflict, not a newly verified assembly finding.

**F8 — SEVERITY: minor — `bench/kernels/dot/natural.f90:32–34`: its mt8 baseline knowingly uses a workshare that does no kernel worksharing.**

The source and notes acknowledge that `DOT_PRODUCT` executes on one thread. This is valid and disclosed, but inconsistent with the treatment of `SUM` in `onepass` and `fusedchain`, where explicit reduction loops were introduced precisely to avoid that serialization.

`dot/spec.md:37–38` already allows the explicit loop. Preserve the natural intrinsic in serial and give mt8 the ordinary threaded transcription:

```diff
--- a/bench/kernels/dot/natural.f90
+++ b/bench/kernels/dot/natural.f90
@@ -29,9 +29,16 @@
   !$omp end parallel do
 
 #ifndef BENCH_INPUTONLY
-  !$omp parallel workshare
+#ifdef _OPENMP
+  cs = 0.0_real64
+  !$omp parallel do reduction(+:cs)
+  do k = 1, n
+    cs = cs + a(k) * b(k)
+  end do
+  !$omp end parallel do
+#else
   cs = dot_product(a, b)
-  !$omp end parallel workshare
+#endif
 #else
   cs = 0.0_real64
 #endif
```

If the intrinsic-only mt8 arm is intentionally retained instead, its label must remain “parallel preparation, serial kernel”; it is not evidence about the capability of threaded natural Fortran.

**F9 — SEVERITY: note — per-kernel performance, natural-tier, and directive verdicts.**

Paths below are relative to `bench/kernels/<kernel>/`. These verdicts incorporate F1–F8.

| Kernel | Expert performance and fairness | Natural tier | Directive audit |
|---|---|---|---|
| **gram** | `expert.f90:93–124,179–195`: credible blocked SYRK implementation; canonical output, ascending sample chains, padded zeros harmless for these finite nonnegative inputs. Improve checksums via F5/F6. | `natural.f90:36`: `matmul(transpose(xs),xs)` is the right dense intrinsic baseline. A compiler-generated transpose is not an artificially inserted temporary. | Fill/packing private lists are complete. Dynamic column panels own disjoint packed columns. `sb` is shared and stable during each parallel region. Private `c` prevents races. Inner `simd` updates different cells, not one reduction. |
| **comoment3** | `expert.f90:70–108`: good simplex/slab approach, but F1 blocks arithmetic acceptance. Division at line 101 is correct. Multiplicities at 121–123 are correct. | `natural.f90:34–43`: competent literal dense transcription, contiguous sample sections and contiguous output stores. No deliberate stride penalty. | `bp` is an unallocated allocatable on entry to the parallel region and privately allocated per thread. Slabs indexed by distinct `j` write disjoint cells. `rn`, `x`, and `ap` are shared read-only. Checksum private lists are complete. |
| **antisym** | `expert.f90:88–116`: correct strict triangle, two complete sums followed by one subtraction, correct weight 2. Keeping the two 8×6 products separate avoids needing 24 vector accumulator registers simultaneously. F5 applies. | `natural.f90:38`: two contiguous `DOT_PRODUCT`s per cell are plausible first code. The permitted two-MATMUL alternative is also natural and likely worth comparison; this tier is not “best dense Fortran.” | Private `c1/c2`, offsets and temporaries are correct. Column panels write disjoint packed cells. Neither product is incorrectly replaced by a reduction of per-sample differences. |
| **pairkernel** | `expert.f90:48–69`: good SoA, hoisted coordinates, branch-free strict column, explicit diagonal stores. Formula and divisions match Blade. No justified replacement of scalar `exp` under current permissions. | `natural.f90:33–44`: fair dense independent-cell baseline; contiguous inner access and stores. | Dynamic columns are disjoint; all scalar work variables and `w` are private. Guarded `simd reduction(+:w)` is on the licensed checksum only. |
| **fusedchain** | `expert.f90:41–53`: appropriate fused scalar-stage loop and licensed reduction. Division is retained. F3 and the input-boundary conflict F7 apply. | `natural.f90:35–55`: fair **staged baseline**, not a claim about the only competent spelling. Separate stage arrays are intentional protocol work. | Combined/standalone SIMD split is appropriate for the documented ifx behavior. `u1/u2/u3` are private; `cs` reduced. No explicit end directive is required. |
| **onepass** | `expert.f90:49–58`: good one-pass implementation with four independent sums and correctly ordered powers. F3 applies. | `natural.f90:35–59`: legitimate four-pass baseline. Explicit products avoid the rounding change from a squared-square implementation of `x**4`. | Four reduction variables and scalar temporaries are correctly scoped. Current spec lines 12–18 and emission evidence authorize licensed SIMD despite the stale “no licence” comment at expert line 13. |
| **stencil** | `expert.f90:44–59`: sound contiguous producer followed by a hot-row checksum; all five terms match Blade’s order. F3 applies. No cache tiling change is clearly justified for this single sweep. | `natural.f90:41–53`: row array sections are natural and contiguous. Splitting by row to obtain actual worksharing is reasonable. | Rows own disjoint output slices; `j/w` private, `cs` reduced. Inner checksum SIMD matches the recorded per-row `prodsum` licence, not merely the outer row-fold licence. |
| **recur** | `expert.f90:68–99`: sound serial fused RK4 implementation; all trajectory cells stored. F4 is the main mt8 opportunity. | `natural.f90:35–66`: reasonable staged RK4 baseline with reusable stage arrays. Hoisting those allocations is ordinary Fortran, not unfair tuning. | Current private `t` makes every thread run the same sequence of worksharing constructs. Implicit barriers make stage dependencies safe. Repeated reductions on shared `cs` are correct but expensive. |
| **maskagg** | `expert.f90:38–46`: good stable branch-free compaction; retained prefix is genuinely materialized. F2 corrects an unfair licence restriction. | `natural.f90:27–29`: `PACK`, `SIZE`, `SUM` are exactly the expected natural spelling. A masked sum would violate the named-output contract. | Current source has no directives or races. Proposed SIMD touches only independent checksum folds, never the running compaction counter. |
| **dot** | `expert.f90:36–45`: correct repro chain and licensed reduction. F3 matters because input work dominates the region. | `natural.f90:33`: right serial intrinsic; mt8 concern is F8. | Reduction variable is initialized and correctly scoped. `_OPENMP` split selects combined threading/SIMD versus standalone SIMD. |
| **saxpy** | `expert.f90:36–47`: good fused store/checksum loop; no checksum-only shortcut in source. F3 applies. | `natural.f90:31–39`: proper array assignment followed by a checksum; workshare is useful here. | `v` private, `cs` reduced, disjoint `y2(k)` stores. Licensed reduction placement is matched. Repro retains the sequential checksum chain. |

No source-level data race or missing `firstprivate` requirement was found. Variables that need initial values are either shared read-only, initialized inside their private scope, or initialized by reduction semantics. Private scratch values are assigned before use.

**F10 — SEVERITY: note — `bench/kernels/gram/expert.f90:35–40` and the other contraction microkernels: retain the present tile sizes pending measurements.**

The chosen 8×6 microtile is technically defensible on Zen 3:

- Forty-eight doubles require twelve YMM accumulator registers, leaving four architectural vector registers for operands and temporaries.
- Gram’s full-size `kc` is **251**, not 256. Its two active panels occupy `14 × 251 × 8 = 28,112` bytes, plus the small accumulator tile: a sensible L1 target, though with limited headroom.
- Comoment’s corresponding panel pair occupies **22,512 bytes** at `n=201`.
- Antisym’s pair occupies **44,912 bytes** at `n=401`: it is L2-resident, not wholly L1-resident. Each sample is consumed sequentially, so exceeding L1 does not by itself justify sample blocking.

I would not prescribe 8×8, 16×4, or another power-of-two tile as automatically faster: those can exhaust the sixteen-register AVX2 register file. Nor would I split accumulators in repro.

The explicit-shape tile arguments and contiguous element-sequence association already provide useful layout information. Adding `CONTIGUOUS` indiscriminately to these explicit-shape dummies is not the fix. `ASSOCIATE` alone supplies no new no-alias guarantee.

Packed-offset divisions occur outside the sample microkernels; there is no inner-loop integer-division defect to fix there.

**F11 — SEVERITY: minor — build flags and routing: one explicit exclusion is missing; no broad performance flag change is presently justified.**

`bench/README.md:146` specifies both `-fno-external-blas` and `-fno-external-blas64`. `build_gfortran.ps1:71` and the harness flag lists contain only the former. This is not evidence of accidental BLAS linkage, but the implementation does not match the stated explicit exclusion.

```diff
--- a/bench/fortran/build_gfortran.ps1
+++ b/bench/fortran/build_gfortran.ps1
@@ -68,7 +68,7 @@
 #  * -fno-external-blas: MATMUL never routes to an external BLAS (it is gfortran's default;
 #    explicit per review M22). MATMUL stays libgfortran's _gfortran_matmul_r8 above the
 #    default -finline-matmul-limit (30).
-$all = @($flags) + @('-cpp', '-fno-external-blas', "-I$PSScriptRoot", '-Wl,--stack,268435456') + @($srcs) + @('-o', $out)
+$all = @($flags) + @('-cpp', '-fno-external-blas', '-fno-external-blas64', "-I$PSScriptRoot", '-Wl,--stack,268435456') + @($srcs) + @('-o', $out)
 [Console]::Error.WriteLine('+ ' + $gf + ' ' + ($all -join ' '))
 & $gf @all
 exit $LASTEXITCODE
```

Other flag decisions are reasonable:

- Keep `-finline-matmul-limit` at its recorded default unless a **suite-wide** experiment establishes a better choice. GNU documents the default as 30 and treats external 32-bit and 64-bit BLAS routing separately. [GNU Fortran code-generation options](https://gcc.gnu.org/onlinedocs/gfortran/Code-Gen-Options.html).
- Keep ifx `/fp:precise` explicit. Intel documents `/fp:fast` as the default. The repository’s actual-version FMA/reassociation probes are stronger evidence for the additional backend contraction option than assumptions based on flag names. Repeat those probes when changing compiler versions. [Intel floating-point model documentation](https://www.intel.com/content/www/us/en/docs/fortran-compiler/developer-guide-reference/2025-0/fp-model-fp.html).
- Do not add `/Qparallel` to a serial regime. Do not treat `/Qopt-matmul` as guaranteed to enable a particular route in ifx 2026.1; retain the observed-artifact routing audit.
- SIMD-only OpenMP flags are appropriate. Replacing them with full OpenMP in serial would change more than vectorization.
- The 256 MB main-thread stack reserve is a defensible uniform crash-prevention setting. It is virtual reserve, not 256 MB of physical working set. It does not enlarge worker stacks.
- The current large explicit arrays and the comoment worker panels are allocatable; small microtiles do not warrant a larger worker stack. There is no demonstrated reason to add `/heap-arrays`, `-fstack-arrays`, or reallocation-assumption changes globally.
- Including `bench_support.f90` keeps generator bodies visible without introducing LTO/IPO asymmetry. Its integer masking and shifts are valid for the nonnegative bounded generator intermediates.

**F12 — SEVERITY: note — `bench/kernels/gram/expert_blas.f90:31–38,55–56`: DSYRK routine, orientation, and leading dimensions are correct.**

For `xs(n,p)`, the desired product is `xsᵀ xs`. Therefore:

```text
UPLO='U', TRANS='T', N=p, K=n, LDA=n, LDC=p,
ALPHA=1, BETA=0
```

is correct. Only the upper triangle is subsequently read; the uninitialized lower half is harmless. The selected routine avoids computing both triangles. [DSYRK specification](https://www.netlib.org/lapack/explore-html/dc/d05/dsyrk_8f_source.html).

The interface passes scalar arguments by address, matching the documented OpenBLAS entry. Its character ABI is intentionally specific to that pinned OpenBLAS implementation; it should not be described as a universally portable interface to every Fortran BLAS. The documented LP64 integer check and DLL identity audit are appropriate.

**F13 — SEVERITY: minor — timing endpoints mostly comply, but the claim that every Fortran deallocation is after the stop line is false.**

Every inspected program calls `bench_start` before its explicit allocations and input generation, and calls `bench_stop_and_report` after both checksums. Dumps and final main-program deallocations are after the stop call.

The exception is **`comoment3/expert.f90:107`**: every thread deallocates its private `bp` panel before the checksum and before the stop at line 147. This contradicts `fortran-notes.md:457`’s blanket claim.

Either retain worker workspaces until after timing or explicitly record this small in-region destruction cost. Compiler-created expression temporaries also have their own lifetimes; the after-stop statement should describe retained program arrays, not all temporary storage.

The timer itself is suitable: `bench_support.f90:145` captures the stopping counter before the frequency query, formatting, and printing. Those reporting operations are excluded.

No source-authored checksum-only shortcut was found. All named outputs are assigned. However, `fortran-notes.md:453–456` overstates “structural guarantee”: source stores and later deallocation do not prevent an optimizer from eliminating an unobservable array. Keep the machine-code store audit for each frozen build.

**F14 — SEVERITY: minor — several documentary claims are stale enough to mislead the review.**

- **Serial is not directive-free.** `README.md:109–114` intentionally enables standalone SIMD; expert contraction tiles retain it in repro. The accurate claim is “no active thread-level OpenMP.” Natural sources also contain threading directives, although the serial build ignores them.
- `onepass/expert.f90:13` and `fortran-notes.md:503–504` still question licences resolved by the current specs and emission evidence. Lack of an explicit threading `where omp` does not disprove the builtin-fold reassociation licence.
- `gram/spec.md:13–14` says licensed does not touch the dot; its own lines 105–118 correctly say the opposite.
- `comoment3/spec.md:40` should list **848,046** packed cells.
- `bench_support.f90:14` and `fortran-notes.md:124` incorrectly suggest inclusion suppresses `.mod` generation. Existing build directories contain `bench_support.mod`. Inclusion enables visibility/inlining; build-directory isolation handles module-file collisions.
- `README.md:188–190` specifies bound mt8 execution, while `harness/config.json:36–41` selects unbound execution with `KMP_AFFINITY=disabled`. The configuration implements the later jitter recommendation; update the protocol and report the actual placement condition.
- `harness/config.json:8` selects a **licensed** compute control, whereas `README.md:422` and `recur/spec.md:114–115` describe **repro**. Either choice can serve as a fixed control, but its identity must be consistent.

The remaining kernels are acceptable as reviewed source implementations subject to their listed findings. The performance patches need smoke validation, renewed store/routing audits, and interleaved measurements before replacing the frozen expert arms.

[exited with code 0]
