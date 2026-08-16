// gr-render -- translate a plotly figure spec into a GR-rendered image.
//
//   one-shot:  gr-render --out PATH [--width N] [--height N] [--format png|svg|pdf]
//              (the figure JSON arrives on stdin)
//   serve:     gr-render --serve
//              (NDJSON request per line on stdin, one response line per request)
//
// See README.md for the full contract.  Every GR-touching detail worth knowing
// is commented in render.hpp.
#include <fcntl.h>
#include <io.h>
#include <process.h>

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

FILE *g_out = nullptr;  // the REAL stdout; fd 1 is rewired to stderr
long g_counter = 0;

// GR (and the plugins it loads) occasionally print diagnostics on stdout.  In
// serve mode stdout carries NDJSON only, so fd 1 is pointed at stderr and the
// original handle is kept privately for responses.
void captureStdout() {
  int saved = _dup(1);
  if (saved >= 0) {
    _dup2(2, 1);
    _setmode(saved, _O_BINARY);
    g_out = _fdopen(saved, "wb");
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
  _putenv("GKS_WSTYPE=100");
  _putenv("GR_DISPLAY=");
  const char *grdir = std::getenv("GRDIR");
  if (!grdir || !*grdir)
    throw std::runtime_error(
        "GRDIR is not set; point it at the GR install root (its bin/ must also "
        "be on PATH) -- without it GR dies with an access violation");
}

std::string tempDir() {
  const char *t = std::getenv("TEMP");
  if (!t || !*t) t = std::getenv("TMP");
  if (!t || !*t) t = ".";
  return std::string(t);
}

std::string tempFile(const std::string &ext) {
  return tempDir() + "\\gr-render-" + std::to_string(_getpid()) + "-" +
         std::to_string(++g_counter) + "." + ext;
}

struct TempGuard {
  std::string path;
  explicit TempGuard(std::string p) : path(std::move(p)) {}
  ~TempGuard() { std::remove(path.c_str()); }
};

bool validFormat(const std::string &f) { return f == "png" || f == "svg" || f == "pdf"; }

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

// ---- serve mode ------------------------------------------------------------

int serve() {
  _setmode(_fileno(stdin), _O_BINARY);
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
  _setmode(_fileno(stdin), _O_BINARY);
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
               "\n"
               "  --out PATH     destination file; its extension picks the default format\n"
               "  --width N      pixels (default 800; odd values round down to even)\n"
               "  --height N     pixels (default 600; odd values round down to even)\n"
               "  --format F     png (default), svg or pdf\n"
               "  --serve        NDJSON request/response loop on stdin/stdout\n"
               "\n"
               "Requires GRDIR to point at the GR root, with $GRDIR/bin on PATH.\n");
}

}  // namespace

int main(int argc, char **argv) {
  captureStdout();
  std::string out, format;
  int width = 800, height = 600;
  bool serveMode = false;

  try {
    for (int i = 1; i < argc; ++i) {
      std::string a = argv[i];
      auto next = [&](const char *what) -> std::string {
        if (i + 1 >= argc) throw std::runtime_error(std::string(what) + " needs a value");
        return argv[++i];
      };
      if (a == "--serve") {
        serveMode = true;
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
      if (!out.empty()) throw std::runtime_error("--serve and --out are mutually exclusive");
      return serve();
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
