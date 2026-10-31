"""Validate one saved B04 diagnostic; never builds, runs, or measures a native program.

Exit 0: all diagnostic gates pass; 1: failed gate; 2: invalid invocation/evidence file;
3: numerical pass but store/endpoint evidence remains unmeasured. Never timing eligible.
"""
import argparse
import ast
import hashlib
import json
import math
from pathlib import Path
import re

import reference

TIME = re.compile(r"^pm52_store_consume completed in\s+(\S+)s$")
ARMS = ("blade-cpp", "blade-llvm", "gfortran-natural", "gfortran-expert")
STORE_FIELDS = ("inputs_stored", "original_inputs_preserved", "p_q_distinct",
                "p_all_cells_stored", "q_all_cells_stored", "fp_graph_preserved")
ENDPOINT_FIELDS = ("stop_precedes_array_observation", "post_stop_p_loads_all_cells",
                   "post_stop_q_loads_all_cells", "outputs_valid_through_observation")


def number(value):
    if type(value) not in (float, int) or not math.isfinite(value):
        raise ValueError("expected a finite numeric value")
    return float(value)


def parse_stdout(text, blade):
    values = {}
    timing = None
    for line in text.splitlines():
        line = line.strip()
        if not line:
            continue
        match = TIME.fullmatch(line)
        if match:
            if timing is not None:
                raise ValueError("duplicate stop/timing line")
            timing = number(float(match.group(1)))
            if timing <= 0:
                raise ValueError("nonpositive diagnostic timer")
            continue
        if " = " not in line:
            raise ValueError("unexpected stdout line")
        if timing is None:
            raise ValueError("observation precedes stop line")
        label, rhs = line.split(" = ", 1)
        allowed = ("p", "q", "checksum", "input_checksum") if blade else ("checksum", "input_checksum")
        if label not in allowed or label in values:
            raise ValueError("unexpected or duplicate binding: " + label)
        value = ast.literal_eval(rhs)
        if label in ("p", "q"):
            if type(value) is not list:
                raise ValueError("rank-one output must be a list")
            values[label] = [number(v) for v in value]
        else:
            values[label] = number(value)
    required = {"p", "q", "checksum", "input_checksum"} if blade else {"checksum", "input_checksum"}
    if timing is None or set(values) != required:
        raise ValueError("missing stop line or required output")
    return values


def parse_dump(text):
    # One finite cell on every line, no ignored blanks, comments, or truncated suffix.
    return [number(float(line)) for line in text.splitlines()]


def validate_numbers(got, expected, regime):
    errors = []
    counts = {}
    for label in ("p", "q"):
        values = got.get(label)
        counts[label] = len(values) if type(values) is list else None
        if type(values) is not list or len(values) != expected["n"]:
            errors.append(label + ": wrong full-output count")
            continue
        for k, (value, truth) in enumerate(zip(values, expected[label])):
            if type(value) not in (float, int) or not math.isfinite(value) or abs(value - truth) > 1e-12 * max(1.0, abs(truth)):
                errors.append(f"{label}: cell {k} differs or is nonfinite")
                break
    suffix = "serial" if regime == "repro" else "fsum"
    for label in ("checksum", "input_checksum"):
        truth = expected[label + "_" + suffix]
        value = got.get(label)
        if type(value) not in (float, int) or not math.isfinite(value) or abs(value - truth) > 1e-12 * max(1.0, abs(truth)):
            errors.append(label + ": wrong or missing scalar")
    return {"status": "fail" if errors else "pass", "errors": errors,
            "full_output_counts": counts, "reference_count": expected["n"],
            "scalar_reference": suffix, "relative_tolerance_with_unit_floor": 1e-12,
            "bit_identity_established": False}


def assess_audit(audit, identity, artifact_digests):
    """Manual exact-executable audits are evidence, never inferred from numeric success."""
    if audit is None:
        missing = {"status": "unmeasured", "errors": ["no exact-executable audit supplied"]}
        return {"stores": dict(missing), "endpoints": dict(missing)}
    identity_errors = []
    for key in ("schema", "contract", "scope", "arm", "regime", "exe_sha256", "source_sha256", "output_sha256"):
        if key not in audit:
            identity_errors.append("missing identity: " + key)
        elif audit[key] != identity[key]:
            identity_errors.append("mismatched identity: " + key)
    artifacts = audit.get("artifacts")
    artifact_errors = []
    if not isinstance(artifacts, list) or not artifacts:
        artifact_errors.append("missing hashed audit artifacts")
    else:
        for item in artifacts:
            if (not isinstance(item, dict) or not isinstance(item.get("path"), str)
                    or not isinstance(item.get("sha256"), str)
                    or artifact_digests.get(item["path"]) != item["sha256"]):
                artifact_errors.append("missing or mismatched audit artifact")
    if not isinstance(audit.get("reviewer"), str) or not audit["reviewer"].strip():
        artifact_errors.append("missing audit reviewer")
    result = {}
    for name, fields in (("stores", STORE_FIELDS), ("endpoints", ENDPOINT_FIELDS)):
        gate = audit.get(name)
        errors = list(identity_errors) + list(artifact_errors)
        if not isinstance(gate, dict) or gate.get("status") in (None, "unmeasured"):
            result[name] = {"status": "fail" if identity_errors else "unmeasured",
                            "errors": errors + ["gate unmeasured"], "evidence": None}
            continue
        if gate.get("status") != "pass":
            errors.append("audit did not pass")
        for field in fields:
            if gate.get(field) is not True:
                errors.append("unestablished: " + field)
        if not isinstance(gate.get("evidence"), str) or not gate["evidence"].strip():
            errors.append("missing annotated artifact/line references")
        if name == "stores" and gate.get("route") not in ("separate-passes", "scalar-forwarding", "mixed"):
            errors.append("missing actual route classification")
        if name == "endpoints" and gate.get("cleanup") not in ("explicit-after-observation", "process-exit-reclamation"):
            errors.append("missing cleanup classification")
        result[name] = {"status": "fail" if errors else "pass", "errors": errors,
                        "evidence": gate.get("evidence"), "details": gate}
    return result


def validate_run(record, identity):
    errors = []
    if not isinstance(record, dict):
        return {"status": "fail", "errors": ["missing run provenance"]}
    for key in ("arm", "regime", "exe_sha256", "output_sha256"):
        if record.get(key) != identity[key]:
            errors.append("run provenance mismatch: " + key)
    if type(record.get("returncode")) is not int or record["returncode"] != 0:
        errors.append("native run did not exit successfully")
    if record.get("timed_out") is not False or "launch_error" not in record or record["launch_error"] is not None:
        errors.append("native run timed out, failed to launch, or lacks completion evidence")
    argv = record.get("argv")
    if not isinstance(argv, list) or not argv or argv[0] != identity["exe_path"]:
        errors.append("recorded command is not the observed executable")
    return {"status": "fail" if errors else "pass", "errors": errors}


def report(numeric, gates, identity, provenance):
    statuses = [numeric["status"], gates["stores"]["status"], gates["endpoints"]["status"], provenance["status"]]
    return {**identity, "diagnostic_accepted": all(s == "pass" for s in statuses),
            "performance_eligible": False, "numeric": numeric, **gates, "run_provenance": provenance,
            "unmeasured": {"dram_traffic_bytes": None, "logical_liveness_bytes": None,
                           "whole_process_allocation_capacity_bytes": None,
                           "retained_allocator_capacity_bytes": None},
            "timing_claim": "none: post-stop formatting and teardown remain in process wall time"}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_audit(path):
    if path is None:
        return None, {}
    audit = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(audit, dict):
        raise ValueError("audit must be a JSON object")
    digests = {}
    for artifact in audit.get("artifacts", []):
        if not isinstance(artifact, dict) or not isinstance(artifact.get("path"), str):
            continue
        target = Path(artifact["path"])
        if target.is_absolute() and target.is_file():
            digests[artifact["path"]] = digest(target)
    return audit, digests


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arm", choices=ARMS, required=True)
    parser.add_argument("--regime", choices=("licensed", "repro"), required=True)
    for name in ("stdout", "exe", "out", "run-record"):
        parser.add_argument("--" + name, type=reference.absolute_path, required=True)
    for name in ("p-dump", "q-dump", "audit"):
        parser.add_argument("--" + name, type=reference.absolute_path)
    parser.add_argument("--source", type=reference.absolute_path, action="append", required=True)
    args = parser.parse_args()
    try:
        blade = args.arm.startswith("blade-")
        if blade and (args.p_dump or args.q_dump):
            raise ValueError("Blade must observe its own stdout arrays")
        if not blade and (args.p_dump is None or args.q_dump is None):
            raise ValueError("Fortran requires both dump paths")
        output_paths = {"stdout": args.stdout}
        if not blade:
            output_paths.update(p_dump=args.p_dump, q_dump=args.q_dump)
        identity = {"schema": 1, "contract": reference.CONTRACT, "scope": "diagnostic-only",
                    "arm": args.arm, "regime": args.regime, "exe_path": str(args.exe), "exe_sha256": digest(args.exe),
                    "source_sha256": {str(p): digest(p) for p in args.source},
                    "output_sha256": {key: digest(p) for key, p in output_paths.items()}}
        try:
            got = parse_stdout(args.stdout.read_text(encoding="utf-8"), blade)
            if not blade:
                got.update(p=parse_dump(args.p_dump.read_text(encoding="utf-8")),
                           q=parse_dump(args.q_dump.read_text(encoding="utf-8")))
            numeric = validate_numbers(got, reference.compute(), args.regime)
        except (ValueError, SyntaxError, TypeError) as exc:
            numeric = {"status": "fail", "errors": ["invalid complete output: " + str(exc)]}
        audit, artifacts = load_audit(args.audit)
        run = json.loads(args.run_record.read_text(encoding="utf-8"))
        result = report(numeric, assess_audit(audit, identity, artifacts), identity, validate_run(run, identity))
        result["run_record_sha256"] = digest(args.run_record)
        result["validator_sha256"] = digest(Path(__file__))
        result["reference_sha256"] = digest(Path(reference.__file__))
        args.out.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    except (OSError, ValueError, TypeError) as exc:
        parser.exit(2, str(exc) + "\n")
    if result["diagnostic_accepted"]:
        return 0
    return 1 if any(result[k]["status"] == "fail" for k in ("numeric", "stores", "endpoints", "run_provenance")) else 3


if __name__ == "__main__":
    raise SystemExit(main())
