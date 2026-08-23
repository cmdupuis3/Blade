# gr-render

Translates a **plotly figure spec** — exactly what `stdlib/plot.blade` emits — into a
**GR**-rendered image (PNG, SVG or PDF). It is the native half of the Blade Plots panel's
plotly ⇄ GR toggle: the extension keeps the plotly JSON, and this helper turns that same
JSON into a static raster on demand.

Nothing here knows about Blade, display frames, or the serve protocol. Input is a figure
object, output is image bytes.

## Getting a helper without a toolchain

The whole point of this section: **most users never run `build.ps1`.** A release ships one
precompiled, platform-stamped binary per platform —

```
gr-render-win32-x64.exe
gr-render-linux-x64
gr-render-darwin-x64
gr-render-darwin-arm64
```

— named `gr-render-<platform>-<arch>[.exe]`, where `<platform>` and `<arch>` are spelled the
way Node's `process.platform` / `process.arch` spell them (`win32`, not `windows`; `darwin`,
not `macos`) rather than a second vocabulary invented for the .NET side. That's the same pair
`deps.json`'s `gr` asset keys and the Blade-REPL extension's `fetch-vendor.js` use, so "which
platform" means one thing across the whole toolchain, not three.

`src/display/GrRender.fs`'s `resolveHelper` looks for one of these — see
[Resolution order](#resolution-order) — at each of the same locations it always searched
(`BLADE_GR_RENDER`, beside `Blade.exe`, walking up to `tools/gr-render/`), so dropping a
`gr-render-win32-x64.exe` next to `Blade.exe` (or in this directory, in a dev checkout) is
enough: no g++, no GR install, no `build.ps1`. `.github/workflows/gr-render.yml` is what
produces these artifacts on GitHub's windows/linux/macOS runners (see
[CI packaging](#ci-packaging)); until a release actually publishes them somewhere a user
downloads from, this is the shape a manual copy needs to have.

**The runtime dependency does NOT go away.** A prebuilt `gr-render` still needs GR itself at
run time — see [Environment contract](#environment-contract): `GRDIR` pointing at a GR
install, `$GRDIR/bin` on `PATH`, and `GKS_WSTYPE=100` (which the helper sets itself). Shipping
the *helper* prebuilt removes the g++/MSYS2 dependency; it does not remove the GR dependency.
That is a separate, not-yet-solved packaging problem (vendoring or fetching GR itself, the way
`deps.json` + `fetch-vendor.js` do for the VS Code extension's plotly.js).

## Resolution order

`GrRender.fs`'s `resolveHelper` tries, in order:

1. **`$env:BLADE_GR_RENDER`**, if set. Used exactly as given, whatever it's named — this is
   the explicit override, so a wrong path here is an ERROR, never a silent fallthrough to the
   next step.
2. **Beside the running `Blade.exe`** (a deployed toolchain's own directory).
3. **`tools/gr-render/`, walking up from `Blade.exe` up to 8 directory levels** (a dev
   checkout, where `Blade.exe` runs out of `bin/Release/net10.0/` under the repo root).

At steps 2 and 3, **each location is checked for the platform-stamped name FIRST, then the
plain `gr-render[.exe]` SECOND** — a `gr-render-win32-x64.exe` sitting next to a leftover
`gr-render.exe` wins, so a CI-produced or hand-copied stamped release binary is never shadowed
by an older manual build under the plain name. If neither name is at a location, resolution
continues to the next one; only once every location has been tried for both names does
resolution fail (with an error naming both leaf names it looked for).

This preference is a location-major, name-minor search: it prefers a *closer* plain binary
over a *farther* stamped one, not the other way around. That was a deliberate call — closeness
already encodes "this is what the developer/deployment actually put here for me," and a
platform mismatch two directories further up wasn't going to run correctly anyway, so there's
no scenario where preferring the far stamped copy over the near plain one would have helped.

## Building from source (when you DO have a toolchain)

```powershell
powershell -File build.ps1                 # resolves GR automatically
powershell -File build.ps1 -GrDir C:\gr    # or point it at an install
```

The GR root is resolved in this order: `-GrDir`, `$env:GRDIR`, then the two vendored trees
(`Blade-REPL/.claude/worktrees/.../vendor/gr`, then `Blade-REPL/vendor/gr`). The script is
idempotent — it recompiles only when a source file or GR's link library is newer than the
exe (`-Force` to compile anyway). It prints the path of the exe it produced (`gr-render.exe`
on Windows, `gr-render` elsewhere).

The compile it runs on Windows is:

```
g++ main.cpp -I <GR>/include -L <GR>/lib -lGR -static-libgcc -static-libstdc++ -std=c++17 -O2 -o gr-render.exe
```

**`-static-libgcc -static-libstdc++` are mandatory on Windows, not an optimisation.** The
documented plain `-lGR` recipe crashes at load with `STATUS_ENTRYPOINT_NOT_FOUND` on this
machine: MSYS2 UCRT64 g++ 15.2 is ABI-incompatible with the older MinGW runtime DLLs GR ships
in its `bin/`. Static-linking the GCC runtime removes the conflict; the only non-system DLLs
the exe then loads are GR's own `libGR.dll` and `libwinpthread-1.dll`, both of which live in
`$GRDIR/bin`.

`build.ps1` also runs on linux/macOS (invoked by `package.ps1` and by the CI workflow) via
`pwsh`, carrying the same static-link flags over to Linux as portability hardening and
dropping them on macOS (clang/libc++ has no equivalent to `-static-libstdc++`). **Only the
Windows path above has actually been compiled and run** — see `build.ps1`'s own doc comment
and the CI workflow's header comment for exactly what's a verified recipe versus a best-effort
guess on the other two platforms.

Only the headless subset of GR is needed (`include/`, the platform's link library under
`lib/`, `bin/` with libGR/libGKS/cairoplugin/libwinpthread, `fonts/`); the exe in this
directory is built and tested against exactly that pruned tree on Windows. Qt is never loaded.

## Packaging a stamped release artifact

```powershell
powershell -File package.ps1                 # builds (if needed) + stamps + hashes
powershell -File package.ps1 -GrDir C:\gr -Force
powershell -File package.ps1 -OutDir C:\out
```

A thin wrapper over `build.ps1` — it does not reimplement the compile step, so there is
exactly one place (`build.ps1`) that knows the compiler flags for each platform. It builds the
plain `gr-render[.exe]` via `build.ps1`, then copies it into `dist/` (default; `-OutDir` to
change it) under the platform-stamped name from [Resolution order](#resolution-order) above,
e.g. `dist/gr-render-win32-x64.exe`. Idempotent: if `dist/<stamped-name>` already has the same
sha256 as the freshly built exe, nothing is re-copied. Prints the artifact's path and its
sha256 as its last two output lines, in that order, so a script (CI included) can read them off
without parsing prose:

```powershell
$lines = & pwsh -File package.ps1
$artifactPath, $sha256 = $lines[-2], $lines[-1]
```

## CI packaging

`.github/workflows/gr-render.yml` builds `gr-render` on GitHub-hosted windows/linux/macOS
runners: fetch the pinned GR release tarball for the runner's platform (same version/URLs as
the `gr` entry in the Blade-REPL repo's `deps.json`), extract it, run `package.ps1` against it,
run `test.ps1` where the platform allows, and upload the stamped binary as a build artifact
named `gr-render-<platform>-<arch>`.

**This workflow has not been executed.** There is no CI runner access, no non-Windows machine,
and no way to run a GitHub Actions job from this environment — only a YAML syntax check
(`python -c "import yaml; yaml.safe_load(...)"`) was possible. The workflow file's header
comment marks exactly which parts are verified (the Windows leg, built and tested against this
same GR version on a real machine as part of this change) versus best-effort guesses (the
linux/macOS compiler selection, the tarball layout assumption, whether static-linking
libstdc++ is right on Linux). Read that comment before trusting a green run of this workflow
completely — and especially before trusting a red one, since a failure might be an environment
assumption instead of an actual bug.

## Environment contract

The caller **must** provide:

| variable | value | why |
|---|---|---|
| `GRDIR` | the GR install root | GR resolves plugins and fonts through it; without it GR dies with an access violation. gr-render refuses to start rather than crash. |
| `PATH` | `$GRDIR\bin` prepended | `libGR.dll` / `libwinpthread-1.dll` are load-time dependencies; without them the process dies with `STATUS_DLL_NOT_FOUND`. |

`GKS_WSTYPE=100` and an unset `GR_DISPLAY` are also required, but gr-render sets both itself
(with `_putenv`, before the first GR call — GKS caches the workstation type in a static).
Without them GR's default Windows workstation is gksqt, which can leave a stray `gksqt.exe`
Qt process behind. Setting them in the caller's environment too is harmless and recommended.

## One-shot mode

```
gr-render --out PATH [--width N] [--height N] [--format png|svg|pdf]   < figure.json
```

* the figure JSON arrives on **stdin**;
* `--format` defaults to `--out`'s extension, else `png`;
* `--width` defaults to 800, `--height` to 600;
* success: exit 0, the file at `--out` is the complete image, **stdout is empty**;
* failure: a `gr-render: ...` message on **stderr**, nonzero exit, and **no output file** —
  the render always goes to a temp file first, so a partial image is never published.

## Serve mode

```
gr-render --serve
```

One NDJSON request per line on stdin; exactly one single-line response per request, in
order, on stdout. Requests:

```jsonc
{"id":N,"cmd":"render","spec":{...},"width":800,"height":600,"format":"png"}
{"id":N,"cmd":"ping"}
{"id":N,"cmd":"shutdown"}
```

Responses:

```jsonc
{"id":N,"ok":true,"format":"png","data":"<base64 of the image bytes>"}
{"id":N,"ok":true}                                    // ping
{"id":N,"ok":false,"error":"..."}                     // anything that failed
```

* `width` / `height` / `format` are optional and take the one-shot defaults.
* `shutdown` — and EOF on stdin — exit 0.
* A failed render (bad spec, unsupported trace, GR error) answers `ok:false` and **the
  process stays alive**; only `shutdown`/EOF end the loop.
* **stdout carries responses and nothing else.** fd 1 is redirected to stderr at startup and
  responses are written through a private duplicate of the original handle, so any
  diagnostic a GR plugin decides to print cannot corrupt the stream.
* Each render goes through a unique temp file under `%TEMP%`
  (`gr-render-<pid>-<counter>.<ext>`), which is read back and deleted immediately. GR has no
  in-memory print sink — `mem://` is a silent no-op — so the temp file is not optional.

> **Note — deviation from the plan.** `docs/gr-graphics-plan.md` §4.1 sketches the response
> as `{"id":N,"ok":true,"png":"<base64>"}`. The implemented shape is
> `{"id":N,"ok":true,"format":"png","data":"..."}`, which carries the format explicitly so
> svg/pdf can use the same verb. Whoever writes the `renderPlot` arm in `IdeServe.fs` must
> read `data`, not `png`.

## Sizing

cairo (GR's PNG/SVG/PDF backend) is hardwired to 600 dpi, truncates, and forces even
dimensions. The recipe that produces exact pixels is:

```c
gr_beginprint(path);                       // FIRST: the workstation must exist
gr_setwsviewport(0, (W+0.5)*0.0254/600.0, 0, (H+0.5)*0.0254/600.0);
gr_setwswindow(0, W>=H ? 1.0 : (double)W/H, 0, H>=W ? 1.0 : (double)H/W);
```

Setting the workstation transform *before* `gr_beginprint` is silently ignored (you get
2400×2400); `gr_beginprintext`'s page-size argument rejects `WxH` strings outright. Odd
widths and heights are **rounded down to the next even value** before rendering, so the file
always matches the size gr-render actually used. Sizes are clamped to 16…8192.

Because `gr_setwswindow` normalises the aspect, NDC is *not* the unit square: the shorter
axis spans less than 1. Every viewport and text coordinate in `render.hpp` is scaled
accordingly.

## Determinism

* **PNG is byte-deterministic** and process-state-independent: two runs, and a render from a
  long-lived `--serve` worker versus a fresh one-shot process, produce identical bytes. The
  chunk stream carries no timestamp. `test.ps1` asserts this with SHA256. PNG goldens are
  therefore viable — but treat them as **machine-pinned** until cross-machine reproduction is
  tested (FreeType and libpng builds feed the bytes).
* **SVG and PDF are NOT deterministic**: SVG clip-path ids come off a `srand(time)` counter
  and PDF embeds `/CreationDate`. Never golden-test them; use them for export only.

## What it draws

| plot.blade factory | trace | GR path |
|---|---|---|
| `plot.contourf` | `contour` + `contours.coloring="fill"` | `gr_contourf` |
| `plot.contour` | `contour` + `contours.coloring="lines"` | `gr_contour` (colored lines) |
| `plot.heatmap` | `heatmap` | `gr_cellarray` (there is no `gr_heatmap`) |
| `plot.line` | `scatter` + `mode="lines"` | `gr_polyline` |
| `plot.scatter` | `scatter` + `mode="markers"` | `gr_polymarker` |

Plus axes with ticks (`gr_axes`, boxed), title and axis labels (`gr_text`, y rotated via
`gr_setcharup`), and a colorbar for every grid trace.

Colorscales: **Viridis** (default, GR 44) · **Plasma** (GR 46) · **Cividis**, **Greys**,
**RdBu** (installed from plotly's own RGB anchor tables via `gr_setcolormapfromrgb`, since GR
has no equivalent). Unknown names fall back to Viridis, matching `plot.blade`.

Multiple traces per figure are supported: grid traces draw first, then line/scatter traces
in plotly's default qualitative palette (installed at colour indices 980+).

### Input tolerances

* Numbers may be `null`, or the bare `NaN` / `Infinity` / `-Infinity` tokens that
  `stdlib/plot.blade` currently emits. All read as non-finite doubles, so both today's
  emitter and the fixed one parse.
* A non-finite point in a line/scatter trace **breaks the polyline** (a gap, as in plotly)
  and is skipped for markers.
* `x` / `y` may be omitted on any trace; integer indices are used.
* Grid axes may be given descending — the data is flipped to match. Non-monotonic grid axes
  are an error.
* A UTF-8 BOM is tolerated. Text is passed to GR as UTF-8.

### Known fidelity gaps versus plotly

* **NaN inside a `z` grid.** A heatmap cell renders as background (colour index 0), but
  contour/contourf have no hole concept in GR: non-finite cells are sunk to the bottom of the
  colour range instead of being left transparent.
* **Contour levels** are `ncontours` evenly spaced values across the exact data range;
  plotly picks round level boundaries via its own autocontour heuristic.
* **Tick label phase.** Axis limits are the exact data range for grid traces (so a heatmap
  fills its box), which can put the first tick label on a non-round value. Line/scatter-only
  figures are snapped out to tick boundaries with `gr_adjustlimits`, so they look plotly-ish.
* **Colorbar.** Drawn by hand rather than with `gr_colorbar()`, which phases its labels on
  `zmin` and prints seven-significant-digit labels (`8.54322`, `6.04322`, …) that overrun the
  canvas. Same primitives, tick phase snapped to a round multiple. Either way, a colorbar
  paints into the *current* viewport and must be bracketed by
  `gr_savestate()` / `gr_restorestate()` around a strip viewport, or it repaints the plot.
* **Log axes, legends, per-trace colours/markers/widths, annotations, subplots, secondary
  axes** are not implemented (`plot.blade` cannot express them yet).
* Contour grids larger than 2× the output pixel size are **stride-decimated** before
  contouring (`gr_contourf` is O(grid): 2000² costs ~1.1 s). `gr_cellarray` is O(output
  pixels) and is never decimated.

## Tests

```powershell
powershell -File test.ps1                  # builds if needed, then asserts
powershell -File test.ps1 -GrDir C:\gr
```

Hermetic: everything transient lands in `%TEMP%\gr-render-test-<pid>` and is removed
afterwards. It checks exit codes, PNG signature + IHDR width/height against the *requested*
size, byte-stability across runs and across the serve worker, the full NDJSON round trip
(including that a failed render does not kill the loop and that stdout stays clean),
failure modes (bad JSON, unsupported trace, missing `--out`), svg/pdf smoke tests, and the
hygiene invariants: no surviving `gksqt.exe`, no stray `gks.*` in cwd, no leftover temp
renders. Every check prints `ok`/`FAIL`; the script exits nonzero if any failed.

Fixtures in `fixtures/` are static and hand-checked: one per trace shape, one with
title + both axis labels + Cividis, one line with `null` *and* `NaN` gaps, one 200×200
contourf, and one deliberately malformed JSON file.

## Source map

| file | contents |
|---|---|
| `main.cpp` | CLI, env setup, stdout capture, one-shot and serve loops, temp-file lifecycle |
| `json.hpp` | strict hand-rolled JSON reader (+ `NaN`/`Infinity` tokens), string escaper |
| `figure.hpp` | the accepted plotly-figure subset and its reader |
| `render.hpp` | sizing recipe, layout, and every GR drawing call |
| `colormaps.hpp` | the five colorscales and the trace palette |
| `base64.hpp` | base64 encoder for serve responses |
| `build.ps1` | compiles `gr-render[.exe]` from source against a GR install |
| `package.ps1` | wraps `build.ps1`; stamps the result into `dist/gr-render-<platform>-<arch>[.exe]` + sha256 |
| `test.ps1` | hermetic self-test (builds if needed, asserts against `fixtures/`) |
