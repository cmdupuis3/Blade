! maskagg / natural -- y = (x(k) : x(k) > 1.0, k ascending), m = |y|; checksum = sum y^2 + m;
! input_checksum = sum x.  Spec: bench/kernels/maskagg/spec.md.
! Natural tier: y = PACK(x, x > 1.0) into an allocatable (allocated by the assignment, inside
! the timed region), SIZE and SUM.  Serial only (no OpenMP directives).
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = y(1:m) to maskagg_dump.txt
! after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program maskagg_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 1000003   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 2.0_real64
  real(real64), allocatable :: x(:), y(:)
  real(real64) :: cs, ics
  integer :: k, m

  call bench_start()
  allocate(x(n))
  do k = 1, n
    x(k) = lcg(k - 1, seed_x, lo, hi)
  end do

#ifndef BENCH_INPUTONLY
  y = pack(x, x > 1.0_real64)
  m = size(y)
  cs = sum(y * y) + real(m, real64)
#else
  cs = 0.0_real64
#endif

  ics = 0.0_real64
  do k = 1, n
    ics = ics + x(k)
  end do
  call bench_stop_and_report('maskagg')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('maskagg')
  do k = 1, m
    call bench_dump(y(k))
  end do
  call bench_dump_close()
#endif
  deallocate(x)
  if (allocated(y)) deallocate(y)
end program maskagg_natural
