! fusedchain / natural -- s1 = 1.5 a + 0.25; s2 = s1 / (1 + |s1|); s3 = s2^2 - 0.5 s2;
! checksum = sum s3^2; input_checksum = sum a.  Spec: bench/kernels/fusedchain/spec.md.
! Natural tier: three named allocatable stage arrays, one pass each, then the sum (four
! passes); mt8: `!$omp parallel do` per pass, reduction(+:) on the sum (README section 2);
! the fill is `parallel do`, the input sum serial (canonical order).
! Builds: -DBENCH_INPUTONLY = inputonly twin (stages and sum skipped, checksum = 0).  Scalar
! output: no dump.  Deallocation after the stop line.
include 'bench_support.f90'

program fusedchain_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 20011   ! SIZE n
  integer, parameter :: seed_a = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: a(:), s1(:), s2(:), s3(:)
  real(real64) :: cs, ics
  integer :: k

  call bench_start()
  allocate(a(n), s1(n), s2(n), s3(n))
  !$omp parallel do
  do k = 1, n
    a(k) = lcg(k - 1, seed_a, lo, hi)
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  ! one pass per stage into its named array, then the sum: the array statements
  !   s1 = 1.5*a + 0.25;  s2 = s1/(1 + abs(s1));  s3 = s2*s2 - 0.5*s2;  cs = sum(s3*s3)
  ! written as one loop each so that mt8 threads every pass (a PARALLEL WORKSHARE around
  ! the array statements runs the SUM on one thread in both compilers, and ifx also
  ! serializes the s2 statement: warning #5474; see fortran-notes.md)
  !$omp parallel do
  do k = 1, n
    s1(k) = 1.5_real64 * a(k) + 0.25_real64
  end do
  !$omp end parallel do
  !$omp parallel do
  do k = 1, n
    s2(k) = s1(k) / (1.0_real64 + abs(s1(k)))
  end do
  !$omp end parallel do
  !$omp parallel do
  do k = 1, n
    s3(k) = s2(k) * s2(k) - 0.5_real64 * s2(k)
  end do
  !$omp end parallel do
  cs = 0.0_real64
  !$omp parallel do reduction(+:cs)
  do k = 1, n
    cs = cs + s3(k) * s3(k)
  end do
  !$omp end parallel do
#else
  cs = 0.0_real64
#endif

  ! input checksum: sequential, ascending k (the order of the Blade source's reduce)
  ics = 0.0_real64
  do k = 1, n
    ics = ics + a(k)
  end do
  call bench_stop_and_report('fusedchain')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
  deallocate(a, s1, s2, s3)
end program fusedchain_natural
