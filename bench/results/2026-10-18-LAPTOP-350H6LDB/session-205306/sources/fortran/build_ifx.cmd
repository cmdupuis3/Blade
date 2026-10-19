@echo off
rem build_ifx.cmd <out.exe> <flags...> -- <sources...>
rem
rem Sets up MSVC (vcvars64) + the Intel compiler env (compiler\latest\env\vars.bat), then runs
rem   ifx /nologo <flags> /fpp /F268435456 /I<this dir> <sources> /Fo:<out.exe>.obj /Fe:<out.exe>
rem and exits with ifx's exit code. Environment-setup chatter goes to stderr only, and so does
rem the exact ifx command line ("+ ifx ..."); ifx's own diagnostics go wherever ifx sends them.
rem
rem * The flags are taken VERBATIM from the raw command line (%*), so flags containing '='
rem   survive (cmd's %1..%9 would split "-fp-contract=fast" at the '='); e.g. the strict
rem   regime's  /O3 /QxHost /fp:precise -mllvm -fp-contract=fast
rem * Separator: the first " -- " on the command line. <out.exe> must be the first argument
rem   (quote it if it has spaces); sources may be quoted. No '!' anywhere (delayed expansion).
rem * The wrapper's own directory (bench\fortran) is added to the include path so a kernel's
rem   "include 'bench_support.f90'" resolves; pass ONLY the kernel source (ONE source: the
rem   object file is written as <out.exe>.obj, beside the exe, not into the cwd).
rem * OpenMP builds (a flag containing "openmp" other than /Qopenmp-simd): the 2026.1 libiomp5md.dll is copied
rem   beside the exe (see the comment at the end).
setlocal EnableExtensions EnableDelayedExpansion
set "BX_HERE=%~dp0."
if "%~1"=="" (
  echo usage: build_ifx.cmd ^<out.exe^> ^<flags...^> -- ^<sources...^> 1>&2
  exit /b 2
)
set "BX_OUT=%~1"
set "BX_ALL= %* "
rem everything after the first " -- "
set "BX_SRCS=!BX_ALL:* -- =!"
if "!BX_SRCS!"=="!BX_ALL!" (
  echo build_ifx: missing the -- separator before the sources 1>&2
  exit /b 2
)
if "!BX_SRCS: =!"=="" (
  echo build_ifx: no sources after -- 1>&2
  exit /b 2
)
rem everything before it: drop " -- <srcs>" from the end, then the leading <out.exe> token
set "BX_PRE=!BX_ALL!"
call set "BX_PRE=%%BX_PRE: -- !BX_SRCS!=%%"
set "BX_FLAGS=!BX_PRE:*%1=!"

rem ---- MSVC linker environment (VS 2022 Community preferred, VS 18 Build Tools fallback).
rem vcvars wants vswhere.exe on PATH (harmless "not recognized" message otherwise).
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
set "BX_VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if not exist "!BX_VCVARS!" set "BX_VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if not exist "!BX_VCVARS!" (
  echo build_ifx: vcvars64.bat not found 1>&2
  exit /b 3
)
if not defined VSCMD_VER call "!BX_VCVARS!" 1>&2
rem ---- Intel compiler environment (it sees VSCMD_VER, so it skips its own VS probing)
set "SETVARS_CALL=1"
call "C:\Program Files (x86)\Intel\oneAPI\compiler\latest\env\vars.bat" 1>&2
where ifx >nul 2>&1
if errorlevel 1 (
  echo build_ifx: ifx not on PATH after environment setup 1>&2
  exit /b 4
)
rem Fixed addition /fpp: every kernel source is preprocessed so the harness can build the
rem per-tier twins from the same file (/DBENCH_INPUTONLY, /DBENCH_DUMP); no codegen change.
rem Fixed addition /F268435456: 256 MB main-thread stack RESERVE (virtual). ifx puts
rem array-expression temporaries on the stack by default (no /heap-arrays); an 8 MB temporary
rem overflows the 1 MB default (measured: forrtl severe 170). Same reserve as the gfortran wrapper.
echo + ifx /nologo !BX_FLAGS! /fpp /F268435456 /I"!BX_HERE!" !BX_SRCS! /Fo:"!BX_OUT!.obj" /Fe:"!BX_OUT!" 1>&2
ifx /nologo !BX_FLAGS! /fpp /F268435456 /I"!BX_HERE!" !BX_SRCS! /Fo:"!BX_OUT!.obj" /Fe:"!BX_OUT!"
set "BX_RC=!ERRORLEVEL!"
if not "!BX_RC!"=="0" exit /b !BX_RC!
rem ---- OpenMP builds import libiomp5md.dll (the Fortran runtime itself is linked statically).
rem Outside the Intel env, the Windows DLL search finds a 2010 libiomp5md.dll in
rem "Common Files\Intel\Shared Files" (on the system PATH) or anaconda's copy. Copy the
rem 2026.1 runtime beside the exe: the application directory is searched first.
rem (/Qopenmp-simd alone imports no runtime: it does not count)
set "BX_NOSIMD=!BX_FLAGS:openmp-simd=!"
if not "!BX_NOSIMD:openmp=!"=="!BX_NOSIMD!" (
  for %%D in ("!BX_OUT!") do copy /y "%CMPLR_ROOT%\bin\libiomp5md.dll" "%%~dpD" >nul
  if errorlevel 1 (
    echo build_ifx: could not copy libiomp5md.dll beside !BX_OUT! 1>&2
    exit /b 5
  )
)
exit /b 0
