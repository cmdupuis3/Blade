r"""count_disjoint.py -- one-dimension tie-break choices whose sorter sets
R_a (over the non-constant canonical tuples a) are pairwise disjoint.

Pairwise-disjoint sorter sets make the full-dimension blocks R_a x R_b
pairwise disjoint, which is what lets the counting bound max_c d_c be
attained (eps_1 = 1, the other weights per-block orbit indicators).  At
(r, L) = (3, 2) exactly the 2 parity-graded choices are disjoint.  This
script counts them for other (r, L) by backtracking (count only, with a cap)
and reports how many of the first few found are sign-graded.

Usage: python -I count_disjoint.py r L [cap]
"""
import sys, os, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *

def count_disjoint(r, L, cap=None):
    ts = [i for i in tuples(L, r) if len(set(i)) > 1]
    types = sorted(set(tuple(sorted(i)) for i in ts))
    tidx = {i: types.index(tuple(sorted(i))) for i in ts}
    # order: tuples with the fewest sorters first
    ts.sort(key=lambda i: len(sorters(i)))
    opts = [sorters(i) for i in ts]
    used = [set() for _ in types]
    found = [0]
    graded = [0]
    sample = []
    def rec(k):
        if cap is not None and found[0] >= cap:
            return
        if k == len(ts):
            found[0] += 1
            if all(len(set(sgn(s) for s in u)) == 1 for u in used):
                graded[0] += 1
            if len(sample) < 3:
                sample.append([sorted(u) for u in used])
            return
        t = tidx[ts[k]]
        for s in opts[k]:
            if any(s in used[u] for u in range(len(types)) if u != t):
                continue
            used[t].add(s)
            rec(k + 1)
            used[t].discard(s)
    rec(0)
    return types, found[0], graded[0], sample

if __name__ == '__main__':
    r, L = int(sys.argv[1]), int(sys.argv[2])
    cap = int(float(sys.argv[3])) if len(sys.argv) > 3 else None
    t0 = time.time()
    types, n, g, sample = count_disjoint(r, L, cap)
    print(f"(r,L) = ({r},{L}): non-constant types {[''.join(map(str,t)) for t in types]}, "
          f"sorter-set sizes {[len(perms(r)) // len(stab(t)) for t in types]} of {len(perms(r))}")
    print(f"pairwise-disjoint sorter-set choices: {n}{' (capped)' if cap and n >= cap else ''}; "
          f"sign-graded among them: {g}   [{time.time()-t0:.1f}s]")
    for s in sample:
        print("  e.g.", [[''.join(map(str, p)) for p in R] for R in s])
