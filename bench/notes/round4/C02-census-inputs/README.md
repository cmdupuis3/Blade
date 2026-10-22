# C02 frozen census inputs — runner execution only

No compiler change is included. Use the already-built C01 DLL/executable;
do not rebuild it, compile generated C++, evaluate Blade programs, launch tests,
or benchmark for this census. Record the existing executable/DLL hashes.

| File | Question |
|---|---|
| `01-primary.blade` | Exact private mapped-matrix binding and row-consumer IR; mapped `R*C` pool expected today |
| `02-ordered.blade` | Same shape with a seeded noncommutative row fold |
| `03-rowwise.blade` | Fully parenthesized existing-machine target: outer row map with inner fused scalar terminal |
| `04-named-computed.blade` | Required mapped output `P` plus keyed sample: its full pool must remain |
| `05-named-deferred.blade` | Named deferred `P`: record force under its own name and default auto-print behavior |
| `06-two-vector-covered.blade` | Already-streamed two-vector outer-product control: no rank-2 producer allocation |
| `07-zero-columns.blade` | `C=0,R=2` source-reachability probe; if accepted, one exact seed per result row |
| `08-zero-rows.blade` | `R=0,C=3` source-reachability probe; if accepted, empty output and no row seeds |

The independent arithmetic comments are hand-derived expectations, not executed
oracle pins. The two empty probes have not been checked; preserve exact frontend
or renderer refusals instead of silently changing their spelling or declaring a
passing empty-row contract. No broader pipeline variants are part of this bundle.

There is no production CLI IR-dump verb in the current dispatch (`Cli.fs:101`–
`149`); `test --ir-only` runs tests and is not a dump command. `probe-ir.fsx` is
a read-only FSI adapter against the existing assembly. It lowers/validates one
fixture, then prints binding/let IDs, callable parameters/captures, map input
ranks, reduction nodes/seeds and types. It does not call codegen or the
interpreter. `emit` and `plan --json` below are the available production CLI
commands for emitted storage and decisions; neither compiles/runs the program.

## Exact PowerShell census commands

Run only in the sole Luna runner, serialized with the existing queue. These
commands use a private output directory and a private per-fixture working
directory. They preserve every invocation's exit status and diagnostics. They
do not select outputs away: default print demand is part of this census.

```powershell
$c02Checkout = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\c-terminals'
$c02Bundle = Join-Path $c02Checkout 'bench\notes\round4\C02-census-inputs'
$c02Dll = Join-Path $c02Checkout 'bin\Release\net10.0\Blade.dll'
$c02Blade = Join-Path $c02Checkout 'bin\Release\net10.0\Blade.exe'
$c02Probe = Join-Path $c02Bundle 'probe-ir.fsx'
$c02Out = Join-Path $c02Checkout 'bench\.build\C02-census'
New-Item -ItemType Directory -Force -Path $c02Out | Out-Null
Get-FileHash -Algorithm SHA256 -LiteralPath $c02Dll, $c02Blade |
    Format-List | Out-File -Encoding utf8 (Join-Path $c02Out 'binary-hashes.txt')

$c02Keys = @('BLADE_FP_REPRO', 'BLADE_FP_REASSOC', 'BLADE_FP_CONTRACT',
    'BLADE_BLAS', 'BLADE_LLVM', 'BLADE_PRINT', 'BLADE_FUSION')
$c02Prior = @{}
foreach ($c02Key in $c02Keys) {
    $c02Prior[$c02Key] = [Environment]::GetEnvironmentVariable($c02Key)
}
$c02Results = [System.Collections.Generic.List[object]]::new()
try {
    $env:BLADE_FP_REPRO = '1'
    $env:BLADE_FP_REASSOC = $null
    $env:BLADE_FP_CONTRACT = 'off'
    $env:BLADE_BLAS = 'off'
    $env:BLADE_LLVM = 'off'
    $env:BLADE_PRINT = $null
    $env:BLADE_FUSION = 'on'
    foreach ($c02File in (Get-ChildItem -LiteralPath $c02Bundle -Filter '*.blade' | Sort-Object Name)) {
        $c02CaseDir = Join-Path $c02Out $c02File.BaseName
        New-Item -ItemType Directory -Force -Path $c02CaseDir | Out-Null
        Push-Location -LiteralPath $c02CaseDir
        try {
            & dotnet fsi ("--reference:" + $c02Dll) --exec $c02Probe $c02File.FullName *> 'ir.log'
            $c02Results.Add([pscustomobject]@{ Case=$c02File.Name; Verb='fsi-ir'; Exit=$LASTEXITCODE })
            & $c02Blade emit $c02File.FullName -o (Join-Path $c02CaseDir 'source.cpp') *> 'emit.log'
            $c02Results.Add([pscustomobject]@{ Case=$c02File.Name; Verb='emit'; Exit=$LASTEXITCODE })
            & $c02Blade plan $c02File.FullName --json 1> 'plan.json' 2> 'plan.stderr.log'
            $c02Results.Add([pscustomobject]@{ Case=$c02File.Name; Verb='plan'; Exit=$LASTEXITCODE })
        }
        finally {
            Pop-Location
        }
    }
}
finally {
    foreach ($c02Key in $c02Keys) {
        [Environment]::SetEnvironmentVariable($c02Key, $c02Prior[$c02Key])
    }
    $c02Results | ConvertTo-Json | Out-File -Encoding utf8 (Join-Path $c02Out 'invocations.json')
}
```

If FSI cannot load this existing net10 assembly, report that diagnostic and use
the emitted C++/plan evidence first; do not rebuild or silently run another
compiler. The source-only probe can be repaired separately if needed.

## Return these observations to root before implementation

1. For 01/02: identify the private local ID, its value (`IRApplyCombinator` or
   `IRCompute` wrapper), all references/captures, the row-apply node and callable
   body, original `R`/`C` index IDs and exact seed position. Do not infer scope
   from `__pfsrc`/`__v` names. Include the relevant IR excerpts.
2. Count and locate mapped-producer allocation/fill/read sites in the emitted
   C++; distinguish the already-required stored `M` from its mapped temporary.
   A global ban on `Array<double,2>` would be wrong because `M` must remain.
3. For 03: confirm an inner `IRReduceCompute` is bound inside the row callable
   and emitted without an extra full mapped-matrix pool. Confirm whether any
   additional row-sized allocation remains. A refusal is useful evidence and
   must be returned, not bypassed.
4. For 04/05: show which `P` allocation and print statements survive. For 06:
   confirm the already-streamed no-rank-2-pool route.
5. For 07/08: report exact accepted IR/emission or refusal. This census does not
   execute the expected outputs or seed counts; runtime gates follow only after
   root approves implementation and its legality/tests.

No emission, lower, compile, run or test command was executed by the author.
Root will authorize the one scoped implementation after these shapes are known.
