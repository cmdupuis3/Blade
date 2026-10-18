! gram / expert -- G = X X^T for X (p x n), G(i,j) = sum_{s} X(i,s) X(j,s) accumulated in
! ascending s; checksum = sum of G(i,j)^2 over the full logical p x p matrix;
! input_checksum = sum of X.  Spec: bench/kernels/gram/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "gram"):
!  * canonical cells only (i <= j), stored PACKED by columns: G(i,j) at gp(j*(j-1)/2 + i);
!  * register-blocked micro-kernel: an MR x NR tile of G is held in registers and updated in
!    outer-product order (vectorized over i), so every cell is still ONE sequential FMA chain
!    over s = 1..n -- the strict regime's "no reassociation" holds and every cell is
!    bit-identical to a plain ascending dot loop;
!  * X is packed (inside the timed region) into MR-wide and NR-wide panels with the sample
!    axis padded with zeros to NBLK*KC: the micro-kernel streams contiguous memory with a
!    compile-time trip count, and the padding terms add fma(0, 0, acc) = acc exactly;
!  * the sample axis is blocked by KC (the p x KC slice of the A panels stays cache-resident
!    while every column panel streams it); a tile's accumulators are reloaded from G at the
!    start of each block, so blocking reorders no cell's sum;
!  * checksum read back from the packed triangle: diagonal once, off-diagonal twice;
!  * mt8: schedule(dynamic) over column panels, largest first (the kernel loop); the input
!    fill, the packing, the checksum and the per-row input sums are `parallel do` too.
!    `!$omp simd` (no reduction clause) on the micro-kernel's i loop: without it ifx
!    mis-vectorizes the blocked tile (spills; ~2.5x slower), gfortran is unaffected.
!  * licensed regime (-DBENCH_LICENSED): `!$omp simd reduction(+:coff)` on the checksum's
!    read-back of each packed column (review F5; Blade's checksum fold is licensed).  The
!    micro-kernel needs no reassociation in any regime;
!  * input checksum: MR adjacent variables summed together, each row ascending in s (F6).
! Builds: -DBENCH_INPUTONLY = inputonly twin (packing, kernel, checksum skipped; checksum = 0);
! -DBENCH_DUMP = G row-major to gram_dump.txt after the stop line.  Deallocation after the
! stop line.
include 'bench_support.f90'

program gram_expert
  use, intrinsic :: iso_fortran_env, only: int64, real64
  use bench_support
  implicit none
  integer, parameter :: p = 1601   ! SIZE p
  integer, parameter :: n = 1001   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  ! micro-tile MR x NR (8 x 6: 12 ymm accumulators); sample block KC <= 256, dividing the
  ! zero-padded sample axis exactly
  integer, parameter :: mr = 8, nr = 6
  integer, parameter :: nblk = (n + 255) / 256
  integer, parameter :: kc = (n + nblk - 1) / nblk
  integer, parameter :: npad = nblk * kc
  integer, parameter :: npa = (p + mr - 1) / mr, npb = (p + nr - 1) / nr
  integer(int64), parameter :: ncell = int(p, int64) * int(p + 1, int64) / 2

  real(real64), allocatable :: x(:,:), ap(:,:,:), bp(:,:,:), gp(:), rs(:)
  real(real64) :: cs, ics, cdiag, coff
  real(real64) :: c(mr, nr), sums(mr)
  integer :: i, s, j, ip, jp, ii, jj, i0, j0, nip, sb, m
  integer(int64) :: jo

  call bench_start()
  allocate(x(p, n), ap(mr, npad, npa), bp(nr, npad, npb), gp(ncell), rs(p))

  ! ---- input: x(i, s) = X(i-1, s-1); mathematical row-major index k = (i-1)*n + (s-1)
  !$omp parallel do private(i)
  do s = 1, n
    do i = 1, p
      x(i, s) = lcg(lin2(i - 1, s - 1, n), seed_x, lo, hi)
    end do
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  ! ---- pack MR-wide and NR-wide panels, zero padded past p and past n
  !$omp parallel do private(s, ii, i)
  do ip = 1, npa
    do s = 1, npad
      do ii = 1, mr
        i = (ip - 1) * mr + ii
        if (i <= p .and. s <= n) then
          ap(ii, s, ip) = x(i, s)
        else
          ap(ii, s, ip) = 0.0_real64
        end if
      end do
    end do
  end do
  !$omp end parallel do
  !$omp parallel do private(s, jj, j)
  do jp = 1, npb
    do s = 1, npad
      do jj = 1, nr
        j = (jp - 1) * nr + jj
        if (j <= p .and. s <= n) then
          bp(jj, s, jp) = x(j, s)
        else
          bp(jj, s, jp) = 0.0_real64
        end if
      end do
    end do
  end do
  !$omp end parallel do

  ! ---- the triangle of tiles; column panel jp needs the row panels covering i <= j0 + nr
  do sb = 1, npad, kc
    !$omp parallel do schedule(dynamic) private(j0, nip, ip, i0, c, jj, j, jo, m)
    do jp = npb, 1, -1
      j0 = (jp - 1) * nr
      nip = (min(j0 + nr, p) - 1) / mr + 1
      do ip = 1, nip
        i0 = (ip - 1) * mr
        if (sb == 1) then
          c = 0.0_real64
        else
          do jj = 1, nr
            j = j0 + jj
            m = 0
            if (j <= p) m = max(0, min(mr, j - i0))
            jo = int(j, int64) * int(j - 1, int64) / 2 + i0
            if (m > 0) c(1:m, jj) = gp(jo + 1:jo + m)
            c(m + 1:mr, jj) = 0.0_real64
          end do
        end if
        call tile(ap(1, sb, ip), bp(1, sb, jp), c)
        do jj = 1, nr
          j = j0 + jj
          if (j > p) exit
          m = max(0, min(mr, j - i0))
          jo = int(j, int64) * int(j - 1, int64) / 2 + i0
          if (m > 0) gp(jo + 1:jo + m) = c(1:m, jj)
        end do
      end do
    end do
    !$omp end parallel do
  end do

  ! ---- checksum over the full logical matrix: diagonal once, strict upper twice
  cdiag = 0.0_real64
  coff = 0.0_real64
  !$omp parallel do private(jo, i) reduction(+:cdiag, coff)
  do j = 1, p
    jo = int(j, int64) * int(j - 1, int64) / 2
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:coff)
#endif
    do i = 1, j - 1
      coff = coff + gp(jo + i) * gp(jo + i)
    end do
    cdiag = cdiag + gp(jo + j) * gp(jo + j)
  end do
  !$omp end parallel do
  cs = cdiag + 2.0_real64 * coff

#else
  cs = 0.0_real64
#endif

  ! ---- input checksum: per-variable sums over s (MR adjacent variables together, each row
  ! still ascending in s: review F6), then the sum over variables (serial: the same order in
  ! every build)
  !$omp parallel do private(i0, m, s, ii, sums)
  do ip = 1, npa
    i0 = (ip - 1) * mr
    m = min(mr, p - i0)
    sums = 0.0_real64
    do s = 1, n
      !$omp simd
      do ii = 1, m
        sums(ii) = sums(ii) + x(i0 + ii, s)
      end do
    end do
    rs(i0 + 1:i0 + m) = sums(1:m)
  end do
  !$omp end parallel do
  ics = 0.0_real64
  do i = 1, p
    ics = ics + rs(i)
  end do

  call bench_stop_and_report('gram')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  ! G row-major over the full logical matrix, read through the packed upper triangle
  call bench_dump_open('gram')
  do i = 1, p
    do j = 1, p
      jo = int(max(i, j), int64) * int(max(i, j) - 1, int64) / 2 + min(i, j)
      call bench_dump(gp(jo))
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(x, ap, bp, gp, rs)

contains

  ! c(ii, jj) <- c(ii, jj) + sum_{s=1..kc} a(ii, s) * b(jj, s): each cell a sequential FMA
  ! chain over s; the MR x NR accumulators stay in registers (vectorized over ii).
  subroutine tile(a, b, c)
    real(real64), intent(in) :: a(mr, kc), b(nr, kc)
    real(real64), intent(inout) :: c(mr, nr)
    real(real64) :: acc(mr, nr), bs
    integer :: s, ii, jj
    acc = c
    do s = 1, kc
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

end program gram_expert
