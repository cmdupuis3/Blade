! hashjoin / natural -- the inner equijoin of (lk, la) and (rk, ra), its relation stored:
!   matched = (la(i) * ra(j) : lk(i) == rk(j)), (i, j) in lex order (i ascending, then j);
!   checksum = sum matched + |matched| + fp, fp = left fold a -> mod(3 a + b, 1000000007) from 0;
!   input_checksum = sum lk + sum la + sum rk + sum ra.  Spec: bench/kernels/hashjoin/spec.md.
!
! Natural tier: the textbook nested loops, O(N M) comparisons -- a COUNTING double loop
! sizes matched (J is not known beforehand), then a second double loop appends every
! matching payload in lex order into the allocated relation (no N x M matrix is ever
! formed: review B/13). The total, the count and the fingerprint are a separate pass over the
! stored relation.  Integer keys and payloads: every value is exact, licensed and repro are
! the same arithmetic.  Serial (spec: no mt8 arm).
! Builds: -DBENCH_INPUTONLY = inputonly twin (no join, checksum = 0; the fills and the input
! sums are the total's); -DBENCH_DUMP = matched(1:J) to hashjoin_dump.txt after the stop line.
! Deallocation after the stop line.
include 'bench_support.f90'

program hashjoin_natural
  use, intrinsic :: iso_fortran_env, only: int64, real64
  use bench_support
  implicit none
  integer, parameter :: n = 2003   ! SIZE n
  integer, parameter :: m = 1009   ! SIZE m
  integer, parameter :: nk = 401   ! SIZE k
  integer, parameter :: s1 = 10000019, s2 = 20000038, s3 = 30000057, s4 = 40000076
  integer(int64), parameter :: md = 1000000007_int64
  integer(int64), allocatable :: lk(:), la(:), rk(:), ra(:), matched(:)
  integer(int64) :: total, fp, isum
  real(real64) :: cs, ics
  integer :: i, j, q, nj

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
  nj = 0
  do i = 0, n - 1
    do j = 0, m - 1
      if (lk(i) == rk(j)) nj = nj + 1
    end do
  end do
  allocate(matched(nj))
  q = 0
  do i = 0, n - 1
    do j = 0, m - 1
      if (lk(i) == rk(j)) then
        q = q + 1
        matched(q) = la(i) * ra(j)
      end if
    end do
  end do
  total = 0_int64
  fp = 0_int64
  do q = 1, nj
    total = total + matched(q)
    fp = mod(3_int64 * fp + matched(q), md)
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
  deallocate(matched)
#endif
  deallocate(lk, la, rk, ra)
end program hashjoin_natural
