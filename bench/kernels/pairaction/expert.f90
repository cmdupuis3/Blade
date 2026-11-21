! pairaction / expert -- W(i,j) = (1 + a + a^2/3) exp(-a), a = sqrt(5 r2(i,j)) / 0.7 (pairkernel's
! Matern-5/2 matrix of n points in 3-D), R = 3 right-hand sides v_r;
!   z(i) = sum_j W(i,j),   u_r(i) = sum_j W(i,j) v_r(j),   y_r(i) = u_r(i) / z(i);
! checksum = sum W (full logical matrix) + sum z + sum u_1 + sum u_2 + sum u_3 + sum y_1 + sum y_2
! + sum y_3; input_checksum = sum (x + y + z) + sum v_1 + sum v_2 + sum v_3.
! Spec: bench/kernels/pairaction/spec.md.
!
! Expert choices:
!  * W packed by columns, upper triangle: W(i,j) at wp(j*(j-1)/2 + i), i <= j -- pairkernel
!    expert's producer (SoA coordinates, the spec's per-cell operation order, the diagonal
!    stored as the spec's exact value 1.0);
!  * SERIAL (no -fopenmp): ONE fused column sweep updates ALL FOUR channels (z, u_1..u_3) --
!    the dspmv-style symmetric packed action, every channel at once. Column j, rows i < j
!    ascending: cell w = W(i,j) gives row i its term at partner j (z(i) += w, u_r(i) +=
!    w * v_r(j)) and row j its term at partner i (zj += w, u_rj += w * v_r(i)), then the
!    diagonal. This is STRICT: row chain j has received nothing before column j (earlier
!    columns touch rows < their own index only), takes its partners 1..j ascending inside
!    column j, then partners j+1..n from the later columns in ascending column order -- every
!    chain gets its terms in ascending partner order from 0, the spec's chain (the same per-cell
!    products as every arm). The four column chains (zj, u_rj) are scalar reductions the
!    compiler keeps in order; under BENCH_LICENSED they carry `!$omp simd reduction` (the
!    source's `(+)` fold licence -- the matched permission);
!  * mt8 (-fopenmp): the fused sweep's row updates are shared across columns, so it has no
!    strict thread form (a shared-update canonical loop with atomics or private partial merges
!    is NOT strict and not a fair repro twin). The mt8 form is OUTPUT-OWNED: each thread owns
!    rows i (schedule(dynamic)) and gathers row i's canonical cells in ascending j -- partners
!    j < i from packed column i (contiguous), j >= i across the columns (strided) -- into its
!    own chains, all four channels per row; the producer is pairkernel expert's mt8 form
!    (schedule(dynamic) over columns, largest first);
!  * y_r = u_r / z elementwise; the checksum reads every stored element (W through its packed
!    cells with weight 2 off the diagonal);
!  * exp/sqrt are scalar libm calls in both compilers (pairkernel's note).
! Builds: -DBENCH_INPUTONLY = inputonly twin (checksum = 0.0); -DBENCH_DUMP = z, u_r, y_r
! (row i: z, u_1, u_2, u_3, y_1, y_2, y_3) to pairaction_dump.txt after the stop line.
! Deallocation after the stop line.
include 'bench_support.f90'

program pairaction_expert
  use, intrinsic :: iso_fortran_env, only: int64, real64
  use bench_support
  implicit none
  integer, parameter :: n = 2503   ! SIZE n
  integer, parameter :: seed_x = 10000019, seed_y = 20000038, seed_z = 30000057
  integer, parameter :: seed_v1 = 40000076, seed_v2 = 50000095, seed_v3 = 60000114
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  integer(int64), parameter :: ncell = int(n, int64) * int(n + 1, int64) / 2
  real(real64), allocatable :: x(:), y(:), z(:), v1(:), v2(:), v3(:), wp(:)
  real(real64), allocatable :: zs(:), u1(:), u2(:), u3(:), y1(:), y2(:), y3(:)
  real(real64) :: cs, ics, dx, dy, dz, a, xj, yj, zj, w, s, d
  real(real64) :: zc, c1, c2, c3, p1, p2, p3
  integer :: i, j
  integer(int64) :: jo, io

  call bench_start()
  allocate(x(n), y(n), z(n), v1(n), v2(n), v3(n), wp(ncell))
  allocate(zs(n), u1(n), u2(n), u3(n), y1(n), y2(n), y3(n))
  do i = 1, n
    x(i) = lcg(i - 1, seed_x, lo, hi)
    y(i) = lcg(i - 1, seed_y, lo, hi)
    z(i) = lcg(i - 1, seed_z, lo, hi)
    v1(i) = lcg(i - 1, seed_v1, lo, hi)
    v2(i) = lcg(i - 1, seed_v2, lo, hi)
    v3(i) = lcg(i - 1, seed_v3, lo, hi)
  end do

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
  ! the producer: packed upper triangle by columns (pairkernel expert's)
  !$omp parallel do schedule(dynamic) private(jo, xj, yj, zj, i, dx, dy, dz, a)
  do j = n, 1, -1
    jo = int(j, int64) * int(j - 1, int64) / 2
    xj = x(j)
    yj = y(j)
    zj = z(j)
    do i = 1, j - 1
      dx = x(i) - xj
      dy = y(i) - yj
      dz = z(i) - zj
      a = sqrt(5.0_real64 * (dx * dx + dy * dy + dz * dz)) / 0.7_real64
      wp(jo + i) = (1.0_real64 + a + a * a / 3.0_real64) * exp(-a)
    end do
    wp(jo + j) = 1.0_real64
  end do
  !$omp end parallel do
#ifndef _OPENMP
  ! SERIAL: the fused four-channel column sweep (strict; see the header)
  zs = 0.0_real64
  u1 = 0.0_real64
  u2 = 0.0_real64
  u3 = 0.0_real64
  do j = 1, n
    jo = int(j, int64) * int(j - 1, int64) / 2
    zc = zs(j)
    c1 = u1(j)
    c2 = u2(j)
    c3 = u3(j)
    p1 = v1(j)
    p2 = v2(j)
    p3 = v3(j)
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:zc, c1, c2, c3)
#endif
    do i = 1, j - 1
      w = wp(jo + i)
      zs(i) = zs(i) + w
      u1(i) = u1(i) + w * p1
      u2(i) = u2(i) + w * p2
      u3(i) = u3(i) + w * p3
      zc = zc + w
      c1 = c1 + w * v1(i)
      c2 = c2 + w * v2(i)
      c3 = c3 + w * v3(i)
    end do
    w = wp(jo + j)
    zs(j) = zc + w
    u1(j) = c1 + w * p1
    u2(j) = c2 + w * p2
    u3(j) = c3 + w * p3
  end do
#else
  ! mt8: OUTPUT-OWNED rows, each row's four chains on one thread, partners ascending
  !$omp parallel do schedule(dynamic) private(io, jo, j, w, zc, c1, c2, c3)
  do i = 1, n
    io = int(i, int64) * int(i - 1, int64) / 2
    zc = 0.0_real64
    c1 = 0.0_real64
    c2 = 0.0_real64
    c3 = 0.0_real64
    ! partners j < i: packed column i, contiguous
    do j = 1, i - 1
      w = wp(io + j)
      zc = zc + w
      c1 = c1 + w * v1(j)
      c2 = c2 + w * v2(j)
      c3 = c3 + w * v3(j)
    end do
    ! partners j >= i: row i across the columns
    do j = i, n
      jo = int(j, int64) * int(j - 1, int64) / 2
      w = wp(jo + i)
      zc = zc + w
      c1 = c1 + w * v1(j)
      c2 = c2 + w * v2(j)
      c3 = c3 + w * v3(j)
    end do
    zs(i) = zc
    u1(i) = c1
    u2(i) = c2
    u3(i) = c3
  end do
  !$omp end parallel do
#endif
  y1 = u1 / zs
  y2 = u2 / zs
  y3 = u3 / zs
  ! every stored element read back, in the Blade source's order of terms; W's logical sum
  ! from its packed cells (weight 2 off the diagonal)
  s = 0.0_real64
  d = 0.0_real64
  do j = 1, n
    jo = int(j, int64) * int(j - 1, int64) / 2
    do i = 1, j - 1
      s = s + wp(jo + i)
    end do
    d = d + wp(jo + j)
  end do
  cs = (2.0_real64 * s + d) + sum(zs)
  cs = cs + sum(u1)
  cs = cs + sum(u2)
  cs = cs + sum(u3)
  cs = cs + sum(y1)
  cs = cs + sum(y2)
  cs = cs + sum(y3)
#endif
  ! input checksum in the order of the Blade source: per point (x + y + z), then the sum; then
  ! each right-hand side's sum
  ics = 0.0_real64
  do i = 1, n
    ics = ics + (x(i) + y(i) + z(i))
  end do
  ics = ics + sum(v1)
  ics = ics + sum(v2)
  ics = ics + sum(v3)
  call bench_stop_and_report('pairaction')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('pairaction')
  do i = 1, n
    call bench_dump(zs(i))
    call bench_dump(u1(i))
    call bench_dump(u2(i))
    call bench_dump(u3(i))
    call bench_dump(y1(i))
    call bench_dump(y2(i))
    call bench_dump(y3(i))
  end do
  call bench_dump_close()
#endif
  deallocate(x, y, z, v1, v2, v3, wp, zs, u1, u2, u3, y1, y2, y3)
end program pairaction_expert
