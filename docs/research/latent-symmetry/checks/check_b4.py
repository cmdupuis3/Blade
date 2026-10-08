import numpy as np
from scipy.linalg import null_space, expm
rng = np.random.default_rng(1)
s2 = np.sqrt
def table(axis, l):
    d = 2*l+1; m = np.zeros((d, d))
    a = lambda k: (l-k)*(l+k+1); b = lambda k: (l+k)*(l-k+1)
    if axis == 'z':
        for k in range(1, l+1): m[l+k, l-k] = -k; m[l-k, l+k] = k
    if axis == 'y':
        if l >= 1: m[l, l+1] = -0.5*s2(2*a(0)); m[l+1, l] = 0.5*s2(2*b(1))
        for k in range(2, l+1): m[l+k, l+k-1] = 0.5*s2(b(k)); m[l-k, l-k+1] = 0.5*s2(b(k))
        for k in range(1, l): m[l+k, l+k+1] = -0.5*s2(a(k)); m[l-k, l-k-1] = -0.5*s2(a(k))
    if axis == 'x':
        if l >= 1: m[l, l-1] = 0.5*s2(2*a(0)); m[l-1, l] = -0.5*s2(2*b(1))
        for k in range(2, l+1): m[l+k, l-k+1] = 0.5*s2(b(k)); m[l-k, l+k-1] = -0.5*s2(b(k))
        for k in range(1, l): m[l+k, l-k-1] = 0.5*s2(a(k)); m[l-k, l+k+1] = -0.5*s2(a(k))
    return m
br = lambda X, Y: X@Y - Y@X
for l in range(0, 5):
    X, Y, Z = (table(a, l) for a in 'xyz')
    print(f"l={l}: [Lx,Ly]-Lz={np.abs(br(X,Y)-Z).max():.1e} [Ly,Lz]-Lx={np.abs(br(Y,Z)-X).max():.1e} "
          f"Casimir+l(l+1)={np.abs(X@X+Y@Y+Z@Z + l*(l+1)*np.eye(2*l+1)).max():.1e} antisym={np.abs(X+X.T).max()+np.abs(Y+Y.T).max():.1e}")
# l=1 vs standard rotation generators in (y,z,x) order
eps = np.zeros((3,3,3))
for (i,j,k) in [(0,1,2),(1,2,0),(2,0,1)]: eps[i,j,k]=1; eps[i,k,j]=-1
Lstd = [-eps[i] for i in range(3)]   # (L_i)_{jk} = -eps_ijk, basis (x,y,z)
P = np.zeros((3,3)); P[0,1]=P[1,2]=P[2,0]=1  # new coords (y,z,x) = P (x,y,z)
for i, a in enumerate('xyz'):
    print(f"l=1 table {a} == P Lstd P^T:", np.allclose(table(a,1), P@Lstd[i]@P.T))
print("Lz std generates CCW rotation:", np.round(expm(np.pi/2*Lstd[2]) @ np.array([1,0,0]), 12))

# ---------- canonicalization pipeline on a disguised rep ----------
spec = [0, 1, 1, 2]
D = {a: None for a in 'xyz'}
def blockdiag(ms):
    n = sum(m.shape[0] for m in ms); out = np.zeros((n, n)); o = 0
    for m in ms: k = m.shape[0]; out[o:o+k, o:o+k] = m; o += k
    return out
Dstd = [blockdiag([table(a, l) for l in spec]) for a in 'xyz']
d = Dstd[0].shape[0]
S = rng.standard_normal((d, d)) + 3*np.eye(d)
Bmix = rng.standard_normal((3, 3))
L = [sum(Bmix[i, j] * S @ Dstd[j] @ np.linalg.inv(S) for j in range(3)) for i in range(3)]
# (i) orthonormal Frobenius basis + structure constants
V = np.stack([m.ravel() for m in L], 1)
U_, sv, _ = np.linalg.svd(V, full_matrices=False)
E = [U_[:, i].reshape(d, d) for i in range(3)]
c = np.array([[[np.sum(E[k]*br(E[i], E[j])) for k in range(3)] for j in range(3)] for i in range(3)])
closure = max(np.linalg.norm(br(E[i],E[j]) - sum(c[i,j,k]*E[k] for k in range(3))) for i in range(3) for j in range(3))
kap = np.einsum('ikl,jlk->ij', c, c)
print("closure defect", closure, " Killing eig", np.linalg.eigvalsh(kap))
# (d) invariant inner product via Casimir projector on bilinear forms: rho_W(E)(S) = -(E^T S + S E)
kinv = np.linalg.inv(kap)
def rhoW(Em):  # matrix acting on vec(S) (row-major)
    I = np.eye(d)
    return -(np.kron(Em.T, I) + np.kron(I, Em.T))   # row-major: vec(E^T S) = kron(E^T, I) vec S ; vec(S E) = kron(I, E^T) vec S
Om = -2*sum(kinv[i, j]*rhoW(E[i])@rhoW(E[j]) for i in range(3) for j in range(3))
Nr = null_space(Om, rcond=1e-9); Nl = null_space(Om.T, rcond=1e-9)
coef = np.linalg.solve(Nl.T@Nr, Nl.T@np.eye(d).ravel())
M = (Nr@coef).reshape(d, d); M = (M+M.T)/2
print("dim invariant bilinear forms", Nr.shape[1], " M eig min/max", np.linalg.eigvalsh(M).min(), np.linalg.eigvalsh(M).max(),
      " invariance resid", max(np.abs(Em.T@M + M@Em).max() for Em in E))
w_, Q_ = np.linalg.eigh(M); R = Q_@np.diag(np.sqrt(w_))@Q_.T; Ri = np.linalg.inv(R)
Et = [R@Em@Ri for Em in E]
print("antisymmetry after change of variables", max(np.abs(Em+Em.T).max() for Em in Et))
# (f) -kappa/2 orthonormal oriented basis
Lk, Qk = np.linalg.eigh(-kap/2)
J = [sum(Qk[j, i]/np.sqrt(Lk[i])*Et[j] for j in range(3)) for i in range(3)]
c12_3 = np.sum(J[2]*br(J[0], J[1]))/np.sum(J[2]*J[2])
if c12_3 < 0: J = [-x for x in J]
print("orientation sign was", np.sign(c12_3), " [Jx,Jy]-Jz", np.abs(br(J[0],J[1])-J[2]).max(), np.abs(br(J[1],J[2])-J[0]).max())
C = J[0]@J[0]+J[1]@J[1]+J[2]@J[2]
ev, evec = np.linalg.eigh(C)
print("Casimir eigenvalues", np.round(ev, 9))
# (h) intertwiner nullspace per l
rows = []
for l in sorted(set(spec)):
    Dl = [table(a, l) for a in 'xyz']; n = 2*l+1
    Amat = np.vstack([np.kron(np.eye(n), Jm.T) - np.kron(Dm, np.eye(d)) for Jm, Dm in zip(J, Dl)])  # row-major vec(T), T is n x d
    Ns = null_space(Amat, rcond=1e-8)
    Ts = [Ns[:, k].reshape(n, d) for k in range(Ns.shape[1])]
    G = np.array([[np.sum(Ta*Tb) for Tb in Ts] for Ta in Ts])
    gw, gv = np.linalg.eigh(G); W = gv@np.diag(gw**-0.5)@gv.T
    Ts = [np.sqrt(n)*sum(W[i, j]*Ts[j] for j in range(len(Ts))) for i in range(len(Ts))]
    print(f"l={l}: Hom dim {len(Ts)}, T T^T = I:", all(np.allclose(T@T.T, np.eye(n)) for T in Ts))
    rows += Ts
Ufin = np.vstack(rows)
print("U orthogonal:", np.allclose(Ufin@Ufin.T, np.eye(d)))
specsorted = sorted(spec)
Dtarget = [blockdiag([table(a, l) for l in specsorted]) for a in 'xyz']
print("U J_a U^T == standard tables:", [np.allclose(Ufin@J[i]@Ufin.T, Dtarget[i], atol=1e-8) for i in range(3)])
