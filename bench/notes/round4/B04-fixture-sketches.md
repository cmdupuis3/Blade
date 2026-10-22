# B04 fixture sketches, not runnable sources

**Historical design sketches; concrete diagnostic now authored, still unexecuted.**
See [diagnostic README](../../diagnostics/store-consume/README.md). No code below has
been built, emitted or executed. The eventual source
set must satisfy [B04-contract.md](B04-contract.md), including its observer gate. This
file authorizes no harness/compiler/runtime edit. Absolute future scratch root:
`C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement/bench/.build/B04-runner`.

## One idiomatic Blade diagnostic

Use the existing saxpy R5 gen/inputs definitions, changing only Ix to 2053. No manual
index loops. New producer/consumer are elementwise arithmetic, so use implicit lifting
as the highest idiom rung; a refused forced-lifting form is recorded rather than silently
replaced with a different computation. The approved co-iteration map fallback can be
considered only if necessary to express that identical graph.

```blade sketch
type Ix = Idx<2053>
// gen and inputs: exact existing saxpy R5 definitions, not elided in actual source.
function producer(x: Array<Float64 like Ix>, y: Array<Float64 like Ix>) = {
    (1.5 * x + y) |> compute
}
function consumer(p: Array<Float64 like Ix>) = { (p * p) |> compute }
let x = inputs(10000019)
let y = inputs(20000038)
let p = producer(x, y)
let q = consumer(p)
let checksum = reduce(q, (+), 0.0)
let input_checksum = reduce(x, (+), 0.0) + reduce(y, (+), 0.0)
```

Luna's diagnostic selection must include `p,q,checksum,input_checksum`. Confirm both
arrays are fully printed after stop in both lanes; validate source/emitted lifetime
and every printed cell. This is outside the scalar-only harness parser. Do not rely on
the stale README statement that LLVM ignores selection; verify actual emission instead.
Unselected x/y remain stored inputs. Explicit source-tree-only ownership through print
and process exit is reported, not called matched explicit cleanup.

## Same-required-output Fortran mirrors

Both use the existing support's bench_start, R5 lcg generator, bench_stop_and_report,
scalar reports and post-stop BENCH_DUMP helpers. Allocate x/y/p/q after start. Separate
typed double operations and FP flags preserve the graph; no intrinsic non-temporal
stores, per-kernel flags, volatile output replacements or checksum-only shortcuts.

```fortran sketch
! natural body after input generation
p = 1.5_real64*x + y
q = p*p
cs = 0.0_real64
do k=1,n
  cs = cs + q(k)
end do
```

```fortran sketch
! expert body after input generation: scalar forwarding, two mandatory output stores
cs = 0.0_real64
do k=1,n
  v = 1.5_real64*x(k) + y(k)
  p(k) = v
  w = v*v
  q(k) = w
  cs = cs + w
end do
```

Both then compute sx=sum(x) and sy=sum(y) separately, input_checksum=sx+sy, stop and
report. Diagnostic output dumps p's n cells then q's n cells in separate named files
(`pm52_p_dump.txt`, `pm52_q_dump.txt`) using existing helpers, then deallocates all four
arrays. Working directory is the absolute private arm directory. Repro has one ascending
chain; default licensed reductions may use only the existing matching licence protocol.
No new natural/expert source is claimed reviewed before root examines the actual files.

## Independent reference and meaningful fixtures to author after approval

Use R5 scalar integer math independently from compiler output. For each k compute exact
integer h1..h5, convert to binary64 x/y, evaluate v=(1.5*x)+y then w=v*v, storing reference
p/q. For reproducible pins accumulate explicit ascending binary64 sums separately;
also retain math.fsum truth for the existing tolerance protocol. Do not call fsum an
ascending-chain oracle. Record BENCH_GENERATOR=R5 explicitly; no ambient R3 selection.
Existing ref_saxpy and pins-B offer cross-checks for v and sum(v*v), not sole evidence of
the new q buffer or current association. Luna generates/executes any oracle/pins.

Authored mathematical small-value witness, independent of R5 and not executable pins:
x=[0,0.5,1], y=[0.25,0.5,0.75] gives p=[0.25,1.25,2.25],
q=[0.0625,1.5625,5.0625], checksum=6.6875, input_checksum=3.0.
All values and sums are exactly binary representable. Use it to catch a changed
coefficient, omitted square, incorrect q/p dump mapping and off-by-one output lengths.
Odd R5 n=2053 provides nontrivial full-array coverage; these tiny constants are not a
performance size or a substitute for the R5 fixture.

Focused validator fixtures must reject: missing p or q, n-1/n+1 cells, duplicate output,
q substituted by p, changed interior cell with unchanged endpoints, changed tail, NaN,
wrong coefficient, reordered dump labels, missing observer-success evidence and an arm
labelled checksum-only by the store audit. A numerically correct no-store synthetic
record must fail admission. A separate dump's passing cells cannot approve a scalar-only
timed record. Keep numerical result, store audit and endpoint evidence as distinct gates.

No broad test suite, .NET build, C++ compilation or runner command is issued for these
sketches. After root source authorization, provide exact fresh-process commands and
hashes for the concrete implementation, then freeze before sole Luna validation.
