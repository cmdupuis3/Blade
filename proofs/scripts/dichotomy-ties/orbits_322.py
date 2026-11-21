r"""orbits_322.py -- the 4096 rules at (3,2,2) up to the symmetries of the
problem, and the second pass on orbit representatives only.

Symmetries (each preserves the minimal width, since weights, cells and
orbit structures transport along):
  * a relabeling g of the three slot positions, applied to every tuple and
    every sorter at once (|S_3| = 6);
  * the value flip 0 <-> 1 in the lat dimension, and in the lon dimension
    (sorters compose with the reversal [2;1;0]);
  * the lat/lon swap.
Group of order 48.  Rules are canonicalised to the lexicographically least
image; one representative per orbit is then re-examined with large limits:
exhaustive GF(3) and GF(5) searches at m = FB, up to N GF(3) witnesses tried
for an integer lift, and an exact upper witness at m = FB + 1.

Reads map_322.tsv (first pass), writes map_322_orbits.tsv:
  orbit_rep_index  size  overlap  LB  FB  status  seconds
Run:  C:\Python314\python.exe -I orbits_322.py [node_limit=1e6] [N=200]
"""
import sys, os, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *
from collections import Counter

r, L, M = 3, 2, 2
HERE = os.path.dirname(os.path.abspath(__file__))
TSV = os.path.join(HERE, 'map_322.tsv')
OUT = os.path.join(HERE, 'map_322_orbits.tsv')
REV = (2, 1, 0)

def relabel(rho, g):
    """positions permuted by g: tuple i -> pact g i, sorter s -> pmul (pinv g) s
    (so that the new sorter sorts the new tuple)"""
    out = {}
    for i, s in rho.items():
        i2 = pact(g, i)
        s2 = pmul(pinv(g), s)
        assert pact(s2, i2) == tuple(sorted(i2)), (i, s, g)
        out[i2] = s2
    return out

def flip(rho):
    out = {}
    for i, s in rho.items():
        i2 = tuple(1 - v for v in i)
        s2 = pmul(s, REV)
        assert pact(s2, i2) == tuple(sorted(i2)), (i, s)
        out[i2] = s2
    return out

def key(lat, lon):
    return (tuple(sorted(lat.items())), tuple(sorted(lon.items())))

def canon(lat, lon):
    best = None
    for g in perms(3):
        for fl in (False, True):
            for fm in (False, True):
                a = relabel(lat, g); b = relabel(lon, g)
                if fl: a = flip(a)
                if fm: b = flip(b)
                for sw in (False, True):
                    k = key(b, a) if sw else key(a, b)
                    if best is None or k < best:
                        best = k
    return best

def main():
    limit = int(float(sys.argv[1])) if len(sys.argv) > 1 else 1_000_000
    N = int(sys.argv[2]) if len(sys.argv) > 2 else 200
    rows = {}
    for l in open(TSV):
        if l.strip():
            f = l.rstrip('\n').split('\t'); rows[int(f[0])] = f
    # the first pass fixed constant sorters to the identity; the symmetries
    # move them, so canonicalise on the non-constant part only (the width
    # of a rule with identity constants is what the first pass measured;
    # the orbit representative is re-measured with identity constants too)
    orbits = {}
    n = 0
    rules = {}
    for lat in rule_choices(L, r, 'id'):
        for lon in rule_choices(M, r, 'id'):
            n += 1
            nl = {i: s for i, s in lat.items() if len(set(i)) > 1}
            nm = {j: t for j, t in lon.items() if len(set(j)) > 1}
            k = canon(nl, nm)
            orbits.setdefault(k, []).append(n)
            rules[n] = (lat, lon)
    print(f"{len(orbits)} orbits of the 4096 rules under the 48 symmetries")
    # consistency: first-pass (LB, FB) constant on each orbit?
    incons = 0
    for k, members in orbits.items():
        vals = {(rows[m][2], rows[m][3]) for m in members}
        incons += len(vals) > 1
    print(f"orbits whose first-pass (LB, FB) is not constant: {incons}")
    hist = Counter()
    with open(OUT, 'w') as out:
        for k, members in sorted(orbits.items(), key=lambda kv: kv[1][0]):
            rep = members[0]
            f = rows[rep]
            status = f[4]
            t1 = time.time()
            if status != 'exact':
                lat, lon = rules[rep]
                P = Problem(Rule(r, L, M, lat, lon))
                fb = int(f[3])
                notes = []
                tried = 0
                for E in P.search_p_iter(fb, 3, limit=limit):
                    tried += 1
                    if P.lift_to_Q(E, 3) is not None:
                        status = 'exact'; break
                    if tried >= N:
                        break
                if status != 'exact':
                    notes.append(f'{tried} GF(3) witnesses at m=FB' + (' (exhaustive)' if (tried < N and P.last_search_exhaustive if hasattr(P, "last_search_exhaustive") else False) else ''))
                    E5 = P.search_p(fb, 5, limit=limit)
                    if E5 is not None:
                        status = 'exact' if P.lift_to_Q(E5, 5) is not None else None
                        notes.append('GF(5) witness' + (', lifts' if status == 'exact' else ', no Z lift'))
                    else:
                        notes.append('no GF(5) scheme at m=FB' if P.last_search_exhaustive else 'GF(5) search hit limit')
                if status != 'exact':
                    E = P.search_p(fb + 1, 3, limit=limit)
                    up = f'exact width <= {fb + 1}' if (E is not None and P.lift_to_Q(E, 3) is not None) else 'no upper witness'
                    status = '; '.join(notes) + '; ' + up
            out.write(f"{rep}\t{len(members)}\t{f[1]}\t{f[2]}\t{f[3]}\t{status}\t{time.time()-t1:.1f}\n")
            out.flush()
            hist[(f[1], int(f[2]), int(f[3]), 'exact' if status == 'exact' else status)] += len(members)
            if status != 'exact':
                print("  orbit of rule", rep, "size", len(members), f[1], "LB", f[2], "FB", f[3], "->", status, flush=True)
    print("histogram over 4096 rules (overlap, LB, FB, status) -> count:")
    for k, v in sorted(hist.items()):
        print(f"  {k[0]}  LB {k[1]}  FB {k[2]}  {k[3]}: {v}")

if __name__ == '__main__':
    main()
