r"""resolve_322.py -- second pass over the rules map_322.py left without an
exact Q witness at m = FB.

For each such rule: a larger-limit exhaustive GF(3) search, then GF(5) and
GF(7) searches, lifting every witness found to an integer witness checked
exactly over Q.  Writes map_322_resolved.tsv (same columns) and prints the
final (LB, FB, status) histogram over all 4096 rules.

Run:  C:\Python314\python.exe -I resolve_322.py [node_limit]   (default 3e6)
"""
import sys, os, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *
from collections import Counter

r, L, M = 3, 2, 2
HERE = os.path.dirname(os.path.abspath(__file__))
TSV = os.path.join(HERE, 'map_322.tsv')
OUT = os.path.join(HERE, 'map_322_resolved.tsv')

def main():
    limit = int(float(sys.argv[1])) if len(sys.argv) > 1 else 3_000_000
    rows = {}
    for l in open(TSV):
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
                    status = None
                    notes = []
                    for p in (3, 5):
                        E = P.search_p(fb, p, limit=limit)
                        if E is None:
                            notes.append(f'no GF({p}) scheme at m=FB' if P.last_search_exhaustive
                                         else f'GF({p}) search hit limit')
                            continue
                        if P.lift_to_Q(E, p) is not None:
                            status = 'exact'; break
                        notes.append(f'GF({p}) witness, no Z lift')
                    if status != 'exact':
                        # upper bound one above FB, exact over Q if it lifts
                        E = P.search_p(fb + 1, 3, limit=limit)
                        up = ('exact width <= %d' % (fb + 1)) if (E is not None and P.lift_to_Q(E, 3) is not None) else 'no upper witness'
                        status = '; '.join(notes) + '; ' + up
                    f = [f[0], f[1], f[2], f[3], status, f"{time.time()-t1:.1f}"]
                    print("  rule", n, f[1], "LB", f[2], "FB", f[3], "->", status, flush=True)
                out.write('\t'.join(f) + '\n')
    hist = Counter()
    for l in open(OUT):
        f = l.rstrip('\n').split('\t')
        hist[(int(f[2]), int(f[3]), f[4])] += 1
    print("final histogram over 4096 rules (LB, FB, status):")
    for k, v in sorted(hist.items()):
        print(f"  LB {k[0]}  FB {k[1]}  [{k[2]}]: {v}")

if __name__ == '__main__':
    main()
