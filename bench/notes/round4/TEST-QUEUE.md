# Round-4 serialized validation queue

Owner: one `gpt-6-luna` / high runner. Orchestrator dispatches each job.
No build/test/emit/run/benchmark by a coding agent. No full-suite launch before round closure.

| Job | Frozen source identity | Requested checks | State | Result / skips |
|---|---|---|---|---|
| Setup | master `361504b6` | Read-only toolchain and test-alias inventory; no baseline full suite | Queued | |
| B02 | Frozen `b-measurement` at `3bec8a80`; six handoff pins verified | 11 PM54 fixtures, 10 memory fixtures, native diagnostics at 65,539 and 1,000,003 bytes | Integrated / targeted-tested | 21/21 Python fixtures; both native diagnostics validated 3/3 arms with releases successful. No performance or allocator-lifetime claims. See [B02-tests.md](B02-tests.md).
| B01 | Frozen `b-measurement` handoff; run.py SHA256 `B413D5F038F3C144810A79B0E0CA82C281E3E1EA60E553D5E4E45A625F099E3A` | Ten memory-report unit fixtures, then saxpy check-mode report plumbing | Integrated / targeted-tested | 10/10 Python; 24/24 smoke arms; 2/2 control arms; independent smoke/full/control pins passed. Release build passed (2 NU1900 warnings). 20,011-cell dump plus both checksums passed in all 10 Blade/Fortran policy combinations; A01 store/lifetime audit remains deferred. See [B01-tests.md](B01-tests.md). |
| A01 | Frozen `a-lifetimes`; latest 29-file manifest `24b64af9…7e6960` | Release build, then named LLVM/lifetime and remaining targeted blocks | Repair required; paused at first failure | Rebuild passed (4 NU1900 warnings). First category `llvm facts` failed 117/1/0 on genuine tuple-return LLVM ABI; no internal skips. Earlier results apply only to prior manifest. See A01-tests.md. |
| D01 | Frozen `d-streaming`; four unchanged hashes plus corrected ZarrTests.fs `A85ED13D…7FA33C6` verified at base `361504b6` | Release build, standalone Zarr route/refusal/provenance tests | Integrated / targeted-tested | Corrected Release build passed (4 NU1900 warnings); `test zarr` passed 389, failed 0, skipped 1 (NetCDF unavailable). Initial syntax failure retained in separate log. See D01-tests.md. |
| D02 | Frozen `d-streaming` at `9b36f1d1`; five handoff hashes verified; source `ec75ae38` integrated `e43d14d3` | Release build, standalone Zarr resident-destination route/refusal/value/failure controls | Integrated / targeted-tested; combined integration run pending | Build passed (4 NU1900 warnings); `test zarr` 510 passed, 0 failed, plus 1 internal NetCDF differential skip (`libnetcdf` unavailable; omitted from footer total). See [D02-tests.md](D02-tests.md).
| C01 | Frozen `c-terminals`, base `361504b6`; compiler sources/corpus unchanged, OptimizeTests.fs `B0D35A…C878079` | Exact Release build, optimize, 543–546 C++/LLVM runtime routes and 543 plan/emission control | Integrated / targeted-tested | Corrected incremental Release build passed (2 NU1900 warnings); optimize 524 passed, 0 failed, 0 skipped. 12 C++ + 12 LLVM runtime fixture comparisons passed with expected values/BL8013 site. Real declared-index payload retention is covered locally and across modules; unused erased alias admission also passes. See C01-tests.md. |

Full-suite launches: **0**. `full-suite.started` must not exist until final authorization
from the orchestrator after all round cards close. A job is not complete without an exit
status, skip census and source identity. Runtime route assertions are required where a
fallback can otherwise make value tests pass.
