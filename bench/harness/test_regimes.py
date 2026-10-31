"""mt<N> regime fixtures (plan-cloud-bench-campaign.md 4.1). Runner only:
python -m unittest discover -s bench/harness -p test_regimes.py.

No compiler, child executable or benchmark is invoked; the physical-core probe is mocked.
"""
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import run as harness

# The box's matrix, as REGIME_ORDER was spelled before thread counts became configurable.
BOX_ORDER = [
    "serial-licensed-native", "mt8-licensed-native", "serial-repro-native", "mt8-repro-native",
    "serial-fastmath-native", "mt8-fastmath-native",
    "serial-licensed-blas", "mt8-licensed-blas", "serial-repro-blas", "mt8-repro-blas",
    "serial-fastmath-blas", "mt8-fastmath-blas",
]


def base_config() -> dict:
    return json.loads(harness.CONFIG_FILE.read_text(encoding="utf-8-sig"))


def write_config(cfg: dict) -> Path:
    d = Path(tempfile.mkdtemp())
    p = d / "config.json"
    p.write_text(json.dumps(cfg), encoding="utf-8")
    return p


def write_kernel(raw: dict) -> harness.Kernel:
    d = Path(tempfile.mkdtemp()) / raw["name"]
    d.mkdir()
    (d / "kernel.json").write_text(json.dumps(raw), encoding="utf-8")
    return harness.load_kernel(d)


KERNEL = {
    "name": "k1", "sizes": {"smoke": {"n": 3}, "full": {"n": 7}},
    "regimes": ["serial-licensed-native", "mt8-licensed-native", "serial-repro-native", "mt8-repro-native"],
    "rounds": {"mt8": 9},
    "regime_arms": {"mt8-licensed-native": ["blade-cpp", "gfortran-expert"]},
    "no_parallel_form": ["mt8-repro-native"],
}


class RegimeParseTests(unittest.TestCase):
    def test_serial_and_mt_n(self):
        s = harness.Regime.parse("serial-licensed-native")
        self.assertEqual((s.threaded, s.n_threads, s.thread_class, s.family), (False, 1, "serial", "serial-licensed-native"))
        for n in (1, 8, 32, 128):
            r = harness.Regime.parse(f"mt{n}-repro-blas")
            self.assertEqual((r.threaded, r.n_threads, r.thread_class, r.family), (True, n, "mt", "mt-repro-blas"))
            self.assertEqual(r.name, f"mt{n}-repro-blas")
            self.assertEqual(r.serial_twin().name, "serial-repro-blas")
        self.assertEqual(s.with_threads(16).name, "mt16-licensed-native")

    def test_bad_names_are_refused(self):
        for bad in ("mt0-licensed-native", "mt-licensed-native", "mt08-licensed-native", "threads-licensed-native",
                    "mt8-fast-native", "mt8-licensed-mkl", "mt8-licensed", "serial"):
            with self.assertRaises(harness.HarnessError, msg=bad):
                harness.Regime.parse(bad)

    def test_family_of_normalizes_legacy_mt8(self):
        self.assertEqual(harness.Regime.family_of("mt8-licensed-native"), "mt-licensed-native")
        self.assertEqual(harness.Regime.family_of("mt32-fastmath-blas"), "mt-fastmath-blas")
        self.assertEqual(harness.Regime.family_of("mt-repro-blas"), "mt-repro-blas")
        self.assertEqual(harness.Regime.family_of("serial-repro-native"), "serial-repro-native")
        for bad in ("mtx-licensed-native", "mt0-licensed-native", "omp-licensed-native"):
            with self.assertRaises(harness.HarnessError, msg=bad):
                harness.Regime.family_of(bad)

    def test_expand_one_regime_per_thread_count(self):
        names = [r.name for r in harness.Regime.expand(["serial-licensed-native", "mt8-licensed-native"], [1, 8, 32])]
        self.assertEqual(names, ["serial-licensed-native", "mt1-licensed-native", "mt8-licensed-native", "mt32-licensed-native"])

    def test_default_order_is_the_box_matrix(self):
        self.assertEqual(harness.regime_order([8]), BOX_ORDER)
        self.assertEqual(harness.REGIME_ORDER, BOX_ORDER)
        self.assertEqual(harness.regime_order([2, 16]),
                         ["serial-licensed-native", "mt2-licensed-native", "mt16-licensed-native",
                          "serial-repro-native", "mt2-repro-native", "mt16-repro-native",
                          "serial-fastmath-native", "mt2-fastmath-native", "mt16-fastmath-native",
                          "serial-licensed-blas", "mt2-licensed-blas", "mt16-licensed-blas",
                          "serial-repro-blas", "mt2-repro-blas", "mt16-repro-blas",
                          "serial-fastmath-blas", "mt2-fastmath-blas", "mt16-fastmath-blas"])

    def test_sort_key_orders_any_n(self):
        names = ["mt64-licensed-native", "serial-licensed-blas", "mt16-licensed-native", "serial-licensed-native",
                 "mt8-repro-native", "mt8-licensed-native"]
        self.assertEqual(sorted(names, key=harness.regime_sort_key),
                         ["serial-licensed-native", "mt8-licensed-native", "mt16-licensed-native", "mt64-licensed-native",
                          "mt8-repro-native", "serial-licensed-blas"])
        self.assertEqual(sorted(BOX_ORDER[::-1], key=harness.regime_sort_key), BOX_ORDER)


class ConfigTests(unittest.TestCase):
    def test_committed_config_loads_with_threads_8(self):
        cfg = harness.load_config()
        self.assertEqual(cfg["threads"], [8])
        self.assertIn("mt", cfg["run_env"])
        self.assertNotIn("mt8", cfg["run_env"])
        self.assertEqual(cfg["run_env"]["mt"]["OMP_NUM_THREADS"], "{n}")
        for comp in ("gfortran", "ifx"):
            self.assertIn("mt_extra", cfg["fortran_flags"][comp])

    def test_threads_absent_defaults_to_8(self):
        cfg = base_config()
        cfg.pop("threads", None)
        self.assertEqual(harness.load_config(write_config(cfg))["threads"], [8])

    def test_threads_list_is_deduplicated_and_sorted_and_all_resolves(self):
        cfg = base_config()
        cfg["threads"] = [32, 8, 8, "all", 1]
        with patch.object(harness, "physical_core_count", return_value=48):
            self.assertEqual(harness.load_config(write_config(cfg))["threads"], [1, 8, 32, 48])

    def test_bad_threads_entries_are_refused(self):
        for bad in ([], [0], [8, "most"], [True], "8", [8.0]):
            cfg = base_config()
            cfg["threads"] = bad
            with self.assertRaises(harness.HarnessError, msg=repr(bad)):
                harness.load_config(write_config(cfg))
        cfg = base_config()
        cfg["threads"] = ["all"]
        with patch.object(harness, "physical_core_count", return_value=None):
            with self.assertRaises(harness.HarnessError):
                harness.load_config(write_config(cfg))

    def test_legacy_mt8_keys_are_read_as_mt(self):
        cfg = base_config()
        cfg["run_env"]["mt8"] = cfg["run_env"].pop("mt")
        for comp in ("gfortran", "ifx"):
            cfg["fortran_flags"][comp]["mt8_extra"] = cfg["fortran_flags"][comp].pop("mt_extra")
        loaded = harness.load_config(write_config(cfg))
        self.assertIn("mt", loaded["run_env"])
        self.assertEqual(loaded["fortran_flags"]["gfortran"]["mt_extra"], ["-fopenmp"])
        cfg["run_env"]["mt"] = dict(cfg["run_env"]["mt8"])     # both spellings = ambiguous
        with self.assertRaises(harness.HarnessError):
            harness.load_config(write_config(cfg))

    def test_run_knobs_substitute_n(self):
        cfg = harness.load_config()
        mt16 = harness.run_knobs(harness.Regime.parse("mt16-licensed-native"), cfg)
        self.assertEqual((mt16["OMP_NUM_THREADS"], mt16["OPENBLAS_NUM_THREADS"], mt16["OMP_DYNAMIC"]), ("16", "16", "FALSE"))
        self.assertEqual(harness.run_knobs(harness.Regime.parse("mt8-repro-blas"), cfg)["OMP_NUM_THREADS"], "8")
        serial = harness.run_knobs(harness.Regime.parse("serial-licensed-native"), cfg)
        self.assertEqual((serial["OMP_NUM_THREADS"], serial["OPENBLAS_NUM_THREADS"]), ("1", "1"))
        self.assertFalse(any(k.startswith("_") for k in mt16))

    def test_fixed_thread_count_disagreeing_with_the_regime_is_refused(self):
        cfg = harness.load_config()
        cfg["run_env"]["mt"]["OMP_NUM_THREADS"] = "8"
        self.assertEqual(harness.run_knobs(harness.Regime.parse("mt8-licensed-native"), cfg)["OMP_NUM_THREADS"], "8")
        with self.assertRaises(harness.HarnessError):
            harness.run_knobs(harness.Regime.parse("mt16-licensed-native"), cfg)

    def test_fortran_flags_pick_the_thread_class_extra(self):
        cfg = harness.load_config()
        mt32 = harness.fortran_flags("gfortran", harness.Regime.parse("mt32-licensed-native"), cfg, twin=False)
        serial = harness.fortran_flags("gfortran", harness.Regime.parse("serial-licensed-native"), cfg, twin=False)
        self.assertIn("-fopenmp", mt32)
        self.assertNotIn("-fopenmp-simd", mt32)
        self.assertIn("-fopenmp-simd", serial)
        self.assertNotIn("-fopenmp", serial)
        self.assertIn("/Qopenmp", harness.fortran_flags("ifx", harness.Regime.parse("mt2-repro-native"), cfg, twin=False))

    def test_fingerprint_run_env_records_thread_counts(self):
        cfg = harness.load_config()
        cfg["threads"] = [1, 8, 32]
        fp: dict = {}
        harness.record_run_env(fp, cfg)
        self.assertEqual(fp["thread_counts"], [1, 8, 32])
        self.assertEqual(set(fp["run_env"]), {"serial", "mt"})
        self.assertEqual(fp["mt_binding"], "unbound")


class KernelTests(unittest.TestCase):
    def test_kernel_json_declares_families(self):
        k = write_kernel(KERNEL)
        self.assertEqual(k.regimes, ["serial-licensed-native", "mt-licensed-native", "serial-repro-native", "mt-repro-native"])
        self.assertEqual(k.rounds, {"mt": 9})
        self.assertEqual(k.no_parallel_form, ["mt-repro-native"])
        self.assertEqual(list(k.regime_arms), ["mt-licensed-native"])
        for name, declared in (("mt32-licensed-native", True), ("mt8-repro-native", True), ("mt1-licensed-native", True),
                               ("serial-repro-native", True), ("mt8-fastmath-native", False), ("serial-licensed-blas", False)):
            self.assertEqual(k.declares(harness.Regime.parse(name)), declared, name)

    def test_regime_arms_and_parallel_form_follow_the_family(self):
        k = write_kernel(KERNEL)
        self.assertEqual(k.arms_in(harness.Regime.parse("mt16-licensed-native")), ["blade-cpp", "gfortran-expert"])
        self.assertEqual(k.arms_in(harness.Regime.parse("serial-licensed-native")), harness.BASE_ARMS)
        self.assertFalse(k.blade_has_parallel_form(harness.Regime.parse("mt4-repro-native")))
        self.assertTrue(k.blade_has_parallel_form(harness.Regime.parse("mt4-licensed-native")))
        self.assertTrue(k.blade_has_parallel_form(harness.Regime.parse("serial-repro-native")))
        every = write_kernel({**KERNEL, "name": "k2", "no_parallel_form": True})
        self.assertFalse(every.blade_has_parallel_form(harness.Regime.parse("mt64-licensed-native")))
        self.assertTrue(every.blade_has_parallel_form(harness.Regime.parse("serial-licensed-native")))

    def test_bad_rounds_key_is_refused(self):
        with self.assertRaises(harness.HarnessError):
            write_kernel({**KERNEL, "name": "k3", "rounds": {"mt16": 9}})

    def test_every_committed_kernel_loads_as_families(self):
        kernels = harness.discover(harness.KERNELS_DIR)
        self.assertTrue(kernels)
        for k in kernels:
            for fam in k.regimes:
                self.assertEqual(fam, harness.Regime.family_of(fam), f"{k.name}: {fam}")
            self.assertTrue(set(k.rounds) <= {"serial", "mt"}, k.name)


class SessionSelectionTests(unittest.TestCase):
    def fake(self, regimes, mode="bench", threads=(8,)):
        return SimpleNamespace(opt=SimpleNamespace(regimes=regimes, mode=mode, config={"threads": list(threads)}))

    def test_bench_expands_declared_families_over_the_config_threads(self):
        k = write_kernel(KERNEL)
        names = [r.name for r in harness.Session.regimes_for(self.fake(None, threads=(1, 8, 32)), k)]
        self.assertEqual(names, ["serial-licensed-native", "mt1-licensed-native", "mt8-licensed-native", "mt32-licensed-native",
                                 "serial-repro-native", "mt1-repro-native", "mt8-repro-native", "mt32-repro-native"])
        self.assertEqual([r.name for r in harness.Session.regimes_for(self.fake(None), k)],
                         ["serial-licensed-native", "mt8-licensed-native", "serial-repro-native", "mt8-repro-native"])

    def test_explicit_regimes_keep_only_declared_families(self):
        k = write_kernel(KERNEL)
        want = [harness.Regime.parse(n) for n in ("mt32-licensed-native", "mt32-fastmath-native", "serial-repro-native")]
        self.assertEqual([r.name for r in harness.Session.regimes_for(self.fake(want), k)],
                         ["mt32-licensed-native", "serial-repro-native"])

    def test_smoke_runs_the_default_regime_only(self):
        k = write_kernel(KERNEL)
        self.assertEqual([r.name for r in harness.Session.regimes_for(self.fake(None, mode="smoke", threads=(1, 64)), k)],
                         [harness.DEFAULT_SMOKE_REGIME])


if __name__ == "__main__":
    unittest.main()
