# zarr_stores/

Generated zarr fixture stores for `blade test zarr` (see `tests/ZarrTests.fs`).

Every store in here is written on the fly by `ZarrProvider.ZarrWrite` at the
start of the relevant test section — nothing is committed or hand-maintained,
and the whole directory is safe to delete (the next `blade test zarr` run
recreates what it needs).

Two copies of each fixture exist during a test run:

- `providers/zarr_stores/<store>` (this directory) — resolved at the
  **compiler's** cwd for compile-time metadata loads (`z.load("...")`) and
  static folds.
- `generated_cpp_tests/providers/zarr_stores/<store>` — the mirror resolved at
  the **test executable's** cwd for runtime reads; `*_out` stores written by
  the compiled programs land there too.

The same relative path string (`providers/zarr_stores/<store>`) is baked into
the blade sources so it resolves correctly from either working directory.
