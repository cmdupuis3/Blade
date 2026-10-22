# C06 first-input topology census

## Status

The staged first-input checkpoint passed on frozen `c-terminals` HEAD
`cd23a7b35b0898d5cd3e9930273da119e5fd0d36`. The 16-entry source/input manifest
`C06-census-inputs/SHA256SUMS.txt` matched the dispatched SHA256
`B21C2E5CEB4D943DFDAF26CC16BE773B0BECB1EB4D157FA259236F8BC86CE4E7`; the
adapter SHA256 was `1177ED5FFFACF3D1C94F2CCC454D7246DE958517D74E974460A2C894151B0E64`.
This checkpoint covers only `01-private-full.blade`: early and canonical-final
IR probes plus one fusion-on C++ emission. It does not complete the remaining
C06 census inputs or authorize implementation.

Root accepted that first-input evidence and authorized the remaining 18
source-only invocations. That continuation stopped at the first genuine
unexpected failure: the frozen producer-before-fold input `06` does not parse
(`zero` is rejected as a pattern token at line 6, column 9). Four earlier
canonical-final inputs (02–05) completed successfully. No later C06 arms ran.

## Build and invocation record

One fresh Release build succeeded with exit code 0. The exact command was
`dotnet build Blade.fsproj -c Release -p:RestoreIgnoreFailedSources=true` from
the `c-terminals` root. There were four NU1900 vulnerability-feed warnings
because `api.nuget.org` was unreachable; both projects restored from the
existing package cache and the build completed. `Blade.dll` SHA256 is
`97CD9919D5CA349C83CE680117C5F8AF1CDBC3B924F5F178C3CF0DDFA2D351A6` and
`Blade.exe` SHA256 is `A7152FFC55DF003C1E167FE22E8DA8347DF857DC29F87903B38A6FE71126CDEB`.

The two successful adapter commands, from the worktree root, were:

```text
dotnet fsi --reference:<absolute c-terminals Blade.dll> <absolute topology-probe.fsx> <absolute 01-private-full.blade> early <absolute fresh 01-early directory>
dotnet fsi --reference:<absolute c-terminals Blade.dll> <absolute topology-probe.fsx> <absolute 01-private-full.blade> final <absolute fresh 01-final directory>
```

Both exited 0. The early probe is the public lowering prefix after
`lowerArrayBinOps` and before `Optimize`; the final probe is canonical
`Lowering.lower` followed by successful `IRValidate`. Each saved the exhaustive
SrcLoc-aware reflected payload and summaries. The one actual emission command
was `Blade.exe emit <absolute 01-private-full.blade> -o <absolute
01-fusion-on.cpp>` with `BLADE_FUSION=on`; it exited 0 and wrote 9,637 bytes
(SHA256 `4547FA549B5B910C9A823491FC8219960B2545DED43CE2562C9545FEEA1D0948`).

All three operations used private `TEMP`/`TMP` beneath
`c-terminals/bench/.build/C06-census-runner`, explicit worktree-root CWD, and
`BLADE_FP_REPRO=1`, `BLADE_FP_CONTRACT=off`, `BLADE_FUSION=on`,
`BLADE_BLAS=off`, `BLADE_OMP_THREADS=1`, `BLADE_LLVM=0`; `BLADE_PRINT` was
unset (all bindings). No generated program was compiled or run. A separate
read-only `Blade.exe emit --help` syntax preflight exited 1 after printing usage
and `unknown option '--help'`; it emitted no source and is not counted as an
emission. It confirmed `emit <file> -o <out.cpp>`. That preflight and its logs
are preserved alongside the three requested invocations.

## Observed graph and allocation topology

The early and final IR agree on the materialized route. `M` is binding id 4,
`Array<Float64 like R, C>`, with the distinct nominal axes R(id 0, extent 3)
and C(id 1, extent 3). `total` is binding id 20. Its value binds a deferred
map application as id 14 over M, with output `Array<Float64 like R, C>` and
`sdims=[2], total=2`, then binds the literal seed 7 as id 15. The outer loop is
id 16 over `0..3`, the inner loop is id 17 over `0..3`, and the only update is
`v15 <- fold#22(v15, map#14(v16,v17))`. The result is v15 after both loops.
Thus the seed is initialized once and the original fold consumes mapped
elements in R-major, C-minor order. This is not a row partial or an
`IRReduceCompute` terminal in either observed IR.

The emitted C++ confirms actual allocation and use rather than a declaration
alone: M allocates 9 doubles and is filled with its nine source literals;
private `__v14` also allocates 9 doubles. A distinct flat nine-cell loop fills
`__v14[k] = M[k]*M[k]+1`, then two ascending loops update the single scalar
seeded accumulator from `__v14[i][j]`. The printed outputs include both M and
total. Each array therefore has a 72-byte element payload in this tiny fixture;
this is only a shape-derived payload count, not an RSS, peak-memory, allocator,
or performance measurement.

Both callable bodies are closed with zero captures, but their current IR effect
summaries have `Unknown=true`. This observation does not establish the
structural totality/purity proof required by the census proposal. No runtime
value, failure-order, lifetime, or optimization claim was tested.

## Artifacts

All raw artifacts and complete stdout/stderr/provenance are retained at
`C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\c-terminals\bench\.build\C06-census-runner`:

- `build.stdout.log`, `build.stderr.log`, `build.provenance.txt`
- `01-early.stdout.log`, `01-early.stderr.log`, `01-early.provenance.txt`, `01-early/typed-ir.txt`
- `01-final.stdout.log`, `01-final.stderr.log`, `01-final.provenance.txt`, `01-final/typed-ir.txt`
- `01-emit.stdout.log`, `01-emit.stderr.log`, `01-emit.provenance.txt`, `01-fusion-on.cpp`
- `emit-help.stdout.log`, `emit-help.stderr.log` (preflight only)

No additional C06 input, fusion-off emission, LLVM emission, plan, interpreter,
native execution, test category, benchmark, or full-suite run occurred.

## Authorized continuation and stop

After the first-input review, a serialized continuation began with final IR for
02–08, early IR for 02/08, C++ fusion-on emits for 02–08, and fusion-off emits
for 01/08. The first wrapper attempt pre-created the FSI destination for 02;
the adapter correctly rejected that non-fresh output directory before parsing.
Its process exited 1 and is retained as a runner setup failure, not a source
result. The corrected wrapper leaves FSI output directories absent at launch.
It completed canonical-final captures for 02–05 (four successful source
captures), then stopped at 06 on the parse refusal below. Of the 18 requested
remaining source captures, four completed, one failed parsing, and 13 were not
launched. The setup-failure FSI launch is additional to those 18 valid
attempts. Including the earlier three successful primary invocations and the
read-only CLI help preflight, the continuation artifacts distinguish every
process launch; no overlap occurred.

The successful final captures show the controls as written: 02 preserves a
rank-2 mapped apply, computes its rows, then performs an outer seeded stored
fold; 03 keeps named `P` and `rows` bindings while separately lowering flat
and nested results; 04 remains the existing rank-1 fused-fold control; 05
preserves separate flat and rowwise fused-fold structures. These are IR
observations only, not executions of their analytic values.

For `06-producer-before-fold.blade` (source SHA256
`23BAAB7DFE6BEDCA12258861AE6E90E3813C1AA513B9F7116C2F8FDA042222B2`), the
final-mode FSI invocation exited 1 in `Parser.parseProgram` with:

```text
Parse error at 6:9: Unexpected token in pattern: keyword 'zero'
```

No `typed-ir.txt` was written. The captured stdout SHA256 is
`CC40C45588B45C446D53813458BDCB52819CCA9D3C6A917AB79011B67A58201E`, stderr
SHA256 `2EDB93B135C2A15DB1EDD489673CFA7B2C33CBFDAF0C4559EB6CA529E595592A`, and
provenance SHA256 `8CE5E02DC29BF6207AD0D3886FFA6A2218CF1B9108CCA3DBFA6BEC5F12FFA2D9`.
The failure occurred before lowering, so it establishes no producer/fold
failure-order behavior. The source was not edited and no other gate was
continued.

The failed pre-created-directory attempt is at `02-final.stdout.log`,
`02-final.stderr.log`, and `02-final.provenance.txt`; corrected captures are
under `02-final-r2` through `05-final-r2`. All files remain under the artifact
root listed above.

## Repaired continuation: full source census captured

The input06 fixture-only correction was reviewed and frozen at `c-terminals`
HEAD `566d3ec1029eee444906385435b3b65662353aaf`. The repaired 16-entry
`SHA256SUMS.txt` has SHA-256
`1AD0F2C8C3999133FCE5E0194B2B54622E2C697027BD8B54CDB6926DD215A1D0`; the
input06 source now hashes to `1C76193C8AC3EB01341EEA53A1D75CAB46A1C299C2D4A3A03FAA4CCACE5A6E44`.
Production/compiler source was unchanged, so the already built Release binary
was reused: DLL `97CD9919D5CA349C83CE680117C5F8AF1CDBC3B924F5F178C3CF0DDFA2D351A6`,
EXE `A7152FFC55DF003C1E167FE22E8DA8347DF857DC29F87903B38A6FE71126CDEB`.
The read-only adapter remained `1177ED5FFFACF3D1C94F2CCC454D7246DE958517D74E974460A2C894151B0E64`.

The repaired `06-final-r3` lowering exited 0 and wrote full reflected IR. The
remaining final07/final08 and early02/early08 probes also exited 0. C++
emission succeeded for fusion-on inputs02–08 and fusion-off inputs01/08. Thus
all 14 captures left after the initial first-input and final02–05 evidence
completed successfully. Across the requested census there are eight canonical
final IR captures, three early IR captures, eight fusion-on C++ emits, and two
fusion-off C++ emits; the initial first-input captures are included in those
totals. The original final06 parse failure, the FSI freshness-guard setup
failure, and the CLI help preflight remain separately preserved and are not
counted as successful captures.

The early02/08 and final06/07/08 payloads are under
`C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\c-terminals\bench\.build\C06-census-runner`.
The early02 and early08 IR SHA-256 values are respectively
`0676A943FF06E96A74A0477C0E958DADCC451859DF2299BE0AB9FCE4205C2C85` and
`43CA8767D920CAE03245501FBADC11A74C5BE5106590E4496E4A2D1A97D69728`;
final06-r3, final07, and final08 IR hashes are in their adjacent provenance
files. The fusion-on C++ hashes for inputs02–08 are, in order,
`A8D6DEC154D22DC4E36470146DAF20328697E5FCE5B3F9252DA2D231D20EEABE`,
`45297AF569606A7335181AAAAC2476CBF6066927230746F34F62DDF6E49FE6C6`,
`E5E8B4DA04A411FECA7A7A0DC7B09B966EF95D848A733D981D205CA2F7376F0C`,
`F8F2BAF1EF97CF374C3A070165648C95A800C325A56B3ADD6ACA859446EDAEA7`,
`B3658ED0D64A337B2009DC4124BD8FB32C11EA6762747B458069A85053169261`,
`D130D7EAC92A98BF33CCB80081024623E04FC07AEBCAAAEB04FAE0F2D9F840D7`, and
`528CF4198E5F5EEE10203CB68735CC06B031BA88F79CFD93D879B8D97FF5851F`.
The fusion-off input01 output is byte-identical to its earlier fusion-on output
(SHA-256 `4547FA549B5B910C9A823491FC8219960B2545DED43CE2562C9545FEEA1D0948`).
Input08 fusion-on/off outputs are byte-identical (SHA-256
`528CF4198E5F5EEE10203CB68735CC06B031BA88F79CFD93D879B8D97FF5851F`),
consistent with this existing rowwise control already avoiding a full mapped
matrix in both settings.

Every invocation used explicit worktree-root CWD, private TEMP/TMP under the
C06 artifact root, REPRO=1, CONTRACT=off, BLAS=off, OMP_THREADS=1, and unset
PRINT; fusion was set per emit and LLVM remained off. Full stdout, stderr,
provenance, inputs, raw IR, and C++ are retained with each capture. No program
was compiled or run; no interpreter, plan, native, category, full-suite, timing,
or memory measurement was performed. This completes the source-only census
capture set for root review; it does not authorize implementation.
## Optional direct LLVM source emissions

After review of the full C++ source census, root authorized only inputs01,04,08
with fusion on. The same existing compiler was used; each CLI invocation ran
from the c-terminals checkout root with a fresh `.ll` output, private TEMP/TMP,
REPRO=1, CONTRACT=off, BLAS=off, OMP threads=1, and PRINT unset. `BLADE_LLVM=1`
was set explicitly. All three exited 0 and produced direct LLVM identified by
the generated-file header as the BLADE_LLVM lane; no fallback or unsupported
renderer occurred. No source or binary was changed.

| Input | LLVM bytes | LLVM SHA-256 |
|---|---:|---|
| 01-private-full | 6,700 | `D64729B9C9B54AA56F1F9F7D8D0ED24B406A372F17D3F5DD05658C999BA399F5` |
| 04-rank1-covered | 3,640 | `2DE91B47AA31635CB1A008A31217082E22C860E3032AD286501CFA5449FF9E49` |
| 08-rowwise-covered-control | 5,852 | `54731DE9B8FBE772031ADD8C3FFB8094952FF906E3971FB17BC90F3B48A1F1CD` |

Full `.ll`, stdout/stderr, and provenance are retained as `01-llvm-fusion-on.ll`
(and corresponding `04-llvm-fusion-on.ll` / `08-llvm-fusion-on.ll`) under the
C06 artifact root above. The first emission has no diagnostic output, but the
artifact is present, starts with the BLADE_LLVM generated header, and is
6,700 bytes. These are source-generation observations only. No LLVM program
was compiled or executed.