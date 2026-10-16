r"""map_322.py -- the minimal-width map of ALL 4096 rules at (3,2,2).

For every rule (constant tuples' sorters fixed to the identity): the
restriction bound LB, the flag bound FB (ties.Problem.flag_bound, a
triangular-determinant certificate valid over any field), an exhaustive
GF(3) search at m = FB (bounded by a node limit) and an exact integer
witness over Q lifted from it.  'exact' = FB is attained by an exact Q
witness, so the minimal width over Q is exactly FB.  Also classifies the
rules by the overlap pattern (|R_001 n R_011| in lat, in lon).

Incremental: one line per rule is appended to map_322.tsv as it goes
(rule index, overlap sizes, LB, FB, status, seconds), and the summary is
recomputed from that file, so a killed run loses nothing.  Rerunning skips
the rules already in the file.

Run:  C:\Python314\python.exe -I map_322.py [node_limit]      (default 2e5)
      C:\Python314\python.exe -I map_322.py summary           (just the table)
"""
import sys, os, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *
from collections import Counter

r, L, M = 3, 2, 2
HERE = os.path.dirname(os.path.abspath(__file__))
TSV = os.path.join(HERE, 'map_322.tsv')

def overlap_sizes(rule):
    out = []
    for side in ('lat', 'lon'):
        R1, R2 = set(rule.R(side, (0, 0, 1))), set(rule.R(side, (0, 1, 1)))
        out.append(len(R1 & R2))
    return tuple(out)

def summary():
    rows = [l.rstrip('\n').split('\t') for l in open(TSV) if l.strip()]
    hist = Counter(); byover = Counter(); unresolved = 0
    for idx, ov, lb, fb, st, secs in rows:
        hist[(int(lb), int(fb), st)] += 1
        byover[(ov, int(fb))] += 1
        unresolved += st != 'exact'
    print(f"(3,2,2): {len(rows)} of 4096 rules")
    for k, v in sorted(hist.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2])):
        print(f"  LB {k[0]}  FB {k[1]}  [{k[2]}]: {v}")
    print("by overlap sizes (|lat overlap|,|lon overlap|) -> FB: count")
    for k, v in sorted(byover.items()):
        print(f"  {k[0]} -> width {k[1]}: {v}")
    print(f"rules without an exact Q witness at m = FB: {unresolved}")

def main():
    if len(sys.argv) > 1 and sys.argv[1] == 'summary':
        summary(); return
    limit = int(float(sys.argv[1])) if len(sys.argv) > 1 else 200_000
    done = set()
    if os.path.exists(TSV):
        done = {int(l.split('\t')[0]) for l in open(TSV) if l.strip()}
    n = 0
    t0 = time.time()
    with open(TSV, 'a') as out:
        for lat in rule_choices(L, r, 'id'):
            for lon in rule_choices(M, r, 'id'):
                n += 1
                if n in done:
                    continue
                t1 = time.time()
                rule = Rule(r, L, M, lat, lon)
                P = Problem(rule)
                lb = P.lower_bound()[0]
                fb, _ = P.flag_bound()
                E = P.search_p(fb, 3, limit=limit)
                if E is None:
                    status = 'no GF(3) scheme at m=FB' if P.last_search_exhaustive else 'GF(3) search hit limit'
                elif P.lift_to_Q(E, 3) is not None:
                    status = 'exact'
                else:
                    status = 'GF(3) witness, no Z lift'
                ov = overlap_sizes(rule)
                out.write(f"{n}\t{ov[0]},{ov[1]}\t{lb}\t{fb}\t{status}\t{time.time()-t1:.1f}\n")
                out.flush()
                if n % 256 == 0:
                    print(f"  ... {n} rules, {time.time()-t0:.0f}s", flush=True)
    summary()

if __name__ == '__main__':
    main()
