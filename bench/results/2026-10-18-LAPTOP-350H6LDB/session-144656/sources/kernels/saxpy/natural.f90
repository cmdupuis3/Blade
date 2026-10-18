! saxpy / natural -- y2 = 1.5 x + y (named output, stored); checksum = sum y2^2;
! input_checksum = sum x + sum y.  Spec: bench/kernels/saxpy/spec.md.
! Natural tier: the array statement y2 = 1.5*x + y (PARALLEL WORKSHARE in mt8: both
! compilers divide a plain array assignment among the threads), then the sum of squares as
! a `parallel do reduction` loop (inside WORKSHARE a SUM runs on one thread).
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = y2 to saxpy_dump.txt after the
! stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program saxpy_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 2000003   ! SIZE n
  integer, parameter :: seed_x = 10000019, seed_y = 20000038
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:), y(:), y2(:)
  real(real64) :: cs, ics, sx, sy
  integer :: k

  call bench_start()
  allocate(x(n), y(n), y2(n))
  !$omp parallel do
  do k = 1, n
    x(k) = lcg(k - 1, seed_x, lo, hi)
    y(k) = lcg(k - 1, seed_y, lo, hi)
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  !$omp parallel workshare
  y2 = 1.5_real64 * x + y
  !$omp end parallel workshare
  cs = 0.0_real64
  !$omp parallel do reduction(+:cs)
  do k = 1, n
    cs = cs + y2(k) * y2(k)
  end do
  !$omp end parallel do
#else
  cs = 0.0_real64
#endif

  ! input checksum in the order of the Blade source: (sum x) + (sum y), each ascending
  sx = 0.0_real64
  sy = 0.0_real64
  do k = 1, n
    sx = sx + x(k)
    sy = sy + y(k)
  end do
  ics = sx + sy
  call bench_stop_and_report('saxpy')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('saxpy')
  do k = 1, n
    call bench_dump(y2(k))
  end do
  call bench_dump_close()
#endif
  deallocate(x, y, y2)
end program saxpy_natural
