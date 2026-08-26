// make_station_icechunk.fsx — deterministic fixture generator for the
// Icechunk demo notebook (examples/station_temps.bladenb).
//
// Writes a small versioned station-temperature repo at
// examples/data/station_temps.icechunk, using the repo's own fixture writer
// (Blade.IcechunkWrite, src/providers/IcechunkWrite.fs) end to end. The
// history is authored for the notebook's story:
//
//   tag  v1.0    -> s_raw        raw ingest: a +1.5 K warm bias in the
//                                eastern third (lon index >= 8)
//   main         -> s_corrected  the bias-correction commit. DATA-ONLY:
//                                the coordinate arrays are byte-identical,
//                                so their manifest and chunk files are
//                                REUSED (content-derived ids) — which is
//                                what licenses cross-checkout arithmetic
//                                in the notebook (plan section 5).
//   regrid       -> s_regrid     lat re-gridded at twice the resolution:
//                                a genuinely different axis, so mixing it
//                                with main REFUSES.
//   release      -> s_corrected  a branch AND a tag of the same name (the
//   release(tag) -> s_raw        bare-name ambiguity refusal fixture).
//
// The bias band is 4 of 12 longitudes at exactly +1.5, so
// mean(main - v1.0) = -0.5 exactly — the notebook pins it.
//
// Everything is analytic (no RNG) and IcechunkWrite's ids are content-
// derived, so the committed repo is byte-stable across regenerations.
//
// Run from anywhere, AFTER `dotnet build Blade.fsproj -c Release`:
//   dotnet fsi examples/tools/make_station_icechunk.fsx
// (Regenerates the committed repo in place; idempotent. Prints the snapshot
// ids the notebook's pinned-checkout cell bakes in.)

#r "../../bin/Release/net10.0/Google.FlatBuffers.dll"
#r "../../bin/Release/net10.0/ZstdSharp.dll"
#r "../../bin/Release/net10.0/Blade.IcechunkFormat.dll"
#load "../../src/providers/IcechunkWrite.fs"

open System
open System.IO
open Blade.IcechunkWrite

let nT, nLat, nLon = 24, 10, 12
let nLatFine = 20

// Coordinate values. time in hours; lat/lon in degrees.
let timeHours = [| for t in 0 .. nT - 1 -> float t |]
let latCoarse = [| for la in 0 .. nLat - 1 -> 30.0 + 0.5 * float la |]
let latFine   = [| for la in 0 .. nLatFine - 1 -> 30.0 + 0.25 * float la |]
let lonVals   = [| for lo in 0 .. nLon - 1 -> 100.0 + 0.5 * float lo |]

/// The "true" field: a lat gradient, a weak lon tilt, a diurnal cycle.
let baseTemp (latDeg: float) (lonIdx: int) (t: int) =
    20.0 + 0.8 * (latDeg - 30.0) - 0.1 * float lonIdx
         + 2.0 * sin (2.0 * Math.PI * float t / float nT)

/// Row-major (time, lat, lon) cells over a given latitude axis.
let field (lats: float[]) (bias: bool) : float[] =
    [| for t in 0 .. nT - 1 do
         for la in 0 .. lats.Length - 1 do
           for lo in 0 .. nLon - 1 ->
             baseTemp lats.[la] lo t + (if bias && lo >= 8 then 1.5 else 0.0) |]

/// The domain's latitude span as a CF-style bounds pair. Two cells, so a
/// compile-time fold of it stays legible: `let static` materializes a provider
/// array as a structural TUPLE (StaticValue has no array carrier), which is
/// informative at 2 elements and noise at 24.
let latBounds (lats: float[]) =
    mkArray "lat_bounds" ["bnds"] [2L] [2L] (IceF64 [| lats.[0]; lats.[lats.Length - 1] |])

let coord name (vals: float[]) (dim: string) =
    mkArray name [dim] [int64 vals.Length] [int64 vals.Length] (IceF64 vals)

let tempArray (lats: float[]) (bias: bool) =
    // One chunk per 8 time steps: 3 native chunks (well over the 512-byte
    // inline threshold), exercising the baked-table read path.
    mkArray "temp" ["time"; "lat"; "lon"]
            [int64 nT; int64 lats.Length; int64 nLon]
            [8L; int64 lats.Length; int64 nLon]
            (IceF64 (field lats bias))

let coarseCoords = [
    coord "time_hours" timeHours "time"
    coord "lat" latCoarse "lat"
    coord "lon" lonVals "lon"
    latBounds latCoarse
]

let spec =
    { emptyRepo with
        Implementation = "blade-examples"
        Seed = 42
        Snapshots = [
            mkSnapshot "s_raw"       (tempArray latCoarse true  :: coarseCoords)
            mkSnapshot "s_corrected" (tempArray latCoarse false :: coarseCoords)
            mkSnapshot "s_regrid"    (tempArray latFine   false ::
                                      [ coord "time_hours" timeHours "time"
                                        coord "lat" latFine "lat"
                                        coord "lon" lonVals "lon"
                                        latBounds latFine ])
        ]
        Branches = [ "main", "s_corrected"; "regrid", "s_regrid"; "release", "s_corrected" ]
        Tags = [ "v1.0", "s_raw"; "release", "s_raw" ]
    }

let root =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "data", "station_temps.icechunk")
    |> Path.GetFullPath

writeRepo root spec

printfn "repo:    %s" root
for s in spec.Snapshots do
    printfn "%-12s snapshot %s" s.Name (snapshotId spec s.Name)
printfn "refs:    main + regrid + release (branches); v1.0 + release (tags)"
printfn "check:   mean(main.temp - v1.0.temp) = -0.5 exactly"
