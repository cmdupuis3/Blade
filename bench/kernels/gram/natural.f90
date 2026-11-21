! gram / natural -- G = X X^T, X is p x n, G(i,j) = sum_s X(i,s) X(j,s); checksum = sum G^2.
! Natural tier: the data matrix stored the Fortran way (observations down the columns:
! xs(s, i) = X(i, s)), G by the MATMUL intrinsic, dense p x p storage, SUM for the checksums.
! mt8: only the input fill is threaded -- MATMUL has no user-placeable pragma and neither
! compiler threads it (gfortran calls libgfortran's serial _gfortran_matmul_r8; ifx inlines
! it): "parallel preparation, serial kernel".
! Builds: -DBENCH_INPUTONLY = the inputonly twin (kernel and checksum skipped, checksum = 0);
! -DBENCH_DUMP = G written row-major to gram_dump.txt after the stop line.  Arrays are
! deallocated after the stop line (README section 1.6 / review M21).
include 'bench_support.f90'

program gram_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: p = 1601   ! SIZE p
  integer, parameter :: n = 1001   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: xs(:,:), g(:,:)
  real(real64) :: cs, ics
  integer :: i, s


  call bench_start()
  allocate(xs(n, p), g(p, p))
  !$omp parallel do private(s)
  do i = 1, p
    do s = 1, n
      xs(s, i) = lcg(lin2(i - 1, s - 1, n), seed_x, lo, hi)
    end do
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  g = matmul(transpose(xs), xs)
  cs = sum(g * g)
#else
  cs = 0.0_real64
#endif
  ics = sum(sum(xs, dim=1))
  call bench_stop_and_report('gram')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('gram')
  do i = 1, p
    do s = 1, p
      call bench_dump(g(i, s))
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(xs, g)
end program gram_natural
