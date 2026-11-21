# Replay of corpus cell lie/014 (T1, the Mode B trainer): numpy, analytic task gradient,
# central-difference gradient of the frozen-generator Hessian term (h = 1e-6), so the
# agreement with the Blade cell (reverse-mode AD) is to about 1e-6, not to the last digit.
import numpy as np
np.set_printoptions(precision=6, linewidth=200, suppress=True)

perms = [(0, 1, 2), (0, 2, 1), (1, 0, 2), (1, 2, 0), (2, 0, 1), (2, 1, 0)]
psign = [1, -1, -1, 1, 1, -1]


def orbit(base):
    pts = []
    for r in range(24):
        p = perms[r // 4]
        s0 = 1 - 2 * (r % 2)
        s1 = 1 - 2 * ((r // 2) % 2)
        sg = [s0, s1, psign[r // 4] * s0 * s1]
        pts.append([sg[i] * base[p[i]] for i in range(3)])
    return np.array(pts)


def gk(x, y, s2):
    d2 = (x * x).sum(1)[:, None] + (y * y).sum(1)[None, :] - 2 * x @ y.T
    return np.exp(-d2 / (2 * s2))


def q_gauss(x, A, s2):
    n = len(x)
    ax = x @ A.T
    K = gk(x, x, s2)
    xa_axa = (x * ax).sum(1)
    x_ax = x @ ax.T
    t1 = xa_axa[:, None] - x_ax.T
    t2 = x_ax - xa_axa[None, :]
    return (K * ((ax @ ax.T) / s2 - t1 * t2 / s2**2)).sum() / n**2


def ladder(x, s2=1.0):
    basis = []
    for c in range(9):
        E = np.zeros((3, 3)); E[c // 3, c % 3] = 1.0; basis.append(E)
    qd = [q_gauss(x, B, s2) for B in basis]
    H = np.array([[(q_gauss(x, basis[i] + basis[j], s2) - qd[i] - qd[j]) / 2 for j in range(9)] for i in range(9)])
    return np.linalg.eigh(H)


def mono(x):
    return np.stack([x[:, 0]**2, x[:, 0]*x[:, 1], x[:, 0]*x[:, 2], x[:, 1]**2, x[:, 1]*x[:, 2], x[:, 2]**2], 1)


def run(Z, beta, lam2, eta, rounds, epochs, verbose=True):
    N = len(Z); y = (Z * Z).sum(1)
    X = Z.copy(); X[:, 0] = Z[:, 0] + beta * Z[:, 1]**2
    Mx = mono(X)
    enc = lambda th: X + Mx @ th.T
    def task(th):
        z = enc(th); r = (z * z).sum(1) - y
        return (r * r).mean()
    def task_grad(th):
        z = enc(th); r = (z * z).sum(1) - y
        return 4 * (r[:, None] * z).T @ Mx / N
    def reg(th, gens):
        z = enc(th)
        return sum(q_gauss(z, A, 1.0) for A in gens)
    def reg_grad(th, gens):
        g = np.zeros_like(th); h = 1e-6
        for i in range(3):
            for m in range(6):
                d = np.zeros_like(th); d[i, m] = h
                g[i, m] = (reg(th + d, gens) - reg(th - d, gens)) / (2 * h)
        return g
    th = np.zeros((3, 6))
    out = []
    for rd in range(rounds):
        lam, Q = ladder(enc(th))
        gens = []
        for k in range(3):
            A = Q[:, k].reshape(3, 3) * np.sqrt(2); gens.append((A - A.T) / 2)
        obj0 = task(th) + lam2 * reg(th, gens)
        if verbose:
            print("   round", rd, "task %.3e reg %.3e gap %.1f spread %.2f th011 %.4f" % (task(th), reg(th, gens), lam[3] / lam[2], lam[2] / lam[0], th[0, 3]))
        for ep in range(epochs):
            g = task_grad(th) + (lam2 * reg_grad(th, gens) if lam2 > 0 else 0)
            th = th - eta * g
        obj1 = task(th) + lam2 * reg(th, gens)
        out.append((obj0, obj1))
    lam, Q = ladder(enc(th))
    true = np.array([[0, 0, 0, -beta, 0, 0], [0]*6, [0]*6])
    print("   final task %.3e gap %.1f spread %.2f th011 %.4f maxerr %.4f monotone %s" % (task(th), lam[3] / lam[2], lam[2] / lam[0], th[0, 3], np.abs(th - true).max(), all(b < a for a, b in out)))
    return th


if __name__ == "__main__":
    C12 = np.unique(np.round(orbit([0.9, 0.9, 0.0]), 12), axis=0)
    C8 = np.unique(np.round(orbit([0.5, 0.5, 0.5]), 12), axis=0)
    C6 = np.unique(np.round(orbit([1.2, 0.0, 0.0]), 12), axis=0)
    Z = np.vstack([C12, C8, C6])
    print("Mode B: three rounds of 20 steps, lambda2 = 10")
    run(Z, 0.5, 10.0, 0.05, 3, 20)
    print("control: the same 60 steps on the task alone")
    run(Z, 0.5, 0.0, 0.05, 3, 20)
