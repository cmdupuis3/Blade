// Blade tile cache: revision reuse across immutable dataset snapshots
// (docs/plans/structural/04-revision-reuse.md). A pure, index-local traversal
// over Icechunk reads is emitted one leading-axis TILE at a time; every tile
// has a compile-time key -- a SHA-256 over the task's text, the output
// geometry, and the content identities of exactly the input chunks the tile
// reads -- and a local on-disk store keyed that way lets a run against a
// later snapshot skip the recomputation (and the chunk reads) of every tile
// whose dependencies did not move. Values only, never text: what prints is
// the materialized array either way, so cold and warm stdout are identical.
//
// The store: BLADE_TILE_CACHE, read at RUN time with the compiler's grammar
// (CodeGenTiles.tileCacheEnabled, Build.fs tileCacheDir) -- surrounding
// whitespace trimmed, then unset / empty / `0` / `off` / `false` -> disabled
// (every tile computes, nothing stores), `1` / `on` / `true` ->
// %LOCALAPPDATA%\Blade\tile-cache (or ~/.cache/blade/tile-cache), a rooted
// path -> that directory, anything else -> disabled. Missing parent
// directories are created.
// Files are `<dir>/<key[0:2]>/<key>-<toolchain>.tile`: the toolchain id
// (`-DBLADE_TOOLCHAIN_ID=...`, Build.fs) keeps bits compiled by a different
// compiler / flags / CPU selection apart without hashing at run time. A file
// is a 32-byte header { "BLTL", version, payload bytes } plus the tile's
// cells, row-major. A header that does not match what the program expects
// is a miss, never a value, and so is a file whose length is not exactly
// header + payload: a short or overlong entry is deleted on sight, so the
// next store replaces it.
//
// Concurrency. Several processes may share a store (a test harness runs
// many programs at once). A store writes a temp file private to its process
// -- `<file>.<pid>.<counter>.<random>.tmp` -- and then renames it onto the
// key, so no reader ever opens a partially written entry under the key's
// name and two stores of one key never share a temp file. The published
// file is never removed first: a rename that fails because the key already
// exists (Windows) means another process published the same bytes, so the
// temp file is simply discarded.
//
// Deliberately light, like blade_run_record.hpp: C stdio only, cold code.
#pragma once
#if !defined(__CUDA_ARCH__)
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <string>
#include <atomic>
#include <random>
#include <sys/types.h>
#include <sys/stat.h>
#if defined(_WIN32)
#include <direct.h>
#include <process.h>
#define BLADE_TILES_MKDIR(p) _mkdir(p)
#define BLADE_TILES_GETPID() ((unsigned long long)_getpid())
#define BLADE_TILES_FSEEK(f, o, w) _fseeki64(f, o, w)
#define BLADE_TILES_FTELL(f) _ftelli64(f)
#else
#include <unistd.h>
#define BLADE_TILES_MKDIR(p) mkdir(p, 0755)
#define BLADE_TILES_GETPID() ((unsigned long long)getpid())
#define BLADE_TILES_FSEEK(f, o, w) fseeko(f, (off_t)(o), w)
#define BLADE_TILES_FTELL(f) ftello(f)
#endif

#ifndef BLADE_TOOLCHAIN_ID
#define BLADE_TOOLCHAIN_ID unknown
#endif
#define BLADE_TILES_STR_(x) #x
#define BLADE_TILES_STR(x) BLADE_TILES_STR_(x)
#if defined(__GNUC__)
#define BLADE_TILES_COLD __attribute__((noinline, cold))
#else
#define BLADE_TILES_COLD
#endif

namespace blade_tiles {

  struct Header {
    char magic[4];          // "BLTL"
    std::uint32_t version;  // 1
    std::uint64_t bytes;    // payload size
    std::uint64_t reserved0;
    std::uint64_t reserved1;
  };

  // BLADE_TILE_CACHE resolved to a store directory; "" = disabled. Same
  // grammar as the compiler's gate, trim included, so a value the compiler
  // reads as enabled (and plans tiles for) is enabled here too.
  BLADE_TILES_COLD inline std::string resolve_dir() {
    const char* v = std::getenv("BLADE_TILE_CACHE");
    std::string s = v ? v : "";
    std::size_t b = s.find_first_not_of(" \t\r\n\f\v");
    std::size_t e = s.find_last_not_of(" \t\r\n\f\v");
    s = (b == std::string::npos) ? std::string() : s.substr(b, e - b + 1);
    std::string l = s;
    for (auto& c : l) c = (char)((c >= 'A' && c <= 'Z') ? c + 32 : c);
    if (s.empty() || l == "0" || l == "off" || l == "false") return "";
    if (l == "1" || l == "on" || l == "true") {
#if defined(_WIN32)
      const char* base = std::getenv("LOCALAPPDATA");
      if (!base || !*base) return "";
      return std::string(base) + "\\Blade\\tile-cache";
#else
      const char* home = std::getenv("HOME");
      if (!home || !*home) return "";
      return std::string(home) + "/.cache/blade/tile-cache";
#endif
    }
    // Rooted, as .NET's Path.IsPathRooted reads it: a leading separator, or
    // (Windows) a drive letter.
#if defined(_WIN32)
    bool rooted = s[0] == '/' || s[0] == '\\' || (s.size() >= 2 && s[1] == ':');
#else
    bool rooted = s[0] == '/';
#endif
    return rooted ? s : "";
  }

  // The store directory, resolved once. A function-local static: its
  // initialization is thread-safe, which the old done-flag pair was not.
  BLADE_TILES_COLD inline const std::string& dir() {
    static const std::string resolved = resolve_dir();
    return resolved;
  }

  inline bool enabled() { return !dir().empty(); }

  BLADE_TILES_COLD inline bool verbose() {
    const char* v = std::getenv("BLADE_TILE_CACHE_VERBOSE");
    return v && *v && !(v[0] == '0' && v[1] == '\0');
  }

  // mkdir -p: create every missing component of `p`. A component that
  // already exists -- or that a concurrent process creates first -- is fine.
  BLADE_TILES_COLD inline void make_dirs(const std::string& p) {
    for (std::size_t i = 1; i <= p.size(); i++) {
      if (i < p.size() && p[i] != '/' && p[i] != '\\') continue;
      std::string prefix = p.substr(0, i);
      if (prefix.back() == ':') continue;   // a bare drive ("C:") is not made
      BLADE_TILES_MKDIR(prefix.c_str());
    }
  }

  BLADE_TILES_COLD inline std::string path_of(const char* key) {
    const std::string& d = dir();
    if (d.empty()) return "";
    std::string sub = d + "/" + std::string(key, 2);
    make_dirs(sub);
    return sub + "/" + key + "-" + BLADE_TILES_STR(BLADE_TOOLCHAIN_ID) + ".tile";
  }

  // Open `p` as a COMPLETE entry of `bytes` payload bytes: a matching header
  // and a file length of exactly header + payload. A file that exists but
  // fails either check is not an entry -- it is deleted (the next store
  // replaces it) and the caller sees a miss. On success the stream is
  // positioned at the payload.
  BLADE_TILES_COLD inline std::FILE* open_entry(const std::string& p, std::uint64_t bytes) {
    std::FILE* f = std::fopen(p.c_str(), "rb");
    if (!f) return nullptr;
    Header h;
    bool ok = std::fread(&h, sizeof h, 1, f) == 1
              && std::memcmp(h.magic, "BLTL", 4) == 0 && h.version == 1 && h.bytes == bytes;
    if (ok) {
      // The payload must be all there and nothing may follow it (64-bit
      // offsets: one tile of a large field passes 2 GB).
      ok = BLADE_TILES_FSEEK(f, 0, SEEK_END) == 0;
      long long end = ok ? (long long)BLADE_TILES_FTELL(f) : -1;
      ok = ok && end >= 0 && (unsigned long long)end == (unsigned long long)(sizeof h) + bytes;
      ok = ok && BLADE_TILES_FSEEK(f, (long long)sizeof h, SEEK_SET) == 0;
    }
    if (!ok) {
      std::fclose(f);
      std::remove(p.c_str());
      return nullptr;
    }
    return f;
  }

  // A complete stored tile of exactly `bytes` payload bytes exists under `key`.
  BLADE_TILES_COLD inline bool probe(const char* key, std::uint64_t bytes) {
    if (!enabled()) return false;
    std::FILE* f = open_entry(path_of(key), bytes);
    if (!f) return false;
    std::fclose(f);
    return true;
  }

  // Read the tile under `key` into `dst` (exactly `bytes` bytes). False on a
  // miss. A short, overlong or otherwise corrupt entry is a miss too (and is
  // deleted): never partial cells, never a process exit.
  BLADE_TILES_COLD inline bool load(const char* key, void* dst, std::uint64_t bytes) {
    if (!enabled()) return false;
    std::string p = path_of(key);
    std::FILE* f = open_entry(p, bytes);
    if (!f) return false;
    std::size_t got = std::fread(dst, 1, (std::size_t)bytes, f);
    std::fclose(f);
    if (got != bytes) { std::remove(p.c_str()); return false; }
    return true;
  }

  // A temp-file name no other store -- in this process or another -- uses:
  // the pid, a per-process counter, and a random_device draw.
  BLADE_TILES_COLD inline std::string temp_name(const std::string& p) {
    static std::atomic<unsigned long long> counter{0};
    unsigned long long r = 0;
    try { std::random_device rd; r = ((unsigned long long)rd() << 32) ^ (unsigned long long)rd(); }
    catch (...) { r = (unsigned long long)(std::uintptr_t)&r; }
    return p + "." + std::to_string(BLADE_TILES_GETPID()) + "." + std::to_string(counter.fetch_add(1))
             + "." + std::to_string(r) + ".tmp";
  }

  // Store `bytes` bytes from `src` under `key`: a private temp file, then a
  // rename onto the key. A complete entry already under the key holds the
  // same bytes by construction and is left alone.
  BLADE_TILES_COLD inline void store(const char* key, const void* src, std::uint64_t bytes) {
    if (!enabled()) return;
    std::string p = path_of(key);
    if (probe(key, bytes)) return;
    std::string tmp = temp_name(p);
    std::FILE* f = std::fopen(tmp.c_str(), "wb");
    if (!f) return;
    Header h;
    std::memcpy(h.magic, "BLTL", 4);
    h.version = 1; h.bytes = bytes; h.reserved0 = 0; h.reserved1 = 0;
    bool ok = std::fwrite(&h, sizeof h, 1, f) == 1 && std::fwrite(src, 1, (std::size_t)bytes, f) == bytes;
    ok = (std::fflush(f) == 0) && ok;
    ok = (std::fclose(f) == 0) && ok;
    if (!ok) { std::remove(tmp.c_str()); return; }
    // POSIX rename replaces atomically. Windows refuses when the key exists:
    // a concurrent store published the same bytes first, so ours is surplus.
    if (std::rename(tmp.c_str(), p.c_str()) != 0) std::remove(tmp.c_str());
  }
}
#endif
