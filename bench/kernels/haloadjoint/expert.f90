! haloadjoint / expert -- a 1-D weighted nonlinear halo stencil and its reverse action:
!   F_c(x) = x(c-1) x(c+1) + sin(x(c)) x(c+2) - (0.5 x(c-2)) x(c-2)      centres c = 2 .. n-3
!   y(c)   = F_c(x)                                   (stored, n - 4 cells)
!   dx     = J^T w, by the spec's EXPLICIT ORDERED UPDATE LIST          (stored, n cells)
! checksum = sum y^2 + sum dx^2;  input_checksum = sum x + sum w.
! Spec: bench/kernels/haloadjoint/spec.md ("The derivative reference").
!
! Expert choices (spec.md "Tiers"):
!  * ONE fused DESTINATION-OWNED pass over j: each cell's y(j) and dx(j) are computed together
!    from the same loads, dx(j) as one register chain +0, U1, U2, U3, U4, U5, U6 -- the list's
!    order and rounding points, so every cell is the list's value (no scatter, no read-modify-
!    write passes over dx) -- and both checksum sums are accumulated in the same pass;
!  * each transcendental APPROXIMATELY once per cell: the body runs in blocks of nb cells; per
!    block a strip sn(-2 .. len-1) = sin(x(jb-2 .. je)) (two cells of overlap) and
!    cn(0 .. len-1) = cos(x(jb .. je)) is formed first (independent loops, vectorizable where
!    the compiler has a vector libm), then the fused loop reads sin(x(j)) for y(j) and
!    sin(x(j-2)) for U1 from the strip.  Calls: sin n + 2B (the overlaps, and the peeled
!    boundary's own), cos n - 4, B = ceil((n-8)/nb) blocks -- 2024 at smoke, 100136 at full;
!  * the 8 boundary cells (j = 0 .. 3, n-4 .. n-1: the only ones where a window is missing) are
!    peeled: dxcell(j) applies the list with its guards; y(2), y(3), y(n-4), y(n-3) likewise;
!    the sums still run in ascending j (head, blocks, tail);
!  * every expression parenthesized as the spec writes it; s * w(c) written w(c) (s = 1.0);
!  * mt8: `parallel do reduction(+:sy, sd)` over the blocks (strips private), the fills and
!    the input sums `parallel do` too;
!  * licensed (-DBENCH_LICENSED): `simd reduction(+:sy, sd)` on the fused loop and
!    `simd reduction(+:..)` on the input sums -- the Blade source licenses those four folds
!    (`lambda(p, q) where omp -> p + q`); the strips get plain `simd` in every regime (no
!    reduction). Absent in repro.
! Builds: -DBENCH_INPUTONLY = inputonly twin (y / dx not allocated, cs stays 0; the fills and
! the input sums are the total's); -DBENCH_DUMP = y(2 .. n-3) then dx(0 .. n-1) to
! haloadjoint_dump.txt after the stop line.  Deallocation after the stop line.
include 'bench_support.f90'

program haloadjoint_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: n = 1000003   ! SIZE n
  integer, parameter :: nb = 1024
  integer, parameter :: seed_x = 10000019, seed_w = 20000038
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  real(real64), allocatable :: x(:), w(:), y(:), dx(:)
  real(real64) :: cs, ics, sx, sw, sy, sd, yv, a
  real(real64) :: sn(-2:nb - 1), cn(0:nb - 1)
  integer :: j, jb, je, k, m

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

  cs = 0.0_real64
#ifndef BENCH_INPUTONLY
  sy = 0.0_real64
  sd = 0.0_real64
  ! head: dx(0 .. 3), y(2), y(3), in ascending j
  do j = 0, 3
    if (j >= 2) then
      yv = ((x(j - 1) * x(j + 1)) + (sin(x(j)) * x(j + 2))) - ((0.5_real64 * x(j - 2)) * x(j - 2))
      y(j) = yv
      sy = sy + yv * yv
    end if
    a = dxcell(j)
    dx(j) = a
    sd = sd + a * a
  end do
  ! body: j = 4 .. n-5 (every window present), blocks of nb cells
#if defined(_OPENMP)
  !$omp parallel do private(jb, je, m, k, sn, cn) reduction(+:sy, sd)
#endif
  do jb = 4, n - 5, nb
    je = min(jb + nb - 1, n - 5)
    m = je - jb
    !$omp simd
    do k = -2, m
      sn(k) = sin(x(jb + k))
    end do
    !$omp simd
    do k = 0, m
      cn(k) = cos(x(jb + k))
    end do
    ! the cell: y(j) and dx(j) as single expressions (dx's chain parenthesized in the list's
    ! order: +0, U1 .. U6), the sums reading the stored cells back in the same iteration.
    ! No scalar temporaries, so the licensed simd loop needs no private clause (gfortran
    ! 16.2 warns -Waggressive-loop-optimizations on a privatized simd reduction loop here).
#if defined(BENCH_LICENSED)
    !$omp simd reduction(+:sy, sd)
#endif
    do k = 0, m
      y(jb + k) = ((x(jb + k - 1) * x(jb + k + 1)) + (sn(k) * x(jb + k + 2))) &
                  - ((0.5_real64 * x(jb + k - 2)) * x(jb + k - 2))
      dx(jb + k) = (((((0.0_real64 + w(jb + k - 2) * sn(k - 2)) &
                       + w(jb + k - 1) * x(jb + k - 2)) &
                      + (w(jb + k) * x(jb + k + 2)) * cn(k)) &
                     + w(jb + k + 1) * x(jb + k + 2)) &
                    + ((-w(jb + k + 2)) * x(jb + k)) * 0.5_real64) &
                   + (-w(jb + k + 2)) * (0.5_real64 * x(jb + k))
      sy = sy + y(jb + k) * y(jb + k)
      sd = sd + dx(jb + k) * dx(jb + k)
    end do
  end do
  ! tail: dx(n-4 .. n-1), y(n-4), y(n-3), in ascending j
  do j = n - 4, n - 1
    if (j <= n - 3) then
      yv = ((x(j - 1) * x(j + 1)) + (sin(x(j)) * x(j + 2))) - ((0.5_real64 * x(j - 2)) * x(j - 2))
      y(j) = yv
      sy = sy + yv * yv
    end if
    a = dxcell(j)
    dx(j) = a
    sd = sd + a * a
  end do
  cs = sy + sd
#endif

  ! input checksum: sum x + sum w, ascending in repro; matched simd reduction in licensed;
  ! threaded in mt8
  sx = 0.0_real64
  sw = 0.0_real64
#if defined(BENCH_LICENSED) && defined(_OPENMP)
  !$omp parallel do simd reduction(+:sx, sw)
#elif defined(BENCH_LICENSED)
  !$omp simd reduction(+:sx, sw)
#else
  !$omp parallel do reduction(+:sx, sw)
#endif
  do j = 0, n - 1
    sx = sx + x(j)
    sw = sw + w(j)
  end do
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

contains

  ! dx(j) by the update list with its window guards (the boundary cells only)
  function dxcell(j) result(acc)
    integer, intent(in) :: j
    real(real64) :: acc
    acc = 0.0_real64
    if (j >= 4) acc = acc + w(j - 2) * sin(x(j - 2))
    if (j >= 3 .and. j <= n - 2) acc = acc + w(j - 1) * x(j - 2)
    if (j >= 2 .and. j <= n - 3) acc = acc + (w(j) * x(j + 2)) * cos(x(j))
    if (j >= 1 .and. j <= n - 4) acc = acc + w(j + 1) * x(j + 2)
    if (j <= n - 5) acc = acc + ((-w(j + 2)) * x(j)) * 0.5_real64
    if (j <= n - 5) acc = acc + (-w(j + 2)) * (0.5_real64 * x(j))
  end function dxcell

end program haloadjoint_expert
