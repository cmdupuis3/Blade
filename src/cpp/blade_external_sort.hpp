// The EXTERNAL STABLE SORT (round 4 wave 4, lane D item D1 / ST14; plan rule
// `external-sort v1`). Runs only under the explicit private-spool grant
// (BLADE_PRIVATE_SPOOL); every byte it puts on disk goes through
// blade_private_spool.hpp's D06 protocol (private run directory, ownership
// manifest, quotas, per-block CRCs, cleanup on every exit path).
//
// The contract is `sort(A, key)`'s (docs/features/sql.md section 9): stable
// ascending under the TOTAL preorder of blade_rt::sort_key_less. Each key is
// mapped to an order-preserving 64-bit code (key_code: every NaN -> 0, the
// least, all tie; -0.0 and +0.0 one code; floats by their sign-flipped bits;
// integers by their offset-binary bits), and records (code, ordinal) are
// ordered lexicographically -- a TOTAL order whose ordinal tie-break IS the
// stable order. So the external result equals the resident stable sort's
// exactly: equal keys and NaNs keep their input order.
//
// Algorithm: run generation (each key evaluated ONCE, ascending input order,
// into a bounded run buffer of C records; each run sorted and appended to the
// one spool file in blocks of B records), seal, then ONE bounded-fan-in merge:
// R = ceil(n / C) cursors, one block buffer each, read back by random-access
// block replay (read_block, every block's CRC checked, every block read
// exactly once), a binary heap over the cursors' head records; each popped
// ordinal is handed to `emit(position, ordinal)` in sorted order. The shape is
// chosen so BOTH phases fit the tracked memory allowance; when the merge's R
// cursors cannot fit (n beyond roughly (mem / 16)^1.5 records) the sort is
// REFUSED (memory_quota) -- no multi-pass merge in v1.
#pragma once
#include "blade_private_spool.hpp"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <new>
#include <type_traits>

namespace blade_external_sort {
namespace ps = blade_private_spool;
#ifdef BLADE_PRIVATE_SPOOL_TESTING
namespace testing { inline std::uint64_t corrupt_block = UINT64_MAX; }
#endif

// The order-preserving code of a key under blade_rt::sort_key_less.
template <class K> inline std::uint64_t key_code(K k) noexcept {
  if constexpr (std::is_floating_point<K>::value) {
    double x = static_cast<double>(k);          // float -> double is exact
    if (x != x) return 0;                       // every NaN: the least key, all tie
    if (x == 0.0) x = 0.0;                      // -0.0 and +0.0 tie
    std::uint64_t b = 0; std::memcpy(&b, &x, 8);
    b = (b >> 63) ? ~b : (b | (std::uint64_t(1) << 63));
    return b + 1;                               // after every NaN
  } else if constexpr (std::is_same<K, bool>::value) {
    return k ? 1u : 0u;
  } else {
    return static_cast<std::uint64_t>(static_cast<std::int64_t>(k)) ^ (std::uint64_t(1) << 63);
  }
}

struct Record { std::uint64_t code, ord; };
static_assert(sizeof(Record) == 16, "an external-sort record is two 64-bit cells");
inline bool before(const Record& a, const Record& b) noexcept {
  return a.code < b.code || (a.code == b.code && a.ord < b.ord);
}

struct Shape {
  std::uint64_t n = 0, block = 0, run = 0, runs = 0;
  std::uint64_t run_bytes = 0, merge_bytes = 0;
};
// Fixed tracked overhead beside the buffers: the spool's control slot and
// stack reserve, plus this driver's own small state.
constexpr std::uint64_t fixed_bytes = sizeof(ps::detail::Control) + 4096 + 1024;
constexpr std::uint64_t cursor_bytes = 48; // per run: position, end, length, index, heap slot

// Choose block B and run C (a multiple of B) records so the run phase
// (C records + the spool's 16 B block buffer) and the merge phase (R block
// buffers + R cursors + the spool buffer) both fit `memory`.
inline ps::Status shape(std::uint64_t n, std::uint64_t memory, Shape& out) noexcept {
  out = {}; out.n = n;
  if (memory <= fixed_bytes) return {ps::Code::memory_quota};
  const std::uint64_t avail = memory - fixed_bytes;
  std::uint64_t b = static_cast<std::uint64_t>(std::sqrt(static_cast<double>(avail / 32)));
  if (b < 1) b = 1;
  if (b > ps::max_block_cells / 2) b = ps::max_block_cells / 2;
  if (avail < 32 * b) return {ps::Code::memory_quota};
  std::uint64_t c = ((avail - 16 * b) / 16) / b * b;
  if (c < b) return {ps::Code::memory_quota};
  const std::uint64_t r = n == 0 ? 0 : n / c + (n % c != 0);
  out.block = b; out.run = c; out.runs = r;
  out.run_bytes = 16 * c + 16 * b;
  if (r > (UINT64_MAX - 16 * b) / (16 * b + cursor_bytes)) return {ps::Code::overflow};
  out.merge_bytes = r * (16 * b + cursor_bytes) + 16 * b;
  if (out.merge_bytes > avail) return {ps::Code::memory_quota};
  return {};
}

struct Stats {
  Shape shape{};
  ps::Plan plan{};
  ps::Counters counters{};
};

// Sort n records under the grant: key_at(i) -> the key code of input cell i
// (each called once, i ascending); emit(position, ordinal) receives the sorted
// order. `owner` is the live token (1 writer, 2 sealed, 3 reader, 0 none) so a
// caller's failure path can cancel it; on any failure the token is cancelled
// here and `cleanup` holds that attempt's status.
template <class KeyAt, class Emit>
inline ps::Status sort(const char* root, std::uint64_t memory, std::uint64_t disk, std::uint64_t axis,
                       std::uint64_t n, KeyAt key_at, Emit emit, Stats& stats, ps::Status& cleanup) noexcept {
  stats = {}; cleanup = {};
  auto s = shape(n, memory, stats.shape);
  if (!s) return s;
  if (n == 0) return {};
  const Shape sh = stats.shape;
  if (n > UINT64_MAX / 2) return {ps::Code::overflow};
  ps::Grant grant{true, root, sizeof(ps::detail::Control) + 4096 + 16 * sh.block, disk, 2 * n, 2 * sh.block, axis};
  s = ps::plan(grant, stats.plan);
  if (!s) return s;
  if (!ps::supported()) return {ps::Code::unsupported};
  ps::Writer writer; ps::Sealed sealed; ps::Reader reader;
  int owner = 0;
  auto abort_with = [&](ps::Status primary) -> ps::Status {
    if (owner == 1) cleanup = writer.cancel();
    else if (owner == 2) cleanup = sealed.cancel();
    else if (owner == 3) cleanup = reader.cancel();
    return primary;
  };
  // ---- run generation ----
  Record* run = new (std::nothrow) Record[static_cast<std::size_t>(sh.run)];
  if (!run) return {ps::Code::allocation};
  s = writer.begin(grant);
  if (s || (s.code != ps::Code::busy && ps::reserved_disk() != 0)) owner = 1;
  if (!s) { delete[] run; return abort_with(s); }
  for (std::uint64_t lo = 0; lo < n; lo += sh.run) {
    const std::uint64_t len = n - lo < sh.run ? n - lo : sh.run;
    for (std::uint64_t i = 0; i < len; ++i) run[i] = Record{key_at(lo + i), lo + i};
    std::sort(run, run + len, before); // a total order: the stable order, in place
    for (std::uint64_t at = 0; at < len; at += sh.block) {
      const std::uint64_t take = len - at < sh.block ? len - at : sh.block;
      s = writer.append(reinterpret_cast<const double*>(run + at), 2 * take);
      if (!s) { delete[] run; return abort_with(s); }
    }
  }
  delete[] run;
  s = writer.seal(sealed);
  if (s) owner = 2;
  if (!s) return abort_with(s);
#ifdef BLADE_PRIVATE_SPOOL_TESTING
  if (testing::corrupt_block < stats.plan.blocks) { // flip one payload byte of a sealed block
    if (std::FILE* f = std::fopen(ps::detail::active.data, "r+b")) {
      const long at = static_cast<long>(ps::header_bytes + testing::corrupt_block * (ps::block_header_bytes + 16 * sh.block)
                                        + ps::block_header_bytes + 3);
      if (std::fseek(f, at, SEEK_SET) == 0) { const int c = std::fgetc(f); std::fseek(f, at, SEEK_SET); std::fputc(c ^ 0xff, f); }
      std::fclose(f);
    }
  }
#endif
  s = sealed.open(reader);
  if (s) owner = 3;
  if (!s) return abort_with(s);
  // ---- one bounded-fan-in merge ----
  const std::uint64_t R = sh.runs, B = sh.block;
  const std::uint64_t blocks_per_run = sh.run / B;
  Record* bufs = new (std::nothrow) Record[static_cast<std::size_t>(R * B)];
  std::uint64_t* next_block = new (std::nothrow) std::uint64_t[static_cast<std::size_t>(R)];
  std::uint64_t* end_block = new (std::nothrow) std::uint64_t[static_cast<std::size_t>(R)];
  std::uint64_t* pos = new (std::nothrow) std::uint64_t[static_cast<std::size_t>(R)];
  std::uint64_t* len = new (std::nothrow) std::uint64_t[static_cast<std::size_t>(R)];
  std::uint64_t* heap = new (std::nothrow) std::uint64_t[static_cast<std::size_t>(R)];
  auto release = [&]() { delete[] bufs; delete[] next_block; delete[] end_block; delete[] pos; delete[] len; delete[] heap; };
  if (!bufs || !next_block || !end_block || !pos || !len || !heap) { release(); return abort_with({ps::Code::allocation}); }
  const std::uint64_t total_blocks = stats.plan.blocks;
  auto refill = [&](std::uint64_t r) -> ps::Status {
    std::uint64_t got = 0;
    auto st = reader.read_block(next_block[r], reinterpret_cast<double*>(bufs + r * B), 2 * B, got);
    if (!st) return st;
    if (got == 0 || got % 2 != 0) return {ps::Code::integrity};
    ++next_block[r]; pos[r] = 0; len[r] = got / 2; return {};
  };
  std::uint64_t live = 0;
  for (std::uint64_t r = 0; r < R; ++r) {
    next_block[r] = r * blocks_per_run;
    const std::uint64_t e = (r + 1) * blocks_per_run;
    end_block[r] = e < total_blocks ? e : total_blocks;
    s = refill(r);
    if (!s) { release(); return abort_with(s); }
    heap[live++] = r;
  }
  auto head = [&](std::uint64_t r) -> const Record& { return bufs[r * B + pos[r]]; };
  auto heap_after = [&](std::uint64_t a, std::uint64_t b) { return before(head(b), head(a)); }; // min-heap
  std::make_heap(heap, heap + live, heap_after);
  std::uint64_t out = 0;
  while (live > 0) {
    std::pop_heap(heap, heap + live, heap_after);
    const std::uint64_t r = heap[live - 1];
    emit(out++, head(r).ord);
    if (++pos[r] == len[r]) {
      if (next_block[r] == end_block[r]) { --live; continue; }
      s = refill(r);
      if (!s) { release(); return abort_with(s); }
    }
    std::push_heap(heap, heap + live, heap_after);
  }
  release();
  if (out != n) return abort_with({ps::Code::integrity});
  s = reader.finish_random();
  if (!s) return abort_with(s);
  stats.counters = ps::counters();
  s = reader.finish();
  cleanup = s;
  if (s || ps::reserved_disk() == 0) owner = 0;
  if (!s) return abort_with(s);
  return {};
}
} // namespace blade_external_sort
