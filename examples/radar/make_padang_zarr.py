"""Merge one day of Padang radar rain-rate scans into a Blade-readable Zarr store.

Input:  a directory of PDG_rain_rates_YYYYMMDD_HHMMSS.nc4 files, each carrying
        rain_rate(z, lat, lon) float32 with _FillValue -9999 and 2-D curvilinear
        lat/lon coordinate grids. (Note: the files' lat/lon `units` attributes
        are swapped -- lon claims degrees_north -- so everything here keys off
        variable NAMES, never units.)

Output: <out>.zarr, a Zarr v3 store in exactly the subset Blade's ZarrProvider
        reads (src/providers/ZarrProvider.fs v1 scope): UNCOMPRESSED chunks
        (single little-endian "bytes" codec), C order, one raw binary file per
        chunk. Layout mirrors examples/data/qg_init_zarr:

          rain_rate  (scan, y, x)   f4, chunks (1, 500, 500)  -- one chunk per scan
          lat        (y)            f8   1-D axis, cut from the 2-D grid's center column
          lon        (x)            f8   1-D axis, cut from the 2-D grid's center row
          time_min   (scan)         f8   minutes since midnight, from the file names
          rain_zoom  (scan, yz, xz) f4   128x128 cutout centered (clamped to the
                                         grid) on the rainiest point, for contour
                                         plots at a scale where features span
                                         many pixels
          lat_zoom   (yz) / lon_zoom (xz)  the cutout's axes

        The leading dimension is named `scan`, NOT `time`: a provider dim
        named `time` reaches generated C++ as `using time = int64_t;` and
        collides with the C library.

        The z (elevation) axis is REDUCED here: by default the lowest sweep
        (z=0, the surface-rain convention); --composite takes the max over all
        six elevations instead. Fill values become 0.0 ("no rain detected") so
        reductions in Blade stay finite.

Also prints the day's rainiest point -- argmax over pixels of the time-summed
rain -- and the peak scan index, for pasting into the notebook as `let static`.

Usage:
  python make_padang_zarr.py <scan-dir> [--out PATH] [--composite]
"""

import argparse
import json
import re
import sys
from pathlib import Path

import numpy as np
from netCDF4 import Dataset

FILL = -9999.0
NAME_RE = re.compile(r"_(\d{8})_(\d{2})(\d{2})(\d{2})\.nc4$")


def chunk_dir_layout(n_chunks_leading: int) -> bool:
    """Blade's provider uses the v3 default chunk key encoding: c/<i>/<j>/..."""
    return True


def write_array(root: Path, name: str, data: np.ndarray, dim_names, chunk_shape):
    """Write one uncompressed v3 array: zarr.json + raw little-endian chunks."""
    arr_dir = root / name
    arr_dir.mkdir(parents=True, exist_ok=True)
    dtype_name = {"float32": "float32", "float64": "float64"}[str(data.dtype)]
    meta = {
        "zarr_format": 3,
        "node_type": "array",
        "shape": list(data.shape),
        "data_type": dtype_name,
        "chunk_grid": {"name": "regular", "configuration": {"chunk_shape": list(chunk_shape)}},
        "chunk_key_encoding": {"name": "default", "configuration": {"separator": "/"}},
        "fill_value": 0,
        "codecs": [{"name": "bytes", "configuration": {"endian": "little"}}],
        "dimension_names": list(dim_names),
    }
    (arr_dir / "zarr.json").write_text(json.dumps(meta), encoding="utf-8", newline="\n")

    # Chunk grid walk. Edge chunks must be stored FULL-SIZE (padded); the
    # provider copies only the intersection with the array bounds.
    counts = [-(-s // c) for s, c in zip(data.shape, chunk_shape)]
    for idx in np.ndindex(*counts):
        sl = tuple(slice(i * c, min((i + 1) * c, s)) for i, c, s in zip(idx, chunk_shape, data.shape))
        block = data[sl]
        if block.shape != tuple(chunk_shape):
            padded = np.zeros(chunk_shape, dtype=data.dtype)
            padded[tuple(slice(0, b) for b in block.shape)] = block
            block = padded
        chunk_path = arr_dir / "c" / Path(*[str(i) for i in idx])
        chunk_path.parent.mkdir(parents=True, exist_ok=True)
        block.astype(block.dtype.newbyteorder("<")).tofile(chunk_path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("scan_dir", type=Path)
    ap.add_argument("--out", type=Path, default=None, help="store path (default: <scan-dir parent>/padang_<date>.zarr)")
    ap.add_argument("--composite", action="store_true", help="max over elevations instead of lowest sweep")
    args = ap.parse_args()

    files = sorted(args.scan_dir.glob("*.nc4"))
    if not files:
        sys.exit(f"no .nc4 files in {args.scan_dir}")

    m = NAME_RE.search(files[0].name)
    if not m:
        sys.exit(f"file name {files[0].name} does not match _YYYYMMDD_HHMMSS.nc4")
    date = m.group(1)
    out = args.out or (args.scan_dir.parent / f"padang_{date}.zarr")

    frames, minutes = [], []
    lat1d = lon1d = None
    for f in files:
        m = NAME_RE.search(f.name)
        if not m:
            print(f"  skipping {f.name} (unrecognized name)", file=sys.stderr)
            continue
        hh, mm, ss = int(m.group(2)), int(m.group(3)), int(m.group(4))
        minutes.append(hh * 60 + mm + ss / 60.0)
        with Dataset(f) as ds:
            rr = ds.variables["rain_rate"][:]  # (z, lat, lon), masked array
            rr = np.ma.filled(rr, FILL)
            frame = rr.max(axis=0) if args.composite else rr[0]
            frame = np.where(frame == FILL, 0.0, frame).astype(np.float32)
            frames.append(frame)
            if lat1d is None:
                lat2d = np.ma.filled(ds.variables["lat"][:], np.nan)
                lon2d = np.ma.filled(ds.variables["lon"][:], np.nan)
                mid = lat2d.shape[0] // 2
                lat1d = lat2d[:, mid].astype(np.float64)  # center column: latitude along rows
                lon1d = lon2d[mid, :].astype(np.float64)  # center row: longitude along columns

    R = np.stack(frames)  # (time, y, x)
    t = np.asarray(minutes, dtype=np.float64)
    nt, ny, nx = R.shape

    root = out
    if root.exists():
        sys.exit(f"{root} already exists -- remove it first (refusing to overwrite)")
    root.mkdir(parents=True)
    (root / "zarr.json").write_text(json.dumps({"zarr_format": 3, "node_type": "group"}), encoding="utf-8", newline="\n")

    write_array(root, "rain_rate", R, ["scan", "y", "x"], (1, ny, nx))
    write_array(root, "lat", lat1d, ["y"], (ny,))
    write_array(root, "lon", lon1d, ["x"], (nx,))
    write_array(root, "time_min", t, ["scan"], (nt,))

    # Rainiest point, EXCLUDING the radar's ground-clutter disk: pixels within
    # ~25 px of the grid center report rain nearly 100% of the day (measured
    # wet-fraction 1.00 vs a 0.39 p99 in the 15-40 px ring on 20180301), which
    # is backscatter, not weather. 40 px keeps a margin.
    total = R.sum(axis=0)
    yy_g, xx_g = np.mgrid[0:ny, 0:nx]
    dist = np.sqrt((yy_g - ny // 2) ** 2 + (xx_g - nx // 2) ** 2)
    iy, ix = np.unravel_index(np.argmax(np.where(dist >= 40, total, -1.0)), total.shape)
    series = R[:, iy, ix]
    t_peak = int(np.argmax(series))
    wet = float((series > 0.5).mean())
    t_wide = int(np.argmax(R.mean(axis=(1, 2))))
    hhmm = lambda x: f"{int(x) // 60:02d}:{int(x) % 60:02d}"

    # The zoom cutout: index-tag arithmetic is forbidden in Blade by design,
    # so a sub-window cannot be cut at the language level -- it is declared
    # here, at the data boundary, as its own array with its own axes.
    ZW = 128
    y0 = min(max(iy - ZW // 2, 0), ny - ZW)
    x0 = min(max(ix - ZW // 2, 0), nx - ZW)
    write_array(root, "rain_zoom", R[:, y0:y0 + ZW, x0:x0 + ZW].copy(), ["scan", "yz", "xz"], (1, ZW, ZW))
    write_array(root, "lat_zoom", lat1d[y0:y0 + ZW].copy(), ["yz"], (ZW,))
    write_array(root, "lon_zoom", lon1d[x0:x0 + ZW].copy(), ["xz"], (ZW,))

    print(f"store:        {root}")
    print(f"shape:        rain_rate({nt}, {ny}, {nx})  [{'max composite' if args.composite else 'z=0 lowest sweep'}]")
    print(f"day max:      {R.max():.2f} mm/h")
    print(f"rainiest pt (>=40 px from radar): iy={iy} ix={ix}  (lat={lat1d[iy]:.4f}, lon={lon1d[ix]:.4f})")
    print(f"  daily total {total[iy, ix]:.1f}, series max {series.max():.2f} at scan {t_peak} ({hhmm(t[t_peak])}), wet {wet:.0%} of scans")
    print(f"widest rain:  scan {t_wide} ({hhmm(t[t_wide])}), domain mean {R.mean(axis=(1, 2))[t_wide]:.3f} mm/h")
    print(f"zoom cutout:  rain_zoom = rain_rate[:, {y0}:{y0 + ZW}, {x0}:{x0 + ZW}]")
    print("paste into the notebook (as inline cast literals, e.g. (303 : Y)):")
    print(f"  iy_rainy = {iy}   ix_rainy = {ix}   t_peak = {t_peak}   t_wide = {t_wide}")


if __name__ == "__main__":
    main()
