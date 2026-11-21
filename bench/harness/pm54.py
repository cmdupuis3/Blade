"""PM54 portable contract, protocol and reporting. Native calls live in pm54_win32.py."""
from __future__ import annotations

ARMS = ("reserve-only", "sparse", "dense")
STAGES = ("baseline", "reserved", "committed", "touched", "released")
COUNTERS = ("current_resident_bytes", "current_commit_bytes", "peak_resident_bytes",
            "peak_commit_bytes", "page_fault_count")
CONTRACT = "PM54-page-touch-release-v1"


def plan(arm: str, requested: int, page_size: int, size_limit: int, stride_pages: int = 17) -> dict:
    """Checked SIZE_T geometry; stored bytes and distinct touched pages are different."""
    if arm not in ARMS:
        raise ValueError("unknown PM54 workload")
    if any(type(v) is not int or v <= 0 for v in (requested, page_size, size_limit, stride_pages)):
        raise ValueError("positive integer byte/page geometry required")
    pages = (requested - 1) // page_size + 1
    if requested > size_limit or pages > size_limit // page_size or stride_pages > size_limit // page_size:
        raise ValueError("page-rounded size or stride exceeds SIZE_T")
    rounded, stride = pages * page_size, stride_pages * page_size
    stores = requested if arm == "dense" else (requested - 1) // stride + 1 if arm == "sparse" else 0
    return {"requested_bytes": requested, "page_size_bytes": page_size,
            "page_rounded_reservation_bytes": rounded, "size_t_limit": size_limit,
            "commit_request_bytes": 0 if arm == "reserve-only" else rounded,
            "sparse_stride_pages": stride_pages, "sparse_stride_bytes": stride,
            "deliberate_byte_stores": stores, "distinct_deliberately_touched_pages":
                pages if arm == "dense" else stores,
            "checksum_bytes_read": stores, "expected_byte_sum": stores}


def exercise(api, arm: str, requested: int) -> dict:
    """One fresh-child workload; checksum is read before the explicit release boundary."""
    geometry = plan(arm, requested, api.page_size, api.size_limit)
    result = {"schema": 1, "contract": CONTRACT, "arm": arm, "geometry": geometry,
              "allocation_granularity_bytes": api.allocation_granularity,
              "stages": [], "byte_sum": None, "work_success": False,
              "dense_work_completed": False, "release": {"attempted": False, "success": None},
              "ok": False, "error": None}
    address = None

    def snapshot(stage, skipped=False):
        result["stages"].append({"stage": stage, "action": "skipped" if skipped else "observed",
                                 "counters": api.snapshot()})

    try:
        snapshot("baseline")
        address = api.reserve(geometry["page_rounded_reservation_bytes"])
        if type(address) is not int or address <= 0 or address > api.size_limit - geometry["page_rounded_reservation_bytes"] + 1:
            raise ValueError("reservation address/range invalid")
        snapshot("reserved")
        if arm != "reserve-only":
            api.commit(address, geometry["commit_request_bytes"])
        snapshot("committed", arm == "reserve-only")
        if arm == "dense":
            checksum = api.dense_store_and_sum(address, requested)
        elif arm == "sparse":
            checksum = api.sparse_store_and_sum(address, requested, geometry["sparse_stride_bytes"])
        else:
            checksum = 0             # empty observation; no reservation bytes are read
        result["byte_sum"] = checksum
        result["work_success"] = type(checksum) is int and checksum == geometry["expected_byte_sum"]
        result["dense_work_completed"] = arm == "dense" and result["work_success"]
        snapshot("touched", arm == "reserve-only")
    except Exception as error:
        result["error"] = f"{type(error).__name__}: {error}"
    finally:
        if address:
            result["release"]["attempted"] = True
            try:
                result["release"]["success"] = bool(api.release(address))
                if not result["release"]["success"]:
                    result["error"] = result["error"] or "VirtualFree(MEM_RELEASE) failed"
            except Exception as error:
                result["release"]["success"] = False
                result["error"] = result["error"] or f"release: {error}"
            try:
                snapshot("released")
            except Exception as error:
                result["error"] = result["error"] or f"released snapshot: {error}"
    result["ok"] = result["work_success"] and result["release"]["success"] is True and result["error"] is None
    return result


def validate(record: dict, arm: str, requested: int) -> list[str]:
    """Independently check the sidecar; current footprint need not decline after release."""
    errors = []
    try:
        if record["schema"] != 1 or record["contract"] != CONTRACT or record["arm"] != arm:
            errors.append("diagnostic identity mismatch")
        geometry = record["geometry"]
        expected = plan(arm, requested, geometry["page_size_bytes"], geometry["size_t_limit"])
        granularity = record["allocation_granularity_bytes"]
        if type(granularity) is not int or granularity < expected["page_size_bytes"] or granularity % expected["page_size_bytes"]:
            errors.append("invalid OS allocation granularity")
        if geometry != expected:
            errors.append("geometry/store/read coverage differs from contract")
        if (not record["ok"] or not record["work_success"] or record["error"] is not None
                or record["release"] != {"attempted": True, "success": True}):
            errors.append("work or explicit release failed")
        if type(record["byte_sum"]) is not int or record["byte_sum"] != expected["expected_byte_sum"]:
            errors.append("observed-byte checksum mismatch")
        if record["dense_work_completed"] is not (arm == "dense"):
            errors.append("non-dense workload claimed dense completion")
        stages = record["stages"]
        if [entry["stage"] for entry in stages] != list(STAGES):
            errors.append("missing or reordered stage snapshot")
        previous = {}
        for entry in stages:
            skipped = arm == "reserve-only" and entry["stage"] in ("committed", "touched")
            if entry["action"] != ("skipped" if skipped else "observed"):
                errors.append("stage action mismatch")
            counters = entry["counters"]
            for key in COUNTERS:
                value = counters[key]
                if value is not None and (type(value) is not int or value < 0):
                    errors.append("invalid counter (missing is null, never zero-filled)")
                if value is not None and key.startswith(("peak_", "page_fault")):
                    if key in previous and value < previous[key]:
                        errors.append("cumulative/peak counter decreased")
                    previous[key] = value
            for current, peak in (("current_resident_bytes", "peak_resident_bytes"),
                                  ("current_commit_bytes", "peak_commit_bytes")):
                if counters[current] is not None and counters[peak] is not None and counters[current] > counters[peak]:
                    errors.append("current counter exceeds its process high water")
    except (KeyError, TypeError, ValueError) as error:
        errors.append(f"invalid diagnostic sidecar: {error}")
    return list(dict.fromkeys(errors))


def render(records: list[dict]) -> str:
    lines = ["# PM54 page touch/release diagnostic", "",
             "Distinct workloads, **no equivalence or performance ratios**. Only dense assigns and "
             "observes every requested byte. Reserve-only performs no dense work.", "",
             "Stage counters include Python, metadata and checksum overhead. Current working set/commit "
             "are snapshots, not peaks. Peak resident/commit and faults are lifetime/cumulative counters; "
             "they remain high after release. Release success is API evidence, not a promise of an "
             "immediate resident-memory decrease. No allocator live/retained-byte or DRAM inference.", "",
             "Independent parent process peaks and available/missing-counter distributions: [memory.md](memory.md). "
             "Raw sidecars, geometry, source identities and validation: session.json and each child directory."]
    for entry in records:
        arm, record = entry["arm"], entry.get("diagnostic")
        lines += ["", f"## {arm}: {entry['status']}", ""]
        if not isinstance(record, dict) or not record:
            lines.append(entry.get("reason", "No child sidecar; unmeasured."))
            continue
        lines += [f"- Geometry/coverage: `{record.get('geometry')}`.",
                  f"- OS allocation-address granularity: {record.get('allocation_granularity_bytes')} bytes.",
                  f"- Work success: {record.get('work_success')}; dense work completed: "
                  f"{record.get('dense_work_completed')}; observed byte sum: {record.get('byte_sum')}.",
                  f"- Release: `{record.get('release')}`; error: {record.get('error')}.",
                  "", "| stage | action | current resident bytes | current commit bytes | peak resident bytes | "
                  "peak commit bytes | cumulative faults |", "|---|---|---|---|---|---|---|"]
        stages = record.get("stages", [])
        for stage in (stages if isinstance(stages, list) else []):
            if not isinstance(stage, dict):
                continue
            counters = stage.get("counters", {})
            counters = counters if isinstance(counters, dict) else {}
            values = ["unmeasured" if counters.get(key) is None else str(counters[key]) for key in COUNTERS]
            lines.append("| " + " | ".join([stage.get("stage", "?"), stage.get("action", "?"), *values]) + " |")
        if entry.get("reason"):
            lines.append("\nValidation: " + entry["reason"])
    return "\n".join(lines) + "\n"


def run_diagnostic(h, args) -> int:
    """Reuse the benchmark parent's process capture/report APIs; never enter its ratios."""
    import dataclasses
    import datetime as dt
    import json
    import platform
    import sys
    from pathlib import Path

    if type(args.requested) is not int or not 1 <= args.requested <= 256_000_003:
        raise h.HarnessError("PM54 --bytes must be 1..256000003 (diagnostic ceiling, not a benchmark size policy)")
    if not 0 < args.run_timeout <= 60:
        raise h.HarnessError("PM54 --run-timeout must be positive and at most 60 s")
    started = dt.datetime.now()
    root = args.out_dir.resolve() if args.out_dir else (h.BUILD_ROOT / "pm54").resolve()
    bundle = root / f"session-{started:%Y%m%d-%H%M%S-%f}"
    bundle.mkdir(parents=True, exist_ok=False)
    child = Path(__file__).with_name("pm54_win32.py").resolve()
    python = Path(sys.executable).resolve()
    sources = {str(path.relative_to(h.BENCH_DIR)): h.sha256_file(path)
               for path in (Path(__file__).resolve(), child, h.HARNESS_DIR / "run.py", h.BENCH_DIR / "diagnostics/pm54/spec.md")}
    contract = {"id": CONTRACT, "source": "bench/diagnostics/pm54/spec.md",
                "required_values": "dense full assigned byte buffer; sparse probe bytes; reserve-only no output bytes; distinct tasks",
                "observation_boundary": "assigned bytes read into byte_sum before release; reserve-only reads no region bytes",
                "cleanup_endpoint": "VirtualFree(original_base, 0, MEM_RELEASE) before released snapshot and child exit",
                "audit_status": "API/control-flow diagnostic only; no Blade/Fortran store, allocator or lifetime-parity claim"}
    block = {"kernel": "pm54", "regime": "diagnostic-distinct-workloads", "size": "requested",
             "sizes": {"bytes": args.requested}, "memory_contract": contract, "arms": {}}
    data = {"schema": 2, "diagnostic_schema": 1, "harness_version": h.HARNESS_VERSION,
            "mode": "diagnostic", "started": started.isoformat(), "size": "requested",
            "machine": {"host": platform.node(), "platform": platform.platform()},
            "python": {"executable": str(python), "version": sys.version,
                       "sha256": h.sha256_file(python)},
            "source_root": str(h.BENCH_DIR.resolve()), "source_sha256": sources, "memory_report": h.memory_coverage(),
            "blocks": [block], "controls": [], "failures": [],
            "coverage": "Win32 private VirtualAlloc region only; snapshots/process peaks include Python and diagnostic overhead; no live/retained allocator bytes or DRAM counters"}
    data["memory_report"]["primary_population"] = "successful_contracts (distinct diagnostic workloads)"
    entries = []
    for arm in ARMS:
        directory = bundle / arm
        directory.mkdir()
        sidecar = directory / "child.json"
        command = [str(python), "-E", "-s", str(child), "--arm", arm,
                   "--bytes", str(args.requested), "--output", str(sidecar)]
        result = h.run_proc(command, env=h.make_env({"OMP_NUM_THREADS": "1"}),
                            cwd=directory, timeout=args.run_timeout)
        problems = []
        record = None
        if result.launch_error or result.timed_out or result.returncode != 0:
            problems.append(result.launch_error or ("watchdog timeout" if result.timed_out else f"exit {result.returncode}"))
        if result.stdout.strip():
            problems.append("unexpected child stdout; diagnostic contract is sidecar-only")
        try:
            record = json.loads(sidecar.read_text(encoding="utf-8"))
            problems.extend(validate(record, arm, args.requested))
            if not isinstance(record, dict):
                record = None
        except (OSError, ValueError) as error:
            problems.append(f"missing/invalid sidecar: {error}")
        sample = h.Sample(arm=arm, kind="contract", ok=not problems, reason="; ".join(problems),
                          checksum=record.get("byte_sum") if isinstance(record, dict) else None,
                          wall_s=result.wall_s, returncode=result.returncode, stderr_tail=h.tail(result.stderr))
        if result.memory:
            sample.peak_working_set = result.memory.get("peak_working_set")
            sample.peak_commit_bytes = result.memory.get("peak_pagefile")
            sample.page_faults = result.memory.get("page_faults")
        entry = {"arm": arm, "status": "validated-diagnostic" if sample.ok else "failed",
                 "reason": sample.reason, "diagnostic": record, "command": command,
                 "samples": [dataclasses.asdict(sample)]}
        entry["memory_report"] = h.summarize_memory(entry["samples"])
        entries.append(entry)
        block["arms"][arm] = entry
        if problems:
            data["failures"].append(f"{arm}: {sample.reason}")
        h.write_json_atomic(bundle / "session.json", data)
    (bundle / "memory.md").write_text(h.render_memory_report(data), encoding="utf-8")
    (bundle / "diagnostic.md").write_text(render(entries), encoding="utf-8")
    print(f"PM54 distinct-workload diagnostic: {len(entries) - len(data['failures'])}/3 validated; {bundle}")
    return 1 if data["failures"] else 0
