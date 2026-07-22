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
    let rest = text.Substring(nl + 1)
    if rest.StartsWith("// MODULE: ") then
        let nl2 = rest.IndexOf('\n')
        let modName = rest.Substring(11, (if nl2 < 0 then rest.Length else nl2) - 11).TrimEnd('\r')
        (name, Some modName, (if nl2 < 0 then "" else rest.Substring(nl2 + 1)))
    else
        (name, None, rest)

/// The .blade files of a directory in deterministic (ordinal filename) order.
let private bladeFiles (dir: string) : string[] =
    if not (Directory.Exists dir) then
        failwithf "corpus category directory missing: %s" (Path.GetFullPath dir)
    let files = Directory.GetFiles(dir, "*.blade")
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
