// `blade test emitter-names` (borrow round, lane x4): the emitter's own names
// against the generator sources and against the program spellings.
//
// THE HAZARD. The C++ generators write identifiers of their own into the scopes
// they emit user names into: the flat sweep's loop index `__fk`, a range holder
// `__range<i>_<name>`, a fold's lanes `__rlane<l>`, ... A binding, function,
// parameter or capture spelled exactly like one met it in the translation unit
// and was shadowed by it -- `let __fk: Float64 = 10.0` read inside a map's
// kernel read the sweep's index, a silent wrong value. src/CodeGenEmitterNames.fs
// lists those names in tables (OPEN: reserved; CLOSED, MINTED AVOIDING,
// COUPLINGS: not), and CodeGenState.installCppNameTable moves every program
// spelling -- user-written or minted by a front-end pass -- that is an OPEN
// name, exactly as it moves a spelling a runtime header declares.
//
// THE CHECKS (the fast part, in the default suite as "Emitter Names Sync"):
//   1. EXTRACTION. Every identifier beginning with `__` in the source text of
//      src/CodeGen*.fs (but the tables' own file), src/providers/*.fs,
//      src/RunRecord.fs and src/display/DisplayFrame.fs, outside `//` comments,
//      read as a FIXED token, or a TEMPLATE whose interpolation holes (`{e}`,
//      `{{e}}`, sprintf's `%d` / `%s`) are kept with their source text, or an
//      OPEN token (a fixed token that ends a string literal continued by `+`).
//      A brace group that is empty or holds a top-level comma is a C++ brace,
//      not a hole: the token ends before it.
//   2. COVERAGE. Every fixed token and every template shape is in EXACTLY ONE
//      table; a row's `#` holes are filled in the source only by hole texts read
//      as numbers (`numericHoleTexts`), its `@` holes only by a binding's emitted
//      spelling (`bindingNameHoleTexts`) -- a new hole text fails until it is
//      classified, or the hole becomes `*`; an open token is a row of
//      `builtInSteps`. Spellings under the reserved prefixes (`__blade_`,
//      `blade_`) are covered by the prefix rule.
//   3. NO STALE ROW: every row of every table is still written by some source;
//      a coupling's emitter end is, and its spelling is no OPEN name.
//   4. The tables' shape (every row begins with `__`, so the `u_` escape of
//      installCppNameTable leaves every family; no family ends in a literal
//      `_`; at most one `@` hole, no `*` before it), and the predicate on
//      spellings that must and must not be emitter names.
// WHAT THE EXTRACTION CANNOT SEE: a name built in more than one step (a stem a
// helper appends to, `free "__sc_pool"`; a string joined with `+` when the
// token does not end its literal), and a name written outside the files read
// (the runtime headers are the generated reserved set, CppReservedNames.txt).
// The first kind is listed by hand (`builtInSteps`, `mintedAvoiding`), which
// check 3 keeps current; names that do not begin with `__` (a binding's
// generated suffix `a_extents`) are CodeGenState's generated-suffix rule.
//
// THE CORPUS MODE (`blade test emitter-names` only): every single-file corpus
// program and every .blade under examples/, bench/ and stdlib/ is lowered from
// its own path (as `blade emit` lowers it), and every spelling
// installCppNameTable judges -- module bindings, functions, parameters,
// captures, index-type names, in every module of the program -- is checked
// against the predicate with that module's program words; each hit must be
// MOVED by the name table run on that module (one `hit:` line per program,
// which the renames-only emission gate of lane x4 reads). Then the witness is
// compiled and run by this binary with `--print __fk,r`: a moved binding prints, and is
// selected, under the user's spelling.
module Blade.Tests.EmitterNameTests

open System
open System.IO
open Blade
open Blade.IR
open Blade.Tests.TestHarness
module EN = Blade.CodeGenEmitterNames

// ---------------------------------------------------------------------------
// The extraction.
// ---------------------------------------------------------------------------

type private Token = { Text: string; Holes: string list; Open: bool; Where: string }

let private isIdent (c: char) = Char.IsLetterOrDigit c || c = '_'

/// The `__` tokens of one source line (see the header, check 1).
let private scanLine (where: string) (line: string) : Token list =
    let n = line.Length
    let out = Collections.Generic.List<Token>()
    let mutable p = 0
    let mutable stop = false
    while not stop && p < n - 1 do
        if line[p] = '_' && line[p + 1] = '_' && (p = 0 || not (isIdent (line[p - 1]) || line[p - 1] = '}')) then
            let pre = line.Substring(0, p)
            if pre.Contains "//" && (pre |> Seq.filter ((=) '"') |> Seq.length) % 2 = 0 then stop <- true
            else
                let parts = Collections.Generic.List<string>()
                let holes = Collections.Generic.List<string>()
                let buf = Text.StringBuilder()
                let mutable q = p
                let mutable go = true
                while go && q < n do
                    let c = line[q]
                    if isIdent c then
                        buf.Append c |> ignore
                        q <- q + 1
                    elif c = '%' && q + 1 < n && (line[q + 1] = 'd' || line[q + 1] = 's') then
                        parts.Add(buf.ToString()); buf.Clear() |> ignore
                        holes.Add("%" + string (line[q + 1]))
                        q <- q + 2
                    elif c = '{' then
                        let mutable d = 0
                        let mutable r = q
                        let mutable found = false
                        while not found && r < n do
                            if line[r] = '{' then d <- d + 1
                            elif line[r] = '}' then
                                d <- d - 1
                                if d = 0 then found <- true
                            if not found then r <- r + 1
                        if not found then go <- false
                        else
                            let content = line.Substring(q + 1, r - q - 1).Trim('{', '}').Trim()
                            if content = "" || content.Contains "," then go <- false
                            else
                                parts.Add(buf.ToString()); buf.Clear() |> ignore
                                holes.Add content
                                q <- r + 1
                    else go <- false
                parts.Add(buf.ToString())
                let text = String.Join("{}", parts)
                if text.Length > 2 then
                    let rest = line.Substring q
                    let opened =
                        holes.Count = 0 && rest.StartsWith "\"" && rest.Substring(1).TrimStart().StartsWith "+"
                    out.Add { Text = text; Holes = List.ofSeq holes; Open = opened; Where = where }
                p <- max q (p + 2)
        else p <- p + 1
    List.ofSeq out

/// The generator sources the extraction reads (repo-relative).
let private sourceFiles (root: string) : string list =
    let src = Path.Combine(root, "src")
    let codeGen =
        Directory.GetFiles(src, "CodeGen*.fs")
        |> Array.filter (fun p -> Path.GetFileName p <> "CodeGenEmitterNames.fs")
        |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    let providers =
        Directory.GetFiles(Path.Combine(src, "providers"), "*.fs") |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    [ yield! codeGen; yield! providers
      yield Path.Combine(src, "RunRecord.fs"); yield Path.Combine(src, "display", "DisplayFrame.fs") ]
    |> List.map (fun p -> Path.GetRelativePath(root, p).Replace('\\', '/'))

let private extract (root: string) : Token list =
    [ for rel in sourceFiles root do
        let lines = File.ReadAllLines(Path.Combine(root, rel))
        for i in 0 .. lines.Length - 1 do
            if not (lines[i].TrimStart().StartsWith "//") then
                yield! scanLine $"{rel}:{i + 1}" lines[i] ]

// ---------------------------------------------------------------------------
// The classification the table's holes rest on.
// ---------------------------------------------------------------------------

/// Hole texts the generators fill with a NUMBER (an id, a level, a position, a
/// counter). A `#` hole of the table may be filled only by these.
let private numericHoleTexts =
    Set.ofList [
        "%d"; "(List.findIndex ((=) n) invIdx)"; "Map.count srcAliases"; "b.Id"; "bJ.Id"; "binding.Id"
        "builder.FreshId()"; "c.Id"; "callable.Id"; "d"; "e.ArrayPosition"; "e.FoldLetId"; "elem.ArrayPosition"
        "f.Id"; "g"; "gkId"; "hostId"; "i"; "id"; "int binding.Id"; "ix.Id"; "j"; "k"; "l"; "l.Slot"
        "level.Level"; "p"; "p.VarId"; "pi"; "re.ArrayPosition"; "setId"; "sumId"; "t"; "tmp"; "vid"; "w.Index" ]

/// Hole texts the generators fill with the EMITTED SPELLING of the binding the
/// code is emitted for. An `@` hole of the table may be filled only by these.
let private bindingNameHoleTexts =
    Set.ofList [
        "%s"; "b"; "name"; "varName"; "(sanitizeCppName name)"; "(sanitizeCppName lname)"
        "(sanitizeCppName plan.ConsumerName)"; "(sanitizeCppName codeGen.OutputName)"
        "(prefixFamilyHole ((=) \"cells\") (sanitizeCppName n))"; "(prefixFamilyHole bareRangeWord s)"
        "(prefixFamilyHole ((=) \"up\") varName)" ]

let private shapeOf (pattern: string) = pattern.Replace("#", "{}").Replace("*", "{}").Replace("@", "{}")
let private holeKinds (pattern: string) = pattern |> Seq.filter (fun c -> c = '#' || c = '*' || c = '@') |> List.ofSeq
/// Does the fixed spelling `s` fall in the table row `pattern`? An `@` hole is
/// read with an internal word only (`__...`): a fixed spelling the emitter
/// writes belongs to a binding-name family only through such a word.
let private matchesPattern (pattern: string) (s: string) =
    let parts = pattern.Split '@'
    let text = parts |> Array.map EN.patternRegexText |> String.concat "__[A-Za-z0-9_]*"
    Text.RegularExpressions.Regex.IsMatch(s, "^" + text + "$")
let private underPrefix (text: string) = EN.emitterReservedPrefix text

/// The tables, each as its patterns (a fixed name is a pattern without holes).
let private openTable = EN.fixedNames @ EN.familyPatterns @ List.map snd EN.builtInSteps |> List.distinct
let private closedTable = EN.closedNames |> List.map fst
let private mintedTable = EN.mintedAvoiding |> List.map fst
let private tables = [ "OPEN", openTable; "CLOSED", closedTable; "MINTED AVOIDING", mintedTable ]

/// Checks 2-4 over the extracted tokens: the failures, one line each.
let private syncFailures (tokens: Token list) : string list =
    let fixedTokens = tokens |> List.filter (fun t -> t.Holes.IsEmpty && not t.Open)
    let templates = tokens |> List.filter (fun t -> not t.Holes.IsEmpty)
    let openTokens = tokens |> List.filter (fun t -> t.Open)
    let fixedTexts = fixedTokens |> List.map _.Text |> Set.ofList
    let templateShapes = templates |> List.map _.Text |> Set.ofList
    let openTexts = openTokens |> List.map _.Text |> Set.ofList
    let stepKeys = EN.builtInSteps |> List.map fst |> Set.ofList
    let firstWhere (ts: Token list) = (List.head ts).Where
    let holding (pick: string -> bool) = tables |> List.filter (fun (_, ps) -> ps |> List.exists pick) |> List.map fst
    [ // 2. coverage: exactly one table
      for (text, ts) in fixedTokens |> List.groupBy _.Text do
        if not (underPrefix text) then
            match holding (fun p -> p = text || matchesPattern p text) with
            | [ _ ] -> ()
            | [] -> yield $"{text} ({firstWhere ts}) is written by the emitter and is in no table of src/CodeGenEmitterNames.fs: add it to fixedNames (OPEN), or to closedNames with its scope argument"
            | many -> yield $"""{text} ({firstWhere ts}) is in more than one table ({String.concat ", " many})"""
      for (shape, ts) in templates |> List.groupBy _.Text do
        if not (underPrefix shape) then
            match holding (fun p -> shapeOf p = shape) with
            | [] -> yield $"{shape} ({firstWhere ts}) is a family the emitter writes and no table has its shape: add it to familyPatterns (OPEN; `#` a number hole, `@` a binding-name hole, `*` any word), or to closedNames with its scope argument"
            | [ _ ] ->
                let candidates = tables |> List.collect snd |> List.filter (fun p -> shapeOf p = shape)
                for t in ts |> List.distinctBy _.Holes do
                    let fits (p: string) =
                        List.forall2 (fun k (h: string) ->
                            k = '*' || (k = '#' && numericHoleTexts.Contains h) || (k = '@' && bindingNameHoleTexts.Contains h))
                            (holeKinds p) t.Holes
                    if not (candidates |> List.exists fits) then
                        let rows = String.concat " | " candidates
                        let holeTexts = String.concat "`, `" t.Holes
                        yield $"{shape} ({t.Where}) fills its holes with `{holeTexts}`, which no row of its shape ({rows}) admits: a `#` hole takes the texts of numericHoleTexts, an `@` hole those of bindingNameHoleTexts (tests/EmitterNameTests.fs); classify the text there, or make the hole `*`"
            | many -> yield $"""{shape} ({firstWhere ts}) is in more than one table ({String.concat ", " many})"""
      for (text, ts) in openTokens |> List.groupBy _.Text do
        if not (underPrefix text) && not (stepKeys.Contains text) then
            yield $"{text} ({firstWhere ts}) begins a name built in more than one step: give it a row of builtInSteps with the family it begins"
      // 3. no stale row
      for (tableName, ps) in tables do
        for p in ps do
          if not (List.contains p (List.map snd EN.builtInSteps)) then
            let holes = holeKinds p
            let written =
                if holes.IsEmpty then fixedTexts.Contains p || stepKeys.Contains p
                else templateShapes.Contains (shapeOf p) || fixedTexts |> Set.exists (matchesPattern p)
            if not written then
                yield $"{tableName} row {p}: no generator source writes it any more -- remove the row"
      for (k, _) in EN.builtInSteps do
        if not (fixedTexts.Contains k) && not (openTexts.Contains k) then
            yield $"builtInSteps row {k}: no generator source writes it any more -- remove the row"
      // couplings: the emitter end is a name the emitter writes; the spelling is no OPEN name
      for (pass, spelling, emitterEnd) in EN.couplings do
        if not (fixedTexts.Contains emitterEnd || templateShapes.Contains (shapeOf emitterEnd)) then
            yield $"coupling {pass} / {spelling}: its emitter end {emitterEnd} is written by no generator source"
        if EN.isEmitterName (fun _ -> true) spelling then
            yield $"coupling {pass} / {spelling}: the spelling is an OPEN emitter name, so the name table would move it"
      // 4. the tables' shape
      for (tableName, ps) in tables do
        for s in ps do
          if not (s.StartsWith "__") then yield $"{tableName} row {s} does not begin with `__`"
          if s |> Seq.exists (fun c -> not (isIdent c || c = '#' || c = '*' || c = '@')) then
              yield $"{tableName} row {s} has a character that is not an identifier character or a hole"
          if (s |> Seq.filter ((=) '@') |> Seq.length) > 1 then yield $"{tableName} row {s} has more than one `@` hole"
          if s.EndsWith "_" && not (holeKinds s).IsEmpty then yield $"{tableName} row {s} ends in a literal `_`: a lengthened spelling could stay in it"
          if s.Contains "@" && (let k = s.IndexOf '@' in s.Substring(0, k).Contains "*") then
              yield $"{tableName} row {s} has a `*` hole before its `@` hole"
      for (p, why) in EN.closedNames @ EN.mintedAvoiding do
        if String.IsNullOrWhiteSpace why then yield $"row {p} states no reason" ]

/// Spellings that must and must not be emitter names (check 4), for a program
/// spelling `a`, `f` and `v`.
let private predicateFailures () : string list =
    let word (w: string) = w = "a" || w = "f" || w = "v" || w.StartsWith "__"
    let must = [ "__fk"; "__i3"; "__range2_a"; "__range0_r7"; "__rlane5"; "__tup_12"; "__fp_cells"; "__blade_t0"
                 "blade_x"; "__dca_g1_2"; "__dcf_a"; "__fp_a"; "__omp_maxth_q"; "__match_4"; "__a_x"; "__t__v3_0"; "__sk2_q" ]
    let mustNot = [ "fk"; "__v3"; "__lambda_3"; "__expr0"; "x"; "__fk_"; "__i3_"; "__userthing"; "u___fk"
                    "__dcq_a"; "__fp_q"; "__t_x"; "__q_k"; "__sc_pool"; "__sc_pool2" ]
    let wordTail = [ "__af_x"; "__range2_out"; "__fp_y_" ]
    let notWordTail = [ "__fk_"; "__i3_"; "__range2_" ]
    [ for s in must do if not (EN.isEmitterName word s) then yield $"isEmitterName {s} should be true"
      for s in mustNot do if EN.isEmitterName word s then yield $"isEmitterName {s} should be false"
      for s in wordTail do if not (EN.isWordTailMember s) then yield $"isWordTailMember {s} should be true"
      for s in notWordTail do if EN.isWordTailMember s then yield $"isWordTailMember {s} should be false" ]

// ---------------------------------------------------------------------------
// The corpus mode.
// ---------------------------------------------------------------------------

/// The spellings installCppNameTable judges, module by module, with the same
/// program words: the OPEN emitter names among them ("site spelling"), each with
/// whether the name table, run on that module, MOVED it.
let private programHits (ir: IRProgram) : (string * bool) list =
    [ for m in ir.Modules do
        let census =
            seq {
                for b in m.Bindings do
                    if not (b.Name.StartsWith "_(") then yield b.Name; yield b.Name.Replace(".", "__")
                for f in m.Functions do
                    yield f.Name; yield f.Name.Replace(".", "__")
                    for p in f.Params do yield p.Name
                    for c in f.Captures do yield c.Name }
            |> Set.ofSeq
        let word (w: string) = census.Contains w || w.StartsWith "__"
        let isName (s: string) = EN.isEmitterName word (s.Replace(".", "__"))
        let moved = CodeGenState.installCppNameTable m
        let movedFn = moved.Functions |> List.map (fun f -> f.Id, f) |> Map.ofList
        for b in m.Bindings do
            if not (b.Name.StartsWith "_(") && isName b.Name then
                yield $"binding {b.Name}", CodeGenState.sanitizeCppName b.Name <> b.Name.Replace(".", "__")
        for f in m.Functions do
            let g = Map.tryFind f.Id movedFn |> Option.defaultValue f
            if isName f.Name then yield $"function {f.Name}", g.Name <> f.Name
            for (p, q) in List.zip f.Params g.Params do
                if not (p.Name.Contains ".") && isName p.Name then yield $"parameter {p.Name} of {f.SourceName}", q.Name <> p.Name
            for (c, d) in List.zip f.Captures g.Captures do
                if not (c.Name.Contains ".") && isName c.Name then yield $"capture {c.Name} of {f.SourceName}", d.Name <> c.Name
        for t in m.Types do
            match t with
            | IRTDIndexType (name, _) | IRTDEnumIdx (name, _, _) when EN.isEmitterName (fun w -> w.StartsWith "__") name ->
                yield $"index type {name}", CodeGenState.indexTypeCppName name <> name
            | _ -> () ]

/// (root-relative path, full path, source) of every single-file corpus program
/// and every .blade under examples/, bench/ and stdlib/ (the emission gates'
/// lists), each lowered as `blade emit` lowers it: from its own path.
let private corpusPrograms (root: string) : (string * string * string) list =
    let rel (p: string) = Path.GetRelativePath(root, p).Replace('\\', '/')
    let files (dir: string) (opt: SearchOption) =
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.blade", opt) |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofArray
        else []
    [ for cat in Blade.Tests.Corpus.singleFileCategories () do
        for f in files (Path.Combine(root, "tests", "corpus", cat)) SearchOption.TopDirectoryOnly do yield rel f, f, File.ReadAllText f
      for top in [ "examples"; "bench"; "stdlib" ] do
        for f in files (Path.Combine(root, top)) SearchOption.AllDirectories do yield rel f, f, File.ReadAllText f ]
type private Lowered = NotLowered | Hits of (string * bool) list

// ---------------------------------------------------------------------------
// The block.
// ---------------------------------------------------------------------------

let private runBlock (withCorpus: bool) : BlockResult =
    let blockName = if withCorpus then "Emitter Names" else "Emitter Names Sync"
    printHeader "Emitter names: the generator sources against src/CodeGenEmitterNames.fs (lane x4)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let mutable names = []
    let record (ok: bool) (name: string) (detail: string) =
        if ok then passed <- passed + 1; resultLine Pass name detail
        else failed <- failed + 1; names <- names @ [ name ]; resultLine Fail name detail
    match DocTests.tryDocRoot () with
    | None ->
        skipped <- skipped + 1
        resultLine Skip "the generator sources" "no checkout root (docs/formalism.md + CLAUDE.md) above the working directory or the binary"
    | Some root ->
        let tokens = extract root
        let sync = syncFailures tokens
        let fixedCount = tokens |> List.filter (fun t -> t.Holes.IsEmpty) |> List.map _.Text |> List.distinct |> List.length
        let familyCount = tokens |> List.filter (fun t -> not t.Holes.IsEmpty) |> List.map _.Text |> List.distinct |> List.length
        record sync.IsEmpty "every name the generators write is a row of src/CodeGenEmitterNames.fs, and every row is written"
            (if sync.IsEmpty then $"{(sourceFiles root).Length} files, {fixedCount} fixed spellings, {familyCount} family shapes"
             else String.concat "; " sync)
        let pf = predicateFailures ()
        record pf.IsEmpty "the predicate on spellings that must and must not be emitter names"
            (if pf.IsEmpty then "" else String.concat "; " pf)
        if withCorpus then
            printHeader "Emitter names, corpus mode: every spelling the name table judges, in every corpus program and every .blade under examples/, bench/, stdlib/"
            let programs = corpusPrograms root |> Array.ofList
            let outcomes = Array.create programs.Length NotLowered
            let lowerOne (i: int) =
                let _, full, src = programs[i]
                outcomes[i] <-
                    Blade.Runtime.runOnLargeStack (fun () ->
                        try
                            match fst (Lowering.lowerFileDiag full src) with
                            | Ok (ir, _) -> Hits (programHits ir)
                            | Error _ -> NotLowered
                        with _ -> NotLowered)
            // The corpus programs in parallel, from the root (as the harness runs
            // them); the rest one by one from their own directory, where a
            // provider store path resolves (as `blade emit` runs them).
            let isCorpus (i: int) = (let p, _, _ = programs[i] in p.StartsWith "tests/")
            let options = Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = 4)
            Threading.Tasks.Parallel.For(0, programs.Length, options, fun i -> if isCorpus i then lowerOne i) |> ignore
            let cwd = Directory.GetCurrentDirectory()
            try
                for i in 0 .. programs.Length - 1 do
                    if not (isCorpus i) then
                        let _, full, _ = programs[i]
                        Directory.SetCurrentDirectory(Path.GetDirectoryName full)
                        lowerOne i
            finally Directory.SetCurrentDirectory cwd
            let mutable lowered = 0
            let mutable hitCount = 0
            let found = Collections.Generic.List<string>()
            let unmoved = Collections.Generic.List<string>()
            for i in 0 .. programs.Length - 1 do
                match outcomes[i] with
                | NotLowered -> ()
                | Hits hs ->
                    lowered <- lowered + 1
                    let path, _, _ = programs[i]
                    if not hs.IsEmpty then
                        found.Add path
                        hitCount <- hitCount + hs.Length
                        // one line per program: the renames-only emission gate reads it
                        printfn "    hit: %s: %s" path (hs |> List.map fst |> List.distinct |> String.concat ", ")
                        for (h, isMoved) in hs do
                            if not isMoved then unmoved.Add $"{path}: {h}"
            record (lowered > 0 && unmoved.Count = 0)
                "every spelling on an OPEN emitter name is moved by the name table"
                ($"{lowered} of {programs.Length} programs lowered, {found.Count} spell an OPEN emitter name, {hitCount} spellings, {hitCount - unmoved.Count} moved, {unmoved.Count} unmoved"
                 + (if unmoved.Count = 0 then "" else "; UNMOVED: " + String.concat "; " unmoved))
            // Printing and `--print` keep the USER's spelling of a moved binding.
            let witness =
                "let __fk: Float64 = 10.0\nlet a: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n"
                + "let r = method_for(a) <@> lambda(v) -> v + __fk |> compute\n"
            let run =
                try
                    let dir = Path.Combine(Path.GetTempPath(), $"blade_emitter_names_{Diagnostics.Process.GetCurrentProcess().Id}")
                    Directory.CreateDirectory dir |> ignore
                    let file = Path.Combine(dir, "witness.blade")
                    File.WriteAllText(file, witness)
                    let psi = Diagnostics.ProcessStartInfo(Environment.ProcessPath, $"run --print-form test --print __fk,r \"{file}\"")
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    psi.UseShellExecute <- false
                    psi.WorkingDirectory <- dir
                    use p = Diagnostics.Process.Start psi
                    let err = p.StandardError.ReadToEndAsync()
                    let out = p.StandardOutput.ReadToEnd()
                    p.WaitForExit()
                    (try Directory.Delete(dir, true) with _ -> ())
                    if p.ExitCode = 0 then Ok (0, out) else Ok (p.ExitCode, out + err.Result)
                with ex -> Error ex.Message
            match run with
            | Ok (0, out) ->
                let ok = out.Contains "__fk = 10.0" && out.Contains "r = [11.0, 12.0, 13.0]"
                record ok "a moved binding prints, and is selected by --print, under the user's spelling"
                    (if ok then "" else "printed: " + out.Replace("\n", " | "))
            | Ok (code, out) -> record false "a moved binding prints, and is selected by --print, under the user's spelling" $"exit {code}: {out}"
            | Error why ->
                skipped <- skipped + 1
                resultLine Skip "a moved binding prints, and is selected by --print, under the user's spelling" $"not run: {why}"
    printFooter blockName [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = names }

/// `blade test emitter-names`: the sync checks and the corpus mode.
let runEmitterNameTests () : BlockResult = runBlock true

/// The default suite's part: the sync checks only (under two seconds).
let runEmitterNameSyncTests () : BlockResult = runBlock false
