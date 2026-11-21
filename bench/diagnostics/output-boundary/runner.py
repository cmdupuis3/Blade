"""Sole-runner staged request. Never invoke without root review and runner slot.

Primary = exactly eight canonical native processes, one task call each.
Prepared = one explicitly root-selected extra mutation process in a new output root.
Neither mode is a benchmark or an ordinary emitted-Blade performance admission.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
sys.dont_write_bytecode = True
import adapt

ROOT = Path(__file__).resolve().parent


def absolute(value):
    p = Path(value)
    if not p.is_absolute():
        raise argparse.ArgumentTypeError("absolute path required")
    return p.resolve()


def identity(path):
    return {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", choices=["primary", "prepared"], required=True)
    parser.add_argument("--out", type=absolute, required=True)
    for name in ("gcc", "gxx", "clang", "gfortran", "objdump"):
        parser.add_argument("--" + name, type=absolute, required=True)
    parser.add_argument("--arm", choices=[a+"-"+r for a in ("cpp", "llvm", "natural", "expert") for r in ("licensed", "repro")])
    args = parser.parse_args()
    if not args.out.is_relative_to(ROOT.parents[1] / ".build"):
        parser.error("output must be a new directory within this checkout's absolute bench/.build")
    if not Path(sys.executable).is_absolute():
        parser.error("absolute Python executable required")
    if args.stage == "primary" and args.arm or args.stage == "prepared" and not args.arm:
        parser.error("primary has no --arm; prepared requires exactly one root-selected --arm")
    for name in ("gcc", "gxx", "clang", "gfortran", "objdump"):
        if not getattr(args, name).is_file():
            parser.error("missing tool: " + name)
    adapt.verify_bundle()
    args.out.mkdir(parents=True, exist_ok=False)
    base_temp = args.out / "base-temp"
    base_temp.mkdir()
    env = os.environ.copy()
    for name in list(env):
        if name.upper().startswith("BLADE_") or name.upper() in ("OPENBLAS_DIR", "OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS"):
            env.pop(name)
    env.update(TEMP=str(base_temp), TMP=str(base_temp), OMP_NUM_THREADS="1", OMP_DYNAMIC="FALSE",
               OPENBLAS_NUM_THREADS="1", PYTHONDONTWRITEBYTECODE="1")
    env["PATH"] = os.pathsep.join(dict.fromkeys(str(getattr(args, n).parent) for n in ("gcc", "gxx", "clang", "gfortran", "objdump"))) + os.pathsep + env.get("PATH", "")
    counter = 0

    def call(argv, cwd, logroot, child_env=env):
        nonlocal counter
        counter += 1
        words = [str(x) for x in argv]
        log = logroot / f"{counter:03d}-command.json"
        record = {"argv": words, "cwd": str(cwd), "TEMP": child_env["TEMP"], "TMP": child_env["TMP"]}
        log.write_text(json.dumps(record, indent=2)+"\n", encoding="utf-8")
        with (logroot / f"{counter:03d}-stdout.txt").open("wb") as stdout, (logroot / f"{counter:03d}-stderr.txt").open("wb") as stderr:
            try:
                result = subprocess.run(words, cwd=cwd, env=child_env, stdout=stdout, stderr=stderr, check=False, timeout=180)
            except (OSError, subprocess.TimeoutExpired) as error:
                record.update(launch_or_timeout_error=str(error), returncode=None)
                log.write_text(json.dumps(record, indent=2)+"\n", encoding="utf-8")
                raise
        record["returncode"] = result.returncode
        log.write_text(json.dumps(record, indent=2)+"\n", encoding="utf-8")
        if result.returncode:
            raise RuntimeError(f"stage refused: exit {result.returncode}; {log}")
        return words, logroot / f"{counter:03d}-stdout.txt"

    versions = {}
    compiler_identities = {}
    for name in ("gcc", "gxx", "clang", "gfortran", "objdump"):
        _, log = call([getattr(args, name), "--version"], args.out, args.out)
        versions[name] = log.read_text(encoding="utf-8", errors="replace")
        compiler_identities[name] = identity(getattr(args, name))
    compiler_identities["python"] = identity(Path(sys.executable).resolve())
    # Pure fixtures execute before native compilation; private temp exists already.
    call([sys.executable, ROOT / "test_boundary.py"], ROOT, args.out)
    names = ([a+"-"+r for a in ("cpp", "llvm", "natural", "expert") for r in ("licensed", "repro")]
             if args.stage == "primary" else [args.arm])
    prepared = args.stage == "prepared"
    completed = []
    for name in names:
        route, regime = name.split("-")
        arm = args.out / name
        arm.mkdir()
        temp = arm / "temp"; temp.mkdir()
        arm_env = dict(env, TEMP=str(temp), TMP=str(temp))
        commands = []
        files = {"driver_source": identity(ROOT / "driver.c"), "observer_source": identity(ROOT / "observer.c"),
                 "abi": identity(ROOT / "b07_abi.h"),
                 "frozen_manifest": identity(ROOT / "frozen-manifest.json")}
        driver_object, task_object = arm / "driver.o", arm / "task.o"
        argv = [args.gcc, "-std=c11", "-O3", "-march=native", "-ffp-contract=off", "-fno-lto", "-I", ROOT,
                "-DB07_EXPECT_PREPARED_MUTATION="+str(int(prepared)), "-c", ROOT / "driver.c", "-o", driver_object]
        commands.append(call(argv, arm, arm, arm_env)[0])
        observer_object = arm / "observer.o"
        commands.append(call([args.gcc, "-std=c11", "-O3", "-march=native", "-ffp-contract=off", "-fno-lto",
                              "-I", ROOT, "-c", ROOT / "observer.c", "-o", observer_object], arm, arm, arm_env)[0])
        files["observer_object"] = identity(observer_object)
        objects = [task_object, driver_object, observer_object]
        fp_contract = "fast" if regime == "licensed" else "off"
        if route in ("cpp", "llvm"):
            adaptation_dir = arm / "adapted"
            adapt_argv = [sys.executable, ROOT / "adapt.py", "--kind", route, "--regime", regime, "--out-dir", adaptation_dir]
            if prepared:
                adapt_argv.append("--prepared-mutation")
            call(adapt_argv, arm, arm, arm_env)
            ext = "cpp" if route == "cpp" else "ll"
            source = adaptation_dir / (("task-prepared." if prepared else "task.") + ext)
            files["adaptation"] = identity(adaptation_dir / "adaptation.json")
            if route == "cpp":
                argv = [args.gxx, "-std=c++20", "-O3", "-march=native", "-ffp-contract="+fp_contract,
                        "-fno-math-errno", "-fwrapv", "-fopenmp", "-fno-lto", "-I", ROOT,
                        "-I", ROOT / "frozen/runtime", "-c", source, "-o", task_object]
                linker = args.gxx
            else:
                argv = [args.clang, "-c", "-O3", "-march=native", "-mllvm", "-force-ordered-reductions",
                        "-Wno-override-module", "-fno-lto", source, "-o", task_object]
                linker = args.clang
                shim, shim_object = ROOT / "frozen/runtime/blade_llvm_shim.c", arm / "shim.o"
                commands.append(call([args.clang, "-c", "-O2", "-fno-lto", "-I", ROOT / "frozen/runtime",
                                      shim, "-o", shim_object], arm, arm, arm_env)[0])
                files["shim_source"], files["shim_object"] = identity(shim), identity(shim_object)
                objects.append(shim_object)
        else:
            source = ROOT / "task.f90"
            argv = [args.gfortran, "-c", "-O3", "-march=native", "-ffp-contract="+fp_contract,
                    "-fopenmp-simd", "-cpp", "-fno-lto", "-I", ROOT / "frozen/runtime", "-J", arm]
            if regime == "licensed":
                argv.append("-DBENCH_LICENSED")
            if route == "expert":
                argv.append("-DB07_EXPERT")
            if prepared:
                argv.append("-DB07_PREPARED_MUTATION")
            argv += [source, "-o", task_object]
            linker = args.gfortran
        commands.append(call(argv, arm, arm, arm_env)[0])
        files["task_source"] = identity(source)
        files["task_object"], files["driver_object"] = identity(task_object), identity(driver_object)
        executable = arm / "boundary.exe"
        link = [linker, "-fno-lto", *objects, "-o", executable]
        if route == "cpp":
            link.append("-fopenmp")
        commands.append(call(link, arm, arm, arm_env)[0])
        files["executable"] = identity(executable)
        inventory = []
        for binary in [*objects, executable]:
            for flag in ("-f", "-h", "-d"):
                _, log = call([args.objdump, flag, "-C", binary], arm, arm, arm_env)
                label = binary.stem + "_" + flag[1:] + "_inventory"
                files[label] = identity(log)
                inventory.append(str(log))
        # Exactly one native process/task interval per arm, followed immediately by its numeric gate.
        report = arm / "report.json"
        call([executable, report], arm, arm, arm_env)
        files["report"] = identity(report)
        provenance = {"schema": 1, "contract": adapt.CONTRACT, "route": route, "regime": regime,
                      "prepared_mutation": prepared, "files": files, "commands": commands,
                      "compiler_versions": versions, "compiler_identities": compiler_identities,
                      "environment": {k: arm_env[k] for k in ("PATH", "TEMP", "TMP", "OMP_NUM_THREADS", "OMP_DYNAMIC")},
                      "native_inventory": inventory,
                      "performance_eligible": False, "native_review": "unmeasured"}
        provenance_path = arm / "provenance.json"
        provenance_path.write_text(json.dumps(provenance, indent=2)+"\n", encoding="utf-8")
        validation = [sys.executable, ROOT / "validate.py", "--report", report, "--regime", regime,
                      "--provenance", provenance_path, "--out", arm / "numeric-validation.json"]
        if prepared:
            validation.append("--prepared-mutation")
        call(validation, arm, arm, arm_env)
        completed.append(name)
        (args.out / "stage-status.json").write_text(json.dumps({"completed": completed, "native_processes": len(completed),
            "task_calls": len(completed), "timed_intervals": len(completed), "prepared_mutation": prepared,
            "native_review": "unmeasured", "performance_eligible": False}, indent=2)+"\n", encoding="utf-8")
    # Missing manual native review deliberately remains unmeasured. No automatic acceptance.


if __name__ == "__main__":
    main()
