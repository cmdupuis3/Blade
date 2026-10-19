! pairkernel / expert -- K(i,j) = (1 + a + a^2/3) exp(-a), a = sqrt(5 r2(i,j)) / 0.7, for n
! points in 3-D; checksum = sum of K^2 over the full n x n matrix;
! input_checksum = sum x + sum y + sum z.  Spec: bench/kernels/pairkernel/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "pairkernel"):
!  * upper triangle only, packed by columns: K(i,j) at kp(j*(j-1)/2 + i), i <= j;
!  * the diagonal is stored as the spec's exact value 1 without evaluating the kernel
!    (sqrt(0) = 0, exp(-0) = 1, so the formula gives exactly 1.0 too);
!  * coordinates as three contiguous vectors (SoA), so the off-diagonal column loop
!    (i = 1..j-1) is a unit-stride, branch-free loop the compilers vectorize; the per-cell
!    operation order is exactly the spec's;
!  * the checksum is fused per column (each column re-read from L1 right after it is written;
!    a reduction inside the producing loop would block its vectorization in strict mode);
!  * mt8: schedule(dynamic) over columns, largest first (the kernel loop, checksum fused);
!    the 3n-cell fill and the input sum are serial (negligible);
!  * licensed regime (-DBENCH_LICENSED): `!$omp simd reduction(+:w)` on the per-column
!    checksum sum (blade-A: Blade's checksum fold is reassociation-licensed there);
!  * exp/sqrt are scalar calls in both compilers (gfortran: mingw libm `exp`, no libmvec;
!    ifx /fp:precise: scalar `exp`, the loop is not vectorized to SVML even with
!    `!$omp simd` -- tried, no change, not used).
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = K row-major to
! pairkernel_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program pairkernel_expert
  use, intrinsic :: iso_fortran_env, only: int64, real64
  use bench_support
  implicit none
  integer, parameter :: n = 2503   ! SIZE n
  integer, parameter :: seed_x = 10000019, seed_y = 20000038, seed_z = 30000057
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  integer(int64), parameter :: ncell = int(n, int64) * int(n + 1, int64) / 2
  real(real64), allocatable :: x(:), y(:), z(:), kp(:)
  real(real64) :: cs, ics, dx, dy, dz, a, w, xj, yj, zj
  integer :: i, j
  integer(int64) :: jo

  call bench_start()
  allocate(x(n), y(n), z(n), kp(ncell))
  do i = 1, n
    x(i) = lcg(i - 1, seed_x, lo, hi)
    y(i) = lcg(i - 1, seed_y, lo, hi)
    z(i) = lcg(i - 1, seed_z, lo, hi)
  end do

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
  !$omp parallel do schedule(dynamic) private(jo, xj, yj, zj, i, dx, dy, dz, a, w) reduction(+:cs)
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
      kp(jo + i) = (1.0_real64 + a + a * a / 3.0_real64) * exp(-a)
    end do
    kp(jo + j) = 1.0_real64
    w = 0.0_real64
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:w)
#endif
    do i = 1, j - 1
      w = w + kp(jo + i) * kp(jo + i)
    end do
    cs = cs + (2.0_real64 * w + kp(jo + j) * kp(jo + j))
  end do
  !$omp end parallel do
#endif

  ! input checksum in the order of the Blade source: per point (x + y + z), then the sum
  ics = 0.0_real64
  do i = 1, n
    ics = ics + (x(i) + y(i) + z(i))
  end do
  call bench_stop_and_report('pairkernel')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  ! K row-major over the full logical matrix, read through the packed upper triangle
  call bench_dump_open('pairkernel')
  do i = 1, n
    do j = 1, n
      jo = int(max(i, j), int64) * int(max(i, j) - 1, int64) / 2 + min(i, j)
      call bench_dump(kp(jo))
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(x, y, z, kp)
end program pairkernel_expert
