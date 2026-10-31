// Standalone B03 probe. One scenario per fresh process; no ledger reset.
#include <atomic>
#include <barrier>
#include <complex>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <limits>
#include <thread>
#include "nested_array_utilities.hpp"

static int checks = 0, failed = 0;
static void check(bool okay, const char* label) {
    ++checks;
    if (!okay) ++failed;
    std::printf("[%s] %s\n", okay ? "PASS" : "FAIL", label);
}
constexpr size_t cells = 2053, other_cells = 2059, big_cells = 393217;
constexpr std::uint64_t B = 16424, D = 16472, H = 3145736;
static_assert(sizeof(double) == 8 && sizeof(int64_t) == 8, "probe's independent byte constants require 8-byte elements");
#if defined(BLADE_POOL_CACHE_OFF) && BLADE_POOL_CACHE_OFF
constexpr bool cache_on = false;
#else
constexpr bool cache_on = true;
#endif

static void semantic() {
    const size_t ext[1] = {3};
    auto* p = nested_array_utilities::allocate<double*>(ext);
    p[0] = 1.0; p[1] = 2.0; p[2] = 4.0;
    check(p[0] + p[1] + p[2] == 7.0, "semantic checksum=7");
    nested_array_utilities::deallocate<double*>(p, ext);
    blade_pool::flush();
}

#if defined(BLADE_POOL_CAPACITY_DIAGNOSTICS) && BLADE_POOL_CAPACITY_DIAGNOSTICS
namespace cd = blade_pool::capacity_detail;
using V = cd::Values;
static_assert(std::is_trivially_destructible<cd::Ledger>::value, "ledger survives TLS teardown without a destructor");
struct Record { const char* label; cd::Snapshot snapshot; };
static Record records[64];
static size_t record_count = 0;
static bool coherent(const V& v) {
    return v.active_bytes <= v.combined_bytes && v.retained_bytes == v.combined_bytes - v.active_bytes
        && v.active_at_peak <= v.peak_combined && v.retained_at_peak == v.peak_combined - v.active_at_peak
        && v.active_bytes <= v.peak_active && v.retained_bytes <= v.peak_retained
        && v.combined_bytes <= v.peak_combined && v.peak_sequence <= v.sequence;
}
static cd::Snapshot capture(const char* label, bool quiescent = true) {
    auto s = blade_pool::capacity_snapshot_v1();
    check(record_count < 64, "bounded snapshot buffer");
    if (record_count < 64) records[record_count++] = {label, s};
    if (s.values) {
        check(coherent(*s.values), "coherent simultaneous state and combined-peak witness");
        const bool quiet = s.values->api_in_flight == 0 && s.values->native_new_in_flight == 0
            && s.values->native_delete_in_flight == 0;
        check(quiet == quiescent, "snapshot in-flight/quiescent classification");
    }
    return s;
}
static V expect(const char* label, std::uint64_t a, std::uint64_t r,
                std::uint64_t pa, std::uint64_t pr, std::uint64_t pc) {
    auto s = capture(label);
    check(s.invalid == cd::Invalid::none && s.values.has_value(), "capacity completeness valid");
    if (!s.values) return {};
    V v = *s.values;
    check(v.active_bytes == a && v.retained_bytes == r && v.combined_bytes == a + r, "independent current capacity totals");
    check(v.peak_active == pa && v.peak_retained == pr && v.peak_combined == pc, "independent component and simultaneous peaks");
    return v;
}
static void invalid(const char* label, cd::Invalid reason) {
    auto s = capture(label);
    check(s.invalid == reason && !s.values, "sticky invalidity exposes absent numeric state");
}
static double* pool(size_t n = cells) { return blade_pool::alloc<double>(n); }
static void drop(double* p, size_t n = cells) { blade_pool::release(p, n); }

static void small() {
    const size_t empty[1] = {0};
    auto* p = pool(3);
    auto* e = nested_array_utilities::allocate<double*>(empty);
    p[0] = 7; e[0] = 9;
    auto v = expect("small+sentinel", 32, 0, 32, 0, 32);
    check(v.active_blocks == 2 && p[0] + e[0] == 16, "two physical element-capacity blocks and values");
    drop(p, 3);
    expect("small-released", 8, 0, 32, 0, 32);
    nested_array_utilities::deallocate<double*>(e, empty);
    v = expect("rank1-sentinel-released", 0, 0, 32, 0, 32);
    check(v.native_successes == 2 && v.native_success_bytes == 32 && v.direct_deletes == 2
          && v.active_blocks == 0 && v.retain_transfers == 0, "small+empty independent event totals");
}
static void reuse() {
    auto* a = pool(); a[0] = 7;
    const auto address = reinterpret_cast<std::uintptr_t>(a);
    drop(a);
    expect("first-retained", 0, B, B, B, B);
    auto* b = pool();
    check(reinterpret_cast<std::uintptr_t>(b) == address, "exact-size reuse returns original address");
    expect("cache-hit", B, 0, B, B, B);
    auto* i = blade_pool::alloc<int64_t>(cells); i[0] = 11; b[0] = 3;
    check(reinterpret_cast<std::uintptr_t>(i) != address && i[0] + b[0] == 14, "different same-byte type is fresh and values valid");
    expect("typed-miss", 2*B, 0, 2*B, B, 2*B);
    drop(b); blade_pool::release(i, cells);
    expect("both-retained", 0, 2*B, 2*B, 2*B, 2*B);
    blade_pool::flush();
    auto v = expect("reuse-flushed", 0, 0, 2*B, 2*B, 2*B);
    check(v.allocation_successes == 3 && v.native_successes == 2 && v.native_success_bytes == 32848
          && v.cache_hits == 1 && v.retain_transfers == 3 && v.evictions == 2, "reuse/type independent event totals");
}
static void different_count() {
    auto* a = pool(); drop(a);
    auto* b = pool(other_cells); b[0] = 5;
    expect("different-count-active", D, B, D, B, 32896);
    drop(b, other_cells); blade_pool::flush();
    auto v = expect("different-count-flushed", 0, 0, D, 32896, 32896);
    check(v.native_successes == 2 && v.native_success_bytes == 32896 && v.cache_hits == 0, "different count never reuses");
}
static void slots() {
    double* pointers[17];
    for (auto& p : pointers) { p = pool(); p[0] = 1; }
    expect("17-active", 279208, 0, 279208, 0, 279208);
    for (int i = 0; i < 16; ++i) drop(pointers[i]);
    expect("16-retained", B, 262784, 279208, 262784, 279208);
    drop(pointers[16]);
    auto v = expect("slot-eviction", 0, 262784, 279208, 262784, 279208);
    check(v.evictions == 1 && v.retain_transfers == 17, "seventeenth release evicts one oldest slot");
    blade_pool::flush();
    v = expect("slots-flushed", 0, 0, 279208, 262784, 279208);
    check(v.evictions == 17 && v.native_successes == 17 && v.retained_blocks == 0, "all retained slot blocks deleted exactly once");
}
static void budget() {
    auto* a = pool(big_cells); auto* b = pool(big_cells); auto* c = pool(big_cells);
    a[0] = b[0] = c[0] = 1;
    drop(a, big_cells); drop(b, big_cells);
    expect("two-large-retained", H, 6291472, 9437208, 6291472, 9437208);
    drop(c, big_cells);
    auto v = expect("budget-eviction", 0, 6291472, 9437208, 6291472, 9437208);
    check(v.evictions == 1 && v.retain_transfers == 3, "default byte budget evicts one oldest block");
    blade_pool::flush();
    v = expect("budget-flushed", 0, 0, 9437208, 6291472, 9437208);
    check(v.evictions == 3 && v.native_success_bytes == 9437208, "large-block independent totals");
}
static void direct_paths() {
    constexpr size_t huge = 524291; // 4,194,328 bytes, above default cap
    auto* a = pool(huge);
    auto* z = blade_pool::alloc<std::complex<double>>(3);
    a[0] = 5; z[0] = {2, 3};
    const auto total = 4194328 + 3*sizeof(std::complex<double>);
    expect("uncached-types", total, 0, total, 0, total);
    check(a[0] == 5 && z[0].imag() == 3, "typed values before direct release");
    drop(a, huge); blade_pool::release(z, size_t(3));
    auto v = expect("uncached-types-freed", 0, 0, total, 0, total);
    check(v.native_successes == 2 && v.direct_deletes == 2 && v.retain_transfers == 0, "above-cap and non-arithmetic never retained");
}
static void zero_cells() {
    auto* p = pool(0);
    auto v = expect("zero-cell-active-block", 0, 0, 0, 0, 0);
    check(v.active_blocks == 1 && v.native_successes == 1, "zero cells still one successful API block");
    drop(p, 0);
    v = expect("zero-cell-released", 0, 0, 0, 0, 0);
    check(v.active_blocks == 0 && v.direct_deletes == 1, "zero-cell block count closes");
}
static void sentinel(bool strict = false) {
    const size_t ext[2] = {0, 3};
    auto** p = strict ? nested_array_utilities::allocate_strict<double**, nullptr, nullptr>(ext)
                     : nested_array_utilities::allocate<double**>(ext);
    expect("rank2-sentinel", 8, 0, 8, 0, 8);
    if (strict) nested_array_utilities::deallocate_strict<double**, nullptr, nullptr>(p, ext);
    else nested_array_utilities::deallocate<double**>(p, ext);
    blade_pool::flush();
    auto v = expect("rank2-sentinel-outstanding", 8, 0, 8, 0, 8);
    check(v.active_blocks == 1 && v.native_successes == 1 && v.direct_deletes == 0, "unrecoverable sentinel remains allocated, no forged closure");
}
static void unknown(bool strict) {
    auto* p = pool(3); p[0] = 1;
    if (strict) nested_array_utilities::deallocate_strict<double*, nullptr, nullptr>(p, nullptr);
    else nested_array_utilities::deallocate<double*>(p, nullptr);
    invalid("unknown-count-release", cd::Invalid::unknown_release);
    blade_pool::flush();
    invalid("unknown-count-remains-invalid", cd::Invalid::unknown_release);
}
static void null_paths() {
    nested_array_utilities::deallocate<double*>(nullptr, nullptr);
    nested_array_utilities::deallocate_strict<double*, nullptr, nullptr>(nullptr, nullptr);
    blade_pool::release<double>(nullptr, 99);
    auto v = expect("null-deletes-known-and-unknown", 0, 0, 0, 0, 0);
    check(v.native_successes == 0 && v.direct_deletes == 0, "null deletion never fabricates unknown storage or bytes");
}
struct ConstructorThrows { ConstructorThrows() { throw 17; } };
struct DestructorThrows { ~DestructorThrows() noexcept(false) { throw 23; } };
static void constructor_failure() {
    bool caught = false;
    try { (void)blade_pool::alloc<ConstructorThrows>(3); } catch (int tag) { caught = tag == 17; }
    auto v = expect("constructor-failure", 0, 0, 0, 0, 0);
    check(caught && v.native_attempts == 1 && v.native_other_exception == 1 && v.native_successes == 0
          && v.allocation_failures == 1, "constructor exception preserved with zero successful capacity");
}
static void destructor_failure() {
    auto* p = blade_pool::alloc<DestructorThrows>(1);
    bool caught = false;
    try { blade_pool::release(p, size_t(1)); } catch (int tag) { caught = tag == 23; }
    check(caught, "throwing destructor exception preserved");
    invalid("delete-outcome-unknown", cd::Invalid::native_delete_exception);
}
static void cache_off() {
    auto* a = pool(); drop(a);
    auto* b = pool(); auto* i = blade_pool::alloc<int64_t>(cells);
    b[0] = 4; i[0] = 6;
    expect("cache-off-two-active", 32848, 0, 32848, 0, 32848);
    drop(b); blade_pool::release(i, cells); blade_pool::flush();
    auto v = expect("cache-off-closed", 0, 0, 32848, 0, 32848);
    check(v.native_successes == 3 && v.native_success_bytes == 49272 && v.direct_deletes == 3
          && v.cache_hits == 0 && v.retain_transfers == 0, "cache-off counts direct successes and no retained capacity");
}
static void origin_exit() {
    double* pointer = nullptr;
    std::thread origin([&] { pointer = pool(); pointer[0] = 9; });
    origin.join();
    expect("origin-exited-active", B, 0, B, 0, B);
    check(pointer[0] == 9, "active storage survives allocating thread");
    drop(pointer);
    expect("released-on-main", 0, cache_on ? B : 0, B, cache_on ? B : 0, B);
    blade_pool::flush();
    expect("origin-exit-closed", 0, 0, B, cache_on ? B : 0, B);
}
#if !defined(BLADE_POOL_CACHE_OFF) || !BLADE_POOL_CACHE_OFF
static void threads() {
    std::barrier<> barrier(3);
    auto worker = [&](int id) {
        auto* p = pool(); p[0] = 1;
        barrier.arrive_and_wait(); barrier.arrive_and_wait();
        if (id == 0) drop(p);
        barrier.arrive_and_wait(); barrier.arrive_and_wait();
        if (id == 1) drop(p);
        barrier.arrive_and_wait(); barrier.arrive_and_wait();
    };
    std::thread a(worker, 0), b(worker, 1);
    barrier.arrive_and_wait(); expect("threads-two-active", 2*B, 0, 2*B, 0, 2*B); barrier.arrive_and_wait();
    barrier.arrive_and_wait(); expect("threads-mixed", B, B, 2*B, B, 2*B); barrier.arrive_and_wait();
    barrier.arrive_and_wait(); expect("threads-two-retained", 0, 2*B, 2*B, 2*B, 2*B); barrier.arrive_and_wait();
    a.join(); b.join();
    auto v = expect("threads-exited", 0, 0, 2*B, 2*B, 2*B);
    check(v.native_successes == 2 && v.evictions == 2, "thread Reapers delete only retained blocks");
}
static void retainer_exit() {
    double* pointer = nullptr;
    std::thread origin([&] { pointer = pool(); pointer[0] = 1; }); origin.join();
    std::barrier<> barrier(2);
    std::thread releaser([&] { drop(pointer); barrier.arrive_and_wait(); barrier.arrive_and_wait(); });
    barrier.arrive_and_wait(); expect("other-thread-retained", 0, B, B, B, B); barrier.arrive_and_wait();
    releaser.join();
    expect("retaining-thread-exited", 0, 0, B, B, B);
}
static std::atomic<bool> saw_dead{false};
struct Late {
    double* p = nullptr;
    ~Late() { if (p) { saw_dead.store(blade_pool::tl_cache.dead); drop(p); } }
};
static thread_local Late late;
static void late_release() {
    std::thread worker([] { late.p = pool(); auto* q = pool(); drop(q); }); worker.join();
    auto v = expect("post-reaper-release", 0, 0, 2*B, B, 2*B);
    check(saw_dead.load() && v.native_successes == 2 && v.retain_transfers == 1
          && v.evictions == 1 && v.direct_deletes == 1, "late TLS release uses existing dead-cache direct path");
}
static void observer() {
    std::barrier<> checkpoint(3), phase_end(3);
    std::atomic<int> phase_done{0};
    auto worker = [&] {
        for (int phase = 0; phase < 4; ++phase) {
            auto* p = pool();
            // Both allocations remain active until the observer samples this checkpoint.
            checkpoint.arrive_and_wait(); checkpoint.arrive_and_wait();
            p[0] = 1; drop(p);
            for (int i = 1; i < 16; ++i) { auto* q = pool(); q[0] = 1; drop(q); }
            phase_done.fetch_add(1);
            phase_end.arrive_and_wait();
        }
    };
    std::thread a(worker), b(worker);
    bool okay = true;
    std::uint64_t last_sequence = 0, last_peak = 0;
    size_t checkpoint_observed = 0, stress_observed = 0;
    auto observe = [&](const cd::Snapshot& s) {
        if (!s.values || !coherent(*s.values)) { okay = false; return; }
        okay = okay && s.values->sequence >= last_sequence && s.values->peak_combined >= last_peak;
        last_sequence = s.values->sequence; last_peak = s.values->peak_combined;
    };
    const char* names[] = {"observer-active-1", "observer-active-17", "observer-active-33", "observer-active-49"};
    for (int phase = 0; phase < 4; ++phase) {
        phase_done.store(0);
        checkpoint.arrive_and_wait();
        auto s = capture(names[phase]); observe(s); ++checkpoint_observed;
        okay = okay && s.values && s.values->active_bytes == 2*B && s.values->retained_bytes == 0;
        checkpoint.arrive_and_wait();
        // Optional stress: workers may finish this segment before the observer is scheduled.
        // Required evidence is the four live checkpoints, never this loop's sample count.
        size_t phase_observed = 0;
        while (phase_done.load() != 2 && phase_observed < 250000) {
            observe(blade_pool::capacity_snapshot_v1()); ++phase_observed; ++stress_observed;
        }
        phase_end.arrive_and_wait();
    }
    a.join(); b.join();
    auto s = capture("observer-joined");
    std::printf("observer checkpoint_samples=%zu optional_stress_samples=%zu\n",
                checkpoint_observed, stress_observed);
    check(checkpoint_observed == 4, "four required snapshots precede workers' next work segments");
    check(okay && s.values && s.values->active_bytes == 0 && s.values->retained_bytes == 0
          && s.values->peak_combined == 2*B && s.values->native_successes == 2
          && s.values->allocation_successes == 128 && s.values->cache_hits == 126 && s.values->evictions == 2,
          "live checkpoint/optional stress snapshots coherent and independent final event totals");
}
#endif
#if defined(BLADE_POOL_CAPACITY_TESTING) && BLADE_POOL_CAPACITY_TESTING
static int remaining_failures = 0;
static cd::Count failure_bytes = D;
static void fail_native(cd::Count bytes) {
    if (bytes == failure_bytes && remaining_failures > 0) { --remaining_failures; throw std::bad_alloc{}; }
}
static void oom(int failures) {
    auto* p = pool(); drop(p);
    remaining_failures = failures; cd::native_new_test_hook = fail_native;
    bool caught = false; double* q = nullptr;
    try { q = pool(other_cells); } catch (const std::bad_alloc&) { caught = true; }
    cd::native_new_test_hook = nullptr;
    auto v = expect("oom-outcome", failures == 1 ? D : 0, 0, failures == 1 ? D : B, B, failures == 1 ? D : B);
    check(caught == (failures == 2) && v.native_attempts == 3 && v.native_bad_alloc == std::uint64_t(failures)
          && v.native_successes == std::uint64_t(failures == 1 ? 2 : 1)
          && v.native_success_bytes == std::uint64_t(failures == 1 ? 32896 : B)
          && v.oom_retries == 1 && v.evictions == 1, "failed attempts never create capacity; retry counted once");
#if defined(BLADE_POOL_CACHE_STATS) && BLADE_POOL_CACHE_STATS
    check(blade_pool::g_stats.fresh.load() == 2, "legacy fresh remains misses, including failed request");
#endif
    if (q) { q[0] = 1; drop(q, other_cells); blade_pool::flush(); }
}
static void fail_small() {
    remaining_failures = 1; failure_bytes = 24; cd::native_new_test_hook = fail_native;
    bool caught = false;
    try { (void)pool(3); } catch (const std::bad_alloc&) { caught = true; }
    cd::native_new_test_hook = nullptr;
    auto v = expect("ineligible-failure", 0, 0, 0, 0, 0);
    check(caught && v.native_attempts == 1 && v.native_bad_alloc == 1 && v.native_successes == 0
          && v.oom_retries == 0 && v.flushes == 0 && v.allocation_failures == 1, "ineligible failure gets no extra retry/flush");
}
static void fail_eligible_off() {
    remaining_failures = 1; failure_bytes = B; cd::native_new_test_hook = fail_native;
    bool caught = false;
    try { (void)pool(); } catch (const std::bad_alloc&) { caught = true; }
    cd::native_new_test_hook = nullptr;
    auto v = expect("cache-off-eligible-failure", 0, 0, 0, 0, 0);
    check(caught && v.native_attempts == 1 && v.native_bad_alloc == 1 && v.native_successes == 0
          && v.oom_retries == 0 && v.flushes == 0, "cache-off eligible failure has no retry or fake capacity");
#if defined(BLADE_POOL_CACHE_STATS) && BLADE_POOL_CACHE_STATS
    check(blade_pool::g_stats.fresh.load() == 1, "cache-off legacy eligible fresh includes failure");
#endif
}
static void reentrant_hook(cd::Count) {
    cd::native_new_test_hook = nullptr;
    capture("native-callback-in-flight", false);
    auto* p = pool(3); p[0] = 3; drop(p, 3);
}
static void reentry() {
    cd::native_new_test_hook = reentrant_hook;
    auto* p = pool();
    auto v = expect("reentrant-native-return", B, 0, B, 0, B);
    check(v.native_successes == 2 && v.native_success_bytes == B+24 && v.direct_deletes == 1,
          "native callback reenters without global-lock deadlock");
    drop(p); blade_pool::flush();
}
#endif

static const char* reason(cd::Invalid v) {
    switch (v) {
    case cd::Invalid::none: return "none";
    case cd::Invalid::arithmetic: return "arithmetic";
    case cd::Invalid::unknown_release: return "unknown_release";
    case cd::Invalid::native_delete_exception: return "native_delete_exception";
    case cd::Invalid::unsupported_policy: return "unsupported_policy";
    }
    return "unknown";
}
static unsigned long long u(std::uint64_t value) { return static_cast<unsigned long long>(value); }
static double* (* volatile leak_allocate)(size_t) = pool;
static void intentional_leak() {
    // Opaque call prevents allocation-elision of the expected-leak negative; no
    // global application pointer is retained by the probe or capacity ledger.
    auto* p = leak_allocate(3); p[0] = 1;
    expect("intentional-unreleased", 24, 0, 24, 0, 24);
}
static void write_report(FILE* f, const char* mode) {
    std::fprintf(f, "{\"schema\":1,\"scope\":\"cpp-blade-pool-api-element-capacity\","
                 "\"measurement\":\"api-linearized-accounted-capacity\",\"physical_native_live_peak\":null,"
                 "\"allocator_usable_bytes\":null,\"rss\":null,\"commit\":null,\"dram_bytes\":null,"
                 "\"excluded\":[\"logical-liveness\",\"pointer-skeletons\",\"LLVM\",\"provider-raw-buffers\",\"library-workspaces\",\"allocator-metadata\"],"
                 "\"mode\":\"%s\",\"cache_on\":%s,\"compiler\":\"%s\","
                 "\"policy\":{\"max_block\":%llu,\"budget\":%llu,\"slots\":%llu},"
                 "\"ledger_static_storage_bytes\":%llu,\"checks\":%d,\"failed\":%d,\"snapshots\":[",
                 mode, cache_on ? "true" : "false", __VERSION__, u(blade_pool::kMaxBlock),
                 u(blade_pool::kBudget), u(blade_pool::kSlots), u(sizeof(cd::ledger)+sizeof(cd::gate)), checks, failed);
    for (size_t i = 0; i < record_count; ++i) {
        const auto& r = records[i]; const auto& s = r.snapshot;
        std::fprintf(f, "%s{\"label\":\"%s\",\"valid\":%s,\"reason\":\"%s\",\"valid_through_sequence\":%llu,\"quiescent\":%s,\"api_accounted_element_capacity\":",
                     i ? "," : "", r.label, s.values ? "true" : "false", reason(s.invalid), u(s.valid_through_sequence),
                     !s.values ? "null" : (s.values->api_in_flight || s.values->native_new_in_flight || s.values->native_delete_in_flight) ? "false" : "true");
        if (!s.values) { std::fputs("null}", f); continue; }
        const auto& v = *s.values;
        std::fprintf(f, "{\"active_bytes\":%llu,\"retained_bytes\":%llu,\"combined_bytes\":%llu,"
                     "\"active_blocks\":%llu,\"retained_blocks\":%llu,\"peak_active\":%llu,\"peak_retained\":%llu,\"peak_combined\":%llu,"
                     "\"combined_peak_witness\":{\"sequence\":%llu,\"active\":%llu,\"retained\":%llu},"
                     "\"api_in_flight\":%llu,\"native_new_in_flight\":%llu,\"native_delete_in_flight\":%llu,"
                     "\"native_attempts\":%llu,\"native_successes\":%llu,\"native_bad_alloc\":%llu,\"native_other_exception\":%llu,"
                     "\"native_success_bytes\":%llu,\"allocation_successes\":%llu,\"allocation_failures\":%llu,"
                     "\"cache_hits\":%llu,\"retain_transfers\":%llu,\"direct_deletes\":%llu,\"evictions\":%llu,\"flushes\":%llu,\"oom_retries\":%llu}}",
                     u(v.active_bytes), u(v.retained_bytes), u(v.combined_bytes), u(v.active_blocks), u(v.retained_blocks),
                     u(v.peak_active), u(v.peak_retained), u(v.peak_combined), u(v.peak_sequence), u(v.active_at_peak), u(v.retained_at_peak),
                     u(v.api_in_flight), u(v.native_new_in_flight), u(v.native_delete_in_flight), u(v.native_attempts), u(v.native_successes),
                     u(v.native_bad_alloc), u(v.native_other_exception), u(v.native_success_bytes), u(v.allocation_successes),
                     u(v.allocation_failures), u(v.cache_hits), u(v.retain_transfers), u(v.direct_deletes), u(v.evictions), u(v.flushes), u(v.oom_retries));
    }
    std::fputs("]}\n", f);
}
static bool run_mode(const char* mode) {
    if (!std::strcmp(mode, "small")) small();
    else if (!std::strcmp(mode, "null")) null_paths();
    else if (!std::strcmp(mode, "zero")) zero_cells();
    else if (!std::strcmp(mode, "sentinel")) sentinel();
    else if (!std::strcmp(mode, "strict-sentinel")) sentinel(true);
    else if (!std::strcmp(mode, "direct")) direct_paths();
    else if (!std::strcmp(mode, "unknown")) unknown(false);
    else if (!std::strcmp(mode, "unknown-strict")) unknown(true);
    else if (!std::strcmp(mode, "ctor")) constructor_failure();
    else if (!std::strcmp(mode, "dtor")) destructor_failure();
    else if (!std::strcmp(mode, "origin-exit")) origin_exit();
    else if (!std::strcmp(mode, "unsupported-policy")) {
        auto* p = pool(3); p[0] = 1; drop(p, 3); invalid("unsupported-policy", cd::Invalid::unsupported_policy);
    }
    else if (!std::strcmp(mode, "cache-off")) { if (cache_on) return false; cache_off(); }
#if !defined(BLADE_POOL_CACHE_OFF) || !BLADE_POOL_CACHE_OFF
    else if (!std::strcmp(mode, "reuse")) reuse();
    else if (!std::strcmp(mode, "different-count")) different_count();
    else if (!std::strcmp(mode, "slots")) slots();
    else if (!std::strcmp(mode, "budget")) budget();
    else if (!std::strcmp(mode, "threads")) threads();
    else if (!std::strcmp(mode, "retainer-exit")) retainer_exit();
    else if (!std::strcmp(mode, "late")) late_release();
    else if (!std::strcmp(mode, "observer")) observer();
#endif
#if defined(BLADE_POOL_CAPACITY_TESTING) && BLADE_POOL_CAPACITY_TESTING
    else if (!std::strcmp(mode, "fail-small")) fail_small();
    else if (!std::strcmp(mode, "fail-eligible-off")) { if (cache_on) return false; fail_eligible_off(); }
    else if (!std::strcmp(mode, "reentry")) reentry();
    else if (!std::strcmp(mode, "overflow")) { auto* p = pool(1); cd::test_capacity_overflow(); drop(p, 1); invalid("overflow", cd::Invalid::arithmetic); }
    else if (!std::strcmp(mode, "underflow")) { cd::test_capacity_underflow(); invalid("underflow", cd::Invalid::arithmetic); }
    else if (!std::strcmp(mode, "sequence-overflow")) { cd::test_sequence_overflow(); invalid("sequence-overflow", cd::Invalid::arithmetic); }
    else if (!std::strcmp(mode, "inflight-overflow")) { cd::test_inflight_overflow(); invalid("inflight-overflow", cd::Invalid::arithmetic); }
    else if (!std::strcmp(mode, "counter-overflow")) { cd::test_counter_overflow(); invalid("counter-overflow", cd::Invalid::arithmetic); }
    else if (!std::strcmp(mode, "product-overflow")) { cd::test_request_product_overflow(); invalid("product-overflow", cd::Invalid::arithmetic); }
#if !defined(BLADE_POOL_CACHE_OFF) || !BLADE_POOL_CACHE_OFF
    else if (!std::strcmp(mode, "oom1")) oom(1);
    else if (!std::strcmp(mode, "oom2")) oom(2);
#endif
#endif
    else if (!std::strcmp(mode, "leak")) intentional_leak();
    else return false;
    return true;
}
#endif

int main(int argc, char** argv) {
    if (argc != 3 || !std::filesystem::path(argv[2]).is_absolute()) {
        std::fprintf(stderr, "usage: probe <scenario> <ABSOLUTE-report.json>\n"); return 2;
    }
    if (!std::strcmp(argv[1], "semantic")) semantic();
    else {
#if defined(BLADE_POOL_CAPACITY_DIAGNOSTICS) && BLADE_POOL_CAPACITY_DIAGNOSTICS
        if (!run_mode(argv[1])) { std::fprintf(stderr, "unsupported scenario/build combination\n"); return 2; }
#else
        std::fprintf(stderr, "capacity diagnostic compiled out; only semantic scenario supported\n"); return 2;
#endif
    }
    FILE* f = std::fopen(argv[2], "wb");
    if (!f) { std::perror("report"); return 2; }
#if defined(BLADE_POOL_CAPACITY_DIAGNOSTICS) && BLADE_POOL_CAPACITY_DIAGNOSTICS
    write_report(f, argv[1]);
#else
    std::fprintf(f, "{\"schema\":1,\"capacity_diagnostics\":\"compiled-out\",\"semantic_checksum\":7,\"checks\":%d,\"failed\":%d}\n", checks, failed);
#endif
    const bool written = std::fclose(f) == 0;
    std::printf("POOL CAPACITY PROBE: %d/%d passed\n", checks-failed, checks);
    return failed || !written ? 1 : 0;
}
