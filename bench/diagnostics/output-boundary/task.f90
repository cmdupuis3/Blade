! B07 diagnostic-only task object. Compile separately from driver.c, never LTO.
! Natural/expert arithmetic mirrors frozen B04; no timer or formatting here.
include 'bench_support.f90'
module b07_fortran_task
  use, intrinsic :: iso_c_binding
  use bench_support, only: lcg
  implicit none
  integer, parameter :: n = 2053
  type, bind(C) :: result_t
    integer(c_int64_t) :: abi_version, n
    type(c_ptr) :: x, y, p, q
    real(c_double) :: checksum, input_checksum
    integer(c_int32_t) :: status, ownership
  end type
  real(c_double), allocatable, target, save :: x(:), y(:), p(:), q(:)
  logical, save :: owned = .false.
  interface
    function can_begin(r) bind(C, name='b07_can_begin') result(ok)
      import result_t, c_int
      type(result_t), intent(in) :: r
      integer(c_int) :: ok
    end function
    function validate_owned(r) bind(C, name='b07_validate_owned') result(ok)
      import result_t, c_int
      type(result_t), intent(in) :: r
      integer(c_int) :: ok
    end function
    subroutine publish(r, xp, yp, pp, qp, count, cs, ics) bind(C, name='b07_publish')
      import result_t, c_ptr, c_int64_t, c_double
      type(result_t) :: r
      type(c_ptr), value :: xp, yp, pp, qp
      integer(c_int64_t), value :: count
      real(c_double), value :: cs, ics
    end subroutine
    subroutine mark_released(r) bind(C, name='b07_mark_released')
      import result_t
      type(result_t) :: r
    end subroutine
#ifdef B07_PREPARED_MUTATION
    subroutine mutate_prepared(xp, yp, count) bind(C, name='b07_mutate_prepared')
      import c_ptr, c_int64_t
      type(c_ptr), value :: xp, yp
      integer(c_int64_t), value :: count
    end subroutine
#endif
  end interface
contains
  function task_size() bind(C, name='b07_task_size') result(bytes)
    integer(c_int64_t) :: bytes
    type(result_t) :: r
    bytes = int(c_sizeof(r), c_int64_t)
  end function

  function task(r) bind(C, name='b07_pm52_task') result(status)
    type(result_t) :: r
    integer(c_int) :: status
    integer :: k, allocation_status
    real(c_double) :: cs, sx, sy, ics, v, w
    status = 1
    if (can_begin(r) == 0 .or. owned) return
    allocate(x(n), y(n), p(n), q(n), stat=allocation_status)
    if (allocation_status /= 0) then
      if (allocated(x)) deallocate(x)
      if (allocated(y)) deallocate(y)
      if (allocated(p)) deallocate(p)
      if (allocated(q)) deallocate(q)
      status = 2
      return
    end if
    do k = 1, n
      x(k) = lcg(k - 1, 10000019, 0.0_c_double, 1.0_c_double)
      y(k) = lcg(k - 1, 20000038, 0.0_c_double, 1.0_c_double)
    end do
#ifdef B07_PREPARED_MUTATION
    ! Distinct TESTING artifact only; canonical source graph has no callback.
    call mutate_prepared(c_loc(x(1)), c_loc(y(1)), int(n, c_int64_t))
#endif
    cs = 0.0_c_double
#ifdef B07_EXPERT
#ifdef BENCH_LICENSED
    !$omp simd private(v, w) reduction(+:cs)
#endif
    do k = 1, n
      v = 1.5_c_double * x(k) + y(k)
      p(k) = v
      w = v * v
      q(k) = w
      cs = cs + w
    end do
#else
    p = 1.5_c_double * x + y
    q = p * p
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:cs)
#endif
    do k = 1, n
      cs = cs + q(k)
    end do
#endif
    sx = 0.0_c_double
    sy = 0.0_c_double
#ifdef BENCH_LICENSED
    !$omp simd reduction(+:sx, sy)
#endif
    do k = 1, n
      sx = sx + x(k)
      sy = sy + y(k)
    end do
    ics = sx + sy
    call publish(r, c_loc(x(1)), c_loc(y(1)), c_loc(p(1)), c_loc(q(1)), int(n, c_int64_t), cs, ics)
    owned = .true.
    status = 0
  end function

  function cleanup(r) bind(C, name='b07_pm52_cleanup') result(status)
    type(result_t) :: r
    integer(c_int) :: status
    status = 1
    if (validate_owned(r) == 0 .or. .not. owned) return
    if (.not. allocated(x) .or. .not. allocated(y) .or. .not. allocated(p) .or. .not. allocated(q)) return
    if (.not. c_associated(r%x, c_loc(x(1))) .or. .not. c_associated(r%y, c_loc(y(1)))) return
    if (.not. c_associated(r%p, c_loc(p(1))) .or. .not. c_associated(r%q, c_loc(q(1)))) return
    deallocate(x, y, p, q)
    owned = .false.
    call mark_released(r)
    status = 0
  end function
end module
