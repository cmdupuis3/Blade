import numpy as np
rng = np.random.default_rng(5)
n, d, K, c, sig = 40, 3, 4, 0.7, 1.1
X = rng.standard_normal((n, d)); A = rng.standard_normal((d, d)); a = A.ravel()   # row-major vec
G = X@X.T; XA = X@A.T; GAA = XA@XA.T; P = XA@X.T
Kp = K*(c+G)**(K-1); Kpp = K*(K-1)*(c+G)**(K-2)
q = (Kp*GAA + Kpp*P*P.T).mean()
Z = np.einsum('ap,aq->apq', X, X).reshape(n, d*d)
H1 = np.kron(np.eye(d), X.T@Kp@X)
ZKZ = (Z.T@Kpp@Z).reshape(d, d, d, d)          # [(k,i'),(i,k')] -> axes (k,i',i,k')
H2 = ZKZ.transpose(2, 0, 1, 3).reshape(d*d, d*d)  # -> (i,k),(i',k')
H = (H1+H2)/n**2; H = (H+H.T)/2
print("dot-product kernel assembly:", q, a@H@a)
# Gaussian
D2 = (X**2).sum(1)[:, None] + (X**2).sum(1)[None, :] - 2*G; Kg = np.exp(-D2/(2*sig**2))
dP = np.diag(P)
qg = (Kg*(GAA/sig**2 - (dP[:, None]-P)*(P.T-dP[None, :])/sig**4)).mean()
T1 = np.kron(np.eye(d), X.T@Kg@X)/sig**2
KX = Kg@X
# sum_ab K_ab (u u^T) (x) (x_a x_b^T), u = x_a - x_b; entry ((i,k),(i',k')) = sum_ab K_ab u_i u_i' x_ak x_bk'
S = np.zeros((d, d, d, d))
S += np.einsum('ai,aj,ak,al->ikjl', X, X, X, KX)                 # x_a x_a^T  (x) x_a (K x)_b
S -= np.einsum('ab,ai,bj,ak,bl->ikjl', Kg, X, X, X, X)            # - x_a x_b^T
S -= np.einsum('ab,bi,aj,ak,bl->ikjl', Kg, X, X, X, X)            # - x_b x_a^T
S += np.einsum('bi,bj,bl,bk->ikjl', X, X, X, KX)                 # x_b x_b^T (x) (K x)_b... careful: sum_a K_ab x_ak = (KX)_bk
T2 = S.reshape(d*d, d*d)/sig**4
Hg = (T1 - T2)/n**2; Hg = (Hg+Hg.T)/2
print("Gaussian assembly:", qg, a@Hg@a)
