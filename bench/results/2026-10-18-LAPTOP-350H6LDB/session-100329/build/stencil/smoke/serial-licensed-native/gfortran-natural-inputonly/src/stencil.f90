! stencil / natural -- lap(i,j) = u(i-1,j) + u(i+1,j) + u(i,j-1) + u(i,j+1) - 4 u(i,j) on the
! interior of an ny x nx grid; checksum = sum lap^2; input_checksum = sum u.
! Spec: bench/kernels/stencil/spec.md.
! Natural tier: the grid stored f(nx, ny) (f(j+1, i+1) = u(i, j): a mathematical row is a
! contiguous Fortran column), lap by array-section statements (one per row; parallel do over
! rows in mt8), the checksum as a SUM per row (parallel do over rows in mt8); the fill and
! the per-row input sums are `parallel do` over rows too.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = lap row-major to
! stencil_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program stencil_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: ny = 31   ! SIZE ny
  integer, parameter :: nx = 41   ! SIZE nx
  integer, parameter :: seed_u = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: f(:,:), lap(:,:), rs(:)
  real(real64) :: cs, ics
  integer :: i, j

  call bench_start()
  allocate(f(nx, ny), lap(nx - 2, ny - 2), rs(ny))
  !$omp parallel do private(j)
  do i = 1, ny
    do j = 1, nx
      f(j, i) = lcg(lin2(i - 1, j - 1, nx), seed_u, lo, hi)
    end do
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  ! lap(j, i) holds the mathematical interior cell (i, j), i = 1..ny-2, j = 1..nx-2;
  ! terms in the spec's order: u(i-1,j) + u(i+1,j) + u(i,j-1) + u(i,j+1) - 4 u(i,j)
  ! (the whole-grid statement
  !    lap = f(2:nx-1,1:ny-2) + f(2:nx-1,3:ny) + f(1:nx-2,2:ny-1) + f(3:nx,2:ny-1) - 4*f(2:nx-1,2:ny-1)
  !  is split into one array-section statement per row so that mt8 threads it: ifx refuses
  !  to parallelize the whole-grid statement inside PARALLEL WORKSHARE, warning #5474)
  !$omp parallel do
  do i = 1, ny - 2
    lap(:, i) = f(2:nx-1, i) + f(2:nx-1, i+2) + f(1:nx-2, i+1) + f(3:nx, i+1) &
                - 4.0_real64 * f(2:nx-1, i+1)
  end do
  !$omp end parallel do

  cs = 0.0_real64
  !$omp parallel do reduction(+:cs)
  do i = 1, ny - 2
    cs = cs + sum(lap(:, i) * lap(:, i))
  end do
  !$omp end parallel do
#else
  cs = 0.0_real64
#endif

  ! input checksum in the order of the Blade source: per row sums, then the sum of rows
  !$omp parallel do
  do i = 1, ny
    rs(i) = sum(f(:, i))
  end do
  !$omp end parallel do
  ics = 0.0_real64
  do i = 1, ny
    ics = ics + rs(i)
  end do
  call bench_stop_and_report('stencil')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('stencil')
  do i = 1, ny - 2
    do j = 1, nx - 2
      call bench_dump(lap(j, i))
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(f, lap, rs)
end program stencil_natural
