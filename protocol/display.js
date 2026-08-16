// Display frames — the `{mime, data}` rich-output channel the Blade runtime
// uses to ship plots (and any other MIME payload) alongside stdout/stderr.
// The wire format is specified in docs/display-frames.md; this module is its
// ONLY parser, shared by every channel that can carry a frame:
//
//   `blade repl`      stdout, sentinel-prefixed line    -> scanReplOutput
//   `blade ide serve` eval response, `display` array    -> framesFromEval
//   `blade ide serve` out-of-band {"event":"display"}   -> frameFromEvent
//
// Zero dependencies, no `vscode` require — the same split src/replProto.js and
// src/serveProto.js make, and for the same reason: scripts/plots-test.js
// drives the REAL parser, and the parser must not care which channel a frame
// arrived on.
//
// A malformed frame never throws and never breaks its channel. decode returns
// a reason instead of a frame, the caller keeps the payload as plain text, and
// the reason is logged (setLogger) — a plotting bug in the compiler must not
// cost the user their REPL session.
//
// Routing is a one-hub publish/subscribe: producers (src/repl.js,
// src/notebook.js, the plot demo) call route(); src/plots.js is the sole
// subscriber today. Keeping the hub here rather than in plots.js means no
// producer requires the webview module, so nothing drags `vscode` into a
// channel that is otherwise pure string work.

"use strict";

/** Frame envelope version. A frame declaring a HIGHER version is rejected
 *  (degrades to text) rather than guessed at — see decodeFrame. */
const FRAME_VERSION = 1;

/** REPL channel sentinel. The SOH (U+0001) delimiters make the prefix
 *  impossible to produce accidentally from JSON-encoded output: JSON.stringify
 *  escapes control characters inside strings, so a frame's own payload can
 *  never re-open a frame, and ordinary program output does not begin with
 *  SOH. Line-oriented (not an ANSI/OSC escape) because the REPL's own framing
 *  — prompt sentinels, cleanFrame, summarize — is already line-oriented. */
const SENTINEL = "\u0001blade-display\u0001";

/** Hard ceiling on one frame's JSON text. Frames above this are rejected as
 *  malformed: a multi-hundred-megabyte line would be forwarded verbatim into a
 *  webview postMessage and take the extension host with it. Producers should
 *  stay an order of magnitude below (see docs/display-frames.md § Size). */
const MAX_FRAME_CHARS = 32 * 1024 * 1024;

/** How much of an offending payload a rejection reason quotes. */
const REASON_ECHO = 160;

const MIME_RE = /^[A-Za-z0-9][A-Za-z0-9.+_-]*\/[A-Za-z0-9][A-Za-z0-9.+_-]*$/;
const BASE64_RE = /^[A-Za-z0-9+/=\s]+$/;
const ENCODINGS = ["json", "utf8", "base64"];

const PLOTLY_MIME = "application/vnd.plotly.v1+json";
const PNG_MIME = "image/png";

/** Encoding implied by a mime type when the frame omits `encoding`:
 *  JSON-shaped mimes carry an inline JSON value, text/* carries a string,
 *  everything else is binary and must be base64. */
function defaultEncodingFor(mime) {
  if (mime === "application/json" || /\+json$/.test(mime)) return "json";
  if (/^text\//.test(mime)) return "utf8";
  return "base64";
}

/** Which panel backend produced (or can render) a frame. `meta.backend` wins;
 *  otherwise the plotly mime is plotly's and a raster image is the static
 *  render lane, which is GR's lane (docs/display-frames.md § meta.backend). */
function backendFor(frame) {
  if (frame.meta && typeof frame.meta.backend === "string") return frame.meta.backend;
  if (frame.mime === PLOTLY_MIME) return "plotly";
  if (/^image\//.test(frame.mime)) return "gr";
  return "other";
}

function echo(s) {
  const t = String(s);
  return t.length > REASON_ECHO ? t.slice(0, REASON_ECHO - 1) + "…" : t;
}

/**
 * Validate/normalize one already-JSON-parsed frame object. Returns
 * `{ok: true, frame}` with `{v, mime, encoding, data, meta}` fully populated
 * (`meta` defaults to `{}`), or `{ok: false, reason}` — never throws.
 */
function decodeFrame(obj) {
  if (!obj || typeof obj !== "object" || Array.isArray(obj)) {
    return { ok: false, reason: `display frame is not a JSON object: ${echo(JSON.stringify(obj))}` };
  }
  const v = obj.v === undefined ? FRAME_VERSION : obj.v;
  if (typeof v !== "number" || !Number.isFinite(v)) {
    return { ok: false, reason: `display frame "v" is not a number: ${echo(obj.v)}` };
  }
  if (v > FRAME_VERSION) {
    return { ok: false, reason: `display frame version ${v} is newer than this extension understands (${FRAME_VERSION}) — update the Blade extension` };
  }
  if (typeof obj.mime !== "string" || !MIME_RE.test(obj.mime)) {
    return { ok: false, reason: `display frame "mime" is missing or not a mime type: ${echo(obj.mime)}` };
  }
  const encoding = obj.encoding === undefined ? defaultEncodingFor(obj.mime) : obj.encoding;
  if (ENCODINGS.indexOf(encoding) === -1) {
    return { ok: false, reason: `display frame "encoding" must be one of ${ENCODINGS.join("/")}: ${echo(obj.encoding)}` };
  }
  if (obj.data === undefined || obj.data === null) {
    return { ok: false, reason: `display frame "data" is missing (mime ${obj.mime})` };
  }
  if (encoding === "json") {
    if (typeof obj.data !== "object") {
      return { ok: false, reason: `display frame "data" must be an inline JSON object/array for encoding "json" (mime ${obj.mime}), got ${typeof obj.data}` };
    }
  } else {
    if (typeof obj.data !== "string") {
      return { ok: false, reason: `display frame "data" must be a string for encoding "${encoding}" (mime ${obj.mime}), got ${typeof obj.data}` };
    }
    if (encoding === "base64" && !BASE64_RE.test(obj.data)) {
      return { ok: false, reason: `display frame "data" is not base64 (mime ${obj.mime}): ${echo(obj.data)}` };
    }
  }
  if (obj.meta !== undefined && (typeof obj.meta !== "object" || obj.meta === null || Array.isArray(obj.meta))) {
    return { ok: false, reason: `display frame "meta" is not a JSON object: ${echo(JSON.stringify(obj.meta))}` };
  }
  return {
    ok: true,
    frame: { v, mime: obj.mime, encoding, data: obj.data, meta: obj.meta || {} },
  };
}

/** JSON text -> decodeFrame. Rejects (rather than parses) text past
 *  MAX_FRAME_CHARS, and turns a JSON syntax error into a reason. */
function parseFrameJson(text) {
  if (typeof text !== "string") return { ok: false, reason: "display frame payload is not text" };
  if (text.length > MAX_FRAME_CHARS) {
    return { ok: false, reason: `display frame is ${text.length} bytes, over the ${MAX_FRAME_CHARS}-byte limit — decimate server-side` };
  }
  let obj;
  try {
    obj = JSON.parse(text);
  } catch (e) {
    return { ok: false, reason: `malformed display frame JSON: ${e.message} — ${echo(text)}` };
  }
  return decodeFrame(obj);
}

/** One REPL-channel wire line for `frame` (sentinel + compact JSON + "\n").
 *  Used by the plot demo to push a frame through the real parse path, and by
 *  scripts/plots-test.js as the encoder half of the round-trip. */
function encodeReplLine(frame) {
  const f = Object.assign({ v: FRAME_VERSION }, frame);
  return SENTINEL + JSON.stringify(f) + "\n";
}

/**
 * Split a completed REPL stdout frame into the text the terminal should show
 * and the display frames it carried. A sentinel line that fails to decode is
 * NOT dropped: its payload text stays in `text` (minus the sentinel, which is
 * an unprintable control character) so the user sees whatever the compiler
 * meant to send, and the reason lands in `errors`.
 * @returns {{text: string, frames: object[], errors: string[]}}
 */
function scanReplOutput(text) {
  if (typeof text !== "string" || text.indexOf(SENTINEL) === -1) {
    return { text: typeof text === "string" ? text : "", frames: [], errors: [] };
  }
  const kept = [];
  const frames = [];
  const errors = [];
  for (const raw of text.split("\n")) {
    const line = raw.endsWith("\r") ? raw.slice(0, -1) : raw;
    if (!line.startsWith(SENTINEL)) {
      kept.push(raw);
      continue;
    }
    const payload = line.slice(SENTINEL.length);
    const res = parseFrameJson(payload);
    if (res.ok) frames.push(res.frame);
    else {
      errors.push(res.reason);
      kept.push(payload); // degrade to plain text
    }
  }
  return { text: kept.join("\n"), frames, errors };
}

/**
 * A stateful scanner for STREAMED REPL stdout (the interactive path, where
 * output is written to the terminal as it arrives rather than buffered until
 * the frame-terminating prompt). Feed it chunks; get back the text to write
 * plus whatever frames completed.
 *
 * A trailing partial line is only withheld when it could still become a frame
 * line — i.e. it starts with the sentinel, or is a proper prefix of it.
 * Anything else (crucially the `blade> ` prompt, which carries no newline) is
 * emitted immediately, so holding back frames never stalls the prompt.
 */
function createStreamScanner() {
  let buf = "";
  function couldStartSentinel(s) {
    return s.startsWith(SENTINEL) || SENTINEL.startsWith(s);
  }
  return {
    push(chunk) {
      buf += chunk;
      const nl = buf.lastIndexOf("\n");
      let head;
      if (nl === -1) {
        if (couldStartSentinel(buf)) return { text: "", frames: [], errors: [] };
        head = buf;
        buf = "";
        return { text: head, frames: [], errors: [] };
      }
      head = buf.slice(0, nl + 1);
      const tail = buf.slice(nl + 1);
      if (couldStartSentinel(tail) && tail !== "") {
        buf = tail;
      } else {
        head += tail;
        buf = "";
      }
      return scanReplOutput(head);
    },
    /** Give up on a withheld partial line (session end) and emit it as text. */
    flush() {
      const rest = buf;
      buf = "";
      return rest;
    },
  };
}

/**
 * Frames carried by one `ide serve` eval response's `display` array. A
 * response without the field (every compiler predating display frames) yields
 * nothing and no error — the field is additive, its absence is not a failure.
 * @returns {{frames: object[], errors: string[]}}
 */
function framesFromEval(resp) {
  const list = resp && resp.display;
  if (list === undefined || list === null) return { frames: [], errors: [] };
  if (!Array.isArray(list)) {
    return { frames: [], errors: [`eval response "display" is not an array: ${echo(JSON.stringify(list))}`] };
  }
  const frames = [];
  const errors = [];
  for (const entry of list) {
    const res = decodeFrame(entry);
    if (res.ok) frames.push(res.frame);
    else errors.push(res.reason);
  }
  return { frames, errors };
}

/** Is this NDJSON message an out-of-band event rather than a response? The
 *  serve protocol's rule (docs/display-frames.md § serve): a line carrying
 *  "event" is never a response to a request id, so src/serve.js must not
 *  resolve a pending request with it. */
function isEvent(msg) {
  return !!msg && typeof msg === "object" && typeof msg.event === "string";
}

/** Decode a `{"event":"display","frame":{...}}` line's frame. */
function frameFromEvent(msg) {
  if (!isEvent(msg) || msg.event !== "display") {
    return { ok: false, reason: `not a display event: ${echo(JSON.stringify(msg))}` };
  }
  return decodeFrame(msg.frame);
}

// --- Routing hub ---------------------------------------------------------------

/** @type {Array<(frame: object, origin: string) => void>} */
let listeners = [];
/** @type {((line: string) => void) | null} */
let logger = null;

/** Route rejection reasons here (extension.js's "Blade" output channel).
 *  Unset by default so this module stays usable from a plain Node test. */
function setLogger(fn) {
  logger = typeof fn === "function" ? fn : null;
}

function log(line) {
  if (logger) logger(line);
}

/** Subscribe to every routed frame. Returns a vscode-Disposable-shaped object
 *  so callers can push it straight onto context.subscriptions. */
function subscribe(fn) {
  listeners.push(fn);
  return {
    dispose() {
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    },
  };
}

/** Deliver one frame to every subscriber. A throwing subscriber is logged and
 *  skipped — a broken panel must not break the channel that fed it. */
function publish(frame, origin) {
  for (const fn of listeners.slice()) {
    try {
      fn(frame, origin || "unknown");
    } catch (e) {
      log(`[display] subscriber failed on a ${frame.mime} frame: ${e && e.message}`);
    }
  }
}

/**
 * Publish a scan/decode result: every valid frame to the subscribers, every
 * rejection reason to the log. `origin` tags the channel ("repl", "notebook",
 * "demo") in log lines and is passed through to subscribers.
 * @param {{frames: object[], errors: string[]}} result
 */
function route(result, origin) {
  for (const reason of result.errors || []) log(`[display:${origin}] ${reason}`);
  for (const frame of result.frames || []) publish(frame, origin);
  return result;
}

/** Scan REPL stdout, route what it found, and hand back the text the terminal
 *  and the inline decorations should use. The one call src/repl.js makes. */
function ingestReplText(text, origin) {
  const res = scanReplOutput(text);
  route(res, origin || "repl");
  return res.text;
}

module.exports = {
  FRAME_VERSION,
  SENTINEL,
  MAX_FRAME_CHARS,
  PLOTLY_MIME,
  PNG_MIME,
  defaultEncodingFor,
  backendFor,
  decodeFrame,
  parseFrameJson,
  encodeReplLine,
  scanReplOutput,
  createStreamScanner,
  framesFromEval,
  isEvent,
  frameFromEvent,
  setLogger,
  subscribe,
  publish,
  route,
  ingestReplText,
};
