"""B01 focused fixtures. Runner only: python -m unittest discover -s bench/harness -p test_memory.py.

No compiler, child executable or benchmark is invoked. Win32 capture is mocked; the
driver itself is Windows-only, as documented by the benchmark protocol.
"""
import dataclasses
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import run as harness


def sample(kind="round", *, ok=True, resident=100, commit=200, faults=3, dropped=False):
    return dataclasses.asdict(harness.Sample(
        arm="blade-cpp", kind=kind, ok=ok, time_s=0.01,
        peak_working_set=resident, peak_commit_bytes=commit, page_faults=faults,
        excursion_dropped=dropped))


class MemoryReportTests(unittest.TestCase):
    def population(self, samples, name="successful_rounds"):
        return harness.summarize_memory(samples)["populations"][name]

    def test_populations_include_timing_exclusion_but_separate_failed_and_warmup(self):
        runs = [sample(resident=100), sample(resident=300, dropped=True),
                sample("warmup", resident=800), sample(ok=False, resident=900),
                sample("contract", resident=700), sample("control", resident=600)]
        report = harness.summarize_memory(runs)["populations"]
        resident = report["successful_rounds"]["counters"]["process_peak_resident_bytes"]
        self.assertEqual(resident["distribution"]["median"], 200)
        self.assertEqual(resident["distribution"]["max"], 300)
        self.assertEqual(report["successful_rounds"]["sample_count"], 2)
        for name in ("successful_warmups", "successful_contracts", "successful_controls",
                     "successful_timing_excluded", "failed_runs"):
            self.assertEqual(report[name]["sample_count"], 1)

    def test_missing_and_null_are_unmeasured_and_zero_is_observed(self):
        legacy = {"kind": "round", "ok": True, "peak_working_set": None}
        pop = self.population([legacy, sample(resident=0, commit=None, faults=0)])
        resident = pop["counters"]["process_peak_resident_bytes"]
        self.assertEqual(resident["status"], "partial")
        self.assertEqual(resident["missing_runs"], 1)
        self.assertEqual(resident["distribution"]["median"], 0)
        commit = pop["counters"]["process_peak_commit_bytes"]
        self.assertEqual(commit["status"], "unmeasured")
        self.assertEqual(commit["missing_runs"], 2)
        self.assertEqual(commit["distribution"], {"n": 0})

    def test_empty_and_failed_only_do_not_become_zero_peaks(self):
        for runs in ([], [sample(ok=False)]):
            pop = self.population(runs)
            self.assertEqual(pop["sample_count"], 0)
            for counter in pop["counters"].values():
                self.assertEqual(counter["status"], "unmeasured")
                self.assertEqual(counter["distribution"], {"n": 0})

    def test_commit_and_resident_are_independent_not_capacity_or_retention(self):
        runs = [sample(resident=100, commit=200), sample(resident=100, commit=900)]
        pop = self.population(runs)
        self.assertEqual(pop["counters"]["process_peak_resident_bytes"]["distribution"]["iqr"], 0)
        self.assertEqual(pop["counters"]["process_peak_commit_bytes"]["distribution"]["max"], 900)
        coverage = harness.memory_coverage()
        for name in ("live_allocation_capacity_bytes", "retained_allocator_capacity_bytes",
                     "measured_dram_traffic_bytes", "required_store_audit"):
            self.assertIn(name, coverage["unmeasured"])
        self.assertEqual(coverage["counters"]["process_page_fault_count"]["unit"], "count")
        # These synthetic process peaks cannot reveal a fall in live payload or a
        # rise in retention: neither inference may appear in a measured field.
        self.assertNotIn("live_allocation_capacity_bytes", pop["counters"])
        self.assertNotIn("retained_allocator_capacity_bytes", pop["counters"])

    def test_native_capture_propagates_commit_even_when_run_times_out(self):
        build = harness.Build(arm="blade-cpp", regime="serial-repro-native", size="smoke",
                              exe="unused.exe", cwd=".", expect_names=["saxpy"], lane="cpp")
        for timeout in (False, True):
            result = SimpleNamespace(wall_s=0.01, launch_error=None, timed_out=timeout,
                                     memory={"peak_working_set": 100, "peak_pagefile": 700,
                                             "page_faults": 0},
                                     stdout="saxpy completed in 0.01s\nchecksum = 2\ninput_checksum = 1\n",
                                     stderr="", returncode=0)
            with patch.object(harness, "make_env", return_value={}), \
                    patch.object(harness, "run_proc", return_value=result):
                captured = harness.run_exe(build, harness.Regime.parse(build.regime), 1, "round", None)
            self.assertEqual(captured.peak_commit_bytes, 700)
            self.assertEqual(captured.peak_working_set, 100)
            self.assertEqual(captured.page_faults, 0)
            self.assertEqual(captured.ok, not timeout)

    def test_unavailable_native_counters_remain_null(self):
        build = harness.Build(arm="blade-cpp", regime="serial-repro-native", size="smoke",
                              exe="unused.exe", cwd=".", expect_names=["saxpy"], lane="cpp")
        result = SimpleNamespace(wall_s=0.01, launch_error="mock launch failure", timed_out=False, memory=None)
        with patch.object(harness, "make_env", return_value={}), \
                patch.object(harness, "run_proc", return_value=result):
            captured = harness.run_exe(build, harness.Regime.parse(build.regime), 1, "round", None)
        self.assertIsNone(captured.peak_working_set)
        self.assertIsNone(captured.peak_commit_bytes)
        self.assertIsNone(captured.page_faults)
        self.assertFalse(captured.ok)

    def test_legacy_max_stays_all_samples_while_memory_rounds_are_separate(self):
        entry = {"status": "ok", "samples": [sample(resident=100), sample("warmup", resident=800),
                                                sample(ok=False, resident=900)]}
        session = harness.Session.__new__(harness.Session)
        session._statistics(SimpleNamespace(name="saxpy"), harness.Regime.parse("serial-repro-native"),
                            {}, {"blade-cpp": entry}, None, False)
        self.assertEqual(entry["peak_working_set_max"], 900)
        self.assertEqual(self.population(entry["samples"])["counters"]["process_peak_resident_bytes"]
                         ["distribution"]["max"], 100)

    def test_checkpoint_adds_memory_to_partial_blocks_and_controls_without_statistics(self):
        session = harness.Session.__new__(harness.Session)
        session.data = {"schema": 2, "memory_report": harness.memory_coverage()}
        session.failures, session.flags, session.warnings, session.manifest = [], [], [], []
        session.blocks = [{"arms": {"blade-cpp": {"samples": [sample()]}}}]
        session.controls = [{"samples": [sample("control", commit=None)]}]
        with tempfile.TemporaryDirectory() as directory:
            session.bundle = Path(directory)
            session.checkpoint()
            stored = json.loads((session.bundle / "session.json").read_text(encoding="utf-8"))
        self.assertEqual(stored["schema"], 2)
        self.assertEqual(stored["memory_report"]["schema"], 1)
        self.assertEqual(stored["blocks"][0]["arms"]["blade-cpp"]["samples"][0]["peak_working_set"], 100)
        self.assertIsNone(stored["controls"][0]["samples"][0]["peak_commit_bytes"])
        self.assertEqual(stored["controls"][0]["memory_report"]["populations"]
                         ["successful_controls"]["counters"]["process_peak_commit_bytes"]["status"], "unmeasured")

    def test_renderer_accepts_legacy_json_and_uses_explicit_units(self):
        data = {"schema": 2, "mode": "check", "blocks": [{"kernel": "saxpy", "size": "smoke",
                "regime": "serial-repro-native", "arms": {"blade-cpp": {"status": "ok", "samples": [
                    {"kind": "contract", "ok": True, "peak_working_set": 2**20, "page_faults": 0}]}}}]}
        rendered = harness.render_memory_report(data)
        self.assertIn("1/1; 1 / 0 / 1", rendered)
        self.assertIn("0/1; unmeasured", rendered)
        self.assertIn("PeakPagefileUsage", rendered)
        self.assertIn("1,048,576", rendered)
        self.assertIn("soft and hard", rendered)
        self.assertIn("cleanup audit: **unmeasured**", rendered)

    def test_pm49_metadata_and_malformed_model_are_checked_before_execution(self):
        source_dir = harness.KERNELS_DIR / "saxpy"
        kernel = harness.load_kernel(source_dir)
        self.assertEqual(kernel.raw["memory_contract"]["id"], "PM49-triad-materialized-v1")
        raw = json.loads((source_dir / "kernel.json").read_text(encoding="utf-8"))
        raw["memory_contract"]["linear_payload_model"]["bytes_per_element"]["required_output_payload"] = True
        with tempfile.TemporaryDirectory() as directory:
            kdir = Path(directory) / "saxpy"
            kdir.mkdir()
            (kdir / "kernel.json").write_text(json.dumps(raw), encoding="utf-8")
            with self.assertRaisesRegex(harness.HarnessError, "non-negative integers"):
                harness.load_kernel(kdir)


if __name__ == "__main__":
    unittest.main()
