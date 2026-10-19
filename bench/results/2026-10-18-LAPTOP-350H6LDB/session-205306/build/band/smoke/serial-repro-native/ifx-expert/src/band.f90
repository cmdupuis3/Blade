! band / expert -- s = x^T B x for the banded operator B(i,j) = phi(i,j) on |i - j| <= w,
! phi(i,j) = (2 + d/(w+1)) / (1 + d^2), d = i - j; checksum = s; input_checksum = sum x.
! Spec: bench/kernels/band/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "band"):
!  * only the band's cells are visited, with the hand-written clipped bounds
!    `do j = max(1, i - w), min(n, i + w)`; per cell exactly the spec's arithmetic,
!    phi(i,j) * x(i) * x(j) (left to right), phi evaluated per cell (no table of phi(d):
!    the spec makes the per-cell division part of the kernel);
!  * repro: the band's lexicographic order (i ascending, j ascending), ONE accumulator;
!  * licensed (-DBENCH_LICENSED): the spec allows the diagonal ordering -- `do d = -w, w;
!    do i` over the valid range: the inner loop is unit-stride in x(i) and x(i - d), phi(d)
!    is invariant in it (the compilers hoist the division out of the inner loop), and the
!    inner sum carries `!$omp simd reduction(+:...)` (the Blade fold over the domain is a
!    licensed builtin (+)); the input-checksum fold is licensed too;
!  * mt8: `!$omp parallel do reduction(+:s)` on the outer loop (rows in repro, diagonals in
!    licensed); the fill is `parallel do`.
! Builds: -DBENCH_INPUTONLY = inputonly twin (kernel skipped, checksum = 0).  Scalar output:
! no dump.  Deallocation after the stop line.
include 'bench_support.f90'

program band_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 211   ! SIZE n
  integer, parameter :: w = 5     ! SIZE w
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:)
  real(real64) :: s, ics, d
  integer :: i, j
#ifdef BENCH_LICENSED
  real(real64) :: phi, sd
  integer :: k
#endif

  call bench_start()
  allocate(x(n))
  !$omp parallel do
  do i = 1, n
    x(i) = lcg(i - 1, seed_x, lo, hi)
  end do
  !$omp end parallel do

  s = 0.0_real64
#ifndef BENCH_INPUTONLY
#ifndef BENCH_LICENSED
  ! repro: lexicographic (i, j) order, one accumulator
  !$omp parallel do private(j, d) reduction(+:s)
  do i = 1, n
    do j = max(1, i - w), min(n, i + w)
      d = real(i - j, real64)
      s = s + (2.0_real64 + d / real(w + 1, real64)) / (1.0_real64 + d * d) * x(i) * x(j)
    end do
  end do
  !$omp end parallel do
#else
  ! licensed: diagonal order (k = i - j), the cells and their arithmetic unchanged
  !$omp parallel do schedule(static) private(i, d, phi, sd) reduction(+:s)
  do k = -w, w
    d = real(k, real64)
    phi = (2.0_real64 + d / real(w + 1, real64)) / (1.0_real64 + d * d)
    sd = 0.0_real64
    !$omp simd reduction(+:sd)
    do i = max(1, 1 + k), min(n, n + k)
      sd = sd + phi * x(i) * x(i - k)
    end do
    s = s + sd
  end do
  !$omp end parallel do
#endif
#endif

  ics = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:ics)
#endif
  do i = 1, n
    ics = ics + x(i)
  end do
  call bench_stop_and_report('band')
  call bench_print('checksum', s)
  call bench_print('input_checksum', ics)
  deallocate(x)
end program band_expert
