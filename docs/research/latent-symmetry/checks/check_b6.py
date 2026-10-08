import numpy as np
from fractions import Fraction
from math import factorial
from scipy.linalg import expm, expm_frechet
u = 2.0**-53
def delta_coeffs(m, N):
    # coefficients of log(T_m(x)) - x up to degree N, T_m = sum_{k<=m} x^k/k!
    t = [Fraction(1, factorial(k)) if k <= m else Fraction(0) for k in range(N+1)]
    # log of series with t0=1: L' = T'/T  -> L_n = (n t_n - sum_{k=1}^{n-1} k L_k t_{n-k}) / n
    L = [Fraction(0)]*(N+1)
    for n in range(1, N+1):
        s = n*t[n] - sum(k*L[k]*t[n-k] for k in range(1, n))
        L[n] = s/n
    L[1] -= 1
    return [float(abs(x)) for x in L]
print(" m  theta_m(Taylor, |coef| bound)   cost m-1+? ")
res = {}
for m in [4,6,8,10,12,14,16,18,20,24,30]:
    L = delta_coeffs(m, m+150)
    f = lambda th: sum(abs(L[k])*th**(k-1) for k in range(m+1, len(L))) - u
    lo, hi = 1e-6, 10.0
    for _ in range(200):
        mid = (lo+hi)/2
        if f(mid) > 0: hi = mid
        else: lo = mid
    res[m] = float(lo)
    print(f"{m:2d}  {float(lo):.6g}   leading |delta_(m+1)| = {float(abs(L[m+1])):.3g}")

def expm_taylor(A, E=None):
    th = {18: res[18]}
    m = 18; nrm = np.abs(A).sum(0).max()
    s = max(0, int(np.ceil(np.log2(nrm/th[m])))) if nrm > 0 else 0
    X = A / 2**s; dX = None if E is None else E / 2**s
    # Horner: T = I + X(I + X/2(I + X/3(...)))
    n = A.shape[0]; I = np.eye(n)
    Y = I.copy(); dY = np.zeros_like(A)
    for k in range(m, 0, -1):
        if E is not None: dY = (dX @ Y + X @ dY) / k
        Y = I + X @ Y / k
    for _ in range(s):
        if E is not None: dY = Y @ dY + dY @ Y
        Y = Y @ Y
    return (Y, dY, s)
rng = np.random.default_rng(3)
for scale in [0.1, 1.0, 10.0, 40.0]:
    A = scale*rng.standard_normal((6, 6))/np.sqrt(6); Ed = rng.standard_normal((6, 6))
    Y, dY, s = expm_taylor(A, Ed)
    eA, Lr = expm_frechet(A, Ed)
    Mb = np.block([[A, Ed], [np.zeros_like(A), A]])
    Lb = expm(Mb)[:6, 6:]
    print(f"||A||1={np.abs(A).sum(0).max():7.2f} s={s:2d} relerr expm {np.linalg.norm(Y-eA)/np.linalg.norm(eA):.1e} "
          f"Frechet vs scipy {np.linalg.norm(dY-Lr)/np.linalg.norm(Lr):.1e} vs block {np.linalg.norm(dY-Lb)/np.linalg.norm(Lb):.1e}")
# reverse-mode identity: <G, L(A,E)> = <L(A^T,G), E>
A = rng.standard_normal((5,5)); Ed = rng.standard_normal((5,5)); Gb = rng.standard_normal((5,5))
print("adjoint identity:", np.sum(Gb*expm_frechet(A, Ed)[1]), np.sum(expm_frechet(A.T, Gb)[1]*Ed))
