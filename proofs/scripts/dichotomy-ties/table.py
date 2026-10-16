r"""table.py -- the minimal-width table for small (r, L, M), plus the
constant-tuple dependence probe.  Sequential, prints as it goes.

For each case: the lexicographic rule (lex-first sorter per tuple), then a
random sample of rules (all sorters random, constants included).  Each line:
LB (restriction bound), FB (flag bound), GF(p) width at m = FB if the
exhaustive search finishes within the node limit, and whether an exact Q
witness was lifted.  Cases with a free cell (L, M >= 3) are forced to r! by
BladeDichotomy and only reported.

Usage: python -I table.py [sample_size]
"""
import sys, os, random, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *
from minwidth import random_rule
from collections import Counter

N = int(sys.argv[1]) if len(sys.argv) > 1 else 20

def report(rule, p=3, limit=400_000, search=True):
    P = Problem(rule)
    lb = P.lower_bound()[0]
    fb, chain = P.flag_bound()
    st = ''
    if search:
        E = P.search_p(fb, p, limit=limit)
        if E is None:
            st = 'no GF(%d) scheme at m=FB (exhaustive)' % p if P.last_search_exhaustive else 'GF(%d) search hit limit' % p
        else:
            st = 'exact Q witness at m=FB' if P.lift_to_Q(E, p) is not None else 'GF(%d) witness at m=FB (no Z lift)' % p
    return P.n, P.d_max(), lb, fb, st, chain

def run_case(r, L, M, search=True, p=3, limit=400_000):
    print(f"=== (r,L,M) = ({r},{L},{M})")
    t0 = time.time()
    n, d, lb, fb, st, _ = report(lex_rule(r, L, M), p, limit, search)
    print(f"  lex rule: points {n}, d_max {d}, LB {lb}, FB {fb}  {st}   [{time.time()-t0:.1f}s]")
    rng = random.Random(11)
    hist = Counter()
    t0 = time.time()
    for _ in range(N):
        n, d, lb, fb, st, _ = report(random_rule(r, L, M, rng), p, limit, search)
        hist[(d, lb, fb, st)] += 1
    print(f"  {N} random rules [{time.time()-t0:.1f}s]:")
    for k, v in sorted(hist.items()):
        print(f"    d_max {k[0]}  LB {k[1]}  FB {k[2]}  {k[3]}: {v}")

def constants_probe():
    print("=== constant-tuple sorters matter above the counting bound? (3,2,2)")
    # take the lex rule's non-constant part and vary the constants
    base = lex_rule(3, 2, 2)
    hist = Counter()
    for c000 in perms(3):
        for c111 in perms(3):
            for d000 in perms(3):
                for d111 in perms(3):
                    lat = dict(base.lat); lon = dict(base.lon)
                    lat[(0,0,0)] = c000; lat[(1,1,1)] = c111
                    lon[(0,0,0)] = d000; lon[(1,1,1)] = d111
                    P = Problem(Rule(3, 2, 2, lat, lon))
                    hist[(P.lower_bound()[0], P.flag_bound()[0])] += 1
    print("  lex non-constant part, all 1296 constant choices -> (LB, FB) histogram:", dict(hist))

if __name__ == '__main__':
    run_case(2, 2, 2); run_case(2, 3, 3)
    run_case(3, 2, 2)
    constants_probe()
    run_case(3, 2, 3, limit=300_000)
    run_case(3, 3, 3, search=False)
    run_case(4, 2, 2, limit=300_000)
