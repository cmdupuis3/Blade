/* blade_member.h -- membership hoisting's search structure (round 3, item
 * B-T1; Blade.Optimize.planMembershipHoist, plan rule `member-hoist`).
 *
 * ONE C core, two lanes. The C++ lane includes this header directly (a
 * program with a planned hoist: CodeGen appends the #include); the LLVM lane
 * reaches the same functions through thin exports in blade_llvm_shim.c,
 * which includes this header -- so the representation chooser, the build,
 * the probe and the build report are one text for both back ends. The
 * interpreter does NOT use it (src/Interp keeps its own linear scan): the
 * independent oracle of `blade test interp`.
 *
 * Plain C99 that is also valid C++ (every allocation is cast, no designated
 * initializers, no VLAs). Every function is `static inline`: no symbol is
 * exported from this header, so including it twice in one program (the
 * shim and a C++ TU are never linked together anyway) cannot collide.
 *
 * THE OPERATION. A set of m Int64 keys (a rank-1 stored array or the compact
 * buffer of a rank-1 compound; duplicates allowed, any order, any values
 * including INT64_MIN and INT64_MAX) is built ONCE into a search structure;
 * each probe answers "is x equal to some cell?" -- exactly integer `==`
 * against every cell, i.e. the as-written linear scan's boolean. The answer
 * never depends on the representation:
 *   LINEAR  -- the cells themselves (not owned), scanned with early exit;
 *   BITMAP  -- one bit per key in [lo, hi] (span = hi - lo + 1), a probe
 *              outside the span is false without touching the words;
 *   HASH    -- open addressing over a power-of-two capacity >= 2m (load
 *              <= 1/2, so a probe always meets an empty slot), Fibonacci
 *              hashing of the key's bits, linear probing. Occupancy is its
 *              OWN byte array: no key value is a sentinel, so 0, -1,
 *              INT64_MIN are ordinary keys.
 * Build-side duplicates collapse (a key is inserted once); the probe side is
 * the caller's loop, untouched (its order and multiplicity are the
 * program's). Nothing but the boolean is ever exposed: no iteration over the
 * structure exists, so no representation order can escape.
 *
 * THE CHOOSER picks the PREFERRED representation, deterministically: a
 * function of m, min, max and `forced` only -- never of timing, addresses
 * or the machine (Blade.Optimize.memberChooseRep restates it for the plan
 * record):
 *   m == 0                      -> LINEAR (empty: every probe is false;
 *                                  decided BEFORE min / max exist)
 *   forced LINEAR               -> LINEAR
 *   forced BITMAP               -> BITMAP when the span fits the bit cap
 *                                  (2^26 bits = 8 MiB), else HASH
 *   forced HASH                 -> HASH
 *   auto, m <= 16               -> LINEAR (tiny sets never lose)
 *   auto, span <= max(64 m, 4096) and span <= 2^26 -> BITMAP
 *   auto, otherwise             -> HASH
 * The span is computed as the UNSIGNED difference hi - lo (exact for every
 * hi >= lo, no signed overflow) and compared without forming hi - lo + 1;
 * 64 m is formed only below 2^57.
 *
 * THE ACTUAL REPRESENTATION can differ from the preferred one: an allocation
 * the build cannot make degrades it -- a bitmap whose words cannot be
 * allocated is built as a hash (fallback bit BLADE_MEMBER_FELL_BITMAP_ALLOC),
 * a hash whose keys or occupancy cannot be allocated (or whose capacity is
 * not addressable) is built as linear (BLADE_MEMBER_FELL_HASH_ALLOC /
 * _HASH_CAPACITY), a half-made table freed first -- always a representation
 * that answers the same booleans, never an abort. The build records both
 * (`preferred`, `kind`) and the reasons (`fallback`), and its CALLER reports
 * them (`blade_member_note_i64`: a `[member] ...` line on stderr under
 * BLADE_MEMBER_VERBOSE=1, both lanes; the C++ lane's run record also lists
 * every build under "member_hoist"). The probe stays read-only.
 *
 * ALLOCATION HOOKS. BLADE_MEMBER_MALLOC / _CALLOC / _FREE default to the C
 * library's; a test driver defines them before including this header to
 * inject failures deterministically and count what was freed.
 *
 * LIFETIME. `blade_member_build_i64` fills a caller-owned struct (the C++
 * lane's file-scope static, the LLVM lane's entry-block alloca);
 * `blade_member_free_i64` releases what the build allocated and resets the
 * struct to BLADE_MEMBER_NONE. A LINEAR structure borrows the set's cells: it
 * is valid exactly while the set's storage is (the plan builds it right
 * before the host binding and frees it right after; the set is never
 * assigned anywhere -- planMembershipHoist's admission). BLADE_MEMBER_NONE
 * (all-zero, the static initializer) is "not built": the C++ lane's probe
 * tests it first and takes the as-written scan.
 */
#ifndef BLADE_MEMBER_H
#define BLADE_MEMBER_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifndef BLADE_MEMBER_MALLOC
#define BLADE_MEMBER_MALLOC(bytes) malloc(bytes)
#endif
#ifndef BLADE_MEMBER_CALLOC
#define BLADE_MEMBER_CALLOC(n, size) calloc((n), (size))
#endif
#ifndef BLADE_MEMBER_FREE
#define BLADE_MEMBER_FREE(p) free(p)
#endif

#define BLADE_MEMBER_NONE   0
#define BLADE_MEMBER_LINEAR 1
#define BLADE_MEMBER_BITMAP 2
#define BLADE_MEMBER_HASH   3

/* Why the actual representation is not the preferred one (bit flags). */
#define BLADE_MEMBER_FELL_BITMAP_ALLOC  1
#define BLADE_MEMBER_FELL_HASH_ALLOC    2
#define BLADE_MEMBER_FELL_HASH_CAPACITY 4

/* The chooser's constants (keep Blade.Optimize.memberChooseRep in step: the
 * `blade plan` record of a literal set states the representation the build
 * will prefer). */
#define BLADE_MEMBER_LINEAR_MAX   16
#define BLADE_MEMBER_SPAN_PER_KEY 64
#define BLADE_MEMBER_SPAN_FLOOR   4096
#define BLADE_MEMBER_SPAN_BITS    ((uint64_t)1 << 26)

#define BLADE_MEMBER_GOLDEN 0x9E3779B97F4A7C15ULL

typedef struct blade_member_i64 {
    int kind;             /* the ACTUAL representation */
    int shift;            /* HASH: 64 - log2(capacity) */
    int preferred;        /* the chooser's representation */
    int fallback;         /* BLADE_MEMBER_FELL_* bits: why kind != preferred */
    int64_t m;            /* the set's cells, duplicates included */
    const int64_t *cells; /* LINEAR: the set's own cells (borrowed) */
    int64_t lo;           /* BITMAP: the smallest key */
    uint64_t span_m1;     /* BITMAP: hi - lo, unsigned */
    uint64_t *bits;       /* BITMAP: (span_m1 >> 6) + 1 words (owned) */
    int64_t *keys;        /* HASH: capacity slots (owned) */
    unsigned char *used;  /* HASH: capacity occupancy bytes (owned) */
    uint64_t cap_mask;    /* HASH: capacity - 1 */
} blade_member_i64;

/* The PREFERRED representation for (m, lo, hi) under `forced` (0 = auto,
 * else one of BLADE_MEMBER_LINEAR / _BITMAP / _HASH; any other value reads
 * as auto). `lo` / `hi` are read only when m > 0. */
static inline int blade_member_choose_i64(int64_t m, int64_t lo, int64_t hi, int forced) {
    uint64_t span_m1;
    int bitmapFits, bitmapDense;
    if (m <= 0) return BLADE_MEMBER_LINEAR;
    span_m1 = (uint64_t)hi - (uint64_t)lo;
    bitmapFits = span_m1 < BLADE_MEMBER_SPAN_BITS;
    if (forced == BLADE_MEMBER_LINEAR) return BLADE_MEMBER_LINEAR;
    if (forced == BLADE_MEMBER_BITMAP) return bitmapFits ? BLADE_MEMBER_BITMAP : BLADE_MEMBER_HASH;
    if (forced == BLADE_MEMBER_HASH) return BLADE_MEMBER_HASH;
    if (m <= BLADE_MEMBER_LINEAR_MAX) return BLADE_MEMBER_LINEAR;
    {
        /* span <= max(64 m, 4096)  <=>  span_m1 < limit */
        uint64_t limit = (uint64_t)BLADE_MEMBER_SPAN_FLOOR;
        if (m >= ((int64_t)1 << 57)) limit = UINT64_MAX;
        else if ((uint64_t)m * BLADE_MEMBER_SPAN_PER_KEY > limit) limit = (uint64_t)m * BLADE_MEMBER_SPAN_PER_KEY;
        bitmapDense = span_m1 < limit;
    }
    return (bitmapFits && bitmapDense) ? BLADE_MEMBER_BITMAP : BLADE_MEMBER_HASH;
}

static inline void blade_member_free_i64(blade_member_i64 *t) {
    if (t->bits) BLADE_MEMBER_FREE(t->bits);
    if (t->keys) BLADE_MEMBER_FREE(t->keys);
    if (t->used) BLADE_MEMBER_FREE(t->used);
    memset(t, 0, sizeof *t);
}

/* Build over `cells[0, m)`. Never fails: an allocation that cannot be made
 * degrades the representation and is recorded in `fallback` (see the
 * header note). */
static inline void blade_member_build_i64(blade_member_i64 *t, const int64_t *cells, int64_t m, int forced) {
    int64_t i, lo, hi;
    int kind;
    memset(t, 0, sizeof *t);
    t->m = m < 0 ? 0 : m;
    t->cells = cells;
    if (t->m == 0) {
        t->kind = BLADE_MEMBER_LINEAR;
        t->preferred = BLADE_MEMBER_LINEAR;
        return;
    }
    lo = cells[0];
    hi = cells[0];
    for (i = 1; i < t->m; i++) {
        if (cells[i] < lo) lo = cells[i];
        if (cells[i] > hi) hi = cells[i];
    }
    kind = blade_member_choose_i64(t->m, lo, hi, forced);
    t->preferred = kind;
    if (kind == BLADE_MEMBER_BITMAP) {
        uint64_t span_m1 = (uint64_t)hi - (uint64_t)lo;
        size_t words = (size_t)((span_m1 >> 6) + 1);
        t->bits = (uint64_t *)BLADE_MEMBER_CALLOC(words, sizeof(uint64_t));
        if (t->bits) {
            t->lo = lo;
            t->span_m1 = span_m1;
            for (i = 0; i < t->m; i++) {
                uint64_t d = (uint64_t)cells[i] - (uint64_t)lo;
                t->bits[d >> 6] |= (uint64_t)1 << (d & 63);
            }
            t->kind = BLADE_MEMBER_BITMAP;
            return;
        }
        t->fallback |= BLADE_MEMBER_FELL_BITMAP_ALLOC;
        kind = BLADE_MEMBER_HASH;
    }
    if (kind == BLADE_MEMBER_HASH) {
        /* capacity: the least power of two >= 2m, at least 16; m beyond
         * 2^61 slots cannot be addressed (and would not fit in memory). */
        uint64_t cap = 16;
        int log2cap = 4;
        int ok = (uint64_t)t->m <= ((uint64_t)1 << 61);
        while (ok && cap < (uint64_t)t->m * 2) {
            cap <<= 1;
            log2cap++;
        }
        if (ok && cap <= (uint64_t)SIZE_MAX / sizeof(int64_t)) {
            t->keys = (int64_t *)BLADE_MEMBER_MALLOC((size_t)cap * sizeof(int64_t));
            t->used = (unsigned char *)BLADE_MEMBER_CALLOC((size_t)cap, 1);
            if (t->keys && t->used) {
                t->cap_mask = cap - 1;
                t->shift = 64 - log2cap;
                for (i = 0; i < t->m; i++) {
                    int64_t k = cells[i];
                    uint64_t h = ((uint64_t)k * BLADE_MEMBER_GOLDEN) >> t->shift;
                    while (t->used[h] && t->keys[h] != k) h = (h + 1) & t->cap_mask;
                    if (!t->used[h]) {
                        t->used[h] = 1;
                        t->keys[h] = k;
                    }
                }
                t->kind = BLADE_MEMBER_HASH;
                return;
            }
            t->fallback |= BLADE_MEMBER_FELL_HASH_ALLOC;
        } else {
            t->fallback |= BLADE_MEMBER_FELL_HASH_CAPACITY;
        }
        if (t->keys) BLADE_MEMBER_FREE(t->keys);
        if (t->used) BLADE_MEMBER_FREE(t->used);
        t->keys = NULL;
        t->used = NULL;
    }
    t->kind = BLADE_MEMBER_LINEAR;
}

/* Is x one of the set's keys? Integer `==` against every cell, whatever the
 * representation; BLADE_MEMBER_NONE (never built) answers 0 -- the emitters
 * never probe one. Read-only. */
static inline int blade_member_test_i64(const blade_member_i64 *t, int64_t x) {
    switch (t->kind) {
    case BLADE_MEMBER_BITMAP: {
        uint64_t d = (uint64_t)x - (uint64_t)t->lo;
        if (d > t->span_m1) return 0;
        return (int)((t->bits[d >> 6] >> (d & 63)) & 1u);
    }
    case BLADE_MEMBER_HASH: {
        uint64_t h = ((uint64_t)x * BLADE_MEMBER_GOLDEN) >> t->shift;
        for (;;) {
            if (!t->used[h]) return 0;
            if (t->keys[h] == x) return 1;
            h = (h + 1) & t->cap_mask;
        }
    }
    case BLADE_MEMBER_LINEAR: {
        int64_t i;
        for (i = 0; i < t->m; i++)
            if (t->cells[i] == x) return 1;
        return 0;
    }
    default:
        return 0;
    }
}

/* The representation a build chose, and the names the report prints. */
static inline int blade_member_kind_i64(const blade_member_i64 *t) { return t->kind; }

static inline const char *blade_member_rep_name(int kind) {
    switch (kind) {
    case BLADE_MEMBER_LINEAR: return "linear";
    case BLADE_MEMBER_BITMAP: return "bitmap";
    case BLADE_MEMBER_HASH: return "hash";
    default: return "none";
    }
}

/* The fallback bits as text ("" when the preferred representation was built). */
static inline const char *blade_member_fallback_text(int fallback) {
    switch (fallback) {
    case 0: return "";
    case BLADE_MEMBER_FELL_BITMAP_ALLOC: return "the bitmap's words could not be allocated";
    case BLADE_MEMBER_FELL_HASH_ALLOC: return "the hash table could not be allocated";
    case BLADE_MEMBER_FELL_HASH_CAPACITY: return "the hash capacity is not addressable";
    case BLADE_MEMBER_FELL_BITMAP_ALLOC | BLADE_MEMBER_FELL_HASH_ALLOC: return "the bitmap's words, then the hash table, could not be allocated";
    case BLADE_MEMBER_FELL_BITMAP_ALLOC | BLADE_MEMBER_FELL_HASH_CAPACITY: return "the bitmap's words could not be allocated and the hash capacity is not addressable";
    default: return "allocation failures";
    }
}

/* BLADE_MEMBER_VERBOSE: set, non-empty and not 0 / off / false. Read at each
 * build (once per host), never cached. */
static inline int blade_member_verbose(void) {
    const char *v = getenv("BLADE_MEMBER_VERBOSE");
    if (!v || !*v) return 0;
    if (strcmp(v, "0") == 0 || strcmp(v, "off") == 0 || strcmp(v, "false") == 0) return 0;
    return 1;
}

#if defined(__cplusplus) && defined(BLADE_RUN_RECORD_HPP)
/* The C++ lane's run record (blade_run_record.hpp): every build's report,
 * written under "member_hoist" when the program exits. A fixed table (no
 * allocation); builds past it are counted. */
namespace blade_member_rr {
    struct Note { const char *host; const char *set; long long m; int preferred; int kind; int fallback; };
    inline Note notes[64];
    inline int n_notes = 0;
    inline int dropped = 0;
    inline void write_member(std::FILE *f) {
        std::fputs(",\"member_hoist\":[", f);
        for (int i = 0; i < n_notes; ++i) {
            const Note &n = notes[i];
            if (i) std::fputc(',', f);
            std::fputs("{\"host\":", f); blade_rr::json_string(f, n.host);
            std::fputs(",\"set\":", f); blade_rr::json_string(f, n.set);
            std::fprintf(f, ",\"m\":%lld,\"preferred\":\"%s\",\"actual\":\"%s\",\"fallback\":", n.m,
                         blade_member_rep_name(n.preferred), blade_member_rep_name(n.kind));
            blade_rr::json_string(f, blade_member_fallback_text(n.fallback));
            std::fputc('}', f);
        }
        std::fprintf(f, "],\"member_hoist_dropped\":%d", dropped);
    }
    inline void add(const blade_member_i64 *t, const char *host, const char *set) {
        blade_rr::extra_members = write_member;
        if (n_notes < 64) notes[n_notes++] = Note{ host, set, (long long)t->m, t->preferred, t->kind, t->fallback };
        else ++dropped;
    }
}
#endif

/* The build's REPORT, made by its caller right after the build (never by the
 * probe): the actual representation beside the preferred one and why they
 * differ -- a `[member] ...` line on stderr under BLADE_MEMBER_VERBOSE, and
 * (C++ lane) an entry of the run record. */
static inline void blade_member_note_i64(const blade_member_i64 *t, const char *host, const char *set) {
    if (blade_member_verbose()) {
        fprintf(stderr, "[member] %s: set %s, M %lld, preferred %s, actual %s%s%s\n",
                host, set, (long long)t->m, blade_member_rep_name(t->preferred), blade_member_rep_name(t->kind),
                t->fallback ? " -- " : "", blade_member_fallback_text(t->fallback));
    }
#if defined(__cplusplus) && defined(BLADE_RUN_RECORD_HPP)
    blade_member_rr::add(t, host, set);
#endif
}

/* ---------------------------------------------------------------------------
 * THE EQUIJOIN CSR (round 3 W3-2, item T2; Blade.Optimize.planEquiJoin, plan
 * rule `hash-join`): the KEY -> BUCKET map of the same core, plus the bucket
 * rows. Built ONCE over the right keys rk[0, m) of an inline equijoin
 * compound; the caller (the C++ lane's genEquiJoinBinding) then probes the
 * left keys i ascending and walks every bucket row ascending, which is the
 * compound's lex (left-major / right-minor) order.
 *
 * THE OPERATION. Every distinct right key k gets a bucket b; off[b] ..
 * off[b + 1] are the positions in perm of the right rows j with rk[j] == k,
 * ASCENDING in j: the rows are filled FORWARD (stable), never prepended, and
 * duplicates are all kept (bags survive). `blade_join_lookup_i64(t, x)` is
 * the bucket of key x, or -1 when no right row holds x -- exactly integer
 * `==` against every right cell: negative keys, 0, INT64_MIN and INT64_MAX
 * are ordinary keys, and a key outside any declared domain simply has no
 * bucket (there is no domain check, as `==` has none). A DIRECT bucket may be
 * empty (off[b] == off[b + 1]): the caller reads the range, never assumes it
 * non-empty.
 *
 * TWO FORMS, one answer:
 *   DIRECT -- a counting sort over the keys' measured span [lo, hi]: bucket
 *             b = x - lo (unsigned difference, exact for every hi >= lo),
 *             nb = hi - lo + 1 buckets. Taken when `positional` (the right
 *             key's type is a positional Idx<K>: span <= K by type, and the
 *             span is still MEASURED, never assumed) and the span is below
 *             2^20 (8 MiB of offsets), or when the span is below
 *             max(4 m, 4096) and 2^26 (offsets O(m)).
 *   HASH   -- open addressing over a power-of-two capacity >= 2m (load <=
 *             1/2), Fibonacci hashing of the key's bits, linear probing; an
 *             OWN occupancy byte array (no key is a sentinel); buckets
 *             numbered in first-occurrence order (the numbering never
 *             reaches the output: the probe order is the left keys').
 * The chooser reads (m, lo, hi, positional) only -- never timing or the
 * machine. m == 0 is DIRECT with nb = 0 (every lookup -1).
 *
 * FAILURE. An allocation that cannot be made returns -1 with every partial
 * allocation freed and the struct reset; the caller raises std::bad_alloc
 * (the as-written N x M pools would have needed far more). No other failure
 * exists: no key value, duplicate or span overflows the arithmetic.
 *
 * LIFETIME. Caller-owned struct; `blade_join_free_i64` releases the build
 * and resets it. The build reads rk only during the call (nothing borrowed).
 * The interpreter does NOT use this core (the independent oracle).
 */
#define BLADE_JOIN_NONE   0
#define BLADE_JOIN_DIRECT 1
#define BLADE_JOIN_HASH   2

#define BLADE_JOIN_SPAN_PER_ROW 4
#define BLADE_JOIN_SPAN_FLOOR   4096
#define BLADE_JOIN_SPAN_CAP     ((uint64_t)1 << 26)
#define BLADE_JOIN_POSITIONAL_CAP ((uint64_t)1 << 20)

typedef struct blade_join_csr_i64 {
    int kind;              /* BLADE_JOIN_DIRECT / _HASH (NONE = not built) */
    int shift;             /* HASH: 64 - log2(capacity) */
    int64_t m;             /* right rows */
    int64_t nb;            /* buckets */
    int64_t *off;          /* nb + 1 offsets into perm (owned) */
    int64_t *perm;         /* m right rows, bucket-major, ascending within a bucket (owned) */
    int64_t lo;            /* DIRECT: the smallest key */
    uint64_t span_m1;      /* DIRECT: hi - lo, unsigned */
    int64_t *keys;         /* HASH: capacity slots (owned) */
    int64_t *bucket;       /* HASH: capacity bucket ids (owned) */
    unsigned char *used;   /* HASH: capacity occupancy bytes (owned) */
    uint64_t cap_mask;     /* HASH: capacity - 1 */
} blade_join_csr_i64;

/* The form the build takes for (m, lo, hi, positional); lo / hi read only
 * when m > 0. */
static inline int blade_join_choose_i64(int64_t m, int64_t lo, int64_t hi, int positional) {
    uint64_t span_m1, limit;
    if (m <= 0) return BLADE_JOIN_DIRECT;
    span_m1 = (uint64_t)hi - (uint64_t)lo;
    if (positional && span_m1 < BLADE_JOIN_POSITIONAL_CAP) return BLADE_JOIN_DIRECT;
    if (span_m1 >= BLADE_JOIN_SPAN_CAP) return BLADE_JOIN_HASH;
    /* m <= SIZE_MAX / 16 (the build checked it), so 4 m does not wrap */
    limit = (uint64_t)BLADE_JOIN_SPAN_FLOOR;
    if ((uint64_t)m * BLADE_JOIN_SPAN_PER_ROW > limit) limit = (uint64_t)m * BLADE_JOIN_SPAN_PER_ROW;
    return span_m1 < limit ? BLADE_JOIN_DIRECT : BLADE_JOIN_HASH;
}

/* Two allocations at most: the CSR block (off, then perm, interior) and the
 * HASH form's table block (keys, bucket ids, occupancy bytes, interior). */
static inline void blade_join_free_i64(blade_join_csr_i64 *t) {
    if (t->off) BLADE_MEMBER_FREE(t->off);
    if (t->keys) BLADE_MEMBER_FREE(t->keys);
    memset(t, 0, sizeof *t);
}

/* The bucket of key x: an index in [0, nb), or -1 when no right row holds x.
 * Read-only. */
static inline int64_t blade_join_lookup_i64(const blade_join_csr_i64 *t, int64_t x) {
    if (t->kind == BLADE_JOIN_DIRECT) {
        uint64_t d;
        if (t->nb == 0) return -1;
        d = (uint64_t)x - (uint64_t)t->lo;
        return d > t->span_m1 ? -1 : (int64_t)d;
    }
    if (t->kind == BLADE_JOIN_HASH) {
        uint64_t h = ((uint64_t)x * BLADE_MEMBER_GOLDEN) >> t->shift;
        for (;;) {
            if (!t->used[h]) return -1;
            if (t->keys[h] == x) return t->bucket[h];
            h = (h + 1) & t->cap_mask;
        }
    }
    return -1;
}

/* Build over rk[0, m). Returns 0, or -1 (allocation failure, struct reset).
 *
 * THE CSR BLOCK is ONE allocation of nb + 2 + m cells: off[0, nb + 2) then
 * perm[0, m). The classic one-array build: the count of bucket b goes to
 * off[b + 2]; the prefix makes off[b + 1] the START of bucket b; the forward
 * fill `perm[off[b + 1]++] = j` (j ascending) then leaves off[b + 1] at the
 * END of bucket b -- the start of b + 1 -- with off[0] = 0, so off[b] ..
 * off[b + 1] is bucket b, its rows ascending. No cursor array, no row ->
 * bucket array (the HASH fill looks each row's bucket up again): every cell
 * the build writes is one it keeps, which at the pilot sizes is what first
 * touch costs. */
static inline int blade_join_build_i64(blade_join_csr_i64 *t, const int64_t *rk, int64_t m, int positional) {
    int64_t j, lo, hi, b;
    memset(t, 0, sizeof *t);
    t->m = m < 0 ? 0 : m;
    if (t->m == 0) {
        t->kind = BLADE_JOIN_DIRECT;
        t->off = (int64_t *)BLADE_MEMBER_CALLOC(2, sizeof(int64_t));
        if (!t->off) { blade_join_free_i64(t); return -1; }
        return 0;
    }
    if ((uint64_t)t->m > (uint64_t)SIZE_MAX / sizeof(int64_t) / 4) return -1;
    lo = rk[0];
    hi = rk[0];
    for (j = 1; j < t->m; j++) {
        if (rk[j] < lo) lo = rk[j];
        if (rk[j] > hi) hi = rk[j];
    }
    t->kind = blade_join_choose_i64(t->m, lo, hi, positional);
    if (t->kind == BLADE_JOIN_DIRECT) {
        t->lo = lo;
        t->span_m1 = (uint64_t)hi - (uint64_t)lo;
        t->nb = (int64_t)t->span_m1 + 1;
    } else {
        uint64_t cap = 16;
        int log2cap = 4;
        size_t bytes;
        while (cap < (uint64_t)t->m * 2) { cap <<= 1; log2cap++; }
        if (cap > (uint64_t)SIZE_MAX / (2 * sizeof(int64_t) + 1)) goto fail;
        bytes = (size_t)cap * (2 * sizeof(int64_t) + 1);
        t->keys = (int64_t *)BLADE_MEMBER_MALLOC(bytes);
        if (!t->keys) goto fail;
        t->bucket = t->keys + cap;
        t->used = (unsigned char *)(t->bucket + cap);
        memset(t->used, 0, (size_t)cap);
        t->cap_mask = cap - 1;
        t->shift = 64 - log2cap;
        /* first-occurrence bucket numbering */
        for (j = 0; j < t->m; j++) {
            int64_t k = rk[j];
            uint64_t h = (((uint64_t)k * BLADE_MEMBER_GOLDEN) >> t->shift);
            while (t->used[h] && t->keys[h] != k) h = (h + 1) & t->cap_mask;
            if (!t->used[h]) { t->used[h] = 1; t->keys[h] = k; t->bucket[h] = t->nb++; }
        }
    }
    t->off = (int64_t *)BLADE_MEMBER_MALLOC(((size_t)t->nb + 2 + (size_t)t->m) * sizeof(int64_t));
    if (!t->off) goto fail;
    t->perm = t->off + t->nb + 2;
    memset(t->off, 0, ((size_t)t->nb + 2) * sizeof(int64_t));
    if (t->kind == BLADE_JOIN_DIRECT) {
        for (j = 0; j < t->m; j++) t->off[(int64_t)((uint64_t)rk[j] - (uint64_t)lo) + 2]++;
    } else {
        for (j = 0; j < t->m; j++) t->off[blade_join_lookup_i64(t, rk[j]) + 2]++;
    }
    for (b = 2; b <= t->nb; b++) t->off[b] += t->off[b - 1];
    /* FORWARD fill (stable: every bucket lists its rows j ascending) */
    if (t->kind == BLADE_JOIN_DIRECT) {
        for (j = 0; j < t->m; j++) t->perm[t->off[(int64_t)((uint64_t)rk[j] - (uint64_t)lo) + 1]++] = j;
    } else {
        for (j = 0; j < t->m; j++) t->perm[t->off[blade_join_lookup_i64(t, rk[j]) + 1]++] = j;
    }
    return 0;
fail:
    blade_join_free_i64(t);
    return -1;
}

static inline const char *blade_join_form_name(int kind) {
    switch (kind) {
    case BLADE_JOIN_DIRECT: return "counting sort";
    case BLADE_JOIN_HASH: return "hash";
    default: return "none";
    }
}

/* The join's REPORT, made by its caller after the counting pass: the form
 * the build took and the relation's size -- a `[join] ...` line on stderr.
 * Printed UNCONDITIONALLY: the caller's call exists only when the program was
 * compiled with the instrumentation gate BLADE_JOIN_REPORT=1 (no run-time
 * switch, so no getenv inside the timed region; BLADE_MEMBER_VERBOSE, the
 * member builds' run-time switch, does not apply). */
static inline void blade_join_note_i64(const blade_join_csr_i64 *t, const char *rel, const char *right, int64_t n, int64_t card) {
    fprintf(stderr, "[join] %s: CSR over %s, M %lld, form %s, buckets %lld; N %lld, |J| %lld\n",
            rel, right, (long long)t->m, blade_join_form_name(t->kind), (long long)t->nb, (long long)n, (long long)card);
}

#endif /* BLADE_MEMBER_H */
