"""probe_sample.py -- compare LB, the flag bound FB and the GF(3) width on a
random sample of rules; prints the certificate chain when FB > LB.
Usage: python -I probe_sample.py r L M N [p]"""
import sys, os, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import *
from minwidth import random_rule

r, L, M, N = map(int, sys.argv[1:5])
p = int(sys.argv[5]) if len(sys.argv) > 5 else 3
rng = random.Random(7)
for k in range(N):
    rule = random_rule(r, L, M, rng)
    P = Problem(rule)
    lb = P.lower_bound()[0]
    fb, chain = P.flag_bound()
    E = P.search_p(fb, p, limit=3_000_000)
    tag = ('GF(%d) width %d' % (p, fb)) if E is not None else ('no GF(%d) width %d%s' % (p, fb, '' if P.last_search_exhaustive else ' (limit)'))
    lifted = P.lift_to_Q(E, p) is not None if E is not None else False
    print(k, 'LB', lb, 'FB', fb, tag, 'Q-lift' if lifted else '')
    if fb > lb:
        for (ck, x, coeffs) in chain:
            a, b, _, _ = P.cells[ck]
            print('      cell', ''.join(map(str, a)), ''.join(map(str, b)), 'point', P.points[x], 'T coeffs', [str(c) for c in coeffs])
