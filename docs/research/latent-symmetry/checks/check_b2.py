import numpy as np
from math import comb
from scipy.linalg import expm
rng = np.random.default_rng(0)
n, d, K, c = 60, 3, 4, 0.7
X = rng.standard_normal((n, d)) * np.array([1.0, 0.5, 2.0]) + 0.3
A = rng.standard_normal((d, d))

def mom(X, j):
    T = np.ones(())
    out = 0
    # M_j = mean of x^{⊗j}
    M = np.zeros((d,)*j) if j > 0 else np.array(1.0)
    for x in X:
        t = np.array(1.0)
        for _ in range(j): t = np.multiply.outer(t, x)
        M = M + t
    return M / len(X)

def rho(A, M):
    j = M.ndim
    out = np.zeros_like(M)
    for s in range(j):
        out = out + np.moveaxis(np.tensordot(A, M, axes=([1], [s])), 0, s)
    return out

def mmd_V_poly(X, Y):
    k = lambda U, V: (c + U @ V.T) ** K
    return k(X, X).mean() + k(Y, Y).mean() - 2 * k(X, Y).mean()

w = [comb(K, j) * c ** (K - j) for j in range(K + 1)]
g = expm(0.3 * A)
Y = X @ g.T
lhs = mmd_V_poly(X, Y)
rhs = sum(w[j] * np.sum((mom(X, j) - mom(Y, j)) ** 2) for j in range(K + 1))
print("V-stat == weighted moment Frobenius distance:", lhs, rhs, abs(lhs - rhs) / rhs)

# second derivative at t=0
q = sum(w[j] * np.sum(rho(A, mom(X, j)) ** 2) for j in range(1, K + 1))
F = lambda t: mmd_V_poly(X, X @ expm(t * A).T)
h = 1e-3
fd2 = (F(h) - 2 * F(0) + F(-h)) / h ** 2
fd1 = (F(h) - F(-h)) / (2 * h)
print("F'(0) ~", fd1, " F''(0) ~", fd2, " 2q =", 2 * q, " q =", q)

# Gram formula for q with kappa(s)=(c+s)^K
G = X @ X.T; XA = X @ A.T
GAA = XA @ XA.T; P = XA @ X.T  # P_ab = <A x_a, x_b>
kp = K * (c + G) ** (K - 1); kpp = K * (K - 1) * (c + G) ** (K - 2)
qg = (kp * GAA + kpp * P * P.T).mean()
print("Gram formula q:", qg)

# Gaussian kernel: Hessian of V-stat MMD^2(X, gX) at t=0
sig = 1.3
def kg(U, V):
    D = (U ** 2).sum(1)[:, None] + (V ** 2).sum(1)[None, :] - 2 * U @ V.T
    return np.exp(-D / (2 * sig ** 2))
def mmd_V_g(X, Y): return kg(X, X).mean() + kg(Y, Y).mean() - 2 * kg(X, Y).mean()
Fg = lambda t: mmd_V_g(X, X @ expm(t * A).T)
fd2g = (Fg(h) - 2 * Fg(0) + Fg(-h)) / h ** 2
Kxx = kg(X, X)
dP = np.diag(P)
term = Kxx * (GAA / sig ** 2 - (dP[:, None] - P) * (P.T - dP[None, :]) / sig ** 4)
print("Gaussian: F''(0) fd", fd2g, " 2*gram-formula", 2 * term.mean())
# diagonal (V-stat) bias of the Hessian: (1/n^2) sum_a |A x_a|^2/sig^2
print("Gaussian diag contribution", np.trace(GAA) / n ** 2 / sig ** 2, "= term diag", np.trace(term) / n ** 2)
