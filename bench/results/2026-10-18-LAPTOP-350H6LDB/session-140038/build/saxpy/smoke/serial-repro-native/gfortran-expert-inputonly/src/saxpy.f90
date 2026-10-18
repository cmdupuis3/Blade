! saxpy / expert -- y2 = 1.5 x + y (named output, stored); checksum = sum y2^2;
! input_checksum = sum x + sum y.  Spec: bench/kernels/saxpy/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "saxpy"):
!  * one loop: the store of y2 and the checksum accumulation fused (one pass over x, y, y2);
!    repro: one sequential accumulator (the add chain overlaps the memory streams);
!  * mt8: parallel do reduction(+:cs) on the fused loop; the fill is `parallel do`;
!  * licensed regime (-DBENCH_LICENSED): `parallel do simd reduction(+:cs)` -- the Blade
!    source licenses its checksum fold (`lambda(p, q) where omp -> p + q`).
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = y2 to saxpy_dump.txt after the
! stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program saxpy_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 20011   ! SIZE n
  integer, parameter :: seed_x = 10000019, seed_y = 20000038
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:), y(:), y2(:)
  real(real64) :: cs, ics, sx, sy, v
  integer :: k

  call bench_start()
  allocate(x(n), y(n), y2(n))
  !$omp parallel do
  do k = 1, n
    x(k) = lcg(k - 1, seed_x, lo, hi)
    y(k) = lcg(k - 1, seed_y, lo, hi)
  end do
  !$omp end parallel do

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
#if defined(BENCH_LICENSED) && defined(_OPENMP)
  !$omp parallel do simd private(v) reduction(+:cs)
#elif defined(BENCH_LICENSED)
  !$omp simd private(v) reduction(+:cs)
#else
  !$omp parallel do private(v) reduction(+:cs)
#endif
  do k = 1, n
    v = 1.5_real64 * x(k) + y(k)
    y2(k) = v
    cs = cs + v * v
  end do
#endif

  ! input checksum: (sum x) + (sum y), each ascending in repro; matched SIMD reductions
  ! in licensed (review F3)
  sx = 0.0_real64
  sy = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:sx, sy)
#endif
  do k = 1, n
    sx = sx + x(k)
    sy = sy + y(k)
  end do
  ics = sx + sy
  call bench_stop_and_report('saxpy')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('saxpy')
  do k = 1, n
    call bench_dump(y2(k))
  end do
  call bench_dump_close()
#endif
  deallocate(x, y, y2)
end program saxpy_expert
