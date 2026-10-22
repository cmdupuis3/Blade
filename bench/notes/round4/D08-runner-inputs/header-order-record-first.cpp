// Sole-Luna compile-only C++17 fixture, also compile with -DNO_STRICT.
#include "blade_run_record.hpp"
#include "blade_private_spool.hpp"
static_assert(__cplusplus >= 201703L, "C++17 required");
void d08_record_first() {
  char path[8] = {};
  blade_rr::self_path(path, sizeof path);
  blade_private_spool::Plan plan;
  (void)blade_private_spool::plan({}, plan);
}
