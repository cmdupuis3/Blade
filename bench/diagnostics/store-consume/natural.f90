! B04 PM52 diagnostic ONLY: same executable always dumps both full output buffers.
! Natural tier: two array assignments, then a checksum read-back.
include 'bench_support.f90'
program pm52_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 2053
  real(real64), allocatable :: x(:), y(:), p(:), q(:)
  real(real64) :: cs, sx, sy, ics
  integer :: k

  call bench_start()
  allocate(x(n), y(n), p(n), q(n))
  do k = 1, n
    x(k) = lcg(k - 1, 10000019, 0.0_real64, 1.0_real64)
    y(k) = lcg(k - 1, 20000038, 0.0_real64, 1.0_real64)
  end do
  p = 1.5_real64 * x + y
  q = p * p
  cs = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:cs)
#endif
  do k = 1, n
    cs = cs + q(k)
  end do
  sx = 0.0_real64
  sy = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:sx, sy)
#endif
  do k = 1, n
    sx = sx + x(k)
    sy = sy + y(k)
  end do
  ics = sx + sy
  call bench_stop_and_report('pm52_store_consume')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
  ! Always present: these are not conditional dump twins of a different timed program.
  call bench_dump_open('pm52_p')
  do k = 1, n
    call bench_dump(p(k))
  end do
  call bench_dump_close()
  call bench_dump_open('pm52_q')
  do k = 1, n
    call bench_dump(q(k))
  end do
  call bench_dump_close()
  deallocate(x, y, p, q)
end program pm52_natural
