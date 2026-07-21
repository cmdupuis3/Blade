#pragma once

#include <string>
#include <vector>
#include <array>
#include <unordered_map>
#include <functional>
#include <utility>

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

	// tuple -> rank. O(1) amortized. Throws if the tuple was never inserted.
	size_t linearize(const IDX& tuple) const {
		return this->tuple_to_rank.at(tuple);
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
	size_t linearize(const IDX& tuple) const {
		return this->tuple_to_rank.at(tuple);
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

	compound_index_t(std::string name_in, IDX extents_in, std::vector<bool> mask_in)
		: base(name_in, product(extents_in)), extents(extents_in), mask(std::move(mask_in)) {
		this->hash();
	}

	void hash() {
		this->rank_to_tuple.clear();
		this->tuple_to_rank.clear();
		IDX idx{};
		enumerate(0, idx);
		this->cardinality = this->rank_to_tuple.size();
	}

	// Presence (allocation) query for the <|:> allocated-fallback read: does
	// this compound hold a cell for the full tuple? The mask bit IS the
	// allocation record of the compact buffer (absent cells have no storage).
	bool present(const IDX& t) const {
		return mask[mask_offset(t)];
	}

	std::pair<size_t, size_t> prefix_range(const IDX& prefix, size_t plen) const {
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
