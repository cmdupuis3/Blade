# Blade Examples

Worked end-to-end programs. Each example names the features it exercises and,
where applicable, the test category that pins the behavior
(`tests/corpus/`). Every block below is a complete program: `blade test docs`
compiles and runs it and checks its `// EXPECT:` pins. Longer programs live in
`examples/`. Tutorials: [quickstart-1.md](quickstart-1.md),
[quickstart-2.md](quickstart-2.md).

## 1. Covariance matrix from a NetCDF file

*Features: type providers, loop objects, comm + identity, symmetric output.*

```blade sketch
// sketch: needs an era5.nc on disk -- the provider reads its metadata at compile time
import netcdf as nc
from stats import mean

function covariance(a: T^1, b: T^1) where comm(a, b) -> T^0 =
    mean((a - mean(a)) * (b - mean(b)))

let era5 = nc.load("era5.nc")
let t2m = era5.vars.t2m |> nc.read      // lat x lon x time, typed from the file
let cov = object_for(covariance) <@> (t2m, t2m) |> compute
// cov : one SymIdx<2, ...> over the compound (lat, lon) space
```

The kernel consumes the trailing time dimension of each copy; the two
compound spatial positions are jointly symmetric (2× storage and iteration
savings over compound pairs). Note the single joint `SymIdx` over the
compound spatial space — not one `SymIdx` per dimension (formalism §12.4).
[quickstart-1.md](quickstart-1.md) §7 runs the same computation on inline
data.

## 2. One generator, every comoment

*Features: arity polymorphism, recursion over poly-packs, identity base case.
Corpus: `arity`, `loops`.*

```blade
from stats import mean

function comoment_prod(a: Poly<T^1>) where comm(a) -> T^1 = {
    match arity(a) with
    | 0 -> 1.0                        // the empty product: identity
    | _ -> let head :: tail = a
           (head - mean(head)) * comoment_prod(tail)
}

function comoment(a: Poly<T^1>) where comm(a) -> T^0 = mean(comoment_prod(a))

let data = [[1.0, 2.0, 4.0, 7.0], [2.0, 1.0, 0.0, 1.0], [3.0, 3.0, 5.0, 5.0]]
let m = object_for(comoment)
let cov    = m <@> (data, data)             |> compute   // SymIdx<2, 3>,  2x
let coskew = m <@> (data, data, data)       |> compute   // SymIdx<3, 3>,  6x
let cokurt = m <@> (data, data, data, data) |> compute   // SymIdx<4, 3>, 24x
let var0 = cov(0, 0)
let skew0 = coskew(0, 0, 0)
// EXPECT: var0 = 5.25
// EXPECT: skew0 = 6
```

## 3. SELECT ... WHERE ... ORDER BY

*Features: relational suite. Corpus: `sql-combined`, `sql-masks`, `sql-sort`.*

```blade
// SELECT temp FROM temps WHERE temp > 25 ORDER BY temp DESC
let temps = [21.0, 30.5, 26.0, 18.0, 28.0, 26.0]
let m   = mask(temps, lambda(t) -> t > 25.0)   // Bool presence array
let hot = compound(temps, m)                   // compact CompoundIdx view
let out = sort(hot, lambda(t) -> -t)           // stable, key-based, dense result

let count = extents(hot)                       // COUNT(*) WHERE ...
let total = reduce(hot)                        // SUM   (default kernel (+))
// EXPECT: out = [30.5, 28, 26, 26]
// EXPECT: count = 4
// EXPECT: total = 110.5
```

## 4. GROUP BY with ragged aggregation

*Features: group_keys/group_by, ragged arrays, per-group reduce.
Corpus: `sql-group-by`.*

```blade
// SELECT region, SUM(temp), COUNT(*) FROM stations GROUP BY region
type RegionIdx = EnumIdx<["north", "south"]>
type StationIdx = Idx<5>
let region: Array<RegionIdx like StationIdx> = ["north", "south", "north", "north", "south"]
let temps: Array<Float64 like StationIdx> = [10.0, 20.0, 12.0, 14.0, 22.0]

let gk      = group_keys(region)         // CSR structure; static because region is EnumIdx-typed
let grouped = group_by(temps, gk)        // rank-2 ragged array

let sums   = method_for(grouped) <@> lambda(g) -> reduce(g, (+)) |> compute
let sizes  = method_for(grouped) <@> lambda(g) -> extents(g)     |> compute
let grand  = reduce(sums)                // SUM ... then total
// EXPECT: sums = [36, 42]
// EXPECT: sizes = [3, 2]
// EXPECT: grand = 78
```

Uneven group sizes are fine — `grouped` is genuinely ragged. Elementwise maps
over `grouped` are rejected by design: map before grouping.

## 5. Semijoin with multiplicity

*Features: contains-in-mask idiom. Corpus: `sql-semijoins`.*

```blade
// Keep readings whose station appears in the QC-passed list; keep duplicates
let readings = [3, 7, 3, 9, 1, 7]
let passed = [7, 3]
let ok   = compound(readings, mask(readings, lambda(x) -> contains(passed, x)))
// Antijoin: the readings that did NOT pass
let bad  = compound(readings, mask(readings, lambda(x) -> !contains(passed, x)))
let dedup = intersect(readings, passed)
let n_ok = extents(ok)
let n_bad = extents(bad)
// EXPECT: n_ok = 4
// EXPECT: n_bad = 2
// EXPECT: dedup = [3, 7]
```

`intersect(readings, passed)` dedups — the mask idiom preserves
multiplicity.

## 6. Symmetrizing a non-commutative kernel (Reynolds)

*Features: reynolds, antisymmetric variant. Corpus: `reynolds`,
`index-types` 050–054.*

```blade
let A = [1.0, 2.0, 4.0]
let ratio = lambda(x, y) -> x / y              // not commutative

let sym = method_for(A, A) <@> reynolds(ratio) |> compute
// sym(i, j) = A(i)/A(j) + A(j)/A(i): symmetric VALUES, dense storage

let anti = method_for(A, A) <@> reynolds(ratio, Antisymmetric) |> compute
// anti(i, j) = -anti(j, i); the diagonal is identically zero

let ratio_c = lambda(x, y) where comm(x, y) -> x / y
let packed = method_for(A, A) <@> reynolds(ratio_c) |> compute
// same array in both positions + the comm license: SymIdx<2, 3> storage

let s01 = sym(0, 1)
let a01 = anti(0, 1)
let a10 = anti(1, 0)
let p01 = packed(1, 0)
// EXPECT: s01 = 2.5
// EXPECT: a01 = -1.5
// EXPECT: a10 = 1.5
// EXPECT: p01 = 2.5
```

With DISTINCT arrays the output is dense and not index-symmetric even with the
clause: identity is still required.

## 7. Stencil smoothing

*Features: the `halo` virtual array, windowed kernels. Corpus: `loops`, `access`.*

```blade
type Cells = Idx<6>
let A: Array<Float64 like Cells> = [0.0, 4.0, 8.0, 4.0, 0.0, 4.0]
let smoothed = method_for(halo<Cells, [-1, 0, 1]>) <@> lambda(w) -> 0.25 * A(w(-1)) + 0.5 * A(w(0)) + 0.25 * A(w(1)) |> compute
// EXPECT: smoothed = [4, 6, 4, 2]
```

The kernel receives a window `w`; `w(o)` is the index at offset `o`. The
`[-1, 0, 1]` window shrinks the traversal to the interior, so the boundary is
handled once, by the index space, and no read can fall off the ends.
(Periodic / reflecting boundary modes and the `stencil` / `align` bundles are
planned, formalism §2.6; a periodic wrap today is an explicit `% n` on a
position, `examples/08_burgers_les.blade`.)

## 8. Ocean-only data with a compound index

*Features: CompoundIdx (mask-derived, lex-sorted), flat indexing.
Corpus: `index-types` 001, 010, 015–017 (002–014 are the reject cases).*

```blade
type LatIdx = Idx<2>
type LonIdx = Idx<3>
let sst_dense: Array<Float64 like LatIdx, LonIdx> = [[15.0, 16.0, 0.0], [0.0, 18.0, 19.0]]
let ocean_mask: Array<Bool like LatIdx, LonIdx> = [[true, true, false], [false, true, true]]
let sst = compound(sst_dense, ocean_mask)   // only ocean points stored

let n_ocean = extents(sst)                  // cardinality = number of ocean points
let mean_sst = reduce(sst) / Float64(n_ocean)
// EXPECT: n_ocean = 4
// EXPECT: mean_sst = 17
```

A compound axis indexes flat and full-arity, like `SymIdx` (`sst(lat, lon)`,
valid points only). Iteration over compound views is lexicographic over
mask-true cells, matching dense scan order — the mask makes the valid-tuple
table sorted by construction, which is what keeps the layout contiguous.

## 8b. Edge lists with a sparse index

*Features: SparseIdx, partial indexing with wildcards, residual sparse.
Corpus: `index-types` 171–184.*

```blade
let static edges = [(0, 3), (2, 1), (1, 0), (3, 1)]   // key order is iteration order
let weights = [0.5, 1.5, 2.0, 4.0]
let w = sparse(weights, edges)                // one value per key, in key order

let e21 = w((2, 1))                           // O(1) hash lookup on the full key
let into1 = w((_, 1))                         // every edge INTO node 1: a plain Idx<n_matching>
// EXPECT: e21 = 1.5
// EXPECT: into1 = [1.5, 4]
```

Where a compound's validity derives from a mask over a grid, a sparse index
takes the valid tuples *explicitly* — and because its table is hashed rather
than sorted, pinning any subset of coordinates costs the same single gather
over the entries. The residual of a key set is a key set (a rank-3 key set
read at `w((0, _, _))` is a residual `SparseIdx` over the free axes).

## 9. Fused multi-statistic pass

*Features: loop reuse, mandatory fusion, sequential composition.
Corpus: `loops`, `guard-combinators`.*

```blade
from stats import mean, variance
let data = [[1.0, 2.0, 3.0, 4.0], [2.0, 2.0, 2.0, 2.0]]
let L = method_for(data)

// One traversal, two results:
let (means, vars) = (L <@> mean) <&!> (L <@> variance) |> compute

// Staged: demean, then the sum of squares of the result, same loop structure:
function demean(row: T^1) -> T^1 = row - mean(row)
function sumsq(row: T^1) -> T^0 = reduce(row * row, (+))
let ss = (L <@> demean) @>> (L <@> sumsq) |> compute

// Conditional pipeline with algebraic fallback (per cell: the right side
// fills where the guarded side is zero):
let enough = extents(data(0)) >= 10
let est = guard(enough, L <@> variance) <|> (L <@> mean) |> compute
// EXPECT: means = [2.5, 2]
// EXPECT: vars = [1.25, 0]
// EXPECT: ss = [5, 0]
// EXPECT: est = [2.5, 2]
```

## 10. Interop: compact to dense and back

*Features: decompact, gram, hermitian, complex. Corpus: `index-types`
034–049, 059–071.*

```blade
let V = [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]
let G = gram(V, V)                        // Gram matrix; SymIdx<2, 3> storage
let D = decompact(G, 0) |> compute        // dense 3 x 3 for external tools
let g10 = D(1, 0)

let Z = [[complex(1.0, 1.0), complex(0.0, 2.0)], [complex(3.0, 0.0), complex(1.0, -1.0)]]
let Gh = gram(Z, Z)                       // complex input: HermitianIdx storage
let Zh = hermitian(Z)                     // adjoint; conj(x) elementwise
// EXPECT: g10 = 11
```

`decompact` on an `AntisymIdx` axis reconstructs signs; chaining it across
axes yields fully dense output.

## 11. Units catching a real bug

*Features: units of measure, nominal index typing. Corpus: `units`,
`index-types` 092–104.*

```blade prelude
Unit meters
Unit seconds
type Speed = Float<meters / seconds>

let dist = 100.0: Float<meters>
let t    = 9.58: Float<seconds>
```

```blade
let v: Speed = dist / t      // OK
```

```blade rejects
let oops = dist + t          // meters + seconds
// ERROR: BL3006
```

The same mechanism guards indices: an index value carries its index type, so
a `LatIdx` value cannot subscript a `LonIdx` axis, even at equal extents:

```blade rejects
type LatIdx = Idx<4>
type LonIdx = Idx<4>
let A: Array<Float64 like LatIdx> = [1.0, 2.0, 3.0, 4.0]
let B: Array<Float64 like LonIdx> = [5.0, 6.0, 7.0, 8.0]
let ok = method_for(range<LatIdx>) <@> lambda(i) -> A(i) |> compute
let bad = method_for(range<LatIdx>) <@> lambda(i) -> B(i) |> compute
// ERROR: BL4003
```

## 12. Foreign keys without a join engine

*Features: integer foreign keys, captured-array deref. Corpus:
`sql-foreign-keys`.*

```blade
type StationIdx = Idx<4>
type RegionIdx = Idx<3>                                // pacific, atlantic, indian
let station_region: Array<Nat<RegionIdx> like StationIdx> = [0, 2, 0, 1]
let region_weight: Array<Float64 like RegionIdx> = [1.0, 0.5, 2.0]
let values: Array<Float64 like StationIdx> = [10.0, 20.0, 30.0, 40.0]

// Weighted per-station values: deref the FK inside the kernel
let weighted = method_for(zip(station_region, values)) <@> lambda(r, v) -> v * region_weight(r) |> compute
// EXPECT: weighted = [10, 40, 30, 20]
```

The key column's element type `Nat<RegionIdx>` says its values are positions
in `RegionIdx`, so `region_weight(r)` subscripts with an index value of the
right type (an untyped `Int64` column works too, with a BL4003 advisory). (A
key column typed by a string `EnumIdx` — `Array<RegionIdx like StationIdx>`
with `RegionIdx = EnumIdx<["pacific", ...]>` — groups correctly with
`group_keys`, but using its element as a subscript is currently rejected by
the C++ back end with BL9002: a known compiler bug, not a language rule.)
