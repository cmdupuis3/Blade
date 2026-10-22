# B07 prepared LLVM repro: root native review

Root read the fresh027 executable disassembly, four native COFF inventories,
compile/link commands, adapted IR identity and retained report. All addresses
below are RVAs with image base0x140000000. This judgment binds only the new
prepared executable42ff2e2ebefc9637a6805917d5a52d2056e46e8de648c98c5887ea4315f68be2.
Task objectd34bcdca9d97c20f58ebda32debbd0ba95dc136c3b44e496274a3326de71a3c2;
driver6ae09e1605d2620d623a5b1e0dc716f1c4e2219763cc5066089d1ddc5bc61637.
Observer remainsf15ffaed35a59ce33dd5b4915fd5d3e8061d8ffb906ec82c1ed172e51cdfc2e7,
shimf07bffe04beae5d648822f9919bb6cfa35fce8f85c5d92d320eea1b679c78131.
Separate -c/-fno-lto object compilation and object-only linking preserve all
three task/driver/observer boundaries. Native text/relocations are present;
no bitcode/LTO section supplies the executed graph.

Driver QPC(t0)4619 -> actual task1460 call462b -> QPC(t1)463d.
Input x stores complete at15f1 and y at16dc, including index2052.
At16d0..16e6 n=2053, x=rdi, y=rbx are passed to one actual
b07_mutate_prepared call2250. It increments prepared_calls at2250, validates
pointers/count, loads0.125 at2280, adds x+2010 at2288 and stores it at2290.
Root directly read the PE rdata mapping (RVA6000/file4a00): RVA6418/file4e18
contains binary64 0.125. No callback merely alters a checksum or output.

Only after callback return is p allocated at16f5 into r14. Its live memory
loads begin1710, followed by separate multiply/add and store1758 onwards.
At loop index1084 the first vector covers x[1024..1027], including changed1026;
the old canonical value is not regenerated or held across the opaque call.
The p loop1710..18aa covers0..2047; vector store18ca covers2048..2051 and
scalar store18eb covers2052. q is separately allocated18f7, loads retained p
from1910, squares and stores1948 onwards; loop ends1a6b, vector1a83 and
scalar1a98 cover the final five cells. Both complete buffers are retained.

The q checksum is one ordered accumulator from1ab0 through1bd1; its completed
value is stored to outgoing stack30 at1cbf. x/y ordered accumulators read
their actual memory from1be0 through1d17, then final sx+sy1d1f is stored
to outgoing38 at1d23. Publish1d2c calls21d0, which writes roots16/24/32/40,
n8 and both scalars48/56 (store2215) plus status/ownership. Task returns1d8e.
Thus the callback, affected stores and both final checksums finish inside
the t0/task/t1 interval. Independent verification arithmetic after t1 is
not deferred result computation.

Five observer calls47ed/48d7/496c/49dd/4a35 follow t1. The new driver passes
prepared=1 (for example47cd) to the actual shared observer22a0, whose unchanged
native all-cell load paths and2053 bounds were reviewed in the canonical audit.
The first checks x/y and p/q; later calls preserve the input-mutation,
p-corruption, q-tail-corruption and restored-value controls. JSON calls4aae/
4ad8 read actual output roots through helper1e90 (cell0 load1eaa, later1f2a,
step8,end=base+4028). Output publication does not substitute recomputation.

Four malformed-copy cleanup probes precede real cleanup4c0d and duplicate4c30.
Cleanup1da0 validates2030 before root loads, then calls blade_free at1dc5/
1dcd/1dd5/1ddd and marks released1de5. blade_free2e50 recovers allocation
header at-40, using the matching aligned free/cache paths. Roots remain live
through all observations and JSON reads. Logical release is not an RSS claim.

The independent full-cell/scalar validator reports success for checksum
3718.23948049707 and input_checksum2031.88822963322, exactly one prepared
task/callback/interval, five observers and six cleanup calls. The setup
FileExistsError preceded every fixture/native launch and is retained separately.
Numeric tolerance is1e-12 with unit floor; no bit-identity/performance assertion.
Root accepts this new native enclosure subject to one saved-output audit
validation. Do not execute another task or alter any successful artifact.