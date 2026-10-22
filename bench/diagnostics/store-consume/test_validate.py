"""Pure authored fixtures. Sole Luna executes; no native process/build or generated pins."""
import os
import unittest
from unittest.mock import patch

import reference as r
import validate as v


TRUTH = {"n": 3, "p": [0.25, 1.25, 2.25], "q": [0.0625, 1.5625, 5.0625],
         "checksum_serial": 6.6875, "checksum_fsum": 6.6875,
         "input_checksum_serial": 3.0, "input_checksum_fsum": 3.0}
TEXT = ("pm52_store_consume completed in 0.001s\n"
        "p = [0.25, 1.25, 2.25]\nq = [0.0625, 1.5625, 5.0625]\n"
        "checksum = 6.6875\ninput_checksum = 3.0\n")


def identity():
    return {"schema": 1, "contract": r.CONTRACT, "scope": "diagnostic-only", "arm": "blade-cpp",
            "regime": "repro", "exe_path": "C:/fixture/probe.exe", "exe_sha256": "a"*64,
            "source_sha256": {"C:/fixture/source.blade": "b"*64}, "output_sha256": {"stdout": "c"*64}}


def evidence():
    # Fictional immutable fixture IDs, not claims about an authored/native executable.
    ident = identity()
    return {**ident, "reviewer": "fixture reviewer", "artifacts": [{"path": "C:/fixture/audit.txt", "sha256": "d"*64}],
            "stores": {"status": "pass", **dict.fromkeys(v.STORE_FIELDS, True),
                       "route": "scalar-forwarding", "evidence": "audit.txt:1-4, both output loops"},
            "endpoints": {"status": "pass", **dict.fromkeys(v.ENDPOINT_FIELDS, True),
                          "cleanup": "process-exit-reclamation", "evidence": "audit.txt:5-8, stop/loads/exit"}}


def gates(audit):
    return v.assess_audit(audit, identity(), {"C:/fixture/audit.txt": "d"*64})


def run_record():
    return {**identity(), "returncode": 0, "timed_out": False, "launch_error": None,
            "argv": ["C:/fixture/probe.exe"]}


class NumericalFixtures(unittest.TestCase):
    def test_independently_authored_exact_three_cell_graph(self):
        self.assertEqual(r.from_inputs([0.0, 0.5, 1.0], [0.25, 0.5, 0.75]), TRUTH)

    def test_reference_extent_mismatch(self):
        with self.assertRaises(ValueError):
            r.from_inputs([1.0], [])

    def test_serial_sum_is_not_fsum(self):
        self.assertEqual(r.serial_sum([1e16, 1.0, -1e16]), 0.0)
        self.assertEqual(r.math.fsum([1e16, 1.0, -1e16]), 1.0)

    def test_r5_is_fixed_despite_ambient_r3(self):
        before = [r.r5(k, seed) for seed in (10000019, 20000038) for k in (0, 7, 2052)]
        with patch.dict(os.environ, {"BENCH_GENERATOR": "R3"}):
            self.assertEqual(before, [r.r5(k, seed) for seed in (10000019, 20000038) for k in (0, 7, 2052)])
        self.assertTrue(all(0.0 <= x < 1.0 and (x * 2**31).is_integer() for x in before))

    def test_full_small_output_and_both_regimes(self):
        got = v.parse_stdout(TEXT, True)
        for regime in ("licensed", "repro"):
            self.assertEqual(v.validate_numbers(got, TRUTH, regime)["status"], "pass")

    def test_missing_output_and_scalar_only(self):
        for label in ("p", "q"):
            with self.subTest(label=label), self.assertRaises(ValueError):
                v.parse_stdout("\n".join(line for line in TEXT.splitlines() if not line.startswith(label + " = ")), True)

    def test_empty_short_and_long_arrays(self):
        for values in ([], [0.25, 1.25], [0.25, 1.25, 2.25, 0.0]):
            got = v.parse_stdout(TEXT, True)
            got["p"] = values
            self.assertEqual(v.validate_numbers(got, TRUTH, "repro")["status"], "fail")

    def test_interior_cell_with_correct_endpoints_and_checksums(self):
        got = v.parse_stdout(TEXT, True)
        got["p"][1] = 0.5
        self.assertEqual(v.validate_numbers(got, TRUTH, "repro")["status"], "fail")

    def test_tail_and_q_substitution(self):
        for label, values in (("q", TRUTH["p"]), ("p", [0.25, 1.25, 2.0])):
            got = v.parse_stdout(TEXT, True)
            got[label] = values
            self.assertEqual(v.validate_numbers(got, TRUTH, "repro")["status"], "fail")

    def test_wrong_scalar_even_with_complete_arrays(self):
        got = v.parse_stdout(TEXT, True)
        got["input_checksum"] = 0.0
        self.assertEqual(v.validate_numbers(got, TRUTH, "repro")["status"], "fail")

    def test_wrong_coefficient_with_consistent_wrong_outputs_and_checksum(self):
        got = {"p": [0.25, 1.0, 1.75], "q": [0.0625, 1.0, 3.0625],
               "checksum": 4.125, "input_checksum": 3.0}
        self.assertEqual(v.validate_numbers(got, TRUTH, "repro")["status"], "fail")

    def test_duplicate_or_unknown_binding(self):
        for extra in ("checksum = 6.6875\n", "x = [1.0]\n", "p = [0.25]\n"):
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                v.parse_stdout(TEXT + extra, True)

    def test_duplicate_missing_nonfinite_or_wrong_named_stop(self):
        for text in (TEXT + TEXT.splitlines()[0], TEXT.split("\n", 1)[1], TEXT.replace("0.001s", "nans"),
                     TEXT.replace("pm52_store_consume", "saxpy"), TEXT.replace("0.001s", "0s")):
            with self.subTest(text=text), self.assertRaises(ValueError):
                v.parse_stdout(text, True)

    def test_output_before_stop_is_not_post_stop_observation(self):
        lines = TEXT.splitlines()
        with self.assertRaises(ValueError):
            v.parse_stdout("\n".join([lines[1], lines[0], *lines[2:]]), True)

    def test_rank_mismatch_bool_or_nonfinite_cell(self):
        for replacement in ("[[0.25], 1.25, 2.25]", "[True, 1.25, 2.25]", "[1e999, 1.25, 2.25]"):
            with self.subTest(replacement=replacement), self.assertRaises((ValueError, TypeError)):
                v.parse_stdout(TEXT.replace("[0.25, 1.25, 2.25]", replacement), True)

    def test_fortran_full_dump_and_label_swap(self):
        text = "\n".join(line for line in TEXT.splitlines() if not line.startswith(("p =", "q =")))
        got = v.parse_stdout(text, False)
        got.update(p=v.parse_dump("0.25\n1.25\n2.25\n"), q=v.parse_dump("0.0625\n1.5625\n5.0625\n"))
        self.assertEqual(v.validate_numbers(got, TRUTH, "repro")["status"], "pass")
        got["p"], got["q"] = got["q"], got["p"]
        self.assertEqual(v.validate_numbers(got, TRUTH, "repro")["status"], "fail")

    def test_dump_rejects_nonfinite_blank_or_comment(self):
        for text in ("nan\n", "0.25\n\n", "0.25\n# missing cells\n"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                v.parse_dump(text)


class EvidenceFixtures(unittest.TestCase):
    def test_numeric_success_alone_cannot_accept_storage(self):
        numeric = v.validate_numbers(v.parse_stdout(TEXT, True), TRUTH, "repro")
        result = v.report(numeric, gates(None), identity(), v.validate_run(run_record(), identity()))
        self.assertEqual(result["stores"]["status"], "unmeasured")
        self.assertEqual(result["endpoints"]["status"], "unmeasured")
        self.assertFalse(result["diagnostic_accepted"])
        self.assertIsNone(result["unmeasured"]["dram_traffic_bytes"])

    def test_all_diagnostic_gates_never_become_timing_eligible(self):
        result = v.report(v.validate_numbers(v.parse_stdout(TEXT, True), TRUTH, "repro"),
                          gates(evidence()), identity(), v.validate_run(run_record(), identity()))
        self.assertTrue(result["diagnostic_accepted"])
        self.assertFalse(result["performance_eligible"])

    def test_checksum_only_store_audit_rejected(self):
        audit = evidence()
        audit["stores"].update(status="fail", p_all_cells_stored=False)
        result = gates(audit)
        self.assertEqual(result["stores"]["status"], "fail")
        self.assertEqual(result["endpoints"]["status"], "pass")

    def test_same_source_different_executable_dump_twin_rejected(self):
        audit = evidence()
        audit["exe_sha256"] = "e"*64
        self.assertEqual(gates(audit)["stores"]["status"], "fail")

    def test_changed_output_source_regime_and_scope_rejected(self):
        for key, value in (("output_sha256", {}), ("source_sha256", {}), ("regime", "licensed"),
                           ("scope", "timed"), ("arm", "blade-llvm")):
            audit = evidence()
            audit[key] = value
            self.assertEqual(gates(audit)["stores"]["status"], "fail")

    def test_missing_or_tampered_artifact_and_reviewer(self):
        for change in ("missing", "hash", "reviewer"):
            audit = evidence()
            if change == "missing": audit["artifacts"] = []
            elif change == "hash": audit["artifacts"][0]["sha256"] = "e"*64
            else: audit["reviewer"] = ""
            self.assertEqual(gates(audit)["stores"]["status"], "fail")

    def test_unknown_gate_and_null_claim_are_not_pass(self):
        audit = evidence()
        audit["endpoints"] = {"status": "unmeasured"}
        self.assertEqual(gates(audit)["endpoints"]["status"], "unmeasured")
        audit["stores"]["q_all_cells_stored"] = None
        self.assertEqual(gates(audit)["stores"]["status"], "fail")

    def test_post_stop_early_free_or_unclassified_cleanup_refused(self):
        for key, value in (("outputs_valid_through_observation", False), ("post_stop_q_loads_all_cells", False),
                           ("stop_precedes_array_observation", False), ("cleanup", "unknown")):
            audit = evidence()
            audit["endpoints"][key] = value
            self.assertEqual(gates(audit)["endpoints"]["status"], "fail")

    def test_failed_timed_out_or_missing_run_completion(self):
        for key, value in (("returncode", 1), ("returncode", None), ("timed_out", True),
                           ("launch_error", "could not launch"), ("argv", [])):
            run = run_record()
            run[key] = value
            self.assertEqual(v.validate_run(run, identity())["status"], "fail")

    def test_run_executable_and_outputs_are_exact(self):
        for key, value in (("exe_sha256", "f"*64), ("output_sha256", {}), ("arm", "blade-llvm")):
            run = run_record()
            run[key] = value
            self.assertEqual(v.validate_run(run, identity())["status"], "fail")


if __name__ == "__main__":
    unittest.main()
