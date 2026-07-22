/// Bit-exact F# mirror of cpp/rand_runtime.hpp (namespace blade_rand) for the
/// Blade tree-walking interpreter. Every draw here must byte-match what the
/// generated C++ binary produces so interpreter output == compiled output.
///
/// SOURCE OF TRUTH: cpp/rand_runtime.hpp. If that file changes, this mirror
/// MUST be updated in lockstep (and revalidated with the probe in
/// scratchpad, see the arc report). The `mix64`/`Mt19937_64`/`bitsToUnit`
/// core is a faithful copy of spectra/Rand.fs (module BladeSpectra.Rand),
/// which is the pre-existing, oracle-validated uniform mirror; it is copied
/// here rather than shared to avoid a cross-project (.fsproj) file-include
/// coupling and to keep this module self-contained under Blade.Interp.
///
/// Codegen contract mirrored (CodeGen.fs genRandGenBinding, ~L8187-8213):
///   blade_rand::<kind>(pool_base(A.data), card, key)
/// where `card` = product of ALL extents (dense SymNone, row-major flat pool),
/// `key` is the int64 stream key, one draw per pool slot, filled in flat order.
/// `normal` consumes TWO uniform draws per element (Box-Muller, cos branch
/// only; the sin partner is NOT cached — see `next_normal` below).
module Blade.Interp.RandMirror

// ---------------------------------------------------------------------------
// Core stream (copy of spectra/Rand.fs BladeSpectra.Rand)
// ---------------------------------------------------------------------------

/// SplitMix64 finalizer (rand_runtime.hpp `mix64`): decorrelates nearby keys
/// before seeding the engine.
let mix64 (z0: uint64) : uint64 =
    let z = z0 + 0x9E3779B97F4A7C15UL
    let z = (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
    let z = (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
    z ^^^ (z >>> 31)

/// std::mt19937_64: MT19937-64 with the single-uint64 seed constructor
/// (bit-exact per the C++ standard).
type Mt19937_64(seed: uint64) =
    let n = 312
    let m = 156
    let matrixA = 0xB5026F5AA96619E9UL
    let upperMask = 0xFFFFFFFF80000000UL
    let lowerMask = 0x7FFFFFFFUL
    let mt = Array.zeroCreate<uint64> n
    let mutable mti = n
    do
        mt.[0] <- seed
        for i in 1 .. n - 1 do
            mt.[i] <- 6364136223846793005UL * (mt.[i-1] ^^^ (mt.[i-1] >>> 62)) + uint64 i
    member _.Next() : uint64 =
        if mti >= n then
            for i in 0 .. n - 1 do
                let x = (mt.[i] &&& upperMask) ||| (mt.[(i + 1) % n] &&& lowerMask)
                let mutable xa = x >>> 1
                if x &&& 1UL <> 0UL then xa <- xa ^^^ matrixA
                mt.[i] <- mt.[(i + m) % n] ^^^ xa
            mti <- 0
        let mutable y = mt.[mti]
        mti <- mti + 1
        y <- y ^^^ ((y >>> 29) &&& 0x5555555555555555UL)
        y <- y ^^^ ((y <<< 17) &&& 0x71D67FFFEDA60000UL)
        y <- y ^^^ ((y <<< 37) &&& 0xFFF7EEE000000000UL)
        y ^^^ (y >>> 43)

/// 2^-53 = 1.0 / 9007199254740992.0 (rand_runtime.hpp scale/floor constant).
let private twoPow53Inv : float = 1.0 / 9007199254740992.0

/// Top 53 bits scaled to [0,1) (rand_runtime.hpp `bits_to_unit`).
let bitsToUnit (x: uint64) : float =
    float (x >>> 11) * twoPow53Inv

/// rand_runtime.hpp `next_uniform`: one uniform draw from the engine.
let inline private nextUniform (g: Mt19937_64) : float =
    bitsToUnit (g.Next())

/// rand_runtime.hpp `next_normal`: Box-Muller, TWO uniforms -> one N(0,1).
/// u1 is floored away from 0 so log(u1) stays finite. The sin partner is NOT
/// produced or cached: every call consumes exactly two fresh uniform draws and
/// returns only the cos branch. Arithmetic/order/literal match the header.
let private nextNormal (g: Mt19937_64) : float =
    let twoPi = 6.283185307179586476925286766559
    let mutable u1 = nextUniform g
    let u2 = nextUniform g
    if u1 < twoPow53Inv then u1 <- twoPow53Inv
    sqrt (-2.0 * log u1) * cos (twoPi * u2)

// ---------------------------------------------------------------------------
// Public draw APIs (match blade_rand::uniform / blade_rand::normal)
// ---------------------------------------------------------------------------

/// blade_rand::uniform(out, n, key) — `n` draws ~ U[0,1) for stream `key`.
let uniform (key: int64) (n: int) : float[] =
    let g = Mt19937_64(mix64 (uint64 key))
    Array.init n (fun _ -> nextUniform g)

/// blade_rand::normal(out, n, key) — `n` draws ~ N(0,1) via Box-Muller for
/// stream `key`. Each element consumes two uniform draws (no caching).
let normal (key: int64) (n: int) : float[] =
    let g = Mt19937_64(mix64 (uint64 key))
    Array.init n (fun _ -> nextNormal g)

/// Dispatch on the runtime `kind` string ("uniform" | "normal") that codegen
/// records in RandGen (CodeGen.fs). Matches the internal builtin call surface
/// (__rand_uniform / __rand_normal) — a single int64 key, `n` flat draws.
let draws (kind: string) (key: int64) (n: int) : float[] =
    match kind with
    | "uniform" -> uniform key n
    | "normal"  -> normal key n
    | other -> failwithf "RandMirror.draws: unknown rand kind '%s' (expected 'uniform' | 'normal')" other

// ---------------------------------------------------------------------------
// RandomFillSpec executor
// ---------------------------------------------------------------------------

/// A filled random array: the flat, row-major pool plus its extents. Mirrors
/// the dense-SymNone pool codegen emits (genRandGenBinding): a single flat
/// `card`-length draw sequence, where `card` = product of extents.
type FilledRandom = { Data: float[]; Extents: int list }

/// Execute a rand.uniform/normal fill exactly as CodeGen.fs genRandGenBinding
/// emits it: card = product of extents (row-major flat pool), one blade_rand
/// call of `card` draws keyed by `key`, filled in flat order. `kind` is the
/// RandGen kind ("uniform" | "normal"); `key` is the already-evaluated int64
/// stream key; `extents` are the (all positive, static) dense extents.
let runFill (kind: string) (key: int64) (extents: int list) : FilledRandom =
    let card = extents |> List.fold (fun acc e -> acc * e) 1
    { Data = draws kind key card; Extents = extents }
