! twofir / natural -- two direct FIR filters over one stored input x(0:n-1):
!   y1(i) = 0.0625 x(i-3) + 0.125 x(i-2) + 0.1875 x(i-1) + 0.25 x(i) + 0.1875 x(i+1)
!           + 0.125 x(i+2) + 0.0625 x(i+3)                        centres i = 3 .. n-4
!   y2(i) = 0.5 x(i) - 0.25 x(i-4) - 0.25 x(i+4) + 0.125 x(i-8) + 0.125 x(i+8)
!                                                                  centres i = 8 .. n-9
! checksum = s1 + s2 (s1 = sum y1^2, s2 = sum y2^2); input_checksum = sum x.
! Spec: bench/kernels/twofir/spec.md.
! Natural tier: two separate loops, one per output (x is streamed twice), each output stored
! in its own array; then one sum loop per output.  Storage: y1(m) is the centre m+2 (0-based),
! i.e. x(m) .. x(m+6) in 1-based Fortran; y2(m) is the centre m+7, x(m) .. x(m+16).
! mt8: `parallel do` on every loop (fill, the two FIR loops, the two sums with
! reduction(+:), the input sum with reduction(+:) -- the Blade source licenses all of them).
! Builds: -DBENCH_INPUTONLY = inputonly twin (y1/y2 not allocated, FIRs and sums skipped,
! checksum = 0; the fill and the input sum are the total's);
! -DBENCH_DUMP = y1 then y2 to twofir_dump.txt after the stop line.  Deallocation after the
! stop line.
include 'bench_support.f90'

program twofir_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 2000003   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:), y1(:), y2(:)
  real(real64) :: cs, ics, s1, s2
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

#ifndef BENCH_INPUTONLY
  ! y1: the taps in the spec's order, left to right
  !$omp parallel do
  do k = 1, n - 6
    y1(k) = 0.0625_real64 * x(k) + 0.125_real64 * x(k + 1) + 0.1875_real64 * x(k + 2) &
            + 0.25_real64 * x(k + 3) + 0.1875_real64 * x(k + 4) + 0.125_real64 * x(k + 5) &
            + 0.0625_real64 * x(k + 6)
  end do
  !$omp end parallel do
  ! y2: 0.5 x(i) - 0.25 x(i-4) - 0.25 x(i+4) + 0.125 x(i-8) + 0.125 x(i+8), left to right
  !$omp parallel do
  do k = 1, n - 16
    y2(k) = 0.5_real64 * x(k + 8) - 0.25_real64 * x(k + 4) - 0.25_real64 * x(k + 12) &
            + 0.125_real64 * x(k) + 0.125_real64 * x(k + 16)
  end do
  !$omp end parallel do
  s1 = 0.0_real64
  !$omp parallel do reduction(+:s1)
  do k = 1, n - 6
    s1 = s1 + y1(k) * y1(k)
  end do
  !$omp end parallel do
  s2 = 0.0_real64
  !$omp parallel do reduction(+:s2)
  do k = 1, n - 16
    s2 = s2 + y2(k) * y2(k)
  end do
  !$omp end parallel do
  cs = s1 + s2
#else
  cs = 0.0_real64
#endif

  ! input checksum: ascending k (the order of the Blade source's fold); threaded in mt8
  ics = 0.0_real64
  !$omp parallel do reduction(+:ics)
  do k = 1, n
    ics = ics + x(k)
  end do
  !$omp end parallel do
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
end program twofir_natural
