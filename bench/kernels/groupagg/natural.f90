! groupagg / natural -- per-group s(g) = sum x, q(g) = sum x^2, c(g) = count over positional
! keys key(k) in [0, ngroups); checksum = sum s^2 + sum q + sum c^2; input_checksum =
! sum x + sum key.  Spec: bench/kernels/groupagg/spec.md.
! Natural tier: the literal transcription with masked intrinsics, one per group and output:
!   s(g) = SUM(x, MASK = key == g),  q(g) = SUM(x * x, MASK = key == g),  c(g) = COUNT(key == g)
! O(n * ngroups) (~3e9 element tests at full size): the dense baseline, not a competent
! one-pass program (that is the expert).  Serial (no directives).
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = s, q, c to groupagg_dump.txt
! after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program groupagg_natural
  use, intrinsic :: iso_fortran_env, only: real64, int64
  use bench_support
  implicit none
  integer, parameter :: n = 1000003       ! SIZE n
  integer, parameter :: ngroups = 1009    ! SIZE ngroups
  integer, parameter :: seed_key = 10000019, seed_x = 20000038
  integer(int64), allocatable :: key(:), c(:)
  real(real64), allocatable :: x(:), s(:), q(:)
  real(real64) :: cs, ics
  integer :: k, g

  call bench_start()
  allocate(key(n), x(n))
  do k = 1, n
    key(k) = int(floor(lcg(k - 1, seed_key, 0.0_real64, 1.0_real64) * real(ngroups, real64)), int64)
  end do
  do k = 1, n
    x(k) = lcg(k - 1, seed_x, 0.0_real64, 2.0_real64)
  end do

#ifndef BENCH_INPUTONLY
  allocate(s(ngroups), q(ngroups), c(ngroups))
  do g = 1, ngroups
    s(g) = sum(x, mask = key == int(g - 1, int64))
    q(g) = sum(x * x, mask = key == int(g - 1, int64))
    c(g) = count(key == int(g - 1, int64))
  end do
  cs = sum(s * s) + sum(q) + real(sum(c * c), real64)
#else
  cs = 0.0_real64
#endif

  ics = sum(x) + sum(real(key, real64))
  call bench_stop_and_report('groupagg')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('groupagg')
  do g = 1, ngroups
    call bench_dump(s(g))
    call bench_dump(q(g))
    call bench_dump(real(c(g), real64))
  end do
  call bench_dump_close()
#endif
  deallocate(key, x)
  if (allocated(s)) deallocate(s, q, c)
end program groupagg_natural
