// On-disk test corpus loader. Test sources live in tests/corpus/**/*.blade —
// one file per test, one directory per sublist — instead of being embedded in
// the Test_*.fs modules (audit §2.3 / plan Phase 0.1: the corpus doubles as
// the differential oracle for the rewrite, so it must be real files).
//
// File format (written originally by the one-shot corpus dump):
//   line 1:  // TEST: <exact test name>     — REQUIRED. Names carry semantics:
//            a name ending in "(rejects)" marks an intentional reject-probe,
//            so silent name loss would corrupt pass/fail classification.
//   line 2:  // MODULE: <module file name>  — multi-file tests only.
//   rest:    the Blade source, verbatim (EXPECT comments intact).
// Files run in filename order — keep the NNN_ prefix so corpus order is
// stable and diffs against recorded runs stay meaningful.
module Blade.Tests.Corpus

open System
open System.IO

/// Root of the .blade corpus. Prefer the source tree relative to the working
/// directory (so corpus edits take effect without a rebuild when running from
/// the repo root), falling back to the copy deployed next to the binary
/// (Blade.fsproj copies tests/corpus/** to the output dir). Resolved lazily
/// so non-test commands (`blade run` etc.) never touch it.
let private corpusRoot : Lazy<string> =
    lazy
        let candidates =
            [ Path.Combine(".", "tests", "corpus")
              Path.Combine(AppContext.BaseDirectory, "tests", "corpus") ]
        match candidates |> List.tryFind Directory.Exists with
        | Some d -> d
        | None ->
            failwithf "Test corpus not found. Looked in: %s"
                (candidates |> List.map Path.GetFullPath |> String.concat " ; ")

/// Split a .blade file into its directive lines and source body.
/// Returns (testName, moduleName option, source).
let private parseBladeFile (path: string) : string * string option * string =
    let text = File.ReadAllText(path)
    let nl = text.IndexOf('\n')
    if nl < 0 || not (text.StartsWith("// TEST: ")) then
        failwithf "corpus file %s: first line must be '// TEST: <name>'" path
    let name = text.Substring(9, nl - 9).TrimEnd('\r')
    // The name is load-bearing, not decoration: "(rejects)" / "(aborts)"
    // suffixes decide which classification arm the runner takes, so a blank name
    // would silently demote a probe to an ordinary test.
    if String.IsNullOrWhiteSpace name then
        failwithf "corpus file %s: '// TEST:' has an empty name (the name carries the (rejects)/(aborts) probe markers)" path
    let rest = text.Substring(nl + 1)
    if rest.StartsWith("// MODULE: ") then
        let nl2 = rest.IndexOf('\n')
        let modName = rest.Substring(11, (if nl2 < 0 then rest.Length else nl2) - 11).TrimEnd('\r')
        (name, Some modName, (if nl2 < 0 then "" else rest.Substring(nl2 + 1)))
    else
        (name, None, rest)

/// The .blade files of a directory in deterministic (ordinal filename) order.
///
/// BOTH "no such directory" and "directory with no .blade files" are hard
/// failures, and for the same reason. A MISSING directory already failed loudly;
/// an EXISTING but EMPTY one silently produced zero tests, and a category that
/// contributes zero tests reports "0 passed, 0 failed" — a green line that
/// asserts nothing. That is the shape a mistyped corpus path, an interrupted
/// `git mv`, or a stale deployed copy takes, and it is indistinguishable from a
/// healthy run at the summary level. So the empty case says so instead.
///
/// Note this is safe for the multi-file corpus: `multiFileCategory` calls this
/// per TEST subdirectory (each of which does hold .blade files) and never on the
/// category directory itself, which legitimately holds only subdirectories.
let private bladeFiles (dir: string) : string[] =
    if not (Directory.Exists dir) then
        failwithf "corpus category directory missing: %s" (Path.GetFullPath dir)
    let files = Directory.GetFiles(dir, "*.blade")
    if Array.isEmpty files then
        failwithf "corpus category directory has no .blade files: %s (an empty category would report '0 passed, 0 failed' and assert nothing)"
            (Path.GetFullPath dir)
    Array.sortInPlaceWith (fun (a: string) (b: string) -> String.CompareOrdinal(Path.GetFileName a, Path.GetFileName b)) files
    files

/// Load a single-file test category: tests/corpus/<dirName>/*.blade
/// as the (name, source) list the runners consume.
let category (dirName: string) : (string * string) list =
    bladeFiles (Path.Combine(corpusRoot.Value, dirName))
    |> Array.map (fun f ->
        let (name, _, source) = parseBladeFile f
        (name, source))
    |> Array.toList

/// Load a multi-file test category: tests/corpus/<dirName>/<test>/*.blade,
/// one subdirectory per test, one .blade per module file (NN_ order prefix,
/// module file name from the // MODULE: directive).
let multiFileCategory (dirName: string) : (string * (string * string) list) list =
    let catDir = Path.Combine(corpusRoot.Value, dirName)
    if not (Directory.Exists catDir) then
        failwithf "corpus category directory missing: %s" (Path.GetFullPath catDir)
    let dirs = Directory.GetDirectories(catDir)
    // Same rule as bladeFiles: a multi-file category with no test subdirectories
    // yields zero tests and a vacuous green "0 passed, 0 failed".
    if Array.isEmpty dirs then
        failwithf "multi-file corpus category %s has no test subdirectories (an empty category would report '0 passed, 0 failed' and assert nothing)"
            (Path.GetFullPath catDir)
    Array.sortInPlaceWith (fun (a: string) (b: string) -> String.CompareOrdinal(Path.GetFileName a, Path.GetFileName b)) dirs
    dirs
    |> Array.map (fun testDir ->
        let parts =
            bladeFiles testDir
            |> Array.map (fun f ->
                let (name, modOpt, source) = parseBladeFile f
                match modOpt with
                | Some m -> (name, (m, source))
                | None -> failwithf "corpus file %s: multi-file test lacks '// MODULE: <name>'" f)
        if Array.isEmpty parts then
            failwithf "corpus test directory %s has no .blade files" testDir
        let (testName, _) = parts.[0]
        (testName, parts |> Array.map snd |> Array.toList))
    |> Array.toList
