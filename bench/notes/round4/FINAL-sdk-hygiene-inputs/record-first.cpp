// Compile only: both default and -DBLADE_MACRO_PREDEFINED, also -DNO_STRICT.
#if defined(far) || defined(near) || defined(pascal)
#error fixture requires initially undefined legacy macros
#endif
#if defined(BLADE_MACRO_PREDEFINED)
#define far 901
#define near 902
#define pascal 903
#endif
#include "blade_run_record.hpp"
#include "check-state.hpp"
#include "blade_private_spool.hpp"
#include "check-state.hpp"
#if defined(BLADE_MACRO_PREDEFINED)
#undef far
#undef near
#undef pascal
#endif
static_assert(__cplusplus >= 201703L, "C++17 required");
void final_record_first() {
  int far = 1, near = 2, pascal = 3;
  (void)(far + near + pascal);
  char path[8] = {};
  blade_rr::self_path(path, sizeof path);
  blade_private_spool::Plan plan;
  (void)blade_private_spool::plan({}, plan);
}
