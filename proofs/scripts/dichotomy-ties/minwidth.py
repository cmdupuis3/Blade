r"""minwidth.py -- minimal width of a tie-breaking rule, small cases.

For a rule: LB = the restriction lower bound (ties.Problem.lower_bound: for
every intersection Y of blocks, dim of the sum of the restricted V_c over the
cells whose block contains Y; a theorem over any field).  Then an upper bound
by exhaustive GF(p) search for a width-m scheme at m = LB, LB+1, ..., and an
attempt to lift the GF(p) witness to an INTEGER witness checked exactly over
Q.  "exact" in the output means LB == width of an exact Q witness; "GF(p)"
means only a finite-field witness was found (not a Q proof); "open" means
neither within the search limit.

Usage:
  python -I minwidth.py 3 2 2 lex          # the lexicographic rule
  python -I minwidth.py 3 2 2 all          # every rule (constants fixed to id)
  python -I minwidth.py 3 2 3 sample 50    # 50 random rules
  python -I minwidth.py 4 2 2 lex
Options after the rule spec:  p=<prime> (default 3)  limit=<nodes> (default 2e6)
"""
import sys, os, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *
from collections import Counter

def random_rule(r, L, M, rng, constants='all'):
    lat = {i: rng.choice(sorters(i)) if (constants == 'all' or len(set(i)) > 1) else tuple(range(r))
           for i in tuples(L, r)}
    lon = {j: rng.choice(sorters(j)) if (constants == 'all' or len(set(j)) > 1) else tuple(range(r))
           for j in tuples(M, r)}
    return Rule(r, L, M, lat, lon)

def analyse(rule, p=3, limit=2_000_000, mmax=None, verbose=True):
    P = Problem(rule)
    lb, Y, used = P.lower_bound()
    dmax = P.d_max()
    fact = 1
    for k in range(2, rule.r + 1): fact *= k
    if mmax is None: mmax = fact
    status, width, witness = 'open', None, None
    for m in range(lb, mmax + 1):
        E = P.search_p(m, p, limit=limit)
        if E is None and not P.last_search_exhaustive:
            status = f'open (GF({p}) search hit limit at m={m})'
            break
        if E is not None:
            width = m
            Z = P.lift_to_Q(E, p)
            if Z is not None:
                status, witness = 'exact', Z
            else:
                status = f'GF({p})'
            break
    if width is None and mmax >= fact and not status.startswith('open ('):
        # the r! scheme always works: eps_mu(s,t) = [mu = s t^-1]
        width, status = fact, 'exact (r! scheme)'
    if verbose:
        print(f"  points {P.n}, d_max {dmax}, LB {lb} (Y = {Y}, {len(used)} cells), "
              f"width {width} [{status}]")
    return lb, width, status, witness

def main():
    r, L, M = int(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3])
    spec = sys.argv[4]
    p, limit = 3, 2_000_000
    for a in sys.argv[5:]:
        if a.startswith('p='): p = int(a[2:])
        if a.startswith('limit='): limit = int(float(a[6:]))
    if spec == 'lex':
        print(f"(r,L,M) = ({r},{L},{M}), lexicographic rule:")
        analyse(lex_rule(r, L, M), p, limit)
    elif spec == 'all':
        hist = Counter()
        n = 0
        for lat in rule_choices(L, r, 'id'):
            for lon in rule_choices(M, r, 'id'):
                n += 1
                lb, w, st, _ = analyse(Rule(r, L, M, lat, lon), p, limit, verbose=False)
                hist[(lb, w, st)] += 1
        print(f"(r,L,M) = ({r},{L},{M}), all {n} rules (constants fixed):")
        for k, v in sorted(hist.items(), key=lambda kv: (kv[0][0], str(kv[0][1]))):
            print(f"  LB {k[0]}  width {k[1]}  [{k[2]}]: {v} rules")
    elif spec == 'sample':
        N = int(sys.argv[5]); rng = random.Random(7)
        hist = Counter()
        for _ in range(N):
            lb, w, st, _ = analyse(random_rule(r, L, M, rng), p, limit, verbose=False)
            hist[(lb, w, st)] += 1
        print(f"(r,L,M) = ({r},{L},{M}), {N} random rules (all sorters random):")
        for k, v in sorted(hist.items(), key=lambda kv: (kv[0][0], str(kv[0][1]))):
            print(f"  LB {k[0]}  width {k[1]}  [{k[2]}]: {v} rules")

if __name__ == '__main__':
    main()
