// The `blade ide serve` wire protocol, transcribed from src/IdeServe.fs
// (request dispatch at :288-357, the eval response emitter at :146-185).
//
// NDJSON over stdin/stdout: one JSON object per line, UTF-8, no BOM, LF
// (IdeServe.fs:200-203 writes '\n' explicitly rather than WriteLine, which
// would emit CRLF on Windows). There is NO BANNER — the process says nothing
// until it is asked something, so the capability probe is a `ping`.
//
// Three properties that shape every client:
//
//   1. Requests are handled STRICTLY SERIALLY compiler-side (Ast.synthSpan is
//      a mutable global). Pipelining is allowed on the wire; it just won't buy
//      concurrency.
//   2. Error responses may carry `id: null` — a request that failed to parse,
//      or one that omitted its own id, has no id to echo. Correlation by id
//      alone therefore cannot settle every request: clients need TIMEOUTS.
//   3. An unknown command is answered, not rejected:
//      `{"id":N,"error":"unknown cmd 'x'"}`. That is a CONTRACT, and it is how
//      you probe for a verb a given compiler may not have.

import { CheckPayload, CellWindow, Diagnostic } from "./check";

export type Tier = "fast" | "full";
export type Lane = "interp" | "gpp";

/** Capability probe. The only command whose response identifies the process. */
export interface PingRequest {
  id: number;
  cmd: "ping";
}

/** Check one buffer. `file` need not exist (an unsaved buffer is fine) — the
 *  compiler chdirs to its directory so provider-relative data paths resolve
 *  the way a one-shot `ide check` would. */
export interface CheckRequest {
  id: number;
  cmd: "check";
  file: string;
  source: string;
  /** Defaults to "fast" when absent. Any other value is an error response. */
  tier?: Tier;
}

/** Check a whole notebook. `cells` is the ordered source of every CODE cell;
 *  the COMPILER assembles them into one session source
 *  (ReplSession.assembleCells: rebind-in-place, bare-expression wrapping), so
 *  no client reimplements REPL session semantics. Stateless — there is no
 *  `session` field, the whole notebook travels in the request. An EMPTY cells
 *  array is legitimate; a MISSING one is a malformed request. */
export interface CheckCellsRequest {
  id: number;
  cmd: "checkCells";
  file: string;
  cells: string[];
  tier?: Tier;
}

/** Evaluate `source` as the next submission in REPL session `session`
 *  (created on first use; append or rebind-in-place by top-level name). */
export interface EvalRequest {
  id: number;
  cmd: "eval";
  session: string;
  source: string;
  /** Directory that relative data paths in the snippet resolve against. */
  cwd?: string;
}

/** Discard a session's accumulated bindings ("restart kernel"). Idempotent:
 *  an unknown session key simply has no state to clear. */
export interface ResetSessionRequest {
  id: number;
  cmd: "resetSession";
  session: string;
}

/** Dump the compiler's language surface (see ./surface.d.ts).
 *
 *  NOTE: this verb is newer than the rest of the protocol. A compiler that
 *  predates it answers `{"id":N,"error":"unknown cmd 'surface'"}`, which is
 *  precisely how a client detects it. */
export interface SurfaceRequest {
  id: number;
  cmd: "surface";
}

/** Re-render an already-emitted figure as a static image through the
 *  compiler's GR worker. POST-HOC: `spec` is the figure JSON the client
 *  retained (the same `{data, layout}` its plotly frame carried), so nothing
 *  re-runs and the program that produced it need not still exist.
 *
 *  NOTE: newer than the rest of the protocol, like `surface`. A compiler
 *  predating it answers `{"id":N,"error":"unknown cmd 'renderPlot'"}` — the
 *  capability probe. A compiler that HAS it but cannot find the `gr-render`
 *  helper, or has no `GRDIR`, answers with an ordinary error naming that. */
export interface RenderPlotRequest {
  id: number;
  cmd: "renderPlot";
  /** The backend-neutral figure object: `{data: [...], layout: {...}}`. */
  spec: object;
  /** The original frame's `meta.id`, echoed into the response frame's meta.
   *  Supplying it is what makes a panel MERGE the render into that plot rather
   *  than appending a second entry. */
  plotId?: string;
  /** Pixels. Default 800x600, clamped to [64..4096]. A present-but-
   *  non-integer value is an error, not a silent default. */
  width?: number;
  height?: number;
  /** Default "png". */
  format?: "png" | "svg" | "pdf";
}

/** Stop the loop. No id, and NO RESPONSE — the process just exits, so a
 *  client's only confirmation is the child's exit. Closing stdin (EOF) is an
 *  equally clean shutdown. */
export interface ShutdownRequest {
  cmd: "shutdown";
}

export type ServeRequest =
  | PingRequest
  | CheckRequest
  | CheckCellsRequest
  | EvalRequest
  | ResetSessionRequest
  | SurfaceRequest
  | RenderPlotRequest
  | ShutdownRequest;

/** `serve` is the protocol revision (1 today); `version` is the compiler's own
 *  version string. IdeServe.fs:301. */
export interface PingResponse {
  id: number;
  ok: true;
  serve: number;
  version: string;
}

/** A `check` response is the check payload with `id` and `tier` filled in. */
export type CheckResponse = CheckPayload & { id: number; tier: Tier };

/** A `checkCells` response additionally carries one window per input cell. */
export type CheckCellsResponse = CheckResponse & { windows: CellWindow[] };

/** One value the submission left bound, already rendered for display. */
export interface EvalBinding {
  name: string;
  type: string;
  value: string;
}

/** A rich MIME output (a plot). Specified in docs/display-frames.md and parsed
 *  by this package's `display` module. `encoding` defaults by mime when
 *  absent: JSON-shaped mimes -> "json", text/* -> "utf8", else "base64". */
export interface DisplayFrame {
  /** Envelope version. A frame declaring a HIGHER version is rejected by the
   *  parser (it degrades to text) rather than guessed at. */
  v?: number;
  mime: string;
  encoding?: "json" | "utf8" | "base64";
  /** An inline JSON value for encoding "json", otherwise a string. */
  data: unknown;
  meta?: Record<string, unknown>;
}

/** The `eval` response. src/IdeServe.fs:146-185. */
export interface EvalResponse {
  id: number;
  /** Did the submission join the session, or was it rejected? */
  kept: boolean;
  exitCode: number;
  /** Which lane ran it: the interpreter, or the g++ fallback (slow — size
   *  your timeouts for it). */
  lane: Lane;
  elapsedMs: number;
  stdout: string;
  stderr: string;
  bindings: EvalBinding[];
  diagnostics: Diagnostic[];
  /** Emitted ONLY when non-empty, so a program that never plots produces a
   *  response byte-identical to one from a compiler predating display frames.
   *  Frames are spliced in as raw JSON, not escaped strings. */
  display?: DisplayFrame[];
}

/** The `resetSession` response. */
export interface OkResponse {
  id: number;
  ok: true;
}

/** The `surface` response: the surface document with an `id` added. */
export type SurfaceResponse = { id: number } & import("./surface").SurfaceJson;

/** The `renderPlot` response: one complete display frame, so a client feeds it
 *  to the SAME decode/publish path an eval's frames take. `meta.backend` is
 *  always "gr"; `meta.id` is present exactly when the request carried a
 *  `plotId`. `data` is base64 for all three formats (`image/svg+xml` is
 *  neither `text/*` nor `+json`, so the frame format calls it binary). */
export interface RenderPlotResponse {
  id: number;
  frame: DisplayFrame;
}

/** Any command can fail this way. `id` is null when the request could not be
 *  parsed, or carried no id of its own to echo — which is exactly why a client
 *  cannot rely on id correlation alone. */
export interface ErrorResponse {
  id: number | null;
  error: string;
}

export type ServeResponse =
  | PingResponse
  | CheckResponse
  | CheckCellsResponse
  | EvalResponse
  | OkResponse
  | SurfaceResponse
  | RenderPlotResponse
  | ErrorResponse;

/** An UNSOLICITED line: streamed display frames from an eval still in flight.
 *
 *  A line carrying `event` is NEVER a response. It can repeat an in-flight
 *  request's id and must not settle it — a client checks for `event` BEFORE
 *  the id lookup, or a mid-eval plot will resolve the eval early with a
 *  payload that has no stdout and no bindings. */
export interface DisplayEvent {
  event: "display";
  id?: number;
  frame: DisplayFrame;
}
