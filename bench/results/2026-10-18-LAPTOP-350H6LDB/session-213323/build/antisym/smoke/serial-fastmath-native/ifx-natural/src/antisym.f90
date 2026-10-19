! antisym / natural -- L(i,j) = sum_s X(i,s) Y(j,s) - sum_s X(j,s) Y(i,s) for X, Y (p x n);
! checksum = sum of L^2 over the full p x p matrix; input_checksum = sum X + sum Y.
! Spec: bench/kernels/antisym/spec.md.
! Natural tier: x(n, p), y(n, p) (components contiguous), the full dense p x p nest in
! column-major order, each cell its two DOT_PRODUCTs, SUM for the checksum.
! mt8: `!$omp parallel do` (default static schedule) on the outer j loop; the fill too.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = L row-major to antisym_dump.txt
! after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program antisym_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: p = 23   ! SIZE p
  integer, parameter :: n = 11    ! SIZE n
  integer, parameter :: seed_x = 10000019, seed_y = 20000038
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:,:), y(:,:), l(:,:)
  real(real64) :: cs, ics
  integer :: i, j, s

  call bench_start()
  allocate(x(n, p), y(n, p), l(p, p))
  !$omp parallel do private(s)
  do i = 1, p
    do s = 1, n
      x(s, i) = lcg(lin2(i - 1, s - 1, n), seed_x, lo, hi)
      y(s, i) = lcg(lin2(i - 1, s - 1, n), seed_y, lo, hi)
    end do
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  !$omp parallel do private(i)
  do j = 1, p
    do i = 1, p
      l(i, j) = dot_product(x(:, i), y(:, j)) - dot_product(x(:, j), y(:, i))
    end do
  end do
  !$omp end parallel do
  cs = sum(l * l)
#else
  cs = 0.0_real64
#endif
  ! input checksum in the order of the Blade source: per particle (sum_s X + sum_s Y), then
  ! the sum over particles
  ics = sum(sum(x, dim=1) + sum(y, dim=1))
  call bench_stop_and_report('antisym')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('antisym')
  do i = 1, p
    do j = 1, p
      call bench_dump(l(i, j))
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(x, y, l)
end program antisym_natural
