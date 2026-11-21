! bench_support.f90 -- shared support for every Fortran arm of the Blade-vs-Fortran suite.
!
! HOW TO USE: kernel sources INCLUDE this file (they do not compile it separately):
!
!     include 'bench_support.f90'
!     program gram_natural
!       use bench_support
!       ...
!
! so the module lands in the kernel's own translation unit, and gfortran -O3 / ifx /O3 inline
! lcg() into the fill loops (a separately compiled module procedure is an opaque call per
! element without LTO/IPO, which would bill the Fortran arms for a call Blade never makes).
! The build wrappers add this directory to the include path; pass ONLY the kernel source.
! (Including does NOT suppress the module file: the compiler still writes bench_support.mod
! into the build's working directory -- build-directory isolation, not inclusion, is what
! keeps parallel builds from colliding on it.  Review F14.)
!
! Contract (bench/notes/charter.md section 5):
!   * lcg(k, seed, lo, hi): the suite's input generator, a pure function of the 0-based
!     row-major linear index k of the MATHEMATICAL array (README section 5: affine round,
!     quadratic mixing round, affine round, middle-square round, affine round, all mod 2^31;
!     seed = 10000019 * j for the j-th input).  Exact in integer(int64): (k + seed) < 2^31,
!     every h < 2^31, h1 div 4096 + 1 <= 2^19, so every product < 2^62.
!   * bench_start() / bench_stop_and_report(name): monotonic wall clock (Win32
!     QueryPerformanceCounter, integer(8) count and rate -- see the note at bench_start for
!     why not system_clock); prints "<name> completed in <t>s".
!   * bench_print(label, value): "<label> = <value>", 17 significant digits.
!   Nothing else in this module writes to stdout.
module bench_support
  use, intrinsic :: iso_fortran_env, only: int32, int64, real64, output_unit
  use, intrinsic :: iso_c_binding, only: c_int, c_long_long
  implicit none
  private
  public :: lcg, lin2, lin3, bench_start, bench_stop_and_report, bench_print
  public :: bench_dump_open, bench_dump, bench_dump_close

  integer(int64), parameter :: lcg_a = 1103515245_int64
  integer(int64), parameter :: lcg_c = 12345_int64
  ! mod 2^31 of a NONNEGATIVE value == iand with 2^31 - 1 (all operands here are >= 0).
  integer(int64), parameter :: lcg_mask = 2147483647_int64
  real(real64),   parameter :: inv_2_31 = 1.0_real64 / 2147483648.0_real64   ! exact (2^-31)

  integer(int64), save :: bench_t0 = 0_int64
  integer, save :: bench_dump_unit = -1

  interface lcg
    module procedure lcg_k8, lcg_k4
  end interface lcg

  interface lin2
    module procedure lin2_8, lin2_4
  end interface lin2

  interface lin3
    module procedure lin3_8, lin3_4
  end interface lin3

  interface
    function qpc(cnt) bind(C, name='QueryPerformanceCounter') result(ok)
      import :: c_int, c_long_long
      integer(c_long_long), intent(out) :: cnt
      integer(c_int) :: ok
    end function qpc
    function qpf(freq) bind(C, name='QueryPerformanceFrequency') result(ok)
      import :: c_int, c_long_long
      integer(c_long_long), intent(out) :: freq
      integer(c_int) :: ok
    end function qpf
  end interface

contains

  ! value = lo + (hi - lo) * u,  u = h2 / 2^31 in [0, 1).
  ! Bit-identity across languages needs (hi - lo) * u to be EXACT (true whenever hi - lo has
  ! <= 22 significant bits, e.g. 1, 2, 0.5, 3): then FMA contraction of the final multiply-add
  ! cannot change the result in any lane.  See bench/notes/fortran-notes.md.
  pure elemental function lcg_k8(k, seed, lo, hi) result(v)
    integer(int64), intent(in) :: k
    integer,        intent(in) :: seed
    real(real64),   intent(in) :: lo, hi
    real(real64) :: v
    integer(int64) :: h1, h2, h3, h4, h5
    h1 = iand((k + int(seed, int64)) * lcg_a + lcg_c, lcg_mask)
    ! quadratic mixing round: h1 * (h1 div 4096 + 1); h1 >= 0 so div 4096 == shift right 12
    h2 = iand(h1 * (ishft(h1, -12) + 1_int64), lcg_mask)
    h3 = iand(h2 * lcg_a + lcg_c, lcg_mask)
    ! middle-square round: (h3 * h3 div 65536) mod 2^31  (h3 * h3 < 2^62)
    h4 = iand(ishft(h3 * h3, -16), lcg_mask)
    h5 = iand(h4 * lcg_a + lcg_c, lcg_mask)
    v = lo + (hi - lo) * (real(h5, real64) * inv_2_31)
  end function lcg_k8

  pure elemental function lcg_k4(k, seed, lo, hi) result(v)
    integer(int32), intent(in) :: k
    integer,        intent(in) :: seed
    real(real64),   intent(in) :: lo, hi
    real(real64) :: v
    v = lcg_k8(int(k, int64), seed, lo, hi)
  end function lcg_k4

  ! Row-major linear index of the MATHEMATICAL element (i0, j0) of an n0 x n1 array, all
  ! 0-based: k = i0*n1 + j0 (Blade's Array<T like I, J>: J contiguous).  Independent of how
  ! the Fortran stores the array.
  pure elemental function lin2_8(i0, j0, n1) result(k)
    integer(int64), intent(in) :: i0, j0, n1
    integer(int64) :: k
    k = i0 * n1 + j0
  end function lin2_8

  pure elemental function lin2_4(i0, j0, n1) result(k)
    integer(int32), intent(in) :: i0, j0, n1
    integer(int64) :: k
    k = int(i0, int64) * int(n1, int64) + int(j0, int64)
  end function lin2_4

  ! Row-major linear index of (i0, j0, l0) in an n0 x n1 x n2 array, 0-based.
  pure elemental function lin3_8(i0, j0, l0, n1, n2) result(k)
    integer(int64), intent(in) :: i0, j0, l0, n1, n2
    integer(int64) :: k
    k = (i0 * n1 + j0) * n2 + l0
  end function lin3_8

  pure elemental function lin3_4(i0, j0, l0, n1, n2) result(k)
    integer(int32), intent(in) :: i0, j0, l0, n1, n2
    integer(int64) :: k
    k = (int(i0, int64) * int(n1, int64) + int(j0, int64)) * int(n2, int64) + int(l0, int64)
  end function lin3_4

  ! The clock is the Win32 QueryPerformanceCounter (monotonic, 10 MHz on this host) -- the
  ! source std::chrono's clocks use on Windows (Blade's timer).  NOT system_clock: ifx 2026.1's
  ! integer(8) system_clock reports count_rate = 1e6 but only ticks every 1000 counts (1 ms
  ! granularity), measured; gfortran's is 1e7 with 1-count granularity.  One clock for every
  ! arm.  See bench/notes/fortran-notes.md.
  subroutine bench_start()
    integer(c_long_long) :: c
    integer(c_int) :: ok
    ok = qpc(c)
    bench_t0 = int(c, int64)
  end subroutine bench_start

  subroutine bench_stop_and_report(name)
    character(len=*), intent(in) :: name
    integer(c_long_long) :: c, rate
    integer(c_int) :: ok
    real(real64) :: t
    character(len=40) :: buf
    ok = qpc(c)
    ok = qpf(rate)
    t = real(int(c, int64) - bench_t0, real64) / real(rate, real64)
    write(buf, '(es24.16e3)') t
    write(output_unit, '(a)') trim(name) // ' completed in ' // trim(adjustl(buf)) // 's'
  end subroutine bench_stop_and_report

  ! ---- validation dumps (built only with -DBENCH_DUMP; never in a timed arm) -------------
  ! bench_dump_open(name) creates <name>_dump.txt in the working directory; bench_dump(v)
  ! appends one value per line (17 significant digits); the kernel writes its named output
  ! in ROW-MAJOR LOGICAL order (every logical cell, symmetric/antisymmetric ones expanded),
  ! after the stop line, so the dump is never inside the timed region.
  subroutine bench_dump_open(name)
    character(len=*), intent(in) :: name
    open(newunit=bench_dump_unit, file=trim(name) // '_dump.txt', status='replace', &
         action='write', form='formatted')
  end subroutine bench_dump_open

  subroutine bench_dump(value)
    real(real64), intent(in) :: value
    character(len=40) :: buf
    write(buf, '(es24.16e3)') value
    write(bench_dump_unit, '(a)') trim(adjustl(buf))
  end subroutine bench_dump

  subroutine bench_dump_close()
    close(bench_dump_unit)
  end subroutine bench_dump_close

  subroutine bench_print(label, value)
    character(len=*), intent(in) :: label
    real(real64),     intent(in) :: value
    character(len=40) :: buf
    write(buf, '(es24.16e3)') value
    write(output_unit, '(a)') trim(label) // ' = ' // trim(adjustl(buf))
  end subroutine bench_print

end module bench_support
