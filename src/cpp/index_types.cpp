
#include "index_types.h"
#include <utility>

// Reference TU: a using-directive is safe in a .cpp -- it never leaks to
// other translation units the way a header-level one does. index_types.h
// is deliberately kept header-clean (fully std::-qualified) so it pollutes
// no includer, including generated code.
using namespace std;

/** promote class' implementation function */
template<typename TYPE, const size_t rank, const size_t depth = 0>
constexpr auto promote_impl() {

	if constexpr (depth < rank) {
		return promote_impl<typename add_pointer<TYPE>::type, rank, depth + 1>();
	}
	else if constexpr (depth == rank) {
		TYPE dummy = { 0 };
		return dummy;
	}
	else {
		return; // fatal
	}

}

/** Class to allow promotion of a value type by an arbitrary number of pointers at compile time. */
template<typename TYPE, const size_t rank, const size_t depth = 0>
class promote {

public:
	promote() {};
	~promote() {};

	typedef decltype(promote_impl<TYPE, rank>()) type;

};



struct simple_idx_t : abstract_simple_idx_t<1> {
	simple_idx_t(std::string name_in, size_t size_in)
		: abstract_simple_idx_t<1>(name_in, size_in) { this->hash(); }

	void hash() {
		this->cardinality = this->size;
		this->table.resize(this->cardinality);

		for (size_t i = 0; i < this->cardinality; i++) {
			this->table[i] = { i }; // trivial hashing
		}
	}

};

template<size_t ARITY>
struct symmetric_idx_t : abstract_simple_idx_t<ARITY> {
	using base = abstract_simple_idx_t<ARITY>;
	using IDX = typename base::IDX;
	symmetric_idx_t(std::string name_in, size_t size_in)
		: base(name_in, size_in) { this->hash(); }

	void hash() {
		this->cardinality = compute_cardinality();
		this->table.resize(this->cardinality);

		IDX indices{};
		size_t flat = 0;
		enumerate_sorted(indices, 0, 0, flat);
	}

private:

	size_t compute_cardinality() {
		// C(n+r-1, r) = (n+r-1)! / (r! * (n-1)!)
		size_t result = 1;
		for (size_t i = 0; i < ARITY; i++) {
			result = result * (this->size + ARITY - 1 - i) / (i + 1);
		}
		return result;
	}

	void enumerate_sorted(IDX& indices,
		size_t depth, size_t min_val, size_t& flat) {

		if (depth == ARITY) {
			this->table[flat++] = indices;
			return;
		}
		for (size_t i = min_val; i < this->size; i++) {
			indices[depth] = i;
			enumerate_sorted(indices, depth + 1, i, flat);
		}
	}
};


template<size_t ARITY>
struct antisymmetric_idx_t : abstract_simple_idx_t<ARITY> {
	using base = abstract_simple_idx_t<ARITY>;
	using IDX = typename base::IDX;
	antisymmetric_idx_t(std::string name_in, size_t size_in)
		: base(name_in, size_in) { this->hash(); }

	void hash() {
		this->cardinality = compute_cardinality();
		this->table.resize(this->cardinality);

		IDX indices{};
		size_t flat = 0;
		enumerate_strict(indices, 0, 0, flat);
	}

private:
	size_t compute_cardinality() {
		// C(n, r) = n! / (r! * (n-r)!) where r=ARITY, n=size
		size_t result = 1;
		for (size_t i = 0; i < ARITY; i++) {
			result = result * (this->size - i) / (i + 1);
		}
		return result;
	}

	void enumerate_strict(IDX& indices,
		size_t depth, size_t min_val, size_t& flat) {

		if (depth == ARITY) {
			this->table[flat++] = indices;
			return;
		}
		for (size_t i = min_val; i < this->size; i++) {
			indices[depth] = i;
			enumerate_strict(indices, depth + 1, i + 1, flat);  // strict
		}
	}
};
