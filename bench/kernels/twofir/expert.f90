! twofir / expert -- two direct FIR filters over one stored input x(0:n-1):
!   y1(i) = 0.0625 x(i-3) + 0.125 x(i-2) + 0.1875 x(i-1) + 0.25 x(i) + 0.1875 x(i+1)
!           + 0.125 x(i+2) + 0.0625 x(i+3)                        centres i = 3 .. n-4
!   y2(i) = 0.5 x(i) - 0.25 x(i-4) - 0.25 x(i+4) + 0.125 x(i-8) + 0.125 x(i+8)
!                                                                  centres i = 8 .. n-9
! checksum = s1 + s2 (s1 = sum y1^2, s2 = sum y2^2); input_checksum = sum x.
! Spec: bench/kernels/twofir/spec.md.
!
! Expert choices (spec.md "Tiers"):
!  * ONE fused direct loop over y2's domain (centres 8 .. n-9) producing BOTH outputs from the
!    same loads of x(k) .. x(k+16) (one traversal of x instead of two), each output still
!    stored in its own array, and both checksum sums accumulated in the same loop;
!  * y1's ten fringe centres outside y2's domain (3 .. 7 and n-8 .. n-4) are computed by two
!    five-cell loops before and after it, so s1 still accumulates in ascending centre order
!    (head, fused body, tail) -- the same chain as the Blade source's fold in repro;
!  * every cell's arithmetic is the spec's left-to-right expression (no tap reordering);
!  * mt8: `parallel do reduction(+:s1, s2)` on the fused loop (the fringe loops are 5 cells
!    each and stay serial); the fill and the input sum are `parallel do` too;
!  * licensed regime (-DBENCH_LICENSED): `simd reduction(+:s1, s2)` on the fused loop and
!    `simd reduction(+:ics)` on the input sum -- the Blade source licenses all three folds
!    (`lambda(p, q) where omp -> p + q`).  Absent in repro.
!  * A 17-cell register ring (load x once per centre and rotate) is a reviewed ALTERNATIVE,
!    not this arm: the direct form's re-loads are L1 hits, and the fused loop vectorizes.
! Builds: -DBENCH_INPUTONLY = inputonly twin (y1/y2 not allocated, cs stays 0; the fill and
! the input sum are the total's); -DBENCH_DUMP = y1 then y2 to
! twofir_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program twofir_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 2000003   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:), y1(:), y2(:)
  real(real64) :: cs, ics, s1, s2, v1, v2
  integer :: k

  call bench_start()
  allocate(x(n))
#ifndef BENCH_INPUTONLY
  allocate(y1(n - 6), y2(n - 16))
#endif
  !$omp parallel do
  do k = 1, n
    x(k) = lcg(k - 1, seed_x, lo, hi)
  end do
  !$omp end parallel do

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
  s1 = 0.0_real64
  s2 = 0.0_real64
  ! y1 head: y1(1..5) = centres 3..7 (x(k) .. x(k+6), 1-based)
  do k = 1, 5
    v1 = 0.0625_real64 * x(k) + 0.125_real64 * x(k + 1) + 0.1875_real64 * x(k + 2) &
         + 0.25_real64 * x(k + 3) + 0.1875_real64 * x(k + 4) + 0.125_real64 * x(k + 5) &
         + 0.0625_real64 * x(k + 6)
    y1(k) = v1
    s1 = s1 + v1 * v1
  end do
  ! fused body: y2(k) = centre k+7 (x(k) .. x(k+16)); y1(k+5) is the same centre
  ! (x(k+5) .. x(k+11)); k = 1 .. n-16
#if defined(BENCH_LICENSED) && defined(_OPENMP)
  !$omp parallel do simd private(v1, v2) reduction(+:s1, s2)
#elif defined(BENCH_LICENSED)
  !$omp simd private(v1, v2) reduction(+:s1, s2)
#else
  !$omp parallel do private(v1, v2) reduction(+:s1, s2)
#endif
  do k = 1, n - 16
    v1 = 0.0625_real64 * x(k + 5) + 0.125_real64 * x(k + 6) + 0.1875_real64 * x(k + 7) &
         + 0.25_real64 * x(k + 8) + 0.1875_real64 * x(k + 9) + 0.125_real64 * x(k + 10) &
         + 0.0625_real64 * x(k + 11)
    v2 = 0.5_real64 * x(k + 8) - 0.25_real64 * x(k + 4) - 0.25_real64 * x(k + 12) &
         + 0.125_real64 * x(k) + 0.125_real64 * x(k + 16)
    y1(k + 5) = v1
    y2(k) = v2
    s1 = s1 + v1 * v1
    s2 = s2 + v2 * v2
  end do
  ! y1 tail: y1(n-10 .. n-6) = centres n-8 .. n-4
  do k = n - 10, n - 6
    v1 = 0.0625_real64 * x(k) + 0.125_real64 * x(k + 1) + 0.1875_real64 * x(k + 2) &
         + 0.25_real64 * x(k + 3) + 0.1875_real64 * x(k + 4) + 0.125_real64 * x(k + 5) &
         + 0.0625_real64 * x(k + 6)
    y1(k) = v1
    s1 = s1 + v1 * v1
  end do
  cs = s1 + s2
#endif

  ! input checksum: ascending k in repro; matched simd reduction in licensed; threaded in mt8
  ics = 0.0_real64
#if defined(BENCH_LICENSED) && defined(_OPENMP)
  !$omp parallel do simd reduction(+:ics)
#elif defined(BENCH_LICENSED)
  !$omp simd reduction(+:ics)
#else
  !$omp parallel do reduction(+:ics)
#endif
  do k = 1, n
    ics = ics + x(k)
  end do
  call bench_stop_and_report('twofir')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('twofir')
  do k = 1, n - 6
    call bench_dump(y1(k))
  end do
  do k = 1, n - 16
    call bench_dump(y2(k))
  end do
  call bench_dump_close()
#endif
#ifndef BENCH_INPUTONLY
  deallocate(y1, y2)
#endif
  deallocate(x)
end program twofir_expert
