! ompprobe.f90 -- review M20: thread count and placement of each OpenMP runtime.
! Run once per compiler under OMP_NUM_THREADS=8 OMP_PLACES=cores OMP_PROC_BIND=close
! OMP_DYNAMIC=FALSE.  Each thread spins briefly (so the OS has placed it) and reports its
! OpenMP place and the logical processor it runs on (Win32 GetCurrentProcessorNumber).
! Built with -fopenmp / /Qopenmp only (uses the OpenMP runtime API).  Not a benchmark arm.
program ompprobe
  use, intrinsic :: iso_fortran_env, only: real64
  use, intrinsic :: iso_c_binding, only: c_int
  use omp_lib
  implicit none
  interface
    function cur_proc() bind(C, name='GetCurrentProcessorNumber') result(p)
      import :: c_int
      integer(c_int) :: p
    end function cur_proc
  end interface
  integer :: nt, tid, place, cpu, i
  real(real64) :: s
  integer :: cpus(0:255), places(0:255)
  cpus = -1
  places = -1
  nt = 0
  !$omp parallel private(tid, s, i)
  tid = omp_get_thread_num()
  s = 0.0_real64
  do i = 1, 20000000
    s = s + 1.0e-9_real64 * real(i, real64)
  end do
  if (s < 0.0_real64) print *, s
  !$omp single
  nt = omp_get_num_threads()
  !$omp end single
  places(tid) = omp_get_place_num()
  cpus(tid) = cur_proc()
  !$omp end parallel
  write(*, '(a,i0,a,i0,a,l1,a,i0)') 'threads = ', nt, '  num_places = ', omp_get_num_places(), &
      '  dynamic = ', omp_get_dynamic(), '  proc_bind = ', omp_get_proc_bind()
  do tid = 0, nt - 1
    place = places(tid)
    cpu = cpus(tid)
    write(*, '(a,i0,a,i0,a,i0)') 'thread ', tid, '  place ', place, '  logical cpu ', cpu
  end do
end program ompprobe
