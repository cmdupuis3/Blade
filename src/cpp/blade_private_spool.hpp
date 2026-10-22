// Private sequential Float64 intermediate. No compiler admission or default
// disk policy. Callers grant every resource explicitly; errors are statuses.
#pragma once
#include "blade_runtime.hpp"
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <new>
#include <utility>
#include <initializer_list>
#if defined(_WIN32)
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0600
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace blade_private_spool {
constexpr std::size_t path_capacity = 240;
constexpr std::uint64_t header_bytes = 72, block_header_bytes = 64;
constexpr std::uint64_t footer_bytes = 64, manifest_bytes = 128;
constexpr std::uint64_t max_block_cells = 1048576;

enum class Code {
  ok, unsupported, no_grant, invalid_grant, overflow, memory_quota,
  disk_quota, busy, hooks_full, exit_registration, allocation,
  root, ownership, uncertain, create, open, write, read, zero_progress,
  flush, close, integrity, state, remove, cancelled
};
struct Status {
  Code code = Code::ok;
  std::uint32_t os_error = 0;
  explicit operator bool() const noexcept { return code == Code::ok; }
};
struct Grant {
  bool permitted = false;
  const char* root = nullptr; // existing, absolute, ASCII local-drive root
  std::uint64_t memory_bytes = 0, disk_bytes = 0;
  std::uint64_t cells = 0, block_cells = 0, axis = 0;
};
struct Plan {
  std::uint64_t blocks = 0, file_bytes = 0, reserved_disk = 0;
  std::uint64_t workspace_bytes = 0;
};
struct Counters {
  bool available = false;
  std::uint64_t payload_written = 0, payload_read = 0;
  std::uint64_t data_written = 0, data_read = 0;
  std::uint64_t logical_disk_high_water = 0, workspace_capacity = 0;
};
enum class Phase { idle, writing, faulted, sealed, reading, checked };

namespace detail {
// Wire values are raw IEEE64 cells. Check the actual integer and floating
// byte representations without requiring the C++20 std::endian API: ordinary
// generated Blade programs use C++17. Never assume host byte order from OS.
inline bool little_endian_f64() noexcept {
  const std::uint64_t integer = UINT64_C(0x0102030405060708);
  const double floating = 1.0;
  unsigned char integer_bytes[sizeof integer], floating_bytes[sizeof floating];
  std::memcpy(integer_bytes, &integer, sizeof integer);
  std::memcpy(floating_bytes, &floating, sizeof floating);
  const unsigned char integer_le[8] = {8, 7, 6, 5, 4, 3, 2, 1};
  const unsigned char floating_le[8] = {0, 0, 0, 0, 0, 0, 0xf0, 0x3f};
  return sizeof integer == 8 && sizeof floating == 8
      && std::memcmp(integer_bytes, integer_le, 8) == 0
      && std::memcmp(floating_bytes, floating_le, 8) == 0;
}
inline bool add(std::uint64_t a, std::uint64_t b, std::uint64_t& out) noexcept {
  if (b > UINT64_MAX - a) return false;
  out = a + b; return true;
}
inline bool mul(std::uint64_t a, std::uint64_t b, std::uint64_t& out) noexcept {
  if (a && b > UINT64_MAX / a) return false;
  out = a * b; return true;
}
inline void put(unsigned char* p, std::uint64_t n) noexcept {
  for (unsigned i = 0; i < 8; ++i) { p[i] = static_cast<unsigned char>(n); n >>= 8; }
}
inline std::uint64_t get(const unsigned char* p) noexcept {
  std::uint64_t n = 0;
  for (unsigned i = 0; i < 8; ++i) n |= std::uint64_t(p[i]) << (8 * i);
  return n;
}
// CRC-64/ECMA, non-reflected, zero initial state, no final xor. Format v1.
inline std::uint64_t crc(const unsigned char* p, std::size_t n,
                         std::uint64_t value = 0) noexcept {
  for (std::size_t i = 0; i < n; ++i) {
    value ^= std::uint64_t(p[i]) << 56;
    for (unsigned j = 0; j < 8; ++j)
      value = (value << 1) ^ ((value >> 63) ? UINT64_C(0x42f0e1eba9ea3693) : 0);
  }
  return value;
}
struct Control {
  Phase phase = Phase::idle;
  std::uint64_t generation = 0, n = 0, b = 0, axis = 0;
  std::uint64_t sequence = 0, origin = 0, rolling = 0;
  std::uint64_t nonce[2] = {}, pid = 0, start = 0;
  std::uint64_t root_volume = 0, root_id = 0, run_volume = 0, run_id = 0;
  std::uint64_t data_volume = 0, data_id = 0;
  std::uint64_t manifest_volume = 0, manifest_id = 0;
  Plan plan{}; Counters counters{}; Status primary{}, cleanup_status{};
  char root[path_capacity] = {}, directory[path_capacity] = {};
  char name[48] = {}, data[path_capacity] = {}, manifest[path_capacity] = {};
  unsigned char* buffer = nullptr;
  bool owns_directory = false, owns_data = false, owns_manifest = false;
  bool root_retired = false;
  bool directory_retiring = false;
#if defined(_WIN32)
  HANDLE root_handle = INVALID_HANDLE_VALUE, directory_handle = INVALID_HANDLE_VALUE;
  HANDLE file = INVALID_HANDLE_VALUE, lock = INVALID_HANDLE_VALUE, auxiliary = INVALID_HANDLE_VALUE;
  HANDLE find = INVALID_HANDLE_VALUE;
#endif
};
static_assert(sizeof(Control) <= 4096, "private spool fixed control metadata exceeds its stack reserve");
// One stable slot, never an address of a caller's stack object. No per-block
// registration/catalog; the first serial owner installs one hook for process life.
inline Control active;
inline std::uint64_t next_generation = 0;
inline bool failure_registered = false, normal_registered = false;
inline Status last_cleanup;
inline std::atomic_flag api_busy = ATOMIC_FLAG_INIT;
struct Guard {
  bool held = !api_busy.test_and_set(std::memory_order_acquire);
  ~Guard() { if (held) api_busy.clear(std::memory_order_release); }
  explicit operator bool() const noexcept { return held; }
};
inline Status fail(Control& c, Code code, std::uint32_t os = 0) noexcept {
  if (c.primary) c.primary = {code, os};
  c.phase = Phase::faulted;
  return c.primary;
}
inline bool matches(std::uint64_t generation) noexcept {
  return generation && generation == active.generation && active.phase != Phase::idle;
}
} // detail

inline Status plan(const Grant& g, Plan& out) noexcept {
  out = {};
  if (!g.permitted) return {Code::no_grant};
  if (!g.root || !g.block_cells || g.block_cells > max_block_cells)
    return {Code::invalid_grant};
  if (sizeof(double) != 8 || std::numeric_limits<unsigned char>::digits != 8
      || !std::numeric_limits<double>::is_iec559 || !detail::little_endian_f64()) return {Code::unsupported};
  const auto blocks = g.cells / g.block_cells + (g.cells % g.block_cells != 0);
  std::uint64_t payload, headers, bytes, workspace;
  if (!detail::mul(g.cells, 8, payload)
      || !detail::mul(blocks, block_header_bytes, headers)
      || !detail::add(header_bytes, payload, bytes)
      || !detail::add(bytes, headers, bytes) || !detail::add(bytes, footer_bytes, bytes)
      || !detail::add(bytes, manifest_bytes, out.reserved_disk)
      || !detail::mul(g.block_cells, 8, workspace)
      || !detail::add(workspace, sizeof(detail::Control) + 4096, workspace)) return {Code::overflow};
  // Native file APIs are deliberately limited to signed 64-bit file lengths.
  if (bytes > std::uint64_t(INT64_MAX) || workspace > std::uint64_t(SIZE_MAX)) return {Code::overflow};
  out.blocks = blocks; out.file_bytes = bytes; out.workspace_bytes = workspace;
  if (workspace > g.memory_bytes) return {Code::memory_quota};
  if (out.reserved_disk > g.disk_bytes) return {Code::disk_quota};
  return {};
}

#ifdef BLADE_PRIVATE_SPOOL_TESTING
namespace testing {
struct Faults {
  std::uint64_t max_transfer = UINT64_MAX;
  std::uint64_t write_error_at = UINT64_MAX, read_error_at = UINT64_MAX;
  std::uint64_t zero_write_at = UINT64_MAX, zero_read_at = UINT64_MAX;
  bool flush = false, close = false, remove = false, owner_unknown = false;
  bool allocation = false, exit_registration = false;
  bool directory_remove = false, manifest_remove = false;
  bool lock_close = false, lock_close_after_success = false;
};
inline Faults faults;
}
#endif

namespace detail {
inline Status cleanup() noexcept;
inline void exit_cleanup() noexcept {
  Guard guard;
  const auto s = guard ? cleanup() : Status{Code::busy};
  if (!s) {
#if defined(_WIN32)
    constexpr char pending[] = "private spool: owned scratch cleanup incomplete; explicit recovery requires recognized evidence\n";
    constexpr char retired[] = "private spool: cleanup reported failure after scratch retirement\n";
    const char* warning = guard && active.phase == Phase::idle ? retired : pending;
    DWORD written = 0;
    (void)WriteFile(GetStdHandle(STD_ERROR_HANDLE), warning, static_cast<DWORD>(std::strlen(warning)), &written, nullptr);
#endif
  }
}
inline Status install_hooks() noexcept {
  if (!failure_registered) {
    if (!blade_rt::try_on_failure_exit(exit_cleanup)) return {Code::hooks_full};
    failure_registered = true;
  }
  if (!normal_registered) {
#ifdef BLADE_PRIVATE_SPOOL_TESTING
    if (testing::faults.exit_registration) return {Code::exit_registration};
#endif
    if (std::atexit(exit_cleanup) != 0) return {Code::exit_registration};
    normal_registered = true;
  }
  return {};
}

#if defined(_WIN32)
inline Status win(Code code) noexcept { return {code, static_cast<std::uint32_t>(GetLastError())}; }
inline bool path(char* dst, const char* base, const char* leaf) noexcept {
  const auto a = std::strlen(base), b = std::strlen(leaf);
  if (a + b + 2 > path_capacity) return false;
  std::memcpy(dst, base, a); dst[a] = '\\'; std::memcpy(dst + a + 1, leaf, b + 1); return true;
}
inline bool ascii_path(const char* p) noexcept {
  if (!p) return false;
  std::size_t n = 0;
  for (; n < path_capacity && p[n]; ++n)
    if (static_cast<unsigned char>(p[n]) < 32 || static_cast<unsigned char>(p[n]) > 126 || p[n] == '/') return false;
  return n >= 3 && n < path_capacity && ((p[0] >= 'A' && p[0] <= 'Z') || (p[0] >= 'a' && p[0] <= 'z'))
         && p[1] == ':' && p[2] == '\\';
}
inline bool identity(HANDLE h, std::uint64_t& volume, std::uint64_t& id, bool directory) noexcept {
  BY_HANDLE_FILE_INFORMATION info{};
  if (!GetFileInformationByHandle(h, &info) || (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
      || bool(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != directory) return false;
  volume = info.dwVolumeSerialNumber;
  id = (std::uint64_t(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
  return id != 0;
}
inline HANDLE open_directory(const char* p, bool deletable = false) noexcept {
  return CreateFileA(p, FILE_READ_ATTRIBUTES | (deletable ? DELETE : 0), FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                     OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
}
inline bool same_directory(HANDLE h, const char* expected, std::uint64_t volume, std::uint64_t id) noexcept {
  std::uint64_t actual_volume = 0, actual_id = 0;
  if (!identity(h, actual_volume, actual_id, true) || actual_volume != volume || actual_id != id) return false;
  char final_path[path_capacity + 4] = {};
  const DWORD n = GetFinalPathNameByHandleA(h, final_path, path_capacity + 4, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
  if (!n || n >= path_capacity + 4 || std::strncmp(final_path, "\\\\?\\", 4)) return false;
  return _stricmp(final_path + 4, expected) == 0;
}
inline Status root_open(Control& c, const char* root) noexcept {
  if (!ascii_path(root)) return {Code::root};
  char drive[4] = {root[0], ':', '\\', 0};
  if (GetDriveTypeA(drive) != DRIVE_FIXED) return {Code::unsupported};
  char full[path_capacity] = {};
  const DWORD length = GetFullPathNameA(root, path_capacity, full, nullptr);
  if (!length || length >= path_capacity) return {Code::root};
  // Do not silently normalize dot components, aliases, trailing dots or spaces.
  if (_stricmp(root, full) != 0) return {Code::root};
  std::size_t n = std::strlen(full);
  if (n > 3 && full[n - 1] == '\\') full[--n] = 0;
  for (std::size_t i = 3; i <= n; ++i) {
    if (i != n && full[i] != '\\') continue;
    if (i > 3 && (full[i - 1] == '.' || full[i - 1] == ' ')) return {Code::root};
    const char saved = full[i]; full[i] = 0;
    const auto attr = GetFileAttributesA(full);
    full[i] = saved;
    if (attr == INVALID_FILE_ATTRIBUTES || !(attr & FILE_ATTRIBUTE_DIRECTORY)
        || (attr & FILE_ATTRIBUTE_REPARSE_POINT)) return {Code::root};
  }
  std::memcpy(c.root, full, n + 1);
  c.root_handle = open_directory(c.root);
  if (c.root_handle == INVALID_HANDLE_VALUE || !identity(c.root_handle, c.root_volume, c.root_id, true))
    return {Code::root};
  if (!same_directory(c.root_handle, c.root, c.root_volume, c.root_id)) return {Code::root};
  return {};
}
inline std::uint64_t time_bits(FILETIME t) noexcept {
  return (std::uint64_t(t.dwHighDateTime) << 32) | t.dwLowDateTime;
}
inline bool process_start(HANDLE h, std::uint64_t& start) noexcept {
  FILETIME creation{}, exit{}, kernel{}, user{};
  if (!GetProcessTimes(h, &creation, &exit, &kernel, &user)) return false;
  start = time_bits(creation); return true;
}
inline bool valid_name(const char* name) noexcept {
  if (!name || std::strncmp(name, "blade-spool-v1-", 15) != 0) return false;
  for (unsigned i = 15; i < 47; ++i)
    if (!((name[i] >= '0' && name[i] <= '9') || (name[i] >= 'a' && name[i] <= 'f'))) return false;
  return name[47] == 0;
}
inline void nonce_name(Control& c) noexcept {
  std::memcpy(c.name, "blade-spool-v1-", 15);
  constexpr char hex[] = "0123456789abcdef";
  for (unsigned k = 0; k < 2; ++k)
    for (unsigned j = 0; j < 16; ++j) c.name[15 + k * 16 + j] = hex[(c.nonce[k] >> (60 - 4 * j)) & 15];
  c.name[47] = 0;
}
inline Status paths(Control& c) noexcept {
  char owner[54] = {}; std::memcpy(owner, c.name, 47); std::memcpy(owner + 47, ".owner", 7);
  if (!path(c.directory, c.root, c.name) || !path(c.data, c.directory, "values.bin")
      || !path(c.manifest, c.root, owner))
    return {Code::invalid_grant};
  return {};
}
inline Status close_handle(HANDLE& h, bool inject = false) noexcept {
  if (h == INVALID_HANDLE_VALUE) return {};
  if (!CloseHandle(h)) return win(Code::close);
  h = INVALID_HANDLE_VALUE;
#ifdef BLADE_PRIVATE_SPOOL_TESTING
  if (inject && testing::faults.close) return {Code::close};
#else
  (void)inject;
#endif
  return {};
}
inline Status transfer(Control& c, HANDLE file, unsigned char* bytes, std::uint64_t length,
                       bool writing, bool data) noexcept {
  std::uint64_t at = 0;
  while (at < length) {
    auto take = (length - at > (1u << 30)) ? std::uint64_t(1u << 30) : length - at;
#ifdef BLADE_PRIVATE_SPOOL_TESTING
    if (data) {
      const auto done = writing ? c.counters.data_written : c.counters.data_read;
      const auto error = writing ? testing::faults.write_error_at : testing::faults.read_error_at;
      const auto zero = writing ? testing::faults.zero_write_at : testing::faults.zero_read_at;
      if (done >= error) return {writing ? Code::write : Code::read};
      if (done >= zero) return {Code::zero_progress};
      if (take > error - done) take = error - done;
      if (take > zero - done) take = zero - done;
      if (take > testing::faults.max_transfer) take = testing::faults.max_transfer;
      if (!take) return {Code::zero_progress};
    }
#endif
    DWORD transferred = 0;
    const BOOL ok = writing ? WriteFile(file, bytes + at, static_cast<DWORD>(take), &transferred, nullptr)
                            : ReadFile(file, bytes + at, static_cast<DWORD>(take), &transferred, nullptr);
    if (!ok) return win(writing ? Code::write : Code::read);
    if (!transferred) return {Code::zero_progress};
    if (transferred > take) return {Code::integrity};
    at += transferred;
    if (!data && writing && at > c.counters.logical_disk_high_water)
      c.counters.logical_disk_high_water = at; // Manifest exists before data.
    if (data) {
      if (writing) { c.counters.data_written += transferred;
        const auto high = manifest_bytes + c.counters.data_written;
        if (high > c.counters.logical_disk_high_water) c.counters.logical_disk_high_water = high;
      } else c.counters.data_read += transferred;
    }
  }
  return {};
}
inline Status flush(HANDLE file, bool inject) noexcept {
#ifdef BLADE_PRIVATE_SPOOL_TESTING
  if (inject && testing::faults.flush) return {Code::flush};
#else
  (void)inject;
#endif
  if (!FlushFileBuffers(file)) return win(Code::flush);
  return {};
}
inline bool exact_size(HANDLE file, std::uint64_t size) noexcept {
  LARGE_INTEGER value{};
  return GetFileSizeEx(file, &value) && value.QuadPart >= 0 && std::uint64_t(value.QuadPart) == size;
}
inline HANDLE create_file(const char* p) noexcept {
  return CreateFileA(p, GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                     FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
}
inline HANDLE read_file(const char* p) noexcept {
  return CreateFileA(p, GENERIC_READ, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
}
inline void header(Control& c, unsigned char* h) noexcept {
  std::memset(h, 0, header_bytes); std::memcpy(h, "BLSP001", 7);
  put(h + 8, 1); put(h + 16, c.n); put(h + 24, c.b); put(h + 32, c.plan.blocks);
  put(h + 40, c.axis); put(h + 48, c.nonce[0]); put(h + 56, c.nonce[1]); put(h + 64, crc(h, 64));
}
inline void manifest(Control& c, unsigned char* h) noexcept {
  std::memset(h, 0, manifest_bytes); std::memcpy(h, "BLSPMAN1", 8);
  put(h + 8, 1); put(h + 16, c.pid); put(h + 24, c.start);
  put(h + 32, c.nonce[0]); put(h + 40, c.nonce[1]);
  put(h + 48, c.root_volume); put(h + 56, c.root_id); put(h + 64, c.run_volume); put(h + 72, c.run_id);
  put(h + 80, c.plan.reserved_disk); put(h + 88, c.n); put(h + 96, c.b); put(h + 104, c.axis);
  put(h + 120, crc(h, 120));
}
// Enumerate only to refuse unexpected entries, never to choose deletion targets.
// At most '.', '..' and values.bin are accepted; no tree traversal.
inline Status known_files(Control& c) noexcept {
  char pattern[path_capacity] = {};
  if (!path(pattern, c.directory, "*")) return {Code::ownership};
  WIN32_FIND_DATAA item{}; c.find = FindFirstFileA(pattern, &item);
  if (c.find == INVALID_HANDLE_VALUE) return win(Code::uncertain);
  Status status{}; unsigned count = 0;
  do {
    if (++count > 3) { status = {Code::ownership}; break; }
    if (!std::strcmp(item.cFileName, ".") || !std::strcmp(item.cFileName, "..")) continue;
    if ((item.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))
        || std::strcmp(item.cFileName, "values.bin")) { status = {Code::ownership}; break; }
  } while (FindNextFileA(c.find, &item));
  if (status && GetLastError() != ERROR_NO_MORE_FILES) status = win(Code::uncertain);
  if (FindClose(c.find)) c.find = INVALID_HANDLE_VALUE;
  else if (status) status = win(Code::close);
  return status;
}
inline Status remove_file(Control& c, const char* p, std::uint64_t volume = 0, std::uint64_t id = 0) noexcept {
#ifdef BLADE_PRIVATE_SPOOL_TESTING
  if (testing::faults.remove) return {Code::remove};
#endif
  c.auxiliary = CreateFileA(p, DELETE | FILE_READ_ATTRIBUTES, 0, nullptr, OPEN_EXISTING,
                            FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  if (c.auxiliary == INVALID_HANDLE_VALUE) {
    const DWORD e = GetLastError();
    return (e == ERROR_FILE_NOT_FOUND) ? Status{} : Status{Code::remove, e};
  }
  std::uint64_t actual_volume = 0, actual_id = 0;
  if (!identity(c.auxiliary, actual_volume, actual_id, false)
      || (id && (actual_volume != volume || actual_id != id))) {
    (void)close_handle(c.auxiliary); return {Code::ownership};
  }
  FILE_DISPOSITION_INFO disposition{TRUE};
  if (!SetFileInformationByHandle(c.auxiliary, FileDispositionInfo, &disposition, sizeof disposition)) {
    const auto status = win(Code::remove); (void)close_handle(c.auxiliary); return status;
  }
  return close_handle(c.auxiliary);
}
inline Status dispose(Control& c) noexcept {
  // Closing precedes unlink. Never allocate, throw, recurse, or replace primary.
  Status s{};
  if (c.find != INVALID_HANDLE_VALUE) {
    if (!FindClose(c.find)) return win(Code::close);
    c.find = INVALID_HANDLE_VALUE;
  }
  for (HANDLE* h : {&c.file, &c.auxiliary}) { auto x = close_handle(*h); if (!x && s) s = x; }
  if (!s) return s;
  if (c.owns_directory) {
    if (c.directory_handle == INVALID_HANDLE_VALUE) {
      c.directory_handle = open_directory(c.directory, true);
      std::uint64_t v = 0, id = 0;
      if (c.directory_handle == INVALID_HANDLE_VALUE || !identity(c.directory_handle, v, id, true)
          || (c.run_id && (v != c.run_volume || id != c.run_id))) return {Code::ownership};
      c.run_volume = v; c.run_id = id;
    }
    if (!same_directory(c.root_handle, c.root, c.root_volume, c.root_id)
        || !same_directory(c.directory_handle, c.directory, c.run_volume, c.run_id)) return {Code::ownership};
    auto x = known_files(c); if (!x) return x;
    if (c.owns_data) { x = remove_file(c, c.data, c.data_volume, c.data_id); if (!x) return x; c.owns_data = false; }
#ifdef BLADE_PRIVATE_SPOOL_TESTING
    if (testing::faults.directory_remove) return {Code::remove};
#endif
    FILE_DISPOSITION_INFO disposition{TRUE};
    if (!SetFileInformationByHandle(c.directory_handle, FileDispositionInfo, &disposition, sizeof disposition)) return win(Code::remove);
    c.directory_retiring = true;
    x = close_handle(c.directory_handle); if (!x) return x;
    c.owns_directory = false;
  }
  auto x = close_handle(c.directory_handle); if (!x) return x;
  if (c.directory_retiring) {
    if (!same_directory(c.root_handle, c.root, c.root_volume, c.root_id)) return {Code::ownership};
    if (GetFileAttributesA(c.directory) != INVALID_FILE_ATTRIBUTES) return {Code::uncertain};
    const auto error = GetLastError();
    if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) return {Code::uncertain, error};
    c.directory_retiring = false;
  }
  if (c.owns_manifest && c.root_handle != INVALID_HANDLE_VALUE
      && !same_directory(c.root_handle, c.root, c.root_volume, c.root_id)) return {Code::ownership};
  if (c.owns_manifest && c.root_handle == INVALID_HANDLE_VALUE && !c.root_retired) return {Code::ownership};
  x = close_handle(c.root_handle); if (!x) return x;
  c.root_retired = true;
  delete[] c.buffer; c.buffer = nullptr;
  if (c.owns_manifest) {
    std::uint64_t volume = 0, id = 0;
    if (c.lock == INVALID_HANDLE_VALUE || !identity(c.lock, volume, id, false)
        || volume != c.manifest_volume || id != c.manifest_id) return {Code::ownership};
#ifdef BLADE_PRIVATE_SPOOL_TESTING
    if (testing::faults.manifest_remove) return {Code::remove};
    if (testing::faults.lock_close) return {Code::close};
#endif
    FILE_DISPOSITION_INFO disposition{TRUE};
    if (!SetFileInformationByHandle(c.lock, FileDispositionInfo, &disposition, sizeof disposition)) return win(Code::remove);
    x = close_handle(c.lock);
    if (c.lock == INVALID_HANDLE_VALUE) c.owns_manifest = false;
#ifdef BLADE_PRIVATE_SPOOL_TESTING
    if (x && testing::faults.lock_close_after_success) x = {Code::close};
#endif
    if (!x) return x;
  } else { x = close_handle(c.lock); if (!x) return x; }
  return {};
}
inline Status cleanup() noexcept {
  if (active.phase == Phase::idle) return {};
  const auto status = dispose(active);
  active.cleanup_status = status; last_cleanup = status;
  if (!status && active.primary) active.primary = status;
  const bool retired = !active.owns_directory && !active.directory_retiring && !active.owns_data && !active.owns_manifest && !active.buffer
                       && active.file == INVALID_HANDLE_VALUE && active.auxiliary == INVALID_HANDLE_VALUE
                       && active.lock == INVALID_HANDLE_VALUE && active.find == INVALID_HANDLE_VALUE
                       && active.root_handle == INVALID_HANDLE_VALUE && active.directory_handle == INVALID_HANDLE_VALUE;
  if (status || retired) active.phase = Phase::idle;
  return status;
}
inline Status begin_native(const Grant& g) noexcept {
  if (active.phase != Phase::idle) return {Code::busy};
  Plan p; auto s = plan(g, p); if (!s) return s;
  s = install_hooks(); if (!s) return s;
  if (next_generation == UINT64_MAX) return {Code::overflow};
  active = Control{}; auto& c = active;
  c.generation = ++next_generation; c.phase = Phase::writing;
  c.n = g.cells; c.b = g.block_cells; c.axis = g.axis; c.plan = p;
  c.counters.workspace_capacity = p.workspace_bytes;
  c.counters.available = true;
  c.pid = GetCurrentProcessId();
  if (!process_start(GetCurrentProcess(), c.start)) s = {Code::unsupported};
  if (s) { c.nonce[0] = c.start; c.nonce[1] = (c.pid << 32) ^ c.generation; nonce_name(c); }
  if (s) s = root_open(c, g.root);
  if (s) s = paths(c);
  if (s) {
#ifdef BLADE_PRIVATE_SPOOL_TESTING
    if (testing::faults.allocation) s = {Code::allocation};
#endif
  }
  if (s) {
    c.buffer = new (std::nothrow) unsigned char[static_cast<std::size_t>(c.b * 8)];
    if (!c.buffer) s = {Code::allocation};
  }
  if (s) {
    if (!CreateDirectoryA(c.directory, nullptr)) s = win(Code::create);
    else c.owns_directory = true;
  }
  if (s) {
    c.directory_handle = open_directory(c.directory, true);
    if (c.directory_handle == INVALID_HANDLE_VALUE || !identity(c.directory_handle, c.run_volume, c.run_id, true)) s = {Code::ownership};
    else if (!same_directory(c.root_handle, c.root, c.root_volume, c.root_id)
             || !same_directory(c.directory_handle, c.directory, c.run_volume, c.run_id)) s = {Code::ownership};
  }
  if (s) {
    c.lock = CreateFileA(c.manifest, GENERIC_READ | GENERIC_WRITE | DELETE, 0, nullptr, CREATE_NEW,
                         FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (c.lock == INVALID_HANDLE_VALUE) s = win(Code::create);
    else { c.owns_manifest = true; if (!identity(c.lock, c.manifest_volume, c.manifest_id, false)) s = {Code::ownership}; }
  }
  if (s) {
    unsigned char m[manifest_bytes] = {}; manifest(c, m);
    s = transfer(c, c.lock, m, manifest_bytes, true, false);
    if (s) s = flush(c.lock, false);
  }
  if (s) {
    c.file = create_file(c.data);
    if (c.file == INVALID_HANDLE_VALUE) s = win(Code::create);
    else {
      c.owns_data = true;
      if (!identity(c.file, c.data_volume, c.data_id, false)) s = {Code::ownership};
      unsigned char h[header_bytes] = {}; header(c, h);
      if (s) s = transfer(c, c.file, h, header_bytes, true, true);
    }
  }
  if (!s) { fail(c, s.code, s.os_error); (void)cleanup(); }
  return s;
}
inline Status append(std::uint64_t generation, const double* values, std::uint64_t count) noexcept {
  if (!matches(generation) || active.phase != Phase::writing) return {Code::state};
  auto& c = active;
  const auto expected = c.n - c.origin < c.b ? c.n - c.origin : c.b;
  if (!values || !count || count != expected) return fail(c, Code::state);
  for (std::uint64_t i = 0; i < count; ++i) {
    std::uint64_t bits = 0; std::memcpy(&bits, values + i, 8); put(c.buffer + i * 8, bits);
  }
  unsigned char h[block_header_bytes] = {};
  put(h, c.sequence); put(h + 8, c.origin); put(h + 16, count); put(h + 24, count * 8);
  put(h + 32, c.nonce[0]); put(h + 40, c.nonce[1]); put(h + 48, crc(c.buffer, static_cast<std::size_t>(count * 8)));
  put(h + 56, crc(h, 56));
  auto s = transfer(c, c.file, h, block_header_bytes, true, true);
  if (s) s = transfer(c, c.file, c.buffer, count * 8, true, true);
  if (!s) return fail(c, s.code, s.os_error);
  c.rolling = crc(c.buffer, static_cast<std::size_t>(count * 8), c.rolling);
  c.origin += count; ++c.sequence; c.counters.payload_written += count * 8; return {};
}
inline Status seal(std::uint64_t generation) noexcept {
  if (!matches(generation) || active.phase != Phase::writing) return {Code::state};
  auto& c = active;
  if (c.origin != c.n || c.sequence != c.plan.blocks) return fail(c, Code::state);
  unsigned char f[footer_bytes] = {}; std::memcpy(f, "BLEND001", 8);
  put(f + 8, c.n); put(f + 16, c.sequence); put(f + 24, c.n * 8); put(f + 32, c.rolling);
  put(f + 40, c.nonce[0]); put(f + 48, c.nonce[1]); put(f + 56, crc(f, 56));
  auto s = transfer(c, c.file, f, footer_bytes, true, true);
  if (s) s = flush(c.file, true);
  if (s && !exact_size(c.file, c.plan.file_bytes)) s = {Code::integrity};
  const auto closed = close_handle(c.file, true); if (s) s = closed;
  if (!s) return fail(c, s.code, s.os_error);
  c.phase = Phase::sealed; return {};
}
inline Status open_reader(std::uint64_t generation) noexcept {
  if (!matches(generation) || active.phase != Phase::sealed) return {Code::state};
  auto& c = active; c.file = read_file(c.data);
  if (c.file == INVALID_HANDLE_VALUE) return fail(c, Code::open, GetLastError());
  std::uint64_t volume = 0, id = 0;
  if (!identity(c.file, volume, id, false) || volume != c.data_volume || id != c.data_id
      || !same_directory(c.root_handle, c.root, c.root_volume, c.root_id)
      || !same_directory(c.directory_handle, c.directory, c.run_volume, c.run_id)) return fail(c, Code::ownership);
  if (!exact_size(c.file, c.plan.file_bytes)) return fail(c, Code::integrity);
  unsigned char expected[header_bytes] = {}, actual[header_bytes] = {}; header(c, expected);
  auto s = transfer(c, c.file, actual, header_bytes, false, true);
  if (!s) return fail(c, s.code, s.os_error);
  if (std::memcmp(actual, expected, header_bytes)) return fail(c, Code::integrity);
  c.origin = 0; c.sequence = 0; c.rolling = 0; c.phase = Phase::reading; return {};
}
inline Status next(std::uint64_t generation, double* values, std::uint64_t capacity, std::uint64_t& count) noexcept {
  count = 0;
  if (!matches(generation) || active.phase != Phase::reading) return {Code::state};
  auto& c = active;
  if (c.origin == c.n) {
    unsigned char f[footer_bytes] = {};
    auto s = transfer(c, c.file, f, footer_bytes, false, true);
    if (!s) return fail(c, s.code, s.os_error);
    if (std::memcmp(f, "BLEND001", 8) || get(f + 8) != c.n || get(f + 16) != c.sequence
        || get(f + 24) != c.n * 8 || get(f + 32) != c.rolling || get(f + 40) != c.nonce[0]
        || get(f + 48) != c.nonce[1] || get(f + 56) != crc(f, 56) || !exact_size(c.file, c.plan.file_bytes))
      return fail(c, Code::integrity);
    unsigned char byte = 0; DWORD got = 0;
    if (!ReadFile(c.file, &byte, 1, &got, nullptr)) return fail(c, Code::read, GetLastError());
    if (got) return fail(c, Code::integrity);
    c.phase = Phase::checked; return {};
  }
  const auto length = c.n - c.origin < c.b ? c.n - c.origin : c.b;
  if (!values || capacity < length) return fail(c, Code::state);
  unsigned char h[block_header_bytes] = {};
  auto s = transfer(c, c.file, h, block_header_bytes, false, true);
  if (!s) return fail(c, s.code, s.os_error);
  if (get(h) != c.sequence || get(h + 8) != c.origin || get(h + 16) != length
      || get(h + 24) != length * 8 || get(h + 32) != c.nonce[0] || get(h + 40) != c.nonce[1]
      || get(h + 56) != crc(h, 56)) return fail(c, Code::integrity);
  s = transfer(c, c.file, c.buffer, length * 8, false, true);
  if (!s) return fail(c, s.code, s.os_error);
  if (get(h + 48) != crc(c.buffer, static_cast<std::size_t>(length * 8))) return fail(c, Code::integrity);
  for (std::uint64_t i = 0; i < length; ++i) {
    const auto bits = get(c.buffer + i * 8); std::memcpy(values + i, &bits, 8);
  }
  c.rolling = crc(c.buffer, static_cast<std::size_t>(length * 8), c.rolling);
  c.origin += length; ++c.sequence; c.counters.payload_read += length * 8; count = length; return {};
}
inline Status owner_dead(Control& c) noexcept {
#ifdef BLADE_PRIVATE_SPOOL_TESTING
  if (testing::faults.owner_unknown) return {Code::uncertain};
#endif
  if (!c.pid || c.pid > UINT32_MAX || !c.start) return {Code::uncertain};
  c.auxiliary = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(c.pid));
  if (!c.auxiliary) {
    const auto error = GetLastError(); c.auxiliary = INVALID_HANDLE_VALUE;
    return error == ERROR_INVALID_PARAMETER ? Status{} : Status{Code::uncertain, error};
  }
  std::uint64_t actual = 0; Status s{};
  if (!process_start(c.auxiliary, actual)) s = {Code::uncertain};
  else if (actual == c.start) {
    const auto wait = WaitForSingleObject(c.auxiliary, 0);
    if (wait == WAIT_TIMEOUT) s = {Code::busy};
    else if (wait != WAIT_OBJECT_0) s = {Code::uncertain};
  } // Different start identity: the recorded process is dead, even if PID reused.
  const auto closed = close_handle(c.auxiliary); if (s) s = closed;
  return s;
}
inline Status recover(const Grant& grant, const char* name) noexcept {
  if (!grant.permitted) return {Code::no_grant};
  if (active.phase != Phase::idle) return {Code::busy};
  if (!valid_name(name)) return {Code::ownership};
  if (grant.memory_bytes < sizeof(Control) + 4096) return {Code::memory_quota};
  auto s = install_hooks(); if (!s) return s;
  if (next_generation == UINT64_MAX) return {Code::overflow};
  active = Control{}; auto& c = active;
  c.generation = ++next_generation; c.phase = Phase::faulted;
  c.plan.workspace_bytes = sizeof(Control) + 4096;
  s = root_open(c, grant.root);
  if (s) { std::memcpy(c.name, name, 48); s = paths(c); }
  if (s) {
    c.lock = CreateFileA(c.manifest, GENERIC_READ | GENERIC_WRITE | DELETE, 0, nullptr, OPEN_EXISTING,
                         FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (c.lock == INVALID_HANDLE_VALUE) s = win(Code::busy);
    else if (!identity(c.lock, c.manifest_volume, c.manifest_id, false)) s = {Code::ownership};
  }
  unsigned char m[manifest_bytes] = {};
  if (s) {
    if (!exact_size(c.lock, manifest_bytes)) s = {Code::ownership};
    else s = transfer(c, c.lock, m, manifest_bytes, false, false);
  }
  if (s) {
    if (std::memcmp(m, "BLSPMAN1", 8) || get(m + 8) != 1 || get(m + 120) != crc(m, 120)
        || get(m + 48) != c.root_volume || get(m + 56) != c.root_id
        || !get(m + 72) || get(m + 112) != 0) s = {Code::ownership};
  }
  if (s) {
    Grant recorded; recorded.permitted = true; recorded.root = c.root;
    recorded.memory_bytes = UINT64_MAX; recorded.disk_bytes = UINT64_MAX;
    recorded.cells = get(m + 88); recorded.block_cells = get(m + 96); recorded.axis = get(m + 104);
    Plan checked;
    if (!plan(recorded, checked) || checked.reserved_disk != get(m + 80)) s = {Code::ownership};
    else { c.n = recorded.cells; c.b = recorded.block_cells; c.axis = recorded.axis; c.plan = checked; }
  }
  if (s) {
    c.pid = get(m + 16); c.start = get(m + 24); c.nonce[0] = get(m + 32); c.nonce[1] = get(m + 40);
    char original[48] = {}; std::memcpy(original, c.name, 48); nonce_name(c);
    if (std::strcmp(c.name, original)) s = {Code::ownership};
  }
  if (s) s = owner_dead(c);
  bool directory_present = false;
  if (s) {
    c.directory_handle = open_directory(c.directory, true);
    if (c.directory_handle == INVALID_HANDLE_VALUE) {
      const auto error = GetLastError();
      if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) s = {Code::uncertain, error};
    } else {
      directory_present = true;
      if (!identity(c.directory_handle, c.run_volume, c.run_id, true)
          || c.run_volume != get(m + 64) || c.run_id != get(m + 72)) s = {Code::ownership};
      else s = known_files(c);
    }
  }
  if (s) {
    c.owns_directory = c.owns_data = directory_present; c.owns_manifest = true;
    c.directory_retiring = !directory_present;
    s = cleanup();
    if (!s) (void)fail(c, s.code, s.os_error);
  } else {
    (void)fail(c, s.code, s.os_error);
    // No ownership was admitted: cleanup closes only acquired handles.
    // If a close fails the stable slot retains it for the exit hook.
    (void)cleanup();
  }
  return s;
}
#else
inline Status cleanup() noexcept { return {}; }
inline Status begin_native(const Grant&) noexcept { return {Code::unsupported}; }
inline Status append(std::uint64_t, const double*, std::uint64_t) noexcept { return {Code::unsupported}; }
inline Status seal(std::uint64_t) noexcept { return {Code::unsupported}; }
inline Status open_reader(std::uint64_t) noexcept { return {Code::unsupported}; }
inline Status next(std::uint64_t, double*, std::uint64_t, std::uint64_t& n) noexcept { n = 0; return {Code::unsupported}; }
inline Status recover(const Grant&, const char*) noexcept { return {Code::unsupported}; }
#endif
inline void abandon(std::uint64_t generation) noexcept {
  Guard guard;
  if (guard && matches(generation)) (void)cleanup();
}
} // detail

class Writer; class Sealed; class Reader;
class Reader {
  std::uint64_t generation_ = 0;
  friend class Sealed;
public:
  Reader() noexcept = default;
  Reader(const Reader&) = delete; Reader& operator=(const Reader&) = delete;
  Reader(Reader&& other) noexcept : generation_(std::exchange(other.generation_, 0)) {}
  Reader& operator=(Reader&&) = delete;
  ~Reader() { detail::abandon(generation_); }
  Status next(double* dst, std::uint64_t capacity, std::uint64_t& count) noexcept {
    detail::Guard guard; if (!guard) { count = 0; return {Code::busy}; }
    return detail::next(generation_, dst, capacity, count);
  }
  Status finish() noexcept {
    detail::Guard guard; if (!guard) return {Code::busy};
    if (!detail::matches(generation_) || detail::active.phase != Phase::checked) return {Code::state};
    const auto s = detail::cleanup(); if (s) generation_ = 0; return s;
  }
  Status cancel() noexcept {
    detail::Guard guard; if (!guard) return {Code::busy};
    if (!detail::matches(generation_)) return {Code::state};
    const auto s = detail::cleanup(); if (s) generation_ = 0; return s;
  }
};
class Sealed {
  std::uint64_t generation_ = 0;
  friend class Writer;
public:
  Sealed() noexcept = default;
  Sealed(const Sealed&) = delete; Sealed& operator=(const Sealed&) = delete;
  Sealed(Sealed&& other) noexcept : generation_(std::exchange(other.generation_, 0)) {}
  Sealed& operator=(Sealed&&) = delete;
  ~Sealed() { detail::abandon(generation_); }
  Status open(Reader& reader) noexcept {
    detail::Guard guard; if (!guard) return {Code::busy};
    if (reader.generation_) return {Code::state};
    const auto s = detail::open_reader(generation_);
    if (s) { reader.generation_ = generation_; generation_ = 0; }
    return s;
  }
  Status cancel() noexcept {
    detail::Guard guard; if (!guard) return {Code::busy};
    if (!detail::matches(generation_)) return {Code::state};
    const auto s = detail::cleanup(); if (s) generation_ = 0; return s;
  }
};
class Writer {
  std::uint64_t generation_ = 0;
public:
  Writer() noexcept = default;
  Writer(const Writer&) = delete; Writer& operator=(const Writer&) = delete;
  Writer(Writer&& other) noexcept : generation_(std::exchange(other.generation_, 0)) {}
  Writer& operator=(Writer&&) = delete;
  ~Writer() { detail::abandon(generation_); }
  Status begin(const Grant& grant) noexcept {
    detail::Guard guard; if (!guard) return {Code::busy};
    if (generation_) return {Code::state};
    const auto s = detail::begin_native(grant);
    // If creation failed and cleanup itself failed, this token and the stable
    // hook still own the pending resource. No dangling local registration.
    if (detail::active.phase != Phase::idle && s.code != Code::busy) generation_ = detail::active.generation;
    return s;
  }
  Status append(const double* values, std::uint64_t count) noexcept {
    detail::Guard guard; if (!guard) return {Code::busy};
    return detail::append(generation_, values, count);
  }
  Status seal(Sealed& sealed) noexcept {
    detail::Guard guard; if (!guard) return {Code::busy};
    if (sealed.generation_) return {Code::state};
    const auto s = detail::seal(generation_);
    if (s) { sealed.generation_ = generation_; generation_ = 0; }
    return s;
  }
  Status cancel() noexcept {
    detail::Guard guard; if (!guard) return {Code::busy};
    if (!detail::matches(generation_)) return {Code::state};
    const auto s = detail::cleanup(); if (s) generation_ = 0; return s;
  }
};
inline Counters counters() noexcept { detail::Guard guard; return guard ? detail::active.counters : Counters{}; }
inline Status primary_status() noexcept { detail::Guard guard; return guard ? detail::active.primary : Status{Code::busy}; }
inline Status cleanup_status() noexcept { detail::Guard guard; return guard ? detail::last_cleanup : Status{Code::busy}; }
inline std::uint64_t reserved_disk() noexcept {
  detail::Guard guard;
  if (!guard) return UINT64_MAX; // Unknown/busy is never reported as free capacity.
  return detail::active.phase != Phase::idle ? detail::active.plan.reserved_disk : 0;
}
// Explicit recognized directory only. No enumeration/sweep API is provided.
inline Status recover(const Grant& grant, const char* run_name) noexcept {
  detail::Guard guard; if (!guard) return {Code::busy};
  return detail::recover(grant, run_name);
}
inline bool supported() noexcept {
#if defined(_WIN32)
  return true;
#else
  return false;
#endif
}
} // blade_private_spool
