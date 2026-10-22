# B04 runner results

## Frozen inputs and toolchain

The B04 handoff SHA-256 `CB91246CFFB8AA39D4A9E48F59DC7892A22778F933AC844AFFA674419B41357E` and all 13 listed source/dependency pins were verified in the frozen `b-measurement` checkout before execution. The diagnostic source and compiler tree stayed unchanged. Results and full command logs are under the absolute runner directory `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B04-runner/`.

The Python fixtures passed 27/27. Reference generation succeeded; reference SHA-256 is `E7AE74DFA9F2CC56C3970F4FF7D5135878D6421C80BD31F5EF27AFFD82A66CA6`. The exact Release Blade build passed with 0 errors and 4 NU1900 vulnerability-source warnings. Blade.exe SHA-256 is `BD86829A3F013AB032EA9D181A9EBE431F056154F8296E4885CD5359F8AF006E`; Blade.dll is `2BDCB333A841E912E354FF24CDFB8E73497975705389F5A8FBA9AD262E51CC6A`. Compiler, g++, gfortran, clang, and objdump identities are recorded in `tool-identity.json`; the build log is `blade-build.stdout.txt` plus stderr.

## Native diagnostic arms

Each native invocation was created in a fresh private arm directory. All eight programs exited 0 without timeout or launch error. Every arm produced 2,053 p and 2,053 q values and passed numeric comparison at relative tolerance `1e-12` plus run-provenance validation. Licensed arms compare against the independent `fsum` reference; reproducible arms compare against the ascending serial reference. Full value lists/dumps and complete compile, emission, disassembly, run, and validator logs are retained per arm. The Blade LLVM runs emitted `.ll` and their compile logs confirm the LLVM lane; no fallback is counted as an LLVM pass.

| Arm | Regime | Exact executable SHA-256 | Stdout SHA-256 | Result |
|---|---|---|---|---|
| Blade C++ | Licensed | `047b1e6a0da0e8b401ea82994693b6f3d7e6eae014e860be536b09ba3ac8f046` | `645b5b93fcc97a180f8c909f5f46e2e3975dc275c18ccb4f476430f317fc692a` | All four gates pass; root store/endpoint review accepted |
| Blade C++ | Repro | `42a978eac81fb72b8bb7a46689e562becc26724848e43c491ae09b14aaf85a97` | `22007072496f5ae2019e4824e079dc9869f1fe73979463a694ba2a8007d185ae` | All four gates pass; root store/endpoint review accepted |
| Blade LLVM | Licensed | `35ad75a4e96e54b96d55be264178b90510afe81a0eaad2a7249b25b488596dc6` | `58004f8bd3f7c93077aff1010b85509519bdcc24b778dd95382d1b176e7c171c` | All four gates pass; root store/endpoint review accepted |
| Blade LLVM | Repro | `5b521f36bbd06debb5ca5374535532e257a4662dcbd1888602d77da1bd371017` | `56dbae8f073f217063911d4b9f5cd55b55ecbabe2d0939d3d23918df6339d302` | All four gates pass; root store/endpoint review accepted |
| gfortran natural | Licensed | `966b2fc22318648b61542dd308ba597a43d6970194f4e2d857022445f075b746` | `49c5293acdca7fa23648de9c93a5fb20c86231af788b95c761318a92bf8ed2b1` | All four gates pass; root store/endpoint review accepted |
| gfortran natural | Repro | `e2d99df193d9d7ed1f4690fd0bdf35fd7af20870f6e5de006778b90d6a2c1fe3` | `a338720ef683c6365a94c10e278583f60c6ec8abb328ee94310d828e57224edd` | All four gates pass; root store/endpoint review accepted |
| gfortran expert | Licensed | `c03f056b454376aec47d61fc6aa5d6ebf1cf16d2beff9f065f15f129c26e02fe` | `771b0e243778f9b7f002417b1b38b25102b164d4505fb1f9b02c8a873abaca96` | All four gates pass; root store/endpoint review accepted |
| gfortran expert | Repro | `8510e6144093a96da28921fcebe880d005c2f46f0971e02c5ea9c8201c9024bc` | `2031363235065e97f6fb89dbbe8a69af217648fd50f6ce87479d5ef2ef6fac26` | All four gates pass; root store/endpoint review accepted |

The gfortran dump files have identical SHA-256 across these runs because the emitted p/q vectors are identical after formatting: p `25370908dd94e624758807dde4dde8d47279b5a25790c203a5f0e0a086138e61`; q `159f50eadf073b5339e09ab16c5c0168209dc1f147ea7a347998d8ec813fb185`. This is output identity evidence only, not a bit-identity or timing claim.

Root reviewed exact-executable stores/endpoints in all eight arms. Each arm has an `audit.json` referencing its root review, exact executable, emission, disassembly, output and provenance files. The handoff saved-output validator then exited 0 for all eight: numeric, stores, endpoints and provenance pass; `diagnostic_accepted=true`; `performance_eligible=false`. No native rerun was performed. The command summary and per-arm audited JSON/logs are under `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B04-runner/`. Whole-process counters do not estimate allocation peak, retained capacity, or DRAM traffic. No performance claim is made.

Two non-arm harness invocation errors are preserved separately: attempt 1 stopped in the runner script before a compiler call; attempt 2 incorrectly passed `bench_support.f90` as a second Fortran source even though `natural.f90` includes it. Root confirmed this was a runner command deviation. The corrected attempt 3 used `natural.f90` alone and passed. Neither earlier attempt is counted among the eight native arms.

No full/default suite was launched. There are no diagnostic category skips.
