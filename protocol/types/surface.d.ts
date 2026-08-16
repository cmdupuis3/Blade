// surface.json — the compiler's own language surface, dumped by
// `blade ide surface` (and available over the serve protocol as the `surface`
// command). Regenerate with `npm run generate-surface`.
//
// The point of this file is drift detection. Every name here used to be
// hand-copied into a consumer (the extension kept its own keyword, builtin and
// type lists, with `file:line` citations that went stale silently — a builtin
// with a hover but no highlighting is the bug that motivated this package).
// Consumers now derive their NAME SETS from this document and keep only
// categories and prose as their own curation.
//
// Deliberately NOT in this schema, so consumers do not assume it:
//   * no `typeNames` split — `scalarTypes` is the whole list, and the index
//     type constructors (Idx, SymIdx, AntisymIdx, ...) are lexer KEYWORDS, so
//     they appear in `keywords`;
//   * no categories on builtins — word plus token suffices for drift
//     detection, and categorization is a client-side editorial choice;
//   * `diagnostics` is an ARRAY of {code,title,phase}, not a map. Index it
//     into a Map at load if you want lookups.

/** Diagnostic phase bands, from `Diagnostics.phaseOfCode`. Treat as open: a
 *  new elaborator adds a band without a protocol revision. */
export type SurfacePhase =
  | "lex"
  | "parse"
  | "resolve"
  | "types"
  | "constraints"
  | "ir"
  | "backend"
  | "runtime"
  | "internal"
  | (string & {});

/** One lexer keyword, with the name of the DU token it produces. Both are
 *  needed: the word is what a consumer highlights, the token is what makes a
 *  rename on the compiler side visible as a diff here. Emitted in declaration
 *  order, and case variants (`true` and `True`) are separate entries. */
export interface SurfaceKeyword {
  word: string;
  /** F# DU case name, e.g. "KwLet". */
  token: string;
}

/** One registered diagnostic code. `title` byte-matches
 *  `Diagnostics.Codes.registryEntries`, in registry order. */
export interface SurfaceDiagnostic {
  /** "BL3016". */
  code: string;
  title: string;
  phase: SurfacePhase;
}

/** The math intrinsics, split by arity as the compiler splits them
 *  (`Grad.mathIntrinsics` / `binaryMathIntrinsics` / `complexMathIntrinsics`). */
export interface SurfaceMathIntrinsics {
  unary: string[];
  /** Exactly ["atan2", "log_base"] today. */
  binary: string[];
  complex: string[];
}

/** The whole document. */
export interface SurfaceJson {
  /** Schema version. 1 today. */
  version: 1;
  /** The compiler that produced this dump — compare against a live `ping`'s
   *  `version` to detect a checked-in surface that has fallen behind its
   *  binary. */
  compilerVersion: string;
  keywords: SurfaceKeyword[];
  /** Operator spellings, longest-first (the lexer's own ordering). */
  operators: string[];
  mathIntrinsics: SurfaceMathIntrinsics;
  /** `StaticEval.knownBuiltinNames()`, sorted. */
  builtins: string[];
  /** The builtin scalar type names. */
  scalarTypes: string[];
  /** Names `Ide.builtinCallOf` recognizes at a call site. */
  builtinCalls: string[];
  diagnostics: SurfaceDiagnostic[];
}

/** The union a consumer should test name membership against — no single array
 *  is "the names". */
export type SurfaceNameSets = Pick<
  SurfaceJson,
  "keywords" | "builtins" | "builtinCalls" | "mathIntrinsics"
>;
