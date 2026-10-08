import numpy as np
rng = np.random.default_rng(4)
d = 3
def sample(N):
    th = rng.uniform(0, 2*np.pi, N)
    r = rng.gamma(2.0, 1.0, N); h = r - 2.0 + 0.5*rng.standard_normal(N)   # (r,h) dependent, non-Gaussian
    Z = np.stack([r*np.cos(th), r*np.sin(th), h], 1)
    return Z
def moments(Z, K):
    out = []
    for k in range(1, K+1):
        T = Z
        M = Z
        for _ in range(k-1):
            M = np.einsum('n...,ni->n...i', M, Z)
        out.append(M.mean(0))
    return out
def rho(A, M):
    k = M.ndim; out = np.zeros_like(M)
    for s in range(k):
        out = out + np.moveaxis(np.tensordot(A, M, axes=([1], [s])), 0, s)
    return out
def Cmat(Ms):
    cols = []
    for i in range(d):
        for j in range(d):
            Eij = np.zeros((d, d)); Eij[i, j] = 1
            cols.append(np.concatenate([rho(Eij, M).ravel() for M in Ms]))
    return np.array(cols).T
Jz = np.zeros((3,3)); Jz[1,0] = 1; Jz[0,1] = -1
for N in [10**4, 10**5, 10**6]:
    Z = sample(N)
    # population mean/cov are known: whiten with the population transform for a clean demo
    Zc = Z - Z.mean(0); S = np.cov(Zc.T); w, V = np.linalg.eigh(S); W = V@np.diag(w**-0.5)@V.T
    Y = Zc @ W.T
    Ms = moments(Y, 4)
    for K in [2, 3, 4]:
        sv, Vt = np.linalg.svd(Cmat(Ms[:K]))[1:]
        print(f"N={N:8d} K={K}  smallest 4 singular values {np.round(sv[-4:], 4)}")
    v = Vt[-1].reshape(3, 3)
    # Jz expressed in whitened coordinates: W Jz W^{-1}
    Jw = W @ Jz @ np.linalg.inv(W); Jw /= np.linalg.norm(Jw)
    print("    sin(angle) between bottom singular vector (K=4) and whitened Jz:", np.sqrt(max(0, 1-np.sum(v*Jw)**2)))
