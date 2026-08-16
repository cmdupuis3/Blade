# @blade-lang/ide-protocol

The Blade compiler's IDE-facing protocol, as a package: the NDJSON client for
`blade ide serve`, the payload typings, and the generated language surface.

It lives in the compiler repo, beside `src/IdeServe.fs` and `src/Ide.fs`,
because that is the only place a protocol mirror can be kept honest. Before
this package existed, the VS Code extension hand-maintained copies of the
compiler's keyword, builtin and type lists (with `file:line` citations that
went stale silently — a builtin with a hover but no highlighting was the bug
that motivated this) and duplicated the serve client and binary-discovery logic
five times over.

Plain CommonJS. **Zero dependencies.** No build step.

## Exports

| Export | What it is | Source of truth |
|---|---|---|
| `serveProto` | NDJSON framing for `ide serve` — encoders per command, plus a chunk-tolerant line decoder | `src/IdeServe.fs` |
| `replProto` | Framing for the terminal-shaped `blade repl` (prompt sentinels, `:paste`, frame cleanup) | `src/Cli.fs` `replLoop` |
| `display` | The display-frame parser **and** its publish/subscribe hub | `docs/display-frames.md` |
| `createClient(deps, label?)` | One whole `ide serve` child process: spawn, ping probe, id correlation, timeouts, backoff, dispose | — |
| `resolveCompiler(opts?)` | Which Blade binary to run, and **why** (`{exe, origin}`) | — |
| `resolveRepoRoot(opts?)` | The source checkout, when there is one | — |
| `DEFAULT_CANDIDATES` | In-repo build outputs, resolved relative to this package | — |
| `surface` *(lazy)* | `surface.json` — the language surface | `blade ide surface` |
| `diagnosticsKb` *(lazy)* | `data/diagnostics.json` — hand-authored BLxxxx prose | hand-authored |

Types live in `types/` and are hand-written transcriptions of the emitters,
citing the F# line ranges they came from. There is no `exports` field in
`package.json`, so every subpath stays reachable:
`require("@blade-lang/ide-protocol/serveProto")`, `/surface.json`,
`/data/diagnostics.json`.

`surface` and `diagnosticsKb` are **lazy getters**. Both files are artifacts
rather than source, so requiring them eagerly would make the whole package
unloadable in a tree where they have not been produced yet — which is exactly
the state `generate-surface` exists to fix. Accessing a missing one throws with
instructions instead of a bare `MODULE_NOT_FOUND`.

## Using the client

```js
const { createClient, resolveCompiler } = require("@blade-lang/ide-protocol");

const client = createClient({
  findCompiler: () => resolveCompiler({ explicitPath: mySetting }).exe,
  output: { appendLine: (line) => console.error(line) },
  cwd: () => myWorkspaceRoot(),
}, "my-host");

const payload = await client.check("/abs/path/prog.blade", sourceText, "fast");
```

`findCompiler` is a **function**, called at every spawn rather than once: that
is what lets `resolveCompiler`'s newest-mtime rule pick up a rebuild, and a
stale Release build next to a fresh Debug build otherwise reports errors the
compiler no longer produces.

`deps.args` (default `["ide", "serve"]`) is a test seam — a fake NDJSON server
is spawned as `node fake-serve.js`. It exists because Windows Node ≥18.20
refuses to spawn `.cmd` shims without a shell, so tests need to name the
interpreter directly.

`deps.output` writes to a log, never to stdout by default. An MCP server must
route it to **stderr**: the MCP transport owns stdout, and one stray line on it
corrupts the stream.

`deps.env` (an object, or a function re-read per spawn like `findCompiler`) is
the child's environment; omitted, the child inherits this process's. It
**replaces** rather than extends — Node's spawn semantics — so spread the
parent's:

```js
env: () => ({ ...process.env, GRDIR: grRoot,
              PATH: path.join(grRoot, "bin") + path.delimiter + process.env.PATH }),
```

That is what `renderPlot` needs: the compiler spawns a native GR worker whose
DLLs resolve off `GRDIR` and `PATH`, and both failure modes are *silent*
crashes rather than error messages.

## Rendering a plot with GR

```js
const { frame } = await client.renderPlot({
  spec: entry.spec,          // the retained {data, layout} figure JSON
  plotId: entry.id,          // the original frame's meta.id
  width: 800, height: 600,   // optional; clamped to [64..4096]
});
display.publish(display.decodeFrame(frame).frame, "gr");
```

Post-hoc by construction: nothing re-runs, and the program that produced the
figure need not still exist. The response is a complete display frame
(`image/png`, base64), so it takes the same decode/publish path an eval's
frames take — and echoing `plotId` back in `frame.meta.id` is what makes a
panel **merge** the render into the existing plot instead of appending a second
entry. A compiler without the verb — or one that cannot reach GR — rejects with
`err.protocolError === true` and a message naming which.

## Consumer notes

**There is no banner.** The compiler says nothing until asked, so the
capability probe is a `ping`: `{"id":N,"cmd":"ping"}` answers
`{"id":N,"ok":true,"serve":1,"version":"0.20.0"}`. `createClient` does this for
you on first use and latches `available()` to `"yes"` or `"no"`.

**An unknown command is answered, not rejected** —
`{"id":N,"error":"unknown cmd 'x'"}`. That is a contract, and it is how you
probe for a verb a given compiler may not have. `createClient` surfaces it as a
rejection with `err.protocolError === true`, and — deliberately — does **not**
tear the process down: the compiler answered correctly, it just doesn't know
that word.

**Error responses may carry `id: null`.** A request that failed to parse, or
one that omitted its own id, has no id to echo. Correlation by id alone
therefore cannot settle every request, so **clients need timeouts**;
`createClient` has them (ping 5s, fast 10s, full 30s) and kills the process on
expiry, respawning on the next call subject to a 500ms/2s/8s backoff floor.

**Requests are strictly serial compiler-side** (`Ast.synthSpan` is a mutable
global). Pipelining is allowed on the wire; it just won't buy concurrency. If
you need a second concurrent lane — say a notebook eval that runs g++ for
several seconds without stalling every keystroke's check — call `createClient`
again for an independent process.

**A line carrying `event` is never a response.** Streamed display frames can
repeat an in-flight request's id, so the event check must come BEFORE the id
lookup, or a mid-eval plot resolves the eval early with a payload that has no
stdout and no bindings. `createClient` handles this and routes frames to
`display.publish`.

**`display`'s hub is module-level state.** publish/subscribe only connect when
every participant requires the *same* module instance; a consumer that keeps a
private copy of `display.js` alongside the package silently splits the channel.

**Spans are 1-based and `endCol` is exclusive**, everywhere in the check
payload. Optional fields are *absent*, not null — the sole exception is
`references[].def`, which is literally `null` when no name span survived.

## Regenerating `surface.json`

```bash
npm run generate-surface          # or: node scripts/generate-surface.js
```

It resolves a compiler (honoring `BLADE_EXE`), runs `<exe> ide surface`,
validates the output with `JSON.parse`, and writes the file as UTF-8 with no
BOM, LF, and exactly one trailing newline. It prints the resolved binary and
the field counts, and exits 1 with remediation if the compiler cannot be run or
predates the verb.

**This node script is the only supported regeneration path.** In particular it
is not `blade ide surface > surface.json` from PowerShell: `>` there writes a
BOM and translates the line ending to CRLF, and the compiler-side freshness
test compares the committed bytes against the renderer's output.

`surface.json` and `data/diagnostics.json` are checked in, and both are
deployed beside the binary by `Blade.fsproj`, so consumers find them under both
a repo checkout and an installed toolchain.

## Versioning

The package version is in **lockstep with the compiler version**
(`src/Cli.fs`'s `compilerVersion`, `0.20.0` today). Bump both in the same
change; a vendored tarball's name follows it
(`blade-lang-ide-protocol-0.20.0.tgz`).

The lockstep is a claim about the *protocol*, not a guarantee about the binary
a consumer happens to run — a checked-in `surface.json` can fall behind its
compiler. Compare `surface.compilerVersion` against a live `ping`'s `version`
to detect the skew, and treat a code absent from the surface registry as
"unregistered in this surface" rather than unknown.

## License

LGPL-3.0-only, same as the Blade compiler it ships with. See `LICENSE`.
