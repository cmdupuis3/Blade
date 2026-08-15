// Where is the Blade compiler? One implementation of a rule that had been
// copy-pasted five times across the VS Code extension and its scripts, each
// copy drifting on precedence and on whether "nothing local, fell through to
// PATH" was distinguishable from a real hit.
//
// It is distinguishable here: every resolution carries an `origin`, because
// the callers genuinely differ. A script that must SKIP rather than fail when
// no compiler was built checks `origin === "path"`; an MCP server reports the
// origin in its doctor output so a user can see WHICH binary answered.
//
// The newest-mtime rule among the candidates is not a tiebreak detail — it is
// the point. A stale Release build sitting next to a fresh Debug build would
// otherwise report errors the compiler no longer produces, which is why
// `findCompiler` is called at every spawn (see ./client.js) rather than once.
//
// Zero dependencies, no host coupling: `env` is injectable so a test can pin
// it, and nothing here reads a VS Code setting — a host passes its setting in
// as `explicitPath`.

"use strict";

const fs = require("fs");
const path = require("path");

/**
 * The in-repo build outputs, resolved RELATIVE to this file (`protocol/..` is
 * the repo root) rather than hardcoded to one machine's absolute paths.
 *
 * Two consequences worth knowing:
 *   - Installed as a dependency (node_modules/@blade-lang/ide-protocol), `..`
 *     is the scope directory, these paths cannot exist, and resolution
 *     correctly falls through to `explicitPath` / BLADE_EXE / PATH.
 *   - The names are Windows-shaped (`Blade.exe`), matching the layout `dotnet
 *     build` produces on the development platform. On other platforms the
 *     candidates simply miss and the PATH fallback carries it; pass your own
 *     `candidates` to override.
 */
const DEFAULT_CANDIDATES = [
  path.join(__dirname, "..", "bin", "Release", "net7.0", "Blade.exe"),
  path.join(__dirname, "..", "bin", "Debug", "net7.0", "Blade.exe"),
];

/** Newest-mtime existing regular file among `candidates`, or undefined. A
 *  candidate that doesn't exist (or that we can't stat) is skipped, never
 *  thrown — "not built yet" is the normal case, not an error. */
function newestExisting(candidates) {
  let best;
  for (const c of candidates) {
    try {
      const st = fs.statSync(c);
      if (!st.isFile()) continue;
      if (!best || st.mtimeMs > best.mtimeMs) best = { exe: c, mtimeMs: st.mtimeMs };
    } catch (_) {
      /* candidate doesn't exist */
    }
  }
  return best;
}

/**
 * Resolve the compiler to run. Never throws, never spawns anything — the
 * returned `exe` is a path (or the bare name "Blade", to be found on PATH),
 * and only an actual spawn can tell you whether it runs.
 *
 * Precedence, first hit wins:
 *   1. `explicitPath`      -> origin "explicit"  (a host's own setting/flag)
 *   2. `env.BLADE_EXE`     -> origin "env"       (the existing convention)
 *   3. newest-mtime of `candidates` -> origin "candidate"
 *   4. "Blade"             -> origin "path"      (nothing local was found)
 *
 * @param {object} [options]
 * @param {string} [options.explicitPath] a path the caller was told to use
 * @param {object} [options.env] environment to read (default process.env)
 * @param {string[]} [options.candidates] default DEFAULT_CANDIDATES
 * @returns {{exe: string, origin: "explicit"|"env"|"candidate"|"path"}}
 */
function resolveCompiler(options) {
  const opts = options || {};
  const env = opts.env || process.env;
  if (opts.explicitPath) return { exe: opts.explicitPath, origin: "explicit" };
  if (env.BLADE_EXE) return { exe: env.BLADE_EXE, origin: "env" };
  const best = newestExisting(opts.candidates || DEFAULT_CANDIDATES);
  if (best) return { exe: best.exe, origin: "candidate" };
  return { exe: "Blade", origin: "path" };
}

function isDirectory(p) {
  try {
    return fs.statSync(p).isDirectory();
  } catch (_) {
    return false;
  }
}

/** How far up from the binary we are willing to look for a checkout. A
 *  standard build sits at `<root>/bin/Release/net7.0/`, which is three
 *  parents; the budget leaves room for a variant layout without wandering
 *  into a user's home directory. */
const MAX_WALK_UP = 5;

/**
 * Find the Blade source checkout, if there is one. Used by consumers that
 * want repo-only assets (docs/, examples/) — never required: everything that
 * ships beside the binary (tests/corpus, stdlib) is reachable without this,
 * and callers must degrade gracefully when it returns undefined.
 *
 * `env.BLADE_REPO` wins when it names an existing directory. Otherwise we
 * walk up from the binary's directory looking for a dir that has BOTH
 * `Blade.fsproj` and `docs/formalism.md` — both, because either alone is a
 * plausible coincidence and this result is used to build doc paths.
 *
 * @param {object} [options]
 * @param {string} [options.exe] compiler path (from resolveCompiler().exe)
 * @param {object} [options.env] environment to read (default process.env)
 * @returns {string|undefined}
 */
function resolveRepoRoot(options) {
  const opts = options || {};
  const env = opts.env || process.env;
  if (env.BLADE_REPO && isDirectory(env.BLADE_REPO)) return env.BLADE_REPO;
  if (!opts.exe) return undefined;
  let dir = path.dirname(path.resolve(opts.exe));
  for (let i = 0; i <= MAX_WALK_UP; i++) {
    if (
      fs.existsSync(path.join(dir, "Blade.fsproj")) &&
      fs.existsSync(path.join(dir, "docs", "formalism.md"))
    ) {
      return dir;
    }
    const parent = path.dirname(dir);
    if (parent === dir) break; // filesystem root
    dir = parent;
  }
  return undefined;
}

module.exports = { resolveCompiler, resolveRepoRoot, DEFAULT_CANDIDATES };
