// Blade run record: what a compiled program observed and how it ended,
// written as one JSON object when the process exits (normally, or through
// the runtime's failure exit), to the path in BLADE_RUN_RECORD. Unset means
// no record and no cost beyond a getenv at exit. Host-only; device passes
// see nothing.
//
// The record pairs the COMPILE-TIME manifest codegen bakes into the program
// (docs/plans/plan-fortran-killer-2.md section 7: logical input name,
// provider, path, variable, element type, axes, storage interpretation,
// units, identity policy, and for compile-time-folded inputs the content
// hash the fold was taken over) with what the RUN observed: each input's
// existence, kind, size and modification time; the executable's own size and
// modification time; the toolchain (BLADE_RR_COMPILER) and the FP / library
// policy the build was compiled under (the BLADE_RR_* defines Build.fs
// passes, so the emitted .cpp itself carries no environment); the RNG
// generator when the program draws; the runtime FORM CENSUS (`forms`: per
// site whose form is chosen at run time, the form it executed and the
// requested / actual team -- note_form); and the completion status -- ok, or
// the BLxxxx code and message of the failure that ended the run.
//
// "Observed identity" is version identity (size + mtime), not content
// identity: hashing a multi-gigabyte store at exit is not a cost a run
// record may impose silently. A content hash is recorded only for inputs the
// COMPILER folded (it read every byte anyway), where it is the identity the
// executable's values depend on. Equal sizes and times on a different grid
// are not detected; coordinate identity is the manifest's business.
//
// This header must not name the runtime's failure exit function: the
// shadow-frame elision analysis classifies runtime headers by the text they
// contain (tests/Test_Diagnostics.fs's tripwire). It reads the status the
// runtime leaves behind instead.
//
// Deliberately light: this header is in EVERY program's include list, so it
// pulls no <filesystem>, <chrono>, <string> or <windows.h>, and the writer is
// cold C stdio code -- a std::string-building writer cost g++ ~0.4 s of -O3
// per program (measured on a two-line program: 1.15 s vs 0.76 s). File
// observation is POSIX/MinGW stat(); the executable's own path is one
// declared Win32 entry point or /proc/self/exe.
#pragma once
#if !defined(__CUDA_ARCH__)
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <sys/types.h>
#include <sys/stat.h>
#include "blade_runtime.hpp"
#if defined(_WIN32)
extern "C" __declspec(dllimport) unsigned long __stdcall GetModuleFileNameA(void* module, char* out, unsigned long size);
#else
#include <unistd.h>
#endif

#ifndef BLADE_RR_MARCH
#define BLADE_RR_MARCH unknown
#endif
#ifndef BLADE_RR_FPC
#define BLADE_RR_FPC unknown
#endif
#ifndef BLADE_RR_REASSOC
#define BLADE_RR_REASSOC 0
#endif
#ifndef BLADE_RR_BLAS
#define BLADE_RR_BLAS 0
#endif
#ifndef BLADE_RR_LAPACK
#define BLADE_RR_LAPACK 0
#endif
#ifndef BLADE_RR_CUBLAS
#define BLADE_RR_CUBLAS 0
#endif
#define BLADE_RR_STR_(x) #x
#define BLADE_RR_STR(x) BLADE_RR_STR_(x)
// The compiler identity the record names. GCC and clang spell it __VERSION__
// (the g++ record text is that string, unchanged); MSVC -- the host compiler
// nvcc drives for the Windows CUDA paths -- has no __VERSION__ and names
// itself through _MSC_FULL_VER (and _MSC_BUILD, the fourth component).
#if defined(__VERSION__)
#define BLADE_RR_COMPILER __VERSION__
#elif defined(_MSC_FULL_VER) && defined(_MSC_BUILD)
#define BLADE_RR_COMPILER "MSVC " BLADE_RR_STR(_MSC_FULL_VER) "." BLADE_RR_STR(_MSC_BUILD)
#elif defined(_MSC_FULL_VER)
#define BLADE_RR_COMPILER "MSVC " BLADE_RR_STR(_MSC_FULL_VER)
#else
#define BLADE_RR_COMPILER "unknown"
#endif
#if defined(__GNUC__)
#define BLADE_RR_COLD __attribute__((noinline, cold))
#else
#define BLADE_RR_COLD
#endif

namespace blade_rr {

  // One manifest row. `axes` is a ready JSON array text; `content_hash` is
  // empty for runtime-read inputs.
  struct Input {
    const char* name;
    const char* provider;
    const char* path;
    const char* variable;
    const char* elem;
    const char* axes;
    const char* storage;
    const char* units;
    const char* identity;
    const char* content_hash;
  };

  // THE RUNTIME FORM CENSUS (round 3 D-P3; Codex diff review of W2-3,
  // finding 1). A site whose EXECUTED form is chosen at run time records the
  // form it ran together with the requested and the ACTUAL team, so the record
  // says which execution occurred -- `blade plan`'s counts are the planner's
  // static facts (what the build COULD run), never an observation. Today's
  // only such site is the rank-3 orbit scatter's leaf stage
  // (blade_orbit_scatter.hpp pull3): `row-pull` (the row-owned pull on an
  // actual team of more than one) or `serial-leaf3` (the serial slice form: a
  // requested team of one, which opens no team, or an opened team the runtime
  // made a team of one -- OMP_THREAD_LIMIT, an inactive nested region). One
  // row per distinct (site, form, requested, actual), its `calls` counting
  // repeats; the first 64 distinct rows are kept and the rest counted in
  // `forms_dropped`. Called by the thread that called the site, after its
  // team (if any) has joined; a spin flag serializes concurrent callers.
  struct FormEvent { const char* site; const char* form; int requested; int actual; long long calls; };
  constexpr int form_event_cap = 64;
  inline FormEvent form_events[form_event_cap] = {};
  inline int n_form_events = 0;
  inline long long form_events_dropped = 0;
  inline std::atomic_flag form_lock = ATOMIC_FLAG_INIT;
  inline void note_form(const char* site, const char* form, int requested, int actual) {
    while (form_lock.test_and_set(std::memory_order_acquire)) {}
    int i = 0;
    for (; i < n_form_events; ++i) {
      const FormEvent& e = form_events[i];
      if (e.requested == requested && e.actual == actual
          && std::strcmp(e.site, site) == 0 && std::strcmp(e.form, form) == 0) break;
    }
    if (i < n_form_events) form_events[i].calls++;
    else if (n_form_events < form_event_cap) form_events[n_form_events++] = FormEvent{ site, form, requested, actual, 1 };
    else form_events_dropped++;
    form_lock.clear(std::memory_order_release);
  }

  BLADE_RR_COLD inline void json_string(std::FILE* f, const char* s) {
    std::fputc('"', f);
    for (const char* p = s ? s : ""; *p; ++p) {
      unsigned char c = (unsigned char)*p;
      if (c == '"') std::fputs("\\\"", f);
      else if (c == '\\') std::fputs("\\\\", f);
      else if (c == '\n') std::fputs("\\n", f);
      else if (c == '\r') std::fputs("\\r", f);
      else if (c == '\t') std::fputs("\\t", f);
      else if (c < 0x20) std::fprintf(f, "\\u%04x", c);
      else std::fputc(c, f);
    }
    std::fputc('"', f);
  }

  // size + mtime (Unix seconds) of a path, as JSON; a directory (a zarr /
  // icechunk store) reports its kind and mtime without a size.
  BLADE_RR_COLD inline void observe(std::FILE* f, const char* path) {
    struct stat st;
    if (!path || stat(path, &st) != 0) { std::fputs("{\"exists\":false}", f); return; }
    bool dir = (st.st_mode & S_IFMT) == S_IFDIR;
    std::fputs(dir ? "{\"exists\":true,\"kind\":\"directory\"" : "{\"exists\":true,\"kind\":\"file\"", f);
    if (!dir) std::fprintf(f, ",\"size\":%lld", (long long)st.st_size);
    std::fprintf(f, ",\"mtime\":%lld}", (long long)st.st_mtime);
  }

  BLADE_RR_COLD inline void self_path(char* buf, unsigned long size) {
    buf[0] = '\0';
#if defined(_WIN32)
    unsigned long n = GetModuleFileNameA(nullptr, buf, size);
    if (n == 0 || n >= size) buf[0] = '\0';
#else
    long n = readlink("/proc/self/exe", buf, size - 1);
    if (n > 0) buf[n] = '\0'; else buf[0] = '\0';
#endif
  }

  // Assemble and write the record. `rank` is the MPI rank (0 for a
  // non-MPI program); only rank 0 writes.
  BLADE_RR_COLD inline void write(const char* program, const char* blade_version, bool uses_rng,
                                  const Input* inputs, int n_inputs, int rank) {
    const char* dest = std::getenv("BLADE_RUN_RECORD");
    if (!dest || !*dest || rank != 0) return;
    std::FILE* f = std::fopen(dest, "wb");
    if (!f) return;
    std::fputs("{\"blade_run_record\":1,\"program\":", f);
    json_string(f, program);
    std::fputs(",\"status\":", f);
    if (blade_rt::exit_code[0] == '\0') std::fputs("{\"ok\":true}", f);
    else {
      std::fputs("{\"ok\":false,\"code\":", f);
      json_string(f, blade_rt::exit_code);
      std::fputs(",\"message\":", f);
      json_string(f, blade_rt::exit_message);
      std::fputc('}', f);
    }
    char self[4096];
    self_path(self, sizeof self);
    std::fputs(",\"executable\":{\"path\":", f);
    json_string(f, self);
    std::fputs(",\"observed\":", f);
    observe(f, self);
    std::fputs(",\"compiler\":", f);
    json_string(f, BLADE_RR_COMPILER);
    std::fputs(",\"blade\":", f);
    json_string(f, blade_version);
    std::fputs("},\"policy\":{\"march\":", f);
    json_string(f, BLADE_RR_STR(BLADE_RR_MARCH));
    std::fputs(",\"fp_contract\":", f);
    json_string(f, BLADE_RR_STR(BLADE_RR_FPC));
    std::fprintf(f, ",\"fp_reassoc\":%s},\"routes\":{\"blas\":%s,\"lapack\":%s,\"cublas\":%s},\"rng\":%s",
                 BLADE_RR_REASSOC ? "true" : "false",
                 BLADE_RR_BLAS ? "true" : "false",
                 BLADE_RR_LAPACK ? "true" : "false",
                 BLADE_RR_CUBLAS ? "true" : "false",
                 uses_rng ? "{\"generator\":\"philox4x32-10\",\"version\":1}" : "null");
    // the runtime form census (observed, per site; see note_form)
    std::fputs(",\"forms\":[", f);
    for (int i = 0; i < n_form_events; ++i) {
      const FormEvent& e = form_events[i];
      if (i) std::fputc(',', f);
      std::fputs("{\"site\":", f); json_string(f, e.site);
      std::fputs(",\"form\":", f); json_string(f, e.form);
      std::fprintf(f, ",\"requested\":%d,\"actual\":%d,\"calls\":%lld}", e.requested, e.actual, e.calls);
    }
    std::fputc(']', f);
    if (form_events_dropped > 0) std::fprintf(f, ",\"forms_dropped\":%lld", form_events_dropped);
    std::fputs(",\"inputs\":[", f);
    for (int i = 0; i < n_inputs; ++i) {
      const Input& in = inputs[i];
      if (i) std::fputc(',', f);
      std::fputs("{\"name\":", f); json_string(f, in.name);
      std::fputs(",\"provider\":", f); json_string(f, in.provider);
      std::fputs(",\"path\":", f); json_string(f, in.path);
      std::fputs(",\"variable\":", f); json_string(f, in.variable);
      std::fputs(",\"elem\":", f); json_string(f, in.elem);
      std::fputs(",\"axes\":", f); std::fputs(in.axes, f);
      std::fputs(",\"storage\":", f); json_string(f, in.storage);
      std::fputs(",\"units\":", f);
      if (in.units && *in.units) json_string(f, in.units); else std::fputs("null", f);
      std::fputs(",\"identity\":", f); json_string(f, in.identity);
      if (in.content_hash && *in.content_hash) { std::fputs(",\"content_hash\":", f); json_string(f, in.content_hash); }
      std::fputs(",\"observed\":", f); observe(f, in.path);
      std::fputc('}', f);
    }
    std::fputs("]}\n", f);
    std::fclose(f);
  }

  // A file-scope instance of this writes the record: from its destructor when
  // main returns, and -- the runtime's failure exit leaves through
  // std::_Exit, which runs no static destructors -- from the failure-exit
  // hook its `hook` member registers. Exactly one of the two runs. `rank` is read through a pointer so an MPI program's rank global
  // (assigned in main) is seen as it is at exit.
  struct AtExit;
  struct AtExitHook { explicit AtExitHook(const AtExit* self); };
  struct AtExit {
    const char* program; const char* blade_version; bool uses_rng;
    const Input* inputs; int n_inputs; const int* rank;
    // A default member initializer, so the emitted aggregate initialization
    // (Blade.RunRecord.cppLines) is unchanged.
    AtExitHook hook{this};
    void write_now() const { write(program, blade_version, uses_rng, inputs, n_inputs, rank ? *rank : 0); }
    ~AtExit() { write_now(); }
  };
  inline const AtExit* registered = nullptr;
  BLADE_RR_COLD inline void write_on_failure_exit() { if (registered) registered->write_now(); }
  inline AtExitHook::AtExitHook(const AtExit* self) {
    registered = self;
    blade_rt::on_failure_exit(write_on_failure_exit);
  }
}
#endif
