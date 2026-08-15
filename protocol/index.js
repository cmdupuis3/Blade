// @blade-lang/ide-protocol — the Blade compiler's IDE-facing protocol, as a
// package. It lives in the compiler repo, beside src/IdeServe.fs and
// src/Ide.fs, because that is the only place a protocol mirror can be kept
// honest: every consumer (the VS Code extension, the MCP server) now reads
// ONE copy of the framing, the client, and the language surface instead of
// maintaining its own.
//
// Everything here is plain CommonJS with zero dependencies.
//
//   serveProto        NDJSON framing for `blade ide serve` (encode/decode)
//   replProto         framing for the terminal-shaped `blade repl`
//   display           the display-frame parser and its publish/subscribe hub
//   createClient      a whole `ide serve` child process: spawn, ping probe,
//                     id correlation, timeouts, backoff, dispose
//   resolveCompiler   which Blade binary to run, and why
//   resolveRepoRoot   the source checkout, when there is one
//
// Two exports are GENERATED/AUTHORED DATA rather than code, and both are
// loaded lazily (see the getters below) so that this module parses and
// requires cleanly in a tree where they have not been produced yet:
//
//   surface           surface.json — the language surface dumped by
//                     `blade ide surface`; regenerate with
//                     `npm run generate-surface`
//   diagnosticsKb     data/diagnostics.json — the hand-authored BLxxxx
//                     knowledge base (explanations, fixes, example paths)
//
// A note for consumers of `display`: its routing hub is module-level state,
// so publish/subscribe only connect when every participant requires the SAME
// module instance. Requiring both this package's `display` and a private copy
// silently splits the channel.

"use strict";

const serveProto = require("./serveProto");
const replProto = require("./replProto");
const display = require("./display");
const { createClient } = require("./client");
const { resolveCompiler, resolveRepoRoot, DEFAULT_CANDIDATES } = require("./resolveCompiler");

module.exports = {
  serveProto,
  replProto,
  display,
  createClient,
  resolveCompiler,
  resolveRepoRoot,
  DEFAULT_CANDIDATES,
};

/** Define a lazily-required JSON export. Deferred because these two files are
 *  build/authoring artifacts, not source: requiring them eagerly would make
 *  the whole package unloadable in a checkout where they are absent, which is
 *  exactly the state a `generate-surface` run is meant to fix. Memoized, so a
 *  present file is parsed once; a missing one reports what to do about it
 *  instead of a bare MODULE_NOT_FOUND from a path the caller never wrote. */
function defineLazyJson(name, request, remedy) {
  let cached;
  Object.defineProperty(module.exports, name, {
    enumerable: true,
    configurable: true,
    get() {
      if (cached === undefined) {
        try {
          cached = require(request);
        } catch (e) {
          throw new Error(
            `@blade-lang/ide-protocol: cannot load ${request} — ${remedy} (${e.message})`
          );
        }
      }
      return cached;
    },
  });
}

defineLazyJson(
  "surface",
  "./surface.json",
  "regenerate it with `npm run generate-surface` (needs a Blade compiler that has the `ide surface` verb)"
);

defineLazyJson(
  "diagnosticsKb",
  "./data/diagnostics.json",
  "this install has no diagnostics knowledge base; consumers should degrade to surface.diagnostics for titles"
);
