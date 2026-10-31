# Cloud benchmark campaign

Status: DRAFT 2026-10-31. Nothing run, nothing built. D2 DECIDED 2026-10-31: Windows VMs for B1, B2 and B4 (B3 on Windows where the fp64 GPU class has a Windows image, else B3 alone on Linux). D1, D3, D4 open (section 5).

A plan for spending cloud credits on Blade. The question was "CUDA optimization or a
generic benchmark run?"; the answer here is a TARGETED benchmark campaign with one
bounded CUDA block inside it, and no CUDA optimization work. Section 0 says why.

## 0. Why measurement, not CUDA optimization

- The open performance rows are all CPU-side: G8 recur 0.5x, G30/G33 band, G32 mt8
  pairkernel checksum, G34 comoment3 repro checksum (`bench/notes/gap-register.md`,
  `bench/RESULTS.md` §13). The CUDA lane has no open row because nothing measures it.
- The standing CUDA verdict (2026-08-29, three reasons) was partly HARDWARE: the local
  GTX 1650 runs fp64 at 1/32 rate, so the CPU was the faster fp64 device. That reason does
  not transfer to a datacenter GPU with full-rate fp64. The other two reasons (the gate is
  reachable only through `blade run --cuda` / `blade test --cuda`; device bodies refuse
  recursion and captures) are design facts the credits cannot change. So the honest use of
  a GPU instance is to RE-TEST the verdict's hardware leg with a few numbers, not to start
  an optimization round against a lane whose doctrine questions are unanswered.
- An optimization round needs the A/B discipline the box already provides (interleaved
  rounds, medians, quiet machine, `bench/results/stats.csv`). Credits do not make that
  cheaper; they buy hardware shapes the box does not have.

## 1. What the credits buy that the box cannot

| Shape | Why it matters | Which block |
|---|---|---|
| 32-64 physical cores, one socket or two | Every thread form (`where omp` Path B chunks, row slots, the ordered orbit scatter's row-owned pull, the threaded `let rec` step, the reduction join team) was tuned and gated at mt8. The static work gates `rowslots_min` and `pull_min` are PROVISIONAL (operation counts, no timing). A scaling curve says where they bend | B2 |
| A second CPU family | Every number so far is one Zen 3 laptop (`LAPTOP-350H6LDB`). `-march=native` resolves to one ISA; the tile budgets (`antisymRowGroup`'s 256 KiB = half of Zen 3's L2) are that box's. Cross-machine replication is what a paper claim needs | B1 |
| A quiet, dedicated machine | The box's matrix carried "indicative" verdicts on 7/11 blocks from clock drift and a sibling session's builds; one `find /` burned a core beside an A/B for 3.5 h | B1, B2 |
| 2-8 GPUs with full-rate fp64 | The multi-device paths (section 2) have never run on more than one device. fp64 throughput is the verdict's hardware leg | B3 |

## 2. Facts established before planning

### 2.1 Multi-GPU: what exists, what does not

The question "query the GPU count and divide `where cuda` clauses evenly between the
devices" is already answered in two places of `src/CodeGenCuda.fs`, and open in a third:

1. **`<&>` soft-join leaves round-robin across devices (built).** A `<&>` join whose leaves
   are independent `where cuda` kernels that cannot co-fuse (different extents, block
   sizes or arities) emits one kernel per leaf with split `_begin` / `_end` wrappers
   (`genCudaKernel` / `genCudaKernelSimplicial`, the `softSplit` arm, lines ~945-971 and
   ~1475-1504). `_begin` does `cudaGetDeviceCount`, `cudaSetDevice(leaf % deviceCount)`,
   H2D staging and an ASYNC launch; `_end` re-selects the device, synchronizes, copies back
   and frees. One device: the default stream serializes the leaves. Several: the begin
   pass overlaps them. So "divide the clauses between the GPUs" IS the semantics, at the
   granularity of join leaves; the device count is queried at run time, nothing is
   annotated (the fastest-way doctrine). Tested by `blade test cuda` (`softRect`,
   `softSimp`, `softMixed`, `softCapture` in `tests/CudaTests.fs`) -- on ONE device only.
2. **MPI ranks each take a device (built).** Under `blade run --mpi N` a cuda kernel's
   rank-scoped wrapper does `cudaSetDevice(rank % deviceCount)` and computes its own cell
   range `[rlo, rhi)`; an empty range returns before touching the device, so an idle rank
   on a GPU-less node is not a failure (lines ~924-944). This IS "one kernel's cells split
   evenly across the devices", but only through MPI ranks (one process per device, and
   today's `--mpi` replicates all storage, `plan-distributed-memory.md`).
3. **One kernel's cells split across devices IN ONE PROCESS (not built).** No streams, no
   per-device chunking of a single launch. The emitted shape it would take is the MPI slab
   form of item 2 with the rank loop inside the wrapper (one stream per device, chunked
   `[lo, hi)` per device, D2H into the shared host buffer). Whether it is worth building is
   exactly what B3 measures: every wrapper today does `cudaMalloc` + H2D + launch + sync +
   D2H + `cudaFree` PER LAUNCH, no persistent device buffers, so PCIe staging bounds every
   kernel the suite has (all <= 64 MB working sets). A split that halves the kernel time
   and doubles the staging is not a win. The plan takes NO position until the overhead
   census of B3.3 is in.

Other facts that bound B3: the CUDA gate is an `AsyncLocal` defaulting OFF, flipped only by
`blade run --cuda` and `blade test --cuda` (no env var, no `compile --cuda`); `omp` and
`cuda` are mutually exclusive at parse time, so a cuda kernel has no CPU thread fallback;
`Build.fs` probes a GPU with `nvidia-smi -L`; the Linux build path (nvcc + g++ host) exists
in `Build.fs` (`UseNvcc`, `PLinux`) but CI runs no `test cuda` lane anywhere and the test
lanes are Windows-only, so the Linux cuda path has NEVER been exercised end to end.

### 2.2 The harness is Windows-bound

`bench/harness/run.py` pins serial arms by `kernel32.SetProcessAffinityMask`, reads peak
working set / peak commit / page faults from Win32 process counters, builds gfortran
through `fortran/build_gfortran.ps1` and ifx through the vcvars64 + `setvars` recipe
(`fortran-toolchain-on-this-box`), and counts cores through `Get-CimInstance`. A Linux port
is mechanical (`os.sched_setaffinity`, `resource.getrusage` for peak RSS -- coarser than
Win32 commit --, bash build wrappers, `setvars.sh`) but it is a day of work plus a
re-validation, and it introduces a CONFOUND the Windows VM does not: the distro gcc is not
the box's gcc 16.2 (MSYS2 ucrt64), so a gfortran delta could be a compiler-version delta.
Decision D2.

### 2.3 The thread regime is `mt8` by name

`Regime.parse` accepts `<serial|mt8>-<fp>-<blas>`; `MIN_ROUNDS`, `rounds` in `kernel.json`,
`run_env` and `fortran_flags.*_extra` in `config.json` are keyed `serial` / `mt8`; the
Blade mt8 build is `BLADE_OMP_THREADS` unset + `OMP_NUM_THREADS=8`, unbound (libgomp cannot
bind on Windows). B2 needs `mt<N>` as a regime family (section 4.1). The Fortran arms need
only `OMP_NUM_THREADS=N`.

### 2.4 Paper tie-in

`plan-paper-1-comoment.md` lists "a single-commit bench re-run" as open: the §6 numbers
come from several commits of the follow-up round. B1 IS that re-run, on a second machine,
which is strictly more than the paper asked for.

## 3. The blocks

Order: B1 first (it is the one with a consumer waiting), B2 on the same instance in the
same session, B3 on a GPU instance in its own session, B4 only if credits remain.

### B1. Replication run (one commit, the whole matrix, a second machine)

- One master commit, named in the bundle. Build Blade, gfortran 16.2 (MSYS2 ucrt64) and
  ifx 2026.1 on the instance; `run.py smoke --regimes all` first (required smoke evidence);
  then the full protocol order (`licensed x serial`, `licensed x mt8`, `repro x serial`,
  `repro x mt8`, `fastmath`, `blas`) over every kernel in `bench/kernels/` at README
  minimum rounds, with the two drift controls.
- Instance: dedicated or bare-metal, FIXED CPU model, nothing else scheduled on it, a CPU
  family other than Zen 3 (an Intel Sapphire Rapids / Granite Rapids class or a Zen 4/5
  server part both answer the "second family" question; record which). SMT and turbo
  state recorded; the local recipe's power cap has no cloud analogue, so the controls carry
  the drift verdict.
- Output: a session bundle under `bench/results/<date>-<host>/` (manifest, session.json,
  summary.md, sources-sha256.json -- the slimmed set), rows appended to
  `bench/results/stats.csv` with a `label` naming the campaign, and a `RESULTS.md` section
  "§15 second-machine replication" with the headline table beside §13's: the four group-A
  ratios serial-licensed and mt8, the repro ratios, recur / band / maskagg. A ratio that
  moves by more than the verdict delta (10%) between machines is a FINDING with its own
  row, not a footnote.
- NOT in B1: new kernels, knob sweeps, any compiler change. If a kernel fails to build on
  the instance, it is recorded as a gap and skipped; the campaign does not stop to fix it.

### B2. Thread-scaling sweep

- Kernels: the five with thread forms the box could not stress -- `gram` (syrk tile team),
  `antisym` (antisym tile team), `comoment3` (row-owned pull, `pull_min`), `pairkernel`
  (orbit fold / row slots, `rowslots_min`, G32), `stencil` (Path B row-view folds, G51) --
  plus `recur` if `BLADE_REC_STEP_OMP`'s persistent team applies to its source (check the
  plan line first; if the step declines, drop it and say so).
- Grid: `N in {1, 2, 4, 8, 16, 32, <all physical cores>}`, `licensed` and `repro`, `native`,
  Blade C++ lane and both Fortran experts (the LLVM lane has no thread runtime: serial
  only, one row). Blade-llvm and the natural tier are not in the sweep.
- Binding: unbound on Windows (platform limit, as today). On Linux `OMP_PLACES=cores
  OMP_PROC_BIND=close` would be available and SHOULD be recorded as a second arm if D2
  picks Linux; never mix bound and unbound rows in one table.
- Readout, per kernel: a speedup curve with medians and the round-block bootstrap, the
  thread count where the curve bends, and the `blade plan` form at each N (the form does
  not change with N, but the census does: team entry ~40 us on the box, unknown here).
  Two specific questions get yes/no answers: does `pull_min` = 2^18 sit on the right side
  of the comoment3 bend, and does the pairkernel mt8 regression (G32) persist, grow or
  vanish at 32 threads?
- No knob changes land from B2. Its output is numbers in `stats.csv` and, where a gate is
  shown wrong, a gap row proposing the new value with the curve as evidence.

### B3. Bounded CUDA block

A GPU instance with 2-4 devices of full-rate fp64 (A100 / H100 class; an L4 / T4 / consumer
part would repeat the 1/32 leg and prove nothing). The block is SMOKE + WITNESS +
CENSUS; no optimization, no compiler change beyond what a smoke failure forces.

- B3.1 **Lane smoke.** `blade test cuda` on the instance (Windows: nvcc + cl under
  vcvars64 with ucrt64 layered, the local recipe; Linux: the never-exercised nvcc + g++
  path). Expected outcome on Linux: build failures the path has never seen. Each is a
  gap row with the error text; fix only what blocks the rest of B3, in a branch, and record
  the commit the rest ran on.
- B3.2 **Multi-device placement witness.** Three programs, each run with
  `CUDA_VISIBLE_DEVICES` set to 1, 2 and all devices, `nvidia-smi --query-gpu=utilization.gpu,memory.used --format=csv -l 1`
  (or `nsys`) capturing placement:
  (a) a `<&>` join of 4 independent cuda leaves at a size where each leaf runs >= 50 ms --
  the witness that leaf k lands on device k mod D and that the begin pass overlaps (wall
  time at D = 4 vs D = 1); (b) the same with `<&!>` (co-fused, ONE kernel on ONE device --
  the control: no split expected); (c) `blade run --mpi 4 --cuda` of one cuda kernel over
  a rank-scoped range -- the witness that rank r takes device r mod D and that an idle rank
  with an empty range on a device-less node returns cleanly. Each is pinned as a corpus-
  style file under `bench/diagnostics/cuda/` with its expected placement in a comment; the
  run record (`BLADE_RUN_RECORD`) is kept beside the nvidia-smi log.
- B3.3 **Overhead census.** For the gram / antisym / comoment3 shapes at the suite's full
  sizes, time one cuda launch end to end and split it: H2D, kernel (from `nsys` or
  `cudaEvent` pairs added to the wrapper under a `BLADE_CUDA_TIMING` compile define the
  block may add -- a measurement instrument, not a feature), D2H, malloc/free. The ratio
  kernel / total is the one number that decides whether item 3 of section 2.1 (in-process
  device split) can ever pay for these shapes. Report it beside the CPU serial-licensed
  number of the same shape on the GPU instance's own CPU, and beside expert ifx there.
- B3.4 **The verdict's hardware leg.** One table: for the kernels B3.3 covers, GPU kernel
  time alone, GPU end to end, Blade C++ mt<all>, expert ifx mt<all>, on the same instance.
  Write the sentence the table supports and nothing more; the sentence replaces the
  "wrong hardware shape" paragraph of the 2026-08-29 verdict if it no longer holds.
- NOT in B3: cuBLAS routing (`BLADE_CUBLAS`, a separate opt-in with its own gate), any
  change to device-body admission, the in-process split of section 2.1 item 3 (a design
  item for a later plan if B3.3 says the staging share is small).

### B4. Round-4 peak-memory families on a known memory system (optional)

Only if credits remain after B1-B3 and D2 picked Windows (the memory counters are Win32).
Re-run the peakmem kernels (`recur`, `stencil`, `twofir`, `groupagg*`, `hashjoin`,
`tensorop`, `haloadjoint`, `pairaction`) with `BLADE_POOL_RETAIN` default and `1`, and
report peak working set + wall per arm as the knob's row demands. No new families.

## 4. Prerequisites (the only changes that land before the runs)

### 4.1 `mt<N>` regimes in the harness

BUILT 2026-10-31 (uncommitted at writing): `Regime.parse` accepts `serial` or `mt<N>`; kernel.json
declares threaded regimes by FAMILY (`mt-<fp>-<blas>`, the committed `mt8-...` spelling read as
the family, so no kernel.json changed); `config.json` `threads` (default `[8]`) lists the
session's N, `"all"` = the physical core count; `run_env.mt` carries `{n}`; `rounds`, `run_env`
and `*_extra` key by class `serial` / `mt` (legacy `mt8` keys accepted); `regime_sort_key`
orders any N; `bench/harness/test_regimes.py` (22 tests) pins the parser, the order, the
config loader, the kernel.json normalization and the session's regime selection. The original
specification follows.

`Regime.parse` accepts `serial` or `mt<N>` (`N` a positive integer; `mt8` unchanged);
`MIN_ROUNDS`, `rounds`, `run_env` and `fortran_flags.*_extra` key by `serial` / `mt`
(one threaded entry, `OMP_NUM_THREADS` substituted from N); `REGIME_ORDER` takes the
thread list from `config.json` (`"threads": [1, 2, 4, 8, 16, 32, "all"]`, default `[8]`
so today's behaviour is the default); the serial gate (M12) gates every `mt<N>` arm on the
same serial arm; `summary.md` groups by N. Pinned by `test_memory.py`-style harness tests
for the parser and the order. Estimated at a few hours; it lands on master before the
campaign so the bundle's harness hash is a master commit.

### 4.2 Instance fingerprint

`fingerprint()` already records host, OS, CPU string, affinity and `run_env`. Add: the
instance type string (provider metadata endpoint or an env var the runner sets), the
resolved `-march=native` target (the exe cache already computes this identity -- reuse
it), SMT on/off, and for B3 the `nvidia-smi -L` list and driver / CUDA versions. One
JSON object in `manifest.json`; `stats.csv` gets an `instance` column.

### 4.3 Placement capture for B3.2

A small PowerShell / bash wrapper that starts the `nvidia-smi` query loop, runs the
program, stops the loop and writes both logs beside the run record. Lives in
`bench/diagnostics/cuda/`. No harness integration needed.

### 4.4 Nothing else

No optimizer gate changes, no new kernels, no Linux port unless D2 says so. The
campaign measures the compiler as it is at one commit.

## 5. Decisions

- **D1. Provider and instance classes.** Unknown to the plan. Needed: a dedicated CPU
  instance with a fixed, non-Zen-3 CPU model and >= 32 physical cores for B1/B2; a 2-4 GPU
  instance with full-rate fp64 for B3. Whether the GPU instance's CPU is also a usable B1
  target is a bonus, not a requirement.
- **D2. Windows VMs (no port) vs Linux (port the harness).** DECIDED 2026-10-31 (the user's call): Windows for B1,
  B2 and B4 -- the exact toolchain of the box (MSYS2 gcc 16.2, ifx via vcvars64), the same
  memory counters, zero harness work, and the second-machine comparison is then a
  hardware comparison, not a hardware + OS + compiler-version comparison. B3 on Windows
  too if the provider offers Windows images on its fp64 GPU class (the local nvcc + cl
  recipe transfers); if not, B3 on Linux is where the never-run Linux cuda path gets its
  first exercise, which is itself a finding. A full Linux port of the harness is a
  separate item, worth doing only when a Linux-only claim is wanted (a paper reviewer
  asking for it would be the trigger).
- **D3. Scope cut if credits are short.** Order of sacrifice: B4, then B3.3/B3.4 (keep the
  smoke and the placement witness, which are cheap), then B2's `repro` arm, then B2's
  Fortran arms. B1 is never cut; a cut B1 is a different plan.
- **D4. Who runs it.** The box's protocol: a runner that does nothing else on the
  instance, one session per block, every number into `stats.csv` with a campaign label,
  the bundle committed with the harness hash. Builders (if B3.1 forces fixes) run only
  their witnesses; the full gates run once, locally, on the commit the campaign names.

## 6. Budget sketch (instance hours, not credits)

| Block | Setup | Runs | Notes |
|---|---|---|---|
| B1 | 2-3 h (SDK, MSYS2, oneAPI, OpenBLAS, smoke) | 6-10 h | the box's 12-kernel matrix was ~5 h at 8 cores; 19 kernels now, more cores do not shorten serial arms |
| B2 | 0 (same instance, same session) | 3-5 h | 5-6 kernels x 7 thread counts x 2 fp x 3-4 arms x 7 rounds, threaded arms are short |
| B3 | 2-4 h (GPU toolchain; on Linux add the smoke fixes) | 2-3 h | the witness programs are seconds each; the census is dominated by setup |
| B4 | 0 | 1-2 h | |

Roughly 2 instance-days CPU + half a GPU instance-day. The GPU half-day is the expensive
one; D3 cuts it first after B4.

## 7. What this plan must not claim

- No "Blade on GPU is N times faster/slower" headline from B3: the lane stages every
  launch, has no thread fallback, no cuBLAS by default and no multi-device split of one
  kernel. B3 reports kernel-alone and end-to-end separately and lets the staging share
  speak.
- No thread-binding comparison across OSes (section 3, B2).
- No new gate values from B2 without a curve in `stats.csv` behind them.
- No numbers at power-of-two extents (the suite has none; a new witness program must not
  introduce one).
