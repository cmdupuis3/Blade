! tempprobe.f90 -- where does each compiler put a large array-expression temporary?
! x = x(n:1:-1) + 1 forces a temporary (overlapping, reversed RHS) of n*8 bytes = 8 MB,
! far beyond the 1-2 MB default Windows stack.  A stack temporary crashes (stack overflow);
! a heap temporary runs.  Not part of any benchmark arm.
program tempprobe
  use, intrinsic :: iso_fortran_env, only: real64
  implicit none
  integer, parameter :: n = 1000003
  real(real64), allocatable :: x(:)
  integer :: i
  allocate(x(n))
  do i = 1, n
    x(i) = real(i, real64)
  end do
  x = x(n:1:-1) + 1.0_real64
  write(*, '(a,2es24.16)') 'ok ', x(1), x(n)
end program tempprobe
