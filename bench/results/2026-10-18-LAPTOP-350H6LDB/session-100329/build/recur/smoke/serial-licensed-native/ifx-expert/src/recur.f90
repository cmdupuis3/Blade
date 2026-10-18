! recur / expert -- RK4 (h = 0.01) of du/dt = u - u^3 for nx independent initial values over
! nt - 1 steps; the whole trajectory U(t, x) is stored; checksum = sum U^2;
! input_checksum = sum u0.  Spec: bench/kernels/recur/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "recur"):
!  * u(nx, nt): one step is a contiguous column; per step ONE fused loop over x computes the
!    four stages as scalars (no stage arrays) and stores u(x, t): branch-free, vectorized.
!    Per cell the arithmetic and its order are exactly the spec's;
!  * the checksum is summed per step right after the step is written (the row is L2-hot):
!    one accumulator per step (or per thread-range and step), steps added in order --
!    accumulating straight into one running sum made ifx 2x slower (measured);
!  * tried and dropped (serial): x-blocks outermost with all time steps inside (512 / 2048 /
!    128 values) -- slower with both compilers (strided page-by-page trajectory stores);
!  * mt8 (reviews M5, F4): each thread OWNS a contiguous x-range for every time step inside
!    one parallel region -- the trajectories are independent, so there is no per-step
!    synchronization; per-thread checksum combined once at region exit (reduction(+:cs));
!    `!$omp simd` (no reduction) on the stage loop;
!  * licensed regime (-DBENCH_LICENSED): `!$omp simd reduction(+:w)` on the checksum sums and
!    `simd reduction(+:ics)` on the input sum (review F3); repro keeps every fold in order;
!  * gfortran serial build of this file is the suite's drift-control binary.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = U row-major (t, then x) to
! recur_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program recur_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
#ifdef _OPENMP
  use omp_lib, only: omp_get_thread_num, omp_get_num_threads
#endif
  implicit none
  integer, parameter :: nt = 11     ! SIZE nt
  integer, parameter :: nx = 211   ! SIZE nx
  integer, parameter :: seed_u = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 2.0_real64
  real(real64), allocatable :: u(:,:)
  real(real64) :: cs, ics, v, a1, a2, a3, a4, y, w
  integer :: t, x, xlo, xhi
#ifdef _OPENMP
  integer :: tid, team
#endif

  call bench_start()
  allocate(u(nx, nt))
  do x = 1, nx
    u(x, 1) = lcg(x - 1, seed_u, lo, hi)
  end do

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
#ifdef _OPENMP
  !$omp parallel private(t, x, tid, team, xlo, xhi, v, a1, a2, a3, a4, y, w) reduction(+:cs)
  tid = omp_get_thread_num()
  team = omp_get_num_threads()
  xlo = 1 + (nx * tid) / team
  xhi = (nx * (tid + 1)) / team
#else
  xlo = 1
  xhi = nx
#endif
  w = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:w)
#endif
  do x = xlo, xhi
    w = w + u(x, 1) * u(x, 1)
  end do
  cs = cs + w
  do t = 2, nt
    !$omp simd private(v, a1, a2, a3, a4, y)
    do x = xlo, xhi
      v = u(x, t - 1)
      a1 = v - v * v * v
      y = v + 0.005_real64 * a1
      a2 = y - y * y * y
      y = v + 0.005_real64 * a2
      a3 = y - y * y * y
      y = v + 0.01_real64 * a3
      a4 = y - y * y * y
      u(x, t) = v + (0.01_real64 / 6.0_real64) * (a1 + 2.0_real64 * a2 + 2.0_real64 * a3 + a4)
    end do
    w = 0.0_real64
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:w)
#endif
    do x = xlo, xhi
      w = w + u(x, t) * u(x, t)
    end do
    cs = cs + w
  end do
#ifdef _OPENMP
  !$omp end parallel
#endif
#endif

  ics = 0.0_real64
#ifdef BENCH_LICENSED
  !$omp simd reduction(+:ics)
#endif
  do x = 1, nx
    ics = ics + u(x, 1)
  end do
  call bench_stop_and_report('recur')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('recur')
  do t = 1, nt
    do x = 1, nx
      call bench_dump(u(x, t))
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(u)
end program recur_expert
