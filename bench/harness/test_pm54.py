"""Pure PM54 fixtures: no native APIs, process launch, compiler or measured workloads."""
import copy
import dataclasses
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import pm54


class FakeMemory:
    page_size = 4096
    allocation_granularity = 65536
    size_limit = 2**64 - 1

    def __init__(self, *, fail_work=False, fail_release=False, missing_counters=False):
        self.trace, self.snapshots = [], 0
        self.fail_work, self.fail_release, self.missing_counters = fail_work, fail_release, missing_counters
        self.storage = None

    def reserve(self, size):
        self.trace.append(("reserve", size))
        self.storage = bytearray(size)
        return 65536

    def commit(self, address, size):
        self.trace.append(("commit", address, size))

    def dense_store_and_sum(self, address, size):
        self.trace.append(("dense", address, size))
        if self.fail_work:
            raise RuntimeError("injected work failure")
        self.storage[:size] = b"\x01" * size
        return sum(self.storage[:size])

    def sparse_store_and_sum(self, address, size, stride):
        self.trace.append(("sparse", address, size, stride))
        for offset in range(0, size, stride):
            self.storage[offset] = 1
        return sum(self.storage[offset] for offset in range(0, size, stride))

    def release(self, address):
        self.trace.append(("release", address))
        self.storage = None
        return not self.fail_release

    def snapshot(self):
        self.snapshots += 1
        self.trace.append(("snapshot", self.snapshots))
        if self.missing_counters:
            return dict.fromkeys(pm54.COUNTERS)
        # Deliberately HIGHER current footprint after release. No decline is promised.
        return {"current_resident_bytes": 100 + self.snapshots,
                "current_commit_bytes": 200 + self.snapshots,
                "peak_resident_bytes": 1000 + self.snapshots,
                "peak_commit_bytes": 2000 + self.snapshots, "page_fault_count": self.snapshots}


class PM54Tests(unittest.TestCase):
    def test_page_rounding_boundaries_and_checked_overflow(self):
        for n, rounded in ((1, 4096), (4096, 4096), (4097, 8192)):
            self.assertEqual(pm54.plan("dense", n, 4096, 2**32-1)["page_rounded_reservation_bytes"], rounded)
        for n, page, limit in ((0, 4096, 2**32-1), (True, 4096, 2**32-1),
                               (1, 0, 2**32-1), (2**32-1, 4096, 2**32-1)):
            with self.assertRaises(ValueError):
                pm54.plan("dense", n, page, limit)

    def test_actual_page_size_and_sparse_stride_distinguish_stores_from_pages(self):
        n = 40 * 4096 + 3
        dense = pm54.plan("dense", n, 4096, 2**64-1)
        sparse = pm54.plan("sparse", n, 4096, 2**64-1)
        self.assertEqual(dense["deliberate_byte_stores"], n)
        self.assertEqual(dense["distinct_deliberately_touched_pages"], 41)
        self.assertEqual(sparse["deliberate_byte_stores"], 3)
        self.assertEqual(sparse["distinct_deliberately_touched_pages"], 3)
        self.assertEqual(dense["commit_request_bytes"], sparse["commit_request_bytes"])
        other_os = pm54.plan("sparse", 32769, 16384, 2**64-1)
        self.assertEqual(other_os["page_rounded_reservation_bytes"], 49152)
        self.assertEqual(other_os["sparse_stride_bytes"], 17 * 16384)

    def test_reserve_only_never_commits_reads_or_scores_dense_work(self):
        api = FakeMemory()
        record = pm54.exercise(api, "reserve-only", 8195)
        self.assertEqual(pm54.validate(record, "reserve-only", 8195), [])
        self.assertEqual(record["byte_sum"], 0)
        self.assertFalse(record["dense_work_completed"])
        self.assertEqual(record["geometry"]["commit_request_bytes"], 0)
        self.assertEqual(record["geometry"]["checksum_bytes_read"], 0)
        self.assertFalse(any(t[0] in ("commit", "sparse", "dense") for t in api.trace))
        self.assertEqual([s["action"] for s in record["stages"]],
                         ["observed", "observed", "skipped", "skipped", "observed"])

    def test_dense_and_sparse_checksum_only_assigned_bytes_then_release(self):
        for arm in ("dense", "sparse"):
            api = FakeMemory()
            record = pm54.exercise(api, arm, 8195)
            self.assertEqual(pm54.validate(record, arm, 8195), [])
            self.assertEqual(record["byte_sum"], 8195 if arm == "dense" else 1)
            self.assertEqual(record["dense_work_completed"], arm == "dense")
            self.assertLess(next(i for i, t in enumerate(api.trace) if t[0] == arm),
                            next(i for i, t in enumerate(api.trace) if t[0] == "release"))
            self.assertIsNone(api.storage)

    def test_failed_readback_still_releases_and_cannot_pass(self):
        api = FakeMemory(fail_work=True)
        record = pm54.exercise(api, "dense", 8195)
        self.assertFalse(record["ok"])
        self.assertFalse(record["dense_work_completed"])
        self.assertEqual(record["release"], {"attempted": True, "success": True})
        self.assertTrue(pm54.validate(record, "dense", 8195))

    def test_release_failure_is_separate_from_completed_work(self):
        record = pm54.exercise(FakeMemory(fail_release=True), "dense", 8195)
        self.assertTrue(record["dense_work_completed"])
        self.assertFalse(record["ok"])
        self.assertFalse(record["release"]["success"])
        self.assertTrue(pm54.validate(record, "dense", 8195))

    def test_missing_counters_are_null_and_not_a_fabricated_zero(self):
        record = pm54.exercise(FakeMemory(missing_counters=True), "dense", 8195)
        self.assertEqual(pm54.validate(record, "dense", 8195), [])
        self.assertTrue(all(v is None for s in record["stages"] for v in s["counters"].values()))
        self.assertIn("unmeasured", pm54.render([{"arm": "dense", "status": "validated-diagnostic", "diagnostic": record}]))

    def test_release_does_not_reset_peaks_or_require_resident_decline(self):
        record = pm54.exercise(FakeMemory(), "sparse", 8195)
        self.assertGreater(record["stages"][-1]["counters"]["current_resident_bytes"],
                           record["stages"][-2]["counters"]["current_resident_bytes"])
        self.assertEqual(pm54.validate(record, "sparse", 8195), [])
        record["stages"][-1]["counters"]["peak_commit_bytes"] = 500
        self.assertIn("cumulative/peak counter decreased", pm54.validate(record, "sparse", 8195))

    def test_dishonest_dense_completion_geometry_checksum_or_missing_stage_reject(self):
        good = pm54.exercise(FakeMemory(), "reserve-only", 8195)
        mutations = [lambda r: r.update(dense_work_completed=True),
                     lambda r: r["geometry"].update(deliberate_byte_stores=8195),
                     lambda r: r.update(byte_sum=8195), lambda r: r["stages"].pop()]
        for mutation in mutations:
            record = copy.deepcopy(good)
            mutation(record)
            self.assertTrue(pm54.validate(record, "reserve-only", 8195))

    def test_malformed_sidecars_fail_and_failed_run_report_keeps_diagnostic_label(self):
        for record in ({}, [], None, {"schema": 2}):
            self.assertTrue(pm54.validate(record, "dense", 8195))
        text = pm54.render([{"arm": "dense", "status": "failed", "diagnostic": None, "reason": "watchdog timeout"}])
        self.assertIn("no equivalence or performance ratios", text)
        self.assertIn("watchdog timeout", text)

    def test_parent_uses_absolute_paths_and_does_not_replace_missing_process_metrics_with_stage_values(self):
        @dataclasses.dataclass
        class Sample:
            arm: str
            kind: str
            ok: bool
            reason: str
            checksum: int | None
            wall_s: float
            returncode: int
            stderr_tail: str
            peak_working_set: int | None = None
            peak_commit_bytes: int | None = None
            page_faults: int | None = None

        def captured_process(command, *, env, cwd, timeout):
            self.assertTrue(Path(command[3]).is_absolute())
            self.assertTrue(cwd.is_absolute())
            output = Path(command[command.index("--output") + 1])
            self.assertTrue(output.is_absolute())
            arm = command[command.index("--arm") + 1]
            output.write_text(json.dumps(pm54.exercise(FakeMemory(), arm, 8195)), encoding="utf-8")
            return SimpleNamespace(launch_error=None, timed_out=False, returncode=0,
                                   stdout="", stderr="", wall_s=0.1, memory=None)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            harness_dir = Path(__file__).resolve().parent
            h = SimpleNamespace(HarnessError=ValueError, BUILD_ROOT=root, BENCH_DIR=harness_dir.parent,
                                HARNESS_DIR=harness_dir, HARNESS_VERSION="fixture", sha256_file=lambda path: "fixture-sha256",
                                memory_coverage=lambda: {}, run_proc=captured_process, make_env=lambda knobs: {},
                                Sample=Sample, tail=lambda text: text, summarize_memory=lambda samples: {"fixture_n": len(samples)},
                                write_json_atomic=lambda path, value: path.write_text(json.dumps(value), encoding="utf-8"),
                                render_memory_report=lambda data: "fixture process report")
            self.assertEqual(pm54.run_diagnostic(h, SimpleNamespace(requested=8195, out_dir=root, run_timeout=30)), 0)
            data = json.loads(next(root.glob("session-*/session.json")).read_text(encoding="utf-8"))
        for entry in data["blocks"][0]["arms"].values():
            self.assertIsNone(entry["samples"][0]["peak_commit_bytes"])
            self.assertGreater(entry["diagnostic"]["stages"][0]["counters"]["peak_commit_bytes"], 0)


if __name__ == "__main__":
    unittest.main()
