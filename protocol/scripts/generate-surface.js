#!/usr/bin/env node
// Regenerate ../surface.json from a Blade compiler.
//
// This is the ONLY supported way to produce that file. In particular it is not
// `blade ide surface > surface.json` from PowerShell: `>` there writes UTF-16
// or a BOM-prefixed UTF-8 and translates the line ending to CRLF, and the
// compiler-side freshness test compares the committed bytes against the
// renderer's output. Node's writeFileSync does neither.
//
// The compiler is resolved by ../resolveCompiler.js, so BLADE_EXE selects one
// explicitly and an in-repo build is picked up automatically (newest of
// Release/Debug).
//
// Exit codes: 0 wrote the file; 1 the compiler could not be run, does not have
// the `ide surface` verb, or printed something that is not JSON.

"use strict";

const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const { resolveCompiler } = require("../resolveCompiler");

const OUT = path.join(__dirname, "..", "surface.json");

function die(lines) {
  for (const line of [].concat(lines)) console.error(line);
  process.exit(1);
}

function main() {
  const { exe, origin } = resolveCompiler();

  let stdout;
  try {
    stdout = execFileSync(exe, ["ide", "surface"], {
      encoding: "utf8",
      windowsHide: true,
      maxBuffer: 32 * 1024 * 1024,
    });
  } catch (e) {
    // A nonzero exit sets `status`; a spawn failure (ENOENT) leaves it null
    // and puts the reason in `code`.
    const status = e.status == null ? e.code : `exit ${e.status}`;
    die([
      `generate-surface: could not run \`${exe} ide surface\` (${status})`,
      `  compiler resolved from: ${origin}`,
      e.stderr ? `  stderr: ${String(e.stderr).trim().split(/\r?\n/)[0]}` : "",
      "",
      "  If this compiler predates the `ide surface` verb, rebuild it:",
      "    dotnet build Blade.fsproj -c Release",
      "  Or point BLADE_EXE at a compiler that has it.",
    ].filter(Boolean));
  }

  // `printfn` emits CRLF on Windows; the payload itself is one line by
  // construction (Ide.jsonEscape turns every control character into an escape),
  // so the first non-empty line IS the document.
  const line = stdout.split(/\r?\n/).find((l) => l.trim() !== "");
  if (!line) {
    die([
      `generate-surface: \`${exe} ide surface\` printed nothing`,
      "  Expected one line of JSON.",
    ]);
  }

  let surface;
  try {
    surface = JSON.parse(line);
  } catch (e) {
    die([
      `generate-surface: \`${exe} ide surface\` did not print JSON — ${e.message}`,
      `  got: ${line.slice(0, 200)}`,
      "",
      "  An older compiler answers an unknown verb with usage text; rebuild it.",
    ]);
  }

  // UTF-8, no BOM, LF, exactly one trailing newline.
  fs.writeFileSync(OUT, line + "\n", { encoding: "utf8" });

  const mi = surface.mathIntrinsics || {};
  const n = (v) => (Array.isArray(v) ? v.length : 0);
  console.log(`generate-surface: wrote ${OUT}`);
  console.log(`  compiler:        ${exe} (${origin})`);
  console.log(`  version:         schema ${surface.version}, compiler ${surface.compilerVersion}`);
  console.log(`  keywords:        ${n(surface.keywords)}`);
  console.log(`  operators:       ${n(surface.operators)}`);
  console.log(
    `  mathIntrinsics:  ${n(mi.unary)} unary, ${n(mi.binary)} binary, ${n(mi.complex)} complex`
  );
  console.log(`  builtins:        ${n(surface.builtins)}`);
  console.log(`  scalarTypes:     ${n(surface.scalarTypes)}`);
  console.log(`  builtinCalls:    ${n(surface.builtinCalls)}`);
  console.log(`  diagnostics:     ${n(surface.diagnostics)}`);
}

main();
