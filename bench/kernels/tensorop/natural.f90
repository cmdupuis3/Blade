! tensorop / natural -- Y = (A (x) B (x) C) X as three ordered mode contractions:
!   T1(a, j, k) = sum_i A(a, i) X(i, j, k);  T2(a, b, k) = sum_j B(b, j) T1(a, j, k);
!   Y(a, b, c)  = sum_k C(c, k) T2(a, b, k);
! checksum = sum Y^2; input_checksum = sum X + sum A + sum B + sum C.
! Spec: bench/kernels/tensorop/spec.md.
! Natural tier: three textbook loop nests in the specified order (I, then J, then K), each
! over the OUTPUT cells in row-major order with the contraction innermost and one scalar
! accumulator per cell (s = 0; s = s + U(r, t) * T(.., t, ..), t ascending), dense storage,
! distinct arrays for T1 and T2.  Storage: every array is the mathematical one with its
! index order reversed (xf(k, j, i) = X(i, j, k), af(i, a) = A(a, i), bf(j, b) = B(b, j),
! cf(k, c) = C(c, k), t1(k, j, a), t2(k, b, a), y(c, b, a)), so the last mathematical index
! is the contiguous one, as in the Blade source.  mt8: `parallel do` on the outermost loop
! of every nest (fills, the three contractions, the checksum with reduction(+:), and the
! four input-sum folds over their leading index with reduction(+:)); the checksum is SUM per
! output plane.
! Builds: -DBENCH_INPUTONLY = inputonly twin (contractions and checksum skipped, checksum =
! 0); -DBENCH_DUMP = Y row-major (a, b, c) to tensorop_dump.txt after the stop line.
! Deallocation after the stop line.
include 'bench_support.f90'

program tensorop_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n1 = 89    ! SIZE n1
  integer, parameter :: n2 = 97    ! SIZE n2
  integer, parameter :: n3 = 101   ! SIZE n3
  integer, parameter :: seed_x = 10000019, seed_a = 20000038, seed_b = 30000057, seed_c = 40000076
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: xf(:,:,:), af(:,:), bf(:,:), cf(:,:), t1(:,:,:), t2(:,:,:), y(:,:,:)
  real(real64) :: cs, ics, s, sx, sa, sb, sc
  integer :: i, j, k, a, b, c

  call bench_start()
  allocate(xf(n3, n2, n1), af(n1, n1), bf(n2, n2), cf(n3, n3))
#ifndef BENCH_INPUTONLY
  allocate(t1(n3, n2, n1), t2(n3, n2, n1), y(n3, n2, n1))
#endif
  !$omp parallel do private(j, k)
  do i = 1, n1
    do j = 1, n2
      do k = 1, n3
        xf(k, j, i) = lcg(lin3(i - 1, j - 1, k - 1, n2, n3), seed_x, lo, hi)
      end do
    end do
  end do
  !$omp end parallel do
  !$omp parallel do private(i)
  do a = 1, n1
    do i = 1, n1
      af(i, a) = lcg(lin2(a - 1, i - 1, n1), seed_a, lo, hi)
    end do
  end do
  !$omp end parallel do
  !$omp parallel do private(j)
  do b = 1, n2
    do j = 1, n2
      bf(j, b) = lcg(lin2(b - 1, j - 1, n2), seed_b, lo, hi)
    end do
  end do
  !$omp end parallel do
  !$omp parallel do private(k)
  do c = 1, n3
    do k = 1, n3
      cf(k, c) = lcg(lin2(c - 1, k - 1, n3), seed_c, lo, hi)
    end do
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  ! T1(a, j, k) = sum_i A(a, i) X(i, j, k)
  !$omp parallel do private(j, k, i, s)
  do a = 1, n1
    do j = 1, n2
      do k = 1, n3
        s = 0.0_real64
        do i = 1, n1
          s = s + af(i, a) * xf(k, j, i)
        end do
        t1(k, j, a) = s
      end do
    end do
  end do
  !$omp end parallel do
  ! T2(a, b, k) = sum_j B(b, j) T1(a, j, k)
  !$omp parallel do private(b, k, j, s)
  do a = 1, n1
    do b = 1, n2
      do k = 1, n3
        s = 0.0_real64
        do j = 1, n2
          s = s + bf(j, b) * t1(k, j, a)
        end do
        t2(k, b, a) = s
      end do
    end do
  end do
  !$omp end parallel do
  ! Y(a, b, c) = sum_k C(c, k) T2(a, b, k)
  !$omp parallel do private(b, c, k, s)
  do a = 1, n1
    do b = 1, n2
      do c = 1, n3
        s = 0.0_real64
        do k = 1, n3
          s = s + cf(k, c) * t2(k, b, a)
        end do
        y(c, b, a) = s
      end do
    end do
  end do
  !$omp end parallel do

  cs = 0.0_real64
  !$omp parallel do reduction(+:cs)
  do a = 1, n1
    cs = cs + sum(y(:, :, a) * y(:, :, a))
  end do
  !$omp end parallel do
#else
  cs = 0.0_real64
#endif

  ! input checksum: the grouping of the Blade source -- one fold per input over its leading
  ! (mathematical row / plane) index, each threaded in mt8, combined sum X + sum A + sum B +
  ! sum C; ordinary SUMs within a plane / row (af(:, a) is row a of A, cf(:, c) row c of C)
  sx = 0.0_real64
  !$omp parallel do reduction(+:sx)
  do i = 1, n1
    sx = sx + sum(xf(:, :, i))
  end do
  !$omp end parallel do
  sa = 0.0_real64
  !$omp parallel do reduction(+:sa)
  do a = 1, n1
    sa = sa + sum(af(:, a))
  end do
  !$omp end parallel do
  sb = 0.0_real64
  !$omp parallel do reduction(+:sb)
  do b = 1, n2
    sb = sb + sum(bf(:, b))
  end do
  !$omp end parallel do
  sc = 0.0_real64
  !$omp parallel do reduction(+:sc)
  do c = 1, n3
    sc = sc + sum(cf(:, c))
  end do
  !$omp end parallel do
  ics = sx + sa + sb + sc
  call bench_stop_and_report('tensorop')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('tensorop')
  do a = 1, n1
    do b = 1, n2
      do c = 1, n3
        call bench_dump(y(c, b, a))
      end do
    end do
  end do
  call bench_dump_close()
#endif
#ifndef BENCH_INPUTONLY
  deallocate(t1, t2, y)
#endif
  deallocate(xf, af, bf, cf)
end program tensorop_natural
