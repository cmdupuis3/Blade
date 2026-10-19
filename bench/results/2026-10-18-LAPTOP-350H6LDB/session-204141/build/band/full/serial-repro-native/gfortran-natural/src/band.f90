! band / natural -- s = x^T B x for the banded operator B(i,j) = phi(i,j) on |i - j| <= w,
! phi(i,j) = (2 + d/(w+1)) / (1 + d^2), d = i - j; checksum = s; input_checksum = sum x.
! Spec: bench/kernels/band/spec.md.
! Natural tier (dense-predicate baseline, a straw man by construction -- spec): the full
! n x n nest with the band predicate around the cell (the dense B would be 3.2 GB, so the
! predicate nest IS the dense transcription), s accumulated in the nest in the band's
! lexicographic order (i, then j).  mt8: `!$omp parallel do reduction(+:s)` on the outer i
! loop; the input fill is `parallel do`.
! Builds: -DBENCH_INPUTONLY = inputonly twin (kernel skipped, checksum = 0).  Scalar output:
! no dump.  Deallocation after the stop line.
include 'bench_support.f90'

program band_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 20011   ! SIZE n
  integer, parameter :: w = 101     ! SIZE w
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:)
  real(real64) :: s, ics, d
  integer :: i, j

  call bench_start()
  allocate(x(n))
  !$omp parallel do
  do i = 1, n
    x(i) = lcg(i - 1, seed_x, lo, hi)
  end do
  !$omp end parallel do

  s = 0.0_real64
#ifndef BENCH_INPUTONLY
  !$omp parallel do private(j, d) reduction(+:s)
  do i = 1, n
    do j = 1, n
      if (abs(i - j) <= w) then
        d = real(i - j, real64)
        s = s + (2.0_real64 + d / real(w + 1, real64)) / (1.0_real64 + d * d) * x(i) * x(j)
      end if
    end do
  end do
  !$omp end parallel do
#endif

  ics = 0.0_real64
  do i = 1, n
    ics = ics + x(i)
  end do
  call bench_stop_and_report('band')
  call bench_print('checksum', s)
  call bench_print('input_checksum', ics)
  deallocate(x)
end program band_natural
