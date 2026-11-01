#!/usr/bin/env python3
"""Quick Linux multithread snapshot, three toolchains interleaved: blade-cpp (reusing run1's builds),
gfortran-expert, ifx-expert. Per toolchain: a threads-off build pinned to CPU 1, and the threaded
build at OMP_NUM_THREADS = 1..4 (unbound). Licensed native, full size. Arm order rotated per round."""
import json, os, re, resource, statistics, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = Path("/home/user/Blade")
FORT = REPO / "bench/fortran"
BLADE_RUN1 = HERE / "run1"
OUT = Path(sys.argv[1]).resolve()
KERNELS = sys.argv[2].split(",")
ROUNDS = int(sys.argv[3]) if len(sys.argv) > 3 else 9
THREADS = [1, 2, 3, 4]
IFX = "/opt/ifx/bin/ifx"
RE_T = re.compile(r"completed in\s+(\S+)\s*s")
RE_C = re.compile(r"^\s*checksum\s*=\s*(\S+)", re.M)
RE_I = re.compile(r"^\s*input_checksum\s*=\s*(\S+)", re.M)

FLAGS = {
    "gfortran": {"base": ["-O3", "-march=native", "-ffp-contract=fast", "-fno-external-blas", "-cpp", "-DBENCH_LICENSED"],
                 "serial": ["-fopenmp-simd"], "mt": ["-fopenmp"]},
    "ifx": {"base": ["-O3", "-xHost", "-fp-model=precise", "-mllvm", "-fp-contract=fast", "-fpp",
                     "-DBENCH_LICENSED"],
            "serial": ["-qopenmp-simd"], "mt": ["-qopenmp"]},
}

base = {k: v for k, v in os.environ.items() if not k.startswith("BLADE_") and not k.startswith("OMP_")}
base.update({"BLADE_BLAS": "off", "OMP_DYNAMIC": "FALSE", "OPENBLAS_NUM_THREADS": "1",
             "KMP_AFFINITY": "disabled", "LD_LIBRARY_PATH": "/opt/ifx/lib",
             "PATH": "/opt/ifx/bin:" + os.environ["PATH"]})

def bigstack():  # stands in for the Windows builds' 256 MB main-thread stack (-Wl,--stack / /F)
    resource.setrlimit(resource.RLIMIT_STACK, (resource.RLIM_INFINITY, resource.RLIM_INFINITY))

def fbuild(k, comp, cls):
    d = OUT / k / f"{comp}-expert"; d.mkdir(parents=True, exist_ok=True)
    exe = d / cls
    shim = d / "qpc_shim.o"
    if not shim.exists():
        subprocess.run(["gcc", "-O2", "-c", str(HERE / "qpc_shim.c"), "-o", str(shim)], check=True)
    cc = "gfortran" if comp == "gfortran" else IFX
    f = FLAGS[comp]
    cmd = [cc, *f["base"], *f[cls], f"-I{FORT}", str(REPO / "bench/kernels" / k / "expert.f90"),
           str(shim), "-o", str(exe)]
    r = subprocess.run(cmd, env=base, capture_output=True, text=True, cwd=d)
    (d / f"build-{cls}.log").write_text(" ".join(cmd) + "\n" + r.stdout + r.stderr)
    if r.returncode != 0:
        print(f"{k} {comp} {cls} BUILD FAILED: {(r.stdout + r.stderr)[-600:]}", flush=True)
        return None
    return exe

def run(exe, n, pin):
    env = dict(base); env["OMP_NUM_THREADS"] = str(n)
    cmd = (["taskset", "-c", "1"] if pin else []) + [str(exe)]
    r = subprocess.run(cmd, env=env, capture_output=True, text=True, timeout=600, preexec_fn=bigstack,
                       cwd=exe.parent)
    m = RE_T.search(r.stdout)
    if r.returncode != 0 or not m:
        return None, None, None, (r.stdout + r.stderr)[-300:]
    return float(m.group(1)), RE_C.search(r.stdout).group(1), RE_I.search(r.stdout).group(1), None

res = json.loads((OUT / "results.json").read_text()) if (OUT / "results.json").exists() else {}
for k in KERNELS:
    if k in res: continue
    arms = []
    for tc in ["blade", "gfortran", "ifx"]:
        if tc == "blade":
            se, me = BLADE_RUN1 / k / "serial", BLADE_RUN1 / k / "mt"
        else:
            se, me = fbuild(k, tc, "serial"), fbuild(k, tc, "mt")
        if se: arms.append((f"{tc}/off", se, 1, True))
        if me: arms += [(f"{tc}/mt{n}", me, n, False) for n in THREADS]
    errs = {}
    for a in arms:
        _, _, _, e = run(a[1], a[2], a[3])  # warm-up, discarded
        if e: errs[a[0]] = e
    arms = [a for a in arms if a[0] not in errs]
    times = {a[0]: [] for a in arms}; cks = {}
    for rd in range(ROUNDS):
        rot = arms[rd % len(arms):] + arms[:rd % len(arms)]
        for name, exe, n, pin in rot:
            t, c, ic, e = run(exe, n, pin); time.sleep(0.2)
            if t is not None: times[name].append(t); cks[name] = [c, ic]
            elif e: errs[name] = e
    res[k] = {"times": times, "checksums": cks, "errors": errs}
    med = {a: statistics.median(v) * 1e3 for a, v in times.items() if v}
    print(k, " ".join(f"{a}={m:.2f}" for a, m in med.items()), ("ERR " + str(list(errs))) if errs else "", flush=True)
    (OUT / "results.json").write_text(json.dumps(res, indent=1))
