! pairaction / natural -- W(i,j) = (1 + a + a^2/3) exp(-a), a = sqrt(5 r2(i,j)) / 0.7 (pairkernel's
! Matern-5/2 matrix of n points in 3-D), R = 3 right-hand sides v_r;
!   z(i) = sum_j W(i,j),   u_r(i) = sum_j W(i,j) v_r(j),   y_r(i) = u_r(i) / z(i);
! checksum = sum W (full logical matrix) + sum z + sum u_1 + sum u_2 + sum u_3 + sum y_1 + sum y_2
! + sum y_3; input_checksum = sum (x + y + z) + sum v_1 + sum v_2 + sum v_3.
! Spec: bench/kernels/pairaction/spec.md.
! Natural tier (dense baseline): W stored DENSE, every cell evaluated (the diagonal included,
! n^2 sqrt+exp -- pairkernel natural's producer), then the straightforward row loops: one pass
! over the dense matrix per output (z, then each RHS), each row's accumulation an ordered scalar
! chain over j = 1..n from 0 (W is symmetric cell for cell -- W(j,i) evaluates the same squared
! differences -- so row i is read as the contiguous column i). y = u / z elementwise.
! mt8: `!$omp parallel do` (default static schedule) on the producer's column loop and on the
! row loops (OUTPUT-OWNED: each thread owns rows of z / u_r, each row's chain on one thread --
! strict); the fill and the input checksum are serial (negligible).
! Builds: -DBENCH_INPUTONLY = inputonly twin (checksum = 0.0); -DBENCH_DUMP = z, u_r, y_r
! (row i: z, u_1, u_2, u_3, y_1, y_2, y_3) to pairaction_dump.txt after the stop line.
! Deallocation after the stop line.
include 'bench_support.f90'

program pairaction_natural
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 2503   ! SIZE n
  integer, parameter :: seed_x = 10000019, seed_y = 20000038, seed_z = 30000057
  integer, parameter :: seed_v1 = 40000076, seed_v2 = 50000095, seed_v3 = 60000114
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:), y(:), z(:), v(:,:), k(:,:), zs(:), u(:,:), yr(:,:)
  real(real64) :: cs, ics, dx, dy, dz, a, acc
  integer :: i, j, r

  call bench_start()
  allocate(x(n), y(n), z(n), v(n, 3), k(n, n), zs(n), u(n, 3), yr(n, 3))
  do i = 1, n
    x(i) = lcg(i - 1, seed_x, lo, hi)
    y(i) = lcg(i - 1, seed_y, lo, hi)
    z(i) = lcg(i - 1, seed_z, lo, hi)
    v(i, 1) = lcg(i - 1, seed_v1, lo, hi)
    v(i, 2) = lcg(i - 1, seed_v2, lo, hi)
    v(i, 3) = lcg(i - 1, seed_v3, lo, hi)
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
  ! z: row i's ordered chain over j (column i: W is symmetric)
  !$omp parallel do private(j, acc)
  do i = 1, n
    acc = 0.0_real64
    do j = 1, n
      acc = acc + k(j, i)
    end do
    zs(i) = acc
  end do
  !$omp end parallel do
  ! u_r, one pass per right-hand side
  do r = 1, 3
    !$omp parallel do private(j, acc)
    do i = 1, n
      acc = 0.0_real64
      do j = 1, n
        acc = acc + k(j, i) * v(j, r)
      end do
      u(i, r) = acc
    end do
    !$omp end parallel do
  end do
  do r = 1, 3
    yr(:, r) = u(:, r) / zs
  end do
  ! every stored element read back, in the Blade source's order of terms
  cs = sum(k)
  cs = cs + sum(zs)
  do r = 1, 3
    cs = cs + sum(u(:, r))
  end do
  do r = 1, 3
    cs = cs + sum(yr(:, r))
  end do
#else
  cs = 0.0_real64
#endif
  ! input checksum in the order of the Blade source: per point (x + y + z), then the sum; then
  ! each right-hand side's sum
  ics = 0.0_real64
  do i = 1, n
    ics = ics + (x(i) + y(i) + z(i))
  end do
  do r = 1, 3
    ics = ics + sum(v(:, r))
  end do
  call bench_stop_and_report('pairaction')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('pairaction')
  do i = 1, n
    call bench_dump(zs(i))
    do r = 1, 3
      call bench_dump(u(i, r))
    end do
    do r = 1, 3
      call bench_dump(yr(i, r))
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(x, y, z, v, k, zs, u, yr)
end program pairaction_natural
