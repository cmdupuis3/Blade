#pragma once

#include <string>
#include <vector>
#include <array>
#include <unordered_map>
#include <functional>
#include <utility>
#include <stdexcept>

/* A read of a tuple outside a hashed index type's key set. Runtime headers
 * may not call the runtime panic (shadow-frame elision, tests/
 * Test_Diagnostics.fs), so this throws; the "[BL8006] " prefix tells the
 * generated main's exception handler (panic_exception) which code to report. */
#define BLADE_OUT_OF_KEY_SET "[BL8006] index out of bounds: a key outside the sparse key set"

/* array_hasher: a std::hash-style functor for std::array<size_t, RANK>.
 *
 * The standard library provides no hash for std::array, so this supplies the
 * canonical boost-style hash-combine recipe -- the SAME recipe as tuple_hasher
 * in nested_array_utilities.hpp, but over a fixed-size array of size_t rather
 * than a std::tuple. (Kept local rather than shared because index_types.h is
 * standalone and IDX is an array, not a tuple; consolidating the two hashers
 * is a later cleanup, not load-bearing here.)
 *
 * This is the Hash argument for an unordered_map keyed by an index tuple
 * (IDX), giving the tuple -> rank reverse lookup that arithmetic-rankable
 * index types never need but tabulated ones (CompoundIdx) require.
 */
template<size_t RANK>
struct array_hasher {
	std::size_t operator()(const std::array<size_t, RANK>& a) const noexcept {
		std::size_t seed = 0;
		for (size_t i = 0; i < RANK; i++) {
			seed ^= std::hash<size_t>{}(a[i]) + 0x9e3779b9 + (seed << 6) + (seed >> 2);
		}
		return seed;
	}
};

/* Abstract base class for all index types.
 */
template<size_t NDIM>
struct abstract_idx_t {
	using IDX = std::array<size_t, NDIM>;
	std::string name;
	size_t size;
	size_t cardinality;

	virtual void hash() = 0;
	virtual IDX unhash(size_t index) = 0;
	virtual ~abstract_idx_t() = default;

	abstract_idx_t(std::string name_in, size_t size_in) {
		this->name = name_in;
		this->size = size_in;
	}

};

/* Abstract simple index types.
 *
 * Any index type that would inherit from this isn't strictly needed,
 * because the iteration patterns can be inlined at code-generation.
 * This is here mainly for comparison with the code generation and 
 * index types where hashing is needed.
 */
template<size_t ARITY>
struct abstract_simple_idx_t : abstract_idx_t<ARITY> {
	using IDX = typename abstract_idx_t<ARITY>::IDX;
	std::vector<IDX> table;

	abstract_simple_idx_t(std::string name_in, size_t size_in)
		: abstract_idx_t<ARITY>(name_in, size_in) {
		// Deferred build: the derived class calls hash() from its OWN
		// constructor, after its members are established. Calling a pure-virtual
		// hash() from HERE (during base-subobject construction) dispatches to the
		// base vtable, not the derived override -- "pure virtual method called".
		// This is the same reason abstract_sorted_hashed_idx_t omits the call.
	}

	IDX unhash(size_t index) {
		return this->table[index];
	}
};

/* Abstract hashed (dynamic, unordered) index types.
 *
 * For tabulated types whose valid set is built INCREMENTALLY at runtime --
 * dynamic SparseIdx (formalism 4.6.2: "empty hash tables, expand on indexing")
 * and SQL-style group-by buckets. Unlike abstract_sorted_hashed_idx_t there is
 * NO sort invariant: tuples are ranked in INSERTION order, so contiguous prefix
 * slicing is not available -- that is the trade for grow-on-insert.
 *
 *   - rank_to_tuple : insertion-order dense vector. O(1) unhash. The reverse
 *       direction is a vector (not a hash) because rank is dense 0..card-1 --
 *       the previous size_t->IDX map keyed the WRONG direction.
 *   - tuple_to_rank : IDX -> rank hash, the direction that genuinely needs
 *       hashing (membership test + rank lookup on insert/access).
 *
 * Starts EMPTY (no one-shot build in the constructor -- nothing to enumerate);
 * insert() adds a tuple on first occurrence and returns its rank. hash() stays
 * pure-virtual: a concrete dynamic type supplies it (often a no-op or an
 * initial seeding), keeping this an abstract base like its siblings.
 */
template<size_t RANK>
struct abstract_hashed_idx_t : abstract_idx_t<RANK> {
	using IDX = typename abstract_idx_t<RANK>::IDX;
	std::vector<IDX> rank_to_tuple;
	std::unordered_map<IDX, size_t, array_hasher<RANK>> tuple_to_rank;

	abstract_hashed_idx_t(std::string name_in, size_t size_in)
		: abstract_idx_t<RANK>(name_in, size_in) {
		// Dynamic: starts empty, populated incrementally via insert().
	}

	// rank -> tuple (insertion order). O(1).
	IDX unhash(size_t index) {
		return this->rank_to_tuple[index];
	}

	// tuple -> rank. O(1) amortized. A tuple never inserted is an
	// out-of-domain read: BL8006, the interpreter's sparseLinearize twin.
	size_t linearize(const IDX& tuple) const {
		auto it = this->tuple_to_rank.find(tuple);
		if (it == this->tuple_to_rank.end())
			throw std::out_of_range(BLADE_OUT_OF_KEY_SET);
		return it->second;
	}

	// Grow-on-insert: returns the rank of `tuple`, assigning the next rank (and
	// appending to rank_to_tuple) on first occurrence. Idempotent thereafter.
	size_t insert(const IDX& tuple) {
		auto it = this->tuple_to_rank.find(tuple);
		if (it != this->tuple_to_rank.end()) return it->second;
		size_t r = this->rank_to_tuple.size();
		this->tuple_to_rank[tuple] = r;
		this->rank_to_tuple.push_back(tuple);
		this->cardinality = this->rank_to_tuple.size();
		return r;
	}
};

/* Abstract sorted-and-hashed index types.
 *
 * For tabulated index types whose valid-tuple set has NO closed-form rank
 * (CompoundIdx, static SparseIdx) and so must be MATERIALIZED. This is the
 * "both" case that neither existing base covers:
 *
 *   abstract_simple_idx_t -- a sorted dense vector, but assumes the FORWARD
 *       direction (tuple -> rank) is a computed formula; sym/antisym never
 *       store a reverse map because their rank is arithmetic.
 *   abstract_hashed_idx_t -- a map with NO order guarantee; the right base for
 *       dynamic / grow-on-insert types (dynamic SparseIdx, group-by buckets),
 *       which give up contiguous slicing in exchange for runtime growth.
 *
 * This base is materialized-AND-sorted AND reverse-hashed:
 *   - rank_to_tuple : sorted-by-construction dense vector. O(1) unhash, and
 *       the contiguous artifact extracted for cudaMemcpy to a device. Its
 *       canonical (lex) order is what makes a PREFIX slice a contiguous range
 *       query rather than a scan.
 *   - tuple_to_rank : reverse hash (IDX -> rank) for linearize / scatter.
 *       Needed precisely because there is no arithmetic rank to compute.
 *
 * Naming bridge: within this class family `unhash(rank) -> tuple` is what
 * linearized_storage.hpp calls `unlinearize`; the forward `linearize(tuple)
 * -> rank` is the new direction this base adds. `hash()` (inherited, pure
 * virtual) means BUILD-THE-TABLE, not "hash one tuple".
 *
 * DELIBERATE divergence from the two bases above: this base does NOT call
 * hash() from its constructor. A tabulated type is parameterized by build-time
 * data the base cannot see (CompoundIdx's per-dimension extents + mask, a
 * SparseIdx's entry list), and that data is not yet initialized while the base
 * constructor runs. So the derived class initializes its own inputs and then
 * calls hash() from its OWN constructor body. That also removes the CRTP
 * call-into-derived-during-construction hazard entirely, so this base does not
 * take a DERIVED template parameter.
 *
 * SCOPE: this base owns ONLY the rank<->tuple bijection. The per-prefix
 * offset / subtree-size table for the NESTED skeleton is a separate artifact --
 * produced from the same enumeration pass in the derived hash(), but consumed
 * by build_skeleton's tabulated variant in nested_array_utilities.hpp, not
 * stored here. Keeping placement out of this base preserves the bijection /
 * skeleton separation.
 *
 * NOTE: abstract_idx_t carries a single scalar `size`, which is meaningful for
 * uniform-extent types but not for a CompoundIdx over heterogeneous extents
 * (e.g. 180 x 360). Derived tabulated classes hold their own extent data; the
 * inherited `size` is left to the derived class's discretion.
 */
template<size_t RANK>
struct abstract_sorted_hashed_idx_t : abstract_idx_t<RANK> {
	using IDX = typename abstract_idx_t<RANK>::IDX;
	std::vector<IDX> rank_to_tuple;
	std::unordered_map<IDX, size_t, array_hasher<RANK>> tuple_to_rank;

	abstract_sorted_hashed_idx_t(std::string name_in, size_t size_in)
		: abstract_idx_t<RANK>(name_in, size_in) {
		// Intentionally no hash() here -- the derived class calls it after
		// initializing its own build inputs (see class comment).
	}

	// rank -> tuple. O(1). The forward storage direction (== unlinearize).
	IDX unhash(size_t index) {
		return this->rank_to_tuple[index];
	}

	// tuple -> rank. O(1) amortized via the reverse hash. The direction with
	// no closed form for a tabulated type -- hence the map rather than a formula.
	// A key outside the set is an out-of-domain read: BL8006, matching the
	// interpreter (it used to escape as std::out_of_range, BL8005
	// "unordered_map::at").
	size_t linearize(const IDX& tuple) const {
		auto it = this->tuple_to_rank.find(tuple);
		if (it == this->tuple_to_rank.end())
			throw std::out_of_range(BLADE_OUT_OF_KEY_SET);
		return it->second;
	}
};

/* Compound index type: a masked product space (formalism section 4.5).
 *
 * Models the "rectangular-minus-holes" regime -- a k-dimensional array whose
 * valid coordinates are a subset of the full product space, selected by a
 * boolean mask. Storing only the valid combinations (contiguously, in canonical
 * lex order) avoids the NaN/sentinel cells a dense rectangular layout carries
 * for the holes, while keeping prefix slices contiguous (cache-friendly).
 *
 * Validity has no closed-form rank, so the bijection is materialized: hash()
 * scans the product space once in row-major lex order, appending each valid
 * tuple to rank_to_tuple (sorted by construction) and recording its rank in
 * tuple_to_rank. cardinality = popcount(mask).
 *
 * Derives from abstract_sorted_hashed_idx_t (not abstract_hashed_idx_t): a
 * CompoundIdx is sorted (for contiguous prefix slicing and as the flat artifact
 * shipped to a device) AND reverse-hashed (no arithmetic rank exists). The build
 * is deferred -- extents + mask are set by this constructor before hash() runs.
 *
 * Mask layout: a flat row-major boolean buffer over prod(extents); mask_offset
 * maps a full tuple to its bit. (Flat rather than nested promote<bool,RANK>:
 * a validity grid is naturally flat and the scan is a simple lex recursion.)
 *
 * prefix_range gives the half-open rank range of valid tuples sharing a leading
 * coordinate prefix -- a contiguous range because the table is lex-sorted (the
 * cache-friendly wildcard-currying slice of section 4.5), found by bisection.
 *
 * NOTE: the per-prefix offset / subtree-size table for the NESTED skeleton is a
 * separate artifact (extensions section 2.3.3), to be emitted from this same
 * enumeration when the tabulated build_skeleton variant is wired; it is not
 * produced here, keeping this type focused on the rank<->tuple bijection.
 */
template<size_t RANK>
struct compound_index_t : abstract_sorted_hashed_idx_t<RANK> {
	using base = abstract_sorted_hashed_idx_t<RANK>;
	using IDX = typename base::IDX;

	IDX extents;
	std::vector<bool> mask;

	/* A CARDINALITY-ONLY index (the hash equijoin, round 3 W3-2,
	 * Blade.Optimize.planEquiJoin): the compiler's demand census proved no
	 * consumer reads anything but the compact buffer and the cardinality, so
	 * no tuple table, reverse hash or mask is built. Extents and cardinality
	 * are those of every other form; `unhash`, `linearize`, `present`,
	 * `prefix_range` and `hash` refuse the absent table (BL9002, an internal
	 * error) -- never a read through it. */
	bool tuples_elided_ = false;
	struct cardinality_only_t {};
	static constexpr cardinality_only_t cardinality_only{};
	[[noreturn]] static void tuples_absent() {
		throw std::logic_error("[BL9002] internal codegen invariant violated: a lookup through a cardinality-only compound index (the hash equijoin's demand census missed a consumer)");
	}

	compound_index_t(std::string name_in, IDX extents_in, std::vector<bool> mask_in)
		: base(name_in, product(extents_in)), extents(extents_in), mask(std::move(mask_in)) {
		this->hash();
	}

	compound_index_t(std::string name_in, IDX extents_in, size_t cardinality_in, cardinality_only_t)
		: base(name_in, product(extents_in)), extents(extents_in), mask() {
		this->cardinality = cardinality_in;
		tuples_elided_ = true;
	}

	void hash() {
		if (tuples_elided_) tuples_absent();
		this->rank_to_tuple.clear();
		this->tuple_to_rank.clear();
		IDX idx{};
		enumerate(0, idx);
		this->cardinality = this->rank_to_tuple.size();
	}

	IDX unhash(size_t index) {
		if (tuples_elided_) tuples_absent();
		return this->rank_to_tuple[index];
	}

	size_t linearize(const IDX& tuple) const {
		if (tuples_elided_) tuples_absent();
		return base::linearize(tuple);
	}

	// Presence (allocation) query for the <|:> allocated-fallback read: does
	// this compound hold a cell for the full tuple? The mask bit IS the
	// allocation record of the compact buffer (absent cells have no storage).
	bool present(const IDX& t) const {
		if (tuples_elided_) tuples_absent();
		return mask[mask_offset(t)];
	}

	std::pair<size_t, size_t> prefix_range(const IDX& prefix, size_t plen) const {
		if (tuples_elided_) tuples_absent();
		size_t n = this->rank_to_tuple.size();
		size_t lo, hi;
		{ size_t a = 0, b = n;
		  while (a < b) { size_t m = a + (b - a) / 2;
		    if (prefix_cmp(this->rank_to_tuple[m], prefix, plen) < 0) a = m + 1; else b = m; }
		  lo = a; }
		{ size_t a = 0, b = n;
		  while (a < b) { size_t m = a + (b - a) / 2;
		    if (prefix_cmp(this->rank_to_tuple[m], prefix, plen) <= 0) a = m + 1; else b = m; }
		  hi = a; }
		return { lo, hi };
	}

private:
	static size_t product(const IDX& e) {
		size_t p = 1;
		for (size_t d = 0; d < RANK; d++) p *= e[d];
		return p;
	}
	size_t mask_offset(const IDX& t) const {
		size_t off = 0;
		for (size_t d = 0; d < RANK; d++) off = off * extents[d] + t[d];
		return off;
	}
	void enumerate(size_t depth, IDX& idx) {
		if (depth == RANK) {
			if (mask[mask_offset(idx)]) {
				this->tuple_to_rank[idx] = this->rank_to_tuple.size();
				this->rank_to_tuple.push_back(idx);
			}
			return;
		}
		for (size_t v = 0; v < extents[depth]; v++) {
			idx[depth] = v;
			enumerate(depth + 1, idx);
		}
	}
	int prefix_cmp(const IDX& t, const IDX& target, size_t plen) const {
		for (size_t d = 0; d < plen; d++) {
			if (t[d] < target[d]) return -1;
			if (t[d] > target[d]) return 1;
		}
		return 0;
	}
};

/* RANK-1 COMPOUND INDEX: the compaction representation (bench gap G9).
 *
 * A rank-1 mask selects cells of ONE dense axis [0, n), so the valid set is a
 * SORTED list of positions and nothing more: the rank of a present cell is its
 * position in that list, and the list IS the mask (a cell is present iff its
 * position is in it). The generic template above materializes a tuple->rank
 * unordered_map -- one hash node per present cell, ~40 B and ~78 ns each
 * (bench/notes/gap-register.md G9: 85% of maskagg's time) -- which a rank-1
 * index never needs. This specialization keeps:
 *
 *   - rank_to_tuple : the kept positions, ascending (== lex order), owned raw
 *       buffer behind a vector-shaped view (operator[], size(), data()). It
 *       doubles as the mask: present(t) and linearize(t) are a binary search.
 *   - extents       : the source axis's extent, as the generic type exposes it.
 *
 * Construction is a COUNT pass over the bool array (vectorizes) and ONE
 * branchless fill (`pos[q] = i; q += keep[i]`, one slot of slack): no hash,
 * no std::vector<bool> bit copy. The legacy (name, extents, vector<bool>)
 * constructor is kept for the sites that still hand over a bit vector
 * (cuda range<CompoundIdx>, alloc_layout_tests) and builds the same table.
 * A key outside the kept set is BL8006, exactly as the generic linearize.
 * Under compaction fusion the emitter counts in the mask pass and fills the
 * table in its value-gather pass, and hands it over to the ADOPTING
 * (name, n, m, pos) constructor: the same table, built without a pass here.
 *
 * The data gather is NOT here: codegen reads rank_to_tuple[q][0] and copies
 * cell q from the dense pool (EmitCpp.compactScatter, Rank1 arm), so the
 * compact buffer is written exactly once, in kept order, by position.
 */
template<>
struct compound_index_t<1> : abstract_idx_t<1> {
	using IDX = std::array<size_t, 1>;

	/* A lookup through an ELIDED table (compaction fusion v2, T4b: the
	 * compiler's demand census proved no consumer reads the positions, and
	 * built the index cardinality-only). Unreachable by that certificate; if
	 * it is ever reached it is an internal error, reported as such -- never a
	 * read through a null pointer. */
	[[noreturn]] static void positions_absent() {
		throw std::logic_error("[BL9002] internal codegen invariant violated: a lookup through a compound index whose kept positions were elided (the positions demand census missed a consumer)");
	}

	/* The kept-position table: a vector-shaped view over an owned raw buffer
	 * (new IDX[] leaves the cells indeterminate instead of zero-filling them,
	 * which std::vector::resize would -- one whole write pass of the table).
	 * Every constructor but the positions-elided one leaves `p` non-null (the
	 * counting forms allocate m + 1 cells, the adopting form refuses a null
	 * buffer), so a null `p` means exactly "elided", and every accessor
	 * refuses it. */
	struct positions_t {
		IDX* p = nullptr;
		size_t n = 0;
		positions_t() = default;
		positions_t(const positions_t&) = delete;
		positions_t& operator=(const positions_t&) = delete;
		~positions_t() { delete[] p; }
		void reset(size_t cap) { delete[] p; p = cap ? new IDX[cap] : nullptr; n = 0; }
		// Take ownership of a caller-built `new IDX[]` buffer holding `count`
		// table cells (plus whatever slack the caller allocated).
		void adopt(IDX* buf, size_t count) { delete[] p; p = buf; n = count; }
		IDX& operator[](size_t i) { if (!p) positions_absent(); return p[i]; }
		const IDX& operator[](size_t i) const { if (!p) positions_absent(); return p[i]; }
		size_t size() const { return n; }
		IDX* data() { if (!p) positions_absent(); return p; }
		const IDX* data() const { if (!p) positions_absent(); return p; }
		const IDX* begin() const { if (!p) positions_absent(); return p; }
		const IDX* end() const { if (!p) positions_absent(); return p + n; }
	};

	IDX extents;
	positions_t rank_to_tuple;
	/* True only for the positions-elided form: cardinality and extent are
	 * intact (rank_to_tuple.n == cardinality == m), the table is absent. */
	bool positions_elided_ = false;

	/* The tag selecting the positions-elided constructor. */
	struct positions_elided_t {};
	static constexpr positions_elided_t positions_elided{};

	/* The compaction constructor: `keep` is the flat bool array over [0, n). */
	compound_index_t(std::string name_in, size_t n, const bool* keep)
		: abstract_idx_t<1>(std::move(name_in), n), extents{ n } {
		size_t m = 0;
		for (size_t i = 0; i < n; i++) m += (size_t)(!!keep[i]);
		// The unconditional store below lands one past the last kept slot
		// after the last kept cell, hence the slack cell (never read).
		rank_to_tuple.reset(m + 1);
		size_t q = 0;
		for (size_t i = 0; i < n; i++) { rank_to_tuple.p[q][0] = i; q += (size_t)(!!keep[i]); }
		rank_to_tuple.n = m;
		this->cardinality = m;
	}

	/* The ADOPTING constructor (compaction fusion, Blade.Optimize
	 * planCompactionFusion): the caller already counted m (in its mask pass)
	 * and filled `pos` -- a `new IDX[m + 1]` buffer whose first m cells are
	 * the ascending kept positions, the last one the branchless fill's slack
	 * -- in the same pass that gathered the values. No count, no fill here;
	 * the table is the one the compaction constructor would have built
	 * (dense extent n, cardinality m, rank_to_tuple.n = m), and it is owned
	 * and freed exactly as that one is. */
	compound_index_t(std::string name_in, size_t n, size_t m, IDX* pos)
		: abstract_idx_t<1>(std::move(name_in), n), extents{ n } {
		// A null table is the elided form's, which has its own constructor:
		// refusing it here keeps "p == nullptr" meaning exactly "elided".
		if (!pos) throw std::logic_error("[BL9002] internal codegen invariant violated: the adopting compound-index constructor was handed no position table");
		rank_to_tuple.adopt(pos, m);
		this->cardinality = m;
	}

	/* The POSITIONS-ELIDED constructor (compaction fusion v2, T4b): the
	 * caller counted m and gathered the values; no consumer reads the kept
	 * positions (the compiler's demand census certified it), so no table is
	 * built. Dense extent n and cardinality m are those of every other form
	 * (rank_to_tuple.n == m: the count is kept, never a 0 meaning "absent");
	 * `unhash`, `linearize`, `present`, `prefix_range` and every table
	 * accessor refuse the absent table (positions_absent, BL9002). */
	compound_index_t(std::string name_in, size_t n, size_t m, positions_elided_t)
		: abstract_idx_t<1>(std::move(name_in), n), extents{ n } {
		rank_to_tuple.p = nullptr;
		rank_to_tuple.n = m;
		this->cardinality = m;
		positions_elided_ = true;
	}

	/* The legacy constructor: the same table from a bit vector. */
	compound_index_t(std::string name_in, IDX extents_in, std::vector<bool> mask_in)
		: abstract_idx_t<1>(std::move(name_in), extents_in[0]), extents(extents_in) {
		const size_t n = extents_in[0];
		size_t m = 0;
		for (size_t i = 0; i < n; i++) m += (size_t)(mask_in[i]);
		rank_to_tuple.reset(m + 1);
		size_t q = 0;
		for (size_t i = 0; i < n; i++) { rank_to_tuple.p[q][0] = i; q += (size_t)(mask_in[i]); }
		rank_to_tuple.n = m;
		this->cardinality = m;
	}

	// The table is built at construction; nothing to (re)build.
	void hash() override {}

	IDX unhash(size_t index) override { if (positions_elided_) positions_absent(); return rank_to_tuple[index]; }

	// Binary search for a position: the first slot >= key (== size when none).
	size_t lower_bound(size_t key) const {
		if (positions_elided_) positions_absent();
		size_t a = 0, b = rank_to_tuple.n;
		while (a < b) {
			size_t mid = a + (b - a) / 2;
			if (rank_to_tuple.p[mid][0] < key) a = mid + 1; else b = mid;
		}
		return a;
	}

	// tuple -> rank. A position that was not kept is an out-of-domain read:
	// BL8006, the generic type's (and the interpreter's) contract.
	size_t linearize(const IDX& tuple) const {
		size_t r = lower_bound(tuple[0]);
		if (r >= rank_to_tuple.n || rank_to_tuple.p[r][0] != tuple[0])
			throw std::out_of_range(BLADE_OUT_OF_KEY_SET);
		return r;
	}

	// Presence query for the <|:> allocated-fallback read.
	bool present(const IDX& t) const {
		size_t r = lower_bound(t[0]);
		return r < rank_to_tuple.n && rank_to_tuple.p[r][0] == t[0];
	}

	// The half-open rank range sharing a leading prefix: the whole table for
	// an empty prefix, the single kept slot (or an empty range) for a full one.
	std::pair<size_t, size_t> prefix_range(const IDX& prefix, size_t plen) const {
		if (positions_elided_) positions_absent();
		if (plen == 0) return { 0, rank_to_tuple.n };
		size_t r = lower_bound(prefix[0]);
		if (r < rank_to_tuple.n && rank_to_tuple.p[r][0] == prefix[0]) return { r, r + 1 };
		return { r, r };
	}
};

/* Sparse index type: an explicit valid-tuple enumeration (formalism 3.5).
 *
 * Models the "arbitrary key set" regime -- edge lists, CG triples: a rank-k
 * index whose valid tuples are handed over as an explicit list rather than
 * derived from a mask over a product space. There are no per-axis extents and
 * no grid; the entry list IS the type's whole content.
 *
 * Derives from abstract_hashed_idx_t (NOT abstract_sorted_hashed_idx_t):
 * the keys are kept in GIVEN order, never sorted. A CompoundIdx earns its
 * sorted table for free from lex-enumerating its mask, which is what makes
 * contiguous prefix slicing possible; a sparse key set has no such structure
 * to exploit, so it keeps the caller's order (iteration order == key order)
 * and answers every lookup through the tuple hash. Consequently there is no
 * prefix_range here -- partial (wildcard) indexing is always a gather over
 * the entry list.
 *
 * Static: the entry set is fixed at construction (this type never calls the
 * base's insert()). Duplicate keys are a construction error -- the rank<->
 * tuple bijection would be ill-defined. hash() is discharged as a no-op
 * because the tables are built directly in the constructor from the given
 * list; there is nothing to enumerate.
 */
template<size_t RANK>
struct sparse_index_t : abstract_hashed_idx_t<RANK> {
	using base = abstract_hashed_idx_t<RANK>;
	using IDX = typename base::IDX;

	sparse_index_t(std::string name_in, std::vector<IDX> keys)
		: base(name_in, keys.size()) {
		this->rank_to_tuple = std::move(keys);
		this->tuple_to_rank.reserve(this->rank_to_tuple.size());
		for (size_t r = 0; r < this->rank_to_tuple.size(); r++) {
			this->tuple_to_rank[this->rank_to_tuple[r]] = r;
		}
		if (this->tuple_to_rank.size() != this->rank_to_tuple.size()) {
			throw std::runtime_error("SparseIdx '" + this->name + "': duplicate key tuple");
		}
		this->cardinality = this->rank_to_tuple.size();
	}

	// Tables are built in the constructor from the explicit key list; the
	// inherited pure-virtual build hook has nothing to do.
	void hash() override {}

	// Presence query (allocated-fallback / membership): does the key set
	// contain this full tuple? Hash probe, no grid to consult.
	bool present(const IDX& t) const {
		return this->tuple_to_rank.count(t) != 0;
	}
};
