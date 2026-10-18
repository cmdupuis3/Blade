! onepass / natural -- S1..S4 = sum x, x^2, x^3, x^4 (powers by repeated multiplication);
! checksum = S1 + 2 S2 + 3 S3 + 4 S4; input_checksum = sum x.
! Spec: bench/kernels/onepass/spec.md.
! Natural tier: four passes over x, one per power sum.  The powers are written as explicit
! left-to-right products ((x*x)*x)*x, the spec's operation order (x**4 is expanded as
! (x*x)**2, which rounds differently).  mt8: `parallel do reduction` per pass; the fill is
! `parallel do`, the input sum serial (canonical order).
! Builds: -DBENCH_INPUTONLY = inputonly twin.  Scalar output: no dump.  Deallocation after
! the stop line.
include 'bench_support.f90'

program onepass_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 5000011   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 2.0_real64
  real(real64), allocatable :: x(:)
  real(real64) :: s1, s2, s3, s4, cs, ics
  integer :: k

  call bench_start()
  allocate(x(n))
  !$omp parallel do
  do k = 1, n
    x(k) = lcg(k - 1, seed_x, lo, hi)
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  ! the four passes  s1 = sum(x); s2 = sum(x*x); s3 = sum((x*x)*x); s4 = sum(((x*x)*x)*x)
  ! as one reduction loop each, so that mt8 threads every pass (inside PARALLEL WORKSHARE
  ! both compilers run SUM on a single thread; see fortran-notes.md)
  s1 = 0.0_real64
  !$omp parallel do reduction(+:s1)
  do k = 1, n
    s1 = s1 + x(k)
  end do
  !$omp end parallel do
  s2 = 0.0_real64
  !$omp parallel do reduction(+:s2)
  do k = 1, n
    s2 = s2 + x(k) * x(k)
  end do
  !$omp end parallel do
  s3 = 0.0_real64
  !$omp parallel do reduction(+:s3)
  do k = 1, n
    s3 = s3 + (x(k) * x(k)) * x(k)
  end do
  !$omp end parallel do
  s4 = 0.0_real64
  !$omp parallel do reduction(+:s4)
  do k = 1, n
    s4 = s4 + ((x(k) * x(k)) * x(k)) * x(k)
  end do
  !$omp end parallel do
  cs = s1 + 2.0_real64 * s2 + 3.0_real64 * s3 + 4.0_real64 * s4
#else
  cs = 0.0_real64
#endif

  ! input checksum: sequential, ascending k (the order of the Blade source's reduce)
  ics = 0.0_real64
  do k = 1, n
    ics = ics + x(k)
  end do
  call bench_stop_and_report('onepass')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
  deallocate(x)
end program onepass_natural
