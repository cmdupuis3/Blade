! onepass / expert -- S1..S4 = sum x, x^2, x^3, x^4 (powers by repeated multiplication);
! checksum = S1 + 2 S2 + 3 S3 + 4 S4; input_checksum = sum x.
! Spec: bench/kernels/onepass/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "onepass"):
!  * one traversal, four accumulators, powers by repeated multiplication in the spec's order
!    (v2 = v*v, v3 = v2*v, v4 = v3*v); strict: ONE accumulator per sum, ascending k (four
!    independent add chains run in parallel in the core);
!  * mt8: parallel do reduction(+:s1, s2, s3, s4) on the one loop; the fill is `parallel do`,
!    the input sum serial (canonical order);
!  * licensed regime (-DBENCH_LICENSED): `parallel do simd reduction(+:s1, s2, s3, s4)` /
!    `simd reduction(...)` -- the builtin (+) fold of the <&!> join is reassociation-licensed
!    in the Blade source (spec, emission evidence); the input sum is licensed too (F3).
! Builds: -DBENCH_INPUTONLY = inputonly twin.  Scalar output: no dump.  Deallocation after
! the stop line.
include 'bench_support.f90'

program onepass_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 5000011   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 2.0_real64
  real(real64), allocatable :: x(:)
  real(real64) :: s1, s2, s3, s4, cs, ics, v, v2, v3
  integer :: k

  call bench_start()
  allocate(x(n))
  !$omp parallel do
  do k = 1, n
    x(k) = lcg(k - 1, seed_x, lo, hi)
  end do
  !$omp end parallel do

  s1 = 0.0_real64
  s2 = 0.0_real64
  s3 = 0.0_real64
  s4 = 0.0_real64
#ifndef BENCH_INPUTONLY
#if defined(BENCH_LICENSED) && defined(_OPENMP)
  !$omp parallel do simd private(v, v2, v3) reduction(+:s1, s2, s3, s4)
#elif defined(BENCH_LICENSED)
  !$omp simd private(v, v2, v3) reduction(+:s1, s2, s3, s4)
#else
  !$omp parallel do private(v, v2, v3) reduction(+:s1, s2, s3, s4)
#endif
  do k = 1, n
    v = x(k)
    v2 = v * v
    v3 = v2 * v
    s1 = s1 + v
    s2 = s2 + v2
    s3 = s3 + v3
    s4 = s4 + v3 * v
  end do
  cs = s1 + 2.0_real64 * s2 + 3.0_real64 * s3 + 4.0_real64 * s4
#else
  cs = 0.0_real64
#endif

  ! input checksum: ascending k in repro; matched SIMD reduction in licensed (review F3)
  ics = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:ics)
#endif
  do k = 1, n
    ics = ics + x(k)
  end do
  call bench_stop_and_report('onepass')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
  deallocate(x)
end program onepass_expert
