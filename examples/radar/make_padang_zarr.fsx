// make_padang_zarr.fsx — merge one day of Padang radar rain-rate scans into a
// Blade-readable zarr v3 store (examples/radar/padang_20180301.bladenb).
//
// Reuses the repo's own machinery end to end, so the store is correct by
// construction rather than by a hand-rolled writer:
//   - NetcdfProvider.readVarData (providers/NetcdfProvider.fs) reads each
//     scan's rain_rate / lat / lon through the SAME libnetcdf path the
//     compiler's own provider uses,
//   - ZarrWrite.writeStoreV3 (providers/ZarrProvider.fs) writes the store, so
//     the layout is exactly what ZarrProvider READS back: uncompressed, little
//     endian, C order, one chunk per scan.
//
// A provider path is a compile-time literal, so 143 of them is not a program:
// merging the day into ONE store is what makes the notebook possible at all.
//
// Run from anywhere:
//   dotnet fsi examples/radar/make_padang_zarr.fsx <dir-of-nc4-scans> [--out DIR] [--composite]
//
// Prints the day's rainiest NON-CLUTTER point and the peak scan for pasting
// into the notebook, plus the cutout bounds.

// The prefix of Blade.fsproj's compile order that IR.fs and the two providers
// need, in that order -- fsi has no project file, so the dependency chain is
// spelled out. (Blade.fsproj is the source of truth if this ever drifts.)
#load "../../src/Runtime.fs"
#load "../../src/Platforms.fs"
#load "../../src/Ast.fs"
#load "../../src/Diagnostics.fs"
#load "../../src/Types.fs"
#load "../../src/PerfCounters.fs"
#load "../../src/SimplexBlocksCore.fs"
#load "../../src/OrbRank.fs"
#load "../../src/IR.fs"
#load "../../src/IRLoopStructure.fs"
#load "../../src/IRStorage.fs"
#load "../../src/IRLift.fs"
#load "../../src/IRMono.fs"
#load "../../src/IRPrint.fs"
#load "../../src/IRValidate.fs"
#load "../../src/providers/ProviderRegistry.fs"
#load "../../src/providers/ZarrProvider.fs"
#load "../../src/providers/NetcdfProvider.fs"

open System
open System.IO
open System.Text.RegularExpressions
open Blade.ZarrProvider

let FILL = -9999.0
// PDG_rain_rates_YYYYMMDD_HHMMSS.nc4
let nameRe = Regex(@"_(\d{8})_(\d{2})(\d{2})(\d{2})\.nc4$", RegexOptions.Compiled)

let argv = fsi.CommandLineArgs |> Array.skip 1
let positional = argv |> Array.filter (fun a -> not (a.StartsWith "--"))
let hasFlag f = argv |> Array.contains f
if positional.Length < 1 then
    eprintfn "usage: dotnet fsi make_padang_zarr.fsx <dir-of-nc4-scans> [--out DIR] [--composite]"
    exit 2

let scanDir = positional.[0]
// Max over ELEVATIONS instead of the lowest sweep. The lowest sweep is the
// surface-rain convention; a composite answers "did it rain anywhere in the
// column", which is a different question, so it is opt-in.
let composite = hasFlag "--composite"

let files =
    Directory.GetFiles(scanDir, "*.nc4")
    |> Array.filter (fun f -> nameRe.IsMatch(Path.GetFileName f))
    |> Array.sort
if files.Length = 0 then failwithf "no PDG_*.nc4 scans in %s" scanDir

let date = nameRe.Match(Path.GetFileName files.[0]).Groups.[1].Value
let outDir =
    match argv |> Array.tryFindIndex ((=) "--out") with
    | Some i when i + 1 < argv.Length -> argv.[i + 1]
    | _ ->
        let parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator scanDir)
        Path.Combine(parent, sprintf "padang_%s.zarr" date)

let floats (path: string) (var: string) =
    match Blade.NetcdfProvider.readVarData path var with
    | Ok { DimLengths = lens; Payload = Blade.NetcdfProvider.NcFloats xs } -> lens, xs
    | Ok _ -> failwithf "%s: '%s' is integer-coded; expected floating point" path var
    | Error e -> failwith e

// ---- read every scan ------------------------------------------------------
// rain_rate is (z, lat, lon); the fill (-9999) becomes 0.0 ("no rain
// detected") so every reduction downstream stays finite.

let mutable ny = 0
let mutable nx = 0
let frames = ResizeArray<float32[]>()
let minutes = ResizeArray<float>()
let mutable lat1d : float[] = [||]
let mutable lon1d : float[] = [||]

for f in files do
    let m = nameRe.Match(Path.GetFileName f)
    let hh = int m.Groups.[2].Value
    let mm = int m.Groups.[3].Value
    let ss = int m.Groups.[4].Value
    minutes.Add(float hh * 60.0 + float mm + float ss / 60.0)

    let lens, rr = floats f "rain_rate"
    let nz, ry, rx =
        match lens with
        | [ a; b; c ] -> a, b, c
        | other -> failwithf "%s: rain_rate has dims %A; expected (z, lat, lon)" f other
    if ny = 0 then
        ny <- ry
        nx <- rx
    elif ny <> ry || nx <> rx then
        failwithf "%s: %dx%d does not match %dx%d" f ry rx ny nx

    let frame = Array.zeroCreate<float32> (ny * nx)
    for i in 0 .. ny * nx - 1 do
        let v =
            if composite then
                let mutable best = FILL
                for k in 0 .. nz - 1 do
                    let s = rr.[k * ny * nx + i]
                    if s <> FILL && (best = FILL || s > best) then best <- s
                best
            else
                rr.[i]
        frame.[i] <- if v = FILL then 0.0f else float32 v
    frames.Add frame

    if lat1d.Length = 0 then
        // The coordinate grids are 2-D and curvilinear; the notebook wants 1-D
        // axes, so cut them through the grid CENTRE. NOTE the files' lat/lon
        // `units` attributes are SWAPPED -- lon claims degrees_north -- so
        // everything here keys off variable NAMES, never units.
        let _, lat2 = floats f "lat"
        let _, lon2 = floats f "lon"
        let mid = ny / 2
        lat1d <- Array.init ny (fun i -> lat2.[i * nx + mid])   // centre COLUMN: latitude along rows
        lon1d <- Array.init nx (fun j -> lon2.[mid * nx + j])   // centre ROW: longitude along columns

let nt = frames.Count
let at t y x = frames.[t].[y * nx + x]

// ---- the day's rainiest point, EXCLUDING ground clutter -------------------
// Pixels within ~25 px of the grid centre report rain in essentially every
// scan (measured wet fraction 1.00, against a 0.39 p99 in the 15-40 px ring on
// 20180301): that is backscatter off the tower, not weather, and a naive
// argmax lands squarely in it. 40 px keeps a margin.

let total = Array.zeroCreate<float> (ny * nx)
for t in 0 .. nt - 1 do
    for i in 0 .. ny * nx - 1 do
        total.[i] <- total.[i] + float frames.[t].[i]

let cy = ny / 2
let cx = nx / 2
let dist y x = sqrt (float ((y - cy) * (y - cy) + (x - cx) * (x - cx)))

let mutable iy = 0
let mutable ix = 0
let mutable best = -1.0
for y in 0 .. ny - 1 do
    for x in 0 .. nx - 1 do
        if dist y x >= 40.0 && total.[y * nx + x] > best then
            best <- total.[y * nx + x]
            iy <- y
            ix <- x

let series = Array.init nt (fun t -> at t iy ix)
let tPeak = series |> Array.mapi (fun t v -> t, v) |> Array.maxBy snd |> fst
let wet = float (series |> Array.filter (fun v -> v > 0.5f) |> Array.length) / float nt
let domainMean t = (Array.sumBy float frames.[t]) / float (ny * nx)
let tWide = [| 0 .. nt - 1 |] |> Array.maxBy domainMean
let dayMax = frames |> Seq.collect id |> Seq.max

// ---- the storm cutout -----------------------------------------------------
// Index-tag arithmetic is forbidden in Blade by design, so a sub-window cannot
// be cut in-language: it is declared HERE, at the data boundary, as its own
// array with its own axes.

let ZW = 128
let y0 = min (max (iy - ZW / 2) 0) (ny - ZW)
let x0 = min (max (ix - ZW / 2) 0) (nx - ZW)

// ---- write ----------------------------------------------------------------
// The leading dimension is `scan`, not `time`: it numbers the sweep, while
// time_min carries the clock. (A dim named `time` also used to collide with
// the C library in generated C++; that is fixed, but `scan` is still the
// truthful name.)

let flat3 (pick: int -> int -> int -> float32) (a: int) (b: int) (c: int) =
    let out = Array.zeroCreate<float32> (a * b * c)
    for i in 0 .. a - 1 do
        for j in 0 .. b - 1 do
            for k in 0 .. c - 1 do
                out.[(i * b + j) * c + k] <- pick i j k
    out

let var name dims shape chunks data : ZarrWrite.WriteVar =
    { Name = name
      DimNames = Some dims
      Shape = shape
      Chunks = chunks
      FillValue = FillFloat 0.0
      Data = data
      OmitChunks = []
      Blade = None }

if Directory.Exists outDir then
    failwithf "%s already exists -- remove it first (refusing to overwrite)" outDir

ZarrWrite.writeStoreV3 outDir
    [ var "rain_rate" [ "scan"; "y"; "x" ] [ int64 nt; int64 ny; int64 nx ] [ 1L; int64 ny; int64 nx ]
          (ZarrWrite.WF32 (flat3 (fun t y x -> at t y x) nt ny nx))
      var "lat" [ "y" ] [ int64 ny ] [ int64 ny ] (ZarrWrite.WF64 lat1d)
      var "lon" [ "x" ] [ int64 nx ] [ int64 nx ] (ZarrWrite.WF64 lon1d)
      var "time_min" [ "scan" ] [ int64 nt ] [ int64 nt ] (ZarrWrite.WF64 (minutes.ToArray()))
      var "rain_zoom" [ "scan"; "yz"; "xz" ] [ int64 nt; int64 ZW; int64 ZW ] [ 1L; int64 ZW; int64 ZW ]
          (ZarrWrite.WF32 (flat3 (fun t y x -> at t (y0 + y) (x0 + x)) nt ZW ZW))
      var "lat_zoom" [ "yz" ] [ int64 ZW ] [ int64 ZW ] (ZarrWrite.WF64 lat1d.[y0 .. y0 + ZW - 1])
      var "lon_zoom" [ "xz" ] [ int64 ZW ] [ int64 ZW ] (ZarrWrite.WF64 lon1d.[x0 .. x0 + ZW - 1]) ]

let hhmm (x: float) = sprintf "%02d:%02d" (int x / 60) (int x % 60)
printfn "store:        %s" outDir
printfn "shape:        rain_rate(%d, %d, %d)  [%s]" nt ny nx (if composite then "max composite" else "z=0 lowest sweep")
printfn "day max:      %.2f mm/h" dayMax
printfn "rainiest pt (>=40 px from radar): iy=%d ix=%d  (lat=%.4f, lon=%.4f)" iy ix lat1d.[iy] lon1d.[ix]
printfn "  daily total %.1f, series max %.2f at scan %d (%s), wet %.0f%% of scans"
        total.[iy * nx + ix] (Array.max series) tPeak (hhmm minutes.[tPeak]) (wet * 100.0)
printfn "widest rain:  scan %d (%s), domain mean %.3f mm/h" tWide (hhmm minutes.[tWide]) (domainMean tWide)
printfn "zoom cutout:  rain_zoom = rain_rate[:, %d:%d, %d:%d]" y0 (y0 + ZW) x0 (x0 + ZW)
printfn "paste into the notebook (as inline cast literals, e.g. (%d : Y)):" iy
printfn "  iy_rainy = %d   ix_rainy = %d   t_peak = %d   t_wide = %d" iy ix tPeak tWide
