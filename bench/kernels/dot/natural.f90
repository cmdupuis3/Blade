! dot / natural -- checksum = sum_k a(k) b(k); input_checksum = sum a + sum b.
! Spec: bench/kernels/dot/spec.md.
! Natural tier: the DOT_PRODUCT intrinsic (serial builds).  mt8 (review F8): the ordinary
! threaded transcription, an explicit `parallel do reduction(+:cs)` loop -- consistent with
! onepass/fusedchain natural (a DOT_PRODUCT inside PARALLEL WORKSHARE runs on ONE thread in
! both compilers: gfortran GOMP_single, ifx warning #5474).  The fill is `parallel do`.
! Builds: -DBENCH_INPUTONLY = inputonly twin.  Scalar output: no dump.  Deallocation after
! the stop line.
include 'bench_support.f90'

program dot_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 3000017   ! SIZE n
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

#ifndef BENCH_INPUTONLY
#ifdef _OPENMP
  cs = 0.0_real64
  !$omp parallel do reduction(+:cs)
  do k = 1, n
    cs = cs + a(k) * b(k)
  end do
  !$omp end parallel do
#else
  cs = dot_product(a, b)
#endif
#else
  cs = 0.0_real64
#endif

  ! input checksum in the order of the Blade source: (sum a) + (sum b), each ascending
  sa = 0.0_real64
  sb = 0.0_real64
  do k = 1, n
    sa = sa + a(k)
    sb = sb + b(k)
  end do
  ics = sa + sb
  call bench_stop_and_report('dot')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
  deallocate(a, b)
end program dot_natural
