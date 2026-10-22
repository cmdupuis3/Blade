"""B07 saved-output validation. Numeric success cannot substitute for native review."""
import argparse
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import adapt

ROOT = Path(__file__).resolve().parent
CONTRACT = "B07-PM52-opaque-task-v1"
REFERENCE_SHA = "d4207199f19f2db8e9bef8cce0c8b338edd0f7d47a582816d0fd69c508212fb0"
N = 2053
NATIVE_GATES = (
    "objects_are_native_not_bitcode", "separate_task_driver_compilation", "no_lto_ipo_unity_or_cross_boundary_clone",
    "abi_offsets_and_values_match", "t0_before_real_task_call", "payload_work_inside_call",
    "both_full_output_store_streams", "checksums_and_final_input_add_before_publish",
    "publish_before_task_return", "t1_after_task_return", "all_cell_actual_loads_after_t1",
    "roots_live_until_observation_finishes", "matched_cleanup_after_observation",
)
FLAGS = ("post_input_mutation_pass", "p_corruption_detected", "q_corruption_detected", "restored_pass",
         "bad_n_rejected", "bad_abi_rejected", "alias_rejected", "null_rejected",
         "duplicate_cleanup_rejected", "descriptors_cleared")


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def load_reference():
    path = ROOT.parent / "store-consume" / "reference.py"
    if sha(path.read_bytes()) != REFERENCE_SHA:
        raise ValueError("independent reference identity changed")
    spec = importlib.util.spec_from_file_location("b07_independent_reference", path)
    ref = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(ref)
    return ref


def expected(prepared=False):
    ref = load_reference()
    x = [ref.r5(k, 10000019) for k in range(N)]
    y = [ref.r5(k, 20000038) for k in range(N)]
    if prepared:
        x[N // 2] += 0.125
    return ref.from_inputs(x, y)


def close(got, truth):
    return type(got) in (int, float) and math.isfinite(got) and abs(got-truth) <= 1e-12*max(1.0, abs(truth))


def numeric_errors(report, truth, regime, prepared=False):
    errors = []
    if report.get("schema") != 1 or report.get("contract") != CONTRACT or report.get("n") != N:
        errors.append("wrong report contract")
    for name in ("p", "q"):
        values = report.get(name)
        if not isinstance(values, list) or len(values) != N:
            errors.append(name + " must contain every cell")
        elif any(not close(got, wanted) for got, wanted in zip(values, truth[name])):
            errors.append(name + " cell mismatch")
    suffix = "serial" if regime == "repro" else "fsum"
    for key in ("checksum", "input_checksum"):
        if not close(report.get(key), truth[key + "_" + suffix]):
            errors.append(key + " mismatch")
    for key, wanted in {"task_status": 0, "task_calls": 1, "timed_intervals": 1, "abi_size": 72,
                        "canonical_bad": 0, "p_count": N, "q_count": N, "cleanup_status": 0,
                        "observer_calls": 5, "cleanup_calls": 6,
                        "prepared_calls": int(prepared), "failed": 0}.items():
        if type(report.get(key)) is not int or report[key] != wanted:
            errors.append(key + " missing/wrong")
    if report.get("prepared_mutation") is not prepared:
        errors.append("prepared mutation identity mismatch")
    for key in FLAGS:
        if report.get(key) is not True:
            errors.append(key + " missing/failure")
    if type(report.get("assertions")) is not int or report["assertions"] < 14:
        errors.append("missing runtime assertions")
    for key in ("opaque_task_interval_s", "observer_s", "cleanup_s"):
        value = report.get(key)
        if type(value) not in (int, float) or not math.isfinite(value) or value < 0 or (key == "opaque_task_interval_s" and value == 0):
            errors.append(key + " missing/invalid")
    if report.get("compiler_enclosure_established", "absent") is not None:
        errors.append("driver cannot certify compiler enclosure")
    if report.get("performance_eligible") is not False:
        errors.append("performance admission forbidden")
    return errors


def provenance_errors(provenance, base):
    errors = []
    if provenance.get("contract") != CONTRACT or provenance.get("regime") not in ("licensed", "repro"):
        errors.append("bad provenance contract")
    if provenance.get("route") not in ("cpp", "llvm", "natural", "expert"):
        errors.append("unknown route")
    if type(provenance.get("prepared_mutation")) is not bool:
        errors.append("missing mutation identity")
    if provenance.get("performance_eligible") is not False:
        errors.append("provenance cannot admit performance")
    records = provenance.get("files", {})
    required = {"report", "executable", "task_source", "driver_source", "observer_source", "abi",
                "task_object", "driver_object", "observer_object", "frozen_manifest"}
    if provenance.get("route") == "llvm":
        required |= {"shim_source", "shim_object", "adaptation"}
    elif provenance.get("route") == "cpp":
        required |= {"adaptation"}
    if not required <= set(records):
        errors.append("missing identity records")
    for label, record in records.items():
        try:
            path = Path(record["path"])
            if not path.is_absolute() or sha(path.read_bytes()) != record["sha256"]:
                errors.append("identity mismatch: " + label)
        except (KeyError, OSError, TypeError):
            errors.append("unreadable identity: " + label)
    for label, source in (("driver_source", ROOT / "driver.c"), ("observer_source", ROOT / "observer.c"), ("abi", ROOT / "b07_abi.h"),
                          ("frozen_manifest", ROOT / "frozen-manifest.json")):
        if records.get(label, {}).get("sha256") != sha(source.read_bytes()):
            errors.append("reviewed source identity mismatch: " + label)
    try:
        adapt.verify_bundle()
        route, regime = provenance["route"], provenance["regime"]
        prepared = provenance["prepared_mutation"]
        if route in ("cpp", "llvm"):
            extension = "cpp" if route == "cpp" else "ll"
            frozen = (ROOT / "frozen" / f"{route}-{regime}.{extension}").read_bytes()
            output, prefix = adapt.transform(frozen, route, regime, prepared)
            adaptation = json.loads(Path(records["adaptation"]["path"]).read_bytes())
            if (records["task_source"]["sha256"] != sha(output)
                    or adaptation.get("adapted_sha256") != sha(output)
                    or adaptation.get("canonical_prefix_sha256") != prefix
                    or adaptation.get("origin_sha256") != sha(frozen)
                    or adaptation.get("manifest_sha256") != adapt.MANIFEST_SHA
                    or adaptation.get("adapter_sha256") != sha((ROOT / "adapt.py").read_bytes())
                    or adaptation.get("prepared_mutation") is not prepared
                    or adaptation.get("kind") != route or adaptation.get("regime") != regime):
                errors.append("adaptation identity mismatch")
        elif records["task_source"]["sha256"] != sha((ROOT / "task.f90").read_bytes()):
            errors.append("Fortran task identity mismatch")
        if route == "llvm" and records["shim_source"]["sha256"] != sha((ROOT / "frozen/runtime/blade_llvm_shim.c").read_bytes()):
            errors.append("frozen shim identity mismatch")
    except (KeyError, ValueError, OSError, TypeError):
        errors.append("source adaptation identity unavailable")
    commands = provenance.get("commands")
    if not isinstance(commands, list) or not commands or any(
            not isinstance(c, list) or "-fno-lto" not in c or any(
                not isinstance(a, str) or a.startswith(("-flto", "-ipo", "/GL", "/LTCG")) for a in c) for c in commands):
        errors.append("missing/rejected no-LTO compile/link commands")
    if not provenance.get("compiler_versions") or not provenance.get("native_inventory"):
        errors.append("missing compiler/object inventory")
    tools = provenance.get("compiler_identities", {})
    if not {"gcc", "gxx", "clang", "gfortran", "objdump", "python"} <= set(tools):
        errors.append("missing toolchain identities")
    for name, record in tools.items():
        try:
            p = Path(record["path"])
            if not p.is_absolute() or sha(p.read_bytes()) != record["sha256"]:
                errors.append("toolchain identity mismatch: " + name)
        except (KeyError, OSError, TypeError):
            errors.append("toolchain identity unavailable: " + name)
    if base and records.get("report", {}).get("path") != str(base):
        errors.append("wrong report provenance target")
    return errors


def audit_errors(audit, provenance_raw):
    if audit is None:
        return ["native review unmeasured"]
    errors = []
    if audit.get("schema") != 1 or audit.get("contract") != CONTRACT:
        errors.append("wrong native audit contract")
    if audit.get("provenance_sha256") != sha(provenance_raw):
        errors.append("audit provenance identity mismatch")
    if not isinstance(audit.get("reviewer"), str) or not audit["reviewer"].strip():
        errors.append("reviewer missing")
    artifacts = audit.get("evidence_files", {})
    if not isinstance(artifacts, dict) or not artifacts:
        errors.append("native annotations/evidence identities missing")
    else:
        for label, record in artifacts.items():
            try:
                path = Path(record["path"])
                if not path.is_absolute() or sha(path.read_bytes()) != record["sha256"]:
                    errors.append("native evidence identity mismatch: " + label)
            except (KeyError, OSError, TypeError):
                errors.append("native evidence unreadable: " + label)
    gates = audit.get("gates", {})
    for name in NATIVE_GATES:
        item = gates.get(name, {})
        # A boolean or a marker is insufficient; reviewer supplies native addresses/dataflow.
        if not isinstance(item, dict) or item.get("pass") is not True or not isinstance(item.get("evidence"), str) or not item["evidence"].strip():
            errors.append(name + " unmeasured/failed")
    if audit.get("performance_eligible") is not False:
        errors.append("audit must remain diagnostic only")
    return errors


def enclosure_state(audit, errors):
    if audit is None:
        return None
    values = [audit.get("gates", {}).get(name, {}).get("pass")
              if isinstance(audit.get("gates", {}).get(name, {}), dict) else None for name in NATIVE_GATES]
    if any(v is False for v in values):
        return False
    if any(v is not True for v in values):
        return None
    return not errors


def absolute(value):
    path = Path(value)
    if not path.is_absolute():
        raise argparse.ArgumentTypeError("absolute path required")
    return path.resolve()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=absolute, required=True)
    parser.add_argument("--regime", choices=["licensed", "repro"], required=True)
    parser.add_argument("--prepared-mutation", action="store_true")
    parser.add_argument("--provenance", type=absolute)
    parser.add_argument("--audit", type=absolute)
    parser.add_argument("--out", type=absolute, required=True)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    numeric = numeric_errors(report, expected(args.prepared_mutation), args.regime, args.prepared_mutation)
    provenance_raw = args.provenance.read_bytes() if args.provenance else b""
    provenance = json.loads(provenance_raw) if provenance_raw else {}
    identity = provenance_errors(provenance, args.report) if provenance_raw else ["provenance unmeasured"]
    if provenance_raw and (provenance.get("regime") != args.regime or provenance.get("prepared_mutation") is not args.prepared_mutation):
        identity.append("run identity disagrees with numeric contract")
    audit = json.loads(args.audit.read_bytes()) if args.audit else None
    native = audit_errors(audit, provenance_raw)
    result = {"schema": 1, "contract": CONTRACT, "numeric_pass": not numeric,
              "identity_pass": not identity, "compiler_enclosure_established": enclosure_state(audit, native),
              "diagnostic_accepted": not (numeric or identity or native), "performance_eligible": False,
              "bit_identity_established": False, "comparison": "all-cell/scalar tolerance 1e-12 relative with unit floor",
              "numeric_errors": numeric, "identity_errors": identity, "native_errors": native,
              "report_sha256": sha(args.report.read_bytes()), "reference_sha256": REFERENCE_SHA}
    args.out.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    # Numeric-stage operation is allowed without a native audit, but cannot accept the diagnostic.
    return 1 if numeric or (args.provenance and identity) or (args.audit and native) else 0


if __name__ == "__main__":
    raise SystemExit(main())
