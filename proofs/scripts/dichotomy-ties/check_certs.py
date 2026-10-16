r"""check_certs.py -- Python twin of the computed pins in BladeDichotomyTies.v.

Checks, independently of the Coq definitions:
  * pick3: for every (i0 of type 001, i0' of type 011, shared sorter s0) the
    small orbits of cells (001,001) and (011,001) meet the row at DIFFERENT
    lon tuples (the 3 x 3 certificate is [1 0 1; 0 1 1; 0 0 1]);
  * pick4: the four lon permutations with (value under the 1 of i0, value
    under the 0 of i0') = (0,1),(1,0),(2,1),(1,2) evaluate the tensors
    (A0, B0, A2, B2) to the identity matrix;
  * the coset representatives read by the (3,2,3) scheme;
  * the (3,2,3) scheme's target index has x's orbit name for every index and
    every pair of sorters (so it is sound for every lon tie-break).
Run: python -I check_certs.py
"""
import sys, os
from itertools import product
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ties import pact, pmul, pinv

perms3 = [(0,1,2),(0,2,1),(1,0,2),(1,2,0),(2,0,1),(2,1,0)]
def orbit2(x): return tuple(sorted(2*i+j for i, j in zip(x[:3], x[3:])))
def pc3(x): return tuple(sorted(3*i+j for i, j in zip(x[:3], x[3:])))
t001 = [(0,0,1),(0,1,0),(1,0,0)]; t011 = [(0,1,1),(1,0,1),(1,1,0)]
names = {'A0': (1,2,3), 'A2': (0,1,5), 'B0': (0,4,5), 'B2': (2,3,4)}
ok = 0
for i0 in t001:
    for i0p in t011:
        for s0 in perms3:
            if pact(s0, i0) == (0,0,1) and pact(s0, i0p) == (0,1,1):
                chi1 = [1 if orbit2(i0+j) == (0,0,3) else 0 for j in t001]
                chi2 = [1 if orbit2(i0p+j) == (1,2,2) else 0 for j in t001]
                assert sum(chi1) == 1 and sum(chi2) == 1 and chi1.index(1) != chi2.index(1)
                p1 = i0.index(1); p0 = i0p.index(0); assert p0 != p1
                for (v, w) in [(0,1),(1,0),(2,1),(1,2)]:
                    j = [j for j in perms3 if j[p1] == v and j[p0] == w][0]
                    row = (pc3(i0+j) == names['A0'], pc3(i0p+j) == names['B0'],
                           pc3(i0+j) == names['A2'], pc3(i0p+j) == names['B2'])
                    exp = {(0,1):(1,0,0,0),(1,0):(0,1,0,0),(2,1):(0,0,1,0),(1,2):(0,0,0,1)}[(v,w)]
                    assert tuple(int(b) for b in row) == exp, (i0, i0p, v, w, row)
                ok += 1
print("certificate configurations (i0, i0', shared sorter) verified:", ok)
H001 = [(0,1,2),(1,0,2)]; H011 = [(0,1,2),(0,2,1)]
for H, exp in [(H001, [(0,1,2),(0,2,1),(1,2,0)]), (H011, [(0,1,2),(1,0,2),(2,0,1)])]:
    reps = sorted(set([g for g in perms3 if any(pmul(mu, h) == g for h in H)][0] for mu in perms3))
    print("coset reps", reps, "as expected:", reps == exp)
bad = n = 0
for x in product(range(2), range(2), range(2), range(3), range(3), range(3)):
    i, j = x[:3], x[3:]; a, b = tuple(sorted(i)), tuple(sorted(j))
    for s in perms3:
        if pact(s, i) != a: continue
        for t in perms3:
            if pact(t, j) != b: continue
            H = H001 if a == (0,0,1) else H011 if a == (0,1,1) else H001
            mu = pmul(pinv(t), s)
            g = [g for g in perms3 if any(pmul(mu, h) == g for h in H)][0]
            y = a + pact(g, b); n += 1
            bad += pc3(y) != pc3(x)
print("(3,2,3) scheme orbit-name check: (x, sigma, tau) triples", n, "mismatches", bad)
