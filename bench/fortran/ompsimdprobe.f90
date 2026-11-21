! ompsimdprobe.f90 -- under -fopenmp-simd / /Qopenmp-simd, `!$omp simd` must be honoured and
! `!$omp parallel do` ignored (no threads, no OpenMP runtime import).  The exe must import no
! libgomp-1.dll / libiomp5md.dll; the printed sum must equal the serial one.
! No `!$` conditional-compilation lines on purpose: gfortran -fopenmp-simd COMPILES them
! (measured: `!$ n = omp_get_num_threads()` fails to link), so kernels must not use them.
! Not part of any benchmark arm.
program ompsimdprobe
  use, intrinsic :: iso_fortran_env, only: real64
  implicit none
  integer, parameter :: n = 1001
  real(real64) :: a(n), b(n), s
  integer :: i
  !$omp simd
  do i = 1, n
    a(i) = real(i, real64) * 0.5_real64
  end do
  !$omp parallel do
  do i = 1, n
    b(i) = a(i) * a(i)
  end do
  !$omp end parallel do
  s = 0.0_real64
  do i = 1, n
    s = s + b(i)
  end do
  write(*, '(a,es24.16)') 'sum = ', s
end program ompsimdprobe
