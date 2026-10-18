! stencil / expert -- lap(i,j) = u(i-1,j) + u(i+1,j) + u(i,j-1) + u(i,j+1) - 4 u(i,j) on the
! interior of an ny x nx grid; checksum = sum lap^2; input_checksum = sum u.
! Spec: bench/kernels/stencil/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "stencil"):
!  * orientation f(nx, ny): a mathematical row is contiguous, so the inner loop is unit
!    stride over j for all five reads and the store;
!  * explicit row loop; each output row is produced by a branch-free vectorized loop, then
!    its squares are summed while the row is still in L1 (the checksum fused per row; the
!    stores to lap still happen; one sequential accumulator per row, as the Blade source);
!  * mt8: parallel do over rows, reduction(+:cs) (the kernel loop); the fill and the per-row
!    input sums are `parallel do` over rows too;
!  * licensed regime (-DBENCH_LICENSED): `!$omp simd reduction(+:w)` on the per-row checksum
!    sum -- the Blade source licenses its checksum fold (`where omp -> p + q` over rows).
!    Absent in the repro regime (strictly sequential per-row sums).
! Builds: -DBENCH_INPUTONLY = inputonly twin (cs stays 0); -DBENCH_DUMP = lap row-major to
! stencil_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program stencil_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: ny = 1501   ! SIZE ny
  integer, parameter :: nx = 2003   ! SIZE nx
  integer, parameter :: seed_u = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: f(:,:), lap(:,:), rs(:)
  real(real64) :: cs, ics, w
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

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
  !$omp parallel do private(j, w) reduction(+:cs)
  do i = 1, ny - 2
    do j = 1, nx - 2
      lap(j, i) = f(j + 1, i) + f(j + 1, i + 2) + f(j, i + 1) + f(j + 2, i + 1) &
                  - 4.0_real64 * f(j + 1, i + 1)
    end do
    w = 0.0_real64
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:w)
#endif
    do j = 1, nx - 2
      w = w + lap(j, i) * lap(j, i)
    end do
    cs = cs + w
  end do
  !$omp end parallel do
#endif

  ! input checksum in the order of the Blade source: per row sums, then the sum of rows
  !$omp parallel do private(j, w)
  do i = 1, ny
    w = 0.0_real64
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:w)
#endif
    do j = 1, nx
      w = w + f(j, i)
    end do
    rs(i) = w
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
end program stencil_expert
