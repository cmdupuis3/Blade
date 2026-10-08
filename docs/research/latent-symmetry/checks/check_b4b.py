import numpy as np
from scipy.linalg import null_space, orth
rng = np.random.default_rng(2)
br = lambda X, Y: X@Y - Y@X
def E(n, i, j): m = np.zeros((n, n)); m[i, j] = 1; return m
def analyze(name, gens):
    d = gens[0].shape[0]
    V = np.stack([g.ravel() for g in gens], 1)
    B = orth(V); r = B.shape[1]
    Eb = [B[:, i].reshape(d, d) for i in range(r)]
    c = np.array([[[np.sum(Eb[k]*br(Eb[i], Eb[j])) for k in range(r)] for j in range(r)] for i in range(r)])
    clos = max(np.linalg.norm(br(Eb[i],Eb[j]) - sum(c[i,j,k]*Eb[k] for k in range(r))) for i in range(r) for j in range(r))
    kap = np.einsum('ikl,jlk->ij', c, c)
    ev = np.linalg.eigvalsh(kap); tol = 1e-9*max(1, np.abs(ev).max())
    sig = (int((ev > tol).sum()), int((ev < -tol).sum()), int((np.abs(ev) <= tol).sum()))
    # derived algebra
    Dv = np.stack([br(Eb[i], Eb[j]).ravel() for i in range(r) for j in range(i+1, r)], 1) if r > 1 else np.zeros((d*d, 1))
    Dd = orth(Dv, rcond=1e-9).shape[1] if np.abs(Dv).max() > 1e-12 else 0
    # derived algebra coords in basis Eb
    Dcoord = (B.T @ Dv) if Dd else np.zeros((r, 0))
    # radical = kappa-orthogonal of [g,g]: {x : x^T kap y = 0 for y in Dcoord}
    rad = null_space((kap @ Dcoord).T, rcond=1e-9) if Dd else np.eye(r)
    # center = ker ad
    adm = np.vstack([c[:, i, :].T for i in range(r)])  # rows: for each i, sum_a x_a c[a,i,k] = 0
    cen = null_space(np.vstack([np.einsum('a,aik->ik', np.eye(r)[a], c) for a in range(r)]).reshape(r, -1).T, rcond=1e-9)
    # rank: multiplicity of eigenvalue 0 of ad_X for generic X
    x = rng.standard_normal(r); adX = np.einsum('a,aik->ki', x, c)
    rank = int((np.abs(np.linalg.eigvals(adX)) < 1e-7).sum())
    print(f"{name:10s} dim={r} closure={clos:.0e} [g,g]dim={Dd} center={cen.shape[1]} rad(perp)={rad.shape[1]} killing(+,-,0)={sig} rank={rank}")
    return rad
J3 = [E(3,2,1)-E(3,1,2), E(3,0,2)-E(3,2,0), E(3,1,0)-E(3,0,1)]
analyze("so(3)", J3)
analyze("sl(2,R)", [E(2,0,0)-E(2,1,1), E(2,0,1), E(2,1,0)])
so4 = [E(4,i,j)-E(4,j,i) for i in range(4) for j in range(i+1,4)]
analyze("so(4)", so4)
so31 = [E(4,i,j)-E(4,j,i) for i in range(1,4) for j in range(i+1,4)] + [E(4,0,j)+E(4,j,0) for j in range(1,4)]
analyze("so(3,1)", so31)
se2 = [E(3,1,0)-E(3,0,1), E(3,0,2), E(3,1,2)]
analyze("se(2)", se2)
se11 = [E(3,1,0)+E(3,0,1), E(3,0,2), E(3,1,2)]
analyze("se(1,1)", se11)
se3 = [np.pad(j, ((0,1),(0,1))) for j in J3] + [E(4,i,3) for i in range(3)]
rad = analyze("se(3)", se3)
heis = [E(3,0,1), E(3,1,2), E(3,0,2)]
analyze("heis3", heis)
gl2 = [E(2,i,j) for i in range(2) for j in range(2)]
analyze("gl(2)", gl2)
u2 = [1j*np.eye(2)]
aff_sl2 = [np.pad(m, ((0,1),(0,1))) for m in [E(2,0,0)-E(2,1,1), E(2,0,1), E(2,1,0)]] + [E(3,0,2), E(3,1,2)]
analyze("sl2xR2", aff_sl2)
# commutant checks
def commutant(gens):
    d = gens[0].shape[0]; I = np.eye(d)
    A = np.vstack([np.kron(I, g.T) - np.kron(g, I) for g in gens])  # row-major vec: vec(MG)=kron(I,G^T)vecM, vec(GM)=kron(G,I)vecM
    N = null_space(A, rcond=1e-9); return [N[:, k].reshape(d, d) for k in range(N.shape[1])]
J2 = np.array([[0., -1], [1, 0]])
print("commutant dims: so(2) on R2:", len(commutant([J2])),
      "| u(1) charge1 on R4:", len(commutant([np.kron(np.eye(2), J2)])),
      "| u(1) charges (1,2) on R4:", len(commutant([np.block([[J2, 0*J2], [0*J2, 2*J2]])])))
# su(2) on H=R4 by left mult: i,j,k left-multiplication matrices
def qmat(q):  # left mult by quaternion q=(a,b,c,d) on (1,i,j,k)
    a,b,c,d = q
    return np.array([[a,-b,-c,-d],[b,a,-d,c],[c,d,a,-b],[d,-c,b,a]])
su2H = [qmat((0,1,0,0)), qmat((0,0,1,0)), qmat((0,0,0,1))]
Cm = commutant(su2H)
print("su(2) on H commutant dim", len(Cm), " Casimir eig (with [Lx,Ly]=Lz normalization):",
      np.round(np.linalg.eigvalsh(sum((m/2)@(m/2) for m in su2H)), 6))
