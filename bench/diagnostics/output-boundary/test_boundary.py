"""Pure fixture tests ONLY. No compiler, emitted executable, or timing workload."""
import copy
import json
import tempfile
import unittest
from pathlib import Path
import adapt
import validate


class ExactArtifactTests(unittest.TestCase):
    def test_frozen_bundle(self):
        adapt.verify_bundle()

    def test_four_adaptations_preserve_exact_arithmetic_prefix(self):
        for kind, regime in adapt.PROGRAMS:
            extension = "cpp" if kind == "cpp" else "ll"
            raw = (adapt.ROOT / "frozen" / f"{kind}-{regime}.{extension}").read_bytes()
            if kind == "cpp":
                # These exact frozen emitters end at '}', without a final newline.
                self.assertTrue(raw.endswith(b"\n}"))
                self.assertFalse(raw.endswith(b"\n"))
            canonical, prefix = adapt.transform(raw, kind, regime)
            mutant, mutated_prefix = adapt.transform(raw, kind, regime, True)
            text = raw.decode().replace("\r\n", "\n")
            output = canonical.decode()
            if kind == "cpp":
                head, main = text.split("int main() {\n")
                body = main.split("    try {\n")[1].split("    auto __blade_t1 = TIME;\n")[0]
                self.assertIn(head, output)
                self.assertIn(body, output)
                self.assertNotIn("int main()", output)
                self.assertIn("out->q,ext", output)
            else:
                head, main = text.split("define i32 @main() #0 {\nentry:\n")
                body = main.split("  %r63 = call double @blade_now()\n")[0]
                body = body.replace("  %r1 = call double @blade_now()\n", "")
                self.assertIn(head, output)
                self.assertIn(body, output)
                self.assertNotIn("define i32 @main", output)
                self.assertIn("call void @blade_free(ptr %q)", output)
                self.assertIn("define i32 @b07_pm52_task(ptr %b07_out) {", output)
            self.assertNotIn("b07_mutate_prepared(", output)
            self.assertEqual(mutant.decode().count("call void @b07_mutate_prepared") if kind == "llvm"
                             else mutant.decode().count("    b07_mutate_prepared("), 1)
            self.assertEqual(prefix, mutated_prefix)
            self.assertNotEqual(adapt.sha(canonical), adapt.sha(mutant))

    def test_modified_artifact_refuses_before_structure_parser(self):
        for kind, regime in adapt.PROGRAMS:
            ext = "cpp" if kind == "cpp" else "ll"
            raw = (adapt.ROOT / "frozen" / f"{kind}-{regime}.{ext}").read_bytes()
            for altered in (raw + b" ", raw.replace(b"2053", b"2054", 1), b"unknown"):
                with self.assertRaisesRegex(ValueError, "unknown source artifact"):
                    adapt.transform(altered, kind, regime)

    def test_regime_and_route_are_not_interchangeable(self):
        raw = (adapt.ROOT / "frozen/cpp-repro.cpp").read_bytes()
        with self.assertRaises(ValueError):
            adapt.transform(raw, "cpp", "licensed")
        with self.assertRaises(ValueError):
            adapt.transform(raw, "llvm", "repro")

    def test_structure_refusal_is_strict(self):
        with self.assertRaises(ValueError):
            adapt.once("two two", "two")
        with self.assertRaises(ValueError):
            adapt.cpp_task("int main() {\nunknown", False)
        for regime in ("licensed", "repro"):
            text = (adapt.ROOT / "frozen" / f"cpp-{regime}.cpp").read_text(encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "unknown C\\+\\+ failure scaffold"):
                adapt.cpp_task(text + "\n", False)

    def test_changed_manifest_cannot_authorize_a_new_artifact(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            changed = json.loads((adapt.ROOT / "frozen-manifest.json").read_bytes())
            changed["files"]["frozen/cpp-repro.cpp"] = "0" * 64
            (root / "frozen-manifest.json").write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "unknown frozen manifest"):
                adapt.verify_bundle(root)


class IndependentOutputTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.truth = validate.expected()

    def fixture(self):
        report = {"schema": 1, "contract": validate.CONTRACT, "n": 2053,
                  "p": list(self.truth["p"]), "q": list(self.truth["q"]),
                  "checksum": self.truth["checksum_serial"], "input_checksum": self.truth["input_checksum_serial"],
                  "task_status": 0, "task_calls": 1, "timed_intervals": 1, "observer_calls": 5, "cleanup_calls": 6, "abi_size": 72,
                  "canonical_bad": 0, "p_count": 2053, "q_count": 2053, "cleanup_status": 0,
                  "prepared_calls": 0, "prepared_mutation": False, "assertions": 14, "failed": 0,
                  "opaque_task_interval_s": 0.001, "observer_s": 0.002, "cleanup_s": 0.003,
                  "compiler_enclosure_established": None, "performance_eligible": False}
        report.update(dict.fromkeys(validate.FLAGS, True))
        return report

    def test_hand_specified_graph(self):
        # An independently written 3-cell graph witness, not generated native pins.
        ref = validate.load_reference()
        result = ref.from_inputs([0.0, 0.5, 1.0], [1.0, 0.25, 0.0])
        self.assertEqual(result["p"], [1.0, 1.0, 1.5])
        self.assertEqual(result["q"], [1.0, 1.0, 2.25])
        self.assertEqual(result["checksum_serial"], 4.25)
        self.assertEqual(result["input_checksum_serial"], 2.75)

    def test_valid_numeric_still_has_no_native_proof(self):
        self.assertEqual(validate.numeric_errors(self.fixture(), self.truth, "repro"), [])
        self.assertEqual(validate.audit_errors(None, b""), ["native review unmeasured"])

    def test_interior_tail_and_null_fail_even_with_correct_checksums(self):
        for array, index, value in (("p", 1026, -10), ("q", 2052, -10), ("q", 0, None)):
            r = self.fixture(); r[array][index] = value
            self.assertTrue(validate.numeric_errors(r, self.truth, "repro"))

    def test_missing_cells_and_flags_and_multiple_calls_refuse(self):
        for name, value in (("p", []), ("q_count", 2052), ("cleanup_status", 1), ("abi_size", 64),
                            ("task_calls", 2), ("timed_intervals", 2), ("failed", 1),
                            ("null_rejected", None), ("opaque_task_interval_s", None),
                            ("compiler_enclosure_established", True), ("performance_eligible", True)):
            r = self.fixture(); r[name] = value
            self.assertTrue(validate.numeric_errors(r, self.truth, "repro"), name)

    def test_preproducer_mutation_is_distinct_dependency_witness(self):
        changed = validate.expected(True)
        self.assertNotEqual(self.truth["p"][1026], changed["p"][1026])
        self.assertNotEqual(self.truth["q"][1026], changed["q"][1026])
        self.assertEqual(changed["input_checksum_serial"] - self.truth["input_checksum_serial"], 0.125)
        r = self.fixture()
        self.assertTrue(validate.numeric_errors(r, changed, "repro", True))
        r.update(p=changed["p"], q=changed["q"], checksum=changed["checksum_serial"],
                 input_checksum=changed["input_checksum_serial"], prepared_calls=1, prepared_mutation=True)
        self.assertEqual(validate.numeric_errors(r, changed, "repro", True), [])

    def test_audit_requires_hash_bound_address_evidence(self):
        audit = {"schema": 1, "contract": validate.CONTRACT, "reviewer": "fixture", "provenance_sha256": validate.sha(b"identity"),
                 "performance_eligible": False,
                 "evidence_files": {"fixture_only": {"path": str(Path(__file__).resolve()),
                     "sha256": validate.sha(Path(__file__).read_bytes())}},
                 "gates": {g: {"pass": True, "evidence": "fixture address/dataflow witness"} for g in validate.NATIVE_GATES}}
        self.assertEqual(validate.audit_errors(audit, b"identity"), [])
        self.assertTrue(validate.audit_errors(audit, b"changed executable identity"))
        for gate in validate.NATIVE_GATES:
            bad = copy.deepcopy(audit); bad["gates"][gate] = True
            self.assertTrue(validate.audit_errors(bad, b"identity"))
            bad = copy.deepcopy(audit); bad["gates"][gate]["pass"] = None
            self.assertTrue(validate.audit_errors(bad, b"identity"))

    def test_unmeasured_native_stays_null_and_explicit_failure_stays_false(self):
        template = json.loads((adapt.ROOT / "audit-template.json").read_bytes())
        self.assertIsNone(validate.enclosure_state(template, validate.audit_errors(template, b"")))
        template["gates"]["both_full_output_store_streams"]["pass"] = False
        self.assertIs(validate.enclosure_state(template, validate.audit_errors(template, b"")), False)

    def test_missing_provenance_and_lto_refuse(self):
        for commands in ([], [["tool", "-flto"]], [["tool", "-fno-lto", "-flto=auto"]]):
            errors = validate.provenance_errors({"commands": commands}, None)
            self.assertIn("missing/rejected no-LTO compile/link commands", errors)


if __name__ == "__main__":
    unittest.main()
