#pragma once
// Opt-in C++ pool API element-capacity accounting; not native-heap/RSS peaks.
// No state, includes, hooks or reporting code exists in disabled builds.
#if defined(BLADE_POOL_CAPACITY_DIAGNOSTICS) && BLADE_POOL_CAPACITY_DIAGNOSTICS
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <new>
#include <optional>

namespace blade_pool { namespace capacity_detail {
using Count = std::uint64_t;
static_assert(sizeof(size_t) <= sizeof(Count), "capacity diagnostic requires representable SIZE_T");
enum class Invalid { none, arithmetic, unknown_release, native_delete_exception, unsupported_policy };
enum class OperationKind { allocate, release, flush };

struct Values {
    Count sequence = 0;
    Count active_bytes = 0, retained_bytes = 0, combined_bytes = 0;
    Count active_blocks = 0, retained_blocks = 0;
    Count peak_active = 0, peak_retained = 0, peak_combined = 0;
    Count peak_sequence = 0, active_at_peak = 0, retained_at_peak = 0;
    Count api_in_flight = 0, native_new_in_flight = 0, native_delete_in_flight = 0;
    Count native_attempts = 0, native_successes = 0, native_bad_alloc = 0, native_other_exception = 0;
    Count native_success_bytes = 0, allocation_successes = 0, allocation_failures = 0;
    Count cache_hits = 0, retain_transfers = 0, direct_deletes = 0, evictions = 0, flushes = 0, oom_retries = 0;
};
struct Ledger { Values values{}; Invalid invalid = Invalid::none; };
// Trivial static storage, no heap pointers or teardown-order dependency.
inline Ledger ledger{};
inline std::atomic_flag gate = ATOMIC_FLAG_INIT;
struct Guard {
    Guard() noexcept { while (gate.test_and_set(std::memory_order_acquire)) {} }
    ~Guard() { gate.clear(std::memory_order_release); }
    Guard(const Guard&) = delete;
    Guard& operator=(const Guard&) = delete;
};
inline bool add(Count& value, Count amount = 1) noexcept {
    if (amount > (std::numeric_limits<Count>::max)() - value) return false;
    value += amount; return true;
}
inline bool subtract(Count& value, Count amount = 1) noexcept {
    if (amount > value) return false;
    value -= amount; return true;
}
// Caller owns Guard. Lambdas below perform only fixed POD arithmetic.
template<class F> inline void update_locked(F change) noexcept {
    if (ledger.invalid != Invalid::none) return;
    Values next = ledger.values;
    bool valid = add(next.sequence) && change(next);
    next.combined_bytes = next.active_bytes;
    valid = valid && add(next.combined_bytes, next.retained_bytes);
    Count blocks = next.active_blocks;
    valid = valid && add(blocks, next.retained_blocks);
    valid = valid && (next.active_blocks || !next.active_bytes) && (next.retained_blocks || !next.retained_bytes);
    if (!valid) { ledger.invalid = Invalid::arithmetic; return; }
    if (next.active_bytes > next.peak_active) next.peak_active = next.active_bytes;
    if (next.retained_bytes > next.peak_retained) next.peak_retained = next.retained_bytes;
    if (next.combined_bytes > next.peak_combined) {
        next.peak_combined = next.combined_bytes;
        next.peak_sequence = next.sequence;
        next.active_at_peak = next.active_bytes;
        next.retained_at_peak = next.retained_bytes;
    }
    ledger.values = next;
}
template<class F> inline void update(F change) noexcept { Guard guard; update_locked(change); }
inline void invalidate(Invalid reason) noexcept {
    Guard guard;
    if (ledger.invalid == Invalid::none) ledger.invalid = reason;
}
template<class T> inline Count bytes_for(size_t cells) noexcept {
    if (cells > (std::numeric_limits<size_t>::max)() / sizeof(T)
        || cells > (std::numeric_limits<Count>::max)() / sizeof(T)) {
        invalidate(Invalid::arithmetic); return 0;
    }
    return Count(cells) * sizeof(T);
}
struct Operation {
    OperationKind kind;
    bool succeeded = false;
    explicit Operation(OperationKind k, bool policy_supported = true) noexcept : kind(k) {
        if (!policy_supported) invalidate(Invalid::unsupported_policy);
        update([=](Values& n) { return add(n.api_in_flight) && (k != OperationKind::flush || add(n.flushes)); });
    }
    void success() noexcept {
        succeeded = true;
        update([](Values& n) { return add(n.allocation_successes); });
    }
    ~Operation() {
        const bool failed = kind == OperationKind::allocate && !succeeded;
        update([=](Values& n) { return subtract(n.api_in_flight) && (!failed || add(n.allocation_failures)); });
    }
    Operation(const Operation&) = delete;
    Operation& operator=(const Operation&) = delete;
};

#if defined(BLADE_POOL_CAPACITY_TESTING) && BLADE_POOL_CAPACITY_TESTING
// Standalone probe seam only. Runs outside all ledger locks, can throw/reenter.
inline void (*native_new_test_hook)(Count) = nullptr;
#endif
template<class T> inline T* native_new(size_t cells) {
    const Count bytes = bytes_for<T>(cells);
    update([](Values& n) { return add(n.native_attempts) && add(n.native_new_in_flight); });
    try {
#if defined(BLADE_POOL_CAPACITY_TESTING) && BLADE_POOL_CAPACITY_TESTING
        if (native_new_test_hook) native_new_test_hook(bytes);
#endif
        T* result = new T[cells];
        update([=](Values& n) {
            return subtract(n.native_new_in_flight) && add(n.native_successes)
                && add(n.native_success_bytes, bytes) && add(n.active_bytes, bytes) && add(n.active_blocks);
        });
        return result;
    } catch (const std::bad_alloc&) {
        update([](Values& n) { return subtract(n.native_new_in_flight) && add(n.native_bad_alloc); });
        throw;
    } catch (...) {
        update([](Values& n) { return subtract(n.native_new_in_flight) && add(n.native_other_exception); });
        throw;
    }
}
inline void begin_delete() noexcept {
    update([](Values& n) { return add(n.native_delete_in_flight); });
}
template<class T> inline void native_delete(T* p, size_t cells) {
    if (!p) { delete[] p; return; }
    const Count bytes = bytes_for<T>(cells);
    begin_delete();
    try { delete[] p; }
    catch (...) { invalidate(Invalid::native_delete_exception); throw; }
    update([=](Values& n) {
        return subtract(n.native_delete_in_flight) && subtract(n.active_bytes, bytes)
            && subtract(n.active_blocks) && add(n.direct_deletes);
    });
}
inline void reuse_locked(size_t bytes) noexcept {
    update_locked([=](Values& n) {
        return subtract(n.retained_bytes, bytes) && subtract(n.retained_blocks)
            && add(n.active_bytes, bytes) && add(n.active_blocks) && add(n.cache_hits);
    });
}
inline void retain_locked(size_t bytes) noexcept {
    update_locked([=](Values& n) {
        return subtract(n.active_bytes, bytes) && subtract(n.active_blocks)
            && add(n.retained_bytes, bytes) && add(n.retained_blocks) && add(n.retain_transfers);
    });
}
inline void evict_locked(size_t bytes) noexcept {
    update_locked([=](Values& n) {
        return subtract(n.native_delete_in_flight) && subtract(n.retained_bytes, bytes)
            && subtract(n.retained_blocks) && add(n.evictions);
    });
}
inline void retry() noexcept { update([](Values& n) { return add(n.oom_retries); }); }
inline void unknown_release() noexcept { invalidate(Invalid::unknown_release); }
struct Snapshot {
    Invalid invalid;
    Count valid_through_sequence;
    std::optional<Values> values; // absent = null/unmeasured, never a forged numeric zero
};
inline Snapshot snapshot() noexcept {
    Guard guard;
    return {ledger.invalid, ledger.values.sequence,
            ledger.invalid == Invalid::none ? std::optional<Values>(ledger.values) : std::nullopt};
}
#if defined(BLADE_POOL_CAPACITY_TESTING) && BLADE_POOL_CAPACITY_TESTING
// No reset API. These deliberately INVALIDATE a fresh probe's numeric completeness.
inline void test_capacity_overflow() noexcept {
    update([](Values& n) { return add(n.active_bytes, (std::numeric_limits<Count>::max)()); });
}
inline void test_capacity_underflow() noexcept {
    update([](Values& n) { return subtract(n.active_bytes, (std::numeric_limits<Count>::max)()); });
}
inline void test_sequence_overflow() noexcept {
    update([](Values& n) { return add(n.sequence, (std::numeric_limits<Count>::max)()); });
}
inline void test_inflight_overflow() noexcept {
    update([](Values& n) { return add(n.api_in_flight, (std::numeric_limits<Count>::max)()) && add(n.api_in_flight); });
}
inline void test_counter_overflow() noexcept {
    update([](Values& n) { return add(n.native_attempts, (std::numeric_limits<Count>::max)()) && add(n.native_attempts); });
}
inline void test_request_product_overflow() noexcept {
    (void)bytes_for<double>((std::numeric_limits<size_t>::max)());
}
#endif
}} // namespace blade_pool::capacity_detail

namespace blade_pool {
inline capacity_detail::Snapshot capacity_snapshot_v1() noexcept { return capacity_detail::snapshot(); }
}
#endif
