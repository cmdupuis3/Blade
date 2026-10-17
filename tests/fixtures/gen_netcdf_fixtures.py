"""Regenerate the NetCDF fixtures the provider boundary rule (BL8015) needs.

    python tests/fixtures/gen_netcdf_fixtures.py

Needs the netCDF4 and numpy packages. Run from the repository root.

1. tests/fixtures/sample.nc -- REWRITTEN IN PLACE, data bit for bit.

   The old file's `B_mask` (byte, xdim x ydim) was never written: every cell
   held NC_FILL_BYTE (-127), which load_compound's "nonzero = present" read as
   PRESENT, so `load_compound(B, B_mask)` handed the program B's 9450 NaN fill
   cells (189 whole z-columns) as data. This script copies every dimension,
   variable and attribute, copies A and B as raw float32 buffers (no decimal
   round trip -- ncgen from CDL is not bit-exact for NaN payloads), and WRITES
   B_mask: 1 where B's z-column holds data, 0 where it is all fill. It refuses
   to write if any column is partly fill, and verifies A and B afterwards.
   The sha256 of A and B (float32 bytes) is printed; tests/NetcdfTests.fs pins it.

2. tests/fixtures/missing.nc -- one variable per missing-value rule:

     fill   float(r, c)  _FillValue -999   two cells hold -999          -> refused
     mv     double(r, c) missing_value -1  one cell holds -1            -> refused
     vr     float(r, c)  valid_range [0, 100], one cell 150             -> refused
     unw    double(r, c) no _FillValue, row 1 never written (default fill) -> refused
     nanv   double(r, c) no attributes, one NaN cell                    -> refused
     ok     double(r, c) _FillValue -999, no cell holds it              -> reads
     ifill  int(r, c)    _FillValue -9, one cell holds -9               -> refused
     iraw   int(r, c)    no attributes, one cell holds NC_FILL_INT      -> reads (integer
                         default fills are representable data: refused only on an
                         explicit attribute)
     i64    int64(r, c)  _FillValue NC_FILL_INT64; one cell holds it, one holds its
                         double-rounded neighbour  -> exactly 1 cell refused
                         (integer sentinels compare as int64)

   r = 2, c = 3; cell values are small exact decimals so pins are exact.
"""
import hashlib
import os
import sys

import numpy as np
import netCDF4

HERE = os.path.dirname(os.path.abspath(__file__))


def sha(arr):
    return hashlib.sha256(np.ascontiguousarray(arr).tobytes()).hexdigest()


def regenerate_sample():
    path = os.path.join(HERE, "sample.nc")
    tmp = path + ".tmp"
    src = netCDF4.Dataset(path, "r")
    src.set_auto_maskandscale(False)
    A = src.variables["A"][:]
    B = src.variables["B"][:]
    assert A.dtype == np.float32 and B.dtype == np.float32
    fiber_nan = np.isnan(B).all(axis=2)
    fiber_any = np.isnan(B).any(axis=2)
    if (fiber_any & ~fiber_nan).any():
        sys.exit("refusing: some z-column of B is only PARTLY fill; a (x, y) mask cannot describe it")
    mask = np.where(fiber_nan, 0, 1).astype(np.int8)
    dst = netCDF4.Dataset(tmp, "w", format=src.data_model)
    for name, dim in src.dimensions.items():
        dst.createDimension(name, None if dim.isunlimited() else len(dim))
    for name, var in src.variables.items():
        attrs = {k: var.getncattr(k) for k in var.ncattrs()}
        fill = attrs.pop("_FillValue", None)
        out = dst.createVariable(name, var.dtype, var.dimensions,
                                 fill_value=fill if fill is not None else False)
        out.set_auto_maskandscale(False)
        for k, v in attrs.items():
            out.setncattr(k, v)
        out[:] = mask if name == "B_mask" else var[:]
    dst.close()
    src.close()
    os.replace(tmp, path)
    chk = netCDF4.Dataset(path, "r")
    chk.set_auto_maskandscale(False)
    assert chk.variables["A"][:].tobytes() == A.tobytes(), "A changed"
    assert chk.variables["B"][:].tobytes() == B.tobytes(), "B changed"
    assert (chk.variables["B_mask"][:] == mask).all()
    chk.close()
    print("sample.nc: B_mask written (%d present, %d absent columns)" % (int(mask.sum()), int((mask == 0).sum())))
    print("  sha256 A float32 bytes:", sha(A))
    print("  sha256 B float32 bytes:", sha(B))


def write_missing():
    path = os.path.join(HERE, "missing.nc")
    ds = netCDF4.Dataset(path, "w", format="NETCDF4")
    ds.createDimension("r", 2)
    ds.createDimension("c", 3)

    def var(name, dtype, fill=False, **attrs):
        v = ds.createVariable(name, dtype, ("r", "c"), fill_value=fill)
        v.set_auto_maskandscale(False)
        for k, a in attrs.items():
            v.setncattr(k, a)
        return v

    var("fill", "f4", fill=np.float32(-999.0))[:] = np.array([[1.5, -999.0, 2.5], [3.5, 4.5, -999.0]], np.float32)
    var("mv", "f8", missing_value=np.float64(-1.0))[:] = np.array([[1.0, 2.0, 3.0], [-1.0, 5.0, 6.0]])
    var("vr", "f4", valid_range=np.array([0.0, 100.0], np.float32))[:] = np.array([[10.0, 20.0, 150.0], [30.0, 40.0, 50.0]], np.float32)
    unw = ds.createVariable("unw", "f8", ("r", "c"))  # libnetcdf's default fill, no _FillValue attribute
    unw.set_auto_maskandscale(False)
    unw[0, :] = np.array([1.0, 2.0, 3.0])            # row 1 never written
    var("nanv", "f8")[:] = np.array([[1.0, np.nan, 3.0], [4.0, 5.0, 6.0]])
    var("ok", "f8", fill=np.float64(-999.0))[:] = np.array([[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]])
    var("ifill", "i4", fill=np.int32(-9))[:] = np.array([[1, 2, -9], [4, 5, 6]], np.int32)
    var("iraw", "i4")[:] = np.array([[1, -2147483647, 3], [4, 5, 6]], np.int32)
    # NC_FILL_INT64 and its double-rounded neighbour: only the fill is missing.
    var("i64", "i8", fill=np.int64(-9223372036854775806))[:] = np.array(
        [[1, -9223372036854775806, 3], [-9223372036854775807, 5, 6]], np.int64)
    ds.close()
    print("missing.nc written")


if __name__ == "__main__":
    regenerate_sample()
    write_missing()
