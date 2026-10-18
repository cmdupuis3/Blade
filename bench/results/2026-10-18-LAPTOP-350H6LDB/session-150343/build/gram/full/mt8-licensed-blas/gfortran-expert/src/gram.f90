! gram / expert_blas -- the `blas` regime's expert tier: G = X X^T by OpenBLAS DSYRK
! (upper triangle), the same inputs, checksum and contract as natural.f90 / expert.f90.
! Spec: bench/kernels/gram/spec.md ("blas regime: expert calls dsyrk('U', 'T', ...)").
!
! * X stored xs(n, p) (column i = variable i, samples contiguous), so G = xs^T xs is
!   DSYRK('U', 'T', N = p, K = n, 1, xs, lda = n, 0, g, ldc = p); g(p, p) dense, upper half
!   written by DSYRK (the lower half is never touched);
! * DSYRK is called through a bind(C) interface to OpenBLAS's `dsyrk_` (32-bit integers:
!   OPENBLAS_USE64BITINT is not defined in this install's openblas_config.h; the character
!   arguments are passed by address, OpenBLAS's C entry ignores hidden lengths), which is
!   identical for gfortran and ifx;
! * link: gfortran `-L%OPENBLAS_DIR%\lib -lopenblas`; ifx `%OPENBLAS_DIR%\lib\libopenblas.lib`
!   after the `--` of build_ifx.cmd; run with %OPENBLAS_DIR%\bin (libopenblas.dll) on PATH;
!   OPENBLAS_NUM_THREADS=1 for serial arms, 8 for mt8;
! * checksum read back from the upper triangle: diagonal once, off-diagonal twice; the fill,
!   the checksum and the input sums are `parallel do` in mt8 (the threading of DSYRK itself
!   is OpenBLAS's, set by OPENBLAS_NUM_THREADS).
! * licensed regime (-DBENCH_LICENSED): `!$omp simd reduction(+:coff)` on the checksum's
!   column read-back (review F5); no OpenMP directive around the DSYRK call.
! Builds: -DBENCH_INPUTONLY = inputonly twin; -DBENCH_DUMP = G row-major to gram_dump.txt.
include 'bench_support.f90'

program gram_expert_blas
  use, intrinsic :: iso_fortran_env, only: real64
  use, intrinsic :: iso_c_binding, only: c_int, c_double, c_char
  use bench_support
  implicit none
  integer, parameter :: p = 1601   ! SIZE p
  integer, parameter :: n = 1001   ! SIZE n
  integer, parameter :: seed_x = 10000019
  real(real64), parameter :: lo = 0.0_real64, hi = 1.0_real64
  interface
    subroutine dsyrk(uplo, trans, nn, kk, alpha, a, lda, beta, c, ldc) bind(C, name='dsyrk_')
      import :: c_int, c_double, c_char
      character(kind=c_char), intent(in) :: uplo, trans
      integer(c_int), intent(in) :: nn, kk, lda, ldc
      real(c_double), intent(in) :: alpha, beta
      real(c_double), intent(in) :: a(*)
      real(c_double), intent(inout) :: c(*)
    end subroutine dsyrk
  end interface
  real(real64), allocatable :: xs(:,:), g(:,:), rs(:)
  real(real64) :: cs, ics, cdiag, coff
  integer :: i, j, s

  call bench_start()
  allocate(xs(n, p), g(p, p), rs(p))
  !$omp parallel do private(s)
  do i = 1, p
    do s = 1, n
      xs(s, i) = lcg(lin2(i - 1, s - 1, n), seed_x, lo, hi)
    end do
  end do
  !$omp end parallel do

#ifndef BENCH_INPUTONLY
  call dsyrk('U', 'T', int(p, c_int), int(n, c_int), 1.0_c_double, xs, int(n, c_int), &
             0.0_c_double, g, int(p, c_int))
  cdiag = 0.0_real64
  coff = 0.0_real64
  !$omp parallel do private(i) reduction(+:cdiag, coff)
  do j = 1, p
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:coff)
#endif
    do i = 1, j - 1
      coff = coff + g(i, j) * g(i, j)
    end do
    cdiag = cdiag + g(j, j) * g(j, j)
  end do
  !$omp end parallel do
  cs = cdiag + 2.0_real64 * coff
#else
  cs = 0.0_real64
#endif

  ! input checksum: per-variable sums over s, then the sum over variables (Blade's order)
  !$omp parallel do private(s)
  do i = 1, p
    rs(i) = 0.0_real64
    do s = 1, n
      rs(i) = rs(i) + xs(s, i)
    end do
  end do
  !$omp end parallel do
  ics = 0.0_real64
  do i = 1, p
    ics = ics + rs(i)
  end do
  call bench_stop_and_report('gram')
  call bench_print('checksum', cs)
  call bench_print('input_checksum', ics)
#if defined(BENCH_DUMP) && !defined(BENCH_INPUTONLY)
  call bench_dump_open('gram')
  do i = 1, p
    do j = 1, p
      call bench_dump(g(min(i, j), max(i, j)))
    end do
  end do
  call bench_dump_close()
#endif
  deallocate(xs, g, rs)
end program gram_expert_blas
