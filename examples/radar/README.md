# Padang radar: a GR graphics notebook + video

One day of PPI rain-rate scans (Padang, 2018-03-01: 143 `.nc4` files, ~10 min apart,
500×500 grid, 6 elevations) plotted through Blade's display lane with the GR backend:
`stdlib/plot.blade` → display frames → `tools/gr-render`.

## Files

| file | what |
|---|---|
| `make_padang_zarr.fsx` | merges the raw `.nc4` scans into ONE Zarr v3 store, reading through `NetcdfProvider.readVarData` and writing through `ZarrWrite.writeStoreV3` — the repo's own provider code both ways, so the store is what `ZarrProvider` reads back by construction; also picks the day's rainiest non-clutter point and the 128×128 storm cutout |
| `padang_20180301.bladenb` | the notebook: Zarr provider load → storm time series (`plot.line`) → full-disk heatmap + zoomed filled contours, all `1: backend` (GR) with fixed `zmin`/`zmax` color ranges |
| `render_video.fsx` | the whole day as one video: generates a 143-frame Blade program, harvests the display frames from a single `blade run`, and pipes them into `gr-render --video` |

## Running

```
dotnet fsi examples/radar/make_padang_zarr.fsx <dir-of-nc4-scans>   # once, writes <parent>/padang_<date>.zarr
# open the notebook, or run its flat text: blade run examples/radar/padang_20180301.bladenb works too
dotnet fsi examples/radar/render_video.fsx <store.zarr>             # full-disk heatmap video
dotnet fsi examples/radar/render_video.fsx <store.zarr> --var rain_zoom
```

Needs: the built compiler (`bin/Release/net10.0/Blade.exe`), MSYS2 ucrt64 g++ on PATH, and
`GRDIR` set (gr-render's environment contract, `tools/gr-render/README.md`). No ffmpeg, no
image library, no Python: GR writes the video itself and the merge runs on the repo's own
provider code.

**`make_padang_zarr.fsx` needs netCDF's `bin` AHEAD of MSYS2 on `PATH`** —
`$env:NETCDF_DIRin` ships its own `zlib1.dll`, and MSYS2's copy shadows it, which
surfaces as `Unable to load DLL 'netcdf'`. (`Blade.exe` handles this itself; a bare `fsi`
does not.)

## Things this corpus taught the toolchain (facts to keep)

* **A provider dim named `time` used to break codegen** — it reached generated C++ as
  `using time = int64_t;` and collided with the C library. FIXED by the
  `indexTypeCppName` sanitizer (commit `codegen: index type names must dodge C library
  globals`); the store keeps `scan` anyway — it names the scan ordinal, `time_min`
  carries the clock.
* **`zmin`/`zmax` slots** (added for this): the grid factories fix the color range —
  `zmax >= 0` activates, default `-1.0` keeps the automatic range and the trace
  byte-identical. gr-render honors the pair for colors, contour levels and the colorbar,
  clamping out-of-range cells. `tests/corpus/display/010_plot_fixed_range.blade`,
  `tools/gr-render/fixtures/contourf_fixed_range.json`.
* **`gr_contourf` gets `major_h = -1`** — 0 strokes an unlabeled black line at every
  level, which turns steep few-pixel radar cells into solid black blobs.
* **Sub-windows are declared at the data boundary.** Index-tag arithmetic
  (`R(36, i + 239, j)`) is forbidden by design, so the storm cutout is its own store
  variable (`rain_zoom`) with its own axes (`yz`/`xz`), not a slice expression.
* **Float32 flows end to end** (fixed in-session, two typecheck seams + an IR literal
  width): bare float literals adopt the Float32 width beside Float32 operands
  (`a * 1.0` stays Float32; `IRLitFloat32` emits `1.0f`), and the plot factories'
  element vars survive the `__display_json_array` shaping with their HM mark, so a
  Float32 series/grid/row-view monomorphizes its own specialization instead of
  meeting a frozen `Array<double>` parameter. `let frame = R((36 : Scan))` — the bare
  curried slice — is now the whole idiom. Pins: `tests/corpus/basic/047`,
  `tests/corpus/display/011`.
* **`* 1.0` was covering a deduction bug, not doing work.** A kernel reading a
  *captured generic array* (`lambda(k) -> a((k * na) / B)` with
  `a: Array<T like Idx<n>>`) had its output element type overwritten by the
  *iterated* array's — and iterating a `range<I>` means those elements are `Nat<I>`,
  so the map was typed as the loop index. `stdlib/plot.blade`'s whole decimation
  ladder carried a trailing `* 1.0` for this, which is also why an integer grid used
  to decimate to floating-point JSON. Fixed by keeping an HM-polymorphic kernel
  return instead of falling back; all 12 `* 1.0`s removed, integer grids now stay
  integer. Pin: `tests/corpus/functions/121`.
* **A point's time series is dimensional currying, not a loop** — transpose scan to
  the trailing axis and partially index: `transpose(R, [0, 2])((50 : X), (303 : Y))`.
  The hard transpose moves the whole array once (~0.9 s for 36.5M elements) where a
  gather reads 143 values, so it pays off as soon as more than a few series are read.
* **The radar's clutter disk lies.** Pixels within ~25 px of the grid center report rain
  ~100 % of the day; `make_padang_zarr.fsx` excludes a 40 px disk when picking the
  rainiest point.
* **GR renders video natively, and the by-hand pipeline was a mistake.**
  `videoplugin.dll` is a statically linked ffmpeg (h264/vp8/theora/gif), so
  `gr_beginprint("day.mp4")` plus a `clearws`/`updatews` cycle per frame is the whole
  job. The first version of this driver instead encoded 143 PNGs, base64'd them through the
  serve protocol, flattened each onto white (GR's *still* driver has a transparent
  background, which ffmpeg composites onto black, swallowing every black label — its
  *video* driver does not), then shelled out to ffmpeg. `gr-render --video` replaced all
  of it: two fewer dependencies and one encode instead of two. `GKS_VIDEO_OPTS`
  (`<width>x<height>@<framerate>`) is not optional — without it the video driver ignores
  the workstation viewport and invents its own size.
