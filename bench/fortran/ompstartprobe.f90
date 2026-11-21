! ompstartprobe.f90 -- where does the OpenMP team start-up land?  Times the FIRST parallel
! region of the process ("cold": includes thread-pool creation, which every kernel's first
! `!$omp parallel do` -- the input fill -- pays INSIDE the timed region) and a second
! identical region ("warm").  Run as many fresh processes under different environments
! (KMP_BLOCKTIME, OMP_WAIT_POLICY, ...).  Built with -fopenmp / /Qopenmp.  Not a benchmark arm.
include 'bench_support.f90'

program ompstartprobe
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 20011
  real(real64), allocatable :: a(:)
  integer :: i, rep
  allocate(a(n))
  do rep = 1, 2
    call bench_start()
    !$omp parallel do
    do i = 1, n
      a(i) = real(i, real64) * 0.5_real64
    end do
    !$omp end parallel do
    if (rep == 1) then
      call bench_stop_and_report('cold')
    else
      call bench_stop_and_report('warm')
    end if
  end do
  if (a(n) < 0.0_real64) print *, a(1)
  deallocate(a)
end program ompstartprobe
