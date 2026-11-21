# Replay of corpus cells lie/011 (D1', the Gaussian-kernel Hessian ladder on an
# octahedral orbit) and lie/012 (N2, an so(2)-invariant sample): the value pins.
import itertools
import numpy as np

np.set_printoptions(precision=15, linewidth=200)


def gk(x, y, s2):
    d2 = (x * x).sum(1)[:, None] + (y * y).sum(1)[None, :] - 2 * x @ y.T
    return np.exp(-d2 / (2 * s2))


def q_gauss(x, A, s2):
    n = len(x)
    ax = x @ A.T
    K = gk(x, x, s2)
    # u = x_a - x_b ; <u, ax_a> = xa.axa - xb.axa
    xa_axa = (x * ax).sum(1)
    x_ax = x @ ax.T  # [b, a] = x_b . ax_a
    t1 = xa_axa[:, None] - x_ax.T  # [a, b] = <u, ax_a>
    t2 = x_ax - xa_axa[None, :]  # [a, b] = <u, ax_b> = x_a.ax_b - x_b.ax_b
    return (K * ((ax @ ax.T) / s2 - t1 * t2 / s2**2)).sum() / n**2


def hess(x, s2, basis):
    p = len(basis)
    qd = [q_gauss(x, B, s2) for B in basis]
    H = np.zeros((p, p))
    for i in range(p):
        for j in range(p):
            H[i, j] = (q_gauss(x, basis[i] + basis[j], s2) - qd[i] - qd[j]) / 2
    return H


def egl():
    out = []
    for c in range(9):
        E = np.zeros((3, 3))
        E[c // 3, c % 3] = 1.0
        out.append(E)
    return out


print("=== lie/011 (D1')")
base = np.array([1.0, 0.6, 0.3])
perms = [(0, 1, 2), (0, 2, 1), (1, 0, 2), (1, 2, 0), (2, 0, 1), (2, 1, 0)]
psign = [1, -1, -1, 1, 1, -1]
pts = []
for r in range(24):
    p = perms[r // 4]
    s0 = 1 - 2 * (r % 2)
    s1 = 1 - 2 * ((r // 2) % 2)
    sg = [s0, s1, psign[r // 4] * s0 * s1]  # det = +1: the 24 proper rotations
    pts.append([sg[i] * base[p[i]] for i in range(3)])
X = np.array(pts)
s2 = 1.0
H = hess(X, s2, egl())
lam, Q = np.linalg.eigh(H)
print("ladder ascending", lam)
for k in range(9):
    A = Q[:, k].reshape(3, 3)
    print(k, "lam", lam[k], "antisym part", np.linalg.norm(A - A.T) ** 2 / 2, "sym part", np.linalg.norm(A + A.T) ** 2 / 2, "trace", np.trace(A))
# closed-form block values: q on one normalized representative of each irrep
Ea = (np.array([[0, -1, 0], [1, 0, 0], [0, 0, 0]])) / np.sqrt(2)
Et2 = (np.array([[0, 1, 0], [1, 0, 0], [0, 0, 0]])) / np.sqrt(2)
Ee = np.diag([1, -1, 0]) / np.sqrt(2)
Ea1 = np.eye(3) / np.sqrt(3)
for name, A in (("T1 so(3)", Ea), ("T2", Et2), ("E", Ee), ("A1", Ea1)):
    print(name, repr(q_gauss(X, A, s2)))

print("=== lie/012 (N2)")
# 8 points on a circle in the (z0, z1) plane at each of 3 heights: invariant under C8, z2 free
pts = []
hs = [-0.7, 0.2, 0.9]
rads = [1.0, 0.6, 1.3]
for r in range(24):
    h, rad = hs[r // 8], rads[r // 8]
    th = 2 * np.pi * (r % 8) / 8 + 0.3 * h
    pts.append([rad * np.cos(th), rad * np.sin(th), h])
X = np.array(pts)
H = hess(X, 1.0, egl())
lam, Q = np.linalg.eigh(H)
print("ladder descending", lam[::-1])
print("gap lam[1]/lam[0]", lam[1] / lam[0])
A = Q[:, 0].reshape(3, 3) * np.sqrt(2)
print("bottom generator J"); print(A)
print("J J^T spectrum", np.linalg.eigvalsh(A @ A.T)[::-1])
# commutant of J: M J - J M = 0 on gl(3)
J = np.array([[0.0, -1.0, 0.0], [1.0, 0.0, 0.0], [0.0, 0.0, 0.0]])
C = np.zeros((9, 9))
for c, E in enumerate(egl()):
    C[:, c] = (E @ J - J @ E).reshape(9)
print("commutant singular values", np.linalg.svd(C, compute_uv=False))
