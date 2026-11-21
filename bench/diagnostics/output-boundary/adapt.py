"""B07 exact-artifact adaptation; runner only. This is not a Blade emitter."""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CONTRACT = "B07-PM52-opaque-task-v1"
MANIFEST_SHA = "56b53905e7a28d96b1ae4f1a3257ea47bece002baa1d26f7fe970edfd83082c9"
PROGRAMS = {
    ("cpp", "licensed"): "104e53156dcd3dd716d8b6d263ab6ddbe8198a49d08c32119783b7d58a570386",
    ("cpp", "repro"): "5f23272c53b3e4f3c6b4dfa1e1b88fac9fd82e0983d13a76e9f3c97fefa22d94",
    ("llvm", "licensed"): "cb893595eb117cbb59207d4c17c685c6232c39844152d98e1c194bb45354f73d",
    ("llvm", "repro"): "6d8f5e274e5225a7c21f3d5571a7715ed71ec76289b8164fee96a573d63ef0c5",
}


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def once(text, marker):
    if text.count(marker) != 1:
        raise ValueError("unknown structure: " + repr(marker))
    return text.split(marker)


def verify_bundle(root=ROOT):
    raw = (root / "frozen-manifest.json").read_bytes()
    if sha(raw) != MANIFEST_SHA:
        raise ValueError("unknown frozen manifest")
    manifest = json.loads(raw)
    if manifest["contract"] != CONTRACT or manifest["n"] != 2053:
        raise ValueError("wrong frozen contract")
    actual = {p.relative_to(root).as_posix() for p in (root / "frozen").rglob("*") if p.is_file()}
    if actual != set(manifest["files"]):
        raise ValueError("missing or extra frozen file")
    for name, expected in manifest["files"].items():
        path = (root / name).resolve()
        if not path.is_relative_to((root / "frozen").resolve()) or sha(path.read_bytes()) != expected:
            raise ValueError("frozen identity mismatch: " + name)
    return manifest


CPP_CATCH = '''    } catch (const std::exception& e) { blade_rt::panic_exception(e); }
      catch (...) { blade_rt::panic("BL8005", "unknown exception", nullptr, 0); }
}'''


def cpp_task(text, prepared):
    head, main = once(text, "int main() {\n")
    setup, main = once(main, "    try {\n")
    if setup != ('    cout << std::setprecision(15);\n    cout << std::boolalpha;\n'
                 '    blade_rt::typed_float_print(cout);\n    auto __blade_t0 = TIME;\n\n'):
        raise ValueError("unknown C++ entry scaffold")
    body, tail = once(main, "    auto __blade_t1 = TIME;\n")
    if not tail.endswith("    return 0;\n" + CPP_CATCH):
        raise ValueError("unknown C++ failure scaffold")
    if body.count("    double input_checksum = (__v70 + __v71);\n") != 1:
        raise ValueError("missing completed input checksum")
    marker = "    Array<double, 1> p = producer(x, y);\n"
    before, after = once(body, marker)
    canonical_body = body
    if prepared:
        body = before + "    b07_mutate_prepared(x.data,y.data,2053);\n" + marker + after
    task = ('#include "b07_abi.h"\n' + head +
            'extern "C" int b07_pm52_task(B07Result* out) {\n'
            '    if (!b07_can_begin(out)) return B07_BAD_STATE;\n    try {\n' + body +
            '    b07_publish(out,x.data,y.data,p.data,q.data,2053,checksum,input_checksum);\n'
            '    return B07_OK;\n' + CPP_CATCH + '\n'
            'extern "C" int64_t b07_task_size() { return sizeof(B07Result); }\n'
            'extern "C" int b07_pm52_cleanup(B07Result* out) {\n'
            '    if (!b07_validate_owned(out)) return B07_BAD_STATE;\n'
            '    const size_t ext[1]={2053};\n'
            '    nested_array_utilities::deallocate<double*,nullptr>(out->x,ext);\n'
            '    nested_array_utilities::deallocate<double*,nullptr>(out->y,ext);\n'
            '    nested_array_utilities::deallocate<double*,nullptr>(out->p,ext);\n'
            '    nested_array_utilities::deallocate<double*,nullptr>(out->q,ext);\n'
            '    b07_mark_released(out); return B07_OK;\n}\n')
    return task, head + canonical_body


LLVM_DECL = '''
%b07_result = type { i64, i64, ptr, ptr, ptr, ptr, double, double, i32, i32 }
declare i32 @b07_can_begin(ptr)
declare i32 @b07_validate_owned(ptr)
declare void @b07_publish(ptr, ptr, ptr, ptr, ptr, i64, double, double)
declare void @b07_mark_released(ptr)
declare void @blade_free(ptr)
'''
LLVM_CLEANUP = '''
define i64 @b07_task_size() { ret i64 72 }
define i32 @b07_pm52_cleanup(ptr %out) {
entry:
  %ok = call i32 @b07_validate_owned(ptr %out)
  %yes = icmp ne i32 %ok, 0
  br i1 %yes, label %release, label %bad
bad:
  ret i32 1
release:
  %xp = getelementptr %b07_result, ptr %out, i32 0, i32 2
  %yp = getelementptr %b07_result, ptr %out, i32 0, i32 3
  %pp = getelementptr %b07_result, ptr %out, i32 0, i32 4
  %qp = getelementptr %b07_result, ptr %out, i32 0, i32 5
  %x = load ptr, ptr %xp
  %y = load ptr, ptr %yp
  %p = load ptr, ptr %pp
  %q = load ptr, ptr %qp
  call void @blade_free(ptr %x)
  call void @blade_free(ptr %y)
  call void @blade_free(ptr %p)
  call void @blade_free(ptr %q)
  call void @b07_mark_released(ptr %out)
  ret i32 0
}
'''


def llvm_task(text, prepared):
    head, main = once(text, "define i32 @main() #0 {\nentry:\n")
    body, tail = once(main, "  %r63 = call double @blade_now()\n")
    discarded, attrs = once(tail, "}\n\nattributes #0 = ")
    if "ret i32 0" not in discarded or " phi " in body:
        raise ValueError("unknown LLVM entry/control structure")
    before, after = once(body, "  %r1 = call double @blade_now()\n")
    body = before + after
    canonical_body = body
    marker = "  %r4 = call ptr @bl_producer_21(ptr %r2, ptr %r3)\n"
    before, after = once(body, marker)
    if prepared:
        body = before + "  call void @b07_mutate_prepared(ptr %r2, ptr %r3, i64 2053)\n" + marker + after
    if body.count("  store double %r61, ptr %r62\n") != 1:
        raise ValueError("missing completed LLVM input checksum")
    decl = LLVM_DECL + ("declare void @b07_mutate_prepared(ptr, ptr, i64)\n" if prepared else "")
    task = (head + decl + 'define i32 @b07_pm52_task(ptr %b07_out) {\nentry:\n'
            '  %b07_ok = call i32 @b07_can_begin(ptr %b07_out)\n'
            '  %b07_yes = icmp ne i32 %b07_ok, 0\n'
            '  br i1 %b07_yes, label %b07_run, label %b07_bad\nb07_bad:\n  ret i32 1\nb07_run:\n' + body +
            '  %b07_cs = load double, ptr %r24\n  %b07_ics = load double, ptr %r62\n'
            '  call void @b07_publish(ptr %b07_out, ptr %r2, ptr %r3, ptr %r4, ptr %r5, i64 2053, double %b07_cs, double %b07_ics)\n'
            '  ret i32 0\n}\n' + LLVM_CLEANUP + '\nattributes #0 = ' + attrs)
    return task, head + canonical_body


def transform(raw, kind, regime, prepared=False):
    if sha(raw) != PROGRAMS.get((kind, regime)):
        raise ValueError("unknown source artifact")
    text = raw.decode("utf-8").replace("\r\n", "\n")
    adapted, prefix = (cpp_task if kind == "cpp" else llvm_task)(text, prepared)
    return adapted.encode("utf-8"), sha(prefix.encode("utf-8"))


def absolute(value):
    p = Path(value)
    if not p.is_absolute():
        raise argparse.ArgumentTypeError("absolute path required")
    return p.resolve()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kind", choices=["cpp", "llvm"], required=True)
    parser.add_argument("--regime", choices=["licensed", "repro"], required=True)
    parser.add_argument("--out-dir", type=absolute, required=True)
    parser.add_argument("--prepared-mutation", action="store_true", help="TESTING ONLY, distinct task identity")
    args = parser.parse_args()
    verify_bundle()
    ext = "cpp" if args.kind == "cpp" else "ll"
    origin = ROOT / "frozen" / f"{args.kind}-{args.regime}.{ext}"
    raw = origin.read_bytes()
    task, prefix = transform(raw, args.kind, args.regime, args.prepared_mutation)
    # Refuse to overwrite previous evidence, including an empty existing directory.
    args.out_dir.mkdir(parents=True, exist_ok=False)
    name = "task-prepared." + ext if args.prepared_mutation else "task." + ext
    (args.out_dir / name).write_bytes(task)
    identity = {"schema": 1, "contract": CONTRACT, "kind": args.kind, "regime": args.regime,
                "prepared_mutation": args.prepared_mutation, "origin": str(origin),
                "origin_sha256": sha(raw), "manifest_sha256": MANIFEST_SHA,
                "adapted_file": name, "adapted_sha256": sha(task), "canonical_prefix_sha256": prefix,
                "adapter_sha256": sha(Path(__file__).read_bytes()),
                "native_validation": "unmeasured", "performance_eligible": False}
    (args.out_dir / "adaptation.json").write_text(json.dumps(identity, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
