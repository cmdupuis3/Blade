VERDICT: MERGE AFTER FIXES

Read-only review of 66e04875..142ee9d7. Read bench-fortran/CLAUDE.md first. No builds, emission, tests, program execution, or compiler invocation. Counterexamples below are source traces, not executed reproductions. Existing w13 artifacts/logs were inspected.

1. BLOCKER — Tuple-wide positional forcing precedes the new member-order machinery.

   src/CodeGenBinding.fs:580 runs forceDeferredPositionalReads on the ENTIRE tuple before :601 invokes forceTupleArrayMembers. CodeGenFusion.fs:1637,1664 collects a later member's indexed producer; an earlier IRCompute(IRVar) does not get collected (:1678). Thus the newly supported tuple still reverses its first failure:

   ```blade
   type Row = Idx<1>
   let a: Array<Int64 like Row> = [1]
   let z: Array<Int64 like Row> = [0]
   let first = method_for(z) <@> lambda(v) -> 1 / v
   let later = method_for(a) <@> lambda(v) -> a((v : Row))
   let t = (first |> compute, later(0))
   ```

   Interpreter: Core.fs:642 evaluates member 1, Loops.fs:2348 forces it: BL8013. C++: the pre-pass materializes later first: BL8006. The base could not declare first for the tuple; this patch makes that shape executable with the wrong first failure. tuples/037 misses this because its scalar member reads only stored arrays.

   Fix: dispatch tuple evaluation before the whole-expression pre-pass; force dependencies and bind evaluated leaves at their own left-to-right positions. Cover the enclosing genLetChainBinding pre-pass (:6517) too. Do not stop sequencing at lastForced (:718): two evaluating scalar leaves AFTER the last forced member still reach std::make_tuple with C++17's unspecified argument order. Pin the probe above (BL8013), its reversed-member control (BL8006), and a safe computed first member followed by a failing cast and a failing integer division (cast first).

2. BLOCKER — A bare deferred tuple member is silently turned into compute; this changes first failure, not just printing.

   src/CodeGenBinding.fs:640-644 accepts IRVar without requiring IRCompute; :714 inserts IRCompute. The interpreter's IRTuple arm retains a bare VDeferred member (Core.fs:642-643); force on the enclosing VTuple does not recursively force its members (Loops.fs:274-299).

   ```blade
   type Row = Idx<1>
   let a: Array<Int64 like Row> = [1]
   let z: Array<Int64 like Row> = [0]
   let g = method_for(z) <@> lambda(v) -> 1 / v
   let held = (g, 1.0)
   let bad = a((a(0) : Row))
   ```

   Interpreter: held stores the deferred member; bad fails BL8006 before printing. Patched C++: held computes g and fails BL8013. No writing map, printing limitation, or ambiguous C++ argument order is needed. This C++ acceptance/divergence is INTRODUCED, despite the report grouping it with pre-existing defects. LLVM already doing the eager thing does not establish interpreter parity.

   Fix: restrict this correction to explicit compute members, retaining a clear unsupported path for bare deferred members until their semantics are implemented consistently; alternatively fix the representation/evaluation contract in all lanes. Add the probe with the interpreter's BL8006 and an explicit-compute control with BL8013. Qualify RESULTS.md:2686's bare "(g, 1.0)" closure claim.

3. HIGH — The new Sparse branch has no corpus pin.

   src/CodeGenExpr.fs:2277 deliberately admits Sparse as well as Compound. Sparse DOES have data (nested_array_types.hpp:178-181). Its pool is in supplied-key order, not hash iteration or sorted-key order (index_types.h:494-504); sparseToDense preserves that order (Interp/ArrayOps.fs:1869). The access is justified, but a scratch probe does not pin this newly claimed behavior.

   Fix: promote w13/z5_sparse_setops.blade to corpus and add union. With static keys [3,7,1,9] and values [5,2,5,2], pin unique(F)=[5,2], intersect(F,[2,8])=[2], union(F,[2,8])=[5,2,8], plus intersect([2,5],F)=[2,5]. Include an empty-input control. Check C++/interpreter parity; LLVM currently refuses these set-op nodes.

4. MEDIUM — The committed lane account conflates CLI fallback with differential skips.

   bench/notes/gap-register.md:171-173 says 014/015/009/012 "fall back to C++"; it also omits dense control 010. That describes CLI behavior (CliCommands.fs:405-414), NOT test llvm. LlvmTests.fs:2016-2017 returns SkippedCase on refusal; :2058-2060 writes/compiles the accepted LLVM text directly.

   Fix the note: sql-masks/014 and /015, sql-unique-contains/009 and /010, sql-set-ops/012 are LLVM SKIPS. sql-masks/016 and tuples/034-037 actually PASS LLVM in the saved gates-final logs. NONE of those passes is C++ fallback. No extra corpus lane pin is needed to prevent fallback in this harness; require explicit LLVM emission success if claiming enduring LLVM coverage rather than merely "0 failed". w13/lanes.ps1 uses BLADE_LLVM=1 blade run and CAN fall back: its results need backend verification before being called LLVM evidence.

Evaluation count / storage / compound order:

- No changed writing-producer count established for the three requested sharing patterns. Lowering.fs:2726-2785 forces recognized named writing maps and array choice/fallback values at their definitions (join-only readers are separately refused). Two tuple readers, a member's captured producer also appearing as a member, and compound data/mask sharing that writer therefore consume stored cells. forceDeferredArrayInput removes an actually forced id (:2535); the new compound helper retains both contexts (:2584-2586); tuple members are rechecked against the threaded context (:713).
- tuples/036's gw counter=6 is precisely G45: six canonical stored cells of the compact 3x3 symmetric array, evaluated once at gw's definition. It does NOT independently exercise G50 choice/fallback. Its tw tuple has an already stored gw, so it does not test the new unforced-member branch. Extend it with (gw |> compute, gw |> compute), and with a pure computed member capturing gw alongside gw; seen must remain 6.
- An explicit (g |> compute, ...) member gets a compiler temporary; pure g can remain deferred, matching forceExpr's non-memoizing explicit-compute path. A capture-forced g is stored under its own name and retained for later reads/printing. No named output is deliberately removed. Bare (g, ...) is the exception in finding 2: it adds a store/evaluation the interpreter does not perform there.
- Compound emits densePre THEN maskPre (CodeGenBinding.fs:2341-2342), matching materializeCompoundBinding (Interp/Loops.fs:1780-1781). For the requested competing-failure probe, use Row=Idx<1>, a=[1], and an empty Int64 array: data operand "method_for(a) <@> lambda(v) -> a((v : Row))"; mask operand "method_for(a) <@> lambda(v) -> reduce(empty, (+)) > v". Both source paths select data's BL8006 before mask's BL8003. No contrary compound-order defect found. Add this abort pin and a valid-data control selecting BL8003.
- Compound set-op order is compact buffer order: compoundToDense (:1713) copies the same cells. IndexTypes.Length=1 excludes trailing axes, so trailing_stride=1 and cardinality is the correct count. Rank-1 kept-position construction and fused fill preserve ascending kept order; the higher-rank compound index uses lexicographic order. The set-op walk never reads positions.
- T4b dependency: preserve idx->cardinality and the stable compact data[0..cardinality) sequence when omitting positions; never substitute dense extent or a zero position-table length for cardinality. At THIS commit normal rank-1 constructors build positions; the adopting constructor can structurally accept null, but that is not an implemented demand-certified no-position path. T4b must prohibit keyed/unhash/range consumers from reaching an omitted table and pin set-ops on an actually elided representation. This helper itself needs no positions.

Five hook checks (all repository lines at 142ee9d7):

| Claim | Checked hook | Result |
|---|---|---|
| Computed compound operands defer, then force with retained context | CodeGenBinding.fs:2570,2584-2586; forceDeferredArrayInput:2527-2542 | Confirmed; dense-before-mask emission at :2341-2342. |
| Both set-op passes use compact count/cells | CodeGenExpr.fs:2274-2279,2297-2315,2353-2380; Interp/ArrayOps.fs:1713,1869 | Confirmed; Sparse uses key order. No position-table dependency. |
| Fixture 02 and the residual explicit-compute tuple share the undeclared-name site | w13/base_034_tuple_of_computes_from_block.cpp:342; base_036_tuple_holding_unforced_map_over_compact_pool.cpp:297; CodeGenBinding.fs:594-603 | Both are make_tuple of undeclared producers. This proves the shared explicit-compute site, not bare-member semantic equivalence. |
| Tuple forcing preserves first-failure order | CodeGenBinding.fs:580,713-719 versus Interp/Core.fs:642-643 | Incomplete: finding 1 bypasses the helper; finding 2 inserts a new force. |
| Named writer is stored once; captured producer is forced under its name | Lowering.fs:2783-2784; CodeGenBinding.fs:1172-1174,2527-2535; tuples/036:47-58 | G45 control matches the contract. 04b00fe8 also contains loops/524's separate nested-capture correction. |

Reported defects — gap disposition (numbers left for merge):

- Module-level call argument: PARTLY REAL. f(m1 |> compute, 2.0) bypasses CodeGenFusion.note's direct-IRVar test and can name undeclared m1. Bare f(m1) is explicitly covered by :1659 and CodeGenBinding.fs:580; w13/z4_call_arg.blade's earlier failing wrapped call does not establish the later bare call fails. Gap row for wrapped arguments; isolate the bare spelling before claiming it.
- Deferred tuple member (p, 1.0), p=(m1,m2): REAL, pre-existing. p is deferred; unforcedTupleArrayMember only accepts array-typed members. Gap row.
- Tuple of computes inside if: REAL, pre-existing. The new branch requires top-level IRTuple; IRIf still goes through expression rendering. Gap row; LLVM refusal is not coverage.
- Bare deferred array member (g, 1.0): REAL disagreement, but newly accepted with wrong first-failure timing in C++; finding 2 is a merge fix, not merely a pre-existing gap row. Track shared bare-tuple semantics if left unsupported.
- Deferred alias let g2=g printing g only in C++: REAL, pre-existing for assignable array lets. genVarAliasBinding:5728-5731 forces the source before copying; Core.evalBinding:1385-1386 returns a deferred alias before its copy branch. Gap row; distinguish static/immutable aliases, which propagate deferral in C++ too.

Artifacts inspected: w13 under C:/Users/cdupu/AppData/Local/Temp/claude/C--Users-cdupu-Documents-GitHub-Blade/dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b/scratchpad/. Existing gate totals are builder evidence only; this review reran none.
