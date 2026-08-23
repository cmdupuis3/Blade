import os, re, sys
from fractions import Fraction

ROOT = r"C:\Users\cdupu\Documents\GitHub\Blade\tests\corpus"

# The two byte-exactness gates' slices, read out of tests/InterpDiff.fs / DiffOracle.fs
M0 = ["basic","guards","static","intrinsics"]
M1 = M0 + ["functions","structs","struct-mutual","struct-aborts","sum-types","interfaces",
           "mutability","mutability-errors","units","unit-errors","modules","tuples","display-errors","diagnostics"]
M2 = ["loops","recursive-arrays","bracketed","anon-ranges","replicate","tuple-views",
      "zero-combinators","sequence-combinators","guard-combinators","func-arrays","arity","inference-probes"]
M3 = ["symmetry","reynolds"]
RAND=["rand"]; DISP=["display"]
M4A=["sql-reduce","sql-foreign-keys","sql-sort","sql-set-ops","sql-unique-contains","sql-extents",
     "sql-regressions","sql-combined","sql-extents-multi-rank","sql-group-by","sql-masks",
     "sql-semijoins","sql-v24d-probes"]
FB=["fallback"]; IDX=["index-types"]
M5=["ad","ad-jvp","ad-jvp-comb","spectra","math","ml-ops","ml-equiv","ml-e2e","ppl"]
DC=["deferred-concrete"]; SJ=["stack-join"]; MF=["memfree"]
INTERP_SLICE = set(M1+M2+M3+RAND+DISP+M4A+FB+M5+IDX+DC+SJ+MF)
ORACLE_SLICE = set(["basic","loops","guards","recursive-arrays","stack-join"])

NUM = re.compile(r'(?<![A-Za-z0-9_.])(\d+\.\d*(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?|\d+[eE][-+]?\d+)')
INT = re.compile(r'(?<![A-Za-z0-9_.])(\d+)(?![0-9.eE])')
# operations that force a rounding somewhere regardless of literal exactness
TRANS = re.compile(r'\b(sqrt|exp|log|log2|log10|sin|cos|tan|asin|acos|atan|atan2|sinh|cosh|tanh|pow|cbrt|erf|erfc|gamma|lgamma|hypot|norm|normalize|mean|variance|stddev|softmax|sigmoid|expm1|log1p|rsqrt|eigh|solve|det|inv|svd|qr|cholesky)\s*\(')
RANDU = re.compile(r'\brand\.(uniform|normal|exponential|poisson|gamma|beta|binomial|bernoulli)')
DIV   = re.compile(r'/(?!/)')          # division; '//' is a comment
UNITDECL = re.compile(r'^\s*(Unit|type)\b')

def dyadic(tok):
    """True if the decimal literal is EXACTLY a dyadic rational with a small
       numerator/denominator -- i.e. representable, and products/sums of a
       handful of them stay exact in float64."""
    try:
        f = Fraction(tok)
    except Exception:
        return False
    d = f.denominator
    if d & (d-1): return False        # denominator not a power of two -> rounds
    if d > (1<<12): return False       # too many fraction bits to stay exact under products
    if abs(f.numerator) > (1<<24): return False
    return True

def strip_comments(src):
    code, pins = [], []
    for ln in src.split("\n"):
        i = ln.find("//")
        if i >= 0:
            pins.append(ln[i:])
            ln = ln[:i]
        code.append(ln)
    return "\n".join(code), "\n".join(pins)

rows = []
for dirpath, _, files in os.walk(ROOT):
    cat = os.path.relpath(dirpath, ROOT).replace("\\","/")
    for fn in files:
        if not fn.endswith(".blade"): continue
        p = os.path.join(dirpath, fn)
        src = open(p, encoding="utf-8", errors="replace").read()
        code, pins = strip_comments(src)
        # a line that only declares Units contributes '/' without arithmetic
        codelines = [l for l in code.split("\n") if not UNITDECL.match(l)]
        arith = "\n".join(codelines)

        floats = NUM.findall(code)
        nondyadic = [t for t in floats if not dyadic(t)]
        has_trans = bool(TRANS.search(code))
        has_rand  = bool(RANDU.search(code))
        has_div   = bool(DIV.search(arith))
        has_float_lit = bool(floats)
        round_capable = bool(nondyadic) or has_trans or has_rand or has_div
        rows.append(dict(cat=cat, f=fn, nondyadic=len(nondyadic), trans=has_trans,
                         rand=has_rand, div=has_div, floats=len(floats),
                         round_capable=round_capable,
                         reject=("(rejects)" in src or "ERROR:" in pins)))

def rep(name, sel):
    s = [r for r in rows if sel(r)]
    if not s: 
        print(f"{name}: 0 files"); return
    n = len(s)
    nonrej = [r for r in s if not r["reject"]]
    rc = [r for r in nonrej if r["round_capable"]]
    blind = [r for r in nonrej if not r["round_capable"]]
    print(f"{name}: {n} files ({len(nonrej)} non-reject)")
    print(f"   round-CAPABLE (some op can round): {len(rc)}  = {100*len(rc)/max(1,len(nonrej)):.1f}%")
    print(f"   round-BLIND  (exact arithmetic)  : {len(blind)} = {100*len(blind)/max(1,len(nonrej)):.1f}%")
    # breakdown of WHY capable
    print(f"      by non-dyadic literal: {sum(1 for r in rc if r['nondyadic'])}"
          f" | by transcendental: {sum(1 for r in rc if r['trans'])}"
          f" | by division: {sum(1 for r in rc if r['div'])}"
          f" | by rand: {sum(1 for r in rc if r['rand'])}")
    return blind

print("="*72)
print("ROUND-CAPABILITY AUDIT of tests/corpus  (can a 1-ULP change be PRODUCED at all?)")
print("="*72)
rep("ALL CORPUS", lambda r: True)
print()
b1 = rep("InterpDiff gate slice (byte-exact stdout vs interpreter)", lambda r: r["cat"] in INTERP_SLICE)
print()
b2 = rep("DiffOracle gate slice (byte-exact stdout vs pinned oracle)", lambda r: r["cat"] in ORACLE_SLICE)
print()
print("--- per-category round-BLIND share, gated categories only ---")
cats = sorted(INTERP_SLICE | ORACLE_SLICE)
for c in cats:
    s = [r for r in rows if r["cat"]==c and not r["reject"]]
    if not s: continue
    blind = sum(1 for r in s if not r["round_capable"])
    print(f"   {c:28s} {blind:4d}/{len(s):4d} blind  ({100*blind/len(s):5.1f}%)")
