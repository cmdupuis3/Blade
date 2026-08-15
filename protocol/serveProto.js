// Wire protocol for the compiler's `blade ide serve` subcommand, factored out
// of the VS Code layer so scripts/serve-protocol-test.js can drive the SAME
// framing logic against a real compiler process, and so src/serve.js (the
// process owner) stays free of parsing detail. Zero dependencies, no vscode
// require — everything here is pure string/JSON work. Modeled on
// src/replProto.js's dependency-free split, for the identical reason: the
// live protocol test needs this module without pulling in VS Code.
//
// NDJSON over stdin/stdout: one JSON object per line, UTF-8. Unlike the REPL
// (a terminal-shaped prompt/echo protocol), `ide serve` is a plain data pipe
// — requests and responses correlate by an integer "id" (ping, check,
// checkCells, eval, resetSession; shutdown has no response and no id). See the
// plan's frozen protocol spec for the exact message shapes:
//
//   request  {"id": N, "cmd": "check", "tier": "fast"|"full", "file": "...", "source": "..."}
//   request  {"id": N, "cmd": "ping"}
//   request  {"id": N, "cmd": "checkCells", "file": "...", "cells": ["...", ...], "tier": "fast"|"full"}
//   request  {"id": N, "cmd": "eval", "session": "...", "source": "...", "cwd": "..."}  (cwd optional)
//   request  {"id": N, "cmd": "resetSession", "session": "..."}
//   request  {"cmd": "shutdown"}
//   response {"id": N, "ok": true, "serve": 1, "version": "..."}          (ping)
//   response {"id": N, "tier": "fast"|"full", "diagnostics": [...], ...}  (check)
//   response {"id": N, "tier": "...", "diagnostics": [...], ...,
//             "windows": [{"startLine": S, "endLine": E,
//                          "wrapLine": L, "wrapCol": C}, ...]}            (checkCells)
//   response {"id": N, "kept": true|false, "exitCode": E, "lane": "interp"|"gpp",
//             "elapsedMs": M, "stdout": "...", "stderr": "...",
//             "bindings": [...], "diagnostics": [...]}                    (eval)
//   response {"id": N, "ok": true}                                        (resetSession)
//   response {"id": N|null, "error": "..."}                                (error)
//   event    {"event": "display", "id": N, "frame": {...}}                 (unsolicited)
//
// An eval response may additionally carry "display": [frame, ...] — rich
// MIME outputs (plots) produced by that submission. A line carrying "event"
// is never a response: it can repeat an in-flight request's id and must not
// settle it (src/serve.js checks for "event" before the id lookup). Both are
// specified in docs/display-frames.md and parsed by src/display.js; a
// compiler that emits neither is unaffected.
//
// A compiler that predates notebook support answers "eval"/"resetSession"/
// "checkCells" with the generic {"id", "error": "..."} shape (unknown cmd)
// rather than rejecting the connection — src/serve.js tags that case (see
// handleStdout's `protocolError`) so callers can tell "unsupported command"
// apart from a transport failure (timeout, crash, not found).
//
// stderr is free-form compiler logging (never JSON, never parsed here) —
// src/serve.js routes it straight to the "Blade" output channel.

"use strict";

/** One "check" request line: {"id","cmd":"check","tier","file","source"}\n */
function encodeCheck(id, tier, file, source) {
  return JSON.stringify({ id, cmd: "check", tier, file, source }) + "\n";
}

/**
 * One "checkCells" request line: {"id","cmd":"checkCells","file","cells","tier"}\n
 * — `cells` is the ordered source text of every CODE cell of a notebook
 * (markdown cells are the caller's to filter out). The compiler assembles them
 * into ONE session source itself (ReplSession.assembleCells: rebind-in-place,
 * bare-expression wrapping) and checks that, so the extension never has to
 * reimplement REPL session semantics. Stateless — no "session" field; the
 * whole notebook is in the request. The response is a normal check payload
 * plus a `windows` entry per input cell saying where that cell's text landed
 * (see the header, and src/notebook.js's fan-out).
 */
function encodeCheckCells(id, tier, file, cells) {
  return JSON.stringify({ id, cmd: "checkCells", file, cells, tier }) + "\n";
}

/** One "ping" request line: {"id","cmd":"ping"}\n */
function encodePing(id) {
  return JSON.stringify({ id, cmd: "ping" }) + "\n";
}

/** One "eval" request line: {"id","cmd":"eval","session","source"[,"cwd"]}\n
 *  — evaluate `source` as the next submission in the named REPL session
 *  (append, or rebind-in-place by top-level name), same semantics as one
 *  `blade repl` submission. `cwd` (optional) is the directory relative data
 *  paths in the snippet resolve against (the notebook file's directory). */
function encodeEval(id, session, source, cwd) {
  const req = { id, cmd: "eval", session, source };
  if (cwd) req.cwd = cwd;
  return JSON.stringify(req) + "\n";
}

/** One "resetSession" request line: {"id","cmd":"resetSession","session"}\n
 *  — discard the named session's accumulated bindings (Restart Kernel). */
function encodeResetSession(id, session) {
  return JSON.stringify({ id, cmd: "resetSession", session }) + "\n";
}

/** The "shutdown" request line: {"cmd":"shutdown"}\n — no id, no response. */
function encodeShutdown() {
  return JSON.stringify({ cmd: "shutdown" }) + "\n";
}

/**
 * Parse one already-newline-stripped line into a message object. A line that
 * isn't valid JSON, or is valid JSON but not an object (e.g. a bare number,
 * or stray compiler output that leaked onto stdout), decodes to an error
 * object shaped like the protocol's own error responses (`{id: null, error}`)
 * instead of throwing — the caller can route it straight to the output
 * channel exactly like a real `{"error": "..."}` response.
 */
function decodeLine(line) {
  let obj;
  try {
    obj = JSON.parse(line);
  } catch (e) {
    return { id: null, error: `malformed JSON from 'ide serve': ${e.message} — ${line.slice(0, 200)}` };
  }
  if (!obj || typeof obj !== "object" || Array.isArray(obj)) {
    return { id: null, error: `non-object JSON line from 'ide serve': ${line.slice(0, 200)}` };
  }
  return obj;
}

/**
 * A stateful line decoder for one child process's stdout: feed it chunks as
 * they arrive (`push`), get back the complete messages they contained (zero,
 * one, or several — fast consecutive responses can coalesce into a single
 * `data` event). Tolerates `\r\n` line endings. The trailing partial line (no
 * `\n` yet) is retained across calls until it completes.
 */
function createDecoder() {
  let buf = "";
  return {
    push(chunk) {
      buf += chunk;
      const lines = buf.split("\n");
      buf = lines.pop(); // last element is the trailing partial line (or "")
      const messages = [];
      for (const raw of lines) {
        const line = raw.endsWith("\r") ? raw.slice(0, -1) : raw;
        if (line.trim() === "") continue;
        messages.push(decodeLine(line));
      }
      return messages;
    },
  };
}

module.exports = {
  encodeCheck,
  encodeCheckCells,
  encodePing,
  encodeEval,
  encodeResetSession,
  encodeShutdown,
  decodeLine,
  createDecoder,
};
