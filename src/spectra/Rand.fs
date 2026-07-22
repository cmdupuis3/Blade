/// Bit-exact mirror of cpp/rand_runtime.hpp (blade_rand::uniform):
/// SplitMix64 finalizer -> std::mt19937_64 -> top-53-bits [0,1) map.
/// This lets oracle fixtures consume the SAME streams the generated code
/// draws via `import rand`, so rand-driven spectra pins are exact.
/// Cross-checked against tests/corpus/rand/001_determinism.blade's pins.
module BladeSpectra.Rand

/// SplitMix64 finalizer (rand_runtime.hpp mix64).
let mix64 (z0: uint64) : uint64 =
    let z = z0 + 0x9E3779B97F4A7C15UL
    let z = (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
    let z = (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
    z ^^^ (z >>> 31)

/// std::mt19937_64: standard MT19937-64 with the single-uint64 seed
/// constructor (bit-exact per the C++ standard).
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

/// Top 53 bits scaled to [0,1) (rand_runtime.hpp bits_to_unit).
let bitsToUnit (x: uint64) : float =
    float (x >>> 11) * (1.0 / 9007199254740992.0)

/// blade_rand::uniform — n draws for a stream key.
let uniform (key: int64) (n: int) : float[] =
    let g = Mt19937_64(mix64 (uint64 key))
    Array.init n (fun _ -> bitsToUnit (g.Next()))
