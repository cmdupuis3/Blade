# B02 targeted validation

Runner: Luna/high. Frozen `b-measurement` checkout at `3bec8a805dec5ecc62ff1116b7d1b58cd81251ef`. The six handoff pins matched before running the authorized tests: `run.py` `1915F56D9323B15B6E117C66AD7071DEBF57FEFB9E19B00B9B96CC0201939FDC`; `pm54.py` `98AE2446941B5C23FC65879122D7F2F72676B8CE835A8D567B28CFE8516B0A00`; `pm54_win32.py` `9437282CE04A92771571B0D385436F07B29D2B5F150B878A5B9E7468CEC48BBB`; `test_pm54.py` `0DF2155231EBCADB316075E8F8328311D272776CDF401B5DF3C118E023A410AE`; `README.md` `D2C1D49835655821B19865E8D49A7403212CE7BEC84B9E7929E0445390E4AFDA`; diagnostic specification `18A2A7125AFC1D36FB591A8C5DCA9457B1864087A6F67E2F89076F0218E594EA`.

All runs used the B worktree as explicit CWD, with private TEMP/TMP and absolute output/log paths under `bench/.build/B02-runner`. No Blade build, runtime, benchmark, or full/default test suite was run.

The 11 PM54 fixtures passed (`11 ran, OK`), and the 10 memory-report fixtures passed (`10 ran, OK`), both with no skips. Full logs: [test-pm54.log](../../../../b-measurement/bench/.build/B02-runner/test-pm54.log) and [test-memory.log](../../../../b-measurement/bench/.build/B02-runner/test-memory.log).

Native diagnostic `--bytes 65539` exited 0 and independently validated all 3 arms. Windows page size was 4096, allocation granularity 65536, and rounded reservation 69632 bytes. Reserve-only performed 0 stores and reads; sparse performed 1 store, touched 1 page, and read 1 checksum byte (sum 1); dense wrote and read all 65,539 bytes across 17 pages (sum 65,539). All arms released successfully. Session: `single-probe/session-20261022-102134-289098`.

Native diagnostic `--bytes 1000003` exited 0 and independently validated all 3 arms. Rounded reservation was 1,003,520 bytes. Reserve-only performed 0 stores and reads; sparse performed 15 stores, touched 15 pages, and read 15 checksum bytes (sum 15); dense wrote and read all 1,000,003 bytes across 245 pages (sum 1,000,003). All arms released successfully. Session: `multiple-probes/session-20261022-102145-199001`.

Both complete diagnostic directories are retained: [single-probe](../../../../b-measurement/bench/.build/B02-runner/single-probe) and [multiple-probes](../../../../b-measurement/bench/.build/B02-runner/multiple-probes). Stage snapshots expose current resident/current commit/peak resident/peak commit/page faults; the session separately records parent-process peaks. `sample.time_s` is null. No footprint threshold, performance result, or allocator-lifetime claim is inferred from these diagnostics.
