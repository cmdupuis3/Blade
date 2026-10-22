# B07 saved native evidence extraction

2026-10-22. Supplemental address/dataflow notes for **root's independent review**,
not final audit acceptance. Only existing provenance, saved disassembly/format/
section text and file identities were read. No program, adapter, compiler,
preprocessor, objdump, validator, test or benchmark was executed for this note.
Frozen source, reports, provenance and audit fields remain unchanged.

Evidence root `R` is the absolute directory
`C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B07-runner-a2`.
Each row below refers only to `R/<arm>/boundary.exe` and that arm's provenance.
Addresses below are **hexadecimal RVAs** in the saved executable disassembly:
add image base `0x140000000` (e.g. `d159` means `0x14000d159`). They are static
file addresses, not sampled runtime PCs. Ranges include both endpoints.

## Exact artifact lookup

`D` names the executable disassembly `<number>-stdout.txt` inside the arm directory.
`provenance.json` binds all native object, source, format, section, disassembly,
report and compiler identities and the complete separate compile/link commands.

| arm | D | executable SHA-256 | provenance SHA-256 |
|---|---|---|---|
| cpp-licensed | 023 | 3205b96daa7b5bddc72a03c438e4e104761ce3f7ecfc42384eb3693501888c82 | f9e39c73e22ef4668c5e85d8ae9d2f13aa777849c9475509181b23af2844d070 |
| cpp-repro | 042 | b0c68a94d1cf3afa135ac015467ea5c27a8731072b68340476d7dfee2ff92115 | 2b0c010592324b6313132f73c24bbce4f47a301a1bf324fa24820f339b440fb7 |
| llvm-licensed | 065 | 351c5dc2c7a188941627a4c68ba944dc53f39a826e61b48f2fb94db613c9aaf5 | 6bf22f9dfc3d22297086657ba6233af3c681408f98edee1eae07a7f48c8086fa |
| llvm-repro | 088 | cb3d2084e3012ff229998e1271bf5b4f809cb2783bc0055ffbae637520fd01c6 | f85227b87b9a09ebbfcf51dacc9f333780fa6fcf8311217f77ab027c1a6dfbca |
| natural-licensed | 106 | d8e964dc847a7c1b4b1377fb92a3c1124786425502d4215b293645608065d9e1 | bf07d0d4350213b432ac167997d88d9d8af18ec1b6632c9b074c6d770c9efd6d |
| natural-repro | 124 | e60e6f8817b2350d03196adeca0a13005d54e1f4eaee3a86c0219695eca330ec | 0193e3a794c5eddd1cc731879e232cacb4a5743f744bf4db6d3b8cdc19de9f95 |
| expert-licensed | 142 | 88587e1a6d285f63deec7cb8b1ed0ed80b2c8811d349f8884290921095548e62 | 686d47a813624b674ad313e7be884b81f47d9b53c5b5741d2f13255e3b50a95f |
| expert-repro | 160 | 32313bb282cfe31dfd5162b2a1e35b80625402438c9d586bab7fda218f825020 | 2788ef8a186f7c2ef5943cbe2c9a6d50e258423fdb5e8b7e4de8f06d52db5fbe |

## Object boundary evidence

Triples are saved **format / section inventory / object disassembly** file numbers,
all with suffix `-stdout.txt`. These give targeted independent inspection locations.

| arm | task.o | driver.o | observer.o | shim.o | boundary.exe format/sections/D |
|---|---|---|---|---|---|
| cpp-licensed | 012/013/014 | 015/016/017 | 018/019/020 | none | 021/022/023 |
| cpp-repro | 031/032/033 | 034/035/036 | 037/038/039 | none | 040/041/042 |
| llvm-licensed | 051/052/053 | 054/055/056 | 057/058/059 | 060/061/062 | 063/064/065 |
| llvm-repro | 074/075/076 | 077/078/079 | 080/081/082 | 083/084/085 | 086/087/088 |
| natural-licensed | 095/096/097 | 098/099/100 | 101/102/103 | none | 104/105/106 |
| natural-repro | 113/114/115 | 116/117/118 | 119/120/121 | none | 122/123/124 |
| expert-licensed | 131/132/133 | 134/135/136 | 137/138/139 | none | 140/141/142 |
| expert-repro | 149/150/151 | 152/153/154 | 155/156/157 | none | 158/159/160 |

The saved objects are `pe-x86-64`, architecture `i386:x86-64`, with real .text
instructions, relocations and unwind sections. Section inventories have no
`.gnu.lto*`, `.llvmbc` or `.llvm.lto*` payload sections. LLVM task/shim have
`.llvm_addrsig`; this is an address-significance section in these native COFF
objects, not evidence of bitcode. Executable inventories likewise contain native
PE sections. C++ additionally has runtime/TLS/exception COMDATs; their presence
does not identify an active task computation or capacity measurement.

All eight recorded command lists compile task, driver and observer separately
with `-c`, `-fno-lto`; LLVM separately compiles the shim too. Final links take
those .o paths, contain `-fno-lto`, and take no task/driver source or bitcode input.
Driver object identity is shared across all eight:
`70941f98da8dd710244c248fa615777c59c1aa2e5116302047eccb3f99010327`;
observer object is
`f15ffaed35a59ce33dd5b4915fd5d3e8061d8ffb906ec82c1ed172e51cdfc2e7`.
Actual direct native calls below preserve these boundaries. This is saved evidence
support for the no-LTO route, not approval based on flag spelling alone.

## t0 -> task -> t1, observation -> release

`main` loads the QueryPerformanceCounter import into rdi, supplies distinct stack
timestamp addresses, calls through rdi for t0, directly calls the task with the
descriptor at rsp+0x130, then calls through the same rdi for t1 (rsp+0x118).
Only after successful t1 does it validate/read the published descriptor and begin
independent arithmetic/observation. Inspect each D at the following RVAs.

| arm | main | QPC t0 / actual task call / QPC t1 | task entry | five observer call span (first..last) | full-array JSON calls p / q | real cleanup call / duplicate call |
|---|---|---|---|---|---|---|
| cpp-licensed | d080 | d159 / d16b / d17d | 2ce0 | d315..d551 | d5ca / d5f4 | d729 / d74c |
| cpp-repro | d080 | d159 / d16b / d17d | 2d00 | d315..d551 | d5ca / d5f4 | d729 / d74c |
| llvm-licensed | 4620 | 46f9 / 470b / 471d | 1460 | 48b5..4af1 | 4b6a / 4b94 | 4cc9 / 4cec |
| llvm-repro | 4530 | 4609 / 461b / 462d | 1460 | 47c5..4a01 | 4a7a / 4aa4 | 4bd9 / 4bfc |
| natural-licensed | 3c00 | 3cd9 / 3ceb / 3cfd | 2020 | 3e95..40d1 | 414a / 4174 | 42a9 / 42cc |
| natural-repro | 3be0 | 3cb9 / 3ccb / 3cdd | 2040 | 3e75..40b1 | 412a / 4154 | 4289 / 42ac |
| expert-licensed | 3be0 | 3cb9 / 3ccb / 3cdd | 2040 | 3e75..40b1 | 412a / 4154 | 4289 / 42ac |
| expert-repro | 3ba0 | 3c79 / 3c8b / 3c9d | 2040 | 3e35..4071 | 40ea / 4114 | 4249 / 426c |

For every main, the five observer calls occur at main+0x295, +0x37c, +0x40e,
+0x47c, +0x4d1. The first/final request input checks; the middle three check stored
p/q after input mutation, p corruption and q corruption. The native driver stores
restored x/y before the p corruption and restores p and q before the final call
(C++ D: d451/d465, d4d3, d544). The corrupt-cell calls are external, their returned
bad counts are tested against one; this is not a source-only marker.

Four malformed-descriptor cleanup probes precede the real call; duplicate cleanup
follows it. The extra cleanup call in each main's error block (e.g. cpp `db52`)
is the failed-output-file/error route, not a
seventh call on the successful path. The two JSON calls pass descriptor p/q roots
(rsp+0x150/+0x158), and finish before t2 at main+0x581, well before real release.

## Required native stores, including cell 2052

`0x4020 = 2052*8`; `0x4028 = 2053*8`. GCC vector loops below start at byte offset
zero, step 0x20 and stop when offset reaches 0x4020: they store cells 0..2051,
then explicitly store cell 2052. LLVM's unrolled loop starts at cell index 0x3c,
uses 16 adjacent four-double stores with offsets -0x1e0..0, steps 0x40 cells and
stops at 0x83c: cells 0..2047. Its vector epilogue stores cells 2048..2051 at
+0x4000, then the scalar epilogue stores 2052 at +0x4020. Thus a vector marker
alone is not the reasoning for complete coverage.

| arm | p computation/store loop and odd tail | q computation/store loop and odd tail | output root flow |
|---|---|---|---|
| cpp-licensed | producer helper 20a0..20bb (`vfmadd213pd`, store 20ab); tail FMA 20cd, store 20e4 | 3860..3879 (`vmulpd`, store 3869); tail square 389e, store 38a7 | task calls producer at 3541; p is rdi and q r15 at publication |
| cpp-repro | producer helper 20c0..20da (multiply then add, store 20ca); tail store 2103 | 3880..389b (store 388a); tail square 38bc, store 38c4 | task calls producer at 355f; p is r12 and q r13 at publication |
| llvm-licensed | 1700..18aa FMA/store unroll; vector tail 18e4, scalar tail 18ed | 1910..1a6b square/store unroll; vector tail 1a8f, scalar tail 1aa4 | p r14, q rax, passed as r9 and stack+0x20 |
| llvm-repro | 1700..189a separate multiply/add/store; vector tail 18ba, scalar tail 18db | 1900..1a5b square/store unroll; vector tail 1a73, scalar tail 1a88 | same p r14 / q rax publication flow |
| natural-licensed | 2320..233b FMA, p store 232b; tail 235d | 2380..2399 square, q store 238a; tail 23ad | x rsi, y r8, p r9, q rdi; two distinct output passes |
| natural-repro | 2320..233b multiply/add, p store 232b; tail 235c | 2380..2399 square, q store 238a; tail 23ad | same distinct roots/passes |
| expert-licensed | shared 2340..2368 loop: FMA 2345, p store 234f; tail 2395 | square 234b, distinct q store 2355; tail 23a2 | p r9 / q rdi; scalar forwarding retains both stores |
| expert-repro | shared 2330..2373 loop: multiply/add, p store 233f; tail 2394 | square 233b, distinct q store 234d; tail 23a9 | p r9 / q r10; ordered scalar checksum additions interleave vector stores |

The C++ p helper really executes on the task's valid path; it is not the unrelated
standalone consumer helper also present in the object. q's active stores are the
ones in b07_pm52_task listed above. LLVM p/q allocation calls return distinct roots;
Fortran's four malloc results enter its saved descriptors. Result publication roots
match the destinations above; the observer subsequently loads these roots, rather
than a regenerated or separately printed clone.

## Completed checksum dataflow before publication/return

On Win64, callers put q, n, checksum and input_checksum in outgoing stack slots
+0x20, +0x28, +0x30, +0x38 respectively. The scalar values must already exist to
make this external call. In shared C++ publisher 4f50..4f9a, the two incoming scalar
slots are loaded (callee rsp+0x38/+0x40), then stored into descriptor +0x30/+0x38
at 4f95 before ret. The relocated publishers in other arms have the same driver
object dataflow. This links the arithmetic below to the actual observed fields.

| arm | checksum body -> completed checksum -> outgoing +0x30 store | x/y folds and final sx+sy -> outgoing +0x38 store | publish call -> normal task return |
|---|---|---|---|
| cpp-licensed | 38c0..3940, including q tail/lane merge -> 39b2 | 3960..39fe (x r12/y r13 and explicit tails); final add 39fe -> 3a02 | 3a0b -> shared epilogue 2d04..2d17 |
| cpp-repro | serial chain 38e0..3900, tail in xmm1 -> 398d | 3940..3982 ordered x/y chains/tails; final add 39aa -> 39ae | 39b7 -> shared epilogue 2d24 |
| llvm-licensed | 1ab0..1c31, final q[2052] addition 1c31 -> 1d61 | 1c40..1df5, tails and lane merges; final add 1dfd -> 1e01 | 1e0a -> 1e6c |
| llvm-repro | ordered 1aa0..1bc1, last q tail addition 1bc1 -> 1caf | ordered 1bd0..1d07; final sx+sy 1d0f -> 1d13 | 1d1c -> 1d7e |
| natural-licensed | q readback 23c0..23f5; completed bits held in rdx -> 2444 | 2420..2499, with saved input tails; final add 249d -> 24a1 | 24aa -> 24c9 |
| natural-repro | ordered q readback 23c0..23dc -> 2442 | 2400..243e, with input tails; final add 245c -> 2460 | 2469 -> 2482 |
| expert-licensed | forwarded q values accumulate in 2340..23c8 -> 2405 | 23e0..2469, with saved input tails; final add 246d -> 2471 | 247a -> 2499 |
| expert-repro | ordered per-cell q additions 2345..23a1 -> 2402 | 23c0..23fe, with input tails; final add 241c -> 2420 | 2429 -> 2442 |

Licensed code uses the backend's permitted vector/lane association and contraction;
repro uses ordered scalar accumulator chains and separate multiply/add in producer.
These address notes do not assert cross-backend byte identity. The late scalar
work remains **inside the task call** in these saved adapted artifacts; independent
reference arithmetic in main after t1 serves verification, not the published values.

## Actual post-t1 observation and allocator-matched cleanup

Observer entry loads p from descriptor+0x20 into rbp and q from +0x28 into r12.
Its loops start r15=0 and load `[rbp+r15*8]` / `[r12+r15*8]`, feed finite/tolerance
comparisons and mismatch accumulation, increment r15 by one, and branch until
r15==0x805. Both the output-only and input-check paths visit every cell through
2052 without returning early for a mismatch. Object-relative load offsets are
+0x1ac/+0x1b6 and +0x412/+0x41c, bounds +0x283/+0x5ba.

| arm | observer entry | output-only p / q loads | input-check p / q loads | loop bounds (both paths) | JSON helper entry |
|---|---|---|---|---|---|
| cpp-licensed | 5020 | 51cc / 51d6 | 5432 / 543c | 52a3 / 55da | 4c10 |
| cpp-repro | 5020 | 51cc / 51d6 | 5432 / 543c | 52a3 / 55da | 4c10 |
| llvm-licensed | 2380 | 252c / 2536 | 2792 / 279c | 2603 / 293a | 1f70 |
| llvm-repro | 2290 | 243c / 2446 | 26a2 / 26ac | 2513 / 284a | 1e80 |
| natural-licensed | 2a40 | 2bec / 2bf6 | 2e52 / 2e5c | 2cc3 / 2ffa | 2630 |
| natural-repro | 2a20 | 2bcc / 2bd6 | 2e32 / 2e3c | 2ca3 / 2fda | 2610 |
| expert-licensed | 2a20 | 2bcc / 2bd6 | 2e32 / 2e3c | 2ca3 / 2fda | 2610 |
| expert-repro | 29e0 | 2b8c / 2b96 | 2df2 / 2dfc | 2c63 / 2f9a | 25d0 |

For JSON helper entry A, A+0x1a loads cell zero; A+0x9a loads successive actual
buffer cells; A+0x61 constructs end=base+0x4028 and the loop advances by eight.
The helper passes loaded bits to fprintf (or null on nonfinite), not regenerated
R5 output. Main's two calls use the actual retained roots before cleanup. Some
LLVM-linked local helper names are absent: D labels the same real code as offsets
from b07_pm52_cleanup; use addresses/dataflow rather than that misleading nearest
symbol label.

| arm | cleanup entry and gate | four allocator endpoints on valid path | mark released / return |
|---|---|---|---|
| cpp-licensed | 42c0, validation call 42d1 | root loads 42f0/4321/4352/4383; typed TLS cache branches or delete[] calls 431c/434d/437e/43af | 43b7 / 43cc |
| cpp-repro | 42a0, validation call 42b1 | root loads 42d0/4301/4332/4363; typed TLS cache branches or delete[] calls 42fc/432d/435e/438f | 4397 / 43ac |
| llvm-licensed | 1e80, validation call 1e8c | blade_free calls 1ea5/1ead/1eb5/1ebd, roots +0x10/+0x18/+0x20/+0x28 | 1ec5 / 1edc |
| llvm-repro | 1d90, validation call 1d9c | blade_free calls 1db5/1dbd/1dc5/1dcd, same roots | 1dd5 / 1dec |
| natural-licensed | 1e50, validation call 1e58 | root/saved-descriptor identity gates 1eba..1f20, frees 1f3b/1f57/1f77/1f93 | 1fb0 / 1fca |
| natural-repro | 1e70, validation call 1e78 | root/saved-descriptor identity gates 1eda..1f40, frees 1f5b/1f77/1f97/1fb3 | 1fd0 / 1fea |
| expert-licensed | 1e70, validation call 1e78 | same roots/free call sites as natural-repro | 1fd0 / 1fea |
| expert-repro | 1e70, validation call 1e78 | same roots/free call sites as natural-repro, relocated free import | 1fd0 / 1fea |

C++ cleanup is typed count-aware pool release, not always a direct delete. For
example cpp-licensed 44b8 adds 0x4028 bytes; 44e4 stores the original root into the
TLS cache, 44ec stores its double type tag and 44fc stores its count/size metadata,
then rejoins the next root. Four valid roots are consumed before mark_released.
LLVM blade_free entry 2f30 (licensed) / 2e40 (repro) reads the allocation's -0x40
header and recovers the aligned raw base; it caches qualifying blocks or dispatches
to the matching aligned-free import. Fortran compares against its saved malloc
roots before free and clears its saved descriptors. These branches explain matched
logical release; they do **not** establish an immediate RSS decline or retained
byte measurement. Invalid-descriptor gates return before root release.

All notes concern the eight canonical prepared_mutation=false artifacts only.
There is no pre-producer mutation execution evidence here, no sanitizer/lifetime
proof for arbitrary inputs, no production-emitter admission and no performance
claim. Root must independently inspect these targeted raw ranges and decide audit
acceptance; this note does not modify null audit gates or saved validation results.
