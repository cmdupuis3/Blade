"""Resolve the three W2-3 squash-merge conflicts on the integration worktree.
OmpTests.fs: keep both sides. CLAUDE.md / gap-register.md: HEAD's rows, with THEIRS' insertions (relative to the base
667250cd) re-applied by base-context anchor; rows only THEIRS has are added. Fails loudly on any ambiguity."""
import subprocess, difflib, re, sys, pathlib
WT = pathlib.Path(r"C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran")
BASE, THEIRS = "667250cd", "8f2d2480"

def show(rev, path):
    return subprocess.run(["git", "-C", str(WT), "show", f"{rev}:{path}"], capture_output=True, text=True, encoding="utf8").stdout

def split_conflicts(text):
    """yield (kind, lines): kind in {'text','conflict'}; conflict -> (head_lines, theirs_lines)"""
    lines = text.split("\n")
    out, i, buf = [], 0, []
    while i < len(lines):
        if lines[i].startswith("<<<<<<< "):
            out.append(("text", buf)); buf = []
            j = i + 1; head = []
            while not lines[j].startswith("======="): head.append(lines[j]); j += 1
            j += 1; theirs = []
            while not lines[j].startswith(">>>>>>> "): theirs.append(lines[j]); j += 1
            out.append(("conflict", (head, theirs))); i = j + 1
        else:
            buf.append(lines[i]); i += 1
    out.append(("text", buf))
    return out

def key_of(line):
    m = re.match(r"^\| (`[A-Z_ /`]+`|G\d+) \|", line)
    return m.group(1) if m else None

def apply_theirs_edits(base_row, head_row, theirs_row, label):
    """re-apply THEIRS' edits (vs base) onto HEAD's row, anchored on 48 chars of base context before each edit"""
    sm = difflib.SequenceMatcher(None, base_row, theirs_row, autojunk=False)
    ops = [o for o in sm.get_opcodes() if o[0] != "equal"]
    if not ops:
        return head_row
    out = head_row
    for op, i1, i2, j1, j2 in ops:
        if op not in ("insert", "replace"):
            sys.exit(f"{label}: THEIRS deletes base text ({op} {base_row[i1:i2][:80]!r}); resolve by hand")
        anchor = base_row[max(0, i1 - 48):i1]
        removed = base_row[i1:i2]
        n = out.count(anchor + removed)
        if n != 1:
            sys.exit(f"{label}: anchor found {n} times in HEAD's row: {anchor[-60:]!r}")
        out = out.replace(anchor + removed, anchor + theirs_row[j1:j2], 1)
        print(f"  {label}: applied THEIRS {op} ({len(theirs_row[j1:j2])} chars) after {anchor[-30:]!r}")
    return out

def g44_merge(head_row, theirs_row):
    """W2-0 rewrote G44's tail where W2-3 appended its note; the note also said 'rank 2 mt8 repro stays G35', false after W2-0a."""
    old = "(`prefer` = G35, `0` = no row slots); v2:"
    new = ("(`prefer` = G35, `0` = no row slots); a RANK-3 walk under an outer request takes the row-owned pull ahead of both "
           "(G66: the leaf chains partitioned by H row over the team, bitwise this scatter; pull > scatter > row slots, W2-3; "
           "rank 2 has no pull: the scatter by default, G35 under `prefer`); v2:")
    assert head_row.count(old) == 1, "G44 anchor"
    return head_row.replace(old, new, 1)

def g66_fix(row):
    old = "v2: rank 2 (no pull needed: G35 / the P1 commit)"
    new = "v2: rank 2 (no pull needed: the scatter by default since W2-0a, G35 under `prefer` / the P1 commit)"
    assert row.count(old) == 1, "G66 anchor"
    return row.replace(old, new, 1)

OVERRIDE = {("bench/notes/gap-register.md", "G44"): g44_merge}
POSTFIX = {("bench/notes/gap-register.md", "G66"): g66_fix}

def resolve_rows(path, order_hint):
    base = {key_of(l): l for l in show(BASE, path).split("\n") if key_of(l)}
    text = (WT / path).read_text(encoding="utf8")
    parts = split_conflicts(text)
    res = []
    for kind, payload in parts:
        if kind == "text":
            res.extend(payload); continue
        head, theirs = payload
        hk = {key_of(l): l for l in head if key_of(l)}
        tk = {key_of(l): l for l in theirs if key_of(l)}
        if len(hk) != len(head) or len(tk) != len(theirs):
            sys.exit(f"{path}: a conflict side has non-row lines")
        merged = {}
        for k, hl in hk.items():
            if (path, k) in OVERRIDE:
                merged[k] = OVERRIDE[(path, k)](hl, tk.get(k)); print(f"  {path} {k}: hand merge applied")
            else:
                merged[k] = apply_theirs_edits(base[k], hl, tk[k], f"{path} {k}") if (k in tk and tk[k] != base.get(k)) else hl
        for k, tl in tk.items():
            if k not in merged:
                merged[k] = tl; print(f"  {path}: added THEIRS-only row {k}")
        for k in list(merged):
            if (path, k) in POSTFIX:
                merged[k] = POSTFIX[(path, k)](merged[k]); print(f"  {path} {k}: post-fix applied")
        keys = sorted(merged, key=order_hint(list(hk) + [k for k in tk if k not in hk]))
        res.extend(merged[k] for k in keys)
        print(f"  {path}: hunk resolved as {keys}")
    (WT / path).write_text("\n".join(res), encoding="utf8", newline="\n")

# CLAUDE.md: keep HEAD's row order, THEIRS-only rows after the last HEAD row of the hunk
def claude_order(all_keys):
    pos = {k: i for i, k in enumerate(all_keys)}
    return lambda k: pos[k]
# gap register: numeric by G number
def gap_order(all_keys):
    return lambda k: int(k[1:])

resolve_rows("CLAUDE.md", claude_order)
resolve_rows("bench/notes/gap-register.md", gap_order)

# OmpTests.fs: both sides, HEAD first (CRLF working tree: preserve the file's own endings)
p = WT / "tests/OmpTests.fs"
raw = p.read_bytes()
nl = b"\r\n" if b"\r\n" in raw else b"\n"
lines = raw.split(nl)
out, state = [], None
for l in lines:
    if l.startswith(b"<<<<<<< "): state = "head"; continue
    if l == b"=======" and state == "head": state = "theirs"; continue
    if l.startswith(b">>>>>>> ") and state == "theirs": state = None; continue
    out.append(l)
p.write_bytes(nl.join(out))
print("  OmpTests.fs: both sides kept")
for f in ["CLAUDE.md", "bench/notes/gap-register.md", "tests/OmpTests.fs"]:
    t = (WT / f).read_bytes()
    assert b"<<<<<<<" not in t and b">>>>>>>" not in t, f
print("no markers remain")
