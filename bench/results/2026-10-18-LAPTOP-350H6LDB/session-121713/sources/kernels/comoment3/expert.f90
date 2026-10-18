! comoment3 / expert -- T(i,j,k) = (1/n) sum_s X(i,s) X(j,s) X(k,s) for X (p x n);
! checksum = sum of T^2 over the full p^3 cube; input_checksum = sum of X.
! Spec: bench/kernels/comoment3/spec.md.
!
! Expert choices (bench/notes/fortran-notes.md, "comoment3"):
!  * canonical cells only, i <= j <= k, packed simplex ordered by k, then j, then i:
!    T(i,j,k) at tp((k-1)k(k+1)/6 + (j-1)j/2 + i)  (1-based) -- i contiguous;
!  * for each middle index j the canonical slab {i <= j} x {k >= j} is one rectangular
!    product: for each MR-wide i panel the weighted panel W(i, s) = X(i,s) * X(j,s) is formed
!    once and multiplied against raw X(k, s) panels (k >= j) by the gram MR x NR micro-kernel,
!    vectorized over i.  Every cell is ONE sequential FMA chain over ascending s with the
!    CANONICAL grouping (X(i,s) * X(j,s)) * X(k,s), i <= j <= k -- the Blade cell's
!    `a * b * c` exactly (review F1: the earlier X(i,s) * (X(j,s) X(k,s)) regrouping differed
!    by one ulp at some cells, e.g. (0,0,6));
!  * one division by n per cell after the sum (as the Blade source: prodsum(...) / n);
!  * checksum read back from the simplex with multiplicities 1 / 3 / 6;
!  * mt8: schedule(dynamic) over j (the kernel loop); each thread owns its X(k) panel buffer
!    and W panel; the fill, the packing, the checksum (dynamic over k) and the per-row input
!    sums are `parallel do` too.  `!$omp simd` (no reduction clause) on the micro-kernel's i
!    loop and on the W formation;
!  * licensed regime (-DBENCH_LICENSED): `!$omp simd reduction(+:w)` on the checksum's inner
!    read-back sum (review F5; Blade's checksum fold is licensed);
!  * input checksum: MR adjacent variables summed together (each row still ascending in s,
!    the final sum over rows serial): review F6.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = T row-major (all p^3 logical
! cells) to comoment3_dump.txt after the stop line.  The retained program arrays are
! deallocated after the stop line; each thread's private panel buffers are freed inside the
! parallel region, i.e. inside the timed region (eight small frees; review F13, recorded).
include 'bench_support.f90'

program comoment3_expert
  use, intrinsic :: iso_fortran_env, only: real64
  use bench_support
  implicit none
  integer, parameter :: p = 171   ! SIZE p
  integer, parameter :: n = 201   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  integer, parameter :: mr = 8, nr = 6
  integer, parameter :: npa = (p + mr - 1) / mr, npbmax = (p + nr - 1) / nr
  integer, parameter :: ncell = p * (p + 1) * (p + 2) / 6

  real(real64), allocatable :: x(:,:), ap(:,:,:), bp(:,:,:), tp(:), rs(:)
  real(real64) :: c(mr, nr), wp(mr, n), sums(mr), cs, ics, w, rn
  integer :: i, j, k, s, ip, kp, ii, kk, i0, k0, nip, nkp, m, off

  call bench_start()
  allocate(x(p, n), ap(mr, n, npa), tp(ncell), rs(p))

  ! ---- input: x(i, s) = X(i-1, s-1); mathematical row-major index k = (i-1)*n + (s-1)
  !$omp parallel do private(i)
  do s = 1, n
    do i = 1, p
      x(i, s) = lcg(lin2(i - 1, s - 1, n), seed_x, lo, hi)
    end do
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  ! ---- MR-wide row panels of X, zero padded past p
  !$omp parallel do private(s, ii, i)
  do ip = 1, npa
    do s = 1, n
      do ii = 1, mr
        i = (ip - 1) * mr + ii
        if (i <= p) then
          ap(ii, s, ip) = x(i, s)
        else
          ap(ii, s, ip) = 0.0_real64
        end if
      end do
    end do
  end do
  !$omp end parallel do

  ! ---- the simplex, one slab per middle index j
  rn = real(n, real64)
  !$omp parallel private(bp, j, nkp, nip, kp, k0, s, kk, k, ip, i0, ii, c, wp, m, off)
  allocate(bp(nr, n, npbmax))
  !$omp do schedule(dynamic)
  do j = 1, p
    ! raw X(k, s) panels for k >= j: bp(kk, s, kp) = X(k, s), k = j + (kp-1)*nr + kk - 1 <= p
    nkp = (p - j) / nr + 1
    do kp = 1, nkp
      k0 = j + (kp - 1) * nr - 1
      do s = 1, n
        do kk = 1, nr
          k = k0 + kk
          if (k <= p) then
            bp(kk, s, kp) = x(k, s)
          else
            bp(kk, s, kp) = 0.0_real64
          end if
        end do
      end do
    end do
    nip = (j - 1) / mr + 1
    do ip = 1, nip
      i0 = (ip - 1) * mr
      ! W(i, s) = X(i, s) * X(j, s): the canonical first product, formed once per i panel
      do s = 1, n
        !$omp simd
        do ii = 1, mr
          wp(ii, s) = ap(ii, s, ip) * x(j, s)
        end do
      end do
      do kp = 1, nkp
        k0 = j + (kp - 1) * nr - 1
        c = 0.0_real64
        call tile(wp, bp(1, 1, kp), c)
        m = min(mr, j - i0)
        do kk = 1, nr
          k = k0 + kk
          if (k > p) exit
          off = (k - 1) * k * (k + 1) / 6 + (j - 1) * j / 2 + i0
          tp(off + 1:off + m) = c(1:m, kk) / rn
        end do
      end do
    end do
  end do
  !$omp end do
  deallocate(bp)
  !$omp end parallel

  ! ---- checksum over the full cube: multiplicity 6 (i<j<k), 3 (two equal), 1 (i=j=k)
  cs = 0.0_real64
  !$omp parallel do schedule(dynamic) private(j, i, off, w) reduction(+:cs)
  do k = 1, p
    do j = 1, k
      off = (k - 1) * k * (k + 1) / 6 + (j - 1) * j / 2
      w = 0.0_real64
#ifdef BENCH_LICENSED
      !$omp simd reduction(+:w)
#endif
      do i = 1, j - 1
        w = w + tp(off + i) * tp(off + i)
      end do
      if (j < k) then
        cs = cs + 6.0_real64 * w + 3.0_real64 * tp(off + j) * tp(off + j)
      else
        cs = cs + 3.0_real64 * w + tp(off + j) * tp(off + j)
      end if
    end do
  end do
  !$omp end parallel do

#else
  cs = 0.0_real64
#endif

  ! ---- input checksum: per-variable sums over s (MR adjacent variables together, each still
  ! ascending in s), then the sum over variables (serial: the same order in every build)
  !$omp parallel do private(i0, m, s, ii, sums)
  do ip = 1, npa
    i0 = (ip - 1) * mr
    m = min(mr, p - i0)
    sums = 0.0_real64
    do s = 1, n
      !$omp simd
      do ii = 1, m
        sums(ii) = sums(ii) + x(i0 + ii, s)
      end do
    end do
    rs(i0 + 1:i0 + m) = sums(1:m)
  end do
  !$omp end parallel do
  ics = 0.0_real64
  do i = 1, p
    ics = ics + rs(i)
  end do

  call bench_stop_and_report('comoment3')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  ! T row-major over the full logical cube, read through the simplex (sorted index triple)
  call bench_dump_open('comoment3')
  do i = 1, p
    do j = 1, p
      do k = 1, p
        ip = min(i, j, k)
        kp = max(i, j, k)
        m = i + j + k - ip - kp
        off = (kp - 1) * kp * (kp + 1) / 6 + (m - 1) * m / 2 + ip
        call bench_dump(tp(off))
      end do
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(x, ap, tp, rs)

contains

  ! c(ii, kk) <- c(ii, kk) + sum_{s=1..n} a(ii, s) * b(kk, s): each cell a sequential FMA
  ! chain over s; the MR x NR accumulators stay in registers (vectorized over ii).
  subroutine tile(a, b, c)
    real(real64), intent(in) :: a(mr, n), b(nr, n)
    real(real64), intent(inout) :: c(mr, nr)
    real(real64) :: acc(mr, nr), bs
    integer :: s, ii, kk
    acc = c
    do s = 1, n
      do kk = 1, nr
        bs = b(kk, s)
        !$omp simd
        do ii = 1, mr
          acc(ii, kk) = acc(ii, kk) + a(ii, s) * bs
        end do
      end do
    end do
    c = acc
  end subroutine tile

end program comoment3_expert
