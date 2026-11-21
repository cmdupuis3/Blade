! pairkernel / natural -- K(i,j) = (1 + a + a^2/3) exp(-a), a = sqrt(5 r2(i,j)) / 0.7, for n
! points in 3-D; checksum = sum of K^2 over the full n x n matrix;
! input_checksum = sum x + sum y + sum z.  Spec: bench/kernels/pairkernel/spec.md.
! Natural tier: three coordinate vectors, the full dense n x n double nest in column-major
! order (every cell evaluated, the diagonal included), SUM for the checksum.
! mt8: `!$omp parallel do` (default static schedule) on the outer j loop; the 3n-cell fill is
! serial (negligible).  exp/sqrt: scalar calls in both compilers (no vector math library is
! used: gfortran/mingw has no libmvec; ifx /fp:precise does not vectorize this loop to SVML).
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = K row-major to
! pairkernel_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program pairkernel_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 2503   ! SIZE n
  integer, parameter :: seed_x = 10000019, seed_y = 20000038, seed_z = 30000057
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:), y(:), z(:), k(:,:)
  real(real64) :: cs, ics, dx, dy, dz, a
  integer :: i, j

  call bench_start()
  allocate(x(n), y(n), z(n), k(n, n))
  do i = 1, n
    x(i) = lcg(i - 1, seed_x, lo, hi)
    y(i) = lcg(i - 1, seed_y, lo, hi)
    z(i) = lcg(i - 1, seed_z, lo, hi)
  end do

#ifndef BENCH_INPUTONLY
  !$omp parallel do private(i, dx, dy, dz, a)
  do j = 1, n
    do i = 1, n
      dx = x(i) - x(j)
      dy = y(i) - y(j)
      dz = z(i) - z(j)
      a = sqrt(5.0_real64 * (dx * dx + dy * dy + dz * dz)) / 0.7_real64
      k(i, j) = (1.0_real64 + a + a * a / 3.0_real64) * exp(-a)
    end do
  end do
  !$omp end parallel do
  cs = sum(k * k)
#else
  cs = 0.0_real64
#endif
  ! input checksum in the order of the Blade source: per point (x + y + z), then the sum
  ics = sum(x + y + z)
  call bench_stop_and_report('pairkernel')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('pairkernel')
  do i = 1, n
    do j = 1, n
      call bench_dump(k(i, j))
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(x, y, z, k)
end program pairkernel_natural
