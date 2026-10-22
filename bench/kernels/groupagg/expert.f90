! groupagg / expert -- per-group s(g) = sum x, q(g) = sum x^2, c(g) = count over positional
! keys key(k) in [0, ngroups); checksum = sum s^2 + sum q + sum c^2; input_checksum =
! sum x + sum key.  Spec: bench/kernels/groupagg/spec.md.
!
! Expert choices:
!  * ONE pass over the rows in source order into direct-indexed accumulators s, q, c
!    (ngroups cells each, L1/L2-resident): the textbook form, no sort, no permutation, no
!    gathered copy.  Each group's chain is the ascending row order from 0 -- 0 + x = x
!    exactly (x >= 0), so the strict chains equal Blade's unseeded fold and its prodsum
!    from 0 bit for bit (spec, "Chain order").
!  * The three per-group arrays s, q, c are the named outputs, stored.
!  * serial only (spec: the per-group chains are independent, but Blade v1 has no thread
!    form; a parallel Fortran arm would need privatized accumulators and a combine -- a
!    different association).  licensed regime (-DBENCH_LICENSED): `!$omp simd reduction`
!    on the checksum folds over the groups and the input sums only (Blade's licensed
!    emission reassociates exactly those); the accumulation pass is a scatter (no
!    reduction directive is meaningful on it).
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = s, q, c to groupagg_dump.txt
! after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program groupagg_expert
  use, intrinsic :: iso_fortran_env, only: real64, int64
  use bench_support
  implicit none
  integer, parameter :: n = 1000003       ! SIZE n
  integer, parameter :: ngroups = 1009    ! SIZE ngroups
  integer, parameter :: seed_key = 10000019, seed_x = 20000038
  integer(int64), allocatable :: key(:), c(:)
  real(real64), allocatable :: x(:), s(:), q(:)
  real(real64) :: cs, ics, sk, v, a1, a2
  integer(int64) :: ic
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
  s = 0.0_real64
  q = 0.0_real64
  c = 0_int64
  do k = 1, n
    g = int(key(k)) + 1
    v = x(k)
    s(g) = s(g) + v
    q(g) = q(g) + v * v
    c(g) = c(g) + 1_int64
  end do
  a1 = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:a1)
#endif
  do g = 1, ngroups
    a1 = a1 + s(g) * s(g)
  end do
  a2 = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:a2)
#endif
  do g = 1, ngroups
    a2 = a2 + q(g)
  end do
  ic = 0_int64
  do g = 1, ngroups
    ic = ic + c(g) * c(g)
  end do
  cs = a1 + a2 + real(ic, real64)
#else
  cs = 0.0_real64
#endif

  ics = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:ics)
#endif
  do k = 1, n
    ics = ics + x(k)
  end do
  sk = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:sk)
#endif
  do k = 1, n
    sk = sk + real(key(k), real64)
  end do
  ics = ics + sk
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
end program groupagg_expert
