! tensorop / expert -- Y = (A (x) B (x) C) X as three ordered mode contractions:
!   T1(a, j, k) = sum_i A(a, i) X(i, j, k);  T2(a, b, k) = sum_j B(b, j) T1(a, j, k);
!   Y(a, b, c)  = sum_k C(c, k) T2(a, b, k);
! checksum = sum Y^2; input_checksum = sum X + sum A + sum B + sum C.
! Spec: bench/kernels/tensorop/spec.md.
!
! Expert choices (spec.md "Tiers"):
!  * flat storage in the MATHEMATICAL row-major order (xs(q) is X's cell with linear index
!    q - 1, likewise as_, bs, t1s, t2s, ys), except C, which is stored transposed (cct holds
!    C(c, k) at c + n3*k, 0-based: generated directly in that orientation);
!  * the factor order is the spec's (I, then J, then K); each contraction is ONE call shape,
!    out(r, q) = sum_t p(r, t) * w(t, q) (column-major, r contiguous), on views of the flat
!    arrays -- no copies, no packing:
!      mode I:  p = X as (n2*n3) x n1 (r = (j, k), t = i),   w = A as n1 x n1 (w(i, a) = A(a, i)),
!               out = T1 as (n2*n3) x n1;
!      mode J:  per a, p = T1(a) as n3 x n2 (r = k, t = j),  w = B as n2 x n2 (w(j, b) = B(b, j)),
!               out = T2(a) as n3 x n2;
!      mode K:  p = C^T as n3 x n3 (p(c, k) = C(c, k)),      w = T2 as n3 x (n1*n2) (w(k, (a, b))),
!               out = Y as n3 x (n1*n2);
!  * the call is an 8 x 6 register tile (8 contiguous rows = 2 ymm, 6 columns: 12 vector
!    accumulators), rows blocked by MC = 240 so the p panel stays in L2 across the column
!    tiles; every cell is ONE chain seeded with +0 and accumulated in ascending t with no
!    split (the tile only jams DIFFERENT cells), so the per-cell order is the spec's in every
!    regime -- the contraction loops carry no reduction directive in any regime;
!  * the checksum is a separate ordered pass (per (a, b) row, then rows, then planes: the
!    Blade source's grouping), and so is each input sum (X per (i, j) row, then rows, then
!    planes; A, B, C per mathematical row, C's rows through the transposed cct); licensed
!    (-DBENCH_LICENSED): `simd reduction(+:w)` on those innermost row sums (the Blade source
!    licenses those folds); repro: none;
!  * mt8: `parallel do` over row blocks (mode I), over a (mode J), over column chunks
!    (mode K), over a (checksum, reduction(+:)), on the fills, and on the outer loop of each
!    of the four input sums (reduction(+:)).
! Builds: -DBENCH_INPUTONLY = inputonly twin (contractions and checksum skipped, checksum =
! 0); -DBENCH_DUMP = Y row-major (a, b, c) to tensorop_dump.txt after the stop line.
! Deallocation after the stop line.
include 'bench_support.f90'

module tensorop_tile
  use, intrinsic :: iso_fortran_env, only: real64
  implicit none
  private
  public :: gemm_nn
  integer, parameter :: mr = 8, nr = 6, mc = 240
contains
  ! out(1:m, 1:nq) = p(1:m, 1:kd) * w(1:kd, 1:nq) (leading dimensions ldp, ldw, ldo), one
  ! +0-seeded chain per cell in ascending t.
  subroutine gemm_nn(m, nq, kd, p, ldp, w, ldw, out, ldo)
    integer, intent(in) :: m, nq, kd, ldp, ldw, ldo
    real(real64), intent(in) :: p(ldp, *), w(ldw, *)
    real(real64), intent(inout) :: out(ldo, *)
    ! acc (the full tile) is only ever indexed by constants, so the compiler keeps it in
    ! registers; the edge tiles use their own array (variable extents)
    real(real64) :: acc(mr, nr), acce(mr, nr)
    integer :: rb, re, q0, r0, t, qq, mrr, nqq
    do rb = 1, m, mc
      re = min(rb + mc - 1, m)
      do q0 = 1, nq, nr
        nqq = min(nr, nq - q0 + 1)
        do r0 = rb, re, mr
          mrr = min(mr, re - r0 + 1)
          if (mrr == mr .and. nqq == nr) then
            acc = 0.0_real64
            do t = 1, kd
              do qq = 1, nr
                acc(:, qq) = acc(:, qq) + p(r0:r0 + mr - 1, t) * w(t, q0 + qq - 1)
              end do
            end do
            out(r0:r0 + mr - 1, q0:q0 + nr - 1) = acc
          else
            acce = 0.0_real64
            do t = 1, kd
              do qq = 1, nqq
                acce(1:mrr, qq) = acce(1:mrr, qq) + p(r0:r0 + mrr - 1, t) * w(t, q0 + qq - 1)
              end do
            end do
            out(r0:r0 + mrr - 1, q0:q0 + nqq - 1) = acce(1:mrr, 1:nqq)
          end if
        end do
      end do
    end do
  end subroutine gemm_nn
end module tensorop_tile

program tensorop_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  use tensorop_tile
  implicit none
  integer, parameter :: n1 = 89    ! SIZE n1
  integer, parameter :: n2 = 97    ! SIZE n2
  integer, parameter :: n3 = 101   ! SIZE n3
  integer, parameter :: njk = n2 * n3, nab = n1 * n2, ncells = n1 * n2 * n3
  integer, parameter :: rchunk = 240, qchunk = 48
  integer, parameter :: seed_x = 10000019, seed_a = 20000038, seed_b = 30000057, seed_c = 40000076
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: xs(:), as_(:), bs(:), cct(:), t1s(:), t2s(:), ys(:)
  real(real64) :: cs, ics, sx, sa, sb, sc, w, sp
  integer :: q, i, j, a, b, c, k, rc, qc, base

  call bench_start()
  allocate(xs(ncells), as_(n1 * n1), bs(n2 * n2), cct(n3 * n3))
#ifndef BENCH_INPUTONLY
  allocate(t1s(ncells), t2s(ncells), ys(ncells))
#endif
  !$omp parallel do
  do q = 1, ncells
    xs(q) = lcg(q - 1, seed_x, lo, hi)
  end do
  !$omp end parallel do
  !$omp parallel do
  do q = 1, n1 * n1
    as_(q) = lcg(q - 1, seed_a, lo, hi)
  end do
  !$omp end parallel do
  !$omp parallel do
  do q = 1, n2 * n2
    bs(q) = lcg(q - 1, seed_b, lo, hi)
  end do
  !$omp end parallel do
  ! cct(c + n3*k) (0-based) = C(c, k): the mathematical C transposed
  !$omp parallel do private(c)
  do k = 1, n3
    do c = 1, n3
      cct(c + n3 * (k - 1)) = lcg(lin2(c - 1, k - 1, n3), seed_c, lo, hi)
    end do
  end do
  !$omp end parallel do

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
  ! mode I: T1 (n2*n3 x n1) = X (n2*n3 x n1) * A^T, row blocks of rchunk
  !$omp parallel do schedule(static)
  do rc = 1, njk, rchunk
    call gemm_nn(min(rchunk, njk - rc + 1), n1, n1, xs(rc), njk, as_, n1, t1s(rc), njk)
  end do
  !$omp end parallel do
  ! mode J: per a, T2(a) (n3 x n2) = T1(a) (n3 x n2) * B^T
  !$omp parallel do private(base) schedule(static)
  do a = 1, n1
    base = 1 + njk * (a - 1)
    call gemm_nn(n3, n2, n2, t1s(base), n3, bs, n2, t2s(base), n3)
  end do
  !$omp end parallel do
  ! mode K: Y (n3 x n1*n2) = C (n3 x n3) * T2 (n3 x n1*n2), column chunks of qchunk
  !$omp parallel do private(base) schedule(static)
  do qc = 1, nab, qchunk
    base = 1 + n3 * (qc - 1)
    call gemm_nn(n3, min(qchunk, nab - qc + 1), n3, cct, n3, t2s(base), n3, ys(base), n3)
  end do
  !$omp end parallel do

  ! checksum: per (a, b) row of n3 cells, rows summed in order, planes summed in order
  !$omp parallel do private(b, c, base, w, sp) reduction(+:cs)
  do a = 1, n1
    sp = 0.0_real64
    do b = 1, n2
      base = n3 * ((a - 1) * n2 + (b - 1))
      w = 0.0_real64
#ifdef BENCH_LICENSED
      !$omp simd reduction(+:w)
#endif
      do c = 1, n3
        w = w + ys(base + c) * ys(base + c)
      end do
      sp = sp + w
    end do
    cs = cs + sp
  end do
  !$omp end parallel do
#endif

  ! input checksum: sum X + sum A + sum B + sum C, the Blade source's grouping -- one fold per
  ! input over its leading mathematical index (X: per plane i, the per-(i, j) row sums in
  ! order; A, B, C: per mathematical row, C's rows read through the transposed cct), each
  ! threaded in mt8 (reduction(+:) on the outer loop); licensed: `simd reduction` on the
  ! innermost row sums (the source's licensed inner folds); repro: none
  sx = 0.0_real64
  !$omp parallel do private(j, k, base, w, sp) reduction(+:sx)
  do i = 1, n1
    sp = 0.0_real64
    do j = 1, n2
      base = n3 * ((i - 1) * n2 + (j - 1))
      w = 0.0_real64
#ifdef BENCH_LICENSED
      !$omp simd reduction(+:w)
#endif
      do k = 1, n3
        w = w + xs(base + k)
      end do
      sp = sp + w
    end do
    sx = sx + sp
  end do
  !$omp end parallel do
  sa = 0.0_real64
  !$omp parallel do private(i, w) reduction(+:sa)
  do a = 1, n1
    w = 0.0_real64
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:w)
#endif
    do i = 1, n1
      w = w + as_(n1 * (a - 1) + i)
    end do
    sa = sa + w
  end do
  !$omp end parallel do
  sb = 0.0_real64
  !$omp parallel do private(j, w) reduction(+:sb)
  do b = 1, n2
    w = 0.0_real64
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:w)
#endif
    do j = 1, n2
      w = w + bs(n2 * (b - 1) + j)
    end do
    sb = sb + w
  end do
  !$omp end parallel do
  sc = 0.0_real64
  !$omp parallel do private(k, w) reduction(+:sc)
  do c = 1, n3
    w = 0.0_real64
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:w)
#endif
    do k = 1, n3
      w = w + cct(c + n3 * (k - 1))
    end do
    sc = sc + w
  end do
  !$omp end parallel do
  ics = sx + sa + sb + sc
  call bench_stop_and_report('tensorop')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('tensorop')
  do q = 1, ncells
    call bench_dump(ys(q))
  end do
  call bench_dump_close()
#endif
#ifndef BENCH_INPUTONLY
  deallocate(t1s, t2s, ys)
#endif
  deallocate(xs, as_, bs, cct)
end program tensorop_expert
