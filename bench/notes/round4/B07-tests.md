# B07 stage 1 and saved-output audit validation\n\nThe reviewed source-only suffix repair passed the pure fixture gate and all eight requested numeric/provenance arms. Root reviewed the eight exact native artifacts, and saved-output validation now passes for every canonical arm. This completes stage 1 plus the authorized validator checkpoint; no native task was rerun for validation. Each arm produced
one native process/task interval; all eight reports show 14 assertions, zero failed,
full 2,053-cell `p` and `q` output, and matching scalar checksums. Native enclosure
review remains unmeasured, diagnostic acceptance remains false, and performance
eligibility remains false. No saved-output audit validation, prepared mutation,
benchmark, or full-suite run was dispatched.

The runner used frozen checkout `2a5c2e26a6925b126aa9eafd8e0331068bf4ca3d`
(`b-measurement`). The handoff SHA256 is
`E73E0AA82BC48BC047DA7016999CD0C38D874554A85893D313DB212CBF90C925`; the runner,
adapter, fixture, and frozen-manifest hashes are respectively `77E15567DC72D8FA9EADEB852AE6499271887E818326EDA077F3CD46D445143B`,
`F90C7DF3CC8297A1F090144DE4873722AEE096CAD8263A7A9F5419B44879C925`,
`01B946E51D8E8CAEBE101BB683C3CBAADD531A90D006EF69179B2EB851DF2FFF`, and
`56B53905E7A28D96B1AE4F1A3257EA47BECE002BAA1D26F7FE970EDFD83082C9`. The 27-file
frozen manifest matched. The exact repair changed only the C++ terminal-suffix fixture
expectation to match the pinned inputs’ final `}` byte with no final newline.

Pure fixtures: 14 passed, 0 failed, 0 skipped. The runner completed these eight
arms: C++ licensed/repro, LLVM licensed/repro, natural Fortran licensed/repro, and
expert Fortran licensed/repro. Each report records `task_status=0`, 14/14 assertions,
`failed=0`, `canonical_bad=0`, `p_count=q_count=2053`, checksum `3717.49623874405`,
and input checksum `2031.76322963322`. Across them the runner recorded 8 native
processes, 8 task calls/intervals, 40 observer calls, and 48 cleanup calls. These are
stage counters, not memory or performance measurements; all reports retain
`compiler_enclosure_established=null` and `performance_eligible=false`.

Tool identities were Python 3.14.7, gcc/g++ 16.2.0, clang 22.1.8, gfortran 16.2.0,
and GNU objdump 2.47.20260726. Binary SHA256 identities are Python
`4942B86A6597E5AEE0128DAA00050ED79BC21F6E709A78EB19CBFEB0C2F39AC9`, gcc
`F4120FFEA38B73EFAF4D770C1942905E220819AE97299C94BFEAB3C396B686E2`, g++
`58EFE82FA3CE35005F89D46FE439D7EE54E6889CB7DB894019202DA691A529A5`, clang
`071874172EDC90EA355DEF3E7E253A27A82646B20B3CA393962621B52F32A708`, gfortran
`D818D364C3F0A422FC195F80539554D4EA38FDCD719046771233DBAC294BC371`, and objdump
`4F79D1DB631C0717F0DCAC8CDB7D28A2ADFA371553A698C95663EB79664E324C`. The complete
runner artifacts, command/stdout/stderr records, adapted inputs, object files,
executables, disassemblies, reports, and provenance are retained under
`C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner-a2`.
The exact top-level invocation and environment are recorded in
`bench/.build/B07-runner-a2.invocation.txt`; the Python fixture command is
`006-command.json` with its 14/14 output in `006-stdout.txt`. Per-arm report,
provenance, executable, and complete child-command evidence are saved in each arm
directory. Executable/report SHA256 identities:

| Arm | Executable | Report |
|---|---|---|
| cpp-licensed | `3205B96DAA7B5BDDC72A03C438E4E104761CE3F7ECFC42384EB3693501888C82` | `EB8E4AC1E1A2BE00E955FB8E9A8F5FF5FD15EBB6B6E10E1BB2B95A3518DAB122` |
| cpp-repro | `B0C68A94D1CF3AFA135AC015467EA5C27A8731072B68340476D7DFEE2FF92115` | `3C98FDDE381676B2132D28A28B6DC4F24CD15CF01F5E16D5E487AD9C60AFA9B1` |
| llvm-licensed | `351C5DC2C7A188941627A4C68BA944DC53F39A826E61B48F2FB94DB613C9AAF5` | `08D3C5F8D6C3769DEA60390A43AF2E1474ECEE14513D72499AA02329DAC622BE` |
| llvm-repro | `CB3D2084E3012FF229998E1271BF5B4F809CB2783BC0055FFBAE637520FD01C6` | `D4D43C30D0D78FFC8B58089FCD9D09A794E6A4DE3D153F0D452A9150AF0E4FC0` |
| natural-licensed | `D8E964DC847A7C1B4B1377FB92A3C1124786425502D4215B293645608065D9E1` | `2D66E4909EE9923204F0648FB1B72BB21C973B3A08AA456B7905281610C2185A` |
| natural-repro | `E60E6F8817B2350D03196ADECA0A13005D54E1F4EAEE3A86C0219695ECA330EC` | `67B19408AD960FBB7F58E5490FABEAF4A3376B3615EA7CB269F1DD223FC0B867` |
| expert-licensed | `88587E1A6D285F63DEEC7CB8B1ED0ED80B2C8811D349F8884290921095548E62` | `59F24F7AB084EB5E021C6C15270EF9B16BD26E20452B123A04DEF674F8FCE29F` |
| expert-repro | `32313BB282CFE31DFD5162B2A1E35B80625402438C9D586BAB7FDA218F825020` | `12BB31A122083B3E0F6AFABF317EA421087A5F5B110E4E8446FC458851AA32C3` |

Stage status JSON SHA256:
`9AC856379A4A2F92265C096CAF120FAFE442FA01EF0165404D8938E29D314360`.

## Historical first attempt

The initial B07 primary stage stopped before native arms. It remains preserved under
`bench/.build/B07-runner`; 14 fixtures ran, 13 passed, and one errored in
`test_four_adaptations_preserve_exact_arithmetic_prefix` with
`ValueError: unknown C++ failure scaffold`. The repaired retry above is the current
stage result; the first-attempt evidence is historical and unchanged.

Frozen checkout identity is `fa4fa882605fc404868b99eff1df9eeade44c9d9` (`b-measurement`). The handoff hash is `36429D225D636067118D6B94C45CF5952861C96ED705C13870F336DF12C617B8`; runner hash `77E15567DC72D8FA9EADEB852AE6499271887E818326EDA077F3CD46D445143B`; frozen manifest hash `56B53905E7A28D96B1AE4F1A3257EA47BECE002BAA1D26F7FE970EDFD83082C9`. All 27 entries in the frozen manifest matched their content hashes. The fixture and adapter identities were also pinned: `test_boundary.py` SHA256 `0F6C842E72C3CD3FE321DA0D4D32BDC0B187D6C2BC96DB337C9B57DFF255681C`; `adapt.py` SHA256 `0138808DCC5B291E02E42F60E3C028E29C97F37CD6CC028A0D6C2D5CA2683C4F`.

The failing test was `ExactArtifactTests.test_four_adaptations_preserve_exact_arithmetic_prefix`. It reached the pinned C++ artifact and failed in `adapt.transform` / `cpp_task` with `ValueError: unknown C++ failure scaffold`. This is a failure of the frozen fixture/adaptation checkpoint; it does not establish a native compiler or kernel failure. Per the handoff, the run stopped there and preserves the initial output for root review.

The exact invocation was:

```powershell
C:\Python314\python.exe C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\diagnostics\output-boundary\runner.py --stage primary --out C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner --gcc C:\msys64\ucrt64\bin\gcc.exe --gxx C:\msys64\ucrt64\bin\g++.exe --clang C:\msys64\clang64\bin\clang.exe --gfortran C:\msys64\ucrt64\bin\gfortran.exe --objdump C:\msys64\ucrt64\bin\objdump.exe
```

The working directory was the `b-measurement` checkout root. `TEMP` and `TMP` pointed to its private `bench/.build/B07-runner-private-tmp`; the runner recorded its own base temp at `bench/.build/B07-runner/base-temp`. `PYTHONDONTWRITEBYTECODE=1`. Tool identities were Python 3.14.7 (`4942B86A6597E5AEE0128DAA00050ED79BC21F6E709A78EB19CBFEB0C2F39AC9`), gcc/g++ 16.2.0 (`F4120FFEA38B73EFAF4D770C1942905E220819AE97299C94BFEAB3C396B686E2` / `58EFE82FA3CE35005F89D46FE439D7EE54E6889CB7DB894019202DA691A529A5`), clang 22.1.8 (`071874172EDC90EA355DEF3E7E253A27A82646B20B3CA393962621B52F32A708`), gfortran 16.2.0 (`D818D364C3F0A422FC195F80539554D4EA38FDCD719046771233DBAC294BC371`), and objdump 2.47.20260726 (`4F79D1DB631C0717F0DCAC8CDB7D28A2ADFA371553A698C95663EB79664E324C`). The runner's version checks 001–005 exited 0; fixture command 006 exited 1.

Full stdout/stderr and command records are preserved under `C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner\001..006-*`. The failing fixture stderr (`006-stderr.txt`) SHA256 is `0E2C63F4C99DF64B5413C8D54A3CBDFA3D060E645A0204C0153CE1711F19EAE2`; command record SHA256 `6CABC7DD4803C16DF2B6B961A36DB26F95B388190E5DCA1617490255184D7E3B`; runner-level stderr with the exception and failure location SHA256 `3939409D9B7C81BC9F68B8BEA1DE3C0C0B7E329A94BBF972622F2EB79EDFF156`. Runner-level stdout was empty (SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`). Launch provenance is `bench/.build/B07-stage1-launch-provenance.txt` (SHA256 `4BE9EF22683B60B59CD61A4B18D605E6C21555BFC949E9DEC067541BCC4EBDD3`). The failed run's output tree is retained intact.

Result: 13 fixture tests passed, one fixture errored, zero native arms attempted, zero skips reported by the fixture runner, no performance result, and no full-suite launch. Further B07 work awaits review of the frozen adapter/fixture error.

## Saved-output audit validation

The eight existing `report.json` and `provenance.json` pairs were validated against the corresponding root-reviewed `B07-native-audits/<arm>.json`. The validator exited 0 for all eight arms. Each result reports `numeric_pass=true`, `identity_pass=true`, `compiler_enclosure_established=true`, `diagnostic_accepted=true`, empty numeric/identity/native error arrays, and `performance_eligible=false`. Validation used the saved artifacts only; no native task, fixture gate, or build was repeated.

The validator, saved reports, provenance, and audits are identified in each per-arm `command.json` under `integration/bench/.build/B07-saved-validation/<arm>/`; stdout and stderr logs are retained beside it. Validator output SHA-256:

| Arm | Result SHA-256 |
|---|---|
| cpp-licensed | `632830E461939DCBE5FD7799F8E33EA794E5CF3D4907A4C700296D395B4112CC` |
| cpp-repro | `A0DEDDEA7AB1686C9BA3725389CDD5EDDEFC49ACF25727FA4A4C902667CD9535` |
| llvm-licensed | `F1E23A901B4DD62EFEAC966DDF7DA7351D340AB168AEAA6310EBF26A2F41F60C` |
| llvm-repro | `2246638109600B1AC28910D2C754C422B07CC39922914F26C84907EDEAEF13FB` |
| natural-licensed | `E37F15AF0674DB5003AF83273745EAF16730EC27E07722C4849DF2F31BC8EE27` |
| natural-repro | `ADACB8E70B0435D9A6EAEE8C5745BB0C0321404F5B5CB99AB21D907B12A33F66` |
| expert-licensed | `8A2B8FE54F422578FA9B80244A16816EEF6352622B92F0426100A4A5179836AC` |
| expert-repro | `32B816730E54E53752E2DBC9D676F40F8AAD382F8112DB298CF75D1D5521F186` |

## One prepared-mutation LLVM repro arm

After all eight canonical saved-output validators passed and the D08 Zarr category completed, the single authorized `llvm-repro` prepared-mutation arm ran from the frozen `b-measurement` checkout at `2a5c2e26a6925b126aa9eafd8e0331068bf4ca3d`. The proposal hash is `571C3C3B8ED943D55844A1291BAEE2C6ADE95CFEF7A7F3FFE0DBBED0359BC1E6`; the 11 relevant runner/adapter/fixture/runtime inputs matched their proposal pins. No existing canonical task was rerun.

A setup-only invocation initially failed because I had created `--out` to hold wrapper logs before starting `runner.py`, which requires the output root to be absent. The runner exited at its root `mkdir` before fixtures or native work. Its logs are preserved in `b-measurement/bench/.build/B07-prepared-llvm-repro-a1-setup-failure`; stdout was empty, and stderr records `FileExistsError`. I moved that owned setup directory aside and reran the same authorized command with stdout/stderr and initial TEMP/TMP in adjacent private paths, leaving the requested output root absent for the runner to create. The setup failure is not counted as a fixture or native attempt.

The corrected runner invocation exited 0. Its embedded pure fixtures passed 14/14. The stage status records exactly one native process, task call and timed interval, `prepared_mutation=true`, `native_review="unmeasured"`, and `performance_eligible=false`. The actual report records task status 0, 14 assertions, 0 failures, `canonical_bad=0`, all 2,053 `p` and `q` cells, checksums `3718.23948049707` and `2031.88822963322`, five observer calls, six cleanup calls and cleanup status 0. The initial numeric validator had numeric/identity checks true while enclosure was null and acceptance false. After root reviewed the new callback/store/checksum/observer/cleanup evidence, the authorized saved-output validator passed with enclosure and diagnostic acceptance true. Performance eligibility remains false.

Key artifacts in `b-measurement/bench/.build/B07-prepared-llvm-repro-a1/llvm-repro/`: prepared IR `adapted/task-prepared.ll` SHA-256 `4ED2056D492E77804963A96EFC12C3502A4CF17015FB8B7D0CF2349FD3B63F37`; task object `D34BCDCA9D97C20F58EBDA32DEBBD0BA95DC136C3B44E496274A3326DE71A3C2`; driver object `6AE09E1605D2620D623A5B1E0DC716F1C4E2219763CC5066089D1DDC5BC61637`; observer object `F15FFAED35A59CE33DD5B4915FD5D3E8061D8FFB906EC82C1ED172E51CDFC2E7`; shim object `F07BFFE04BEAE5D648822F9919BB6CFA35FCE8F85C5D92D320EEA1B679C78131`; executable `42FF2E2EBEFC9637A6805917D5A52D2056E46E8DE648C98C5887EA4315F68BE2`; report `C88DFEE4D6C00D411C960C042163105E3BB9F7D17DA93A9D32956F2FDDA264BC`; provenance `4EDC96EE3F528FD45E409BFB110697A31EF76D3916E06052AF315CE720E876D2`; numeric validation `3F908326BEE14CAAAF64E874950FCA31C3AF6C2E01C9A9B1B3D370A28D7F7D7C`. Task object disassembly is retained as `015-stdout.txt` and executable disassembly as `027-stdout.txt`; the executable invocation and exit 0 are in `028-command.json` (the program emitted its report file, so its stdout/stderr are empty). The complete per-child commands and outputs are numbered `001`–`029` in the arm/output root; the outer wrapper stdout/stderr are empty and the invocation record is in sibling `B07-prepared-llvm-repro-a1-invocation` (invocation SHA-256 `B1330054E1815DAA3BEEC5796EC87EB5A7030765B253A43EFC84727165FEB656`). Tool versions and hashes are recorded in `provenance.json`.

The prepared output and saved-output validation are accepted for this diagnostic. No performance claim, full suite, or benchmark was run.

### Prepared-arm saved-output validation

Root supplied `prepared-llvm-repro.json` (SHA-256 `59CE01899F71F2C918468F6DF6C3F36F4EC91FA7F5A7E23E33AD0EC894AFFF04`) and review `B07-prepared-native-review.md` (SHA-256 `CDE38F1180BB73628EBB611D07197DC2C3D2F084289AD6B75EE7B7890A3E7D6D`). The saved validator was run once against the existing report/provenance; no native process was repeated. Result: numeric pass, identity pass, compiler enclosure established, diagnostic accepted, empty error arrays, and performance eligibility false. `audited-validation.json` SHA-256 `82D3BE4EA13EE0C0095964676D6708966F968A061357C5BCBD5073262172958D`; command/provenance is in `saved-validation-command.txt` SHA-256 `57BC4748EA2E645401B1FC8BE4CF3FEA48F8EEFFEF17B819754EDE271414E821`; validator stdout/stderr are empty. The existing prepared `report.json` and `provenance.json` identities remain unchanged.
