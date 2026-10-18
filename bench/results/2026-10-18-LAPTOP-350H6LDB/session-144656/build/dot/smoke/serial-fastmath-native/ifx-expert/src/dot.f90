! dot / expert -- checksum = sum_k a(k) b(k); input_checksum = sum a + sum b.
! Spec: bench/kernels/dot/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "dot"):
!  * one loop, one accumulator, ascending k (repro: one FMA chain, latency-bound);
!  * mt8: parallel do reduction(+:s) (the kernel loop); the fill is `parallel do`;
!  * licensed regime (-DBENCH_LICENSED): `parallel do simd reduction(+:s)` (serial builds
!    honour its simd part) -- the Blade source licenses this fold
!    (`lambda(p, q) where omp -> p + q`).
! Builds: -DBENCH_INPUTONLY = inputonly twin.  Scalar output: no dump.  Deallocation after
! the stop line.
include 'bench_support.f90'

program dot_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 30011   ! SIZE n
  integer, parameter :: seed_a = 10000019, seed_b = 20000038
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: a(:), b(:)
  real(real64) :: cs, ics, sa, sb
  integer :: k

  call bench_start()
  allocate(a(n), b(n))
  !$omp parallel do
  do k = 1, n
    a(k) = lcg(k - 1, seed_a, lo, hi)
    b(k) = lcg(k - 1, seed_b, lo, hi)
  end do
  !$omp end parallel do

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
#if defined(BENCH_LICENSED) && defined(_OPENMP)
  !$omp parallel do simd reduction(+:cs)
#elif defined(BENCH_LICENSED)
  !$omp simd reduction(+:cs)
#else
  !$omp parallel do reduction(+:cs)
#endif
  do k = 1, n
    cs = cs + a(k) * b(k)
  end do
#endif

  ! input checksum: (sum a) + (sum b), each ascending in repro; matched SIMD reductions
  ! in licensed (review F3)
  sa = 0.0_real64
  sb = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:sa, sb)
#endif
  do k = 1, n
    sa = sa + a(k)
    sb = sb + b(k)
  end do
  ics = sa + sb
  call bench_stop_and_report('dot')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
  deallocate(a, b)
end program dot_expert
