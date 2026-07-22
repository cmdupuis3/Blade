# Blade Examples

Worked end-to-end programs. Each example names the features it exercises and,
where applicable, the v7 test category that pins the behavior
(`tests/corpus/`). Tutorials: [quickstart-1.md](quickstart-1.md),
[quickstart-2.md](quickstart-2.md).

## 1. Covariance matrix from a NetCDF file

*Features: type providers, loop objects, comm + identity, symmetric output.*

```blade
type ERA5 = NetCDFProvider<"era5.nc">
// ERA5.t2m : Array<Float like Idx<721>, Idx<1440>, Idx<8760>>

function covariance(a: T^1, b: T^1)
where comm(a, b)
= mean((a - mean(a)) * (b - mean(b)))

let t2m = read(ERA5.t2m)
let cov = object_for(covariance) <@> (t2m, t2m) |> compute
// cov : Array<Float like SymIdx<2, <Idx<721>, Idx<1440>>>>
```

The kernel consumes the trailing time dimension of each copy; the two
compound spatial positions are jointly symmetric (2× storage and iteration
savings over compound pairs). Note the single joint `SymIdx` over the
compound spatial space — not one `SymIdx` per dimension (formalism §12.4).

## 2. One generator, every comoment

*Features: arity polymorphism, recursion over poly-packs, zero base case.
Corpus: `arity`, `loops`.*

```blade
function comoment_prod(a: Poly<T^1>)
where comm(a) -> T^1 = {
    match arity with
    | 0 -> zero                       // identity (1) in this product context
    | _ -> let head :: tail = a
           (head - mean(head)) * comoment_prod(tail)
}

function comoment(a: Poly<T^1>)
where comm(a) -> T^0 = mean(comoment_prod(a))

let m = object_for(comoment)
let cov    = m <@> (data, data)             |> compute   // SymIdx<2, X>,  2x
let coskew = m <@> (data, data, data)       |> compute   // SymIdx<3, X>,  6x
let cokurt = m <@> (data, data, data, data) |> compute   // SymIdx<4, X>, 24x
```

## 3. SELECT ... WHERE ... ORDER BY

*Features: relational suite. Corpus: `sql-combined`, `sql-masks`, `sql-sort`.*

```blade
// SELECT temp FROM temps WHERE temp > 25 ORDER BY temp DESC
let m   = mask(temps, lambda(t) -> t > 25.0)   // Bool presence array
let hot = compound(temps, m)                   // compact CompoundIdx view
let out = sort(hot, lambda(t) -> -t)           // stable, key-based, dense result

let count = extents(hot)                       // COUNT(*) WHERE ...
let total = reduce(hot)                        // SUM   (default kernel (+))
```

## 4. GROUP BY with ragged aggregation

*Features: group_keys/group_by, ragged arrays, per-group reduce.
Corpus: `sql-group-by`.*

```blade
// SELECT region, SUM(temp) FROM stations GROUP BY region
let gk      = group_keys(region)         // CSR structure; static if region: EnumIdx
let grouped = group_by(temps, gk)        // rank-2 ragged array

let sums   = method_for(grouped) <@> lambda(g) -> reduce(g, (+)) |> compute
let sizes  = method_for(grouped) <@> lambda(g) -> extents(g)     |> compute
let grand  = reduce(sums)                // SUM ... then total
```

Uneven group sizes are fine — `grouped` is genuinely ragged. Elementwise maps
over `grouped` are rejected by design: map before grouping.

## 5. Semijoin with multiplicity

*Features: contains-in-mask idiom. Corpus: `sql-semijoins`.*

```blade
// Keep readings whose station appears in the QC-passed list; keep duplicates
let ok   = compound(readings, mask(readings, lambda(x) -> contains(passed, x)))
// Antijoin: the readings that did NOT pass
let bad  = compound(readings, mask(readings, lambda(x) -> !contains(passed, x)))
```

`intersect(readings, passed)` would dedup — the mask idiom preserves
multiplicity.

## 6. Symmetrizing a non-commutative kernel (Reynolds)

*Features: reynolds, antisymmetric variant. Corpus: `reynolds`,
`index-types` 050–054.*

```blade
let ratio = lambda(x, y) -> x / y              // not commutative

let sym = method_for(A, A) <@> reynolds(ratio) |> compute
// sym(i, j) = A(i)/A(j) + A(j)/A(i) — symmetric; triangular SymIdx storage
// (same array in both positions: reynolds's manufactured commutativity plus
// identity licenses the collapse — with DISTINCT arrays the output is dense
// and not index-symmetric; identity is still required)

let anti = method_for(A, A) <@> reynolds(ratio, Antisymmetric) |> compute
// anti(i, j) = -anti(j, i); diagonal is identically zero; stored AntisymIdx
```

## 7. Stencil smoothing with explicit boundaries

*Features: stencil/align/shift, elementwise pipelines.*

```blade
let smoothed = method_for(stencil(A, {0: [-1, 0, 1]}, Shrink))
    <@> lambda(left, center, right) -> 0.25*left + 0.5*center + 0.25*right
    |> compute
```

The kernel receives three separate neighbor arguments; boundary policy
(`Shrink | Pad(v) | Periodic | Reflect`) is declared once, at the data level.

## 8. Ocean-only data with a compound index

*Features: CompoundIdx, partial indexing, residual compounds.
Corpus: `index-types` 001–017.*

```blade
let ocean_mask: Array<Bool like LatIdx, LonIdx> = read(...)
let sst = compound(sst_dense, ocean_mask)   // only ocean points stored

sst((lat, lon))    // coordinate-based read (valid points only)
sst((lat, _))      // all valid lons at this lat  : plain Idx<n_valid>
extents(sst)       // cardinality = number of ocean points
```

Fixing some coordinates of a higher-rank compound leaves a *residual
compound* — the residual of a mask is a mask. Iteration over compound views
is lexicographic over mask-true cells, matching dense scan order.

## 9. Fused multi-statistic pass

*Features: loop reuse, mandatory fusion, sequential composition.
Corpus: `loops`, `guard-combinators`.*

```blade
let L = method_for(data)

// One traversal, two results:
let stats = (L <@> mean) <&!> (L <@> variance) |> compute

// Staged: demean, then variance of the result, same loop structure:
let v = (L <@> demean) @>> (L <@> variance) |> compute

// Conditional pipeline with algebraic fallback:
let r = guard(has_enough_samples, L <@> variance) <|> (L <@> fallback_est)
        |> compute
```

## 10. Interop: compact to dense and back

*Features: decompact, gram, hermitian, complex. Corpus: `index-types`
034–049, 059–071.*

```blade
let S: Array<Float like SymIdx<2, n>> = ...
let D = decompact(S, 0) |> compute        // dense n × n for external tools

let G  = gram(V)                          // Gram matrix; symmetric storage
let Gh = gram(Z)                          // complex input: HermitianIdx storage
let Zh = hermitian(Z)                     // adjoint; conj(x) elementwise
```

`decompact` on an `AntisymIdx` axis reconstructs signs; chaining it across
axes yields fully dense output.

## 11. Units catching a real bug

*Features: units of measure, nominal index typing. Corpus: `units`,
`index-types` 092–104.*

```blade
Unit meters; Unit seconds
type Speed = Float<meters / seconds>

let dist = 100.0: Float<meters>
let t    = 9.58:  Float<seconds>
let v: Speed = dist / t      // OK
let oops = dist + t          // compile error: meters + seconds

// The same mechanism guards indices:
method_for(range<LatIdx>) <@> lambda(i) ->
    A(i)     // OK: A expects LatIdx
    // B(i)  // compile error if B expects LonIdx — even at equal extents
```

## 12. Foreign keys without a join engine

*Features: EnumIdx key domains, captured-array deref. Corpus:
`sql-foreign-keys`.*

```blade
type RegionIdx = EnumIdx<["pacific", "atlantic", "indian"]>
let station_region : Array<RegionIdx like StationIdx> = ...
let region_weight  : Array<Float like RegionIdx> = ...

// Weighted per-station values: deref the FK inside the kernel
let weighted = method_for(station_region, values)
    <@> lambda(r, v) -> v * region_weight(r)
    |> compute
```
