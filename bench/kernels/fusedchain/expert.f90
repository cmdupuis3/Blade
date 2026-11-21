! fusedchain / expert -- s1 = 1.5 a + 0.25; s2 = s1 / (1 + |s1|); s3 = s2^2 - 0.5 s2;
! checksum = sum s3^2; input_checksum = sum a.  Spec: bench/kernels/fusedchain/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "fusedchain"):
!  * one pass, no stage arrays: the three stages and the square are fused per element into
!    ONE accumulator, ascending k (strict: the add chain is the bound, ~3-4 cycles/element;
!    the divisions overlap with it).  A strip-mined variant (vectorized stage block into an
!    L1 buffer, then the sequential sum) was measured SLOWER with both compilers and dropped;
!    so was an explicit `cs + (u3*u3)` that blocks the FMA contraction (no difference);
!  * ifx fuses this loop with the fill and input-sum loops (the input array is still stored);
!  * mt8: parallel do reduction(+:cs) on the fused loop; the fill is `parallel do`;
!  * licensed regime (-DBENCH_LICENSED): the fused loop becomes `parallel do simd
!    reduction(+:cs)` (serial builds honour only its simd part) -- the Blade source licenses
!    this fold (`lambda(p, q) where omp -> p + q`).  repro regime: sequential fold.
! Builds: -DBENCH_INPUTONLY = inputonly twin.  Scalar output: no dump.  Deallocation after
! the stop line.
include 'bench_support.f90'

program fusedchain_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 2000003   ! SIZE n
  integer, parameter :: seed_a = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  integer, parameter :: nb = 512
  real(real64), allocatable :: a(:)
  real(real64) :: cs, ics, t(nb), u1, u2, u3
  integer :: k, kb, m

  call bench_start()
  allocate(a(n))
  !$omp parallel do
  do k = 1, n
    a(k) = lcg(k - 1, seed_a, lo, hi)
  end do
  !$omp end parallel do

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
#if defined(BENCH_LICENSED) && defined(_OPENMP)
  !$omp parallel do simd private(u1, u2, u3) reduction(+:cs)
#elif defined(BENCH_LICENSED)
  !$omp simd private(u1, u2, u3) reduction(+:cs)
#else
  !$omp parallel do private(u1, u2, u3) reduction(+:cs)
#endif
  do k = 1, n
    u1 = 1.5_real64 * a(k) + 0.25_real64
    u2 = u1 / (1.0_real64 + abs(u1))
    u3 = u2 * u2 - 0.5_real64 * u2
    cs = cs + u3 * u3
  end do
#else
  cs = 0.0_real64
#endif

  ! input checksum: ascending k in repro; matched SIMD reduction in licensed (review F3)
  ics = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:ics)
#endif
  do k = 1, n
    ics = ics + a(k)
  end do
  call bench_stop_and_report('fusedchain')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
  deallocate(a)
end program fusedchain_expert
