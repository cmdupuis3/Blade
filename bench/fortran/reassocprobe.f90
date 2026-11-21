! reassocprobe.f90 -- does the compiler reassociate a sum reduction under the given flags?
! x(1) = 1, x(2:n) = 2^-54 (set at run time).  A strictly sequential left fold gives exactly
! 1.0 (each 1 + 2^-54 rounds back to 1); any reordering (vector lanes, partial sums) adds up
! some of the tiny terms first and prints > 1.  Probes a DO loop and the SUM intrinsic.
! Not part of any benchmark arm.
program reassocprobe
  use, intrinsic :: iso_fortran_env, only: real64
  implicit none
  integer, parameter :: n = 4001
  real(real64) :: x(n), s, tiny
  integer :: i
  tiny = 2.0_real64 ** (-54) * real(1 + command_argument_count(), real64)
  x(1) = 1.0_real64
  do i = 2, n
    x(i) = tiny
  end do
  s = 0.0_real64
  do i = 1, n
    s = s + x(i)
  end do
  write(*, '(a,es24.16)') 'loop sum - 1 = ', s - 1.0_real64
  write(*, '(a,es24.16)') 'SUM  sum - 1 = ', sum(x) - 1.0_real64
end program reassocprobe
