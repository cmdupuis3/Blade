# A05 raw-route adapter: separate frozen diagnostic

Source-only adapter over reviewed repair `b9df5a9bb23b116c66e17155927f70412b100efa`. Coder has not executed it. It is not part of the production fsproj, fixtures or primary implementation manifest; those files are unchanged. Root reports the primary gate passed (152 facts, 730 optimize); affected categories are separately queued. Root reviews this adapter/manifest before dispatch. Use only the fresh source-matching DLL accepted at that primary gate. A DLL hash argument is mandatory, and the adapter also rejects an assembly lacking the A05 gate registry. Do not use the original census DLL or build another compiler for this request.

The adapter supports only four pinned original input files. It checks their frozen SHA256 values. For 034/035 it saves the exact original file and removes only the TEST header from its compiler input, matching Corpus.parseBladeFile and the primary facts helpers. Therefore 035's first failure site is harness line7; a direct original file run would use line8. The caller/later census files retain all original source lines. No fixture or pin is rewritten. BLADE_PRINT stays unset: every original array/scalar demand remains present.

After clearing every Optimize.optimizerGates entry to its default, the adapter sets **BLADE_POOL_EARLY_RELEASE explicitly to the arm's 1/0 value**. BLADE_POOL_REUSE is explicitly **1 in both arms**; it is not the experimental toggle. BLADE_LLVM is explicitly **off**, while the adapter still invokes the direct LLVM emitter itself. Fusion, CSE and competing schedulers stay unset/default. Strict numeric pins: FP_REPRO=1, FP_CONTRACT=off, BLAS=off, OMP_THREADS=1/OMP_NUM_THREADS=1; tile/exe caches off, memcheck unset. The complete selected environment is saved in each arm. No build, native compile/run, interpreter, harness category, sanitizer, pin generation or benchmark is part of this adapter.

One fresh private output directory per process under `bench/.build/A05-implementation-runner/raw-route`; do not overwrite the original census logs or any earlier attempt. Luna records absolute tool path/version/DLL hash, command, adapter/manifest hashes, private TEMP/TMP, environment, stdout/stderr and exit status. Command shape:

```powershell
dotnet fsi "--reference:$a05Dll" --exec $a05Adapter $a05Case $a05NewOutput $a05EarlyGate $a05ApprovedDllSha
```

Root-approved bounded order, stopping on unexpected error/refusal/shape:

| Process | Case | Early gate | Expected observation, not a passing fallback |
| --- | --- | --- | --- |
| 1 | 034 | 1 | One common early-release admission; same original 7/11 pools, p free once after snapshots and before final result allocation. |
| 2 | 034 | 0 | No applied early schedule; p cleanup after final fill. Original expression/dataflow remains. |
| 3 | caller | 1 | No owned local p/free or early admission; only the fresh returned result in the ordinary function. |
| 4 | later | 1 | No early admission. C++ tuple reads p(6) after result fill; inspect LLVM's actual flattened-main route without claiming a common tuple ABI. |
| 5 | 035 | 1 | No early admission for no_early; both failure sites remain in original order, first at harness line7. Emission only, so abort behavior is not established here. |
| 6 | 035 | 0 | Same failure-source ordering without early scheduling. |

Maximum six lower/validate processes and twelve raw backend emissions. Each process lowers once, validates once, then emits C++ followed by direct LLVM from the **same validated tree and live planner tables**. It never lowers between emissions or resets planner state. Snapshots before, after C++ and after LLVM record ordinary emission handshakes rather than pretending every mutable plan record is byte-immutable. Complete generic donation maps/current-return state are captured **immediately after each emitter** and compared separately with the original tuple; a C++ mutation cannot hide by reverting during LLVM. Serialized IR must also remain unchanged. C++ raw text, full warnings/settled CodeGenState.takeCodegenRefusalDiagnostics diagnostics, decisions, immediate donation tuple and ownership/IR snapshots are saved before checking its gate. Any settled refusal, #error, BLADE_CODEGEN_ERROR marker or C++ IR/donor mutation stops **before invoking LLVM**. LLVM outputs/state are likewise saved before its final invariant check. Artifact hashes are saved in a finally block on success or failure.

`ir.*.full.txt` uses the established C04 reflected dump with explicit SrcLoc.Span handling and round-trip float bits. It has no collection/depth truncation or <expr> fallback. It serializes all actual modules, declarations, types, operands, captures, functions, event IDs, metadata and side payloads; unsupported dump types fail visibly. Lowering and each backend have separate complete decision records and rendered text, so spans/evidence/subjects are not truncated by F# %A. Diagnostic/warning records are also saved fully.

Ownership inventories query every integer-valued field encountered in actual IR (IRId aliases int), hence an **over-approximation** including all real IDs in types/metadata/captures as well as incidental ranks/span positions. Complete returned plan objects are saved for those IDs, plus all donation keys/values/current return, all membership plans and all nonempty orbit audits over the inventory. Owner tuple field order is in owner-schema.txt. This is exhaustive for the relevant IR identity inventory; it does not claim enumeration of unreachable private table entries. Planner ownership is still distinct from actual emitted allocation ownership: inspect arm.cpp/arm.ll for the exact backing/slot/read/free/return traces required by the existing implementation request.

For 034 compare arithmetic SSA operands/dataflow and flags, p's actual root/slot/null initialization/store/load/free, absence of duplicate cleanup/later aliases, fresh result/return and all caller demands after two calls. Check nofree is absent from any function that frees. Count payload sites separately from extents, scalar slots and dynamic calls; no logical-peak, retention, RSS or timing claim. For controls save actual refusal subjects/reasons and raw route, including any changed ABI. `invariants.txt` must show all true; `artifact-sha256.txt` lists successful output hashes. Any FSI type/API/dump error is returned for source-only repair; Luna must not edit the adapter or silently omit evidence.

Root reviews these artifacts before native/categories/sanitizer stages in A05-implementation-request.md. Full-suite launches remain zero. Stop after this bounded diagnostic; no next kernel.
