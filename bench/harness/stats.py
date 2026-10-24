"""The bench's ONE statistics table: ``bench/results/stats.csv``.

One row per (session, kernel, regime, size, arm). Every number the harness measures that a record has ever quoted is a
column -- the arm's sample statistics, its peak working set, its ratio against the Blade reference with the bootstrap CI and
verdict, the block's control medians and class, the build's identity. Narrative records (RESULTS.md) cite rows by
``session`` + ``label``; nothing is re-derived by hand into a markdown table again.

    python bench/harness/stats.py ingest [ROOT ...]      walk ROOT/**/session.json (default: bench/results and the
                                                         A/B mirrors under C:\\bt_r2ab), append the rows not yet present
    python bench/harness/stats.py ingest-bundle DIR      one bundle (what run.py calls at the end of a session)
    python bench/harness/stats.py sql "SELECT ..."       the CSV loaded into an in-memory SQLite table ``stats``
    python bench/harness/stats.py summary [--kernel K] [--regime R] [--label L] [--size S]
                                                         medians per (label, kernel, regime, size, arm), latest session per label

Rows are keyed by (bundle, kernel, regime, size, arm); re-ingesting is idempotent. The ``label`` is the A/B mirror the
bundle came from (the directory under ``C:\\bt_r2ab``, e.g. ``r3w3``) or ``repo`` for a bundle under ``bench/results``.
"""
from __future__ import annotations

import argparse
import csv
import json
import sqlite3
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
BENCH = HERE.parent
CSV = BENCH / "results" / "stats.csv"
DEFAULT_ROOTS = [BENCH / "results", Path(r"C:\bt_r2ab")]

COLUMNS = [
    "session", "label", "bundle", "host", "git_sha", "blade_dll_sha", "harness", "mode", "size_mode",
    "kernel", "group", "regime", "size", "sizes", "arm", "lane", "status", "reason",
    "n", "min_s", "median_s", "q1_s", "q3_s", "iqr_s", "mean_s", "max_s", "trend", "peak_ws_mb",
    "ratio_ref", "ratio", "ci_lo", "ci_hi", "verdict",
    "block_class", "block_flags", "ctl_compute_median_s", "ctl_memory_median_s", "rounds",
    "fold_form", "parallelism", "exe_sha", "build_wall_s", "checksum", "note",
]
KEY = ("bundle", "kernel", "regime", "size", "arm")


def _label_for(bundle: Path) -> str:
    parts = [p.lower() for p in bundle.parts]
    if "bt_r2ab" in parts:
        return bundle.parts[parts.index("bt_r2ab") + 1]
    return "repo"


def _bundle_id(bundle: Path) -> str:
    # <date-host>/<session-id>, the same spelling the records use
    return f"{bundle.parent.name}/{bundle.name}"


def _get(d, *keys, default=""):
    for k in keys:
        if not isinstance(d, dict) or k not in d:
            return default
        d = d[k]
    return default if d is None else d


def _evidence(build: dict, key: str) -> str:
    ev = build.get("evidence") if isinstance(build, dict) else None
    if isinstance(ev, dict):
        v = ev.get(key)
        if isinstance(v, (list, tuple)):
            return "; ".join(str(x) for x in v)
        return "" if v is None else str(v)
    return ""


def rows_of(session_path: Path) -> list[dict]:
    d = json.loads(session_path.read_text(encoding="utf-8-sig"))
    bundle = session_path.parent
    base = {
        "session": d.get("started", ""),
        "label": _label_for(bundle),
        "bundle": _bundle_id(bundle),
        "host": _get(d, "machine", "host"),
        "git_sha": str(_get(d, "machine", "git_sha"))[:12],
        "blade_dll_sha": str(_get(d, "toolchain", "blade", "dll_sha256"))[:16],
        "harness": d.get("harness_version", ""),
        "mode": d.get("mode", ""),
        "size_mode": d.get("size", ""),
    }
    out = []
    for b in d.get("blocks", []) or []:
        flags = b.get("flags") or []
        if isinstance(flags, dict):
            flags = [f"{k}: {v}" for k, v in flags.items()]
        block_class = "indicative" if (b.get("suppressed") or flags) else "live"
        ctl = b.get("controls") or {}
        for arm, a in (b.get("arms") or {}).items():
            st = a.get("stats") or {}
            ratio = a.get("ratio") or {}
            ci = ratio.get("ci95") or [None, None]
            if isinstance(ci, dict):
                ci = [ci.get("lo", ci.get("low")), ci.get("hi", ci.get("high"))]
            build = a.get("build") or {}
            pws = a.get("peak_working_set_max")
            samples = a.get("samples") or []
            checksum = ""
            for s in samples:
                if s.get("kind") != "warmup" and s.get("ok"):
                    checksum = s.get("checksum_text", "") or str(s.get("checksum", ""))
                    break
            row = dict(base)
            row.update({
                "kernel": b.get("kernel", ""), "group": b.get("group", ""), "regime": b.get("regime", ""),
                "size": b.get("size", ""), "sizes": json.dumps(b.get("sizes") or {}, separators=(",", ":")),
                "arm": arm, "lane": build.get("lane") or "", "status": a.get("status", ""), "reason": a.get("reason", ""),
                "n": st.get("n", ""), "min_s": st.get("min", ""), "median_s": st.get("median", ""),
                "q1_s": st.get("q1", ""), "q3_s": st.get("q3", ""), "iqr_s": st.get("iqr", ""),
                "mean_s": st.get("mean", ""), "max_s": st.get("max", ""), "trend": a.get("trend", ""),
                "peak_ws_mb": round(pws / 1048576, 2) if isinstance(pws, (int, float)) else "",
                "ratio_ref": b.get("ratio_reference", ""), "ratio": ratio.get("ratio", ""),
                "ci_lo": ci[0] if ci and ci[0] is not None else "", "ci_hi": ci[1] if ci and len(ci) > 1 and ci[1] is not None else "",
                "verdict": ratio.get("verdict", ""),
                "block_class": block_class, "block_flags": " | ".join(str(f) for f in flags),
                "ctl_compute_median_s": _get(ctl, "compute", "median"), "ctl_memory_median_s": _get(ctl, "memory", "median"),
                "rounds": _get(b, "rounds", "n"),
                "fold_form": _evidence(build, "fold_form"), "parallelism": _evidence(build, "parallelism"),
                "exe_sha": str(build.get("exe_sha256", ""))[:16], "build_wall_s": build.get("build_wall_s", ""),
                "checksum": checksum, "note": a.get("note", ""),
            })
            out.append(row)
    return out


def load() -> list[dict]:
    if not CSV.exists():
        return []
    with CSV.open(encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def save(rows: list[dict]) -> None:
    rows.sort(key=lambda r: (r["session"], r["label"], r["kernel"], r["regime"], r["size"], r["arm"]))
    CSV.parent.mkdir(parents=True, exist_ok=True)
    with CSV.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=COLUMNS, lineterminator="\n")
        w.writeheader()
        for r in rows:
            w.writerow({c: ("" if r.get(c) is None else r.get(c, "")) for c in COLUMNS})


def ingest(paths: list[Path]) -> int:
    rows = load()
    have = {tuple(str(r[k]) for k in KEY) for r in rows}
    added = 0
    for p in paths:
        try:
            new = rows_of(p)
        except Exception as e:  # a half-written checkpoint: skip, say so
            print(f"skip {p}: {e}", file=sys.stderr)
            continue
        for r in new:
            k = tuple(str(r[c]) for c in KEY)
            if k in have:
                continue
            have.add(k)
            rows.append(r)
            added += 1
    save(rows)
    return added


def find_sessions(roots: list[Path]) -> list[Path]:
    out = []
    for root in roots:
        if root.exists():
            out.extend(sorted(root.rglob("session.json")))
    return out


def cmd_sql(query: str) -> None:
    rows = load()
    con = sqlite3.connect(":memory:")
    numeric = {"n", "min_s", "median_s", "q1_s", "q3_s", "iqr_s", "mean_s", "max_s", "trend", "peak_ws_mb", "ratio",
               "ci_lo", "ci_hi", "ctl_compute_median_s", "ctl_memory_median_s", "rounds", "build_wall_s"}
    cols = ", ".join(f'"{c}" {"REAL" if c in numeric else "TEXT"}' for c in COLUMNS)
    con.execute(f"CREATE TABLE stats ({cols})")
    def conv(c, v):
        if c in numeric:
            try:
                return float(v) if v not in ("", None) else None
            except ValueError:
                return None
        return v
    con.executemany(f"INSERT INTO stats VALUES ({', '.join('?' * len(COLUMNS))})",
                    [[conv(c, r.get(c, "")) for c in COLUMNS] for r in rows])
    cur = con.execute(query)
    names = [d[0] for d in cur.description]
    print("\t".join(names))
    for row in cur.fetchall():
        print("\t".join("" if v is None else (f"{v:.6g}" if isinstance(v, float) else str(v)) for v in row))


def cmd_summary(kernel: str | None, regime: str | None, label: str | None, size: str | None) -> None:
    where = ["status = 'ok'"]
    if size: where.append(f"size = '{size}'")
    if kernel: where.append(f"kernel = '{kernel}'")
    if regime: where.append(f"regime = '{regime}'")
    if label: where.append(f"label = '{label}'")
    cmd_sql("SELECT label, kernel, regime, size, arm, COUNT(*) AS sessions, SUM(n) AS samples, "
            "MIN(median_s) AS best_median_s, MAX(session) AS latest, MAX(peak_ws_mb) AS peak_ws_mb "
            f"FROM stats WHERE {' AND '.join(where)} GROUP BY label, kernel, regime, size, arm ORDER BY kernel, regime, size, label, arm")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("ingest"); p.add_argument("roots", nargs="*", type=Path)
    p = sub.add_parser("ingest-bundle"); p.add_argument("bundle", type=Path)
    p = sub.add_parser("sql"); p.add_argument("query")
    p = sub.add_parser("summary"); p.add_argument("--kernel"); p.add_argument("--regime"); p.add_argument("--label"); p.add_argument("--size", default="full")
    a = ap.parse_args(argv)
    if a.cmd == "ingest":
        roots = a.roots or DEFAULT_ROOTS
        sessions = find_sessions(roots)
        n = ingest(sessions)
        print(f"{len(sessions)} session(s) seen, {n} row(s) added -> {CSV} ({len(load())} rows)")
    elif a.cmd == "ingest-bundle":
        n = ingest([a.bundle / "session.json"])
        print(f"{n} row(s) added -> {CSV}")
    elif a.cmd == "sql":
        cmd_sql(a.query)
    elif a.cmd == "summary":
        cmd_summary(a.kernel, a.regime, a.label, a.size)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
