! selfcheck.f90 -- contract self-check for the harness parser and the cross-language LCG.
! x(k) = lcg(k, seed = 10000019, lo = -1.0, hi = 1.0), k = 0..9.
! input_checksum = sum(x), checksum = sum(x**2).
include 'bench_support.f90'

program selfcheck
  use, intrinsic :: iso_fortran_env, only: int64, real64
  use bench_support
  implicit none
  integer, parameter :: n = 10   ! SIZE n
  real(real64) :: x(0:n-1), s, ss
  integer :: k

  call bench_start()
  do k = 0, n - 1
    x(k) = lcg(int(k, int64), 10000019, -1.0_real64, 1.0_real64)
  end do
  s = 0.0_real64
  ss = 0.0_real64
  do k = 0, n - 1
    s = s + x(k)
    ss = ss + x(k) * x(k)
  end do
  call bench_stop_and_report('selfcheck')
  call bench_print('checksum', ss)
  call bench_print('input_checksum', s)
end program selfcheck
