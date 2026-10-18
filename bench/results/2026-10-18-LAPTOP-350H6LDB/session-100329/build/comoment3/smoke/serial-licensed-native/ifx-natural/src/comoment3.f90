! comoment3 / natural -- T(i,j,k) = (1/n) sum_s X(i,s) X(j,s) X(k,s) for X (p x n);
! checksum = sum of T^2 over the full p^3 cube; input_checksum = sum of X.
! Spec: bench/kernels/comoment3/spec.md.
! Natural tier: x(n, p) (samples contiguous), the dense p x p x p cube filled in column-major
! order with one SUM of the triple product per cell (sample loop inside), SUM for the checksums.
! mt8: `!$omp parallel do` (default static schedule) on the outer k loop; the fill too.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = T row-major to
! comoment3_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program comoment3_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: p = 13   ! SIZE p
  integer, parameter :: n = 11   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:,:), t(:,:,:)
  real(real64) :: cs, ics
  integer :: i, j, k, s

  call bench_start()
  allocate(x(n, p), t(p, p, p))
  !$omp parallel do private(s)
  do i = 1, p
    do s = 1, n
      x(s, i) = lcg(lin2(i - 1, s - 1, n), seed_x, lo, hi)
    end do
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  !$omp parallel do private(j, i)
  do k = 1, p
    do j = 1, p
      do i = 1, p
        t(i, j, k) = sum(x(:, i) * x(:, j) * x(:, k)) / real(n, real64)
      end do
    end do
  end do
  !$omp end parallel do
  cs = sum(t * t)
#else
  cs = 0.0_real64
#endif
  ics = sum(sum(x, dim=1))
  call bench_stop_and_report('comoment3')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('comoment3')
  do i = 1, p
    do j = 1, p
      do k = 1, p
        call bench_dump(t(i, j, k))
      end do
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(x, t)
end program comoment3_natural
