# A04 bounded sanitizer dispatch

Root reviewed the actual default-scheduler033 native C++/LLVM on/off captures.
Use frozen source52f4bcc3 and existing DLL
CF599009323132F2805139134CF4333C835C3170B6CA859CC7537C5DBE705B03.
No new build, topology census or earlier030/031/032 execution is requested.
Exactly four native runs: two saved LLVM diagnostics and two C++ memcheck mirrors.

Inputs under the A checkout bench/.build/A04-033-default-52f4:
- llvm-reuse1.emitted.ll SHA2560A60A99D3CD348ECE3DCAB1B01AE7363ED73ADFA4247820403FDBF5B8B08D713
- llvm-reuse0.emitted.ll SHA25695FEEE75BC0BEDFD2765F6CAFBBCCA08B65EFED29E96D55D928BD908FCE62B49
- C++ on/off both04494AFE08CD8B09B721A64B988E627537083E970F235F941719070B17C8DC51.

Apply the accepted A03-sanitizer-brief.md clang64 O0 diagnostic pipeline to private
copies of these two LLVM inputs only. The exact ONE replacement is insertion of
`sanitize_address ` immediately after `attributes #0 = { `; ON's original group
contains `mustprogress nofree norecurse nosync nounwind willreturn`, OFF omits
`nofree`. Preserve that distinction and every other byte. Verify one group, no
prior annotation, reverse-byte equality and original/adapted hashes. No body or
other attribute edits. Save checked IR, native object/relocations, shim and linked
EXE hashes. Compile original C shim with ASan and BLADE_POOL_CACHE_OFF=1.

Before execution, prove actual payload __asan_load8/store8 checks in
bl_same_cell_3. ON source GEP r176 and destination r183 share r128/indexr174;
loadr177 must feed fmul/fadd before the store. OFF reads r128 and writes r170,
then frees the original donor once. Check main's reads of x/y/a/b after both
outlined calls. Require corresponding checking-call relocations in those object
function ranges and link exactly that object. Runtime init/shim alone is not
coverage. Unsupported flags or missing checks stop the stage, not a clean pass.

C++ mirrors: original033 corpus source, run --memcheck --verbose --no-cache,
BLADE_LLVM=0, reuse1/0, explicit clang64 clang++, strict repro1/contractoff,
BLASoff, CSE0, OMP1, HOT_ROW/CELL_EPILOGUE/EPILOGUE_LEGS unset, other environment
matching the successful default scheduler invocations. PRINT unset; require all
six pins. Capture actual ASan/cache-off command and asan=1 evidence.

Use new SHORT private temp/output paths and established hidden-process redirected
stdout/stderr capture. ASAN_OPTIONS=detect_leaks=0:halt_on_error=1:abort_on_error=1:symbolize=1
and actual matching llvm-symbolizer. No full suite or performance run. LSan is
unsupported on this Windows runtime; live output allocations are not leaks, and
56-byte payloads do not test cache retention. Stop any unexpected failure and
preserve artifacts. Runner updates A04-tests.md/queue; root reviews before merge.