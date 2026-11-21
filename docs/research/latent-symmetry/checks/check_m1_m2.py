# Replay of corpus cells lie/009 (M1) and lie/010 (M2): the value pins.
# numpy only; expm by a 40-term Taylor series (the arguments have norm < 2).
import numpy as np

np.set_printoptions(precision=17)


def expm(A):
    out = np.eye(A.shape[0])
    term = np.eye(A.shape[0])
    for k in range(1, 60):
        term = term @ A / k
        out = out + term
    return out


def poly_v(x, y, c, k):
    n, m = len(x), len(y)
    return ((c + x @ x.T) ** k).sum() / n**2 + ((c + y @ y.T) ** k).sum() / m**2 - 2 * ((c + x @ y.T) ** k).sum() / (n * m)


def poly_u_paired(x, y, c, k):
    n = len(x)
    kxx, kyy, kxy = (c + x @ x.T) ** k, (c + y @ y.T) ** k, (c + x @ y.T) ** k
    off = lambda K: K.sum() - np.trace(K)
    return (off(kxx) + off(kyy) - 2 * off(kxy)) / (n * (n - 1))


def gk(x, y, s2):
    d2 = (x * x).sum(1)[:, None] + (y * y).sum(1)[None, :] - 2 * x @ y.T
    return np.exp(-d2 / (2 * s2))


def gauss_v(x, y, sigma):
    n, m = len(x), len(y)
    s2 = sigma**2
    return gk(x, x, s2).sum() / n**2 + gk(y, y, s2).sum() / m**2 - 2 * gk(x, y, s2).sum() / (n * m)


def gauss_u(x, y, sigma):
    n, m = len(x), len(y)
    s2 = sigma**2
    return (gk(x, x, s2).sum() - n) / (n * (n - 1)) + (gk(y, y, s2).sum() - m) / (m * (m - 1)) - 2 * gk(x, y, s2).sum() / (n * m)


def gauss_u_paired(x, y, sigma):
    n = len(x)
    s2 = sigma**2
    off = lambda K: K.sum() - np.trace(K)
    return (off(gk(x, x, s2)) + off(gk(y, y, s2)) - 2 * off(gk(x, y, s2))) / (n * (n - 1))


def q_poly(x, A, c, k):
    n = len(x)
    ax = x @ A.T
    G = c + x @ x.T
    return (k * G ** (k - 1) * (ax @ ax.T) + k * (k - 1) * G ** (k - 2) * (ax @ x.T) * (x @ ax.T)).sum() / n**2


def gauss_hess_terms(x, A, sigma):
    s2 = sigma**2
    ax = x @ A.T
    K = gk(x, x, s2)
    n = len(x)
    T = np.zeros((n, n))
    for a in range(n):
        for b in range(n):
            u = x[a] - x[b]
            T[a, b] = K[a, b] * (ax[a] @ ax[b] / s2 - (u @ ax[a]) * (u @ ax[b]) / s2**2)
    return T


Z0 = np.array([[1.0, 0.0, -0.5], [0.25, 0.75, 0.5], [-0.5, 1.0, 0.0], [0.6, -0.4, 0.2],
               [-1.0, 0.3, 0.7], [0.3, -0.8, 0.4], [0.9, 0.2, -0.9], [-0.7, 0.5, 0.1]])
Ar = np.array([[0.0, -1.0, 0.5], [1.0, 0.0, -0.3], [-0.5, 0.3, 0.0]])
Ag = np.array([[0.2, -1.0, 0.5], [0.4, -0.1, 0.2], [-0.5, 0.6, 0.3]])

print("=== lie/009 (M1)")
Zc = Z0 - Z0.mean(0)
M2 = Zc.T @ Zc / 8
lam, Q = np.linalg.eigh(M2)
W = Q @ np.diag(lam**-0.5) @ Q.T
X = Zc @ W.T
print("whitening dev", np.abs(X.T @ X / 8 - np.eye(3)).max(), "mean", np.abs(X.mean(0)).max())
gr = expm(0.7 * Ar)
gg = expm(0.7 * Ag)
Yr = X @ gr.T
Yg = X @ gg.T
print("f2_rot", poly_v(X, Yr, 1.0, 2))
print("f3_rot", repr(poly_v(X, Yr, 1.0, 3)))
print("f2_gen", repr(poly_v(X, Yg, 1.0, 2)))
print("q2_rot", q_poly(X, Ar, 1.0, 2))
print("q3_rot", repr(q_poly(X, Ar, 1.0, 3)))
q3 = q_poly(X, Ag, 1.0, 3)
print("q3_gen", repr(q3))
for h in (1e-2, 3e-3, 1e-3, 3e-4):
    fd = (poly_v(X, X @ expm(h * Ag).T, 1.0, 3) + poly_v(X, X @ expm(-h * Ag).T, 1.0, 3)) / h**2
    print("  h", h, "fd", repr(fd), "rel", abs(fd - 2 * q3) / (2 * q3))

print("=== lie/010 (M2)")
X2 = Z0
sigma = 1.5
g = expm(0.7 * Ar)
Y2 = X2 @ g.T
n = 8
print("u_id", gauss_u_paired(X2, X2, sigma))
u = gauss_u_paired(X2, Y2, sigma)
v = gauss_v(X2, Y2, sigma)
diag = 2.0 / n**2 * (1 - np.diag(gk(X2, Y2, sigma**2))).sum()
print("u_rot", repr(u))
print("v_rot", repr(v))
print("diag", repr(diag), "identity residual", v - (n - 1) / n * u - diag)
print("u_indep", repr(gauss_u(X2, Y2, sigma)))
print("pu_rot", repr(poly_u_paired(X2, Y2, 1.0, 3)))
T = gauss_hess_terms(X2, Ag, sigma)
qv = T.sum() / n**2
qu = (T.sum() - np.trace(T)) / (n * (n - 1))
dg = np.trace(T) / n**2
ax = X2 @ Ag.T
print("qv", repr(qv), "qu", repr(qu), "dg", repr(dg), "tr form", (ax * ax).sum() / (n**2 * sigma**2))
print("split residual", qv - (n - 1) / n * qu - dg)
