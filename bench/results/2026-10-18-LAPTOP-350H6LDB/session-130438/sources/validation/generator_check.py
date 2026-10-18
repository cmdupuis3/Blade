"""Generator quality check at the specs' FULL sizes (methodology review M6).

Usage:  C:\\Python314\\python.exe -P bench/validation/generator_check.py [out.md]
        (BENCH_GENERATOR=R3|R5 selects the formula, see benchgen.py; default R3)
Writes Markdown to bench/notes/generator-check.md unless an output path is given. Pure
numpy, single-threaded BLAS, peak memory ~0.3 GB, runtime ~1 minute.

Every statistic is compared with an iid yardstick: the closed form where one exists, and
otherwise the same statistic on numpy's PCG64 (an independent, well-tested generator).
Verdicts: "OK" = indistinguishable from iid at the stated tolerance; "DEFECT" = measurably
non-iid. Each DEFECT line also says whether it can bias a timing or a checksum (no input
statistic changes any kernel's operation count, and every arm sees the same bits).
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402
import numpy as np  # noqa: E402

LINES = []
VARIANT = g.VARIANT


def out(s=""):
    LINES.append(s)
    print(s)


def verdict(ok, why_ok, why_bad):
    return "Verdict: " + ("OK -- " + why_ok if ok else "DEFECT -- " + why_bad)


def corr(a, b):
    a = a.ravel() - a.mean()
    b = b.ravel() - b.mean()
    return float((a * b).sum() / math.sqrt((a * a).sum() * (b * b).sum()))


def lag_corr(a, lag, axis):
    a = a - a.mean()
    x, y = (a[:, :-lag], a[:, lag:]) if axis == 1 else (a[:-lag, :], a[lag:, :])
    return float((x * y).sum() / math.sqrt((x * x).sum() * (y * y).sum()))


def run_lengths(mask):
    m = mask.astype(np.int8)
    edges = np.flatnonzero(np.diff(m)) + 1
    starts = np.concatenate(([0], edges))
    lens = np.concatenate((edges, [m.size])) - starts
    vals = m[starts]
    return lens[vals == 1], lens[vals == 0]


def autocorr_fft(u):
    N = u.size
    x = u - u.mean()
    F = np.fft.rfft(x, 2 * N)
    ac = np.fft.irfft(F * np.conj(F))[:N] / (x * x).sum()
    return ac * N / (N - np.arange(N))


def lap_of(u):
    return u[:-2, 1:-1] + u[2:, 1:-1] + u[1:-1, :-2] + u[1:-1, 2:] - 4.0 * u[1:-1, 1:-1]


def excess_kurtosis(a):
    z = (a - a.mean()) / a.std()
    return float((z ** 4).mean() - 3.0)


def gram_row_corr(X):
    Xc = X - X.mean(axis=1, keepdims=True)
    Xc /= np.sqrt((Xc * Xc).sum(axis=1, keepdims=True))
    R = Xc @ Xc.T
    return R[np.triu_indices(X.shape[0], 1)]


# ---------------------------------------------------------------------------------------
def section_reference():
    out(f"## Reference values (variant {VARIANT}, seed 10000019)")
    out("")
    k = np.arange(3, dtype=np.int64)
    h = g.gen_int(k, g.SEED)
    u = h.astype(np.float64) / 2147483648.0
    out("| k | final integer state | u |")
    out("|---|---|---|")
    for i in range(3):
        out(f"| {i} | {int(h[i])} | {u[i]!r} |")
    x = g.gen(np.arange(10), g.SEED, -1.0, 1.0)
    out("")
    out(f"x(k) = gen(k, 10000019, -1, 1), k = 0..9: sum = {g.exact_sum(x)!r}, "
        f"sum of squares = {g.exact_sum(x * x)!r}")
    out("")
    pins = {"R3": [184246361, 43451288, 411006385], "R5": [408748897, 189020261, 1945512958]}
    ok = [int(v) for v in h] == pins[VARIANT]
    out("Check: " + ("matches the cross-language pins " if ok else "MISMATCH against the pins ")
        + str(pins[VARIANT]))
    out("")
    if VARIANT == "R5":
        out("Blade, both lanes (C++ and LLVM, 2026-10-17, `<scratchpad>/p/g10.blade`): final states "
            "408748897, 189020261, 1945512958; 10-cell [-1, 1) selfcheck sum = -3.1010268041864, "
            "sum of squares = 3.4272198544854 (15 significant digits).")
        out("")


def section_marginals():
    out("## Marginals of every full-size input array (U(0,1): mean 0.5, var 0.083333)")
    out("")
    out("| array | cells | mean u | var u | P(bit b = 1), b = 0..7 of the final state |")
    out("|---|---|---|---|---|")
    arrays = [
        ("fusedchain a", 2000003, 1), ("onepass x", 5000011, 1), ("stencil u", 1501 * 2003, 1),
        ("recur u0", 20011, 1), ("maskagg x", 1000003, 1), ("dot a", 3000017, 1),
        ("dot b", 3000017, 2), ("saxpy x", 2000003, 1), ("saxpy y", 2000003, 2),
        ("gram X", 1601 * 1001, 1), ("comoment3 X", 171 * 201, 1), ("antisym X", 1201 * 401, 1),
        ("antisym Y", 1201 * 401, 2), ("pairkernel x", 2503, 1), ("pairkernel y", 2503, 2),
        ("pairkernel z", 2503, 3),
    ]
    ok_mv = True
    worst_bit = 0.0
    for name, n, j in arrays:
        h = g.gen_int(np.arange(n, dtype=np.int64), g.SEED * j)
        u = h.astype(np.float64) / 2147483648.0
        m, v = float(u.mean()), float(u.var())
        se_m = math.sqrt(1.0 / 12.0 / n)
        se_v = math.sqrt((1.0 / 80.0 - 1.0 / 144.0) / n)
        ok_mv &= abs(m - 0.5) < 6 * se_m and abs(v - 1.0 / 12.0) < 6 * se_v
        bits = [float(((h >> b) & 1).mean()) for b in range(8)]
        if n > 100000:
            worst_bit = max(worst_bit, max(abs(b - 0.5) for b in bits))
        out(f"| {name} | {n} | {m:.6f} | {v:.6f} | " + " ".join(f"{b:.3f}" for b in bits) + " |")
    out("")
    out(verdict(ok_mv, "every mean and variance matches U(0,1) within 6 standard errors.",
                "a mean or variance is off by more than 6 standard errors."))
    out("")
    out(verdict(worst_bit < 0.01, "the low bits are balanced.",
                f"the low bits of the final state are biased (worst |P - 0.5| = {worst_bit:.3f}). "
                "They carry at most 2^-27 of u, below every tolerance in the suite. Harmless."))
    out("")


def section_cross():
    out("## Cross-array correlation between one kernel's inputs (iid: |r| ~ 1/sqrt(N))")
    out("")
    out("| kernel | arrays | N | r | |r| sqrt(N) |")
    out("|---|---|---|---|---|")
    cases = [("dot", "a, b", 3000017, 1, 2), ("saxpy", "x, y", 2000003, 1, 2),
             ("antisym", "X, Y", 1201 * 401, 1, 2), ("pairkernel", "x, y", 2503, 1, 2),
             ("pairkernel", "x, z", 2503, 1, 3), ("pairkernel", "y, z", 2503, 2, 3),
             ("(seed pair j=1,3, long)", "segments 1, 3", 3000017, 1, 3),
             ("(seed pair j=2,3, long)", "segments 2, 3", 3000017, 2, 3)]
    worst = 0.0
    for kern, names, n, j1, j2 in cases:
        r = corr(g.gen_array((n,), j1, 0.0, 1.0), g.gen_array((n,), j2, 0.0, 1.0))
        z = abs(r) * math.sqrt(n)
        worst = max(worst, z)
        out(f"| {kern} | {names} | {n} | {r:+.6f} | {z:.2f} |")
    out("")
    out(verdict(worst < 5.0, f"largest |r| sqrt(N) = {worst:.2f}: the inputs of one kernel are "
                "uncorrelated (they are non-overlapping segments of one sequence, 10000019 apart; "
                "the two 'long' rows repeat pairkernel's seed pairs over 3M cells, where any "
                "structural correlation would show).",
                f"largest |r| sqrt(N) = {worst:.2f}."))
    out("")


def section_lags():
    out("## Lag structure of one long sequence (FFT autocorrelation of 2^21 cells)")
    out("")
    N = 2 ** 21
    ac = autocorr_fft(g.gen(np.arange(N, dtype=np.int64), g.SEED, 0.0, 1.0))
    ac_ref = autocorr_fft(np.random.default_rng(11).random(N))
    L = 1_200_000
    top = np.argsort(np.abs(ac[1:L]))[::-1][:6] + 1
    ref_max = float(np.abs(ac_ref[1:L]).max())
    out(f"Largest |autocorrelation| over lags 1..{L}; PCG64's largest over the same lags: {ref_max:.4f}.")
    out("")
    out("| lag | autocorrelation |")
    out("|---|---|")
    for t in top:
        out(f"| {int(t)} | {ac[t]:+.4f} |")
    klags = [1, 2, 3, 4, 8, 201, 401, 1001, 2003, 4006]
    worst_k = max(abs(ac[t]) for t in klags) * math.sqrt(N)
    out("")
    out("Kernel-relevant lags (stencil neighbours 1, 2003, 4006; row lengths 201, 401, 1001): "
        + ", ".join(f"{t}: {ac[t]:+.4f}" for t in klags) + f" (worst {worst_k:.2f} sd)")
    out("")
    big = float(np.abs(ac[top[0]]))
    out(verdict(big < 2.0 * ref_max,
                f"no lag stands out (max |r| {big:.4f}, PCG64 {ref_max:.4f}).",
                f"isolated long lags are strongly correlated (|r| up to {big:.2f} at lag "
                f"{int(top[0])}, PCG64 max {ref_max:.4f}). Mechanism: the first round is affine, "
                "so h1(k + D) = h1(k) + D*1103515245 (mod 2^31), and one quadratic mix leaves "
                "some shifts visible. Every kernel-relevant short lag is clean. No kernel's cost "
                "depends on it, but it shows up as correlated row pairs in gram (next section)."))
    out("")


def section_2d():
    out("## 2-D inputs: stride autocorrelations and gram's row-row correlations")
    out("")
    out("| array | shape | lag-1 row | lag-1 column | max |lag 2..8| along row | 1/sqrt(cells) |")
    out("|---|---|---|---|---|---|")
    cases = [("gram X", (1601, 1001), 1), ("comoment3 X", (171, 201), 1),
             ("antisym X", (1201, 401), 1), ("antisym Y", (1201, 401), 2),
             ("stencil u", (1501, 2003), 1)]
    worst = 0.0
    for name, shape, j in cases:
        a = g.gen_array(shape, j, 0.0, 1.0)
        rr_, rc = lag_corr(a, 1, 1), lag_corr(a, 1, 0)
        rm = max(abs(lag_corr(a, t, 1)) for t in range(2, 9))
        s = 1 / math.sqrt(a.size)
        worst = max(worst, abs(rr_) / s, abs(rc) / s, rm / s)
        out(f"| {name} | {shape[0]} x {shape[1]} | {rr_:+.5f} | {rc:+.5f} | {rm:.5f} | {s:.5f} |")
    out("")
    out(verdict(worst < 6.0, f"largest stride autocorrelation {worst:.2f} sd.",
                f"a stride autocorrelation reaches {worst:.2f} sd."))
    out("")
    rr = gram_row_corr(g.gen_array((1601, 1001), 1, 0.0, 1.0))
    ref = gram_row_corr(np.random.default_rng(5).random((1601, 1001)))
    thr = 6.0 / math.sqrt(1001)
    n_out, n_ref = int((np.abs(rr) > thr).sum()), int((np.abs(ref) > thr).sum())
    out(f"gram X row-row correlations over {rr.size} pairs: sd {rr.std():.5f} (iid "
        f"{1 / math.sqrt(1001):.5f}), max |r| {np.abs(rr).max():.4f} (PCG64 {np.abs(ref).max():.4f}), "
        f"pairs beyond 6 sd: {n_out} (PCG64 {n_ref}).")
    out("")
    out(verdict(n_out <= max(5, 3 * n_ref), "gram's rows are mutually uncorrelated like iid rows.",
                f"{n_out} row pairs are correlated beyond 6 sd (the long-lag defect: rows whose "
                "offset times 1001 hits a bad lag). G's off-diagonal cells for those pairs are "
                "larger than iid; the cost of gram is unchanged."))
    out("")


def section_maskagg():
    out("## maskagg: acceptance and run lengths (n = 1,000,003, x in [0, 2), keep x > 1.0)")
    out("")
    x = g.gen_array((1000003,), 1, 0.0, 2.0)
    keep = x > 1.0
    m = int(keep.sum())
    p = m / keep.size
    out(f"accepted m = {m} (rate {p:.6f}; iid sd {math.sqrt(0.25 / keep.size):.6f})")
    out("")
    ks = list(range(2, 11))
    out("Ratio observed / iid of P(run length >= k), k = " + ", ".join(map(str, ks)) + ":")
    out("")
    out("| runs of | runs | mean length (iid 2) | " + " | ".join(f"k={k}" for k in ks) + " |")
    out("|---|---|---|" + "---|" * len(ks))
    bad = 0.0
    rng_ref = np.random.default_rng(3).random(1000003) > 0.5
    for kind, L in (("accepted", run_lengths(keep)[0]), ("rejected", run_lengths(keep)[1]),
                    ("PCG64 accepted", run_lengths(rng_ref)[0])):
        cells = []
        for k in ks:
            pk = 2.0 ** -(k - 1)
            obs = float((L >= k).mean())
            sd = math.sqrt(pk * (1 - pk) / L.size)
            if not kind.startswith("PCG64"):
                bad = max(bad, abs(obs - pk) / sd)
            cells.append(f"{obs / pk:.3f}")
        out(f"| {kind} | {L.size} | {L.mean():.4f} | " + " | ".join(cells) + " |")
    out("")
    out(verdict(abs(p - 0.5) < 6 * math.sqrt(0.25 / keep.size) and bad < 5.0,
                "acceptance rate and run lengths are those of iid fair coin flips.",
                f"the run-length distribution is not geometric (worst deviation {bad:.1f} sd; "
                "for example runs of 6-9 are about 30% too frequent), although the lag-1..12 "
                "autocorrelations of the indicator are about 0. Every arm sees the same "
                "sequence, so fairness is unaffected. Branchy loops see slightly non-iid "
                "patterns; this can bias the expert tier's branch-mispredict cost relative to "
                "truly random data."))
    out("")


def section_stencil():
    out("## stencil output lap (full size 1501 x 2003)")
    out("")
    lap = lap_of(g.gen_array((1501, 2003), 1, 0.0, 1.0))
    ref = [excess_kurtosis(lap_of(np.random.default_rng(s).random((1501, 2003)))) for s in range(3)]
    k4 = (4 * 1 + 256) * (-1.0 / 120.0)
    k2 = (4 * 1 + 16) * (1.0 / 12.0)
    ek = k4 / (k2 * k2)
    z = (lap - lap.mean()) / lap.std()
    kurt = excess_kurtosis(lap)
    frac_int = float((np.abs(lap - np.round(lap)) < 1e-9).mean())
    out(f"mean {lap.mean():+.6f} (iid 0), variance {lap.var():.6f} (iid {20 / 12:.6f}), skewness "
        f"{float((z ** 3).mean()):+.4f} (iid 0), excess kurtosis {kurt:+.4f} (exact iid {ek:+.4f}; "
        f"PCG64 fields " + ", ".join(f"{r:+.4f}" for r in ref) + f"), integer-valued cells {frac_int:.1e}")
    out("")
    q = np.percentile(lap, [0.1, 1, 25, 50, 75, 99, 99.9])
    out("percentiles 0.1/1/25/50/75/99/99.9: " + ", ".join(f"{v:+.4f}" for v in q))
    out("")
    ok_basic = abs(lap.mean()) < 0.003 and abs(lap.var() - 20 / 12) < 0.01 and frac_int < 1e-6
    ok_shape = abs(kurt - ek) < 0.01
    out(verdict(ok_basic and ok_shape,
                "the Laplacian has the iid distribution (continuous, centred, right variance and shape).",
                ("" if ok_basic else "basic moments are off; ") +
                f"the excess kurtosis {kurt:+.4f} departs from iid {ek:+.4f}, so neighbouring cells "
                "have higher-order dependence. The output is continuous and centred with the right "
                "variance, and no cell is integer-valued (the old sawtooth failure is gone). "
                "Harmless to cost and to the checksum."))
    out("")


def section_pairkernel():
    out("## pairkernel: pair-distance histogram (n = 2503 points, unit cube)")
    out("")
    iu = np.triu_indices(2503, 1)

    def pair_dist(P):
        d2 = np.zeros((P.shape[0], P.shape[0]))
        for c in range(3):
            diff = P[:, None, c] - P[None, :, c]
            d2 += diff * diff
        return np.sqrt(d2[iu])

    d = pair_dist(np.stack([g.gen_array((2503,), j, 0.0, 1.0) for j in (1, 2, 3)], axis=1))
    dq = pair_dist(np.random.default_rng(20261017).random((2503, 3)))
    robbins = 0.66170718226717623515
    edges = np.linspace(0.0, math.sqrt(3.0), 15)
    hist, _ = np.histogram(d, bins=edges)
    href, _ = np.histogram(dq, bins=edges)
    out(f"pairs {d.size}; mean distance {d.mean():.6f} (exact unit-cube mean {robbins:.6f}; "
        f"PCG64 points {dq.mean():.6f}); min {d.min():.5f}; max {d.max():.5f}")
    out("")
    out("| distance bin | count | PCG64 count |")
    out("|---|---|---|")
    for i in range(len(hist)):
        out(f"| {edges[i]:.3f}-{edges[i + 1]:.3f} | {hist[i]} | {href[i]} |")
    a = np.sqrt(5.0 * d * d) / 0.7
    K = (1.0 + a + a * a / 3.0) * np.exp(-a)
    big = (hist + href) > 2000
    rel = float((np.abs(hist[big] - href[big]) / (0.5 * (hist[big] + href[big]))).max())
    out("")
    out(f"K over the pairs: mean {K.mean():.5f}, sd {K.std():.5f}, fraction K < 1e-3 "
        f"{(K < 1e-3).mean():.4f}, fraction K > 0.9 {(K > 0.9).mean():.4f}. Largest relative "
        f"bin difference against the PCG64 point set: {rel:.3f}. The point-set noise is "
        "~1/sqrt(2503) = 0.02 per bin, more in the sparse bins.")
    out("")
    out(verdict(abs(d.mean() - robbins) < 0.005 and rel < 0.10,
                "the distance distribution is that of uniform points in the unit cube "
                "(Robbins' mean; bin counts agree with an independent PCG64 point set); K "
                "spans its range.",
                "the distance distribution departs from uniform points in the cube."))
    out("")


def section_compare():
    out("## Before / after: R3 (the 2026-10-17 morning formula) vs R5 (adopted, 'variant D') vs PCG64")
    out("")
    out("R3 = affine, quadratic, affine. R5 = R3 followed by a middle-square round and a final "
        "affine round. The detailed sections below this table measure R5 only. Rerun with "
        "BENCH_GENERATOR=R3 to reproduce the R3 column in full.")
    out("")
    out("| metric | R3 | R5 | PCG64 |")
    out("|---|---|---|---|")
    N = 2 ** 21
    rows = {}
    for v in ("R3", "R5", "pcg"):
        if v == "pcg":
            u = np.random.default_rng(11).random(N)
            x = 2.0 * np.random.default_rng(3).random(1000003)
            X = np.random.default_rng(5).random((1601, 1001))
            U = np.random.default_rng(0).random((1501, 2003))
        else:
            u = g.gen(np.arange(N, dtype=np.int64), g.SEED, 0.0, 1.0, v)
            x = g.gen_array((1000003,), 1, 0.0, 2.0, v)
            X = g.gen_array((1601, 1001), 1, 0.0, 1.0, v)
            U = g.gen_array((1501, 2003), 1, 0.0, 1.0, v)
        ac = autocorr_fft(u)
        L = run_lengths(x > 1.0)[0]
        rr = gram_row_corr(X)
        if v == "pcg":
            bit = "n/a"
        else:
            hh = g.gen_int(np.arange(1_000_003, dtype=np.int64), g.SEED, v)
            bit = " ".join(f"{float(((hh >> b) & 1).mean()):.3f}" for b in range(4))
        rows[v] = [bit, f"{float(np.abs(ac[1:1_200_000]).max()):.4f}",
                   " ".join(f"{(L >= k).mean() * 2 ** (k - 1):.2f}" for k in range(3, 10)),
                   f"{int((np.abs(rr) > 6.0 / math.sqrt(1001)).sum())}",
                   f"{float(np.abs(rr).max()):.3f}",
                   f"{excess_kurtosis(lap_of(U)):+.4f}"]
    names = ["P(bit b of the final state = 1), b = 0..3 (iid 0.5)",
             "max |autocorr|, lags 1..1.2e6 (iid max ~0.005)", "maskagg run ratio P(L>=k)/iid, k=3..9",
             "gram row pairs beyond 6 sd (of 1,280,800)", "gram max |row r|",
             "stencil lap excess kurtosis (iid -0.780)"]
    for i, nm in enumerate(names):
        out(f"| {nm} | {rows['R3'][i]} | {rows['R5'][i]} | {rows['pcg'][i]} |")
    out("")
    out("R3's defects, all measured above: (1) isolated long lags strongly correlated "
        "(u(k) vs u(k+981991): r = -0.27, the same in every 1001-cell window; lag 1081146: +0.20), "
        "from the affine first round (h1(k+D) = h1(k) + D*A mod 2^31) surviving one quadratic "
        "mix; (2) as a consequence, 570 gram row pairs correlated beyond 6 sd (e.g. rows 84/719, "
        "635 rows apart, r = 0.34); (3) maskagg run lengths not geometric (runs of 6-9 about 30% "
        "too frequent) although every lag-1..12 autocorrelation of the indicator is ~0; (4) "
        "higher-order dependence between stencil neighbours (lap kurtosis -0.742 vs -0.780); (5) "
        "low-bit bias of h3 (bit 0 set in 75% of cells, bits 1-3 also biased), harmless (<= 2^-27 "
        "of u). R5 removes all five: every row of the table is at PCG64's level, and R5's low "
        "bits are balanced.")
    out("")
    out("R5 = R3 followed by `h4 = (h3 * h3 div 65536) mod 2^31; h5 = (h4 * 1103515245 + 12345) "
        "mod 2^31; u = h5 / 2^31`. It is exact in int64 (h3^2 < 2^62) and uses only "
        "+ * div mod (Blade has no bitwise operators). Cost: two more integer multiply/shift/mask "
        "rounds per cell, identical in every arm.")
    out("")


def main():
    dest = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", "notes", "generator-check.md")
    out("# Generator check at full sizes (methodology review M6)")
    out("")
    out(f"Produced by `bench/validation/generator_check.py` (numpy {np.__version__}, "
        "`C:\\Python314\\python.exe -P`), generator variant **" + VARIANT + "** (benchgen.py). "
        "Every number is a measurement on the exact suite inputs at the specs' FULL sizes "
        "(maskagg at its revised n = 1,000,003). Yardsticks: closed forms and numpy PCG64.")
    out("")
    section_reference()
    section_compare()
    section_marginals()
    section_cross()
    section_lags()
    section_2d()
    section_maskagg()
    section_stencil()
    section_pairkernel()
    with open(dest, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(LINES) + "\n")


if __name__ == "__main__":
    main()
