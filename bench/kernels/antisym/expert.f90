! antisym / expert -- L(i,j) = sum_s X(i,s) Y(j,s) - sum_s X(j,s) Y(i,s) for X, Y (p x n);
! checksum = sum of L^2 over the full p x p matrix; input_checksum = sum X + sum Y.
! Spec: bench/kernels/antisym/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "antisym"):
!  * strict upper triangle only (i < j), packed by columns: L(i,j) at lp((j-1)(j-2)/2 + i);
!  * each MR x NR tile is two register-blocked products, (X Y^T) and (Y X^T), with the same
!    micro-kernel as gram (vectorized over i, every inner product ONE sequential FMA chain
!    over s), subtracted once per cell at the end (the spec's dot - dot);
!  * X and Y packed into MR- and NR-wide panels (inside the timed region); n is small
!    enough that a whole panel pair stays cache-resident, so the sample axis is not blocked;
!  * checksum read back from the strict triangle with weight 2 (the diagonal is zero);
!  * mt8: schedule(dynamic) over column panels, largest first (the kernel loop); the fill,
!    the packing, the checksum and the per-row input sums are `parallel do` too.
!    `!$omp simd` (no reduction clause) on the micro-kernel's i loop.
!  * licensed regime (-DBENCH_LICENSED): the strict-triangle checksum loop becomes
!    `parallel do simd reduction(+:cs)` / `simd reduction(+:cs)` (review F5);
!  * input checksum: MR adjacent particles summed together, each row ascending in s (F6).
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = L row-major (signed, zero
! diagonal) to antisym_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program antisym_expert
  use, intrinsic :: iso_fortran_env, only: int64, real64
  use bench_support
  implicit none
  integer, parameter :: p = 1201   ! SIZE p
  integer, parameter :: n = 401    ! SIZE n
  integer, parameter :: seed_x = 10000019, seed_y = 20000038
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  integer, parameter :: mr = 8, nr = 6
  integer, parameter :: npa = (p + mr - 1) / mr, npb = (p + nr - 1) / nr
  integer(int64), parameter :: ncell = int(p, int64) * int(p - 1, int64) / 2

  real(real64), allocatable :: x(:,:), y(:,:), axp(:,:,:), ayp(:,:,:), bxp(:,:,:), byp(:,:,:)
  real(real64), allocatable :: lp(:), rs(:)
  real(real64) :: c1(mr, nr), c2(mr, nr), cs, ics, sumx(mr), sumy(mr)
  integer :: i, j, s, ip, jp, ii, jj, i0, j0, nip, m
  integer(int64) :: jo

  call bench_start()
  allocate(x(p, n), y(p, n), axp(mr, n, npa), ayp(mr, n, npa), bxp(nr, n, npb), byp(nr, n, npb))
  allocate(lp(ncell), rs(p))

  ! ---- inputs: x(i, s) = X(i-1, s-1), y(i, s) = Y(i-1, s-1); k = (i-1)*n + (s-1)
  !$omp parallel do private(i)
  do s = 1, n
    do i = 1, p
      x(i, s) = lcg(lin2(i - 1, s - 1, n), seed_x, lo, hi)
      y(i, s) = lcg(lin2(i - 1, s - 1, n), seed_y, lo, hi)
    end do
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  ! ---- MR-wide and NR-wide panels of X and Y, zero padded past p
  !$omp parallel do private(s, ii, i)
  do ip = 1, npa
    do s = 1, n
      do ii = 1, mr
        i = (ip - 1) * mr + ii
        if (i <= p) then
          axp(ii, s, ip) = x(i, s)
          ayp(ii, s, ip) = y(i, s)
        else
          axp(ii, s, ip) = 0.0_real64
          ayp(ii, s, ip) = 0.0_real64
        end if
      end do
    end do
  end do
  !$omp end parallel do
  !$omp parallel do private(s, jj, j)
  do jp = 1, npb
    do s = 1, n
      do jj = 1, nr
        j = (jp - 1) * nr + jj
        if (j <= p) then
          bxp(jj, s, jp) = x(j, s)
          byp(jj, s, jp) = y(j, s)
        else
          bxp(jj, s, jp) = 0.0_real64
          byp(jj, s, jp) = 0.0_real64
        end if
      end do
    end do
  end do
  !$omp end parallel do

  ! ---- strict triangle of tiles: column panel jp needs rows i <= min(j0 + nr, p) - 1
  !$omp parallel do schedule(dynamic) private(j0, nip, ip, i0, c1, c2, jj, j, jo, m)
  do jp = npb, 1, -1
    j0 = (jp - 1) * nr
    nip = (min(j0 + nr, p) - 2) / mr + 1
    do ip = 1, nip
      i0 = (ip - 1) * mr
      c1 = 0.0_real64
      call tile(axp(1, 1, ip), byp(1, 1, jp), c1)
      c2 = 0.0_real64
      call tile(ayp(1, 1, ip), bxp(1, 1, jp), c2)
      do jj = 1, nr
        j = j0 + jj
        if (j > p) exit
        m = max(0, min(mr, j - 1 - i0))
        jo = int(j - 1, int64) * int(j - 2, int64) / 2 + i0
        if (m > 0) lp(jo + 1:jo + m) = c1(1:m, jj) - c2(1:m, jj)
      end do
    end do
  end do
  !$omp end parallel do

  ! ---- checksum over the full logical matrix: 2 x the strict triangle (zero diagonal)
  cs = 0.0_real64
#if defined(BENCH_LICENSED) && defined(_OPENMP)
  !$omp parallel do simd reduction(+:cs)
#elif defined(BENCH_LICENSED)
  !$omp simd reduction(+:cs)
#else
  !$omp parallel do reduction(+:cs)
#endif
  do jo = 1, ncell
    cs = cs + lp(jo) * lp(jo)
  end do
  cs = 2.0_real64 * cs

#else
  cs = 0.0_real64
#endif

  ! ---- input checksum in the order of the Blade source: per particle
  ! (sum_s X(i,s) + sum_s Y(i,s)), then the sum over particles
  ! (MR adjacent particles together, each row still ascending in s: review F6)
  !$omp parallel do private(i0, m, s, ii, sumx, sumy)
  do ip = 1, npa
    i0 = (ip - 1) * mr
    m = min(mr, p - i0)
    sumx = 0.0_real64
    sumy = 0.0_real64
    do s = 1, n
      !$omp simd
      do ii = 1, m
        sumx(ii) = sumx(ii) + x(i0 + ii, s)
        sumy(ii) = sumy(ii) + y(i0 + ii, s)
      end do
    end do
    rs(i0 + 1:i0 + m) = sumx(1:m) + sumy(1:m)
  end do
  !$omp end parallel do
  ics = 0.0_real64
  do i = 1, p
    ics = ics + rs(i)
  end do

  call bench_stop_and_report('antisym')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  ! L row-major over the full logical matrix: strict upper stored, L(j,i) = -L(i,j), L(i,i) = 0
  call bench_dump_open('antisym')
  do i = 1, p
    do j = 1, p
      if (i < j) then
        call bench_dump(lp(int(j - 1, int64) * int(j - 2, int64) / 2 + i))
      else if (i > j) then
        call bench_dump(-lp(int(i - 1, int64) * int(i - 2, int64) / 2 + j))
      else
        call bench_dump(0.0_real64)
      end if
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(x, y, axp, ayp, bxp, byp, lp, rs)

contains

  ! c(ii, jj) <- c(ii, jj) + sum_{s=1..n} a(ii, s) * b(jj, s): each cell a sequential FMA
  ! chain over s; the MR x NR accumulators stay in registers (vectorized over ii).
  subroutine tile(a, b, c)
    real(real64), intent(in) :: a(mr, n), b(nr, n)
    real(real64), intent(inout) :: c(mr, nr)
    real(real64) :: acc(mr, nr), bs
    integer :: s, ii, jj
    acc = c
    do s = 1, n
      do jj = 1, nr
        bs = b(jj, s)
        !$omp simd
        do ii = 1, mr
          acc(ii, jj) = acc(ii, jj) + a(ii, s) * bs
        end do
      end do
    end do
    c = acc
  end subroutine tile

end program antisym_expert
