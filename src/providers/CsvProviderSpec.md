# CSV Provider Specification (v1)

`import csv as c` — comma-separated text files as compile-time-shaped array
I/O, alongside the netcdf and zarr providers. Pure std C++17 at runtime
(`<fstream>`/`<sstream>`, no link flags); pure .NET at compile time.
Implementation: `providers/CsvProvider.fs`; gate: `blade test csv`
(hermetic; e2e blocks skip without g++).

## Surface

```blade
import csv as c

// Headered column table (first row has any non-numeric cell):
//   time,temp,pressure
//   0.0,14.0,101.2
let t = c.load("obs.csv")
let obs = t.vars.data |> c.read     // Array<Float64, rows x cols>
let x = obs(1, "temp")              // column access BY LABEL (see below)

// Headerless all-numeric grid -> matrix mode:
let m = c.load("V.csv")
let V = m.vars.data |> c.read       // Array<Float64, R x C>, plain Idx axes

let _ = c.write("out.csv", V)       // rank-1: one value/line; rank-2: comma rows

let static S = m.vars.data |> c.read  // small files fold at compile time
```

Every CSV module exposes exactly one variable, `vars.data`, always rank 2.

## File-shape sniffing

Applied identically by the F# parser (metadata / fold / interpreter) and the
generated C++ reader:

> Split the first non-empty record on commas. If **every** cell parses as a
> number, the file is a **matrix** (R x C, plain anonymous `Idx` axes).
> Otherwise the first row is a **header**: each cell becomes a column label,
> and the column axis is a synthesized `EnumIdx` over the labels (in file
> order) named `<binding>_cols`.

- Labels are arbitrary **strings** (EnumIdx values, not identifiers): they
  must be non-empty and unique, nothing more. `time,2` is a header with
  labels `"time"` and `"2"`.
- The one ambiguity is inherent to CSV: a header whose labels ALL look
  numeric reads as a matrix. Unsupported — rename a column.

## Column access by label

The headered var types as `Array<elem like <rows>, <binding>_cols>`. A
**string literal** in the column-axis position folds to its ordinal at
type-check time (`TypeCheck.foldEnumIdxLabels`), so lowering, codegen, and
the interpreter all see a plain constant subscript:

```blade
let t1 = obs(1, "temp")     // -> obs(1, 1): "temp" is column ordinal 1
```

An unknown label is a compile error (BL3007) naming the available labels.
Restrictions: string literals only (a runtime string variable as a column
subscript is not supported), and int-valued EnumIdx keys are deliberately
NOT folded — they keep their raw foreign-key semantics (sql-foreign-keys
corpus).

## Format rules (v1)

Enforced identically at compile time (F#) and runtime (C++); every
violation names the file and 1-based line:

| Rule | Behavior |
| --- | --- |
| Delimiter | Comma only. |
| Quoting | None. Any `"` anywhere is an error. |
| Line endings | LF and CRLF both accepted (one trailing `\r` stripped per line). |
| BOM | A UTF-8 BOM on line 1 is stripped. |
| Trailing newline | One tolerated. Interior blank lines are errors. |
| Ragged rows | Error (cell count must match line 1). |
| Empty cells | Error. |
| Data cells | Numeric only — string columns are deferred (`ProviderPayload` carries only floats/ints). |
| Number parsing | Locale-independent (InvariantCulture / `strtod` under the never-set "C" locale). `nan`, `inf`, `-inf` accepted as float specials. |

## Dtype inference

Whole-table homogeneous (the EnumIdx column axis makes `data` one array):

- Every data cell matches `^[+-]?[0-9]+$` → **Int64**.
- Otherwise every cell must parse as a float → **Float64** (so `1e5` and
  `2.5` are floats; one decimal cell floats the whole table).

## Writes

`c.write("path.csv", A)` for rank 1 or 2, dense only (packed/symmetric
groups rejected; literal extents required):

- No header row (v1). Rank-1 writes one value per line — a single column
  that **re-loads as an R x 1 matrix**, not rank-1.
- Floats print at 17 significant digits (`max_digits10`) with a **forced
  decimal point**: a whole-valued float renders `2.0`, never `2`, so the
  file re-loads as Float64 (dtype stability). `nan`/`inf` renderings are
  left untouched. Int64 arrays print bare integers.
- LF line endings, trailing newline.

## Resolution contract (paths and cwd)

The `c.load` path is a compile-time string literal, resolved **twice**:

1. At compile time against the **compiler process cwd** (metadata + folds).
2. At runtime against the **executable's cwd** (`blade run` places the exe
   next to the `.blade` file and pins its cwd there).

The two agree when you run from the source file's directory with relative
paths (`cd examples/physics && blade run 42_dynamical_q.blade` with
`data/...` paths). The runtime reader re-validates the baked shape: row or
column drift between compile and run aborts with `CSV error: ...` and a
nonzero exit.

## Fold and interpreter

- `let static ... |> c.read` folds through the provider-neutral bridge
  (`ProviderStatics.readAndFold`): 65536-element ceiling, sha256 provenance
  line, mtime-stamped memoization. Nothing csv-specific.
- The interpreter materializes dense csv reads via the same `readVarData`
  (byte-parity is structural); `c.write` classifies unsupported and falls
  back to the compiled path, like zarr writes.

## v1 exclusions (all rejected loudly)

Strings in data cells; quoting/escaping; non-comma delimiters; header row
on write; `.stream`; `load_compound`; packed (SymIdx/AntisymIdx) reads and
writes; rank > 2 writes; runtime (non-literal) column-label subscripts.

## Internals note: provider-synthesized EnumIdx

CSV is the first provider whose `LoadAsModule` emits an `IRTDEnumIdx` and
uniquely-named structs (`<binding>__vars`) so several loads coexist in one
program. `TypeEnv.registerProviderModule` registers both (the enum with a
synthesized `TyEnumIdx` body); netcdf/zarr's literal `dims`/`vars` naming is
unchanged.
