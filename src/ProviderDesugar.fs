/// ProviderDesugar -- the icechunk `checkout` desugar (raw AST -> raw AST).
///
/// Icechunk is the first provider whose module-creating verb has a DERIVED
/// binding as its receiver rather than the imported alias:
///
///     import icechunk as ic
///     let repo = ic.load("data/weather.icechunk")   // repo handle
///     let ck1  = repo.checkout("main")              // bare
///     let ck2  = repo.checkout("v1.0", ic.tag)      // ref-unit marker
///
/// Teaching that shape to every phase individually would plant provider-
/// specific knowledge in four base-compiler passes (typecheck's load
/// recognition, Lowering's `tryInvokeProvider`, StaticEval's raw-AST
/// `providerRoots` scan, and two Ide walkers), because the binding -> path
/// association is carried by three independently built maps. Instead this
/// single pass rewrites checkout into the LOAD SHAPE all three already
/// understand, keyed on the canonical key of
/// docs/plans/plan-icechunk-provider.md section 3.1:
///
///     let ck2 = repo.checkout("v1.0", ic.tag)
///         =>  let ck2 = ic.load("data/weather.icechunk@tag:v1.0")
///
/// `<kind>` is read off the marker (`branch` / `tag` / `snapshot`), or `?`
/// for the bare form, which the provider resolves by cross-namespace
/// uniqueness at `LoadAsModule` time. After the rewrite every downstream
/// phase sees a shape it already handles -- ZERO new arms anywhere.
///
/// Three properties this pass is responsible for:
///
///   * SPAN FIDELITY. The synthesized `ExprApp`/`ExprField`/`ExprVar`/
///     `ExprLit` nodes wear the ORIGINAL checkout call's spans, so every
///     later diagnostic underlines the text the user actually wrote and not
///     a synthesized key that appears in no source file.
///   * IDEMPOTENCE. A rewritten binding loads a path containing `@`, which
///     the repo-binding scan declines, so a second application is a no-op.
///     (It is applied at more than one funnel -- see the wiring note below.)
///   * A NO-OP FAST PATH. A module with no `import icechunk` is returned
///     unchanged and reference-equal, so the pass costs one decl scan on
///     every program that does not use the provider.
///
/// Deliberately CONCRETE to icechunk, per the plan: no generic
/// "derived-binding verb" registry slot until a second provider wants one.
/// It therefore depends on nothing but `Blade.Ast` and `Blade.Diagnostics`
/// and can compile very early -- which it must, since its consumers are
/// TypeCheck.fs, Lowering.fs and Ide.fs.
///
/// WIRING (all three must stay wired; see the plan's section 13 risk note --
/// a consumer that grabs the AST upstream of this pass sees undesugared
/// checkouts and fails obliquely, and StaticEval's miss is SILENT):
///
///   * `Blade.TypeCheck.typeCheck` runs `expand` FIRST, ahead of Unfold --
///     which is itself the first `resolveStatics` consumer -- so typecheck,
///     every domain elaborator's own static fold, and `checkModule`'s
///     `providerRoots` scan all see the load shape.
///   * `Blade.Lowering.lowerTypedProgram` runs `desugarOrIdentity` over its
///     `rawProgram`, because callers hand lowering the program they parsed,
///     not the one typeCheck rewrote internally; Phase 0's `resolveStatics`
///     reads that raw decl list.
///   * `Blade.Ide.ideCheckSourceWith` runs it on the parsed entry buffer,
///     which is what the editor's provider/provenance walkers read.
module Blade.ProviderDesugar

open Blade.Ast

/// The one provider module name this pass knows. Matched as a literal rather
/// than through `ProviderRegistry`: the pass has to run before typecheck
/// installs the registry, and its knowledge is icechunk-specific anyway.
let private icechunkModule = "icechunk"

/// The ref-namespace markers. `ic.branch` / `ic.tag` / `ic.snapshot` are
/// unit-carrying marker constants at the surface (plan section 3), but the
/// desugar reads them SYNTACTICALLY -- it runs before there is a unit
/// environment to consult, and the field name is the whole content.
let private refMarkers = set [ "branch"; "tag"; "snapshot" ]

/// The kind slot of a bare (unmarked) checkout: the provider resolves it by
/// demanding uniqueness across branches, tags and plausible snapshot ids.
let private bareKind = "?"

/// The canonical key: `"<repoPath>@<kind>:<name>"`. The one string that
/// enters every path-keyed carrier (`ProviderPaths`, `ProviderRoots`, the
/// fold/axis caches, `ProviderReadSpec.FilePath`); `IcechunkProvider` parses
/// it back internally. Public so the provider and its tests spell it once.
let canonicalKey (repoPath: string) (kind: string) (refName: string) : string =
    repoPath + "@" + kind + ":" + refName

/// Does this path already carry a ref suffix? A load whose path names a
/// checkout is not a repo handle, so `ck.checkout(...)` on it is left alone
/// (and this is what makes the pass idempotent).
let private isCheckoutKey (path: string) = path.Contains("@")

// Diagnostics

/// Wrong-shaped `checkout` on a receiver we DID recognize as a repo handle.
/// Reported as BL3007 ("invalid builtin argument"), whose registry entry
/// already covers "the provider read/write forms" -- a new code would need
/// its protocol/surface.json + protocol/data/diagnostics.json companions and
/// buys nothing here.
///
/// A checkout on a receiver we did NOT record is deliberately NOT an error:
/// it is left unrewritten and fails as the ordinary missing-member error it
/// is, which is the right message for `someTuple.checkout(...)`.
let private checkoutShapeError (span: Span) (repo: string) (alias: string)
                               (args: Expr list) : Blade.Diagnostics.Diagnostic =
    let isStringLit (e: Expr) =
        match e.Kind with
        | ExprKind.ExprLit (LitString _) -> true
        | _ -> false
    let detail =
        match args with
        | [] -> "no arguments"
        | [ _ ] -> "one argument that is not a string literal"
        | [ a; _ ] when not (isStringLit a) -> "a first argument that is not a string literal"
        | [ _; _ ] ->
            $"a second argument that is not one of the ref markers `{alias}.branch`, `{alias}.tag`, `{alias}.snapshot`"
        | many -> $"{many.Length} arguments"
    let msg =
        $"`{repo}.checkout(...)` got {detail}. A checkout takes a string LITERAL ref name, "
        + $"optionally followed by one ref marker: `{repo}.checkout(\"main\")`, "
        + $"`{repo}.checkout(\"v1.0\", {alias}.tag)`, `{repo}.checkout(\"main\", {alias}.branch)` "
        + $"or `{repo}.checkout(\"<id>\", {alias}.snapshot)`. The store's metadata is resolved at "
        + "compile time, so a computed ref name cannot be served."
    Blade.Diagnostics.mkError "BL3007" (Blade.Diagnostics.Codes.phaseOfCode "BL3007") span msg

// Node construction

/// Build `alias.load("<key>")` wearing the checkout call's own spans:
/// the whole application keeps the checkout call's span, `alias.load` keeps
/// `repo.checkout`'s, the alias keeps `repo`'s, and the key literal keeps the
/// ref-name literal's -- so a bad-ref diagnostic underlines the string the
/// user wrote rather than a key that exists in no source file.
let private loadNode (appSpan: Span) (fieldSpan: Span) (recvSpan: Span) (litSpan: Span)
                     (alias: string) (key: string) : Expr =
    mkExpr appSpan
        (ExprKind.ExprApp (
            mkExpr fieldSpan
                (ExprKind.ExprField (mkExpr recvSpan (ExprKind.ExprVar alias), "load")),
            [ mkExpr litSpan (ExprKind.ExprLit (LitString key)) ]))

// The module pass

/// Every name a pattern binds. Local (and tiny) on purpose: the shared
/// helpers live in StaticEval / TypeCheckSupport, both far later in compile
/// order than this pass may sit.
let rec private patternBoundNames (p: Pattern) : string list =
    match p.Kind with
    | PatternKind.PatVar n -> [ n ]
    | PatternKind.PatTuple ps -> ps |> List.collect patternBoundNames
    | PatternKind.PatCons (h, t) -> patternBoundNames h @ patternBoundNames t
    | PatternKind.PatStruct (_, flds) -> flds |> List.collect (snd >> patternBoundNames)
    | PatternKind.PatVariant (_, Some inner)
    | PatternKind.PatGuarded (inner, _)
    | PatternKind.PatTyped (inner, _) -> patternBoundNames inner
    | _ -> []

/// `import icechunk` / `import icechunk as ic` aliases declared in a module.
/// The parser leaves `ModuleDecl.Imports` empty and emits every import as a
/// `DeclImport` (Parser.fs, `Imports = []`), so scanning the decls is
/// complete -- same idiom as StaticEval's `providerAliases`.
let private icechunkAliases (decls: Located<Decl> list) : Set<string> =
    decls |> List.fold (fun acc d ->
        match d.Value with
        | DeclImport ([ pname ], ImportQualified aliasOpt) when pname = icechunkModule ->
            Set.add (aliasOpt |> Option.defaultValue pname) acc
        | _ -> acc) Set.empty

let private desugarModule (diags: ResizeArray<Blade.Diagnostics.Diagnostic>)
                          (m: ModuleDecl) : ModuleDecl =
    let aliases = icechunkAliases m.Decls
    // Fast path: no icechunk import, nothing to look at -- return the SAME
    // object so `expand` can hand a whole untouched program straight back.
    if Set.isEmpty aliases then m else
    // binding name -> (icechunk alias, repo path). Built LEFT TO RIGHT as the
    // walk proceeds, so a checkout only ever resolves against a repo bound
    // ABOVE it -- which is also the only ordering Blade's scoping admits.
    // Reference cells rather than `let mutable`: both are read and written
    // from the local functions below, which are closures.
    let repos : Map<string, string * string> ref = ref Map.empty
    let changed = ref false

    /// The rewrite itself, at a top-level binding's right-hand side. That is
    /// the ONLY position worth rewriting: all three binding -> path carriers
    /// (TypeCheckInfer's load-recognition arm in `checkDecl`, StaticEval's
    /// `providerRoots`, Ide's `collectProviderStores`) match a module-level
    /// binding's VALUE, so a load synthesized anywhere else would be
    /// recognized by nobody.
    let rewriteValue (b: Binding) : Binding =
        match b.Value.Kind with
        | ExprKind.ExprApp (({ Kind = ExprKind.ExprField (({ Kind = ExprKind.ExprVar recv } as recvE), "checkout") } as headE), args)
            when Map.containsKey recv repos.Value ->
            let (alias, repoPath) = repos.Value.[recv]
            let rewritten (nameE: Expr) (kind: string) (refName: string) =
                changed.Value <- true
                { b with Value =
                            loadNode b.Value.Span headE.Span recvE.Span nameE.Span
                                     alias (canonicalKey repoPath kind refName) }
            match args with
            // `repo.checkout("main")` -- bare, resolved across namespaces.
            | [ ({ Kind = ExprKind.ExprLit (LitString refName) } as nameE) ] ->
                rewritten nameE bareKind refName
            // `repo.checkout("v1.0", ic.tag)` -- the marker names the namespace.
            | [ ({ Kind = ExprKind.ExprLit (LitString refName) } as nameE)
                { Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar markerAlias }, marker) } ]
                when markerAlias = alias && Set.contains marker refMarkers ->
                rewritten nameE marker refName
            // Anything else on a RECOGNIZED repo handle is loud: silently
            // leaving it would surface as a baffling missing-member error
            // about a `checkout` field that the provider never had.
            | _ ->
                diags.Add(checkoutShapeError b.Value.Span recv alias args)
                b
        | _ -> b

    /// Record (or drop) a repo handle for the names this binding binds.
    /// Dropping matters: a later `let repo = 5` must stop `repo.checkout(...)`
    /// from rewriting, and so must a destructuring that rebinds the name.
    let recordRepo (b: Binding) =
        for n in patternBoundNames b.Pattern do
            repos.Value <- Map.remove n repos.Value
        match b.Pattern.Kind, b.Value.Kind with
        | PatternKind.PatVar root,
          ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") },
                            [ { Kind = ExprKind.ExprLit (LitString path) } ])
            when Set.contains alias aliases && not (isCheckoutKey path) ->
            repos.Value <- Map.add root (alias, path) repos.Value
        | _ -> ()

    let decls' =
        m.Decls |> List.map (fun (d: Located<Decl>) ->
            match d.Value with
            | DeclLet b ->
                let b' = rewriteValue b
                recordRepo b'
                if LanguagePrimitives.PhysicalEquality b' b then d
                else { d with Value = DeclLet b' }
            | DeclStatic b ->
                let b' = rewriteValue b
                recordRepo b'
                if LanguagePrimitives.PhysicalEquality b' b then d
                else { d with Value = DeclStatic b' }
            | DeclFunction fd ->
                // A function shadows the value namespace for its name.
                repos.Value <- Map.remove fd.Name repos.Value
                d
            | _ -> d)
    if changed.Value then { m with Decls = decls' } else m

// Entry points

/// The pass entry point -- same shape as the other AST->AST expansions
/// (`Unfold.expand`, `Grad.expand`). Programs with no icechunk import come
/// back reference-equal. Every wrong-shaped checkout on a recognized repo is
/// collected, so one run reports them all.
let expand (program: Program) : Result<Program, Blade.Diagnostics.Diagnostic list> =
    let diags = ResizeArray<Blade.Diagnostics.Diagnostic>()
    let changed = ref false
    let modules' =
        program.Modules |> List.map (fun m ->
            let m' = desugarModule diags m
            if not (LanguagePrimitives.PhysicalEquality m' m) then changed.Value <- true
            m')
    if diags.Count > 0 then Error (List.ofSeq diags)
    elif changed.Value then Ok { program with Modules = modules' }
    else Ok program

/// Total variant for the funnels that run AFTER typecheck has already
/// accepted the program (lowering's raw decl list, the IDE's payload
/// collectors). A refusal there is not actionable -- `expand` already
/// reported it from `typeCheck`, and re-reporting from lowering would
/// double every diagnostic -- so a failed desugar simply leaves the program
/// alone and the shape fails downstream exactly as it did before.
let desugarOrIdentity (program: Program) : Program =
    match expand program with
    | Ok p -> p
    | Error _ -> program
