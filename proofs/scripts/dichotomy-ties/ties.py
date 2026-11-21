r"""ties.py -- the per-dimension-canonical scalar-access model with tie-breaks.

Model (reconstructed from proofs/BladeDichotomy.v, Part B):

  * rank r, extents (L, M); an index is x = (i, j), i in [L]^r, j in [M]^r.
  * S_r acts diagonally: p . (i, j) = (pact p i, pact p j) where
    pact p l = [l[p[k]] for k].  A tensor is SYMMETRIC iff T(p . x) = T(x).
  * canon(x) = (sort i, sort j).  The FIBER of a canonical cell c = (a, b) is
    {x : canon x = c}; its diagonal orbits are the double cosets
    Stab(a) \ S_r / Stab(b); V_c = span of their indicators = the restrictions
    of all symmetric tensors to the fiber (dim d_c = #orbits).
  * a SORTER of a tuple i is a permutation s with pact s i = sort i; a
    TIE-BREAKING RULE rho picks one sorter per tuple (both dimensions).
    R_a = { rho(i) : sort i = a } is the sorter set of the canonical tuple a;
    it is a transversal of the right cosets Stab(a) g.
  * a WIDTH-m SCHEME for rho is m weight functions eps_k : S_r x S_r -> F
    plus, for every symmetric T, m stored scalars P_k(c) per cell such that
        T(x) = sum_k eps_k(rho(i), rho(j)) * P_k(canon x)   for all x.
    Storage is arbitrary (per-tensor existential), so soundness is exactly:
    for every cell c, V_c is contained in the restriction to the BLOCK
    I_c = R_a x R_b (the fiber's image under x -> (rho i, rho j), a bijection)
    of W = span(eps_1..eps_m).  min width mw(rho) = least such m.

Everything here is exact (Fractions / integers / GF(p)); no floats.
"""
from fractions import Fraction
from itertools import permutations, product
from functools import lru_cache

# ----------------------------------------------------------------- permutations
def perms(r):
    return list(permutations(range(r)))

def pact(p, l):
    return tuple(l[p[k]] for k in range(len(p)))

def pmul(a, b):
    """pmul a b = pact b a  (BladeDichotomy's convention: first b, then a)."""
    return pact(b, a)

def pinv(p):
    q = [0] * len(p)
    for k, v in enumerate(p):
        q[v] = k
    return tuple(q)

def sgn(p):
    s = 1
    p = list(p)
    for i in range(len(p)):
        for j in range(i + 1, len(p)):
            if p[i] > p[j]:
                s = -s
    return s

# ----------------------------------------------------------------- tuples / cells
def tuples(L, r):
    return [tuple(t) for t in product(range(L), repeat=r)]

def sorted_tuples(L, r):
    return sorted(set(tuple(sorted(t)) for t in tuples(L, r)))

def sorters(i):
    a = tuple(sorted(i))
    return [p for p in perms(len(i)) if pact(p, i) == a]

def stab(a):
    return [p for p in perms(len(a)) if pact(p, a) == a]

def orbit_name(x, r):
    """complete invariant of the diagonal orbit: the sorted list of pairs"""
    i, j = x[:r], x[r:]
    return tuple(sorted(zip(i, j)))

def fiber(a, b):
    """all x = i ++ j with sort i = a, sort j = b"""
    r = len(a)
    out = []
    for p in perms(r):
        for q in perms(r):
            out.append(pact(p, a) + pact(q, b))
    return sorted(set(out))

def cell_orbits(a, b):
    """the diagonal orbits of the fiber, as lists of x"""
    r = len(a)
    groups = {}
    for x in fiber(a, b):
        groups.setdefault(orbit_name(x, r), []).append(x)
    return [sorted(v) for _, v in sorted(groups.items())]

def ndc(a, b):
    return len(cell_orbits(a, b))

# ----------------------------------------------------------------- rules
class Rule:
    """A tie-breaking rule: rho_lat on [L]^r, rho_lon on [M]^r."""
    def __init__(self, r, L, M, lat, lon):
        self.r, self.L, self.M = r, L, M
        self.lat, self.lon = dict(lat), dict(lon)
        for i, s in self.lat.items():
            assert pact(s, i) == tuple(sorted(i)), (i, s)
        for j, t in self.lon.items():
            assert pact(t, j) == tuple(sorted(j)), (j, t)

    def R(self, side, a):
        rho = self.lat if side == 'lat' else self.lon
        return sorted(set(rho[i] for i in rho if tuple(sorted(i)) == a))

    def point(self, x):
        i, j = x[:self.r], x[self.r:]
        return (self.lat[i], self.lon[j])

    def cells(self):
        return [(a, b) for a in sorted_tuples(self.L, self.r)
                for b in sorted_tuples(self.M, self.r)]

    def block(self, a, b):
        return [self.point(x) for x in fiber(a, b)]

def lex_rule(r, L, M):
    """the canonical rule: lexicographically first sorter (as image list)"""
    lat = {i: min(sorters(i)) for i in tuples(L, r)}
    lon = {j: min(sorters(j)) for j in tuples(M, r)}
    return Rule(r, L, M, lat, lon)

def rule_choices(L, r, constants='id'):
    """Enumerate one-dimension rules as dicts.  constants='id' fixes every
    constant tuple's sorter to the identity (the 4096 census convention);
    constants='all' enumerates them too."""
    ts = tuples(L, r)
    opts = []
    for i in ts:
        ss = sorters(i)
        if len(set(i)) == 1 and constants == 'id':
            ss = [tuple(range(r))]
        opts.append(ss)
    for choice in product(*opts):
        yield dict(zip(ts, choice))

# ----------------------------------------------------------------- exact linear algebra over Q
def rank_Q(rows):
    """rank of a list of rows (Fractions / ints), exact"""
    rows = [[Fraction(v) for v in row] for row in rows if any(row)]
    if not rows:
        return 0
    n = len(rows[0])
    rk = 0
    col = 0
    while rk < len(rows) and col < n:
        piv = None
        for k in range(rk, len(rows)):
            if rows[k][col] != 0:
                piv = k
                break
        if piv is None:
            col += 1
            continue
        rows[rk], rows[piv] = rows[piv], rows[rk]
        pv = rows[rk][col]
        rows[rk] = [v / pv for v in rows[rk]]
        for k in range(len(rows)):
            if k != rk and rows[k][col] != 0:
                f = rows[k][col]
                rows[k] = [u - f * v for u, v in zip(rows[k], rows[rk])]
        rk += 1
        col += 1
    return rk

def rowspace_contains(rows, vec):
    return rank_Q(rows + [vec]) == rank_Q(rows)

def nullspace_Q(rows, n):
    """basis of {v : row . v = 0 for all rows}, exact"""
    rows = [[Fraction(v) for v in row] for row in rows]
    pivots = []
    rk = 0
    for col in range(n):
        piv = None
        for k in range(rk, len(rows)):
            if rows[k][col] != 0:
                piv = k
                break
        if piv is None:
            continue
        rows[rk], rows[piv] = rows[piv], rows[rk]
        pv = rows[rk][col]
        rows[rk] = [v / pv for v in rows[rk]]
        for k in range(len(rows)):
            if k != rk and rows[k][col] != 0:
                f = rows[k][col]
                rows[k] = [u - f * v for u, v in zip(rows[k], rows[rk])]
        pivots.append(col)
        rk += 1
    free = [c for c in range(n) if c not in pivots]
    basis = []
    for f in free:
        v = [Fraction(0)] * n
        v[f] = Fraction(1)
        for k, pc in enumerate(pivots):
            v[pc] = -rows[k][f]
        basis.append(v)
    return basis

# ----------------------------------------------------------------- GF(p) linear algebra
def rank_p(rows, p):
    rows = [[v % p for v in row] for row in rows]
    rows = [row for row in rows if any(row)]
    if not rows:
        return 0
    n = len(rows[0])
    rk = 0
    col = 0
    while rk < len(rows) and col < n:
        piv = None
        for k in range(rk, len(rows)):
            if rows[k][col]:
                piv = k
                break
        if piv is None:
            col += 1
            continue
        rows[rk], rows[piv] = rows[piv], rows[rk]
        inv = pow(rows[rk][col], -1, p)
        rows[rk] = [(v * inv) % p for v in rows[rk]]
        for k in range(len(rows)):
            if k != rk and rows[k][col]:
                f = rows[k][col]
                rows[k] = [(u - f * v) % p for u, v in zip(rows[k], rows[rk])]
        rk += 1
        col += 1
    return rk

# ----------------------------------------------------------------- the scheme problem
class Problem:
    """The block/orbit structure of a rule: points, cells, V_c indicator rows."""
    def __init__(self, rule):
        self.rule = rule
        r = rule.r
        pts = set()
        self.cells = []      # list of (a, b, pts_of_block, orbit_indicators)
        for (a, b) in rule.cells():
            orbs = cell_orbits(a, b)
            blk = [rule.point(x) for o in orbs for x in o]
            assert len(set(blk)) == len(blk), "block not a bijective image"
            pts.update(blk)
            inds = []
            for o in orbs:
                inds.append(set(rule.point(x) for x in o))
            self.cells.append((a, b, blk, inds))
        self.points = sorted(pts)
        self.pidx = {pt: k for k, pt in enumerate(self.points)}
        self.n = len(self.points)

    def d_max(self):
        return max(len(inds) for _, _, _, inds in self.cells)

    def V_rows(self, cell_k, restrict=None):
        """indicator rows of V_c, as vectors over self.points (zero off block),
        optionally restricted to a point set"""
        _, _, blk, inds = self.cells[cell_k]
        rows = []
        for o in inds:
            if restrict is None:
                rows.append([1 if pt in o else 0 for pt in self.points])
            else:
                rows.append([1 if pt in o else 0 for pt in restrict])
        return rows

    def width_ok_Q(self, eps_rows):
        """exact soundness check of a candidate W = rowspace(eps_rows),
        eps_rows given over self.points"""
        for k, (a, b, blk, inds) in enumerate(self.cells):
            cols = [self.pidx[pt] for pt in blk]
            E = [[row[c] for c in cols] for row in eps_rows]
            rkE = rank_Q(E)
            for o in inds:
                v = [1 if pt in o else 0 for pt in blk]
                if rank_Q(E + [v]) != rkE:
                    return False
        return True

    # --- the restriction lower bound ------------------------------------
    def lower_bound(self):
        """LB = max over point sets Y of dim sum_{c : I_c >= Y} res_Y(V_c),
        Y ranging over intersections of blocks (products of intersections of
        sorter sets).  Returns (LB, witness Y, cells used)."""
        rule = self.rule
        latR = {a: set(rule.R('lat', a)) for a in sorted_tuples(rule.L, rule.r)}
        lonR = {b: set(rule.R('lon', b)) for b in sorted_tuples(rule.M, rule.r)}
        def inters(Rs):
            keys = list(Rs)
            seen = {}
            for mask in range(1, 1 << len(keys)):
                Y = None
                for k in range(len(keys)):
                    if mask >> k & 1:
                        Y = set(Rs[keys[k]]) if Y is None else Y & Rs[keys[k]]
                if Y:
                    seen[frozenset(Y)] = True
            return list(seen)
        best = (0, None, None)
        for Yl in inters(latR):
            for Ym in inters(lonR):
                Y = sorted(product(sorted(Yl), sorted(Ym)))
                rows = []
                used = []
                for k, (a, b, blk, inds) in enumerate(self.cells):
                    if Yl <= latR[a] and Ym <= lonR[b]:
                        used.append((a, b))
                        rows += self.V_rows(k, restrict=Y)
                d = rank_Q(rows) if rows else 0
                if d > best[0]:
                    best = (d, (sorted(Yl), sorted(Ym)), used)
        return best

    # --- GF(p) exhaustive search for a width-m scheme --------------------
    def search_p(self, m, p, limit=None):
        """Backtracking over e-vectors e(pt) in GF(p)^m, canonical under GL_m
        (a vector outside the current span is WLOG the next basis vector).
        Returns the m x n matrix (rows = eps_k) or None."""
        n = self.n
        # cells touching each point, for incremental checks
        touch = [[] for _ in range(n)]
        cellinfo = []
        for k, (a, b, blk, inds) in enumerate(self.cells):
            cols = [self.pidx[pt] for pt in blk]
            B = [[1 if pt in o else 0 for pt in blk] for o in inds]
            cellinfo.append((cols, B))
            for c in cols:
                touch[c].append(k)
        # order points: most-constrained first (in most cells)
        order = sorted(range(n), key=lambda c: -len(touch[c]))
        pos = {c: t for t, c in enumerate(order)}
        E = [[None] * n for _ in range(m)]
        count = [0]

        def cell_ok(k):
            cols, B = cellinfo[k]
            assigned = [c for c in cols if E[0][c] is not None]
            if not assigned:
                return True
            Ecols = [[E[i][c] for c in assigned] for i in range(m)]
            Brows = [[B[o][cols.index(c)] for c in assigned] for o in range(len(B))]
            rk = rank_p(Ecols, p)
            return rank_p(Ecols + Brows, p) == rk

        def candidates(rank):
            # all vectors in span of first `rank` basis vectors, plus the
            # next basis vector if rank < m
            for coeffs in product(range(p), repeat=rank):
                v = list(coeffs) + [0] * (m - rank)
                yield v, rank
            if rank < m:
                v = [0] * m
                v[rank] = 1
                yield v, rank + 1

        hit = [False]

        def rec(t, rank):
            if limit is not None and count[0] > limit:
                hit[0] = True
                return None
            if t == n:
                return [row[:] for row in E]
            c = order[t]
            for v, nrank in candidates(rank):
                count[0] += 1
                for i in range(m):
                    E[i][c] = v[i]
                if all(cell_ok(k) for k in touch[c]):
                    res = rec(t + 1, nrank)
                    if res is not None:
                        return res
            for i in range(m):
                E[i][c] = None
            return None

        res = rec(0, 0)
        self.last_search_exhaustive = not hit[0]
        self.last_search_nodes = count[0]
        return res

    def search_p_iter(self, m, p, limit=None):
        """Generator over ALL width-m schemes over GF(p) (canonical under
        GL_m), in the same order as search_p.  Used to try several GF(p)
        witnesses for an integer lift."""
        n = self.n
        touch = [[] for _ in range(n)]
        cellinfo = []
        for k, (a, b, blk, inds) in enumerate(self.cells):
            cols = [self.pidx[pt] for pt in blk]
            B = [[1 if pt in o else 0 for pt in blk] for o in inds]
            cellinfo.append((cols, B))
            for c in cols:
                touch[c].append(k)
        order = sorted(range(n), key=lambda c: -len(touch[c]))
        E = [[None] * n for _ in range(m)]
        count = [0]

        def cell_ok(k):
            cols, B = cellinfo[k]
            assigned = [c for c in cols if E[0][c] is not None]
            if not assigned:
                return True
            Ecols = [[E[i][c] for c in assigned] for i in range(m)]
            Brows = [[B[o][cols.index(c)] for c in assigned] for o in range(len(B))]
            return rank_p(Ecols + Brows, p) == rank_p(Ecols, p)

        def candidates(rank):
            for coeffs in product(range(p), repeat=rank):
                yield list(coeffs) + [0] * (m - rank), rank
            if rank < m:
                v = [0] * m; v[rank] = 1
                yield v, rank + 1

        def rec(t, rank):
            if limit is not None and count[0] > limit:
                return
            if t == n:
                yield [row[:] for row in E]
                return
            c = order[t]
            for v, nrank in candidates(rank):
                count[0] += 1
                for i in range(m):
                    E[i][c] = v[i]
                if all(cell_ok(k) for k in touch[c]):
                    yield from rec(t + 1, nrank)
            for i in range(m):
                E[i][c] = None

        yield from rec(0, 0)

    def lift_to_Q(self, E, p):
        """try the GF(p) witness as an integer witness (entries in
        (-p/2, p/2]); exact check"""
        rows = [[(v if v <= p // 2 else v - p) for v in row] for row in E]
        return rows if self.width_ok_Q(rows) else None

    # --- the FLAG lower bound (triangular determinant certificate) ------
    def flag_bound(self):
        """Largest k such that there are points x_1..x_k and cells c_1..c_k
        with x_i in I_{c_j} for all i >= j, and tensors T_j in V_{c_j} with
        T_j(x_j) <> 0 and T_j(x_i) = 0 for i > j.  The k x k matrix
        M_ij = <eps(x_i), P_j> (P_j = storage of T_j at c_j) is then upper
        triangular with nonzero diagonal (its below/diagonal entries are the
        known values T_j(x_i)), although it factors through R^m: m >= k.
        lower_bound() is the special case x_i in every I_{c_j}.
        Returns (k, chain), chain = [(cell_index, point_index, coefficients of
        T_j over the cell's orbit indicators), ...] for j = 1..k."""
        from collections import deque
        cellpts = [frozenset(self.pidx[pt] for pt in blk) for _, _, blk, _ in self.cells]
        rankcache = {}

        def vrows(k, pts):
            _, _, blk, inds = self.cells[k]
            return [[1 if self.points[c] in o else 0 for c in pts] for o in inds]

        def rk(k, S):
            key = (k, S)
            if key not in rankcache:
                rankcache[key] = rank_Q(vrows(k, sorted(S))) if S else 0
            return rankcache[key]

        # states: S = the set of LATER points; a step prepends (c, x)
        start = frozenset()
        parent = {start: None}
        q = deque([start])
        best = start
        while q:
            S = q.popleft()
            if len(S) > len(best):
                best = S
            for k, I in enumerate(cellpts):
                if not S <= I:
                    continue
                r0 = rk(k, S)
                for x in I - S:
                    S2 = S | {x}
                    if S2 in parent:
                        continue
                    if rk(k, S2) > r0:
                        parent[S2] = (S, k, x)
                        q.append(S2)
        chain = []
        S = best
        while parent[S] is not None:
            Sprev, k, x = parent[S]
            pts = [x] + sorted(Sprev)
            rows = vrows(k, pts)
            n_o = len(rows)
            # coefficients a with sum_o a_o rows[o] = (1, 0, ..., 0)
            aug = [[rows[o][c] for o in range(n_o)] + [-(1 if c == 0 else 0)]
                   for c in range(len(pts))]
            sol = next((s for s in nullspace_Q(aug, n_o + 1) if s[-1] != 0), None)
            assert sol is not None
            chain.append((k, x, [s / sol[-1] for s in sol[:-1]]))
            S = Sprev
        chain.reverse()
        return len(best), chain
