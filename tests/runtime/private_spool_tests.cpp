// Standalone bounded protocol harness; NOT compiler admission. The sole runner
// compiles this file directly. Each child has a private, empty test root.
#define BLADE_PRIVATE_SPOOL_TESTING 1
#include "blade_private_spool.hpp"
#include <type_traits>
#include <string>
#include <cinttypes>

using namespace blade_private_spool;
static int passed = 0, failed = 0, skipped = 0;
static void check(const char* label, bool ok) {
  std::printf("%s %s\n", ok ? "PASS" : "FAIL", label);
  ok ? ++passed : ++failed;
}
static void skip(const char* label) { std::printf("SKIP %s\n", label); ++skipped; }
static Grant grant(const char* root, std::uint64_t n = 5, std::uint64_t b = 2) {
  Grant g; g.permitted = true; g.root = root; g.memory_bytes = 1u << 20;
  g.disk_bytes = 1u << 20; g.cells = n; g.block_cells = b; g.axis = UINT64_C(0x0123456789abcdef); return g;
}
static constexpr std::uint64_t pins[] = {
  UINT64_C(0x0000000000000000), UINT64_C(0x8000000000000000),
  UINT64_C(0x0000000000000001), UINT64_C(0x8000000000000001),
  UINT64_C(0x3ff0000000000000), UINT64_C(0x7ff0000000000000),
  UINT64_C(0xfff0000000000000), UINT64_C(0x7ff8000000000123),
  UINT64_C(0xfff8000000000456), UINT64_C(0x7ff0000000000001)
};
static void fill(double* dst, std::uint64_t origin, std::uint64_t n) {
  for (std::uint64_t i = 0; i < n; ++i) std::memcpy(dst + i, &pins[(origin + i) % 10], 8);
}
static bool exact(const double* dst, std::uint64_t origin, std::uint64_t n) {
  for (std::uint64_t i = 0; i < n; ++i) {
    std::uint64_t bits = 0; std::memcpy(&bits, dst + i, 8);
    if (bits != pins[(origin + i) % 10]) return false;
  }
  return true;
}
static void pure_plan_tests(const char* root) {
  static_assert(!std::is_copy_constructible_v<Writer> && !std::is_copy_constructible_v<Sealed>
                && !std::is_copy_constructible_v<Reader>);
  static_assert(std::is_move_constructible_v<Writer> && std::is_move_constructible_v<Sealed>
                && std::is_move_constructible_v<Reader>);
  const unsigned char text[] = "123456789";
  check("CRC64 format pin", detail::crc(text, 9) == UINT64_C(0x6c40df5f0b497347));
  Plan p{}; auto g = grant(root);
  check("checked disk capacity N5 B2 includes manifest", bool(plan(g, p)) && p.file_bytes == 368 && p.reserved_disk == 496 && p.blocks == 3);
  const auto workspace = p.workspace_bytes;
  g.disk_bytes = 495; check("quota one byte short", plan(g, p).code == Code::disk_quota);
  g.disk_bytes = 496; check("quota exact fit", bool(plan(g, p)));
  g.memory_bytes = workspace - 1; check("workspace one byte short", plan(g, p).code == Code::memory_quota);
  g.memory_bytes = workspace; check("workspace exact fit", bool(plan(g, p)));
  g.permitted = false; check("no implicit grant", plan(g, p).code == Code::no_grant);
  g = grant(root, 0); check("empty spool is header footer manifest only", bool(plan(g, p)) && p.file_bytes == 136 && p.reserved_disk == 264 && !p.blocks);
  g.block_cells = 0; check("zero block refuses", plan(g, p).code == Code::invalid_grant);
  g.block_cells = max_block_cells + 1; check("unbounded buffer refuses", plan(g, p).code == Code::invalid_grant);
  g = grant(root, UINT64_MAX); check("payload multiplication overflow", plan(g, p).code == Code::overflow);
  g = grant(root, UINT64_C(1152921504606846976)); check("signed file offset overflow", plan(g, p).code == Code::overflow);
  // Metadata only: never begin/create/allocate payload for this shape.
  g = grant(root, UINT64_C(1000000000000), 3); g.disk_bytes = UINT64_MAX;
  check("large shape metadata only has constant workspace", bool(plan(g, p)) && p.workspace_bytes == workspace + 8 && p.blocks == UINT64_C(333333333334));
}
#if defined(_WIN32)
static void roundtrip(const char* root, std::uint64_t n, std::uint64_t b, bool partial = false) {
  testing::faults = {};
  auto g = grant(root, n, b); Plan p{}; (void)plan(g, p);
  Writer writer; auto s = writer.begin(g);
  if (!s) { check("roundtrip begin", false); return; }
  Writer moved(std::move(writer)); double values[8] = {};
  check("moved writer cannot append", writer.append(values, 1).code == Code::state);
  if (partial) testing::faults.max_transfer = 3;
  std::uint64_t origin = 0;
  while (s && origin < n) {
    const auto length = n - origin < b ? n - origin : b;
    fill(values, origin, length); s = moved.append(values, length); origin += length;
  }
  Sealed sealed; if (s) s = moved.seal(sealed);
  check("writer seals complete sequence only", bool(s));
  check("consumed writer cannot append", moved.append(values, 1).code == Code::state);
  Sealed token(std::move(sealed)); Reader reader;
  if (s) s = token.open(reader);
  Reader duplicate; check("sealed token permits one reader", token.open(duplicate).code == Code::state);
  Reader owned(std::move(reader));
  origin = 0; bool correct = true;
  while (s) {
    std::uint64_t count = 0; s = owned.next(values, 8, count);
    if (!s || !count) break;
    correct = correct && exact(values, origin, count); origin += count;
  }
  const auto observed = counters();
  check("exact IEEE bits including signed zero subnormal NaN payload", bool(s) && correct && origin == n);
  check("payload and file traffic witness", observed.payload_written == n * 8 && observed.payload_read == n * 8
        && observed.data_written == p.file_bytes && observed.data_read == p.file_bytes);
  check("capacity ledger and disk high water", observed.available && observed.workspace_capacity == p.workspace_bytes
        && observed.logical_disk_high_water == p.reserved_disk && reserved_disk() == p.reserved_disk);
  if (s) s = owned.finish();
  check("finish requires replay checked and releases reservation", bool(s) && reserved_disk() == 0);
  check("finish is single use", owned.finish().code == Code::state);
  testing::faults = {};
}
static bool make_sealed(const char* root, Sealed& sealed) {
  Writer writer; auto s = writer.begin(grant(root)); double values[2] = {};
  for (std::uint64_t at = 0; s && at < 5; at += 2) {
    const auto length = 5 - at < 2 ? 5 - at : 2;
    fill(values, at, length); s = writer.append(values, length);
  }
  if (s) s = writer.seal(sealed);
  return bool(s);
}
static bool mutate(const char* path, std::uint64_t offset, bool truncate = false, bool append = false) {
  HANDLE file = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
  if (file == INVALID_HANDLE_VALUE) return false;
  LARGE_INTEGER where{}; where.QuadPart = static_cast<LONGLONG>(offset);
  bool ok = SetFilePointerEx(file, where, nullptr, FILE_BEGIN) != 0;
  if (ok && truncate) ok = SetEndOfFile(file) != 0;
  else if (ok) {
    unsigned char value = 0; DWORD got = 0;
    if (!append) ok = ReadFile(file, &value, 1, &got, nullptr) && got == 1;
    value ^= 1;
    if (ok) ok = SetFilePointerEx(file, where, nullptr, FILE_BEGIN) != 0;
    if (ok) ok = WriteFile(file, &value, 1, &got, nullptr) && got == 1;
  }
  if (!CloseHandle(file)) ok = false;
  return ok;
}
static void corruptions(const char* root) {
  // Header, every block-header field, payload, footer, short and overlong file.
  const std::uint64_t offsets[] = {0,8,16,24,32,40,48,56,64,72,80,88,96,104,112,120,128,136,304,312,320,328,336,344,352,360};
  for (const auto offset : offsets) {
    testing::faults = {}; Sealed sealed;
    if (!make_sealed(root, sealed)) { check("corruption fixture seals", false); return; }
    check("bounded byte corruption injected", mutate(detail::active.data, offset));
    Reader reader; auto s = sealed.open(reader); double values[2] = {};
    while (s) { std::uint64_t count = 0; s = reader.next(values, 2, count); if (!s || !count) break; }
    check("corrupt format or payload refuses", !s && s.code == Code::integrity);
    if (detail::active.phase != Phase::idle) { if (reader.cancel().code == Code::state) (void)sealed.cancel(); }
  }
  for (const bool append : {false, true}) {
    testing::faults = {}; Sealed sealed;
    if (!make_sealed(root, sealed)) { check("length fixture seals", false); return; }
    const auto bytes = detail::active.plan.file_bytes;
    check("bounded truncation or trailing byte injected", mutate(detail::active.data, append ? bytes : bytes - 1, !append, append));
    Reader reader; check("exact file length required", sealed.open(reader).code == Code::integrity);
    (void)sealed.cancel();
  }
}
// Fixture operations use exact file handles, preserving the runtime's protected
// ancestor directory handles. A setup failure stops THIS fixture immediately.
static bool fixture_step(const char* label, Status actual, Code expected = Code::ok) {
  const bool ok = actual.code == expected; check(label, ok);
  if (!ok) std::printf("  fixture diagnostic: code=%u expected=%u native_error=%lu\n",
                       static_cast<unsigned>(actual.code), static_cast<unsigned>(expected),
                       static_cast<unsigned long>(actual.os_error));
  return ok;
}
static Status fixture_info(HANDLE file, std::uint64_t& volume, std::uint64_t& id) {
  BY_HANDLE_FILE_INFORMATION info{};
  if (!GetFileInformationByHandle(file, &info)) return {Code::ownership, GetLastError()};
  if (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) return {Code::ownership};
  volume = info.dwVolumeSerialNumber; id = (std::uint64_t(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
  return id ? Status{} : Status{Code::ownership};
}
static Status fixture_unlink(const char* path, std::uint64_t expected_volume, std::uint64_t expected_id) {
  // Track even a failed native close in the stable test owner's auxiliary
  // slot, so the token's ordinary cleanup can retry without a dangling local.
  auto& file = detail::active.auxiliary;
  if (file != INVALID_HANDLE_VALUE) return {Code::state};
  file = CreateFileA(path, DELETE | FILE_READ_ATTRIBUTES, 0, nullptr, OPEN_EXISTING,
                     FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  if (file == INVALID_HANDLE_VALUE) return {Code::open, GetLastError()};
  std::uint64_t volume = 0, id = 0; auto status = fixture_info(file, volume, id);
  if (status && (volume != expected_volume || id != expected_id)) status = {Code::ownership};
  FILE_DISPOSITION_INFO disposition{TRUE};
  if (status && !SetFileInformationByHandle(file, FileDispositionInfo, &disposition, sizeof disposition))
    status = {Code::remove, GetLastError()};
  const auto closed = detail::close_handle(file); if (status) status = closed;
  return status;
}
static Status fixture_identity(const char* path, std::uint64_t& volume, std::uint64_t& id, bool create) {
  auto& file = detail::active.auxiliary;
  if (file != INVALID_HANDLE_VALUE) return {Code::state};
  file = CreateFileA(path, create ? (GENERIC_READ | GENERIC_WRITE | FILE_READ_ATTRIBUTES) : FILE_READ_ATTRIBUTES,
                     0, nullptr, create ? CREATE_NEW : OPEN_EXISTING,
                     FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  if (file == INVALID_HANDLE_VALUE) return {create ? Code::create : Code::open, GetLastError()};
  auto status = fixture_info(file, volume, id);
  const auto closed = detail::close_handle(file); if (status) status = closed;
  return status;
}
static void missing_replay_fixture(const char* root) {
  Sealed sealed; const bool prepared = make_sealed(root, sealed);
  check("reopen fixture seals", prepared); if (!prepared) return;
  char original[path_capacity] = {}; std::memcpy(original, detail::active.data, path_capacity);
  const auto volume = detail::active.data_volume, id = detail::active.data_id;
  if (!fixture_step("missing fixture removes exact original by handle", fixture_unlink(original, volume, id))) return;
  Reader reader;
  if (!fixture_step("missing replay file refuses", sealed.open(reader), Code::open)) return;
  if (!fixture_step("missing replay cleanup succeeds without restoring a file", sealed.cancel())) return;
  check("missing replay fixture releases ownership", reserved_disk() == 0);
}
static void replaced_identity_fixture(const char* root) {
  Sealed sealed; const bool prepared = make_sealed(root, sealed);
  check("replacement fixture seals", prepared); if (!prepared) return;
  char original[path_capacity] = {}; std::memcpy(original, detail::active.data, path_capacity);
  const auto original_volume = detail::active.data_volume, original_id = detail::active.data_id;
  if (!fixture_step("replacement fixture removes exact original by handle", fixture_unlink(original, original_volume, original_id))) return;
  std::uint64_t volume = 0, id = 0;
  if (!fixture_step("harness creates its separately owned replacement", fixture_identity(original, volume, id, true))) return;
  const bool different = volume != original_volume || id != original_id;
  check("replacement has proven distinct file identity", different);
  if (!different) { (void)fixture_step("retire unusable replacement fixture", fixture_unlink(original, volume, id)); return; }
  Reader reader;
  if (!fixture_step("same path different file identity refuses", sealed.open(reader), Code::ownership)) return;
  // This assertion MUST precede the harness's deletion: production cleanup
  // closes its attempted reader but refuses to unlink the unowned file ID.
  if (!fixture_step("runtime cleanup refuses the replacement identity", sealed.cancel(), Code::ownership)) return;
  std::uint64_t remaining_volume = 0, remaining_id = 0;
  if (!fixture_step("replacement still exists after refused cleanup", fixture_identity(original, remaining_volume, remaining_id, false))) return;
  const bool untouched = remaining_volume == volume && remaining_id == id
                        && GetFileAttributesA(detail::active.manifest) != INVALID_FILE_ATTRIBUTES && reserved_disk() != 0;
  check("refused cleanup preserves replacement and owner evidence", untouched); if (!untouched) return;
  if (!fixture_step("harness unlinks only its verified replacement handle", fixture_unlink(original, volume, id))) return;
  if (!fixture_step("runtime cleans owner after replacement is gone", sealed.cancel())) return;
  check("replacement fixture releases ownership", reserved_disk() == 0);
}
static bool state_and_faults(const char* root) {
  testing::faults = {}; double values[2] = {}; fill(values, 0, 2);
  {
    testing::faults.allocation = true; Writer writer;
    check("allocation failure before private files", writer.begin(grant(root)).code == Code::allocation && reserved_disk() == 0);
    testing::faults = {};
  }
  missing_replay_fixture(root);
  if (reserved_disk()) { std::puts("STOP state/fault section: missing fixture retains an owner"); return false; }
  replaced_identity_fixture(root);
  if (reserved_disk()) { std::puts("STOP state/fault section: replacement fixture retains an owner"); return false; }
  {
    try {
      Writer writer; check("exception owner fixture begins", bool(writer.begin(grant(root)))); throw 7;
    } catch (int) {}
    check("exception unwinding retires stable owner", reserved_disk() == 0);
  }
  {
    Writer writer; check("state fixture begin", bool(writer.begin(grant(root))));
    Writer other; check("one active writer", other.begin(grant(root)).code == Code::busy);
    Sealed sealed; check("early seal refuses", writer.seal(sealed).code == Code::state);
    check("poisoned writer cannot resume", writer.append(values, 2).code == Code::state);
    check("cancel cleans incomplete writer", bool(writer.cancel()) && reserved_disk() == 0);
    check("double cancel refuses", writer.cancel().code == Code::state);
  }
  {
    Writer writer; check("bad block fixture begin", bool(writer.begin(grant(root))));
    check("noncanonical short block refuses", writer.append(values, 1).code == Code::state); (void)writer.cancel();
  }
  for (const bool zero : {false, true}) {
    testing::faults = {}; Writer writer; check("write fault begin", bool(writer.begin(grant(root))));
    const auto at = counters().data_written + 5;
    if (zero) testing::faults.zero_write_at = at; else testing::faults.write_error_at = at;
    const auto s = writer.append(values, 2);
    check("short write continues until deliberate error or zero progress", s.code == (zero ? Code::zero_progress : Code::write));
    testing::faults.remove = true;
    check("cleanup failure retains quota and primary", writer.cancel().code == Code::remove
          && reserved_disk() != 0 && primary_status().code == s.code);
    testing::faults = {}; check("explicit retry cleans pending owner", bool(writer.cancel()) && reserved_disk() == 0);
  }
  for (const bool close : {false, true}) {
    testing::faults = {}; Writer writer; check("seal fault begin", bool(writer.begin(grant(root, 0))));
    if (close) testing::faults.close = true; else testing::faults.flush = true;
    Sealed sealed; check("flush or close failure prevents sealing", writer.seal(sealed).code == (close ? Code::close : Code::flush));
    Reader reader; check("failed seal has no reader token", sealed.open(reader).code == Code::state);
    testing::faults = {}; check("seal fault cleanup", bool(writer.cancel()));
  }
  for (const bool zero : {false, true}) {
    testing::faults = {}; Sealed sealed; check("read fault fixture seals", make_sealed(root, sealed));
    Reader reader; auto s = sealed.open(reader); check("read fault reader opens", bool(s));
    const auto at = counters().data_read + block_header_bytes + 5;
    if (zero) testing::faults.zero_read_at = at; else testing::faults.read_error_at = at;
    std::uint64_t count = 99; values[0] = 123.0; s = reader.next(values, 2, count);
    check("short or zero read cannot publish incomplete block", s.code == (zero ? Code::zero_progress : Code::read) && !count && values[0] == 123.0);
    check("unchecked reader cannot finish", reader.finish().code == Code::state);
    testing::faults = {}; check("read fault cleanup", bool(reader.cancel()));
  }
  {
    testing::faults = {}; Writer writer; check("unexpected entry fixture begin", bool(writer.begin(grant(root))));
    char foreign[path_capacity] = {}; (void)detail::path(foreign, detail::active.directory, "unrelated.txt");
    HANDLE file = detail::create_file(foreign); check("unexpected entry fixture created", file != INVALID_HANDLE_VALUE); (void)detail::close_handle(file);
    check("unexpected entry retained without recursive deletion", writer.cancel().code == Code::ownership
          && GetFileAttributesA(foreign) != INVALID_FILE_ATTRIBUTES && GetFileAttributesA(detail::active.manifest) != INVALID_FILE_ATTRIBUTES);
    check("harness removes only its own foreign fixture", DeleteFileA(foreign) != 0);
    check("owner retry after foreign fixture removed", bool(writer.cancel()));
  }
  {
    detail::Guard held; Writer writer;
    check("overlapping API entry fails closed", writer.begin(grant(root)).code == Code::busy);
  }
  return true;
}
static void noop() {}
static int child(const char* root, const char* mode) {
  if (!std::strcmp(mode, "hook-registration")) {
    testing::faults.exit_registration = true; Writer first;
    if (first.begin(grant(root)).code != Code::exit_registration) return 37;
    testing::faults = {}; Writer second;
    if (!second.begin(grant(root))) return 38;
    return second.cancel() ? 0 : 39;
  }
  if (!std::strcmp(mode, "hooks")) {
    // Legacy void callers still fill slots, checked callers see exhaustion.
    for (unsigned i = 0; i < 4; ++i) blade_rt::on_failure_exit(noop);
    for (unsigned i = 0; i < 4; ++i) if (!blade_rt::try_on_failure_exit(noop)) return 30;
    if (blade_rt::try_on_failure_exit(noop) || blade_rt::try_on_failure_exit(nullptr)) return 31;
    Writer writer; return writer.begin(grant(root)).code == Code::hooks_full ? 0 : 32;
  }
  Writer writer; if (!writer.begin(grant(root))) return 33;
  double values[2] = {}; fill(values, 0, 2); if (!writer.append(values, 2)) return 34;
  if (!std::strcmp(mode, "return")) return 0;
  if (!std::strcmp(mode, "exit")) std::exit(7);
  if (!std::strcmp(mode, "panic") || !std::strcmp(mode, "panic-remove")) {
    testing::faults.remove = !std::strcmp(mode, "panic-remove");
    blade_rt::panic("BL8005", "spool protocol primary", nullptr, 0);
  }
  if (!std::strcmp(mode, "late-dir") || !std::strcmp(mode, "late-manifest")
      || !std::strcmp(mode, "late-lock") || !std::strcmp(mode, "late-close-after")) {
    testing::faults.directory_remove = !std::strcmp(mode, "late-dir");
    testing::faults.manifest_remove = !std::strcmp(mode, "late-manifest");
    testing::faults.lock_close = !std::strcmp(mode, "late-lock");
    testing::faults.lock_close_after_success = !std::strcmp(mode, "late-close-after");
    std::exit(11);
  }
  if (!std::strcmp(mode, "orphan")) std::_Exit(9);
  if (!std::strcmp(mode, "hold")) {
    char ready[path_capacity] = {}; (void)detail::path(ready, root, "ready");
    HANDLE file = detail::create_file(ready); if (file == INVALID_HANDLE_VALUE) return 35;
    (void)detail::close_handle(file);
    Sleep(10000); std::_Exit(10); // Hard upper bound even if parent disappears.
  }
  return 36;
}
static unsigned owned_count(const char* root, char* name = nullptr) {
  char pattern[path_capacity] = {}; if (!detail::path(pattern, root, "*")) return UINT32_MAX;
  WIN32_FIND_DATAA item{}; HANDLE find = FindFirstFileA(pattern, &item);
  if (find == INVALID_HANDLE_VALUE) return UINT32_MAX;
  unsigned count = 0;
  do {
    if (detail::valid_name(item.cFileName)) { ++count; if (name) std::memcpy(name, item.cFileName, 48); }
    else if (std::strlen(item.cFileName) == 53 && !std::strcmp(item.cFileName + 47, ".owner")) {
      char run[48] = {}; std::memcpy(run, item.cFileName, 47);
      if (detail::valid_name(run)) {
        char directory[path_capacity] = {}; (void)detail::path(directory, root, run);
        if (GetFileAttributesA(directory) == INVALID_FILE_ATTRIBUTES) ++count;
        if (name) std::memcpy(name, run, 48);
      }
    }
  } while (FindNextFileA(find, &item));
  FindClose(find); return count;
}
static bool start_child(const char* root, const char* mode, PROCESS_INFORMATION& process) {
  char executable[path_capacity] = {}, command[1024] = {}, log[path_capacity] = {};
  const auto n = GetModuleFileNameA(nullptr, executable, path_capacity);
  if (!n || n >= path_capacity || !detail::path(log, root, "child.log")) return false;
  const int used = std::snprintf(command, sizeof command, "\"%s\" \"%s\" %s", executable, root, mode);
  if (used < 0 || std::size_t(used) >= sizeof command) return false;
  SECURITY_ATTRIBUTES security{sizeof security, nullptr, TRUE};
  HANDLE output = CreateFileA(log, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               &security, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (output == INVALID_HANDLE_VALUE) return false;
  STARTUPINFOA startup{}; startup.cb = sizeof startup; startup.dwFlags = STARTF_USESTDHANDLES;
  startup.hStdOutput = output; startup.hStdError = output; startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
  const BOOL ok = CreateProcessA(executable, command, nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
                                 nullptr, nullptr, &startup, &process);
  CloseHandle(output); return ok != 0;
}
static bool wait_child(PROCESS_INFORMATION& process, DWORD expected) {
  const DWORD waited = WaitForSingleObject(process.hProcess, 5000);
  if (waited != WAIT_OBJECT_0) { TerminateProcess(process.hProcess, 40); WaitForSingleObject(process.hProcess, 2000); }
  DWORD code = 0; const bool ok = waited == WAIT_OBJECT_0 && GetExitCodeProcess(process.hProcess, &code) && code == expected;
  CloseHandle(process.hThread); CloseHandle(process.hProcess); return ok;
}
static bool manifest_edit(const char* path, unsigned char* bytes, bool write) {
  HANDLE file = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
  if (file == INVALID_HANDLE_VALUE) return false;
  DWORD got = 0; const bool ok = (write ? WriteFile(file, bytes, manifest_bytes, &got, nullptr)
                                      : ReadFile(file, bytes, manifest_bytes, &got, nullptr)) && got == manifest_bytes;
  return CloseHandle(file) && ok;
}
static void children_and_recovery(const char* root) {
  const char* modes[] = {"return", "exit", "panic", "panic-remove", "hooks", "hook-registration", "orphan", "hold",
                         "late-dir", "late-manifest", "late-lock", "late-close-after"};
  unsigned index = 0;
  for (const auto mode : modes) {
    char child_root[path_capacity] = {}, leaf[32] = {}, name[48] = {};
    std::snprintf(leaf, sizeof leaf, "child-%u", index++);
    if (!detail::path(child_root, root, leaf) || !CreateDirectoryA(child_root, nullptr)) { check("private child root", false); continue; }
    PROCESS_INFORMATION process{};
    if (!start_child(child_root, mode, process)) { check("bounded child starts", false); continue; }
    if (!std::strcmp(mode, "hold")) {
      char ready[path_capacity] = {}; (void)detail::path(ready, child_root, "ready");
      unsigned polls = 0; for (; polls < 200 && GetFileAttributesA(ready) == INVALID_FILE_ATTRIBUTES; ++polls) Sleep(10);
      check("live child reached owned state", polls < 200 && owned_count(child_root, name) == 1);
      check("live exclusive owner cannot be recovered", recover(grant(child_root), name).code == Code::busy);
      check("bounded forced termination", TerminateProcess(process.hProcess, 12) != 0);
      check("forced child exits", wait_child(process, 12)); DeleteFileA(ready);
      check("explicit dead owner recovery after kill", bool(recover(grant(child_root), name)) && owned_count(child_root) == 0);
    } else {
      const bool late = !std::strncmp(mode, "late-", 5);
      const bool close_after = !std::strcmp(mode, "late-close-after");
      const DWORD expected = late ? 11 : !std::strcmp(mode, "exit") ? 7 : (!std::strcmp(mode, "panic") || !std::strcmp(mode, "panic-remove")) ? 1 : !std::strcmp(mode, "orphan") ? 9 : 0;
      check("child preserves primary exit status", wait_child(process, expected));
      const bool leftover = !std::strcmp(mode, "orphan") || !std::strcmp(mode, "panic-remove") || (late && !close_after);
      check("normal exit and panic hook ownership", owned_count(child_root, name) == (leftover ? 1u : 0u));
      if (leftover && late) {
        char directory[path_capacity] = {}, owner[54] = {}, manifest[path_capacity] = {};
        (void)detail::path(directory, child_root, name);
        std::memcpy(owner, name, 47); std::memcpy(owner + 47, ".owner", 7); (void)detail::path(manifest, child_root, owner);
        check("late cleanup failure preserves root owner evidence", GetFileAttributesA(manifest) != INVALID_FILE_ATTRIBUTES);
        check("late failure has exact directory retirement state",
              (GetFileAttributesA(directory) != INVALID_FILE_ATTRIBUTES) == !std::strcmp(mode, "late-dir"));
        check("real exited owner recovers after late-stage cleanup fault", bool(recover(grant(child_root), name)) && owned_count(child_root) == 0);
      }
      if (leftover && !late) {
        testing::faults.owner_unknown = true;
        check("uncertain process identity retains", recover(grant(child_root), name).code == Code::uncertain && owned_count(child_root) == 1);
        testing::faults = {};
        char manifest[path_capacity] = {}, owner[54] = {};
        std::memcpy(owner, name, 47); std::memcpy(owner + 47, ".owner", 7); (void)detail::path(manifest, child_root, owner);
        unsigned char original[manifest_bytes] = {}, altered[manifest_bytes] = {};
        check("fixed ownership manifest readable", manifest_edit(manifest, original, false));
        std::memcpy(altered, original, manifest_bytes); altered[0] ^= 1;
        check("invalid manifest injected", manifest_edit(manifest, altered, true));
        check("unrecognized ownership retained", recover(grant(child_root), name).code == Code::ownership && owned_count(child_root) == 1);
        std::memcpy(altered, original, manifest_bytes); detail::put(altered + 96, 0); detail::put(altered + 120, detail::crc(altered, 120));
        check("malformed bounded descriptor injected", manifest_edit(manifest, altered, true));
        check("malformed descriptor retains", recover(grant(child_root), name).code == Code::ownership);
        std::memcpy(altered, original, manifest_bytes); detail::put(altered + 48, detail::get(altered + 48) ^ 1); detail::put(altered + 120, detail::crc(altered, 120));
        check("root identity mismatch injected", manifest_edit(manifest, altered, true));
        check("root identity mismatch retains", recover(grant(child_root), name).code == Code::ownership);
        std::uint64_t start = 0; check("current process start identity", detail::process_start(GetCurrentProcess(), start));
        std::memcpy(altered, original, manifest_bytes); detail::put(altered + 16, GetCurrentProcessId()); detail::put(altered + 24, start); detail::put(altered + 120, detail::crc(altered, 120));
        check("live identity without held lock injected", manifest_edit(manifest, altered, true));
        check("PID plus live start retains even with lock available", recover(grant(child_root), name).code == Code::busy);
        detail::put(altered + 24, start - 1); detail::put(altered + 120, detail::crc(altered, 120));
        check("PID reuse identity fixture injected", manifest_edit(manifest, altered, true));
        check("different process start proves recorded owner dead", bool(recover(grant(child_root), name)) && owned_count(child_root) == 0);
      }
      if (close_after) {
        char log[path_capacity] = {}; (void)detail::path(log, child_root, "child.log");
        std::FILE* file = std::fopen(log, "rb"); char text[2048] = {};
        if (file) { std::fread(text, 1, sizeof text - 1, file); std::fclose(file); }
        check("close-after-success reports retirement without invented evidence", std::strstr(text, "failure after scratch retirement") != nullptr
              && owned_count(child_root) == 0);
      }
      if (!std::strcmp(mode, "panic") || !std::strcmp(mode, "panic-remove")) {
        char log[path_capacity] = {}; (void)detail::path(log, child_root, "child.log");
        std::FILE* file = std::fopen(log, "rb"); char text[2048] = {};
        if (file) { std::fread(text, 1, sizeof text - 1, file); std::fclose(file); }
        check("cleanup does not replace primary diagnostic", std::strstr(text, "error[BL8005]: spool protocol primary") != nullptr);
        if (!std::strcmp(mode, "panic-remove")) check("secondary cleanup failure visible", std::strstr(text, "cleanup incomplete") != nullptr);
      }
    }
    char log[path_capacity] = {}; (void)detail::path(log, child_root, "child.log"); DeleteFileA(log);
    check("child directory nonrecursive removal", RemoveDirectoryA(child_root) != 0);
  }
}
static void path_controls(const char* root) {
  auto g = grant(root); Writer writer;
  g.root = "."; check("relative scratch root refuses", writer.begin(g).code == Code::root);
  char dot[path_capacity] = {}; (void)detail::path(dot, root, "."); g.root = dot;
  Writer another; check("noncanonical root refuses", another.begin(g).code == Code::root);
  check("unrecognized recovery path refuses", recover(grant(root), "../outside").code == Code::ownership);
  char alias[path_capacity] = {}; (void)detail::path(alias, root, "alias");
  if (CreateSymbolicLinkA(alias, root, SYMBOLIC_LINK_FLAG_DIRECTORY | 2)) {
    g.root = alias; Writer linked; check("reparse root refuses", linked.begin(g).code == Code::root);
    check("reparse fixture removed without following", RemoveDirectoryA(alias) != 0);
  } else skip("directory symlink creation unavailable; reparse root live probe");
}
#endif
int main(int argc, char** argv) {
  if (argc < 2) { std::fputs("usage: private_spool_tests ABSOLUTE_EMPTY_SCRATCH_ROOT [child-mode]\n", stderr); return 2; }
#if defined(_WIN32)
  if (argc == 3) return child(argv[1], argv[2]);
#endif
  pure_plan_tests(argv[1]);
  if (!supported()) skip("private spool owner identity unsupported on this platform; no files created");
#if defined(_WIN32)
  else {
    check("root exists and is initially empty of spool runs", owned_count(argv[1]) == 0);
    roundtrip(argv[1], 0, 2); roundtrip(argv[1], 1, 2); roundtrip(argv[1], 5, 2);
    roundtrip(argv[1], 17, 3, true); roundtrip(argv[1], 257, 3);
    corruptions(argv[1]);
    if (state_and_faults(argv[1])) { path_controls(argv[1]); children_and_recovery(argv[1]); }
    else std::puts("STOP remaining sections: preserve pending fixture evidence for inspection");
    check("all owned spool runs cleaned", owned_count(argv[1]) == 0 && reserved_disk() == 0);
  }
#endif
  std::printf("private-spool: %d passed, %d failed, %d skipped\n", passed, failed, skipped);
  return failed ? 1 : 0;
}
