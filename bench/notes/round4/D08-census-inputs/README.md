# D08 frozen topology inputs — runner only after root dispatch

See ../D08-design.md for the exact bounded request and proposed API/region.
No production code or compiler admission is included. The author did not run
this adapter, lower/emit the fixture, compile it or evaluate expected values.

- `01-statistic-barrier.blade`: one rank-one computed producer, seeded first
  fold, scalar barrier and seeded transformed terminal; select result only.
- `fixture.zgroup.json`, `fixture.zarray.json`, `fixture.zattrs.json`: Zarr v2,
  N5/C2/<f8/identity codec, x axis and finite zero fill.
- `chunks.tsv`: three fixed 16-byte padded chunks encoded as hexadecimal;
  logical values 1,2,3,4,5 and one zero tail-padding cell.
- `probe.fsx`: materializes only those fixed files in a NEW private directory,
  lowers/validates once and emits baseline.cpp once against the explicitly
  referenced existing approved DLL. Environment/cwd restore in finally.

Independent expected p=[2,5,10,17,26], t=65, m=13, result=382. These are hand
calculations, not observed output. This probe captures topology/storage and
normal reader order only; it never compiles/runs generated C++ or uses D06.
Retain any exact first refusal. No alternate spelling, hidden rebuild, test,
benchmark, output-directory overwrite or evidence deletion is authorized here.
