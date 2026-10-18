"""Summarize the loops of one function in an objdump -d listing.

usage: python asmfn.py file.s <function-name-substring>
Prints every backward branch (a loop) with the instruction count of its body and, inside
the body: vector/scalar FP stores to memory, FMAs / mul / div / add (packed vs scalar),
64-bit integer vector multiplies (vpmuludq), and calls.
"""
import re
import sys

path, name = sys.argv[1], sys.argv[2]
lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
start = None
for i, l in enumerate(lines):
    if re.match(r"^[0-9a-f]+ <.*>:$", l) and name in l:
        start = i
        break
if start is None:
    sys.exit(f"no function containing {name!r}")
body = []
for l in lines[start + 1:]:
    if re.match(r"^[0-9a-f]+ <.*>:$", l):
        break
    m = re.match(r"^\s+([0-9a-f]+):\s+(.*)$", l)
    if m:
        body.append((int(m.group(1), 16), m.group(2)))
print(lines[start])
addr_idx = {a: i for i, (a, _) in enumerate(body)}
for i, (a, ins) in enumerate(body):
    m = re.match(r"^(j\w+)\s+([0-9a-f]+)", ins)
    if not m:
        continue
    tgt = int(m.group(2), 16)
    if tgt >= a or tgt not in addr_idx:
        continue
    seg = [x for _, x in body[addr_idx[tgt]:i + 1]]
    def cnt(pat):
        return sum(1 for x in seg if re.search(pat, x))
    vst = cnt(r"^vmov[au]p[ds]\s+%[yx]mm\d+,.*\(")
    sst = cnt(r"^vmovs[ds]\s+%xmm\d+,.*\(")
    fma_p = cnt(r"^vfn?m(add|sub)\d+p[ds]")
    fma_s = cnt(r"^vfn?m(add|sub)\d+s[ds]")
    div_p, div_s = cnt(r"^vdivp[ds]"), cnt(r"^vdivs[ds]")
    add_p, add_s = cnt(r"^v(add|sub)p[ds]"), cnt(r"^v(add|sub)s[ds]")
    mul_p, mul_s = cnt(r"^vmulp[ds]"), cnt(r"^vmuls[ds]")
    vimul = cnt(r"^vpmul")
    calls = [re.sub(r".*<(.*)>.*", r"\1", x) for x in seg if x.startswith("call")]
    ymm = cnt(r"%ymm")
    print(f"  loop {tgt:x}..{a:x}: {len(seg)} insns | stores vec {vst} scalar {sst} | "
          f"fma p{fma_p}/s{fma_s} add p{add_p}/s{add_s} mul p{mul_p}/s{mul_s} div p{div_p}/s{div_s} | "
          f"vpmul {vimul} | ymm-insns {ymm} | calls {calls}")
