"""census_322.py -- which tie-breaking rules admit width 2 at (r, L, M) = (3, 2, 2)?

Exact decision.  At (3,2,2) every cell (a, b) with a, b both non-constant has
d_c = 2 = m, so a width-2 W must lie in U_c = res_c^{-1}(V_c) for those four
cells; K = the intersection.  Width 2 is achievable iff dim res_c(K) = 2 for
each of the four (then W = span(1, w) with w generic in K works: 1 in K
always, and the constant-tuple cells only need 1 in res_c(W)).

Prints: the count over the 4096 rules (constant tuples' sorters fixed to the
identity), the admitting rules, the match with the combinatorial criterion
"R_001 and R_011 disjoint in both dimensions", the restriction lower bound
LB for every rule, and the check that the explicit scheme (eps1 = 1,
eps2 = small-orbit indicator) is sound for the admitting rules under ALL
1296 constant-tuple sorter choices.

Run:  C:\\Python314\\python.exe -I census_322.py
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *
from collections import Counter

r, L, M = 3, 2, 2

def forced_K_dims(P):
    """dims of res_c(K) for the forced cells (d_c == d_max)"""
    n, m = P.n, P.d_max()
    cons = []
    forced = []
    for k, (a, b, blk, inds) in enumerate(P.cells):
        if len(inds) != m:
            continue
        forced.append(k)
        cols = [P.pidx[pt] for pt in blk]
        Vloc = [[1 if pt in o else 0 for pt in blk] for o in inds]
        for z in nullspace_Q(Vloc, len(blk)):      # V_c^perp inside F^{I_c}
            row = [Fraction(0)] * n
            for c, v in zip(cols, z):
                row[c] = v
            cons.append(row)
    K = nullspace_Q(cons, n) if cons else [[Fraction(int(i == j)) for j in range(n)] for i in range(n)]
    dims = {}
    for k in forced:
        cols = [P.pidx[pt] for pt in P.cells[k][2]]
        dims[(P.cells[k][0], P.cells[k][1])] = rank_Q([[w[c] for c in cols] for w in K])
    return dims, len(K)

def disjoint(rule, side):
    nonconst = [a for a in sorted_tuples(rule.L if side == 'lat' else rule.M, rule.r) if len(set(a)) > 1]
    sets = [set(rule.R(side, a)) for a in nonconst]
    for i in range(len(sets)):
        for j in range(i + 1, len(sets)):
            if sets[i] & sets[j]:
                return False
    return True

def explicit_scheme(P):
    """eps1 = 1; eps2 = indicator of 'small orbit of the unique full-dim
    block containing the point' (0 elsewhere).  Only meaningful when the
    full-dim blocks are pairwise disjoint."""
    m = P.d_max()
    chi = [0] * P.n
    for a, b, blk, inds in P.cells:
        if len(inds) == m:
            small = min(inds, key=len)
            for pt in small:
                chi[P.pidx[pt]] = 1
    return [[1] * P.n, chi]

def main():
    lat_rules = list(rule_choices(L, r, 'id'))
    lon_rules = list(rule_choices(M, r, 'id'))
    print(f"one-dimension rules (constants fixed): {len(lat_rules)}; total {len(lat_rules)*len(lon_rules)}")
    good = []
    lb_hist = Counter()
    mismatches = 0
    for lat in lat_rules:
        for lon in lon_rules:
            rule = Rule(r, L, M, lat, lon)
            P = Problem(rule)
            dims, dimK = forced_K_dims(P)
            ok = all(d == 2 for d in dims.values())
            crit = disjoint(rule, 'lat') and disjoint(rule, 'lon')
            if ok != crit:
                mismatches += 1
            lb = P.lower_bound()[0]
            lb_hist[(ok, lb)] += 1
            if ok:
                good.append((lat, lon, dimK))
    print(f"width-2 admitting rules: {len(good)} of {len(lat_rules)*len(lon_rules)}")
    print(f"criterion 'sorter sets of 001 and 011 disjoint, both dims' mismatches: {mismatches}")
    print("histogram (width2?, LB):", dict(lb_hist))
    for lat, lon, dimK in good:
        print("--- rule (dim K = %d)" % dimK)
        for side, rho in (('lat', lat), ('lon', lon)):
            parts = []
            for i, s in sorted(rho.items()):
                if len(set(i)) > 1:
                    parts.append(f"{''.join(map(str,i))}->{''.join(map(str,s))}({'+' if sgn(s)>0 else '-'})")
            print(f"  {side}: " + "  ".join(parts))
    # explicit scheme under every constant-tuple choice
    print("checking explicit scheme (1, chi) under all constant-tuple sorters ...")
    lat_all = list(rule_choices(L, r, 'all'))
    lon_all = list(rule_choices(M, r, 'all'))
    goodkeys = set()
    for lat, lon, _ in good:
        goodkeys.add((tuple(sorted((i, s) for i, s in lat.items() if len(set(i)) > 1)),
                      tuple(sorted((j, t) for j, t in lon.items() if len(set(j)) > 1))))
    checked = failed = 0
    for lat in lat_all:
        kl = tuple(sorted((i, s) for i, s in lat.items() if len(set(i)) > 1))
        for lon in lon_all:
            km = tuple(sorted((j, t) for j, t in lon.items() if len(set(j)) > 1))
            if (kl, km) not in goodkeys:
                continue
            P = Problem(Rule(r, L, M, lat, lon))
            checked += 1
            if not P.width_ok_Q(explicit_scheme(P)):
                failed += 1
    print(f"  checked {checked} full rules (4 x 36 x 36), scheme failures: {failed}")
    # the lower bound never looks at constant tuples either; spot-check that a
    # bad rule stays at LB >= 3 under random constant choices
    import random
    random.seed(1)
    bad = [(lat, lon) for lat in lat_rules for lon in lon_rules
           if not (disjoint(Rule(r, L, M, lat, lon), 'lat') and disjoint(Rule(r, L, M, lat, lon), 'lon'))]
    mn = 99
    for _ in range(40):
        lat, lon = random.choice(bad)
        lat2 = dict(lat); lon2 = dict(lon)
        for i in lat2:
            if len(set(i)) == 1: lat2[i] = random.choice(sorters(i))
        for j in lon2:
            if len(set(j)) == 1: lon2[j] = random.choice(sorters(j))
        P = Problem(Rule(r, L, M, lat2, lon2))
        dims, _ = forced_K_dims(P)
        assert not all(d == 2 for d in dims.values())
        mn = min(mn, P.lower_bound()[0])
    print(f"  40 random bad rules with random constant sorters: still no width 2; min LB = {mn}")

if __name__ == '__main__':
    main()
