! groupagg_dyn / expert -- groupagg over DYNAMIC keys key(k) = 7919 * floor(ngroups * u) + 13:
! groups numbered by FIRST occurrence; s(g) = sum x, q(g) = sum x^2, c(g) = count;
! checksum = sum s^2 + sum q + sum c^2; input_checksum = sum x + sum key.
! Spec: bench/kernels/groupagg_dyn/spec.md (shared definition: bench/kernels/groupagg/spec.md).
!
! Expert choices:
!  * ONE pass over the rows in source order: an open-addressing hash (power-of-two capacity,
!    multiplicative hashing of the key's bits, linear probing, load <= 1/2, doubled and
!    rehashed on growth) maps each key to its group number, assigned by FIRST occurrence;
!    the direct-indexed accumulators s, q, c grow with it.  One probe per row -- the same
!    work Blade's one pass does through its C core.  No sort, no permutation, no gather.
!  * Each group's chain is the ascending row order from 0 (0 + x = x exactly, x >= 0): the
!    strict chains equal Blade's unseeded fold and its prodsum from 0 bit for bit.
!  * The key bound is the spec's (keys < 2^24, so key * 2654435761 < 2^56: no overflow).
!  * serial only, as groupagg; licensed regime (-DBENCH_LICENSED): `!$omp simd reduction` on
!    the checksum folds over the groups and the input sums only.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = s, q, c (first-occurrence order)
! to groupagg_dyn_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program groupagg_dyn_expert
  use, intrinsic :: iso_fortran_env, only: real64, int64
  use bench_support
  implicit none
  integer, parameter :: n = 1000003       ! SIZE n
  integer, parameter :: ngroups = 1009    ! SIZE ngroups
  integer, parameter :: seed_key = 10000019, seed_x = 20000038
  integer(int64), parameter :: mult = 2654435761_int64
  integer(int64), allocatable :: key(:), c(:), hkey(:), tc(:)
  integer, allocatable :: hgrp(:)
  real(real64), allocatable :: x(:), s(:), q(:), ts(:), tq(:)
  real(real64) :: cs, ics, sk, v, a1, a2
  integer(int64) :: ic, kk, h, msk
  integer :: k, g, ng, cap, gcap, i, shift

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
  cap = 1024
  shift = 10
  msk = int(cap - 1, int64)
  allocate(hkey(0:cap - 1), hgrp(0:cap - 1))
  hgrp = 0
  gcap = 512
  allocate(s(gcap), q(gcap), c(gcap))
  ng = 0
  do k = 1, n
    kk = key(k)
    h = iand(ishft(kk * mult, -shift), msk)
    do while (hgrp(h) /= 0)
      if (hkey(h) == kk) exit
      h = iand(h + 1_int64, msk)
    end do
    g = hgrp(h)
    if (g == 0) then
      ! a new group: first occurrence numbering
      ng = ng + 1
      if (ng > gcap) then
        allocate(ts(2 * gcap), tq(2 * gcap), tc(2 * gcap))
        ts(1:gcap) = s
        tq(1:gcap) = q
        tc(1:gcap) = c
        call move_alloc(ts, s)
        call move_alloc(tq, q)
        call move_alloc(tc, c)
        gcap = 2 * gcap
      end if
      s(ng) = 0.0_real64
      q(ng) = 0.0_real64
      c(ng) = 0_int64
      hkey(h) = kk
      hgrp(h) = ng
      g = ng
      if (2 * ng > cap) call grow()
    end if
    v = x(k)
    s(g) = s(g) + v
    q(g) = q(g) + v * v
    c(g) = c(g) + 1_int64
  end do
  a1 = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:a1)
#endif
  do g = 1, ng
    a1 = a1 + s(g) * s(g)
  end do
  a2 = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:a2)
#endif
  do g = 1, ng
    a2 = a2 + q(g)
  end do
  ic = 0_int64
  do g = 1, ng
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
#ifndef BENCH_INPUTONLY
  deallocate(s, q, c, hkey, hgrp)
#endif

contains

  ! double the table and reinsert every occupied slot (group numbers unchanged)
  subroutine grow()
    integer(int64), allocatable :: nkey(:)
    integer, allocatable :: ngrp(:)
    integer(int64) :: nmsk, hh
    integer :: ncap
    ncap = 2 * cap
    nmsk = int(ncap - 1, int64)
    allocate(nkey(0:ncap - 1), ngrp(0:ncap - 1))
    ngrp = 0
    do i = 0, cap - 1
      if (hgrp(i) /= 0) then
        hh = iand(ishft(hkey(i) * mult, -(shift + 1)), nmsk)
        do while (ngrp(hh) /= 0)
          hh = iand(hh + 1_int64, nmsk)
        end do
        nkey(hh) = hkey(i)
        ngrp(hh) = hgrp(i)
      end if
    end do
    call move_alloc(nkey, hkey)
    call move_alloc(ngrp, hgrp)
    cap = ncap
    shift = shift + 1
    msk = nmsk
  end subroutine grow
end program groupagg_dyn_expert
