// Wire protocol for the compiler's `blade repl` (Cli.fs replLoop), factored
// out of the VS Code layer so scripts/repl-protocol-test.js can drive the
// SAME framing logic against a real compiler process. Zero dependencies,
// no vscode require — everything here is pure string work.
//
// The child writes a prompt (Console.Write + Flush, no trailing newline)
// whenever it is ready to read a line: PROMPT at the top level, CONT while a
// multi-line buffer / :paste block is open. Because the REPL prints ALL of a
// submission's output between consuming its last input line and printing the
// next top-level prompt, "stdout buffer ends with PROMPT" is a reliable frame
// terminator: everything accumulated since the previous frame belongs to the
// current submission.

"use strict";

const PROMPT = "blade> ";
const CONT = "  ... ";

// stderr lines that are commentary, not failures: the interpreter's one-line
// g++ fallback notice and positionless typecheck warnings.
const FALLBACK_NOTICE = "-- falling back to compiled evaluation";
const NOISE_RE = /^(-- falling back to compiled evaluation|\[TypeCheck Warning\])/;

/**
 * Encode one editor submission. Always :paste-framed — even single lines —
 * so a snippet with unbalanced brackets is evaluated (and rejected) instead
 * of leaving the child waiting at a continuation prompt with the frame open
 * forever. `:paste` is only recognized at the top level, which is guaranteed
 * because submissions are strictly serialized (one frame in flight at a time).
 */
function wireFor(code) {
  const body = code.replace(/\r\n/g, "\n").replace(/\n+$/, "");
  return ":paste\n" + body + "\n:end\n";
}

/** Does the stdout buffer end at a top-level prompt (= frame complete)? */
function frameDone(buf) {
  return buf.endsWith(PROMPT);
}

/**
 * A completed frame's stdout, minus protocol noise. The child echoes one CONT
 * prompt per line it read inside the :paste block, all BEFORE any evaluation
 * output (every read happens before evaluate runs), so they form a contiguous
 * prefix. The trailing PROMPT must already be sliced off by the caller.
 */
function cleanFrame(frame) {
  let f = frame;
  while (f.startsWith(CONT)) f = f.slice(CONT.length);
  return f;
}

/** stderr with commentary lines (fallback notice, warnings) removed. */
function significantErr(err) {
  return err
    .split(/\r?\n/)
    .filter((l) => l.trim() && !NOISE_RE.test(l.trim()))
    .join("\n");
}

/**
 * Did this submission fail? A rejected snippet always carries the
 * "[snippet not kept]" / "[exit N — snippet not kept]" marker on stderr;
 * empty stdout with non-commentary stderr covers process-level failures
 * (spawn/runtime errors reported by the client itself).
 */
function isErrorFrame(out, err) {
  if (err.includes("snippet not kept")) return true;
  return out.trim() === "" && significantErr(err) !== "";
}

/** Did the interpreter punt this input to the g++ lane? */
function fellBack(err) {
  return err.includes(FALLBACK_NOTICE);
}

/**
 * One-line summary for inline display. Value echoes are either one line
 * (`a = Int64: 5`) or value + tabbed type/signature line — the pair joins
 * with an em-dash (`v = [1, 2, 3] — Array<Int64, Idx<3>>`). Failures
 * summarize as the first non-commentary stderr line.
 */
function summarize(out, err) {
  const lines = out.split(/\r?\n/).filter((l) => l.trim() !== "");
  if (lines.length > 0) {
    let s = lines[0].trim();
    if (lines.length > 1 && /^\t/.test(lines[1])) s += " — " + lines[1].trim();
    return s.replace(/\s+/g, " ");
  }
  const sig = significantErr(err);
  if (sig) return sig.split(/\r?\n/)[0].trim();
  return "(no output)";
}

function ellipsize(s, max) {
  return s.length > max ? s.slice(0, max - 1) + "…" : s;
}

module.exports = {
  PROMPT,
  CONT,
  FALLBACK_NOTICE,
  wireFor,
  frameDone,
  cleanFrame,
  significantErr,
  isErrorFrame,
  fellBack,
  summarize,
  ellipsize,
};
