r"""resolve2_322.py -- third pass: for the rules still without an exact Q
witness at m = FB after resolve_322.py, enumerate up to N GF(3) width-FB
schemes (ties.Problem.search_p_iter) and try to lift each one to an integer
witness checked exactly over Q.  Writes map_322_final.tsv and prints the
final histogram.

Run:  C:\Python314\python.exe -I resolve2_322.py [N=300] [node_limit=1e6]
"""
import sys, os, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *
from collections import Counter

r, L, M = 3, 2, 2
HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, 'map_322_resolved.tsv')
OUT = os.path.join(HERE, 'map_322_final.tsv')

def main():
    N = int(sys.argv[1]) if len(sys.argv) > 1 else 300
    limit = int(float(sys.argv[2])) if len(sys.argv) > 2 else 1_000_000
    rows = {}
    for l in open(SRC):
        if l.strip():
            f = l.rstrip('\n').split('\t'); rows[int(f[0])] = f
    todo = {k for k, f in rows.items() if f[4] != 'exact'}
    print(f"{len(todo)} rules to resolve of {len(rows)}")
    n = 0
    with open(OUT, 'w') as out:
        for lat in rule_choices(L, r, 'id'):
            for lon in rule_choices(M, r, 'id'):
                n += 1
                f = rows[n]
                if n in todo:
                    t1 = time.time()
                    P = Problem(Rule(r, L, M, lat, lon))
                    fb = int(f[3])
                    tried = 0
                    lifted = False
                    for E in P.search_p_iter(fb, 3, limit=limit):
                        tried += 1
                        if P.lift_to_Q(E, 3) is not None:
                            lifted = True
                            break
                        if tried >= N:
                            break
                    if lifted:
                        f = [f[0], f[1], f[2], f[3], 'exact', f"{time.time()-t1:.1f}"]
                    else:
                        f = [f[0], f[1], f[2], f[3], f[4] + f'; {tried} GF(3) witnesses, none lifts', f"{time.time()-t1:.1f}"]
                    print("  rule", n, f[1], "LB", f[2], "FB", f[3], "->", f[4], flush=True)
                out.write('\t'.join(f) + '\n')
    hist = Counter()
    for l in open(OUT):
        f = l.rstrip('\n').split('\t')
        hist[(f[1], int(f[2]), int(f[3]), 'exact' if f[4] == 'exact' else 'open')] += 1
    print("final histogram over 4096 rules (overlap, LB, FB, exact?):")
    for k, v in sorted(hist.items()):
        print(f"  overlap {k[0]}  LB {k[1]}  FB {k[2]}  {k[3]}: {v}")

if __name__ == '__main__':
    main()
