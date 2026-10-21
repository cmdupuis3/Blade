"""Resolve the W2-1 squash-merge conflicts on the integration worktree: every hunk is additive on both sides
(dry run mt-w21.txt), so keep HEAD's lines then THEIRS'; the gap register's rows are ordered by G number; Lowering.fs
loses the stale 'semijoin hash-set is a separate, not-yet-implemented optimization' comment. Fails if a file still
holds a marker, or if an unexpected file conflicts."""
import subprocess, re, sys, pathlib
WT = pathlib.Path(r"C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran")
EXPECTED = {"bench/notes/gap-register.md", "src/Lowering.fs", "src/Optimize.fs", "src/cpp/blade_run_record.hpp"}
STALE = [b"        // mask+contains fusion always runs a linear scan; the semijoin",
         b"        // hash-set is a separate, not-yet-implemented optimization."]

unmerged = subprocess.run(["git", "-C", str(WT), "diff", "--name-only", "--diff-filter=U"], capture_output=True, text=True).stdout.split()
extra = set(unmerged) - EXPECTED
if extra:
    sys.exit(f"unexpected conflicts: {sorted(extra)} -- resolve by hand")
print("unmerged:", unmerged)

def key_of(line):
    m = re.match(rb"^\| (G\d+) \|", line)
    return int(m.group(1)[1:]) if m else None

for rel in unmerged:
    p = WT / rel
    raw = p.read_bytes()
    nl = b"\r\n" if b"\r\n" in raw else b"\n"
    lines = raw.split(nl)
    out, state, head, theirs, hunks = [], None, [], [], 0
    for l in lines:
        if l.startswith(b"<<<<<<< "):
            state, head, theirs = "head", [], []; continue
        if l == b"=======" and state == "head":
            state = "theirs"; continue
        if l.startswith(b">>>>>>> ") and state == "theirs":
            merged = head + theirs
            if rel.endswith("gap-register.md"):
                assert all(key_of(x) for x in merged), "non-row line in a gap-register hunk"
                merged = sorted(merged, key=key_of)
                print(f"  {rel}: hunk rows -> {[key_of(x) for x in merged]}")
            else:
                print(f"  {rel}: hunk kept both ({len(head)} + {len(theirs)} lines)")
            out.extend(merged); hunks += 1; state = None; continue
        (head if state == "head" else theirs if state == "theirs" else out).append(l)
    assert state is None and hunks >= 1, rel
    if rel == "src/Lowering.fs":
        for s in STALE:
            if s in out:
                out.remove(s); print("  Lowering.fs: dropped stale comment line")
    p.write_bytes(nl.join(out))
    t = p.read_bytes()
    assert b"<<<<<<<" not in t and b">>>>>>>" not in t and b"\n=======\n" not in t.replace(b"\r\n", b"\n"), rel
subprocess.run(["git", "-C", str(WT), "add"] + unmerged, check=True)
print("resolved and staged:", unmerged)
