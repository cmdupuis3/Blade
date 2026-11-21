! recur / natural -- RK4 (h = 0.01) of du/dt = u - u^3 for nx independent initial values over
! nt - 1 steps; the whole trajectory U(t, x) is stored; checksum = sum U^2;
! input_checksum = sum u0.  Spec: bench/kernels/recur/spec.md.
! Natural tier: u(nx, nt) (x contiguous within a step); inside `do t` each RK4 stage is its
! own pass over x into a stage array k1..k4 (the array statements
!   k1 = f(u(:,t-1)); k2 = f(u(:,t-1) + 0.005*k1); k3 = ...; k4 = ...; u(:,t) = ...
! written as one loop each so that mt8 threads every pass: ifx does not parallelize such
! statements inside WORKSHARE, see fortran-notes.md), then the checksum per step.
! mt8 (review M5): one parallel region around the time loop, `!$omp do` (static) on every
! stage pass (the implicit barrier orders the stages); each trajectory's arithmetic is
! unchanged.  The checksum is a `parallel do reduction` over steps of per-step sums.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = U row-major (t, then x) to
! recur_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program recur_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: nt = 301     ! SIZE nt
  integer, parameter :: nx = 20011   ! SIZE nx
  integer, parameter :: seed_u = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 2.0_real64
  real(real64), allocatable :: u(:,:), k1(:), k2(:), k3(:), k4(:)
  real(real64) :: cs, ics
  integer :: t, x

  call bench_start()
  allocate(u(nx, nt), k1(nx), k2(nx), k3(nx), k4(nx))
  do x = 1, nx
    u(x, 1) = lcg(x - 1, seed_u, lo, hi)
  end do

#ifndef BENCH_INPUTONLY
  !$omp parallel private(t)
  do t = 2, nt
    !$omp do
    do x = 1, nx
      k1(x) = f(u(x, t - 1))
    end do
    !$omp do
    do x = 1, nx
      k2(x) = f(u(x, t - 1) + 0.005_real64 * k1(x))
    end do
    !$omp do
    do x = 1, nx
      k3(x) = f(u(x, t - 1) + 0.005_real64 * k2(x))
    end do
    !$omp do
    do x = 1, nx
      k4(x) = f(u(x, t - 1) + 0.01_real64 * k3(x))
    end do
    !$omp do
    do x = 1, nx
      u(x, t) = u(x, t - 1) + (0.01_real64 / 6.0_real64) * &
                (k1(x) + 2.0_real64 * k2(x) + 2.0_real64 * k3(x) + k4(x))
    end do
  end do
  !$omp end parallel

  cs = 0.0_real64
  !$omp parallel do reduction(+:cs)
  do t = 1, nt
    cs = cs + sum(u(:, t) * u(:, t))
  end do
  !$omp end parallel do
#else
  cs = 0.0_real64
#endif
  ics = 0.0_real64
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
  deallocate(u, k1, k2, k3, k4)

contains

  pure elemental function f(v) result(r)
    real(real64), intent(in) :: v
    real(real64) :: r
    r = v - v * v * v
  end function f

end program recur_natural
