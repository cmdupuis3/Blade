#pragma once

#include <string>
#include <vector>
#include <array>
#include <unordered_map>

using namespace std;

/* Abstract base class for all index types.
 */
template<size_t ARITY>
struct abstract_idx_t {
	using IDX = std::array<size_t, ARITY>;
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
template<typename DERIVED, size_t ARITY>
struct abstract_simple_idx_t : abstract_idx_t<ARITY> {
	using IDX = typename abstract_idx_t<ARITY>::IDX;
	std::vector<IDX> table;

	abstract_simple_idx_t(std::string name_in, size_t size_in)
		: abstract_idx_t<ARITY>(name_in, size_in) {
		static_cast<DERIVED*>(this)->hash();
	}

	IDX unhash(size_t index) {
		return this->table[index];
	}
};

/* Abstract hashed index types.
 * 
 * Constructs a hash table of all valid index tuples in the iteration space.
 */
template<typename DERIVED, size_t ARITY>
struct abstract_hashed_idx_t : abstract_idx_t<ARITY> {
	using IDX = typename abstract_idx_t<ARITY>::IDX;
	std::unordered_map<size_t, IDX> table;

	abstract_hashed_idx_t(std::string name_in, size_t size_in)
		: abstract_idx_t<ARITY>(name_in, size_in) {
		static_cast<DERIVED*>(this)->hash();
	}

	IDX unhash(size_t index) {
		return this->table[index];
	}

};
