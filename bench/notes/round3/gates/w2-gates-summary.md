# Round-3 wave-2 end gates (integration tree 3a3925ac = the wave-2 tip)

Chain `w2-scripts/wave2-end.ps1` (unattended): build -> five gates from C:\bt_r2w1 -> examples sweep -> mirrors -> smoke -> A/B -> analysis.
Blade.dll SHA256 12917295D0525C30AAC5505158462D92F735F3651365B5E40696A4B4F7115B26, unchanged across the gates.

| gate | footer | wave-1 tip 9da403a8 |
|---|---|---|
| blade test | TOTAL: 7872 passed, 0 failed | 7389/0 |
| blade test opt-diff | Opt Diff: 2257 passed (1918 optimizer-inert), 0 failed | 2173/0 |
| blade test docs | Docs (executable code blocks): 132 passed, 0 failed, 78 skipped | 131/0 |
| blade test llvm | TOTAL: 914 passed, 0 failed | 841/0 |
| blade test interp | Interp Diff: 3118 passed, 0 failed | 3077/0 |

Examples sweep: 68 files, the same six pre-existing non-zero exits as wave 1 and master. Smoke: 15 of 15 (mirror, kernels,
regimes) runs exit 0, plus the saxpy mt8 pairs on r3w2legs0 smoked after the chain (the chain's smoke plan covered that
mirror's serial regimes only, so its saxpy block was refused and re-run). A/B: 27 blocks in the chain (11 clean, 16 drift
flags only), the re-run block, and one extra arm after the merge-level review (r3w2g35 = pull off AND `prefer`: the G35 row
slots proper on comoment3 mt8 repro, since `prefer` alone still pulls at rank 3). Merge-level block checks after each code
merge are appended below; the merge-level review's own fixes (records, the verbose switch cleared by the suite) were
verified by the `w2-fix` build + cli / run-record / docs blocks (log below).

Chain log:
```
2026-10-21T13:05:57 START tip=3a3925ac
2026-10-21T13:06:45 BUILD     0 Warning(s) /     0 Error(s) hash=12917295D0525C30AAC5505158462D92F735F3651365B5E40696A4B4F7115B26
2026-10-21T13:33:36 GATE default exit=0 wall_min=26.85 hash=12917295D0525C30AAC5505158462D92F735F3651365B5E40696A4B4F7115B26
2026-10-21T13:39:17 GATE opt-diff exit=0 wall_min=5.67 hash=12917295D0525C30AAC5505158462D92F735F3651365B5E40696A4B4F7115B26
2026-10-21T13:39:23 GATE docs exit=0 wall_min=0.11 hash=12917295D0525C30AAC5505158462D92F735F3651365B5E40696A4B4F7115B26
2026-10-21T13:58:53 GATE llvm exit=0 wall_min=19.5 hash=12917295D0525C30AAC5505158462D92F735F3651365B5E40696A4B4F7115B26
2026-10-21T14:38:46 GATE interp exit=0 wall_min=39.88 hash=12917295D0525C30AAC5505158462D92F735F3651365B5E40696A4B4F7115B26
FOOTER docs OK: Docs (executable code blocks): 132 passed, 0 failed
FOOTER opt-diff OK: Opt Diff: 2257 passed (1918 optimizer-inert), 0 failed
FOOTER default OK: TOTAL: 7872 passed, 0 failed
FOOTER llvm OK: TOTAL: 914 passed, 0 failed
FOOTER interp OK: Interp Diff: 3118 passed, 0 failed
2026-10-21T14:40:22 EXAMPLES 68 files, 6 failed
2026-10-21T14:40:56 MIRRORS exit=0
2026-10-21T15:07:05 SMOKE exit=0 0 nonzero
2026-10-21T15:07:05 AUDIT stray processes: 
2026-10-21T17:49:18 AB DONE exit=0
2026-10-21T17:49:29 ANALYZE ratio rows 198 arm rows 60 slow 2 missing 4
ALL DONE
```

A/B driver log:
```
2026-10-21T15:07:16 START pairkernel [mt8-repro-native,serial-repro-native] r3w1 load_before=2.2%
2026-10-21T15:10:56 END pairkernel r3w1 exit 0 (0 clean, 3 flags only, 1 failure) wall=3.7min
2026-10-21T15:11:06 START pairkernel [mt8-repro-native,serial-repro-native] r3w2 load_before=1.3%
2026-10-21T15:14:46 END pairkernel r3w2 exit 0 (0 clean, 3 flags only, 1 failure) wall=3.7min
2026-10-21T15:14:56 START pairkernel [mt8-repro-native,serial-repro-native] r3w2prefer load_before=1.6%
2026-10-21T15:18:37 END pairkernel r3w2prefer exit 3 (0 clean, 3 flags only, 1 failure) wall=3.7min
2026-10-21T15:18:47 START pairkernel [mt8-repro-native,serial-repro-native] r3w2pull0 load_before=1.5%
2026-10-21T15:22:27 END pairkernel r3w2pull0 exit 0 (0 clean, 3 flags only, 1 failure) wall=3.7min
2026-10-21T15:22:37 START comoment3 [mt8-repro-native,serial-repro-native] r3w1 load_before=2%
2026-10-21T15:26:50 END comoment3 r3w1 exit 0 (0 clean, 3 flags only, 1 failure) wall=4.2min
2026-10-21T15:27:00 START comoment3 [mt8-repro-native,serial-repro-native] r3w2 load_before=2.2%
2026-10-21T15:31:14 END comoment3 r3w2 exit 0 (0 clean, 3 flags only, 1 failure) wall=4.2min
2026-10-21T15:31:24 START comoment3 [mt8-repro-native,serial-repro-native] r3w2prefer load_before=1.5%
2026-10-21T15:35:38 END comoment3 r3w2prefer exit 3 (0 clean, 3 flags only, 1 failure) wall=4.2min
2026-10-21T15:35:48 START comoment3 [mt8-repro-native,serial-repro-native] r3w2pull0 load_before=1.5%
2026-10-21T15:40:03 END comoment3 r3w2pull0 exit 3 (0 clean, 3 flags only, 1 failure) wall=4.3min
2026-10-21T15:40:13 START antisym [serial-licensed-native,mt8-licensed-native] r3w1 load_before=10%
2026-10-21T15:44:25 END antisym r3w1 exit 3 (0 clean, 3 flags only, 1 failure) wall=4.2min
2026-10-21T15:44:35 START antisym [serial-licensed-native,mt8-licensed-native] r3w2 load_before=16%
2026-10-21T15:48:50 END antisym r3w2 exit 3 (0 clean, 3 flags only, 1 failure) wall=4.2min
2026-10-21T15:49:00 START antisym [serial-licensed-native,mt8-licensed-native] r3w2antig1 load_before=10.9%
2026-10-21T15:53:21 END antisym r3w2antig1 exit 3 (0 clean, 3 flags only, 1 failure) wall=4.3min
2026-10-21T15:53:31 START antisym [serial-licensed-native,mt8-licensed-native] r3w2 load_before=4.6%
2026-10-21T15:57:43 END antisym r3w2 exit 3 (0 clean, 3 flags only, 1 failure) wall=4.2min
2026-10-21T15:57:53 START antisym [serial-licensed-native,mt8-licensed-native] r3w2antig1 load_before=17.3%
2026-10-21T16:02:04 END antisym r3w2antig1 exit 0 (0 clean, 3 flags only, 1 failure) wall=4.2min
2026-10-21T16:02:14 START antisym [serial-licensed-native,mt8-licensed-native] r3w2 load_before=3.1%
2026-10-21T16:06:22 END antisym r3w2 exit 3 (0 clean, 3 flags only, 1 failure) wall=4.1min
2026-10-21T16:06:32 START antisym [serial-licensed-native,mt8-licensed-native] r3w2antig1 load_before=3.8%
2026-10-21T16:10:41 END antisym r3w2antig1 exit 3 (0 clean, 3 flags only, 1 failure) wall=4.1min
2026-10-21T16:10:51 START saxpy [serial-repro-native,serial-licensed-native,mt8-repro-native,mt8-licensed-native] r3w1 load_before=5.2%
2026-10-21T16:23:20 END saxpy r3w1 exit 0 (0 clean, 3 flags only, 1 failure) wall=12.5min
2026-10-21T16:23:30 START saxpy [serial-repro-native,serial-licensed-native,mt8-repro-native,mt8-licensed-native] r3w2 load_before=1.7%
2026-10-21T16:35:59 END saxpy r3w2 exit 3 (0 clean, 3 flags only, 1 failure) wall=12.5min
2026-10-21T16:36:09 START saxpy [serial-repro-native,serial-licensed-native,mt8-repro-native,mt8-licensed-native] r3w2legs0 load_before=2.1%
2026-10-21T16:36:15 END saxpy r3w2legs0 exit 1 (0 clean, 3 flags only, 1 failure) wall=0.1min
2026-10-21T16:36:25 START haloadjoint [serial-licensed-native,mt8-licensed-native,serial-repro-native,mt8-repro-native] r3w1 load_before=2.3%
2026-10-21T16:53:47 END haloadjoint r3w1 exit 3 (0 clean, 3 flags only, 1 failure) wall=17.4min
2026-10-21T16:53:57 START haloadjoint [serial-licensed-native,mt8-licensed-native,serial-repro-native,mt8-repro-native] r3w2 load_before=1.5%
2026-10-21T17:11:18 END haloadjoint r3w2 exit 3 (0 clean, 3 flags only, 1 failure) wall=17.4min
2026-10-21T17:11:28 START stencil [mt8-repro-native,mt8-licensed-native] r3w1 load_before=1.1%
2026-10-21T17:18:33 END stencil r3w1 exit 0 (0 clean, 3 flags only, 1 failure) wall=7.1min
2026-10-21T17:18:43 START stencil [mt8-repro-native,mt8-licensed-native] r3w2 load_before=1.4%
2026-10-21T17:25:49 END stencil r3w2 exit 0 (0 clean, 3 flags only, 1 failure) wall=7.1min
2026-10-21T17:25:59 START stencil [mt8-repro-native,mt8-licensed-native] r3w2retain load_before=1.5%
2026-10-21T17:33:09 END stencil r3w2retain exit 3 (0 clean, 3 flags only, 1 failure) wall=7.2min
2026-10-21T17:33:19 START recur [mt8-licensed-native,mt8-repro-native] r3w1 load_before=0.8%
2026-10-21T17:37:24 END recur r3w1 exit 3 (0 clean, 3 flags only, 1 failure) wall=4.1min
2026-10-21T17:37:34 START recur [mt8-licensed-native,mt8-repro-native] r3w2 load_before=12%
2026-10-21T17:41:40 END recur r3w2 exit 3 (0 clean, 3 flags only, 1 failure) wall=4.1min
2026-10-21T17:41:50 START recur [mt8-licensed-native,mt8-repro-native] r3w2retain load_before=2.1%
2026-10-21T17:45:57 END recur r3w2retain exit 0 (0 clean, 3 flags only, 1 failure) wall=4.1min
2026-10-21T17:46:07 START gram [serial-licensed-native] r3w1 load_before=1.8%
2026-10-21T17:47:37 END gram r3w1 exit 0 (0 clean, 3 flags only, 1 failure) wall=1.5min
2026-10-21T17:47:47 START gram [serial-licensed-native] r3w2 load_before=1.7%
2026-10-21T17:49:18 END gram r3w2 exit 3 (0 clean, 3 flags only, 1 failure) wall=1.5min
ALL DONE
2026-10-21T17:51:52 FIXUP smoke r3w2legs0 saxpy mt8 exit 0 wall=2.2min
2026-10-21T17:52:02 START saxpy [serial-repro-native,serial-licensed-native,mt8-repro-native,mt8-licensed-native] r3w2legs0 (fix-up) load_before=10.2%
2026-10-21T18:04:37 END saxpy r3w2legs0 (fix-up) exit 3 (0 clean, 3 flags only, 1 failure) wall=12.6min
2026-10-21T18:04:48 FIXUP ANALYZE ratio rows 222 arm rows 64 slow 2 missing 0
FIXUP DONE
2026-10-21T18:08:51 G35ARM smoke r3w2g35 comoment3 mt8-repro exit 0 wall=0.7min
2026-10-21T18:09:01 START comoment3 [mt8-repro-native] r3w2g35 (G35 arm) load_before=5.2%
2026-10-21T18:11:14 END comoment3 r3w2g35 (G35 arm) exit 3 (0 clean, 3 flags only, 1 failure) wall=2.2min
2026-10-21T18:11:25 G35ARM ANALYZE ratio rows 234 arm rows 65 slow 2 missing 0
G35ARM DONE
```

#### merge check 1: W2-0 + W2-3 on 844e2ae7
```
2026-10-21T09:27:07 START tip=844e2ae7
2026-10-21T09:27:54 BUILD     0 Warning(s) /     0 Error(s) hash=99C9E286176202DD375587F78E704FE6C8EDB9897C04AA9AFB03856E189DF951
2026-10-21T09:32:46 BLOCK omp-reduce exit=0 wall_min=4.84 :: [PASS]: g7_zero_extent_seeded_joins -- threaded T = 1 / 3 / 8 identical to the serial build: z1 = 10.0, z2 = 10.0, y1 = 20.0, y2 = 30.0 || OpenMP Reduce: 126 passed, 0 failed
2026-10-21T09:33:17 BLOCK omp-coverage exit=0 wall_min=0.52 :: [PASS]: omp_value_check -- correct values across 5 runs under OMP_NUM_THREADS=4 || OpenMP Coverage: 15 passed, 0 error(s), 0 warning(s)
2026-10-21T09:35:13 BLOCK orbit-scatter exit=0 wall_min=1.93 :: [PASS]: LLVM (its serial scatter): 12 cases, raw bits == the pull build's -- every e<k> true; routes as expected; all 12 raw patterns identical to the reference (NaN payload and -0.0 included) || Orbit Scatter: 30 passed, 0 failed, 0 skipped
2026-10-21T09:35:50 BLOCK symmetry exit=0 wall_min=0.62 :: Full Pass:    101 / 140 (IR + Compile + Run) || Verdict:      140 passed, 0 failed, 0 skipped
2026-10-21T09:35:58 BLOCK optimize exit=0 wall_min=0.13 :: [PASS]: validate_empty_match_in_function -- refused: empty match expression || Optimization Layer: 344 passed, 0 failed
2026-10-21T09:42:30 BLOCK cli exit=0 wall_min=6.53 :: [PASS]: names: the REPL's session names for lambdas are their bindings || CLI Smoke: 150 passed, 0 failed
2026-10-21T09:42:36 BLOCK run-record exit=0 wall_min=0.1 :: PASS: record: exactly one failure is reported however many workers fail || PASS: record: one well-formed record carrying the failure code
2026-10-21T09:43:01 BLOCK row-tile exit=0 wall_min=0.42 :: [PASS]: vfmadd census contract=fast -- 245 packed vfmadd (6x8 kloops: 12 per step; paired 4x4: 8) || Row Contraction Tiles: 27 passed, 0 failed
2026-10-21T09:44:02 BLOCK linalg exit=0 wall_min=1.03 :: [PASS]: malformed toolchain file degrades to unconfigured -- no crash, empty map || BLAS Tier Resolution: 19 passed, 0 failed
2026-10-21T09:44:27 BLOCK corpus-wiring exit=0 wall_min=0.41 :: [PASS]: a nested Bool pin with a non-Bool leaf or an empty row is malformed, not dropped || Corpus Wiring: 14 passed, 0 failed
2026-10-21T09:44:57 BLOCK docs exit=0 wall_min=0.49 :: [PASS]: links -- 59 page(s): every relative link and cited corpus path exists || Docs (executable code blocks): 131 passed, 0 failed, 78 skipped
2026-10-21T09:48:19 BLOCK interp-symmetry exit=0 wall_min=3.37 :: [PASS]: provider-reads: zarr dense f64/f32/i64 (M6 wiring) -- interp read == compiled read (byte-identical: f64 rank-2 + f32 + i64) || Interp Diff: 141 passed, 0 failed, 0 skip-unsupported, 0 skipped
2026-10-21T09:48:28 BLOCK opt-diff-symmetry exit=0 wall_min=0.16 :: [PASS]: a comm pair on non-adjacent parameters over one array stays dense -- C++ differs (BLADE_FUSION), output identical || Opt Diff: 102 passed (96 optimizer-inert), 0 failed, 38 skipped, 9s
2026-10-21T09:51:01 BLOCK llvm-symmetry exit=0 wall_min=2.54 :: [PASS]: Row-owned pull scatter: rank-3 strict checksums under an outer omp request || LLVM Differential: 83 passed, 0 failed, 57 skipped
2026-10-21T09:51:28 BLOCK llvm-tiles exit=0 wall_min=0.45 :: [PASS]: policy keying: =off and =fast build distinct tile objects in one directory -- blade_llvm_tiles-c3e75487f738c50f.obj / blade_llvm_tiles-adbcbee55c3841b6.obj || LLVM Tiles Raw-Bit: 12 passed, 0 failed, 0 skipped
ALL DONE
```

#### merge check -2: W2-1 on e2194721
```
2026-10-21T09:53:28 START tip=e2194721
2026-10-21T09:54:21 BUILD     0 Warning(s) /     0 Error(s) hash=BE364872AB3EFBBBBFE85654D5812EA17F147DA1AB8B4FC69490EEB2132E72CB
2026-10-21T09:55:41 BLOCK sql exit=0 wall_min=1.3 :: Full Pass:    161 / 188 (IR + Compile + Run) || Verdict:      188 passed, 0 failed, 0 skipped
2026-10-21T09:57:18 BLOCK index-types exit=0 wall_min=1.62 :: Full Pass:    222 / 390 (IR + Compile + Run) || Verdict:      390 passed, 0 failed, 0 skipped
2026-10-21T09:57:25 BLOCK optimize exit=0 wall_min=0.12 :: [PASS]: validate_empty_match_in_function -- refused: empty match expression || Optimization Layer: 401 passed, 0 failed
2026-10-21T10:05:19 BLOCK cli exit=0 wall_min=7.91 :: [PASS]: names: the REPL's session names for lambdas are their bindings || CLI Smoke: 165 passed, 0 failed
2026-10-21T10:05:26 BLOCK run-record exit=0 wall_min=0.11 :: PASS: record: exactly one failure is reported however many workers fail || PASS: record: one well-formed record carrying the failure code
2026-10-21T10:07:22 BLOCK orbit-scatter exit=0 wall_min=1.94 :: [PASS]: LLVM (its serial scatter): 12 cases, raw bits == the pull build's -- every e<k> true; routes as expected; all 12 raw patterns identical to the reference (NaN payload and -0.0 included) || Orbit Scatter: 30 passed, 0 failed, 0 skipped
2026-10-21T10:07:55 BLOCK omp-coverage exit=0 wall_min=0.56 :: [PASS]: omp_value_check -- correct values across 5 runs under OMP_NUM_THREADS=4 || OpenMP Coverage: 15 passed, 0 error(s), 0 warning(s)
2026-10-21T10:07:56 BLOCK subst exit=0 wall_min=0 :: [PASS]: SubstMap is live: a reference hit at the IRIndex arm renders the substituted local || Subst: 3 passed, 0 failed
2026-10-21T10:08:27 BLOCK corpus-wiring exit=0 wall_min=0.51 :: [PASS]: a nested Bool pin with a non-Bool leaf or an empty row is malformed, not dropped || Corpus Wiring: 14 passed, 0 failed
2026-10-21T10:09:02 BLOCK docs exit=0 wall_min=0.58 :: [PASS]: links -- 59 page(s): every relative link and cited corpus path exists || Docs (executable code blocks): 131 passed, 0 failed, 78 skipped
2026-10-21T10:09:04 BLOCK llvm-facts exit=0 wall_min=0.04 :: [PASS]: member hoist route (llvm): sql-semijoins `Semijoin Then Mask` -- 1 build(s) -- 1 init -> report -> probes -> fini || LLVM Fact Layer: 99 passed, 0 failed, 0 skipped
2026-10-21T10:09:07 BLOCK llvm-shim exit=0 wall_min=0.05 :: [PASS]: shim_pool_cache_off || LLVM Runtime Shim: 4 passed, 0 failed, 0 skipped
2026-10-21T10:09:44 BLOCK llvm-sql-unique-contains exit=0 wall_min=0.62 :: [PASS]: Member Hoist Stored Alias Chain || LLVM Differential: 19 passed, 0 failed, 7 skipped
2026-10-21T10:10:21 BLOCK llvm-sql-masks exit=0 wall_min=0.61 :: [PASS]: Compound Mask Failure Valid Data (aborts) -- both lanes aborted with the same error || LLVM Differential: 15 passed, 0 failed, 3 skipped
2026-10-21T10:10:35 BLOCK llvm-sql-semijoins exit=0 wall_min=0.25 :: [PASS]: Semijoin Then Mask || LLVM Differential: 6 passed, 0 failed, 1 skipped
2026-10-21T10:13:37 BLOCK llvm-symmetry exit=0 wall_min=3.03 :: [PASS]: Row-owned pull scatter: rank-3 strict checksums under an outer omp request || LLVM Differential: 83 passed, 0 failed, 57 skipped
2026-10-21T10:14:30 BLOCK interp-sql-unique-contains exit=0 wall_min=0.88 :: [PASS]: provider-reads: zarr dense f64/f32/i64 (M6 wiring) -- interp read == compiled read (byte-identical: f64 rank-2 + f32 + i64) || Interp Diff: 26 passed, 0 failed, 1 skip-unsupported, 0 skipped
2026-10-21T10:15:02 BLOCK interp-sql-masks exit=0 wall_min=0.54 :: [PASS]: provider-reads: zarr dense f64/f32/i64 (M6 wiring) -- interp read == compiled read (byte-identical: f64 rank-2 + f32 + i64) || Interp Diff: 19 passed, 0 failed, 0 skip-unsupported, 0 skipped
2026-10-21T10:15:22 BLOCK interp-sql-semijoins exit=0 wall_min=0.32 :: [PASS]: provider-reads: zarr dense f64/f32/i64 (M6 wiring) -- interp read == compiled read (byte-identical: f64 rank-2 + f32 + i64) || Interp Diff: 8 passed, 0 failed, 0 skip-unsupported, 0 skipped
2026-10-21T10:15:32 BLOCK opt-diff-sql-unique-contains exit=0 wall_min=0.18 :: [PASS]: Member Hoist Stored Alias Chain -- C++ differs (BLADE_MEMBER_HOIST), output identical || Opt Diff: 27 passed (12 optimizer-inert), 0 failed, 0 skipped, 10s
2026-10-21T10:15:39 BLOCK opt-diff-sql-masks exit=0 wall_min=0.11 :: [PASS]: SQL WHERE -- C++ differs (BLADE_COMPACTION_FUSION=gather), output identical || Opt Diff: 20 passed (16 optimizer-inert), 0 failed, 0 skipped, 6s
2026-10-21T10:15:44 BLOCK opt-diff-sql-semijoins exit=0 wall_min=0.08 :: [PASS]: Semijoin Then Mask -- C++ differs (BLADE_MEMBER_HOIST), output identical || Opt Diff: 7 passed (1 optimizer-inert), 0 failed, 0 skipped, 5s
ALL DONE
```

#### merge check -3: W2-4 on 58efa62d
```
2026-10-21T11:14:12 START tip=58efa62d
2026-10-21T11:15:04 BUILD     0 Warning(s) /     0 Error(s) hash=6BA83C910283E8686CDDDFFFA48A31BD40661459C161EDDB2D4E33297ECAAE3C
2026-10-21T11:15:18 BLOCK sibling-folds exit=0 wall_min=0.21 :: Full Pass:    25 / 29 (IR + Compile + Run) || Verdict:      29 passed, 0 failed, 0 skipped
2026-10-21T11:17:15 BLOCK cell-epilogue exit=0 wall_min=1.94 :: [PASS]: 029_input_legs_throwing_callee_keeps_frame llvm: fault, site and frame equal gate on vs off -- exit 1 both; identical text: error[BL8013]: integer division by zero /   at kernel || Cell Epilogue: 68 passed, 0 failed, 0 skipped
2026-10-21T11:17:42 BLOCK optimize exit=0 wall_min=0.45 :: [PASS]: validate_empty_match_in_function -- refused: empty match expression || Optimization Layer: 433 passed, 0 failed
2026-10-21T11:18:13 BLOCK omp-coverage exit=0 wall_min=0.51 :: [PASS]: omp_value_check -- correct values across 5 runs under OMP_NUM_THREADS=4 || OpenMP Coverage: 15 passed, 0 error(s), 0 warning(s)
2026-10-21T11:22:57 BLOCK omp-reduce exit=0 wall_min=4.74 :: [PASS]: g7_zero_extent_seeded_joins -- threaded T = 1 / 3 / 8 identical to the serial build: z1 = 10.0, z2 = 10.0, y1 = 20.0, y2 = 30.0 || OpenMP Reduce: 126 passed, 0 failed
2026-10-21T11:30:44 BLOCK cli exit=0 wall_min=7.77 :: [PASS]: names: the REPL's session names for lambdas are their bindings || CLI Smoke: 165 passed, 0 failed
2026-10-21T11:32:41 BLOCK orbit-scatter exit=0 wall_min=1.96 :: [PASS]: LLVM (its serial scatter): 12 cases, raw bits == the pull build's -- every e<k> true; routes as expected; all 12 raw patterns identical to the reference (NaN payload and -0.0 included) || Orbit Scatter: 30 passed, 0 failed, 0 skipped
2026-10-21T11:33:32 BLOCK sql exit=0 wall_min=0.84 :: Full Pass:    161 / 188 (IR + Compile + Run) || Verdict:      188 passed, 0 failed, 0 skipped
2026-10-21T11:33:48 BLOCK run-record exit=0 wall_min=0.26 :: PASS: record (parsed): rr_opt_forms_serial -- top-level members, the route fields || PASS: record (parsed): rr_opt_both -- top-level members, the route fields
2026-10-21T11:33:52 BLOCK corpus-wiring exit=0 wall_min=0.07 :: [PASS]: a nested Bool pin with a non-Bool leaf or an empty row is malformed, not dropped || Corpus Wiring: 14 passed, 0 failed
2026-10-21T11:34:26 BLOCK docs exit=0 wall_min=0.56 :: [PASS]: links -- 59 page(s): every relative link and cited corpus path exists || Docs (executable code blocks): 131 passed, 0 failed, 78 skipped
2026-10-21T11:34:28 BLOCK llvm-facts exit=0 wall_min=0.05 :: [PASS]: member hoist route (llvm): reynolds `Reynolds Antisym Dedup Sees Contains` -- 0 build(s) -- no structure, the scan || LLVM Fact Layer: 104 passed, 0 failed, 0 skipped
2026-10-21T11:35:17 BLOCK llvm-sibling-folds exit=0 wall_min=0.82 :: [PASS]: A producer callee that may fail is not inlined for the input legs, and fails in its own frame (aborts) -- both lanes aborted with the same error || LLVM Differential: 28 passed, 0 failed, 1 skipped
2026-10-21T11:36:02 BLOCK interp-sibling-folds exit=0 wall_min=0.74 :: [PASS]: provider-reads: zarr dense f64/f32/i64 (M6 wiring) -- interp read == compiled read (byte-identical: f64 rank-2 + f32 + i64) || Interp Diff: 30 passed, 0 failed, 0 skip-unsupported, 0 skipped
2026-10-21T11:36:16 BLOCK opt-diff-sibling-folds exit=0 wall_min=0.23 :: [PASS]: A producer callee that may fail is not inlined for the input legs, and fails in its own frame (aborts) -- C++ differs (BLADE_CELL_EPILOGUE+BLADE_SIBLING_FOLDS), output identical || Opt Diff: 29 passed (6 optimizer-inert), 0 failed, 0 skipped, 13s
ALL DONE
```

#### merge check -4: W2-6 on 8fd38425
```
2026-10-21T11:44:44 START tip=8fd38425
2026-10-21T11:45:57 BUILD     0 Warning(s) /     0 Error(s) hash=8E45602C16345090C6AB35C9B662141DEBC451A1B6D4DE5A7278F953353A9D92
2026-10-21T11:46:14 BLOCK alloc exit=0 wall_min=0.25 :: ALLOC TESTS: 73/73 passed || Alloc Layout: all passed
2026-10-21T11:46:20 BLOCK run-record exit=0 wall_min=0.09 :: PASS: pool record: the default and the pair print identical values || PASS: record (parsed): rr_opt_both under BLADE_POOL_RETAIN=1 -- policy.pool beside forms and member_hoist
2026-10-21T11:53:38 BLOCK cli exit=0 wall_min=7.31 :: [PASS]: names: the REPL's session names for lambdas are their bindings || CLI Smoke: 175 passed, 0 failed
2026-10-21T11:53:44 BLOCK llvm-shim exit=0 wall_min=0.1 :: [PASS]: shim_pool_retain_retain32_64 || LLVM Runtime Shim: 12 passed, 0 failed, 0 skipped
2026-10-21T11:54:06 BLOCK llvm-facts exit=0 wall_min=0.36 :: [PASS]: member hoist route (llvm): reynolds `Reynolds Antisym Dedup Sees Contains` -- 0 build(s) -- no structure, the scan || LLVM Fact Layer: 104 passed, 0 failed, 0 skipped
2026-10-21T11:56:05 BLOCK cell-epilogue exit=0 wall_min=1.98 :: [PASS]: 029_input_legs_throwing_callee_keeps_frame llvm: fault, site and frame equal gate on vs off -- exit 1 both; identical text: error[BL8013]: integer division by zero /   at kernel || Cell Epilogue: 68 passed, 0 failed, 0 skipped
2026-10-21T11:56:16 BLOCK sibling-folds exit=0 wall_min=0.18 :: Full Pass:    25 / 29 (IR + Compile + Run) || Verdict:      29 passed, 0 failed, 0 skipped
2026-10-21T11:56:25 BLOCK optimize exit=0 wall_min=0.16 :: [PASS]: validate_empty_match_in_function -- refused: empty match expression || Optimization Layer: 433 passed, 0 failed
2026-10-21T11:56:30 BLOCK corpus-wiring exit=0 wall_min=0.08 :: [PASS]: a nested Bool pin with a non-Bool leaf or an empty row is malformed, not dropped || Corpus Wiring: 14 passed, 0 failed
2026-10-21T11:56:46 BLOCK docs exit=0 wall_min=0.27 :: [PASS]: links -- 59 page(s): every relative link and cited corpus path exists || Docs (executable code blocks): 131 passed, 0 failed, 78 skipped
ALL DONE
```

#### merge check -5: T4c on f6c8c115
```
2026-10-21T12:39:47 START tip=f6c8c115
2026-10-21T12:40:28 BUILD     0 Warning(s) /     0 Error(s) hash=8284330F8BE7F465F0EED708548A52DD184779FBBAD9D15BEE0C8AC0643F82BF
2026-10-21T12:41:44 BLOCK index-types exit=0 wall_min=1.24 :: Full Pass:    230 / 401 (IR + Compile + Run) || Verdict:      401 passed, 0 failed, 0 skipped
2026-10-21T12:42:14 BLOCK sql exit=0 wall_min=0.48 :: Full Pass:    161 / 188 (IR + Compile + Run) || Verdict:      188 passed, 0 failed, 0 skipped
2026-10-21T12:42:21 BLOCK optimize exit=0 wall_min=0.13 :: [PASS]: validate_empty_match_in_function -- refused: empty match expression || Optimization Layer: 486 passed, 0 failed
2026-10-21T12:53:23 BLOCK cli exit=0 wall_min=11.03 :: [PASS]: names: the REPL's session names for lambdas are their bindings || CLI Smoke: 202 passed, 0 failed
2026-10-21T12:53:29 BLOCK run-record exit=0 wall_min=0.1 :: PASS: pool record: the default and the pair print identical values || PASS: record (parsed): rr_opt_both under BLADE_POOL_RETAIN=1 -- policy.pool beside forms and member_hoist
2026-10-21T12:53:33 BLOCK corpus-wiring exit=0 wall_min=0.06 :: [PASS]: a nested Bool pin with a non-Bool leaf or an empty row is malformed, not dropped || Corpus Wiring: 14 passed, 0 failed
2026-10-21T12:53:42 BLOCK docs exit=0 wall_min=0.15 :: [PASS]: links -- 59 page(s): every relative link and cited corpus path exists || Docs (executable code blocks): 132 passed, 0 failed, 78 skipped
2026-10-21T12:53:45 BLOCK llvm-facts exit=0 wall_min=0.05 :: [PASS]: member hoist route (llvm): reynolds `Reynolds Antisym Dedup Sees Contains` -- 0 build(s) -- no structure, the scan || LLVM Fact Layer: 107 passed, 0 failed, 0 skipped
2026-10-21T12:58:43 BLOCK llvm-index-types exit=0 wall_min=4.96 :: [PASS]: Compaction fusion T4c: two companions whose sources precede the mask (the gather form) || LLVM Differential: 183 passed, 0 failed, 218 skipped
2026-10-21T13:04:46 BLOCK interp-index-types exit=0 wall_min=6.06 :: [PASS]: provider-reads: zarr dense f64/f32/i64 (M6 wiring) -- interp read == compiled read (byte-identical: f64 rank-2 + f32 + i64) || Interp Diff: 399 passed, 0 failed, 3 skip-unsupported, 0 skipped
2026-10-21T13:05:16 BLOCK opt-diff-index-types exit=0 wall_min=0.5 :: [PASS]: Compaction fusion T4c: two companions over a run-time source extent -- C++ differs (BLADE_COMPACTION_FUSION=unshared), output identical || Opt Diff: 316 passed (239 optimizer-inert), 0 failed, 136 skipped, 30s
ALL DONE
```

#### merge-level review fixes (w2-fix)
```
2026-10-21T18:11:48 START
2026-10-21T18:12:30 BUILD     0 Warning(s) /     0 Error(s)
2026-10-21T18:25:05 BLOCK cli exit=0 wall_min=12.58 :: [PASS]: names: the REPL's session names for lambdas are their bindings || CLI Smoke: 202 passed, 0 failed
2026-10-21T18:25:31 BLOCK run-record exit=0 wall_min=0.43 :: PASS: pool record: the default and the pair print identical values || PASS: record (parsed): rr_opt_both under BLADE_POOL_RETAIN=1 -- policy.pool beside forms and member_hoist
2026-10-21T18:26:00 BLOCK docs exit=0 wall_min=0.49 :: [PASS]: links -- 59 page(s): every relative link and cited corpus path exists || Docs (executable code blocks): 132 passed, 0 failed, 78 skipped
ALL DONE
```
