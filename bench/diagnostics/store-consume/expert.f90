! B04 PM52 diagnostic ONLY: scalar forwarding preserves BOTH mandatory stores.
include 'bench_support.f90'
program pm52_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 2053
  real(real64), allocatable :: x(:), y(:), p(:), q(:)
  real(real64) :: cs, sx, sy, ics, v, w
  integer :: k

  call bench_start()
  allocate(x(n), y(n), p(n), q(n))
  do k = 1, n
    x(k) = lcg(k - 1, 10000019, 0.0_real64, 1.0_real64)
    y(k) = lcg(k - 1, 20000038, 0.0_real64, 1.0_real64)
  end do
  cs = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd private(v, w) reduction(+:cs)
#endif
  do k = 1, n
    v = 1.5_real64 * x(k) + y(k)
    p(k) = v
    w = v * v
    q(k) = w
    cs = cs + w
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
end program pm52_expert
