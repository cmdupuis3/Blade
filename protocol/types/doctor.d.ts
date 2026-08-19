// `blade doctor --json` — the toolchain report. Transcribed from
// src/Doctor.fs:351-364 (`renderJson`), with the status spellings from
// `statusJson` at :50-55 and the row keys from `collectChecks` at :327-340.
//
// This is a ONE-SHOT verb, not part of the serve protocol: run it with
// execFile, not through an `ide serve` child.
//
// Note the emitter uses Doctor.fs's own weaker escaper (:347), not
// Ide.jsonEscape — it escapes backslash, quote, CR, LF and tab but not other
// control characters. Detail strings come from tool version banners, so this
// has not bitten in practice.

export type DoctorOs = "windows" | "linux" | "macos";

/**
 * "ok"      the thing works
 * "off"     present but deliberately disabled (an env gate is off)
 * "warn"    usable, with a caveat in `detail`
 * "missing" not installed; optional rows say so in `detail`
 * "error"   present and broken
 */
export type DoctorStatus = "ok" | "off" | "warn" | "missing" | "error";

/** One row of the report. */
export interface DoctorCheck {
  /** Stable machine key. Known values, in emission order: "dotnet", "stdlib",
   *  "gpp", "blas", "lapack", "netcdf", "mpi", "cuda", "llvm", "make",
   *  "gfortran", "git", "coq". Treat as open — rows get added. */
  key: string;
  /** Human row title ("g++ / OpenMP"). */
  title: string;
  status: DoctorStatus;
  /** One line of prose: a version banner, or why it is missing. */
  detail: string;
  /** What configured it ("OPENBLAS_DIR [env]"), or "" for a plain probe. */
  origin: string;
}

export interface DoctorReport {
  os: DoctorOs;
  /** Lowercased process architecture ("x64", "arm64"). */
  arch: string;
  /** True iff the REQUIRED core is up — that is, g++ compiles and runs. Every
   *  other row is optional and never affects this (or the exit code), so do
   *  not read `healthy` as "everything is fine". */
  healthy: boolean;
  checks: DoctorCheck[];
}
