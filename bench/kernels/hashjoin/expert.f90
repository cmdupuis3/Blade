! hashjoin / expert -- the inner equijoin of (lk, la) and (rk, ra), its relation stored:
!   matched = (la(i) * ra(j) : lk(i) == rk(j)), (i, j) in lex order (i ascending, then j);
!   checksum = sum matched + |matched| + fp, fp = left fold a -> mod(3 a + b, 1000000007) from 0;
!   input_checksum = sum lk + sum la + sum rk + sum ra.  Spec: bench/kernels/hashjoin/spec.md.
!
! Expert tier: the CSR hash join with a COUNTING SORT over the key domain [0, K):
!  * build: cnt(k) = right rows of key k, an exclusive prefix (off), then a FORWARD fill of
!    perm (each bucket lists its right rows j ascending -- stable, every duplicate kept);
!  * size: J = sum over i of off(lk(i)+1) - off(lk(i)) (a counting pass over the left keys);
!  * probe: i ascending, every row of the bucket ascending, matched(q) = la(i) * ra(j) --
!    lex order -- with the total and the fingerprint FUSED into the probe (the stores still
!    happen; README section 1.5's rule: an arm may fuse the checksum into the producing loop).
! Like Blade's v1 it relies on nothing but the keys' values: a key outside [0, K) cannot
! occur in this generated distribution (floor of [0, K)), which the expert USES (the
! direct-index table is sized K with no range check); Blade's build measures the span instead.
! Serial: a legal output-owned threaded form exists (per-left-row output offsets from the
! counting pass, each thread writing its own rows' cells) but Blade's v1 is serial, so this
! suite records no mt8 arm (kernel.json).  Integer arithmetic throughout: licensed and repro
! are the same.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = matched(1:J) to hashjoin_dump.txt
! after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program hashjoin_expert
  use, intrinsic :: iso_fortran_env, only: int64, real64
  use bench_support
  implicit none
  integer, parameter :: n = 2003   ! SIZE n
  integer, parameter :: m = 1009   ! SIZE m
  integer, parameter :: nk = 401   ! SIZE k
  integer, parameter :: s1 = 10000019, s2 = 20000038, s3 = 30000057, s4 = 40000076
  integer(int64), parameter :: md = 1000000007_int64
  integer(int64), allocatable :: lk(:), la(:), rk(:), ra(:), matched(:)
  integer, allocatable :: off(:), cur(:), perm(:)
  integer(int64) :: total, fp, isum, v, li
  real(real64) :: cs, ics
  integer :: i, j, q, nj, kk, t

  call bench_start()
  allocate(lk(0:n - 1), la(0:n - 1), rk(0:m - 1), ra(0:m - 1))
  do i = 0, n - 1
    lk(i) = int(floor(lcg(i, s1, 0.0_real64, real(nk, real64))), int64)
    la(i) = int(floor(lcg(i, s2, 0.0_real64, 1024.0_real64)), int64)
  end do
  do j = 0, m - 1
    rk(j) = int(floor(lcg(j, s3, 0.0_real64, real(nk, real64))), int64)
    ra(j) = int(floor(lcg(j, s4, 0.0_real64, 1024.0_real64)), int64)
  end do

#ifndef BENCH_INPUTONLY
  allocate(off(0:nk), cur(0:nk - 1), perm(0:m - 1))
  off = 0
  do j = 0, m - 1
    kk = int(rk(j))
    off(kk + 1) = off(kk + 1) + 1
  end do
  do kk = 0, nk - 1
    off(kk + 1) = off(kk + 1) + off(kk)
  end do
  cur(0:nk - 1) = off(0:nk - 1)
  do j = 0, m - 1
    kk = int(rk(j))
    perm(cur(kk)) = j
    cur(kk) = cur(kk) + 1
  end do
  nj = 0
  do i = 0, n - 1
    kk = int(lk(i))
    nj = nj + (off(kk + 1) - off(kk))
  end do
  allocate(matched(nj))
  q = 0
  total = 0_int64
  fp = 0_int64
  do i = 0, n - 1
    kk = int(lk(i))
    li = la(i)
    do t = off(kk), off(kk + 1) - 1
      v = li * ra(perm(t))
      q = q + 1
      matched(q) = v
      total = total + v
      fp = mod(3_int64 * fp + v, md)
    end do
  end do
  cs = real(total, real64) + real(nj, real64) + real(fp, real64)
#else
  cs = 0.0_real64
#endif

  isum = sum(lk) + sum(la) + sum(rk) + sum(ra)
  ics = real(isum, real64)
  call bench_stop_and_report('hashjoin')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('hashjoin')
  do q = 1, nj
    call bench_dump(real(matched(q), real64))
  end do
  call bench_dump_close()
#endif
#ifndef BENCH_INPUTONLY
  deallocate(matched, off, cur, perm)
#endif
  deallocate(lk, la, rk, ra)
end program hashjoin_expert
