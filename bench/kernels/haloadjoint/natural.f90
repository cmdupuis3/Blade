! haloadjoint / natural -- a 1-D weighted nonlinear halo stencil and its reverse action:
!   F_c(x) = x(c-1) x(c+1) + sin(x(c)) x(c+2) - (0.5 x(c-2)) x(c-2)      centres c = 2 .. n-3
!   y(c)   = F_c(x)                                   (stored, n - 4 cells)
!   dx     = J^T w, by the spec's EXPLICIT ORDERED UPDATE LIST          (stored, n cells)
! checksum = sum y^2 + sum dx^2;  input_checksum = sum x + sum w.
! Spec: bench/kernels/haloadjoint/spec.md ("The derivative reference").
!
! Natural tier: the literal transcription of the spec -- one loop for the primal, dx = +0,
! then ONE LOOP PER UPDATE of the list, in list order, each over its destination range
! (U1 offset +2, U2 +1, U3 0, U4 -1, U5 -2 left occurrence, U6 -2 right occurrence); every
! cell therefore receives its contributions in the list's order, each its own rounded add.
! Each expression is parenthesized exactly as the spec writes it (Fortran honours
! parentheses).  Arrays are 0-based (x(0:n-1)), so the indices are the spec's.
! The cotangent factor s of the reverse sweep is 1.0 (the loss's sum seed), so s * w(c) is
! written w(c) and -(s * w(c)) is -w(c): the same bits.
! mt8: `parallel do` on every loop (the fills, the primal, the zero fill, the six update
! loops -- destination-indexed, so race-free --, the two sums and the input sums with
! reduction(+:)).
! (The textbook hand adjoint, a SCATTER over centres ascending -- dx(c+o) += term_o(c) for
! o = +2, +1, 0, -1, -2, -2 -- gives each cell the same order, but cannot be threaded with a
! plain parallel do; spec.md records it as the alternative.)
! Builds: -DBENCH_INPUTONLY = inputonly twin (y / dx not allocated, primal, adjoint and sums
! skipped, checksum = 0; the fills and the input sums are the total's);
! -DBENCH_DUMP = y(2 .. n-3) then dx(0 .. n-1) to haloadjoint_dump.txt after the stop line.
! Deallocation after the stop line.
include 'bench_support.f90'

program haloadjoint_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 1000003   ! SIZE n
  integer, parameter :: seed_x = 10000019, seed_w = 20000038
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:), w(:), y(:), dx(:)
  real(real64) :: cs, ics, sx, sw, sy, sd
  integer :: j

  call bench_start()
  allocate(x(0:n - 1), w(0:n - 1))
#ifndef BENCH_INPUTONLY
  allocate(y(2:n - 3), dx(0:n - 1))
#endif
  !$omp parallel do
  do j = 0, n - 1
    x(j) = lcg(j, seed_x, lo, hi)
  end do
  !$omp end parallel do
  !$omp parallel do
  do j = 0, n - 1
    w(j) = lcg(j, seed_w, lo, hi)
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  ! the primal, centres 2 .. n-3
  !$omp parallel do
  do j = 2, n - 3
    y(j) = ((x(j - 1) * x(j + 1)) + (sin(x(j)) * x(j + 2))) - ((0.5_real64 * x(j - 2)) * x(j - 2))
  end do
  !$omp end parallel do
  ! dx = +0, then the update list in order (window centre c = j - offset, present iff
  ! 2 <= c <= n-3)
  !$omp parallel do
  do j = 0, n - 1
    dx(j) = 0.0_real64
  end do
  !$omp end parallel do
  ! U1, offset +2 (c = j-2): w(c) * sin(x(c))
  !$omp parallel do
  do j = 4, n - 1
    dx(j) = dx(j) + w(j - 2) * sin(x(j - 2))
  end do
  !$omp end parallel do
  ! U2, offset +1 (c = j-1): w(c) * x(c-1)
  !$omp parallel do
  do j = 3, n - 2
    dx(j) = dx(j) + w(j - 1) * x(j - 2)
  end do
  !$omp end parallel do
  ! U3, offset 0 (c = j): (w(c) * x(c+2)) * cos(x(c))
  !$omp parallel do
  do j = 2, n - 3
    dx(j) = dx(j) + (w(j) * x(j + 2)) * cos(x(j))
  end do
  !$omp end parallel do
  ! U4, offset -1 (c = j+1): w(c) * x(c+1)
  !$omp parallel do
  do j = 1, n - 4
    dx(j) = dx(j) + w(j + 1) * x(j + 2)
  end do
  !$omp end parallel do
  ! U5, offset -2, left occurrence of (0.5 x(c-2)) x(c-2) (c = j+2): ((-w(c)) * x(c-2)) * 0.5
  !$omp parallel do
  do j = 0, n - 5
    dx(j) = dx(j) + ((-w(j + 2)) * x(j)) * 0.5_real64
  end do
  !$omp end parallel do
  ! U6, offset -2, right occurrence (c = j+2): (-w(c)) * (0.5 * x(c-2))
  !$omp parallel do
  do j = 0, n - 5
    dx(j) = dx(j) + (-w(j + 2)) * (0.5_real64 * x(j))
  end do
  !$omp end parallel do
  ! the checksum: two +0-seeded sums, ascending, then one add
  sy = 0.0_real64
  !$omp parallel do reduction(+:sy)
  do j = 2, n - 3
    sy = sy + y(j) * y(j)
  end do
  !$omp end parallel do
  sd = 0.0_real64
  !$omp parallel do reduction(+:sd)
  do j = 0, n - 1
    sd = sd + dx(j) * dx(j)
  end do
  !$omp end parallel do
  cs = sy + sd
#else
  cs = 0.0_real64
#endif

  ! input checksum: sum x + sum w, each ascending (the Blade source's two folds); threaded in mt8
  sx = 0.0_real64
  !$omp parallel do reduction(+:sx)
  do j = 0, n - 1
    sx = sx + x(j)
  end do
  !$omp end parallel do
  sw = 0.0_real64
  !$omp parallel do reduction(+:sw)
  do j = 0, n - 1
    sw = sw + w(j)
  end do
  !$omp end parallel do
  ics = sx + sw
  call bench_stop_and_report('haloadjoint')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('haloadjoint')
  do j = 2, n - 3
    call bench_dump(y(j))
  end do
  do j = 0, n - 1
    call bench_dump(dx(j))
  end do
  call bench_dump_close()
#endif
#ifndef BENCH_INPUTONLY
  deallocate(y, dx)
#endif
  deallocate(x, w)
end program haloadjoint_natural
