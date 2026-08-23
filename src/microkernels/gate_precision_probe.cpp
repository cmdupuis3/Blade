// gate_precision_probe.cpp -- how much can Blade's stdout gate SEE?
//
// Blade's two byte-exactness gates (tests/InterpDiff.fs, tests/DiffOracle.fs)
// compare NORMALIZED STDOUT.  Stdout is written by the emitted main wrapper as
//     cout << std::setprecision(15);
// (src/CodeGen.fs:2536/2548/2608/2620, src/display/DisplayFrame.fs).
// A double needs 17 significant digits to round-trip, so the gate is a LOSSY
// projection of the value.  This probe measures the loss directly: given two
// doubles that differ by k ULP, how often does the printed text differ?
//
// Arms:
//   p15  -- exactly what the gate sees (the emitted print block's stream)
//   p17  -- the round-trip control; must read ~100%
// Ranges are chosen to span a binade (ULP/decimal-step varies 8x across one),
// which is why a single number is not the answer.
//
// build: g++ -O2 -std=c++17 -o gpp.exe gate_precision_probe.cpp
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <random>
#include <sstream>
#include <iomanip>
#include <string>
#include <vector>

static std::string fmt(double x, int prec) {
    std::ostringstream o;
    o << std::setprecision(prec) << x;   // defaultfloat, exactly the emitted form
    return o.str();
}
static double ulp_step(double x, int k) {
    uint64_t b; std::memcpy(&b, &x, 8);
    b += (uint64_t)k;                     // positive normal finite: +k ULP
    double y; std::memcpy(&y, &b, 8);
    return y;
}

int main(int argc, char** argv) {
    const long TRIALS = (argc > 1) ? atol(argv[1]) : 2000000;
    std::mt19937_64 rng(0x9E3779B97F4A7C15ull);
    std::uniform_real_distribution<double> U(0.0, 1.0);

    struct Range { const char* name; double lo, hi; };
    // A gram/fold output lands wherever the data puts it; these span the
    // decades a corpus value realistically occupies, plus one full binade
    // sweep to expose the 8x intra-binade variation.
    Range ranges[] = {
        {"[1,2)      (low binade)",   1.0,   2.0},
        {"[8,16)     (high binade)",  8.0,  16.0},
        {"[1,10)     one decade",     1.0,  10.0},
        {"[1e2,1e3)",                1e2,  1e3},
        {"[1e5,1e6)",                1e5,  1e6},
        {"[1e-3,1e-2)",              1e-3, 1e-2},
    };
    int ks[] = {1, 2, 4, 8, 16, 64};

    printf("Detection rate of a k-ULP difference through the gate's own printer\n");
    printf("  p15 = `cout << setprecision(15)` = what InterpDiff/DiffOracle compare\n");
    printf("  p17 = round-trip control\n");
    printf("  %-26s %6s %10s %10s\n", "value range", "kULP", "p15 seen", "p17 seen");
    for (auto& r : ranges) {
        for (int k : ks) {
            long seen15 = 0, seen17 = 0;
            for (long t = 0; t < TRIALS; ++t) {
                double x = r.lo + (r.hi - r.lo) * U(rng);
                double y = ulp_step(x, k);
                if (fmt(x,15) != fmt(y,15)) ++seen15;
                if (fmt(x,17) != fmt(y,17)) ++seen17;
            }
            printf("  %-26s %6d %9.3f%% %9.3f%%\n", r.name, k,
                   100.0*seen15/TRIALS, 100.0*seen17/TRIALS);
        }
    }

    // How many printed values must differ before the gate is likely to fire?
    // A whole-array compare fails if ANY cell's text differs.  For a per-cell
    // detection probability q, an N-cell output is caught with 1-(1-q)^N.
    printf("\nWhole-output detection: P(at least one of N printed cells differs)\n");
    printf("  %8s", "q(1ULP)");
    int Ns[] = {1, 10, 100, 1000, 10000};
    for (int N : Ns) printf(" %10s%d", "N=", N);
    printf("\n");
    double qs[] = {0.0222, 0.0625, 0.1778};
    const char* qn[] = {"0.0222 (low binade)", "0.0625 (typical)", "0.1778 (high binade)"};
    for (int i = 0; i < 3; ++i) {
        printf("  %-22s", qn[i]);
        for (int N : Ns) printf(" %10.4f%%", 100.0*(1.0 - std::pow(1.0-qs[i], (double)N)));
        printf("\n");
    }
    return 0;
}
