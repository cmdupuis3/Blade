r"""case422.py -- (r, L, M) = (4, 2, 2): the lexicographic rule, a few random
rules, and a hand-built pairwise-disjoint rule.

Prints d_max (= floor(r/2)+1 = 3, the counting bound), the restriction bound
LB, and a bounded GF(3) search at m = LB with an integer lift (the flag-bound
BFS is skipped here: the blocks have up to 36 points).  For the disjoint rule
the width-3 scheme (1 + per-block orbit indicators) is checked exactly.

Run:  python -I case422.py [N=4] [limit=3e5]
"""
import sys, os, time, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *
from minwidth import random_rule
from count_disjoint import count_disjoint

def report(tag, rule, limit):
    P = Problem(rule)
    t0 = time.time()
    lb = P.lower_bound()[0]
    E = P.search_p(lb, 3, limit=limit)
    if E is None:
        st = 'no GF(3) scheme at m=LB (exhaustive)' if P.last_search_exhaustive else 'GF(3) search hit limit at m=LB'
    else:
        st = 'exact Q witness at m=LB' if P.lift_to_Q(E, 3) is not None else 'GF(3) witness at m=LB, no Z lift'
    print(f"  {tag}: points {P.n}, d_max {P.d_max()}, LB {lb}, {st}  [{time.time()-t0:.1f}s]", flush=True)

def disjoint_rule():
    """a rule whose non-constant sorter sets are pairwise disjoint (if any)"""
    r, L = 4, 2
    ts = [i for i in tuples(L, r) if len(set(i)) > 1]
    types = sorted(set(tuple(sorted(i)) for i in ts))
    tidx = {i: types.index(tuple(sorted(i))) for i in ts}
    ts.sort(key=lambda i: len(sorters(i)))
    used = [set() for _ in types]
    choice = {}
    def rec(k):
        if k == len(ts):
            return True
        t = tidx[ts[k]]
        for s in sorters(ts[k]):
            if any(s in used[u] for u in range(len(types)) if u != t):
                continue
            used[t].add(s); choice[ts[k]] = s
            if rec(k + 1):
                return True
            used[t].discard(s); del choice[ts[k]]
        return False
    if not rec(0):
        return None
    rho = dict(choice)
    for i in tuples(L, r):
        if len(set(i)) == 1:
            rho[i] = tuple(range(r))
    return rho

if __name__ == '__main__':
    N = int(sys.argv[1]) if len(sys.argv) > 1 else 4
    limit = int(float(sys.argv[2])) if len(sys.argv) > 2 else 300_000
    print("=== (4,2,2)")
    report('lex rule', lex_rule(4, 2, 2), limit)
    rng = random.Random(3)
    for k in range(N):
        report(f'random rule {k}', random_rule(4, 2, 2, rng), limit)
    t0 = time.time()
    types, n, g, _ = count_disjoint(4, 2, cap=2000)
    print(f"  pairwise-disjoint one-dimension choices at (4,2): {n}{' (capped at 2000)' if n >= 2000 else ''}, "
          f"sign-graded among those: {g}  [{time.time()-t0:.1f}s]")
    rho = disjoint_rule()
    if rho is not None:
        rule = Rule(4, 2, 2, rho, rho)
        P = Problem(rule)
        # explicit width-3 scheme: eps_1 = 1; eps_k (k = 2, 3) = on each block
        # (pairwise disjoint) the indicator of its (k-1)-th orbit, dropping
        # the largest; the block's orbit indicators are then in the span
        rows = [[1] * P.n, [0] * P.n, [0] * P.n]
        for a, b, blk, inds in P.cells:
            for k, o in enumerate(sorted(inds, key=len)[:-1]):
                for pt in o:
                    rows[1 + k][P.pidx[pt]] = 1
        ok = P.width_ok_Q(rows)
        print(f"  disjoint rule (both dims): LB {P.lower_bound()[0]}, d_max {P.d_max()}, "
              f"explicit width-{len(rows)} scheme sound: {ok}")
