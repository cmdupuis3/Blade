! maskagg / expert -- y = (x(k) : x(k) > 1.0, k ascending), m = |y|; checksum = sum y^2 + m;
! input_checksum = sum x.  Spec: bench/kernels/maskagg/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "maskagg"):
!  * ONE pass over x into a pre-sized buffer y(n) (y(1:m) is the output): branch-free
!    compaction -- every element is stored at y(m+1) and m advances by the predicate, so the
!    ~50% unpredictable branch of the plain `if` never mispredicts;
!  * the sum of squares as a second, sequential pass over y(1:m) (half of x, cache-warm in
!    part): measured faster than fusing it branch-free into the compaction pass (that form
!    adds an exact 0.0 for every rejected element, doubling the length of the add chain)
!    and faster than the branchy `if` form;
!  * serial only (spec).  licensed regime (-DBENCH_LICENSED): `!$omp simd reduction` on the
!    output sum of squares and on the input sum (review F2: Blade's licensed emission has
!    8-lane accumulators for both); the compaction stays sequential.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = y(1:m) to maskagg_dump.txt
! after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program maskagg_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 1000003   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 2.0_real64
  real(real64), allocatable :: x(:), y(:)
  real(real64) :: cs, ics, v, acc
  integer :: k, m

  call bench_start()
  allocate(x(n), y(n))
  do k = 1, n
    x(k) = lcg(k - 1, seed_x, lo, hi)
  end do

#ifndef BENCH_INPUTONLY
  m = 0
  acc = 0.0_real64
  do k = 1, n
    v = x(k)
    y(m + 1) = v
    m = m + merge(1, 0, v > 1.0_real64)
  end do
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:acc)
#endif
  do k = 1, m
    acc = acc + y(k) * y(k)
  end do
  cs = acc + real(m, real64)
#else
  cs = 0.0_real64
#endif

  ics = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:ics)
#endif
  do k = 1, n
    ics = ics + x(k)
  end do
  call bench_stop_and_report('maskagg')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('maskagg')
  do k = 1, m
    call bench_dump(y(k))
  end do
  call bench_dump_close()
#endif
  deallocate(x, y)
end program maskagg_expert
