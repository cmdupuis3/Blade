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
// passes, so the emitted .cpp itself carries no environment), plus the pool
// allocator's retention policy (`policy.pool`: blade_pool's block cap and
// per-thread budget in bytes, `default` or the opt-in BLADE_POOL_RETAIN arm --
// the one policy the emitted text carries, as #defines -- and whether the
// cache is compiled in; an ADDITIVE, OPTIONAL member of the version-1 record:
// written by every C++ program since round-3 W2-6, default builds included,
// so a v1 reader must accept a record without it -- older v1 records lack it
// -- and a default record is not byte-identical to a pre-W2-6 one); the RNG
// generator when the program draws; the runtime FORM CENSUS (`forms`: per
// site whose form is chosen at run time, the form it executed and the
// requested / actual team -- note_form; present only when a site recorded
// one); membership hoisting's builds (`member_hoist`, blade_member.h; present
// only when a build ran); and the completion status -- ok, or the BLxxxx code
// and message of the failure that ended the run. A program that records
// neither writes the record format that predates both.
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
// declared Win32 entry point or /proc/self/exe. The declaration is written by
// hand, NOT taken from an SDK header: <libloaderapi.h> drags minwindef.h /
// winnt.h into every program, whose macros (IN, OUT, DELETE, TRUE, far, ...)
// and typedefs (INT, BOOL, DWORD, ...) collide with Blade names. Its parameter
// type is the SDK's own HMODULE (struct HINSTANCE__* under STRICT, the SDK
// default; void* under NO_STRICT), so it redeclares the same function as the
// private spool's <windows.h> in either include order.
#pragma once
#if !defined(__CUDA_ARCH__)
#include <atomic>
// Runtime components that report into the record test for this (blade_member.h).
#define BLADE_RUN_RECORD_HPP 1
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <sys/types.h>
#include <sys/stat.h>
#include "blade_runtime.hpp"
#if defined(_WIN32)
#if defined(NO_STRICT)
extern "C" __declspec(dllimport) unsigned long __stdcall GetModuleFileNameA(void* module, char* out, unsigned long size);
#else
struct HINSTANCE__;
extern "C" __declspec(dllimport) unsigned long __stdcall GetModuleFileNameA(HINSTANCE__* module, char* out, unsigned long size);
#endif
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

  // THE PROVIDER I/O CENSUS (round 4 wave 3, D2 / PM42): a streamed provider
  // site that counts its chunk traffic records, per evaluation, the chunk
  // FETCHES (file reads), the FETCHED bytes (as stored on disk: the
  // compressed size for a codec, the raw size for the identity codec), the
  // DECODED bytes (produced into chunk buffers), the cache HITS (a chunk
  // served without a fetch) and the cache's slots and slot bytes (0 when
  // uncached). Written as `provider_io` ONLY when a site recorded (an
  // additive, optional member of the version-1 record, like `forms`).
  // Serial callers only (the halo run loop's read is serial).
  struct IoEvent { const char* site; const char* var; const char* form;
                   long long fetches; long long fetched_bytes; long long decoded_bytes;
                   long long hits; long long slots; long long slot_bytes; };
  constexpr int io_event_cap = 64;
  inline IoEvent io_events[io_event_cap] = {};
  inline int n_io_events = 0;
  inline long long io_events_dropped = 0;
  inline void note_io(const char* site, const char* var, const char* form,
                      long long fetches, long long fetched_bytes, long long decoded_bytes,
                      long long hits, long long slots, long long slot_bytes) {
    if (n_io_events < io_event_cap)
      io_events[n_io_events++] = IoEvent{ site, var, form, fetches, fetched_bytes, decoded_bytes, hits, slots, slot_bytes };
    else io_events_dropped++;
  }
  // THE PER-WORKER WORKSPACE CENSUS (round 4 wave 3 A4, PM06; compiled in
  // only under BLADE_WS_CENSUS, which the compiler writes for
  // BLADE_WORKSPACE_CENSUS=1). A threaded site brackets its team with
  // ws_site_begin / ws_site_end (called by the thread that runs the site,
  // outside the team); nested_array_utilities.hpp's blade_pool counts every
  // pool allocated or released meanwhile against the OpenMP thread doing it.
  // One row per site: calls, the largest team that allocated, allocations and
  // bytes summed over calls, the largest per-worker live high-water mark
  // (`bytes_per_worker`: the scratch one worker held at once) and the largest
  // sum of the workers' high-water marks over one call (`workspace`: P x the
  // per-worker workspace when the team is uniform). Not thread-safe against a
  // NESTED site (Blade opens no nested team).
#if defined(BLADE_WS_CENSUS) && BLADE_WS_CENSUS
  struct WsEvent { const char* site; long long calls; int max_threads; int workers; long long allocs; long long bytes; long long per_worker; long long workspace; };
  constexpr int ws_event_cap = 64;
  inline WsEvent ws_events[ws_event_cap] = {};
  inline int n_ws_events = 0;
  inline long long ws_events_dropped = 0;
  // A site reached INSIDE another team (a kernel calling a function that holds
  // its own threaded nest) is not bracketed: the outer site already counts it,
  // and resetting the shared counters from every outer worker would race.
  inline bool ws_nested() {
#if defined(_OPENMP)
    return omp_in_parallel() != 0;
#else
    return false;
#endif
  }
  inline void ws_site_begin() {
    if (ws_nested()) return;
    for (int w = 0; w < ::blade_ws::kMaxWorkers; ++w) ::blade_ws::workers[w] = ::blade_ws::Worker{};
    ::blade_ws::open.store(true, std::memory_order_release);
  }
  inline void ws_site_end(const char* site, int max_threads) {
    if (ws_nested()) return;
    ::blade_ws::open.store(false, std::memory_order_release);
    int workers = 0; long long allocs = 0, bytes = 0, per_worker = 0, workspace = 0;
    for (int w = 0; w < ::blade_ws::kMaxWorkers; ++w) {
      const ::blade_ws::Worker& k = ::blade_ws::workers[w];
      if (k.allocs > 0) workers++;
      allocs += k.allocs; bytes += k.bytes; workspace += k.peak;
      if (k.peak > per_worker) per_worker = k.peak;
    }
    int i = 0;
    for (; i < n_ws_events; ++i) if (std::strcmp(ws_events[i].site, site) == 0) break;
    if (i == n_ws_events) {
      if (n_ws_events == ws_event_cap) { ws_events_dropped++; return; }
      ws_events[n_ws_events++] = WsEvent{ site, 0, 0, 0, 0, 0, 0, 0 };
    }
    WsEvent& e = ws_events[i];
    e.calls++;
    if (max_threads > e.max_threads) e.max_threads = max_threads;
    if (workers > e.workers) e.workers = workers;
    e.allocs += allocs; e.bytes += bytes;
    if (per_worker > e.per_worker) e.per_worker = per_worker;
    if (workspace > e.workspace) e.workspace = workspace;
  }
#endif
  // THE PRIVATE SPOOL (blade_private_spool.hpp; BLADE_PRIVATE_SPOOL): one row
  // per completed spool region -- its terminal (a string literal), N, the
  // block (the source chunk edge), the blocks written, the payload bytes
  // written and read back, the logical disk high water and the PLANNED
  // reservation, and the grant it ran under. Noted only after the region's
  // reader finished and its scratch was retired; an ADDITIVE, OPTIONAL member
  // (`private_spool`) written only when a row exists, so every other record
  // is unchanged. Its own slot, not extra_members (membership hoisting owns
  // that pointer), so a program holding both keeps both.
  struct SpoolEvent {
    const char* site; unsigned long long cells, block_cells, blocks, written, read, high_water, reserved;
    const char* root; unsigned long long mem, disk;
  };
  constexpr int spool_event_cap = 8;
  inline SpoolEvent spool_events[spool_event_cap] = {};
  inline int n_spool_events = 0;
  inline void note_private_spool(const char* site, unsigned long long cells, unsigned long long block_cells,
                                 unsigned long long blocks, unsigned long long written, unsigned long long read,
                                 unsigned long long high_water, unsigned long long reserved,
                                 const char* root, unsigned long long mem, unsigned long long disk) {
    if (n_spool_events < spool_event_cap)
      spool_events[n_spool_events++] = SpoolEvent{ site, cells, block_cells, blocks, written, read, high_water, reserved, root, mem, disk };
  }
  // EXTRA MEMBERS a runtime component appends after "inputs" (each writes
  // `,"key":value` text): null unless one registered -- membership
  // hoisting's builds (blade_member.h, "member_hoist") are the one today.
  inline void (*extra_members)(std::FILE*) = nullptr;

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
    std::fprintf(f, ",\"fp_reassoc\":%s", BLADE_RR_REASSOC ? "true" : "false");
#if defined(BLADE_POOL_POLICY_DECLARED)
    // BLADE_POOL_RETAIN (gap G52): which retention arm this executable runs.
    {
#if defined(BLADE_POOL_CACHE_OFF) && BLADE_POOL_CACHE_OFF
      const char* pool_cache = "off";
#else
      const char* pool_cache = "on";
#endif
      std::fprintf(f, ",\"pool\":{\"retain\":\"%s\",\"max_block\":%llu,\"budget\":%llu,\"budget_scope\":\"per-thread\",\"cache\":\"%s\"}",
                   ::blade_pool::kRetainArm ? "arm" : "default",
                   (unsigned long long)::blade_pool::kMaxBlock,
                   (unsigned long long)::blade_pool::kBudget,
                   pool_cache);
    }
#endif
    std::fprintf(f, "},\"routes\":{\"blas\":%s,\"lapack\":%s,\"cublas\":%s},\"rng\":%s",
                 BLADE_RR_BLAS ? "true" : "false",
                 BLADE_RR_LAPACK ? "true" : "false",
                 BLADE_RR_CUBLAS ? "true" : "false",
                 uses_rng ? "{\"generator\":\"philox4x32-10\",\"version\":1}" : "null");
    // the runtime form census (observed, per site; see note_form) -- written
    // ONLY when a site recorded an event (or dropped one), so a program with
    // no run-time-chosen form keeps the top-level members of the pre-census
    // format (version 1 unchanged; since the pool-retention arm every record
    // also carries policy.pool, so a default record is NOT byte-identical to
    // one written before that additive member existed)
    if (n_form_events > 0 || form_events_dropped > 0) {
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
    }
    if (n_io_events > 0 || io_events_dropped > 0) {
      std::fputs(",\"provider_io\":[", f);
      for (int i = 0; i < n_io_events; ++i) {
        const IoEvent& e = io_events[i];
        if (i) std::fputc(',', f);
        std::fputs("{\"site\":", f); json_string(f, e.site);
        std::fputs(",\"variable\":", f); json_string(f, e.var);
        std::fputs(",\"form\":", f); json_string(f, e.form);
        std::fprintf(f, ",\"fetches\":%lld,\"fetched_bytes\":%lld,\"decoded_bytes\":%lld,\"hits\":%lld,\"slots\":%lld,\"slot_bytes\":%lld}",
                     e.fetches, e.fetched_bytes, e.decoded_bytes, e.hits, e.slots, e.slot_bytes);
      }
      std::fputc(']', f);
      if (io_events_dropped > 0) std::fprintf(f, ",\"provider_io_dropped\":%lld", io_events_dropped);
    }
#if defined(BLADE_WS_CENSUS) && BLADE_WS_CENSUS
    // the workspace census (see ws_site_end): every bracketed site, even one
    // whose team allocated nothing (that is the census's answer for it)
    std::fputs(",\"workspace\":[", f);
    for (int i = 0; i < n_ws_events; ++i) {
      const WsEvent& e = ws_events[i];
      if (i) std::fputc(',', f);
      std::fputs("{\"site\":", f); json_string(f, e.site);
      std::fprintf(f, ",\"calls\":%lld,\"max_threads\":%d,\"workers_allocating\":%d,\"allocs\":%lld,\"bytes\":%lld,\"bytes_per_worker\":%lld,\"workspace\":%lld,\"retained\":\"%s\"}",
                   e.calls, e.max_threads, e.workers, e.allocs, e.bytes, e.per_worker, e.workspace,
#if defined(BLADE_POOL_CACHE_OFF) && BLADE_POOL_CACHE_OFF
                   "none (pool cache off)"
#else
                   "per-thread blade_pool cache (16 KB .. max_block)"
#endif
                   );
    }
    std::fputc(']', f);
    if (ws_events_dropped > 0) std::fprintf(f, ",\"workspace_dropped\":%lld", ws_events_dropped);
#endif
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
    std::fputc(']', f);
    if (n_spool_events > 0) {
      std::fputs(",\"private_spool\":[", f);
      for (int i = 0; i < n_spool_events; ++i) {
        const SpoolEvent& e = spool_events[i];
        if (i) std::fputc(',', f);
        std::fputs("{\"site\":", f); json_string(f, e.site);
        std::fprintf(f, ",\"route\":\"spool\",\"cells\":%llu,\"block_cells\":%llu,\"blocks\":%llu,\"payload_written\":%llu,\"payload_read\":%llu,\"logical_disk_high_water\":%llu,\"reserved_disk\":%llu",
                     e.cells, e.block_cells, e.blocks, e.written, e.read, e.high_water, e.reserved);
        std::fputs(",\"grant\":{\"root\":", f); json_string(f, e.root);
        std::fprintf(f, ",\"mem\":%llu,\"disk\":%llu}}", e.mem, e.disk);
      }
      std::fputc(']', f);
    }
    if (extra_members) extra_members(f);
    std::fputs("}\n", f);
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
