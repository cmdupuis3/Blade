// gr-render -- translate a plotly figure spec into a GR-rendered image.
//
//   one-shot:  gr-render --out PATH [--width N] [--height N] [--format png|svg|pdf]
//              (the figure JSON arrives on stdin)
//   serve:     gr-render --serve
//              (NDJSON request per line on stdin, one response line per request)
//   video:     gr-render --video --out PATH [--width N] [--height N] [--fps N]
//              (one figure spec per stdin line; each becomes one frame)
//
// See README.md for the full contract.  Every GR-touching detail worth knowing
// is commented in render.hpp.
#include <fcntl.h>
#ifdef _WIN32
#include <io.h>
#include <process.h>
#else
#include <unistd.h>
#endif

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <string>
#include <vector>

#include "base64.hpp"
#include "figure.hpp"
#include "json.hpp"
#include "render.hpp"

namespace {

// ---- platform shim ---------------------------------------------------------
//
// This file was written against the MSVC/UCRT spellings (_dup, _setmode,
// _putenv, ...).  Off Windows they are the same calls without the leading
// underscore, with two exceptions worth stating rather than hiding:
//
//   - _setmode(fd, _O_BINARY) has no POSIX counterpart because it has nothing
//     to do: a POSIX fd is already binary, there is no CRLF translation to
//     turn off.  binaryFd/binaryStream are deliberate no-ops there, not TODOs.
//   - _putenv takes one "NAME=VALUE" string; setenv takes the two halves plus
//     an overwrite flag.  setEnvVar hands both spellings the same pair, so
//     `setEnvVar("GR_DISPLAY", "")` sets an EMPTY value on both -- it does not
//     unset the variable, which is what GR needs (see setupEnv below).
namespace plat {

#ifdef _WIN32
constexpr char kPathSep = '\\';
inline int dupFd(int fd) { return _dup(fd); }
inline int redirectFd(int from, int to) { return _dup2(from, to); }
inline void binaryFd(int fd) { _setmode(fd, _O_BINARY); }
inline void binaryStream(std::FILE *f) { _setmode(_fileno(f), _O_BINARY); }
inline std::FILE *fdOpen(int fd, const char *mode) { return _fdopen(fd, mode); }
inline void setEnvVar(const char *name, const char *value) { _putenv_s(name, value); }
inline long processId() { return long(_getpid()); }
#else
constexpr char kPathSep = '/';
inline int dupFd(int fd) { return ::dup(fd); }
inline int redirectFd(int from, int to) { return ::dup2(from, to); }
inline void binaryFd(int) {}
inline void binaryStream(std::FILE *) {}
inline std::FILE *fdOpen(int fd, const char *mode) { return ::fdopen(fd, mode); }
inline void setEnvVar(const char *name, const char *value) { ::setenv(name, value, 1); }
inline long processId() { return long(::getpid()); }
#endif

}  // namespace plat

FILE *g_out = nullptr;  // the REAL stdout; fd 1 is rewired to stderr
long g_counter = 0;

// GR (and the plugins it loads) occasionally print diagnostics on stdout.  In
// serve mode stdout carries NDJSON only, so fd 1 is pointed at stderr and the
// original handle is kept privately for responses.
void captureStdout() {
  int saved = plat::dupFd(1);
  if (saved >= 0) {
    plat::redirectFd(2, 1);
    plat::binaryFd(saved);
    g_out = plat::fdOpen(saved, "wb");
  }
  if (!g_out) g_out = stdout;
}

void writeLine(const std::string &s) {
  std::fwrite(s.data(), 1, s.size(), g_out);
  std::fputc('\n', g_out);
  std::fflush(g_out);
}

// GR must never fall back to its Qt workstation: gksqt.exe would linger.
// GKS_WSTYPE is cached in a static on first use, so this has to happen before
// any GR entry point runs.
void setupEnv() {
  plat::setEnvVar("GKS_WSTYPE", "100");
  plat::setEnvVar("GR_DISPLAY", "");
  const char *grdir = std::getenv("GRDIR");
  if (!grdir || !*grdir)
    throw std::runtime_error(
        "GRDIR is not set; point it at the GR install root (its bin/ must also "
        "be on PATH) -- without it GR dies with an access violation");
}

// TEMP/TMP are the Windows spelling; POSIX spells it TMPDIR and guarantees
// /tmp when even that is unset.  Without the POSIX arm this fell through to
// ".", i.e. it scattered render temp files across whatever directory the
// caller happened to be in.
std::string tempDir() {
  const char *t = std::getenv("TEMP");
  if (!t || !*t) t = std::getenv("TMP");
#ifndef _WIN32
  if (!t || !*t) t = std::getenv("TMPDIR");
  if (!t || !*t) t = "/tmp";
#endif
  if (!t || !*t) t = ".";
  return std::string(t);
}

std::string tempFile(const std::string &ext) {
  return tempDir() + plat::kPathSep + "gr-render-" + std::to_string(plat::processId()) + "-" +
         std::to_string(++g_counter) + "." + ext;
}

struct TempGuard {
  std::string path;
  explicit TempGuard(std::string p) : path(std::move(p)) {}
  ~TempGuard() { std::remove(path.c_str()); }
};

bool validFormat(const std::string &f) { return f == "png" || f == "svg" || f == "pdf"; }

// Containers GR's video plugin writes (a statically linked ffmpeg: h264 for
// mp4, vp8 for webm, theora for ogg).  Kept separate from validFormat because
// a video is not a still with a different extension -- it consumes a STREAM of
// specs, which is a different stdin contract, so it is reached only through an
// explicit --video rather than by extension.
bool validVideoFormat(const std::string &f) {
  return f == "mp4" || f == "webm" || f == "ogg" || f == "gif";
}

std::string extensionOf(const std::string &path) {
  std::size_t dot = path.find_last_of('.');
  std::size_t sep = path.find_last_of("/\\");
  if (dot == std::string::npos || (sep != std::string::npos && dot < sep)) return std::string();
  std::string e = path.substr(dot + 1);
  for (char &c : e)
    if (c >= 'A' && c <= 'Z') c = char(c - 'A' + 'a');
  return e;
}

// The whole render pipeline: spec -> bytes.  Always via a temp file, so a
// failure part-way through can never leave a partial file at the caller's
// destination.
std::vector<unsigned char> renderBytes(const grr::Figure &fig, int w, int h,
                                       const std::string &format) {
  grr::Size sz = grr::normalizeSize(w, h);
  std::string path = tempFile(format);
  TempGuard guard(path);
  std::remove(path.c_str());
  grr::renderToFile(fig, sz.w, sz.h, path);
  return grr::detail::readAll(path);
}

std::string numToJson(double d) {
  if (d > -9.0e15 && d < 9.0e15 && d == static_cast<double>(static_cast<long long>(d)))
    return std::to_string(static_cast<long long>(d));
  char buf[40];
  std::snprintf(buf, sizeof buf, "%.17g", d);
  return std::string(buf);
}

int optInt(const bj::Value &req, const char *key, int fallback) {
  const bj::Value *v = req.get(key);
  if (!v || v->isNull()) return fallback;
  if (!v->is(bj::Type::Number)) throw bj::Error(std::string(key) + ": expected a number");
  return int(v->number);
}

// ---- video mode ------------------------------------------------------------
//
// One figure spec per LINE (the same shape serve reads, minus the request
// envelope), one frame each, in order.  Streaming rather than a JSON array of
// specs is what keeps memory flat: a day of radar is 143 figures of ~1 MB, and
// the caller is already producing them a line at a time.
//
// Frame geometry is fixed for the whole file -- a video has ONE size -- so
// --width/--height are session-level here, unlike serve's per-request pair.
int video(const std::string &out, int w, int h, int fps) {
  plat::binaryStream(stdin);
  grr::Size sz = grr::normalizeSize(w, h);

  // Written through a temp file for the same reason stills are: a stream that
  // fails on frame 100 must not leave a truncated movie at the destination.
  std::string tmp = tempFile(extensionOf(out));
  TempGuard guard(tmp);
  std::remove(tmp.c_str());

  long n = 0;
  {
    grr::VideoSession session(tmp, sz.w, sz.h, fps);
    std::string line;
    while (std::getline(std::cin, line)) {
      while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();
      if (line.find_first_not_of(" \t") == std::string::npos) continue;
      try {
        session.frame(grr::readFigure(bj::parse(line)));
      } catch (const std::exception &e) {
        // Unlike serve, a bad frame is FATAL: frames are positional, so
        // skipping one silently would shift every timestamp after it.
        throw std::runtime_error("frame " + std::to_string(n + 1) + ": " + e.what());
      }
      ++n;
    }
    if (n == 0) throw std::runtime_error("no figure specs on stdin (one per line)");
    session.finish();
  }

  std::vector<unsigned char> bytes = grr::detail::readAll(tmp);
  FILE *f = std::fopen(out.c_str(), "wb");
  if (!f) throw std::runtime_error("cannot open --out for writing: " + out);
  std::size_t wrote = std::fwrite(bytes.data(), 1, bytes.size(), f);
  bool ok = wrote == bytes.size();
  if (std::fclose(f) != 0) ok = false;
  if (!ok) {
    std::remove(out.c_str());
    throw std::runtime_error("short write to " + out);
  }
  std::fprintf(stderr, "gr-render: wrote %s (%ld frames, %dx%d @ %d fps)\n", out.c_str(), n, sz.w,
               sz.h, fps);
  return 0;
}

// ---- serve mode ------------------------------------------------------------

int serve() {
  plat::binaryStream(stdin);
  std::string line;
  while (std::getline(std::cin, line)) {
    while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();
    if (line.empty()) continue;
    std::string id = "0";
    try {
      bj::Value req = bj::parse(line);
      if (!req.is(bj::Type::Object)) throw bj::Error("request must be a JSON object");
      const bj::Value *idv = req.get("id");
      if (idv && idv->is(bj::Type::Number)) id = numToJson(idv->number);
      const bj::Value *cmdv = req.get("cmd");
      if (!cmdv || !cmdv->is(bj::Type::String)) throw bj::Error("request has no 'cmd' string");
      const std::string &cmd = cmdv->text;

      if (cmd == "shutdown") {
        std::fflush(g_out);
        return 0;
      }
      if (cmd == "ping") {
        writeLine("{\"id\":" + id + ",\"ok\":true}");
        continue;
      }
      if (cmd != "render") throw bj::Error("unknown cmd '" + cmd + "'");

      const bj::Value *spec = req.get("spec");
      if (!spec) throw bj::Error("render: 'spec' is required");
      std::string format = "png";
      const bj::Value *fv = req.get("format");
      if (fv && fv->is(bj::Type::String)) format = fv->text;
      if (!validFormat(format)) throw bj::Error("unsupported format '" + format + "'");
      int w = optInt(req, "width", 800);
      int h = optInt(req, "height", 600);

      grr::Figure fig = grr::readFigure(*spec);
      std::vector<unsigned char> bytes = renderBytes(fig, w, h, format);
      writeLine("{\"id\":" + id + ",\"ok\":true,\"format\":\"" + format + "\",\"data\":\"" +
                grr::base64(bytes) + "\"}");
    } catch (const std::exception &e) {
      // A bad request, or a render that blew up, must not take the loop down.
      writeLine("{\"id\":" + id + ",\"ok\":false,\"error\":\"" + bj::escape(e.what()) + "\"}");
    }
  }
  return 0;  // EOF on stdin
}

// ---- one-shot mode ---------------------------------------------------------

std::string readStdin() {
  plat::binaryStream(stdin);
  std::string all;
  char buf[65536];
  std::size_t n;
  while ((n = std::fread(buf, 1, sizeof buf, stdin)) > 0) all.append(buf, n);
  return all;
}

void usage(FILE *f) {
  std::fprintf(f,
               "gr-render -- plotly figure JSON (stdin) -> a GR-rendered image\n"
               "\n"
               "  gr-render --out PATH [--width N] [--height N] [--format png|svg|pdf]\n"
               "  gr-render --serve\n"
               "  gr-render --video --out PATH [--width N] [--height N] [--fps N]\n"
               "\n"
               "  --out PATH     destination file; its extension picks the default format\n"
               "  --width N      pixels (default 800; odd values round down to even)\n"
               "  --height N     pixels (default 600; odd values round down to even)\n"
               "  --format F     png (default), svg or pdf\n"
               "  --serve        NDJSON request/response loop on stdin/stdout\n"
               "  --video        one figure spec per stdin LINE -> one frame each;\n"
               "                 --out extension picks the container (mp4, webm, ogg, gif)\n"
               "  --fps N        video frame rate (default 12)\n"
               "\n"
               "Requires GRDIR to point at the GR root, with $GRDIR/bin on PATH.\n");
}

}  // namespace

int main(int argc, char **argv) {
  captureStdout();
  std::string out, format;
  int width = 800, height = 600, fps = 12;
  bool serveMode = false, videoMode = false;

  try {
    for (int i = 1; i < argc; ++i) {
      std::string a = argv[i];
      auto next = [&](const char *what) -> std::string {
        if (i + 1 >= argc) throw std::runtime_error(std::string(what) + " needs a value");
        return argv[++i];
      };
      if (a == "--serve") {
        serveMode = true;
      } else if (a == "--video") {
        videoMode = true;
      } else if (a == "--fps") {
        fps = std::atoi(next("--fps").c_str());
      } else if (a == "--out" || a == "-o") {
        out = next("--out");
      } else if (a == "--width") {
        width = std::atoi(next("--width").c_str());
      } else if (a == "--height") {
        height = std::atoi(next("--height").c_str());
      } else if (a == "--format") {
        format = next("--format");
      } else if (a == "--help" || a == "-h") {
        usage(g_out);
        return 0;
      } else {
        throw std::runtime_error("unknown argument '" + a + "'");
      }
    }

    setupEnv();

    if (serveMode) {
      if (videoMode) throw std::runtime_error("--serve and --video are mutually exclusive");
      if (!out.empty()) throw std::runtime_error("--serve and --out are mutually exclusive");
      return serve();
    }

    if (videoMode) {
      if (out.empty()) throw std::runtime_error("--video needs --out PATH");
      if (!format.empty())
        throw std::runtime_error("--format is for stills; a video's container comes from --out");
      std::string ext = extensionOf(out);
      if (!validVideoFormat(ext))
        throw std::runtime_error("unsupported video container '" + ext +
                                 "' (--out must end in .mp4, .webm, .ogg or .gif)");
      if (width <= 0 || height <= 0) throw std::runtime_error("--width/--height must be positive");
      if (fps <= 0 || fps > 240) throw std::runtime_error("--fps must be between 1 and 240");
      return video(out, width, height, fps);
    }

    if (out.empty()) throw std::runtime_error("--out PATH is required (or use --serve)");
    if (format.empty()) {
      std::string ext = extensionOf(out);
      format = validFormat(ext) ? ext : std::string("png");
    }
    if (!validFormat(format)) throw std::runtime_error("unsupported format '" + format + "'");
    if (width <= 0 || height <= 0) throw std::runtime_error("--width/--height must be positive");

    std::string text = readStdin();
    if (text.find_first_not_of(" \t\r\n") == std::string::npos)
      throw std::runtime_error("no figure JSON on stdin");

    grr::Figure fig = grr::readFigure(bj::parse(text));
    std::vector<unsigned char> bytes = renderBytes(fig, width, height, format);

    FILE *f = std::fopen(out.c_str(), "wb");
    if (!f) throw std::runtime_error("cannot open --out for writing: " + out);
    std::size_t wrote = std::fwrite(bytes.data(), 1, bytes.size(), f);
    bool ok = wrote == bytes.size();
    if (std::fclose(f) != 0) ok = false;
    if (!ok) {
      std::remove(out.c_str());
      throw std::runtime_error("short write to " + out);
    }
    return 0;
  } catch (const std::exception &e) {
    // The destination is only ever opened after a successful render, so there
    // is nothing partial to clean up here (and an unrelated pre-existing file
    // at that path must not be destroyed by a spec error).
    std::fprintf(stderr, "gr-render: %s\n", e.what());
    return 1;
  }
}
