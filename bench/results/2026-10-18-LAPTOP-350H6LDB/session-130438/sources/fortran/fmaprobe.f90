! fmaprobe.f90 -- does the compiler contract a*b - c into an FMA under the given flags?
! a = b = 1 + 2^-27 at RUN time (opaque to constant folding); c = fl(a*b) = 1 + 2^-26.
! Unfused: a*b - c = 0.  Fused: the exact rounding error 2^-54 = 5.55e-17.
! Probes a scalar expression and a vectorizable loop.  Not part of any benchmark arm.
program fmaprobe
  use, intrinsic :: iso_fortran_env, only: real64
  implicit none
  integer, parameter :: n = 1001
  real(real64) :: a, b, r, eps
  real(real64), volatile :: c
  real(real64) :: x(n), y(n), z(n), d(n)
  integer :: i
  eps = 2.0_real64 ** (-27) * real(1 + command_argument_count(), real64)
  a = 1.0_real64 + eps
  b = 1.0_real64 + eps
  c = a * b
  r = a * b - c
  write(*, '(a,es12.4)') 'scalar a*b-c = ', r
  do i = 1, n
    x(i) = a
    y(i) = b
    z(i) = c
  end do
  do i = 1, n
    d(i) = x(i) * y(i) - z(i)
  end do
  write(*, '(a,es12.4)') 'loop   a*b-c = ', maxval(abs(d))
end program fmaprobe
