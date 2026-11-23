# AGENTS.md

The project guide for AI coding agents is **[CLAUDE.md](CLAUDE.md)**. Read it in full before
changing anything: it covers what Blade is, the repository layout, how to build and test, the
language essentials and style rules, and the conventions of the F# compiler. This file used to
be a second copy of that guide and drifted from it; it now only points there.

The rules most often needed, in one place:

- Blade is an array-functional language; the compiler is F# 10 (`Blade.fsproj`) and emits C++17.
  Build with `dotnet build Blade.fsproj -c Release`; the build must stay at 0 warnings.
- MSYS2 ucrt64 g++ must be first on PATH for anything that compiles generated code.
- Run tests with `blade test <key>` and read the TOTAL line and its `, N skipped` suffix: the
  harness can exit 0 with failures, and missing toolchain pieces skip.
- Never run two `blade test` processes from the same working directory.
- Iteration in Blade is declarative (loop objects, recursive arrays); there is no imperative
  loop. Nothing converts numbers implicitly. Refusals are features: fix the declaration.
- Environment knobs: the registry is `src/Knobs.fs`, the generated index
  `docs/reference/knobs.md`, the long notes `docs/reference/knob-notes.md`.
- Semantics: `docs/formalism.md`. Feature census: `docs/features.md`.
