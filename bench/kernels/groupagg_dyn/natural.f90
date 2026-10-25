! groupagg_dyn / natural -- groupagg over DYNAMIC keys key(k) = 7919 * floor(ngroups * u) + 13:
! groups numbered by FIRST occurrence; s(g) = sum x, q(g) = sum x^2, c(g) = count;
! checksum = sum s^2 + sum q + sum c^2; input_checksum = sum x + sum key.
! Spec: bench/kernels/groupagg_dyn/spec.md (shared definition: bench/kernels/groupagg/spec.md).
! Natural tier: the distinct keys discovered in first-occurrence order with ANY over the keys
! found so far, then one masked intrinsic per group and output (as groupagg's natural):
! O(n * G) twice.  Serial (no directives).
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = s, q, c to groupagg_dyn_dump.txt
! after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program groupagg_dyn_natural
  use, intrinsic :: iso_fortran_env, only: real64, int64
  use bench_support
  implicit none
  integer, parameter :: n = 1000003       ! SIZE n
  integer, parameter :: ngroups = 1009    ! SIZE ngroups
  integer, parameter :: seed_key = 10000019, seed_x = 20000038
  integer(int64), allocatable :: key(:), uk(:), c(:)
  real(real64), allocatable :: x(:), s(:), q(:)
  real(real64) :: cs, ics
  integer :: k, g, ng

  call bench_start()
  allocate(key(n), x(n))
  do k = 1, n
    key(k) = 7919_int64 * int(floor(lcg(k - 1, seed_key, 0.0_real64, 1.0_real64) * real(ngroups, real64)), int64) &
             + 13_int64
  end do
  do k = 1, n
    x(k) = lcg(k - 1, seed_x, 0.0_real64, 2.0_real64)
  end do

#ifndef BENCH_INPUTONLY
  allocate(uk(n))
  ng = 0
  do k = 1, n
    if (.not. any(uk(1:ng) == key(k))) then
      ng = ng + 1
      uk(ng) = key(k)
    end if
  end do
  allocate(s(ng), q(ng), c(ng))
  do g = 1, ng
    s(g) = sum(x, mask = key == uk(g))
    q(g) = sum(x * x, mask = key == uk(g))
    c(g) = count(key == uk(g))
  end do
  cs = sum(s * s) + sum(q) + real(sum(c * c), real64)
#else
  cs = 0.0_real64
#endif

  ics = sum(x) + sum(real(key, real64))
  call bench_stop_and_report('groupagg_dyn')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('groupagg_dyn')
  do g = 1, ng
    call bench_dump(s(g))
    call bench_dump(q(g))
    call bench_dump(real(c(g), real64))
  end do
  call bench_dump_close()
#endif
  deallocate(key, x)
  if (allocated(s)) deallocate(s, q, c, uk)
end program groupagg_dyn_natural
